using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // One source-bound service turn after a real build/moving overlap.
        // This owner never changes a paid command, proof, reservation or lease.
        sealed class BuildOverlapRepair
        {
            public uint Epoch, Round, Revision;
            public ulong Mutation;
            public BlockWorld Source;
            public Command Head;
            public BlockPublicationWindow Window;
            public GravityMotionId Motion, ServiceMotion;
            public Vector2Int Column;
            public ulong SourceRun;
            public int SourceIndex;
            public Vector3Int SourceCell, ConflictCell;
            public object Preparation;
            public bool Dependency, HeadPending;
        }
        sealed class BuildRepairVisit
        {
            public BuildOverlapRepair Owner;
            public GravityMotionId Motion;
            public bool Dependency, Served, Blocked, HeadPending;
            public object Preparation;
        }
        readonly struct BuildRepairProgress
        {
            readonly GravityMotionWork Work;
            readonly ulong Mutation, Membership;
            readonly int Phase, Scan, Landing, Validate, Member, Index, Regroup, Spans, Pieces;
            readonly bool Proof;
            readonly GravityStaticProof Native;
            readonly FallingCohortReplacement Replacement;
            public BuildRepairProgress(GravityMotionWork work, BlockWorld next)
            {
                Work = work; Mutation = next.MutationGeneration; Membership = work.MembershipRevision;
                Phase = work.Phase; Scan = work.Scan; Landing = work.Landing; Validate = work.Validate;
                Member = work.Member; Index = work.Index; Regroup = work.RegroupY;
                Spans = work.Spans.Count; Pieces = work.Pieces.Count; Proof = work.ProofKnown;
                Native = work.NativeProof; Replacement = work.Replacement;
            }
            public bool Changed(BlockWorld next)
                => Work != null && (Mutation != next.MutationGeneration || Membership != Work.MembershipRevision
                    || Phase != Work.Phase || Scan != Work.Scan || Landing != Work.Landing || Validate != Work.Validate
                    || Member != Work.Member || Index != Work.Index || Regroup != Work.RegroupY
                    || Spans != Work.Spans.Count || Pieces != Work.Pieces.Count || Proof != Work.ProofKnown
                    || !ReferenceEquals(Native, Work.NativeProof) || !ReferenceEquals(Replacement, Work.Replacement));
        }
        BuildOverlapRepair _buildOverlapRepair, _stageBuildOverlapRepair;
        long _buildRepairArmed, _buildRepairPublished, _buildRepairNoChange;
        long _buildRepairNoReady, _buildRepairCancelled, _buildRepairRejected, _buildRepairFairDeclined;
        long _buildRepairTargetSelected, _buildRepairDependencySelected, _buildRepairTargetServed;
        long _buildRepairTargetBudgetDeferred, _buildRepairTargetBlocked, _buildRepairUnrelatedPublished;
        long _buildRepairPreparationHandoffs, _buildRepairPriorityDebt;

        void CancelBuildOverlapRepair()
        {
            if (_buildOverlapRepair != null) _buildRepairCancelled++;
            _buildOverlapRepair = _stageBuildOverlapRepair = null;
        }
        bool RefreshBuildOverlapRepair()
        {
            var repair = _buildOverlapRepair;
            if (repair == null) return false;
            // An active fair owner already controls its own foreground/background
            // dependency. This narrow repair never overrides that safety lease.
            if (!_authority || !_accepting || _destroying || _remoteQueued != null || _fairSeed != null
                || repair.Epoch != _epoch || repair.Round != Round || repair.Revision != Revision
                || !ReferenceEquals(repair.Source, World) || repair.Mutation != World.MutationGeneration
                || _commands.Count == 0 || !ReferenceEquals(_commands.Peek(), repair.Head)
                || repair.Head.Done || repair.Head.Completed || !ReferenceEquals(repair.Head.RetryWindow, repair.Window))
            { CancelBuildOverlapRepair(); return false; }
            return true;
        }
        void ArmBuildOverlapRepair(BuildOccupancyPreparation preparation)
        {
            // Called only AFTER the entire original checkpoint list is restored.
            if (!preparation.RepairAfterRetry || preparation.OverlapCell == null || !preparation.SourceValid(this)) return;
            if (_fairSeed != null) { CancelBuildOverlapRepair(); _buildRepairFairDeclined++; return; }
            if (!_authority || !_accepting || _destroying || _remoteQueued != null || _commands.Count == 0) return;
            var head = _commands.Peek();
            if (head.Done || head.Completed || !ReferenceEquals(head.RetryWindow, preparation.Window)
                || !preparation.Window.Includes(BlockPublicationOperation.Gravity)) return;
            CancelBuildOverlapRepair();
            _buildOverlapRepair = new BuildOverlapRepair
            {
                Epoch = _epoch, Round = Round, Revision = Revision, Mutation = World.MutationGeneration,
                Source = World, Head = head, Window = preparation.Window,
                Motion = preparation.OverlapCell.SourceMotion, Column = preparation.OverlapColumn,
                SourceRun = preparation.OverlapCell.SourceRun, SourceIndex = preparation.OverlapCell.SourceIndex,
                SourceCell = preparation.OverlapCell.SourceCell, ConflictCell = preparation.OverlapDestination
            };
            _buildRepairArmed++;
        }
        BuildOverlapRepair SelectBuildOverlapRepair()
        {
            if (!RefreshBuildOverlapRepair() || !ReferenceEquals(_stagePublicationWindow, _buildOverlapRepair.Window)) return null;
            return _buildOverlapRepair;
        }
        bool ProtectBuildOverlapRepairPending(Pending pending)
        {
            if (pending == null || !RefreshBuildOverlapRepair()) return false;
            var repair = _buildOverlapRepair;
            // Owners completing finite preparation attach their journal after
            // BeginWindowTransaction, so bind at the first pending observation.
            if (pending.Remote || pending.Gravity == null || pending.Results.Count != 0
                || (pending.CommandCheckpoints?.Count ?? 0) != 0 || pending.FairSeed != null
                || !ReferenceEquals(pending.Window, repair.Window)) return false;
            if (pending.Gravity.BuildRepair?.Served != true || !ReferenceEquals(pending.Gravity.BuildRepair.Owner, repair)) return false;
            pending.BuildOverlapRepair = repair;
            return true;
        }
        void CommitBuildOverlapRepair(Pending pending)
        {
            // Called only after the atomic World/collider swap and journal commit.
            // Source identity has intentionally changed by this point.
            if (pending.BuildOverlapRepair == null || !ReferenceEquals(pending.BuildOverlapRepair, _buildOverlapRepair))
            {
                // A changed source retires this token, but unrelated publication
                // is explicitly not credited as service of the witnessed motion.
                if (_buildOverlapRepair != null) { _buildRepairUnrelatedPublished++; CancelBuildOverlapRepair(); }
                return;
            }
            _buildOverlapRepair = null;
            _buildRepairPublished++; _buildRepairTargetServed++;
        }
        void RejectBuildOverlapRepair(Pending pending)
        {
            if (pending.BuildOverlapRepair == null || !ReferenceEquals(pending.BuildOverlapRepair, _buildOverlapRepair)) return;
            // Final-validation rejection is not service. Return the original
            // FIFO head after existing retry restoration rather than holding it
            // until an unbounded run of background validation retries succeeds.
            _buildOverlapRepair = _stageBuildOverlapRepair = null;
            _buildRepairRejected++;
        }
        void CommitBuildOverlapRepairNoChange(GravityJournal journal)
        {
            if (!RefreshBuildOverlapRepair() || journal == null || !ReferenceEquals(journal.Window, _buildOverlapRepair.Window)) return;
            var visit = journal.BuildRepair;
            if (visit == null || !ReferenceEquals(visit.Owner, _buildOverlapRepair)) return;
            if (visit.Blocked) { _buildRepairTargetBlocked++; CancelBuildOverlapRepair(); return; }
            if (visit.Preparation != null || visit.HeadPending)
            { BindBuildOverlapRepairPreparation(visit.Preparation, journal); _buildOverlapRepair.HeadPending |= visit.HeadPending; return; }
            // Only an admitted eligible visit of the exact motion or its actual
            // column-head dependency consumes this token. A refused visit keeps it.
            if (!visit.Served || journal.AdmittedWork <= 0) return;
            _buildOverlapRepair = null;
            _buildRepairNoChange++; _buildRepairTargetServed++;
        }

        bool TrySelectBuildOverlapRepairMotion(ref int checks, int maximum, Stopwatch timer, double milliseconds, out GravityMotionId id)
        {
            id = default; var journal = _gravityJournal; var repair = _stageBuildOverlapRepair;
            if (repair == null || journal == null || journal.BuildRepairSelectionAttempted || !RefreshBuildOverlapRepair()
                || !ReferenceEquals(repair, _buildOverlapRepair) || !ReferenceEquals(journal.Window, repair.Window)) return false;
            journal.BuildRepairSelectionAttempted = true;
            if (!journal.BuildRepairDebtTracked)
            { journal.BuildRepairDebtTracked = true; journal.BuildRepairPriorityDebt = _buildRepairPriorityDebt; }
            // A prior target visit must first repay its actual CPU work through
            // original ordinary motion visits. Startup priority cannot repay it.
            if (journal.BuildRepairPriorityDebt > 0) return false;
            // Admit all fixed source validation and tree-min work BEFORE looking
            // up any source cell or column heads. No world/chain enumeration.
            if (!SpendGravity(ref checks, 24, maximum, timer, milliseconds))
            { _buildRepairTargetBudgetDeferred++; return false; }
            ChargeGravityStartupChecks(CurrentGravityStartup(), true, true, 24);
            journal.BuildRepairPriorityDebt += 24;
            var visit = journal.BuildRepair = new BuildRepairVisit { Owner = repair, Motion = repair.Motion };
            if (!repair.Source.Falling.TryGetValue(repair.SourceRun, out var run) || repair.SourceIndex < 0
                || repair.SourceIndex >= run.Cells.Length || !run.Cells[repair.SourceIndex].Occupied
                || run.Bottom + Vector3Int.up * repair.SourceIndex != repair.SourceCell
                || new Vector2Int(run.Bottom.x, run.Bottom.z) != repair.Column)
            { visit.Blocked = true; return false; }
            var sourceMotion = new GravityMotionId(run.Cohort == null ? run.Id : run.Cohort.Id, run.Cohort != null);
            if (!sourceMotion.Equals(repair.Motion)) { visit.Blocked = true; return false; }
            // A lower source head must settle before the overlapping source can
            // settle. Give only this necessary committed dependency one visit.
            // The ordinary body rechecks the current staged heads and all proofs.
            if (_cohortHeads.TryGetValue(repair.Column, out var heads) && heads.Count != 0
                && heads.Min.Bottom <= (run.Cohort == null ? run.Bottom.y : run.Cohort.Bottom.y))
                visit.Motion = heads.Min.Motion;
            id = visit.Motion; visit.Dependency = !id.Equals(repair.Motion);
            repair.ServiceMotion = id; repair.Dependency = visit.Dependency; repair.HeadPending = false;
            _buildRepairTargetSelected++; if (visit.Dependency) _buildRepairDependencySelected++;
            return true;
        }
        void ObserveBuildOverlapRepairVisit(GravityMotionId id, bool priority, bool startup, bool eligible,
            int visitChecks, int admittedPrefix, int checks, BuildRepairProgress progress,
            GravityDependentEditRequest dependentBefore, GravityPrivateRequest privateBefore,
            HistoricalRecoveryRequest recoveryBefore, Vector2Int? headBefore)
        {
            var journal = _gravityJournal; var visit = journal?.BuildRepair;
            if (journal == null) return;
            if (!journal.BuildRepairDebtTracked)
            { journal.BuildRepairDebtTracked = true; journal.BuildRepairPriorityDebt = _buildRepairPriorityDebt; }
            long actualWork = System.Math.Max(0, checks - visitChecks);
            if (!priority)
            {
                if (!startup) journal.BuildRepairPriorityDebt = System.Math.Max(0, journal.BuildRepairPriorityDebt - 2 * actualWork);
                return;
            }
            journal.BuildRepairPriorityDebt += actualWork;
            if (visit == null || !visit.Motion.Equals(id)) return;
            var dependency = journal.DependentEditRequest;
            var replacement = journal.PrivateRequest;
            var recovery = journal.RecoveryRequest;
            if (!ReferenceEquals(dependentBefore, dependency) && dependency != null && dependency.Motion.Equals(id) && dependency.Column == visit.Owner.Column)
                visit.Preparation = dependency;
            else if (!ReferenceEquals(privateBefore, replacement) && replacement != null && replacement.Motion.Equals(id) && replacement.Column == visit.Owner.Column)
                visit.Preparation = replacement;
            else if (!ReferenceEquals(recoveryBefore, recovery) && recovery != null && recovery.Motion.Equals(id) && recovery.Column == visit.Owner.Column)
                visit.Preparation = recovery;
            visit.HeadPending = headBefore != journal.HeadPreparationColumn && journal.HeadPreparationColumn == visit.Owner.Column;
            if (visit.Preparation != null || visit.HeadPending) return;
            // A later refusal cannot erase admitted target progress already
            // retained in this journal. Credit still waits for its real commit.
            visit.Served = eligible && checks > admittedPrefix && progress.Changed(journal.Next);
            if (journal.MotionVisitBudgetDeferred) { _buildRepairTargetBudgetDeferred++; return; }
            if (!eligible) { visit.Blocked = true; return; }
            // A geometry/time wait or a bare lower-head query is not progress.
            // Release the FIFO head after this finite opportunity.
            if (!visit.Served) visit.Blocked = true;
        }
        void BindBuildOverlapRepairPreparation(object request, GravityJournal journal)
        {
            var visit = journal?.BuildRepair;
            if (!RefreshBuildOverlapRepair() || visit == null || !ReferenceEquals(visit.Owner, _buildOverlapRepair)
                || !ReferenceEquals(visit.Preparation, request)) return;
            var repair = _buildOverlapRepair;
            if (request != null && !ReferenceEquals(repair.Preparation, request)) _buildRepairPreparationHandoffs++;
            repair.Preparation = request; repair.ServiceMotion = visit.Motion; repair.Dependency = visit.Dependency;
        }
        void CancelBuildOverlapRepairPreparation(object request)
        {
            if (request != null && ReferenceEquals(_buildOverlapRepair?.Preparation, request)) CancelBuildOverlapRepair();
        }
        void CancelBuildOverlapRepairHeadPreparation()
        {
            if (_buildOverlapRepair?.HeadPending == true) CancelBuildOverlapRepair();
        }
        void TransferBuildOverlapRepairPreparation(object from, object to)
        {
            if (from != null && to != null && ReferenceEquals(_buildOverlapRepair?.Preparation, from))
            { _buildOverlapRepair.Preparation = to; _buildRepairPreparationHandoffs++; }
        }
        void CompleteBuildOverlapRepairPreparation(object request, GravityJournal journal, bool accepted)
        {
            if (request == null || !RefreshBuildOverlapRepair() || !ReferenceEquals(_buildOverlapRepair.Preparation, request)) return;
            var repair = _buildOverlapRepair;
            // A dependent proof can in turn discover the need for classified
            // recovery. Keep that exact request identity, rather than any gravity.
            if (journal?.RecoveryRequest != null && journal.RecoveryRequest.Motion.Equals(repair.ServiceMotion)
                && journal.RecoveryRequest.Column == repair.Column)
            {
                TransferBuildOverlapRepairPreparation(request, journal.RecoveryRequest);
                if (journal.BuildRepair != null) journal.BuildRepair.Preparation = journal.RecoveryRequest;
                return;
            }
            if (!accepted) { _buildRepairTargetBlocked++; CancelBuildOverlapRepair(); return; }
            repair.Preparation = null;
            if (journal == null)
            { _buildOverlapRepair = null; _buildRepairNoChange++; _buildRepairTargetServed++; return; }
            journal.BuildRepair = new BuildRepairVisit { Owner = repair, Motion = repair.ServiceMotion,
                Dependency = repair.Dependency, Served = true };
        }

        void ReleaseBuildOverlapRepairNoReady()
        {
            if (_stageBuildOverlapRepair == null || !ReferenceEquals(_stageBuildOverlapRepair, _buildOverlapRepair)) return;
            // No queued column/index/motion exists. Release the original head
            // without waiting for a nonexistent publication or completing it here.
            _buildOverlapRepair = _stageBuildOverlapRepair = null;
            _buildRepairNoReady++;
        }
    }
}
