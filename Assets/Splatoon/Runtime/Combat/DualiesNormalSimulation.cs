using System;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Normal Glooga-style shots only. Bias sampling is an explicit project approximation.</summary>
    public static class DualiesNormalSimulation
    {
        public static bool Enabled(WeaponRuntimeConfig w) => w.MotionMode == ProjectileMotionMode.DualiesNormal;

        public static void Reset(ref PlayerSnapshot s, WeaponRuntimeConfig w)
        {
            s.DualiesGroundBias = w.DualiesSpreadMinBias;
            s.DualiesJumpAge = 0;
            s.DualiesWasGrounded = s.Grounded;
            s.LastShotSpreadBias = 0;
            s.LastShotSpread = s.LastShotVerticalSpread = 0;
            Refresh(ref s, w);
        }

        public static void Before(ref PlayerSnapshot s, WeaponRuntimeConfig w, double now, float dt)
        {
            if (s.DualiesGroundBias <= 0) Reset(ref s, w);
            s.DualiesJumpAge = s.Grounded || s.DualiesWasGrounded ? 0 : s.DualiesJumpAge + dt;
            s.DualiesWasGrounded = s.Grounded;
            // Recover only after the last shot's ordinary firing interval has elapsed.
            if (!s.SpreadFiring)
            {
                float recovery = (float)Math.Max(0, now - Math.Max(now - dt, s.NextShotAt));
                s.DualiesGroundBias -= recovery * w.DualiesSpreadRecoverPerSecond;
            }
            s.DualiesGroundBias = Mathf.Clamp(s.DualiesGroundBias, w.DualiesSpreadMinBias, w.DualiesSpreadMaxBias);
            Refresh(ref s, w);
        }

        public static float Bias(PlayerSnapshot s, WeaponRuntimeConfig w)
        {
            float ground = Mathf.Clamp(s.DualiesGroundBias, w.DualiesSpreadMinBias, w.DualiesSpreadMaxBias);
            if (s.Grounded) return ground;
            float recovery = Mathf.InverseLerp((float)w.DualiesJumpRecoverStartSeconds,
                (float)w.DualiesJumpRecoverEndSeconds, (float)s.DualiesJumpAge);
            return Mathf.Lerp(w.DualiesJumpBias, ground, recovery);
        }

        public static void After(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, bool emitted, bool canShoot)
        {
            if (emitted)
            {
                s.LastShotSpread = s.CurrentSpread;
                s.LastShotVerticalSpread = s.CurrentVerticalSpread;
                s.LastShotSpreadBias = Bias(s, w);
                s.DualiesGroundBias = Mathf.Min(w.DualiesSpreadMaxBias, s.DualiesGroundBias + w.DualiesSpreadPerShot);
            }
            s.SpreadFiring = canShoot && s.Health > 0 && input.Fire && !input.CancelFire && !s.AttackNeedsRelease &&
                s.Ink + .00001f >= w.ShotInk && (emitted || s.SpreadFiring);
            Refresh(ref s, w);
        }

        public static void Refresh(ref PlayerSnapshot s, WeaponRuntimeConfig w)
        {
            s.CurrentSpread = s.CurrentVerticalSpread = s.Grounded ? w.SpreadDegrees : w.JumpSpreadDegrees;
            s.SpreadProgress = Mathf.InverseLerp(w.DualiesSpreadMinBias, w.DualiesSpreadMaxBias, s.DualiesGroundBias);
        }

        public static Vector3 LaunchVelocity(Vector3 direction, WeaponRuntimeConfig w, float spread, float bias, ref uint seed)
        {
            // The median radial tangent is bias * tan(envelope). Unlike zero-spread
            // first shots, this retains a small nonzero distribution from the first round.
            float exponent = Mathf.Log(Mathf.Clamp(bias, .00001f, .99999f)) / Mathf.Log(.5f);
            float radius = Mathf.Pow(InkBallistics.Random01(ref seed), exponent) * Mathf.Tan(spread * Mathf.Deg2Rad);
            float angle = InkBallistics.Random01(ref seed) * Mathf.PI * 2;
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized;
            return Quaternion.LookRotation(direction) * local * Mathf.Lerp(w.SpeedMin, w.SpeedMax, InkBallistics.Random01(ref seed));
        }
    }

    /// <summary>Allocation-free 60 Hz reference stepping, linearly sampled between reference positions.</summary>
    public static class DualiesBallistics
    {
        const double Step = 1.0 / 60;
        public static Vector3 Position(Vector3 origin, Vector3 initial, WeaponRuntimeConfig w, double age)
        { Evaluate(initial, w, age, out var offset, out _); return origin + offset; }
        public static Vector3 Velocity(Vector3 initial, WeaponRuntimeConfig w, double age)
        { Evaluate(initial, w, age, out _, out var velocity); return velocity; }
        public static float Distance(WeaponRuntimeConfig w, float speed, double age)
            => Position(Vector3.zero, Vector3.forward * speed, w, age).z;
        public static double AgeAtDistance(WeaponRuntimeConfig w, float speed, float distance)
        {
            double lo = 0, hi = w.Lifetime;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) * .5;
                if (Distance(w, speed, mid) < distance) lo = mid; else hi = mid;
            }
            return (lo + hi) * .5;
        }
        static void Evaluate(Vector3 initial, WeaponRuntimeConfig w, double age, out Vector3 offset, out Vector3 velocity)
        {
            double time = Math.Clamp(age, 0, w.Lifetime);
            offset = initial * (float)Math.Min(time, w.StraightSeconds);
            velocity = initial;
            time -= w.StraightSeconds;
            if (time <= 1e-9) return;
            velocity = Vector3.ClampMagnitude(initial, w.DualiesBrakeEndSpeed);
            double brake = Math.Min(time, w.BrakeSeconds);
            while (brake > 1e-9)
            {
                velocity = velocity * (1 - w.DualiesBrakeDrag) + Vector3.down * (w.DualiesBrakeGravity / 60);
                double dt = Math.Min(brake, Step);
                offset += velocity * (float)dt;
                brake -= dt;
            }
            time -= w.BrakeSeconds;
            if (time <= 1e-9) return;
            // Sum complete free-flight frames analytically; no lifetime-sized loops per particle.
            int frames = (int)Math.Floor(time / Step + 1e-8);
            double rest = Math.Max(0, time - frames * Step);
            var gravityStep = Vector3.down * (w.ProjectileGravity / 60);
            offset += velocity * (float)(frames * Step) + gravityStep * (float)(Step * frames * (frames + 1) * .5);
            velocity += gravityStep * frames;
            if (rest > 1e-9) { velocity += gravityStep; offset += velocity * (float)rest; }
        }
    }
}
