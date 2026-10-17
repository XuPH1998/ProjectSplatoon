using System;
using System.Collections.Generic;
using System.Diagnostics;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        Queue<Vector2Int> _spreadDirty = new();
        HashSet<Vector2Int> _spreadDirtySet = new();
        Queue<Vector2Int> _spreadRound;
        readonly HashSet<Vector3Int> _spreadArrivals = new();
        readonly HashSet<Vector2Int> _spreadReceivedColumns = new();
        IEnumerator<Vector2Int?> _spreadSeed;
        IEnumerator<BlockSpreadMove?> _spreadSearch;
        BlockWorld _spreadSearchSource;
        readonly uint[] _spreadSearchGenerations = new uint[9];
        Vector2Int _spreadSearchColumn;
        bool _spreadQueued, _spreadOwesGravity;
        bool _spreadStartupWaiting;
        ulong _spreadStartupWaitVersion;
        Command _spreadCommand;
        uint _spreadGeneration;
        int _spreadQueryChecks;
        double _spreadNextAt;
        public long SpreadRounds { get; private set; }
        public long SpreadMoves { get; private set; }
        public long SpreadSearchChecks { get; private set; }
        public long SpreadPublicationRetries { get; private set; }

        void ResetBlockSpread(bool seed = false)
        {
            _spreadSeed?.Dispose(); _spreadSeed = seed && World != null ? SpreadSeed(World).GetEnumerator() : null;
            ResetSpreadSearch(); _spreadDirty = new(); _spreadDirtySet = new();
            _spreadRound = null; _spreadArrivals.Clear(); _spreadReceivedColumns.Clear(); _spreadQueued = _spreadOwesGravity = false; _spreadCommand = null; _spreadGeneration++;
            _spreadStartupWaiting = false; _spreadStartupWaitVersion = 0;
            _spreadNextAt = _now + BlockGameplaySettings.SpreadInterval;
        }
        static IEnumerable<Vector2Int?> SpreadSeed(BlockWorld world)
        {
            // One local column per yield, including empty ones. The immutable
            // snapshot is only a seed; every eventual search uses the live world.
            foreach (var pair in world.Chunks)
                for (int z = 0; z < BlockWorld.ChunkSize; z++)
                    for (int x = 0; x < BlockWorld.ChunkSize; x++)
                    {
                        var local = new Vector3Int(x, 0, z);
                        ushort row = pair.Value.UnanchoredVerticalRow(local);
                        var origin = BlockWorld.ChunkOrigin(pair.Key);
                        yield return row == 0 ? null : new Vector2Int(origin.x + x, origin.z + z);
                    }
        }
        void DirtySpread(Vector2Int column)
        {
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
            {
                var neighbour = column + new Vector2Int(x, z);
                if (_spreadDirtySet.Add(neighbour)) _spreadDirty.Enqueue(neighbour);
            }
        }
        void ScheduleSpreadWork()
        {
            if (_spreadQueued || _cohortIndexPending) return;
            // An executed search quantum leaves one actual opportunity for
            // outstanding gravity before another spread quantum joins FIFO.
            if (_spreadRound == null && _spreadSeed == null && (_spreadDirty.Count == 0 || _now < _spreadNextAt)) return;
            if (DeferSpreadForStartupVisit()) return;
            if (_spreadOwesGravity && (_gravityColumns.Count > 0 || FallingReady())) return;
            // A single finite search quantum joins the existing FIFO. Subsequent
            // quanta go behind waiting attacks, including under continuous fire.
            _spreadCommand = new Command { Spread = true, SpreadGeneration = _spreadGeneration, CreatedAt = _now };
            _commands.Enqueue(_spreadCommand);
            CommandsQueued++; _spreadQueued = true;
        }
        bool DeferSpreadForStartupVisit()
        {
            if (_startupColumns.Count == 0 && _startupMotions.Count == 0)
            { _spreadStartupWaiting = false; return false; }
            if (!_spreadStartupWaiting)
            {
                _spreadStartupWaiting = true; _spreadStartupWaitVersion = GravityStartupServedVersion;
                return true;
            }
            return GravityStartupServedVersion == _spreadStartupWaitVersion;
        }
        void ResetSpreadSearch()
        { _spreadSearch?.Dispose(); _spreadSearch = null; _spreadSearchSource = null; }
        bool SpreadSearchGenerationsMatch(bool capture)
        {
            int index = 0;
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
            {
                var column = _spreadSearchColumn + new Vector2Int(x, z);
                uint generation = _landingGeneration.TryGetValue(column, out uint found) ? found : 0;
                if (capture) _spreadSearchGenerations[index] = generation;
                else if (_spreadSearchGenerations[index] != generation) return false;
                index++;
            }
            return true;
        }
        void ConsumeSpreadColumn()
        {
            ResetSpreadSearch();
            if (_spreadRound == null) return;
            if (_spreadRound.Count != 0) _spreadRound.Dequeue();
            if (_spreadRound.Count == 0) { _spreadRound = null; _spreadArrivals.Clear(); _spreadReceivedColumns.Clear(); }
        }
        bool SpreadBlockedColumn(Vector2Int column)
            => _cohortIndexPending || _cohortHeads.TryGetValue(column, out var heads) && heads.Count != 0;
        bool SpreadStaticSupport(Vector3Int cell) { _spreadQueryChecks++; return StaticSupport(cell); }
        bool SpreadDestinationClear(Vector3Int cell)
        {
            _spreadQueryChecks++;
            if (MapOverlap(cell)) return false;
            _spreadQueryChecks += StaticWallAnchorMaxQueryChecks;
            // Fixed wall layers are built in place. A mobile surface block may
            // not migrate into that layer with an unanchored cell payload.
            return !StaticMapWallAnchor(cell);
        }
        bool SpreadRouteClear(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from; float length = delta.magnitude;
            if (length <= .00001f) return true;
            _spreadQueryChecks++;
            int count = Physics.BoxCastNonAlloc(from, Vector3.one * (BlockWorld.CellSize * .45f),
                delta / length, _rayHits, Quaternion.identity, length,
                PlayerMotorSimulation.WorldMask & ~(1 << TerrainLayer), QueryTriggerInteraction.Ignore);
            if (count == _rayHits.Length) return false;
            for (int i = 0; i < count; i++) if (_staticColliders.Contains(_rayHits[i].collider)) return false;
            return true;
        }
        void StageSpread(Command command, BlockWorld next, HashSet<Vector3Int> changed,
            List<(Command, BlockMutationResult)> results, ref int checks, Stopwatch timer, double milliseconds, int maximum)
        {
            if (command.SpreadGeneration != _spreadGeneration)
            { command.Done = true; _commands.Dequeue(); results.Add((command, default)); return; }
            _spreadOwesGravity = true;
            // The following quantum earns a new committed bounded startup
            // visit. Already queued FIFO work is never withdrawn by this gate.
            _spreadStartupWaiting = false;
            CheckpointCommand(command);
            int seeded = 0;
            while (_spreadSeed != null && seeded < 64 && checks + 16 <= maximum && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                checks += 16; SpreadSearchChecks += 16; CommandWorkChecks += 16; seeded++;
                if (!_spreadSeed.MoveNext()) { _spreadSeed.Dispose(); _spreadSeed = null; break; }
                if (_spreadSeed.Current.HasValue) DirtySpread(_spreadSeed.Current.Value);
            }
            if (_spreadRound == null && _spreadDirty.Count != 0 && _now >= _spreadNextAt)
            {
                _spreadRound = _spreadDirty; _spreadDirty = new(); _spreadDirtySet = new();
                _spreadArrivals.Clear(); _spreadReceivedColumns.Clear(); SpreadRounds++; _spreadNextAt = _now + BlockGameplaySettings.SpreadInterval;
            }
            bool stagedMove = false;
            if (_spreadRound != null && _spreadRound.Count != 0)
            {
                // An unrelated attack must not restart this resumable search.
                // Only the source's 3x3 neighbourhood can affect its lattice facts.
                if (_spreadSearch != null && !SpreadSearchGenerationsMatch(false)) ResetSpreadSearch();
                if (_spreadSearch == null)
                {
                    _spreadSearchSource = World; _spreadSearchColumn = _spreadRound.Peek(); SpreadSearchGenerationsMatch(true);
                    _spreadSearch = BlockSpreadRules.Search(World, _spreadRound.Peek(),
                        BlockGameplaySettings.SpreadMaxStepCells, BlockGameplaySettings.SpreadMinStackCells,
                        SpreadStaticSupport, SpreadDestinationClear, SpreadRouteClear, SpreadBlockedColumn,
                        _spreadArrivals, _spreadReceivedColumns).GetEnumerator();
                }
                // Reserve the largest finite native query unit before advancing,
                // then charge actual callback work without widening this budget.
                while (checks + StaticWallAnchorMaxQueryChecks + 4 <= maximum && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    _spreadQueryChecks = 0;
                    bool advanced = _spreadSearch.MoveNext(); int cost = 1 + _spreadQueryChecks;
                    checks += cost; SpreadSearchChecks += cost; CommandWorkChecks += cost;
                    if (!advanced) { ConsumeSpreadColumn(); break; }
                    if (!_spreadSearch.Current.HasValue) continue;
                    var move = _spreadSearch.Current.Value;
                    var keys = new HashSet<Vector3Int>(changed); IncludeCell(keys, move.From); IncludeCell(keys, move.To);
                    int mutationCost = EstimateQueueGravityWork(move.From) + EstimateQueueGravityWork(move.To) + 8;
                    if (keys.Count > _stagePublicationWindow.MaximumChangedKeys) { DirtySpread(_spreadRound.Peek()); ConsumeSpreadColumn(); break; }
                    if (checks + mutationCost > maximum || !HasStageBudget(checks, maximum, timer, milliseconds))
                    { ResetSpreadSearch(); break; }
                    checks += mutationCost; CommandWorkChecks += mutationCost; SpreadSearchChecks += mutationCost;
                    next.Set(move.From, default); next.Set(move.To, move.Value);
                    IncludeCell(changed, move.From); IncludeCell(changed, move.To);
                    QueueGravity(move.From); QueueGravity(move.To); _gravityJournal.SpreadMoves.Add(move);
                    stagedMove = true; break;
                }
            }
            // A quantum never stages a paid attack or gravity motion alongside a
            // move, so a live safety rejection can retry it without losing either.
            command.SpreadStaged = stagedMove; command.Done = true;
            _commands.Dequeue(); results.Add((command, default));
        }
        void OnSpreadGravityCommitted(GravityJournal journal)
        {
            if (!_authority || !_accepting) return;
            foreach (var pair in journal.Generations) DirtySpread(pair.Key);
            foreach (var move in journal.SpreadMoves)
            {
                _spreadArrivals.Add(move.To);
                _spreadReceivedColumns.Add(new Vector2Int(move.To.x, move.To.z)); SpreadMoves++;
                ConsumeSpreadColumn();
            }
        }
        bool SpreadFoundationStillValid(Vector3Int cell)
            => World.TryGet(cell, out var value) && value.Anchored || StaticSupport(cell);
        bool ValidateSpreadPublication(Pending pending)
        {
            if (pending.Gravity == null || pending.Gravity.SpreadMoves.Count == 0) return true;
            Physics.SyncTransforms();
            CaptureStageConstructionZones();
            try
            {
                foreach (var move in pending.Gravity.SpreadMoves)
                {
                    var fromColumn = new Vector2Int(move.From.x, move.From.z); var toColumn = new Vector2Int(move.To.x, move.To.z);
                    if (!World.TryGet(move.From, out var value) || !value.Equals(move.Value) || World.TryGet(move.To, out _)
                        || SpreadBlockedColumn(fromColumn) || SpreadBlockedColumn(toColumn)
                        || !SpreadFoundationStillValid(move.SourceFoundation) || !SpreadFoundationStillValid(move.TargetFoundation)
                        || !SpreadDestinationClear(move.To)) return false;
                    Vector3 a = BlockWorld.Center(move.From), b = BlockWorld.Center(move.To), high = new(b.x, a.y, b.z);
                    if (move.ViaFoundation.HasValue)
                    {
                        var via = move.ViaFoundation.Value;
                        if (SpreadBlockedColumn(new Vector2Int(via.x, via.z)) || !SpreadFoundationStillValid(via)
                            || Math.Abs(via.y - move.SourceFoundation.y) > BlockGameplaySettings.SpreadMaxStepCells
                            || Math.Abs(via.y - move.TargetFoundation.y) > BlockGameplaySettings.SpreadMaxStepCells) return false;
                        Vector3 corner = BlockWorld.Center(new Vector3Int(via.x, move.From.y, via.z));
                        if (!SpreadRouteClear(a, corner) || !SpreadRouteClear(corner, high)) return false;
                    }
                    else if (!SpreadRouteClear(a, high)) return false;
                    if (!SpreadRouteClear(high, b)) return false;
                }
                return true;
            }
            finally { _stageConstructionZonesActive = false; _stageConstructionZoneCount = 0; }
        }
        void RetrySpreadPublication(Pending pending)
        {
            SpreadPublicationRetries++;
            // Tick may continue searching while the changed physical geometry is
            // still present. Retain a next-round visit even if that search finds
            // no legal move, so a transient safety rejection cannot retire it.
            foreach (var move in pending.Gravity.SpreadMoves) DirtySpread(new Vector2Int(move.From.x, move.From.z));
            // The source column was not consumed. A new FIFO quantum searches
            // against current map/player geometry instead of replaying stale casts.
            ResetSpreadSearch(); AbortPendingCore(PreserveFairSeedAfterFailure(pending));
        }
        void CompleteSpread(Command command)
        {
            if (!command.Spread || !ReferenceEquals(_spreadCommand, command)) return;
            _spreadQueued = false; _spreadCommand = null;
            if (command.SpreadStaged) ResetSpreadSearch();
        }
    }
}
