using System;
using System.Collections.Generic;

namespace Splatoon.Blocks
{
    /// <summary>
    /// A finite dependent-metadata COW on one exclusively owned unpublished
    /// draft. No trajectory, payload, count or semantic mutation is performed.
    /// Its one-shot completion belongs to the exact draft mutation and target;
    /// publishing or sharing this draft while Step is active is not permitted.
    /// </summary>
    public sealed class BlockCohortEditPreparation : IDisposable
    {
        public BlockWorld DraftWorld { get; }
        public ulong TargetId { get; }
        public uint SourceRevision { get; }
        public uint SourceRound { get; }
        public ulong SourceMutationGeneration { get; }
        public bool Ready { get; private set; }
        public bool Failed { get; private set; }
        public long ProcessedWork { get; private set; }
        public long MaximumWork => 64L + BlockWorld.MaxCohortCells
            + BlockWorld.MaxCohortCells * (64L + 13L * BlockWorld.MaxCohortCells);
        public ulong ProgressVersion { get; private set; }
        readonly LinkedList<ulong> _queue = new(), _affected = new();
        readonly HashSet<ulong> _seen = new();
        readonly int _columnX, _columnZ;
        readonly int _targetBottom, _targetTop, _targetCursor;
        readonly ulong _targetMembership;
        int _phase, _member, _releaseDepth;
        FallingCohort _source, _current;
        bool _copyMembers, _upperOpened;
        IEnumerator<ulong> _uppers;
        LinkedListNode<ulong> _link;
        PreparedRebaseSample _release;
        ulong _preparationVersion;

        internal BlockCohortEditPreparation(BlockWorld draft, ulong id)
        {
            if (draft == null || !draft.Cohorts.TryGetValue(id, out var target))
                throw new ArgumentException("A dependent edit needs a live target in its private draft.", nameof(id));
            DraftWorld = draft; TargetId = id; SourceRevision = draft.Revision; SourceRound = draft.Round;
            SourceMutationGeneration = draft.MutationGeneration; _columnX = target.Bottom.x; _columnZ = target.Bottom.z;
            _targetBottom = target.Bottom.y; _targetTop = target.TopExclusive; _targetCursor = target.DetachCursor;
            _targetMembership = target.MembershipRevision;
            _preparationVersion = draft.DependentPreparationVersion;
        }
        public bool IsValid(BlockWorld draft)
            => !Failed && ReferenceEquals(draft, DraftWorld) && draft.Revision == SourceRevision
                && draft.Round == SourceRound && draft.MutationGeneration == SourceMutationGeneration
                && draft.DependentPreparationVersion == _preparationVersion && draft.OwnsDependentEditPreparation(this)
                && draft.Cohorts.TryGetValue(TargetId, out var target) && target.MembershipRevision == _targetMembership
                && target.Bottom.x == _columnX && target.Bottom.z == _columnZ && target.Bottom.y == _targetBottom
                && target.TopExclusive == _targetTop && target.DetachCursor == _targetCursor;
        public int EstimateNextStepWork()
        {
            if (_phase == 0) return BlockWorld.MaxCohortCells + 4;
            if (_phase == 1 && _current == null && _queue.First != null
                && DraftWorld.Cohorts.TryGetValue(_queue.First.Value, out var next))
                return 8 + (DraftWorld._ownedCohorts.Contains(next.Id) ? 0 : next.Members.Count);
            return 6;
        }
        public bool Step(BlockWorld draft, int maxWork, out int work)
        {
            if (maxWork < 0) throw new ArgumentOutOfRangeException(nameof(maxWork));
            work = 0;
            if (!IsValid(draft)) { Cancel(); return false; }
            if (Ready) return true;
            try
            {
                while (maxWork - work >= EstimateNextStepWork())
                {
                    int cost = EstimateNextStepWork();
                    switch (_phase)
                    {
                        case 0:
                            // Charge the finite capacity before allocating it.
                            // Linked nodes avoid an uncharged queue/list resize.
                            _seen.EnsureCapacity(BlockWorld.MaxCohortCells); _queue.AddLast(TargetId); _phase = 1; break;
                        case 1:
                            if (_current == null)
                            {
                                if (_queue.First == null) { _link = _affected.First; _phase = 2; break; }
                                ulong id = _queue.First.Value; _queue.RemoveFirst();
                                if (!_seen.Add(id)) break;
                                if (_seen.Count > BlockWorld.MaxCohortCells || !draft.Cohorts.TryGetValue(id, out _source)
                                    || _source.Bottom.x != _columnX || _source.Bottom.z != _columnZ
                                    || _source.Members.Count > BlockWorld.MaxCohortCells)
                                    throw new InvalidOperationException("Dependent preparation crossed its finite source column.");
                                _copyMembers = !draft._ownedCohorts.Contains(id);
                                _current = _copyMembers ? _source.CloneDescriptor() : _source;
                                if (_copyMembers)
                                {
                                    // Capacity allocation was included in this admission.
                                    _current.Members.Capacity = _source.Members.Count; draft.Cohorts[id] = _current;
                                }
                                _release = draft.BeginBudgetedUnprepareCohort(id);
                                _releaseDepth = 0;
                                _member = 0; _upperOpened = false; break;
                            }
                            if (_release != null)
                            {
                                if (++_releaseDepth > BlockWorld.MaxCohortCells)
                                    throw new InvalidOperationException("Prepared publication prefix exceeded its finite motion depth.");
                                _release = draft.ReleasePreparedSampleStep(_release); break;
                            }
                            if (_member < _source.Members.Count)
                            {
                                ulong member = _source.Members[_member++];
                                if (_copyMembers) _current.Members.Add(member);
                                if (!draft.Falling.TryGetValue(member, out var run))
                                    throw new InvalidOperationException("Dependent preparation encountered an orphan storage member.");
                                if (draft._ownedRuns.Add(member)) draft.Falling[member] = run = run.Clone();
                                run.Cohort = _current; break;
                            }
                            if (!_upperOpened)
                            {
                                if (draft._upperSupports.TryGetValue(_current.Id, out var uppers))
                                {
                                    if (uppers.Count > BlockWorld.MaxCohortCells)
                                        throw new InvalidOperationException("Dependent preparation exceeded its finite reverse edge count.");
                                    _uppers = uppers.GetEnumerator();
                                }
                                _upperOpened = true; break;
                            }
                            if (_uppers != null && _uppers.MoveNext())
                            {
                                ulong upperId = _uppers.Current;
                                if (!draft.Cohorts.TryGetValue(upperId, out var upper) || upper.LowerSupport?.Id != _current.Id)
                                    throw new InvalidOperationException("The reverse dependent edge changed during preparation.");
                                _queue.AddLast(upperId); break;
                            }
                            _uppers?.Dispose(); _uppers = null;
                            _affected.AddLast(_current.Id); _source = _current = null; break;
                        case 2:
                            if (_link == null) { _link = _affected.First; _phase = 3; break; }
                            var cohort = draft.Cohorts[_link.Value];
                            if (cohort.LowerSupport != null) cohort.LowerSupport = draft.Cohorts[cohort.LowerSupport.Id];
                            _link = _link.Next; break;
                        case 3:
                            if (_link == null) { _phase = 4; break; }
                            draft._ownedCohorts.Add(_link.Value); _link = _link.Next; break;
                        default:
                            Ready = true; draft.RegisterPreparedDependentEdit(this); break;
                    }
                    _preparationVersion = draft.DependentPreparationVersion;
                    work += cost; ProcessedWork += cost; ProgressVersion++;
                    if (ProcessedWork > MaximumWork) throw new InvalidOperationException("Dependent preparation exceeded its finite admitted work.");
                    if (Ready) break;
                }
                return Ready;
            }
            catch
            {
                Cancel(); throw;
            }
        }
        public void Cancel()
        {
            Failed = true; DraftWorld.DiscardPreparedDependentEdit(this); Dispose();
        }
        public void Dispose()
        {
            _uppers?.Dispose(); _uppers = null;
            if (!Ready && !Failed) { Failed = true; DraftWorld.DiscardPreparedDependentEdit(this); }
        }
    }
}
