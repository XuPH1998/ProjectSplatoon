using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Blocks;
using UnityEngine;
using Unity.Profiling;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        bool _paintReplayPending, _syncReplayPending, _terrainReplayPending;
        readonly Dictionary<Vector3Int, uint> _terrainReplayDependencies = new();
        readonly List<Vector3Int> _publishedReplayDependencies = new();
        readonly Dictionary<BlockSurfaceKey, uint> _surfaceReplayDependencies = new();
        readonly List<BlockSurfaceKey> _publishedSurfaceReplayDependencies = new();
        uint _terrainReplayLife, _terrainReplayRound;
        uint _fixedReplayRevision, _fixedReplayEpoch;
        bool _terrainReplayRoundKnown, _terrainReplayBoundaryKnown, _terrainMovementBoundaryPending;
        public bool TerrainReplayPending => _terrainReplayPending;
        static readonly ProfilerMarker PredictionReplayMarker = new("Splatoon.Prediction.Reconcile");

        void ResetTerrainReplayState()
        {
            _terrainReplayPending = _terrainReplayRoundKnown = _terrainReplayBoundaryKnown = _terrainMovementBoundaryPending = false;
            _terrainReplayLife = _terrainReplayRound = 0;
            _fixedReplayRevision = _fixedReplayEpoch = 0;
            _terrainReplayDependencies.Clear(); _publishedReplayDependencies.Clear();
            _surfaceReplayDependencies.Clear(); _publishedSurfaceReplayDependencies.Clear();
        }

        static void KeepPublishedMovement(PlayerSnapshot published, ref PlayerSnapshot state)
        {
            state.Position = published.Position; state.Velocity = published.Velocity;
            state.PlanarVelocity = published.PlanarVelocity; state.VerticalSpeed = published.VerticalSpeed;
            state.Movement = published.Movement; state.Grounded = published.Grounded;
            state.Swimming = published.Swimming; state.CompactBody = published.CompactBody;
            state.FriendlyInkContact = published.FriendlyInkContact; state.SwimSource = published.SwimSource;
            state.AirSwimSource = published.AirSwimSource; state.SwimWasHeld = published.SwimWasHeld;
            state.SupportCell = published.SupportCell; state.SupportTerrainRevision = published.SupportTerrainRevision;
            state.FixedBlockRevision = published.FixedBlockRevision; state.FixedBlockEpoch = published.FixedBlockEpoch; state.FixedBlockRound = published.FixedBlockRound;
            state.SupportIsFixedGrid = published.SupportIsFixedGrid; state.SupportFixedId = published.SupportFixedId; state.SupportFixedGeneration = published.SupportFixedGeneration;
            state.SupportIsSurfacePaint = published.SupportIsSurfacePaint; state.SupportSurfaceFace = published.SupportSurfaceFace;
            state.SupportSurfaceU = published.SupportSurfaceU; state.SupportSurfaceV = published.SupportSurfaceV;
            state.GroundNormal = published.GroundNormal;
            state.WallNormal = published.WallNormal; state.WallPoint = published.WallPoint;
            state.WallSurfaceId = published.WallSurfaceId; state.WallRegionId = published.WallRegionId; state.WallSeenAt = published.WallSeenAt;
            state.MantleFrom = published.MantleFrom; state.MantleTo = published.MantleTo; state.MantleStartedAt = published.MantleStartedAt;
            state.AirHumanOffset = published.AirHumanOffset; state.CameraRebaseOffset = published.CameraRebaseOffset;
            state.PaperPose = published.PaperPose; state.PaperCenter = published.PaperCenter; state.PaperRotation = published.PaperRotation;
            state.PaperChangedAt = published.PaperChangedAt; state.PaperAnimationTime = published.PaperAnimationTime; state.PaperMove = published.PaperMove;
        }

        static bool TerrainReadyFor(PlayerSnapshot state)
        {
            var fixedTerrain = BlockTerrain.Current;
            if (state.FixedBlockEpoch != 0)
            {
                var world = fixedTerrain != null && fixedTerrain.FixedGridMode ? fixedTerrain.FixedBlocks : null;
                if (world == null || world.Epoch != state.FixedBlockEpoch || world.Round != state.FixedBlockRound || world.Revision < state.FixedBlockRevision) return false;
                if (!state.SupportIsFixedGrid || world.Revision > state.FixedBlockRevision) return true;
                var handle = new BlockFixedGridHandle { Epoch = state.FixedBlockEpoch, Round = state.FixedBlockRound,
                    Id = state.SupportFixedId, Generation = state.SupportFixedGeneration };
                return world.TryGet(handle, out var support) && support.Cell == state.SupportCell && support.Revision == state.SupportTerrainRevision;
            }
            if (state.SupportTerrainRevision == 0 || state.Health <= 0 || !state.CanMove) return true;
            var terrain = BlockTerrain.Current;
            if (terrain == null || !terrain.Initialized) return false;
            if (state.SupportIsSurfacePaint)
                return SurfaceReadyFor(terrain, new BlockSurfaceKey(state.SupportSurfaceFace, state.SupportSurfaceU, state.SupportSurfaceV),
                    state.SupportTerrainRevision);
            return terrain.ChunkRevision(BlockWorld.ChunkAt(state.SupportCell)) >= state.SupportTerrainRevision;
        }

        static bool SurfaceReadyFor(BlockTerrain terrain, BlockSurfaceKey key, uint revision)
            => terrain.SurfacePaints != null && terrain.SurfacePaints.TryGet(key, out var cell) && cell.Revision >= revision;

        bool TerrainReadyForReplay(PlayerSnapshot state, int observationSource = 0)
        {
            BeginReplayDependencyObservation(state, observationSource);
            var terrain = BlockTerrain.Current;
            bool terrainAvailable = terrain != null && terrain.Initialized;
            bool boundary = _terrainReplayBoundaryKnown && state.Revision != _terrainReplayLife ||
                terrainAvailable && _terrainReplayRoundKnown && terrain.Round != _terrainReplayRound;
            // Refresh or PredictInput can observe a new round before reconciliation.
            // Keep that boundary until its movement frame is actually installed.
            if (boundary) _terrainMovementBoundaryPending = true;
            _terrainReplayBoundaryKnown = true;
            if (boundary || state.Health <= 0 || !state.CanMove || state.InputTimedOut)
            {
                _terrainReplayDependencies.Clear(); _surfaceReplayDependencies.Clear();
                _publishedReplayDependencies.Clear(); _publishedSurfaceReplayDependencies.Clear();
            }
            // Death and input timeout end legacy support replay, but Step still
            // performs volume queries in those states. Their explicit fixed-world
            // dependency must survive until that completed commit arrives.
            if (boundary) _fixedReplayRevision = _fixedReplayEpoch = 0;
            _terrainReplayLife = state.Revision;
            if (terrainAvailable)
            { _terrainReplayRound = terrain.Round; _terrainReplayRoundKnown = true; }
            if (state.FixedBlockEpoch != 0 || _fixedReplayEpoch != 0 || terrainAvailable && terrain.FixedGridMode)
            {
                var world = terrainAvailable && terrain.FixedGridMode ? terrain.FixedBlocks : null;
                if (world == null)
                {
                    if (state.FixedBlockEpoch != 0 && (_fixedReplayEpoch == 0 || _fixedReplayEpoch == state.FixedBlockEpoch))
                    {
                        _fixedReplayEpoch = state.FixedBlockEpoch;
                        _fixedReplayRevision = System.Math.Max(_fixedReplayRevision, state.FixedBlockRevision);
                    }
                    EndReplayDependencyObservation(state, boundary, false);
                    return false;
                }
                // Fixed occupancy and paint arrive as one completed commit. A newer
                // commit can legitimately remove/move an old support: wait on the global
                // watermark instead of waiting forever for that old occupied cell.
                if (_fixedReplayEpoch != world.Epoch)
                { _fixedReplayEpoch = world.Epoch; _fixedReplayRevision = 0; }
                if (state.FixedBlockEpoch == _fixedReplayEpoch)
                    _fixedReplayRevision = System.Math.Max(_fixedReplayRevision, state.FixedBlockRevision);
                var match = PrototypeMatch.Current;
                bool fixedReady = (match == null || match.IsServer || match.InitialSyncComplete && match.FixedGridBlockSyncReady) &&
                    world.Revision >= _fixedReplayRevision && TerrainReadyFor(state);
                EndReplayDependencyObservation(state, boundary, fixedReady);
                return fixedReady;
            }

            if (state.Health <= 0 || !state.CanMove || state.InputTimedOut)
            { EndReplayDependencyObservation(state, boundary, true); return true; }

            bool checkedDependencies = terrainAvailable && (_terrainReplayDependencies.Count != 0 || _surfaceReplayDependencies.Count != 0);
            _publishedReplayDependencies.Clear(); _publishedSurfaceReplayDependencies.Clear();
            if (terrainAvailable && _terrainReplayDependencies.Count != 0)
            {
                foreach (var dependency in _terrainReplayDependencies)
                    if (terrain.ChunkRevision(dependency.Key) >= dependency.Value)
                        _publishedReplayDependencies.Add(dependency.Key);
                foreach (var chunk in _publishedReplayDependencies) _terrainReplayDependencies.Remove(chunk);
            }
            if (terrainAvailable && _surfaceReplayDependencies.Count != 0)
            {
                foreach (var dependency in _surfaceReplayDependencies)
                    if (SurfaceReadyFor(terrain, dependency.Key, dependency.Value))
                        _publishedSurfaceReplayDependencies.Add(dependency.Key);
                foreach (var key in _publishedSurfaceReplayDependencies) _surfaceReplayDependencies.Remove(key);
            }
            if (checkedDependencies) ObserveSatisfiedReplayDependencies();
            if (!TerrainReadyFor(state))
            {
                if (state.SupportIsSurfacePaint)
                {
                    var key = new BlockSurfaceKey(state.SupportSurfaceFace, state.SupportSurfaceU, state.SupportSurfaceV);
                    _surfaceReplayDependencies.TryGetValue(key, out uint required);
                    if (state.SupportTerrainRevision > required) _surfaceReplayDependencies[key] = state.SupportTerrainRevision;
                }
                else
                {
                    var chunk = BlockWorld.ChunkAt(state.SupportCell);
                    _terrainReplayDependencies.TryGetValue(chunk, out uint required);
                    if (state.SupportTerrainRevision > required) _terrainReplayDependencies[chunk] = state.SupportTerrainRevision;
                }
            }
            // An airborne snapshot has no current support. It does not prove that a
            // previously required collision chunk has arrived for historical replay.
            bool ready = _terrainReplayDependencies.Count == 0 && _surfaceReplayDependencies.Count == 0;
            EndReplayDependencyObservation(state, boundary, ready);
            return ready;
        }

        void RefreshPredictionPaint(PlayerSnapshot authority, bool initialSyncComplete, uint appliedSequence, MatchPhase phase)
        {
            if (!initialSyncComplete) { _syncReplayPending = true; return; }
            if (BlockTerrain.Current != null || authority.SupportTerrainRevision != 0)
            {
                if (!TerrainReadyForReplay(authority, 1))
                {
                    if (!_terrainReplayPending || _terrainMovementBoundaryPending)
                    {
                        SetReconcileTelemetryOrigin(ReconcileOrigin.TerrainWait);
                        ReconcilePrediction(authority, false, true, appliedSequence, phase);
                    }
                    return;
                }
                if (!_syncReplayPending && !_terrainReplayPending && !_terrainMovementBoundaryPending) return;
                RecordPaintReplay();
                SetReconcileTelemetryOrigin(_syncReplayPending ? ReconcileOrigin.InitialSyncResume : ReconcileOrigin.TerrainResume);
                ReconcilePrediction(authority, false, true, appliedSequence, phase);
                return;
            }
            if (!_syncReplayPending && !_terrainMovementBoundaryPending &&
                (!_paintReplayPending || appliedSequence < authority.RequiredPaintSequence)) return;
            RecordPaintReplay();
            SetReconcileTelemetryOrigin(_syncReplayPending ? ReconcileOrigin.InitialSyncResume :
                _terrainMovementBoundaryPending ? ReconcileOrigin.Snapshot : ReconcileOrigin.PaintResume);
            ReconcilePrediction(authority, false, true, appliedSequence, phase);
        }

        void ReconcilePrediction(PlayerSnapshot authority, bool movementChanged, bool initialSyncComplete, uint appliedSequence, MatchPhase phase)
        {
            using var sample = PredictionReplayMarker.Auto();
            BeginReconcileTelemetry(authority, movementChanged, initialSyncComplete);
            var oldPosition = _predicted.Position;
            bool lifecycle = authority.Revision != _predicted.Revision;
            RecordInputAcknowledgement(authority);
            _history.RemoveAll(f => f.Sequence <= authority.AcknowledgedInput || f.Revision != authority.Revision);
            _predictionPaused = authority.InputTimedOut;
            if (_predictionPaused || lifecycle) { _history.Clear(); DiscardInputTiming(); }
            _syncReplayPending = !initialSyncComplete;
            _terrainReplayPending = !TerrainReadyForReplay(authority, 2);
            if (_terrainMovementBoundaryPending)
            { _history.Clear(); DiscardInputTiming(); }
            // Keep the last published movement frame while its replacement collision
            // dependency is missing. ACK processing and telemetry still happen for
            // every snapshot; Refresh applies the latest authority when it can replay.
            bool deferMovement = _terrainReplayPending && initialSyncComplete && !_predictionPaused && !lifecycle && !_terrainMovementBoundaryPending &&
                authority.IsAlive && authority.HeroId == _predicted.HeroId && authority.HeroRevision == _predicted.HeroRevision &&
                authority.Team == _predicted.Team && authority.Slot == _predicted.Slot && authority.LifeState == _predicted.LifeState &&
                authority.SpecialPhase == _predicted.SpecialPhase && authority.Movement != MovementMode.Reefslider && _predicted.Movement != MovementMode.Reefslider;
            var publishedMovement = _predicted;
            _predicted = authority;
            if (deferMovement) KeepPublishedMovement(publishedMovement, ref _predicted);
            else
            {
                // The movement capsule is needed for replay; historical paper hit meshes are not.
                _motor.Restore(authority, false);
                _terrainMovementBoundaryPending = false;
            }
            _paintReplayPending = BlockTerrain.Current == null && authority.Health > 0 && (authority.Swimming || authority.SwimWasHeld || authority.CompactBody) &&
                appliedSequence < authority.RequiredPaintSequence;
            int replayed = 0;
            if (lifecycle)
            { _visualOffset = Vector3.zero; _look = new Vector2(authority.Yaw, authority.Pitch); }
            else if (initialSyncComplete && !_predictionPaused && !_terrainReplayPending && authority.CanMove)
            {
                // Already-applied authoritative ink is a prediction input. A global paint watermark
                // may be ahead because somebody else is shooting; it must not freeze this owner.
                foreach (var frame in _history)
                {
                    Step(ref _predicted, frame, 1f / GameplayConfig.Global.SimulationRate,
                        _predicted.SimulatedAt + 1.0 / GameplayConfig.Global.SimulationRate, phase);
                    replayed++;
                }
            }
            LastCorrectionDistance = lifecycle ? 0 : Vector3.Distance(oldPosition, _predicted.Position);
            if (LastCorrectionDistance > .01f) CorrectionCount++;
            bool hard = lifecycle || authority.IsDead || LastCorrectionDistance > .75f || movementChanged || _terrainReplayPending;
            _visualOffset = hard ? Vector3.zero : Vector3.ClampMagnitude(_visualOffset + oldPosition - _predicted.Position, .5f);
            RecordPredictionReplay(replayed, hard, initialSyncComplete && (_paintReplayPending || _terrainReplayPending));
            EndReconcileTelemetry(replayed, hard, appliedSequence, phase);
        }

        bool PredictInput(PlayerInputFrame frame, bool initialSyncComplete, MatchPhase phase)
        {
            if (!initialSyncComplete || _predictionPaused || _terrainReplayPending) return false;
            if (!TerrainReadyForReplay(_predicted, 3))
            { _terrainReplayPending = true; BeginTerrainWaitTelemetry(); return false; }
            if (_terrainMovementBoundaryPending) return false;
            RecordPredictionStep();
            uint subAction = _predicted.SubAction, specialAttack = _predicted.SpecialAttack;
            bool shot = Step(ref _predicted, frame, 1f / GameplayConfig.Global.SimulationRate,
                _predicted.SimulatedAt + 1.0 / GameplayConfig.Global.SimulationRate, phase);
            if (_predicted.SubAction != subAction) PrototypeMatch.Current?.SubPresentation?.PredictThrow(this, _predicted, _lastSubLaunch);
            if (_predicted.SpecialAttack != specialAttack) PrototypeMatch.Current?.SpecialPresentation?.Predict(this, _predicted);
            return shot;
        }
    }
}
