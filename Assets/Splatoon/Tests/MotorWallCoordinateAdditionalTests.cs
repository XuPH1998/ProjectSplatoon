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
    // No reflected TryAttach or mocked probes; restored ground snapshots require fresh real floor queries.
    public sealed class MotorWallCoordinateAdditionalTests
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
        void SeedFrame(float offset, float camera = .37f, float z = .6f)
        {
            ResetContinuousAir(z);
            // Valid supported-plane snapshots with distinct reversible human
            // origins; do not clamp a naturally generated offset to make a test pass.
            state.AirHumanOffset = offset; state.CameraRebaseOffset = camera;
            motor.Restore(state); Physics.SyncTransforms();
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out _), Is.False);
        }
        bool InnerCapsuleEmbedded(Vector3 feet)
        {
            var shape = HeroBodyShape.For(state.HeroId); float radius = Mathf.Max(.05f, shape.Radius - .025f);
            return Physics.CheckCapsule(feet + Vector3.up * radius,
                feet + Vector3.up * Mathf.Max(radius, shape.CompactHeight - radius), radius,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
        Vector3 ConvertedFeet() => new(state.Position.x, state.PaperCenter.y - .9f, state.Position.z);
        bool WallAt(Vector3 feet, out PlayerMotorSimulation.TraversalContact contact) =>
            PlayerMotorSimulation.Query(feet + Vector3.up * (HeroBodyShape.For(state.HeroId).CompactHeight * .5f),
                Vector3.forward, GameplayConfig.GetHero(state.HeroId).WallProbeDistance, out contact);
        static void SameSnapshot(PlayerSnapshot before, PlayerSnapshot after)
        {
            foreach (var field in typeof(PlayerSnapshot).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                Assert.That(field.GetValue(after), Is.EqualTo(field.GetValue(before)), "Tentative frame rollback field: " + field.Name);
        }
        void AssertRejectedFrame(PlayerSnapshot before, bool requireResolved)
        {
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
            Assert.That(state.Swimming && !state.Grounded, Is.True); Assert.That(controller.transform.position, Is.EqualTo(state.Position));
            Assert.That(state.AirHumanOffset, Is.EqualTo(before.AirHumanOffset));
            Assert.That(state.CameraRebaseOffset, Is.EqualTo(before.CameraRebaseOffset * Mathf.Exp(-18 * Dt)).Within(.00001f),
                "Failure keeps only the normal per-Step camera decay, with no provisional conversion remaining");
            Assert.That(state.PaperCenter.y - state.Position.y, Is.EqualTo(.03f).Within(.00001f));
            Assert.That(Mathf.Abs(state.Position.y - before.Position.y), Is.LessThan(.05f));
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry ||
                e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirExit), Is.False);
            var evaluations = events.Where(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachEvaluation).ToArray();
            Assert.That(evaluations.Length, Is.EqualTo(1)); Assert.That(evaluations[0].ProbeSucceeded, Is.False);
            var resolutions = events.Where(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved).ToArray();
            if (requireResolved)
            {
                Assert.That(resolutions.Length, Is.EqualTo(1)); Assert.That(resolutions[0].ProbeSucceeded, Is.False);
                SameSnapshot(resolutions[0].Before, resolutions[0].After);
                Assert.That(resolutions[0].ControllerPosition, Is.EqualTo(before.Position),
                    "Both provisional vertical conversion and physical normal snap must be restored before ordinary air movement");
            }
            else
            {
                SameSnapshot(evaluations[0].Before, evaluations[0].After);
                Assert.That(evaluations[0].ControllerPosition, Is.EqualTo(before.Position));
                Assert.That(evaluations[0].RequestedMove, Is.EqualTo(Vector3.zero), "Embedded destination rejection must occur before native snap");
            }
        }
        void AssertCenterPreserved(PlayerSnapshot before)
        {
            AssertActualAttachment();
            Assert.That(state.PaperCenter.y, Is.EqualTo(before.PaperCenter.y).Within(.005f));
            Assert.That(state.Position.y, Is.EqualTo(before.PaperCenter.y - .9f).Within(.005f));
            Assert.That(Mathf.Abs(state.Velocity.y), Is.LessThan(.01f));
            float conversion = before.PaperCenter.y - .9f - before.Position.y;
            Assert.That(state.CameraRebaseOffset,
                Is.EqualTo(before.CameraRebaseOffset * Mathf.Exp(-18 * Dt) - conversion).Within(.00001f));
            var resolved = events.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved);
            Assert.That(resolved.ProbeSucceeded, Is.True); Assert.That(resolved.Contact.Cell, Is.EqualTo(state.SupportCell));
            Assert.That(resolved.Contact.Revision, Is.EqualTo(state.SupportTerrainRevision));
            Assert.That(resolved.Contact.Cell.y, Is.LessThan(30), "Bind the real lower-height wall, not the old air-height cell");
        }
        static BlockWorld HeightSplitWall(string lower)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -8; x <= 8; x++) for (int y = 30; y < 50; y++)
                world.Set(new Vector3Int(x, y, 10), BlockCell.Create(1, true));
            if (lower == "enemy" || lower == "recessed")
                for (int x = -8; x <= 8; x++) for (int y = 0; y < 30; y++)
                    world.Set(new Vector3Int(x, y, lower == "recessed" ? 11 : 10), BlockCell.Create((byte)(lower == "enemy" ? 2 : 1), true));
            return world;
        }
        static GameObject Box(string name, Vector3 center, Vector3 size)
        {
            var root = new GameObject(name); root.transform.position = center; root.AddComponent<BoxCollider>().size = size;
            Physics.SyncTransforms(); return root;
        }

        [UnityTest] public IEnumerator ZeroNegativeAndPositiveAirOffsetsPreserveActualSheetCenter()
        {
            yield return Publish(TallFriendlyWall());
            foreach (float offset in new[] { 0f, -.02f, .87f })
            {
                SeedFrame(offset); Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
                Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.False);
                var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
                Step(); Save("additional-offset-" + (offset == 0 ? "zero" : offset < 0 ? "negative" : "positive"), before, rootBefore, nativeBefore);
                AssertCenterPreserved(before);
                // For zero/negative ground-derived offsets HumanPosition cannot
                // also remain constant while rotating around this sheet centre.
                Assert.That(PlayerMotorSimulation.HumanPosition(state).y - PlayerMotorSimulation.HumanPosition(before).y,
                    Is.EqualTo(state.Position.y - before.Position.y + before.AirHumanOffset).Within(.00001f));
            }
        }

        [UnityTest] public IEnumerator NativeGroundDepartureRetainsItsUnmodifiedOffsetOnWallRotation()
        {
            var world = TallFriendlyWall();
            for (int x = -8; x <= 8; x++) for (int z = 0; z < 10; z++)
                world.Set(new Vector3Int(x, 20, z), BlockCell.Create(1, true));
            yield return Publish(world); ResetContinuousAir();
            state.Position = new Vector3(.05f, 2.14f, .6f); state.Swimming = false; state.CompactBody = false;
            state.PaperPose = PaperPose.None; state.PaperCenter = state.Position; state.AirHumanOffset = 0;
            state.SwimSource = state.AirSwimSource = SwimSurface.None; state.VerticalSpeed = -2;
            input.Swim = false; input.Move = Vector2.zero; motor.Restore(state); Physics.SyncTransforms();
            for (int tick = 0; tick < 12 && !state.Grounded; tick++) Step();
            Assert.That(state.Grounded && controller.isGrounded, Is.True, "Establish actual ground before requesting paper");
            input.Swim = true; Step();
            Assert.That(state.Movement, Is.EqualTo(MovementMode.GroundInk)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Ground));
            Assert.That(state.FriendlyInkContact, Is.True);
            yield return Publish(TallFriendlyWall());
            var groundBefore = state; var groundRoot = controller.transform.position; bool nativeGroundBefore = controller.isGrounded;
            Step(); Save("additional-natural-ground-departure", groundBefore, groundRoot, nativeGroundBefore);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out _), Is.False);
            Assert.That(state.AirHumanOffset, Is.LessThan(-.0001f),
                "Native skin clearance must produce the real negative offset; do not edit this field to force a premise");
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry), Is.True);
            input.Move = Vector2.up; events.Clear();
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
            Step(); Save("additional-natural-ground-air-wall", before, rootBefore, nativeBefore); AssertCenterPreserved(before);
        }

        [UnityTest] public IEnumerator ConvertedHeightEnemyNeutralAndEmptyContactsRejectAndRestoreFrame()
        {
            foreach (string lower in new[] { "enemy", "neutral", "empty" })
            {
                yield return Publish(HeightSplitWall(lower));
                GameObject neutral = lower == "neutral" ? Box("Converted-height neutral map wall", new Vector3(.05f, 1.5f, 1.05f), new Vector3(1.7f, 3, .1f)) : null;
                SeedFrame(.87f, .43f); Assert.That(FreshWall(out var upper), Is.True); AssertFriendlyWall(upper, terrain.Revision);
                Assert.That(upper.Cell.y, Is.GreaterThanOrEqualTo(30)); Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.False);
                bool lowerFound = WallAt(ConvertedFeet(), out var atLower);
                if (lower == "enemy") { Assert.That(lowerFound, Is.True); Assert.That(atLower.Owner, Is.EqualTo(2)); }
                else
                {
                    Assert.That(lowerFound, Is.False);
                    bool rawHit = Physics.Raycast(ConvertedFeet() + Vector3.up * .35f, Vector3.forward, out var raw,
                        .6f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
                    Assert.That(rawHit, Is.EqualTo(lower == "neutral"));
                    if (rawHit) Assert.That(raw.collider.gameObject, Is.EqualTo(neutral));
                }
                var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
                Step(); Save("additional-converted-height-" + lower, before, rootBefore, nativeBefore); AssertRejectedFrame(before, true);
                if (lower == "enemy")
                    Assert.That(recorder.Contacts.Any(c => c.BlockCollider && c.Normal.z < -.9f && c.Point.y < 3), Is.True);
                if (neutral != null) UnityEngine.Object.DestroyImmediate(neutral);
            }
        }

        [UnityTest] public IEnumerator EmbeddedConvertedCapsuleIsRejectedBeforeProvisionalMovement()
        {
            yield return Publish(TallFriendlyWall());
            Box("Real conversion destination obstruction", new Vector3(.05f, 2.35f, .6f), new Vector3(.08f, .08f, .08f));
            SeedFrame(.87f, -.31f); Assert.That(FreshWall(out var upper), Is.True); AssertFriendlyWall(upper, terrain.Revision);
            Assert.That(InnerCapsuleEmbedded(state.Position), Is.False); Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.True);
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("additional-converted-destination-embedded", before, rootBefore, nativeBefore); AssertRejectedFrame(before, false);
            Assert.That(recorder.Contacts.Any(c => !c.BlockCollider && c.Point.y < 2.8f), Is.False,
                "A rejected coordinate destination must never become the physical capsule origin");
        }

        [UnityTest] public IEnumerator RecessedConvertedWallDoesNotInheritTheOldHeightSnap()
        {
            yield return Publish(HeightSplitWall("recessed")); SeedFrame(-.02f, .29f);
            Assert.That(FreshWall(out var upper), Is.True); AssertFriendlyWall(upper, terrain.Revision); Assert.That(upper.Cell.z, Is.EqualTo(10));
            Assert.That(WallAt(ConvertedFeet(), out var lower), Is.True); AssertFriendlyWall(lower, terrain.Revision); Assert.That(lower.Cell.z, Is.EqualTo(11));
            Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.False);
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("additional-converted-height-recessed", before, rootBefore, nativeBefore); AssertRejectedFrame(before, true);
            Assert.That(Vector3.Dot(controller.transform.position + Vector3.up * .35f - lower.Point, lower.Normal),
                Is.GreaterThan(HeroBodyShape.For(1).Radius + .065f));
        }

        [UnityTest] public IEnumerator LegalNearWallOriginsSurviveInnerClearanceCheck()
        {
            yield return Publish(TallFriendlyWall());
            foreach (float z in new[] { .61f, .62f })
            {
                SeedFrame(0, -.23f, z); Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
                Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.False);
                var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
                Step(); Save("additional-legal-near-wall-" + (z < .615f ? "039" : "038"), before, rootBefore, nativeBefore);
                AssertCenterPreserved(before);
            }
        }

        [UnityTest] public IEnumerator NativeBlockedSnapRollsBackNonzeroCameraAndEverySnapshotField()
        {
            yield return Publish(TallFriendlyWall()); SeedFrame(-.02f, .41f, .48f);
            foreach (float side in new[] { -1f, 1f })
                Box("Real rollback capsule blocker " + side, new Vector3(.05f + side * .24f, 2.5f, .80f), new Vector3(.04f, 5, .04f));
            Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
            Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.False, "Test native snap failure after conversion, not precheck rejection");
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("additional-native-blocked-nonzero-camera", before, rootBefore, nativeBefore); AssertRejectedFrame(before, true);
            Assert.That(recorder.Contacts.Any(c => !c.BlockCollider && Mathf.Abs(c.Normal.z) > .1f && c.Point.y < 2.8f), Is.True,
                "The physical failed snap must occur at the converted capsule height");
            Assert.That(FreshWall(out var remaining), Is.True); AssertFriendlyWall(remaining, terrain.Revision);
        }

        [UnityTest] public IEnumerator ShortFloorApproachRecordsFreshSupportAndStillEntersFriendlyWall()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++)
                world.Set(new Vector3Int(x, 0, z), BlockCell.Create(1, true));
            for (int y = 0; y < 40; y++) for (int x = -10; x <= 10; x++)
                world.Set(new Vector3Int(x, y, 20), BlockCell.Create(1, true));
            yield return Publish(world); ResetContinuousAir();
            state.Position = new Vector3(.05f, .14f, 1); state.Movement = MovementMode.Human;
            state.Swimming = state.CompactBody = false; state.Grounded = true;
            state.PaperPose = PaperPose.None; state.PaperCenter = state.Position; state.AirHumanOffset = 0;
            state.VerticalSpeed = 0; state.Velocity = state.PlanarVelocity = Vector3.zero;
            state.SwimSource = state.AirSwimSource = SwimSurface.None; input.Move = Vector2.up;
            motor.Restore(state); Physics.SyncTransforms();
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out var initialFloor), Is.True);
            Assert.That(initialFloor.IsBlock && initialFloor.Stable && initialFloor.Owner == 1, Is.True,
                "The original fixture's restored Grounded snapshot must have a real published floor query");
            bool nativeWall = false, supportedAirObserved = false;
            for (int tick = 0; tick < 90 && state.Movement != MovementMode.WallInk; tick++)
            {
                events.Clear(); var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
                bool freshFloor = PlayerMotorSimulation.TryGetGround(state.Position, out _);
                supportedAirObserved |= freshFloor && state.VerticalSpeed <= 0 && state.Movement == MovementMode.Air && state.PaperPose == PaperPose.Air;
                Step(); Save("additional-short-floor-wall-step-" + tick.ToString("D2"), before, rootBefore, nativeBefore);
                nativeWall |= recorder.Contacts.Any(c => c.BlockCollider && c.Normal.z < -.9f && Mathf.Abs(c.Point.z - 2) < .015f);
            }
            TestContext.Progress.WriteLine("Fresh floor with descending Air/Paper frame observed: " + supportedAirObserved);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk), "Keep the original short-floor entry requirement");
            Assert.That(state.FriendlyInkContact, Is.True); Assert.That(nativeWall, Is.True);
            Assert.That(PlayerMotorSimulation.Query(state.Position + Vector3.up * .35f, Vector3.forward, .6f, out var wall), Is.True);
            Assert.That(wall.IsBlock && wall.Stable && wall.Owner == 1, Is.True);
            Assert.That(state.SupportCell, Is.EqualTo(wall.Cell)); Assert.That(state.SupportTerrainRevision, Is.EqualTo(wall.Revision));
        }

        [UnityTest] public IEnumerator RisingAirPaperKeepsCenterPivotDespiteNearbyFreshFloor()
        {
            var world = TallFriendlyWall();
            for (int x = -8; x <= 8; x++) for (int z = -8; z <= 8; z++)
                world.Set(new Vector3Int(x, 20, z), BlockCell.Create(1, true));
            yield return Publish(world); SeedFrame(-.02f, .26f);
            state.Position = new Vector3(.05f, 2.14f, .6f); state.PaperCenter = state.Position + Vector3.up * .03f;
            state.VerticalSpeed = 3; state.Velocity = Vector3.up * 3; motor.Restore(state); Physics.SyncTransforms();
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out var floor), Is.True);
            Assert.That(floor.IsBlock && floor.Stable && floor.Owner == 1, Is.True);
            Assert.That(state.Grounded, Is.False); Assert.That(state.VerticalSpeed, Is.GreaterThan(0));
            Assert.That(InnerCapsuleEmbedded(state.Position), Is.False); Assert.That(InnerCapsuleEmbedded(ConvertedFeet()), Is.False);
            Assert.That(FreshWall(out var entry), Is.True); AssertFriendlyWall(entry, terrain.Revision);
            var before = state; var rootBefore = controller.transform.position; bool nativeBefore = controller.isGrounded;
            Step(); Save("additional-rising-air-near-fresh-floor", before, rootBefore, nativeBefore); AssertCenterPreserved(before);
            Assert.That(events.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved).RebaseHeight,
                Is.LessThan(-.8f), "A nearby floor query cannot turn a rising air sheet into a ground pivot");
        }
    }
}
#endif
