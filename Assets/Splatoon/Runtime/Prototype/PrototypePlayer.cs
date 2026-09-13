using Unity.Netcode;
using Splatoon.Config;
using Splatoon.Combat;
using UnityEngine;
using UnityEngine.InputSystem;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public struct PlayerSnapshot : INetworkSerializable
    {
        public const uint ProtocolVersion = 5;
        public Vector3 Position, Velocity;
        public float Yaw, Pitch, Health, Ink;
        public float BodyYaw, TurnStartYaw;
        public sbyte TurnDirection;
        public byte DeathDirection;
        public double TurnStartedAt, FireStartedAt, DiedAt;
        public double RespawnsAt, ProtectedUntil;
        public uint Revision;
        public byte Team, Slot;
        public bool Swimming, Grounded, Firing;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Position); s.SerializeValue(ref Yaw); s.SerializeValue(ref Pitch);
            s.SerializeValue(ref Health); s.SerializeValue(ref Ink); s.SerializeValue(ref RespawnsAt);
            s.SerializeValue(ref ProtectedUntil); s.SerializeValue(ref Revision); s.SerializeValue(ref Team);
            s.SerializeValue(ref Slot); s.SerializeValue(ref Swimming); s.SerializeValue(ref Velocity); s.SerializeValue(ref Grounded); s.SerializeValue(ref Firing);
            s.SerializeValue(ref BodyYaw); s.SerializeValue(ref TurnStartYaw); s.SerializeValue(ref TurnDirection);
            s.SerializeValue(ref TurnStartedAt); s.SerializeValue(ref FireStartedAt); s.SerializeValue(ref DiedAt); s.SerializeValue(ref DeathDirection);
        }
    }
    [RequireComponent(typeof(CharacterController))]
    public sealed class PrototypePlayer : NetworkBehaviour
    {
        public static readonly System.Collections.Generic.Dictionary<ulong, PrototypePlayer> ByOwner = new();
        public readonly NetworkVariable<PlayerSnapshot> Snapshot = new();
        [Tooltip("角色外观根节点，仅用于客户端表现")] public Transform Visual;
        [Tooltip("人物模型、方向动画和持枪约束")] public InkCharacterView CharacterView;
        [Tooltip("独立于美术后坐力的逻辑枪口")] public Transform SimulationMuzzle;
        public Vector3 SimulationAimPivot;
        public GameObject BoundVisualPrefab;
        public CharacterPresentationProfile Presentation => CharacterView != null ? CharacterView.Profile : null;
        public Vector3 CameraPivot => transform.position + (Presentation != null ? Presentation.CameraPivot : Vector3.up * 1.5f);
        public Vector3 MuzzleOffset(float pitch) => Presentation != null ? Presentation.MuzzleOffset(pitch) : SimulationAimPivot + Quaternion.Euler(pitch, 0, 0) * (SimulationMuzzle.localPosition - SimulationAimPivot);
        private CharacterController _controller;
        private PlayerInputFrame _input;
        private uint _sequence, _jumpSequence, _consumedJump, _revision;
        private double _lastInput, _nextShot;
        private float _vertical;
        private Vector2 _look;
        private Camera _camera;
        private byte _initialTeam, _initialSlot;


        public static PrototypePlayer Local { get; private set; }
        public Vector2 Look => _look;
        private void Awake() { _controller = GetComponent<CharacterController>(); }
        public void Initialize(byte team, byte slot)
        {
            _initialTeam = team; _initialSlot = slot;
        }
        public void Respawn()
        {
            var s = Snapshot.Value;
            _controller.enabled = false;
            transform.position = PrototypeArena.Spawn(s.Team, s.Slot);
            _controller.height = 1.8f; _controller.center = Vector3.up * .9f;
            _controller.enabled = true;
            _vertical = 0; _input.Fire = _input.Swim = false; _input.Move = Vector2.zero; _consumedJump = _input.JumpSequence;
            s.Position = transform.position; s.Health = GameplayConfig.Character.MaxHealth; s.Ink = GameplayConfig.Character.MaxInk;
            s.Yaw = s.Team == 1 ? 0 : 180; s.Pitch = 12; s.Swimming = false; s.RespawnsAt = 0;
            s.BodyYaw = s.TurnStartYaw = s.Yaw; s.TurnDirection = 0; s.TurnStartedAt = s.FireStartedAt = s.DiedAt = 0; s.DeathDirection = 0;
            s.ProtectedUntil = Unity.Netcode.NetworkManager.Singleton.ServerTime.Time + GameplayConfig.Mode.ProtectionSeconds;
            s.Velocity = Vector3.zero; s.Firing = false; s.Grounded = true; s.Revision++; Snapshot.Value = s; _nextShot = 0;
            _input.Look = new Vector2(s.Yaw, s.Pitch);
        }
        public override void OnNetworkSpawn()
        {
            ByOwner[OwnerClientId] = this;
            if (IsServer)
            {
                Snapshot.Value = new PlayerSnapshot { Team = _initialTeam, Slot = _initialSlot, Yaw = _initialTeam == 1 ? 0 : 180 };
                Respawn();
            }
            _controller.enabled = IsServer;
            transform.position = Snapshot.Value.Position;
            Visual.localRotation = Quaternion.Euler(0, Snapshot.Value.BodyYaw, 0);
            _revision = Snapshot.Value.Revision;
            if (IsOwner)
            {
                Local = this; _look = new Vector2(Snapshot.Value.Yaw, 12);
                _camera = Camera.main; NetworkManager.NetworkTickSystem.Tick += SendInput;
                PrototypeApp.Current.CaptureMouse(true);
            }
        }
        private void Update()
        {
            if (!IsSpawned) return;
            var s = Snapshot.Value;
            if (_revision != s.Revision)
            {
                _revision = s.Revision; transform.position = s.Position;
                Visual.localRotation = Quaternion.Euler(0, Presentation != null ? s.BodyYaw : s.Yaw, 0);
                if (IsOwner) _look = new Vector2(s.Yaw, s.Pitch);
            }
            if (!IsServer) transform.position = Vector3.Lerp(transform.position, s.Position, 1 - Mathf.Exp(-18 * Time.deltaTime));
            float bodyYaw = Presentation != null ? s.BodyYaw : s.Yaw;
            Visual.localRotation = Quaternion.Slerp(Visual.localRotation, Quaternion.Euler(0, bodyYaw, 0), 1 - Mathf.Exp(-20 * Time.deltaTime));
            CharacterView.Present(s, Time.deltaTime, NetworkManager.ServerTime.Time);
            if (!IsOwner || !PrototypeApp.Current.HasControl) return;
            if (Mouse.current != null)
            {
                var delta = Mouse.current.delta.ReadValue();
                _look.x = Mathf.Repeat(_look.x + delta.x * .12f, 360);
                _look.y = Mathf.Clamp(_look.y - delta.y * .12f, -65, 75);
            }
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) _jumpSequence++;
        }
        private void SendInput()
        {
            if (!IsOwner || !IsSpawned) return;
            var k = Keyboard.current;
            var frame = new PlayerInputFrame { Sequence = ++_sequence, Tick = (uint)NetworkManager.ServerTime.Tick, JumpSequence = _jumpSequence, Look = _look };
            if (PrototypeApp.Current.HasControl && k != null)
            {
                frame.Move = new Vector2((k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0), (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0));
                frame.Fire = Mouse.current != null && Mouse.current.leftButton.isPressed;
                frame.Swim = k.leftShiftKey.isPressed;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            PrototypeSmoke.ModifyInput(this, ref frame);
            if (PrototypeSmoke.Active) _look = frame.Look;
            RifleGirlSmoke.ModifyInput(this, ref frame);
            if (RifleGirlSmoke.Active) _look = frame.Look;
#endif
            if (IsServer) AcceptInput(frame); else InputRpc(frame);
        }
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        private void InputRpc(PlayerInputFrame frame, RpcParams rpc = default)
        { if (rpc.Receive.SenderClientId == OwnerClientId) AcceptInput(frame); }
        private void AcceptInput(PlayerInputFrame frame)
        {
            if (frame.Sequence <= _input.Sequence || !float.IsFinite(frame.Move.x) || !float.IsFinite(frame.Move.y) || !float.IsFinite(frame.Look.x) || !float.IsFinite(frame.Look.y)) return;
            if (System.Math.Abs((long)frame.Tick - NetworkManager.ServerTime.Tick) > 150) return;
            frame.Move = Vector2.ClampMagnitude(frame.Move, 1);
            frame.Look.x = Mathf.Repeat(frame.Look.x, 360); frame.Look.y = Mathf.Clamp(frame.Look.y, -65, 75);
            _input = frame; _lastInput = NetworkManager.ServerTime.Time;
        }
        public void Simulate(float dt, double now, MatchPhase phase)
        {
            if (!IsServer) return;
            var s = Snapshot.Value;
            if (phase == MatchPhase.Finished)
            {
                if (s.Firing || s.TurnDirection != 0 || s.Velocity != Vector3.zero)
                { s.Firing = false; s.TurnDirection = 0; s.Velocity = Vector3.zero; Snapshot.Value = s; }
                return;
            }
            if (s.Health <= 0)
            { if (PrototypeRules.CanRespawn(s.Health, now, s.RespawnsAt)) Respawn(); return; }
            var input = _input;
            if (now - _lastInput > GameplayConfig.Global.InputTimeout) { input.Move = Vector2.zero; input.Fire = input.Swim = false; }
            s.Yaw = input.Look.x; s.Pitch = input.Look.y;
            byte floor = PrototypeArena.Current.FloorOwner(transform.position);
            s.Swimming = input.Swim && floor == s.Team && _controller.isGrounded;
            _controller.height = s.Swimming ? .7f : 1.8f; _controller.center = Vector3.up * (_controller.height * .5f);
            float speed = (s.Swimming ? GameplayConfig.Character.SwimSpeed : GameplayConfig.Character.MoveSpeed);
            if (floor != 0 && floor != 255 && floor != s.Team && _controller.isGrounded) speed *= GameplayConfig.Character.EnemyInkMultiplier;
            if (_controller.isGrounded && _vertical < 0) _vertical = -2;
            if (input.JumpSequence != _consumedJump)
            { _consumedJump = input.JumpSequence; if (_controller.isGrounded) _vertical = GameplayConfig.Character.JumpSpeed; }
            _vertical -= GameplayConfig.Character.Gravity * dt;
            var move = Quaternion.Euler(0, s.Yaw, 0) * new Vector3(input.Move.x, 0, input.Move.y) * speed;
            Vector3 beforeMove = transform.position;
            _controller.Move((move + Vector3.up * _vertical) * dt);
            s.Velocity = (transform.position - beforeMove) / dt; s.Grounded = _controller.isGrounded;
            if (transform.position.y < -5) { Respawn(); return; }
            s.Position = transform.position;
            if (Presentation != null) CharacterFacing.Step(ref s, Presentation, dt, now);
            else s.BodyYaw = s.Yaw;
            bool fired = false;
            var weapon = GameplayConfig.Weapon;
            bool wasFiring = s.Firing;
            s.Firing = input.Fire && !s.Swimming && s.Ink + .00001f >= weapon.ShotInk;
            if (s.Firing && !wasFiring) s.FireStartedAt = now;
            if (s.Firing)
            {
                _nextShot = System.Math.Max(_nextShot, now - dt);
                while (_nextShot < now - 1e-8 && PrototypeRules.Spend(ref s.Ink, weapon.ShotInk))
                {
                    fired = true; s.ProtectedUntil = 0;
                    PrototypeMatch.Current.Projectiles.Spawn(this, s, _nextShot, PrototypeMatch.Current.State.Value.Round);
                    _nextShot += 1.0 / weapon.FireRate;
                }
            }
            else _nextShot = now;
            // Holding the trigger must actually empty the tank, including intervals between shots.
            if (!fired && (!input.Fire || s.Swimming)) s.Ink = PrototypeRules.Recover(s.Ink, GameplayConfig.Character.MaxInk, (s.Swimming ? GameplayConfig.Character.SwimRecoverInk : GameplayConfig.Character.RecoverInk), dt);
            if (s.Ink + .00001f < weapon.ShotInk) s.Firing = false;
            Snapshot.Value = s;
        }
        public void ReceiveDamage(byte attackerTeam, float damage, Vector3 incomingVelocity = default)
        {
            if (!IsServer) return;
            var s = Snapshot.Value; double now = NetworkManager.ServerTime.Time;
            if (s.Health <= 0) return;
            float before=s.Health;
            s.Health = PrototypeRules.Damage(s.Health, damage, attackerTeam == s.Team && !GameplayConfig.Mode.FriendlyFire, s.ProtectedUntil, now);
            if(before!=s.Health) Debug.Log($"[LAN] Damage player={OwnerClientId} hp={s.Health:F0}");
            if (s.Health <= 0)
            {
                s.RespawnsAt = now + GameplayConfig.Mode.RespawnSeconds; s.DiedAt = now;
                s.DeathDirection = CharacterFacing.DeathDirection(s.BodyYaw, incomingVelocity);
                s.Firing = s.Swimming = false; s.TurnDirection = 0; s.Velocity = Vector3.zero;
                _controller.enabled = false;
            }
            Snapshot.Value = s;
        }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        internal void ValidateMapSwimming()
        {
            if (!IsServer) return;
            var original = Snapshot.Value; var oldInput = _input; float oldVertical = _vertical; double oldLastInput = _lastInput;
            try
            {
                foreach (var position in new[] { new Vector3(1, .08f, 1), new Vector3(1, 3.08f, 1), new Vector3(10.5f, 1.58f, -8) })
                {
                    _controller.enabled = false; transform.position = position; _controller.enabled = true; Physics.SyncTransforms();
                    for (int i = 0; i < 8; i++) _controller.Move(Vector3.down * .04f);
                    if (!Physics.Raycast(transform.position + Vector3.up * .2f, Vector3.down, out var hit, .55f, ~(1 << 8))) throw new System.InvalidOperationException("诊断角色未落地");
                    var surface = hit.collider.GetComponent<Splatoon.Painting.PaintSurface>();
                    var state = original; state.Ink = 40; state.Position = transform.position; state.Health = 100; Snapshot.Value = state;
                    double now = NetworkManager.ServerTime.Time; _lastInput = now; _vertical = -2;
                    _input = new PlayerInputFrame { Swim = true, Look = Vector2.zero, JumpSequence = _consumedJump };
                    PrototypeMatch.Current.Paint(surface, hit.point, hit.normal, 1.5f, state.Team, .5f, 1);
                    Simulate(1f / 30, now, MatchPhase.Practice);
                    if (!Snapshot.Value.Swimming || Snapshot.Value.Ink <= 40) throw new System.InvalidOperationException("实际角色潜墨或回墨失败：" + surface.name);
                    PrototypeMatch.Current.Paint(surface, hit.point, hit.normal, 1.5f, (byte)(state.Team == 1 ? 2 : 1), .5f, 1);
                    _input.Move = Vector2.right;
                    Simulate(1f / 30, now, MatchPhase.Practice);
                    var velocity = Snapshot.Value.Velocity; velocity.y = 0;
                    if (Snapshot.Value.Swimming || velocity.magnitude > GameplayConfig.Character.MoveSpeed * GameplayConfig.Character.EnemyInkMultiplier + .15f)
                        throw new System.InvalidOperationException("实际角色敌墨判定失败：" + surface.name);
                }
                Debug.Log("[MAP-SMOKE] Player simulation PASS ground/bridge/ramp swimming/recovery/enemy slowdown");
            }
            finally
            {
                _controller.enabled = false; transform.position = original.Position; _controller.height = 1.8f; _controller.center = Vector3.up * .9f; _controller.enabled = true;
                Snapshot.Value = original; _input = oldInput; _vertical = oldVertical; _lastInput = oldLastInput;
            }
        }
#endif
        public static Vector3 CameraPosition(Vector3 pivot, Quaternion rotation, CharacterPresentationProfile profile = null)
        {
            Vector3 offset = rotation * (profile != null ? profile.CameraOffset : new Vector3(.65f, .15f, -3.8f));
            float distance = offset.magnitude;
            if (Physics.SphereCast(pivot, profile != null ? profile.CameraCollisionRadius : .2f, offset / distance, out var hit, distance, ~(1 << 8), QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(.05f, hit.distance - (profile != null ? profile.CameraCollisionPadding : .08f));
            return pivot + offset.normalized * distance;
        }
        private void LateUpdate()
        {
            if (!IsSpawned || !IsOwner || _camera == null) return;
            var rotation = Quaternion.Euler(_look.y, _look.x, 0);
            Vector2 kick = CharacterView.CameraKick;
            _camera.transform.SetPositionAndRotation(CameraPosition(CameraPivot, rotation, Presentation), rotation * Quaternion.Euler(kick.x, kick.y, 0));
        }
        public override void OnNetworkDespawn()
        {
            ByOwner.Remove(OwnerClientId);
            if (NetworkManager.NetworkTickSystem != null) NetworkManager.NetworkTickSystem.Tick -= SendInput;
            if (Local == this) Local = null;
        }
    }
}
