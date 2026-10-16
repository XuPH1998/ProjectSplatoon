#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Published voxel colliders, a real authored Profile, the actual captured
    // silhouette and ordinary Motor.Step. These checks do not certify the next
    // animation sample or an independent Player's network acceptance.
    public sealed class BlockWallLiveGeometryTests
    {
        const float Dt = 1f / 60;
        BlockTerrain terrain;
        PrototypePlayer player;
        CharacterController controller;
        MotorGroundContactRecorder recorder;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> events = new();
        SwimBody Body => player.SwimBody;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            terrain = new GameObject("Real silhouette wall terrain").AddComponent<BlockTerrain>(); terrain.Initialize(null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            player = Object.Instantiate(prefab).GetComponent<PrototypePlayer>(); player.gameObject.layer = 8;
            Body.Bind(PaperBodyTests.Profile("RifleGirl"));
            Assert.That(Body.Profile, Is.Not.Null); Assert.That(Body.Profile.CapturePrefab, Is.Not.Null);
            controller = player.GetComponent<CharacterController>();
            recorder = player.gameObject.AddComponent<MotorGroundContactRecorder>();
            motor = new PlayerMotorSimulation(controller);
            PlayerMotorSimulation.MotorBranchObserved += Observe;
        }
        [TearDown] public void Cleanup()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe;
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Observe(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
        { if (source == motor) events.Add(observation); }
        static BlockWorld Wall()
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
            for (int i = 0; i < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); i++)
            { terrain.Tick(false, 10, Dt); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.Revision, Is.EqualTo(revision));
            Physics.SyncTransforms();
        }
        void Air()
        {
            state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1,
                Health = 100, Ink = 43, LifeState = PlayerLifeState.Alive,
                Position = new Vector3(.05f, 2.87f, .48f), Movement = MovementMode.Air,
                Swimming = true, Grounded = false, PaperPose = PaperPose.Air,
                AirHumanOffset = .87f, CameraRebaseOffset = -.2f, VerticalSpeed = -.1f,
                Velocity = Vector3.down * .1f, GroundNormal = Vector3.up,
                // Turn the airborne sheet across the approach so its real
                // horizontal geometry is clear before the wall snap. Camera
                // yaw remains forward: continuous Air paper retains its heading.
                PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 90),
                PaperAnimationTime = .13f, PaperMove = Vector2.zero,
                SwimSource = SwimSurface.Friendly, AirSwimSource = SwimSurface.Friendly, SwimWasHeld = true,
                SimulatedAt = 10, SimulationTick = 1 };
            state.PaperCenter = state.Position + Vector3.up * Body.Profile.SurfaceOffset;
            input = new PlayerInputFrame { Swim = true, Move = Vector2.up, Look = Vector2.zero,
                Sequence = 100, Tick = 100, Revision = 1, HeroRevision = 1 };
            motor.Restore(state); Physics.SyncTransforms(); events.Clear();
            Assert.That(Body.Capture, Is.Not.Null); Assert.That(Body.HitRects, Is.Not.Null);
            Assert.That(Body.HitRects.Count, Is.GreaterThan(4), "Exercise actual captured silhouette rectangles");
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out _), Is.False);
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False);
        }
        void Step(bool sampleFinal = false)
        {
            input.Sequence++; input.Tick++; state.SimulationTick++; state.SimulatedAt += Dt;
            recorder.Begin(input.Sequence); motor.Step(ref state, input, Dt, state.SimulatedAt, false);
            if (sampleFinal) Body.ApplyCollision(state);
            Physics.SyncTransforms();
        }
        PlayerSnapshot WallCandidate()
        {
            var candidate = state;
            candidate.Position.y = state.PaperCenter.y - Body.Profile.Size.y * .5f;
            candidate.Position.z = 1 - controller.radius - .015f;
            candidate.Movement = MovementMode.WallInk; candidate.Swimming = true;
            candidate.CompactBody = false; candidate.Grounded = false; candidate.AirHumanOffset = 0;
            candidate.WallNormal = Vector3.back; candidate.WallPoint = new Vector3(candidate.Position.x, candidate.Position.y, 1);
            PaperPoseSimulation.Resolve(ref candidate, state, Body.Profile, state.SimulatedAt);
            return candidate;
        }
        static BoxCollider Box(string name, Vector3 center, float side)
        {
            var root = new GameObject(name); root.transform.position = center;
            var box = root.AddComponent<BoxCollider>(); box.size = Vector3.one * side;
            Physics.SyncTransforms(); return box;
        }
        bool PaperOverlaps(PlayerSnapshot s)
        {
            Assert.That(Body.HitRects, Is.Not.Null);
            foreach (var rect in Body.HitRects)
                if (Physics.CheckBox(s.PaperCenter + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                    new Vector3(rect.width * .5f, rect.height * .5f, Body.Profile.Thickness * .5f), s.PaperRotation,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }
        static bool CapsuleOverlaps(PlayerSnapshot s)
        {
            var shape = HeroBodyShape.For(s.HeroId);
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = s.Position + Vector3.up * (height * .5f);
            return Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment,
                shape.Radius - Mathf.Max(.005f, shape.SkinWidth), PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
        void Attached()
        {
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(PlayerMotorSimulation.Query(state.Position + Vector3.up * (controller.height * .5f),
                -state.WallNormal, controller.radius + .065f, out var contact), Is.True);
            Assert.That(contact.IsBlock && contact.Stable && contact.Climbable, Is.True);
            Assert.That(contact.Owner, Is.EqualTo(state.Team)); Assert.That(contact.Revision, Is.EqualTo(terrain.Revision));
            Assert.That(state.SupportTerrainRevision, Is.EqualTo(contact.Revision)); Assert.That(state.SupportCell, Is.EqualTo(contact.Cell));
            Assert.That(player.transform.position, Is.EqualTo(state.Position));
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False);
        }

        sealed class LiveGeometryStamp
        {
            readonly SwimBody body;
            readonly PaperCapture capture;
            readonly int version;
            readonly IReadOnlyList<Rect> rects;
            readonly Rect[] values;
            readonly Mesh mesh;
            readonly Vector3[] vertices;
            readonly int[] triangles;
            readonly RenderTexture texture;
            readonly Bounds bounds;
            readonly bool hitEnabled, rendererEnabled;
            readonly (Transform Transform, Vector3 Position, Quaternion Rotation, Vector3 Scale)[] transforms;
            public LiveGeometryStamp(SwimBody body)
            {
                this.body = body; capture = body.Capture; Assert.That(capture, Is.Not.Null);
                version = capture.PoseVersion; rects = body.HitRects; values = rects.ToArray();
                mesh = body.HitVolume.sharedMesh; Assert.That(mesh, Is.Not.Null);
                vertices = mesh.vertices; triangles = mesh.triangles; texture = capture.Texture; bounds = capture.PosedBounds;
                hitEnabled = body.HitVolume.enabled; rendererEnabled = body.BodyRenderer.enabled;
                var stage = (GameObject)typeof(PaperCapture).GetField("_root",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(capture);
                transforms = stage.GetComponentsInChildren<Transform>(true)
                    .Concat(new[] { body.HitVolume.transform, body.BodyRenderer.transform, body.transform.root }).Distinct()
                    .Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToArray();
            }
            public void AssertUnchanged()
            {
                Assert.That(body.Capture, Is.SameAs(capture)); Assert.That(capture.PoseVersion, Is.EqualTo(version));
                Assert.That(body.HitRects, Is.SameAs(rects)); CollectionAssert.AreEqual(values, body.HitRects);
                Assert.That(body.HitVolume.sharedMesh, Is.SameAs(mesh));
                CollectionAssert.AreEqual(vertices, mesh.vertices); CollectionAssert.AreEqual(triangles, mesh.triangles);
                Assert.That(capture.Texture, Is.SameAs(texture)); Assert.That(capture.PosedBounds, Is.EqualTo(bounds));
                Assert.That(body.HitVolume.enabled, Is.EqualTo(hitEnabled));
                Assert.That(body.BodyRenderer.enabled, Is.EqualTo(rendererEnabled));
                foreach (var t in transforms)
                {
                    Assert.That(t.Transform.localPosition, Is.EqualTo(t.Position));
                    Assert.That(t.Transform.localRotation, Is.EqualTo(t.Rotation));
                    Assert.That(t.Transform.localScale, Is.EqualTo(t.Scale));
                }
            }
        }

        // Independent authored samples choose a genuine distinguishing obstacle.
        // The production candidate query is not used to construct the oracle.
        void DifferentContours(out PlayerSnapshot blocked, out PlayerSnapshot hole, out BoxCollider obstacle)
        {
            blocked = WallCandidate(); hole = default;
            obstacle = Box("Actual animated contour difference outside both capsules", Vector3.zero, .004f);
            using var oracle = new PaperCapture(Body.Profile);
            oracle.Sample(blocked); var occupied = oracle.HitRects.ToArray();
            foreach (var move in new[] { Vector2.up, Vector2.right, Vector2.left, Vector2.down, Vector2.zero })
            foreach (float time in new[] { .03f, .23f, .43f, .63f, .83f, 1.03f })
            {
                var other = blocked; other.PaperAnimationTime = time; other.PaperMove = move;
                oracle.Sample(other); var otherRects = oracle.HitRects.ToArray();
                for (int y = PaperSilhouette.Rows - 2; y > PaperSilhouette.Rows / 2; y--)
                for (int x = 1; x < PaperSilhouette.Columns - 1; x++)
                {
                    var point = new Vector2(((x + .5f) / PaperSilhouette.Columns - .5f) * Body.Profile.Size.x,
                        ((y + .5f) / PaperSilhouette.Rows - .5f) * Body.Profile.Size.y);
                    // A 10 mm clear square around the 4 mm obstacle avoids
                    // edge tolerance or quantization being the counterexample.
                    var clearance = new Rect(point - Vector2.one * .005f, Vector2.one * .01f);
                    if (!occupied.Any(r => r.xMin < clearance.xMin && r.xMax > clearance.xMax &&
                        r.yMin < clearance.yMin && r.yMax > clearance.yMax) || otherRects.Any(r => r.Overlaps(clearance))) continue;
                    obstacle.transform.position = blocked.PaperCenter + blocked.PaperRotation * new Vector3(point.x, point.y, 0);
                    Physics.SyncTransforms();
                    if (CapsuleOverlaps(blocked) || CapsuleOverlaps(state) || PaperOverlaps(state)) continue;
                    hole = other;
                    Body.ApplyCollision(blocked); Physics.SyncTransforms();
                    Assert.That(PaperOverlaps(blocked), Is.True, "Independent real candidate sample is actually blocked");
                    Body.ApplyCollision(hole); Physics.SyncTransforms();
                    Assert.That(PaperOverlaps(blocked), Is.False, "Another actual animation/move key has a genuine hole at this point");
                    return;
                }
            }
            Assert.Fail("The authored rig must supply an actual occupied-versus-hole contour difference outside the capsules");
        }

        [UnityTest] public IEnumerator CandidateWallContourIgnoresLiveHistoryAndKeepsItsRealHoleWithoutPublishing()
        {
            yield return Publish(Wall()); Air();
            DifferentContours(out var blocked, out var hole, out _);
            var size = Body.Profile.Size;
            Assert.That(Physics.CheckBox(blocked.PaperCenter, new Vector3(size.x * .5f, size.y * .5f, Body.Profile.Thickness * .5f),
                blocked.PaperRotation, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True,
                "A full profile rectangle must reject the genuine hole counterexample");
            foreach (var live in new[] { hole, blocked, hole })
            {
                Body.ApplyCollision(live); Physics.SyncTransforms(); var stamp = new LiveGeometryStamp(Body);
                Assert.That(terrain.PublishedTraversalPoseFits(player, blocked), Is.False,
                    "The candidate's own occupied sample must reject independently of the live capture key");
                Assert.That(terrain.PublishedTraversalPoseFits(player, hole), Is.True,
                    "The candidate's real hole must pass independently of a conflicting live capture key");
                stamp.AssertUnchanged();
            }
        }

        [Test] public void CandidateContourCacheQuantizesEvictsRecomputesAndInvalidatesAuthoredShape()
        {
            var sample = new PlayerSnapshot { PaperAnimationTime = .101f, PaperMove = new Vector2(.2f, -.2f) };
            var retained = Body.QueryTraversalRects(sample); var frozen = retained.ToArray();
            Assert.That(Body.Capture, Is.Null, "A candidate lookup must not promote its private evaluator to live capture");
            var same = sample; same.PaperAnimationTime = .108f; same.PaperMove = new Vector2(.24f, -.24f);
            Assert.That(Body.QueryTraversalRects(same), Is.SameAs(retained), "Exactly the 30 Hz/eighth-move key is shared");
            for (int i = 1; i <= 65; i++)
            { var later = sample; later.PaperAnimationTime = (3 + i + .25f) / PaperAnimation.FramesPerSecond; Body.QueryTraversalRects(later); }
            var recomputed = Body.QueryTraversalRects(sample);
            Assert.That(recomputed, Is.Not.SameAs(retained), "The first entry really passed deterministic FIFO eviction");
            CollectionAssert.AreEqual(frozen, retained); CollectionAssert.AreEqual(frozen, recomputed);
            var mutable = retained as IList<Rect>; Assert.That(mutable, Is.Not.Null); Assert.That(mutable.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => mutable[0] = default);
            using (var oracle = new PaperCapture(Body.Profile))
            { oracle.Sample(sample); CollectionAssert.AreEqual(oracle.HitRects, recomputed); }
            var original = Body.Profile; var changed = Object.Instantiate(original);
            try
            {
                Body.Bind(changed); var beforeShapeChange = Body.QueryTraversalRects(sample); var beforeValues = beforeShapeChange.ToArray();
                changed.Size = new Vector2(changed.Size.x * .75f, changed.Size.y * .9f);
                changed.CaptureCenter += Vector3.up * .015f; changed.ContentHash = "candidate-contour-test-shape-change";
                var afterShapeChange = Body.QueryTraversalRects(sample);
                Assert.That(afterShapeChange, Is.Not.SameAs(beforeShapeChange));
                CollectionAssert.AreEqual(beforeValues, beforeShapeChange);
                using var oracle = new PaperCapture(changed); oracle.Sample(sample);
                CollectionAssert.AreEqual(oracle.HitRects, afterShapeChange);
                Body.Bind(PaperBodyTests.Profile("DualPistolGirl"));
                using var otherHero = new PaperCapture(Body.Profile); otherHero.Sample(sample);
                CollectionAssert.AreEqual(otherHero.HitRects, Body.QueryTraversalRects(sample), "A different hero/weapon rig uses its own shape");
                Assert.That(Body.Capture, Is.Null);
            }
            finally { Body.Bind(original); Object.DestroyImmediate(changed); }
            PaperBodyTests.ReleaseTestBody(Body);
            Assert.That(Body.Capture, Is.Null);
            Assert.That(typeof(SwimBody).GetField("_traversalCapture", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).GetValue(Body), Is.Null, "The independent stage is released by the existing lifecycle");
        }

        PlayerSnapshot[] ContourHistory(PlayerSnapshot initial, PlayerSnapshot liveSeed, bool publishEveryStep, out bool firstAttach)
        {
            state = initial; motor.Restore(state, false); Body.ApplyCollision(liveSeed); Physics.SyncTransforms();
            input = new PlayerInputFrame { Swim = true, Move = Vector2.up, Look = Vector2.zero,
                Sequence = 100, Tick = 100, Revision = 1, HeroRevision = 1 };
            events.Clear(); var result = new List<PlayerSnapshot>(); int version = Body.Capture.PoseVersion;
            firstAttach = false;
            for (int i = 0; i < 12; i++)
            {
                Step(publishEveryStep); result.Add(state);
                if (i == 0)
                    firstAttach = events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved && e.ProbeSucceeded);
                if (!publishEveryStep) Assert.That(Body.Capture.PoseVersion, Is.EqualTo(version), "Replay does not publish intermediate paper");
                else Assert.That(PaperOverlaps(state), Is.False, "The authority's final sampled contour must really fit; do not skip expansions");
                Assert.That(CapsuleOverlaps(state), Is.False);
            }
            Body.ApplyCollision(state); Physics.SyncTransforms();
            Assert.That(PaperOverlaps(state), Is.False, "The replay's final actually published sample must fit");
            return result.ToArray();
        }

        [UnityTest] public IEnumerator WallInputHistoryMatchesPerStepAuthorityAndUnsampledReplayAcrossRealObstructionAndHole()
        {
            yield return Publish(Wall()); Air(); var initial = state;
            DifferentContours(out var blocked, out var hole, out _);
            foreach (bool shouldAttach in new[] { false, true })
            {
                var start = initial; var candidateKey = shouldAttach ? hole : blocked;
                start.PaperAnimationTime = candidateKey.PaperAnimationTime; start.PaperMove = candidateKey.PaperMove;
                var authority = ContourHistory(start, candidateKey, true, out bool authorityAttach);
                var replay = ContourHistory(start, shouldAttach ? blocked : hole, false, out bool replayAttach);
                Assert.That(authorityAttach, Is.EqualTo(shouldAttach), "Actual obstruction/hole must affect the ordinary first attach branch");
                Assert.That(replayAttach, Is.EqualTo(authorityAttach));
                for (int i = 0; i < authority.Length; i++)
                {
                    var a = authority[i]; var b = replay[i];
                    Assert.That(b.SimulatedAt, Is.EqualTo(a.SimulatedAt)); Assert.That(b.SimulationTick, Is.EqualTo(a.SimulationTick));
                    Assert.That(b.Movement, Is.EqualTo(a.Movement)); Assert.That(b.PaperPose, Is.EqualTo(a.PaperPose));
                    Assert.That(b.Grounded, Is.EqualTo(a.Grounded)); Assert.That(b.Swimming, Is.EqualTo(a.Swimming));
                    Assert.That(b.Position, Is.EqualTo(a.Position), "Compare the unmodified raw coordinate, including origin conversions");
                    Assert.That(b.AirHumanOffset, Is.EqualTo(a.AirHumanOffset)); Assert.That(b.PaperCenter, Is.EqualTo(a.PaperCenter));
                    Assert.That(b.PaperRotation, Is.EqualTo(a.PaperRotation)); Assert.That(b.Velocity, Is.EqualTo(a.Velocity));
                    Assert.That(b.PaperAnimationTime, Is.EqualTo(a.PaperAnimationTime)); Assert.That(b.PaperMove, Is.EqualTo(a.PaperMove));
                    Assert.That(b.SupportCell, Is.EqualTo(a.SupportCell)); Assert.That(b.SupportTerrainRevision, Is.EqualTo(a.SupportTerrainRevision));
                }
            }
        }

        [UnityTest] public IEnumerator FirstAttachRejectsARealPaperOnlyUpperWingObstructionAndKeepsTheAirOrigin()
        {
            yield return Publish(Wall()); Air();
            var candidate = WallCandidate(); var obstacle = Box("Upper live paper rectangle obstacle", Vector3.zero, .01f);
            bool found = false;
            foreach (var rect in Body.HitRects.OrderByDescending(r => r.center.y))
            {
                obstacle.transform.position = candidate.PaperCenter + candidate.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
                Physics.SyncTransforms();
                if (!CapsuleOverlaps(candidate) && !CapsuleOverlaps(state) && !PaperOverlaps(state) && PaperOverlaps(candidate))
                { found = true; break; }
            }
            Assert.That(found, Is.True, "The obstruction must hit a real upper/side silhouette rectangle while both motor capsules remain clear");
            Assert.That(PlayerMotorSimulation.Query(state.Position + Vector3.up * (controller.height * .5f), Vector3.forward,
                GameplayConfig.GetHero(1).WallProbeDistance, out var centre), Is.True);
            Assert.That(centre.IsBlock && centre.Climbable && centre.Owner == state.Team, Is.True);
            Assert.That(terrain.PublishedTraversalPoseFits(player, candidate), Is.False);
            var before = state; int sampledVersion = Body.Capture.PoseVersion;
            Step();
            Assert.That(Body.Capture.PoseVersion, Is.EqualTo(sampledVersion), "A tentative wall check cannot bake a historical paper frame");
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved && !e.ProbeSucceeded), Is.True);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False);
            Assert.That(state.AirHumanOffset, Is.EqualTo(before.AirHumanOffset));
            Assert.That(state.CameraRebaseOffset, Is.EqualTo(before.CameraRebaseOffset * Mathf.Exp(-18 * Dt)).Within(.00001f),
                "Only ordinary per-step camera damping is allowed after the provisional origin conversion rolls back");
            Assert.That(state.Position.y - PlayerMotorSimulation.HumanPosition(state).y, Is.EqualTo(before.AirHumanOffset).Within(.00001f));
            Assert.That(state.PaperCenter.y - state.Position.y, Is.EqualTo(Body.Profile.SurfaceOffset).Within(.00001f));
            Assert.That(Vector3.Distance(state.Position, before.Position), Is.LessThan(.1f), "Only the ordinary air step may occur after rejection");
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry || e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirExit), Is.False);
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False);
            Body.ApplyCollision(state); Physics.SyncTransforms();
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False,
                "The final actually sampled hit pose must also be clear");
        }

        [UnityTest] public IEnumerator HeldWallMovementStopsBeforeARealUpperSilhouetteObstructionWithoutDetaching()
        {
            yield return Publish(Wall()); Air(); Step(true); Attached();
            input.Move = new Vector2(0, .1f);
            float delta = GameplayConfig.GetHero(1).WallSwimSpeed * input.Move.y * Dt;
            Assert.That(delta, Is.GreaterThan(.004f));
            var top = Body.HitRects.OrderByDescending(r => r.yMax).First();
            var point = state.PaperCenter + state.PaperRotation * new Vector3(top.center.x, top.yMax + delta * .5f, 0);
            Box("Immediately above the actual live silhouette", point, .002f);
            var next = state; next.Position += Vector3.up * delta; next.PaperCenter += Vector3.up * delta;
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(CapsuleOverlaps(next), Is.False);
            Assert.That(PaperOverlaps(state), Is.False); Assert.That(PaperOverlaps(next), Is.True,
                "The requested wall move must cross a real paper rectangle rather than a capsule or centre-ray blocker");
            var before = state; int sampledVersion = Body.Capture.PoseVersion;
            for (int i = 0; i < 5; i++)
            {
                Step();
                Assert.That(state.Position, Is.EqualTo(before.Position));
                Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Wall));
                Assert.That(state.AirHumanOffset, Is.EqualTo(before.AirHumanOffset));
                Assert.That(state.WallNormal, Is.EqualTo(before.WallNormal));
                Assert.That(state.PaperCenter, Is.EqualTo(before.PaperCenter));
                Assert.That(state.Velocity, Is.EqualTo(Vector3.zero));
                Assert.That(PaperOverlaps(state), Is.False); Assert.That(CapsuleOverlaps(state), Is.False);
            }
            Assert.That(Body.Capture.PoseVersion, Is.EqualTo(sampledVersion), "Pure wall geometry queries must not recook the current silhouette");
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry || e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirExit), Is.False);
            Assert.That(state.Movement, Is.Not.EqualTo(MovementMode.Mantle));
            Body.ApplyCollision(state); Physics.SyncTransforms();
            Assert.That(PaperOverlaps(state), Is.False, "Record a genuine failure if the next actual animation sample expands into the obstacle");
        }

        [UnityTest] public IEnumerator ARealLiveSilhouetteHoleAllowsTheOrdinaryWallAttachment()
        {
            yield return Publish(Wall()); Air(); var candidate = WallCandidate();
            var obstacle = Box("Inside profile rectangle but outside live silhouette", Vector3.zero, .008f);
            bool found = false; var size = Body.Profile.Size;
            for (float x = -size.x * .5f + .03f; x < size.x * .5f - .03f && !found; x += .035f)
            for (float y = .05f; y < size.y * .5f - .03f && !found; y += .035f)
            {
                obstacle.transform.position = candidate.PaperCenter + candidate.PaperRotation * new Vector3(x, y, 0);
                Physics.SyncTransforms();
                found = !CapsuleOverlaps(state) && !CapsuleOverlaps(candidate) && !PaperOverlaps(state) && !PaperOverlaps(candidate);
            }
            Assert.That(found, Is.True, "The real captured silhouette must contain a clear hole within the Profile rectangle");
            Assert.That(Physics.CheckBox(candidate.PaperCenter, new Vector3(size.x * .5f, size.y * .5f, Body.Profile.Thickness * .5f),
                candidate.PaperRotation, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True,
                "A bounding rectangle would reject this obstacle even though every live rectangle is clear");
            Assert.That(terrain.PublishedTraversalPoseFits(player, candidate), Is.True);
            int sampledVersion = Body.Capture.PoseVersion; Step(); Attached();
            Assert.That(Body.Capture.PoseVersion, Is.EqualTo(sampledVersion));
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved && e.ProbeSucceeded), Is.True);
            Body.ApplyCollision(state); Physics.SyncTransforms(); Assert.That(PaperOverlaps(state), Is.False);
        }

        [UnityTest] public IEnumerator RemovingThePublishedWallDetachesOnTheNextStepAndCannotReattachToTheOldCollider()
        {
            yield return Publish(Wall()); Air(); Step(true); Attached();
            uint oldRevision = state.SupportTerrainRevision;
            Assert.That(terrain.World.PinkCount, Is.GreaterThan(0));
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            Assert.That(terrain.Revision, Is.GreaterThan(oldRevision)); Assert.That(terrain.World.PinkCount, Is.Zero);
            Assert.That(PlayerMotorSimulation.Query(state.Position + Vector3.up * (controller.height * .5f),
                -state.WallNormal, controller.radius + .065f, out _), Is.False, "The old published collider must really be gone");
            Step(true);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
            Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.FriendlyInkContact, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False);
            Assert.That(state.PaperCenter.y - state.Position.y, Is.EqualTo(Body.Profile.SurfaceOffset).Within(.00001f));
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False);
            for (int i = 0; i < 5; i++) { Step(true); Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.SupportTerrainRevision, Is.Zero); }
            Assert.That(events.Count(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry), Is.EqualTo(1));
        }
        sealed class FinalFrameCase
        {
            public PlayerSnapshot Initial, MotorEntry, PreAnimation, Final;
            public PlayerInputFrame Input;
            public Rect[] InitialRects, PreAnimationRects, FinalRects;
            public BoxCollider Obstacle;
        }
        FinalFrameCase finalFrameCase;
        sealed class FinalFrameContour
        {
            public Rect[] Rects;
            public readonly ulong[] InsideClearance = new ulong[PaperSilhouette.Rows];
            public readonly ulong[] TouchesClearance = new ulong[PaperSilhouette.Rows];
        }
        readonly struct FinalFrameSeed
        {
            public readonly int Frame;
            public readonly Vector2 Move, Point;
            public FinalFrameSeed(int frame, Vector2 move, Vector2 point) { Frame = frame; Move = move; Point = point; }
        }
        static readonly Dictionary<(PaperBodyProfile, GameObject, string, Vector3, float, Vector2, float, float, bool, bool), FinalFrameSeed>
            FinalFrameSeeds = new();

        FinalFrameContour FinalFrameSample(PaperCapture oracle, PlayerSnapshot sample,
            Dictionary<(int, int, int), FinalFrameContour> cache)
        {
            var key = (Mathf.FloorToInt(sample.PaperAnimationTime * PaperAnimation.FramesPerSecond),
                (int)Mathf.Round(sample.PaperMove.x * 8), (int)Mathf.Round(sample.PaperMove.y * 8));
            if (cache.TryGetValue(key, out var found)) return found;
            oracle.Sample(sample); found = new FinalFrameContour { Rects = oracle.HitRects.ToArray() };
            foreach (var rect in found.Rects)
            {
                // Real rectangles define exact intervals for grid-point centres.
                // Masks reduce the difference search to occupied row bits. Every
                // selected point is still checked with the original Rect rules.
                AddMask(found.InsideClearance, rect.xMin + .005f, rect.xMax - .005f, rect.yMin + .005f, rect.yMax - .005f);
                AddMask(found.TouchesClearance, rect.xMin - .005f, rect.xMax + .005f, rect.yMin - .005f, rect.yMax + .005f);
            }
            cache.Add(key, found); return found;
            void AddMask(ulong[] rows, float minX, float maxX, float minY, float maxY)
            {
                int x0 = Mathf.Max(1, Mathf.FloorToInt((minX / Body.Profile.Size.x + .5f) * PaperSilhouette.Columns - .5f) + 1);
                int x1 = Mathf.Min(PaperSilhouette.Columns - 2, Mathf.CeilToInt((maxX / Body.Profile.Size.x + .5f) * PaperSilhouette.Columns - .5f) - 1);
                int y0 = Mathf.Max(PaperSilhouette.Rows / 2 + 1, Mathf.FloorToInt((minY / Body.Profile.Size.y + .5f) * PaperSilhouette.Rows - .5f) + 1);
                int y1 = Mathf.Min(PaperSilhouette.Rows - 2, Mathf.CeilToInt((maxY / Body.Profile.Size.y + .5f) * PaperSilhouette.Rows - .5f) - 1);
                if (x0 > x1 || y0 > y1) return;
                ulong mask = (ulong.MaxValue >> (64 - (x1 - x0 + 1))) << x0;
                for (int y = y0; y <= y1; y++) rows[y] |= mask;
            }
        }
        IEnumerable<Vector2> FinalFrameDifferencePoints(FinalFrameContour future, FinalFrameContour current, Vector2? saved)
        {
            if (saved.HasValue) yield return saved.Value;
            for (int y = PaperSilhouette.Rows - 2; y > PaperSilhouette.Rows / 2; y--)
            {
                ulong bits = future.InsideClearance[y] & ~current.TouchesClearance[y];
                while (bits != 0)
                {
                    int x = 0; ulong shifted = bits;
                    while ((shifted & 1UL) == 0) { x++; shifted >>= 1; }
                    bits &= bits - 1;
                    var point = new Vector2(((x + .5f) / PaperSilhouette.Columns - .5f) * Body.Profile.Size.x,
                        ((y + .5f) / PaperSilhouette.Rows - .5f) * Body.Profile.Size.y);
                    if (!saved.HasValue || !point.Equals(saved.Value)) yield return point;
                }
            }
        }

        bool FinalFrameOracleOverlaps(PlayerSnapshot sample, IReadOnlyList<Rect> rects)
        {
            foreach (var rect in rects)
                if (Physics.CheckBox(sample.PaperCenter + sample.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                    new Vector3(rect.width * .5f, rect.height * .5f, Body.Profile.Thickness * .5f), sample.PaperRotation,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }
        static bool FinalFrameCapsuleSweepTouchesWorld(PlayerSnapshot start, Vector3 destination)
        {
            var shape = HeroBodyShape.For(start.HeroId);
            float height = start.Swimming || start.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = start.Position + Vector3.up * (height * .5f);
            Vector3 a = center - Vector3.up * segment, b = center + Vector3.up * segment, delta = destination - start.Position;
            if (Physics.CheckCapsule(a, b, shape.Radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) ||
                Physics.CheckCapsule(a + delta, b + delta, shape.Radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return delta.sqrMagnitude > .000000000001f && Physics.CapsuleCast(a, b, shape.Radius, delta.normalized,
                out _, delta.magnitude, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
        PlayerSnapshot FinalFrameAttachQuery(PlayerMotorSimulation.MotorBranchObservation attach)
        {
            var query = attach.After;
            query.Position = attach.ControllerPosition; query.Movement = MovementMode.WallInk;
            query.Swimming = true; query.Grounded = false; query.CompactBody = false; query.AirHumanOffset = 0;
            query.WallSurfaceId = attach.Contact.SurfaceId; query.WallRegionId = attach.Contact.RegionId;
            query.WallNormal = attach.Contact.Normal; query.WallPoint = attach.Contact.Point;
            query.SwimSource = query.AirSwimSource = attach.Contact.Owner == query.Team ? SwimSurface.Friendly : SwimSurface.Neutral;
            PaperPoseSimulation.Resolve(ref query, attach.Before, Body.Profile, attach.After.SimulatedAt);
            return query;
        }
        void RestoreFinalFrameCase(FinalFrameCase trial, PlayerSnapshot liveSeed)
        {
            state = trial.Initial; input = trial.Input; motor.Restore(state, false);
            Body.ApplyCollision(liveSeed); Physics.SyncTransforms(); events.Clear();
        }
        void AssertFinalFramePublication()
        {
            Assert.That(player.transform.position.Equals(state.Position), Is.True);
            Assert.That(CapsuleOverlaps(state), Is.False);
            Body.ApplyCollision(state); Physics.SyncTransforms();
            if (!state.ShowsSwimBody) { Assert.That(Body.HitVolume.enabled, Is.False); return; }
            Assert.That(PaperOverlaps(state), Is.False, "Every actual final live contour must fit; no expansion is skipped");
            using var oracle = new PaperCapture(Body.Profile); oracle.Sample(state);
            CollectionAssert.AreEqual(oracle.HitRects, Body.HitRects);
            Assert.That(Body.HitVolume.transform.position, Is.EqualTo(state.PaperCenter));
            Assert.That(Quaternion.Angle(Body.HitVolume.transform.rotation, state.PaperRotation), Is.LessThan(.0001f));
            // Present is given a conflicting camera rotation. It must consume the
            // validated snapshot's paper pose, including after Yaw/Pitch changed.
            int version = Body.Capture.PoseVersion;
            Body.Present(state, Vector3.zero, Quaternion.Euler(21, 123, 7));
            Assert.That(Body.Capture.PoseVersion, Is.EqualTo(version));
            Assert.That(Body.BodyRenderer.transform.position, Is.EqualTo(state.PaperCenter));
            Assert.That(Quaternion.Angle(Body.BodyRenderer.transform.rotation, state.PaperRotation), Is.LessThan(.0001f));
            Assert.That(PaperOverlaps(state), Is.False);
        }
        PlayerSnapshot RunFinalFrameCase(FinalFrameCase trial, PlayerSnapshot liveSeed, bool publish)
        {
            RestoreFinalFrameCase(trial, liveSeed);
            int version = Body.Capture.PoseVersion; var liveRects = Body.HitRects; var liveValues = liveRects.ToArray();
            Step();
            Assert.That(events.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete).Before.Equals(trial.MotorEntry), Is.True);
            Assert.That(Body.Capture.PoseVersion, Is.EqualTo(version), "Motor finalization never publishes live geometry");
            Assert.That(Body.HitRects, Is.SameAs(liveRects)); CollectionAssert.AreEqual(liveValues, Body.HitRects);
            Assert.That(state.SimulationTick, Is.EqualTo(trial.Initial.SimulationTick + 1));
            Assert.That(state.SimulatedAt, Is.EqualTo(trial.Initial.SimulatedAt + Dt));
            Assert.That(state.Yaw, Is.EqualTo(trial.Input.Look.x)); Assert.That(state.Pitch, Is.EqualTo(trial.Input.Look.y));
            Assert.That(state.SpecialImpulseVelocity.Equals(trial.Initial.SpecialImpulseVelocity), Is.True);
            Assert.That(state.SpecialImpulseRetention, Is.EqualTo(trial.Initial.SpecialImpulseRetention));
            Assert.That(state.SpecialImpulseRemaining, Is.EqualTo(trial.Initial.SpecialImpulseRemaining));
            Assert.That(CapsuleOverlaps(state), Is.False);
            if (publish) AssertFinalFramePublication();
            return state;
        }
        PlayerSnapshot[] RunStationaryFinalFrameHistory(FinalFrameCase trial, PlayerSnapshot liveSeed, bool publishEveryStep)
        {
            RestoreFinalFrameCase(trial, liveSeed);
            int version = Body.Capture.PoseVersion;
            var result = new List<PlayerSnapshot>();
            for (int i = 0; i < 5; i++)
            {
                uint tick = state.SimulationTick; double time = state.SimulatedAt;
                Step();
                Assert.That(state.SimulationTick, Is.EqualTo(tick + 1)); Assert.That(state.SimulatedAt, Is.EqualTo(time + Dt));
                Assert.That(state.Position.Equals(trial.Initial.Position), Is.True);
                Assert.That(state.PaperCenter.Equals(trial.Initial.PaperCenter), Is.True);
                Assert.That(state.PaperRotation.Equals(trial.Initial.PaperRotation), Is.True);
                Assert.That(state.PaperAnimationTime, Is.EqualTo(trial.Initial.PaperAnimationTime));
                Assert.That(state.PaperMove.Equals(trial.Initial.PaperMove), Is.True);
                Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); Assert.That(CapsuleOverlaps(state), Is.False);
                Assert.That(state.Yaw, Is.EqualTo(trial.Input.Look.x)); Assert.That(state.Pitch, Is.EqualTo(trial.Input.Look.y));
                Assert.That(state.SpecialImpulseVelocity.Equals(trial.Initial.SpecialImpulseVelocity), Is.True);
                Assert.That(state.SpecialImpulseRetention, Is.EqualTo(trial.Initial.SpecialImpulseRetention));
                Assert.That(state.SpecialImpulseRemaining, Is.EqualTo(trial.Initial.SpecialImpulseRemaining));
                result.Add(state);
                if (publishEveryStep) AssertFinalFramePublication();
                else Assert.That(Body.Capture.PoseVersion, Is.EqualTo(version), "Replay does not publish any intermediate frame");
            }
            AssertFinalFramePublication();
            return result.ToArray();
        }

        IEnumerator FindFinalFrameExpansion(bool held, bool stationary)
        {
            finalFrameCase = null;
            var origin = state; var originalInput = input;
            origin.SpecialImpulseVelocity = new Vector3(1.25f, 0, -.5f);
            origin.SpecialImpulseRetention = .83f; origin.SpecialImpulseRemaining = .47f;
            originalInput.Move = held ? stationary ? Vector2.zero : Vector2.up * .1f : Vector2.up;
            // First attachment probes along input yaw: from z=.48 to the
            // actual wall at z=1, .6*cos(37deg) cannot cover the .52m gap.
            // Keep the held-wall camera change, and use a reachable first-attach
            // heading that still differs from the saved airborne yaw.
            originalInput.Look = new Vector2(held ? 37 : 15, -11);
            var obstacle = Box("Exact actual next-key contour difference", Vector3.zero, .004f);
            obstacle.enabled = false; Physics.SyncTransforms();
            if (!held)
            {
                var aim = Quaternion.Euler(0, originalInput.Look.x, 0);
                Assert.That(PlayerMotorSimulation.Query(origin.Position + HeroBodyShape.For(origin.HeroId).CompactCenter,
                    aim * Vector3.forward, GameplayConfig.GetHero(origin.HeroId).WallProbeDistance, out var entry), Is.True,
                    "The exact ordinary first-attach ray must reach the real published wall before searching animation keys");
                Assert.That(entry.IsBlock && entry.Stable && entry.Climbable && entry.Owner == origin.Team, Is.True);
                Assert.That(entry.Revision, Is.EqualTo(terrain.Revision));
            }
            int baselineTrials = 0;
            using var oracle = new PaperCapture(Body.Profile);
            var contourCache = new Dictionary<(int, int, int), FinalFrameContour>();
            var profile = Body.Profile;
            var shapeKey = (profile, profile.CapturePrefab, profile.ContentHash ?? "", profile.CaptureCenter,
                profile.CaptureHeight, profile.Size, profile.Thickness, profile.AnimationReferenceSpeed, held, stationary);
            bool hasSaved = FinalFrameSeeds.TryGetValue(shapeKey, out var saved);
            var moves = new[] { Vector2.zero, Vector2.up, Vector2.down, Vector2.left, Vector2.right,
                new Vector2(-1, -1).normalized, new Vector2(-1, 1).normalized,
                new Vector2(1, -1).normalized, new Vector2(1, 1).normalized };
            var seeds = new List<(int Frame, Vector2 Move)>();
            if (hasSaved) seeds.Add((saved.Frame, saved.Move));
            for (int frame = 0; frame <= 90; frame++)
            foreach (var move in moves)
                if (!hasSaved || frame != saved.Frame || !move.Equals(saved.Move)) seeds.Add((frame, move));
            foreach (var seed in seeds)
            {
                if (finalFrameCase != null) break;
                obstacle.enabled = false; Physics.SyncTransforms();
                var trial = new FinalFrameCase { Initial = origin, Input = originalInput, Obstacle = obstacle };
                trial.Initial.PaperAnimationTime = (seed.Frame + .99f) / PaperAnimation.FramesPerSecond;
                trial.Initial.PaperMove = seed.Move;
                RestoreFinalFrameCase(trial, trial.Initial);
                Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False);
                Step(); baselineTrials++;
                if (!held)
                {
                    Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk),
                        $"The unobstructed ordinary baseline must really attach; frame={seed.Frame}, move={seed.Move}");
                    Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Wall));
                    Assert.That(events.Count(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved && e.ProbeSucceeded),
                        Is.EqualTo(1), "Require the real successful first-attachment branch, not a synthetic wall snapshot");
                }
                if (state.Movement != MovementMode.WallInk || state.PaperPose != PaperPose.Wall)
                { yield return null; continue; }
                trial.Final = state;
                trial.MotorEntry = events.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete).Before;
                var recomputed = trial.Final;
                PaperAnimation.Step(ref recomputed, trial.MotorEntry, Dt, Body.Profile);
                Assert.That(recomputed.PaperAnimationTime, Is.EqualTo(trial.Final.PaperAnimationTime));
                Assert.That(recomputed.PaperMove.Equals(trial.Final.PaperMove), Is.True,
                    "Future key is derived from actual finalized velocity/pose, never a guessed time or move");
                trial.PreAnimation = trial.Final;
                trial.PreAnimation.PaperAnimationTime = trial.MotorEntry.PaperAnimationTime;
                trial.PreAnimation.PaperMove = trial.MotorEntry.PaperMove;
                PlayerSnapshot query, sweepStart;
                Vector3 sweepEnd;
                if (held)
                {
                    var wallMove = events.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallMove);
                    query = wallMove.After; PaperPoseSimulation.Resolve(ref query, wallMove.Before, Body.Profile, query.SimulatedAt);
                    sweepStart = wallMove.Before; sweepEnd = wallMove.ControllerPosition;
                }
                else
                {
                    var attach = events.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachEvaluation && e.ProbeSucceeded);
                    Assert.That(attach.Contact.IsBlock && attach.Contact.Stable && attach.Contact.Climbable && attach.Contact.Owner == state.Team, Is.True);
                    query = FinalFrameAttachQuery(attach); sweepStart = attach.After; sweepEnd = attach.ControllerPosition;
                }
                Assert.That(query.Position.Equals(trial.PreAnimation.Position), Is.True);
                Assert.That(query.PaperCenter.Equals(trial.PreAnimation.PaperCenter), Is.True);
                Assert.That(query.PaperRotation.Equals(trial.PreAnimation.PaperRotation), Is.True);
                Assert.That(query.PaperAnimationTime, Is.EqualTo(trial.PreAnimation.PaperAnimationTime));
                Assert.That(query.PaperMove.Equals(trial.PreAnimation.PaperMove), Is.True);
                Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision));
                var initialContour = FinalFrameSample(oracle, trial.Initial, contourCache); trial.InitialRects = initialContour.Rects;
                var preContour = FinalFrameSample(oracle, trial.PreAnimation, contourCache); trial.PreAnimationRects = preContour.Rects;
                var finalContour = FinalFrameSample(oracle, trial.Final, contourCache); trial.FinalRects = finalContour.Rects;
                Assert.That(FinalFrameOracleOverlaps(trial.Final, trial.FinalRects), Is.False);
                if (held && stationary) Assert.That(trial.Final.Velocity.Equals(Vector3.zero), Is.True);
                if (held && !stationary) Assert.That(Vector3.Distance(trial.Final.Position, trial.Initial.Position), Is.GreaterThan(.004f));
                Vector2? savedPoint = hasSaved && seed.Frame == saved.Frame && seed.Move.Equals(saved.Move) ? saved.Point : null;
                foreach (var point in FinalFrameDifferencePoints(finalContour, preContour, savedPoint))
                {
                    var clearance = new Rect(point - Vector2.one * .005f, Vector2.one * .01f);
                    if (!trial.FinalRects.Any(r => r.xMin < clearance.xMin && r.xMax > clearance.xMax && r.yMin < clearance.yMin && r.yMax > clearance.yMax) ||
                        trial.PreAnimationRects.Any(r => r.Overlaps(clearance))) continue;
                    obstacle.transform.SetPositionAndRotation(trial.Final.PaperCenter + trial.Final.PaperRotation * new Vector3(point.x, point.y, 0), trial.Final.PaperRotation);
                    obstacle.enabled = true; Physics.SyncTransforms();
                    if (CapsuleOverlaps(trial.Initial) || CapsuleOverlaps(trial.Final) ||
                        FinalFrameCapsuleSweepTouchesWorld(sweepStart, sweepEnd) ||
                        FinalFrameOracleOverlaps(trial.Initial, trial.InitialRects) ||
                        FinalFrameOracleOverlaps(trial.PreAnimation, trial.PreAnimationRects) ||
                        !FinalFrameOracleOverlaps(trial.Final, trial.FinalRects) ||
                        !terrain.PublishedTraversalPoseFits(player, trial.PreAnimation) || terrain.PublishedTraversalPoseFits(player, trial.Final)) continue;
                    // Crucially do not demand that a with-obstacle Step still
                    // accepts the unsafe baseline. Correct rejection is the test.
                    finalFrameCase = trial; FinalFrameSeeds[shapeKey] = new FinalFrameSeed(seed.Frame, seed.Move, point); break;
                }
                if (finalFrameCase == null) { obstacle.enabled = false; Physics.SyncTransforms(); yield return null; }
            }
            Assert.That(finalFrameCase, Is.Not.Null,
                $"A real authored one-Step upper-contour expansion is required; baselineTrials={baselineTrials}. No ignored or fabricated coverage.");
            Assert.That(terrain.PublishedTraversalPoseFits(player, finalFrameCase.PreAnimation), Is.True);
            Assert.That(terrain.PublishedTraversalPoseFits(player, finalFrameCase.Final), Is.False);
        }

        [UnityTest] public IEnumerator FinalKeyNewAttachmentRollsBackOriginAndReplaysTheSafeAirStep()
        {
            yield return Publish(Wall()); Air(); yield return FindFinalFrameExpansion(false, false);
            var trial = finalFrameCase;
            var authority = RunFinalFrameCase(trial, trial.Initial, true);
            var replay = RunFinalFrameCase(trial, trial.Final, false);
            Assert.That(replay.Equals(authority), Is.True, "Compare every raw snapshot field across conflicting live histories");
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False);
            Assert.That(state.AirHumanOffset, Is.EqualTo(trial.Initial.AirHumanOffset));
            Assert.That(state.CameraRebaseOffset, Is.EqualTo(trial.Initial.CameraRebaseOffset * Mathf.Exp(-18 * Dt)).Within(.00001f));
            Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.FriendlyInkContact, Is.False);
            Assert.That(state.PaperCenter.y - state.Position.y, Is.EqualTo(Body.Profile.SurfaceOffset).Within(.00001f));
            AssertFinalFramePublication();
        }

        [UnityTest] public IEnumerator FinalKeyHeldWallMovementRetainsTheCompleteSafeFrameAcrossCameraChanges()
        {
            yield return Publish(Wall()); Air(); Step(true); Attached(); yield return FindFinalFrameExpansion(true, false);
            var trial = finalFrameCase;
            var authority = RunFinalFrameCase(trial, trial.Initial, true);
            var replay = RunFinalFrameCase(trial, trial.Final, false);
            Assert.That(replay.Equals(authority), Is.True);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Wall));
            Assert.That(state.Position.Equals(trial.Initial.Position), Is.True); Assert.That(state.PaperCenter.Equals(trial.Initial.PaperCenter), Is.True);
            Assert.That(state.PaperRotation.Equals(trial.Initial.PaperRotation), Is.True);
            Assert.That(state.PaperAnimationTime, Is.EqualTo(trial.Initial.PaperAnimationTime));
            Assert.That(state.PaperMove.Equals(trial.Initial.PaperMove), Is.True); Assert.That(state.Velocity.Equals(Vector3.zero), Is.True);
            Assert.That(state.Yaw, Is.Not.EqualTo(trial.Initial.Yaw)); Assert.That(state.Pitch, Is.Not.EqualTo(trial.Initial.Pitch));
            Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision)); Assert.That(state.FriendlyInkContact, Is.True);
            AssertFinalFramePublication();
        }

        [UnityTest] public IEnumerator FinalKeyStationaryWallRejectsOnlyTheBlockedAnimationTransactionWhileTimeAdvances()
        {
            yield return Publish(Wall()); Air(); Step(true); Attached(); yield return FindFinalFrameExpansion(true, true);
            var trial = finalFrameCase;
            var authority = RunStationaryFinalFrameHistory(trial, trial.Initial, true);
            var replay = RunStationaryFinalFrameHistory(trial, trial.Final, false);
            Assert.That(replay.Length, Is.EqualTo(authority.Length));
            for (int i = 0; i < authority.Length; i++) Assert.That(replay[i].Equals(authority[i]), Is.True,
                "Every complete raw snapshot matches per-step publication versus unsampled replay");
            trial.Obstacle.enabled = false; Physics.SyncTransforms(); Step(true);
            Assert.That(state.PaperAnimationTime, Is.GreaterThan(trial.Initial.PaperAnimationTime), "Removing the obstacle resumes the actual animation");
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); AssertFinalFramePublication();
        }

        [UnityTest] public IEnumerator FinalKeyPublishedTerrainInvalidatesThePriorFrameAndForcesARealSafeDetach()
        {
            yield return Publish(Wall()); Air(); Step(true); Attached(); yield return FindFinalFrameExpansion(true, true);
            var trial = finalFrameCase; var changed = Wall();
            var preview = Box("Preview of exactly one forthcoming published voxel", Vector3.zero, BlockWorld.CellSize);
            bool found = false; Vector3Int cell = default;
            foreach (var rect in trial.InitialRects.OrderByDescending(r => r.center.y))
            {
                Vector3 point = trial.Initial.PaperCenter + trial.Initial.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
                cell = new Vector3Int(Mathf.FloorToInt(point.x / BlockWorld.CellSize), Mathf.FloorToInt(point.y / BlockWorld.CellSize), Mathf.FloorToInt(point.z / BlockWorld.CellSize));
                if (changed.TryGet(cell, out _)) continue;
                preview.transform.position = BlockWorld.Center(cell); Physics.SyncTransforms();
                if (!CapsuleOverlaps(trial.Initial) && FinalFrameOracleOverlaps(trial.Initial, trial.InitialRects)) { found = true; break; }
            }
            Assert.That(found, Is.True, "A real voxel must invalidate the prior paper frame while its motor capsule stays clear");
            preview.enabled = false; changed.Set(cell, BlockCell.Create(2, true));
            uint oldRevision = terrain.Revision; yield return Publish(changed);
            Assert.That(terrain.Revision, Is.GreaterThan(oldRevision));
            Assert.That(terrain.PublishedTraversalPoseFits(player, trial.Initial), Is.False, "The old frame is invalid in the actual newly published collider");
            Assert.That(CapsuleOverlaps(trial.Initial), Is.False);
            var authority = RunFinalFrameCase(trial, trial.Initial, true);
            var replay = RunFinalFrameCase(trial, trial.Final, false);
            Assert.That(replay.Equals(authority), Is.True);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.SupportTerrainRevision, Is.Zero);
            Assert.That(state.FriendlyInkContact, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            AssertFinalFramePublication();
        }

    }
}
#endif
