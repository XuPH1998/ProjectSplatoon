using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Splatoon.Painting
{
    /// <summary>Versioned, shared alpha data and decoded appearance for CPU and GPU painting.</summary>
    public static class InkShapeAtlas
    {
        public const int Columns = 8, Rows = 4, Count = Columns * Rows, CellSize = 256;
        public const int Width = Columns * CellSize, Height = Rows * CellSize, SamplingVersion = 2;
        public const string AssetPath = "Assets/GameResource/Effects/Ink/Textures/InkSplatAtlas-32.png";
        private static Texture2D _texture;
        private static byte[] _alpha;
        private static string _contentHash;
        public static Texture2D Texture => _texture;
        public static string ContentHash => _texture != null && _alpha != null ? _contentHash
            : throw new InvalidOperationException("落墨图集未初始化，不能生成游戏内容签名");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset() { _texture = null; _alpha = null; _contentHash = null; }
        public static void Configure(Texture2D texture, bool reload = false)
        {
            if (texture == null) throw new InvalidOperationException("PaintSurface.ShapeAtlas 未绑定 32 款落墨图集");
            if (texture.width != Width || texture.height != Height || !texture.isReadable || texture.mipmapCount != 1
                || texture.isDataSRGB || texture.filterMode != FilterMode.Bilinear || texture.wrapMode != TextureWrapMode.Clamp)
                throw new InvalidOperationException($"落墨图集必须为 {Width}x{Height}、可读、Linear、无 Mipmap、Bilinear、Clamp：{texture.name}");
            if (!reload && _texture == texture && _alpha != null) return;
            if (_texture != null && _texture != texture) throw new InvalidOperationException("表面必须使用同一张共享落墨图集");
            var pixels = texture.GetPixels32(); var alpha = new byte[pixels.Length];
            var occupied = new bool[Count];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                byte a = pixels[y * Width + x].a; alpha[y * Width + x] = a;
                int lx = x % CellSize, ly = y % CellSize;
                if ((lx == 0 || ly == 0 || lx == CellSize - 1 || ly == CellSize - 1) && a != 0)
                    throw new InvalidOperationException("落墨图集格间边界必须完全透明");
                if (a >= 128) occupied[y / CellSize * Columns + x / CellSize] = true;
            }
            if (Array.IndexOf(occupied, false) >= 0) throw new InvalidOperationException("落墨图集存在空白格");
            _contentHash = ComputeContentHash(alpha); _texture = texture; _alpha = alpha;
        }
        public static string ComputeContentHash(byte[] alpha)
        {
            if (alpha == null || alpha.Length != Width * Height) throw new ArgumentException("落墨 Alpha 数据尺寸无效");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(SamplingVersion); writer.Write(Columns); writer.Write(Rows); writer.Write(CellSize); writer.Write(alpha);
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
        }
        public static uint Hash(uint value)
        { unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); } }
        // Protocol 7: low five bits = tile; bit 5 = reflection; bits 6..21 = rotation.
        public static uint Pack(int index, uint entropy)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return (entropy & ~31u) | (uint)index;
        }
        public static int Index(uint seed) => (int)(seed & 31);
        public static bool Mirrored(uint seed) => (seed & 32) != 0;
        public static float Rotation(uint seed) => ((seed >> 6) & 65535) * (Mathf.PI * 2f / 65536f);
        public static Vector4 Transform(uint seed)
        { float angle = Rotation(seed); return new Vector4(Mathf.Cos(angle), Mathf.Sin(angle), Mirrored(seed) ? -1 : 1, 0); }
        private static void Basis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            normal = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up;
            Vector3 axis = Mathf.Abs(normal.y) > .5f ? Vector3.forward : Vector3.up;
            tangent = Vector3.Cross(normal, axis).normalized; bitangent = Vector3.Cross(tangent, normal);
        }
        public static Vector2 ProjectedUv(Vector3 point, Vector3 center, Vector3 normal, float radius, uint seed)
        {
            Basis(normal, out var tangent, out var bitangent);
            Vector3 delta = point - center;
            Vector2 p = new Vector2(Vector3.Dot(delta, tangent), Vector3.Dot(delta, bitangent)) / Mathf.Max(.0001f, radius);
            Vector4 t = Transform(seed);
            return new Vector2((p.x * t.x - p.y * t.y) * t.z, p.x * t.y + p.y * t.x) * .5f + Vector2.one * .5f;
        }
        internal static void StampBasis(PaintStamp stamp, out Vector3 tangent, out Vector3 bitangent)
        {
            var direction = Vector3.ProjectOnPlane(stamp.Direction, stamp.Normal);
            if (direction.sqrMagnitude < 1e-8f) { Basis(stamp.Normal, out tangent, out bitangent); return; }
            bitangent = direction.normalized; tangent = Vector3.Cross(stamp.Normal.normalized, bitangent).normalized;
        }
        public static bool Visible(PaintStamp stamp, Vector2 point)
        {
            if (!stamp.ClipEnabled) return true;
            float angle = Mathf.Repeat(Mathf.Atan2(point.y, point.x), Mathf.PI * 2) * (4 / Mathf.PI);
            int a = Mathf.FloorToInt(angle) % 8, b = (a + 1) % 8;
            float ra = a < 4 ? stamp.Clip0[a] : stamp.Clip1[a - 4], rb = b < 4 ? stamp.Clip0[b] : stamp.Clip1[b - 4];
            return point.magnitude <= Mathf.Min(ra, rb) + .00001f;
        }
        static float Depth(PaintStamp stamp) => stamp.DepthScale > 0 ? stamp.DepthScale : 1;
        public static Vector2 LocalExtents(PaintStamp stamp, Matrix4x4 worldToLocal)
        {
            StampBasis(stamp, out var tangent, out var bitangent); Vector4 t = Transform(stamp.ShapeSeed);
            bitangent *= Depth(stamp);
            Vector3 u = worldToLocal.MultiplyVector((tangent * t.x - bitangent * t.y) * stamp.Radius);
            Vector3 v = worldToLocal.MultiplyVector((tangent * t.y + bitangent * t.x) * stamp.Radius);
            return new Vector2(Mathf.Abs(u.x) + Mathf.Abs(v.x), Mathf.Abs(u.z) + Mathf.Abs(v.z));
        }
        public static float Sample(Vector2 uv, uint seed)
        {
            if (_texture == null || _alpha == null) throw new InvalidOperationException("落墨图集未初始化，禁止退回圆形覆盖");
            if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) return 0;
            int index = Index(seed), ox = index % Columns * CellSize, oy = index / Columns * CellSize;
            float x = Mathf.Clamp(uv.x * CellSize - .5f, 0, CellSize - 1), y = Mathf.Clamp(uv.y * CellSize - .5f, 0, CellSize - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, CellSize - 1), y1 = Math.Min(y0 + 1, CellSize - 1);
            float a = Mathf.Lerp(_alpha[(oy + y0) * Width + ox + x0], _alpha[(oy + y0) * Width + ox + x1], x - x0);
            float b = Mathf.Lerp(_alpha[(oy + y1) * Width + ox + x0], _alpha[(oy + y1) * Width + ox + x1], x - x0);
            return Mathf.Lerp(a, b, y - y0) / 255f;
        }
        public static float RemapCoverage(float alpha, float hardness, float strength)
        {
            float h = Mathf.Max(.0001f, Mathf.Clamp01(hardness));
            float t = Mathf.Clamp01((alpha - (1 - h)) / h);
            return t * t * (3 - 2 * t) * Mathf.Clamp01(strength);
        }
        public static float Coverage(Vector3 point, PaintStamp stamp) => new Brush(stamp).Coverage(point);

        /// <summary>Stamp-invariant work, shared by all tested regions and covered cells.</summary>
        internal readonly struct Brush
        {
            readonly PaintStamp _stamp;
            readonly Vector3 _tangent, _bitangent;
            readonly Vector4 _transform;
            public Brush(PaintStamp stamp)
            {
                _stamp = stamp; StampBasis(stamp, out _tangent, out _bitangent);
                _transform = Transform(stamp.ShapeSeed);
            }
            public Vector2 LocalExtents(Matrix4x4 inverse)
            {
                Vector3 u = inverse.MultiplyVector((_tangent * _transform.x - _bitangent * (_transform.y * Depth(_stamp))) * _stamp.Radius);
                Vector3 v = inverse.MultiplyVector((_tangent * _transform.y + _bitangent * (_transform.x * Depth(_stamp))) * _stamp.Radius);
                return new Vector2(Mathf.Abs(u.x) + Mathf.Abs(v.x), Mathf.Abs(u.z) + Mathf.Abs(v.z));
            }
            public float Coverage(Vector3 point)
            {
                if (_stamp.Radius <= 0) return 0;
                Vector3 delta = point - _stamp.Position;
                if (!Visible(_stamp, new Vector2(Vector3.Dot(delta, _tangent), Vector3.Dot(delta, _bitangent)))) return 0;
                Vector2 p = new Vector2(Vector3.Dot(delta, _tangent), Vector3.Dot(delta, _bitangent) / Depth(_stamp)) / Mathf.Max(.0001f, _stamp.Radius);
                Vector2 uv = new Vector2((p.x * _transform.x - p.y * _transform.y) * _transform.z,
                    p.x * _transform.y + p.y * _transform.x) * .5f + Vector2.one * .5f;
                return RemapCoverage(Sample(uv, _stamp.ShapeSeed), _stamp.Hardness, _stamp.Strength);
            }
        }
    }
}
