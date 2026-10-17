using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        sealed class GravityStartupTicket
        {
            public Vector2Int Column;
            // All edits to a committed ticket use Copy(), including timing.
            public double AddedAt = -1, RemovedAt = -1;
            public double CheckStartedAt = -1, ProofCompletedAt = -1;
            public uint AdditionRevision, RemovalRevision, Round, Generation;
            public bool PendingAddition, PendingRemoval;
            public GravityStartupTicket Copy() => (GravityStartupTicket)MemberwiseClone();
            public double TriggerAt => AddedAt < 0 ? RemovedAt : RemovedAt < 0 ? AddedAt : Math.Min(AddedAt, RemovedAt);
            public uint TriggerRevision => AddedAt >= 0 && (RemovedAt < 0 || AddedAt <= RemovedAt) ? AdditionRevision : RemovalRevision;
        }

        sealed class GravityStartupJournal
        {
            public int ColumnCursor, MotionCursor;
            public bool ColumnTurn, MotionTurn;
            // Debt is charged by real admitted checks, not by number of visits.
            // Positive debt owes ordinary work; ordinary checks pay twice their cost.
            public long ColumnDebt, MotionDebt;
            public bool VisitActive, VisitMotion, VisitPriority, VisitRejected;
            public ulong ServedVisits;
            public readonly Dictionary<Vector2Int, GravityStartupTicket> Columns = new();
            public readonly Dictionary<GravityMotionId, GravityStartupTicket> Motions = new();
            public readonly List<Vector2Int> NewColumns = new();
            public readonly List<GravityMotionId> NewMotions = new();

        }

        [Serializable]
        public sealed class GravityStartupSample
        {
            public ulong cohort;
            public int columnX, columnZ;
            public uint round, removalRevision, activationRevision, creationRevision, triggerRevision, columnGeneration;
            // reason is NewBuild, SupportRemoval, or NewBuild+SupportRemoval.
            public string reason;
            public double supportRemovedAt = -1, createdAt = -1, triggerAt = -1;
            public double checkStartedAt = -1, proofCompletedAt = -1, activatedAt, firstPresentedAt = -1;
            public double queueLatencyMs = -1, proofLatencyMs = -1, publicationLatencyMs = -1;
            public double activationLatencyMs, presentationLatencyMs = -1, totalLatencyMs = -1;
            public float activationOffset, firstPresentedOffset;
        }

        const int GravityStartupSampleCapacity = 1024;
        readonly Dictionary<Vector2Int, GravityStartupTicket> _startupColumns = new();
        readonly Dictionary<GravityMotionId, GravityStartupTicket> _startupMotions = new();
        readonly List<Vector2Int> _startupColumnOrder = new();
        readonly List<GravityMotionId> _startupMotionOrder = new();
        readonly Dictionary<Vector2Int, int> _startupColumnSlots = new();
        readonly Dictionary<GravityMotionId, int> _startupMotionSlots = new();
        readonly Queue<GravityStartupSample> _startupSamples = new();
        readonly Dictionary<ulong, GravityStartupSample> _startupAwaitPresentation = new();
        readonly List<(GravityStartupSample sample, float offset)> _startupPresentation = new();
        readonly HashSet<ulong> _startupPresentationCohorts = new();
        int _startupColumnCursor, _startupMotionCursor;
        bool _startupColumnTurn = true, _startupMotionTurn = true;
        long _startupColumnDebt, _startupMotionDebt;
        public long GravityStartupSamplesDropped { get; private set; }
        public ulong GravityStartupServedVersion { get; private set; }
        public int GravityStartupPriorityColumns => _startupColumns.Count;
        public int GravityStartupPriorityMotions => _startupMotions.Count;
        public GravityStartupSample[] InspectGravityStartupSamples() => _startupSamples.ToArray();

        GravityStartupJournal NewGravityStartupJournal() => new()
        {
            ColumnCursor = _startupColumnCursor, MotionCursor = _startupMotionCursor,
            ColumnTurn = _startupColumnTurn, MotionTurn = _startupMotionTurn,
            ColumnDebt = _startupColumnDebt, MotionDebt = _startupMotionDebt
        };
        GravityStartupJournal CurrentGravityStartup()
            => _gravityJournal.Startup ??= NewGravityStartupJournal();

        void ResetGravityStartup()
        {
            _startupColumns.Clear(); _startupMotions.Clear();
            _startupColumnOrder.Clear(); _startupMotionOrder.Clear();
            _startupColumnSlots.Clear(); _startupMotionSlots.Clear();
            _startupSamples.Clear(); _startupAwaitPresentation.Clear(); _startupPresentation.Clear(); _startupPresentationCohorts.Clear();
            _startupColumnCursor = _startupMotionCursor = 0;
            _startupColumnTurn = _startupMotionTurn = true;
            _startupColumnDebt = _startupMotionDebt = 0; GravityStartupSamplesDropped = 0;
            GravityStartupServedVersion = 0;
        }

        GravityStartupTicket StartupColumnTicket(GravityStartupJournal startup, Vector2Int column)
            => startup.Columns.TryGetValue(column, out var staged) ? staged
                : _startupColumns.TryGetValue(column, out var committed) ? committed : null;

        GravityStartupTicket StartupMotionTicket(GravityStartupJournal startup, GravityMotionId id)
            => startup.Motions.TryGetValue(id, out var staged) ? staged
                : _startupMotions.TryGetValue(id, out var committed) ? committed : null;

        uint StartupGeneration(Vector2Int column)
            => _gravityJournal.Generations.TryGetValue(column, out uint staged) ? staged
                : _landingGeneration.TryGetValue(column, out uint committed) ? committed : 0;

        GravityStartupTicket EditStartupColumn(GravityStartupJournal startup, Vector2Int column)
        {
            if (startup.Columns.TryGetValue(column, out var staged)) return staged;
            var ticket = StartupColumnTicket(startup, column);
            if (ticket == null) return null;
            return startup.Columns[column] = ticket.Copy();
        }
        GravityStartupTicket EditStartupMotion(GravityStartupJournal startup, GravityMotionId id)
        {
            if (startup.Motions.TryGetValue(id, out var staged)) return staged;
            var ticket = StartupMotionTicket(startup, id);
            if (ticket == null) return null;
            return startup.Motions[id] = ticket.Copy();
        }
        static void RefreshStartupGeneration(GravityStartupTicket ticket, uint generation)
        {
            if (ticket.Generation == generation) return;
            ticket.Generation = generation;
            // Column edits invalidate partial proof timing, never the earliest
            // still-unresolved cause. The proof itself retains existing guards.
            ticket.CheckStartedAt = ticket.ProofCompletedAt = -1;
        }

        // Queue only after a real cell edit and QueueGravity. The cause gets an
        // observed timestamp only when that exact draft becomes World.
        void QueueGravityStartupRemoval(Vector3Int cell) => QueueGravityStartupCause(cell, false);
        void QueueGravityStartupAddition(Vector3Int cell) => QueueGravityStartupCause(cell, true);
        void QueueGravityStartupCause(Vector3Int cell, bool addition)
        {
            if (_gravityJournal == null) return;
            var startup = CurrentGravityStartup(); var column = new Vector2Int(cell.x, cell.z);
            var ticket = EditStartupColumn(startup, column);
            if (ticket == null || ticket.Round != Round)
            {
                ticket = new GravityStartupTicket { Column = column, Round = Round };
                if (!_startupColumnSlots.ContainsKey(column) && !startup.Columns.ContainsKey(column)) startup.NewColumns.Add(column);
                startup.Columns[column] = ticket;
            }
            RefreshStartupGeneration(ticket, StartupGeneration(column));
            if (addition) { if (ticket.AddedAt < 0) ticket.PendingAddition = true; }
            else if (ticket.RemovedAt < 0) ticket.PendingRemoval = true;
        }

        void QueueGravityStartupMotion(GravityMotionId id, Vector2Int column)
        {
            var startup = CurrentGravityStartup(); var ticket = StartupColumnTicket(startup, column);
            if (ticket == null || ticket.Round != Round || StartupMotionTicket(startup, id) != null) return;
            if (!_startupMotionSlots.ContainsKey(id) && !startup.Motions.ContainsKey(id)) startup.NewMotions.Add(id);
            startup.Motions[id] = ticket.Copy();
        }

        void ObserveGravityStartupCheck(Vector2Int column)
        {
            var ticket = EditStartupColumn(CurrentGravityStartup(), column);
            if (ticket == null || ticket.Round != Round) return;
            RefreshStartupGeneration(ticket, StartupGeneration(column));
            if (ticket.CheckStartedAt < 0) ticket.CheckStartedAt = _now;
        }
        void ObserveGravityStartupProof(GravityMotionId id)
        {
            var ticket = EditStartupMotion(CurrentGravityStartup(), id);
            if (ticket == null || ticket.Round != Round) return;
            RefreshStartupGeneration(ticket, StartupGeneration(ticket.Column));
            if (ticket.CheckStartedAt < 0) ticket.CheckStartedAt = _now;
            // This hook is called only when a real landing proof completes.
            // A recomputed proof replaces the prior completion time, including
            // native revalidation retries that leave the column generation equal.
            ticket.ProofCompletedAt = _now;
        }

        void BeginGravityStartupVisit(bool motion, bool priority)
        {
            var startup = CurrentGravityStartup();
            startup.VisitActive = true; startup.VisitMotion = motion; startup.VisitPriority = priority; startup.VisitRejected = false;
        }
        void EndGravityStartupVisit() => CurrentGravityStartup().VisitActive = false;
        void ObserveGravityStartupVisitDeferred()
        {
            var startup = _gravityJournal?.Startup;
            if (startup != null && startup.VisitActive) startup.VisitRejected = true;
        }
        void ObserveGravityStartupColumnServed(Vector2Int column)
        {
            var startup = CurrentGravityStartup(); var ticket = StartupColumnTicket(startup, column);
            if (!startup.VisitRejected && ticket != null && ticket.Round == Round) startup.ServedVisits++;
        }
        void ObserveGravityStartupMotionServed(GravityMotionId id)
        {
            var startup = CurrentGravityStartup(); var ticket = StartupMotionTicket(startup, id);
            if (!startup.VisitRejected && ticket != null && ticket.Round == Round) startup.ServedVisits++;
        }
        void ChargeGravityStartupWork(int checks)
        {
            var startup = _gravityJournal?.Startup;
            if (startup == null || !startup.VisitActive || checks <= 0) return;
            ChargeGravityStartupChecks(startup, startup.VisitMotion, startup.VisitPriority, checks);
        }
        void ChargeGravityStartupChecks(GravityStartupJournal startup, bool motion, bool priority, int checks)
        {
            // No queued priority work means there is no entitlement to bank.
            bool queued = motion ? _startupMotionOrder.Count + startup.NewMotions.Count != 0
                : _startupColumnOrder.Count + startup.NewColumns.Count != 0;
            ref long debt = ref (motion ? ref startup.MotionDebt : ref startup.ColumnDebt);
            debt = queued ? debt + (priority ? checks : -2L * checks) : 0;
            if (motion) startup.MotionTurn = debt <= 0;
            else startup.ColumnTurn = debt <= 0;
        }

        bool TrySelectStartupColumn(ref int checks, int maximum, Stopwatch timer, double milliseconds, out Vector2Int column)
        {
            column = default; var startup = CurrentGravityStartup();
            int count = _startupColumnOrder.Count + startup.NewColumns.Count;
            if (!startup.ColumnTurn || count == 0 || !SpendGravity(ref checks, 2, maximum, timer, milliseconds)) return false;
            ChargeGravityStartupChecks(startup, false, true, 2);
            int slot = startup.ColumnCursor++ % count;
            column = slot < _startupColumnOrder.Count ? _startupColumnOrder[slot] : startup.NewColumns[slot - _startupColumnOrder.Count];
            var ticket = StartupColumnTicket(startup, column);
            if (ticket == null) return false;
            if (ticket.Round != Round) { startup.Columns[column] = null; return false; }
            if (_gravityJournal.Columns.TryGetValue(column, out var work) && work == null)
            { startup.Columns[column] = null; return false; }
            return true;
        }

        bool TrySelectStartupMotion(ref int checks, int maximum, Stopwatch timer, double milliseconds, out GravityMotionId id)
        {
            id = default; var startup = CurrentGravityStartup();
            int count = _startupMotionOrder.Count + startup.NewMotions.Count;
            if (!startup.MotionTurn || count == 0 || !SpendGravity(ref checks, 2, maximum, timer, milliseconds)) return false;
            ChargeGravityStartupChecks(startup, true, true, 2);
            int slot = startup.MotionCursor++ % count;
            id = slot < _startupMotionOrder.Count ? _startupMotionOrder[slot] : startup.NewMotions[slot - _startupMotionOrder.Count];
            var ticket = StartupMotionTicket(startup, id);
            if (ticket == null) return false;
            if (ticket.Round != Round) { startup.Motions[id] = null; return false; }
            if (_gravityJournal.Motions.TryGetValue(id, out var work) && work == null)
            { startup.Motions[id] = null; return false; }
            return true;
        }

        void CommitGravityStartup(GravityJournal journal)
        {
            // Private/recovery journals can activate an already-prioritized
            // cohort without going through the ordinary stage constructor.
            var startup = journal.Startup ?? NewGravityStartupJournal();
            // A no-publication proof-only stage may commit cursors, but cannot
            // assign an observed support-removal time to an unpublished draft.
            bool published = ReferenceEquals(World, journal.Next);
            void CommitCause(GravityStartupTicket ticket)
            {
                if (ticket == null) return;
                if (!published) { ticket.PendingAddition = ticket.PendingRemoval = false; return; }
                if (ticket.PendingAddition) { ticket.AddedAt = _now; ticket.AdditionRevision = Revision; ticket.PendingAddition = false; }
                if (ticket.PendingRemoval) { ticket.RemovedAt = _now; ticket.RemovalRevision = Revision; ticket.PendingRemoval = false; }
                if (ticket.CheckStartedAt >= 0) ticket.CheckStartedAt = Math.Max(ticket.TriggerAt, ticket.CheckStartedAt);
                if (ticket.ProofCompletedAt >= 0) ticket.ProofCompletedAt = Math.Max(ticket.CheckStartedAt, ticket.ProofCompletedAt);
            }
            foreach (var ticket in startup.Columns.Values) CommitCause(ticket);
            foreach (var ticket in startup.Motions.Values) CommitCause(ticket);
            foreach (var pair in startup.Columns)
            {
                if (pair.Value == null || pair.Value.TriggerAt < 0 || pair.Value.Round != Round) { RemoveStartupColumn(pair.Key); continue; }
                _startupColumns[pair.Key] = pair.Value;
                if (!_startupColumnSlots.ContainsKey(pair.Key))
                { _startupColumnSlots.Add(pair.Key, _startupColumnOrder.Count); _startupColumnOrder.Add(pair.Key); }
            }
            foreach (var pair in startup.Motions)
            {
                if (pair.Value == null || pair.Value.TriggerAt < 0 || pair.Value.Round != Round) { RemoveStartupMotion(pair.Key); continue; }
                _startupMotions[pair.Key] = pair.Value;
                if (!_startupMotionSlots.ContainsKey(pair.Key))
                { _startupMotionSlots.Add(pair.Key, _startupMotionOrder.Count); _startupMotionOrder.Add(pair.Key); }
            }
            foreach (var pair in journal.Columns) if (pair.Value == null) RemoveStartupColumn(pair.Key);
            foreach (var pair in journal.Motions)
            {
                if (pair.Value == null) RemoveStartupMotion(pair.Key);
                else ObserveGravityStartupActivation(pair.Key);
            }
            // A column can finish its last piece using an already-committed
            // proof, without editing the motion in this particular stage.
            foreach (var pair in journal.OwnActivationProofs)
                ObserveGravityStartupActivation(new GravityMotionId(pair.Key, true));
            _startupColumnCursor = _startupColumnOrder.Count == 0 ? 0 : startup.ColumnCursor % _startupColumnOrder.Count;
            _startupMotionCursor = _startupMotionOrder.Count == 0 ? 0 : startup.MotionCursor % _startupMotionOrder.Count;
            _startupColumnTurn = startup.ColumnTurn; _startupMotionTurn = startup.MotionTurn;
            _startupColumnDebt = startup.ColumnDebt; _startupMotionDebt = startup.MotionDebt;
            // Empty queues cannot bank old priority credit for a later cause.
            if (_startupColumns.Count == 0) { _startupColumnDebt = 0; _startupColumnTurn = true; }
            if (_startupMotions.Count == 0) { _startupMotionDebt = 0; _startupMotionTurn = true; }
            if (startup.ServedVisits != 0)
            {
                GravityStartupServedVersion += startup.ServedVisits;
                _spreadOwesGravity = false;
            }
        }

        void ObserveGravityStartupActivation(GravityMotionId id)
        {
            if (!_startupMotions.TryGetValue(id, out var ticket)) return;
            if (!World.Cohorts.TryGetValue(id.Id, out var group)) { RemoveStartupMotion(id); return; }
            if (group.Phase != FallingCohortPhase.Falling && group.Phase != FallingCohortPhase.Settling) return;
            var sample = new GravityStartupSample { cohort = group.Id, columnX = ticket.Column.x, columnZ = ticket.Column.y,
                round = Round, removalRevision = ticket.RemovalRevision, creationRevision = ticket.AdditionRevision,
                triggerRevision = ticket.TriggerRevision, columnGeneration = ticket.Generation, activationRevision = Revision,
                reason = ticket.AddedAt >= 0 ? ticket.RemovedAt >= 0 ? "NewBuild+SupportRemoval" : "NewBuild" : "SupportRemoval",
                supportRemovedAt = ticket.RemovedAt, createdAt = ticket.AddedAt, triggerAt = ticket.TriggerAt,
                checkStartedAt = ticket.CheckStartedAt, proofCompletedAt = ticket.ProofCompletedAt,
                activatedAt = _now, activationOffset = group.InitialOffset,
                activationLatencyMs = Math.Max(0, (_now - ticket.TriggerAt) * 1000),
                queueLatencyMs = ticket.CheckStartedAt < 0 ? -1 : Math.Max(0, (ticket.CheckStartedAt - ticket.TriggerAt) * 1000),
                proofLatencyMs = ticket.ProofCompletedAt < 0 || ticket.CheckStartedAt < 0 ? -1 : Math.Max(0, (ticket.ProofCompletedAt - ticket.CheckStartedAt) * 1000),
                publicationLatencyMs = ticket.ProofCompletedAt < 0 ? -1 : Math.Max(0, (_now - ticket.ProofCompletedAt) * 1000) };
            if (_startupSamples.Count == GravityStartupSampleCapacity)
            {
                var dropped = _startupSamples.Dequeue();
                if (_startupAwaitPresentation.TryGetValue(dropped.cohort, out var waiting) && ReferenceEquals(waiting, dropped))
                    _startupAwaitPresentation.Remove(dropped.cohort);
                GravityStartupSamplesDropped++;
            }
            _startupSamples.Enqueue(sample); _startupAwaitPresentation[group.Id] = sample;
            RemoveStartupMotion(id);
        }

        void RemoveStartupColumn(Vector2Int column)
        { _startupColumns.Remove(column); RemoveSlot(column, _startupColumnSlots, _startupColumnOrder); }
        void RemoveStartupMotion(GravityMotionId id)
        { _startupMotions.Remove(id); RemoveSlot(id, _startupMotionSlots, _startupMotionOrder); }

        // The normal renderer queues only runs with submitted occupied cells,
        // then commits these samples after all instanced draws succeed. This is
        // draw-submission evidence, not camera visibility or pixel verification.
        void BeginGravityStartupPresentation()
        { _startupPresentation.Clear(); _startupPresentationCohorts.Clear(); }
        void QueueGravityStartupPresentation(FallingRun run, float offset)
        {
            if (run.Cohort == null || !_startupAwaitPresentation.TryGetValue(run.Cohort.Id, out var sample)
                || offset - sample.activationOffset <= .001f || !_startupPresentationCohorts.Add(run.Cohort.Id)) return;
            _startupPresentation.Add((sample, offset));
        }
        void CommitGravityStartupPresentation(double now)
        {
            foreach (var entry in _startupPresentation)
            {
                var sample = entry.sample;
                if (sample.firstPresentedAt >= 0 || now < sample.activatedAt) continue;
                sample.firstPresentedAt = now; sample.firstPresentedOffset = entry.offset;
                sample.presentationLatencyMs = (now - sample.activatedAt) * 1000;
                sample.totalLatencyMs = (now - sample.triggerAt) * 1000;
                _startupAwaitPresentation.Remove(sample.cohort);
            }
            _startupPresentation.Clear(); _startupPresentationCohorts.Clear();
        }

        static bool TryGravityLandingRow(BlockWorld world, Vector2Int column, int scan, int minimum,
            FallingCohort group, out int support, out int nextScan)
        {
            int low = Math.Max(minimum, scan & ~15), length = scan - low + 1;
            var at = new Vector3Int(column.x, low, column.y); int local = low & 15;
            uint occupied = ((uint)world.OccupiedRow(at, 1) >> local) & ((1u << length) - 1);
            if (group != null && group.Phase == FallingCohortPhase.Detaching)
            {
                int firstOwn = Math.Max(low, group.DetachCursor), lastOwn = Math.Min(scan + 1, group.TopExclusive);
                if (firstOwn < lastOwn) occupied &= ~(((1u << (lastOwn - firstOwn)) - 1) << (firstOwn - low));
            }
            nextScan = low - 1;
            support = occupied == 0 ? 0 : low + HighestBuildBit(occupied);
            return occupied != 0;
        }
    }
}
