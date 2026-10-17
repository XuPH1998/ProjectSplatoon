#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Real EditMode PhysX and terrain publication; network membership is supplied explicitly.
    // This fixture does not certify a remote process or rendered Player acceptance.
    public sealed class BlockSafetyTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<PrototypePlayer> players = new();
        readonly Dictionary<ulong, PrototypePlayer> previousPlayers = new();
        PrototypeArena arena;
        BlockTerrain terrain;
        object Call(string name, params object[] args) => typeof(BlockTerrain).GetMethod(name, Private).Invoke(terrain, args);
        int CachedPositions => ((IDictionary)typeof(BlockTerrain).GetField("_safePositions", Private).GetValue(terrain)).Count;
        bool Excluded(Vector3Int cell) => (bool)Call("ExcludedConstruction", cell);
        static void Spawned(PrototypePlayer player, bool value) => typeof(NetworkBehaviour).GetProperty(nameof(NetworkBehaviour.IsSpawned)).SetValue(player, value);

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            previousPlayers.Clear(); foreach (var pair in PrototypePlayer.ByOwner) previousPlayers.Add(pair.Key, pair.Value);
            PrototypePlayer.ByOwner.Clear(); players.Clear(); LubanConfigService.Current.Reset();
            arena = new GameObject("Safety arena").AddComponent<PrototypeArena>();
            arena.SpawnPoints = new Transform[8];
            for (int i = 0; i < 8; i++)
            {
                var point = new GameObject("Safety spawn " + i).transform; point.SetParent(arena.transform);
                point.position = new Vector3(6 + (i % 4) * 2, .04f, i < 4 ? -6 : 6); arena.SpawnPoints[i] = point;
            }
            HeroMigrationTests.Load();
            terrain = new GameObject("Safety block world").AddComponent<BlockTerrain>(); terrain.Initialize(arena);
        }
        [TearDown] public void Cleanup()
        {
            foreach (var player in players) if (player != null) Spawned(player, false);
            PaperBodyTests.ReleaseTestBodies(); PrototypePlayer.ByOwner.Clear();
            foreach (var pair in previousPlayers) if (pair.Value != null) PrototypePlayer.ByOwner.Add(pair.Key, pair.Value);
            LubanConfigService.Current.Reset(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        PrototypePlayer Player(Vector3 position, bool prefab = false)
        {
            PrototypePlayer player;
            if (prefab) player = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab")).GetComponent<PrototypePlayer>();
            else
            {
                var root = new GameObject("Safety authority player") { layer = 8 };
                root.AddComponent<NetworkObject>(); root.AddComponent<CharacterController>(); player = root.AddComponent<PrototypePlayer>();
            }
            var state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1, Slot = 0,
                Position = position, Health = 100, Ink = 43, SpecialPoints = 7, LifeState = PlayerLifeState.Alive,
                GroundNormal = Vector3.up, PaperRotation = Quaternion.identity };
            Place(player, state); Spawned(player, true); players.Add(player); PrototypePlayer.ByOwner.Add((ulong)players.Count, player);
            return player;
        }
        static void Place(PrototypePlayer player, PlayerSnapshot state)
        {
            var controller = player.GetComponent<CharacterController>(); controller.enabled = false;
            HeroBodyShape.For(state.HeroId).Apply(controller, state.Swimming || state.CompactBody);
            player.transform.position = state.Position; controller.enabled = true; player.Snapshot.Value = state; Physics.SyncTransforms();
        }
        HeroChangeZone Zone(Vector3 position, Vector3 size, Quaternion rotation, Vector3 scale)
        {
            var root = new GameObject("Safety construction exclusion"); root.transform.SetParent(arena.transform);
            root.transform.SetPositionAndRotation(position, rotation); root.transform.localScale = scale;
            var zone = root.AddComponent<HeroChangeZone>(); zone.Volume.size = size; zone.Volume.isTrigger = true; zone.Team = 1;
            Physics.SyncTransforms(); Call("InitializeSafety"); return zone;
        }
        static BoxCollider Box(string name, Vector3 center, Vector3 size, int layer = 0)
        {
            var root = new GameObject(name) { layer = layer }; root.transform.position = center;
            var box = root.AddComponent<BoxCollider>(); box.size = size; Physics.SyncTransforms(); return box;
        }
        void Capture() => terrain.Tick(true, 1, 1f / 60);
        void Recover() { Call("RecoverEmbeddedPlayers"); Physics.SyncTransforms(); }
        Vector3 CachedPosition(PrototypePlayer player)
        {
            var value = ((IDictionary)typeof(BlockTerrain).GetField("_safePositions", Private).GetValue(terrain))[player];
            return (Vector3)value.GetType().GetField("Position").GetValue(value);
        }
        static PlayerSnapshot AirPaper(PrototypePlayer player, float airOffset = .87f, bool compactOnly = false)
        {
            var state = player.Snapshot.Value;
            state.Swimming = !compactOnly; state.CompactBody = compactOnly; state.Grounded = false;
            state.Movement = MovementMode.Air; state.PaperPose = PaperPose.Air; state.AirHumanOffset = airOffset;
            state.PaperCenter = state.Position + Vector3.up * .03f;
            state.PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 0);
            state.SwimSource = state.AirSwimSource = SwimSurface.Friendly; state.SwimWasHeld = true;
            state.Velocity = new Vector3(.2f, -1.1f, -.3f); state.PlanarVelocity = new Vector3(.2f, 0, -.3f); state.VerticalSpeed = -1.1f;
            state.CameraRebaseOffset = -.12f; state.PaperChangedAt = 8; state.PaperAnimationTime = .75f;
            state.SimulatedAt = 9; state.AcknowledgedInput = 31; state.SimulationTick = 17; state.ConsumedJump = 3;
            state.ConsumedFire = 11; state.RequiredPaintSequence = 19;
            Place(player, state); player.SwimBody?.ApplyCollision(state); Physics.SyncTransforms();
            return state;
        }
        static bool LiveCapsuleOverlaps(PlayerSnapshot state)
        {
            var shape = HeroBodyShape.For(state.HeroId);
            float height = state.Swimming || state.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = state.Position + Vector3.up * (height * .5f);
            return Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment,
                shape.Radius - Mathf.Max(.005f, shape.SkinWidth), PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
        static bool LivePaperOverlaps(PrototypePlayer player, PlayerSnapshot state)
        {
            var body = player.SwimBody; var profile = body != null ? body.Profile : player.Presentation?.Paper;
            float thickness = profile != null ? profile.Thickness : .04f;
            bool Overlaps(Rect rect) => Physics.CheckBox(state.PaperCenter + state.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                new Vector3(rect.width * .5f, rect.height * .5f, thickness * .5f), state.PaperRotation,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            if (body != null && body.HitRects != null) return body.HitRects.Any(Overlaps);
            Vector2 size = profile != null ? profile.Size : new Vector2(.9f, 1.8f);
            return Overlaps(new Rect(-size * .5f, size));
        }
        static void AssertPreservedAirPaper(PlayerSnapshot before, PlayerSnapshot after)
        {
            Vector3 displacement = after.Position - before.Position;
            var expected = before; expected.Position += displacement; expected.PaperCenter += displacement;
            Assert.That(after, Is.EqualTo(expected), "Only the live motor origin and paper centre may move during a current-pose rescue");
            Assert.That(Vector3.Distance(PlayerMotorSimulation.HumanPosition(before), PlayerMotorSimulation.HumanPosition(after)),
                Is.EqualTo(displacement.magnitude).Within(.00001f));
        }
        static bool StandingOverlaps(PlayerSnapshot state)
        {
            var shape = HeroBodyShape.For(state.HeroId); float radius = shape.Radius - Mathf.Max(.005f, shape.SkinWidth);
            return Physics.CheckCapsule(state.Position + Vector3.up * shape.Radius,
                state.Position + Vector3.up * (shape.Height - shape.Radius), radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }

        [Test] public void RotatedScaledProtectionUsesWholeCellAndDoesNotExcludeEmptyAabbCorners()
        {
            var zone = Zone(new Vector3(.15f, 1, .15f), new Vector3(.2f, 2, .2f), Quaternion.Euler(0, 45, 0), new Vector3(2, 1, 1));
            var crossing = new Vector3Int(3, 10, 1); var emptyCorner = new Vector3Int(3, 10, 3);
            Assert.That(zone.Contains(1, BlockWorld.Center(crossing)), Is.False, "The cell centre is outside, but its cube crosses the protection volume");
            Assert.That(Excluded(crossing), Is.True);
            Assert.That(zone.Volume.bounds.Contains(BlockWorld.Center(emptyCorner)), Is.True, "This corner is inside the broad world AABB");
            Assert.That(Excluded(emptyCorner), Is.False, "The rotated volume does not occupy this AABB corner");
            zone.enabled = false; Assert.That(Excluded(crossing), Is.False);
        }
        [TestCase((byte)1)] [TestCase((byte)2)]
        public void BothTeamsCannotBuildInAProtectedVolume(byte team)
        {
            Zone(new Vector3(.05f, .5f, .05f), Vector3.one, Quaternion.identity, Vector3.one);
            bool completed = false; BlockMutationResult result = default;
            terrain.QueueBuild(new Vector3(.05f, -.001f, .05f), Vector3.up, team, 1, 0, false, r => { result = r; completed = true; });
            for (int i = 0; i < 100 && !completed; i++) terrain.Tick(true, i / 60d, 1f / 60);
            Assert.That(completed, Is.True); Assert.That(result.Added, Is.Zero); Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero);
            Assert.That(terrain.Revision, Is.Zero, "An entirely excluded attack cannot publish phantom terrain or credit");
        }
        [UnityTest] public IEnumerator ProtectedVerticalColumnsRejectHighConstructionAndAllowTheAdjacentRotatedCorner()
        {
            using var historicalHeight = new HistoricalBlockWorldHeightScope();
            Zone(new Vector3(.15f, 1, .15f), new Vector3(.2f, 2, .2f), Quaternion.Euler(0, 45, 0), new Vector3(2, 1, 1));
            var protectedCell = new Vector3Int(3, 310, 1); var adjacentCell = new Vector3Int(3, 310, 3);
            Assert.That(Excluded(protectedCell), Is.True, "Protection extends above the original four-metre volume to the top of the world");
            Assert.That(Excluded(adjacentCell), Is.False, "The exact vertical column test must preserve a neighbouring empty rotated corner");
            bool refused = false;
            terrain.QueueBuild(BlockWorld.Center(protectedCell) - Vector3.up * .051f, Vector3.up, 2, 1, 0, false,
                result => { Assert.That(result.Added, Is.Zero); refused = true; });
            for (int i = 0; i < 100 && !refused; i++) { terrain.Tick(true, i / 60d, 1f / 60); yield return null; }
            Assert.That(refused, Is.True); Assert.That(terrain.BlueCount, Is.Zero);
            bool allowed = false;
            terrain.QueueBuild(BlockWorld.Center(adjacentCell) - Vector3.up * .051f, Vector3.up, 2, 1, 0, false,
                result => { Assert.That(result.Added, Is.EqualTo(1)); allowed = true; });
            for (int i = 0; i < 1000 && !allowed; i++) { terrain.Tick(true, i / 60d, 1f / 60); yield return null; }
            Assert.That(allowed, Is.True); Assert.That(terrain.BlueCount, Is.EqualTo(1));
        }
        [Test] public void AllEightAuthorityCapsulesUseSnapshotsAndLeaveEmptyCapsuleCornersBuildable()
        {
            for (int i = 0; i < 8; i++) Player(new Vector3(-7 + i * 2, .04f, 0)); Capture();
            Assert.That((int)typeof(BlockTerrain).GetField("_safetyPlayerCount", Private).GetValue(terrain), Is.EqualTo(8));
            foreach (var player in players)
            {
                var state = player.Snapshot.Value; var shape = HeroBodyShape.For(state.HeroId);
                player.transform.position += Vector3.up * 20; // A render correction cannot move authority occupancy.
                Assert.That(Excluded(BlockWorld.CellAt(state.Position + Vector3.up * (shape.Height * .5f))), Is.True);
                Assert.That(Excluded(BlockWorld.CellAt(state.Position + new Vector3(shape.Radius + .12f, shape.Height * .5f, shape.Radius + .12f))), Is.False);
            }
        }
        [Test] public void TiltedPaperUsesItsLiveSilhouetteAndAllowsAnEmptySilhouetteHole()
        {
            var player = Player(new Vector3(0, .04f, 0), true); var body = player.SwimBody;
            body.Bind(PaperBodyTests.Profile("RifleGirl"));
            var state = player.Snapshot.Value; state.Swimming = state.CompactBody = true; state.Movement = MovementMode.WallInk;
            state.PaperPose = PaperPose.Wall; state.PaperCenter = new Vector3(0, 3, 0); state.PaperRotation = Quaternion.Euler(0, 25, 15);
            Place(player, state); body.ApplyCollision(state); Physics.SyncTransforms(); Capture();
            Assert.That(body.HitRects, Is.Not.Null); Assert.That(body.HitRects.Count, Is.GreaterThan(0));
            Assert.That(body.FlatHitActive, Is.True); Assert.That(body.HitVolume.sharedMesh.vertexCount, Is.GreaterThan(0));
            var interior = body.HitRects.OrderByDescending(rect => rect.width * rect.height).First().center;
            Assert.That(Excluded(BlockWorld.CellAt(state.PaperCenter + state.PaperRotation * new Vector3(interior.x, interior.y, 0))), Is.True,
                "The centre of a real animated silhouette rectangle must protect its occupied cell");
            var probe = Box("Paper cell comparison", Vector3.zero, Vector3.one * BlockWorld.CellSize, 8);
            var full = Box("Paper broad rectangle comparison", state.PaperCenter, new Vector3(body.Profile.Size.x, body.Profile.Size.y, body.Profile.Thickness), 8);
            var overlaps = new Collider[8]; bool occupied = false, hole = false; int broadCandidates = 0;
            for (float x = -.4f; x <= .4f && (!occupied || !hole); x += .1f)
            for (float y = -.8f; y <= .8f && (!occupied || !hole); y += .1f)
            {
                var cell = BlockWorld.CellAt(state.PaperCenter + state.PaperRotation * new Vector3(x, y, 0)); var center = BlockWorld.Center(cell);
                bool broad = Physics.ComputePenetration(probe, center, Quaternion.identity, full, state.PaperCenter, state.PaperRotation, out _, out _);
                if (!broad) continue;
                broadCandidates++;
                int count = Physics.OverlapBoxNonAlloc(center, Vector3.one * (BlockWorld.CellSize * .5f), overlaps,
                    Quaternion.identity, 1 << SwimBody.HitProxyLayer, QueryTriggerInteraction.Ignore);
                bool actual = false; for (int i = 0; i < count; i++) if (overlaps[i] == body.HitVolume) { actual = true; break; }
                if (actual) { Assert.That(Excluded(cell), Is.True); occupied = true; }
                else { Assert.That(Excluded(cell), Is.False, "Empty silhouette space is not an occupied player volume"); hole = true; }
            }
            Assert.That(broadCandidates, Is.GreaterThan(0), "The enabled reference query shapes must cover the rotated paper plane");
            Assert.That(occupied, Is.True); Assert.That(hole, Is.True, "The reference animated silhouette must provide an actual empty corner");
        }
        [UnityTest] public IEnumerator PublishedBlocksRecoverToNearestClearPositionWithoutEnteringAStaticWall()
        {
            var player = Player(new Vector3(0, .04f, 0)); Capture();
            Box("Static wall beside landing", new Vector3(-.75f, 1.5f, 0), new Vector3(1, 3, 4));
            var seed = new BlockWorld(arena.Dimensions);
            for (int x = -3; x <= 2; x++) for (int z = -3; z <= 2; z++) for (int y = 0; y < 18; y++)
                seed.Set(new Vector3Int(x, y, z), BlockCell.Create(2, true));
            Assert.That(terrain.QueueSeed(seed), Is.True);
            for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++) { terrain.Tick(true, tick / 60d, 1f / 60); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.BlueCount, Is.EqualTo(648));
            var recovered = player.Snapshot.Value;
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1)); Assert.That(terrain.SafetyRecoveryFailures, Is.Zero);
            Assert.That(StandingOverlaps(recovered), Is.False, "All map layers and the newly published block collider must be clear");
            Assert.That(Vector3.Distance(recovered.Position, new Vector3(0, .04f, 0)), Is.InRange(.5f, .8f), "A side exit is nearer than climbing above the 1.8m stack");
            Assert.That(recovered.Grounded, Is.False); Assert.That(recovered.SupportTerrainRevision, Is.Zero);
            Assert.That(recovered.Health, Is.EqualTo(100)); Assert.That(recovered.Ink, Is.EqualTo(43)); Assert.That(recovered.SpecialPoints, Is.EqualTo(7));
        }
        [Test] public void ASealedNeighbourhoodReturnsToTheLastValidatedSafePosition()
        {
            var player = Player(new Vector3(4, .04f, 0)); Capture(); Assert.That(CachedPositions, Is.EqualTo(1));
            var state = player.Snapshot.Value; state.Position = new Vector3(0, .04f, 0); Place(player, state);
            Box("Sealed unsafe volume", new Vector3(0, 2, 0), new Vector3(4, 4, 4)); Capture(); Recover();
            Assert.That(player.Snapshot.Value.Position, Is.EqualTo(new Vector3(4, .04f, 0))); Assert.That(StandingOverlaps(player.Snapshot.Value), Is.False);
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1)); Assert.That(player.Snapshot.Value.Health, Is.EqualTo(100));
        }
        [Test] public void MissingOrNewlyBlockedSafeHistoryFallsBackToTheProtectedSpawn()
        {
            var player = Player(new Vector3(4, .04f, 0)); Capture();
            Box("Sealed former safe volume", new Vector3(4, 2, 0), new Vector3(4, 4, 4));
            var state = player.Snapshot.Value; state.Position = new Vector3(0, .04f, 0); Place(player, state);
            Box("Sealed current unsafe volume", new Vector3(0, 2, 0), new Vector3(4, 4, 4)); Capture(); Recover();
            Assert.That(player.Snapshot.Value.Position, Is.EqualTo(arena.SpawnPoints[0].position));
            Assert.That(StandingOverlaps(player.Snapshot.Value), Is.False); Assert.That(terrain.SafetyRecoveryFailures, Is.Zero);
        }
        [TestCase("life")] [TestCase("hero")] [TestCase("team")] [TestCase("death")] [TestCase("disconnect")] [TestCase("round")]
        public void SafeHistoryCannotLeakAcrossPlayerOrRoundLifecycles(string boundary)
        {
            var player = Player(new Vector3(4, .04f, 0)); Capture(); Assert.That(CachedPositions, Is.EqualTo(1));
            var state = player.Snapshot.Value; state.Position = new Vector3(0, .04f, 0);
            if (boundary == "life") state.Revision++;
            if (boundary == "hero") state.HeroRevision++;
            if (boundary == "team") state.Team = 2;
            if (boundary == "death") { state.Health = 0; state.LifeState = PlayerLifeState.Dead; }
            Place(player, state); Box("Lifecycle unsafe volume", new Vector3(0, 2, 0), new Vector3(4, 4, 4));
            if (boundary == "disconnect") Spawned(player, false);
            if (boundary == "round") terrain.Clear(terrain.Round + 1); else Capture();
            Assert.That(CachedPositions, Is.Zero, "A former life, hero, team, departed role or round cannot supply rescue history");
        }
        [TestCase(0f, false)] [TestCase(.87f, false)] [TestCase(0f, true)] [TestCase(.87f, true)]
        public void PaperOnlyRecoveryMovesTheCurrentPoseWithoutResettingFormTrajectoryOrInput(float airOffset, bool compactOnly)
        {
            var player = Player(new Vector3(4, 1, 0));
            var before = AirPaper(player, airOffset, compactOnly);
            // The horizontal sheet extends beyond both compact and standing capsules.
            Box("Paper-only overlap", before.PaperCenter + Vector3.right * .42f, Vector3.one * .04f);
            var observations = new List<BlockTerrain.SafetyRecoveryObservation>();
            System.Action<BlockTerrain, PrototypePlayer, BlockTerrain.SafetyRecoveryObservation> observe = (world, actor, observation) =>
            { if (world == terrain && actor == player) observations.Add(observation); };
            BlockTerrain.SafetyRecoveryObserved += observe;
            try
            {
                Capture(); Recover();
                Assert.That(observations.Count, Is.EqualTo(1));
                var observation = observations[0];
                Assert.That(observation.CapsuleEmbedded, Is.False); Assert.That(observation.PaperEmbedded, Is.True);
                Assert.That(observation.Recovered, Is.True); Assert.That(observation.RecoveryCount, Is.EqualTo(1));
                Assert.That(observation.RawDistance, Is.EqualTo(.1f).Within(.00001));
                Assert.That(observation.HumanDistance, Is.EqualTo(observation.RawDistance).Within(.00001));
                var after = player.Snapshot.Value;
                AssertPreservedAirPaper(before, after);
                Assert.That(LiveCapsuleOverlaps(after), Is.False); Assert.That(LivePaperOverlaps(player, after), Is.False);
                Assert.That(player.GetComponent<CharacterController>().height, Is.EqualTo(HeroBodyShape.For(after.HeroId).CompactHeight));
                Assert.That(observation.Before.Revision, Is.EqualTo(observation.After.Revision));
                Assert.That(observation.Before.AcknowledgedInput, Is.EqualTo(observation.After.AcknowledgedInput));
                var changed = player.Snapshot.Value; changed.Position = Vector3.one * 100; player.Snapshot.Value = changed;
                Assert.That(observation.Before.Position, Is.EqualTo(before.Position));
                Assert.That(observation.After.Position, Is.EqualTo(after.Position));
            }
            finally { BlockTerrain.SafetyRecoveryObserved -= observe; }
        }

        [Test] public void RotatedAirPaperRescueChecksTheSheetAndKeepsItsFrame()
        {
            var player = Player(new Vector3(0, 3, 0), true); var body = player.SwimBody;
            body.Bind(PaperBodyTests.Profile("RifleGirl")); var before = AirPaper(player);
            before.PaperRotation = Quaternion.Euler(72, 31, 18); Place(player, before); body.ApplyCollision(before);
            var wing = body.HitRects.OrderByDescending(rect => rect.center.sqrMagnitude).First().center;
            Box("Rotated paper wing obstruction", before.PaperCenter + before.PaperRotation * new Vector3(wing.x, wing.y, 0), Vector3.one * .04f);
            Assert.That(LiveCapsuleOverlaps(before), Is.False); Assert.That(LivePaperOverlaps(player, before), Is.True);
            Capture(); Recover(); var after = player.Snapshot.Value;
            AssertPreservedAirPaper(before, after); Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1));
            Assert.That(Vector3.Distance(after.Position, before.Position), Is.InRange(.099f, .3f));
            Assert.That(LiveCapsuleOverlaps(after), Is.False); Assert.That(LivePaperOverlaps(player, after), Is.False);
        }

        [TestCase(.87f, false)] [TestCase(-.02f, false)] [TestCase(.87f, true)]
        public void EmbeddedAirCapsuleKeepsItsCurrentFormWhenTheWholePoseCanMoveClear(float airOffset, bool compactOnly)
        {
            var player = Player(new Vector3(0, 3, 0)); var before = AirPaper(player, airOffset, compactOnly);
            Box("Air movement capsule obstruction", before.Position + Vector3.up * .2f, Vector3.one * .08f);
            Assert.That(LiveCapsuleOverlaps(before), Is.True);
            Assert.That(LivePaperOverlaps(player, before), Is.False, "Exercise capsule-only embedding rather than the existing sheet-only branch");
            Capture(); Recover(); var after = player.Snapshot.Value;
            AssertPreservedAirPaper(before, after);
            Assert.That(after.Position, Is.Not.EqualTo(before.Position));
            Assert.That(LiveCapsuleOverlaps(after), Is.False); Assert.That(LivePaperOverlaps(player, after), Is.False);
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1)); Assert.That(terrain.SafetyRecoveryFailures, Is.Zero);
        }

        [Test] public void ARealAirPaperSilhouetteHoleDoesNotCauseARescue()
        {
            var player = Player(new Vector3(0, 3, 0), true); var body = player.SwimBody;
            body.Bind(PaperBodyTests.Profile("RifleGirl")); var before = AirPaper(player);
            before.PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 35);
            Place(player, before); body.ApplyCollision(before); Physics.SyncTransforms();
            Assert.That(body.HitRects, Is.Not.Null); Assert.That(body.HitRects.Count, Is.GreaterThan(0));
            var obstacle = Box("Air paper silhouette hole", before.PaperCenter, Vector3.one * .02f);
            bool found = false; Vector2 size = body.Profile.Size;
            for (float x = -size.x * .5f + .03f; x < size.x * .5f - .03f && !found; x += .05f)
            for (float y = -size.y * .5f + .03f; y < size.y * .5f - .03f && !found; y += .05f)
            {
                obstacle.transform.position = before.PaperCenter + before.PaperRotation * new Vector3(x, y, 0);
                Physics.SyncTransforms();
                found = !LiveCapsuleOverlaps(before) && !LivePaperOverlaps(player, before);
            }
            Assert.That(found, Is.True, "The real captured silhouette must contain empty space outside the compact movement capsule");
            Assert.That(Physics.CheckBox(before.PaperCenter, new Vector3(size.x * .5f, size.y * .5f, body.Profile.Thickness * .5f),
                before.PaperRotation, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True,
                "The obstacle is inside the full paper rectangle but outside its live silhouette");
            Capture(); Recover(); Assert.That(player.Snapshot.Value, Is.EqualTo(before)); Assert.That(terrain.SafetyRecoveries, Is.Zero);
        }

        [TestCase("ground")] [TestCase("wall")] [TestCase("mantle")] [TestCase("support")]
        public void CurrentPoseRescueDoesNotChangeAttachedOrVersionDependentRecovery(string boundary)
        {
            var player = Player(new Vector3(0, 3, 0)); var before = AirPaper(player, 0);
            if (boundary == "ground") { before.Grounded = true; before.Movement = MovementMode.GroundInk; before.PaperPose = PaperPose.Ground; }
            if (boundary == "wall") { before.Movement = MovementMode.WallInk; before.PaperPose = PaperPose.Wall; }
            if (boundary == "mantle") { before.Movement = MovementMode.Mantle; before.PaperPose = PaperPose.Mantle; }
            if (boundary == "support") before.SupportTerrainRevision = 5;
            Place(player, before);
            Box("Existing recovery branch paper wing", before.PaperCenter + Vector3.right * .42f, Vector3.one * .04f);
            Assert.That(LiveCapsuleOverlaps(before), Is.False); Assert.That(LivePaperOverlaps(player, before), Is.True);
            Capture(); Recover(); var after = player.Snapshot.Value;
            Assert.That(after.Swimming || after.CompactBody, Is.False); Assert.That(after.PaperPose, Is.EqualTo(PaperPose.None));
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1)); Assert.That(after.AcknowledgedInput, Is.EqualTo(before.AcknowledgedInput));
        }

        [TestCase("x+")] [TestCase("x-")] [TestCase("z+")] [TestCase("z-")] [TestCase("top")] [TestCase("bottom")]
        public void CurrentAirPaperRescueKeepsItsEntireGeometryInsideWorldBounds(string boundary)
        {
            Vector3 position = new(0, 3, 0), wing = Vector3.right * .42f;
            if (boundary == "x+" || boundary == "x-")
            {
                float sign = boundary == "x+" ? 1 : -1;
                position.x = sign * (arena.Dimensions.x * .5f - .36f); wing = Vector3.right * (.42f * sign);
            }
            if (boundary == "z+" || boundary == "z-")
            {
                float sign = boundary == "z+" ? 1 : -1;
                position.z = sign * (arena.Dimensions.y * .5f - .36f); wing = Vector3.forward * (.8f * sign);
            }
            if (boundary == "top") position.y = BlockWorld.Top - HeroBodyShape.For(1).CompactHeight - .02f;
            if (boundary == "bottom") position.y = terrain.World.Bottom + .02f;
            var player = Player(position); var before = AirPaper(player, 0);
            Box("Boundary paper wing obstruction", before.PaperCenter + wing, Vector3.one * .04f);
            Assert.That(LiveCapsuleOverlaps(before), Is.False); Assert.That(LivePaperOverlaps(player, before), Is.True);
            Capture(); Recover(); var after = player.Snapshot.Value;
            AssertPreservedAirPaper(before, after); Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1));
            Assert.That(LiveCapsuleOverlaps(after), Is.False); Assert.That(LivePaperOverlaps(player, after), Is.False);
            var shape = HeroBodyShape.For(after.HeroId);
            Assert.That(Mathf.Abs(after.Position.x) + shape.Radius, Is.LessThanOrEqualTo(arena.Dimensions.x * .5f));
            Assert.That(Mathf.Abs(after.Position.z) + shape.Radius, Is.LessThanOrEqualTo(arena.Dimensions.y * .5f));
            Assert.That(after.Position.y, Is.GreaterThanOrEqualTo(terrain.World.Bottom));
            Assert.That(after.Position.y + shape.CompactHeight, Is.LessThanOrEqualTo(BlockWorld.Top));
            Vector3 paperExtent = Abs(after.PaperRotation * Vector3.right) * .45f +
                Abs(after.PaperRotation * Vector3.up) * .9f + Abs(after.PaperRotation * Vector3.forward) * .02f;
            Assert.That(Mathf.Abs(after.PaperCenter.x) + paperExtent.x, Is.LessThanOrEqualTo(arena.Dimensions.x * .5f));
            Assert.That(Mathf.Abs(after.PaperCenter.z) + paperExtent.z, Is.LessThanOrEqualTo(arena.Dimensions.y * .5f));
            Assert.That(after.PaperCenter.y - paperExtent.y, Is.GreaterThanOrEqualTo(terrain.World.Bottom));
            Assert.That(after.PaperCenter.y + paperExtent.y, Is.LessThanOrEqualTo(BlockWorld.Top));
        }
        static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        [Test] public void CompactPaperRescueUnderALowCeilingDoesNotOverwriteStandingSafeHistory()
        {
            var player = Player(new Vector3(4, .04f, 0)); Capture(); Vector3 saved = CachedPosition(player);
            var relocated = player.Snapshot.Value; relocated.Position = new Vector3(0, 1, 0); Place(player, relocated);
            var before = AirPaper(player, 0);
            Box("Low ceiling over compact paper", new Vector3(0, 2, 0), new Vector3(4, .1f, 4));
            Box("Paper wing beneath low ceiling", before.PaperCenter + Vector3.right * .42f, Vector3.one * .04f);
            Assert.That(LiveCapsuleOverlaps(before), Is.False); Assert.That(StandingOverlaps(before), Is.True);
            Capture(); Recover(); var after = player.Snapshot.Value;
            AssertPreservedAirPaper(before, after); Assert.That(LiveCapsuleOverlaps(after), Is.False);
            Assert.That(LivePaperOverlaps(player, after), Is.False); Assert.That(StandingOverlaps(after), Is.True);
            Assert.That(CachedPosition(player), Is.EqualTo(saved), "A compact-only safe exit cannot replace standing-safe history");
        }

        [Test] public void PaperOnlyRescueWithoutAnyClearCurrentPoseUsesTheExistingStandingFallback()
        {
            var player = Player(new Vector3(0, 1, 0)); var before = AirPaper(player);
            foreach (float x in new[] { -.85f, -.42f, .42f, .85f })
                Box("Paper-only sealed wing fence", new Vector3(x, 2, 0), new Vector3(.04f, 8, 8));
            Assert.That(LiveCapsuleOverlaps(before), Is.False); Assert.That(LivePaperOverlaps(player, before), Is.True);
            Capture(); Recover(); var after = player.Snapshot.Value;
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1)); Assert.That(terrain.SafetyRecoveryFailures, Is.Zero);
            Assert.That(after.Position, Is.EqualTo(PlayerMotorSimulation.HumanPosition(before)));
            Assert.That(after.Swimming || after.CompactBody, Is.False); Assert.That(after.AirHumanOffset, Is.Zero);
            Assert.That(after.PaperPose, Is.EqualTo(PaperPose.None)); Assert.That(StandingOverlaps(after), Is.False);
            Assert.That(after.AcknowledgedInput, Is.EqualTo(before.AcknowledgedInput)); Assert.That(after.Revision, Is.EqualTo(before.Revision));
            Assert.That(after.Health, Is.EqualTo(before.Health)); Assert.That(after.Ink, Is.EqualTo(before.Ink)); Assert.That(after.SpecialPoints, Is.EqualTo(before.SpecialPoints));
        }

        [UnityTest] public IEnumerator RepeatedRealPublicationsAndHeldSwimKeepTheAirTrajectoryAndInputState()
        {
            const float dt = 1f / 60;
            var player = Player(new Vector3(0, 5, 0)); var before = AirPaper(player);
            var controller = player.GetComponent<CharacterController>(); var motor = new PlayerMotorSimulation(controller, arena);
            motor.Restore(before); double now = before.SimulatedAt;
            var seed = new BlockWorld(arena.Dimensions);
            var wingCell = BlockWorld.CellAt(before.PaperCenter + Vector3.right * .42f);
            seed.Set(wingCell, BlockCell.Create(2, true));
            var observations = new List<BlockTerrain.SafetyRecoveryObservation>();
            Action<BlockTerrain, PrototypePlayer, BlockTerrain.SafetyRecoveryObservation> observe = (world, actor, observation) =>
            { if (world == terrain && actor == player) observations.Add(observation); };
            BlockTerrain.SafetyRecoveryObserved += observe;
            try
            {
                for (int publication = 0; publication < 12; publication++)
                {
                    if (publication != 0)
                    {
                        var moving = player.Snapshot.Value; var previous = moving;
                        now += dt;
                        var input = new PlayerInputFrame { Swim = true, Move = new Vector2(0, .1f), Look = Vector2.zero,
                            JumpSequence = moving.ConsumedJump, Sequence = moving.AcknowledgedInput + 1, Revision = moving.Revision };
                        motor.Step(ref moving, input, dt, now, false); moving.SimulatedAt = now; moving.AcknowledgedInput = input.Sequence;
                        player.Snapshot.Value = moving;
                        Assert.That(moving.Swimming, Is.True); Assert.That(moving.Movement, Is.EqualTo(MovementMode.Air));
                        Assert.That(moving.AirHumanOffset, Is.EqualTo(before.AirHumanOffset));
                        Assert.That(moving.VerticalSpeed, Is.LessThan(0), "Held swim must continue the existing fall instead of reopening from rest");
                        Assert.That(PlayerMotorSimulation.HumanPosition(moving).y - PlayerMotorSimulation.HumanPosition(previous).y,
                            Is.EqualTo(moving.VerticalSpeed * dt).Within(.003f));
                    }
                    var immediatelyBeforePublication = player.Snapshot.Value;
                    seed.Set(new Vector3Int(80 + publication, 0, 80), BlockCell.Create(1, true));
                    uint revision = terrain.Revision + 1;
                    Assert.That(terrain.QueueSeed(seed), Is.True);
                    for (int tick = 0; tick < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); tick++)
                    { terrain.Tick(true, now, dt); yield return null; }
                    Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.Revision, Is.EqualTo(revision));
                    var after = player.Snapshot.Value; AssertPreservedAirPaper(immediatelyBeforePublication, after);
                    Assert.That(LiveCapsuleOverlaps(after), Is.False); Assert.That(LivePaperOverlaps(player, after), Is.False);
                    Assert.That(after.Swimming, Is.True); Assert.That(after.VerticalSpeed, Is.LessThan(0));
                    Assert.That(terrain.PinkCount, Is.EqualTo(publication + 1)); Assert.That(terrain.BlueCount, Is.EqualTo(1));
                }
                Assert.That(observations.Count, Is.GreaterThanOrEqualTo(1)); Assert.That(terrain.SafetyRecoveryFailures, Is.Zero);
                foreach (var observation in observations)
                {
                    Assert.That(observation.CapsuleEmbedded, Is.False); Assert.That(observation.PaperEmbedded, Is.True);
                    AssertPreservedAirPaper(observation.Before, observation.After);
                    Assert.That(observation.RawDistance, Is.EqualTo(observation.HumanDistance).Within(.00001f));
                }
            }
            finally { BlockTerrain.SafetyRecoveryObserved -= observe; }
        }

        [Test] public void AThrowingSafetyObserverCannotInterruptRecoveryOrTheNextObserver()
        {
            var player = Player(new Vector3(0, .04f, 0));
            Box("Observer-test embedded capsule", new Vector3(0, 1, 0), new Vector3(.8f, 2, .8f));
            int observations = 0;
            System.Action<BlockTerrain, PrototypePlayer, BlockTerrain.SafetyRecoveryObservation> throwing = (world, actor, observation) =>
            { if (world == terrain) throw new System.InvalidOperationException("safety observer test"); };
            System.Action<BlockTerrain, PrototypePlayer, BlockTerrain.SafetyRecoveryObservation> observe = (world, actor, observation) =>
            { if (world == terrain && actor == player) observations++; };
            BlockTerrain.SafetyRecoveryObserved += throwing; BlockTerrain.SafetyRecoveryObserved += observe;
            try
            {
                Capture();
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: safety observer test"));
                Recover();
                Assert.That(observations, Is.EqualTo(1));
                Assert.That(terrain.SafetyRecoveryObserverErrors, Is.EqualTo(1));
                Assert.That(terrain.SafetyRecoveries, Is.EqualTo(1)); Assert.That(terrain.SafetyRecoveryFailures, Is.Zero);
                Assert.That(StandingOverlaps(player.Snapshot.Value), Is.False);
            }
            finally { BlockTerrain.SafetyRecoveryObserved -= throwing; BlockTerrain.SafetyRecoveryObserved -= observe; }
        }

        [Test] public void RemoteTerrainCannotRecordOrRelocateAuthorityPlayerState()
        {
            var player = Player(new Vector3(0, .04f, 0)); Box("Remote unsafe volume", new Vector3(0, 2, 0), new Vector3(4, 4, 4));
            terrain.Tick(false, 1, 1f / 60); Recover();
            Assert.That(CachedPositions, Is.Zero); Assert.That(terrain.SafetyRecoveries, Is.Zero); Assert.That(player.Snapshot.Value.Position, Is.EqualTo(new Vector3(0, .04f, 0)));
        }
        IDictionary ZoneColumnCache => (IDictionary)typeof(BlockTerrain).GetField("_constructionZoneColumnCache", Private).GetValue(terrain);
        void EndZoneStage()
        {
            typeof(BlockTerrain).GetField("_stageConstructionZonesActive", Private).SetValue(terrain, false);
            typeof(BlockTerrain).GetField("_stageConstructionZoneCount", Private).SetValue(terrain, 0);
        }
        [TestCase(false)] [TestCase(true)]
        public void NegativeZoneColumnCacheKeepsHumanAndCompactActorChecksLive(bool compact)
        {
            var player = Player(new Vector3(.05f, .04f, .05f));
            if (compact) AirPaper(player, compactOnly: true);
            Call("CaptureSafetyPlayers"); Call("CaptureStageConstructionZones");
            var occupied = new Vector3Int(0, 1, 0); var empty = new Vector3Int(0, 250, 0);
            try
            {
                Assert.That(Excluded(empty), Is.False); Assert.That(Excluded(occupied), Is.True);
                Assert.That(ZoneColumnCache.Count, Is.EqualTo(1)); Assert.That((bool)ZoneColumnCache[Vector2Int.zero], Is.False);
            }
            finally { EndZoneStage(); }
            var moved = player.Snapshot.Value; moved.Position += Vector3.right * 10; moved.PaperCenter += Vector3.right * 10; Place(player, moved);
            Call("CaptureSafetyPlayers"); Call("CaptureStageConstructionZones");
            try
            {
                Assert.That(ZoneColumnCache.Count, Is.EqualTo(1), "Unchanged zone inputs retain only their negative SAT answer");
                Assert.That(Excluded(occupied), Is.False); Assert.That(player.Snapshot.Value, Is.EqualTo(moved));
            }
            finally { EndZoneStage(); }
        }
        [Test] public void NegativeZoneColumnCacheKeepsTheAnimatedPaperSilhouetteLive()
        {
            var player = Player(new Vector3(0, .04f, 0), true); var body = player.SwimBody;
            body.Bind(PaperBodyTests.Profile("RifleGirl"));
            var state = player.Snapshot.Value; state.Swimming = state.CompactBody = true; state.Movement = MovementMode.WallInk;
            state.PaperPose = PaperPose.Wall; state.PaperCenter = new Vector3(0, 3, 0); state.PaperRotation = Quaternion.Euler(0, 25, 15);
            Place(player, state); body.ApplyCollision(state); Physics.SyncTransforms(); Call("CaptureSafetyPlayers");
            Assert.That(body.HitRects, Is.Not.Null); Assert.That(body.HitRects.Count, Is.GreaterThan(0));
            var interior = body.HitRects.OrderByDescending(rect => rect.width * rect.height).First().center;
            var occupied = BlockWorld.CellAt(state.PaperCenter + state.PaperRotation * new Vector3(interior.x, interior.y, 0));
            var empty = new Vector3Int(occupied.x, 250, occupied.z); var key = new Vector2Int(occupied.x, occupied.z);
            Call("CaptureStageConstructionZones");
            try
            {
                Assert.That(Excluded(empty), Is.False); Assert.That(Excluded(occupied), Is.True);
                Assert.That((bool)ZoneColumnCache[key], Is.False, "The paper result must never become a column proof");
            }
            finally { EndZoneStage(); }
            state.PaperCenter += Vector3.right * 10; Place(player, state); body.ApplyCollision(state); Physics.SyncTransforms();
            Call("CaptureSafetyPlayers"); Call("CaptureStageConstructionZones");
            try { Assert.That(Excluded(occupied), Is.False); Assert.That(player.Snapshot.Value, Is.EqualTo(state)); }
            finally { EndZoneStage(); }
        }

        static IEnumerable<Vector3Int> HorizontalActorLegacy(BlockWorld world, Vector3 point, Vector3Int n, int count, float radius)
        {
            // Independent original ring stream; never calls the new footprint proof.
            var first = BlockWorld.CellAt(point + (Vector3)n * (BlockWorld.CellSize * .51f));
            var u = Vector3Int.up; var v = Vector3Int.RoundToInt(Vector3.Cross(n, u));
            int span = Mathf.CeilToInt(Mathf.Max(Mathf.Max(world.Dimensions.x, world.Dimensions.y), BlockWorld.Top - world.Bottom) / BlockWorld.CellSize);
            int extent = Mathf.CeilToInt(Mathf.Min(radius / BlockWorld.CellSize, span));
            for (int layer = 0; layer < Math.Min(span, Math.Max(1, count)); layer++) for (int ring = 0; ring <= extent; ring++)
            {
                if (ring == 0) { yield return first + n * layer; continue; }
                for (int x = -ring; x <= ring; x++) for (int side = -1; side <= 1; side += 2)
                    if ((x * x + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radius * radius + .00001f)
                        yield return first + n * layer + u * x + v * (ring * side);
                for (int y = -ring + 1; y < ring; y++) for (int side = -1; side <= 1; side += 2)
                    if ((y * y + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radius * radius + .00001f)
                        yield return first + n * layer + u * (ring * side) + v * y;
            }
        }
        static object TerrainCall(BlockTerrain target, string name, params object[] arguments)
            => typeof(BlockTerrain).GetMethod(name, Private).Invoke(target, arguments);
        static (BlockWorld world, bool done, int checks) ActorConstructionStage(BlockTerrain target, object command, int maxChecks)
        {
            TerrainCall(target, "CaptureSafetyPlayers");
            var commandType = command.GetType();
            var pairType = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            var results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType));
            var next = target.World.Clone();
            object[] args = { next, new HashSet<Vector3Int>(), results, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, maxChecks };
            var journal = typeof(BlockTerrain).GetField("_gravityJournal", Private);
            Assert.That(journal, Is.Not.Null);
            Assert.That(journal.GetValue(target), Is.Null, "Every fixture stage owns a fresh gravity journal");
            try
            {
                TerrainCall(target, "StageCommands", args);
                Assert.That((int)args[3], Is.LessThanOrEqualTo(maxChecks));
                Assert.That((bool)typeof(BlockTerrain).GetField("_stageConstructionZonesActive", Private).GetValue(target), Is.False);
                foreach (var pair in results)
                {
                    var result = (BlockMutationResult)pair.GetType().GetField("Item2").GetValue(pair);
                    var total = (BlockMutationResult)commandType.GetField("Total").GetValue(command);
                    total.Added += result.Added; total.RemovedEnemy += result.RemovedEnemy; total.RemovedFriendly += result.RemovedFriendly;
                    commandType.GetField("Total").SetValue(command, total);
                }
                typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(target, next);
                // The helper manually publishes its comparison draft. Match the
                // production World -> committed journal -> journal release lifecycle.
                TerrainCall(target, "CommitGravityJournal", journal.GetValue(target));
                return (next, (bool)commandType.GetField("Done").GetValue(command), (int)args[3]);
            }
            finally { journal.SetValue(target, null); }
        }
        [TestCase("human")] [TestCase("compact")] [TestCase("paper")]
        public void HorizontalMidLayerKeepsLiveActorAndPaperOccupancyEqualToTheOldFullStream(string mode)
        {
            var zone = Zone(new Vector3(-.35f, 1, .15f), new Vector3(.5f, 2, .05f), Quaternion.identity, Vector3.one);
            var player = Player(new Vector3(.05f, 1, .15f), mode == "paper");
            if (mode != "human") AirPaper(player, compactOnly: mode == "compact");
            var playerBefore = player.Snapshot.Value;
            var old = new GameObject("Independent horizontal actor full-stream").AddComponent<BlockTerrain>(); old.Initialize(arena);
            try
            {
                var point = new Vector3(.05f, 1.05f, .05f); var n = new Vector3Int(0, 0, 1); int oldCallbacks = 0, newCallbacks = 0;
                BlockMutationResult oldDelivered = default, newDelivered = default;
                old.QueueBuild(point, n, 1, 18, .3f, false, result => { oldCallbacks++; oldDelivered = result; });
                terrain.QueueBuild(point, n, 1, 18, .3f, false, result => { newCallbacks++; newDelivered = result; });
                object CommandFor(BlockTerrain target) => ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(target)).Cast<object>().Single();
                var oldCommand = CommandFor(old); var newCommand = CommandFor(terrain);
                oldCommand.GetType().GetField("Cells").SetValue(oldCommand, HorizontalActorLegacy(old.World, point, n, 18, .3f).GetEnumerator());
                var oldFirst = ActorConstructionStage(old, oldCommand, 1); var newFirst = ActorConstructionStage(terrain, newCommand, 1);
                CollectionAssert.AreEqual(oldFirst.world.EncodeSnapshot(), newFirst.world.EncodeSnapshot());
                Assert.That(terrain.BuildOccupiedByPlayer, Is.GreaterThan(0), "The first unprotected candidate is actually checked against the actor");
                Assert.That(oldCallbacks + newCallbacks, Is.Zero);
                // Neither actor result nor a negative zone proof may survive into this stage.
                zone.enabled = false;
                var moved = player.Snapshot.Value; moved.Position = new Vector3(.05f, 1.65f, .15f);
                moved.PaperCenter = moved.Position + Vector3.up * .03f; moved.PaperRotation = Quaternion.Euler(0, 20, 35);
                Place(player, moved); player.SwimBody?.ApplyCollision(moved);
                for (int stage = 0; stage < 512 && !(bool)newCommand.GetType().GetField("Done").GetValue(newCommand); stage++)
                {
                    var previous = ActorConstructionStage(old, oldCommand, 1); var actual = ActorConstructionStage(terrain, newCommand, 1);
                    CollectionAssert.AreEqual(previous.world.EncodeSnapshot(), actual.world.EncodeSnapshot(), "Every construction stage retains the old live occupancy state");
                    Assert.That(actual.done, Is.EqualTo(previous.done)); Assert.That(actual.checks, Is.EqualTo(previous.checks));
                }
                Assert.That((bool)newCommand.GetType().GetField("Done").GetValue(newCommand), Is.True);
                var oldTotal = (BlockMutationResult)oldCommand.GetType().GetField("Total").GetValue(oldCommand);
                var newTotal = (BlockMutationResult)newCommand.GetType().GetField("Total").GetValue(newCommand);
                Assert.That(newTotal.Added, Is.EqualTo(oldTotal.Added)); Assert.That(newTotal.Added, Is.EqualTo(18));
                TerrainCall(old, "Complete", oldCommand, oldTotal); TerrainCall(old, "Complete", oldCommand, oldTotal);
                TerrainCall(terrain, "Complete", newCommand, newTotal); TerrainCall(terrain, "Complete", newCommand, newTotal);
                Assert.That(oldCallbacks, Is.EqualTo(1)); Assert.That(newCallbacks, Is.EqualTo(1));
                Assert.That(newDelivered.Added, Is.EqualTo(oldDelivered.Added)); Assert.That(player.Snapshot.Value, Is.EqualTo(moved), "Staging cannot mutate the actor snapshot");
            }
            finally { Object.DestroyImmediate(old.gameObject); Place(player, playerBefore); }
        }

    }
}
#endif
