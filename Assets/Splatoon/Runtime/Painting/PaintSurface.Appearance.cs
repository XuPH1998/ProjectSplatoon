using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Splatoon.Painting
{
    public sealed partial class PaintSurface
    {
        public int VisualTextureBytes => Resolution * Height * 2;
        RenderTexture _visual, _visualSupport;
        public RenderTexture VisualState { get { FlushDisplay(); return _visual; } }
        public GraphicsFormat VisualFormat => _visual != null ? _visual.graphicsFormat : GraphicsFormat.None;
        static readonly Dictionary<(int, int, GraphicsFormat), RenderTexture> VisualScratch = new();
        readonly RenderTargetIdentifier[] _paintTargets = new RenderTargetIdentifier[2];
        void InitializeAppearance()
        {
            if (SystemInfo.supportedRenderTargetCount < 2) throw new NotSupportedException("墨迹细节需要两个绘制目标");
            var profile = InkAppearanceProfile.Current;
            if (profile == null || profile.DetailAtlas == null) throw new InvalidOperationException("缺少墨迹细节图集");
            _visual = Texture(" Visual", PaintTextureMemory.SelectVisualFormat());
            CheckpointBytes += PaintTextureMemory.Bytes(_visual);
            var key = (Resolution, Height, _visual.graphicsFormat);
            if (!VisualScratch.TryGetValue(key, out _visualSupport)) { _visualSupport = Texture(" VisualScratch", _visual.graphicsFormat); VisualScratch.Add(key, _visualSupport); }
            _painter.SetTexture("_DetailAtlas", profile.DetailAtlas);
        }
        void BindAppearance()
        {
            InkAppearanceProfile.Current.Bind(_properties);
            if (Application.platform == RuntimePlatform.Android) _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _properties.SetTexture("_InkVisualTexture", _visual);
            _properties.SetTexture("_InkStateTexture", _mask);
            _properties.SetTexture("_InkIslands", _islands);
        }
        // Changes only rendering; both planes keep accumulating for replay and late join.
        public void SetAppearance(bool enabled)
        {
            if (_renderer == null) return;
            _renderer.GetPropertyBlock(_properties); _properties.SetFloat("_InkAppearance", enabled ? 1 : 0); _renderer.SetPropertyBlock(_properties);
        }
        void PadVisual()
        {
            _extend.SetTexture("_UVIslands", _islands); _extend.SetTexture("_VisualTex", _visual);
            Graphics.Blit(_mask, _visualSupport, _extend, 1); Graphics.CopyTexture(_visualSupport, _visual);
        }
        void ReleaseAppearance()
        {
            if (_visual != null) CheckpointBytes -= PaintTextureMemory.Bytes(_visual);
            Release(_visual); _visual = _visualSupport = null;
            if (_instances == 0) { foreach (var texture in VisualScratch.Values) Release(texture); VisualScratch.Clear(); }
        }
        public static void ValidatePair(byte[] rgba, byte[] visual, int expectedBytes)
        {
            if (rgba == null || visual == null || rgba.Length != expectedBytes || visual.Length != expectedBytes / 2)
                throw new InvalidOperationException("覆盖与细节快照必须成对且尺寸匹配");
        }
        public void Restore(byte[] rgba, byte[] visual)
        {
            ValidatePair(rgba, visual, TextureBytes);
            InkShapeAtlas.Configure(ShapeAtlas);
            if (!GraphicsEnabled) { HasPaint = true; return; }
            Initialize();
            var expanded = new byte[TextureBytes];
            for (int i = 0, j = 0; i < expanded.Length; i += 4, j += 2) { expanded[i] = visual[j]; expanded[i + 1] = visual[j + 1]; }
            var texture = new Texture2D(Resolution, Height, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            try
            {
                texture.LoadRawTextureData(expanded); texture.Apply(false, false); Graphics.Blit(texture, _visual);
                RestoreCoverage(rgba); PadVisual();
            }
            finally { DisposeObject(texture); }
        }
    }
}
