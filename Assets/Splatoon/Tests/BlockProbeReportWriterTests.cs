#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class BlockProbeReportWriterTests
    {
        [Serializable] sealed class TerminalRead { public bool complete = false; }
        [Serializable] sealed class ReceiptRead
        {
            public bool terminalPayloadMatches = false;
            public string payloadSha256 = "", session = "";
            public BlockProbeReportWriter.Statistics writer = default;
        }
        string _directory;
        [SetUp] public void Setup()
        { _directory = Path.Combine(Path.GetTempPath(), "Splatoon-report-writer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory); }
        [TearDown] public void Cleanup()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        [Test] public void SlowDiskKeepsOneLatestProgressAndPublishesTerminalAfterTheInFlightWrite()
        {
            string path = Path.Combine(_directory, "report.json");
            File.WriteAllText(path, "before");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var writer = new BlockProbeReportWriter(path, (destination, json) => {
                if (json == "first")
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("Test disk gate was not released.");
                }
                BlockProbeReportWriter.WriteAtomic(destination, json);
            });
            try
            {
                writer.SubmitProgress("first");
                Assert.That(entered.Wait(5000), Is.True);
                writer.SubmitProgress("superseded"); writer.SubmitProgress("latest");
                writer.SubmitTerminal("terminal");
                writer.SubmitProgress("late progress must not replace terminal");
                writer.SubmitTerminal("duplicate must not replace accepted terminal");
                Assert.That(writer.PollTerminal(), Is.False);
                Assert.That(File.ReadAllText(path), Is.EqualTo("before"));
                release.Set();
                Assert.That(writer.WaitForIdle(5000), Is.True);
                Assert.That(writer.PollTerminal(), Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("terminal"));
                var stats = writer.ReadStatistics();
                Assert.That(stats.submittedProgress, Is.EqualTo(3));
                Assert.That(stats.coalescedProgress, Is.EqualTo(2));
                Assert.That(stats.completedWrites, Is.EqualTo(2));
                Assert.That(stats.failedWrites, Is.Zero);
                Assert.That(File.Exists(path + ".writing"), Is.False);
            }
            finally { release.Set(); }
        }

        [Test] public void FailedTerminalPublicationRetriesTheExactAcceptedPayloadAfterTheFilesystemRecovers()
        {
            string blocked = Path.Combine(_directory, "blocked");
            File.WriteAllText(blocked, "a file prevents creating the parent directory");
            string path = Path.Combine(blocked, "report.json");
            using var writer = new BlockProbeReportWriter(path);
            writer.SubmitTerminal("accepted terminal");
            Assert.That(writer.WaitForIdle(5000), Is.True);
            Assert.That(writer.ReadStatistics().failedWrites, Is.EqualTo(1));
            Assert.That(writer.ReadStatistics().terminalPublished, Is.False);
            Assert.That(writer.ReadStatistics().lastError, Is.Not.Empty);
            File.Delete(blocked); Directory.CreateDirectory(blocked);
            writer.SubmitTerminal("retry caller must not change the evidence");
            writer.PollTerminal();
            Assert.That(writer.WaitForIdle(5000), Is.True);
            Assert.That(writer.PollTerminal(), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo("accepted terminal"));
            Assert.That(writer.ReadStatistics().completedWrites, Is.EqualTo(1));
        }

        [Test] public void RealAtomicReplacementPreservesUtf8AndDisposalDrainsAcceptedTerminal()
        {
            string path = Path.Combine(_directory, "report.json");
            File.WriteAllText(path, "old complete report");
            string terminal = "{\"complete\":true,\"message\":\"积木终态\",\"data\":\"" + new string('x', 131072) + "\"}";
            var writer = new BlockProbeReportWriter(path);
            writer.SubmitTerminal(terminal);
            writer.Dispose();
            Assert.That(writer.ReadStatistics().terminalPublished, Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo(terminal));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.That(bytes[0], Is.EqualTo((byte)'{'), "The report retains UTF-8 without a BOM.");
            Assert.That(File.Exists(path + ".writing"), Is.False);
            Assert.Throws<ObjectDisposedException>(() => writer.SubmitProgress("late"));
        }

        [Test] public void TeardownRetriesPendingTerminalAfterAnInFlightProgressWriteFails()
        {
            string path = Path.Combine(_directory, "report.json");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var writer = new BlockProbeReportWriter(path, (destination, json) => {
                if (json == "progress")
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("Test disk gate was not released.");
                    throw new IOException("Injected failure of the already in-flight progress publication.");
                }
                BlockProbeReportWriter.WriteAtomic(destination, json);
            });
            try
            {
                writer.SubmitProgress("progress"); Assert.That(entered.Wait(5000), Is.True);
                writer.SubmitTerminal("terminal survives progress failure"); release.Set();
                Assert.That(writer.Close(5000), Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("terminal survives progress failure"));
                Assert.That(writer.ReadStatistics().failedWrites, Is.EqualTo(1));
                Assert.That(writer.ReadStatistics().drainCompleted, Is.True);
            }
            finally { release.Set(); }
        }

        [Test] public void PermanentDiskFailureReturnsAnExplicitUnpublishedTeardownResult()
        {
            using var writer = new BlockProbeReportWriter(Path.Combine(_directory, "report.json"),
                (_, _) => throw new IOException("The disk remains unavailable."));
            writer.SubmitTerminal("unpublished evidence");
            Assert.That(writer.Close(150), Is.False);
            var stats = writer.ReadStatistics();
            Assert.That(stats.closed, Is.True); Assert.That(stats.drainCompleted, Is.False);
            Assert.That(stats.terminalPublished, Is.False); Assert.That(stats.failedWrites, Is.GreaterThan(0));
            Assert.That(writer.Close(5000), Is.False, "A finished failed close must remain explicit.");
        }

        [Test] public void CloseTimeoutDoesNotClaimToDrainABlockedNativeWrite()
        {
            string path = Path.Combine(_directory, "report.json");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var writer = new BlockProbeReportWriter(path, (destination, json) => {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Test disk gate was not released.");
                BlockProbeReportWriter.WriteAtomic(destination, json);
            });
            try
            {
                writer.SubmitTerminal("blocked terminal"); Assert.That(entered.Wait(5000), Is.True);
                Assert.That(writer.Close(25), Is.False);
                Assert.That(writer.ReadStatistics().drainCompleted, Is.False);
                Assert.That(writer.ReadStatistics().terminalPublished, Is.False);
                release.Set(); Assert.That(writer.WaitForIdle(5000), Is.True);
                Assert.That(writer.PollTerminal(), Is.True, "An OS write can finish after the bounded teardown has timed out.");
                Assert.That(writer.Close(5000), Is.False, "The timed-out close result must not be rewritten as a successful drain.");
                Assert.That(writer.ReadStatistics().diskElapsedMilliseconds, Is.GreaterThan(0));
            }
            finally { release.Set(); }
        }

        [Test] public void ProbeWriteCannotAcknowledgeQuitUntilItsExactTerminalFileAndReceiptArePublished()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string path = Path.Combine(_directory, "probe.json");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var writer = new BlockProbeReportWriter(path, (destination, json) => {
                if (json == "old progress")
                { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Test disk gate was not released."); }
                BlockProbeReportWriter.WriteAtomic(destination, json);
            });
            var root = new UnityEngine.GameObject("Probe terminal publication contract");
            root.SetActive(false);
            try
            {
                var probe = root.AddComponent<BlockDynamicAcceptanceProbe>();
                var type = typeof(BlockDynamicAcceptanceProbe);
                var reportType = type.GetNestedType("Report", BindingFlags.NonPublic);
                var report = Activator.CreateInstance(reportType, true);
                reportType.GetField("session").SetValue(report, "writer-contract-session");
                reportType.GetField("label").SetValue(report, "writer-contract-host");
                reportType.GetField("stage").SetValue(report, "quit");
                reportType.GetField("complete").SetValue(report, true);
                type.GetField("_report", flags).SetValue(probe, report);
                type.GetField("_output", flags).SetValue(probe, path);
                type.GetField("_finished", flags).SetValue(probe, true);
                type.GetField("_reportWriter", flags).SetValue(probe, writer);
                var write = type.GetMethod("Write", flags);
                writer.SubmitProgress("old progress"); Assert.That(entered.Wait(5000), Is.True);
                Assert.That(write.Invoke(probe, null), Is.EqualTo(false));
                Assert.That(File.Exists(path + ".writer.json"), Is.False);
                release.Set(); Assert.That(writer.WaitForIdle(5000), Is.True);
                Assert.That(write.Invoke(probe, null), Is.EqualTo(true));
                var json = UnityEngine.JsonUtility.FromJson<TerminalRead>(File.ReadAllText(path));
                var receipt = UnityEngine.JsonUtility.FromJson<ReceiptRead>(File.ReadAllText(path + ".writer.json"));
                Assert.That(json.complete, Is.True);
                Assert.That(receipt.terminalPayloadMatches, Is.True);
                Assert.That(receipt.writer.terminalPublished, Is.True);
                using var hash = System.Security.Cryptography.SHA256.Create();
                string sha = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
                Assert.That(receipt.payloadSha256, Is.EqualTo(sha));
                Assert.That(receipt.session, Is.EqualTo("writer-contract-session"));
            }
            finally { release.Set(); UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
#endif
