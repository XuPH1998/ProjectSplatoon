using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Profiling;
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
            public uint foamRevision,foamHash,foamRound;
            public double foamCommitP95Ms,foamCommitP99Ms,foamInstallP95Ms;
            public int foamInstallSamples,foamColliderRebuildsPeak,foamNormalOnlyPeak,foamFeedbackGroupsPeak;
            public int foamDirtyChunksPeak,foamCommitSamples;
            public long foamBytesSent;
            public double duration, joinSeconds, rttMeanMs, rttP95Ms, frameP95Ms, frameP99Ms, maxCorrection, gcMeanBytes;
            public long[] serializedBytesByKind;
            public string[] trafficKinds;
            public long peakSnapshotInFlightBytes;
            public long canonicalSnapshotBytes, encodedSnapshotBytes;
            public double lastSnapshotSeconds;
            public double snapshotAgeMeanMs, snapshotAgeP95Ms;
            public PlayerShots[] authoritativeShots;
            public int rttSamples;
            public bool rttAvailable;
            public uint inputAckSamples, predictionSteps, paintReplays, speculativePaintReconciles, hardCorrections;
            public double inputAckMeanMs, inputAckMaxMs, initialSyncPauseSeconds, inputTimeoutPauseSeconds, correctionDistanceTotal;
            public long totalReplaySteps;
            public int maxReplaySteps;
            public int humanSamples, neutralSwimSamples, friendlySwimSamples, deadSamples;
        }
        struct Sample
        {
            public double time, frameMs, rttMs, inputAckMs, syncPause, timeoutPause;
            public int pending, replaySteps, paperPose;
            public uint corrections, acknowledgedInput, appliedPaint, requiredPaint;
            public float correction, x, y, z;
            public bool paintPending;
        }
        [Serializable] sealed class PlayerShots { public ulong player; public uint shots; }
        readonly List<double> _frames = new(20000), _rtt = new(20000), _snapshotAges = new(20000);
        readonly List<double> _foamTimes=new(4096);
        readonly List<double> _foamInstallTimes=new(4096);
        readonly List<Splatoon.Painting.FoamUpdateMetrics> _foamUpdates=new(16384);
        Splatoon.Painting.FoamTerrainWorld _observedFoam;
        readonly List<string> _foamStates=new(1024);
        readonly Dictionary<ulong, uint> _shots = new(8);
        readonly HashSet<string> _errors = new();
        readonly List<Sample> _samples = new(2400);
        PrototypePlayer _observedPlayer;
        uint _latencySamples;
        double _nextSample;
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
                if(_observedFoam!=match.Arena.Foam)
                {
                    if(_observedFoam!=null)_observedFoam.Updated-=OnFoamUpdate;
                    _observedFoam=match.Arena.Foam;
                    if(_observedFoam!=null)_observedFoam.Updated+=OnFoamUpdate;
                }
                if(match.Arena.FoamPresentation!=null)_result.foamFeedbackGroupsPeak=Math.Max(_result.foamFeedbackGroupsPeak,match.Arena.FoamPresentation.PeakGroups);
                if (_inFlight != null) _result.peakSnapshotInFlightBytes = Math.Max(_result.peakSnapshotInFlightBytes, _inFlight());
                _result.peakPending = Math.Max(_result.peakPending, player.PendingInputCount);
                _result.corrections = player.CorrectionCount;
                _result.maxCorrection = Math.Max(_result.maxCorrection, player.LastCorrectionDistance);
                if (_observedPlayer != player) { _observedPlayer = player; _latencySamples = 0; }
                var timing = player.InputTiming;
                _result.inputAckSamples = timing.Samples;
                _result.inputAckMeanMs = timing.Samples > 0 ? timing.TotalMilliseconds / timing.Samples : 0;
                _result.inputAckMaxMs = timing.MaxMilliseconds;
                _result.predictionSteps = player.PredictionSteps;
                _result.paintReplays = player.PaintReplayCount;
                _result.speculativePaintReconciles = player.SpeculativePaintReconciles;
                _result.hardCorrections = player.HardCorrectionCount;
                _result.initialSyncPauseSeconds = player.InitialSyncPauseSeconds;
                _result.inputTimeoutPauseSeconds = player.InputTimeoutPauseSeconds;
                _result.correctionDistanceTotal = player.CorrectionDistanceTotal;
                _result.totalReplaySteps = player.TotalReplaySteps;
                _result.maxReplaySteps = player.MaxReplaySteps;
                bool validRtt = !player.IsServer && player.GameLatency.TryRead(Time.realtimeSinceStartupAsDouble, out _);
                _result.rttAvailable = validRtt;
                if (elapsed >= _nextSample)
                {
                    _nextSample = elapsed + .1;
                    var predicted = player.PresentedState; var authority = player.Snapshot.Value;
                    if (predicted.Health <= 0) _result.deadSamples++;
                    else if (!predicted.Swimming) _result.humanSamples++;
                    else if (predicted.SwimSource == Splatoon.Combat.SwimSurface.Friendly) _result.friendlySwimSamples++;
                    else _result.neutralSwimSamples++;
                    _samples.Add(new Sample { time = elapsed, frameMs = Time.unscaledDeltaTime * 1000,
                        rttMs = validRtt ? player.GameLatency.Milliseconds : -1, inputAckMs = timing.Samples > 0 ? timing.LastMilliseconds : -1,
                        pending = player.PendingInputCount, replaySteps = player.LastReplaySteps,
                        paperPose = player.SwimBody?.Capture?.PoseVersion ?? 0, corrections = player.CorrectionCount,
                        acknowledgedInput = authority.AcknowledgedInput, appliedPaint = match.AppliedPaintSequence,
                        requiredPaint = authority.RequiredPaintSequence, paintPending = player.PaintReplayPending,
                        syncPause = player.InitialSyncPauseSeconds, timeoutPause = player.InputTimeoutPauseSeconds,
                        correction = player.LastCorrectionDistance, x = predicted.Position.x, y = predicted.Position.y, z = predicted.Position.z });
                    if(match.Arena.Foam!=null)
                    {
                        var foam=match.Arena.Foam;_result.foamRevision=foam.Revision;_result.foamHash=foam.StateHash();_result.foamRound=match.State.Value.Round;
                        _result.foamBytesSent=match.FoamBytesSent;
                        _foamStates.Add(FormattableString.Invariant($"{elapsed:F3},{_result.foamRound},{foam.Revision},{_result.foamHash},{match.AppliedPaintSequence},{match.Arena.OwnershipHash()}"));
                    }
                }
                foreach (var pair in PrototypePlayer.ByOwner)
                    if (pair.Value != null && pair.Value.IsSpawned)
                    { _shots.TryGetValue(pair.Key, out uint previous); _shots[pair.Key] = Math.Max(previous, pair.Value.Snapshot.Value.ShotSequence); }
                if (elapsed - _firstConnected > 3)
                {
                    _frames.Add(Time.unscaledDeltaTime * 1000.0);
                    // Each echo contributes once; reading the same value every frame is not a new sample.
                    if (validRtt && player.GameLatency.Samples != _latencySamples)
                    { _latencySamples = player.GameLatency.Samples; _rtt.Add(player.GameLatency.Milliseconds); }
                    if (!player.IsServer) _snapshotAges.Add(Math.Max(0, player.NetworkManager.ServerTime.Time - player.Snapshot.Value.SimulatedAt) * 1000);
                    if (_gc.Valid) _sumGc += _gc.LastValue;
                }
            }
            if (elapsed >= _duration) Finish();
        }
        static double Percentile(List<double> values, double fraction)
        { if (values.Count == 0) return 0; values.Sort(); return values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * fraction) - 1)]; }
        void OnFoamUpdate(Splatoon.Painting.FoamUpdateMetrics metrics)
        {
            if(_done)return;
            _foamUpdates.Add(metrics);
            if(metrics.Install)_foamInstallTimes.Add(metrics.TotalMs);
            else if(metrics.DirtyChunks>0)_foamTimes.Add(metrics.TotalMs);
            _result.foamDirtyChunksPeak=Math.Max(_result.foamDirtyChunksPeak,metrics.DirtyChunks);
            _result.foamColliderRebuildsPeak=Math.Max(_result.foamColliderRebuildsPeak,metrics.ColliderRebuilds);
            _result.foamNormalOnlyPeak=Math.Max(_result.foamNormalOnlyPeak,metrics.NormalOnlyUpdates);
        }
        void Finish()
        {
            if (_done) return; _done = true;
            _result.duration = Time.realtimeSinceStartupAsDouble - _started; _result.samples = _frames.Count;
            _result.foamCommitSamples=_foamTimes.Count;_result.foamCommitP95Ms=Percentile(_foamTimes,.95);
            _result.foamCommitP99Ms=Percentile(_foamTimes,.99);
            _result.foamInstallSamples=_foamInstallTimes.Count;_result.foamInstallP95Ms=Percentile(_foamInstallTimes,.95);
            var foamCsv=new System.Text.StringBuilder("revision,install,totalMs,depositMs,roundMs,relaxMs,ownershipMs,meshMs,colliderMs,feedbackMs,dirtyChunks,colliderRebuilds,normalOnlyUpdates,ownerUploads\n");
            foreach(var m in _foamUpdates)foamCsv.AppendLine(FormattableString.Invariant($"{m.Revision},{m.Install},{m.TotalMs:F6},{m.DepositMs:F6},{m.RoundMs:F6},{m.RelaxMs:F6},{m.OwnershipMs:F6},{m.MeshMs:F6},{m.ColliderMs:F6},{m.FeedbackMs:F6},{m.DirtyChunks},{m.ColliderRebuilds},{m.NormalOnlyUpdates},{m.OwnerUploads}"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
            File.WriteAllText(Path.ChangeExtension(_output,"foam-commits.csv"),foamCsv.ToString());
            _result.errors = _errors.Count;
            double sum = 0; foreach (var value in _rtt) sum += value;
            _result.rttMeanMs = _rtt.Count > 0 ? sum / _rtt.Count : 0;
            _result.rttP95Ms = Percentile(_rtt, .95);
            _result.rttSamples = _rtt.Count;
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
            File.WriteAllLines(Path.ChangeExtension(_output,".foam.csv"),new[]{"seconds,round,revision,foamHash,paintSequence,ownershipHash"});
            File.AppendAllLines(Path.ChangeExtension(_output,".foam.csv"),_foamStates);
            using (var csv = new StreamWriter(Path.ChangeExtension(_output, ".csv")))
            {
                csv.WriteLine("seconds,frameMs,gameRttMs,inputAckMs,pendingInputs,corrections,correctionMetres,lastReplaySteps,paperPoseVersion,acknowledgedInput,appliedPaint,requiredPaint,paintReplayPending,initialSyncPauseSeconds,inputTimeoutPauseSeconds,x,y,z");
                foreach (var s in _samples) csv.WriteLine(FormattableString.Invariant($"{s.time:F4},{s.frameMs:F3},{s.rttMs:F3},{s.inputAckMs:F3},{s.pending},{s.corrections},{s.correction:F5},{s.replaySteps},{s.paperPose},{s.acknowledgedInput},{s.appliedPaint},{s.requiredPaint},{s.paintPending},{s.syncPause:F4},{s.timeoutPause:F4},{s.x:F5},{s.y:F5},{s.z:F5}"));
            }
            File.WriteAllLines(Path.ChangeExtension(_output, ".errors.txt"), _errors);
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                ScreenCapture.CaptureScreenshot(Path.ChangeExtension(_output, ".png"));
            Application.Quit(_result.connected && _result.initialSyncComplete && _result.errors == 0 ? 0 : 4);
        }
        void OnDestroy() { if(_observedFoam!=null)_observedFoam.Updated-=OnFoamUpdate;Application.logMessageReceived -= OnLog; _gc.Dispose(); }
#endif
    }
}
