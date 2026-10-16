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
#endif

namespace Splatoon.Prototype
{
    /// <summary>Explicit Player-only fixture and telemetry. Normal sessions never create this component.</summary>
    public sealed partial class BlockNetworkProbe : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static readonly bool RequestedFromArguments = Array.IndexOf(Environment.GetCommandLineArgs(), "-blockNetworkProbe") >= 0;
#endif
        public static bool Requested
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return RequestedFromArguments;
#else
                return false;
#endif
            }
        }
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
        [Serializable] public sealed class TerrainSample
        {
            public string stage, chunkHash, fallingHash, fallingOffsetHash, snapshotHash, phase;
            public uint round, terrainRound, revision;
            public int players, pink, blue, total, chunks, fallingRuns, fallingCells, pending, failedTransactions, connectionGeneration;
            public int scorePink, scoreBlue;
            public int controlCommand, projectilePending, subPending, specialPending, unstableColumns;
            public long commandsQueued, commandsCompleted;
            public double drainQuietSeconds, oldestPendingMs;
            public BlockTerrain.CommandHeadDiagnostic commandHead;
            public List<DrainInputSample> authorityInputs;
            public int safetyRecoveries, safetyRecoveryFailures;
            public bool initialSyncComplete;
            public double serverTime, frozenAt, terrainTime;
        }
        [Serializable] public sealed class CorrectionSample
        {
            public string stage;
            public uint acknowledgedInput, life, terrainRevision;
            public bool friendlySupport, terrainPending;
            public double serverTime, metres, movedMetres;
        }
        [Serializable] public sealed class ReconcileStateSample
        {
            public Vector3 position, humanPosition, velocity, planarVelocity, groundNormal, paperCenter;
            public Quaternion paperRotation;
            public Vector3Int supportCell;
            public uint life, acknowledgedInput, heroRevision, requiredSupportRevision, appliedSupportRevision;
            public int heroId;
            public byte team, supportOwner;
            public float health, airHumanOffset, cameraRebaseOffset, verticalSpeed;
            public double simulatedAt;
            public string movement, paperPose, lifeState;
            public bool grounded, swimming, compactBody, inputTimedOut, canMove, supportPresent, terrainAvailable;

            internal ReconcileStateSample(PrototypePlayer.ReconcileTelemetryState state)
            {
                position = state.Position; humanPosition = state.HumanPosition; velocity = state.Velocity;
                planarVelocity = state.PlanarVelocity; verticalSpeed = state.VerticalSpeed; groundNormal = state.GroundNormal;
                paperCenter = state.PaperCenter; paperRotation = state.PaperRotation; supportCell = state.SupportCell;
                life = state.Life; acknowledgedInput = state.AcknowledgedInput; heroRevision = state.HeroRevision;
                requiredSupportRevision = state.RequiredSupportRevision; appliedSupportRevision = state.AppliedSupportRevision;
                heroId = state.HeroId; team = state.Team; supportOwner = state.SupportOwner; health = state.Health;
                airHumanOffset = state.AirHumanOffset; cameraRebaseOffset = state.CameraRebaseOffset; simulatedAt = state.SimulatedAt;
                movement = state.Movement.ToString(); paperPose = state.PaperPose.ToString(); lifeState = state.LifeState.ToString();
                grounded = state.Grounded; swimming = state.Swimming; compactBody = state.CompactBody;
                inputTimedOut = state.InputTimedOut; canMove = state.CanMove; supportPresent = state.SupportPresent;
                terrainAvailable = state.TerrainAvailable;
            }
        }
        [Serializable] public sealed class ReconcileEventSample
        {
            public long eventId;
            public ulong owner, networkObjectId;
            public int playerObjectId, connectionGeneration;
            public string stage, phase, origin;
            public uint terrainRound, terrainRevision, appliedPaintSequence;
            public ReconcileStateSample oldPredicted, authority, replayed;
            public double realtime, startedRealtime, serverTime, terrainTime, terrainWaitSeconds, oldestPendingSeconds;
            public double metres, humanMetres, unadjustedRawMetres, unadjustedHumanMetres;
            public double predictedMotionMetres, authorityMotionMetres;
            public int replaySteps, inputHistoryBefore, inputHistoryAfter, terrainPendingTransactions, unstableColumns;
            public int safetyRecoveries, safetyRecoveryFailures;
            public bool lifecycle, movementChanged, hard, initialSyncComplete, networkTimeAvailable, terrainAvailable;
            public bool terrainPendingBefore, terrainPendingAfter, syncPendingBefore, syncPendingAfter;
            public bool paintPendingBefore, paintPendingAfter, terrainWaitDurationKnown, friendlySupport;
            public bool hasPreviousObservation, newAcknowledgement, sameAcknowledgement;
        }
        [Serializable] public sealed class SafetyRecoveryEventSample
        {
            public long eventId;
            public ulong owner, networkObjectId;
            public int playerObjectId, connectionGeneration, recoveryCount, failureCount;
            public uint terrainRound, terrainRevision;
            public string stage, phase;
            public ReconcileStateSample before, after;
            public double realtime, serverTime, terrainTime, rawMetres, humanMetres;
            public bool capsuleEmbedded, paperEmbedded, recovered, noRawDisplacement, noHumanDisplacement, networkTimeAvailable;
        }
        [Serializable] public sealed class MotorStateSample
        {
            public Vector3 position, humanPosition, velocity, planarVelocity, groundNormal, paperCenter;
            public Vector3 wallNormal, wallPoint, mantleFrom, mantleTo;
            public Quaternion paperRotation;
            public Vector3Int supportCell;
            public uint life, acknowledgedInput, heroRevision, requiredSupportRevision, simulationTick, consumedJump;
            public int heroId;
            public byte team;
            public float health, airHumanOffset, cameraRebaseOffset, verticalSpeed;
            public double simulatedAt, attackRecoveryUntil, subRecoveryUntil, mantleStartedAt;
            public string movement, paperPose, lifeState, weaponPhase;
            public bool grounded, swimming, compactBody, inputTimedOut, canMove, swimWasHeld, friendlyInkContact;

            internal MotorStateSample(PlayerSnapshot state)
            {
                // Copy captured values only. No current-world or Physics query enters this DTO.
                position = state.Position; humanPosition = PlayerMotorSimulation.HumanPosition(state);
                velocity = state.Velocity; planarVelocity = state.PlanarVelocity; groundNormal = state.GroundNormal;
                paperCenter = state.PaperCenter; paperRotation = state.PaperRotation; supportCell = state.SupportCell;
                wallNormal = state.WallNormal; wallPoint = state.WallPoint; mantleFrom = state.MantleFrom; mantleTo = state.MantleTo;
                life = state.Revision; acknowledgedInput = state.AcknowledgedInput; heroRevision = state.HeroRevision;
                requiredSupportRevision = state.SupportTerrainRevision; simulationTick = state.SimulationTick; consumedJump = state.ConsumedJump;
                heroId = state.HeroId; team = state.Team; health = state.Health;
                airHumanOffset = state.AirHumanOffset; cameraRebaseOffset = state.CameraRebaseOffset; verticalSpeed = state.VerticalSpeed;
                simulatedAt = state.SimulatedAt; attackRecoveryUntil = state.AttackRecoveryUntil;
                subRecoveryUntil = state.SubRecoveryUntil; mantleStartedAt = state.MantleStartedAt;
                movement = state.Movement.ToString(); paperPose = state.PaperPose.ToString(); lifeState = state.LifeState.ToString();
                weaponPhase = state.WeaponPhase.ToString(); grounded = state.Grounded; swimming = state.Swimming;
                compactBody = state.CompactBody; inputTimedOut = state.InputTimedOut; canMove = state.CanMove;
                swimWasHeld = state.SwimWasHeld; friendlyInkContact = state.FriendlyInkContact;
            }
        }
        [Serializable] public sealed class MotorBranchEventSample
        {
            public long eventId;
            public ulong invocation, owner, networkObjectId;
            public int playerObjectId, connectionGeneration;
            public uint terrainRound, terrainRevision, simulationTick, life;
            public string stage, phase, branch;
            public MotorStateSample before, after;
            public PlayerInputFrame input;
            public bool identityAvailable, probeEvaluated, probeSucceeded, controllerGrounded, standingEvaluated, standingFits;
            public bool wantsFire, shootingMovement;
            public int collisions, contactSurfaceId, contactRegionId;
            public Vector3 requestedMove, controllerPosition, contactPoint, contactNormal;
            public Vector3Int contactCell;
            public byte contactOwner;
            public uint contactRevision;
            public bool contactIsBlock, contactStable, contactClimbable;
            public float rebaseHeight;
            public double simulatedAt, realtime, terrainTime;
        }
        [Serializable] sealed class Report
        {
            public int schema = 1;
            public int reconcileEventSchema = 1, correctionSampleSchema = 1;
            public int safetyRecoveryEventSchema = 1;
            public int motorBranchEventSchema = 1;
            public bool motorTraceEnabled, motorBranchEventsComplete = true, motorBranchEventsIncluded;
            public int motorBranchConsumerErrors, motorBranchProviderErrors, motorBranchEventCount;
            public string motorTraceScope = "opt-in -blockMotorTrace; immutable motor branches; same-process invocation/time association only; independent of formal correction statistics";
            public string motorBranchProviderLastError;
            public string primaryCorrectionSource = "reconcileEvents: immutable reconciliation observations; metres is the unchanged raw correction";
            public string correctionSampleScope = "legacy Update polling retained for comparison; pending and intermediate/same-ack events may be absent";
            public string reconcileMotionScope = "motion between consecutive reconciliation events for the same player, life, terrain round and connection; first observation has no baseline";
            public string safetyCounterScope = "local terrain authority counters; client zero does not rule out host recovery";
            public bool reconcileEventsComplete = true;
            public bool reconcileEventsIncluded;
            public int reconcileConsumerErrors, reconcileEventCount;
            public bool safetyRecoveryEventsComplete = true, safetyRecoveryEventsIncluded;
            public int safetyRecoveryConsumerErrors, safetyRecoveryEventCount;
            public string reconcilePersistence = "all events retained in memory and included in the final report; live reports publish count and completeness only";
            public string label, role, session, probeScope = "same-machine independent Players; opt-in fixture telemetry; performance diagnostic only";
            public bool complete, connected, initialSyncComplete, reconnectRequested, reconnectDisconnected, reconnectCleanup, reconnectSuccess;
            public int configuredPlayers, connectionGenerations, errors, fixtureAdded, fixtureRemoved, friendlyMovementSamples, movingAckSamples;
            public double duration, firstReadySeconds = -1;
            public long blockBytesSent;
            public bool drainInputObserved;
            public uint drainInputSequence, drainInputLife;
            public ulong drainInputOwner;
            public FallCoverageReport fallCoverage;
            public TerrainSample current;
            public List<TerrainSample> states = new();
            public List<CorrectionSample> corrections = new();
            public List<ReconcileEventSample> reconcileEvents = new();
            public List<SafetyRecoveryEventSample> safetyRecoveryEvents = new();
            public List<MotorBranchEventSample> motorBranchEvents = new();
            public List<string> motorBranchErrorMessages = new();
            public List<string> errorMessages = new();
        }
        static BlockNetworkProbe _instance;
        readonly Dictionary<string, int> _stateIndices = new();
        readonly Dictionary<ulong, Vector3> _lanes = new();
        readonly HashSet<(uint round, ulong owner)> _seeded = new();
        readonly HashSet<PrototypeMatch> _matches = new();
        readonly Dictionary<(int player, uint life, uint round, int connection), ReconcileMotionReference> _reconcileMotion = new();
        readonly struct ReconcileMotionReference
        {
            public readonly uint Ack;
            public readonly Vector3 AuthorityPosition, PredictedPosition;
            public ReconcileMotionReference(PrototypePlayer.ReconcileTelemetryEvent observation)
            { Ack = observation.Authority.AcknowledgedInput; AuthorityPosition = observation.Authority.Position; PredictedPosition = observation.Replayed.Position; }
        }
        Report _report;
        PrototypeMatch _match;
        PrototypePlayer _observedPlayer;
        uint _ack, _life, _hashedRound = uint.MaxValue, _hashedRevision = uint.MaxValue;
        string _chunkHash, _fallingHash, _snapshotHash, _output, _control, _stage = "boot";
        int _command, _exitCode;
        double _boot, _nextPoll, _nextWrite, _nextPulse;
        Vector3 _lastPosition;
        bool _cycling, _finished, _motorTraceEnabled;
        int _motorBranchProviderBaseline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Requested) return;
            var obj = new GameObject("Block network acceptance probe"); DontDestroyOnLoad(obj); obj.AddComponent<BlockNetworkProbe>();
        }
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
        void Awake()
        {
            _instance = this; _boot = Time.realtimeSinceStartupAsDouble;
            _output = Path.GetFullPath(Arg("-blockProbeOutput", "Reports/BlockTerrain/network/process.json"));
            _control = Path.GetFullPath(Arg("-blockProbeControl", "Reports/BlockTerrain/network/control.txt"));
            _report = new Report { label = Arg("-inkLabel", "process"), session = Arg("-blockProbeSession", Guid.NewGuid().ToString("N")),
                configuredPlayers = int.Parse(Arg("-blockProbePlayers", "2"), CultureInfo.InvariantCulture) };
            PrototypePlayer.ReconcileObserved += OnReconcileObserved;
            BlockTerrain.SafetyRecoveryObserved += OnSafetyRecoveryObserved;
            var diagnosticArguments = Environment.GetCommandLineArgs();
            _motorTraceEnabled = Array.IndexOf(diagnosticArguments, "-blockMotorTrace") >= 0;
            _report.motorTraceEnabled = _motorTraceEnabled;
            if (_motorTraceEnabled)
            {
                _motorBranchProviderBaseline = PlayerMotorSimulation.MotorBranchObserverErrors;
                PlayerMotorSimulation.MotorBranchObserved += OnMotorBranchObserved;
            }
            BeginMotorDetailsCollection();
            BeginTerrainDependencyCollection();
            ConfigureNetworkCpuObservation(diagnosticArguments);
            Application.logMessageReceived += OnLog; Write();
        }
        void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            AbortNetworkCpuOnLog();
            _report.errors++; if (_report.errorMessages.Count < 100) _report.errorMessages.Add(message);
        }
        void UpdateProbe()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_finished)
            {
                if (now >= _nextWrite && Write()) Application.Quit(_exitCode);
                return;
            }
            if (now >= _nextPoll) { _nextPoll = now + .2; ReadCommand(); }
            if (_finished) return;
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            if (match != null && match.IsSpawned && player != null && player.IsSpawned && match.Blocks != null && match.Blocks.Initialized)
            {
                _match = match; _report.connected = true; _report.role = match.IsServer ? "host" : "client";
                if (_matches.Add(match)) { _report.connectionGenerations++; _hashedRound = _hashedRevision = uint.MaxValue; }
                _report.initialSyncComplete = match.InitialSyncComplete;
                if (match.InitialSyncComplete && _report.firstReadySeconds < 0) _report.firstReadySeconds = now - _boot;
                _report.blockBytesSent = match.BlockBytesSent;
                ObserveNetworkCpu(match, now);
                if (match.IsServer && match.InitialSyncComplete) Fixture(match, now);
                RecordCorrection(player, match);
                if (now >= _nextWrite) { _nextWrite = now + .5; Capture(match); Write(); }
            }
            else { _report.initialSyncComplete = false; if (now >= _nextWrite) { _nextWrite = now + .5; Write(); } }
            if (now - _boot > double.Parse(Arg("-blockProbeTimeout", "420"), CultureInfo.InvariantCulture))
            { _report.errorMessages.Add("Probe timed out before the explicit quit command"); _report.errors++; Finish(false); }
        }
        void ReadCommand()
        {
            if (!File.Exists(_control)) return;
            string[] lines;
            try { lines = File.ReadAllLines(_control); } catch (IOException) { return; }
            if (lines.Length < 2 || !int.TryParse(lines[0], out int command) || command <= _command) return;
            var match = PrototypeMatch.Current;
            string nextStage = lines[1].Trim();
            BeforeNetworkCpuStageChange(match, nextStage);
            PrepareFallStageChange(match, nextStage, command);
            _command = command; _stage = nextStage; _nextPulse = 0;
            _hashedRound = _hashedRevision = uint.MaxValue;
            if (_stage == "reconnect" && Array.IndexOf(Environment.GetCommandLineArgs(), "-blockReconnectParticipant") >= 0 && !_cycling)
            { _cycling = true; Reconnect().Forget(); }
            if (match != null && match.IsServer)
            {
                if (_stage == "round-start" || _stage == "restart") match.StartRound();
                if (_stage == "finish" && match.State.Value.Phase == MatchPhase.Playing)
                {
                    if (_report.fallCoverage?.finishRequested != true)
                        FallCoverageFailure("Finish command lacks the complete current-round published falling fixture proof");
                    else { var state = match.State.Value; state.EndsAt = match.NetworkManager.ServerTime.Time - 1; match.State.Value = state; }
                }
                if (_stage == "clear") match.ReturnToRoom();
            }
            if (_stage == "quit-clients" && match != null && match.IsSpawned && !match.IsServer) Finish(true);
            if (_stage == "quit") Finish(true);
        }
        static Vector3 Lane(PlayerSnapshot state)
        {
            var origin = PrototypeArena.Spawn(state.Team, state.Slot) + new Vector3(0, 3, state.Team == 1 ? 6 : -6);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 12, PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore))
                throw new InvalidOperationException("Block fixture has no static ground at the spawn lane");
            return hit.point;
        }
        void Fixture(PrototypeMatch match, double now)
        {
            if (_stage == "drain") { ObserveDrain(match, now); return; }
            if (_stage == "fall") { QueueFiniteFallFixture(match, now); return; }
            bool seed = _stage == "seed" || _stage == "seed-round" || _stage == "seed-rebuild";
            if (seed)
            {
                foreach (var player in match.Players)
                {
                    if (player == null || !player.IsSpawned || !_seeded.Add((match.State.Value.Round, player.PlayerId))) continue;
                    var lane = Lane(player.Snapshot.Value); _lanes[player.PlayerId] = lane;
                    var owner = player; uint round = match.State.Value.Round;
                    match.Blocks.QueueBuild(lane, Vector3.up, player.Snapshot.Value.Team, 512, 1.2f, true, result =>
                    {
                        _report.fixtureAdded += result.Added;
                        if (owner != null && owner.IsSpawned && match != null && match.IsSpawned && match.State.Value.Round == round)
                            owner.DiagnosticPlace(lane + Vector3.up * .25f, owner.Snapshot.Value.Team == 1 ? 0 : 180);
                    });
                }
            }
            if (_stage != "traverse") return;
            foreach (var player in match.Players)
            {
                var state = player.Snapshot.Value;
                if (state.Ink < 90) { state.Ink = 100; player.Snapshot.Value = state; }
            }
            if (now < _nextPulse) return; _nextPulse = now + .5;
            foreach (var player in match.Players)
            {
                if (!_lanes.TryGetValue(player.PlayerId, out var lane)) continue;
                byte team = player.Snapshot.Value.Team;
                // Build and remove nearby supports while the real predicted owner crosses the lane.
                float offset = Mathf.Sin((float)match.NetworkManager.ServerTime.Time) * .35f;
                var point = lane + new Vector3(offset, .1f, 0);
                match.Blocks.QueueBuild(point, Vector3.up, team, 8, .2f, false, result => _report.fixtureAdded += result.Added);
                match.Blocks.QueueDestroy(lane + new Vector3(.8f, .1f, 0), .14f, .14f, team, false, result => _report.fixtureRemoved += result.Changed, 8, 8);
            }
        }
        void Input(PrototypePlayer player, ref PlayerInputFrame input)
        {
            var state = player.PresentedState;
            input.Fire = input.Swim = false; input.Move = Vector2.zero;
            input.JumpSequence = state.ConsumedJump; input.Look = new Vector2(state.Team == 1 ? 0 : 180, 70);
            RecordDrainInput(player, input);
            if (_stage != "traverse") return;
            double now = player.NetworkManager.ServerTime.Time;
            Vector3 target = Lane(state) + new Vector3(Mathf.Sin((float)now * 1.3f) * .35f, 0, Mathf.Cos((float)now * .8f) * .35f);
            Vector3 delta = target - PlayerMotorSimulation.HumanPosition(state); delta.y = 0;
            delta = Quaternion.Euler(0, -input.Look.x, 0) * delta;
            input.Move = Vector2.ClampMagnitude(new Vector2(delta.x, delta.z) * 1.5f, .3f);
            input.Swim = ((int)now % 8) >= 3;
            input.Fire = !input.Swim && !state.AttackNeedsRelease;
        }
        void RecordCorrection(PrototypePlayer player, PrototypeMatch match)
        {
            if (player.IsServer) return;
            var state = player.Snapshot.Value; var presented = player.PresentedState;
            if (_observedPlayer != player || _life != state.Revision)
            { _observedPlayer = player; _life = state.Revision; _ack = state.AcknowledgedInput; _lastPosition = presented.Position; return; }
            if (state.AcknowledgedInput == _ack) return;
            _ack = state.AcknowledgedInput;
            double moved = Vector3.Distance(_lastPosition, presented.Position); _lastPosition = presented.Position;
            if ((_stage != "traverse" && _stage != "fall") || !match.InitialSyncComplete || state.Health <= 0 || moved < .001 || player.TerrainReplayPending) return;
            bool friendly = state.SupportTerrainRevision > 0 && match.Blocks.World.TryGet(state.SupportCell, out var support) && support.Owner == state.Team;
            _report.movingAckSamples++; if (friendly) _report.friendlyMovementSamples++;
            _report.corrections.Add(new CorrectionSample { stage = _stage, acknowledgedInput = _ack, life = _life,
                terrainRevision = match.Blocks.Revision, serverTime = match.NetworkManager.ServerTime.Time,
                metres = player.LastCorrectionDistance, movedMetres = moved, friendlySupport = friendly, terrainPending = player.TerrainReplayPending });
        }
        void OnMotorBranchObserved(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorBranchObservation observation)
        {
            if (_finished || !_motorTraceEnabled) return;
            try
            {
                var contact = observation.Contact;
                var match = PrototypeMatch.Current;
                // Never update the formal match/generation bookkeeping from this optional consumer.
                // Every appended row remains unchanged; associate replay spans offline using this
                // process's realtime interval plus captured player/life/round and invocation.
                _report.motorBranchEvents.Add(new MotorBranchEventSample
                {
                    eventId = _report.motorBranchEvents.Count + 1L, invocation = observation.Invocation,
                    owner = observation.Owner, networkObjectId = observation.NetworkObjectId,
                    playerObjectId = observation.PlayerObjectId, connectionGeneration = _report.connectionGenerations,
                    terrainRound = observation.TerrainRound, terrainRevision = observation.TerrainRevision,
                    simulationTick = observation.SimulationTick, life = observation.Life, stage = _stage,
                    phase = match != null ? match.State.Value.Phase.ToString() : "Unavailable", branch = observation.Branch.ToString(),
                    before = new MotorStateSample(observation.Before), after = new MotorStateSample(observation.After), input = observation.Input,
                    identityAvailable = observation.IdentityAvailable, probeEvaluated = observation.ProbeEvaluated,
                    probeSucceeded = observation.ProbeSucceeded, controllerGrounded = observation.ControllerGrounded,
                    standingEvaluated = observation.StandingEvaluated, standingFits = observation.StandingFits,
                    wantsFire = observation.WantsFire, shootingMovement = observation.ShootingMovement,
                    collisions = (int)observation.Collisions, requestedMove = observation.RequestedMove,
                    controllerPosition = observation.ControllerPosition, rebaseHeight = observation.RebaseHeight,
                    contactPoint = contact.Point, contactNormal = contact.Normal, contactCell = contact.Cell,
                    contactOwner = contact.Owner, contactRevision = contact.Revision,
                    contactIsBlock = contact.IsBlock, contactStable = contact.Stable, contactClimbable = contact.Climbable,
                    contactSurfaceId = contact.SurfaceId, contactRegionId = contact.RegionId,
                    simulatedAt = observation.SimulatedAt, realtime = observation.Realtime, terrainTime = observation.TerrainTime
                });
                _report.motorBranchEventCount = _report.motorBranchEvents.Count;
            }
            catch (Exception exception)
            {
                _report.motorBranchEventsComplete = false; _report.motorBranchConsumerErrors++;
                if (_report.motorBranchErrorMessages.Count < 100) _report.motorBranchErrorMessages.Add(exception.ToString());
            }
        }
        void OnSafetyRecoveryObserved(BlockTerrain terrain, PrototypePlayer player, BlockTerrain.SafetyRecoveryObservation observation)
        {
            if (_finished || terrain == null || player == null) return;
            try
            {
                var match = PrototypeMatch.Current;
                if (match != null && _matches.Add(match))
                { _report.connectionGenerations++; _hashedRound = _hashedRevision = uint.MaxValue; }
                bool networkTimeAvailable = player.NetworkManager != null;
                _report.safetyRecoveryEvents.Add(new SafetyRecoveryEventSample
                {
                    eventId = _report.safetyRecoveryEvents.Count + 1L, owner = player.PlayerId,
                    networkObjectId = player.IsSpawned ? player.NetworkObjectId : 0, playerObjectId = player.GetInstanceID(),
                    connectionGeneration = _report.connectionGenerations, stage = _stage,
                    phase = match != null ? match.State.Value.Phase.ToString() : "Unavailable",
                    terrainRound = observation.Round, terrainRevision = observation.TerrainRevision,
                    recoveryCount = observation.RecoveryCount, failureCount = observation.FailureCount,
                    before = new ReconcileStateSample(new PrototypePlayer.ReconcileTelemetryState(observation.Before, terrain)),
                    after = new ReconcileStateSample(new PrototypePlayer.ReconcileTelemetryState(observation.After, terrain)),
                    realtime = observation.Realtime, terrainTime = observation.TerrainTime,
                    serverTime = networkTimeAvailable ? player.NetworkManager.ServerTime.Time : -1,
                    networkTimeAvailable = networkTimeAvailable,
                    rawMetres = observation.RawDistance, humanMetres = observation.HumanDistance,
                    capsuleEmbedded = observation.CapsuleEmbedded, paperEmbedded = observation.PaperEmbedded,
                    recovered = observation.Recovered, noRawDisplacement = observation.RawDistance <= .00001f,
                    noHumanDisplacement = observation.HumanDistance <= .00001f
                });
                _report.safetyRecoveryEventCount = _report.safetyRecoveryEvents.Count;
            }
            catch (Exception exception)
            {
                _report.safetyRecoveryEventsComplete = false; _report.safetyRecoveryConsumerErrors++; _report.errors++;
                if (_report.errorMessages.Count < 100) _report.errorMessages.Add("Safety recovery event observation failed: " + exception.Message);
            }
        }
        void OnReconcileObserved(PrototypePlayer player, PrototypePlayer.ReconcileTelemetryEvent observation)
        {
            if (_finished || player == null || player.IsServer) return;
            try
            {
                var match = PrototypeMatch.Current;
                if (match != null && _matches.Add(match))
                { _report.connectionGenerations++; _hashedRound = _hashedRevision = uint.MaxValue; }
                int playerId = player.GetInstanceID();
                var key = (playerId, observation.Authority.Life, observation.TerrainRound, _report.connectionGenerations);
                bool hasPrevious = _reconcileMotion.TryGetValue(key, out var previous);
                var sample = new ReconcileEventSample
                {
                    eventId = _report.reconcileEvents.Count + 1L, owner = player.PlayerId,
                    networkObjectId = player.IsSpawned ? player.NetworkObjectId : 0, playerObjectId = playerId,
                    connectionGeneration = _report.connectionGenerations, stage = _stage,
                    phase = observation.Phase.ToString(), origin = observation.Origin.ToString(),
                    terrainRound = observation.TerrainRound, terrainRevision = observation.TerrainRevision,
                    appliedPaintSequence = observation.AppliedPaintSequence,
                    oldPredicted = new ReconcileStateSample(observation.Before),
                    authority = new ReconcileStateSample(observation.Authority), replayed = new ReconcileStateSample(observation.Replayed),
                    realtime = observation.Realtime, startedRealtime = observation.StartedRealtime,
                    serverTime = observation.ServerTime, terrainTime = observation.TerrainTime,
                    terrainWaitSeconds = observation.TerrainWaitSeconds, oldestPendingSeconds = observation.OldestPendingSeconds,
                    metres = observation.RawDistance, humanMetres = observation.HumanDistance,
                    unadjustedRawMetres = observation.UnadjustedRawDistance, unadjustedHumanMetres = observation.UnadjustedHumanDistance,
                    replaySteps = observation.ReplaySteps, inputHistoryBefore = observation.InputHistoryBefore,
                    inputHistoryAfter = observation.InputHistoryAfter, terrainPendingTransactions = observation.TerrainPendingTransactions,
                    unstableColumns = observation.UnstableColumns, safetyRecoveries = observation.SafetyRecoveries,
                    safetyRecoveryFailures = observation.SafetyRecoveryFailures,
                    lifecycle = observation.Lifecycle, movementChanged = observation.MovementChanged, hard = observation.Hard,
                    initialSyncComplete = observation.InitialSyncComplete, networkTimeAvailable = observation.NetworkTimeAvailable,
                    terrainAvailable = observation.TerrainAvailable, terrainPendingBefore = observation.TerrainPendingBefore,
                    terrainPendingAfter = observation.TerrainPendingAfter, syncPendingBefore = observation.SyncPendingBefore,
                    syncPendingAfter = observation.SyncPendingAfter, paintPendingBefore = observation.PaintPendingBefore,
                    paintPendingAfter = observation.PaintPendingAfter, terrainWaitDurationKnown = observation.TerrainWaitDurationKnown,
                    friendlySupport = observation.Authority.RequiredSupportRevision > 0 && observation.Authority.SupportPresent &&
                        observation.Authority.SupportOwner == observation.Authority.Team,
                    hasPreviousObservation = hasPrevious,
                    newAcknowledgement = hasPrevious && previous.Ack != observation.Authority.AcknowledgedInput,
                    sameAcknowledgement = hasPrevious && previous.Ack == observation.Authority.AcknowledgedInput,
                    predictedMotionMetres = hasPrevious ? Vector3.Distance(previous.PredictedPosition, observation.Replayed.Position) : 0,
                    authorityMotionMetres = hasPrevious ? Vector3.Distance(previous.AuthorityPosition, observation.Authority.Position) : 0
                };
                // Append every event, including stationary, pending, lifecycle and same-ACK recovery.
                // DTOs are never changed after append; the source observation contains only value copies.
                _report.reconcileEvents.Add(sample);
                MarkTerrainSourceAppend(match, player, observation, sample.eventId, sample.connectionGeneration);
                _report.reconcileEventCount = _report.reconcileEvents.Count;
                _reconcileMotion[key] = new ReconcileMotionReference(observation);
            }
            catch (Exception exception)
            {
                _report.reconcileEventsComplete = false; _report.reconcileConsumerErrors++; _report.errors++;
                if (_report.errorMessages.Count < 100) _report.errorMessages.Add("Reconcile telemetry consumer failed: " + exception);
            }
        }
        static string Digest(byte[] data)
        { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        void Hash(BlockWorld world)
        {
            if (_hashedRound == world.Round && _hashedRevision == world.Revision) return;
            _hashedRound = world.Round; _hashedRevision = world.Revision;
            _snapshotHash = Digest(world.EncodeSnapshot());
            using var chunk = new MemoryStream(); using (var writer = new BinaryWriter(chunk, Encoding.UTF8, true))
            {
                foreach (var pair in world.Chunks.OrderBy(p => p.Key.x).ThenBy(p => p.Key.y).ThenBy(p => p.Key.z))
                {
                    writer.Write(pair.Key.x); writer.Write(pair.Key.y); writer.Write(pair.Key.z); writer.Write(pair.Value.Revision);
                    foreach (var cell in pair.Value.Cells) { writer.Write(cell.Owner); writer.Write(cell.Flags); writer.Write(cell.Durability); }
                }
            }
            _chunkHash = Digest(chunk.ToArray());
            using var falling = new MemoryStream(); using (var writer = new BinaryWriter(falling, Encoding.UTF8, true))
            {
                world.WriteFallingState(writer); // Complete descriptors, membership, aliases and every cell owner/flags/durability.
            }
            _fallingHash = Digest(falling.ToArray());
        }
        void Capture(PrototypeMatch match)
        {
            var terrain = match.Blocks; var world = terrain.World; Hash(world); var state = match.State.Value;
            using var offsets = new MemoryStream(); using (var writer = new BinaryWriter(offsets, Encoding.UTF8, true))
                foreach (var run in world.Falling.Values.OrderBy(r => r.Id)) { writer.Write(run.Id); writer.Write(run.Offset(terrain.SimulationTime)); }
            var sample = new TerrainSample { stage = _stage, phase = state.Phase.ToString(), round = state.Round, terrainRound = world.Round,
                revision = world.Revision, players = state.PlayerCount, pink = world.PinkCount, blue = world.BlueCount,
                total = world.PinkCount + world.BlueCount, chunks = world.Chunks.Count, fallingRuns = world.Falling.Count,
                fallingCells = world.Falling.Values.Sum(r => r.Cells.Count(c => c.Occupied)), pending = terrain.PendingTransactions,
                failedTransactions = terrain.FailedTransactions, initialSyncComplete = match.InitialSyncComplete,
                controlCommand = _command, commandsQueued = terrain.CommandsQueued, commandsCompleted = terrain.CommandsCompleted,
                projectilePending = match.IsServer ? match.Projectiles.PendingCount : 0, unstableColumns = terrain.UnstableColumns,
                subPending = match.IsServer ? match.SubWeapons.Entities.Count(e => !e.Removed) : 0,
                specialPending = match.IsServer ? match.SpecialWeapons.Entities.Count(e => !e.Removed) : 0,
                drainQuietSeconds = _stage == "drain" && _drainQuietAt >= 0 ? Math.Max(0, Time.realtimeSinceStartupAsDouble - _drainQuietAt) : 0,
                oldestPendingMs = terrain.OldestPendingSeconds * 1000, commandHead = terrain.InspectCommandHead(),
                authorityInputs = match.IsServer ? CaptureDrainInputs(match) : null,
                safetyRecoveries = terrain.SafetyRecoveries, safetyRecoveryFailures = terrain.SafetyRecoveryFailures,
                connectionGeneration = _report.connectionGenerations, scorePink = state.PinkBlockCount, scoreBlue = state.BlueBlockCount,
                serverTime = match.NetworkManager.ServerTime.Time, frozenAt = state.TerrainFrozenAt, terrainTime = terrain.SimulationTime,
                chunkHash = _chunkHash, fallingHash = _fallingHash, fallingOffsetHash = Digest(offsets.ToArray()), snapshotHash = _snapshotHash };
            _report.current = sample;
            string key = _stage + ":" + world.Round + ":" + world.Revision + ":" + _report.connectionGenerations;
            if (_stateIndices.TryGetValue(key, out int index)) _report.states[index] = sample;
            else { _stateIndices.Add(key, _report.states.Count); _report.states.Add(sample); }
        }
        async UniTaskVoid Reconnect()
        {
            _report.reconnectRequested = true;
            try
            {
                var app = PrototypeApp.Current;
                await app.Leave(); _report.reconnectDisconnected = true; _report.initialSyncComplete = false;
                _report.reconnectCleanup = Splatoon.Painting.PaintSurface.AllocatedBytes == 0 && PrototypeMatch.Current == null;
                Write(); await UniTask.Delay(TimeSpan.FromSeconds(1));
                await app.Connect(false, Arg("-lanAddress", "127.0.0.1"), ushort.Parse(Arg("-lanPort", "7788"), CultureInfo.InvariantCulture));
                _report.reconnectSuccess = app.InRoom;
                if (!app.InRoom) { _report.errors++; _report.errorMessages.Add("Reconnect failed: " + app.Error); }
                Debug.Log("[BLOCK-NET] Reconnect completed; new initial-sync evidence is still required");
            }
            catch (Exception exception) { _report.errors++; _report.errorMessages.Add(exception.ToString()); }
            finally { _cycling = false; Write(); }
        }
        bool Write()
        {
            _report.duration = Time.realtimeSinceStartupAsDouble - _boot;
            _report.reconcileEventsIncluded = _finished;
            _report.safetyRecoveryEventsIncluded = _finished;
            _report.motorBranchEventsIncluded = _finished && _motorTraceEnabled;
            if (_motorTraceEnabled)
            {
                _report.motorBranchProviderErrors = Math.Max(0, PlayerMotorSimulation.MotorBranchObserverErrors - _motorBranchProviderBaseline);
                if (_report.motorBranchProviderErrors > 0)
                {
                    _report.motorBranchEventsComplete = false;
                    _report.motorBranchProviderLastError = PlayerMotorSimulation.LastMotorBranchObserverError;
                }
            }
            if (_finished) { FinishMotorDetailsCollection(); FinishTerrainDependencyCollection(); }
            var events = _report.reconcileEvents;
            var recoveries = _report.safetyRecoveryEvents;
            var motorEvents = _report.motorBranchEvents;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_output));
                // Writing the entire growing event list every half-second would perturb the
                // measured reconciliation. Keep live status small and serialize all events once
                // the explicit finish command closes observation. No event is removed from memory.
                if (!_finished) { _report.reconcileEvents = null; _report.safetyRecoveryEvents = null; }
                if (!_finished || !_motorTraceEnabled) _report.motorBranchEvents = null;
                string temporary = _output + ".writing";
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false), 16384))
                    WriteReportJson(writer, _report);
                if (File.Exists(_output)) File.Replace(temporary, _output, null); else File.Move(temporary, _output);
                return true;
            }
            catch (IOException)
            {
                // The harness may briefly hold a Windows read handle without delete sharing.
                // Keep the previous complete report and retry after the reader releases it.
                _nextWrite = Time.realtimeSinceStartupAsDouble + .1; return false;
            }
            finally { _report.reconcileEvents = events; _report.safetyRecoveryEvents = recoveries; _report.motorBranchEvents = motorEvents; }
        }
        void Finish(bool complete)
        {
            if (_finished) return;
            CloseNetworkCpuObservation(complete ? "probe_finish" : "probe_finish_incomplete");
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            bool liveMatch = match != null && match.IsSpawned && match.Blocks != null && match.Blocks.Initialized;
            _report.connected = liveMatch && player != null && player.IsSpawned;
            _report.initialSyncComplete = liveMatch && match.InitialSyncComplete;
            if (liveMatch)
            {
                _match = match; _report.role = match.IsServer ? "host" : "client"; _report.blockBytesSent = match.BlockBytesSent;
                if (_matches.Add(match)) { _report.connectionGenerations++; _hashedRound = _hashedRevision = uint.MaxValue; }
                Capture(match);
            }
            _finished = true; _report.complete = complete; _exitCode = complete && _report.errors == 0 ? 0 : 4;
            if (Write()) Application.Quit(_exitCode);
        }
        void OnDestroy()
        {
            CloseNetworkCpuObservation("probe_destroy");
            StopFallCoverageCollection();
            StopMotorDetailsCollection();
            StopTerrainDependencyCollection();
            PrototypePlayer.ReconcileObserved -= OnReconcileObserved;
            BlockTerrain.SafetyRecoveryObserved -= OnSafetyRecoveryObserved;
            if (_motorTraceEnabled) PlayerMotorSimulation.MotorBranchObserved -= OnMotorBranchObserved;
            Application.logMessageReceived -= OnLog; if (_instance == this) _instance = null;
        }
#endif
    }
}
