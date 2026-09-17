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
            if (w.ReferenceRules && WeaponSimulation.IsBubble(w)) shot.Velocity = ReferenceBallistics.BubbleLaunch(aim.InitialDirection, w, 0, true);
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
            if (WeaponSimulation.IsBubble(w)) return CalculateBubble(shot, w);
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
        static FlatShotRange CalculateBubble(InkShot shot, WeaponRuntimeConfig w)
        {
            if (w.ReferenceRules) return CalculateReferenceBubble(shot, w);
            Vector3 position = shot.Origin, velocity = shot.Velocity;
            double time = 0, step = 1.0 / GameplayConfig.Global.ProjectileStepRate;
            float travelled = 0; int bounces = 0; bool grounded = false;
            while (time < w.Lifetime - 1e-8)
            {
                double dt = Math.Min(step, w.Lifetime - time);
                var next = InkBallistics.Position(position, velocity, w.ProjectileGravity, dt);
                float distance = Vector3.Distance(position, next);
                bool limited = travelled + distance >= w.EffectiveRange;
                if (limited && distance > 0)
                { float fraction = (w.EffectiveRange - travelled) / distance; next = Vector3.Lerp(position, next, fraction); dt *= fraction; distance *= fraction; }
                if (next.y <= w.CollisionRadius)
                {
                    float fraction = Mathf.Clamp01((position.y - w.CollisionRadius) / Mathf.Max(.000001f, position.y - next.y));
                    next = Vector3.Lerp(position, next, fraction); dt *= fraction; travelled += distance * fraction;
                    time += dt; position = next;
                    if (bounces >= w.BubbleGroundBounces || bounces >= w.BubbleMaxBounces || travelled >= w.EffectiveRange)
                    { grounded = true; break; }
                    velocity += Vector3.down * (w.ProjectileGravity * (float)dt);
                    velocity = InkProjectileService.ReflectBubble(velocity, Vector3.up, w, out _);
                    position.y = w.CollisionRadius + .002f; bounces++;
                }
                else
                {
                    time += dt; travelled += distance; position = next;
                    velocity += Vector3.down * (w.ProjectileGravity * (float)dt);
                    if (limited) break;
                }
            }
            Vector3 delta = position - shot.Origin;
            return new FlatShotRange(new Vector2(delta.x, delta.z).magnitude, time, grounded);
        }
        static FlatShotRange CalculateReferenceBubble(InkShot shot, WeaponRuntimeConfig w)
        {
            var segment = InkBounce.Initial(shot); double time = shot.Born; float travelled = 0;
            while (time < shot.Born + w.Lifetime - 1e-8)
            {
                double end = Math.Min(time + 1.0 / 60, shot.Born + w.Lifetime), cursor = time;
                for (int contacts=0;cursor<end-1e-8 && contacts<8;contacts++)
                {
                    Vector3 from=segment.PositionAt(cursor,shot),to=segment.PositionAt(end,shot);
                    float radius=ReferenceBallistics.BubbleRadius(shot,end-shot.Born,(int)segment.Sequence,false);
                    float distance=Vector3.Distance(from,to),remaining=Mathf.Max(0,w.EffectiveRange-travelled);
                    if(distance>=remaining) return new FlatShotRange(Vector3.ProjectOnPlane(Vector3.Lerp(from,to,remaining/Mathf.Max(.000001f,distance))-shot.Origin,Vector3.up).magnitude,cursor-shot.Born,false);
                    if(to.y<=radius)
                    {
                        float fraction=Mathf.Clamp01((from.y-radius)/Mathf.Max(.000001f,from.y-to.y));
                        double hit=cursor+(end-cursor)*fraction;Vector3 point=Vector3.Lerp(from,to,fraction);travelled+=distance*fraction;
                        if(segment.GroundBounces>=w.BubbleGroundBounces || segment.Sequence>=w.BubbleMaxBounces)
                            return new FlatShotRange(Vector3.ProjectOnPlane(point-shot.Origin,Vector3.up).magnitude,hit-shot.Born,true);
                        var velocity=segment.VelocityAt(hit,shot);
                        segment.Sequence++;segment.GroundBounces++;segment.Time=hit;
                        segment.Position=new Vector3(point.x,ReferenceBallistics.BubbleRadius(shot,hit-shot.Born,(int)segment.Sequence,false)+.002f,point.z);
                        segment.Velocity=InkProjectileService.ReflectBubble(velocity,Vector3.up,w,out _);
                        cursor=Math.Max(cursor+.000001,hit);
                    }
                    else{travelled+=distance;cursor=end;}
                }
                time=end;
            }
            var final=segment.PositionAt(time,shot)-shot.Origin;
            return new FlatShotRange(new Vector2(final.x,final.z).magnitude,w.Lifetime,false);
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
            if (WeaponSimulation.IsBubble(w))
            {
                var flight = HeroFlatRange.Calculate(w, profile, 0, near, far);
                return new[] {
                    new HeroStat("伤害", $"{w.Damage:0.#}", $"每颗伤害 · 每组 {w.BurstCount} 颗"),
                    new HeroStat("射速", $"{1 / w.BubbleVolleySeconds:0.##} 组/秒", $"颗间 {w.BubbleIntervalSeconds:0.###} 秒"),
                    new HeroStat("最大散布", "0°", "方向由准星控制"),
                    new HeroStat("平射射程", $"{flight.Distance:0.0} 米", "平地弹跳至破裂 · 参考射程")
                };
            }
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
