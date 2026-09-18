using System;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Explosher launch and growing hit volumes. Flight uses the common 60 Hz reference integrator.</summary>
    public static class ExplosherSimulation
    {
        public static Vector3 Launch(Vector3 direction, WeaponRuntimeConfig w, bool grounded,
            Vector3 playerVelocity = default, float yaw = 0)
        {
            var velocity = direction.normalized * (grounded ? w.SpeedMin : w.ExplosherAirSpeed);
            velocity.y += new Vector2(velocity.x, velocity.z).magnitude * w.ExplosherUpwardRate;
            var rotation = Quaternion.Euler(0, yaw, 0);
            var local = Quaternion.Inverse(rotation) * playerVelocity;
            return velocity + rotation * new Vector3(local.x * w.ExplosherMoveSideRate,
                (grounded ? 0 : local.y) * w.ExplosherMoveVerticalRate, local.z * w.ExplosherMoveForwardRate);
        }

        public static float Radius(WeaponRuntimeConfig w, double age, bool player)
            => Mathf.Lerp(player ? w.ExplosherPlayerInitialRadius : w.ExplosherFieldInitialRadius,
                player ? w.ReferencePlayerRadius : w.CollisionRadius,
                Mathf.Clamp01((float)(age / (player ? w.ExplosherPlayerGrowSeconds : w.ExplosherFieldGrowSeconds))));

        public static float PaintRadius(InkShot shot, Vector3 position)
        {
            var w = shot.Configuration;
            float horizontal = new Vector2(position.x - shot.Origin.x, position.z - shot.Origin.z).magnitude;
            return Mathf.Lerp(w.Ammo.ExplosionPaintRadiusMax, w.Ammo.ExplosionPaintRadiusMin,
                Mathf.InverseLerp(w.ExplosherPaintNearDistance, w.ExplosherPaintFarDistance, horizontal));
        }

        public static Vector3 PredictLanding(TpsAimSolver solver, TpsAimSolution aim, WeaponRuntimeConfig w,
            bool grounded, Vector3 playerVelocity, float yaw, ulong shooter)
        {
            TryPredictLanding(solver, aim, w, grounded, playerVelocity, yaw, shooter, out var point);
            return point;
        }

        // Expiry has no explosion. Keep its endpoint available to existing trajectory callers,
        // but only show the landing marker when the sweep actually finds a surface.
        public static bool TryPredictLanding(TpsAimSolver solver, TpsAimSolution aim, WeaponRuntimeConfig w,
            bool grounded, Vector3 playerVelocity, float yaw, ulong shooter, out Vector3 point)
        {
            if (aim.MuzzleBlocked) { point = aim.MuzzleHit.Point; return true; }
            var shot = new InkShot { Origin = aim.Muzzle, Velocity = Launch(aim.InitialDirection, w, grounded, playerVelocity, yaw), Configuration = w };
            var from = shot.Origin;
            for (double t = 1.0 / 60; t <= w.Lifetime + 1e-8; t += 1.0 / 60)
            {
                var to = InkBallistics.Position(shot, w, t); var delta = to - from;
                float radius = Radius(w, t, false);
                if (solver.Overlap(from, radius, shooter, -delta.normalized, out var hit, false) ||
                    solver.ClosestCast(from, delta, delta.magnitude, radius, shooter, out hit, false))
                { point = hit.Point; return true; }
                from = to;
            }
            point = from;
            return false;
        }
    }

    public sealed partial class InkProjectileService
    {
        bool TraceExplosher(ref Active active, Vector3 from, Vector3 to, double start, double end)
        {
            var shot = active.Shot;
            var w = shot.Configuration;
            Vector3 delta = to - from;
            float length = delta.magnitude;
            // Conservative continuous sweep of each growing sphere over one reference step.
            float fieldRadius = ExplosherSimulation.Radius(w, end - shot.Born, false);
            float playerRadius = ExplosherSimulation.Radius(w, end - shot.Born, true);
            bool worldHit = _aim.Overlap(from, fieldRadius, shot.Shooter, -delta.normalized, out var world, false);
            if (worldHit) world.Distance = 0;
            else worldHit = _aim.ClosestCast(from, delta, length, fieldRadius, shot.Shooter, out world, false);

            // Re-query after each hit with the victim excluded. This includes overlapping
            // players and thin paper geometry without advancing past another victim or wall.
            while (true)
            {
                bool playerHit = _aim.Overlap(from, playerRadius, shot.Shooter, -delta.normalized,
                    out var contact, true, shot.Team, active.Pierced);
                if (playerHit) contact.Distance = 0;
                else playerHit = _aim.ClosestCast(from, delta, length, playerRadius, shot.Shooter,
                    out contact, true, shot.Team, active.Pierced);
                if (!playerHit || (worldHit && world.Distance <= contact.Distance + .00001f)) break;
                var victim = contact.Collider.GetComponentInParent<PrototypePlayer>();
                if (victim == null || !active.Pierced.Add((victim.PlayerId, victim.Snapshot.Value.Revision))) break;
                double time = start + (end - start) * contact.Distance / Mathf.Max(.0001f, length);
                float before = victim.Snapshot.Value.Health;
                victim.ReceiveDamage(shot.Team, w.Damage, InkBallistics.Velocity(shot, w, time - shot.Born), shot.Shooter);
                float damage = before - victim.Snapshot.Value.Health;
                if (damage > 0)
                    Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Time = time, Team = shot.Team,
                        Position = contact.Point, Normal = contact.Normal, Hit = true, ContinuesProjectile = true,
                        Shooter = shot.Shooter, Victim = victim.PlayerId, Damage = damage, Killed = victim.Snapshot.Value.Health <= 0,
                        ActionId = shot.ActionId, Lifecycle = shot.Lifecycle, HeroRevision = shot.HeroRevision, PelletIndex = shot.PelletIndex });
            }
            if (worldHit)
            {
                double time = start + (end - start) * world.Distance / Mathf.Max(.0001f, length);
                PaintReferenceTrail(ref active, from, from + delta.normalized * world.Distance, start, time);
                Resolve(shot, world.Collider, world.Point, world.Normal, time - shot.Born, ref active.PaintOrdinal);
                return true;
            }
            PaintReferenceTrail(ref active, from, to, start, end);
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, end - shot.Born, to);
#endif
            return false;
        }
    }
}
