using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // One retired background transaction lends one foreground opportunity.
        // Its lease is retained until real background terrain publishes or a
        // complete latest-source proof shows that no background work remains.
        sealed class FairPublicationSeed
        {
            public uint Epoch, Round;
            public double StartedAt;
            public BlockPublicationWindow BackgroundWindow, ForegroundWindow;
            public Command Head;
            public bool Foreground, Dependency;
            public BlockWorldCloneBuilder Clone;
            public BlockWorld Replay, Source;
            public uint SourceRevision;
            public ulong SourceMutation;
            public FairBlockedScan Scan;
        }
        sealed class FairBlockedScan : IDisposable
        {
            public BlockWorld Source, TerminalSource;
            public uint Revision, Round, Epoch;
            public ulong Mutation, WorkVersion, InputVersion, ReservationVersion, TerminalMutation;
            public Dictionary<Vector2Int, HistoricalRecoveryOutcome> Terminals;
            public int ColumnCount, MotionCount, Phase, Cursor;
            public bool UnitOpen, HasBlocker, HasSleeping, Finished;
            public Command Blocker;
            public Vector2Int BlockerColumn;
            public Vector3Int LiveCell, BlockerCell;
            public ulong LiveRun, BlockerRun;
            public int LiveMember, LiveIndex, LiveY, LiveTop, LivePhase;
            public Vector2Int Column;
            public GravityMotionId Motion;
            public IEnumerator<Command> Reservations;
            public double ExpiresAt = double.PositiveInfinity;
            public void Dispose() { Reservations?.Dispose(); Reservations = null; }
        }

        FairPublicationSeed _fairSeed;
        Command _fairLastDependencyBlocker, _fairLastDependencyHead;
        BlockWorld _fairLastDependencySource;
        ulong _fairLastDependencyReservationVersion;
        Vector2Int _fairLastDependencyColumn;
        Vector3Int _fairLastDependencyCell;
        ulong _fairLastDependencyRun;
        bool _stagePureAuthorityBackground;
        ulong _fairInputVersion, _fairReservationVersion, _fairWorkVersion;
        public long FairBackgroundYields { get; private set; }
        public long FairForegroundAtomicPublications { get; private set; }
        public long FairDependencyAtomicPublications { get; private set; }
        public long FairForegroundPreparationPublications { get; private set; }
        public long FairTerminalCommandNoOps { get; private set; }
        public long FairBackgroundRebuilds { get; private set; }
        public long FairBackgroundTerrainPublications { get; private set; }
        public long FairBackgroundMetadataPublications { get; private set; }
        public long FairCompleteBlockedScans { get; private set; }
        public long FairScanWork { get; private set; }
        public long FairUnclassifiedRestages { get; private set; }
        public long FairExpiredScans { get; private set; }
        public long FairReservationRestages { get; private set; }
        public long FairContextInvalidations { get; private set; }
        public long FairPhysicalFailures { get; private set; }
        public long FairRetiredPhysicalFailures { get; private set; }
        public long FairRetiredCompletions { get; private set; }
        public long FairPreparationRetirements { get; private set; }
        public long FairNoRemainingBackground { get; private set; }
        public long FairInputsDuringSeed { get; private set; }
        public int FairMaximumQueueDuringSeed { get; private set; }
        public int FairLastCompleteScanColumns { get; private set; }
        public int FairLastCompleteScanMotions { get; private set; }
        public double FairLongestSeedSeconds { get; private set; }
        public double FairCurrentSeedSeconds => _fairSeed == null ? 0 : Math.Max(0, _now - _fairSeed.StartedAt);
        public int FairCapturedBackgroundPolicyVersion => _fairSeed?.BackgroundWindow?.Policy.Version ?? 0;

        void ObserveFairQueuedInput(bool destruction)
        {
            _fairInputVersion++;
            if (destruction) _fairReservationVersion++;
            if (_fairSeed == null) return;
            FairInputsDuringSeed++;
            FairMaximumQueueDuringSeed = Math.Max(FairMaximumQueueDuringSeed, _commands.Count);
        }
        void ObserveFairCommandCompletion(bool removedReservation)
        {
            _fairInputVersion++;
            if (removedReservation) _fairReservationVersion++;
        }
        void ObserveFairGravityWorkMutation() => _fairWorkVersion++;
        bool FairPreparationLocked => HasBuildOccupancyPreparation || HasHistoricalRecovery || HasDependentEditPreparation
            || HasHeadPreparation || HasPrivatePreparation || _activePrivateGravityRequest != null;
        bool FairContextValid(FairPublicationSeed seed)
            => seed != null && _authority && _accepting && !_destroying && _remoteQueued == null
                && seed.Epoch == _epoch && seed.Round == Round;
        bool FairSourceValid(BlockWorld source, uint revision, uint round, ulong mutation)
            => ReferenceEquals(source, World) && Revision == revision && Round == round
                && World.MutationGeneration == mutation;
        void DiscardFairReplay(FairPublicationSeed seed)
        {
            seed.Clone?.Cancel(); seed.Clone = null;
            seed.Scan?.Dispose(); seed.Scan = null;
            seed.Replay = seed.Source = null;
        }
        void CancelFairScheduling()
        {
            var seed = _fairSeed; _fairSeed = null;
            if (seed == null) return;
            FairLongestSeedSeconds = Math.Max(FairLongestSeedSeconds, Math.Max(0, _now - seed.StartedAt));
            DiscardFairReplay(seed);
        }
        bool PreserveFairSeedAfterFailure(Pending pending)
        {
            var seed = _fairSeed;
            if (!ReferenceEquals(pending.FairSeed, seed) || !FairContextValid(seed)) return false;
            // Existing Fail still decides the paid command's terminal result.
            // Physical failure does not lend another foreground opportunity.
            FairPhysicalFailures++; seed.Foreground = false; DiscardFairReplay(seed); return true;
        }
        bool PreserveFairSeedForPreparation(Pending pending)
        {
            var seed = _fairSeed;
            if (!ReferenceEquals(pending.FairSeed, seed) || !FairContextValid(seed)) return false;
            // A failed/incomplete private preparation has no paid head result.
            // Keep its current phase and lease, including historical redirection.
            FairPreparationRetirements++; DiscardFairReplay(seed); return true;
        }
        void RefreshFairSchedulingContext()
        {
            var seed = _fairSeed;
            if (seed == null || FairContextValid(seed)) return;
            FairContextInvalidations++; CancelFairScheduling();
            if (_pending != null && ReferenceEquals(_pending.FairSeed, seed))
            { _pending.AbortReason = PendingAbortReason.FairContextChanged; AbortPending(); }
        }
        void RetireFairBackgroundPending(Pending pending)
        {
            // No command, callback or committed gravity journal is consumed.
            // Retired ownership still joins every entered generation/native Bake.
            if (pending.Results.Count != 0 || (pending.CommandCheckpoints?.Count ?? 0) != 0)
                throw new InvalidOperationException("A foreground transaction cannot use background retirement.");
            pending.AdmissionStop.Cancel();
            pending.FairRetired = true;
            _retired.Enqueue(new Retired { Pending = pending }); _pending = null;
        }
        void ObserveFairRetiredCompletion(Pending pending)
        {
            if (!pending.FairRetired) return;
            pending.FairRetired = false;
            FairRetiredCompletions++;
            // The wall Aborted event measures publication retirement at its
            // original boundary. This separate joined-completion diagnostic
            // observes actual late physical faults, never owned admission
            // cancellation, without touching any newer command or seed credit.
            Exception failure = pending.Generate.IsFaulted ? pending.Generate.Exception
                : pending.Scheduled.Task.IsFaulted ? pending.Scheduled.Task.Exception : null;
            if (failure == null && pending.Scheduled.Task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                foreach (var task in pending.Scheduled.Task.Result)
                    if (task.IsFaulted) { failure = task.Exception; break; }
            if (failure == null && pending.Pieces != null) foreach (var piece in pending.Pieces)
                if (piece?.Bake != null && piece.Bake.IsFaulted) { failure = piece.Bake.Exception; break; }
            if (failure != null)
            {
                if (pending.TrackWall) pending.WallFailure = true;
                FailedTransactions++; FairPhysicalFailures++; FairRetiredPhysicalFailures++;
                UnityEngine.Debug.LogWarning("[BLOCK] Retired fair terrain work failed after joining; wallId=" + pending.WallId
                    + ", chunks=" + pending.Changed.Length + ": " + failure.Message);
            }
        }
        static Exception KnownFairPendingFailure(Pending pending)
        {
            var failure = CompletedFailure(pending.Generate) ?? CompletedFailure(pending.Scheduled.Task);
            if (failure != null) return failure;
            if (pending.Scheduled.Task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                foreach (var task in pending.Scheduled.Task.Result)
                    if ((failure = CompletedFailure(task)) != null) return failure;
            if (pending.Pieces != null) foreach (var piece in pending.Pieces)
                if (piece?.Bake != null && (failure = CompletedFailure(piece.Bake)) != null) return failure;
            return null;
        }
        void TryYieldBackgroundPublication()
        {
            if (ProtectBuildOverlapRepairPending(_pending)) return;
            if (_fairSeed != null || !_authority || !_accepting || _remoteQueued != null || FairPreparationLocked
                || _blastBatch != null || _commands.Count == 0) return;
            var pending = _pending; var head = _commands.Peek();
            if (pending == null || !pending.PureAuthorityBackground || pending.Remote || pending.PrivateGravity
                || pending.BuildGuard != null || pending.RecoveryGuard != null || pending.Changed.Length == 0
                || pending.Results.Count != 0 || (pending.CommandCheckpoints?.Count ?? 0) != 0
                || pending.Epoch != _epoch || !head.OrdinaryMainAttack || head.Done || head.Completed
                || !FairSourceValid(pending.FairSource, pending.FairSourceRevision, pending.FairSourceRound, pending.FairSourceMutation)
                || KnownFairPendingFailure(pending) != null) return;
            var seed = new FairPublicationSeed { Epoch = _epoch, Round = Round, StartedAt = _now,
                BackgroundWindow = pending.Window };
            pending.AbortReason = PendingAbortReason.FairBackgroundYield;
            RetireFairBackgroundPending(pending); _fairSeed = seed;
            FairBackgroundYields++; FairMaximumQueueDuringSeed = Math.Max(FairMaximumQueueDuringSeed, _commands.Count);
            EnterFairForeground(seed, head, false);
            RecordPendingTerminal(pending, PendingWallOutcome.Aborted);
        }
        void EnterFairForeground(FairPublicationSeed seed, Command head, bool dependency)
        {
            if (_commands.Count == 0 || !ReferenceEquals(_commands.Peek(), head) || head.Done || head.Completed)
                throw new InvalidOperationException("Fair continuation must serve the live FIFO head.");
            DiscardFairReplay(seed);
            seed.Head = head; seed.Foreground = true; seed.Dependency = dependency;
            seed.ForegroundWindow = head.RetryWindow ?? _publicationPolicy.Capture(PublicationOperations(head) | BlockPublicationOperation.Gravity);
            // Partial publications of this same paid command retain the lease.
            // Existing finite retry/preparation paths already understand it.
            head.RetryWindow = seed.ForegroundWindow;
        }
        BlockPublicationWindow FairPublicationWindow()
            => _fairSeed == null ? null : _fairSeed.Foreground ? _fairSeed.ForegroundWindow : _fairSeed.BackgroundWindow;
        bool FairAllowsCommand(Command command)
            => _fairSeed == null || _fairSeed.Foreground && ReferenceEquals(command, _fairSeed.Head);
        bool FairAllowsGravity => _fairSeed == null || !_fairSeed.Foreground;
        bool FairForegroundStage => _fairSeed != null && _fairSeed.Foreground;
        bool FairBackgroundStage => _fairSeed != null && !_fairSeed.Foreground;
        void TagFairPending(Pending pending)
        {
            pending.PureAuthorityBackground = _stagePureAuthorityBackground && !pending.Remote;
            pending.FairSource = World; pending.FairSourceRevision = Revision; pending.FairSourceRound = Round;
            pending.FairSourceMutation = World.MutationGeneration;
            pending.FairSeed = _fairSeed; pending.FairForeground = _fairSeed?.Foreground ?? false;
            pending.FairDependency = _fairSeed?.Dependency ?? false;
            pending.FairReservationVersion = _fairReservationVersion;
            pending.ForegroundWork |= pending.FairForeground;
        }
        bool ValidateFairPending(Pending pending)
        {
            if (pending.FairSeed == null && !pending.PureAuthorityBackground) return true;
            // Preserve the original completed-failure observation path. A known
            // generation or native fault must not be disguised as a fair abort.
            if (KnownFairPendingFailure(pending) != null) return true;
            if (pending.FairSeed == null)
            {
                if (!pending.PureAuthorityBackground || pending.FairReservationVersion == _fairReservationVersion) return true;
                // Reservation protection also covers a normal pure background
                // pending when its new head is nonordinary/ineligible for yield.
                // This replay gets no foreground bypass: it must first perform
                // a complete latest-source blocked proof before FIFO dependency.
                pending.AbortReason = PendingAbortReason.FairReservationChanged;
                RetireFairBackgroundPending(pending);
                _fairSeed = new FairPublicationSeed { Epoch = _epoch, Round = Round, StartedAt = _now,
                    BackgroundWindow = pending.Window };
                FairReservationRestages++; RecordPendingTerminal(pending, PendingWallOutcome.Aborted); return false;
            }
            if (!ReferenceEquals(pending.FairSeed, _fairSeed) || !FairContextValid(_fairSeed))
            { pending.AbortReason = PendingAbortReason.FairContextChanged; FairContextInvalidations++; AbortPending(); return false; }
            if (!FairSourceValid(pending.FairSource, pending.FairSourceRevision, pending.FairSourceRound, pending.FairSourceMutation)
                && !pending.FairForeground)
            {
                pending.AbortReason = PendingAbortReason.FairSourceChanged;
                FairContextInvalidations++; RetireFairBackgroundPending(pending);
                DiscardFairReplay(_fairSeed); RecordPendingTerminal(pending, PendingWallOutcome.Aborted); return false;
            }
            if (!pending.FairForeground && pending.FairReservationVersion != _fairReservationVersion)
            {
                // Newly enqueued attacks froze their stable-cell targets on this
                // published source. Recompute rather than detaching below them.
                pending.AbortReason = PendingAbortReason.FairReservationChanged;
                FairReservationRestages++; RetireFairBackgroundPending(pending);
                DiscardFairReplay(_fairSeed); RecordPendingTerminal(pending, PendingWallOutcome.Aborted); return false;
            }
            return true;
        }
        void FairPublicationCommitted(Pending pending)
        {
            var seed = _fairSeed;
            if (seed == null || !ReferenceEquals(pending.FairSeed, seed)) return;
            if (pending.FairForeground)
            {
                bool headPublication = false;
                foreach (var pair in pending.Results)
                    if (ReferenceEquals(pair.command, seed.Head) && (pair.result.Changed > 0 || pair.command.Done))
                    { headPublication = true; break; }
                if (!headPublication)
                {
                    // A private/index/support continuation can publish metadata
                    // before this paid head is ready. It owns no opportunity.
                    FairForegroundPreparationPublications++; DiscardFairReplay(seed); return;
                }
                FairForegroundAtomicPublications++;
                if (pending.FairDependency) FairDependencyAtomicPublications++;
                seed.Foreground = false; DiscardFairReplay(seed);
            }
            else if (pending.Changed.Length != 0)
            { FairBackgroundTerrainPublications++; CancelFairScheduling(); }
            else
            { FairBackgroundMetadataPublications++; DiscardFairReplay(seed); }
        }
        void FairTerminalNoPublication(List<(Command, BlockMutationResult)> results)
        {
            var seed = _fairSeed;
            if (seed == null || !seed.Foreground) return;
            foreach (var pair in results)
                if (ReferenceEquals(pair.Item1, seed.Head) && pair.Item1.Done)
                { FairTerminalCommandNoOps++; seed.Foreground = false; DiscardFairReplay(seed); return; }
        }
        void FairBackgroundStageDidNotPublish()
        {
            // A rejected background transaction can need several finite
            // stages before producing its replacement. A new paid
            // reservation must not deadlock against its gravity-only lease.
            // Transfer that exact lease to the existing complete latest-
            // source blocked scan; only that proof can admit the FIFO head.
            if (_fairSeed == null && _stagePureAuthorityBackground && !FairPreparationLocked
                && _authority && _accepting && !_destroying && _remoteQueued == null
                && _commands.Count != 0 && !_commands.Peek().Done && !_commands.Peek().Completed
                && _gravityRetryPublicationWindow != null
                && ReferenceEquals(_gravityRetryPublicationWindow, _stagePublicationWindow))
            {
                _fairSeed = new FairPublicationSeed { Epoch = _epoch, Round = Round, StartedAt = _now,
                    BackgroundWindow = _gravityRetryPublicationWindow };
                _gravityRetryPublicationWindow = null;
                FairMaximumQueueDuringSeed = Math.Max(FairMaximumQueueDuringSeed, _commands.Count);
            }
            var seed = _fairSeed;
            if (seed == null || seed.Foreground || FairPreparationLocked) return;
            seed.Scan?.Dispose();
            seed.Scan = new FairBlockedScan { Source = World, Revision = Revision, Round = Round, Epoch = _epoch,
                Mutation = World.MutationGeneration, WorkVersion = _fairWorkVersion, InputVersion = _fairInputVersion,
                ReservationVersion = _fairReservationVersion, ColumnCount = _cohortColumnOrder.Count,
                MotionCount = _cohortMotionOrder.Count, TerminalSource = _historicalTerminalSource,
                TerminalMutation = _historicalTerminalMutation, Terminals = _historicalTerminals };
        }
        bool FairScanValid(FairBlockedScan scan)
            => FairSourceValid(scan.Source, scan.Revision, scan.Round, scan.Mutation) && scan.Epoch == _epoch
                && scan.WorkVersion == _fairWorkVersion && scan.InputVersion == _fairInputVersion
                && scan.ReservationVersion == _fairReservationVersion && !_cohortIndexPending && !FairPreparationLocked
                && scan.ColumnCount == _cohortColumnOrder.Count && scan.MotionCount == _cohortMotionOrder.Count
                && ReferenceEquals(scan.TerminalSource, _historicalTerminalSource)
                && scan.TerminalMutation == _historicalTerminalMutation && ReferenceEquals(scan.Terminals, _historicalTerminals);
        bool SpendFairScan(ref int checks, int cost, Stopwatch timer)
        {
            if (!SpendGravity(ref checks, cost, MaxChecks, timer, .75)) return false;
            FairScanWork += cost; return true;
        }
        bool TryFairSleepDeadline(GravityMotionId id, out double deadline)
        {
            deadline = 0;
            if (!_cohortMotionWork.TryGetValue(id, out var work) || !work.ProofKnown || work.NativeProof == null
                || work.Replacement != null || work.Generation != (_landingGeneration.TryGetValue(work.Column, out uint generation) ? generation : 0)) return false;
            double started; float initial, speed, landing;
            if (id.Cohort)
            {
                if (!World.Cohorts.TryGetValue(id.Id, out var group) || group.NeedsRegroup || group.NeedsLandingProof
                    || !group.LandingKnown || group.Phase != FallingCohortPhase.Falling || group.RebasePending) return false;
                started = group.StartedAt; initial = group.InitialOffset; speed = group.InitialSpeed; landing = group.LandingOffset;
            }
            else
            {
                if (!World.Falling.TryGetValue(id.Id, out var run) || !run.LandingKnown) return false;
                started = run.StartedAt; initial = run.InitialOffset; speed = run.InitialSpeed; landing = run.LandingOffset;
            }
            if (!double.IsFinite(started) || !float.IsFinite(initial) || !float.IsFinite(speed) || !float.IsFinite(landing)
                || speed < 0 || !(BlockWorld.Gravity > 0)) return false;
            // Own unconstrained fall is an upper bound: live/frozen lower
            // support only reduces offset. Use the exact original float test.
            float raw = FallingCohort.MotionOffset(_now, started, initial, speed, false, 0);
            if (!(raw + .001f < landing)) return false;
            double distance = (double)landing - initial - .001;
            double root = (Math.Sqrt((double)speed * speed + 2 * BlockWorld.Gravity * distance) - speed) / BlockWorld.Gravity;
            double candidate = started + root;
            // The analytic root merely proposes an endpoint. Verify a strict
            // interior time with the production float formula before retaining it.
            deadline = _now + (candidate - _now) * .5;
            return double.IsFinite(deadline) && deadline > _now
                && FallingCohort.MotionOffset(deadline, started, initial, speed, false, 0) + .001f < landing;
        }
        void RejectFairClassification(FairPublicationSeed seed)
        { FairUnclassifiedRestages++; seed.Scan?.Dispose(); seed.Scan = null; }
        void BeginFairLiveUnit(FairBlockedScan scan)
        {
            scan.LiveMember = scan.LiveIndex = scan.LivePhase = 0;
            scan.LiveRun = 0; scan.LiveCell = default;
            scan.LiveY = Mathf.FloorToInt(World.Bottom / BlockWorld.CellSize);
            scan.LiveTop = Mathf.RoundToInt(BlockWorld.Top / BlockWorld.CellSize);
            if (scan.Phase == 0)
            {
                var work = _cohortColumnWork[scan.Column];
                if (work.Cohort != 0)
                {
                    if (World.Cohorts.TryGetValue(work.Cohort, out var columnGroup))
                    { scan.LiveY = columnGroup.DetachCursor; scan.LiveTop = columnGroup.TopExclusive; }
                    // Actual staging clears a vanished cohort and Ready/Proving,
                    // retaining Scan. Classify that remaining domain without
                    // mutating work or waiting for a reservation-blocked reset.
                    else scan.LiveY = work.Scan;
                }
                else
                {
                    scan.LiveY = work.Ready || work.Proving ? work.Bottom : work.Scan;
                    if (work.Ready) scan.LiveTop = work.Top;
                }
            }
            else if (scan.Motion.Cohort && World.Cohorts.TryGetValue(scan.Motion.Id, out var group))
            { scan.LiveY = group.DetachCursor; scan.LiveTop = group.TopExclusive; }
        }
        // 0 = budget incomplete, 1 = actual occupied source address, 2 = complete
        // source unit has no body. A queued/stale work record alone is no blocker.
        int AdvanceFairLiveUnit(FairBlockedScan scan, ref int checks, Stopwatch timer)
        {
            if (scan.Phase != 0 && scan.LivePhase == 0)
            {
                if (scan.Motion.Cohort)
                {
                    if (!SpendFairScan(ref checks, 2, timer)) return 0;
                    if (!World.Cohorts.TryGetValue(scan.Motion.Id, out var group)) return 2;
                    while (scan.LiveMember < group.MemberIds.Count)
                    {
                        if (!SpendFairScan(ref checks, 2, timer)) return 0;
                        if (!World.Falling.TryGetValue(group.MemberIds[scan.LiveMember], out var member))
                        { scan.LiveMember++; scan.LiveIndex = 0; continue; }
                        if (scan.LiveIndex == member.Cells.Length)
                        { scan.LiveMember++; scan.LiveIndex = 0; continue; }
                        int index = scan.LiveIndex++;
                        if (!member.Cells[index].Occupied) continue;
                        scan.LiveRun = member.Id; scan.LiveCell = member.Bottom + Vector3Int.up * index; return 1;
                    }
                    scan.LivePhase = 1;
                }
                else
                {
                    if (!SpendFairScan(ref checks, 2, timer)) return 0;
                    if (!World.Falling.TryGetValue(scan.Motion.Id, out var run)) return 2;
                    while (scan.LiveIndex < run.Cells.Length)
                    {
                        if (!SpendFairScan(ref checks, 1, timer)) return 0;
                        int index = scan.LiveIndex++;
                        if (!run.Cells[index].Occupied) continue;
                        scan.LiveRun = run.Id; scan.LiveCell = run.Bottom + Vector3Int.up * index; return 1;
                    }
                    return 2;
                }
            }
            while (scan.LiveY < scan.LiveTop)
            {
                if (!SpendFairScan(ref checks, 2, timer)) return 0;
                var cell = new Vector3Int(scan.Column.x, scan.LiveY, scan.Column.y);
                int length = Math.Min(scan.LiveTop - scan.LiveY, BlockWorld.ChunkSize - (scan.LiveY & 15));
                int gap = World.UnanchoredVerticalGapLength(cell, length);
                if (gap < length) { scan.LiveCell = cell + Vector3Int.up * gap; return 1; }
                scan.LiveY += length;
            }
            return 2;
        }
        void AdvanceFairBlockedScan(FairPublicationSeed seed)
        {
            var scan = seed.Scan;
            if (!FairScanValid(scan)) { RejectFairClassification(seed); return; }
            if (_now >= scan.ExpiresAt)
            { FairExpiredScans++; RejectFairClassification(seed); return; }
            if (scan.Finished) return;
            var timer = Stopwatch.StartNew(); int checks = 0;
            while (HasStageBudget(checks, MaxChecks, timer, .75))
            {
                if (!scan.UnitOpen)
                {
                    int count = scan.Phase == 0 ? scan.ColumnCount : scan.MotionCount;
                    if (scan.Cursor == count)
                    {
                        if (scan.Phase == 0) { scan.Phase = 1; scan.Cursor = 0; continue; }
                        if (!FairScanValid(scan) || _now >= scan.ExpiresAt) { RejectFairClassification(seed); return; }
                        scan.Finished = true; FairCompleteBlockedScans++;
                        FairLastCompleteScanColumns = scan.ColumnCount; FairLastCompleteScanMotions = scan.MotionCount;
                        if (scan.HasBlocker && _commands.Count != 0)
                        {
                            // Never serve a tail directly. Each atomic head segment
                            // is followed by a fresh complete latest-source replay.
                            _fairLastDependencyBlocker = scan.Blocker; _fairLastDependencyHead = _commands.Peek();
                            _fairLastDependencySource = scan.Source; _fairLastDependencyColumn = scan.BlockerColumn;
                            _fairLastDependencyCell = scan.BlockerCell; _fairLastDependencyRun = scan.BlockerRun;
                            _fairLastDependencyReservationVersion = scan.ReservationVersion;
                            EnterFairForeground(seed, _fairLastDependencyHead, true); return;
                        }
                        if (!scan.HasBlocker && !scan.HasSleeping)
                        { FairNoRemainingBackground++; CancelFairScheduling(); }
                        else if (!scan.HasBlocker && scan.HasSleeping && _commands.Count != 0)
                        {
                            // Only future motion remains in this complete current-source proof.
                            // Keep the background lease and serve one ordinary live FIFO head segment;
                            // its commit returns to the normal fresh background replay.
                            var head = _commands.Peek();
                            if (head.OrdinaryMainAttack && !head.Done && !head.Completed) EnterFairForeground(seed, head, false);
                        }
                        return;
                    }
                    if (!SpendFairScan(ref checks, 4, timer)) return;
                    if (scan.Phase == 0)
                    {
                        scan.Column = _cohortColumnOrder[scan.Cursor];
                        if (!_cohortColumnWork.ContainsKey(scan.Column)) { RejectFairClassification(seed); return; }
                    }
                    else
                    {
                        scan.Motion = _cohortMotionOrder[scan.Cursor];
                        if (!_cohortMotionWork.TryGetValue(scan.Motion, out var work)) { RejectFairClassification(seed); return; }
                        scan.Column = work.Column;
                    }
                    if (HistoricalRecoveryTerminal(scan.Column)) { scan.Cursor++; continue; }
                    BeginFairLiveUnit(scan); scan.UnitOpen = true;
                }
                if (scan.Reservations == null)
                {
                    int live = AdvanceFairLiveUnit(scan, ref checks, timer);
                    if (live == 0) return;
                    if (live == 2) { scan.UnitOpen = false; scan.Cursor++; continue; }
                    scan.Reservations = _destructionReservations.GetEnumerator();
                }
                if (!SpendFairScan(ref checks, 2, timer)) return;
                if (scan.Reservations.MoveNext())
                {
                    var command = scan.Reservations.Current;
                    if (command.Done || command.Completed) continue;
                    var center = new Vector2((scan.Column.x + .5f) * BlockWorld.CellSize, (scan.Column.y + .5f) * BlockWorld.CellSize);
                    float radius = Mathf.Max(command.Radius, command.EnemyOnly ? 0 : command.FriendlyRadius) + .051f;
                    if ((center - new Vector2(command.Point.x, command.Point.z)).sqrMagnitude > radius * radius) continue;
                    scan.HasBlocker = true;
                    if (scan.Blocker == null)
                    { scan.Blocker = command; scan.BlockerColumn = scan.Column; scan.BlockerCell = scan.LiveCell; scan.BlockerRun = scan.LiveRun; }
                    scan.Reservations.Dispose(); scan.Reservations = null;
                    scan.UnitOpen = false; scan.Cursor++; continue;
                }
                scan.Reservations.Dispose(); scan.Reservations = null;
                if (scan.Phase == 0) { RejectFairClassification(seed); return; }
                if (!SpendFairScan(ref checks, 8, timer))
                {
                    // No cursor advancement and no successful classification.
                    // Restart this same unit's bounded reservation enumeration.
                    scan.UnitOpen = false; return;
                }
                if (!TryFairSleepDeadline(scan.Motion, out double deadline)) { RejectFairClassification(seed); return; }
                scan.HasSleeping = true; scan.ExpiresAt = Math.Min(scan.ExpiresAt, deadline);
                scan.UnitOpen = false; scan.Cursor++;
            }
        }
        bool PrepareFairAuthorityStage(out BlockWorld next)
        {
            next = null; var seed = _fairSeed;
            if (seed == null || seed.Foreground) return false;
            if (!FairContextValid(seed)) { FairContextInvalidations++; CancelFairScheduling(); return true; }
            if (seed.Scan != null) { AdvanceFairBlockedScan(seed); return true; }
            if (seed.Source != null && !FairSourceValid(seed.Source, seed.SourceRevision, seed.Round, seed.SourceMutation))
                DiscardFairReplay(seed);
            if (seed.Replay != null) { next = seed.Replay; return false; }
            if (seed.Clone == null)
            {
                seed.Source = World; seed.SourceRevision = Revision; seed.SourceMutation = World.MutationGeneration;
                seed.Clone = World.BeginBudgetedClone(); FairBackgroundRebuilds++;
            }
            // Readiness does not authorize TakeWorld: validate the exact current
            // source, epoch, round, authority, remote queue and accepting state.
            if (!FairContextValid(seed) || !seed.Clone.IsValid(World))
            { FairContextInvalidations++; DiscardFairReplay(seed); return true; }
            if (seed.Clone.Ready)
            { seed.Replay = seed.Clone.TakeWorld(); seed.Clone = null; next = seed.Replay; return false; }
            var timer = Stopwatch.StartNew(); int checks = 0;
            while (HasStageBudget(checks, MaxChecks, timer, .75) && !seed.Clone.Ready)
            {
                int cost = seed.Clone.EstimateNextStepWork();
                if (!SpendGravity(ref checks, cost, MaxChecks, timer, .75)) break;
                seed.Clone.Step(World, cost, out _);
                if (seed.Clone.Failed) { FairContextInvalidations++; DiscardFairReplay(seed); break; }
            }
            // Clone and real gravity share the normal finite stage envelope by
            // occupying separate stages; no extra stage budget is silently added.
            return true;
        }
    }
}
