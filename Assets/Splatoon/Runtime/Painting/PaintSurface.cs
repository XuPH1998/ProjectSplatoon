using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
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
        private RenderTexture _mask;
        // Reading the authoritative GPU state is a synchronization boundary (readback/checkpoint/tests).
        public RenderTexture Mask { get { FlushPaint(); return _mask; } }
        struct PendingPaint { public PaintStamp Stamp; public Matrix4x4 Matrix; }
        readonly System.Collections.Generic.List<PendingPaint> _paintQueue = new(64);
        MaterialPropertyBlock _brush;
        MeshFilter _meshFilter;
        public int PendingPaintCount => _paintQueue.Count;
        public bool HasPaint { get; private set; }
        public static long AllocatedBytes { get; private set; }
        public static long CheckpointBytes { get; private set; }
        public GraphicsFormat IslandFormat => _islands != null ? _islands.graphicsFormat : GraphicsFormat.None;
        private RenderTexture _support, _islands;
        private static readonly System.Collections.Generic.Dictionary<Vector2Int, RenderTexture> Scratch = new();
        private static int _instances;
        private Material _painter, _extend;
        private bool _displayDirty;
        private bool _diagnosticScheduled;
        private bool _registeredGraphics;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _properties;
        public bool GraphicsEnabled => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        private void OnEnable() { if (Application.isPlaying && GraphicsEnabled) Initialize(); }
        private RenderTexture Texture(string suffix, GraphicsFormat format = GraphicsFormat.R8G8B8A8_UNorm)
        {
            // Set Linear at construction as well: otherwise descriptor copies can retain the
            // default sRGB creation flag even after assigning a UNorm graphicsFormat.
            var texture = new RenderTexture(Resolution, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { graphicsFormat = format, name = "Ink " + SurfaceId + suffix, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false, antiAliasing = 1 };
            if (!texture.Create() || texture.graphicsFormat != format || texture.sRGB)
            {
                texture.Release(); DisposeObject(texture);
                throw new InvalidOperationException("无法创建指定格式的喷涂纹理：" + format);
            }
            AllocatedBytes += PaintTextureMemory.Bytes(texture); return texture;
        }
        private void Initialize()
        {
            if (_mask != null) return;
            if (PainterShader == null || DisplayShader == null) throw new InvalidOperationException("喷涂 Shader 缺失：" + name);
            var islandFormat = PaintTextureMemory.SelectIslandFormat();
            try
            {
                _renderer = GetComponent<MeshRenderer>(); _properties = new MaterialPropertyBlock();
                _meshFilter = GetComponent<MeshFilter>(); _brush = new MaterialPropertyBlock();
                InkShapeAtlas.Configure(ShapeAtlas);
                _painter = new Material(PainterShader); _extend = new Material(DisplayShader);
                if (!_painter.HasProperty("_ShapeAtlas")) throw new InvalidOperationException("落墨绘制 Shader 缺少 _ShapeAtlas 属性：" + PainterShader.name);
                _extend.SetColor("_InkPink", PrototypeArena.Pink); _extend.SetColor("_InkBlue", PrototypeArena.Blue);
                _instances++; _registeredGraphics = true;
                _mask = Texture(" State"); CheckpointBytes += TextureBytes;
                _mask.filterMode = FilterMode.Point; DisplayMask = Texture(" Display"); _islands = Texture(" Islands", islandFormat);
                var size = new Vector2Int(Resolution, Height);
                if (!Scratch.TryGetValue(size, out _support)) { _support = Texture(" SharedScratch"); Scratch.Add(size, _support); }
                Clear();
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
                if (AllocatedBytes + CheckpointBytes > GameplayConfig.Global.MaxPaintMemoryMiB * 1024L * 1024L) throw new InvalidOperationException("喷涂 RT 含检查点拷贝超过全局内存预算");
            }
            catch { ReleaseGraphics(); throw; }
        }
        public void Clear()
        {
            foreach (var r in _regions) r.Grid.Clear();
            _paintQueue.Clear(); HasPaint = false; _displayDirty = false; if (_mask == null) return;
            var command = CommandBufferPool.Get("Clear ink");
            command.SetRenderTarget(_mask); command.ClearRenderTarget(false, true, Color.clear);
            command.SetRenderTarget(DisplayMask); command.ClearRenderTarget(false, true, Color.clear);
            Graphics.ExecuteCommandBuffer(command); CommandBufferPool.Release(command);
        }
        public void Apply(PaintStamp stamp)
        {
            InkShapeAtlas.Configure(ShapeAtlas);
            if (!GraphicsEnabled) { HasPaint = true; return; } Initialize(); HasPaint = true;
            _paintQueue.Add(new PendingPaint { Stamp = stamp, Matrix = transform.localToWorldMatrix });
            _displayDirty = true;
        }
        public void FlushPaint()
        {
            if (_paintQueue.Count == 0 || _mask == null) return;
            using var marker = FramePerformance.PaintSubmit.Auto();
            var command = CommandBufferPool.Get("Paint ink batch");
            try
            {
                command.BeginSample("Splatoon.Paint.GPU");
                // Scratch is shared by equal-size surfaces. Clear atlas gaps before reusing it.
                command.SetRenderTarget(_support); command.ClearRenderTarget(false, true, Color.clear);
                RenderTexture source = _mask, destination = _support;
                _brush.SetFloat("_PrepareUV", 0);
                _brush.SetTexture("_ShapeAtlas", ShapeAtlas);
                _brush.SetVector("_ShapeLayout", new Vector4(InkShapeAtlas.Columns, InkShapeAtlas.Rows, InkShapeAtlas.CellSize, 0));
                foreach (var pending in _paintQueue)
                {
                    var stamp = pending.Stamp;
                    _brush.SetVector("_PainterPosition", stamp.Position); _brush.SetVector("_PainterNormal", stamp.Normal);
                    _brush.SetFloat("_Radius", stamp.Radius); _brush.SetFloat("_Hardness", stamp.Hardness); _brush.SetFloat("_Strength", stamp.Strength);
                    _brush.SetFloat("_PainterTeam", stamp.Team); _brush.SetInteger("_ShapeIndex", InkShapeAtlas.Index(stamp.ShapeSeed));
                    _brush.SetVector("_ShapeTransform", InkShapeAtlas.Transform(stamp.ShapeSeed));
                    _brush.SetTexture("_MainTex", source);
                    command.SetRenderTarget(destination);
                    // DrawMesh snapshots the property block, preserving every stamp in submission order.
                    command.DrawMesh(_meshFilter.sharedMesh, pending.Matrix, _painter, 0, 0, _brush);
                    (source, destination) = (destination, source);
                }
                if (source != _mask) { command.CopyTexture(source, _mask); FramePerformance.PaintCopies++; }
                command.EndSample("Splatoon.Paint.GPU");
                Graphics.ExecuteCommandBuffer(command);
                FramePerformance.PaintDraws += _paintQueue.Count; FramePerformance.PaintSubmissions++;
                _paintQueue.Clear();
            }
            finally { CommandBufferPool.Release(command); }
        }
        private void LateUpdate() => FlushDisplay();
        public void FlushDisplay()
        {
            FlushPaint();
            if (!_displayDirty || _mask == null) return;
            _extend.SetTexture("_UVIslands", _islands); Graphics.Blit(_mask, DisplayMask, _extend); _displayDirty = false;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (!_diagnosticScheduled) ScheduleDiagnosticReadback();
#endif
        }
        public void Restore(byte[] rgba)
        {
            InkShapeAtlas.Configure(ShapeAtlas);
            if (!GraphicsEnabled) { HasPaint = true; return; } Initialize();
            if (rgba.Length != TextureBytes) throw new InvalidOperationException("喷涂纹理快照尺寸不符");
            _paintQueue.Clear(); _displayDirty = false;
            var texture = new Texture2D(Resolution, Height, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try { texture.LoadRawTextureData(rgba); texture.Apply(false, false); Graphics.Blit(texture, _mask);
                _extend.SetTexture("_UVIslands", _islands); Graphics.Blit(_mask, DisplayMask, _extend); HasPaint = true; }
            finally { RenderTexture.active = previous; DisposeObject(texture); }
        }
        private void OnDisable() => ReleaseGraphics();
        public void ReleaseGraphics()
        {
            _paintQueue.Clear();
            if (_registeredGraphics && --_instances == 0) { foreach (var texture in Scratch.Values) Release(texture); Scratch.Clear(); }
            _registeredGraphics = false;
            if (_mask != null) CheckpointBytes -= (long)_mask.width * _mask.height * 4;
            Release(_mask); Release(DisplayMask); Release(_islands); _mask = DisplayMask = _support = _islands = null;
            if (_painter != null) DisposeObject(_painter); if (_extend != null) DisposeObject(_extend);
            _painter = _extend = null; _displayDirty = false;
            _diagnosticScheduled = false;
        }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void ScheduleDiagnosticReadback()
        {
            if (!SystemInfo.supportsAsyncGPUReadback || _mask == null || DisplayMask == null) return;
            _diagnosticScheduled = true;
            AsyncGPUReadback.Request(_mask, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError || _mask == null || DisplayMask == null) { Debug.LogWarning($"[INK-GPU] surface={SurfaceId} mask readback failed"); return; }
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
        { if (texture == null) return; if (RenderTexture.active == texture) RenderTexture.active = null; AllocatedBytes -= PaintTextureMemory.Bytes(texture); texture.Release(); DisposeObject(texture); }
        private static void DisposeObject(UnityEngine.Object value)
        { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
