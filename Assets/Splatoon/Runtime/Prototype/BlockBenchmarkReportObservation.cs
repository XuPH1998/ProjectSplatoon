#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Opt-in diagnosis. Every callable observation boundary is no-throw.
    internal sealed class BlockBenchmarkReportObservation
    {
        [Serializable] internal struct Point
        {
            public int unityFrame, formalSampleOrdinal;
            public long stopwatchStamp, realtimeDoubleBits;
            internal static Point Read(int sample) => new() { unityFrame = Time.frameCount,
                formalSampleOrdinal = sample, stopwatchStamp = Stopwatch.GetTimestamp(),
                realtimeDoubleBits = BitConverter.DoubleToInt64Bits(Time.realtimeSinceStartupAsDouble) };
        }
        [Serializable] internal struct MarkerState
        {
            public bool beginAttempted, beginReturned, endAttempted, endReturned;
        }
        [Serializable] internal sealed class ReportRow
        {
            public long reportId, supersededPendingReportId, benchmarkBeginRealtimeBits, benchmarkReadyRealtimeBits;
            public bool final, samplingActive;
            public string stage, boundary, outcome, exceptionType;
            public int exceptionHResult, queueHeads, spikes, jsonChars;
            public long utf8PayloadBytes, dtoBeginStamp, dtoEndStamp, serializationBeginStamp, serializationEndStamp;
            public long byteCountBeginStamp, byteCountEndStamp;
            public Point begin, end;
            public MarkerState total, dto, serialization, byteCount;
        }
        [Serializable] internal sealed class WriteRow
        {
            public long attemptId, reportId, benchmarkBeginRealtimeBits, benchmarkReadyRealtimeBits;
            public int snapshotSampleOrdinal, exceptionHResult;
            public bool final, samplingActive;
            public string snapshotStage, stage, boundary, outcome, exceptionType;
            public long expectedUtf8PayloadBytes, destinationBytes, ioBeginStamp, ioEndStamp;
            public long lengthObservationBeginStamp, lengthObservationEndStamp, nextRetryRealtimeBits;
            public Point begin, end;
            public MarkerState io, lengthObservation;
        }
        [Serializable] internal sealed class ErrorRow
        {
            public string where, exceptionType;
            public int hResult;
        }
        [Serializable] sealed class Observation
        {
            public int schemaVersion = 2;
            public string purpose = "Opt-in report diagnosis; original samples, gates and 30-second cadence unchanged.";
            public string clocks = "Point reads Unity frame/sample, Stopwatch, then realtime in order. No shared clock epoch or native frame-index mapping is assumed. Formal frameInterval and Spike.frame describe the preceding completed frame.";
            public string phases = "DTO: original aggregation/arrays; Serialize: ToJson; IO: original directory/write/replace/move and caught error; Observer: UTF8 byte count and destination length. Wall duration is not CPU or GC attribution.";
            public string boundaries = "waiting: no ready/begin; warmup: ready but no begin; during_sampling: begin and not done; after_sampling: begin and done. Retry snapshot ID/sample/stage remain frozen; invocation Point/stage are current.";
            public string markerContract = "MarkerState records managed call-site attempts/returns only. ENABLE_PROFILER, native capture enablement, frame association and actual nested tree balance require CPU raw readback; managed bookkeeping is not proof of native profiling.";
            public string failureContract = "Observer exceptions disable further observation, retain existing Exception references, close only markers whose Begin returned, and cannot log/change Result or mask production failures. Native Begin/End exceptions leave balance unproven. Missing/partial sidecar is incomplete evidence.";
            public string unity, formalReportPath, sidecarPath, finishReason;
            public long stopwatchFrequency = Stopwatch.Frequency;
            public bool requested = true, enableProfilerCompilation, finalReportWritten, incomplete, sidecarWriteFailed, performanceAcceptance;
            public int droppedErrorRecords;
            public ReportRow[] reports;
            public WriteRow[] writes;
            public ErrorRow[] errors;
        }
        sealed class Pending
        {
            internal readonly long Id, Bytes;
            internal readonly int Samples;
            internal readonly bool Final;
            internal readonly string Stage;
            internal Pending(long id, long bytes, int samples, bool final, string stage)
            { Id = id; Bytes = bytes; Samples = samples; Final = final; Stage = stage; }
        }
        readonly Observation _observation;
        readonly List<ReportRow> _reports = new(64);
        readonly List<WriteRow> _writes = new(256);
        // Recording the observer's own failure does not allocate or call Unity/native APIs.
        readonly Exception[] _errorExceptions = new Exception[32];
        readonly string[] _errorWhere = new string[32];
        readonly ProfilerMarker _total, _dto, _serialization, _io, _observer;
        Pending _pending;
        ReportScope _activeReport;
        WriteScope _activeWrite;
        long _reportId, _attemptId;
        int _errorCount, _droppedErrors, _closeDepth;
        bool _disabled, _recovering, _sidecarAttempted, _enteringReport, _enteringWrite;
        bool _insideTestFault = false;
        static Exception _bootstrapFailure;
        internal static Exception BootstrapFailure => _bootstrapFailure;
#if UNITY_EDITOR
        // Tests inject managed observation failures; real writer/serializer/markers still run.
        Action<string> _faultInjectionForTests = null;
#endif
        void TestFault(string where)
        {
#if UNITY_EDITOR
            if (_faultInjectionForTests == null) return;
            _insideTestFault = true;
            try { _faultInjectionForTests.Invoke(where); }
            finally { _insideTestFault = false; }
#endif
        }
        internal static BlockBenchmarkReportObservation CreateIfRequested(string formalReport)
        {
            try
            {
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-blockBenchmarkReportObservation") < 0) return null;
                return new BlockBenchmarkReportObservation(formalReport);
            }
            catch (Exception exception) { _bootstrapFailure = exception; return null; }
        }
        BlockBenchmarkReportObservation(string formalReport)
        {
            _observation = new Observation { formalReportPath = formalReport, sidecarPath = formalReport + ".report-observation.json" };
#if ENABLE_PROFILER
            _observation.enableProfilerCompilation = true;
#endif
            try
            {
                _observation.unity = Application.unityVersion;
                if (File.Exists(_observation.sidecarPath) || Directory.Exists(_observation.sidecarPath))
                    throw new IOException("Existing observer output preserved; use a fresh benchmark directory.");
                // Instance initialization only; the default-off factory never accesses native markers.
                _total = new ProfilerMarker("Splatoon.Benchmark.Report.Total");
                _dto = new ProfilerMarker("Splatoon.Benchmark.Report.Dto");
                _serialization = new ProfilerMarker("Splatoon.Benchmark.Report.Serialize");
                _io = new ProfilerMarker("Splatoon.Benchmark.Report.IO");
                _observer = new ProfilerMarker("Splatoon.Benchmark.Report.Observer");
            }
            catch (Exception exception) { RecordFailure("constructor", exception); }
        }
        static string Boundary(double begin, double ready, bool done) => begin < 0 ? ready < 0 ? "waiting" : "warmup" : done ? "after_sampling" : "during_sampling";
        void RecordFailure(string where, Exception exception)
        {
            _disabled = true; _observation.incomplete = true;
            if (_errorCount < _errorExceptions.Length)
            { _errorExceptions[_errorCount] = exception; _errorWhere[_errorCount++] = where; }
            else _droppedErrors++;
        }
        void Recover()
        {
            if (_recovering || _closeDepth != 0) return; _recovering = true;
            try { _activeWrite?.Dispose(); _activeReport?.Dispose(); }
            catch (Exception exception) { RecordFailure("recovery", exception); }
            finally { _recovering = false; }
        }
        void BeginMarker(ProfilerMarker marker, ref MarkerState state)
        { state.beginAttempted = true; marker.Begin(); state.beginReturned = true; }
        void EndMarker(ProfilerMarker marker, ref MarkerState state, string where)
        {
            if (!state.beginReturned || state.endAttempted) return;
            state.endAttempted = true; // Never repeat an End call whose native state is unknown.
            try { marker.End(); state.endReturned = true; }
            catch (Exception exception) { RecordFailure(where, exception); }
        }
        internal ReportScope BeginReport(bool final, string stage, int samples, double begin, double ready, bool done, int heads, int spikes)
        {
            if (_disabled) return null;
            bool ownsEntry = false;
            try
            {
                if (_insideTestFault || _enteringReport || _activeReport != null)
                    throw new InvalidOperationException("Reentrant observer report refused.");
                _enteringReport = true; ownsEntry = true;
                TestFault("begin_report.before");
                if (_disabled) return null;
                var scope = new ReportScope(this, final, stage, samples, begin, ready, done, heads, spikes);
                _activeReport = scope; scope.Start(); TestFault("begin_report.after"); return scope;
            }
            catch (Exception exception) { RecordFailure("begin_report", exception); Recover(); return null; }
            finally { if (ownsEntry) _enteringReport = false; }
        }
        internal WriteScope BeginWrite(string stage, int samples, double begin, double ready, bool done)
        {
            if (_disabled) return null;
            bool ownsEntry = false;
            try
            {
                if (_insideTestFault || _enteringWrite || _activeWrite != null)
                    throw new InvalidOperationException("Reentrant observer write refused.");
                _enteringWrite = true; ownsEntry = true;
                TestFault("begin_write.before");
                if (_disabled) return null;
                var scope = new WriteScope(this, stage, samples, begin, ready, done);
                _activeWrite = scope; scope.Start(); TestFault("begin_write.after"); return scope;
            }
            catch (Exception exception) { RecordFailure("begin_write", exception); Recover(); return null; }
            finally { if (ownsEntry) _enteringWrite = false; }
        }
        internal sealed class ReportScope : IDisposable
        {
            readonly BlockBenchmarkReportObservation _owner;
            readonly ReportRow _row;
            bool _disposed;
            internal ReportScope(BlockBenchmarkReportObservation owner, bool final, string stage, int samples, double begin, double ready, bool done, int heads, int spikes)
            {
                _owner = owner; _row = new ReportRow { reportId = ++owner._reportId, final = final, stage = stage,
                    samplingActive = begin >= 0 && !done, boundary = Boundary(begin, ready, done),
                    benchmarkBeginRealtimeBits = BitConverter.DoubleToInt64Bits(begin), benchmarkReadyRealtimeBits = BitConverter.DoubleToInt64Bits(ready),
                    queueHeads = heads, spikes = spikes, begin = Point.Read(samples), outcome = "returned",
                    supersededPendingReportId = owner._pending?.Id ?? 0 };
            }
            internal void Start()
            { _owner.BeginMarker(_owner._total, ref _row.total); _owner.BeginMarker(_owner._dto, ref _row.dto); _row.dtoBeginStamp = Stopwatch.GetTimestamp(); }
            internal void BeginSerialization()
            {
                if (_disposed || _owner._disabled) return;
                try
                {
                    _owner.TestFault("begin_serialization.before"); if (_disposed || _owner._disabled) return;
                    _row.dtoEndStamp = Stopwatch.GetTimestamp();
                    _owner.EndMarker(_owner._dto, ref _row.dto, "dto.end");
                    if (_owner._disabled) { _owner.Recover(); return; }
                    _owner.BeginMarker(_owner._serialization, ref _row.serialization);
                    _row.serializationBeginStamp = Stopwatch.GetTimestamp(); _owner.TestFault("begin_serialization.after");
                }
                catch (Exception exception) { _owner.RecordFailure("begin_serialization", exception); _owner.Recover(); }
            }
            internal void EndSerialization()
            {
                if (_disposed || _owner._disabled) return;
                try
                {
                    _row.serializationEndStamp = Stopwatch.GetTimestamp(); _owner.EndMarker(_owner._serialization, ref _row.serialization, "serialization.end");
                    _owner.TestFault("end_serialization"); if (_owner._disabled) _owner.Recover();
                }
                catch (Exception exception) { _owner.RecordFailure("end_serialization", exception); _owner.Recover(); }
            }
            internal void Serialized(string json)
            {
                if (_disposed || _owner._disabled) return;
                try
                {
                    _owner.BeginMarker(_owner._observer, ref _row.byteCount); _row.byteCountBeginStamp = Stopwatch.GetTimestamp();
                    _owner.TestFault("serialized"); if (_disposed || _owner._disabled) return;
                    _row.jsonChars = json.Length; _row.utf8PayloadBytes = Encoding.UTF8.GetByteCount(json);
                    _owner._pending = new Pending(_row.reportId, _row.utf8PayloadBytes, _row.begin.formalSampleOrdinal, _row.final, _row.stage);
                }
                catch (Exception exception) { _owner.RecordFailure("serialized", exception); }
                finally
                {
                    if (!_disposed)
                    {
                        try { _row.byteCountEndStamp = Stopwatch.GetTimestamp(); }
                        catch (Exception exception) { _owner.RecordFailure("byte_count.timestamp", exception); }
                        _owner.EndMarker(_owner._observer, ref _row.byteCount, "byte_count.end");
                    }
                    if (_owner._disabled) _owner.Recover();
                }
            }
            internal void Failed(Exception exception)
            {
                if (_disposed || _owner._disabled) return;
                try { _owner.TestFault("report.failed"); if (_disposed || _owner._disabled) return;
                    _row.outcome = "production-exception"; _row.exceptionType = exception.GetType().FullName; _row.exceptionHResult = exception.HResult; }
                catch (Exception observerException) { _owner.RecordFailure("report.failed", observerException); _owner.Recover(); }
            }
            public void Dispose()
            {
                if (_disposed) return; _disposed = true; if (_owner._activeReport == this) _owner._activeReport = null;
                _owner._closeDepth++;
                try
                {
                    // Independent marker cleanup precedes fallible ledger/metadata work.
                    _owner.EndMarker(_owner._observer, ref _row.byteCount, "dispose.byte_count.end");
                    _owner.EndMarker(_owner._serialization, ref _row.serialization, "dispose.serialization.end");
                    _owner.EndMarker(_owner._dto, ref _row.dto, "dispose.dto.end");
                    _owner.EndMarker(_owner._total, ref _row.total, "dispose.total.end");
                    try
                    {
                        _owner.TestFault("report.dispose");
                        _row.end = Point.Read(_row.begin.formalSampleOrdinal);
                    }
                    catch (Exception exception) { _owner.RecordFailure("report.dispose", exception); }
                    try
                    {
                        if (_owner._disabled && _row.outcome == "returned") _row.outcome = "observer-incomplete";
                        _owner._reports.Add(_row); // Freeze by appending once; never mutate this row afterward.
                    }
                    catch (Exception exception) { _owner.RecordFailure("report.append", exception); }
                }
                finally { _owner._closeDepth--; if (_owner._disabled) _owner.Recover(); }
            }
        }
        internal sealed class WriteScope : IDisposable
        {
            readonly BlockBenchmarkReportObservation _owner;
            readonly WriteRow _row;
            bool _disposed;
            internal WriteScope(BlockBenchmarkReportObservation owner, string stage, int samples, double begin, double ready, bool done)
            {
                _owner = owner; var pending = owner._pending;
                _row = new WriteRow { attemptId = ++owner._attemptId, reportId = pending?.Id ?? 0,
                    snapshotSampleOrdinal = pending?.Samples ?? -1, snapshotStage = pending?.Stage, final = pending?.Final ?? false,
                    expectedUtf8PayloadBytes = pending?.Bytes ?? -1, stage = stage, samplingActive = begin >= 0 && !done,
                    boundary = Boundary(begin, ready, done), benchmarkBeginRealtimeBits = BitConverter.DoubleToInt64Bits(begin),
                    benchmarkReadyRealtimeBits = BitConverter.DoubleToInt64Bits(ready), begin = Point.Read(samples), outcome = "success", destinationBytes = -1 };
            }
            internal void Start() { _owner.BeginMarker(_owner._io, ref _row.io); _row.ioBeginStamp = Stopwatch.GetTimestamp(); }
            internal void Failed(string outcome, Exception exception, double retryRealtime = 0)
            {
                if (_disposed || _owner._disabled) return;
                try { _owner.TestFault("write.failed"); if (_disposed || _owner._disabled) return;
                    _row.outcome = outcome; _row.exceptionType = exception.GetType().FullName;
                    _row.exceptionHResult = exception.HResult; _row.nextRetryRealtimeBits = BitConverter.DoubleToInt64Bits(retryRealtime); }
                catch (Exception observerException) { _owner.RecordFailure("write.failed", observerException); _owner.Recover(); }
            }
            public void Dispose()
            {
                if (_disposed) return; _disposed = true; if (_owner._activeWrite == this) _owner._activeWrite = null;
                _owner._closeDepth++;
                try
                {
                    try { _row.ioEndStamp = Stopwatch.GetTimestamp(); }
                    catch (Exception exception) { _owner.RecordFailure("io.timestamp", exception); }
                    _owner.EndMarker(_owner._io, ref _row.io, "io.end");
                    try
                    {
                        _owner.TestFault("write.dispose");
                        if (!_owner._disabled && _row.outcome == "success")
                        {
                            _owner.BeginMarker(_owner._observer, ref _row.lengthObservation); _row.lengthObservationBeginStamp = Stopwatch.GetTimestamp();
                            _owner.TestFault("destination_length");
                            if (!_owner._disabled)
                            { _row.destinationBytes = new FileInfo(_owner._observation.formalReportPath).Length; _owner._pending = null; }
                        }
                    }
                    catch (Exception exception) { _owner.RecordFailure("write.dispose", exception); }
                    finally
                    {
                        try { _row.lengthObservationEndStamp = Stopwatch.GetTimestamp(); }
                        catch (Exception exception) { _owner.RecordFailure("length.timestamp", exception); }
                        _owner.EndMarker(_owner._observer, ref _row.lengthObservation, "length.end");
                    }
                    try { if (_owner._disabled && _row.outcome == "success") _row.outcome = "observer-incomplete";
                        _row.end = Point.Read(_row.begin.formalSampleOrdinal); _owner._writes.Add(_row); }
                    catch (Exception exception) { _owner.RecordFailure("write.append", exception); }
                }
                finally { _owner._closeDepth--; if (_owner._disabled) _owner.Recover(); }
            }
        }
        internal void WriteIfFinalWritten(bool written)
        { try { if (written) WriteSnapshot("final_report_written", true); } catch (Exception exception) { RecordFailure("snapshot.entry", exception); } }
        internal void WriteOnExit(bool written)
        { try { WriteSnapshot("observer_exit", written); } catch (Exception exception) { RecordFailure("exit.entry", exception); } }
        ErrorRow[] Errors()
        {
            var errors = new ErrorRow[_errorCount];
            for (int i = 0; i < errors.Length; i++) errors[i] = new ErrorRow { where = _errorWhere[i],
                exceptionType = _errorExceptions[i].GetType().FullName, hResult = _errorExceptions[i].HResult };
            return errors;
        }
        void WriteSnapshot(string reason, bool written)
        {
            if (_sidecarAttempted) return; _sidecarAttempted = true; Recover();
            try
            {
                _observation.finishReason = reason; _observation.finalReportWritten = written;
                _observation.reports = _reports.ToArray(); _observation.writes = _writes.ToArray();
                _observation.droppedErrorRecords = _droppedErrors; _observation.errors = Errors();
                TestFault("sidecar.serialize"); string json = JsonUtility.ToJson(_observation, true);
                TestFault("sidecar.create");
                using (var stream = new FileStream(_observation.sidecarPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
            }
            catch (Exception exception)
            {
                RecordFailure("sidecar.write", exception); _observation.sidecarWriteFailed = true;
                try
                {
                    // A small, separate CreateNew failure record; never overwrite the formal/partial sidecar.
                    var failure = new Observation { formalReportPath = _observation.formalReportPath, sidecarPath = _observation.sidecarPath,
                        incomplete = true, sidecarWriteFailed = true, finalReportWritten = written, errors = Errors(), droppedErrorRecords = _droppedErrors };
                    using (var stream = new FileStream(_observation.sidecarPath + ".write-failure.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(failure, true));
                }
                catch (Exception fallbackException) { RecordFailure("sidecar.failure_record", fallbackException); }
            }
        }
    }
}
#endif
