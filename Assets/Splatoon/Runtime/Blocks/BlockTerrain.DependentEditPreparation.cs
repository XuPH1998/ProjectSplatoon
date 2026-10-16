using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // Requests are immutable. A copied/committed motion cursor never owns
        // or aliases the mutable, transaction-local metadata preparation.
        sealed class GravityDependentEditRequest
        {
            public readonly GravityMotionId Motion;
            public readonly Vector2Int Column;
            public readonly bool Landing;
            public GravityDependentEditRequest(GravityMotionId motion, Vector2Int column, bool landing)
            { Motion = motion; Column = column; Landing = landing; }
        }
        sealed class DependentPublicationContext
        {
            public BlockWorld Source;
            public uint Epoch, Revision, Round;
            public ulong Mutation, ReservationVersion;
            public BlockPublicationWindow Window;
            public GravityJournal Journal;
            public FairPublicationSeed FairSeed;
            public Command FairHead;
            public bool PureBackground, Foreground, Dependency;
            public LinkedList<CommandCheckpoint> Checkpoints;
            public bool SourceValid(BlockTerrain terrain)
                => terrain._authority && terrain._accepting && !terrain._destroying
                    && terrain._remoteQueued == null && Epoch == terrain._epoch
                    && ReferenceEquals(Source, terrain.World) && Revision == terrain.Revision
                    && Round == terrain.Round && Mutation == Source.MutationGeneration;
            public bool FairValid(BlockTerrain terrain)
                => ReferenceEquals(FairSeed, terrain._fairSeed)
                    && (FairSeed == null || terrain.FairContextValid(FairSeed)
                        && FairSeed.Foreground == Foreground && FairSeed.Dependency == Dependency
                        && ReferenceEquals(FairHead, FairSeed.Head));
        }
        sealed class DependentEditPreparation
        {
            public DependentPublicationContext Context;
            public GravityDependentEditRequest Request;
            public BlockWorld Draft;
            public uint DraftRevision, DraftRound;
            public ulong DraftMutation;
            public BlockCohortEditPreparation Builder;
            public HashSet<Vector3Int> Changed;
            public List<(Command, BlockMutationResult)> Results;
            public LinkedList<AddedBuildSlot> Slots;
            public HashSet<Vector2Int> MovingColumns;
            public bool MovingBuild, OriginalChanged, Edited, ProofReset;
            public bool DraftValid()
                => Draft != null && ReferenceEquals(Context.Journal.Next, Draft)
                    && ReferenceEquals(Context.Journal.Window, Context.Window)
                    && Draft.Revision == DraftRevision && Draft.Round == DraftRound
                    && Draft.MutationGeneration == DraftMutation;
        }
        DependentEditPreparation _dependentEditPreparation;
        // A moving-build guard can wrap the same journal in another finite
        // owner. Preserve its original fair classification until Pending owns it.
        DependentPublicationContext _dependentPublicationContext;
        bool HasDependentEditPreparation => _dependentEditPreparation != null;
        public long DependentEditPreparationStarts, DependentEditPreparationStages, DependentEditPreparationWork;
        public long DependentEditPreparationRetries, DependentEditPreparationAborts, DependentEditDeferred;
        public ulong DependentEditPreparationProgressVersion { get; private set; }
        public int LastDependentEditPreparationChecks, MaximumDependentEditPreparationChecks;

        void RequestDependentEditPreparation(GravityMotionId motion, Vector2Int column, bool landing)
        {
            if (!motion.Cohort || _gravityJournal == null) return;
            // One exact motion owns each private continuation. Other motions
            // remain on their unchanged ordinary round-robin journal cursors.
            _gravityJournal.DependentEditRequest ??= new GravityDependentEditRequest(motion, column, landing);
        }
        bool BeginDependentEditPreparation(BlockWorld draft, HashSet<Vector3Int> changed,
            List<(Command, BlockMutationResult)> results, BlockPublicationWindow window, bool any)
        {
            var journal = _gravityJournal; var request = journal?.DependentEditRequest;
            if (request == null) return false;
            journal.DependentEditRequest = null;
            if (!ReferenceEquals(journal.Next, draft) || !ReferenceEquals(journal.Window, window))
                throw new InvalidOperationException("Dependent edit request does not own its admitted draft and window.");
            // Commands may have run after gravity in this same authority frame.
            // A vanished/fractured target cannot use the old dense-span proof.
            if (!draft.Cohorts.TryGetValue(request.Motion.Id, out var group) || group.NeedsRegroup
                || !journal.Motions.TryGetValue(request.Motion, out var motion) || motion == null
                || motion.Column != request.Column || group.Bottom.x != request.Column.x || group.Bottom.z != request.Column.y)
            {
                if (journal.Motions.TryGetValue(request.Motion, out var stale) && stale != null)
                { stale.ProofKnown = false; stale.Phase = 0; }
                DependentEditDeferred++; return false;
            }
            var context = new DependentPublicationContext { Source = World, Epoch = _epoch,
                Revision = Revision, Round = Round, Mutation = World.MutationGeneration,
                ReservationVersion = _fairReservationVersion, Window = window, Journal = journal,
                FairSeed = _fairSeed, FairHead = _fairSeed?.Head,
                Foreground = _fairSeed?.Foreground ?? false, Dependency = _fairSeed?.Dependency ?? false,
                PureBackground = _stagePureAuthorityBackground, Checkpoints = _stageCommandCheckpoints };
            _dependentEditPreparation = new DependentEditPreparation { Context = context, Request = request, Draft = draft,
                DraftRevision = draft.Revision, DraftRound = draft.Round, DraftMutation = draft.MutationGeneration,
                Changed = changed, Results = results, Slots = _stageAddedBuildSlots,
                MovingBuild = _stageHasMovingBuild, MovingColumns = _stageMovingBuildColumns, OriginalChanged = any };
            _gravityJournal = null; _stageCommandCheckpoints = null; _stageAddedBuildSlots = null;
            _stageCommandCheckpoint = null; _stageHasMovingBuild = false; _stageMovingBuildColumns = null;
            DependentEditPreparationStarts++; return true;
        }
        void AbortDependentEditPreparation()
        {
            _dependentPublicationContext = null;
            var owner = _dependentEditPreparation; _dependentEditPreparation = null;
            if (owner == null) return;
            owner.Builder?.Cancel(); owner.Builder?.Dispose();
            // The surrounding reset/authority-loss paths complete their queue
            // only after restoring this owner's unpublished paid cursors.
            if (owner.Context.Checkpoints != null)
                for (var node = owner.Context.Checkpoints.Last; node != null; node = node.Previous)
                    RestoreCheckpoint(node.Value);
            DependentEditPreparationAborts++;
        }
        void RetryDependentEditPreparation(DependentEditPreparation owner, bool reservationsChanged)
        {
            if (!ReferenceEquals(owner, _dependentEditPreparation)) return;
            owner.Builder?.Cancel(); owner.Builder?.Dispose(); _dependentEditPreparation = null;
            _dependentPublicationContext = null;
            // A newly frozen destruction reservation invalidates pure background
            // work exactly as ValidateFairPending does; no foreground bypass is
            // granted before the existing complete latest-source blocked proof.
            if (reservationsChanged && owner.Context.PureBackground && owner.Context.FairSeed == null
                && _fairSeed == null && owner.Context.SourceValid(this))
            {
                _fairSeed = new FairPublicationSeed { Epoch = _epoch, Round = Round, StartedAt = _now,
                    BackgroundWindow = owner.Context.Window };
                FairReservationRestages++;
            }
            else if (owner.Context.FairSeed != null && ReferenceEquals(owner.Context.FairSeed, _fairSeed))
                DiscardFairReplay(_fairSeed);
            // This existing finite retry path restores reverse checkpoints in
            // FIFO order without touching Total, original targets or callbacks.
            if (_buildOccupancyPreparation != null)
                throw new InvalidOperationException("Two preparation owners cannot replay the same authority stage.");
            _buildOccupancyPreparation = new BuildOccupancyPreparation { Retry = true,
                Window = owner.Context.Window, Checkpoints = owner.Context.Checkpoints,
                RetryCursor = owner.Context.Checkpoints?.Last };
            DependentEditPreparationRetries++;
        }
        bool SpendDependentPreparation(ref int checks, int cost, int maximum, Stopwatch timer, double milliseconds)
        {
            if (!SpendGravity(ref checks, cost, maximum, timer, milliseconds)) return false;
            DependentEditPreparationWork += cost; DependentEditPreparationProgressVersion++; return true;
        }
        bool AdvanceDependentEditPreparation()
        {
            var owner = _dependentEditPreparation;
            if (owner == null)
            {
                // A canceled existing build wrapper no longer owns this stamp.
                if (_dependentPublicationContext != null && !HasBuildOccupancyPreparation && _pending == null)
                    _dependentPublicationContext = null;
                return false;
            }
            bool commandWork = _commands.Count != 0;
            int maximum = commandWork ? 1024 : MaxChecks, checks = 0;
            double milliseconds = commandWork ? .25 : .75;
            var timer = Stopwatch.StartNew(); long before = GravityWorkChecks; bool finalize = false;
            AuthorityStageCount++; DependentEditPreparationStages++; LastCommandStageMilliseconds = 0;
            try
            {
                // Match ValidateFairPending: a combined ordinary command and
                // gravity draft has no background lease to invalidate. A new
                // tail destruction must not force that paid draft to restart.
                bool reservationSensitive = owner.Context.PureBackground
                    || owner.Context.FairSeed != null && !owner.Context.Foreground;
                bool reservationsChanged = reservationSensitive && owner.Context.ReservationVersion != _fairReservationVersion;
                if (!owner.Context.SourceValid(this) || !owner.Context.FairValid(this) || !owner.DraftValid() || reservationsChanged)
                { RetryDependentEditPreparation(owner, reservationsChanged); return true; }
                _gravityJournal = owner.Context.Journal; _stagePublicationWindow = owner.Context.Window;
                _stagePureAuthorityBackground = owner.Context.PureBackground;
                if (!owner.Draft.Cohorts.TryGetValue(owner.Request.Motion.Id, out var group) || group.NeedsRegroup)
                {
                    // Once copying starts, no partially repaired draft can be
                    // finalized, even if an unexpected target write was unversioned.
                    if (owner.Builder != null)
                    { RetryDependentEditPreparation(owner, false); return true; }
                    DependentEditDeferred++; finalize = true;
                }
                else
                {
                    if (owner.Builder == null)
                    {
                        if (!SpendDependentPreparation(ref checks, 4, maximum, timer, milliseconds)) return true;
                        owner.Builder = owner.Draft.BeginBudgetedCohortEdit(group.Id);
                    }
                    while (!owner.Builder.Ready && HasStageBudget(checks, maximum, timer, milliseconds))
                    {
                        int cost = owner.Builder.EstimateNextStepWork();
                        if (cost <= 0 || cost > maximum)
                            throw new InvalidOperationException("Dependent metadata step cannot fit the unchanged stage budget.");
                        if (!SpendDependentPreparation(ref checks, cost, maximum, timer, milliseconds)) return true;
                        ulong progress = owner.Builder.ProgressVersion;
                        owner.Builder.Step(owner.Draft, cost, out int actual);
                        if (actual > cost || owner.Builder.Failed || owner.Builder.ProgressVersion == progress)
                            throw new InvalidOperationException("Dependent metadata preparation failed its admitted finite step.");
                    }
                    if (!owner.Builder.Ready) return true;
                    if (!owner.Builder.IsValid(owner.Draft) || !owner.DraftValid())
                        throw new InvalidOperationException("Prepared dependent metadata no longer belongs to this exact draft.");
                    group = owner.Draft.Cohorts[owner.Request.Motion.Id];
                    if (!_gravityJournal.Motions.TryGetValue(owner.Request.Motion, out var work) || work == null)
                        throw new InvalidOperationException("Dependent owner lost its exact journal motion cursor.");
                    if (owner.Request.Landing && !owner.ProofReset)
                    {
                        if (!SpendDependentPreparation(ref checks, 1, maximum, timer, milliseconds)) return true;
                        work.ProofKnown = false; work.Phase = 0; owner.ProofReset = true;
                    }
                    _gravityJournal.DependentEditRequest = null; _gravityJournal.DependentEditDeferred = false;
                    if (owner.Request.Landing)
                    {
                        owner.Edited |= StageMotionLandingAndSettle(owner.Draft, owner.Changed, owner.Request.Motion,
                            group, null, work, ref checks, timer, milliseconds, maximum, true);
                        finalize = work.ProofKnown || _gravityJournal.RecoveryRequest != null;
                    }
                    else
                    {
                        bool complete = TryBindLowerSupport(owner.Draft, owner.Request.Motion, group, work,
                            out bool changed, out _, ref checks, timer, milliseconds, maximum, true);
                        owner.Edited |= changed; finalize = complete || _gravityJournal.DependentEditDeferred;
                        if (_gravityJournal.DependentEditDeferred) DependentEditDeferred++;
                    }
                    if (_gravityJournal.DependentEditRequest != null)
                        throw new InvalidOperationException("Completed targeted metadata did not admit its original edit.");
                    // Any semantic edit completes this owner's original request.
                    // Incomplete proof stages retain the same exact draft cursor.
                    if (!finalize && owner.Draft.MutationGeneration != owner.DraftMutation)
                        throw new InvalidOperationException("An incomplete dependent proof mutated its owned draft.");
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[BLOCK] Dependent metadata preparation rejected: " + exception.Message);
                FailedTransactions++; RetryDependentEditPreparation(owner, false); return true;
            }
            finally
            {
                _gravityJournal = null; _stagePublicationWindow = null; _stagePureAuthorityBackground = false;
                LastDependentEditPreparationChecks = checks;
                MaximumDependentEditPreparationChecks = Math.Max(MaximumDependentEditPreparationChecks, checks);
                LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (GravityWorkChecks != before) GravityProgressStages++;
            }
            // Preserve existing publication/callback exception semantics. A
            // callback after a real atomic swap must never enter preparation retry.
            if (finalize) FinishDependentEditPreparation(owner);
            return true;
        }
        void FinishDependentEditPreparation(DependentEditPreparation owner)
        {
            if (!ReferenceEquals(owner, _dependentEditPreparation)) return;
            owner.Builder?.Dispose(); _dependentEditPreparation = null;
            var context = owner.Context;
            _gravityJournal = context.Journal; _gravityJournal.DependentEditRequest = null;
            _stagePublicationWindow = context.Window; _stagePureAuthorityBackground = context.PureBackground;
            _stageCommandCheckpoints = context.Checkpoints; _stageAddedBuildSlots = owner.Slots;
            _stageHasMovingBuild = owner.MovingBuild; _stageMovingBuildColumns = owner.MovingColumns;
            try
            {
                if (!owner.OriginalChanged && !owner.Edited)
                {
                    CommitGravityJournal(_gravityJournal); _gravityJournal = null;
                    FairTerminalNoPublication(owner.Results);
                    if (context.PureBackground) FairBackgroundStageDidNotPublish();
                    foreach (var pair in owner.Results) if (pair.Item1.Done) Complete(pair.Item1, pair.Item1.Total);
                    return;
                }
                _dependentPublicationContext = context;
                owner.Draft.Seal(Revision + 1, owner.Changed);
                BeginWindowTransaction(owner.Draft, owner.Changed, false, owner.Results, context.Window);
            }
            finally
            {
                _gravityJournal = null; _stagePublicationWindow = null; _stagePureAuthorityBackground = false;
                _stageCommandCheckpoints = null; _stageAddedBuildSlots = null; _stageCommandCheckpoint = null;
                _stageHasMovingBuild = false; _stageMovingBuildColumns = null;
            }
        }
        void ApplyDependentPublicationContext(Pending pending)
        {
            var context = _dependentPublicationContext;
            if (context == null || !ReferenceEquals(pending.Gravity, context.Journal)) return;
            if (!context.SourceValid(this) || !context.FairValid(this) || !ReferenceEquals(pending.Window, context.Window))
                throw new InvalidOperationException("Dependent publication lost its source or captured fair lease.");
            pending.PureAuthorityBackground = context.PureBackground;
            pending.FairSource = context.Source; pending.FairSourceRevision = context.Revision;
            pending.FairSourceRound = context.Round; pending.FairSourceMutation = context.Mutation;
            pending.FairSeed = context.FairSeed; pending.FairForeground = context.Foreground;
            pending.FairDependency = context.Dependency; pending.FairReservationVersion = context.ReservationVersion;
            pending.CommandCheckpoints ??= context.Checkpoints;
            pending.ForegroundWork |= context.Foreground || (context.Checkpoints?.Count ?? 0) != 0;
            _dependentPublicationContext = null;
        }
    }
}
