using System;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public enum PredictedImpactKind { None, World, Player, Expiry }
    public struct WeaponImpactForecast
    {
        public PredictedImpactKind Kind;
        public Vector3 Point, EnemyPoint;
        public ulong EnemyId;
        public bool HasEnemyContact;
        public bool HasImpact => Kind != PredictedImpactKind.None;
    }

    /// <summary>Visual-only centre trajectory. Never samples gameplay randomness or applies impacts.</summary>
    public static class WeaponImpactPrediction
    {
        public static bool TryPredict(TpsAimSolver solver, TpsAimSolution aim, WeaponRuntimeConfig w,
            PlayerSnapshot state, ulong shooter, out Vector3 point)
        {
            var forecast = Predict(solver, aim, w, state, shooter);
            point = forecast.Point;
            return forecast.HasImpact;
        }

        public static WeaponImpactForecast Predict(TpsAimSolver solver, TpsAimSolution aim, WeaponRuntimeConfig w,
            PlayerSnapshot state, ulong shooter)
        {
            if (WeaponSimulation.IsExplosher(w)) return PredictExplosher(solver, aim, w, state, shooter);
            var result = new WeaponImpactForecast();
            bool bubble = w.MotionMode == ProjectileMotionMode.BouncingBubble;
            if (aim.MuzzleBlocked && !bubble)
            {
                SetContact(ref result, aim.MuzzleHit, state.Team, WeaponSimulation.IsFloatingBubble(w));
                return result;
            }

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
                    SetContact(ref result, hit, state.Team, WeaponSimulation.IsFloatingBubble(w));
                    return result;
                }
                if (atRange) { result.Point = from + delta; return result; }
                from = to; age = end; travelled += distance;
            }
            result.Point = from;
            // An expiry explosion is a location hint, never a predicted enemy hit.
            if (!bubble && w.Ammo.HasExplosion) result.Kind = PredictedImpactKind.Expiry;
            return result;
        }

        static void SetContact(ref WeaponImpactForecast result, TpsCollision hit, byte team, bool centre = false)
        {
            result.Point = centre ? hit.Center : hit.Point;
            var player = hit.Collider != null ? hit.Collider.GetComponentInParent<PrototypePlayer>() : null;
            result.Kind = player != null ? PredictedImpactKind.Player : PredictedImpactKind.World;
            if (player != null && player.PresentedState.Health > 0 && player.PresentedState.Team != team)
            {
                result.HasEnemyContact = true; result.EnemyId = player.PlayerId; result.EnemyPoint = hit.Point;
            }
        }

        static WeaponImpactForecast PredictExplosher(TpsAimSolver solver, TpsAimSolution aim, WeaponRuntimeConfig w,
            PlayerSnapshot state, ulong shooter)
        {
            var result = new WeaponImpactForecast();
            if (aim.MuzzleBlocked) { SetContact(ref result, aim.MuzzleHit, state.Team); return result; }
            var shot = new InkShot { Origin = aim.Muzzle, Configuration = w,
                Velocity = ExplosherSimulation.Launch(aim.InitialDirection, w, state.Grounded,
                    state.PlanarVelocity + Vector3.up * state.VerticalSpeed, state.Yaw) };
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            var from = shot.Origin;
            for (double age = 0; age < w.Lifetime - 1e-8;)
            {
                double end = Math.Min(age + 1.0 / 60, w.Lifetime);
                var to = InkBallistics.Position(shot, w, end);
                var delta = to - from;
                float length = delta.magnitude;
                // Match TraceExplosher's growing sweep and world-before-player occlusion rule.
                bool worldHit = solver.Overlap(from, ExplosherSimulation.Radius(w, end, false), shooter,
                    -delta.normalized, out var world, false);
                if (worldHit) world.Distance = 0;
                else worldHit = solver.ClosestCast(from, delta, length, ExplosherSimulation.Radius(w, end, false),
                    shooter, out world, false);
                if (!result.HasEnemyContact)
                {
                    bool playerHit = solver.Overlap(from, ExplosherSimulation.Radius(w, end, true), shooter,
                        -delta.normalized, out var player, true, state.Team);
                    if (playerHit) player.Distance = 0;
                    else playerHit = solver.ClosestCast(from, delta, length, ExplosherSimulation.Radius(w, end, true),
                        shooter, out player, true, state.Team);
                    if (playerHit && (!worldHit || player.Distance < world.Distance - .00001f))
                        SetContact(ref result, player, state.Team);
                }
                if (worldHit)
                {
                    result.Point = world.Point; result.Kind = PredictedImpactKind.World;
                    return result;
                }
                from = to; age = end;
            }
            // Direct enemy contact survives, but a piercing shot has no expiry explosion/landing.
            result.Point = from; result.Kind = PredictedImpactKind.None;
            return result;
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
