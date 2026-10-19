#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Prototype;
using Unity.Profiling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridProbeCheckpointTests
    {
        const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly Type ProbeType = typeof(BlockFixedGridAcceptanceProbe);
        static readonly Type ReportType = ProbeType.GetNestedType("Report", BindingFlags.NonPublic);

        sealed class EmptyEnvironment : IBlockFixedGridEnvironment
        {
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            { support = default; return false; }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase,
                bool relocating, out string reason) { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
        }

        [Serializable] sealed class WriterDiagnostic
        {
            public string utc, test, stage, output, diagnosticPath;
            public long expectedSequence, checkpointSequence;
            public int checkpointCaptures, checkpointRetries;
            public BlockProbeReportWriter.Statistics writer, sidecar;
        }

        // Exercise the production Probe.Write/Finish and real asynchronous writers without
        // activating Awake, opening a network session, or installing a runtime Probe singleton.
        sealed class Fixture : IDisposable
        {
            internal readonly string DirectoryPath, Output, DiagnosticPath;
            internal readonly GameObject Root;
            internal readonly BlockFixedGridAcceptanceProbe Probe;
            internal readonly object Report;
            internal readonly BlockProbeCommandReader Reader;
            internal readonly BlockProbeReportWriter Writer, Sidecar;
            internal readonly BlockProbeReportCaptureQueue Captures;
            internal readonly BlockFixedGridWorld World;
            readonly List<IDisposable> _series = new();

            internal Fixture(Action<string, string> write = null, string diagnosticName = null,
                Func<string, object, BlockProbeReportFile> capture = null, Func<object, object> freeze = null)
            {
                DirectoryPath = Path.Combine(Path.GetTempPath(), "Splatoon-fixed-checkpoint-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DirectoryPath); Output = Path.Combine(DirectoryPath, "probe.json");
                if (!string.IsNullOrEmpty(diagnosticName))
                    DiagnosticPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Reports", "BlockSimulation20261018",
                        "FixedGrid65CheckpointDiagnostics01", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" +
                        diagnosticName + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
                Root = new GameObject("Inactive fixed-grid checkpoint fixture"); Root.SetActive(false);
                Probe = Root.AddComponent<BlockFixedGridAcceptanceProbe>();
                Assert.That(Get<double>(Probe, "_boot"), Is.Zero, "Awake must not run in this fixture.");
                Report = Activator.CreateInstance(ReportType, true);
                Set(Report, "session", "checkpoint-session"); Set(Report, "label", "checkpoint-host");
                Set(Report, "mode", "natural"); Set(Report, "stage", "combat");
                Set(Report, "connectionGenerations", 1); Set(Report, "completedNormalRound", true);
                Reader = new BlockProbeCommandReader(Path.Combine(DirectoryPath, "control.txt"),
                    read: _ => Array.Empty<string>(), pollMilliseconds: 1000);
                Writer = new BlockProbeReportWriter(Output, write, write == null ? null :
                    (destination, source) => write(destination, File.ReadAllText(source)));
                Sidecar = new BlockProbeReportWriter(Output + ".progress.json");
                Captures = new BlockProbeReportCaptureQueue(Output + ".snapshot", freeze, capture);
                Set(Probe, "_report", Report); Set(Probe, "_reader", Reader); Set(Probe, "_writer", Writer);
                Set(Probe, "_checkpointProgressWriter", Sidecar); Set(Probe, "_output", Output);
                Set(Probe, "_captureQueue", Captures);
                Set(Probe, "_stage", "combat"); Set(Probe, "_command", 1);
                var seriesType = ProbeType.GetNestedType("Series", BindingFlags.NonPublic);
                foreach (string field in new[] { "_main", "_render", "_physics", "_gc" })
                {
                    var series = (IDisposable)Activator.CreateInstance(seriesType,
                        new object[] { ProfilerCategory.Internal, "Main Thread", .000001d, false });
                    _series.Add(series); Set(Probe, field, series);
                }
                World = NewWorld(); Assert.That(World.Freeze(), Is.True);
                Diagnostic("fixture-created");
            }

            internal List<T> Raw<T>(string name) => Get<List<T>>(Report, name);
            internal List<double> Frames => Get<List<double>>(Probe, "_frames");
            internal bool Write(bool terminal = false, bool progressOnly = false, bool retain = false)
                => (bool)Call(Probe, "Write", terminal, progressOnly, retain, null);
            internal void Checkpoint()
            {
                Call(Probe, "RememberCompletedRoundVersion", World);
                Assert.That(Write(retain: true), Is.True);
                EncodeIdle();
                Assert.That(Get<long>(Probe, "_checkpointSequence"), Is.GreaterThan(0));
            }
            internal void EncodeIdle()
            {
                Assert.That(Captures.WaitForIdle(5000), Is.True);
                Call(Probe, "PumpFullCaptures");
            }
            internal bool Heartbeat(BlockFixedGridWorld world = null)
                => (bool)Call(Probe, "TryPublishCompletedRoundProgress", world ?? World);
            internal void Idle()
            {
                EncodeIdle();
                Assert.That(Writer.WaitForIdle(5000), Is.True);
                Assert.That(Sidecar.WaitForIdle(5000), Is.True);
            }
            internal string Diagnostic(string stage, long expectedSequence = 0)
            {
                if (DiagnosticPath == null) return null;
                var snapshot = new WriterDiagnostic {
                    utc = DateTime.UtcNow.ToString("O"), test = TestContext.CurrentContext.Test.FullName,
                    stage = stage, output = Output, diagnosticPath = DiagnosticPath,
                    expectedSequence = expectedSequence, checkpointSequence = Get<long>(Probe, "_checkpointSequence"),
                    checkpointCaptures = Get<int>(Report, "completedRoundCheckpointCaptures"),
                    checkpointRetries = Get<int>(Report, "completedRoundCheckpointRetries"),
                    writer = Writer.ReadStatistics(), sidecar = Sidecar.ReadStatistics()
                };
                string json = JsonUtility.ToJson(snapshot, false);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticPath));
                    File.AppendAllText(DiagnosticPath, json + "\n", new System.Text.UTF8Encoding(false));
                }
                catch (Exception error)
                {
                    // Diagnostics must never replace the original writer/assertion failure.
                    return json + " diagnosticAppendFailure=" + error.GetType().Name + ": " + error.Message;
                }
                return "writerDiagnosticPath=" + DiagnosticPath + "\n" + json;
            }
            public void Dispose()
            {
                Diagnostic("fixture-dispose-before");
                Assert.That(Captures.Close(5000), Is.True);
                Reader.Dispose(); Reader.Completion.Wait(5000);
                Writer.Dispose(); Sidecar.Dispose();
                Get<BlockProbeReportFile>(Probe, "_checkpointFile")?.Dispose(); Set(Probe, "_checkpointFile", null);
                Diagnostic("fixture-dispose-after");
                // DestroyImmediate need not call OnDestroy for a never-active object.
                // Clear installed resources so cleanup is safe in either Unity lifecycle.
                Set(Probe, "_reader", null); Set(Probe, "_writer", null); Set(Probe, "_checkpointProgressWriter", null);
                Set(Probe, "_captureQueue", null);
                foreach (string field in new[] { "_main", "_render", "_physics", "_gc" }) Set(Probe, field, null);
                foreach (var series in _series) series.Dispose();
                Object.DestroyImmediate(Root);
                if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
            }
        }

        [Serializable] sealed class PayloadRead
        {
            public bool complete = false;
            public int completedRoundCheckpointCaptures = 0;
            public double[] frameMilliseconds = Array.Empty<double>(), collisionMilliseconds = Array.Empty<double>(),
                ordinaryMainCollisionMilliseconds = Array.Empty<double>();
            public BlockFixedGridAcceptanceProbe.FrameRecord[] frameRecords = Array.Empty<BlockFixedGridAcceptanceProbe.FrameRecord>();
            public BlockFixedGridAcceptanceProbe.PaymentRecord[] paidEvents = Array.Empty<BlockFixedGridAcceptanceProbe.PaymentRecord>();
            public BlockFixedGridAcceptanceProbe.ActualRecord[] actualEvents = Array.Empty<BlockFixedGridAcceptanceProbe.ActualRecord>();
            public long[] cpuRawValues = Array.Empty<long>(), cpuSampleCounts = Array.Empty<long>();
            public byte[] cpuRecorderStates = Array.Empty<byte>();
            public int[] completedFixedStepsPerFrame = Array.Empty<int>();
        }
        [Serializable] sealed class ProgressRead
        {
            public bool progressOnly = false, frozen = false;
            public string phase = "", observerFailure = "", checkpointPayloadSha256 = "";
            public int samples = 0, errors = 0, failures = 0;
            public double sampledAt = 0;
            public long completedRoundCheckpointSequence = 0, publishedProgressSequence = 0;
        }
        [Serializable] sealed class ReceiptRead
        {
            public string session = "", label = "", payloadSha256 = "";
            public bool terminalPayloadMatches = false;
            public BlockProbeReportWriter.Statistics writer = default;
        }

        static string SnapshotJson(BlockFixedGridAcceptanceProbe probe)
        { var file = Get<BlockProbeReportFile>(probe, "_checkpointFile"); return file == null ? null : File.ReadAllText(file.Path); }

        static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, InstanceFields).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, InstanceFields).SetValue(target, value);
        static object Call(object target, string method, params object[] arguments)
            => target.GetType().GetMethod(method, PrivateInstance).Invoke(target, arguments);
        static void ChangeProperty(object target, string property, object value)
            => target.GetType().GetProperty(property).GetSetMethod(true).Invoke(target, new[] { value });
        static BlockFixedGridWorld NewWorld()
        {
            var surfaces = new BlockSurfaceWorld(7);
            surfaces.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, new[] { Vector2Int.zero });
            return new BlockFixedGridWorld(new BlockWorld(new Vector2(3, 3), 7, -8), surfaces, new EmptyEnvironment());
        }
        static string Hash(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        [Test] public void CompletedFullCaptureLeavesANetworkLoopIntervalAndProgressDoesNotReencodeRaw()
        {
            using var fixture = new Fixture();
            Set(fixture.Probe, "_nextWrite", -1d);
            double before = Time.realtimeSinceStartupAsDouble;
            Assert.That(fixture.Write(), Is.True);
            fixture.Idle();
            Assert.That(Get<double>(fixture.Probe, "_nextWrite"), Is.GreaterThanOrEqualTo(before + .5));
            Assert.That(Get<int>(fixture.Report, "reportFullCaptureCount"), Is.EqualTo(1));
            Assert.That(Get<double>(fixture.Report, "reportFullCaptureLastMilliseconds"), Is.GreaterThan(0));
            Assert.That(File.ReadAllText(fixture.Output), Does.Contain("\"reportFullCaptureCount\":0"),
                "Full capture counters describe completed encodes before that immutable payload.");
            double deadline = Get<double>(fixture.Probe, "_nextWrite");
            Assert.That(fixture.Write(progressOnly: true), Is.True);
            Assert.That(Get<int>(fixture.Report, "reportFullCaptureCount"), Is.EqualTo(1));
            Assert.That(Get<double>(fixture.Probe, "_nextWrite"), Is.EqualTo(deadline));
        }

        [Test] public void PendingCheckpointHeartbeatsDoNotFreezeAgainAndNewCommandRejectsLateCompletion()
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int freezes = 0;
            using var f = new Fixture(capture: (prefix, payload) => {
                entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Encoder gate was not released.");
                return BlockProbeReportFile.Capture(prefix, payload);
            }, freeze: payload => { Interlocked.Increment(ref freezes); return BlockProbeReportSnapshot.Capture(payload); });
            try
            {
                AppendRaw(f, 1); Call(f.Probe, "RememberCompletedRoundVersion", f.World);
                Assert.That(f.Write(retain: true), Is.True); Assert.That(entered.Wait(5000), Is.True);
                for (int i = 0; i < 64; i++) Assert.That(f.Heartbeat(), Is.True);
                Assert.That(f.Write(), Is.False);
                Assert.That(freezes, Is.EqualTo(1)); Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.Zero);
                Assert.That(f.Sidecar.WaitForIdle(5000), Is.True);
                Call(f.Probe, "Command", 2, "drain");
                release.Set(); f.Idle();
                Assert.That(SnapshotJson(f.Probe), Is.Null);
                Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.Zero);
                Assert.That(File.Exists(f.Output), Is.False, "An old capture cannot overwrite the new command barrier.");
                Assert.That(File.ReadAllText(f.Output + ".progress.json"), Does.Contain("\"command\":2"));
                Assert.That(Directory.GetFiles(f.DirectoryPath, "*.sealed.json"), Is.Empty);
            }
            finally { release.Set(); }
        }

        [TestCase("world")] [TestCase("epoch")] [TestCase("round")] [TestCase("revision")]
        [TestCase("surfaceRevision")] [TestCase("frozen")] [TestCase("command")] [TestCase("generation")]
        public void PendingCheckpointCannotInstallAfterAnyIdentityComponentChanges(string changed)
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var f = new Fixture(capture: (prefix, payload) => {
                entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Encoder gate was not released.");
                return BlockProbeReportFile.Capture(prefix, payload);
            });
            try
            {
                Call(f.Probe, "RememberCompletedRoundVersion", f.World);
                Assert.That(f.Write(retain: true), Is.True); Assert.That(entered.Wait(5000), Is.True);
                var world = f.World;
                switch (changed)
                {
                    case "world": world = NewWorld(); Assert.That(world.Freeze(), Is.True);
                        Call(f.Probe, "RememberCompletedRoundVersion", world); break;
                    case "epoch": ChangeProperty(world, "Epoch", world.Epoch + 1); break;
                    case "round": ChangeProperty(world.Storage, "Round", world.Round + 1); break;
                    case "revision": ChangeProperty(world, "Revision", world.Revision + 1); break;
                    case "surfaceRevision": Assert.That(world.Surfaces.TrySet(new BlockSurfaceKey(11, 0, 0), 2, out _), Is.True); break;
                    case "frozen": ChangeProperty(world, "Frozen", false); break;
                    case "command": Set(f.Probe, "_command", 2); break;
                    case "generation": Set(f.Report, "connectionGenerations", 2); break;
                }
                Assert.That(f.Heartbeat(world), Is.False, changed);
                release.Set(); f.Idle();
                Assert.That(SnapshotJson(f.Probe), Is.Null, changed);
                Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.Zero, changed);
                Assert.That(Directory.GetFiles(f.DirectoryPath, "*.sealed.json"), Is.Empty);
            }
            finally { release.Set(); }
        }

        [Test] public void TerminalEncoderFailureRetriesExactFrozenGraphWithoutAdmittingAnotherFreeze()
        {
            int freezes = 0, attempts = 0; object frozen = null;
            using var f = new Fixture(capture: (prefix, payload) => {
                if (Interlocked.Increment(ref attempts) == 1) { frozen = payload; throw new IOException("Injected encoding failure."); }
                Assert.That(payload, Is.SameAs(frozen)); return BlockProbeReportFile.Capture(prefix, payload);
            }, freeze: payload => { Interlocked.Increment(ref freezes); return BlockProbeReportSnapshot.Capture(payload); });
            AppendRaw(f, 1); Call(f.Probe, "Finish"); f.EncodeIdle();
            Assert.That(freezes, Is.EqualTo(1)); Assert.That(File.Exists(f.Output + ".writer.json"), Is.False);
            AppendRaw(f, 2); // Simulate an errant late source mutation: the terminal must remain unchanged.
            Assert.That(f.Write(terminal: true), Is.False); f.Idle();
            var terminal = JsonUtility.FromJson<PayloadRead>(File.ReadAllText(f.Output));
            Assert.That(terminal.frameRecords.Length, Is.EqualTo(1)); Assert.That(terminal.paidEvents.Length, Is.EqualTo(1));
            Assert.That(freezes, Is.EqualTo(1)); Assert.That(attempts, Is.EqualTo(2));
            Assert.That(Get<long>(f.Report, "reportCaptureFailures"), Is.EqualTo(1));
            Assert.That(Get<int>(f.Probe, "_terminalLateErrors"), Is.EqualTo(1));
            Assert.That(f.Write(terminal: true), Is.True);
            Assert.That(File.ReadAllText(f.Output + ".writer.json"), Does.Contain("\"terminalLateErrors\":1"));
        }

        [Test] public void TerminalFreezeFailureNeverChoosesANewCutoffAndLateErrorsDoNotMutateFrozenReport()
        {
            int freezes = 0;
            using var f = new Fixture(freeze: payload => { Interlocked.Increment(ref freezes); throw new IOException("Injected freeze failure."); });
            AppendRaw(f, 1); Call(f.Probe, "Finish");
            Assert.That(Get<bool>(f.Probe, "_terminalFreezeFailed"), Is.True);
            Assert.That(Get<int>(f.Report, "errors"), Is.Zero);
            Assert.That(Get<int>(f.Probe, "_terminalLateErrors"), Is.EqualTo(1));
            Call(f.Probe, "Error", "late log failure");
            for (int i = 0; i < 8; i++) Assert.That(f.Write(terminal: true), Is.False);
            Assert.That(freezes, Is.EqualTo(1)); Assert.That(Get<int>(f.Report, "errors"), Is.Zero);
            Assert.That(Get<int>(f.Probe, "_terminalLateErrors"), Is.EqualTo(2));
            Assert.That(File.Exists(f.Output), Is.False); Assert.That(File.Exists(f.Output + ".writer.json"), Is.False);
        }

        [Test] public void EarlierSlowProgressCannotAcknowledgeTheRetainedCheckpointAndFailedCheckpointRetriesExactJson()
        {
            const string previous = "{\"progressOnly\":true,\"samples\":0}";
            using var oldEntered = new ManualResetEventSlim(); using var releaseOld = new ManualResetEventSlim();
            using var checkpointEntered = new ManualResetEventSlim(); using var releaseCheckpoint = new ManualResetEventSlim();
            var attempts = new ConcurrentQueue<string>(); int checkpointAttempts = 0;
            using var f = new Fixture((path, json) => {
                attempts.Enqueue(json);
                if (json == previous)
                {
                    oldEntered.Set();
                    if (!releaseOld.Wait(5000)) throw new TimeoutException("Old progress gate was not released.");
                }
                else if (Interlocked.Increment(ref checkpointAttempts) == 1)
                {
                    checkpointEntered.Set();
                    if (!releaseCheckpoint.Wait(5000)) throw new TimeoutException("Checkpoint gate was not released.");
                    throw new IOException("Injected failure after earlier progress was successfully published.");
                }
                BlockProbeReportWriter.WriteAtomic(path, json);
            }, diagnosticName: "EarlierSlowProgress");
            try
            {
                long previousSequence = f.Writer.SubmitProgress(previous); Assert.That(oldEntered.Wait(5000), Is.True);
                f.Diagnostic("previous-writer-entered", previousSequence);
                f.Frames.Add(10); f.Checkpoint(); string exact = SnapshotJson(f.Probe);
                long checkpointSequence = Get<long>(f.Probe, "_checkpointSequence");
                f.Diagnostic("checkpoint-submitted", checkpointSequence);
                Assert.That(checkpointSequence, Is.GreaterThan(previousSequence));
                Assert.That(f.Heartbeat(), Is.True);
                Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence, Is.Zero);
                f.Diagnostic("before-previous-release", previousSequence);
                releaseOld.Set(); Assert.That(checkpointEntered.Wait(5000), Is.True);
                f.Diagnostic("checkpoint-writer-entered", checkpointSequence);
                Assert.That(f.Writer.ReadStatistics().completedWrites, Is.EqualTo(1));
                Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence, Is.EqualTo(previousSequence));
                Assert.That(f.Heartbeat(), Is.True);
                Assert.That(Get<int>(f.Report, "completedRoundCheckpointRetries"), Is.Zero,
                    "Neither accepting the checkpoint nor finishing an older write acknowledges checkpoint publication.");
                Assert.That(f.Sidecar.WaitForIdle(5000), Is.True);
                f.Diagnostic("second-sidecar-idle", previousSequence);
                var pending = JsonUtility.FromJson<ProgressRead>(File.ReadAllText(f.Output + ".progress.json"));
                Assert.That(pending.completedRoundCheckpointSequence, Is.EqualTo(checkpointSequence));
                Assert.That(pending.publishedProgressSequence, Is.EqualTo(previousSequence),
                    f.Diagnostic("assert-sidecar-published-progress-sequence", previousSequence));
                Assert.That(pending.checkpointPayloadSha256, Is.EqualTo(Hash(System.Text.Encoding.UTF8.GetBytes(exact))));
                releaseCheckpoint.Set(); f.Idle();
                f.Diagnostic("injected-checkpoint-failure-idle", checkpointSequence);
                Assert.That(f.Writer.ReadStatistics().failedWrites, Is.EqualTo(1));
                Assert.That(File.ReadAllText(f.Output), Is.EqualTo(previous));
                f.Frames.Add(999); // A retry must not silently recapture changed raw evidence.
                Assert.That(f.Heartbeat(), Is.True); f.Idle();
                f.Diagnostic("checkpoint-retry-idle", Get<long>(f.Probe, "_checkpointSequence"));
                Assert.That(File.ReadAllText(f.Output), Is.EqualTo(exact));
                Assert.That(attempts.ToArray(), Is.EqualTo(new[] { previous, exact, exact }));
                Assert.That(SnapshotJson(f.Probe), Is.EqualTo(exact));
                Assert.That(Get<int>(f.Report, "completedRoundCheckpointCaptures"), Is.EqualTo(1));
                Assert.That(Get<int>(f.Report, "completedRoundCheckpointRetries"), Is.EqualTo(1));
                Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence,
                    Is.EqualTo(Get<long>(f.Probe, "_checkpointSequence")));
            }
            finally
            {
                f.Diagnostic("finally-before-gate-release");
                releaseOld.Set(); releaseCheckpoint.Set();
            }
        }

        [Test] public void FirstCheckpointDiskFailureIsRetriedWithoutRecapturingItsPayload()
        {
            var attempts = new ConcurrentQueue<string>(); int writes = 0;
            using var f = new Fixture((path, json) => {
                attempts.Enqueue(json);
                if (Interlocked.Increment(ref writes) == 1) throw new IOException("Injected first checkpoint failure.");
                BlockProbeReportWriter.WriteAtomic(path, json);
            });
            f.Frames.Add(12); f.Checkpoint(); f.Idle(); string exact = SnapshotJson(f.Probe);
            Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence, Is.Zero);
            Assert.That(f.Writer.ReadStatistics().completedWrites, Is.Zero);
            Assert.That(f.Writer.ReadStatistics().failedWrites, Is.EqualTo(1));
            f.Frames.Add(25);
            Assert.That(f.Heartbeat(), Is.True); f.Idle();
            Assert.That(attempts.ToArray(), Is.EqualTo(new[] { exact, exact }));
            Assert.That(File.ReadAllText(f.Output), Is.EqualTo(exact));
            Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence, Is.EqualTo(2));
            Assert.That(Get<int>(f.Report, "completedRoundCheckpointCaptures"), Is.EqualTo(1));
        }

        [Test] public void ALaterUnrelatedProgressSequenceCannotAcknowledgeTheRetainedCheckpoint()
        {
            using var f = new Fixture(diagnosticName: "LaterUnrelatedProgress"); f.Checkpoint(); f.Idle();
            string exact = SnapshotJson(f.Probe);
            long checkpointSequence = Get<long>(f.Probe, "_checkpointSequence");
            f.Diagnostic("checkpoint-idle", checkpointSequence);
            long unrelated = f.Writer.SubmitProgress("{\"progressOnly\":true,\"unrelated\":true}");
            f.Diagnostic("unrelated-submitted", unrelated);
            f.Idle();
            f.Diagnostic("unrelated-idle", unrelated);
            Assert.That(unrelated, Is.GreaterThan(checkpointSequence));
            Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence, Is.EqualTo(unrelated),
                f.Diagnostic("assert-unrelated-published-progress-sequence", unrelated));
            Assert.That(f.Heartbeat(), Is.True); f.Idle();
            f.Diagnostic("retained-checkpoint-retry-idle", Get<long>(f.Probe, "_checkpointSequence"));
            Assert.That(File.ReadAllText(f.Output), Is.EqualTo(exact));
            Assert.That(Get<long>(f.Probe, "_checkpointSequence"), Is.GreaterThan(unrelated));
            Assert.That(f.Writer.ReadStatistics().lastPublishedProgressSequence,
                Is.EqualTo(Get<long>(f.Probe, "_checkpointSequence")));
            Assert.That(Get<int>(f.Report, "completedRoundCheckpointRetries"), Is.EqualTo(1));
            Assert.That(Get<int>(f.Report, "completedRoundCheckpointCaptures"), Is.EqualTo(1));
        }

        [Serializable] sealed class CommandProgressRead
        {
            public bool progressOnly = false;
            public string session = "", label = "", mode = "", stage = "", checkpointPayloadSha256 = "";
            public int command = 0, generation = 0, errors = 0, samples = 0;
            public double sampledAt = 0;
            public long completedRoundCheckpointSequence = 0;
        }

        [Test] public void ConsumedCommandPublishesItsOwnSmallReceiptWithoutReplacingFullOrLosingLaterRawEvidence()
        {
            using var f = new Fixture(); AppendRaw(f, 1); f.Checkpoint(); f.Idle();
            byte[] retained = File.ReadAllBytes(f.Output);
            AppendRaw(f, 2); Set(f.Report, "errors", 3);
            double before = Time.realtimeSinceStartupAsDouble;
            Call(f.Probe, "Command", 2, "drain");
            double after = Time.realtimeSinceStartupAsDouble;
            f.Idle();
            Assert.That(File.ReadAllBytes(f.Output), Is.EqualTo(retained));
            Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.EqualTo(1));
            Assert.That(f.Sidecar.ReadStatistics().submittedProgress, Is.EqualTo(1));
            string json = File.ReadAllText(f.Output + ".progress.json");
            var receipt = JsonUtility.FromJson<CommandProgressRead>(json);
            Assert.That(receipt.progressOnly, Is.True);
            Assert.That(receipt.session, Is.EqualTo("checkpoint-session"));
            Assert.That(receipt.label, Is.EqualTo("checkpoint-host"));
            Assert.That(receipt.mode, Is.EqualTo("natural"));
            Assert.That(receipt.stage, Is.EqualTo("drain")); Assert.That(receipt.command, Is.EqualTo(2));
            Assert.That(receipt.generation, Is.EqualTo(1)); Assert.That(receipt.errors, Is.EqualTo(3));
            Assert.That(receipt.samples, Is.EqualTo(2)); Assert.That(receipt.sampledAt, Is.InRange(before, after));
            Assert.That(receipt.completedRoundCheckpointSequence, Is.Zero);
            Assert.That(receipt.checkpointPayloadSha256, Is.Null.Or.Empty);
            Assert.That(json.Contains("frameRecords"), Is.False);
            Assert.That(SnapshotJson(f.Probe), Is.Null);
            Call(f.Probe, "Command", 2, "drain"); Call(f.Probe, "Command", 1, "combat"); f.Idle();
            Assert.That(f.Sidecar.ReadStatistics().submittedProgress, Is.EqualTo(1), "Stale commands have no receipt");
            Assert.That(File.ReadAllBytes(f.Output), Is.EqualTo(retained));
            Assert.That(f.Write(), Is.True); f.Idle();
            var fresh = JsonUtility.FromJson<PayloadRead>(File.ReadAllText(f.Output));
            Assert.That(fresh.frameRecords.Length, Is.EqualTo(2));
            Assert.That(fresh.paidEvents.Length, Is.EqualTo(2)); Assert.That(fresh.actualEvents.Length, Is.EqualTo(2));
            Assert.That(fresh.cpuRawValues, Is.EqualTo(new[] { 111L, 222L }));
            Assert.That(fresh.complete, Is.False, "Command receipt cannot become terminal acceptance");
        }

        static void AppendRaw(Fixture f, int index)
        {
            f.Frames.Add(index * 10); Get<List<double>>(f.Probe, "_collision").Add(index * 15);
            Get<List<double>>(f.Probe, "_ordinaryMainCollision").Add(index * 15);
            f.Raw<BlockFixedGridAcceptanceProbe.FrameRecord>("frameRecords").Add(new BlockFixedGridAcceptanceProbe.FrameRecord {
                frameId = index, observedAtFrameId = index + 1, stage = "combat", frameMilliseconds = index * 10,
                cpuOffset = index - 1, cpuCount = 1 });
            f.Raw<long>("cpuRawValues").Add(index * 111); f.Raw<byte>("cpuRecorderStates").Add(7);
            f.Raw<long>("cpuSampleCounts").Add(1); f.Raw<int>("completedFixedStepsPerFrame").Add(index);
            f.Raw<BlockFixedGridAcceptanceProbe.PaymentRecord>("paidEvents").Add(new BlockFixedGridAcceptanceProbe.PaymentRecord {
                source = new BlockFixedGridAcceptanceProbe.SourceRecord { owner = 73, action = (ulong)index, round = 7 },
                context = new BlockFixedGridAcceptanceProbe.EventContext { sequence = index * 2 - 1, stage = "combat" }, material = index });
            f.Raw<BlockFixedGridAcceptanceProbe.ActualRecord>("actualEvents").Add(new BlockFixedGridAcceptanceProbe.ActualRecord {
                source = new BlockFixedGridAcceptanceProbe.SourceRecord { owner = 73, action = (ulong)index, round = 7 },
                context = new BlockFixedGridAcceptanceProbe.EventContext { sequence = index * 2, stage = "combat" },
                entity = (uint)index, hadChanges = true, counts = new BlockFixedGridAcceptanceProbe.ActualCountRecord { newPaint = index } });
        }

        [Test] public void PublishedCheckpointSurvivesManySidecarsAndTerminalContainsLaterRawEvidenceWithExactReceipt()
        {
            using var f = new Fixture(); AppendRaw(f, 1); f.Checkpoint(); f.Idle();
            string checkpoint = File.ReadAllText(f.Output); byte[] checkpointBytes = File.ReadAllBytes(f.Output);
            var first = JsonUtility.FromJson<PayloadRead>(checkpoint);
            Assert.That(first.complete, Is.False); Assert.That(first.frameRecords.Length, Is.EqualTo(1));
            AppendRaw(f, 2);
            Set(f.Report, "errors", 7);
            double heartbeatStarted = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 64; i++) Assert.That(f.Heartbeat(), Is.True);
            double heartbeatFinished = Time.realtimeSinceStartupAsDouble;
            f.Idle();
            Assert.That(File.ReadAllBytes(f.Output), Is.EqualTo(checkpointBytes), "Heartbeat status must preserve the full barrier file.");
            Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.EqualTo(1));
            Assert.That(f.Writer.ReadStatistics().completedWrites, Is.EqualTo(1));
            Assert.That(f.Sidecar.ReadStatistics().submittedProgress, Is.EqualTo(64));
            var progress = JsonUtility.FromJson<ProgressRead>(File.ReadAllText(f.Output + ".progress.json"));
            Assert.That(progress.progressOnly, Is.True); Assert.That(progress.samples, Is.EqualTo(2));
            Assert.That(progress.completedRoundCheckpointSequence, Is.EqualTo(Get<long>(f.Probe, "_checkpointSequence")));
            Assert.That(progress.publishedProgressSequence, Is.EqualTo(progress.completedRoundCheckpointSequence));
            Assert.That(progress.checkpointPayloadSha256, Is.EqualTo(Hash(checkpointBytes)));
            Assert.That(progress.sampledAt, Is.InRange(heartbeatStarted, heartbeatFinished));
            Assert.That(progress.errors, Is.EqualTo(7)); Assert.That(progress.failures, Is.Zero);
            Assert.That(progress.frozen, Is.False, "An absent match must not invent world status.");
            Assert.That(progress.phase, Is.Null.Or.Empty); Assert.That(progress.observerFailure, Is.Null.Or.Empty);
            Assert.That(Get<int>(f.Report, "completedRoundCheckpointCaptures"), Is.EqualTo(1));
            Assert.That(Get<int>(f.Report, "completedRoundCheckpointRetries"), Is.Zero);

            Call(f.Probe, "Finish");
            Assert.That(SnapshotJson(f.Probe), Is.Null);
            Assert.That(Get<string>(f.Probe, "_checkpointHash"), Is.Null);
            Assert.That(Get<long>(f.Probe, "_checkpointSequence"), Is.Zero);
            f.Idle();
            string terminalJson = File.ReadAllText(f.Output); byte[] terminalBytes = File.ReadAllBytes(f.Output);
            var terminal = JsonUtility.FromJson<PayloadRead>(terminalJson);
            Assert.That(terminalJson, Is.Not.EqualTo(checkpoint)); Assert.That(terminal.complete, Is.True);
            Assert.That(terminal.frameMilliseconds, Is.EqualTo(new[] { 10d, 20d }));
            Assert.That(terminal.collisionMilliseconds, Is.EqualTo(new[] { 15d, 30d }));
            Assert.That(terminal.ordinaryMainCollisionMilliseconds, Is.EqualTo(new[] { 15d, 30d }));
            Assert.That(terminal.frameRecords.Length, Is.EqualTo(2)); Assert.That(terminal.frameRecords[1].frameId, Is.EqualTo(2));
            Assert.That(terminal.cpuRawValues, Is.EqualTo(new[] { 111L, 222L }));
            Assert.That(terminal.cpuRecorderStates, Is.EqualTo(new byte[] { 7, 7 }));
            Assert.That(terminal.cpuSampleCounts, Is.EqualTo(new[] { 1L, 1L }));
            Assert.That(terminal.completedFixedStepsPerFrame, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(terminal.paidEvents.Length, Is.EqualTo(2)); Assert.That(terminal.paidEvents[1].source.action, Is.EqualTo(2));
            Assert.That(terminal.actualEvents.Length, Is.EqualTo(2)); Assert.That(terminal.actualEvents[1].counts.newPaint, Is.EqualTo(2));
            Assert.That(terminal.completedRoundCheckpointCaptures, Is.EqualTo(1));

            File.WriteAllText(f.Output, "a mismatched file must not receive a terminal receipt");
            Assert.That(f.Write(terminal: true), Is.False);
            Assert.That(File.Exists(f.Output + ".writer.json"), Is.False);
            BlockProbeReportWriter.WriteAtomic(f.Output, terminalJson);
            Assert.That(f.Write(terminal: true), Is.True);
            var receipt = JsonUtility.FromJson<ReceiptRead>(File.ReadAllText(f.Output + ".writer.json"));
            Assert.That(receipt.session, Is.EqualTo("checkpoint-session")); Assert.That(receipt.label, Is.EqualTo("checkpoint-host"));
            Assert.That(receipt.terminalPayloadMatches, Is.True); Assert.That(receipt.writer.terminalPublished, Is.True);
            Assert.That(receipt.payloadSha256, Is.EqualTo(Hash(terminalBytes)));
            Assert.That(File.ReadAllBytes(f.Output), Is.EqualTo(terminalBytes));
        }

        [TestCase("drain", "natural", true)]
        [TestCase("freeze", "natural", true)]
        [TestCase("combat", "network", true)]
        [TestCase("combat", "blast8", true)]
        [TestCase("combat", "natural", false)]
        public void DrainNetworkAndIncompleteContextsCannotReuseACompletedRoundCheckpoint(string stage, string mode, bool completed)
        {
            using var f = new Fixture(); f.Checkpoint(); f.Idle();
            Set(f.Probe, "_stage", stage); Set(f.Report, "stage", stage); Set(f.Report, "mode", mode);
            Set(f.Report, "completedNormalRound", completed);
            Assert.That(f.Heartbeat(), Is.False);
            Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.EqualTo(1));
            Assert.That(f.Sidecar.ReadStatistics().submittedProgress, Is.Zero);
        }

        [TestCase("world")] [TestCase("epoch")] [TestCase("round")] [TestCase("revision")]
        [TestCase("surfaceRevision")] [TestCase("frozen")] [TestCase("command")] [TestCase("generation")]
        public void EveryCheckpointIdentityComponentInvalidatesRetainedReuse(string changed)
        {
            using var f = new Fixture(); f.Checkpoint(); f.Idle();
            var world = f.World;
            // Isolate individual version fields so a missing guard cannot hide behind
            // another field also changing during a normal reset or world replacement.
            switch (changed)
            {
                case "world": world = NewWorld(); Assert.That(world.Freeze(), Is.True); break;
                case "epoch": ChangeProperty(world, "Epoch", world.Epoch + 1); break;
                case "round": ChangeProperty(world.Storage, "Round", world.Round + 1); break;
                case "revision": ChangeProperty(world, "Revision", world.Revision + 1); break;
                case "surfaceRevision": Assert.That(world.Surfaces.TrySet(new BlockSurfaceKey(11, 0, 0), 2, out _), Is.True); break;
                case "frozen": ChangeProperty(world, "Frozen", false); break;
                case "command": Set(f.Probe, "_command", 2); break;
                case "generation": Set(f.Report, "connectionGenerations", 2); break;
            }
            Assert.That(f.Heartbeat(world), Is.False, changed);
            Assert.That(f.Writer.ReadStatistics().submittedProgress, Is.EqualTo(1));
            Assert.That(f.Sidecar.ReadStatistics().submittedProgress, Is.Zero);
        }

        [Test] public void NewCommandClearsTheCheckpointWhileAStaleCommandPreservesIt()
        {
            using var f = new Fixture(); f.Checkpoint(); f.Idle();
            string checkpoint = SnapshotJson(f.Probe);
            var current = typeof(PrototypeMatch).GetField("<Current>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = current.GetValue(null); current.SetValue(null, null);
            try
            {
                Call(f.Probe, "Command", 1, "drain");
                Assert.That(SnapshotJson(f.Probe), Is.EqualTo(checkpoint));
                Assert.That(Get<string>(f.Probe, "_stage"), Is.EqualTo("combat"));
                Call(f.Probe, "Command", 2, "drain");
                Assert.That(SnapshotJson(f.Probe), Is.Null);
                Assert.That(Get<string>(f.Probe, "_checkpointHash"), Is.Null);
                Assert.That(Get<long>(f.Probe, "_checkpointSequence"), Is.Zero);
                Assert.That(Get<int>(f.Probe, "_command"), Is.EqualTo(2));
                Assert.That(Get<string>(f.Probe, "_stage"), Is.EqualTo("drain"));
                Assert.That(f.Heartbeat(), Is.False);
            }
            finally { current.SetValue(null, previous); }
        }
        [TestCase("command")] [TestCase("finish")]
        public void LockedCheckpointFinalLeaseCannotAbortLifecycleAndRemainsObservableUntilRetried(string transition)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Windows delete-share behavior is required.");
            using var f = new Fixture(); f.Checkpoint(); f.Idle();
            string sealedPath = Get<BlockProbeReportFile>(f.Probe, "_checkpointFile").Path;
            using (var locked = new FileStream(sealedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (transition == "command")
                {
                    Assert.DoesNotThrow(() => Call(f.Probe, "Command", 2, "drain"));
                    Assert.That(Get<int>(f.Probe, "_command"), Is.EqualTo(2));
                    Assert.That(Get<string>(f.Probe, "_stage"), Is.EqualTo("drain")); f.Idle();
                }
                else
                {
                    Assert.DoesNotThrow(() => Call(f.Probe, "Finish"));
                    Assert.That(Get<bool>(f.Probe, "_finished"), Is.True);
                    Assert.That(Get<bool>(f.Report, "complete"), Is.True);
                }
                Assert.That(Get<BlockProbeReportFile>(f.Probe, "_checkpointFile"), Is.Null);
                Assert.That(Get<string>(f.Probe, "_checkpointHash"), Is.Null);
                Assert.That(Get<long>(f.Probe, "_checkpointSequence"), Is.Zero);
                Assert.That(Get<long>(f.Report, "checkpointFileCleanupFailures"), Is.EqualTo(1));
                Assert.That(Get<int>(f.Report, "retainedCheckpointFileCleanupCount"), Is.EqualTo(1));
                Assert.That(Get<string>(f.Report, "lastCheckpointFileCleanupError"), Does.Contain("IOException"));
                Assert.That(Get<int>(f.Report, "errors"), Is.GreaterThan(0), "Recovered IO failure remains explicit failed evidence.");
                Assert.That(File.Exists(sealedPath), Is.True);
            }
            Assert.DoesNotThrow(() => Call(f.Probe, "RetryCheckpointFileCleanup"));
            Assert.That(Get<int>(f.Report, "retainedCheckpointFileCleanupCount"), Is.Zero);
            Assert.That(File.Exists(sealedPath), Is.False);
            Assert.That(Get<long>(f.Report, "checkpointFileCleanupFailures"), Is.EqualTo(1));
            Assert.That(Get<int>(f.Report, "errors"), Is.GreaterThan(0));
        }

    }
}
#endif
