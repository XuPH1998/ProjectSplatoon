using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;
using Splatoon.Prototype;

public class RenderMetaballsScreenSpace : ScriptableRendererFeature
{
    public string PassTag = "RenderMetaballsScreenSpace";
    public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;
    public RenderObjects.FilterSettings FilterSettings = new RenderObjects.FilterSettings();
    public Shader CopyDepthShader, BlurShader;
    public Material BlitMaterial;
    public Material WriteDepthMaterial;
    public bool FlightComposite;
    [Range(1, 15)] public int BlurPasses = 1;
    [Range(0f, 1f)] public float BlurDistance = 0.5f;

    MetaballsRenderGraphPass _pass;

    public override void Create()
    {
        _pass?.Dispose();
        _pass = null;
        if (BlitMaterial == null || WriteDepthMaterial == null || CopyDepthShader == null || BlurShader == null) return;
        _pass = new MetaballsRenderGraphPass(PassTag, Event, FilterSettings,
            BlitMaterial, WriteDepthMaterial, 1, BlurPasses, BlurDistance, CopyDepthShader, BlurShader, FlightComposite);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!ShouldRender(renderingData.cameraData.camera)) { FramePerformance.SkippedComposites++; return; }
        if (_pass != null)
            renderer.EnqueuePass(_pass);
    }

    public bool ShouldRender(Camera camera)
    {
        if (camera == null || (camera.cullingMask & FilterSettings.LayerMask.value) == 0) return false;
        // Editor/reference cameras can contain independent emitters outside the gameplay manager.
        return !Application.isPlaying || PrototypeMatch.Current == null || InkPresentation.Current == null ||
            InkPresentation.Current.HasCompositeContent(FilterSettings.LayerMask.value);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }
}

// Both ink features share the same depth-tested draw, blur and composite pipeline.
// Render Graph owns all per-camera textures; only the two engine materials persist.
internal sealed class MetaballsRenderGraphPass : ScriptableRenderPass
{
    readonly string _passTag;
    readonly RenderObjects.FilterSettings _filter;
    readonly List<ShaderTagId> _shaderTags = new List<ShaderTagId>();
    readonly Material _composite;
    readonly Material _writeDepth;
    readonly Material _copyDepth;
    readonly Material _blur;
    readonly int _downsampling;
    readonly int _blurPasses;
    readonly float _blurDistance;
    readonly bool _flightComposite;

    class PassData
    {
        public TextureHandle cameraColor, cameraDepth;
        public TextureHandle ink, inkDepth, blurA, blurB, linearDepth;
        public RendererListHandle inkRenderers, depthRenderers;
        public Material composite, copyDepth, blur;
        public bool screenSpace, flightComposite;
        public int blurPasses;
        public float blurDistance;
    }

    public MetaballsRenderGraphPass(string passTag, RenderPassEvent passEvent,
        RenderObjects.FilterSettings filter, Material composite, Material writeDepth,
        int downsampling, int blurPasses, float blurDistance, Shader copyDepthShader, Shader blurShader, bool flightComposite = false)
    {
        _passTag = passTag;
        renderPassEvent = passEvent;
        _filter = filter;
        _composite = composite;
        _writeDepth = writeDepth;
        _downsampling = Mathf.Clamp(downsampling, 1, 16);
        _blurPasses = Mathf.Clamp(blurPasses, 1, 15);
        _blurDistance = Mathf.Clamp01(blurDistance);
        _flightComposite = flightComposite;
        _copyDepth = CoreUtils.CreateEngineMaterial(copyDepthShader);
        _blur = CoreUtils.CreateEngineMaterial(blurShader);
        var tags = filter.PassNames;
        if (tags == null || tags.Length == 0)
            tags = new[] { "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly", "LightweightForward" };
        foreach (string tag in tags)
            _shaderTags.Add(new ShaderTagId(tag));
        ConfigureInput(ScriptableRenderPassInput.Depth);
        requiresIntermediateTexture = true;
    }

    public void Dispose()
    {
        CoreUtils.Destroy(_copyDepth);
        CoreUtils.Destroy(_blur);
    }

    RendererListHandle CreateRendererList(RenderGraph graph, ContextContainer frameData, Material material)
    {
        var rendering = frameData.Get<UniversalRenderingData>();
        var camera = frameData.Get<UniversalCameraData>();
        bool transparent = _filter.RenderQueueType == RenderQueueType.Transparent;
        var sorting = transparent ? SortingCriteria.CommonTransparent : camera.defaultOpaqueSortFlags;
        var drawing = RenderingUtils.CreateDrawingSettings(_shaderTags, rendering, camera,
            frameData.Get<UniversalLightData>(), sorting);
        drawing.overrideMaterial = material;
        drawing.overrideMaterialPassIndex = 0;
        var filtering = new FilteringSettings(transparent ? RenderQueueRange.transparent : RenderQueueRange.opaque,
            _filter.LayerMask);
        return graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filtering));
    }

    public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
    {
        if (_composite == null || _copyDepth == null || _blur == null)
            return;

        var resources = frameData.Get<UniversalResourceData>();
        var camera = frameData.Get<UniversalCameraData>();
        if (!resources.cameraDepthTexture.IsValid())
            return;

        var descriptor = new TextureDesc(camera.cameraTargetDescriptor)
        {
            colorFormat = GraphicsFormat.R8G8B8A8_UNorm,
            depthBufferBits = DepthBits.None,
            msaaSamples = MSAASamples.None,
            bindTextureMS = false,
            clearBuffer = false,
            filterMode = FilterMode.Bilinear,
            name = "_MetaballBlurA"
        };
        var blurA = graph.CreateTexture(descriptor);
        descriptor.name = "_MetaballBlurB";
        var blurB = graph.CreateTexture(descriptor);
        descriptor.name = "_MetaballDepthRT";
        // Flight's final occlusion needs unquantized eye-depth / far-plane distance.
        if (_flightComposite) descriptor.colorFormat = GraphicsFormat.R32_SFloat;
        var linearDepth = graph.CreateTexture(descriptor);
        descriptor.colorFormat = GraphicsFormat.R8G8B8A8_UNorm;
        descriptor.width = Mathf.Max(1, descriptor.width / _downsampling);
        descriptor.height = Mathf.Max(1, descriptor.height / _downsampling);
        descriptor.name = "_MetaballRT";
        var ink = graph.CreateTexture(descriptor);
        descriptor.colorFormat = GraphicsFormat.None;
        descriptor.depthBufferBits = DepthBits.Depth32;
        descriptor.name = "_MetaballZBuffer";
        var inkDepth = graph.CreateTexture(descriptor);

        using (var builder = graph.AddUnsafePass<PassData>(_passTag, out var data))
        {
            FramePerformance.CompositePasses++;
            data.cameraColor = resources.activeColorTexture;
            data.cameraDepth = resources.cameraDepthTexture;
            data.ink = ink;
            data.inkDepth = inkDepth;
            data.blurA = blurA;
            data.blurB = blurB;
            data.linearDepth = linearDepth;
            data.inkRenderers = CreateRendererList(graph, frameData, null);
            data.screenSpace = _writeDepth != null;
            data.flightComposite = _flightComposite;
            if (data.screenSpace)
            {
                data.depthRenderers = CreateRendererList(graph, frameData, _writeDepth);
                builder.UseRendererList(data.depthRenderers);
            }
            data.composite = _composite;
            data.copyDepth = _copyDepth;
            data.blur = _blur;
            data.blurPasses = _blurPasses;
            data.blurDistance = _blurDistance;
            builder.UseRendererList(data.inkRenderers);
            builder.UseTexture(data.cameraDepth, AccessFlags.Read);
            // Alpha-clipped compositing must retain the existing scene color.
            builder.UseTexture(data.cameraColor, AccessFlags.ReadWrite);
            builder.UseTexture(ink, AccessFlags.ReadWrite);
            builder.UseTexture(inkDepth, AccessFlags.ReadWrite);
            builder.UseTexture(blurA, AccessFlags.ReadWrite);
            builder.UseTexture(blurB, AccessFlags.ReadWrite);
            builder.UseTexture(linearDepth, AccessFlags.ReadWrite);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData pass, UnsafeGraphContext context) => Execute(pass, context));
        }
    }

    static void Execute(PassData data, UnsafeGraphContext context)
    {
        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        RTHandle ink = data.ink;
        RTHandle inkDepth = data.inkDepth;
        RTHandle linearDepth = data.linearDepth;
        RTHandle blurA = data.blurA;
        RTHandle blurB = data.blurB;
        RTHandle cameraColor = data.cameraColor;

        // This texture stores particle distance for the depth-dependent blur shader.
        cmd.SetRenderTarget(linearDepth.nameID);
        cmd.ClearRenderTarget(false, true, Color.clear);
        if (data.screenSpace)
        {
            cmd.SetRenderTarget(linearDepth.nameID, inkDepth.nameID);
            cmd.ClearRenderTarget(true, false, Color.clear);
            cmd.DrawRendererList(data.depthRenderers);
        }

        cmd.SetRenderTarget(ink.nameID, inkDepth.nameID);
        cmd.ClearRenderTarget(true, true, Color.clear);
        Blitter.BlitTexture(cmd, data.cameraDepth, new Vector4(1, 1, 0, 0), data.copyDepth, 0);
        cmd.DrawRendererList(data.inkRenderers);

        // Existing Shader Graph materials consume _MainTex and the mesh blit vertex layout.
        // Keep that contract inside an unsafe pass, with every resource declared above.
        cmd.SetGlobalTexture("_MetaballDepthRT", linearDepth.nameID);
        if (data.flightComposite) { RTHandle sceneDepth = data.cameraDepth; cmd.SetGlobalTexture("_FlightSceneDepth", sceneDepth.nameID); }
        cmd.SetGlobalFloat("_BlurDistance", data.blurDistance);
        RTHandle source = ink;
        if (!data.screenSpace)
        {
            cmd.SetRenderTarget(blurA.nameID);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.Blit(source.nameID, blurA.nameID, data.composite, 0);
            source = blurA;
        }
        for (int i = 0; i < data.blurPasses; ++i)
        {
            RTHandle destination = source == blurA ? blurB : blurA;
            cmd.SetGlobalFloat("_Offset", 1.5f + i);
            cmd.Blit(source.nameID, destination.nameID, data.blur, 0);
            source = destination;
        }
        cmd.Blit(source.nameID, cameraColor.nameID, data.composite, 0);
    }
}
