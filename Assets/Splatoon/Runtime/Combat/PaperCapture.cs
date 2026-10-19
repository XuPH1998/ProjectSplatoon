using System;
using System.Collections.Generic;
using Splatoon.Prototype;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Unity.Profiling;
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
        const int SharedPoseCapacity = 256;
        // Profile/prefab content is authored and immutable during a capture.
        // Keep both captured and current content identities so an older clone
        // cannot populate a newly rebound shape's cache after an asset change.
        internal readonly struct PoseKey : IEquatable<PoseKey>
        {
            readonly (PaperBodyProfile, GameObject, GameObject, string, string, Vector3, float, Vector2, float, int, Vector2) _value;
            public PoseKey(PaperBodyProfile profile, GameObject prefab, string capturedContent, int frame, Vector2 move)
                => _value = (profile, prefab, profile.CapturePrefab, capturedContent, profile.ContentHash ?? "", profile.CaptureCenter,
                    profile.CaptureHeight, profile.Size, profile.Thickness, frame, move);
            public bool Equals(PoseKey other) => _value.Equals(other._value);
            public override bool Equals(object obj) => obj is PoseKey other && Equals(other);
            public override int GetHashCode() => _value.GetHashCode();
        }
        internal sealed class SharedPose
        {
            internal readonly PoseKey Key;
            public readonly IReadOnlyList<Rect> Rects;
            public readonly Bounds Bounds;
            internal SharedPose(PoseKey key, PaperSilhouette silhouette)
            { Key = key; Rects = Array.AsReadOnly(silhouette.Rects.ToArray()); Bounds = silhouette.CaptureBounds; }
        }
        static readonly Dictionary<PoseKey, SharedPose> SharedPoses = new();
        static readonly Queue<PoseKey> SharedPoseOrder = new();
        static int _activeCaptures;
        readonly PaperBodyProfile _profile;
        readonly GameObject _capturePrefab;
        readonly string _capturedContent;
        readonly bool _contourOnly;
        readonly Scene _scene;
        readonly GameObject _root, _character;
        readonly Animator _animator;
        readonly PaperCaptureRig _rig;
        readonly Camera _camera;
        readonly Light _light;
        readonly List<Part> _parts = new();
        readonly PaperSilhouette _silhouette = new();
        int _frame = -1, _renderedVersion = -1;
        int _bakedFrame = int.MinValue;
        Vector2 _bakedMove;
        PoseKey _sampleKey;
        bool _hasSample, _disposed;
        static readonly ProfilerMarker SampleMarker = FramePerformance.PaperSample;
        Vector2 _move;
        public Mesh HitMesh { get; }
        public RenderTexture Texture { get; private set; }
        public IReadOnlyList<Rect> HitRects => _silhouette.Rects;
        public int PoseVersion { get; private set; }
        public Bounds PosedBounds { get; private set; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static long SharedPoseCacheHits { get; private set; }
        public static long SharedPoseCacheMisses { get; private set; }
        public static long PoseBakeCount { get; private set; }
        public static long DeferredRenderBakeCount { get; private set; }
        // Shared lookup genuinely missed, but this body's immutable exact contour survived locally.
        public static long LocalContourHandoffHits { get; private set; }
        // Independent uncached fresh captures exercise the original pose path
        // in regression checks; normal gameplay always shares exact pose keys.
        public bool SampleUncachedForDiagnostics(PlayerSnapshot state) => Sample(state, false);
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSharedPoses()
        {
            SharedPoses.Clear(); SharedPoseOrder.Clear();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SharedPoseCacheHits = SharedPoseCacheMisses = PoseBakeCount = DeferredRenderBakeCount = 0;
            LocalContourHandoffHits = 0;
#endif
        }
        public PaperCapture(PaperBodyProfile profile) : this(profile, false) { }
        /// <summary>Independent candidate evaluator with no hit mesh or render stage.</summary>
        public static PaperCapture CreateContourEvaluator(PaperBodyProfile profile) => new(profile, true);
        PaperCapture(PaperBodyProfile profile, bool contourOnly)
        {
            _profile = profile; _contourOnly = contourOnly;
            _capturePrefab = profile.CapturePrefab; _capturedContent = profile.ContentHash ?? "";
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
            if (_contourOnly) { _activeCaptures++; return; }
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
            _activeCaptures++;
        }
        public bool Sample(PlayerSnapshot state) => Sample(state, true);
        internal static void PoseCoordinates(PlayerSnapshot state, out int frame, out Vector2 move)
        {
            frame = Mathf.FloorToInt(state.PaperAnimationTime * PaperAnimation.FramesPerSecond);
            move = new Vector2(Mathf.Round(state.PaperMove.x * 8) / 8, Mathf.Round(state.PaperMove.y * 8) / 8);
        }
        /// <summary>Immutable exact-key contour. Does not build or publish a hit mesh.</summary>
        public IReadOnlyList<Rect> QueryContour(PlayerSnapshot state)
            => QueryContourValue(state).Rects;

        internal SharedPose QueryContourValue(PlayerSnapshot state)
        {
            if (!_contourOnly) throw new InvalidOperationException("Use an independent contour evaluator for candidate queries.");
            using var sample = SampleMarker.Auto();
            PoseCoordinates(state, out int frame, out var move);
            var key = new PoseKey(_profile, _capturePrefab, _capturedContent, frame, move);
            if (SharedPoses.TryGetValue(key, out var cached))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                SharedPoseCacheHits++;
                PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.QueryGlobalHit);
#endif
                return cached;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SharedPoseCacheMisses++;
            PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.QueryRealMiss);
#endif
            ProjectPose(frame, move);
            using (FramePerformance.PaperContour.Auto())
                _silhouette.BuildContour(_profile.Size);
            return RememberPose(key);
        }
        internal bool SampleWithLocalContour(PlayerSnapshot state, SharedPose hint)
            => Sample(state, true, hint);

        bool Sample(PlayerSnapshot state, bool share, SharedPose hint = null)
        {
            if (_contourOnly) throw new InvalidOperationException("Contour evaluators query immutable contours instead of publishing hit meshes.");
            PoseCoordinates(state, out int frame, out var move);
            var key = new PoseKey(_profile, _capturePrefab, _capturedContent, frame, move);
            if (_hasSample && _sampleKey.Equals(key))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (share) PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.LiveNoop);
#endif
                return false;
            }
            using var sample = SampleMarker.Auto();
            _frame=frame; _move=move; _sampleKey=key; _hasSample=true;
            SharedPose cached = null;
            bool sharedHit = share && SharedPoses.TryGetValue(key, out cached);
            bool handoffHit = !sharedHit && share && hint != null && hint.Key.Equals(key);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (share)
            {
                if (sharedHit) PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.LiveGlobalHit);
                else if (handoffHit) PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.HandoffHit);
                else if (hint != null) PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.HintReject);
            }
#endif
            if (sharedHit || handoffHit)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (sharedHit) SharedPoseCacheHits++;
                else { SharedPoseCacheMisses++; LocalContourHandoffHits++; }
#endif
                cached ??= hint;
                PosedBounds = cached.Bounds;
                using (FramePerformance.PaperHitMeshRestore.Auto())
                    _silhouette.Restore(HitMesh, cached.Rects, cached.Bounds, _profile.Thickness);
                PoseVersion++; return true;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (share)
            {
                SharedPoseCacheMisses++;
                PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.LiveRealMiss);
            }
#endif
            ProjectPose(frame, move);
            PosedBounds=_silhouette.CaptureBounds;
            using (FramePerformance.PaperHitMesh.Auto())
                _silhouette.Build(HitMesh,_profile.Size,_profile.Thickness);
            if (share) RememberPose(key);
            PoseVersion++; return true;
        }
        SharedPose RememberPose(PoseKey key)
        {
            var pose = new SharedPose(key, _silhouette);
            if (SharedPoses.Count == SharedPoseCapacity) SharedPoses.Remove(SharedPoseOrder.Dequeue());
            SharedPoses.Add(key, pose); SharedPoseOrder.Enqueue(key);
            return pose;
        }
        void ProjectPose(int frame, Vector2 move)
        {
            BakePose(frame, move);
            _silhouette.Clear();
            foreach (var part in _parts)
            {
                using (FramePerformance.PaperMeshRead.Auto())
                using (var meshData=Mesh.AcquireReadOnlyMeshData(part.Mesh)) meshData[0].GetVertices(part.Projected);
                Matrix4x4 matrix=_root.transform.worldToLocalMatrix*part.Renderer.transform.localToWorldMatrix;
                using (FramePerformance.PaperContour.Auto())
                    _silhouette.Add(part.Projected,part.Indices,matrix,_profile.CaptureCenter,_profile.CaptureHeight);
            }
        }
        void BakePose(int frame, Vector2 move)
        {
            using (FramePerformance.PaperPoseEvaluate.Auto())
                _rig.Evaluate(frame/(float)PaperAnimation.FramesPerSecond,move);
            using (FramePerformance.PaperSkinBake.Auto())
                foreach (var part in _parts) if (part.Skin != null) part.Skin.BakeMesh(part.Mesh);
            _bakedFrame = frame; _bakedMove = move;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            PoseBakeCount++;
#endif
        }
        public void Render()
        {
            if (_contourOnly) return;
            if (_renderedVersion==PoseVersion || SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null) return;
            using var marker = FramePerformance.PaperRender.Auto();
            // A shared hit contour is authoritative immediately. Only the
            // final displayed explicit pose needs this capture's render meshes.
            // Keep that real bake inside the render CPU marker.
            if (_hasSample && (_bakedFrame != _frame || _bakedMove != _move))
            {
                BakePose(_frame, _move);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                DeferredRenderBakeCount++;
                PaperSamplingDiagnostics.Record(PaperSamplingDiagnostics.Event.DeferredRenderBake);
#endif
            }
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
            if (_disposed) return; _disposed = true;
            if (Texture != null) { Texture.Release(); Destroy(Texture); }
            foreach (var part in _parts)
            { part.Indices.Dispose(); part.Projected.Dispose(); if (part.Skin != null) Destroy(part.Mesh); }
            _silhouette.Dispose();
            if (HitMesh != null) Destroy(HitMesh);
            Destroy(_root);
            if (--_activeCaptures == 0) ResetSharedPoses();
#if UNITY_EDITOR
            if (!Application.isPlaying) { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(_scene); return; }
#endif
            if (_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
        }
        static void Destroy(Object value) { if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value); }
    }
}
