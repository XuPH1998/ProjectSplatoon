using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockFixedGridWorld
    {
        public BlockFixedGridSnapshot Capture(bool full, uint sinceRevision = 0, uint sinceSurfaceRevision = 0)
        {
            if (sinceRevision > Revision || sinceSurfaceRevision > Surfaces.Revision) throw new ArgumentOutOfRangeException(nameof(sinceRevision));
            if (!full && sinceRevision < _journalFloor) full = true;
            var upserts = new Dictionary<ulong, BlockFixedGridState>(); var removed = new Dictionary<ulong, BlockFixedGridHandle>();
            if (full) foreach (var pair in States) upserts.Add(pair.Key, pair.Value);
            else foreach (var entry in _journal)
                if (entry.Revision > sinceRevision)
                {
                    foreach (var state in entry.Upserts) { upserts[state.Handle.Id] = state; removed.Remove(state.Handle.Id); }
                    foreach (var handle in entry.Removed) { upserts.Remove(handle.Id); removed[handle.Id] = handle; }
                }
            return new BlockFixedGridSnapshot { Full = full, Frozen = Frozen, Epoch = Epoch, Round = Round,
                BaseRevision = full ? 0 : sinceRevision, Revision = Revision, SurfaceBaseRevision = full ? 0 : sinceSurfaceRevision,
                SurfaceRevision = Surfaces.Revision, NextId = _nextId, Upserts = upserts.Values.OrderBy(s => s.Handle.Id).ToArray(),
                Removed = removed.Values.OrderBy(h => h.Id).ToArray(), SurfaceBytes = full ? Surfaces.ExportFull() : Surfaces.ExportDelta(sinceSurfaceRevision) };
        }

        public bool TryPrepareImport(BlockFixedGridSnapshot snapshot, out BlockFixedGridPreparedImport prepared, out string error)
        {
            try { return TryPrepareImportValidated(snapshot, out prepared, out error); }
            catch (Exception exception)
            { prepared = null; error = LastFailure = exception.GetType().Name + ": " + exception.Message; return false; }
        }
        bool TryPrepareImportValidated(BlockFixedGridSnapshot snapshot, out BlockFixedGridPreparedImport prepared, out string error)
        {
            prepared = null; error = null;
            if (_committing || snapshot == null || snapshot.Epoch == 0 || snapshot.NextId == 0 || snapshot.Upserts == null || snapshot.Removed == null || snapshot.SurfaceBytes == null)
            { error = "Missing or invalid fixed snapshot header."; return false; }
            if (snapshot.Full)
            {
                if (snapshot.BaseRevision != 0 || snapshot.SurfaceBaseRevision != 0 || snapshot.Removed.Length != 0 || snapshot.Round < Round ||
                    snapshot.Round == Round && snapshot.Epoch < Epoch || snapshot.Epoch == Epoch && snapshot.Round != Round ||
                    snapshot.Epoch == Epoch && (snapshot.Revision < Revision || snapshot.SurfaceRevision < Surfaces.Revision || snapshot.NextId < _nextId || Frozen && !snapshot.Frozen))
                { error = "Full fixed snapshot regressed its epoch, round, revision or frozen state."; return false; }
            }
            else if (snapshot.Epoch != Epoch || snapshot.Round != Round || snapshot.BaseRevision != Revision || snapshot.Revision < Revision ||
                snapshot.SurfaceBaseRevision != Surfaces.Revision || snapshot.SurfaceRevision < Surfaces.Revision || snapshot.NextId < _nextId || Frozen && !snapshot.Frozen)
            { error = "Fixed delta baseline or frozen state does not match."; return false; }
            if (!snapshot.Full && snapshot.Revision == Revision && (snapshot.Upserts.Length != 0 || snapshot.Removed.Length != 0 ||
                snapshot.SurfaceRevision != Surfaces.Revision || snapshot.NextId != _nextId || snapshot.Frozen != Frozen))
            { error = "A changed fixed delta must advance its outer commit revision."; return false; }
            if (!Surfaces.TryPrepareImport(snapshot.SurfaceBytes, snapshot.Full, snapshot.Round, out var surface) ||
                surface.Round != snapshot.Round || surface.Revision != snapshot.SurfaceRevision)
            { error = "Paired surface snapshot failed topology, round or baseline validation."; return false; }
            var upserts = new Dictionary<ulong, BlockFixedGridState>(); var removes = new Dictionary<ulong, BlockFixedGridHandle>();
            var destinations = new Dictionary<Vector3Int, BlockFixedGridState>(); var affected = new HashSet<Vector2Int>();
            foreach (var handle in snapshot.Removed)
            {
                if (!ValidImportedHandle(handle, snapshot) || removes.ContainsKey(handle.Id) ||
                    States.TryGetValue(handle.Id, out var old) && old.Handle != handle)
                { error = "Fixed removal contains duplicate or stale lifecycle identity."; return false; }
                removes.Add(handle.Id, handle);
                if (States.TryGetValue(handle.Id, out old)) affected.Add(Column(old.Cell));
            }
            foreach (var state in snapshot.Upserts)
            {
                if (!ValidImportedHandle(state.Handle, snapshot) || state.Owner is not (1 or 2) || state.Revision == 0 || state.Revision > snapshot.Revision ||
                    !snapshot.Full && state.Revision <= snapshot.BaseRevision || upserts.ContainsKey(state.Handle.Id) || removes.ContainsKey(state.Handle.Id) || destinations.ContainsKey(state.Cell) ||
                    !snapshot.Full && States.TryGetValue(state.Handle.Id, out var previous) && previous.Handle != state.Handle ||
                    !snapshot.Full && !States.ContainsKey(state.Handle.Id) && state.Handle.Id < _nextId ||
                    !WithinCap(state.Cell, state.Support) || !Environment.TryGetMapSupport(state.Cell, out var actualSupport) || !SameSupport(actualSupport, state.Support))
                { error = "Fixed upsert contains invalid identity, duplicate occupancy, revision or map support."; return false; }
                upserts.Add(state.Handle.Id, state); destinations.Add(state.Cell, state); affected.Add(Column(state.Cell));
                if (!snapshot.Full && States.TryGetValue(state.Handle.Id, out var old)) affected.Add(Column(old.Cell));
            }
            bool TryFinal(Vector3Int cell, out BlockFixedGridState state)
            {
                if (destinations.TryGetValue(cell, out state)) return true;
                if (snapshot.Full || !TryGet(cell, out state) || removes.ContainsKey(state.Handle.Id) || upserts.ContainsKey(state.Handle.Id))
                { state = default; return false; }
                return true;
            }
            var check = new Dictionary<ulong, BlockFixedGridState>(upserts);
            if (!snapshot.Full)
                foreach (var columnKey in affected)
                    if (Columns.TryGetValue(columnKey, out var column)) foreach (var pair in column)
                        if (!removes.ContainsKey(pair.Value) && !upserts.ContainsKey(pair.Value)) check[pair.Value] = States[pair.Value];
            foreach (var state in check.Values)
            {
                if (destinations.TryGetValue(state.Cell, out var other) && other.Handle != state.Handle ||
                    !snapshot.Full && Cells.TryGetValue(state.Cell, out ulong oldId) && oldId != state.Handle.Id && !removes.ContainsKey(oldId) && !upserts.ContainsKey(oldId) ||
                    state.Cell.y != state.Support.BaseCellY && !TryFinal(state.Cell + Vector3Int.down, out _))
                { error = "Fixed import leaves overlapping or unsupported occupancy."; return false; }
            }
            // The caller can reuse its DTO or receive another packet while this preparation waits.
            var copy = new BlockFixedGridSnapshot { Full = snapshot.Full, Frozen = snapshot.Frozen, Epoch = snapshot.Epoch, Round = snapshot.Round,
                BaseRevision = snapshot.BaseRevision, Revision = snapshot.Revision, SurfaceBaseRevision = snapshot.SurfaceBaseRevision,
                SurfaceRevision = snapshot.SurfaceRevision, NextId = snapshot.NextId, Upserts = snapshot.Upserts.ToArray(), Removed = snapshot.Removed.ToArray(),
                SurfaceBytes = snapshot.SurfaceBytes.ToArray() };
            prepared = new BlockFixedGridPreparedImport { Owner = this, SourceEpoch = Epoch, SourceRound = Round,
                SourceRevision = Revision, SourceSurfaceRevision = Surfaces.Revision, Snapshot = copy, Surface = surface };
            return true;
        }
        static bool ValidImportedHandle(BlockFixedGridHandle handle, BlockFixedGridSnapshot snapshot)
            => handle.IsValid && handle.Epoch == snapshot.Epoch && handle.Round == snapshot.Round && handle.Id < snapshot.NextId;

        public bool CommitImport(BlockFixedGridPreparedImport prepared)
        {
            if (_committing || prepared == null || prepared.Owner != this || prepared.Consumed || prepared.SourceEpoch != Epoch ||
                prepared.SourceRound != Round || prepared.SourceRevision != Revision || prepared.SourceSurfaceRevision != Surfaces.Revision) return false;
            _committing = true;
            try
            {
                var snapshot = prepared.Snapshot; var changed = new HashSet<Vector3Int>();
                var changedPaint = new HashSet<BlockSurfaceKey>(ReadSurfaceKeys(snapshot.SurfaceBytes));
                if (snapshot.Full) changedPaint.UnionWith(Surfaces.EnumeratePainted().Select(c => c.Key));
                // Every allocation and storage edit completes before the publication boundary.
                var next = snapshot.Full ? new BlockWorld(Storage.Dimensions, snapshot.Round, Storage.Bottom) : Storage.Clone();
                if (snapshot.Full) changed.UnionWith(Cells.Keys);
                foreach (var handle in snapshot.Removed) if (States.TryGetValue(handle.Id, out var old)) { next.Set(old.Cell, default); changed.Add(old.Cell); }
                foreach (var state in snapshot.Upserts)
                    if (!snapshot.Full && States.TryGetValue(state.Handle.Id, out var old)) { next.Set(old.Cell, default); changed.Add(old.Cell); }
                foreach (var state in snapshot.Upserts) { next.Set(state.Cell, BlockCell.Create(state.Owner)); changed.Add(state.Cell); }
                var changedCells = changed.ToArray(); var changedPaintKeys = changedPaint.ToArray();
                next.Seal(snapshot.Revision, changed.Select(BlockWorld.ChunkAt).Distinct());
                Dictionary<ulong, BlockFixedGridState> nextStates = null;
                Dictionary<Vector3Int, ulong> nextCells = null;
                Dictionary<Vector2Int, SortedDictionary<int, ulong>> nextColumns = null;
                Dictionary<Vector2Int, KeyValuePair<int, ulong>[]> nextQueryColumns = null;
                IndexPatch patch = null;
                if (snapshot.Full)
                {
                    nextStates = new(snapshot.Upserts.Length); nextCells = new(snapshot.Upserts.Length); nextColumns = new();
                    foreach (var state in snapshot.Upserts)
                    {
                        nextStates.Add(state.Handle.Id, state); nextCells.Add(state.Cell, state.Handle.Id);
                        var key = Column(state.Cell);
                        if (!nextColumns.TryGetValue(key, out var column)) nextColumns.Add(key, column = new());
                        column.Add(state.Cell.y, state.Handle.Id);
                    }
                    nextQueryColumns = new(nextColumns.Count);
                    foreach (var pair in nextColumns) nextQueryColumns.Add(pair.Key, pair.Value.ToArray());
                }
                else patch = new IndexPatch(this, snapshot.Upserts, snapshot.Removed);
                EnsureJournalCapacity();
                var entry = new Journal { Revision = snapshot.Revision, Upserts = snapshot.Upserts, Removed = snapshot.Removed };
                if (snapshot.Full)
                {
                    States = nextStates; Cells = nextCells; Columns = nextColumns; QueryColumns = nextQueryColumns;
                    _journal.Clear(); _journalFloor = snapshot.Revision;
                }
                else
                {
                    try { patch.Apply(this); }
                    catch { patch.Restore(this); throw; }
                }
                Storage = next; Surfaces = prepared.Surface; Epoch = snapshot.Epoch; Revision = snapshot.Revision;
                _nextId = snapshot.NextId; Frozen = snapshot.Frozen; LastRelocatedCount = LastConvertedCount = 0;
                _journal.Add(entry); prepared.Consumed = true;
                Notify(new BlockFixedGridCommit(Epoch, Round, Revision, Surfaces.Revision, Frozen, changedCells, default, changedPaintKeys: changedPaintKeys)); return true;
            }
            catch (Exception exception) { LastFailure = exception.GetType().Name + ": " + exception.Message; return false; }
            finally { _committing = false; }
        }
        static IEnumerable<BlockSurfaceKey> ReadSurfaceKeys(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            stream.Position = 26; int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            { var key = new BlockSurfaceKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()); reader.ReadByte(); reader.ReadUInt32(); yield return key; }
        }
    }
}
