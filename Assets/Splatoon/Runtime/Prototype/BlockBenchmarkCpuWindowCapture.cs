#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using RuntimeProfiler = UnityEngine.Profiling.Profiler;

namespace Splatoon.Prototype
{
    // Explicit observation only. No formal Result, Series or Gate is modified.
    internal sealed class BlockBenchmarkCpuWindowCapture
    {
        internal const int FrameCap = 65536, RequestedMemoryBytes = 134217728;
        internal const int SampleTag = 0x42574301;
        internal static readonly Guid MetadataGuid = new("b7fa6a6f-1e90-4a32-a3c5-6d68c2bfb954");
        [Serializable] internal struct Point
        {
            public int unityFrame, formalSampleOrdinal;
            public long stopwatchStamp, realtimeDoubleBits;
            internal static Point Read(int sample) => new()
            {
                unityFrame = Time.frameCount, formalSampleOrdinal = sample,
                stopwatchStamp = Stopwatch.GetTimestamp(),
                realtimeDoubleBits = BitConverter.DoubleToInt64Bits(Time.realtimeSinceStartupAsDouble)
            };
        }
        [Serializable] internal struct State
        {
            public bool enabled, binaryLog;
            public string logFile;
            public int maxUsedMemory;
            internal static State Read() => new()
            {
                enabled = RuntimeProfiler.enabled, binaryLog = RuntimeProfiler.enableBinaryLog,
                logFile = RuntimeProfiler.logFile, maxUsedMemory = RuntimeProfiler.maxUsedMemory
            };
            internal bool Same(State other) => enabled == other.enabled && binaryLog == other.binaryLog
                && string.Equals(logFile, other.logFile, StringComparison.Ordinal) && maxUsedMemory == other.maxUsedMemory;
        }
        [Serializable] internal struct Frame
        {
            public int observerSequence;
            public bool metadataCallReturned;
            public Point emittedPayload, afterEmit;
        }
        [Serializable] sealed class Report
        {
            public int schemaVersion = 1;
            public string purpose = "Opt-in sampling-window native CPU observation; unchanged formal metrics and gates.";
            public string unity, rawPath, sidecarPath, benchmarkReportPath, finishReason, startError, metadataError, requestFlag, actualRole;
            public string metadataGuid = MetadataGuid.ToString("D");
            public int metadataTag = SampleTag, metadataLongElements = 4, requestedMemoryBytes = RequestedMemoryBytes, frameCap = FrameCap;
            public string metadataLayout = "long[4]: Time.frameCount, formalSampleOrdinal, Stopwatch.GetTimestamp(), realtimeSinceStartupAsDouble IEEE-754 Int64 bits";
            public string metadataLifetime = "One preallocated long[4] is reused; actual native copying and frame association require offline readback of each chunk.";
            public string frameBoundary = "Capture enables partway through the sampling-start Update and stops inside Finish before aggregation. Boundary native frames may be partial. Formal unscaledDeltaTime describes the preceding completed frame; emitted metadata describes the current sampled Update.";
            public string clocks = "These are ordered observations, not simultaneous reads. No Profiler clock epoch or index is inferred from Time.frameCount or Stopwatch alone.";
            public bool requested = true, playerOnly, clientRoleAllowed, enableProfilerCompilation, startAttempted, originalStateRead, activeOriginalStateRefused;
            public bool stateTouched, startReadbackMatched, started, closeRequested, restorationAttempted, restorationReadbackMatched;
            public int observedSamples, metadataCallsReturned, retainedFrames, droppedFrames, finalFormalSamples;
            public long stopwatchFrequency = Stopwatch.Frequency, benchmarkBeginRealtimeDoubleBits, benchmarkBeginPendingStamp;
            public int benchmarkBeginUnityFrame;
            public State originalState, afterStart, beforeRestore, restoredState;
            public Point beforeConfigure, beforeEnable, afterEnable, beforeRestorePoint, afterRestorePoint;
            public Frame[] frames;
            public string[] restorationErrors;
            public bool nativeFlushProven, nativeCompleteWindowProven, metadataArrayReuseValidated, profilerFrameIndexMappedToTimeFrameCount;
            public bool profilerClockMappedToStopwatch, profilerOverheadMeasured, performanceAcceptance;
        }
        readonly Report _report;
        readonly Frame[] _frames = new Frame[FrameCap];
        readonly long[] _metadata = new long[4];
        readonly List<string> _restoreErrors = new();
        bool _active, _closed, _writing, _written, _configuring, _abortRequested;

        internal bool Active => _active && !_closed;

        internal static BlockBenchmarkCpuWindowCapture CreateIfRequested(string benchmarkOutput, string flag = "-blockBenchmarkCpuProfile")
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, flag);
            if (index < 0) return null; // No helper allocation or Profiler native state access when default-off.
            string raw = index + 1 < args.Length ? args[index + 1] : "";
            return new BlockBenchmarkCpuWindowCapture(raw, benchmarkOutput, flag);
        }
        BlockBenchmarkCpuWindowCapture(string raw, string benchmarkOutput, string flag)
        {
            _report = new Report { unity = Application.unityVersion, benchmarkReportPath = benchmarkOutput, requestFlag = flag,
                clientRoleAllowed = flag == "-blockNetworkCpuProfile", playerOnly = !Application.isEditor && UnityEngine.Debug.isDebugBuild };
#if ENABLE_PROFILER
            _report.enableProfilerCompilation = true;
#endif
            try
            {
                if (!Path.IsPathRooted(raw) || Path.GetExtension(raw) != ".raw")
                    throw new ArgumentException("Pass a fresh absolute .raw path after the explicit CPU-profile flag.");
                _report.rawPath = Path.GetFullPath(raw); _report.sidecarPath = _report.rawPath + ".window.json";
                string parent = Path.GetDirectoryName(_report.rawPath);
                if (!Directory.Exists(parent) || !string.Equals(parent, Path.GetDirectoryName(Path.GetFullPath(benchmarkOutput)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("CPU raw must use the existing benchmark report directory.");
                if (File.Exists(_report.rawPath) || Directory.Exists(_report.rawPath) || File.Exists(_report.sidecarPath) || Directory.Exists(_report.sidecarPath))
                    throw new IOException("Preserve existing CPU raw/sidecar; choose a fresh output path.");
            }
            catch (Exception exception) { _report.startError = exception.ToString(); }
        }
        internal void StartWindow(string role, int frame, double benchmarkBegin, long pendingStamp)
        {
            if (_report.startAttempted || _closed) return;
            _report.startAttempted = true; _report.actualRole = role; _report.benchmarkBeginUnityFrame = frame;
            _report.benchmarkBeginRealtimeDoubleBits = BitConverter.DoubleToInt64Bits(benchmarkBegin);
            _report.benchmarkBeginPendingStamp = pendingStamp;
            if (_report.startError != null) return;
            _configuring = true; // Includes native getters and checkpoint reads, not only setters.
            try
            {
                if (!_report.playerOnly || !_report.enableProfilerCompilation || role != "host" && !(_report.clientRoleAllowed && role == "client"))
                    throw new InvalidOperationException("CPU-window capture requires an allowed Development Player role compiled with ENABLE_PROFILER.");
                _report.beforeConfigure = Point.Read(0);
                _report.originalState = State.Read(); _report.originalStateRead = true;
                // Do not redirect/interrupt another active runtime recording or binary stream.
                if (_report.originalState.enabled || _report.originalState.binaryLog)
                { _report.activeOriginalStateRefused = true; throw new InvalidOperationException("Start with runtime Profiler and binary logging disabled; active state was preserved."); }
                if (File.Exists(_report.rawPath) || File.Exists(_report.sidecarPath))
                    throw new IOException("CPU output appeared before activation; preserve it.");
                if (_abortRequested) throw new InvalidOperationException("Error log while reading original runtime state; no setters were entered.");
                _report.stateTouched = true; // Restoration is armed before the first setter.
                RuntimeProfiler.enabled = false;
                RuntimeProfiler.enableBinaryLog = false;
                RuntimeProfiler.logFile = _report.rawPath;
                RuntimeProfiler.maxUsedMemory = RequestedMemoryBytes;
                RuntimeProfiler.enableBinaryLog = true;
                _report.beforeEnable = Point.Read(0);
                RuntimeProfiler.enabled = true;
                _report.afterEnable = Point.Read(0);
                _report.afterStart = State.Read();
                _report.startReadbackMatched = _report.afterStart.enabled && _report.afterStart.binaryLog
                    && _report.afterStart.maxUsedMemory == RequestedMemoryBytes
                    && string.Equals(Path.GetFullPath(_report.afterStart.logFile), _report.rawPath, StringComparison.OrdinalIgnoreCase);
                if (!_report.startReadbackMatched) throw new InvalidOperationException("Runtime Profiler activation readback differed from requested state.");
                _active = _report.started = true;
            }
            catch (Exception exception)
            {
                _report.startError = exception.ToString();
                Close("start_failed", 0);
            }
            finally
            {
                _configuring = false;
                if (_abortRequested) Close("benchmark_error_log_during_configure", 0);
            }
        }
        internal void AbortOnErrorLog(int formalSamples)
        {
            if (_closed) return;
            // An existing error callback may fire reentrantly from a native setter.
            // Defer until the configure scope exits so setters cannot restart a closed capture.
            if (_configuring) { _abortRequested = true; return; }
            Close("benchmark_error_log", formalSamples);
        }
        internal void ObserveSample(int formalSampleOrdinal)
        {
            if (!_active || _closed) return;
            var record = new Frame { observerSequence = ++_report.observedSamples, emittedPayload = Point.Read(formalSampleOrdinal) };
            _metadata[0] = record.emittedPayload.unityFrame;
            _metadata[1] = record.emittedPayload.formalSampleOrdinal;
            _metadata[2] = record.emittedPayload.stopwatchStamp;
            _metadata[3] = record.emittedPayload.realtimeDoubleBits;
            try
            {
                if (!RuntimeProfiler.enabled) throw new InvalidOperationException("Profiler disabled before an expected sample.");
                RuntimeProfiler.EmitFrameMetaData(MetadataGuid, SampleTag, _metadata);
                record.metadataCallReturned = true; _report.metadataCallsReturned++;
            }
            catch (Exception exception) { _report.metadataError = exception.ToString(); }
            record.afterEmit = Point.Read(formalSampleOrdinal);
            if (_report.retainedFrames < FrameCap) _frames[_report.retainedFrames++] = record;
            else _report.droppedFrames++;
            if (_report.metadataError != null) Close("metadata_failed", formalSampleOrdinal);
        }
        internal void Close(string reason, int formalSamples)
        {
            _report.finalFormalSamples = Math.Max(_report.finalFormalSamples, formalSamples);
            if (_closed) return;
            _closed = _report.closeRequested = true; _active = false;
            _report.finishReason = reason;
            try { _report.beforeRestorePoint = Point.Read(formalSamples); }
            catch (Exception exception) { _restoreErrors.Add("Read before restore point: " + exception); }
            if (_report.stateTouched && _report.originalStateRead)
            {
                _report.restorationAttempted = true;
                try { _report.beforeRestore = State.Read(); }
                catch (Exception exception) { _restoreErrors.Add("Read before restore: " + exception); }
                // Independent catches prevent one failed native setter from skipping the rest.
                try { RuntimeProfiler.enabled = false; } catch (Exception exception) { _restoreErrors.Add("Stop enabled: " + exception); }
                try { RuntimeProfiler.enableBinaryLog = false; } catch (Exception exception) { _restoreErrors.Add("Close binary: " + exception); }
                try { RuntimeProfiler.logFile = _report.originalState.logFile; } catch (Exception exception) { _restoreErrors.Add("Restore logFile: " + exception); }
                try { RuntimeProfiler.maxUsedMemory = _report.originalState.maxUsedMemory; } catch (Exception exception) { _restoreErrors.Add("Restore memory: " + exception); }
                try { RuntimeProfiler.enableBinaryLog = _report.originalState.binaryLog; } catch (Exception exception) { _restoreErrors.Add("Restore binary: " + exception); }
                try { RuntimeProfiler.enabled = _report.originalState.enabled; } catch (Exception exception) { _restoreErrors.Add("Restore enabled: " + exception); }
                try { _report.restoredState = State.Read(); _report.restorationReadbackMatched = _report.restoredState.Same(_report.originalState); }
                catch (Exception exception) { _restoreErrors.Add("Read after restore: " + exception); }
            }
            try { _report.afterRestorePoint = Point.Read(formalSamples); }
            catch (Exception exception) { _restoreErrors.Add("Read after restore point: " + exception); }
            // Native state is closed now. JSON allocation and disk write occur only later.
        }
        internal void Write()
        {
            if (!_closed || _written || _writing || string.IsNullOrEmpty(_report.sidecarPath)) return;
            _writing = true;
            try
            {
                _report.frames = new Frame[_report.retainedFrames]; Array.Copy(_frames, _report.frames, _report.retainedFrames);
                _report.restorationErrors = _restoreErrors.ToArray();
                string json = JsonUtility.ToJson(_report, true);
                using (var stream = new FileStream(_report.sidecarPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) writer.Write(json);
                _written = true;
            }
            catch (Exception exception)
            {
                // A failed/partial sidecar is preserved; never overwrite it on lifecycle retry.
                _written = File.Exists(_report.sidecarPath);
                try { UnityEngine.Debug.LogWarning("[BLOCK-CPU-WINDOW] Sidecar write failed: " + exception.Message); } catch { }
            }
            finally { _writing = false; }
        }
    }
}
#endif
