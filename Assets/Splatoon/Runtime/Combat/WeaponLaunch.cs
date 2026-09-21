using System;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Shared logical launch. Distribution and guide geometry are project reconstructions.</summary>
    public static class WeaponLaunch
    {
        public const uint SpreadStream = 0xC43F127Du, SpeedStream = 0x8A91E5B3u;
        public const uint DropStream = 0x54BD62C9u, FootOrdinal = 0x40000000u;
        public static uint Stream(uint seed, uint purpose, uint ordinal = 0)
        {
            uint result = InkShapeAtlas.Hash(seed ^ purpose ^ unchecked(ordinal * 2654435761u));
            return result == 0 ? 1 : result;
        }

        public static float PreviewCharge(PlayerSnapshot state, WeaponRuntimeConfig w) =>
            WeaponSimulation.IsSplatling(w) && state.SplatlingRemaining > 0 ? state.SplatlingReleasedCharge :
            state.WeaponPhase == WeaponPhase.Starting || state.WeaponPhase == WeaponPhase.Charging
                ? WeaponSimulation.ChargeRatio(state, w) : 1;

        public static float CenterSpeed(WeaponRuntimeConfig w, float charge)
        {
            if (WeaponSimulation.IsCharge(w)) return WeaponSimulation.Speed(w, charge);
            float speed = (w.SpeedMin + w.SpeedMax) * .5f;
            return WeaponSimulation.IsSplatling(w)
                ? Mathf.Lerp(w.ChargeMinSpeed, speed, SplatlingSimulation.RangeCharge(w, charge)) : speed;
        }

        public static TpsAimSolution Geometry(Vector3 camera, Vector3 forward, Vector3 muzzle, WeaponRuntimeConfig w, float charge)
        {
            forward = forward.sqrMagnitude > .000001f ? forward.normalized : Vector3.forward;
            float range = InkBallistics.Position(Vector3.zero, Vector3.forward * CenterSpeed(w, charge), w,
                Math.Min(w.ShotGuideSeconds, w.Lifetime)).z;
            float depth = Vector3.Dot(muzzle - camera, forward) + Mathf.Max(.0001f, range);
            Vector3 target = camera + forward * depth, delta = target - muzzle;
            bool valid = float.IsFinite(depth) && depth > .0001f && delta.sqrMagnitude > 1e-8f && Vector3.Dot(delta, forward) > .0001f;
            Vector3 direction = valid ? delta.normalized : forward;
            return new TpsAimSolution { CameraOrigin = camera, Forward = forward, Muzzle = muzzle,
                AimPoint = valid ? target : muzzle + forward, CorrectionPoint = valid ? target : muzzle + forward,
                InitialDirection = direction, ExitDirection = direction, FirstSegmentLength = valid ? delta.magnitude : 1 };
        }

        public static Quaternion Basis(Vector3 direction)
        {
            if (direction.sqrMagnitude < 1e-8f) direction = Vector3.forward;
            direction.Normalize();
            return Quaternion.LookRotation(direction, Mathf.Abs(direction.y) > .9999f ? Vector3.forward : Vector3.up);
        }

        public static float BiasedMagnitude(float unit, float bias)
        {
            if (bias <= 0) return 0;
            return Mathf.Pow(Mathf.Clamp01(unit), Mathf.Log(Mathf.Min(bias, .99999f)) / Mathf.Log(.5f));
        }
        static float Signed(ref uint seed, float bias)
        {
            float value = 2 * InkBallistics.Random01(ref seed) - 1;
            return Mathf.Sign(value) * BiasedMagnitude(Mathf.Abs(value), bias);
        }

        public static Vector3 SampleDirection(Vector3 direction, WeaponRuntimeConfig w, float horizontal, float vertical, float bias, ref uint seed)
        {
            bool horizontalZero = horizontal <= 0 || bias <= 0;
            bool verticalZero = vertical <= 0 || w.ReferencePitchBias <= 0;
            if (horizontalZero && (!WeaponSimulation.IsSplatling(w) || verticalZero)) return direction.normalized;
            var basis = Basis(direction);
            if (WeaponSimulation.IsSplatling(w))
            {
                float x = Signed(ref seed, bias) * horizontal * Mathf.Deg2Rad;
                float y = Signed(ref seed, w.ReferencePitchBias) * vertical * Mathf.Deg2Rad;
                return basis * new Vector3(Mathf.Tan(x), Mathf.Tan(y), 1).normalized;
            }
            float theta = horizontal * Mathf.Deg2Rad * BiasedMagnitude(InkBallistics.Random01(ref seed), bias);
            float phi = InkBallistics.Random01(ref seed) * Mathf.PI * 2;
            return basis * new Vector3(Mathf.Cos(phi) * Mathf.Sin(theta), Mathf.Sin(phi) * Mathf.Sin(theta), Mathf.Cos(theta));
        }

        public static Vector3 Velocity(TpsAimSolution aim, WeaponRuntimeConfig w, float charge, Vector3 movement, float yaw,
            float horizontal = 0, float vertical = 0, float bias = 0, uint seed = 0, bool sample = false)
        {
            Vector3 direction = aim.InitialDirection.normalized;
            float speed = CenterSpeed(w, charge);
            if (sample)
            {
                uint spreadSeed = Stream(seed, SpreadStream), speedSeed = Stream(seed, SpeedStream);
                direction = SampleDirection(aim.InitialDirection, w, horizontal, vertical, bias, ref spreadSeed);
                speed = WeaponSimulation.IsSplatling(w)
                    ? Mathf.Max(.01f, speed + Signed(ref speedSeed, w.SplatlingSpeedBias) * (w.SpeedMax - w.SpeedMin) * .5f)
                    : Mathf.Lerp(w.SpeedMin, w.SpeedMax, InkBallistics.Random01(ref speedSeed));
            }
            var velocity = direction * speed;
            return w.InheritsMovement ? ShooterDetailSimulation.InheritMovement(velocity, movement, yaw, w) : velocity;
        }

        public static InkShot Representative(TpsAimSolution aim, WeaponRuntimeConfig w, float charge, Vector3 movement = default, float yaw = 0)
        {
            var shot = new InkShot { Origin = aim.MuzzleBlocked ? aim.Pivot : aim.Muzzle, Configuration = w, Charge = charge,
                Velocity = Velocity(aim, w, charge, movement, yaw) };
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            return shot;
        }
    }
}
