using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        public enum HistoricalRecoveryOutcome { None, Preparing, Recovered, NoCapacity, UncertifiedStaticQuery, Cancelled, NoLongerInvalid, PublicationConflict }
        public HistoricalRecoveryOutcome LastHistoricalRecoveryOutcome { get; private set; }
        public long HistoricalRecoveryStarts, HistoricalRecoveryStages, HistoricalRecoveryWork, HistoricalRecoveryCount, HistoricalRecoveryNoCapacity;
        public long LastHistoricalPublicationWork;
        public long HistoricalRecoveryPublicationConflicts;
        // Lifetime O(1) path counters; compare deltas from the same observation window.
        public long HistoricalNoLongerInvalidMissingTarget, HistoricalNoLongerInvalidInactivePhase;
        public long HistoricalNoLongerInvalidClassifiedValid, HistoricalNoLongerInvalidWitnessGone, HistoricalOwnedCloneStarts;
        const int HistoricalPublicationRetryLimit = 8;
        public ulong HistoricalRecoveryProgressVersion { get; private set; }
        sealed class HistoricalRecoveryRequest
        {
            public BlockPublicationWindow Window;
            public GravityMotionId Motion;
            public Vector2Int Column;
            public uint Epoch;
            public int PublicationAttempts;
        }
        sealed class HistoricalRecoveryRequired : Exception
        {
            public readonly HistoricalRecoveryRequest Request;
            public HistoricalRecoveryRequired(HistoricalRecoveryRequest request) : base("Published source requires classified historical geometry recovery.") { Request = request; }
        }
        sealed class HistoricalRecoveryPreparation
        {
            public HistoricalRecoveryRequest Request;
            public BuildOccupancyPreparation Scratch;
            public BuildColumnProof Column;
            public BuildSourceMotion Target;
            public double SourceTime;
            public int Phase, Candidate, CandidateScan, SourceBottom, Span, FirstOccupied, Landing, LandingScan, MaximumTop, PhysicalMaximumTop;
            public bool Invalid, Saturated, OwnedForMutation;
            public BuildOccupiedCell Witness;
            public BuildOccupiedCell WitnessOther;
            public LinkedListNode<BuildOccupiedCell> CellCursor;
            public LinkedListNode<BuildOccupiedCell> DynamicTargetCursor, DynamicOtherCursor;
            public IEnumerator<BuildSourceMotion> SampleCursor;
            public IEnumerator<GravityMotionKey> SupportCursor;
            public GravityMotionKey BufferedSupport, SupportPredecessor;
            public bool SupportScanStarted, SupportBuffered;
            public BuildSourceMotion SupportLower;
            public BuildSourceMotion BufferedSample;
            public IEnumerator<ulong> UpperCursor;
            public IEnumerator<GravityMotionKey> HeadCursor;
            public SortedSet<GravityMotionKey> PreparedHeads;
            public IEnumerator<ulong> MemberCursor;
            public LinkedList<GravityStaticProof> StaticProofs = new();
            public readonly LinkedList<HistoricalUpperGuard> UpperGuards = new();
            public int Delta;
            public float InitialOffset;
            public bool WitnessGone, SourceLost;
            public BlockWorld Source => Scratch.Source;
            public BlockWorld Next => Scratch.Next;
            public bool SourceValid(BlockTerrain terrain) => Scratch.SourceValid(terrain);
        }
        sealed class HistoricalUpperGuard
        {
            public int SourceBottom;
            public BuildMotionSample Sample;
        }
        HistoricalRecoveryRequest _historicalRecoveryRequest;
        HistoricalRecoveryPreparation _historicalRecoveryPreparation;
        BlockWorld _historicalTerminalSource;
        ulong _historicalTerminalMutation;
        Dictionary<Vector2Int, HistoricalRecoveryOutcome> _historicalTerminals = new();
        readonly Collider[] _historicalOverlapHits = new Collider[64];
        bool HasHistoricalRecovery => _historicalRecoveryRequest != null || _historicalRecoveryPreparation != null;
        bool HistoricalRecoveryTerminal(Vector2Int column)
            => ReferenceEquals(_historicalTerminalSource, World) && _historicalTerminalMutation == World.MutationGeneration && _historicalTerminals.ContainsKey(column);
        public void RecheckHistoricalGeometry()
        {
            // An explicit world/static-map correction can request a new finite
            // proof without discarding any diagnosed source volume.
            _historicalTerminalSource = null; _historicalTerminals = new Dictionary<Vector2Int, HistoricalRecoveryOutcome>();
        }
        void SetHistoricalTerminal(HistoricalRecoveryRequest request, HistoricalRecoveryOutcome outcome)
        {
            if (!ReferenceEquals(_historicalTerminalSource, World) || _historicalTerminalMutation != World.MutationGeneration)
            { _historicalTerminalSource = World; _historicalTerminalMutation = World.MutationGeneration; _historicalTerminals = new Dictionary<Vector2Int, HistoricalRecoveryOutcome>(); }
            _historicalTerminals[request.Column] = outcome;
            if (outcome == HistoricalRecoveryOutcome.NoCapacity) HistoricalRecoveryNoCapacity++;
        }
        bool RequestHistoricalRecovery(GravityMotionId motion, Vector2Int column)
            => RequestWindowHistoricalRecovery(motion, column, _gravityJournal?.Window ?? _stagePublicationWindow
                ?? _publicationPolicy.Capture(BlockPublicationOperation.Gravity));
        bool RequestWindowHistoricalRecovery(GravityMotionId motion, Vector2Int column, BlockPublicationWindow window)
        {
            if (HistoricalRecoveryTerminal(column)) return false;
            var request = new HistoricalRecoveryRequest { Motion = motion, Column = column, Epoch = _epoch, Window = window };
            if (_gravityJournal != null) _gravityJournal.RecoveryRequest ??= request;
            else _historicalRecoveryRequest ??= request;
            return true;
        }
        void AbortHistoricalRecovery()
        {
            CancelBuildOverlapRepairPreparation(_historicalRecoveryPreparation?.Request ?? _historicalRecoveryRequest);
            _historicalRecoveryRequest = null;
            var preparation = _historicalRecoveryPreparation; _historicalRecoveryPreparation = null;
            if (preparation == null) return;
            preparation.Scratch.Clone?.Dispose(); preparation.Column.Heads?.Dispose();
            preparation.SampleCursor?.Dispose(); preparation.SupportCursor?.Dispose(); preparation.UpperCursor?.Dispose(); preparation.HeadCursor?.Dispose(); preparation.MemberCursor?.Dispose();
            LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.Cancelled;
        }
        void FinishHistoricalRecovery(HistoricalRecoveryOutcome outcome, bool terminal)
        {
            var preparation = _historicalRecoveryPreparation;
            if (terminal && preparation != null)
                SetHistoricalTerminal(preparation.Request, outcome);
            CompleteBuildOverlapRepairPreparation(preparation?.Request, null, outcome == HistoricalRecoveryOutcome.NoLongerInvalid);
            AbortHistoricalRecovery(); LastHistoricalRecoveryOutcome = outcome;
        }
        bool SpendHistoricalRecovery(ref int checks, int cost, int maximum, Stopwatch timer, double milliseconds)
        {
            if (!SpendGravity(ref checks, cost, maximum, timer, milliseconds)) return false;
            HistoricalRecoveryWork += cost; HistoricalRecoveryProgressVersion++; return true;
        }
        bool HistoricalStaticOverlap(Vector3 center, out bool saturated)
        {
            int hits = Physics.OverlapBoxNonAlloc(center, Vector3.one * (BlockWorld.CellSize * .5f - .000001f),
                _historicalOverlapHits, Quaternion.identity, Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << TerrainLayer), QueryTriggerInteraction.Ignore);
            saturated = hits == _historicalOverlapHits.Length;
            if (saturated) return true;
            for (int i = 0; i < hits; i++) if (_staticColliders.Contains(_historicalOverlapHits[i])) return true;
            return false;
        }
        bool HistoricalSourceCellInvalid(BuildOccupiedCell cell, double time, bool includeStatic, out bool saturated)
        {
            saturated = false; cell.Sample.Evaluate(time);
            float bottom = cell.SourceCell.y * BlockWorld.CellSize - cell.Sample.Offset;
            if (bottom < World.Bottom - .000001f) return true;
            int first = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize);
            int last = Mathf.CeilToInt((bottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
            for (int y = first; y <= last; y++)
                if (World.TryGet(new Vector3Int(cell.SourceCell.x, y, cell.SourceCell.z), out _)) return true;
            return includeStatic && HistoricalStaticOverlap(new Vector3((cell.SourceCell.x + .5f) * BlockWorld.CellSize,
                bottom + BlockWorld.CellSize * .5f, (cell.SourceCell.z + .5f) * BlockWorld.CellSize), out saturated);
        }
        static bool HistoricalDynamicCellsOverlap(BuildOccupiedCell a, BuildOccupiedCell b, double time)
        {
            if (ReferenceEquals(a.Sample, b.Sample)) return false;
            a.Sample.Evaluate(time); b.Sample.Evaluate(time);
            float first = a.SourceCell.y * BlockWorld.CellSize - a.Sample.Offset;
            float second = b.SourceCell.y * BlockWorld.CellSize - b.Sample.Offset;
            return first < second + BlockWorld.CellSize - .000001f && second < first + BlockWorld.CellSize - .000001f;
        }
        bool AdvanceHistoricalRecoveryPreparation()
        {
            if (!HasHistoricalRecovery) return false;
            bool commands = _commands.Count != 0;
            int maximum = commands ? BlockPublicationPolicy.GravityChecksWithCommands : BlockPublicationPolicy.GravityChecksAlone, checks = 0;
            double milliseconds = commands ? BlockPublicationPolicy.GravityMillisecondsWithCommands : BlockPublicationPolicy.GravityMillisecondsAlone;
            var timer = Stopwatch.StartNew(); long before = GravityWorkChecks;
            AuthorityStageCount++; HistoricalRecoveryStages++; LastCommandStageMilliseconds = 0;
            try
            {
                if (_historicalRecoveryPreparation == null)
                {
                    if (!SpendHistoricalRecovery(ref checks, 2 * BlockWorld.MaxCohortCells + 16, maximum, timer, milliseconds)) return true;
                    var request = _historicalRecoveryRequest; _historicalRecoveryRequest = null;
                    if (request.Epoch != _epoch || HistoricalRecoveryTerminal(request.Column)) return true;
                    int minimum = Mathf.CeilToInt(World.Bottom / BlockWorld.CellSize), length = Mathf.CeilToInt(BlockWorld.Top / BlockWorld.CellSize) - minimum;
                    if (length <= 0 || length > BlockWorld.MaxCohortCells) throw new InvalidOperationException("Historical recovery bounds exceed the original finite column.");
                    var column = new BuildColumnProof { Column = request.Column, Minimum = minimum, Length = length,
                        Added = new bool[length], AddedBelow = new int[length], Heads = _cohortHeads.TryGetValue(request.Column, out var heads) ? heads.GetEnumerator() : null };
                    var scratch = new BuildOccupancyPreparation { Source = World, Draft = World, Window = request.Window, Epoch = _epoch, Revision = Revision, Round = Round,
                        // Classification only reads the published source. It does not
                        // need independent writable metadata until a real repair.
                        Mutation = World.MutationGeneration, Next = World,
                        Journal = new GravityJournal { Next = World, Window = request.Window, ColumnCursor = _cohortColumnCursor, MotionCursor = _cohortMotionCursor } };
                    scratch.ColumnOrder.AddLast(column); scratch.Columns.Add(request.Column, column);
                    _historicalRecoveryPreparation = new HistoricalRecoveryPreparation { Request = request, Scratch = scratch, Column = column, SourceTime = _now, FirstOccupied = int.MaxValue, Phase = 1 };
                    HistoricalRecoveryStarts++; LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.Preparing;
                }
                var preparation = _historicalRecoveryPreparation;
                if (!preparation.SourceValid(this)) { FinishHistoricalRecovery(HistoricalRecoveryOutcome.Cancelled, false); return true; }
                Physics.SyncTransforms();
                if (preparation.Phase == 1)
                {
                    if (!AdvanceBuildColumn(preparation.Scratch, preparation.Column, ref checks, maximum, timer, milliseconds)) return true;
                    if (!preparation.Column.Motions.TryGetValue(preparation.Request.Motion, out preparation.Target))
                    { HistoricalNoLongerInvalidMissingTarget++; FinishHistoricalRecovery(HistoricalRecoveryOutcome.NoLongerInvalid, false); return true; }
                    var target = preparation.Target;
                    if (target.Group != null && target.Group.Phase != FallingCohortPhase.Falling && target.Group.Phase != FallingCohortPhase.Settling)
                    { HistoricalNoLongerInvalidInactivePhase++; FinishHistoricalRecovery(HistoricalRecoveryOutcome.NoLongerInvalid, false); return true; }
                    preparation.SourceBottom = target.Group != null ? target.Group.Bottom.y : target.Legacy.Bottom.y;
                    preparation.Span = target.Group != null ? target.Group.TopExclusive - target.Group.Bottom.y : target.Legacy.Cells.Length;
                    if (preparation.Span <= 0 || preparation.Span > BlockWorld.MaxCohortCells) throw new InvalidOperationException("Historical recovery cannot discard an invalid source span.");
                    preparation.MaximumTop = preparation.Column.Minimum + preparation.Column.Length;
                    preparation.PhysicalMaximumTop = preparation.MaximumTop;
                    preparation.SampleCursor = preparation.Column.Motions.Values.GetEnumerator(); preparation.Phase = 2;
                }
                while (preparation.Phase == 2 && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (preparation.BufferedSample == null)
                    {
                        if (!SpendHistoricalRecovery(ref checks, 1, maximum, timer, milliseconds)) return true;
                        if (!preparation.SampleCursor.MoveNext())
                        { preparation.SampleCursor.Dispose(); preparation.SampleCursor = null; preparation.CellCursor = preparation.Column.Cells.First; preparation.Phase = 3; break; }
                        preparation.BufferedSample = preparation.SampleCursor.Current;
                    }
                    var sample = preparation.BufferedSample.Sample;
                    if (!SpendHistoricalRecovery(ref checks, sample.Nodes.Count, maximum, timer, milliseconds)) return true;
                    sample.Evaluate(preparation.SourceTime); preparation.BufferedSample = null;
                }
                while (preparation.Phase == 3 && preparation.CellCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    var cell = preparation.CellCursor.Value;
                    bool target = ReferenceEquals(cell.Sample, preparation.Target.Sample);
                    int cost = target && !preparation.Invalid ? 69 : 4;
                    if (!SpendHistoricalRecovery(ref checks, cost, maximum, timer, milliseconds)) return true;
                    float bottom = cell.SourceCell.y * BlockWorld.CellSize - cell.Sample.Offset;
                    int first = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize);
                    int last = Mathf.CeilToInt((bottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
                    if (target)
                    {
                        if (!preparation.Invalid && HistoricalSourceCellInvalid(cell, preparation.SourceTime, true, out bool saturated))
                        { preparation.Saturated |= saturated; if (!saturated) { preparation.Invalid = true; preparation.Witness = cell; } }
                        preparation.FirstOccupied = Math.Min(preparation.FirstOccupied, cell.SourceCell.y - preparation.SourceBottom);
                    }
                    else for (int y = first; y <= last; y++) if (y >= preparation.Column.Minimum && y < preparation.Column.Minimum + preparation.Column.Length)
                        preparation.Column.Moving[y - preparation.Column.Minimum] = true;
                    preparation.CellCursor = preparation.CellCursor.Next;
                }
                if (preparation.Phase == 3 && preparation.CellCursor == null)
                {
                    preparation.DynamicTargetCursor = preparation.Column.Cells.First;
                    preparation.DynamicOtherCursor = preparation.Column.Cells.First;
                    preparation.Phase = 12;
                }
                // Pairwise classification is finite and resumable (<=400^2),
                // and compares exact physical spans. A projected grid row alone
                // cannot distinguish touching fractional bodies from overlap.
                while (preparation.Phase == 12 && !preparation.Invalid && preparation.DynamicTargetCursor != null
                    && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendHistoricalRecovery(ref checks, 4, maximum, timer, milliseconds)) return true;
                    var targetCell = preparation.DynamicTargetCursor.Value;
                    if (!ReferenceEquals(targetCell.Sample, preparation.Target.Sample))
                    { preparation.DynamicTargetCursor = preparation.DynamicTargetCursor.Next; preparation.DynamicOtherCursor = preparation.Column.Cells.First; continue; }
                    if (preparation.DynamicOtherCursor == null)
                    { preparation.DynamicTargetCursor = preparation.DynamicTargetCursor.Next; preparation.DynamicOtherCursor = preparation.Column.Cells.First; continue; }
                    var other = preparation.DynamicOtherCursor.Value;
                    if (HistoricalDynamicCellsOverlap(targetCell, other, preparation.SourceTime))
                    { preparation.Invalid = true; preparation.Witness = targetCell; preparation.WitnessOther = other; }
                    preparation.DynamicOtherCursor = preparation.DynamicOtherCursor.Next;
                }
                if (preparation.Phase == 12)
                {
                    if (!preparation.Invalid && preparation.DynamicTargetCursor != null) return true;
                    if (!preparation.Invalid)
                    {
                        if (!preparation.Saturated) HistoricalNoLongerInvalidClassifiedValid++;
                        FinishHistoricalRecovery(preparation.Saturated ? HistoricalRecoveryOutcome.UncertifiedStaticQuery : HistoricalRecoveryOutcome.NoLongerInvalid, preparation.Saturated); return true;
                    }
                    preparation.Candidate = Math.Max(preparation.Column.Minimum, Mathf.CeilToInt(preparation.SourceBottom - preparation.Target.Sample.Offset / BlockWorld.CellSize - .00001f));
                    preparation.CandidateScan = 0;
                    if (preparation.Target.Group != null && preparation.Next._upperSupports.TryGetValue(preparation.Target.Group.Id, out var uppers)) preparation.UpperCursor = uppers.GetEnumerator();
                    preparation.Phase = 4;
                }
                while (preparation.Phase == 4 && preparation.UpperCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendHistoricalRecovery(ref checks, 6, maximum, timer, milliseconds)) return true;
                    if (!preparation.UpperCursor.MoveNext()) { preparation.UpperCursor.Dispose(); preparation.UpperCursor = null; break; }
                    // Source coordinates stay unchanged when the physical repair
                    // fits below the original source bottom. Upward source reuse
                    // must also remain ordered for non-occupied descriptors.
                    var upper = preparation.Next.Cohorts[preparation.UpperCursor.Current];
                    preparation.MaximumTop = Math.Min(preparation.MaximumTop, upper.Bottom.y);
                    if (upper.MemberIds.Count != 0 && preparation.Column.Motions.TryGetValue(new GravityMotionId(upper.Id, true), out var motion))
                    {
                        preparation.PhysicalMaximumTop = Math.Min(preparation.PhysicalMaximumTop,
                            Mathf.FloorToInt(upper.Bottom.y - motion.Sample.Offset / BlockWorld.CellSize + .00001f));
                        preparation.UpperGuards.AddLast(new HistoricalUpperGuard { SourceBottom = upper.Bottom.y, Sample = motion.Sample });
                    }
                }
                if (preparation.Phase == 4 && preparation.UpperCursor == null) preparation.Phase = 5;
                while (preparation.Phase == 5 && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    int allowedTop = Math.Min(preparation.PhysicalMaximumTop,
                        preparation.Candidate <= preparation.SourceBottom ? preparation.Column.Minimum + preparation.Column.Length : preparation.MaximumTop);
                    if (preparation.Candidate + preparation.Span > allowedTop)
                    { FinishHistoricalRecovery(preparation.Saturated ? HistoricalRecoveryOutcome.UncertifiedStaticQuery : HistoricalRecoveryOutcome.NoCapacity, true); return true; }
                    if (preparation.CandidateScan == preparation.Span) { preparation.Phase = 6; break; }
                    int y = preparation.Candidate + preparation.CandidateScan, index = y - preparation.Column.Minimum;
                    int status = preparation.Column.AddedBelow[index];
                    if (!SpendHistoricalRecovery(ref checks, status < 0 ? 67 : 2, maximum, timer, milliseconds)) return true;
                    var at = new Vector3Int(preparation.Request.Column.x, y, preparation.Request.Column.y);
                    bool blocked = preparation.Source.TryGet(at, out _) || preparation.Column.Moving[index];
                    if (!blocked && status < 0)
                    {
                        bool staticOverlap = HistoricalStaticOverlap(BlockWorld.Center(at), out bool saturated);
                        preparation.Saturated |= saturated; status = staticOverlap ? 1 : 0; preparation.Column.AddedBelow[index] = status;
                    }
                    blocked |= status > 0;
                    if (blocked) { preparation.Candidate++; preparation.CandidateScan = 0; }
                    else preparation.CandidateScan++;
                }
                if (preparation.Phase == 6)
                {
                    if (!SpendHistoricalRecovery(ref checks, 67, maximum, timer, milliseconds)) return true;
                    var at = new Vector3Int(preparation.Request.Column.x, preparation.Candidate + preparation.FirstOccupied, preparation.Request.Column.y);
                    if (!QueryStaticProof(BlockWorld.Center(at), preparation.Source.Bottom, out int minimum))
                    { FinishHistoricalRecovery(HistoricalRecoveryOutcome.UncertifiedStaticQuery, true); return true; }
                    preparation.StaticProofs.AddLast(new GravityStaticProof(BlockWorld.Center(at), preparation.Source.Bottom, minimum));
                    preparation.Landing = minimum; preparation.LandingScan = at.y - 1; preparation.Phase = 7;
                }
                while (preparation.Phase == 7 && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendHistoricalRecovery(ref checks, 1, maximum, timer, milliseconds)) return true;
                    if (preparation.LandingScan < preparation.Landing) { preparation.Phase = 13; break; }
                    if (preparation.Source.TryGet(new Vector3Int(preparation.Request.Column.x, preparation.LandingScan, preparation.Request.Column.y), out _))
                    { preparation.Landing = preparation.LandingScan + 1; preparation.Phase = 13; break; }
                    preparation.LandingScan--;
                }
                // A repaired pose must retain its real moving lower bound before
                // activation; waiting for the next Tick permits it to fall through
                // that bound and makes the ordinary continuity guard reject it.
                if (preparation.Phase == 13 && !preparation.SupportScanStarted)
                {
                    if (!SpendHistoricalRecovery(ref checks, 16, maximum, timer, milliseconds)) return true;
                    preparation.SupportScanStarted = true;
                    if (preparation.Target.Group != null && _cohortHeads.TryGetValue(preparation.Request.Column, out var supportHeads))
                    {
                        int repairedBottom = Math.Max(preparation.SourceBottom, preparation.Candidate);
                        var minimum = new GravityMotionKey(int.MinValue, new GravityMotionId(0, false));
                        var targetKey = new GravityMotionKey(repairedBottom, preparation.Request.Motion);
                        preparation.SupportCursor = supportHeads.GetViewBetween(minimum, targetKey).Reverse().GetEnumerator();
                    }
                    else preparation.Phase = 8;
                }
                while (preparation.Phase == 13 && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!preparation.SupportBuffered)
                    {
                        if (!SpendHistoricalRecovery(ref checks, 1, maximum, timer, milliseconds)) return true;
                        if (!preparation.SupportCursor.MoveNext())
                        { preparation.SupportCursor.Dispose(); preparation.SupportCursor = null; preparation.Phase = 8; break; }
                        preparation.BufferedSupport = preparation.SupportCursor.Current; preparation.SupportBuffered = true;
                    }
                    var key = preparation.BufferedSupport;
                    preparation.Column.Motions.TryGetValue(key.Motion, out var motion);
                    int cost = 8 + (motion == null ? 0 : motion.Sample.Nodes.Count);
                    if (!SpendHistoricalRecovery(ref checks, cost, maximum, timer, milliseconds)) return true;
                    preparation.SupportBuffered = false;
                    if (key.Motion.Equals(preparation.Request.Motion)) continue;
                    if (key.Motion.Cohort)
                    {
                        if (!preparation.Next.Cohorts.TryGetValue(key.Motion.Id, out var lower)
                            || lower.Id == preparation.Target.Group.Id
                            || lower.Bottom.x != preparation.Request.Column.x || lower.Bottom.z != preparation.Request.Column.y
                            || lower.TopExclusive > Math.Max(preparation.SourceBottom, preparation.Candidate)) continue;
                        // This is the exact nearest source-admissible predecessor.
                        // Missing payload/sample or an unsafe physical pose blocks
                        // fallback, just as a nearer ordinary predecessor would.
                        preparation.SupportPredecessor = key;
                        if (motion != null && motion.Group != null && motion.Group.Id == lower.Id
                            && lower.MemberIds.Count != 0 && !lower.NeedsRegroup
                            && (lower.Phase == FallingCohortPhase.Falling || lower.Phase == FallingCohortPhase.Settling))
                        {
                            motion.Sample.Evaluate(preparation.SourceTime);
                            float physicalTop = lower.TopExclusive * BlockWorld.CellSize - motion.Sample.Offset;
                            if (physicalTop <= preparation.Candidate * BlockWorld.CellSize + .000001f)
                                preparation.SupportLower = motion;
                        }
                    }
                    else if (!preparation.Next.Falling.ContainsKey(key.Motion.Id)) continue;
                    // A live legacy predecessor remains a barrier. Never bypass
                    // it to bind an older cohort that ordinary selection replaces.
                    preparation.SupportCursor.Dispose(); preparation.SupportCursor = null; preparation.Phase = 8;
                }
                if (preparation.Phase == 8 && !preparation.OwnedForMutation)
                {
                    // All preceding phases only inspected Source/Next. Never
                    // perform the first pose write through that read-only alias.
                    if (preparation.Scratch.Clone == null)
                    {
                        // Keep the original initial admission; this additional
                        // fixed admission covers constructing the delayed owner.
                        if (!SpendHistoricalRecovery(ref checks, 16, maximum, timer, milliseconds)) return true;
                        preparation.Scratch.Clone = preparation.Source.BeginBudgetedOwnedClone();
                        HistoricalOwnedCloneStarts++;
                    }
                    while (!preparation.Scratch.Clone.Ready && HasStageBudget(checks, maximum, timer, milliseconds))
                    {
                        int cost = preparation.Scratch.Clone.EstimateNextStepWork();
                        if (!SpendHistoricalRecovery(ref checks, cost, maximum, timer, milliseconds)) return true;
                        ulong version = preparation.Scratch.Clone.ProgressVersion;
                        preparation.Scratch.Clone.Step(preparation.Source, cost, out int actual);
                        if (actual > cost || preparation.Scratch.Clone.Failed || preparation.Scratch.Clone.ProgressVersion == version)
                            throw new InvalidOperationException("Historical owned clone failed an admitted step.");
                    }
                    if (!preparation.Scratch.Clone.Ready || !SpendHistoricalRecovery(ref checks, 8, maximum, timer, milliseconds)) return true;
                    preparation.Scratch.Next = preparation.Scratch.Clone.TakeWorld(); preparation.Scratch.Clone = null;
                    preparation.Scratch.Journal.Next = preparation.Next; preparation.OwnedForMutation = true;
                }
                if (preparation.Phase == 8)
                {
                    int supportWork = preparation.SupportLower == null ? 0
                        : preparation.Next.EstimateSetCohortSupportWork(preparation.Target.Group.Id);
                    if (!SpendHistoricalRecovery(ref checks, 32 + supportWork, maximum, timer, milliseconds)) return true;
                    preparation.Delta = Math.Max(0, preparation.Candidate - preparation.SourceBottom);
                    preparation.InitialOffset = (preparation.SourceBottom + preparation.Delta - preparation.Candidate) * BlockWorld.CellSize;
                    var target = preparation.Target;
                    if (target.Group != null)
                    {
                        var group = preparation.Next.Cohorts[target.Group.Id];
                        group.Bottom += Vector3Int.up * preparation.Delta; group.TopExclusive += preparation.Delta; group.DetachCursor += preparation.Delta;
                        group.StartedAt = preparation.SourceTime; group.InitialOffset = preparation.InitialOffset; group.InitialSpeed = 0;
                        group.LandingKnown = true; group.LandingOffset = (group.Bottom.y + preparation.FirstOccupied - preparation.Landing) * BlockWorld.CellSize;
                        group.RetainedSupportMotion = Array.Empty<FrozenCohortMotion>(); group.RetainedSupportClearance = 0; group.NeedsLandingProof = false;
                        group.Phase = FallingCohortPhase.AwaitActivation; group.RebasePending = false; group.RebaseMotion = Array.Empty<FrozenCohortMotion>(); group.RebasePrefix = null;
                        group.MembershipRevision++; preparation.Next._pendingCohortIds.Add(group.Id);
                        if (group.LowerSupport != null) group.SupportClearance = (group.Bottom.y - group.LowerSupport.TopExclusive) * BlockWorld.CellSize;
                        if (preparation.SupportLower != null)
                        {
                            var lower = preparation.Next.Cohorts[preparation.SupportLower.Group.Id];
                            preparation.Next.SetCohortSupport(group.Id, lower.Id,
                                (group.Bottom.y - lower.TopExclusive) * BlockWorld.CellSize, out _);
                            group = preparation.Next.Cohorts[group.Id];
                        }
                        preparation.MemberCursor = group.MemberIds.GetEnumerator();
                        if (preparation.Next._upperSupports.TryGetValue(group.Id, out var uppers)) preparation.UpperCursor = uppers.GetEnumerator();
                    }
                    else
                    {
                        var run = preparation.Next.Falling[target.Legacy.Id]; run.Bottom += Vector3Int.up * preparation.Delta;
                        run.StartedAt = preparation.SourceTime; run.InitialOffset = preparation.InitialOffset; run.InitialSpeed = 0;
                        run.LandingKnown = true; run.LandingOffset = (run.Bottom.y + preparation.FirstOccupied - preparation.Landing) * BlockWorld.CellSize;
                        preparation.Next._newLegacyRunIds.Add(run.Id); preparation.Next._preparedLegacyRunIds.Add(run.Id);
                    }
                    preparation.Next.MutationGeneration++; preparation.Phase = 9;
                }
                while (preparation.Phase == 9 && preparation.MemberCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendHistoricalRecovery(ref checks, 2, maximum, timer, milliseconds)) return true;
                    if (!preparation.MemberCursor.MoveNext()) { preparation.MemberCursor.Dispose(); preparation.MemberCursor = null; break; }
                    preparation.Next.Falling[preparation.MemberCursor.Current].Bottom += Vector3Int.up * preparation.Delta;
                }
                if (preparation.Phase == 9 && preparation.MemberCursor == null) preparation.Phase = 10;
                while (preparation.Phase == 10 && preparation.UpperCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendHistoricalRecovery(ref checks, 18, maximum, timer, milliseconds)) return true;
                    if (!preparation.UpperCursor.MoveNext()) { preparation.UpperCursor.Dispose(); preparation.UpperCursor = null; break; }
                    var upper = preparation.Next.Cohorts[preparation.UpperCursor.Current]; upper.SupportClearance = (upper.Bottom.y - preparation.Next.Cohorts[preparation.Target.Group.Id].TopExclusive) * BlockWorld.CellSize;
                    var id = new GravityMotionId(upper.Id, true);
                    preparation.Scratch.Journal.Motions[id] = new GravityMotionWork { Column = preparation.Request.Column, SourceBottom = upper.Bottom.y, SourceTop = upper.TopExclusive };
                }
                if (preparation.Phase == 10 && preparation.UpperCursor == null)
                {
                    preparation.HeadCursor = preparation.Delta == 0 ? null : _cohortHeads[preparation.Request.Column].GetEnumerator();
                    if (preparation.Delta != 0) preparation.PreparedHeads = new SortedSet<GravityMotionKey>();
                    preparation.Phase = 11;
                }
                while (preparation.Phase == 11 && preparation.HeadCursor != null && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendHistoricalRecovery(ref checks, 128, maximum, timer, milliseconds)) return true;
                    if (!preparation.HeadCursor.MoveNext()) { preparation.HeadCursor.Dispose(); preparation.HeadCursor = null; break; }
                    var key = preparation.HeadCursor.Current;
                    preparation.PreparedHeads.Add(key.Motion.Equals(preparation.Request.Motion) ? new GravityMotionKey(key.Bottom + preparation.Delta, key.Motion) : key);
                }
                if (preparation.Phase != 11 || preparation.HeadCursor != null) return true;
                if (!SpendHistoricalRecovery(ref checks, 48, maximum, timer, milliseconds)) return true;
                if (preparation.Target.Group != null) preparation.Next.PrivatePrepareCohort(preparation.Target.Group.Id, out _);
                preparation.Scratch.Journal.Motions[preparation.Request.Motion] = new GravityMotionWork { Column = preparation.Request.Column,
                    SourceBottom = preparation.SourceBottom + preparation.Delta + (preparation.Target.Group == null ? preparation.FirstOccupied : 0),
                    SourceTop = preparation.SourceBottom + preparation.Delta + preparation.Span };
                if (preparation.PreparedHeads != null) preparation.Scratch.Journal.Heads[preparation.Request.Column] = preparation.PreparedHeads;
                preparation.Next.Seal(Revision + 1, Array.Empty<Vector3Int>());
                _gravityJournal = preparation.Scratch.Journal;
                CompleteBuildOverlapRepairPreparation(preparation.Request, _gravityJournal, true);
                BeginWindowTransaction(preparation.Next, new HashSet<Vector3Int>(), false, null, preparation.Request.Window);
                _pending.RecoveryGuard = preparation; _pending.StaticProofs = preparation.StaticProofs;
                _historicalRecoveryPreparation = null; _gravityJournal = null; return true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[BLOCK] Historical geometry recovery rejected: " + exception.Message);
                FailedTransactions++; FinishHistoricalRecovery(HistoricalRecoveryOutcome.Cancelled, false); return true;
            }
            finally
            {
                _gravityJournal = null; LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (GravityWorkChecks != before) GravityProgressStages++;
            }
        }
        bool ValidateHistoricalRecoveryPublication(Pending pending)
        {
            var preparation = pending.RecoveryGuard; LastHistoricalPublicationWork = 0; if (preparation == null) return true;
            preparation.WitnessGone = preparation.SourceLost = false;
            if (!preparation.SourceValid(this)) { preparation.SourceLost = true; return false; }
            // This branch is entered only while an existing published volume is
            // demonstrably invalid. It never repairs a merely late new floor.
            bool saturated = false;
            foreach (var motion in preparation.Column.Motions.Values)
            { motion.Sample.Evaluate(_now); LastHistoricalPublicationWork += motion.Sample.Nodes.Count; }
            if (preparation.SupportLower != null)
            {
                LastHistoricalPublicationWork += 12;
                var selected = preparation.SupportLower;
                var sourceLower = selected.Group;
                if (!preparation.Source.Cohorts.TryGetValue(sourceLower.Id, out var actualSourceLower)
                    || !ReferenceEquals(actualSourceLower, sourceLower)
                    || preparation.SupportPredecessor.CompareTo(new GravityMotionKey(sourceLower.Bottom.y, selected.Id)) != 0
                    || !pending.Next.Cohorts.TryGetValue(preparation.Target.Group.Id, out var target)
                    || !pending.Next.Cohorts.TryGetValue(sourceLower.Id, out var lower)
                    || target.LowerSupport?.Id != lower.Id || lower.Id == target.Id
                    || lower.Bottom.x != target.Bottom.x || lower.Bottom.z != target.Bottom.z || lower.TopExclusive > target.Bottom.y)
                    return false;
                float physicalTop = sourceLower.TopExclusive * BlockWorld.CellSize - selected.Sample.Offset;
                float cap = selected.Sample.Offset + target.SupportClearance;
                if (physicalTop > preparation.Candidate * BlockWorld.CellSize + .000001f
                    || target.InitialOffset > cap + .000001f) return false;
            }
            LastHistoricalPublicationWork += preparation.WitnessOther != null ? 4 : 67;
            bool stillInvalid = preparation.WitnessOther != null
                ? HistoricalDynamicCellsOverlap(preparation.Witness, preparation.WitnessOther, _now)
                : HistoricalSourceCellInvalid(preparation.Witness, _now, true, out saturated) && !saturated;
            if (!stillInvalid) { preparation.WitnessGone = true; return false; }
            foreach (var upper in preparation.UpperGuards)
            {
                LastHistoricalPublicationWork += 2;
                if ((preparation.Candidate + preparation.Span) * BlockWorld.CellSize
                    > upper.SourceBottom * BlockWorld.CellSize - upper.Sample.Offset + .000001f) return false;
            }
            foreach (var cell in preparation.Column.Cells)
            {
                if (ReferenceEquals(cell.Sample, preparation.Target.Sample)) continue;
                LastHistoricalPublicationWork += 4;
                float bottom = cell.SourceCell.y * BlockWorld.CellSize - cell.Sample.Offset;
                if (bottom < (preparation.Candidate + preparation.Span) * BlockWorld.CellSize - .000001f
                    && bottom + BlockWorld.CellSize > preparation.Candidate * BlockWorld.CellSize + .000001f) return false;
            }
            for (int index = 0; index < preparation.Span; index++)
            {
                var at = new Vector3Int(preparation.Request.Column.x, preparation.Candidate + index, preparation.Request.Column.y);
                LastHistoricalPublicationWork += 67;
                if (World.TryGet(at, out _) || HistoricalStaticOverlap(BlockWorld.Center(at), out saturated)) return false;
            }
            return true;
        }
        void RetryHistoricalRecoveryPublication(Pending pending)
        {
            pending.AbortReason = pending.RecoveryGuard.SourceLost
                ? PendingAbortReason.HistoricalRecoverySourceChanged
                : pending.RecoveryGuard.WitnessGone
                    ? PendingAbortReason.HistoricalRecoveryWitnessGone
                    : PendingAbortReason.HistoricalRecoveryConflict;
            RecordPendingTerminal(pending, PendingWallOutcome.Aborted);
            _retired.Enqueue(new Retired { Pending = pending }); _pending = null;
            if (pending.RecoveryGuard.SourceLost)
            { LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.Cancelled; return; }
            if (pending.RecoveryGuard.WitnessGone)
            { HistoricalNoLongerInvalidWitnessGone++; LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.NoLongerInvalid; return; }
            var request = pending.RecoveryGuard.Request;
            if (++request.PublicationAttempts >= HistoricalPublicationRetryLimit)
            {
                SetHistoricalTerminal(request, HistoricalRecoveryOutcome.PublicationConflict);
                HistoricalRecoveryPublicationConflicts++; LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.PublicationConflict; return;
            }
            _historicalRecoveryRequest = request;
            LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.Preparing;
        }
        void HistoricalRecoveryPublished(Pending pending)
        {
            if (pending.RecoveryGuard == null) return;
            HistoricalRecoveryCount++; LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.Recovered;
        }
    }
}
