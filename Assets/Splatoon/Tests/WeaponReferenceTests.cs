#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Splatoon.Networking;
using SimpleJSON;

namespace Splatoon.Tests
{
    public sealed class WeaponReferenceTests
    {
        static readonly string[] Names = { "", "WeaponShooterNormal", "WeaponShooterBlaze", "WeaponShooterGravity", "WeaponShooterTripleQuick", "WeaponChargerNormal" };
        [SetUp] public void Load() => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
            new cfg.Tables(n => JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));
        [TearDown] public void Reset() => LubanConfigService.Current.Reset();
        static JSONNode Reference(int id) => JSONNode.Parse(File.ReadAllText("Docs/WeaponAudit/" + Names[id] + ".1130.json"))["GameParameters"];
        static PlayerSnapshot Player(int id) => new() { WeaponId = id, Health = 100, Ink = 100, Team = 1 };

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ShooterInkAndFullTankCountMatchPinnedReference(int id)
        {
            float cost = Reference(id)["WeaponParam"]["InkConsume"].AsFloat * 100;
            var w = GameplayConfig.GetWeapon(id); var state = Player(id); int count = 0;
            Assert.That(WeaponSimulation.InkCost(w), Is.EqualTo(cost).Within(.00001f));
            for (int tick = 0; tick < 4000; tick++)
                if (WeaponSimulation.Step(ref state, new PlayerInputFrame { Fire = true, FireSequence = 1 }, w, tick / 60.0, false, true)) count++;
            Assert.That(count, Is.EqualTo((int)Math.Floor(100.0 / cost + 1e-5)));
            Assert.That(state.Ink, Is.InRange(0f, cost));
        }
        [TestCase(2)] [TestCase(4)]
        public void RecoveryLockUsesPinnedFramesAndAllowsRecoveryAtBoundary(int id)
        {
            var w = GameplayConfig.GetWeapon(id); int frames = Reference(id)["WeaponParam"]["InkRecoverStop"].AsInt;
            var s = Player(id); double fired = -1;
            for (int tick = 0; tick < 10; tick++)
                if (WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = true, FireSequence = 1 }, w, tick / 60.0, false, true)) { fired = tick / 60.0; break; }
            Assert.That(fired, Is.GreaterThanOrEqualTo(0));
            Assert.That(s.InkRecoverAt, Is.EqualTo(fired + frames / 60.0).Within(1e-8));
            WeaponSimulation.Cancel(ref s, default);
            float before = s.Ink;
            ResourceSimulation.Step(ref s, GameplayConfig.Character, false, false, 1f / 60, s.InkRecoverAt - 1.0 / 60);
            Assert.That(s.Ink, Is.EqualTo(before));
            ResourceSimulation.Step(ref s, GameplayConfig.Character, false, false, 1f / 60, s.InkRecoverAt);
            Assert.That(s.Ink, Is.GreaterThan(before));
        }
        [Test] public void HeavyShooterConsecutiveShotsAreNineReferenceFramesApart()
        {
            var w = GameplayConfig.GetWeapon(3); var s = Player(3); int previous = -1, count = 0;
            int interval = Reference(3)["WeaponParam"]["RepeatFrame"].AsInt;
            for (int tick = 0; tick < 180; tick++)
                if (WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = true, FireSequence = 1 }, w, tick / 60.0, false, true))
                { if (previous >= 0) Assert.That(tick - previous, Is.EqualTo(interval)); previous = tick; count++; }
            Assert.That(count, Is.EqualTo(20));
            Assert.That(WeaponDisplay.SustainedRate(w), Is.EqualTo(60f / interval).Within(.00001));
        }
        [Test] public void FullChargeDamageIsIndependentOfUnverifiedPartialCurve()
        {
            var w = GameplayConfig.GetWeapon(5);
            Assert.That(WeaponSimulation.Damage(w, .1, 1), Is.EqualTo(Reference(5)["DamageParam"]["ValueFullCharge"].AsFloat / 10));
            Assert.That(WeaponSimulation.Damage(w, .1, 0), Is.EqualTo(40));
            Assert.That(WeaponSimulation.Damage(w, .1, .5f), Is.EqualTo(60), "Partial curve remains the explicitly documented legacy curve.");
            Assert.That(WeaponSimulation.Damage(w, .1, 59f / 60), Is.LessThan(80));
            var s = Player(5);
            for (int t = 0; t < 70; t++) WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = true, FireSequence = 1 }, w, t / 60.0, false, true);
            WeaponSimulation.Cancel(ref s, default, true);
            Assert.That(WeaponSimulation.Step(ref s, default, w, 2, false, true), Is.False);
            Assert.That(s.ShotSequence, Is.Zero); Assert.That(s.Ink, Is.EqualTo(100));
        }
        [TestCase(false, 600)] [TestCase(true, 180)]
        public void ZeroAbilityFullInkRecoveryMatchesPinnedCommonData(bool swim, int frames)
        {
            var common = JSONNode.Parse(File.ReadAllText("Docs/WeaponAudit/Common.1130.json"));
            Assert.That(common[swim ? "InkRecoverFrm_Stealth" : "InkRecoverFrm_Std"][2].AsInt, Is.EqualTo(frames));
            var s = Player(1); s.Ink = 0; s.Swimming = swim;
            for (int tick = 1; tick < frames; tick++) ResourceSimulation.Step(ref s, GameplayConfig.Character, false, false, 1f / 60, tick / 60.0);
            Assert.That(s.Ink, Is.LessThan(100));
            ResourceSimulation.Step(ref s, GameplayConfig.Character, false, false, 1f / 60, frames / 60.0);
            Assert.That(s.Ink, Is.EqualTo(100).Within(.001));
        }
        [Test] public void GatedPhysicsAndPaintParametersRetainBaseline()
        {
            foreach (var w in LubanConfigService.Current.Tables.TbWeapon.DataList)
            {
                Assert.That(w.Gravity, Is.EqualTo(9.8f)); Assert.That(w.StraightFrames, Is.EqualTo(4));
                Assert.That(w.BrakeFrames, Is.EqualTo(8)); Assert.That(w.BrakeSpeedMultiplier, Is.EqualTo(.66f));
                Assert.That(w.PaintRadiusMin, Is.EqualTo(.65f)); Assert.That(w.PaintRadiusMax, Is.EqualTo(.8f));
                Assert.That(w.PaintHardness, Is.EqualTo(.55f)); Assert.That(w.PaintStrength, Is.EqualTo(1));
                Assert.That(w.TrailSpacing, Is.EqualTo(.7f)); Assert.That(w.TrailRadius, Is.EqualTo(.58f)); Assert.That(w.TrailMaxDrop, Is.EqualTo(2.4f));
            }
            Assert.That(GameplayConfig.Global.PaintThreshold, Is.EqualTo(.5f));
        }
    }
}
#endif
