#if UNITY_EDITOR
using System;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class FireRateConfigTests
    {
        [SetUp] public void Load() => HeroMigrationTests.Load();
        [TearDown] public void Reset() => LubanConfigService.Current.Reset();
        static PlayerSnapshot Player(int hero) => new() { HeroId = hero, Health = 100, Ink = 100, Grounded = true };
        static bool Fire(ref PlayerSnapshot s, double now, bool held = true, uint press = 1)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = held, FireSequence = press }, GameplayConfig.GetWeapon(s.HeroId), now, false, true);

        [TestCase(1, 15f)] [TestCase(1, 7f)] [TestCase(1, 7.5f)]
        [TestCase(2, 10f)] [TestCase(3, 2.5f)] [TestCase(4, 5f)]
        public void FireRateAloneControlsTheExactCooldownBoundary(int hero, float rate)
        {
            HeroMigrationTests.Load(rows => rows[hero - 1]["fireRate"] = rate);
            GameplayConfig.Validate();
            var w = GameplayConfig.GetWeapon(hero); var s = Player(hero);
            double startup = WeaponTimeFixture.ReferenceFrames(w.StartSeconds) / 60.0, interval = 1.0 / rate;
            Assert.That(Fire(ref s, 0), Is.False);
            Assert.That(Fire(ref s, startup), Is.True);
            Assert.That(s.NextShotAt, Is.EqualTo(startup + interval).Within(1e-10));
            Assert.That(Fire(ref s, startup + interval - .00001), Is.False);
            Assert.That(Fire(ref s, startup + interval), Is.True);
            Assert.That(s.ShotSequence, Is.EqualTo(2));
            Assert.That(WeaponDisplay.SustainedRate(w), Is.EqualTo(rate));
            Assert.That(s.InkRecoverAt, Is.EqualTo(startup + interval + WeaponTimeFixture.ReferenceFrames(w.InkRecoverLockSeconds) / 60.0).Within(1e-8));
        }

        [Test] public void BurstUsesConfiguredRateWithinTheGroupAndPreservesGroupRecovery()
        {
            HeroMigrationTests.Load(rows => { rows[0]["fireMode"] = 1; rows[0]["burstCount"] = 3; rows[0]["burstRecoveryFrames"] = 30; rows[0]["fireRate"] = 12; });
            GameplayConfig.Validate(); var s = Player(1); double first = WeaponTimeFixture.ReferenceFrames(GameplayConfig.GetWeapon(1).StartSeconds) / 60.0;
            Fire(ref s, 0);
            Assert.That(Fire(ref s, first), Is.True);
            Assert.That(Fire(ref s, first + 1.0 / 12), Is.True);
            Assert.That(Fire(ref s, first + 2.0 / 12), Is.True);
            double next = first + 2.0 / 12 + .5;
            Assert.That(s.NextShotAt, Is.EqualTo(next).Within(1e-9));
            Assert.That(Fire(ref s, next - .00001), Is.False);
            Assert.That(Fire(ref s, next), Is.True);
        }

        [TestCase(.1)] [TestCase(.5)] [TestCase(1.1)]
        public void ChargeReleaseUsesConfiguredRateAndDoesNotBlockSwimming(double release)
        {
            HeroMigrationTests.Load(rows => rows[4]["fireRate"] = 4);
            GameplayConfig.Validate(); var s = Player(5);
            Fire(ref s, 0); Assert.That(Fire(ref s, release, false), Is.True);
            Assert.That(s.NextShotAt, Is.EqualTo(release + .25).Within(1e-9));
            Assert.That(WeaponSimulation.WantsFire(s, new PlayerInputFrame { FireSequence = 1 }), Is.False);
            Assert.That(Fire(ref s, release + .1, true, 2), Is.False);
        }

        [Test] public void ZeroGravityKeepsBothBallisticPathsLevelAfterTheStraightPhase()
        {
            HeroMigrationTests.Load(rows => rows[0]["projectileGravity"] = 0);
            GameplayConfig.Validate(); var w = GameplayConfig.GetWeapon(1);
            var origin = new Vector3(0, 3, 0); var velocity = new Vector3(0, 0, 30);
            Assert.That(InkBallistics.Position(origin, velocity, w, 1).y, Is.EqualTo(3));
            var shot = new InkShot { Origin = origin, Velocity = velocity, FirstSegmentLength = 2, PostCorrectionVelocity = velocity, GravityStartAge = .2f };
            Assert.That(InkBallistics.Position(shot, w, 1).y, Is.EqualTo(3));
            Assert.That(InkBallistics.Velocity(shot, w, 1).y, Is.Zero);
        }

        [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidProjectileGravityStillFailsValidation(float gravity)
        {
            HeroMigrationTests.Load(rows => rows[0]["projectileGravity"] = gravity);
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate());
        }

        [TestCase(0f)] [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidFireRateStillFailsValidation(float rate)
        {
            HeroMigrationTests.Load(rows => rows[0]["fireRate"] = rate);
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate());
        }

        [Test] public void CurrentTablesValidateWithoutRedundantFields()
        {
            GameplayConfig.Validate();
            foreach (string field in new[] { "FireIntervalFrames", "PaintRange", "ChargeMinPaintRange" })
                Assert.That(typeof(cfg.HeroConfig).GetField(field), Is.Null);
            foreach (string field in new[] { "Width", "Length", "LayoutVersion" })
                Assert.That(typeof(cfg.MapConfig).GetField(field), Is.Null);
            Assert.That(typeof(cfg.RoomModeConfig).GetField("GroundOnlyScore"), Is.Null);
        }
    }
}
#endif
