#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Explicit opt-in diagnostic sidecar. Never supplies a formal metric or gate.
    internal sealed class BlockFrameTimingObservation
    {
        internal const int HistoryLength = 8, WarmupCap = 512, SamplingCap = 8192, TailCap = 8;
        const int BoundarySlot = WarmupCap, SamplingOffset = BoundarySlot + 1;
        const int TailOffset = SamplingOffset + SamplingCap, Capacity = TailOffset + TailCap;
        internal enum Phase { Warmup, BeforeSampling, Sampling, AfterSampling }
        internal enum GpuValue { Zero, PositiveFinite, NegativeFinite, NotANumber, PositiveInfinity, NegativeInfinity }
        [Serializable] internal struct Timing
        {
            public ulong frameStartTimestamp, firstSubmitTimestamp, cpuTimePresentCalled, cpuTimeFrameComplete;
            // Exact IEEE-754 bits keep zero, NaN/infinity and finite values distinct
            // without inventing a numeric replacement or allocating strings per poll.
            public long gpuFrameTimeBits, cpuFrameTimeBits, cpuMainThreadFrameTimeBits;
            public long cpuRenderThreadFrameTimeBits, cpuMainThreadPresentWaitTimeBits;
            public GpuValue gpuValue;
            internal static Timing Read(FrameTiming value) => new()
            {
                frameStartTimestamp = value.frameStartTimestamp, firstSubmitTimestamp = value.firstSubmitTimestamp,
                cpuTimePresentCalled = value.cpuTimePresentCalled, cpuTimeFrameComplete = value.cpuTimeFrameComplete,
                gpuFrameTimeBits = BitConverter.DoubleToInt64Bits(value.gpuFrameTime),
                cpuFrameTimeBits = BitConverter.DoubleToInt64Bits(value.cpuFrameTime),
                cpuMainThreadFrameTimeBits = BitConverter.DoubleToInt64Bits(value.cpuMainThreadFrameTime),
                cpuRenderThreadFrameTimeBits = BitConverter.DoubleToInt64Bits(value.cpuRenderThreadFrameTime),
                cpuMainThreadPresentWaitTimeBits = BitConverter.DoubleToInt64Bits(value.cpuMainThreadPresentWaitTime),
                gpuValue = double.IsNaN(value.gpuFrameTime) ? GpuValue.NotANumber
                    : double.IsPositiveInfinity(value.gpuFrameTime) ? GpuValue.PositiveInfinity
                    : double.IsNegativeInfinity(value.gpuFrameTime) ? GpuValue.NegativeInfinity
                    : value.gpuFrameTime > 0 ? GpuValue.PositiveFinite : value.gpuFrameTime < 0 ? GpuValue.NegativeFinite : GpuValue.Zero
            };
        }
        [Serializable] internal struct Poll
        {
            public bool retained;
            public Phase phase;
            public int unityFrame, formalSampleIndex, slot, historyOffset;
            public double realtimeSeconds, formalElapsedSeconds;
            public long captureWallStart, captureWallEnd, formalReadWallEnd, historyReadWallStart, historyReadWallEnd;
            public uint formalReturned, historyReturned;
            public int historyRetained;
            public ulong formalTimestampBefore, formalTimestampAfter;
            public int formalGpuSamplesBefore, formalGpuSamplesAfter;
            public Timing formalTiming;
        }
        [Serializable] sealed class Report
        {
            public string purpose = "FrameTiming raw diagnostic only; formal sampling/metrics/gates are unchanged.";
            public string unity, graphicsApi, device, operatingSystem;
            public string clockBoundary = "Unity CPU timer and Stopwatch epochs are unverified; never subtract clocks or infer exact frame ownership from equal frequencies.";
            public string windowBoundary = "BeforeSampling checkpoint is read before the existing pending-window Stopwatch marker and first formal sampled Update. Benchmark _begin uses an earlier cached realtime value; these markers/clocks do not prove exact frame ownership. First seen during/after sampling can contain delayed pre-window frames.";
            public string tailPolicy = "At most eight read-only Update polls after the existing final boundary; no Capture, formal samples, duration extension or Quit delay.";
            public string rawDurationEncoding = "Signed Int64 IEEE-754 bits; durations decode to milliseconds. Zero/negative/nonfinite values remain raw diagnostic evidence and never enter formal Series.";
            public ulong unityCpuTimerFrequency;
            public long stopwatchFrequency;
            public bool cpuFrequencyAvailable, windowStarted, windowEnded;
            public int historyLength = HistoryLength, warmupCap = WarmupCap, samplingCap = SamplingCap, tailCap = TailCap;
            public int warmupRetained, samplingRetained, tailRetained, warmupDropped, samplingDropped;
            public int samplingStartFrame, samplingEndFrame, finalFormalSamples, finalFormalGpuSamples;
            public double samplingStartRealtimeSeconds, samplingEndRealtimeSeconds;
            public long samplingStartStopwatchStamp, samplingEndStopwatchStamp;
            public Poll[] polls;
            public Timing[] history;
        }
        readonly FrameTiming[] _history = new FrameTiming[HistoryLength];
        readonly Report _report;
        readonly string _path;
        bool _written;
        internal BlockFrameTimingObservation(string path, BlockTerrainBenchmark.Result environment)
        {
            _path = path;
            ulong frequency = FrameTimingManager.GetCpuTimerFrequency();
            _report = new Report { unity = environment.unity, graphicsApi = environment.graphicsApi,
                device = environment.device, operatingSystem = environment.operatingSystem,
                unityCpuTimerFrequency = frequency, cpuFrequencyAvailable = frequency != 0,
                stopwatchFrequency = Stopwatch.Frequency, polls = new Poll[Capacity], history = new Timing[Capacity * HistoryLength] };
        }
        internal void ReadWarmup(bool boundary, int frame, double now)
        {
            int slot;
            if (boundary) slot = BoundarySlot;
            else
            {
                if (_report.warmupRetained >= WarmupCap) { _report.warmupDropped++; return; }
                slot = _report.warmupRetained++;
            }
            var poll = new Poll { retained = true, phase = boundary ? Phase.BeforeSampling : Phase.Warmup,
                slot = slot, unityFrame = frame, realtimeSeconds = now };
            ReadHistory(ref poll); _report.polls[slot] = poll;
        }
        internal void StartWindow(int frame, double now, long stamp)
        {
            _report.windowStarted = true; _report.samplingStartFrame = frame;
            _report.samplingStartRealtimeSeconds = now; _report.samplingStartStopwatchStamp = stamp;
        }
        internal Poll BeginSample(int frame, int sample, double now, double elapsed, ulong lastTimestamp, int gpuCount)
        {
            if (_report.samplingRetained >= SamplingCap) { _report.samplingDropped++; return default; }
            return new Poll { retained = true, phase = Phase.Sampling, slot = SamplingOffset + _report.samplingRetained++,
                unityFrame = frame, formalSampleIndex = sample, realtimeSeconds = now, formalElapsedSeconds = elapsed,
                formalTimestampBefore = lastTimestamp, formalGpuSamplesBefore = gpuCount, captureWallStart = Stopwatch.GetTimestamp() };
        }
        internal void EndSample(ref Poll poll, uint returned, FrameTiming latest, ulong lastTimestamp, int gpuCount)
        {
            if (!poll.retained) return;
            poll.formalReturned = returned; poll.formalTimestampAfter = lastTimestamp; poll.formalGpuSamplesAfter = gpuCount;
            if (returned != 0) poll.formalTiming = Timing.Read(latest);
            ReadHistory(ref poll); _report.polls[poll.slot] = poll;
        }
        void ReadHistory(ref Poll poll)
        {
            poll.historyReadWallStart = Stopwatch.GetTimestamp();
            poll.historyReturned = FrameTimingManager.GetLatestTimings(HistoryLength, _history);
            poll.historyReadWallEnd = Stopwatch.GetTimestamp();
            poll.historyOffset = poll.slot * HistoryLength;
            poll.historyRetained = Math.Min((int)poll.historyReturned, HistoryLength);
            for (int i = 0; i < poll.historyRetained; i++) _report.history[poll.historyOffset + i] = Timing.Read(_history[i]);
        }
        internal void EndWindow(int frame, double now, long stamp, int formalSamples, int formalGpuSamples)
        {
            if (_report.windowEnded) return;
            _report.windowEnded = true; _report.samplingEndFrame = frame;
            _report.samplingEndRealtimeSeconds = now; _report.samplingEndStopwatchStamp = stamp;
            _report.finalFormalSamples = formalSamples; _report.finalFormalGpuSamples = formalGpuSamples;
        }
        internal void ReadTail(int frame, double now)
        {
            if (!_report.windowEnded || _written) return;
            if (_report.tailRetained < TailCap)
            {
                int slot = TailOffset + _report.tailRetained++;
                var poll = new Poll { retained = true, phase = Phase.AfterSampling,
                    slot = slot, unityFrame = frame, realtimeSeconds = now };
                ReadHistory(ref poll); _report.polls[slot] = poll;
            }
            if (_report.tailRetained == TailCap) Write();
        }
        internal void Write()
        {
            if (_written) return;
            _written = true;
            // This separate file cannot change the old report or its write/retry policy.
            // Also save a truncated tail when the Player exits before eight Updates.
            try
            {
                // Compact only after sampling/tail stop. Preserve raw poll slots,
                // but serialize no unused default records as apparent zero timings.
                var polls = new List<Poll>(_report.warmupRetained + _report.samplingRetained + _report.tailRetained + 1);
                var history = new List<Timing>(polls.Capacity * HistoryLength);
                foreach (var retained in _report.polls)
                {
                    if (!retained.retained) continue;
                    var poll = retained; int offset = poll.historyOffset;
                    poll.historyOffset = history.Count;
                    for (int i = 0; i < poll.historyRetained; i++) history.Add(_report.history[offset + i]);
                    polls.Add(poll);
                }
                _report.polls = polls.ToArray(); _report.history = history.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonUtility.ToJson(_report, true));
            }
            catch (Exception exception)
            {
                try { UnityEngine.Debug.LogWarning("[BLOCK-FRAME-TIMING-OBSERVATION] Sidecar write failed: " + exception.Message); }
                catch { } // Diagnostic reporting must not replace an existing failure.
            }
        }
    }
}
#endif
