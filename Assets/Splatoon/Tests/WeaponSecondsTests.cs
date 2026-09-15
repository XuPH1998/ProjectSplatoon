#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class WeaponSecondsTests
    {
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static PlayerSnapshot Alive(int hero = 6) => new() { HeroId = hero, Health = 100, Ink = 100, Grounded = true, Team = 1 };
        static bool Tick(ref PlayerSnapshot s, WeaponRuntimeConfig w, double seconds, bool fire = true)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = fire, FireSequence = 1, Sequence = 1 }, w, seconds, false, true);
        static FieldInfo[] Fields => typeof(WeaponConfigAsset).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        [Test] public void SixAuthoredAssetsKeepEveryPreMigrationValueAndReference()
        {
            var audit = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponSeconds/Migration.json"));
            Assert.That(audit["assets"].Count, Is.EqualTo(6));
            Assert.That(Fields.Length, Is.EqualTo(64));
            Assert.That(Fields.Count(f => f.FieldType == typeof(double)), Is.EqualTo(16));
            foreach (var entry in audit["assets"].Children)
            {
                string path = entry["path"].Value;
                var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(path);
                Assert.That(asset, Is.Not.Null);
                foreach (var field in Fields)
                {
                    var expected = entry["after"][field.Name];
                    object value = field.GetValue(asset);
                    if (field.FieldType == typeof(double)) Assert.That(value, Is.EqualTo(expected.AsDouble), path + "/" + field.Name);
                    else if (field.FieldType == typeof(float)) Assert.That(value, Is.EqualTo(expected.AsFloat), path + "/" + field.Name);
                    else if (field.FieldType == typeof(int) || field.FieldType.IsEnum) Assert.That(Convert.ToInt32(value), Is.EqualTo(expected.AsInt));
                    else Assert.That(value, Is.EqualTo(expected.Value));
                }
                using var hash = System.Security.Cryptography.SHA256.Create();
                string actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path + ".meta"))).Replace("-", "").ToLowerInvariant();
                Assert.That(actual, Is.EqualTo(entry["metaSha256"].Value), "Asset GUID and metadata preserved");
                Assert.That(File.ReadAllText(path), Does.Not.Contain("Frames:"));
            }
        }

        [Test] public void EveryFieldAndEveryModeHaveChineseInspectorNames()
        {
            foreach (var field in Fields)
            {
                string name = WeaponConfigLabels.Name(field.Name);
                Assert.That(name, Is.Not.EqualTo("武器参数"), field.Name);
                Assert.That(name.Any(c => c >= '\u4e00' && c <= '\u9fff'), Is.True, field.Name);
                Assert.That(field.GetCustomAttribute<TooltipAttribute>(), Is.Not.Null);
                Assert.That(name, Does.Not.Contain("帧"));
            }
            Assert.That(typeof(WeaponRuntimeConfig).GetField("FireMode").FieldType, Is.EqualTo(typeof(WeaponFireMode)));
            Assert.That(typeof(WeaponRuntimeConfig).GetField("MuzzleMode").FieldType, Is.EqualTo(typeof(WeaponMuzzleMode)));
            foreach (var type in new[] { typeof(WeaponFireMode), typeof(WeaponMuzzleMode) })
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                    Assert.That(field.GetCustomAttribute<InspectorNameAttribute>(), Is.Not.Null, field.Name);
            Assert.That(Enum.GetValues(typeof(WeaponFireMode)).Cast<object>().Select(Convert.ToInt32), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            Assert.That(Enum.GetValues(typeof(WeaponMuzzleMode)).Cast<object>().Select(Convert.ToInt32), Is.EqualTo(new[] { 0, 1 }));
        }

        [TestCase(-1d)] [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)] [TestCase(double.NegativeInfinity)]
        public void EveryInvalidSecondFieldKeepsLastValidConfigAndReportsChinese(double value)
        {
            var old = GameplayConfig.GetWeapon(6); uint revision = WeaponConfigService.Current.Revision(6);
            foreach (var field in Fields.Where(f => f.FieldType == typeof(double)))
            {
                var invalid = WeaponAssetTests.Changed(6, a => field.SetValue(a, value));
                var exception = Assert.Throws<InvalidOperationException>(() => WeaponConfigService.Current.Replace(6, invalid));
                Assert.That(exception.Message, Does.Contain(WeaponConfigLabels.Name(field.Name)));
                Assert.That(GameplayConfig.GetWeapon(6), Is.SameAs(old));
                Assert.That(WeaponConfigService.Current.Revision(6), Is.EqualTo(revision));
            }
        }

        [TestCase(-1)] [TestCase(99)]
        public void UnknownEnumValuesCannotReplaceValidConfig(int value)
        {
            var old = GameplayConfig.GetWeapon(6);
            Assert.Throws<InvalidOperationException>(() => WeaponConfigService.Current.Replace(6, WeaponAssetTests.Changed(6, a => a.fireMode = (WeaponFireMode)value)));
            Assert.Throws<InvalidOperationException>(() => WeaponConfigService.Current.Replace(6, WeaponAssetTests.Changed(6, a => a.muzzleMode = (WeaponMuzzleMode)value)));
            Assert.That(GameplayConfig.GetWeapon(6), Is.SameAs(old));
        }

        [Test] public void FractionalStartupAndRecoveryUseTheAuthoredSecondsExactly()
        {
            var w = WeaponAssetTests.Changed(1, a => { a.startSeconds = .123456789; a.inkRecoverLockSeconds = .234567891; });
            var s = Alive(1);
            Assert.That(Tick(ref s, w, 0), Is.False);
            Assert.That(Tick(ref s, w, w.StartSeconds - .000001), Is.False);
            Assert.That(Tick(ref s, w, w.StartSeconds), Is.True);
            Assert.That(s.InkRecoverAt, Is.EqualTo(w.StartSeconds + w.InkRecoverLockSeconds));
        }

        [Test] public void RocketKeepsIntermediateQuantizationAndReachesFractionalEndpoint()
        {
            var w = WeaponAssetTests.Changed(5, a => { a.startSeconds = 0; a.chargeSeconds = .2375; });
            var s = Alive(5);
            Tick(ref s, w, 0); Tick(ref s, w, .109);
            Assert.That(s.ChargeElapsedSeconds, Is.EqualTo(.1));
            Tick(ref s, w, .23749);
            Assert.That(WeaponSimulation.ChargeRatio(s, w), Is.LessThan(1));
            Assert.That(Tick(ref s, w, .2375, false), Is.True);
            Assert.That(s.LastShotCharge, Is.EqualTo(1));
            Assert.That(s.Ink, Is.EqualTo(100 - w.ShotInk));
            Assert.That(WeaponSimulation.AffordableChargeSeconds(w, w.ShotInk - .001f), Is.LessThan(w.ChargeSeconds));
            Assert.That(WeaponSimulation.AffordableChargeSeconds(w, w.ShotInk), Is.EqualTo(w.ChargeSeconds));
        }

        [Test] public void FractionalSplatlingEndpointsKeepInclusiveMagazineAndRefundBalance()
        {
            var w = WeaponAssetTests.Changed(6, a =>
            {
                a.splatlingMinChargeSeconds = .117; a.splatlingFirstChargeSeconds = .371; a.chargeSeconds = .643;
                a.splatlingFirstShootSeconds = .812; a.splatlingFullShootSeconds = 1.217;
            });
            WeaponConfigValidation.Validate(w);
            var s = Alive(); Tick(ref s, w, 0); Tick(ref s, w, .11699);
            Assert.That(s.SplatlingLoaded, Is.Zero);
            Tick(ref s, w, .117); Assert.That(s.SplatlingLoaded, Is.EqualTo(1));
            Tick(ref s, w, .371); Assert.That(s.SplatlingChargeSeconds, Is.EqualTo(.371)); Assert.That(s.SplatlingLoaded, Is.EqualTo(13));
            Tick(ref s, w, .643); Assert.That(s.SplatlingChargeSeconds, Is.EqualTo(.643)); Assert.That(s.SplatlingLoaded, Is.EqualTo(19));
            Assert.That(Tick(ref s, w, .643, false), Is.True); Assert.That(s.LastShotCharge, Is.EqualTo(1));
            Assert.That(s.SplatlingRemaining, Is.EqualTo(18));
            WeaponSimulation.Cancel(ref s, default, true); WeaponSimulation.Cancel(ref s, default, true);
            Assert.That(s.Ink, Is.EqualTo(100 - w.ShotInk).Within(.0001));
        }

        [Test] public void DoubleChargeSecondsSurviveWireRoundTripAndReplay()
        {
            var w = WeaponAssetTests.Changed(6, a => a.chargeSeconds = 2.537);
            var a = Alive(); Tick(ref a, w, 0); Tick(ref a, w, .713456789);
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(a);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot b);
            Assert.That(b.SplatlingChargeSeconds, Is.EqualTo(a.SplatlingChargeSeconds));
            Assert.That(b.ChargeElapsedSeconds, Is.EqualTo(a.ChargeElapsedSeconds));
            for (int step = 0; step <= 420; step++)
            {
                double time = .72 + step / 60.0; bool held = time < w.ChargeSeconds;
                Assert.That(Tick(ref a, w, time, held), Is.EqualTo(Tick(ref b, w, time, held)));
                Assert.That(b.SplatlingChargeSeconds, Is.EqualTo(a.SplatlingChargeSeconds));
                Assert.That(b.SplatlingRemaining, Is.EqualTo(a.SplatlingRemaining));
                Assert.That(b.Ink, Is.EqualTo(a.Ink));
            }
            Assert.That(a.ShotSequence, Is.EqualTo(66));
        }

        [Test] public void NewBallisticTimesAffectNewShotsAndDoNotRestartTheMagazine()
        {
            var old = GameplayConfig.GetWeapon(6); uint revision = WeaponConfigService.Current.Revision(6);
            var next = WeaponAssetTests.Changed(6, a => { a.straightSeconds = .317; a.brakeSeconds = .229; a.damageReduceStartSeconds = .117; a.damageReduceEndSeconds = .273; });
            Assert.That(old.RequiresRestart(next), Is.False);
            WeaponConfigService.Current.Replace(6, next);
            Assert.That(WeaponConfigService.Current.ForShot(6, revision), Is.SameAs(old));
            var shot = new InkShot { HeroId = 6, Configuration = old, Velocity = Vector3.forward * 100 };
            Assert.That(InkBallistics.Position(shot, shot.Configuration, .2).y, Is.LessThan(0));
            Assert.That(InkBallistics.Position(shot, next, .2).y, Is.Zero);
            Assert.That(WeaponSimulation.Damage(next, .195), Is.EqualTo((next.Damage + next.DamageMin) / 2).Within(.0001));
        }
    }
}
#endif
