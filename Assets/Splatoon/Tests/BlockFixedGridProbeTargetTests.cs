#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Runs the actual Probe selector, logical TPS solver and authored Hero4 reference guide.
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridProbeTargetTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _floor;
        PrototypeMatch _oldMatch;
        BlockFixedGridAcceptanceProbe _probe;
        PrototypePlayer _player;
        readonly TpsAimSolver _aim = new();
        static readonly Type Probe = typeof(BlockFixedGridAcceptanceProbe);
        static readonly Type Agent = Probe.GetNestedType("Agent", BindingFlags.NonPublic);
        static readonly FieldInfo Current = typeof(PrototypeMatch).GetField("<Current>k__BackingField", PrivateStatic);

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            _root = new GameObject("Production Probe target regression");
            var arena = _root.AddComponent<PrototypeArena>();
            var floor = new GameObject("Authored map floor"); floor.transform.SetParent(_root.transform, false);
            floor.AddComponent<PaintSurface>().SurfaceId = 73;
            _floor = floor.AddComponent<BoxCollider>(); _floor.center = new Vector3(0, -.15f, 0); _floor.size = new Vector3(12, .3f, 20);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableFixedGridMode();

            var matchObject = new GameObject("Unspawned test match"); matchObject.transform.SetParent(_root.transform, false);
            matchObject.AddComponent<NetworkObject>(); var match = matchObject.AddComponent<PrototypeMatch>(); match.enabled = false;
            typeof(PrototypeMatch).GetField("<Blocks>k__BackingField", PrivateInstance).SetValue(match, _terrain);
            _oldMatch = PrototypeMatch.Current; Current.SetValue(null, match);

            // Keep the opt-in runtime Probe inactive: no report writer, socket or profiler startup.
            var probeObject = new GameObject("Inactive Probe fixture"); probeObject.SetActive(false); probeObject.transform.SetParent(_root.transform, false);
            _probe = probeObject.AddComponent<BlockFixedGridAcceptanceProbe>();
            var shooter = new GameObject("Authored Hero4 shooter"); shooter.transform.SetParent(_root.transform, false);
            shooter.AddComponent<NetworkObject>(); _player = shooter.AddComponent<PrototypePlayer>(); _player.enabled = false;
            _player.GetComponent<CharacterController>().enabled = false;
            var muzzle = new GameObject("Logical muzzle"); muzzle.transform.SetParent(shooter.transform, false); _player.SimulationMuzzle = muzzle.transform;
            _player.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/GameResource/Characters/PistolGirl/Prefabs/PistolGirlVisual.prefab").GetComponent<InkCharacterView>();
            Assert.That(_player.Presentation, Is.Not.Null);
            var weapon = GameplayConfig.GetWeapon(4);
            Assert.That(weapon.AimMode, Is.EqualTo(WeaponAimMode.WeaponReference));
            Assert.That(weapon.EffectiveRange, Is.EqualTo(9).Within(.00001f), "Keep the formal remover's authored range.");
            Physics.SyncTransforms();
        }
        [TearDown] public void Cleanup()
        {
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                try { Object.DestroyImmediate(_root); }
                finally
                {
                    Current.SetValue(null, _oldMatch);
                    Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
                    LubanConfigService.Current.Reset(); WeaponConfigService.Current.Clear();
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }
        static PlayerSnapshot State() => new()
        {
            HeroId = 4, HeroRevision = 1, Revision = 1, Team = 1, Slot = 2, Health = 100, Ink = 100,
            LifeState = PlayerLifeState.Alive, Grounded = true, Movement = MovementMode.Human,
            Position = new Vector3(.15f, 0, -3), LastShotCharge = 1
        };
        void PaintTop(byte owner)
        {
            Assert.That(_terrain.TryGetSurfacePaint(_floor, Vector3.zero, Vector3.up, out var paint), Is.True);
            Assert.That(_terrain.SurfacePaints.TryGetFace(paint.Key.FaceId, out var face), Is.True);
            var keys = new List<BlockSurfaceKey>();
            foreach (var cell in face.ValidCells) keys.Add(new BlockSurfaceKey(face.Id, cell.x, cell.y));
            Assert.That(keys.Count, Is.GreaterThan(0));
            var result = _terrain.FixedBlocks.Paint(keys, owner, keys.Count);
            Assert.That(result.Added, Is.EqualTo(keys.Count), _terrain.FixedBlocks.LastFailure);
        }
        object Select(PlayerSnapshot state)
        {
            var agent = Activator.CreateInstance(Agent, true);
            Agent.GetField("life").SetValue(agent, state.Revision);
            Agent.GetField("round").SetValue(agent, _terrain.FixedBlocks.Round);
            Agent.GetField("nextTargetDiagnostic").SetValue(agent, double.MaxValue);
            Probe.GetMethod("SelectEnemy", PrivateInstance).Invoke(_probe, new[] { (object)_player, state, agent });
            return agent;
        }
        static T Field<T>(object agent, string name) => (T)Agent.GetField(name).GetValue(agent);
        WeaponImpactForecast Forecast(PlayerSnapshot state, Vector3 target)
        {
            var look = (Vector2)Probe.GetMethod("AimAt", PrivateStatic).Invoke(null, new object[] { _player, state, target });
            state.Yaw = look.x; state.Pitch = look.y;
            var aim = _aim.Resolve(_player, state, state.NextMuzzle);
            return WeaponImpactPrediction.Guide(_aim, aim, GameplayConfig.GetWeapon(state.HeroId), state, _player.PlayerId);
        }
        bool Resolve(TpsCollision contact, out BlockContact hit)
        {
            object[] args = { _terrain, contact, default(BlockContact) };
            bool result = (bool)Probe.GetMethod("TryTargetContact", PrivateStatic).Invoke(null, args);
            hit = (BlockContact)args[2]; return result;
        }

        [Test] public void EnemyMapPaintIsReachableThroughProductionSelectorAndReferenceGuide()
        {
            PaintTop(2); var state = State(); var agent = Select(state);
            Assert.That(Field<bool>(agent, "hasEnemy"), Is.True);
            Assert.That(Field<bool>(agent, "reachableEnemy"), Is.True, "Map paint must satisfy the existing real-hit gate.");
            Assert.That(Field<long>(agent, "targetReachable"), Is.EqualTo(1));
            var contact = Forecast(state, Field<Vector3>(agent, "enemy")).Contact;
            Assert.That(contact.HasContact, Is.True); Assert.That(contact.Collider, Is.SameAs(_floor));
            Assert.That(contact.BlockQuery.IsKnown, Is.False, "Exercise the original unknown-map branch.");
            Assert.That(_terrain.TryContact(contact.Collider, contact.Point, contact.Normal, out _), Is.False, "The old fallback alone misses map paint.");
            Assert.That(Resolve(contact, out var hit), Is.True);
            Assert.That(hit.Kind, Is.EqualTo(BlockContactKind.SurfacePaint)); Assert.That(hit.Owner, Is.EqualTo(2));
        }
        [Test] public void FriendlyMapPaintDoesNotBecomeAnEnemyCandidate()
        {
            PaintTop(1); var state = State(); var agent = Select(state);
            Assert.That(Field<bool>(agent, "hasEnemy"), Is.False); Assert.That(Field<bool>(agent, "reachableEnemy"), Is.False);
            Assert.That(Field<long>(agent, "targetNoCandidates"), Is.EqualTo(1));
            var contact = Forecast(state, state.Position + Vector3.forward * 2).Contact;
            Assert.That(Resolve(contact, out var hit), Is.True); Assert.That(hit.Owner, Is.EqualTo(state.Team));
        }
        [Test] public void DeletedKnownHandleNeverFallsBackToEnemyPaintAtItsOldPoint()
        {
            var cell = Vector3Int.zero; Vector3 map = new(.15f, 0, .15f);
            Assert.That(_terrain.TryGetSurfacePaint(_floor, map, Vector3.up, out var paint), Is.True);
            Assert.That(_terrain.PaintSurfaceCell(paint.Key, 2), Is.True);
            Assert.That(_terrain.FixedBlocks.Build(new[] { cell }, 2, 1).Added, Is.EqualTo(1));
            Assert.That(_terrain.FixedBlocks.TryGet(cell, out var block), Is.True);
            Assert.That(_aim.ClosestCast(new Vector3(.15f, .15f, -1), Vector3.forward, 2, 0, 0, out var known, false), Is.True);
            Assert.That(known.BlockQuery.IsKnown, Is.True);
            Assert.That(_terrain.FixedBlocks.Destroy(new[] { block.Handle }, 1, 1, 0).RemovedEnemy, Is.EqualTo(1));
            Assert.That(known.BlockQuery.TryResolveCurrent(out _), Is.False);
            // A tempting fallback is deliberately supplied. The real deleted fixed handle still owns identity.
            known.Collider = _floor; known.Point = map; known.Normal = Vector3.up;
            Assert.That(_terrain.TrySurfacePaintContact(_floor, map, Vector3.up, out var fallback), Is.True);
            Assert.That(fallback.Owner, Is.EqualTo(2));
            Assert.That(Resolve(known, out _), Is.False, "Stale known identity must stop before unknown-map fallback.");
        }
        [Test] public void EnemyFixedBlockRemainsReachableWithItsStableIdentity()
        {
            // Keep this target inside the authored four-frame Guide window.
            // EffectiveRange alone does not establish that the Guide reaches it.
            var cell = new Vector3Int(0, 0, 14); Vector3 map = BlockWorld.Center(cell); map.y = 0;
            Assert.That(_terrain.TryGetSurfacePaint(_floor, map, Vector3.up, out var paint), Is.True);
            Assert.That(_terrain.PaintSurfaceCell(paint.Key, 2), Is.True);
            Assert.That(_terrain.FixedBlocks.Build(new[] { cell }, 2, 1).Added, Is.EqualTo(1));
            Assert.That(_terrain.FixedBlocks.TryGet(cell, out var block), Is.True);
            Assert.That(_terrain.PaintSurfaceCell(paint.Key, 1), Is.True, "Only the fixed block should be an enemy target.");
            var state = State(); Assert.That(Vector3.Distance(state.Position, BlockWorld.Center(cell)), Is.LessThan(9));
            var target = BlockWorld.Center(cell);
            var fixtureForecast = Forecast(state, target);
            double horizon = Math.Min(GameplayConfig.GetWeapon(state.HeroId).ShotGuideSeconds,
                GameplayConfig.GetWeapon(state.HeroId).Lifetime);
            string diagnostic = $"Guide kind={fixtureForecast.Kind}, point={fixtureForecast.Point}, age={fixtureForecast.Age}, horizon={horizon}, feetDistance={Vector3.Distance(state.Position, target)}";
            Assert.That(fixtureForecast.Contact.HasContact, Is.True, diagnostic);
            Assert.That(fixtureForecast.Contact.BlockQuery.IsKnown, Is.True, diagnostic);
            Assert.That(fixtureForecast.Contact.BlockQuery.Contact.FixedHandle, Is.EqualTo(block.Handle), diagnostic);
            Assert.That(fixtureForecast.Age, Is.LessThanOrEqualTo(horizon), diagnostic);
            var agent = Select(state); Assert.That(Field<bool>(agent, "reachableEnemy"), Is.True);
            var contact = Forecast(state, Field<Vector3>(agent, "enemy")).Contact;
            Assert.That(contact.BlockQuery.IsKnown, Is.True); Assert.That(Resolve(contact, out var hit), Is.True);
            Assert.That(hit.Kind, Is.EqualTo(BlockContactKind.FixedGrid)); Assert.That(hit.FixedHandle, Is.EqualTo(block.Handle));
            Assert.That(hit.Owner, Is.EqualTo(2));
        }
    }
}
#endif
