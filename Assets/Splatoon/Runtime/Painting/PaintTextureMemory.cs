using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Splatoon.Painting
{
    public static class PaintTextureMemory
    {
        public static bool SupportsIslandFormat(GraphicsFormat format) => SystemInfo.IsFormatSupported(format,
            GraphicsFormatUsage.Render | GraphicsFormatUsage.Sample | GraphicsFormatUsage.Linear);

        public static GraphicsFormat SelectIslandFormat(Func<GraphicsFormat, bool> supported = null)
        {
            supported ??= SupportsIslandFormat;
            foreach (var format in new[] { GraphicsFormat.R8_UNorm, GraphicsFormat.R16_UNorm, GraphicsFormat.R16_SFloat })
                if (supported(format)) return format;
            throw new NotSupportedException("当前设备不支持可渲染、可过滤的单通道涂色辅助纹理");
        }

        public static long Bytes(RenderTexture texture) =>
            (long)GraphicsFormatUtility.ComputeMipmapSize(texture.width, texture.height, texture.graphicsFormat);

        // Two RGBA targets, one occupancy target, one worst-case RGBA checkpoint per surface,
        // plus one shared RGBA scratch per distinct size. No mipmaps or MSAA are used.
        public static long PeakBytes(IEnumerable<PaintSurface> surfaces, int islandBytesPerPixel = 2)
        {
            long bytes = 0;
            var sizes = new HashSet<Vector2Int>();
            foreach (var surface in surfaces)
            {
                bytes += (long)surface.Resolution * surface.Height * (12 + islandBytesPerPixel);
                sizes.Add(new Vector2Int(surface.Resolution, surface.Height));
            }
            foreach (var size in sizes) bytes += (long)size.x * size.y * 4;
            return bytes;
        }
    }
}
