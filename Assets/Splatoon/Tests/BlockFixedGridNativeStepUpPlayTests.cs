#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    // Reports-only new test. The two historical actual-displacement cases and
    // their unsuccessful step-down searches remain unchanged in their own file.
    [Category("BlockFixedGridNativeStepUp65")]
    public sealed class BlockFixedGridNativeStepUpPlayTests
    {
        const float Dt = 1f / 60;
        const float LaneZ = 2.55f, CurbX = .9f, BaseTop = .06f, CurbTop = .30f;
        const float RoofBottom = 2.20f, RoofTop = 2.22f;
        const string RootName = "65 native step-up authored arena";
        const string FloorName = "65 native step-up map floor";
        const string BaseName = "65 native step-up map base";
        const string CurbName = "65 native step-up map curb";
        const string RoofName = "65 native step-up map roof";
        static readonly Vector3 Request = Vector3.right * 1.8f;
        static readonly MethodInfo CombinedMove = typeof(PlayerMotorSimulation).GetMethod("MoveWithFreeBlocks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        PrototypeArena arena;
        BlockTerrain terrain;
        BoxCollider laneBase, curb, roof;
        PrototypePlayer player;
        CharacterController controller;
        PlayerMotorSimulation motor;
        string reportPath;
        bool capturedIgnore, priorTerrainPlayerIgnore, capturedCursor;
        int cursorWrites;
        BlockFixedGridWorld World => terrain.FixedBlocks;

        [Serializable] sealed class Record
        {
            public string stage, unity, topology, profile, detail, worldBytes;
            public Vector3 start, requested, delta, actualPosition, center, direction;
            public float radius, height, skinWidth, stepOffset, slopeLimit, minMoveDistance, halfSegment, penetrationDepth;
            public float maximumForward, skinGap, safePrefix;
            public int flags, nativeColliders, count, frameId;
            public bool grounded, requestedClear, requestedBlocked, actualBlocked, actualCrossed, endpointOverlap;
            public uint epoch, round, revision, surfaceRevision;
            public Vector3Int cell;
            public ulong id;
        }
        void Log(Record record)
        {
            if (string.IsNullOrEmpty(reportPath))
            {
                const string folder = "Reports/BlockSimulation20261018/NativeStepUp65";
                Directory.CreateDirectory(folder);
                reportPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
                TestContext.Out.WriteLine("NativeStepUp65 diagnostic: " + Path.GetFullPath(reportPath));
            }
            record.unity = Application.unityVersion; record.frameId = Time.frameCount;
            if (arena != null) record.topology = arena.BakedTopology;
            if (terrain != null && terrain.Initialized && terrain.FixedGridMode)
            {
                record.epoch = World.Epoch; record.round = World.Round;
                record.revision = World.Revision; record.surfaceRevision = World.Surfaces.Revision; record.count = World.Count;
            }
            if (controller != null)
            {
                record.actualPosition = controller.transform.position; record.grounded = controller.isGrounded;
                record.radius = controller.radius; record.height = controller.height; record.skinWidth = controller.skinWidth;
                record.stepOffset = controller.stepOffset; record.slopeLimit = controller.slopeLimit;
                record.minMoveDistance = controller.minMoveDistance;
                record.profile = player.SwimBody?.Profile?.ContentHash;
            }
            File.AppendAllText(reportPath, JsonUtility.ToJson(record) + Environment.NewLine);
        }
        static void AssertCursorSuppressed()
        {
            Assert.That((bool)typeof(PrototypeApp).GetProperty("SystemCursorWritesSuppressed",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.True,
                "Root runner must suppress system cursor writes before bootstrap.");
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            terrain = null; arena = null; player = null; controller = null; motor = null;
            laneBase = curb = roof = null; capturedIgnore = capturedCursor = false; reportPath = null;
            AssertCursorSuppressed();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            var authoredRoot = new GameObject(RootName);
            var authored = authoredRoot.AddComponent<PrototypeArena>();
            authored.OwnershipCellSize = GameplayConfig.Map.CellSize; authored.SpawnPoints = new Transform[8];
            for (int i = 0; i < 8; i++)
            {
                var spawn = new GameObject("native step-up spawn " + i).transform;
                spawn.SetParent(authoredRoot.transform, false); spawn.position = new Vector3(i % 4 * 2 - 3, .04f, i < 4 ? -3 : 3);
                authored.SpawnPoints[i] = spawn;
            }
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
            Assert.That(atlas, Is.Not.Null);
            AuthorBox(authoredRoot.transform, FloorName, 6541, new Vector3(8, .3f, 8), new Vector3(0, -.15f, 0), atlas);
            AuthorBox(authoredRoot.transform, BaseName, 6542, new Vector3(3.6f, .06f, 1.5f), new Vector3(.6f, .03f, LaneZ), atlas);
            AuthorBox(authoredRoot.transform, CurbName, 6543, new Vector3(1.5f, .24f, 1.5f), new Vector3(1.65f, .18f, LaneZ), atlas);
            AuthorBox(authoredRoot.transform, RoofName, 6544, new Vector3(1.5f, .02f, 1.5f), new Vector3(1.65f, 2.21f, LaneZ), atlas);
            authored.BakedTopology = authored.ComputeTopology();
            yield return new EnterPlayMode();
            Log(new Record { stage = "setup.after-enter-play-mode", detail = "Complete native map authored before topology, runtime Awake and map capture." });
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            HeroMigrationTests.Load();
            arena = GameObject.Find(RootName).GetComponent<PrototypeArena>(); arena.InitializeRuntime();
            laneBase = GameObject.Find(BaseName).GetComponent<BoxCollider>();
            curb = GameObject.Find(CurbName).GetComponent<BoxCollider>();
            roof = GameObject.Find(RoofName).GetComponent<BoxCollider>();
            Assert.That(arena.SpawnPoints.Length, Is.EqualTo(8)); Assert.That(arena.Surfaces.Count, Is.EqualTo(4));
            foreach (int id in new[] { 6541, 6542, 6543, 6544 }) Assert.That(arena.Surfaces[id].Ownership, Is.Not.Null);
            Assert.That(arena.TotalArea, Is.GreaterThan(0));
            priorTerrainPlayerIgnore = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, 8); capturedIgnore = true;
            terrain = new GameObject("65 native step-up fixed terrain").AddComponent<BlockTerrain>();
            terrain.Initialize(arena); terrain.EnableFixedGridMode(); terrain.Tick(true, 10, Dt);
            Assert.That(terrain.Initialized && terrain.FixedGridMode && terrain.Dynamic == null, Is.True);
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f)); Assert.That(BlockWorld.Top, Is.EqualTo(8f));
            Assert.That(BlockFixedGridWorld.MaximumStackLayers, Is.EqualTo(4));
            Assert.That(BlockFixedGridWorld.MaximumStackHeight, Is.EqualTo(1.2f));
            foreach (string name in new[] { FloorName, BaseName, CurbName, RoofName })
            {
                var box = GameObject.Find(name).GetComponent<BoxCollider>();
                Assert.That(terrain.IsAuthoredMapCollider(box), Is.True);
                Assert.That(box.transform.lossyScale, Is.EqualTo(Vector3.one));
                Assert.That(box.enabled && !box.isTrigger, Is.True);
            }
            Assert.That(laneBase.bounds.max.y, Is.EqualTo(BaseTop).Within(.000001f));
            Assert.That(curb.bounds.min.x, Is.EqualTo(CurbX).Within(.000001f));
            Assert.That(curb.bounds.max.y, Is.EqualTo(CurbTop).Within(.000001f));
            Assert.That(roof.bounds.min.y, Is.EqualTo(RoofBottom).Within(.000001f));
            Assert.That(roof.bounds.max.y, Is.EqualTo(RoofTop).Within(.000001f));
            cursorWrites = PrototypeApp.Current.SystemCursorWriteCount; capturedCursor = true; Assert.That(cursorWrites, Is.Zero);
            int before = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length;
            var paintKeys = new HashSet<BlockSurfaceKey>();
            for (int z = 7; z <= 9; z++) for (int x = 4; x <= 6; x++)
                AddSupportedRoofCell(new Vector3Int(x, 7, z), paintKeys);
            Assert.That(World.Count, Is.EqualTo(9)); Assert.That(paintKeys.Count, Is.EqualTo(9));
            Assert.That(Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length, Is.EqualTo(before),
                "Real fixed data creation must add no native Collider.");
            Assert.That(terrain.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            Assert.That(prefab, Is.Not.Null);
            player = Object.Instantiate(prefab).GetComponent<PrototypePlayer>(); player.gameObject.layer = 8;
            player.SwimBody.Bind(PaperBodyTests.Profile("RifleGirl"));
            controller = player.GetComponent<CharacterController>(); motor = new PlayerMotorSimulation(controller, arena);
            Log(new Record { stage = "setup", nativeColliders = before, worldBytes = WorldBytes(),
                detail = "Nine real admitted y7 fixed cells; no native fixed proxies. Separate scene/category leaves historical fixture untouched." });
        }
        static void AuthorBox(Transform parent, string name, int id, Vector3 size, Vector3 center, Texture2D atlas)
        {
            var boxObject = GameObject.CreatePrimitive(PrimitiveType.Cube); boxObject.name = name;
            boxObject.transform.SetParent(parent, false);
            var filter = boxObject.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh); var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size) + center;
            mesh.vertices = vertices; mesh.RecalculateBounds(); filter.sharedMesh = mesh;
            var box = boxObject.GetComponent<BoxCollider>(); box.size = size; box.center = center;
            var paint = boxObject.AddComponent<PaintSurface>(); paint.SurfaceId = id;
            paint.Scores = true; paint.WalkableSize = new Vector2(size.x, size.z); paint.ShapeAtlas = atlas;
        }
        void AddSupportedRoofCell(Vector3Int cell, HashSet<BlockSurfaceKey> keys)
        {
            Vector3 c = BlockWorld.Center(cell);
            Assert.That(Physics.Raycast(new Vector3(c.x, 2.8f, c.z), Vector3.down, out var hit, 1,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.collider, Is.SameAs(roof)); Assert.That(hit.point.y, Is.EqualTo(RoofTop).Within(.000001f));
            Assert.That(hit.normal.y, Is.GreaterThan(.999f));
            Assert.That(terrain.TryGetSurfacePaint(hit.collider, hit.point, hit.normal, out var paint), Is.True);
            Assert.That(keys.Add(paint.Key), Is.True, "Each complete 0.3m roof square must have its own admitted surface key.");
            Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, 1, 1, true).Added, Is.EqualTo(1));
            var admission = (BoxCollider)typeof(BlockTerrain).GetField("_fixedAdmissionBox",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(terrain);
            Assert.That(admission, Is.Not.Null); Assert.That(admission.enabled, Is.False);
            Assert.That(admission.isTrigger && admission.gameObject.activeInHierarchy, Is.True);
            Assert.That(admission.transform.parent, Is.Null); Assert.That(admission.transform.lossyScale, Is.EqualTo(Vector3.one));
            Assert.That(admission.gameObject.scene.handle, Is.EqualTo(terrain.gameObject.scene.handle));
            Assert.That(BlockFixedGridAdmissionShape65Tests.Penetration(terrain, c, roof, out var direction, out float depth), Is.True);
            Assert.That(admission.enabled, Is.False, "The actual scoped narrow-phase proof must retire its helper before returning.");
            Log(new Record { stage = "setup.roof-penetration-proof", cell = cell, direction = direction, penetrationDepth = depth });
            Assert.That(Vector3.Dot(direction, hit.normal), Is.GreaterThan(.99f));
            Assert.That(depth, Is.EqualTo(.1199f).Within(.001f));
            var added = World.Build(new[] { cell }, 1, 1);
            Log(new Record { stage = "setup.roof-cell-before-assert", cell = cell, detail = World.LastFailure });
            Assert.That(added.Added, Is.EqualTo(1), World.LastFailure);
            Assert.That(World.TryGet(cell, out var block), Is.True);
            Assert.That(block.Handle.IsValid, Is.True); Assert.That(block.Owner, Is.EqualTo(1));
            Assert.That(block.Support.BaseCellY, Is.EqualTo(7));
            Assert.That(block.Support.Height, Is.EqualTo(RoofTop).Within(.000001f));
            Assert.That(block.Support.SurfaceKey, Is.EqualTo(paint.Key));
            Assert.That(World.Surfaces.TryGet(block.Support.SurfaceKey, out var own), Is.True); Assert.That(own.Owner, Is.EqualTo(1));
            var bounds = BlockFixedGridWorld.GetBounds(block);
            Assert.That(bounds.min.y, Is.EqualTo(2.1f).Within(.000001f));
            Assert.That(bounds.max.y - block.Support.Height, Is.LessThanOrEqualTo(1.2f));
            Assert.That(bounds.min.x, Is.GreaterThanOrEqualTo(roof.bounds.min.x));
            Assert.That(bounds.max.x, Is.LessThanOrEqualTo(roof.bounds.max.x));
            Assert.That(bounds.min.z, Is.GreaterThanOrEqualTo(roof.bounds.min.z));
            Assert.That(bounds.max.z, Is.LessThanOrEqualTo(roof.bounds.max.z));
        }
        void AssertFormalShape()
        {
            var shape = HeroBodyShape.For(1);
            Assert.That(controller.radius, Is.EqualTo(shape.Radius)); Assert.That(controller.height, Is.EqualTo(shape.Height));
            Assert.That(controller.center, Is.EqualTo(shape.Center)); Assert.That(controller.skinWidth, Is.EqualTo(shape.SkinWidth));
            Assert.That(controller.stepOffset, Is.EqualTo(shape.StepOffset));
            Assert.That(shape.Height, Is.EqualTo(1.8f)); Assert.That(shape.Radius, Is.EqualTo(.35f));
            Assert.That(shape.SkinWidth, Is.EqualTo(.03f)); Assert.That(shape.StepOffset, Is.EqualTo(.3f));
            Assert.That(controller.slopeLimit, Is.EqualTo(45)); Assert.That(controller.minMoveDistance, Is.EqualTo(.001f));
            Assert.That(controller.detectCollisions && controller.enableOverlapRecovery, Is.True);
            Assert.That(controller.transform.lossyScale, Is.EqualTo(Vector3.one));
            Assert.That(CurbTop - BaseTop, Is.LessThan(shape.StepOffset));
        }
        PlayerSnapshot Prepare()
        {
            var start = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1, Health = 100, Ink = 50,
                LifeState = PlayerLifeState.Alive, Position = new Vector3(-.15f, .9f, LaneZ), Movement = MovementMode.Air,
                Grounded = false, GroundNormal = Vector3.up, SimulatedAt = 10, SimulationTick = 1,
                FixedBlockEpoch = World.Epoch, FixedBlockRound = World.Round, FixedBlockRevision = World.Revision };
            motor.Restore(start, false); Physics.SyncTransforms(); AssertFormalShape();
            for (int i = 0; i < 2; i++)
            {
                Vector3 before = controller.transform.position, request = Vector3.down * (i == 0 ? 1 : .05f);
                var flags = controller.Move(request); Physics.SyncTransforms();
                Log(new Record { stage = "prepare.native-grounding", start = before, requested = request,
                    delta = controller.transform.position - before, flags = (int)flags });
                Assert.That((flags & CollisionFlags.Below) != 0, Is.True);
            }
            start.Position = controller.transform.position; start.Grounded = controller.isGrounded;
            start.Movement = start.Grounded ? MovementMode.Human : MovementMode.Air;
            Log(new Record { stage = "prepare.actual-grounded", start = start.Position,
                detail = "Feet and grounded copied from two actual native Moves; no forced contact or post-landing Restore." });
            Assert.That(start.Grounded, Is.True); Assert.That(start.Position.y, Is.EqualTo(BaseTop + controller.skinWidth).Within(.002f));
            Assert.That(Physics.Raycast(start.Position + Vector3.up * .2f, Vector3.down, out var support, .5f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(support.collider, Is.SameAs(laneBase)); Assert.That(support.point.y, Is.EqualTo(BaseTop).Within(.000001f));
            AssertFormalShape(); return start;
        }
        string WorldBytes() => Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true)));
        void Shape(Vector3 feet, out Vector3 center, out float segment, out float radius)
        { center = feet + controller.center; segment = Mathf.Max(0, controller.height * .5f - controller.radius); radius = controller.radius - .001f; }
        static float DistanceSquared(Vector3 center, float halfSegment, Vector3Int cell)
        {
            var bounds = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize + Vector3.up * (2 * halfSegment));
            return (center - bounds.ClosestPoint(center)).sqrMagnitude;
        }
        bool IndependentOverlap(Vector3 center, float halfSegment, float radius)
            => World.Blocks.Values.Any(b => DistanceSquared(center, halfSegment, b.Cell) < radius * radius - 1e-7f);
        bool IndependentSweep(Vector3 center, float segment, float radius, Vector3 delta)
        {
            foreach (var block in World.Blocks.Values)
            {
                float lo = 0, hi = 1;
                for (int i = 0; i < 72; i++)
                {
                    float a = (2 * lo + hi) / 3, b = (lo + 2 * hi) / 3;
                    if (DistanceSquared(center + delta * a, segment, block.Cell) < DistanceSquared(center + delta * b, segment, block.Cell)) hi = b;
                    else lo = a;
                }
                float minimum = Mathf.Min(DistanceSquared(center, segment, block.Cell),
                    Mathf.Min(DistanceSquared(center + delta, segment, block.Cell), DistanceSquared(center + delta * ((lo + hi) * .5f), segment, block.Cell)));
                if (minimum <= radius * radius + 1e-7f) return true;
            }
            return false;
        }
        bool NativeOverlap(Vector3 center, float segment, float radius)
            => Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment, radius,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        bool NativeSweep(Vector3 center, float segment, float radius, Vector3 delta)
            => delta.sqrMagnitude > 1e-12f && Physics.CapsuleCast(center - Vector3.up * segment, center + Vector3.up * segment,
                radius, delta.normalized, out _, delta.magnitude, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        void AssertClearRequest(PlayerSnapshot initial)
        {
            Shape(initial.Position, out var center, out float segment, out float radius);
            Assert.That(IndependentOverlap(center, segment, radius), Is.False);
            Assert.That(NativeOverlap(center, segment, radius), Is.False);
            Assert.That(IndependentSweep(center, segment, radius, Request), Is.False);
            Assert.That(BlockFixedGridQueries.CastCapsule(World, center, segment, radius, Request, Request.magnitude, out _), Is.False);
            Assert.That(center.y + segment + radius, Is.LessThan(2.1f - .1f), "Initial requested capsule has more than 0.1m genuine data-ceiling clearance.");
        }
        void AssertSameStart(PlayerSnapshot expected, PlayerSnapshot actual)
        {
            Assert.That(Vector3.Distance(actual.Position, expected.Position), Is.LessThan(1e-5f));
            Assert.That(actual.Grounded, Is.True); Assert.That(controller.isGrounded, Is.True);
            AssertFormalShape(); AssertClearRequest(actual);
        }
        void AssertLegalAccepted(Vector3 initial, Vector3 delta)
        {
            Shape(initial, out var center, out float segment, out float radius);
            Assert.That(IndependentSweep(center, segment, radius, delta), Is.False);
            Assert.That(IndependentOverlap(center + delta, segment, radius - .001f), Is.False);
            Assert.That(BlockFixedGridQueries.CastCapsule(World, center, segment, radius, delta, delta.magnitude, out _), Is.False);
            Assert.That(terrain.FixedCapsuleOverlaps(center + delta, segment, radius - .001f), Is.False);
            Assert.That(NativeSweep(center, segment, radius, delta), Is.False);
            Assert.That(NativeOverlap(center + delta, segment, radius), Is.False);
            // Exact capsule-to-curb corner boundary for this horizontal lane,
            // including the rounded lower cap. The native skin is a distance
            // around that curve, not a smaller horizontal radius or shape.
            float verticalGap = Mathf.Max(0, center.y - segment - curb.bounds.max.y);
            Assert.That(verticalGap, Is.LessThan(radius));
            float halfWidth = Mathf.Sqrt(radius * radius - verticalGap * verticalGap);
            float nativeRadiusWithSkin = controller.radius + controller.skinWidth;
            float skinHalfWidth = Mathf.Sqrt(nativeRadiusWithSkin * nativeRadiusWithSkin - verticalGap * verticalGap);
            float maximumForward = curb.bounds.min.x - halfWidth - initial.x;
            float skinGap = skinHalfWidth - halfWidth;
            float actualForward = Vector3.Dot(delta, Vector3.right);
            Assert.That(actualForward, Is.GreaterThan(.5f));
            Assert.That(actualForward, Is.GreaterThanOrEqualTo(maximumForward - skinGap - .005f));
            Assert.That(actualForward, Is.LessThanOrEqualTo(maximumForward + .001f));
            Assert.That(Mathf.Abs(delta.y), Is.LessThan(.002f)); Assert.That(Mathf.Abs(delta.z), Is.LessThan(.0001f));
        }
        void AssertSafeConsumer(Vector3 initial, Vector3 delta, Vector3 independentlyReachedPrefix)
        {
            Shape(initial, out var center, out float segment, out float radius);
            // These are the same six independent safety checks as the original
            // strict horizontal control. Native map sliding/steps may change Y.
            Assert.That(IndependentSweep(center, segment, radius, delta), Is.False);
            Assert.That(IndependentOverlap(center + delta, segment, radius - .001f), Is.False);
            Assert.That(BlockFixedGridQueries.CastCapsule(World, center, segment, radius, delta, delta.magnitude, out _), Is.False);
            Assert.That(terrain.FixedCapsuleOverlaps(center + delta, segment, radius - .001f), Is.False);
            Assert.That(NativeSweep(center, segment, radius, delta), Is.False);
            Assert.That(NativeOverlap(center + delta, segment, radius), Is.False);
            float actualForward = Vector3.Dot(delta, Vector3.right);
            Assert.That(actualForward, Is.GreaterThan(.5f));
            Assert.That(actualForward, Is.GreaterThanOrEqualTo(Vector3.Dot(independentlyReachedPrefix, Vector3.right) - .001f),
                "Do not discard travel which the real independent map/data-safe native control proved reachable.");
            Assert.That(actualForward, Is.LessThanOrEqualTo(Vector3.Dot(Request, Vector3.right) + .001f));
            Assert.That(Mathf.Abs(delta.z), Is.LessThan(.0001f));
        }

        [UnityTest, Timeout(600000)] public IEnumerator NativeStepUpChangesActualPathAndCombinedConsumerMatchesLegalNoStepControl()
        {
            string original = WorldBytes(); int nativeCount = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length;
            var initial = Prepare(); AssertClearRequest(initial);
            Shape(initial.Position, out var center, out float segment, out float radius);
            var rawFlags = controller.Move(Request); Physics.SyncTransforms();
            Vector3 raw = controller.transform.position - initial.Position;
            bool independent = IndependentSweep(center, segment, radius, raw) || IndependentOverlap(center + raw, segment, radius - .001f);
            bool crossed = raw.sqrMagnitude > 1e-12f && BlockFixedGridQueries.CastCapsule(World, center, segment, radius, raw, raw.magnitude, out var hit) &&
                hit.Distance < raw.magnitude - .001f;
            bool overlaps = terrain.FixedCapsuleOverlaps(center + raw, segment, radius - .001f);
            Log(new Record { stage = "raw.native-step-up-before-assert", start = initial.Position, requested = Request, delta = raw, flags = (int)rawFlags,
                center = center, halfSegment = segment, requestedClear = true, requestedBlocked = false,
                actualBlocked = independent, actualCrossed = crossed, endpointOverlap = overlaps,
                detail = "Real native CCT with full authored stepOffset; data roof is invisible to native collision." });
            Assert.That(Vector3.Distance(raw, Request), Is.GreaterThan(.001f)); Assert.That(raw.y, Is.GreaterThan(.1f));
            Assert.That(controller.transform.position.x - controller.radius, Is.GreaterThan(curb.bounds.min.x + .01f));
            Assert.That(independent, Is.True); Assert.That(crossed || overlaps, Is.True);
            Assert.That(IndependentOverlap(center + raw, segment, radius - .001f), Is.True);
            Assert.That(NativeOverlap(center + raw, segment, radius), Is.False);
            float actualHead = center.y + raw.y + segment + radius;
            Assert.That(actualHead, Is.GreaterThan(2.101f));
            Assert.That(roof.bounds.min.y - actualHead, Is.GreaterThanOrEqualTo(controller.skinWidth + .005f));
            AssertFormalShape(); Assert.That(WorldBytes(), Is.EqualTo(original));
            var repeated = Prepare(); AssertSameStart(initial, repeated);
            var repeatFlags = controller.Move(Request); Physics.SyncTransforms(); Vector3 repeat = controller.transform.position - repeated.Position;
            Log(new Record { stage = "raw.complete-native-replay", start = repeated.Position, requested = Request, delta = repeat, flags = (int)repeatFlags });
            Assert.That(Vector3.Distance(repeat, raw), Is.LessThan(1e-5f)); Assert.That(repeatFlags, Is.EqualTo(rawFlags));
            // A zero stepOffset does not suppress the capsule's rounded-cap climb.
            // Preserve that real full-request observation; it is not a legal horizontal control.
            var noStepStart = Prepare(); AssertSameStart(initial, noStepStart); float authoredStep = controller.stepOffset;
            CollisionFlags noStepFlags;
            controller.stepOffset = 0;
            try { noStepFlags = controller.Move(Request); }
            finally { controller.stepOffset = authoredStep; }
            Physics.SyncTransforms(); Vector3 noStep = controller.transform.position - noStepStart.Position;
            bool noStepIndependent = IndependentSweep(center, segment, radius, noStep) || IndependentOverlap(center + noStep, segment, radius - .001f);
            bool noStepCrossed = noStep.sqrMagnitude > 1e-12f && BlockFixedGridQueries.CastCapsule(World, center, segment, radius,
                noStep, noStep.magnitude, out var noStepHit) && noStepHit.Distance < noStep.magnitude - .001f;
            Log(new Record { stage = "native.full-request-zero-step-observation", start = noStepStart.Position, requested = Request, delta = noStep, flags = (int)noStepFlags,
                requestedClear = true, actualBlocked = noStepIndependent, actualCrossed = noStepCrossed,
                endpointOverlap = terrain.FixedCapsuleOverlaps(center + noStep, segment, radius - .001f),
                detail = "Actual native Move used zero stepOffset and restored it in finally. This full-request path may climb the rounded cap and is never treated as a legal horizontal control." });
            AssertFormalShape(); Assert.That((noStepFlags & CollisionFlags.Sides) != 0, Is.True);
            Assert.That(WorldBytes(), Is.EqualTo(original));
            // Capture actual production movement before control validation so a failure
            // in either path still leaves its independent, source-bound observation.
            var consumerStart = Prepare(); AssertSameStart(initial, consumerStart);
            Assert.That(CombinedMove, Is.Not.Null); CollisionFlags flags;
            try { flags = (CollisionFlags)CombinedMove.Invoke(motor, new object[] { Request, Dt }); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            Physics.SyncTransforms(); Vector3 accepted = controller.transform.position - consumerStart.Position;
            bool acceptedCrossed = accepted.sqrMagnitude > 1e-12f && BlockFixedGridQueries.CastCapsule(World, center, segment, radius,
                accepted, accepted.magnitude, out var acceptedHit) && acceptedHit.Distance < accepted.magnitude - .001f;
            Log(new Record { stage = "combined.consumer-before-assert", start = consumerStart.Position, requested = Request, delta = accepted, flags = (int)flags,
                requestedClear = true,
                actualBlocked = IndependentSweep(center, segment, radius, accepted) || IndependentOverlap(center + accepted, segment, radius - .001f),
                actualCrossed = acceptedCrossed,
                endpointOverlap = terrain.FixedCapsuleOverlaps(center + accepted, segment, radius - .001f),
                detail = "Production native map slide/step may change Y. Check actual independent data/native safety and reachable progress; do not force a particular 1mm-margin horizontal control result." });
            AssertFormalShape(); Assert.That(WorldBytes(), Is.EqualTo(original));
            // This actual accepted endpoint has neither native nor data foot support.
            // The first native attempt's Below flag must be discarded on rollback.
            Vector3 acceptedFeet = consumerStart.Position + accepted;
            bool nativeFootSupport = Physics.Raycast(acceptedFeet + Vector3.up * .08f, Vector3.down, out _, .16f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            bool dataFootSupport = terrain.TryCastFixed(acceptedFeet + Vector3.up * .08f, Vector3.down, .16f, 0, out _);
            Assert.That(nativeFootSupport, Is.False); Assert.That(dataFootSupport, Is.False);
            Assert.That(controller.isGrounded, Is.False);
            Assert.That((flags & CollisionFlags.Below), Is.EqualTo(CollisionFlags.None),
                "A rejected and rolled-back native attempt cannot ground the accepted unsupported endpoint.");
            var controlStart = Prepare(); AssertSameStart(initial, controlStart);
            Shape(controlStart.Position, out var controlCenter, out float controlSegment, out float controlRadius);
            // Independent analytic curb/capsule boundary, including native skin.
            // This uses only actual authored geometry and controller dimensions;
            // neither the production fixed cast nor movement consumer chooses it.
            float verticalGap = Mathf.Max(0, controlCenter.y - controlSegment - curb.bounds.max.y);
            Assert.That(verticalGap, Is.LessThan(controlRadius));
            float halfWidth = Mathf.Sqrt(controlRadius * controlRadius - verticalGap * verticalGap);
            float nativeRadiusWithSkin = controller.radius + controller.skinWidth;
            float skinHalfWidth = Mathf.Sqrt(nativeRadiusWithSkin * nativeRadiusWithSkin - verticalGap * verticalGap);
            float maximumForward = curb.bounds.min.x - halfWidth - controlStart.Position.x;
            float skinGap = skinHalfWidth - halfWidth;
            float safePrefix = maximumForward - skinGap - .001f;
            Vector3 prefix = Vector3.right * safePrefix;
            Log(new Record { stage = "native.safe-prefix-geometric-proof", start = controlStart.Position, requested = prefix,
                center = controlCenter, halfSegment = controlSegment, maximumForward = maximumForward, skinGap = skinGap, safePrefix = safePrefix,
                detail = "1mm before the expanded native rounded-cap boundary. Original >0.5m, near-boundary, map/data overlap and sweep assertions remain unchanged." });
            AssertLegalAccepted(controlStart.Position, prefix);
            CollisionFlags controlFlags;
            controller.stepOffset = 0;
            try { controlFlags = controller.Move(prefix); }
            finally { controller.stepOffset = authoredStep; }
            Physics.SyncTransforms(); Vector3 control = controller.transform.position - controlStart.Position;
            Log(new Record { stage = "native.safe-prefix-control-before-assert", start = controlStart.Position, requested = prefix, delta = control, flags = (int)controlFlags,
                maximumForward = maximumForward, skinGap = skinGap, safePrefix = safePrefix,
                detail = "Real native Move of independently computed safe prefix; no transform assignment or simulated movement substitutes for this control." });
            AssertFormalShape(); AssertLegalAccepted(controlStart.Position, control);
            Assert.That(Vector3.Distance(control, prefix), Is.LessThan(.0001f));
            Assert.That(WorldBytes(), Is.EqualTo(original));
            var controlReplayStart = Prepare(); AssertSameStart(initial, controlReplayStart);
            CollisionFlags controlReplayFlags;
            controller.stepOffset = 0;
            try { controlReplayFlags = controller.Move(prefix); }
            finally { controller.stepOffset = authoredStep; }
            Physics.SyncTransforms(); Vector3 controlReplay = controller.transform.position - controlReplayStart.Position;
            Log(new Record { stage = "native.safe-prefix-complete-replay", start = controlReplayStart.Position, requested = prefix,
                delta = controlReplay, flags = (int)controlReplayFlags, safePrefix = safePrefix });
            AssertFormalShape(); AssertLegalAccepted(controlReplayStart.Position, controlReplay);
            Assert.That(Vector3.Distance(controlReplay, control), Is.LessThan(.00001f));
            Assert.That(controlReplayFlags, Is.EqualTo(controlFlags));
            AssertSafeConsumer(consumerStart.Position, accepted, control);
            Assert.That(Vector3.Distance(accepted, raw), Is.GreaterThan(.01f));
            Assert.That((flags & controlFlags), Is.EqualTo(controlFlags));
            Assert.That(WorldBytes(), Is.EqualTo(original));
            Assert.That(Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length, Is.EqualTo(nativeCount));
            Assert.That(terrain.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(PrototypeApp.Current.SystemCursorWriteCount, Is.EqualTo(cursorWrites));
            Log(new Record { stage = "complete", worldBytes = WorldBytes(), delta = accepted, detail = "New real-map step-up scenario only. Historical step-down test remains untriggered/failed in its original report." });
            yield return null;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            Log(new Record { stage = "cleanup", detail = TestContext.CurrentContext.Result.Outcome.ToString() });
            if (capturedIgnore) Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, priorTerrainPlayerIgnore);
            bool untouched = !capturedCursor || PrototypeApp.Current == null || PrototypeApp.Current.SystemCursorWriteCount == cursorWrites;
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(untouched, Is.True, "No system cursor write in the isolated fixture.");
        }
    }
}
#endif
