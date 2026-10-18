#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        [Serializable] public sealed class BlastPressureActor
        {
            public ulong owner;
            public uint life, heroRevision, shotsBefore, actionBefore, releaseInput, releaseSequence, entity;
            public byte team, slot;
            public Vector3 lane, seed, landing;
            public int surfaces, bodies, previewSteps, removed;
            public bool atLane, seeded, targetsSleeping, receivedRelease, deliveredRelease, committed, exploded, completed;
            public float inkBefore, inkAfter;
            public long receivedTick, deliveredTick, explosionTick;
            public double receivedServerTime, deliveredServerTime;
            public bool holdInput, holdRearming, authorityInputSeen, authorityInputHeld;
            public int holdAttempts;
            public double holdAttemptAt;
            public uint authorityInputSequence, authorityInputPress, authorityInputRelease;
            public bool authorityCancelSub, authorityCancelFire, authorityInputSwim;
            public string authorityInputPhase;
            public Vector2 authorityInputMove;
            public string recoveryMotion, recoveryGate;
            public Vector3 recoveryWaypoint;
            public int recoveryAttempts;
            public double recoveryMoveAt, recoveryNextPlanAt;
            public int recoveryPlans, recoveryReturnReplans;
            public List<BlastRecoveryCandidate> recoveryPlan = new();
        }
        [Serializable] public sealed class BlastRecoveryCandidate
        {
            public Vector3 waypoint;
            public string result, collider;
            public bool selected, freeEndpointBlocked;
        }
        [Serializable] public sealed class BlastPressureSubState
        {
            public ulong owner;
            public string phase, source, subPhase, failure, standingBlockers, recoveryMotion, recoveryGate;
            public double serverTime, wallSeconds, simulatedAt;
            public long physicsTick;
            public Vector3 position;
            public Vector3 recoveryWaypoint;
            public Vector2 inputMove;
            public float ink;
            public int recoveryAttempts;
            public bool needsRelease, timedOut, canStand, inputSeen, inputHeld, cancelSub, cancelFire, swim;
            public uint consumedPress, consumedRelease, action, inputSequence, inputPress, inputRelease;
        }
        [Serializable] public sealed class BlastPressurePhase
        {
            public string phase;
            public double serverTime, wallSeconds;
            public long physicsTick;
        }
        [Serializable] public sealed class BlastPressureReport
        {
            public string scope = "independent eight-owner pressure workload: real client inputs and payments; development-only authority release delivery barrier; not ordinary network input latency acceptance";
            public string recoveryMovementSource = "authority chooses an authored-floor waypoint clear of original map geometry; real owner Move pushes free bodies out and returns to the original lane; production standing checks remain unchanged; no player or Rigidbody state is assigned";
            public string targetRestSource = "targetsSleeping requires every nearby friendly target Rigidbody to be native sleeping with exactly zero native linear and angular velocity; range management tags do not substitute for confirmed rest";
            public string phase = "starting", failure;
            public uint round;
            public long firstReleaseStamp, allReleasesStamp, deliveredStamp, targetPhysicsTick;
            public double releaseCollectionMs, firstReleaseToDeliveryMs;
            public int controlPublications;
            public List<BlastPressureActor> actors = new();
            public List<BlastPressurePhase> phases = new();
            public List<BlastPressureSubState> subStates = new();
        }
        [Serializable] sealed class BlastPressureControl
        {
            public string session, phase;
            public uint round;
            public int sequence;
            public List<BlastPressureActor> actors = new();
        }
        sealed partial class Report { public BlastPressureReport blastPressure; }
        readonly Dictionary<ulong, PlayerInputFrame> _pressureReleases = new();
        readonly TpsAimSolver _pressureAim = new();
        readonly List<Vector3> _pressurePath = new();
        readonly Collider[] _pressureStanding = new Collider[32];
        readonly Dictionary<ulong, BlastPressureSubState> _pressureLastSubState = new();
        static readonly float[] PressureRecoveryAngles = { 0, 45, -45, 90, -90, 180 };
        BlastPressureControl _pressureControl;
        double _pressureNextPoll, _pressurePhaseAt, _pressureStableAt = -1;
        bool _pressureReleased, _pressureFailed;
        string PressurePath => _control + ".blast8-phase.json";
        static long PressureTick => (long)Math.Round(Time.fixedTimeAsDouble / Time.fixedDeltaTime);

        // Called after a real owner frame has been accepted and selected by the normal input queue.
        // Only the release edge is held; movement, cancellation, ACKs and all payment code stay live.
        public static void CoordinateBlastRelease(PrototypePlayer player, ref PlayerInputFrame input)
        {
            if (_instance == null || !Requested || !player.IsServer || _instance._report.mode != "blast8") return;
            _instance.PressureRelease(player, ref input);
        }
        BlastPressureActor PressureActor(ulong owner)
        {
            if (_pressureControl == null) return null;
            foreach (var actor in _pressureControl.actors) if (actor.owner == owner) return actor;
            return null;
        }
        void PressureFailure(string failure)
        {
            if (_pressureFailed) return;
            _pressureFailed = true; _report.blastPressure.failure = failure;
            Error("Blast8 preparation/release barrier: " + failure);
            PressurePhase("failed");
        }
        void PressurePhase(string phase)
        {
            _pressureControl.phase = phase; _pressureControl.sequence++;
            _pressurePhaseAt = Time.realtimeSinceStartupAsDouble; _pressureStableAt = -1;
            _report.blastPressure.phase = phase;
            _report.blastPressure.phases.Add(new BlastPressurePhase { phase = phase,
                serverTime = _match.NetworkManager.ServerTime.Time, wallSeconds = _pressurePhaseAt - _boot, physicsTick = PressureTick });
            PublishPressure();
        }
        void PublishPressure()
        {
            string temporary = PressurePath + ".writing";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(_pressureControl));
                if (File.Exists(PressurePath)) File.Replace(temporary, PressurePath, null); else File.Move(temporary, PressurePath);
                _report.blastPressure.controlPublications++;
            }
            catch (IOException exception) { PressureFailure("Phase publication failed: " + exception.Message); }
        }
        void ObserveBlastPressure(PrototypeMatch match, double now)
        {
            if (_report.mode != "blast8" || _stage != "combat" || match.State.Value.Phase != MatchPhase.Playing) return;
            _report.blastPressure ??= new BlastPressureReport();
            if (now < _pressureNextPoll) return;
            _pressureNextPoll = now + .1;
            if (!match.IsServer)
            {
                try
                {
                    if (!File.Exists(PressurePath)) return;
                    var received = JsonUtility.FromJson<BlastPressureControl>(File.ReadAllText(PressurePath));
                    if (received != null && received.session == _report.session && received.round == match.State.Value.Round &&
                        (_pressureControl == null || received.sequence > _pressureControl.sequence))
                    {
                        _pressureControl = received; _report.blastPressure.round = received.round;
                        _report.blastPressure.phase = received.phase; _report.blastPressure.actors = received.actors;
                    }
                    var local = PrototypePlayer.Local;
                    if (local != null && _pressureControl != null) ObservePressureSubState(local, local.PresentedState, false);
                }
                catch (IOException) { }
                return;
            }
            if (_pressureControl == null)
            {
                if (match.Players.Count != 8) return;
                _pressureControl = new BlastPressureControl { session = _report.session, round = match.State.Value.Round };
                _report.blastPressure.round = _pressureControl.round;
                foreach (var player in match.Players)
                {
                    var state = player.Snapshot.Value;
                    float x = state.Slot switch { 0 => -6, 1 => 6, 2 => -12, _ => 12 };
                    _pressureControl.actors.Add(new BlastPressureActor { owner = player.PlayerId, team = state.Team, slot = state.Slot,
                        life = state.Revision, heroRevision = state.HeroRevision, shotsBefore = state.ShotSequence,
                        lane = new Vector3(x, 0, state.Team == 1 ? -22 : 22) });
                }
                _report.blastPressure.actors = _pressureControl.actors;
                PressurePhase("traverse");
            }
            if (_pressureFailed || _pressureControl.phase == "done") return;
            foreach (var actor in _pressureControl.actors)
                if (!PrototypePlayer.ByOwner.TryGetValue(actor.owner, out var player) || !player.IsSpawned ||
                    !player.Snapshot.Value.IsAlive || player.Snapshot.Value.Revision != actor.life ||
                    player.Snapshot.Value.HeroRevision != actor.heroRevision || PlayerLoadout.From(player.Snapshot.Value).SubWeaponId != 3)
                { PressureFailure("An owner disconnected, died, respawned or changed equipment during preparation."); return; }
            if (now - _pressurePhaseAt > 35)
            {
                foreach (var actor in _pressureControl.actors)
                    if (PrototypePlayer.ByOwner.TryGetValue(actor.owner, out var player)) ObservePressureSubState(player, player.Snapshot.Value, true);
                PressureFailure("Timed out in phase " + _pressureControl.phase + "; no state or material was forced."); return;
            }
            bool all = true, seedChanged = false, holdChanged = false, recoveryChanged = false;
            foreach (var actor in _pressureControl.actors)
            {
                var player = PrototypePlayer.ByOwner[actor.owner]; var state = player.Snapshot.Value;
                bool canStand = ObservePressureSubState(player, state, true);
                actor.atLane = Vector3.ProjectOnPlane(PlayerMotorSimulation.HumanPosition(state) - actor.lane, Vector3.up).magnitude < .12f &&
                    state.Grounded && state.PlanarVelocity.sqrMagnitude < .0025f;
                if (_pressureControl.phase == "traverse") all &= actor.atLane;
                if (_pressureControl.phase is "seed" or "visual-seed")
                {
                    CountPressureTargets(actor, match.Blocks);
                    bool seeded = actor.surfaces > 0 && actor.bodies > 0 && state.ShotSequence > actor.shotsBefore;
                    seedChanged |= actor.seeded != seeded;
                    actor.seeded = seeded; all &= seeded;
                }
                if (_pressureControl.phase == "recover")
                {
                    var config = PlayerLoadout.SubWeapon(state);
                    recoveryChanged |= UpdateBlastRecoveryMove(match.Blocks, actor, HeroBodyShape.For(state.HeroId),
                        PlayerMotorSimulation.HumanPosition(state), canStand, now, _pressureStanding);
                    actor.targetsSleeping = true;
                    foreach (var body in match.Blocks.Dynamic.Bodies.Values)
                        if (body.Owner == actor.team && (body.Body.position - actor.seed).sqrMagnitude < 2.25f && !body.IsAtRest)
                            actor.targetsSleeping = false;
                    actor.recoveryGate = actor.recoveryMotion is "outbound" or "return" ? "real clearance movement in progress"
                        : !actor.atLane ? "not stationary at original lane" : !canStand ? "standing capsule blocked"
                        : state.InputTimedOut ? "real owner input timed out" : state.SubPhase != SubWeaponPhase.Idle ? "sub weapon not idle"
                        : state.SubNeedsRelease ? "real release required" : !actor.targetsSleeping ? "real target bodies still moving"
                        : state.Ink < config.Common.inkCost + 2 ? "normal material recovery"
                        : state.SimulatedAt < state.SubReadyAt || state.SimulatedAt < state.SubRecoveryUntil || state.SimulatedAt < state.AttackRecoveryUntil
                            ? "normal attack recovery clocks" : "ready; awaiting all owners, empty queues and stable interval";
                    all &= actor.recoveryMotion is not ("outbound" or "return") && actor.atLane && canStand && !state.InputTimedOut && state.SubPhase == SubWeaponPhase.Idle && !state.SubNeedsRelease &&
                        actor.targetsSleeping &&
                        state.Ink >= config.Common.inkCost + 2 && state.SimulatedAt >= state.SubReadyAt &&
                        state.SimulatedAt >= state.SubRecoveryUntil && state.SimulatedAt >= state.AttackRecoveryUntil;
                }
                if (_pressureControl.phase == "hold")
                {
                    bool holding = state.SubPhase == SubWeaponPhase.Holding && !state.SubNeedsRelease;
                    all &= holding;
                    holdChanged |= UpdateBlastHoldInput(actor, state, canStand, now);
                }
                if (_pressureControl.phase == "flight")
                { actor.committed = state.SubAction == actor.actionBefore + 1; if (actor.committed) actor.inkAfter = state.Ink; all &= actor.exploded && actor.completed && actor.committed; }
            }
            switch (_pressureControl.phase)
            {
                case "traverse" when all:
                    foreach (var actor in _pressureControl.actors)
                    {
                        var player = PrototypePlayer.ByOwner[actor.owner]; var state = player.Snapshot.Value;
                        state.Yaw = actor.team == 1 ? 0 : 180; state.Pitch = 75;
                        var preview = PressurePreview(player, state);
                        if (!preview.Valid || preview.End != SubPreviewEnd.Impact) { PressureFailure("No real flat-ground BurstBomb landing at a lane."); return; }
                        actor.landing = preview.Position;
                        // Deposit beside the throw corridor, inside the unchanged friendly blast radius.
                        actor.seed = new Vector3(preview.Position.x + (actor.lane.x < 0 ? -.75f : .75f), 0, preview.Position.z);
                        if (!Physics.Raycast(actor.seed + Vector3.up * 3, Vector3.down, out var floor, 5,
                            PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer), QueryTriggerInteraction.Ignore) ||
                            !match.Blocks.IsAuthoredMapCollider(floor.collider) || floor.normal.y < .99f)
                        { PressureFailure("A preparation target has no authored flat floor."); return; }
                        actor.seed = floor.point;
                    }
                    PressurePhase("seed"); break;
                case "seed" when all: PressurePhase("recover"); break;
                case "visual-seed" when all: PressurePhase("done"); break;
                case "recover" when all && match.Blocks.PendingTransactions == 0 && match.Projectiles.PendingCount == 0:
                    if (_pressureStableAt < 0) _pressureStableAt = now;
                    if (now - _pressureStableAt >= .5)
                    {
                        foreach (var actor in _pressureControl.actors)
                        { actor.holdInput = true; actor.holdAttempts = 1; actor.holdAttemptAt = now; }
                        PressurePhase("hold");
                    }
                    break;
                case "hold" when all:
                    int steps = -1;
                    foreach (var actor in _pressureControl.actors)
                    {
                        var player = PrototypePlayer.ByOwner[actor.owner]; var state = player.Snapshot.Value;
                        var preview = PressurePreview(player, state);
                        actor.previewSteps = (int)Math.Round(preview.Time * GameplayConfig.Global.SimulationRate);
                        CountPressureTargets(actor, match.Blocks);
                        bool target = false;
                        float radius = PlayerLoadout.SubWeapon(state).Blast.outerRadius * BlockGameplaySettings.FriendlyBlastRadiusFraction;
                        foreach (var body in match.Blocks.Dynamic.Bodies.Values)
                            if (body.Owner == state.Team && (body.Body.position - preview.Position).sqrMagnitude <= (radius - .15f) * (radius - .15f) &&
                                match.Blocks.Dynamic.Visible(preview.Position, body.Body.position)) { target = true; break; }
                        if (!preview.Valid || preview.End != SubPreviewEnd.Impact || !target || actor.surfaces == 0 || actor.bodies == 0 ||
                            steps >= 0 && actor.previewSteps != steps || !actor.atLane)
                        { PressureFailure("Real previews lack equal flight steps, a stationary lane, or a visible paid target inside the original blast radius."); return; }
                        steps = actor.previewSteps; actor.landing = preview.Position; actor.inkBefore = state.Ink; actor.actionBefore = state.SubAction;
                    }
                    PressurePhase("release"); break;
                case "release" when _pressureReleased: PressurePhase("flight"); break;
                case "flight" when all:
                    foreach (var actor in _pressureControl.actors) { actor.seeded = false; actor.shotsBefore = PrototypePlayer.ByOwner[actor.owner].Snapshot.Value.ShotSequence; }
                    PressurePhase("visual-seed"); break;
            }
            if ((seedChanged && (_pressureControl.phase is "seed" or "visual-seed")) ||
                (holdChanged && _pressureControl.phase == "hold") || (recoveryChanged && _pressureControl.phase == "recover"))
            { _pressureControl.sequence++; PublishPressure(); }
            if (_pressureControl.phase == "recover" && (!all || match.Blocks.PendingTransactions != 0 || match.Projectiles.PendingCount != 0))
                _pressureStableAt = -1;
        }
        public static bool UpdateBlastRecoveryMove(BlockTerrain terrain, BlastPressureActor actor, HeroBodyShape shape,
            Vector3 feet, bool canStand, double now, Collider[] standing)
        {
            if (actor.recoveryMotion == "outbound")
            {
                if (Vector3.ProjectOnPlane(actor.recoveryWaypoint - feet, Vector3.up).magnitude >= .07f && now - actor.recoveryMoveAt < 2.5)
                    return false;
                actor.recoveryMotion = "return"; actor.recoveryWaypoint = actor.lane; actor.recoveryMoveAt = now;
                return true;
            }
            bool returnExpired = actor.recoveryMotion == "return" && !actor.atLane && now - actor.recoveryMoveAt >= 2.5;
            if (actor.recoveryMotion == "return" && !actor.atLane && !returnExpired) return false;
            if (returnExpired)
            {
                // A blocked real return must not hold the same Move until the
                // phase timeout. Try another outward leg, then retry the lane.
                actor.recoveryReturnReplans++; actor.recoveryNextPlanAt = now;
            }
            if (canStand && !returnExpired)
            {
                if (!actor.atLane)
                {
                    actor.recoveryMotion = "return"; actor.recoveryWaypoint = actor.lane; actor.recoveryMoveAt = now;
                    return true;
                }
                if (string.IsNullOrEmpty(actor.recoveryMotion)) return false;
                actor.recoveryMotion = ""; actor.recoveryWaypoint = actor.lane;
                return true;
            }
            if (now < actor.recoveryNextPlanAt) return false;
            actor.recoveryNextPlanAt = now + .5;
            if (!TryBlastRecoveryWaypoint(terrain, actor, shape, feet, standing, out var waypoint))
            {
                if (actor.recoveryMotion == "blocked") return false;
                actor.recoveryMotion = "blocked"; actor.recoveryWaypoint = actor.lane;
                return true;
            }
            actor.recoveryAttempts++; actor.recoveryMotion = "outbound";
            actor.recoveryWaypoint = waypoint; actor.recoveryMoveAt = now;
            return true;
        }
        public static bool TryBlastRecoveryWaypoint(BlockTerrain terrain, BlastPressureActor actor, HeroBodyShape shape,
            Vector3 feet, Collider[] standing, out Vector3 waypoint)
        {
            Vector3 away = Vector3.ProjectOnPlane(feet - actor.seed, Vector3.up).normalized;
            if (away.sqrMagnitude < .5f) away = actor.lane.x < 0 ? Vector3.right : Vector3.left;
            int mapMask = PlayerMotorSimulation.WorldMask & ~(1 << BlockDynamicWorld.Layer);
            float radius = Mathf.Max(.05f, shape.Radius - .025f), segment = shape.Height * .5f - radius;
            Vector3 center = feet + shape.Center;
            actor.recoveryPlans++; actor.recoveryPlan.Clear();
            bool found = false; waypoint = actor.lane;
            for (int distance = 0; distance < 3; distance++)
            for (int direction = 0; direction < PressureRecoveryAngles.Length; direction++)
            {
                float angle = PressureRecoveryAngles[(actor.recoveryAttempts + direction) % PressureRecoveryAngles.Length];
                Vector3 travel = Quaternion.Euler(0, angle, 0) * away * (.6f + distance * .3f);
                var candidate = feet + travel;
                var evidence = new BlastRecoveryCandidate { waypoint = candidate, collider = "" };
                actor.recoveryPlan.Add(evidence);
                if (!Physics.Raycast(candidate + Vector3.up, Vector3.down, out var floor, 1.15f, mapMask, QueryTriggerInteraction.Ignore))
                    evidence.result = "no floor hit";
                else if (!terrain.IsAuthoredMapCollider(floor.collider))
                { evidence.result = "floor is not authored map"; evidence.collider = floor.collider.name; }
                else if (floor.normal.y < .99f)
                { evidence.result = "floor is not flat"; evidence.collider = floor.collider.name; }
                else if (candidate.y - floor.point.y < -.005f || candidate.y - floor.point.y > .1f)
                { evidence.result = "floor height differs from current feet"; evidence.collider = floor.collider.name; }
                // Free bodies are legal push contacts, including at the endpoint.
                // Only the planner excludes them; actual motor collision and the
                // production CanStand gate still include their real colliders.
                else if (!shape.Fits(candidate, standing, mapMask))
                { evidence.result = "original map blocks endpoint capsule"; evidence.collider = standing[0] != null ? standing[0].name : ""; }
                else if (Physics.CapsuleCast(center - Vector3.up * segment, center + Vector3.up * segment, radius,
                    travel.normalized, out var hit, travel.magnitude, mapMask, QueryTriggerInteraction.Ignore))
                { evidence.result = "original map blocks capsule path"; evidence.collider = hit.collider.name; }
                else
                {
                    evidence.freeEndpointBlocked = !shape.Fits(candidate, standing, 1 << BlockDynamicWorld.Layer);
                    evidence.selected = !found; evidence.result = found ? "clear alternative" : "selected";
                    if (!found) { waypoint = candidate; found = true; }
                }
            }
            return found;
        }
        public static Vector2 BlastRecoveryMoveInput(BlastPressureActor actor, Vector3 feet, float yaw)
        {
            if (actor.recoveryMotion is not ("outbound" or "return")) return Vector2.zero;
            Vector3 travel = Vector3.ProjectOnPlane(actor.recoveryWaypoint - feet, Vector3.up);
            if (travel.magnitude <= .07f) return Vector2.zero;
            var direction = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0)) * travel.normalized;
            return new Vector2(direction.x, direction.z) * Mathf.Min(.55f, travel.magnitude * 2);
        }
        public static bool UpdateBlastHoldInput(BlastPressureActor actor, PlayerSnapshot state, bool canStand, double now)
        {
            // A rejected real press is consumed by production simulation. Releasing
            // and pressing again is the only legal retry; never reset its latch/state.
            if (state.SubPhase == SubWeaponPhase.Idle && !actor.holdRearming && now - actor.holdAttemptAt >= 1 &&
                actor.authorityInputPhase == "hold" && actor.authorityInputSeen && actor.authorityInputHeld &&
                state.SubConsumedPress == actor.authorityInputPress)
            { actor.holdRearming = true; actor.holdInput = false; return true; }
            if (actor.holdRearming && actor.authorityInputPhase == "hold" && actor.authorityInputSeen &&
                !actor.authorityInputHeld && state.SubPhase == SubWeaponPhase.Idle && !state.SubNeedsRelease &&
                canStand && !state.InputTimedOut)
            { actor.holdRearming = false; actor.holdInput = true; actor.holdAttemptAt = now; actor.holdAttempts++; return true; }
            return false;
        }
        bool ObservePressureSubState(PrototypePlayer player, PlayerSnapshot state, bool authority)
        {
            var shape = HeroBodyShape.For(state.HeroId);
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state), center = feet + shape.Center;
            float radius = Mathf.Max(.05f, shape.Radius - .025f);
            int count = Physics.OverlapCapsuleNonAlloc(center + Vector3.up * (shape.Height * .5f - radius),
                center - Vector3.up * (shape.Height * .5f - radius), radius, _pressureStanding,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            var actor = PressureActor(player.PlayerId);
            var sample = new BlastPressureSubState { owner = player.PlayerId, phase = _pressureControl.phase,
                source = authority ? "authority Snapshot and selected real owner input before release barrier" : "local PresentedState; authority input fields are host observations",
                serverTime = player.NetworkManager.ServerTime.Time, simulatedAt = state.SimulatedAt,
                wallSeconds = Time.realtimeSinceStartupAsDouble - _boot, physicsTick = PressureTick,
                position = feet, ink = state.Ink, subPhase = state.SubPhase.ToString(), failure = state.SubFailure.ToString(),
                needsRelease = state.SubNeedsRelease, timedOut = state.InputTimedOut, canStand = count == 0,
                consumedPress = state.SubConsumedPress, consumedRelease = state.SubConsumedRelease, action = state.SubAction,
                inputSeen = actor != null && actor.authorityInputSeen, inputHeld = actor != null && actor.authorityInputHeld,
                inputSequence = actor?.authorityInputSequence ?? 0, inputPress = actor?.authorityInputPress ?? 0,
                inputRelease = actor?.authorityInputRelease ?? 0, cancelSub = actor != null && actor.authorityCancelSub,
                cancelFire = actor != null && actor.authorityCancelFire, swim = actor != null && actor.authorityInputSwim,
                inputMove = actor?.authorityInputMove ?? Vector2.zero,
                recoveryMotion = actor?.recoveryMotion ?? "", recoveryGate = actor?.recoveryGate ?? "",
                recoveryWaypoint = actor?.recoveryWaypoint ?? Vector3.zero, recoveryAttempts = actor?.recoveryAttempts ?? 0,
                standingBlockers = "" };
            for (int i = 0; i < count; i++)
            {
                var collider = _pressureStanding[i];
                var body = collider.GetComponentInParent<BlockDynamicBody>();
                sample.standingBlockers += (i == 0 ? "" : "; ") + collider.name + " layer=" + collider.gameObject.layer +
                    (body != null ? " dynamic=" + body.Id : "");
            }
            if (!_pressureLastSubState.TryGetValue(player.PlayerId, out var previous) || sample.phase != previous.phase ||
                sample.subPhase != previous.subPhase || sample.failure != previous.failure || sample.needsRelease != previous.needsRelease ||
                sample.timedOut != previous.timedOut || sample.canStand != previous.canStand || sample.inputHeld != previous.inputHeld ||
                sample.inputPress != previous.inputPress || sample.inputRelease != previous.inputRelease ||
                sample.consumedPress != previous.consumedPress || sample.consumedRelease != previous.consumedRelease ||
                sample.cancelSub != previous.cancelSub || sample.cancelFire != previous.cancelFire ||
                sample.recoveryMotion != previous.recoveryMotion || sample.recoveryGate != previous.recoveryGate ||
                (sample.inputMove - previous.inputMove).sqrMagnitude > .001f ||
                sample.wallSeconds - previous.wallSeconds >= 1)
            { _report.blastPressure.subStates.Add(sample); _pressureLastSubState[player.PlayerId] = sample; }
            return count == 0;
        }
        SubPreviewResult PressurePreview(PrototypePlayer player, PlayerSnapshot state)
        {
            var config = PlayerLoadout.SubWeapon(state);
            return SubWeaponMotion.Preview(SubWeaponService.SolveLaunch(player, state, config, 0), config,
                new SubEntityState { Owner = player.PlayerId, Team = state.Team }, _pressurePath, _pressureAim);
        }
        static void CountPressureTargets(BlastPressureActor actor, BlockTerrain terrain)
        {
            actor.surfaces = actor.bodies = 0;
            var center = BlockWorld.CellAt(actor.seed + Vector3.up * .1f);
            for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) for (int y = -1; y <= 1; y++)
                if (terrain.World.TryGet(center + new Vector3Int(x, y, z), out var cell) && cell.SurfaceAttached && cell.Owner == actor.team) actor.surfaces++;
            foreach (var body in terrain.Dynamic.Bodies.Values)
                if (body.Owner == actor.team && (body.Body.position - actor.seed).sqrMagnitude < 1.5f * 1.5f) actor.bodies++;
        }
        void BlastPressureInput(PrototypePlayer player, PlayerSnapshot state, ref PlayerInputFrame input)
        {
            var actor = PressureActor(player.PlayerId); if (actor == null) return;
            var phase = _pressureControl.phase;
            input.Look = new Vector2(state.Team == 1 ? 0 : 180, 75);
            if (phase == "traverse")
            {
                Vector3 feet = PlayerMotorSimulation.HumanPosition(state);
                // First leave the central shield's x span while still behind it.
                Vector3 waypoint = Mathf.Abs(feet.z) > 24.8f && Mathf.Abs(feet.x - actor.lane.x) > .1f
                    ? new Vector3(actor.lane.x, feet.y, feet.z) : actor.lane;
                var travel = Vector3.ProjectOnPlane(waypoint - feet, Vector3.up);
                if (travel.magnitude > .07f)
                {
                    var direction = Quaternion.Inverse(Quaternion.Euler(0, input.Look.x, 0)) * travel.normalized;
                    input.Move = new Vector2(direction.x, direction.z) * Mathf.Min(.7f, travel.magnitude * 2);
                }
            }
            if (phase is "seed" or "visual-seed")
            {
                input.Look = AimAt(player, state, actor.seed);
                input.Fire = !actor.seeded && !state.AttackNeedsRelease && state.Ink > 5;
            }
            if (phase == "recover" && (actor.recoveryMotion is "outbound" or "return"))
                input.Move = BlastRecoveryMoveInput(actor, PlayerMotorSimulation.HumanPosition(state), input.Look.x);
            input.SubHeld = phase == "hold" && actor.holdInput;
        }
        void PressureRelease(PrototypePlayer player, ref PlayerInputFrame input)
        {
            if (_stage != "combat" || _pressureControl == null) return;
            var actor = PressureActor(player.PlayerId); if (actor == null) return;
            actor.authorityInputSeen = true; actor.authorityInputPhase = _pressureControl.phase;
            actor.authorityInputSequence = input.Sequence; actor.authorityInputPress = input.SubPressSequence;
            actor.authorityInputRelease = input.SubReleaseSequence; actor.authorityInputHeld = input.SubHeld;
            actor.authorityCancelSub = input.CancelSub; actor.authorityCancelFire = input.CancelFire; actor.authorityInputSwim = input.Swim;
            actor.authorityInputMove = input.Move;
            if (_pressureControl.phase != "release" || _pressureFailed) return;
            var state = player.Snapshot.Value;
            if (!state.IsAlive || state.Revision != actor.life || state.HeroRevision != actor.heroRevision ||
                input.Revision != actor.life || input.HeroRevision != actor.heroRevision || input.CancelSub || input.CancelFire || input.Swim || state.InputTimedOut)
            { PressureFailure("A real cancellation, timeout or owner lifecycle change aborted the release barrier."); return; }
            long tick = PressureTick;
            if (!actor.receivedRelease && state.SubPhase == SubWeaponPhase.Holding &&
                !input.SubHeld && input.SubReleaseSequence != state.SubConsumedRelease)
            {
                _pressureReleases.Add(actor.owner, input); actor.receivedRelease = true;
                actor.releaseInput = input.Sequence; actor.releaseSequence = input.SubReleaseSequence;
                actor.receivedTick = tick; actor.receivedServerTime = player.NetworkManager.ServerTime.Time;
                long stamp = Stopwatch.GetTimestamp();
                if (_report.blastPressure.firstReleaseStamp == 0) _report.blastPressure.firstReleaseStamp = stamp;
                if (_pressureReleases.Count == 8)
                {
                    _report.blastPressure.allReleasesStamp = stamp; _report.blastPressure.targetPhysicsTick = tick + 1;
                    _report.blastPressure.releaseCollectionMs = (stamp - _report.blastPressure.firstReleaseStamp) * 1000d / Stopwatch.Frequency;
                }
            }
            if (_pressureReleases.TryGetValue(actor.owner, out var release) && _report.blastPressure.targetPhysicsTick > 0 && tick >= _report.blastPressure.targetPhysicsTick)
            {
                input.SubHeld = false; input.SubReleaseSequence = release.SubReleaseSequence;
                if (!actor.deliveredRelease)
                {
                    actor.deliveredRelease = true; actor.deliveredTick = tick; actor.deliveredServerTime = player.NetworkManager.ServerTime.Time;
                    _report.blastPressure.deliveredStamp = Stopwatch.GetTimestamp();
                    _report.blastPressure.firstReleaseToDeliveryMs = (_report.blastPressure.deliveredStamp - _report.blastPressure.firstReleaseStamp) * 1000d / Stopwatch.Frequency;
                    _pressureReleased = true; foreach (var other in _pressureControl.actors) _pressureReleased &= other.deliveredRelease;
                }
                return;
            }
            // Delaying the already-received release never creates an edge or spends material.
            input.SubHeld = true; input.SubReleaseSequence = state.SubConsumedRelease;
        }
        void ObservePressureBlast(BlockAttackObservation.Blast observation, int removed, bool completed)
        {
            if (_report.mode != "blast8" || _pressureControl == null || _pressureFailed ||
                observation.Round != _pressureControl.round || observation.Kind != "Sub") return;
            var actor = PressureActor(observation.Owner); if (actor == null || !actor.deliveredRelease) return;
            if (!completed)
            {
                if (actor.exploded) return;
                actor.exploded = true; actor.entity = observation.Entity; actor.explosionTick = observation.PhysicsTick;
            }
            else if (actor.exploded && observation.Entity == actor.entity && !actor.completed)
            { actor.completed = true; actor.removed += removed; }
        }
    }
}
#endif
