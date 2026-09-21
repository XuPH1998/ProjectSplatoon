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

        public static GraphicsFormat SelectVisualFormat(Func<GraphicsFormat, bool> supported = null)
        {
            supported ??= f => SystemInfo.IsFormatSupported(f, GraphicsFormatUsage.Render | GraphicsFormatUsage.Sample | GraphicsFormatUsage.Linear | GraphicsFormatUsage.ReadPixels);
            foreach (var f in new[] { GraphicsFormat.R8G8_UNorm, GraphicsFormat.R8G8B8A8_UNorm }) if (supported(f)) return f;
            throw new NotSupportedException("当前设备无法存储和读回墨迹细节");
        }

        // Coverage/display + occupancy + visual state and paired GPU checkpoint per surface;
        // coverage/visual scratch shared per distinct size. No mipmaps or MSAA.
        public static long PeakBytes(IEnumerable<PaintSurface> surfaces, int islandBytesPerPixel = 2, int visualBytesPerPixel = 2)
        {
            long bytes = 0;
            var sizes = new HashSet<Vector2Int>();
            foreach (var surface in surfaces)
            {
                bytes += (long)surface.Resolution * surface.Height * (12 + islandBytesPerPixel + 2 * visualBytesPerPixel);
                sizes.Add(new Vector2Int(surface.Resolution, surface.Height));
            }
            foreach (var size in sizes) bytes += (long)size.x * size.y * (4 + visualBytesPerPixel);
            return bytes;
        }
    }
}
