#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        enum EnemyAimResult { Rejected, Ready, Deferred }
        const int EnemyAimIterations = 4, EnemyAimMaximumResolves = 1 + 3 * EnemyAimIterations;
        const int EnemyAimFrameResolveBudget = 26, EnemyAimMaximumChords = 16;
        const float EnemyAimDifference = .5f, EnemyAimMaximumAngleStep = 12, EnemyAimResidualTolerance = .02f;
        int _enemyAimBudgetFrame = -1, _enemyAimFrameResolves;
        BlockDynamicBody _enemyAimHintTarget;
        ulong _enemyAimHintId;
        uint _enemyAimHintRound, _enemyAimHintLife, _enemyAimHintHeroRevision;
        Vector2 _enemyAimHintLook;
        Vector3 _enemyAimHintFeet, _enemyAimHintPosition;

        void ResetEnemyAim()
        {
            // The actual Resolve budget is deliberately not reset by a command/replan.
            // Observe and Input, including a replacement command, share one frame quota.
            _enemyAimHintTarget = null;
        }
        bool EnemyAimHasBudget(int resolves)
        {
            if (_enemyAimBudgetFrame != Time.frameCount)
            { _enemyAimBudgetFrame = Time.frameCount; _enemyAimFrameResolves = 0; }
            if (_enemyAimFrameResolves + resolves > EnemyAimFrameResolveBudget)
            { _enemyReport.aimBudgetDeferrals++; return false; }
            return true;
        }
        TpsAimSolution ResolveEnemyLook(PlayerSnapshot state, Vector2 look)
        {
            state.Yaw = look.x; state.Pitch = look.y;
            _enemyReport.aimResolves++; _enemyAimFrameResolves++;
            _enemyReport.aimMaximumResolvesInFrame = Math.Max(_enemyReport.aimMaximumResolvesInFrame, _enemyAimFrameResolves);
            return _enemyAim.Resolve(_enemyHost, state, state.NextMuzzle);
        }
        bool SameEnemyFirstContact(PlayerSnapshot state, BlockDynamicBody target, Vector2 look,
            TpsAimSolution solution, WeaponRuntimeConfig weapon, out WeaponImpactForecast forecast,
            out BlockContact contact, out string reason)
        {
            contact = default; forecast = default; _enemyReport.aimForecasts++;
            if (solution.MuzzleBlocked)
            { forecast.Contact = solution.MuzzleHit; reason = "formal muzzle is blocked"; return false; }
            state.Yaw = look.x; state.Pitch = look.y;
            forecast = weapon.AimMode == WeaponAimMode.WeaponReference
                ? WeaponImpactPrediction.Guide(_enemyAim, solution, weapon, state, _enemyHost.PlayerId)
                : WeaponImpactPrediction.Predict(_enemyAim, solution, weapon, state, _enemyHost.PlayerId);
            var hit = forecast.Contact;
            bool resolved = hit.BlockQuery.IsKnown ? hit.BlockQuery.TryResolveCurrent(out contact) :
                _enemyTerrain.TryContact(hit.Collider, hit.Point, hit.Normal, out contact);
            if (!resolved || contact.Kind != BlockContactKind.Dynamic || contact.DynamicId != target.Id || contact.Owner != target.Owner ||
                !EnemyTargetCurrent(target, target.Id, target.Owner))
            { reason = resolved ? "formal first centre contact is another block" : "formal first centre contact is map, player or absent"; return false; }
            reason = "formal first centre contact resolves to the same current enemy body identity"; return true;
        }
        bool EnemyTrajectoryResidual(PlayerSnapshot state, Vector2 look, TpsAimSolution solution,
            WeaponRuntimeConfig weapon, Vector3 target, Vector3 axis, out Vector2 residual)
        {
            residual = default; _enemyReport.aimTrajectoryEvaluations++;
            if (solution.MuzzleBlocked) return false;
            state.Yaw = look.x; state.Pitch = look.y;
            var shot = WeaponLaunch.Representative(solution, weapon, WeaponLaunch.PreviewCharge(state, weapon), state.PlanarVelocity, state.Yaw);
            double horizon = Math.Min(weapon.ShotGuideSeconds, weapon.Lifetime);
            // This inverse is deliberately scoped to hero 1's short reference guide.
            // Refuse a longer configuration rather than hiding an unbounded inner scan.
            if (horizon <= 0 || horizon > EnemyAimMaximumChords / 60.0 + 1e-8) return false;
            var from = shot.Origin;
            float before = Vector3.Dot(from - target, axis);
            if (!float.IsFinite(before) || before >= 0) return false;
            var right = Vector3.Cross(Vector3.up, axis);
            for (int chord = 1; chord <= EnemyAimMaximumChords; chord++)
            {
                double end = Math.Min(chord / 60.0, horizon);
                var to = InkBallistics.Position(shot, weapon, end); _enemyReport.aimTrajectoryChords++;
                float after = Vector3.Dot(to - target, axis);
                if (!float.IsFinite(after)) return false;
                if (after >= 0)
                {
                    // Match the production fixed-step sweep chord, not a different continuous curve.
                    var crossing = Vector3.Lerp(from, to, -before / (after - before));
                    residual = new Vector2(Vector3.Dot(crossing - target, right), crossing.y - target.y);
                    return float.IsFinite(residual.x) && float.IsFinite(residual.y);
                }
                if (end >= horizon - 1e-8) return false;
                from = to; before = after;
            }
            return false;
        }
        Vector2 EnemySeed(PlayerSnapshot state, BlockDynamicBody target, Vector3 position)
        {
            // A successful angle is only a seed; every call still runs the formal current first-contact gate.
            // This makes a stationary locked target cheap without trusting a cached collision result.
            if (state.HeroId == 1 && ReferenceEquals(target, _enemyAimHintTarget) && target.Id == _enemyAimHintId &&
                _enemyWorld.Round == _enemyAimHintRound && state.Revision == _enemyAimHintLife && state.HeroRevision == _enemyAimHintHeroRevision &&
                (PlayerMotorSimulation.HumanPosition(state) - _enemyAimHintFeet).sqrMagnitude <= .01f &&
                (position - _enemyAimHintPosition).sqrMagnitude <= .09f)
            { _enemyReport.aimHintSeeds++; return _enemyAimHintLook; }
            _enemyReport.aimCameraSeeds++;
            return AimAt(_enemyHost, state, position);
        }
        void RememberEnemyLook(PlayerSnapshot state, BlockDynamicBody target, Vector3 position, Vector2 look)
        {
            _enemyAimHintTarget = target; _enemyAimHintId = target.Id; _enemyAimHintRound = _enemyWorld.Round;
            _enemyAimHintLife = state.Revision; _enemyAimHintHeroRevision = state.HeroRevision;
            _enemyAimHintLook = look; _enemyAimHintFeet = PlayerMotorSimulation.HumanPosition(state); _enemyAimHintPosition = position;
        }
        EnemyAimResult EnemyForecast(PlayerSnapshot state, BlockDynamicBody target, out Vector2 look,
            out WeaponImpactForecast forecast, out BlockContact contact, out string reason)
        {
            look = default; forecast = default; contact = default;
            if (target == null || !EnemyTargetCurrent(target, target.Id, target.Owner))
            { reason = "candidate identity is stale"; return EnemyAimResult.Rejected; }
            var position = target.Body.position;
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            if (Vector3.Distance(PlayerMotorSimulation.HumanPosition(state), position) > weapon.EffectiveRange)
            { reason = "target lies beyond the unchanged weapon effective range"; return EnemyAimResult.Rejected; }
            // Charge actual Resolve work. Locked successful hints need only one fresh formal check,
            // so ordinary catch-up fixed steps do not consume thirteen unused slots per input.
            if (!EnemyAimHasBudget(1))
            { reason = "shared frame aim budget exhausted; retry unchanged candidate and standing pose"; return EnemyAimResult.Deferred; }
            long resolvesBefore = _enemyReport.aimResolves;
            long readyBefore = _enemyReport.aimReady;
            bool deferred = false;
            double started = Time.realtimeSinceStartupAsDouble;
            _enemyReport.aimSolveCalls++;
            _enemyReport.aimLastResidual = default;
            _enemyReport.aimLastTarget = position;
            try
            {
                look = EnemySeed(state, target, position);
                _enemyReport.aimLastSeedLook = look;
                if (!float.IsFinite(look.x) || !float.IsFinite(look.y))
                { reason = "bounded aim seed is non-finite"; return EnemyAimResult.Rejected; }
                var solution = ResolveEnemyLook(state, look);
                bool seedReady = SameEnemyFirstContact(state, target, look, solution, weapon, out forecast, out contact, out reason);
                _enemyReport.aimLastSeedCollider = forecast.Contact.Collider != null ? forecast.Contact.Collider.name : "";
                _enemyReport.aimLastSeedPoint = forecast.Point;
                if (seedReady)
                { RememberEnemyLook(state, target, position, look); _enemyReport.aimReady++; return EnemyAimResult.Ready; }
                // Other weapons retain their original centre-prediction decision.
                if (state.HeroId != 1 || weapon.AimMode != WeaponAimMode.WeaponReference || !weapon.ReferenceRules)
                    return EnemyAimResult.Rejected;
                var axis = Vector3.ProjectOnPlane(position - PlayerMotorSimulation.HumanPosition(state), Vector3.up).normalized;
                if (axis.sqrMagnitude < .5f || !EnemyTrajectoryResidual(state, look, solution, weapon, position, axis, out var residual))
                { reason = "bounded reference inverse has no valid target-depth crossing"; return EnemyAimResult.Rejected; }
                _enemyReport.aimInverseCalls++;
                for (int iteration = 0; iteration < EnemyAimIterations && residual.sqrMagnitude > EnemyAimResidualTolerance * EnemyAimResidualTolerance; iteration++)
                {
                    // Check before either finite difference. Earlier work remains charged even
                    // when this caller defers; it never accepts or remembers a partial inverse.
                    if (!EnemyAimHasBudget(3))
                    {
                        deferred = true;
                        reason = "shared frame aim budget cannot fit the next bounded inverse update; retry unchanged candidate and standing pose";
                        return EnemyAimResult.Deferred;
                    }
                    var yawLook = new Vector2(Mathf.DeltaAngle(0, look.x + EnemyAimDifference), look.y);
                    float pitchDelta = look.y + EnemyAimDifference <= 75 ? EnemyAimDifference : -EnemyAimDifference;
                    var pitchLook = new Vector2(look.x, look.y + pitchDelta);
                    var yawSolution = ResolveEnemyLook(state, yawLook);
                    var pitchSolution = ResolveEnemyLook(state, pitchLook);
                    if (!EnemyTrajectoryResidual(state, yawLook, yawSolution, weapon, position, axis, out var yawResidual) ||
                        !EnemyTrajectoryResidual(state, pitchLook, pitchSolution, weapon, position, axis, out var pitchResidual))
                    { reason = "bounded reference inverse difference has no valid crossing"; return EnemyAimResult.Rejected; }
                    var dx = (yawResidual - residual) / EnemyAimDifference;
                    var dy = (pitchResidual - residual) / pitchDelta;
                    float determinant = dx.x * dy.y - dy.x * dx.y;
                    if (!float.IsFinite(determinant) || Mathf.Abs(determinant) < 1e-8f)
                    { reason = "bounded reference inverse is singular"; return EnemyAimResult.Rejected; }
                    float yawStep = (-residual.x * dy.y + dy.x * residual.y) / determinant;
                    float pitchStep = (-dx.x * residual.y + residual.x * dx.y) / determinant;
                    if (!float.IsFinite(yawStep) || !float.IsFinite(pitchStep))
                    { reason = "bounded reference inverse produced a non-finite correction"; return EnemyAimResult.Rejected; }
                    look = new Vector2(Mathf.DeltaAngle(0, look.x + Mathf.Clamp(yawStep, -EnemyAimMaximumAngleStep, EnemyAimMaximumAngleStep)),
                        Mathf.Clamp(look.y + Mathf.Clamp(pitchStep, -EnemyAimMaximumAngleStep, EnemyAimMaximumAngleStep), -65, 75));
                    solution = ResolveEnemyLook(state, look); _enemyReport.aimIterations++;
                    if (!EnemyTrajectoryResidual(state, look, solution, weapon, position, axis, out residual))
                    { reason = "bounded reference inverse update has no valid crossing"; return EnemyAimResult.Rejected; }
                }
                _enemyReport.aimLastResidual = residual;
                // A small mathematical residual never accepts a hit. All original formal identity/occlusion gates still run.
                if (!SameEnemyFirstContact(state, target, look, solution, weapon, out forecast, out contact, out reason))
                    return EnemyAimResult.Rejected;
                RememberEnemyLook(state, target, position, look); _enemyReport.aimReady++; return EnemyAimResult.Ready;
            }
            finally
            {
                int used = (int)(_enemyReport.aimResolves - resolvesBefore);
                _enemyReport.aimMaximumResolvesPerSolve = Math.Max(_enemyReport.aimMaximumResolvesPerSolve, used);
                _enemyReport.aimMaximumSolveMs = Math.Max(_enemyReport.aimMaximumSolveMs, (Time.realtimeSinceStartupAsDouble - started) * 1000);
                _enemyReport.aimLastLook = look;
                if (!deferred && _enemyReport.aimReady == readyBefore) _enemyReport.aimRejected++;
            }
        }
    }
}
#endif
