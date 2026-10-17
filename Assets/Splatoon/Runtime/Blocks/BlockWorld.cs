using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed class BlockChunk
    {
        public readonly BlockCell[] Cells = new BlockCell[BlockWorld.CellsPerChunk];
        public uint Revision;
        public int Count;
        // Derived occupancy; not part of the snapshot/delta wire representation.
        readonly ushort[] _xRows = new ushort[256], _yRows = new ushort[256], _zRows = new ushort[256];
        readonly ushort[] _anchoredYRows = new ushort[256];
        internal ushort UnanchoredVerticalRow(Vector3Int local)
            => (ushort)(_yRows[local.x + 16 * local.z] & ~_anchoredYRows[local.x + 16 * local.z]);
        internal ushort OccupiedRow(int axis, Vector3Int local)
            => axis == 0 ? _xRows[local.y + 16 * local.z]
                : axis == 1 ? _yRows[local.x + 16 * local.z] : _zRows[local.x + 16 * local.y];
        static ushort SetRowBit(ushort row, int bit, bool occupied)
            => occupied ? (ushort)(row | (1 << bit)) : (ushort)(row & ~(1 << bit));
        internal void SetOccupied(int index, bool occupied)
        {
            int x = index & 15, y = index >> 4 & 15, z = index >> 8;
            _xRows[y + 16 * z] = SetRowBit(_xRows[y + 16 * z], x, occupied);
            _yRows[x + 16 * z] = SetRowBit(_yRows[x + 16 * z], y, occupied);
            _zRows[x + 16 * y] = SetRowBit(_zRows[x + 16 * y], z, occupied);
            _anchoredYRows[x + 16 * z] = SetRowBit(_anchoredYRows[x + 16 * z], y, occupied && Cells[index].Anchored);
        }
        // Caller validates occupied source cells before taking writable ownership.
        internal void ClearOccupiedVerticalSpan(int firstIndex, int count)
        {
            int x = firstIndex & 15, y = firstIndex >> 4 & 15, z = firstIndex >> 8;
            uint mask = ((1u << count) - 1u) << y;
            int column = x + 16 * z;
            _yRows[column] = (ushort)(_yRows[column] & ~mask);
            _anchoredYRows[column] = (ushort)(_anchoredYRows[column] & ~mask);
            uint xMask = ~(1u << x), zMask = ~(1u << z);
            for (int i = 0; i < count; i++)
            {
                int rowY = y + i;
                Cells[firstIndex + 16 * i] = default;
                int xRow = rowY + 16 * z, zRow = x + 16 * rowY;
                _xRows[xRow] = (ushort)(_xRows[xRow] & xMask);
                _zRows[zRow] = (ushort)(_zRows[zRow] & zMask);
            }
            Count -= count;
        }
        public BlockChunk Clone()
        {
            var copy = new BlockChunk { Revision = Revision, Count = Count };
            Array.Copy(Cells, copy.Cells, Cells.Length);
            Array.Copy(_xRows, copy._xRows, _xRows.Length);
            Array.Copy(_yRows, copy._yRows, _yRows.Length);
            Array.Copy(_zRows, copy._zRows, _zRows.Length);
            Array.Copy(_anchoredYRows, copy._anchoredYRows, _anchoredYRows.Length);
            return copy;
        }
    }

    internal readonly struct FrozenCohortMotion
    {
        internal readonly FallingCohortPhase Phase;
        internal readonly double StartedAt;
        internal readonly float InitialOffset, InitialSpeed, LandingOffset, SupportClearance;
        internal readonly bool LandingKnown;
        internal FrozenCohortMotion(FallingCohortPhase phase, double startedAt, float initialOffset, float initialSpeed,
            bool landingKnown, float landingOffset, float supportClearance)
        { Phase = phase; StartedAt = startedAt; InitialOffset = initialOffset; InitialSpeed = initialSpeed;
            LandingKnown = landingKnown; LandingOffset = landingOffset; SupportClearance = supportClearance; }
        internal static int Depth(FallingCohort source)
        {
            int depth = 0;
            for (var node = source; node != null; node = node.LowerSupport)
            {
                depth++;
                if (node.LowerSupport == null) depth += node.RetainedSupportMotion.Length;
                if (depth > BlockWorld.MaxCohortCells) throw new InvalidOperationException("Invalid support trajectory depth.");
            }
            return depth;
        }
        internal static FrozenCohortMotion[] Capture(FallingCohort source)
        {
            var chain = new FrozenCohortMotion[Depth(source)]; int index = chain.Length;
            for (var node = source; node != null; node = node.LowerSupport)
            {
                float clearance = node.LowerSupport != null ? node.SupportClearance : node.RetainedSupportClearance;
                chain[--index] = new FrozenCohortMotion(node.Phase, node.StartedAt, node.InitialOffset, node.InitialSpeed, node.LandingKnown, node.LandingOffset, clearance);
                if (node.LowerSupport == null && node.RetainedSupportMotion.Length != 0)
                { Array.Copy(node.RetainedSupportMotion, 0, chain, 0, node.RetainedSupportMotion.Length); index = 0; }
            }
            return chain;
        }
        internal static void Evaluate(FrozenCohortMotion[] chain, double now, out float offset, out float speed)
        {
            offset = speed = 0;
            for (int i = 0; i < chain.Length; i++)
                EvaluateNode(chain[i], now, i != 0, offset, speed, out offset, out speed);
        }
        internal static void EvaluateNode(FrozenCohortMotion node, double now, bool hasLower, float lowerOffset, float lowerSpeed, out float offset, out float speed)
        {
            bool moving = node.Phase == FallingCohortPhase.Falling || node.Phase == FallingCohortPhase.Settling;
            offset = moving ? FallingCohort.MotionOffset(now, node.StartedAt, node.InitialOffset, node.InitialSpeed, node.LandingKnown, node.LandingOffset) : node.InitialOffset;
            speed = node.Phase == FallingCohortPhase.Falling ? FallingCohort.MotionSpeed(now, node.StartedAt, node.InitialOffset, node.InitialSpeed, node.LandingKnown, node.LandingOffset) : 0;
            if (hasLower && moving && offset >= lowerOffset + node.SupportClearance)
            { offset = Mathf.Max(node.InitialOffset, lowerOffset + node.SupportClearance); speed = lowerSpeed; }
        }
    }

    public enum FallingCohortPhase : byte { Detaching, AwaitActivation, Falling, Settling }

    // Transaction-local memo: children sharing an immutable source chain sample it once at publication.
    // This cache is derived state and is deliberately absent from the wire representation.
    internal sealed class PreparedRebaseSample
    {
        internal readonly FrozenCohortMotion[] Motion;
        internal readonly PreparedRebaseSample Prefix;
        internal int LocalWork => Prefix == null ? Math.Max(1, Motion.Length) : 1;
        internal int PreparedUsers;
        internal bool Evaluated;
        internal double EvaluatedAt;
        internal float Offset, Speed;
        internal PreparedRebaseSample(FrozenCohortMotion[] motion, PreparedRebaseSample prefix = null) { Motion = motion; Prefix = prefix; }
        internal int EvaluateOnce(double publishNow, out int samples)
        {
            samples = 0; if (Evaluated && EvaluatedAt == publishNow) return 0;
            int work = LocalWork;
            if (Prefix == null) FrozenCohortMotion.Evaluate(Motion, publishNow, out Offset, out Speed);
            else
            {
                work += Prefix.EvaluateOnce(publishNow, out samples);
                FrozenCohortMotion.EvaluateNode(Motion[Motion.Length - 1], publishNow, true, Prefix.Offset, Prefix.Speed, out Offset, out Speed);
            }
            Evaluated = true; EvaluatedAt = publishNow; samples++; return work;
        }
    }

    public readonly struct FallingPrivateLandingProofRequest
    {
        // Zero denotes the first new child; otherwise this identifies an existing owner or direct upper.
        public readonly ulong TargetCohortId;
        public readonly Vector3Int SourceBottom;
        public readonly int SourceTopExclusive;
        public readonly int SourceDetachCursor;
        public readonly float SourceOffsetAtSourceTime;
        public readonly ulong LiveLowerId;
        internal FallingPrivateLandingProofRequest(Vector3Int bottom, int topExclusive, int detachCursor, float sourceOffset, ulong liveLowerId, ulong targetCohortId = 0)
        { SourceBottom = bottom; SourceTopExclusive = topExclusive; SourceDetachCursor = detachCursor; SourceOffsetAtSourceTime = sourceOffset; LiveLowerId = liveLowerId; TargetCohortId = targetCohortId; }
    }

    /// <summary>One connected column owns one trajectory; <=16-cell runs are storage only.</summary>
    public sealed class FallingCohort
    {
        public ulong Id;
        public ulong MembershipRevision;
        public Vector3Int Bottom;
        public int TopExclusive, DetachCursor;
        public FallingCohortPhase Phase;
        public double StartedAt;
        public float InitialOffset, InitialSpeed, LandingOffset;
        public bool LandingKnown, NeedsRegroup;
        public FallingCohort LowerSupport;
        public float SupportClearance;
        public bool NeedsLandingProof;
        internal FrozenCohortMotion[] RetainedSupportMotion = Array.Empty<FrozenCohortMotion>();
        internal float RetainedSupportClearance;
        // A fracture samples this immutable source trajectory at actual publication, not staging.
        internal FrozenCohortMotion[] RebaseMotion = Array.Empty<FrozenCohortMotion>();
        internal PreparedRebaseSample PreparedRebase;
        internal FrozenCohortMotion[] RebasePrefix;
        internal bool VerifiedPrivateLanding;
        internal float VerifiedPrivateLandingOffset;
        public bool RebasePending, RebaseSourceMoving, RebaseSourceLandingKnown;
        public double RebaseSourceStartedAt;
        public float RebaseSourceOffset, RebaseSourceSpeed, RebaseSourceLandingOffset;
        internal readonly List<ulong> Members = new();
        public IReadOnlyList<ulong> MemberIds => Members;
        public float Offset(double now) { Evaluate(now, out float offset, out _); return offset; }
        public float Speed(double now) { Evaluate(now, out _, out float speed); return speed; }
        void Evaluate(double now, out float offset, out float speed)
        {
            bool moving = Phase == FallingCohortPhase.Falling || Phase == FallingCohortPhase.Settling;
            offset = moving ? MotionOffset(now, StartedAt, InitialOffset, InitialSpeed, LandingKnown, LandingOffset) : InitialOffset;
            speed = Phase == FallingCohortPhase.Falling ? MotionSpeed(now, StartedAt, InitialOffset, InitialSpeed, LandingKnown, LandingOffset) : 0;
            if (!moving || LowerSupport == null && RetainedSupportMotion.Length == 0) return;
            float lowerOffset, lowerSpeed, clearance;
            if (LowerSupport != null) { LowerSupport.Evaluate(now, out lowerOffset, out lowerSpeed); clearance = SupportClearance; }
            else { FrozenCohortMotion.Evaluate(RetainedSupportMotion, now, out lowerOffset, out lowerSpeed); clearance = RetainedSupportClearance; }
            float maximum = lowerOffset + clearance;
            if (offset >= maximum) { offset = Mathf.Max(InitialOffset, maximum); speed = lowerSpeed; }
        }
        internal static float MotionOffset(double now, double started, float offsetBase, float speed, bool known, float landing)
        {
            float elapsed = (float)Math.Max(0, now - started);
            float offset = offsetBase + speed * elapsed + .5f * BlockWorld.Gravity * elapsed * elapsed;
            return known ? Mathf.Max(offsetBase, Mathf.Min(offset, landing)) : offset;
        }
        internal static float MotionSpeed(double now, double started, float offsetBase, float speed, bool known, float landing)
        {
            float elapsed = (float)Math.Max(0, now - started);
            float raw = offsetBase + speed * elapsed + .5f * BlockWorld.Gravity * elapsed * elapsed;
            return known && raw >= landing ? 0 : speed + BlockWorld.Gravity * elapsed;
        }
        public FallingCohort Clone()
        {
            var copy = CloneDescriptor(); copy.Members.AddRange(Members); return copy;
        }
        internal FallingCohort CloneDescriptor()
        {
            return new FallingCohort { Id = Id, MembershipRevision = MembershipRevision, Bottom = Bottom, TopExclusive = TopExclusive, DetachCursor = DetachCursor,
                Phase = Phase, StartedAt = StartedAt, InitialOffset = InitialOffset, InitialSpeed = InitialSpeed,
                LandingOffset = LandingOffset, LandingKnown = LandingKnown, NeedsRegroup = NeedsRegroup, LowerSupport = LowerSupport, SupportClearance = SupportClearance,
                NeedsLandingProof = NeedsLandingProof, RetainedSupportMotion = RetainedSupportMotion, RetainedSupportClearance = RetainedSupportClearance,
                RebaseMotion = RebaseMotion, RebasePrefix = RebasePrefix, PreparedRebase = PreparedRebase,
                VerifiedPrivateLanding = VerifiedPrivateLanding, VerifiedPrivateLandingOffset = VerifiedPrivateLandingOffset,
                RebasePending = RebasePending, RebaseSourceMoving = RebaseSourceMoving,
                RebaseSourceLandingKnown = RebaseSourceLandingKnown, RebaseSourceStartedAt = RebaseSourceStartedAt,
                RebaseSourceOffset = RebaseSourceOffset, RebaseSourceSpeed = RebaseSourceSpeed,
                RebaseSourceLandingOffset = RebaseSourceLandingOffset };
        }
    }

    public readonly struct FallingCellAddress : IEquatable<FallingCellAddress>
    {
        public readonly ulong Run;
        public readonly int Index;
        public FallingCellAddress(ulong run, int index) { Run = run; Index = index; }
        public bool Equals(FallingCellAddress other) => Run == other.Run && Index == other.Index;
        public override bool Equals(object obj) => obj is FallingCellAddress other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Run, Index);
    }

    public readonly struct FallingCohortPiece
    {
        public readonly ulong RunId;
        public readonly int FirstIndex, Count;
        public FallingCohortPiece(ulong runId, int firstIndex, int count) { RunId = runId; FirstIndex = firstIndex; Count = count; }
    }
    public sealed class FallingCohortSpan
    {
        public readonly Vector3Int Bottom;
        public readonly int TopExclusive, DetachCursor;
        public readonly IReadOnlyList<FallingCohortPiece> Pieces;
        public FallingCohortSpan(Vector3Int bottom, int topExclusive, IReadOnlyList<FallingCohortPiece> pieces)
            : this(bottom, topExclusive, topExclusive, pieces) { }
        public FallingCohortSpan(Vector3Int bottom, int topExclusive, int detachCursor, IReadOnlyList<FallingCohortPiece> pieces)
        { Bottom = bottom; TopExclusive = topExclusive; DetachCursor = detachCursor; Pieces = pieces ?? throw new ArgumentNullException(nameof(pieces)); }
    }

    public sealed class FallingRun
    {
        public ulong Id;
        public Vector3Int Bottom;
        public BlockCell[] Cells;
        public FallingCohort Cohort;
        double _startedAt;
        float _initialOffset, _initialSpeed, _landingOffset;
        bool _landingKnown;
        // Grouped observations always project the descriptor; legacy setters/Clone retain their original semantics.
        public double StartedAt { get => Cohort?.StartedAt ?? _startedAt; set { if (Cohort != null) Cohort.StartedAt = value; else _startedAt = value; } }
        public float InitialOffset { get => Cohort?.InitialOffset ?? _initialOffset; set { if (Cohort != null) Cohort.InitialOffset = value; else _initialOffset = value; } }
        public float InitialSpeed { get => Cohort?.InitialSpeed ?? _initialSpeed; set { if (Cohort != null) Cohort.InitialSpeed = value; else _initialSpeed = value; } }
        public float LandingOffset { get => Cohort?.LandingOffset ?? _landingOffset; set { if (Cohort != null) Cohort.LandingOffset = value; else _landingOffset = value; } }
        public bool LandingKnown { get => Cohort?.LandingKnown ?? _landingKnown; set { if (Cohort != null) Cohort.LandingKnown = value; else _landingKnown = value; } }
        public float Offset(double now) => Cohort != null ? Cohort.Offset(now)
            : FallingCohort.MotionOffset(now, StartedAt, InitialOffset, InitialSpeed, LandingKnown, LandingOffset);
        public float Speed(double now) => Cohort != null ? Cohort.Speed(now)
            : FallingCohort.MotionSpeed(now, StartedAt, InitialOffset, InitialSpeed, LandingKnown, LandingOffset);
        public FallingRun Clone() => new() { Id = Id, Bottom = Bottom, Cells = Cells, Cohort = Cohort,
            _startedAt = _startedAt, _initialOffset = _initialOffset, _initialSpeed = _initialSpeed,
            _landingOffset = _landingOffset, _landingKnown = _landingKnown };
    }

    /// <summary>Resumable, transaction-owned fracture preparation. Finished piece arrays are immutable and shared by Clone.</summary>
    public sealed class FallingCohortReplacement
    {
        internal sealed class PreparedPiece
        {
            internal readonly ulong SourceId;
            internal readonly int FirstIndex, Count;
            internal readonly Vector3Int Bottom;
            internal readonly BlockCell[] Cells;
            internal PreparedPiece(ulong source, int first, int count, Vector3Int bottom, BlockCell[] cells)
            { SourceId = source; FirstIndex = first; Count = count; Bottom = bottom; Cells = cells; }
        }
        internal sealed class PreparedSpan
        {
            internal readonly FallingCohortSpan Span;
            internal readonly List<PreparedPiece> Pieces = new();
            internal PreparedSpan(FallingCohortSpan span) { Span = span; }
        }
        internal readonly ulong ParentId, MembershipRevision;
        internal readonly Vector3Int SourceBottom;
        internal readonly int SourceTop, SourceCursor;
        internal readonly ulong[] SourceIds;
        readonly IReadOnlyList<FallingCohortSpan> _spans;
        readonly IReadOnlyDictionary<ulong, FallingRun> _sourceRuns;
        internal readonly List<PreparedSpan> Prepared = new();
        HashSet<FallingCellAddress> _covered = new();
        int _phase, _memberIndex, _cellIndex, _remaining, _spanIndex, _pieceIndex, _copyIndex, _spanY, _lastTop;
        bool _spanOpen, _pieceOpen, _committed;
        BlockCell[] _copyCells;
        internal int PreparedPieceCount, AliasCellsUpperBound;
        public bool Ready => _phase == 2 && !_committed;
        public bool Failed { get; private set; }
        internal FallingCohortReplacement(FallingCohort source, ulong[] ids, IReadOnlyDictionary<ulong, FallingRun> runs, IReadOnlyList<FallingCohortSpan> spans)
        {
            ParentId = source.Id; MembershipRevision = source.MembershipRevision; SourceBottom = source.Bottom;
            SourceTop = source.TopExclusive; SourceCursor = source.DetachCursor; SourceIds = ids; _sourceRuns = runs; _spans = spans; _lastTop = source.Bottom.y;
        }
        public bool IsValid(BlockWorld world)
            => !Failed && !_committed && world.Cohorts.TryGetValue(ParentId, out var cohort)
                && cohort.MembershipRevision == MembershipRevision && cohort.Bottom == SourceBottom
                && cohort.TopExclusive == SourceTop && cohort.DetachCursor == SourceCursor;
        public int EstimateCloneWork()
        {
            int work = 1 + _covered.Count + Prepared.Count + (_copyCells?.Length ?? 0);
            foreach (var span in Prepared) work += span.Pieces.Count; return work;
        }
        public FallingCohortReplacement Clone(out int work)
        {
            work = EstimateCloneWork();
            var source = new FallingCohort { Id = ParentId, MembershipRevision = MembershipRevision, Bottom = SourceBottom, TopExclusive = SourceTop, DetachCursor = SourceCursor };
            var copy = new FallingCohortReplacement(source, SourceIds, _sourceRuns, _spans) { _phase = _phase,
                _memberIndex = _memberIndex, _cellIndex = _cellIndex, _remaining = _remaining, _spanIndex = _spanIndex,
                _pieceIndex = _pieceIndex, _copyIndex = _copyIndex, _spanY = _spanY, _lastTop = _lastTop,
                _spanOpen = _spanOpen, _pieceOpen = _pieceOpen, _committed = _committed, Failed = Failed,
                PreparedPieceCount = PreparedPieceCount, AliasCellsUpperBound = AliasCellsUpperBound,
                _covered = new HashSet<FallingCellAddress>(_covered), _copyCells = _copyCells == null ? null : (BlockCell[])_copyCells.Clone() };
            foreach (var span in Prepared) { var child = new PreparedSpan(span.Span); child.Pieces.AddRange(span.Pieces); copy.Prepared.Add(child); }
            return copy;
        }
        public bool Step(BlockWorld world, int maxWork, out int work)
        {
            if (maxWork < 0) throw new ArgumentOutOfRangeException(nameof(maxWork));
            work = 0; if (!IsValid(world)) { Failed = true; return false; } if (Ready) return true;
            while (work < maxWork)
            {
                if (_phase == 0)
                {
                    if (_memberIndex == SourceIds.Length) { _phase = 1; work++; continue; }
                    var run = _sourceRuns[SourceIds[_memberIndex]];
                    if (_cellIndex == run.Cells.Length) { _memberIndex++; _cellIndex = 0; work++; continue; }
                    if (run.Cells[_cellIndex++].Occupied) _remaining++; work++; continue;
                }
                if (_spanIndex == _spans.Count)
                {
                    if (_covered.Count != _remaining) { Failed = true; return false; }
                    _phase = 2; work++; return true;
                }
                var span = _spans[_spanIndex];
                if (!_spanOpen)
                {
                    if (span.Bottom.x != SourceBottom.x || span.Bottom.z != SourceBottom.z || span.Bottom.y < _lastTop
                        || _spanIndex != 0 && span.Bottom.y == _lastTop || span.Bottom.y < SourceBottom.y || span.TopExclusive > SourceTop
                        || span.TopExclusive <= span.Bottom.y || span.DetachCursor != Math.Max(span.Bottom.y, Math.Min(SourceCursor, span.TopExclusive)))
                    { Failed = true; return false; }
                    _spanY = span.Bottom.y; _pieceIndex = 0; _spanOpen = true; Prepared.Add(new PreparedSpan(span)); work++; continue;
                }
                if (_pieceIndex == span.Pieces.Count)
                {
                    if (_spanY != span.DetachCursor) { Failed = true; return false; }
                    _lastTop = span.TopExclusive; _spanIndex++; _spanOpen = false; work++; continue;
                }
                var piece = span.Pieces[_pieceIndex];
                if (!_pieceOpen)
                {
                    if (!_sourceRuns.TryGetValue(piece.RunId, out var source) || piece.Count <= 0 || piece.FirstIndex < 0
                        || piece.FirstIndex > source.Cells.Length - piece.Count || source.Cells.Length > BlockWorld.MaxCohortPieceCells
                        || source.Bottom.y + piece.FirstIndex != _spanY)
                    { Failed = true; return false; }
                    int cost = 1 + source.Cells.Length;
                    if (maxWork - work < cost) return false;
                    _copyCells = new BlockCell[source.Cells.Length]; _copyIndex = piece.FirstIndex; _pieceOpen = true; work += cost; continue;
                }
                var runSource = _sourceRuns[piece.RunId];
                if (_copyIndex == piece.FirstIndex + piece.Count)
                {
                    Prepared[_spanIndex].Pieces.Add(new PreparedPiece(piece.RunId, piece.FirstIndex, piece.Count, runSource.Bottom, _copyCells));
                    PreparedPieceCount++; AliasCellsUpperBound += piece.Count; _copyCells = null; _pieceOpen = false;
                    _spanY += piece.Count; _pieceIndex++; work++; continue;
                }
                if (maxWork - work < 2) return false;
                var address = new FallingCellAddress(piece.RunId, _copyIndex); var cell = runSource.Cells[_copyIndex];
                if (!cell.Occupied || !_covered.Add(address)) { Failed = true; return false; }
                _copyCells[_copyIndex++] = cell; work += 2;
            }
            return Ready;
        }
        internal void MarkCommitted() { _committed = true; }
        public int EstimateNextStepWork()
        {
            if (Ready || Failed) return 1;
            if (_phase == 1 && _spanOpen && _pieceIndex < _spans[_spanIndex].Pieces.Count)
            {
                var piece = _spans[_spanIndex].Pieces[_pieceIndex];
                if (!_pieceOpen && _sourceRuns.TryGetValue(piece.RunId, out var run)) return 1 + run.Cells.Length;
                if (_pieceOpen && _copyIndex < piece.FirstIndex + piece.Count) return 2;
            }
            return 1;
        }
    }

    /// <summary>Incremental metadata clone. The source must remain the exact published World under a private transaction lock.</summary>
    public sealed class BlockWorldCloneBuilder : IDisposable
    {
        public BlockWorld SourceWorld { get; }
        public uint SourceRevision { get; }
        public uint SourceRound { get; }
        public ulong SourceMutationGeneration { get; }
        public bool Ready { get; private set; }
        public bool Failed { get; private set; }
        public long ProcessedWork { get; private set; }
        public ulong ProgressVersion { get; private set; }
        BlockWorld _copy;
        int _phase, _memberIndex, _aliasDepth;
        bool _aliasOpen;
        KeyValuePair<FallingCellAddress, FallingCellAddress> _alias;
        FallingCellAddress _aliasEndpoint;
        FallingCohort _sourceCohort, _copyCohort;
        readonly IEnumerator<KeyValuePair<Vector3Int, BlockChunk>> _chunks;
        readonly IEnumerator<KeyValuePair<Vector3Int, uint>> _tombstones;
        readonly IEnumerator<KeyValuePair<ulong, FallingCohort>> _cohorts, _links;
        readonly IEnumerator<KeyValuePair<ulong, FallingRun>> _runs;
        readonly IEnumerator<KeyValuePair<FallingCellAddress, FallingCellAddress>> _aliases;
        readonly IEnumerator<ulong> _pending, _legacy, _prepared, _preparedLegacy;
        bool _preparedBuffered;
        readonly bool _ownMetadata;
        internal BlockWorldCloneBuilder(BlockWorld source, bool ownMetadata = false)
        {
            if (source.DependentEditIncomplete) throw new InvalidOperationException("An incomplete dependent draft cannot be cloned.");
            _ownMetadata = ownMetadata;
            SourceWorld = source; SourceRevision = source.Revision; SourceRound = source.Round; SourceMutationGeneration = source.MutationGeneration;
            _copy = source.CreateCloneShell(); _chunks = source._chunks.GetEnumerator(); _tombstones = source._tombstones.GetEnumerator();
            _cohorts = source.Cohorts.GetEnumerator(); _links = source.Cohorts.GetEnumerator(); _runs = source.Falling.GetEnumerator();
            _aliases = source._targetAliases.GetEnumerator(); _pending = source._pendingCohortIds.GetEnumerator(); _legacy = source._newLegacyRunIds.GetEnumerator();
            _prepared = source._preparedCohortIds.GetEnumerator(); _preparedLegacy = source._preparedLegacyRunIds.GetEnumerator();
        }
        public bool IsValid(BlockWorld current)
            => !Failed && ReferenceEquals(current, SourceWorld) && current.Round == SourceRound && current.Revision == SourceRevision
                && current.MutationGeneration == SourceMutationGeneration;
        public int EstimateNextStepWork() => _ownMetadata && _phase == 8 && _preparedBuffered
            ? Math.Max(6, _copy.EstimatePrivatePrepareCohortWork(_prepared.Current)) : _ownMetadata ? 6 : 5;
        public long MaximumWork => 64L + 3L * SourceWorld._chunks.Count + 2L * SourceWorld._tombstones.Count
            + (MaxMembersPerDescriptor + 9L) * SourceWorld.Cohorts.Count + (_ownMetadata ? 6L : 5L) * SourceWorld.Falling.Count
            + 24L * SourceWorld._targetAliases.Count + 2L * (SourceWorld._pendingCohortIds.Count + SourceWorld._newLegacyRunIds.Count)
            + (_ownMetadata ? (17L + BlockWorld.MaxCohortCells) * SourceWorld._preparedCohortIds.Count + 2L * SourceWorld._preparedLegacyRunIds.Count : 0);
        const int MaxMembersPerDescriptor = BlockWorld.MaxCohortCells;
        public bool Step(BlockWorld current, int maxWork, out int work)
        {
            if (maxWork < 0) throw new ArgumentOutOfRangeException(nameof(maxWork));
            work = 0; if (!IsValid(current)) { Failed = true; return false; } if (Ready) return true;
            while (maxWork - work >= EstimateNextStepWork())
            {
                int cost;
                switch (_phase)
                {
                    case 0:
                        if (!_chunks.MoveNext()) { _phase++; cost = 1; break; }
                        var chunk = _chunks.Current; SourceWorld._owned.Remove(chunk.Key); _copy._chunks.Add(chunk.Key, chunk.Value); cost = 3; break;
                    case 1:
                        if (!_tombstones.MoveNext()) { _phase++; cost = 1; break; }
                        var tombstone = _tombstones.Current; _copy._tombstones.Add(tombstone.Key, tombstone.Value); cost = 2; break;
                    case 2:
                        if (_sourceCohort == null)
                        {
                            if (!_cohorts.MoveNext()) { _phase++; cost = 1; break; }
                            _sourceCohort = _cohorts.Current.Value; _copyCohort = _sourceCohort.CloneDescriptor(); _copyCohort.PreparedRebase = null; _memberIndex = 0;
                            SourceWorld._ownedCohorts.Remove(_sourceCohort.Id); _copy.Cohorts.Add(_copyCohort.Id, _copyCohort); cost = 4;
                            if (_ownMetadata) { _copy._ownedCohorts.Add(_copyCohort.Id); cost++; }
                            break;
                        }
                        if (_memberIndex < _sourceCohort.Members.Count) { _copyCohort.Members.Add(_sourceCohort.Members[_memberIndex++]); cost = 1; break; }
                        _sourceCohort = _copyCohort = null; cost = 1; break;
                    case 3:
                        if (!_runs.MoveNext()) { _phase++; cost = 1; break; }
                        var sourceRun = _runs.Current.Value; var run = sourceRun.Clone();
                        if (run.Cohort != null) run.Cohort = _copy.Cohorts[run.Cohort.Id];
                        SourceWorld._ownedRuns.Remove(run.Id); SourceWorld._ownedRunCells.Remove(run.Id);
                        _copy.Falling.Add(run.Id, run); cost = 5;
                        if (_ownMetadata) { _copy._ownedRuns.Add(run.Id); cost++; }
                        break;
                    case 4:
                        if (!_links.MoveNext()) { _phase++; cost = 1; break; }
                        var descriptor = _copy.Cohorts[_links.Current.Key];
                        if (descriptor.LowerSupport != null) { descriptor.LowerSupport = _copy.Cohorts[descriptor.LowerSupport.Id]; _copy.AddUpperSupport(descriptor.LowerSupport.Id, descriptor.Id); }
                        cost = 3; break;
                    case 5:
                        if (!_aliasOpen)
                        {
                            if (!_aliases.MoveNext()) { _phase++; cost = 1; break; }
                            _alias = _aliases.Current; _copy._targetAliases.Add(_alias.Key, _alias.Value);
                            _aliasEndpoint = _alias.Value; _aliasDepth = 0; _aliasOpen = true; cost = 3; break;
                        }
                        if (SourceWorld._targetAliases.TryGetValue(_aliasEndpoint, out var next))
                        {
                            if (++_aliasDepth >= BlockWorld.MaxCohortPieceCells) { Failed = true; return false; }
                            _aliasEndpoint = next; cost = 1; break;
                        }
                        if (!_copy._aliasOrigins.TryGetValue(_aliasEndpoint, out var origins))
                            _copy._aliasOrigins.Add(_aliasEndpoint, origins = new HashSet<FallingCellAddress>());
                        origins.Add(_alias.Key); _aliasOpen = false; cost = 3; break;
                    case 6:
                        if (!_pending.MoveNext()) { _phase++; cost = 1; break; }
                        _copy._pendingCohortIds.Add(_pending.Current); cost = 2; break;
                    case 7:
                        if (!_legacy.MoveNext()) { _phase++; cost = 1; break; }
                        _copy._newLegacyRunIds.Add(_legacy.Current); cost = 2; break;
                    case 8:
                        if (!_ownMetadata) { _phase = 10; cost = 1; break; }
                        if (!_preparedBuffered)
                        { if (!_prepared.MoveNext()) _phase++; else _preparedBuffered = true; cost = 1; break; }
                        // An owned clone of an unpublished draft also preserves
                        // its prepared activation registrations, one admitted
                        // shared-prefix installation at a time.
                        _copy.PrivatePrepareCohort(_prepared.Current, out cost); _preparedBuffered = false; break;
                    case 9:
                        if (!_preparedLegacy.MoveNext()) { _phase++; cost = 1; break; }
                        _copy._preparedLegacyRunIds.Add(_preparedLegacy.Current); cost = 2; break;
                    default: Ready = true; cost = 1; break;
                }
                work += cost; ProcessedWork += cost; ProgressVersion++;
                if (Ready) break;
            }
            return Ready;
        }
        public BlockWorld TakeWorld()
        {
            if (!Ready || Failed || _copy == null) throw new InvalidOperationException("The metadata clone is not ready.");
            var result = _copy; _copy = null; Dispose(); return result;
        }
        public void Cancel() { Failed = true; _copy = null; Dispose(); }
        public void Dispose()
        { _chunks.Dispose(); _tombstones.Dispose(); _cohorts.Dispose(); _links.Dispose(); _runs.Dispose(); _aliases.Dispose(); _pending.Dispose(); _legacy.Dispose(); _prepared.Dispose(); _preparedLegacy.Dispose(); }
    }

    /// <summary>A finite, private whole-World preparation transaction. Partial state is never returned to Terrain.</summary>
    public sealed class FallingPrivateReplacement : IDisposable
    {
        public BlockWorld SourceWorld { get; }
        public uint SourceRevision { get; }
        public uint SourceRound { get; }
        public ulong SourceMutationGeneration { get; }
        public ulong RemovedParentId => _replacement.ParentId;
        public bool Ready { get; private set; }
        public bool Failed { get; private set; }
        public bool WaitingForLandingProof { get; private set; }
        public FallingPrivateLandingProofRequest LandingProofRequest
            => WaitingForLandingProof ? _landingProofRequest : throw new InvalidOperationException("No private landing proof is pending.");
        public long ProcessedWork { get; private set; }
        public ulong ProgressVersion { get; private set; }
        public IReadOnlyList<FallingCohort> Children => Ready ? _children : throw new InvalidOperationException("Children are private until preparation completes.");
        public IReadOnlyList<FallingCohort> PreparedExistingCohorts => Ready ? _existing : throw new InvalidOperationException("Existing cohort edits are private until preparation completes.");
        readonly FallingCohortReplacement _replacement;
        readonly BlockWorldCloneBuilder _clone;
        readonly double _sourceTime;
        readonly List<FallingCohort> _children = new();
        readonly List<FallingCohort> _existing = new();
        readonly List<FrozenCohortMotion> _topDownMotion = new();
        readonly HashSet<ulong> _reused = new();
        BlockWorld _privateWorld;
        FallingCohort _parent, _motionNode, _child;
        FrozenCohortMotion[] _motion;
        FrozenCohortMotion[] _ownerOldMotion;
        FallingCohort _normalizationOwner;
        ulong _retainedOwnerId;
        int _ownerMotionCopy;
        float _ownerOffset, _ownerSpeed, _ownerLandingOffset;
        bool _ownerLandingVerified;
        FallingCohort _bridgeUpper, _bridgeSource;
        FrozenCohortMotion[] _bridgeMotion;
        int _bridgeCopy, _existingPrepareIndex, _existingSealIndex;
        float _bridgeOffset, _bridgeSpeed, _bridgeLandingOffset;
        bool _bridgeLandingVerified;
        IEnumerator<ulong> _upperIds;
        IEnumerator<FallingCellAddress> _origins;
        HashSet<FallingCellAddress> _destinationOrigins;
        FallingCellAddress _aliasFrom, _aliasTo;
        FallingCohortReplacement.PreparedPiece _piece;
        ulong _pieceId;
        float _offset, _speed;
        float _verifiedLandingOffset;
        bool _verifiedLanding;
        FallingPrivateLandingProofRequest _landingProofRequest;
        int _phase, _retainedIndex = -1, _motionCopy, _spanIndex, _pieceIndex, _aliasIndex, _sourceIdIndex, _prepareIndex, _sealChild, _sealMember;
        bool _pieceOpen, _aliasOpen;
        internal FallingPrivateReplacement(BlockWorld source, FallingCohortReplacement replacement, double sourceTime)
        {
            if (replacement == null || !replacement.Ready || !replacement.IsValid(source) || !double.IsFinite(sourceTime))
                throw new InvalidOperationException("A private replacement requires a complete, valid membership proof.");
            SourceWorld = source; SourceRevision = source.Revision; SourceRound = source.Round; SourceMutationGeneration = source.MutationGeneration;
            _replacement = replacement; _sourceTime = sourceTime; _clone = source.BeginBudgetedClone(); _motionNode = source.Cohorts[replacement.ParentId];
        }
        public bool IsValid(BlockWorld current)
            => !Failed && ReferenceEquals(current, SourceWorld) && current.Revision == SourceRevision && current.Round == SourceRound
                && current.MutationGeneration == SourceMutationGeneration && _replacement.IsValid(current);
        public int EstimateNextStepWork()
        {
            if (_phase == 0) return _clone.EstimateNextStepWork();
            if (_phase == 2) return Math.Max(1, _topDownMotion.Count);
            if (_phase == 4) return Math.Max(1, _motion.Length);
            if (_phase == 11) return Math.Max(1, SourceWorld.Cohorts[_retainedOwnerId].RetainedSupportMotion.Length + 1);
            if (_phase == 13) return Math.Max(1, _ownerOldMotion.Length);
            if (_phase == 14) return 64;
            if (_phase == 15) return Math.Max(1, _motion.Length + 1);
            if (_phase == 17 || _phase == 18) return 64;
            if (_phase == 5 && _child == null) return 20;
            if (_phase == 9 && _existingPrepareIndex < _existing.Count)
                return _existing[_existingPrepareIndex].Phase == FallingCohortPhase.AwaitActivation
                    ? _privateWorld.EstimatePrivatePrepareCohortWork(_existing[_existingPrepareIndex].Id) : 8;
            if (_phase == 9 && _prepareIndex < _children.Count)
                return _privateWorld.EstimatePrivatePrepareCohortWork(_children[_prepareIndex].Id);
            return 8;
        }
        public long MaximumWork => _clone.MaximumWork + 4096L + 64L * BlockWorld.MaxCohortCells
            + 32L * SourceWorld._targetAliases.Count + (128L + 2L * BlockWorld.MaxCohortCells) * SourceWorld.Cohorts.Count
            + (18L + BlockWorld.MaxCohortCells) * BlockWorld.MaxCohortCells;
        public bool Step(BlockWorld current, int maxWork, out int work)
        {
            if (maxWork < 0) throw new ArgumentOutOfRangeException(nameof(maxWork));
            work = 0; if (!IsValid(current)) { Failed = true; return false; } if (Ready) return true;
            if (WaitingForLandingProof) return false;
            while (maxWork - work >= EstimateNextStepWork())
            {
                int cost = 1;
                switch (_phase)
                {
                    case 0:
                        _clone.Step(current, maxWork - work, out cost);
                        if (_clone.Failed) { Failed = true; return false; }
                        if (_clone.Ready) { _privateWorld = _clone.TakeWorld(); _parent = _privateWorld.Cohorts[RemovedParentId]; _phase++; }
                        if (cost == 0) return false;
                        break;
                    case 1:
                        if (_motionNode == null) { _phase++; break; }
                        if (_retainedIndex >= 0)
                        {
                            _topDownMotion.Add(_motionNode.RetainedSupportMotion[_retainedIndex--]); cost = 1;
                            if (_retainedIndex < 0) _motionNode = null;
                            break;
                        }
                        _topDownMotion.Add(new FrozenCohortMotion(_motionNode.Phase, _motionNode.StartedAt, _motionNode.InitialOffset,
                            _motionNode.InitialSpeed, _motionNode.LandingKnown, _motionNode.LandingOffset,
                            _motionNode.LowerSupport != null ? _motionNode.SupportClearance : _motionNode.RetainedSupportClearance));
                        if (_motionNode.LowerSupport == null && _motionNode.RetainedSupportMotion.Length != 0) _retainedOwnerId = _motionNode.Id;
                        if (_motionNode.LowerSupport != null) _motionNode = _motionNode.LowerSupport;
                        else if (_motionNode.RetainedSupportMotion.Length != 0) _retainedIndex = _motionNode.RetainedSupportMotion.Length - 1;
                        else _motionNode = null;
                        cost = 2; break;
                    case 2:
                        if (_topDownMotion.Count > BlockWorld.MaxCohortCells) { Failed = true; return false; }
                        _motion = new FrozenCohortMotion[_topDownMotion.Count]; _phase++; cost = Math.Max(1, _motion.Length); break;
                    case 3:
                        if (_motionCopy == _motion.Length) { _phase++; break; }
                        _motion[_motionCopy] = _topDownMotion[_motion.Length - 1 - _motionCopy]; _motionCopy++; break;
                    case 4:
                        FrozenCohortMotion.Evaluate(_motion, _sourceTime, out _offset, out _speed); _phase++; cost = Math.Max(1, _motion.Length);
                        if (_retainedOwnerId != 0 && _retainedOwnerId != RemovedParentId)
                        { _normalizationOwner = _privateWorld.Cohorts[_retainedOwnerId]; _phase = 11; break; }
                        if (_replacement.Prepared.Count != 0 && (_parent.RetainedSupportMotion.Length != 0
                            || _parent.LandingKnown && _parent.LowerSupport == null && _replacement.Prepared[0].Span.Bottom.y != _parent.Bottom.y))
                        {
                            var first = _replacement.Prepared[0].Span;
                            _landingProofRequest = new FallingPrivateLandingProofRequest(first.Bottom, first.TopExclusive, first.DetachCursor, _offset, _parent.LowerSupport?.Id ?? 0);
                            WaitingForLandingProof = true;
                        }
                        break;
                    case 5:
                        if (_spanIndex == _replacement.Prepared.Count) { _phase++; break; }
                        var prepared = _replacement.Prepared[_spanIndex];
                        if (_child == null)
                        {
                            if (_privateWorld.NextCohortId == ulong.MaxValue) { Failed = true; return false; }
                            var span = prepared.Span;
                            _child = new FallingCohort { Id = _privateWorld.NextCohortId++, Bottom = span.Bottom, TopExclusive = span.TopExclusive,
                                DetachCursor = span.DetachCursor, Phase = span.DetachCursor < span.TopExclusive ? FallingCohortPhase.Detaching : FallingCohortPhase.AwaitActivation,
                                StartedAt = _sourceTime, InitialOffset = _offset, InitialSpeed = _speed, RebasePending = true, RebaseMotion = _motion,
                                RebaseSourceMoving = _parent.Phase == FallingCohortPhase.Falling, RebaseSourceStartedAt = _parent.StartedAt,
                                RebaseSourceOffset = _parent.InitialOffset, RebaseSourceSpeed = _parent.InitialSpeed,
                                RebaseSourceLandingKnown = _parent.LandingKnown, RebaseSourceLandingOffset = _parent.LandingOffset,
                                LandingKnown = _parent.LandingKnown, LandingOffset = _parent.LandingOffset, NeedsLandingProof = true };
                            if (_children.Count != 0)
                            {
                                var lower = _children[_children.Count - 1]; _child.LowerSupport = lower; _child.SupportClearance = (_child.Bottom.y - lower.TopExclusive) * BlockWorld.CellSize;
                                _child.LandingKnown = false; _child.LandingOffset = 0;
                            }
                            else
                            {
                                float delta = (_child.Bottom.y - _parent.Bottom.y) * BlockWorld.CellSize;
                                if (_verifiedLanding)
                                {
                                    _child.LandingKnown = true; _child.LandingOffset = _verifiedLandingOffset;
                                    _child.VerifiedPrivateLanding = true; _child.VerifiedPrivateLandingOffset = _verifiedLandingOffset;
                                    _child.NeedsLandingProof = false;
                                }
                                else
                                {
                                    _child.LowerSupport = _parent.LowerSupport; _child.SupportClearance = _parent.LowerSupport == null ? 0 : _parent.SupportClearance + delta;
                                    _child.RetainedSupportMotion = _parent.RetainedSupportMotion;
                                    _child.RetainedSupportClearance = _parent.RetainedSupportMotion.Length == 0 ? 0 : _parent.RetainedSupportClearance + delta;
                                }
                            }
                            _privateWorld.Cohorts.Add(_child.Id, _child); _privateWorld._ownedCohorts.Add(_child.Id);
                            if (_child.LowerSupport != null) _privateWorld.AddUpperSupport(_child.LowerSupport.Id, _child.Id);
                            if (_child.Phase == FallingCohortPhase.AwaitActivation) _privateWorld._pendingCohortIds.Add(_child.Id);
                            _children.Add(_child); _pieceIndex = 0; cost = 20; break;
                        }
                        if (_pieceIndex == prepared.Pieces.Count) { _child = null; _spanIndex++; break; }
                        if (!_pieceOpen)
                        {
                            _piece = prepared.Pieces[_pieceIndex];
                            if (_privateWorld.NextRunId == ulong.MaxValue) { Failed = true; return false; }
                            _pieceId = _reused.Add(_piece.SourceId) ? _piece.SourceId : _privateWorld.NextRunId++;
                            _privateWorld.Falling[_pieceId] = new FallingRun { Id = _pieceId, Bottom = _piece.Bottom, Cells = _piece.Cells, Cohort = _child };
                            _privateWorld._ownedRuns.Add(_pieceId); _privateWorld._ownedRunCells.Remove(_pieceId); _child.Members.Add(_pieceId);
                            _pieceOpen = true; _aliasIndex = _piece.FirstIndex; cost = 6; break;
                        }
                        if (_pieceId == _piece.SourceId || _aliasIndex == _piece.FirstIndex + _piece.Count)
                        { _pieceOpen = false; _pieceIndex++; break; }
                        if (!_aliasOpen)
                        {
                            _aliasFrom = new FallingCellAddress(_piece.SourceId, _aliasIndex); _aliasTo = new FallingCellAddress(_pieceId, _aliasIndex);
                            if (!_privateWorld._aliasOrigins.TryGetValue(_aliasTo, out _destinationOrigins))
                                _privateWorld._aliasOrigins.Add(_aliasTo, _destinationOrigins = new HashSet<FallingCellAddress>());
                            _origins = _privateWorld._aliasOrigins.TryGetValue(_aliasFrom, out var origins) ? origins.GetEnumerator() : null;
                            _aliasOpen = true; cost = 3; break;
                        }
                        if (_origins != null && _origins.MoveNext())
                        { var origin = _origins.Current; _privateWorld._targetAliases[origin] = _aliasTo; _destinationOrigins.Add(origin); cost = 3; break; }
                        _origins?.Dispose(); _origins = null; _privateWorld._aliasOrigins.Remove(_aliasFrom);
                        _privateWorld._targetAliases[_aliasFrom] = _aliasTo; _destinationOrigins.Add(_aliasFrom);
                        _aliasOpen = false; _aliasIndex++; cost = 4; break;
                    case 6:
                        if (_sourceIdIndex == _replacement.SourceIds.Length)
                        {
                            _upperIds = _privateWorld._upperSupports.TryGetValue(RemovedParentId, out var ids) ? ids.GetEnumerator() : null;
                            _phase++; cost = 2; break;
                        }
                        ulong sourceId = _replacement.SourceIds[_sourceIdIndex++];
                        if (!_reused.Contains(sourceId)) _privateWorld.Falling.Remove(sourceId); cost = 2; break;
                    case 7:
                        if (_upperIds != null && _upperIds.MoveNext())
                        {
                            _bridgeUpper = _privateWorld.Cohorts[_upperIds.Current]; _bridgeSource = SourceWorld.Cohorts[_bridgeUpper.Id];
                            if (_bridgeUpper.Phase == FallingCohortPhase.Detaching && _bridgeUpper.Members.Count == 0)
                            {
                                // An undetached descriptor has no published moving cells to rebase or prove.
                                // Keep its complete old support until normal final detachment obtains a real proof.
                                _bridgeUpper.RetainedSupportMotion = _motion; _bridgeUpper.RetainedSupportClearance = _bridgeUpper.SupportClearance;
                                _bridgeUpper.LowerSupport = null; _bridgeUpper.SupportClearance = 0; _bridgeUpper.NeedsLandingProof = true;
                                _privateWorld._ownedCohorts.Add(_bridgeUpper.Id); _existing.Add(_bridgeUpper);
                                _bridgeUpper = _bridgeSource = null; cost = 8; break;
                            }
                            _bridgeCopy = 0; _bridgeLandingVerified = false; _phase = 15; cost = 3; break;
                        }
                        _upperIds?.Dispose(); _upperIds = null; _phase++; break;
                    case 8:
                        if (_parent.LowerSupport != null) _privateWorld.RemoveUpperSupport(_parent.LowerSupport.Id, RemovedParentId);
                        _privateWorld._upperSupports.Remove(RemovedParentId); _privateWorld.Cohorts.Remove(RemovedParentId);
                        _privateWorld._ownedCohorts.Remove(RemovedParentId); _privateWorld._pendingCohortIds.Remove(RemovedParentId); _privateWorld._preparedCohortIds.Remove(RemovedParentId);
                        _privateWorld.MutationGeneration++; _phase++; cost = 7; break;
                    case 9:
                        if (_existingPrepareIndex < _existing.Count)
                        {
                            var existing = _existing[_existingPrepareIndex++];
                            if (existing.Phase == FallingCohortPhase.AwaitActivation)
                                _privateWorld.PrivatePrepareCohort(existing.Id, out cost);
                            break;
                        }
                        if (_prepareIndex == _children.Count) { _phase++; break; }
                        var activate = _children[_prepareIndex];
                        if (activate.Phase == FallingCohortPhase.AwaitActivation)
                        {
                            if (activate.RebaseMotion.Length == 0) { Failed = true; return false; }
                            if (!activate.VerifiedPrivateLanding)
                            {
                                // Every new child retains its own source-locked
                                // stable/static cap; a live lower remains an extra
                                // bound and cannot certify an intervening platform.
                                _landingProofRequest = new FallingPrivateLandingProofRequest(activate.Bottom,
                                    activate.TopExclusive, activate.DetachCursor, _offset,
                                    activate.LowerSupport?.Id ?? 0, activate.Id);
                                WaitingForLandingProof = true; break;
                            }
                            _privateWorld.PrivatePrepareCohort(activate.Id, out cost);
                        }
                        _prepareIndex++; break;
                    case 10:
                        if (_sealChild == _children.Count)
                        {
                            if (_existingSealIndex < _existing.Count)
                            {
                                var existingSeal = _existing[_existingSealIndex++];
                                if (!_privateWorld._preparedCohortIds.Contains(existingSeal.Id)) _privateWorld._ownedCohorts.Remove(existingSeal.Id);
                                cost = 3; break;
                            }
                            _privateWorld._privateOwnershipPrepared = true; Ready = true; cost = 3; break;
                        }
                        var sealChild = _children[_sealChild];
                        if (_sealMember < sealChild.Members.Count)
                        { _privateWorld._ownedRuns.Remove(sealChild.Members[_sealMember++]); break; }
                        if (!_privateWorld._preparedCohortIds.Contains(sealChild.Id)) _privateWorld._ownedCohorts.Remove(sealChild.Id);
                        _sealChild++; _sealMember = 0; cost = 2; break;
                    case 11:
                        // The terminal live node's complete old trajectory is exactly this immutable prefix.
                        _ownerOldMotion = new FrozenCohortMotion[SourceWorld.Cohorts[_retainedOwnerId].RetainedSupportMotion.Length + 1];
                        cost = Math.Max(1, _ownerOldMotion.Length); _phase++; break;
                    case 12:
                        if (_ownerMotionCopy == _ownerOldMotion.Length) { _phase++; break; }
                        _ownerOldMotion[_ownerMotionCopy] = _motion[_ownerMotionCopy]; _ownerMotionCopy++; break;
                    case 13:
                        FrozenCohortMotion.Evaluate(_ownerOldMotion, _sourceTime, out _ownerOffset, out _ownerSpeed);
                        _landingProofRequest = new FallingPrivateLandingProofRequest(_normalizationOwner.Bottom,
                            _normalizationOwner.TopExclusive, _normalizationOwner.DetachCursor, _ownerOffset, 0, _normalizationOwner.Id);
                        WaitingForLandingProof = true; _phase++; cost = Math.Max(1, _ownerOldMotion.Length); break;
                    case 14:
                        if (!_ownerLandingVerified) { Failed = true; return false; }
                        var ownerSource = SourceWorld.Cohorts[_normalizationOwner.Id];
                        _normalizationOwner.RetainedSupportMotion = Array.Empty<FrozenCohortMotion>(); _normalizationOwner.RetainedSupportClearance = 0;
                        _normalizationOwner.StartedAt = _sourceTime; _normalizationOwner.InitialOffset = _ownerOffset; _normalizationOwner.InitialSpeed = _ownerSpeed;
                        _normalizationOwner.LandingKnown = true; _normalizationOwner.LandingOffset = _ownerLandingOffset; _normalizationOwner.NeedsLandingProof = false;
                        _normalizationOwner.RebaseMotion = _ownerOldMotion; _normalizationOwner.RebasePending = true;
                        _normalizationOwner.RebasePrefix = null;
                        _normalizationOwner.RebaseSourceMoving = ownerSource.Phase == FallingCohortPhase.Falling;
                        _normalizationOwner.RebaseSourceStartedAt = ownerSource.StartedAt; _normalizationOwner.RebaseSourceOffset = ownerSource.InitialOffset;
                        _normalizationOwner.RebaseSourceSpeed = ownerSource.InitialSpeed; _normalizationOwner.RebaseSourceLandingKnown = ownerSource.LandingKnown;
                        _normalizationOwner.RebaseSourceLandingOffset = ownerSource.LandingOffset;
                        _normalizationOwner.VerifiedPrivateLanding = true; _normalizationOwner.VerifiedPrivateLandingOffset = _ownerLandingOffset;
                        _normalizationOwner.Phase = _normalizationOwner.DetachCursor < _normalizationOwner.TopExclusive
                            ? FallingCohortPhase.Detaching : FallingCohortPhase.AwaitActivation;
                        if (_normalizationOwner.Phase == FallingCohortPhase.AwaitActivation) _privateWorld._pendingCohortIds.Add(_normalizationOwner.Id);
                        _privateWorld._ownedCohorts.Add(_normalizationOwner.Id); _existing.Add(_normalizationOwner);
                        _phase = 5; cost = 64; break;
                    case 15:
                        if (_motion.Length >= BlockWorld.MaxCohortCells) { Failed = true; return false; }
                        _bridgeMotion = new FrozenCohortMotion[_motion.Length + 1]; cost = _bridgeMotion.Length; _phase++; break;
                    case 16:
                        if (_bridgeCopy < _motion.Length) { _bridgeMotion[_bridgeCopy] = _motion[_bridgeCopy]; _bridgeCopy++; break; }
                        _bridgeMotion[_motion.Length] = new FrozenCohortMotion(_bridgeSource.Phase, _bridgeSource.StartedAt,
                            _bridgeSource.InitialOffset, _bridgeSource.InitialSpeed, _bridgeSource.LandingKnown, _bridgeSource.LandingOffset, _bridgeSource.SupportClearance);
                        _phase++; break;
                    case 17:
                        FrozenCohortMotion.EvaluateNode(_bridgeMotion[_motion.Length], _sourceTime, true, _offset, _speed, out _bridgeOffset, out _bridgeSpeed);
                        _landingProofRequest = new FallingPrivateLandingProofRequest(_bridgeSource.Bottom, _bridgeSource.TopExclusive,
                            _bridgeSource.DetachCursor, _bridgeOffset, RemovedParentId, _bridgeSource.Id);
                        WaitingForLandingProof = true; _phase++; cost = 64; break;
                    case 18:
                        if (!_bridgeLandingVerified) { Failed = true; return false; }
                        _bridgeUpper.LowerSupport = _children.Count == 0 ? null : _children[_children.Count - 1];
                        _bridgeUpper.SupportClearance = _bridgeUpper.LowerSupport == null ? 0 : (_bridgeUpper.Bottom.y - _bridgeUpper.LowerSupport.TopExclusive) * BlockWorld.CellSize;
                        if (_bridgeUpper.SupportClearance < 0) { Failed = true; return false; }
                        if (_bridgeUpper.LowerSupport != null) _privateWorld.AddUpperSupport(_bridgeUpper.LowerSupport.Id, _bridgeUpper.Id);
                        _bridgeUpper.RetainedSupportMotion = Array.Empty<FrozenCohortMotion>(); _bridgeUpper.RetainedSupportClearance = 0;
                        _bridgeUpper.StartedAt = _sourceTime; _bridgeUpper.InitialOffset = _bridgeOffset; _bridgeUpper.InitialSpeed = _bridgeSpeed;
                        _bridgeUpper.LandingKnown = true; _bridgeUpper.LandingOffset = _bridgeLandingOffset; _bridgeUpper.NeedsLandingProof = false;
                        _bridgeUpper.RebasePending = true; _bridgeUpper.RebaseMotion = _bridgeMotion; _bridgeUpper.RebasePrefix = _motion;
                        _bridgeUpper.RebaseSourceMoving = _bridgeSource.Phase == FallingCohortPhase.Falling;
                        _bridgeUpper.RebaseSourceStartedAt = _bridgeSource.StartedAt; _bridgeUpper.RebaseSourceOffset = _bridgeSource.InitialOffset;
                        _bridgeUpper.RebaseSourceSpeed = _bridgeSource.InitialSpeed; _bridgeUpper.RebaseSourceLandingKnown = _bridgeSource.LandingKnown;
                        _bridgeUpper.RebaseSourceLandingOffset = _bridgeSource.LandingOffset;
                        _bridgeUpper.VerifiedPrivateLanding = true; _bridgeUpper.VerifiedPrivateLandingOffset = _bridgeLandingOffset;
                        _bridgeUpper.Phase = _bridgeUpper.DetachCursor < _bridgeUpper.TopExclusive ? FallingCohortPhase.Detaching : FallingCohortPhase.AwaitActivation;
                        if (_bridgeUpper.Phase == FallingCohortPhase.AwaitActivation) _privateWorld._pendingCohortIds.Add(_bridgeUpper.Id);
                        _privateWorld._ownedCohorts.Add(_bridgeUpper.Id); _existing.Add(_bridgeUpper);
                        _bridgeUpper = _bridgeSource = null; _bridgeMotion = null; _phase = 7; cost = 64; break;
                }
                work += cost; ProcessedWork += cost; ProgressVersion++;
                if (Ready || WaitingForLandingProof) break;
            }
            return Ready;
        }
        public BlockWorld TakePreparedWorld()
        {
            if (!Ready || Failed || _privateWorld == null) throw new InvalidOperationException("The complete private replacement is not ready.");
            var result = _privateWorld; _privateWorld = null; Dispose(); return result;
        }
        public void ProvideLandingProof(BlockWorld current, float landingOffset)
        {
            if (!IsValid(current) || !WaitingForLandingProof || !float.IsFinite(landingOffset) || landingOffset < _landingProofRequest.SourceOffsetAtSourceTime)
                throw new InvalidOperationException("A finite stable landing proof must belong to the exact locked source.");
            if (_landingProofRequest.LiveLowerId != 0 && _landingProofRequest.TargetCohortId == 0)
                throw new InvalidOperationException("A live dynamic support cannot be removed by a static landing proof.");
            if (_phase == 9)
            {
                if (_prepareIndex >= _children.Count || _children[_prepareIndex].Id != _landingProofRequest.TargetCohortId)
                    throw new InvalidOperationException("The new child's proof must match its exact pending source span.");
                var child = _children[_prepareIndex];
                child.LandingKnown = true; child.LandingOffset = landingOffset; child.NeedsLandingProof = false;
                child.VerifiedPrivateLanding = true; child.VerifiedPrivateLandingOffset = landingOffset;
            }
            else if (_landingProofRequest.TargetCohortId == 0) { _verifiedLanding = true; _verifiedLandingOffset = landingOffset; }
            else if (_phase == 18) { _bridgeLandingVerified = true; _bridgeLandingOffset = landingOffset; }
            else { _ownerLandingVerified = true; _ownerLandingOffset = landingOffset; }
            WaitingForLandingProof = false; ProgressVersion++;
        }
        public void Cancel() { Failed = true; WaitingForLandingProof = false; _privateWorld = null; Dispose(); }
        public void Dispose() { _privateWorld = null; _clone.Cancel(); _origins?.Dispose(); _upperIds?.Dispose(); }
    }

    /// <summary>Sparse lattice state. Mutable copies are private to a terrain transaction.</summary>
    public sealed class BlockWorld
    {
        public const float CellSize = .10f, Gravity = 9.81f;
        public static float Top => Splatoon.Config.BlockGameplaySettings.WorldTopHeight;
        public const int ChunkSize = 16, CellsPerChunk = ChunkSize * ChunkSize * ChunkSize;
        public const int ProtocolVersion = 3, MaxCohortCells = 400, MaxCohortPieceCells = 16;
        const uint Magic = 0x334B4C42, DeltaMagic = 0x33444C42; // BLK3 / BLD3: explicit shared-motion wire version.
        internal readonly Dictionary<Vector3Int, BlockChunk> _chunks = new();
        internal readonly Dictionary<Vector3Int, uint> _tombstones = new();
        internal readonly HashSet<Vector3Int> _owned = new();
        internal readonly HashSet<ulong> _ownedRuns = new();
        internal readonly HashSet<ulong> _ownedRunCells = new();
        internal readonly HashSet<ulong> _ownedCohorts = new(), _pendingCohortIds = new(), _preparedCohortIds = new();
        internal readonly HashSet<ulong> _newLegacyRunIds = new(), _preparedLegacyRunIds = new();
        internal readonly Dictionary<FallingCellAddress, FallingCellAddress> _targetAliases = new();
        internal readonly Dictionary<FallingCellAddress, HashSet<FallingCellAddress>> _aliasOrigins = new();
        internal readonly Dictionary<ulong, HashSet<ulong>> _upperSupports = new();
        BlockCohortEditPreparation _preparedDependentEditOwner;
        BlockCohortEditPreparation _activeDependentEditOwner;
        internal bool DependentEditIncomplete;
        internal ulong DependentPreparationVersion;
        readonly Dictionary<FrozenCohortMotion[], PreparedRebaseSample> _preparedMotionSamples = new();
        long _preparedMotionPublicationWork;
        public long EstimatedPreparedPublicationWork => _preparedMotionPublicationWork + 16L * _preparedCohortIds.Count + 2L * _preparedLegacyRunIds.Count;
        public long LastPublicationWork { get; private set; }
        public int LastPublicationMotionSamples { get; private set; }
        internal bool _privateOwnershipPrepared;
        public readonly Dictionary<ulong, FallingRun> Falling = new();
        public readonly Dictionary<ulong, FallingCohort> Cohorts = new();
        public IReadOnlyDictionary<FallingCellAddress, FallingCellAddress> FallingTargetAliases => _targetAliases;
        public IReadOnlyDictionary<Vector3Int, BlockChunk> Chunks => _chunks;
        public IEnumerable<Vector3Int> RevisionKeys => _chunks.Keys.Concat(_tombstones.Keys);
        public uint Revision { get; private set; }
        public uint Round { get; private set; }
        public ulong NextRunId = 1, NextCohortId = 1;
        public ulong MutationGeneration { get; internal set; }
        public int PinkCount { get; internal set; }
        public int BlueCount { get; internal set; }
        public Vector2 Dimensions { get; private set; }
        public float Bottom { get; private set; }

        public BlockWorld(Vector2 dimensions, uint round = 0, float bottom = -8)
        { Dimensions = dimensions; Round = round; Bottom = bottom; }
        public static Vector3Int CellAt(Vector3 p) => new(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
        public static Vector3 Center(Vector3Int c) => ((Vector3)c + Vector3.one * .5f) * CellSize;
        static int Div(int value) => value >= 0 ? value / ChunkSize : (value + 1) / ChunkSize - 1;
        public static Vector3Int ChunkAt(Vector3Int cell) => new(Div(cell.x), Div(cell.y), Div(cell.z));
        public static Vector3Int ChunkOrigin(Vector3Int chunk) => chunk * ChunkSize;
        public static int Index(Vector3Int cell)
        {
            var local = cell - ChunkOrigin(ChunkAt(cell));
            return local.x + ChunkSize * (local.y + ChunkSize * local.z);
        }
        public static Vector3Int CellFromIndex(Vector3Int chunk, int index)
            => ChunkOrigin(chunk) + new Vector3Int(index % ChunkSize, index / ChunkSize % ChunkSize, index / (ChunkSize * ChunkSize));
        public bool InBounds(Vector3Int cell)
        {
            var p = Center(cell);
            return p.x >= -Dimensions.x * .5f && p.x < Dimensions.x * .5f && p.z >= -Dimensions.y * .5f && p.z < Dimensions.y * .5f
                && p.y >= Bottom && p.y < Top;
        }
        public bool TryGet(Vector3Int cell, out BlockCell value)
        {
            if (_chunks.TryGetValue(ChunkAt(cell), out var chunk)) { value = chunk.Cells[Index(cell)]; return value.Occupied; }
            value = default; return false;
        }
        /// <summary>Static occupied bits along one local 16-cell axis row.</summary>
        public ushort OccupiedRow(Vector3Int cell, int axis)
        {
            if ((uint)axis > 2) throw new ArgumentOutOfRangeException(nameof(axis));
            var key = ChunkAt(cell);
            return _chunks.TryGetValue(key, out var chunk) ? chunk.OccupiedRow(axis, cell - ChunkOrigin(key)) : (ushort)0;
        }
        // Fresh derived rows of this World, never a cross-draft cell proof cache.
        // At most two row lookups for a <=16-cell piece; all cell payload copying
        // and the exact three occupancy-row updates remain in DetachSmallPiece.
        internal static int VerticalPieceRows(Vector3Int bottom, int count)
            => ((bottom.y & 15) + count + 15) / ChunkSize;
        internal int UnanchoredVerticalSpanLength(Vector3Int bottom, int maximum)
        {
            if (maximum <= 0 || maximum > MaxCohortPieceCells) throw new ArgumentOutOfRangeException(nameof(maximum));
            var key = ChunkAt(bottom); var local = bottom - ChunkOrigin(key); int length = 0;
            while (length < maximum)
            {
                if (!_chunks.TryGetValue(key, out var chunk)) return length;
                int part = Math.Min(maximum - length, ChunkSize - local.y);
                uint available = (uint)(chunk.UnanchoredVerticalRow(local) >> local.y);
                uint missing = ~available & ((1u << part) - 1);
                if (missing != 0) return length + FirstSetBit16(missing);
                length += part; key += Vector3Int.up; local.y = 0;
            }
            return length;
        }
        internal int UnanchoredVerticalGapLength(Vector3Int bottom, int maximum)
        {
            var key = ChunkAt(bottom); var local = bottom - ChunkOrigin(key);
            if (maximum <= 0 || maximum > ChunkSize - local.y) throw new ArgumentOutOfRangeException(nameof(maximum));
            if (!_chunks.TryGetValue(key, out var chunk)) return maximum;
            uint candidates = (uint)(chunk.UnanchoredVerticalRow(local) >> local.y) & ((1u << maximum) - 1);
            return candidates == 0 ? maximum : FirstSetBit16(candidates);
        }
        static int FirstSetBit16(uint bits)
        {
            int offset = 0;
            if ((bits & 255) == 0) { offset += 8; bits >>= 8; }
            if ((bits & 15) == 0) { offset += 4; bits >>= 4; }
            if ((bits & 3) == 0) { offset += 2; bits >>= 2; }
            return offset + ((bits & 1) == 0 ? 1 : 0);
        }
        public uint ChunkRevision(Vector3Int chunk) => _chunks.TryGetValue(chunk, out var c) ? c.Revision : _tombstones.TryGetValue(chunk, out var r) ? r : 0;
        public BlockWorld Clone()
        {
            if (DependentEditIncomplete) throw new InvalidOperationException("An incomplete dependent draft cannot be shared.");
            _preparedDependentEditOwner = null;
            DependentPreparationVersion++;
            _owned.Clear(); _ownedRuns.Clear(); _ownedRunCells.Clear(); _ownedCohorts.Clear(); _preparedCohortIds.Clear(); _preparedLegacyRunIds.Clear();
            _preparedMotionSamples.Clear(); _preparedMotionPublicationWork = 0;
            var copy = new BlockWorld(Dimensions, Round, Bottom) { Revision = Revision, PinkCount = PinkCount, BlueCount = BlueCount, NextRunId = NextRunId, NextCohortId = NextCohortId, MutationGeneration = MutationGeneration };
            copy._chunks.EnsureCapacity(_chunks.Count); copy._tombstones.EnsureCapacity(_tombstones.Count); copy.Falling.EnsureCapacity(Falling.Count);
            foreach (var pair in _chunks) copy._chunks.Add(pair.Key, pair.Value);
            foreach (var pair in _tombstones) copy._tombstones.Add(pair.Key, pair.Value);
            foreach (var pair in Falling) copy.Falling.Add(pair.Key, pair.Value);
            foreach (var pair in Cohorts) copy.Cohorts.Add(pair.Key, pair.Value);
            foreach (var pair in _targetAliases) copy._targetAliases.Add(pair.Key, pair.Value);
            foreach (var pair in _aliasOrigins) copy._aliasOrigins.Add(pair.Key, new HashSet<FallingCellAddress>(pair.Value));
            copy._pendingCohortIds.UnionWith(_pendingCohortIds);
            copy._newLegacyRunIds.UnionWith(_newLegacyRunIds);
            foreach (var pair in _upperSupports) copy._upperSupports.Add(pair.Key, new HashSet<ulong>(pair.Value));
            // Prepared ownership is local to a transaction and is never inherited by another copy.
            return copy;
        }
        internal BlockWorld CreateCloneShell()
            => new(Dimensions, Round, Bottom) { Revision = Revision, PinkCount = PinkCount, BlueCount = BlueCount,
                NextRunId = NextRunId, NextCohortId = NextCohortId, MutationGeneration = MutationGeneration };
        public BlockWorldCloneBuilder BeginBudgetedClone() => new(this);
        // All descriptor/run objects are copied under admission; immutable cell arrays still require COW.
        public BlockWorldCloneBuilder BeginBudgetedOwnedClone() => new(this, true);
        // The caller must exclusively own this unpublished draft until ready.
        // Only its dependent metadata is copied; unrelated columns are untouched.
        public BlockCohortEditPreparation BeginBudgetedCohortEdit(ulong id)
        {
            if (DependentEditIncomplete) throw new InvalidOperationException("This draft already has a dependent preparation owner.");
            _preparedDependentEditOwner = null;
            var owner = new BlockCohortEditPreparation(this, id); _activeDependentEditOwner = owner; DependentEditIncomplete = true;
            return owner;
        }
        public FallingPrivateReplacement BeginPrivateReplacement(FallingCohortReplacement prepared, double sourceTime)
            => new(this, prepared, sourceTime);
        public bool Set(Vector3Int cell, BlockCell value)
        {
            if (!InBounds(cell)) return false;
            var key = ChunkAt(cell);
            if (!_chunks.TryGetValue(key, out var chunk))
            { if (!value.Occupied) return false; _chunks.Add(key, chunk = new BlockChunk()); _owned.Add(key); }
            else if (!_owned.Contains(key)) { _chunks[key] = chunk = chunk.Clone(); _owned.Add(key); }
            int index = Index(cell); var before = chunk.Cells[index];
            if (before.Owner == value.Owner && before.Flags == value.Flags && before.Durability == value.Durability) return false;
            AddCount(before.Owner, -1); AddCount(value.Owner, 1);
            chunk.Count += (value.Occupied ? 1 : 0) - (before.Occupied ? 1 : 0);
            chunk.Cells[index] = value;
            MutationGeneration++;
            if (before.Occupied != value.Occupied || before.Anchored != value.Anchored) chunk.SetOccupied(index, value.Occupied);
            return true;
        }
        void AddCount(byte team, int count) { if (team == 1) PinkCount += count; else if (team == 2) BlueCount += count; }
        public bool HasSupport(Vector3Int cell, Func<Vector3Int, bool> staticSupport = null)
        {
            return TryGet(cell, out var own) && own.Anchored
                || InBounds(cell) && cell.y <= Mathf.CeilToInt(Bottom / CellSize)
                || TryGet(cell + Vector3Int.down, out _)
                || (staticSupport != null && staticSupport(cell));
        }
        public FallingRun Detach(Vector3Int bottom, int count, double now)
        {
            if (count <= 0 || count > MaxCohortCells || !double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(count));
            if (NextRunId == 0 || NextRunId == ulong.MaxValue) throw new InvalidOperationException("Run identities exhausted.");
            if (count <= MaxCohortPieceCells && InBounds(bottom) && InBounds(bottom + Vector3Int.up * (count - 1)))
                return DetachSmallPiece(bottom, count, now);
            var cells = new BlockCell[count];
            for (int i = 0; i < count; i++)
            { var c = bottom + Vector3Int.up * i; if (!TryGet(c, out cells[i]) || cells[i].Anchored) throw new InvalidOperationException("Only unanchored occupied runs can fall."); }
            var run = new FallingRun { Id = NextRunId++, Bottom = bottom, Cells = cells, StartedAt = now };
            for (int i = 0; i < count; i++) { Set(bottom + Vector3Int.up * i, default); AddCount(cells[i].Owner, 1); }
            Falling.Add(run.Id, run); _ownedRuns.Add(run.Id); _ownedRunCells.Add(run.Id); _newLegacyRunIds.Add(run.Id); return run;
        }
        FallingRun DetachSmallPiece(Vector3Int bottom, int count, double now)
        {
            var key = ChunkAt(bottom); var local = bottom - ChunkOrigin(key);
            int firstCount = Math.Min(count, ChunkSize - local.y);
            int firstIndex = local.x + ChunkSize * (local.y + ChunkSize * local.z);
            int secondIndex = local.x + ChunkSize * ChunkSize * local.z;
            if (!_chunks.TryGetValue(key, out var first))
                throw new InvalidOperationException("Only unanchored occupied runs can fall.");
            BlockChunk second = null;
            if (firstCount < count && !_chunks.TryGetValue(key + Vector3Int.up, out second))
                throw new InvalidOperationException("Only unanchored occupied runs can fall.");
            var cells = new BlockCell[count];
            for (int i = 0; i < count; i++)
            {
                var cell = i < firstCount ? first.Cells[firstIndex + ChunkSize * i]
                    : second.Cells[secondIndex + ChunkSize * (i - firstCount)];
                if (!cell.Occupied || cell.Anchored)
                    throw new InvalidOperationException("Only unanchored occupied runs can fall.");
                cells[i] = cell;
            }
            // Match Detach's complete validation before any mutation or run ID allocation.
            var run = new FallingRun { Id = NextRunId++, Bottom = bottom, Cells = cells, StartedAt = now };
            if (!_owned.Contains(key)) { _chunks[key] = first = first.Clone(); _owned.Add(key); }
            first.ClearOccupiedVerticalSpan(firstIndex, firstCount);
            if (second != null)
            {
                var secondKey = key + Vector3Int.up;
                if (!_owned.Contains(secondKey)) { _chunks[secondKey] = second = second.Clone(); _owned.Add(secondKey); }
                second.ClearOccupiedVerticalSpan(secondIndex, count - firstCount);
            }
            // The same occupied cells move from stable storage to falling storage;
            // both team totals retain Set(default) + AddCount(owner, 1)'s exact net result.
            MutationGeneration += (ulong)count;
            Falling.Add(run.Id, run); _ownedRuns.Add(run.Id); _ownedRunCells.Add(run.Id); _newLegacyRunIds.Add(run.Id); return run;
        }
        public FallingCohort BeginCohort(Vector3Int bottom, int count)
        {
            if (count <= 0 || count > MaxCohortCells || !InBounds(bottom) || !InBounds(bottom + Vector3Int.up * (count - 1)))
                throw new ArgumentOutOfRangeException(nameof(count));
            if (NextCohortId == 0 || NextCohortId == ulong.MaxValue) throw new InvalidOperationException("Cohort identities exhausted.");
            var cohort = new FallingCohort { Id = NextCohortId++, Bottom = bottom, TopExclusive = bottom.y + count,
                DetachCursor = bottom.y, Phase = FallingCohortPhase.Detaching };
            Cohorts.Add(cohort.Id, cohort); _ownedCohorts.Add(cohort.Id); MutationGeneration++; return cohort;
        }
        IEnumerable<ulong> DependentCohorts(ulong id)
        {
            var queue = new Queue<ulong>(); var seen = new HashSet<ulong>(); queue.Enqueue(id);
            while (queue.Count > 0)
            {
                ulong next = queue.Dequeue(); if (!seen.Add(next)) continue;
                if (seen.Count > MaxCohortCells) throw new InvalidOperationException("Cohort support graph exceeds one source column.");
                yield return next;
                if (_upperSupports.TryGetValue(next, out var upper)) foreach (ulong member in upper) queue.Enqueue(member);
            }
        }
        // An owned descriptor is never replaced by EditCohort. Its runs and
        // dependent support references already point into this same private
        // world, so only outstanding publication proofs require revisiting
        // descendants. Clone clears ownership; budgeted owned cloning repairs
        // every link before returning; new cohorts start with private members.
        bool CanEditOwnedCohortDirectly(ulong id)
            => !DependentEditIncomplete && _ownedCohorts.Contains(id) && _preparedCohortIds.Count == 0 && _preparedMotionSamples.Count == 0;
        internal bool OwnsDependentEditPreparation(BlockCohortEditPreparation owner)
            => ReferenceEquals(_activeDependentEditOwner, owner) || ReferenceEquals(_preparedDependentEditOwner, owner);
        bool HasPreparedDependentEdit(ulong id)
            => _preparedDependentEditOwner != null && _preparedDependentEditOwner.TargetId == id
                && _preparedDependentEditOwner.Ready && _preparedDependentEditOwner.IsValid(this);
        internal void RegisterPreparedDependentEdit(BlockCohortEditPreparation owner)
        {
            if (owner == null || !owner.Ready || !owner.IsValid(this) || !_ownedCohorts.Contains(owner.TargetId))
                throw new InvalidOperationException("The dependent edit must belong to this completed private draft.");
            _activeDependentEditOwner = null; DependentEditIncomplete = false;
            _preparedDependentEditOwner = owner;
        }
        internal void DiscardPreparedDependentEdit(BlockCohortEditPreparation owner)
        {
            if (ReferenceEquals(_preparedDependentEditOwner, owner)) _preparedDependentEditOwner = null;
            if (ReferenceEquals(_activeDependentEditOwner, owner)) _activeDependentEditOwner = null;
            // Partial private references must never be published or shared;
            // cancellation discards the whole owning draft rather than reuse it.
        }
        public int EstimateEditCohortWork(ulong id)
        {
            if (!Cohorts.ContainsKey(id)) return 0;
            if (HasPreparedDependentEdit(id)) return 1;
            if (CanEditOwnedCohortDirectly(id)) return 1;
            int work = 0;
            foreach (ulong affected in DependentCohorts(id))
            {
                var cohort = Cohorts[affected]; work += 1;
                if (!_ownedCohorts.Contains(affected)) work = checked(work + 1 + 2 * cohort.Members.Count);
                if (cohort.LowerSupport != null) work++;
            }
            return work;
        }
        public FallingCohort EditCohort(ulong id, out int work)
            => EditCohort(id, out work, true);
        FallingCohort EditCohort(ulong id, out int work, bool invalidatePrepared)
        {
            if (DependentEditIncomplete) throw new InvalidOperationException("An incomplete dependent draft cannot be edited outside its owner.");
            work = 0; if (!Cohorts.ContainsKey(id)) return null;
            bool prepared = HasPreparedDependentEdit(id);
            if (invalidatePrepared) MutationGeneration++;
            if (prepared) { _preparedDependentEditOwner = null; work = 1; return Cohorts[id]; }
            if (CanEditOwnedCohortDirectly(id)) { work = 1; return Cohorts[id]; }
            // The reverse graph is derived, cloned per world. This bounded traversal never scans other columns.
            var affectedIds = DependentCohorts(id).ToArray();
            foreach (ulong affected in affectedIds)
            {
                var cohort = Cohorts[affected]; work++;
                if (invalidatePrepared) UnprepareCohort(affected);
                if (!_ownedCohorts.Add(affected)) continue;
                Cohorts[affected] = cohort = cohort.Clone(); work += 1 + cohort.Members.Count;
                foreach (ulong member in cohort.Members)
                {
                    if (!Falling.TryGetValue(member, out var run)) throw new InvalidOperationException("Orphan cohort member.");
                    if (_ownedRuns.Add(member)) Falling[member] = run = run.Clone();
                    run.Cohort = cohort; work++;
                }
            }
            foreach (ulong affected in affectedIds)
            {
                var cohort = Cohorts[affected]; if (cohort.LowerSupport == null) continue;
                cohort.LowerSupport = Cohorts[cohort.LowerSupport.Id]; work++;
            }
            return Cohorts[id];
        }
        static bool ValidSupport(FallingCohort upper, FallingCohort lower)
            => upper.Id != lower.Id && upper.Bottom.x == lower.Bottom.x && upper.Bottom.z == lower.Bottom.z && lower.TopExclusive <= upper.Bottom.y;
        internal void AddUpperSupport(ulong lower, ulong upper)
        { if (!_upperSupports.TryGetValue(lower, out var members)) _upperSupports.Add(lower, members = new HashSet<ulong>()); members.Add(upper); }
        internal void RemoveUpperSupport(ulong lower, ulong upper)
        { if (_upperSupports.TryGetValue(lower, out var members)) { members.Remove(upper); if (members.Count == 0) _upperSupports.Remove(lower); } }
        public int EstimateSetCohortSupportWork(ulong id) => checked(EstimateEditCohortWork(id) + 2);
        public void SetCohortSupport(ulong id, ulong lowerId, float clearance, out int work)
        {
            if (!Cohorts.TryGetValue(id, out var original) || !float.IsFinite(clearance) || clearance < 0
                || lowerId == 0 && clearance != 0 || lowerId != 0 && (!Cohorts.TryGetValue(lowerId, out var lower) || !ValidSupport(original, lower)))
                throw new InvalidOperationException("Invalid cohort support link.");
            var cohort = EditCohort(id, out work);
            if (cohort.LowerSupport != null) RemoveUpperSupport(cohort.LowerSupport.Id, id);
            cohort.LowerSupport = lowerId == 0 ? null : Cohorts[lowerId]; cohort.SupportClearance = clearance;
            cohort.RetainedSupportMotion = Array.Empty<FrozenCohortMotion>(); cohort.RetainedSupportClearance = 0; cohort.NeedsLandingProof = false;
            if (lowerId != 0) AddUpperSupport(lowerId, id); work += 2;
        }
        public int EstimateEditFallingWork(ulong id)
            => Falling.TryGetValue(id, out var run) ? run.Cohort != null ? EstimateEditCohortWork(run.Cohort.Id) : _ownedRuns.Contains(id) ? 0 : 1 : 0;
        public FallingRun EditFalling(ulong id) => EditFalling(id, out _);
        public FallingRun EditFalling(ulong id, out int work)
        {
            work = 0; if (!Falling.TryGetValue(id, out var run)) return null;
            if (run.Cohort != null) { EditCohort(run.Cohort.Id, out work); return Falling[id]; }
            MutationGeneration++;
            if (_ownedRuns.Add(id)) { Falling[id] = run = run.Clone(); work++; }
            return run;
        }
        public int EstimateDetachCohortPieceWork(ulong id, int count)
            => checked(EstimateEditCohortWork(id) + 2 * count + 2
                + (Cohorts.TryGetValue(id, out var source)
                    ? VerticalPieceRows(new Vector3Int(source.Bottom.x, source.DetachCursor, source.Bottom.z), count) : 0));
        public FallingRun DetachCohortPiece(ulong id, int count, double now, out int work)
        {
            work = 0;
            if (!Cohorts.TryGetValue(id, out var source) || source.Phase != FallingCohortPhase.Detaching || source.NeedsRegroup
                || count <= 0 || count > MaxCohortPieceCells || count > source.TopExclusive - source.DetachCursor || !double.IsFinite(now))
                throw new InvalidOperationException("Invalid cohort detach piece.");
            // Validate the whole small piece before any source cells or descriptor are changed.
            var bottom = new Vector3Int(source.Bottom.x, source.DetachCursor, source.Bottom.z);
            int validationRows = VerticalPieceRows(bottom, count);
            if (UnanchoredVerticalSpanLength(bottom, count) != count) throw new InvalidOperationException("Cohort source changed.");
            var cohort = EditCohort(id, out int editWork);
            var run = Detach(bottom, count, now); _newLegacyRunIds.Remove(run.Id); run.Cohort = cohort; cohort.Members.Add(run.Id); cohort.DetachCursor += count;
            cohort.MembershipRevision++;
            if (cohort.DetachCursor == cohort.TopExclusive) { cohort.Phase = FallingCohortPhase.AwaitActivation; _pendingCohortIds.Add(id); }
            work = editWork + 2 * count + validationRows + 2; return run;
        }
        public int EstimateFinalizeCohortDetachmentWork(ulong id) => checked(EstimateEditCohortWork(id) + 1 + (Cohorts.TryGetValue(id, out var cohort) ? EstimateRemoveCohortLinksWork(cohort) : 0));
        public void FinishCohortDetachment(ulong id, int topExclusive, out int work)
        {
            if (!Cohorts.TryGetValue(id, out var source) || source.Phase != FallingCohortPhase.Detaching || topExclusive != source.DetachCursor)
                throw new InvalidOperationException("Only an un-detached tail can be trimmed.");
            var cohort = EditCohort(id, out work); cohort.TopExclusive = topExclusive; cohort.MembershipRevision++; work++;
            if (cohort.Members.Count == 0) { work += RemoveCohortLinks(cohort); UnprepareCohort(id); Cohorts.Remove(id); _pendingCohortIds.Remove(id); return; }
            cohort.Phase = FallingCohortPhase.AwaitActivation; _pendingCohortIds.Add(id);
        }
        int EstimateRemoveCohortLinksWork(FallingCohort cohort)
            => 1 + (_upperSupports.TryGetValue(cohort.Id, out var members) ? 2 * FrozenCohortMotion.Depth(cohort) + members.Count : 0);
        int RemoveCohortLinks(FallingCohort cohort)
        {
            if (cohort.LowerSupport != null) RemoveUpperSupport(cohort.LowerSupport.Id, cohort.Id);
            int work = 1;
            // Callers reserve dependent COW work before reaching here. Upper source spans are not silently dropped.
            if (_upperSupports.TryGetValue(cohort.Id, out var members))
            {
                var frozen = FrozenCohortMotion.Capture(cohort); work += 2 * frozen.Length;
                foreach (ulong upper in members)
                {
                    var descriptor = Cohorts[upper]; descriptor.RetainedSupportMotion = frozen;
                    descriptor.RetainedSupportClearance = descriptor.SupportClearance; descriptor.LowerSupport = null; descriptor.SupportClearance = 0;
                    descriptor.NeedsLandingProof = true; work++;
                }
                _upperSupports.Remove(cohort.Id);
            }
            return work;
        }
        public bool TryGetFallingCell(ulong id, int index, out FallingRun run, out int resolvedIndex)
            => TryGetFallingCell(id, index, out run, out resolvedIndex, out _);
        public bool TryGetFallingCell(ulong id, int index, out FallingRun run, out int resolvedIndex, out int work)
        {
            run = null; resolvedIndex = index; work = 0;
            if (id == 0 || index < 0 || index >= MaxCohortCells) return false;
            var address = new FallingCellAddress(id, index);
            // Every fracture destroys at least one original storage-cell boundary; no legitimate chain exceeds 16.
            for (int i = 0; i <= MaxCohortPieceCells; i++)
            {
                work++;
                if (!_targetAliases.TryGetValue(address, out var next))
                {
                    if (!Falling.TryGetValue(address.Run, out run) || address.Index >= run.Cells.Length || !run.Cells[address.Index].Occupied)
                    { run = null; return false; }
                    resolvedIndex = address.Index; return true;
                }
                address = next;
            }
            throw new InvalidOperationException("Invalid falling target alias chain.");
        }
        public int EstimateRemoveFallingCellWork(ulong id, int index)
        {
            if (!TryGetFallingCell(id, index, out var run, out _, out int resolve)) return resolve;
            return resolve + EstimateEditFallingWork(run.Id) + 2 * run.Cells.Length + 1 + (run.Cohort == null ? 0 : run.Cohort.Members.Count + EstimateRemoveCohortLinksWork(run.Cohort));
        }
        public bool RemoveFallingCell(ulong id, int index, out BlockCell removed) => RemoveFallingCell(id, index, out removed, out _);
        public bool RemoveFallingCell(ulong id, int index, out BlockCell removed, out int work)
        {
            removed = default;
            if (!TryGetFallingCell(id, index, out var found, out int resolvedIndex, out work)) return false;
            var run = EditFalling(found.Id, out int editWork); work += editWork;
            if (_ownedRunCells.Add(run.Id)) { run.Cells = (BlockCell[])run.Cells.Clone(); work += run.Cells.Length; }
            removed = run.Cells[resolvedIndex]; run.Cells[resolvedIndex] = default; AddCount(removed.Owner, -1);
            bool any = false; foreach (var cell in run.Cells) { work++; any |= cell.Occupied; }
            if (run.Cohort != null) { run.Cohort.NeedsRegroup = true; run.Cohort.MembershipRevision++; }
            if (!any)
            {
                Falling.Remove(run.Id);
                _newLegacyRunIds.Remove(run.Id); _preparedLegacyRunIds.Remove(run.Id);
                if (run.Cohort != null) { work += run.Cohort.Members.Count; run.Cohort.Members.Remove(run.Id); work += RemoveEmptyCohort(run.Cohort); }
            }
            work++; return true;
        }
        int RemoveEmptyCohort(FallingCohort cohort)
        {
            // A detaching descriptor can still own a not-yet-detached source tail.
            if (cohort.Members.Count != 0 || cohort.DetachCursor < cohort.TopExclusive) return 0;
            int work = RemoveCohortLinks(cohort); UnprepareCohort(cohort.Id); Cohorts.Remove(cohort.Id); _pendingCohortIds.Remove(cohort.Id); return work;
        }
        public int EstimateSettleWork(ulong id)
            => Falling.TryGetValue(id, out var run) ? EstimateEditFallingWork(id) + 2 * run.Cells.Length + 1 + (run.Cohort == null ? 0 : run.Cohort.Members.Count + EstimateRemoveCohortLinksWork(run.Cohort)) : 0;
        public bool Settle(ulong id, Vector3Int bottom, ISet<Vector3Int> changed) => Settle(id, bottom, changed, out _);
        public bool Settle(ulong id, Vector3Int bottom, ISet<Vector3Int> changed, out int work)
        {
            work = 0; if (!Falling.TryGetValue(id, out var original)) return false;
            if (original.Cohort != null && (original.Cohort.NeedsRegroup || !original.Cohort.LandingKnown
                || original.Cohort.Phase == FallingCohortPhase.Detaching || original.Cohort.Phase == FallingCohortPhase.AwaitActivation)) return false;
            for (int i = 0; i < original.Cells.Length; i++)
            {
                work++;
                if (original.Cells[i].Occupied && (!InBounds(bottom + Vector3Int.up * i) || TryGet(bottom + Vector3Int.up * i, out _))) return false;
            }
            var run = EditFalling(id, out int editWork); work += editWork;
            for (int i = 0; i < run.Cells.Length; i++)
            {
                work++; if (!run.Cells[i].Occupied) continue;
                AddCount(run.Cells[i].Owner, -1); var cell = bottom + Vector3Int.up * i; Set(cell, run.Cells[i]); changed.Add(ChunkAt(cell));
            }
            Falling.Remove(id);
            _newLegacyRunIds.Remove(id); _preparedLegacyRunIds.Remove(id);
            if (run.Cohort != null) { work += run.Cohort.Members.Count; run.Cohort.Phase = FallingCohortPhase.Settling; run.Cohort.MembershipRevision++; run.Cohort.Members.Remove(id); work += RemoveEmptyCohort(run.Cohort); }
            work++; return true;
        }
        public int EstimateReplaceCohortWork(ulong parentId, IReadOnlyList<FallingCohortSpan> spans)
        {
            if (!Cohorts.TryGetValue(parentId, out var cohort)) return 0;
            int work = EstimateBeginCohortReplacementWork(parentId, spans) + 3 + EstimateEditCohortWork(parentId)
                + 2 * cohort.Members.Count + EstimateRemoveCohortLinksWork(cohort) + 2 * FrozenCohortMotion.Depth(cohort);
            foreach (ulong member in cohort.Members) work = checked(work + Falling[member].Cells.Length + 1);
            foreach (var span in spans)
            {
                work = checked(work + 4);
                foreach (var piece in span.Pieces) work = checked(work + Falling[piece.RunId].Cells.Length + 6 * piece.Count + 5);
            }
            return work;
        }
        public int EstimateBeginCohortReplacementWork(ulong parentId, IReadOnlyList<FallingCohortSpan> spans)
        {
            if (spans == null || spans.Count > MaxCohortCells || !Cohorts.TryGetValue(parentId, out var cohort))
                throw new InvalidOperationException("Invalid fracture source or span count.");
            int pieces = 0; foreach (var span in spans) pieces = checked(pieces + span.Pieces.Count);
            if (pieces > MaxCohortCells) throw new InvalidOperationException("Invalid fracture piece count.");
            return 1 + 2 * cohort.Members.Count + spans.Count + pieces;
        }
        public FallingCohortReplacement BeginCohortReplacement(ulong parentId, IReadOnlyList<FallingCohortSpan> spans, out int work)
        {
            work = EstimateBeginCohortReplacementWork(parentId, spans);
            var parent = Cohorts[parentId]; var ids = parent.Members.ToArray(); var runs = new Dictionary<ulong, FallingRun>(ids.Length);
            foreach (ulong id in ids) runs.Add(id, Falling[id]);
            var frozenSpans = new FallingCohortSpan[spans.Count];
            for (int i = 0; i < spans.Count; i++)
            {
                var span = spans[i]; frozenSpans[i] = new FallingCohortSpan(span.Bottom, span.TopExclusive, span.DetachCursor, span.Pieces.ToArray());
            }
            return new FallingCohortReplacement(parent, ids, runs, frozenSpans);
        }
        public int EstimateCommitCohortReplacementWork(FallingCohortReplacement replacement)
        {
            if (replacement == null || !replacement.Ready || !replacement.IsValid(this)) throw new InvalidOperationException("Unprepared or obsolete fracture.");
            var parent = Cohorts[replacement.ParentId];
            int aliases = 0;
            foreach (var span in replacement.Prepared) foreach (var piece in span.Pieces)
                for (int i = piece.FirstIndex; i < piece.FirstIndex + piece.Count; i++)
                    if (_aliasOrigins.TryGetValue(new FallingCellAddress(piece.SourceId, i), out var origins)) aliases = checked(aliases + 2 * origins.Count);
            return checked(1 + EstimateEditCohortWork(parent.Id) + 4 * FrozenCohortMotion.Depth(parent)
                + replacement.Prepared.Count + 2 * replacement.PreparedPieceCount + 4 * replacement.AliasCellsUpperBound
                + aliases + replacement.SourceIds.Length + EstimateRemoveCohortLinksWork(parent));
        }
        void RedirectAliases(FallingCellAddress from, FallingCellAddress to)
        {
            if (!_aliasOrigins.TryGetValue(to, out var destination)) _aliasOrigins.Add(to, destination = new HashSet<FallingCellAddress>());
            if (_aliasOrigins.TryGetValue(from, out var origins))
            {
                foreach (var origin in origins) { _targetAliases[origin] = to; destination.Add(origin); }
                _aliasOrigins.Remove(from);
            }
            _targetAliases[from] = to; destination.Add(from);
        }
        public IReadOnlyList<FallingCohort> CommitCohortReplacement(FallingCohortReplacement replacement, double stageNow, out int work)
        {
            if (!double.IsFinite(stageNow)) throw new ArgumentOutOfRangeException(nameof(stageNow));
            work = EstimateCommitCohortReplacementWork(replacement);
            if (work > 4096) throw new InvalidOperationException("Fracture metadata commit exceeds the unchanged stage work limit.");
            if ((ulong)replacement.Prepared.Count >= ulong.MaxValue - NextCohortId || (ulong)replacement.PreparedPieceCount >= ulong.MaxValue - NextRunId)
                throw new InvalidOperationException("Fracture identities exhausted.");
            var parent = EditCohort(replacement.ParentId, out _); var frozenMotion = FrozenCohortMotion.Capture(parent);
            FrozenCohortMotion.Evaluate(frozenMotion, stageNow, out float offset, out float speed);
            var children = new List<FallingCohort>(replacement.Prepared.Count); var reused = new HashSet<ulong>();
            foreach (var prepared in replacement.Prepared)
            {
                var span = prepared.Span;
                var child = new FallingCohort { Id = NextCohortId++, Bottom = span.Bottom, TopExclusive = span.TopExclusive, DetachCursor = span.DetachCursor,
                    Phase = span.DetachCursor < span.TopExclusive ? FallingCohortPhase.Detaching : FallingCohortPhase.AwaitActivation,
                    StartedAt = stageNow, InitialOffset = offset, InitialSpeed = speed, RebasePending = true, RebaseMotion = frozenMotion,
                    RebaseSourceMoving = parent.Phase == FallingCohortPhase.Falling, RebaseSourceStartedAt = parent.StartedAt,
                    RebaseSourceOffset = parent.InitialOffset, RebaseSourceSpeed = parent.InitialSpeed,
                    RebaseSourceLandingKnown = parent.LandingKnown, RebaseSourceLandingOffset = parent.LandingOffset };
                Cohorts.Add(child.Id, child); _ownedCohorts.Add(child.Id); children.Add(child);
                if (child.Phase == FallingCohortPhase.AwaitActivation) _pendingCohortIds.Add(child.Id);
                foreach (var piece in prepared.Pieces)
                {
                    ulong id = reused.Add(piece.SourceId) ? piece.SourceId : NextRunId++;
                    Falling[id] = new FallingRun { Id = id, Bottom = piece.Bottom, Cells = piece.Cells, Cohort = child };
                    // Prepared arrays remain shared with transaction/journal builders; the first mutation must COW them.
                    _ownedRuns.Add(id); _ownedRunCells.Remove(id); child.Members.Add(id);
                    if (id != piece.SourceId) for (int i = piece.FirstIndex; i < piece.FirstIndex + piece.Count; i++)
                        RedirectAliases(new FallingCellAddress(piece.SourceId, i), new FallingCellAddress(id, i));
                }
            }
            foreach (ulong id in replacement.SourceIds) if (!reused.Contains(id)) Falling.Remove(id);
            RemoveCohortLinks(parent); UnprepareCohort(parent.Id); Cohorts.Remove(parent.Id); _ownedCohorts.Remove(parent.Id); _pendingCohortIds.Remove(parent.Id);
            replacement.MarkCommitted(); return children;
        }
        /// <summary>Compatibility wrapper for small direct callers/tests. Terrain uses the resumable builder APIs.</summary>
        public IReadOnlyList<FallingCohort> ReplaceCohort(ulong parentId, IReadOnlyList<FallingCohortSpan> spans, double stageNow, out int work)
        {
            if (!double.IsFinite(stageNow)) throw new ArgumentOutOfRangeException(nameof(stageNow));
            var builder = BeginCohortReplacement(parentId, spans, out work);
            builder.Step(this, int.MaxValue, out int preparation); work += preparation;
            if (!builder.Ready) throw new InvalidOperationException("Invalid or incomplete fracture spans.");
            var result = CommitCohortReplacement(builder, stageNow, out int commit); work += commit; return result;
        }
        public int EstimatePreparePendingCohortsWork()
        {
            int work = 0; foreach (ulong id in _pendingCohortIds) work = checked(work + 1 + EstimatePrepareCohortForPublicationWork(id));
            return work;
        }
        public int EstimatePrepareCohortForPublicationWork(ulong id)
        {
            if (!_pendingCohortIds.Contains(id) || _preparedCohortIds.Contains(id)) return 1;
            return checked(EstimateEditCohortWork(id) + EstimatePrivatePrepareCohortWork(id));
        }
        internal int EstimatePrivatePrepareCohortWork(ulong id)
        {
            var cohort = Cohorts[id];
            if (!cohort.RebasePending || _preparedMotionSamples.ContainsKey(cohort.RebaseMotion)) return 16;
            if (cohort.RebasePrefix == null) return 16 + Math.Max(1, cohort.RebaseMotion.Length);
            return 17 + (_preparedMotionSamples.ContainsKey(cohort.RebasePrefix) ? 0 : Math.Max(1, cohort.RebasePrefix.Length));
        }
        internal void PrivatePrepareCohort(ulong id, out int work)
        {
            DependentPreparationVersion++;
            var cohort = Cohorts[id]; work = EstimatePrivatePrepareCohortWork(id);
            if (!cohort.LandingKnown || cohort.NeedsLandingProof)
                throw new InvalidOperationException("First cohort activation requires its own completed landing cap.");
            if (cohort.RebasePending)
            {
                if (!_preparedMotionSamples.TryGetValue(cohort.RebaseMotion, out var sample))
                {
                    PreparedRebaseSample prefix = null;
                    if (cohort.RebasePrefix != null)
                    {
                        if (cohort.RebaseMotion.Length != cohort.RebasePrefix.Length + 1)
                            throw new InvalidOperationException("Invalid private publication prefix.");
                        if (!_preparedMotionSamples.TryGetValue(cohort.RebasePrefix, out prefix))
                        {
                            prefix = new PreparedRebaseSample(cohort.RebasePrefix); _preparedMotionSamples.Add(cohort.RebasePrefix, prefix);
                            _preparedMotionPublicationWork += prefix.LocalWork;
                        }
                        prefix.PreparedUsers++;
                    }
                    sample = new PreparedRebaseSample(cohort.RebaseMotion, prefix); _preparedMotionSamples.Add(cohort.RebaseMotion, sample);
                    _preparedMotionPublicationWork += sample.LocalWork;
                }
                sample.PreparedUsers++; cohort.PreparedRebase = sample;
            }
            _preparedCohortIds.Add(id);
        }
        void UnprepareCohort(ulong id)
        {
            if (!_preparedCohortIds.Remove(id) || !Cohorts.TryGetValue(id, out var cohort)) return;
            DependentPreparationVersion++;
            var sample = cohort.PreparedRebase; cohort.PreparedRebase = null;
            ReleasePreparedSample(sample);
        }
        // The finite owner is the only observer of its unpublished draft while
        // these registrations and shared prefixes are released one at a time.
        internal PreparedRebaseSample BeginBudgetedUnprepareCohort(ulong id)
        {
            if (!_preparedCohortIds.Remove(id) || !Cohorts.TryGetValue(id, out var cohort)) return null;
            DependentPreparationVersion++;
            var sample = cohort.PreparedRebase; cohort.PreparedRebase = null; return sample;
        }
        internal PreparedRebaseSample ReleasePreparedSampleStep(PreparedRebaseSample sample)
        {
            DependentPreparationVersion++;
            if (sample == null || --sample.PreparedUsers != 0) return null;
            _preparedMotionPublicationWork -= sample.LocalWork; _preparedMotionSamples.Remove(sample.Motion);
            return sample.Prefix;
        }
        void ReleasePreparedSample(PreparedRebaseSample sample)
        {
            if (sample == null || --sample.PreparedUsers != 0) return;
            _preparedMotionPublicationWork -= sample.LocalWork; _preparedMotionSamples.Remove(sample.Motion);
            ReleasePreparedSample(sample.Prefix);
        }
        public void PrepareCohortForPublication(ulong id, out int work)
        {
            work = 1;
            if (!_pendingCohortIds.Contains(id) || _preparedCohortIds.Contains(id)) return;
            var cohort = EditCohort(id, out int editWork, false); work = editWork + 16;
            if (cohort.Phase != FallingCohortPhase.AwaitActivation || cohort.NeedsRegroup
                || !cohort.LandingKnown || cohort.NeedsLandingProof) return;
            if (cohort.RebasePending && cohort.RebaseMotion.Length == 0)
                throw new InvalidOperationException("Pending fracture has no frozen publication trajectory.");
            PrivatePrepareCohort(id, out int preparation); work = editWork + preparation;
        }
        public void PreparePendingCohortsForPublication(out int work)
        {
            work = 0;
            foreach (ulong id in _pendingCohortIds)
            {
                PrepareCohortForPublication(id, out int preparation); work += 1 + preparation;
            }
        }
        public int EstimatePrepareLegacyRunEpochsWork()
        {
            int work = 0;
            foreach (ulong id in _newLegacyRunIds) work += 1 + (_preparedLegacyRunIds.Contains(id) ? 0 : EstimateEditFallingWork(id) + 2);
            return work;
        }
        public void PrepareLegacyRunEpochsForPublication(out int work)
        {
            work = 0;
            foreach (ulong id in _newLegacyRunIds)
            {
                work++; if (_preparedLegacyRunIds.Contains(id)) continue;
                var run = EditFalling(id, out int edit); work += edit + 2;
                if (run != null && run.Cohort == null) _preparedLegacyRunIds.Add(id);
            }
        }
        internal bool IsPreparedCohort(ulong id) => _preparedCohortIds.Contains(id);
        public void ActivateRebasePendingCohorts(double publishNow)
        {
            DependentPreparationVersion++;
            LastPublicationWork = 0; LastPublicationMotionSamples = 0;
            // Every distinct immutable source chain is sampled and checked before any cohort writes.
            // Crossing a newly proved floor cannot move the floor or lift a child to hide the failure.
            foreach (var sample in _preparedMotionSamples.Values)
            {
                LastPublicationWork += sample.EvaluateOnce(publishNow, out int samples); LastPublicationMotionSamples += samples;
            }
            foreach (ulong id in _preparedCohortIds)
            {
                var cohort = Cohorts[id];
                if (!cohort.LandingKnown || cohort.NeedsLandingProof)
                    throw new InvalidOperationException("First cohort activation requires its own completed landing cap.");
                if (cohort.RebasePending && (cohort.PreparedRebase == null || cohort.RebaseMotion.Length == 0))
                    throw new InvalidOperationException("Pending fracture has no prepared publication trajectory.");
                if (cohort.RebasePending && cohort.VerifiedPrivateLanding && cohort.PreparedRebase.Offset > cohort.VerifiedPrivateLandingOffset)
                    throw new InvalidOperationException("Actual publication trajectory has crossed the verified private landing.");
            }
            foreach (ulong id in _preparedCohortIds)
            {
                var cohort = Cohorts[id];
                if (cohort.RebasePending)
                {
                    var sample = cohort.PreparedRebase;
                    cohort.InitialOffset = sample.Offset; cohort.InitialSpeed = sample.Speed;
                    cohort.RebaseMotion = Array.Empty<FrozenCohortMotion>();
                    cohort.RebasePrefix = null;
                    cohort.RebasePending = false;
                    if (cohort.LandingKnown && cohort.LandingOffset < cohort.InitialOffset)
                    { cohort.LandingOffset = cohort.InitialOffset; cohort.NeedsLandingProof = true; }
                    cohort.RebaseSourceMoving = false; cohort.RebaseSourceLandingKnown = false; cohort.RebaseSourceStartedAt = 0;
                    cohort.RebaseSourceOffset = cohort.RebaseSourceSpeed = cohort.RebaseSourceLandingOffset = 0;
                    cohort.PreparedRebase = null;
                    cohort.VerifiedPrivateLanding = false; cohort.VerifiedPrivateLandingOffset = 0;
                }
                cohort.StartedAt = publishNow; cohort.Phase = FallingCohortPhase.Falling; _pendingCohortIds.Remove(id);
                LastPublicationWork += 16;
            }
            _preparedCohortIds.Clear();
            _preparedMotionSamples.Clear(); _preparedMotionPublicationWork = 0;
            foreach (ulong id in _preparedLegacyRunIds)
            {
                if (Falling.TryGetValue(id, out var run)) run.StartedAt = publishNow;
                _newLegacyRunIds.Remove(id);
                LastPublicationWork += 2;
            }
            _preparedLegacyRunIds.Clear();
        }
        public void Seal(uint revision, IEnumerable<Vector3Int> changed)
        {
            if (DependentEditIncomplete) throw new InvalidOperationException("An incomplete dependent draft cannot be published.");
            _preparedDependentEditOwner = null;
            Revision = revision;
            MutationGeneration++;
            foreach (var key in changed)
            {
                if (_chunks.TryGetValue(key, out var chunk))
                {
                    if (!_owned.Contains(key)) { _chunks[key] = chunk = chunk.Clone(); _owned.Add(key); }
                    chunk.Revision = revision;
                    if (chunk.Count == 0) { _chunks.Remove(key); _tombstones[key] = revision; } else _tombstones.Remove(key);
                }
                else _tombstones[key] = revision;
            }
            if (_privateOwnershipPrepared) _privateOwnershipPrepared = false;
            else { _owned.Clear(); _ownedRuns.IntersectWith(_preparedLegacyRunIds); _ownedRunCells.Clear(); _ownedCohorts.IntersectWith(_preparedCohortIds); }
        }
        static void WriteVector(BinaryWriter w, Vector3Int v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3Int ReadVector(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        static void WriteCell(BinaryWriter w, BlockCell c) { w.Write(c.Owner); w.Write(c.Flags); w.Write(c.Durability); }
        static BlockCell ReadCell(BinaryReader r)
        {
            var c = new BlockCell { Owner = r.ReadByte(), Flags = r.ReadByte(), Durability = r.ReadUInt16() };
            if (c.Owner > 2 || c.Flags > 1 || (c.Occupied && c.Durability == 0)) throw new InvalidDataException("Invalid block cell.");
            return c;
        }
        public void WriteFallingState(BinaryWriter w)
        {
            w.Write(NextRunId); w.Write(NextCohortId); w.Write(Cohorts.Count);
            foreach (var cohort in Cohorts.Values.OrderBy(c => c.Id))
            {
                w.Write(cohort.Id); w.Write(cohort.MembershipRevision); WriteVector(w, cohort.Bottom); w.Write(cohort.TopExclusive); w.Write(cohort.DetachCursor);
                w.Write((byte)cohort.Phase); w.Write(cohort.StartedAt); w.Write(cohort.InitialOffset); w.Write(cohort.InitialSpeed);
                w.Write(cohort.LandingKnown); w.Write(cohort.LandingOffset); w.Write(cohort.NeedsRegroup);
                w.Write(cohort.RebasePending); w.Write(cohort.RebaseSourceMoving); w.Write(cohort.RebaseSourceStartedAt);
                w.Write(cohort.RebaseSourceOffset); w.Write(cohort.RebaseSourceSpeed); w.Write(cohort.RebaseSourceLandingKnown); w.Write(cohort.RebaseSourceLandingOffset);
                w.Write(cohort.LowerSupport?.Id ?? 0); w.Write(cohort.SupportClearance);
                w.Write(cohort.NeedsLandingProof); w.Write(cohort.RetainedSupportClearance); WriteMotionChain(w, cohort.RetainedSupportMotion);
                w.Write(cohort.RebaseMotion.Length);
                foreach (var motion in cohort.RebaseMotion)
                { w.Write((byte)motion.Phase); w.Write(motion.StartedAt); w.Write(motion.InitialOffset); w.Write(motion.InitialSpeed);
                    w.Write(motion.LandingKnown); w.Write(motion.LandingOffset); w.Write(motion.SupportClearance); }
                w.Write(cohort.Members.Count); foreach (ulong id in cohort.Members) w.Write(id);
            }
            w.Write(Falling.Count);
            foreach (var run in Falling.Values.OrderBy(x => x.Id))
            {
                w.Write(run.Id); WriteVector(w, run.Bottom); w.Write(run.Cohort?.Id ?? 0);
                w.Write(run.StartedAt); w.Write(run.InitialOffset); w.Write(run.InitialSpeed); w.Write(run.LandingKnown); w.Write(run.LandingOffset);
                w.Write(run.Cells.Length); foreach (var cell in run.Cells) WriteCell(w, cell);
            }
            w.Write(_targetAliases.Count);
            foreach (var pair in _targetAliases.OrderBy(p => p.Key.Run).ThenBy(p => p.Key.Index))
            { w.Write(pair.Key.Run); w.Write(pair.Key.Index); w.Write(pair.Value.Run); w.Write(pair.Value.Index); }
        }
        void WriteRuns(BinaryWriter w) => WriteFallingState(w);
        static void WriteMotionChain(BinaryWriter w, FrozenCohortMotion[] chain)
        {
            w.Write(chain.Length);
            foreach (var motion in chain)
            { w.Write((byte)motion.Phase); w.Write(motion.StartedAt); w.Write(motion.InitialOffset); w.Write(motion.InitialSpeed);
                w.Write(motion.LandingKnown); w.Write(motion.LandingOffset); w.Write(motion.SupportClearance); }
        }
        static FrozenCohortMotion[] ReadMotionChain(BinaryReader r)
        {
            int count = r.ReadInt32(); if (count < 0 || count > MaxCohortCells) throw new InvalidDataException("Invalid frozen motion count.");
            var chain = new FrozenCohortMotion[count];
            for (int j = 0; j < count; j++)
            {
                var motion = new FrozenCohortMotion((FallingCohortPhase)r.ReadByte(), r.ReadDouble(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean(), r.ReadSingle(), r.ReadSingle());
                if ((byte)motion.Phase > (byte)FallingCohortPhase.Settling || !ValidMotion(motion.StartedAt, motion.InitialOffset, motion.InitialSpeed, motion.LandingKnown, motion.LandingOffset)
                    || !float.IsFinite(motion.SupportClearance) || motion.SupportClearance < 0 || j == 0 && motion.SupportClearance != 0)
                    throw new InvalidDataException("Invalid frozen motion.");
                chain[j] = motion;
            }
            return chain;
        }
        static bool ValidMotion(double epoch, float offset, float speed, bool known, float landing)
            => double.IsFinite(epoch) && float.IsFinite(offset) && float.IsFinite(speed) && float.IsFinite(landing)
                && offset >= 0 && speed >= 0 && landing >= 0 && (!known || landing >= offset);
        void ReadRuns(BinaryReader r)
        {
            Falling.Clear(); Cohorts.Clear(); _targetAliases.Clear(); _aliasOrigins.Clear(); _ownedRuns.Clear(); _ownedRunCells.Clear(); _ownedCohorts.Clear();
            _pendingCohortIds.Clear(); _preparedCohortIds.Clear(); _upperSupports.Clear();
            _preparedMotionSamples.Clear(); _preparedMotionPublicationWork = 0;
            _newLegacyRunIds.Clear(); _preparedLegacyRunIds.Clear();
            NextRunId = r.ReadUInt64(); NextCohortId = r.ReadUInt64(); int groups = r.ReadInt32();
            if (NextRunId == 0 || NextCohortId == 0 || groups < 0 || groups > 1000000) throw new InvalidDataException("Invalid cohort count or identity.");
            var supportIds = new Dictionary<ulong, ulong>();
            for (int i = 0; i < groups; i++)
            {
                var cohort = new FallingCohort { Id = r.ReadUInt64(), MembershipRevision = r.ReadUInt64(), Bottom = ReadVector(r), TopExclusive = r.ReadInt32(), DetachCursor = r.ReadInt32(),
                    Phase = (FallingCohortPhase)r.ReadByte(), StartedAt = r.ReadDouble(), InitialOffset = r.ReadSingle(), InitialSpeed = r.ReadSingle(),
                    LandingKnown = r.ReadBoolean(), LandingOffset = r.ReadSingle(), NeedsRegroup = r.ReadBoolean(),
                    RebasePending = r.ReadBoolean(), RebaseSourceMoving = r.ReadBoolean(), RebaseSourceStartedAt = r.ReadDouble(),
                    RebaseSourceOffset = r.ReadSingle(), RebaseSourceSpeed = r.ReadSingle(), RebaseSourceLandingKnown = r.ReadBoolean(), RebaseSourceLandingOffset = r.ReadSingle() };
                ulong supportId = r.ReadUInt64(); cohort.SupportClearance = r.ReadSingle();
                cohort.NeedsLandingProof = r.ReadBoolean(); cohort.RetainedSupportClearance = r.ReadSingle(); cohort.RetainedSupportMotion = ReadMotionChain(r);
                if (!float.IsFinite(cohort.RetainedSupportClearance) || cohort.RetainedSupportClearance < 0
                    || cohort.RetainedSupportMotion.Length == 0 && cohort.RetainedSupportClearance != 0
                    || cohort.RetainedSupportMotion.Length != 0 && (!cohort.NeedsLandingProof || supportId != 0))
                    throw new InvalidDataException("Invalid retained cohort support.");
                int motionCount = r.ReadInt32();
                if (motionCount < 0 || motionCount > MaxCohortCells || (cohort.RebasePending ? motionCount == 0 : motionCount != 0))
                    throw new InvalidDataException("Invalid pending rebase trajectory.");
                cohort.RebaseMotion = new FrozenCohortMotion[motionCount];
                for (int j = 0; j < motionCount; j++)
                {
                    var motion = new FrozenCohortMotion((FallingCohortPhase)r.ReadByte(), r.ReadDouble(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean(), r.ReadSingle(), r.ReadSingle());
                    if ((byte)motion.Phase > (byte)FallingCohortPhase.Settling || !ValidMotion(motion.StartedAt, motion.InitialOffset, motion.InitialSpeed, motion.LandingKnown, motion.LandingOffset)
                        || !float.IsFinite(motion.SupportClearance) || motion.SupportClearance < 0 || j == 0 && motion.SupportClearance != 0)
                        throw new InvalidDataException("Invalid frozen rebase motion.");
                    cohort.RebaseMotion[j] = motion;
                }
                if (cohort.Id == 0 || cohort.Id >= NextCohortId || (byte)cohort.Phase > (byte)FallingCohortPhase.Settling
                    || (long)cohort.TopExclusive - cohort.Bottom.y <= 0 || (long)cohort.TopExclusive - cohort.Bottom.y > MaxCohortCells
                    || cohort.DetachCursor < cohort.Bottom.y || cohort.DetachCursor > cohort.TopExclusive
                    || !InBounds(cohort.Bottom) || !InBounds(new Vector3Int(cohort.Bottom.x, cohort.TopExclusive - 1, cohort.Bottom.z))
                    || !ValidMotion(cohort.StartedAt, cohort.InitialOffset, cohort.InitialSpeed, cohort.LandingKnown, cohort.LandingOffset)
                    || !ValidMotion(cohort.RebaseSourceStartedAt, cohort.RebaseSourceOffset, cohort.RebaseSourceSpeed, cohort.RebaseSourceLandingKnown, cohort.RebaseSourceLandingOffset)
                    || !float.IsFinite(cohort.SupportClearance) || cohort.SupportClearance < 0 || supportId == cohort.Id
                    || (cohort.Phase == FallingCohortPhase.Detaching ? cohort.DetachCursor == cohort.TopExclusive : cohort.DetachCursor != cohort.TopExclusive)
                    || (cohort.RebasePending && cohort.Phase != FallingCohortPhase.Detaching && cohort.Phase != FallingCohortPhase.AwaitActivation))
                    throw new InvalidDataException("Invalid falling cohort descriptor.");
                int members = r.ReadInt32();
                if (members < 0 || members > MaxCohortCells || members == 0 && cohort.Phase != FallingCohortPhase.Detaching)
                    throw new InvalidDataException("Invalid cohort member count.");
                var seenMembers = new HashSet<ulong>();
                for (int j = 0; j < members; j++)
                { ulong member = r.ReadUInt64(); if (member == 0 || member >= NextRunId || !seenMembers.Add(member)) throw new InvalidDataException("Duplicate cohort member."); cohort.Members.Add(member); }
                if (!Cohorts.TryAdd(cohort.Id, cohort)) throw new InvalidDataException("Duplicate falling cohort.");
                supportIds.Add(cohort.Id, supportId);
                if (cohort.Phase == FallingCohortPhase.AwaitActivation) _pendingCohortIds.Add(cohort.Id);
            }
            foreach (var pair in supportIds)
            {
                if (pair.Value == 0) { if (Cohorts[pair.Key].SupportClearance != 0) throw new InvalidDataException("Orphan support clearance."); continue; }
                if (!Cohorts.TryGetValue(pair.Value, out var lower) || !ValidSupport(Cohorts[pair.Key], lower)) throw new InvalidDataException("Orphan or unordered cohort support.");
                Cohorts[pair.Key].LowerSupport = lower; AddUpperSupport(lower.Id, pair.Key);
            }
            foreach (var cohort in Cohorts.Values)
            {
                try { FrozenCohortMotion.Depth(cohort); }
                catch (InvalidOperationException exception)
                { throw new InvalidDataException("Aggregate falling support trajectory exceeds the protocol bound.", exception); }
            }
            int count = r.ReadInt32();
            if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid falling run count.");
            var occupiedSources = new HashSet<Vector3Int>();
            for (int i = 0; i < count; i++)
            {
                ulong id = r.ReadUInt64(); var bottom = ReadVector(r); ulong groupId = r.ReadUInt64();
                var run = new FallingRun { Id = id, Bottom = bottom, StartedAt = r.ReadDouble(), InitialOffset = r.ReadSingle(), InitialSpeed = r.ReadSingle(),
                    LandingKnown = r.ReadBoolean(), LandingOffset = r.ReadSingle() };
                int cells = r.ReadInt32();
                if (cells <= 0 || cells > MaxCohortCells || (groupId != 0 && cells > MaxCohortPieceCells)
                    || !ValidMotion(run.StartedAt, run.InitialOffset, run.InitialSpeed, run.LandingKnown, run.LandingOffset)) throw new InvalidDataException("Invalid falling run.");
                if (groupId != 0)
                {
                    if (!Cohorts.TryGetValue(groupId, out var cohort) || !cohort.Members.Contains(id)
                        || run.StartedAt != cohort.StartedAt || run.InitialOffset != cohort.InitialOffset || run.InitialSpeed != cohort.InitialSpeed
                        || run.LandingKnown != cohort.LandingKnown || run.LandingOffset != cohort.LandingOffset)
                        throw new InvalidDataException("Orphan or inconsistent cohort run projection.");
                    run.Cohort = cohort;
                }
                run.Cells = new BlockCell[cells]; bool occupied = false;
                for (int j = 0; j < cells; j++)
                {
                    var cell = run.Cells[j] = ReadCell(r); var source = run.Bottom + Vector3Int.up * j;
                    if (cell.Occupied)
                    {
                        if (cell.Anchored || !InBounds(source) || !occupiedSources.Add(source)
                            || run.Cohort != null && (source.x != run.Cohort.Bottom.x || source.z != run.Cohort.Bottom.z
                                || source.y < run.Cohort.Bottom.y || source.y >= run.Cohort.DetachCursor))
                            throw new InvalidDataException("Invalid falling source membership.");
                        occupied = true;
                    }
                    AddCount(cell.Owner, 1);
                }
                if (!occupied || run.Id == 0 || run.Id >= NextRunId || !Falling.TryAdd(run.Id, run)) throw new InvalidDataException("Duplicate or empty falling run.");
            }
            foreach (var cohort in Cohorts.Values)
            {
                int occupied = 0;
                foreach (ulong member in cohort.Members)
                {
                    if (!Falling.TryGetValue(member, out var run) || run.Cohort != cohort) throw new InvalidDataException("Orphan cohort member reference.");
                    foreach (var cell in run.Cells) if (cell.Occupied) occupied++;
                }
                if (!cohort.NeedsRegroup && cohort.Phase != FallingCohortPhase.Settling && occupied != cohort.DetachCursor - cohort.Bottom.y)
                    throw new InvalidDataException("Incomplete intact cohort membership.");
            }
            int aliases = r.ReadInt32();
            if (aliases < 0 || aliases > 16000000 || (long)aliases * 24 > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid falling alias count.");
            for (int i = 0; i < aliases; i++)
            {
                var from = new FallingCellAddress(r.ReadUInt64(), r.ReadInt32()); var to = new FallingCellAddress(r.ReadUInt64(), r.ReadInt32());
                if (from.Run == 0 || from.Run >= NextRunId || to.Run <= from.Run || to.Run >= NextRunId
                    || from.Index < 0 || from.Index >= MaxCohortPieceCells || to.Index != from.Index
                    || !_targetAliases.TryAdd(from, to)) throw new InvalidDataException("Invalid or duplicate falling target alias.");
            }
            foreach (var pair in _targetAliases)
            {
                if (Falling.TryGetValue(pair.Key.Run, out var originRun) && pair.Key.Index < originRun.Cells.Length && originRun.Cells[pair.Key.Index].Occupied)
                    throw new InvalidDataException("Falling alias shadows an occupied source cell.");
                var address = pair.Key; int depth = 0;
                while (_targetAliases.TryGetValue(address, out var next))
                { if (++depth > MaxCohortPieceCells) throw new InvalidDataException("Falling target alias chain too long."); address = next; }
                // Historical endpoints may have settled/died; they intentionally stay until an explicit reservation proof permits cleanup.
                if (Falling.TryGetValue(address.Run, out var target) && address.Index >= target.Cells.Length) throw new InvalidDataException("Invalid falling alias endpoint.");
                if (!_aliasOrigins.TryGetValue(address, out var origins)) _aliasOrigins.Add(address, origins = new HashSet<FallingCellAddress>());
                origins.Add(pair.Key);
            }
        }
        void WriteChunk(BinaryWriter w, Vector3Int key)
        {
            WriteVector(w, key); bool exists = _chunks.TryGetValue(key, out var chunk);
            // A private unsealed removal keeps a real empty chunk until Seal writes its tombstone.
            // Preserve malformed Count declarations: occupied cells must never be hidden by Count == 0.
            if (exists && chunk.Count == 0)
            {
                exists = false;
                for (int i = 0; i < CellsPerChunk; i++)
                    if (chunk.Cells[i].Occupied) { exists = true; break; }
            }
            w.Write(exists); w.Write(ChunkRevision(key));
            if (!exists) return;
            w.Write(chunk.Count);
            for (int i = 0; i < CellsPerChunk; i++) if (chunk.Cells[i].Occupied) { w.Write((ushort)i); WriteCell(w, chunk.Cells[i]); }
        }
        Vector3Int ReadChunk(BinaryReader r)
        {
            var key = ReadVector(r); bool exists = r.ReadBoolean(); uint revision = r.ReadUInt32();
            if (_chunks.TryGetValue(key, out var prior)) foreach (var c in prior.Cells) AddCount(c.Owner, -1);
            _chunks.Remove(key);
            if (!exists) { _tombstones[key] = revision; return key; }
            int count = r.ReadInt32(); if (count < 1 || count > CellsPerChunk) throw new InvalidDataException("Invalid block chunk size.");
            var chunk = new BlockChunk { Count = count, Revision = revision };
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadUInt16(); var cell = ReadCell(r);
                if (index >= CellsPerChunk || !cell.Occupied || chunk.Cells[index].Occupied || !InBounds(CellFromIndex(key, index))) throw new InvalidDataException("Invalid block location.");
                chunk.Cells[index] = cell; chunk.SetOccupied(index, true); AddCount(cell.Owner, 1);
            }
            _chunks.Add(key, chunk); _tombstones.Remove(key); return key;
        }
        public readonly struct EncodingRequest
        {
            public readonly bool Full;
            public readonly uint Since;
            public EncodingRequest(bool full, uint since = 0) { Full = full; Since = full ? 0 : since; }
        }
        // Synchronous call-local sharing only. No serialized state survives in this World.
        // Request duplicates share immutable output bytes; each sender owns its own ACK state.
        public byte[][] EncodeBatch(IReadOnlyList<EncodingRequest> requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            if (requests.Count == 0) return Array.Empty<byte[]>();
            var unique = new Dictionary<(bool Full, uint Since), byte[]>();
            bool full = false; uint minimum = Revision;
            foreach (var request in requests)
            {
                if (!request.Full && request.Since > Revision) throw new ArgumentOutOfRangeException(nameof(requests));
                unique.TryAdd((request.Full, request.Since), null);
                full |= request.Full; if (!request.Full) minimum = Math.Min(minimum, request.Since);
            }
            var result = new byte[requests.Count][];
            if (unique.Count == 1)
            {
                var request = requests[0]; byte[] bytes = request.Full ? EncodeSnapshot() : EncodeDelta(request.Since);
                for (int i = 0; i < result.Length; i++) result[i] = bytes;
                return result;
            }
            // A revived unsealed chunk can coexist with its old tombstone. The larger
            // revision selects delta membership; WriteChunk still emits the live revision.
            var keys = _chunks.Keys.Concat(_tombstones.Keys).Distinct()
                .Where(key => full || (_chunks.TryGetValue(key, out var chunk) && chunk.Revision > minimum)
                    || (_tombstones.TryGetValue(key, out uint tombstone) && tombstone > minimum))
                .OrderBy(key => key.x).ThenBy(key => key.y).ThenBy(key => key.z).ToArray();
            var slices = new (int Offset, int Length, uint Revision)[keys.Length];
            using var shared = new MemoryStream(); using var sharedWriter = new BinaryWriter(shared);
            for (int i = 0; i < keys.Length; i++)
            {
                int offset = checked((int)shared.Position); WriteChunk(sharedWriter, keys[i]);
                uint revision = _chunks.TryGetValue(keys[i], out var chunk) ? chunk.Revision : 0;
                if (_tombstones.TryGetValue(keys[i], out uint tombstone)) revision = Math.Max(revision, tombstone);
                slices[i] = (offset, checked((int)shared.Position - offset), revision);
            }
            int runsOffset = checked((int)shared.Position); WriteRuns(sharedWriter);
            int runsLength = checked((int)shared.Position - runsOffset); byte[] sharedBytes = shared.GetBuffer();
            for (int i = 0; i < requests.Count; i++)
            {
                var request = requests[i]; var identity = (request.Full, request.Since);
                if (!unique.TryGetValue(identity, out byte[] bytes) || bytes == null)
                {
                    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                    if (request.Full)
                    {
                        writer.Write(Magic); writer.Write(Round); writer.Write(Revision);
                        writer.Write(Dimensions.x); writer.Write(Dimensions.y); writer.Write(Bottom);
                    }
                    else { writer.Write(DeltaMagic); writer.Write(Round); writer.Write(request.Since); writer.Write(Revision); }
                    int count = 0; foreach (var slice in slices) if (request.Full || slice.Revision > request.Since) count++;
                    writer.Write(count);
                    foreach (var slice in slices)
                        if (request.Full || slice.Revision > request.Since) writer.Write(sharedBytes, slice.Offset, slice.Length);
                    writer.Write(sharedBytes, runsOffset, runsLength); bytes = stream.ToArray(); unique[identity] = bytes;
                }
                result[i] = bytes;
            }
            return result;
        }
        public byte[] EncodeSnapshot()
        {
            using var s = new MemoryStream(); using var w = new BinaryWriter(s);
            w.Write(Magic); w.Write(Round); w.Write(Revision); w.Write(Dimensions.x); w.Write(Dimensions.y); w.Write(Bottom);
            var keys = _chunks.Keys.Concat(_tombstones.Keys).Distinct().OrderBy(k => k.x).ThenBy(k => k.y).ThenBy(k => k.z).ToArray();
            w.Write(keys.Length); foreach (var key in keys) WriteChunk(w, key);
            WriteRuns(w); return s.ToArray();
        }
        public static BlockWorld DecodeSnapshot(byte[] bytes)
        {
            using var s = new MemoryStream(bytes, false); using var r = new BinaryReader(s);
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("Unknown block protocol.");
            uint round = r.ReadUInt32(), revision = r.ReadUInt32(); var dimensions = new Vector2(r.ReadSingle(), r.ReadSingle()); float bottom = r.ReadSingle();
            if (!float.IsFinite(dimensions.x) || !float.IsFinite(dimensions.y) || dimensions.x <= 0 || dimensions.y <= 0 || dimensions.x > 1000 || dimensions.y > 1000 || !float.IsFinite(bottom) || bottom >= Top || bottom < -8) throw new InvalidDataException("Invalid block bounds.");
            var world = new BlockWorld(dimensions, round, bottom) { Revision = revision };
            int chunks = r.ReadInt32(); if (chunks < 0 || chunks > 1000000) throw new InvalidDataException("Invalid chunk count.");
            var keys = new HashSet<Vector3Int>();
            for (int i = 0; i < chunks; i++) if (!keys.Add(world.ReadChunk(r))) throw new InvalidDataException("Duplicate snapshot chunk."); world.ReadRuns(r);
            if (s.Position != s.Length) throw new InvalidDataException("Trailing block data."); return world;
        }
        public byte[] EncodeDelta(uint since)
        {
            if (since > Revision) throw new ArgumentOutOfRangeException(nameof(since));
            using var s = new MemoryStream(); using var w = new BinaryWriter(s);
            w.Write(DeltaMagic); w.Write(Round); w.Write(since); w.Write(Revision);
            var keys = _chunks.Where(p => p.Value.Revision > since).Select(p => p.Key).Concat(_tombstones.Where(p => p.Value > since).Select(p => p.Key)).Distinct().OrderBy(k => k.x).ThenBy(k => k.y).ThenBy(k => k.z).ToArray();
            w.Write(keys.Length); foreach (var key in keys) WriteChunk(w, key); WriteRuns(w); return s.ToArray();
        }
        public BlockWorld WithDelta(byte[] bytes) => WithDelta(bytes, out _);
        public BlockWorld WithDelta(byte[] bytes, out HashSet<Vector3Int> changed)
        {
            using var s = new MemoryStream(bytes, false); using var r = new BinaryReader(s);
            if (r.ReadUInt32() != DeltaMagic || r.ReadUInt32() != Round || r.ReadUInt32() != Revision) throw new InvalidDataException("Block delta baseline mismatch.");
            uint revision = r.ReadUInt32(); if (revision < Revision) throw new InvalidDataException("Block revision regressed.");
            var world = Clone(); foreach (var run in world.Falling.Values) foreach (var c in run.Cells) world.AddCount(c.Owner, -1);
            int chunks = r.ReadInt32(); if (chunks < 0 || chunks > 1000000) throw new InvalidDataException("Invalid delta chunk count.");
            var keys = new HashSet<Vector3Int>();
            // Includes removed chunks and neighbour revisions stamped by the host.
            for (int i = 0; i < chunks; i++) if (!keys.Add(world.ReadChunk(r))) throw new InvalidDataException("Duplicate delta chunk."); world.ReadRuns(r); world.Revision = revision;
            if (s.Position != s.Length) throw new InvalidDataException("Trailing delta data.");
            changed = keys; return world;
        }
    }
}
