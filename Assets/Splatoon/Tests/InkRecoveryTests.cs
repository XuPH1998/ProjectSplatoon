#if UNITY_EDITOR
using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class InkRecoveryTests
    {
        const float Dt = 1f / 60;
        [SetUp] public void Load() => HeroMigrationTests.Load();
        [TearDown] public void Reset() => LubanConfigService.Current.Reset();

        static PlayerSnapshot FireRifle(out int releaseTick)
        {
            var w = GameplayConfig.GetHero(1);
            var s = new PlayerSnapshot { HeroId = 1, Team = 1, Health = w.MaxHealth, Ink = 20, Grounded = true };
            releaseTick = w.StartFrames + (int)Math.Ceiling(3 * WeaponSimulation.FireInterval(w) * 60) + 1;
            for (int tick = 0; tick < releaseTick; tick++)
                WeaponSimulation.Step(ref s, new PlayerInputFrame { Sequence = (uint)tick + 1, Fire = true, FireSequence = 1 }, w, tick / 60.0, false, true);
            Assert.That(s.ShotSequence, Is.GreaterThan(0));
            Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Firing));
            return s;
        }

        static void StopAndRecover(ref PlayerSnapshot s, int tick, bool swim, SwimSurface source = SwimSurface.Friendly,
            bool grounded = true, bool clearance = true)
        {
            var w = GameplayConfig.GetHero(s.HeroId);
            var input = new PlayerInputFrame { Sequence = (uint)tick + 1, FireSequence = 1, ReleaseSequence = 1, Swim = swim };
            // Match production order, with explicit contact fixtures instead of Unity physics.
            bool wasSwimming = s.Swimming;
            s.Swimming = swim && !WeaponSimulation.WantsFire(s, input, w, tick / 60.0);
            s.SwimSource = s.Swimming ? source : SwimSurface.None;
            s.Grounded = grounded;
            s.FriendlyInkContact = s.Swimming && grounded && source == SwimSurface.Friendly;
            s.Movement = !grounded ? MovementMode.Air : s.Swimming ? MovementMode.GroundInk : MovementMode.Human;
            WeaponSimulation.Step(ref s, input, w, tick / 60.0, wasSwimming, !s.Swimming && clearance);
            ResourceSimulation.Step(ref s, w, false, false, Dt, tick / 60.0);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void ImmediateSwimFinishesReleaseAndRecoversAtTheConfiguredRate(int swimDelay)
        {
            var s = FireRifle(out int release); var fired = s; var w = GameplayConfig.GetHero(1);
            int recovered = 0;
            for (int tick = release; tick < release + 360; tick++)
            {
                float before = s.Ink;
                StopAndRecover(ref s, tick, tick >= release + swimDelay);
                Assert.That(s.WeaponPhase, Is.EqualTo(tick == release ? WeaponPhase.Ending : WeaponPhase.Idle));
                bool eligible = tick / 60.0 + 1e-8 >= fired.InkRecoverAt && tick > release;
                float rate = s.Swimming ? w.SwimRecoverInk : w.RecoverInk;
                float expected = eligible ? Math.Min(w.MaxInk, before + rate * Dt) : before;
                Assert.That(s.Ink, Is.EqualTo(expected).Within(.0001f), "tick " + tick);
                if (eligible && before < w.MaxInk) recovered++;
                Assert.That(s.ShotSequence, Is.EqualTo(fired.ShotSequence));
                Assert.That(s.NextShotAt, Is.EqualTo(fired.NextShotAt));
                Assert.That(s.InkRecoverAt, Is.EqualTo(fired.InkRecoverAt));
            }
            Assert.That(recovered, Is.GreaterThan(0)); Assert.That(s.Ink, Is.EqualTo(w.MaxInk));
        }

        [Test] public void NoStandingClearanceStillAllowsStopAndNormalRecovery()
        {
            var s = FireRifle(out int release); var fired = s; var w = GameplayConfig.GetHero(1);
            for (int tick = release; tick < release + 120; tick++)
            {
                float before = s.Ink;
                StopAndRecover(ref s, tick, false, clearance: false);
                bool eligible = tick > release && tick / 60.0 + 1e-8 >= fired.InkRecoverAt;
                Assert.That(s.Ink, Is.EqualTo(before + (eligible ? w.RecoverInk * Dt : 0)).Within(.0001f));
            }
            Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Idle));
            Assert.That(s.ShotSequence, Is.EqualTo(fired.ShotSequence));
            Assert.That(s.NextShotAt, Is.EqualTo(fired.NextShotAt));
        }

        [TestCase(SwimSurface.Neutral, true)] [TestCase(SwimSurface.Friendly, false)]
        public void NeutralGroundAndAirOnlyGetNormalRecovery(SwimSurface source, bool grounded)
        {
            var s = FireRifle(out int release); var w = GameplayConfig.GetHero(1);
            for (int tick = release; tick < release + 120; tick++)
            {
                float before = s.Ink;
                StopAndRecover(ref s, tick, true, source, grounded);
                Assert.That(s.HasInkRecovery, Is.False);
                bool eligible = tick > release && tick / 60.0 + 1e-8 >= s.InkRecoverAt;
                Assert.That(s.Ink, Is.EqualTo(before + (eligible ? w.RecoverInk * Dt : 0)).Within(.0001f));
            }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void SnapshotReplayAgreesAcrossImmediateSwim(int swimDelay)
        {
            var authority = FireRifle(out int release);
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(authority);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot replay);
            for (int tick = release; tick < release + 120; tick++)
            {
                StopAndRecover(ref authority, tick, tick >= release + swimDelay);
                StopAndRecover(ref replay, tick, tick >= release + swimDelay);
                Assert.That(replay.WeaponPhase, Is.EqualTo(authority.WeaponPhase));
                Assert.That(replay.Ink, Is.EqualTo(authority.Ink));
                Assert.That(replay.ShotSequence, Is.EqualTo(authority.ShotSequence));
                Assert.That(replay.NextShotAt, Is.EqualTo(authority.NextShotAt));
                Assert.That(replay.InkRecoverAt, Is.EqualTo(authority.InkRecoverAt));
            }
            Assert.That(authority.WeaponPhase, Is.EqualTo(WeaponPhase.Idle));
        }

        [Test] public void PendingAutomaticTapWaitsForClearanceAndFiresOnce()
        {
            var w = GameplayConfig.GetHero(1);
            var s = new PlayerSnapshot { HeroId = 1, Health = 100, Ink = 100 };
            var press = new PlayerInputFrame { Fire = true, FireSequence = 1 };
            WeaponSimulation.Step(ref s, press, w, 0, false, true);
            var release = new PlayerInputFrame { FireSequence = 1, ReleaseSequence = 1 };
            for (int tick = 1; tick <= w.StartFrames + 5; tick++)
            {
                Assert.That(WeaponSimulation.Step(ref s, release, w, tick / 60.0, false, false), Is.False);
                Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Starting));
            }
            double now = (w.StartFrames + 6) / 60.0;
            Assert.That(WeaponSimulation.Step(ref s, release, w, now, false, true), Is.True);
            for (int tick = 1; tick <= 120; tick++)
                Assert.That(WeaponSimulation.Step(ref s, release, w, now + tick / 60.0, false, false), Is.False);
            Assert.That(s.ShotSequence, Is.EqualTo(1)); Assert.That(s.WeaponPhase, Is.EqualTo(WeaponPhase.Idle));
            Assert.That(s.Ink, Is.EqualTo(100 - w.ShotInk).Within(.0001f));
        }
    }
}
#endif
