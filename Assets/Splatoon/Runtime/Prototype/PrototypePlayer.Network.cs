using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        public static readonly Dictionary<ulong, PrototypePlayer> ByOwner = new();
        public static PrototypePlayer Local { get; private set; }
        public readonly NetworkVariable<PlayerSnapshot> Snapshot = new();
        public readonly NetworkVariable<PlayerCombatStats> Stats = new();
        public Transform Visual;
        public InkCharacterView CharacterView;
        public SwimBody SwimBody;
        public Transform SimulationMuzzle;
        public Vector3 SimulationAimPivot;
        public GameObject BoundVisualPrefab;
        public CharacterPresentationProfile Presentation => _heroView?.Content?.Profile ?? (CharacterView != null ? CharacterView.Profile : null);
        HeroViewBinder _heroView;
        public PlayerSnapshot PresentedState => ControlsLocalPlayer && !IsServer ? _predicted : Snapshot.Value;
        public Vector2 Look => _look;
        public Vector3 CameraPivot => transform.position + _visualOffset + CameraPivotOffset(PresentedState, Presentation);
        public static Vector3 CameraPivotOffset(PlayerSnapshot state, CharacterPresentationProfile profile) =>
            (state.ShowsSwimBody && profile != null && profile.Paper != null ? profile.Paper.CameraOffset :
            profile != null ? profile.CameraPivot : Vector3.up * 1.5f) + Vector3.up * state.CameraRebaseOffset;
        public Vector3 MuzzleOffset(float pitch, byte muzzle = 0) => Presentation != null ? Presentation.MuzzleOffset(pitch, muzzle) : SimulationAimPivot + Quaternion.Euler(pitch, 0, 0) * (SimulationMuzzle.localPosition - SimulationAimPivot);
        public float LastCorrectionDistance { get; private set; }
        public uint CorrectionCount { get; private set; }
        public double HitConfirmedUntil { get; private set; }
        public bool LastHitKilled { get; private set; }
        public bool MuzzleBlocked { get; private set; }
        public Vector2 ReticleViewport { get; private set; } = new(.5f, .5f);
        readonly TpsAimSolver _aimSolver = new();
        public int PendingInputCount => _history.Count;
        CharacterController _controller;
        PlayerMotorSimulation _motor;
        PlayerSnapshot _predicted;
        PlayerInputFrame _lastInput;
        readonly SortedDictionary<uint, PlayerInputFrame> _serverInputs = new();
        readonly List<PlayerInputFrame> _history = new(128);
        uint _sequence, _jumpSequence, _fireSequence, _releaseSequence;
        readonly HashSet<(uint life, uint hero, ulong action)> _playedShotActions = new();
        readonly Queue<(uint life, uint hero, ulong action)> _playedShotOrder = new();
        readonly Dictionary<(uint life, uint hero, ulong action), bool> _hitActions = new();
        readonly Queue<(uint life, uint hero, ulong action)> _hitOrder = new();
        double _lastReceivedAt;
        Vector2 _look;
        Vector3 _visualRest, _visualOffset;
        Camera _camera;
        AudioSource _audio;
        AudioClip _shotAudio, _hitAudio;
        byte _initialTeam, _initialSlot;
        bool _fireWasHeld, _waitingForPaint, _predictionPaused;
        void Awake() { _controller = GetComponent<CharacterController>(); _motor = new PlayerMotorSimulation(_controller); }
        public void Initialize(byte team, byte slot) { _initialTeam = team; _initialSlot = slot; }
        public void Respawn() => Respawn(Snapshot.Value.Team, Snapshot.Value.Slot, false);
        void Respawn(byte team, byte slot, bool requireRelease)
        {
            if (!IsServer) return;
            var old = Snapshot.Value;
            var hero = GameplayConfig.GetHero(old.HeroId);
            var s = new PlayerSnapshot { Team = team, Slot = slot, Revision = old.Revision + 1,
                HeroId = old.HeroId == 0 ? GameplayConfig.Mode.HeroId : old.HeroId, HeroRevision = old.HeroRevision, ConsumedRelease = old.ConsumedRelease,
                Health = hero.MaxHealth, Ink = hero.MaxInk,
                Position = IsTestBot ? _botPosition : PrototypeArena.Spawn(team, slot), Yaw = IsTestBot ? _botYaw : team == 1 ? 0 : 180, Pitch = 12,
                AttackNeedsRelease = requireRelease,
                ProtectedUntil = NetworkManager.ServerTime.Time + GameplayConfig.Mode.ProtectionSeconds,
                Grounded = true, Movement = MovementMode.Human, CurrentSpread = WeaponSimulation.Spread(GameplayConfig.GetWeapon(old.HeroId), false, 0),
                SimulatedAt = NetworkManager.ServerTime.Time, AcknowledgedInput = old.AcknowledgedInput,
                ShotSequence = old.ShotSequence, ConsumedFire = old.ConsumedFire, ConsumedJump = old.ConsumedJump };
            SpreadSimulation.Reset(ref s, GameplayConfig.GetWeapon(s.HeroId));
            s.BodyYaw = s.TurnStartYaw = s.Yaw; s.LastDamageAt = s.SimulatedAt;
            _serverInputs.Clear(); _lastInput = new PlayerInputFrame { Look = new Vector2(s.Yaw, s.Pitch), Revision = s.Revision, FireSequence = s.ConsumedFire, JumpSequence = s.ConsumedJump };
            _motor.Restore(s); Snapshot.Value = s;
            PrototypeMatch.Current?.CombatStats.BeginLife(PlayerId, team, s.Revision);
            if (PrototypeMatch.Current != null) Stats.Value = PrototypeMatch.Current.CombatStats.Get(PlayerId);
        }
        public override void OnNetworkSpawn()
        {
            if (IsServer) { TestBot.Value = _initialTestBot; Snapshot.Value = new PlayerSnapshot { Team = _initialTeam, Slot = _initialSlot, HeroId = _initialTestBot ? _botHero : 0 }; Respawn(); }
            ByOwner[PlayerId] = this;
            _predicted = Snapshot.Value; _controller.enabled = IsServer || ControlsLocalPlayer;
            SwimBody?.ApplyCollision(_predicted);
            transform.position = _predicted.Position;
            EnsureHeroPresentation(_predicted.HeroId);
            _visualRest = Visual.localPosition; Visual.localRotation = Quaternion.Euler(0, _predicted.BodyYaw, 0);
            Snapshot.OnValueChanged += Reconcile;
            if (ControlsLocalPlayer)
            {
                Local = this; _look = new Vector2(_predicted.Yaw, _predicted.Pitch); _camera = Camera.main;
                NetworkManager.NetworkTickSystem.Tick += SendInput;
                PrototypeApp.Current.CaptureMouse(true);
                _audio = gameObject.AddComponent<AudioSource>(); _audio.playOnAwake = false; _audio.spatialBlend = 0;
                _shotAudio = Tone("墨弹发射", 170, .035f); _hitAudio = Tone("命中确认", 720, .045f);
            }
        }
        void Update()
        {
            if (!IsSpawned || !ControlsLocalPlayer || !PrototypeApp.Current.HasControl) return;
            if (Mouse.current != null)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();
                _look.x = Mathf.Repeat(_look.x + delta.x * .12f, 360);
                _look.y = Mathf.Clamp(_look.y - delta.y * .12f, -65, 75);
                if (PrototypeApp.Current.CanFireInput)
                {
                    if (Mouse.current.leftButton.wasPressedThisFrame) _fireSequence++;
                    if (Mouse.current.leftButton.wasReleasedThisFrame) _releaseSequence++;
                }
            }
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) _jumpSequence++;
        }
        void FixedUpdate()
        {
            if (!IsSpawned || !ControlsLocalPlayer || PrototypeMatch.Current == null) return;
            if (_waitingForPaint && PrototypeMatch.Current.InitialSyncComplete && PrototypeMatch.Current.AppliedPaintSequence >= Snapshot.Value.RequiredPaintSequence)
                Reconcile(Snapshot.Value, Snapshot.Value);
            var k = Keyboard.current;
            var frame = new PlayerInputFrame { Sequence = ++_sequence, Tick = (uint)Math.Max(0, NetworkManager.ServerTime.Time * GameplayConfig.Global.SimulationRate),
                JumpSequence = _jumpSequence, FireSequence = _fireSequence, Revision = PresentedState.Revision, Look = _look, HeroRevision = PresentedState.HeroRevision,
                ReleaseSequence = _releaseSequence, CancelFire = !PrototypeApp.Current.HasControl || !PrototypeApp.Current.CanFireInput };
            if (PrototypeApp.Current.HasControl && k != null && PresentedState.Health > 0)
            {
                frame.Move = new Vector2((k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0), (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0));
                frame.Fire = PrototypeApp.Current.CanFireInput && Mouse.current != null && Mouse.current.leftButton.isPressed; frame.Swim = k.leftShiftKey.isPressed;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            PrototypeSmoke.ModifyInput(this, ref frame);
            RifleGirlSmoke.ModifyInput(this, ref frame);
            ShooterMovementSmoke.ModifyInput(this, ref frame);
            HeroSelectionSmoke.ModifyInput(this, ref frame);
            if (PrototypeSmoke.Active || RifleGirlSmoke.Active || ShooterMovementSmoke.Active || HeroSelectionSmoke.Active) { _look = frame.Look; frame.CancelFire = false; }
#endif
            frame.Move = Vector2.ClampMagnitude(frame.Move, 1);
            if (frame.Fire && !_fireWasHeld && frame.FireSequence == PresentedState.ConsumedFire) frame.FireSequence = ++_fireSequence;
            if (!frame.Fire && _fireWasHeld && frame.ReleaseSequence == PresentedState.ConsumedRelease) frame.ReleaseSequence = ++_releaseSequence;
            _fireWasHeld = frame.Fire;
            if (IsServer) AcceptInput(frame);
            else
            {
                _history.Add(frame);
                if (_history.Count > 120) { _history.RemoveAt(0); _visualOffset = Vector3.zero; }
                if (_waitingForPaint || _predictionPaused) return;
                bool shot = Step(ref _predicted, frame, 1f / GameplayConfig.Global.SimulationRate,
                    _predicted.SimulatedAt + 1.0 / GameplayConfig.Global.SimulationRate, PrototypeMatch.Current.State.Value.Phase);
                if (shot) PredictShotFeedback(_predicted);
            }
        }
        void SendInput()
        {
            if (!IsSpawned || !ControlsLocalPlayer || IsServer || _history.Count == 0) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (ShooterMovementSmoke.SuppressInputSend(this)) return;
#endif
            // Re-send unacknowledged commands; duplicates and stale lifecycle commands are ignored.
            InputBatchRpc(_history.Take(32).ToArray());
        }
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void InputBatchRpc(PlayerInputFrame[] frames, RpcParams rpc = default)
        {
            if (IsTestBot || rpc.Receive.SenderClientId != OwnerClientId || frames == null || frames.Length > 32) return;
            foreach (var frame in frames) AcceptInput(frame);
        }
        void AcceptInput(PlayerInputFrame frame)
        {
            var s = Snapshot.Value;
            if (IsTestBot) return;
            if (frame.Revision != s.Revision || frame.Sequence <= s.AcknowledgedInput || _serverInputs.ContainsKey(frame.Sequence) || _serverInputs.Count >= 120) return;
            if (frame.Tick + 1.0 < s.InputExpiredAt * GameplayConfig.Global.SimulationRate) return;
            if (!float.IsFinite(frame.Move.x) || !float.IsFinite(frame.Move.y) || !float.IsFinite(frame.Look.x) || !float.IsFinite(frame.Look.y)) return;
            if (Math.Abs((long)frame.Tick - (long)(NetworkManager.ServerTime.Time * GameplayConfig.Global.SimulationRate)) > GameplayConfig.Global.SimulationRate * 3) return;
            frame.Move = Vector2.ClampMagnitude(frame.Move, 1); frame.Look.x = Mathf.Repeat(frame.Look.x, 360); frame.Look.y = Mathf.Clamp(frame.Look.y, -65, 75);
            _serverInputs.Add(frame.Sequence, frame); _lastReceivedAt = NetworkManager.ServerTime.Time;
        }
        public void Simulate(float dt, double now, MatchPhase phase)
        {
            if (!IsServer) return;
            if (ApplyTeamRequest(phase)) return;
            var s = Snapshot.Value;
            ApplyHeroRequest(ref s, phase);
            if (s.Health <= 0 && now >= s.RespawnsAt && phase != MatchPhase.Finished) { Respawn(); return; }
            if (_serverInputs.Count > 0)
            {
                var pair = _serverInputs.First(); _serverInputs.Remove(pair.Key); _lastInput = pair.Value;
                s.AcknowledgedInput = pair.Key;
            }
            var input = _lastInput;
            bool timedOut = !IsTestBot && NetworkManager.ServerTime.Time - _lastReceivedAt > GameplayConfig.Global.InputTimeout;
            if (timedOut)
            {
                if (!s.InputTimedOut) s.InputExpiredAt = NetworkManager.ServerTime.Time;
                _serverInputs.Clear(); input.Move = Vector2.zero; input.Fire = input.Swim = false; input.CancelFire = true; input.FireSequence = s.ConsumedFire; input.JumpSequence = s.ConsumedJump;
            }
            s.InputTimedOut = timedOut;
            bool shot = Step(ref s, input, dt, now, phase);
            if (s.Position.y < -5 && phase != MatchPhase.Finished)
            {
                PrototypeMatch.Current.CombatStats.RecordDeath(PlayerId, s.Revision, null, now, phase);
                PrototypeMatch.Current.PublishCombatStats(); Respawn(); return;
            }
            Snapshot.Value = s;
            if (shot)
            {
                if (ControlsLocalPlayer) PredictShotFeedback(s);
                Physics.SyncTransforms();
                PrototypeMatch.Current.Projectiles.Spawn(this, s, now, PrototypeMatch.Current.State.Value.Round);
            }
        }
        bool Step(ref PlayerSnapshot s, PlayerInputFrame input, float dt, double now, MatchPhase phase)
        {
            EnsureHeroPresentation(s.HeroId);
            s.SimulatedAt = now; s.SimulationTick++;
            if (phase == MatchPhase.Finished)
            { WeaponSimulation.Cancel(ref s, input, true); s.TurnDirection = 0; s.Velocity = s.PlanarVelocity = Vector3.zero; SwimBody?.ApplyCollision(s); return false; }
            var hero = GameplayConfig.GetHero(s.HeroId);
            var w = GameplayConfig.GetWeapon(s.HeroId);
            if (input.HeroRevision != s.HeroRevision) { input.CancelFire = true; input.Fire = false; }
            bool wasSwimming = s.Swimming;
            bool swimPressed = input.Swim && !s.SwimWasHeld; s.SwimWasHeld = input.Swim;
            if ((WeaponSimulation.IsSemi(w) || WeaponSimulation.IsSplatling(w)) && swimPressed && !wasSwimming) WeaponSimulation.Cancel(ref s, input, true);
            bool fire = WeaponSimulation.WantsFire(s, input, w, now);
            _motor.Step(ref s, input, dt, now, fire, WeaponSimulation.IsSplatling(w) ? SplatlingSimulation.MovementSpeed(s, w) : w.ShootMoveSpeed,
                WeaponSimulation.IsSemi(w) && s.FireVisualUntil > now);
            if (IsServer && PrototypeMatch.Current != null) s.RequiredPaintSequence = PrototypeMatch.Current.PaintSequence;
            if (s.Health <= 0) { SwimBody?.ApplyCollision(s); return false; }
            if (Presentation != null) CharacterFacing.Step(ref s, Presentation, dt, now); else s.BodyYaw = s.Yaw;
            SwimBody?.ApplyCollision(s);
            bool shot = WeaponSimulation.Step(ref s, input, w, now, wasSwimming, !s.Swimming && _motor.CanStand(s.Position), out var fireResult);
            byte floor = PrototypeArena.Current != null ? PrototypeArena.Current.FloorOwner(s.Position) : (byte)255;
            ResourceSimulation.Step(ref s, hero, s.Grounded && PlayerMotorSimulation.IsEnemy(floor, s.Team), input.Fire && !WeaponSimulation.IsSemi(w) && !WeaponSimulation.IsSplatling(w), dt, now);
            return shot;
        }
        void Reconcile(PlayerSnapshot before, PlayerSnapshot authority)
        {
            EnsureHeroPresentation(authority.HeroId);
            SwimBody?.ApplyCollision(authority);
            if (authority.Revision != before.Revision)
            {
                _visualOffset = Vector3.zero;
                if (ControlsLocalPlayer) { _look = new Vector2(authority.Yaw, authority.Pitch); _history.Clear(); }
                if (!ControlsLocalPlayer && !IsServer) transform.position = authority.Position;
                if (Visual != null) Visual.localRotation = Quaternion.Euler(0, authority.BodyYaw, 0);
            }
            if (!ControlsLocalPlayer || IsServer) return;
            var oldPosition = _predicted.Position; bool lifecycle = authority.Revision != _predicted.Revision;
            _history.RemoveAll(f => f.Sequence <= authority.AcknowledgedInput || f.Revision != authority.Revision);
            _predictionPaused = authority.InputTimedOut;
            if (_predictionPaused) _history.Clear();
            _predicted = authority; _motor.Restore(authority);
            _waitingForPaint = (authority.Swimming || authority.SwimWasHeld || authority.CompactBody) && PrototypeMatch.Current != null &&
                (!PrototypeMatch.Current.InitialSyncComplete || PrototypeMatch.Current.AppliedPaintSequence < authority.RequiredPaintSequence);
            if (lifecycle)
            { _history.Clear(); _visualOffset = Vector3.zero; _look = new Vector2(authority.Yaw, authority.Pitch); }
            else if (!_waitingForPaint && !_predictionPaused && authority.Health > 0 && PrototypeMatch.Current != null)
                foreach (var frame in _history) Step(ref _predicted, frame, 1f / GameplayConfig.Global.SimulationRate,
                    _predicted.SimulatedAt + 1.0 / GameplayConfig.Global.SimulationRate, PrototypeMatch.Current.State.Value.Phase);
            LastCorrectionDistance = lifecycle ? 0 : Vector3.Distance(oldPosition, _predicted.Position);
            if (LastCorrectionDistance > .01f) CorrectionCount++;
            bool hard = lifecycle || authority.Health <= 0 || LastCorrectionDistance > .75f || before.Movement != authority.Movement;
            _visualOffset = hard ? Vector3.zero : Vector3.ClampMagnitude(_visualOffset + oldPosition - _predicted.Position, .5f);
        }
        public void ReceiveDamage(byte attackerTeam, float damage, Vector3 incomingVelocity = default, ulong? attackerClientId = null)
        {
            if (!IsServer || !float.IsFinite(damage) || damage <= 0 || PrototypeMatch.Current?.State.Value.Phase == MatchPhase.Finished) return;
            var s = Snapshot.Value; if (s.Health <= 0) return;
            double now = NetworkManager.ServerTime.Time;
            float before = s.Health;
            s.Health = PrototypeRules.Damage(s.Health, damage, attackerTeam == s.Team && !GameplayConfig.Mode.FriendlyFire, s.ProtectedUntil, now);
            if (s.Health < before) s.LastDamageAt = now;
            if (s.Health <= 0)
            {
                s.RespawnsAt = now + GameplayConfig.Mode.RespawnSeconds; s.DiedAt = now;
                s.DeathDirection = CharacterFacing.DeathDirection(s.BodyYaw, incomingVelocity);
                s.Movement = MovementMode.Dead; s.Swimming = false; WeaponSimulation.Cancel(ref s, _lastInput, true);
                s.SwimSource = s.AirSwimSource = SwimSurface.None; s.CompactBody = false; s.PaperPose = PaperPose.None;
                s.TurnDirection = 0; s.PlanarVelocity = Vector3.zero; _controller.enabled = false;
            }
            Snapshot.Value = s;
            var match = PrototypeMatch.Current;
            if (match != null)
            {
                match.CombatStats.RecordDamage(PlayerId, s.Revision, attackerClientId, attackerTeam, before - s.Health, s.Health <= 0, now, match.State.Value.Phase);
                if (s.Health <= 0) match.PublishCombatStats();
            }
        }
        public void PredictShotFeedback(PlayerSnapshot state) => PlayShotFeedback(state.ShotActionId, state.LastShotMuzzle, state.HeroId, state.Revision, state.HeroRevision);
        public void PredictShotFeedback(InkShot shot) => PlayShotFeedback(shot.ActionId, shot.MuzzleIndex, shot.HeroId, shot.Lifecycle, shot.HeroRevision);
        void PlayShotFeedback(ulong actionId, byte muzzle, int heroId, uint lifecycle, uint heroRevision)
        {
            var current = PresentedState;
            if (heroId != current.HeroId || lifecycle != current.Revision || heroRevision != current.HeroRevision || current.Health <= 0 || current.Swimming) return;
            var key = (lifecycle, heroRevision, actionId);
            if (!_playedShotActions.Add(key)) return;
            _playedShotOrder.Enqueue(key);
            while (_playedShotOrder.Count > 256) _playedShotActions.Remove(_playedShotOrder.Dequeue());
            CharacterView?.Shot(muzzle); if (ControlsLocalPlayer && _audio != null) _audio.PlayOneShot(_shotAudio, .13f);
        }
        public void ConfirmHit(InkImpact impact)
        {
            if (!ControlsLocalPlayer) return;
            var key = (impact.Lifecycle, impact.HeroRevision, impact.ActionId);
            if (_hitActions.TryGetValue(key, out bool killed))
            { if (impact.Killed && !killed) { _hitActions[key] = true; LastHitKilled = true; HitConfirmedUntil = Time.unscaledTimeAsDouble + .16; } return; }
            _hitActions.Add(key, impact.Killed); _hitOrder.Enqueue(key);
            while (_hitOrder.Count > 256) _hitActions.Remove(_hitOrder.Dequeue());
            ConfirmHit(impact.Killed);
        }
        public void ConfirmHit(bool killed)
        { if (!ControlsLocalPlayer) return; HitConfirmedUntil = Time.unscaledTimeAsDouble + .16; LastHitKilled = killed; if (_audio != null) _audio.PlayOneShot(_hitAudio, .18f); }
        void EnsureHeroPresentation(int heroId)
        {
            if (PrototypeApp.Current == null) return;
            if (heroId == 0) heroId = GameplayConfig.Mode.HeroId;
            var content = PrototypeApp.Current.Heroes.Get(heroId);
            if (ReferenceEquals(_heroView?.Content, content)) return;
            var authoredVisual = _heroView == null ? Visual : null;
            _heroView ??= new HeroViewBinder(transform);
            bool changed = _heroView.Apply(content);
            Visual = _heroView.Visual; CharacterView = _heroView.View; BoundVisualPrefab = content.CharacterPrefab;
            SwimBody?.Bind(content.Profile.Paper);
            SimulationAimPivot = content.Profile.AimPivot; SimulationMuzzle.localPosition = content.Profile.MuzzlePosition;
            if (changed) { _visualRest = Visual.localPosition; _visualOffset = Vector3.zero; }
            if (authoredVisual != null && authoredVisual != Visual)
            { authoredVisual.gameObject.SetActive(false); HeroViewBinder.Destroy(authoredVisual.gameObject); }
        }
        static AudioClip Tone(string name, float frequency, float seconds)
        {
            int count = Mathf.CeilToInt(22050 * seconds); var samples = new float[count];
            for (int i = 0; i < count; i++) samples[i] = Mathf.Sin(i * frequency * 2 * Mathf.PI / 22050) * Mathf.Pow(1 - i / (float)count, 2);
            var clip = AudioClip.Create(name, count, 1, 22050, false); clip.SetData(samples, 0); return clip;
        }
        public static Vector3 CameraPosition(Vector3 pivot, Quaternion rotation, CharacterPresentationProfile profile = null)
        {
            Vector3 offset = rotation * (profile != null ? profile.CameraOffset : new Vector3(.65f, .15f, -3.8f));
            float distance = offset.magnitude;
            if (Physics.SphereCast(pivot, profile != null ? profile.CameraCollisionRadius : .2f, offset / distance, out var hit, distance, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(.05f, hit.distance - (profile != null ? profile.CameraCollisionPadding : .08f));
            return pivot + offset.normalized * distance;
        }
        void LateUpdate()
        {
            if (!IsSpawned) return;
            var s = PresentedState;
            if (!IsServer && !ControlsLocalPlayer) transform.position = Vector3.Lerp(transform.position, s.Position, 1 - Mathf.Exp(-18 * Time.deltaTime));
            _visualOffset *= Mathf.Exp(-18 * Time.deltaTime); Visual.localPosition = _visualRest + _visualOffset;
            var bodyRotation = Quaternion.Euler(0, s.BodyYaw, 0);
            Visual.localRotation = ControlsLocalPlayer ? bodyRotation : Quaternion.Slerp(Visual.localRotation, bodyRotation, 1 - Mathf.Exp(-20 * Time.deltaTime));
            if (ControlsLocalPlayer && s.Health > 0) { s.Yaw = _look.x; s.Pitch = _look.y; }
            CharacterView.Present(s, Time.deltaTime, ControlsLocalPlayer && !IsServer ? s.SimulatedAt : NetworkManager.ServerTime.Time);
            if (!IsServer && !ControlsLocalPlayer) SwimBody?.ApplyCollision(s);
            SwimBody?.Present(s, !ControlsLocalPlayer && !IsServer ? transform.position - s.Position : _visualOffset, Visual.localRotation);
            if (!ControlsLocalPlayer || _camera == null) return;
            var rotation = Quaternion.Euler(_look.y, _look.x, 0); Vector2 kick = CharacterView.CameraKick;
            _camera.transform.SetPositionAndRotation(CameraPosition(CameraPivot, rotation, Presentation), rotation * Quaternion.Euler(kick.x, kick.y, 0));
            var aim = _aimSolver.Resolve(this, s, s.NextMuzzle);
            ReticleViewport = TpsAimSolver.ReticleViewport(_camera, aim.AimPoint);
            MuzzleBlocked = _aimSolver.IsObstructed(aim, GameplayConfig.GetWeapon(s.HeroId).CollisionRadius, PlayerId);
        }
        public override void OnNetworkDespawn()
        {
            _teamRequest = null; _heroRequest = null; TeamChangePending = HeroChangePending = false;
            _heroView?.Dispose(); _heroView = null;
            Snapshot.OnValueChanged -= Reconcile; ByOwner.Remove(PlayerId);
            if (NetworkManager.NetworkTickSystem != null) NetworkManager.NetworkTickSystem.Tick -= SendInput;
            _history.Clear(); _serverInputs.Clear(); if (_shotAudio != null) Destroy(_shotAudio); if (_hitAudio != null) Destroy(_hitAudio);
            if (Local == this) Local = null;
        }
    }
}
