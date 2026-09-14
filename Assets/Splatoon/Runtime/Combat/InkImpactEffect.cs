using System.Collections.Generic;
using UnityEngine;
using Splatoon.Painting;

namespace Splatoon.Combat
{
    /// <summary>Cosmetic only. All variation comes from the authoritative impact identity.</summary>
    public sealed class InkImpactEffect : MonoBehaviour
    {
        public const int ParticleLimit = 32;
        public const float RecycleTimeout = .8f;
        public ParticleSystem Splash, Streaks, Droplets;
        [Header("墨片")]
        public Vector2Int SplashCount = new(1, 2);
        public Vector2 SplashWidth = new(.7f, 1.1f);
        public Vector2 SplashAspect = new(.72f, 1.15f);
        public Vector2 SplashLifetime = new(.18f, .30f);
        [Header("外甩墨条")]
        public Vector2Int StreakCount = new(8, 12);
        public Vector2 StreakLength = new(.16f, .38f);
        public Vector2 StreakWidth = new(.045f, .085f);
        public Vector2 StreakSpeed = new(1.8f, 3.8f);
        public Vector2 StreakLifetime = new(.20f, .40f);
        [Header("零散墨滴")]
        public Vector2Int DropletCount = new(12, 18);
        public Vector2 DropletSize = new(.035f, .095f);
        public Vector2 DropletSpeed = new(1.2f, 3.2f);
        public Vector2 DropletLifetime = new(.35f, .65f);
        readonly List<Vector4> _splashData = new(2);

        // There are no scheduled emissions: only actual particles extend the pooled effect's life.
        public bool IsAlive => Splash.particleCount + Streaks.particleCount + Droplets.particleCount > 0;

        public static uint Seed(InkImpact impact) => NonZero(InkShapeAtlas.Hash(impact.Id ^
            InkShapeAtlas.Hash(impact.Round + 0x9e3779b9u) ^ ((uint)impact.PelletIndex * 0x85ebca6bu)));
        static uint NonZero(uint seed) => seed == 0 ? 1u : seed;
        static float Range(ref uint seed, Vector2 range) => Mathf.Lerp(range.x, range.y, InkBallistics.Random01(ref seed));
        static int Count(ref uint seed, Vector2Int range, int remaining) => Mathf.Clamp(range.x +
            Mathf.FloorToInt(InkBallistics.Random01(ref seed) * Mathf.Max(1, range.y - range.x + 1)), 0, remaining);

        public void Play(Color color, uint seed)
        {
            Stop();
            uint splashSeed = NonZero(InkShapeAtlas.Hash(seed ^ 0xa511e9b3u));
            uint streakSeed = NonZero(InkShapeAtlas.Hash(seed ^ 0x63d83595u));
            uint dropSeed = NonZero(InkShapeAtlas.Hash(seed ^ 0xb5297a4du));
            Prepare(Splash, splashSeed); Prepare(Streaks, streakSeed); Prepare(Droplets, dropSeed);
            int remaining = ParticleLimit;
            int count = Count(ref splashSeed, SplashCount, remaining); remaining -= count;
            _splashData.Clear();
            for (int i = 0; i < count; i++)
            {
                float width = Range(ref splashSeed, SplashWidth) * (i == 0 ? 1 : .72f);
                float aspect = Range(ref splashSeed, SplashAspect);
                float roll = InkBallistics.Random01(ref splashSeed) * 360;
                var rotation = transform.rotation * Quaternion.AngleAxis(roll, Vector3.forward);
                var p = new ParticleSystem.EmitParams
                {
                    position = transform.position + transform.forward * (i * .008f),
                    velocity = transform.forward * .06f,
                    rotation3D = rotation.eulerAngles,
                    startSize3D = new Vector3(width, width * aspect, 1),
                    startLifetime = Range(ref splashSeed, SplashLifetime), startColor = color,
                    randomSeed = NonZero(splashSeed), applyShapeToPosition = false
                };
                Splash.Emit(p, 1);
                _splashData.Add(new Vector4(Mathf.FloorToInt(InkBallistics.Random01(ref splashSeed) * 16), 0, 0, 0));
            }
            Splash.SetCustomParticleData(_splashData, ParticleSystemCustomData.Custom1);
            count = Count(ref streakSeed, StreakCount, remaining); remaining -= count;
            EmitScatter(Streaks, count, true, color, ref streakSeed);
            count = Count(ref dropSeed, DropletCount, remaining);
            EmitScatter(Droplets, count, false, color, ref dropSeed);
        }

        void EmitScatter(ParticleSystem system, int count, bool streak, Color color, ref uint seed)
        {
            float phase = InkBallistics.Random01(ref seed) * Mathf.PI * 2;
            for (int i = 0; i < count; i++)
            {
                // Stratified directions keep visible gaps while jitter avoids a regular starburst.
                float angle = phase + (i + InkBallistics.Random01(ref seed) * .85f) * Mathf.PI * 2 / count;
                Vector3 tangent = transform.right * Mathf.Cos(angle) + transform.up * Mathf.Sin(angle);
                float lift = Range(ref seed, streak ? new Vector2(.12f, .38f) : new Vector2(.25f, .85f));
                Vector3 direction = (tangent + transform.forward * lift).normalized;
                float size = Range(ref seed, streak ? StreakWidth : DropletSize);
                float length = streak ? Range(ref seed, StreakLength) : size * Range(ref seed, new Vector2(1, 1.65f));
                var p = new ParticleSystem.EmitParams
                {
                    position = transform.position + tangent * Range(ref seed, new Vector2(.035f, .11f)),
                    velocity = direction * Range(ref seed, streak ? StreakSpeed : DropletSpeed),
                    rotation3D = Quaternion.LookRotation(direction, transform.forward).eulerAngles,
                    startSize3D = new Vector3(size, size, length),
                    startLifetime = Range(ref seed, streak ? StreakLifetime : DropletLifetime),
                    startColor = color, randomSeed = NonZero(seed), applyShapeToPosition = false
                };
                system.Emit(p, 1);
            }
        }

        static void Prepare(ParticleSystem system, uint seed)
        {
            system.useAutoRandomSeed = false;
            system.randomSeed = seed;
            system.Play(false);
        }

        public void Stop()
        {
            Splash.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            Streaks.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            Droplets.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }
}
