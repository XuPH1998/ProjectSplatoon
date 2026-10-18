#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>Bounded real authority/controller/PhysX diagnosis, outside measured gameplay loads.</summary>
    public sealed class BlockPushRecoveryPlayTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const float SettlingSeconds = .4f, WaypointDistance = 1.2f;
        static readonly FieldInfo MotorField = typeof(PrototypePlayer).GetField("_motor", Private);
        static readonly FieldInfo InputQueueField = typeof(PrototypePlayer).GetField("_serverInputs", Private);
        static readonly MethodInfo AcceptInputMethod = typeof(PrototypePlayer).GetMethod("AcceptInput", Private);
        readonly List<BlockDynamicBody> _seeded = new(6);
        readonly Collider[] _standing = new Collider[64];
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockDefaultContractFixture _fixture;
        PlayerMotorSimulation _motor;
        CharacterController _controller;
        IDictionary<uint, PlayerInputFrame> _inputs;
        RaycastHit _floor;
        Report _report;
        TickRow _row;
        float _dt;
        bool _insideAuthorityInvocation, _observing, _written;
        int _branchErrors, _detailErrors;
        string _reportPath;
        BlockPushRecoveryFixedDriver _driver;
        BlockDynamicAcceptanceProbe.BlastPressureActor _actor;
        Vector3 _lane;
        int _caseCount, _maximumTicks, _settlingTicks, _completedTicks, _fixedCallbacks;
        double _wallDeadline;
        bool _driverDone;

        [Serializable] sealed class Report
        {
            public int schema = 2;
            public string caseName, result = "incomplete", failure = "Run did not reach its gate", unityVersion;
            public string scope = "Controlled seeded bodies; real host authority Simulate and automatic PhysX. No paid creation, independent prediction/network, Blast phase, or performance acceptance.";
            public string inputSource = "Test invokes production AcceptInput with the real server-time input tick; production Simulate consumes and acknowledges it. RPC transport is outside this fixture.";
            public string presentationSource = "Host PresentedState is authority Snapshot.Value; no independent prediction is sampled.";
            public string clearanceSource = "Unavailable: bridge probe clearance/final clamp/no-force contact IDs are not exposed. Movement shortfall is observational, not the executed bridge clamp.";
            public string capsuleSource = "MotorDetailObserved DestinationMove: capsule captured before bridge temporary stepOffset override.";
            public string physicsSamplingSource = "Real default-order MonoBehaviour.FixedUpdate driver; previous automatic PhysX row sampled at the next FixedUpdate entry, after earlier BlockDynamicWorld (-200) maintenance and before the next controlled authority step. This is not a post-physics-before-all-prephysics hook.";
            public string driverException;
            public bool authorityTickMismatch, physicsStepMismatch, fixedScheduleMismatch;
            public int driverFixedCallbacks, completedPhysicsRows;
            public string pushSource = "PlayerBlockPushBridge.PushObserved: actual authority force-bearing contacts only.";
            public bool completed, passed, outboundReached, recoveryGate;
            public bool bridgeClearanceAvailable = false;
            public bool observerErrors, duplicatePush, inputMismatch, tickMismatch, missingDestination;
            public bool traceIdentityMismatch, bodyIdentityMismatch, stepOffsetMismatch, wallBoundReached, floorMaterialHasExplicitAsset;
            public float dt, fixedDeltaTime, maximumSeconds, elapsedSeconds, cellSize, mass, staticFriction, dynamicFriction;
            public float pushForce, pushSpeed, activationRadius, deactivationRadius, maximumForce, maximumRise, maximumBodyTravel;
            public int expectedBodies, actualBodies, branchObserverErrors, detailObserverErrors;
            public Vector3 originalLane, outwardWaypoint, gravity;
            public string floorCollider, floorMaterial;
            public float floorStaticFriction, floorDynamicFriction;
            public BodySample[] initialBodies;
            public List<TickRow> ticks = new();
        }

        [Serializable] sealed class TickRow
        {
            public int fixedCallbackBefore, fixedCallbackAfter;
            public int ordinal, destinationEvents, completeEvents, queuedBeforeAcceptance, queuedAfterAcceptance, queuedAfterSimulate;
            public string recoveryMotion;
            public uint inputSequence, inputNetworkTick, beforeTick, afterTick, acknowledgedInput, observedInputSequence, observedMotorTick;
            public ulong motorInvocation, observedOwner;
            public double simulationTime, fixedTimeBefore, fixedTimeAfterPhysics;
            public Vector2 requestedInput, observedMotorInput;
            public Vector3 waypoint, authorityBefore, authorityAfter, authorityDelta, controllerAfter, presentedAfter;
            public Vector3 requestedMove, destinationBefore, destinationAfter, observedMoveShortfall;
            public Vector3 planarVelocity;
            public float verticalSpeed, laneDistance, totalForce;
            public bool inputAccepted, invocationCompleted, inputTimedOut, grounded, canStandBefore, canStandAfter, atLane, recoveryGate;
            public bool canStandAfterPhysics, recoveryGateAfterPhysics;
            public float restoredStepOffset;
            public bool destinationCapsuleObserved;
            public PlayerMotorSimulation.MoveCapsuleDetails capsuleBeforeBridge;
            public CollisionFlags destinationCollisions;
            public List<PushSample> pushes = new();
            public BodySample[] afterPhysics;
        }

        [Serializable] sealed class PushSample
        {
            public ulong owner, bodyId;
            public Vector3 force;
        }

        [Serializable] sealed class BodySample
        {
            public ulong id;
            public byte owner;
            public bool present, sleeping, kinematic, parked;
            public Vector3 position, velocity, angularVelocity;
            public Quaternion rotation;
        }

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot config/assets");
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks?.Dynamic != null,
                "Spawned authority player and actual dynamic world");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local;
            _match.enabled = false;
            foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match);
            _fixture.Tick();
            _dt = 1f / GameplayConfig.Global.SimulationRate;
            Assert.That(Time.fixedDeltaTime, Is.EqualTo(_dt).Within(.000001f), "Use the configured production fixed rate");
            Assert.That(Time.timeScale, Is.EqualTo(1f), "The fixture does not change the physics clock");
            Assert.That(Physics.simulationMode, Is.EqualTo(SimulationMode.FixedUpdate), "Use automatic real PhysX exactly once per tick");
            Assert.That(_host.IsServer && _host.IsSpawned && _match.IsServer, Is.True);
            Assert.That(_match.Blocks.SurfaceOnly, Is.True);
            Assert.That(_match.Blocks.Dynamic.Count, Is.Zero, "A fresh room isolates the controlled pile");
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out _floor, 10,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True, "Actual authored floor");
            Assert.That(_match.Blocks.IsAuthoredMapCollider(_floor.collider) && _floor.normal.y > .99f, Is.True);
            _motor = (PlayerMotorSimulation)MotorField.GetValue(_host);
            _controller = _host.GetComponent<CharacterController>();
            _inputs = (IDictionary<uint, PlayerInputFrame>)InputQueueField.GetValue(_host);
            _inputs.Clear(); _seeded.Clear(); _report = null; _written = false; _reportPath = null;
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            StopFixedDriver();
            StopObserving();
            WriteReport();
            _fixture?.Dispose(); _fixture = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)] public IEnumerator SingleCubeWalksOutAndReturnsWithinFiveSeconds()
            => RunCase("single", 1, 5);

        [UnityTest, Timeout(600000)] public IEnumerator SixCubePileWalksOutAndReturnsWithinEightSeconds()
            => RunCase("six", 6, 8);

        IEnumerator RunCase(string caseName, int count, float maximumSeconds)
        {
            Vector3 lane = _floor.point + Vector3.up * .035f;
            _report = new Report
            {
                caseName = caseName, expectedBodies = count, maximumSeconds = maximumSeconds,
                dt = _dt, fixedDeltaTime = Time.fixedDeltaTime, unityVersion = Application.unityVersion,
                cellSize = BlockWorld.CellSize, mass = BlockDynamicWorld.Mass,
                staticFriction = BlockDynamicWorld.StaticFriction, dynamicFriction = BlockDynamicWorld.DynamicFriction,
                pushForce = BlockDynamicWorld.PushForce, pushSpeed = BlockDynamicWorld.PushSpeed,
                activationRadius = BlockDynamicWorld.ActivationRadius, deactivationRadius = BlockDynamicWorld.DeactivationRadius,
                originalLane = lane, outwardWaypoint = lane + Vector3.forward * WaypointDistance, gravity = Physics.gravity,
                floorCollider = _floor.collider.name,
                floorMaterial = _floor.collider.sharedMaterial != null ? _floor.collider.sharedMaterial.name : "Unity default",
                floorMaterialHasExplicitAsset = _floor.collider.sharedMaterial != null,
                floorStaticFriction = _floor.collider.sharedMaterial != null ? _floor.collider.sharedMaterial.staticFriction : -1,
                floorDynamicFriction = _floor.collider.sharedMaterial != null ? _floor.collider.sharedMaterial.dynamicFriction : -1
            };
            try
            {
                Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f).Within(.000001f));
                Assert.That(BlockDynamicWorld.Mass, Is.EqualTo(.5f));
                Assert.That(BlockDynamicWorld.StaticFriction, Is.EqualTo(.35f));
                Assert.That(BlockDynamicWorld.DynamicFriction, Is.EqualTo(.2f));
                Assert.That(BlockDynamicWorld.PushForce, Is.EqualTo(100));
                Assert.That(BlockDynamicWorld.PushSpeed, Is.EqualTo(3));
                _lane = lane; _caseCount = count;
                _maximumTicks = Mathf.FloorToInt(maximumSeconds / _dt + .0001f);
                _settlingTicks = Mathf.CeilToInt(SettlingSeconds / _dt);
                _completedTicks = 0; _fixedCallbacks = 0; _driverDone = false;
                _wallDeadline = Time.realtimeSinceStartupAsDouble + 45;
                var driverObject = new GameObject("PushRecoveryFixedDriver");
                _driver = driverObject.AddComponent<BlockPushRecoveryFixedDriver>();
                _driver.OnFixedStep = AdvanceFixedStep;
                // EditMode TestConsumer owns this iterator, so it only waits; real FixedUpdate owns every authority step.
                while (!_driverDone && _driver.Failure == null && Time.realtimeSinceStartupAsDouble < _wallDeadline)
                    yield return null;
                if (_driver.Failure != null) _report.driverException = _driver.Failure.ToString();
                if (!_driverDone && _driver.Failure == null) _report.wallBoundReached = true;
                StopFixedDriver();
                _report.actualBodies = _match.Blocks.Dynamic.Count;
                _report.branchObserverErrors = PlayerMotorSimulation.MotorBranchObserverErrors - _branchErrors;
                _report.detailObserverErrors = PlayerMotorSimulation.MotorDetailObserverErrors - _detailErrors;
                _report.observerErrors = _report.branchObserverErrors != 0 || _report.detailObserverErrors != 0;
                _report.failure = Failure();
                _report.completed = true; _report.passed = string.IsNullOrEmpty(_report.failure);
                _report.result = _report.passed ? "passed" : "failed";
            }
            finally
            {
                _insideAuthorityInvocation = false;
                StopFixedDriver();
                _row = null; StopObserving(); WriteReport();
            }
            Assert.That(_report.passed, Is.True, _report.failure + "; diagnostic: " + _reportPath);
        }

        // One invocation per real fixed callback. The first callback creates the state and starts row zero.
        // Every later callback first observes the preceding automatic PhysX step, then starts at most one row.
        bool AdvanceFixedStep()
        {
            _fixedCallbacks++;
            _report.driverFixedCallbacks = _fixedCallbacks;
            _report.fixedScheduleMismatch |= !Time.inFixedTimeStep ||
                Physics.simulationMode != SimulationMode.FixedUpdate || Mathf.Abs(Time.timeScale - 1f) > .000001f ||
                Mathf.Abs(Time.fixedDeltaTime - _dt) > .000001f;
            _report.tickMismatch |= _report.fixedScheduleMismatch;
            if (_fixedCallbacks == 1)
            {
                // No seeded body exists before this first real fixed callback.
                PlaceInitialState(_lane);
                SeedPile(_caseCount, _lane);
                _report.initialBodies = CaptureBodies();
                _actor = new BlockDynamicAcceptanceProbe.BlastPressureActor
                {
                    owner = _host.PlayerId, lane = _lane, seed = _lane + Vector3.forward * .65f,
                    recoveryMotion = "outbound", recoveryWaypoint = _report.outwardWaypoint,
                    recoveryMoveAt = _fixture.Now + SettlingSeconds
                };
                StartObserving();
            }
            if (_row != null)
            {
                CompletePhysicsRow();
                if ((_completedTicks > _settlingTicks && _actor.recoveryMotion == "" && _report.recoveryGate) ||
                    _completedTicks >= _maximumTicks)
                { _driverDone = true; return true; }
            }
            if (Time.realtimeSinceStartupAsDouble >= _wallDeadline)
            { _report.wallBoundReached = true; _driverDone = true; return true; }
            BeginAuthorityRow();
            return false;
        }

        void BeginAuthorityRow()
        {
            var before = _host.Snapshot.Value;
            Vector3 feet = PlayerMotorSimulation.HumanPosition(before);
            bool canStand = _motor.CanStand(feet);
            bool settling = _completedTicks < _settlingTicks;
            if (!settling)
            {
                _actor.atLane = AtLane(before, _lane);
                BlockDynamicAcceptanceProbe.UpdateBlastRecoveryMove(_match.Blocks, _actor,
                    HeroBodyShape.For(before.HeroId), feet, canStand, _fixture.Now, _standing);
            }
            var move = settling ? Vector2.zero : BlockDynamicAcceptanceProbe.BlastRecoveryMoveInput(_actor, feet, before.Yaw);
            uint inputTick = (uint)(_match.NetworkManager.ServerTime.Time * GameplayConfig.Global.SimulationRate);
            var input = NeutralInput(before, move, inputTick);
            _row = new TickRow
            {
                ordinal = _completedTicks, fixedCallbackBefore = _fixedCallbacks, recoveryMotion = settling ? "settle" : _actor.recoveryMotion,
                inputSequence = input.Sequence, inputNetworkTick = input.Tick, beforeTick = before.SimulationTick,
                simulationTime = _fixture.Now + _dt, fixedTimeBefore = Time.fixedTimeAsDouble,
                requestedInput = move, authorityBefore = feet, canStandBefore = canStand,
                waypoint = _actor.recoveryWaypoint
            };
            _report.ticks.Add(_row);
            _fixture.Now += _dt;
            _match.Blocks.Tick(true, _fixture.Now, _dt);
            _row.queuedBeforeAcceptance = _inputs.Count;
            AcceptInputMethod.Invoke(_host, new object[] { input });
            _row.queuedAfterAcceptance = _inputs.Count;
            _row.inputAccepted = _inputs.ContainsKey(input.Sequence);
            _insideAuthorityInvocation = true;
            try { _host.Simulate(_dt, _fixture.Now, MatchPhase.Practice); }
            finally { _insideAuthorityInvocation = false; }
            _row.invocationCompleted = true;
            var after = _host.Snapshot.Value;
            _row.queuedAfterSimulate = _inputs.Count;
            _row.afterTick = after.SimulationTick; _row.acknowledgedInput = after.AcknowledgedInput;
            _row.authorityAfter = PlayerMotorSimulation.HumanPosition(after);
            _row.authorityDelta = _row.authorityAfter - _row.authorityBefore;
            _row.controllerAfter = _controller.transform.position;
            _row.presentedAfter = PlayerMotorSimulation.HumanPosition(_host.PresentedState);
            _row.restoredStepOffset = _controller.stepOffset;
            _row.inputTimedOut = after.InputTimedOut;
            _row.planarVelocity = after.PlanarVelocity; _row.verticalSpeed = after.VerticalSpeed;
            _row.grounded = after.Grounded;
            _row.canStandAfter = _motor.CanStand(_row.authorityAfter);
            _row.laneDistance = HorizontalDistance(_row.authorityAfter, _lane);
            _row.atLane = AtLane(after, _lane);
            _row.recoveryGate = _row.atLane && _row.canStandAfter && !after.InputTimedOut;
            _row.observedMoveShortfall = Vector3.ProjectOnPlane(_row.requestedMove -
                (_row.destinationAfter - _row.destinationBefore), Vector3.up);
            _report.inputMismatch |= !_row.inputAccepted || _row.queuedBeforeAcceptance != 0 ||
                _row.queuedAfterAcceptance != 1 || _row.queuedAfterSimulate != 0 ||
                after.AcknowledgedInput != input.Sequence || after.InputTimedOut ||
                (_row.observedMotorInput - move).sqrMagnitude > .00000001f;
            _report.authorityTickMismatch |= after.SimulationTick != before.SimulationTick + 1;
            _report.tickMismatch |= _report.authorityTickMismatch;
            _report.stepOffsetMismatch |= Mathf.Abs(_controller.stepOffset - HeroBodyShape.For(after.HeroId).StepOffset) > .000001f;
            _report.missingDestination |= _row.destinationEvents != 1 || _row.completeEvents != 1 || !_row.destinationCapsuleObserved;
            _report.maximumRise = Mathf.Max(_report.maximumRise, _row.authorityAfter.y - _lane.y);
            if (!settling && _actor.recoveryMotion == "outbound" &&
                HorizontalDistance(_row.authorityAfter, _report.outwardWaypoint) < .07f) _report.outboundReached = true;
            _match.Blocks.Dynamic.PrepareForPhysics(_match.Players, _dt);
            Physics.SyncTransforms();
        }

        void CompletePhysicsRow()
        {
            _row.fixedTimeAfterPhysics = Time.fixedTimeAsDouble;
            _row.fixedCallbackAfter = _fixedCallbacks;
            _report.physicsStepMismatch |= _row.fixedCallbackAfter != _row.fixedCallbackBefore + 1 ||
                Math.Abs((_row.fixedTimeAfterPhysics - _row.fixedTimeBefore) - _dt) > .00001;
            _report.tickMismatch |= _report.physicsStepMismatch;
            _row.afterPhysics = CaptureBodies();
            UpdateBodyTravel(_row.afterPhysics);
            _row.canStandAfterPhysics = _motor.CanStand(_row.authorityAfter);
            _row.recoveryGateAfterPhysics = _row.atLane && _row.canStandAfterPhysics && !_row.inputTimedOut;
            _completedTicks++;
            _report.completedPhysicsRows = _completedTicks;
            _report.elapsedSeconds = _completedTicks * _dt;
            _report.recoveryGate = _row.recoveryGateAfterPhysics;
            _row = null;
        }

        void StopFixedDriver()
        {
            if (_driver == null) return;
            _driver.OnFixedStep = null; _driver.enabled = false;
            UnityEngine.Object.Destroy(_driver.gameObject);
            _driver = null;
        }

        void PlaceInitialState(Vector3 lane)
        {
            var current = _host.Snapshot.Value;
            var state = new PlayerSnapshot
            {
                HeroId = 1, HeroRevision = current.HeroRevision, Team = current.Team, Slot = current.Slot,
                Health = 100, Ink = 100, LifeState = PlayerLifeState.Alive, Revision = current.Revision,
                SimulationTick = current.SimulationTick, AcknowledgedInput = current.AcknowledgedInput,
                Position = lane, Grounded = true, Movement = MovementMode.Human,
                SubWeaponId = 1, SpecialWeaponId = 1, SpecialPhase = SpecialPhase.Charging,
                AttackNeedsRelease = true, SubNeedsRelease = true, SpecialNeedsRelease = true
            };
            // Setup only. No restore, pose write, respawn or velocity reset occurs during the walk/recovery.
            _motor.Restore(state); _host.Snapshot.Value = state; Physics.SyncTransforms();
            Assert.That(_motor.CanStand(lane), Is.True, "The initial authored-map lane fits the production capsule");
        }

        void SeedPile(int count, Vector3 lane)
        {
            float pitch = BlockWorld.CellSize + .02f;
            for (int i = 0; i < count; i++)
            {
                float x = count == 1 ? 0 : (i % 2 - .5f) * pitch;
                float z = .65f + (count == 1 ? 0 : i / 2 * pitch);
                Vector3 point = _floor.point + new Vector3(x, 0, z);
                Assert.That(Physics.Raycast(point + Vector3.up, Vector3.down, out var support, 2,
                    PlayerMotorSimulation.WorldMask & ~(1 << BlockDynamicWorld.Layer), QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(_match.Blocks.IsAuthoredMapCollider(support.collider) && support.normal.y > .99f, Is.True);
                Assert.That(support.point.y, Is.EqualTo(_floor.point.y).Within(.002f), "No custom floor or hidden map step");
                Vector3 spawn = support.point + Vector3.up * (BlockWorld.CellSize * .5f + .006f);
                Assert.That(_match.Blocks.Dynamic.TrySpawn(_host.Snapshot.Value.Team, spawn, Quaternion.identity, out var body), Is.True,
                    "Legal non-overlapping diagnostic seed " + i + " from lane " + lane);
                Assert.That(body.Body.mass, Is.EqualTo(.5f));
                Assert.That(body.Body.isKinematic, Is.False);
                Assert.That(body.Body.useGravity, Is.True);
                _seeded.Add(body);
            }
        }

        static PlayerInputFrame NeutralInput(PlayerSnapshot s, Vector2 move, uint networkTick) => new()
        {
            Sequence = s.AcknowledgedInput + 1, Tick = networkTick,
            Revision = s.Revision, HeroRevision = s.HeroRevision,
            Move = move, Look = new Vector2(s.Yaw, s.Pitch),
            JumpSequence = s.ConsumedJump, FireSequence = s.ConsumedFire, ReleaseSequence = s.ConsumedRelease,
            SubPressSequence = s.SubConsumedPress, SubReleaseSequence = s.SubConsumedRelease,
            SpecialSequence = s.SpecialConsumed, CancelFire = true, CancelSub = true
        };

        static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.ProjectOnPlane(a - b, Vector3.up).magnitude;
        static bool AtLane(PlayerSnapshot state, Vector3 lane) =>
            HorizontalDistance(PlayerMotorSimulation.HumanPosition(state), lane) < .12f &&
            state.Grounded && state.PlanarVelocity.sqrMagnitude < .0025f;

        void StartObserving()
        {
            _branchErrors = PlayerMotorSimulation.MotorBranchObserverErrors;
            _detailErrors = PlayerMotorSimulation.MotorDetailObserverErrors;
            PlayerMotorSimulation.MotorBranchObserved += MotorObserved;
            PlayerMotorSimulation.MotorDetailObserved += DetailObserved;
            PlayerBlockPushBridge.PushObserved += PushObserved;
            _observing = true;
        }

        void StopObserving()
        {
            if (!_observing) return;
            PlayerMotorSimulation.MotorBranchObserved -= MotorObserved;
            PlayerMotorSimulation.MotorDetailObserved -= DetailObserved;
            PlayerBlockPushBridge.PushObserved -= PushObserved;
            _observing = false;
        }

        void MotorObserved(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorBranchObservation observation)
        {
            if (!_insideAuthorityInvocation || _row == null || !ReferenceEquals(motor, _motor)) return;
            if (observation.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete) _row.completeEvents++;
            if (observation.Branch != PlayerMotorSimulation.MotorTraceBranch.DestinationMove) return;
            _row.destinationEvents++; _row.motorInvocation = observation.Invocation;
            _row.observedInputSequence = observation.Input.Sequence; _row.observedMotorTick = observation.SimulationTick;
            _row.observedOwner = observation.Owner;
            _report.traceIdentityMismatch |= observation.Input.Sequence != _row.inputSequence ||
                observation.SimulationTick != _row.beforeTick + 1 || observation.Owner != _host.PlayerId || !observation.IdentityAvailable;
            _row.observedMotorInput = observation.Input.Move;
            _row.requestedMove = observation.RequestedMove;
            _row.destinationBefore = observation.Before.Position; _row.destinationAfter = observation.After.Position;
            _row.destinationCollisions = observation.Collisions;
        }

        void DetailObserved(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorDetailObservation observation)
        {
            if (!_insideAuthorityInvocation || _row == null || !ReferenceEquals(motor, _motor) ||
                observation.Kind != PlayerMotorSimulation.MotorDetailKind.Move ||
                observation.SourceBranch != PlayerMotorSimulation.MotorTraceBranch.DestinationMove) return;
            _row.destinationCapsuleObserved = true; _row.capsuleBeforeBridge = observation.Capsule;
        }

        void PushObserved(ulong owner, BlockDynamicBody body, Vector3 force)
        {
            if (!_insideAuthorityInvocation || _row == null || owner != _host.PlayerId) return;
            foreach (var prior in _row.pushes) if (prior.bodyId == body.Id) _report.duplicatePush = true;
            _row.pushes.Add(new PushSample { owner = owner, bodyId = body.Id, force = force });
            _row.totalForce += force.magnitude;
            _report.maximumForce = Mathf.Max(_report.maximumForce, _row.totalForce);
        }

        BodySample[] CaptureBodies()
        {
            var samples = new BodySample[_seeded.Count];
            for (int i = 0; i < samples.Length; i++)
            {
                var block = _seeded[i];
                bool present = block != null && _match.Blocks.Dynamic.Contains(block);
                samples[i] = new BodySample { id = block != null ? block.Id : 0, present = present };
                if (!present) continue;
                var body = block.Body; var sample = samples[i];
                sample.owner = block.Owner; sample.position = body.position; sample.rotation = body.rotation;
                sample.velocity = body.linearVelocity; sample.angularVelocity = body.angularVelocity;
                sample.sleeping = body.IsSleeping(); sample.kinematic = body.isKinematic; sample.parked = block.Parked;
            }
            return samples;
        }

        void UpdateBodyTravel(BodySample[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                _report.bodyIdentityMismatch |= !samples[i].present || samples[i].kinematic ||
                    samples[i].id != _report.initialBodies[i].id || samples[i].owner != _report.initialBodies[i].owner;
                if (samples[i].present)
                    _report.maximumBodyTravel = Mathf.Max(_report.maximumBodyTravel,
                        HorizontalDistance(samples[i].position, _report.initialBodies[i].position));
            }
        }

        string Failure()
        {
            var failures = new List<string>();
            if (!_report.outboundReached) failures.Add("Authority did not reach the real outbound waypoint (<0.07 m)");
            if (!_report.recoveryGate) failures.Add("Original lane/grounded/stationary/CanStand/input gates did not recover");
            if (_report.maximumBodyTravel <= .15f) failures.Add("No real seeded cube travelled >0.15 m");
            if (_report.maximumForce <= 0 || _report.maximumForce > BlockDynamicWorld.PushForce + .001f)
                failures.Add($"Missing real authority push or total force exceeds the configured {BlockDynamicWorld.PushForce} N budget");
            if (_report.maximumRise >= .12f) failures.Add("Walking auto-stepped/climbed more than the baseline 0.12 m");
            if (_report.actualBodies != _report.expectedBodies) failures.Add("Seeded body count changed");
            if (_report.inputMismatch || _report.tickMismatch || _report.missingDestination || _report.observerErrors || _report.duplicatePush ||
                _report.traceIdentityMismatch || _report.bodyIdentityMismatch || _report.stepOffsetMismatch)
                failures.Add("Authority input/tick/trace identity or observer/force uniqueness validation failed");
            if (_report.authorityTickMismatch) failures.Add("Authority SimulationTick did not advance exactly once");
            if (_report.physicsStepMismatch) failures.Add("Consecutive real FixedUpdate entries did not bracket exactly one configured automatic PhysX step");
            if (_report.fixedScheduleMismatch) failures.Add("The driver left the real fixed loop or production automatic physics clock changed");
            if (_report.completedPhysicsRows != _report.ticks.Count) failures.Add("An authority row has no following fixed-entry physics observation");
            if (!string.IsNullOrEmpty(_report.driverException)) failures.Add("FixedUpdate driver exception: " + _report.driverException);
            if (_report.wallBoundReached) failures.Add("45-second wall bound reached inside the bounded fixed-step diagnostic");
            return string.Join("; ", failures);
        }

        void WriteReport()
        {
            if (_report == null || _written) return;
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Reports", "BlockPhysics20261017", "PushRecovery53"));
            Directory.CreateDirectory(directory);
            _reportPath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + _report.caseName + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_reportPath, JsonUtility.ToJson(_report, true));
            _written = true;
            TestContext.Out.WriteLine("Push/recovery diagnostic: " + _reportPath);
        }
    }
}
#endif
