#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockFixedGridAcceptanceProbe
    {
        [Serializable] public sealed class WorkloadEvidence
        {
            public int schema = 3, windowSeconds = 15, expectedPlayers, nominalSimulationRate, observedPlayers;
            public uint round;
            public bool authoritative, observationComplete;
            public double startServerTime, endServerTime, firstElapsed = -1, lastElapsed, maximumRouteSampleGap;
            public int authoritySteps, movementDiscontinuities, routeLifeBreaks, missingSourceActors, observerFaults;
            public string observerFailure;
            public string scope = "Authority steps and original paid attack sources, grouped by round/owner/life/equipment and fixed 15-second windows. Route distance excludes lifecycle changes and discontinuities. Counters are evidence, not an automatic formal pass.";
        }
        [Serializable] public sealed class ActorWindow
        {
            public ulong owner;
            public uint round, life, equipment;
            public int team, slot = -1, hero, sub, windowIndex, authoritySteps, shots, payments, mainPayments, subPayments, specialPayments;
            public string role;
            public double startElapsed, endElapsed, simulatedSeconds, aliveSeconds, bubbleSeconds, deadSeconds, paidMaterial, spentMaterial;
            public double recoveredMaterial, inkRecoverySeconds, fireRequestedSeconds, swimRequestedSeconds,
                appliedFireSeconds, needsReleaseSeconds, cannotShootSeconds, compactSeconds, inputTimedOutSeconds,
                cancelledFireSeconds, standingRecoverySeconds, subBlockedSeconds, distanceMoved, routeDistance, routeSeconds, stuckSeconds;
            public int actualFixedAdded, actualPaintAdded, enemyPaintReplaced, enemyFixedRemoved, friendlyFixedRemoved,
                enemyPaintRemoved, friendlyPaintRemoved, converted, relocated, successfulMutations, noChangeMutations,
                blastCompleted, blastRemoved, routeWaypointsPassed, routeCycles, movementDiscontinuities,
                effectiveCollisions, ordinaryMainCollisions, routeLifeBreaks;
            public double collisionMaximumMilliseconds, ordinaryMainCollisionMaximumMilliseconds;
        }
        [Serializable] public sealed class RouteSample
        {
            public ulong owner;
            public uint round, life, equipment, simulationTick;
            public double serverTime, elapsed;
            public Vector3 position;
            public int segment, waypoint, routePasses;
            public bool alive, grounded, swimming, compact, needsRelease, inputTimedOut, canShoot, requestedMove;
        }
        sealed class RouteTracker
        {
            public int segment, waypoint, passes;
            public double nextSample, previousSample = -1;
            public float previousInk;
            public bool hasInk, hasStep, previousAlive;
            public Vector3 previousStepFeet;
        }
        readonly Dictionary<(uint round, ulong owner, uint life, uint equipment, int window), ActorWindow> _windows = new();
        readonly Dictionary<(uint round, ulong owner, uint life), RouteTracker> _routes = new();
        readonly Dictionary<(uint round, ulong owner, uint life), (int slot, string role)> _sourceActors = new();
        readonly HashSet<ulong> _observedOwners = new();
        readonly Dictionary<ulong, (uint life, bool canShoot)> _authorityStanding = new();
        long _lastCompletedPhysicsTick = long.MinValue;
        int _completedFixedStepsThisFrame;
        int _observerFaultBaseline;

        // Called by the production authority after the ordinary motor and weapon step.
        // This observer never changes input, state, payment, physics or terrain.
        public static void RecordAuthorityStep(PrototypePlayer player, PlayerInputFrame requestedInput,
            PlayerInputFrame appliedInput, PlayerSnapshot beforeMotor, PlayerSnapshot afterWeapon,
            float dt, double now, bool canShoot, bool shot)
        {
            if (!Requested || _instance == null || !player.IsServer) return;
            _instance.AuthorityStep(player, requestedInput, appliedInput, beforeMotor, afterWeapon, dt, now, canShoot, shot);
        }
        bool EvidenceTime(uint round, double now, out double elapsed)
        {
            elapsed = 0;
            var match = PrototypeMatch.Current;
            if (_finished || _stage != "combat" || match == null || !match.IsSpawned || !match.IsServer ||
                match.State.Value.Phase != MatchPhase.Playing || match.State.Value.Round != round) return false;
            var state = match.State.Value;
            elapsed = now - state.StartsAt;
            if (elapsed < 0 || !double.IsFinite(elapsed)) return false;
            var evidence = _report.workloadEvidence;
            if (evidence.round != 0 && evidence.round != round) return false;
            evidence.authoritative = true; evidence.round = round;
            evidence.expectedPlayers = _report.configuredPlayers; evidence.nominalSimulationRate = GameplayConfig.Global.SimulationRate;
            evidence.startServerTime = state.StartsAt; evidence.endServerTime = now;
            if (evidence.firstElapsed < 0) evidence.firstElapsed = elapsed;
            evidence.lastElapsed = Math.Max(evidence.lastElapsed, elapsed);
            return true;
        }
        ActorWindow Window(uint round, ulong owner, uint life, uint equipment, byte team, int hero, int sub, double elapsed)
        {
            int index = (int)Math.Floor(elapsed / 15);
            var key = (round, owner, life, equipment, index);
            bool known = _sourceActors.TryGetValue((round, owner, life), out var actor);
            if (_windows.TryGetValue(key, out var window))
            {
                window.endElapsed = Math.Max(window.endElapsed, elapsed);
                if (known) { window.slot = actor.slot; window.role = actor.role; }
                return window;
            }
            window = new ActorWindow { round = round, owner = owner, life = life, equipment = equipment, team = team,
                hero = hero, sub = sub, slot = known ? actor.slot : -1, role = known ? actor.role : "unobserved-source",
                windowIndex = index, startElapsed = elapsed, endElapsed = elapsed };
            _windows.Add(key, window); _report.actorWindows.Add(window); return window;
        }
        void AuthorityStep(PrototypePlayer player, PlayerInputFrame requested, PlayerInputFrame applied,
            PlayerSnapshot before, PlayerSnapshot after, float dt, double now, bool canShoot, bool shot)
        {
            _authorityStanding[player.PlayerId] = (after.Revision, canShoot);
            uint round = PrototypeMatch.Current?.State.Value.Round ?? 0;
            if (!EvidenceTime(round, now, out double elapsed) || !float.IsFinite(dt) || dt <= 0) return;
            ulong owner = player.PlayerId; var sourceKey = (round, owner, after.Revision);
            _sourceActors[sourceKey] = (after.Slot, ActorRole(after)); _observedOwners.Add(owner);
            _report.workloadEvidence.observedPlayers = _observedOwners.Count;
            _report.workloadEvidence.authoritySteps++;
            long physicsTick = (long)Math.Round(Time.fixedTimeAsDouble / Time.fixedDeltaTime);
            if (physicsTick != _lastCompletedPhysicsTick) { _lastCompletedPhysicsTick = physicsTick; _completedFixedStepsThisFrame++; }
            var window = Window(round, owner, after.Revision, after.HeroRevision, after.Team, after.HeroId,
                PlayerLoadout.From(after).SubWeaponId, elapsed);
            window.authoritySteps++; window.simulatedSeconds += dt;
            if (after.InputTimedOut) window.inputTimedOutSeconds += dt;
            if (after.IsAlive)
            {
                window.aliveSeconds += dt;
                if (requested.Fire) window.fireRequestedSeconds += dt;
                if (requested.Swim) window.swimRequestedSeconds += dt;
                if (applied.Fire) window.appliedFireSeconds += dt;
                if (after.AttackNeedsRelease) window.needsReleaseSeconds += dt;
                if (!canShoot) window.cannotShootSeconds += dt;
                if (after.CompactBody) window.compactSeconds += dt;
                if (applied.CancelFire) window.cancelledFireSeconds += dt;
                if (SubWeaponSimulation.BlocksMain(after, now)) window.subBlockedSeconds += dt;
                if (after.HasInkRecovery) window.inkRecoverySeconds += dt;
                else if (!applied.Fire && !after.Swimming) window.standingRecoverySeconds += dt;
                if (shot) window.shots++;
                window.spentMaterial += Math.Max(0, before.Ink - after.Ink);
            }
            else if (after.IsBubble) window.bubbleSeconds += dt;
            else window.deadSeconds += dt;
            if (!_routes.TryGetValue(sourceKey, out var route))
            { route = new RouteTracker(); _routes.Add(sourceKey, route); }
            if (route.hasInk && before.IsAlive && after.IsAlive)
                window.recoveredMaterial += Math.Max(0, before.Ink - route.previousInk);
            route.hasInk = after.IsAlive; route.previousInk = after.Ink;
            Vector3 feet = PlayerMotorSimulation.HumanPosition(after);
            Vector3 displacement = Vector3.ProjectOnPlane(feet - PlayerMotorSimulation.HumanPosition(before), Vector3.up);
            float distance = displacement.magnitude;
            var hero = GameplayConfig.GetHero(after.HeroId);
            float maximumSpeed = Mathf.Max(hero.MoveSpeed, Mathf.Max(hero.SwimSpeed, Mathf.Max(hero.AirSwimSpeed, hero.WallSwimSpeed)));
            Vector3 beforeFeet = PlayerMotorSimulation.HumanPosition(before);
            bool externalRelocation = route.hasStep && (beforeFeet - route.previousStepFeet).sqrMagnitude > .0625f;
            bool lifeTransition = before.IsAlive != after.IsAlive || route.hasStep && route.previousAlive != after.IsAlive;
            bool lifeBreak = before.Revision != after.Revision || lifeTransition;
            bool discontinuity = before.IsAlive && after.IsAlive &&
                (externalRelocation || !float.IsFinite(distance) || distance > maximumSpeed * dt + .75f);
            route.previousStepFeet = feet; route.hasStep = true; route.previousAlive = after.IsAlive;
            if (discontinuity || lifeBreak)
            {
                route.segment++; route.waypoint = route.passes = 0;
                if (discontinuity) { window.movementDiscontinuities++; _report.workloadEvidence.movementDiscontinuities++; }
                else { window.routeLifeBreaks++; _report.workloadEvidence.routeLifeBreaks++; }
            }
            else if (after.IsAlive)
            {
                window.distanceMoved += distance;
                if (Vector3.ProjectOnPlane(RouteWaypoint(after, route.waypoint) - feet, Vector3.up).magnitude < 1.3f)
                {
                    route.waypoint++; route.passes++; window.routeWaypointsPassed++;
                    if (route.passes % 6 == 0) window.routeCycles++;
                }
                if (route.passes > 0 && Mathf.Abs(feet.z) <= 26.5f)
                { window.routeDistance += distance; if (distance > .001f) window.routeSeconds += dt; }
                if (requested.Move.sqrMagnitude > .01f && distance < .003f) window.stuckSeconds += dt;
            }
            if (now + 1e-8 < route.nextSample) return;
            route.nextSample = now + .25;
            if (route.previousSample >= 0) _report.workloadEvidence.maximumRouteSampleGap =
                Math.Max(_report.workloadEvidence.maximumRouteSampleGap, now - route.previousSample);
            route.previousSample = now;
            _report.routeSamples.Add(new RouteSample { owner = owner, round = round, life = after.Revision, equipment = after.HeroRevision,
                simulationTick = after.SimulationTick, serverTime = now, elapsed = elapsed, position = feet, segment = route.segment,
                waypoint = route.waypoint, routePasses = route.passes, alive = after.IsAlive, grounded = after.Grounded,
                swimming = after.Swimming, compact = after.CompactBody, needsRelease = after.AttackNeedsRelease,
                inputTimedOut = after.InputTimedOut, canShoot = canShoot, requestedMove = requested.Move.sqrMagnitude > .01f });
        }
        void Paid(BlockAttackObservation.Payment payment)
        {
            if (_finished) return;
            var source = payment.Source;
            bool eligible = EvidenceTime(source.Round, payment.ServerTime, out double elapsed);
            RecordPaidEvent(payment, eligible);
            if (!eligible) return;
            var window = Window(source.Round, source.Owner, source.Life, source.Equipment, source.Team,
                source.Hero, source.SubWeaponId, elapsed);
            window.payments++; window.paidMaterial += payment.Material;
            if (source.Kind == PaintAttackKind.Main) window.mainPayments++;
            else if (source.Kind == PaintAttackKind.Sub) window.subPayments++;
            else if (source.Kind == PaintAttackKind.Special) window.specialPayments++;
        }
        void Actual(BlockAttackObservation.ActualResult actual)
        {
            if (_finished) return;
            var source = actual.Source;
            bool eligible = EvidenceTime(source.Round, actual.ServerTime, out double elapsed);
            RecordActualEvent(actual, eligible);
            if (!eligible) return;
            var window = Window(source.Round, source.Owner, source.Life, source.Equipment, source.Team,
                source.Hero, source.SubWeaponId, elapsed);
            var counts = actual.Counts;
            window.actualFixedAdded += counts.NewFixed; window.actualPaintAdded += counts.NewPaint;
            window.enemyPaintReplaced += counts.EnemyPaintReplaced; window.enemyFixedRemoved += counts.EnemyFixedRemoved;
            window.friendlyFixedRemoved += counts.FriendlyFixedRemoved; window.enemyPaintRemoved += counts.EnemyPaintRemoved;
            window.friendlyPaintRemoved += counts.FriendlyPaintRemoved; window.converted += counts.Converted; window.relocated += counts.Relocated;
            if (actual.HadChanges) window.successfulMutations++; else window.noChangeMutations++;
            if (actual.Operation == BlockAttackObservation.ActualOperation.Blast)
            {
                window.blastCompleted++;
                window.blastRemoved += counts.EnemyFixedRemoved + counts.FriendlyFixedRemoved + counts.EnemyPaintRemoved + counts.FriendlyPaintRemoved;
            }
        }
        void CompleteWorkloadEvidence()
        {
            var evidence = _report.workloadEvidence;
            evidence.observerFaults = BlockAttackObservation.ObserverFaultCount - _observerFaultBaseline;
            evidence.observerFailure = evidence.observerFaults > 0 ? BlockAttackObservation.LastObserverError : null;
            evidence.missingSourceActors = 0;
            foreach (var window in _report.actorWindows) if (window.slot < 0) evidence.missingSourceActors++;
            evidence.observationComplete = evidence.authoritative && _report.completedNormalRound &&
                evidence.observedPlayers == evidence.expectedPlayers && evidence.firstElapsed >= 0 && evidence.firstElapsed <= 1 &&
                evidence.lastElapsed >= _report.configuredSeconds - .5 && evidence.missingSourceActors == 0 && evidence.observerFaults == 0;
            if (evidence.observerFaults > 0) Error("Actual-result observer failed: " + evidence.observerFailure);
        }
    }
}
#endif
