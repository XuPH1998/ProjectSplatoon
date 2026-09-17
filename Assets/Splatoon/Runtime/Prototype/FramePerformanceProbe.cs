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
        bool _done;
        int _exitCode;
        ulong _lastGpuTimestamp;
        long _stamps, _draws, _copies, _submits, _muzzles, _passes, _skips;
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        static double Number(string key, string fallback) => double.Parse(Arg(key, fallback), CultureInfo.InvariantCulture);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Requested) return;
            var go = new GameObject("Frame performance probe"); DontDestroyOnLoad(go); go.AddComponent<FramePerformanceProbe>();
        }
        void Awake()
        {
            _boot = Time.realtimeSinceStartupAsDouble; _duration = Number("-frameProbe", "60");
            _warmup = Number("-frameProbeWarmup", "5"); _players = (int)Number("-frameProbePlayers", "4");
            _result = new Result { unity = Application.unityVersion, device = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType,
                graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, label = Arg("-inkLabel", "manual") };
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
            if (_done) return;
            double now = Time.realtimeSinceStartupAsDouble;
            var match = PrototypeMatch.Current;
            bool ready = match != null && match.IsSpawned && match.InitialSyncComplete && match.State.Value.PlayerCount == _players;
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
            string output = Path.GetFullPath(Arg("-frameProbeOutput", "Reports/FourPlayerPerformance/player.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output, JsonUtility.ToJson(_result, true));
            Debug.Log("[FRAME-PERF] " + output);
            _exitCode = complete && _result.errors == 0 ? 0 : 4;
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
