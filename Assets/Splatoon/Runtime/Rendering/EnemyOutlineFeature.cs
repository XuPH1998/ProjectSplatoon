using Splatoon.Combat;
using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Splatoon.Rendering
{
    /// <summary>Visible silhouette stencil plus unlit exterior hull. No duplicate rigs or screen textures.</summary>
    public sealed class EnemyOutlineFeature : ScriptableRendererFeature
    {
        public Shader OutlineShader;
        public Color Color = new(1, .035f, .025f, 1);
        [Min(.001f)] public float Width = .018f;
        [Range(1, 5)] public float MaxPixels = 2.5f;
        [Min(1)] public float MaxDistance = 80;
        Material _material;
        OutlinePass _pass;

        public override void Create()
        {
            CoreUtils.Destroy(_material);
            _material = OutlineShader != null ? CoreUtils.CreateEngineMaterial(OutlineShader) : null;
            _pass = new OutlinePass(this) { renderPassEvent = RenderPassEvent.AfterRenderingOpaques };
        }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_material == null || PrototypePlayer.Local == null || renderingData.cameraData.cameraType != CameraType.Game ||
                renderingData.cameraData.camera != Camera.main) return;
            _material.SetColor("_OutlineColor", Color); _material.SetFloat("_OutlineWidth", Width); _material.SetFloat("_MaxPixels", MaxPixels);
            renderer.EnqueuePass(_pass);
        }
        protected override void Dispose(bool disposing) { CoreUtils.Destroy(_material); _material = null; }

        sealed class OutlinePass : ScriptableRenderPass
        {
            readonly EnemyOutlineFeature _owner;
            readonly Plane[] _planes = new Plane[6];
            sealed class PassData { public EnemyOutlineFeature owner; public Camera camera; public byte team; public Plane[] planes; }
            public OutlinePass(EnemyOutlineFeature owner) => _owner = owner;
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var local = PrototypePlayer.Local;
                if (local == null || _owner._material == null) return;
                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>().camera;
                GeometryUtility.CalculateFrustumPlanes(camera, _planes);
                using var builder = graph.AddRasterRenderPass<PassData>("Enemy red outline", out var data);
                data.owner = _owner; data.camera = camera; data.team = local.PresentedState.Team; data.planes = _planes;
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
                {
                    // Mask all enemies first so an outline also cannot cross a second enemy's silhouette.
                    for (int shaderPass = 0; shaderPass < 2; shaderPass++)
                    foreach (var player in PrototypePlayer.ByOwner.Values)
                    {
                        if (player == null || !EnemyOutlineRules.IsEnemy(player.PresentedState, pass.team)) continue;
                        var state = player.PresentedState;
                        if (state.ShowsSwimBody || (state.Position - pass.camera.transform.position).sqrMagnitude > pass.owner.MaxDistance * pass.owner.MaxDistance) continue;
                        var view = player.CharacterView;
                        if (view == null) continue;
                        foreach (var draw in view.OutlineDraws)
                        {
                            var renderer = draw.Renderer;
                            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                                (pass.camera.cullingMask & (1 << renderer.gameObject.layer)) == 0 ||
                                !GeometryUtility.TestPlanesAABB(pass.planes, renderer.bounds)) continue;
                            for (int submesh = 0; submesh < draw.Submeshes; submesh++)
                                context.cmd.DrawRenderer(renderer, pass.owner._material, submesh, shaderPass);
                        }
                    }
                });
            }
        }
    }
}
