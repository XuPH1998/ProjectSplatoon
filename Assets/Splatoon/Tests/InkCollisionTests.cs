#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class InkCollisionTests
    {
        private Scene _scene;
        private PrototypePlayer _player;
        [SetUp] public void SetUp()
        {
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json")))));
            var root = new GameObject("Test shooter"); root.AddComponent<Unity.Netcode.NetworkObject>();
            _player = root.AddComponent<PrototypePlayer>();
            _player.GetComponent<CharacterController>().enabled = false;
            _player.SimulationMuzzle = new GameObject("Muzzle").transform;
            _player.SimulationMuzzle.SetParent(root.transform); _player.SimulationMuzzle.localPosition = Vector3.up * 1.5f;
        }
        [TearDown] public void TearDown()
        {
            foreach (var root in _scene.GetRootGameObjects()) Object.DestroyImmediate(root);
            LubanConfigService.Current.Reset();
        }
        private static void Wall(float z, float thickness = .01f)
        {
            var wall = new GameObject("Thin wall"); wall.transform.position = new Vector3(0, 1.5f, z);
            wall.AddComponent<BoxCollider>().size = new Vector3(20, 20, thickness); Physics.SyncTransforms();
        }
        private InkProjectileService Shot()
        {
            var service = new InkProjectileService();
            service.Spawn(_player, new PlayerSnapshot { Team = 1 }, 0, 1); return service;
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void SweptBallHitsNearestThinWallOnlyOnce(int frameRate)
        {
            Wall(3); Wall(4); var service = Shot();
            for (int i = 1; i <= frameRate; i++) service.Simulate((double)i / frameRate);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Impacts.Count, Is.EqualTo(1));
            Assert.That(service.Impacts[0].Hit, Is.True); Assert.That(service.Impacts[0].Position.z, Is.EqualTo(2.995f).Within(.01f));
        }
        [Test] public void ExpiryAndRoundClearDoNotCreatePaintHits()
        {
            var service = Shot(); service.Simulate(GameplayConfig.Weapon.Lifetime + .01);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.Impacts[0].Hit, Is.False);
            service = Shot(); service.Clear(); service.Simulate(1);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Spawned, Is.Empty); Assert.That(service.Impacts, Is.Empty);
        }
        [Test] public void MuzzleInsideColliderResolvesOnce()
        {
            Wall(.001f, .02f); var service = Shot(); service.Simulate(.1); service.Simulate(.2);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.Impacts[0].Hit, Is.True);
        }
    }
}
#endif
