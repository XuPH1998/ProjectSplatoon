using System;
using System.Diagnostics;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public enum ReconcileOrigin : byte { Snapshot, TerrainWait, TerrainResume, InitialSyncResume, PaintResume }

    public sealed partial class PrototypePlayer
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>Value copies taken inside reconciliation, before another snapshot can overwrite its result.</summary>
        public readonly struct ReconcileTelemetryState
        {
            public readonly Vector3 Position, HumanPosition, Velocity, PlanarVelocity, GroundNormal, PaperCenter;
            public readonly Quaternion PaperRotation;
            public readonly Vector3Int SupportCell;
            public readonly uint Life, AcknowledgedInput, HeroRevision, RequiredSupportRevision, AppliedSupportRevision;
            public readonly int HeroId;
            public readonly byte Team, SupportOwner;
            public readonly float Health, AirHumanOffset, CameraRebaseOffset, VerticalSpeed;
            public readonly double SimulatedAt;
            public readonly MovementMode Movement;
            public readonly PaperPose PaperPose;
            public readonly PlayerLifeState LifeState;
            public readonly bool Grounded, Swimming, CompactBody, InputTimedOut, CanMove, SupportPresent, TerrainAvailable;

            public ReconcileTelemetryState(PlayerSnapshot state, BlockTerrain terrain)
            {
                Position = state.Position; HumanPosition = PlayerMotorSimulation.HumanPosition(state);
                Velocity = state.Velocity; PlanarVelocity = state.PlanarVelocity; VerticalSpeed = state.VerticalSpeed;
                GroundNormal = state.GroundNormal; PaperCenter = state.PaperCenter; PaperRotation = state.PaperRotation;
                SupportCell = state.SupportCell; Life = state.Revision; AcknowledgedInput = state.AcknowledgedInput;
                HeroId = state.HeroId; HeroRevision = state.HeroRevision; Team = state.Team; Health = state.Health;
                AirHumanOffset = state.AirHumanOffset; CameraRebaseOffset = state.CameraRebaseOffset;
                SimulatedAt = state.SimulatedAt; Movement = state.Movement; PaperPose = state.PaperPose; LifeState = state.LifeState;
                Grounded = state.Grounded; Swimming = state.Swimming; CompactBody = state.CompactBody;
                InputTimedOut = state.InputTimedOut; CanMove = state.CanMove;
                RequiredSupportRevision = state.SupportTerrainRevision;
                TerrainAvailable = terrain != null && terrain.Initialized && terrain.World != null;
                AppliedSupportRevision = TerrainAvailable ? terrain.ChunkRevision(BlockWorld.ChunkAt(state.SupportCell)) : 0;
                BlockCell support = default;
                SupportPresent = TerrainAvailable && terrain.World.TryGet(state.SupportCell, out support);
                SupportOwner = SupportPresent ? support.Owner : (byte)0;
            }
        }

        /// <summary>Opt-in observation only. The raw correction keeps the existing lifecycle and distance semantics.</summary>
        public readonly struct ReconcileTelemetryEvent
        {
            public readonly ReconcileTelemetryState Before, Authority, Replayed;
            public readonly ReconcileOrigin Origin;
            public readonly long DependencyCheckInvocation, ReconcileInvocation, WallStamp;
            public readonly uint TerrainRound, TerrainRevision, AppliedPaintSequence;
            public readonly MatchPhase Phase;
            public readonly double Realtime, StartedRealtime, ServerTime, TerrainTime, TerrainWaitSeconds, OldestPendingSeconds;
            public readonly float RawDistance, HumanDistance, UnadjustedRawDistance, UnadjustedHumanDistance;
            public readonly int ReplaySteps, InputHistoryBefore, InputHistoryAfter, TerrainPendingTransactions, UnstableColumns;
            public readonly int SafetyRecoveries, SafetyRecoveryFailures;
            public readonly bool Lifecycle, MovementChanged, Hard, InitialSyncComplete, NetworkTimeAvailable, TerrainAvailable;
            public readonly bool TerrainPendingBefore, TerrainPendingAfter, SyncPendingBefore, SyncPendingAfter;
            public readonly bool PaintPendingBefore, PaintPendingAfter, TerrainWaitDurationKnown;

            internal ReconcileTelemetryEvent(PrototypePlayer player, int steps, bool hard, uint appliedPaintSequence, MatchPhase phase)
            {
                var terrain = BlockTerrain.Current;
                Before = player._telemetryBefore; Authority = player._telemetryAuthority;
                Replayed = new ReconcileTelemetryState(player._predicted, terrain);
                Origin = player._telemetryOrigin; Realtime = Time.realtimeSinceStartupAsDouble;
                DependencyCheckInvocation = player._dependencyInvocation; WallStamp = Stopwatch.GetTimestamp();
                ReconcileInvocation = player._terrainReconcileInvocation;
                StartedRealtime = player._telemetryStartedRealtime;
                NetworkTimeAvailable = player.NetworkManager != null;
                ServerTime = NetworkTimeAvailable ? player.NetworkManager.ServerTime.Time : -1;
                TerrainAvailable = terrain != null && terrain.Initialized && terrain.World != null;
                TerrainRound = TerrainAvailable ? terrain.Round : 0; TerrainRevision = TerrainAvailable ? terrain.Revision : 0;
                TerrainTime = TerrainAvailable ? terrain.SimulationTime : -1;
                TerrainPendingTransactions = TerrainAvailable ? terrain.PendingTransactions : -1;
                OldestPendingSeconds = TerrainAvailable ? terrain.OldestPendingSeconds : -1;
                UnstableColumns = TerrainAvailable ? terrain.UnstableColumns : -1;
                // These are local authority counters. Client zeroes do not describe server recovery.
                SafetyRecoveries = TerrainAvailable ? terrain.SafetyRecoveries : -1;
                SafetyRecoveryFailures = TerrainAvailable ? terrain.SafetyRecoveryFailures : -1;
                Lifecycle = Before.Life != Authority.Life; MovementChanged = player._telemetryMovementChanged;
                Hard = hard; InitialSyncComplete = player._telemetryInitialSyncComplete;
                RawDistance = player.LastCorrectionDistance;
                UnadjustedRawDistance = Vector3.Distance(Before.Position, Replayed.Position);
                UnadjustedHumanDistance = Vector3.Distance(Before.HumanPosition, Replayed.HumanPosition);
                HumanDistance = Lifecycle ? 0 : UnadjustedHumanDistance;
                ReplaySteps = steps; InputHistoryBefore = player._telemetryInputHistoryBefore; InputHistoryAfter = player._history.Count;
                AppliedPaintSequence = appliedPaintSequence; Phase = phase;
                TerrainPendingBefore = player._telemetryTerrainPendingBefore; TerrainPendingAfter = player._terrainReplayPending;
                SyncPendingBefore = player._telemetrySyncPendingBefore; SyncPendingAfter = player._syncReplayPending;
                PaintPendingBefore = player._telemetryPaintPendingBefore; PaintPendingAfter = player._paintReplayPending;
                if (TerrainPendingAfter && !TerrainPendingBefore) player._telemetryTerrainWaitStarted = StartedRealtime;
                TerrainWaitDurationKnown = player._telemetryTerrainWaitStarted >= 0;
                TerrainWaitSeconds = TerrainWaitDurationKnown ? Math.Max(0, Realtime - player._telemetryTerrainWaitStarted) : -1;
                if (!TerrainPendingAfter) player._telemetryTerrainWaitStarted = -1;
            }
        }

        // The persistent opt-in Probe subscribes before connecting. Synchronous delivery cannot overwrite
        // same-ACK recovery or intermediate reconciliations, and needs no bounded telemetry ring.
        public static event Action<PrototypePlayer, ReconcileTelemetryEvent> ReconcileObserved;
        ReconcileTelemetryState _telemetryBefore, _telemetryAuthority;
        ReconcileOrigin _telemetryNextOrigin, _telemetryOrigin;
        double _telemetryStartedRealtime, _telemetryTerrainWaitStarted = -1;
        int _telemetryInputHistoryBefore;
        long _terrainReconcileInvocation;
        bool _telemetryRecording, _telemetryMovementChanged, _telemetryInitialSyncComplete;
        bool _telemetryTerrainPendingBefore, _telemetrySyncPendingBefore, _telemetryPaintPendingBefore;
#endif

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void SetReconcileTelemetryOrigin(ReconcileOrigin origin)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _telemetryNextOrigin = origin;
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void BeginTerrainWaitTelemetry()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (ReconcileObserved != null && _telemetryTerrainWaitStarted < 0)
                _telemetryTerrainWaitStarted = Time.realtimeSinceStartupAsDouble;
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void BeginReconcileTelemetry(PlayerSnapshot authority, bool movementChanged, bool initialSyncComplete)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _telemetryOrigin = _telemetryNextOrigin; _telemetryNextOrigin = ReconcileOrigin.Snapshot;
            _telemetryRecording = ReconcileObserved != null;
            if (!_telemetryRecording) { _telemetryTerrainWaitStarted = -1; return; }
            _terrainReconcileInvocation++;
            var terrain = BlockTerrain.Current;
            _telemetryBefore = new ReconcileTelemetryState(_predicted, terrain);
            _telemetryAuthority = new ReconcileTelemetryState(authority, terrain);
            _telemetryStartedRealtime = Time.realtimeSinceStartupAsDouble;
            _telemetryInputHistoryBefore = _history.Count;
            _telemetryMovementChanged = movementChanged; _telemetryInitialSyncComplete = initialSyncComplete;
            _telemetryTerrainPendingBefore = _terrainReplayPending;
            _telemetrySyncPendingBefore = _syncReplayPending; _telemetryPaintPendingBefore = _paintReplayPending;
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void EndReconcileTelemetry(int steps, bool hard, uint appliedPaintSequence, MatchPhase phase)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_telemetryRecording) return;
            var observation = new ReconcileTelemetryEvent(this, steps, hard, appliedPaintSequence, phase);
            _telemetryRecording = false;
            ReconcileObserved?.Invoke(this, observation);
#endif
        }
    }
}
