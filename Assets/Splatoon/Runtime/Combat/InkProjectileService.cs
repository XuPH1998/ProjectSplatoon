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
        public uint Lifecycle, HeroRevision;
        public byte MuzzleIndex, PelletIndex;
        public int HeroId;
        public float Charge;
        public byte Team;
        public double Born;
        public Vector3 Origin, Velocity;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Seed); s.SerializeValue(ref Shooter);
            s.SerializeValue(ref HeroId); s.SerializeValue(ref Team); s.SerializeValue(ref Born); s.SerializeValue(ref Origin); s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Charge);
            s.SerializeValue(ref ShotSequence);
            s.SerializeValue(ref ActionId);
            s.SerializeValue(ref Lifecycle); s.SerializeValue(ref HeroRevision);
            s.SerializeValue(ref MuzzleIndex); s.SerializeValue(ref PelletIndex);
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
        public static float TravelTime(cfg.HeroConfig w, double age)
        {
            float t = Mathf.Max(0, (float)age), straight = (float)WeaponSimulation.Seconds(w.StraightFrames);
            if (t <= straight) return t;
            float brake = Mathf.Max(.0001f, (float)WeaponSimulation.Seconds(w.BrakeFrames));
            float b = Mathf.Min(t - straight, brake);
            return straight + b - .5f * (1 - w.BrakeSpeedMultiplier) * b * b / brake + Mathf.Max(0, t - straight - brake) * w.BrakeSpeedMultiplier;
        }
        public static Vector3 Position(Vector3 origin, Vector3 velocity, cfg.HeroConfig w, double age)
        {
            float fall = Mathf.Max(0, (float)(age - WeaponSimulation.Seconds(w.StraightFrames)));
            return origin + velocity * TravelTime(w, age) + Vector3.down * (.5f * w.ProjectileGravity * fall * fall);
        }
        public static float Random01(ref uint seed)
        { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / 16777216f; }
        public static Vector3 LaunchVelocity(Vector3 direction, cfg.HeroConfig w, ref uint seed, float spread = -1)
        {
            float radius = Mathf.Sqrt(Random01(ref seed)) * Mathf.Tan((spread < 0 ? w.SpreadDegrees : spread) * Mathf.Deg2Rad);
            float angle = Random01(ref seed) * Mathf.PI * 2;
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized;
            return Quaternion.LookRotation(direction) * local * Mathf.Lerp(w.SpeedMin, w.SpeedMax, Random01(ref seed));
        }
        public static Vector3 PelletVelocity(Vector3 direction, cfg.HeroConfig w, float spread, int index, uint groupSeed)
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
        private struct Active { public InkShot Shot; public double SimulatedUntil; public Vector3 LastTrail; }
        private readonly List<Active> _active = new(256);
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Collider[] _overlaps = new Collider[64];
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
            if (GameplayConfig.GetHero(shot.HeroId) == null || shot.Velocity.sqrMagnitude <= 0)
                throw new ArgumentException("测量墨弹必须指定有效武器与初速");
            Spawned.Add(shot);
            BeginFlight(shot, shot.Origin, shot.Velocity.normalized);
        }
#endif
        public void Spawn(PrototypePlayer player, PlayerSnapshot state, double born, uint round)
        {
            var w = GameplayConfig.GetHero(state.HeroId);
            var aim = Quaternion.Euler(state.Pitch, state.Yaw, 0);
            var pivot = state.Position + (player.Presentation != null ? player.Presentation.CameraPivot : Vector3.up * 1.5f);
            var camera = PrototypePlayer.CameraPosition(pivot, aim, player.Presentation);
            var forward = aim * Vector3.forward;
            Vector3 target = camera + forward * 100;
            if (ClosestRay(camera, forward, 100, player.OwnerClientId, out var aimHit)) target = aimHit.point;
            Vector3 muzzle = state.Position + Quaternion.Euler(0, state.Yaw, 0) * player.MuzzleOffset(state.Pitch, state.LastShotMuzzle);
            bool blocked = ClosestRay(pivot, (muzzle - pivot).normalized, (muzzle - pivot).magnitude, player.OwnerClientId, out var wall);
            uint groupSeed = unchecked((uint)state.ShotActionId ^ (uint)(state.ShotActionId >> 32) * 747796405u ^ round * 2891336453u ^ (uint)player.OwnerClientId ^ state.HeroRevision);
            if (groupSeed == 0) groupSeed = 1;
            for (byte pellet = 0; pellet < w.PelletCount; pellet++)
            {
            uint seed = unchecked(++_id * 747796405u + round * 2891336453u + (uint)player.OwnerClientId + 1u);
            if (seed == 0) seed = 1;
            var shot = new InkShot { Id = _id, Round = round, Seed = seed, ShotSequence = state.ShotSequence, Shooter = player.OwnerClientId, HeroId = w.Id, Team = state.Team, Born = born, Origin = blocked ? pivot : muzzle };
            shot.ActionId = state.ShotActionId;
            shot.Lifecycle = state.Revision; shot.HeroRevision = state.HeroRevision;
            shot.MuzzleIndex = state.LastShotMuzzle; shot.PelletIndex = pellet;
            shot.Charge = state.LastShotCharge;
            float spread = state.CurrentSpread > 0 ? state.CurrentSpread : w.SpreadDegrees;
            shot.Velocity = w.PelletCount > 1 ? InkBallistics.PelletVelocity((target - muzzle).normalized, w, spread, pellet, groupSeed)
                : InkBallistics.LaunchVelocity((target - muzzle).normalized, w, ref seed, spread);
            if (WeaponSimulation.IsCharge(w)) shot.Velocity = shot.Velocity.normalized * WeaponSimulation.Speed(w, shot.Charge);
            Spawned.Add(shot);
            if (blocked) Resolve(shot, wall.collider, wall.point, wall.normal, 0);
            else BeginFlight(shot, muzzle, forward);
            }
        }
        private void BeginFlight(InkShot shot, Vector3 muzzle, Vector3 forward)
        {
            if (shot.PelletIndex == 0) PaintTrail(muzzle - forward * .6f, shot, GameplayConfig.GetHero(shot.HeroId));
            _active.Add(new Active { Shot = shot, SimulatedUntil = shot.Born, LastTrail = muzzle });
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, 0, muzzle);
#endif
        }
        private bool ClosestRay(Vector3 origin, Vector3 direction, float distance, ulong shooter, out RaycastHit closest)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
            closest = default; float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
                if (Valid(_hits[i].collider, shooter) && _hits[i].distance < nearest) { nearest = _hits[i].distance; closest = _hits[i]; }
            return nearest < float.MaxValue;
        }
        private static bool Valid(Collider c, ulong shooter)
        {
            var p = c.GetComponentInParent<PrototypePlayer>();
            return p == null || (p.OwnerClientId != shooter && p.Snapshot.Value.Health > 0);
        }
        public void Simulate(double until)
        {
            double step = 1.0 / GameplayConfig.Global.ProjectileStepRate;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i]; var w = LubanConfigService.Current.Tables.TbHero.Get(a.Shot.HeroId);
                double end = Math.Min(until, a.Shot.Born + w.Lifetime); bool hit = false;
                while (a.SimulatedUntil < end - 1e-8)
                {
                    double next = Math.Min(a.SimulatedUntil + step, end);
                    Vector3 from = InkBallistics.Position(a.Shot.Origin, a.Shot.Velocity, w, a.SimulatedUntil - a.Shot.Born);
                    Vector3 to = InkBallistics.Position(a.Shot.Origin, a.Shot.Velocity, w, next - a.Shot.Born);
                    int overlaps = Physics.OverlapSphereNonAlloc(from, w.CollisionRadius, _overlaps, ~0, QueryTriggerInteraction.Ignore);
                    for (int n = 0; n < overlaps; n++)
                    {
                        if (!Valid(_overlaps[n], a.Shot.Shooter)) continue;
                        Vector3 point = _overlaps[n].ClosestPoint(from);
                        Vector3 normal = (from - point).sqrMagnitude > 1e-8f ? (from - point).normalized : -(to - from).normalized;
                        Resolve(a.Shot, _overlaps[n], point, normal, a.SimulatedUntil - a.Shot.Born); hit = true; break;
                    }
                    if (hit) break;
                    Vector3 delta = to - from; float distance = delta.magnitude;
                    int count = Physics.SphereCastNonAlloc(from, w.CollisionRadius, delta.normalized, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
                    float nearest = float.MaxValue; int index = -1;
                    for (int n = 0; n < count; n++)
                        if (Valid(_hits[n].collider, a.Shot.Shooter) && _hits[n].distance < nearest) { nearest = _hits[n].distance; index = n; }
                    if (index >= 0) { var h = _hits[index]; Resolve(a.Shot, h.collider, h.point, h.normal, a.SimulatedUntil - a.Shot.Born + (next - a.SimulatedUntil) * h.distance / Mathf.Max(.0001f, distance)); hit = true; break; }
#if UNITY_EDITOR
                    TraceObserved?.Invoke(a.Shot, next - a.Shot.Born, to);
#endif
                    if (Vector3.Distance(a.LastTrail, to) >= w.TrailSpacing) { PaintTrail(to, a.Shot, w); a.LastTrail = to; }
                    a.SimulatedUntil = next;
                }
                if (hit || end >= a.Shot.Born + w.Lifetime - 1e-8)
                {
                    if (!hit) Impacts.Add(new InkImpact { Id = a.Shot.Id, Round = a.Shot.Round, Team = a.Shot.Team, Position = InkBallistics.Position(a.Shot.Origin, a.Shot.Velocity, w, w.Lifetime), Hit = false });
                    _active.RemoveAt(i);
                }
                else _active[i] = a;
            }
        }
        private void PaintTrail(Vector3 position, InkShot shot, cfg.HeroConfig w)
        {
            bool enabled = PrototypeMatch.Current != null;
#if UNITY_EDITOR
            enabled |= PaintObserved != null;
#endif
            if (!enabled || !Physics.Raycast(position, Vector3.down, out var h, w.TrailMaxDrop, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return;
            var surface = h.collider.GetComponentInParent<PaintSurface>();
            if (surface != null) ApplyPaint(surface, shot, h.point, h.normal, w.TrailRadius, w);
        }
        private void ApplyPaint(PaintSurface surface, InkShot shot, Vector3 point, Vector3 normal, float radius, cfg.HeroConfig w)
        {
#if UNITY_EDITOR
            PaintObserved?.Invoke(new PaintStamp { Round = shot.Round, SurfaceId = surface.SurfaceId, Team = shot.Team,
                Position = point, Normal = normal, Radius = radius, Hardness = w.PaintHardness, Strength = w.PaintStrength });
#endif
            if (PrototypeMatch.Current != null)
                PrototypeMatch.Current.Paint(surface, point, normal, radius, shot.Team, w.PaintHardness, w.PaintStrength);
        }
        private void Resolve(InkShot shot, Collider collider, Vector3 point, Vector3 normal, double age)
        {
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, age, point);
#endif
            var w = LubanConfigService.Current.Tables.TbHero.Get(shot.HeroId);
            var victim = collider.GetComponentInParent<PrototypePlayer>();
            float actualDamage = 0; bool killed = false;
            if (victim != null)
            {
                float before = victim.Snapshot.Value.Health;
                if (shot.Velocity.magnitude * InkBallistics.TravelTime(w, age) <= WeaponSimulation.Range(w, shot.Charge))
                    victim.ReceiveDamage(shot.Team, WeaponSimulation.Damage(w, age, shot.Charge), shot.Velocity);
                actualDamage = before - victim.Snapshot.Value.Health; killed = actualDamage > 0 && victim.Snapshot.Value.Health <= 0;
            }
            else
            {
                var surface = collider.GetComponentInParent<PaintSurface>();
                if (surface != null)
                {
                    uint seed = shot.Seed;
                    ApplyPaint(surface, shot, point, normal, Mathf.Lerp(w.PaintRadiusMin, w.PaintRadiusMax, InkBallistics.Random01(ref seed)), w);
                }
            }
            Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Team = shot.Team, Position = point, Normal = normal, Hit = true,
                ActionId = shot.ActionId, Lifecycle = shot.Lifecycle, HeroRevision = shot.HeroRevision, PelletIndex = shot.PelletIndex,
                Shooter = shot.Shooter, Victim = victim != null ? victim.OwnerClientId : 0, Damage = actualDamage, Killed = killed });
        }
        public InkShot[] LiveShots() => _active.ConvertAll(a => a.Shot).ToArray();
        public void Clear() { _active.Clear(); Spawned.Clear(); Impacts.Clear(); }
    }
}
