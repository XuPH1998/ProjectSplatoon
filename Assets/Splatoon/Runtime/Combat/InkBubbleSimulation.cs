using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Combat
{
    public struct InkBounce : INetworkSerializable
    {
        public uint Id, Round, Sequence;
        public int GroundBounces;
        public double Time;
        public Vector3 Position, Velocity, Normal;
        public float Travelled;
        public static InkBounce Initial(InkShot s) => new() { Id = s.Id, Round = s.Round, Time = s.Born, Position = s.Origin, Velocity = s.Velocity };
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Sequence); s.SerializeValue(ref GroundBounces);
            s.SerializeValue(ref Time); s.SerializeValue(ref Position); s.SerializeValue(ref Velocity); s.SerializeValue(ref Normal); s.SerializeValue(ref Travelled);
        }
        public Vector3 PositionAt(double time, float gravity) => InkBallistics.Position(Position, Velocity, gravity, Math.Max(0, time - Time));
        public Vector3 VelocityAt(double time, float gravity) => Velocity + Vector3.down * (gravity * (float)Math.Max(0, time - Time));
    }

    public struct InkBubbleState : INetworkSerializable
    {
        public InkShot Shot;
        public InkBounce Segment;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Shot); s.SerializeValue(ref Segment); }
    }

    public sealed partial class InkProjectileService
    {
        public readonly List<InkBounce> Bounces = new(64);
        public void CaptureBubbles(List<InkBubbleState> destination)
        {
            destination.Clear();
            foreach (var a in _active)
                if (a.Shot.Configuration.MotionMode == ProjectileMotionMode.BouncingBubble)
                    destination.Add(new InkBubbleState { Shot = a.Shot, Segment = a.Bubble });
        }

        public static Vector3 ReflectBubble(Vector3 velocity, Vector3 normal, WeaponRuntimeConfig w, out bool ground)
        {
            normal.Normalize(); ground = Vector3.Dot(normal, Vector3.up) >= .7071067f;
            var normalVelocity = normal * Vector3.Dot(velocity, normal);
            return ground ? (velocity - normalVelocity) * w.BubbleTangentRetention - normalVelocity * w.BubbleNormalRetention
                : Vector3.Reflect(velocity, normal) * w.BubbleWallRetention;
        }

        bool SimulateBubble(ref Active a, double until, double step)
        {
            var w = a.Shot.Configuration;
            double expires = a.Shot.Born + w.Lifetime, end = Math.Min(until, expires);
            while (a.SimulatedUntil < end - 1e-8)
            {
                double next = Math.Min(a.SimulatedUntil + step, expires);
                if (next > end + 1e-8) break;
                double cursor = a.SimulatedUntil;
                int contacts = 0;
                while (cursor < next - 1e-8)
                {
                    Vector3 from = a.Bubble.PositionAt(cursor, w.ProjectileGravity);
                    Vector3 to = a.Bubble.PositionAt(next, w.ProjectileGravity);
                    Vector3 delta = to - from; float distance = delta.magnitude;
                    float remainingRange = Mathf.Max(0, w.EffectiveRange - a.Bubble.Travelled);
                    double traceEnd = next;
                    bool atRange = distance >= remainingRange;
                    if (atRange && distance > .000001f)
                    { traceEnd = cursor + (next - cursor) * remainingRange / distance; delta *= remainingRange / distance; distance = remainingRange; to = from + delta; }
                    var velocity = a.Bubble.VelocityAt(cursor, w.ProjectileGravity);
                    bool contact = _aim.Overlap(from, w.CollisionRadius, a.Shot.Shooter, -velocity.normalized, out var hit, null, a.Shot.Team);
                    if (contact) hit.Distance = 0; // Overlap is a contact now, not travel to the surface.
                    if (!contact) contact = _aim.ClosestCast(from, delta, distance, w.CollisionRadius, a.Shot.Shooter, out hit, null, a.Shot.Team);
                    if (contact)
                    {
                        double time = cursor + (traceEnd - cursor) * Mathf.Clamp01(hit.Distance / Mathf.Max(.000001f, distance));
                        a.Bubble.Travelled += Mathf.Min(distance, hit.Distance);
                        a.BubbleTrailDistance += Mathf.Min(distance, hit.Distance);
                        velocity = a.Bubble.VelocityAt(time, w.ProjectileGravity);
                        if (hit.Collider.GetComponentInParent<PrototypePlayer>() != null)
                        { Resolve(a.Shot, hit.Collider, hit.Point, hit.Normal, time - a.Shot.Born, ref a.PaintOrdinal, velocity); return true; }
                        var reflected = ReflectBubble(velocity, hit.Normal, w, out bool ground);
                        bool terminal = a.Bubble.Sequence >= w.BubbleMaxBounces || ground && a.Bubble.GroundBounces >= w.BubbleGroundBounces ||
                            ++contacts > 8 || Vector3.Dot(velocity, hit.Normal) >= -.0001f || a.Bubble.Travelled >= w.EffectiveRange;
                        if (terminal)
                        { Resolve(a.Shot, hit.Collider, hit.Point, hit.Normal, time - a.Shot.Born, ref a.PaintOrdinal, velocity); return true; }
                        var surface = hit.Collider.GetComponentInParent<PaintSurface>();
                        if (surface != null) ApplyPaint(surface, a.Shot, hit.Point, hit.Normal,
                            Mathf.Lerp(w.PaintRadiusMin, w.PaintRadiusMax, InkBallistics.Random01(ref a.TrailSeed)), w, ++a.PaintOrdinal, true);
                        a.Bubble.Sequence++; if (ground) a.Bubble.GroundBounces++;
                        a.Bubble.Time = time; a.Bubble.Position = hit.Point + hit.Normal * (w.CollisionRadius + .002f);
                        a.Bubble.Velocity = reflected; a.Bubble.Normal = hit.Normal;
                        Bounces.Add(a.Bubble);
                        cursor = Math.Min(next, Math.Max(time, cursor + .000001));
                    }
                    else
                    {
                        a.Bubble.Travelled += distance; a.BubbleTrailDistance += distance;
                        if (a.BubbleTrailDistance >= w.TrailSpacing)
                        { PaintTrail(to, a.Shot, w, ref a.TrailSeed, ref a.PaintOrdinal); a.BubbleTrailDistance %= w.TrailSpacing; }
#if UNITY_EDITOR
                        TraceObserved?.Invoke(a.Shot, traceEnd - a.Shot.Born, to);
#endif
                        if (atRange) { FinishBubble(a, to, traceEnd); return true; }
                        cursor = next;
                    }
                }
                a.SimulatedUntil = next;
            }
            if (end >= expires - 1e-8)
            { FinishBubble(a, a.Bubble.PositionAt(expires, w.ProjectileGravity), expires); return true; }
            return false;
        }
        void FinishBubble(Active a, Vector3 position, double time)
        {
            var s = a.Shot;
            Impacts.Add(new InkImpact { Id = s.Id, Round = s.Round, Team = s.Team, Position = position, Normal = Vector3.up, Time = time,
                Shooter = s.Shooter, ActionId = s.ActionId, Lifecycle = s.Lifecycle, HeroRevision = s.HeroRevision, PelletIndex = s.PelletIndex });
        }
    }
}
