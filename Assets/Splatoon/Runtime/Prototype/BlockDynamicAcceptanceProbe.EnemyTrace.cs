#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        [Serializable] public sealed class EnemyExecutionSample
        {
            public string kind, source;
            public EnemyPlanIdentity plan;
            public double serverTime, nextShotAt, inkRecoverAt;
            public uint sequence, inputTick, inputLife, inputHeroRevision, fireSequence, releaseSequence, acknowledged;
            public uint shotSequenceBefore, shotSequence, entity, life, heroRevision, round;
            public ulong action, actualTargetId;
            public int pellet, queuedBefore, discardedInputs, actualTargetOwner, removed, revision;
            public bool fire, cancelFire, swim, timedOut, alive, needsRelease, standQueried, canShoot, emitted;
            public bool knownContact, validContact, actualIdsAbsent;
            public float materialBefore, materialAfter;
            public string phaseBefore, phaseAfter;
            public Vector3 position, contactPoint;
            public List<ulong> removedIds;
            [NonSerialized] public BlockAttackToken token;
        }
        [Serializable] public sealed class EnemyExecutionReport
        {
            public string source = "normal final input; actual authority selection after timeout; original main weapon call before resource recovery; real Spawn/contact/Direct observations; centre forecasts are planning only";
            public int capacity = 8192, overflow;
            public long inputFrames, authoritySteps, queuedInputs, reusedInputs, timeoutSteps, weaponSteps, emittedShots, spawnedProjectiles, contacts, directlyRemoved;
            public List<EnemyExecutionSample> samples = new();
        }
        sealed partial class Report { public EnemyExecutionReport enemyExecution = new(); }
        readonly Dictionary<(uint round, ulong owner, uint life, uint shot), EnemyExecutionSample> _enemyEmissions = new();
        readonly Dictionary<(uint round, ulong owner, uint entity), EnemyExecutionSample> _enemyProjectiles = new();
        void ClearEnemyExecutionBindings() { _enemyEmissions.Clear(); _enemyProjectiles.Clear(); }
        static bool EnemyEmissionMatches(EnemyExecutionSample emission, InkShot shot, int generation)
            => emission.plan.connectionGeneration == generation && emission.plan.owner == shot.Shooter &&
                emission.plan.round == shot.Round && emission.life == shot.Lifecycle && emission.heroRevision == shot.HeroRevision &&
                emission.action == shot.ActionId && emission.shotSequence == shot.ShotSequence;
        static bool EnemyProjectileMatches(EnemyExecutionSample spawn, InkShot shot, int generation)
            => EnemyEmissionMatches(spawn, shot, generation) && spawn.entity == shot.Id && spawn.pellet == shot.PelletIndex &&
                ReferenceEquals(spawn.token, shot.BlockAttack);

        public static bool TracingEnemy(PrototypePlayer player)
            => _instance != null && _instance._report?.mode == "network" && TryEnemyPlanTrace(player, out _);

        EnemyExecutionSample NewEnemyExecution(string kind, EnemyPlanIdentity plan)
        {
            var report = _report.enemyExecution;
            if (report.samples.Count >= report.capacity) { report.overflow++; return null; }
            var sample = new EnemyExecutionSample { kind = kind, plan = plan, round = plan.round, life = plan.life, heroRevision = plan.heroRevision,
                serverTime = PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : 0 };
            report.samples.Add(sample); return sample;
        }
        static void InputFields(EnemyExecutionSample sample, PlayerInputFrame input, PlayerSnapshot state)
        {
            sample.sequence = input.Sequence; sample.inputTick = input.Tick;
            sample.inputLife = input.Revision; sample.inputHeroRevision = input.HeroRevision;
            sample.fireSequence = input.FireSequence; sample.releaseSequence = input.ReleaseSequence;
            sample.fire = input.Fire; sample.cancelFire = input.CancelFire; sample.swim = input.Swim;
            sample.life = state.Revision; sample.heroRevision = state.HeroRevision;
            sample.acknowledged = state.AcknowledgedInput; sample.timedOut = state.InputTimedOut;
            sample.alive = state.IsAlive; sample.needsRelease = state.AttackNeedsRelease;
            sample.materialBefore = sample.materialAfter = state.Ink; sample.position = state.Position;
            sample.phaseBefore = sample.phaseAfter = state.WeaponPhase.ToString();
            sample.nextShotAt = state.NextShotAt; sample.inkRecoverAt = state.InkRecoverAt;
        }
        public static void RecordEnemyInput(PrototypePlayer player, PlayerInputFrame input)
        {
            if (!TracingEnemy(player) || !TryEnemyPlanTrace(player, out var plan)) return;
            var probe = _instance; probe._report.enemyExecution.inputFrames++;
            var sample = probe.NewEnemyExecution("input", plan); if (sample == null) return;
            InputFields(sample, input, player.PresentedState);
        }
        public static void RecordEnemyAuthority(PrototypePlayer player, PlayerSnapshot state, PlayerInputFrame input, int queuedBefore, int discarded)
        {
            if (!TracingEnemy(player) || !TryEnemyPlanTrace(player, out var plan)) return;
            var probe = _instance; var report = probe._report.enemyExecution;
            report.authoritySteps++; if (queuedBefore > 0) report.queuedInputs++; else report.reusedInputs++;
            if (state.InputTimedOut) report.timeoutSteps++;
            var sample = probe.NewEnemyExecution("authority-input", plan); if (sample == null) return;
            InputFields(sample, input, state); sample.queuedBefore = queuedBefore; sample.discardedInputs = discarded;
            sample.source = queuedBefore > 0 ? "oldest queued input" : "reused last input";
        }
        public static void RecordEnemyWeapon(PrototypePlayer player, PlayerInputFrame input, PlayerSnapshot before, PlayerSnapshot after,
            bool standQueried, bool canShoot, bool emitted, WeaponFireResult result)
        {
            if (!player.IsServer || !TracingEnemy(player) || !TryEnemyPlanTrace(player, out var plan)) return;
            var probe = _instance; var report = probe._report.enemyExecution;
            report.weaponSteps++; if (emitted) report.emittedShots++;
            var sample = probe.NewEnemyExecution("weapon", plan); if (sample == null) return;
            InputFields(sample, input, before); sample.standQueried = standQueried; sample.canShoot = canShoot; sample.emitted = emitted;
            sample.materialAfter = after.Ink; sample.phaseAfter = after.WeaponPhase.ToString();
            sample.action = result.ActionId; sample.shotSequenceBefore = before.ShotSequence; sample.shotSequence = after.ShotSequence;
            if (emitted) probe._enemyEmissions[(plan.round, player.PlayerId, after.Revision, after.ShotSequence)] = sample;
        }
        public static void RecordEnemySpawn(InkShot shot)
        {
            var probe = _instance;
            if (probe == null || !probe._enemyEmissions.TryGetValue((shot.Round, shot.Shooter, shot.Lifecycle, shot.ShotSequence), out var emission) ||
                !EnemyEmissionMatches(emission, shot, probe._report.connectionGenerations)) return;
            probe._report.enemyExecution.spawnedProjectiles++;
            var sample = probe.NewEnemyExecution("spawn", emission.plan); if (sample == null) return;
            sample.sequence = emission.sequence; sample.inputTick = emission.inputTick; sample.action = shot.ActionId;
            sample.shotSequence = shot.ShotSequence; sample.entity = shot.Id; sample.pellet = shot.PelletIndex;
            sample.life = shot.Lifecycle; sample.heroRevision = shot.HeroRevision; sample.round = shot.Round; sample.position = shot.Origin;
            sample.token = shot.BlockAttack;
            probe._enemyProjectiles[(shot.Round, shot.Shooter, shot.Id)] = sample;
        }
        public static void RecordEnemyContact(InkShot shot, bool known, bool valid, BlockContact contact, Vector3 point, string source)
        {
            var probe = _instance;
            if (probe == null || !probe._enemyProjectiles.TryGetValue((shot.Round, shot.Shooter, shot.Id), out var spawn) ||
                !EnemyProjectileMatches(spawn, shot, probe._report.connectionGenerations)) return;
            probe._report.enemyExecution.contacts++;
            var sample = probe.NewEnemyExecution("contact", spawn.plan); if (sample == null) return;
            sample.sequence = spawn.sequence; sample.inputTick = spawn.inputTick; sample.action = shot.ActionId;
            sample.entity = shot.Id; sample.pellet = shot.PelletIndex; sample.round = shot.Round;
            sample.life = shot.Lifecycle; sample.heroRevision = shot.HeroRevision; sample.shotSequence = shot.ShotSequence;
            sample.knownContact = known; sample.validContact = valid; sample.actualTargetId = contact.DynamicId;
            sample.actualTargetOwner = contact.Owner; sample.contactPoint = point; sample.source = source;
        }
        public static void RecordEnemyDirect(BlockAttackToken token, uint entity, IReadOnlyList<ulong> ids, int removed)
        {
            var probe = _instance;
            if (probe == null || token == null || !probe._enemyProjectiles.TryGetValue((token.Credit.Round, token.Credit.Owner, entity), out var spawn) ||
                spawn.plan.connectionGeneration != probe._report.connectionGenerations || !ReferenceEquals(token, spawn.token)) return;
            probe._report.enemyExecution.directlyRemoved += removed;
            var sample = probe.NewEnemyExecution("direct-dynamic-removal", spawn.plan); if (sample == null) return;
            sample.sequence = spawn.sequence; sample.inputTick = spawn.inputTick; sample.action = spawn.action;
            sample.entity = entity; sample.round = token.Credit.Round; sample.removed = removed;
            sample.shotSequence = spawn.shotSequence; sample.pellet = spawn.pellet;
            sample.removedIds = new List<ulong>(ids); sample.actualIdsAbsent = removed == ids.Count;
            var world = BlockTerrain.Current.Dynamic; sample.revision = (int)world.LifecycleRevision;
            foreach (ulong id in ids) if (world.Bodies.ContainsKey(id)) sample.actualIdsAbsent = false;
        }
    }
}
#endif
