#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;

namespace Splatoon.Prototype
{
    /// <summary>One background file poller and one ordered, non-replaceable command slot.</summary>
    public sealed class BlockProbeCommandReader : IDisposable
    {
        public const int PollMilliseconds = 100;

        public readonly struct Command
        {
            public readonly int Sequence;
            public readonly string Stage;
            public Command(int sequence, string stage) { Sequence = sequence; Stage = stage; }
        }

        [Serializable] public struct Statistics
        {
            public long readsStarted, readsSucceeded, missingReads, failedReads, invalidReads, staleReads;
            public long commandsPublished, commandsConsumed, completedAfterClose;
            public double readElapsedMilliseconds, maximumReadElapsedMilliseconds;
            public bool pending, readInFlight, closeRequested, stopped;
            public string lastError;
        }

        static readonly ProfilerMarker FileReadCpu = new("Splatoon.Probe.CommandFileRead");
        readonly object _gate = new();
        readonly string _path;
        readonly Func<string, string[]> _read;
        readonly int _pollMilliseconds;
        readonly CancellationTokenSource _stop = new();
        Statistics _stats;
        Command _pending;
        int _lastPublishedSequence;

        // Completion is for owner diagnostics/tests. Production consumption and
        // teardown never join a potentially blocked native filesystem operation.
        public Task Completion { get; }

        public BlockProbeCommandReader(string path, int afterSequence = 0,
            Func<string, string[]> read = null, int pollMilliseconds = PollMilliseconds)
        {
            if (pollMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(pollMilliseconds));
            _path = Path.GetFullPath(path);
            _lastPublishedSequence = afterSequence;
            _read = read ?? File.ReadAllLines;
            _pollMilliseconds = pollMilliseconds;
            Completion = Task.Run(Poll);
        }

        public Statistics ReadStatistics() { lock (_gate) return _stats; }

        public bool TryTake(out Command command)
        {
            lock (_gate)
            {
                command = default;
                if (_stats.closeRequested || !_stats.pending) return false;
                command = _pending; _pending = default; _stats.pending = false;
                _stats.commandsConsumed++;
                return true;
            }
        }

        async Task Poll()
        {
            try
            {
                while (true)
                {
                    bool read;
                    lock (_gate)
                    {
                        if (_stats.closeRequested) return;
                        // Backpressure preserves an observed command until the
                        // main thread consumes it; no replacement or growing queue.
                        read = !_stats.pending;
                    }
                    if (read) ReadOnce();
                    try { await Task.Delay(_pollMilliseconds, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            finally
            {
                lock (_gate) _stats.stopped = true;
                _stop.Dispose();
            }
        }

        void ReadOnce()
        {
            lock (_gate)
            {
                if (_stats.closeRequested) return;
                _stats.readsStarted++; _stats.readInFlight = true;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string[] lines = null;
            string error = null;
            bool missing = false;
            try { using (FileReadCpu.Auto()) lines = _read(_path); }
            catch (FileNotFoundException) { missing = true; }
            catch (DirectoryNotFoundException) { missing = true; }
            catch (Exception failure)
            {
                error = failure.GetType().Name + ": " + failure.Message;
                if (error.Length > 1024) error = error.Substring(0, 1024);
            }
            timer.Stop();
            int sequence = 0;
            bool valid = lines != null && lines.Length >= 2 && int.TryParse(lines[0], out sequence);
            string stage = valid ? lines[1].Trim() : null;
            lock (_gate)
            {
                _stats.readInFlight = false;
                double milliseconds = timer.Elapsed.TotalMilliseconds;
                _stats.readElapsedMilliseconds += milliseconds;
                _stats.maximumReadElapsedMilliseconds = Math.Max(_stats.maximumReadElapsedMilliseconds, milliseconds);
                if (missing) _stats.missingReads++;
                else if (error != null) { _stats.failedReads++; _stats.lastError = error; }
                else
                {
                    _stats.readsSucceeded++;
                    if (!valid) _stats.invalidReads++;
                    else if (sequence <= _lastPublishedSequence) _stats.staleReads++;
                }
                if (_stats.closeRequested) { _stats.completedAfterClose++; return; }
                if (missing || error != null || !valid || sequence <= _lastPublishedSequence) return;
                _lastPublishedSequence = sequence;
                _pending = new Command(sequence, stage); _stats.pending = true; _stats.commandsPublished++;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stats.closeRequested) return;
                _stats.closeRequested = true; _stats.pending = false; _pending = default;
            }
            // An already-entered OS read is not cancellable. Its result is
            // discarded after it returns, without blocking Unity's main thread.
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
#endif
