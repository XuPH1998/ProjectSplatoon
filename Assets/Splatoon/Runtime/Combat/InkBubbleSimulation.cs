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
        public Vector3 PositionAt(double time, InkShot shot)
        {
            if (WeaponSimulation.IsFloatingBubble(shot.Configuration)) return InkBallistics.Position(shot, shot.Configuration, Math.Clamp(time - shot.Born, 0, shot.Configuration.Lifetime));
            if (!shot.Configuration.ReferenceRules) return PositionAt(time, shot.Configuration.ProjectileGravity);
            ReferenceBallistics.Evaluate(Velocity, shot.Configuration, Math.Max(0, time-Time), Time-shot.Born, out var delta, out _);
            return Position + delta;
        }
        public Vector3 VelocityAt(double time, InkShot shot)
        {
            if (WeaponSimulation.IsFloatingBubble(shot.Configuration)) return InkBallistics.Velocity(shot, shot.Configuration, Math.Clamp(time - shot.Born, 0, shot.Configuration.Lifetime));
            if (!shot.Configuration.ReferenceRules) return VelocityAt(time, shot.Configuration.ProjectileGravity);
            ReferenceBallistics.Evaluate(Velocity, shot.Configuration, Math.Max(0, time-Time), Time-shot.Born, out _, out var velocity);
            return velocity;
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
                if (WeaponSimulation.UsesBubbleMesh(a.Shot.Configuration))
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
                    Vector3 from = a.Bubble.PositionAt(cursor, a.Shot);
                    Vector3 to = a.Bubble.PositionAt(next, a.Shot);
                    Vector3 delta = to - from; float distance = delta.magnitude;
                    float remainingRange = Mathf.Max(0, w.EffectiveRange - a.Bubble.Travelled);
                    double traceEnd = next;
                    bool atRange = distance >= remainingRange;
                    if (atRange && distance > .000001f)
                    { traceEnd = cursor + (next - cursor) * remainingRange / distance; delta *= remainingRange / distance; distance = remainingRange; to = from + delta; }
                    var velocity = a.Bubble.VelocityAt(cursor, a.Shot);
                    float fieldRadius = ReferenceBallistics.BubbleRadius(a.Shot, traceEnd-a.Shot.Born, (int)a.Bubble.Sequence, false);
                    float playerRadius = ReferenceBallistics.BubbleRadius(a.Shot, traceEnd-a.Shot.Born, (int)a.Bubble.Sequence, true);
                    bool worldOverlap = _aim.Overlap(from, fieldRadius, a.Shot.Shooter, -velocity.normalized, out var world, false);
                    bool playerOverlap = _aim.Overlap(from, playerRadius, a.Shot.Shooter, -velocity.normalized, out var player, true, a.Shot.Team);
                    bool contact = worldOverlap || playerOverlap;
                    var hit = worldOverlap ? world : player;
                    if (contact) hit.Distance = 0;
                    else
                    {
                        bool worldHit = _aim.ClosestCast(from, delta, distance, fieldRadius, a.Shot.Shooter, out world, false);
                        bool playerHit = _aim.ClosestCast(from, delta, distance, playerRadius, a.Shot.Shooter, out player, true, a.Shot.Team);
                        contact = worldHit || playerHit;
                        hit = worldHit && (!playerHit || world.Distance <= player.Distance) ? world : player;
                    }
                    if (contact)
                    {
                        double time = cursor + (traceEnd - cursor) * Mathf.Clamp01(hit.Distance / Mathf.Max(.000001f, distance));
                        if (w.ReferenceRules) PaintReferenceTrail(ref a, from, from + delta * Mathf.Clamp01(hit.Distance/Mathf.Max(.000001f, distance)), cursor, time);
                        a.Bubble.Travelled += Mathf.Min(distance, hit.Distance);
                        a.BubbleTrailDistance += Mathf.Min(distance, hit.Distance);
                        velocity = a.Bubble.VelocityAt(time, a.Shot);
                        if (hit.Collider.GetComponentInParent<PrototypePlayer>() != null)
                        { Resolve(a.Shot, hit.Collider, hit.Point, hit.Normal, time - a.Shot.Born, ref a.PaintOrdinal, velocity); return true; }
                        var reflected = ReflectBubble(velocity, hit.Normal, w, out bool ground);
                        bool terminal = a.Bubble.Sequence >= w.BubbleMaxBounces || ground && a.Bubble.GroundBounces >= w.BubbleGroundBounces ||
                            ++contacts > 8 || Vector3.Dot(velocity, hit.Normal) >= -.0001f || a.Bubble.Travelled >= w.EffectiveRange;
                        if (terminal)
                        { Resolve(a.Shot, hit.Collider, hit.Point, hit.Normal, time - a.Shot.Born, ref a.PaintOrdinal, velocity); return true; }
                        var surface = hit.Collider.GetComponentInParent<PaintSurface>();
                        if (surface != null) ApplyPaint(surface, a.Shot, hit.Point, hit.Normal,
                            w.ReferenceRules ? BubbleBouncePaintRadius(a.Shot, (int)a.Bubble.Sequence) : Mathf.Lerp(w.PaintRadiusMin, w.PaintRadiusMax, InkBallistics.Random01(ref a.TrailSeed)),
                            w, ++a.PaintOrdinal, true, null, w.ReferenceRules ? velocity : Vector3.zero, w.ReferenceRules ? w.PaintDepthMax : 1);
                        a.Bubble.Sequence++; if (ground) a.Bubble.GroundBounces++;
                        // The sweep uses the interval-end growing radius. Place the rebound
                        // outside that same envelope so growth cannot cause an immediate
                        // separating overlap to be mistaken for a second terminal impact.
                        a.Bubble.Time = time; a.Bubble.Position = hit.Point + hit.Normal * (ReferenceBallistics.BubbleRadius(a.Shot, traceEnd-a.Shot.Born, (int)a.Bubble.Sequence, false) + .002f);
                        a.Bubble.Velocity = reflected; a.Bubble.Normal = hit.Normal;
                        Bounces.Add(a.Bubble);
                        cursor = Math.Min(next, Math.Max(time, cursor + .000001));
                    }
                    else
                    {
                        a.Bubble.Travelled += distance; a.BubbleTrailDistance += distance;
                        if (w.ReferenceRules) PaintReferenceTrail(ref a, from, to, cursor, traceEnd);
                        else if (a.BubbleTrailDistance >= w.TrailSpacing)
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
            { FinishBubble(a, a.Bubble.PositionAt(expires, a.Shot), expires); return true; }
            return false;
        }
        void FinishBubble(Active a, Vector3 position, double time)
        {
            var s = a.Shot;
            if (s.Configuration.ReferenceRules)
                QueuePaintDrop(s, position, time, s.VolleyIndex == 0 ? s.Configuration.PaintRadiusMax : s.Configuration.BubbleLaterImpactRadius,
                    a.PaintOrdinal + 1, a.Bubble.VelocityAt(time, s), s.Configuration.PaintDepthMax);
            Impacts.Add(new InkImpact { Id = s.Id, Round = s.Round, Team = s.Team, Position = position, Normal = Vector3.up, Time = time,
                Shooter = s.Shooter, ActionId = s.ActionId, Lifecycle = s.Lifecycle, HeroRevision = s.HeroRevision, PelletIndex = s.PelletIndex });
        }
    }
}
