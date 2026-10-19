using System;
using System.Diagnostics;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Public fields allow JsonUtility to copy the evidence. These are detached value
        // snapshots; inspecting them never advances or refreshes the prediction gate.
        [Serializable]
        public struct FixedQueryWorldVersion
        {
            public bool Available;
            public int TerrainObjectId;
            public uint Epoch, Round, Revision, SurfaceRevision;
        }

        [Serializable]
        public struct FixedQueryActorDependency
        {
            public uint Life, HeroRevision, SimulationTick, Epoch, Round, RequiredRevision;
            public bool RequiresDependency, SupportIsFixedGrid, SupportReady, Ready;
            public bool IsAlive, CanMove, InputTimedOut;
            public ulong SupportId;
            public uint SupportGeneration, SupportRevision;
            public Vector3Int SupportCell;
        }

        [Serializable]
        public struct FixedQueryReplayGate
        {
            public uint Epoch, Round, RequiredRevision;
            public bool RoundKnown, InitialSyncComplete, FixedSyncReady, IsAuthority;
            public bool PredictionPaused, SyncReplayPending, TerrainReplayPending, MovementBoundaryPending, Ready;
        }

        [Serializable]
        public struct FixedQueryExecution
        {
            public long Token, BeginStamp, EndStamp;
            public int Frame;
            public FixedQueryActorDependency Source;
            public FixedQueryReplayGate Gate;
            public FixedQueryWorldVersion WorldBegin, WorldEnd;
            public bool Completed, SameWorldObject, SameWorld, Valid;
        }

        [Serializable]
        public struct FixedQueryCounters
        {
            public long StartedSteps, CompletedSteps, InvalidQuerySteps, DifferentWorldSteps, AbortedSteps;

            public void Begin() => StartedSteps++;
            public void Finish(FixedQueryExecution execution)
            {
                if (execution.Completed) CompletedSteps++; else AbortedSteps++;
                if (!execution.SameWorld) DifferentWorldSteps++;
                if (!execution.Valid) InvalidQuerySteps++;
            }
        }

        [Serializable]
        public struct FixedQueryDependencySnapshot
        {
            public FixedQueryActorDependency Authority, Presented;
            public FixedQueryReplayGate Gate;
            public FixedQueryWorldVersion World;
            public FixedQueryExecution Last;
            public long StartedSteps, CompletedSteps, InvalidQuerySteps, DifferentWorldSteps, AbortedSteps, TokenErrors;
        }

        FixedQueryCounters _fixedQueryCounters;
        FixedQueryExecution _fixedQueryActive, _fixedQueryLast;
        BlockFixedGridWorld _fixedQueryWorld;
        long _fixedQueryNextToken, _fixedQueryTokenErrors;
        static FixedQueryCounters _allFixedQueries;
        static long _allFixedQueryTokenErrors;
        public static long AllFixedQuerySteps => _allFixedQueries.StartedSteps;
        public static long AllInvalidFixedQuerySteps => _allFixedQueries.InvalidQuerySteps;
        public static long AllFixedQueryTokenErrors => _allFixedQueryTokenErrors;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetAllFixedQueryEvidence()
        { _allFixedQueries = default; _allFixedQueryTokenErrors = 0; }

        // This predicate deliberately uses actor and retained replay dependencies, never
        // the match's advertised revision, which may describe an in-flight later commit.
        public static bool FixedQueryActorReady(FixedQueryActorDependency actor, FixedQueryWorldVersion world)
        {
            // Match TerrainReadyFor: an explicit fixed-world dependency is checked
            // before its legacy dead/unmovable exemptions.
            if (actor.Epoch == 0) return !actor.RequiresDependency || actor.SupportReady;
            if (!world.Available || world.Epoch != actor.Epoch || world.Round != actor.Round || world.Revision < actor.RequiredRevision) return false;
            return !actor.SupportIsFixedGrid || world.Revision > actor.RequiredRevision || actor.SupportReady;
        }

        public static bool FixedQueryGateReady(FixedQueryReplayGate gate, FixedQueryWorldVersion world)
        {
            if (gate.IsAuthority) return true;
            if (!gate.InitialSyncComplete || !gate.FixedSyncReady || gate.PredictionPaused || gate.SyncReplayPending ||
                gate.TerrainReplayPending || gate.MovementBoundaryPending) return false;
            if (gate.Epoch == 0) return true;
            return world.Available && world.Epoch == gate.Epoch && (!gate.RoundKnown || world.Round == gate.Round) &&
                world.Revision >= gate.RequiredRevision;
        }

        public static bool SameFixedQueryWorld(FixedQueryWorldVersion before, FixedQueryWorldVersion after)
            => before.Available && after.Available && before.TerrainObjectId == after.TerrainObjectId &&
                before.Epoch == after.Epoch && before.Round == after.Round && before.Revision == after.Revision &&
                before.SurfaceRevision == after.SurfaceRevision;

        public static FixedQueryExecution ValidateFixedQueryExecution(FixedQueryExecution execution)
        {
            execution.Source.Ready = FixedQueryActorReady(execution.Source, execution.WorldBegin);
            execution.Gate.Ready = FixedQueryGateReady(execution.Gate, execution.WorldBegin);
            execution.SameWorld = execution.SameWorldObject && SameFixedQueryWorld(execution.WorldBegin, execution.WorldEnd);
            execution.Valid = execution.Token > 0 && execution.Completed && execution.Source.Ready && execution.Gate.Ready && execution.SameWorld;
            return execution;
        }

        static FixedQueryWorldVersion ReadFixedQueryWorld(BlockTerrain terrain)
        {
            var world = terrain != null && terrain.Initialized && terrain.FixedGridMode ? terrain.FixedBlocks : null;
            return new FixedQueryWorldVersion
            {
                Available = world != null, TerrainObjectId = terrain != null ? terrain.GetInstanceID() : 0,
                Epoch = world != null ? world.Epoch : 0, Round = world != null ? world.Round : 0,
                Revision = world != null ? world.Revision : 0,
                SurfaceRevision = world != null && world.Surfaces != null ? world.Surfaces.Revision : 0
            };
        }

        static FixedQueryActorDependency ReadFixedQueryActor(PlayerSnapshot state, BlockFixedGridWorld world, FixedQueryWorldVersion version)
        {
            var actor = new FixedQueryActorDependency
            {
                Life = state.Revision, HeroRevision = state.HeroRevision, SimulationTick = state.SimulationTick,
                Epoch = state.FixedBlockEpoch, Round = state.FixedBlockRound, RequiredRevision = state.FixedBlockRevision,
                RequiresDependency = state.FixedBlockEpoch != 0 || state.SupportTerrainRevision != 0 && state.Health > 0 && state.CanMove,
                IsAlive = state.IsAlive, CanMove = state.CanMove, InputTimedOut = state.InputTimedOut,
                SupportIsFixedGrid = state.SupportIsFixedGrid, SupportId = state.SupportFixedId,
                SupportGeneration = state.SupportFixedGeneration, SupportRevision = state.SupportTerrainRevision, SupportCell = state.SupportCell
            };
            if (actor.Epoch == 0) actor.SupportReady = TerrainReadyFor(state);
            else if (actor.SupportIsFixedGrid && world != null)
            {
                var handle = new BlockFixedGridHandle { Epoch = actor.Epoch, Round = actor.Round, Id = actor.SupportId, Generation = actor.SupportGeneration };
                actor.SupportReady = world.TryGet(handle, out var support) && support.Cell == actor.SupportCell && support.Revision == actor.SupportRevision;
            }
            actor.Ready = FixedQueryActorReady(actor, version);
            return actor;
        }

        FixedQueryReplayGate ReadFixedQueryGate(PlayerSnapshot source, FixedQueryWorldVersion version)
        {
            var match = PrototypeMatch.Current;
            var gate = new FixedQueryReplayGate
            {
                IsAuthority = IsServer, Epoch = IsServer ? source.FixedBlockEpoch : _fixedReplayEpoch,
                Round = IsServer ? source.FixedBlockRound : _terrainReplayRound,
                RequiredRevision = IsServer ? source.FixedBlockRevision : _fixedReplayRevision,
                RoundKnown = IsServer ? source.FixedBlockEpoch != 0 : _terrainReplayRoundKnown,
                InitialSyncComplete = IsServer || match != null && match.InitialSyncComplete,
                FixedSyncReady = IsServer || match != null && match.FixedGridBlockSyncReady,
                PredictionPaused = _predictionPaused, SyncReplayPending = _syncReplayPending,
                TerrainReplayPending = _terrainReplayPending, MovementBoundaryPending = _terrainMovementBoundaryPending
            };
            gate.Ready = FixedQueryGateReady(gate, version);
            return gate;
        }

        public FixedQueryDependencySnapshot InspectFixedQueryDependency()
        {
            var terrain = BlockTerrain.Current;
            var version = ReadFixedQueryWorld(terrain);
            var world = version.Available ? terrain.FixedBlocks : null;
            var authority = Snapshot.Value;
            return new FixedQueryDependencySnapshot
            {
                Authority = ReadFixedQueryActor(authority, world, version),
                Presented = ReadFixedQueryActor(PresentedState, world, version),
                Gate = ReadFixedQueryGate(authority, version), World = version, Last = _fixedQueryLast,
                StartedSteps = _fixedQueryCounters.StartedSteps, CompletedSteps = _fixedQueryCounters.CompletedSteps,
                InvalidQuerySteps = _fixedQueryCounters.InvalidQuerySteps, DifferentWorldSteps = _fixedQueryCounters.DifferentWorldSteps,
                AbortedSteps = _fixedQueryCounters.AbortedSteps, TokenErrors = _fixedQueryTokenErrors
            };
        }

        long BeginFixedQueryEvidence(PlayerSnapshot source)
        {
            var terrain = BlockTerrain.Current;
            var version = ReadFixedQueryWorld(terrain);
            if (!version.Available) return 0;
            if (_fixedQueryActive.Token != 0)
            {
                _fixedQueryTokenErrors++; _allFixedQueryTokenErrors++;
                EndFixedQueryEvidence(_fixedQueryActive.Token, false);
            }
            _fixedQueryWorld = terrain.FixedBlocks;
            _fixedQueryActive = new FixedQueryExecution
            {
                Token = ++_fixedQueryNextToken, BeginStamp = Stopwatch.GetTimestamp(), Frame = Time.frameCount,
                Source = ReadFixedQueryActor(source, _fixedQueryWorld, version), Gate = ReadFixedQueryGate(source, version), WorldBegin = version
            };
            _fixedQueryCounters.Begin(); _allFixedQueries.Begin();
            return _fixedQueryActive.Token;
        }

        void EndFixedQueryEvidence(long token, bool completed)
        {
            if (token == 0) return;
            // Reefslider closes its query interval before server combat callbacks. Its
            // enclosing finally may then close the same token, which is already done.
            if (_fixedQueryActive.Token == 0 && token == _fixedQueryLast.Token) return;
            if (token != _fixedQueryActive.Token) { _fixedQueryTokenErrors++; _allFixedQueryTokenErrors++; return; }
            var terrain = BlockTerrain.Current;
            var execution = _fixedQueryActive;
            execution.EndStamp = Stopwatch.GetTimestamp(); execution.Completed = completed;
            execution.WorldEnd = ReadFixedQueryWorld(terrain);
            execution.SameWorldObject = execution.WorldEnd.Available && ReferenceEquals(_fixedQueryWorld, terrain.FixedBlocks);
            execution = ValidateFixedQueryExecution(execution);
            _fixedQueryCounters.Finish(execution); _allFixedQueries.Finish(execution);
            _fixedQueryLast = execution; _fixedQueryActive = default; _fixedQueryWorld = null;
        }

        void EndFixedQueryEvidenceBeforeCommit()
        {
            if (_fixedQueryActive.Token != 0) EndFixedQueryEvidence(_fixedQueryActive.Token, true);
        }

        void ResetFixedQueryEvidence()
        {
            if (_fixedQueryActive.Token != 0) EndFixedQueryEvidence(_fixedQueryActive.Token, false);
            _fixedQueryCounters = default; _fixedQueryActive = _fixedQueryLast = default;
            _fixedQueryWorld = null; _fixedQueryNextToken = _fixedQueryTokenErrors = 0;
        }
#endif
        bool QueryCanStand(PlayerSnapshot source, Vector3 position)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long token = BeginFixedQueryEvidence(source);
            bool completed = false;
            try
            {
                bool result = _motor.CanStand(position); completed = true; return result;
            }
            finally { EndFixedQueryEvidence(token, completed); }
#else
            return _motor.CanStand(position);
#endif
        }
    }
}
