#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
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
        [Serializable] public struct AtomicFailure
        {
            public string utc, stage, path, temporaryPath, exceptionType, exception, targetStateError, temporaryStateError;
            public int attempt, hresult, hresultLow16, nativeError, workerThread;
            public bool targetExistsObserved, temporaryExistsObserved;
            public long targetBytesObserved, temporaryBytesObserved;
        }

        [Serializable] public struct Statistics
        {
            public long submittedProgress, coalescedProgress, completedWrites, failedWrites, lastPublishedProgressSequence;
            public double diskElapsedMilliseconds, maximumDiskElapsedMilliseconds;
            public bool terminalAccepted, terminalPublished, closed, drainCompleted;
            public string lastError;
            // Logical failures retain failedWrites; every default-publisher attempt is separately visible.
            public long atomicWriteAttempts, atomicAttemptFailures, atomicWriteRetries, recoveredAtomicWrites;
            public int lastAtomicAttemptCount;
            public AtomicFailure lastAtomicFailure;
            public string lastAtomicFailureTrace;
            public long fileCleanupFailures;
            public int retainedFileCleanupCount;
            public string lastFileCleanupError;
        }

        readonly object _gate = new();
        readonly string _path;
        readonly Action<string, string> _write;
        readonly Action<string, string> _copyFile;
        readonly bool _usesDefaultAtomic;
        sealed class Payload : IDisposable
        {
            public readonly string Json;
            public readonly BlockProbeReportFile File;
            public Payload(string json) { Json = json; }
            public Payload(BlockProbeReportFile file) { File = file.Retain(); }
            public void Dispose() => File?.Dispose();
        }
        Payload _pending;
        readonly List<Payload> _retiredFiles = new();
        long _pendingProgressSequence;
        bool _pendingTerminal, _running, _disposed, _closeFinished;
        Statistics _stats;
        static readonly ProfilerMarker DiskWriteCpu = new("Splatoon.Probe.DiskWrite");

        public BlockProbeReportWriter(string path, Action<string, string> write = null, Action<string, string> copyFile = null)
        {
            _path = Path.GetFullPath(path);
            _write = write ?? WriteAtomic;
            _copyFile = copyFile;
            _usesDefaultAtomic = write == null;
        }

        public Statistics ReadStatistics() { lock (_gate) return _stats; }

        public long SubmitProgress(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BlockProbeReportWriter));
                if (_stats.terminalAccepted) return 0;
                _stats.submittedProgress++;
                if (_pending != null) { _stats.coalescedProgress++; ReleasePayload(_pending); }
                _pending = new Payload(json); _pendingTerminal = false;
                _pendingProgressSequence = _stats.submittedProgress;
                StartWorker();
                return _pendingProgressSequence;
            }
        }

        public void SubmitTerminal(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BlockProbeReportWriter));
                if (_stats.terminalAccepted) return;
                if (_pending != null) { _stats.coalescedProgress++; ReleasePayload(_pending); }
                _stats.terminalAccepted = true;
                _pending = new Payload(json); _pendingTerminal = true;
                StartWorker();
            }
        }

        public long SubmitProgress(BlockProbeReportFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BlockProbeReportWriter));
                if (_stats.terminalAccepted) return 0;
                // Retain can reject a disposed lease; do not mutate accepted queue state before it succeeds.
                var retained = new Payload(file);
                _stats.submittedProgress++;
                if (_pending != null) { _stats.coalescedProgress++; ReleasePayload(_pending); }
                _pending = retained; _pendingTerminal = false;
                _pendingProgressSequence = _stats.submittedProgress;
                StartWorker();
                return _pendingProgressSequence;
            }
        }

        public void SubmitTerminal(BlockProbeReportFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BlockProbeReportWriter));
                if (_stats.terminalAccepted) return;
                // Retain can reject a disposed lease; do not mutate accepted queue state before it succeeds.
                var retained = new Payload(file);
                if (_pending != null) { _stats.coalescedProgress++; ReleasePayload(_pending); }
                _stats.terminalAccepted = true;
                _pending = retained; _pendingTerminal = true;
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
                Payload payload; bool terminal; long progressSequence;
                lock (_gate)
                {
                    if (_pending == null)
                    { _running = false; Monitor.PulseAll(_gate); return; }
                    payload = _pending; terminal = _pendingTerminal;
                    progressSequence = _pendingProgressSequence;
                    _pending = null;
                }
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                Exception failure = null;
                try { using (DiskWriteCpu.Auto()) WriteOnWorker(payload); }
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
                        _stats.failedWrites++; _stats.lastError = ExceptionText(failure);
                        if (terminal) { _pending = payload; _pendingTerminal = true; }
                        else ReleasePayload(payload);
                        _running = false; Monitor.PulseAll(_gate); return;
                    }
                    _stats.completedWrites++;
                    ReleasePayload(payload);
                    if (terminal) _stats.terminalPublished = true;
                    else _stats.lastPublishedProgressSequence = progressSequence;
                }
            }
        }

        // A cleanup error must not strand _running or turn a successfully published
        // terminal into an unobservable worker exception. Keep its live lease for
        // the bounded teardown retry and expose the failure separately from IO.
        void ReleasePayload(Payload payload)
        {
            try { payload.Dispose(); }
            catch (Exception error)
            {
                _stats.fileCleanupFailures++;
                _stats.lastFileCleanupError = ExceptionText(error);
                _retiredFiles.Add(payload); _stats.retainedFileCleanupCount = _retiredFiles.Count;
            }
        }

        void RetryFileCleanup()
        {
            for (int i = _retiredFiles.Count - 1; i >= 0; i--)
            {
                try { _retiredFiles[i].Dispose(); _retiredFiles.RemoveAt(i); }
                catch (Exception error)
                { _stats.fileCleanupFailures++; _stats.lastFileCleanupError = ExceptionText(error); }
            }
            _stats.retainedFileCleanupCount = _retiredFiles.Count;
        }

        // Retry only the default filesystem publisher, on this background worker.
        // An injected owner/test writer retains its original one-call contract.
        void WriteOnWorker(Payload payload)
        {
            if (payload.File != null && _copyFile != null) { _copyFile(_path, payload.File.Path); return; }
            if (payload.File == null && !_usesDefaultAtomic) { _write(_path, payload.Json); return; }
            const int maximumAttempts = 4;
            string failureTrace = null;
            for (int attempt = 1; ; attempt++)
            {
                lock (_gate)
                { _stats.atomicWriteAttempts++; _stats.lastAtomicAttemptCount = attempt; }
                string stage = "directory";
                try
                {
                    if (payload.File != null) WriteAtomicFileOnce(_path, payload.File.Path, ref stage);
                    else WriteAtomicOnce(_path, payload.Json, ref stage);
                    if (attempt > 1) lock (_gate) _stats.recoveredAtomicWrites++;
                    return;
                }
                catch (Exception error)
                {
                    var detail = DescribeAtomicFailure(_path, stage, attempt, error);
                    failureTrace += (failureTrace == null ? "" : "\n") + "attempt=" + attempt + " stage=" + stage +
                        " hresult=" + detail.hresult + " low16=" + detail.hresultLow16 + "\n" + detail.exception;
                    bool retry = error is IOException && (stage == "replace" || stage == "move") && attempt < maximumAttempts;
                    lock (_gate)
                    {
                        _stats.atomicAttemptFailures++; _stats.lastAtomicFailure = detail;
                        _stats.lastAtomicFailureTrace = failureTrace;
                        _stats.lastError = detail.exception;
                        if (retry) _stats.atomicWriteRetries++;
                    }
                    if (!retry) throw;
                    // 170ms total deliberate backoff; an OS call itself has no timeout guarantee.
                    Thread.Sleep(attempt == 1 ? 20 : attempt == 2 ? 50 : 100);
                }
            }
        }

        public static void WriteAtomic(string path, string json)
        {
            string stage = "directory";
            WriteAtomicOnce(path, json, ref stage);
        }

        public static void WriteAtomicFile(string path, string source)
        {
            string stage = "directory";
            WriteAtomicFileOnce(path, source, ref stage);
        }

        static void WriteAtomicFileOnce(string path, string source, ref string stage)
        {
            stage = "directory";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".writing";
            // The producer owns an immutable file, never a live mutable Report.
            // Copying avoids recreating either a full UTF-16 string or byte array.
            stage = "write-temporary";
            File.Copy(source, temporary, true);
            stage = "target-exists";
            if (File.Exists(path)) { stage = "replace"; File.Replace(temporary, path, null); }
            else { stage = "move"; File.Move(temporary, path); }
        }

        static void WriteAtomicOnce(string path, string json, ref string stage)
        {
            stage = "directory";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".writing";
            stage = "write-temporary";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            stage = "target-exists";
            if (File.Exists(path)) { stage = "replace"; File.Replace(temporary, path, null); }
            else { stage = "move"; File.Move(temporary, path); }
        }

        static AtomicFailure DescribeAtomicFailure(string path, string stage, int attempt, Exception error)
        {
            var detail = new AtomicFailure { utc = DateTime.UtcNow.ToString("O"), stage = stage, attempt = attempt,
                path = path, temporaryPath = path + ".writing", exceptionType = error.GetType().FullName,
                exception = ExceptionText(error), hresult = error.HResult, hresultLow16 = error.HResult & 65535,
                workerThread = Thread.CurrentThread.ManagedThreadId };
            // HResult low bits are recorded as such; never present a stale Marshal error as native evidence.
            if (error.GetBaseException() is System.ComponentModel.Win32Exception native) detail.nativeError = native.NativeErrorCode;
            detail.targetStateError = InspectAtomicFile(path, out detail.targetExistsObserved, out detail.targetBytesObserved);
            detail.temporaryStateError = InspectAtomicFile(detail.temporaryPath, out detail.temporaryExistsObserved, out detail.temporaryBytesObserved);
            return detail;
        }

        static string InspectAtomicFile(string path, out bool exists, out long bytes)
        {
            exists = false; bytes = -1;
            try
            {
                var info = new FileInfo(path); exists = info.Exists;
                if (exists) bytes = info.Length;
                return null;
            }
            catch (Exception error) { return ExceptionText(error); }
        }

        // Diagnostic representation: [utf16-escaped-v1] followed by the complete exception text.
        // Double literal backslashes; encode controls/surrogates as literal \uXXXX code units.
        // JsonUtility otherwise truncates a managed string at its first NUL on the tested Editor.
        static string ExceptionText(Exception error)
        {
            string text;
            try { text = error.ToString(); }
            catch { text = error.GetType().FullName + ": " + error.Message; }
            var escaped = new StringBuilder("[utf16-escaped-v1] ", text.Length + 32);
            foreach (char character in text)
            {
                if (character == '\\') escaped.Append("\\\\");
                else if (char.IsControl(character) || char.IsSurrogate(character))
                    escaped.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                else escaped.Append(character);
            }
            return escaped.ToString();
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
                        RetryFileCleanup();
                        _stats.drainCompleted = (!_stats.terminalAccepted || _stats.terminalPublished) && _retiredFiles.Count == 0;
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
