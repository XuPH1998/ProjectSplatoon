#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridSpecialTargetTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _floor;
        SpecialWeaponConfigAsset _config;
        cfg.Tables _previousTables;

        [OneTimeSetUp] public void LoadConfiguration()
        {
            _previousTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }
        [OneTimeTearDown] public void RestoreConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _previousTables);
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("WaveBreaker fixed-support target regression");
            var arena = _root.AddComponent<PrototypeArena>();
            var floor = new GameObject("Original floor"); floor.transform.SetParent(_root.transform, false);
            floor.AddComponent<PaintSurface>().SurfaceId = 73;
            _floor = floor.AddComponent<BoxCollider>(); _floor.center = new Vector3(0, -.15f, 0); _floor.size = new Vector3(6, .3f, 6);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableFixedGridMode();
            _config = ScriptableObject.CreateInstance<SpecialWeaponConfigAsset>(); SpecialWeaponDefaults.Apply(_config, SpecialWeaponType.WaveBreaker);
            Physics.SyncTransforms();
        }
        [TearDown] public void Cleanup()
        {
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                Object.DestroyImmediate(_root); Object.DestroyImmediate(_config);
                Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        BlockFixedGridState Add(int y)
        {
            Assert.That(_terrain.TryGetSurfacePaint(_floor, new Vector3(.15f, 0, .15f), Vector3.up, out var paint), Is.True);
            if (paint.Owner != 1) Assert.That(_terrain.PaintSurfaceCell(paint.Key, 1), Is.True);
            var cell = new Vector3Int(0, y, 0);
            Assert.That(_terrain.FixedBlocks.Build(new[] { cell }, 1, 1).Added, Is.EqualTo(1), _terrain.FixedBlocks.LastFailure);
            Assert.That(_terrain.FixedBlocks.TryGet(cell, out var state), Is.True); return state;
        }
        SpecialWeaponService.Entity Attach(BlockFixedGridState support)
        {
            var entity = new SpecialWeaponService.Entity(new SpecialEntityState { Id = 77, Round = _terrain.FixedBlocks.Round,
                Team = 1, Type = SpecialWeaponType.WaveBreaker, Health = 100, Phase = SpecialEntityPhase.Active,
                Position = BlockWorld.Center(support.Cell) + Vector3.up * .15f }, _config.Snapshot())
            {
                BlockAttachment = true, BlockAttachmentCell = support.Cell, FixedAttachmentWorld = _terrain.FixedBlocks,
                FixedAttachmentHandle = support.Handle, FixedAttachmentOffset = Vector3.up * .15f
            };
            var go = new GameObject("Authoritative WaveBreaker target"); go.transform.SetParent(_root.transform, false);
            entity.Target = go.AddComponent<SpecialWeaponTarget>(); entity.Target.Initialize(new SpecialWeaponService(), entity);
            Physics.SyncTransforms(); return entity;
        }
        static bool Follow(SpecialWeaponService.Entity entity) => (bool)typeof(SpecialWeaponService)
            .GetMethod("TryFollowFixedAttachment", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { entity });
        static bool RayHits(SpecialWeaponService.Entity entity, Vector3 point)
        {
            foreach (var hit in Physics.RaycastAll(point + Vector3.back, Vector3.forward, 2,
                1 << entity.Target.gameObject.layer, QueryTriggerInteraction.Collide))
                if (hit.collider == entity.Target.HitCollider) return true;
            return false;
        }

        [Test] public void RestackedWaveBreakerMovesItsActualColliderBeforeSameStepQueries()
        {
            var bottom = Add(0); Add(1); Add(2); var support = Add(3); var entity = Attach(support);
            var capsule = (CapsuleCollider)entity.Target.HitCollider;
            Vector3 oldProbe = entity.Target.transform.TransformPoint(capsule.center + Vector3.up * (capsule.height * .5f - .02f));
            Assert.That(RayHits(entity, oldProbe), Is.True);
            _terrain.FixedBlocks.Destroy(new[] { bottom.Handle }, 2);
            Assert.That(_terrain.FixedBlocks.TryGet(support.Handle, out var moved), Is.True); Assert.That(moved.Cell.y, Is.EqualTo(2));
            Assert.That(Follow(entity), Is.True);
            Vector3 newProbe = entity.State.Position + capsule.center - Vector3.up * (capsule.height * .5f - .02f);
            Assert.That(RayHits(entity, newProbe), Is.True, "The authoritative hit shape must already be at its new support without a caller SyncTransforms.");
            Assert.That(RayHits(entity, oldProbe), Is.False, "The previous target location must not remain hittable after restacking.");
            Assert.That(entity.BlockAttachmentCell, Is.EqualTo(moved.Cell));
        }
        [Test] public void DeletedAndRecreatedSupportCannotMoveTheOldTargetToANewIdentity()
        {
            var support = Add(0); var entity = Attach(support); Vector3 before = entity.Target.transform.position;
            _terrain.FixedBlocks.Destroy(new[] { support.Handle }, 2); var replacement = Add(0);
            Assert.That(replacement.Handle, Is.Not.EqualTo(support.Handle));
            Assert.That(Follow(entity), Is.False); Assert.That(entity.Target.transform.position, Is.EqualTo(before));
        }
    }
}
#endif
