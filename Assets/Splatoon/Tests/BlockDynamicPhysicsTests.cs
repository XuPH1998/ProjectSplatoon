#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockDynamicPhysicsTests
    {
        GameObject _root;
        BlockDynamicWorld _world;
        SimulationMode _simulation;
        bool[,] _ignored;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _simulation = Physics.simulationMode;
            _ignored = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) _ignored[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            Physics.simulationMode = SimulationMode.Script;
            _root = new GameObject("Independent block PhysX fixture");
            var terrain = _root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
            _world = terrain.Dynamic; _world.SetAuthority(true);
        }

        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            Physics.simulationMode = _simulation;
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) Physics.IgnoreLayerCollision(a, b, _ignored[a, b]);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Floor(int layer = 0, float top = 0)
        {
            var floor = new GameObject("Physical support") { layer = layer };
            floor.transform.SetParent(_root.transform);
            floor.transform.position = new Vector3(0, top - .1f, 0);
            floor.AddComponent<BoxCollider>().size = new Vector3(12, .2f, 12);
            Physics.SyncTransforms();
        }

        static void Steps(int count)
        {
            for (int i = 0; i < count; i++) Physics.Simulate(.02f);
            Physics.SyncTransforms();
        }

        const BindingFlags PushPrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        const float PushStep = .02f;

        static void InvokePushBridge(PlayerBlockPushBridge bridge, string method, params object[] arguments)
        {
            var member = typeof(PlayerBlockPushBridge).GetMethod(method, PushPrivate);
            Assert.That(member, Is.Not.Null, "Production push bridge method: " + method);
            member.Invoke(bridge, arguments);
        }

        PlayerBlockPushBridge IsolatedPushStep(bool authority)
        {
            var player = new GameObject("Isolated production push step"); player.transform.SetParent(_root.transform);
            var controller = player.AddComponent<CharacterController>(); controller.enabled = false;
            var bridge = player.AddComponent<PlayerBlockPushBridge>();
            // Supply only the authority/request boundary that normally comes
            // from PrototypePlayer and Move. Collection, deduplication, force
            // sharing and native AddForceAtPosition all execute production code.
            InvokePushBridge(bridge, "BeginStep", null, PushStep);
            var authorityField = typeof(PlayerBlockPushBridge).GetField("_authority", PushPrivate);
            var velocityField = typeof(PlayerBlockPushBridge).GetField("_requestedVelocity", PushPrivate);
            Assert.That(authorityField, Is.Not.Null); Assert.That(velocityField, Is.Not.Null);
            authorityField.SetValue(bridge, authority);
            velocityField.SetValue(bridge, Vector3.right * 3);
            return bridge;
        }

        BlockDynamicBody IsolatedPushBody(Vector3 position, byte owner = 1)
        {
            Assert.That(_world.TrySpawn(owner, position, Quaternion.identity, out var block), Is.True);
            // No floor, capsule contact or damping may add another impulse to
            // this native force/torque measurement. Mass and inertia stay real.
            block.Body.useGravity = false;
            block.Body.linearDamping = block.Body.angularDamping = 0;
            Physics.SyncTransforms();
            Assert.That(block.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass));
            return block;
        }

        [Test] public void WorldOriginOverlapPointKeepsRealPushTorqueLocalToTheDistantCube()
        {
            var block = IsolatedPushBody(new Vector3(8, 3, 11));
            Assert.That(block.Body.worldCenterOfMass.magnitude, Is.GreaterThan(10), "A world-origin lever would be much larger than this cube");
            var bridge = IsolatedPushStep(true);
            InvokePushBridge(bridge, "Collect", block, Vector3.zero);
            InvokePushBridge(bridge, "EndStep", true);

            Vector3 force = block.Body.GetAccumulatedForce(PushStep);
            Vector3 torque = block.Body.GetAccumulatedTorque(PushStep);
            const float expectedForce = 75; // 0.5 kg reaches the requested 3 m/s in 0.02 s, below the 100 N player budget.
            Assert.That(Vector3.Distance(force, Vector3.right * expectedForce), Is.LessThan(.0001f), "The production EndStep queues only the force needed for the requested speed");
            float localTorqueLimit = expectedForce * BlockWorld.CellSize * Mathf.Sqrt(3) * .5f;
            Assert.That(torque.magnitude, Is.GreaterThan(.1f), "An off-centre native force must exercise a real torque");
            Assert.That(torque.magnitude, Is.LessThanOrEqualTo(localTorqueLimit + .001f), "The lever stays inside the cube instead of reaching the world origin");
            var inertia = block.Body.inertiaTensor;
            float minimumInertia = Mathf.Min(inertia.x, Mathf.Min(inertia.y, inertia.z));
            Assert.That(minimumInertia, Is.GreaterThan(0));
            float angularLimit = localTorqueLimit * PushStep / minimumInertia;

            Steps(1);
            Assert.That(Vector3.Distance(block.Body.linearVelocity, Vector3.right * (expectedForce * PushStep / block.Body.mass)), Is.LessThan(.0001f), "Real PhysX integrates the speed-limited force");
            Assert.That(block.Body.angularVelocity.magnitude, Is.GreaterThan(.1f));
            // The native torque bound above is independent of the angular-speed cap.
            // Keep its integrated upper bound without assuming when PhysX applies that cap.
            Assert.That(block.Body.angularVelocity.magnitude, Is.LessThanOrEqualTo(angularLimit + .01f), "Real angular motion remains bounded by the cube's local inertia and lever");
        }

        [Test] public void RepeatedSameIdContactPushesOnceAndSharesOneSharedPlayerBudget()
        {
            var first = IsolatedPushBody(new Vector3(7, 3, 10));
            var second = IsolatedPushBody(new Vector3(9, 3, 10), 2);
            var bridge = IsolatedPushStep(true);
            int firstCalls = 0, secondCalls = 0; float observedTotal = 0;
            void Observe(ulong owner, BlockDynamicBody block, Vector3 force)
            {
                if (block != first && block != second) return;
                if (block == first) firstCalls++; else secondCalls++;
                observedTotal += force.magnitude;
            }
            PlayerBlockPushBridge.PushObserved += Observe;
            try
            {
                InvokePushBridge(bridge, "Collect", first, Vector3.zero);
                InvokePushBridge(bridge, "Collect", first, Vector3.zero);
                InvokePushBridge(bridge, "Collect", first, Vector3.zero);
                InvokePushBridge(bridge, "Collect", second, Vector3.zero);
                InvokePushBridge(bridge, "EndStep", true);
                Assert.That(firstCalls, Is.EqualTo(1), "Multiple cast/overlap contacts for one ID enqueue only one native force");
                Assert.That(secondCalls, Is.EqualTo(1));
                Assert.That(observedTotal, Is.EqualTo(100).Within(.0001f));
            }
            finally { PlayerBlockPushBridge.PushObserved -= Observe; }

            Vector3 firstForce = first.Body.GetAccumulatedForce(PushStep), secondForce = second.Body.GetAccumulatedForce(PushStep);
            Assert.That(Vector3.Distance(firstForce, Vector3.right * 50), Is.LessThan(.0001f));
            Assert.That(Vector3.Distance(secondForce, Vector3.right * 50), Is.LessThan(.0001f));
            Assert.That(firstForce.magnitude + secondForce.magnitude, Is.EqualTo(100).Within(.0001f), "The player force budget is shared across real bodies");
            Steps(1);
            foreach (var block in new[] { first, second })
                Assert.That(Vector3.Distance(block.Body.linearVelocity, Vector3.right * (50 * PushStep / block.Body.mass)), Is.LessThan(.0001f));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void UnauthoritativeOrUncommittedPushQueuesNoNativeForce(bool authority, bool committed)
        {
            var block = IsolatedPushBody(new Vector3(8, 3, 11));
            var position = block.Body.position; var rotation = block.Body.rotation;
            var bridge = IsolatedPushStep(authority);
            InvokePushBridge(bridge, "Collect", block, Vector3.zero);
            InvokePushBridge(bridge, "EndStep", committed);
            Assert.That(block.Body.GetAccumulatedForce(PushStep).sqrMagnitude, Is.LessThan(.000000000001f));
            Assert.That(block.Body.GetAccumulatedTorque(PushStep).sqrMagnitude, Is.LessThan(.000000000001f));
            Steps(1);
            Assert.That(Vector3.Distance(block.Body.position, position), Is.LessThan(.000001f));
            Assert.That(Quaternion.Angle(block.Body.rotation, rotation), Is.LessThan(.0001f));
            Assert.That(block.Body.linearVelocity.sqrMagnitude, Is.LessThan(.000000000001f));
            Assert.That(block.Body.angularVelocity.sqrMagnitude, Is.LessThan(.000000000001f));
        }

        [Test] public void FinishRoundAndFollowingTerrainTickKeepOneCanonicalFrozenSnapshot()
        {
            Assert.That(_world.TrySpawn(1, new Vector3(0, 2, 0), Quaternion.Euler(10, 20, 30), out var moving), Is.True);
            moving.Body.linearVelocity = new Vector3(2, 1, -3);
            moving.Body.angularVelocity = new Vector3(1, 3, 2);
            var before = new List<BlockDynamicState>(); _world.CaptureStates(before);
            Assert.That(before[0].Sleeping, Is.False);
            Assert.That(before[0].Velocity.sqrMagnitude, Is.GreaterThan(0));
            Assert.That(before[0].AngularVelocity.sqrMagnitude, Is.GreaterThan(0));

            var terrain = _root.GetComponent<BlockTerrain>();
            terrain.FinishRound(180);
            var early = new List<BlockDynamicState>(); _world.CaptureStates(early);
            byte[] earlyBytes = BlockDynamicCodec.Encode(_world.Round, early, System.Array.Empty<ulong>());
            uint revision = _world.LifecycleRevision;
            Assert.That(_world.IsAuthority, Is.True, "The first capture is taken in the actual FinishRound frame");

            terrain.Tick(true, 180.02, .02f);
            Assert.That(_world.IsAuthority, Is.False, "The production Tick disables simulation after FinishRound");
            Assert.That(_world.Frozen, Is.True);
            Assert.That(_world.LifecycleRevision, Is.EqualTo(revision));
            var later = new List<BlockDynamicState>(); _world.CaptureStates(later);
            Assert.That(later[0].Sleeping, Is.True);
            Assert.That(later[0].Velocity, Is.EqualTo(Vector3.zero));
            Assert.That(later[0].AngularVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(BlockDynamicCodec.Encode(_world.Round, later, System.Array.Empty<ulong>()), Is.EqualTo(earlyBytes), "Early peers and later reconnects receive identical bytes at one frozen revision");
        }

        [Test] public void KinematicReplicaRetainsRemoteMotionUntilTheWorldActuallyFreezes()
        {
            _world.SetAuthority(false);
            var remote = new BlockDynamicState { Id = 700, Round = _world.Round, Owner = 2,
                Position = new Vector3(0, 2, 0), Rotation = Quaternion.identity,
                Velocity = new Vector3(2, 1, -3), AngularVelocity = new Vector3(1, 3, 2), Sleeping = false };
            Assert.That(_world.ApplySnapshot(_world.Round, 1, 1, new[] { remote }, false), Is.True);
            Assert.That(_world.Bodies[remote.Id].Body.isKinematic, Is.True);
            var states = new List<BlockDynamicState>(); _world.CaptureStates(states);
            Assert.That(states[0].Velocity, Is.EqualTo(remote.Velocity));
            Assert.That(states[0].AngularVelocity, Is.EqualTo(remote.AngularVelocity));
            Assert.That(states[0].Sleeping, Is.False);
            _world.Freeze(); _world.CaptureStates(states);
            Assert.That(states[0].Sleeping, Is.True);
            Assert.That(states[0].Velocity, Is.EqualTo(Vector3.zero));
            Assert.That(states[0].AngularVelocity, Is.EqualTo(Vector3.zero));
        }

        BlockDynamicWorld FrozenReplica(IReadOnlyList<BlockDynamicState> states, uint revision)
        {
            var root = new GameObject("Frozen lifecycle client replica"); root.transform.SetParent(_root.transform, false);
            var terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
            var replica = terrain.Dynamic; replica.SetAuthority(false);
            Assert.That(replica.ApplySnapshot(_world.Round, revision, 1000, states, true), Is.True);
            return replica;
        }

        static void AssertActualFrozenPose(BlockDynamicWorld replica, IReadOnlyList<BlockDynamicState> expected)
        {
            foreach (var state in expected)
            {
                Assert.That(replica.TryGet(state.Id, out var body), Is.True);
                Assert.That(body.Body.isKinematic, Is.True);
                Assert.That(body.Body.useGravity, Is.False);
                Assert.That(Vector3.Distance(body.Body.position, state.Position), Is.LessThanOrEqualTo(.000001f), "Actual native Rigidbody position");
                var rotation = Quaternion.Normalize(state.Rotation);
                // Compare rotated basis vectors so unit-quaternion roundoff does not
                // become a spurious angle from acos(dot) near exactly one.
                foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                    Assert.That(Vector3.Distance(body.Body.rotation * axis, rotation * axis), Is.LessThanOrEqualTo(.00001f), "Actual native Rigidbody orientation");
                Assert.That(body.Body.linearVelocity.sqrMagnitude, Is.LessThanOrEqualTo(.000000000001f));
                Assert.That(body.Body.angularVelocity.sqrMagnitude, Is.LessThanOrEqualTo(.000000000001f));
            }
        }

        static void AssertSameFrozenBytes(uint round, IReadOnlyList<BlockDynamicState> expected, List<BlockDynamicState> actual)
        {
            string difference = "No per-body difference found.";
            for (int i = 0; i < expected.Count && i < actual.Count; i++)
            {
                var a = expected[i]; var b = actual[i];
                if (a.Position.Equals(b.Position) && a.Rotation.Equals(b.Rotation) && a.Velocity.Equals(b.Velocity) &&
                    a.AngularVelocity.Equals(b.AngularVelocity) && a.Sleeping == b.Sleeping) continue;
                difference = $"First differing ID {a.Id}: host position ({a.Position.x:R},{a.Position.y:R},{a.Position.z:R}) native ({b.Position.x:R},{b.Position.y:R},{b.Position.z:R}); host quaternion ({a.Rotation.x:R},{a.Rotation.y:R},{a.Rotation.z:R},{a.Rotation.w:R}) native ({b.Rotation.x:R},{b.Rotation.y:R},{b.Rotation.z:R},{b.Rotation.w:R}).";
                break;
            }
            Assert.That(BlockDynamicCodec.Encode(round, actual, System.Array.Empty<ulong>()),
                Is.EqualTo(BlockDynamicCodec.Encode(round, expected, System.Array.Empty<ulong>())), difference);
        }

        [Test] public void RealPhysXRotationsSurviveFrozenLifecycleReplicaWithoutChangingCanonicalBytes()
        {
            for (int i = 0; i < 96; i++)
            {
                float spacing = BlockWorld.CellSize * 2;
                var position = new Vector3((i % 12 - 6) * spacing, 2, (i / 12 - 4) * spacing);
                Assert.That(_world.TrySpawn((byte)(i % 2 + 1), position, Quaternion.Euler(i * 37 + 13, i * 73 + 17, i * 11 + 19), out var body), Is.True);
                body.Body.linearVelocity = new Vector3((i % 3 - 1) * .1f, .2f, (i % 5 - 2) * .1f);
                body.Body.angularVelocity = new Vector3(.3f + i * .01f, 1.1f, .7f);
            }
            Steps(15);
            var terrain = _root.GetComponent<BlockTerrain>(); terrain.FinishRound(180); terrain.Tick(true, 180.02, .02f);
            var expected = new List<BlockDynamicState>(); _world.CaptureStates(expected);
            var replica = FrozenReplica(expected, _world.LifecycleRevision);
            AssertActualFrozenPose(replica, expected);
            var actual = new List<BlockDynamicState>(); replica.CaptureStates(actual);
            AssertSameFrozenBytes(_world.Round, expected, actual);
        }

        [Test] public void LegalWireQuaternionNativeNormalizationCannotChangeFrozenLifecycleBytes()
        {
            var rotation = Quaternion.Euler(17, 38, -23);
            rotation = new Quaternion(rotation.x * 1.0001f, rotation.y * 1.0001f, rotation.z * 1.0001f, rotation.w * 1.0001f);
            var expected = new[] { new BlockDynamicState { Id = 700, Round = _world.Round, Owner = 2,
                Position = new Vector3(.35f, 2.15f, -.45f), Rotation = rotation, Sleeping = true } };
            Assert.That(BlockDynamicCodec.Valid(expected[0], _world.Round), Is.True, "The signed wire protocol accepts finite near-unit quaternions");
            var replica = FrozenReplica(expected, 1);
            AssertActualFrozenPose(replica, expected);
            var actual = new List<BlockDynamicState>(); replica.CaptureStates(actual);
            AssertSameFrozenBytes(_world.Round, expected, actual);
        }

        [Test] public void GravityRotationStackContactSleepAndWakePreserveEveryBody()
        {
            Floor();
            Assert.That(_world.TrySpawn(1, new Vector3(0, 1, 0), Quaternion.identity, out var lower), Is.True);
            Assert.That(_world.TrySpawn(2, new Vector3(0, 1.5f, 0), Quaternion.identity, out var upper), Is.True);
            upper.Body.angularVelocity = new Vector3(0, 3, 0);
            Steps(10);
            Assert.That(lower.Body.position.y, Is.LessThan(.95f), "Real gravity moves the independent mass");
            Assert.That(Quaternion.Angle(upper.Body.rotation, Quaternion.identity), Is.GreaterThan(5), "Rotation is unconstrained");
            Steps(390);
            Assert.That(lower.Body.position.y, Is.EqualTo(BlockWorld.CellSize * .5f).Within(.025f));
            Assert.That(upper.Body.position.y, Is.GreaterThan(BlockWorld.CellSize * 1.35f), "Another independent block supports its volume");
            Assert.That(lower.Body.IsSleeping(), Is.True); Assert.That(upper.Body.IsSleeping(), Is.True);
            ulong identity = upper.Id;
            upper.Body.AddForce(new Vector3(1.5f, .5f, 0), ForceMode.Impulse);
            Steps(20);
            Assert.That(upper.Body.position.x, Is.GreaterThan(.15f), "A sleeping block wakes and remains movable");
            Assert.That(upper.Id, Is.EqualTo(identity)); Assert.That(_world.Count, Is.EqualTo(2));
            Assert.That(_world.PinkCount + _world.BlueCount, Is.EqualTo(2));
        }

        [Test] public void AttachedCollisionSupportsFreeMassAndMovingRotatedQueriesKeepStableIdentity()
        {
            Floor(BlockTerrain.TerrainLayer, BlockWorld.CellSize);
            Assert.That(Physics.GetIgnoreLayerCollision(8, BlockTerrain.TerrainLayer), Is.True);
            Assert.That(_world.TrySpawn(1, new Vector3(0, 1, 0), Quaternion.identity, out var body), Is.True);
            Steps(220);
            Assert.That(body.Body.position.y, Is.EqualTo(BlockWorld.CellSize * 1.5f).Within(.025f), "Coating remains a real obstacle for free masses");
            var solver = new TpsAimSolver();
            Assert.That(solver.ClosestCast(body.Body.position + Vector3.back, Vector3.forward, 2, .01f, ulong.MaxValue, out var before), Is.True);
            Assert.That(before.BlockQuery.IsKnown, Is.True);
            ulong id = body.Id;
            body.Body.position = new Vector3(1, 1, 0); body.Body.rotation = Quaternion.Euler(20, 35, 15);
            Physics.SyncTransforms();
            Assert.That(solver.ClosestCast(body.Body.position + Vector3.back, Vector3.forward, 2, .01f, ulong.MaxValue, out var moved), Is.True);
            Assert.That(BlockAttackService.Contact(moved, out var contact), Is.True);
            Assert.That(contact.DynamicId, Is.EqualTo(id));
            Assert.That(_world.DestroyIds(new[] { id }, 2), Is.EqualTo(1));
            Assert.That(_world.TrySpawn(1, new Vector3(1, 1, 0), Quaternion.identity, out var replacement), Is.True);
            Assert.That(replacement.Id, Is.GreaterThan(id));
            Assert.That(before.BlockQuery.TryResolveCurrent(out _), Is.False, "A pooled collider cannot retarget an old shot");
            Assert.That(moved.BlockQuery.TryResolveCurrent(out _), Is.False);
        }
    }
}
#endif
