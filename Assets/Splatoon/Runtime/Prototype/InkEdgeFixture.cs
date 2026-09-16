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
        }
    }
}
#endif
