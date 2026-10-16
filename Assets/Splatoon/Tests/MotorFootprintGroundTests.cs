#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public sealed class MotorFootprintGroundTests
    {
        const float Dt = 1f / 60;
        BlockTerrain terrain;
        CharacterController controller;
        MotorGroundContactRecorder recorder;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        double now;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> events = new();
        readonly List<MotorGroundContactRecorder.ContactObservation> contacts = new();
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load();
            terrain = new GameObject("Complete footprint fixture world").AddComponent<BlockTerrain>(); terrain.Initialize(null);
            var root = new GameObject("Actual footprint motor") { layer = 8 };
            controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f; controller.center = Vector3.up * .9f; controller.radius = .35f;
            controller.skinWidth = .03f; controller.stepOffset = .3f;
            recorder = root.AddComponent<MotorGroundContactRecorder>(); motor = new PlayerMotorSimulation(controller);
            PlayerMotorSimulation.MotorBranchObserved += Observe;
        }
        [TearDown] public void Cleanup()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe; PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Observe(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
        { if (source == motor) events.Add(observation); }
        IEnumerator Publish(BlockWorld world)
        {
            uint revision = terrain.Revision + 1;
            world.Seal(revision, world.Chunks.Keys.Concat(terrain.World.Chunks.Keys).Distinct().ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); tick++)
            { terrain.Tick(false, now, Dt); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }
        static BlockWorld World(params (Vector3Int cell, byte owner)[] cells)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            foreach (var entry in cells) world.Set(entry.cell, BlockCell.Create(entry.owner, true));
            return world;
        }
        void ResetAir(Vector3 position, uint sequence, float verticalSpeed = -1.5f)
        {
            events.Clear(); contacts.Clear(); now = 10;
            state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1,
                LifeState = PlayerLifeState.Alive, Health = 100, Ink = 43, Position = position, Movement = MovementMode.Air,
                Swimming = true, Grounded = false, PaperPose = PaperPose.Air, AirHumanOffset = .87f,
                PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 0), SwimSource = SwimSurface.Neutral,
                AirSwimSource = SwimSurface.Neutral, SwimWasHeld = true, VerticalSpeed = verticalSpeed,
                Velocity = Vector3.up * verticalSpeed, GroundNormal = Vector3.up, SimulatedAt = now };
            state.PaperCenter = position + Vector3.up * .03f;
            input = new PlayerInputFrame { Swim = true, Move = Vector2.zero, Look = Vector2.zero,
                Sequence = sequence, Revision = 1, HeroRevision = 1 };
            motor.Restore(state); Physics.SyncTransforms();
        }
        void Tick()
        {
            now += Dt; input.Sequence++; input.Tick++; state.SimulatedAt = now; state.SimulationTick++;
            recorder.Begin(input.Sequence); motor.Step(ref state, input, Dt, now, false);
            contacts.AddRange(recorder.Contacts); state.AcknowledgedInput = input.Sequence;
        }
        static bool LegacyFive(Vector3 feet, float radius, float slope)
        {
            float minimum = Mathf.Cos(slope * Mathf.Deg2Rad);
            foreach (var offset in new[] { Vector3.zero, Vector3.right * (radius * .8f), Vector3.left * (radius * .8f),
                         Vector3.forward * (radius * .8f), Vector3.back * (radius * .8f) })
                if (Physics.Raycast(feet + offset + Vector3.up * .08f, Vector3.down, out var hit, .16f,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) && hit.normal.y >= minimum) return true;
            return false;
        }
        static BoxCollider Box(Vector3 center, Vector3 size)
        {
            var box = new GameObject("Neutral geometry footprint candidate").AddComponent<BoxCollider>();
            box.transform.position = center; box.size = size; return box;
        }
        [Serializable] sealed class Row
        {
            public string branch;
            public ulong invocation;
            public uint sequence, terrainRevision, supportRevision;
            public bool succeeded, controllerGrounded, afterGrounded;
            public int collisions;
            public Vector3 beforePosition, afterPosition, requestedMove, point, normal;
            public Vector3Int cell;
            public byte owner;
            public float beforeOffset, afterOffset, rebase, beforeVerticalSpeed, afterVerticalSpeed;
        }
        [Serializable] sealed class ActualContact
        { public uint sequence; public Vector3 point, normal; public bool block; }
        [Serializable] sealed class Evidence
        {
            public string label, scope = "real CharacterController.Move and published collision fixture; no Player gate replacement";
            public List<Row> branches = new();
            public List<ActualContact> actualContacts = new();
        }
        void Save(string label)
        {
            var report = new Evidence { label = label };
            foreach (var o in events) report.branches.Add(new Row { branch = o.Branch.ToString(), invocation = o.Invocation,
                sequence = o.Input.Sequence, terrainRevision = o.TerrainRevision, supportRevision = o.After.SupportTerrainRevision,
                succeeded = o.ProbeSucceeded, controllerGrounded = o.ControllerGrounded, afterGrounded = o.After.Grounded,
                collisions = (int)o.Collisions, beforePosition = o.Before.Position, afterPosition = o.After.Position, requestedMove = o.RequestedMove,
                point = o.Contact.Point, normal = o.Contact.Normal, cell = o.Contact.Cell, owner = o.Contact.Owner,
                beforeOffset = o.Before.AirHumanOffset, afterOffset = o.After.AirHumanOffset, rebase = o.RebaseHeight,
                beforeVerticalSpeed = o.Before.VerticalSpeed, afterVerticalSpeed = o.After.VerticalSpeed });
            foreach (var c in contacts) report.actualContacts.Add(new ActualContact { sequence = c.InputSequence, point = c.Point,
                normal = c.Normal, block = c.BlockCollider });
            string directory = Path.GetFullPath("Reports/BlockTerrain/PredictionAudit/PhysXFootprint"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + label + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true)); TestContext.Progress.WriteLine(path);
        }
        [UnityTest] public IEnumerator DiagonalVoxelRealLandingSucceedsDespiteLegacyFiveRayMiss()
        {
            var cell = new Vector3Int(1, 10, 1); yield return Publish(World((cell, (byte)1)));
            ResetAir(new Vector3(.08f, 1.3f, .08f), 1000);
            for (int tick = 0; tick < 90 && !state.Grounded; tick++) Tick();
            var landings = events.Where(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.GroundEvaluation && o.ProbeSucceeded).ToArray();
            for (int tick = 0; tick < 12; tick++) Tick(); Save("diagonal-real-landing");
            Assert.That(landings.Length, Is.GreaterThan(0));
            Assert.That(LegacyFive(landings[0].Before.Position, controller.radius, controller.slopeLimit), Is.False);
            Assert.That(contacts.Any(c => c.BlockCollider && Mathf.Abs(c.Point.y - 1.1f) < .03f && c.Normal.y > .9f), Is.True);
            Assert.That(landings[0].Contact.Cell, Is.EqualTo(cell)); Assert.That(landings[0].Contact.Normal.y, Is.GreaterThan(.99f));
            Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.True); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(state.SupportCell, Is.EqualTo(cell)); Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision));
            Assert.That(events.Any(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry && o.Invocation > landings[0].Invocation && o.RebaseHeight > .8f), Is.False);
        }
        [UnityTest] public IEnumerator MultipleUnsampledAnglesResolveCurrentTopFaces()
        {
            foreach (var cell in new[] { new Vector3Int(1, 10, 1), new Vector3Int(-2, 10, 1),
                         new Vector3Int(-2, 10, -2), new Vector3Int(1, 10, -2), new Vector3Int(16, 10, 16) })
            {
                yield return Publish(World((cell, (byte)1)));
                float x = cell.x >= 0 ? cell.x * .1f - .02f : (cell.x + 1) * .1f + .02f;
                float z = cell.z >= 0 ? cell.z * .1f - .02f : (cell.z + 1) * .1f + .02f;
                Vector3 feet = new Vector3(x, 1.13f, z);
                Assert.That(LegacyFive(feet, .35f, 45), Is.False);
                Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var contact, 45, .35f), Is.True);
                Assert.That(contact.IsBlock && contact.Stable, Is.True); Assert.That(contact.Cell, Is.EqualTo(cell));
                Assert.That(contact.Revision, Is.EqualTo(terrain.World.ChunkRevision(BlockWorld.ChunkAt(cell))));
            }
        }
        [UnityTest] public IEnumerator CentreEnemyAndCentreNeutralKeepPriorityOverFriendlyNeighbour()
        {
            var enemy = new Vector3Int(1, 10, 1); var friendly = new Vector3Int(2, 10, 2);
            yield return Publish(World((enemy, (byte)2), (friendly, (byte)1)));
            Assert.That(PlayerMotorSimulation.TryGetGround(new Vector3(.15f, 1.13f, .15f), out var enemyContact), Is.True);
            Assert.That(enemyContact.Owner, Is.EqualTo(2)); Assert.That(enemyContact.Cell, Is.EqualTo(enemy));
            yield return Publish(World((new Vector3Int(1, 10, 1), (byte)1)));
            Box(new Vector3(0, 1.05f, 0), Vector3.one * .1f); Physics.SyncTransforms();
            Assert.That(PlayerMotorSimulation.TryGetGround(new Vector3(0, 1.13f, 0), out var neutral), Is.True);
            Assert.That(neutral.IsBlock, Is.False); Assert.That(neutral.Owner, Is.Zero);
        }
        [UnityTest] public IEnumerator FallbackChoosesCloserEnemyGeometryAndCannotGrantFriendlySwim()
        {
            var enemy = new Vector3Int(1, 10, 1); var friendly = new Vector3Int(-1, 10, -1);
            yield return Publish(World((enemy, (byte)2), (friendly, (byte)1)));
            var feet = new Vector3(.08f, 1.13f, .08f);
            Assert.That(LegacyFive(feet, .35f, 45), Is.False);
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var contact), Is.True);
            Assert.That(contact.Owner, Is.EqualTo(2)); Assert.That(contact.Cell, Is.EqualTo(enemy));
            ResetAir(new Vector3(.08f, 1.3f, .08f), 2000);
            for (int tick = 0; tick < 90 && !state.Grounded; tick++) Tick(); Save("enemy-physical-priority");
            Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.False);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Human)); Assert.That(state.AirHumanOffset, Is.Zero);
        }
        [UnityTest] public IEnumerator RisingAirPaperDoesNotConsumeOffsetWhileSupportIsNearby()
        {
            yield return Publish(World((new Vector3Int(1, 10, 1), (byte)1)));
            ResetAir(new Vector3(.08f, 1.13f, .08f), 3000, 5);
            for (int tick = 0; tick < 4; tick++) Tick(); Save("rising-air-offset");
            Assert.That(events.Any(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.GroundEvaluation), Is.False);
            Assert.That(state.Grounded, Is.False); Assert.That(state.VerticalSpeed, Is.GreaterThan(0));
            Assert.That(state.AirHumanOffset, Is.EqualTo(.87f)); Assert.That(state.SupportTerrainRevision, Is.Zero);
        }
        [UnityTest] public IEnumerator RealJumpFromDiagonalSupportDoesNotRelandDuringPositiveMotion()
        {
            yield return Publish(World((new Vector3Int(1, 10, 1), (byte)1)));
            ResetAir(new Vector3(.08f, 1.3f, .08f), 4000);
            for (int tick = 0; tick < 90 && !state.Grounded; tick++) Tick();
            Assert.That(state.Grounded, Is.True, "Jump eligibility must come from actual landing, not assigned Grounded");
            events.Clear(); contacts.Clear(); input.JumpSequence = 1; Tick(); Save("real-positive-jump");
            Assert.That(events.Any(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.DestinationMove && o.RequestedMove.y > 0), Is.True);
            Assert.That(events.Any(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.GroundResolved && o.Before.VerticalSpeed > 0), Is.False);
            Assert.That(state.Grounded, Is.False); Assert.That(state.VerticalSpeed, Is.GreaterThan(0));
        }
        [UnityTest] public IEnumerator RemovingPublishedSupportInvalidatesQueriesAndDoesNotReuseOldHit()
        {
            var cell = new Vector3Int(1, 10, 1); yield return Publish(World((cell, (byte)1)));
            var feet = new Vector3(.08f, 1.13f, .08f);
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var old), Is.True);
            Assert.That(old.Stable, Is.True); yield return Publish(World());
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out _), Is.False);
            ResetAir(feet, 5000); for (int tick = 0; tick < 12; tick++) Tick(); Save("published-support-removed");
            Assert.That(state.Grounded, Is.False); Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.AirHumanOffset, Is.EqualTo(.87f));
        }
        [UnityTest] public IEnumerator WallSidesAndHorizontalOrVerticalGapsCannotBecomeStableGround()
        {
            var wall = World(); for (int y = 0; y < 22; y++) wall.Set(new Vector3Int(1, y, 1), BlockCell.Create(1, true));
            yield return Publish(wall);
            Assert.That(PlayerMotorSimulation.TryGetGround(new Vector3(.08f, 1.13f, .08f), out _), Is.False);
            yield return Publish(World((new Vector3Int(5, 10, 5), (byte)1)));
            Assert.That(PlayerMotorSimulation.TryGetGround(new Vector3(0, 1.13f, 0), out _), Is.False);
            yield return Publish(World((new Vector3Int(1, 10, 1), (byte)1)));
            Assert.That(PlayerMotorSimulation.TryGetGround(new Vector3(.08f, 1.32f, .08f), out _), Is.False);
        }
        [UnityTest] public IEnumerator CompleteMultiHitSetGrowsInsteadOfDiscardingNeutralTopCandidates()
        {
            yield return Publish(World());
            int beforeCapacity = PlayerMotorSimulation.GroundFootprintBufferCapacity;
            int beforeGrowth = PlayerMotorSimulation.GroundFootprintBufferGrowths;
            int count = Mathf.Max(65, beforeCapacity + 1); float spacing = .10f / (count + 1), highest = 0;
            for (int i = 0; i < count; i++)
            {
                float top = 1.035f + i * spacing; highest = top;
                Box(new Vector3(.15f, top - spacing * .1f, .15f), new Vector3(.1f, spacing * .2f, .1f));
            }
            Physics.SyncTransforms(); var feet = new Vector3(.08f, 1.1f, .08f);
            Assert.That(LegacyFive(feet, .35f, 45), Is.False);
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var contact), Is.True);
            Assert.That(contact.IsBlock, Is.False); Assert.That(contact.Owner, Is.Zero);
            Assert.That(contact.Point.y, Is.EqualTo(highest).Within(.001f));
            Assert.That(PlayerMotorSimulation.LastGroundFootprintHitCount, Is.GreaterThanOrEqualTo(count));
            Assert.That(PlayerMotorSimulation.GroundFootprintBufferGrowths, Is.GreaterThan(beforeGrowth));
            Assert.That(PlayerMotorSimulation.GroundFootprintBufferCapacity, Is.GreaterThan(count));
        }
        [UnityTest] public IEnumerator InitialOverlappedChunkCannotHideItsRealDiagonalFloor()
        {
            var floorCell = new Vector3Int(1, 4, 1); var ceilingCell = new Vector3Int(1, 13, 1);
            yield return Publish(World((floorCell, (byte)1), (ceilingCell, (byte)1)));
            var feet = new Vector3(.08f, .53f, .08f); var origin = feet + Vector3.up * (.35f + .08f);
            Assert.That(Physics.Raycast(new Vector3(.15f, .55f, .15f), Vector3.down, out var floorFace, .1f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(Physics.Raycast(new Vector3(.15f, 1.5f, .15f), Vector3.down, out var ceilingFace, .3f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(floorFace.collider, Is.EqualTo(ceilingFace.collider), "The ceiling and support must share the actually published collider");
            Assert.That(LegacyFive(feet, .35f, 45), Is.False);
            bool overlaps = Physics.CheckSphere(origin, .35f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            Assert.That(overlaps, Is.True, "The diagnostic must reach a real initial-overlap geometry");
            var rawHits = Physics.SphereCastAll(origin, .35f, Vector3.down, .16f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            bool rawFloor = rawHits.Any(hit => hit.collider == floorFace.collider && hit.distance > 0 && Mathf.Abs(hit.point.y - .5f) < .003f);
            TestContext.Progress.WriteLine($"Initial overlap={overlaps}, raw hits={rawHits.Length}, raw positive floor hit={rawFloor}");
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var contact), Is.True);
            Assert.That(contact.IsBlock && contact.Stable, Is.True); Assert.That(contact.Cell, Is.EqualTo(floorCell));
            ResetAir(new Vector3(.08f, .55f, .08f), 7000);
            for (int tick = 0; tick < 40 && !state.Grounded; tick++) Tick(); Save("initial-overlapped-chunk-real-landing");
            Assert.That(contacts.Any(c => c.BlockCollider && c.Normal.y > .9f && Mathf.Abs(c.Point.y - .5f) < .03f), Is.True);
            Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.True); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(state.SupportCell, Is.EqualTo(floorCell)); Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision));
        }
        [UnityTest] public IEnumerator OverlappedEnemyChunkBeatsFartherVisibleFriendlyChunkByGeometry()
        {
            var enemy = new Vector3Int(1, 4, 1); var ceiling = new Vector3Int(1, 13, 1); var friendly = new Vector3Int(-1, 4, -1);
            yield return Publish(World((enemy, (byte)2), (ceiling, (byte)2), (friendly, (byte)1)));
            var feet = new Vector3(.08f, .53f, .08f);
            Assert.That(LegacyFive(feet, .35f, 45), Is.False);
            Assert.That(Physics.CheckSphere(feet + Vector3.up * (.35f + .08f), .35f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var contact), Is.True);
            Assert.That(contact.Owner, Is.EqualTo(2)); Assert.That(contact.Cell, Is.EqualTo(enemy));
            Assert.That(BlockWorld.ChunkAt(enemy), Is.Not.EqualTo(BlockWorld.ChunkAt(friendly)));
        }

        [UnityTest] public IEnumerator VerticalSideFirstHitDoesNotHideAnotherColliderTop()
        {
            yield return Publish(World((new Vector3Int(1, 10, 1), (byte)1)));
            // This edge is reached before the block top, but its radial contact
            // is too lateral to count as a slope-valid lower-cap contact.
            var side = Box(new Vector3(-.26f, 1.13f, .08f), new Vector3(.02f, .6f, .4f)); Physics.SyncTransforms();
            var feet = new Vector3(.08f, 1.13f, .08f);
            Assert.That(LegacyFive(feet, .35f, 45), Is.False);
            Assert.That(Physics.SphereCast(feet + Vector3.up * (.35f + .08f), .35f, Vector3.down,
                out var first, .16f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(first.collider, Is.EqualTo(side), "The geometry must actually demonstrate an invalid first candidate");
            Assert.That(first.normal.y, Is.LessThan(Mathf.Cos(45 * Mathf.Deg2Rad)));
            Assert.That(PlayerMotorSimulation.TryGetGround(feet, out var contact), Is.True);
            Assert.That(contact.IsBlock && contact.Stable, Is.True); Assert.That(contact.Cell, Is.EqualTo(new Vector3Int(1, 10, 1)));
        }
    }
}
#endif
