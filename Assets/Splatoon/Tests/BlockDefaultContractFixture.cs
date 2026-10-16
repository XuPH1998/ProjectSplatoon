#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    /// <summary>Test-only default terrain publication and real resource-payment fixture.</summary>
    internal sealed class BlockDefaultContractFixture : IDisposable
    {
        public const float Dt = 1f / 60;
        public readonly PrototypeMatch Match;
        public readonly BlockTerrain Terrain;
        public double Now;
        readonly Action _serverTick;
        bool _disposed;

        public BlockDefaultContractFixture(PrototypeMatch match)
        {
            Match = match; Terrain = match.Blocks;
            Assert.That(Terrain, Is.Not.Null, "Default block terrain must be installed");
            Assert.That(Terrain.Initialized, Is.True);
            Assert.That(BlockAttackService.Active, Is.True, "Never disable block mode to recover a legacy paint contract");
            Now = match.NetworkManager.ServerTime.Time;
            _serverTick = (Action)Delegate.CreateDelegate(typeof(Action), match,
                typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic));
            if (match.NetworkManager.NetworkTickSystem != null) match.NetworkManager.NetworkTickSystem.Tick -= _serverTick;
        }
        public void Tick()
        {
            Now += Dt; Terrain.Tick(true, Now, Dt); Physics.SyncTransforms();
        }
        public IEnumerator Publish(Func<bool> ready = null, int maximumFrames = 1800)
        {
            for (int frame = 0; frame < maximumFrames && (Terrain.PendingTransactions > 0 || ready != null && !ready()); frame++)
            { Tick(); yield return null; }
            Assert.That(Terrain.PendingTransactions, Is.Zero, "Finish the full requested terrain result, not a partial publication");
            Assert.That(ready == null || ready(), Is.True, "Expected real terrain result was published");
            Assert.That(Terrain.FailedTransactions, Is.Zero);
            Assert.That(Match.State.Value.PinkBlockCount, Is.EqualTo(Terrain.PinkCount));
            Assert.That(Match.State.Value.BlueBlockCount, Is.EqualTo(Terrain.BlueCount));
            Assert.That(Match.State.Value.TerrainRevision, Is.EqualTo(Terrain.Revision));
        }
        public IEnumerator Seed(BlockWorld world)
        {
            Assert.That(Terrain.PendingTransactions, Is.Zero, "Fixture seeding cannot cancel an accepted attack");
            var points = Match.Players.Select(p => p.Snapshot.Value.SpecialPoints).ToArray();
            Assert.That(Terrain.QueueSeed(world), Is.True);
            yield return Publish();
            CollectionAssert.AreEqual(points, Match.Players.Select(p => p.Snapshot.Value.SpecialPoints).ToArray(),
                "Diagnostic structure placement must never award resource-payment income");
        }
        public BlockWorld Empty() => new(Terrain.World.Dimensions, Terrain.Round, Terrain.World.Bottom);
        public byte[] Capture() => Terrain.World.EncodeSnapshot();
        public void Unchanged(byte[] before, string label)
        {
            Assert.That(Terrain.PendingTransactions, Is.Zero, label + ": no deferred terrain mutation");
            CollectionAssert.AreEqual(before, Capture(), label + ": positions, ownership, counts and revision are unchanged");
        }
        public void FlushEvents() => _serverTick();

        public BlockAttackToken PayMain(PrototypePlayer owner, int hero)
        {
            var s = owner.Snapshot.Value; var weapon = GameplayConfig.GetWeapon(hero);
            s.HeroId = hero; s.Health = 100; s.LifeState = PlayerLifeState.Alive;
            s.Ink = 100; s.WeaponPhase = WeaponPhase.Idle; s.AttackNeedsRelease = false;
            s.BurstRemaining = 0; s.NextShotAt = s.BurstReadyAt = s.AttackRecoveryUntil = 0;
            s.SpecialPhase = SpecialPhase.Charging; s.SpecialChargeLockedUntil = 0;
            var input = new PlayerInputFrame { Sequence = s.ShotSequence + 1, HeroRevision = s.HeroRevision,
                Fire = true, FireSequence = s.ConsumedFire + 1, ReleaseSequence = s.ConsumedRelease };
            float before = s.Ink; bool emitted = false; WeaponFireResult fire = default;
            for (int frame = 0; frame < 240 && !emitted; frame++)
            { emitted = WeaponSimulation.Step(ref s, input, weapon, Now, false, true, out fire); if (!emitted) Now += Dt; }
            Assert.That(emitted, Is.True, "Loaded weapon input must really emit the paid attack");
            Assert.That(before - s.Ink, Is.EqualTo(WeaponSimulation.InkCost(weapon, fire.Charge)).Within(.001),
                "One accepted attack pays the original authored material cost");
            owner.Snapshot.Value = s;
            var token = BlockAttackService.Main(owner.PlayerId, s, Terrain.Round, weapon);
            Assert.That(token, Is.Not.Null);
            Assert.That(token.Credit.IsBlockPoints, Is.True);
            return token;
        }
        public InkShot PaidShot(PrototypePlayer owner, int hero, uint id, Vector3 origin, Vector3 velocity)
        {
            var token = PayMain(owner, hero); var s = owner.Snapshot.Value;
            return new InkShot { Id = id, Round = Terrain.Round, Shooter = owner.PlayerId, Team = s.Team,
                HeroId = hero, Lifecycle = s.Revision, HeroRevision = s.HeroRevision, Seed = id + 7,
                ShotSequence = s.ShotSequence, VolleyIndex = (byte)Math.Max(0, s.BurstShotIndex - 1),
                Born = Now, Origin = origin, Velocity = velocity, ActionId = s.ShotActionId,
                Configuration = GameplayConfig.GetWeapon(hero), ConfigurationRevision = WeaponConfigService.Current.Revision(hero), BlockAttack = token };
        }
        public float PaySub(PrototypePlayer owner, SubWeaponType type, float desiredCharge = 0)
        {
            var s = owner.Snapshot.Value; s.SubWeaponId = SubWeaponConfigService.Id(type);
            s.Health = 100; s.LifeState = PlayerLifeState.Alive; s.Ink = 100;
            s.SubPhase = SubWeaponPhase.Idle; s.SubNeedsRelease = false;
            s.SubReadyAt = s.SubRecoveryUntil = s.AttackRecoveryUntil = 0;
            var config = PlayerLoadout.SubWeapon(s); float before = s.Ink; uint action = s.SubAction;
            var input = new PlayerInputFrame { HeroRevision = s.HeroRevision, SubHeld = true,
                SubPressSequence = s.SubConsumedPress + 1, SubReleaseSequence = s.SubConsumedRelease };
            for (int frame = 0; frame < 600; frame++)
            {
                Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, Now, true,
                    Match.SubWeapons.CanUse(owner.PlayerId, s, config), out _), Is.False, "Holding does not pay or spawn");
                Now += Dt; if (config.Charge(s.SubChargeSeconds) + 1e-6 >= desiredCharge) break;
            }
            Assert.That(s.SubCharge + 1e-6, Is.GreaterThanOrEqualTo(desiredCharge));
            input.SubHeld = false; input.SubReleaseSequence++;
            bool paid = SubWeaponSimulation.Step(ref s, input, config, Dt, Now, true,
                Match.SubWeapons.CanUse(owner.PlayerId, s, config), out float charge);
            for (int frame = 0; frame < 240 && !paid; frame++)
            {
                Now += Dt;
                paid = SubWeaponSimulation.Step(ref s, input, config, Dt, Now, true,
                    Match.SubWeapons.CanUse(owner.PlayerId, s, config), out charge);
            }
            Assert.That(paid, Is.True, "Actual sub input reaches its authored paid commitment");
            Assert.That(s.SubAction, Is.EqualTo(action + 1));
            Assert.That(before - s.Ink, Is.EqualTo(config.Common.inkCost).Within(.001));
            owner.Snapshot.Value = s; return charge;
        }
        public static BlockFlightPresentation Flights(InkPresentation presentation)
        {
            var flight = (BlockFlightPresentation)typeof(InkPresentation).GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(presentation);
            Assert.That(flight, Is.Not.Null, "Actual instance renderer was created"); return flight;
        }
        public static Matrix4x4[] RenderedInstances(InkPresentation presentation, byte team, int count)
        {
            var flight = Flights(presentation);
            var matrices = (Matrix4x4[][])typeof(BlockFlightPresentation).GetField("_matrices", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flight);
            Assert.That(count, Is.InRange(1, BlockFlightPresentation.InstanceBatch));
            Assert.That(presentation.BlockCubeCount, Is.EqualTo(count), "This isolated render contains only the requested team's shots");
            // Flush resets the active write cursor, but retains the just-submitted transforms.
            return matrices[team - 1].Take(count).ToArray();
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (Match != null && Match.NetworkManager != null && Match.NetworkManager.NetworkTickSystem != null)
                Match.NetworkManager.NetworkTickSystem.Tick += _serverTick;
        }
    }
}
#endif
