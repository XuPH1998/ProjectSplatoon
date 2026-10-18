#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockDynamicWorldTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        readonly List<Mesh> _mapMeshes = new();
        BlockDynamicWorld World => _terrain.Dynamic;
        [SetUp] public void Setup()
        {
            _root = new GameObject("Dynamic block contract fixture");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null); _terrain.EnableSurfaceMode();
        }
        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            foreach (var mesh in _mapMeshes) if (mesh != null) Object.DestroyImmediate(mesh);
            _mapMeshes.Clear();
            Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
        }
        static BlockDynamicState State(ulong id = 1, byte owner = 1, uint round = 1, float x = 0) => new()
        { Id = id, Owner = owner, Round = round, Position = new Vector3(x, 2, 0), Rotation = Quaternion.identity, Velocity = Vector3.right };

        [Test] public void IndependentBodiesHaveRealVolumeMassAndNoVoxelOccupancy()
        {
            World.SetAuthority(true);
            Assert.That(World.TrySpawn(1, new Vector3(0, 2, 0), Quaternion.identity, out var a), Is.True);
            Assert.That(World.TrySpawn(2, new Vector3(.5f, 2, 0), Quaternion.identity, out var b), Is.True);
            Assert.That(a.Body, Is.Not.SameAs(b.Body)); Assert.That(a.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass));
            Assert.That(a.Collider.size, Is.EqualTo(Vector3.one * BlockWorld.CellSize)); Assert.That(a.Body.isKinematic, Is.False);
            Assert.That(a.Collider.contactOffset, Is.EqualTo(.002f), "New active bodies must use the authored contact margin");
            Assert.That(a.Body.useGravity, Is.True); Assert.That(a.Body.constraints, Is.EqualTo(RigidbodyConstraints.None));
            Assert.That(World.PinkCount, Is.EqualTo(1)); Assert.That(World.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.World.PinkCount, Is.Zero, "Free bodies do not enter the terrain publication queue");
            Assert.That(World.TrySpawn(1, a.Body.position, Quaternion.identity, out _), Is.False, "Birth cannot overlap another body");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void RotatedBirthChecksTheWholeCubeAtConstructionBoundaries(int face)
        {
            World.SetAuthority(true);
            float half = BlockWorld.CellSize * .5f;
            var center = face switch
            {
                0 => new Vector3(0, BlockWorld.Top - half - .005f, 0),
                1 => new Vector3(_terrain.World.Dimensions.x * .5f - half - .005f, 2, 0),
                2 => new Vector3(0, 2, _terrain.World.Dimensions.y * .5f - half - .005f),
                _ => new Vector3(0, _terrain.World.Bottom + half + .005f, 0)
            };
            Assert.That(World.TrySpawn(1, center, Quaternion.identity, out _), Is.True, "The unrotated cube fits at this center");
            World.Reset(1);
            Assert.That(World.TrySpawn(1, center, Quaternion.Euler(35, 35, 35), out _), Is.False, "Rotated corners cross the birth boundary");
            Assert.That(World.Count, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void RotatedBirthQueriesRealMapOrientationRatherThanAnAxisAlignedBox(bool aabbCornerOnly)
        {
            World.SetAuthority(true);
            var center = new Vector3(0, 2, 0); float half = BlockWorld.CellSize * .5f;
            var obstacle = new GameObject("Rotated birth obstacle"); obstacle.transform.SetParent(_root.transform);
            obstacle.transform.position = center + new Vector3(half * 1.25f, aabbCornerOnly ? half * 1.25f : 0, 0);
            obstacle.AddComponent<BoxCollider>().size = Vector3.one * .01f; Physics.SyncTransforms();
            Assert.That(_terrain.IsDynamicSpawnAllowed(center), Is.True, "The identity box misses this obstacle");
            Assert.That(World.TrySpawn(1, center, Quaternion.Euler(0, 0, 45), out _), Is.EqualTo(aabbCornerOnly),
                "A real corner hit rejects birth, while an empty corner of its AABB does not");
        }

        [TestCase(false)] [TestCase(true)]
        public void RotatedBirthChecksPublishedVoxelVolumeWithoutOverrejectingAabbCorners(bool aabbCornerOnly)
        {
            World.SetAuthority(true);
            var origin = new Vector3Int(0, 6, 0); var center = BlockWorld.Center(origin);
            var occupied = origin + Vector3Int.right + (aabbCornerOnly ? Vector3Int.up : Vector3Int.zero);
            Assert.That(_terrain.World.Set(occupied, BlockCell.Create(2, surfaceAttached: true)), Is.True);
            Assert.That(_terrain.IsDynamicSpawnAllowed(center), Is.True, "Only touching the identity cube is allowed");
            Assert.That(World.TrySpawn(1, center, Quaternion.Euler(0, 0, 45), out _), Is.EqualTo(aabbCornerOnly),
                "Published voxel occupancy is checked even before its collider is built");
        }

        [Test] public void RotatedBirthProtectsTheFullColumnFootprintAtOtherHeights()
        {
            World.SetAuthority(true); float half = BlockWorld.CellSize * .5f;
            ConstructionZone(new Vector3(half * 1.25f, 1, 0), Vector3.one * .01f);
            var center = new Vector3(0, 3, 0);
            Assert.That(_terrain.IsDynamicSpawnAllowed(center), Is.True);
            Assert.That(World.TrySpawn(1, center, Quaternion.Euler(0, 0, 45), out _), Is.False,
                "A rotated cube cannot be born above a protected zone reached by its footprint");
        }

        [Test] public void InvalidBirthRotationIsAtomicAndLegalRotationIsNormalized()
        {
            World.SetAuthority(true); uint revision = World.LifecycleRevision; int notifications = 0;
            World.Changed += () => notifications++;
            foreach (var rotation in new[] { default(Quaternion), new Quaternion(float.NaN, 0, 0, 1),
                new Quaternion(0, 0, 0, float.PositiveInfinity), new Quaternion(0, 0, 0, float.MaxValue) })
            {
                Assert.That(World.TrySpawn(1, new Vector3(0, 2, 0), rotation, out var rejected), Is.False);
                Assert.That(rejected, Is.Null); Assert.That(World.Count, Is.Zero);
                Assert.That(World.LifecycleRevision, Is.EqualTo(revision)); Assert.That(World.CreatedCount, Is.Zero);
                Assert.That(notifications, Is.Zero);
            }
            var unit = Quaternion.Euler(10, 20, 30);
            var scaled = new Quaternion(unit.x * 2, unit.y * 2, unit.z * 2, unit.w * 2);
            Assert.That(World.TrySpawn(1, new Vector3(0, 2, 0), scaled, out var body), Is.True);
            Assert.That(body.Id, Is.EqualTo(1), "Rejected births do not consume body identities");
            foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                Assert.That(Vector3.Distance(body.Body.rotation * axis, unit * axis), Is.LessThan(.00001f));
            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test] public void MotionIsOrderedPerBodyAndCannotResurrectDeletedIds()
        {
            Assert.That(World.ApplySnapshot(1, 1, 1, new[] { State(1), State(2) }, false), Is.True);
            Assert.That(World.ApplyMotion(1, 1, 10, new[] { State(1, x: 1) }), Is.True);
            Assert.That(World.ApplyMotion(1, 1, 9, new[] { State(2, x: 2) }), Is.True);
            Assert.That(World.Bodies[1].Body.position.x, Is.EqualTo(1)); Assert.That(World.Bodies[2].Body.position.x, Is.EqualTo(2));
            World.ApplyMotion(1, 1, 8, new[] { State(1, x: 8) });
            Assert.That(World.Bodies[1].Body.position.x, Is.EqualTo(1));
            Assert.That(World.ApplySnapshot(1, 2, 11, new[] { State(2, x: 2) }, false), Is.True);
            Assert.That(World.ApplyMotion(1, 1, 100, new[] { State(1) }), Is.False);
            Assert.That(World.Count, Is.EqualTo(1)); Assert.That(World.Bodies.ContainsKey(1), Is.False);
        }

        [Test] public void InvalidSnapshotIsAtomicAndOldRoundCannotReplaceNewRound()
        {
            World.ApplySnapshot(2, 1, 1, new[] { State(round: 2) }, false);
            var invalid = State(round: 2); invalid.Position.x = float.NaN;
            Assert.That(World.ApplySnapshot(2, 2, 2, new[] { State(2, round: 2), invalid }, false), Is.False);
            Assert.That(World.Count, Is.EqualTo(1)); Assert.That(World.Bodies.ContainsKey(1), Is.True);
            Assert.That(World.ApplySnapshot(1, 10, 100, new[] { State() }, false), Is.False);
            Assert.That(World.Round, Is.EqualTo(2));
        }

        [Test] public void SleepRetainsIdentityAndRoundClearDoesNotReuseIds()
        {
            World.SetAuthority(true); World.TrySpawn(1, new Vector3(0, 2, 0), Quaternion.identity, out var body);
            ulong oldId = body.Id; body.Body.Sleep();
            var states = new List<BlockDynamicState>(); World.CaptureStates(states);
            Assert.That(states[0].Sleeping, Is.True); Assert.That(World.Count, Is.EqualTo(1));
            body.Body.WakeUp(); Assert.That(body.Id, Is.EqualTo(oldId));
            World.Reset(2); World.TrySpawn(1, new Vector3(0, 2, 0), Quaternion.identity, out body);
            Assert.That(body.Id, Is.GreaterThan(oldId)); Assert.That(World.Round, Is.EqualTo(2));
            World.Freeze(); Assert.That(body.Body.isKinematic, Is.True);
            Assert.That(World.TrySpawn(1, new Vector3(1, 2, 0), Quaternion.identity, out _), Is.False);
            World.Reset(3); Assert.That(World.Count, Is.Zero); Assert.That(World.Frozen, Is.False);
        }

        // Deliberately retain the old layer/disk loop as the comparison oracle.
        // TrySpawn runs the unchanged complete admission against real colliders.
        List<Vector3> ReferenceBuild(Vector3 point, Vector3Int n, float radius, int count)
        {
            var result = new List<Vector3>();
            var u = n.y != 0 ? Vector3Int.right : Vector3Int.up;
            var v = Vector3Int.RoundToInt(Vector3.Cross(n, u));
            var first = BlockWorld.CellAt(point + (Vector3)n * (BlockWorld.CellSize * .51f));
            int extent = Mathf.CeilToInt(radius / BlockWorld.CellSize);
            var offsets = new List<Vector2Int> { Vector2Int.zero };
            for (int ring = 1; ring <= extent; ring++)
            {
                for (int x = -ring; x <= ring; x++) for (int side = -1; side <= 1; side += 2)
                    if ((x * x + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radius * radius + .00001f)
                        offsets.Add(new Vector2Int(x, ring * side));
                for (int y = -ring + 1; y < ring; y++) for (int side = -1; side <= 1; side += 2)
                    if ((y * y + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radius * radius + .00001f)
                        offsets.Add(new Vector2Int(ring * side, y));
            }
            for (int layer = 0; layer < count && result.Count < count; layer++)
            foreach (var offset in offsets)
            {
                var center = BlockWorld.Center(first + n * layer + u * offset.x + v * offset.y);
                if (!World.TrySpawn(1, center, Quaternion.identity, out var body)) continue;
                result.Add(body.Body.position);
                if (result.Count == count) break;
            }
            return result;
        }

        void AssertBuildMatchesOriginal(Vector3 point, Vector3Int normal, float radius, int count)
        {
            World.Reset(0); World.SetAuthority(true); Physics.SyncTransforms();
            var expected = ReferenceBuild(point, normal, radius, count);
            World.Reset(0); Physics.SyncTransforms();
            Assert.That(World.Build(point, normal, radius, 1, count), Is.EqualTo(expected.Count));
            var states = new List<BlockDynamicState>(); World.CaptureStates(states);
            Assert.That(states.Count, Is.EqualTo(expected.Count));
            for (int i = 0; i < states.Count; i++)
                Assert.That(states[i].Position, Is.EqualTo(expected[i]), $"Original accepted candidate {i} at point {point}, normal {normal}");
        }

        HeroChangeZone ConstructionZone(Vector3 center, Vector3 size, float yaw = 0)
        {
            var obj = new GameObject("Dynamic build protected columns"); obj.transform.SetParent(_root.transform);
            obj.transform.SetPositionAndRotation(center, Quaternion.Euler(0, yaw, 0));
            var box = obj.AddComponent<BoxCollider>(); box.isTrigger = true; box.size = size;
            var zone = obj.AddComponent<HeroChangeZone>();
            typeof(BlockTerrain).GetField("_constructionZones", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_terrain, new[] { zone });
            Physics.SyncTransforms(); return zone;
        }

        PrototypeArena BeginAuthoredMap()
        {
            Object.DestroyImmediate(_root);
            _root = new GameObject("Authored map dynamic birth fixture");
            return _root.AddComponent<PrototypeArena>();
        }

        BoxCollider MapBox(string name, Vector3 center, Vector3 size)
        {
            var obj = new GameObject(name); obj.transform.SetParent(_root.transform, false);
            obj.transform.position = center;
            var box = obj.AddComponent<BoxCollider>(); box.size = size; return box;
        }

        MeshCollider MapFloor()
        {
            var obj = new GameObject("Authored one-sided ground"); obj.transform.SetParent(_root.transform, false);
            var mesh = new Mesh { name = "Dynamic birth one-sided ground fixture",
                vertices = new[] { new Vector3(-6, 0, -6), new Vector3(-6, 0, 6), new Vector3(6, 0, 6), new Vector3(6, 0, -6) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            mesh.RecalculateBounds(); _mapMeshes.Add(mesh);
            var collider = obj.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; return collider;
        }

        void InitializeAuthoredMap(PrototypeArena arena)
        {
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableSurfaceMode();
            World.SetAuthority(true); Physics.SyncTransforms();
            foreach (var collider in arena.GetComponentsInChildren<Collider>(true))
                if (!collider.isTrigger && collider.attachedRigidbody == null && collider.enabled)
                    Assert.That(_terrain.IsAuthoredMapCollider(collider), Is.True, "The production visibility filter must recognize fixture map geometry");
        }

        BlockDynamicBody LegalSeed(Vector3 center)
        {
            Assert.That(World.TrySpawn(1, center, Quaternion.identity, out var body), Is.True);
            return body;
        }

        [TestCase(false)] [TestCase(true)]
        public void HorizontalBuildCannotBirthBeyondGroundAndStillFulfilsLegalCount(bool includeFoundation)
        {
            var arena = BeginAuthoredMap(); MapFloor();
            if (includeFoundation) MapBox("Authored Foundation", new Vector3(0, -.3f, 0), new Vector3(12, .55f, 12));
            InitializeAuthoredMap(arena);
            var seed = LegalSeed(new Vector3(.15f, .15f, .15f));
            Vector3 point = seed.Body.position + Vector3.right * (BlockWorld.CellSize * .5f);
            long beforeCreated = World.CreatedCount;
            Assert.That(World.Build(point, Vector3.right, 1.2f, 1, 32), Is.EqualTo(32), "Blocked underground locations must be replaced with later legal disk candidates");
            Assert.That(World.Count, Is.EqualTo(33)); Assert.That(World.CreatedCount - beforeCreated, Is.EqualTo(32));
            Assert.That(World.LastBuildWork.requested, Is.EqualTo(32)); Assert.That(World.LastBuildWork.added, Is.EqualTo(32));
            foreach (var body in World.Bodies.Values)
                Assert.That(body.Collider.bounds.min.y, Is.GreaterThanOrEqualTo(-.00001f), "No cube is born through the ground at " + body.Body.position);
        }

        [Test] public void BuildRetainsLegalFreeSpaceBelowElevatedBridge()
        {
            var arena = BeginAuthoredMap(); MapFloor();
            var bridge = MapBox("Authored elevated bridge", new Vector3(0, 2.78f, 0), new Vector3(16, .4f, 4));
            InitializeAuthoredMap(arena);
            var seed = LegalSeed(new Vector3(.15f, 1.35f, .15f));
            Vector3 point = seed.Body.position + Vector3.right * (BlockWorld.CellSize * .5f);
            Assert.That(World.Build(point, Vector3.right, .6f, 1, 32), Is.EqualTo(32));
            Assert.That(World.Count, Is.EqualTo(33));
            foreach (var body in World.Bodies.Values)
            {
                Assert.That(body.Collider.bounds.min.y, Is.GreaterThan(0));
                Assert.That(body.Collider.bounds.max.y, Is.LessThan(bridge.bounds.min.y), "Having an authored bridge overhead must not forbid legal birth");
            }
        }

        [TestCase(-1)] [TestCase(1)]
        public void BuildRejectsEmptyLocationsBehindAuthoredWallOnEitherSide(int direction)
        {
            var arena = BeginAuthoredMap();
            MapBox("Authored separating wall", new Vector3(direction * .75f, 2, 0), new Vector3(.2f, 4, 4));
            InitializeAuthoredMap(arena);
            var normal = Vector3.right * direction;
            var seed = LegalSeed(new Vector3(direction * .15f, 1.95f, .15f));
            Vector3 point = seed.Body.position + normal * (BlockWorld.CellSize * .5f);
            int added = World.Build(point, normal, .6f, 1, 32);
            Assert.That(added, Is.GreaterThan(0).And.LessThan(32), "Only the near-side disk fits before the authored wall");
            Assert.That(World.Count, Is.EqualTo(added + 1)); Assert.That(World.LastBuildWork.requested, Is.EqualTo(32));
            foreach (var body in World.Bodies.Values)
                Assert.That(body.Body.position.x * direction + BlockWorld.CellSize * .5f, Is.LessThan(.65f),
                    "The empty far side cannot satisfy a near-side build reservation");
        }

        [Test] public void BlocksCreatedEarlierInBuildDoNotOccludeLaterFreeBodies()
        {
            var arena = BeginAuthoredMap(); InitializeAuthoredMap(arena);
            var seed = LegalSeed(new Vector3(.15f, 1.95f, .15f));
            Vector3 point = seed.Body.position + Vector3.right * (BlockWorld.CellSize * .5f);
            Assert.That(World.Build(point, Vector3.right, .6f, 1, 32), Is.EqualTo(32));
            Assert.That(World.Count, Is.EqualTo(33));
            bool laterCollinearLayer = false;
            foreach (var body in World.Bodies.Values)
                if (body.Body.position.x > .9f && Mathf.Abs(body.Body.position.y - seed.Body.position.y) < .001f &&
                    Mathf.Abs(body.Body.position.z - seed.Body.position.z) < .001f)
                {
                    laterCollinearLayer = true;
                    Assert.That(World.Visible(point, body.Body.position), Is.True, "A nearer dynamic cube cannot turn into map occlusion");
                }
            Assert.That(laterCollinearLayer, Is.True, "The reservation reaches a later layer behind earlier-created cubes");
        }

        [Test] public void EmbeddedSourceAdverseCaseMustNotBuildOutThroughAuthoredSolid()
        {
            var arena = BeginAuthoredMap();
            MapBox("Authored solid containing adverse source", new Vector3(0, 2, 0), new Vector3(.6f, 4, 4));
            InitializeAuthoredMap(arena);
            Vector3 point = new Vector3(0, 1.95f, .15f), target = new Vector3(.75f, 1.95f, .15f);
            Assert.That(_terrain.IsDynamicSpawnAllowed(target), Is.True, "The endpoint itself is legal; the source is intentionally inside map geometry");
            Assert.That(World.Visible(point + Vector3.right * .002f, target), Is.True,
                "Adverse-input evidence: a forward ray starting inside solid geometry does not report its exit");
            Assert.That(World.Visible(target, point + Vector3.right * .002f), Is.False,
                "The same segment in reverse enters the authored solid and exposes the hidden obstruction");
            Assert.That(World.Build(point, Vector3.right, .3f, 1, 32), Is.Zero,
                "An embedded source must not bypass map separation merely because the endpoint is empty");
            Assert.That(World.Count, Is.Zero);
        }

        [TestCase(1, 0, 0)] [TestCase(-1, 0, 0)]
        [TestCase(0, 1, 0)] [TestCase(0, -1, 0)]
        [TestCase(0, 0, 1)] [TestCase(0, 0, -1)]
        public void BuildClipKeepsOriginalAcceptedOrderAtAllSixWorldFaces(int x, int y, int z)
        {
            var normal = new Vector3Int(x, y, z);
            foreach (var point in new[] { new Vector3(.11f, 2.01f, -.09f), new Vector3(15.91f, 7.91f, 31.91f),
                new Vector3(-15.91f, .01f, -31.91f), new Vector3(16.21f, 8.21f, 32.21f),
                new Vector3(-16.21f, -.21f, -32.21f) })
                AssertBuildMatchesOriginal(point, normal, .41f, 64);
        }

        [TestCase(1, 0, 0)] [TestCase(-1, 0, 0)]
        [TestCase(0, 1, 0)] [TestCase(0, -1, 0)]
        [TestCase(0, 0, 1)] [TestCase(0, 0, -1)]
        public void BuildProtectionCacheKeepsOriginalOrderWithRotatedZoneAndRealObstacles(int x, int y, int z)
        {
            ConstructionZone(new Vector3(.2f, 2, .3f), new Vector3(1.6f, 4, 1.1f), 35);
            var obstacle = new GameObject("Existing obstacle"); obstacle.transform.SetParent(_root.transform);
            obstacle.transform.position = new Vector3(1, 2, 0); obstacle.AddComponent<BoxCollider>().size = new Vector3(.5f, 5, .5f);
            AssertBuildMatchesOriginal(new Vector3(.2f, 1.8f, -.4f), new Vector3Int(x, y, z), .61f, 75);
        }

        [Test] public void FullyProtectedBuildRejectsColumnsOnceAndStillReportsProductionWork()
        {
            ConstructionZone(new Vector3(0, 2, 0), new Vector3(4, 4, 4)); World.SetAuthority(true);
            int reports = 0; BlockDynamicWorld.BuildWorkDiagnostics observed = default;
            World.BuildObserved += work => { reports++; observed = work; };
            Assert.That(World.Build(Vector3.zero, Vector3.up, .6f, 1, 4096), Is.Zero);
            Assert.That(reports, Is.EqualTo(1)); Assert.That(observed.requested, Is.EqualTo(4096));
            Assert.That(observed.added, Is.Zero); Assert.That(observed.candidateChecks, Is.Zero);
            Assert.That(observed.protectionChecks, Is.GreaterThan(0).And.LessThan(100), "One disk of column proofs, without scanning every layer");
            Assert.That(observed.protectedCandidatesSkipped, Is.GreaterThan(observed.protectionChecks));
            Assert.That(observed.boundsCandidatesSkipped, Is.GreaterThan(1000));
            Assert.That(observed.cpuMilliseconds, Is.GreaterThanOrEqualTo(0));
            Assert.That(World.InspectBuildWork().calls, Is.EqualTo(1));
        }

        [Test] public void BuildBeyondWorldTopDoesNoAdmissionQueriesAndRetainsRequestedCount()
        {
            World.SetAuthority(true);
            Assert.That(World.Build(new Vector3(0, BlockWorld.Top + 1, 0), Vector3.up, .4f, 1, 4096), Is.Zero);
            Assert.That(World.LastBuildWork.requested, Is.EqualTo(4096));
            Assert.That(World.LastBuildWork.candidateChecks, Is.Zero);
            Assert.That(World.LastBuildWork.protectionChecks, Is.Zero);
            Assert.That(World.LastBuildWork.boundsCandidatesSkipped, Is.GreaterThan(4096));
            Assert.That(World.CreatedCount, Is.Zero);
        }

        [Test] public void SuccessfulFirstCandidateDoesNotPrecheckTheRemainingDiskAndZonesStayLive()
        {
            var zone = ConstructionZone(new Vector3(0, 2, 0), new Vector3(4, 4, 4)); World.SetAuthority(true);
            Assert.That(World.Build(new Vector3(0, 2, 0), Vector3.up, 1.2f, 1, 1), Is.Zero);
            zone.transform.position += Vector3.right * 10; Physics.SyncTransforms();
            Assert.That(World.Build(new Vector3(0, 2, 0), Vector3.up, 1.2f, 1, 1), Is.EqualTo(1));
            Assert.That(World.LastBuildWork.protectionChecks, Is.EqualTo(1), "The original first candidate satisfies this payment");
            Assert.That(World.LastBuildWork.candidateChecks, Is.EqualTo(1));
        }
    }
}
#endif
