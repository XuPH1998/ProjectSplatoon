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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long contactStamp = BlockAttackObservation.ContactStamp(token, entityId);
#endif
            var terrain = BlockTerrain.Current;
            if (terrain.SurfaceOnly && !anchorToMap)
            {
                int actual = terrain.Dynamic.Build(point, normal, radius * BlockDynamicWorld.BuildRadiusScale, token.Credit.Team, reserved);
                token.CompleteBuild(reserved, auxiliary, actual, entityId); Award(token);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                BlockAttackObservation.Effective(token, entityId, "dynamic", actual, contactStamp);
#endif
                return;
            }
            bool completed = false;
            terrain.QueueBuild(point, normal, token.Credit.Team, reserved,
                terrain.SurfaceOnly ? radius * BlockDynamicWorld.BuildRadiusScale : radius, anchorToMap, result =>
            {
                if (completed || !Valid(token)) return;
                completed = true;
                token.CompleteBuild(reserved, auxiliary, result.Added, entityId); Award(token);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                BlockAttackObservation.Effective(token, entityId, "surface", result.Added, contactStamp);
#endif
            }, ordinaryMainAttack: token.Credit.Kind == PaintAttackKind.Main && !auxiliary);
        }
        public static void Direct(BlockAttackToken token, Vector3 point, int perContact, uint entityId = 0, uint eventOrdinal = 0)
        {
            if (!Valid(token) || !token.BeginEvent(entityId, eventOrdinal, 3)) return;
            int reserved = token.ReserveDirect(perContact); if (reserved == 0) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long contactStamp = BlockAttackObservation.ContactStamp(token, entityId);
#endif
            int dynamicRemoved = 0, staticQuota = reserved;
            var terrain = BlockTerrain.Current;
            if (terrain.SurfaceOnly)
            {
                // Select one nearest-first budget across both representations, before mutating either.
                var candidates = new List<(float distance, ulong dynamicId)>();
                float reach = token.DirectRadius + BlockWorld.CellSize * .5f + .001f;
                float squared = reach * reach;
                var low = BlockWorld.CellAt(point - Vector3.one * reach);
                var high = BlockWorld.CellAt(point + Vector3.one * reach);
                for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
                {
                    var cell = new Vector3Int(x, y, z);
                    if (!terrain.World.TryGet(cell, out var own) || own.Owner == token.Credit.Team) continue;
                    var center = BlockWorld.Center(cell); float distance = (center - point).sqrMagnitude;
                    if (distance <= squared && terrain.Dynamic.Visible(point, center)) candidates.Add((distance, 0));
                }
                foreach (var body in terrain.Dynamic.Bodies.Values)
                {
                    float distance = (body.Body.position - point).sqrMagnitude;
                    if (body.Owner != token.Credit.Team && distance <= squared && terrain.Dynamic.Visible(point, body.Body.position)) candidates.Add((distance, body.Id));
                }
                candidates.Sort((a, b) => { int compare = a.distance.CompareTo(b.distance); return compare != 0 ? compare : a.dynamicId.CompareTo(b.dynamicId); });
                var ids = new List<ulong>(); staticQuota = 0;
                for (int i = 0; i < Math.Min(reserved, candidates.Count); i++)
                    if (candidates[i].dynamicId == 0) staticQuota++; else ids.Add(candidates[i].dynamicId);
                dynamicRemoved = terrain.Dynamic.DestroyIds(ids, token.Credit.Team);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                BlockDynamicAcceptanceProbe.RecordEnemyDirect(token, entityId, ids, dynamicRemoved);
                BlockAttackObservation.Effective(token, entityId, "direct", dynamicRemoved, contactStamp);
#endif
                if (staticQuota == 0) { token.CompleteDirect(reserved, dynamicRemoved); Award(token); return; }
                if (dynamicRemoved > 0) { token.CompleteDirect(dynamicRemoved, dynamicRemoved); Award(token); }
            }
            bool completed = false;
            BlockTerrain.Current.QueueDestroy(point, token.DirectRadius, 0, token.Credit.Team, true, result =>
            {
                if (completed || !Valid(token)) return;
                completed = true;
                token.CompleteDirect(reserved - dynamicRemoved, result.RemovedEnemy); Award(token);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                BlockAttackObservation.Effective(token, entityId, "direct", result.RemovedEnemy, contactStamp);
#endif
            }, staticQuota, 0, ordinaryMainAttack: token.Credit.Kind == PaintAttackKind.Main);
        }
        public static void Blast(BlockAttackToken token, Vector3 point, float radius, uint entityId = 0, uint eventOrdinal = 0)
        {
            if (!Valid(token) || radius <= 0 || !token.BeginEvent(entityId, eventOrdinal, 4)) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var blastObservation = BlockAttackObservation.BeginBlast(token, entityId, eventOrdinal);
#endif
            var terrain = BlockTerrain.Current;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            int failuresAtQueue = terrain.FailedTransactions;
#endif
            // Capture both target sets in this simulation step. Dynamic deletion is an ID transaction,
            // independent of the subsequent terrain mesh publication and physics motion.
            var dynamicResult = terrain.SurfaceOnly ? terrain.Dynamic.DestroyInRadius(point, radius,
                radius * BlockGameplaySettings.FriendlyBlastRadiusFraction, token.Credit.Team, false) : default;
            if (dynamicResult.RemovedEnemy > 0) { token.RemovedEnemy += dynamicResult.RemovedEnemy; Award(token); }
            // The terrain queue bounds work per tick. The radius is the gameplay limit;
            // a large blast is completed over several atomic publications, never truncated.
            bool completed = false;
            BlockTerrain.Current.QueueDestroy(point, radius, radius * BlockGameplaySettings.FriendlyBlastRadiusFraction, token.Credit.Team, false, result =>
            {
                if (completed || !Valid(token)) return;
                completed = true;
                token.RemovedEnemy += result.RemovedEnemy; Award(token);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // FinishRound cancels commands before Match publishes its Finished phase.
                // Their ordinary default callbacks are not completed collider publications.
                if (terrain != null && BlockTerrain.Current == terrain && terrain.Round == blastObservation.Round &&
                    terrain.Dynamic?.Frozen != true && terrain.FailedTransactions == failuresAtQueue)
                    BlockAttackObservation.Completed(blastObservation, dynamicResult.Changed + result.Changed);
#endif
            });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            BlockAttackObservation.Queued(blastObservation, dynamicResult.Changed);
#endif
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
            if (collider != null && collider.TryGetComponent<BlockDynamicBody>(out var body)) return body.TryContact(point, normal, out contact);
            return BlockTerrain.Current != null && BlockTerrain.Current.TryContact(collider, point, normal, out contact);
        }
        public static void Clear()
        {
            _generation++; Tokens.Clear(); BuildFractions.Clear();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            BlockAttackObservation.Clear();
#endif
        }
    }
}
