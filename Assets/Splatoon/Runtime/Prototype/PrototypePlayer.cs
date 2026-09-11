using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public struct PlayerSnapshot : INetworkSerializable
    {
        public Vector3 Position;
        public float Yaw, Pitch, Health, Ink;
        public double RespawnsAt, ProtectedUntil;
        public uint Revision;
        public byte Team, Slot;
        public bool Swimming;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Position); s.SerializeValue(ref Yaw); s.SerializeValue(ref Pitch);
            s.SerializeValue(ref Health); s.SerializeValue(ref Ink); s.SerializeValue(ref RespawnsAt);
            s.SerializeValue(ref ProtectedUntil); s.SerializeValue(ref Revision); s.SerializeValue(ref Team);
            s.SerializeValue(ref Slot); s.SerializeValue(ref Swimming);
        }
    }
    [RequireComponent(typeof(CharacterController))]
    public sealed class PrototypePlayer : NetworkBehaviour
    {
        public readonly NetworkVariable<PlayerSnapshot> Snapshot = new();
        [Tooltip("角色外观根节点，仅用于客户端表现")] public Transform Visual;
        [Tooltip("胶囊角色渲染器，用队伍颜色显示")] public Renderer Body;
        [Tooltip("短暂弹道线使用的材质")] public Material TracerMaterial;
        private CharacterController _controller;
        private PlayerInputFrame _input;
        private uint _sequence, _jumpSequence, _consumedJump, _revision;
        private double _lastInput, _nextShot;
        private float _vertical;
        private Vector2 _look;
        private Camera _camera;
        private byte _initialTeam, _initialSlot;
        private readonly RaycastHit[] _hits = new RaycastHit[24];
        private MaterialPropertyBlock _properties;
        public static PrototypePlayer Local { get; private set; }
        public Vector2 Look => _look;
        private void Awake() { _controller = GetComponent<CharacterController>(); _properties = new MaterialPropertyBlock(); }
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
            s.Position = transform.position; s.Health = PrototypeSettings.Value("MaxHealth"); s.Ink = PrototypeSettings.Value("MaxInk");
            s.Yaw = s.Team == 1 ? 0 : 180; s.Pitch = 12; s.Swimming = false; s.RespawnsAt = 0;
            s.ProtectedUntil = Unity.Netcode.NetworkManager.Singleton.ServerTime.Time + PrototypeSettings.Value("ProtectionSeconds");
            s.Revision++; Snapshot.Value = s; _nextShot = 0;
            _input.Look = new Vector2(s.Yaw, s.Pitch);
        }
        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                Snapshot.Value = new PlayerSnapshot { Team = _initialTeam, Slot = _initialSlot, Yaw = _initialTeam == 1 ? 0 : 180 };
                Respawn();
            }
            _controller.enabled = IsServer;
            transform.position = Snapshot.Value.Position;
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
                if (IsOwner) _look = new Vector2(s.Yaw, s.Pitch);
            }
            if (!IsServer) transform.position = Vector3.Lerp(transform.position, s.Position, 1 - Mathf.Exp(-18 * Time.deltaTime));
            Visual.localRotation = Quaternion.Slerp(Visual.localRotation, Quaternion.Euler(0, s.Yaw, 0), 1 - Mathf.Exp(-20 * Time.deltaTime));
            Visual.localScale = new Vector3(1, s.Swimming ? .35f : 1, 1);
            Visual.gameObject.SetActive(s.Health > 0);
            Color color = PrototypeArena.TeamColor(s.Team);
            if (NetworkManager.ServerTime.Time < s.ProtectedUntil) color = Color.Lerp(color, Color.white, .35f + .2f * Mathf.Sin(Time.time * 12));
            _properties.SetColor("_BaseColor", color); Body.SetPropertyBlock(_properties);
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
            if (phase == MatchPhase.Finished) return;
            if (s.Health <= 0)
            { if (PrototypeRules.CanRespawn(s.Health, now, s.RespawnsAt)) Respawn(); return; }
            var input = _input;
            if (now - _lastInput > .3) { input.Move = Vector2.zero; input.Fire = input.Swim = false; }
            s.Yaw = input.Look.x; s.Pitch = input.Look.y;
            byte floor = PrototypeMatch.Current.Grid.At(transform.position);
            s.Swimming = input.Swim && floor == s.Team && _controller.isGrounded;
            _controller.height = s.Swimming ? .7f : 1.8f; _controller.center = Vector3.up * (_controller.height * .5f);
            float speed = PrototypeSettings.Value(s.Swimming ? "SwimSpeed" : "MoveSpeed");
            if (floor != 0 && floor != 255 && floor != s.Team && _controller.isGrounded) speed *= PrototypeSettings.Value("EnemyInkMultiplier");
            if (_controller.isGrounded && _vertical < 0) _vertical = -2;
            if (input.JumpSequence != _consumedJump)
            { _consumedJump = input.JumpSequence; if (_controller.isGrounded) _vertical = PrototypeSettings.Value("JumpSpeed"); }
            _vertical -= PrototypeSettings.Value("Gravity") * dt;
            var move = Quaternion.Euler(0, s.Yaw, 0) * new Vector3(input.Move.x, 0, input.Move.y) * speed;
            _controller.Move((move + Vector3.up * _vertical) * dt);
            if (transform.position.y < -5) { Respawn(); return; }
            s.Position = transform.position;
            bool fired = false;
            if (input.Fire && !s.Swimming && now >= _nextShot && PrototypeRules.Spend(ref s.Ink, PrototypeSettings.Value("ShotInk")))
            {
                fired = true; _nextShot = now + 1.0 / PrototypeSettings.Value("FireRate"); s.ProtectedUntil = 0;
                Fire(s);
            }
            // Holding the trigger must actually empty the tank, including intervals between shots.
            if (!fired && (!input.Fire || s.Swimming)) s.Ink = PrototypeRules.Recover(s.Ink, PrototypeSettings.Value("MaxInk"), PrototypeSettings.Value(s.Swimming ? "SwimRecoverInk" : "RecoverInk"), dt);
            Snapshot.Value = s;
        }
        private void Fire(PlayerSnapshot s)
        {
            Quaternion aim = Quaternion.Euler(s.Pitch, s.Yaw, 0);
            Vector3 pivot = transform.position + Vector3.up * 1.5f;
            Vector3 cameraPosition = CameraPosition(pivot, aim);
            Vector3 direction = aim * Vector3.forward;
            float range = PrototypeSettings.Value("Range");
            Vector3 target = cameraPosition + direction * range;
            if (Raycast(cameraPosition, direction, range, out var cameraHit)) target = cameraHit.point;
            Vector3 muzzle = transform.position + Vector3.up * 1.25f + Quaternion.Euler(0, s.Yaw, 0) * new Vector3(.4f, 0, .6f);
            // Ray from body to muzzle prevents the barrel from clipping through cover.
            if (Raycast(pivot, (muzzle-pivot).normalized, Vector3.Distance(pivot,muzzle), out var blocked))
            { ShotRpc(pivot, blocked.point, s.Team); return; }
            Vector3 end = target;
            if (Raycast(muzzle, (target - muzzle).normalized, Mathf.Min(range, Vector3.Distance(muzzle, target) + .05f), out var hit))
            {
                end = hit.point;
                var victim = hit.collider.GetComponent<PrototypePlayer>();
                if (victim != null) victim.ReceiveDamage(s.Team);
                else if (hit.normal.y > .9f && Mathf.Abs(hit.point.y) < .1f) PrototypeMatch.Current.Paint(hit.point, s.Team);
            }
            ShotRpc(muzzle, end, s.Team);
        }
        private bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit closest)
        {
            int n = Physics.RaycastNonAlloc(origin, direction, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
            closest = default; float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            { if (_hits[i].collider.gameObject == gameObject || _hits[i].distance >= nearest) continue; closest = _hits[i]; nearest = closest.distance; }
            return nearest < float.MaxValue;
        }
        public void ReceiveDamage(byte attackerTeam)
        {
            if (!IsServer) return;
            var s = Snapshot.Value; double now = NetworkManager.ServerTime.Time;
            float before=s.Health;
            s.Health = PrototypeRules.Damage(s.Health, PrototypeSettings.Value("Damage"), attackerTeam == s.Team, s.ProtectedUntil, now);
            if(before!=s.Health) Debug.Log($"[LAN] Damage player={OwnerClientId} hp={s.Health:F0}");
            if (s.Health <= 0) { s.RespawnsAt = now + PrototypeSettings.Value("RespawnSeconds"); s.Swimming = false; _controller.enabled = false; }
            Snapshot.Value = s;
        }
        [Rpc(SendTo.Everyone)]
        private void ShotRpc(Vector3 from, Vector3 to, byte team)
        {
            var go = new GameObject("墨水弹道"); var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = TracerMaterial; line.positionCount = 2; line.SetPosition(0, from); line.SetPosition(1, to);
            line.startWidth = .065f; line.endWidth = .025f;
            line.startColor = line.endColor = PrototypeArena.TeamColor(team);
            Destroy(go, .075f);
        }
        public static Vector3 CameraPosition(Vector3 pivot, Quaternion rotation)
        {
            Vector3 offset = rotation * new Vector3(.65f, .15f, -3.8f);
            float distance = offset.magnitude;
            if (Physics.SphereCast(pivot, .2f, offset / distance, out var hit, distance, ~(1 << 8), QueryTriggerInteraction.Ignore)) distance = Mathf.Max(.05f, hit.distance - .08f);
            return pivot + offset.normalized * distance;
        }
        private void LateUpdate()
        {
            if (!IsSpawned || !IsOwner || _camera == null) return;
            var rotation = Quaternion.Euler(_look.y, _look.x, 0);
            _camera.transform.SetPositionAndRotation(CameraPosition(transform.position + Vector3.up * 1.5f, rotation), rotation);
        }
        public override void OnNetworkDespawn()
        {
            if (NetworkManager.NetworkTickSystem != null) NetworkManager.NetworkTickSystem.Tick -= SendInput;
            if (Local == this) Local = null;
        }
    }
}
