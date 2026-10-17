using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // O(1) FIFO front insertion allows a stale unpublished transaction to
        // retry its original command order without copying the waiting queue.
        sealed class CommandDeque : IEnumerable<Command>
        {
            readonly LinkedList<Command> _items = new();
            public int Count => _items.Count;
            public void Enqueue(Command value) => _items.AddLast(value);
            public void EnqueueFront(Command value) => _items.AddFirst(value);
            public Command Peek() => _items.First.Value;
            public Command Dequeue() { var value = Peek(); _items.RemoveFirst(); return value; }
            public IEnumerator<Command> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        readonly struct ProtectedBuildReplaySpan
        {
            public readonly int BeforeVisited, K, Ring, FirstSide, Pairs;
            public readonly Vector3Int Origin, Varying, FixedAxis;
            public ProtectedBuildReplaySpan(int beforeVisited, Vector3Int origin, Vector3Int varying,
                Vector3Int fixedAxis, int k, int ring, int firstSide, int pairs)
            { BeforeVisited = beforeVisited; Origin = origin; Varying = varying; FixedAxis = fixedAxis;
                K = k; Ring = ring; FirstSide = firstSide; Pairs = pairs; }
        }
        sealed class CommandCheckpoint
        {
            public Command Command;
            public BlockPublicationWindow Window;
            public int TargetIndex;
            public bool Done;
            public UnboundedBlastBatch BlastBatch;
            public int[] BlastTargetIndexes;
            public bool[] BlastDone;
            public readonly LinkedList<Vector3Int> Visited = new();
            public readonly LinkedList<ProtectedBuildReplaySpan> ProtectedBuildSpans = new();
        }
        sealed class AddedBuildSlot
        {
            public Vector3Int Cell;
            public Command Command;
            // Exclusive private-preparation chain; the original Slots list stays intact.
            public AddedBuildSlot NextInColumn;
        }
        LinkedList<CommandCheckpoint> _stageCommandCheckpoints;
        LinkedList<AddedBuildSlot> _stageAddedBuildSlots;
        CommandCheckpoint _stageCommandCheckpoint;
        bool _stageHasMovingBuild;
        HashSet<Vector2Int> _stageMovingBuildColumns;
        sealed class BuildGravityBarrier
        {
            public SortedSet<GravityMotionKey> Heads;
            public IEnumerator<GravityMotionKey> Cursor;
            public GravityMotionId? Buffered;
            public int LegacyIndex;
        }
        Dictionary<Vector2Int, BuildGravityBarrier> _buildGravityBarriers = new();
        public long BuildSourceOrderDeferrals, BuildGravityBarrierVisits;
        public int BuildGravityBarrierColumns => _buildGravityBarriers.Count;
        void ResetBuildGravityBarriers()
        {
            // Enumerators own no native resource. Replacing the dictionary is
            // bounded reset bookkeeping, independent of previous column count.
            _buildGravityBarriers = new Dictionary<Vector2Int, BuildGravityBarrier>();
            _stageMovingBuildColumns = null;
        }
        bool DeferNewBuildCohort(BlockWorld next, Vector2Int column, ref int checks,
            int maximum, Stopwatch timer, double milliseconds)
        {
            // Before actual publication the overlap proof has not classified
            // source ordering yet. Existing descriptors still detach/settle.
            if (_stageMovingBuildColumns != null && _stageMovingBuildColumns.Contains(column)) return true;
            if (!_buildGravityBarriers.TryGetValue(column, out var barrier)) return false;
            // Barrier retirement observes committed owners only. A draft
            // can retire its old owner and still fail publication later;
            // removing the committed barrier then would lose protection.
            _cohortHeads.TryGetValue(column, out var heads);
            if (!ReferenceEquals(heads, barrier.Heads))
            {
                barrier.Cursor?.Dispose(); barrier.Heads = heads;
                barrier.Cursor = heads?.GetEnumerator(); barrier.Buffered = null; barrier.LegacyIndex = 0;
            }
            while (HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!SpendGravity(ref checks, 4, maximum, timer, milliseconds)) return true;
                BuildGravityBarrierVisits++;
                if (!barrier.Buffered.HasValue)
                {
                    if (barrier.Cursor == null || !barrier.Cursor.MoveNext())
                    { barrier.Cursor?.Dispose(); _buildGravityBarriers.Remove(column); return false; }
                    barrier.Buffered = barrier.Cursor.Current.Motion;
                    barrier.LegacyIndex = 0;
                }
                var id = barrier.Buffered.Value;
                if (id.Cohort)
                {
                    if (World.Cohorts.TryGetValue(id.Id, out var group)
                        && (group.MemberIds.Count != 0 || group.Phase == FallingCohortPhase.Detaching)) return true;
                }
                else if (World.Falling.TryGetValue(id.Id, out var run))
                {
                    if (barrier.LegacyIndex < run.Cells.Length)
                    { if (run.Cells[barrier.LegacyIndex].Occupied) return true; barrier.LegacyIndex++; continue; }
                }
                // Phantom metadata does not keep new blocks weightless. This
                // cursor advances under admission until all old bodies retire.
                barrier.Buffered = null;
            }
            return true;
        }
        CommandCheckpoint CheckpointCommand(Command command)
        {
            if (_stageCommandCheckpoint != null && ReferenceEquals(_stageCommandCheckpoint.Command, command)) return _stageCommandCheckpoint;
            var checkpoint = new CommandCheckpoint { Command = command, Window = _stagePublicationWindow, TargetIndex = command.TargetIndex, Done = command.Done };
            (_stageCommandCheckpoints ??= new LinkedList<CommandCheckpoint>()).AddLast(checkpoint);
            _stageCommandCheckpoint = checkpoint; return checkpoint;
        }
        static IEnumerable<Vector3Int> ReplayProtectedBuildSpan(ProtectedBuildReplaySpan span)
        {
            // Each restore MoveNext yields one original cell. The forward proof
            // checked all span arithmetic and capped it at one chunk-local row.
            for (int pair = 0; pair < span.Pairs; pair++)
                for (int side = pair == 0 ? span.FirstSide : 0; side < 2; side++)
                    yield return span.Origin + span.Varying * (span.K + pair) +
                        span.FixedAxis * (side == 0 ? -span.Ring : span.Ring);
        }
        static IEnumerable<Vector3Int> ReplayBuildCandidates(CommandCheckpoint checkpoint, IEnumerator<Vector3Int> remaining)
        {
            var span = checkpoint.ProtectedBuildSpans.First; int visited = 0;
            foreach (var cell in checkpoint.Visited)
            {
                while (span != null && span.Value.BeforeVisited == visited)
                {
                    foreach (var restored in ReplayProtectedBuildSpan(span.Value)) yield return restored;
                    span = span.Next;
                }
                yield return cell; visited++;
            }
            // A budget-deferred last cell was removed from Visited and prepended
            // to remaining. Any prefix before it belongs ahead of that prepend.
            while (span != null)
            {
                foreach (var restored in ReplayProtectedBuildSpan(span.Value)) yield return restored;
                span = span.Next;
            }
            while (remaining.MoveNext()) yield return remaining.Current;
        }
        void RestoreCheckpoint(CommandCheckpoint checkpoint)
        {
            if (RestoreCoalescedBlastCheckpoint(checkpoint)) return;
            var command = checkpoint.Command;
            command.RetryWindow = checkpoint.Window;
            command.TargetIndex = checkpoint.TargetIndex; command.Done = checkpoint.Done;
            if (command.Build && (checkpoint.Visited.Count != 0 || checkpoint.ProtectedBuildSpans.Count != 0))
                command.Cells = ReplayBuildCandidates(checkpoint, command.Cells).GetEnumerator();
            if (_commands.Count == 0 || !ReferenceEquals(_commands.Peek(), command)) _commands.EnqueueFront(command);
        }

        // Frozen query data has no reference back to a source or private draft.
        sealed class BuildMotionQuery
        {
            public readonly LinkedList<FrozenCohortMotion> Nodes;
            public bool Evaluated;
            public BuildMotionQuery(LinkedList<FrozenCohortMotion> nodes) { Nodes = nodes; }
            public double EvaluatedAt;
            public float Offset, Speed;
            public void Evaluate(double now)
            {
                if (Evaluated && EvaluatedAt == now) return;
                Offset = Speed = 0; bool hasLower = false;
                for (var node = Nodes.Last; node != null; node = node.Previous)
                {
                    FrozenCohortMotion.EvaluateNode(node.Value, now, hasLower, Offset, Speed, out Offset, out Speed);
                    hasLower = true;
                }
                Evaluated = true; EvaluatedAt = now;
            }
        }
        sealed class BuildMotionSample
        {
            public readonly LinkedList<FrozenCohortMotion> Nodes = new();
            public readonly BuildMotionQuery Query;
            public FallingCohort Cursor;
            public FrozenCohortMotion[] Retained;
            public int RetainedCursor = -1;
            public bool Ready, Evaluated;
            public double EvaluatedAt;
            public float Offset, Speed;
            public BuildMotionSample() { Query = new BuildMotionQuery(Nodes); }
            public void Evaluate(double now)
            {
                Query.Evaluate(now);
                Offset = Query.Offset; Speed = Query.Speed;
                Evaluated = Query.Evaluated; EvaluatedAt = Query.EvaluatedAt;
            }
        }
        readonly struct BuildQueryCell
        {
            public readonly Vector3Int SourceCell;
            public readonly BuildMotionQuery Sample;
            public BuildQueryCell(Vector3Int cell, BuildMotionQuery sample) { SourceCell = cell; Sample = sample; }
        }
        sealed class BuildColumnQuery
        {
            public int Minimum, Length, QueryWork;
            public bool[] Moving;
            public double MovingAt = double.NaN;
            public readonly LinkedList<BuildMotionQuery> Motions = new();
            public readonly LinkedList<BuildQueryCell> Cells = new();
        }
        sealed class BuildSourceQueryCache
        {
            public BlockWorld Source;
            public uint Epoch, Revision, Round;
            public ulong Mutation;
            public readonly Dictionary<Vector2Int, BuildColumnQuery> Columns = new();
            public bool SourceValid(BlockTerrain terrain)
                => Epoch == terrain._epoch && ReferenceEquals(Source, terrain.World) && Revision == terrain.Revision
                    && Round == terrain.Round && Mutation == Source.MutationGeneration && terrain._remoteQueued == null;
        }
        sealed class BuildSourceMotion
        {
            public GravityMotionId Id;
            public BuildMotionSample Sample;
            public FallingCohort Group;
            public FallingRun Legacy;
            public int Member, Cell;
            public FallingRun Run;
        }
        sealed class BuildOccupiedCell
        {
            public Vector3Int SourceCell;
            public ulong SourceRun;
            public GravityMotionId SourceMotion;
            public int SourceIndex;
            public BuildMotionSample Sample;
            public FallingRun PreparedRun;
        }
        sealed class BuildColumnProof
        {
            public readonly BuildColumnQuery Query = new();
            public Vector2Int Column;
            public int Minimum, Maximum, Length, PrefixCursor;
            public AddedBuildSlot AddedFirst, AddedLast, AddedCursor;
            public bool[] Added;
            public bool[] Moving;
            public int[] AddedBelow;
            public double MovingAt = double.NaN;
            public IEnumerator<GravityMotionKey> Heads;
            public BuildSourceMotion Motion;
            public readonly Dictionary<GravityMotionId, BuildSourceMotion> Motions = new();
            public readonly LinkedList<BuildOccupiedCell> Cells = new();
            public LinkedListNode<BuildOccupiedCell> MapCursor;
            public bool HeadsComplete, MappingComplete;
            public int QueryWork;
            public bool SourceOrderInversion;
        }
        sealed class BuildLandingCap
        {
            public FallingCohort Group;
            public FallingRun Legacy;
            public BuildMotionSample Sample;
            public float Cap, PublicationOffset, PublicationCap;
            public bool PublicationApplies;
        }
        sealed class BuildOccupancyPreparation
        {
            public BlockPublicationWindow Window;
            public BlockWorld Source, Draft, Next;
            public uint Epoch, Revision, Round;
            public ulong Mutation;
            public BlockWorldCloneBuilder Clone;
            public bool ZeroPreparedMetadata;
            public BuildMetadataOwnership MetadataOwnership;
            public GravityJournal Journal;
            public HashSet<Vector3Int> Changed;
            public List<(Command, BlockMutationResult)> Results;
            public LinkedList<CommandCheckpoint> Checkpoints;
            public LinkedList<AddedBuildSlot> Slots;
            public LinkedListNode<AddedBuildSlot> SlotCursor;
            public readonly Dictionary<Vector2Int, BuildColumnProof> Columns = new();
            public readonly LinkedList<BuildColumnProof> ColumnOrder = new();
            public LinkedListNode<BuildColumnProof> ColumnCursor;
            public readonly Dictionary<GravityMotionId, BuildLandingCap> Caps = new();
            public int Phase;
            public bool Ready, Retry, RepairAfterRetry;
            public BuildOccupiedCell OverlapCell;
            public Vector2Int OverlapColumn;
            public Vector3Int OverlapDestination;
            public LinkedListNode<CommandCheckpoint> RetryCursor;
            public bool SourceValid(BlockTerrain terrain)
                => Epoch == terrain._epoch && ReferenceEquals(Source, terrain.World) && Revision == terrain.Revision
                    && Round == terrain.Round && Mutation == Source.MutationGeneration && terrain._remoteQueued == null;
        }
        BuildOccupancyPreparation _buildOccupancyPreparation;
        BuildOccupancyPreparation _lastBuildOccupancyProof;
        BuildSourceQueryCache _buildSourceQueryCache;
        // Positive evidence only: a source cell must still overlap at the CURRENT
        // time. Never retain a time-independent occupied-address fact.
        sealed class BuildOverlapWitness
        {
            public Vector3Int SourceCell;
            public BuildMotionQuery Sample;
            public BuildColumnQuery Column;
            public LinkedListNode<Vector3Int> Order;
        }
        const int MaxCachedBuildOverlapWitnesses = 4096;
        Dictionary<Vector3Int, BuildOverlapWitness> _buildOverlapWitnesses = new();
        LinkedList<Vector3Int> _buildOverlapWitnessOrder = new();
        bool HasBuildOccupancyPreparation => _buildOccupancyPreparation != null;
        public long BuildOccupancyPreparationStarts, BuildOccupancyPreparationStages, BuildOccupancyPreparationWork;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Observation only: one sample per existing preparation start, with no
        // metadata scan. These lifetime counters do not select another path.
        long _buildPreparationObservedStarts, _buildPreparationZeroPreparedStarts;
        static readonly Unity.Profiling.ProfilerMarker BuildOwnedCloneCpu = new("Splatoon.Blocks.BuildPreparation.OwnedClone");
#endif
        public long BuildMovingOccupied, BuildPublicationRetries;
        public ulong BuildOccupancyProgressVersion { get; private set; }
        public long LastBuildPublicationWork;

        void ResetBuildOccupancyQueryCache()
        {
            // Drop the old binding and all ready columns in fixed bookkeeping.
            _buildSourceQueryCache = null;
            _buildOverlapWitnesses = new Dictionary<Vector3Int, BuildOverlapWitness>();
            _buildOverlapWitnessOrder = new LinkedList<Vector3Int>();
        }
        bool CacheCompletedBuildColumn(BuildOccupancyPreparation preparation, BuildColumnProof column,
            ref int checks, int maximum, Stopwatch timer, double milliseconds)
        {
            if (!SpendBuildPreparation(ref checks, 4, maximum, timer, milliseconds)) return false;
            if (_buildSourceQueryCache == null || !_buildSourceQueryCache.SourceValid(this))
            {
                ResetBuildOccupancyQueryCache();
                _buildSourceQueryCache = new BuildSourceQueryCache { Source = preparation.Source, Epoch = preparation.Epoch,
                    Revision = preparation.Revision, Round = preparation.Round, Mutation = preparation.Mutation };
            }
            if (!_buildSourceQueryCache.Columns.ContainsKey(column.Column))
            {
                var query = column.Query;
                query.Minimum = column.Minimum; query.Length = column.Length;
                query.Moving = column.Moving; query.QueryWork = column.QueryWork;
                _buildSourceQueryCache.Columns.Add(column.Column, query);
            }
            return true;
        }
        void RemoveBuildOverlapWitness(Vector3Int cell)
        {
            if (!_buildOverlapWitnesses.TryGetValue(cell, out var witness)) return;
            _buildOverlapWitnesses.Remove(cell); _buildOverlapWitnessOrder.Remove(witness.Order);
        }
        void RecordBuildOverlapWitness(BuildColumnProof column, BuildOccupiedCell source, Vector3Int cell)
        {
            var cache = _buildSourceQueryCache;
            if (cache == null || !cache.SourceValid(this)
                || !cache.Columns.TryGetValue(column.Column, out var cachedColumn)) return;
            // These nodes were frozen by the admitted complete source proof.
            // Keep its bounded graph verbatim; no live owner/draft reference.
            var sample = source.Sample.Query;
            if (sample.Nodes.Count == 0 || sample.Nodes.Count > BlockWorld.MaxCohortCells) return;
            if (!_buildOverlapWitnesses.TryGetValue(cell, out var witness))
            {
                if (_buildOverlapWitnesses.Count == MaxCachedBuildOverlapWitnesses)
                    RemoveBuildOverlapWitness(_buildOverlapWitnessOrder.First.Value);
                witness = new BuildOverlapWitness { Order = _buildOverlapWitnessOrder.AddLast(cell) };
                _buildOverlapWitnesses.Add(cell, witness);
            }
            witness.SourceCell = source.SourceCell; witness.Sample = sample; witness.Column = cachedColumn;
        }
        bool TryBuildOverlapWitness(Vector3Int cell, BuildColumnQuery column, ref int checks,
            int maximum, Stopwatch timer, double milliseconds, out bool occupied)
        {
            occupied = false;
            if (!_buildOverlapWitnesses.TryGetValue(cell, out var witness)) return true;
            int nodes = witness.Sample.Nodes.Count;
            if (!ReferenceEquals(witness.Column, column) || nodes == 0 || nodes > BlockWorld.MaxCohortCells)
            { RemoveBuildOverlapWitness(cell); return true; }
            // Charge the COMPLETE chain and geometry even if Evaluate already
            // has a same-time memo. Never borrow an oversized inline allowance.
            int cost = nodes + 4;
            if (checks + cost > maximum || !HasStageBudget(checks, maximum, timer, milliseconds)) return false;
            checks += cost; CommandWorkChecks += cost;
            witness.Sample.Evaluate(_now);
            float bottom = witness.SourceCell.y * BlockWorld.CellSize - witness.Sample.Offset;
            int first = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize);
            int last = Mathf.CeilToInt((bottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
            occupied = cell.y >= first && cell.y <= last;
            if (!occupied) RemoveBuildOverlapWitness(cell);
            return true;
        }
        bool RequiresBuildOccupancyGuard()
        {
            return _stageHasMovingBuild;
        }
        void RecordAddedBuildSlot(Command command, Vector3Int cell)
        {
            (_stageAddedBuildSlots ??= new LinkedList<AddedBuildSlot>()).AddLast(new AddedBuildSlot { Cell = cell, Command = command });
            var column = new Vector2Int(cell.x, cell.z);
            if (_cohortHeads.TryGetValue(column, out var heads) && heads.Count != 0)
            { _stageHasMovingBuild = true; (_stageMovingBuildColumns ??= new HashSet<Vector2Int>()).Add(column); }
        }
        bool TryCurrentBuildOverlap(Vector3Int cell, ref int checks, int maximum, Stopwatch timer, double milliseconds, out bool occupied)
        {
            occupied = false;
            var cache = _buildSourceQueryCache;
            if (cache == null) return true;
            if (!cache.SourceValid(this)) { ResetBuildOccupancyQueryCache(); return true; }
            if (!cache.Columns.TryGetValue(new Vector2Int(cell.x, cell.z), out var column))
            { RemoveBuildOverlapWitness(cell); return true; }
            if (!TryBuildOverlapWitness(cell, column, ref checks, maximum, timer, milliseconds, out occupied)) return false;
            if (occupied) return true;
            // A completed narrow mask proves only its own range. Positive
            // witnesses above already rechecked current motion, including
            // outside this mask; other outside cells keep the full guard path.
            if (cell.y < column.Minimum || cell.y >= column.Minimum + column.Length) return true;
            if (column.MovingAt == _now) { occupied = column.Moving[cell.y - column.Minimum]; return true; }
            // A large motion graph goes through the finite private/publication
            // proof path. Never submit it as an oversized StageAuthority action.
            if (column.QueryWork > maximum) return true;
            if (checks + column.QueryWork > maximum || !HasStageBudget(checks, maximum, timer, milliseconds)) return false;
            checks += column.QueryWork; CommandWorkChecks += column.QueryWork;
            Array.Clear(column.Moving, 0, column.Moving.Length);
            foreach (var motion in column.Motions) motion.Evaluate(_now);
            foreach (var source in column.Cells)
            {
                float movingBottom = source.SourceCell.y * BlockWorld.CellSize - source.Sample.Offset;
                int first = Mathf.FloorToInt((movingBottom + .000001f) / BlockWorld.CellSize);
                int last = Mathf.CeilToInt((movingBottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
                for (int y = first; y <= last; y++)
                    if (y >= column.Minimum && y < column.Minimum + column.Length) column.Moving[y - column.Minimum] = true;
            }
            column.MovingAt = _now; occupied = column.Moving[cell.y - column.Minimum];
            return true;
        }
        void BeginBuildOccupancyPreparation(BlockWorld next, HashSet<Vector3Int> changed, List<(Command, BlockMutationResult)> results, BlockPublicationWindow window)
        {
            // Decide before constructing the full-owned clone. Prepared activation
            // keeps the original path and native-proof migration unchanged.
            bool zeroPrepared = next.EstimatedPreparedPublicationWork == 0;
            _buildOccupancyPreparation = new BuildOccupancyPreparation
            {
                Source = World, Draft = next, Window = window, Epoch = _epoch, Revision = Revision, Round = Round, Mutation = World.MutationGeneration,
                Clone = zeroPrepared ? null : next.BeginBudgetedOwnedClone(),
                Next = zeroPrepared ? next : null, Phase = zeroPrepared ? 1 : 0,
                ZeroPreparedMetadata = zeroPrepared,
                MetadataOwnership = zeroPrepared ? new BuildMetadataOwnership(next) : null,
                Journal = _gravityJournal, Changed = changed, Results = results,
                Checkpoints = _stageCommandCheckpoints, Slots = _stageAddedBuildSlots, SlotCursor = _stageAddedBuildSlots.First
            };
            _gravityJournal = null; _stageCommandCheckpoints = null; _stageAddedBuildSlots = null; _stageCommandCheckpoint = null;
            _stageHasMovingBuild = false; _stageMovingBuildColumns = null;
            BuildOccupancyPreparationStarts++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _buildPreparationObservedStarts++;
            if (next.EstimatedPreparedPublicationWork == 0) _buildPreparationZeroPreparedStarts++;
#endif
        }
        void AbortBuildOccupancyPreparation()
        {
            var preparation = _buildOccupancyPreparation; _buildOccupancyPreparation = null;
            if (preparation == null) return;
            preparation.Clone?.Dispose();
            preparation.MetadataOwnership?.Dispose();
            foreach (var column in preparation.ColumnOrder) column.Heads?.Dispose();
            // Reset/authority-loss cancellation restores cursors before the caller
            // performs its existing queue completion or remote replacement logic.
            if (preparation.Checkpoints != null)
                for (var node = preparation.Retry ? preparation.RetryCursor : preparation.Checkpoints.Last; node != null; node = node.Previous) RestoreCheckpoint(node.Value);
        }
        bool SpendBuildPreparation(ref int checks, int cost, int maximum, Stopwatch timer, double milliseconds)
        {
            if (!SpendGravity(ref checks, cost, maximum, timer, milliseconds)) return false;
            BuildOccupancyPreparationWork += cost; BuildOccupancyProgressVersion++; return true;
        }
        bool AdvanceBuildMotion(BuildOccupancyPreparation preparation, BuildColumnProof column,
            ref int checks, int maximum, Stopwatch timer, double milliseconds)
        {
            var motion = column.Motion;
            while (!motion.Sample.Ready && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!SpendBuildPreparation(ref checks, 2, maximum, timer, milliseconds)) return false;
                if (motion.Sample.Cursor != null)
                {
                    var source = motion.Sample.Cursor;
                    float clearance = source.LowerSupport != null ? source.SupportClearance : source.RetainedSupportClearance;
                    motion.Sample.Nodes.AddLast(new FrozenCohortMotion(source.Phase, source.StartedAt, source.InitialOffset,
                        source.InitialSpeed, source.LandingKnown, source.LandingOffset, clearance));
                    motion.Sample.Cursor = source.LowerSupport;
                    if (source.LowerSupport == null) { motion.Sample.Retained = source.RetainedSupportMotion; motion.Sample.RetainedCursor = source.RetainedSupportMotion.Length - 1; }
                }
                else if (motion.Sample.RetainedCursor >= 0)
                    motion.Sample.Nodes.AddLast(motion.Sample.Retained[motion.Sample.RetainedCursor--]);
                else motion.Sample.Ready = true;
                if (motion.Sample.Nodes.Count > BlockWorld.MaxCohortCells) throw new InvalidOperationException("Build occupancy encountered an invalid source motion depth.");
            }
            if (!motion.Sample.Ready) return false;
            while (HasStageBudget(checks, maximum, timer, milliseconds))
            {
                // The additional query cell record is built under this same finite scan.
                if (!SpendBuildPreparation(ref checks, 4, maximum, timer, milliseconds)) return false;
                if (motion.Run == null)
                {
                    if (motion.Group == null)
                    {
                        if (motion.Member != 0) { column.QueryWork += motion.Sample.Nodes.Count; column.Motion = null; return true; }
                        motion.Run = motion.Legacy; motion.Member++;
                    }
                    else
                    {
                        if (motion.Member == motion.Group.MemberIds.Count) { column.QueryWork += motion.Sample.Nodes.Count; column.Motion = null; return true; }
                        preparation.Source.Falling.TryGetValue(motion.Group.MemberIds[motion.Member++], out motion.Run);
                        if (motion.Run == null) continue;
                    }
                    motion.Cell = 0;
                }
                if (motion.Cell == motion.Run.Cells.Length) { motion.Run = null; continue; }
                int index = motion.Cell++;
                if (!motion.Run.Cells[index].Occupied) continue;
                var sourceCell = motion.Run.Bottom + Vector3Int.up * index;
                column.Cells.AddLast(new BuildOccupiedCell { SourceCell = sourceCell,
                    SourceRun = motion.Run.Id, SourceMotion = motion.Id, SourceIndex = index, Sample = motion.Sample });
                column.Query.Cells.AddLast(new BuildQueryCell(sourceCell, motion.Sample.Query));
                column.QueryWork += 4;
                if (column.Cells.Count > BlockWorld.MaxCohortCells) throw new InvalidOperationException("Build occupancy source column contains duplicate or out-of-range occupied cells.");
            }
            return false;
        }
        bool AdvanceBuildColumn(BuildOccupancyPreparation preparation, BuildColumnProof column,
            ref int checks, int maximum, Stopwatch timer, double milliseconds)
        {
            // Phase 1 has collected EVERY added slot before any column enters
            // phase 2. Admit the two actual range arrays once; no world-size
            // allocation or hidden whole-column initialization is performed.
            if (column.Added == null)
            {
                int length = checked(column.Maximum - column.Minimum + 1);
                if (length <= 0 || length > BlockWorld.MaxCohortCells)
                    throw new InvalidOperationException("Build added range exceeds the source-column contract.");
                if (!SpendBuildPreparation(ref checks, checked(2 * length + 8), maximum, timer, milliseconds)) return false;
                column.Length = length;
                column.Added = new bool[length]; column.AddedBelow = new int[length];
                column.AddedCursor = column.AddedFirst;
            }
            while (column.AddedCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!SpendBuildPreparation(ref checks, 4, maximum, timer, milliseconds)) return false;
                int index = column.AddedCursor.Cell.y - column.Minimum;
                if ((uint)index >= (uint)column.Length)
                    throw new InvalidOperationException("A frozen build slot left its collected added range.");
                column.Added[index] = true; column.AddedCursor = column.AddedCursor.NextInColumn;
            }
            if (column.AddedCursor != null) return false;
            column.AddedFirst = column.AddedLast = null;
            if (column.Moving == null)
            {
                if (!SpendBuildPreparation(ref checks, column.Length, maximum, timer, milliseconds)) return false;
                column.Moving = new bool[column.Length]; column.QueryWork += column.Length;
            }
            while (!column.HeadsComplete && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (column.Motion != null)
                { if (!AdvanceBuildMotion(preparation, column, ref checks, maximum, timer, milliseconds)) return false; continue; }
                if (!SpendBuildPreparation(ref checks, 18, maximum, timer, milliseconds)) return false;
                if (column.Heads == null || !column.Heads.MoveNext())
                { column.Heads?.Dispose(); column.Heads = null; column.HeadsComplete = true; column.MapCursor = column.Cells.First; break; }
                var key = column.Heads.Current;
                if (column.Motions.ContainsKey(key.Motion)) continue;
                var motion = new BuildSourceMotion { Id = key.Motion, Sample = new BuildMotionSample() };
                if (key.Motion.Cohort)
                {
                    if (!preparation.Source.Cohorts.TryGetValue(key.Motion.Id, out motion.Group) || motion.Group.MemberIds.Count == 0) continue;
                    motion.Sample.Cursor = motion.Group;
                }
                else
                {
                    if (!preparation.Source.Falling.TryGetValue(key.Motion.Id, out motion.Legacy)) continue;
                    motion.Sample.Nodes.AddLast(new FrozenCohortMotion(FallingCohortPhase.Falling, motion.Legacy.StartedAt,
                        motion.Legacy.InitialOffset, motion.Legacy.InitialSpeed, motion.Legacy.LandingKnown, motion.Legacy.LandingOffset, 0));
                    motion.Sample.Ready = true;
                }
                column.Motions.Add(key.Motion, motion); column.Query.Motions.AddLast(motion.Sample.Query); column.Motion = motion;
            }
            if (!column.HeadsComplete) return false;
            while (column.MapCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                // Alias traversal remains bounded by the existing lookup contract.
                if (!SpendBuildPreparation(ref checks, BlockWorld.MaxCohortPieceCells + 18, maximum, timer, milliseconds)) return false;
                var cell = column.MapCursor.Value;
                preparation.Next.TryGetFallingCell(cell.SourceRun, cell.SourceIndex, out cell.PreparedRun, out _);
                if (cell.PreparedRun != null)
                {
                    var group = cell.PreparedRun.Cohort;
                    var id = new GravityMotionId(group == null ? cell.PreparedRun.Id : group.Id, group != null);
                    if (!preparation.Caps.ContainsKey(id))
                    {
                        preparation.Caps.Add(id, new BuildLandingCap { Group = group, Legacy = group == null ? cell.PreparedRun : null,
                            Sample = cell.Sample, Cap = float.PositiveInfinity });
                        if (_cohortMotionWork.TryGetValue(id, out var committed))
                            preparation.Journal.Motions[id] = new GravityMotionWork { Column = committed.Column,
                                SourceBottom = committed.SourceBottom, SourceTop = committed.SourceTop };
                    }
                }
                column.MapCursor = column.MapCursor.Next;
            }
            if (column.MapCursor != null) return false;
            column.MappingComplete = true;
            while (column.PrefixCursor < column.Length && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!SpendBuildPreparation(ref checks, 1, maximum, timer, milliseconds)) return false;
                int index = column.PrefixCursor++;
                bool stableAdded = column.Added[index] && preparation.Next.TryGet(new Vector3Int(column.Column.x, column.Minimum + index, column.Column.y), out _);
                column.AddedBelow[index] = stableAdded ? index : index == 0 ? -1 : column.AddedBelow[index - 1];
            }
            return column.PrefixCursor == column.Length;
        }
        bool AdvanceBuildOccupancyPreparation()
        {
            var preparation = _buildOccupancyPreparation;
            if (preparation == null) return false;
            bool commandWork = _commands.Count != 0;
            int maximum = commandWork ? BlockPublicationPolicy.GravityChecksWithCommands : BlockPublicationPolicy.GravityChecksAlone, checks = 0;
            double milliseconds = commandWork ? BlockPublicationPolicy.GravityMillisecondsWithCommands : BlockPublicationPolicy.GravityMillisecondsAlone;
            var timer = Stopwatch.StartNew(); long before = GravityWorkChecks;
            AuthorityStageCount++; BuildOccupancyPreparationStages++; LastCommandStageMilliseconds = 0;
            try
            {
                if (preparation.Retry)
                {
                    while (preparation.RetryCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                    {
                        if (!SpendBuildPreparation(ref checks, 16, maximum, timer, milliseconds)) return true;
                        RestoreCheckpoint(preparation.RetryCursor.Value); preparation.RetryCursor = preparation.RetryCursor.Previous;
                    }
                    if (preparation.RetryCursor != null) return true;
                    _gravityRetryPublicationWindow = preparation.Window;
                    _buildOccupancyPreparation = null;
                    ArmBuildOverlapRepair(preparation); return true;
                }
                if (!preparation.SourceValid(this)) { AbortBuildOccupancyPreparation(); return true; }
                if (preparation.Phase == 0)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    // Includes existing admission, clone steps, TakeWorld and
                    // disposal; it does not measure exclusive clone CPU time.
                    using var observation = BuildOwnedCloneCpu.Auto();
#endif
                    while (!preparation.Clone.Ready && HasStageBudget(checks, maximum, timer, milliseconds))
                    {
                        int cost = preparation.Clone.EstimateNextStepWork();
                        if (!SpendBuildPreparation(ref checks, cost, maximum, timer, milliseconds)) return true;
                        ulong version = preparation.Clone.ProgressVersion;
                        preparation.Clone.Step(preparation.Draft, cost, out int actual);
                        if (actual > cost || preparation.Clone.Failed || preparation.Clone.ProgressVersion == version)
                            throw new InvalidOperationException("Owned build publication clone failed its admitted step.");
                    }
                    if (!preparation.Clone.Ready) return true;
                    if (!SpendBuildPreparation(ref checks, 16, maximum, timer, milliseconds)) return true;
                    preparation.Next = TakeOwnedBuildGravityClone(preparation);
                    preparation.Clone.Dispose(); preparation.Clone = null; preparation.Phase = 1;
                }
                while (preparation.Phase == 1 && preparation.SlotCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    var slot = preparation.SlotCursor.Value; var at = new Vector2Int(slot.Cell.x, slot.Cell.z);
                    // Keep the original maximum-400 source contract and reject
                    // any invalid frozen slot before even the empty-head path.
                    if (!SpendBuildPreparation(ref checks, 6, maximum, timer, milliseconds)) return true;
                    int worldMinimum = Mathf.CeilToInt(preparation.Source.Bottom / BlockWorld.CellSize);
                    int worldTop = Mathf.CeilToInt(BlockWorld.Top / BlockWorld.CellSize);
                    int worldLength = worldTop - worldMinimum;
                    if (worldLength <= 0 || worldLength > BlockWorld.MaxCohortCells
                        || slot.Cell.y < worldMinimum || slot.Cell.y >= worldTop || !preparation.Next.InBounds(slot.Cell))
                        throw new InvalidOperationException("Build slot or source bounds exceed the source-column contract.");
                    bool found = preparation.Columns.TryGetValue(at, out var column);
                    // The complete locked source has no moving body in a column
                    // without heads. Keep the slot/result, but omit its empty
                    // motion mask and landing-cap proof. Other columns stay intact.
                    if (!found && !_cohortIndexPending
                        && (!_cohortHeads.TryGetValue(at, out var sourceHeads) || sourceHeads.Count == 0))
                    {
                        if (!SpendBuildPreparation(ref checks, 2, maximum, timer, milliseconds)) return true;
                        preparation.SlotCursor = preparation.SlotCursor.Next;
                        continue;
                    }
                    // Explicit fixed metadata/chain work remains charged. Array
                    // elements are charged later at their collected real length.
                    if (!SpendBuildPreparation(ref checks, found ? 8 : 24, maximum, timer, milliseconds)) return true;
                    if (!found)
                    {
                        column = new BuildColumnProof { Column = at, Minimum = slot.Cell.y, Maximum = slot.Cell.y,
                            Heads = _cohortHeads.TryGetValue(at, out var heads) ? heads.GetEnumerator() : null };
                        preparation.Columns.Add(at, column); preparation.ColumnOrder.AddLast(column);
                    }
                    column.Minimum = Math.Min(column.Minimum, slot.Cell.y);
                    column.Maximum = Math.Max(column.Maximum, slot.Cell.y);
                    slot.NextInColumn = null;
                    if (column.AddedLast == null) column.AddedFirst = slot;
                    else column.AddedLast.NextInColumn = slot;
                    column.AddedLast = slot;
                    preparation.SlotCursor = preparation.SlotCursor.Next;
                }
                if (preparation.Phase == 1 && preparation.SlotCursor == null)
                { preparation.ColumnCursor = preparation.ColumnOrder.First; preparation.Phase = 2; }
                while (preparation.ColumnCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    var column = preparation.ColumnCursor.Value;
                    if (!AdvanceBuildColumn(preparation, column, ref checks, maximum, timer, milliseconds)) return true;
                    // Normal build phase 2 alone installs completed source query data.
                    if (!CacheCompletedBuildColumn(preparation, column, ref checks, maximum, timer, milliseconds)) return true;
                    preparation.ColumnCursor = preparation.ColumnCursor.Next;
                }
                if (preparation.ColumnCursor != null || preparation.Phase != 2) return true;
                if (preparation.ZeroPreparedMetadata
                    && !AdvanceBuildMetadataOwnership(preparation, ref checks, maximum, timer, milliseconds)) return true;
                if (!SpendBuildPreparation(ref checks, 16, maximum, timer, milliseconds)) return true;
                preparation.Ready = true; _buildOccupancyPreparation = null;
                // A later proof for the SAME source must not erase learned
                // positive witnesses; source/reset/publication still clears them.
                _lastBuildOccupancyProof = preparation;
                _gravityJournal = preparation.Journal;
                BeginWindowTransaction(preparation.Next, preparation.Changed, false, preparation.Results, preparation.Window);
                _pending.BuildGuard = preparation; _gravityJournal = null; return true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[BLOCK] Build occupancy preparation rejected: " + exception.Message);
                FailedTransactions++; AbortBuildOccupancyPreparation(); return true;
            }
            finally
            {
                _gravityJournal = null; LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (GravityWorkChecks != before) GravityProgressStages++;
            }
        }
        bool ValidateBuildPublication(Pending pending)
        {
            var preparation = pending.BuildGuard; LastBuildPublicationWork = 0;
            if (preparation == null) return true;
            if (!preparation.SourceValid(this)) { pending.AbortReason = PendingAbortReason.BuildSourceChanged; return false; }
            foreach (var cap in preparation.Caps.Values) cap.Cap = float.PositiveInfinity;
            foreach (var column in preparation.ColumnOrder)
            {
                column.SourceOrderInversion = false;
                foreach (var motion in column.Motions.Values)
                { motion.Sample.Evaluate(_now); LastBuildPublicationWork += motion.Sample.Nodes.Count; }
                foreach (var cell in column.Cells)
                {
                    float bottom = cell.SourceCell.y * BlockWorld.CellSize - cell.Sample.Offset;
                    int first = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize);
                    int last = Mathf.CeilToInt((bottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
                    LastBuildPublicationWork += 4;
                    for (int y = first; y <= last; y++)
                        if (y >= column.Minimum && y < column.Minimum + column.Length && column.Added[y - column.Minimum])
                        {
                            pending.AbortReason = PendingAbortReason.BuildMovingOverlap;
                            preparation.OverlapCell = cell; preparation.OverlapColumn = column.Column;
                            preparation.OverlapDestination = new Vector3Int(column.Column.x, y, column.Column.y);
                            RecordBuildOverlapWitness(column, cell, new Vector3Int(column.Column.x, y, column.Column.y));
                            BuildMovingOccupied++; return false;
                        }
                    if (cell.PreparedRun == null) continue;
                    // A new stable body physically above an existing survivor
                    // may be lower in its old source-address ordering. Do not
                    // turn it into a fictitious lower support that raises the
                    // survivor. Only such columns delay NEW cohort creation.
                    int sourceIndex = Math.Min(cell.SourceCell.y - column.Minimum, column.Length - 1);
                    if (sourceIndex >= 0)
                    {
                        int above = column.AddedBelow[sourceIndex];
                        if (above >= 0 && (column.Minimum + above) * BlockWorld.CellSize >= bottom + BlockWorld.CellSize - .000001f)
                            column.SourceOrderInversion = true;
                    }
                    int below = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize) - 1 - column.Minimum;
                    if (below < 0) continue;
                    int support = column.AddedBelow[Math.Min(below, column.Length - 1)];
                    if (support < 0) continue;
                    var group = cell.PreparedRun.Cohort;
                    var id = new GravityMotionId(group == null ? cell.PreparedRun.Id : group.Id, group != null);
                    preparation.Caps[id].Cap = Mathf.Min(preparation.Caps[id].Cap,
                        (cell.SourceCell.y - (column.Minimum + support + 1)) * BlockWorld.CellSize);
                }
            }
            return true;
        }
        bool BuildPublicationCapApplies(Pending pending, BuildLandingCap cap)
        {
            if (float.IsPositiveInfinity(cap.Cap)) return false;
            bool known = cap.Group != null ? cap.Group.LandingKnown : cap.Legacy.LandingKnown;
            // Use the same predicted activation cap as final support selection.
            float oldCap = cap.Group != null ? PublicationOwnLandingCap(pending, cap.Group) : cap.Legacy.LandingOffset;
            return !known || oldCap > cap.Cap;
        }
        void ApplyBuildPublicationCaps(Pending pending)
        {
            var preparation = pending.BuildGuard; if (preparation == null) return;
            foreach (var column in preparation.ColumnOrder)
                if (column.SourceOrderInversion)
                { pending.Gravity.NewBuildGravityBarriers.Add(column.Column); LastBuildPublicationWork += 2; }
            foreach (var pair in preparation.Caps)
            {
                var cap = pair.Value;
                if (!cap.PublicationApplies) continue;
                if (cap.Cap + .000001f < cap.Sample.Offset) throw new InvalidOperationException("An accepted build floor cannot move an existing survivor upward.");
                if (cap.Group != null)
                {
                    cap.Group.StartedAt = _now; cap.Group.InitialOffset = cap.PublicationOffset; cap.Group.InitialSpeed = cap.Sample.Speed;
                    cap.Group.LandingKnown = true; cap.Group.LandingOffset = cap.Cap;
                    // Retained future constraints remain explicitly awaiting
                    // their normal complete support proof. The new nearer floor
                    // cap is immediate; this flag also preserves codec invariants.
                    cap.Group.NeedsLandingProof = cap.Group.RetainedSupportMotion.Length != 0;
                }
                else
                {
                    cap.Legacy.StartedAt = _now; cap.Legacy.InitialOffset = cap.PublicationOffset; cap.Legacy.InitialSpeed = cap.Sample.Speed;
                    cap.Legacy.LandingKnown = true; cap.Legacy.LandingOffset = cap.Cap;
                }
                if (pending.Gravity.Motions.TryGetValue(pair.Key, out var work) && work != null)
                {
                    // A build cap is not a fresh complete native/stable proof.
                    // Settling already published part of the original span; keep
                    // its existing continuation instead of rescanning that prefix.
                    work.ProofKnown = cap.Group != null && cap.Group.Phase == FallingCohortPhase.Settling; work.Phase = 0;
                    work.Generation = pending.Gravity.Generations.TryGetValue(work.Column, out uint staged) ? staged
                        : _landingGeneration.TryGetValue(work.Column, out uint committed) ? committed : 0;
                }
                LastBuildPublicationWork += 16;
            }
        }
        void RetryBuildPublication(Pending pending)
        {
            var preparation = pending.BuildGuard;
            RecordPendingTerminal(pending, PendingWallOutcome.Aborted);
            _retired.Enqueue(new Retired { Pending = pending }); _pending = null;
            preparation.RepairAfterRetry = pending.AbortReason == PendingAbortReason.BuildMovingOverlap;
            preparation.Retry = true; preparation.RetryCursor = preparation.Checkpoints?.Last;
            _buildOccupancyPreparation = preparation; BuildPublicationRetries++;
        }
        void CancelPendingBuildPublication(Pending pending)
        {
            pending.AbortReason = PendingAbortReason.BuildAuthorityCancellation;
            pending.AdmissionStop.Cancel();
            RetryBuildPublication(pending);
            AbortBuildOccupancyPreparation();
        }
    }
}
