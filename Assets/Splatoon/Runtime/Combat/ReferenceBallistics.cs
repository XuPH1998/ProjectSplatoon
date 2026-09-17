using System;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>60 Hz reference integration, sampled linearly between reference positions.</summary>
    public static class ReferenceBallistics
    {
        public const double Step = 1.0 / 60;
        public static bool Enabled(WeaponRuntimeConfig w) => w.ReferenceRules;
        public static Vector3 Position(Vector3 origin, Vector3 initial, WeaponRuntimeConfig w, double age)
        { Evaluate(initial, w, age, 0, out var offset, out _); return origin + offset; }
        public static Vector3 Velocity(Vector3 initial, WeaponRuntimeConfig w, double age)
        { Evaluate(initial, w, age, 0, out _, out var velocity); return velocity; }
        public static double AgeAtDistance(WeaponRuntimeConfig w, float speed, float distance)
        {
            double lo = 0, hi = w.Lifetime;
            for (int i = 0; i < 40; i++)
            { double mid = (lo + hi) * .5; if (Position(Vector3.zero, Vector3.forward * speed, w, mid).z < distance) lo = mid; else hi = mid; }
            return (lo + hi) * .5;
        }
        public static void Evaluate(Vector3 initial, WeaponRuntimeConfig w, double elapsed, double startAge, out Vector3 offset, out Vector3 velocity)
        {
            double t = Math.Max(0, elapsed), x = 0, y = 0, z = 0, vx = initial.x, vy = initial.y, vz = initial.z;
            double straight = Math.Max(0, w.StraightSeconds - startAge), dt = Math.Min(t, straight);
            x += vx * dt; y += vy * dt; z += vz * dt; t -= dt;
            if (t > 1e-9)
            {
                if (startAge <= w.StraightSeconds + 1e-9)
                {
                    double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                    double scale = Math.Min(1, w.ReferenceBrakeEndSpeed / Math.Max(.00001, speed));
                    vx *= scale; vy *= scale; vz *= scale;
                }
                double brake = Math.Max(0, w.StraightSeconds + w.BrakeSeconds - Math.Max(startAge, w.StraightSeconds));
                dt = Math.Min(t, brake);
                Advance(ref x, ref y, ref z, ref vx, ref vy, ref vz, dt, w.ReferenceBrakeDrag, w.ReferenceBrakeGravity);
                t -= dt;
                Advance(ref x, ref y, ref z, ref vx, ref vy, ref vz, t, w.ReferenceFreeDrag, w.ProjectileGravity);
            }
            offset = new Vector3((float)x, (float)y, (float)z); velocity = new Vector3((float)vx, (float)vy, (float)vz);
        }
        static void Advance(ref double x, ref double y, ref double z, ref double vx, ref double vy, ref double vz, double time, double drag, double gravity)
        {
            if (time <= 1e-9) return;
            int n = (int)Math.Floor(time / Step + 1e-8); double rest = Math.Max(0, time - n * Step), g = gravity * Step;
            if (drag <= 1e-9)
            {
                x += vx * n * Step; z += vz * n * Step;
                y += (vy * n - g * n * (n + 1) * .5) * Step; vy -= g * n;
            }
            else
            {
                double q = 1 - drag, qn = Math.Pow(q, n), sum = q * (1 - qn) / drag;
                x += vx * sum * Step; z += vz * sum * Step;
                y += (vy * sum - g * (n - sum) / drag) * Step;
                vx *= qn; vz *= qn; vy = vy * qn - g * (1 - qn) / drag;
            }
            if (rest > 1e-9)
            { vx *= 1 - drag; vz *= 1 - drag; vy = vy * (1 - drag) - g; x += vx * rest; y += vy * rest; z += vz * rest; }
        }
        public static float BubbleRadius(InkShot shot, double age, int bounces, bool player)
        {
            var w = shot.Configuration;
            if (!w.ReferenceRules) return w.CollisionRadius;
            int index = shot.VolleyIndex;
            float end = index == 0 ? (player ? w.ReferencePlayerRadius : w.CollisionRadius)
                : (player ? w.BubbleLaterPlayerRadius : w.BubbleLaterFieldRadius) - (index - 1) * w.BubbleRadiusDecrement;
            double grow = player ? w.BubblePlayerGrowSeconds : w.BubbleFieldGrowSeconds;
            return end * Mathf.Lerp(w.BubbleInitialRadiusRate, 1, Mathf.Clamp01((float)(age / grow))) * Mathf.Pow(w.BubbleBounceRadiusRate, bounces);
        }
        public static Vector3 BubbleLaunch(Vector3 direction, WeaponRuntimeConfig w, int index, bool grounded)
        {
            float speed = index == 0 ? (grounded ? w.SpeedMin : w.BubbleAirSpeed)
                : (grounded ? w.BubbleLaterSpeed : w.BubbleLaterAirSpeed) - (index - 1) * w.BubbleSpeedDecrement;
            var v = direction.normalized * speed;
            v.y += new Vector2(v.x, v.z).magnitude * w.BubbleUpwardRate;
            return v;
        }
    }
}
