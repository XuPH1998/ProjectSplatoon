using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Serializable] public struct FinalGravityWorkState
        {
            public bool present, proofKnown, nativeProofPresent, replacementPresent, replacementReady, replacementFailed;
            public Vector2Int column;
            public Vector3 nativeProofPosition;
            public float nativeProofWorldBottom;
            public int sourceBottom, sourceTop, phase, scan, minimum, landing, validate, member, index, regroupY;
            public int spans, pieces, spanBottom, pieceFirst, pieceCount, nativeProofMinimum, preparedSpans, preparedPieces;
            public uint generation;
            public ulong membershipRevision, pieceRun, replacementParent, replacementMembership;
        }
        [Serializable] public struct FinalGravitySurvivorState
        {
            public ulong id, membershipRevision, headId;
            public bool cohort, needsRegroup, needsLandingProof, slotPresent, slotMatches, headPresent, headCohort;
            public bool generationPresent, landingCheckedPresent, rejectedBindingPresent, activeGenerationPresent, pendingGenerationPresent, privateGenerationPresent;
            public int phase, slot, headBottom;
            public uint generation, landingChecked, activeGeneration, pendingGeneration, privateGeneration;
            public FinalGravityWorkState committed, activeJournal, pendingJournal, privateJournal;
            public FinalGravityColumnState committedColumn, activeColumn, pendingColumn, privateColumn;
        }
        [Serializable] public struct FinalGravityColumnState
        {
            public bool present, proving, ready;
            public int scan, bottom, top;
            public ulong cohort;
        }
        [Serializable] public struct FinalGravityJournalState
        {
            public bool present, nextIsPublished, motionVisitActive, motionVisitBudgetDeferred, dependentEditDeferred;
            public uint nextRound, nextRevision;
            public ulong nextMutation;
            public int columns, motions, newColumns, newMotions, columnCursor, motionCursor;
        }
        [Serializable] public sealed class FinalGravityState
        {
            public string selection = "First 64 currently published cohort descriptors, then standalone runs, in dictionary enumeration order; no phase or NeedsRegroup filter. Full BLK3 is untruncated.";
            public uint epoch;
            public int motionWork, motionOrder, motionSlots, motionCursor, columnWork, columnOrder, columnCursor;
            public int unstableColumns, commands, survivorTotal, standaloneRuns, detaching, awaitActivation, falling, settling, needsRegroup, needsLandingProof;
            public bool samplesTruncated, indexPending, indexBuffered, groupsIndexed, groupBuffered, pendingPresent;
            public bool privateRequestPresent, activePrivateRequestPresent, privatePreparationPresent, rejectedMotionPresent, rejectedMotionCohort;
            public ulong rejectedMotionId, privateRequestId, activePrivateRequestId;
            public bool privateRequestCohort, activePrivateRequestCohort;
            public int privateProofPhase, privateProofMinimum, privateProofScan, privateProofLanding, privateProofValidate;
            public long commandsQueued, commandsCompleted, authorityStages, gravityChecks, gravityProgressStages;
            public FinalGravityJournalState activeJournal, pendingJournal, privateJournal;
            public FinalGravitySurvivorState[] survivors;
        }
        static FinalGravityWorkState ReadFinalGravityWork(GravityMotionWork work)
        {
            if (work == null) return default;
            var replacement = work.Replacement; var proof = work.NativeProof;
            return new FinalGravityWorkState { present = true, column = work.Column, sourceBottom = work.SourceBottom, sourceTop = work.SourceTop,
                phase = work.Phase, scan = work.Scan, minimum = work.Minimum, landing = work.Landing, validate = work.Validate, generation = work.Generation,
                proofKnown = work.ProofKnown, member = work.Member, index = work.Index, regroupY = work.RegroupY, membershipRevision = work.MembershipRevision,
                spans = work.Spans.Count, pieces = work.Pieces.Count, spanBottom = work.SpanBottom, pieceRun = work.PieceRun,
                pieceFirst = work.PieceFirst, pieceCount = work.PieceCount, nativeProofPresent = proof != null,
                nativeProofPosition = proof != null ? proof.Position : default, nativeProofWorldBottom = proof != null ? proof.WorldBottom : 0,
                nativeProofMinimum = proof != null ? proof.Minimum : 0, replacementPresent = replacement != null,
                replacementParent = replacement?.ParentId ?? 0, replacementMembership = replacement?.MembershipRevision ?? 0,
                replacementReady = replacement?.Ready ?? false, replacementFailed = replacement?.Failed ?? false,
                preparedSpans = replacement?.Prepared.Count ?? 0, preparedPieces = replacement?.PreparedPieceCount ?? 0 };
        }
        static FinalGravityWorkState ReadFinalGravityJournalWork(GravityJournal journal, GravityMotionId id)
            => journal != null && journal.Motions.TryGetValue(id, out var work) ? ReadFinalGravityWork(work) : default;
        static FinalGravityColumnState ReadFinalGravityColumn(Dictionary<Vector2Int, GravityColumnWork> columns, Vector2Int column)
            => columns != null && columns.TryGetValue(column, out var work) && work != null ? new FinalGravityColumnState { present = true,
                scan = work.Scan, bottom = work.Bottom, top = work.Top, proving = work.Proving, ready = work.Ready, cohort = work.Cohort } : default;
        static FinalGravityJournalState ReadFinalGravityJournal(GravityJournal journal, BlockWorld published)
            => journal == null ? default : new FinalGravityJournalState { present = true, nextIsPublished = ReferenceEquals(journal.Next, published),
                nextRound = journal.Next?.Round ?? 0, nextRevision = journal.Next?.Revision ?? 0, nextMutation = journal.Next?.MutationGeneration ?? 0,
                columns = journal.Columns.Count, motions = journal.Motions.Count, newColumns = journal.NewColumns.Count, newMotions = journal.NewMotions.Count,
                columnCursor = journal.ColumnCursor, motionCursor = journal.MotionCursor, motionVisitActive = journal.MotionVisitActive,
                motionVisitBudgetDeferred = journal.MotionVisitBudgetDeferred, dependentEditDeferred = journal.DependentEditDeferred };
        FinalGravitySurvivorState ReadFinalGravitySurvivor(GravityMotionId id, Vector2Int column, FallingCohort cohort)
        {
            var row = new FinalGravitySurvivorState { id = id.Id, cohort = id.Cohort, phase = cohort != null ? (int)cohort.Phase : -1,
                membershipRevision = cohort?.MembershipRevision ?? 0, needsRegroup = cohort?.NeedsRegroup ?? false,
                needsLandingProof = cohort?.NeedsLandingProof ?? false, slot = -1 };
            row.slotPresent = _cohortMotionSlots.TryGetValue(id, out int slot);
            if (row.slotPresent) { row.slot = slot; row.slotMatches = slot >= 0 && slot < _cohortMotionOrder.Count && _cohortMotionOrder[slot].Equals(id); }
            if (_cohortHeads.TryGetValue(column, out var heads) && heads.Count != 0)
            { var head = heads.Min; row.headPresent = true; row.headId = head.Motion.Id; row.headCohort = head.Motion.Cohort; row.headBottom = head.Bottom; }
            row.generationPresent = _landingGeneration.TryGetValue(column, out row.generation);
            if (_gravityJournal != null) row.activeGenerationPresent = _gravityJournal.Generations.TryGetValue(column, out row.activeGeneration);
            if (_pending?.Gravity != null) row.pendingGenerationPresent = _pending.Gravity.Generations.TryGetValue(column, out row.pendingGeneration);
            if (_privateGravityJournal != null) row.privateGenerationPresent = _privateGravityJournal.Generations.TryGetValue(column, out row.privateGeneration);
            if (!id.Cohort) row.landingCheckedPresent = _landingChecked.TryGetValue(id.Id, out row.landingChecked);
            row.rejectedBindingPresent = id.Cohort && _rejectedGravityBindings.ContainsKey(id.Id);
            row.committed = _cohortMotionWork.TryGetValue(id, out var work) ? ReadFinalGravityWork(work) : default;
            row.activeJournal = ReadFinalGravityJournalWork(_gravityJournal, id);
            row.pendingJournal = ReadFinalGravityJournalWork(_pending?.Gravity, id);
            row.privateJournal = ReadFinalGravityJournalWork(_privateGravityJournal, id);
            row.committedColumn = ReadFinalGravityColumn(_cohortColumnWork, column);
            row.activeColumn = ReadFinalGravityColumn(_gravityJournal?.Columns, column);
            row.pendingColumn = ReadFinalGravityColumn(_pending?.Gravity?.Columns, column);
            row.privateColumn = ReadFinalGravityColumn(_privateGravityJournal?.Columns, column);
            return row;
        }
        public FinalGravityState InspectFinalGravityState(BlockWorld published)
        {
            if (published == null || !ReferenceEquals(published, World)) throw new InvalidOperationException("Final diagnostic requires the current published world reference.");
            var result = new FinalGravityState { epoch = _epoch, motionWork = _cohortMotionWork.Count, motionOrder = _cohortMotionOrder.Count,
                motionSlots = _cohortMotionSlots.Count, motionCursor = _cohortMotionCursor, columnWork = _cohortColumnWork.Count,
                columnOrder = _cohortColumnOrder.Count, columnCursor = _cohortColumnCursor, unstableColumns = _gravityColumns.Count,
                commands = _commands.Count, commandsQueued = CommandsQueued, commandsCompleted = CommandsCompleted,
                authorityStages = AuthorityStageCount, gravityChecks = GravityWorkChecks, gravityProgressStages = GravityProgressStages,
                indexPending = _cohortIndexPending, indexBuffered = _cohortIndexBuffered, groupsIndexed = _cohortGroupsIndexed, groupBuffered = _cohortGroupBuffered,
                pendingPresent = _pending != null, privateRequestPresent = _privateGravityRequest != null,
                activePrivateRequestPresent = _activePrivateGravityRequest != null, privatePreparationPresent = _privateGravityPreparation != null,
                privateRequestId = _privateGravityRequest?.Motion.Id ?? 0, privateRequestCohort = _privateGravityRequest?.Motion.Cohort ?? false,
                activePrivateRequestId = _activePrivateGravityRequest?.Motion.Id ?? 0, activePrivateRequestCohort = _activePrivateGravityRequest?.Motion.Cohort ?? false,
                privateProofPhase = _privateGravityProofPhase, privateProofMinimum = _privateGravityProofMinimum, privateProofScan = _privateGravityProofScan,
                privateProofLanding = _privateGravityProofLanding, privateProofValidate = _privateGravityProofValidate,
                rejectedMotionPresent = _rejectedGravityBinding.HasValue, rejectedMotionId = _rejectedGravityBinding?.Id ?? 0,
                rejectedMotionCohort = _rejectedGravityBinding?.Cohort ?? false,
                activeJournal = ReadFinalGravityJournal(_gravityJournal, published), pendingJournal = ReadFinalGravityJournal(_pending?.Gravity, published),
                privateJournal = ReadFinalGravityJournal(_privateGravityJournal, published) };
            var rows = new List<FinalGravitySurvivorState>(64);
            foreach (var cohort in published.Cohorts.Values)
            {
                switch (cohort.Phase) { case FallingCohortPhase.Detaching: result.detaching++; break; case FallingCohortPhase.AwaitActivation: result.awaitActivation++; break;
                    case FallingCohortPhase.Falling: result.falling++; break; case FallingCohortPhase.Settling: result.settling++; break; }
                if (cohort.NeedsRegroup) result.needsRegroup++; if (cohort.NeedsLandingProof) result.needsLandingProof++;
                if (rows.Count < 64) rows.Add(ReadFinalGravitySurvivor(new GravityMotionId(cohort.Id, true), new Vector2Int(cohort.Bottom.x, cohort.Bottom.z), cohort));
            }
            foreach (var run in published.Falling.Values)
            {
                if (run.Cohort != null) continue;
                result.standaloneRuns++;
                if (rows.Count < 64) rows.Add(ReadFinalGravitySurvivor(new GravityMotionId(run.Id, false), new Vector2Int(run.Bottom.x, run.Bottom.z), null));
            }
            result.survivorTotal = published.Cohorts.Count + result.standaloneRuns;
            result.samplesTruncated = result.survivorTotal > rows.Count; result.survivors = rows.ToArray();
            return result;
        }
#endif

        // Authority proofs are private staged state. A pending bake never consumes
        // committed cursors; only the matching World publication commits this journal.
        sealed class GravityColumnWork
        {
            public int Scan, Bottom, Top;
            public bool Proving, Ready;
            public ulong Cohort;
            public GravityColumnWork Copy() => (GravityColumnWork)MemberwiseClone();
        }
        readonly struct GravityMotionId : IEquatable<GravityMotionId>
        {
            public readonly ulong Id;
            public readonly bool Cohort;
            public GravityMotionId(ulong id, bool cohort) { Id = id; Cohort = cohort; }
            public bool Equals(GravityMotionId other) => Id == other.Id && Cohort == other.Cohort;
            public override bool Equals(object obj) => obj is GravityMotionId other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Id, Cohort);
        }
        readonly struct GravityMotionKey : IComparable<GravityMotionKey>
        {
            public readonly int Bottom;
            public readonly GravityMotionId Motion;
            public GravityMotionKey(int bottom, GravityMotionId motion) { Bottom = bottom; Motion = motion; }
            public int CompareTo(GravityMotionKey other)
            {
                int order = Bottom.CompareTo(other.Bottom); if (order != 0) return order;
                order = Motion.Id.CompareTo(other.Motion.Id); return order != 0 ? order : Motion.Cohort.CompareTo(other.Motion.Cohort);
            }
        }
        sealed class GravityMotionWork
        {
            public Vector2Int Column;
            public int SourceBottom, SourceTop, Phase, Scan, Minimum, Landing, Validate;
            public uint Generation;
            public bool ProofKnown;
            public int Member, Index, RegroupY;
            public ulong MembershipRevision;
            public FallingCohortReplacement Replacement;
            public GravityStaticProof NativeProof;
            public List<FallingCohortSpan> Spans = new();
            public List<FallingCohortPiece> Pieces = new();
            public int SpanBottom = int.MinValue, PieceFirst, PieceCount;
            public ulong PieceRun;
            public GravityMotionWork Copy()
            {
                var copy = (GravityMotionWork)MemberwiseClone();
                copy.Spans = new List<FallingCohortSpan>(Spans);
                copy.Pieces = new List<FallingCohortPiece>(Pieces);
                copy.Replacement = Replacement?.Clone(out _); return copy;
            }
        }
        sealed class GravityJournal
        {
            // Only successful work admitted while this exact journal is active.
            public long AdmittedWork;
            public bool BuildRepairSelectionAttempted, BuildRepairDebtTracked;
            public long BuildRepairPriorityDebt;
            public BuildRepairVisit BuildRepair;
            public BlockPublicationWindow Window;
            public BlockWorld Next;
            public int ColumnCursor, MotionCursor;
            public readonly Dictionary<Vector2Int, GravityColumnWork> Columns = new();
            public readonly Dictionary<GravityMotionId, GravityMotionWork> Motions = new();
            public readonly List<Vector2Int> NewColumns = new();
            public readonly HashSet<Vector2Int> NewColumnKeys = new();
            public readonly List<GravityMotionId> NewMotions = new();
            public readonly Dictionary<Vector2Int, uint> Generations = new();
            public readonly Dictionary<ulong, uint> Checked = new();
            public readonly Dictionary<Vector2Int, SortedSet<GravityMotionKey>> Heads = new();
            public readonly HashSet<Vector2Int> NewBuildGravityBarriers = new();
            public readonly Dictionary<ulong, GravityBindingContinuity> BindingContinuity = new();
            public readonly Dictionary<ulong, LegacyLandingContinuity> LegacyContinuity = new();
            public readonly LinkedList<GravityStaticProof> StaticProofs = new();
            public readonly HashSet<GravityStaticProof> RetainedStaticProofs = new();
            public readonly Dictionary<ulong, GravityMotionWork> OwnActivationProofs = new();
            // Visit-local admission state, never wire data or committed proof state.
            public bool MotionVisitActive, MotionVisitBudgetDeferred;
            public bool SupportSnapshotAttempted, RetainSupportSnapshot;
            public GravitySupportSnapshot SupportSnapshot;
            public BlockWorld SupportProofDraft;
            public readonly LinkedList<GravitySupportAbsenceSpan> SupportAbsenceSpans = new();
            public readonly Dictionary<Vector2Int, List<GravitySupportBounds>> SupportColumns = new();
            public GravityPrivateRequest PrivateRequest;
            public HistoricalRecoveryRequest RecoveryRequest;
            public GravityDependentEditRequest DependentEditRequest;
            public bool DependentEditDeferred;
            public Vector2Int? HeadPreparationColumn;
            public GravityStartupJournal Startup;
            public readonly List<BlockSpreadMove> SpreadMoves = new();
        }
        readonly Dictionary<Vector2Int, GravityColumnWork> _cohortColumnWork = new();
        readonly List<Vector2Int> _cohortColumnOrder = new();
        readonly Dictionary<Vector2Int, int> _cohortColumnSlots = new();
        readonly Dictionary<GravityMotionId, GravityMotionWork> _cohortMotionWork = new();
        readonly List<GravityMotionId> _cohortMotionOrder = new();
        readonly Dictionary<GravityMotionId, int> _cohortMotionSlots = new();
        readonly Dictionary<Vector2Int, SortedSet<GravityMotionKey>> _cohortHeads = new();
        int _cohortColumnCursor, _cohortMotionCursor;
        GravityJournal _gravityJournal;
        IEnumerator<KeyValuePair<ulong, FallingRun>> _cohortIndex;
        IEnumerator<KeyValuePair<ulong, FallingCohort>> _cohortGroupIndex;
        bool _cohortIndexPending;
        bool _cohortIndexBuffered;
        bool _cohortGroupsIndexed, _cohortGroupBuffered;
        int _cohortIndexFirst;

        void ResetGravityCohorts(bool keepQueuedColumns = false)
        {
            CancelBuildOverlapRepair(); _buildRepairPriorityDebt = 0;
            ObserveFairGravityWorkMutation();
            ResetGravityStartup();
            AbortPrivatePreparation();
            ResetBuildGravityBarriers();
            ResetBuildOccupancyQueryCache();
            _rejectedGravityBindings = new Dictionary<ulong, RejectedGravityBinding>();
            _rejectedBindingSource = null;
            _cohortIndex?.Dispose(); _cohortIndex = null; _cohortIndexBuffered = false; _cohortIndexFirst = 0;
            _cohortGroupIndex?.Dispose(); _cohortGroupIndex = null; _cohortGroupsIndexed = _cohortGroupBuffered = false;
            _cohortIndexPending = World != null && (World.Falling.Count != 0 || World.Cohorts.Count != 0);
            _cohortMotionWork.Clear(); _cohortMotionOrder.Clear(); _cohortMotionSlots.Clear(); _cohortHeads.Clear(); _cohortMotionCursor = 0;
            if (!keepQueuedColumns)
            {
                _cohortColumnWork.Clear(); _cohortColumnOrder.Clear(); _cohortColumnSlots.Clear(); _cohortColumnCursor = 0;
                _gravityColumns.Clear(); _gravityCursor.Clear();
            }
        }
        void StartGravityJournal(BlockWorld next)
            => StartWindowGravityJournal(next, _stagePublicationWindow ?? _publicationPolicy.Capture(BlockPublicationOperation.Gravity));
        void StartWindowGravityJournal(BlockWorld next, BlockPublicationWindow window)
        {
            _gravityJournal = new GravityJournal { Next = next, Window = window, ColumnCursor = _cohortColumnCursor, MotionCursor = _cohortMotionCursor,
                Startup = NewGravityStartupJournal() };
        }
        GravityColumnWork EditColumn(Vector2Int column)
        {
            if (_gravityJournal.Columns.TryGetValue(column, out var work) && work != null) return work;
            if (_cohortColumnWork.TryGetValue(column, out var committed)) work = committed.Copy();
            else
            {
                work = new GravityColumnWork { Scan = _gravityCursor.TryGetValue(column, out int y) ? y : Mathf.FloorToInt(_gravityJournal.Next.Bottom / BlockWorld.CellSize) };
                if (_gravityJournal.NewColumnKeys.Add(column)) _gravityJournal.NewColumns.Add(column);
            }
            _gravityJournal.Columns[column] = work; return work;
        }
        int EstimateColumnEditWork(Vector2Int column)
            => _gravityJournal.Columns.TryGetValue(column, out var work) && work != null ? 0 : 24;
        int EstimateQueueGravityWork(Vector3Int cell)
            => EstimateColumnEditWork(new Vector2Int(cell.x, cell.z)) + 8;
        GravityMotionWork EditMotion(GravityMotionId id)
        {
            if (_gravityJournal.Motions.TryGetValue(id, out var work)) return work;
            if (!_cohortMotionWork.TryGetValue(id, out var committed)) return null;
            work = committed.Copy(); _gravityJournal.Motions[id] = work; return work;
        }
        int EstimateMotionEditWork(GravityMotionId id)
        {
            if (_gravityJournal.Motions.ContainsKey(id)) return 0;
            return _cohortMotionWork.TryGetValue(id, out var work) ? 16 + work.Spans.Count + work.Pieces.Count + (work.Replacement?.EstimateCloneWork() ?? 0) : 16;
        }
        SortedSet<GravityMotionKey> MotionHeads(Vector2Int column)
            => _gravityJournal.Heads.TryGetValue(column, out var staged) ? staged
                : _cohortHeads.TryGetValue(column, out var committed) ? committed : null;
        int EstimateHeadEditWork(Vector2Int column)
        {
            if (_gravityJournal.Heads.ContainsKey(column) || HasPreparedHeads(column)) return 64;
            int count = MotionHeads(column)?.Count ?? 0;
            if (count <= InlineHeadCopyLimit) return 65 + 2 * count;
            // The complete source column has no400-entry bound. Request a
            // finite staged copy, never submit an oversized indivisible COW.
            if (!_gravityJournal.HeadPreparationColumn.HasValue) _gravityJournal.HeadPreparationColumn = column;
            return MaxChecks + 1;
        }
        SortedSet<GravityMotionKey> EditHeads(Vector2Int column)
        {
            if (!_gravityJournal.Heads.TryGetValue(column, out var heads))
            {
                if (HasPreparedHeads(column)) heads = TakePreparedHeads(column);
                else
                {
                    if (_cohortHeads.TryGetValue(column, out var committed) && committed.Count > InlineHeadCopyLimit)
                        throw new InvalidOperationException("A large column head COW requires completed finite metadata preparation.");
                    heads = committed != null ? new SortedSet<GravityMotionKey>(committed) : new SortedSet<GravityMotionKey>();
                }
                _gravityJournal.Heads.Add(column, heads);
            }
            return heads;
        }
        int EstimateQueueMotionWork(GravityMotionId id, Vector2Int column)
            => EstimateMotionEditWork(id) + (_cohortMotionWork.ContainsKey(id) || _gravityJournal.Motions.ContainsKey(id) ? 0 : EstimateHeadEditWork(column)) + 2;
        void QueueMotion(GravityMotionId id, Vector2Int column, int bottom, int top)
        {
            if (_gravityJournal.Motions.TryGetValue(id, out var staged) && staged != null) { staged.ProofKnown = false; return; }
            if (_cohortMotionWork.ContainsKey(id)) { EditMotion(id).ProofKnown = false; return; }
            _gravityJournal.Motions.Add(id, new GravityMotionWork { Column = column, SourceBottom = bottom, SourceTop = top });
            _gravityJournal.NewMotions.Add(id);
            EditHeads(column).Add(new GravityMotionKey(bottom, id));
        }
        void RemoveMotion(GravityMotionId id, GravityMotionWork work)
        {
            _gravityJournal.Motions[id] = null;
            EditHeads(work.Column).Remove(new GravityMotionKey(work.SourceBottom, id));
        }
        void QueueGravityCohortCell(Vector3Int cell)
        {
            ObserveFairGravityWorkMutation();
            var column = new Vector2Int(cell.x, cell.z);
            if (_gravityJournal == null)
            {
                _gravityColumns.Add(column);
                _landingGeneration[column] = _landingGeneration.TryGetValue(column, out uint old) ? old + 1 : 1;
                if (!_gravityCursor.TryGetValue(column, out int first) || cell.y < first) _gravityCursor[column] = cell.y;
                if (_cohortColumnWork.TryGetValue(column, out var existing))
                {
                    if (existing.Cohort == 0 && (existing.Ready || existing.Proving))
                        existing.Scan = Math.Min(existing.Scan, existing.Bottom);
                    existing.Scan = Math.Min(existing.Scan, cell.y);
                    if (existing.Cohort == 0) { existing.Ready = existing.Proving = false; }
                }
                else AddCommittedColumn(column, new GravityColumnWork { Scan = cell.y });
                return;
            }
            var work = EditColumn(column);
            // Invalidating an unfinished source proof also invalidates its scan
            // progress. Revisit the known unsupported bottom before the edit.
            if (work.Cohort == 0 && (work.Ready || work.Proving))
                work.Scan = Math.Min(work.Scan, work.Bottom);
            work.Scan = Math.Min(work.Scan, cell.y);
            if (work.Cohort == 0) work.Ready = work.Proving = false;
            uint generation = _gravityJournal.Generations.TryGetValue(column, out uint staged) ? staged
                : _landingGeneration.TryGetValue(column, out uint committed) ? committed : 0;
            _gravityJournal.Generations[column] = generation + 1;
        }
        void AddCommittedColumn(Vector2Int column, GravityColumnWork work)
        {
            if (!_cohortColumnSlots.ContainsKey(column)) { _cohortColumnSlots.Add(column, _cohortColumnOrder.Count); _cohortColumnOrder.Add(column); }
            _cohortColumnWork[column] = work; _gravityColumns.Add(column); _gravityCursor[column] = work.Scan;
        }
        static void RemoveSlot<T>(T key, Dictionary<T, int> slots, List<T> order)
        {
            if (!slots.Remove(key, out int index)) return;
            int last = order.Count - 1; if (index != last) { order[index] = order[last]; slots[order[index]] = index; }
            order.RemoveAt(last);
        }
        void CommitGravityJournal(GravityJournal journal)
        {
            if (journal == null) return;
            if (journal.BuildRepairDebtTracked) _buildRepairPriorityDebt = journal.BuildRepairPriorityDebt;
            ObserveFairGravityWorkMutation();
            RefreshRejectedBindingSource();
            foreach (var column in journal.NewBuildGravityBarriers)
            { _buildGravityBarriers[column] = new BuildGravityBarrier(); BuildSourceOrderDeferrals++; }
            foreach (var pair in journal.Columns)
            {
                if (pair.Value == null)
                { _cohortColumnWork.Remove(pair.Key); RemoveSlot(pair.Key, _cohortColumnSlots, _cohortColumnOrder); _gravityColumns.Remove(pair.Key); _gravityCursor.Remove(pair.Key); }
                else AddCommittedColumn(pair.Key, pair.Value);
            }
            foreach (var pair in journal.Motions)
            {
                if (pair.Value == null) { _cohortMotionWork.Remove(pair.Key); RemoveSlot(pair.Key, _cohortMotionSlots, _cohortMotionOrder); }
                else
                {
                    if (!_cohortMotionSlots.ContainsKey(pair.Key)) { _cohortMotionSlots.Add(pair.Key, _cohortMotionOrder.Count); _cohortMotionOrder.Add(pair.Key); }
                    _cohortMotionWork[pair.Key] = pair.Value;
                }
            }
            foreach (var pair in journal.Generations) _landingGeneration[pair.Key] = pair.Value;
            foreach (var pair in journal.Checked) _landingChecked[pair.Key] = pair.Value;
            foreach (var pair in journal.Heads)
                if (pair.Value.Count == 0) _cohortHeads.Remove(pair.Key); else _cohortHeads[pair.Key] = pair.Value;
            if (journal.PrivateRequest != null) _privateGravityRequest = journal.PrivateRequest;
            if (journal.RecoveryRequest != null) _historicalRecoveryRequest = journal.RecoveryRequest;
            if (journal.HeadPreparationColumn.HasValue) RequestWindowHeadPreparation(journal.HeadPreparationColumn.Value, journal.Window);
            _cohortColumnCursor = _cohortColumnOrder.Count == 0 ? 0 : journal.ColumnCursor % _cohortColumnOrder.Count;
            _cohortMotionCursor = _cohortMotionOrder.Count == 0 ? 0 : journal.MotionCursor % _cohortMotionOrder.Count;
            CommitGravityStartup(journal);
            OnSpreadGravityCommitted(journal);
        }
        bool SpendGravity(ref int checks, int count, int maxChecks, Stopwatch timer, double milliseconds)
        {
            if (count < 0) return false;
            if (checks + count > maxChecks || !HasStageBudget(checks, maxChecks, timer, milliseconds) || !TrySpendGravityFrameWork(count))
            {
                ObserveGravityStartupVisitDeferred();
                // A bounded motion operation that fits a fresh stage has not
                // received its turn when only this stage's remainder rejects it.
                // Oversized operations keep their existing preparation/skip path.
                if (_gravityJournal != null && _gravityJournal.MotionVisitActive && count <= maxChecks && checks != 0)
                    _gravityJournal.MotionVisitBudgetDeferred = true;
                return false;
            }
            checks += count; GravityWorkChecks += count;
            if (_gravityJournal != null) _gravityJournal.AdmittedWork += count;
            ChargeGravityStartupWork(count); return true;
        }
        void IndexGravityWork(BlockWorld next, ref int checks, Stopwatch timer, double milliseconds, int maxChecks)
        {
            if (!_cohortIndexPending) return;
            ObserveFairGravityWorkMutation();
            if (!_cohortGroupsIndexed)
            {
                _cohortGroupIndex ??= World.Cohorts.GetEnumerator();
                while (HasStageBudget(checks, maxChecks, timer, milliseconds))
                {
                    if (!_cohortGroupBuffered)
                    {
                        if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return;
                        if (!_cohortGroupIndex.MoveNext()) { _cohortGroupIndex.Dispose(); _cohortGroupIndex = null; _cohortGroupsIndexed = true; break; }
                        _cohortGroupBuffered = true;
                    }
                    var group = _cohortGroupIndex.Current.Value;
                    var id = new GravityMotionId(group.Id, true); var column = new Vector2Int(group.Bottom.x, group.Bottom.z);
                    if (!_cohortMotionWork.ContainsKey(id))
                    {
                        if (!SpendGravity(ref checks, 48, maxChecks, timer, milliseconds)) return;
                        _cohortMotionSlots.Add(id, _cohortMotionOrder.Count); _cohortMotionOrder.Add(id);
                        _cohortMotionWork.Add(id, new GravityMotionWork { Column = column, SourceBottom = group.Bottom.y, SourceTop = group.TopExclusive });
                        if (!_cohortHeads.TryGetValue(column, out var heads)) _cohortHeads.Add(column, heads = new SortedSet<GravityMotionKey>());
                        heads.Add(new GravityMotionKey(group.Bottom.y, id));
                        if (_gravityJournal.Heads.TryGetValue(column, out var stagedHeads)) stagedHeads.Add(new GravityMotionKey(group.Bottom.y, id));
                    }
                    _cohortGroupBuffered = false;
                }
                if (!_cohortGroupsIndexed) return;
            }
            _cohortIndex ??= World.Falling.GetEnumerator();
            while (HasStageBudget(checks, maxChecks, timer, milliseconds))
            {
                if (!_cohortIndexBuffered)
                {
                    if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) break;
                    if (!_cohortIndex.MoveNext()) { _cohortIndex.Dispose(); _cohortIndex = null; _cohortIndexPending = false; break; }
                    _cohortIndexBuffered = true; _cohortIndexFirst = 0;
                }
                // Import a fact about the immutable published source, independently
                // of the candidate transaction. Consumed enumerator entries survive
                // a failed bake because the committed index receives each entry.
                var run = _cohortIndex.Current.Value;
                if (run.Cohort != null) { _cohortIndexBuffered = false; continue; }
                if (run.Cohort == null)
                {
                    while (_cohortIndexFirst < run.Cells.Length && !run.Cells[_cohortIndexFirst].Occupied)
                    {
                        if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return;
                        _cohortIndexFirst++;
                    }
                    if (_cohortIndexFirst == run.Cells.Length) { _cohortIndexBuffered = false; continue; }
                }
                var column = new Vector2Int(run.Bottom.x, run.Bottom.z);
                var id = run.Cohort != null ? new GravityMotionId(run.Cohort.Id, true) : new GravityMotionId(run.Id, false);
                if (!_cohortMotionWork.ContainsKey(id))
                {
                    if (!SpendGravity(ref checks, 48, maxChecks, timer, milliseconds)) break;
                    int bottom = run.Cohort?.Bottom.y ?? run.Bottom.y + _cohortIndexFirst;
                    int top = run.Cohort?.TopExclusive ?? run.Bottom.y + run.Cells.Length;
                    _cohortMotionSlots.Add(id, _cohortMotionOrder.Count); _cohortMotionOrder.Add(id);
                    _cohortMotionWork.Add(id, new GravityMotionWork { Column = column, SourceBottom = bottom, SourceTop = top });
                    if (!_cohortHeads.TryGetValue(column, out var heads)) _cohortHeads.Add(column, heads = new SortedSet<GravityMotionKey>());
                    heads.Add(new GravityMotionKey(bottom, id));
                    if (_gravityJournal.Heads.TryGetValue(column, out var stagedHeads)) stagedHeads.Add(new GravityMotionKey(bottom, id));
                }
                _cohortIndexBuffered = false;
            }
        }
        int ColumnCount => _cohortColumnOrder.Count + _gravityJournal.NewColumns.Count;
        Vector2Int ColumnAt(int index) => index < _cohortColumnOrder.Count ? _cohortColumnOrder[index] : _gravityJournal.NewColumns[index - _cohortColumnOrder.Count];
        bool StageCohortColumns(BlockWorld next, HashSet<Vector3Int> changed, ref int checks, Stopwatch timer, double milliseconds, int maxChecks)
        {
            bool mutation = false; IndexGravityWork(next, ref checks, timer, milliseconds, maxChecks);
            if (_cohortIndexPending) return false;
            int count = ColumnCount; if (count == 0) return false;
            // Fair rounds: one bounded row proof or one <=16 storage piece per column visit.
            // The first accepted source+halo window stays fixed in a detach round.
            int visits = 0, readyPass = 0;
            bool detachedThisPass = false, startupVisit = false, startupVisitPending = false;
            int startupVisitCursor = 0;
            bool FinishColumnVisit(int total, bool skipped = false)
            {
                startupVisitPending = false;
                EndGravityStartupVisit();
                if (startupVisit) return false;
                // Advance only after an admitted visit; an unfinished row proof
                // resumes on this column when the next stage obtains its budget.
                _gravityJournal.ColumnCursor++;
                if (++visits < total) return false;
                bool stop = (readyPass != 0 || skipped) && !detachedThisPass;
                visits = readyPass = 0; detachedThisPass = false;
                return stop;
            }
            while (HasStageBudget(checks, maxChecks, timer, milliseconds) && ColumnCount != 0)
            {
                int total = ColumnCount;
                startupVisitCursor = CurrentGravityStartup().ColumnCursor;
                startupVisit = TrySelectStartupColumn(ref checks, maxChecks, timer, milliseconds, out var column);
                startupVisitPending = startupVisit;
                if (!startupVisit) column = ColumnAt(_gravityJournal.ColumnCursor % total);
                BeginGravityStartupVisit(false, startupVisit);
                // A terminal entry stays in this journal until publication. Only
                // a real cell change may reopen it through QueueGravityCohortCell.
                if (_gravityJournal.Columns.TryGetValue(column, out var terminal) && terminal == null)
                {
                    if (!SpendGravity(ref checks, 3, maxChecks, timer, milliseconds)) break;
                    if (FinishColumnVisit(total, true)) break;
                    continue;
                }
                if (!SpendGravity(ref checks, 3 + EstimateColumnEditWork(column), maxChecks, timer, milliseconds)) break;
                var work = EditColumn(column); if (work == null) { if (FinishColumnVisit(total, true)) break; continue; }
                if (ReservedColumn(column)) { if (FinishColumnVisit(total, true)) break; continue; }
                if (HistoricalRecoveryTerminal(column)) { if (FinishColumnVisit(total, true)) break; continue; }
                if (work.Cohort != 0 && !next.Cohorts.ContainsKey(work.Cohort)) { work.Cohort = 0; work.Ready = work.Proving = false; }
                if (work.Cohort == 0 && DeferNewBuildCohort(next, column, ref checks, maxChecks, timer, milliseconds))
                { if (FinishColumnVisit(total, true)) break; continue; }
                ObserveGravityStartupCheck(column);
                bool actualColumnWork = false;
                if (work.Ready)
                {
                    int beforeDetachChecks = checks;
                    mutation |= TryDetachCohortPiece(next, changed, column, work, ref checks, timer, milliseconds, maxChecks, out bool detached);
                    actualColumnWork = checks > beforeDetachChecks;
                    detachedThisPass |= detached;
                    readyPass++;
                }
                else
                {
                    int top = Mathf.RoundToInt(BlockWorld.Top / BlockWorld.CellSize);
                    if (work.Scan >= top)
                    {
                        if (work.Proving) { work.Top = top; work.Ready = true; }
                        else _gravityJournal.Columns[column] = null;
                    }
                    else
                    {
                        var cell = new Vector3Int(column.x, work.Scan, column.y);
                        actualColumnWork = true;
                        if (work.Proving)
                        {
                            // The bitmap is read afresh from this draft, including later
                            // commands. Only physical AABB facts are journal-local caches.
                            int rowLength = Math.Min(top - work.Scan, 16 - (work.Scan & 15));
                            if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) break;
                            int length = next.UnanchoredVerticalSpanLength(cell, rowLength);
                            if (length == 0) { work.Top = work.Scan; work.Ready = true; }
                            else
                            {
                                if (!TryGravityNoStaticSupport(column, work.Scan, length, out bool noStatic,
                                    ref checks, maxChecks, timer, milliseconds)) break;
                                // A negative certificate covers every quantized-support
                                // probe. Any possible intersection keeps the exact ray.
                                if (noStatic) work.Scan += length;
                                else if (StaticSupport(cell)) { work.Top = work.Scan; work.Ready = true; }
                                else work.Scan++;
                            }
                        }
                        else
                        {
                            int rowLength = Math.Min(top - work.Scan, 16 - (work.Scan & 15));
                            if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) break;
                            int gap = next.UnanchoredVerticalGapLength(cell, rowLength);
                            // Empty and anchored cells cannot start an unsupported
                            // body. Skip that exact bitmap gap, including the tail to Top.
                            if (gap != 0) work.Scan += gap;
                            else if (!next.HasSupport(cell, StaticSupport))
                            { work.Bottom = work.Scan; work.Scan++; work.Proving = true; }
                            else work.Scan++;
                        }
                    }
                }
                if (actualColumnWork) ObserveGravityStartupColumnServed(column);
                // Each column got its fair visit, including skipped terminal peers.
                // Only real detachment enables reuse of the same exact key window.
                if (FinishColumnVisit(total)) break;
            }
            // A selected urgent column whose bounded proof did not fit keeps
            // the same fresh-stage turn, just like the ordinary column cursor.
            EndGravityStartupVisit();
            if (startupVisitPending) { _gravityJournal.Startup.ColumnCursor = startupVisitCursor; _gravityJournal.Startup.ColumnTurn = true; }
            return mutation;
        }
        bool TryDetachCohortPiece(BlockWorld next, HashSet<Vector3Int> changed, Vector2Int column, GravityColumnWork work,
            ref int checks, Stopwatch timer, double milliseconds, int maxChecks, out bool detached)
        {
            detached = false;
            FallingCohort group = work.Cohort == 0 ? null : next.Cohorts[work.Cohort];
            int cursor = group?.DetachCursor ?? work.Bottom;
            int top = group?.TopExclusive ?? work.Top;
            int length = Math.Min(16, top - cursor);
            var bottom = new Vector3Int(column.x, cursor, column.y);
            if (length <= 0) { work.Cohort = 0; work.Ready = work.Proving = false; return false; }
            if (group != null && group.NeedsRegroup) return false;
            // A <=16-cell span encounters at most three distinct footprint events:
            // its first cell, a local-y=15 cell, and the next local-y=0 cell.
            // All interior cells share the same own/side keys. Charge the actual
            // bounded planning work before querying/allocating the private footprint.
            if (!SpendGravity(ref checks, 3 * 14 + changed.Count, maxChecks, timer, milliseconds)) return false;
            var keys = new HashSet<Vector3Int>(changed);
            for (int i = 0; i < length; i++)
            {
                int localY = (cursor + i) & (BlockWorld.ChunkSize - 1);
                if (i != 0 && localY != 0 && localY != BlockWorld.ChunkSize - 1) continue;
                var cell = bottom + Vector3Int.up * i;
                if (!FitsBudget(_gravityJournal.Window, keys, cell)) { length = i; break; }
                IncludeCell(keys, cell);
            }
            if (length == 0) return false;
            int queueCost = group == null ? EstimateQueueMotionWork(new GravityMotionId(next.NextCohortId, true), column) + 1 : 0;
            int rows = BlockWorld.VerticalPieceRows(bottom, length);
            if (!SpendGravity(ref checks, rows, maxChecks, timer, milliseconds)) return false;
            bool valid = next.UnanchoredVerticalSpanLength(bottom, length) == length, noStatic = false;
            if (valid && !TryGravityNoStaticSupport(column, cursor, length, out noStatic,
                ref checks, maxChecks, timer, milliseconds)) return false;
            int worldCost = group == null ? 2 * length + rows + 2 : next.EstimateDetachCohortPieceWork(group.Id, length);
            int validationCost = noStatic ? 2 * rows : 2 * length;
            if (!SpendGravity(ref checks, worldCost + validationCost + queueCost + 1,
                maxChecks, timer, milliseconds)) return false;
            if (valid && noStatic) IncludeVerticalPiece(keys, bottom, length);
            else
            {
                for (int i = 0; i < length; i++)
                {
                    var cell = bottom + Vector3Int.up * i;
                    if (!next.TryGet(cell, out var own) || own.Anchored || StaticSupport(cell))
                    { valid = false; break; }
                    IncludeCell(keys, cell);
                }
            }
            if (!valid)
            {
                if (group == null) { work.Ready = work.Proving = false; work.Scan = work.Bottom; return false; }
                int finishCost = next.EstimateFinalizeCohortDetachmentWork(group.Id);
                if (!SpendGravity(ref checks, finishCost, maxChecks, timer, milliseconds)) return false;
                next.FinishCohortDetachment(group.Id, cursor, out _);
                work.Cohort = 0; work.Ready = work.Proving = false; work.Scan = cursor; return true;
            }
            // Do not allocate a descriptor until the exact source/halo footprint
            // and all first-piece/index work have been admitted.
            if (keys.Count > _gravityJournal.Window.MaximumChangedKeys) return false;
            if (group == null)
            {
                group = next.BeginCohort(bottom, top - cursor); work.Cohort = group.Id;
                QueueMotion(new GravityMotionId(group.Id, true), column, group.Bottom.y, group.TopExclusive);
                QueueGravityStartupMotion(new GravityMotionId(group.Id, true), column);
                if (length == top - cursor) return true; // Metadata first: admit landing proof before the only detach publication.
            }
            bool finalPiece = length == group.TopExclusive - group.DetachCursor;
            uint generation = _gravityJournal.Generations.TryGetValue(column, out uint staged) ? staged
                : _landingGeneration.TryGetValue(column, out uint committed) ? committed : 0;
            var motionId = new GravityMotionId(group.Id, true);
            var proof = _gravityJournal.Motions.TryGetValue(motionId, out var stagedProof) ? stagedProof
                : _cohortMotionWork.TryGetValue(motionId, out var committedProof) ? committedProof : null;
            // A live lower is an extra motion cap, never an own floor certificate.
            if (finalPiece && (!group.LandingKnown || group.NeedsLandingProof || proof == null
                || !proof.ProofKnown || proof.Generation != generation || proof.NativeProof == null)) return false;
            if (finalPiece && !SpendGravity(ref checks, next.EstimateEditCohortWork(group.Id) + 16 + group.RebaseMotion.Length, maxChecks, timer, milliseconds)) return false;
            if (finalPiece) RetainOwnActivationProof(group.Id, proof);
            var run = next.DetachCohortPiece(group.Id, length, _now, out _);
            RetainGravitySupportAbsence(bottom, length, noStatic);
            detached = true;
            changed.UnionWith(keys); work.Scan = run.Cohort.DetachCursor;
            if (run.Cohort.Phase == FallingCohortPhase.AwaitActivation)
            {
                next.PrepareCohortForPublication(group.Id, out _);
                work.Cohort = 0; work.Ready = work.Proving = false;
            }
            return true;
        }
        int MotionCount => _cohortMotionOrder.Count + _gravityJournal.NewMotions.Count;
        GravityMotionId MotionAt(int index) => index < _cohortMotionOrder.Count ? _cohortMotionOrder[index] : _gravityJournal.NewMotions[index - _cohortMotionOrder.Count];
        bool StageCohortFalling(BlockWorld next, HashSet<Vector3Int> changed, ref int checks, Stopwatch timer, double milliseconds, int maxChecks)
        {
            IndexGravityWork(next, ref checks, timer, milliseconds, maxChecks);
            if (_cohortIndexPending) return false;
            bool mutation = false; int visits = MotionCount;
            while (visits > 0 && MotionCount != 0 && !_gravityJournal.MotionVisitBudgetDeferred
                && HasStageBudget(checks, maxChecks, timer, milliseconds))
            {
                int visitCursor = _gravityJournal.MotionCursor, startupCursor = CurrentGravityStartup().MotionCursor;
                int repairSelectionChecks = checks;
                bool repairVisit = TrySelectBuildOverlapRepairMotion(ref checks, maxChecks, timer, milliseconds, out var id);
                // Only a successfully selected repair repeats this fixed prefix.
                // Failed selection and prior indexing remain transient frame work.
                int repairSelectionCost = repairVisit ? checks - repairSelectionChecks : 0;
                bool startupVisit = !repairVisit && TrySelectStartupMotion(ref checks, maxChecks, timer, milliseconds, out id);
                if (!repairVisit && !startupVisit) { id = MotionAt(_gravityJournal.MotionCursor++ % MotionCount); visits--; }
                BeginGravityStartupVisit(true, repairVisit || startupVisit);
                int actualMotionChecks = checks, repairVisitChecks = checks; bool startupMotionEligible = false;
                BuildRepairProgress repairProgress = default;
                var dependentBefore = _gravityJournal.DependentEditRequest;
                var privateBefore = _gravityJournal.PrivateRequest;
                var recoveryBefore = _gravityJournal.RecoveryRequest;
                var headBefore = _gravityJournal.HeadPreparationColumn;
                _gravityJournal.MotionVisitActive = true;
                try
                {
                    var terminalWork = _gravityJournal.Motions.TryGetValue(id, out var stagedWork) ? stagedWork
                        : _cohortMotionWork.TryGetValue(id, out var committedWork) ? committedWork : null;
                    if (terminalWork != null && HistoricalRecoveryTerminal(terminalWork.Column))
                    {
                        if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) break;
                        continue;
                    }
                    // Capture only this motion's required fresh-journal entry. Other
                    // motions/indexing spent earlier in this frame are transient and
                    // must not turn an otherwise admissible edit into preparation.
                    int visitCost = 1 + EstimateMotionEditWork(id);
                    int freshVisitCost = visitCost + repairSelectionCost;
                    if (!SpendGravity(ref checks, visitCost, maxChecks, timer, milliseconds)) break;
                    var work = EditMotion(id); if (work == null) continue;
                    if (repairVisit) repairProgress = new BuildRepairProgress(work, next);
                    actualMotionChecks = checks;
                    FallingCohort group = null; FallingRun legacy = null;
                    if (id.Cohort) next.Cohorts.TryGetValue(id.Id, out group); else next.Falling.TryGetValue(id.Id, out legacy);
                    if (group == null && legacy == null)
                    {
                        if (!SpendGravity(ref checks, EstimateHeadEditWork(work.Column), maxChecks, timer, milliseconds)) break;
                        RemoveMotion(id, work); continue;
                    }
                    if (ReservedColumn(work.Column)) continue;
                    startupMotionEligible = true;
                    bool supportChanged = false; int bindingCost = 0;
                    if (group != null && !TryBindLowerSupport(next, id, group, work, out supportChanged, out bindingCost,
                        ref checks, timer, milliseconds, maxChecks, false, freshVisitCost)) continue;
                    if (group != null)
                    {
                        mutation |= supportChanged; group = next.Cohorts[id.Id];
                        if (supportChanged && _gravityJournal.BindingContinuity.ContainsKey(group.Id)) continue;
                        // Bind the parent before freezing its trajectory into private fracture children.
                        if (group.NeedsRegroup)
                        { mutation |= StageCohortRegroup(next, id, group, work, ref checks, timer, milliseconds, maxChecks); continue; }
                        if (group.NeedsLandingProof && work.ProofKnown) { work.ProofKnown = false; work.Phase = 0; }
                    }
                    if (group != null && group.Phase == FallingCohortPhase.Detaching)
                    {
                        mutation |= StageMotionLandingAndSettle(next, changed, id, group, null, work,
                            ref checks, timer, milliseconds, maxChecks, false, freshVisitCost + bindingCost);
                        group = next.Cohorts[id.Id];
                        // Every detaching child has its own motion entry; one column
                        // proof slot cannot overwrite or strand its siblings.
                        var detach = new GravityColumnWork { Cohort = group.Id, Bottom = group.Bottom.y, Top = group.TopExclusive, Ready = true, Scan = group.DetachCursor };
                        mutation |= TryDetachCohortPiece(next, changed, work.Column, detach, ref checks, timer, milliseconds, maxChecks, out _);
                        continue;
                    }
                    mutation |= StageMotionLandingAndSettle(next, changed, id, group, legacy, work,
                        ref checks, timer, milliseconds, maxChecks, false, freshVisitCost + bindingCost);
                }
                finally
                {
                    _gravityJournal.MotionVisitActive = false;
                    ObserveBuildOverlapRepairVisit(id, repairVisit, startupVisit, startupMotionEligible, repairVisitChecks, actualMotionChecks, checks, repairProgress,
                        dependentBefore, privateBefore, recoveryBefore, headBefore);
                    if (startupMotionEligible && checks > actualMotionChecks) ObserveGravityStartupMotionServed(id);
                    EndGravityStartupVisit();
                    if (_gravityJournal.MotionVisitBudgetDeferred)
                    {
                        // An explicitly requested finite owner runs before the
                        // next ordinary stage. Geometry/state waits still rotate.
                        bool preparation = !ReferenceEquals(dependentBefore, _gravityJournal.DependentEditRequest)
                            || !ReferenceEquals(privateBefore, _gravityJournal.PrivateRequest)
                            || !ReferenceEquals(recoveryBefore, _gravityJournal.RecoveryRequest)
                            || headBefore != _gravityJournal.HeadPreparationColumn;
                        if (preparation) _gravityJournal.MotionVisitBudgetDeferred = false;
                        else if (startupVisit)
                        { _gravityJournal.Startup.MotionCursor = startupCursor; _gravityJournal.Startup.MotionTurn = true; }
                        else if (!repairVisit) { _gravityJournal.MotionCursor = visitCursor; _gravityJournal.Startup.MotionTurn = false; }
                    }
                }
            }
            return mutation;
        }
        // Lowest source member ordering is preserved for all motion units. Index
        // visits are bounded; this helper never enumerates the whole World.
        bool IsMotionColumnHead(GravityMotionId id, GravityMotionWork work, ref int checks, Stopwatch timer, double milliseconds, int maxChecks)
        {
            if (_cohortIndexPending || !SpendGravity(ref checks, 16, maxChecks, timer, milliseconds)) return false;
            var heads = MotionHeads(work.Column);
            return heads != null && heads.Count != 0 && heads.Min.Motion.Equals(id);
        }
        bool TryBindLowerSupport(BlockWorld next, GravityMotionId id, FallingCohort group, GravityMotionWork work,
            out bool changed, out int bindingCost, ref int checks, Stopwatch timer, double milliseconds, int maxChecks,
            bool preparationOwner = false, int freshVisitCost = 0)
        {
            changed = false; bindingCost = 0;
            if (group.Phase == FallingCohortPhase.Settling) return true;
            if (!SpendGravity(ref checks, 16, maxChecks, timer, milliseconds)) return false;
            bindingCost = 16;
            var heads = MotionHeads(work.Column);
            if (heads == null) { if (preparationOwner) _gravityJournal.DependentEditDeferred = true; return false; }
            var key = new GravityMotionKey(work.SourceBottom, id);
            ulong lowerId = 0;
            var minimum = new GravityMotionKey(int.MinValue, new GravityMotionId(0, false));
            foreach (var predecessor in heads.GetViewBetween(minimum, key).Reverse())
            {
                if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return false;
                bindingCost++;
                if (predecessor.Motion.Equals(id)) continue;
                if (predecessor.Motion.Cohort)
                {
                    if (!next.Cohorts.TryGetValue(predecessor.Motion.Id, out var lower)
                        || lower.Id == group.Id || lower.Bottom.x != group.Bottom.x || lower.Bottom.z != group.Bottom.z
                        || lower.TopExclusive > group.Bottom.y) continue;
                    lowerId = predecessor.Motion.Id; break;
                }
                // Legacy direct runs retain source-head ordering. A new cohort
                // stays dormant until the lower legacy run has actually settled.
                if (next.Falling.ContainsKey(predecessor.Motion.Id))
                { if (preparationOwner) _gravityJournal.DependentEditDeferred = true; return false; }
            }
            if ((group.LowerSupport?.Id ?? 0) == lowerId) return true;
            // A source-address predecessor that would relocate the current
            // published body is not a valid new physical support. The original
            // trajectory and its own stable/static proof continue instead.
            if (GravityBindingWasRejected(group.Id, lowerId)) return true;
            // A removed lower descriptor has an immutable retained motion bound.
            // Keep that conservative bound until a complete new landing proof.
            if (lowerId == 0 && group.RetainedSupportMotion.Length != 0) return true;
            int cost = next.EstimateSetCohortSupportWork(group.Id) + 4;
            // The predecessor query repeats before this exact edit in every
            // fresh stage, including its self visit and any phantom heads. Use
            // that unavoidable prefix, never the frame's remaining budget.
            if (cost + freshVisitCost + bindingCost > maxChecks)
            { RequestDependentEditPreparation(id, work.Column, false); return false; }
            if (!SpendGravity(ref checks, cost, maxChecks, timer, milliseconds)) return false;
            float clearance = lowerId == 0 ? 0 : (group.Bottom.y - next.Cohorts[lowerId].TopExclusive) * BlockWorld.CellSize;
            next.SetCohortSupport(group.Id, lowerId, clearance, out _); changed = true;
            RecordGravityBindingContinuity(next, group, work.Column);
            work.ProofKnown = false; work.Phase = 0; return true;
        }
        // Constant-time conservative bound; longer live chains retain the validated maximum.
        static int CohortOffsetWorkUpperBound(FallingCohort group)
        {
            if (group == null) return 1;
            var lower = group.LowerSupport;
            if (lower == null) return 1 + group.RetainedSupportMotion.Length;
            if (lower.LowerSupport == null) return 2 + lower.RetainedSupportMotion.Length;
            return BlockWorld.MaxCohortCells;
        }

        bool StageMotionLandingAndSettle(BlockWorld next, HashSet<Vector3Int> changed, GravityMotionId id, FallingCohort group,
            FallingRun legacy, GravityMotionWork work, ref int checks, Stopwatch timer, double milliseconds, int maxChecks,
            bool landingOnly = false, int freshPrefixCost = 0)
        {
            bool mutation = false;
            uint generation = _gravityJournal.Generations.TryGetValue(work.Column, out uint staged) ? staged
                : _landingGeneration.TryGetValue(work.Column, out uint committed) ? committed : 0;
            if (work.Generation != generation && (group == null || group.Phase != FallingCohortPhase.Settling))
            { work.ProofKnown = false; work.Phase = 0; work.Generation = generation; }
            if (!work.ProofKnown)
            {
                // Every motion keeps its own real stable/static cap even when
                // a live lower motion supplies an additional future constraint.
                int bottom = group != null ? group.Bottom.y : legacy.Bottom.y;
                int first = 0;
                if (legacy != null)
                {
                    while (first < legacy.Cells.Length && !legacy.Cells[first].Occupied)
                    { if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return false; first++; }
                    bottom += first;
                    if (work.SourceBottom != bottom)
                    {
                        if (!SpendGravity(ref checks, EstimateHeadEditWork(work.Column) + 24, maxChecks, timer, milliseconds)) return false;
                        var heads = EditHeads(work.Column); heads.Remove(new GravityMotionKey(work.SourceBottom, id));
                        work.SourceBottom = bottom; heads.Add(new GravityMotionKey(bottom, id));
                        work.Phase = 0;
                        if (!IsMotionColumnHead(id, work, ref checks, timer, milliseconds, maxChecks)) return false;
                    }
                }
                if (work.Phase == 0)
                {
                    // Offset traverses at most the validated 400-node live plus
                    // retained chain; reserve it before observing the trajectory.
                    if (!SpendGravity(ref checks, CohortOffsetWorkUpperBound(group) + 67, maxChecks, timer, milliseconds)) return false;
                    float offset = group != null ? group.Offset(_now) : legacy.Offset(_now);
                    var position = BlockWorld.Center(new Vector3Int(work.Column.x, bottom, work.Column.y)) - Vector3.up * offset;
                    if (!QueryStaticProof(position, next.Bottom, out work.Minimum)) return false;
                    work.NativeProof = new GravityStaticProof(position, next.Bottom, work.Minimum);
                    work.Scan = Math.Min(bottom, Mathf.FloorToInt(position.y / BlockWorld.CellSize)); work.Landing = work.Minimum; work.Phase = 1;
                }
                while (work.Phase == 1 && HasStageBudget(checks, maxChecks, timer, milliseconds))
                {
                    if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return mutation;
                    if (work.Scan < work.Minimum) { work.Phase = 2; work.Validate = 0; break; }
                    if (TryGravityLandingRow(next, work.Column, work.Scan, work.Minimum, group, out int support, out int nextScan))
                    { work.Landing = support + 1; work.Phase = 2; work.Validate = 0; break; }
                    work.Scan = nextScan;
                }
                int span = group != null ? group.TopExclusive - group.Bottom.y : legacy.Cells.Length - first;
                while (work.Phase == 2 && work.Validate < span && HasStageBudget(checks, maxChecks, timer, milliseconds))
                {
                    if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return mutation;
                    var destination = new Vector3Int(work.Column.x, work.Landing + work.Validate, work.Column.y);
                    if (group != null)
                    {
                        // Dense cohort payloads cover every offset. A legacy run
                        // may contain holes, so it keeps its original cell path below.
                        int localY = destination.y & 15;
                        int length = Math.Min(span - work.Validate, 16 - localY);
                        uint occupied = (uint)(next.OccupiedRow(destination, 1) >> localY) & ((1u << length) - 1);
                        if (group.Phase == FallingCohortPhase.Detaching)
                        {
                            int firstOwn = Math.Max(destination.y, group.DetachCursor);
                            int lastOwn = Math.Min(destination.y + length, group.TopExclusive);
                            if (firstOwn < lastOwn)
                                occupied &= ~(((1u << (lastOwn - firstOwn)) - 1) << (firstOwn - destination.y));
                        }
                        // The original first-conflict expression also increases
                        // Landing by exactly one and restarts Validate at zero.
                        if (occupied != 0) { work.Landing++; work.Validate = 0; }
                        else work.Validate += length;
                    }
                    else if (legacy.Cells[work.Validate + first].Occupied && next.TryGet(destination, out _))
                    { work.Landing = destination.y + 1 - work.Validate; work.Validate = 0; }
                    else work.Validate++;
                    if (work.Landing > bottom)
                    {
                        // An existing illegal published body must enter finite
                        // classification even when no normal landing span fits.
                        if (legacy != null || group != null && (group.Phase == FallingCohortPhase.Falling || group.Phase == FallingCohortPhase.Settling))
                            RequestHistoricalRecovery(id, work.Column);
                        work.Phase = 0; return mutation;
                    } // Never resolve overlap by an upward/negative fall.
                }
                if (work.Phase != 2 || work.Validate < span) return mutation;
                int editCost = group != null ? next.EstimateSetCohortSupportWork(id.Id) : next.EstimateEditFallingWork(id.Id);
                int completeEdit = editCost + (group != null ? BlockWorld.MaxCohortCells + 4 : 5);
                // Proof scan cursors persist, but entering this motion and its
                // binding query cannot be skipped on a fresh ordinary stage.
                // The private owner already owns its motion and passes zero.
                if (group != null && completeEdit + freshPrefixCost > maxChecks)
                { RequestDependentEditPreparation(id, work.Column, true); return mutation; }
                if (!SpendGravity(ref checks, completeEdit, maxChecks, timer, milliseconds)) return mutation;
                float clamp = (bottom - work.Landing) * BlockWorld.CellSize;
                if ((group != null && (group.Phase == FallingCohortPhase.Falling || group.Phase == FallingCohortPhase.Settling) || legacy != null)
                    && (group != null ? group.Offset(_now) : legacy.Offset(_now)) > clamp + .000001f)
                { RequestHistoricalRecovery(id, work.Column); work.Phase = 0; return mutation; }
                if (group != null)
                {
                    // Releasing a retained support or extending an old floor cap
                    // must not expose free fall accumulated behind that constraint.
                    // Rebase the published source at actual publication, just as
                    // for a new live lower edge; do not settle its unpublished pose.
                    if (group.RetainedSupportMotion.Length != 0 || !group.LandingKnown || group.LandingOffset != clamp)
                        RecordGravityBindingContinuity(next, group, work.Column);
                    // Head proof describes real stable/static support. Clearing an
                    // obsolete retained bound and the new clamp are one owned edit.
                    if (group.LowerSupport == null) next.SetCohortSupport(group.Id, 0, 0, out _);
                    else
                    {
                        group = next.EditCohort(group.Id, out _);
                        group.RetainedSupportMotion = Array.Empty<FrozenCohortMotion>(); group.RetainedSupportClearance = 0; group.NeedsLandingProof = false;
                    }
                    group = next.Cohorts[id.Id]; group.LandingKnown = true; group.LandingOffset = clamp;
                }
                else
                {
                    if (!legacy.LandingKnown || legacy.LandingOffset != clamp)
                        RecordLegacyLandingContinuity(next, legacy, work.Column);
                    legacy = next.EditFalling(id.Id); legacy.LandingKnown = true; legacy.LandingOffset = clamp;
                }
                work.ProofKnown = true; work.Generation = generation; mutation = true;
                ObserveGravityStartupProof(id);
                RetainGravityStaticProof(work.NativeProof);
            }
            // The private owner completes only the originally requested cap.
            // Activation/detachment/settlement retain their ordinary stages.
            if (landingOnly) return mutation;
            if (group != null && _gravityJournal.BindingContinuity.ContainsKey(group.Id)
                || legacy != null && _gravityJournal.LegacyContinuity.ContainsKey(legacy.Id)) return mutation;
            if (group != null && group.Phase == FallingCohortPhase.AwaitActivation)
            {
                if (!group.LandingKnown || group.NeedsLandingProof || !work.ProofKnown || work.NativeProof == null) return mutation;
                int prepare = next.EstimatePrepareCohortForPublicationWork(group.Id) + 2;
                if (SpendGravity(ref checks, prepare, maxChecks, timer, milliseconds))
                { RetainOwnActivationProof(group.Id, work); next.PrepareCohortForPublication(group.Id, out _); mutation = true; }
                return mutation;
            }
            if (group != null && group.Phase == FallingCohortPhase.Detaching) return mutation;
            if (!SpendGravity(ref checks, CohortOffsetWorkUpperBound(group), maxChecks, timer, milliseconds)) return mutation;
            float displacement = group != null ? group.LandingOffset : legacy.LandingOffset;
            float current = group != null ? group.Offset(_now) : legacy.Offset(_now);
            if (current + .001f < displacement) return mutation;
            if (!IsMotionColumnHead(id, work, ref checks, timer, milliseconds, maxChecks)) return mutation;
            FallingRun head = legacy;
            if (group != null)
            {
                foreach (ulong member in group.MemberIds)
                {
                    if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return mutation;
                    if (!next.Falling.TryGetValue(member, out var candidate)) continue;
                    if (head == null || candidate.Bottom.y < head.Bottom.y || candidate.Bottom.y == head.Bottom.y && candidate.Id < head.Id) head = candidate;
                }
            }
            if (head == null)
            {
                if (SpendGravity(ref checks, EstimateHeadEditWork(work.Column), maxChecks, timer, milliseconds)) RemoveMotion(id, work);
                return mutation;
            }
            var landingBottom = head.Bottom + Vector3Int.down * Mathf.RoundToInt(displacement / BlockWorld.CellSize);
            int cost = next.EstimateSettleWork(head.Id) + head.Cells.Length + EstimateHeadEditWork(work.Column) + EstimateQueueGravityWork(landingBottom);
            if (!SpendGravity(ref checks, cost, maxChecks, timer, milliseconds)) return mutation;
            var keys = new HashSet<Vector3Int>(changed);
            for (int i = 0; i < head.Cells.Length; i++) if (head.Cells[i].Occupied) IncludeCell(keys, landingBottom + Vector3Int.up * i);
            if (keys.Count > _gravityJournal.Window.MaximumChangedKeys) return mutation;
            if (next.Settle(head.Id, landingBottom, changed, out _))
            {
                changed.UnionWith(keys); _gravityJournal.Checked[head.Id] = generation;
                QueueGravityCohortCell(landingBottom);
                if (group == null || !next.Cohorts.ContainsKey(id.Id)) RemoveMotion(id, work);
                mutation = true;
            }
            return mutation;
        }
        bool StageCohortRegroup(BlockWorld next, GravityMotionId id, FallingCohort group, GravityMotionWork work,
            ref int checks, Stopwatch timer, double milliseconds, int maxChecks)
        {
            // The scan is resumable and keyed to the current group's immutable
            // membership; no world-wide scan is hidden inside one target removal.
            uint generation = _gravityJournal.Generations.TryGetValue(work.Column, out uint staged) ? staged
                : _landingGeneration.TryGetValue(work.Column, out uint committed) ? committed : 0;
            if (work.Phase != 3 || work.Generation != generation || work.MembershipRevision != group.MembershipRevision
                || work.Replacement != null && !work.Replacement.IsValid(next))
            { work.Phase = 3; work.Generation = generation; work.MembershipRevision = group.MembershipRevision; work.Replacement = null;
                work.RegroupY = group.Bottom.y; work.Spans.Clear(); work.Pieces.Clear(); work.SpanBottom = int.MinValue; work.Member = work.Index = 0; work.PieceRun = 0; work.PieceCount = 0; }
            while (work.RegroupY < group.TopExclusive && HasStageBudget(checks, maxChecks, timer, milliseconds))
            {
                // Finding a member is bounded by group member count and charged.
                bool occupied = false; ulong runId = 0; int index = 0;
                if (work.RegroupY >= group.DetachCursor)
                    occupied = next.TryGet(new Vector3Int(group.Bottom.x, work.RegroupY, group.Bottom.z), out var stable) && !stable.Anchored;
                else
                {
                    for (int m = work.Member; m < group.MemberIds.Count; m++)
                    {
                        if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) { work.Member = m; return false; }
                        if (!next.Falling.TryGetValue(group.MemberIds[m], out var run)) continue;
                        int cellIndex = work.RegroupY - run.Bottom.y;
                        if (cellIndex < 0 || cellIndex >= run.Cells.Length) continue;
                        if (run.Cells[cellIndex].Occupied) { occupied = true; runId = run.Id; index = cellIndex; break; }
                    }
                    work.Member = 0;
                }
                if (!SpendGravity(ref checks, 1, maxChecks, timer, milliseconds)) return false;
                if (occupied)
                {
                    if (work.SpanBottom == int.MinValue) work.SpanBottom = work.RegroupY;
                    if (runId != 0)
                    {
                        if (work.PieceRun == runId && work.PieceFirst + work.PieceCount == index) work.PieceCount++;
                        else { FlushRegroupPiece(work); work.PieceRun = runId; work.PieceFirst = index; work.PieceCount = 1; }
                    }
                }
                else if (work.SpanBottom != int.MinValue)
                {
                    if (!SpendGravity(ref checks, work.Pieces.Count + 3, maxChecks, timer, milliseconds)) return false;
                    FlushRegroupSpan(group, work, work.RegroupY);
                }
                work.RegroupY++;
            }
            if (work.RegroupY < group.TopExclusive) return false;
            if (work.SpanBottom != int.MinValue)
            {
                if (!SpendGravity(ref checks, work.Pieces.Count + 3, maxChecks, timer, milliseconds)) return false;
                FlushRegroupSpan(group, work, group.TopExclusive);
            }
            if (work.Replacement == null)
            {
                int begin = next.EstimateBeginCohortReplacementWork(group.Id, work.Spans);
                if (!SpendGravity(ref checks, begin, maxChecks, timer, milliseconds)) return false;
                work.Replacement = next.BeginCohortReplacement(group.Id, work.Spans, out _);
            }
            while (!work.Replacement.Ready && HasStageBudget(checks, maxChecks, timer, milliseconds))
            {
                int step = work.Replacement.EstimateNextStepWork();
                if (!SpendGravity(ref checks, step, maxChecks, timer, milliseconds)) return false;
                work.Replacement.Step(next, step, out _);
                if (work.Replacement.Failed) { work.Phase = 0; work.Replacement = null; return false; }
            }
            if (!work.Replacement.Ready) return false;
            // Metadata installation runs in a private transaction under the same
            // 1024/4096 admission. Its partial World never becomes a query source.
            // Keep the current parent trajectory visible until one complete final
            // publication rebases and activates every fully detached child.
            if (_gravityJournal.PrivateRequest == null && _privateGravityRequest == null && _privateGravityPreparation == null)
                _gravityJournal.PrivateRequest = new GravityPrivateRequest { Motion = id, Column = work.Column,
                    Replacement = work.Replacement, Window = _gravityJournal.Window, Epoch = _epoch, SourceTime = _now };
            return false;
        }
        static void FlushRegroupPiece(GravityMotionWork work)
        {
            if (work.PieceRun != 0) work.Pieces.Add(new FallingCohortPiece(work.PieceRun, work.PieceFirst, work.PieceCount));
            work.PieceRun = 0; work.PieceCount = 0;
        }
        static void FlushRegroupSpan(FallingCohort group, GravityMotionWork work, int top)
        {
            FlushRegroupPiece(work);
            var bottom = new Vector3Int(group.Bottom.x, work.SpanBottom, group.Bottom.z);
            work.Spans.Add(new FallingCohortSpan(bottom, top, Math.Max(bottom.y, Math.Min(group.DetachCursor, top)), work.Pieces.ToArray()));
            work.Pieces.Clear(); work.SpanBottom = int.MinValue;
        }
    }
}
