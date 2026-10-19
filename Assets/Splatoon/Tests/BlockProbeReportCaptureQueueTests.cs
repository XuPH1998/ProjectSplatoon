// Reports-only candidate. No tests have been executed by this author.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class BlockProbeReportCaptureQueueTests
    {
        string _directory, _prefix;
        [SetUp] public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "Splatoon-capture-queue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory); _prefix = Path.Combine(_directory, "snapshot");
        }
        [TearDown] public void TearDown()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        [Serializable] public sealed class Payload
        { public string label; public List<int> values = new(); }

        static Payload Copy(object source)
        {
            var value = (Payload)source;
            return new Payload { label = value.label, values = new List<int>(value.values) };
        }

        static BlockProbeReportCaptureQueue.Job Take(BlockProbeReportCaptureQueue queue)
        {
            Assert.That(queue.WaitForIdle(5000), Is.True);
            Assert.That(queue.TryTakeCompleted(out var job), Is.True);
            Assert.That(job.Completed, Is.True); return job;
        }

        [Test] public void DefaultDelegatesFreezeAndCaptureACompleteDetachedDto()
        {
            using var queue = new BlockProbeReportCaptureQueue(_prefix);
            var source = new Payload { label = "default-before", values = new List<int> { 5 } };
            Assert.That(queue.TrySubmit(source, false, out _), Is.True);
            source.label = "default-after"; source.values[0] = 999;
            var job = Take(queue); Assert.That(job.Failure, Is.Null);
            Assert.That(File.ReadAllText(job.File.Path), Does.Contain("\"label\":\"default-before\""));
            Assert.That(File.ReadAllText(job.File.Path), Does.Contain("\"values\":[5]"));
            job.File.Dispose(); Assert.That(queue.Close(5000), Is.True);
        }

        [Test] public void BusyNormalIsRejectedBeforeCloneAndWorkerUsesDetachedCallerThreadSnapshot()
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int ownerThread = Thread.CurrentThread.ManagedThreadId, freezeThread = 0, captureThread = 0, clones = 0;
            var source = new Payload { label = "before", values = new List<int> { 1 } };
            var queue = new BlockProbeReportCaptureQueue(_prefix,
                value => { freezeThread = Thread.CurrentThread.ManagedThreadId; clones++; return Copy(value); },
                (prefix, value) => {
                    captureThread = Thread.CurrentThread.ManagedThreadId; entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("capture gate");
                    return BlockProbeReportFile.Capture(prefix, value);
                });
            try
            {
                Assert.That(queue.TrySubmit(source, false, out var accepted), Is.True);
                Assert.That(entered.Wait(5000), Is.True); Assert.That(accepted.File, Is.Null);
                source.label = "after"; source.values[0] = 99;
                Assert.That(queue.TrySubmit(source, false, out var rejected), Is.False);
                Assert.That(rejected, Is.Null); Assert.That(clones, Is.EqualTo(1));
                release.Set(); Assert.That(queue.WaitForIdle(5000), Is.True);
                Assert.That(queue.TrySubmit(source, false, out _), Is.False,
                    "Unconsumed completions cannot accumulate unlimited sealed files.");
                var job = Take(queue);
                Assert.That(job, Is.SameAs(accepted)); Assert.That(job.Failure, Is.Null);
                Assert.That(freezeThread, Is.EqualTo(ownerThread)); Assert.That(captureThread, Is.Not.EqualTo(ownerThread));
                Assert.That(job.CaptureMilliseconds, Is.GreaterThan(0)); Assert.That(job.FreezeMilliseconds, Is.GreaterThanOrEqualTo(0));
                string path = job.File.Path;
                Assert.That(File.ReadAllText(path), Does.Contain("\"label\":\"before\""));
                Assert.That(File.ReadAllText(path), Does.Contain("\"values\":[1]"));
                Assert.That(queue.Close(5000), Is.True);
                Assert.That(File.Exists(path), Is.True, "Taken file ownership belongs exclusively to the caller.");
                job.File.Dispose(); Assert.That(File.Exists(path), Is.False);
            }
            finally { release.Set(); queue.Close(5000); }
        }

        [Test] public void OneTerminalMayWaitBehindActiveAndIsFrozenOnceBeforeLaterProgressIsRefused()
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int active = 0, maximumActive = 0, clones = 0;
            var queue = new BlockProbeReportCaptureQueue(_prefix, value => { clones++; return Copy(value); },
                (prefix, value) => {
                    int count = Interlocked.Increment(ref active); maximumActive = Math.Max(maximumActive, count);
                    try {
                        if (((Payload)value).label == "first") {
                            entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("active gate");
                        }
                        return BlockProbeReportFile.Capture(prefix, value);
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
            try
            {
                Assert.That(queue.TrySubmit(new Payload { label = "first" }, false, out var first), Is.True);
                Assert.That(entered.Wait(5000), Is.True);
                var terminalSource = new Payload { label = "terminal", values = new List<int> { 2 } };
                Assert.That(queue.TrySubmit(terminalSource, true, out var terminal), Is.True);
                terminalSource.values[0] = 999;
                Assert.That(queue.TrySubmit(terminalSource, true, out _), Is.False);
                Assert.That(queue.TrySubmit(terminalSource, false, out _), Is.False); Assert.That(clones, Is.EqualTo(2));
                release.Set(); Assert.That(queue.WaitForIdle(5000), Is.True);
                var completedFirst = Take(queue); var completedTerminal = Take(queue);
                Assert.That(completedFirst, Is.SameAs(first)); Assert.That(completedTerminal, Is.SameAs(terminal));
                Assert.That(first.Sequence, Is.LessThan(terminal.Sequence)); Assert.That(terminal.IsTerminal, Is.True);
                Assert.That(maximumActive, Is.EqualTo(1));
                Assert.That(File.ReadAllText(terminal.File.Path), Does.Contain("\"values\":[2]"));
                Assert.That(queue.TryTakeCompleted(out _), Is.False);
                first.File.Dispose(); terminal.File.Dispose(); Assert.That(queue.Close(5000), Is.True);
            }
            finally { release.Set(); queue.Close(5000); }
        }

        [Test] public void FailedTerminalFreezeRollsBackAdmissionWithoutTouchingActiveCapture()
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            bool failFreeze = false;
            var queue = new BlockProbeReportCaptureQueue(_prefix,
                value => { if (failFreeze) throw new InvalidOperationException("freeze-fault"); return Copy(value); },
                (prefix, value) => {
                    if (((Payload)value).label == "active") {
                        entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("active gate");
                    }
                    return BlockProbeReportFile.Capture(prefix, value);
                });
            try
            {
                Assert.That(queue.TrySubmit(new Payload { label = "active" }, false, out var first), Is.True);
                Assert.That(entered.Wait(5000), Is.True); failFreeze = true;
                Assert.Throws<InvalidOperationException>(() => queue.TrySubmit(new Payload(), true, out _));
                Assert.That(first.Completed, Is.False); failFreeze = false;
                Assert.That(queue.TrySubmit(new Payload { label = "terminal" }, true, out var terminal), Is.True);
                release.Set(); Assert.That(queue.WaitForIdle(5000), Is.True);
                Assert.That(Take(queue), Is.SameAs(first)); Assert.That(Take(queue), Is.SameAs(terminal));
                first.File.Dispose(); terminal.File.Dispose(); Assert.That(queue.Close(5000), Is.True);
            }
            finally { release.Set(); queue.Close(5000); }
        }

        [Test] public void CaptureFailureProducesOneOrderedFaultAndNeverRecapturesLiveInput()
        {
            int captures = 0;
            using var queue = new BlockProbeReportCaptureQueue(_prefix, Copy,
                (_, _) => { Interlocked.Increment(ref captures); throw new InvalidDataException("encode-fault"); });
            Assert.That(queue.TrySubmit(new Payload { label = "fault" }, false, out var accepted), Is.True);
            var job = Take(queue);
            Assert.That(job, Is.SameAs(accepted)); Assert.That(job.File, Is.Null);
            Assert.That(job.Failure, Does.Contain("encode-fault")); Assert.That(queue.FailedCaptures, Is.EqualTo(1));
            Assert.That(queue.LastFailure, Is.EqualTo(job.Failure)); Assert.That(captures, Is.EqualTo(1));
            Assert.That(queue.TryTakeCompleted(out _), Is.False); Assert.That(queue.Close(5000), Is.True);
            Assert.That(captures, Is.EqualTo(1)); Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test] public void TimedOutClosePreservesActiveAndPendingSnapshotsThenCleansUnconsumedFiles()
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int captures = 0;
            var queue = new BlockProbeReportCaptureQueue(_prefix, Copy,
                (prefix, value) => {
                    if (Interlocked.Increment(ref captures) == 1) {
                        entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("close gate");
                    }
                    Assert.That(((Payload)value).values, Is.EqualTo(new[] { 42 }), "Close cannot clear a graph still used by the worker.");
                    return BlockProbeReportFile.Capture(prefix, value);
                });
            try
            {
                Assert.That(queue.TrySubmit(new Payload { values = new List<int> { 42 } }, false, out var first), Is.True);
                Assert.That(entered.Wait(5000), Is.True);
                Assert.That(queue.TrySubmit(new Payload { values = new List<int> { 42 } }, true, out var terminal), Is.True);
                Assert.That(queue.Close(0), Is.False); Assert.That(first.Completed, Is.False); Assert.That(terminal.Completed, Is.False);
                Assert.That(queue.TrySubmit(new Payload(), false, out _), Is.False);
                release.Set(); Assert.That(queue.Close(5000), Is.True);
                Assert.That(first.Completed, Is.True); Assert.That(terminal.Completed, Is.True); Assert.That(captures, Is.EqualTo(2));
                Assert.That(first.Failure, Is.Null); Assert.That(terminal.Failure, Is.Null);
                Assert.That(first.File, Is.Null); Assert.That(terminal.File, Is.Null);
                Assert.That(queue.TryTakeCompleted(out _), Is.False); Assert.That(queue.Closed, Is.True);
                Assert.That(Directory.GetFiles(_directory), Is.Empty);
            }
            finally { release.Set(); queue.Close(5000); }
        }

        [Test] public void TerminalEncodingRetryUsesTheSameFrozenGraphAndSequenceWithoutReadingChangedLiveReport()
        {
            int freezes = 0, captures = 0; object firstFrozen = null;
            using var queue = new BlockProbeReportCaptureQueue(_prefix,
                value => { freezes++; return Copy(value); },
                (prefix, value) => {
                    if (Interlocked.Increment(ref captures) == 1) {
                        firstFrozen = value; throw new InvalidDataException("first-terminal-encode-fault");
                    }
                    if (!ReferenceEquals(firstFrozen, value)) throw new InvalidOperationException("Terminal graph identity changed on retry.");
                    return BlockProbeReportFile.Capture(prefix, value);
                });
            var live = new Payload { label = "cutoff", values = new List<int> { 17 } };
            Assert.That(queue.TrySubmit(live, true, out var accepted), Is.True);
            Assert.That(queue.WaitForIdle(5000), Is.True);
            Assert.That(queue.RetryTerminal(accepted), Is.False, "The owner must consume the explicit fault first.");
            var failed = Take(queue); long sequence = failed.Sequence; double freezeMilliseconds = failed.FreezeMilliseconds;
            Assert.That(failed.Failure, Does.Contain("first-terminal-encode-fault")); Assert.That(failed.File, Is.Null);
            Assert.That(failed.CaptureAttempts, Is.EqualTo(1));
            live.label = "changed-after-cutoff"; live.values[0] = 999;
            Assert.That(queue.TrySubmit(live, true, out _), Is.False); Assert.That(queue.TrySubmit(live, false, out _), Is.False);
            Assert.That(queue.RetryTerminal(failed), Is.True);
            var recovered = Take(queue);
            Assert.That(recovered, Is.SameAs(accepted)); Assert.That(recovered.Sequence, Is.EqualTo(sequence));
            Assert.That(recovered.FreezeMilliseconds, Is.EqualTo(freezeMilliseconds));
            Assert.That(recovered.CaptureAttempts, Is.EqualTo(2)); Assert.That(recovered.Failure, Is.Null);
            Assert.That(freezes, Is.EqualTo(1)); Assert.That(captures, Is.EqualTo(2)); Assert.That(queue.FailedCaptures, Is.EqualTo(1));
            Assert.That(File.ReadAllText(recovered.File.Path), Does.Contain("\"label\":\"cutoff\""));
            Assert.That(File.ReadAllText(recovered.File.Path), Does.Contain("\"values\":[17]"));
            Assert.That(queue.RetryTerminal(recovered), Is.False); recovered.File.Dispose(); Assert.That(queue.Close(5000), Is.True);
        }

        [Test] public void UnconsumedFileDeleteFailureIsObservableAndALaterCloseRetriesTheSameLease()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Requires Windows delete-share semantics.");
            string sealedPath = null;
            using var queue = new BlockProbeReportCaptureQueue(_prefix, Copy,
                (prefix, value) => {
                    var file = BlockProbeReportFile.Capture(prefix, value); sealedPath = file.Path; return file;
                });
            Assert.That(queue.TrySubmit(new Payload { label = "locked" }, false, out var job), Is.True);
            Assert.That(queue.WaitForIdle(5000), Is.True); Assert.That(job.Failure, Is.Null);
            using (var locked = new FileStream(sealedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(queue.Close(5000), Is.False);
                Assert.That(queue.Closed, Is.False); Assert.That(queue.RetainedFileCleanupCount, Is.EqualTo(1));
                Assert.That(queue.CleanupFailures, Is.EqualTo(1));
                Assert.That(queue.LastCleanupFailure, Does.Contain("IOException")); Assert.That(File.Exists(sealedPath), Is.True);
            }
            Assert.That(queue.Close(5000), Is.True); Assert.That(queue.Closed, Is.True);
            Assert.That(queue.RetainedFileCleanupCount, Is.Zero); Assert.That(queue.CleanupFailures, Is.EqualTo(1));
            Assert.That(File.Exists(sealedPath), Is.False); Assert.That(queue.TryTakeCompleted(out _), Is.False);
        }
    }
}
#endif
