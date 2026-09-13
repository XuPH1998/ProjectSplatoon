using UnityEngine;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public sealed class InkCharacterView : MonoBehaviour
    {
        public Animator Animator;
        public CharacterPresentationProfile Profile;
        public Transform Weapon, WeaponSocket, LeftGrip;
        public Renderer TeamMarker;
        public GameObject BoundWeaponPrefab;
        public Transform Nozzle;
        public ParticleSystem SwimEffect;
        public Vector2 CameraKick { get; private set; }
        private float _kick;
        private ParticleSystem _muzzleEffect;
        private MaterialPropertyBlock _block;
        private Renderer[] _renderers;
        private bool[] _rendererEnabled;
        private bool _presented, _alive, _supportGrip = true;
        private uint _revision;
        private double _turnStarted = double.NaN, _fireStarted = double.NaN, _diedAt = double.NaN;
        private int _baseState;
        private PlayerSnapshot _state;
        private Transform _spine, _chest, _leftUpper, _leftLower, _leftHand;
        private Vector3 _weaponPosition;
        private Quaternion _weaponRotation;
        private static readonly int MoveX = UnityEngine.Animator.StringToHash("MoveX"), MoveY = UnityEngine.Animator.StringToHash("MoveY");
        private static readonly int Locomotion = UnityEngine.Animator.StringToHash("Base Layer.Locomotion"), Air = UnityEngine.Animator.StringToHash("Base Layer.Air");
        private static readonly int TurnLeft = UnityEngine.Animator.StringToHash("Base Layer.TurnLeft"), TurnRight = UnityEngine.Animator.StringToHash("Base Layer.TurnRight");
        private static readonly int DieForward = UnityEngine.Animator.StringToHash("Base Layer.DieForward"), DieBackward = UnityEngine.Animator.StringToHash("Base Layer.DieBackward");
        private static readonly int Shoot = UnityEngine.Animator.StringToHash("Shooting.AutoShoot");
        private void Awake() => InitializeBindings();
        public void InitializeBindings()
        {
            if (_renderers != null) return;
            _block = new MaterialPropertyBlock(); _renderers = GetComponentsInChildren<Renderer>(true);
            _rendererEnabled = new bool[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++) _rendererEnabled[i] = _renderers[i].enabled;
            if (SwimEffect != null)
            {
                ConfigureSwimEffect(SwimEffect);
                if (InkPresentation.Current != null && InkPresentation.Current.StreamPrefab != null)
                    SetInkMesh(SwimEffect, InkPresentation.Current.StreamPrefab.GetComponent<ParticleSystemRenderer>().mesh);
            }
            if (Profile != null && Animator != null && Animator.isHuman)
            {
                _spine = Animator.GetBoneTransform(HumanBodyBones.Spine);
                _chest = Animator.GetBoneTransform(HumanBodyBones.Chest);
                _leftUpper = Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _leftLower = Animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _leftHand = Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (Weapon != null) { _weaponPosition = Weapon.localPosition; _weaponRotation = Weapon.localRotation; }
            }
        }
        public void Shot()
        {
            _kick = 1;
            if (Nozzle == null || SwimEffect == null) return;
            if (_muzzleEffect == null)
            {
                var go = new GameObject("LocalMuzzleFeedback"); go.transform.SetParent(Nozzle, false);
                _muzzleEffect = go.AddComponent<ParticleSystem>(); _muzzleEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _muzzleEffect.main; main.playOnAwake = false; main.loop = false; main.startLifetime = .07f; main.startSpeed = 2; main.startSize = .07f; main.maxParticles = 24; main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = _muzzleEffect.emission; emission.enabled = false;
                var shape = _muzzleEffect.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 8; shape.radius = .01f;
                go.GetComponent<ParticleSystemRenderer>().sharedMaterial = SwimEffect.GetComponent<ParticleSystemRenderer>().sharedMaterial;
                SetInkMesh(_muzzleEffect, SwimEffect.GetComponent<ParticleSystemRenderer>().mesh);
            }
            var settings = _muzzleEffect.main; settings.startColor = PrototypeArena.TeamColor(_state.Team); _muzzleEffect.Emit(3);
        }
        public void Present(PlayerSnapshot state, float dt, double now)
        {
            if (Profile == null || Animator == null) return;
            _state = state;
            bool alive = state.Health > 0, visible = !state.Swimming || !alive;
            bool reset = !_presented || _revision != state.Revision || (alive && !_alive);
            bool restored = visible && !Animator.enabled;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && !(_renderers[i] is ParticleSystemRenderer) && _renderers[i] != TeamMarker)
                    _renderers[i].enabled = visible && _rendererEnabled[i];
            Animator.enabled = visible;
            if (reset)
            {
                _baseState = 0; _turnStarted = _fireStarted = _diedAt = double.NaN; _kick = 0;
                _supportGrip = true;
                if (Weapon != null && WeaponSocket != null)
                { Weapon.SetParent(WeaponSocket, false); Weapon.localPosition = _weaponPosition; Weapon.localRotation = _weaponRotation; }
                Animator.Rebind(); Animator.SetLayerWeight(1, 0);
            }
            _revision = state.Revision; _alive = alive; _presented = true;
            if (visible)
            {
                // Speeds are measured in the body's space; FL and BR occupy the X axis.
                var local = transform.InverseTransformDirection(state.Velocity);
                float speed = new Vector2(local.x, local.z).magnitude;
                float blend = reset || restored ? 0 : Profile.BlendSeconds;
                Animator.SetFloat(MoveX, state.Grounded && speed > .05f ? local.x / speed : 0, blend, dt);
                Animator.SetFloat(MoveY, state.Grounded && speed > .05f ? local.z / speed : 0, blend, dt);
                Animator.SetFloat("MovePlayback", speed > .05f ? speed / Profile.AnimationReferenceSpeed : 1);
                int desired = !alive ? (state.DeathDirection == 1 ? DieForward : DieBackward) : !state.Grounded ? Air
                    : state.TurnDirection < 0 ? TurnLeft : state.TurnDirection > 0 ? TurnRight : Locomotion;
                double started = !alive ? state.DiedAt : state.TurnStartedAt;
                bool timedRestart = !alive ? _diedAt != started : state.TurnDirection != 0 && _turnStarted != started;
                if (_baseState != desired || timedRestart || reset || restored)
                {
                    float offset = 0;
                    if (!alive) offset = Mathf.Clamp((float)(now - state.DiedAt), 0,
                        (state.DeathDirection == 1 ? Profile.DieForwardDuration : Profile.DieBackwardDuration) - .001f);
                    else if (state.TurnDirection != 0) offset = Mathf.Clamp((float)(now - started), 0, Profile.TurnDuration(state.TurnDirection) - .001f);
                    Animator.CrossFadeInFixedTime(desired, reset || restored || !alive ? 0 : Profile.BlendSeconds, 0, offset);
                    _baseState = desired; _turnStarted = state.TurnStartedAt; _diedAt = state.DiedAt;
                }
                bool firing = alive && !state.Swimming && state.Firing;
                if (firing && (_fireStarted != state.FireStartedAt || reset || restored))
                {
                    float phase = Mathf.Repeat((float)(now - state.FireStartedAt), Profile.ShootDuration);
                    Animator.Play(Shoot, 1, phase / Profile.ShootDuration); _fireStarted = state.FireStartedAt;
                }
                float weight = !alive ? 0 : Mathf.MoveTowards(Animator.GetLayerWeight(1), firing ? 1 : 0, dt / Profile.BlendSeconds);
                Animator.SetLayerWeight(1, weight);
            }
            if (!visible || !alive) { _kick = 0; Animator.SetLayerWeight(1, 0); }
            _kick *= Mathf.Exp(-Profile.RecoilRecovery * dt);
            CameraKick = new Vector2(-_kick * Profile.CameraShake, Mathf.Sin(Time.time * 71) * _kick * Profile.CameraShake * .3f);
            var color = PrototypeArena.TeamColor(state.Team);
            if (now < state.ProtectedUntil) color = Color.Lerp(color, Color.white, .35f + .2f * Mathf.Sin(Time.time * 12));
            if (TeamMarker != null)
            {
                TeamMarker.enabled = alive && !state.Swimming;
                TeamMarker.GetPropertyBlock(_block); _block.SetColor("_BaseColor", color); TeamMarker.SetPropertyBlock(_block);
            }
            if (SwimEffect != null)
            {
                bool wall = state.Movement == MovementMode.WallInk || state.Movement == MovementMode.Mantle;
                SwimEffect.transform.position = wall ? state.Position + Vector3.up * .35f - state.WallNormal * .25f : state.Position + Vector3.up * .05f;
                SwimEffect.transform.rotation = Quaternion.FromToRotation(Vector3.forward, wall ? state.WallNormal : Vector3.up);
                var main = SwimEffect.main; main.startColor = color;
                if (state.Swimming && alive) { if (!SwimEffect.isPlaying) SwimEffect.Play(); }
                else if (SwimEffect.isPlaying) SwimEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        public static void ConfigureSwimEffect(ParticleSystem effect)
        {
            var main = effect.main; main.simulationSpace = ParticleSystemSimulationSpace.World;
            var shape = effect.shape; shape.shapeType = ParticleSystemShapeType.Circle; shape.radius = .22f;
        }
        public static void SetInkMesh(ParticleSystem effect, Mesh mesh)
        {
            if (mesh == null) return;
            var renderer = effect.GetComponent<ParticleSystemRenderer>(); renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.mesh = mesh;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Source AnimationEvents are presentation-only; authority state always wins.
        public void SwitchSocket(AnimationEvent animationEvent)
        {
            if (animationEvent == null) return;
            string command = animationEvent.stringParameter ?? "";
            if (command.Contains("IK_OFF_Left_Handle")) _supportGrip = false;
            if (command.Contains("IK_ON_Left_Handle")) _supportGrip = true;
        }

        private void LateUpdate() => ApplyAim();
        public void ApplyAim()
        {
            if (Profile == null || !_presented || !_alive || _state.Swimming || !Animator.enabled || Nozzle == null) return;
            float yaw = transform.eulerAngles.y;
            float aimYaw = yaw + Mathf.Clamp(Mathf.DeltaAngle(yaw, _state.Yaw), -85, 85);
            Vector3 direction = Quaternion.Euler(_state.Pitch, aimYaw, 0) * Vector3.forward;
            if (_spine != null && _chest != null)
            {
                Quaternion correction = Quaternion.FromToRotation(Nozzle.forward, direction);
                _spine.rotation = Quaternion.Slerp(Quaternion.identity, correction, Profile.SpineAimWeight) * _spine.rotation;
                _chest.rotation = Quaternion.FromToRotation(Nozzle.forward, direction) * _chest.rotation;
            }
            if (_supportGrip && LeftGrip != null && _leftHand != null)
                SolveArm(_leftUpper, _leftLower, _leftHand, LeftGrip.position, LeftGrip.rotation);
        }

        internal static void SolveArm(Transform upper, Transform lower, Transform hand, Vector3 goal, Quaternion rotation)
        {
            if (upper == null || lower == null || hand == null) return;
            Vector3 start = upper.position, toGoal = goal - start;
            float a = Vector3.Distance(start, lower.position), b = Vector3.Distance(lower.position, hand.position);
            if (a < .001f || b < .001f || toGoal.sqrMagnitude < .000001f) return;
            Vector3 direction = toGoal.normalized;
            float distance = Mathf.Clamp(toGoal.magnitude, Mathf.Abs(a - b) + .001f, (a + b) * .999f);
            Vector3 bend = Vector3.ProjectOnPlane(lower.position - start, direction).normalized;
            if (bend.sqrMagnitude < .01f) bend = Vector3.Cross(direction, upper.up).normalized;
            float along = (a * a + distance * distance - b * b) / (2 * distance);
            Vector3 elbow = start + direction * along + bend * Mathf.Sqrt(Mathf.Max(0, a * a - along * along));
            upper.rotation = Quaternion.FromToRotation(lower.position - start, elbow - start) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, goal - lower.position) * lower.rotation;
            hand.rotation = rotation;
        }
    }
}
