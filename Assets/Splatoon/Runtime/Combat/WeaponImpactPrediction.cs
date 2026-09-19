using System;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Visual-only centre trajectory. Never samples gameplay randomness or applies impacts.</summary>
    public static class WeaponImpactPrediction
    {
        public static bool TryPredict(TpsAimSolver solver, TpsAimSolution aim, WeaponRuntimeConfig w,
            PlayerSnapshot state, ulong shooter, out Vector3 point)
        {
            if (WeaponSimulation.IsExplosher(w))
                return ExplosherSimulation.TryPredictLanding(solver, aim, w, state.Grounded,
                    state.PlanarVelocity + Vector3.up * state.VerticalSpeed, state.Yaw, shooter, out point);

            bool bubble = w.MotionMode == ProjectileMotionMode.BouncingBubble;
            point = WeaponSimulation.IsFloatingBubble(w) ? aim.MuzzleHit.Center : aim.MuzzleHit.Point;
            if (aim.MuzzleBlocked && !bubble) return true;

            float charge = WeaponSimulation.IsSplatling(w) && state.SplatlingRemaining > 0
                ? state.SplatlingReleasedCharge
                : state.WeaponPhase == WeaponPhase.Starting || state.WeaponPhase == WeaponPhase.Charging
                    ? WeaponSimulation.ChargeRatio(state, w) : 1;
            float speed = (w.SpeedMin + w.SpeedMax) * .5f;
            if (WeaponSimulation.IsCharge(w)) speed = WeaponSimulation.Speed(w, charge);
            if (WeaponSimulation.IsSplatling(w))
                speed = Mathf.Lerp(w.ChargeMinSpeed, speed, SplatlingSimulation.RangeCharge(w, charge));
            var shot = new InkShot { Origin = aim.MuzzleBlocked ? aim.Pivot : aim.Muzzle,
                Velocity = aim.InitialDirection.normalized * speed, Configuration = w, Charge = charge };
            if (bubble && w.ReferenceRules)
            {
                // Show the next bubble in the committed volley, otherwise its first bubble.
                shot.VolleyIndex = (byte)(BubbleVolleySimulation.Pending(state) ? state.BurstShotIndex : 0);
                shot.Velocity = ReferenceBallistics.BubbleLaunch(aim.InitialDirection, w, shot.VolleyIndex, state.Grounded);
            }
            if (w.ShooterDetails) shot.Velocity = ShooterDetailSimulation.InheritMovement(shot.Velocity, state.PlanarVelocity, state.Yaw, w);
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            var segment = InkBounce.Initial(shot);
            double step = w.ReferenceRules || WeaponSimulation.IsBlaster(w) || DualiesNormalSimulation.Enabled(w)
                ? 1.0 / 60 : 1.0 / GameplayConfig.Global.ProjectileStepRate;
            Vector3 from = shot.Origin;
            float travelled = 0;
            for (double age = 0; age < w.Lifetime - 1e-8;)
            {
                double end = Math.Min(age + step, w.Lifetime);
                Vector3 to = bubble ? segment.PositionAt(end, shot) : InkBallistics.Position(shot, w, end);
                Vector3 delta = to - from;
                float distance = delta.magnitude;
                bool atRange = bubble && travelled + distance >= w.EffectiveRange;
                if (atRange && distance > .000001f)
                {
                    float fraction = Mathf.Clamp01((w.EffectiveRange - travelled) / distance);
                    end = age + (end - age) * fraction;
                    delta *= fraction; distance = delta.magnitude;
                }
                if (FirstContact(solver, shot, from, delta, end, state.Team, shooter, out var hit))
                {
                    if (WeaponSimulation.IsFloatingBubble(w)) TpsAimSolver.UnembedFloatingContact(ref hit, w.CollisionRadius);
                    point = WeaponSimulation.IsFloatingBubble(w) ? hit.Center : hit.Point; return true;
                }
                if (atRange) { point = from + delta; return false; }
                from = to; age = end; travelled += distance;
            }
            point = from;
            // Only weapons with a real expiry explosion get an airborne endpoint marker.
            return !bubble && w.Ammo.HasExplosion;
        }

        static bool FirstContact(TpsAimSolver solver, InkShot shot, Vector3 from, Vector3 delta,
            double age, byte team, ulong shooter, out TpsCollision hit)
        {
            var w = shot.Configuration;
            if (WeaponSimulation.IsFloatingBubble(w))
                return solver.Overlap(from, w.CollisionRadius, shooter, -shot.Velocity.normalized, out hit, null, team, floatingMesh: true) ||
                    solver.ClosestCast(from, delta, delta.magnitude, w.CollisionRadius, shooter, out hit, null, team);
            bool bubble = w.MotionMode == ProjectileMotionMode.BouncingBubble;
            bool blaster = WeaponSimulation.IsBlaster(w);
            bool separate = bubble || blaster || w.ReferenceRules || WeaponSimulation.IsSplatling(w) || DualiesNormalSimulation.Enabled(w);
            if (!separate)
                return solver.Overlap(from, w.CollisionRadius, shooter, -delta.normalized, out hit) ||
                    solver.ClosestCast(from, delta, delta.magnitude, w.CollisionRadius, shooter, out hit);
            float worldRadius = bubble ? ReferenceBallistics.BubbleRadius(shot, age, 0, false) : w.CollisionRadius;
            float playerRadius = bubble ? ReferenceBallistics.BubbleRadius(shot, age, 0, true)
                : blaster ? w.BlasterPlayerRadius : w.ReferenceRules ? w.ReferencePlayerRadius
                : DualiesNormalSimulation.Enabled(w) ? w.DualiesPlayerRadius : w.SplatlingPlayerRadius;
            byte? ignoreTeam = bubble || blaster ? team : (byte?)null;
            bool worldOverlap = solver.Overlap(from, worldRadius, shooter, -delta.normalized, out var world, false);
            bool playerOverlap = solver.Overlap(from, playerRadius, shooter, -delta.normalized, out var player, true, ignoreTeam);
            if (worldOverlap || playerOverlap) { hit = worldOverlap ? world : player; return true; }
            bool worldHit = solver.ClosestCast(from, delta, delta.magnitude, worldRadius, shooter, out world, false);
            bool playerHit = solver.ClosestCast(from, delta, delta.magnitude, playerRadius, shooter, out player, true, ignoreTeam);
            hit = worldHit && (!playerHit || world.Distance <= player.Distance) ? world : player;
            return worldHit || playerHit;
        }
    }
}
