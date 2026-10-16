#if UNITY_EDITOR
using System;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class BlockAttackTests
    {
        [SetUp] public void Setup() { HeroMigrationTests.Load(); BlockAttackService.Clear(); }
        [TearDown] public void Cleanup() { BlockAttackService.Clear(); LubanConfigService.Current.Reset(); }
        static PlayerSnapshot Player(int hero = 1) => new() { HeroId = hero, Team = 1, Revision = 1, HeroRevision = 1, ShotSequence = 1, FireBurstSequence = 1 };
        static BlockAttackToken Token(int build = 32, int removal = 8, double maximumPoints = 2)
            => new(new PaintCredit(0, 1, 1, 1, PaintAttackKind.Main, true), 0, build, removal, maximumPoints);

        [Test] public void QueuedAuxiliaryAndImpactReserveTheSamePaymentBeforePublication()
        {
            var token = Token();
            int auxiliary = token.ReserveBuild(100, true);
            int impact = token.ReserveBuild(100, false);
            Assert.That(auxiliary, Is.EqualTo(8));
            Assert.That(impact, Is.EqualTo(24));
            Assert.That(token.ReserveBuild(100, true), Is.Zero);
            Assert.That(token.ReserveBuild(100, false), Is.Zero);
            Assert.That(token.TakePoints(), Is.Zero, "Queued cells have not been published");
            token.CompleteBuild(auxiliary, true, 6);
            Assert.That(token.AuxiliaryRemaining, Is.EqualTo(2));
            Assert.That(token.BuildRemaining, Is.EqualTo(2), "Only unbuilt cells return to the shared balance");
            token.CompleteBuild(impact, false, 24);
            Assert.That(token.Added, Is.EqualTo(30));
            Assert.That(token.TakePoints(), Is.EqualTo(30d / 32));
        }
        [Test] public void CreationAndEnemyRemovalShareOneChargeCapAndNeverPayTwice()
        {
            var token = Token(128, 128);
            int build = token.ReserveBuild(32, false); token.CompleteBuild(build, false, 32);
            Assert.That(token.TakePoints(), Is.EqualTo(1));
            int direct = token.ReserveDirect(32); token.CompleteDirect(direct, 32);
            Assert.That(token.TakePoints(), Is.EqualTo(1));
            token.RemovedEnemy += 5000; // A legal full-radius blast is not truncated to the charge cap.
            Assert.That(token.TakePoints(), Is.Zero);
            Assert.That(token.TakePoints(), Is.Zero);
            Assert.That(token.AwardedPoints, Is.EqualTo(2));
        }
        [Test] public void DeploymentCapCoversAllDelayedPulsesAndSpecialsCannotSelfCharge()
        {
            var deployment = Token(2240, 4096, 10);
            deployment.Added = 2240; deployment.RemovedEnemy = 4096;
            Assert.That(deployment.TakePoints(), Is.EqualTo(10));
            Assert.That(deployment.TakePoints(), Is.Zero);
            var special = Token(4096, 8192, 0); special.Added = 4096; special.RemovedEnemy = 100000;
            Assert.That(special.TakePoints(), Is.Zero);
        }
        [Test] public void ReplayedEventIsRejectedButDistinctPelletsAndPhasesRemainIndependent()
        {
            var token = Token();
            Assert.That(token.BeginEvent(42, 1, 2), Is.True);
            Assert.That(token.BeginEvent(42, 1, 2), Is.False);
            Assert.That(token.BeginEvent(43, 1, 2), Is.True);
            Assert.That(token.BeginEvent(42, 1, 4), Is.True);
        }
        [Test] public void HeldAutomaticFireUsesNewPaidTokenForEveryEmission()
        {
            var player = Player(); var weapon = GameplayConfig.GetWeapon(1);
            var first = BlockAttackService.Main(0, player, 1, weapon);
            player.ShotSequence++;
            var second = BlockAttackService.Main(0, player, 1, weapon);
            Assert.That(second, Is.Not.SameAs(first));
            player.ShotSequence--;
            Assert.That(BlockAttackService.Main(0, player, 1, weapon), Is.Null, "An older replay cannot issue a second terrain payment");
            // Existing projectiles and queued callbacks still own their old token.
            int reserved = first.ReserveBuild(8, false); first.CompleteBuild(reserved, false, reserved);
            Assert.That(first.Added, Is.EqualTo(reserved));
        }
        [Test] public void AllFourPrepaidBubbleFlightsShareOneToken()
        {
            var player = Player(7); var weapon = GameplayConfig.GetWeapon(7);
            var first = BlockAttackService.Main(0, player, 1, weapon);
            first.ReserveBuild(8, true);
            for (uint emitted = 2; emitted <= 4; emitted++)
            {
                player.ShotSequence = emitted;
                Assert.That(BlockAttackService.Main(0, player, 1, weapon), Is.SameAs(first));
            }
            player.FireBurstSequence = player.ShotSequence = 5;
            Assert.That(BlockAttackService.Main(0, player, 1, weapon), Is.Not.SameAs(first));
        }
        [Test] public void FirstBubbleCannotSpendTheOtherThreeFlightsConstructionShares()
        {
            var token = new BlockAttackToken(new PaintCredit(0, 1, 1, 1, PaintAttackKind.Main, true), 0, 256, 32, 2, .2f, 4);
            int firstTrail = token.ReserveBuild(8, true, 1);
            int firstImpact = token.ReserveBuild(256, false, 1);
            Assert.That(firstTrail + firstImpact, Is.EqualTo(64));
            Assert.That(token.ReserveBuild(256, false, 1), Is.Zero, "A later bounce keeps the same emitted flight share");
            token.CompleteBuild(firstImpact, false, 48, 1);
            Assert.That(token.ReserveBuild(256, false, 1), Is.EqualTo(8), "Only that flight's unpublished cells are refunded");
            Assert.That(token.ReserveBuild(256, false, 2), Is.EqualTo(64));
            Assert.That(token.ReserveBuild(256, false, 3), Is.EqualTo(64));
            Assert.That(token.ReserveBuild(256, false, 4), Is.EqualTo(64));
            Assert.That(token.BuildRemaining, Is.Zero);
        }
        [Test] public void SplatlingEmittedRoundsKeepFractionalResourceConversion()
        {
            var player = Player(6); var weapon = GameplayConfig.GetWeapon(6); int total = 0;
            for (uint emitted = 1; emitted <= 100; emitted++)
            {
                player.ShotSequence = emitted;
                total += BlockAttackService.Main(0, player, 1, weapon).BuildLimit;
            }
            Assert.That(total, Is.EqualTo((int)Math.Floor(100d * weapon.ShotInk * 32 + 1e-8)));
        }
        [TestCase(3)] [TestCase(5)] [TestCase(9)]
        public void ExplosiveMainsNeverReceiveConstructionOrDirectRemovalBudget(int hero)
        {
            var player = Player(hero); var weapon = GameplayConfig.GetWeapon(hero);
            var token = BlockAttackService.Main(0, player, 1, weapon);
            Assert.That(BlockAttackService.ExplosiveMain(weapon), Is.True);
            Assert.That(token.BuildLimit, Is.Zero);
            Assert.That(token.DirectLimit, Is.Zero);
        }
        [Test] public void EquipmentLifeOwnerAndRoundCannotShareAPaidToken()
        {
            var player = Player(); var weapon = GameplayConfig.GetWeapon(1);
            var first = BlockAttackService.Main(0, player, 1, weapon);
            Assert.That(BlockAttackService.Main(1, player, 1, weapon), Is.Not.SameAs(first));
            Assert.That(BlockAttackService.Main(0, player, 2, weapon), Is.Not.SameAs(first));
            player.Revision++;
            Assert.That(BlockAttackService.Main(0, player, 1, weapon), Is.Not.SameAs(first));
            player.Revision--; player.HeroRevision++;
            Assert.That(BlockAttackService.Main(0, player, 1, weapon), Is.Not.SameAs(first));
        }
    }
}
#endif
