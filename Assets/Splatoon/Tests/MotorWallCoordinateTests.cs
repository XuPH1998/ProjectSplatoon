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
    // Normal motor Step, real CharacterController.Move and published wall colliders.
    // No reflected TryAttach, mocked probe, or manual grounded assignment is used.
    public sealed class MotorWallCoordinateTests
    {
        const float Dt = 1f / 60;
        BlockTerrain terrain;
        CharacterController controller;
        MotorGroundContactRecorder recorder;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> events = new();

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load();
            terrain = new GameObject("Published wall coordinate fixture").AddComponent<BlockTerrain>(); terrain.Initialize(null);
            var root = new GameObject("Real airborne wall attachment motor") { layer = 8 };
            controller = root.AddComponent<CharacterController>();
            var shape = HeroBodyShape.For(1); shape.Apply(controller, true);
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
        static BlockWorld TallFriendlyWall()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -8; x <= 8; x++) for (int y = 0; y < 50; y++)
                world.Set(new Vector3Int(x, y, 10), BlockCell.Create(1, true));
            return world;
        }
        IEnumerator Publish(BlockWorld world)
        {
            uint revision = terrain.Revision + 1;
            world.Seal(revision, world.Chunks.Keys.Concat(terrain.World.Chunks.Keys).Distinct().ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); tick++)
            { terrain.Tick(false, 10, Dt); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }
        void ResetContinuousAir(float z = .6f)
        {
            events.Clear();
            state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1,
                Health = 100, Ink = 43, LifeState = PlayerLifeState.Alive,
                Position = new Vector3(.05f, 2.87f, z), Movement = MovementMode.Air,
                Swimming = true, CompactBody = false, Grounded = false, PaperPose = PaperPose.Air,
                AirHumanOffset = .87f, CameraRebaseOffset = 0, VerticalSpeed = -.1f,
                Velocity = Vector3.down * .1f, GroundNormal = Vector3.up,
                PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 0),
                SwimSource = SwimSurface.Friendly, AirSwimSource = SwimSurface.Friendly, SwimWasHeld = true,
                SimulatedAt = 10, SimulationTick = 1 };
            state.PaperCenter = state.Position + Vector3.up * .03f;
            input = new PlayerInputFrame { Swim = true, Fire = false, Move = Vector2.up, Look = Vector2.zero,
                Sequence = 100, Tick = 100, Revision = 1, HeroRevision = 1, JumpSequence = state.ConsumedJump };
            motor.Restore(state); Physics.SyncTransforms();
            Assert.That(controller.height, Is.EqualTo(HeroBodyShape.For(1).CompactHeight));
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out _), Is.False,
                "The attachment must start from an actual unsupported air location");
        }
        bool FreshWall(out PlayerMotorSimulation.TraversalContact contact)
        {
            var shape = HeroBodyShape.For(state.HeroId);
            return PlayerMotorSimulation.Query(controller.transform.position + Vector3.up * (shape.CompactHeight * .5f),
                Vector3.forward, GameplayConfig.GetHero(state.HeroId).WallProbeDistance, out contact);
        }
        void Step()
        {
            input.Sequence++; input.Tick++; state.SimulationTick++; state.SimulatedAt += Dt;
            recorder.Begin(input.Sequence); motor.Step(ref state, input, Dt, state.SimulatedAt, false);
        }
        static void AssertFriendlyWall(PlayerMotorSimulation.TraversalContact contact, uint revision)
        {
            Assert.That(contact.IsBlock && contact.Stable && contact.Climbable, Is.True);
            Assert.That(contact.Owner, Is.EqualTo(1)); Assert.That(contact.Normal.z, Is.LessThan(-.99f));
            Assert.That(contact.Revision, Is.EqualTo(revision));
        }
        void AssertActualAttachment()
        {
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(FreshWall(out var contact), Is.True); AssertFriendlyWall(contact, terrain.Revision);
            Assert.That(state.SupportCell, Is.EqualTo(contact.Cell)); Assert.That(state.SupportTerrainRevision, Is.EqualTo(contact.Revision));
            Assert.That(recorder.Contacts.Any(c => c.BlockCollider && c.Normal.z < -.9f && Mathf.Abs(c.Point.z - 1) < .015f), Is.True,
                "The normal attachment snap must produce a real native wall collision callback");
            Assert.That(controller.transform.position, Is.EqualTo(state.Position));
        }
        [Serializable] sealed class NativeContact
        { public Vector3 point, normal, moveDirection; public float moveLength; public int colliderId; public bool block; }
        [Serializable] sealed class SnapshotSample
        {
            public string movement, paperPose, swimSource, airSwimSource;
            public Vector3 position, velocity, planarVelocity, paperCenter, wallNormal, wallPoint;
            public Quaternion paperRotation;
            public Vector3Int supportCell;
            public uint supportTerrainRevision, life, simulationTick;
            public float airHumanOffset, cameraRebaseOffset, verticalSpeed;
            public bool grounded, swimming, compactBody;
            public SnapshotSample(PlayerSnapshot s)
            {
                movement = s.Movement.ToString(); paperPose = s.PaperPose.ToString(); swimSource = s.SwimSource.ToString(); airSwimSource = s.AirSwimSource.ToString();
                position = s.Position; velocity = s.Velocity; planarVelocity = s.PlanarVelocity; paperCenter = s.PaperCenter;
                wallNormal = s.WallNormal; wallPoint = s.WallPoint; paperRotation = s.PaperRotation; supportCell = s.SupportCell;
                supportTerrainRevision = s.SupportTerrainRevision; life = s.Revision; simulationTick = s.SimulationTick;
                airHumanOffset = s.AirHumanOffset; cameraRebaseOffset = s.CameraRebaseOffset; verticalSpeed = s.VerticalSpeed;
                grounded = s.Grounded; swimming = s.Swimming; compactBody = s.CompactBody;
            }
        }
        [Serializable] sealed class Branch
        {
            public string branch; public ulong invocation; public SnapshotSample before, after;
            public Vector3 requestedMove, controllerPosition; public float rebase; public bool controllerGrounded;
        }
        [Serializable] sealed class Evidence
        {
            public string label, scope = "Real normal Step and published wall collider diagnostic; no Player gate replacement";
            public SnapshotSample before, after;
            public Vector3 rootBefore, rootAfter, humanBefore, humanAfter;
            public float rootYDelta, humanYDelta, paperYDelta;
            public bool nativeGroundedBefore, nativeGroundedAfter, freshWall;
            public Vector3Int freshCell; public uint freshRevision;
            public List<NativeContact> contacts = new(); public List<Branch> branches = new();
        }
        void Save(string label, PlayerSnapshot before, Vector3 rootBefore, bool nativeGroundedBefore)
        {
            var evidence = new Evidence { label = label, before = new SnapshotSample(before), after = new SnapshotSample(state), rootBefore = rootBefore,
                rootAfter = controller.transform.position, humanBefore = PlayerMotorSimulation.HumanPosition(before),
                humanAfter = PlayerMotorSimulation.HumanPosition(state), nativeGroundedBefore = nativeGroundedBefore,
                nativeGroundedAfter = controller.isGrounded, rootYDelta = state.Position.y - before.Position.y,
                humanYDelta = PlayerMotorSimulation.HumanPosition(state).y - PlayerMotorSimulation.HumanPosition(before).y,
                paperYDelta = state.PaperCenter.y - before.PaperCenter.y };
            evidence.freshWall = FreshWall(out var wall); evidence.freshCell = wall.Cell; evidence.freshRevision = wall.Revision;
            foreach (var contact in recorder.Contacts) evidence.contacts.Add(new NativeContact { point = contact.Point,
                normal = contact.Normal, moveDirection = contact.MoveDirection, moveLength = contact.MoveLength,
                colliderId = contact.ColliderId, block = contact.BlockCollider });
            foreach (var observation in events) evidence.branches.Add(new Branch { branch = observation.Branch.ToString(),
                invocation = observation.Invocation, before = new SnapshotSample(observation.Before), after = new SnapshotSample(observation.After),
                requestedMove = observation.RequestedMove, controllerPosition = observation.ControllerPosition,
                rebase = observation.RebaseHeight, controllerGrounded = observation.ControllerGrounded });
            string folder = Path.GetFullPath("Reports/BlockTerrain/PredictionAudit/PhysXWallCoordinates"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + label + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(evidence, true)); TestContext.Progress.WriteLine(path);
        }
        [UnityTest] public IEnumerator NormalAirToWallAttachmentHasRealPublishedContactObservation()
        {
            yield return Publish(TallFriendlyWall()); ResetContinuousAir();
            Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("air-wall-actual-contact-observation", before, rootBefore, nativeBefore); AssertActualAttachment();
            // Keep this observational fixture valid before and after the fix.
            // The following test owns the invariant and intentionally fails on old code.
        }
        [UnityTest] public IEnumerator ContinuousAirSwimAttachmentPreservesSheetHeightAndCanonicalHumanOrigin()
        {
            yield return Publish(TallFriendlyWall()); ResetContinuousAir();
            Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("air-wall-coordinate-invariant", before, rootBefore, nativeBefore); AssertActualAttachment();
            Assert.That(state.PaperCenter.y, Is.EqualTo(before.PaperCenter.y).Within(.005f),
                "Continuous air-to-wall swimming with a horizontal native snap must not add half-height minus surface-offset to the sheet");
            Assert.That(PlayerMotorSimulation.HumanPosition(state).y,
                Is.EqualTo(PlayerMotorSimulation.HumanPosition(before).y).Within(.005f),
                "Clearing AirHumanOffset alone cannot reinterpret the air anchor as wall feet");
            Assert.That(Mathf.Abs(state.Velocity.y), Is.LessThan(.01f), "A coordinate conversion must not invent native movement velocity");
        }
        [UnityTest] public IEnumerator BlockedAttachmentKeepsTheAirCoordinateFrameAndContinuousSwimming()
        {
            yield return Publish(TallFriendlyWall()); ResetContinuousAir(.48f);
            // Two thin columns block the real capsule while leaving the central
            // forward query ray clear. They span both old and converted heights.
            foreach (float side in new[] { -1f, 1f })
            {
                var root = new GameObject("Actual capsule snap blocker " + side);
                root.transform.position = new Vector3(.05f + side * .24f, 2.5f, .80f);
                root.AddComponent<BoxCollider>().size = new Vector3(.04f, 5, .04f);
            }
            Physics.SyncTransforms(); Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("air-wall-native-blocked-snap", before, rootBefore, nativeBefore);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air), "The physical blockers must actually prevent a wall bind");
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False);
            Assert.That(recorder.Contacts.Any(c => !c.BlockCollider && Mathf.Abs(c.Normal.z) > .1f), Is.True,
                "The failed attachment premise must follow native blocker contacts");
            Assert.That(FreshWall(out var remainingWall), Is.True); AssertFriendlyWall(remainingWall, terrain.Revision);
            Assert.That(state.AirHumanOffset, Is.EqualTo(before.AirHumanOffset));
            Assert.That(state.CameraRebaseOffset, Is.EqualTo(before.CameraRebaseOffset).Within(.00001f));
            Assert.That(PlayerMotorSimulation.HumanPosition(state).y - PlayerMotorSimulation.HumanPosition(before).y,
                Is.EqualTo(state.Position.y - before.Position.y).Within(.00001f));
            Assert.That(state.PaperCenter.y - state.Position.y, Is.EqualTo(.03f).Within(.00001f));
            Assert.That(Mathf.Abs(state.Position.y - before.Position.y), Is.LessThan(.05f), "Only the normal air movement is permitted after failure");
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry ||
                e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirExit), Is.False);
        }
    }
}
#endif
