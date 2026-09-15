using System;
using System.Collections.Generic;
using Splatoon.Prototype;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Splatoon.Combat
{
    /// <summary>One isolated animated character per swimmer. Image and hit mesh share an explicitly evaluated pose.</summary>
    public sealed class PaperCapture : IDisposable
    {
        sealed class Part
        {
            public SkinnedMeshRenderer Skin;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public NativeArray<int> Indices;
            public NativeArray<Vector3> Projected;
        }
        public const int Layer = 30;
        readonly PaperBodyProfile _profile;
        readonly Scene _scene;
        readonly GameObject _root, _character;
        readonly Animator _animator;
        readonly PaperCaptureRig _rig;
        readonly Camera _camera;
        readonly Light _light;
        readonly List<Part> _parts = new();
        readonly PaperSilhouette _silhouette = new();
        int _frame = -1, _renderedVersion = -1;
        Vector2 _move;
        public Mesh HitMesh { get; }
        public RenderTexture Texture { get; private set; }
        public IReadOnlyList<Rect> HitRects => _silhouette.Rects;
        public int PoseVersion { get; private set; }
        public Bounds PosedBounds { get; private set; }
        public PaperCapture(PaperBodyProfile profile)
        {
            _profile = profile;
#if UNITY_EDITOR
            _scene = !Application.isPlaying ? UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene() : SceneManager.CreateScene("Paper capture " + Guid.NewGuid().ToString("N"));
#else
            _scene = SceneManager.CreateScene("Paper capture " + Guid.NewGuid().ToString("N"));
#endif
            _root = new GameObject("Paper capture stage") { hideFlags = HideFlags.DontSave };
            SceneManager.MoveGameObjectToScene(_root, _scene);
            // Renderers/lights are enabled only inside the manual render request.
            // The stage is also outside the gameplay world and has no colliders.
            _root.transform.position = new Vector3(0,-1000,0);
            _character = Object.Instantiate(profile.CapturePrefab, _root.transform, false);
            _rig = _character.GetComponent<PaperCaptureRig>();
            _animator = _rig.Animator;
            _animator.applyRootMotion = false; _animator.fireEvents = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.Rebind(); _animator.Update(0); _animator.enabled = false;
            foreach (var skin in _character.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!skin.enabled) continue;
                var mesh = new Mesh { name="Live paper skin", hideFlags=HideFlags.DontSave }; mesh.MarkDynamic();
                skin.BakeMesh(mesh);
                var go = new GameObject("Paper posed mesh",typeof(MeshFilter),typeof(MeshRenderer));
                go.transform.SetParent(skin.transform,false); go.layer=Layer;
                go.GetComponent<MeshFilter>().sharedMesh=mesh;
                var renderer=go.GetComponent<MeshRenderer>(); renderer.sharedMaterials=skin.sharedMaterials;
                renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.enabled=false; skin.enabled=false;
                _parts.Add(new Part { Skin=skin, Mesh=mesh, Renderer=renderer, Indices=new NativeArray<int>(mesh.triangles,Allocator.Persistent), Projected=new NativeArray<Vector3>(mesh.vertexCount,Allocator.Persistent) });
            }
            foreach (var renderer in _character.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled) continue;
                var mesh=renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                // Copy once to keep CPU data available in player builds.
                _parts.Add(new Part { Mesh=mesh, Renderer=renderer, Indices=new NativeArray<int>(mesh.triangles,Allocator.Persistent), Projected=new NativeArray<Vector3>(mesh.vertexCount,Allocator.Persistent) }); renderer.enabled=false;
            }
            var cameraObject=new GameObject("Paper virtual camera",typeof(Camera)); cameraObject.transform.SetParent(_root.transform,false);
            _camera=cameraObject.GetComponent<Camera>(); _camera.enabled=false; _camera.cameraType=CameraType.Game;
#if UNITY_EDITOR
            if (!Application.isPlaying) _camera.overrideSceneCullingMask=UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(_scene);
#endif
            _camera.cullingMask=1<<Layer; _camera.clearFlags=CameraClearFlags.SolidColor; _camera.backgroundColor=Color.clear;
            _camera.orthographic=true; _camera.orthographicSize=profile.CaptureHeight/2; _camera.aspect=.5f;
            _camera.nearClipPlane=.01f; _camera.farClipPlane=15; _camera.allowHDR=false; _camera.allowMSAA=false;
            _camera.transform.localPosition=profile.CaptureCenter+Vector3.forward*5;
            _camera.transform.localRotation=Quaternion.Euler(0,180,0);
            var data=_camera.GetUniversalAdditionalCameraData(); data.renderPostProcessing=false; data.renderShadows=false;
            data.requiresColorOption=CameraOverrideOption.Off; data.requiresDepthOption=CameraOverrideOption.Off;
            var lightObject=new GameObject("Paper studio light",typeof(Light)); lightObject.transform.SetParent(_root.transform,false);
            _light=lightObject.GetComponent<Light>(); _light.type=LightType.Directional; _light.intensity=1.2f; _light.cullingMask=1<<Layer;
            _light.shadows=LightShadows.None; _light.transform.localRotation=Quaternion.Euler(25,165,0); _light.enabled=false;
            HitMesh=new Mesh { name="Live paper hit silhouette", hideFlags=HideFlags.DontSave }; HitMesh.MarkDynamic();
        }
        public bool Sample(PlayerSnapshot state)
        {
            int frame=Mathf.FloorToInt(state.PaperAnimationTime*PaperAnimation.FramesPerSecond);
            var move=new Vector2(Mathf.Round(state.PaperMove.x*8)/8,Mathf.Round(state.PaperMove.y*8)/8);
            if (_frame==frame && _move==move) return false;
            _frame=frame; _move=move;
            _rig.Evaluate(frame/(float)PaperAnimation.FramesPerSecond,move);
            _silhouette.Clear();
            foreach (var part in _parts)
            {
                if (part.Skin != null) part.Skin.BakeMesh(part.Mesh);
                using (var meshData=Mesh.AcquireReadOnlyMeshData(part.Mesh)) meshData[0].GetVertices(part.Projected);
                Matrix4x4 matrix=_root.transform.worldToLocalMatrix*part.Renderer.transform.localToWorldMatrix;
                _silhouette.Add(part.Projected,part.Indices,matrix,_profile.CaptureCenter,_profile.CaptureHeight);
            }
            PosedBounds=_silhouette.CaptureBounds;
            _silhouette.Build(HitMesh,_profile.Size,_profile.Thickness); PoseVersion++; return true;
        }
        public void Render()
        {
            if (_renderedVersion==PoseVersion || SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null) return;
            if (Texture == null)
            {
                Texture=new RenderTexture(_profile.TextureWidth,_profile.TextureHeight,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB)
                { name="Live animated paper", hideFlags=HideFlags.DontSave, wrapMode=TextureWrapMode.Clamp,
                    filterMode=FilterMode.Trilinear, useMipMap=true, autoGenerateMips=false };
                Texture.Create();
            }
            try
            {
                foreach (var part in _parts) part.Renderer.enabled=true;
                _character.GetComponent<MachineGunFaceShadow>()?.Apply();
                _light.enabled=true;
                RenderPipeline.SubmitRenderRequest(_camera,new UniversalRenderPipeline.SingleCameraRequest { destination=Texture });
                Texture.GenerateMips();
                _renderedVersion=PoseVersion;
            }
            finally { foreach (var part in _parts) part.Renderer.enabled=false; _light.enabled=false; }
        }
        public Vector3 ClosestPoint(Vector3 point) => _silhouette.ClosestPoint(point,_profile.Thickness);
        public void Dispose()
        {
            if (Texture != null) { Texture.Release(); Destroy(Texture); }
            foreach (var part in _parts)
            { part.Indices.Dispose(); part.Projected.Dispose(); if (part.Skin != null) Destroy(part.Mesh); }
            _silhouette.Dispose();
            Destroy(HitMesh); Destroy(_root);
#if UNITY_EDITOR
            if (!Application.isPlaying) { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(_scene); return; }
#endif
            if (_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
        }
        static void Destroy(Object value) { if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value); }
    }
}
