#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Historical solid-coating geometry contracts retained with the legacy code.
    // Production enables BlockSurfaceWorld; these fixtures explicitly disable that
    // newer store and do not claim acceptance of the zero-thickness paint rules.
    [Category("BlockLegacySurfaceVolume")]
    public sealed class BlockSurfaceModeTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        bool[,] _ignored;
        readonly List<Mesh> _meshes = new();
        HashSet<Collider> MapColliders => (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders", Private).GetValue(_terrain);

        [SetUp]
        public void Setup()
        {
            _ignored = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) _ignored[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("Production map coating contracts");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(null); _terrain.EnableSurfaceMode();
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.SurfacePaints)).SetValue(_terrain, null);
        }

        [TearDown]
        public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            foreach (var mesh in _meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            _meshes.Clear();
            if (_ignored != null)
                for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) Physics.IgnoreLayerCollision(a, b, _ignored[a, b]);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        BoxCollider MapBox(Vector3 center, Vector3 size, bool registered = true, bool paintable = true)
        {
            var go = new GameObject("Authored map face"); go.transform.SetParent(_root.transform, false);
            go.transform.position = center;
            var collider = go.AddComponent<BoxCollider>(); collider.size = size;
            if (paintable) go.AddComponent<PaintSurface>();
            if (registered) MapColliders.Add(collider);
            Physics.SyncTransforms(); return collider;
        }

        BlockWorld StageBuild(Vector3 point, Vector3 normal, int count, float radius)
        {
            _terrain.QueueBuild(point, normal, 1, count, radius, true);
            var command = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var pair = typeof(ValueTuple<,>).MakeGenericType(command, typeof(BlockMutationResult));
            var results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pair));
            var next = _terrain.World.Clone();
            object[] arguments = { next, new HashSet<Vector3Int>(), results, 0,
                System.Diagnostics.Stopwatch.StartNew(), 10000d, 100000 };
            try { typeof(BlockTerrain).GetMethod("StageCommands", Private).Invoke(_terrain, arguments); }
            finally { typeof(BlockTerrain).GetField("_gravityJournal", Private).SetValue(_terrain, null); }
            Assert.That(_terrain.PendingTransactions, Is.Zero, "This small command must finish its staged candidate stream.");
            return next;
        }

        void InstallStaged(BlockWorld next)
        {
            next.Seal(_terrain.Revision + 1, next.Chunks.Keys.ToArray());
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain, next);
        }

        static IEnumerable<(Vector3Int cell, BlockCell value)> Cells(BlockWorld world)
        {
            foreach (var chunk in world.Chunks)
                for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
                    if (chunk.Value.Cells[index].Occupied)
                        yield return (BlockWorld.CellFromIndex(chunk.Key, index), chunk.Value.Cells[index]);
        }

        [Test]
        public void LargePaymentBuildsOnlyOneMapLayerAndNeverSpendsItsRemainderOnASecondLayer()
        {
            MapBox(new Vector3(0, -.1f, 0), new Vector3(8, .2f, 8));
            var next = StageBuild(new Vector3(.3f, 0, .3f), Vector3.up, 100, BlockWorld.CellSize * 2);
            var cells = Cells(next).ToArray();
            Assert.That(cells.Length, Is.EqualTo(13), "A disk with a two-cell radius contains thirteen lattice cells at either size.");
            Assert.That(cells.All(entry => entry.cell.y == 0 && entry.value.SurfaceAttached && entry.value.Anchored), Is.True);
            Assert.That(next.Falling.Count, Is.Zero); Assert.That(next.Cohorts.Count, Is.Zero);
            Assert.That(_terrain.UnstableColumns, Is.Zero); Assert.That(_terrain.SpreadMoves, Is.Zero);
        }

        [Test]
        public void CoatingAtAFiniteWallEndRequiresARealMapFaceForEveryCell()
        {
            MapBox(new Vector3(-.1f, BlockWorld.CellSize, BlockWorld.CellSize),
                new Vector3(.2f, BlockWorld.CellSize * 2, BlockWorld.CellSize * 2));
            var next = StageBuild(new Vector3(0, BlockWorld.CellSize * 1.5f, BlockWorld.CellSize * .5f), Vector3.right, 100, BlockWorld.CellSize * 3);
            var cells = Cells(next).ToArray();
            Assert.That(cells.Length, Is.EqualTo(4));
            Assert.That(cells.All(entry => entry.cell.x == 0 && entry.cell.y is 0 or 1 && entry.cell.z is 0 or 1), Is.True,
                "The disk cannot grow beyond the real wall edges or outward into another layer.");
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void UnregisteredOrUnpaintableGeometryCannotSupportACoating(bool registered, bool paintable)
        {
            MapBox(new Vector3(0, -.1f, 0), new Vector3(8, .2f, 8), registered, paintable);
            Assert.That(StageBuild(new Vector3(.3f, 0, .3f), Vector3.up, 20, .4f).PinkCount, Is.Zero);
        }

        [Test]
        public void ACoatingCannotSupplyTheOriginalMapSupportForAnotherCoating()
        {
            MapBox(new Vector3(0, -.1f, 0), new Vector3(8, .2f, 8));
            InstallStaged(StageBuild(new Vector3(.3f, 0, .3f), Vector3.up, 1, 0));
            var next = StageBuild(new Vector3(.3f, BlockWorld.CellSize, .3f), Vector3.up, 20, .4f);
            Assert.That(next.PinkCount, Is.EqualTo(1), "Even a misclassified map call cannot manufacture an unsupported second coat.");
            int callbacks = 0;
            _terrain.QueueBuild(new Vector3(.3f, .2f, .3f), Vector3.up, 1, 20, .4f, false,
                result => { callbacks++; Assert.That(result.Added, Is.Zero); });
            Assert.That(callbacks, Is.EqualTo(1), "The production lattice must reject an old friendly-stack request immediately.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MovementReadsCoatingOwnershipAtTheOriginalMapFace(bool wall)
        {
            Vector3 point = wall ? new Vector3(0, .3f, .3f) : new Vector3(.3f, 0, .3f);
            Vector3 normal = wall ? Vector3.right : Vector3.up;
            var map = wall ? MapBox(new Vector3(-.1f, 1, 0), new Vector3(.2f, 4, 4))
                : MapBox(new Vector3(0, -.1f, 0), new Vector3(8, .2f, 8));
            var next = StageBuild(point, normal, 1, 0); InstallStaged(next);
            Assert.That(Physics.Raycast(point + normal, -normal, out var hit, 2, 1 << map.gameObject.layer), Is.True);
            Assert.That(_terrain.TrySurfaceContactFromMap(hit, out var contact), Is.True);
            Assert.That(contact.Owner, Is.EqualTo(1)); Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.SurfaceAttached));
            Assert.That(contact.Point, Is.EqualTo(hit.point)); Assert.That(contact.Normal, Is.EqualTo(hit.normal));
            Assert.That(contact.Stable, Is.True); Assert.That(contact.Climbable, Is.EqualTo(wall));
            next.Set(contact.Cell, BlockCell.Create(2, surfaceAttached: true));
            Assert.That(_terrain.TrySurfaceContactFromMap(hit, out contact), Is.True); Assert.That(contact.Owner, Is.EqualTo(2));
        }

        [Test]
        public void CoatingCollisionIsIgnoredByPlayersAndRetainedForFreeBodiesAndProjectileQueries()
        {
            Assert.That(Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, 8), Is.True);
            Assert.That(Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, BlockDynamicWorld.Layer), Is.False);
            var world = _terrain.World.Clone(); var cell = new Vector3Int(1, 0, 1);
            world.Set(cell, BlockCell.Create(1, surfaceAttached: true));
            var mesh = BlockMeshBuilder.Build(world, BlockWorld.ChunkAt(cell), true).Upload("Coating collision contract");
            _meshes.Add(mesh);
            var go = new GameObject("Queryable surface collider") { layer = BlockTerrain.TerrainLayer };
            go.transform.SetParent(_root.transform, false); go.AddComponent<MeshCollider>().sharedMesh = mesh;
            Physics.SyncTransforms();
            Assert.That(Physics.SphereCast(BlockWorld.Center(cell) + Vector3.up, .01f, Vector3.down, out var hit,
                2, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.collider.gameObject, Is.SameAs(go));
            Assert.That(hit.point.y, Is.EqualTo(BlockWorld.CellSize).Within(.0001f));
        }

        [Test]
        public void ProtocolSixSnapshotAndDeltaRetainLegacyFlagsButRejectOldWorldMagic()
        {
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f)); Assert.That(BlockWorld.ProtocolVersion, Is.EqualTo(6));
            var world = new BlockWorld(Vector2.one, 7); world.Set(Vector3Int.zero, BlockCell.Create(1, surfaceAttached: true));
            world.Seal(1, new[] { Vector3Int.zero });
            var bytes = world.EncodeSnapshot(); var peer = BlockWorld.DecodeSnapshot(bytes);
            Assert.That(peer.TryGet(Vector3Int.zero, out var value), Is.True); Assert.That(value.Flags, Is.EqualTo(3));
            var next = world.Clone(); next.Set(Vector3Int.zero, BlockCell.Create(2, surfaceAttached: true));
            next.Seal(2, new[] { Vector3Int.zero });
            var changed = peer.WithDelta(next.EncodeDelta(1));
            Assert.That(changed.TryGet(Vector3Int.zero, out value), Is.True); Assert.That(value.Owner, Is.EqualTo(2));
            Assert.That(value.SurfaceAttached && value.Anchored, Is.True);
            foreach (byte oldMagic in new[] { (byte)'3', (byte)'4', (byte)'5' })
            {
                bytes[3] = oldMagic;
                Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(bytes));
                var delta = next.EncodeDelta(1); delta[3] = oldMagic;
                Assert.Throws<InvalidDataException>(() => peer.WithDelta(delta));
            }
        }

        [TestCase((byte)2)]
        [TestCase((byte)4)]
        public void CodecRejectsUnanchoredSurfaceAndUnknownFlagBits(byte flags)
        {
            var world = new BlockWorld(Vector2.one);
            world.Set(Vector3Int.zero, new BlockCell { Owner = 1, Flags = flags, Durability = 100 });
            world.Seal(1, new[] { Vector3Int.zero });
            Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(world.EncodeSnapshot()));
        }

        static Vector3 NonAlignedWallPoint => new(13, 1.55f, .35f);

        BoxCollider NonAlignedMapWall()
            => MapBox(new Vector3(12, 1.5f, 0), new Vector3(2, 3, 6));

        static RaycastHit NonAlignedWallHit(Collider expected)
        {
            Assert.That(Physics.Raycast(NonAlignedWallPoint + Vector3.right, Vector3.left, out var hit,
                2, 1 << expected.gameObject.layer, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.collider, Is.SameAs(expected), "Use a real original-map hit at the unobstructed centre of the footprint");
            Assert.That(hit.point.x, Is.EqualTo(13).Within(.00001f));
            Assert.That(Vector3.Dot(hit.normal, Vector3.right), Is.GreaterThan(.999f));
            return hit;
        }

        [Test]
        public void NonAlignedThirteenMetreWallKeepsTheNearestSingleLayerAndFullThirtyCentimetreGeometry()
        {
            var map = NonAlignedMapWall();
            var hit = NonAlignedWallHit(map);
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f));
            var first = BlockWorld.CellAt(hit.point + Vector3.right * (BlockWorld.CellSize * .51f));
            Assert.That(first.x, Is.EqualTo(43), "Keep the existing global lattice; do not move the coating to detached outward cell 44");

            var next = StageBuild(hit.point, hit.normal, 100, BlockWorld.CellSize * 2);
            var cells = Cells(next).ToArray();
            Assert.That(cells.Length, Is.EqualTo(13), "Retain the existing thirteen-cell two-radius disk and payment limit");
            Assert.That(cells.All(entry => entry.cell.x == 43 && entry.value.SurfaceAttached && entry.value.Anchored), Is.True);
            Assert.That(next.Falling.Count, Is.Zero); Assert.That(next.Cohorts.Count, Is.Zero);
            Assert.That(_terrain.Dynamic.Count, Is.Zero, "The first coat remains attached lattice material");

            foreach (var chunk in next.Chunks.Keys)
                foreach (bool collision in new[] { false, true })
                {
                    var data = BlockMeshBuilder.Build(next, chunk, collision);
                    Assert.That(data.Vertices.Length, Is.GreaterThan(0));
                    float thickness = data.Vertices.Max(vertex => vertex.x) - data.Vertices.Min(vertex => vertex.x);
                    Assert.That(thickness, Is.EqualTo(.30f).Within(.00001f), "Render and collision geometry retain full cubes instead of clipping them to the wall");
                }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonAlignedWallCoatStillRejectsAnotherColliderInsideItsExteriorFootprint(bool shellOnly)
        {
            var map = NonAlignedMapWall();
            var first = BlockWorld.CellAt(NonAlignedWallPoint + Vector3.right * (BlockWorld.CellSize * .51f));
            // The centre ray misses this small obstacle, while the requested
            // full cube overlaps it. Only the supporting wall may be exempted.
            Vector3 center = BlockWorld.Center(first);
            var obstacle = MapBox(center + (shellOnly ? new Vector3(.07f, .145f, .10f) : new Vector3(.07f, .10f, .10f)),
                shellOnly ? new Vector3(.06f, .004f, .02f) : new Vector3(.06f, .06f, .06f), paintable: false);
            if (shellOnly)
            {
                Assert.That(obstacle.bounds.min.y, Is.GreaterThan(center.y + BlockWorld.CellSize * .45f), "This obstacle is outside the old inset overlap and cast footprint");
                Assert.That(obstacle.bounds.max.y, Is.LessThan(center.y + BlockWorld.CellSize * .5f - .0001f), "The thin obstacle remains inside the real cube's exterior shell");
            }
            Assert.That(obstacle, Is.Not.SameAs(map));
            var hit = NonAlignedWallHit(map);
            Assert.That(StageBuild(hit.point, hit.normal, 1, 0).PinkCount, Is.Zero,
                "A support exception cannot admit a cube through a distinct unpaintable map obstacle");
        }

        MeshCollider NonAlignedMapMeshWithASeparatedFrontLobe()
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.SetActive(false);
            try
            {
                var unit = source.GetComponent<MeshFilter>().sharedMesh;
                var mesh = new Mesh { name = "One original collider with a distant outward lobe" };
                _meshes.Add(mesh);
                mesh.CombineMeshes(new[]
                {
                    new CombineInstance { mesh = unit, transform = Matrix4x4.TRS(new Vector3(12, 1.5f, 0), Quaternion.identity, new Vector3(2, 3, 6)) },
                    // This lobe is outside the local build footprint but belongs
                    // to the same collider and extends in front of x=13.
                    new CombineInstance { mesh = unit, transform = Matrix4x4.TRS(new Vector3(13.2f, 4, .35f), Quaternion.identity, Vector3.one * .2f) }
                }, true, true);
                mesh.RecalculateBounds();
                var go = new GameObject("Authored wall and front lobe in one collider");
                go.transform.SetParent(_root.transform, false);
                var collider = go.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                go.AddComponent<PaintSurface>(); MapColliders.Add(collider);
                Physics.SyncTransforms(); return collider;
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void NonAlignedWallCoatRejectsASupportColliderWhoseBoundsCrossTheSupportPlane()
        {
            var map = NonAlignedMapMeshWithASeparatedFrontLobe();
            var hit = NonAlignedWallHit(map);
            Assert.That(map.bounds.max.x, Is.GreaterThan(13.1f), "The real original collider contains geometry in front of its local x=13 wall face");
            Assert.That(StageBuild(hit.point, hit.normal, 1, 0).PinkCount, Is.Zero,
                "Conservatively refuse the support exception when the same collider cannot be proven entirely behind that face");
        }

        [Test]
        public void NonAlignedWallOwnershipStaysAtTheOriginalThirteenMetreMapPlane()
        {
            var map = NonAlignedMapWall();
            var hit = NonAlignedWallHit(map);
            var next = StageBuild(hit.point, hit.normal, 1, 0);
            Assert.That(next.PinkCount, Is.EqualTo(1)); InstallStaged(next);

            Assert.That(_terrain.TrySurfaceContactFromMap(hit, out var contact), Is.True);
            Assert.That(contact.Cell.x, Is.EqualTo(43));
            Assert.That(contact.Point.x, Is.EqualTo(13).Within(.00001f), "Ownership stays on the authored wall instead of the cube exterior at x=13.2");
            Assert.That(Vector3.Distance(contact.Point, hit.point), Is.LessThan(.00001f));
            Assert.That(contact.Normal, Is.EqualTo(hit.normal));
            Assert.That(contact.Owner, Is.EqualTo(1));
            Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.SurfaceAttached));
            Assert.That(contact.Stable && contact.Climbable, Is.True);
            Assert.That(next.TryGet(contact.Cell, out var value) && value.SurfaceAttached && value.Anchored, Is.True);
        }
    }
}
#endif
