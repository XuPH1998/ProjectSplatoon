using System;
using System.Security.Cryptography;
using UnityEngine;

namespace Splatoon.Painting
{
    [CreateAssetMenu(menuName = "Splatoon/Ink appearance")]
    public sealed class InkAppearanceProfile : ScriptableObject
    {
        public const int AccumulationVersion = 1;
        public const string AssetPath = "Assets/Splatoon/Resources/InkAppearance.asset";
        public const string AtlasPath = "Assets/GameResource/Environment/Ink/Textures/InkSurfaceDetail.png";
        public Texture2D DetailAtlas, FineNormal;
        public bool Enabled = true;
        public float EdgeHeight = .018f, EdgeWidth = .055f, Relief = .002f, BroadRelief = .0005f;
        public float Smoothness = .75f, FineNormalStrength = .25f, FineNormalTiling = .65f, TeamGroove = .001f;
        static InkAppearanceProfile _current;
        string _hash;
        void OnValidate() { _hash = null; }
        public static InkAppearanceProfile Current => _current != null ? _current : _current = Resources.Load<InkAppearanceProfile>("InkAppearance");
        public static string ContentHash => Current != null ? Current.GetHash() : throw new InvalidOperationException("缺少 InkAppearance 配置");
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { _current = null; }
        string GetHash()
        {
            if (_hash != null) return _hash;
            if (DetailAtlas == null || !DetailAtlas.isReadable || DetailAtlas.isDataSRGB || DetailAtlas.mipmapCount != 1 ||
                DetailAtlas.width != InkShapeAtlas.Width || DetailAtlas.height != InkShapeAtlas.Height)
                throw new InvalidOperationException("细节图集必须为可读、Linear、无 Mipmap 的 2048x1024 纹理");
            var pixels = DetailAtlas.GetPixels32(); var rg = new byte[pixels.Length * 2];
            for (int i = 0; i < pixels.Length; i++) { rg[i * 2] = pixels[i].r; rg[i * 2 + 1] = pixels[i].g; }
            using var sha = SHA256.Create(); _hash = BitConverter.ToString(sha.ComputeHash(rg)).Replace("-", ""); return _hash;
        }
        public void Bind(MaterialPropertyBlock block)
        {
            block.SetFloat("_InkAppearance", Enabled ? 1 : 0);
            block.SetVector("_InkRelief", new Vector4(EdgeHeight, EdgeWidth, Relief, BroadRelief));
            block.SetVector("_InkFinish", new Vector4(Smoothness, FineNormalStrength, FineNormalTiling, TeamGroove));
            block.SetTexture("_InkFineNormal", FineNormal);
        }
        public static byte[] PackVisual(byte[] rgba)
        {
            if (rgba == null || rgba.Length % 4 != 0) throw new ArgumentException("Invalid RGBA readback");
            var rg = new byte[rgba.Length / 2];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 2) { rg[j] = rgba[i]; rg[j + 1] = rgba[i + 1]; }
            return rg;
        }
    }
}
