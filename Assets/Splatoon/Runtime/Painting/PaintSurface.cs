using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Painting
{
    public struct PaintStamp : INetworkSerializable
    {
        public uint Sequence, Round;
        public int SurfaceId;
        public byte Team;
        public Vector3 Position, Normal;
        public float Radius, Hardness, Strength;
        public uint ShapeSeed;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Sequence); s.SerializeValue(ref Round); s.SerializeValue(ref SurfaceId); s.SerializeValue(ref Team);
            s.SerializeValue(ref Position); s.SerializeValue(ref Normal); s.SerializeValue(ref Radius); s.SerializeValue(ref Hardness); s.SerializeValue(ref Strength); s.SerializeValue(ref ShapeSeed);
        }
    }
    public static class InkBrush
    {
        public static float Coverage(float distance, float radius, float hardness, float strength)
        {
            if (radius <= 0 || distance >= radius) return 0;
            if (hardness >= 1) return strength;
            float t = Mathf.Clamp01((distance - radius * hardness) / (radius * (1 - hardness)));
            return (1 - t * t * (3 - 2 * t)) * strength;
        }
        public static float OwnershipRadius(float radius, float hardness, float strength, float threshold)
        {
            if (strength < threshold) return 0;
            float low = 0, high = radius;
            for (int i = 0; i < 20; i++) { float middle = (low + high) * .5f; if (Coverage(middle, radius, hardness, strength) >= threshold) low = middle; else high = middle; }
            return low;
        }
    }
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed partial class PaintSurface : MonoBehaviour
    {
        [Tooltip("场景内稳定且唯一的表面 ID")] public int SurfaceId;
        [Tooltip("可行走地面启用计分，包含坡道及高台")] public bool Scores;
        [Tooltip("可行走面的局部 X/Z 尺寸（米），缩放必须为 1")] public Vector2 WalkableSize;
        [Tooltip("编辑器烘焙的不可达归属格索引")] public int[] BlockedCells = Array.Empty<int>();
        public SurfaceOwnershipGrid Ownership { get; private set; }
        public void InitializeOwnership(float cellSize) => InitializeRegions(cellSize);
        [Tooltip("绘制纹理尺寸，地面 1024，掩体 512")] public int Resolution = 512;
        public int ResolutionHeight;
        public int Height => ResolutionHeight > 0 ? ResolutionHeight : Resolution;
        public int TextureBytes => Resolution * Height * 4;
        public Shader DisplayShader;
        public RenderTexture DisplayMask { get; private set; }
        public Shader PainterShader;
        public Shader ExtendShader;
        [Tooltip("共享的不规则落墨图集")] public Texture2D ShapeAtlas;
        public RenderTexture Mask { get; private set; }
        public bool HasPaint { get; private set; }
        public static long AllocatedBytes { get; private set; }
        private RenderTexture _support, _islands;
        private static readonly System.Collections.Generic.Dictionary<Vector2Int, RenderTexture> Scratch = new();
        private static int _instances;
        private Material _painter, _extend;
        private bool _displayDirty;
        private bool _diagnosticScheduled;
        private bool _firstStampDiagnostic;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _properties;
        public bool GraphicsEnabled => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        private void OnEnable() { if (Application.isPlaying && GraphicsEnabled) Initialize(); }
        private RenderTexture Texture(string suffix)
        {
            var texture = new RenderTexture(Resolution, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Ink " + SurfaceId + suffix, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
            texture.Create(); AllocatedBytes += (long)TextureBytes; return texture;
        }
        private void Initialize()
        {
            if (Mask != null) return;
            if (PainterShader == null || DisplayShader == null) throw new InvalidOperationException("喷涂 Shader 缺失：" + name);
            _renderer = GetComponent<MeshRenderer>(); _properties = new MaterialPropertyBlock();
            InkShapeAtlas.Configure(ShapeAtlas);
            _painter = new Material(PainterShader); _extend = new Material(DisplayShader);
            if (!_painter.HasProperty("_ShapeAtlas")) throw new InvalidOperationException("落墨绘制 Shader 缺少 _ShapeAtlas 属性：" + PainterShader.name);
            _extend.SetColor("_InkPink", PrototypeArena.Pink); _extend.SetColor("_InkBlue", PrototypeArena.Blue);
            Mask = Texture(" State"); Mask.filterMode = FilterMode.Point; DisplayMask = Texture(" Display"); _islands = Texture(" Islands");
            var size = new Vector2Int(Resolution, Height);
            if (!Scratch.TryGetValue(size, out _support)) { _support = Texture(" SharedScratch"); Scratch.Add(size, _support); }
            _instances++; Clear();
            var command = CommandBufferPool.Get("Ink UV islands");
            command.SetRenderTarget(_islands); command.ClearRenderTarget(false, true, Color.clear); _painter.SetFloat("_PrepareUV", 1);
            command.DrawMesh(GetComponent<MeshFilter>().sharedMesh, transform.localToWorldMatrix, _painter);
            Graphics.ExecuteCommandBuffer(command); CommandBufferPool.Release(command);
            _renderer.GetPropertyBlock(_properties); _properties.SetTexture("_MaskTexture", DisplayMask);
            _properties.SetFloat("_InkWorldScale", GameplayConfig.Global.PaintWorldUvScale);
            _properties.SetFloat("_InkShapeNoiseScale", GameplayConfig.Global.PaintShapeNoiseScale);
            _properties.SetFloat("_InkThreshold", GameplayConfig.Global.PaintThreshold); _renderer.SetPropertyBlock(_properties);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log($"[INK-GPU] surface={SurfaceId} atlas={ShapeAtlas.name} hash={InkShapeAtlas.ContentHash} shapeProperty={_painter.HasProperty("_ShapeAtlas")} displayMask={DisplayMask.width}x{DisplayMask.height}");
#endif
            if (AllocatedBytes > GameplayConfig.Global.MaxPaintMemoryMiB * 1024L * 1024L) throw new InvalidOperationException("喷涂 RT 超过全局内存预算");
        }
        public void Clear()
        {
            foreach (var r in GameplayRegions) r.Grid.Clear();
            HasPaint = false; _displayDirty = false; if (Mask == null) return;
            var command = CommandBufferPool.Get("Clear ink");
            command.SetRenderTarget(Mask); command.ClearRenderTarget(false, true, Color.clear);
            command.SetRenderTarget(DisplayMask); command.ClearRenderTarget(false, true, Color.clear);
            Graphics.ExecuteCommandBuffer(command); CommandBufferPool.Release(command);
        }
        public void Apply(PaintStamp stamp)
        {
            InkShapeAtlas.Configure(ShapeAtlas);
            if (!GraphicsEnabled) { HasPaint = true; return; } Initialize(); HasPaint = true;
            _painter.SetFloat("_PrepareUV", 0); _painter.SetVector("_PainterPosition", stamp.Position); _painter.SetVector("_PainterNormal", stamp.Normal);
            _painter.SetFloat("_Radius", stamp.Radius); _painter.SetFloat("_Hardness", stamp.Hardness); _painter.SetFloat("_Strength", stamp.Strength);
            _painter.SetFloat("_PainterTeam", stamp.Team);
            _painter.SetTexture("_ShapeAtlas", ShapeAtlas); _painter.SetInteger("_ShapeIndex", InkShapeAtlas.Index(stamp.ShapeSeed));
            _painter.SetVector("_ShapeTransform", InkShapeAtlas.Transform(stamp.ShapeSeed));
            _painter.SetVector("_ShapeLayout", new Vector4(InkShapeAtlas.Columns, InkShapeAtlas.Rows, InkShapeAtlas.CellSize, 0));
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (!_firstStampDiagnostic)
            {
                _firstStampDiagnostic = true;
                Debug.Log($"[INK-GPU] surface={SurfaceId} firstStamp seed={stamp.ShapeSeed} index={InkShapeAtlas.Index(stamp.ShapeSeed)} rotation={InkShapeAtlas.Rotation(stamp.ShapeSeed):F4} radius={stamp.Radius:F3}");
            }
#endif
            _painter.SetTexture("_MainTex", _support); _extend.SetTexture("_UVIslands", _islands);
            var command = CommandBufferPool.Get("Paint ink surface");
            command.Blit(Mask, _support);
            command.SetRenderTarget(Mask); command.DrawMesh(GetComponent<MeshFilter>().sharedMesh, transform.localToWorldMatrix, _painter);
            Graphics.ExecuteCommandBuffer(command); CommandBufferPool.Release(command);
            _displayDirty = true;
        }
        private void LateUpdate() => FlushDisplay();
        public void FlushDisplay()
        {
            if (!_displayDirty || Mask == null) return;
            _extend.SetTexture("_UVIslands", _islands); Graphics.Blit(Mask, DisplayMask, _extend); _displayDirty = false;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (!_diagnosticScheduled) ScheduleDiagnosticReadback();
#endif
        }
        public void Restore(byte[] rgba)
        {
            InkShapeAtlas.Configure(ShapeAtlas);
            if (!GraphicsEnabled) { HasPaint = true; return; } Initialize();
            if (rgba.Length != TextureBytes) throw new InvalidOperationException("喷涂纹理快照尺寸不符");
            var texture = new Texture2D(Resolution, Height, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try { texture.LoadRawTextureData(rgba); texture.Apply(false, false); Graphics.Blit(texture, Mask);
                _extend.SetTexture("_UVIslands", _islands); Graphics.Blit(Mask, DisplayMask, _extend); HasPaint = true; }
            finally { RenderTexture.active = previous; DisposeObject(texture); }
        }
        private void OnDisable() => ReleaseGraphics();
        public void ReleaseGraphics()
        {
            if (Mask != null && --_instances == 0) { foreach (var texture in Scratch.Values) Release(texture); Scratch.Clear(); }
            Release(Mask); Release(DisplayMask); Release(_islands); Mask = DisplayMask = _support = _islands = null;
            if (_painter != null) DisposeObject(_painter); if (_extend != null) DisposeObject(_extend);
            _painter = _extend = null; _displayDirty = false;
            _diagnosticScheduled = false; _firstStampDiagnostic = false;
        }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void ScheduleDiagnosticReadback()
        {
            if (!SystemInfo.supportsAsyncGPUReadback || Mask == null || DisplayMask == null) return;
            _diagnosticScheduled = true;
            AsyncGPUReadback.Request(Mask, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError || Mask == null || DisplayMask == null) { Debug.LogWarning($"[INK-GPU] surface={SurfaceId} mask readback failed"); return; }
                var data = request.GetData<byte>(); int maskAlpha = 0;
                for (int i = 3; i < data.Length; i += 4) if (data[i] > 0) maskAlpha++;
                AsyncGPUReadback.Request(DisplayMask, 0, TextureFormat.RGBA32, displayRequest =>
                {
                    if (displayRequest.hasError) { Debug.LogWarning($"[INK-GPU] surface={SurfaceId} display readback failed maskAlpha={maskAlpha}"); return; }
                    var display = displayRequest.GetData<byte>(); int displayAlpha = 0;
                    for (int i = 3; i < display.Length; i += 4) if (display[i] > 0) displayAlpha++;
                    if (_renderer == null || _properties == null) return;
                    _renderer.GetPropertyBlock(_properties);
                    bool bound = _properties.GetTexture("_MaskTexture") == DisplayMask;
                    Debug.Log($"[INK-GPU] surface={SurfaceId} maskAlpha={maskAlpha} displayAlpha={displayAlpha} propertyBlockBound={bound}");
                });
            });
        }
#endif
        private static void Release(RenderTexture texture)
        { if (texture == null) return; if (RenderTexture.active == texture) RenderTexture.active = null; AllocatedBytes -= (long)texture.width * texture.height * 4; texture.Release(); DisposeObject(texture); }
        private static void DisposeObject(UnityEngine.Object value)
        { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
