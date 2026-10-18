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
    /// <summary>Explicit development Player acceptance for surface deposits and free rigidbodies.</summary>
    public sealed partial class BlockDynamicAcceptanceProbe : MonoBehaviour
    {
        public static bool ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_instance == null || !Requested) return false;
            _instance.Input(player, ref input); return true;
#else
            return false;
#endif
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static readonly bool Requested = Array.IndexOf(Environment.GetCommandLineArgs(), "-blockDynamicProbe") >= 0;
        static BlockDynamicAcceptanceProbe _instance;
        static readonly ProfilerMarker ProbeAggregateCpu = new("Splatoon.Probe.Aggregate");
        static readonly ProfilerMarker ProbeSerializeCpu = new("Splatoon.Probe.Serialize");
        static readonly ProfilerMarker ProbeWriteCpu = new("Splatoon.Probe.Write");
        static readonly ProfilerMarker ProbeCommandCpu = new("Splatoon.Probe.CommandRead");
        static readonly ProfilerMarker ProbeCaptureCpu = new("Splatoon.Probe.Capture");
        static readonly ProfilerMarker ProbeWorkloadCpu = new("Splatoon.Probe.WorkloadObserve");
        [Serializable] public sealed class StateSample
        {
            public string stage, phase, surfaceHash, identityHash, frozenHash;
            public int command, generation, players, surfaces, dynamic, pink, blue, scorePink, scoreBlue, pending, failures, projectiles;
            public uint round, terrainRound, revision, dynamicRevision, requiredDynamicRevision, dynamicCaptureTick;
            public bool ready, frozen;
            public double serverTime, frozenAt, sampledAt;
        }
        [Serializable] public sealed class MotionReceipt
        {
            public string hash, stage;
            public ulong peer, sequence;
            public uint round, revision, tick;
            public bool sending;
            public int rows, appliedRows, staleRows, mismatches;
            public double positionError, rotationError;
        }
        [Serializable] public sealed class DynamicSyncSample
        {
            public string stage;
            public int command, generation;
            public double serverTime;
            public PrototypeMatch.DynamicSyncStatistics statistics;
            public PrototypeMatch.DynamicPeerStatistics[] peers;
        }
        [Serializable] public sealed class Actor
        {
            public ulong owner;
            public uint round;
            public int shots;
            public double spentMaterial, minimumMaterial = 100;
            public int hero;
        }
        [Serializable] public sealed class Metric
        {
            public int samples;
            public double p95, p99, maximum;
        }
        [Serializable] public sealed class FrozenPoseReport
        {
            public string source = "authoritative frozen wire snapshot compared independently against every actual native Rigidbody pose and motion state";
            public bool valid;
            public int samples, missingBodies, nonKinematic, gravityEnabled, nonZeroMotion;
            public double maximumPositionError, maximumBasisError, maximumLinearSpeed, maximumAngularSpeed;
            public ulong firstRawPoseDifference;
            public BlockDynamicState firstCanonical, firstNative;
        }
        [Serializable] sealed partial class Report
        {
            public int schema = 1, weaponSimulation = GameplayContentSignature.WeaponSimulationVersion;
            public int blockProtocol = BlockWorld.ProtocolVersion, dynamicProtocol = BlockDynamicCodec.ProtocolVersion;
            public int rangeActivationVersion = BlockDynamicWorld.RangeActivationVersion;
            public float activationRadius = BlockDynamicWorld.ActivationRadius, deactivationRadius = BlockDynamicWorld.DeactivationRadius;
            public string activationDistanceMetric = "xz-horizontal";
            public string label, session, role = "starting", stage = "boot", mode, unity, graphicsApi;
            public string scope = "same-machine independent Players; real normal input and material costs; controlled lane placement; no old falling-run fixture";
            public int configuredPlayers, connectionGenerations, errors, minimumPlayers = int.MaxValue;
            public bool connected, initialSyncComplete, complete, reconnectRequested, reconnectDisconnected, reconnectCleanup, reconnectSuccess;
            public bool graphics, normalMainCamera;
            public int width, height, renderedFrames, sampledFrames, materialRefills;
            public int processorCount, jobWorkerCount, jobWorkerMaximumCount;
            public long blockBytesSent, dynamicBytesSent;
            public long createdBodies, removedBodies;
            public string reportWriteMode = "background single writer; latest progress; terminal publication before quit";
            public BlockProbeReportWriter.Statistics reportWriterBeforeLastEnqueue;
            public string commandReadMode = "background 100ms file poll; ordered single slot; main-thread memory consumption only";
            public BlockProbeCommandReader.Statistics commandReader;
            public PrototypeMatch.DynamicSyncStatistics dynamicSync;
            public PrototypeMatch.DynamicPeerStatistics[] dynamicPeers;
            public List<DynamicSyncSample> dynamicSyncSamples = new();
            public int dynamicSyncSampleOverflow;
            public double duration, sampledSeconds, cellSize = BlockWorld.CellSize;
            public StateSample current;
            public List<StateSample> states = new();
            public List<Actor> actors = new();
            public List<MotionReceipt> motion = new();
            public List<double> corrections = new();
            public int correctionEvents, movingCorrectionSamples;
            public Metric frames, publication, correction;
            public FrozenPoseReport frozenPose;
            public List<string> errorMessages = new();
        }
        // Only scalar progress and the latest state cross the filesystem during
        // timed workloads. All original evidence remains in Report until the
        // next non-combat/full write; no percentile samples are discarded.
        [Serializable] sealed class ProgressReport
        {
            public bool progressOnly = true;
            public int schema, weaponSimulation, blockProtocol, dynamicProtocol, rangeActivationVersion;
            public float activationRadius, deactivationRadius;
            public double cellSize, duration, sampledSeconds;
            public string label, session, role, stage, mode, activationDistanceMetric;
            public int configuredPlayers, connectionGenerations, errors, minimumPlayers;
            public bool connected, initialSyncComplete, complete, graphics, normalMainCamera;
            public int width, height, renderedFrames, sampledFrames, materialRefills;
            public int processorCount, jobWorkerCount, jobWorkerMaximumCount;
            public long createdBodies, removedBodies;
            public BlockProbeCommandReader.Statistics commandReader;
            public StateSample current;
            public List<string> errorMessages;
            public ProgressNatural natural = new();
        }
        [Serializable] sealed class ProgressNatural
        {
            public bool equipmentReady, completedNormalRound, diagnosticCpuCapture, diagnosticBodyStateCapture;
            public string diagnosticBodyStateStatus;
            public double configuredSeconds, elapsedServerSeconds, localSpentMaterial;
            public int localShots;
        }
        [Serializable] sealed class ReportWriteReceipt
        {
            public string session, label, outputPath, payloadSha256, publishedUtc;
            public bool terminalPayloadMatches;
            public BlockProbeReportWriter.Statistics writer;
        }
        readonly List<BlockDynamicState> _states = new();
        readonly List<double> _frames = new(), _publication = new();
        readonly Dictionary<string, int> _checkpoints = new();
        readonly HashSet<PrototypeMatch> _connections = new();
        readonly HashSet<(uint round, ulong owner)> _placed = new();
        readonly Dictionary<(uint round, ulong owner), Actor> _actors = new();
        readonly Dictionary<(uint round, ulong owner), (uint shots, float ink)> _previousActors = new();
        readonly Dictionary<(int generation, uint round, uint life), (uint ack, Vector3 position)> _correctionPrevious = new();
        PrototypeMatch _match;
        Report _report;
        BlockProbeReportWriter _reportWriter;
        BlockProbeCommandReader _commandReader;
        string _terminalPayloadSha256;
        bool _terminalReceiptWritten;
        readonly ProgressReport _progress = new();
        string _output, _control, _stage = "boot";
        int _command, _renderedFrame = -1;
        double _boot, _stageAt, _nextPoll, _nextWrite, _sampleAt = -1, _timeoutSeconds;
        bool _finished, _cycling;
        bool _frozenPoseErrorReported;
        partial void InitializeWorkload();
        partial void ObserveWorkload(PrototypeMatch match, double now);
        partial void WorkloadStageChanged(PrototypeMatch match, string stage);
        partial void WorkloadInput(PrototypePlayer player, ref PlayerInputFrame input, ref bool handled);
        partial void BeforeWorkloadWrite();
        partial void FinishWorkload();
        partial void DisposeWorkload();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Requested) return;
            var obj = new GameObject("Dynamic block acceptance probe"); DontDestroyOnLoad(obj); obj.AddComponent<BlockDynamicAcceptanceProbe>();
        }
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
        void Awake()
        {
            _instance = this; _boot = _stageAt = Time.realtimeSinceStartupAsDouble;
            _output = Path.GetFullPath(Arg("-dynamicProbeOutput", "dynamic-probe.json"));
            _control = Path.GetFullPath(Arg("-dynamicProbeControl", "dynamic-control.txt"));
            _timeoutSeconds = double.Parse(Arg("-dynamicProbeTimeout", "600"), CultureInfo.InvariantCulture);
            _report = new Report { label = Arg("-inkLabel", "process"), session = Arg("-dynamicProbeSession", "missing"),
                configuredPlayers = int.Parse(Arg("-dynamicProbePlayers", "2"), CultureInfo.InvariantCulture),
                mode = Arg("-dynamicProbeMode", "network"), unity = Application.unityVersion,
                processorCount = SystemInfo.processorCount,
                jobWorkerCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount,
                jobWorkerMaximumCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null };
            _commandReader = new BlockProbeCommandReader(_control, _command);
            Application.logMessageReceived += Log; PrototypePlayer.ReconcileObserved += Reconcile;
            RenderPipelineManager.endCameraRendering += Rendered; InitializeWorkload();
            if (BlockBodyStateDiagnostic55.Initialize(_output, _report.session, _report.label, _report.mode))
            {
                _report.natural ??= new NaturalReport();
                _report.natural.diagnosticBodyStateCapture = true;
                _report.natural.diagnosticBodyStateStatus = BlockBodyStateDiagnostic55.Status;
            }
            InitializeVisual(); Write();
        }
        void Log(string message, string stack, LogType type)
        { if (type is LogType.Error or LogType.Exception or LogType.Assert) Error(message); }
        void Error(string message)
        { _report.errors++; if (_report.errorMessages.Count < 100) _report.errorMessages.Add(message); }
        void Observe(PrototypeMatch match)
        {
            if (_match == match) return;
            if (_match != null)
            { _match.DynamicMotionObserved -= Motion; _match.Blocks.MutationCompleted -= Mutation; _match.Blocks.Dynamic.BodiesBuilt -= BodiesBuilt; }
            ClearEnemyExecutionBindings();
            _match = match;
            if (match == null) return;
            if (_connections.Add(match)) _report.connectionGenerations++;
            match.DynamicMotionObserved += Motion; match.Blocks.MutationCompleted += Mutation; match.Blocks.Dynamic.BodiesBuilt += BodiesBuilt;
        }
        void Mutation(BlockMutationResult result, double milliseconds)
        { if (_stage == "combat" && result.OrdinaryMainAttack && result.Added > 0) _publication.Add(milliseconds); }
        void BodiesBuilt(int count, double milliseconds)
        { if (_stage == "combat" && count > 0) _publication.Add(milliseconds); }
        void Rendered(ScriptableRenderContext context, Camera camera)
        {
            if (_stage != "combat" || _match == null || _match.State.Value.Phase != MatchPhase.Playing || camera != Camera.main || _renderedFrame == Time.frameCount) return;
            _renderedFrame = Time.frameCount; _report.renderedFrames++;
        }
        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            BlockBodyStateDiagnostic55.Poll(PrototypeMatch.Current, _finished);
            if (_report.natural != null && _report.natural.diagnosticBodyStateCapture)
                _report.natural.diagnosticBodyStateStatus = BlockBodyStateDiagnostic55.Status;
            if (_finished) { if (now >= _nextWrite && Write()) Application.Quit(_report.errors == 0 ? 0 : 2); return; }
            if (now >= _nextPoll) { _nextPoll = now + .1; using (ProbeCommandCpu.Auto()) ReadCommand(); }
            if (_finished) return;
            var match = PrototypeMatch.Current;
            if (match != null && match.IsSpawned && match.Blocks?.Dynamic != null && PrototypePlayer.Local != null)
            {
                Observe(match); _report.connected = true; _report.role = match.IsServer ? "host" : "client";
                _report.initialSyncComplete = match.InitialSyncComplete;
                _report.blockBytesSent = match.BlockBytesSent; _report.dynamicBytesSent = match.DynamicBlockBytesSent;
                if (match.IsServer) { PlaceActors(match); ObserveActors(match); }
                ObserveEnemyPlan(match);
                using (ProbeWorkloadCpu.Auto()) ObserveWorkload(match, now);
                ObserveVisual(match, now);
                if (_stage == "combat" && match.State.Value.Phase == MatchPhase.Playing && match.InitialSyncComplete)
                {
                    if (_sampleAt < 0) _sampleAt = now;
                    _frames.Add(Time.unscaledDeltaTime * 1000); _report.sampledFrames = _frames.Count;
                    _report.sampledSeconds = now - _sampleAt;
                    _report.minimumPlayers = Math.Min(_report.minimumPlayers, match.State.Value.PlayerCount);
                    _report.width = Screen.width; _report.height = Screen.height;
                    _report.normalMainCamera = Camera.main != null && Camera.main.targetTexture == null;
                }
                if (now >= _nextWrite) { _nextWrite = now + .5; Capture(match); Write(); }
            }
            else { _report.initialSyncComplete = false; if (now >= _nextWrite) { _nextWrite = now + .5; Write(); } }
            if (now - _boot > _timeoutSeconds)
            { Error("Probe timeout before an explicit quit command."); Finish(); }
        }
        void ReadCommand()
        {
            if (_finished) return;
            // Lazy construction also supports fixtures that install this disabled
            // component with an explicit control path, without invoking Awake.
            _commandReader ??= new BlockProbeCommandReader(_control, _command);
            _report.commandReader = _commandReader.ReadStatistics();
            if (!_commandReader.TryTake(out var command) || command.Sequence <= _command) return;
            _command = command.Sequence; _stage = command.Stage; _stageAt = Time.realtimeSinceStartupAsDouble; _report.stage = _stage;
            var match = PrototypeMatch.Current;
            InitializeEnemyPlan(_stage == "enemy" && match != null && match.IsServer ? match : null);
            WorkloadStageChanged(match, _stage);
            VisualStageChanged(_stage);
            if (match != null && match.IsServer)
            {
                if (_stage is "round-start" or "rebuild") match.StartRound();
                if (_stage == "finish" && match.State.Value.Phase == MatchPhase.Playing)
                { var state = match.State.Value; state.EndsAt = match.NetworkManager.ServerTime.Time - 1; match.State.Value = state; }
                if (_stage == "clear") match.ReturnToRoom();
            }
            if (_stage == "reconnect" && Array.IndexOf(Environment.GetCommandLineArgs(), "-dynamicReconnectParticipant") >= 0 && !_cycling)
            { _cycling = true; Reconnect().Forget(); }
            if (_stage == "quit" || _stage == "quit-clients" && match != null && !match.IsServer) Finish();
        }
        static Vector3 Lane(PlayerSnapshot state)
        {
            var origin = PrototypeArena.Spawn(state.Team, state.Slot) + new Vector3(0, 4, state.Team == 1 ? 6 : -6);
            int mask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 14, mask, QueryTriggerInteraction.Ignore))
                throw new InvalidOperationException("No authored floor under the controlled input lane.");
            return hit.point;
        }
        void PlaceActors(PrototypeMatch match)
        {
            if (_report.mode != "network") return;
            if (_stage is not ("seed" or "combat" or "seed-rebuild")) return;
            foreach (var player in match.Players)
                if (player != null && player.IsSpawned && _placed.Add((match.State.Value.Round, player.PlayerId)))
                {
                    try { player.DiagnosticPlace(Lane(player.Snapshot.Value) + Vector3.up * .02f, player.Snapshot.Value.Team == 1 ? 0 : 180); }
                    catch (Exception exception) { Error(exception.Message); }
                }
        }
        void ObserveActors(PrototypeMatch match)
        {
            foreach (var player in match.Players)
            {
                if (player == null || !player.IsSpawned || player.IsTestBot) continue;
                var state = player.Snapshot.Value; var key = (match.State.Value.Round, player.PlayerId);
                if (!_actors.TryGetValue(key, out var actor))
                { actor = new Actor { owner = player.PlayerId, round = key.Item1, hero = state.HeroId }; _actors.Add(key, actor); _report.actors.Add(actor); }
                if (_previousActors.TryGetValue(key, out var previous))
                {
                    if (state.ShotSequence >= previous.shots) actor.shots += (int)(state.ShotSequence - previous.shots);
                    actor.spentMaterial += Math.Max(0, previous.ink - state.Ink);
                }
                actor.minimumMaterial = Math.Min(actor.minimumMaterial, state.Ink);
                _previousActors[key] = (state.ShotSequence, state.Ink);
            }
            _report.createdBodies = match.Blocks.Dynamic.CreatedCount; _report.removedBodies = match.Blocks.Dynamic.RemovedCount;
        }
        void Input(PrototypePlayer player, ref PlayerInputFrame input)
        {
            var state = player.PresentedState;
            input.Fire = input.Swim = input.SubHeld = false; input.Move = Vector2.zero;
            input.CancelFire = false; input.CancelSub = false;
            input.Look = new Vector2(state.Team == 1 ? 0 : 180, 75); input.JumpSequence = state.ConsumedJump;
            if (_stage == "visual-proof") { VisualInput(player, state, ref input); return; }
            bool handled = false; WorkloadInput(player, ref input, ref handled); if (handled) return;
            if (_stage == "enemy")
            {
                EnemyInput(player, state, ref input); return;
            }
            if (_stage is not ("seed" or "combat" or "seed-rebuild") || !state.IsAlive) return;
            double time = player.NetworkManager.ServerTime.Time;
            double cycle = time % 3;
            input.Swim = cycle >= 2;
            input.Fire = cycle < 1.8 && !state.AttackNeedsRelease;
            if (_stage == "combat") input.Move = new Vector2(Mathf.Sin((float)time * .8f) * .35f, Mathf.Cos((float)time * .65f) * .25f);
        }
        static string Digest(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        void Capture(PrototypeMatch match)
        {
            using var captureMarker = ProbeCaptureCpu.Auto();
            _report.dynamicSync = match.InspectDynamicSyncStatistics();
            if (_report.mode == "network")
            {
                _report.dynamicPeers = match.InspectDynamicSyncPeers();
                if (_report.dynamicSyncSamples.Count < 1500)
                    _report.dynamicSyncSamples.Add(new DynamicSyncSample { stage = _stage, command = _command,
                        generation = _report.connectionGenerations, serverTime = match.NetworkManager.ServerTime.Time,
                        statistics = _report.dynamicSync, peers = _report.dynamicPeers });
                else _report.dynamicSyncSampleOverflow++;
            }
            var terrain = match.Blocks; var dynamic = terrain.Dynamic;
            var stateSnapshot = match.State.Value;
            var sample = new StateSample { stage = _stage, command = _command, generation = _report.connectionGenerations,
                phase = stateSnapshot.Phase.ToString(), players = stateSnapshot.PlayerCount, round = stateSnapshot.Round,
                terrainRound = terrain.Round, revision = terrain.Revision, dynamicRevision = dynamic.LifecycleRevision,
                requiredDynamicRevision = stateSnapshot.DynamicBlockRevision, dynamicCaptureTick = match.DynamicBlockCaptureTick,
                surfaces = terrain.World.PinkCount + terrain.World.BlueCount,
                dynamic = dynamic.Count, pink = terrain.PinkCount, blue = terrain.BlueCount,
                scorePink = stateSnapshot.PinkBlockCount, scoreBlue = stateSnapshot.BlueBlockCount,
                pending = terrain.PendingTransactions, failures = terrain.FailedTransactions,
                projectiles = match.IsServer ? match.Projectiles.PendingCount : 0,
                ready = match.InitialSyncComplete && match.DynamicBlockSyncReady, frozen = dynamic.Frozen,
                serverTime = match.NetworkManager.ServerTime.Time, frozenAt = stateSnapshot.TerrainFrozenAt,
                sampledAt = Time.realtimeSinceStartupAsDouble };
            // Keep world sorting, serialization and SHA work outside the measured natural workload.
            bool measuredWorkload = _report.mode != "network" && _stage == "combat" && stateSnapshot.Phase == MatchPhase.Playing;
            if (!measuredWorkload)
            {
                dynamic.CaptureStates(_states);
                using var ids = new MemoryStream(); using (var writer = new BinaryWriter(ids, Encoding.UTF8, true))
                    foreach (var state in _states) { writer.Write(state.Id); writer.Write(state.Owner); }
                sample.surfaceHash = Digest(terrain.World.EncodeSnapshot()); sample.identityHash = Digest(ids.ToArray());
                sample.frozenHash = dynamic.Frozen ? Digest(BlockDynamicCodec.Encode(dynamic.Round, _states, Array.Empty<ulong>())) : "moving";
                if (dynamic.Frozen) ValidateActualFrozenBodies(dynamic);
            }
            _report.current = sample;
            string key = _stage + ":" + sample.round + ":" + sample.revision + ":" + sample.dynamicRevision + ":" + sample.generation;
            if (_checkpoints.TryGetValue(key, out int index)) _report.states[index] = sample;
            else { _checkpoints.Add(key, _report.states.Count); _report.states.Add(sample); }
        }
        void ValidateActualFrozenBodies(BlockDynamicWorld world)
        {
            var proof = new FrozenPoseReport();
            foreach (var state in _states)
            {
                if (!world.TryGet(state.Id, out var body) || body.Body == null || body.Collider == null || !body.Collider.enabled)
                { proof.missingBodies++; continue; }
                proof.samples++;
                if (!body.Body.isKinematic) proof.nonKinematic++;
                if (body.Body.useGravity) proof.gravityEnabled++;
                Vector3 position = body.Body.position; Quaternion rotation = body.Body.rotation;
                double positionError = Vector3.Distance(position, state.Position), basisError = 0;
                Quaternion expected = Quaternion.Normalize(state.Rotation);
                basisError = Math.Max(basisError, Vector3.Distance(rotation * Vector3.right, expected * Vector3.right));
                basisError = Math.Max(basisError, Vector3.Distance(rotation * Vector3.up, expected * Vector3.up));
                basisError = Math.Max(basisError, Vector3.Distance(rotation * Vector3.forward, expected * Vector3.forward));
                double linear = body.Body.linearVelocity.magnitude, angular = body.Body.angularVelocity.magnitude;
                proof.maximumPositionError = Math.Max(proof.maximumPositionError, positionError);
                proof.maximumBasisError = Math.Max(proof.maximumBasisError, basisError);
                proof.maximumLinearSpeed = Math.Max(proof.maximumLinearSpeed, linear);
                proof.maximumAngularSpeed = Math.Max(proof.maximumAngularSpeed, angular);
                if (linear > .000001 || angular > .000001) proof.nonZeroMotion++;
                if (proof.firstRawPoseDifference == 0 && (!position.Equals(state.Position) || !rotation.Equals(state.Rotation)))
                {
                    proof.firstRawPoseDifference = state.Id; proof.firstCanonical = state;
                    proof.firstNative = new BlockDynamicState { Id = body.Id, Round = world.Round, Owner = body.Owner,
                        Position = position, Rotation = rotation, Velocity = body.Body.linearVelocity,
                        AngularVelocity = body.Body.angularVelocity, Sleeping = body.Body.IsSleeping() };
                }
            }
            proof.valid = proof.samples == world.Count && proof.missingBodies == 0 && proof.nonKinematic == 0 &&
                proof.gravityEnabled == 0 && proof.nonZeroMotion == 0 &&
                double.IsFinite(proof.maximumPositionError) && proof.maximumPositionError <= .000001 &&
                double.IsFinite(proof.maximumBasisError) && proof.maximumBasisError <= .00001 &&
                double.IsFinite(proof.maximumLinearSpeed) && double.IsFinite(proof.maximumAngularSpeed);
            _report.frozenPose = proof;
            if (!proof.valid && !_frozenPoseErrorReported)
            { _frozenPoseErrorReported = true; Error("Frozen canonical snapshot does not match the actual native Rigidbody poses/kinematic/zero-motion state."); }
        }
        void Motion(ulong peer, BlockDynamicMotion motion, bool sending)
        {
            if (_finished || _report.mode != "network") return;
            try
            {
                var receipt = new MotionReceipt { peer = peer, sequence = motion.Sequence, round = motion.Round, revision = motion.Revision, tick = motion.Tick,
                    sending = sending, rows = motion.States.Length, stage = _stage,
                    hash = Digest(BlockDynamicCodec.Encode(motion.Round, motion.States, Array.Empty<ulong>())) };
                if (!sending)
                    foreach (var state in motion.States)
                    {
                        if (!_match.Blocks.Dynamic.TryGet(state.Id, out var body)) continue;
                        if (body.MotionSequence > motion.Sequence) { receipt.staleRows++; continue; }
                        double position = Vector3.Distance(body.Body.position, state.Position);
                        double angle = Quaternion.Angle(body.Body.rotation, state.Rotation);
                        receipt.appliedRows++; receipt.positionError = Math.Max(receipt.positionError, position); receipt.rotationError = Math.Max(receipt.rotationError, angle);
                        if (body.Owner != state.Owner || position > .0001 || angle > .02 ||
                            (body.RemoteVelocity - state.Velocity).sqrMagnitude > .000001f ||
                            (body.RemoteAngularVelocity - state.AngularVelocity).sqrMagnitude > .000001f) receipt.mismatches++;
                    }
                _report.motion.Add(receipt);
            }
            catch (Exception exception) { Error("Motion observer: " + exception.Message); }
        }
        void Reconcile(PrototypePlayer player, PrototypePlayer.ReconcileTelemetryEvent observation)
        {
            if (_finished || player == null || player.IsServer || _stage != "combat") return;
            _report.correctionEvents++;
            var key = (_report.connectionGenerations, observation.TerrainRound, observation.Authority.Life);
            bool previous = _correctionPrevious.TryGetValue(key, out var before);
            _correctionPrevious[key] = (observation.Authority.AcknowledgedInput, observation.Authority.Position);
            if (!previous || observation.Lifecycle || !observation.InitialSyncComplete || observation.Authority.AcknowledgedInput == before.ack
                || Vector3.Distance(observation.Authority.Position, before.position) < .001f) return;
            _report.movingCorrectionSamples++; _report.corrections.Add(observation.RawDistance);
        }
        async UniTaskVoid Reconnect()
        {
            _report.reconnectRequested = true;
            try
            {
                var app = PrototypeApp.Current; await app.Leave(); _report.reconnectDisconnected = true;
                _report.reconnectCleanup = PrototypeMatch.Current == null && FindObjectsByType<BlockDynamicBody>(FindObjectsSortMode.None).Length == 0;
                Write(); await UniTask.Delay(TimeSpan.FromSeconds(1));
                await app.Connect(false, Arg("-lanAddress", "127.0.0.1"), ushort.Parse(Arg("-lanPort", "7788"), CultureInfo.InvariantCulture));
                _report.reconnectSuccess = app.InRoom; if (!app.InRoom) Error("Reconnect failed: " + app.Error);
            }
            catch (Exception exception) { Error(exception.ToString()); }
            finally { _cycling = false; Write(); }
        }
        static Metric Stats(List<double> values)
        {
            if (values.Count == 0) return new Metric();
            var sorted = values.ToArray(); Array.Sort(sorted);
            return new Metric { samples = sorted.Length, p95 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)],
                p99 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .99) - 1)], maximum = sorted[sorted.Length - 1] };
        }
        ProgressReport CaptureProgress()
        {
            var p = _progress; var r = _report; var n = r.natural;
            p.schema = r.schema; p.weaponSimulation = r.weaponSimulation; p.blockProtocol = r.blockProtocol; p.dynamicProtocol = r.dynamicProtocol;
            p.rangeActivationVersion = r.rangeActivationVersion; p.activationRadius = r.activationRadius; p.deactivationRadius = r.deactivationRadius;
            p.activationDistanceMetric = r.activationDistanceMetric; p.cellSize = r.cellSize;
            p.label = r.label; p.session = r.session; p.role = r.role; p.stage = r.stage; p.mode = r.mode;
            p.configuredPlayers = r.configuredPlayers; p.connectionGenerations = r.connectionGenerations;
            p.errors = r.errors; p.errorMessages = r.errorMessages; p.minimumPlayers = r.minimumPlayers;
            p.connected = r.connected; p.initialSyncComplete = r.initialSyncComplete; p.complete = r.complete;
            p.graphics = r.graphics; p.normalMainCamera = r.normalMainCamera;
            p.width = r.width; p.height = r.height; p.renderedFrames = r.renderedFrames; p.sampledFrames = r.sampledFrames; p.materialRefills = r.materialRefills;
            p.processorCount = r.processorCount; p.jobWorkerCount = r.jobWorkerCount; p.jobWorkerMaximumCount = r.jobWorkerMaximumCount;
            p.createdBodies = r.createdBodies; p.removedBodies = r.removedBodies; p.current = r.current;
            p.commandReader = r.commandReader;
            p.duration = r.duration; p.sampledSeconds = r.sampledSeconds;
            p.natural.equipmentReady = n.equipmentReady; p.natural.completedNormalRound = n.completedNormalRound;
            p.natural.configuredSeconds = n.configuredSeconds; p.natural.elapsedServerSeconds = n.elapsedServerSeconds;
            p.natural.localShots = n.localShots; p.natural.localSpentMaterial = n.localSpentMaterial; p.natural.diagnosticCpuCapture = n.diagnosticCpuCapture;
            p.natural.diagnosticBodyStateCapture = n.diagnosticBodyStateCapture; p.natural.diagnosticBodyStateStatus = n.diagnosticBodyStateStatus;
            return p;
        }
        bool Write()
        {
            // Seal the terminal report only after diagnostic output has completed or reached its bounded deadline.
            if (_finished && !BlockBodyStateDiagnostic55.ReadyToQuit())
            { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false; }
            if (_report.natural != null && _report.natural.diagnosticBodyStateCapture)
                _report.natural.diagnosticBodyStateStatus = BlockBodyStateDiagnostic55.Status;
            _reportWriter ??= new BlockProbeReportWriter(_output);
            if (_finished && _reportWriter.ReadStatistics().terminalAccepted)
            {
                bool published = _reportWriter.PollTerminal();
                if (!published) _nextWrite = Time.realtimeSinceStartupAsDouble + .1;
                if (!published) return false;
                return WriteTerminalReceipt();
            }
            _report.reportWriterBeforeLastEnqueue = _reportWriter.ReadStatistics();
            if (_commandReader != null) _report.commandReader = _commandReader.ReadStatistics();
            _report.duration = Time.realtimeSinceStartupAsDouble - _boot;
            bool progressOnly = !_finished && (_report.mode is "natural" or "blast8") && _stage == "combat" &&
                _match != null && _match.IsSpawned && _match.State.Value.Phase == MatchPhase.Playing;
            if (!progressOnly)
            {
                using var marker = ProbeAggregateCpu.Auto();
                BeforeWorkloadWrite();
                _report.frames = Stats(_frames); _report.publication = Stats(_publication); _report.correction = Stats(_report.corrections);
            }
            var motion = _report.motion; var corrections = _report.corrections;
            try
            {
                if (!progressOnly && !_finished) { _report.motion = null; _report.corrections = null; }
                string json;
                using (ProbeSerializeCpu.Auto()) json = JsonUtility.ToJson(progressOnly ? CaptureProgress() : (object)_report, false);
                using (ProbeWriteCpu.Auto())
                {
                    if (_finished)
                    {
                        _terminalPayloadSha256 = Digest(Encoding.UTF8.GetBytes(json));
                        _reportWriter.SubmitTerminal(json);
                    }
                    else _reportWriter.SubmitProgress(json);
                }
                if (!_finished) return true;
                _nextWrite = Time.realtimeSinceStartupAsDouble + .1;
                return _reportWriter.PollTerminal() && WriteTerminalReceipt();
            }
            catch (IOException) { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false; }
            finally { _report.motion = motion; _report.corrections = corrections; }
        }
        bool WriteTerminalReceipt()
        {
            if (_terminalReceiptWritten) return true;
            try
            {
                // This barrier is after the measured workload. A complete report alone
                // cannot stand in for the acknowledgement that its disk publication finished.
                string actual = Digest(File.ReadAllBytes(_output));
                if (actual != _terminalPayloadSha256) throw new IOException("Terminal report payload changed before publication acknowledgement.");
                var receipt = new ReportWriteReceipt { session = _report.session, label = _report.label,
                    outputPath = _output, payloadSha256 = actual, publishedUtc = DateTime.UtcNow.ToString("o"),
                    terminalPayloadMatches = true, writer = _reportWriter.ReadStatistics() };
                BlockProbeReportWriter.WriteAtomic(_output + ".writer.json", JsonUtility.ToJson(receipt, false));
                _terminalReceiptWritten = true; return true;
            }
            catch (IOException) { _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false; }
        }
        void Finish()
        {
            if (_finished) return;
            FinishWorkload();
            BlockBodyStateDiagnostic55.Close();
            if (_match != null && _match.IsSpawned) Capture(_match);
            _finished = true; _report.complete = true; _nextWrite = Time.realtimeSinceStartupAsDouble;
            CloseCommandReader();
        }
        void CloseCommandReader()
        {
            _commandReader?.Dispose();
            if (_report != null && _commandReader != null) _report.commandReader = _commandReader.ReadStatistics();
        }
        void OnDestroy()
        {
            BlockBodyStateDiagnostic55.Destroy();
            CloseCommandReader();
            _reportWriter?.Dispose();
            Application.logMessageReceived -= Log; PrototypePlayer.ReconcileObserved -= Reconcile;
            DisposeWorkload();
            DisposeVisual();
            RenderPipelineManager.endCameraRendering -= Rendered; Observe(null); if (_instance == this) _instance = null;
        }
#endif
    }
}
