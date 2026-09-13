using UnityEngine;

namespace Splatoon.Painting
{
    // Keep the arithmetic and byte rounding paired with InkCoverage.hlsl.
    public static class InkCoverage
    {
        public static Vector2 DetailUV(Vector3 p, Vector3 normal, float scale)
        {
            normal.Normalize();
            Vector3 axis = Mathf.Abs(normal.y) > .5f ? Vector3.forward : Vector3.up;
            Vector3 tangent = Vector3.Cross(normal, axis).normalized;
            Vector3 bitangent = Vector3.Cross(tangent, normal);
            return new Vector2(Vector3.Dot(p, tangent), Vector3.Dot(p, bitangent)) * scale + new Vector2(.37866f, .62134f);
        }
        private static float Mod(float x, float y) => x - Mathf.Floor(x / y) * y;
        private static Vector2 Direction(Vector2 p)
        {
            p.x = Mod(p.x, 289); p.y = Mod(p.y, 289);
            float x = Mod((34 * p.x + 1) * p.x, 289) + p.y;
            x = Mod((34 * x + 1) * x, 289);
            x = Mod(x / 41, 1) * 2 - 1;
            return new Vector2(x - Mathf.Floor(x + .5f), Mathf.Abs(x) - .5f).normalized;
        }
        public static float Noise(Vector2 uv, float scale)
        {
            Vector2 p = uv * scale, ip = new(Mathf.Floor(p.x), Mathf.Floor(p.y)), f = p - ip;
            float d00 = Vector2.Dot(Direction(ip), f), d01 = Vector2.Dot(Direction(ip + Vector2.up), f - Vector2.up);
            float d10 = Vector2.Dot(Direction(ip + Vector2.right), f - Vector2.right), d11 = Vector2.Dot(Direction(ip + Vector2.one), f - Vector2.one);
            Vector2 w = new(Fade(f.x), Fade(f.y));
            return Mathf.Lerp(Mathf.Lerp(d00, d01, w.y), Mathf.Lerp(d10, d11, w.y), w.x) + .5f;
        }
        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static byte Quantize(float x) => (byte)Mathf.Clamp(Mathf.FloorToInt(x + .5f), 0, 255);
        public static Color32 Accumulate(Color32 state, byte team, float strength)
        {
            float f = Mathf.Clamp01(strength);
            byte pink = Quantize(state.r * (1 - f) + (team == 1 ? 255 * f : 0));
            byte blue = Quantize(state.g * (1 - f) + (team == 2 ? 255 * f : 0));
            byte owner = pink > blue ? (byte)1 : blue > pink ? (byte)2 : state.b;
            return new Color32(pink, blue, owner, (byte)Mathf.Min(255, pink + blue));
        }
        public static byte Owner(Color32 state, Vector3 point, Vector3 normal, float threshold, float worldScale, float noiseScale)
        {
            float alpha = state.a / 255f;
            // Equivalent to the reference graph's step(1-a, a+a*noise) at threshold=.5.
            return alpha * (1 + .5f * Noise(DetailUV(point, normal, worldScale), noiseScale)) >= threshold ? state.b : (byte)0;
        }
    }
}
