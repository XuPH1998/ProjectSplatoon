#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
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

// SpecialSafety repair02 candidate: actual platform-edge entry and paused-through-Leave cleanup.
// This revision has zero compiled/executed passes; preserve SpecialSafety01/02 failures.
// Real match.Blocks/TrainingGround/NGO, four legal .30m layers; no legacy seed.
// Effect state is explicit damage/impulse setup, not paid Special launch evidence.
namespace Splatoon.Tests
{
    [Category("BlockFixedGridSpecialImpulseSafety")]
    public sealed class BlockFixedGridSpecialImpulseSafetyPlayTests
    {
        const float Dt = 1f / 60;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        PrototypeApp app;
        PrototypeMatch match;
        PrototypePlayer attacker, victim, replay;
        BlockTerrain terrain;
        Action serverTick;
        bool tickDetached, priorMatchEnabled, caseComplete, schedulingPaused;
        readonly Dictionary<PrototypePlayer, bool> priorPlayerEnabled = new();
        readonly List<PrototypePlayer.ReplayDependencyObservation> dependencies = new();
        System.IO.StreamWriter evidence;
        byte[] preparedWorld;
        int motorObserverErrors, dependencyObserverErrors;
        int cursorWritesBeforeConnect = -1;
        static Rect[] FreshIndependentRects(PaperBodyProfile profile, PlayerSnapshot sample)
        {
            using var fresh = new PaperCapture(profile);
            Assert.That(fresh.SampleUncachedForDiagnostics(sample), Is.True, "Every independent sample starts from a fresh capture and bypasses SharedPoses");
            return fresh.HitRects.ToArray();
        }
        static void AssertCursorIsolationBeforeBoot()
        {
            var suppressed = typeof(PrototypeApp).GetProperty("SystemCursorWritesSuppressed", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(suppressed, Is.Not.Null);
            Assert.That((bool)suppressed.GetValue(null), Is.True,
                "Runner must suppress all system cursor writes before Boot: SPLATOON_DISABLE_SYSTEM_CURSOR=1 / -noSystemCursor / batch mode");
        }
        void AssertCursorWritesUnchanged()
        {
            if (app != null && cursorWritesBeforeConnect >= 0)
                Assert.That(app.SystemCursorWriteCount, Is.EqualTo(cursorWritesBeforeConnect),
                    "Automated fixture must never write the shared system cursor, including releases");
        }
        BoxCollider obstacle;
        PlayerMotorSimulation observedMotor;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> observations = new();

        static object Call(PrototypePlayer player, string name, params object[] args) =>
            typeof(PrototypePlayer).GetMethod(name, Private).Invoke(player, args);
        static void Set(PrototypePlayer player, string name, object value) =>
            typeof(PrototypePlayer).GetField(name, Private).SetValue(player, value);
        static PlayerMotorSimulation Motor(PrototypePlayer player) =>
            (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(player);
        static List<PlayerInputFrame> History(PrototypePlayer player) =>
            (List<PlayerInputFrame>)typeof(PrototypePlayer).GetField("_history", Private).GetValue(player);
        static PlayerSnapshot Predicted(PrototypePlayer player) =>
            (PlayerSnapshot)typeof(PrototypePlayer).GetField("_predicted", Private).GetValue(player);

        IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end)
            { PrototypeApp.Current?.CaptureMouse(false); yield return null; }
            Assert.That(ready(), Is.True, label);
        }

        [UnityTest, Timeout(600000)] public IEnumerator TripleInkstrikeAirPaperRejectsPostMotorSilhouetteOnlyImpulseAndReplaysExactly()
        {
            AssertCursorIsolationBeforeBoot();
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario(false);
            AssertCursorWritesUnchanged();
            // UnityTearDown owns Leave/ExitPlayMode on success and failure.
            // Authority/player scheduling stays paused until shutdown completes.
        }

        [UnityTest, Timeout(600000)] public IEnumerator TripleInkstrikeWallPaperRejectsPostMotorSilhouetteOnlyImpulseAndReplaysExactly()
        {
            AssertCursorIsolationBeforeBoot();
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario(true);
            AssertCursorWritesUnchanged();
            // UnityTearDown owns Leave/ExitPlayMode on success and failure.
            // Authority/player scheduling stays paused until shutdown completes.
        }

        void Observe(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
        { if (source == observedMotor) observations.Add(observation); }

        PlayerSnapshot Outer(PrototypePlayer player, PlayerSnapshot origin, PlayerInputFrame input,
            out PlayerSnapshot postMotor)
        {
            AssertFixedDependency(origin);
            observedMotor = Motor(player); observations.Clear();
            observedMotor.Restore(origin, false);
            object[] args = { origin, input, Dt, origin.SimulatedAt + 1d / 60, MatchPhase.Practice };
            Assert.That((bool)Call(player, "Step", args), Is.False, "No main paid attack is requested");
            Assert.That(observations.Count(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete), Is.EqualTo(1),
                "A rejected post-motor impulse cannot rerun the motor or its animation");
            postMotor = observations.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete).After;
            var result = (PlayerSnapshot)args[0];
            Record("outer", origin, result, input, player.IsServer ? "authority" : "owner");
            AssertFixedDependency(result);
            return result;
        }

        bool CapsuleOverlaps(PlayerSnapshot s, bool shrink = true)
        {
            var shape = HeroBodyShape.For(s.HeroId);
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = s.Position + Vector3.up * (height * .5f);
            float radius = shape.Radius - (shrink ? Mathf.Max(.005f, shape.SkinWidth) : 0);
            return DataCapsuleOverlaps(center, segment, radius) ||
                Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment,
                radius,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }

        bool CapsuleSweepBlocked(PlayerSnapshot s, Vector3 destination)
        {
            var shape = HeroBodyShape.For(s.HeroId);
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = s.Position + Vector3.up * (height * .5f);
            Vector3 a = center - Vector3.up * segment, b = center + Vector3.up * segment, delta = destination - s.Position;
            return CapsuleOverlaps(s, false) || DataCapsuleSweep(center, segment, shape.Radius, delta) ||
                delta.sqrMagnitude > .000000000001f &&
                Physics.CapsuleCast(a, b, shape.Radius, delta.normalized, out _, delta.magnitude,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }

        bool PaperOverlaps(PlayerSnapshot s, IReadOnlyList<Rect> rects, PaperBodyProfile profile)
        {
            foreach (var rect in rects)
                if (BoxOverlaps(s.PaperCenter + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                    new Vector3(rect.width * .5f, rect.height * .5f, profile.Thickness * .5f), s.PaperRotation,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }

        sealed class LiveStamp
        {
            readonly SwimBody body;
            readonly PaperCapture capture;
            readonly int version;
            readonly IReadOnlyList<Rect> rects;
            readonly Rect[] values;
            readonly Mesh mesh;
            readonly Vector3[] vertices;
            readonly int[] triangles;
            readonly Vector3 hitPosition, renderPosition;
            readonly Quaternion hitRotation, renderRotation;
            readonly Bounds bounds;
            readonly RenderTexture texture;
            readonly uint textureUpdate;
            readonly bool hitEnabled, rendererEnabled;
            readonly Mesh renderMesh;
            readonly Material material;
            readonly Texture baseMap;

            public LiveStamp(SwimBody source)
            {
                body = source; capture = body.Capture; Assert.That(capture, Is.Not.Null);
                version = capture.PoseVersion; rects = body.HitRects; values = rects.ToArray();
                mesh = body.HitVolume.sharedMesh; Assert.That(mesh, Is.Not.Null);
                vertices = mesh.vertices; triangles = mesh.triangles; bounds = capture.PosedBounds;
                hitPosition = body.HitVolume.transform.localPosition; hitRotation = body.HitVolume.transform.localRotation;
                renderPosition = body.BodyRenderer.transform.localPosition; renderRotation = body.BodyRenderer.transform.localRotation;
                texture = capture.Texture; Assert.That(texture, Is.Not.Null); textureUpdate = texture.updateCount;
                hitEnabled = body.HitVolume.enabled; rendererEnabled = body.BodyRenderer.enabled;
                renderMesh = body.BodyRenderer.GetComponent<MeshFilter>().sharedMesh;
                material = body.BodyRenderer.sharedMaterial;
                var block = new MaterialPropertyBlock(); body.BodyRenderer.GetPropertyBlock(block);
                baseMap = block.GetTexture("_BaseMap");
            }
            public void AssertUnchanged()
            {
                Assert.That(body.Capture, Is.SameAs(capture)); Assert.That(capture.PoseVersion, Is.EqualTo(version));
                Assert.That(body.HitRects, Is.SameAs(rects)); CollectionAssert.AreEqual(values, body.HitRects);
                Assert.That(body.HitVolume.sharedMesh, Is.SameAs(mesh));
                CollectionAssert.AreEqual(vertices, mesh.vertices); CollectionAssert.AreEqual(triangles, mesh.triangles);
                Assert.That(capture.PosedBounds, Is.EqualTo(bounds));
                Assert.That(body.HitVolume.transform.localPosition, Is.EqualTo(hitPosition));
                Assert.That(body.HitVolume.transform.localRotation, Is.EqualTo(hitRotation));
                Assert.That(body.BodyRenderer.transform.localPosition, Is.EqualTo(renderPosition));
                Assert.That(body.BodyRenderer.transform.localRotation, Is.EqualTo(renderRotation));
                Assert.That(capture.Texture, Is.SameAs(texture)); Assert.That(texture.updateCount, Is.EqualTo(textureUpdate));
                Assert.That(body.HitVolume.enabled, Is.EqualTo(hitEnabled));
                Assert.That(body.BodyRenderer.enabled, Is.EqualTo(rendererEnabled));
                Assert.That(body.BodyRenderer.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(renderMesh));
                Assert.That(body.BodyRenderer.sharedMaterial, Is.SameAs(material));
                var block = new MaterialPropertyBlock(); body.BodyRenderer.GetPropertyBlock(block);
                Assert.That(block.GetTexture("_BaseMap"), Is.SameAs(baseMap));
            }
        }

        void AssertLivePublication(PrototypePlayer player, PlayerSnapshot s)
        {
            var body = player.SwimBody;
            Assert.That(player.transform.position.Equals(s.Position), Is.True);
            Assert.That(CapsuleOverlaps(s), Is.False);
            Assert.That(terrain.PublishedTraversalPoseFits(player, s), Is.True);
            using var oracle = new PaperCapture(body.Profile); oracle.SampleUncachedForDiagnostics(s);
            Assert.That(PaperOverlaps(s, oracle.HitRects, body.Profile), Is.False);
            CollectionAssert.AreEqual(oracle.HitRects, body.HitRects);
            Assert.That(body.HitVolume.sharedMesh, Is.SameAs(body.Capture.HitMesh));
            CollectionAssert.AreEqual(oracle.HitMesh.vertices, body.HitVolume.sharedMesh.vertices);
            CollectionAssert.AreEqual(oracle.HitMesh.triangles, body.HitVolume.sharedMesh.triangles);
            Assert.That(body.HitVolume.sharedMesh.bounds, Is.EqualTo(oracle.HitMesh.bounds));
            Assert.That(body.Capture.PosedBounds, Is.EqualTo(oracle.PosedBounds));
            Assert.That(body.HitVolume.transform.position, Is.EqualTo(s.PaperCenter));
            Assert.That(Quaternion.Angle(body.HitVolume.transform.rotation, s.PaperRotation), Is.LessThan(.0001f));
        }

        bool FindSilhouetteOnlyObstacle(PlayerSnapshot initial, PlayerSnapshot postMotor, PlayerSnapshot unsafeEnd,
            Rect[] initialRects, Rect[] postMotorRects, Rect[] futureRects, PaperBodyProfile profile)
        {
            // Independent real authored samples choose the distinguishing point;
            // the production candidate query only confirms the chosen witness.
            for (int y = PaperSilhouette.Rows - 2; y > 0; y--)
            for (int x = 1; x < PaperSilhouette.Columns - 1; x++)
            {
                var point = new Vector2(((x + .5f) / PaperSilhouette.Columns - .5f) * profile.Size.x,
                    ((y + .5f) / PaperSilhouette.Rows - .5f) * profile.Size.y);
                var square = new Rect(point - Vector2.one * .005f, Vector2.one * .01f);
                if (!futureRects.Any(r => r.xMin < square.xMin && r.xMax > square.xMax &&
                    r.yMin < square.yMin && r.yMax > square.yMax)) continue;
                obstacle.transform.SetPositionAndRotation(unsafeEnd.PaperCenter + unsafeEnd.PaperRotation * new Vector3(point.x, point.y, 0),
                    unsafeEnd.PaperRotation);
                obstacle.enabled = true; Physics.SyncTransforms();
                if (CapsuleOverlaps(initial) || CapsuleOverlaps(postMotor) || CapsuleOverlaps(unsafeEnd) ||
                    CapsuleSweepBlocked(postMotor, unsafeEnd.Position) || PaperOverlaps(initial, initialRects, profile) ||
                    PaperOverlaps(postMotor, postMotorRects, profile) || !PaperOverlaps(unsafeEnd, futureRects, profile)) continue;
                Assert.That(terrain.PublishedTraversalPoseFits(victim, postMotor), Is.True);
                Assert.That(terrain.PublishedTraversalPoseFits(victim, unsafeEnd), Is.False);
                Record("silhouette-only-witness", postMotor, unsafeEnd, default,
                    $"raster={x},{y}; square={square}; obstacle={obstacle.transform.position}; initial/full capsule, native+data sweep, full old papers clear");
                return true;
            }
            obstacle.enabled = false; Physics.SyncTransforms(); return false;
        }


        // The native mask excludes fixed occupancy. This independent reference
        // enumerates every committed cube; it never calls production fixed casts,
        // overlap helpers or PublishedTraversalPoseFits to select a witness.
        bool BoxOverlaps(Vector3 center, Vector3 half, Quaternion rotation, int mask, QueryTriggerInteraction triggers)
        {
            if (Physics.CheckBox(center, half, rotation, mask, triggers)) return true;
            var axes = new[] { rotation * Vector3.right, rotation * Vector3.up, rotation * Vector3.forward };
            foreach (var block in terrain.FixedBlocks.Blocks.Values)
                if (CubeObb(BlockWorld.Center(block.Cell), BlockWorld.CellSize * .5f, center, half, axes)) return true;
            return false;
        }
        static bool CubeObb(Vector3 cube, float cubeHalf, Vector3 center, Vector3 half, Vector3[] axes)
        {
            var candidates = new List<Vector3> { Vector3.right, Vector3.up, Vector3.forward };
            candidates.AddRange(axes);
            foreach (var worldAxis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                foreach (var axis in axes) candidates.Add(Vector3.Cross(worldAxis, axis));
            foreach (var axis in candidates)
            {
                if (axis.sqrMagnitude < 1e-12f) continue;
                float cubeRadius = cubeHalf * (Mathf.Abs(axis.x) + Mathf.Abs(axis.y) + Mathf.Abs(axis.z));
                float paperRadius = half.x * Mathf.Abs(Vector3.Dot(axes[0], axis)) +
                    half.y * Mathf.Abs(Vector3.Dot(axes[1], axis)) + half.z * Mathf.Abs(Vector3.Dot(axes[2], axis));
                if (Mathf.Abs(Vector3.Dot(center - cube, axis)) > cubeRadius + paperRadius + .0000001f) return false;
            }
            return true;
        }
        bool DataCapsuleOverlaps(Vector3 center, float segment, float radius)
        {
            foreach (var block in terrain.FixedBlocks.Blocks.Values)
                if (CapsuleCubeDistanceSquared(center, segment, block.Cell) < radius * radius - .0000001f) return true;
            return false;
        }
        static float CapsuleCubeDistanceSquared(Vector3 center, float segment, Vector3Int cell)
        {
            var bounds = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize + Vector3.up * (2 * segment));
            return (center - bounds.ClosestPoint(center)).sqrMagnitude;
        }
        bool DataCapsuleSweep(Vector3 center, float segment, float radius, Vector3 delta)
        {
            foreach (var block in terrain.FixedBlocks.Blocks.Values)
            {
                float lo = 0, hi = 1;
                // Convex distance along a segment: include both endpoints and a
                // bounded 64-iteration minimum, independently of the production TOI.
                for (int i = 0; i < 64; i++)
                {
                    float a = (2 * lo + hi) / 3, b = (lo + 2 * hi) / 3;
                    if (CapsuleCubeDistanceSquared(center + delta * a, segment, block.Cell) <
                        CapsuleCubeDistanceSquared(center + delta * b, segment, block.Cell)) hi = b; else lo = a;
                }
                float squared = Mathf.Min(CapsuleCubeDistanceSquared(center, segment, block.Cell),
                    Mathf.Min(CapsuleCubeDistanceSquared(center + delta, segment, block.Cell),
                    CapsuleCubeDistanceSquared(center + delta * ((lo + hi) * .5f), segment, block.Cell)));
                if (squared <= radius * radius + .0000001f) return true;
            }
            return false;
        }
        void AssertIndependentPose(PlayerSnapshot state)
        {
            var shape = HeroBodyShape.For(state.HeroId);
            float height = state.Swimming || state.CompactBody ? shape.CompactHeight : shape.Height;
            Assert.That(state.Position.y, Is.GreaterThanOrEqualTo(terrain.World.Bottom));
            Assert.That(state.Position.y + height, Is.LessThanOrEqualTo(BlockWorld.Top));
            Assert.That(CapsuleOverlaps(state), Is.False, "Actual authored map plus every committed fixed cube");
            // Human approach has a full standing capsule and no published sheet.
            // Every actual paper/wall/effect state still validates its complete authored contour.
            if (!state.ShowsSwimBody) return;
            using var oracle = new PaperCapture(victim.SwimBody.Profile); oracle.SampleUncachedForDiagnostics(state);
            Assert.That(oracle.HitRects.Count, Is.GreaterThan(4), "Keep the full real authored silhouette");
            Assert.That(PaperOverlaps(state, oracle.HitRects, victim.SwimBody.Profile), Is.False);
        }
        static BlockFixedGridHandle SupportHandle(PlayerSnapshot state) => new()
        {
            Epoch = state.FixedBlockEpoch, Round = state.FixedBlockRound,
            Id = state.SupportFixedId, Generation = state.SupportFixedGeneration
        };
        void ReadActualWorldDependency(ref PlayerSnapshot state)
        {
            // Snapshot preparation copies a real committed view. No readiness
            // flag, missing dependency or support identity is fabricated.
            state.FixedBlockEpoch = terrain.FixedBlocks.Epoch;
            state.FixedBlockRound = terrain.FixedBlocks.Round;
            state.FixedBlockRevision = terrain.FixedBlocks.Revision;
            state.SupportIsFixedGrid = state.SupportIsSurfacePaint = false;
            state.SupportFixedId = 0; state.SupportFixedGeneration = state.SupportTerrainRevision = 0;
            state.SupportCell = default;
            state.SupportSurfaceFace = state.SupportSurfaceU = state.SupportSurfaceV = 0;
        }
        void AssertFixedDependency(PlayerSnapshot state)
        {
            Assert.That(BlockTerrain.Current, Is.SameAs(terrain)); Assert.That(match.Blocks, Is.SameAs(terrain));
            Assert.That(state.FixedBlockEpoch, Is.EqualTo(terrain.FixedBlocks.Epoch));
            Assert.That(state.FixedBlockRound, Is.EqualTo(terrain.FixedBlocks.Round));
            Assert.That(state.FixedBlockRevision, Is.EqualTo(terrain.FixedBlocks.Revision));
            if (!state.SupportIsFixedGrid) return;
            Assert.That(terrain.FixedBlocks.TryGet(SupportHandle(state), out var block), Is.True);
            Assert.That(block.Cell, Is.EqualTo(state.SupportCell));
            Assert.That(block.Revision, Is.EqualTo(state.SupportTerrainRevision));
            Assert.That(block.Owner, Is.EqualTo(state.Team));
        }
        void ObserveDependency(PrototypePlayer player, PrototypePlayer.ReplayDependencyObservation observation)
        { if (player == replay) dependencies.Add(observation); } // Copy only; no reentry from observer.

        RaycastHit MapColumnHit(Vector2Int column)
        {
            var center = BlockWorld.Center(new Vector3Int(column.x, 0, column.y));
            var hits = Physics.RaycastAll(new Vector3(center.x, BlockWorld.Top + 1, center.z), Vector3.down,
                BlockWorld.Top + 1 - terrain.World.Bottom, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)
                .Where(h => terrain.IsAuthoredMapCollider(h.collider)).OrderBy(h => h.distance).ToArray();
            Assert.That(hits.Length, Is.GreaterThan(0), "Actual TrainingGround support must exist");
            Assert.That(hits[0].normal.y, Is.GreaterThan(.9999f));
            return hits[0];
        }
        (float edgeX, float mapSupport, float approachFloor, float centerZ) BuildActualFourLayerPlatformEdge(byte team)
        {
            Assert.That(terrain.FixedBlocks.Count, Is.Zero);
            // The same existing authored platform used by passed Gameplay02 T03/T04.
            // No added collider/platform, raised floor, map transform or taller stack.
            const float edgeX = 3f, centerZ = -7.95f;
            var map = MapColumnHit(new Vector2Int(10, -27));
            Assert.That(map.collider.name, Is.EqualTo("CenterCover_1_-1"));
            Assert.That(map.point.y, Is.EqualTo(2.2f).Within(.002f));
            Assert.That(map.collider.bounds.min.x, Is.EqualTo(edgeX).Within(.002f));
            Assert.That(Physics.Raycast(new Vector3(edgeX - .52f, 1, centerZ), Vector3.down,
                out var approach, 2, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(terrain.IsAuthoredMapCollider(approach.collider), Is.True);
            Assert.That(approach.point.y, Is.EqualTo(0).Within(.002f));

            var cells = new List<Vector3Int>();
            for (int x = 10; x <= 15; x++) for (int z = -31; z <= -23; z++)
            {
                var hit = MapColumnHit(new Vector2Int(x, z));
                Assert.That(hit.collider, Is.SameAs(map.collider));
                Assert.That(hit.point.y, Is.EqualTo(map.point.y).Within(.00001f));
                Assert.That(terrain.TryGetSurfacePaint(hit.collider, hit.point, hit.normal, out var paint), Is.True,
                    "Every original platform column needs its complete authored paint square");
                if (paint.Owner != team)
                    Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, team, 1, true).Added, Is.EqualTo(1));
                Assert.That(terrain.SurfacePaints.TryGet(paint.Key, out var owned) && owned.Owner == team, Is.True);
                int baseY = BlockFixedGridMapSupport.GridBaseY(hit.point.y);
                for (int layer = 0; layer < BlockFixedGridWorld.MaximumStackLayers; layer++)
                    cells.Add(new Vector3Int(x, baseY + layer, z));
            }
            var result = terrain.FixedBlocks.Build(cells.OrderBy(c => c.y).ThenBy(c => c.x).ThenBy(c => c.z), team, cells.Count);
            Assert.That(cells.Count, Is.EqualTo(6 * 9 * 4));
            Assert.That(result.Added, Is.EqualTo(cells.Count), terrain.FixedBlocks.LastFailure);
            Assert.That(terrain.FixedBlocks.Count, Is.EqualTo(cells.Count));
            foreach (var cell in cells)
            {
                Assert.That(terrain.FixedBlocks.TryGet(cell, out var block), Is.True);
                Assert.That(block.Handle.IsValid && block.Owner == team, Is.True);
                Assert.That(block.Support.Height, Is.EqualTo(map.point.y).Within(.00001f));
                Assert.That(block.Cell.y - block.Support.BaseCellY, Is.InRange(0, 3));
                Assert.That(BlockFixedGridWorld.GetBounds(block).max.y - block.Support.Height, Is.LessThanOrEqualTo(1.20001f));
                Assert.That(BlockFixedGridWorld.GetBounds(block).max.y, Is.LessThanOrEqualTo(BlockWorld.Top));
            }
            Physics.SyncTransforms();
            Record("four-layer-platform-edge-committed", default, default, default,
                $"cells={cells.Count}; original={map.collider.name}; support={map.point.y}; edge={edgeX}; original approach y={approach.point.y}");
            return (edgeX, map.point.y, approach.point.y, centerZ);
        }
        PlayerSnapshot PrepareActualWall(PlayerSnapshot template, ref PlayerInputFrame input, byte team)
        {
            var site = BuildActualFourLayerPlatformEdge(team);
            var candidate = template;
            float gridBase = BlockFixedGridMapSupport.GridBaseY(site.mapSupport) * BlockWorld.CellSize;
            candidate.Position = new Vector3(site.edgeX - .52f, gridBase + .02f, site.centerZ);
            candidate.Yaw = candidate.BodyYaw = 90; candidate.Pitch = 0;
            candidate.Movement = MovementMode.Air; candidate.Grounded = false;
            candidate.Swimming = candidate.CompactBody = candidate.SwimWasHeld = candidate.FriendlyInkContact = false;
            candidate.Velocity = candidate.PlanarVelocity = Vector3.zero; candidate.VerticalSpeed = 0;
            candidate.SwimSource = candidate.AirSwimSource = SwimSurface.None;
            candidate.PaperPose = PaperPose.None; candidate.PaperCenter = Vector3.zero;
            candidate.PaperRotation = Quaternion.identity; candidate.AirHumanOffset = candidate.CameraRebaseOffset = 0;
            candidate.WeaponPhase = WeaponPhase.Idle; candidate.SubPhase = SubWeaponPhase.Idle;
            candidate.AttackNeedsRelease = candidate.SubNeedsRelease = candidate.Firing = candidate.ChargeReleasePending = false;
            candidate.AttackMoveUntil = candidate.AttackRecoveryUntil = candidate.SubRecoveryUntil = candidate.InkRecoverAt = candidate.SubReadyAt = 0;
            ReadActualWorldDependency(ref candidate);
            Motor(victim).Restore(candidate); victim.Snapshot.Value = candidate; Physics.SyncTransforms();
            AssertIndependentPose(candidate);
            Assert.That(CapsuleOverlaps(candidate, false), Is.False, "Full standing capsule is clear outside the original platform edge");
            var shape = HeroBodyShape.For(candidate.HeroId);
            var hero = GameplayConfig.GetHero(candidate.HeroId);
            Assert.That(PlayerMotorSimulation.Query(candidate.Position + Vector3.up * (shape.CompactHeight * .5f),
                Vector3.right, hero.WallProbeDistance, out var initialContact), Is.True);
            Assert.That(initialContact.Kind, Is.EqualTo(BlockContactKind.FixedGrid));
            Assert.That(initialContact.Owner, Is.EqualTo(team)); Assert.That(initialContact.Point.x, Is.EqualTo(site.edgeX).Within(.002f));
            var approach = input; approach.Swim = true; approach.Move = Vector2.up; approach.Look = new Vector2(90, 0);
            Record("ordinary-platform-edge-start", candidate, candidate, approach,
                "Human Air start, original Gameplay02 geometry; no injected WallInk, paper pose or support handle");
            for (int tick = 0; tick < 90; tick++)
            {
                var before = candidate; approach.Sequence++; approach.Tick++;
                candidate = Outer(victim, candidate, approach, out _);
                victim.Snapshot.Value = candidate; Physics.SyncTransforms();
                Record("ordinary-platform-edge-approach", before, candidate, approach, $"tick={tick}; real outer Step");
                AssertIndependentPose(candidate);
                if (candidate.Movement != MovementMode.WallInk || candidate.PaperPose != PaperPose.Wall) continue;
                Assert.That(candidate.ShowsSwimBody && candidate.SupportIsFixedGrid, Is.True);
                Assert.That(candidate.HasInkRecovery && candidate.FriendlyInkContact && candidate.SwimSource == SwimSurface.Friendly, Is.True);
                AssertFixedDependency(candidate);
                Assert.That(PlayerMotorSimulation.Query(candidate.Position + Vector3.up * (shape.CompactHeight * .5f) + candidate.WallNormal * .08f,
                    -candidate.WallNormal, hero.WallProbeDistance + .08f, out var contact), Is.True);
                Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.FixedGrid));
                Assert.That(contact.FixedHandle, Is.EqualTo(SupportHandle(candidate)));
                Assert.That(contact.Stable && contact.Climbable && contact.Owner == team, Is.True);
                Assert.That(contact.Point.x, Is.EqualTo(site.edgeX).Within(.002f));
                Assert.That(Vector3.Dot(candidate.WallNormal, Vector3.left), Is.GreaterThan(.999f));
                Assert.That(observations.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved && e.ProbeSucceeded), Is.True,
                    "Actual ordinary wall attachment branch must have succeeded");
                input = approach; victim.SwimBody.ApplyCollision(candidate); Physics.SyncTransforms();
                AssertIndependentPose(candidate); return candidate;
            }
            Assert.Fail("PRECONDITION BLOCKED: original platform-edge four-layer wall did not produce ordinary WallInk within 90 real outer Steps. No injected wall state allowed.");
            return default;
        }

        // PlayerSnapshot is intentionally not marked Serializable in production.
        // JsonUtility previously omitted before/after entirely; keep a test-local
        // explicit diagnostic DTO while raw runtime equality assertions remain intact.
        [Serializable] sealed class SnapshotEvidence
        {
            public Vector3 position, velocity, planarVelocity, paperCenter, wallPoint, wallNormal, specialImpulse;
            public Quaternion paperRotation;
            public float yaw, pitch, verticalSpeed, airHumanOffset, paperAnimationTime, health, ink, impulseRemaining;
            public Vector2 paperMove;
            public int movement, paperPose, swimSource, airSwimSource;
            public bool grounded, swimming, compact, friendly, fixedSupport;
            public uint simulationTick, life, epoch, round, revision, supportGeneration;
            public ulong supportId;
            public double simulatedAt;
            public SnapshotEvidence(PlayerSnapshot s)
            {
                position = s.Position; velocity = s.Velocity; planarVelocity = s.PlanarVelocity;
                paperCenter = s.PaperCenter; paperRotation = s.PaperRotation; wallPoint = s.WallPoint; wallNormal = s.WallNormal;
                yaw = s.Yaw; pitch = s.Pitch; verticalSpeed = s.VerticalSpeed; airHumanOffset = s.AirHumanOffset;
                paperAnimationTime = s.PaperAnimationTime; paperMove = s.PaperMove; health = s.Health; ink = s.Ink;
                movement = (int)s.Movement; paperPose = (int)s.PaperPose; swimSource = (int)s.SwimSource; airSwimSource = (int)s.AirSwimSource;
                grounded = s.Grounded; swimming = s.Swimming; compact = s.CompactBody; friendly = s.FriendlyInkContact;
                fixedSupport = s.SupportIsFixedGrid; simulationTick = s.SimulationTick; life = s.Revision;
                epoch = s.FixedBlockEpoch; round = s.FixedBlockRound; revision = s.FixedBlockRevision;
                supportId = s.SupportFixedId; supportGeneration = s.SupportFixedGeneration; simulatedAt = s.SimulatedAt;
                specialImpulse = s.SpecialImpulseVelocity; impulseRemaining = s.SpecialImpulseRemaining;
            }
        }

        [Serializable] sealed class StageEvidence
        {
            public string stage, detail, unity, sourcePath, sourceSha256, mapPath, mapSha256;
            public int terrainObject, fixedCount, paintCount;
            public uint epoch, round, revision, surfaceRevision;
            public SnapshotEvidence before, after;
            public PlayerInputFrame input;
            public Vector3 transformPosition, controllerCenter;
            public bool controllerEnabled;
            public float controllerHeight, controllerRadius, cellSize, worldTop;
        }
        void Record(string stage, PlayerSnapshot before, PlayerSnapshot after, PlayerInputFrame input = default, string detail = null)
        {
            if (evidence == null) return;
            var controller = victim != null ? victim.GetComponent<CharacterController>() : null;
            var row = new StageEvidence { stage = stage, detail = detail, unity = Application.unityVersion,
                before = new SnapshotEvidence(before), after = new SnapshotEvidence(after), input = input, terrainObject = terrain != null ? terrain.GetInstanceID() : 0,
                epoch = terrain?.FixedBlocks?.Epoch ?? 0, round = terrain?.Round ?? 0, revision = terrain?.Revision ?? 0,
                surfaceRevision = terrain?.SurfacePaints?.Revision ?? 0, fixedCount = terrain?.FixedBlocks?.Count ?? 0,
                paintCount = (terrain?.SurfacePaints?.PinkCount ?? 0) + (terrain?.SurfacePaints?.BlueCount ?? 0),
                transformPosition = victim != null ? victim.transform.position : default,
                controllerEnabled = controller != null && controller.enabled, controllerCenter = controller != null ? controller.center : default,
                controllerHeight = controller != null ? controller.height : 0, controllerRadius = controller != null ? controller.radius : 0,
                cellSize = BlockWorld.CellSize, worldTop = BlockWorld.Top };
            if (stage == "begin")
            {
                row.sourcePath = "Assets/Splatoon/Tests/BlockFixedGridSpecialImpulseSafetyPlayTests.cs";
                row.mapPath = "Assets/GameResource/Gameplay/maps/TrainingGround.unity";
                using var hash = System.Security.Cryptography.SHA256.Create();
                Assert.That(System.IO.File.Exists(row.sourcePath), Is.True, "Install the reviewed draft under its exact source identity");
                row.sourceSha256 = BitConverter.ToString(hash.ComputeHash(System.IO.File.ReadAllBytes(row.sourcePath))).Replace("-", "").ToLowerInvariant();
                row.mapSha256 = BitConverter.ToString(hash.ComputeHash(System.IO.File.ReadAllBytes(row.mapPath))).Replace("-", "").ToLowerInvariant();
            }
            evidence.WriteLine(JsonUtility.ToJson(row)); evidence.Flush();
        }

        IEnumerator Scenario(bool wall)
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            app = PrototypeApp.Current;
            cursorWritesBeforeConnect = app.SystemCursorWriteCount;
            Assert.That(cursorWritesBeforeConnect, Is.Zero, "Suppressed Boot must already have zero system cursor writes");
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            AssertCursorWritesUnchanged();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks != null, "Actual NGO block host");
            app.CaptureMouse(false); match = PrototypeMatch.Current; attacker = PrototypePlayer.Local; terrain = match.Blocks;
            Assert.That(match.IsServer && attacker.IsSpawned, Is.True, "Real NGO host and player");
            Assert.That(terrain, Is.SameAs(BlockTerrain.Current)); Assert.That(terrain.FixedGridMode, Is.True);
            Assert.That(terrain.Dynamic, Is.Null); Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f));
            Assert.That(BlockFixedGridWorld.MaximumStackLayers, Is.EqualTo(4)); Assert.That(BlockWorld.Top, Is.EqualTo(8));
            Assert.That(match.InitialSyncComplete && match.FixedGridBlockSyncReady, Is.True);
            Assert.That(GameplayConfig.Global.SimulationRate, Is.EqualTo(60), "Use the actual configured dt for authority/replay raw equality");
            string output = System.IO.Path.Combine("Reports/BlockSimulation20261018/Staged65/Acceptance/Special/Runs",
                (wall ? "Wall-" : "Air-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
            evidence = new System.IO.StreamWriter(output, false, new System.Text.UTF8Encoding(false));
            caseComplete = false; motorObserverErrors = PlayerMotorSimulation.MotorBranchObserverErrors;
            dependencyObserverErrors = PrototypePlayer.ReplayDependencyProviderErrors;
            Record("begin", default, default, default, wall ? "wall; same-process semantics; not paid Special launch" : "air; same-process semantics; not paid Special launch");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            victim = match.AddTestBot(prefab); Assert.That(victim, Is.Not.Null, match.TestBotMessage);
            Assert.That(victim.IsServer && victim.IsSpawned && victim.NetworkObject.IsSpawned, Is.True);
            priorMatchEnabled = match.enabled; match.enabled = false; schedulingPaused = true;
            priorPlayerEnabled.Clear();
            foreach (var player in match.Players) { priorPlayerEnabled.Add(player, player.enabled); player.enabled = false; }
            serverTick = (Action)Delegate.CreateDelegate(typeof(Action), match, typeof(PrototypeMatch).GetMethod("ServerTick", Private));
            match.NetworkManager.NetworkTickSystem.Tick -= serverTick; tickDetached = true;
            PlayerMotorSimulation.MotorBranchObserved += Observe;
            PrototypePlayer.ReplayDependencyObserved += ObserveDependency;
            try
            {
                match.Projectiles.Clear(); match.SubWeapons.Clear(); match.SpecialWeapons.Clear(); BlockAttackService.Clear();
                terrain.Clear(match.State.Value.Round);
                Assert.That(terrain.FixedBlocks.Count, Is.Zero); Assert.That(terrain.PendingTransactions, Is.Zero);
                Assert.That(terrain.FailedTransactions, Is.Zero);
                byte team = (byte)(3 - attacker.Snapshot.Value.Team);
                double now = match.NetworkManager.ServerTime.Time + 10;
                Physics.SyncTransforms();

                var initial = victim.Snapshot.Value;
                initial.HeroId = 1; initial.HeroRevision++; initial.Team = team; initial.Health = 100; initial.Ink = 43;
                initial.LifeState = PlayerLifeState.Alive; initial.ProtectedUntil = 0; initial.LastDamageAt = 0;
                initial.SubWeaponId = SubWeaponConfigService.Current.DefaultId(1); initial.SpecialWeaponId = 1;
                initial.SpecialPhase = SpecialPhase.Charging; initial.SpecialPoints = 0; initial.SpecialRemaining = 0;
                initial.SpecialShotAt = 0; initial.SpecialImpulseVelocity = Vector3.zero; initial.SpecialImpulseRemaining = 0;
                initial.Position = new Vector3(BlockWorld.CellSize * .5f, 2.2f, -13);
                initial.Movement = MovementMode.Air; initial.Swimming = true; initial.CompactBody = false; initial.Grounded = false;
                initial.AirHumanOffset = .87f; initial.CameraRebaseOffset = -.2f; initial.VerticalSpeed = -.1f;
                initial.Velocity = Vector3.down * .1f; initial.PlanarVelocity = Vector3.zero; initial.GroundNormal = Vector3.up;
                initial.SwimSource = initial.AirSwimSource = SwimSurface.Friendly; initial.SwimWasHeld = true;
                initial.PaperPose = PaperPose.Air; initial.PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 90);
                initial.PaperAnimationTime = .13f; initial.PaperMove = Vector2.zero; initial.SimulatedAt = now;
                Call(victim, "EnsureHeroPresentation", initial.HeroId);
                initial.PaperCenter = initial.Position + Vector3.up * victim.SwimBody.Profile.SurfaceOffset;
                ReadActualWorldDependency(ref initial);
                var input = new PlayerInputFrame { Swim = true, Move = wall ? Vector2.up : Vector2.zero, Look = Vector2.zero,
                    Sequence = initial.AcknowledgedInput + 1, Tick = 100, Revision = initial.Revision, HeroRevision = initial.HeroRevision,
                    SpecialSequence = initial.SpecialConsumed, FireSequence = initial.ConsumedFire, ReleaseSequence = initial.ConsumedRelease,
                    SubPressSequence = initial.SubConsumedPress, SubReleaseSequence = initial.SubConsumedRelease, JumpSequence = initial.ConsumedJump };
                if (wall) initial = PrepareActualWall(initial, ref input, team);
                else
                {
                    Motor(victim).Restore(initial); Physics.SyncTransforms();
                    Assert.That(initial.PaperPose, Is.EqualTo(PaperPose.Air));
                    AssertIndependentPose(initial);
                }
                now = initial.SimulatedAt;
                AssertFixedDependency(initial);
                input.Move = Vector2.zero; input.Look = new Vector2(37, -11);
                victim.Snapshot.Value = initial; Motor(victim).Restore(initial); Physics.SyncTransforms();
                Assert.That(CapsuleOverlaps(initial), Is.False);

                // Use the real loaded TripleInkstrike entity/service, real server
                // ReceiveDamage and real ReceiveSpecialKnockback. Do not inject a
                // sentinel impulse into the snapshot or invoke the impulse helper.
                var owner = attacker.Snapshot.Value; owner.SpecialWeaponId = 2; owner.SpecialAction++;
                attacker.Snapshot.Value = owner;
                var tornado = match.SpecialWeapons.Spawn(attacker, owner, now, match.State.Value.Round);
                // The real tick also performs its authored 5.2419m structural
                // blast. Keep it enabled. A high centre in the real 8m world
                // plus a 5m wall-tangent offset keeps the original-platform fixed wall outside that sphere while the
                // victim stays inside the authored downward damage cylinder.
                float effectY = BlockWorld.Top - BlockWorld.CellSize * 2;
                Vector3 impulseAxis = wall ? Vector3.Cross(initial.WallNormal, Vector3.up).normalized : Vector3.right;
                Assert.That(Mathf.Abs(impulseAxis.y), Is.LessThan(.000001f));
                Assert.That(impulseAxis.sqrMagnitude, Is.EqualTo(1).Within(.000001f));
                float effectOffset = wall ? 5f : .5f;
                tornado.State.Position = initial.Position - impulseAxis * effectOffset;
                tornado.State.Position.y = effectY;
                Assert.That(effectY, Is.LessThanOrEqualTo(BlockWorld.Top));
                Assert.That(effectY - initial.Position.y, Is.InRange(0, tornado.Config.P.damageHeightDown));
                Assert.That(effectOffset, Is.LessThanOrEqualTo(tornado.Config.P.innerRadius + GameplayConfig.GetHero(initial.HeroId).BodyRadius),
                    "The real fully expanded damage cylinder must cover the victim");
                foreach (var block in terrain.FixedBlocks.Blocks.Values)
                {
                    float padding = BlockWorld.CellSize * .5f + .001f;
                    Assert.That(Vector3.Distance(BlockWorld.Center(block.Cell), tornado.State.Position),
                        Is.GreaterThan(tornado.Config.P.paintRadius + padding), "Real blast must not invalidate the required fixed-wall precondition");
                }
                preparedWorld = BlockFixedGridCodec.Encode(terrain.FixedBlocks.Capture(true));
                Record("before-real-service", initial, initial, input, $"effect={tornado.State.Position}; full blast retained; radius={tornado.Config.P.paintRadius}");
                tornado.State.Phase = SpecialEntityPhase.Active; tornado.State.Changed = now - tornado.Config.P.expandSeconds;
                tornado.State.Expires = now + tornado.Config.P.effectDuration;
                match.SpecialWeapons.Step(now, Dt, match.Players);
                var beforeDamage = initial;
                initial = victim.Snapshot.Value; initial.RequiredPaintSequence = match.PaintSequence;
                Record("after-real-service", beforeDamage, initial, input, "Real ReceiveDamage/ReceiveSpecialKnockback and real structural Blast");
                CollectionAssert.AreEqual(preparedWorld, BlockFixedGridCodec.Encode(terrain.FixedBlocks.Capture(true)),
                    "The real service is not allowed to erase/rebuild the four-layer fixture behind the comparison");
                AssertFixedDependency(initial); AssertIndependentPose(initial);
                Assert.That(initial.Health, Is.EqualTo(100 - tornado.Config.P.damagePerSecond * 5f / 60).Within(.0001f));
                Assert.That(initial.IsAlive && initial.ShowsSwimBody, Is.True, "The real nonlethal tick leaves the original paper active");
                Assert.That(initial.PaperPose, Is.EqualTo(wall ? PaperPose.Wall : PaperPose.Air));
                Assert.That(Vector3.Dot(initial.SpecialImpulseVelocity, impulseAxis), Is.GreaterThan(0),
                    "The real service impulse must follow the chosen unobstructed horizontal wall tangent (or air +X)");
                Assert.That(initial.SpecialImpulseRemaining, Is.EqualTo(tornado.Config.P.knockbackDistance));
                match.SpecialWeapons.Clear(); // No second damage tick or terrain publication during this movement comparison.

                obstacle = new GameObject("Post-motor authored silhouette-only impulse witness").AddComponent<BoxCollider>();
                obstacle.size = Vector3.one * .004f; obstacle.enabled = false;
                var noImpulse = initial; noImpulse.SpecialImpulseVelocity = Vector3.zero; noImpulse.SpecialImpulseRemaining = 0;
                var ordinaryFinal = Outer(victim, noImpulse, input, out var ordinaryPostMotor);
                var unsafeFinal = Outer(victim, initial, input, out var postMotor);
                Assert.That(postMotor.Position.Equals(ordinaryPostMotor.Position), Is.True);
                Assert.That(postMotor.PaperCenter.Equals(ordinaryPostMotor.PaperCenter), Is.True);
                Assert.That(postMotor.PaperAnimationTime, Is.EqualTo(ordinaryPostMotor.PaperAnimationTime));
                Assert.That(Vector3.Distance(unsafeFinal.Position, postMotor.Position), Is.GreaterThan(.01f), "Actual unobstructed product impulse moves");
                var profile = victim.SwimBody.Profile;
                var initialRects = FreshIndependentRects(profile, initial);
                var postMotorRects = FreshIndependentRects(profile, postMotor);
                var futureRects = FreshIndependentRects(profile, unsafeFinal);
                Assert.That(FindSilhouetteOnlyObstacle(initial, postMotor, unsafeFinal, initialRects, postMotorRects, futureRects, profile), Is.True,
                    "Coverage requires a real authored future contour blocked outside the clear capsule and native impulse sweep; never skip the scenario");

                Vector3 requested = Vector3.ClampMagnitude(initial.SpecialImpulseVelocity * Dt, initial.SpecialImpulseRemaining);
                var expected = ordinaryFinal;
                expected.SpecialImpulseVelocity = initial.SpecialImpulseVelocity * Mathf.Pow(initial.SpecialImpulseRetention, Dt * 60);
                expected.SpecialImpulseRetention = initial.SpecialImpulseRetention;
                expected.SpecialImpulseRemaining = Mathf.Max(0, initial.SpecialImpulseRemaining - requested.magnitude);
                var authority = Outer(victim, initial, input, out var finalPostMotor);
                Assert.That(authority.Equals(expected), Is.True,
                    "Retain every post-motor movement/key/paid-action/resource field, then consume/decay the attempted impulse exactly once");
                Assert.That(finalPostMotor.Position.Equals(postMotor.Position), Is.True);
                Assert.That(authority.SimulationTick, Is.EqualTo(initial.SimulationTick + 1));
                Assert.That(authority.SimulatedAt, Is.EqualTo(initial.SimulatedAt + 1d / 60));
                AssertLivePublication(victim, authority);
                var authorityMotor = observations.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete);
                CollectionAssert.AreEqual(preparedWorld, BlockFixedGridCodec.Encode(terrain.FixedBlocks.Capture(true)));

                // The replay runs against the same static collision world, not
                // against an extra overlapping copy of its authority capsule.
                victim.GetComponent<CharacterController>().enabled = false;
                replay = UnityEngine.Object.Instantiate(prefab).GetComponent<PrototypePlayer>(); replay.enabled = false;
                replay.gameObject.layer = 8;
                Assert.That(replay.IsServer || replay.IsSpawned, Is.False, "A genuine unspawned owner prediction fixture has no server publication branch");
                Call(replay, "EnsureHeroPresentation", initial.HeroId);
                var conflictingLiveSeed = initial; conflictingLiveSeed.PaperAnimationTime += .43f; conflictingLiveSeed.PaperMove = Vector2.right;
                replay.SwimBody.ApplyCollision(conflictingLiveSeed);
                // Establish an actual texture/material/render baseline before the
                // read-only motor/replay interval. ApplyCollision only samples hit
                // geometry and cannot create the fresh capture's RenderTexture.
                replay.SwimBody.Present(conflictingLiveSeed, Vector3.zero, Quaternion.identity);
                Assert.That(replay.SwimBody.Capture.Texture, Is.Not.Null);
                var stamp = new LiveStamp(replay.SwimBody);
                Set(replay, "_predicted", initial); History(replay).Clear(); History(replay).Add(input);
                Assert.That(History(replay).Count, Is.EqualTo(1));
                Assert.That(input.Sequence, Is.GreaterThan(initial.AcknowledgedInput));
                Assert.That(match.InitialSyncComplete && match.FixedGridBlockSyncReady, Is.True);
                AssertFixedDependency(initial);
                observedMotor = Motor(replay); observations.Clear(); dependencies.Clear();
                Call(replay, "ReconcilePrediction", initial, false, match.InitialSyncComplete, match.PaintSequence, MatchPhase.Practice);
                Assert.That(replay.TerrainReplayPending, Is.False); Assert.That(replay.LastReplaySteps, Is.EqualTo(1));
                Assert.That(observations.Count(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete), Is.EqualTo(1));
                Assert.That(Predicted(replay).Equals(authority), Is.True, "Compare every raw snapshot field after real historical outer-Step replay");
                var replayMotor = observations.Single(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete);
                Assert.That(replayMotor.Before.Equals(authorityMotor.Before), Is.True, "Exact pre-motor snapshot of the one historical input");
                Assert.That(replayMotor.After.Equals(authorityMotor.After), Is.True, "Exact post-motor snapshot, not only final position");
                Assert.That(dependencies.Count, Is.EqualTo(1));
                var dependency = dependencies.Single();
                Assert.That(dependency.Source, Is.EqualTo(2)); Assert.That(dependency.State.Equals(initial), Is.True);
                Assert.That(dependency.Ready && dependency.TerrainAvailable && !dependency.Boundary, Is.True);
                Assert.That(dependency.After.TerrainObjectId, Is.EqualTo(terrain.GetInstanceID()));
                Assert.That(dependency.After.FixedWorldAvailable, Is.True);
                Assert.That(dependency.After.FixedReplayEpoch, Is.EqualTo(terrain.FixedBlocks.Epoch));
                Assert.That(dependency.After.FixedRequiredRevision, Is.EqualTo(initial.FixedBlockRevision));
                Assert.That(dependency.After.FixedAppliedRevision, Is.EqualTo(terrain.FixedBlocks.Revision));
                Assert.That(dependency.After.TerrainRound, Is.EqualTo(terrain.Round));
                var query = replay.InspectFixedQueryDependency();
                Assert.That(query.Last.Valid && query.Last.SameWorld && query.Last.Completed, Is.True);
                Assert.That(query.Last.Source.Ready && query.Last.Gate.Ready, Is.True);
                Assert.That(query.Last.WorldBegin.TerrainObjectId, Is.EqualTo(terrain.GetInstanceID()));
                Assert.That(query.Last.WorldBegin.Revision, Is.EqualTo(terrain.FixedBlocks.Revision));
                Record("owner-replay-one-input", initial, Predicted(replay), input,
                    $"source={dependency.Source}; epoch={dependency.After.FixedReplayEpoch}; required={dependency.After.FixedRequiredRevision}; applied={dependency.After.FixedAppliedRevision}; queryValid={query.Last.Valid}");
                stamp.AssertUnchanged();
                Assert.That(replay.transform.position.Equals(authority.Position), Is.True);
                Assert.That(terrain.PublishedTraversalPoseFits(replay, Predicted(replay)), Is.True);
                stamp.AssertUnchanged(); // Candidate queries also cannot publish the live capture.
                replay.SwimBody.ApplyCollision(Predicted(replay)); Physics.SyncTransforms();
                AssertLivePublication(replay, Predicted(replay));
                CollectionAssert.AreEqual(preparedWorld, BlockFixedGridCodec.Encode(terrain.FixedBlocks.Capture(true)));
                Assert.That(PlayerMotorSimulation.MotorBranchObserverErrors, Is.EqualTo(motorObserverErrors));
                Assert.That(PrototypePlayer.ReplayDependencyProviderErrors, Is.EqualTo(dependencyObserverErrors));
                caseComplete = true; Record("complete", initial, Predicted(replay), input);
            }
            finally
            {
                Record(caseComplete ? "scenario-finished" : "scenario-aborted-before-completion", default, default,
                    default, "Raw assertion/exception is owned by UTF; an aborted case is not a skip or pass");
                CleanupOwned();
            }
        }

        void CleanupOwned()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe;
            PrototypePlayer.ReplayDependencyObserved -= ObserveDependency;
            if (obstacle != null) { obstacle.enabled = false; UnityEngine.Object.Destroy(obstacle.gameObject); obstacle = null; }
            if (replay != null) { replay.gameObject.SetActive(false); UnityEngine.Object.Destroy(replay.gameObject); replay = null; }
            if (match != null)
            {
                // Idempotent owned-resource cleanup never resumes a live host.
                // The authority capsule is deliberately disabled during replay.
                // Keep both Match.FixedUpdate and player Update paused through
                // the asynchronous real Leave/Session.Shutdown, including failure cleanup.
                if (schedulingPaused)
                {
                    match.enabled = false;
                    foreach (var prior in priorPlayerEnabled) if (prior.Key != null) prior.Key.enabled = false;
                }
                match.Projectiles.Clear(); match.SubWeapons.Clear(); match.SpecialWeapons.Clear();
            }
            BlockAttackService.Clear(); app?.CaptureMouse(false);
            evidence?.Dispose(); evidence = null;
            AssertCursorWritesUnchanged();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            CleanupOwned();
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave("SpecialSafety repair02 cleanup").ToCoroutine();
            Assert.That(PrototypeApp.Current == null || !PrototypeApp.Current.InRoom, Is.True, "Leave must complete before releasing paused-scheduling bookkeeping");
            Assert.That(match == null || match.NetworkManager == null || !match.NetworkManager.IsListening, Is.True,
                "A still-listening host must never resume with the replay authority controller disabled");
            // These test-owned spawned objects are now disconnected/destroyed.
            // Do not re-add their ServerTick handler or re-enable old components.
            tickDetached = false; serverTick = null; schedulingPaused = false; priorMatchEnabled = false;
            priorPlayerEnabled.Clear(); match = null; victim = attacker = null;
            AssertCursorWritesUnchanged();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
#endif
