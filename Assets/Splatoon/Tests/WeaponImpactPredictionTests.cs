#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class WeaponImpactPredictionTests
    {
        readonly List<Object> objects = new();
        static readonly string[] Names = { "", "RifleGirl", "DualPistolGirl", "ShotgunGirl", "PistolGirl", "RocketLauncherGirl", "MachineGunGirl", "BubbleGirl", "SplooshGirl", "BubbleShotgunGirl" };
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
        }
        [TearDown] public void Cleanup()
        {
            foreach (var o in objects) if (o != null) Object.DestroyImmediate(o);
            objects.Clear(); LubanConfigService.Current.Reset();
        }
        GameObject Root(string name) { var o = new GameObject(name); objects.Add(o); return o; }
        WeaponRuntimeConfig Weapon(int hero)
        {
            var asset = Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(hero).WeaponConfigPath));
            objects.Add(asset);
            // Remove speed randomness from the actual emitter to compare its centre trajectory.
            asset.floatingPitchSpreadDegrees = 0;
            asset.speedMin = asset.speedMax = (asset.speedMin + asset.speedMax) * .5f;
            var w = asset.Snapshot(); WeaponConfigService.Current.SetForEditor(hero, w); return w;
        }
        PrototypePlayer Player(int hero)
        {
            var o = Root("Prediction shooter"); o.AddComponent<NetworkObject>();
            var player = o.AddComponent<PrototypePlayer>(); player.GetComponent<CharacterController>().enabled = false;
            player.SimulationMuzzle = Root("Muzzle").transform; player.SimulationMuzzle.SetParent(o.transform);
            player.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{Names[hero]}/Prefabs/{Names[hero]}Visual.prefab").GetComponent<InkCharacterView>();
            return player;
        }
        static IEnumerable<TestCaseData> Contacts()
        {
            for (int hero = 1; hero <= 9; hero++)
                foreach (float pitch in new[] { 0f, 35f }) yield return new TestCaseData(hero, pitch, 1f, 0, false, hero == 2 ? 1 : 0);
            yield return new TestCaseData(6, 0f, .15f, 0, false, 0);
            yield return new TestCaseData(6, 0f, .15f, 0, true, 0);
            yield return new TestCaseData(7, 0f, 1f, 2, false, 0);
            foreach (float pitch in new[] { 0f, 35f }) yield return new TestCaseData(2, pitch, 1f, 0, false, 0);
        }
        [TestCaseSource(nameof(Contacts))]
        public void ForecastMatchesActualEmitterAndFirstPhysicsContact(int hero, float pitch, float charge, int bubbleIndex, bool firing, int muzzle)
        {
            var w = Weapon(hero); var player = Player(hero); var solver = new TpsAimSolver();
            var floor = Root("Landing floor").AddComponent<BoxCollider>();
            floor.transform.position = new Vector3(1000, 999.5f, 1000); floor.size = new Vector3(200, 1, 200);
            var state = new PlayerSnapshot { HeroId = hero, Health = 100, Ink = 100, Team = 1, Revision = 1,
                Position = new Vector3(1000, 1004, 1000), Pitch = pitch, Grounded = true, ShotSequence = 1,
                WeaponPhase = firing || bubbleIndex > 0 ? WeaponPhase.Firing : WeaponPhase.Charging,
                SplatlingChargeSeconds = charge * w.ChargeSeconds, SplatlingReleasedCharge = charge,
                SplatlingRemaining = firing ? 10 : 0, LastShotCharge = charge,
                BurstShotIndex = (byte)bubbleIndex, BurstRemaining = bubbleIndex > 0 ? 2 : 0,
                NextMuzzle = (byte)muzzle, LastShotMuzzle = (byte)muzzle };
            Physics.SyncTransforms();
            var aim = solver.Resolve(player, state, state.NextMuzzle);
            Assert.That(WeaponImpactPrediction.TryPredict(solver, aim, w, state, player.PlayerId, out var predicted), Is.True);
            // The prediction previews the next volley member; Spawn consumes the emitted index.
            state.BurstShotIndex = (byte)(bubbleIndex + 1);
            var service = new InkProjectileService(); service.Spawn(player, state, 10, 1); service.Simulate(10 + w.Lifetime);
            Vector3 actual = service.Bounces.Count > 0 ? floor.ClosestPoint(service.Bounces[0].Position)
                : service.Explosions.Count > 0 ? service.Explosions[0].Position : service.Impacts.First().Position;
            Assert.That(Vector3.Distance(predicted, actual), Is.LessThan(.002f), $"Hero {hero} centre forecast versus authority");
        }
        [TestCase(1, false)] [TestCase(3, false)] [TestCase(5, true)] [TestCase(7, false)]
        public void ClearAirShowsOnlyARealTimedExplosion(int hero, bool visible)
        {
            var w = Weapon(hero);
            var aim = new TpsAimSolution { Muzzle = new Vector3(1000, 1000, 1000), InitialDirection = Vector3.forward };
            var state = new PlayerSnapshot { HeroId = hero, Health = 100, Team = 1, Grounded = true };
            Assert.That(WeaponImpactPrediction.TryPredict(new TpsAimSolver(), aim, w, state, 999, out _), Is.EqualTo(visible));
        }
    }
}
#endif
