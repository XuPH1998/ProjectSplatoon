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
        public uint Id, Round, Seed;
        public ulong Shooter;
        public int WeaponId;
        public byte Team;
        public double Born;
        public Vector3 Origin, Velocity;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Seed); s.SerializeValue(ref Shooter);
            s.SerializeValue(ref WeaponId); s.SerializeValue(ref Team); s.SerializeValue(ref Born); s.SerializeValue(ref Origin); s.SerializeValue(ref Velocity);
        }
    }
    public struct InkImpact : INetworkSerializable
    {
        public uint Id, Round;
        public byte Team;
        public Vector3 Position, Normal;
        public bool Hit;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Team); s.SerializeValue(ref Position); s.SerializeValue(ref Normal); s.SerializeValue(ref Hit); }
    }
    public static class InkBallistics
    {
        public static Vector3 Position(Vector3 origin, Vector3 velocity, float gravity, double age)
        { float t = (float)age; return origin + velocity * t + Vector3.down * (.5f * gravity * t * t); }
        public static float Random01(ref uint seed)
        { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / 16777216f; }
        public static Vector3 LaunchVelocity(Vector3 direction, cfg.WeaponConfig w, ref uint seed)
        {
            float radius = Mathf.Sqrt(Random01(ref seed)) * Mathf.Tan(w.SpreadDegrees * Mathf.Deg2Rad);
            float angle = Random01(ref seed) * Mathf.PI * 2;
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized;
            return Quaternion.LookRotation(direction) * local * Mathf.Lerp(w.SpeedMin, w.SpeedMax, Random01(ref seed));
        }
    }
    /// <summary>Server-only continuous collision simulation. Presentation never reports hits.</summary>
    public sealed class InkProjectileService
    {
        private struct Active { public InkShot Shot; public double SimulatedUntil; }
        private readonly List<Active> _active = new(256);
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Collider[] _overlaps = new Collider[64];
        private uint _id;
        public readonly List<InkShot> Spawned = new(16);
        public readonly List<InkImpact> Impacts = new(32);
        public int ActiveCount => _active.Count;
        public void Spawn(PrototypePlayer player, PlayerSnapshot state, double born, uint round)
        {
            var w = GameplayConfig.Weapon;
            var aim = Quaternion.Euler(state.Pitch, state.Yaw, 0);
            var pivot = state.Position + Vector3.up * 1.5f;
            var camera = PrototypePlayer.CameraPosition(pivot, aim);
            var forward = aim * Vector3.forward;
            Vector3 target = camera + forward * 100;
            if (ClosestRay(camera, forward, 100, player.OwnerClientId, out var aimHit)) target = aimHit.point;
            Vector3 muzzle = state.Position + Quaternion.Euler(0, state.Yaw, 0) * player.MuzzleOffset(state.Pitch);
            bool blocked = ClosestRay(pivot, (muzzle - pivot).normalized, (muzzle - pivot).magnitude, player.OwnerClientId, out var wall);
            uint seed = unchecked(++_id * 747796405u + round * 2891336453u + (uint)player.OwnerClientId + 1u);
            if (seed == 0) seed = 1;
            var shot = new InkShot { Id = _id, Round = round, Seed = seed, Shooter = player.OwnerClientId, WeaponId = w.Id, Team = state.Team, Born = born, Origin = blocked ? pivot : muzzle };
            shot.Velocity = InkBallistics.LaunchVelocity((target - muzzle).normalized, w, ref seed);
            Spawned.Add(shot);
            if (blocked) Resolve(shot, wall.collider, wall.point, wall.normal);
            else _active.Add(new Active { Shot = shot, SimulatedUntil = born });
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
                var a = _active[i]; var w = LubanConfigService.Current.Tables.TbWeapon.Get(a.Shot.WeaponId);
                double end = Math.Min(until, a.Shot.Born + w.Lifetime); bool hit = false;
                while (a.SimulatedUntil < end - 1e-8)
                {
                    double next = Math.Min(a.SimulatedUntil + step, end);
                    Vector3 from = InkBallistics.Position(a.Shot.Origin, a.Shot.Velocity, w.Gravity, a.SimulatedUntil - a.Shot.Born);
                    Vector3 to = InkBallistics.Position(a.Shot.Origin, a.Shot.Velocity, w.Gravity, next - a.Shot.Born);
                    int overlaps = Physics.OverlapSphereNonAlloc(from, w.CollisionRadius, _overlaps, ~0, QueryTriggerInteraction.Ignore);
                    for (int n = 0; n < overlaps; n++)
                    {
                        if (!Valid(_overlaps[n], a.Shot.Shooter)) continue;
                        Vector3 point = _overlaps[n].ClosestPoint(from);
                        Vector3 normal = (from - point).sqrMagnitude > 1e-8f ? (from - point).normalized : -(to - from).normalized;
                        Resolve(a.Shot, _overlaps[n], point, normal); hit = true; break;
                    }
                    if (hit) break;
                    Vector3 delta = to - from; float distance = delta.magnitude;
                    int count = Physics.SphereCastNonAlloc(from, w.CollisionRadius, delta.normalized, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
                    float nearest = float.MaxValue; int index = -1;
                    for (int n = 0; n < count; n++)
                        if (Valid(_hits[n].collider, a.Shot.Shooter) && _hits[n].distance < nearest) { nearest = _hits[n].distance; index = n; }
                    if (index >= 0) { var h = _hits[index]; Resolve(a.Shot, h.collider, h.point, h.normal); hit = true; break; }
                    a.SimulatedUntil = next;
                }
                if (hit || end >= a.Shot.Born + w.Lifetime - 1e-8)
                {
                    if (!hit) Impacts.Add(new InkImpact { Id = a.Shot.Id, Round = a.Shot.Round, Team = a.Shot.Team, Position = InkBallistics.Position(a.Shot.Origin, a.Shot.Velocity, w.Gravity, w.Lifetime), Hit = false });
                    _active.RemoveAt(i);
                }
                else _active[i] = a;
            }
        }
        private void Resolve(InkShot shot, Collider collider, Vector3 point, Vector3 normal)
        {
            var w = LubanConfigService.Current.Tables.TbWeapon.Get(shot.WeaponId);
            var victim = collider.GetComponentInParent<PrototypePlayer>();
            if (victim != null) victim.ReceiveDamage(shot.Team, w.Damage);
            else
            {
                var surface = collider.GetComponentInParent<PaintSurface>();
                if (surface != null)
                {
                    uint seed = shot.Seed;
                    PrototypeMatch.Current.Paint(surface, point, normal, Mathf.Lerp(w.PaintRadiusMin, w.PaintRadiusMax, InkBallistics.Random01(ref seed)), shot.Team, w.PaintHardness, w.PaintStrength);
                }
            }
            Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Team = shot.Team, Position = point, Normal = normal, Hit = true });
        }
        public InkShot[] LiveShots() => _active.ConvertAll(a => a.Shot).ToArray();
        public void Clear() { _active.Clear(); Spawned.Clear(); Impacts.Clear(); }
    }
}
