#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    public static class DualPistolGirlTuningFixture
    {
        public const string AssetPath = "Assets/GameResource/Weapons/DualPistolGirl/DualPistolGirlWeaponConfig.asset";
        public const string RecordPath = "Tools/ValidationData/WeaponAssets/DualPistolGirl-GloogaNormal.json";
        public static void Apply(int hero, JSONNode expected, bool referenceFrames = false)
        {
            if (hero != 2) return;
            var values = JSONNode.Parse(File.ReadAllText(RecordPath))["values"];
            if (referenceFrames) WeaponTimeFixture.ToReferenceFrames(values);
            foreach (string key in values.Keys) expected[key] = values[key];
        }
    }

    public sealed class DualPistolGirlTests
    {
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static PlayerSnapshot Alive() => new() { HeroId = 2, Health = 100, Ink = 100, Grounded = true, Team = 1, Revision = 1 };
        static WeaponRuntimeConfig W => GameplayConfig.GetWeapon(2);
        static PlayerInputFrame Input(int tick, bool fire = true) => new() { Fire = fire, FireSequence = 1, Sequence = (uint)tick + 1 };
        static bool Tick(ref PlayerSnapshot s, int tick, bool fire = true) => WeaponSimulation.Step(ref s, Input(tick, fire), W, tick / 60.0, false, true);

        [Test] public void AuthoredAssetMatchesApprovedTuningAndRemainsValid()
        {
            var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(DualPistolGirlTuningFixture.AssetPath);
            var values = JSONNode.Parse(File.ReadAllText(DualPistolGirlTuningFixture.RecordPath))["values"];
            foreach (string key in values.Keys)
            {
                var field = typeof(WeaponConfigAsset).GetField(key);
                Assert.That(field, Is.Not.Null, key);
                Assert.That(Convert.ToDouble(field.GetValue(asset)), Is.EqualTo(values[key].AsDouble).Within(.00002), key);
            }
            WeaponConfigValidation.Validate(W);
            Assert.That(W.MuzzleMode, Is.EqualTo(WeaponMuzzleMode.AlternatingRightLeft));
            Assert.That(W.Ammo.ExplosionEnabled, Is.False);
            Assert.That(WeaponDisplay.Mechanism(W), Does.Contain("全自动 / 右左交替"));
        }

        [Test] public void FullTankEmits71AlternatingRoundsEveryNineTicks()
        {
            var s = Alive(); var ticks = new List<int>();
            for (int t = 0; t < 700; t++)
                if (Tick(ref s, t))
                {
                    Assert.That(s.LastShotMuzzle, Is.EqualTo(ticks.Count % 2)); ticks.Add(t);
                    Assert.That(s.InkRecoverAt, Is.EqualTo(t / 60.0 + 20.0 / 60).Within(1e-8));
                }
            Assert.That(ticks, Is.EqualTo(Enumerable.Range(0, 71).Select(i => i * 9)));
            Assert.That(s.Ink, Is.EqualTo(.6f).Within(.0002f));
            Assert.That(W.ShootMoveSpeed, Is.EqualTo(3.125f));
        }

        [TestCase("release")] [TestCase("empty")] [TestCase("death")] [TestCase("cancel")] [TestCase("submerged")]
        public void StoppingNeverEmitsOrAdvancesHand(string reason)
        {
            var s = Alive(); for (int t = 0; t <= 18; t++) Tick(ref s, t);
            var count = s.ShotSequence; var hand = s.NextMuzzle;
            if (reason == "empty") s.Ink = 1.39f;
            if (reason == "death") s.Health = 0;
            for (int t = 19; t < 100; t++)
            {
                var input = Input(t, reason != "release"); input.CancelFire = reason == "cancel";
                Assert.That(WeaponSimulation.Step(ref s, input, W, t / 60.0, false, reason != "submerged"), Is.False);
            }
            Assert.That(s.ShotSequence, Is.EqualTo(count)); Assert.That(s.NextMuzzle, Is.EqualTo(hand));
        }

        [Test] public void EmergingWaitsFourTicksThenStartsOnRight()
        {
            var s = Alive();
            for (int t = 0; t <= 4; t++)
                Assert.That(WeaponSimulation.Step(ref s, Input(t), W, t / 60.0, t == 0, true), Is.EqualTo(t == 4));
            Assert.That(s.LastShotMuzzle, Is.Zero);
        }

        [Test] public void GroundBiasGrowsPerEmittedRoundAndRecoversAfterCooldown()
        {
            var s = Alive(); int count = 0;
            for (int t = 0; t <= 90; t++)
                if (Tick(ref s, t))
                {
                    Assert.That(s.LastShotSpreadBias, Is.EqualTo(Mathf.Min(.25f, .03f + count * .03f)).Within(.000001));
                    Assert.That(s.LastShotSpread, Is.EqualTo(3.2f)); count++;
                }
            for (int t = 91; t <= 99; t++) Tick(ref s, t, false);
            Assert.That(s.DualiesGroundBias, Is.EqualTo(.25f));
            for (int t = 100; t <= 109; t++) Tick(ref s, t, false);
            Assert.That(s.DualiesGroundBias, Is.EqualTo(.20f).Within(.000001));
            for (int t = 110; t <= 144; t++) Tick(ref s, t, false);
            Assert.That(s.DualiesGroundBias, Is.EqualTo(.03f));
            Tick(ref s, 145); Assert.That(s.LastShotSpreadBias, Is.EqualTo(.03f));
        }

        [Test] public void JumpBiasRecoversByAirAgeAndLandingUsesGroundBias()
        {
            var s = Alive(); SpreadSimulation.Reset(ref s, W); SpreadSimulation.Before(ref s, W, 0);
            s.Grounded = false; SpreadSimulation.Before(ref s, W, 1.0 / 60);
            Assert.That(DualiesNormalSimulation.Bias(s, W), Is.EqualTo(.4f));
            SpreadSimulation.Before(ref s, W, 26.0 / 60);
            Assert.That(DualiesNormalSimulation.Bias(s, W), Is.EqualTo(.4f).Within(.000001));
            SpreadSimulation.Before(ref s, W, 71.0 / 60);
            Assert.That(DualiesNormalSimulation.Bias(s, W), Is.EqualTo(.03f).Within(.000001));
            s.Grounded = true; SpreadSimulation.Before(ref s, W, 72.0 / 60);
            Assert.That(s.CurrentSpread, Is.EqualTo(3.2f)); Assert.That(s.DualiesJumpAge, Is.Zero);
        }

        [Test] public void SeededFirstShotHasNonzeroDistributionAndContinuousFireIsLessAccurate()
        {
            var first = new List<float>(); var sustained = new List<float>(); uint a = 71, b = a;
            for (int i = 0; i < 8192; i++)
            {
                var va = DualiesNormalSimulation.LaunchVelocity(Vector3.forward, W, 3.2f, .03f, ref a);
                var vb = DualiesNormalSimulation.LaunchVelocity(Vector3.forward, W, 3.2f, .25f, ref b);
                first.Add(new Vector2(va.x, va.y).magnitude / va.z);
                sustained.Add(new Vector2(vb.x, vb.y).magnitude / vb.z);
            }
            first.Sort(); sustained.Sort(); float outer = Mathf.Tan(3.2f * Mathf.Deg2Rad);
            Assert.That(first[4096] / outer, Is.EqualTo(.03f).Within(.004f));
            Assert.That(sustained[4096] / outer, Is.EqualTo(.25f).Within(.015f));
            Assert.That(first.Last(), Is.LessThanOrEqualTo(outer)); Assert.That(sustained.Last(), Is.LessThanOrEqualTo(outer));
            Assert.That(a, Is.EqualTo(b));
        }

        [TestCase(3, 6.40262928f, 0f)] [TestCase(4, 7.75007197f, -.05241919f)]
        [TestCase(7, 9.51757184f, -.36700476f)] [TestCase(10, 10.57724389f, -.80243316f)]
        [TestCase(40, 21.17396441f, -11.08757370f)]
        public void ReferenceTrajectoryAgreesAtStageBoundaries(int frame, float z, float y)
        {
            var shot = new InkShot { Velocity = Vector3.forward * W.SpeedMin };
            var p = InkBallistics.Position(shot, W, frame / 60.0);
            Assert.That(p.z, Is.EqualTo(z).Within(.00002)); Assert.That(p.y, Is.EqualTo(y).Within(.00002));
            Assert.That(InkBallistics.AgeAtDistance(W, W.SpeedMin, z), Is.EqualTo(frame / 60.0).Within(.000002));
            Assert.That(InkBallistics.Position(Vector3.zero, shot.Velocity, W, frame / 60.0), Is.EqualTo(p));
            var before = InkBallistics.Position(shot, W, frame / 60.0 - 1e-7);
            var after = InkBallistics.Position(shot, W, frame / 60.0 + 1e-7);
            Assert.That(Vector3.Distance(before, after), Is.LessThan(.00005));
        }

        [Test] public void DamageUsesAgeFrom36To18()
        {
            Assert.That(WeaponSimulation.Damage(W, 0), Is.EqualTo(36));
            Assert.That(WeaponSimulation.Damage(W, 7.0 / 60), Is.EqualTo(36));
            Assert.That(WeaponSimulation.Damage(W, 23.5 / 60), Is.EqualTo(27).Within(.00001));
            Assert.That(WeaponSimulation.Damage(W, 40.0 / 60), Is.EqualTo(18));
            Assert.That(WeaponSimulation.Damage(W, 2), Is.EqualTo(18));
        }

        [Test] public void SnapshotRoundTripReplaysBiasJumpAndBothHands()
        {
            var a = Alive();
            for (int t = 0; t < 35; t++) { a.Grounded = t < 12; Tick(ref a, t); }
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(a);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot b);
            Assert.That(b.DualiesJumpAge, Is.EqualTo(a.DualiesJumpAge)); Assert.That(b.LastShotSpreadBias, Is.EqualTo(a.LastShotSpreadBias));
            for (int t = 35; t < 150; t++)
            {
                a.Grounded = b.Grounded = t >= 60;
                Assert.That(Tick(ref b, t, t < 90), Is.EqualTo(Tick(ref a, t, t < 90)));
                Assert.That(b.DualiesGroundBias, Is.EqualTo(a.DualiesGroundBias));
                Assert.That(b.DualiesJumpAge, Is.EqualTo(a.DualiesJumpAge));
                Assert.That(b.LastShotSpreadBias, Is.EqualTo(a.LastShotSpreadBias));
                Assert.That(b.NextMuzzle, Is.EqualTo(a.NextMuzzle)); Assert.That(b.ShotActionId, Is.EqualTo(a.ShotActionId));
            }
            Assert.That(b.LeftShotAction, Is.EqualTo(a.LeftShotAction)); Assert.That(b.RightShotAction, Is.EqualTo(a.RightShotAction));
        }

        [Test] public void HotReloadClampsBiasKeepsOldFlightAndChangesContentBytes()
        {
            var old = W; var s = Alive(); for (int t = 0; t <= 90; t++) Tick(ref s, t);
            uint revision = WeaponConfigService.Current.Revision(2);
            var next = WeaponAssetTests.Changed(2, a => { a.dualiesSpreadMaxBias = .2f; a.dualiesBrakeDrag = .5f; });
            Assert.That(old.RequiresRestart(next), Is.False); Assert.That(old.SameValues(next), Is.False);
            WeaponConfigService.Current.Replace(2, next); SpreadSimulation.Before(ref s, next, 91.0 / 60);
            Assert.That(s.DualiesGroundBias, Is.EqualTo(.2f)); Assert.That(s.NextMuzzle, Is.EqualTo(1));
            Assert.That(WeaponConfigService.Current.ForShot(2, revision), Is.SameAs(old));
            Assert.That(DualiesBallistics.Distance(old, old.SpeedMin, .2), Is.GreaterThan(DualiesBallistics.Distance(next, next.SpeedMin, .2)));
        }

        [Test] public void PaintHasTwoTrailDropsFootOnFirstAndSixthAndIdenticalDriverResults()
        {
            const string output = "Reports/DualPistolGirl/paint"; Directory.CreateDirectory(output);
            var one = WeaponReferenceMeasurements.Capture(2, 0, "flat", 60, 1, output);
            var five = WeaponReferenceMeasurements.Capture(2, 0, "flat", 60, 5, output);
            var six = WeaponReferenceMeasurements.Capture(2, 0, "flat", 60, 6, output);
            Assert.That(one.paintStamps, Is.EqualTo(4)); // Foot + two trails + impact.
            Assert.That(five.paintStamps, Is.EqualTo(16)); Assert.That(six.paintStamps, Is.EqualTo(20));
            var a = WeaponReferenceMeasurements.Capture(2, 0, "continuous", 30, 20, output);
            var b = WeaponReferenceMeasurements.Capture(2, 0, "continuous", 60, 20, output);
            var c = WeaponReferenceMeasurements.Capture(2, 0, "continuous", 144, 20, output);
            Assert.That(a.impacts, Is.EqualTo(20)); Assert.That(a.paintStamps, Is.EqualTo(64));
            Assert.That(b.gridHash, Is.EqualTo(a.gridHash)); Assert.That(c.gridHash, Is.EqualTo(a.gridHash));
        }
    }
}
#endif
