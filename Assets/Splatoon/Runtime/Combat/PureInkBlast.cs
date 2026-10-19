using System.Collections.Generic;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Surface-only spherical paint. Deliberately has no projectile/object/player damage path.</summary>
    public static class PureInkBlast
    {
        static readonly TpsAimSolver Aim = new();
        public static void Paint(Vector3 origin, float radius, byte team, uint seed)
        {
            var match = PrototypeMatch.Current;
            if (match == null || !match.IsServer || radius <= 0 || (team != 1 && team != 2)) return;
            foreach (var site in Collect(origin, radius))
                match.Paint(site.Surface, site.Point, site.Normal, site.Radius, team, .7f, 1, seed,
                    clipEnabled: true, clip0: site.Clip0, clip1: site.Clip1);
        }
        public struct Site
        {
            public PaintSurface Surface;
            public Vector3 Point, Normal;
            public float Radius;
            public Vector4 Clip0, Clip1;
        }
        public static List<Site> Collect(Vector3 origin, float radius)
        {
            var sites = new List<Site>();
            for (int ray = -2; ray < 64; ray++)
            {
                float y = 1 - 2 * (ray + .5f) / 64, angle = ray * 2.39996323f, r = Mathf.Sqrt(Mathf.Max(0, 1 - y * y));
                Vector3 direction = ray == -2 ? Vector3.down : ray == -1 ? Vector3.up : new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r);
                if (!Aim.ClosestCast(origin, direction, radius, 0, 0, out var hit, false)) continue;
                var surface = hit.Collider?.GetComponentInParent<PaintSurface>(); if (surface == null) continue;
                bool duplicate = false;
                foreach (var prev in sites)
                    if (prev.Surface == surface && Vector3.Dot(prev.Normal, hit.Normal) > .999f && Mathf.Abs(Vector3.Dot(prev.Point - hit.Point, hit.Normal)) < .025f) { duplicate = true; break; }
                if (duplicate) continue;
                float offset = Vector3.Dot(origin - hit.Point, hit.Normal);
                float footprint = Mathf.Sqrt(Mathf.Max(0, radius * radius - offset * offset)); if (footprint < .001f) continue;
                var point = origin - hit.Normal * offset;
                if (!Aim.ClosestCast(point + hit.Normal * .025f, -hit.Normal, .075f, 0, 0, out var projected, false) || projected.Collider?.GetComponentInParent<PaintSurface>() != surface) point = hit.Point;
                var site = new Site { Surface = surface, Point = point, Normal = hit.Normal, Radius = footprint };
                InkShapeAtlas.StampBasis(new PaintStamp { Position = point, Normal = hit.Normal }, out var tangent, out var bitangent);
                Vector3 center = point + hit.Normal * .025f;
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4, extent = footprint; var radial = tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a);
                    if (Aim.ClosestCast(center, radial, extent, 0, 0, out var edge, false)) extent = Mathf.Max(0, edge.Distance - .025f);
                    if (!Visible(origin, center + radial * extent))
                    {
                        float lo = 0, hi = extent;
                        for (int k = 0; k < 10; k++) { float mid = (lo + hi) * .5f; if (Visible(origin, center + radial * mid)) lo = mid; else hi = mid; }
                        extent = Mathf.Max(0, lo - .025f);
                    }
                    if (i < 4) site.Clip0[i] = extent; else site.Clip1[i - 4] = extent;
                }
                sites.Add(site);
            }
            return sites;
        }
        static bool Visible(Vector3 from, Vector3 to)
        { var delta = to - from; return !Aim.ClosestCast(from, delta, Mathf.Max(0, delta.magnitude - .03f), 0, 0, out _, false); }
    }
}
