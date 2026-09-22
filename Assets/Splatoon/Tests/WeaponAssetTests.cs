#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class WeaponAssetTests
    {
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        public static WeaponRuntimeConfig Changed(int id, Action<WeaponConfigAsset> edit)
        {
            var a = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(id).WeaponConfigPath));
            try { edit(a); return a.Snapshot(); }
            finally { UnityEngine.Object.DestroyImmediate(a); }
        }
        static PlayerSnapshot Alive(int id = 6) => new() { HeroId = id, Health = 100, Ink = 100, Grounded = true, Team = 1 };
        static bool Tick(ref PlayerSnapshot s, WeaponRuntimeConfig w, int frame, bool held, uint press = 1)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = held, FireSequence = press, Sequence = (uint)frame + 1 }, w, frame / 60.0, false, true);

        [Test] public void SixAssetsExactlyPreserveOriginalWeaponValues()
        {
            var baseline = SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponAssets/Migration-Baseline.json"));
            var rows = HeroMigrationTests.CombinedHeroes();
            Assert.That(rows.Children.Count(r => r["id"].AsInt <= 6), Is.EqualTo(6));
            foreach (var old in baseline.Children)
            {
                RapidBlasterTuningFixture.Apply(old["id"].AsInt, old, referenceFrames: true);
                PistolGirlTuningFixture.Apply(old["id"].AsInt, old, referenceFrames: true);
                DualPistolGirlTuningFixture.Apply(old["id"].AsInt, old, referenceFrames: true);
                WeaponAlignmentFixture.Apply(old["id"].AsInt, old, referenceFrames: true);
                var current = rows.Children.Single(r => r["id"].AsInt == old["id"].AsInt);
                foreach (string key in old.Keys)
                    if (key == "displayName") continue; // Renamed heroes are verified by the portrait selection tests.
                    else if (old[key].IsNumber) Assert.That(current[key].AsDouble, Is.EqualTo(old[key].AsDouble).Within(.00002), $"{old["id"]}/{key}");
                    else Assert.That(current[key].Value, Is.EqualTo(old[key].Value), key);
            }
            var characterFields = typeof(cfg.HeroConfig).GetFields().Select(f => f.Name).ToArray();
            Assert.That(characterFields, Does.Contain("WeaponConfigPath"));
            foreach (var f in typeof(WeaponRuntimeConfig).GetFields()) Assert.That(characterFields, Does.Not.Contain(f.Name));
        }
        [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidReloadPreservesLastValidSnapshot(float value)
        {
            var old = GameplayConfig.GetWeapon(6); uint revision = WeaponConfigService.Current.Revision(6);
            var invalid = Changed(6, a => a.spreadExpandSeconds = value);
            Assert.Throws<InvalidOperationException>(() => WeaponConfigService.Current.Replace(6, invalid));
            Assert.That(GameplayConfig.GetWeapon(6), Is.SameAs(old));
            Assert.That(WeaponConfigService.Current.Revision(6), Is.EqualTo(revision));
        }
        [TestCase(1)] [TestCase(4)]
        public void LegacyFirstShotIsZeroThenContinuousIntervalsExpand(int hero)
        {
            var w = WeaponAlignmentFixture.LegacySpread(hero); var s = Alive(hero); int first = -1;
            for (int t = 0; t <= 100; t++)
            {
                bool emitted = Tick(ref s, w, t, true);
                if (first < 0 && emitted) { first = t; Assert.That(s.LastShotSpread, Is.Zero); }
                if (first >= 0) Assert.That(s.SpreadProgress, Is.EqualTo(Mathf.Min(1, (t - first) / 60f)).Within(.00002));
            }
        }
        [Test] public void LegacyHalfProgressRecoversInHalfTheConfiguredTimeAndKeepsAirProgress()
        {
            var w = WeaponAlignmentFixture.LegacySpread(6); var s = Alive();
            s.SpreadInitialized = true; s.SpreadProgress = .5f;
            s.Grounded = false; SpreadSimulation.Refresh(ref s, w);
            Assert.That(s.CurrentSpread, Is.EqualTo(w.JumpSpreadDegrees*.5f)); Assert.That(s.CurrentVerticalSpread, Is.EqualTo(w.JumpSpreadDegrees*.5f));
            s.Grounded = true; SpreadSimulation.Before(ref s, w, .125);
            Assert.That(s.SpreadProgress, Is.EqualTo(.25f));
            Assert.That(s.CurrentSpread, Is.EqualTo(w.SpreadDegrees*.25f)); Assert.That(s.CurrentVerticalSpread, Is.EqualTo(w.SplatlingPitchSpread*.25f));
            SpreadSimulation.Before(ref s, w, .25);
            Assert.That(s.CurrentSpread, Is.Zero); Assert.That(s.CurrentVerticalSpread, Is.Zero);
        }
        [Test] public void LegacyCommittedBurstKeepsGrowingAfterReleaseUntilItsFinalRound()
        {
            var w = Changed(1, a => { WeaponAlignmentFixture.DisableReconstruction(a); a.referenceRules = false; a.referenceSpreadEnabled = false; a.motionMode = ProjectileMotionMode.Ballistic; a.fireMode = WeaponFireMode.Burst; a.burstCount = 3; a.fireRate = 15; a.startSeconds = 0 / 60.0; a.burstRecoverySeconds = 8 / 60.0; });
            var s = Alive(1);
            for (int t = 0; t <= 8; t++) Tick(ref s, w, t, t == 0);
            Assert.That(s.ShotSequence, Is.EqualTo(3)); Assert.That(s.SpreadFiring, Is.False);
            Assert.That(s.SpreadProgress, Is.EqualTo(8 / 60f).Within(.00001));
            Tick(ref s, w, 9, false);
            Assert.That(s.SpreadProgress, Is.EqualTo(.1f).Within(.00001));
        }
        [TestCase("spreadExpandSeconds")] [TestCase("spreadRecoverSeconds")] [TestCase("splatlingPitchSpread")]
        [TestCase("damage")] [TestCase("fireRate")] [TestCase("splatlingFullShootSeconds")]
        public void RoomSignatureIncludesWeaponAssetParameters(string field)
        {
            var hero = GameplayConfig.GetHero(6);
            var character = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/MachineGunGirl/Prefabs/MachineGunGirlVisual.prefab");
            var weapon = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Weapons/MachineGunGirl/Prefabs/MachineGun.prefab");
            var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab").GetComponent<PrototypePlayer>();
            Splatoon.Painting.InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(Splatoon.Painting.InkShapeAtlas.AssetPath));
            var old = new HeroContent(hero, character, weapon);
            var candidate = Changed(6, a =>
            {
                var f = typeof(WeaponConfigAsset).GetField(field);
                if (f.FieldType == typeof(double)) f.SetValue(a, (double)f.GetValue(a) + .1);
                else if (f.FieldType == typeof(int)) f.SetValue(a, (int)f.GetValue(a) + 1);
                else f.SetValue(a, (float)f.GetValue(a) + .1f);
            });
            var changed = new HeroContent(hero, character, weapon, candidate);
            CollectionAssert.AreNotEqual(GameplayContentSignature.Compute(new byte[] { 1 }, "map", player, new[] { old }),
                GameplayContentSignature.Compute(new byte[] { 1 }, "map", player, new[] { changed }));
        }
        [Test] public void LegacySplatlingReleaseGrowsThroughLastRoundAndEndingImmediatelyRecovers()
        {
            var w = WeaponAlignmentFixture.LegacySpread(6); var s = Alive();
            for (int t = 0; t < 27; t++) { Assert.That(Tick(ref s, w, t, true), Is.False); Assert.That(s.SpreadProgress, Is.Zero); }
            Assert.That(Tick(ref s, w, 27, false), Is.True); Assert.That(s.LastShotSpread, Is.Zero); Assert.That(s.LastShotVerticalSpread, Is.Zero);
            for (int t = 28; t <= 111; t++) Tick(ref s, w, t, false);
            Assert.That(s.ShotSequence, Is.EqualTo(22)); Assert.That(s.LastShotSpread, Is.EqualTo(w.SpreadDegrees)); Assert.That(s.LastShotVerticalSpread, Is.EqualTo(w.SplatlingPitchSpread));
            Assert.That(s.SpreadFiring, Is.False); Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Ending));
            Tick(ref s, w, 112, false);
            Assert.That(s.SpreadProgress, Is.EqualTo(1 - 1 / 30f).Within(.00001));
            for (int t = 113; t <= 123; t++) Tick(ref s, w, t, t >= 115, 2);
            Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Charging));
            Assert.That(s.SpreadProgress, Is.EqualTo(.6f).Within(.00002), "Charging keeps residual recovery, never resets it");
        }
        [Test] public void ShotgunRetainsBaseAndRocketStillTightensWithCharge()
        {
            var shotgun = LegacyShotgunFixture.Create();
            Assert.That(SpreadSimulation.Angles(shotgun, false, 0), Is.EqualTo(Vector2.one * 5));
            Assert.That(SpreadSimulation.Angles(shotgun, true, 0), Is.EqualTo(Vector2.one * 5));
            Assert.That(SpreadSimulation.Angles(shotgun, false, 1), Is.EqualTo(Vector2.one * 5));
            var rocket = LegacyChargeFixture.Create(); var s = Alive(5);
            for (int t = 0; t <= WeaponTimeFixture.ReferenceFrames(rocket.ChargeSeconds) + WeaponTimeFixture.ReferenceFrames(rocket.StartSeconds); t++) Tick(ref s, rocket, t, true);
            Assert.That(Tick(ref s, rocket, WeaponTimeFixture.ReferenceFrames(rocket.ChargeSeconds) + WeaponTimeFixture.ReferenceFrames(rocket.StartSeconds) + 1, false), Is.True);
            Assert.That(s.LastShotSpread, Is.EqualTo(rocket.SpreadDegrees)); Assert.That(s.SpreadProgress, Is.Zero);
            Assert.That(WeaponSimulation.Spread(rocket, false, 0), Is.GreaterThan(s.LastShotSpread));
        }
        [Test] public void LegacyZeroDurationUsesMaxForFinalShotBeforeImmediateRecovery()
        {
            var w = Changed(6, a => { WeaponAlignmentFixture.DisableReconstruction(a); a.referenceRules = false; a.referenceSpreadEnabled = false; a.motionMode = ProjectileMotionMode.Ballistic; a.spreadExpandSeconds = 0; a.spreadRecoverSeconds = 0; }); var s = Alive();
            for (int t = 0; t <= 8; t++) Tick(ref s, w, t, false);
            Assert.That(s.ShotSequence, Is.EqualTo(1)); Assert.That(s.LastShotSpread, Is.EqualTo(w.SpreadDegrees)); Assert.That(s.LastShotVerticalSpread, Is.EqualTo(w.SplatlingPitchSpread));
            Assert.That(s.CurrentSpread, Is.Zero); Assert.That(s.CurrentVerticalSpread, Is.Zero);
        }
        [Test] public void ZeroHorizontalNeverInfersVerticalOrAirState()
        {
            var w = GameplayConfig.GetWeapon(6); uint seed = 1729;
            for (int i = 0; i < 256; i++)
            {
                var v = InkBallistics.SplatlingVelocity(Vector3.forward, w, 1, 0, 0, ref seed);
                Assert.That(v.x, Is.Zero); Assert.That(v.y, Is.Zero);
                var y = InkBallistics.SplatlingVelocity(Vector3.forward, w, 1, 0, 2, ref seed);
                Assert.That(y.x, Is.Zero); Assert.That(Mathf.Abs(Mathf.Atan2(y.y, y.z) * Mathf.Rad2Deg), Is.LessThanOrEqualTo(2));
            }
        }
        [TestCase(60)] [TestCase(174)]
        public void ActionReloadRefundsActualBalanceOnceAndRequiresFreshPress(int at)
        {
            var old = GameplayConfig.GetWeapon(6); var s = Alive();
            for (int t = 0; t <= at; t++) Tick(ref s, old, t, t < 150);
            float expected = s.Ink + s.SplatlingReservedInk;
            var next = Changed(6, a => { a.fireRate = 12; a.shotInk = .9f; a.splatlingFullShootSeconds = 300 / 60.0; });
            Assert.That(old.RequiresRestart(next), Is.True);
            WeaponSimulation.Cancel(ref s, new PlayerInputFrame { FireSequence = 1 }, true);
            WeaponConfigService.Current.Replace(6, next);
            WeaponSimulation.Cancel(ref s, new PlayerInputFrame { FireSequence = 1 }, true);
            Assert.That(s.Ink, Is.EqualTo(expected).Within(.00001));
            for (int t = at + 1; t < at + 20; t++) Assert.That(Tick(ref s, next, t, true), Is.False);
            Tick(ref s, next, at + 20, false); Tick(ref s, next, at + 21, true, 2);
            Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Charging));
        }
        [Test] public void LegacyLiveSpreadChangePreservesProgressAndOldShotConfiguration()
        {
            var old = WeaponAlignmentFixture.LegacySpread(6); WeaponConfigService.Current.Replace(6, old); uint revision = WeaponConfigService.Current.Revision(6);
            var next = Changed(6, a => { WeaponAlignmentFixture.DisableReconstruction(a); a.referenceRules = false; a.referenceSpreadEnabled = false; a.motionMode = ProjectileMotionMode.Ballistic; a.spreadDegrees = 4; a.splatlingPitchSpread = 3; a.spreadExpandSeconds = 2; a.projectileGravity = 0; a.damage = 45; });
            var s = Alive(); s.SpreadProgress = .5f;
            Assert.That(old.RequiresRestart(next), Is.False); Assert.That(old.SameValues(next), Is.False);
            WeaponConfigService.Current.Replace(6, next); SpreadSimulation.Refresh(ref s, next);
            Assert.That(s.SpreadProgress, Is.EqualTo(.5f)); Assert.That(s.CurrentSpread, Is.EqualTo(2)); Assert.That(s.CurrentVerticalSpread, Is.EqualTo(1.5f));
            Assert.That(WeaponConfigService.Current.ForShot(6, revision), Is.SameAs(old));
            var shot = new InkShot { HeroId = 6, Configuration = old, Velocity = Vector3.forward * 100 };
            Assert.That(InkBallistics.Position(shot, shot.Configuration, 1).y, Is.LessThan(0));
            Assert.That(InkBallistics.Position(shot, next, 1).y, Is.Zero);
        }
        [Test] public void SpreadStateSurvivesNetworkSerializationAndReplay()
        {
            var w = GameplayConfig.GetWeapon(6); var a = Alive();
            for (int t = 0; t <= 145; t++) Tick(ref a, w, t, t < 120);
            using var writer = new FastBufferWriter(4096, Allocator.Temp);
            writer.WriteNetworkSerializable(a);
            using var reader = new FastBufferReader(writer, Allocator.Temp);
            reader.ReadNetworkSerializable(out PlayerSnapshot b);
            Assert.That(b.SpreadProgress, Is.EqualTo(a.SpreadProgress)); Assert.That(b.SpreadUpdatedAt, Is.EqualTo(a.SpreadUpdatedAt));
            for (int t = 146; t <= 280; t++)
            {
                Assert.That(Tick(ref b, w, t, false), Is.EqualTo(Tick(ref a, w, t, false)));
                Assert.That(b.CurrentSpread, Is.EqualTo(a.CurrentSpread)); Assert.That(b.LastShotVerticalSpread, Is.EqualTo(a.LastShotVerticalSpread));
            }
        }
        [TestCase(60f, 16f / 9)] [TestCase(90f, 16f / 9)] [TestCase(60f, 4f / 3)] [TestCase(75f, 21f / 9)]
        public void ReticleMatchesCameraProjection(float fov, float aspect)
        {
            var camera = new GameObject("Reticle projection test").AddComponent<Camera>();
            try
            {
                camera.fieldOfView = fov; camera.aspect = aspect;
                var size = ReticleGeometry.HalfSize(6, 3, fov, aspect);
                var viewport = camera.WorldToViewportPoint(new Vector3(Mathf.Tan(6 * Mathf.Deg2Rad), Mathf.Tan(3 * Mathf.Deg2Rad), 1));
                Assert.That(size.x, Is.EqualTo((viewport.x - .5f) * 1280).Within(.001));
                Assert.That(size.y, Is.EqualTo((viewport.y - .5f) * 720).Within(.001));
            }
            finally { UnityEngine.Object.DestroyImmediate(camera.gameObject); }
        }
    }
}
#endif
