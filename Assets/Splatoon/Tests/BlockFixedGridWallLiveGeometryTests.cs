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
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Fixed-grid live wall regression; compile and runtime results are recorded separately.
    // Real map support, mutation-committed fixed occupancy, authored Paper and
    // ordinary Motor.Step. This is not a paid attack or independent Player test.
    [Category("BlockFixedGridWallLiveGeometry")]
    public sealed class BlockFixedGridWallLiveGeometryTests
    {
        const float Dt = 1f / 60;
        const int WallCellZ = 3;
        const float WallFront = WallCellZ * BlockWorld.CellSize;
        const string SideSupportName = "65 twelfth-case real side support";
        static bool NeedsSideSupport => TestContext.CurrentContext.Test.MethodName ==
            nameof(FinalKeyCommittedFixedOccupancyInvalidatesThePriorFrameBeforeMeshPublication);
        BlockTerrain terrain;
        BoxCollider mapFloor;
        PrototypePlayer player;
        CharacterController controller;
        MotorGroundContactRecorder recorder;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> events = new();
        SwimBody Body => player.SwimBody;

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Author the complete real arena before Play Mode. In particular,
            // adding an incomplete PrototypeArena after config startup invokes
            // Awake/InitializeRuntime before SpawnPoints/topology are ready.
            HeroMigrationTests.Load();
            var arenaRoot = new GameObject("65 authored fixed wall arena");
            var authoredArena = arenaRoot.AddComponent<PrototypeArena>();
            authoredArena.OwnershipCellSize = GameplayConfig.Map.CellSize;
            authoredArena.SpawnPoints = new Transform[8];
            for (int i = 0; i < 8; i++)
            {
                var spawn = new GameObject("65 spawn " + i).transform;
                spawn.SetParent(arenaRoot.transform, false);
                spawn.position = new Vector3(i % 4 * 2 - 3, .04f, i < 4 ? -20 : 20);
                authoredArena.SpawnPoints[i] = spawn;
            }
            var floorRoot = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floorRoot.name = "65 real map support"; floorRoot.transform.SetParent(arenaRoot.transform, false);
            floorRoot.transform.position = Vector3.down * .7f;
            // Transform scale stays one; the authored solid is exactly 8x.2x8m.
            var mesh = Object.Instantiate(floorRoot.GetComponent<MeshFilter>().sharedMesh);
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], new Vector3(8, .2f, 8));
            mesh.vertices = vertices; mesh.RecalculateBounds();
            floorRoot.GetComponent<MeshFilter>().sharedMesh = mesh;
            floorRoot.GetComponent<BoxCollider>().size = new Vector3(8, .2f, 8);
            var paint = floorRoot.AddComponent<PaintSurface>();
            paint.SurfaceId = 6501; paint.Scores = true; paint.WalkableSize = new Vector2(8, 8);
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
            Assert.That(atlas, Is.Not.Null); paint.ShapeAtlas = atlas;
            // A genuine raised map pad supports the wall at y=0. The approach
            // ground remains y=-.6, so the authored 1.8m paper can rotate below
            // the four-layer wall top without embedding its bottom into a floor.
            var padRoot = GameObject.CreatePrimitive(PrimitiveType.Cube); padRoot.name = "65 raised map support";
            padRoot.transform.SetParent(arenaRoot.transform, false);
            // Anchor the near edge directly to the global grid. Center minus half
            // depth loses one float ULP and excludes the first complete paint row.
            padRoot.transform.position = new Vector3(0, -.3f, WallFront);
            var padSize = new Vector3(8, .3f, 4 - WallFront);
            var padCenter = new Vector3(0, .15f, padSize.z * .5f);
            var padMesh = Object.Instantiate(padRoot.GetComponent<MeshFilter>().sharedMesh);
            var padVertices = padMesh.vertices;
            for (int i = 0; i < padVertices.Length; i++) padVertices[i] = Vector3.Scale(padVertices[i], padSize) + padCenter;
            padMesh.vertices = padVertices; padMesh.RecalculateBounds(); padRoot.GetComponent<MeshFilter>().sharedMesh = padMesh;
            var padPaint = padRoot.AddComponent<PaintSurface>(); padPaint.SurfaceId = 6502; padPaint.ShapeAtlas = atlas;
            padRoot.GetComponent<BoxCollider>().size = padSize;
            padRoot.GetComponent<BoxCollider>().center = padCenter;
            Assert.That(padRoot.transform.TransformPoint(padCenter - padSize * .5f).z, Is.EqualTo(WallFront));
            // Only the committed-occupancy case needs a side wing above its
            // compact capsule. Author this real map solid before runtime capture;
            // do not register synthetic support or add a post-initialize collider.
            if (NeedsSideSupport) AuthorSideSupport(arenaRoot.transform, atlas);
            authoredArena.BakedTopology = authoredArena.ComputeTopology();
            yield return new EnterPlayMode();
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            HeroMigrationTests.Load();
            var arena = GameObject.Find("65 authored fixed wall arena").GetComponent<PrototypeArena>();
            arena.InitializeRuntime();
            Assert.That(arena.Surfaces.Count, Is.EqualTo(NeedsSideSupport ? 3 : 2));
            Assert.That(arena.TotalArea, Is.GreaterThan(0));
            Assert.That(arena.Surfaces[6501].Ownership, Is.Not.Null);
            mapFloor = GameObject.Find("65 real map support").GetComponent<BoxCollider>();
            terrain = new GameObject("65 fixed silhouette wall terrain").AddComponent<BlockTerrain>();
            terrain.Initialize(arena); terrain.EnableFixedGridMode(); terrain.Tick(true, 10, Dt);
            Assert.That(terrain.Initialized && terrain.FixedGridMode && terrain.Dynamic == null, Is.True);
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f));
            Assert.That(BlockWorld.Top, Is.EqualTo(8f));
            Assert.That(BlockFixedGridWorld.MaximumStackLayers, Is.EqualTo(4));
            Assert.That(BlockFixedGridWorld.MaximumStackHeight, Is.EqualTo(1.2f));
            Assert.That(terrain.IsAuthoredMapCollider(mapFloor), Is.True);
            Assert.That(terrain.IsAuthoredMapCollider(GameObject.Find("65 raised map support").GetComponent<BoxCollider>()), Is.True);
            if (NeedsSideSupport)
            {
                var side = GameObject.Find(SideSupportName).GetComponent<BoxCollider>();
                Assert.That(terrain.IsAuthoredMapCollider(side), Is.True);
                Assert.That(arena.Surfaces[6503].Ownership, Is.Not.Null);
                var sideHit = MapColumnHit(new Vector2Int(-2, 2));
                Assert.That(sideHit.collider, Is.SameAs(side));
                Assert.That(sideHit.point.y, Is.EqualTo(2 * BlockWorld.CellSize).Within(.000001f));
                Assert.That(BlockFixedGridMapSupport.GridBaseY(sideHit.point.y), Is.EqualTo(2));
                Assert.That(terrain.TryGetSurfacePaint(side, sideHit.point, sideHit.normal, out var sidePaint), Is.True,
                    "The exact .30 x .30 top must register a complete legal paint square");
                Assert.That(sidePaint.Owner, Is.Zero);
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            player = Object.Instantiate(prefab).GetComponent<PrototypePlayer>(); player.gameObject.layer = 8;
            Body.Bind(PaperBodyTests.Profile("RifleGirl"));
            Assert.That(Body.Profile, Is.Not.Null); Assert.That(Body.Profile.CapturePrefab, Is.Not.Null);
            controller = player.GetComponent<CharacterController>();
            recorder = player.gameObject.AddComponent<MotorGroundContactRecorder>();
            motor = new PlayerMotorSimulation(controller);
            PlayerMotorSimulation.MotorBranchObserved += Observe;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe;
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static void AuthorSideSupport(Transform parent, Texture2D atlas)
        {
            float cell = BlockWorld.CellSize;
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = SideSupportName; root.transform.SetParent(parent, false);
            // World X=[-2c,-c], Z=[2c,3c], top=2c. Keep scale one and
            // local minimum X/Z exactly zero; keep local top Y exactly zero.
            // This avoids center-minus-half cancellation at complete-square edges.
            root.transform.position = new Vector3(-2 * cell, 2 * cell, 2 * cell);
            var size = new Vector3(cell, 4 * cell, cell);
            var center = new Vector3(cell * .5f, -size.y * .5f, cell * .5f);
            var mesh = Object.Instantiate(root.GetComponent<MeshFilter>().sharedMesh);
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size) + center;
            mesh.vertices = vertices; mesh.RecalculateBounds(); root.GetComponent<MeshFilter>().sharedMesh = mesh;
            var collider = root.GetComponent<BoxCollider>(); collider.size = size; collider.center = center;
            var paint = root.AddComponent<PaintSurface>(); paint.SurfaceId = 6503; paint.Scores = true;
            paint.WalkableSize = new Vector2(cell, cell); paint.ShapeAtlas = atlas;
            var minTop = root.transform.TransformPoint(center + new Vector3(-size.x, size.y, -size.z) * .5f);
            var maxTop = root.transform.TransformPoint(center + size * .5f);
            Assert.That(minTop.x, Is.EqualTo(-2 * cell)); Assert.That(maxTop.x, Is.EqualTo(-cell));
            Assert.That(minTop.z, Is.EqualTo(2 * cell)); Assert.That(maxTop.z, Is.EqualTo(3 * cell));
            Assert.That(minTop.y, Is.EqualTo(2 * cell)); Assert.That(maxTop.y, Is.EqualTo(2 * cell));
        }
        void AssertSideSupportClearsUnshrunkCapsule(PlayerSnapshot pose)
        {
            Assert.That(NeedsSideSupport, Is.True);
            var side = GameObject.Find(SideSupportName).GetComponent<BoxCollider>();
            var shape = HeroBodyShape.For(pose.HeroId);
            float height = pose.Swimming || pose.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            var capsuleCenter = pose.Position + Vector3.up * (height * .5f);
            // Independent vertical-segment/AABB distance; use the full authored
            // capsule radius here, with no skin/profile reduction.
            var bounds = new Bounds(side.transform.TransformPoint(side.center), side.size + Vector3.up * (2 * segment));
            float separation = Vector3.Distance(capsuleCenter, bounds.ClosestPoint(capsuleCenter)) - shape.Radius;
            Assert.That(separation, Is.GreaterThan(.002f),
                "The real support pedestal must leave the full ordinary capsule clear");
            Debug.Log("[WALL65 SIDE SUPPORT] capsuleClearance=" + Raw(separation)
                + ",mapCenter=" + Raw(side.transform.TransformPoint(side.center)) + ",mapSize=" + Raw(side.size));
        }
        void Observe(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
        { if (source == motor) events.Add(observation); }
        static Vector3Int[] Wall()
        {
            var cells = new List<Vector3Int>();
            int left = Mathf.FloorToInt(-.8f / BlockWorld.CellSize);
            int right = Mathf.CeilToInt(.9f / BlockWorld.CellSize);
            for (int x = left; x < right; x++)
                for (int y = 0; y < BlockFixedGridWorld.MaximumStackLayers; y++)
                    cells.Add(new Vector3Int(x, y, WallCellZ));
            return cells.ToArray();
        }
        RaycastHit MapColumnHit(Vector2Int column)
        {
            var center = BlockWorld.Center(new Vector3Int(column.x, 0, column.y));
            var hits = Physics.RaycastAll(new Vector3(center.x, 7, center.z), Vector3.down, 16,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)
                .Where(h => terrain.IsAuthoredMapCollider(h.collider)).OrderBy(h => h.distance).ToArray();
            Assert.That(hits.Length, Is.GreaterThan(0), "Actual map supplies the first finite support, not a fake environment");
            var hit = hits[0];
            Assert.That(hit.normal, Is.EqualTo(Vector3.up));
            return hit;
        }
        void PaintMapColumn(Vector2Int column, byte owner)
        {
            var hit = MapColumnHit(column);
            Assert.That(terrain.TryGetSurfacePaint(hit.collider, hit.point, hit.normal, out var paint), Is.True,
                $"Complete paint square at column {column}, map={hit.collider.name}, point={hit.point:R}, normal={hit.normal:R}");
            if (paint.Owner == owner) return;
            Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, owner, 1, true).Added, Is.EqualTo(1));
            Assert.That(terrain.SurfacePaints.TryGet(paint.Key, out var committed) && committed.Owner == owner, Is.True);
        }
        void Publish(IReadOnlyCollection<Vector3Int> cells)
        {
            // Deliberately no snapshot import, QueueSeed, native fixed collider,
            // post-commit terrain Tick, mesh wait or mask/layer override.
            uint revision = terrain.Revision;
            if (cells.Count == 0)
            {
                var handles = terrain.FixedBlocks.Blocks.Values.Select(s => s.Handle).ToArray();
                var result = terrain.FixedBlocks.Destroy(handles, 2);
                Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.EqualTo(handles.Length));
                foreach (var handle in handles) Assert.That(terrain.FixedBlocks.TryGet(handle, out _), Is.False);
                Assert.That(terrain.FixedBlocks.Count, Is.Zero);
            }
            else
            {
                Assert.That(terrain.FixedBlocks.Count, Is.Zero, "Fixture publication starts from a real empty fixed world");
                foreach (var column in cells.Select(c => new Vector2Int(c.x, c.z)).Distinct()) PaintMapColumn(column, 1);
                var ordered = cells.OrderBy(c => c.y).ThenBy(c => c.x).ThenBy(c => c.z).ToArray();
                var result = terrain.FixedBlocks.Build(ordered, 1, ordered.Length);
                Assert.That(result.Added, Is.EqualTo(ordered.Length), terrain.FixedBlocks.LastFailure);
                Assert.That(terrain.FixedBlocks.Count, Is.EqualTo(ordered.Length));
                foreach (var cell in ordered)
                {
                    Assert.That(terrain.FixedBlocks.TryGet(cell, out var block), Is.True);
                    Assert.That(block.Cell, Is.EqualTo(cell)); Assert.That(block.Owner, Is.EqualTo(1));
                    Assert.That(block.Handle.IsValid, Is.True);
                    Assert.That(block.Cell.y - block.Support.BaseCellY, Is.InRange(0, 3));
                    Assert.That((block.Cell.y + 1) * BlockWorld.CellSize - block.Support.Height, Is.LessThanOrEqualTo(1.20001f));
                }
            }
            if (cells.Count != 0) Assert.That(terrain.Revision, Is.GreaterThan(revision));
            Physics.SyncTransforms();
            return;
        }
        void Air()
        {
            state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1,
                Health = 100, Ink = 43, LifeState = PlayerLifeState.Alive,
                Position = new Vector3(.05f, .74f, WallFront - .52f), Movement = MovementMode.Air,
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
            state.FixedBlockEpoch = terrain.FixedBlocks.Epoch; state.FixedBlockRound = terrain.Round;
            state.FixedBlockRevision = terrain.Revision;
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
            candidate.Position.z = WallFront - controller.radius - .015f;
            candidate.Movement = MovementMode.WallInk; candidate.Swimming = true;
            candidate.CompactBody = false; candidate.Grounded = false; candidate.AirHumanOffset = 0;
            candidate.WallNormal = Vector3.back; candidate.WallPoint = new Vector3(candidate.Position.x, candidate.Position.y, WallFront);
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
                if (BoxOverlaps(s.PaperCenter + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                    new Vector3(rect.width * .5f, rect.height * .5f, Body.Profile.Thickness * .5f), s.PaperRotation,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }
        bool CapsuleOverlaps(PlayerSnapshot s)
        {
            var shape = HeroBodyShape.For(s.HeroId);
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = s.Position + Vector3.up * (height * .5f);
            float radius = shape.Radius - Mathf.Max(.005f, shape.SkinWidth);
            return DataCapsuleOverlaps(center, segment, radius) ||
                Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment,
                    radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
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
            // Vertical line segment vs cube = point vs a cube expanded only in Y.
            var bounds = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize + Vector3.up * (2 * segment));
            return (center - bounds.ClosestPoint(center)).sqrMagnitude;
        }
        bool DataCapsuleSweep(Vector3 center, float segment, float radius, Vector3 delta)
        {
            foreach (var block in terrain.FixedBlocks.Blocks.Values)
            {
                float lo = 0, hi = 1;
                // Squared distance to a convex expanded AABB along a segment is
                // convex. Independent bounded minimization includes endpoints.
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

        void Attached(bool neutralMap = false)
        {
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(PlayerMotorSimulation.Query(state.Position + Vector3.up * (controller.height * .5f),
                -state.WallNormal, controller.radius + .065f, out var contact), Is.True);
            Assert.That(contact.Stable && contact.Climbable, Is.True); Assert.That(contact.IsBlock, Is.EqualTo(!neutralMap));
            Assert.That(contact.Owner, Is.EqualTo(neutralMap ? (byte)0 : state.Team));
            if (!neutralMap)
            {
                Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.FixedGrid));
                Assert.That(terrain.FixedBlocks.TryGet(contact.FixedHandle, out var supported), Is.True);
                Assert.That(supported.Cell, Is.EqualTo(contact.Cell));
                Assert.That(state.SupportIsFixedGrid, Is.True);
                Assert.That(state.SupportFixedId, Is.EqualTo(contact.FixedHandle.Id));
                Assert.That(state.SupportFixedGeneration, Is.EqualTo(contact.FixedHandle.Generation));
            }
            Assert.That(contact.Revision, Is.EqualTo(neutralMap ? 0u : terrain.Revision));
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
            Publish(Wall()); Air();
            DifferentContours(out var blocked, out var hole, out _);
            var size = Body.Profile.Size;
            Assert.That(BoxOverlaps(blocked.PaperCenter, new Vector3(size.x * .5f, size.y * .5f, Body.Profile.Thickness * .5f),
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
            yield break;
        }

        [Test] public void CandidateContourCacheQuantizesEvictsRecomputesAndInvalidatesAuthoredShape()
        {
            var sample = new PlayerSnapshot { PaperAnimationTime = .101f, PaperMove = new Vector2(.2f, -.2f) };
            var retained = Body.QueryTraversalRects(sample); var frozen = retained.ToArray();
            Assert.That(Body.Capture, Is.Null, "A candidate lookup must not promote its private evaluator to live capture");
            var evaluator = (PaperCapture)typeof(SwimBody).GetField("_traversalCapture", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).GetValue(Body);
            Assert.That(evaluator.HitMesh, Is.Null, "Traversal uses a contour-only stage without an unused hit Mesh");
            Assert.That(evaluator.Texture, Is.Null);
            var same = sample; same.PaperAnimationTime = .108f; same.PaperMove = new Vector2(.24f, -.24f);
            Assert.That(Body.QueryTraversalRects(same), Is.SameAs(retained), "Exactly the 30 Hz/eighth-move key is shared");
            for (int i = 1; i <= 63; i++)
            { var later = sample; later.PaperAnimationTime = (3 + i + .25f) / PaperAnimation.FramesPerSecond; Body.QueryTraversalRects(later); }
            long sharedHits = PaperCapture.SharedPoseCacheHits;
            Assert.That(Body.QueryTraversalRects(sample), Is.SameAs(retained), "The first entry remains at exactly 64 local keys");
            Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(sharedHits), "A local hit does not look up the shared cache or refresh FIFO order");
            var overflow = sample; overflow.PaperAnimationTime = (3 + 64 + .25f) / PaperAnimation.FramesPerSecond;
            Body.QueryTraversalRects(overflow);
            sharedHits = PaperCapture.SharedPoseCacheHits;
            var recomputed = Body.QueryTraversalRects(sample);
            Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(sharedHits + 1), "The 65th key evicts the oldest local FIFO entry even after a read");
            Assert.That(recomputed, Is.SameAs(retained), "Reuse the immutable shared contour without a second rectangle copy");
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
            Publish(Wall()); Air(); var initial = state;
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
            yield break;
        }

        [UnityTest] public IEnumerator FirstAttachRejectsARealPaperOnlyUpperWingObstructionAndKeepsTheAirOrigin()
        {
            Publish(Wall()); Air();
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
            yield break;
        }

        [UnityTest] public IEnumerator HeldWallMovementStopsBeforeARealUpperSilhouetteObstructionWithoutDetaching()
        {
            Publish(Wall()); Air(); Step(true); Attached();
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
            yield break;
        }

        [UnityTest] public IEnumerator ARealLiveSilhouetteHoleAllowsTheOrdinaryWallAttachment()
        {
            Publish(Wall()); Air(); var candidate = WallCandidate();
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
            Assert.That(BoxOverlaps(candidate.PaperCenter, new Vector3(size.x * .5f, size.y * .5f, Body.Profile.Thickness * .5f),
                candidate.PaperRotation, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True,
                "A bounding rectangle would reject this obstacle even though every live rectangle is clear");
            Assert.That(terrain.PublishedTraversalPoseFits(player, candidate), Is.True);
            int sampledVersion = Body.Capture.PoseVersion; Step(); Attached();
            Assert.That(Body.Capture.PoseVersion, Is.EqualTo(sampledVersion));
            Assert.That(events.Any(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved && e.ProbeSucceeded), Is.True);
            Body.ApplyCollision(state); Physics.SyncTransforms(); Assert.That(PaperOverlaps(state), Is.False);
            yield break;
        }

        [UnityTest] public IEnumerator RemovingCommittedFixedWallInvalidatesHandlesAndDetachesOnTheNextStep()
        {
            Publish(Wall()); Air(); Step(true); Attached();
            uint oldRevision = state.SupportTerrainRevision;
            Assert.That(terrain.World.PinkCount, Is.GreaterThan(0));
            Publish(Array.Empty<Vector3Int>());
            Assert.That(terrain.Revision, Is.GreaterThan(oldRevision)); Assert.That(terrain.World.PinkCount, Is.Zero);
            Assert.That(PlayerMotorSimulation.Query(state.Position + Vector3.up * (controller.height * .5f),
                -state.WallNormal, controller.radius + .065f, out _), Is.False, "The deleted committed occupancy must be absent before any mesh update");
            Step(true);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
            Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.FriendlyInkContact, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.Swimming, Is.True); Assert.That(state.Grounded, Is.False);
            Assert.That(state.PaperCenter.y - state.Position.y, Is.EqualTo(Body.Profile.SurfaceOffset).Within(.00001f));
            Assert.That(CapsuleOverlaps(state), Is.False); Assert.That(PaperOverlaps(state), Is.False);
            for (int i = 0; i < 5; i++) { Step(true); Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.SupportTerrainRevision, Is.Zero); }
            Assert.That(events.Count(e => e.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry), Is.EqualTo(1));
            yield break;
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
                if (BoxOverlaps(sample.PaperCenter + sample.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                    new Vector3(rect.width * .5f, rect.height * .5f, Body.Profile.Thickness * .5f), sample.PaperRotation,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }
        bool FinalFrameCapsuleSweepTouchesWorld(PlayerSnapshot start, Vector3 destination)
        {
            var shape = HeroBodyShape.For(start.HeroId);
            float height = start.Swimming || start.CompactBody ? shape.CompactHeight : shape.Height;
            float segment = Mathf.Max(0, height * .5f - shape.Radius);
            Vector3 center = start.Position + Vector3.up * (height * .5f);
            Vector3 a = center - Vector3.up * segment, b = center + Vector3.up * segment, delta = destination - start.Position;
            if (DataCapsuleSweep(center, segment, shape.Radius, delta)) return true;
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

        IEnumerator FindFinalFrameExpansion(bool held, bool stationary, bool neutralMap = false)
        {
            finalFrameCase = null;
            var origin = state; var originalInput = input;
            origin.SpecialImpulseVelocity = new Vector3(1.25f, 0, -.5f);
            origin.SpecialImpulseRetention = .83f; origin.SpecialImpulseRemaining = .47f;
            originalInput.Move = held ? stationary ? Vector2.zero : Vector2.up * .1f : Vector2.up;
            // First attachment probes along input yaw. Across the preserved
            // .52m wall gap, .6*cos(37deg) cannot reach the wall.
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
                    "The ordinary first-attach ray must reach committed fixed data before searching animation keys");
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
                Assert.That(state.SupportTerrainRevision, Is.EqualTo(neutralMap ? 0u : terrain.Revision));
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
            Publish(Wall()); Air(); yield return FindFinalFrameExpansion(false, false);
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
            Publish(Wall()); Air(); Step(true); Attached(); yield return FindFinalFrameExpansion(true, false);
            var trial = finalFrameCase;
            var authority = RunFinalFrameCase(trial, trial.Initial, true);
            var authorityTrace = events.ToArray();
            var replay = RunFinalFrameCase(trial, trial.Final, false);
            Assert.That(replay.Equals(authority), Is.True, SnapshotDifference(authority, replay)
                + "\nauthority trace=" + string.Join(" | ", authorityTrace.Select(TraceValue))
                + "\nreplay trace=" + string.Join(" | ", events.Select(TraceValue)));
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk)); Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Wall));
            Assert.That(state.Position.Equals(trial.Initial.Position), Is.True); Assert.That(state.PaperCenter.Equals(trial.Initial.PaperCenter), Is.True);
            Assert.That(state.PaperRotation.Equals(trial.Initial.PaperRotation), Is.True);
            Assert.That(state.PaperAnimationTime, Is.EqualTo(trial.Initial.PaperAnimationTime));
            Assert.That(state.PaperMove.Equals(trial.Initial.PaperMove), Is.True); Assert.That(state.Velocity.Equals(Vector3.zero), Is.True);
            Assert.That(state.Yaw, Is.Not.EqualTo(trial.Initial.Yaw)); Assert.That(state.Pitch, Is.Not.EqualTo(trial.Initial.Pitch));
            Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision)); Assert.That(state.FriendlyInkContact, Is.True);
            AssertFinalFramePublication();
        }

        static string SnapshotDifference(PlayerSnapshot authority, PlayerSnapshot replay) => string.Join(" | ",
            typeof(PlayerSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => !object.Equals(f.GetValue(authority), f.GetValue(replay)))
                .Select(f => f.Name + ": authority=" + Raw(f.GetValue(authority)) + "; replay=" + Raw(f.GetValue(replay))));
        static string TraceValue(PlayerMotorSimulation.MotorBranchObservation row) => row.Branch
            + ":probe=" + row.ProbeSucceeded + ",controller=" + Raw(row.ControllerPosition)
            + ",before=" + Raw(row.Before.Position) + ",after=" + Raw(row.After.Position);
        static string Raw(object value)
        {
            if (value is float f) return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (value is Vector2 v2) return "(" + Raw(v2.x) + "," + Raw(v2.y) + ")";
            if (value is Vector3 v3) return "(" + Raw(v3.x) + "," + Raw(v3.y) + "," + Raw(v3.z) + ")";
            if (value is Quaternion q) return "(" + Raw(q.x) + "," + Raw(q.y) + "," + Raw(q.z) + "," + Raw(q.w) + ")";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        [UnityTest] public IEnumerator NeutralMapWallRetainsItsSafeFinalContourWithoutFriendlyRecoveryOrTerrainDependency()
        {
            Publish(Array.Empty<Vector3Int>());
            var map = new GameObject("Bare wall for actual final contour rollback");
            var wall = map.AddComponent<BoxCollider>(); wall.size = new Vector3(1.7f, 5, .1f);
            map.transform.position = new Vector3(.05f, 2.5f, WallFront + .05f); Physics.SyncTransforms();
            Air(); Step(true); Attached(true);
            Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.Neutral));
            yield return FindFinalFrameExpansion(true, false, true);
            var trial = finalFrameCase;
            var authority = RunFinalFrameCase(trial, trial.Initial, true);
            var replay = RunFinalFrameCase(trial, trial.Final, false);
            Assert.That(replay.Equals(authority), Is.True);
            Assert.That(state.Movement, Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.Position.Equals(trial.Initial.Position), Is.True);
            Assert.That(state.PaperCenter.Equals(trial.Initial.PaperCenter), Is.True);
            Assert.That(state.PaperRotation.Equals(trial.Initial.PaperRotation), Is.True);
            Assert.That(state.PaperAnimationTime, Is.EqualTo(trial.Initial.PaperAnimationTime));
            Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.Neutral));
            Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.FriendlyInkContact, Is.False);
            Assert.That(state.HasInkRecovery, Is.False); AssertFinalFramePublication();
        }

        [UnityTest] public IEnumerator FinalKeyStationaryWallRejectsOnlyTheBlockedAnimationTransactionWhileTimeAdvances()
        {
            Publish(Wall()); Air(); Step(true); Attached(); yield return FindFinalFrameExpansion(true, true);
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

        [UnityTest] public IEnumerator FinalKeyCommittedFixedOccupancyInvalidatesThePriorFrameBeforeMeshPublication()
        {
            Publish(Wall()); Air(); Step(true); Attached(); yield return FindFinalFrameExpansion(true, true);
            var trial = finalFrameCase;
            AssertSideSupportClearsUnshrunkCapsule(trial.Initial);
            // The animation-only native obstacle belongs to the preceding
            // counterexample search. This case must be caused exclusively by
            // new committed fixed occupancy, and the native obstacle must not
            // reject otherwise legal construction as an unregistered map solid.
            trial.Obstacle.enabled = false; Physics.SyncTransforms();
            Assert.That(FinalFrameOracleOverlaps(trial.Initial, trial.InitialRects), Is.False);
            Assert.That(FinalFrameOracleOverlaps(trial.Final, trial.FinalRects), Is.False);
            Assert.That(terrain.PublishedTraversalPoseFits(player, trial.Initial), Is.True);
            Assert.That(terrain.PublishedTraversalPoseFits(player, trial.Final), Is.True);
            // Enumerate the complete rectangle OBB footprint. Its center cell
            // alone can miss a real side wing that crosses a global-grid edge.
            // This changes no profile, state, support height or stack rule.
            var candidateCells = new HashSet<Vector3Int>();
            foreach (var rect in trial.InitialRects)
            {
                Vector3 center = trial.Initial.PaperCenter + trial.Initial.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
                Vector3 axisX = trial.Initial.PaperRotation * Vector3.right;
                Vector3 axisY = trial.Initial.PaperRotation * Vector3.up;
                Vector3 axisZ = trial.Initial.PaperRotation * Vector3.forward;
                Vector3 extent = new Vector3(Mathf.Abs(axisX.x), Mathf.Abs(axisX.y), Mathf.Abs(axisX.z)) * (rect.width * .5f)
                    + new Vector3(Mathf.Abs(axisY.x), Mathf.Abs(axisY.y), Mathf.Abs(axisY.z)) * (rect.height * .5f)
                    + new Vector3(Mathf.Abs(axisZ.x), Mathf.Abs(axisZ.y), Mathf.Abs(axisZ.z)) * (Body.Profile.Thickness * .5f);
                var low = BlockWorld.CellAt(center - extent);
                var high = BlockWorld.CellAt(center + extent);
                for (int x = low.x; x <= high.x; x++)
                for (int y = low.y; y <= high.y; y++)
                for (int z = low.z; z <= high.z; z++)
                {
                    var cell = new Vector3Int(x, y, z);
                    if (CubeObb(BlockWorld.Center(cell), BlockWorld.CellSize * .5f, center,
                        new Vector3(rect.width * .5f, rect.height * .5f, Body.Profile.Thickness * .5f),
                        new[] { axisX, axisY, axisZ })) candidateCells.Add(cell);
                }
            }
            var previews = new List<BoxCollider>(); var cells = new List<Vector3Int>();
            int rejectedHeight = 0, rejectedExisting = 0, rejectedCapsule = 0, rejectedContour = 0;
            bool found = false;
            foreach (var target in candidateCells.OrderByDescending(c => c.y).ThenBy(c => c.x).ThenBy(c => c.z))
            {
                var mapHit = MapColumnHit(new Vector2Int(target.x, target.z));
                int baseY = BlockFixedGridMapSupport.GridBaseY(mapHit.point.y);
                if (target.y < baseY || target.y >= baseY + BlockFixedGridWorld.MaximumStackLayers ||
                    (target.y + 1) * BlockWorld.CellSize - mapHit.point.y > 1.20001f)
                { rejectedHeight++; continue; }
                if (terrain.FixedBlocks.TryGet(target, out _)) { rejectedExisting++; continue; }
                foreach (var box in previews) Object.DestroyImmediate(box.gameObject);
                previews.Clear(); cells.Clear();
                for (int y = baseY; y <= target.y; y++)
                {
                    var cell = new Vector3Int(target.x, y, target.z);
                    if (terrain.FixedBlocks.TryGet(cell, out _)) continue;
                    cells.Add(cell); previews.Add(Box("Preview full supported fixed column", BlockWorld.Center(cell), BlockWorld.CellSize));
                }
                if (cells.Count == 0) continue;
                if (CapsuleOverlaps(trial.Initial)) { rejectedCapsule++; continue; }
                if (!FinalFrameOracleOverlaps(trial.Initial, trial.InitialRects)) { rejectedContour++; continue; }
                found = true; break;
            }
            var shape = HeroBodyShape.For(trial.Initial.HeroId);
            Debug.Log("[WALL65 FULL COLUMN] candidates=" + candidateCells.Count + ",height=" + rejectedHeight
                + ",existing=" + rejectedExisting + ",capsule=" + rejectedCapsule + ",contour=" + rejectedContour
                + ",found=" + found + ",initial=" + Raw(trial.Initial.Position) + ",paper=" + Raw(trial.Initial.PaperCenter)
                + ",paperRotation=" + Raw(trial.Initial.PaperRotation) + ",radius=" + Raw(shape.Radius)
                + ",compactHeight=" + Raw(shape.CompactHeight) + ",skin=" + Raw(shape.SkinWidth)
                + ",testedCells=" + string.Join(";", candidateCells.OrderBy(c => c.y).ThenBy(c => c.x).ThenBy(c => c.z)));
            Assert.That(found, Is.True, "A complete admitted four-layer column must invalidate actual Paper while leaving its capsule clear; "
                + "complete footprint candidates=" + candidateCells.Count + ",height=" + rejectedHeight + ",existing=" + rejectedExisting
                + ",capsule=" + rejectedCapsule + ",contour=" + rejectedContour + ". No unsupported-cell or shrunken-profile fallback.");
            foreach (var box in previews) box.enabled = false;
            Physics.SyncTransforms();
            Assert.That(FinalFrameOracleOverlaps(trial.Initial, trial.InitialRects), Is.False,
                "The preview cannot remain as the runtime obstruction");
            foreach (var column in cells.Select(c => new Vector2Int(c.x, c.z)).Distinct()) PaintMapColumn(column, 2);
            uint oldRevision = terrain.Revision;
            var oldHandles = terrain.FixedBlocks.Blocks.Values.ToDictionary(s => s.Cell, s => s.Handle);
            using (var tx = terrain.FixedBlocks.BeginMutation())
            {
                Assert.That(tx.Build(cells.OrderBy(c => c.y), 2, cells.Count), Is.EqualTo(cells.Count));
                Assert.That(tx.TryCommit(out var result, out var error), Is.True, error);
                Assert.That(result.Added, Is.EqualTo(cells.Count));
            }
            // No frame yield or terrain Tick separates commit from these reads.
            Assert.That(terrain.Revision, Is.GreaterThan(oldRevision));
            foreach (var cell in cells)
            {
                Assert.That(terrain.FixedBlocks.TryGet(cell, out var block), Is.True);
                Assert.That(block.Handle.IsValid && block.Owner == 2, Is.True);
                Assert.That(block.Cell.y - block.Support.BaseCellY, Is.InRange(0, 3));
                Assert.That(block.Revision, Is.EqualTo(terrain.Revision));
            }
            foreach (var old in oldHandles)
                Assert.That(terrain.FixedBlocks.TryGet(old.Value, out var retained) && retained.Cell == old.Key, Is.True);
            Assert.That(FinalFrameOracleOverlaps(trial.Initial, trial.InitialRects), Is.True,
                "Independent data geometry sees the actual newly committed fixed column immediately");
            Assert.That(terrain.PublishedTraversalPoseFits(player, trial.Initial), Is.False);
            Assert.That(CapsuleOverlaps(trial.Initial), Is.False);
            var authority = RunFinalFrameCase(trial, trial.Initial, true);
            var authorityTrace = events.ToArray();
            var replay = RunFinalFrameCase(trial, trial.Final, false);
            Assert.That(replay.Equals(authority), Is.True, SnapshotDifference(authority, replay)
                + "\nauthority trace=" + string.Join(" | ", authorityTrace.Select(TraceValue))
                + "\nreplay trace=" + string.Join(" | ", events.Select(TraceValue)));
            Assert.That(state.Movement, Is.EqualTo(MovementMode.Air)); Assert.That(state.SupportTerrainRevision, Is.Zero);
            Assert.That(state.FriendlyInkContact, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.FixedBlockRevision, Is.EqualTo(terrain.Revision));
            AssertFinalFramePublication();
        }


    }
}
#endif
