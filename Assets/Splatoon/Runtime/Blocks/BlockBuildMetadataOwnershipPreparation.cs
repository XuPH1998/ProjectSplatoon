using System;
using System.Collections.Generic;

namespace Splatoon.Blocks
{
    public readonly struct BlockBuildMetadataWriteTarget
    {
        public readonly ulong Id;
        public readonly bool Cohort;
        public BlockBuildMetadataWriteTarget(ulong id, bool cohort) { Id = id; Cohort = cohort; }
    }

    /// <summary>
    /// Candidate primitive only; not connected to terrain publication.
    /// Serial metadata COW on an exclusively owned unpublished draft with no
    /// prepared activation. The caller supplies a frozen, finite scalar stream
    /// containing every cap and continuity writer and discards the entire draft
    /// on failure/cancellation. No world metadata enumeration is performed here.
    /// A ready result is the same draft; journal/native proof identity is retained.
    /// </summary>
    public sealed class BlockBuildMetadataOwnershipPreparation : IDisposable
    {
        public BlockWorld SourceWorld { get; }
        public BlockWorld DraftWorld { get; }
        public bool Ready { get; private set; }
        public bool Failed { get; private set; }
        public long ProcessedWork { get; private set; }
        public ulong ProgressVersion { get; private set; }
        public int TargetsVisited { get; private set; }
        public long MaximumWork => 64L + _maximumTargets * (2L * DependentMaximumWork + 64);
        static long DependentMaximumWork => 64L + BlockWorld.MaxCohortCells
            + BlockWorld.MaxCohortCells * (64L + 13L * BlockWorld.MaxCohortCells);
        readonly uint _sourceRevision, _sourceRound, _draftRevision, _draftRound;
        readonly ulong _sourceMutation;
        readonly int _maximumTargets;
        ulong _draftMutation, _preparationVersion;
        IEnumerator<BlockBuildMetadataWriteTarget> _targets;
        BlockCohortEditPreparation _dependent;
        bool _taken;

        // The caller admits this constant-size creation and owns the stream
        // exclusively; enumeration must yield scalar records, with no callbacks.
        public BlockBuildMetadataOwnershipPreparation(BlockWorld source, BlockWorld draft,
            IEnumerable<BlockBuildMetadataWriteTarget> targets, int maximumTargets)
        {
            if (source == null || draft == null || targets == null || ReferenceEquals(source, draft)
                || maximumTargets < 0 || source.DependentEditIncomplete || draft.DependentEditIncomplete
                || draft.EstimatedPreparedPublicationWork != 0)
                throw new ArgumentException("Requires a distinct exclusive draft without prepared activation.");
            SourceWorld = source; DraftWorld = draft; _maximumTargets = maximumTargets;
            _sourceRevision = source.Revision; _sourceRound = source.Round; _sourceMutation = source.MutationGeneration;
            _draftRevision = draft.Revision; _draftRound = draft.Round;
            _draftMutation = draft.MutationGeneration; _preparationVersion = draft.DependentPreparationVersion;
            _targets = targets.GetEnumerator();
        }

        public bool IsValid(BlockWorld source, BlockWorld draft)
            => !Failed && !_taken && ReferenceEquals(source, SourceWorld) && ReferenceEquals(draft, DraftWorld)
                && !source.DependentEditIncomplete && source.Revision == _sourceRevision && source.Round == _sourceRound && source.MutationGeneration == _sourceMutation
                && draft.Revision == _draftRevision && draft.Round == _draftRound && draft.MutationGeneration == _draftMutation
                && draft.DependentPreparationVersion == _preparationVersion && draft.EstimatedPreparedPublicationWork == 0
                && (_dependent != null ? _dependent.IsValid(draft) : !draft.DependentEditIncomplete);

        public int EstimateNextStepWork() => _dependent == null ? 8 : checked(_dependent.EstimateNextStepWork() + 4);

        public bool Step(BlockWorld source, BlockWorld draft, int maxWork, out int work)
        {
            if (maxWork < 0) throw new ArgumentOutOfRangeException(nameof(maxWork));
            work = 0;
            if (!IsValid(source, draft)) { Cancel(); return false; }
            if (Ready) return true;
            try
            {
                while (maxWork - work >= EstimateNextStepWork())
                {
                    int cost = EstimateNextStepWork();
                    if (_dependent != null)
                    {
                        ulong progress = _dependent.ProgressVersion;
                        _dependent.Step(draft, cost - 4, out int actual);
                        if (actual <= 0 || actual > cost - 4 || _dependent.Failed || _dependent.ProgressVersion == progress)
                            throw new InvalidOperationException("Dependent metadata failed its admitted step.");
                        _preparationVersion = draft.DependentPreparationVersion;
                        if (_dependent.Ready) { _dependent.Dispose(); _dependent = null; }
                    }
                    else if (!_targets.MoveNext())
                    {
                        _targets.Dispose(); _targets = null; Ready = true;
                    }
                    else
                    {
                        if (++TargetsVisited > _maximumTargets || !IsValid(source, draft))
                            throw new InvalidOperationException("Frozen metadata target stream changed or exceeded its bound.");
                        var target = _targets.Current;
                        if (target.Id == 0) throw new InvalidOperationException("A metadata writer needs a live identity.");
                        if (target.Cohort)
                        {
                            if (!draft.Cohorts.ContainsKey(target.Id))
                                throw new InvalidOperationException("A cohort writer disappeared from the frozen draft.");
                            if (!draft._ownedCohorts.Contains(target.Id))
                                _dependent = draft.BeginBudgetedCohortEdit(target.Id);
                        }
                        else
                        {
                            if (!draft.Falling.TryGetValue(target.Id, out var run) || run.Cohort != null)
                                throw new InvalidOperationException("A legacy writer disappeared or changed kind.");
                            int estimate = draft.EstimateEditFallingWork(target.Id);
                            if (estimate > 1) throw new InvalidOperationException("A legacy ownership edit is not bounded.");
                            draft.EditFalling(target.Id, out int actual);
                            if (actual > estimate) throw new InvalidOperationException("Legacy ownership exceeded its admission.");
                            _draftMutation = draft.MutationGeneration;
                        }
                    }
                    if (!IsValid(source, draft)) throw new InvalidOperationException("Metadata ownership lost its exact world token.");
                    work += cost; ProcessedWork += cost; ProgressVersion++;
                    if (ProcessedWork > MaximumWork) throw new InvalidOperationException("Metadata ownership exceeded its finite bound.");
                    if (Ready) break;
                }
                return Ready;
            }
            catch { Cancel(); throw; }
        }

        public BlockWorld TakeWorld(BlockWorld source, BlockWorld draft)
        {
            if (!Ready || !IsValid(source, draft)) throw new InvalidOperationException("Metadata ownership is not complete for this draft.");
            _taken = true; return DraftWorld;
        }
        public void Cancel()
        {
            Failed = true; _dependent?.Cancel(); _dependent = null;
            _targets?.Dispose(); _targets = null;
        }
        public void Dispose()
        {
            if (!Ready && !Failed) Cancel();
            else { _targets?.Dispose(); _targets = null; }
        }
    }
}
