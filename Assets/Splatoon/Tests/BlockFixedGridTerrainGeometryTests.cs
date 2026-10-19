#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Run this category serially: both the production adapter and the fixture
    // touch global Physics state. No fake support adapter or injected face is used.
    [Category("BlockFixedGridTerrainGeometry")]
    public sealed class BlockFixedGridTerrainGeometryTests
    {
        const float Delta = 1f / 60;
        const float RampIntercept = .17f;
        const float RampGradient = .25f;
        static readonly Vector2Int Column = Vector2Int.zero;
        GameObject _root;
        BlockTerrain _terrain;
        readonly List<Mesh> _meshes = new();
        readonly bool[] _terrainLayerIgnores = new bool[32];

        [SetUp] public void Setup()
        {
            for (int layer = 0; layer < 32; layer++)
                _terrainLayerIgnores[layer] = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, layer);
            _root = new GameObject("Fixed-grid production terrain geometry contracts");
            _root.AddComponent<PrototypeArena>();
        }

        [TearDown] public void Cleanup()
        {
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                if (_root != null) Object.DestroyImmediate(_root);
                foreach (var mesh in _meshes) if (mesh != null) Object.DestroyImmediate(mesh);
                _meshes.Clear();
                for (int layer = 0; layer < 32; layer++)
                    Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, layer, _terrainLayerIgnores[layer]);
            }
        }

        BoxCollider Box(string name, Vector3 center, Vector3 size, int surfaceId)
        {
            var go = new GameObject(name); go.transform.SetParent(_root.transform, false);
            go.AddComponent<PaintSurface>().SurfaceId = surfaceId;
            var collider = go.AddComponent<BoxCollider>(); collider.center = center; collider.size = size;
            return collider;
        }

        BoxCollider Ramp()
        {
            // A finite thick map solid, with its real upper plane y=.17+.25*x.
            // The transform is authored before terrain initialization/face baking.
            float angle = Mathf.Atan(RampGradient) * Mathf.Rad2Deg;
            var rotation = Quaternion.Euler(0, 0, angle);
            Vector3 normal = rotation * Vector3.up;
            var collider = Box("Finite inclined map solid", Vector3.zero, new Vector3(4, .3f, 4), 6401);
            collider.transform.SetPositionAndRotation(Vector3.up * RampIntercept - normal * .15f, rotation);
            return collider;
        }

        Mesh NewMesh(string name, Vector3[] vertices, int[] triangles)
        {
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); _meshes.Add(mesh); return mesh;
        }

        MeshCollider MeshMap(string name, Mesh mesh, int surfaceId)
        {
            var go = new GameObject(name); go.transform.SetParent(_root.transform, false);
            go.AddComponent<PaintSurface>().SurfaceId = surfaceId;
            var collider = go.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; return collider;
        }

        Mesh BridgeMesh(bool includeBridge)
        {
            var vertices = new[]
            {
                new Vector3(-1.8f, 0, -1.8f), new Vector3(-1.8f, 0, 1.8f),
                new Vector3(1.8f, 0, 1.8f), new Vector3(1.8f, 0, -1.8f),
                new Vector3(-1.8f, 1.88f, -1.8f), new Vector3(-1.8f, 1.88f, 1.8f),
                new Vector3(1.8f, 1.88f, 1.8f), new Vector3(1.8f, 1.88f, -1.8f)
            };
            return NewMesh(includeBridge ? "Ground and noninteger bridge" : "Same map with bridge removed",
                vertices, includeBridge ? new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 } : new[] { 0, 1, 2, 0, 2, 3 });
        }

        void Initialize()
        {
            Physics.SyncTransforms();
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(_root.GetComponent<PrototypeArena>());
            _terrain.EnableFixedGridMode();
            // Public authority tick enables production recovery and map-change admission.
            _terrain.Tick(true, 1, Delta);
            Physics.SyncTransforms();
        }

        static RaycastHit MapHit(Collider collider, Vector2Int column, float rayStartY)
        {
            var center = BlockWorld.Center(new Vector3Int(column.x, 0, column.y));
            return MapHitAt(collider, center.x, center.z, rayStartY);
        }

        static RaycastHit MapHitAt(Collider collider, float x, float z, float rayStartY)
        {
            var ray = new Ray(new Vector3(x, rayStartY, z), Vector3.down);
            Assert.That(collider.Raycast(ray, out var hit, rayStartY + 8), Is.True,
                "The authored map collider must supply the actual finite support hit.");
            Assert.That(hit.normal.y, Is.GreaterThan(.05f)); return hit;
        }

        BlockSurfaceCell Paint(RaycastHit mapHit, byte team = 1)
        {
            Assert.That(_terrain.TryGetSurfacePaint(mapHit.collider, mapHit.point, mapHit.normal, out var before), Is.True,
                "The actual map hit must fit one complete 0.30m surface square.");
            Assert.That(before.Owner, Is.Not.EqualTo(team), "Fixture paints once before building a column.");
            var result = _terrain.BuildFixedImpact(mapHit.point, mapHit.normal, 0, team, 1, true);
            Assert.That(result.Added, Is.EqualTo(1));
            Assert.That(_terrain.SurfacePaints.TryGet(before.Key, out var after), Is.True);
            Assert.That(after.Owner, Is.EqualTo(team)); return after;
        }

        BlockFixedGridState ImpactMapBase(RaycastHit mapHit, byte team = 1)
        {
            var before = _terrain.FixedBlocks.Blocks.Keys.ToHashSet();
            var result = _terrain.BuildFixedImpact(mapHit.point, mapHit.normal, 0, team, 1, true);
            Assert.That(result.Added, Is.EqualTo(1), _terrain.FixedBlocks.LastFailure);
            var created = _terrain.FixedBlocks.Blocks.Values.Where(s => !before.Contains(s.Handle.Id)).ToArray();
            Assert.That(created.Length, Is.EqualTo(1), "An own-map impact adds one fixed block, not another paint cell.");
            return created[0];
        }

        BlockFixedGridState ImpactTop(BlockFixedGridState below, byte team = 1)
        {
            var point = BlockWorld.Center(below.Cell) + Vector3.up * (BlockWorld.CellSize * .5f);
            var before = _terrain.FixedBlocks.Blocks.Keys.ToHashSet();
            var result = _terrain.BuildFixedImpact(point, Vector3.up, 0, team, 1, false);
            Assert.That(result.Added, Is.EqualTo(1), _terrain.FixedBlocks.LastFailure);
            var created = _terrain.FixedBlocks.Blocks.Values.Where(s => !before.Contains(s.Handle.Id)).ToArray();
            Assert.That(created.Length, Is.EqualTo(1)); return created[0];
        }

        BlockFixedGridState[] Stack(RaycastHit mapHit, int layers)
        {
            var cells = new BlockFixedGridState[layers]; cells[0] = ImpactMapBase(mapHit);
            for (int i = 1; i < layers; i++) cells[i] = ImpactTop(cells[i - 1]);
            return cells;
        }

        void AssertColumn(BlockFixedGridState[] blocks, RaycastHit mapHit)
        {
            int first = BlockFixedGridMapSupport.GridBaseY(mapHit.point.y);
            var horizontal = BlockWorld.CellAt(mapHit.point);
            foreach (var pair in blocks.Select((state, index) => (state, index)))
            {
                Assert.That(pair.state.Cell.x, Is.EqualTo(horizontal.x));
                Assert.That(pair.state.Cell.z, Is.EqualTo(horizontal.z));
                Assert.That(pair.state.Cell.y, Is.EqualTo(first + pair.index));
                Assert.That(pair.state.Support.BaseCellY, Is.EqualTo(first));
                Assert.That(pair.state.Support.Height, Is.EqualTo(mapHit.point.y).Within(.0001f));
                var bounds = BlockFixedGridWorld.GetBounds(pair.state);
                Assert.That(bounds.min.y, Is.EqualTo(pair.state.Cell.y * .30f).Within(.00001f),
                    "A sloped/local support must not shift the global grid.");
                Assert.That(bounds.max.y - mapHit.point.y, Is.LessThanOrEqualTo(1.20001f));
            }
        }

        void AssertFifthRefused(BlockFixedGridState highest)
        {
            int count = _terrain.FixedBlocks.Count, score = _terrain.PinkCount;
            uint revision = _terrain.FixedBlocks.Revision, surfaceRevision = _terrain.SurfacePaints.Revision;
            var point = BlockWorld.Center(highest.Cell) + Vector3.up * (BlockWorld.CellSize * .5f);
            var result = _terrain.BuildFixedImpact(point, Vector3.up, 0, 1, 1, false);
            Assert.That(result.Added, Is.Zero); Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(count));
            Assert.That(_terrain.PinkCount, Is.EqualTo(score)); Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision));
            Assert.That(_terrain.SurfacePaints.Revision, Is.EqualTo(surfaceRevision));
        }

        [Test] public void InclinedSolidImpactsUseGlobalGridAndTheActualLocalFourLayerCap()
        {
            var ramp = Ramp(); Initialize(); var hit = MapHit(ramp, Column, 3);
            Assert.That(Vector3.Angle(hit.normal, Vector3.up), Is.InRange(13f, 15f));
            Assert.That(hit.point.y, Is.EqualTo(RampIntercept + RampGradient * .15f).Within(.0001f));
            Paint(hit); var blocks = Stack(hit, 4); AssertColumn(blocks, hit); AssertFifthRefused(blocks[3]);
            Assert.That(BlockFixedGridWorld.GetBounds(blocks[0]).min.y, Is.LessThan(hit.point.y));
            Assert.That(_root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(_terrain.FailedTransactions, Is.Zero); Assert.That(_terrain.PendingTransactions, Is.Zero);
        }

        [Test] public void JoinedSlopeAndPlatformKeepIndependentLocalBasesAcrossTheirActualMeshSeam()
        {
            const float seam = .6f, platformTop = .32f;
            var mesh = NewMesh("Continuous slope-platform seam", new[]
            {
                new Vector3(-.6f, .02f, -1.8f), new Vector3(-.6f, .02f, 1.8f),
                new Vector3(seam, platformTop, 1.8f), new Vector3(seam, platformTop, -1.8f),
                new Vector3(1.8f, platformTop, 1.8f), new Vector3(1.8f, platformTop, -1.8f)
            }, new[] { 0, 1, 2, 0, 2, 3, 3, 2, 4, 3, 4, 5 });
            var map = MeshMap("One real mesh containing the ramp and platform", mesh, 6402); Initialize();
            var leftSeam = MapHitAt(map, seam - .0001f, .15f, 3);
            var rightSeam = MapHitAt(map, seam + .0001f, .15f, 3);
            Assert.That(leftSeam.point.y, Is.EqualTo(platformTop - RampGradient * .0001f).Within(.00001f));
            Assert.That(rightSeam.point.y, Is.EqualTo(platformTop).Within(.00001f));
            Assert.That(Mathf.Abs(leftSeam.normal.x), Is.GreaterThan(.2f));
            Assert.That(Vector3.Dot(rightSeam.normal, Vector3.up), Is.GreaterThan(.99999f));
            // This slope column's entire footprint stays below y=.30. A column
            // whose high edge crosses y=.30 may legitimately reject layer two
            // as map overlap; that separate condition must not mask this case.
            var slopeHit = MapHit(map, Column, 3);
            var platformHit = MapHit(map, new Vector2Int(2, 0), 3);
            Paint(slopeHit); Paint(platformHit);
            var slope = Stack(slopeHit, 4); var platform = Stack(platformHit, 4);
            AssertColumn(slope, slopeHit); AssertColumn(platform, platformHit);
            Assert.That(slope[0].Cell.y, Is.Zero); Assert.That(platform[0].Cell.y, Is.EqualTo(1));
            AssertFifthRefused(slope[3]); AssertFifthRefused(platform[3]);
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(8)); Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [Test] public void SlopeSupportingFaceExemptionDoesNotAdmitAnAdjacentVerticalMapWall()
        {
            var ramp = Ramp();
            Box("Wall intersecting the rightmost 20mm of the base cell", new Vector3(.3f, 1.12f, .15f),
                new Vector3(.04f, 1.76f, 1.8f), 6403);
            Initialize(); var hit = MapHit(ramp, Column, 3); Paint(hit);
            uint revision = _terrain.FixedBlocks.Revision; int score = _terrain.PinkCount;
            var result = _terrain.BuildFixedImpact(hit.point, hit.normal, 0, 1, 1, true);
            Assert.That(result.Added, Is.Zero); Assert.That(_terrain.FixedBlocks.Count, Is.Zero);
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision)); Assert.That(_terrain.PinkCount, Is.EqualTo(score));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [Test] public void SlopeSupportingFaceExemptionDoesNotAdmitASeparateLowMapCeiling()
        {
            var ramp = Ramp();
            Box("Ceiling intersecting the top 30mm of the global base cube", new Vector3(.15f, .32f, .15f),
                new Vector3(1.8f, .1f, 1.8f), 6404);
            Initialize(); var hit = MapHit(ramp, Column, 3); Paint(hit);
            uint revision = _terrain.FixedBlocks.Revision; int score = _terrain.PinkCount;
            var result = _terrain.BuildFixedImpact(hit.point, hit.normal, 0, 1, 1, true);
            Assert.That(result.Added, Is.Zero); Assert.That(_terrain.FixedBlocks.Count, Is.Zero);
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision)); Assert.That(_terrain.PinkCount, Is.EqualTo(score));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [Test] public void RemovingBasesAboveAndBelowOneRealBridgeRestacksOnlyTheirOwnSupportLayers()
        {
            var map = MeshMap("Finite bridge and ground in one MeshCollider", BridgeMesh(true), 6405); Initialize();
            var groundHit = MapHit(map, Column, 1.5f); var bridgeHit = MapHit(map, Column, 4);
            Assert.That(groundHit.point.y, Is.EqualTo(0).Within(.00001f));
            Assert.That(bridgeHit.point.y, Is.EqualTo(1.88f).Within(.00001f));
            Paint(groundHit); Paint(bridgeHit);
            var ground = Stack(groundHit, 3); var bridge = Stack(bridgeHit, 3);
            Assert.That(bridge[0].Support.Id, Is.Not.EqualTo(ground[0].Support.Id));
            var removed = _terrain.FixedBlocks.Destroy(new[] { ground[0].Handle }, 2, 1, 0);
            Assert.That(removed.RemovedEnemy, Is.EqualTo(1)); Assert.That(removed.Added, Is.Zero);
            Assert.That(_terrain.FixedBlocks.TryGet(ground[0].Handle, out _), Is.False);
            for (int i = 1; i < ground.Length; i++)
            {
                Assert.That(_terrain.FixedBlocks.TryGet(ground[i].Handle, out var moved), Is.True);
                Assert.That(moved.Cell.y, Is.EqualTo(i - 1)); Assert.That(moved.Support.Id, Is.EqualTo(ground[0].Support.Id));
            }
            foreach (var expected in bridge)
            {
                Assert.That(_terrain.FixedBlocks.TryGet(expected.Handle, out var unchanged), Is.True);
                Assert.That(unchanged.Cell, Is.EqualTo(expected.Cell)); Assert.That(unchanged.Support.Id, Is.EqualTo(expected.Support.Id));
            }
            removed = _terrain.FixedBlocks.Destroy(new[] { bridge[0].Handle }, 2, 1, 0);
            Assert.That(removed.RemovedEnemy, Is.EqualTo(1));
            Assert.That(removed.Added, Is.Zero);
            Assert.That(_terrain.FixedBlocks.TryGet(bridge[0].Handle, out _), Is.False);
            for (int i = 1; i < bridge.Length; i++)
            {
                Assert.That(_terrain.FixedBlocks.TryGet(bridge[i].Handle, out var moved), Is.True);
                Assert.That(moved.Cell.y, Is.EqualTo(bridge[0].Cell.y + i - 1));
                Assert.That(moved.Support.Id, Is.EqualTo(bridge[0].Support.Id));
                Assert.That(BlockFixedGridWorld.GetBounds(moved).min.y, Is.GreaterThanOrEqualTo(1.8f - .00001f));
            }
            Assert.That(BlockFixedGridQueries.Cast(_terrain.FixedBlocks, new Vector3(.15f, .15f, -.5f), Vector3.forward, 1, 0, out var underHit), Is.True);
            Assert.That(underHit.State.Handle, Is.EqualTo(ground[1].Handle));
            Assert.That(BlockFixedGridQueries.Cast(_terrain.FixedBlocks, new Vector3(.15f, 1.95f, -.5f), Vector3.forward, 1, 0, out var overHit), Is.True);
            Assert.That(overHit.State.Handle, Is.EqualTo(bridge[1].Handle));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(4)); Assert.That(_terrain.FailedTransactions, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(6), "Four surviving blocks and two unchanged map paint cells remain.");
            Assert.That(_terrain.PendingTransactions, Is.Zero);
        }

        [Test] public void RemovingTheActualBridgeGeometryImmediatelyLandsOnTheFirstRemainingSupportedColumn()
        {
            var map = MeshMap("Mutable real bridge map", BridgeMesh(true), 6406); Initialize();
            var groundHit = MapHit(map, Column, 1.5f); var bridgeHit = MapHit(map, Column, 4);
            Paint(groundHit); Paint(bridgeHit);
            var ground = Stack(groundHit, 2); var bridge = Stack(bridgeHit, 2);
            int count = _terrain.FixedBlocks.Count, score = _terrain.PinkCount;
            map.sharedMesh = BridgeMesh(false); Physics.SyncTransforms();
            // This models support-index invalidation on one existing collider, not a
            // complete surface-paint topology rebake and not a free-body tick.
            Assert.That(_terrain.NotifyFixedMapSupportChanged(new Bounds(new Vector3(.15f, 1.5f, .15f),
                new Vector3(.29f, 4, .29f))), Is.True);
            foreach (var unchanged in ground)
            {
                Assert.That(_terrain.FixedBlocks.TryGet(unchanged.Handle, out var actual), Is.True);
                Assert.That(actual.Cell, Is.EqualTo(unchanged.Cell));
            }
            for (int i = 0; i < bridge.Length; i++)
            {
                Assert.That(_terrain.FixedBlocks.TryGet(bridge[i].Handle, out var moved), Is.True);
                Assert.That(moved.Cell, Is.EqualTo(new Vector3Int(0, 2 + i, 0)));
                Assert.That(moved.Support.Id, Is.EqualTo(ground[0].Support.Id));
                Assert.That(moved.Support.Height, Is.EqualTo(0).Within(.00001f));
            }
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(count)); Assert.That(_terrain.PinkCount, Is.EqualTo(score));
            Assert.That(_terrain.FixedBlocks.LastRelocatedCount, Is.EqualTo(2));
            Assert.That(BlockFixedGridQueries.Cast(_terrain.FixedBlocks, new Vector3(.15f, 1.3f, .15f), Vector3.down, 2, 0, out var top), Is.True);
            Assert.That(top.State.Handle, Is.EqualTo(bridge[1].Handle)); Assert.That(top.Point.y, Is.EqualTo(1.2f).Within(.00001f));
            Assert.That(_terrain.FailedTransactions, Is.Zero); Assert.That(_terrain.PendingTransactions, Is.Zero);
        }
    }
}
#endif
