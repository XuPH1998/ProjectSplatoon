using UnityEngine;
using Splatoon.Networking;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Unity.Profiling;
using UnityEngine.Rendering;
#endif

namespace Splatoon.Prototype
{
    /// <summary>Opt-in independent Player acceptance for the fixed-grid and paint transaction.</summary>
    public sealed partial class BlockFixedGridAcceptanceProbe : MonoBehaviour
    {
        public static bool ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!Requested || _instance == null) return false;
            _instance.Input(player, ref input); return true;
#else
            return false;
#endif
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static readonly bool Requested = Array.IndexOf(Environment.GetCommandLineArgs(), "-blockFixedGridProbe") >= 0;
        static BlockFixedGridAcceptanceProbe _instance;
        static readonly ProfilerMarker CaptureCpu = new("Splatoon.FixedProbe.Capture");
        static readonly ProfilerMarker SerializeCpu = new("Splatoon.FixedProbe.Serialize");
        static readonly ProfilerMarker FrameEvidenceCpu = new("Splatoon.FixedProbe.FrameEvidence");
        [Serializable] public sealed class Metric
        { public int samples; public double p95, p99, maximum; }
        [Serializable] public sealed class StateSample
        {
            public string stage, phase, worldHash, cellHash, paintHash, observerFailure;
            public int command, generation, players, fixedBlocks, paintCells, pink, blue, scorePink, scoreBlue, pending, failures, projectiles, legacyBodies;
            public uint epoch, round, revision, surfaceRevision, requiredRevision, captureTick;
            public bool ready, frozen;
            public double serverTime, sampledAt;
            // Published proofs only; retired proofs excluded. Per-proof peaks need not coincide.
            public long admissionMeshCacheQueries, admissionMeshCacheHits, admissionMeshCacheMisses, admissionMeshCacheCapacityClears;
            public int admissionMeshCachePublishedProofs, admissionMeshCacheEntries, admissionMeshCacheSumOfProofPeakEntries;
            public PrototypeMatch.FixedGridSyncStatistics sync;
            public PrototypeMatch.FixedGridPeerStatistics[] peers;
        }
        [Serializable] public sealed class BlockRow
        {
            public ulong id;
            public uint epoch, round, generation, revision;
            public int owner, x, y, z, supportId, baseY, face, u, v;
            public float supportHeight;
        }
        [Serializable] public sealed class Actor
        {
            public ulong owner;
            public uint round;
            public int team, slot, hero, sub, shots, lifeChanges, movementDiscontinuities;
            public double spentMaterial, distanceMoved;
            public float minimumMaterial = 100;
            public string role;
        }
        [Serializable] public sealed class BlastSample
        {
            public ulong owner;
            public uint round, entity, networkTick;
            public long sequence, physicsTick;
            public int removed;
            public double milliseconds;
        }
        [Serializable] public sealed class CpuMetric
        { public string marker; public Metric time; public bool valid; }
        [Serializable] public sealed class CollisionSample
        {
            public ulong owner, action;
            public uint round, life, equipment, entity, ordinal, epoch, revision, surfaceRevision, networkTick;
            public string kind, sourceKind;
            public int count;
            public bool auxiliary, hadContact, ordinaryMain;
            public double serverTime, elapsed, milliseconds;
        }
        [Serializable] public sealed class ReconnectStageSample
        {
            public string stage;
            public int frame;
            public double realtime;
            public float timeScale, deltaTime, unscaledDeltaTime;
            public bool ready, busy, inRoom;
        }
        [Serializable] sealed class Report
        {
            public int probeRevision = 69, checkpointEvidenceSchema;
            public string sourceManifestSha256, buildId;
            public List<IssuedCheckpoint> issuedCheckpoints = new();
            public int schema = 4, evidenceSchema = 4, weaponSimulation = GameplayContentSignature.WeaponSimulationVersion,
                blockProtocol = GameplayContentSignature.BlockProtocolVersion, fixedProtocol = BlockFixedGridCodec.ProtocolVersion,
                simulationVersion = BlockFixedGridWorld.SimulationVersion;
            public float cellSize = BlockWorld.CellSize;
            public string label, session, role = "starting", stage = "boot", mode, unity, graphicsApi;
            public string scope = "Independent Players, production paid inputs, fixed cell identities/support and paired paint; no synthetic block seeding, refills, simulation or visual reductions.";
            public string checkpointOracle = "Compare worldHash only at equal epoch/round/revision/surfaceRevision/frozen; captureTick is recorded, not substituted for the committed fixed version.";
            public string physicsMetric = "Physics.Simulate sums all recorded physics samples in a rendered frame; not a single fixed-step duration.";
            public int configuredPlayers, connectionGenerations, errors, minimumPlayers = int.MaxValue, width, height, renderedFrames, invalidCameraFrames, invalidResolutionFrames;
            public int processorCount, jobWorkerCount, jobWorkerMaximumCount, realShots;
            public bool connected, complete, initialSyncComplete, graphics, equipmentReady, completedNormalRound, frameGatePassed, latencyGatePassed;
            public bool reconnectRequested, reconnectDisconnected, reconnectCleanup, reconnectSuccess;
            public List<ReconnectStageSample> reconnectStages = new();
            public double duration, sampledSeconds, configuredSeconds, elapsedServerSeconds;
            public StateSample current;
            public List<StateSample> states = new();
            public List<BlockRow> finalBlocks = new();
            public List<Actor> actors = new();
            public List<BlastSample> blasts = new();
            public List<string> errorMessages = new();
            public Metric frames, collisionEffective, ordinaryMainCollisionEffective, mainThread, renderThread, physicsFrame, gcBytes;
            public List<double> frameMilliseconds, collisionMilliseconds, ordinaryMainCollisionMilliseconds;
            public List<CollisionSample> collisions = new(16384);
            public List<CpuMetric> cpu = new();
            public WorkloadEvidence workloadEvidence = new();
            public List<ActorWindow> actorWindows = new(256);
            public List<RouteSample> routeSamples = new(8192);
            public List<int> completedFixedStepsPerFrame = new(16384);
            public FrameEvidence frameEvidence = new();
            public PaperSamplingEvidence paperSampling = new();
            public List<FrameRecord> frameRecords = new(FrameRecordCapacity);
            public List<long> cpuRawValues = new();
            public List<byte> cpuRecorderStates = new();
            public List<long> cpuSampleCounts = new();
            public List<PaymentRecord> paidEvents = new();
            public List<ActualRecord> actualEvents = new();
            public BlockProbeCommandReader.Statistics commandReader;
            public BlockProbeReportWriter.Statistics writer;
            public BlockProbeReportWriter.Statistics checkpointProgressWriter;
            public int completedRoundCheckpointCaptures, completedRoundCheckpointRetries;
            public long checkpointFileCleanupFailures;
            public int retainedCheckpointFileCleanupCount;
            public string lastCheckpointFileCleanupError;
            // Completed full encodes before this capture; never part of gameplay
            // CPU summaries. The current capture is included in the next report.
            public int reportFullCaptureCount;
            public double reportFullCaptureTotalMilliseconds, reportFullCaptureMaximumMilliseconds,
                reportFullCaptureLastMilliseconds;
            public double reportFreezeTotalMilliseconds, reportFreezeMaximumMilliseconds;
            public long reportCaptureFailures;
            public BlastPressureReport blastPressure;
        }
        [Serializable] sealed class Progress
        {
            public bool progressOnly = true, initialSyncComplete, equipmentReady, completedNormalRound;
            public string label, session, stage, mode, role, phase, observerFailure, checkpointPayloadSha256;
            public int command, generation, players, fixedBlocks, paintCells, errors, samples, localShots, failures;
            public bool frozen;
            public uint epoch, round, revision, surfaceRevision;
            public double sampledSeconds, elapsedServerSeconds, sampledAt;
            public long completedRoundCheckpointSequence, publishedProgressSequence;
        }
        sealed class Series : IDisposable
        {
            public ProfilerRecorder Recorder;
            public readonly List<double> Values = new(FrameRecordCapacity);
            readonly double _scale;
            readonly bool _requireFresh;
            int _lastRecorderCount;
            public Series(ProfilerCategory category, string marker, double scale = .000001, bool requireFresh = false)
            { _scale = scale; _requireFresh = requireFresh; Recorder = ProfilerRecorder.StartNew(category, marker, RecorderCapacity, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame); }
            // Poll once per app frame, even outside combat. An unchanged count is an
            // uncalled marker, not permission to reuse the last nonzero frame value.
            public RecorderValue Read()
            {
                bool valid = Recorder.Valid, running = valid && Recorder.IsRunning;
                int count = valid ? Recorder.Count : 0;
                bool wrapped = valid && Recorder.WrappedAround;
                bool fresh = running && !wrapped && count > _lastRecorderCount;
                var sample = fresh ? Recorder.GetSample(count - 1) : default;
                _lastRecorderCount = count;
                return new RecorderValue { raw = sample.Value, calls = sample.Count, count = count,
                    valid = valid, running = running, fresh = fresh, wrapped = wrapped,
                    available = running && !wrapped && (fresh || !_requireFresh) };
            }
            public void Append(RecorderValue value)
            { if (value.available) Values.Add(value.raw * _scale); }
            public void Dispose() => Recorder.Dispose();
        }
        readonly List<double> _frames = new(FrameRecordCapacity), _collision = new(16384), _ordinaryMainCollision = new(16384);
        readonly Dictionary<(uint round, ulong owner), Actor> _actors = new();
        readonly Dictionary<(uint round, ulong owner), (uint shots, float ink, Vector3 position, uint life, double at)> _previousActors = new();
        readonly Dictionary<string, int> _checkpoints = new();
        readonly HashSet<PrototypeMatch> _connections = new();
        readonly HashSet<(uint round, ulong owner)> _placed = new();
        readonly List<(CpuMetric metric, Series series)> _cpu = new();
        Series _main, _render, _physics, _gc;
        PrototypeMatch _match;
        Report _report;
        BlockProbeCommandReader _reader;
        BlockProbeReportWriter _writer;
        BlockProbeReportWriter _checkpointProgressWriter;
        BlockProbeReportCaptureQueue _captureQueue;
        sealed class FullCapture
        {
            internal BlockProbeReportCaptureQueue.Job Job;
            internal BlockProbeReportWriter Writer;
            internal bool Checkpoint;
            internal int Lifecycle, Command, Generation;
            internal (BlockFixedGridWorld world, uint epoch, uint round, uint revision, uint surfaceRevision,
                bool frozen, int command, int generation) Version;
        }
        readonly Dictionary<long, FullCapture> _fullCaptures = new();
        FullCapture _pendingCheckpointCapture;
        BlockProbeReportCaptureQueue.Job _terminalCaptureJob;
        int _captureLifecycle;
        bool _terminalCutoff, _terminalFreezeFailed;
        int _terminalLateErrors;
        string _terminalLateError;
        BlockProbeReportFile _checkpointFile;
        readonly List<BlockProbeReportFile> _retiredCheckpointFiles = new();
        string _checkpointHash;
        long _checkpointSequence;
        (BlockFixedGridWorld world, uint epoch, uint round, uint revision, uint surfaceRevision, bool frozen, int command, int generation) _checkpointVersion;
        string _output, _control, _stage = "boot", _terminalHash;
        double _boot, _stageAt, _nextWrite, _sampleAt = -1, _timeout;
        int _command;
        bool _finished, _cycling, _terminalSubmitted, _receiptWritten;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Requested) return;
            var obj = new GameObject("Fixed-grid acceptance probe"); DontDestroyOnLoad(obj); obj.AddComponent<BlockFixedGridAcceptanceProbe>();
        }
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        void Awake()
        {
            _instance = this; _boot = _stageAt = Time.realtimeSinceStartupAsDouble;
            _output = Path.GetFullPath(Arg("-fixedProbeOutput", "fixed-probe.json"));
            _control = Path.GetFullPath(Arg("-fixedProbeControl", "fixed-control.txt"));
            _timeout = double.Parse(Arg("-fixedProbeTimeout", "900"), CultureInfo.InvariantCulture);
            _report = new Report { label = Arg("-inkLabel", "process"), session = Arg("-fixedProbeSession", "missing"),
                mode = Arg("-fixedProbeMode", "network"), configuredPlayers = int.Parse(Arg("-fixedProbePlayers", "2"), CultureInfo.InvariantCulture),
                unity = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString(), graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,
                processorCount = SystemInfo.processorCount, jobWorkerCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount,
                jobWorkerMaximumCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount };
            InitializeStateCheckpoints();
            PaperSamplingDiagnostics.Enable();
            _report.paperSampling.generation = PaperSamplingDiagnostics.Generation;
            _report.paperSampling.baseline = PaperSamplingDiagnostics.ReadCumulative();
            _reader = new BlockProbeCommandReader(_control); _writer = new BlockProbeReportWriter(_output);
            _checkpointProgressWriter = new BlockProbeReportWriter(_output + ".progress.json");
            _captureQueue = new BlockProbeReportCaptureQueue(_output + ".snapshot");
            _observerFaultBaseline = BlockAttackObservation.ObserverFaultCount;
            _main = new Series(ProfilerCategory.Internal, "Main Thread", requireFresh: true);
            _render = new Series(ProfilerCategory.Internal, "Render Thread", requireFresh: true);
            _physics = new Series(ProfilerCategory.Physics, "Physics.Simulate");
            _gc = new Series(ProfilerCategory.Memory, "GC Allocated In Frame", 1, requireFresh: true);
            foreach (string marker in new[] { "Splatoon.Blocks.Tick", "Splatoon.Blocks.Publish", "Splatoon.Blocks.Upload", "Splatoon.Blocks.TransferEncode",
                "Splatoon.Blocks.TransferSend", "Splatoon.Prediction.Reconcile", "Splatoon.Flight.Update", "Splatoon.Paper.Sample", "Splatoon.Paper.Render",
                "Splatoon.Paper.PoseEvaluate", "Splatoon.Paper.SkinBake", "Splatoon.Paper.MeshRead", "Splatoon.Paper.Contour", "Splatoon.Paper.HitMesh", "Splatoon.Paper.HitMeshRestore", "Splatoon.Paper.HitColliderCook",
                "Splatoon.Fixed.BuildImpact", "Splatoon.Fixed.DestroyImpact", "Splatoon.Fixed.Admission", "Splatoon.Fixed.MapSupportIndex",
                "Splatoon.Fixed.Landing", "Splatoon.Fixed.Commit", "Splatoon.Fixed.Commit.Storage", "Splatoon.Fixed.Commit.Index", "Splatoon.Fixed.Commit.Guard",
                "Splatoon.Fixed.Admission.Players", "Splatoon.Fixed.Admission.Map", "Splatoon.Fixed.Admission.Protected",
                "Splatoon.Fixed.Admission.NativeOverlap", "Splatoon.Fixed.Admission.NativePenetration", "Splatoon.Fixed.Admission.ShapeReady",
                "Splatoon.Fixed.RenderUpdate", "Splatoon.Fixed.Safety", "Splatoon.Fixed.CapsuleMove",
                "Splatoon.Player.Motor", "Splatoon.Player.AuthoritySimulate", "Splatoon.Player.Step", "Splatoon.Match.FixedUpdate",
                "Splatoon.Player.Motor.CanStand", "Splatoon.Player.Motor.StepMovement", "Splatoon.Player.Motor.ResolveGround",
                "Splatoon.Player.Motor.ResolveSupport", "Splatoon.Player.Motor.TryGetMapGround", "Splatoon.Fixed.SupportQuery",
                "Splatoon.Player.Motor.WallGeometryFits", "Splatoon.Fixed.TraversalPoseFits", "Splatoon.Fixed.TraversalPositionFits",
                "Splatoon.Fixed.PaperWorldOverlap", "Splatoon.Fixed.SafetyCapsuleQuery", "Splatoon.Fixed.SafetyBoxQuery",
                "Splatoon.FixedProbe.Capture", "Splatoon.FixedProbe.Serialize", "Splatoon.FixedProbe.Targeting", "Splatoon.FixedProbe.FrameEvidence" })
            {
                // Register named markers before the recorder opens, including paths whose
                // declaring type has not yet been initialized in this fresh Player.
                _ = new ProfilerMarker(ProfilerCategory.Scripts, marker);
                var metric = new CpuMetric { marker = marker };
                _report.cpu.Add(metric); _cpu.Add((metric, new Series(ProfilerCategory.Scripts, marker)));
            }
            InitializeFrameEvidence();
            Application.logMessageReceived += Log; RenderPipelineManager.endCameraRendering += Rendered;
            BlockAttackObservation.CollisionEffective += Collision; BlockAttackObservation.BlastCompleted += BlastCompleted;
            BlockAttackObservation.PaymentAccepted += Paid;
            BlockAttackObservation.ActualResultCommitted += Actual;
            if ((_report.mode is "natural" or "blast8") && _report.graphics) Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
        }
        void Log(string message, string stack, LogType type)
        { if (type is LogType.Error or LogType.Exception or LogType.Assert) Error(message); }
        void Error(string message)
        {
            if (_terminalCutoff) { _terminalLateErrors++; _terminalLateError = message; return; }
            _report.errors++; if (_report.errorMessages.Count < 100) _report.errorMessages.Add(message);
        }
        void Rendered(ScriptableRenderContext context, Camera camera)
        {
            if (_hasPendingFrame && _pendingFrame.frameId == Time.frameCount && camera != null && camera.GetInstanceID() == _pendingFrame.cameraId)
                _pendingFrame.mainCameraRendered = true;
        }
        void Collision(BlockAttackObservation.Collision observation)
        {
            var source = observation.Source;
            if (observation.Count <= 0 || !EvidenceTime(source.Round, observation.ServerTime, out double elapsed)) return;
            bool ordinary = source.Kind == PaintAttackKind.Main && !observation.Auxiliary && observation.HadContact;
            _collision.Add(observation.Milliseconds);
            if (ordinary) _ordinaryMainCollision.Add(observation.Milliseconds);
            var window = Window(source.Round, source.Owner, source.Life, source.Equipment, source.Team,
                source.Hero, source.SubWeaponId, elapsed);
            window.effectiveCollisions++;
            window.collisionMaximumMilliseconds = Math.Max(window.collisionMaximumMilliseconds, observation.Milliseconds);
            if (ordinary)
            {
                window.ordinaryMainCollisions++;
                window.ordinaryMainCollisionMaximumMilliseconds = Math.Max(window.ordinaryMainCollisionMaximumMilliseconds, observation.Milliseconds);
            }
            _report.collisions.Add(new CollisionSample { owner = source.Owner, action = source.Action,
                round = source.Round, life = source.Life, equipment = source.Equipment, entity = observation.Entity,
                ordinal = observation.Ordinal, epoch = observation.Epoch, revision = observation.Revision,
                surfaceRevision = observation.SurfaceRevision, networkTick = observation.NetworkTick,
                kind = observation.Kind, sourceKind = source.Kind.ToString(), count = observation.Count,
                auxiliary = observation.Auxiliary, hadContact = observation.HadContact, ordinaryMain = ordinary,
                serverTime = observation.ServerTime, elapsed = elapsed, milliseconds = observation.Milliseconds });
        }
        void BlastCompleted(BlockAttackObservation.Blast observation, int removed)
        {
            if (_finished || _stage is not ("combat" or "blast")) return;
            _report.blasts.Add(new BlastSample { owner = observation.Owner, round = observation.Round, entity = observation.Entity,
                sequence = observation.Sequence, physicsTick = observation.PhysicsTick, networkTick = observation.NetworkTick,
                removed = removed, milliseconds = observation.ElapsedMilliseconds });
            ObservePressureCompletion(observation, removed);
        }
        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            PumpFullCaptures();
            CompletePreviousFrame();
            PollStateCheckpointWriter();
            if (_finished) { if (now >= _nextWrite && Write(true)) Application.Quit(_report.errors == 0 && _terminalLateErrors == 0 ? 0 : 2); return; }
            if (_reader.TryTake(out var command)) Command(command.Sequence, command.Stage);
            if (_finished) return;
            var match = PrototypeMatch.Current;
            if (match != null && match.IsSpawned && match.Blocks?.FixedBlocks != null && PrototypePlayer.Local != null)
            {
                _match = match; if (_connections.Add(match)) _report.connectionGenerations++;
                if (match.IsServer) ReserveAuthorityEventEvidence();
                _report.connected = true; _report.role = match.IsServer ? "host" : "client"; _report.initialSyncComplete = match.InitialSyncComplete;
                if (match.IsServer) { PlaceActors(match); ObserveActors(match); }
                else ObserveActor(match, PrototypePlayer.Local);
                Equipment(match); ObservePressure(match);
                var round = match.State.Value;
                if (_stage == "combat" && round.Phase == MatchPhase.Finished && _sampleAt >= 0)
                { _report.completedNormalRound = round.TerrainFrozenAt >= round.EndsAt; _report.elapsedServerSeconds = round.TerrainFrozenAt - round.StartsAt; }
                if (now >= _nextWrite)
                {
                    _nextWrite = now + .5;
                    // Raw evidence is serialized after combat. Rebuilding the full
                    // frame/event document every half-second would contaminate it.
                    bool timed = _stage == "combat" && round.Phase == MatchPhase.Playing;
                    if (!TryPublishStateCheckpoint(match) && !(round.Phase == MatchPhase.Finished && TryPublishCompletedRoundProgress(match.Blocks.FixedBlocks)))
                    {
                        if (!timed && _fullCaptures.Count == 0) Capture(match);
                        bool checkpoint = _report.mode == "natural" && _stage == "combat" &&
                            round.Phase == MatchPhase.Finished && _report.completedNormalRound && match.Blocks.FixedBlocks.Frozen;
                        if (checkpoint) RememberCompletedRoundVersion(match.Blocks.FixedBlocks);
                        Write(false, timed, checkpoint);
                    }
                }
            }
            else { _report.initialSyncComplete = false; if (now >= _nextWrite) { _nextWrite = now + .5; Write(false, _stage == "combat"); } }
            if (now - _boot > _timeout) { Error("Probe timed out before explicit quit."); Finish(); }
        }
        void Command(int sequence, string stage)
        {
            if (sequence <= _command) return;
            RetryCheckpointFileCleanup();
            ReleaseCheckpointFile();
            _command = sequence; _stage = stage; _stageAt = Time.realtimeSinceStartupAsDouble; _report.stage = stage;
            var match = PrototypeMatch.Current;
            if (match != null && match.IsServer)
            {
                if ((stage is "round-start" or "rebuild") || stage == "combat" && (_report.mode is "natural" or "blast8")) match.StartRound();
                if ((stage is "freeze" or "finish") && match.State.Value.Phase == MatchPhase.Playing)
                { var state = match.State.Value; state.EndsAt = match.NetworkManager.ServerTime.Time - 1; match.State.Value = state; }
                if (stage == "clear") match.ReturnToRoom();
            }
            if (stage == "reconnect" && Array.IndexOf(Environment.GetCommandLineArgs(), "-fixedReconnectParticipant") >= 0 && !_cycling)
            { _cycling = true; Reconnect().Forget(); }
            if (stage == "blast") ResetPressure();
            if (stage == "quit" || stage == "quit-clients" && match != null && !match.IsServer) Finish();
            else
            {
                // Acknowledge the command only after this process actually consumes it.
                // Keep this small live receipt separate from the full world/raw barrier;
                // alignment and terminal verification still require fresh full reports.
                Write(false, true, false, _checkpointProgressWriter);
            }
        }
        void PlaceActors(PrototypeMatch match)
        {
            if (_report.mode != "network" || _stage is not ("seed" or "seed-rebuild")) return;
            foreach (var player in match.Players)
            {
                if (player == null || !player.IsSpawned || !_placed.Add((match.State.Value.Round, player.PlayerId))) continue;
                var state = player.Snapshot.Value; var origin = PrototypeArena.Spawn(state.Team, state.Slot) + new Vector3(0, 4, state.Team == 1 ? 6 : -6);
                int mask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer);
                if (!Physics.Raycast(origin, Vector3.down, out var hit, 14, mask, QueryTriggerInteraction.Ignore)) { Error("No authored floor under paid network seed lane."); continue; }
                player.DiagnosticPlace(hit.point + Vector3.up * .02f, state.Team == 1 ? 0 : 180);
            }
        }
        void ObserveActors(PrototypeMatch match)
        {
            foreach (var player in match.Players) ObserveActor(match, player);
        }
        void ObserveActor(PrototypeMatch match, PrototypePlayer player)
        {
                if (player == null || !player.IsSpawned || player.IsTestBot) return;
                var state = player.IsServer ? player.Snapshot.Value : player.PresentedState; var key = (match.State.Value.Round, player.PlayerId);
                if (!_actors.TryGetValue(key, out var actor))
                { actor = new Actor { owner = player.PlayerId, round = key.Item1, team = state.Team, slot = state.Slot, role = ActorRole(state) }; _actors.Add(key, actor); _report.actors.Add(actor); }
                actor.hero = state.HeroId; actor.sub = PlayerLoadout.From(state).SubWeaponId;
                if (_previousActors.TryGetValue(key, out var previous))
                {
                    if (state.Revision == previous.life)
                    {
                        if (state.ShotSequence >= previous.shots) { int shots = (int)(state.ShotSequence - previous.shots); actor.shots += shots; _report.realShots += shots; }
                        actor.spentMaterial += Math.Max(0, previous.ink - state.Ink);
                        double dt = state.SimulatedAt - previous.at;
                        float distance = Vector3.ProjectOnPlane(PlayerMotorSimulation.HumanPosition(state) - previous.position, Vector3.up).magnitude;
                        var hero = GameplayConfig.GetHero(state.HeroId);
                        float speed = Mathf.Max(hero.MoveSpeed, Mathf.Max(hero.SwimSpeed, hero.AirSwimSpeed));
                        if (distance > .00001f && (dt <= 0 || distance > speed * dt + .75)) actor.movementDiscontinuities++;
                        else actor.distanceMoved += distance;
                    }
                    else actor.lifeChanges++;
                }
                actor.minimumMaterial = Math.Min(actor.minimumMaterial, state.Ink);
                _previousActors[key] = (state.ShotSequence, state.Ink, PlayerMotorSimulation.HumanPosition(state), state.Revision, state.SimulatedAt);
        }
        void Capture(PrototypeMatch match)
        {
            using var marker = CaptureCpu.Auto();
            var terrain = match.Blocks; var world = terrain.FixedBlocks; var round = match.State.Value;
            var snapshot = world.Capture(true); byte[] bytes = BlockFixedGridCodec.Encode(snapshot);
            var rows = snapshot.Upserts.Select(s => new BlockRow { id = s.Handle.Id, epoch = s.Handle.Epoch, round = s.Handle.Round, generation = s.Handle.Generation,
                revision = s.Revision, owner = s.Owner, x = s.Cell.x, y = s.Cell.y, z = s.Cell.z, supportId = s.Support.Id, baseY = s.Support.BaseCellY,
                supportHeight = s.Support.Height, face = s.Support.SurfaceKey.FaceId, u = s.Support.SurfaceKey.U, v = s.Support.SurfaceKey.V }).ToList();
            using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                foreach (var s in snapshot.Upserts)
                { writer.Write(s.Handle.Id); writer.Write(s.Handle.Generation); writer.Write(s.Owner); writer.Write(s.Cell.x); writer.Write(s.Cell.y); writer.Write(s.Cell.z);
                    writer.Write(s.Support.Id); writer.Write(s.Support.BaseCellY); writer.Write(s.Support.Height); writer.Write(s.Support.SurfaceKey.FaceId); writer.Write(s.Support.SurfaceKey.U); writer.Write(s.Support.SurfaceKey.V); }
            var admissionCache = terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            var sample = new StateSample { stage = _stage, phase = round.Phase.ToString(), command = _command, generation = _report.connectionGenerations,
                players = round.PlayerCount, epoch = world.Epoch, round = world.Round, revision = world.Revision, surfaceRevision = world.Surfaces.Revision,
                requiredRevision = round.FixedBlockRevision, captureTick = match.FixedGridCaptureTick, fixedBlocks = world.Count,
                paintCells = world.Surfaces.PinkCount + world.Surfaces.BlueCount, pink = terrain.PinkCount, blue = terrain.BlueCount,
                scorePink = round.PinkBlockCount, scoreBlue = round.BlueBlockCount, pending = terrain.PendingTransactions, failures = terrain.FailedTransactions,
                projectiles = match.IsServer ? match.Projectiles.PendingCount : 0, ready = match.InitialSyncComplete && match.FixedGridBlockSyncReady,
                frozen = world.Frozen, worldHash = Digest(bytes), cellHash = Digest(stream.ToArray()), paintHash = Digest(snapshot.SurfaceBytes),
                admissionMeshCacheQueries = admissionCache.Queries, admissionMeshCacheHits = admissionCache.Hits,
                admissionMeshCacheMisses = admissionCache.Misses, admissionMeshCacheCapacityClears = admissionCache.CapacityClears,
                admissionMeshCachePublishedProofs = admissionCache.Proofs, admissionMeshCacheEntries = admissionCache.Entries,
                admissionMeshCacheSumOfProofPeakEntries = admissionCache.SumOfProofPeakEntries,
                serverTime = match.NetworkManager.ServerTime.Time, sampledAt = Time.realtimeSinceStartupAsDouble,
                sync = match.InspectFixedGridSyncStatistics(), peers = match.InspectFixedGridSyncPeers(), observerFailure = world.LastObserverException,
                legacyBodies = FindObjectsByType<BlockDynamicBody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length };
            _report.current = sample; _report.finalBlocks = rows;
            string key = _command + ":" + sample.generation + ":" + sample.epoch + ":" + sample.round + ":" + sample.revision + ":" + sample.surfaceRevision;
            if (_checkpoints.TryGetValue(key, out int index)) _report.states[index] = sample;
            else { _checkpoints.Add(key, _report.states.Count); _report.states.Add(sample); }
        }
        async UniTaskVoid Reconnect()
        {
            _report.reconnectRequested = true;
            try
            {
                var app = PrototypeApp.Current;
                ReconnectStage("leave-begin");
                await app.Leave();
                if (_finished) return;
                _report.reconnectDisconnected = true;
                ReconnectStage("leave-complete");
                _report.reconnectCleanup = PrototypeMatch.Current == null && FindObjectsByType<BlockDynamicBody>(FindObjectsSortMode.None).Length == 0;
                Write(false);
                ReconnectStage("leave-report-complete");
                // This is a wall-clock reconnect grace period, independent of
                // scaled gameplay time and Maximum Allowed Timestep clamping.
                await UniTask.Delay(TimeSpan.FromSeconds(1), DelayType.Realtime);
                if (_finished) return;
                ReconnectStage("connect-begin");
                await app.Connect(false, Arg("-lanAddress", "127.0.0.1"), ushort.Parse(Arg("-lanPort", "7788"), CultureInfo.InvariantCulture));
                if (_finished) return;
                _report.reconnectSuccess = app.InRoom; if (!app.InRoom) Error("Reconnect failed: " + app.Error);
                ReconnectStage("connect-complete");
            }
            catch (Exception exception) { Error(exception.ToString()); }
            finally { _cycling = false; if (!_finished) { ReconnectStage("reconnect-finally"); Write(false); } }
        }
        void ReconnectStage(string stage)
        {
            if (_finished) return;
            var app = PrototypeApp.Current;
            var sample = new ReconnectStageSample { stage = stage, frame = Time.frameCount,
                realtime = Time.realtimeSinceStartupAsDouble, timeScale = Time.timeScale,
                deltaTime = Time.deltaTime, unscaledDeltaTime = Time.unscaledDeltaTime,
                ready = app != null && app.Ready, busy = app != null && app.Busy, inRoom = app != null && app.InRoom };
            _report.reconnectStages.Add(sample);
            Debug.Log("[FixedProbe.Reconnect] " + JsonUtility.ToJson(sample));
        }
        static string Digest(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string DigestFile(string path)
        { using var sha = SHA256.Create(); using var stream = File.OpenRead(path); return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static Metric Stats(List<double> values)
        {
            if (values.Count == 0) return new Metric(); var sorted = values.ToArray(); Array.Sort(sorted);
            return new Metric { samples = sorted.Length, p95 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)],
                p99 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .99) - 1)], maximum = sorted[^1] };
        }
        void RememberCompletedRoundVersion(BlockFixedGridWorld world)
        { _checkpointVersion = (world, world.Epoch, world.Round, world.Revision, world.Surfaces.Revision, world.Frozen, _command, _report.connectionGenerations); }
        void ReleaseCheckpointFile()
        {
            // Clear the logical checkpoint before IO. An external delete-share
            // lock must not block Command/Finish or leave a stale identity live.
            var file = _checkpointFile; _checkpointFile = null;
            _captureLifecycle++; _pendingCheckpointCapture = null;
            _checkpointHash = null; _checkpointSequence = 0; _checkpointVersion = default;
            if (file == null) return;
            try { file.Dispose(); }
            catch (Exception error)
            {
                // Dispose deliberately keeps a failing last lease live. Transfer
                // that exact lease to observable retirement for lifecycle retry.
                _retiredCheckpointFiles.Add(file); RecordCheckpointFileCleanupFailure(error);
            }
            if (_report != null) _report.retainedCheckpointFileCleanupCount = _retiredCheckpointFiles.Count;
        }
        void RetryCheckpointFileCleanup()
        {
            for (int index = _retiredCheckpointFiles.Count - 1; index >= 0; index--)
            {
                try { _retiredCheckpointFiles[index].Dispose(); _retiredCheckpointFiles.RemoveAt(index); }
                catch (Exception error) { RecordCheckpointFileCleanupFailure(error); }
            }
            if (_report != null) _report.retainedCheckpointFileCleanupCount = _retiredCheckpointFiles.Count;
        }
        void RecordCheckpointFileCleanupFailure(Exception error)
        {
            if (_report == null) return;
            _report.checkpointFileCleanupFailures++;
            _report.lastCheckpointFileCleanupError = error.ToString();
            _report.retainedCheckpointFileCleanupCount = _retiredCheckpointFiles.Count;
            Error("Checkpoint sealed-file cleanup failed: " + error);
        }
        bool TryPublishCompletedRoundProgress(BlockFixedGridWorld world)
        {
            PumpFullCaptures();
            // Only the completed natural-round barrier may retain a point-in-time full
            // checkpoint. Drain/alignment commands must continue taking fresh samples.
            var version = (world, world.Epoch, world.Round, world.Revision, world.Surfaces.Revision,
                world.Frozen, _command, _report.connectionGenerations);
            if (_report.mode != "natural" || _stage != "combat" || !_report.completedNormalRound || !world.Frozen) return false;
            bool pending = _pendingCheckpointCapture != null &&
                _pendingCheckpointCapture.Lifecycle == _captureLifecycle && _pendingCheckpointCapture.Version == version;
            if (!pending && (_checkpointFile == null || _checkpointVersion != version)) return false;
            // A prior progress write completing is not an acknowledgement of this
            // checkpoint. Retry the exact retained bytes only after its writer is idle.
            if (!pending && _writer.WaitForIdle(0) && _writer.ReadStatistics().lastPublishedProgressSequence != _checkpointSequence)
            { _checkpointSequence = _writer.SubmitProgress(_checkpointFile); _report.completedRoundCheckpointRetries++; }
            // Preserve the full barrier on its original path. Raw frame/event series
            // keep collecting; fresh half-second status uses a separate small file.
            Write(false, true, false, _checkpointProgressWriter);
            return true;
        }
        bool Write(bool terminal, bool progressOnly = false, bool retainCompletedRound = false, BlockProbeReportWriter progressWriter = null)
        {
            PumpFullCaptures();
            if (_terminalSubmitted)
            {
                if (!_writer.PollTerminal()) { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false; }
                if (_receiptWritten) return true;
                if (!FinishStateCheckpointWriter()) { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false; }
                try
                {
                    string actual = DigestFile(_output);
                    if (actual != _terminalHash) return false;
                    BlockProbeReportWriter.WriteAtomic(_output + ".writer.json", JsonUtility.ToJson(new Receipt { session = _report.session, label = _report.label,
                        payloadSha256 = actual, terminalPayloadMatches = true, writer = _writer.ReadStatistics(),
                        terminalLateErrors = _terminalLateErrors, terminalLateError = _terminalLateError,
                        checkpointWriterRequired = _stateCheckpointsEnabled, checkpointWriter = _stateCheckpointWriter?.ReadStatistics() ?? default,
                        probeRevision = _report.probeRevision, sourceManifestSha256 = _report.sourceManifestSha256,
                        buildId = _report.buildId })); _receiptWritten = true; return true;
                }
                catch (IOException) { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false; }
            }
            if (terminal && _terminalFreezeFailed) { _nextWrite = Time.realtimeSinceStartupAsDouble + .5; return false; }
            if (terminal && _terminalCaptureJob != null)
            {
                if (_terminalCaptureJob.Completed && _terminalCaptureJob.Failure != null)
                    _captureQueue.RetryTerminal(_terminalCaptureJob);
                _nextWrite = Time.realtimeSinceStartupAsDouble + .1;
                return false;
            }
            // Admission precedes summaries and the detached graph copy. Keep
            // small command/heartbeat receipts flowing while a full encode runs.
            if (!terminal && !progressOnly && _fullCaptures.Count != 0) return false;
            _report.duration = Time.realtimeSinceStartupAsDouble - _boot; _report.commandReader = _reader.ReadStatistics(); _report.writer = _writer.ReadStatistics();
            _report.checkpointProgressWriter = _checkpointProgressWriter.ReadStatistics();
            object payload;
            if (progressOnly)
            {
                var world = _match?.Blocks?.FixedBlocks;
                payload = new Progress { label = _report.label, session = _report.session, stage = _stage, mode = _report.mode, role = _report.role,
                    command = _command, generation = _report.connectionGenerations, errors = _report.errors, samples = _frames.Count,
                    sampledSeconds = _report.sampledSeconds, elapsedServerSeconds = _report.elapsedServerSeconds,
                    initialSyncComplete = _report.initialSyncComplete, equipmentReady = _report.equipmentReady, localShots = _report.realShots,
                    completedNormalRound = _report.completedNormalRound, completedRoundCheckpointSequence = _checkpointSequence,
                    publishedProgressSequence = _report.writer.lastPublishedProgressSequence,
                    checkpointPayloadSha256 = _checkpointHash, sampledAt = Time.realtimeSinceStartupAsDouble,
                    phase = _match?.State.Value.Phase.ToString(), frozen = world?.Frozen ?? false,
                    failures = _match?.Blocks?.FailedTransactions ?? 0, observerFailure = world?.LastObserverException,
                    players = _match?.State.Value.PlayerCount ?? 0, epoch = world?.Epoch ?? 0, round = world?.Round ?? 0, revision = world?.Revision ?? 0,
                    surfaceRevision = world?.Surfaces.Revision ?? 0, fixedBlocks = world?.Count ?? 0, paintCells = (world?.Surfaces.PinkCount ?? 0) + (world?.Surfaces.BlueCount ?? 0) };
            }
            else
            {
                // Full nonterminal snapshots must carry the same raw series as
                // their summaries. Completeness still becomes true only at Finish.
                _report.frameMilliseconds = _frames; _report.collisionMilliseconds = _collision;
                _report.ordinaryMainCollisionMilliseconds = _ordinaryMainCollision;
                _report.frames = Stats(_frames); _report.collisionEffective = Stats(_collision);
                _report.ordinaryMainCollisionEffective = Stats(_ordinaryMainCollision); _report.mainThread = Stats(_main.Values);
                _report.renderThread = Stats(_render.Values); _report.physicsFrame = Stats(_physics.Values); _report.gcBytes = Stats(_gc.Values);
                foreach (var item in _cpu) { item.metric.time = Stats(item.series.Values); item.metric.valid = item.series.Recorder.Valid; }
                _report.frameGatePassed = _report.frames.samples > 0 && _report.frames.p95 <= 22.2 && _report.frames.p99 <= 33.3 &&
                    _report.frameEvidence.rejectedPlayingFrames == 0 && _report.frameEvidence.unknownPhaseFrames == 0 &&
                    _report.frameEvidence.missingFrameIds == 0 && _report.frameEvidence.duplicateFrameIds == 0 &&
                    _report.frameEvidence.recorderWrappedFrames == 0;
                _report.latencyGatePassed = _report.ordinaryMainCollisionEffective.samples > 0 && _report.ordinaryMainCollisionEffective.p95 <= 200; payload = _report;
            }
            if (progressOnly)
            {
                string json; using (SerializeCpu.Auto()) json = JsonUtility.ToJson(payload, false);
                (progressWriter ?? _writer).SubmitProgress(json);
                return true;
            }
            _captureQueue ??= new BlockProbeReportCaptureQueue(_output + ".snapshot");
            if (retainCompletedRound) _report.completedRoundCheckpointCaptures++;
            BlockProbeReportCaptureQueue.Job job;
            bool accepted;
            try { using (SerializeCpu.Auto()) accepted = _captureQueue.TrySubmit(payload, terminal, out job); }
            catch (Exception error)
            {
                if (retainCompletedRound) _report.completedRoundCheckpointCaptures--;
                if (terminal)
                {
                    // A failed terminal freeze has no immutable graph to retry.
                    // Do not silently choose a different evidence cutoff later.
                    _terminalFreezeFailed = true; Error("Terminal report freeze failed: " + error);
                    _nextWrite = Time.realtimeSinceStartupAsDouble + .5; return false;
                }
                throw;
            }
            if (!accepted) { if (retainCompletedRound) _report.completedRoundCheckpointCaptures--; return false; }
            var context = new FullCapture { Job = job, Writer = progressWriter ?? _writer,
                Checkpoint = retainCompletedRound, Lifecycle = _captureLifecycle, Command = _command,
                Generation = _report.connectionGenerations, Version = _checkpointVersion };
            _fullCaptures.Add(job.Sequence, context);
            _report.reportFreezeTotalMilliseconds += job.FreezeMilliseconds;
            _report.reportFreezeMaximumMilliseconds = Math.Max(_report.reportFreezeMaximumMilliseconds, job.FreezeMilliseconds);
            if (retainCompletedRound) _pendingCheckpointCapture = context;
            if (terminal) _terminalCaptureJob = job;
            _nextWrite = Time.realtimeSinceStartupAsDouble + .5;
            return !terminal;
        }
        void PumpFullCaptures()
        {
            if (_captureQueue == null) return;
            while (_captureQueue.TryTakeCompleted(out var job))
            {
                _report.reportFullCaptureCount++;
                _report.reportFullCaptureLastMilliseconds = job.CaptureMilliseconds;
                _report.reportFullCaptureTotalMilliseconds += job.CaptureMilliseconds;
                _report.reportFullCaptureMaximumMilliseconds = Math.Max(_report.reportFullCaptureMaximumMilliseconds, job.CaptureMilliseconds);
                if (!_fullCaptures.TryGetValue(job.Sequence, out var context))
                    throw new InvalidOperationException("A full report completed without its capture identity.");
                if (job.Failure != null)
                {
                    _report.reportCaptureFailures++;
                    Error("Full report encoding failed: " + job.Failure);
                    if (!job.IsTerminal) { _fullCaptures.Remove(job.Sequence); if (_pendingCheckpointCapture == context) _pendingCheckpointCapture = null; }
                    continue;
                }
                _fullCaptures.Remove(job.Sequence);
                if (_pendingCheckpointCapture == context) _pendingCheckpointCapture = null;
                var snapshot = job.File;
                try
                {
                    if (job.IsTerminal)
                    {
                        _terminalHash = snapshot.Sha256; _writer.SubmitTerminal(snapshot); _terminalSubmitted = true;
                        _nextWrite = Time.realtimeSinceStartupAsDouble + .1;
                        continue;
                    }
                    // A command/connection change invalidates an in-flight full
                    // report. Its late bytes must not revive an old checkpoint.
                    if (_finished || context.Lifecycle != _captureLifecycle || context.Command != _command ||
                        context.Generation != _report.connectionGenerations) continue;
                    if (context.Checkpoint)
                    {
                        var world = context.Version.world;
                        if (world == null || _checkpointVersion != context.Version ||
                            _match != null && !ReferenceEquals(_match.Blocks?.FixedBlocks, world) ||
                            context.Version != (world, world.Epoch, world.Round, world.Revision,
                            world.Surfaces.Revision, world.Frozen, _command, _report.connectionGenerations)) continue;
                    }
                    long progressSequence = context.Writer.SubmitProgress(snapshot);
                    if (context.Checkpoint)
                    {
                        var checkpoint = snapshot.Retain();
                        ReleaseCheckpointFile(); _checkpointFile = checkpoint;
                        _checkpointHash = snapshot.Sha256; _checkpointSequence = progressSequence; _checkpointVersion = context.Version;
                    }
                    _nextWrite = Time.realtimeSinceStartupAsDouble + .5;
                }
                finally
                {
                    try { snapshot.Dispose(); }
                    catch (Exception error) { _retiredCheckpointFiles.Add(snapshot); RecordCheckpointFileCleanupFailure(error); }
                }
            }
        }
        [Serializable] sealed class Receipt
        { public string session, label, payloadSha256, terminalLateError; public int terminalLateErrors;
            public bool terminalPayloadMatches; public BlockProbeReportWriter.Statistics writer;
            public bool checkpointWriterRequired; public BlockProbeReportWriter.Statistics checkpointWriter;
            public int probeRevision; public string sourceManifestSha256, buildId; }
        void Finish()
        {
            if (_finished) return;
            RetryCheckpointFileCleanup();
            ReleaseCheckpointFile();
            if (_match != null && _match.IsSpawned) Capture(_match);
            _report.complete = true; _finished = true; _reader.Dispose(); _nextWrite = Time.realtimeSinceStartupAsDouble;
            _report.frameMilliseconds = _frames; _report.collisionMilliseconds = _collision;
            _report.ordinaryMainCollisionMilliseconds = _ordinaryMainCollision;
            CompleteFrameEvidence();
            CompleteWorkloadEvidence();
            // This is the terminal evidence cutoff. Later log callbacks can still
            // fail the process, but encoding/retries use this one detached graph.
            _terminalCutoff = true; Write(true);
        }
        void OnDestroy()
        {
            _captureQueue?.Dispose();
            _stateCheckpointWriter?.Dispose();
            _reader?.Dispose(); _checkpointProgressWriter?.Dispose(); _writer?.Dispose(); _main?.Dispose(); _render?.Dispose(); _physics?.Dispose(); _gc?.Dispose(); foreach (var item in _cpu) item.series.Dispose();
            ReleaseCheckpointFile(); RetryCheckpointFileCleanup();
            Application.logMessageReceived -= Log; RenderPipelineManager.endCameraRendering -= Rendered;
            BlockAttackObservation.CollisionEffective -= Collision; BlockAttackObservation.BlastCompleted -= BlastCompleted;
            BlockAttackObservation.PaymentAccepted -= Paid;
            BlockAttackObservation.ActualResultCommitted -= Actual;
            if (_instance == this) _instance = null;
        }
#endif
    }
}
