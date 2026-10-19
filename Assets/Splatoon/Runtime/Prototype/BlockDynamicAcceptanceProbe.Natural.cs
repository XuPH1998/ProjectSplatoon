#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        [Serializable] public sealed class PhysicsSample
        {
            public double seconds;
            public int total, active, sleeping, parked;
            public int pendingTransactions;
            public double oldestPendingSecondsSimulationClock;
        }
        [Serializable] public sealed class NaturalQueueSample
        {
            public int unityFrame, terrainObjectId, pendingTransactions, publishedKeysInLastBegunBudgetFrame;
            public uint round, revision;
            public long stopwatchStamp, authorityStages, commandProgressStages, commandChecks, commandsQueued, commandsCompleted, cpuDeferrals;
            public double seconds, realtimeSeconds, simulationTime, oldestPendingSecondsSimulationClock;
            public double lastCommandStageMs, synchronousMsInLastBegunBudgetFrame;
            public BlockTerrain.BuildWorkDiagnostics buildWork;
            public BlockTerrain.CommandHeadDiagnostic commandHead;
            public BlockTerrain.PendingHeadDiagnostic pendingHead;
            public BlockTerrain.PendingWorkDiagnostics pendingWork;
        }
        [Serializable] public sealed partial class NaturalReport
        {
            public string input = "production input; normal material payments and recovery; authored side-lane routes";
            public string physicsCountsSource;
            public bool equipmentReady, completedNormalRound, frameGatePassed, latencyGatePassed, valid;
            public double configuredSeconds, elapsedServerSeconds, observedFrameSeconds;
            public int invalidCameraFrames, invalidResolutionFrames, localShots;
            public double localSpentMaterial, localMinimumMaterial = 100;
            public int attachedCollisionSamples, dynamicCollisionSamples, enemyRemovalSamples;
            public int pushForceEvents, pushedBodiesMoved, playersThatPushed, maximumActive, maximumSleeping, maximumParked, maximumBodies;
            public Metric mainThread, renderThread, physicsStep, gcBytes, gpu, contactToCollision, attachedContactToCollision, dynamicContactToCollision, enemyContactToCollision;
            public List<CpuMetric> cpu = new();
            public BlockDynamicWorld.BuildWorkDiagnostics dynamicBuildWork;
            public BlockDynamicWorld.ActivationDiagnostics dynamicActivation;
            public bool activationDiagnosticsAuthority;
            public string activationDiagnosticsSource = "last combat physics sample; authority world cumulative transition counters and CPU total/maximum call, not per-sample deltas; rest requires native sleep and component-exact zero linear/angular motion; managed masses remain dynamic; pendingMotionWakes count only contradictory native sleep with real momentum; replicas cannot distinguish managed sleep from other authority sleeping flags";
            public string paperCountersSource = "PaperCapture process cumulative counters since subsystem/last-capture reset; poseBakeCount counts real BakePose evaluations, deferredRenderBakeCount is its Render subset";
            public long paperSharedPoseCacheHits, paperSharedPoseCacheMisses, paperPoseBakeCount, paperDeferredRenderBakeCount;
            public bool diagnosticCpuCapture, diagnosticBodyStateCapture;
            public string diagnosticBodyStateStatus;
            public List<PhysicsSample> physics = new();
            public string queueObservationSource = "authority host only; at most 1 Hz from the existing PhysicsSample point; O(1) value snapshots; no worker detailed tracking is enabled by this sampler";
            public string queueCounterSemantics = "terrain-instance cumulative counters; Clear does not reset; commandsCompleted includes cancellation; cpuDeferrals counts admission calls, not frames";
            public string queueClockSemantics = "seconds=elapsed server seconds; realtimeSeconds=probe realtime seconds; simulationTime/oldestPendingSecondsSimulationClock=simulation seconds; commandHead.ageMs=simulation milliseconds; pendingHead.ageMs/phaseAgeMs=Stopwatch wall milliseconds valid only when tracked=true; stopwatchStamp uses queueStopwatchFrequency; synchronous ms/keys describe the latest begun budget frame, which may precede unityFrame; spot samples, not interval aggregates";
            public long queueStopwatchFrequency = Stopwatch.Frequency;
            public int queueSampleCapacity = 512, queueSamplesDropped;
            public List<NaturalQueueSample> queue = new();
            public List<WorkloadActor> roles = new();
            public List<WorkloadDecision> localDecisions = new();
            public ulong hostOwner;
            public string movementSource = "authoritative Snapshot HumanPosition; lifecycle and alive-state transitions reset the movement anchor";
            public List<ulong> movedPushOwners = new();
        }
        [Serializable] public sealed class CpuMetric
        {
            public string marker;
            public Metric time;
        }
        [Serializable] public sealed class WorkloadActor
        {
            public ulong owner;
            public int team, slot, hero, sub, shots, pushEvents, surfaceContacts, dynamicContacts, enemyRemovals, explosions;
            public string role;
            public double spent, horizontalMeters;
            public int movementSamples, movementResets, waypointArrivals;
            public List<WaypointArrival> waypointVisits = new();
            public List<PushMovement> movedPushes = new();
            public Vector3 position, nextWaypoint;
            public int routeIndex;
        }
        [Serializable] public sealed class WorkloadDecision
        {
            public ulong owner;
            public int team, slot, routeIndex, firstContactOwner = -1;
            public double seconds;
            public string role, reason, firstContactCollider;
            public Vector3 position, waypoint, enemyTarget, firstContact;
            public bool hasEnemyTarget, fire, recover;
            public float enemyDistance, effectiveRange;
        }
        [Serializable] public sealed class WaypointArrival
        {
            public int index;
            public uint life;
            public double seconds;
            public Vector3 position, waypoint;
        }
        [Serializable] public sealed class PushMovement
        {
            public ulong body;
            public double seconds, elapsedSeconds, horizontalMeters, alongForceMeters;
            public Vector3 from, to, direction;
        }
        sealed class MovementCursor
        {
            public uint life;
            public bool alive;
            public int waypoint;
            public Vector3 position;
        }
        [Serializable] public sealed class BlastBatch
        {
            public long physicsTick, firstStarted, lastCompleted;
            public uint networkTick;
            public int explosions, completed, removed;
            public double maximumCompletionMs, groupCompletionMs, summedQueueCpuMs;
            public List<ulong> owners = new();
        }
        [Serializable] public sealed class BlastReport
        {
            public string source = "real production BlockAttackService.Blast accepted after material payment; grouped by authoritative PhysX step";
            public bool collisionEffective, valid;
            public int queued, completed, removed, maximumSameTick;
            public long stopwatchFrequency = Stopwatch.Frequency;
            public Metric enqueueCpu, collisionCompletion;
            public List<BlastBatch> batches = new();
        }
        sealed partial class Report
        {
            public NaturalReport natural;
            public BlastReport blast;
        }
        sealed class NaturalAgent
        {
            public uint life;
            public int waypoint, lastJump = -1;
            public bool recovering, hasEnemyTarget, enteredRoute;
            public Vector3 enemyTarget;
            public double nextEnemyTarget;
            public double nextDecision;
            public string lastDecision;
        }
        sealed class ProbeSeries
        {
            public ProfilerRecorder recorder;
            public readonly List<double> values = new(16384);
            public double scale;
            public ProbeSeries(ProfilerCategory category, string marker, double multiplier = .000001)
            {
                scale = multiplier;
                recorder = ProfilerRecorder.StartNew(category, marker, 1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
            }
            public void Sample() { if (recorder.Valid) values.Add(recorder.LastValue * scale); }
        }
        readonly Dictionary<ulong, NaturalAgent> _naturalAgents = new();
        readonly Dictionary<ulong, WorkloadActor> _workloadActors = new();
        readonly Dictionary<ulong, MovementCursor> _movementCursors = new();
        readonly Dictionary<(ulong owner, ulong body), (Vector3 position, Vector3 direction, double at)> _pendingPushes = new();
        readonly HashSet<(ulong owner, ulong body)> _confirmedPushes = new();
        readonly HashSet<ulong> _pushedMoved = new(), _pushOwners = new();
        readonly Dictionary<long, BlastBatch> _blastBatches = new();
        readonly List<double> _contactLatency = new(), _surfaceLatency = new(), _dynamicLatency = new(), _enemyLatency = new();
        readonly List<double> _blastQueue = new(), _blastCompletion = new(), _gpuFrames = new();
        readonly List<(ulong owner, ulong body)> _completedPushes = new();
        readonly FrameTiming[] _naturalTiming = new FrameTiming[1];
        ProbeSeries _naturalMain, _naturalRender, _naturalPhysics, _naturalGc;
        readonly List<(CpuMetric metric, ProbeSeries series)> _naturalCpu = new();
        BlockBenchmarkCpuWindowCapture _cpuCapture;
        bool _cpuCaptureStarted, _cpuCaptureClosed;
        double _cpuCaptureAt;
        bool _workload, _naturalSawPlaying, _naturalValidationFinished;
        double _nextPhysicsSample, _nextQueueSample, _naturalStartedServer;
        ulong _naturalGpuTimestamp;
        uint _ownLastShot;
        float _ownLastInk;
        bool _ownObserved;

        partial void InitializeWorkload()
        {
            _workload = _report.mode is "natural" or "blast8";
            if (!_workload) return;
            _report.scope = "same-machine independent Players; production weapons, material costs and recovery; normal 1920x1080 main camera; no block caps or expiry";
            _report.natural = new NaturalReport(); _report.blast = new BlastReport();
            _naturalMain = new ProbeSeries(ProfilerCategory.Internal, "Main Thread");
            _naturalRender = new ProbeSeries(ProfilerCategory.Internal, "Render Thread");
            _naturalPhysics = new ProbeSeries(ProfilerCategory.Physics, "Physics.Simulate");
            _naturalGc = new ProbeSeries(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            foreach (string marker in new[] { "Splatoon.Blocks.Tick", "Splatoon.Blocks.Publish", "Splatoon.Blocks.Upload", "Splatoon.Blocks.Activation",
                "Splatoon.Blocks.DynamicBuild", "Splatoon.Blocks.DynamicRender", "Splatoon.Blocks.TransferEncode",
                "Splatoon.Blocks.TransferSend", "Splatoon.Blocks.DynamicCapture", "Splatoon.Blocks.DynamicStateRead", "Splatoon.Blocks.DynamicStateSort",
                "Splatoon.Blocks.DynamicLifecyclePrepare", "Splatoon.Blocks.DynamicLifecycleEncode", "Splatoon.Blocks.DynamicMotionSelect",
                "Splatoon.Blocks.DynamicMotionEncode", "Splatoon.Blocks.DynamicMotionDecode",
                "Splatoon.Prediction.Reconcile", "Splatoon.Flight.Update", "Splatoon.Paper.Sample", "Splatoon.Paper.Render",
                "Splatoon.Paper.PoseEvaluate", "Splatoon.Paper.SkinBake", "Splatoon.Paper.MeshRead",
                "Splatoon.Paper.Contour", "Splatoon.Paper.HitMesh", "Splatoon.Paper.HitMeshRestore",
                "Splatoon.Probe.Aggregate", "Splatoon.Probe.Serialize", "Splatoon.Probe.Write" })
            {
                var metric = new CpuMetric { marker = marker };
                _report.natural.cpu.Add(metric);
                _naturalCpu.Add((metric, new ProbeSeries(ProfilerCategory.Scripts, marker)));
            }
            _cpuCapture = BlockBenchmarkCpuWindowCapture.CreateIfRequested(_output, "-dynamicProbeCpuProfile");
            _report.natural.diagnosticCpuCapture = _cpuCapture != null;
            if (_report.graphics && (Screen.width != 1920 || Screen.height != 1080))
                Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            BlockAttackObservation.CollisionEffective += CollisionEffective;
            BlockAttackObservation.BlastQueued += BlastQueued;
            BlockAttackObservation.BlastCompleted += BlastCompleted;
            PlayerBlockPushBridge.PushObserved += Pushed;
        }

        static string ActorRole(PlayerSnapshot state) => state.Slot < 2 ? "builder" : state.Slot == 2 ? "enemy-remover" : "explosive";
        PlayerLoadout WorkloadLoadout(PlayerSnapshot state)
        {
            if (_report.mode == "blast8") return new PlayerLoadout(1, 3, 1);
            int hero = state.Slot switch { 0 => 1, 1 => 8, 2 => 4, _ => 5 };
            return new PlayerLoadout(hero, state.Slot == 3 ? 3 : SubWeaponConfigService.Current.DefaultId(hero), 1);
        }
        partial void WorkloadStageChanged(PrototypeMatch match, string stage)
        {
            if (!_workload || stage != "combat" || match == null) return;
            // The combat command starts the unchanged production 180-second round.
            // Network acceptance retains its own round-start lifecycle commands.
            if (match.IsServer && match.State.Value.Phase == MatchPhase.Practice) match.StartRound();
        }
        partial void ObserveWorkload(PrototypeMatch match, double now)
        {
            if (!_workload) return;
            ObserveWorkloadPending(match);
            var local = PrototypePlayer.Local;
            if (local == null) return;
            var own = local.PresentedState;
            var wanted = WorkloadLoadout(own);
            _report.natural.equipmentReady = PlayerLoadout.From(own).Matches(wanted) && !local.HeroChangePending;
            if (match.State.Value.Phase == MatchPhase.Practice && !_report.natural.equipmentReady && !local.HeroChangePending && own.IsAlive)
                local.RequestLoadoutChange(wanted, HeroSelectionOrigin.Warmup);
            if (_stage != "combat") return;
            var round = match.State.Value;
            _report.natural.configuredSeconds = GameplayConfig.Mode.MatchSeconds;
            if (round.Phase == MatchPhase.Finished && _naturalSawPlaying)
            {
                _report.natural.completedNormalRound = round.TerrainFrozenAt >= round.EndsAt;
                _report.natural.elapsedServerSeconds = round.TerrainFrozenAt - _naturalStartedServer;
                return;
            }
            if (round.Phase != MatchPhase.Playing || !match.InitialSyncComplete) return;
            if (!_naturalSawPlaying)
            {
                _naturalSawPlaying = true; _naturalStartedServer = round.StartsAt;
                _ownObserved = false; _report.natural.localMinimumMaterial = own.Ink;
            }
            _report.natural.elapsedServerSeconds = match.NetworkManager.ServerTime.Time - _naturalStartedServer;
            ObserveBlastPressure(match, now);
            _report.natural.observedFrameSeconds += Time.unscaledDeltaTime;
            if (Camera.main == null || Camera.main.targetTexture != null) _report.natural.invalidCameraFrames++;
            if (Screen.width != 1920 || Screen.height != 1080) _report.natural.invalidResolutionFrames++;
            if (_ownObserved)
            {
                if (own.ShotSequence >= _ownLastShot) _report.natural.localShots += (int)(own.ShotSequence - _ownLastShot);
                _report.natural.localSpentMaterial += Math.Max(0, _ownLastInk - own.Ink);
            }
            _ownLastShot = own.ShotSequence; _ownLastInk = own.Ink; _ownObserved = true;
            _report.natural.localMinimumMaterial = Math.Min(_report.natural.localMinimumMaterial, own.Ink);
            _naturalMain.Sample(); _naturalRender.Sample(); _naturalPhysics.Sample(); _naturalGc.Sample();
            foreach (var cpu in _naturalCpu) cpu.series.Sample();
            if (_cpuCapture != null && match.IsServer)
            {
                if (!_cpuCaptureStarted && _report.natural.elapsedServerSeconds >= 25)
                {
                    _cpuCaptureStarted = true; _cpuCaptureAt = now;
                    _cpuCapture.StartWindow("host", Time.frameCount, now, Stopwatch.GetTimestamp());
                }
                if (_cpuCaptureStarted && !_cpuCaptureClosed)
                {
                    _cpuCapture.ObserveSample(_report.sampledFrames);
                    if (now - _cpuCaptureAt >= 8)
                    { _cpuCaptureClosed = true; _cpuCapture.Close("bounded_eight_second_diagnostic", _report.sampledFrames); }
                }
            }
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _naturalTiming) > 0 && _naturalTiming[0].gpuFrameTime > 0 &&
                _naturalTiming[0].frameStartTimestamp != _naturalGpuTimestamp)
            { _naturalGpuTimestamp = _naturalTiming[0].frameStartTimestamp; _gpuFrames.Add(_naturalTiming[0].gpuFrameTime); }
            if (match.IsServer)
                foreach (var player in match.Players)
                {
                    if (player == null || !player.IsSpawned || player.IsTestBot) continue;
                    var state = player.Snapshot.Value;
                    if (!_workloadActors.TryGetValue(player.PlayerId, out var actor))
                    {
                        actor = new WorkloadActor { owner = player.PlayerId, team = state.Team, slot = state.Slot, role = ActorRole(state) };
                        _workloadActors.Add(player.PlayerId, actor); _report.natural.roles.Add(actor);
                    }
                    _report.natural.hostOwner = match.NetworkManager.LocalClientId;
                    ObserveMovement(actor, state, _report.natural.elapsedServerSeconds);
                    actor.hero = state.HeroId; actor.sub = PlayerLoadout.From(state).SubWeaponId;
                    foreach (var measured in _report.actors)
                        if (measured.owner == actor.owner && measured.round == round.Round)
                        { actor.shots = measured.shots; actor.spent = measured.spentMaterial; break; }
                }
            if (now < _nextPhysicsSample) return;
            _nextPhysicsSample = now + .5;
            var world = match.Blocks.Dynamic;
            if (match.IsServer && world.IsAuthority && now >= _nextQueueSample)
            {
                _nextQueueSample = now + 1;
                var terrain = match.Blocks; var report = _report.natural;
                if (report.queue.Count >= report.queueSampleCapacity) report.queueSamplesDropped++;
                else report.queue.Add(new NaturalQueueSample
                {
                    unityFrame = Time.frameCount, terrainObjectId = terrain.GetInstanceID(), round = terrain.Round, revision = terrain.Revision,
                    stopwatchStamp = Stopwatch.GetTimestamp(), seconds = report.elapsedServerSeconds, realtimeSeconds = now, simulationTime = terrain.SimulationTime,
                    pendingTransactions = terrain.PendingTransactions, oldestPendingSecondsSimulationClock = terrain.OldestPendingSeconds,
                    buildWork = terrain.InspectBuildWork(), commandHead = terrain.InspectCommandHead(),
                    pendingHead = terrain.InspectPendingTransaction(), pendingWork = terrain.InspectPendingWork(),
                    authorityStages = terrain.AuthorityStageCount, commandProgressStages = terrain.CommandProgressStages, commandChecks = terrain.CommandWorkChecks,
                    commandsQueued = terrain.CommandsQueued, commandsCompleted = terrain.CommandsCompleted, cpuDeferrals = terrain.PublicationCpuDeferrals,
                    lastCommandStageMs = terrain.LastCommandStageMilliseconds, synchronousMsInLastBegunBudgetFrame = terrain.TerrainSynchronousMilliseconds,
                    publishedKeysInLastBegunBudgetFrame = terrain.LocalPublishedKeysThisFrame
                });
            }
            var sample = new PhysicsSample { seconds = _report.natural.elapsedServerSeconds, total = world.Count,
                pendingTransactions = match.Blocks.PendingTransactions,
                oldestPendingSecondsSimulationClock = match.Blocks.OldestPendingSeconds };
            _report.natural.activationDiagnosticsAuthority = world.IsAuthority;
            if (world.IsAuthority)
            {
                var activation = world.InspectActivation();
                sample.total = activation.total; sample.active = activation.active;
                sample.sleeping = activation.sleeping; sample.parked = activation.parked;
                _report.natural.dynamicActivation = activation;
            }
            else
                foreach (var block in world.Bodies.Values)
                    if (block.RemoteSleeping) sample.sleeping++; else sample.active++;
            _report.natural.physicsCountsSource = world.IsAuthority
                ? "authoritative mutually exclusive counts: active includes raw sleep with nonzero motion; sleeping and parked require native sleep plus component-exact zero linear/angular motion; active + sleeping + parked = total"
                : "replicated authority sleeping flags; parked has no separate wire field and is not observable here (reported parked=0)";
            _report.natural.physics.Add(sample);
            _report.natural.maximumBodies = Math.Max(_report.natural.maximumBodies, sample.total);
            _report.natural.maximumActive = Math.Max(_report.natural.maximumActive, sample.active);
            _report.natural.maximumSleeping = Math.Max(_report.natural.maximumSleeping, sample.sleeping);
            _report.natural.maximumParked = Math.Max(_report.natural.maximumParked, sample.parked);
            _completedPushes.Clear();
            foreach (var pending in _pendingPushes)
            {
                if (!world.TryGet(pending.Key.body, out var body)) { _completedPushes.Add(pending.Key); continue; }
                if (now - pending.Value.at > 2) { _completedPushes.Add(pending.Key); continue; }
                Vector3 horizontal = Vector3.ProjectOnPlane(body.Body.position - pending.Value.position, Vector3.up);
                float alongForce = Vector3.Dot(horizontal, pending.Value.direction);
                if (alongForce < .01f) continue;
                _confirmedPushes.Add(pending.Key); _pushedMoved.Add(pending.Key.body);
                if (_pushOwners.Add(pending.Key.owner)) _report.natural.movedPushOwners.Add(pending.Key.owner);
                if (_workloadActors.TryGetValue(pending.Key.owner, out var actor))
                    actor.movedPushes.Add(new PushMovement { body = pending.Key.body, seconds = _report.natural.elapsedServerSeconds,
                        elapsedSeconds = now - pending.Value.at, horizontalMeters = horizontal.magnitude, alongForceMeters = alongForce,
                        from = pending.Value.position, to = body.Body.position, direction = pending.Value.direction });
                _completedPushes.Add(pending.Key);
            }
            foreach (var key in _completedPushes) _pendingPushes.Remove(key);
            _report.natural.pushedBodiesMoved = _pushedMoved.Count;
            _report.natural.playersThatPushed = _pushOwners.Count;
        }

        void ObserveMovement(WorkloadActor actor, PlayerSnapshot state, double seconds)
        {
            Vector3 position = PlayerMotorSimulation.HumanPosition(state);
            if (!_movementCursors.TryGetValue(actor.owner, out var cursor))
            {
                cursor = new MovementCursor { life = state.Revision, alive = state.IsAlive, position = position };
                _movementCursors.Add(actor.owner, cursor);
            }
            else
            {
                if (cursor.life == state.Revision && cursor.alive && state.IsAlive)
                {
                    float distance = Vector3.ProjectOnPlane(position - cursor.position, Vector3.up).magnitude;
                    if (float.IsFinite(distance)) { actor.horizontalMeters += distance; actor.movementSamples++; }
                }
                else actor.movementResets++;
                if (cursor.life != state.Revision) cursor.waypoint = 0;
                cursor.life = state.Revision; cursor.alive = state.IsAlive; cursor.position = position;
            }
            Vector3 waypoint = RouteWaypoint(state, cursor.waypoint);
            actor.position = position; actor.nextWaypoint = waypoint; actor.routeIndex = cursor.waypoint;
            if (state.IsAlive && Vector3.ProjectOnPlane(waypoint - position, Vector3.up).magnitude < 1.3f)
            {
                actor.waypointVisits.Add(new WaypointArrival { index = cursor.waypoint++, life = state.Revision,
                    seconds = seconds, position = position, waypoint = waypoint });
                actor.waypointArrivals = actor.waypointVisits.Count;
            }
        }
        static Vector2 AimAt(PrototypePlayer player, PlayerSnapshot state, Vector3 target)
        {
            var profile = player.Presentation;
            var pivot = state.Position + PrototypePlayer.CameraPivotOffset(state, profile);
            var look = new Vector2(state.Yaw, state.Pitch);
            for (int i = 0; i < 3; i++)
            {
                var origin = PrototypePlayer.CameraPosition(pivot, Quaternion.Euler(look.y, look.x, 0), profile);
                var delta = target - origin;
                look = new Vector2(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg,
                    Mathf.Clamp(-Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg, -65, 75));
            }
            return look;
        }
        static Vector3 RouteWaypoint(PlayerSnapshot state, int index)
        {
            float side = state.Slot % 2 == 0 ? -1 : 1;
            float team = state.Team == 1 ? 1 : -1;
            float z = (index % 6) switch { 0 => -20, 1 => -10, 2 => 8, 3 => 20, 4 => 8, _ => -10 };
            return new Vector3(side * 11.5f, 0, z * team);
        }
        bool ClosestBlock(PlayerSnapshot state, bool friendly, float radius, out Vector3 point)
        {
            point = default; if (_match == null) return false;
            float best = radius * radius; bool found = false;
            foreach (var body in _match.Blocks.Dynamic.Bodies.Values)
            {
                if ((body.Owner == state.Team) != friendly) continue;
                float distance = (body.Body.position - PlayerMotorSimulation.HumanPosition(state)).sqrMagnitude;
                if (distance >= best) continue;
                best = distance; point = body.Body.position; found = true;
            }
            return found;
        }
        bool ClosestEnemyBlock(PlayerSnapshot state, float radius, out Vector3 point)
        {
            point = default; if (_match == null) return false;
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state);
            float best = radius * radius; bool found = false;
            foreach (var body in _match.Blocks.Dynamic.Bodies.Values)
            {
                if (body.Owner == state.Team) continue;
                float distance = (body.Body.position - feet).sqrMagnitude;
                if (distance >= best) continue;
                best = distance; point = body.Body.position; found = true;
            }
            float width = BlockWorld.ChunkSize * BlockWorld.CellSize;
            foreach (var pair in _match.Blocks.World.Chunks)
            {
                var origin = (Vector3)BlockWorld.ChunkOrigin(pair.Key) * BlockWorld.CellSize;
                var bounds = new Bounds(origin + Vector3.one * (width * .5f), Vector3.one * width);
                if ((bounds.ClosestPoint(feet) - feet).sqrMagnitude >= best) continue;
                var cells = pair.Value.Cells;
                for (int i = 0; i < cells.Length; i++)
                {
                    if (!cells[i].Occupied || cells[i].Owner == state.Team) continue;
                    var center = BlockWorld.Center(BlockWorld.CellFromIndex(pair.Key, i));
                    float distance = (center - feet).sqrMagnitude;
                    if (distance >= best) continue;
                    best = distance; point = center; found = true;
                }
            }
            return found;
        }
        partial void WorkloadInput(PrototypePlayer player, ref PlayerInputFrame input, ref bool handled)
        {
            if (!_workload) return;
            handled = true;
            var state = player.PresentedState; var match = PrototypeMatch.Current;
            if (_stage != "combat" || match == null || match.State.Value.Phase != MatchPhase.Playing || !state.IsAlive) return;
            double now = player.NetworkManager.ServerTime.Time;
            double elapsed = Math.Max(0, now - match.State.Value.StartsAt);
            if (!_naturalAgents.TryGetValue(player.PlayerId, out var agent) || agent.life != state.Revision)
            {
                agent = new NaturalAgent { life = state.Revision };
                _naturalAgents[player.PlayerId] = agent;
            }
            if (_report.mode == "blast8")
            {
                BlastPressureInput(player, state, ref input);
                return;
            }
            var feet = PlayerMotorSimulation.HumanPosition(state);
            var waypoint = RouteWaypoint(state, agent.waypoint);
            Vector3 travel = Vector3.ProjectOnPlane(waypoint - feet, Vector3.up);
            if (travel.magnitude < 1.3f) { agent.waypoint++; waypoint = RouteWaypoint(state, agent.waypoint); travel = Vector3.ProjectOnPlane(waypoint - feet, Vector3.up); }
            // Leave the central spawn shield sideways before approaching the first
            // authored route node. Firing into its own near face creates a blockage.
            if (!agent.enteredRoute)
            {
                if (agent.waypoint > 0) agent.enteredRoute = true;
                else
                {
                    Vector3 exit = Mathf.Abs(feet.z) > 24.8f && Mathf.Abs(feet.x - waypoint.x) > .15f
                        ? new Vector3(waypoint.x, feet.y, feet.z) : waypoint;
                    var outward = Vector3.ProjectOnPlane(exit - feet, Vector3.up);
                    input.Look = AimAt(player, state, feet + outward.normalized * 2 + Vector3.up);
                    var local = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * outward.normalized;
                    input.Move = new Vector2(local.x, local.z) * .7f;
                    RecordNaturalDecision(player, state, agent, elapsed, waypoint, "leave_spawn_shield_without_firing", false, false);
                    return;
                }
            }
            double phase = (elapsed + state.Slot * .55) % 10;
            if (state.Ink < 12) agent.recovering = true;
            if (state.Ink > 78) agent.recovering = false;
            bool recover = agent.recovering || phase >= 6;
            Vector3 direction = travel.normalized;
            float move = recover ? .45f : phase < 1.5 || phase >= 4 ? .7f : 0;
            Vector3 aim = feet + direction * 2 + Vector3.up * .04f;
            if (state.Slot < 2 && phase >= 4 && phase < 6 && ClosestBlock(state, true, 4, out var pushPoint))
            {
                var towardBlock = Vector3.ProjectOnPlane(pushPoint - feet, Vector3.up);
                if (towardBlock.sqrMagnitude > .04f) direction = towardBlock.normalized;
                aim = pushPoint;
            }
            if (state.Slot >= 2)
            {
                if (now >= agent.nextEnemyTarget)
                {
                    agent.nextEnemyTarget = now + .25;
                    agent.hasEnemyTarget = ClosestEnemyBlock(state, 26, out agent.enemyTarget);
                }
                if (agent.hasEnemyTarget) aim = agent.enemyTarget;
            }
            input.Look = AimAt(player, state, aim);
            var localDirection = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * direction;
            input.Move = new Vector2(localDirection.x, localDirection.z) * move;
            input.Swim = recover;
            input.Fire = !recover && !state.AttackNeedsRelease;
            string reason = recover ? "normal_material_recovery" : state.Slot == 3 ? "normal_paid_explosive_fire" : "normal_paid_builder_fire";
            WeaponImpactForecast removalForecast = default;
            int firstOwner = -1;
            if (state.Slot == 2)
            {
                var weapon = GameplayConfig.GetWeapon(state.HeroId);
                reason = "no_enemy_block_in_search_radius";
                bool reachable = false;
                if (agent.hasEnemyTarget)
                {
                    if (Vector3.Distance(feet, agent.enemyTarget) > weapon.EffectiveRange)
                        reason = "enemy_outside_unchanged_weapon_effective_range";
                    else
                    {
                        var aimed = state; aimed.Yaw = input.Look.x; aimed.Pitch = input.Look.y;
                        var solution = _pressureAim.Resolve(player, aimed, aimed.NextMuzzle);
                        removalForecast = weapon.AimMode == WeaponAimMode.WeaponReference
                            ? WeaponImpactPrediction.Guide(_pressureAim, solution, weapon, aimed, player.PlayerId)
                            : WeaponImpactPrediction.Predict(_pressureAim, solution, weapon, aimed, player.PlayerId);
                        var contact = removalForecast.Contact;
                        BlockContact block = default;
                        bool isBlock = contact.BlockQuery.IsKnown ? contact.BlockQuery.TryResolveCurrent(out block) :
                            _match.Blocks.TryContact(contact.Collider, contact.Point, contact.Normal, out block);
                        if (isBlock) firstOwner = block.Owner;
                        reachable = isBlock && block.Owner != state.Team;
                        reason = reachable ? "real_first_trajectory_contact_is_enemy_block" : isBlock
                            ? "real_first_trajectory_contact_is_friendly_block" : contact.Collider != null
                                ? "real_first_trajectory_contact_is_map_or_player" : "no_block_in_real_weapon_trajectory";
                    }
                }
                // The remover follows the same physical route, and only spends a shot
                // when the real centre trajectory reaches an enemy block first.
                input.Fire &= reachable;
                if (recover) reason = "normal_material_recovery; " + reason;
            }
            if (state.Slot == 3)
            {
                double bomb = elapsed % 22;
                // Standing before the press clears the production swim-cancel latch.
                // Preserve standing through release/startup so Swim cannot cancel the paid throw.
                if (bomb >= 14.5 && bomb < 16 || state.SubPhase != SubWeaponPhase.Idle)
                {
                    input.Swim = input.Fire = false;
                    input.SubHeld = bomb >= 15 && bomb < 15.5 &&
                        state.Ink >= PlayerLoadout.SubWeapon(state).Common.inkCost && !state.SubNeedsRelease;
                }
            }
            RecordNaturalDecision(player, state, agent, elapsed, waypoint, reason, input.Fire, recover, removalForecast, firstOwner);
        }
        void RecordNaturalDecision(PrototypePlayer player, PlayerSnapshot state, NaturalAgent agent, double seconds,
            Vector3 waypoint, string reason, bool fire, bool recover, WeaponImpactForecast forecast = default, int firstOwner = -1)
        {
            if (Time.realtimeSinceStartupAsDouble < agent.nextDecision && reason == agent.lastDecision) return;
            agent.nextDecision = Time.realtimeSinceStartupAsDouble + 1; agent.lastDecision = reason;
            _report.natural.localDecisions.Add(new WorkloadDecision { owner = player.PlayerId, team = state.Team,
                slot = state.Slot, role = ActorRole(state), routeIndex = agent.waypoint, seconds = seconds,
                position = PlayerMotorSimulation.HumanPosition(state), waypoint = waypoint, reason = reason,
                enemyTarget = agent.enemyTarget, hasEnemyTarget = agent.hasEnemyTarget,
                enemyDistance = agent.hasEnemyTarget ? Vector3.Distance(PlayerMotorSimulation.HumanPosition(state), agent.enemyTarget) : 0,
                effectiveRange = GameplayConfig.GetWeapon(state.HeroId).EffectiveRange, fire = fire, recover = recover,
                firstContactOwner = firstOwner, firstContact = forecast.Point,
                firstContactCollider = forecast.Contact.Collider != null ? forecast.Contact.Collider.name : "" });
        }
        void CollisionEffective(BlockAttackObservation.Collision observation)
        {
            if (_stage != "combat") return;
            _contactLatency.Add(observation.Milliseconds);
            if (_workloadActors.TryGetValue(observation.Owner, out var actor))
            {
                if (observation.Kind == "surface") actor.surfaceContacts++;
                if (observation.Kind == "dynamic") actor.dynamicContacts++;
                if (observation.Kind == "direct") actor.enemyRemovals += observation.Count;
            }
            if (observation.Kind == "surface") { _surfaceLatency.Add(observation.Milliseconds); _report.natural.attachedCollisionSamples++; }
            if (observation.Kind == "dynamic") { _dynamicLatency.Add(observation.Milliseconds); _report.natural.dynamicCollisionSamples++; }
            if (observation.Kind == "direct") { _enemyLatency.Add(observation.Milliseconds); _report.natural.enemyRemovalSamples++; }
        }
        BlastBatch Batch(BlockAttackObservation.Blast observation)
        {
            if (_blastBatches.TryGetValue(observation.PhysicsTick, out var batch)) return batch;
            batch = new BlastBatch { physicsTick = observation.PhysicsTick, networkTick = observation.NetworkTick, firstStarted = observation.Started };
            _blastBatches.Add(observation.PhysicsTick, batch); _report.blast.batches.Add(batch); return batch;
        }
        void BlastQueued(BlockAttackObservation.Blast observation, int removed)
        {
            if (_stage != "combat") return;
            ObservePressureBlast(observation, removed, false);
            double elapsed = observation.ElapsedMilliseconds; _blastQueue.Add(elapsed);
            var batch = Batch(observation); batch.explosions++; batch.summedQueueCpuMs += elapsed;
            batch.firstStarted = Math.Min(batch.firstStarted, observation.Started);
            if (!batch.owners.Contains(observation.Owner)) batch.owners.Add(observation.Owner);
            if (_workloadActors.TryGetValue(observation.Owner, out var actor)) actor.explosions++;
            _report.blast.queued++; _report.blast.maximumSameTick = Math.Max(_report.blast.maximumSameTick, batch.explosions);
        }
        void BlastCompleted(BlockAttackObservation.Blast observation, int removed)
        {
            if (_stage != "combat") return;
            ObservePressureBlast(observation, removed, true);
            double elapsed = observation.ElapsedMilliseconds; _blastCompletion.Add(elapsed);
            var batch = Batch(observation); batch.completed++; batch.removed += removed;
            batch.lastCompleted = Math.Max(batch.lastCompleted, Stopwatch.GetTimestamp());
            batch.groupCompletionMs = (batch.lastCompleted - batch.firstStarted) * 1000d / Stopwatch.Frequency;
            batch.maximumCompletionMs = Math.Max(batch.maximumCompletionMs, elapsed);
            _report.blast.completed++; _report.blast.removed += removed;
        }
        void Pushed(ulong owner, BlockDynamicBody block, Vector3 force)
        {
            if (_stage != "combat" || block == null || force.sqrMagnitude <= 0) return;
            _report.natural.pushForceEvents++;
            var key = (owner, body: block.Id);
            if (!_confirmedPushes.Contains(key) && !_pendingPushes.ContainsKey(key))
                _pendingPushes.Add(key, (block.Body.position, Vector3.ProjectOnPlane(force, Vector3.up).normalized, Time.realtimeSinceStartupAsDouble));
            if (_workloadActors.TryGetValue(owner, out var actor)) actor.pushEvents++;
        }
        partial void BeforeWorkloadWrite()
        {
            if (!_workload) return;
            CaptureWorkloadPendingEnd();
            var report = _report.natural;
            report.paperSharedPoseCacheHits = PaperCapture.SharedPoseCacheHits;
            report.paperSharedPoseCacheMisses = PaperCapture.SharedPoseCacheMisses;
            report.paperPoseBakeCount = PaperCapture.PoseBakeCount;
            report.paperDeferredRenderBakeCount = PaperCapture.DeferredRenderBakeCount;
            if (_match != null && _match.Blocks?.Dynamic != null) report.dynamicBuildWork = _match.Blocks.Dynamic.InspectBuildWork();
            report.mainThread = Stats(_naturalMain.values); report.renderThread = Stats(_naturalRender.values);
            foreach (var cpu in _naturalCpu) cpu.metric.time = Stats(cpu.series.values);
            report.physicsStep = Stats(_naturalPhysics.values); report.gcBytes = Stats(_naturalGc.values); report.gpu = Stats(_gpuFrames);
            report.contactToCollision = Stats(_contactLatency); report.attachedContactToCollision = Stats(_surfaceLatency);
            report.dynamicContactToCollision = Stats(_dynamicLatency); report.enemyContactToCollision = Stats(_enemyLatency);
            report.latencyGatePassed = report.attachedContactToCollision.samples > 0 && report.dynamicContactToCollision.samples > 0 &&
                report.contactToCollision.p95 <= 200 && report.attachedContactToCollision.p95 <= 200 && report.dynamicContactToCollision.p95 <= 200;
            var frame = Stats(_frames);
            report.frameGatePassed = !report.diagnosticCpuCapture && frame.samples > 0 && frame.p95 <= 22.2 && frame.p99 <= 33.3;
            _report.blast.enqueueCpu = Stats(_blastQueue); _report.blast.collisionCompletion = Stats(_blastCompletion);
            _report.blast.collisionEffective = false;
            foreach (var batch in _report.blast.batches)
                if (batch.explosions >= 8 && batch.completed == batch.explosions && batch.groupCompletionMs <= 2000 && batch.owners.Count >= 8 && batch.removed > 0)
                    _report.blast.collisionEffective = true;
            _report.blast.valid = _report.blast.maximumSameTick >= 8 && _report.blast.collisionEffective;
        }
        partial void FinishWorkload()
        {
            if (!_workload || _naturalValidationFinished) return;
            _naturalValidationFinished = true;
            // Persist the native capture receipt outside the measured combat window.
            // Close restores Profiler state; Write is an independent lifecycle step.
            if (_cpuCapture != null)
            {
                if (!_cpuCaptureClosed)
                { _cpuCaptureClosed = true; _cpuCapture.Close("workload_finished", _report.sampledFrames); }
                _cpuCapture.Write();
            }
            BeforeWorkloadWrite();
            var natural = _report.natural;
            bool common = _report.configuredPlayers == 8 && _report.minimumPlayers >= 8 && _report.graphics &&
                natural.invalidCameraFrames == 0 && natural.invalidResolutionFrames == 0 &&
                _report.renderedFrames > 0 && natural.localShots > 0 && natural.localSpentMaterial > 0;
            if (_report.mode == "natural")
            {
                natural.valid = common && natural.completedNormalRound && natural.configuredSeconds == 180 &&
                    natural.elapsedServerSeconds >= 179.9 && natural.frameGatePassed;
                if (_report.role == "host")
                {
                    int[] builders = new int[3], removers = new int[3], explosives = new int[3];
                    bool roles = natural.roles.Count == 8;
                    var movingOwners = new HashSet<ulong>();
                    foreach (var actor in natural.roles)
                    {
                        roles &= actor.shots > 0 && actor.spent > 0 && double.IsFinite(actor.horizontalMeters) &&
                            actor.horizontalMeters > 1 && actor.movementSamples > 0 && movingOwners.Add(actor.owner);
                        if (actor.team < 1 || actor.team > 2) { roles = false; continue; }
                        if (actor.role == "builder") { builders[actor.team]++; roles &= actor.surfaceContacts > 0 && actor.dynamicContacts > 0; }
                        if (actor.role == "enemy-remover") { removers[actor.team]++; roles &= actor.enemyRemovals > 0; }
                        if (actor.role == "explosive") { explosives[actor.team]++; roles &= actor.explosions > 0; }
                    }
                    for (int team = 1; team <= 2; team++) roles &= builders[team] == 2 && removers[team] == 1 && explosives[team] == 1;
                    bool remotePushed = false;
                    foreach (var actor in natural.roles)
                        if (actor.owner != natural.hostOwner && actor.movedPushes.Count > 0) remotePushed = true;
                    natural.valid &= roles && movingOwners.Count == 8 && movingOwners.Contains(natural.hostOwner) && remotePushed && natural.attachedCollisionSamples > 0 && natural.dynamicCollisionSamples > 0 &&
                        natural.enemyRemovalSamples > 0 && _report.blast.completed > 0 && natural.pushedBodiesMoved > 0 && natural.latencyGatePassed;
                }
                if (!natural.valid) Error("Natural acceptance failed: inspect frame, full-round, role, real material, actual impact and actual push metrics.");
            }
            else
            {
                natural.valid = common;
                if (!common || _report.role == "host" && !_report.blast.valid)
                    Error("Blast8 acceptance failed: require eight real explosions in one authoritative PhysX step and collision completion <=2000ms.");
            }
        }
        partial void DisposeWorkload()
        {
            if (!_workload) return;
            DisposeWorkloadPending();
            BlockAttackObservation.CollisionEffective -= CollisionEffective;
            BlockAttackObservation.BlastQueued -= BlastQueued; BlockAttackObservation.BlastCompleted -= BlastCompleted;
            PlayerBlockPushBridge.PushObserved -= Pushed;
            _naturalMain.recorder.Dispose(); _naturalRender.recorder.Dispose(); _naturalPhysics.recorder.Dispose(); _naturalGc.recorder.Dispose();
            foreach (var cpu in _naturalCpu) cpu.series.recorder.Dispose();
            if (_cpuCapture != null && !_cpuCaptureClosed)
            { _cpuCaptureClosed = true; _cpuCapture.Close("probe_disposed", _report.sampledFrames); }
            _cpuCapture?.Write();
        }
    }
}
#endif
