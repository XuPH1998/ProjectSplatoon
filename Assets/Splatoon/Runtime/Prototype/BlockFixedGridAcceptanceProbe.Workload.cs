#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockFixedGridAcceptanceProbe
    {
        sealed class Agent
        {
            public uint life, round;
            public int waypoint;
            public bool enteredRoute, recovering, hasEnemy, reachableEnemy, hasProgress;
            public Vector3 enemy, previousFeet;
            public double nextTarget, progressAt, stuckFor, detourUntil, nextJump;
            public int detourSide = 1;
            public readonly Vector3[] candidates = new Vector3[16];
            public readonly float[] candidateDistance = new float[16];
            // Diagnostic totals belong to this owner/life/round, just like targeting state.
            public long targetSelections, targetForecasts, targetNoWorld, targetNoCandidates, targetExhausted;
            public long targetNoContact, targetUnresolved, targetStaleKnown, targetUnowned, targetFriendly, targetReachable;
            public double nextTargetDiagnostic = 10;
        }
        [Serializable] public sealed class BlastPressureActor
        {
            public ulong owner;
            public uint life, heroRevision, releaseSequence;
            public int team, slot, removed;
            public long receivedTick, deliveredTick, explosionTick;
            public bool holding, received, delivered, completed, weaponIdle, canStartHolding, standingVerified;
            public float material, subCost;
        }
        [Serializable] public sealed class BlastPressureReport
        {
            public string scope = "Development-only barrier delays already received real owner release edges until one authority network tick. Production payment, fuse and collision paths stay unchanged; this is a simultaneous explosion cost test, not an input-latency test.";
            public string failure;
            public long targetTick;
            public int received, delivered, completed;
            public bool sameReleaseTick, sameExplosionTick;
            public double directRemovalMaximumMilliseconds;
            public List<BlastPressureActor> actors = new();
        }
        readonly Dictionary<ulong, Agent> _agents = new();
        readonly Dictionary<ulong, PlayerInputFrame> _releases = new();
        readonly TpsAimSolver _aim = new();
        static readonly ProfilerMarker TargetingCpu = new("Splatoon.FixedProbe.Targeting");
        static string ActorRole(PlayerSnapshot state) => state.Slot < 2 ? "builder" : state.Slot == 2 ? "enemy-remover" : "explosive";
        PlayerLoadout WantedLoadout(PlayerSnapshot state)
        {
            if (_report.mode == "blast8") return new PlayerLoadout(1, 3, 1);
            int hero = state.Slot switch { 0 => 1, 1 => 8, 2 => 4, _ => 5 };
            return new PlayerLoadout(hero, state.Slot == 3 ? 3 : SubWeaponConfigService.Current.DefaultId(hero), 1);
        }
        void Equipment(PrototypeMatch match)
        {
            var local = PrototypePlayer.Local; if (local == null) return;
            if (_report.mode == "network") { _report.equipmentReady = true; return; }
            var state = local.PresentedState; var wanted = WantedLoadout(state);
            _report.equipmentReady = PlayerLoadout.From(state).Matches(wanted) && !local.HeroChangePending;
            if (match.State.Value.Phase == MatchPhase.Practice && !_report.equipmentReady && !local.HeroChangePending && state.IsAlive)
                local.RequestLoadoutChange(wanted, HeroSelectionOrigin.Warmup);
        }
        static Vector2 AimAt(PrototypePlayer player, PlayerSnapshot state, Vector3 target)
        {
            var profile = player.Presentation; var pivot = state.Position + PrototypePlayer.CameraPivotOffset(state, profile);
            var look = new Vector2(state.Yaw, state.Pitch);
            for (int i = 0; i < 3; i++)
            {
                var from = PrototypePlayer.CameraPosition(pivot, Quaternion.Euler(look.y, look.x, 0), profile); var delta = target - from;
                look = new Vector2(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg,
                    Mathf.Clamp(-Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg, -65, 75));
            }
            return look;
        }
        static Vector3 RouteWaypoint(PlayerSnapshot state, int index)
        {
            float side = state.Slot % 2 == 0 ? -1 : 1, team = state.Team == 1 ? 1 : -1;
            float z = (index % 6) switch { 0 => -20, 1 => -10, 2 => 8, 3 => 20, 4 => 8, _ => -10 };
            return new Vector3(side * 11.5f, 0, z * team);
        }
        Agent AgentFor(PrototypePlayer player, PlayerSnapshot state)
        {
            uint round = PrototypeMatch.Current?.State.Value.Round ?? 0;
            if (!_agents.TryGetValue(player.PlayerId, out var agent) || agent.life != state.Revision || agent.round != round)
            { agent = new Agent { life = state.Revision, round = round }; _agents[player.PlayerId] = agent; }
            return agent;
        }
        static void Candidate(Agent agent, Vector3 feet, Vector3 point, bool paint, float radiusSquared)
        {
            Vector3 delta = point - feet; float squared = delta.sqrMagnitude;
            if (squared > radiusSquared) return;
            // Keep one candidate per direction and type. A single hidden nearest
            // paint cell must not starve all the reachable enemy structures.
            int sector = Mathf.Min(7, (int)(Mathf.Repeat(Mathf.Atan2(delta.z, delta.x) * Mathf.Rad2Deg + 180, 360) / 45));
            int index = sector + (paint ? 8 : 0);
            if (squared >= agent.candidateDistance[index]) return;
            agent.candidateDistance[index] = squared; agent.candidates[index] = point;
        }
        void SelectEnemy(PrototypePlayer player, PlayerSnapshot state, Agent agent)
        {
            using var targeting = TargetingCpu.Auto();
            agent.hasEnemy = agent.reachableEnemy = false; agent.targetSelections++;
            var world = PrototypeMatch.Current?.Blocks?.FixedBlocks;
            if (world == null) { agent.targetNoWorld++; TargetDiagnostic(player, state, agent); return; }
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state);
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            float radius = Mathf.Min(26, weapon.EffectiveRange), radiusSquared = radius * radius;
            for (int i = 0; i < agent.candidateDistance.Length; i++) agent.candidateDistance[i] = float.PositiveInfinity;
            foreach (var block in world.Blocks.Values)
            {
                if (block.Owner != state.Team) Candidate(agent, feet, BlockWorld.Center(block.Cell), false, radiusSquared);
            }
            foreach (var paint in world.Surfaces.EnumeratePainted())
            {
                if (paint.Owner != state.Team && world.Surfaces.TryGetCenter(paint.Key, out var center))
                    Candidate(agent, feet, center, true, radiusSquared);
            }
            int tried = 0;
            for (int attempt = 0; attempt < agent.candidates.Length; attempt++)
            {
                int best = -1; float distance = float.PositiveInfinity;
                for (int i = 0; i < agent.candidates.Length; i++)
                    if ((tried & (1 << i)) == 0 && agent.candidateDistance[i] < distance) { best = i; distance = agent.candidateDistance[i]; }
                if (best < 0)
                {
                    if (tried == 0) agent.targetNoCandidates++; else agent.targetExhausted++;
                    TargetDiagnostic(player, state, agent); return;
                }
                tried |= 1 << best; agent.targetForecasts++;
                Vector3 point = agent.candidates[best];
                if (!agent.hasEnemy) { agent.hasEnemy = true; agent.enemy = point; }
                var aimed = state; Vector2 look = AimAt(player, state, point); aimed.Yaw = look.x; aimed.Pitch = look.y;
                var solution = _aim.Resolve(player, aimed, aimed.NextMuzzle);
                var forecast = weapon.AimMode == WeaponAimMode.WeaponReference ? WeaponImpactPrediction.Guide(_aim, solution, weapon, aimed, player.PlayerId)
                    : WeaponImpactPrediction.Predict(_aim, solution, weapon, aimed, player.PlayerId);
                var contact = forecast.Contact; BlockContact hit;
                bool resolved = TryTargetContact(PrototypeMatch.Current.Blocks, contact, out hit);
                if (!resolved)
                {
                    if (!contact.HasContact) agent.targetNoContact++;
                    else if (contact.BlockQuery.IsKnown) agent.targetStaleKnown++;
                    else agent.targetUnresolved++;
                    continue;
                }
                if (hit.Owner is not (1 or 2)) { agent.targetUnowned++; continue; }
                if (hit.Owner == state.Team) { agent.targetFriendly++; continue; }
                agent.enemy = point; agent.reachableEnemy = true; agent.targetReachable++;
                TargetDiagnostic(player, state, agent); return;
            }
            agent.targetExhausted++; TargetDiagnostic(player, state, agent);
        }
        static bool TryTargetContact(BlockTerrain terrain, TpsCollision contact, out BlockContact hit)
        {
            // A stale known identity must not become a hit on new ownership at its old point.
            if (contact.BlockQuery.IsKnown) return contact.BlockQuery.TryResolveCurrent(out hit);
            hit = default;
            return terrain != null &&
                (terrain.TrySurfacePaintContact(contact.Collider, contact.Point, contact.Normal, out hit) ||
                 terrain.TryContact(contact.Collider, contact.Point, contact.Normal, out hit));
        }
        static void TargetDiagnostic(PrototypePlayer player, PlayerSnapshot state, Agent agent)
        {
            double now = Time.realtimeSinceStartupAsDouble; if (now < agent.nextTargetDiagnostic) return;
            agent.nextTargetDiagnostic = now + 10;
            Debug.Log($"[FixedProbe.Targeting] owner={player.PlayerId} life={agent.life} round={agent.round} team={state.Team} slot={state.Slot} " +
                $"selections={agent.targetSelections} forecasts={agent.targetForecasts} noWorld={agent.targetNoWorld} noCandidates={agent.targetNoCandidates} exhausted={agent.targetExhausted} " +
                $"noContact={agent.targetNoContact} unresolvedMapOrLegacy={agent.targetUnresolved} staleKnown={agent.targetStaleKnown} unowned={agent.targetUnowned} friendly={agent.targetFriendly} reachable={agent.targetReachable}");
        }
        static bool MaySwim(PlayerSnapshot state, double now)
            => state.WeaponPhase == WeaponPhase.Idle && state.SubPhase == SubWeaponPhase.Idle &&
                now + 1e-8 >= state.AttackRecoveryUntil && now + 1e-8 >= state.SubRecoveryUntil;

        static Vector3 AvoidStall(Agent agent, PlayerSnapshot state, Vector3 direction, double now, bool moving, ref PlayerInputFrame input)
        {
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state);
            if (!agent.hasProgress) { agent.previousFeet = feet; agent.progressAt = now; agent.hasProgress = true; }
            if (!moving) { agent.previousFeet = feet; agent.progressAt = now; agent.stuckFor = 0; return direction; }
            double elapsed = now - agent.progressAt;
            if (elapsed >= .2)
            {
                float travelled = Vector3.ProjectOnPlane(feet - agent.previousFeet, Vector3.up).magnitude;
                agent.stuckFor = travelled < .045f ? agent.stuckFor + elapsed : 0;
                agent.previousFeet = feet; agent.progressAt = now;
            }
            if (agent.stuckFor > .8 && now >= agent.detourUntil)
            {
                agent.detourSide = -agent.detourSide; agent.detourUntil = now + 1.25;
                if (state.Grounded && now >= agent.nextJump)
                { input.JumpSequence = state.ConsumedJump + 1; agent.nextJump = now + 1.5; }
            }
            if (now < agent.detourUntil)
                direction = (direction + Vector3.Cross(Vector3.up, direction) * agent.detourSide * 1.4f).normalized;
            return direction;
        }
        void Input(PrototypePlayer player, ref PlayerInputFrame input)
        {
            var state = player.PresentedState; var match = PrototypeMatch.Current;
            input.Fire = input.Swim = input.SubHeld = input.CancelFire = input.CancelSub = false; input.Move = Vector2.zero;
            input.Look = new Vector2(state.Team == 1 ? 0 : 180, 75); input.JumpSequence = state.ConsumedJump;
            if (_finished || !state.IsAlive || match == null || match.State.Value.Phase == MatchPhase.Finished) return;
            if (_report.mode == "blast8") { PressureInput(player, state, ref input); return; }
            if (_report.mode == "network")
            {
                if (_stage is not ("seed" or "seed-rebuild" or "combat")) return;
                double networkNow = player.NetworkManager.ServerTime.Time, cycle = networkNow % 3;
                input.Swim = cycle >= 2 && MaySwim(state, networkNow); input.Fire = cycle < 1.8 && !state.AttackNeedsRelease;
                Vector3 networkFeet = PlayerMotorSimulation.HumanPosition(state);
                Vector3 networkDirection = state.Team == 1 ? Vector3.forward : Vector3.back;
                if (_stage == "combat")
                {
                    var networkAgent = AgentFor(player, state);
                    Vector3 networkTravel = Vector3.ProjectOnPlane(RouteWaypoint(state, networkAgent.waypoint) - networkFeet, Vector3.up);
                    if (networkTravel.magnitude < 1.3f)
                    { networkAgent.waypoint++; networkTravel = Vector3.ProjectOnPlane(RouteWaypoint(state, networkAgent.waypoint) - networkFeet, Vector3.up); }
                    networkDirection = AvoidStall(networkAgent, state, networkTravel.normalized, networkNow, true, ref input);
                }
                // Use the same legal release/recovery and side-of-route aiming
                // as the natural workload. Steep shots directly into one's feet
                // saturated the seed pile while the actor oscillated against it.
                input.Look = AimAt(player, state, networkFeet + networkDirection * 2 +
                    Vector3.Cross(Vector3.up, networkDirection) * .9f + Vector3.up * .04f);
                if (_stage == "combat")
                {
                    var networkRelative = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * networkDirection;
                    input.Move = new Vector2(networkRelative.x, networkRelative.z) * .7f;
                }
                return;
            }
            if (_stage != "combat" || match.State.Value.Phase != MatchPhase.Playing) return;
            double now = player.NetworkManager.ServerTime.Time, elapsed = Math.Max(0, now - match.State.Value.StartsAt);
            var agent = AgentFor(player, state);
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state), waypoint = RouteWaypoint(state, agent.waypoint);
            Vector3 travel = Vector3.ProjectOnPlane(waypoint - feet, Vector3.up);
            if (travel.magnitude < 1.3f) { agent.waypoint++; waypoint = RouteWaypoint(state, agent.waypoint); travel = Vector3.ProjectOnPlane(waypoint - feet, Vector3.up); }
            if (!agent.enteredRoute)
            {
                if (agent.waypoint > 0) agent.enteredRoute = true;
                else
                {
                    var exit = Mathf.Abs(feet.z) > 24.8f && Mathf.Abs(feet.x - waypoint.x) > .15f ? new Vector3(waypoint.x, feet.y, feet.z) : waypoint;
                    var outward = Vector3.ProjectOnPlane(exit - feet, Vector3.up);
                    var directionOut = AvoidStall(agent, state, outward.normalized, now, true, ref input);
                    input.Look = AimAt(player, state, feet + directionOut * 2 + Vector3.up);
                    var local = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * directionOut; input.Move = new Vector2(local.x, local.z) * .7f; return;
                }
            }
            float reserve = state.Slot == 3 ? PlayerLoadout.SubWeapon(state).Common.inkCost + 12 : 12;
            float recovered = state.Slot == 3 ? Mathf.Min(GameplayConfig.GetHero(state.HeroId).MaxInk - 1, reserve + 15) : 78;
            if (state.Ink < reserve) agent.recovering = true; if (state.Ink > recovered) agent.recovering = false;
            double phase = (elapsed + state.Slot * .55) % 10; bool recover = agent.recovering || phase >= 6;
            bool moving = recover || phase < 1.5 || phase >= 4;
            var direction = AvoidStall(agent, state, travel.normalized, now, moving, ref input);
            // Paint/build beside the walking line, retaining natural paid shots
            // without constructing a four-layer wall directly under every stride.
            var target = feet + direction * 2 + Vector3.Cross(Vector3.up, direction) * .9f + Vector3.up * .04f;
            if (state.Slot >= 2)
            {
                if (now >= agent.nextTarget) { agent.nextTarget = now + .5; SelectEnemy(player, state, agent); }
                if (agent.hasEnemy) target = agent.enemy;
            }
            input.Look = AimAt(player, state, target);
            var relative = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * direction;
            float move = recover ? .45f : phase < 1.5 || phase >= 4 ? .7f : 0;
            input.Move = new Vector2(relative.x, relative.z) * move;
            // Release a running attack first. Only request swimming after the
            // ordinary weapon state machine has reached Idle; never force it.
            input.Swim = recover && MaySwim(state, now); input.Fire = !recover && !state.AttackNeedsRelease;
            if (state.Slot == 2)
            {
                input.Fire &= agent.reachableEnemy;
            }
            if (state.Slot == 3)
            {
                double bomb = elapsed % 22;
                if (bomb >= 14.5 && bomb < 16 || state.SubPhase != SubWeaponPhase.Idle)
                { input.Swim = input.Fire = false; input.SubHeld = bomb >= 15 && bomb < 15.5 && state.Ink >= PlayerLoadout.SubWeapon(state).Common.inkCost && !state.SubNeedsRelease; }
            }
        }

        static Vector3 PressureLane(PlayerSnapshot state)
            => new(state.Slot switch { 0 => -6, 1 => 6, 2 => -12, _ => 12 }, 0, state.Team == 1 ? -22 : 22);
        void PressureInput(PrototypePlayer player, PlayerSnapshot state, ref PlayerInputFrame input)
        {
            if (_stage is not ("seed" or "seed-rebuild" or "seed-recover" or "drain" or "blast-hold" or "blast")) return;
            var feet = PlayerMotorSimulation.HumanPosition(state); var lane = PressureLane(state);
            var exit = Mathf.Abs(feet.z) > 24.8f && Mathf.Abs(feet.x - lane.x) > .1f ? new Vector3(lane.x, feet.y, feet.z) : lane;
            var travel = Vector3.ProjectOnPlane(exit - feet, Vector3.up);
            input.Look = AimAt(player, state, feet + Vector3.forward * (state.Team == 1 ? 2 : -2) + Vector3.up * .04f);
            if (_stage is "seed" or "seed-rebuild")
            {
                if (travel.magnitude > .12f)
                { var relative = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * travel.normalized; input.Move = new Vector2(relative.x, relative.z) * Mathf.Min(.7f, travel.magnitude * 2); return; }
                double cycle = player.NetworkManager.ServerTime.Time % 4;
                input.Swim = cycle >= 2 && MaySwim(state, player.NetworkManager.ServerTime.Time);
                input.Fire = cycle < 1.8 && !state.AttackNeedsRelease; return;
            }
            if (_stage is "seed-recover" or "drain")
            {
                input.Swim = state.Ink < PlayerLoadout.SubWeapon(state).Common.inkCost + .01f &&
                    MaySwim(state, player.NetworkManager.ServerTime.Time);
                // Default standing recovery remains valid if the actual floor
                // is not friendly; the harness waits for real material/holding.
                return;
            }
            input.SubHeld = _stage == "blast-hold" && !state.SubNeedsRelease && state.Ink >= PlayerLoadout.SubWeapon(state).Common.inkCost;
        }
        void ResetPressure()
        { _releases.Clear(); _report.blastPressure = new BlastPressureReport(); }
        BlastPressureActor PressureActor(ulong owner)
        {
            if (_report.blastPressure == null) return null;
            foreach (var actor in _report.blastPressure.actors) if (actor.owner == owner) return actor;
            return null;
        }
        void ObservePressure(PrototypeMatch match)
        {
            if (_report.mode != "blast8" || !match.IsServer || _stage is not ("seed-recover" or "blast-hold" or "blast")) return;
            _report.blastPressure ??= new BlastPressureReport();
            foreach (var player in match.Players)
            {
                if (player == null || !player.IsSpawned || player.IsTestBot) continue; var state = player.Snapshot.Value;
                var actor = PressureActor(player.PlayerId);
                if (actor == null) { actor = new BlastPressureActor { owner = player.PlayerId, life = state.Revision, heroRevision = state.HeroRevision, team = state.Team, slot = state.Slot }; _report.blastPressure.actors.Add(actor); }
                actor.holding = state.SubPhase == SubWeaponPhase.Holding; actor.material = state.Ink;
                actor.subCost = PlayerLoadout.SubWeapon(state).Common.inkCost;
                actor.weaponIdle = state.WeaponPhase == WeaponPhase.Idle;
                actor.standingVerified = _authorityStanding.TryGetValue(player.PlayerId, out var standing) &&
                    standing.life == state.Revision && standing.canShoot;
                double now = match.NetworkManager.ServerTime.Time;
                actor.canStartHolding = state.IsAlive && actor.weaponIdle && actor.standingVerified &&
                    state.SubPhase == SubWeaponPhase.Idle && !state.SubNeedsRelease && !state.Swimming &&
                    now + 1e-8 >= state.SubReadyAt && now + 1e-8 >= state.AttackRecoveryUntil && now + 1e-8 >= state.SubRecoveryUntil &&
                    state.Ink >= actor.subCost + .01f;
            }
            if (_stage == "blast" && Time.realtimeSinceStartupAsDouble - _stageAt > 15 && _report.blastPressure.completed < 8 && _report.blastPressure.failure == null)
                PressureFailure("Eight real paid throws did not complete within 15 seconds; inspect received/delivered counts and material.");
        }
        void PressureFailure(string reason)
        { if (_report.blastPressure.failure != null) return; _report.blastPressure.failure = reason; Error("Fixed blast8 barrier: " + reason); }
        public static void CoordinateBlastRelease(PrototypePlayer player, ref PlayerInputFrame input)
        {
            if (!Requested || _instance == null || !player.IsServer || _instance._report.mode != "blast8" || _instance._stage != "blast") return;
            _instance.PressureRelease(player, ref input);
        }
        void PressureRelease(PrototypePlayer player, ref PlayerInputFrame input)
        {
            var report = _report.blastPressure; var actor = PressureActor(player.PlayerId); if (report == null || actor == null || report.failure != null) return;
            var state = player.Snapshot.Value;
            if (!state.IsAlive || state.Revision != actor.life || state.HeroRevision != actor.heroRevision || input.Revision != actor.life ||
                input.HeroRevision != actor.heroRevision || input.CancelSub || input.CancelFire || input.Swim || state.InputTimedOut)
            { PressureFailure("Owner cancellation, timeout or lifecycle change aborted simultaneous release."); return; }
            long tick = player.NetworkManager.ServerTime.Tick;
            if (!actor.received && state.SubPhase == SubWeaponPhase.Holding && !input.SubHeld && input.SubReleaseSequence != state.SubConsumedRelease)
            {
                actor.received = true; actor.receivedTick = tick; actor.releaseSequence = input.SubReleaseSequence; _releases.Add(player.PlayerId, input); report.received++;
                if (report.received == 8) report.targetTick = tick + 1;
            }
            if (_releases.TryGetValue(player.PlayerId, out var release) && report.targetTick > 0 && tick >= report.targetTick)
            {
                input.SubHeld = false; input.SubReleaseSequence = release.SubReleaseSequence;
                if (!actor.delivered) { actor.delivered = true; actor.deliveredTick = tick; report.delivered++; }
                if (report.delivered == 8) { report.sameReleaseTick = true; foreach (var other in report.actors) report.sameReleaseTick &= other.deliveredTick == report.targetTick; }
                return;
            }
            input.SubHeld = true; input.SubReleaseSequence = state.SubConsumedRelease;
        }
        void ObservePressureCompletion(BlockAttackObservation.Blast observation, int removed)
        {
            if (_report.mode != "blast8" || _stage != "blast" || observation.Kind != "Sub") return;
            var report = _report.blastPressure; var actor = PressureActor(observation.Owner); if (report == null || actor == null || !actor.delivered || actor.completed) return;
            actor.completed = true; actor.explosionTick = observation.PhysicsTick; actor.removed = removed; report.completed++;
            report.directRemovalMaximumMilliseconds = Math.Max(report.directRemovalMaximumMilliseconds, observation.ElapsedMilliseconds);
            if (report.completed == 8)
            { long tick = report.actors[0].explosionTick; report.sameExplosionTick = true; foreach (var other in report.actors) report.sameExplosionTick &= other.explosionTick == tick; }
        }
    }
}
#endif
