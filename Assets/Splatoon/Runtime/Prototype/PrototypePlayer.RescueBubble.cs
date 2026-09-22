using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        RescueBubbleMotor _bubbleMotor;
        RescueBubbleView _bubbleView;
        ulong? _downedBy;
        byte _downedTeam;
        uint _interactSequence, _queuedInteract;
        ulong _interactTarget;
        uint _interactTargetLife;
        public PrototypePlayer BubbleInteractionTarget { get; private set; }
        RescueBubbleMotor BubbleMotor => _bubbleMotor ??= new RescueBubbleMotor();

        void CaptureBubbleInteraction()
        {
            BubbleInteractionTarget = FindBubbleTarget();
            if (Keyboard.current == null || !Keyboard.current.fKey.wasPressedThisFrame || BubbleInteractionTarget == null || !Application.isFocused) return;
            _interactSequence++;
            _interactTarget = BubbleInteractionTarget.PlayerId;
            _interactTargetLife = BubbleInteractionTarget.PresentedState.Revision;
        }
        public PrototypePlayer FindBubbleTarget()
        {
            var match = PrototypeMatch.Current;
            if (match == null || match.State.Value.Phase == MatchPhase.Finished || !PresentedState.IsAlive || PrototypeApp.Current == null || !PrototypeApp.Current.CanFireInput) return null;
            PrototypePlayer best = null; float distance = float.MaxValue;
            foreach (var candidate in ByOwner.Values)
            {
                if (candidate == this || !candidate.IsSpawned) continue;
                var target = candidate.PresentedState;
                if (!CanInteract(PresentedState, target, target.Revision, NetworkManager.ServerTime.Time)) continue;
                float d = Vector3.Distance(InteractionPoint(PresentedState), target.BubbleCenter) - target.BubbleRadius;
                if (d < distance || (Mathf.Approximately(d, distance) && best != null && candidate.PlayerId < best.PlayerId)) { best = candidate; distance = d; }
            }
            return best;
        }
        public static Vector3 InteractionPoint(PlayerSnapshot state) => state.Position + (state.ShowsSwimBody ? Vector3.up * .15f : HeroBodyShape.For(state.HeroId).Center);
        public static bool CanInteract(PlayerSnapshot actor, PlayerSnapshot target, uint life, double now)
        {
            var point = InteractionPoint(actor);
            if (!RescueBubbleRules.Eligible(actor, target, life, now, GameplayConfig.Mode.BubbleInteractionRange, point)) return false;
            Vector3 delta = target.BubbleCenter - point;
            return !Physics.Raycast(point, delta.normalized, delta.magnitude, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
        void QueueBubbleInteraction(PlayerInputFrame input)
        {
            if (input.InteractSequence <= _queuedInteract) return;
            _queuedInteract = input.InteractSequence;
            PrototypeMatch.Current?.QueueBubbleInteraction(this, input);
        }
        public void ResolveBubbleInteraction(PlayerInputFrame input, double now)
        {
            var match = PrototypeMatch.Current;
            var actor = Snapshot.Value;
            if (!IsServer || match == null || match.State.Value.Phase == MatchPhase.Finished || input.Revision != actor.Revision ||
                input.InteractSequence <= actor.ConsumedInteract) return;
            actor.ConsumedInteract = input.InteractSequence; Snapshot.Value = actor;
            if (!ByOwner.TryGetValue(input.InteractTarget, out var target) || target == this || !target.IsSpawned ||
                !CanInteract(actor, target.Snapshot.Value, input.InteractTargetLife, now)) return;
            target.EndBubble(actor.Team == target.Snapshot.Value.Team ? BubbleOutcome.Rescued : BubbleOutcome.Executed, now);
        }
        void EnterBubble(ref PlayerSnapshot s, byte team, ulong? attacker, Vector3 incoming, double now)
        {
            _downedBy = attacker; _downedTeam = team;
            s.BubbleInkTeam = team;
            s.LifeState = PlayerLifeState.Bubble; s.BubbleUntil = now + GameplayConfig.Mode.BubbleSeconds;
            s.RespawnsAt = s.DiedAt = 0; s.ProtectedUntil = 0;
            s.DeathDirection = CharacterFacing.DeathDirection(s.BodyYaw, incoming);
            s.Position = PlayerMotorSimulation.HumanPosition(s); s.AirHumanOffset = s.CameraRebaseOffset = 0;
            s.Swimming = s.CompactBody = s.Firing = false; s.PaperPose = PaperPose.None;
            s.SwimSource = s.AirSwimSource = SwimSurface.None; s.TurnDirection = 0;
            WeaponSimulation.Cancel(ref s, _lastInput, true); SubWeaponSimulation.Cancel(ref s, _lastInput);
            s.PlanarVelocity = s.Velocity = Vector3.zero; s.VerticalSpeed = 0;
            s.Movement = MovementMode.Bubble; _controller.enabled = false;
            EnsureHeroPresentation(s.HeroId);
            var bounds = CharacterView != null ? CharacterView.MeasureBubblePose(transform, s) : new Bounds(HeroBodyShape.For(s.HeroId).Center, Vector3.one * HeroBodyShape.For(s.HeroId).Height);
            s.BubbleCenterHeight = bounds.center.y;
            s.BubbleRadius = RescueBubbleRules.Radius(bounds, s.BubbleCenterHeight, GameplayConfig.Mode.BubblePadding);
            s.Position = BubbleMotor.ResolveOverlap(s.BubbleCenter, s.BubbleRadius) - Vector3.up * s.BubbleCenterHeight;
            transform.position = s.Position;
            ResetBubbleInputs(s);
            SwimBody?.ApplyCollision(s);
        }
        void ResetBubbleInputs(PlayerSnapshot s)
        {
            _serverInputs.Clear(); _inputAssembler.Clear();
            _lastInput = new PlayerInputFrame { Revision = s.Revision, HeroRevision = s.HeroRevision, Look = new Vector2(s.Yaw, s.Pitch),
                FireSequence = s.ConsumedFire, ReleaseSequence = s.ConsumedRelease, JumpSequence = s.ConsumedJump,
                SubPressSequence = s.SubConsumedPress, SubReleaseSequence = s.SubConsumedRelease, InteractSequence = s.ConsumedInteract };
        }
        public void ExpireBubble(double now)
        {
            if (IsServer && PrototypeMatch.Current != null && PrototypeMatch.Current.State.Value.Phase != MatchPhase.Finished && RescueBubbleRules.Expired(Snapshot.Value, now))
                EndBubble(BubbleOutcome.Expired, now);
        }
        public void EndBubble(BubbleOutcome outcome, double now)
        {
            var match = PrototypeMatch.Current;
            if (!IsServer || match == null || match.State.Value.Phase == MatchPhase.Finished || !Snapshot.Value.IsBubble || outcome == BubbleOutcome.None) return;
            var s = Snapshot.Value;
            if (now >= s.BubbleUntil) outcome = BubbleOutcome.Expired;
            Vector3 center = s.BubbleCenter;
            s.BubbleEvent++; s.BubbleResult = outcome; s.BubbleEndedAt = now; s.BubbleEndPosition = center;
            s.BubbleUntil = 0; s.VerticalSpeed = 0; s.PlanarVelocity = s.Velocity = Vector3.zero;
            if (outcome == BubbleOutcome.Rescued)
            {
                var hero = GameplayConfig.GetHero(s.HeroId);
                s.Revision++; s.LifeState = PlayerLifeState.Alive; s.Health = hero.MaxHealth; s.Ink = hero.MaxInk;
                s.ProtectedUntil = s.RespawnsAt = s.DiedAt = 0; s.LastDamageAt = now;
                s.MarkedUntilPink = s.MarkedUntilBlue = s.MistUntil = s.MistExposure = 0;
                s.Grounded = false; s.Movement = MovementMode.Air;
                s.AttackNeedsRelease = s.SubNeedsRelease = true;
                s.WeaponReadyAt = s.NextShotAt = s.AttackRecoveryUntil = s.AttackMoveUntil = 0;
                s.SubReadyAt = s.SubRecoveryUntil = 0;
                SpreadSimulation.Reset(ref s, GameplayConfig.GetWeapon(s.HeroId));
                // The human standing shape is contained by the already depenetrated bubble.
                _motor.Restore(s); match.CombatStats.BeginLife(PlayerId, s.Team, s.Revision);
            }
            else
            {
                s.LifeState = PlayerLifeState.Dead; s.Movement = MovementMode.Dead;
                s.DiedAt = now; s.RespawnsAt = now + GameplayConfig.Mode.RespawnSeconds;
                match.CombatStats.RecordDeath(PlayerId, s.Revision, _downedBy, now, match.State.Value.Phase);
                PureInkBlast.Paint(center, GameplayConfig.Mode.BubblePaintRadius, _downedTeam, (uint)PlayerId * 2654435761u ^ s.Revision);
                match.PublishCombatStats();
                if (_downedBy.HasValue && ByOwner.TryGetValue(_downedBy.Value, out var killer)) killer.BubbleKillClientRpc();
            }
            ResetBubbleInputs(s); Snapshot.Value = s; SwimBody?.ApplyCollision(s);
        }
        [ClientRpc] void BubbleKillClientRpc() => ConfirmHit(true);
        void PresentBubble(PlayerSnapshot s)
        {
            if (_bubbleView == null) _bubbleView = gameObject.AddComponent<RescueBubbleView>();
            _bubbleView.Present(s, transform.position + _visualOffset, Local != null && Local.BubbleInteractionTarget == this, NetworkManager.ServerTime.Time);
        }
        void DisposeBubble()
        {
            _bubbleMotor?.Dispose(); _bubbleMotor = null;
            if (_bubbleView != null) Destroy(_bubbleView); _bubbleView = null; BubbleInteractionTarget = null;
        }
    }
}
