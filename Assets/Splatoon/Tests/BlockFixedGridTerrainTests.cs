#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockFixedGridTerrain")]
    public sealed class BlockFixedGridTerrainTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        readonly List<Mesh> _meshes = new();
        readonly bool[] _terrainLayerIgnores = new bool[32];
        static readonly Vector3 Point = new(.15f, 0, .15f);

        [SetUp] public void Setup()
        {
            for (int layer = 0; layer < 32; layer++) _terrainLayerIgnores[layer] = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, layer);
            _root = new GameObject("Fixed-grid real map adapter contracts");
            _root.AddComponent<PrototypeArena>();
        }
        [TearDown] public void Cleanup()
        {
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
                foreach (var mesh in _meshes) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                _meshes.Clear();
                for (int layer = 0; layer < 32; layer++) Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, layer, _terrainLayerIgnores[layer]);
            }
        }
        BoxCollider Box(string name, Vector3 center, Vector3 size, int surfaceId)
        {
            var value = new GameObject(name); value.transform.SetParent(_root.transform, false);
            value.AddComponent<PaintSurface>().SurfaceId = surfaceId;
            var collider = value.AddComponent<BoxCollider>(); collider.center = center; collider.size = size; return collider;
        }
        BoxCollider Floor(float top = 0)
            => Box("Paintable original floor", new Vector3(0, top - .15f, 0), new Vector3(3.6f, .3f, 3.6f), 601);
        void Initialize()
        {
            Physics.SyncTransforms(); _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(_root.GetComponent<PrototypeArena>()); _terrain.EnableFixedGridMode(); Physics.SyncTransforms();
        }
        BlockSurfaceCell PaintAt(Collider collider, Vector3 point, byte team = 1)
        {
            Assert.That(_terrain.TryGetSurfacePaint(collider, point, Vector3.up, out var before), Is.True, "Fixture must contain one complete map square.");
            var result = _terrain.BuildFixedImpact(point, Vector3.up, 0, team, 1, true);
            Assert.That(result.Added, Is.EqualTo(before.Owner == team ? 0 : 1));
            Assert.That(_terrain.SurfacePaints.TryGet(before.Key, out var painted), Is.True); Assert.That(painted.Owner, Is.EqualTo(team)); return painted;
        }
        BlockFixedGridState BuildAt(Vector3Int candidate, byte team = 1)
        {
            int before = _terrain.FixedBlocks.Count;
            var result = _terrain.FixedBlocks.Build(new[] { candidate }, team, 1);
            Assert.That(result.Added, Is.EqualTo(1), _terrain.FixedBlocks.LastFailure);
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(before + 1), "A painted map must retain a solid fixed cell.");
            return _terrain.FixedBlocks.Blocks.Values.Where(state => state.Cell.x == candidate.x && state.Cell.z == candidate.z)
                .OrderByDescending(state => state.Cell.y).First();
        }
        [Test] public void BlankThenFriendlyMapImpactsPublishPaintThenOneFixedCellWithoutAnyNativeBody()
        {
            var floor = Floor(); Initialize(); int colliders = _root.GetComponentsInChildren<Collider>(true).Length;
            var painted = PaintAt(floor, Point);
            Assert.That(_terrain.FixedBlocks.Count, Is.Zero); Assert.That(_terrain.World.PinkCount, Is.Zero);
            Assert.That(_terrain.SurfacePaints.PinkCount, Is.EqualTo(1));
            var result = _terrain.BuildFixedImpact(Point, Vector3.up, 0, 1, 1, true);
            Assert.That(result.Added, Is.EqualTo(1)); Assert.That(_terrain.FixedBlocks.TryGet(Vector3Int.zero, out var state), Is.True);
            Assert.That(state.Owner, Is.EqualTo(1)); Assert.That(_terrain.PinkCount, Is.EqualTo(2));
            Assert.That(_terrain.SurfacePaints.TryGet(painted.Key, out var retained), Is.True); Assert.That(retained.Revision, Is.EqualTo(painted.Revision));
            Assert.That(_terrain.Dynamic, Is.Null); Assert.That(_root.GetComponentsInChildren<BlockDynamicWorld>(true), Is.Empty);
            Assert.That(_root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(_root.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(colliders), "Occupancy does not create a Collider per block.");
            Assert.That(BlockFixedGridQueries.Cast(_terrain.FixedBlocks, Point + Vector3.up, Vector3.down, 1, 0, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(state.Handle)); Assert.That(hit.Point.y, Is.EqualTo(.3f).Within(.00001));
        }
        [Test] public void FourLayersReachOnePointTwoMetresAndFifthAttemptSpendsNothing()
        {
            var floor = Floor(); Initialize(); PaintAt(floor, Point);
            for (int i = 0; i < 4; i++) Assert.That(BuildAt(new Vector3Int(0, 7, 0)).Cell.y, Is.EqualTo(i));
            uint revision = _terrain.FixedBlocks.Revision; int score = _terrain.PinkCount;
            var refused = _terrain.FixedBlocks.Build(new[] { new Vector3Int(0, 7, 0) }, 1, 1);
            Assert.That(refused.Added, Is.Zero); Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(4));
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision)); Assert.That(_terrain.PinkCount, Is.EqualTo(score));
            var highest = _terrain.FixedBlocks.Blocks.Values.Max(state => BlockFixedGridWorld.GetBounds(state).max.y);
            Assert.That(highest, Is.EqualTo(1.2f).Within(.00001)); Assert.That(_terrain.PendingTransactions, Is.Zero);
        }
        [Test] public void RemovingBaseImmediatelyRelocatesTheSurvivorAndUpdatesQueryBeforeAnyTick()
        {
            var floor = Floor(); Initialize(); PaintAt(floor, Point);
            var lower = BuildAt(new Vector3Int(0, 7, 0)); var upper = BuildAt(new Vector3Int(0, 7, 0)); uint revision = _terrain.Revision;
            int observed = 0;
            _terrain.FixedBlocks.Committed += commit =>
            {
                observed++; Assert.That(_terrain.FixedBlocks.TryGet(lower.Handle, out _), Is.False);
                Assert.That(_terrain.FixedBlocks.TryGet(upper.Handle, out var moved), Is.True); Assert.That(moved.Cell, Is.EqualTo(Vector3Int.zero));
                Assert.That(_terrain.World, Is.SameAs(_terrain.FixedBlocks.Storage)); Assert.That(_terrain.SurfacePaints, Is.SameAs(_terrain.FixedBlocks.Surfaces));
                Assert.That(BlockFixedGridQueries.Cast(_terrain.FixedBlocks, Point + Vector3.up, Vector3.down, 1, 0, out var hit), Is.True);
                Assert.That(hit.State.Handle, Is.EqualTo(upper.Handle)); Assert.That(hit.Point.y, Is.EqualTo(.3f).Within(.00001));
            };
            var result = _terrain.FixedBlocks.Destroy(new[] { lower.Handle }, 2);
            Assert.That(result.RemovedEnemy, Is.EqualTo(1)); Assert.That(observed, Is.EqualTo(1)); Assert.That(_terrain.Revision, Is.EqualTo(revision + 1));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(1)); Assert.That(_terrain.Dynamic, Is.Null);
        }
        [Test] public void MapPlaneInsideTheBaseGridCellRetainsOnlyThePermittedSupportingPenetration()
        {
            const float top = .08f; var floor = Floor(top); Initialize(); PaintAt(floor, Point + Vector3.up * top);
            var state = BuildAt(new Vector3Int(0, 7, 0)); var bounds = BlockFixedGridWorld.GetBounds(state);
            Assert.That(state.Cell.y, Is.Zero); Assert.That(state.Support.Height, Is.EqualTo(top).Within(.00001));
            Assert.That(bounds.min.y, Is.LessThan(top)); Assert.That(bounds.max.y, Is.EqualTo(.3f).Within(.00001));
            Assert.That(_terrain.World.TryGet(state.Cell, out var solid), Is.True); Assert.That(solid.Owner, Is.EqualTo(1));
        }
        [Test] public void SupportingFloorExemptionCannotAdmitTheSameBaseCubeThroughAnAdjacentWall()
        {
            var floor = Floor(.08f);
            Box("Map wall overlapping the candidate", new Vector3(.32f, .7f, .15f), new Vector3(.12f, 1.4f, 1.2f), 602);
            Initialize(); PaintAt(floor, Point + Vector3.up * .08f);
            uint revision = _terrain.Revision;
            var result = _terrain.FixedBlocks.Build(new[] { new Vector3Int(0, 7, 0) }, 1, 1);
            Assert.That(result.Added, Is.Zero); Assert.That(_terrain.FixedBlocks.Count, Is.Zero); Assert.That(_terrain.Revision, Is.EqualTo(revision));
        }
        [Test] public void OneMeshColliderKeepsIndependentBridgeTopAndLowerFloorSupport()
        {
            var map = new GameObject("Two real floors in one map mesh"); map.transform.SetParent(_root.transform, false);
            map.AddComponent<PaintSurface>().SurfaceId = 603;
            var mesh = new Mesh { name = "Finite bridge plus ground in one collider",
                vertices = new[] { new Vector3(-1.8f,0,-1.8f), new Vector3(-1.8f,0,1.8f), new Vector3(1.8f,0,1.8f), new Vector3(1.8f,0,-1.8f),
                    new Vector3(-1.8f,1.8f,-1.8f), new Vector3(-1.8f,1.8f,1.8f), new Vector3(1.8f,1.8f,1.8f), new Vector3(1.8f,1.8f,-1.8f) },
                triangles = new[] { 0,1,2,0,2,3,4,5,6,4,6,7 } };
            mesh.RecalculateBounds(); _meshes.Add(mesh); var collider = map.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
            Initialize(); PaintAt(collider, Point); PaintAt(collider, Point + Vector3.up * 1.8f);
            var under = BuildAt(new Vector3Int(0, 4, 0));
            Assert.That(under.Cell.y, Is.Zero); Assert.That(under.Support.Height, Is.EqualTo(0).Within(.00001));
            var above = BuildAt(new Vector3Int(0, 10, 0));
            Assert.That(above.Cell.y, Is.EqualTo(6)); Assert.That(above.Support.Height, Is.EqualTo(1.8f).Within(.00001));
            Assert.That(above.Support.Id, Is.Not.EqualTo(under.Support.Id));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(2));
        }
        [Test] public void FixedTerrainRejectsLegacyWorldImportAndClearZeroInvalidatesAllOldHandles()
        {
            var floor = Floor(); Initialize(); PaintAt(floor, Point); var state = BuildAt(new Vector3Int(0, 7, 0));
            byte[] legacyFull = _terrain.World.EncodeSnapshot(), legacyDelta = _terrain.World.EncodeDelta(0);
            uint revision = _terrain.Revision;
            Assert.That(_terrain.ApplyRemoteSnapshot(legacyFull), Is.False); Assert.That(_terrain.ApplyRemoteDelta(legacyDelta), Is.False);
            Assert.That(_terrain.Revision, Is.EqualTo(revision)); Assert.That(_terrain.FixedBlocks.TryGet(state.Handle, out _), Is.True);
            _terrain.Clear(7); uint epoch = _terrain.FixedBlocks.Epoch;
            _terrain.Clear(0);
            Assert.That(_terrain.Round, Is.Zero); Assert.That(_terrain.FixedBlocks.Epoch, Is.GreaterThan(epoch));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero); Assert.That(_terrain.FixedBlocks.Count, Is.Zero);
            Assert.That(_terrain.FixedBlocks.TryGet(state.Handle, out _), Is.False); Assert.That(_terrain.Dynamic, Is.Null);
        }
    }
}
#endif
