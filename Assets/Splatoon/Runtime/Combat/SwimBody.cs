using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.Serialization;

namespace Splatoon.Combat
{
    /// <summary>Shared neutral swim visual and hit proxies. Collision never follows render correction offsets.</summary>
    public sealed class SwimBody : MonoBehaviour
    {
        public MeshRenderer BodyRenderer;
        public MeshCollider HitVolume;
        [FormerlySerializedAs("RemoteBody")] public CapsuleCollider CapsuleHitVolume;
        CharacterController _controller;
        float _standingHeight;
        Vector3 _standingCenter;
        MaterialPropertyBlock _color;
        public bool FlatHitActive => HitVolume != null && HitVolume.enabled;
        public bool UsesHitProxy => FlatHitActive || (CapsuleHitVolume != null && CapsuleHitVolume.enabled);

        void Awake()
        {
            _controller = GetComponentInParent<CharacterController>();
            ApplyCollision(default);
            if (BodyRenderer != null) BodyRenderer.enabled = false;
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
            HitVolume.enabled = alive && state.ShowsSwimBody;
            HitVolume.transform.localRotation = Quaternion.Euler(0, state.BodyYaw, 0);
            // Friendly ink keeps the original .7m capsule. Use an explicit query proxy:
            // a moving CharacterController can overlap here while its ray/sweep query misses.
            // The same proxy supplies human aiming when remote movement is disabled.
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
            BodyRenderer.enabled = state.ShowsSwimBody;
            if (!BodyRenderer.enabled) return;
            BodyRenderer.transform.localPosition = HitVolume.transform.localPosition + visualOffset;
            BodyRenderer.transform.localRotation = rotation;
            _color ??= new MaterialPropertyBlock();
            BodyRenderer.GetPropertyBlock(_color);
            _color.SetColor("_BaseColor", PrototypeArena.TeamColor(state.Team));
            BodyRenderer.SetPropertyBlock(_color);
        }

        public bool IsHitVolume(Collider collider) => collider == HitVolume || collider == CapsuleHitVolume;
    }
}
