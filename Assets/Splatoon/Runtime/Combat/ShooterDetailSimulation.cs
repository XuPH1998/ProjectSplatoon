using System;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Explicit project reconstruction of shooter paint scheduling. Not original-game code.</summary>
    public static class ShooterDetailSimulation
    {
        public static Vector3 InheritMovement(Vector3 velocity, Vector3 movement, float yaw, WeaponRuntimeConfig w)
        {
            var forward = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            return velocity + forward * (Vector3.Dot(movement, forward) * w.ShooterMoveForwardRate);
        }
        public static void Schedule(uint shotInHold, WeaponRuntimeConfig w, out int count, out float first, out bool foot)
        {
            // Use the existing per-hold index, not elapsed render time or a client-only accumulator.
            long n = Math.Max(1u, shotInHold);
            double budget = Math.Round(w.ReferenceTrailBudget, 6);
            count = (int)(Math.Floor(n * budget + 1e-8) - Math.Floor((n - 1) * budget + 1e-8));
            int phase = (int)((n - 1) % w.ShooterSplitNum);
            first = w.ReferenceTrailStart + phase * (w.TrailSpacing / w.ShooterSplitNum);
            foot = n % w.ShooterSplitNum == 0;
        }
        public static float ImpactRadius(float distance, WeaponRuntimeConfig w) => distance <= w.PaintDistanceMiddle
            ? Mathf.Lerp(w.ShooterPaintNearRadius, w.PaintRadiusMax, Mathf.InverseLerp(w.ShooterPaintNearDistance, w.PaintDistanceMiddle, distance))
            : Mathf.Lerp(w.PaintRadiusMax, w.PaintRadiusMin, Mathf.InverseLerp(w.PaintDistanceMiddle, w.PaintDistanceFar, distance));
        public static float ImpactDepth(Vector3 velocity, Vector3 normal, float fall, double age, WeaponRuntimeConfig w)
        {
            if (age <= w.StraightSeconds + 1e-8)
            {
                float angle = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(velocity.normalized, normal)))) * Mathf.Rad2Deg;
                return Mathf.Lerp(w.PaintDepthMax, w.PaintDepthMin, Mathf.InverseLerp(w.ShooterPaintAngleMin, w.ShooterPaintAngleMax, angle));
            }
            return Mathf.Lerp(w.PaintDepthBreakMax, w.PaintDepthBreakMin, Mathf.InverseLerp(w.ShooterFallHeightMin, w.ShooterFallHeightMax, fall));
        }
        public static float SplashDepth(float fall, WeaponRuntimeConfig w) => Mathf.Lerp(w.ShooterSplashDepthMax,
            w.ShooterSplashDepthMin, Mathf.InverseLerp(w.ShooterSplashHeightMin, w.ShooterSplashHeightMax, fall));
        public static Vector3 SplashVelocity(Vector3 direction, WeaponRuntimeConfig w, ref uint seed)
        {
            var forward = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            if (forward.sqrMagnitude < .001f) forward = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            return right * ((InkBallistics.Random01(ref seed) * 2 - 1) * w.ShooterSplashSideSpeed) +
                Vector3.up * (InkBallistics.Random01(ref seed) * w.ShooterSplashUpSpeed) +
                forward * Mathf.Lerp(w.ShooterSplashForwardMin, w.ShooterSplashForwardMax, InkBallistics.Random01(ref seed));
        }
        public static void WallTiming(InkShot shot, uint ordinal, out double first, out double last)
        {
            uint seed = Splatoon.Painting.InkShapeAtlas.Hash(shot.Seed ^ ordinal ^ 0x91573u); if (seed == 0) seed = 1;
            var w = shot.Configuration;
            first = w.ShooterWallFirstMin + (w.ShooterWallFirstMax - w.ShooterWallFirstMin) * InkBallistics.Random01(ref seed);
            last = w.ShooterWallLastMin + (w.ShooterWallLastMax - w.ShooterWallLastMin) * InkBallistics.Random01(ref seed);
        }
        public static float WallDistance(double age, double first, double last, WeaponRuntimeConfig w)
        {
            double t = Math.Max(0, age), a = Math.Min(t, first);
            double distance = .5 * w.ShooterWallFirstSpeed * a * a / first; t -= a;
            a = Math.Min(t, w.ShooterWallMiddle);
            distance += w.ShooterWallFirstSpeed * a + .5 * (w.WallDropSpeed - w.ShooterWallFirstSpeed) * a * a / w.ShooterWallMiddle; t -= a;
            a = Math.Min(t, last);
            distance += w.WallDropSpeed * (a - .5 * a * a / last);
            return (float)distance;
        }
        public static double WallAge(float distance, double first, double last, WeaponRuntimeConfig w)
        {
            double low = 0, high = first + w.ShooterWallMiddle + last;
            for (int i = 0; i < 32; i++) { double mid = (low + high) * .5; if (WallDistance(mid, first, last, w) < distance) low = mid; else high = mid; }
            return (low + high) * .5;
        }
    }
}
