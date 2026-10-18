#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class BlockProbeCommandReaderTests
    {
        string _directory;
        [SetUp] public void Setup()
        {
            _directory = Path.Combine(Path.GetTempPath(), "Splatoon-command-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }
        [TearDown] public void Cleanup()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        static void Wait(Func<bool> condition, string message)
        { Assert.That(SpinWait.SpinUntil(condition, 5000), Is.True, message); }
        static void Stop(BlockProbeCommandReader reader)
        {
            reader.Dispose();
            Assert.That(reader.Completion.Wait(5000), Is.True, "The released background reader must stop.");
            Assert.That(reader.ReadStatistics().stopped, Is.True);
        }

        [Test] public void BlockedNativeReadNeverBlocksMainConsumptionStatisticsOrCloseAndDiscardsLateResult()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int mainThread = Thread.CurrentThread.ManagedThreadId, readThread = mainThread;
            var reader = new BlockProbeCommandReader(Path.Combine(_directory, "control.txt"), read: _ => {
                readThread = Thread.CurrentThread.ManagedThreadId; entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("The test must release the native read.");
                return new[] { "1", "combat" };
            }, pollMilliseconds: 5);
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Assert.That(reader.TryTake(out _), Is.False);
                Assert.That(reader.ReadStatistics().readInFlight, Is.True);
                reader.Dispose();
                Assert.That(timer.ElapsedMilliseconds, Is.LessThan(250), "No main-thread disk read, lock around disk IO, or join is allowed.");
                Assert.That(readThread, Is.Not.EqualTo(mainThread));
                Assert.That(reader.Completion.IsCompleted, Is.False, "Cancellation must not claim to cancel entered OS IO.");
                Assert.That(reader.ReadStatistics().closeRequested, Is.True);
                Assert.That(reader.ReadStatistics().stopped, Is.False);
                release.Set(); Stop(reader);
                Assert.That(reader.TryTake(out _), Is.False);
                var stats = reader.ReadStatistics();
                Assert.That(stats.readsStarted, Is.EqualTo(1));
                Assert.That(stats.completedAfterClose, Is.EqualTo(1));
                Assert.That(stats.commandsPublished, Is.Zero);
                Assert.That(stats.pending || stats.readInFlight, Is.False);
            }
            finally { release.Set(); Stop(reader); }
        }

        [Test] public void SingleSlotBackpressureKeepsObservedOrderAndRejectsDuplicateAndOlderIntegers()
        {
            int reads = 0;
            var reader = new BlockProbeCommandReader(Path.Combine(_directory, "control.txt"), read: _ => {
                return Interlocked.Increment(ref reads) switch
                {
                    1 => new[] { "1", "seed" },
                    2 => new[] { "3", "finish" },
                    3 => new[] { "2", "old must not apply" },
                    4 => new[] { "3", "duplicate must not apply" },
                    _ => new[] { "4", "combat" }
                };
            }, pollMilliseconds: 5);
            try
            {
                Wait(() => reader.ReadStatistics().pending, "First observed command");
                Thread.Sleep(25);
                Assert.That(reader.ReadStatistics().readsStarted, Is.EqualTo(1), "A pending command must not be overwritten or grow a queue.");
                Assert.That(reader.TryTake(out var first), Is.True);
                Assert.That(first.Sequence, Is.EqualTo(1)); Assert.That(first.Stage, Is.EqualTo("seed"));
                Wait(() => reader.ReadStatistics().pending, "Next command after consumption");
                Assert.That(reader.TryTake(out var second), Is.True);
                Assert.That(second.Sequence, Is.EqualTo(3)); Assert.That(second.Stage, Is.EqualTo("finish"));
                Wait(() => reader.ReadStatistics().pending, "A newer command after duplicate and older text");
                Assert.That(reader.TryTake(out var third), Is.True);
                Assert.That(third.Sequence, Is.EqualTo(4)); Assert.That(third.Stage, Is.EqualTo("combat"));
                var stats = reader.ReadStatistics();
                Assert.That(stats.commandsPublished, Is.EqualTo(3));
                Assert.That(stats.commandsConsumed, Is.EqualTo(3));
                Assert.That(stats.staleReads, Is.GreaterThanOrEqualTo(2));
            }
            finally { Stop(reader); }
        }

        [Test] public void RealFileMissingPartialInvalidAndUtf8TextRetainTheOriginalTwoLineParsingRules()
        {
            string path = Path.Combine(_directory, "control.txt");
            var reader = new BlockProbeCommandReader(path, pollMilliseconds: 5);
            try
            {
                Wait(() => reader.ReadStatistics().missingReads > 0, "Missing file remains retryable.");
                File.WriteAllText(path, "1");
                Wait(() => reader.ReadStatistics().invalidReads > 0, "Partial one-line control is not a command.");
                long invalid = reader.ReadStatistics().invalidReads;
                File.WriteAllText(path, "2147483648\nignored");
                Wait(() => reader.ReadStatistics().invalidReads > invalid, "Overflow remains invalid int text.");
                Assert.That(reader.TryTake(out _), Is.False);
                File.WriteAllText(path, "  +1  \n  阶段测试  \nignored third line", new UTF8Encoding(true));
                Wait(() => reader.ReadStatistics().pending, "UTF8 BOM, integer whitespace/sign and trimmed second line");
                Assert.That(reader.TryTake(out var command), Is.True);
                Assert.That(command.Sequence, Is.EqualTo(1)); Assert.That(command.Stage, Is.EqualTo("阶段测试"));
                File.WriteAllText(path, "2\n   \nignored");
                Wait(() => reader.ReadStatistics().pending, "An empty trimmed stage is still a valid original command.");
                Assert.That(reader.TryTake(out var empty), Is.True);
                Assert.That(empty.Sequence, Is.EqualTo(2)); Assert.That(empty.Stage, Is.Empty);
                Assert.That(reader.ReadStatistics().commandsConsumed, Is.EqualTo(2));
            }
            finally { Stop(reader); }
        }

        [Test] public void RealSharingFailureIsReportedAndTheSameReaderRecoversAfterTheHandleIsReleased()
        {
            string path = Path.Combine(_directory, "control.txt");
            File.WriteAllText(path, "1\nseed");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var reader = new BlockProbeCommandReader(path, pollMilliseconds: 5);
            try
            {
                Wait(() => reader.ReadStatistics().failedReads > 0, "Real filesystem sharing failure");
                Assert.That(reader.TryTake(out _), Is.False);
                Assert.That(reader.ReadStatistics().lastError, Is.Not.Empty);
                exclusive.Dispose();
                Wait(() => reader.ReadStatistics().pending, "The existing poller retries the released file.");
                Assert.That(reader.TryTake(out var command), Is.True);
                Assert.That(command.Sequence, Is.EqualTo(1)); Assert.That(command.Stage, Is.EqualTo("seed"));
                Assert.That(reader.ReadStatistics().commandsPublished, Is.EqualTo(1));
            }
            finally { exclusive.Dispose(); Stop(reader); }
        }

        [Test] public void ReadFailureRetriesWithoutPublishingDefaultOrReplacingTheLastAppliedCommand()
        {
            int reads = 0;
            using var failed = new ManualResetEventSlim();
            using var recover = new ManualResetEventSlim();
            var reader = new BlockProbeCommandReader(Path.Combine(_directory, "control.txt"), read: _ => {
                if (Interlocked.Increment(ref reads) == 1) return new[] { "7", "seed" };
                if (!recover.IsSet) { failed.Set(); throw new IOException("Injected sharing failure"); }
                return new[] { "8", "combat" };
            }, pollMilliseconds: 5);
            try
            {
                Wait(() => reader.ReadStatistics().pending, "Initial command");
                Assert.That(reader.TryTake(out var applied), Is.True);
                Assert.That(failed.Wait(5000), Is.True);
                Wait(() => reader.ReadStatistics().failedReads > 0, "Failure telemetry is recorded independently.");
                Assert.That(reader.TryTake(out _), Is.False);
                Assert.That(applied.Sequence, Is.EqualTo(7)); Assert.That(applied.Stage, Is.EqualTo("seed"));
                Assert.That(reader.ReadStatistics().lastError, Does.Contain("IOException: Injected sharing failure"));
                recover.Set();
                Wait(() => reader.ReadStatistics().pending, "The same reader retries after recovery.");
                Assert.That(reader.TryTake(out applied), Is.True);
                Assert.That(applied.Sequence, Is.EqualTo(8)); Assert.That(applied.Stage, Is.EqualTo("combat"));
                Assert.That(reader.ReadStatistics().commandsPublished, Is.EqualTo(2));
            }
            finally { recover.Set(); Stop(reader); }
        }

        [Test] public void DisposeDropsAnUnconsumedSlotAndAfterSequenceDoesNotReplayPriorControl()
        {
            int sequence = 9;
            var reader = new BlockProbeCommandReader(Path.Combine(_directory, "control.txt"), afterSequence: 9,
                read: _ => new[] { Volatile.Read(ref sequence).ToString(), "quit" }, pollMilliseconds: 5);
            try
            {
                Wait(() => reader.ReadStatistics().staleReads > 0, "The installed owner sequence remains the lower bound.");
                Assert.That(reader.TryTake(out _), Is.False);
                Volatile.Write(ref sequence, 10);
                Wait(() => reader.ReadStatistics().pending, "A newer command reaches the slot.");
                Stop(reader);
                Assert.That(reader.TryTake(out _), Is.False, "Closed owners must not execute a pending stage.");
                Assert.That(reader.ReadStatistics().commandsConsumed, Is.Zero);
            }
            finally { Stop(reader); }
        }

        [Test] public void ProbeReadCommandConsumesMemoryOnlyWhileTheWorkerIsBlockedAndTeardownDoesNotJoin()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reader = new BlockProbeCommandReader(Path.Combine(_directory, "control.txt"), read: _ => {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Test native read gate");
                return new[] { "13", "combat" };
            }, pollMilliseconds: 5);
            var root = new UnityEngine.GameObject("Background control read contract"); root.SetActive(false);
            try
            {
                var probe = root.AddComponent<BlockDynamicAcceptanceProbe>();
                var type = typeof(BlockDynamicAcceptanceProbe);
                var report = Activator.CreateInstance(type.GetNestedType("Report", BindingFlags.NonPublic), true);
                type.GetField("_report", flags).SetValue(probe, report);
                type.GetField("_commandReader", flags).SetValue(probe, reader);
                type.GetField("_command", flags).SetValue(probe, 12);
                type.GetField("_stage", flags).SetValue(probe, "previous");
                Assert.That(entered.Wait(5000), Is.True);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                type.GetMethod("ReadCommand", flags).Invoke(probe, null);
                type.GetMethod("CloseCommandReader", flags).Invoke(probe, null);
                Assert.That(timer.ElapsedMilliseconds, Is.LessThan(250));
                Assert.That(type.GetField("_command", flags).GetValue(probe), Is.EqualTo(12));
                Assert.That(type.GetField("_stage", flags).GetValue(probe), Is.EqualTo("previous"));
                Assert.That(reader.Completion.IsCompleted, Is.False);
                release.Set(); Stop(reader);
                type.GetMethod("ReadCommand", flags).Invoke(probe, null);
                Assert.That(type.GetField("_command", flags).GetValue(probe), Is.EqualTo(12));
            }
            finally { release.Set(); Stop(reader); UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
#endif
