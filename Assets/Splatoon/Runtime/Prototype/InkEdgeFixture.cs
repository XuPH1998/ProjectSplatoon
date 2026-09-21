#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;
using Splatoon.Painting;

namespace Splatoon.Prototype
{
    // Explicit graphics/network validation fixture. Never called by ordinary gameplay.
    public static class InkEdgeFixture
    {
        public static IEnumerable<PaintStamp> Stamps(PrototypeArena arena)
        {
            uint ordinal = 0;
            foreach (var surface in arena.Surfaces.Values)
            foreach (var region in surface.GameplayRegions)
            {
                var matrix = region.Matrix(surface);
                for (int i = 0; i < 4; i++)
                    yield return new PaintStamp { SurfaceId = surface.SurfaceId, Position = matrix.MultiplyPoint3x4(new Vector3((i % 2 - .5f) * Mathf.Min(1, region.Size.x * .3f), 0, (i / 2 - .5f) * Mathf.Min(.8f, region.Size.y * .3f))),
                        Normal = matrix.MultiplyVector(Vector3.up).normalized, Radius = 1.2f, Hardness = .55f, Strength = 1, Team = (byte)(i == 3 ? 2 : 1), ShapeSeed = InkShapeAtlas.Pack((int)(ordinal % 32), (++ordinal * 7919) << 6) };
            }
            // Exercise replay and checkpoint restoration on both ends of the actual ramps.
            foreach (var surface in arena.Surfaces.Values)
            {
                if (!surface.Scores || surface.transform.up.y < .7071f || surface.transform.up.y > .9999f) continue;
                foreach (int end in new[] { -1, 1 })
                    yield return new PaintStamp { SurfaceId = surface.SurfaceId,
                        Position = surface.transform.TransformPoint(new Vector3(0, 0, end * (surface.WalkableSize.y / 2 - .1f))),
                        Normal = surface.transform.up, Radius = 1.2f, Hardness = .55f, Strength = 1, Team = 1,
                        ShapeSeed = InkShapeAtlas.Pack((int)(ordinal % 32), (++ordinal * 7919) << 6) };
            }
        }
    }
}
#endif
