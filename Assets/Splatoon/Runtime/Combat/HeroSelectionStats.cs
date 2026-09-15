using System;
using System.Collections.Generic;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Combat
{
    public readonly struct FlatShotRange
    {
        public readonly float Distance;
        public readonly double FlightSeconds;
        public readonly bool HitGround;
        public FlatShotRange(float distance, double seconds, bool hitGround)
        { Distance = distance; FlightSeconds = seconds; HitGround = hitGround; }
        public string Display => $"{Distance:0.0} 米" + (HitGround ? "" : "（寿命上限）");
    }

    /// <summary>Reference flat ground shot. Uses the authoritative logical muzzle, not animated bones.</summary>
    public static class HeroFlatRange
    {
        // Matches the grounded reference setup used by projectile measurements.
        public const float StandingRootHeight = .04f;
        public static float MinimumCharge(WeaponRuntimeConfig w) => WeaponSimulation.IsSplatling(w)
            ? (float)(w.SplatlingMinChargeSeconds / w.ChargeSeconds) : 0;

        public static InkShot ReferenceShot(WeaponRuntimeConfig w, CharacterPresentationProfile profile,
            float charge, float nearDistance, float farDistance, byte muzzle = 0)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var root = Vector3.up * StandingRootHeight;
            var origin = root + profile.MuzzleOffset(0, muzzle);
            var camera = root + profile.CameraPivot + profile.CameraOffset;
            var aim = TpsAimSolver.Geometry(camera, Vector3.forward, origin, nearDistance, farDistance, float.PositiveInfinity);
            float speed = WeaponSimulation.IsCharge(w) ? WeaponSimulation.Speed(w, charge)
                : WeaponSimulation.IsSplatling(w) ? Mathf.Lerp(w.ChargeMinSpeed, (w.SpeedMin + w.SpeedMax) * .5f, SplatlingSimulation.RangeCharge(w, charge))
                : w.PelletCount > 1 ? w.SpeedMin : (w.SpeedMin + w.SpeedMax) * .5f;
            var shot = new InkShot { Origin = origin, Velocity = aim.InitialDirection * speed, Charge = charge, Configuration = w, MuzzleIndex = muzzle };
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            return shot;
        }

        public static FlatShotRange Calculate(WeaponRuntimeConfig w, CharacterPresentationProfile profile,
            float charge, float nearDistance, float farDistance)
        {
            var right = Calculate(ReferenceShot(w, profile, charge, nearDistance, farDistance), w);
            if (w.MuzzleMode != WeaponMuzzleMode.AlternatingRightLeft) return right;
            var left = Calculate(ReferenceShot(w, profile, charge, nearDistance, farDistance, 1), w);
            return new FlatShotRange((right.Distance + left.Distance) * .5f,
                (right.FlightSeconds + left.FlightSeconds) * .5, right.HitGround && left.HitGround);
        }

        public static FlatShotRange Calculate(InkShot shot, WeaponRuntimeConfig w)
        {
            if (shot.Origin.y <= w.CollisionRadius) return new FlatShotRange(0, 0, true);
            double end = w.Lifetime;
            bool hit = InkBallistics.Position(shot, w, end).y <= w.CollisionRadius;
            // With positive forward launch and nonnegative gravity, the first descending
            // ground crossing is unique, including the straight/braking transitions.
            if (hit)
            {
                double low = 0, high = end;
                for (int i = 0; i < 40; i++)
                {
                    double mid = (low + high) * .5;
                    if (InkBallistics.Position(shot, w, mid).y > w.CollisionRadius) low = mid;
                    else high = mid;
                }
                end = high;
            }
            Vector3 delta = InkBallistics.Position(shot, w, end) - shot.Origin;
            return new FlatShotRange(new Vector2(delta.x, delta.z).magnitude, end, hit);
        }
    }

    public readonly struct HeroStat
    {
        public readonly string Label, Value, Note;
        public HeroStat(string label, string value, string note = "") { Label = label; Value = value; Note = note; }
    }

    public static class HeroSelectionStats
    {
        public static float MaximumGroundSpread(WeaponRuntimeConfig w) => WeaponSimulation.IsCharge(w)
            ? Mathf.Max(w.ChargeMinSpread, w.SpreadDegrees)
            : WeaponSimulation.IsSplatling(w) ? Mathf.Max(w.BaseSpreadDegrees, Mathf.Max(w.SpreadDegrees, w.SplatlingPitchSpread))
            : Mathf.Max(w.BaseSpreadDegrees, w.SpreadDegrees);
        public static double FullChargeRate(WeaponRuntimeConfig w) => 1 / (w.StartSeconds + w.ChargeSeconds + WeaponSimulation.FireInterval(w));
        public static HeroStat[] Create(WeaponRuntimeConfig w, CharacterPresentationProfile profile, float near, float far)
        {
            bool charge = WeaponSimulation.IsCharge(w), spinner = WeaponSimulation.IsSplatling(w);
            string damage = charge ? $"{w.ChargeMinDamage:0.#}–{w.ChargePartialMaxDamage:0.#} / {w.Damage:0.#}"
                : spinner ? $"{w.ChargePartialMaxDamage:0.#} / {w.Damage:0.#} → {w.DamageMin:0.#}"
                : $"{w.Damage:0.#} → {w.DamageMin:0.#}";
            string damageNote = charge ? "未满蓄 / 满蓄" : spinner ? "未满蓄 / 满蓄 → 远端" : w.PelletCount > 1 ? $"每颗伤害 · 每次 {w.PelletCount} 颗" : "近端 → 远端";
            string rate = charge ? $"{FullChargeRate(w):0.##} 发/秒" : $"{WeaponDisplay.SustainedRate(w):0.##} " + (w.PelletCount > 1 ? "次齐射/秒" : "发/秒");
            var minimum = HeroFlatRange.Calculate(w, profile, HeroFlatRange.MinimumCharge(w), near, far);
            var maximum = charge || spinner ? HeroFlatRange.Calculate(w, profile, 1, near, far) : minimum;
            string range = charge || spinner ? $"{minimum.Distance:0.0} / {maximum.Distance:0.0} 米" : maximum.Display;
            string rangeNote = charge || spinner ? "最低蓄力 / 满蓄 · 参考射程" + (minimum.HitGround && maximum.HitGround ? "" : "（寿命上限）") : "平地站立 · 参考射程";
            return new[] {
                new HeroStat("伤害", damage, damageNote),
                new HeroStat("射速", rate, charge ? "满蓄连续射击，含起手、蓄力及冷却" : spinner ? "连射阶段" : ""),
                new HeroStat("最大散布", $"{MaximumGroundSpread(w):0.#}°", "地面 · 偏离准星中心的最大角度"),
                new HeroStat("平射射程", range, rangeNote)
            };
        }
    }

    public sealed class HeroSelectionStatsCache
    {
        readonly Dictionary<int, Entry> _entries = new();
        sealed class Entry
        {
            public WeaponRuntimeConfig Weapon;
            public uint Revision;
            public Vector3 Right, Left, Camera;
            public bool Dual;
            public float Near, Far;
            public HeroStat[] Stats;
        }
        public HeroStat[] Get(int heroId, uint revision, WeaponRuntimeConfig w, CharacterPresentationProfile profile, float near, float far)
        {
            Vector3 right = profile.MuzzleOffset(0), left = profile.MuzzleOffset(0, 1), camera = profile.CameraPivot + profile.CameraOffset;
            if (_entries.TryGetValue(heroId, out var e) && ReferenceEquals(e.Weapon, w) && e.Revision == revision && e.Right == right && e.Left == left
                && e.Camera == camera && e.Dual == profile.DualWield && e.Near == near && e.Far == far) return e.Stats;
            e = new Entry { Weapon = w, Revision = revision, Right = right, Left = left, Camera = camera, Dual = profile.DualWield,
                Near = near, Far = far, Stats = HeroSelectionStats.Create(w, profile, near, far) };
            _entries[heroId] = e; return e.Stats;
        }
        public void Clear() => _entries.Clear();
    }
}
