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
    // Native Editor Play Mode compatibility fixture, not a network/paid-shot/performance gate.
    public sealed class BlockReplicaCollisionPlayTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo MotorField = typeof(PrototypePlayer).GetField("_motor", Private);
        static readonly MethodInfo Begin = typeof(PlayerBlockPushBridge).GetMethod("BeginStep", Private);
        static readonly MethodInfo Move = typeof(PlayerBlockPushBridge).GetMethod("Move", Private);
        static readonly MethodInfo End = typeof(PlayerBlockPushBridge).GetMethod("EndStep", Private);
        PrototypeMatch _match;
        PrototypePlayer _player;
        PlayerMotorSimulation _motor;
        CharacterController _controller;
        PlayerBlockPushBridge _bridge;
        BlockDefaultContractFixture _fixture;
        BlockPushRecoveryFixedDriver _driver;
        RaycastHit _floor;
        float _dt;
        int _fixedTick;
        readonly TpsAimSolver _aim = new();
        Report _report;
        BlockDynamicWorld World => _match.Blocks.Dynamic;
        Vector3 Lane => _floor.point + Vector3.up * .035f;

        [Serializable] sealed class Row
        {
            public string stage;
            public int fixedTick, mode, flags;
            public double fixedTime;
            public uint round, revision;
            public ulong id;
            public byte owner;
            public bool kinematic, gravity, sleeping, overlapping;
            public Vector3 bodyPosition, capsulePosition, requested, penetrationDirection;
            public Vector3 queryPoint, queryNormal;
            public Quaternion bodyRotation;
            public float stepOffset, penetration;
            public float queryRadius, queryDistance;
        }
        [Serializable] sealed class Report
        {
            public string caseName, unityVersion, failure;
            public int simulationVersion, policyVersion;
            public float fixedDeltaTime, radius, height, skinWidth, stepOffset, slopeLimit;
            public Vector3 center, lossyScale, gravity;
            public bool overlapRecovery, completed;
            public List<Row> rows = new();
            public List<OverlapSetup> overlapSetups = new();
        }
        [Serializable] sealed class OverlapSetup
        {
            public bool control, controllerEnabled, colliderEnabled;
            public Vector3 capsulePosition, capsuleCenter, capsuleBoundsCenter, capsuleBoundsSize;
            public Vector3 capsuleLocalCenter, capsuleScale;
            public Quaternion capsuleRotation;
            public Vector3 nominalPose, selectedPose, selectedDirection;
            public float targetDepth, nominalDepth, centerDepth, outsideDepth, selectedDepth;
            public float radius, height, skinWidth, minMoveDistance;
            public int probeCount;
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks?.Dynamic != null);
            _match = PrototypeMatch.Current; _player = PrototypePlayer.Local;
            _match.enabled = false;
            foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match); _fixture.Tick();
            _dt = 1f / GameplayConfig.Global.SimulationRate;
            Assert.That(_player.IsServer && _player.IsSpawned && _match.IsServer, Is.True);
            Assert.That(_match.Blocks.SurfaceOnly && World.Count == 0, Is.True);
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out _floor, 10,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(_match.Blocks.IsAuthoredMapCollider(_floor.collider) && _floor.normal.y > .99f, Is.True);
            _controller = _player.GetComponent<CharacterController>();
            _motor = (PlayerMotorSimulation)MotorField.GetValue(_player);
            _bridge = _player.GetComponent<PlayerBlockPushBridge>() ?? _player.gameObject.AddComponent<PlayerBlockPushBridge>();
            Assert.That(Begin != null && Move != null && End != null, Is.True, "Production bridge entry points");
            PlacePlayer();
            _report = new Report { caseName = TestContext.CurrentContext.Test.Name, unityVersion = Application.unityVersion,
                simulationVersion = GameplayContentSignature.WeaponSimulationVersion, policyVersion = BlockDynamicWorld.CollisionPolicyVersion,
                fixedDeltaTime = Time.fixedDeltaTime, radius = _controller.radius, height = _controller.height,
                skinWidth = _controller.skinWidth, stepOffset = _controller.stepOffset, slopeLimit = _controller.slopeLimit,
                center = _controller.center, lossyScale = _controller.transform.lossyScale, gravity = Physics.gravity,
                overlapRecovery = _controller.enableOverlapRecovery };
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            StopDriver();
            bool missingPublishedEvidence = _report != null &&
                _report.caseName == nameof(PublishedMotionUpdatesRealRaySphereAndStaleIdentity) &&
                (!_report.completed || _report.rows.Count != 5 ||
                 _report.rows.FindAll(row => row.stage == "real-aim-ray").Count != 2 ||
                 _report.rows.FindAll(row => row.stage == "real-aim-sphere").Count != 2 ||
                 _report.rows.FindAll(row => row.stage == "published-translated-rotated").Count != 1);
            if (_report != null)
            {
                if (missingPublishedEvidence && string.IsNullOrEmpty(_report.failure))
                    _report.failure = "Published motion body did not leave complete native query evidence.";
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Reports", "BlockPhysics20261017", "ReplicaCollision54"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + _report.caseName + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, JsonUtility.ToJson(_report, true));
                TestContext.Out.WriteLine("Native replica compatibility diagnostic: " + path);
                _report = null;
            }
            _fixture?.Dispose(); _fixture = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(missingPublishedEvidence, Is.False, "Published motion requires completed native ray/sphere/pose evidence after cleanup");
        }

        [UnityTest, Timeout(600000)] public IEnumerator ModesFreezeAuthorityTransitionsAndPoolReuse()
        {
            BlockDynamicBody original = null;
            BlockQueryContact stale = default;
            ulong originalId = 0;
            yield return Fixed(tick =>
            {
                if (tick == 0)
                {
                    World.SetAuthority(true);
                    Assert.That(World.TrySpawn(1, _floor.point + new Vector3(-2, 1, 0), Quaternion.identity, out original), Is.True);
                    originalId = original.Id;
                    Native(original, CollisionDetectionMode.ContinuousDynamic, false, true);
                    stale = Cast(original.Body.position, .01f).BlockQuery;
                    Sample("authority-awake", tick, original);
                    return false;
                }
                // Native Sleep is a controlled transition, not a claim of natural settling/managed range sleep.
                original.Body.Sleep(); Assert.That(original.Body.IsSleeping(), Is.True);
                Native(original, CollisionDetectionMode.ContinuousDynamic, false, true);
                Sample("authority-native-sleep", tick, original);
                World.Freeze(); Native(original, CollisionDetectionMode.ContinuousSpeculative, true, false);
                Sample("authority-frozen", tick, original);
                World.SetAuthority(false); Native(original, CollisionDetectionMode.Discrete, true, false);
                Sample("replica-frozen", tick, original);
                World.SetAuthority(true); Native(original, CollisionDetectionMode.ContinuousSpeculative, true, false);
                World.Reset(World.Round);
                Assert.That(World.TrySpawn(2, _floor.point + new Vector3(-2, 1, 0), Quaternion.identity, out var reused), Is.True);
                Assert.That(reused, Is.SameAs(original), "Exercise actual native component pool reuse");
                Assert.That(reused.Id, Is.GreaterThan(originalId));
                Assert.That(stale.TryResolveCurrent(out _), Is.False, "Old query cannot acquire the new ID");
                Native(reused, CollisionDetectionMode.ContinuousDynamic, false, true);
                World.SetAuthority(false); Native(reused, CollisionDetectionMode.Discrete, true, false);
                var state = State(reused.Id, reused.Owner, reused.Body.position, Quaternion.Euler(0, 20, 0));
                Assert.That(World.ApplySnapshot(World.Round, World.LifecycleRevision + 1, 20, new[] { state }, true), Is.True);
                Native(reused, CollisionDetectionMode.Discrete, true, false);
                Sample("pooled-frozen-replica", tick, reused);
                return true;
            });
        }

        [UnityTest, Timeout(600000)] public IEnumerator PublishedMotionUpdatesRealRaySphereAndStaleIdentity()
        {
            yield return Fixed(tick =>
            {
                var position = _floor.point + new Vector3(-2, 2, 0);
                var block = Replica(position);
                Cast(position, 0); Cast(position, .015f);
                Vector3 moved = position + Vector3.right * 1.2f;
                Quaternion rotation = Quaternion.Euler(20, 90, 15);
                var state = State(block.Id, block.Owner, moved, rotation);
                Assert.That(World.ApplyMotion(World.Round, World.LifecycleRevision, 11, new[] { state }), Is.True);
                Assert.That(Vector3.Distance(block.Body.position, moved), Is.LessThan(.00001f));
                foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                    Assert.That(Vector3.Distance(block.Body.rotation * axis, rotation * axis), Is.LessThan(.0001f));
                foreach (float radius in new[] { 0f, .015f })
                {
                    var hit = Cast(moved, radius);
                    Assert.That(BlockAttackService.Contact(hit, out var contact), Is.True);
                    Assert.That(contact.DynamicId, Is.EqualTo(block.Id));
                    Assert.That(contact.Owner, Is.EqualTo(block.Owner));
                    Assert.That(_aim.ClosestCast(position + Vector3.back, Vector3.forward, 2, radius, ulong.MaxValue, out var old) &&
                        old.BlockQuery.IsKnown && old.BlockQuery.Contact.DynamicId == block.Id, Is.False, "Old endpoint no longer contains the moved ID");
                }
                var older = State(block.Id, block.Owner, position, Quaternion.identity);
                Assert.That(World.ApplyMotion(World.Round, World.LifecycleRevision, 9, new[] { older }), Is.True);
                Assert.That(Vector3.Distance(block.Body.position, moved), Is.LessThan(.00001f), "Older per-body sequence cannot rewind the pose");
                Assert.That(World.ApplyMotion(World.Round + 1, World.LifecycleRevision, 12, new[] { state }), Is.False);
                Assert.That(World.ApplyMotion(World.Round, World.LifecycleRevision + 1, 12, new[] { state }), Is.False);
                Sample("published-translated-rotated", tick, block);
                return true;
            });
        }

        [UnityTest, Timeout(600000)] public IEnumerator CapsuleSideBlockAndStepOffsetRestoreMatchSpeculativeControl()
        {
            Vector3 baseline = default;
            foreach (bool control in new[] { true, false })
            {
                BlockDynamicBody block = null; Vector3 fixedPose = default;
                yield return Fixed(tick =>
                {
                    if (tick == 0)
                    {
                        World.Reset(World.Round); PlacePlayer();
                        float distance = _controller.radius + BlockWorld.CellSize * .5f + _controller.skinWidth + .08f;
                        fixedPose = _floor.point + new Vector3(0, BlockWorld.CellSize * .5f + .006f, distance);
                        block = Replica(fixedPose + Vector3.right * 1.2f);
                        if (control) block.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                        Assert.That(World.ApplyMotion(World.Round, World.LifecycleRevision, 11,
                            new[] { State(block.Id, block.Owner, fixedPose, Quaternion.identity) }), Is.True);
                    }
                    Vector3 request = Vector3.forward * (3 * _dt) + Vector3.down * .02f;
                    CollisionFlags flags = BridgeMove(request);
                    Sample(control ? "side-speculative-control" : "side-discrete", tick, block, flags, request);
                    Assert.That(Vector3.Distance(block.Body.position, fixedPose), Is.LessThan(.00001f), "Replica receives no local push");
                    Assert.That(_controller.transform.position.y - Lane.y, Is.LessThan(.04f), "Grounded approach cannot climb the cube");
                    if (tick < 29) return false;
                    float limit = block.Collider.bounds.min.z - _controller.radius + _controller.skinWidth + .02f;
                    Assert.That(_controller.transform.position.z, Is.LessThanOrEqualTo(limit), "Actual capsule remains on near side");
                    Assert.That(_controller.transform.position.z, Is.GreaterThan(Lane.z + .01f), "Approach issued real movement before blocking");
                    if (control) baseline = _controller.transform.position;
                    else Assert.That(Vector3.Distance(_controller.transform.position, baseline), Is.LessThan(.02f), "Declared CC endpoint comparison tolerance");
                    return true;
                });
            }
        }

        [UnityTest, Timeout(600000)] public IEnumerator MotionIntoCapsuleHasComparableMildOverlapRecovery()
        {
            Vector3 baseline = default; float baselineDepth = 0;
            Vector3 overlapOffset = default;
            foreach (bool control in new[] { true, false })
            {
                BlockDynamicBody block = null; float initialDepth = 0;
                yield return Fixed(tick =>
                {
                    if (tick == 0)
                    {
                        World.Reset(World.Round); PlacePlayer();
                        Assert.That(_controller.enableOverlapRecovery, Is.True, "Capture and use authored default recovery; do not toggle it");
                        Vector3 center = _controller.transform.TransformPoint(_controller.center);
                        block = Replica(center + Vector3.right * 2);
                        if (control) block.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                        float overlap = Mathf.Max(.001f, _controller.skinWidth * .5f);
                        var setup = new OverlapSetup { control = control, controllerEnabled = _controller.enabled,
                            colliderEnabled = block.Collider.enabled, capsulePosition = _controller.transform.position,
                            capsuleRotation = _controller.transform.rotation, capsuleCenter = center,
                            capsuleBoundsCenter = _controller.bounds.center, capsuleBoundsSize = _controller.bounds.size,
                            capsuleLocalCenter = _controller.center, capsuleScale = _controller.transform.lossyScale,
                            radius = _controller.radius, height = _controller.height, skinWidth = _controller.skinWidth,
                            minMoveDistance = _controller.minMoveDistance,
                            nominalPose = center + Vector3.right * (_controller.radius + BlockWorld.CellSize * .5f - overlap),
                            targetDepth = overlap };
                        _report.overlapSetups.Add(setup);
                        if (!control)
                        {
                            var first = _report.overlapSetups[0];
                            Assert.That(setup.capsulePosition, Is.EqualTo(first.capsulePosition));
                            Assert.That(setup.capsuleRotation, Is.EqualTo(first.capsuleRotation));
                            Assert.That(setup.capsuleCenter, Is.EqualTo(first.capsuleCenter));
                            Assert.That(setup.capsuleLocalCenter, Is.EqualTo(first.capsuleLocalCenter));
                            Assert.That(setup.capsuleScale, Is.EqualTo(first.capsuleScale));
                            Assert.That(setup.radius, Is.EqualTo(first.radius));
                            Assert.That(setup.height, Is.EqualTo(first.height));
                            Assert.That(setup.skinWidth, Is.EqualTo(first.skinWidth));
                            Assert.That(setup.minMoveDistance, Is.EqualTo(first.minMoveDistance));
                        }
                        setup.nominalDepth = PenetrationAt(block, setup.nominalPose, out _);
                        // Query the actual native pair at hypothetical poses; these probes never move it.
                        // Only the control chooses geometry. Discrete reuses that exact relative pose.
                        if (control) overlapOffset = FindMildOverlapOffset(block, center, setup);
                        setup.selectedPose = center + overlapOffset;
                        setup.selectedDepth = PenetrationAt(block, setup.selectedPose, out var direction);
                        setup.selectedDirection = direction;
                        Assert.That(setup.selectedDepth, Is.EqualTo(overlap).Within(.0001f), "Same native mild-overlap geometry in both modes");
                        Assert.That(Vector3.Dot(direction, Vector3.right), Is.GreaterThan(.9f), "Native minimum translation is lateral");
                        var state = State(block.Id, block.Owner, setup.selectedPose, Quaternion.identity);
                        Assert.That(World.ApplyMotion(World.Round, World.LifecycleRevision, 11, new[] { state }), Is.True);
                        initialDepth = Penetration(block, out _);
                        Sample(control ? "overlap-speculative-before" : "overlap-discrete-before", tick, block);
                        Assert.That(initialDepth, Is.GreaterThan(0), "Real published native overlap exists before Move");
                        Assert.That(initialDepth, Is.EqualTo(overlap).Within(.0001f), "ApplyMotion publishes the calibrated native overlap");
                    }
                    float moveDistance = Mathf.Max(.005f, _controller.minMoveDistance * 2);
                    Assert.That(moveDistance, Is.GreaterThan(_controller.minMoveDistance), "Request exceeds the authored native movement threshold");
                    Vector3 request = Vector3.down * moveDistance;
                    var flags = BridgeMove(request);
                    Sample(control ? "overlap-speculative-control" : "overlap-discrete", tick, block, flags, request);
                    Assert.That(_controller.transform.position.y - Lane.y, Is.LessThan(.04f), "No extra upward escape from mild side overlap");
                    if (tick < 29) return false;
                    Assert.That(Vector3.Distance(_controller.transform.position, _report.overlapSetups[_report.overlapSetups.Count - 1].capsulePosition),
                        Is.GreaterThan(_controller.minMoveDistance), "Actual capsule movement witnesses the overlap comparison");
                    float depth = Penetration(block, out _);
                    Assert.That(depth, Is.LessThanOrEqualTo(initialDepth + .002f), "Recovery cannot deepen the initial overlap");
                    if (control) { baseline = _controller.transform.position; baselineDepth = depth; }
                    else
                    {
                        Assert.That(depth, Is.LessThanOrEqualTo(baselineDepth + .002f), "No extra persistent penetration relative to native control");
                        Assert.That(Vector3.Distance(_controller.transform.position, baseline), Is.LessThan(.02f));
                    }
                    return true;
                });
            }
        }

        Vector3 FindMildOverlapOffset(BlockDynamicBody block, Vector3 center, OverlapSetup setup)
        {
            float inside = 0, outside = _controller.radius + BlockWorld.CellSize * .5f + _controller.skinWidth + .05f;
            setup.centerDepth = PenetrationAt(block, center, out _);
            setup.outsideDepth = PenetrationAt(block, center + Vector3.right * outside, out _);
            setup.probeCount = 2;
            Assert.That(setup.centerDepth, Is.GreaterThan(setup.targetDepth), "Actual native pair has a bounded overlapping endpoint");
            Assert.That(setup.outsideDepth, Is.EqualTo(0), "Actual native pair has a bounded separated endpoint");
            for (int i = 0; i < 24; i++)
            {
                float distance = (inside + outside) * .5f;
                float depth = PenetrationAt(block, center + Vector3.right * distance, out _);
                setup.probeCount++;
                Assert.That(float.IsFinite(depth), Is.True);
                if (depth >= setup.targetDepth) inside = distance;
                else outside = distance;
            }
            return Vector3.right * ((inside + outside) * .5f);
        }

        IEnumerator Fixed(Func<int, bool> step)
        {
            _report.completed = false;
            int tick = 0; bool done = false; double previous = 0;
            _driver = new GameObject("ReplicaCollisionFixedDriver").AddComponent<BlockPushRecoveryFixedDriver>();
            _driver.OnFixedStep = () =>
            {
                Assert.That(Time.inFixedTimeStep, Is.True);
                Assert.That(Physics.simulationMode, Is.EqualTo(SimulationMode.FixedUpdate));
                Assert.That(Time.timeScale, Is.EqualTo(1f));
                Assert.That(Time.fixedDeltaTime, Is.EqualTo(_dt).Within(.000001f));
                if (tick > 0) Assert.That(Time.fixedTimeAsDouble - previous, Is.EqualTo((double)_dt).Within(.00001));
                previous = Time.fixedTimeAsDouble;
                _fixedTick = tick;
                done = step(tick++); return done;
            };
            double deadline = Time.realtimeSinceStartupAsDouble + 30;
            while (!done && _driver.Failure == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            var failure = _driver.Failure; StopDriver();
            if (failure != null) { _report.failure = failure.ToString(); throw failure; }
            Assert.That(done, Is.True, "Bounded automatic fixed-loop fixture completed");
            _report.completed = true;
        }

        void StopDriver()
        {
            if (_driver == null) return;
            _driver.OnFixedStep = null; _driver.enabled = false;
            UnityEngine.Object.Destroy(_driver.gameObject); _driver = null;
        }
        static IEnumerator Wait(Func<bool> ready)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 60;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, "Production room startup completed within bound");
        }
        void PlacePlayer()
        {
            var current = _player.Snapshot.Value;
            var state = new PlayerSnapshot { HeroId = 1, HeroRevision = current.HeroRevision, Team = current.Team, Slot = current.Slot,
                Health = 100, Ink = 100, LifeState = PlayerLifeState.Alive, Revision = current.Revision, Position = Lane,
                Grounded = true, Movement = MovementMode.Human, SubWeaponId = 1, SpecialWeaponId = 1,
                SpecialPhase = SpecialPhase.Charging, AttackNeedsRelease = true, SubNeedsRelease = true, SpecialNeedsRelease = true };
            _motor.Restore(state); _player.Snapshot.Value = state;
            Assert.That(_motor.CanStand(Lane), Is.True, "Initial capsule fits the real authored map");
        }
        BlockDynamicState State(ulong id, byte owner, Vector3 position, Quaternion rotation) => new()
            { Id = id, Round = World.Round, Owner = owner, Position = position, Rotation = rotation };
        BlockDynamicBody Replica(Vector3 position)
        {
            World.SetAuthority(false);
            Assert.That(World.ApplySnapshot(World.Round, World.LifecycleRevision + 1, 10,
                new[] { State(700, 2, position, Quaternion.identity) }, false), Is.True);
            var body = World.Bodies[700]; Native(body, CollisionDetectionMode.Discrete, true, false); return body;
        }
        static void Native(BlockDynamicBody block, CollisionDetectionMode mode, bool kinematic, bool gravity)
        {
            Assert.That(block.World.Contains(block) && block.Collider.enabled && block.gameObject.activeInHierarchy, Is.True);
            Assert.That(block.Body.collisionDetectionMode, Is.EqualTo(mode));
            Assert.That(block.Body.isKinematic, Is.EqualTo(kinematic)); Assert.That(block.Body.useGravity, Is.EqualTo(gravity));
            Assert.That(block.Body.mass, Is.EqualTo(1f)); Assert.That(block.Collider.size, Is.EqualTo(Vector3.one * .3f));
            Assert.That(block.gameObject.layer, Is.EqualTo(BlockDynamicWorld.Layer));
        }
        TpsCollision Cast(Vector3 position, float radius)
        {
            Assert.That(_aim.ClosestCast(position + Vector3.back, Vector3.forward, 2, radius, ulong.MaxValue, out var hit), Is.True);
            Assert.That(hit.BlockQuery.IsKnown && hit.BlockQuery.TryResolveCurrent(out _), Is.True);
            Sample(radius == 0 ? "real-aim-ray" : "real-aim-sphere", _fixedTick, hit.BlockQuery.DynamicBody);
            var row = _report.rows[_report.rows.Count - 1];
            row.queryRadius = radius; row.queryDistance = hit.Distance; row.queryPoint = hit.Point; row.queryNormal = hit.Normal;
            return hit;
        }
        CollisionFlags BridgeMove(Vector3 request)
        {
            float offset = _controller.stepOffset;
            // Use the real disabled host object outside its server-authority simulation scope.
            // Never inject the bridge's private authority field or claim independent client dispatch.
            Begin.Invoke(_bridge, new object[] { _player, _dt });
            try { return (CollisionFlags)Move.Invoke(_bridge, new object[] { request, _dt }); }
            finally { End.Invoke(_bridge, new object[] { false }); Assert.That(_controller.stepOffset, Is.EqualTo(offset)); }
        }
        float Penetration(BlockDynamicBody block, out Vector3 direction)
            => PenetrationAt(block, block.Body.position, out direction);
        float PenetrationAt(BlockDynamicBody block, Vector3 position, out Vector3 direction)
        {
            if (Physics.ComputePenetration(block.Collider, position, block.Body.rotation, _controller,
                _controller.transform.position, _controller.transform.rotation, out direction, out float depth)) return depth;
            direction = Vector3.zero; return 0;
        }
        void Sample(string stage, int tick, BlockDynamicBody block, CollisionFlags flags = CollisionFlags.None, Vector3 request = default)
        {
            float depth = Penetration(block, out var direction);
            _report.rows.Add(new Row { stage = stage, fixedTick = tick, fixedTime = Time.fixedTimeAsDouble,
                round = World.Round, revision = World.LifecycleRevision, id = block.Id, owner = block.Owner,
                mode = (int)block.Body.collisionDetectionMode, kinematic = block.Body.isKinematic, gravity = block.Body.useGravity,
                sleeping = !block.Body.isKinematic && block.Body.IsSleeping(), bodyPosition = block.Body.position, bodyRotation = block.Body.rotation,
                capsulePosition = _controller.transform.position, stepOffset = _controller.stepOffset,
                flags = (int)flags, requested = request, penetration = depth, overlapping = depth > 0, penetrationDirection = direction });
        }
    }
}
#endif
