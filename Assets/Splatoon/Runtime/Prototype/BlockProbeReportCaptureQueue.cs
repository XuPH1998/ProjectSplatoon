// Reports-only candidate. Root owns installation, compilation and execution.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Splatoon.Prototype
{
    /// <summary>
    /// One encoder, one optional pending terminal, and no publisher. Submit freezes
    /// on its caller; the worker sees only that private detached graph. A normal
    /// request is rejected before freezing while any earlier job remains busy or
    /// unconsumed. Only TryTakeCompleted transfers a sealed-file lease to the owner.
    /// </summary>
    public sealed class BlockProbeReportCaptureQueue : IDisposable
    {
        public sealed class Job
        {
            public readonly long Sequence;
            public readonly bool IsTerminal;
            public readonly double FreezeMilliseconds;
            volatile bool _completed;
            internal object Payload;
            internal BlockProbeReportFile OwnedFile;
            internal string FailureValue;
            internal double CaptureMillisecondsValue;
            internal int CaptureAttemptsValue;
            internal bool Taken;

            internal Job(long sequence, bool terminal, object payload, double freezeMilliseconds)
            { Sequence = sequence; IsTerminal = terminal; Payload = payload; FreezeMilliseconds = freezeMilliseconds; }

            public bool Completed => _completed;
            public string Failure => _completed ? FailureValue : null;
            public double CaptureMilliseconds => _completed ? CaptureMillisecondsValue : 0;
            public int CaptureAttempts => Volatile.Read(ref CaptureAttemptsValue);
            // Null until TryTakeCompleted succeeds. Thereafter the caller owns this
            // exact lease and must Dispose it after any publisher has retained it.
            public BlockProbeReportFile File { get; internal set; }
            internal void Complete() => _completed = true;
            internal void BeginRetry() => _completed = false;
        }

        readonly object _gate = new();
        readonly string _prefix;
        readonly Func<object, object> _freeze;
        readonly Func<string, object, BlockProbeReportFile> _capture;
        readonly Queue<Job> _completed = new();
        readonly Queue<BlockProbeReportFile> _cleanupPending = new();
        readonly List<BlockProbeReportFile> _retiredFiles = new();
        Job _active, _pendingTerminal, _terminalRetryJob;
        bool _running, _freezing, _terminalAccepted, _closing, _closed;
        long _sequence, _failedCaptures, _cleanupFailures;
        string _lastFailure, _lastCleanupFailure;

        public BlockProbeReportCaptureQueue(string pathPrefix, Func<object, object> freeze = null,
            Func<string, object, BlockProbeReportFile> capture = null)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix)) throw new ArgumentException("A capture path prefix is required.", nameof(pathPrefix));
            _prefix = System.IO.Path.GetFullPath(pathPrefix);
            _freeze = freeze ?? BlockProbeReportSnapshot.Capture;
            _capture = capture ?? BlockProbeReportFile.Capture;
        }

        public bool IsIdle { get { lock (_gate) return !_running && !_freezing; } }
        public bool Closed { get { lock (_gate) return _closed; } }
        public int CompletedCount { get { lock (_gate) return _completed.Count; } }
        public long FailedCaptures { get { lock (_gate) return _failedCaptures; } }
        public string LastFailure { get { lock (_gate) return _lastFailure; } }
        public long CleanupFailures { get { lock (_gate) return _cleanupFailures; } }
        public int RetainedFileCleanupCount { get { lock (_gate) return _retiredFiles.Count; } }
        public string LastCleanupFailure { get { lock (_gate) return _lastCleanupFailure; } }

        /// <summary>
        /// false means no freeze and no accepted job. Freeze errors propagate on
        /// the caller with admission rolled back. Success owns the frozen graph;
        /// callers must not retain or mutate a copy returned by an injected freeze.
        /// </summary>
        public bool TrySubmit(object payload, bool terminal, out Job job)
        {
            job = null;
            lock (_gate)
            {
                if (_closing || _terminalAccepted || _freezing) return false;
                if (!terminal && (_running || _active != null || _pendingTerminal != null || _completed.Count != 0)) return false;
                // Reservation counts as the sole pending terminal while freezing.
                _freezing = true;
                if (terminal) _terminalAccepted = true;
            }

            object frozen;
            var elapsed = Stopwatch.StartNew();
            try { frozen = _freeze(payload); }
            catch
            {
                lock (_gate)
                {
                    _freezing = false;
                    if (terminal) _terminalAccepted = false;
                    UpdateClosedLocked(); Monitor.PulseAll(_gate);
                }
                throw;
            }
            elapsed.Stop();
            lock (_gate)
            {
                _freezing = false;
                // A concurrent Close can win while the caller is freezing. The
                // request never returned accepted and no worker may consume it.
                if (_closing)
                {
                    frozen = null;
                    if (terminal) _terminalAccepted = false;
                    UpdateClosedLocked(); Monitor.PulseAll(_gate); return false;
                }
                job = new Job(++_sequence, terminal, frozen, elapsed.Elapsed.TotalMilliseconds);
                if (_running) _pendingTerminal = job;
                else _active = job;
                try { StartWorkerLocked(); }
                catch
                {
                    if (ReferenceEquals(_active, job)) _active = null;
                    if (ReferenceEquals(_pendingTerminal, job)) _pendingTerminal = null;
                    job.Payload = null; job = null;
                    if (terminal) _terminalAccepted = false;
                    Monitor.PulseAll(_gate); throw;
                }
                Monitor.PulseAll(_gate); return true;
            }
        }

        /// <summary>Remove one completed job in submission order and transfer its file lease.</summary>
        public bool TryTakeCompleted(out Job job)
        {
            lock (_gate)
            {
                job = null;
                if (_closing || _completed.Count == 0) return false;
                job = _completed.Dequeue();
                job.File = job.OwnedFile; job.OwnedFile = null; job.Taken = true;
                return true;
            }
        }

        /// <summary>
        /// Explicit retry of an already consumed terminal encoding fault. The same
        /// frozen graph and sequence are reused; no freeze delegate or live input
        /// is read. The caller must take each fault before requesting another try.
        /// </summary>
        public bool RetryTerminal(Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_gate)
            {
                if (_closing || _freezing || _running || _active != null || _pendingTerminal != null || _completed.Count != 0 ||
                    !ReferenceEquals(_terminalRetryJob, job) || !job.Taken) return false;
                job.Taken = false; job.BeginRetry(); _terminalRetryJob = null; _active = job;
                try { StartWorkerLocked(); }
                catch
                {
                    _active = null; _terminalRetryJob = job; job.Taken = true; job.Complete();
                    Monitor.PulseAll(_gate); throw;
                }
                return true;
            }
        }

        void StartWorkerLocked()
        {
            if (_running) return;
            if (_active == null && !(_closing && (_completed.Count != 0 || _cleanupPending.Count != 0))) return;
            _running = true;
            try { Task.Run(Drain); }
            catch { _running = false; throw; }
        }

        void Drain()
        {
            while (true)
            {
                Job job = null;
                BlockProbeReportFile cleanup = null;
                lock (_gate)
                {
                    if (_active != null) job = _active;
                    else if (_pendingTerminal != null)
                    { _active = job = _pendingTerminal; _pendingTerminal = null; }
                    else if (_closing && _completed.Count != 0)
                    {
                        var abandoned = _completed.Dequeue();
                        cleanup = abandoned.OwnedFile; abandoned.OwnedFile = null;
                        // Failed jobs own no file; continue to drain their small record.
                        if (cleanup == null) continue;
                    }
                    else if (_closing && _cleanupPending.Count != 0) cleanup = _cleanupPending.Dequeue();
                    else
                    {
                        _running = false; UpdateClosedLocked(); Monitor.PulseAll(_gate); return;
                    }
                }
                if (cleanup != null)
                {
                    try { cleanup.Dispose(); }
                    catch (Exception failure)
                    {
                        string detail = FailureText(failure);
                        lock (_gate)
                        {
                            _retiredFiles.Add(cleanup); _cleanupFailures++; _lastCleanupFailure = detail;
                        }
                    }
                    continue;
                }

                BlockProbeReportFile file = null;
                string failureText = null;
                object frozen = job.Payload; job.Payload = null;
                Interlocked.Increment(ref job.CaptureAttemptsValue);
                var elapsed = Stopwatch.StartNew();
                try
                {
                    file = _capture(_prefix, frozen);
                    if (file == null) throw new InvalidOperationException("Report capture returned no sealed-file lease.");
                }
                catch (Exception failure) { failureText = FailureText(failure); }
                finally { elapsed.Stop(); }
                lock (_gate)
                {
                    job.OwnedFile = file; job.FailureValue = failureText;
                    job.CaptureMillisecondsValue = elapsed.Elapsed.TotalMilliseconds;
                    if (failureText != null) { _failedCaptures++; _lastFailure = failureText; }
                    if (job.IsTerminal && failureText != null && !_closing)
                    {
                        // One retry owner, never an Exception or the original live
                        // graph. Keep this exact frozen graph until retry or Close.
                        job.Payload = frozen; _terminalRetryJob = job;
                    }
                    // All fields precede the volatile completion publication.
                    job.Complete(); _completed.Enqueue(job); _active = null;
                    Monitor.PulseAll(_gate);
                }
                frozen = null;
            }
        }

        static string FailureText(Exception failure)
        {
            try { return failure.ToString(); }
            catch { return failure.GetType().FullName + ": exception text unavailable"; }
        }

        void UpdateClosedLocked()
        {
            _closed = _closing && !_running && !_freezing && _active == null && _pendingTerminal == null &&
                _terminalRetryJob == null && _completed.Count == 0 && _cleanupPending.Count == 0 && _retiredFiles.Count == 0;
        }

        /// <summary>Optional bounded owner/test wait. Runtime Update should use zero or poll IsIdle.</summary>
        public bool WaitForIdle(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var elapsed = Stopwatch.StartNew();
            lock (_gate)
            {
                while (_running || _freezing)
                {
                    int remaining = milliseconds - (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0 || !Monitor.Wait(_gate, remaining)) return !_running && !_freezing;
                }
                return true;
            }
        }

        /// <summary>
        /// Stop admission, finish accepted captures, and clean only unconsumed
        /// file leases. A timeout keeps worker data live. A later Close can retry
        /// a last-lease delete failure. A true result concerns this queue only;
        /// it is not a publisher receipt or proof that capture jobs succeeded.
        /// </summary>
        public bool Close(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var elapsed = Stopwatch.StartNew();
            lock (_gate)
            {
                _closing = true;
                if (_terminalRetryJob != null)
                {
                    // This graph belongs only to a completed failed attempt;
                    // active workers detached their payload to a local variable.
                    _terminalRetryJob.Payload = null; _terminalRetryJob = null;
                }
                foreach (var file in _retiredFiles) _cleanupPending.Enqueue(file);
                _retiredFiles.Clear();
                StartWorkerLocked();
                while (_running || _freezing)
                {
                    int remaining = milliseconds - (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0 || !Monitor.Wait(_gate, remaining)) return false;
                }
                UpdateClosedLocked(); return _closed;
            }
        }

        // Do not hide a thirty-second join in a measured/lifecycle callback.
        // Owners which need an actual drain receipt call Close with their bound.
        public void Dispose() => Close(0);
    }
}
#endif
