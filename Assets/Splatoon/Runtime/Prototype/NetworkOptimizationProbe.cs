using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using Unity.Netcode.Transports.UTP;
#endif

namespace Splatoon.Prototype
{
    /// <summary>Opt-in process probe; does not generate input or change simulation.</summary>
    public sealed class NetworkOptimizationProbe : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Serializable] sealed class Result
        {
            public string label;
            public bool connected, initialSyncComplete;
            public int players, samples, peakPending, errors;
            public uint corrections, appliedPaint, ownershipHash;
            public double duration, joinSeconds, rttMeanMs, rttP95Ms, frameP95Ms, frameP99Ms, maxCorrection, gcMeanBytes;
            public long[] serializedBytesByKind;
            public string[] trafficKinds;
            public long peakSnapshotInFlightBytes;
            public long canonicalSnapshotBytes, encodedSnapshotBytes;
            public double lastSnapshotSeconds;
            public double snapshotAgeMeanMs, snapshotAgeP95Ms;
            public PlayerShots[] authoritativeShots;
        }
        [Serializable] sealed class PlayerShots { public ulong player; public uint shots; }
        readonly List<double> _frames = new(20000), _rtt = new(20000), _snapshotAges = new(20000);
        readonly Dictionary<ulong, uint> _shots = new(8);
        readonly HashSet<string> _errors = new();
        ProfilerRecorder _gc;
        Result _result;
        string _output;
        double _started, _firstConnected = -1, _sumGc, _duration;
        bool _done;
        Func<long> _inFlight;
        PrototypeMatch _observedMatch;
        Type _trafficType;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-networkProbe") < 0) return;
            var go = new GameObject("NetworkOptimizationProbe"); DontDestroyOnLoad(go); go.AddComponent<NetworkOptimizationProbe>();
        }
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        void Awake()
        {
            _started = Time.realtimeSinceStartupAsDouble;
            _duration = double.Parse(Arg("-networkProbe", "45"), CultureInfo.InvariantCulture);
            _output = Arg("-networkProbeOutput", "Reports/NetworkOptimization/process.json");
            _result = new Result { label = Arg("-inkLabel", "process"), joinSeconds = -1 };
            _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            Application.logMessageReceived += OnLog;
            // Reflection keeps this same observation-only probe usable in the unmodified baseline build.
            _trafficType = typeof(PrototypeMatch).Assembly.GetType("Splatoon.Networking.NetworkTrafficCounters");
        }
        void OnLog(string text, string stack, LogType type)
        { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _errors.Add(text); }
        void Update()
        {
            if (_done) return;
            double elapsed = Time.realtimeSinceStartupAsDouble - _started;
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            if (match != null && player != null && match.IsSpawned)
            {
                _result.connected = true;
                if (_firstConnected < 0) _firstConnected = elapsed;
                if (match.InitialSyncComplete && _result.joinSeconds < 0) _result.joinSeconds = elapsed;
                _result.players = Math.Max(_result.players, PrototypePlayer.ByOwner.Count);
                _result.initialSyncComplete = match.InitialSyncComplete;
                _result.appliedPaint = match.AppliedPaintSequence;
                if (_observedMatch != match)
                {
                    _observedMatch = match; _inFlight = null;
                    var getter = typeof(PrototypeMatch).GetProperty("SnapshotInFlightBytes")?.GetGetMethod();
                    if (getter != null) _inFlight = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), match, getter);
                }
                if (_inFlight != null) _result.peakSnapshotInFlightBytes = Math.Max(_result.peakSnapshotInFlightBytes, _inFlight());
                _result.peakPending = Math.Max(_result.peakPending, player.PendingInputCount);
                _result.corrections = player.CorrectionCount;
                _result.maxCorrection = Math.Max(_result.maxCorrection, player.LastCorrectionDistance);
                foreach (var pair in PrototypePlayer.ByOwner)
                    if (pair.Value != null && pair.Value.IsSpawned)
                    { _shots.TryGetValue(pair.Key, out uint previous); _shots[pair.Key] = Math.Max(previous, pair.Value.Snapshot.Value.ShotSequence); }
                if (elapsed - _firstConnected > 3)
                {
                    _frames.Add(Time.unscaledDeltaTime * 1000.0);
                    _rtt.Add(player.IsServer ? 0 : ((UnityTransport)player.NetworkManager.NetworkConfig.NetworkTransport).GetCurrentRtt(0));
                    if (!player.IsServer) _snapshotAges.Add(Math.Max(0, player.NetworkManager.ServerTime.Time - player.Snapshot.Value.SimulatedAt) * 1000);
                    if (_gc.Valid) _sumGc += _gc.LastValue;
                }
            }
            if (elapsed >= _duration) Finish();
        }
        static double Percentile(List<double> values, double fraction)
        { if (values.Count == 0) return 0; values.Sort(); return values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * fraction) - 1)]; }
        void Finish()
        {
            if (_done) return; _done = true;
            _result.duration = Time.realtimeSinceStartupAsDouble - _started; _result.samples = _frames.Count;
            _result.errors = _errors.Count;
            double sum = 0; foreach (var value in _rtt) sum += value;
            _result.rttMeanMs = _rtt.Count > 0 ? sum / _rtt.Count : 0;
            _result.rttP95Ms = Percentile(_rtt, .95);
            _result.frameP95Ms = Percentile(_frames, .95); _result.frameP99Ms = Percentile(_frames, .99);
            _result.gcMeanBytes = _frames.Count > 0 ? _sumGc / _frames.Count : 0;
            sum = 0; foreach (var age in _snapshotAges) sum += age;
            _result.snapshotAgeMeanMs = _snapshotAges.Count > 0 ? sum / _snapshotAges.Count : 0;
            _result.snapshotAgeP95Ms = Percentile(_snapshotAges, .95);
            _result.authoritativeShots = new PlayerShots[_shots.Count];
            int shotIndex = 0;
            foreach (var pair in _shots) _result.authoritativeShots[shotIndex++] = new PlayerShots { player = pair.Key, shots = pair.Value };
            if (PrototypeMatch.Current != null) _result.ownershipHash = PrototypeMatch.Current.Arena.OwnershipHash();
            if (_trafficType != null)
            {
                var kind = _trafficType.Assembly.GetType("Splatoon.Networking.NetworkTrafficKind");
                var names = Enum.GetNames(kind); _result.trafficKinds = new string[names.Length - 1];
                _result.serializedBytesByKind = new long[names.Length - 1];
                for (int i = 0; i < names.Length - 1; i++)
                { _result.trafficKinds[i] = names[i]; _result.serializedBytesByKind[i] = (long)_trafficType.GetMethod("GetBytes").Invoke(null, new[] { Enum.ToObject(kind, i) }); }
                var codec = _trafficType.Assembly.GetType("Splatoon.Networking.PlayerSnapshotDelta");
                _result.canonicalSnapshotBytes = (long)codec.GetProperty("FullBytes").GetValue(null);
                _result.encodedSnapshotBytes = (long)codec.GetProperty("EncodedBytes").GetValue(null);
                if (PrototypeMatch.Current != null)
                    _result.lastSnapshotSeconds = (double)typeof(PrototypeMatch).GetProperty("LastSnapshotSeconds").GetValue(PrototypeMatch.Current);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
            File.WriteAllText(_output, JsonUtility.ToJson(_result, true));
            File.WriteAllLines(Path.ChangeExtension(_output, ".errors.txt"), _errors);
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.ChangeExtension(_output, ".png"));
            Application.Quit(_result.connected && _result.initialSyncComplete && _result.errors == 0 ? 0 : 4);
        }
        void OnDestroy() { Application.logMessageReceived -= OnLog; _gc.Dispose(); }
#endif
    }
}
