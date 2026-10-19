using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed class BlockFixedGridTransaction : IDisposable
    {
        static readonly ProfilerMarker LandingCpu = new("Splatoon.Fixed.Landing");
        internal readonly BlockFixedGridWorld Owner;
        internal readonly uint SourceEpoch, SourceRound, SourceRevision, SourceSurfaceRevision;
        internal readonly Dictionary<ulong, BlockFixedGridState> Upserts = new();
        internal readonly Dictionary<ulong, BlockFixedGridHandle> Deleted = new();
        internal readonly Dictionary<Vector3Int, ulong> CellWrites = new();
        internal readonly Dictionary<BlockSurfaceKey, byte> PaintWrites = new();
        readonly HashSet<Vector2Int> _dirtyColumns = new();
        // A written zero hides published occupancy; forgetting a write restores it.
        struct VerticalWrites { internal ushort Written, Occupied; }
        readonly Dictionary<Vector3Int, VerticalWrites> _verticalWrites = new();
        readonly Dictionary<Vector2Int, HashSet<ulong>> _upsertsByColumn = new();
#if UNITY_EDITOR
        internal long LandingRowReads, LandingOccupiedChecks, ColumnStagedEntriesVisited;
#endif
        bool _closed, _prepared;
        string _error;
        internal ulong NextId;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal readonly bool ObservationEnabled;
        internal readonly BlockAttackObservation.Context ObservationContext;
        readonly Dictionary<BlockSurfaceKey, (byte team, bool conversion)> _observedPaint;
        readonly Dictionary<ulong, byte> _observedDirect;
        readonly HashSet<ulong> _observedConversions;
#endif
        public BlockMutationResult Result { get; private set; }
        public int RelocatedCount { get; private set; }
        public int ConvertedCount { get; private set; }
        internal bool HasChanges => Upserts.Count != 0 || Deleted.Count != 0 || PaintWrites.Count != 0;

        internal BlockFixedGridTransaction(BlockFixedGridWorld owner)
        {
            Owner = owner; SourceEpoch = owner.Epoch; SourceRound = owner.Round;
            SourceRevision = owner.Revision; SourceSurfaceRevision = owner.Surfaces.Revision; NextId = owner.NextId;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ObservationEnabled = BlockAttackObservation.HasActualObservers;
            if (ObservationEnabled)
            {
                ObservationContext = BlockAttackObservation.CurrentContext;
                _observedPaint = new(); _observedDirect = new(); _observedConversions = new();
            }
#endif
        }
        void CheckOpen() { if (_closed || _prepared) throw new InvalidOperationException("The fixed transaction is already closed or prepared."); }
        public bool TryGet(Vector3Int cell, out BlockFixedGridState state)
        {
            if (CellWrites.TryGetValue(cell, out ulong id))
            { if (id != 0 && Upserts.TryGetValue(id, out state)) return true; state = default; return false; }
            return Owner.TryGet(cell, out state) && !Deleted.ContainsKey(state.Handle.Id);
        }
        bool TryGet(BlockFixedGridHandle handle, out BlockFixedGridState state)
        {
            if (Deleted.ContainsKey(handle.Id)) { state = default; return false; }
            if (Upserts.TryGetValue(handle.Id, out state)) return state.Handle == handle;
            return Owner.TryGet(handle, out state);
        }
        byte SurfaceOwner(BlockSurfaceKey key)
            => PaintWrites.TryGetValue(key, out byte owner) ? owner : Owner.Surfaces.TryGet(key, out var cell) ? cell.Owner : (byte)0;
        bool SetPaint(BlockSurfaceKey key, byte owner)
        {
            if (!Owner.Surfaces.TryGet(key, out var old) || SurfaceOwner(key) == owner) return false;
            if (owner != 0 && Owner.Environment is IBlockFixedGridPaintAdmission admission && !admission.PaintAllowed(key, out _)) return false;
            if (owner == old.Owner) PaintWrites.Remove(key); else PaintWrites[key] = owner;
            return true;
        }
        public int Paint(IEnumerable<BlockSurfaceKey> keys, byte owner, int maxAdded)
        {
            CheckOpen(); if (owner is not (1 or 2) || keys == null || maxAdded <= 0) return 0;
            int added = 0; var result = Result;
            foreach (var key in keys)
            {
                if (added == maxAdded) break;
                byte old = SurfaceOwner(key); if (!SetPaint(key, owner)) continue;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (ObservationEnabled) _observedPaint[key] = (owner, false);
#endif
                added++; result.Added++; if (old != 0) result.RemovedEnemy++;
            }
            Result = result; return added;
        }
        // Explosion removes paint without consuming the ordinary build budget.
        public BlockMutationResult DestroyPaint(IEnumerable<BlockSurfaceKey> keys, byte team, int maxEnemy = int.MaxValue, int maxFriendly = int.MaxValue)
        {
            CheckOpen(); var delta = new BlockMutationResult();
            if (keys == null || team is not (1 or 2)) return delta;
            foreach (var key in keys)
            {
                byte old = SurfaceOwner(key); if (old == 0 || (old == team ? delta.RemovedFriendly >= maxFriendly : delta.RemovedEnemy >= maxEnemy)) continue;
                if (!SetPaint(key, 0)) continue;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (ObservationEnabled) _observedPaint[key] = (team, false);
#endif
                if (old == team) delta.RemovedFriendly++; else delta.RemovedEnemy++;
            }
            var result = Result; result.RemovedEnemy += delta.RemovedEnemy; result.RemovedFriendly += delta.RemovedFriendly; Result = result; return delta;
        }
        public BlockMutationResult RemovePaint(IEnumerable<BlockSurfaceKey> keys, byte team, int maxEnemy = int.MaxValue, int maxFriendly = int.MaxValue)
            => DestroyPaint(keys, team, maxEnemy, maxFriendly);
        public void ReconcileColumns(IEnumerable<Vector2Int> columns)
        { CheckOpen(); if (columns != null) foreach (var column in columns) _dirtyColumns.Add(column); }
        public int Build(IEnumerable<Vector3Int> candidates, byte owner, int maxAdded)
        {
            CheckOpen(); if (candidates == null || owner is not (1 or 2) || maxAdded <= 0) return 0;
            int added = 0; var seen = new HashSet<Vector3Int>();
            using var iterator = candidates.GetEnumerator();
            while (added < maxAdded && iterator.MoveNext())
            {
                var candidate = iterator.Current;
                if (!seen.Add(candidate) || TryGet(candidate, out _)) continue;
                if (!TryLanding(candidate, owner, false, out var cell, out var support, out bool paint, out _)) continue;
                var result = Result;
                if (paint)
                {
                    byte old = SurfaceOwner(support.SurfaceKey);
                    if (!SetPaint(support.SurfaceKey, owner)) continue;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (ObservationEnabled) _observedPaint[support.SurfaceKey] = (owner, false);
#endif
                    if (old != 0) result.RemovedEnemy++;
                }
                else
                {
                    if (NextId == 0 || NextId == ulong.MaxValue) { _error = "Fixed ID space exhausted."; break; }
                    var state = new BlockFixedGridState { Handle = new BlockFixedGridHandle { Epoch = SourceEpoch, Round = SourceRound, Generation = 1, Id = NextId++ },
                        Cell = cell, Owner = owner, Support = support };
                    StageUpsert(state, true);
                }
                added++; result.Added++; Result = result;
            }
            return added;
        }
        public BlockMutationResult Destroy(IEnumerable<BlockFixedGridHandle> handles, byte team, int maxEnemy = int.MaxValue, int maxFriendly = int.MaxValue)
        {
            CheckOpen(); var delta = new BlockMutationResult();
            if (handles == null || team is not (1 or 2)) return delta;
            foreach (var handle in handles)
            {
                if (!TryGet(handle, out var state) || (state.Owner == team ? delta.RemovedFriendly >= maxFriendly : delta.RemovedEnemy >= maxEnemy)) continue;
                Remove(state); _dirtyColumns.Add(BlockFixedGridWorld.Column(state.Cell));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (ObservationEnabled) _observedDirect[state.Handle.Id] = team;
#endif
                if (state.Owner == team) delta.RemovedFriendly++; else delta.RemovedEnemy++;
            }
            var result = Result; result.RemovedEnemy += delta.RemovedEnemy; result.RemovedFriendly += delta.RemovedFriendly; Result = result; return delta;
        }
        void Remove(BlockFixedGridState state)
        {
            RemoveUpsert(state.Handle.Id); WriteCell(state.Cell, 0);
            if (Owner.States.ContainsKey(state.Handle.Id)) Deleted[state.Handle.Id] = state.Handle;
        }
        static Vector3Int VerticalRowKey(Vector3Int cell)
            => new(cell.x, cell.y & ~(BlockWorld.ChunkSize - 1), cell.z);
        void WriteCell(Vector3Int cell, ulong id)
        {
            CellWrites[cell] = id;
            var key = VerticalRowKey(cell);
            _verticalWrites.TryGetValue(key, out var row);
            ushort bit = (ushort)(1 << (cell.y - key.y));
            row.Written |= bit;
            if (id == 0) row.Occupied = (ushort)(row.Occupied & ~bit);
            else row.Occupied |= bit;
            _verticalWrites[key] = row;
        }
        void ForgetCellWrite(Vector3Int cell)
        {
            CellWrites.Remove(cell);
            var key = VerticalRowKey(cell);
            if (!_verticalWrites.TryGetValue(key, out var row)) return;
            ushort bit = (ushort)(1 << (cell.y - key.y));
            row.Written = (ushort)(row.Written & ~bit);
            row.Occupied = (ushort)(row.Occupied & ~bit);
            if (row.Written == 0) _verticalWrites.Remove(key);
            else _verticalWrites[key] = row;
        }
        void StageUpsert(BlockFixedGridState state, bool requireNew)
        {
            bool hadOld = Upserts.TryGetValue(state.Handle.Id, out var old);
            if (requireNew) Upserts.Add(state.Handle.Id, state);
            else Upserts[state.Handle.Id] = state;
            if (hadOld && BlockFixedGridWorld.Column(old.Cell) != BlockFixedGridWorld.Column(state.Cell))
                RemoveColumnUpsert(old);
            var column = BlockFixedGridWorld.Column(state.Cell);
            if (!_upsertsByColumn.TryGetValue(column, out var ids))
                _upsertsByColumn.Add(column, ids = new HashSet<ulong>());
            ids.Add(state.Handle.Id);
            WriteCell(state.Cell, state.Handle.Id);
        }
        void RemoveColumnUpsert(BlockFixedGridState state)
        {
            var column = BlockFixedGridWorld.Column(state.Cell);
            if (!_upsertsByColumn.TryGetValue(column, out var ids)) return;
            ids.Remove(state.Handle.Id);
            if (ids.Count == 0) _upsertsByColumn.Remove(column);
        }
        void RemoveUpsert(ulong id)
        {
            if (!Upserts.TryGetValue(id, out var state)) return;
            RemoveColumnUpsert(state); Upserts.Remove(id);
        }
        static int HighestBit16(uint bits)
        {
            int bit = 0;
            if (bits >= 256) { bits >>= 8; bit += 8; }
            if (bits >= 16) { bits >>= 4; bit += 4; }
            if (bits >= 4) { bits >>= 2; bit += 2; }
            return bit + (bits >= 2 ? 1 : 0);
        }
        int FindLandingTarget(Vector3Int from, int baseY)
        {
            // Preserve the complete original [baseY, from.y - 1] interval. Unknown
            // environments need not have monotone map supports or a four-cell gap.
            for (int y = from.y; y > baseY;)
            {
                var high = new Vector3Int(from.x, y - 1, from.z);
                if (!Owner.Storage.InBounds(high)) break;
                var key = VerticalRowKey(high);
                int low = Math.Max(baseY, key.y);
                // Preserve the original first-out-of-bounds stop, including the
                // exact float tolerance. Only the bottom boundary row needs this
                // binary search; no Bottom-to-grid rounding formula is introduced.
                if (!Owner.Storage.InBounds(new Vector3Int(from.x, low, from.z)))
                {
                    int upper = high.y;
                    while (low < upper)
                    {
                        int middle = low + (upper - low) / 2;
                        if (Owner.Storage.InBounds(new Vector3Int(from.x, middle, from.z))) upper = middle;
                        else low = middle + 1;
                    }
                }
#if UNITY_EDITOR
                LandingRowReads++;
#endif
                uint occupied = Owner.Storage.OccupiedRow(high, 1);
                if (_verticalWrites.TryGetValue(key, out var overlay))
                    occupied = (occupied & ~(uint)overlay.Written) | overlay.Occupied;
                uint throughHigh = (1u << (high.y - key.y + 1)) - 1;
                uint belowLow = (1u << (low - key.y)) - 1;
                occupied &= throughHigh & ~belowLow;
                while (occupied != 0)
                {
                    int bit = HighestBit16(occupied);
                    var below = new Vector3Int(from.x, key.y + bit, from.z);
#if UNITY_EDITOR
                    LandingOccupiedChecks++;
#endif
                    // Storage and the fixed index publish together. Retain the
                    // authoritative payload lookup before accepting a support.
                    if (TryGet(below, out _)) return below.y + 1;
                    occupied &= ~(1u << bit);
                }
                y = low;
            }
            return baseY;
        }
        IEnumerable<BlockFixedGridState> ColumnStates(Vector2Int key)
        {
            var result = new Dictionary<ulong, BlockFixedGridState>();
            if (Owner.Columns.TryGetValue(key, out var column))
                foreach (var pair in column)
                {
                    if (Deleted.ContainsKey(pair.Value)) continue;
                    var state = Upserts.TryGetValue(pair.Value, out var changed) ? changed : Owner.States[pair.Value];
                    if (BlockFixedGridWorld.Column(state.Cell) == key) result[pair.Value] = state;
                }
            if (_upsertsByColumn.TryGetValue(key, out var staged))
                foreach (ulong id in staged)
                {
#if UNITY_EDITOR
                    ColumnStagedEntriesVisited++;
#endif
                    result[id] = Upserts[id];
                }
            return result.Values.OrderBy(s => s.Cell.y).ThenBy(s => s.Handle.Id);
        }
        bool TryLanding(Vector3Int from, byte owner, bool relocating, out Vector3Int cell,
            out BlockFixedGridMapSupport map, out bool paint, out string error)
        {
            using var timing = LandingCpu.Auto();
            cell = from; map = default; paint = false; error = null;
            if (!Owner.Storage.InBounds(from) || !Owner.Environment.TryGetMapSupport(from, out map) || !Owner.ValidMapSupport(map) ||
                map.Height > (from.y + 1d) * BlockWorld.CellSize + .00001)
            { error = "No legal map support below the candidate."; return false; }
            cell.y = FindLandingTarget(from, map.BaseCellY);
            if (cell.y > from.y || !Owner.WithinCap(cell, map) || TryGet(cell, out _))
            { error = "Fixed landing is occupied, exceeds its local map cap, or would move upward."; return false; }
            if (!Owner.Environment.PlacementAllowed(cell, map, cell.y == map.BaseCellY, relocating, out error)) return false;
            paint = cell.y == map.BaseCellY && SurfaceOwner(map.SurfaceKey) != owner;
            if (paint && (!relocating || cell != from) && Owner.Environment is IBlockFixedGridPaintAdmission admission && !admission.PaintAllowed(map.SurfaceKey, out error)) return false;
            return true;
        }
        internal bool Prepare(out string error)
        {
            error = _error; if (_closed || _prepared) { error = "Transaction is already consumed."; return false; }
            _prepared = true;
            if (error != null) return false;
            foreach (var key in _dirtyColumns.OrderBy(c => c.x).ThenBy(c => c.y))
            {
                var remaining = ColumnStates(key).ToArray();
                foreach (var state in remaining) Remove(state);
                foreach (var old in remaining)
                {
                    if (!TryLanding(old.Cell, old.Owner, true, out var cell, out var support, out bool paint, out error)) return false;
                    // Restacking is triggered by a removed support. A block that did not move does not acquire a new map impact.
                    if (cell == old.Cell) paint = false;
                    if (paint)
                    {
                        if (!SetPaint(support.SurfaceKey, old.Owner)) { error = "Restacking surface conversion was rejected."; return false; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        if (ObservationEnabled)
                        { _observedPaint[support.SurfaceKey] = (old.Owner, true); _observedConversions.Add(old.Handle.Id); }
#endif
                        ConvertedCount++; continue;
                    }
                    var state = old; state.Cell = cell; state.Support = support;
                    Deleted.Remove(state.Handle.Id); StageUpsert(state, false);
                    if (cell != old.Cell) RelocatedCount++;
                }
            }
            // Remove identity-preserving no-op rewrites; unrelated columns are never visited.
            foreach (var pair in Upserts.ToArray())
                if (Owner.States.TryGetValue(pair.Key, out var old) && old.Cell == pair.Value.Cell && old.Owner == pair.Value.Owner &&
                    BlockFixedGridWorld.SameSupport(old.Support, pair.Value.Support))
                { RemoveUpsert(pair.Key); ForgetCellWrite(old.Cell); }
            return true;
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Compare only final changed identities against this transaction's baseline. A
        // temporary paint write or identity-preserving restack is never a paid addition.
        internal BlockAttackObservation.ActualCounts ObservationCounts()
        {
            var counts = new BlockAttackObservation.ActualCounts();
            if (!ObservationEnabled) return counts;
            foreach (var pair in Upserts)
                if (!Owner.States.ContainsKey(pair.Key)) counts.NewFixed++;
            foreach (var pair in Deleted)
            {
                if (_observedConversions.Contains(pair.Key) || !Owner.States.TryGetValue(pair.Key, out var old) ||
                    !_observedDirect.TryGetValue(pair.Key, out byte team)) continue;
                if (old.Owner == team) counts.FriendlyFixedRemoved++; else counts.EnemyFixedRemoved++;
            }
            foreach (var pair in PaintWrites)
            {
                if (!_observedPaint.TryGetValue(pair.Key, out var cause) || cause.conversion ||
                    !Owner.Surfaces.TryGet(pair.Key, out var old) || old.Owner == pair.Value) continue;
                if (pair.Value != 0)
                { if (old.Owner == 0) counts.NewPaint++; else counts.EnemyPaintReplaced++; }
                else if (old.Owner == cause.team) counts.FriendlyPaintRemoved++;
                else counts.EnemyPaintRemoved++;
            }
            counts.Converted = ConvertedCount; counts.Relocated = RelocatedCount;
            return counts;
        }
        internal void PublishObservation(BlockAttackObservation.ActualCounts counts, bool changed)
        {
            if (ObservationEnabled) BlockAttackObservation.PublishActual(ObservationContext, Owner, counts, changed);
        }
#endif
        public bool TryCommit(out BlockMutationResult result, out string error)
        {
            if (_closed) { result = default; error = "Transaction is already consumed."; return false; }
            try { return Owner.Commit(this, out result, out error); }
            finally { _closed = true; }
        }
        public void Dispose() { _closed = true; }
    }

    public sealed class BlockFixedGridCommitPreview
    {
        readonly BlockFixedGridTransaction _tx;
        BlockWorld _storage;
        BlockSurfaceWorld _surfaces;
        public Vector3Int[] ChangedCells { get; }
        internal BlockFixedGridCommitPreview(BlockFixedGridTransaction tx, Vector3Int[] changed) { _tx = tx; ChangedCells = changed; }
        public bool TryGet(Vector3Int cell, out BlockFixedGridState state) => _tx.TryGet(cell, out state);
        // Storage uses a shallow world shell and copy-on-write changed chunks. Paint is cloned only if safety requests it.
        public BlockWorld Storage
        {
            get
            {
                if (_storage != null) return _storage;
                _storage = _tx.Owner.Storage.Clone();
                foreach (var cell in ChangedCells) _storage.Set(cell, default);
                foreach (var pair in _tx.Upserts) _storage.Set(pair.Value.Cell, BlockCell.Create(pair.Value.Owner));
                return _storage;
            }
        }
        public BlockSurfaceWorld Surfaces
        {
            get
            {
                if (_surfaces != null) return _surfaces;
                _surfaces = _tx.Owner.Surfaces.Clone();
                foreach (var pair in _tx.PaintWrites) _surfaces.TrySet(pair.Key, pair.Value, out _);
                return _surfaces;
            }
        }
    }
}
