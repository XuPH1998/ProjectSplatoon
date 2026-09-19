using System;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Controlled horizontal shot, 1.4 m muzzle, no spread, flat infinite floor.
    /// Paint is a geometric envelope; ownership depends on the selected alpha shape.</summary>
    public readonly struct WeaponRangeMetrics
    {
        public readonly float Straight, FullDamage, MinimumDamage, Flat, PaintEnvelope;
        public readonly bool Grounded;
        public WeaponRangeMetrics(WeaponRuntimeConfig w)
        {
            float speed = WeaponSimulation.IsSplatling(w) ? (w.SpeedMin + w.SpeedMax) * .5f : w.SpeedMin;
            var shot = new InkShot { Origin = Vector3.up * 1.4f, Velocity = Vector3.forward * speed, Configuration = w, Charge = 1 };
            if (WeaponSimulation.IsBubble(w) && w.ReferenceRules) shot.Velocity = ReferenceBallistics.BubbleLaunch(Vector3.forward, w, 0, true);
            if (WeaponSimulation.IsExplosher(w)) shot.Velocity = ExplosherSimulation.Launch(Vector3.forward, w, true);
            var flat = HeroFlatRange.Calculate(shot, w);
            Flat = flat.Distance; Grounded = flat.HitGround;
            Straight = InkBallistics.Position(shot, w, Math.Min(w.Lifetime, w.StraightSeconds)).z;
            FullDamage = InkBallistics.Position(shot, w, Math.Min(w.Lifetime, w.DamageReduceStartSeconds)).z;
            MinimumDamage = InkBallistics.Position(shot, w, Math.Min(w.Lifetime, w.DamageReduceEndSeconds)).z;
            // Includes rotated atlas-square support (sqrt(2)); this deliberately describes
            // a bound, not measured owned area or a guaranteed connected swimming path.
            float depth = Mathf.Max(1, Mathf.Max(w.PaintDepthMin, w.PaintDepthMax));
            PaintEnvelope = Flat + Mathf.Max(w.PaintRadiusMin, w.PaintRadiusMax) * depth * Mathf.Sqrt(2);
            if (WeaponSimulation.IsFloatingBubble(w))
            {
                float height = InkBallistics.Position(shot, w, flat.FlightSeconds).y;
                float radius = w.Ammo.ExplosionPaintRadiusMax;
                PaintEnvelope = Flat + Mathf.Sqrt(Mathf.Max(0, radius * radius - height * height));
            }
            else if (WeaponSimulation.IsBlaster(w) || WeaponSimulation.IsExplosher(w)) PaintEnvelope = Flat + w.Ammo.ExplosionPaintRadiusMax * Mathf.Sqrt(2);
            else if (w.ReferenceRules && w.ReferenceTrailBudget > 0)
                PaintEnvelope = Mathf.Max(PaintEnvelope, Flat + w.TrailRadiusMax * Mathf.Max(1, w.TrailDepthScale) * Mathf.Sqrt(2));
        }
    }
}
