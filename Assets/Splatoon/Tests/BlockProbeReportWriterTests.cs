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
        [Test] public void ARealWindowsDeleteShareLockCanRecoverWithinTheSameWorkerPublication()
        {
            if (System.Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Ignore("This real delete-sharing fixture requires Windows filesystem semantics.");
            string path = Path.Combine(_directory, "recovering.json");
            const string old = "{\"sequence\":0,\"message\":\"完整旧报告\"}";
            string next = "{\"sequence\":1,\"message\":\"完整新报告\",\"data\":\"" + new string('z', 65536) + "\"}";
            File.WriteAllText(path, old, new System.Text.UTF8Encoding(false));
            using var writer = new BlockProbeReportWriter(path);
            long sequence;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Prove that this actual filesystem/Mono lock blocks the unchanged single-attempt API.
                Assert.Throws<IOException>(() => BlockProbeReportWriter.WriteAtomic(path, next));
                Assert.That(File.ReadAllText(path), Is.EqualTo(old));
                sequence = writer.SubmitProgress(next);
                Assert.That(SpinWait.SpinUntil(() => writer.ReadStatistics().atomicAttemptFailures > 0, 5000), Is.True);
                var blocked = writer.ReadStatistics();
                Assert.That(blocked.lastAtomicFailure.stage, Is.EqualTo("replace"));
                Assert.That(blocked.completedWrites, Is.Zero); Assert.That(blocked.lastPublishedProgressSequence, Is.Zero);
                Assert.That(blocked.atomicWriteAttempts, Is.LessThan(4), "Release must occur inside the existing bounded attempt window.");
                Assert.That(writer.WaitForIdle(0), Is.False);
                Assert.That(File.ReadAllText(path), Is.EqualTo(old), "No retry can expose a partial or deleted old payload.");
            }
            Assert.That(writer.WaitForIdle(5000), Is.True);
            var published = writer.ReadStatistics();
            Assert.That(published.submittedProgress, Is.EqualTo(1));
            Assert.That(published.completedWrites, Is.EqualTo(1)); Assert.That(published.failedWrites, Is.Zero);
            Assert.That(published.lastPublishedProgressSequence, Is.EqualTo(sequence));
            Assert.That(published.atomicWriteAttempts, Is.InRange(2L, 4L));
            Assert.That(published.atomicAttemptFailures, Is.EqualTo(published.atomicWriteAttempts - 1));
            Assert.That(published.atomicWriteRetries, Is.EqualTo(published.atomicAttemptFailures));
            Assert.That(published.recoveredAtomicWrites, Is.EqualTo(1));
            Assert.That(published.lastAtomicFailure.exception, Is.Not.Empty, "Successful retry retains the actual IO exception.");
            Assert.That(File.ReadAllText(path), Is.EqualTo(next));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new System.Text.UTF8Encoding(false).GetBytes(next)));
            Assert.That(File.Exists(path + ".writing"), Is.False);
        }

        [Test] public void DefaultAtomicWorkerExhaustsItsBoundWithoutAcknowledgingAndRecoversOnANewSubmission()
        {
            if (System.Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Ignore("This real delete-sharing fixture requires Windows filesystem semantics.");
            string path = Path.Combine(_directory, "locked.json");
            File.WriteAllText(path, "previous complete publication");
            using var writer = new BlockProbeReportWriter(path);
            BlockProbeReportWriter.Statistics failed;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long sequence = writer.SubmitProgress("new complete publication");
                Assert.That(writer.WaitForIdle(5000), Is.True, "Idle must remain distinct from a successful publication.");
                failed = writer.ReadStatistics();
                TestContext.WriteLine("writer-exhausted-statistics=" + UnityEngine.JsonUtility.ToJson(failed, false));
                Assert.That(sequence, Is.EqualTo(1)); Assert.That(failed.completedWrites, Is.Zero);
                Assert.That(failed.lastPublishedProgressSequence, Is.Zero); Assert.That(failed.failedWrites, Is.EqualTo(1));
                Assert.That(failed.atomicWriteAttempts, Is.EqualTo(4)); Assert.That(failed.atomicAttemptFailures, Is.EqualTo(4));
                Assert.That(failed.atomicWriteRetries, Is.EqualTo(3)); Assert.That(failed.recoveredAtomicWrites, Is.Zero);
                Assert.That(failed.lastAtomicAttemptCount, Is.EqualTo(4));
                Assert.That(failed.lastAtomicFailure.stage, Is.EqualTo("replace"));
                Assert.That(failed.lastAtomicFailure.attempt, Is.EqualTo(4));
                Assert.That(failed.lastAtomicFailure.hresult, Is.Not.Zero);
                Assert.That(failed.lastAtomicFailureTrace, Does.Contain("attempt=1 stage=replace"));
                Assert.That(failed.lastAtomicFailureTrace, Does.Contain("attempt=4 stage=replace"));
                Assert.That(failed.lastAtomicFailure.exceptionType, Does.Contain("IOException"));
                Assert.That(failed.lastAtomicFailure.exception, Does.Contain("Replace"));
                Assert.That(failed.lastAtomicFailure.path, Is.EqualTo(path));
                Assert.That(failed.lastAtomicFailure.temporaryPath, Is.EqualTo(path + ".writing"));
                Assert.That(failed.lastAtomicFailure.targetExistsObserved, Is.True);
                Assert.That(failed.lastAtomicFailure.temporaryExistsObserved, Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("previous complete publication"));
            }
            long fresh = writer.SubmitProgress("recovered complete publication");
            Assert.That(writer.WaitForIdle(5000), Is.True);
            var recovered = writer.ReadStatistics();
            TestContext.WriteLine("writer-recovered-statistics=" + UnityEngine.JsonUtility.ToJson(recovered, false));
            Assert.That(fresh, Is.EqualTo(2)); Assert.That(recovered.lastPublishedProgressSequence, Is.EqualTo(fresh));
            Assert.That(recovered.completedWrites, Is.EqualTo(1)); Assert.That(recovered.failedWrites, Is.EqualTo(1));
            Assert.That(recovered.submittedProgress, Is.EqualTo(2)); Assert.That(recovered.coalescedProgress, Is.Zero);
            long attempts = recovered.atomicWriteAttempts - failed.atomicWriteAttempts;
            long failures = recovered.atomicAttemptFailures - failed.atomicAttemptFailures;
            long retries = recovered.atomicWriteRetries - failed.atomicWriteRetries;
            Assert.That(attempts, Is.InRange(1L, 4L), "Every new submission retains the same four-attempt bound.");
            Assert.That(failures, Is.EqualTo(attempts - 1), "One native attempt succeeds; every preceding attempt must retain a real failure.");
            Assert.That(retries, Is.EqualTo(failures));
            Assert.That(recovered.recoveredAtomicWrites - failed.recoveredAtomicWrites, Is.EqualTo(attempts > 1 ? 1L : 0L));
            Assert.That(recovered.lastAtomicAttemptCount, Is.EqualTo(attempts));
            if (attempts == 1)
            {
                Assert.That(recovered.lastAtomicFailure, Is.EqualTo(failed.lastAtomicFailure), "Success without a new IO failure must retain every old failure field.");
                Assert.That(recovered.lastAtomicFailureTrace, Is.EqualTo(failed.lastAtomicFailureTrace));
            }
            else
            {
                var latest = recovered.lastAtomicFailure;
                Assert.That(latest.attempt, Is.EqualTo(failures));
                Assert.That(latest.stage, Is.EqualTo("replace").Or.EqualTo("move"));
                Assert.That(latest.hresult, Is.Not.Zero); Assert.That(latest.hresultLow16, Is.EqualTo(latest.hresult & 65535));
                Assert.That(latest.exceptionType, Does.Contain("IOException")); Assert.That(latest.exception, Does.Contain("IOException"));
                Assert.That(latest.path, Is.EqualTo(path)); Assert.That(latest.temporaryPath, Is.EqualTo(path + ".writing"));
                string[] entries = recovered.lastAtomicFailureTrace.Split(new[] { "\nattempt=" }, StringSplitOptions.None);
                Assert.That(entries.Length, Is.EqualTo(failures), "Retain exactly this submission's real failed attempts, without an extra retry.");
                for (int i = 0; i < entries.Length; i++)
                {
                    string entry = i == 0 ? entries[i] : "attempt=" + entries[i];
                    Assert.That(entry, Does.Match("^attempt=" + (i + 1) + " stage=(replace|move) hresult=-?[0-9]+ low16=[0-9]+\n"));
                    Assert.That(entry, Does.Contain("[utf16-escaped-v1] ").And.Contain("IOException"));
                }
                string latestTrace = "attempt=" + latest.attempt + " stage=" + latest.stage + " hresult=" + latest.hresult +
                    " low16=" + latest.hresultLow16 + "\n" + latest.exception;
                Assert.That(recovered.lastAtomicFailureTrace, Does.EndWith(latestTrace), "The newest retained error must exactly match the final failed attempt.");
            }
            Assert.That(File.ReadAllText(path), Is.EqualTo("recovered complete publication"));
            Assert.That(File.Exists(path + ".writing"), Is.False);
        }

        [Test] public void AnInjectedIoFailureRetainsOneCallAndCannotEnterTheDefaultFilesystemRetry()
        {
            int calls = 0; IOException thrown = null;
            using var writer = new BlockProbeReportWriter(Path.Combine(_directory, "injected.json"), (_, _) =>
            { Interlocked.Increment(ref calls); thrown = new IOException("Explicit owner failure must remain observable.\r\n\t\0积木"); throw thrown; });
            writer.SubmitProgress("one retained owner call"); Assert.That(writer.WaitForIdle(5000), Is.True);
            var stats = writer.ReadStatistics();
            Assert.That(calls, Is.EqualTo(1)); Assert.That(stats.failedWrites, Is.EqualTo(1));
            Assert.That(stats.completedWrites, Is.Zero); Assert.That(stats.lastPublishedProgressSequence, Is.Zero);
            Assert.That(stats.atomicWriteAttempts, Is.Zero); Assert.That(stats.atomicAttemptFailures, Is.Zero);
            Assert.That(stats.atomicWriteRetries, Is.Zero); Assert.That(stats.recoveredAtomicWrites, Is.Zero);
            Assert.That(stats.lastError, Does.Contain("Explicit owner failure must remain observable."));
            string encoded = UnityEngine.JsonUtility.ToJson(stats, false);
            foreach (char character in encoded) Assert.That((int)character, Is.GreaterThanOrEqualTo(32),
                "Full exception controls must be escaped by the real report serializer.");
            var decoded = UnityEngine.JsonUtility.FromJson<BlockProbeReportWriter.Statistics>(encoded);
            Assert.That(decoded.lastError, Is.EqualTo(stats.lastError));
            Assert.That(DecodeEscapedException(decoded.lastError), Is.EqualTo(thrown.ToString()),
                "The complete original exception, including NUL and all following characters, must be recoverable.");
        }

        static string DecodeEscapedException(string encoded)
        {
            const string prefix = "[utf16-escaped-v1] ";
            Assert.That(encoded.StartsWith(prefix, StringComparison.Ordinal), Is.True,
                "Every encoded exception identifies its reversible representation.");
            var text = new System.Text.StringBuilder();
            for (int i = prefix.Length; i < encoded.Length; i++)
            {
                char character = encoded[i];
                if (character != '\\') { text.Append(character); continue; }
                Assert.That(++i, Is.LessThan(encoded.Length), "An escape cannot end at its introducer.");
                if (encoded[i] == '\\') { text.Append('\\'); continue; }
                Assert.That(encoded[i], Is.EqualTo('u'), "Only doubled backslash and four-hex code units are defined.");
                Assert.That(i + 4, Is.LessThan(encoded.Length), "A code-unit escape must contain all four digits.");
                text.Append((char)Convert.ToUInt16(encoded.Substring(i + 1, 4), 16));
                i += 4;
            }
            return text.ToString();
        }

        [Test] public void ExceptionDiagnosticsPreserveAllControlsLiteralEscapesAndSurrogatesAcrossJsonUtility()
        {
            var message = new System.Text.StringBuilder("all control code units:");
            for (int code = 0; code < 32; code++) message.Append((char)code);
            for (int code = 127; code <= 159; code++) message.Append((char)code);
            message.Append(" literal slash-u: \\u0000; quote: \"; slash: \\; ");
            message.Append((char)0xd800).Append('x').Append((char)0xdc00).Append("积木 after controls and isolated surrogates");
            IOException thrown = null;
            using var writer = new BlockProbeReportWriter(Path.Combine(_directory, "all-controls.json"), (_, _) =>
            { thrown = new IOException(message.ToString()); throw thrown; });
            writer.SubmitProgress("one controlled error");
            Assert.That(writer.WaitForIdle(5000), Is.True);
            var stats = writer.ReadStatistics();
            Assert.That(stats.failedWrites, Is.EqualTo(1)); Assert.That(stats.completedWrites, Is.Zero);
            Assert.That(DecodeEscapedException(stats.lastError), Is.EqualTo(thrown.ToString()),
                "Encoding must preserve the exact complete exception, including literal escapes and all text after NUL.");
            Assert.That(stats.lastError, Does.Contain("\\u0000"));
            Assert.That(stats.lastError, Does.Contain("\\\\u0000"), "A literal slash-u sequence must remain distinguishable from NUL.");
            string encoded = UnityEngine.JsonUtility.ToJson(stats, false);
            foreach (char character in encoded) Assert.That((int)character, Is.GreaterThanOrEqualTo(32));
            var decoded = UnityEngine.JsonUtility.FromJson<BlockProbeReportWriter.Statistics>(encoded);
            Assert.That(decoded.lastError, Is.EqualTo(stats.lastError));
            Assert.That(DecodeEscapedException(decoded.lastError), Is.EqualTo(thrown.ToString()));
            Assert.That(decoded.failedWrites, Is.EqualTo(stats.failedWrites));
            Assert.That(decoded.lastPublishedProgressSequence, Is.Zero);
        }

        [Serializable] sealed class FilePayload
        { public int schema = 4; public string label; public long[] raw = { long.MinValue, 0, long.MaxValue }; }

        [Test] public void ImmutableFileTerminalRetriesExactBytesAfterTheProducerReleasesItsLease()
        {
            string output = Path.Combine(_directory, "file-terminal.json");
            var snapshot = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "完整报告" });
            string source = snapshot.Path, expectedHash = snapshot.Sha256;
            int attempts = 0;
            using var writer = new BlockProbeReportWriter(output, copyFile: (destination, sealedSource) => {
                Assert.That(sealedSource, Is.EqualTo(source));
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("First file publication fails.");
                BlockProbeReportWriter.WriteAtomicFile(destination, sealedSource);
            });
            writer.SubmitTerminal(snapshot); snapshot.Dispose();
            Assert.That(writer.WaitForIdle(5000), Is.True);
            Assert.That(File.Exists(source), Is.True, "An unpublished terminal retains the exact sealed file for retry.");
            Assert.That(writer.ReadStatistics().failedWrites, Is.EqualTo(1));
            Assert.That(writer.PollTerminal(), Is.False);
            Assert.That(writer.WaitForIdle(5000), Is.True);
            Assert.That(writer.PollTerminal(), Is.True);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(output);
            Assert.That(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(), Is.EqualTo(expectedHash));
            Assert.That(File.Exists(source), Is.False, "Publication releases the writer's last file lease.");
            Assert.That(attempts, Is.EqualTo(2));
        }

        [Test] public void CoalescedFilePayloadsReleaseOnlyTheirOwnLeasesAndCannotReplaceTheTerminal()
        {
            string output = Path.Combine(_directory, "coalesced-files.json");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var writer = new BlockProbeReportWriter(output, (destination, json) => {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Release the original progress write.");
                BlockProbeReportWriter.WriteAtomic(destination, json);
            });
            try
            {
                writer.SubmitProgress("old progress"); Assert.That(entered.Wait(5000), Is.True);
                var first = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "first" });
                string firstPath = first.Path;
                writer.SubmitProgress(first); first.Dispose();
                Assert.That(File.Exists(firstPath), Is.True);
                var second = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "second" });
                string secondPath = second.Path;
                writer.SubmitProgress(second); second.Dispose();
                Assert.That(File.Exists(firstPath), Is.False);
                var terminal = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "terminal" });
                string terminalPath = terminal.Path;
                writer.SubmitTerminal(terminal); terminal.Dispose();
                Assert.That(File.Exists(secondPath), Is.False);
                Assert.That(writer.SubmitProgress("late progress"), Is.Zero);
                release.Set(); Assert.That(writer.WaitForIdle(5000), Is.True);
                Assert.That(writer.PollTerminal(), Is.True);
                Assert.That(File.Exists(terminalPath), Is.False);
                Assert.That(UnityEngine.JsonUtility.FromJson<FilePayload>(File.ReadAllText(output)).label, Is.EqualTo("terminal"));
                Assert.That(writer.ReadStatistics().coalescedProgress, Is.EqualTo(2));
            }
            finally { release.Set(); }
        }

        [Test] public void DefaultFilePublicationAtomicallyReplacesTheDestinationAndPreservesAllBytes()
        {
            string output = Path.Combine(_directory, "atomic-file.json");
            File.WriteAllText(output, "previous complete report");
            using var snapshot = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "无截断" });
            using var writer = new BlockProbeReportWriter(output);
            long sequence = writer.SubmitProgress(snapshot);
            Assert.That(writer.WaitForIdle(5000), Is.True);
            Assert.That(File.ReadAllBytes(output), Is.EqualTo(File.ReadAllBytes(snapshot.Path)));
            Assert.That(File.Exists(output + ".writing"), Is.False);
            var stats = writer.ReadStatistics();
            Assert.That(stats.lastPublishedProgressSequence, Is.EqualTo(sequence));
            Assert.That(stats.atomicWriteAttempts, Is.EqualTo(1)); Assert.That(stats.failedWrites, Is.Zero);
            Assert.That(File.Exists(snapshot.Path), Is.True, "The producer's retained lease survives publication.");
        }

        [Test] public void ASealedFileCleanupFailureCannotStrandThePublishedTerminalAndRetriesAtClose()
        {
            string output = Path.Combine(_directory, "cleanup-retry.json");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            FileStream sourceLock = null;
            using var writer = new BlockProbeReportWriter(output, copyFile: (destination, source) => {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Release the file publisher.");
                BlockProbeReportWriter.WriteAtomicFile(destination, source);
                sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            });
            try
            {
                var snapshot = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "cleanup retry" });
                string source = snapshot.Path;
                writer.SubmitTerminal(snapshot); Assert.That(entered.Wait(5000), Is.True);
                snapshot.Dispose(); release.Set();
                Assert.That(writer.WaitForIdle(5000), Is.True, "Cleanup failure must always release the running state.");
                Assert.That(writer.PollTerminal(), Is.True);
                var stats = writer.ReadStatistics();
                Assert.That(stats.fileCleanupFailures, Is.EqualTo(1));
                Assert.That(stats.retainedFileCleanupCount, Is.EqualTo(1));
                Assert.That(stats.lastFileCleanupError, Does.Contain("IOException"));
                Assert.That(stats.failedWrites, Is.Zero); Assert.That(File.Exists(source), Is.True);
                sourceLock.Dispose(); sourceLock = null;
                Assert.That(writer.Close(5000), Is.True);
                Assert.That(writer.ReadStatistics().retainedFileCleanupCount, Is.Zero);
                Assert.That(File.Exists(source), Is.False);
            }
            finally { release.Set(); sourceLock?.Dispose(); }
        }

        [TestCase(false)] [TestCase(true)]
        public void DisposedFileSubmissionCannotMutateThePendingQueueOrLatchTheTerminal(bool terminal)
        {
            string output = Path.Combine(_directory, "disposed-submission.json");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var writer = new BlockProbeReportWriter(output, (destination, json) => {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Release the active progress write.");
                BlockProbeReportWriter.WriteAtomic(destination, json);
            });
            try
            {
                writer.SubmitProgress("active"); Assert.That(entered.Wait(5000), Is.True);
                var pending = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "valid pending" });
                string pendingPath = pending.Path; writer.SubmitProgress(pending); pending.Dispose();
                var disposed = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "disposed input" });
                disposed.Dispose(); var before = writer.ReadStatistics();
                if (terminal) Assert.Throws<ObjectDisposedException>(() => writer.SubmitTerminal(disposed));
                else Assert.Throws<ObjectDisposedException>(() => writer.SubmitProgress(disposed));
                var after = writer.ReadStatistics();
                Assert.That(after.submittedProgress, Is.EqualTo(before.submittedProgress));
                Assert.That(after.coalescedProgress, Is.EqualTo(before.coalescedProgress));
                Assert.That(after.terminalAccepted, Is.False);
                Assert.That(File.Exists(pendingPath), Is.True, "An invalid new lease cannot release the accepted pending payload.");
                var valid = BlockProbeReportFile.Capture(output + ".snapshot", new FilePayload { label = "valid terminal" });
                writer.SubmitTerminal(valid); valid.Dispose();
                Assert.That(File.Exists(pendingPath), Is.False);
                release.Set(); Assert.That(writer.WaitForIdle(5000), Is.True); Assert.That(writer.PollTerminal(), Is.True);
                Assert.That(UnityEngine.JsonUtility.FromJson<FilePayload>(File.ReadAllText(output)).label, Is.EqualTo("valid terminal"));
                Assert.That(writer.ReadStatistics().failedWrites, Is.Zero);
            }
            finally { release.Set(); }
        }

    }
}
#endif
