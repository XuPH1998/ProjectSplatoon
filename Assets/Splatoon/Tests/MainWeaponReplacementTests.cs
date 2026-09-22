#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class MainWeaponReplacementTests
    {
        const double S = 18.0 / 24.037;
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();

        [TestCase(6, "WeaponSpinnerQuick", "斯普拉旋转枪")]
        [TestCase(8, "WeaponShooterBlaze", "专业模型枪MG")]
        public void FixedSourceMatchesLiveWeaponAndIdentity(int id, string source, string display)
        {
            var w = GameplayConfig.GetWeapon(id);
            WeaponConfigValidation.Validate(w);
            Assert.That(GameplayConfig.GetHero(id).WeaponTypeName, Is.EqualTo(display));
            var data = File.ReadAllBytes($"Tools/ValidationData/MainWeaponReplacement/{source}.1130.json");
            var p = JSONNode.Parse(System.Text.Encoding.UTF8.GetString(data))["GameParameters"];
            var ledger = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/MainWeaponReplacement/Tuning.json"))["weapons"][id.ToString()];
            using var hash = SHA256.Create();
            Assert.That(BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(), Is.EqualTo(ledger["sha256"].Value));
            Assert.That(w.Damage, Is.EqualTo(p["DamageParam"]["ValueMax"].AsFloat / 10));
            Assert.That(w.DamageMin, Is.EqualTo(p["DamageParam"]["ValueMin"].AsFloat / 10));
            Assert.That(w.FireRate, Is.EqualTo(60 / p["WeaponParam"]["RepeatFrame"].AsFloat));
            Assert.That(w.ShootMoveSpeed, Is.EqualTo(p["WeaponParam"]["MoveSpeed"].AsDouble * 60 * S).Within(1e-5));
            Assert.That(w.ReferencePlayerRadius, Is.EqualTo(p["CollisionParam"]["EndRadiusForPlayer"].AsDouble * S).Within(1e-6));
            Assert.That(w.ReferenceTrailBudget, Is.EqualTo(p["SplashSpawnParam"]["SpawnNum"].AsFloat));
            Assert.That(w.ShooterSplitNum, Is.EqualTo(p["SplashSpawnParam"]["SplitNum"].AsInt));
            Assert.That(w.PaintRadiusMin, Is.EqualTo(p["PaintParam"]["WidthHalfFar"].AsDouble*S).Within(1e-6));
            Assert.That(w.ReferenceFootRadius, Is.EqualTo(p["SplashPaintParam"]["WidthHalfNearest"].AsDouble*S).Within(1e-6));
            Assert.That(w.ShotInk * (id == 6 ? 22 : 1), Is.EqualTo(p["WeaponParam"]["InkConsume"].AsDouble * 100).Within(1e-5));
            var metrics = new WeaponRangeMetrics(w);
            Assert.That(w.EffectiveRange, Is.EqualTo(metrics.MinimumDamage).Within(.0001));
            Assert.That(metrics.Grounded && metrics.Flat > 0, Is.True);
        }

        [Test] public void ValidationAcceptsEqualChargeDamageAndIndependentAirBiasButRejectsInvalidValues()
        {
            var mini = GameplayConfig.GetWeapon(6);
            Assert.That(mini.ChargePartialMaxDamage, Is.EqualTo(mini.Damage));
            Assert.DoesNotThrow(() => WeaponConfigValidation.Validate(mini));
            Assert.Throws<InvalidOperationException>(() => WeaponConfigValidation.Validate(WeaponAssetTests.Changed(6, a => a.chargePartialMaxDamage = 33)));
            Assert.Throws<InvalidOperationException>(() => WeaponConfigValidation.Validate(WeaponAssetTests.Changed(6, a => a.chargePartialMaxDamage = 0)));
            Assert.That(GameplayConfig.GetWeapon(8).ReferenceJumpBias, Is.LessThan(GameplayConfig.GetWeapon(8).ReferenceBiasMax));
            foreach (float bias in new[] { -.01f, 1f, float.NaN, float.PositiveInfinity })
                Assert.Throws<InvalidOperationException>(() => WeaponConfigValidation.Validate(WeaponAssetTests.Changed(8, a => a.referenceJumpBias = bias)));
            Assert.Throws<InvalidOperationException>(() => WeaponConfigValidation.Validate(WeaponAssetTests.Changed(8, a => a.referenceBiasMin = .6f)));
            // Mini's equal near/middle node has identical radii, so the collapsed interval is continuous.
            Assert.That(mini.ShooterPaintNearDistance, Is.EqualTo(mini.PaintDistanceMiddle));
            Assert.That(ShooterDetailSimulation.ImpactRadius(mini.PaintDistanceMiddle, mini), Is.EqualTo(mini.PaintRadiusMax));
            Assert.Throws<InvalidOperationException>(() => WeaponConfigValidation.Validate(WeaponAssetTests.Changed(6, a => a.shooterPaintNearRadius += .1f)));
        }

        [Test] public void MgGroundBiasAirTransitionAndStopRecoveryRemainIndependent()
        {
            var w = GameplayConfig.GetWeapon(8);
            var s = new PlayerSnapshot { HeroId=8, Health=100, Ink=100, Grounded=true, Team=1 };
            for (int f=0; f<=100; f++) WeaponSimulation.Step(ref s, new PlayerInputFrame {Fire=true, FireSequence=1, Sequence=(uint)f+1}, w, f/60.0, false, true);
            Assert.That(s.DualiesGroundBias, Is.EqualTo(.5f).Within(1e-6));
            s.Grounded=false;
            SpreadSimulation.Before(ref s, w, 101/60.0);
            Assert.That(ReferenceSpreadSimulation.Bias(s,w), Is.EqualTo(.4f).Within(1e-6));
            Assert.That(s.CurrentSpread, Is.EqualTo(15.54f));
            for (int f=102; f<240; f++) WeaponSimulation.Step(ref s, default, w, f/60.0, false, true);
            s.Grounded=true; SpreadSimulation.Before(ref s,w,4);
            Assert.That(s.DualiesGroundBias, Is.EqualTo(.06f).Within(1e-6));
            Assert.That(s.CurrentSpread, Is.EqualTo(12.63f));
        }

        [TestCase(6)] [TestCase(8)]
        public void ReplacementChangesSignedConfigAndFreezesInFlightParameters(int id)
        {
            var current=GameplayConfig.GetWeapon(id);
            var previous=WeaponAssetTests.Changed(id, a => { if(id==6) { a.chargeSeconds=2.5; a.damage=40; } else { a.damage=38; a.fireRate=12; } });
            byte[] Signature(WeaponRuntimeConfig w) { using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream); w.Write(writer); return stream.ToArray(); }
            Assert.That(Signature(current).SequenceEqual(Signature(previous)), Is.False);
            var shot=new InkShot {Configuration=current};
            var changed=WeaponAssetTests.Changed(id, a => a.damage+=1);
            WeaponConfigService.Current.Replace(id,changed);
            Assert.That(shot.Configuration.Damage, Is.EqualTo(id==6?32:24));
            Assert.That(GameplayConfig.GetWeapon(id).Damage, Is.EqualTo(id==6?33:25));
        }

        [Test] public void ReinstallingMgUsesTheSameApprovedProfileWithoutReplacingAssetReferences()
        {
            var source = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(8).WeaponConfigPath);
            var clone = UnityEngine.Object.Instantiate(source);
            try
            {
                clone.damage=38; clone.fireRate=12; clone.shotInk=.8f; clone.detailedPaint=false;
                var builder=Type.GetType("Splatoon.Editor.SplooshGirlBuilder, Splatoon.Editor", true);
                builder.GetMethod("ApplyApprovedWeaponParameters", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null,new object[]{clone});
                byte[] Serialized(WeaponRuntimeConfig w) { using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream); w.Write(writer); return stream.ToArray(); }
                Assert.That(Serialized(clone.Snapshot()), Is.EqualTo(Serialized(source.Snapshot())));
                Assert.That(clone.ammoConfig, Is.SameAs(source.ammoConfig));
                Assert.That(clone.weaponPrefabAddress, Is.EqualTo(source.weaponPrefabAddress));
            }
            finally { UnityEngine.Object.DestroyImmediate(clone); }
        }
    }
}
#endif
