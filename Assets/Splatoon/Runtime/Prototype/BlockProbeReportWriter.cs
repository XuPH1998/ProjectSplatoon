#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;

namespace Splatoon.Prototype
{
    /// <summary>One disk writer, one replaceable progress report, and an ordered terminal barrier.</summary>
    public sealed class BlockProbeReportWriter : IDisposable
    {
        [Serializable] public struct Statistics
        {
            public long submittedProgress, coalescedProgress, completedWrites, failedWrites;
            public double diskElapsedMilliseconds, maximumDiskElapsedMilliseconds;
            public bool terminalAccepted, terminalPublished, closed, drainCompleted;
            public string lastError;
        }

        readonly object _gate = new();
        readonly string _path;
        readonly Action<string, string> _write;
        string _pending;
        bool _pendingTerminal, _running, _disposed, _closeFinished;
        Statistics _stats;
        static readonly ProfilerMarker DiskWriteCpu = new("Splatoon.Probe.DiskWrite");

        public BlockProbeReportWriter(string path, Action<string, string> write = null)
        {
            _path = Path.GetFullPath(path);
            _write = write ?? WriteAtomic;
        }

        public Statistics ReadStatistics() { lock (_gate) return _stats; }

        public void SubmitProgress(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BlockProbeReportWriter));
                if (_stats.terminalAccepted) return;
                _stats.submittedProgress++;
                if (_pending != null) _stats.coalescedProgress++;
                _pending = json; _pendingTerminal = false;
                StartWorker();
            }
        }

        public void SubmitTerminal(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BlockProbeReportWriter));
                if (_stats.terminalAccepted) return;
                if (_pending != null) _stats.coalescedProgress++;
                _stats.terminalAccepted = true;
                _pending = json; _pendingTerminal = true;
                StartWorker();
            }
        }

        // A failed terminal write retains the exact serialized payload for a bounded owner poll.
        public bool PollTerminal()
        {
            lock (_gate)
            {
                if (_stats.terminalPublished) return true;
                if (!_disposed && _stats.terminalAccepted) StartWorker();
                return false;
            }
        }

        void StartWorker()
        {
            if (_running || _pending == null) return;
            _running = true;
            Task.Run(Drain);
        }

        void Drain()
        {
            while (true)
            {
                string json; bool terminal;
                lock (_gate)
                {
                    if (_pending == null)
                    { _running = false; Monitor.PulseAll(_gate); return; }
                    json = _pending; terminal = _pendingTerminal;
                    _pending = null;
                }
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                Exception failure = null;
                try { using (DiskWriteCpu.Auto()) _write(_path, json); }
                catch (Exception error)
                { failure = error; }
                elapsed.Stop();
                lock (_gate)
                {
                    double milliseconds = elapsed.Elapsed.TotalMilliseconds;
                    _stats.diskElapsedMilliseconds += milliseconds;
                    _stats.maximumDiskElapsedMilliseconds = Math.Max(_stats.maximumDiskElapsedMilliseconds, milliseconds);
                    if (failure != null)
                    {
                        _stats.failedWrites++; _stats.lastError = failure.GetType().Name + ": " + failure.Message;
                        if (terminal) { _pending = json; _pendingTerminal = true; }
                        _running = false; Monitor.PulseAll(_gate); return;
                    }
                    _stats.completedWrites++;
                    if (terminal) _stats.terminalPublished = true;
                }
            }
        }

        public static void WriteAtomic(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".writing";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public bool WaitForIdle(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            lock (_gate)
            {
                while (_running)
                {
                    int remaining = milliseconds - (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds);
                    if (remaining <= 0 || !Monitor.Wait(_gate, remaining)) return !_running;
                }
                return true;
            }
        }

        public bool Close(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            lock (_gate)
            {
                if (_closeFinished) return _stats.drainCompleted;
                _disposed = _stats.closed = true;
                long failures = _stats.failedWrites, retryAt = 0;
                while (true)
                {
                    if (!_running && _pending == null)
                    {
                        _stats.drainCompleted = !_stats.terminalAccepted || _stats.terminalPublished;
                        _closeFinished = true; return _stats.drainCompleted;
                    }
                    long elapsed = timer.ElapsedMilliseconds;
                    if (elapsed >= milliseconds) { _closeFinished = true; return false; }
                    if (failures != _stats.failedWrites)
                    { failures = _stats.failedWrites; retryAt = elapsed + 100; }
                    if (!_running && _pending != null && elapsed >= retryAt) StartWorker();
                    int wait = (int)Math.Min(milliseconds - elapsed, _running ? milliseconds - elapsed : Math.Max(1, retryAt - elapsed));
                    Monitor.Wait(_gate, Math.Max(1, wait));
                }
            }
        }

        // Only owner teardown waits; measured Update never joins the disk worker.
        public void Dispose() => Close(30000);
    }
}
#endif
