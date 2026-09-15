using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Prototype;
using Splatoon.Painting;

namespace Splatoon.Combat
{
    public struct InkShot : INetworkSerializable
    {
        public uint Id, Round, Seed, ShotSequence;
        public ulong Shooter;
        public ulong ActionId;
        public uint ReleaseSequence => (uint)(ActionId >> 32);
        public uint RoundIndex => (uint)ActionId;
        public uint Lifecycle, HeroRevision;
        public byte MuzzleIndex, PelletIndex;
        public int HeroId;
        public float Charge;
        public float SpreadHorizontal, SpreadVertical;
        public uint ConfigurationRevision;
        [NonSerialized] public WeaponRuntimeConfig Configuration;
        public byte Team;
        public double Born;
        public Vector3 Origin, Velocity;
        public float FirstSegmentLength, GravityStartAge;
        public Vector3 PostCorrectionVelocity;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Seed); s.SerializeValue(ref Shooter);
            s.SerializeValue(ref HeroId); s.SerializeValue(ref Team); s.SerializeValue(ref Born); s.SerializeValue(ref Origin); s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Charge); s.SerializeValue(ref SpreadHorizontal); s.SerializeValue(ref SpreadVertical); s.SerializeValue(ref ConfigurationRevision);
            s.SerializeValue(ref ShotSequence);
            s.SerializeValue(ref ActionId);
            s.SerializeValue(ref Lifecycle); s.SerializeValue(ref HeroRevision);
            s.SerializeValue(ref MuzzleIndex); s.SerializeValue(ref PelletIndex);
            s.SerializeValue(ref FirstSegmentLength); s.SerializeValue(ref PostCorrectionVelocity); s.SerializeValue(ref GravityStartAge);
        }
    }
    public struct InkImpact : INetworkSerializable
    {
        public uint Id, Round;
        public byte Team;
        public Vector3 Position, Normal;
        public bool Hit;
        public ulong Shooter, Victim;
        public float Damage;
        public bool Killed;
        public ulong ActionId;
        public uint Lifecycle, HeroRevision;
        public byte PelletIndex;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Team); s.SerializeValue(ref Position); s.SerializeValue(ref Normal); s.SerializeValue(ref Hit);
          s.SerializeValue(ref Shooter); s.SerializeValue(ref Victim); s.SerializeValue(ref Damage); s.SerializeValue(ref Killed);
          s.SerializeValue(ref ActionId); s.SerializeValue(ref Lifecycle); s.SerializeValue(ref HeroRevision); s.SerializeValue(ref PelletIndex); }
    }
    public static class InkBallistics
    {
        public static Vector3 Position(Vector3 origin, Vector3 velocity, float gravity, double age)
        { float t = (float)age; return origin + velocity * t + Vector3.down * (.5f * gravity * t * t); }
        public static float TravelTime(WeaponRuntimeConfig w, double age)
        {
            float t = Mathf.Max(0, (float)age), straight = (float)w.StraightSeconds;
            if (t <= straight) return t;
            float brake = Mathf.Max(.0001f, (float)w.BrakeSeconds);
            float b = Mathf.Min(t - straight, brake);
            return straight + b - .5f * (1 - w.BrakeSpeedMultiplier) * b * b / brake + Mathf.Max(0, t - straight - brake) * w.BrakeSpeedMultiplier;
        }
        public static Vector3 Position(Vector3 origin, Vector3 velocity, WeaponRuntimeConfig w, double age)
        {
            float fall = Mathf.Max(0, (float)(age - w.StraightSeconds));
            return origin + velocity * TravelTime(w, age) + Vector3.down * (.5f * w.ProjectileGravity * fall * fall);
        }
        public static double AgeAtDistance(WeaponRuntimeConfig w, float speed, float distance)
        {
            double travel = Math.Max(0, distance) / Math.Max(.0001, speed);
            double straight = w.StraightSeconds;
            if (travel <= straight) return travel;
            double brake = Math.Max(.0001, w.BrakeSeconds);
            double multiplier = w.BrakeSpeedMultiplier, extra = travel - straight;
            double brakingTravel = brake * (1 + multiplier) * .5;
            if (extra >= brakingTravel) return straight + brake + (extra - brakingTravel) / multiplier;
            // Stable inverse of b - (1 - multiplier) * b^2 / (2 * brake).
            return straight + 2 * extra / (1 + Math.Sqrt(Math.Max(0, 1 - 2 * (1 - multiplier) * extra / brake)));
        }
        public static double CorrectionAge(InkShot shot, WeaponRuntimeConfig w) =>
            AgeAtDistance(w, shot.Velocity.magnitude, shot.FirstSegmentLength);
        public static void ApplyCorrection(ref InkShot shot, TpsAimSolution aim, WeaponRuntimeConfig w)
        {
            // Preserve the sampled muzzle-to-target direction and spread for the entire flight.
            // Keep the serialized trajectory layout; passing the target never rotates a projectile.
            shot.PostCorrectionVelocity = shot.Velocity;
            shot.FirstSegmentLength = aim.FirstSegmentLength;
            // Convergence distance does not change the weapon's gravity clock.
            shot.GravityStartAge = (float)w.StraightSeconds;
        }
        public static Vector3 Position(InkShot shot, WeaponRuntimeConfig w, double age)
        {
            if (shot.PostCorrectionVelocity.sqrMagnitude == 0) return Position(shot.Origin, shot.Velocity, w, age);
            float distance = shot.Velocity.magnitude * TravelTime(w, age);
            float first = Mathf.Min(distance, shot.FirstSegmentLength);
            float fall = Mathf.Max(0, (float)age - shot.GravityStartAge);
            return shot.Origin + shot.Velocity.normalized * first
                + shot.PostCorrectionVelocity.normalized * (distance - first)
                + Vector3.down * (.5f * w.ProjectileGravity * fall * fall);
        }
        public static Vector3 Velocity(InkShot shot, WeaponRuntimeConfig w, double age)
        {
            float straight = (float)w.StraightSeconds;
            float brake = Mathf.Max(.0001f, (float)w.BrakeSeconds);
            float multiplier = Mathf.Lerp(1, w.BrakeSpeedMultiplier, Mathf.Clamp01(((float)age - straight) / brake));
            bool corrected = shot.PostCorrectionVelocity.sqrMagnitude > 0;
            Vector3 velocity = corrected && age >= CorrectionAge(shot, w) ? shot.PostCorrectionVelocity : shot.Velocity;
            float fall = Mathf.Max(0, (float)age - (corrected ? shot.GravityStartAge : straight));
            return velocity * multiplier + Vector3.down * (w.ProjectileGravity * fall);
        }
        public static float Random01(ref uint seed)
        { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / 16777216f; }
        public static Vector3 LaunchVelocity(Vector3 direction, WeaponRuntimeConfig w, ref uint seed, float spread = -1)
        {
            float radius = Mathf.Sqrt(Random01(ref seed)) * Mathf.Tan((spread < 0 ? w.SpreadDegrees : spread) * Mathf.Deg2Rad);
            float angle = Random01(ref seed) * Mathf.PI * 2;
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized;
            return Quaternion.LookRotation(direction) * local * Mathf.Lerp(w.SpeedMin, w.SpeedMax, Random01(ref seed));
        }
        public static Vector3 SplatlingVelocity(Vector3 direction, WeaponRuntimeConfig w, float charge, float spread, float vertical, ref uint seed)
        {
            float bias = Mathf.Log(w.SplatlingSpreadBias) / Mathf.Log(.5f);
            float x = Centered(ref seed, bias), y = Centered(ref seed, bias);
            Vector3 local = new(Mathf.Tan(x * spread * Mathf.Deg2Rad), Mathf.Tan(y * vertical * Mathf.Deg2Rad), 1);
            float center = Mathf.Lerp(w.ChargeMinSpeed, (w.SpeedMin+w.SpeedMax)*.5f, SplatlingSimulation.RangeCharge(w, charge));
            float jitter = Centered(ref seed, Mathf.Log(w.SplatlingSpeedBias)/Mathf.Log(.5f)) * (w.SpeedMax-w.SpeedMin)*.5f;
            return Quaternion.LookRotation(direction) * local.normalized * Mathf.Max(.01f,center+jitter);
        }
        static float Centered(ref uint seed, float gamma)
        { float x=Random01(ref seed)*2-1; return Mathf.Sign(x)*Mathf.Pow(Mathf.Abs(x),gamma); }
        public static Vector3 PelletVelocity(Vector3 direction, WeaponRuntimeConfig w, float spread, int index, uint groupSeed)
        {
            // Equal-area disk samples; rotate the complete pattern, never cluster eight independent random samples.
            float angle = index * 2.39996323f + Random01(ref groupSeed) * Mathf.PI * 2;
            float radius = Mathf.Sqrt((index + .5f) / w.PelletCount) * Mathf.Tan(spread * Mathf.Deg2Rad);
            return Quaternion.LookRotation(direction) * new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized * w.SpeedMin;
        }
    }
    /// <summary>Server-only continuous collision simulation. Presentation never reports hits.</summary>
    public sealed class InkProjectileService
    {
        private struct Active { public InkShot Shot; public double SimulatedUntil, CorrectionAt; public Vector3 LastTrail; public uint TrailSeed, PaintOrdinal; public int TrailCount; }
        private readonly List<Active> _active = new(256);
        private readonly TpsAimSolver _aim = new();
        private readonly InkShapeSelector _shapes = new();
        private uint _id;
        public readonly List<InkShot> Spawned = new(16);
        public readonly List<InkImpact> Impacts = new(32);
        public int ActiveCount => _active.Count;
#if UNITY_EDITOR
        // Opt-in observations of the real simulation; never sent over the network.
        public Action<InkShot, double, Vector3> TraceObserved;
        public Action<PaintStamp> PaintObserved;
        public void SpawnForMeasurement(InkShot shot)
        {
            if (GameplayConfig.GetWeapon(shot.HeroId) == null || shot.Velocity.sqrMagnitude <= 0)
                throw new ArgumentException("测量墨弹必须指定有效武器与初速");
            shot.Configuration ??= GameplayConfig.GetWeapon(shot.HeroId);
            Spawned.Add(shot);
            BeginFlight(shot, shot.Origin, shot.Velocity.normalized);
        }
#endif
        public void Spawn(PrototypePlayer player, PlayerSnapshot state, double born, uint round)
        {
            var w = GameplayConfig.GetWeapon(state.HeroId);
            var aim = _aim.Resolve(player, state, state.LastShotMuzzle);
            uint groupSeed = unchecked((uint)state.ShotActionId ^ (uint)(state.ShotActionId >> 32) * 747796405u ^ round * 2891336453u ^ (uint)player.PlayerId ^ state.HeroRevision);
            if (groupSeed == 0) groupSeed = 1;
            for (byte pellet = 0; pellet < w.PelletCount; pellet++)
            {
            uint seed = unchecked(++_id * 747796405u + round * 2891336453u + (uint)player.PlayerId + 1u);
            if (seed == 0) seed = 1;
            var shot = new InkShot { Id = _id, Round = round, Seed = seed, ShotSequence = state.ShotSequence, Shooter = player.PlayerId, HeroId = state.HeroId, Team = state.Team, Born = born, Origin = aim.MuzzleBlocked ? aim.Pivot : aim.Muzzle };
            shot.ActionId = state.ShotActionId;
            shot.Lifecycle = state.Revision; shot.HeroRevision = state.HeroRevision;
            shot.MuzzleIndex = state.LastShotMuzzle; shot.PelletIndex = pellet;
            shot.Charge = state.LastShotCharge;
            shot.Configuration = w; shot.ConfigurationRevision = WeaponConfigService.Current.Revision(state.HeroId); shot.SpreadHorizontal = state.LastShotSpread; shot.SpreadVertical = state.LastShotVerticalSpread;
            float spread = shot.SpreadHorizontal;
            shot.Velocity = w.PelletCount > 1 ? InkBallistics.PelletVelocity(aim.InitialDirection, w, spread, pellet, groupSeed)
                : InkBallistics.LaunchVelocity(aim.InitialDirection, w, ref seed, spread);
            if (WeaponSimulation.IsCharge(w)) shot.Velocity = shot.Velocity.normalized * WeaponSimulation.Speed(w, shot.Charge);
            if (WeaponSimulation.IsSplatling(w)) shot.Velocity = InkBallistics.SplatlingVelocity(aim.InitialDirection,w,shot.Charge,spread,shot.SpreadVertical,ref seed);
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            Spawned.Add(shot);
            if (aim.MuzzleBlocked) { uint ordinal = 0; Resolve(shot, aim.MuzzleHit.Collider, aim.MuzzleHit.Point, aim.MuzzleHit.Normal, 0, ref ordinal); }
            else BeginFlight(shot, aim.Muzzle, aim.InitialDirection, WeaponSimulation.IsSplatling(w) ? state.Position + Vector3.up * .1f : (Vector3?)null);
            }
        }
        private void BeginFlight(InkShot shot, Vector3 muzzle, Vector3 forward, Vector3? foot = null)
        {
            uint trailSeed = shot.Seed ^ 0x9E3779B9u, ordinal = 0;
            if (trailSeed == 0) trailSeed = 1;
            var config = shot.Configuration;
            if (shot.PelletIndex == 0 && (!WeaponSimulation.IsSplatling(config) || (shot.ShotSequence-1)%config.SplatlingFootEvery==0))
                PaintTrail(foot ?? (muzzle - forward * .6f), shot, config, ref trailSeed, ref ordinal, WeaponSimulation.IsSplatling(config) ? config.SplatlingFootRadius : -1);
            _active.Add(new Active { Shot = shot, SimulatedUntil = shot.Born, LastTrail = muzzle, TrailSeed = trailSeed, PaintOrdinal = ordinal,
                CorrectionAt = shot.Born + InkBallistics.CorrectionAge(shot, config) });
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, 0, muzzle);
#endif
        }
        public void Simulate(double until)
        {
            double step = 1.0 / GameplayConfig.Global.ProjectileStepRate;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i]; var w = a.Shot.Configuration;
                double end = Math.Min(until, a.Shot.Born + w.Lifetime); bool hit = false;
                while (a.SimulatedUntil < end - 1e-8)
                {
                    // External callers may run at 30/60/144 Hz. Only integrate complete
                    // configured projectile steps, so trail positions and random draws agree.
                    double next = Math.Min(a.SimulatedUntil + step, a.Shot.Born + w.Lifetime);
                    if (next > end + 1e-8) break;
                    double from = a.SimulatedUntil;
                    // Keep the fixed grid intact; split only the collision path at the bend.
                    if (a.CorrectionAt > from && a.CorrectionAt < next)
                    {
                        hit = TraceSegment(ref a, w, from, a.CorrectionAt);
                        from = a.CorrectionAt;
                    }
                    if (!hit) hit = TraceSegment(ref a, w, from, next);
                    if (hit) break;
                    a.SimulatedUntil = next;
                }
                if (hit || end >= a.Shot.Born + w.Lifetime - 1e-8)
                {
                    if (!hit) Impacts.Add(new InkImpact { Id = a.Shot.Id, Round = a.Shot.Round, Team = a.Shot.Team, Position = InkBallistics.Position(a.Shot, w, w.Lifetime), Hit = false });
                    _active.RemoveAt(i);
                }
                else _active[i] = a;
            }
        }
        private bool TraceSegment(ref Active active, WeaponRuntimeConfig weapon, double start, double end)
        {
            var shot = active.Shot;
            Vector3 from = InkBallistics.Position(shot, weapon, start - shot.Born);
            Vector3 to = InkBallistics.Position(shot, weapon, end - shot.Born);
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (WeaponSimulation.IsSplatling(weapon))
            {
                bool worldOverlap = _aim.Overlap(from,weapon.CollisionRadius,shot.Shooter,-delta.normalized,out var world,false);
                bool playerOverlap = _aim.Overlap(from,weapon.SplatlingPlayerRadius,shot.Shooter,-delta.normalized,out var player,true);
                if (worldOverlap || playerOverlap)
                {
                    var contact=worldOverlap?world:player;
                    Resolve(shot,contact.Collider,contact.Point,contact.Normal,start-shot.Born,ref active.PaintOrdinal); return true;
                }
                bool worldHit=_aim.ClosestCast(from,delta,distance,weapon.CollisionRadius,shot.Shooter,out world,false);
                bool playerHit=_aim.ClosestCast(from,delta,distance,weapon.SplatlingPlayerRadius,shot.Shooter,out player,true);
                if (worldHit || playerHit)
                {
                    var contact=worldHit && (!playerHit || world.Distance <= player.Distance) ? world : player;
                    Resolve(shot,contact.Collider,contact.Point,contact.Normal,start-shot.Born+(end-start)*contact.Distance/Mathf.Max(.0001f,distance),ref active.PaintOrdinal); return true;
                }
            }
            else if (_aim.Overlap(from, weapon.CollisionRadius, shot.Shooter, -InkBallistics.Velocity(shot, weapon, start - shot.Born).normalized, out var overlap))
            {
                Resolve(shot, overlap.Collider, overlap.Point, overlap.Normal, start - shot.Born, ref active.PaintOrdinal);
                return true;
            }
            if (!WeaponSimulation.IsSplatling(weapon) && _aim.ClosestCast(from, delta, distance, weapon.CollisionRadius, shot.Shooter, out var hit))
            {
                Resolve(shot, hit.Collider, hit.Point, hit.Normal, start - shot.Born + (end - start) * hit.Distance / Mathf.Max(.0001f, distance), ref active.PaintOrdinal);
                return true;
            }
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, end - shot.Born, to);
#endif
            if (Vector3.Distance(active.LastTrail, to) >= weapon.TrailSpacing && (!WeaponSimulation.IsSplatling(weapon) || active.TrailCount < weapon.SplatlingTrailCount))
            { PaintTrail(to, shot, weapon, ref active.TrailSeed, ref active.PaintOrdinal); active.LastTrail = to; active.TrailCount++; }
            return false;
        }
        private void PaintTrail(Vector3 position, InkShot shot, WeaponRuntimeConfig w, ref uint seed, ref uint ordinal, float radius = -1)
        {
            bool enabled = PrototypeMatch.Current != null;
#if UNITY_EDITOR
            enabled |= PaintObserved != null;
#endif
            if (!enabled || !Physics.Raycast(position, Vector3.down, out var h, w.TrailMaxDrop, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return;
            var surface = h.collider.GetComponentInParent<PaintSurface>();
            if (surface != null) ApplyPaint(surface, shot, h.point, h.normal, radius > 0 ? radius : Mathf.Lerp(w.TrailRadiusMin, w.TrailRadiusMax, InkBallistics.Random01(ref seed)), w, ++ordinal, false);
        }
        private void ApplyPaint(PaintSurface surface, InkShot shot, Vector3 point, Vector3 normal, float radius, WeaponRuntimeConfig w, uint ordinal, bool impact)
        {
            // A sustained magazine can have several bullets in flight. Give each
            // stamp an immutable shape so batching their arrivals cannot alter paint.
            uint entropy = InkShapeAtlas.Hash(shot.Seed ^ InkShapeAtlas.Hash(shot.Id) ^ InkShapeAtlas.Hash(ordinal) ^ (impact ? 0xb5297a4du : 0x68e31da4u));
            uint shapeSeed = WeaponSimulation.IsSplatling(w) ? InkShapeAtlas.Pack((int)(entropy % InkShapeAtlas.Count), entropy)
                : _shapes.Select(shot.Shooter, shot.Round, shot.Seed, shot.Id, shot.PelletIndex, ordinal, impact);
#if UNITY_EDITOR
            PaintObserved?.Invoke(new PaintStamp { Round = shot.Round, SurfaceId = surface.SurfaceId, Team = shot.Team,
                Position = point, Normal = normal, Radius = radius, Hardness = w.PaintHardness, Strength = w.PaintStrength, ShapeSeed = shapeSeed });
#endif
            if (PrototypeMatch.Current != null)
                PrototypeMatch.Current.Paint(surface, point, normal, radius, shot.Team, w.PaintHardness, w.PaintStrength, shapeSeed);
        }
        private void Resolve(InkShot shot, Collider collider, Vector3 point, Vector3 normal, double age, ref uint ordinal)
        {
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, age, point);
#endif
            var w = shot.Configuration ?? GameplayConfig.GetWeapon(shot.HeroId);
            var victim = collider.GetComponentInParent<PrototypePlayer>();
            float actualDamage = 0; bool killed = false;
            if (victim != null)
            {
                float before = victim.Snapshot.Value.Health;
                if (shot.Velocity.magnitude * InkBallistics.TravelTime(w, age) <= WeaponSimulation.Range(w, shot.Charge))
                    victim.ReceiveDamage(shot.Team, WeaponSimulation.Damage(w, age, shot.Charge), InkBallistics.Velocity(shot, w, age), shot.Shooter);
                actualDamage = before - victim.Snapshot.Value.Health; killed = actualDamage > 0 && victim.Snapshot.Value.Health <= 0;
            }
            else
            {
                var surface = collider.GetComponentInParent<PaintSurface>();
                if (surface != null)
                {
                    uint seed = shot.Seed;
                    ApplyPaint(surface, shot, point, normal, Mathf.Lerp(w.PaintRadiusMin, w.PaintRadiusMax, InkBallistics.Random01(ref seed)), w, ++ordinal, true);
                }
            }
            Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Team = shot.Team, Position = point, Normal = normal, Hit = true,
                ActionId = shot.ActionId, Lifecycle = shot.Lifecycle, HeroRevision = shot.HeroRevision, PelletIndex = shot.PelletIndex,
                Shooter = shot.Shooter, Victim = victim != null ? victim.PlayerId : 0, Damage = actualDamage, Killed = killed });
        }
        public InkShot[] LiveShots() => _active.ConvertAll(a => a.Shot).ToArray();
        public void Clear() { _active.Clear(); Spawned.Clear(); Impacts.Clear(); _shapes.Clear(); }
    }
}
