#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BlockTraversalTests
    {
        const float Dt = 1f / 60;
        BlockTerrain terrain;
        CharacterController controller;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        double now;

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(); // Publication budgets require real advancing Unity frames.
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            HeroMigrationTests.Load();
            SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear();
            foreach (var row in LubanConfigService.Current.Tables.TbSubWeapon.DataList)
                SubWeaponConfigService.Current.SetById(row.Id, AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row.ConfigPath));
            foreach (var row in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)
                SpecialWeaponConfigService.Current.Set(row.Id, AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(row.ConfigPath));
            terrain = new GameObject("Block traversal world").AddComponent<BlockTerrain>();
            terrain.Initialize(null);
            var root = new GameObject("Block traversal motor") { layer = 8 };
            controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f; controller.center = Vector3.up * .9f;
            controller.radius = .35f; controller.skinWidth = .03f; controller.stepOffset = .3f;
            motor = new PlayerMotorSimulation(controller);
            state = new PlayerSnapshot { HeroId = 1, Team = 1, Health = 100, Ink = 20,
                SubWeaponId = SubWeaponConfigService.Current.DefaultId(1), SpecialWeaponId = 1,
                Grounded = true, Position = new Vector3(.05f, .14f, .05f), Revision = 1 };
            input = new PlayerInputFrame { Swim = true };
            motor.Restore(state);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            PaperBodyTests.ReleaseTestBodies(); SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear(); LubanConfigService.Current.Reset();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Tick() { now += Dt; motor.Step(ref state, input, Dt, now, false); }
        void ResetAt(Vector3 feet)
        {
            state.Position = feet; state.Grounded = true; state.Swimming = false; state.CompactBody = false;
            state.VerticalSpeed = 0; state.PlanarVelocity = Vector3.zero; state.Movement = MovementMode.Human;
            state.AirHumanOffset = 0; state.PaperPose = PaperPose.None; motor.Restore(state);
        }
        static BlockWorld Floor(byte owner, int y = 0)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int z = -12; z <= 12; z++) for (int x = -12; x <= 12; x++)
                world.Set(new Vector3Int(x, y, z), BlockCell.Create(owner, true));
            return world;
        }
        static BlockWorld Staircase(int baseHeight = 10)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++)
                world.Set(new Vector3Int(x, baseHeight + Mathf.FloorToInt(x / 2f), z), BlockCell.Create(1, true));
            return world;
        }
        static BoxCollider MapBox(string name, Vector3 center, Vector3 size)
        {
            var root = new GameObject(name); root.transform.position = center;
            var box = root.AddComponent<BoxCollider>(); box.size = size;
            Physics.SyncTransforms(); return box;
        }
        void AssertNeutralContact()
        {
            Assert.That(state.Swimming, Is.True); Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.Neutral));
            Assert.That(state.FriendlyInkContact, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.SupportTerrainRevision, Is.Zero);
        }
        IEnumerator Publish(BlockWorld world, params Vector3Int[] changedChunks)
        {
            uint revision = terrain.World.Revision + 1;
            var chunks = changedChunks.Length > 0 ? changedChunks : world.Chunks.Keys.Concat(terrain.World.Chunks.Keys).Distinct().ToArray();
            world.Seal(revision, chunks);
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && (terrain.PendingTransactions != 0 || terrain.World.Revision != revision); tick++)
            {
                terrain.Tick(false, now, Dt);
                int frame = Time.frameCount;
                double deadline = Time.realtimeSinceStartupAsDouble + 3;
                do { yield return null; }
                while (Time.frameCount == frame && Time.realtimeSinceStartupAsDouble < deadline);
                Assert.That(Time.frameCount, Is.Not.EqualTo(frame), "Publication retries require a new native frame, rather than another Editor test-runner update");
            }
            Assert.That(terrain.PendingTransactions, Is.Zero, "terrain publication must finish before contact queries");
            Assert.That(terrain.World.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static object Invoke(PrototypePlayer player, string method, params object[] args) =>
            typeof(PrototypePlayer).GetMethod(method, Private).Invoke(player, args);
        static void Set(PrototypePlayer player, string field, object value) => typeof(PrototypePlayer).GetField(field, Private).SetValue(player, value);
        static PlayerSnapshot Prediction(PrototypePlayer player) => (PlayerSnapshot)typeof(PrototypePlayer).GetField("_predicted", Private).GetValue(player);
        static List<PlayerInputFrame> History(PrototypePlayer player) => (List<PlayerInputFrame>)typeof(PrototypePlayer).GetField("_history", Private).GetValue(player);
        PrototypePlayer PredictedOwner(PlayerSnapshot authority, int frames = 12)
        {
            controller.enabled = false;
            // Play Mode replay enters the production presentation path. Supply
            // the authored assets rather than relying on its Edit Mode bypass.
            var hero = GameplayConfig.GetHero(authority.HeroId);
            Assert.That(hero.Id, Is.EqualTo(1), "These replay fixtures use the authored RifleGirl assets");
            PrototypeApp.Current.Heroes.Replace(new HeroContent(hero,
                AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/RifleGirl/Prefabs/RifleGirlVisual.prefab"),
                AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Weapons/RifleGirl/Prefabs/RifleGirlRifle.prefab")));
            var player = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab")).GetComponent<PrototypePlayer>();
            var capsule = player.GetComponent<CharacterController>(); var ownerMotor = new PlayerMotorSimulation(capsule);
            Set(player, "_motor", ownerMotor); Set(player, "_controller", capsule); Set(player, "_predicted", authority);
            ownerMotor.Restore(authority, false);
            for (uint sequence = 1; sequence <= frames; sequence++) History(player).Add(new PlayerInputFrame
                { Sequence = sequence, Revision = authority.Revision, Swim = true, Move = Vector2.up });
            return player;
        }

        [UnityTest] public IEnumerator PredictionWaitsForItsSupportChunkAndResumesAfterAtomicPublication()
        {
            yield return Publish(Floor(1)); ResetAt(new Vector3(.05f, .14f, .05f));
            for (int tick = 0; tick < 4; tick++) Tick();
            Assert.That(state.SupportTerrainRevision, Is.GreaterThan(0), "Prediction starts from a real published block support");
            var authority = state; authority.SimulatedAt = 1; authority.RequiredPaintSequence = 5000;
            authority.SupportTerrainRevision = terrain.World.Revision + 1;
            var player = PredictedOwner(authority); var supportChunk = BlockWorld.ChunkAt(authority.SupportCell);
            Invoke(player, "ReconcilePrediction", authority, false, true, (uint)0, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.True); Assert.That(player.LastReplaySteps, Is.Zero);
            Assert.That(Prediction(player).Position, Is.EqualTo(authority.Position));
            Invoke(player, "PredictInput", new PlayerInputFrame { Sequence = 13, Revision = authority.Revision, Swim = true, Move = Vector2.up }, true, MatchPhase.Practice);
            Assert.That(player.PredictionSteps, Is.Zero);

            var unrelated = terrain.World.Clone(); var distant = new Vector3Int(100, 0, 100);
            unrelated.Set(distant, BlockCell.Create(2, true));
            yield return Publish(unrelated, BlockWorld.ChunkAt(distant));
            Assert.That(terrain.World.Revision, Is.GreaterThanOrEqualTo(authority.SupportTerrainRevision));
            Assert.That(terrain.ChunkRevision(supportChunk), Is.LessThan(authority.SupportTerrainRevision));
            Invoke(player, "RefreshPredictionPaint", authority, true, (uint)5000, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.True); Assert.That(player.LastReplaySteps, Is.Zero);

            authority.AcknowledgedInput = 4;
            yield return Publish(terrain.World.Clone(), supportChunk);
            Invoke(player, "RefreshPredictionPaint", authority, true, (uint)0, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.False); Assert.That(player.LastReplaySteps, Is.EqualTo(8));
            Assert.That(History(player)[0].Sequence, Is.EqualTo(5)); Assert.That(Prediction(player).Position.z, Is.GreaterThan(authority.Position.z));
            var resumed = Prediction(player); uint replays = player.PaintReplayCount;
            Invoke(player, "RefreshPredictionPaint", authority, true, (uint)0, MatchPhase.Practice);
            Assert.That(Prediction(player).Position, Is.EqualTo(resumed.Position)); Assert.That(player.PaintReplayCount, Is.EqualTo(replays));

            yield return Publish(terrain.World.Clone(), BlockWorld.ChunkAt(distant));
            Invoke(player, "RefreshPredictionPaint", authority, true, (uint)0, MatchPhase.Practice);
            Invoke(player, "PredictInput", new PlayerInputFrame { Sequence = 13, Revision = authority.Revision, Swim = true, Move = Vector2.up }, true, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.False); Assert.That(player.PredictionSteps, Is.EqualTo(1));
            Assert.That(Prediction(player).Position.z, Is.GreaterThan(resumed.Position.z), "unrelated chunk activity must not pause this owner");
        }

        [UnityTest] public IEnumerator PublishedEmptyChunkTombstoneLetsPredictionResolveRemovedSupport()
        {
            yield return Publish(Floor(1)); ResetAt(new Vector3(.05f, .14f, .05f));
            for (int tick = 0; tick < 4; tick++) Tick();
            Assert.That(state.SupportTerrainRevision, Is.GreaterThan(0), "The removed dependency must be the block actually supporting this capsule");
            var authority = state; authority.SimulatedAt = 1; authority.SupportTerrainRevision++;
            var player = PredictedOwner(authority);
            Invoke(player, "ReconcilePrediction", authority, false, true, (uint)0, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.True);
            var empty = terrain.World.Clone();
            foreach (var pair in terrain.World.Chunks)
            for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
                if (pair.Value.Cells[i].Occupied) empty.Set(BlockWorld.CellFromIndex(pair.Key, i), default);
            yield return Publish(empty);
            Assert.That(terrain.ChunkRevision(BlockWorld.ChunkAt(authority.SupportCell)), Is.GreaterThanOrEqualTo(authority.SupportTerrainRevision));
            Invoke(player, "RefreshPredictionPaint", authority, true, (uint)0, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.False); Assert.That(player.LastReplaySteps, Is.EqualTo(12));
            Assert.That(Prediction(player).Grounded, Is.False); Assert.That(Prediction(player).HasInkRecovery, Is.False);
            Assert.That(Prediction(player).Movement, Is.EqualTo(MovementMode.Air));
        }

        [UnityTest] public IEnumerator OnlyFriendlyStableBlockGroundAllowsTraversalAndEnemyGroundDoesNotDealDamage()
        {
            yield return Publish(Floor(1)); ResetAt(new Vector3(.05f, .14f, .05f));
            for (int tick = 0; tick < 4; tick++) Tick();
            Assert.That(state.Swimming && state.Grounded && state.HasInkRecovery, Is.True);
            Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.World.Revision));
            yield return Publish(Floor(2));
            Tick(); Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.False);
            Assert.That(state.HasInkRecovery, Is.False);
            state.Health = 50; state.LastDamageAt = now;
            for (int tick = 0; tick < 20; tick++)
            {
                Tick(); ResourceSimulation.Step(ref state, GameplayConfig.DefaultHero, true, false, Dt, now);
                Assert.That(state.Health, Is.GreaterThanOrEqualTo(50), "enemy blocks cannot apply legacy enemy-ink damage");
            }
        }

        [UnityTest] public IEnumerator BareMapGroundIgnoresLegacyPaintAndUsesNeutralSpeedAndOrdinaryRecovery()
        {
            var box = MapBox("Bare map floor", Vector3.down * .5f, new Vector3(12, 1, 12));
            // This oracle requires CPU ownership and a real collider, not legacy
            // GPU paint. Configure the disabled component before any OnEnable.
            box.gameObject.SetActive(false);
            var paint = box.gameObject.AddComponent<PaintSurface>(); paint.enabled = false;
            paint.Scores = true; paint.WalkableSize = new Vector2(12, 12);
            paint.InitializeOwnership(.125f);
            box.gameObject.SetActive(true); Physics.SyncTransforms();
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            foreach (byte owner in new byte[] { 0, 1, 2 })
            {
                for (int i = 0; i < paint.Ownership.Cells.Length; i++) paint.Ownership.Set(i, owner);
                ResetAt(new Vector3(.05f, .04f, .05f)); input.Move = Vector2.right;
                for (int tick = 0; tick < 60; tick++) Tick();
                Assert.That(state.Grounded, Is.True); Assert.That(state.Movement, Is.EqualTo(MovementMode.GroundInk));
                AssertNeutralContact();
                Assert.That(state.PlanarVelocity.magnitude, Is.EqualTo(3).Within(.002f));
                state.Ink = 20;
                ResourceSimulation.Step(ref state, GameplayConfig.DefaultHero, false, false, 1, now);
                Assert.That(state.Ink, Is.EqualTo(30).Within(.001f), "Bare map contact must not inherit old friendly paint benefits");
            }
        }

        [UnityTest] public IEnumerator NeutralMapGroundPredictionHasNoBlockDependencyAndReplaysMovement()
        {
            MapBox("Neutral prediction floor", Vector3.down * .5f, new Vector3(12, 1, 12));
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            ResetAt(new Vector3(.05f, .04f, .05f));
            for (int tick = 0; tick < 4; tick++) Tick();
            AssertNeutralContact();
            var authority = state; authority.SimulatedAt = 1;
            var player = PredictedOwner(authority);
            Invoke(player, "ReconcilePrediction", authority, false, true, (uint)0, MatchPhase.Practice);
            Assert.That(player.TerrainReplayPending, Is.False); Assert.That(player.LastReplaySteps, Is.EqualTo(12));
            var predicted = Prediction(player);
            Assert.That(predicted.Movement, Is.EqualTo(MovementMode.GroundInk));
            Assert.That(predicted.SwimSource, Is.EqualTo(SwimSurface.Neutral));
            Assert.That(predicted.SupportTerrainRevision, Is.Zero); Assert.That(predicted.HasInkRecovery, Is.False);
            Assert.That(predicted.Position.z, Is.GreaterThan(authority.Position.z));
        }

        [UnityTest] public IEnumerator BareMapWallSupportsNeutralMovementReplayJumpAndCompleteMantle()
        {
            var floor = MapBox("Bare map floor", Vector3.down * .5f, new Vector3(12, 1, 12));
            MapBox("Bare map wall and top", new Vector3(0, 1, 3), new Vector3(6, 2, 2));
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            var traversalTrace = new List<string>(); bool complete = false;
            System.Action<PlayerMotorSimulation, PlayerMotorSimulation.MotorBranchObservation> observe = (observed, row) =>
            {
                if (!ReferenceEquals(observed, motor)) return;
                traversalTrace.Add("branch=" + row.Branch + " invocation=" + row.Invocation
                    + " controller=" + row.ControllerPosition.ToString("F5") + " nativeGround=" + row.ControllerGrounded
                    + " probe=" + row.ProbeEvaluated + "/" + row.ProbeSucceeded + " collisions=" + row.Collisions
                    + " requested=" + row.RequestedMove.ToString("F5") + " rebase=" + row.RebaseHeight.ToString("F5")
                    + " contact=" + row.Contact.Owner + "/" + row.Contact.IsBlock + "/" + row.Contact.Stable + "/" + row.Contact.Climbable
                    + "/" + row.Contact.Point.ToString("F5") + "/" + row.Contact.Normal.ToString("F5")
                    + " before=" + JsonUtility.ToJson(row.Before) + " after=" + JsonUtility.ToJson(row.After));
            };
            PlayerMotorSimulation.MotorBranchObserved += observe;
            try
            {
                ResetAt(new Vector3(.05f, .04f, .8f));
                input.Swim = false; input.Move = Vector2.zero;
                for (int tick = 0; tick < 12; tick++)
                {
                    Tick();
                    if (state.Grounded && controller.isGrounded) break;
                }
                Assert.That(state.Grounded && controller.isGrounded && !state.Swimming, Is.True,
                    "Bare-wall fixture must first land as human through the real controller: " + JsonUtility.ToJson(state));
                Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out var ground, controller.slopeLimit, controller.radius), Is.True);
                Assert.That(ground.Stable && !ground.IsBlock && ground.Owner == 0, Is.True);
                Assert.That(Physics.Raycast(state.Position + Vector3.up * .08f, Vector3.down, out var floorHit, .16f,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(floorHit.collider, Is.SameAs(floor));
                input.Swim = true; input.Move = Vector2.up;
                for (int tick = 0; tick < 90 && state.Movement != MovementMode.WallInk; tick++) Tick();
                Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); AssertNeutralContact();
                var start = state.Position;
                for (int tick = 0; tick < 12; tick++) Tick();
                Assert.That(state.Position.y - start.y, Is.EqualTo(.6f).Within(.015f));
                Assert.That(Vector3.ProjectOnPlane(state.Velocity, state.WallNormal).magnitude, Is.EqualTo(3).Within(.01f));
                AssertNeutralContact(); state.Ink = 20;
                ResourceSimulation.Step(ref state, GameplayConfig.DefaultHero, false, false, 1, now);
                Assert.That(state.Ink, Is.EqualTo(30).Within(.001f));

                var wall = state; double wallTime = now; input.Move = Vector2.right;
                for (int tick = 0; tick < 8; tick++) Tick();
                var expected = state;
                state = wall; now = wallTime; motor.Restore(state);
                for (int tick = 0; tick < 8; tick++) Tick();
                Assert.That(Vector3.Distance(state.Position, expected.Position), Is.LessThan(.001f));
                Assert.That(state.SwimSource, Is.EqualTo(expected.SwimSource));
                Assert.That(state.SupportTerrainRevision, Is.Zero);

                input.JumpSequence++; Tick();
                Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.Grounded, Is.False);
                Assert.That(state.AirSwimSource, Is.EqualTo(SwimSurface.Neutral)); Assert.That(state.HasInkRecovery, Is.False);
                state = wall; now = wallTime; motor.Restore(state);
                input.JumpSequence = state.ConsumedJump; input.Move = Vector2.up;
                bool mantle = false, landed = false;
                for (int tick = 0; tick < 240; tick++)
                {
                    Tick(); mantle |= state.Movement == MovementMode.Mantle;
                    Assert.That(state.HasInkRecovery, Is.False); Assert.That(state.SupportTerrainRevision, Is.Zero);
                    if (mantle && state.Grounded && state.Movement == MovementMode.GroundInk && state.Position.y > 1.9f)
                    { landed = true; break; }
                }
                Assert.That(mantle, Is.True); Assert.That(landed, Is.True); AssertNeutralContact();
                complete = true;
            }
            finally
            {
                PlayerMotorSimulation.MotorBranchObserved -= observe;
                if (!complete)
                {
                    const string output = "Reports/BlockGameplay20261017/NeutralWallFailures";
                    System.IO.Directory.CreateDirectory(output);
                    System.IO.File.WriteAllLines(output + "/" + System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".jsonl", traversalTrace);
                }
            }
        }

        [UnityTest] public IEnumerator RemovingTheNeutralMapMantleDestinationCancelsOnTheNextMotorTick()
        {
            MapBox("Bare map floor", Vector3.down * .5f, new Vector3(12, 1, 12));
            var wall = MapBox("Removable map wall and top", new Vector3(0, 1, 3), new Vector3(6, 2, 2));
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            ResetAt(new Vector3(.05f, .04f, .8f)); input.Move = Vector2.up;
            for (int tick = 0; tick < 240 && state.Movement != MovementMode.Mantle; tick++) Tick();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Mantle));
            wall.enabled = false; Physics.SyncTransforms(); Tick();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.SupportTerrainRevision, Is.Zero);
        }

        [UnityTest] public IEnumerator BareCeilingsAndUnpublishedTerrainGeometryCannotBecomeClimbableMapFaces()
        {
            MapBox("Ceiling underside", new Vector3(0, 3, 0), new Vector3(6, .2f, 6));
            var unbound = MapBox("Unpublished terrain-owned face", new Vector3(0, 1, 3), new Vector3(6, 2, .2f));
            unbound.transform.SetParent(terrain.transform, true);
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            Assert.That(PlayerMotorSimulation.Query(Vector3.up, Vector3.up, 3, out var ceiling), Is.True);
            Assert.That(PlayerMotorSimulation.CanClimb(ceiling, 1), Is.False);
            Assert.That(PlayerMotorSimulation.Query(Vector3.up, Vector3.forward, 4, out _), Is.False,
                "A missing voxel contact in a terrain-owned collider cannot fall back to neutral geometry");
            Assert.That(PlayerMotorSimulation.TryGetGround(new Vector3(0, 2.04f, 3), out _, 45, 0), Is.False);
            var falling = new PlayerMotorSimulation.TraversalContact(new BlockContact
                { Owner = 1, Stable = false, Climbable = true, Normal = Vector3.back });
            Assert.That(PlayerMotorSimulation.CanClimb(falling, 1), Is.False);
        }

        [UnityTest] public IEnumerator RemovingPlatformEndsContactOnTheNextSimulationTick()
        {
            yield return Publish(Floor(1, 10)); ResetAt(new Vector3(.05f, 1.14f, .05f));
            // The restored capsule starts 4 cm above this platform; establish a real contact first.
            for (int tick = 0; tick < 4; tick++) Tick();
            Assert.That(state.Grounded && state.HasInkRecovery, Is.True);
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            Tick();
            Assert.That(state.Grounded, Is.False); Assert.That(state.Movement, Is.EqualTo(MovementMode.Air));
            Assert.That(state.HasInkRecovery, Is.False); Assert.That(state.SupportTerrainRevision, Is.Zero);
            Assert.That(state.VerticalSpeed, Is.LessThan(0));
        }

        [UnityTest] public IEnumerator WallAttachmentCannotBridgeADestroyedBlockHoleWithoutBackingGeometry()
        {
            var world = Floor(1);
            for (int y = 0; y < 40; y++) for (int x = -10; x <= 10; x++)
                world.Set(new Vector3Int(x, y, 20), BlockCell.Create(1, true));
            yield return Publish(world); ResetAt(new Vector3(.05f, .14f, 1)); input.Move = Vector2.up;
            for (int tick = 0; tick < 90 && state.Movement != MovementMode.WallInk; tick++) Tick();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.FriendlyInkContact, Is.True);
            for (int tick = 0; tick < 8; tick++) Tick();
            var damaged = terrain.World.Clone(); var support = state.SupportCell;
            for (int y = support.y - 2; y <= support.y + 2; y++) for (int x = support.x - 2; x <= support.x + 2; x++)
                damaged.Set(new Vector3Int(x, y, 20), default);
            yield return Publish(damaged); Tick();
            Assert.That(state.Movement, Is.Not.EqualTo(MovementMode.WallInk)); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.SupportTerrainRevision, Is.Zero);

        }

        [UnityTest] public IEnumerator RemovingBlockWallRevealsAnActualBackingMapWallAsNeutralContact()
        {
            MapBox("Actual backing map wall", new Vector3(0, 2, 2.2f), new Vector3(6, 4, .2f));
            var world = Floor(1);
            for (int y = 0; y < 40; y++) for (int x = -10; x <= 10; x++)
                world.Set(new Vector3Int(x, y, 20), BlockCell.Create(1, true));
            yield return Publish(world); ResetAt(new Vector3(.05f, .14f, 1)); input.Move = Vector2.up;
            for (int tick = 0; tick < 90 && state.Movement != MovementMode.WallInk; tick++) Tick();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); Assert.That(state.HasInkRecovery, Is.True);
            for (int tick = 0; tick < 8; tick++) Tick();
            yield return Publish(Floor(1)); Tick();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); AssertNeutralContact();
            Assert.That(state.WallPoint.z, Is.EqualTo(2.1f).Within(.005f));
        }

        [UnityTest] public IEnumerator StaircasePoseAndReplayUseTheSameSupportFrame()
        {
            yield return Publish(Staircase()); ResetAt(new Vector3(.05f, 1.14f, .05f));
            for (int tick = 0; tick < 8; tick++) Tick();
            Assert.That(state.Grounded && state.Swimming, Is.True);
            float slope = Vector3.Angle(Vector3.up, state.GroundNormal);
            Assert.That(slope, Is.InRange(5f, 45.01f));
            Assert.That(Vector3.Dot(state.PaperRotation * Vector3.forward, state.GroundNormal), Is.GreaterThan(.9999f));
            Assert.That(state.GroundNormal.x, Is.LessThan(0));
            var saved = state; double checkpoint = now; input.Move = Vector2.up;
            for (int tick = 0; tick < 8; tick++) Tick(); var expected = state;
            state = saved; now = checkpoint; motor.Restore(state);
            for (int tick = 0; tick < 8; tick++) Tick();
            Assert.That(Vector3.Distance(state.Position, expected.Position), Is.LessThan(.002f));
            Assert.That(Vector3.Distance(state.PaperCenter, expected.PaperCenter), Is.LessThan(.002f));
            Assert.That(Quaternion.Angle(state.PaperRotation, expected.PaperRotation), Is.LessThan(.01f));
        }

        [UnityTest] public IEnumerator FittedBlockFrameDrivesTheVisibleSheetAndWeaponHitGeometry()
        {
            yield return Publish(Staircase()); ResetAt(new Vector3(.05f, 1.14f, .05f));
            for (int tick = 0; tick < 8; tick++) Tick();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab");
            var body = Object.Instantiate(prefab, controller.transform).GetComponent<SwimBody>();
            body.Bind(PaperBodyTests.Profile("RifleGirl"));
            body.ApplyCollision(state); body.Present(state, new Vector3(.2f, .5f, .3f), Quaternion.Euler(0, 120, 0));
            Assert.That(body.FlatHitActive, Is.True); Assert.That(body.BodyRenderer.enabled, Is.True);
            Assert.That(Vector3.Distance(body.HitVolume.transform.position, state.PaperCenter), Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(body.HitVolume.transform.rotation, state.PaperRotation), Is.LessThan(.001f));
            Assert.That(Quaternion.Angle(body.BodyRenderer.transform.rotation, state.PaperRotation), Is.LessThan(.001f));
            Assert.That(Mathf.Abs(Vector3.Dot(body.BodyRenderer.transform.position - state.PaperCenter, state.GroundNormal)),
                Is.LessThan(.0001f), "render correction cannot shift the sheet off its authoritative support plane");
        }

        static BlockWorld MovementStaircase()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -24; x <= 24; x++) for (int z = -20; z <= 20; z++)
                world.Set(new Vector3Int(x, 20 + Mathf.FloorToInt(x / 2f), z), BlockCell.Create(1, true));
            return world;
        }
        IEnumerator TraverseStaircase(Vector2 direction)
        {
            yield return Publish(MovementStaircase()); ResetAt(new Vector3(.05f, 2.14f, .05f));
            input.Move = Vector2.zero; for (int tick = 0; tick < 8; tick++) Tick();
            Assert.That(state.Grounded && state.Swimming, Is.True); var start = state.Position;
            input.Move = direction;
            for (int tick = 0; tick < 12; tick++) Tick();
            input.Move = Vector2.zero; for (int tick = 0; tick < 90; tick++) Tick();
            Assert.That(state.Grounded && state.Swimming && state.HasInkRecovery, Is.True, "Movement must finish on actual friendly stair support");
            Assert.That((state.Position.x - start.x) * Mathf.Sign(direction.x), Is.GreaterThan(.35f));
            if (direction.y > 0) Assert.That(state.Position.z - start.z, Is.GreaterThan(.35f));
            Assert.That((state.Position.y - start.y) * Mathf.Sign(direction.x), Is.GreaterThan(.1f), "The motor must climb or descend the physical voxel steps");
            Assert.That(Vector3.Angle(Vector3.up, state.GroundNormal), Is.InRange(5f, 45.01f));
            Assert.That(Vector3.Dot(state.PaperRotation * Vector3.forward, state.GroundNormal), Is.GreaterThan(.9999f));
            Assert.That(state.SupportTerrainRevision, Is.GreaterThan(0));
        }
        [UnityTest] public IEnumerator ForwardSlopeMovementClimbsRealBlockSteps() => TraverseStaircase(Vector2.right);
        [UnityTest] public IEnumerator ReverseSlopeMovementDescendsRealBlockSteps() => TraverseStaircase(Vector2.left);
        [UnityTest] public IEnumerator DiagonalSlopeMovementKeepsTheRealSupportFrame() => TraverseStaircase(new Vector2(1, 1).normalized);

        [UnityTest] public IEnumerator NarrowBlockStepsSupportTheMotorAndEndContactAfterTheirRemoval()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = 0; x <= 1; x++) for (int z = -30; z <= 30; z++)
                world.Set(new Vector3Int(x, 10 + Mathf.FloorToInt(z / 3f), z), BlockCell.Create(1, true));
            yield return Publish(world); ResetAt(new Vector3(.1f, 1.14f, .05f));
            input.Move = Vector2.zero; for (int tick = 0; tick < 8; tick++) Tick();
            Assert.That(state.Grounded && state.Swimming, Is.True); var start = state.Position;
            input.Move = Vector2.up; for (int tick = 0; tick < 12; tick++) Tick();
            input.Move = Vector2.zero; for (int tick = 0; tick < 60; tick++) Tick();
            Assert.That(state.Grounded && state.HasInkRecovery, Is.True, "A 0.2m strip uses real capsule support, not invented neighbouring cells");
            Assert.That(state.Position.z - start.z, Is.GreaterThan(.5f)); Assert.That(state.Position.y, Is.GreaterThan(start.y + .1f));
            Assert.That(state.GroundNormal.z, Is.LessThan(-.05f));
            var removed = terrain.World.Clone(); var support = state.SupportCell;
            for (int x = 0; x <= 1; x++) for (int z = support.z - 5; z <= support.z + 5; z++)
                for (int y = 0; y < 30; y++) removed.Set(new Vector3Int(x, y, z), default);
            yield return Publish(removed); Tick();
            Assert.That(state.Grounded, Is.False); Assert.That(state.HasInkRecovery, Is.False); Assert.That(state.SupportTerrainRevision, Is.Zero);
        }

        static BlockWorld MantleWorld(int topKind)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -20; x <= 20; x++) for (int z = -15; z <= 42; z++) world.Set(new Vector3Int(x, 0, z), BlockCell.Create(1, true));
            for (int x = -10; x <= 10; x++) for (int z = 20; z <= 32; z++) for (int y = 1; y < 20; y++)
            {
                if (topKind == 1 && x >= -4 && x <= 4 && z >= 21 && z <= 29) continue;
                world.Set(new Vector3Int(x, y, z), BlockCell.Create(topKind == 2 && z > 20 && y == 19 ? (byte)2 : (byte)1, true));
            }
            return world;
        }
        IEnumerator CheckBlockMantle(int topKind)
        {
            yield return Publish(MantleWorld(topKind)); ResetAt(new Vector3(.05f, .14f, .8f));
            input.Move = Vector2.zero; for (int tick = 0; tick < 4; tick++) Tick();
            input.Move = Vector2.up; bool wall = false, mantle = false, landed = false;
            for (int tick = 0; tick < 240; tick++)
            {
                Tick(); wall |= state.Movement == MovementMode.WallInk; mantle |= state.Movement == MovementMode.Mantle;
                if (topKind != 0) Assert.That(state.Movement, Is.Not.EqualTo(MovementMode.Mantle), "A hole or enemy target is not a friendly stable mantle destination");
                if (mantle && state.Grounded && state.Movement == MovementMode.GroundInk && state.Position.y > 1.9f) { landed = true; break; }
            }
            Assert.That(wall, Is.True, "This must reach the actual friendly block wall before evaluating its top");
            if (topKind == 0)
            {
                Assert.That(mantle, Is.True); Assert.That(landed, Is.True); Assert.That(state.Position.z, Is.GreaterThan(2));
                Assert.That(state.HasInkRecovery, Is.True); Assert.That(state.SupportTerrainRevision, Is.GreaterThan(0));
            }
            else Assert.That(mantle, Is.False);
        }
        [UnityTest] public IEnumerator FriendlyStableBlockTopAllowsACompleteMotorMantle() => CheckBlockMantle(0);
        [UnityTest] public IEnumerator BlockWallCannotMantleAcrossAMissingDestinationColumn() => CheckBlockMantle(1);
        [UnityTest] public IEnumerator BlockWallCannotMantleOntoEnemySupport() => CheckBlockMantle(2);

        [UnityTest] public IEnumerator RemovingTheBlockMantleDestinationCancelsOnTheNextMotorTick()
        {
            yield return Publish(MantleWorld(0)); ResetAt(new Vector3(.05f, .14f, .8f)); input.Move = Vector2.up;
            for (int tick = 0; tick < 240 && state.Movement != MovementMode.Mantle; tick++) Tick();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Mantle)); var target = state.MantleTo;
            var removed = terrain.World.Clone();
            for (int x = -4; x <= 4; x++) for (int z = 21; z <= 29; z++) for (int y = 1; y < 20; y++)
                removed.Set(new Vector3Int(x, y, z), default);
            yield return Publish(removed);
            Assert.That(PlayerMotorSimulation.TryGetGround(target, out _, controller.slopeLimit, 0), Is.False);
            Tick(); Assert.That(state.Movement, Is.Not.EqualTo(MovementMode.Mantle), "The owner cannot continue a published unsupported mantle path");
        }

        [UnityTest, Timeout(600000)] public IEnumerator TransverseSlopeAndBridgeDecksUseRealSupportAndDetachAfterRemoval()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab");
            var body = Object.Instantiate(prefab, controller.transform).GetComponent<SwimBody>();
            body.Bind(PaperBodyTests.Profile("RifleGirl"));
            void Attached()
            {
                Assert.That(state.Grounded && state.Swimming && state.HasInkRecovery, Is.True);
                Assert.That(state.Movement, Is.EqualTo(MovementMode.GroundInk));
                Assert.That(state.SupportTerrainRevision, Is.GreaterThan(0));
                Assert.That(terrain.World.TryGet(state.SupportCell, out var support), Is.True);
                Assert.That(support.Owner, Is.EqualTo(1));
                Assert.That(Vector3.Distance(controller.transform.position, state.Position), Is.LessThan(.0001f));
                Assert.That(Vector3.Dot(state.PaperRotation * Vector3.forward, state.GroundNormal), Is.GreaterThan(.9999f));
                body.ApplyCollision(state); body.Present(state, Vector3.zero, Quaternion.Euler(0, state.Yaw, 0));
                Assert.That(Vector3.Distance(body.BodyRenderer.transform.position, state.PaperCenter), Is.LessThan(.0001f));
                Assert.That(body.FlatHitActive && body.BodyRenderer.enabled, Is.True);
                Assert.That(Vector3.Distance(body.HitVolume.transform.position, state.PaperCenter), Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(body.HitVolume.transform.rotation, state.PaperRotation), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(body.BodyRenderer.transform.rotation, state.PaperRotation), Is.LessThan(.001f));
            }

            yield return Publish(MovementStaircase()); ResetAt(new Vector3(.05f, 2.14f, .05f));
            input.Move = Vector2.zero; for (int tick = 0; tick < 8; tick++) Tick();
            Attached(); var transverseStart = state.Position;
            input.Move = Vector2.up;
            for (int tick = 0; tick < 12; tick++)
            {
                Tick(); Attached();
                Assert.That(state.GroundNormal.x, Is.LessThan(-.05f));
                Assert.That(Vector3.Angle(Vector3.up, state.GroundNormal), Is.InRange(5f, 45.01f));
                Assert.That(state.SupportCell.y, Is.EqualTo(20 + Mathf.FloorToInt(state.SupportCell.x / 2f)));
            }
            input.Move = Vector2.zero; for (int tick = 0; tick < 90; tick++) { Tick(); Attached(); }
            Assert.That(state.Position.z - transverseStart.z, Is.GreaterThan(.35f), "Actually travel across the x-oriented slope");
            Assert.That(Mathf.Abs(state.Position.x - transverseStart.x), Is.LessThan(.1f));
            Assert.That(Mathf.Abs(state.Position.y - transverseStart.y), Is.LessThan(.1f));

            // Both published decks occupy the same horizontal columns, with a clear passage below the bridge.
            var bridge = new BlockWorld(new Vector2(32, 64));
            for (int x = -20; x <= 20; x++) for (int z = -50; z <= 50; z++)
            {
                bridge.Set(new Vector3Int(x, 0, z), BlockCell.Create(1, true));
                bridge.Set(new Vector3Int(x, 30, z), BlockCell.Create(1, true));
            }
            foreach (int deck in new[] { 0, 30 })
            {
                yield return Publish(bridge.Clone());
                ResetAt(new Vector3(.05f, (deck + 1) * BlockWorld.CellSize + .04f, .05f));
                input.Move = Vector2.zero; for (int tick = 0; tick < 8; tick++) Tick();
                Attached(); Assert.That(state.SupportCell.y, Is.EqualTo(deck));
                if (deck == 0)
                {
                    Assert.That(Physics.Raycast(state.Position + Vector3.up * .2f, Vector3.up, out var roof, 4,
                        1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                    Assert.That(terrain.TryContact(roof, out var roofContact), Is.True);
                    Assert.That(roofContact.Cell.y, Is.EqualTo(30), "There is a real published bridge over the lower passage");
                }
                else
                {
                    Assert.That(Physics.Raycast(new Vector3(state.Position.x, 2.9f, state.Position.z), Vector3.down,
                        out var lower, 3, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                    Assert.That(terrain.TryContact(lower, out var lowerContact), Is.True);
                    Assert.That(lowerContact.Cell.y, Is.Zero, "The lower deck remains beneath the bridge walker");
                }
                var start = state.Position; input.Move = Vector2.up;
                for (int tick = 0; tick < 12; tick++)
                {
                    Tick(); Attached(); Assert.That(state.SupportCell.y, Is.EqualTo(deck));
                    Assert.That(Vector3.Dot(state.GroundNormal, Vector3.up), Is.GreaterThan(.9999f));
                }
                input.Move = Vector2.zero;
                for (int tick = 0; tick < 90; tick++) { Tick(); Attached(); Assert.That(state.SupportCell.y, Is.EqualTo(deck)); }
                Assert.That(state.Position.z - start.z, Is.GreaterThan(.35f), "Both bridge levels require real CharacterController displacement");
                Assert.That(Mathf.Abs(state.Position.y - start.y), Is.LessThan(.1f));

                var removed = terrain.World.Clone(); var underfoot = state.SupportCell;
                for (int x = underfoot.x - 8; x <= underfoot.x + 8; x++)
                for (int z = underfoot.z - 8; z <= underfoot.z + 8; z++) removed.Set(new Vector3Int(x, deck, z), default);
                yield return Publish(removed); Tick();
                Assert.That(state.Grounded, Is.False); Assert.That(state.HasInkRecovery, Is.False);
                Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.Movement, Is.EqualTo(MovementMode.Air));
                Assert.That(state.VerticalSpeed, Is.LessThan(0), "Destroying either actual support cannot transfer attachment to the other deck");
            }
        }

        [TestCase(0f)] [TestCase(35f)] [TestCase(80f)]
        public void FittedSheetClearsRealVoxelTopFacesInsideItsRotatedFootprint(float yaw)
        {
            var world = Staircase(5);
            Vector3 normal = new Vector3(-.5f, 1, 0).normalized;
            Quaternion rotation = PaperPoseSimulation.GroundRotation(normal, yaw);
            Vector2 size = new(.9f, 1.8f); const float offset = .03f;
            Vector3 center = PaperPoseSimulation.BlockGroundCenter(world, new Vector3(.05f, .6f, .05f), normal, rotation, size, offset);
            Quaternion inverse = Quaternion.Inverse(rotation);
            foreach (var chunk in world.Chunks)
            for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
            {
                if (!chunk.Value.Cells[i].Occupied) continue;
                var cell = BlockWorld.CellFromIndex(chunk.Key, i);
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3 vertex = (Vector3)cell * BlockWorld.CellSize + new Vector3((corner & 1) * BlockWorld.CellSize,
                        BlockWorld.CellSize, ((corner >> 1) & 1) * BlockWorld.CellSize);
                    Vector3 local = inverse * (vertex - center);
                    if (Mathf.Abs(local.x) > size.x * .5f || Mathf.Abs(local.y) > size.y * .5f) continue;
                    Assert.That(local.z, Is.LessThanOrEqualTo(-offset + .0001f), "an authoritative paper plane cannot intersect a real stair top");
                }
            }
        }

        [Test] public void SupportFrameSurvivesNetworkSerialization()
        {
            state.SupportCell = new Vector3Int(-17, 12, 31); state.SupportTerrainRevision = 47;
            state.GroundNormal = new Vector3(-.5f, 1, .2f).normalized;
            state.PaperRotation = PaperPoseSimulation.GroundRotation(state.GroundNormal, 72);
            using var writer = new Unity.Netcode.FastBufferWriter(4096, Unity.Collections.Allocator.Temp);
            writer.WriteNetworkSerializable(state);
            using var reader = new Unity.Netcode.FastBufferReader(writer, Unity.Collections.Allocator.Temp);
            reader.ReadNetworkSerializable(out PlayerSnapshot restored);
            Assert.That(restored.SupportCell, Is.EqualTo(state.SupportCell));
            Assert.That(restored.SupportTerrainRevision, Is.EqualTo(47)); Assert.That(restored.GroundNormal, Is.EqualTo(state.GroundNormal));
            Assert.That(restored.PaperRotation, Is.EqualTo(state.PaperRotation));
        }
    }
}
#endif
