#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
using UnityEditor.SceneManagement;

namespace Splatoon.Tests
{
    public sealed class BlockBlastHoldInputTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void RejectedProductionPressMustReallyReleaseBeforeRetryAndStillPayOnlyOnFinalRelease(bool busy)
        {
            var asset = ScriptableObject.CreateInstance<SubWeaponConfigAsset>();
            SubWeaponDefaults.Apply(asset, SubWeaponType.BurstBomb);
            try
            {
                var config = asset.Snapshot();
                var state = new PlayerSnapshot { Health = 100, Ink = 100, Revision = 1, HeroRevision = 1,
                    SubReadyAt = busy ? 1 : 0 };
                var actor = new BlockDynamicAcceptanceProbe.BlastPressureActor { holdInput = true, holdAttempts = 1,
                    holdAttemptAt = 0, authorityInputSeen = true, authorityInputPhase = "hold",
                    authorityInputHeld = true, authorityInputPress = 1 };
                var input = new PlayerInputFrame { Revision = 1, HeroRevision = 1, SubHeld = true, SubPressSequence = 1 };
                SubWeaponSimulation.Step(ref state, input, config, 1f / 60, 0, busy, SubWeaponFailure.None, out _);
                Assert.That(state.SubFailure, Is.EqualTo(busy ? SubWeaponFailure.Busy : SubWeaponFailure.NoStandingRoom));
                Assert.That(state.SubNeedsRelease, Is.True);
                Assert.That(state.SubConsumedPress, Is.EqualTo(1));
                Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, state, true, 1.1), Is.True);
                Assert.That(actor.holdInput, Is.False);
                Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, state, true, 1.2), Is.False,
                    "A host decision to release cannot substitute for a received real owner release.");
                input.SubHeld = false; input.SubReleaseSequence = 1;
                SubWeaponSimulation.Step(ref state, input, config, 1f / 60, 1.3, true, SubWeaponFailure.None, out _);
                actor.authorityInputHeld = false; actor.authorityInputRelease = 1;
                Assert.That(state.SubNeedsRelease, Is.False);
                Assert.That(state.Ink, Is.EqualTo(100)); Assert.That(state.SubAction, Is.Zero);
                Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, state, true, 1.4), Is.True);
                Assert.That(actor.holdInput, Is.True); Assert.That(actor.holdAttempts, Is.EqualTo(2));
                input.SubHeld = true; input.SubPressSequence = 2;
                SubWeaponSimulation.Step(ref state, input, config, 1f / 60, 1.5, true, SubWeaponFailure.None, out _);
                actor.authorityInputHeld = true; actor.authorityInputPress = 2;
                Assert.That(state.SubPhase, Is.EqualTo(SubWeaponPhase.Holding));
                Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, state, true, 3), Is.False);
                Assert.That(actor.holdInput, Is.True, "A ready owner must remain holding while other owners prepare.");
                input.SubHeld = false; input.SubReleaseSequence = 2;
                SubWeaponSimulation.Step(ref state, input, config, 1f / 60, 3.1, true, SubWeaponFailure.None, out _);
                Assert.That(state.SubPhase, Is.EqualTo(SubWeaponPhase.Starting));
                Assert.That(state.Ink, Is.EqualTo(100));
                Assert.That(SubWeaponSimulation.Step(ref state, input, config, 1f / 60, 3.1 + config.Common.startup + .001,
                    true, SubWeaponFailure.None, out _), Is.True);
                Assert.That(state.SubAction, Is.EqualTo(1));
                Assert.That(state.Ink, Is.EqualTo(100 - config.Common.inkCost));
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test]
        public void ReleasedOwnerCannotRepressWhileStandingSpaceIsBlocked()
        {
            var actor = new BlockDynamicAcceptanceProbe.BlastPressureActor { holdRearming = true,
                authorityInputSeen = true, authorityInputPhase = "hold", authorityInputHeld = false };
            var state = new PlayerSnapshot { SubPhase = SubWeaponPhase.Idle };
            Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, state, false, 3), Is.False);
            Assert.That(actor.holdInput, Is.False);
            state.SubNeedsRelease = true;
            Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, state, true, 4), Is.False);
            Assert.That(actor.holdInput, Is.False);
        }
    }

    public sealed class BlockBlastRecoveryInputTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        HeroBodyShape _shape;
        readonly Collider[] _standing = new Collider[32];
        bool[,] _ignored;
        static readonly Vector3 Feet = new(0, .03f, 0);

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _ignored = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) _ignored[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            _root = new GameObject("Blast recovery planning contracts");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null); _terrain.EnableSurfaceMode();
            _terrain.Dynamic.SetAuthority(true);
            MapBox(new Vector3(0, -.1f, 0), new Vector3(8, .2f, 8));
            var player = new GameObject("Real capsule dimensions") { layer = 8 };
            player.transform.SetParent(_root.transform); player.transform.position = Feet;
            var controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f; controller.center = Vector3.up * .9f;
            controller.radius = .35f; controller.skinWidth = .03f; controller.stepOffset = .3f;
            _shape = new HeroBodyShape(controller);
            Physics.SyncTransforms();
        }

        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) Physics.IgnoreLayerCollision(a, b, _ignored[a, b]);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        BoxCollider MapBox(Vector3 center, Vector3 size)
        {
            var go = new GameObject("Original map geometry"); go.transform.SetParent(_root.transform);
            go.transform.position = center; var box = go.AddComponent<BoxCollider>(); box.size = size;
            ((HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders", Private).GetValue(_terrain)).Add(box);
            Physics.SyncTransforms(); return box;
        }
        static BlockDynamicAcceptanceProbe.BlastPressureActor Actor() => new()
        { lane = Vector3.zero, seed = Vector3.left * .75f };

        [Test]
        public void FreeCubeAtWaypointAllowsRealMoveWhileProductionStandingStillRejectsIt()
        {
            Vector3 spawn = new(.6f, BlockWorld.CellSize * .5f + .002f, 0);
            Assert.That(_terrain.Dynamic.TrySpawn(1, spawn, Quaternion.identity, out var body), Is.True);
            Physics.SyncTransforms();
            var actor = Actor();
            Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastRecoveryMove(_terrain, actor, _shape, Feet, false, 1, _standing), Is.True);
            Vector3 waypoint = actor.recoveryWaypoint;
            Assert.That(waypoint, Is.EqualTo(Feet + Vector3.right * .6f));
            Assert.That(actor.recoveryMotion, Is.EqualTo("outbound"));
            Assert.That(actor.recoveryPlan.Count, Is.EqualTo(18), "Only the latest complete candidate plan is retained.");
            Assert.That(actor.recoveryPlan[0].selected, Is.True);
            Assert.That(actor.recoveryPlan[0].freeEndpointBlocked, Is.True);
            Assert.That(_shape.Fits(waypoint, _standing, PlayerMotorSimulation.WorldMask), Is.False,
                "The production standing capsule still collides with the real free cube.");
            Vector2 move = BlockDynamicAcceptanceProbe.BlastRecoveryMoveInput(actor, Feet, 0);
            Assert.That(move.x, Is.EqualTo(.55f).Within(.00001f)); Assert.That(move.y, Is.Zero.Within(.00001f));
            Assert.That(body.Body.position, Is.EqualTo(spawn), "Planning and input selection cannot move a Rigidbody.");
            Assert.That(body.Body.isKinematic, Is.False);
            actor.holdRearming = true; actor.authorityInputSeen = true; actor.authorityInputPhase = "hold";
            Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastHoldInput(actor, new PlayerSnapshot { SubPhase = SubWeaponPhase.Idle },
                _shape.Fits(waypoint, _standing, PlayerMotorSimulation.WorldMask), 3), Is.False);
            Assert.That(actor.holdInput, Is.False, "Clearance planning cannot waive the final production standing gate.");
        }

        [Test]
        public void OriginalMapAtWaypointRemainsRejectedAndLatestPlanReplacesOldCandidates()
        {
            MapBox(new Vector3(.6f, .15f, 0), Vector3.one * .3f);
            var actor = Actor();
            Assert.That(BlockDynamicAcceptanceProbe.TryBlastRecoveryWaypoint(_terrain, actor, _shape, Feet, _standing, out var waypoint), Is.True);
            Assert.That(actor.recoveryPlan[0].selected, Is.False);
            Assert.That(actor.recoveryPlan[0].result, Is.EqualTo("floor height differs from current feet"));
            Assert.That(waypoint, Is.Not.EqualTo(Feet + Vector3.right * .6f));
            Assert.That(BlockDynamicAcceptanceProbe.TryBlastRecoveryWaypoint(_terrain, actor, _shape, Feet, _standing, out _), Is.True);
            Assert.That(actor.recoveryPlans, Is.EqualTo(2)); Assert.That(actor.recoveryPlan.Count, Is.EqualTo(18));
        }

        [Test]
        public void UnfinishedReturnUsesAnotherRealOutboundLegAfterItsFiniteBudget()
        {
            var actor = Actor(); actor.recoveryMotion = "return"; actor.recoveryWaypoint = actor.lane;
            actor.recoveryAttempts = 1; actor.recoveryMoveAt = 1;
            Vector3 feet = Feet + Vector3.right * .6f;
            Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastRecoveryMove(_terrain, actor, _shape, feet, true, 3, _standing), Is.False);
            Assert.That(actor.recoveryMotion, Is.EqualTo("return"));
            Assert.That(BlockDynamicAcceptanceProbe.UpdateBlastRecoveryMove(_terrain, actor, _shape, feet, true, 3.51, _standing), Is.True);
            Assert.That(actor.recoveryReturnReplans, Is.EqualTo(1)); Assert.That(actor.recoveryAttempts, Is.EqualTo(2));
            Assert.That(actor.recoveryMotion, Is.EqualTo("outbound"));
            Assert.That((actor.recoveryWaypoint - feet).magnitude, Is.EqualTo(.6f).Within(.00001f));
            Assert.That(BlockDynamicAcceptanceProbe.BlastRecoveryMoveInput(actor, feet, 0).magnitude, Is.GreaterThan(0));
        }
    }
}
#endif
