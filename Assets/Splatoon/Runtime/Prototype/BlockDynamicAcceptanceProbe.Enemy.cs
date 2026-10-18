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
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        [Serializable] public struct EnemyPlanIdentity
        {
            public string session, status;
            public int command, connectionGeneration;
            public uint round, life, heroRevision, targetRound;
            public ulong owner, targetId;
            public byte team, targetOwner;
        }
        [Serializable] public sealed class EnemyPlanTransition
        {
            public string status, reason, collider;
            public double wallSeconds, serverTime;
            public ulong candidateId, firstContactId;
            public byte firstContactOwner;
            public Vector3 candidateFeet, targetPosition, firstContact;
        }
        [Serializable] public sealed class EnemyPlanReport
        {
            public string scope = "development network enemy setup and stable target plan; centre prediction is a read-only filter, never payment or hit acceptance";
            public string placementScope = "at most one diagnostic Respawn/place per command, after complete read-only preflight; target replans use the actual current player position";
            public EnemyPlanIdentity identity;
            public string status, lastReason;
            public int candidateCount, candidatesExamined, standChecks, placements, replans, inputFrames;
            public int maximumStandChecksPerUpdate = EnemyStandChecksPerUpdate;
            public string aimScope = "development hero1 reference inverse only; original formal first-contact identity gate and normal input/payment are unchanged";
            public int aimIterationLimit = EnemyAimIterations, aimResolveLimitPerSolve = EnemyAimMaximumResolves;
            public int aimResolveBudgetPerFrame = EnemyAimFrameResolveBudget;
            public int aimChordLimitPerEvaluation = EnemyAimMaximumChords;
            public long aimSolveCalls, aimInverseCalls, aimReady, aimRejected, aimBudgetDeferrals, aimResolves, aimForecasts;
            public long aimTrajectoryEvaluations, aimTrajectoryChords, aimIterations, aimCameraSeeds, aimHintSeeds;
            public int aimMaximumResolvesInFrame, aimMaximumResolvesPerSolve;
            public double aimMaximumSolveMs;
            public Vector2 aimLastLook, aimLastResidual, aimLastSeedLook;
            public Vector3 aimLastTarget, aimLastSeedPoint;
            public string aimLastSeedCollider;
            public bool transitionsOverflow, lastInputFire;
            public double commandWallSeconds;
            public Vector3 placedFeet;
            public uint baselineShots, baselineSimulationTick;
            public ulong baselineAction;
            public float baselineInk;
            public long baselineRemoved;
            public List<EnemyPlanTransition> transitions = new();
        }
        sealed partial class Report { public EnemyPlanReport enemyPlan; }
        readonly struct EnemyCandidate
        {
            public readonly ulong Id;
            public readonly BlockDynamicBody Body;
            public readonly byte Owner;
            public readonly bool Rest;
            public readonly float Distance;
            public readonly Vector3 Position;
            public EnemyCandidate(BlockDynamicBody body, Vector3 origin)
            { Id = body.Id; Body = body; Owner = body.Owner; Rest = body.IsAtRest; Position = body.Body.position; Distance = (Position - origin).sqrMagnitude; }
        }
        const int EnemyStandChecksPerUpdate = 2, EnemyTransitionLimit = 256;
        static readonly float[] EnemyStandAngles = { 0, 45, -45, 90, -90, 135, -135, 180 };
        static readonly float[] EnemyStandDistances = { 2.5f, 4f, 6f };
        readonly List<EnemyCandidate> _enemyCandidates = new();
        readonly Collider[] _enemyClearance = new Collider[32];
        readonly TpsAimSolver _enemyAim = new();
        PrototypePlayer _enemyHost;
        BlockTerrain _enemyTerrain;
        BlockDynamicWorld _enemyWorld;
        BlockDynamicBody _enemyTarget;
        EnemyPlanReport _enemyReport;
        int _enemyCandidateAt, _enemyStandAt;
        bool _enemyPlaced, _enemyTerminal, _enemyNeedsCandidates;
        double _enemyNextValidation, _enemyRetryAt;

        // Called for every stage transition; null only clears an existing plan.
        void InitializeEnemyPlan(PrototypeMatch match)
        {
            if (match == null || _stage != "enemy" || _report.mode != "network" || !match.IsServer)
            { ClearEnemyPlan("stage, room or authority changed"); return; }
            var host = PrototypePlayer.Local;
            if (host == null || !host.IsServer || !host.IsSpawned || host.IsTestBot || match.Blocks?.Dynamic == null) return;
            if (_enemyReport != null && _enemyReport.identity.command == _command && ReferenceEquals(host, _enemyHost) &&
                ReferenceEquals(match.Blocks.Dynamic, _enemyWorld) && _enemyReport.identity.round == match.State.Value.Round) return;
            ClearEnemyPlan("replaced by a new enemy command");
            _enemyHost = host; _enemyTerrain = match.Blocks; _enemyWorld = match.Blocks.Dynamic;
            var state = host.Snapshot.Value;
            _enemyReport = new EnemyPlanReport { commandWallSeconds = _stageAt - _boot,
                identity = new EnemyPlanIdentity { session = _report.session, command = _command,
                    connectionGeneration = _report.connectionGenerations, round = match.State.Value.Round,
                    owner = host.PlayerId, life = state.Revision, heroRevision = state.HeroRevision, team = state.Team } };
            _report.enemyPlan = _enemyReport;
            _enemyPlaced = _enemyTerminal = false; _enemyNeedsCandidates = true; _enemyNextValidation = 0;
            EnemyStatus("planning", "new command; awaiting bounded read-only candidate preflight");
        }
        void ClearEnemyPlan(string reason)
        {
            if (_enemyHost != null && _enemyReport != null) EnemyStatus("cleared", reason);
            _enemyHost = null; _enemyTarget = null; _enemyTerrain = null; _enemyWorld = null;
            _enemyCandidates.Clear(); _enemyCandidateAt = _enemyStandAt = 0;
            _enemyTerminal = true; _enemyNeedsCandidates = false; ResetEnemyAim();
        }
        bool EnemyContextCurrent(PrototypeMatch match, out string reason)
        {
            reason = "";
            if (_enemyHost == null || _enemyReport == null || match == null || _stage != "enemy" || _report.mode != "network")
                reason = "no active enemy command";
            else if (!match.IsServer || !_enemyHost.IsServer || !_enemyHost.IsSpawned || !ReferenceEquals(PrototypePlayer.Local, _enemyHost))
                reason = "host disconnected or lost authority";
            else if (_enemyReport.identity.command != _command || _enemyReport.identity.session != _report.session)
                reason = "command or session changed";
            else if (!ReferenceEquals(match.Blocks, _enemyTerrain) || !ReferenceEquals(match.Blocks?.Dynamic, _enemyWorld) ||
                match.State.Value.Round != _enemyReport.identity.round || _enemyWorld.Round != _enemyReport.identity.round)
                reason = "terrain world or round changed";
            else if (match.State.Value.Phase != MatchPhase.Playing) reason = "match is no longer Playing";
            else
            {
                var state = _enemyHost.Snapshot.Value; var identity = _enemyReport.identity;
                if (!state.IsAlive || state.Revision != identity.life || state.HeroRevision != identity.heroRevision || state.Team != identity.team)
                    reason = "host died, respawned, changed hero or changed team";
            }
            return reason.Length == 0;
        }
        bool EnemyTargetCurrent(BlockDynamicBody body, ulong id, byte owner)
            => body != null && body.Body != null && body.Collider != null && body.Collider.enabled &&
                _enemyWorld != null && _enemyWorld.Round == _enemyReport.identity.round && _enemyWorld.Contains(body) &&
                body.Id == id && body.Owner == owner && owner != _enemyReport.identity.team;
        bool EnemyTargetCurrent()
            => _enemyReport != null && EnemyTargetCurrent(_enemyTarget, _enemyReport.identity.targetId, _enemyReport.identity.targetOwner);
        void EnemyStatus(string status, string reason, BlockDynamicBody candidate = null, Vector3 feet = default,
            WeaponImpactForecast forecast = default, BlockContact first = default, ulong candidateId = 0, Vector3 candidatePosition = default)
        {
            if (candidate == null && candidateId == 0 && _enemyReport.status == status && _enemyReport.lastReason == reason) return;
            _enemyReport.status = status; _enemyReport.lastReason = reason;
            var identity = _enemyReport.identity; identity.status = status; _enemyReport.identity = identity;
            if (_enemyReport.transitions.Count >= EnemyTransitionLimit) { _enemyReport.transitionsOverflow = true; return; }
            _enemyReport.transitions.Add(new EnemyPlanTransition { status = status, reason = reason,
                wallSeconds = Time.realtimeSinceStartupAsDouble - _boot,
                serverTime = _enemyHost != null ? _enemyHost.NetworkManager.ServerTime.Time : 0,
                candidateId = candidateId != 0 ? candidateId : candidate != null ? candidate.Id : identity.targetId, candidateFeet = feet,
                targetPosition = candidateId != 0 ? candidatePosition : candidate != null && candidate.Body != null ? candidate.Body.position : default,
                firstContactId = first.DynamicId, firstContactOwner = first.Owner, firstContact = forecast.Point,
                collider = forecast.Contact.Collider != null ? forecast.Contact.Collider.name : "" });
        }
        void EnemyCandidates()
        {
            _enemyCandidates.Clear(); _enemyCandidateAt = _enemyStandAt = 0; _enemyNeedsCandidates = false;
            var state = _enemyHost.Snapshot.Value;
            foreach (var body in _enemyWorld.Bodies.Values)
                if (body.Owner != state.Team && body.Body != null) _enemyCandidates.Add(new EnemyCandidate(body, PlayerMotorSimulation.HumanPosition(state)));
            _enemyCandidates.Sort((a, b) => {
                int compare = b.Rest.CompareTo(a.Rest);
                if (compare == 0) compare = a.Distance.CompareTo(b.Distance);
                return compare != 0 ? compare : a.Id.CompareTo(b.Id);
            });
            _enemyReport.candidateCount = _enemyCandidates.Count;
            if (_enemyCandidates.Count == 0) EnemyStatus("exhausted", "the complete candidate snapshot contains no enemy bodies");
        }
        void EnemyReplan(string reason)
        {
            var identity = _enemyReport.identity; identity.targetId = 0; identity.targetOwner = 0; identity.targetRound = 0;
            _enemyReport.identity = identity; _enemyTarget = null;
            _enemyNeedsCandidates = true; _enemyReport.replans++; ResetEnemyAim();
            EnemyStatus("planning", reason);
        }
        void ObserveEnemyPlan(PrototypeMatch match)
        {
            if (_enemyHost == null) return;
            if (!EnemyContextCurrent(match, out string reason))
            { EnemyStatus("invalidated", reason); ClearEnemyPlan(reason); return; }
            if (_enemyTerminal) return;
            double now = Time.realtimeSinceStartupAsDouble;
            int used = 0;
            if (_enemyReport.identity.targetId != 0)
            {
                if (!EnemyTargetCurrent()) EnemyReplan("locked target identity was removed, recycled or changed owner");
                else
                {
                    var state = _enemyHost.Snapshot.Value;
                    if (_enemyReport.status == "placed-awaiting-step" && state.SimulationTick <= _enemyReport.baselineSimulationTick) return;
                    if (now < _enemyNextValidation) return;
                    var feet = PlayerMotorSimulation.HumanPosition(state);
                    if (!HeroBodyShape.For(state.HeroId).Fits(feet, _enemyClearance, PlayerMotorSimulation.WorldMask))
                    { used++; _enemyReport.standChecks++; EnemyReplan("actual current full standing capsule is blocked; no gate was changed"); }
                    else
                    {
                        var aim = EnemyForecast(state, _enemyTarget, out _, out var forecast, out var contact, out reason);
                        if (aim == EnemyAimResult.Deferred) return;
                        _enemyNextValidation = now + .1; used++; _enemyReport.standChecks++;
                        if (aim == EnemyAimResult.Rejected)
                        { EnemyReplan("actual current centre trajectory became unreachable: " + reason); }
                        else
                        {
                            if (_enemyReport.status != "ready") EnemyStatus("ready", "normal authority step advanced; actual pose retains full clearance and the same first centre contact", _enemyTarget, feet, forecast, contact);
                            return;
                        }
                    }
                }
            }
            if (!_enemyNeedsCandidates && _enemyCandidateAt >= _enemyCandidates.Count)
            {
                if (now < _enemyRetryAt) return;
                _enemyNeedsCandidates = true;
            }
            if (_enemyNeedsCandidates) { EnemyCandidates(); _enemyRetryAt = now + .5; }
            for (int work = used; work < EnemyStandChecksPerUpdate && _enemyCandidateAt < _enemyCandidates.Count; work++)
            {
                var candidate = _enemyCandidates[_enemyCandidateAt];
                if (!EnemyTargetCurrent(candidate.Body, candidate.Id, candidate.Owner))
                { _enemyReport.candidatesExamined++; EnemyStatus("rejected", "candidate identity is stale", candidateId: candidate.Id, candidatePosition: candidate.Position); _enemyCandidateAt++; _enemyStandAt = 0; continue; }
                var state = _enemyHost.Snapshot.Value;
                int stands = _enemyPlaced ? 1 : EnemyStandAngles.Length * EnemyStandDistances.Length;
                var aim = EnemyStand(state, candidate.Body, _enemyStandAt, out var feet, out var look,
                    out var forecast, out var contact, out reason);
                // A frame quota is not a geometry rejection. Keep both cursors and retry next frame.
                if (aim == EnemyAimResult.Deferred) return;
                _enemyStandAt++; _enemyReport.standChecks++;
                if (aim == EnemyAimResult.Ready)
                {
                    _enemyReport.candidatesExamined++;
                    _enemyTarget = candidate.Body;
                    var identity = _enemyReport.identity;
                    identity.targetId = candidate.Id; identity.targetOwner = candidate.Owner; identity.targetRound = _enemyWorld.Round;
                    _enemyReport.identity = identity;
                    if (!_enemyPlaced)
                    {
                        _enemyPlaced = true; _enemyReport.placements++;
                        try { _enemyHost.DiagnosticPlace(feet, look.x); }
                        catch (Exception exception)
                        { _enemyTerminal = true; EnemyStatus("failed", "the single diagnostic placement failed: " + exception.Message, candidate.Body, feet); Error(_enemyReport.lastReason); return; }
                        var actual = _enemyHost.Snapshot.Value;
                        identity.life = actual.Revision; identity.heroRevision = actual.HeroRevision; _enemyReport.identity = identity;
                        _enemyReport.placedFeet = PlayerMotorSimulation.HumanPosition(actual);
                        _enemyReport.baselineShots = actual.ShotSequence; _enemyReport.baselineAction = actual.ShotActionId;
                        _enemyReport.baselineInk = actual.Ink; _enemyReport.baselineRemoved = _enemyWorld.RemovedCount;
                        _enemyReport.baselineSimulationTick = actual.SimulationTick;
                        _previousActors[(_enemyWorld.Round, _enemyHost.PlayerId)] = (actual.ShotSequence, actual.Ink);
                        EnemyStatus("placed-awaiting-step", "one diagnostic placement completed; actual life and payment baseline were rebuilt", candidate.Body, _enemyReport.placedFeet, forecast, contact);
                    }
                    else EnemyStatus("ready", "replan is reachable from the unchanged actual current player pose", candidate.Body, feet, forecast, contact);
                    _enemyNextValidation = 0; return;
                }
                EnemyStatus("rejected", reason, candidate.Body, feet, forecast, contact);
                if (_enemyStandAt >= stands) { _enemyCandidateAt++; _enemyStandAt = 0; _enemyReport.candidatesExamined++; }
            }
            if (_enemyCandidateAt >= _enemyCandidates.Count && _enemyCandidates.Count > 0)
            {
                _enemyRetryAt = now + .5;
                EnemyStatus("exhausted", "the complete candidate snapshot has no legal standing and same-target centre trajectory; the original external deadline remains unchanged");
            }
        }
        EnemyAimResult EnemyStand(PlayerSnapshot state, BlockDynamicBody target, int stand, out Vector3 feet, out Vector2 look,
            out WeaponImpactForecast forecast, out BlockContact contact, out string reason)
        {
            feet = PlayerMotorSimulation.HumanPosition(state); look = default; forecast = default; contact = default; reason = "";
            if (!_enemyTerrain.World.InBounds(BlockWorld.CellAt(target.Body.position)))
            { reason = "candidate target lies outside the current block world bounds"; return EnemyAimResult.Rejected; }
            if (!_enemyPlaced)
            {
                Vector3 away = Vector3.ProjectOnPlane(feet - target.Body.position, Vector3.up).normalized;
                if (away.sqrMagnitude < .5f) away = Vector3.back;
                Vector3 offset = Quaternion.Euler(0, EnemyStandAngles[stand % EnemyStandAngles.Length], 0) * away * EnemyStandDistances[stand / EnemyStandAngles.Length];
                Vector3 origin = target.Body.position + offset + Vector3.up * 3;
                int mapMask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer);
                if (!Physics.Raycast(origin, Vector3.down, out var floor, 10, mapMask, QueryTriggerInteraction.Ignore))
                { reason = "no original map floor at candidate"; return EnemyAimResult.Rejected; }
                if (!_enemyTerrain.IsAuthoredMapCollider(floor.collider) || floor.normal.y < .7f)
                { reason = "candidate floor is not a walkable authored map surface"; return EnemyAimResult.Rejected; }
                feet = floor.point + Vector3.up * .02f;
            }
            var shape = HeroBodyShape.For(state.HeroId); var dimensions = _enemyTerrain.World.Dimensions;
            if (!float.IsFinite(feet.x) || !float.IsFinite(feet.y) || !float.IsFinite(feet.z) ||
                Mathf.Abs(feet.x) + shape.Radius > dimensions.x * .5f || Mathf.Abs(feet.z) + shape.Radius > dimensions.y * .5f)
            { reason = "candidate standing capsule lies outside map horizontal bounds"; return EnemyAimResult.Rejected; }
            if (!shape.Fits(feet, _enemyClearance, PlayerMotorSimulation.WorldMask))
            { reason = "candidate full standing capsule is blocked, including free bodies"; return EnemyAimResult.Rejected; }
            var shadow = state;
            if (!_enemyPlaced)
            {
                // Geometry of the already-authorized diagnostic Respawn, on a copy only.
                // Actual player/weapon state is read again after that single placement.
                shadow.Position = feet; shadow.AirHumanOffset = shadow.CameraRebaseOffset = 0;
                shadow.Swimming = shadow.CompactBody = false; shadow.Grounded = true;
                shadow.PlanarVelocity = shadow.Velocity = Vector3.zero; shadow.NextMuzzle = 0;
                shadow.WeaponPhase = WeaponPhase.Idle; shadow.SplatlingRemaining = shadow.BurstRemaining = 0;
            }
            return EnemyForecast(shadow, target, out look, out forecast, out contact, out reason);
        }
        void EnemyInput(PrototypePlayer player, PlayerSnapshot state, ref PlayerInputFrame input)
        {
            // Fire=false does not cancel an already committed Starting shot. On any early return,
            // preserve the authority's accepted look instead of Input's generic downward workload look.
            input.Look = new Vector2(state.Yaw, state.Pitch);
            input.Fire = false;
            if (!ReferenceEquals(player, _enemyHost) || _enemyTerminal || _enemyReport?.status != "ready" ||
                !EnemyContextCurrent(_match, out _)) return;
            _enemyReport.lastInputFire = false;
            if (!EnemyTargetCurrent()) { EnemyReplan("normal input stopped because the locked target identity became stale"); return; }
            var aim = EnemyForecast(state, _enemyTarget, out var look, out _, out _, out var reason);
            if (aim == EnemyAimResult.Deferred) return;
            if (aim == EnemyAimResult.Rejected)
            { EnemyReplan("normal input stopped because the shared current trajectory is unreachable: " + reason); return; }
            input.Look = look;
            input.Fire = state.IsAlive && !state.AttackNeedsRelease;
            _enemyReport.inputFrames++; _enemyReport.lastInputFire = input.Fire;
            // No CanStand/WeaponSimulation/payment call here. Normal authority consumes this intent.
        }
        public static bool TryEnemyPlanTrace(PrototypePlayer player, out EnemyPlanIdentity identity)
        {
            identity = default;
            var probe = _instance;
            if (probe == null || !ReferenceEquals(player, probe._enemyHost) || !probe.EnemyContextCurrent(probe._match, out _)) return false;
            identity = probe._enemyReport.identity;
            if (identity.targetId != 0 && !probe.EnemyTargetCurrent())
            { identity.targetId = 0; identity.targetOwner = 0; identity.targetRound = 0; identity.status = "target-invalid-awaiting-replan"; }
            return true;
        }
    }
}
#endif
