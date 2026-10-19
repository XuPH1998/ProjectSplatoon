using System;
using System.Diagnostics;
using Splatoon.Blocks;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly struct ReplayDependencyRow
        {
            public readonly Vector3Int Chunk;
            public readonly uint Required, Applied;
            public readonly bool AppliedAvailable;
            internal ReplayDependencyRow(Vector3Int chunk, uint required, BlockTerrain terrain)
            {
                Chunk = chunk; Required = required;
                AppliedAvailable = terrain != null && terrain.Initialized && terrain.World != null;
                Applied = AppliedAvailable ? terrain.ChunkRevision(chunk) : 0;
            }
        }
        public readonly struct ReplayDependencyReadSet
        {
            public readonly ReplayDependencyRow[] Rows;
            public readonly SurfaceReplayDependencyRow[] SurfaceRows;
            public readonly int TotalCount;
            public readonly bool Available, Truncated;
            public readonly int TerrainObjectId;
            public readonly uint TerrainRound, WorldRevision;
            public readonly uint SurfaceRevision;
            public readonly bool WorldAvailable;
            public readonly bool SurfaceWorldAvailable;
            public readonly uint FixedReplayEpoch, FixedRequiredRevision, FixedAppliedRevision;
            public readonly bool FixedWorldAvailable;
            internal ReplayDependencyReadSet(PrototypePlayer player, BlockTerrain terrain)
            {
                // Value copies of the real replay gate dictionary, never motor read-set claims.
                TotalCount = player._terrainReplayDependencies.Count + player._surfaceReplayDependencies.Count; Available = true;
                FixedReplayEpoch = player._fixedReplayEpoch; FixedRequiredRevision = player._fixedReplayRevision;
                FixedWorldAvailable = terrain != null && terrain.FixedGridMode;
                FixedAppliedRevision = FixedWorldAvailable ? terrain.FixedBlocks.Revision : 0;
                if (FixedRequiredRevision != 0) TotalCount++;
                TerrainObjectId = terrain != null ? terrain.GetInstanceID() : 0;
                WorldAvailable = terrain != null && terrain.Initialized && terrain.World != null;
                TerrainRound = WorldAvailable ? terrain.Round : 0; WorldRevision = WorldAvailable ? terrain.Revision : 0;
                SurfaceWorldAvailable = terrain != null && terrain.Initialized && terrain.SurfacePaints != null;
                SurfaceRevision = SurfaceWorldAvailable ? terrain.SurfacePaints.Revision : 0;
                Truncated = TotalCount > 64; Rows = new ReplayDependencyRow[Math.Min(player._terrainReplayDependencies.Count, 64)];
                int index = 0;
                foreach (var pair in player._terrainReplayDependencies)
                { if (index == Rows.Length) break; Rows[index++] = new ReplayDependencyRow(pair.Key, pair.Value, terrain); }
                SurfaceRows = new SurfaceReplayDependencyRow[Math.Min(player._surfaceReplayDependencies.Count, 64 - Rows.Length)];
                index = 0;
                foreach (var pair in player._surfaceReplayDependencies)
                { if (index == SurfaceRows.Length) break; SurfaceRows[index++] = new SurfaceReplayDependencyRow(pair.Key, pair.Value, terrain); }
            }
        }
        public readonly struct SurfaceReplayDependencyRow
        {
            public readonly BlockSurfaceKey Key;
            public readonly uint Required, Applied;
            public readonly bool AppliedAvailable;
            internal SurfaceReplayDependencyRow(BlockSurfaceKey key, uint required, BlockTerrain terrain)
            {
                Key = key; Required = required; AppliedAvailable = false; Applied = 0;
                if (terrain != null && terrain.Initialized && terrain.SurfacePaints != null && terrain.SurfacePaints.TryGet(key, out var cell))
                { AppliedAvailable = true; Applied = cell.Revision; }
            }
        }
        public readonly struct ReplayDependencyObservation
        {
            public readonly long Invocation, BeginStamp, EndStamp;
            public readonly int Source;
            public readonly PlayerSnapshot State;
            public readonly ReplayDependencyReadSet Before, After;
            public readonly Vector3Int[] SatisfiedChunks;
            public readonly BlockSurfaceKey[] SatisfiedSurfaceCells;
            public readonly int SatisfiedCount;
            public readonly int SatisfiedSurfaceCount;
            public readonly uint PreviousLife, PreviousRound, TerrainRound;
            public readonly bool PreviousRoundKnown, TerrainAvailable, Boundary, Ready;
            public readonly bool SatisfactionCheckPerformed, SatisfiedTruncated;
            internal ReplayDependencyObservation(PrototypePlayer player, PlayerSnapshot state, bool boundary, bool ready)
            {
                Invocation = player._dependencyInvocation; BeginStamp = player._dependencyBeginStamp;
                EndStamp = Stopwatch.GetTimestamp(); Source = player._dependencySource; State = state;
                Before = player._dependencyBefore;
                SatisfiedChunks = player._dependencySatisfied; SatisfiedCount = player._dependencySatisfiedCount;
                SatisfiedSurfaceCells = player._dependencySurfaceSatisfied; SatisfiedSurfaceCount = player._dependencySurfaceSatisfiedCount;
                SatisfactionCheckPerformed = player._dependencySatisfactionPerformed;
                SatisfiedTruncated = SatisfiedCount + SatisfiedSurfaceCount > 64;
                var terrain = BlockTerrain.Current;
                After = new ReplayDependencyReadSet(player, terrain);
                PreviousLife = player._dependencyPreviousLife; PreviousRound = player._dependencyPreviousRound;
                PreviousRoundKnown = player._dependencyPreviousRoundKnown;
                TerrainAvailable = terrain != null && terrain.Initialized;
                TerrainRound = TerrainAvailable ? terrain.Round : 0; Boundary = boundary; Ready = ready;
            }
        }
        // Diagnostic consumers must only copy observations; no gameplay or terrain reentry.
        public static event Action<PrototypePlayer, ReplayDependencyObservation> ReplayDependencyObserved;
        public static int ReplayDependencyProviderErrors { get; private set; }
        long _dependencyInvocation, _dependencyBeginStamp;
        int _dependencySource;
        uint _dependencyPreviousLife, _dependencyPreviousRound;
        bool _dependencyPreviousRoundKnown, _dependencyRecording;
        ReplayDependencyReadSet _dependencyBefore;
        Vector3Int[] _dependencySatisfied;
        BlockSurfaceKey[] _dependencySurfaceSatisfied;
        int _dependencySatisfiedCount;
        int _dependencySurfaceSatisfiedCount;
        bool _dependencySatisfactionPerformed;
#endif
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void BeginReplayDependencyObservation(PlayerSnapshot state, int source)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _dependencyRecording = ReplayDependencyObserved != null;
            if (!_dependencyRecording) return;
            try
            {
                _dependencyInvocation++; _dependencyBeginStamp = Stopwatch.GetTimestamp(); _dependencySource = source;
                _dependencyPreviousLife = _terrainReplayLife; _dependencyPreviousRound = _terrainReplayRound;
                _dependencyPreviousRoundKnown = _terrainReplayRoundKnown;
                _dependencySatisfied = Array.Empty<Vector3Int>(); _dependencySatisfiedCount = 0;
                _dependencySurfaceSatisfied = Array.Empty<BlockSurfaceKey>(); _dependencySurfaceSatisfiedCount = 0;
                _dependencySatisfactionPerformed = false;
                _dependencyBefore = new ReplayDependencyReadSet(this, BlockTerrain.Current);
            }
            catch (Exception) { _dependencyRecording = false; ReplayDependencyProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveSatisfiedReplayDependencies()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_dependencyRecording) return;
            try
            {
                _dependencySatisfactionPerformed = true;
                _dependencySatisfiedCount = _publishedReplayDependencies.Count;
                _dependencySatisfied = new Vector3Int[Math.Min(64, _dependencySatisfiedCount)];
                for (int i = 0; i < _dependencySatisfied.Length; i++) _dependencySatisfied[i] = _publishedReplayDependencies[i];
                _dependencySurfaceSatisfiedCount = _publishedSurfaceReplayDependencies.Count;
                _dependencySurfaceSatisfied = new BlockSurfaceKey[Math.Min(64 - _dependencySatisfied.Length, _dependencySurfaceSatisfiedCount)];
                for (int i = 0; i < _dependencySurfaceSatisfied.Length; i++) _dependencySurfaceSatisfied[i] = _publishedSurfaceReplayDependencies[i];
            }
            catch (Exception) { _dependencyRecording = false; ReplayDependencyProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void EndReplayDependencyObservation(PlayerSnapshot state, bool boundary, bool ready)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_dependencyRecording) return;
            _dependencyRecording = false;
            try
            {
                var observation = new ReplayDependencyObservation(this, state, boundary, ready);
                var handlers = ReplayDependencyObserved;
                if (handlers == null) return;
                foreach (Action<PrototypePlayer, ReplayDependencyObservation> handler in handlers.GetInvocationList())
                    try { handler(this, observation); } catch (Exception) { ReplayDependencyProviderErrors++; }
            }
            catch (Exception) { ReplayDependencyProviderErrors++; }
#endif
        }
    }
}
