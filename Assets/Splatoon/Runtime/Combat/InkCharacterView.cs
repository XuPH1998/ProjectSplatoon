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
        public Transform LeftWeapon, LeftWeaponSocket, LeftNozzle, AimReference;
        public bool UseSupportGrip = true;
        public Renderer TeamMarker;
        public GameObject BoundWeaponPrefab;
        public Transform Nozzle;
        public ParticleSystem SwimEffect;
        public Vector2 CameraKick { get; private set; }
        private float _kick;
        private InkMuzzleEmitter _muzzleEffect;
        private InkMuzzleEmitter _leftMuzzleEffect;
        private MaterialPropertyBlock _block;
        public readonly struct OutlineDraw
        {
            public readonly Renderer Renderer;
            public readonly int Submeshes;
            public OutlineDraw(Renderer renderer, int submeshes) { Renderer = renderer; Submeshes = submeshes; }
        }
        public readonly System.Collections.Generic.List<OutlineDraw> OutlineDraws = new();
        private Renderer[] _renderers;
        private bool[] _rendererEnabled;
        private bool _presented, _alive, _supportGrip = true;
        private uint _revision;
        private double _turnStarted = double.NaN, _fireStarted = double.NaN, _diedAt = double.NaN;
        private double _shootEnded = double.NaN;
        private SplatlingFeedback _splatlingFeedback;
        private int _baseState;
        private PlayerSnapshot _state;
        private Transform _spine, _chest, _leftUpper, _leftLower, _leftHand;
        private Vector3 _weaponPosition;
        private Quaternion _weaponRotation;
        private Vector3 _leftWeaponPosition;
        private Quaternion _leftWeaponRotation;
        private readonly ulong[] _shotActions = new ulong[2];
        private readonly ulong[] _hiddenShotActions = new ulong[2];
        private static readonly int MoveX = UnityEngine.Animator.StringToHash("MoveX"), MoveY = UnityEngine.Animator.StringToHash("MoveY");
        private static readonly int Locomotion = UnityEngine.Animator.StringToHash("Base Layer.Locomotion"), Air = UnityEngine.Animator.StringToHash("Base Layer.Air");
        private static readonly int TurnLeft = UnityEngine.Animator.StringToHash("Base Layer.TurnLeft"), TurnRight = UnityEngine.Animator.StringToHash("Base Layer.TurnRight");
        private static readonly int DieForward = UnityEngine.Animator.StringToHash("Base Layer.DieForward"), DieBackward = UnityEngine.Animator.StringToHash("Base Layer.DieBackward");
        private static readonly int Shoot = UnityEngine.Animator.StringToHash("Shooting.AutoShoot");
        private void Awake() => InitializeBindings();
        public void BindWeapon(HeroWeaponBindings bindings, GameObject prefab)
        {
            if (_muzzleEffect != null) HeroViewBinder.Destroy(_muzzleEffect.gameObject);
            if (_leftMuzzleEffect != null) HeroViewBinder.Destroy(_leftMuzzleEffect.gameObject);
            _muzzleEffect = null;
            _leftMuzzleEffect = null;
            Weapon = bindings.transform; Nozzle = bindings.Nozzle; LeftGrip = bindings.LeftGrip; BoundWeaponPrefab = prefab;
            LeftWeapon = bindings.LeftPart; LeftNozzle = bindings.LeftNozzle; UseSupportGrip = bindings.SupportLeftHand;
            if (LeftWeapon != null) LeftWeapon.SetParent(LeftWeaponSocket, false);
            _renderers = null; _rendererEnabled = null; _presented = false;
            InitializeBindings();
        }
        public void InitializeBindings()
        {
            if (_renderers != null) return;
            _block = new MaterialPropertyBlock(); _renderers = GetComponentsInChildren<Renderer>(true);
            _rendererEnabled = new bool[_renderers.Length];
            OutlineDraws.Clear();
            for (int i = 0; i < _renderers.Length; i++)
            {
                var renderer = _renderers[i]; _rendererEnabled[i] = renderer.enabled;
                if (!renderer.enabled || renderer == TeamMarker || renderer is ParticleSystemRenderer) continue;
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null) OutlineDraws.Add(new OutlineDraw(renderer, mesh.subMeshCount));
            }
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
                if (LeftWeapon != null) { _leftWeaponPosition = LeftWeapon.localPosition; _leftWeaponRotation = LeftWeapon.localRotation; }
            }
        }
        public void Shot(byte muzzleIndex = 0, ulong actionId = 0, double born = double.NaN)
        {
            var weapon = GameplayConfig.GetWeapon(_state.HeroId);
            if (!WeaponSimulation.IsBubble(weapon) || (uint)actionId == 1) _kick = 1;
            var nozzle = muzzleIndex == 1 ? LeftNozzle : Nozzle;
            if (nozzle == null || InkPresentation.Current == null) return;
            var effect = muzzleIndex == 1 ? _leftMuzzleEffect : _muzzleEffect;

            if (effect != null && !effect.Ammo.SameValues(weapon.Ammo))
            {
                effect.Retire(InkPresentation.Current.transform); effect = null;
                if (muzzleIndex == 1) _leftMuzzleEffect = null; else _muzzleEffect = null;
            }
            if (effect == null)
            {
                effect = InkPresentation.Current.CreateMuzzle(nozzle, weapon);
                if (muzzleIndex == 1) _leftMuzzleEffect = effect; else _muzzleEffect = effect;
            }
            double now = PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : Time.timeAsDouble;
            effect?.Shot((uint)(actionId ^ (actionId >> 32)) + muzzleIndex + 1u, _state.Team,
                InkFlightPresentation.IsContinuous(weapon), now, double.IsNaN(born) ? now : born);
        }
        public void RefreshAmmo(WeaponRuntimeConfig weapon)
        {
            Refresh(ref _muzzleEffect, Nozzle); Refresh(ref _leftMuzzleEffect, LeftNozzle);
            void Refresh(ref InkMuzzleEmitter emitter, Transform nozzle)
            {
                if (emitter == null || emitter.Ammo.SameValues(weapon.Ammo) || InkPresentation.Current == null) return;
                emitter.Retire(InkPresentation.Current.transform);
                emitter = InkPresentation.Current.CreateMuzzle(nozzle, weapon);

            }
        }
        public void Present(PlayerSnapshot state, float dt, double now)
        {
            if (Profile == null || Animator == null) return;
            if (state.IsBubble) { SetBubblePose(state); return; }
            if (_bubblePose) { _bubblePose = false; Animator.speed = 1; _presented = false; }
            _state = state;
            bool allowMuzzle = state.Health > 0 && !state.Swimming;
            _muzzleEffect?.Present(state.Firing, allowMuzzle, now);
            _leftMuzzleEffect?.Present(state.Firing, allowMuzzle, now);
            if(Profile.Splatling && Application.isPlaying)
            {
                if(_splatlingFeedback==null)_splatlingFeedback=gameObject.AddComponent<SplatlingFeedback>();
                _splatlingFeedback.Present(state,GameplayConfig.GetWeapon(state.HeroId),Nozzle,SwimEffect!=null?SwimEffect.GetComponent<ParticleSystemRenderer>().sharedMaterial:null,dt);
            }
            bool alive = state.Health > 0, visible = !(state.Swimming || state.CompactBody) || !alive;
            bool reset = !_presented || _revision != state.Revision || (alive && !_alive);
            bool restored = visible && !Animator.enabled;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && !(_renderers[i] is ParticleSystemRenderer) && _renderers[i] != TeamMarker)
                    _renderers[i].enabled = visible && _rendererEnabled[i] && !(SpecialWeaponSimulation.Active(state) && ((Weapon!=null&&_renderers[i].transform.IsChildOf(Weapon))||(LeftWeapon!=null&&_renderers[i].transform.IsChildOf(LeftWeapon))));
            Animator.enabled = visible;
            if (reset)
            {
                _baseState = 0; _turnStarted = _fireStarted = _diedAt = double.NaN; _kick = 0;
                _supportGrip = true;
                _shotActions[0] = _shotActions[1] = 0;
                _hiddenShotActions[0] = _hiddenShotActions[1] = 0;
                if (Weapon != null && WeaponSocket != null)
                { Weapon.SetParent(WeaponSocket, false); Weapon.localPosition = _weaponPosition; Weapon.localRotation = _weaponRotation; }
                if (LeftWeapon != null && LeftWeaponSocket != null)
                { LeftWeapon.SetParent(LeftWeaponSocket, false); LeftWeapon.localPosition = _leftWeaponPosition; LeftWeapon.localRotation = _leftWeaponRotation; }
                Animator.Rebind(); ClearShootingLayers();
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
                if (Profile.SingleShot)
                {
                    PresentShot(0, state.RightShotAction, state.RightShotAt, reset || restored, now, dt, alive && !state.Swimming);
                    if (Profile.DualWield) PresentShot(1, state.LeftShotAction, state.LeftShotAt, reset || restored, now, dt, alive && !state.Swimming);
                }
                else
                {
                if (firing && (_fireStarted != state.FireStartedAt || reset || restored))
                {
                    float phase = Mathf.Repeat((float)(now - state.FireStartedAt), Profile.ShootDuration);
                    Animator.Play(Shoot, 1, phase / Profile.ShootDuration); _fireStarted = state.FireStartedAt;
                }
                bool ending = Profile.Splatling && alive && !state.Swimming && !state.CompactBody && !firing &&
                    !SplatlingSimulation.Charging(state) && state.SplatlingEndedAt > 0 && now >= state.SplatlingEndedAt && now < state.SplatlingEndedAt + Profile.ShootEndDuration;
                if (ending && (_shootEnded != state.SplatlingEndedAt || reset || restored))
                {
                    Animator.Play("Shooting.ShootEnd",1,(float)(now-state.SplatlingEndedAt)/Profile.ShootEndDuration);
                    _shootEnded=state.SplatlingEndedAt;
                }
                float weight = !alive ? 0 : Mathf.MoveTowards(Animator.GetLayerWeight(1), firing || ending ? 1 : 0, dt / Profile.BlendSeconds);
                Animator.SetLayerWeight(1, weight);
                }
                // Present runs after Unity's Animator update. Rebind restores the
                // bind pose, so evaluate the selected state now on spawn/respawn
                // and emergence instead of rendering that pose for one frame.
                if (reset || restored) Animator.Update(0);
            }
            if (!visible || !alive)
            {
                _kick = 0; ClearShootingLayers();
                _hiddenShotActions[0]=state.RightShotAction;_hiddenShotActions[1]=state.LeftShotAction;
            }
            _kick *= Mathf.Exp(-Profile.RecoilRecovery * dt);
            CameraKick = new Vector2(-_kick * Profile.CameraShake, Mathf.Sin(Time.time * 71) * _kick * Profile.CameraShake * .3f);
            var color = PrototypeArena.TeamColor(state.Team);
            if (now < state.ProtectedUntil) color = Color.Lerp(color, Color.white, .35f + .2f * Mathf.Sin(Time.time * 12));
            if (TeamMarker != null)
            {
                TeamMarker.enabled = alive && !state.Swimming && !state.CompactBody;
                TeamMarker.GetPropertyBlock(_block); _block.SetColor("_BaseColor", color); TeamMarker.SetPropertyBlock(_block);
            }
            if (SwimEffect != null)
            {
                bool wall = state.Movement == MovementMode.WallInk || state.Movement == MovementMode.Mantle;
                SwimEffect.transform.position = wall ? state.Position + Vector3.up * .35f - state.WallNormal * .25f : state.Position + Vector3.up * .05f;
                SwimEffect.transform.rotation = Quaternion.FromToRotation(Vector3.forward, wall ? state.WallNormal : Vector3.up);
                var main = SwimEffect.main; main.startColor = color;
                if (ShouldEmitSwimEffect(state)) { if (!SwimEffect.isEmitting) SwimEffect.Play(); }
                else if (SwimEffect.isPlaying) SwimEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        bool _bubblePose;
        public void SetBubblePose(PlayerSnapshot state)
        {
            if (Animator == null) return;
            _state = state; _alive = false;
            Animator.enabled = true;
            if (!_bubblePose)
            {
                Animator.Rebind(); Animator.SetFloat(MoveX, 0); Animator.SetFloat(MoveY, 0);
                ClearShootingLayers(); Animator.Play(Locomotion, 0, 0); Animator.Update(0);
                _bubblePose = true;
            }
            Animator.speed = 0;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && !(_renderers[i] is ParticleSystemRenderer) && _renderers[i] != TeamMarker)
                    _renderers[i].enabled = _rendererEnabled[i];
            if (TeamMarker != null) TeamMarker.enabled = false;
            if (SwimEffect != null) SwimEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _muzzleEffect?.Present(false, false, state.SimulatedAt); _leftMuzzleEffect?.Present(false, false, state.SimulatedAt);
            _splatlingFeedback?.Present(state, GameplayConfig.GetWeapon(state.HeroId), Nozzle, null, 1);
            _kick = 0; CameraKick = Vector2.zero;
        }
        public Bounds MeasureBubblePose(Transform root, PlayerSnapshot state)
        {
            SetBubblePose(state);
            var bounds = new Bounds(HeroBodyShape.For(state.HeroId).Center, new Vector3(.8f, HeroBodyShape.For(state.HeroId).Height, .8f));
            var mesh = new Mesh();
            foreach (var renderer in _renderers)
            {
                if (renderer == null || renderer is ParticleSystemRenderer || renderer == TeamMarker) continue;
                Bounds local;
                if (renderer is SkinnedMeshRenderer skin) { skin.BakeMesh(mesh); local = mesh.bounds; }
                else { var filter = renderer.GetComponent<MeshFilter>(); if (filter == null || filter.sharedMesh == null) continue; local = filter.sharedMesh.bounds; }
                var matrix = root.worldToLocalMatrix * renderer.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                    bounds.Encapsulate(matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1))));
            }
            HeroViewBinder.Destroy(mesh);
            return bounds;
        }

        public static bool ShouldEmitSwimEffect(PlayerSnapshot state)
        {
            if (state.Health <= 0 || !state.HasInkRecovery) return false;
            bool wall = state.Movement == MovementMode.WallInk || state.Movement == MovementMode.Mantle;
            Vector3 normal = wall ? state.WallNormal : Vector3.up;
            return Vector3.ProjectOnPlane(state.Velocity, normal).sqrMagnitude > .05f * .05f;
        }

        void ClearShootingLayers() { for (int i = 1; i < Animator.layerCount; i++) Animator.SetLayerWeight(i, 0); }
        void PresentShot(int hand, ulong action, double started, bool restore, double now, float dt, bool allowed)
        {
            int layer = hand + 1;
            float duration = Mathf.Max(.01f, Profile.ShotPlaybackSeconds);
            float elapsed = Mathf.Max(0, (float)(now - started));
            bool active = allowed && action != 0 && action != _hiddenShotActions[hand] && elapsed < duration;
            if (active && (_shotActions[hand] != action || restore))
            {
                Animator.Play(hand == 0 ? "Shooting.Shot" : "ShootingLeft.Shot", layer, elapsed / duration);
                _shotActions[hand] = action;
            }
            float fade = Mathf.Min(Profile.BlendSeconds, duration * .1f);
            Animator.SetLayerWeight(layer, !allowed ? 0 : Mathf.MoveTowards(Animator.GetLayerWeight(layer), active ? 1 : 0, dt / Mathf.Max(.001f, fade)));
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
                var reference = AimReference != null ? AimReference : Nozzle;
                Quaternion correction = Quaternion.FromToRotation(reference.forward, direction);
                _spine.rotation = Quaternion.Slerp(Quaternion.identity, correction, Profile.SpineAimWeight) * _spine.rotation;
                _chest.rotation = Quaternion.FromToRotation(reference.forward, direction) * _chest.rotation;
            }
            if (UseSupportGrip && _supportGrip && LeftGrip != null && _leftHand != null)
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
