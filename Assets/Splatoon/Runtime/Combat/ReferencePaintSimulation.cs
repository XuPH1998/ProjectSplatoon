using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Painting;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed partial class InkProjectileService
    {
        struct PaintDrop
        {
            public InkShot Shot;
            public Vector3 Origin, Direction, Velocity;
            public float Gravity;
            public double Born, SimulatedUntil;
            public float Radius, Depth;
            public uint Ordinal;
            public bool ScaleDepthWithFall;
        }
        struct WallDrop
        {
            public InkShot Shot;
            public PaintSurface Surface;
            public Vector3 Origin, Normal;
            public double Born, FirstSeconds, LastSeconds;
            public float Distance;
            public uint Ordinal;
        }
        readonly List<PaintDrop> _paintDrops = new(128);
        readonly List<WallDrop> _wallDrops = new(32);

        void QueuePaintDrop(InkShot shot, Vector3 origin, double born, float radius, uint ordinal, Vector3 direction, float depth, Vector3 velocity = default, float gravity = -1, bool scaleDepthWithFall = false)
        {
            if (radius <= 0) return;
            _paintDrops.Add(new PaintDrop { Shot = shot, Origin = origin, Born = born, SimulatedUntil = born,
                Radius = radius, Ordinal = ordinal, Direction = direction, Depth = depth, Velocity = velocity, Gravity = gravity >= 0 ? gravity : shot.Configuration.PaintDropGravity, ScaleDepthWithFall = scaleDepthWithFall });
        }
        void PaintReferenceTrail(ref Active a, Vector3 from, Vector3 to, double start, double end)
        {
            var w = a.Shot.Configuration;
            float length = Vector3.Distance(from, to), reached = a.TrailTravelled + length;
            while (a.TrailCount < a.TrailBudget && a.NextTrailDistance <= reached + .00001f)
            {
                float fraction = length > .000001f ? Mathf.Clamp01((a.NextTrailDistance - a.TrailTravelled) / length) : 0;
                uint dropSeed = w.DetailedPaint ? WeaponLaunch.Stream(a.Shot.Seed, WeaponLaunch.DropStream, (uint)a.TrailCount) : a.TrailSeed;
                Vector3 dropVelocity = w.UsesDetailedPaint ? ShooterDetailSimulation.SplashVelocity(a.Shot.Velocity, w, ref dropSeed) : Vector3.zero;
                QueuePaintDrop(a.Shot, Vector3.Lerp(from, to, fraction), start + (end - start) * fraction,
                    Mathf.Lerp(w.TrailRadiusMin, w.TrailRadiusMax, InkBallistics.Random01(ref dropSeed)), ++a.PaintOrdinal, a.Shot.Velocity, w.TrailDepthScale, dropVelocity, scaleDepthWithFall: w.UsesDetailedPaint);
                if (!w.DetailedPaint) a.TrailSeed = dropSeed;
                a.TrailCount++; a.NextTrailDistance += w.TrailSpacing;
            }
            a.TrailTravelled = reached;
        }
        void SimulatePaintDrops(double until)
        {
            const double step = 1.0 / 120;
            for (int i = _paintDrops.Count - 1; i >= 0; i--)
            {
                var d = _paintDrops[i]; var w = d.Shot.Configuration;
                double expires = d.Born + w.PaintDropLifetime, end = Math.Min(until, expires);
                bool done = false;
                while (d.SimulatedUntil < end - 1e-8)
                {
                    double next = Math.Min(expires, d.SimulatedUntil + step);
                    if (next > end + 1e-8) break;
                    double a = d.SimulatedUntil - d.Born, b = next - d.Born;
                    var from = d.Origin + d.Velocity * (float)a + Vector3.down * (float)(.5 * d.Gravity * a * a);
                    var to = d.Origin + d.Velocity * (float)b + Vector3.down * (float)(.5 * d.Gravity * b * b);
                    // Non-paintable geometry blocks the drop too. No teleport through floors.
                    Vector3 rayDirection = w.UsesDetailedPaint ? (to - from).normalized : Vector3.down;
                    if (Physics.Raycast(from - rayDirection * .001f, rayDirection, out var hit, Vector3.Distance(from, to) + .001f,
                        PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore))
                    {
                        var surface = hit.collider.GetComponentInParent<PaintSurface>();
                        if (surface != null) ApplyPaint(surface, d.Shot, hit.point, hit.normal, d.Radius, w, d.Ordinal, false, null, d.Direction,
                            d.ScaleDepthWithFall ? ShooterDetailSimulation.SplashDepth(Mathf.Max(0, d.Origin.y - hit.point.y), w) : d.Depth);
                        done = true; break;
                    }
                    d.SimulatedUntil = next;
                }
                if (done || end >= expires - 1e-8) _paintDrops.RemoveAt(i); else _paintDrops[i] = d;
            }
            for (int i = _wallDrops.Count - 1; i >= 0; i--)
            {
                var d = _wallDrops[i]; var w = d.Shot.Configuration;
                double duration = w.UsesDetailedPaint ? d.FirstSeconds + w.ShooterWallMiddle + d.LastSeconds : w.WallDropSeconds;
                float target = w.UsesDetailedPaint ? ShooterDetailSimulation.WallDistance(until - d.Born, d.FirstSeconds, d.LastSeconds, w)
                    : (float)Math.Min(w.WallDropSeconds, Math.Max(0, until - d.Born)) * w.WallDropSpeed;
                bool done = false;
                // Fixed spatial samples make wall coverage independent of outer frame rate.
                while (d.Distance + .125f <= target + .00001f)
                {
                    Vector3 previous = d.Origin + Vector3.down * d.Distance + d.Normal * .025f;
                    d.Distance += .125f;
                    Vector3 point = d.Origin + Vector3.down * d.Distance + d.Normal * .025f;
                    if (Physics.Raycast(previous, Vector3.down, out var floor, .125f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) && floor.normal.y > .5f)
                    {
                        var surface = floor.collider.GetComponentInParent<PaintSurface>();
                        if (surface != null) ApplyPaint(surface, d.Shot, floor.point, floor.normal, w.WallDropGroundRadius, w, ++d.Ordinal, false);
                        done = true; break;
                    }
                    if (!Physics.Raycast(point, -d.Normal, out var hit, .075f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) ||
                        hit.collider.GetComponentInParent<PaintSurface>() != d.Surface)
                    {
                        double age = w.UsesDetailedPaint ? ShooterDetailSimulation.WallAge(d.Distance, d.FirstSeconds, d.LastSeconds, w) : d.Distance / w.WallDropSpeed;
                        QueuePaintDrop(d.Shot, point, d.Born + age, w.WallDropGroundRadius, ++d.Ordinal, Vector3.down, 1,
                            Vector3.zero, w.UsesDetailedPaint ? w.ShooterWallGravity : -1); done = true; break;
                    }
                    ApplyPaint(d.Surface, d.Shot, hit.point, hit.normal, w.WallDropRadius, w, ++d.Ordinal, false, null, Vector3.down, 1.2f);
                }
                if (done || until >= d.Born + duration) _wallDrops.RemoveAt(i); else _wallDrops[i] = d;
            }
        }
        void PaintReferenceImpact(PaintSurface surface, InkShot shot, Vector3 point, Vector3 normal, Vector3 incoming, double age, ref uint ordinal)
        {
            var w = shot.Configuration;
            // The blaster's zero-width ordinary impact is painted by its explosion only.
            if (w.DetailedPaint && WeaponSimulation.IsBlaster(w)) return;
            float distance = new Vector2(point.x - shot.Origin.x, point.z - shot.Origin.z).magnitude;
            float fraction = Mathf.InverseLerp(w.PaintDistanceMiddle, w.PaintDistanceFar, distance);
            float radius = Mathf.Lerp(w.PaintRadiusMax, w.PaintRadiusMin, fraction);
            if (WeaponSimulation.IsBubble(w) && shot.VolleyIndex > 0) radius = w.BubbleLaterImpactRadius;
            bool falling = shot.Origin.y - point.y >= w.PaintBreakHeight;
            float depth = Mathf.Lerp(falling ? w.PaintDepthBreakMax : w.PaintDepthMax, falling ? w.PaintDepthBreakMin : w.PaintDepthMin, fraction);
            if (w.UsesDetailedPaint)
            {
                radius = ShooterDetailSimulation.ImpactRadius(distance, w);
                depth = ShooterDetailSimulation.ImpactDepth(incoming, normal, Mathf.Max(0, shot.Origin.y - point.y), age, w);
                if (Mathf.Abs(normal.y) < .5f) { radius = w.ShooterWallShockRadius; depth = 1; }
            }
            ApplyPaint(surface, shot, point, normal, radius, w, ++ordinal, true, null, incoming, depth);
            QueueReferenceWall(surface, shot, point, normal, age, 0x10000u + ordinal * 4096);
        }
        void QueueReferenceWall(PaintSurface surface, InkShot shot, Vector3 point, Vector3 normal, double age, uint wallOrdinal)
        {
            var w = shot.Configuration;
            if (Mathf.Abs(normal.y) < .5f && w.WallDropRadius > 0 && (w.UsesDetailedPaint || w.WallDropSeconds > 0) && w.WallDropSpeed > 0)
            {
                double first = 0, last = 0;
                if (w.UsesDetailedPaint) ShooterDetailSimulation.WallTiming(shot, wallOrdinal, out first, out last);
                _wallDrops.Add(new WallDrop { Shot = shot, Surface = surface, Origin = point, Normal = normal, Born = shot.Born + age, Ordinal = wallOrdinal, FirstSeconds = first, LastSeconds = last });
            }
        }
        float BubbleBouncePaintRadius(InkShot shot, int bounce)
        {
            var w = shot.Configuration;
            float first = shot.VolleyIndex == 0 ? w.BubbleFirstBouncePaintRadius : w.BubbleLaterBouncePaintRadius - (shot.VolleyIndex - 1) * w.BubbleBouncePaintDecrement;
            return first * Mathf.Pow(w.BubbleBouncePaintRate, bounce);
        }
    }
}
