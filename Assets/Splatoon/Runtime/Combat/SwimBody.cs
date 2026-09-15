using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;

namespace Splatoon.Combat
{
    /// <summary>Paper rendering and independent hit proxies. Collision never follows render correction offsets.</summary>
    public sealed class SwimBody : MonoBehaviour
    {
        public const int HitProxyLayer = 10;
        public MeshRenderer BodyRenderer;
        public MeshCollider HitVolume;
        public PaperBodyProfile Profile;
        [FormerlySerializedAs("RemoteBody")] public CapsuleCollider CapsuleHitVolume;
        CharacterController _controller;
        float _standingHeight;
        Vector3 _standingCenter;
        MaterialPropertyBlock _color;
        PaperCapture _capture;
        public PaperCapture Capture => _capture;
        public IReadOnlyList<Rect> HitRects => _capture?.HitRects;
        static readonly List<SwimBody> Active = new();
        public static IReadOnlyList<SwimBody> ActiveBodies => Active;
        public bool FlatHitActive => HitVolume != null && HitVolume.enabled;
        public bool UsesHitProxy => FlatHitActive || (CapsuleHitVolume != null && CapsuleHitVolume.enabled);

        void Awake()
        {
            _controller = GetComponentInParent<CharacterController>();
            Bind(Profile);
            ApplyCollision(default);
            if (BodyRenderer != null) BodyRenderer.enabled = false;
        }

        public void Bind(PaperBodyProfile profile)
        {
            // This layer is queryable by weapons but has no physical contacts.
            HitVolume.gameObject.layer = CapsuleHitVolume.gameObject.layer = HitProxyLayer;
            if (profile == null) return;
            if (Profile != profile) ReleaseCapture();
            Profile = profile;
            HitVolume.enabled = false; HitVolume.isTrigger = false; HitVolume.convex = false;
            HitVolume.sharedMesh = null;
            BodyRenderer.GetComponent<MeshFilter>().sharedMesh = profile.DisplayMesh;
            BodyRenderer.sharedMaterial = profile.Material;
        }

        void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        void OnDisable()
        {
            Active.Remove(this);
            if (HitVolume != null) HitVolume.enabled = false;
            if (BodyRenderer != null) BodyRenderer.enabled = false;
            ReleaseCapture();
        }
        void OnDestroy() => ReleaseCapture();
        void ReleaseCapture() { if (HitVolume != null) HitVolume.sharedMesh = null; _capture?.Dispose(); _capture = null; }
        void Sample(PlayerSnapshot state)
        {
            if (Profile == null || Profile.CapturePrefab == null) return;
            _capture ??= new PaperCapture(Profile);
            // Publish a newly sampled silhouette to PhysX once per animation frame.
            if (_capture.Sample(state) || HitVolume.sharedMesh == null)
            { HitVolume.sharedMesh = null; HitVolume.sharedMesh = _capture.HitMesh; }
        }

        PlayerSnapshot Frame(PlayerSnapshot state)
        {
            if (state.ShowsSwimBody && state.PaperPose == PaperPose.None)
                PaperPoseSimulation.Resolve(ref state, default, Profile, state.SimulatedAt);
            return state;
        }

        public void ApplyCollision(PlayerSnapshot state)
        {
            if (_controller == null) _controller = GetComponentInParent<CharacterController>();
            if (_standingHeight == 0)
            {
                _standingHeight = _controller != null ? _controller.height : CapsuleHitVolume.height;
                _standingCenter = _controller != null ? _controller.center : CapsuleHitVolume.center;
            }
            bool alive = state.Health > 0;
            state = Frame(state);
            HitVolume.enabled = alive && state.ShowsSwimBody;
            if (HitVolume.enabled)
            {
                Sample(state);
                HitVolume.transform.SetPositionAndRotation(state.PaperCenter, state.PaperRotation);
            }
            // Human proxies remain available when remote movement is disabled.
            CapsuleHitVolume.enabled = alive && !HitVolume.enabled &&
                (state.Swimming || (_controller != null && !_controller.enabled));
            if (_controller != null)
            {
                CapsuleHitVolume.radius = _controller.radius;
                CapsuleHitVolume.height = state.Swimming ? .7f : _standingHeight;
                CapsuleHitVolume.center = state.Swimming ? Vector3.up * .35f : _standingCenter;
            }
        }

        public void Present(PlayerSnapshot state, Vector3 visualOffset, Quaternion rotation)
        {
            state = Frame(state);
            BodyRenderer.enabled = state.ShowsSwimBody;
            if (!BodyRenderer.enabled) return;
            // Presentation consumes the last simulated pose; it cannot recook or
            // move authority hit geometry using an interpolated render state.
            if (_capture == null) Sample(state);
            _capture?.Render();
            Vector3 normal = state.PaperRotation * Vector3.forward;
            if (state.PaperPose == PaperPose.Ground || state.PaperPose == PaperPose.Wall)
                visualOffset = Vector3.ProjectOnPlane(visualOffset, normal);
            BodyRenderer.transform.SetPositionAndRotation(state.PaperCenter + visualOffset, state.PaperRotation);
            _color ??= new MaterialPropertyBlock();
            BodyRenderer.GetPropertyBlock(_color);
            _color.SetColor("_BaseColor", PrototypeArena.TeamColor(state.Team));
            var viewer = PrototypePlayer.Local;
            _color.SetFloat("_EnemyOutline", viewer != null && EnemyOutlineRules.IsEnemy(state, viewer.PresentedState.Team) ? 1 : 0);
            if (_capture?.Texture != null)
            {
                var texture = _capture.Texture;
                _color.SetTexture("_BaseMap", texture);
                _color.SetVector("_BaseMap_TexelSize", new Vector4(1f / texture.width, 1f / texture.height, texture.width, texture.height));
            }
            BodyRenderer.SetPropertyBlock(_color);
        }

        public Vector3 ClosestHitPoint(Vector3 worldPoint) => _capture == null ? worldPoint :
            HitVolume.transform.TransformPoint(_capture.ClosestPoint(HitVolume.transform.InverseTransformPoint(worldPoint)));

        public bool IsHitVolume(Collider collider) => collider == HitVolume || collider == CapsuleHitVolume;
    }
}
