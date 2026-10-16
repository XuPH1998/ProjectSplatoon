using System;
using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Blocks
{
    /// <summary>One accepted resource payment, shared by pellets, drops and later entity pulses.</summary>
    public sealed class BlockAttackToken
    {
        public readonly PaintCredit Credit;
        public readonly int Generation, BuildLimit, AuxiliaryLimit, DirectLimit;
        public readonly double PointLimit;
        public readonly float DirectRadius;
        public readonly int BlocksPerPoint;
        public readonly int ProjectilesPerPayment;
        public int BuildRemaining, AuxiliaryRemaining, DirectRemaining;
        public int Added, RemovedEnemy;
        public double AwardedPoints;
        readonly HashSet<(uint entity, uint ordinal, byte kind)> _events = new();
        readonly Dictionary<uint, int> _projectileBuildRemaining = new();

        public BlockAttackToken(PaintCredit credit, int generation, int buildLimit, int directLimit, double pointLimit, float directRadius = .2f, int projectilesPerPayment = 1)
        {
            Credit = credit; Generation = generation;
            BuildRemaining = BuildLimit = Math.Max(0, buildLimit);
            AuxiliaryRemaining = AuxiliaryLimit = (int)Math.Floor(BuildLimit * (double)BlockGameplaySettings.AuxiliaryBudgetFraction);
            DirectRemaining = DirectLimit = Math.Max(0, directLimit);
            PointLimit = pointLimit;
            DirectRadius = directRadius;
            BlocksPerPoint = BlockGameplaySettings.BlocksPerSpecialPoint;
            ProjectilesPerPayment = Math.Max(1, projectilesPerPayment);
        }
        public int ReserveBuild(int requested, bool auxiliary, uint entityId = 0)
        {
            int count = Math.Min(Math.Max(0, requested), BuildRemaining);
            if (auxiliary) count = Math.Min(count, AuxiliaryRemaining);
            if (entityId != 0 && ProjectilesPerPayment > 1)
            {
                if (!_projectileBuildRemaining.TryGetValue(entityId, out int remaining))
                    remaining = (int)Math.Ceiling(BuildLimit / (double)ProjectilesPerPayment);
                count = Math.Min(count, remaining);
                _projectileBuildRemaining[entityId] = remaining - count;
            }
            BuildRemaining -= count; if (auxiliary) AuxiliaryRemaining -= count;
            return count;
        }
        public bool BeginEvent(uint entity, uint ordinal, byte kind) => entity == 0 || _events.Add((entity, ordinal, kind));
        public void CompleteBuild(int reserved, bool auxiliary, int actual, uint entityId = 0)
        {
            actual = Math.Clamp(actual, 0, reserved);
            BuildRemaining += reserved - actual;
            if (auxiliary) AuxiliaryRemaining += reserved - actual;
            if (entityId != 0 && _projectileBuildRemaining.TryGetValue(entityId, out int remaining))
                _projectileBuildRemaining[entityId] = remaining + reserved - actual;
            Added += actual;
        }
        public int ReserveDirect(int requested)
        {
            int count = Math.Min(Math.Max(0, requested), DirectRemaining);
            DirectRemaining -= count; return count;
        }
        public void CompleteDirect(int reserved, int actual)
        {
            actual = Math.Clamp(actual, 0, reserved);
            DirectRemaining += reserved - actual; RemovedEnemy += actual;
        }
        public double TakePoints()
        {
            double total = Math.Min(PointLimit, (Added + (double)RemovedEnemy) / BlocksPerPoint);
            double difference = Math.Max(0, total - AwardedPoints);
            AwardedPoints += difference; return difference;
        }
    }

    /// <summary>Server-only terrain accounting. Results pay credit only after collision publication.</summary>
    public static class BlockAttackService
    {
        // Live entities retain their token directly. The issuance index only needs
        // the latest payment per source, and rejects older replayed actions.
        static readonly Dictionary<(uint round, ulong owner, uint life, uint equipment, PaintAttackKind kind), (ulong action, BlockAttackToken token)> Tokens = new();
        static readonly Dictionary<(ulong owner, uint equipment), double> BuildFractions = new();
        static int _generation;
        public static bool Active => BlockTerrain.Current != null && PrototypeMatch.Current != null && PrototypeMatch.Current.IsServer && PrototypeMatch.Current.State.Value.Phase != MatchPhase.Finished;

        public static bool ExplosiveMain(WeaponRuntimeConfig weapon) => weapon != null &&
            (WeaponSimulation.IsBlaster(weapon) || WeaponSimulation.IsExplosher(weapon) || WeaponSimulation.IsFloatingBubble(weapon));

        public static BlockAttackToken Main(ulong owner, PlayerSnapshot state, uint round, WeaponRuntimeConfig weapon)
        {
            ulong action = WeaponSimulation.IsBubble(weapon) ? state.FireBurstSequence : state.ShotSequence;
            var key = (round, owner, state.Revision, state.HeroRevision, PaintAttackKind.Main);
            if (Existing(key, action, out var token)) return token;
            int build = !weapon.BlockBuild ? 0 : BuildBudget(owner, state.HeroRevision, WeaponSimulation.InkCost(weapon, state.LastShotCharge), BlockGameplaySettings.MainAttackMaxBlocks);
            int direct = weapon.BlockEnemyRemoval;
            token = new BlockAttackToken(new PaintCredit(owner, state.Team, round, state.HeroRevision, PaintAttackKind.Main, true), _generation, build,
                direct, BlockGameplaySettings.MainSpecialPointCap, weapon.BlockRemovalRadius, WeaponSimulation.IsBubble(weapon) ? weapon.BurstCount : weapon.PelletCount);
            Tokens[key] = (action, token); return token;
        }
        public static BlockAttackToken Sub(ulong owner, PlayerSnapshot state, uint round, SubWeaponRuntimeConfig config)
        {
            var key = (round, owner, state.Revision, state.HeroRevision, PaintAttackKind.Sub);
            if (Existing(key, state.SubAction, out var token)) return token;
            token = new BlockAttackToken(new PaintCredit(owner, state.Team, round, state.HeroRevision, PaintAttackKind.Sub, true), _generation,
                BuildBudget(owner, state.HeroRevision, config.Common.inkCost, int.MaxValue), 4096, BlockGameplaySettings.SubSpecialPointCap);
            Tokens[key] = (state.SubAction, token); return token;
        }
        public static BlockAttackToken Special(ulong owner, PlayerSnapshot state, uint round)
        {
            var key = (round, owner, state.Revision, state.HeroRevision, PaintAttackKind.Special);
            if (Existing(key, state.SpecialAction, out var token)) return token;
            token = new BlockAttackToken(new PaintCredit(owner, state.Team, round, state.HeroRevision, PaintAttackKind.Special, true), _generation, 4096, 8192, 0);
            Tokens[key] = (state.SpecialAction, token); return token;
        }
        static bool Existing((uint round, ulong owner, uint life, uint equipment, PaintAttackKind kind) source, ulong action, out BlockAttackToken token)
        {
            token = null;
            if (!Tokens.TryGetValue(source, out var previous) || action > previous.action) return false;
            if (action == previous.action) token = previous.token;
            return true;
        }
        static int BuildBudget(ulong owner, uint equipment, double spent, int maximum)
        {
            if (!double.IsFinite(spent) || spent <= 0) return 0;
            var key = (owner, equipment); BuildFractions.TryGetValue(key, out double fraction);
            double raw = spent * BlockGameplaySettings.BlocksPerMaterial + fraction;
            int whole = (int)Math.Min(int.MaxValue, Math.Floor(raw + 1e-8));
            BuildFractions[key] = Math.Max(0, raw - whole);
            return Math.Min(maximum, whole);
        }
        static bool Valid(BlockAttackToken token) => Active && token != null && token.Generation == _generation && token.Credit.Round == PrototypeMatch.Current.State.Value.Round;
        public static void Build(BlockAttackToken token, Vector3 point, Vector3 normal, float radius, bool auxiliary, bool anchorToMap, int perContact = int.MaxValue, uint entityId = 0, uint eventOrdinal = 0)
        {
            if (!Valid(token) || radius <= 0 || !token.BeginEvent(entityId, eventOrdinal, auxiliary ? (byte)1 : (byte)2)) return;
            // A trail pulse is a small part of the payment; it cannot eat all impact capacity.
            int reserved = token.ReserveBuild(Math.Min(perContact, auxiliary ? 8 : token.BuildLimit), auxiliary, entityId);
            if (reserved == 0) return;
            bool completed = false;
            BlockTerrain.Current.QueueBuild(point, normal, token.Credit.Team, reserved, radius, anchorToMap, result =>
            {
                if (completed || !Valid(token)) return;
                completed = true;
                token.CompleteBuild(reserved, auxiliary, result.Added, entityId); Award(token);
            }, ordinaryMainAttack: token.Credit.Kind == PaintAttackKind.Main && !auxiliary);
        }
        public static void Direct(BlockAttackToken token, Vector3 point, int perContact, uint entityId = 0, uint eventOrdinal = 0)
        {
            if (!Valid(token) || !token.BeginEvent(entityId, eventOrdinal, 3)) return;
            int reserved = token.ReserveDirect(perContact); if (reserved == 0) return;
            bool completed = false;
            BlockTerrain.Current.QueueDestroy(point, token.DirectRadius, 0, token.Credit.Team, true, result =>
            {
                if (completed || !Valid(token)) return;
                completed = true;
                token.CompleteDirect(reserved, result.RemovedEnemy); Award(token);
            }, reserved, 0, ordinaryMainAttack: token.Credit.Kind == PaintAttackKind.Main);
        }
        public static void Blast(BlockAttackToken token, Vector3 point, float radius, uint entityId = 0, uint eventOrdinal = 0)
        {
            if (!Valid(token) || radius <= 0 || !token.BeginEvent(entityId, eventOrdinal, 4)) return;
            // The terrain queue bounds work per tick. The radius is the gameplay limit;
            // a large blast is completed over several atomic publications, never truncated.
            bool completed = false;
            BlockTerrain.Current.QueueDestroy(point, radius, radius * BlockGameplaySettings.FriendlyBlastRadiusFraction, token.Credit.Team, false, result =>
            {
                if (completed || !Valid(token)) return;
                completed = true;
                token.RemovedEnemy += result.RemovedEnemy; Award(token);
            });
        }
        static void Award(BlockAttackToken token)
        {
            double points = token.TakePoints();
            if (points > 0) token.Credit.Apply(points, PrototypeMatch.Current.NetworkManager.ServerTime.Time);
        }
        public static bool Contact(TpsCollision hit, out BlockContact contact)
        {
            if (hit.BlockQuery.IsKnown) return hit.BlockQuery.TryResolveCurrent(out contact);
            return Contact(hit.Collider, hit.Point, hit.Normal, out contact);
        }
        public static bool Contact(Collider collider, Vector3 point, Vector3 normal, out BlockContact contact)
        {
            contact = default;
            return BlockTerrain.Current != null && BlockTerrain.Current.TryContact(collider, point, normal, out contact);
        }
        public static void Clear() { _generation++; Tokens.Clear(); BuildFractions.Clear(); }
    }
}
