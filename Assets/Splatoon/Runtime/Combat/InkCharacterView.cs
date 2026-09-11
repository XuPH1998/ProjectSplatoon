using UnityEngine;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public sealed class InkCharacterView : MonoBehaviour
    {
        public Animator Animator;
        public GameObject BoundWeaponPrefab;
        public Transform AimRoot, Nozzle;
        public Renderer[] TeamRenderers;
        public ParticleSystem SwimEffect;
        [Tooltip("局部后坐距离（米）")] public float RecoilDistance = .2f;
        [Tooltip("后坐恢复速度")] public float RecoilRecovery = 18;
        public float CameraShake = .22f;
        public Vector2 CameraKick { get; private set; }
        private Vector3 _aimPosition, _nozzleScale;
        private float _kick;
        private MaterialPropertyBlock _block;
        private Renderer[] _renderers;
        private bool _wasGrounded = true;
        private static readonly int X = UnityEngine.Animator.StringToHash("X"), Y = UnityEngine.Animator.StringToHash("Y"), Blend = UnityEngine.Animator.StringToHash("Blend"), Grounded = UnityEngine.Animator.StringToHash("Grounded"), Vertical = UnityEngine.Animator.StringToHash("VerticalSpeed");
        private void Awake()
        {
            _aimPosition = AimRoot.localPosition; _nozzleScale = Nozzle.localScale;
            _block = new MaterialPropertyBlock(); _renderers = GetComponentsInChildren<Renderer>(true);
        }
        public void Shot()
        { _kick = 1; }
        public void Present(PlayerSnapshot state, float dt, double now)
        {
            bool visible = state.Health > 0 && !state.Swimming;
            foreach (var renderer in _renderers) if (!(renderer is ParticleSystemRenderer)) renderer.enabled = visible;
            Animator.enabled = visible;
            if (visible)
            {
                var local = Quaternion.Inverse(Quaternion.Euler(0, state.Yaw, 0)) * state.Velocity;
                float speed = GameplayConfig.Character.MoveSpeed;
                Animator.SetFloat(X, local.x / speed, .1f, dt); Animator.SetFloat(Y, local.z / speed, .1f, dt);
                Animator.SetFloat(Blend, new Vector2(local.x, local.z).magnitude / speed, .12f, dt);
                Animator.SetBool("shooting", true); Animator.SetBool(Grounded, state.Grounded); Animator.SetFloat(Vertical, state.Velocity.y);
                if (!_wasGrounded && state.Grounded) Animator.SetTrigger("Land");
            }
            _wasGrounded = state.Grounded;
            _kick *= Mathf.Exp(-RecoilRecovery * dt);
            AimRoot.localPosition = _aimPosition + Vector3.back * (_kick * RecoilDistance);
            AimRoot.localRotation = Quaternion.Slerp(AimRoot.localRotation, Quaternion.Euler(Mathf.Clamp(state.Pitch, -65, 75), 0, 0), 1 - Mathf.Exp(-20 * dt));
            Nozzle.localScale = Vector3.Scale(_nozzleScale, new Vector3(1, 1 + _kick * .35f, 1 + _kick * .35f));
            CameraKick = new Vector2(-_kick * CameraShake, Mathf.Sin(Time.time * 71) * _kick * CameraShake * .3f);
            var color = PrototypeArena.TeamColor(state.Team);
            if (now < state.ProtectedUntil) color = Color.Lerp(color, Color.white, .35f + .2f * Mathf.Sin(Time.time * 12));
            foreach (var renderer in TeamRenderers) { renderer.GetPropertyBlock(_block); _block.SetColor("_BaseColor", color); renderer.SetPropertyBlock(_block); }
            if (SwimEffect != null)
            {
                var main = SwimEffect.main; main.startColor = color;
                if (state.Swimming && state.Health > 0) { if (!SwimEffect.isPlaying) SwimEffect.Play(); }
                else if (SwimEffect.isPlaying) SwimEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }
}
