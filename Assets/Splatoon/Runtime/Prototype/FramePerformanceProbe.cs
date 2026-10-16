using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in observation of normal Player frames. Never renders a second camera or supplies input.</summary>
    public sealed class FramePerformanceProbe : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static readonly bool Requested = Array.IndexOf(Environment.GetCommandLineArgs(), "-frameProbe") >= 0;
        static readonly bool ReadinessTraceEnabled = Environment.GetEnvironmentVariable("SPLATOON_FRAME_PROBE_READINESS_TRACE") == "1";
        int _readinessTraceIndex;
        static string Observed(bool? value) => value.HasValue ? value.Value.ToString() : "skipped";
        static string Observed(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "skipped";
        static string ReadinessIntegerArg(string[] args, string key)
        {
            int index = Array.IndexOf(args, key), value = 0;
            bool numeric = index >= 0 && index + 1 < args.Length &&
                int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return string.Format(CultureInfo.InvariantCulture, "{0}:seen={1},numeric={2},value={3}",
                key, index >= 0, numeric, numeric ? value.ToString(CultureInfo.InvariantCulture) : "unparsed");
        }
        [Serializable] public sealed class Metric
        {
            public string name;
            public bool available;
            public int samples;
            public double p50, p95, p99, max, mean;
        }
        [Serializable] sealed class Result
        {
            public string unity, device, cpu, role, label;
            public int width, height, players, renderedFrames, longFrames33, longFrames50, errors;
            public bool complete, graphics, gpuTimingAvailable;
            public double seconds;
            public long paintStamps, paintDraws, paintCopies, paintSubmissions, muzzleParticles, compositePasses, skippedComposites;
            public Metric[] metrics;
        }
        sealed class Series
        {
            public readonly string Name;
            public readonly List<double> Values = new(32768);
            public ProfilerRecorder Recorder;
            public readonly double Scale;
            public Series(string name, double scale = 1) { Name = name; Scale = scale; }
            public void Record() { if (Recorder.Valid) Values.Add(Recorder.LastValue * Scale); }
            public Metric Finish()
            {
                var v = Values; v.Sort(); double sum = 0; foreach (double x in v) sum += x;
                double At(double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)Math.Ceiling(v.Count * q) - 1)];
                return new Metric { name = Name, available = v.Count > 0, samples = v.Count,
                    p50 = At(.5), p95 = At(.95), p99 = At(.99), max = At(1), mean = v.Count > 0 ? sum / v.Count : 0 };
            }
        }
        readonly List<Series> _series = new();
        readonly FrameTiming[] _timing = new FrameTiming[1];
        Series _frames, _gpu;
        Result _result;
        double _boot, _ready = -1, _begin = -1, _duration, _warmup;
        int _players, _rendered;
        bool _done, _written;
        int _requestedWidth, _requestedHeight, _resolutionStableFrames;
        int _exitCode;
        string _output;
        double _nextWrite;
        ulong _lastGpuTimestamp;
        long _stamps, _draws, _copies, _submits, _muzzles, _passes, _skips;
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        static double Number(string key, string fallback) => double.Parse(Arg(key, fallback), CultureInfo.InvariantCulture);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (ReadinessTraceEnabled)
            {
                var args = Environment.GetCommandLineArgs();
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[FRAME-PERF-READINESS] Boot requested={0} rawFrameProbeSeen={1} {2} {3} {4}",
                    Requested, Array.IndexOf(args, "-frameProbe") >= 0,
                    ReadinessIntegerArg(args, "-screen-fullscreen"), ReadinessIntegerArg(args, "-screen-width"),
                    ReadinessIntegerArg(args, "-screen-height")));
            }
            if (!Requested) return;
            var go = new GameObject("Frame performance probe"); DontDestroyOnLoad(go); go.AddComponent<FramePerformanceProbe>();
        }
        void Awake()
        {
            int traceInitialWidth = 0, traceInitialHeight = 0;
            if (ReadinessTraceEnabled) { traceInitialWidth = Screen.width; traceInitialHeight = Screen.height; }
            bool traceSetResolutionCalled = false;
            _boot = Time.realtimeSinceStartupAsDouble; _duration = Number("-frameProbe", "60");
            _warmup = Number("-frameProbeWarmup", "5"); _players = (int)Number("-frameProbePlayers", "4");
            _result = new Result { unity = Application.unityVersion, device = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType,
                graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, label = Arg("-inkLabel", "manual") };
            // Honor explicit windowed Player dimensions before the existing warmup.
            // Unflagged gameplay and probes without dimensions retain their current mode.
            if (_result.graphics && Arg("-screen-fullscreen", "") == "0" &&
                int.TryParse(Arg("-screen-width", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) && width > 0 &&
                int.TryParse(Arg("-screen-height", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) && height > 0)
            {
                _requestedWidth = width; _requestedHeight = height;
                Screen.SetResolution(width, height, FullScreenMode.Windowed);
                traceSetResolutionCalled = true;
            }
            if (ReadinessTraceEnabled)
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[FRAME-PERF-READINESS] Awake graphics={0} setResolutionCalled={1} requested={2}x{3} initialScreen={4}x{5} players={6} warmup={7:R} duration={8:R}",
                    _result.graphics, traceSetResolutionCalled, _requestedWidth, _requestedHeight,
                    traceInitialWidth, traceInitialHeight, _players, _warmup, _duration));
            _series.Add(_frames = new Series("frameIntervalMs")); _series.Add(_gpu = new Series("gpuFrameMs"));
            Add("mainThreadMs", ProfilerCategory.Internal, "Main Thread");
            Add("renderThreadMs", ProfilerCategory.Internal, "Render Thread");
            Add("gcBytes", ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            foreach (string marker in FramePerformance.MarkerNames)
                Add(marker + "Ms", ProfilerCategory.Scripts, marker);
            Application.logMessageReceived += OnLog; RenderPipelineManager.endCameraRendering += OnCamera;
        }
        void Add(string name, ProfilerCategory category, string marker, double scale = .000001)
        {
            var s = new Series(name, scale);
            s.Recorder = ProfilerRecorder.StartNew(category, marker, 1,
                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
            _series.Add(s);
        }
        void OnLog(string message, string stack, LogType type)
        { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _result.errors++; }
        void OnCamera(ScriptableRenderContext context, Camera camera)
        { if (_begin >= 0 && !_done && camera == Camera.main) _rendered++; }
        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_done) { if (!_written && now >= _nextWrite) Write(); return; }
            bool sizeRequested = _requestedWidth > 0 && _requestedHeight > 0;
            if (sizeRequested)
                _resolutionStableFrames = Screen.width == _requestedWidth && Screen.height == _requestedHeight
                    ? Math.Min(3, _resolutionStableFrames + 1) : 0;
            var match = PrototypeMatch.Current;
            bool traceReadiness = ReadinessTraceEnabled && _readinessTraceIndex < 8 &&
                now - _boot >= (_readinessTraceIndex == 0 ? 0 : 1 << (_readinessTraceIndex - 1));
            bool ready;
            if (traceReadiness)
            {
                // Capture only operands actually evaluated by the original short-circuit chain.
                bool? settled = null, matchPresent = null, spawned = null, initialSync = null;
                int? playerCount = null;
                ready = (!sizeRequested || _resolutionStableFrames == 3) &&
                    (settled = InkStaticScenario.Settled).Value && (matchPresent = match != null).Value &&
                    (spawned = match.IsSpawned).Value && (initialSync = match.InitialSyncComplete).Value &&
                    (playerCount = match.State.Value.PlayerCount).Value == _players;
                double scheduled = _readinessTraceIndex == 0 ? 0 : 1 << (_readinessTraceIndex - 1);
                _readinessTraceIndex++;
                double warmupElapsed = ready && _ready >= 0 ? now - _ready : 0;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[FRAME-PERF-READINESS] Update point=afterReadyBeforeTransitions slot={0} scheduled={1:R} elapsed={2:R} sizeRequested={3} requested={4}x{5} actual={6}x{7} stableFrames={8} settled={9} matchPresent={10} spawned={11} initialSync={12} playerCount={13} expectedPlayers={14} warmup={15:R} warmupElapsed={16:R} warmupSatisfied={17} ready={18} readySince={19:R} sampleStarted={20} begin={21:R} done={22} renderedFrames={23}",
                    _readinessTraceIndex - 1, scheduled, now - _boot, sizeRequested, _requestedWidth, _requestedHeight,
                    Screen.width, Screen.height, _resolutionStableFrames, Observed(settled), Observed(matchPresent),
                    Observed(spawned), Observed(initialSync), Observed(playerCount), _players, _warmup, warmupElapsed,
                    ready && warmupElapsed >= _warmup, ready, _ready, _begin >= 0, _begin, _done, _rendered));
            }
            else
                ready = (!sizeRequested || _resolutionStableFrames == 3) && InkStaticScenario.Settled &&
                    match != null && match.IsSpawned && match.InitialSyncComplete && match.State.Value.PlayerCount == _players;
            if (!ready)
            {
                _ready = -1;
                if (_begin >= 0 || now - _boot > Number("-frameProbeTimeout", "180")) Finish(false);
                return;
            }
            if (_ready < 0) _ready = now;
            if (now - _ready < _warmup) return;
            if (_begin < 0)
            {
                _begin = now; _result.role = match.IsServer ? "host" : "client"; _result.players = _players;
                _result.width = Screen.width; _result.height = Screen.height;
                _stamps = FramePerformance.PaintStamps; _draws = FramePerformance.PaintDraws; _copies = FramePerformance.PaintCopies;
                _submits = FramePerformance.PaintSubmissions; _muzzles = FramePerformance.MuzzleParticles;
                _passes = FramePerformance.CompositePasses; _skips = FramePerformance.SkippedComposites;
                Debug.Log("[FRAME-PERF] Sampling normal rendered frames"); return;
            }
            double ms = Time.unscaledDeltaTime * 1000.0; _frames.Values.Add(ms);
            if (ms > 33.333) _result.longFrames33++; if (ms > 50) _result.longFrames50++;
            for (int i = 2; i < _series.Count; i++) _series[i].Record();
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timing) > 0 && _timing[0].gpuFrameTime > 0 && _timing[0].frameStartTimestamp != _lastGpuTimestamp)
            { _gpu.Values.Add(_timing[0].gpuFrameTime); _lastGpuTimestamp = _timing[0].frameStartTimestamp; }
            if (now - _begin >= _duration) Finish(true);
        }
        void Finish(bool complete)
        {
            if (_done) return; _done = true;
            _result.complete = complete; _result.seconds = _begin < 0 ? 0 : Time.realtimeSinceStartupAsDouble - _begin;
            _result.renderedFrames = _rendered; _result.gpuTimingAvailable = _gpu.Values.Count > 0;
            _result.paintStamps = FramePerformance.PaintStamps - _stamps; _result.paintDraws = FramePerformance.PaintDraws - _draws;
            _result.paintCopies = FramePerformance.PaintCopies - _copies; _result.paintSubmissions = FramePerformance.PaintSubmissions - _submits;
            _result.muzzleParticles = FramePerformance.MuzzleParticles - _muzzles;
            _result.compositePasses = FramePerformance.CompositePasses - _passes; _result.skippedComposites = FramePerformance.SkippedComposites - _skips;
            _result.metrics = new Metric[_series.Count]; for (int i = 0; i < _series.Count; i++) _result.metrics[i] = _series[i].Finish();
            _output = Path.GetFullPath(Arg("-frameProbeOutput", "Reports/FourPlayerPerformance/player.json"));
            _exitCode = complete && _result.errors == 0 ? 0 : 4;
            Write();
        }
        void Write()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_output));
                string temporary = _output + ".writing"; File.WriteAllText(temporary, JsonUtility.ToJson(_result, true));
                if (File.Exists(_output)) File.Replace(temporary, _output, null); else File.Move(temporary, _output);
            }
            catch (IOException)
            { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return; }
            _written = true;
            Debug.Log("[FRAME-PERF] " + _output);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-frameProbeQuit") >= 0)
                Invoke(nameof(Quit), (float)Number("-frameProbeQuitDelay", "3"));
        }
        void Quit() => Application.Quit(_exitCode);
        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog; RenderPipelineManager.endCameraRendering -= OnCamera;
            foreach (var s in _series) s.Recorder.Dispose();
        }
#endif
    }
}
