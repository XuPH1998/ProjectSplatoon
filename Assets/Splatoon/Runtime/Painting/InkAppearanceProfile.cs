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
        [Tooltip("普通对局初始化时使用的墨迹外观；仍可通过对照菜单临时切换")]
        public InkLook DefaultLook = InkLook.Current;
        public InkLook StartupLook
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-inkStaticLook");
                if (index >= 0 && index + 1 < args.Length)
                {
                    if (args[index + 1] == "soft") return InkLook.Soft;
                    if (args[index + 1] == "legacy" || args[index + 1] == "today" || args[index + 1] == "rounded") return InkLook.Current;
                }
                return DefaultLook;
            }
        }
        public float EdgeHeight = .018f, EdgeWidth = .055f, Relief = .002f, BroadRelief = .0005f;
        public float Smoothness = .75f, FineNormalStrength = .25f, FineNormalTiling = .65f, TeamGroove = .001f;
        [Header("Rounded outer edge (display only)")]
        public bool RoundedEdges = true;
        [Min(0)] public float RoundedEdgeHeight = .030f;
        [Min(.001f)] public float RoundedEdgeWidth = .080f;
        [Range(.1f, 1.5f)] public float RoundedEdgeMaxSlope = .9f;
        [Range(.65f, .82f)] public float RoundedEdgeSmoothness = .78f;
        [Range(0, .08f)] public float RoundedEdgeContactShade = .08f;
        [Header("Soft ink (metres, normal-only relief)")]
        [Min(0)] public float SoftHeight = .018f;
        [Range(.04f, .20f)] public float SoftWidth = .12f;
        [Range(.65f, .82f)] public float SoftSmoothness = .76f;
        [Range(0, .03f)] public float SoftContactShade = .03f;
        [Min(0)] public float SoftInteriorRelief = .002f, SoftBroadRelief = .001f;
        [Range(0, 1)] public float SoftFineResidual = .25f, SoftFineNormal = .12f;
        [Range(.01f, .10f)] public float SoftFilterRadius = .06f;
        [Range(0, .5f)] public float SoftTeamHeightRatio = .25f;
        [Range(.1f, 1)] public float SoftTeamWidthRatio = .5f;
        [Range(0, 1)] public float SoftContourSmoothing = 1;
        public void BindSoft(MaterialPropertyBlock block)
        {
            block.SetVector("_InkSoftRelief", new Vector4(SoftHeight, SoftWidth, SoftInteriorRelief, SoftBroadRelief));
            block.SetVector("_InkSoftFinish", new Vector4(SoftSmoothness, SoftContactShade, SoftFineResidual, SoftFineNormal));
            block.SetVector("_InkSoftDetail", new Vector4(SoftFilterRadius, SoftTeamHeightRatio, SoftTeamWidthRatio, SoftContourSmoothing));
        }
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
            block.SetFloat("_InkRoundedEdge", RoundedEdges ? 1 : 0);
            block.SetVector("_InkRoundedRelief", RoundedRelief);
            block.SetVector("_InkRoundedFinish", RoundedFinish);
        }
        Vector4 RoundedRelief => new Vector4(RoundedEdgeHeight, RoundedEdgeWidth, RoundedEdgeMaxSlope, 0);
        Vector4 RoundedFinish => new Vector4(RoundedEdgeSmoothness, RoundedEdgeContactShade, 0, 0);
        public void Bind(Material material)
        {
            material.SetFloat("_InkAppearance", Enabled ? 1 : 0);
            material.SetVector("_InkRelief", new Vector4(EdgeHeight, EdgeWidth, Relief, BroadRelief));
            material.SetVector("_InkFinish", new Vector4(Smoothness, FineNormalStrength, FineNormalTiling, TeamGroove));
            material.SetTexture("_InkFineNormal", FineNormal);
            material.SetFloat("_InkRoundedEdge", RoundedEdges ? 1 : 0);
            material.SetVector("_InkRoundedRelief", RoundedRelief);
            material.SetVector("_InkRoundedFinish", RoundedFinish);
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
