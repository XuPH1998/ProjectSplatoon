#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class CameraReticleTests
    {
        readonly List<Object> objects = new();
        readonly Vector3 origin = new(1000, 1000, 1000);
        [SetUp] public void Setup() { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load(); }
        [TearDown] public void Cleanup()
        {
            foreach (var o in objects) if (o != null) Object.DestroyImmediate(o);
            objects.Clear(); LubanConfigService.Current.Reset();
        }
        GameObject Root(string name) { var root = new GameObject(name); objects.Add(root); return root; }
        PrototypePlayer Target(Vector3 at, byte team = 2, float health = 100)
        {
            var root = Root("Forecast target"); root.transform.position = at;
            root.AddComponent<NetworkObject>(); var player = root.AddComponent<PrototypePlayer>();
            player.GetComponent<CharacterController>().enabled = false;
            root.AddComponent<BoxCollider>().size = Vector3.one * .7f;
            player.Snapshot.Value = new PlayerSnapshot { HeroId = 1, Team = team, Health = health, Position = at };
            return player;
        }
        void Wall(float z)
        {
            var root = Root("Forecast wall"); root.transform.position = origin + Vector3.forward * z;
            root.AddComponent<BoxCollider>().size = new Vector3(50, 50, .1f);
        }
        WeaponImpactForecast Predict(int hero = 1, Vector3? muzzle = null)
        {
            Physics.SyncTransforms();
            var aim = new TpsAimSolution { Muzzle = muzzle ?? origin, InitialDirection = Vector3.forward, Forward = Vector3.forward };
            return WeaponImpactPrediction.Predict(new TpsAimSolver(), aim, GameplayConfig.GetWeapon(hero),
                new PlayerSnapshot { HeroId = hero, Team = 1, Health = 100, Grounded = true }, 999);
        }
        [TestCase(1)] [TestCase(2)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)]
        public void DirectEnemyContactIsAReadOnlyPrediction(int hero)
        {
            var target = Target(origin + Vector3.forward);
            var before = target.Snapshot.Value;
            var prediction = Predict(hero);
            Assert.That(prediction.HasEnemyContact, Is.True, "Direct contact for hero " + hero);
            Assert.That(prediction.Kind, Is.EqualTo(PredictedImpactKind.Player));
            Assert.That(target.Snapshot.Value.Health, Is.EqualTo(before.Health), "Prediction never damages");
            Assert.That(target.HitConfirmedUntil, Is.Zero, "Prediction never confirms hits");
            Assert.That(Predict(hero).EnemyPoint, Is.EqualTo(prediction.EnemyPoint), "No random UI motion");
        }
        [TestCase(1, 100)] [TestCase(2, 0)]
        public void FriendlyOrDeadPlayersDoNotTriggerTargetHint(byte team, float health)
        { Target(origin + Vector3.forward, team, health); Assert.That(Predict().HasEnemyContact, Is.False); }
        [Test] public void WorldOcclusionWinsOverAnEnemyBehindIt()
        { Target(origin + Vector3.forward * 4); Wall(1); Assert.That(Predict().HasEnemyContact, Is.False); Assert.That(Predict().Kind, Is.EqualTo(PredictedImpactKind.World)); }
        [Test] public void ACameraDirectionAloneDoesNotPromiseAnOutOfRangeHit()
        { Target(origin + Vector3.forward * 1000); Assert.That(Predict().HasEnemyContact, Is.False); }
        [Test] public void PiercingForecastKeepsEnemyAndLaterWorldContactSeparate()
        {
            var w = GameplayConfig.GetWeapon(3);
            var velocity = ExplosherSimulation.Launch(Vector3.forward, w, true);
            Target(origin + velocity.normalized * 1.5f); Wall(5);
            var forecast = Predict(3);
            Assert.That(forecast.HasEnemyContact, Is.True);
            Assert.That(forecast.Kind, Is.EqualTo(PredictedImpactKind.World));
            Assert.That(forecast.EnemyPoint.z, Is.LessThan(forecast.Point.z));
        }
        [Test] public void PiercingForecastDoesNotSeeEnemiesThroughWalls()
        {
            var velocity = ExplosherSimulation.Launch(Vector3.forward, GameplayConfig.GetWeapon(3), true);
            Target(origin + velocity.normalized * 5); Wall(1);
            Assert.That(Predict(3).HasEnemyContact, Is.False);
        }
        [Test] public void EmptyAirExpiryIsNotAnEnemyPrediction()
        { var result = Predict(5); Assert.That(result.Kind, Is.EqualTo(PredictedImpactKind.Expiry)); Assert.That(result.HasEnemyContact, Is.False); }
        [Test] public void ObstructionAndLowInkRemainDistinctFromTargetFeedback()
        {
            Assert.That(ReticleGeometry.StatusColor(true, true), Is.EqualTo(Color.red));
            Assert.That(ReticleGeometry.StatusColor(false, true), Is.EqualTo(Color.yellow));
            Assert.That(ReticleGeometry.StatusColor(false, false), Is.EqualTo(Color.white));
            Assert.That(ReticleGeometry.MergeImpact(Vector2.zero, Vector2.right * 3.99f), Is.True);
            Assert.That(ReticleGeometry.MergeImpact(Vector2.zero, Vector2.right * 4), Is.False);
        }
        [Test] public void EveryAuthoredCameraHasAFormContinuousPivotAndSignedFov()
        {
            int count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:CharacterPresentationProfile", new[] { "Assets/GameResource/Characters" }))
            {
                var p = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(p.CameraVerticalFov, Is.EqualTo(60), p.name);
                Assert.That(p.CameraReferenceHeight, Is.GreaterThan(.1f), p.name);
                Assert.That(p.Paper.CameraOffset, Is.EqualTo(p.CameraPivot), p.name);
                var state = new PlayerSnapshot { Health = 100, CameraRebaseOffset = -.4f };
                var pivot = PrototypePlayer.CameraPivotOffset(state, p); state.Swimming = true;
                Assert.That(PrototypePlayer.CameraPivotOffset(state, p), Is.EqualTo(pivot), p.name);
                state.CompactBody = true; state.Swimming = false;
                Assert.That(PrototypePlayer.CameraPivotOffset(state, p), Is.EqualTo(pivot), p.name);
                count++;
            }
            Assert.That(count, Is.EqualTo(9));
        }
        [Test] public void FovParticipatesInContentCompatibility()
        {
            Splatoon.Painting.InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(Splatoon.Painting.InkShapeAtlas.AssetPath));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var root = Object.Instantiate(prefab); objects.Add(root);
            var player = root.GetComponent<PrototypePlayer>();
            var clone = Object.Instantiate(player.Presentation); objects.Add(clone); player.CharacterView.Profile = clone;
            var before = GameplayContentSignature.Compute(new byte[0], "camera", player);
            clone.CameraVerticalFov = 61;
            Assert.That(GameplayContentSignature.Compute(new byte[0], "camera", player), Is.Not.EqualTo(before));
        }
    }
}
#endif
