using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Blocks
{
    /// <summary>Mutation-driven fixed occupancy. There is no per-frame simulation or native physics object.</summary>
    public sealed partial class BlockFixedGridWorld
    {
        static readonly ProfilerMarker CommitCpu = new("Splatoon.Fixed.Commit");
        static readonly ProfilerMarker CommitStorageCpu = new("Splatoon.Fixed.Commit.Storage");
        static readonly ProfilerMarker CommitIndexCpu = new("Splatoon.Fixed.Commit.Index");
        static readonly ProfilerMarker CommitGuardCpu = new("Splatoon.Fixed.Commit.Guard");
        public const int SimulationVersion = 1, MaximumStackLayers = 4;
        public const float MaximumStackHeight = 1.2f;
        const float Epsilon = .00001f;
        internal Dictionary<ulong, BlockFixedGridState> States = new();
        internal Dictionary<Vector3Int, ulong> Cells = new();
        internal Dictionary<Vector2Int, SortedDictionary<int, ulong>> Columns = new();
        // Immutable, sorted occupied entries for allocation-free collision queries.
        // Replaced with the same affected-column patch as the authoritative indexes.
        internal Dictionary<Vector2Int, KeyValuePair<int, ulong>[]> QueryColumns = new();
        readonly List<Journal> _journal = new();
        uint _journalFloor;
        bool _committing;
        ulong _nextId = 1;
        public BlockWorld Storage { get; private set; }
        public BlockSurfaceWorld Surfaces { get; private set; }
        public IBlockFixedGridEnvironment Environment { get; }
        public uint Epoch { get; private set; }
        public uint Round => Storage.Round;
        public uint Revision { get; private set; }
        public bool Frozen { get; private set; }
        public ulong NextId => _nextId;
        public int PinkCount => Storage.PinkCount + Surfaces.PinkCount;
        public int BlueCount => Storage.BlueCount + Surfaces.BlueCount;
        public int Count => States.Count;
        public int LastRelocatedCount { get; private set; }
        public int LastConvertedCount { get; private set; }
        public string LastFailure { get; private set; }
        public string LastObserverException { get; private set; }
        public IReadOnlyDictionary<ulong, BlockFixedGridState> Blocks => States;
        public event Action<BlockFixedGridCommit> Committed;
        sealed class Journal
        {
            public uint Revision;
            public BlockFixedGridState[] Upserts;
            public BlockFixedGridHandle[] Removed;
        }
        void EnsureJournalCapacity()
        {
            if (_journal.Count < _journal.Capacity) return;
            _journal.Capacity = _journal.Count == 0 ? 4 : checked(_journal.Count + Math.Max(4, _journal.Count));
        }

        public BlockFixedGridWorld(BlockWorld storage, BlockSurfaceWorld surfaces, IBlockFixedGridEnvironment environment, uint epoch = 1)
        {
            Storage = storage ?? throw new ArgumentNullException(nameof(storage));
            Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            if (epoch == 0 || storage.Round != surfaces.Round || storage.PinkCount != 0 || storage.BlueCount != 0 || storage.Falling.Count != 0)
                throw new ArgumentException("Fixed mode requires a new empty solid world and matching surface round; legacy data is not migrated.");
            Epoch = epoch; Revision = storage.Revision;
        }

        public bool TryGet(Vector3Int cell, out BlockFixedGridState state)
        { if (Cells.TryGetValue(cell, out ulong id)) return States.TryGetValue(id, out state); state = default; return false; }
        public bool TryGet(BlockFixedGridHandle handle, out BlockFixedGridState state)
        { if (handle.Epoch == Epoch && handle.Round == Round && States.TryGetValue(handle.Id, out state) && state.Handle == handle) return true; state = default; return false; }
        public bool TryGet(ulong id, out BlockFixedGridState state) => States.TryGetValue(id, out state);
        internal ReadOnlySpan<KeyValuePair<int, ulong>> QueryColumn(int x, int z)
            => QueryColumns.TryGetValue(new Vector2Int(x, z), out var entries) ? entries : ReadOnlySpan<KeyValuePair<int, ulong>>.Empty;
        public static Bounds GetBounds(BlockFixedGridState state) => new(BlockWorld.Center(state.Cell), Vector3.one * BlockWorld.CellSize);
        public bool Visible(Vector3 from, Vector3 to) => Environment.Visible(from, to);
        public BlockFixedGridTransaction BeginMutation() => new(this);

        public BlockMutationResult Build(IEnumerable<Vector3Int> candidates, byte owner, int maxAdded)
        { using var tx = BeginMutation(); tx.Build(candidates, owner, maxAdded); return Finish(tx); }
        public BlockMutationResult Paint(IEnumerable<BlockSurfaceKey> keys, byte owner, int maxAdded)
        { using var tx = BeginMutation(); tx.Paint(keys, owner, maxAdded); return Finish(tx); }
        public BlockMutationResult Destroy(IEnumerable<BlockFixedGridHandle> handles, byte team, int maxEnemy = int.MaxValue, int maxFriendly = int.MaxValue)
        { using var tx = BeginMutation(); tx.Destroy(handles, team, maxEnemy, maxFriendly); return Finish(tx); }
        public BlockMutationResult ReconcileColumns(IEnumerable<Vector2Int> columns)
        { using var tx = BeginMutation(); tx.ReconcileColumns(columns); return Finish(tx); }
        BlockMutationResult Finish(BlockFixedGridTransaction tx)
        { return tx.TryCommit(out var result, out _) ? result : default; }

        internal bool ValidMapSupport(BlockFixedGridMapSupport support)
            => support.Id != 0 && float.IsFinite(support.Height) && support.BaseCellY == BlockFixedGridMapSupport.GridBaseY(support.Height)
                && support.SurfaceKey.FaceId > 0 && Surfaces.TryGet(support.SurfaceKey, out _);
        internal bool WithinCap(Vector3Int cell, BlockFixedGridMapSupport support)
            => ValidMapSupport(support) && cell.y >= support.BaseCellY && cell.y - (long)support.BaseCellY < MaximumStackLayers
                && (cell.y + 1d) * BlockWorld.CellSize - support.Height <= MaximumStackHeight + Epsilon && Storage.InBounds(cell);
        internal static bool SameSupport(BlockFixedGridMapSupport a, BlockFixedGridMapSupport b)
            => a.Id == b.Id && a.BaseCellY == b.BaseCellY && Math.Abs(a.Height - b.Height) <= Epsilon && a.SurfaceKey == b.SurfaceKey;
        internal static Vector2Int Column(Vector3Int c) => new(c.x, c.z);
        void AddIndex(BlockFixedGridState state)
        {
            States.Add(state.Handle.Id, state); Cells.Add(state.Cell, state.Handle.Id);
            var key = Column(state.Cell);
            if (!Columns.TryGetValue(key, out var column)) Columns.Add(key, column = new SortedDictionary<int, ulong>());
            column.Add(state.Cell.y, state.Handle.Id);
            QueryColumns[key] = column.ToArray();
        }
        void RemoveIndex(BlockFixedGridState state)
        {
            States.Remove(state.Handle.Id); Cells.Remove(state.Cell);
            var key = Column(state.Cell); var column = Columns[key]; column.Remove(state.Cell.y);
            if (column.Count == 0) { Columns.Remove(key); QueryColumns.Remove(key); }
            else QueryColumns[key] = column.ToArray();
        }
        // Only affected identities and columns are copied. Sleeping/remote columns are never scanned.
        sealed class IndexPatch
        {
            internal readonly Dictionary<ulong, BlockFixedGridState> Before = new();
            internal readonly Dictionary<Vector2Int, SortedDictionary<int, ulong>> OldColumns = new(), NewColumns = new();
            readonly Dictionary<Vector2Int, KeyValuePair<int, ulong>[]> _oldQueryColumns = new(), _newQueryColumns = new();
            internal readonly BlockFixedGridState[] After;
            internal IndexPatch(BlockFixedGridWorld world, BlockFixedGridState[] upserts, BlockFixedGridHandle[] removed)
            {
                After = upserts;
                foreach (var handle in removed) if (world.States.TryGetValue(handle.Id, out var old)) Before[handle.Id] = old;
                foreach (var state in upserts) if (world.States.TryGetValue(state.Handle.Id, out var old)) Before[state.Handle.Id] = old;
                var affected = new HashSet<Vector2Int>(Before.Values.Select(s => Column(s.Cell)));
                affected.UnionWith(upserts.Select(s => Column(s.Cell)));
                foreach (var key in affected)
                {
                    world.Columns.TryGetValue(key, out var old); OldColumns.Add(key, old);
                    NewColumns.Add(key, old == null ? new SortedDictionary<int, ulong>() : new SortedDictionary<int, ulong>(old));
                }
                foreach (var old in Before.Values) NewColumns[Column(old.Cell)].Remove(old.Cell.y);
                foreach (var state in upserts) NewColumns[Column(state.Cell)].Add(state.Cell.y, state.Handle.Id);
                foreach (var pair in NewColumns)
                {
                    world.QueryColumns.TryGetValue(pair.Key, out var old); _oldQueryColumns.Add(pair.Key, old);
                    _newQueryColumns.Add(pair.Key, pair.Value.Count == 0 ? null : pair.Value.ToArray());
                }
                world.States.EnsureCapacity(checked(world.States.Count + upserts.Length));
                world.Cells.EnsureCapacity(checked(world.Cells.Count + upserts.Length));
                world.Columns.EnsureCapacity(checked(world.Columns.Count + affected.Count));
                world.QueryColumns.EnsureCapacity(checked(world.QueryColumns.Count + affected.Count));
            }
            internal void Apply(BlockFixedGridWorld world)
            {
                foreach (var old in Before.Values) { world.States.Remove(old.Handle.Id); world.Cells.Remove(old.Cell); }
                foreach (var state in After) { world.States[state.Handle.Id] = state; world.Cells[state.Cell] = state.Handle.Id; }
                InstallColumns(world, NewColumns);
                InstallQueryColumns(world, _newQueryColumns);
            }
            internal void Restore(BlockFixedGridWorld world)
            {
                foreach (var state in After) { world.States.Remove(state.Handle.Id); world.Cells.Remove(state.Cell); }
                foreach (var old in Before.Values) { world.States[old.Handle.Id] = old; world.Cells[old.Cell] = old.Handle.Id; }
                InstallColumns(world, OldColumns);
                InstallQueryColumns(world, _oldQueryColumns);
            }
            static void InstallColumns(BlockFixedGridWorld world, Dictionary<Vector2Int, SortedDictionary<int, ulong>> columns)
            {
                foreach (var pair in columns)
                    if (pair.Value == null || pair.Value.Count == 0) world.Columns.Remove(pair.Key);
                    else world.Columns[pair.Key] = pair.Value;
            }
            static void InstallQueryColumns(BlockFixedGridWorld world, Dictionary<Vector2Int, KeyValuePair<int, ulong>[]> columns)
            {
                foreach (var pair in columns)
                    if (pair.Value == null) world.QueryColumns.Remove(pair.Key);
                    else world.QueryColumns[pair.Key] = pair.Value;
            }
        }
        internal bool Commit(BlockFixedGridTransaction tx, out BlockMutationResult result, out string error)
        {
            using var timing = CommitCpu.Auto();
            result = default; error = null; LastFailure = null;
            if (_committing || tx.Owner != this || tx.SourceEpoch != Epoch || tx.SourceRound != Round || tx.SourceRevision != Revision ||
                tx.SourceSurfaceRevision != Surfaces.Revision || Frozen)
            { error = LastFailure = "Fixed transaction baseline changed, is frozen, or another commit is active."; return false; }
            _committing = true;
            try
            {
                if (!tx.Prepare(out error)) { LastFailure = error; return false; }
                if (!tx.HasChanges)
                {
                    result = tx.Result; result.Revision = Revision;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    tx.PublishObservation(default, false);
#endif
                    return true;
                }
                if (Revision == uint.MaxValue || tx.PaintWrites.Count > uint.MaxValue - Surfaces.Revision)
                { error = LastFailure = "Fixed or surface revision exhausted; no mutation was published."; return false; }
                uint revision = Revision + 1;
                var changed = tx.CellWrites.Keys.OrderBy(c => c.x).ThenBy(c => c.y).ThenBy(c => c.z).ToArray();
                var changedPaint = tx.PaintWrites.Keys.ToArray();
                var preview = new BlockFixedGridCommitPreview(tx, changed);
                BlockWorld nextStorage;
                using (CommitStorageCpu.Auto())
                {
                    nextStorage = preview.Storage;
                    nextStorage.Seal(revision, changed.Select(BlockWorld.ChunkAt).Distinct());
                }
                var upserts = tx.Upserts.Values.OrderBy(s => s.Handle.Id).Select(s => { s.Revision = revision; return s; }).ToArray();
                var removals = tx.Deleted.Values.OrderBy(h => h.Id).ToArray();
                IndexPatch patch;
                using (CommitIndexCpu.Auto()) patch = new IndexPatch(this, upserts, removals);
                var paintBefore = new Dictionary<BlockSurfaceKey, BlockSurfaceCell>(tx.PaintWrites.Count);
                foreach (var key in tx.PaintWrites.Keys)
                {
                    if (!Surfaces.TryGet(key, out var old)) throw new InvalidOperationException("Invalid prevalidated surface key.");
                    paintBefore.Add(key, old);
                }
                Surfaces.EnsureMutationCapacity(tx.PaintWrites.Count);
                EnsureJournalCapacity();
                var entry = new Journal { Revision = revision, Upserts = upserts, Removed = removals };
                var oldStorage = Storage; uint oldRevision = Revision, oldSurfaceRevision = Surfaces.Revision;
                int oldPink = Surfaces.PinkCount, oldBlue = Surfaces.BlueCount, oldRelocated = LastRelocatedCount, oldConverted = LastConvertedCount;
                int oldJournalCount = _journal.Count; ulong oldNextId = _nextId;
                var guard = Environment as IBlockFixedGridCommitGuard;
                using (CommitGuardCpu.Auto())
                    if (guard != null && !guard.TryPrepareCommit(preview, out error))
                    { error = LastFailure = error ?? "Player safety preflight rejected the fixed transaction."; return false; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var actualCounts = tx.ObservationCounts();
#endif
                try
                {
                    patch.Apply(this);
                    foreach (var pair in tx.PaintWrites)
                        if (!Surfaces.TrySet(pair.Key, pair.Value, out _)) throw new InvalidOperationException("A prevalidated surface write failed during fixed commit.");
                    Storage = nextStorage; Revision = revision; _nextId = tx.NextId;
                    LastRelocatedCount = tx.RelocatedCount; LastConvertedCount = tx.ConvertedCount; _journal.Add(entry);
                    // Recovery applies only the pose proved above, before any observer can see a commit.
                    guard?.CommitPreparedRecovery();
                }
                catch
                {
                    Storage = oldStorage; Revision = oldRevision; _nextId = oldNextId;
                    LastRelocatedCount = oldRelocated; LastConvertedCount = oldConverted;
                    if (_journal.Count > oldJournalCount) _journal.RemoveRange(oldJournalCount, _journal.Count - oldJournalCount);
                    Surfaces.RestoreMutation(oldSurfaceRevision, oldPink, oldBlue, paintBefore); patch.Restore(this);
                    if (guard is IBlockFixedGridRecoveryRollback rollback) rollback.RollbackPreparedRecovery();
                    throw;
                }
                result = tx.Result; result.Revision = revision;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                tx.PublishObservation(actualCounts, true);
#endif
                Notify(new BlockFixedGridCommit(Epoch, Round, Revision, Surfaces.Revision, Frozen, changed, result, LastRelocatedCount, LastConvertedCount, changedPaint));
                return true;
            }
            catch (Exception exception) { result = default; error = LastFailure = exception.GetType().Name + ": " + exception.Message; return false; }
            finally { _committing = false; }
        }
        void Notify(BlockFixedGridCommit commit)
        {
            if (Committed == null) return;
            foreach (Action<BlockFixedGridCommit> observer in Committed.GetInvocationList())
                try { observer(commit); }
                catch (Exception exception) { LastObserverException = exception.ToString(); }
        }
        public bool Freeze()
        {
            if (Frozen) return true;
            if (_committing || Revision == uint.MaxValue) return false;
            uint revision = Revision + 1; var next = Storage.Clone(); next.Seal(revision, Array.Empty<Vector3Int>());
            var entry = new Journal { Revision = revision, Upserts = Array.Empty<BlockFixedGridState>(), Removed = Array.Empty<BlockFixedGridHandle>() };
            EnsureJournalCapacity(); _committing = true;
            try
            {
                Frozen = true; Revision = revision; Storage = next; _journal.Add(entry);
                Notify(new BlockFixedGridCommit(Epoch, Round, Revision, Surfaces.Revision, true, Array.Empty<Vector3Int>(), default)); return true;
            }
            finally { _committing = false; }
        }
        public void Clear(uint round)
        {
            if (_committing) throw new InvalidOperationException("Cannot clear inside a fixed-grid commit.");
            if (Epoch == uint.MaxValue || round < Round) throw new InvalidOperationException("Fixed world epoch exhausted or round regressed.");
            var changed = Cells.Keys.ToArray(); var changedPaint = Surfaces.EnumeratePainted().Select(c => c.Key).ToArray();
            var storage = new BlockWorld(Storage.Dimensions, round, Storage.Bottom);
            var surface = Surfaces.Clone(); surface.Clear(round);
            _committing = true;
            try
            {
                Storage = storage; Surfaces = surface;
                States.Clear(); Cells.Clear(); Columns.Clear(); QueryColumns.Clear(); _journal.Clear(); _journalFloor = 0; Epoch++; Revision = 0; _nextId = 1; Frozen = false;
                LastFailure = null; LastRelocatedCount = LastConvertedCount = 0;
                Notify(new BlockFixedGridCommit(Epoch, Round, 0, 0, false, changed, default, changedPaintKeys: changedPaint));
            }
            finally { _committing = false; }
        }
        public void PruneJournal(uint throughRevision)
        {
            if (throughRevision > Revision) throw new ArgumentOutOfRangeException(nameof(throughRevision));
            if (throughRevision <= _journalFloor) return;
            _journal.RemoveAll(entry => entry.Revision <= throughRevision); _journalFloor = throughRevision;
        }
    }
}
