#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Real CharacterController/map queries and production consumers, without a socket or Player build.
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridConsumerTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        Collider _floor;
        cfg.Tables _previousTables;
        readonly TpsAimSolver _aim = new();

        [OneTimeSetUp] public void LoadConfiguration()
        {
            _previousTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }
        [OneTimeTearDown] public void RestoreConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _previousTables);

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("Fixed-grid production consumers");
            var arena = _root.AddComponent<PrototypeArena>();
            var floor = new GameObject("Original map floor"); floor.transform.SetParent(_root.transform, false);
            floor.AddComponent<PaintSurface>().SurfaceId = 73;
            var box = floor.AddComponent<BoxCollider>(); box.center = new Vector3(0, -.15f, 0); box.size = new Vector3(6, .3f, 6); _floor = box;
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableFixedGridMode();
            Physics.SyncTransforms();
        }

        [TearDown] public void Cleanup()
        {
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                Object.DestroyImmediate(_root); Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        BlockFixedGridState Add(Vector3Int cell, byte owner = 1)
        {
            Vector3 point = BlockWorld.Center(cell); point.y = 0;
            Assert.That(_terrain.TryGetSurfacePaint(_floor, point, Vector3.up, out var paint), Is.True);
            if (paint.Owner != owner) Assert.That(_terrain.PaintSurfaceCell(paint.Key, owner), Is.True);
            Assert.That(_terrain.FixedBlocks.Build(new[] { cell }, owner, 1).Added, Is.EqualTo(1), _terrain.FixedBlocks.LastFailure);
            Assert.That(_terrain.FixedBlocks.TryGet(cell, out var state), Is.True); return state;
        }

        TpsCollision Hit(float y = .15f)
        {
            Assert.That(_aim.ClosestCast(new Vector3(.15f, y, -1), Vector3.forward, 2, 0, 0, out var hit, false), Is.True);
            return hit;
        }
        static PlayerSnapshot Standing(Vector3 feet) => new()
        { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1, Health = 100, Ink = 50,
            LifeState = PlayerLifeState.Alive, Position = feet, Grounded = true, Movement = MovementMode.Human };
        PlayerMotorSimulation Motor(Vector3 feet, out CharacterController controller)
        {
            var go = new GameObject("Fixed-grid capsule") { layer = 8 }; go.transform.SetParent(_root.transform, false);
            controller = go.AddComponent<CharacterController>(); HeroBodyShape.For(1).Apply(controller, false);
            var motor = new PlayerMotorSimulation(controller); motor.Restore(Standing(feet), false); return motor;
        }

        static void CompleteTraversalContract(PlayerMotorSimulation motor, ref PlayerSnapshot state, double now)
        {
            // Stable existing boundary: real map/data queries, binding and pose finalization.
            // This deliberately does not call the new FromSupport helper or inject contacts.
            var complete = typeof(PlayerMotorSimulation).GetMethod("CompleteTraversalFrame", PrivateInstance);
            Assert.That(complete, Is.Not.Null);
            object[] arguments = { state, state, 1f / 60, now };
            complete.Invoke(motor, arguments); state = (PlayerSnapshot)arguments[0];
        }

        [Test] public void AimAndOverlapSeeFixedIdentityBeforeAnyMeshOrColliderPublication()
        {
            int before = _root.GetComponentsInChildren<Collider>(true).Length;
            var state = Add(Vector3Int.zero); var hit = Hit();
            Assert.That(hit.HasContact, Is.True); Assert.That(hit.Collider, Is.Null); Assert.That(hit.BlockQuery.IsKnown, Is.True);
            Assert.That(hit.BlockQuery.TryResolveCurrent(out var contact), Is.True);
            Assert.That(contact.FixedHandle, Is.EqualTo(state.Handle)); Assert.That(contact.Cell, Is.EqualTo(state.Cell));
            Assert.That(_aim.Overlap(new Vector3(.15f, .15f, -.01f), .02f, 0, Vector3.back, out hit, false), Is.True);
            Assert.That(hit.Collider, Is.Null); Assert.That(hit.BlockQuery.Contact.FixedHandle, Is.EqualTo(state.Handle));
            Assert.That(_root.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(before));
            Assert.That(_root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
        }

        [Test] public void AimChoosesNearestMapOrDataHitWithoutUsingNullAsIdentity()
        {
            Add(Vector3Int.zero); var data = Hit();
            var go = new GameObject("Near map occluder"); go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(.15f, .15f, -.5f); var wall = go.AddComponent<BoxCollider>(); wall.size = Vector3.one * .2f;
            Physics.SyncTransforms(); var map = Hit();
            Assert.That(map.Collider, Is.SameAs(wall)); Assert.That(map.Distance, Is.LessThan(data.Distance));
            Object.DestroyImmediate(go); Physics.SyncTransforms();
            Assert.That(Hit().BlockQuery.Contact.FixedHandle, Is.EqualTo(data.BlockQuery.Contact.FixedHandle));
        }

        [Test] public void DifferentFixedTargetsBlockGuideEvenThoughBothCollidersAreNull()
        {
            Add(Vector3Int.zero); Add(Vector3Int.up); var low = Hit(); var high = Hit(.45f);
            Assert.That(TpsAimSolver.SameTarget(low, low), Is.True);
            Assert.That(TpsAimSolver.SameTarget(low, high), Is.False);
            var guide = new WeaponImpactForecast { Kind = PredictedImpactKind.World, Contact = low };
            Assert.That(WeaponImpactPrediction.GuideObstructed(new TpsAimSolution { AimHit = high }, guide), Is.True);
            Assert.That(WeaponImpactPrediction.GuideObstructed(new TpsAimSolution { AimHit = low }, guide), Is.False);
        }

        [Test] public void DeferredHitRejectsRelocatedAndRebuiltTargets()
        {
            var bottom = Add(Vector3Int.zero); var upper = Add(Vector3Int.up); var old = Hit(.45f);
            _terrain.FixedBlocks.Destroy(new[] { bottom.Handle }, 2, 1, 0);
            Assert.That(old.BlockQuery.TryResolveCurrent(out _), Is.False, "Same ID at a different cell/revision is an old hit.");
            Assert.That(_terrain.FixedBlocks.TryGet(upper.Handle, out var moved), Is.True);
            Assert.That(Hit().BlockQuery.Contact.FixedHandle, Is.EqualTo(upper.Handle));
            var beforeDelete = Hit(); _terrain.FixedBlocks.Destroy(new[] { moved.Handle }, 2, 1, 0);
            var replacement = Add(Vector3Int.zero);
            Assert.That(replacement.Handle, Is.Not.EqualTo(moved.Handle));
            Assert.That(beforeDelete.BlockQuery.TryResolveCurrent(out _), Is.False);
        }

        [Test] public void CapsuleSweepStopsAtFixedBlockAndNeverPushesItOrChangesMapStepSetting()
        {
            var block = Add(Vector3Int.zero); var motor = Motor(new Vector3(.15f, .035f, -.8f), out var controller);
            float step = controller.stepOffset;
            var move = typeof(PlayerMotorSimulation).GetMethod("MoveWithFreeBlocks", PrivateInstance);
            for (int i = 0; i < 30; i++) move.Invoke(motor, new object[] { Vector3.forward * .04f, 1f / 60 });
            Assert.That(controller.transform.position.z, Is.LessThan(-.05f));
            Assert.That(controller.stepOffset, Is.EqualTo(step));
            Assert.That(_terrain.FixedBlocks.TryGet(block.Handle, out var after), Is.True);
            Assert.That(after.Cell, Is.EqualTo(block.Cell)); Assert.That(after.Revision, Is.EqualTo(block.Revision));
        }

        [Test] public void FallingCapsuleLandsOnDataTopAndFriendlyContactProvidesSurfaceAbilities()
        {
            Add(Vector3Int.zero); var motor = Motor(new Vector3(.15f, .7f, .15f), out var controller);
            var move = typeof(PlayerMotorSimulation).GetMethod("MoveWithFreeBlocks", PrivateInstance);
            var flags = (CollisionFlags)move.Invoke(motor, new object[] { Vector3.down * .8f, 1f / 60 });
            Assert.That((flags & CollisionFlags.Below) != 0, Is.True);
            Assert.That(controller.transform.position.y, Is.EqualTo(.3f).Within(.004f));
            Assert.That(PlayerMotorSimulation.TryGetGround(controller.transform.position, out var contact), Is.True);
            Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.FixedGrid)); Assert.That(contact.Owner, Is.EqualTo(1));
            var canSwim = typeof(PlayerMotorSimulation).GetMethod("CanSwimOnGround", PrivateStatic);
            Assert.That((bool)canSwim.Invoke(null, new object[] { contact, (byte)1 }), Is.True);
            Assert.That((bool)canSwim.Invoke(null, new object[] { contact, (byte)2 }), Is.False);
            var state = Standing(controller.transform.position); state.Swimming = true; state.Movement = MovementMode.GroundInk;
            var friendly = (bool)typeof(PlayerMotorSimulation).GetMethod("HasFriendlyInkContact", PrivateInstance).Invoke(motor, new object[] { state });
            Assert.That(friendly, Is.True);
        }

        [Test] public void FixedWallIsClimbableForOwnerAndBlocksStandingVolume()
        {
            for (int y = 0; y < 4; y++) Add(new Vector3Int(0, y, 0));
            Assert.That(PlayerMotorSimulation.Query(new Vector3(.15f, .7f, -.2f), Vector3.forward, .5f, out var wall), Is.True);
            Assert.That(wall.Kind, Is.EqualTo(BlockContactKind.FixedGrid)); Assert.That(wall.Climbable, Is.True);
            Assert.That(PlayerMotorSimulation.CanClimb(wall, 1), Is.True); Assert.That(PlayerMotorSimulation.CanClimb(wall, 2), Is.False);
            var motor = Motor(new Vector3(.15f, 0, -1), out _);
            Assert.That(motor.CanStand(new Vector3(.15f, 0, .15f)), Is.False);
        }

        [Test] public void FullMotorCanLeaveSwimmingAndFireWhileStandingOnFourLayerFixedFloor()
        {
            for (int z = -3; z <= 3; z++) for (int x = -3; x <= 3; x++)
                for (int y = 0; y < 4; y++) Add(new Vector3Int(x, y, z));
            var motor = Motor(new Vector3(.15f, 1.203f, .15f), out var controller);
            var state = Standing(controller.transform.position);
            var input = new PlayerInputFrame { Revision = state.Revision, HeroRevision = state.HeroRevision, Look = Vector2.zero };
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            const float dt = 1f / 60;
            double now = 10;
            for (int i = 0; i < 30; i++, now += dt) motor.Step(ref state, input, dt, now, false);
            Assert.That(state.Grounded, Is.True);
            Assert.That(motor.CanStand(PlayerMotorSimulation.HumanPosition(state)), Is.True);
            input.Swim = true;
            for (int i = 0; i < 60; i++, now += dt) motor.Step(ref state, input, dt, now, false);
            Assert.That(state.Swimming, Is.True);
            Assert.That(state.FriendlyInkContact, Is.True);
            input.Swim = false; input.Fire = true; input.FireSequence = 1;
            bool fired = false;
            for (int i = 0; i < 60; i++, now += dt)
            {
                motor.Step(ref state, input, dt, now, true, weapon.ShootMoveSpeed, true);
                bool canShoot = !state.Swimming && motor.CanStand(state.Position);
                fired |= WeaponSimulation.Step(ref state, input, weapon, now, false, canShoot, out _);
            }
            Assert.That(state.Swimming, Is.False);
            Assert.That(state.CompactBody, Is.False);
            Assert.That(motor.CanStand(state.Position), Is.True);
            Assert.That(fired, Is.True, "The production weapon gate must accept a standing actor after releasing swim.");
            Assert.That(state.Position.y, Is.EqualTo(1.2f).Within(.004f));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(196));
        }

        [TestCase(.29937440156936646f, 1)]
        [TestCase(.29973262548446655f, 1)]
        [TestCase(.29937440156936646f, 2)]
        [TestCase(.29973262548446655f, 2)]
        public void FullMotorLeavesCompactBodyAndMovesOnOneLayerFloorAtObservedContactHeight(float feetY, int team)
        {
            // These are the two exact stuck contact heights from build60. Its
            // 1.2m platform fixture missed native step-down onto the map 0.3m
            // below a single layer, as well as the sub-millimetre stand test.
            // At the authored 3.2350128m/s shooting speed, the requested half
            // input can travel up to 1.6175064m in this second. Include the full
            // 0.35m capsule footprint at the far end of that unobstructed path.
            for (int z = -4; z <= 4; z++) for (int x = -7; x <= 7; x++) Add(new Vector3Int(x, 0, z));
            var motor = Motor(new Vector3(.15f, feetY, .15f), out var controller);
            var state = Standing(controller.transform.position); state.Team = (byte)team; state.CompactBody = true;
            motor.Restore(state, false);
            Assert.That(motor.CanStand(state.Position), Is.True, "A flat floor contact within 1mm is not an overhead obstruction.");
            var input = new PlayerInputFrame { Revision = state.Revision, HeroRevision = state.HeroRevision,
                Look = Vector2.zero, Move = new Vector2(.5f, 0), Fire = true, FireSequence = 1 };
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            const float dt = 1f / 60;
            bool fired = false; float originalStep = controller.stepOffset;
            for (int i = 0; i < 60; i++)
            {
                double now = 10 + i * (double)dt;
                motor.Step(ref state, input, dt, now, true, weapon.ShootMoveSpeed, true);
                bool canShoot = !state.Swimming && motor.CanStand(state.Position);
                fired |= WeaponSimulation.Step(ref state, input, weapon, now, false, canShoot, out _);
                Assert.That(state.CompactBody, Is.False);
                Assert.That(state.Position.y, Is.EqualTo(.3f).Within(.002f));
            }
            Assert.That(state.Position.x, Is.GreaterThan(.65f), "Map step-down must not roll back all horizontal travel over a data floor.");
            Assert.That(state.Grounded, Is.True); Assert.That(fired, Is.True);
            Assert.That(controller.stepOffset, Is.EqualTo(originalStep), "Temporary data movement constraints must restore map stepping.");
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(135));
        }

        [Test] public void FixedFloorCompactExitKeepsClearanceAndFreshRecoveryThroughTheOrdinaryMotor()
        {
            for (int z = -3; z <= 3; z++) for (int x = -3; x <= 3; x++) Add(new Vector3Int(x, 0, z));
            var motor = Motor(new Vector3(.15f, .301f, .15f), out var controller);
            var state = Standing(controller.transform.position);
            var input = new PlayerInputFrame { Revision = state.Revision, HeroRevision = state.HeroRevision };
            const float dt = 1f / 60; double now = 10;
            for (int i = 0; i < 5; i++, now += dt) motor.Step(ref state, input, dt, now, false);
            Assert.That(state.Swimming || state.CompactBody, Is.False);
            input.Swim = true;
            for (int i = 0; i < 5; i++, now += dt) motor.Step(ref state, input, dt, now, false);
            Assert.That(state.Swimming && state.FriendlyInkContact && state.HasInkRecovery, Is.True);
            Assert.That(state.SupportIsFixedGrid, Is.True);

            var shape = HeroBodyShape.For(state.HeroId);
            var ceiling = new GameObject("Compact exit clearance contract"); ceiling.transform.SetParent(_root.transform, false);
            ceiling.transform.position = new Vector3(.15f,
                state.Position.y + (shape.CompactHeight + shape.Height) * .5f, .15f);
            ceiling.AddComponent<BoxCollider>().size = new Vector3(2, .1f, 2);
            Physics.SyncTransforms(); Assert.That(motor.CanStand(state.Position), Is.False);
            input.Swim = false; input.Fire = true; input.FireSequence = 1;
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            for (int i = 0; i < 3; i++, now += dt)
            {
                motor.Step(ref state, input, dt, now, true, weapon.ShootMoveSpeed, true);
                Assert.That(state.Swimming, Is.False); Assert.That(state.CompactBody, Is.True);
                Assert.That(state.FriendlyInkContact || state.HasInkRecovery, Is.False);
                Assert.That(controller.height, Is.EqualTo(shape.CompactHeight));
                bool fired = WeaponSimulation.Step(ref state, input, weapon, now, false,
                    !state.Swimming && motor.CanStand(PlayerMotorSimulation.HumanPosition(state)), out _);
                Assert.That(fired, Is.False, "A blocked standing volume cannot become a weapon-clearance pass.");
            }
            Object.DestroyImmediate(ceiling); Physics.SyncTransforms();
            bool firedAfterClearance = false;
            for (int i = 0; i < 60; i++, now += dt)
            {
                motor.Step(ref state, input, dt, now, true, weapon.ShootMoveSpeed, true);
                firedAfterClearance |= WeaponSimulation.Step(ref state, input, weapon, now, false,
                    !state.Swimming && motor.CanStand(PlayerMotorSimulation.HumanPosition(state)), out _);
                Assert.That(state.CompactBody || state.Swimming, Is.False);
                Assert.That(controller.height, Is.EqualTo(shape.Height));
                Assert.That(state.FriendlyInkContact, Is.False);
            }
            Assert.That(firedAfterClearance, Is.True);
            Assert.That(state.Grounded && state.SupportIsFixedGrid, Is.True);
            Assert.That(state.Position.y, Is.EqualTo(.3f).Within(.002f));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(49));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void CompletedWallFrameKeepsWallRecoverySeparateFromFriendlyGroundFallback(int wallOwner)
        {
            // Bounded defensive finalization state, not a claim that ordinary Step
            // naturally publishes WallInk+Grounded. All contacts and support are real.
            var floor = Add(new Vector3Int(0, 0, -2));
            if (wallOwner != 0) for (int y = 0; y < 4; y++) Add(new Vector3Int(0, y, 0), (byte)wallOwner);
            var motor = Motor(new Vector3(.15f, .301f, -.36f), out var controller);
            var state = Standing(controller.transform.position);
            state.Swimming = true; state.Movement = MovementMode.WallInk; state.SwimSource = SwimSurface.Friendly;
            state.WallNormal = Vector3.back; state.WallPoint = new Vector3(.15f, .65f, 0);
            motor.Restore(state, false);
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out var ground,
                controller.slopeLimit, controller.radius), Is.True);
            Assert.That(ground.FixedHandle, Is.EqualTo(floor.Handle)); Assert.That(ground.Owner, Is.EqualTo(1));
            bool wallFound = PlayerMotorSimulation.Query(state.Position + Vector3.up * (HeroBodyShape.For(1).CompactHeight * .5f),
                -state.WallNormal, controller.radius + .065f, out var wall);
            Assert.That(wallFound, Is.EqualTo(wallOwner != 0));
            if (wallFound)
            {
                Assert.That(wall.Owner, Is.EqualTo(wallOwner));
                Assert.That(PlayerMotorSimulation.CanClimb(wall, 1), Is.EqualTo(wallOwner == 1));
            }
            var before = state;
            CompleteTraversalContract(motor, ref state, 10);
            bool friendlyWall = wallOwner == 1;
            var expectedHandle = friendlyWall ? wall.FixedHandle : floor.Handle;
            Assert.That(state.SupportIsFixedGrid, Is.True);
            Assert.That(state.SupportFixedId, Is.EqualTo(expectedHandle.Id));
            Assert.That(state.SupportFixedGeneration, Is.EqualTo(expectedHandle.Generation));
            Assert.That(state.SupportCell, Is.EqualTo(friendlyWall ? wall.Cell : floor.Cell));
            Assert.That(state.SupportTerrainRevision, Is.EqualTo(friendlyWall ? wall.Revision : floor.Revision));
            Assert.That(state.GroundNormal, Is.EqualTo(friendlyWall ? Vector3.back : Vector3.up));
            Assert.That(state.FriendlyInkContact, Is.EqualTo(friendlyWall),
                "A friendly ground fallback is support, not proof of fresh friendly wall contact.");
            Assert.That(state.HasInkRecovery, Is.EqualTo(friendlyWall));
            Assert.That(state.Position, Is.EqualTo(before.Position)); Assert.That(state.WallNormal, Is.EqualTo(before.WallNormal));
            Assert.That(state.FixedBlockEpoch, Is.EqualTo(_terrain.FixedBlocks.Epoch));
            Assert.That(state.FixedBlockRound, Is.EqualTo(_terrain.FixedBlocks.Round));
            Assert.That(state.FixedBlockRevision, Is.EqualTo(_terrain.FixedBlocks.Revision));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(wallOwner == 0 ? 1 : 5));
        }

        [Test] public void OrdinaryMotorRefreshesSurfaceSupportAndRecoveryAfterOwnershipCommits()
        {
            var point = new Vector3(.15f, 0, .15f);
            Assert.That(_terrain.TryGetSurfacePaint(_floor, point, Vector3.up, out var original), Is.True);
            Assert.That(_terrain.PaintSurfaceCell(original.Key, 1), Is.True);
            var motor = Motor(point + Vector3.up * .035f, out var controller);
            var state = Standing(controller.transform.position);
            var input = new PlayerInputFrame { Revision = state.Revision, HeroRevision = state.HeroRevision, Swim = true };
            const float dt = 1f / 60;
            for (int phase = 0; phase < 3; phase++)
            {
                byte owner = phase == 1 ? (byte)2 : (byte)1;
                if (phase != 0) Assert.That(_terrain.PaintSurfaceCell(original.Key, owner), Is.True);
                motor.Step(ref state, input, dt, 10 + phase * (double)dt, false);
                Assert.That(_terrain.SurfacePaints.TryGet(original.Key, out var current), Is.True);
                Assert.That(state.SupportIsSurfacePaint, Is.True); Assert.That(state.SupportIsFixedGrid, Is.False);
                Assert.That(state.SupportSurfaceFace, Is.EqualTo(original.Key.FaceId));
                Assert.That(state.SupportSurfaceU, Is.EqualTo(original.Key.U)); Assert.That(state.SupportSurfaceV, Is.EqualTo(original.Key.V));
                Assert.That(state.SupportTerrainRevision, Is.EqualTo(current.Revision));
                Assert.That(state.FixedBlockRevision, Is.EqualTo(_terrain.FixedBlocks.Revision));
                Assert.That(state.Swimming, Is.EqualTo(owner == 1));
                Assert.That(state.FriendlyInkContact, Is.EqualTo(owner == 1)); Assert.That(state.HasInkRecovery, Is.EqualTo(owner == 1));
                state.Ink = 20; state.InkRecoverAt = 0; state.WeaponPhase = WeaponPhase.Idle;
                var hero = GameplayConfig.GetHero(state.HeroId);
                ResourceSimulation.Step(ref state, hero, false, false, dt, 10 + phase * (double)dt);
                Assert.That(state.Ink, Is.EqualTo(20 + (owner == 1 ? hero.SwimRecoverInk : hero.RecoverInk) * dt).Within(.0001f));
            }
            Assert.That(_terrain.FixedBlocks.Count, Is.Zero);
        }

        [Test] public void CompletedLegacyGroundFrameStillQueriesFriendlyNonBlockPaintWithoutBindingBlockSupport()
        {
            // Dispose the fixture's owned fixed storage before destroying its component.
            BlockFixedGridEditFixtureCleanup.Release(_terrain); Object.DestroyImmediate(_terrain); _terrain = null;
            var surface = _floor.GetComponent<PaintSurface>(); surface.Scores = true; surface.WalkableSize = new Vector2(6, 6);
            surface.InitializeOwnership(.3f);
            for (int i = 0; i < surface.Ownership.Cells.Length; i++) surface.Ownership.Set(i, 1);
            Assert.That(PlayerMotorSimulation.BlockTraversalEnabled, Is.False);
            var motor = Motor(new Vector3(.15f, .035f, .15f), out var controller);
            var state = Standing(controller.transform.position); state.Swimming = true;
            state.Movement = MovementMode.GroundInk; state.SwimSource = SwimSurface.Friendly;
            motor.Restore(state, false);
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out var contact,
                controller.slopeLimit, controller.radius), Is.True);
            Assert.That(contact.IsBlock, Is.False); Assert.That(contact.Owner, Is.EqualTo(1)); Assert.That(contact.Stable, Is.True);
            CompleteTraversalContract(motor, ref state, 10);
            Assert.That(state.FriendlyInkContact && state.HasInkRecovery, Is.True);
            Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.SupportIsFixedGrid || state.SupportIsSurfacePaint, Is.False);
            Assert.That(state.FixedBlockEpoch, Is.Zero); Assert.That(state.FixedBlockRevision, Is.Zero);
        }

        [Test] public void FloorContactToleranceDoesNotPermitDeepFloorSideWallOrCeilingPenetration()
        {
            for (int z = -2; z <= 2; z++) for (int x = -2; x <= 2; x++) Add(new Vector3Int(x, 0, z));
            var motor = Motor(new Vector3(.15f, .2993744f, .15f), out _);
            Assert.That(motor.CanStand(new Vector3(.15f, .298f, .15f)), Is.False, "A 2mm floor penetration is not mere touching.");
            for (int y = 1; y < 4; y++) Add(new Vector3Int(2, y, 0));
            float wallContactX = .6f - (HeroBodyShape.For(1).Radius - .025f);
            Assert.That(motor.CanStand(new Vector3(wallContactX + .0002f, .2993744f, .15f)), Is.False,
                "Floor contact tolerance must not hide a simultaneous 0.2mm side obstruction.");
            var ceiling = new GameObject("Native overhead obstruction"); ceiling.transform.SetParent(_root.transform, false);
            ceiling.transform.position = new Vector3(0, 1.5f, 0); ceiling.AddComponent<BoxCollider>().size = new Vector3(2, .1f, 2);
            Physics.SyncTransforms();
            Assert.That(motor.CanStand(new Vector3(-.5f, .2993744f, .15f)), Is.False, "A real overhead map obstruction still prevents standing.");
        }

        [Test] public void HorizontalMoveOnOneLayerFloorRetriesNativeStepDownWithoutDiscardingTravel()
        {
            for (int z = -3; z <= 3; z++) for (int x = -3; x <= 3; x++) Add(new Vector3Int(x, 0, z));
            var motor = Motor(new Vector3(.15f, .2993744f, .15f), out var controller);
            var move = typeof(PlayerMotorSimulation).GetMethod("MoveWithFreeBlocks", PrivateInstance);
            float originalStep = controller.stepOffset;
            for (int i = 0; i < 30; i++) move.Invoke(motor, new object[] { Vector3.right * .02f, 1f / 60 });
            Assert.That(controller.transform.position.x, Is.GreaterThan(.7f));
            Assert.That(controller.transform.position.y, Is.EqualTo(.3f).Within(.002f));
            Assert.That(controller.stepOffset, Is.EqualTo(originalStep));
        }

        [Test] public void MineGroundUsesDataTopAndRequiresFriendlyOwner()
        {
            Add(Vector3Int.zero); var state = Standing(new Vector3(.15f, .301f, .15f));
            Assert.That(SubWeaponService.TryMineGroundContact(state, out var hit), Is.True);
            Assert.That(hit.Collider, Is.Null); Assert.That(hit.BlockQuery.Contact.Kind, Is.EqualTo(BlockContactKind.FixedGrid));
            state.Team = 2; Assert.That(SubWeaponService.TryMineGroundContact(state, out _), Is.False);
        }

        [Test] public void FixedSupportIdentitySurvivesCanonicalAndDeltaSerialization()
        {
            var block = Add(Vector3Int.zero); var source = Standing(new Vector3(.15f, .301f, .15f));
            source.FixedBlockEpoch = block.Handle.Epoch; source.FixedBlockRound = block.Handle.Round; source.FixedBlockRevision = _terrain.FixedBlocks.Revision;
            source.SupportIsFixedGrid = true; source.SupportFixedId = block.Handle.Id; source.SupportFixedGeneration = block.Handle.Generation;
            source.SupportCell = block.Cell; source.SupportTerrainRevision = block.Revision;
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(source);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot restored);
            AssertSupport(source, restored);
            var baseline = Standing(Vector3.one);
            using var delta = new FastBufferWriter(4096, Allocator.Temp); PlayerSnapshotDelta.WriteDelta(delta, source, baseline);
            using var deltaReader = new FastBufferReader(delta, Allocator.Temp); PlayerSnapshotDelta.ReadDelta(deltaReader, ref baseline);
            AssertSupport(source, baseline);
        }

        static void AssertSupport(PlayerSnapshot expected, PlayerSnapshot actual)
        {
            Assert.That(actual.FixedBlockEpoch, Is.EqualTo(expected.FixedBlockEpoch)); Assert.That(actual.FixedBlockRound, Is.EqualTo(expected.FixedBlockRound));
            Assert.That(actual.FixedBlockRevision, Is.EqualTo(expected.FixedBlockRevision)); Assert.That(actual.SupportIsFixedGrid, Is.True);
            Assert.That(actual.SupportFixedId, Is.EqualTo(expected.SupportFixedId)); Assert.That(actual.SupportFixedGeneration, Is.EqualTo(expected.SupportFixedGeneration));
            Assert.That(actual.SupportCell, Is.EqualTo(expected.SupportCell)); Assert.That(actual.SupportTerrainRevision, Is.EqualTo(expected.SupportTerrainRevision));
        }

        [Test] public void ReplayWaitsForCompleteFixedRevisionButAcceptsNewerDeletion()
        {
            var block = Add(Vector3Int.zero); var state = Standing(new Vector3(.15f, .3f, .15f));
            var go = new GameObject("Fixed replay owner"); go.transform.SetParent(_root.transform, false); var player = go.AddComponent<PrototypePlayer>();
            state.FixedBlockEpoch = block.Handle.Epoch; state.FixedBlockRound = block.Handle.Round;
            state.FixedBlockRevision = _terrain.FixedBlocks.Revision + 1;
            state.SupportIsFixedGrid = true; state.SupportFixedId = block.Handle.Id; state.SupportFixedGeneration = block.Handle.Generation;
            state.SupportCell = block.Cell; state.SupportTerrainRevision = block.Revision;
            var ready = typeof(PrototypePlayer).GetMethod("TerrainReadyForReplay", PrivateInstance);
            Assert.That((bool)ready.Invoke(player, new object[] { state, 0 }), Is.False);
            _terrain.FixedBlocks.Destroy(new[] { block.Handle }, 2, 1, 0);
            state.FixedBlockRevision--;
            Assert.That((bool)ready.Invoke(player, new object[] { state, 0 }), Is.True, "An applied newer commit can legitimately delete old support.");
            var observation = new PrototypePlayer.ReconcileTelemetryState(state, _terrain);
            Assert.That(observation.SupportIsFixedGrid, Is.True); Assert.That(observation.SupportPresent, Is.False);
        }

        [TestCase("dead", "revision")] [TestCase("dead", "epoch")] [TestCase("dead", "round")]
        [TestCase("unmovable", "revision")] [TestCase("unmovable", "epoch")] [TestCase("unmovable", "round")]
        [TestCase("timeout", "revision")] [TestCase("timeout", "epoch")] [TestCase("timeout", "round")]
        public void InactiveReplayStillRequiresTheExplicitFixedWorld(string inactive, string missing)
        {
            var go = new GameObject("Inactive fixed replay owner"); go.transform.SetParent(_root.transform, false);
            var player = go.AddComponent<PrototypePlayer>();
            var state = Standing(new Vector3(-2, .04f, -2));
            state.FixedBlockEpoch = _terrain.FixedBlocks.Epoch;
            state.FixedBlockRound = _terrain.FixedBlocks.Round;
            state.FixedBlockRevision = _terrain.FixedBlocks.Revision;
            if (inactive == "dead") { state.Health = 0; state.LifeState = PlayerLifeState.Dead; state.Movement = MovementMode.Dead; }
            if (inactive == "unmovable") state.LifeState = PlayerLifeState.Dead;
            if (inactive == "timeout") state.InputTimedOut = true;
            if (missing == "revision") state.FixedBlockRevision++;
            if (missing == "epoch") state.FixedBlockEpoch++;
            if (missing == "round") state.FixedBlockRound++;

            var ready = typeof(PrototypePlayer).GetMethod("TerrainReadyForReplay", PrivateInstance);
            Assert.That((bool)ready.Invoke(player, new object[] { state, 0 }), Is.False,
                "Life/movement flags cannot make a missing explicit collision dependency safe to query.");
            Assert.That(state.FixedBlockRevision, Is.EqualTo(missing == "revision" ? _terrain.FixedBlocks.Revision + 1 : _terrain.FixedBlocks.Revision));
        }

        [TestCase("dead")] [TestCase("unmovable")] [TestCase("timeout")]
        public void InactiveReplayDoesNotForgetAHigherRequiredFixedCommit(string inactive)
        {
            var block = Add(Vector3Int.zero);
            var go = new GameObject("Retained fixed replay owner"); go.transform.SetParent(_root.transform, false);
            var player = go.AddComponent<PrototypePlayer>();
            var state = Standing(new Vector3(-2, .04f, -2));
            state.FixedBlockEpoch = _terrain.FixedBlocks.Epoch; state.FixedBlockRound = _terrain.FixedBlocks.Round;
            state.FixedBlockRevision = _terrain.FixedBlocks.Revision + 1;
            var ready = typeof(PrototypePlayer).GetMethod("TerrainReadyForReplay", PrivateInstance);
            Assert.That((bool)ready.Invoke(player, new object[] { state, 0 }), Is.False);
            uint required = state.FixedBlockRevision;
            state.FixedBlockRevision--;
            if (inactive == "dead") { state.Health = 0; state.LifeState = PlayerLifeState.Dead; state.Movement = MovementMode.Dead; }
            if (inactive == "unmovable") state.LifeState = PlayerLifeState.Dead;
            if (inactive == "timeout") state.InputTimedOut = true;
            Assert.That((bool)ready.Invoke(player, new object[] { state, 0 }), Is.False,
                "An inactive snapshot is not evidence that an earlier required completed commit arrived.");
            Assert.That(typeof(PrototypePlayer).GetField("_fixedReplayRevision", PrivateInstance).GetValue(player), Is.EqualTo(required));
            Assert.That(typeof(PrototypePlayer).GetField("_fixedReplayEpoch", PrivateInstance).GetValue(player), Is.EqualTo(state.FixedBlockEpoch));

            Assert.That(_terrain.FixedBlocks.Destroy(new[] { block.Handle }, 2, 1, 0).RemovedEnemy, Is.EqualTo(1));
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(required));
            Assert.That((bool)ready.Invoke(player, new object[] { state, 0 }), Is.True);
        }

        [Test] public void AttachmentHandleFollowsRelocationAndCannotBindReplacementAtSameCell()
        {
            var bottom = Add(Vector3Int.zero); var upper = Add(Vector3Int.up); var hit = Hit(.45f);
            var asset = ScriptableObject.CreateInstance<SubWeaponConfigAsset>(); SubWeaponDefaults.Apply(asset, SubWeaponType.SuctionBomb);
            try
            {
                var entity = new SubWeaponService.Entity(new SubEntityState { Type = SubWeaponType.SuctionBomb,
                    Position = hit.Point + hit.Normal * .04f, Normal = hit.Normal, Phase = SubEntityPhase.Active }, asset.Snapshot());
                typeof(SubWeaponService).GetMethod("RememberBlockAttachment", PrivateStatic).Invoke(null,
                    new object[] { entity, hit.BlockQuery.Contact });
                var exists = typeof(SubWeaponService).GetMethod("BlockAttachmentExists", PrivateStatic);
                Assert.That(entity.Attached, Is.True); Assert.That(entity.Attachment, Is.Null);
                Assert.That((bool)exists.Invoke(null, new object[] { entity }), Is.True);
                _terrain.FixedBlocks.Destroy(new[] { bottom.Handle }, 2, 1, 0);
                Assert.That((bool)exists.Invoke(null, new object[] { entity }), Is.True);
                Assert.That(_terrain.FixedBlocks.TryGet(entity.FixedAttachmentHandle, out var moved), Is.True);
                Assert.That(moved.Handle, Is.EqualTo(upper.Handle)); Assert.That(moved.Cell, Is.EqualTo(Vector3Int.zero));
                Assert.That(Vector3.Distance(BlockWorld.Center(moved.Cell) + entity.FixedAttachmentOffset,
                    entity.State.Position - Vector3.up * .3f), Is.LessThan(.000001f));
                _terrain.FixedBlocks.Destroy(new[] { moved.Handle }, 2, 1, 0); Add(Vector3Int.zero);
                Assert.That((bool)exists.Invoke(null, new object[] { entity }), Is.False);
                _terrain.Clear(_terrain.Round + 1);
                Assert.That((bool)exists.Invoke(null, new object[] { entity }), Is.False);
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test] public void AnimatedPaperOutsideAuthoredRectangleStillExcludesConstruction()
        {
            var go = new GameObject("Animated contour owner"); go.transform.SetParent(_root.transform, false); var player = go.AddComponent<PrototypePlayer>();
            var state = Standing(new Vector3(-4, 2, -4)); state.Swimming = true; state.PaperPose = PaperPose.Wall;
            state.PaperCenter = new Vector3(0, 2.55f, .15f); state.PaperRotation = Quaternion.identity;
            var rects = new[] { new Rect(2.55f, -.1f, .3f, .2f) };
            var actorType = typeof(BlockTerrain).GetNestedType("SafetyActor", BindingFlags.NonPublic);
            var actor = Activator.CreateInstance(actorType, new object[] { player, state, rects });
            bool occupied = (bool)typeof(BlockTerrain).GetMethod("CellOccupiesActor", PrivateStatic).Invoke(null,
                new[] { (object)new Vector3Int(8, 8, 0), actor });
            Assert.That(occupied, Is.True, "Animated silhouette rectangles are not clipped by authored PaperSize.");
        }
    }
}
#endif
