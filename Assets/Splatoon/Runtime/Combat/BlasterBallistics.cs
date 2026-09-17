using System;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>60 Hz reference steps shared by authority, presentation and measurements.</summary>
    public static class BlasterBallistics
    {
        const double StepSeconds = 1.0 / 60;
        public static Vector3 Position(Vector3 origin, Vector3 velocity, WeaponRuntimeConfig w, double age)
        {
            Evaluate(velocity, w, age, out var offset, out _);
            return origin + offset;
        }
        public static Vector3 Velocity(Vector3 velocity, WeaponRuntimeConfig w, double age)
        { Evaluate(velocity, w, age, out _, out var result); return result; }

        static void Evaluate(Vector3 initial, WeaponRuntimeConfig w, double age, out Vector3 offset, out Vector3 velocity)
        {
            double t = Math.Clamp(age, 0, w.Lifetime);
            offset = initial * (float)Math.Min(t, w.StraightSeconds);
            velocity = initial;
            t -= w.StraightSeconds;
            if (t <= 1e-9) return;
            velocity = Vector3.ClampMagnitude(initial, w.BlasterBrakeEndSpeed);
            // Parameters are converted to metres/seconds; drag remains per reference frame.
            while (t > 1e-9)
            {
                velocity = velocity * (1 - w.BlasterBrakeDrag) + Vector3.down * (w.BlasterBrakeGravity / 60);
                double dt = Math.Min(t, StepSeconds);
                offset += velocity * (float)dt;
                t -= dt;
            }
        }
    }
}
