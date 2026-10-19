#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockSurfacePaint")]
    public sealed class BlockSurfaceImpactTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        Collider _floor;
        cfg.Tables _previousTables;
        static readonly Vector3 Point = new(.15f, 0, .15f);
        [OneTimeSetUp] public void LoadTokenConfiguration()
        {
            _previousTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }
        [OneTimeTearDown] public void RestoreTokenConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _previousTables);
        [SetUp] public void Setup()
        {
            _root = new GameObject("Ordinary surface impact contracts");
            var arena = _root.AddComponent<PrototypeArena>();
            var floor = new GameObject("Original floor"); floor.transform.SetParent(_root.transform, false);
            floor.AddComponent<PaintSurface>().SurfaceId = 73;
            var box = floor.AddComponent<BoxCollider>(); box.center = new Vector3(0, -.15f, 0); box.size = new Vector3(3.6f, .3f, 3.6f); _floor = box;
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableSurfaceMode();
            _terrain.Dynamic.SetAuthority(true); Physics.SyncTransforms();
        }
        [TearDown] public void Cleanup()
        {
            Object.DestroyImmediate(_root); Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
        }
        BlockSurfaceCell Cell()
        { Assert.That(_terrain.TryGetSurfacePaint(_floor, Point, Vector3.up, out var cell), Is.True); return cell; }

        [Test] public void BlankMapImpactPublishesThirteenFlatSquaresWithoutAnyNewColliderOrVoxel()
        {
            int colliders = _root.GetComponentsInChildren<Collider>(true).Length;
            int added = _terrain.BuildOrPaintMapSurface(Point, Vector3.up, .6f, 1, 100, out bool free);
            Assert.That(added, Is.EqualTo(13)); Assert.That(free, Is.False);
            Assert.That(_terrain.PinkCount, Is.EqualTo(13)); Assert.That(_terrain.Dynamic.Count, Is.Zero);
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.Zero);
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_root.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(colliders));
            foreach (var cell in _terrain.SurfacePaints.EnumeratePainted())
                Assert.That(_terrain.SurfacePaintCenter(cell.Key).y, Is.EqualTo(0).Within(.00001));
        }

        [Test] public void EnemyColourReplacementTransfersScoreImmediatelyAndConsumesOnlyOneBuildCredit()
        {
            Assert.That(_terrain.PaintSurfaceCell(Cell().Key, 2), Is.True);
            var token = new BlockAttackToken(new PaintCredit(1, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, 4, 8, 2);
            int reserved = token.ReserveBuild(4, false, 19);
            int actual = _terrain.BuildOrPaintMapSurface(Point, Vector3.up, 0, 1, reserved, out bool free);
            token.CompleteBuild(reserved, false, actual, 19);
            Assert.That(actual, Is.EqualTo(1)); Assert.That(free, Is.False);
            Assert.That(Cell().Owner, Is.EqualTo(1)); Assert.That(_terrain.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.Zero);
            Assert.That(token.BuildRemaining, Is.EqualTo(3)); Assert.That(token.Added, Is.EqualTo(1)); Assert.That(token.RemovedEnemy, Is.Zero);
            Assert.That(token.TakePoints(), Is.EqualTo(1d / token.BlocksPerPoint)); Assert.That(token.TakePoints(), Is.Zero);
            Assert.That(_terrain.Dynamic.Count, Is.Zero);
        }

        [Test] public void OwnColourMapImpactCreatesFreeMassImmediatelyWithoutAnotherPaintLayer()
        {
            Assert.That(_terrain.PaintSurfaceCell(Cell().Key, 1), Is.True); uint revision = _terrain.SurfacePaints.Revision;
            Assert.That(_terrain.BuildOrPaintMapSurface(Point, Vector3.up, 0, 1, 1, out bool free), Is.EqualTo(1));
            Assert.That(free, Is.True); Assert.That(_terrain.Dynamic.Count, Is.EqualTo(1));
            Assert.That(_terrain.SurfacePaints.Revision, Is.EqualTo(revision)); Assert.That(_terrain.SurfacePaints.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(2)); Assert.That(_terrain.World.PinkCount, Is.Zero);
        }

        [Test] public void UnsupportedMapPointReturnsTheEntireReservationAndDoesNotBuildAFreeBody()
        {
            Assert.That(_terrain.BuildOrPaintMapSurface(Point + Vector3.up * .3f, Vector3.up, .6f, 1, 100, out bool free), Is.Zero);
            Assert.That(free, Is.False); Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
        }

        [Test] public void ACompleteMapSquareStillCannotExtendBeyondTheConstructionWorldBounds()
        {
            var world = _terrain.SurfacePaints;
            world.RegisterFace(900, Vector3.up * (BlockWorld.Top + .01f), Vector3.right, Vector3.back,
                new[] { Vector2Int.zero });
            world.RegisterFace(901, new Vector3(_terrain.World.Dimensions.x * .5f - .15f, 0, 0), Vector3.right, Vector3.back,
                new[] { Vector2Int.zero });
            Assert.That(_terrain.PaintSurfaceCell(new BlockSurfaceKey(900, 0, 0), 1), Is.False);
            Assert.That(_terrain.PaintSurfaceCell(new BlockSurfaceKey(901, 0, 0), 1), Is.False,
                "The whole 0.30 metre square must fit, including its far edge.");
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
            Assert.That(world.Revision, Is.Zero);
        }

        [Test] public void BlastClearsColourWithoutConvertingItOrTheSurvivingBodies()
        {
            _terrain.PaintSurfaceCell(Cell().Key, 2);
            Assert.That(_terrain.Dynamic.TrySpawn(1, new Vector3(1.2f, 1, 0), Quaternion.identity, out var body), Is.True);
            var result = _terrain.DestroySurfacePaint(Point + Vector3.up * .01f, .2f, .05f, 1, false);
            Assert.That(result.RemovedEnemy, Is.EqualTo(1)); Assert.That(result.Added, Is.Zero);
            Assert.That(Cell().Owner, Is.Zero); Assert.That(_terrain.Dynamic.Contains(body), Is.True);
            Assert.That(_terrain.SurfacePaints.PinkCount + _terrain.SurfacePaints.BlueCount, Is.Zero);
        }

        [Test] public void SurfaceQueryBecomesStaleOnReplacementAndCannotResolveThroughRoundReset()
        {
            _terrain.PaintSurfaceCell(Cell().Key, 1);
            Assert.That(_terrain.TrySurfacePaintContact(_floor, Point, Vector3.up, out var contact), Is.True);
            var stable = typeof(BlockQueryContact).GetMethod("Stable", BindingFlags.Static | BindingFlags.NonPublic);
            var query = (BlockQueryContact)stable.Invoke(null, new object[] { _terrain, contact });
            Assert.That(query.TryResolveCurrent(out _), Is.True);
            _terrain.PaintSurfaceCell(Cell().Key, 2); Assert.That(query.TryResolveCurrent(out _), Is.False);
            _terrain.Clear(_terrain.Round + 1); _terrain.PaintSurfaceCell(Cell().Key, 1);
            Assert.That(query.TryResolveCurrent(out _), Is.False);
        }

        [Test] public void RoundClearObserversNeverSeeTheOldPaintWithTheNewEmptyDynamicWorld()
        {
            _terrain.PaintSurfaceCell(Cell().Key, 1); uint next = _terrain.Round + 1;
            int observed = 0;
            _terrain.Dynamic.Changed += () =>
            { observed++; Assert.That(_terrain.SurfacePaints.Round, Is.EqualTo(next)); Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero); };
            _terrain.Clear(next); Assert.That(observed, Is.EqualTo(1));
        }
    }
}
#endif
