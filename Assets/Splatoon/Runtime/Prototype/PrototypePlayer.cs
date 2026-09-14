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
        public const uint ProtocolVersion = 12;
        public SwimSurface SwimSource;
        public bool CompactBody;
        public bool HasInkRecovery => Swimming && SwimSource == SwimSurface.Friendly &&
            (Grounded || Movement == MovementMode.WallInk || Movement == MovementMode.Mantle);
        public bool ShowsSwimBody => Health > 0 && (CompactBody || (Swimming && SwimSource == SwimSurface.Neutral));
        public byte NextMuzzle, LastShotMuzzle;
        public bool SwimWasHeld, SemiHoldStarted;
        public double RightShotAt, LeftShotAt;
        public ulong RightShotAction, LeftShotAction;
        public int HeroId, BurstRemaining, ChargeTicks;
        public uint HeroRevision, ConsumedRelease;
        public bool AttackNeedsRelease, ChargeReleasePending;
        public double ChargeStartedAt, BurstReadyAt, FireVisualUntil;
        public float LastShotCharge;
        public Vector3 Position, Velocity;
        public float Yaw, Pitch, Health, Ink;
        public float BodyYaw, TurnStartYaw;
        public sbyte TurnDirection;
        public byte DeathDirection;
        public double TurnStartedAt, FireStartedAt, DiedAt;
        public double RespawnsAt, ProtectedUntil;
        public uint Revision;
        public byte Team, Slot;
        public bool Swimming, Grounded, Firing, InputTimedOut;
        public double InputExpiredAt;
        public uint AcknowledgedInput, SimulationTick, ConsumedJump, ConsumedFire, ShotSequence, RequiredPaintSequence;
        public uint FireBurstSequence, BurstShotIndex;
        public ulong ShotActionId => ((ulong)FireBurstSequence << 32) | BurstShotIndex;
        public double SimulatedAt, WeaponReadyAt, NextShotAt, InkRecoverAt, LastDamageAt, WallSeenAt, MantleStartedAt;
        public float VerticalSpeed, CurrentSpread;
        public Vector3 PlanarVelocity, WallNormal, WallPoint, MantleFrom, MantleTo;
        public int WallSurfaceId, WallRegionId;
        public MovementMode Movement;
        public WeaponPhase WeaponPhase;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref SwimSource); s.SerializeValue(ref CompactBody);
            s.SerializeValue(ref NextMuzzle); s.SerializeValue(ref LastShotMuzzle); s.SerializeValue(ref SwimWasHeld); s.SerializeValue(ref SemiHoldStarted);
            s.SerializeValue(ref RightShotAt); s.SerializeValue(ref LeftShotAt);
            s.SerializeValue(ref RightShotAction); s.SerializeValue(ref LeftShotAction);
            s.SerializeValue(ref HeroId); s.SerializeValue(ref HeroRevision); s.SerializeValue(ref ConsumedRelease);
            s.SerializeValue(ref BurstRemaining); s.SerializeValue(ref ChargeTicks); s.SerializeValue(ref AttackNeedsRelease); s.SerializeValue(ref ChargeReleasePending);
            s.SerializeValue(ref ChargeStartedAt); s.SerializeValue(ref BurstReadyAt); s.SerializeValue(ref FireVisualUntil); s.SerializeValue(ref LastShotCharge);
            s.SerializeValue(ref Position); s.SerializeValue(ref Yaw); s.SerializeValue(ref Pitch);
            s.SerializeValue(ref Health); s.SerializeValue(ref Ink); s.SerializeValue(ref RespawnsAt);
            s.SerializeValue(ref ProtectedUntil); s.SerializeValue(ref Revision); s.SerializeValue(ref Team);
            s.SerializeValue(ref Slot); s.SerializeValue(ref Swimming); s.SerializeValue(ref Velocity); s.SerializeValue(ref Grounded); s.SerializeValue(ref Firing);
            s.SerializeValue(ref InputTimedOut); s.SerializeValue(ref InputExpiredAt);
            s.SerializeValue(ref BodyYaw); s.SerializeValue(ref TurnStartYaw); s.SerializeValue(ref TurnDirection);
            s.SerializeValue(ref TurnStartedAt); s.SerializeValue(ref FireStartedAt); s.SerializeValue(ref DiedAt); s.SerializeValue(ref DeathDirection);
            s.SerializeValue(ref AcknowledgedInput); s.SerializeValue(ref SimulationTick); s.SerializeValue(ref ConsumedJump); s.SerializeValue(ref ConsumedFire); s.SerializeValue(ref ShotSequence);
            s.SerializeValue(ref RequiredPaintSequence);
            s.SerializeValue(ref FireBurstSequence); s.SerializeValue(ref BurstShotIndex);
            s.SerializeValue(ref SimulatedAt); s.SerializeValue(ref WeaponReadyAt); s.SerializeValue(ref NextShotAt); s.SerializeValue(ref InkRecoverAt); s.SerializeValue(ref LastDamageAt);
            s.SerializeValue(ref WallSeenAt); s.SerializeValue(ref MantleStartedAt); s.SerializeValue(ref VerticalSpeed); s.SerializeValue(ref CurrentSpread);
            s.SerializeValue(ref PlanarVelocity); s.SerializeValue(ref WallNormal); s.SerializeValue(ref WallPoint); s.SerializeValue(ref MantleFrom); s.SerializeValue(ref MantleTo);
            s.SerializeValue(ref WallSurfaceId); s.SerializeValue(ref WallRegionId); s.SerializeValue(ref Movement); s.SerializeValue(ref WeaponPhase);
        }
    }
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-100)]
    public sealed partial class PrototypePlayer : NetworkBehaviour { }
}
