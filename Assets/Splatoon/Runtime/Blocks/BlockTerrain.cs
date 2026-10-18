using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace Splatoon.Blocks
{
    [DefaultExecutionOrder(-10000)]
    public sealed partial class BlockTerrain : MonoBehaviour
    {
        public const int TerrainLayer = 29;
        public static BlockTerrain Current { get; private set; }
        public BlockWorld World { get; private set; }
        public bool Initialized { get; private set; }
        public uint Revision => World?.Revision ?? 0;
        public uint Round => World?.Round ?? 0;
        public double SimulationTime => _now;
        public int PinkCount => (World?.PinkCount ?? 0) + (Dynamic?.PinkCount ?? 0);
        public int BlueCount => (World?.BlueCount ?? 0) + (Dynamic?.BlueCount ?? 0);
        public int PendingTransactions => _commands.Count + (_pending != null || _fairSeed != null || HasPrivatePreparation || HasHeadPreparation || HasBuildOccupancyPreparation || HasHistoricalRecovery || HasDependentEditPreparation ? 1 : 0) + (_remoteQueued != null ? 1 : 0);
        public event Action<BlockTerrainCommit> PublishedCommit;
        public event Action<BlockMutationResult, double> MutationCompleted;
        public static readonly ProfilerMarker TickCpu = new("Splatoon.Blocks.Tick");
        public static readonly ProfilerMarker PublishCpu = new("Splatoon.Blocks.Publish");
        public static readonly ProfilerMarker UploadCpu = new("Splatoon.Blocks.Upload");
        public static readonly ProfilerMarker FallingRenderCpu = new("Splatoon.Blocks.FallingRender");
        public int FallingCount { get { int count = 0; if (World != null) foreach (var run in World.Falling.Values) foreach (var cell in run.Cells) if (cell.Occupied) count++; return count; } }
        public int FailedTransactions { get; private set; }
        public int PublishedChunks { get; private set; }
        public long AuthorityStageCount { get; private set; }
        public long CommandWorkChecks { get; private set; }
        public long GravityWorkChecks { get; private set; }
        public long FallingSelectionChecks { get; private set; }
        public long ReservationCommandChecks { get; private set; }
        public long CommandProgressStages { get; private set; }
        public long GravityProgressStages { get; private set; }
        public long CommandsQueued { get; private set; }
        public long CommandsCompleted { get; private set; }
        public long BuildCandidatesVisited { get; private set; }
        public long BuildOutsideBounds { get; private set; }
        public long BuildAlreadyOccupied { get; private set; }
        public long BuildProtectedByZone { get; private set; }
        public long BuildOccupiedByPlayer { get; private set; }
        public long BuildStaticOverlaps { get; private set; }
        public long BuildStaticCacheHits { get; private set; }
        public long BuildOverlapBufferFull { get; private set; }
        public long BuildDeferredByChunkBudget { get; private set; }
        public long BuildsFullyProtected { get; private set; }
        public long BuildHorizontalProofAttempts { get; private set; }
        public long BuildHorizontalProtectedLayers { get; private set; }
        public long BuildHorizontalProtectedTails { get; private set; }
        public long BuildOccupiedMaskQueries { get; private set; }
        public long BuildOccupiedCandidatesSkipped { get; private set; }
        [Serializable] public struct BuildWorkDiagnostics
        {
            public long queued, completed, visited, outsideBounds, alreadyOccupied;
            public long protectedByZone, occupiedByPlayer, staticOverlaps, staticCacheHits;
            public long overlapBufferFull, deferredByChunkBudget, fullyProtectedCommands;
            public long horizontalProofAttempts, horizontalProtectedLayers, horizontalProtectedTails;
            public long occupiedMaskQueries, occupiedCandidatesSkipped;
        }
        public BuildWorkDiagnostics InspectBuildWork() => new()
        {
            queued = CommandsQueued, completed = CommandsCompleted, visited = BuildCandidatesVisited,
            outsideBounds = BuildOutsideBounds, alreadyOccupied = BuildAlreadyOccupied,
            protectedByZone = BuildProtectedByZone, occupiedByPlayer = BuildOccupiedByPlayer,
            staticOverlaps = BuildStaticOverlaps, staticCacheHits = BuildStaticCacheHits,
            overlapBufferFull = BuildOverlapBufferFull, deferredByChunkBudget = BuildDeferredByChunkBudget,
            fullyProtectedCommands = BuildsFullyProtected,
            horizontalProofAttempts = BuildHorizontalProofAttempts,
            horizontalProtectedLayers = BuildHorizontalProtectedLayers,
            horizontalProtectedTails = BuildHorizontalProtectedTails,
            occupiedMaskQueries = BuildOccupiedMaskQueries, occupiedCandidatesSkipped = BuildOccupiedCandidatesSkipped
        };
        [Serializable] public struct CommandHeadDiagnostic
        {
            public bool present, build, ordinaryMainAttack, hasCandidate;
            public byte team;
            public Vector3 point, normal;
            public Vector3Int candidate;
            public float radius, friendlyRadius;
            public int count, added, removedEnemy, removedFriendly, maxEnemy, maxFriendly;
            public uint sourceRevision;
            public double ageMs;
            public string preparation;
        }
        public CommandHeadDiagnostic InspectCommandHead()
        {
            if (_commands.Count == 0) return default;
            var command = _commands.Peek();
            return new CommandHeadDiagnostic
            {
                present = true, build = command.Build, ordinaryMainAttack = command.OrdinaryMainAttack,
                team = command.Team, point = command.Point, normal = command.Normal,
                radius = command.Radius, friendlyRadius = command.FriendlyRadius,
                count = command.Count, added = command.Total.Added, removedEnemy = command.Total.RemovedEnemy,
                removedFriendly = command.Total.RemovedFriendly, maxEnemy = command.MaxEnemy, maxFriendly = command.MaxFriendly,
                candidate = command.LastCandidate, hasCandidate = command.HasCandidate,
                sourceRevision = command.Source?.Revision ?? 0, ageMs = Math.Max(0, _now - command.CreatedAt) * 1000,
                preparation = command.Build ? command.Cells == null ? "unstarted" : "building"
                    : command.Targets != null ? "deleting" : command.Sorting != null ? "sorting" : "capturing"
            };
        }
        public double LastCommandStageMilliseconds { get; private set; }
        public double LastGravityStageMilliseconds { get; private set; }
        public int UnstableColumns => _gravityColumns.Count;
        public bool LossOfSupportReady => Initialized && _gravityColumns.Count == 0
            && (_pending == null || _pending.Next.NextRunId == World.NextRunId);
        public double OldestPendingSeconds
        {
            get
            {
                double oldest = _pending != null ? _pending.CreatedAt : _now;
                if (_commands.Count > 0) oldest = Math.Min(oldest, _commands.Peek().CreatedAt);
                return Math.Max(0, _now - oldest);
            }
        }
        const int MaxChangedChunks = 8, MaxChecks = 4096;
        const MeshColliderCookingOptions Cooking = MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.UseFastMidphase | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices;
        static readonly Vector3Int[] Neighbours = { Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, new(0, 0, 1), new(0, 0, -1) };
        static readonly SemaphoreSlim Workers = new(2);
        static readonly BlockWorkerQueue WorkerAdmissions = new(Workers);
        readonly Dictionary<Vector3Int, View> _views = new();
        readonly List<Bounds> _changingSupportBounds = new(16);
        readonly Dictionary<Collider, Vector3Int> _colliderChunks = new();
        readonly CommandDeque _commands = new();
        readonly HashSet<Command> _destructionReservations = new();
        readonly Dictionary<Vector2Int, bool> _reservedColumns = new();
        readonly Dictionary<Vector2Int, HashSet<Command>> _reservationBuckets = new();
        readonly Dictionary<Command, ReservationBucketRange> _reservationRanges = new();
        readonly HashSet<Command> _reservationFallback = new();
        readonly Queue<Retired> _retired = new();
        readonly HashSet<Collider> _staticColliders = new();
        readonly HashSet<Vector2Int> _gravityColumns = new();
        readonly Dictionary<Vector2Int, int> _gravityCursor = new();
        readonly List<Vector2Int> _gravityScratch = new(1024);
        readonly Dictionary<Vector2Int, uint> _landingGeneration = new();
        readonly Dictionary<ulong, uint> _landingChecked = new();
        readonly List<FallingWork> _readyRuns = new(64);
        readonly Dictionary<Vector2Int, (ulong id, int lowest)> _fallingColumnHeads = new();
        readonly Collider[] _overlaps = new Collider[64];
        readonly RaycastHit[] _rayHits = new RaycastHit[64];
        readonly Matrix4x4[] _fallMatrices = new Matrix4x4[1023];
        readonly Vector4[] _fallColors = new Vector4[1023];
        MaterialPropertyBlock _fallProperties;
        Pending _pending;
        RemoteInstall _remoteQueued;
        PrototypeArena _arena;
        Material _material;
        Mesh _fallMesh;
        Collider _fallProxy;
        uint _epoch;
        bool _accepting = true, _authority, _destroying;
        double _now;
        int _budgetFrame = -1, _publishedThisFrame;
        bool _gravityFirst, _settleFirst;
        bool _cohortAuthorityInitialized;

        sealed class View
        {
            public GameObject Object;
            public Mesh RenderMesh, CollisionMesh;
            public MeshCollider Collider;
        }
        sealed class Prepared
        {
            public Vector3Int Key;
            public BlockMeshData Render, Collision;
            public Mesh RenderMesh, CollisionMesh;
            public Task Bake;
            // Main owns upload stamps. Bake stamps are read only after Bake completion.
            public long UploadStartStamp, UploadEndStamp, BakeStartStamp, BakeEndStamp;
            public PendingPieceWorkerDiagnostic Worker;
            public View View;
        }
        public enum PendingWallPhase { Untracked, Generation, Upload, Bake, ReadyToPublish }
        public enum PendingWallOutcome { Published, Failed, Aborted }
        [Serializable] public struct PendingWorkDiagnostics
        {
            public bool tracking;
            public long created, published, failed, aborted, observerFailures;
            public long generateNotReady, uploadRemaining, bakeNotReady, readyAwaitTick, frameChunkBudgetBlocked;
        }
        [Serializable] public struct PendingHeadDiagnostic
        {
            public bool present, tracked, remote, terminalObserved;
            public long id, createdWallTimestamp;
            public PendingWallPhase phase;
            public PendingWallOutcome terminalOutcome;
            public double ageMs, phaseAgeMs;
        }
        // Overlap intervals are wall windows, not thread CPU or extra additive phases.
        public readonly struct PendingPipelineDiagnostic
        {
            public readonly bool Available, UploadedBeforeGroupCompletion;
            public readonly double FirstUploadFromCreatedMs, UploadPieceActiveElapsedMs, BakeActiveElapsedMs;
            public readonly double UploadGenerationOverlapWindowMs, BakeGenerationOverlapWindowMs;
            public PendingPipelineDiagnostic(bool early, double first, double upload, double bake, double uploadOverlap, double bakeOverlap)
            {
                Available = true; UploadedBeforeGroupCompletion = early;
                FirstUploadFromCreatedMs = first; UploadPieceActiveElapsedMs = upload; BakeActiveElapsedMs = bake;
                UploadGenerationOverlapWindowMs = uploadOverlap; BakeGenerationOverlapWindowMs = bakeOverlap;
            }
        }
        public readonly struct PendingWallDiagnostic
        {
            // Published ends after world/collider commit, before recovery/callbacks.
            // Failed/Aborted end at rejection/abort entry, before cleanup/callbacks.
            public readonly long Id, CreatedWallTimestamp;
            public readonly bool Remote, GenerationObserved, UploadCompleted, BakeObserved, PublishEntered;
            public readonly PendingWallOutcome Outcome;
            public readonly PendingAbortReason AbortReason;
            public readonly int Pieces;
            public readonly double GenerationObservedMs, UploadMs, BakeObservedTailMs, ReadyWaitMs, PublicationMs, TotalMs;
            public readonly PendingGenerationWorkerDiagnostic Worker;
            public readonly PendingPipelineDiagnostic Pipeline;
            public PendingWallDiagnostic(PendingWallOutcome outcome, long id, long created, bool remote, int pieces,
                bool generation, bool upload, bool bake, bool publishEntered, double generationMs, double uploadMs,
                double bakeMs, double readyMs, double publicationMs, double totalMs, PendingGenerationWorkerDiagnostic worker = default, PendingPipelineDiagnostic pipeline = default, PendingAbortReason abortReason = PendingAbortReason.Unclassified)
            {
                AbortReason = abortReason;
                Outcome = outcome; Id = id; CreatedWallTimestamp = created; Remote = remote; Pieces = pieces;
                GenerationObserved = generation; UploadCompleted = upload; BakeObserved = bake; PublishEntered = publishEntered;
                GenerationObservedMs = generationMs; UploadMs = uploadMs; BakeObservedTailMs = bakeMs;
                ReadyWaitMs = readyMs; PublicationMs = publicationMs; TotalMs = totalMs; Worker = worker; Pipeline = pipeline;
            }
        }
        // Worker observations are scalar stamps/lengths recorded once per piece.
        // Elapsed spans include OS/GC scheduling; they are not thread CPU time.
        public struct PendingPieceWorkerDiagnostic
        {
            public Vector3Int Key;
            public bool Rendered;
            public long SemaphoreAwaitStartStamp, SemaphoreAcquiredStamp, BuildBodyStartStamp;
            public long RenderStartStamp, RenderEndStamp, CollisionStartStamp, CollisionEndStamp, BuildBodyEndStamp;
            public int RenderVertices, RenderNormals, RenderColors, RenderIndices;
            public int CollisionVertices, CollisionNormals, CollisionColors, CollisionIndices;
            public double SemaphoreAwaitElapsedMs => PendingWallMilliseconds(SemaphoreAwaitStartStamp, SemaphoreAcquiredStamp);
            public double RenderBuildElapsedMs => Rendered ? PendingWallMilliseconds(RenderStartStamp, RenderEndStamp) : 0;
            public double CollisionBuildElapsedMs => PendingWallMilliseconds(CollisionStartStamp, CollisionEndStamp);
            public long OutputArrayLogicalBytes => 12L * (RenderVertices + (long)RenderNormals + CollisionVertices + (long)CollisionNormals)
                + 4L * (RenderColors + (long)RenderIndices + CollisionColors + (long)CollisionIndices);
        }
        public readonly struct PendingGenerationWorkerDiagnostic
        {
            public readonly bool Available;
            public readonly PendingPieceWorkerDiagnostic[] Pieces;
            public readonly int CriticalPieceIndex;
            public readonly long GroupEnteredStamp, GroupFinishedStamp;
            // The eight critical-path spans partition Created -> first main-thread
            // generation observation. Initial includes outer dispatch/enumeration.
            public readonly double InitialToCriticalAwaitMs, CriticalSemaphoreAwaitMs, CriticalInnerDispatchMs;
            public readonly double CriticalRenderBuildMs, CriticalCollisionBuildMs, CriticalBodyOtherMs;
            public readonly double GroupJoinTailMs, CompletionToMainObservedMs, OuterDispatchMs;
            public PendingGenerationWorkerDiagnostic(long created, long observed, long entered, long finished,
                PendingPieceWorkerDiagnostic[] pieces, int critical)
            {
                Available = true; Pieces = pieces; CriticalPieceIndex = critical;
                GroupEnteredStamp = entered; GroupFinishedStamp = finished;
                OuterDispatchMs = PendingWallMilliseconds(created, entered);
                CompletionToMainObservedMs = PendingWallMilliseconds(finished, observed);
                if (critical < 0)
                {
                    // A genuine empty group has no piece/Bake work, but retains
                    // its real outer dispatch, WhenAll and main observation.
                    InitialToCriticalAwaitMs = OuterDispatchMs;
                    CriticalSemaphoreAwaitMs = CriticalInnerDispatchMs = CriticalRenderBuildMs = 0;
                    CriticalCollisionBuildMs = CriticalBodyOtherMs = 0;
                    GroupJoinTailMs = PendingWallMilliseconds(entered, finished);
                    return;
                }
                var piece = pieces[critical];
                InitialToCriticalAwaitMs = PendingWallMilliseconds(created, piece.SemaphoreAwaitStartStamp);
                CriticalSemaphoreAwaitMs = piece.SemaphoreAwaitElapsedMs;
                CriticalInnerDispatchMs = PendingWallMilliseconds(piece.SemaphoreAcquiredStamp, piece.BuildBodyStartStamp);
                CriticalRenderBuildMs = piece.RenderBuildElapsedMs;
                CriticalCollisionBuildMs = piece.CollisionBuildElapsedMs;
                long other = piece.BuildBodyEndStamp - piece.BuildBodyStartStamp
                    - (piece.Rendered ? piece.RenderEndStamp - piece.RenderStartStamp : 0)
                    - (piece.CollisionEndStamp - piece.CollisionStartStamp);
                CriticalBodyOtherMs = PendingWallMilliseconds(0, other);
                GroupJoinTailMs = PendingWallMilliseconds(piece.BuildBodyEndStamp, finished);
            }
        }
        static Prepared PrepareGenerationPiece(BlockWorld next, Vector3Int key, bool needsRenderMesh,
            bool track, long awaitStart, long acquired)
        {
            if (!track)
            {
                if (!needsRenderMesh) return new Prepared { Key = key, Collision = BlockMeshBuilder.Build(next, key, true) };
                BlockMeshBuilder.BuildPair(next, key, out var render, out var collision, false, out _);
                return new Prepared { Key = key, Render = render, Collision = collision };
            }
            long bodyStart = PendingWallNow();
            var piece = new Prepared { Key = key };
            var worker = new PendingPieceWorkerDiagnostic { Key = key, Rendered = needsRenderMesh,
                SemaphoreAwaitStartStamp = awaitStart, SemaphoreAcquiredStamp = acquired, BuildBodyStartStamp = bodyStart };
            if (needsRenderMesh)
            {
                worker.RenderStartStamp = PendingWallNow();
                // This region includes the one shared exposure scan and render emission.
                BlockMeshBuilder.BuildPair(next, key, out piece.Render, out piece.Collision, true, out long renderReady);
                worker.RenderEndStamp = worker.CollisionStartStamp = renderReady;
                worker.CollisionEndStamp = PendingWallNow();
            }
            else
            {
                worker.CollisionStartStamp = PendingWallNow();
                piece.Collision = BlockMeshBuilder.Build(next, key, true);
                worker.CollisionEndStamp = PendingWallNow();
            }
            worker.RenderVertices = piece.Render?.Vertices?.Length ?? 0; worker.RenderNormals = piece.Render?.Normals?.Length ?? 0;
            worker.RenderColors = piece.Render?.Colors?.Length ?? 0; worker.RenderIndices = piece.Render?.Triangles?.Length ?? 0;
            worker.CollisionVertices = piece.Collision?.Vertices?.Length ?? 0; worker.CollisionNormals = piece.Collision?.Normals?.Length ?? 0;
            worker.CollisionColors = piece.Collision?.Colors?.Length ?? 0; worker.CollisionIndices = piece.Collision?.Triangles?.Length ?? 0;
            worker.BuildBodyEndStamp = PendingWallNow(); piece.Worker = worker;
            return piece;
        }
        static PendingPipelineDiagnostic ObservePipeline(Pending pending)
        {
            // No worker-owned field is sampled while its task is unfinished or failed.
            if (!pending.TrackWall || pending.GenerationObservedWallStamp == 0 || pending.UploadCompleteWallStamp == 0
                || pending.BakeObservedWallStamp == 0 || pending.Generate.Status != TaskStatus.RanToCompletion || pending.Pieces == null)
                return default;
            long firstUpload = 0, lastUpload = 0, firstBake = 0, lastBake = 0, uploadTicks = 0, bakeTicks = 0;
            foreach (var piece in pending.Pieces)
            {
                if (piece == null || piece.Bake == null || piece.Bake.Status != TaskStatus.RanToCompletion) return default;
                if (piece.UploadStartStamp != 0)
                {
                    if (firstUpload == 0 || piece.UploadStartStamp < firstUpload) firstUpload = piece.UploadStartStamp;
                    lastUpload = Math.Max(lastUpload, piece.UploadEndStamp);
                    uploadTicks += Math.Max(0, piece.UploadEndStamp - piece.UploadStartStamp);
                }
                if (piece.BakeStartStamp != 0)
                {
                    if (firstBake == 0 || piece.BakeStartStamp < firstBake) firstBake = piece.BakeStartStamp;
                    lastBake = Math.Max(lastBake, piece.BakeEndStamp);
                    bakeTicks += Math.Max(0, piece.BakeEndStamp - piece.BakeStartStamp);
                }
            }
            double overlap(long begin, long end) => begin == 0 ? 0 : PendingWallMilliseconds(0,
                Math.Max(0, Math.Min(end, pending.WorkerGroupFinishedStamp) - Math.Max(begin, pending.WorkerGroupEnteredStamp)));
            return new PendingPipelineDiagnostic(pending.UploadedBeforeGroupCompletion,
                firstUpload == 0 ? 0 : PendingWallMilliseconds(pending.CreatedWallStamp, firstUpload),
                PendingWallMilliseconds(0, uploadTicks), PendingWallMilliseconds(0, bakeTicks),
                overlap(firstUpload, lastUpload), overlap(firstBake, lastBake));
        }
        static PendingGenerationWorkerDiagnostic ObserveGenerationWorker(Pending pending)
        {
            // Read worker data only after successful Generate completion was
            // observed on the main thread. Fault/early abort/synthetic work is unavailable.
            if (pending.GenerationObservedWallStamp == 0 || pending.WorkerGroupEnteredStamp < pending.CreatedWallStamp
                || pending.WorkerGroupEnteredStamp == 0 || pending.WorkerGroupFinishedStamp < pending.WorkerGroupEnteredStamp
                || pending.WorkerGroupFinishedStamp > pending.GenerationObservedWallStamp || pending.Pieces == null) return default;
            int critical = -1; long latest = 0;
            var pieces = pending.Pieces.Length == 0 ? Array.Empty<PendingPieceWorkerDiagnostic>()
                : new PendingPieceWorkerDiagnostic[pending.Pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pending.Pieces[i] == null) return default;
                var piece = pending.Pieces[i].Worker;
                if (piece.SemaphoreAwaitStartStamp < pending.WorkerGroupEnteredStamp
                    || piece.SemaphoreAcquiredStamp < piece.SemaphoreAwaitStartStamp
                    || piece.BuildBodyStartStamp < piece.SemaphoreAcquiredStamp
                    || piece.CollisionStartStamp < piece.BuildBodyStartStamp || piece.CollisionEndStamp < piece.CollisionStartStamp
                    || piece.BuildBodyEndStamp < piece.CollisionEndStamp || piece.BuildBodyEndStamp > pending.WorkerGroupFinishedStamp
                    || piece.Rendered && (piece.RenderStartStamp < piece.BuildBodyStartStamp || piece.RenderEndStamp < piece.RenderStartStamp
                        || piece.CollisionStartStamp < piece.RenderEndStamp)
                    || !piece.Rendered && (piece.RenderStartStamp != 0 || piece.RenderEndStamp != 0)) return default;
                pieces[i] = piece;
                if (piece.BuildBodyEndStamp > latest) { latest = piece.BuildBodyEndStamp; critical = i; }
            }
            return new PendingGenerationWorkerDiagnostic(pending.CreatedWallStamp, pending.GenerationObservedWallStamp,
                pending.WorkerGroupEnteredStamp, pending.WorkerGroupFinishedStamp, pieces, critical);
        }
        // Terminal boundaries are main-thread observations; worker stamps
        // are separate per-piece observations, without per-cell samples. Observers
        // must only collect diagnostics and must not reenter terrain/gameplay APIs.
        public event Action<PendingWallDiagnostic> PendingTransactionFinished;
        PendingWorkDiagnostics _pendingWork;
        long _pendingWallId;
        static long PendingWallNow() => System.Diagnostics.Stopwatch.GetTimestamp();
        static double PendingWallMilliseconds(long start, long end)
            => Math.Max(0, end - start) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
        public PendingWorkDiagnostics InspectPendingWork()
        { var value = _pendingWork; value.tracking = PendingTransactionFinished != null; return value; }
        public PendingHeadDiagnostic InspectPendingTransaction()
        {
            var pending = _pending;
            if (pending == null) return default;
            if (!pending.TrackWall) return new PendingHeadDiagnostic { present = true, remote = pending.Remote };
            long now = PendingWallNow(), phaseStart = pending.CreatedWallStamp;
            var phase = PendingWallPhase.Generation;
            if (pending.GenerationObservedWallStamp != 0) { phase = PendingWallPhase.Upload; phaseStart = pending.GenerationObservedWallStamp; }
            if (pending.UploadCompleteWallStamp != 0) { phase = PendingWallPhase.Bake; phaseStart = pending.UploadCompleteWallStamp; }
            if (pending.ReadyWallStamp != 0) { phase = PendingWallPhase.ReadyToPublish; phaseStart = pending.ReadyWallStamp; }
            return new PendingHeadDiagnostic { present = true, tracked = true, remote = pending.Remote,
                id = pending.WallId, createdWallTimestamp = pending.CreatedWallStamp, phase = phase,
                terminalObserved = pending.WallReported, terminalOutcome = pending.WallOutcome,
                ageMs = PendingWallMilliseconds(pending.CreatedWallStamp, now), phaseAgeMs = PendingWallMilliseconds(phaseStart, now) };
        }
        void RecordPendingTerminal(Pending pending, PendingWallOutcome outcome)
        {
            if (outcome != PendingWallOutcome.Published) RejectBuildOverlapRepair(pending);
            if (!pending.TrackWall || pending.WallReported) return;
            pending.WallReported = true;
            pending.WallOutcome = outcome;
            if (outcome == PendingWallOutcome.Aborted) ObservePublicationAbort(pending);
            if (outcome == PendingWallOutcome.Published) _pendingWork.published++;
            else if (outcome == PendingWallOutcome.Failed) _pendingWork.failed++;
            else _pendingWork.aborted++;
            long now = PendingWallNow(); int pieces = pending.Pieces?.Length ?? 0;
            bool generation = pending.GenerationObservedWallStamp != 0;
            bool upload = pending.UploadCompleteWallStamp != 0;
            bool bake = pending.BakeObservedWallStamp != 0;
            bool publishEntered = pending.PublishEnterWallStamp != 0;
            var sample = new PendingWallDiagnostic(outcome, pending.WallId, pending.CreatedWallStamp, pending.Remote, pieces,
                generation, upload, bake, publishEntered,
                generation ? PendingWallMilliseconds(pending.CreatedWallStamp, pending.GenerationObservedWallStamp) : 0,
                generation && upload && pieces > 0 ? PendingWallMilliseconds(pending.GenerationObservedWallStamp, pending.UploadCompleteWallStamp) : 0,
                upload && bake && pieces > 0 ? PendingWallMilliseconds(pending.UploadCompleteWallStamp, pending.BakeObservedWallStamp) : 0,
                pending.ReadyWallStamp != 0 && publishEntered ? PendingWallMilliseconds(pending.ReadyWallStamp, pending.PublishEnterWallStamp) : 0,
                publishEntered ? PendingWallMilliseconds(pending.PublishEnterWallStamp, now) : 0,
                PendingWallMilliseconds(pending.CreatedWallStamp, now), ObserveGenerationWorker(pending), ObservePipeline(pending), pending.AbortReason);
            ObserveRemoteInstallTerminal(pending, now, sample);
            // A diagnostic observer exception must not interrupt primary behavior.
            try { PendingTransactionFinished?.Invoke(sample); }
            catch (Exception) { _pendingWork.observerFailures++; }
        }

        sealed class Pending
        {
            public uint Epoch;
            public double CreatedAt;
            public bool TrackWall, WallReported, WallFailure;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public long TraceInstallId;
            public RemoteTransferContext TraceContext;
            public RemoteInstallToken TracePublishedToken;
#endif
            public PendingWallOutcome WallOutcome;
            public PendingAbortReason AbortReason;
            public long WallId, CreatedWallStamp, GenerationObservedWallStamp, UploadCompleteWallStamp, BakeObservedWallStamp, ReadyWallStamp, PublishEnterWallStamp;
            public long WorkerGroupEnteredStamp, WorkerGroupFinishedStamp;
            public BlockWorld Next;
            public BlockPublicationWindow Window;
            public GravityJournal Gravity;
            public bool PrivateGravity;
            public LinkedList<GravityStaticProof> StaticProofs;
            public BuildOccupancyPreparation BuildGuard;
            public BuildOverlapRepair BuildOverlapRepair;
            public HistoricalRecoveryPreparation RecoveryGuard;
            public LinkedList<CommandCheckpoint> CommandCheckpoints;
            public BlockWorld PrivateSource;
            public uint PrivateSourceRevision, PrivateSourceRound;
            public ulong PrivateSourceMutation;
            public Vector3Int[] Changed;
            public readonly CancellationTokenSource AdmissionStop = new();
            public bool ForegroundWork;
            public int AdmissionsDisposed;
            public bool PureAuthorityBackground, FairForeground, FairDependency, FairRetired;
            public FairPublicationSeed FairSeed;
            public BlockWorld FairSource;
            public uint FairSourceRevision, FairSourceRound;
            public ulong FairSourceMutation, FairReservationVersion;
            public Task<Prepared[]> Generate;
            // Schedule is an immutable task-array handoff. Main reads it only after completion.
            public TaskCompletionSource<Task<Prepared>[]> Scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<Prepared>[] Generating;
            // Only main installs completed results and owns upload/view fields.
            public Prepared[] Pieces;
            public bool[] PieceUploaded;
            public int Uploaded, UploadScanCursor;
            public bool UploadedBeforeGroupCompletion;
            public bool Remote;
            public readonly List<(Command command, BlockMutationResult result)> Results = new();
        }
        sealed class Retired { public Pending Pending; }
        sealed class RemoteInstall
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public long TraceInstallId;
            public RemoteTransferContext TraceContext;
#endif
            public BlockWorld Next;
            public HashSet<Vector3Int> Changed;
            public bool Full;
        }
        readonly struct FallingWork
        {
            public readonly ulong Id;
            public readonly float Height;
            public FallingWork(FallingRun run, double now) { Id = run.Id; Height = BlockWorld.Center(run.Bottom).y - run.Offset(now); }
        }
        readonly struct DestroyTarget
        {
            public readonly Vector3Int Cell;
            public readonly byte Owner;
            public readonly ulong Run;
            public readonly int Index;
            public readonly float Distance;
            public DestroyTarget(Vector3Int cell, byte owner, float distance, ulong run = 0, int index = 0)
            { Cell = cell; Owner = owner; Distance = distance; Run = run; Index = index; }
        }
        sealed class Command
        {
            public Vector3 Point, Normal;
            public byte Team;
            public int Count, MaxEnemy, MaxFriendly;
            public float Radius, FriendlyRadius;
            public bool Build, Anchor, EnemyOnly, Done, Completed, OrdinaryMainAttack, Spread, SpreadStaged;
            public uint SpreadGeneration;
            public double CreatedAt;
            public Action<BlockMutationResult> Callback;
            public IEnumerator<Vector3Int> Cells;
            public Vector3Int LastCandidate;
            public bool HasCandidate;
            public Vector2 BuildDimensions;
            public float BuildBottom;
            public BlockWorld Source;
            public BlockPublicationWindow RetryWindow;
            public double SourceTime;
            public IEnumerator<DestroyTarget> Preparing;
            public List<DestroyTarget> Prepared;
            public Task<DestroyTarget[]> Sorting;
            public DestroyTarget[] Targets;
            public int TargetIndex;
            public BlockMutationResult Total;
        }

        void Awake() { Current = this; }
        public void Initialize(PrototypeArena arena)
        {
            using var publicationTiming = MeasurePublicationWork();
            if (Initialized) return;
            ResetBuildQueriesCache();
            _fallProperties = new MaterialPropertyBlock();
            _arena = arena; Current = this; InitializeSafety();
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); transform.localScale = Vector3.one;
            World = new BlockWorld(arena != null ? arena.Dimensions : new Vector2(32, 64));
            if (arena != null) foreach (var collider in arena.GetComponentsInChildren<Collider>(true)) if (!collider.isTrigger) _staticColliders.Add(collider);
            // The serialized template retains the falling instancing variants in Players.
            var template = Resources.Load<Material>("BlockTerrainInstanced");
            if (template != null) _material = new Material(template) { name = "Runtime Block Terrain", enableInstancing = true };
            else
            {
                var shader = Resources.Load<Shader>("BlockTerrain");
                if (shader == null) shader = Shader.Find("Splatoon/Blocks");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
                _material = new Material(shader) { name = "Runtime Block Terrain", enableInstancing = true };
            }
            _material.SetFloat("_BlockCellSize", BlockWorld.CellSize);
            var cubeWorld = new BlockWorld(Vector2.one); cubeWorld.Set(Vector3Int.zero, BlockCell.Create(1));
            _fallMesh = BlockMeshBuilder.Build(cubeWorld, Vector3Int.zero, false).Upload("Block falling cube");
            var vertices = _fallMesh.vertices; for (int i = 0; i < vertices.Length; i++) vertices[i] = vertices[i] / BlockWorld.CellSize - Vector3.one * .5f;
            _fallMesh.vertices = vertices; _fallMesh.colors32 = Enumerable.Repeat(new Color32(255, 255, 255, 255), vertices.Length).ToArray(); _fallMesh.RecalculateBounds();
            var proxy = new GameObject("Falling block hit proxy") { layer = 8 }; proxy.transform.SetParent(transform, false);
            _fallProxy = proxy.AddComponent<BoxCollider>(); _fallProxy.enabled = false;
            Initialized = true; ResetGravityCohorts();
        }
        public bool HasRevision(uint revision) => Initialized && Revision >= revision;
        public uint ChunkRevision(Vector3Int chunk) => World?.ChunkRevision(chunk) ?? 0;
        public byte[] ExportSnapshot() => World?.EncodeSnapshot() ?? Array.Empty<byte>();
        public byte[] ExportDelta(uint sinceRevision) => World?.EncodeDelta(sinceRevision) ?? Array.Empty<byte>();
        public bool ApplyRemoteSnapshot(byte[] bytes)
        {
            using var publicationTiming = MeasurePublicationWork();
            if (!Initialized) Initialize(PrototypeArena.Current);
            try
            {
                var next = BlockWorld.DecodeSnapshot(bytes);
                if (next.Dimensions != World.Dimensions || EarlierVersion(next, World) ||
                    _pending != null && EarlierVersion(next, _pending.Next) ||
                    _remoteQueued != null && EarlierVersion(next, _remoteQueued.Next)) return false;
                if (next.Round > Round) Clear(next.Round);
                else if (_remoteQueued != null) return false;
                // A full repair may wait behind a current publication, but cannot
                // overwrite another queued install or skip its publication event.
                _remoteQueued = new RemoteInstall { Next = next, Full = true }; ObserveRemoteInstallQueued(_remoteQueued); return true;
            }
            catch (Exception exception) { Debug.LogWarning("[BLOCK] Snapshot rejected: " + exception.Message); return false; }
        }
        public bool ApplyRemoteDelta(byte[] bytes)
        {
            using var publicationTiming = MeasurePublicationWork();
            if (!Initialized || _pending != null || _remoteQueued != null) return false;
            try
            {
                var next = World.WithDelta(bytes, out var changed);
                _remoteQueued = new RemoteInstall { Next = next, Changed = changed }; ObserveRemoteInstallQueued(_remoteQueued); return true;
            }
            catch (Exception exception) { Debug.LogWarning("[BLOCK] Delta rejected: " + exception.Message); return false; }
        }
        static bool EarlierVersion(BlockWorld next, BlockWorld prior)
            => next.Round < prior.Round || next.Round == prior.Round && next.Revision < prior.Revision;
        /// <summary>Explicit diagnostic fixture; uses the same asynchronous collider publication path.</summary>
        public bool QueueSeed(BlockWorld seed)
        {
            using var publicationTiming = MeasurePublicationWork();
            if (!Initialized || seed == null || seed.Dimensions != World.Dimensions || seed.Round != Round) return false;
            uint revision = Revision + 1; var changed = new HashSet<Vector3Int>(World.RevisionKeys); changed.UnionWith(seed.RevisionKeys);
            _epoch++; AbortPending(); _blastBatch = null; while (_commands.Count > 0) Complete(_commands.Dequeue(), default);
            _gravityColumns.Clear(); _gravityCursor.Clear(); _landingGeneration.Clear(); _landingChecked.Clear();
            ResetGravityCohorts();
            ResetBlockSpread();
            var next = seed.Clone(); next.Seal(revision, changed); _remoteQueued = new RemoteInstall { Next = next, Full = true }; return true;
        }
        public void QueueBuild(Vector3 point, Vector3 normal, byte team, int count, float radius, bool anchorToMap, Action<BlockMutationResult> callback = null, bool ordinaryMainAttack = false)
        {
            if (!Initialized || !_accepting || SurfaceOnly && !anchorToMap || team < 1 || team > 2 || count <= 0 || !Finite(point) || !Finite(normal) || !ValidRadius(radius)) { callback?.Invoke(default); return; }
            CommandsQueued++;
            _commands.Enqueue(new Command { Point = point, Normal = normal.normalized, Team = team, Count = count, Radius = radius, Anchor = anchorToMap, Build = true,
                Callback = callback, CreatedAt = _now, BuildDimensions = World.Dimensions, BuildBottom = World.Bottom, OrdinaryMainAttack = ordinaryMainAttack });
            ObserveFairQueuedInput(false);
        }
        public void QueueDestroy(Vector3 point, float enemyRadius, float friendlyRadius, byte team, bool enemyOnly, Action<BlockMutationResult> callback = null, int maxEnemyBlocks = int.MaxValue, int maxFriendlyBlocks = int.MaxValue, bool ordinaryMainAttack = false)
        {
            if (!Initialized || !_accepting || team < 1 || team > 2 || !Finite(point) || !ValidRadius(enemyRadius) || !ValidRadius(friendlyRadius) || maxEnemyBlocks < 0 || maxFriendlyBlocks < 0) { callback?.Invoke(default); return; }
            CommandsQueued++;
            var command = new Command { Point = point, Team = team, Radius = enemyRadius, FriendlyRadius = friendlyRadius, EnemyOnly = enemyOnly,
                MaxEnemy = maxEnemyBlocks, MaxFriendly = maxFriendlyBlocks, Callback = callback, CreatedAt = _now, Source = World, SourceTime = _now, OrdinaryMainAttack = ordinaryMainAttack };
            _commands.Enqueue(command); _destructionReservations.Add(command); RegisterReservation(command); _reservedColumns.Clear();
            ObserveFairQueuedInput(true);
        }
        public void FinishRound(double frozenAt = double.NaN)
        {
            using var publicationTiming = MeasurePublicationWork();
            if (double.IsFinite(frozenAt)) _now = Math.Max(0, frozenAt);
            if (!_accepting) return;
            _accepting = false; _epoch++;
            Dynamic?.Freeze();
            ResetBlockSpread();
            AbortPending(); _blastBatch = null; while (_commands.Count > 0) Complete(_commands.Dequeue(), default);
            _gravityColumns.Clear(); _gravityCursor.Clear(); ObserveRemoteInstallDiscarded(_remoteQueued, "DiscardedFinishRound"); _remoteQueued = null; ResetGravityCohorts();
        }
        public void Clear(uint round)
        {
            using var publicationTiming = MeasurePublicationWork();
            _epoch++; AbortPending(); _blastBatch = null; while (_commands.Count > 0) Complete(_commands.Dequeue(), default);
            ResetBlockSpread();
            ObserveRemoteInstallDiscarded(_remoteQueued, "DiscardedClear");
            _remoteQueued = null; _gravityColumns.Clear(); _gravityCursor.Clear(); _landingGeneration.Clear(); _landingChecked.Clear(); ResetGravityCohorts();
            ClearSafetyState();
            ResetBuildQueriesCache();
            foreach (var view in _views.Values) Dispose(view); _views.Clear(); _colliderChunks.Clear();
            World = new BlockWorld(_arena != null ? _arena.Dimensions : new Vector2(32, 64), round); _accepting = true;
            Dynamic?.Reset(round);
            Physics.SyncTransforms();
        }
        public void Tick(bool authority, double now, float dt)
        {
            using var marker = TickCpu.Auto();
            using var publicationTiming = MeasurePublicationWork();
            if (!Initialized || _destroying) return;
            Dynamic?.SetAuthority(authority && _accepting);
            if (authority && !_cohortAuthorityInitialized) { ResetGravityCohorts(!SurfaceOnly); ResetBlockSpread(!SurfaceOnly); _cohortAuthorityInitialized = true; }
            if (!authority && _pending?.BuildOverlapRepair != null) AbortPending();
            if (!authority) { if (_cohortAuthorityInitialized) ResetBlockSpread(); _cohortAuthorityInitialized = false; AbortPrivatePreparation(); if (_pending != null && _pending.BuildGuard != null) CancelPendingBuildPublication(_pending);
                else if (_pending != null && (_pending.PrivateGravity || _pending.RecoveryGuard != null)) AbortPending(); }
            if (!authority && _pending?.Gravity?.SpreadMoves.Count > 0) AbortPending();
            _authority = authority; if (_accepting) _now = now;
            RefreshFairSchedulingContext();
            RefreshBuildOverlapRepair();
            if (authority && _accepting) CaptureSafetyPlayers();
            if (_budgetFrame != Time.frameCount) { _budgetFrame = Time.frameCount; _publishedThisFrame = 0; }
            CleanupRetired(); TryYieldBackgroundPublication(); AdvancePending(true);
            if (_pending != null) return;
            if (_remoteQueued != null)
            {
                AbortPrivatePreparation();
                var install = _remoteQueued; _remoteQueued = null;
                var changed = install.Changed;
                if (install.Full)
                {
                    // Compute after any preceding transaction publishes, so full
                    // repairs rebuild every old/new live chunk even at equal revision.
                    changed = new HashSet<Vector3Int>(World.Chunks.Keys); changed.UnionWith(install.Next.Chunks.Keys);
                }
                BeginTransaction(install.Next, changed, true, null); ObserveRemoteInstallStaged(install); return;
            }
            if (!authority || !_accepting) return;
            ScheduleSpreadWork();
            StageAuthority();
        }
        void Update() { using var publicationTiming = MeasurePublicationWork(); if (Initialized) { CleanupRetired(); AdvancePending(false); } }
        static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
        static bool ValidRadius(float radius) => !float.IsNaN(radius) && !float.IsInfinity(radius) && radius >= 0;
        static Vector3Int Axis(Vector3 n)
        {
            if (Mathf.Abs(n.y) >= Mathf.Abs(n.x) && Mathf.Abs(n.y) >= Mathf.Abs(n.z)) return n.y >= 0 ? Vector3Int.up : Vector3Int.down;
            return Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? n.x >= 0 ? Vector3Int.right : Vector3Int.left : new Vector3Int(0, 0, n.z >= 0 ? 1 : -1);
        }
        IEnumerable<Vector3Int> BuildCandidates(Command command) => OrderedBuildCandidates(command);
        IEnumerable<DestroyTarget> DestroyCandidates(Command command)
        {
            var source = command.Source;
            float radius = Mathf.Max(command.Radius, command.EnemyOnly ? 0 : command.FriendlyRadius) + BlockWorld.CellSize;
            var low = Vector3.Max(command.Point - Vector3.one * radius, new Vector3(-source.Dimensions.x * .5f, source.Bottom, -source.Dimensions.y * .5f));
            var high = Vector3.Min(command.Point + Vector3.one * radius, new Vector3(source.Dimensions.x * .5f, BlockWorld.Top, source.Dimensions.y * .5f));
            var minCell = BlockWorld.CellAt(low); var maxCell = BlockWorld.CellAt(high);
            var min = BlockWorld.ChunkAt(minCell); var max = BlockWorld.ChunkAt(maxCell);
            foreach (var pair in source.Chunks)
            {
                var key = pair.Key;
                // Count rejected chunks too: one MoveNext cannot scan the entire sparse world.
                if (key.x < min.x || key.x > max.x || key.y < min.y || key.y > max.y || key.z < min.z || key.z > max.z)
                { yield return default; continue; }
                var origin = BlockWorld.ChunkOrigin(key);
                var first = Vector3Int.Max(origin, minCell); var last = Vector3Int.Min(origin + Vector3Int.one * (BlockWorld.ChunkSize - 1), maxCell);
                // A valid command can be outside a partial map-edge chunk.
                // Never form a negative-width mask or skip empty intersections in one MoveNext.
                if (first.x > last.x || first.y > last.y || first.z > last.z)
                { yield return default; continue; }
                int lowX = first.x - origin.x, width = last.x - first.x + 1;
                uint xMask = ((1u << width) - 1u) << lowX;
                for (int z = first.z; z <= last.z; z++) for (int y = first.y; y <= last.y; y++)
                {
                    // Rows are eagerly maintained by BlockWorld.Set/decode and copied under COW.
                    // The immutable attack source is the same source used by the original scan.
                    uint occupied = (uint)pair.Value.OccupiedRow(0, new Vector3Int(0, y - origin.y, z - origin.z)) & xMask;
                    // Empty and nonempty rows both consume one bounded preparation step.
                    yield return default;
                    while (occupied != 0)
                    {
                        int localX = LowestBuildBit(occupied); occupied &= occupied - 1;
                        var cell = new Vector3Int(origin.x + localX, y, z); var own = pair.Value.Cells[BlockWorld.Index(cell)];
                        if (!own.Occupied || command.EnemyOnly && own.Owner == command.Team) { yield return default; continue; }
                        float r = own.Owner == command.Team ? command.FriendlyRadius : command.Radius;
                        var point = BlockWorld.Center(cell); float distance = (point - command.Point).sqrMagnitude;
                        yield return distance <= (r + BlockRadiusPadding) * (r + BlockRadiusPadding) && Visible(command.Point, point)
                            ? new DestroyTarget(cell, own.Owner, distance) : default;
                    }
                }
            }
            float fallingRadiusSquared = radius * radius;
            foreach (var run in source.Falling.Values)
            {
                // Falling changes only Y. Use the wider existing cell-padded
                // radius and independent axis bounds; keep sphere-edge decisions
                // in the original Vector3 predicate. Infinity stays conservative.
                // One rejected run still yields bounded work.
                var columnCenter = BlockWorld.Center(run.Bottom);
                float dx = columnCenter.x - command.Point.x, dz = columnCenter.z - command.Point.z;
                if (dx * dx > fallingRadiusSquared || dz * dz > fallingRadiusSquared) { yield return default; continue; }
                bool sampledOffset = false; float offset = 0;
                for (int i = 0; i < run.Cells.Length; i++)
                {
                    var own = run.Cells[i]; if (!own.Occupied || command.EnemyOnly && own.Owner == command.Team) { yield return default; continue; }
                    // The source and SourceTime are frozen by QueueDestroy. Offset
                    // is pure, including live/retained lower-support evaluation.
                    if (!sampledOffset) { offset = run.Offset(command.SourceTime); sampledOffset = true; }
                    var cell = run.Bottom + Vector3Int.up * i; var point = BlockWorld.Center(cell) - Vector3.up * offset;
                    float radiusForTeam = own.Owner == command.Team ? command.FriendlyRadius : command.Radius, distance = (point - command.Point).sqrMagnitude;
                    yield return distance <= (radiusForTeam + BlockRadiusPadding) * (radiusForTeam + BlockRadiusPadding) && Visible(command.Point, point)
                        ? new DestroyTarget(cell, own.Owner, distance, run.Id, i) : default;
                }
            }
        }
        bool MapOverlap(Vector3Int cell) => BuildMapOverlapCached(cell);
        // Match the native landing quantization exactly. A snapped-up first
        // cell can have its center almost 1.5 cell widths above the map.
        const float StaticSupportProbeLength = BlockWorld.CellSize * 1.5f + .00001f;
        static int StaticLandingCellY(float height)
            => Mathf.CeilToInt(height / BlockWorld.CellSize - .0001f);
        bool IsQuantizedStaticSupport(Vector3Int cell, RaycastHit hit)
            => _staticColliders.Contains(hit.collider) && hit.normal.y > .2f
                && StaticLandingCellY(hit.point.y) == cell.y;
        bool StaticSupport(Vector3Int cell)
        {
            var start = BlockWorld.Center(cell);
            int count = Physics.RaycastNonAlloc(start, Vector3.down, _rayHits, StaticSupportProbeLength, SurfaceMapMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++) if (IsQuantizedStaticSupport(cell, _rayHits[i])) return true;
            return false;
        }
        bool StaticAnchor(Vector3Int cell, Vector3 normal)
        {
            if (Mathf.Abs(normal.y) > .7f) return false;
            int count = Physics.RaycastNonAlloc(BlockWorld.Center(cell), -normal, _rayHits, BlockWorld.CellSize * 1.25f, SurfaceMapMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++) if (_staticColliders.Contains(_rayHits[i].collider)) return true;
            return false;
        }
        bool Visible(Vector3 from, Vector3 to)
        {
            var delta = to - from; float distance = delta.magnitude; if (distance < .03f) return true;
            int count = Physics.RaycastNonAlloc(from + delta / distance * .015f, delta / distance, _rayHits, Mathf.Max(0, distance - .03f), SurfaceMapMask, QueryTriggerInteraction.Ignore);
            if (count == _rayHits.Length) return false;
            for (int i = 0; i < count; i++) if (_staticColliders.Contains(_rayHits[i].collider)) return false;
            return true;
        }
        static bool Boundary(Vector3Int local, Vector3Int normal)
            => normal.x > 0 ? local.x == BlockWorld.ChunkSize - 1 : normal.x < 0 ? local.x == 0
                : normal.y > 0 ? local.y == BlockWorld.ChunkSize - 1 : normal.y < 0 ? local.y == 0
                : normal.z > 0 ? local.z == BlockWorld.ChunkSize - 1 : local.z == 0;
        static void IncludeCell(HashSet<Vector3Int> changed, Vector3Int cell)
        {
            var key = BlockWorld.ChunkAt(cell); var local = cell - BlockWorld.ChunkOrigin(key);
            changed.Add(key); foreach (var normal in Neighbours) if (Boundary(local, normal)) changed.Add(key + normal);
        }
        static bool FitsBudget(BlockPublicationWindow window, HashSet<Vector3Int> changed, Vector3Int cell)
            => window.CanIncludeCell(changed, cell);
        void QueueGravity(Vector3Int cell) { if (!SurfaceOnly) QueueGravityCohortCell(cell); }
        bool FallingReady() => _cohortIndexPending || _cohortMotionOrder.Count != 0;
        bool LandingDirty(FallingRun run)
            => _landingGeneration.TryGetValue(new Vector2Int(run.Bottom.x, run.Bottom.z), out uint generation)
                && (!_landingChecked.TryGetValue(run.Id, out uint checkedGeneration) || checkedGeneration != generation);
        void StageAuthority()
        {
            try
            {
            _stagePublicationWindow = SelectPublicationWindow();
            // Full synchronous accounting supplements the unchanged phase clocks.
            // At least one finite authority unit per fresh frame remains possible.
            if (_authorityPublicationWorkFrame == Time.frameCount && TerrainSynchronousMilliseconds >= _stagePublicationWindow.Policy.FrameSynchronousMilliseconds) return;
            _authorityPublicationWorkFrame = Time.frameCount;
            _stagePublicationReplayOnly = _commands.Count != 0 && _commands.Peek().RetryWindow != null;
            if (AdvanceDependentEditPreparation()) return;
            if (AdvanceBuildOccupancyPreparation()) return;
            if (AdvanceHistoricalRecoveryPreparation()) return;
            if (AdvanceHeadPreparation()) return;
            if (AdvancePrivatePreparation()) return;
            if (PrepareFairAuthorityStage(out var fairNext)) return;
            _stageCommandCheckpoints = null; _stageAddedBuildSlots = null; _stageCommandCheckpoint = null; _stageHasMovingBuild = false; _stageMovingBuildColumns = null;
            _stageBuildOverlapRepair = SelectBuildOverlapRepair();
            bool commandWork = _stageBuildOverlapRepair == null && _commands.Count > 0 && CanRunPublicationCommand(_commands.Peek()),
                gravityWork = FairAllowsGravity && (_gravityColumns.Count > 0 || FallingReady()) && _stagePublicationWindow.Includes(BlockPublicationOperation.Gravity);
            bool spreadStage = commandWork && _commands.Peek().Spread;
            if (spreadStage) gravityWork = false;
            _stagePureAuthorityBackground = !commandWork && gravityWork;
            if (!commandWork && !gravityWork)
            {
                ReleaseBuildOverlapRepairNoReady();
                // A retry with no remaining work releases its lease before
                // a later attack is admitted under the current policy.
                if (ReferenceEquals(_gravityRetryPublicationWindow, _stagePublicationWindow)) _gravityRetryPublicationWindow = null;
                if (_fairSeed != null && !_fairSeed.Foreground && fairNext != null) FairBackgroundStageDidNotPublish(); return;
            }
            // Static overlap proofs must describe the same transforms PhysX queries.
            Physics.SyncTransforms();
            _stageTransformsSynced = true;
            var next = fairNext ?? World.Clone(); var changed = new HashSet<Vector3Int>(); var results = new List<(Command, BlockMutationResult)>();
            StartWindowGravityJournal(next, _stagePublicationWindow);
            AuthorityStageCount++; _reservedColumns.Clear();
            LastCommandStageMilliseconds = LastGravityStageMilliseconds = 0;
            // Internal complete-burst throughput; ordinary attack budgets remain unchanged.
            bool coalescedBlast = !FairForegroundStage && commandWork && HasCoalescedBlastWork();
            double commandBudget = coalescedBlast ? 4 : !spreadStage && commandWork ? .5 : .75, gravityBudget = _commands.Count != 0 ? BlockPublicationPolicy.GravityMillisecondsWithCommands : BlockPublicationPolicy.GravityMillisecondsAlone;
            int commandChecks = coalescedBlast ? 32768 : !spreadStage && commandWork ? 3072 : MaxChecks, gravityChecks = _commands.Count != 0 ? BlockPublicationPolicy.GravityChecksWithCommands : BlockPublicationPolicy.GravityChecksAlone;
            if (SurfaceOnly && commandWork)
            {
                // Surface construction has no voxel-gravity phase. Reuse only
                // the remaining captured frame allowance, including clone,
                // safety and all earlier Tick/Update work already charged.
                // HasStageBudget still permits one finite unit for progress.
                commandBudget = Math.Max(0, _stagePublicationWindow.Policy.FrameSynchronousMilliseconds
                    - TerrainSynchronousMilliseconds);
            }
            if (FairForegroundStage)
            {
                commandBudget = .5; commandChecks = 3072;
                if (commandWork && _commands.Peek().Build && _cohortIndexPending)
                {
                    var indexTimer = System.Diagnostics.Stopwatch.StartNew(); int indexChecks = 0;
                    IndexGravityWork(next, ref indexChecks, indexTimer, BlockPublicationPolicy.GravityMillisecondsWithCommands, BlockPublicationPolicy.GravityChecksWithCommands);
                    LastGravityStageMilliseconds = indexTimer.Elapsed.TotalMilliseconds;
                }
            }
            bool gravityChanged = false, gravityPhaseRan = false;
            bool CanRunGravityPhase() => !spreadStage && FairAllowsGravity && _stagePublicationWindow.Includes(BlockPublicationOperation.Gravity)
                && (ColumnCount > 0 || MotionCount > 0 || _cohortIndexPending);
            // Separate clocks guarantee work even when the other phase overruns. Alternating
            // the first phase also shares the bounded chunk slots under sustained load.
            for (int phase = 0; phase < 2; phase++)
            {
                bool gravity = _gravityFirst ? phase == 0 : phase == 1;
                if (gravity && CanRunGravityPhase())
                {
                    gravityPhaseRan = true;
                    var timer = System.Diagnostics.Stopwatch.StartNew(); int checks = 0;
                    _spreadOwesGravity = false;
                    gravityChanged = StageGravity(next, changed, ref checks, timer, gravityBudget, gravityChecks);
                    LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                    if (gravityChanged) GravityProgressStages++;
                }
                else if (!gravity && commandWork)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew(); int checks = 0; long before = CommandWorkChecks;
                    StageCommands(next, changed, results, ref checks, timer, commandBudget, commandChecks);
                    LastCommandStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                    if (CommandWorkChecks != before) CommandProgressStages++;
                }
            }
            // A command may remove the first support after the gravity-first
            // slot was empty. Spend its already reserved slot once this stage.
            if (!gravityPhaseRan && CanRunGravityPhase())
            {
                var timer = System.Diagnostics.Stopwatch.StartNew(); int checks = 0;
                _spreadOwesGravity = false;
                gravityChanged = StageGravity(next, changed, ref checks, timer, gravityBudget, gravityChecks);
                LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (gravityChanged) GravityProgressStages++;
            }
            _stageTransformsSynced = false;
            _gravityFirst = !_gravityFirst;
            bool any = gravityChanged || changed.Count > 0 || results.Any(r => r.Item2.Changed > 0) || next.Falling.Count != World.Falling.Count;
            // An oversized dependent edit owns the complete final admitted
            // draft, including commands that ran after the gravity phase.
            if (BeginDependentEditPreparation(next, changed, results, _stagePublicationWindow, any)) return;
            if (!any)
            {
                CommitGravityJournal(_gravityJournal);
                CommitBuildOverlapRepairNoChange(_gravityJournal); _gravityJournal = null;
                FairTerminalNoPublication(results);
                if (_stagePureAuthorityBackground && _stageBuildOverlapRepair == null) FairBackgroundStageDidNotPublish();
                foreach (var pair in results) if (pair.Item1.Done) Complete(pair.Item1, pair.Item1.Total);
                return;
            }
            next.Seal(Revision + 1, changed); BeginWindowTransaction(next, changed, false, results, _stagePublicationWindow);
            }
            finally { _stageBuildOverlapRepair = null; _stageTransformsSynced = false; _gravityJournal = null; _stagePublicationWindow = null; _stagePublicationReplayOnly = false; _stagePureAuthorityBackground = false; }
        }
        static bool HasStageBudget(int checks, int maxChecks, System.Diagnostics.Stopwatch timer, double milliseconds)
            => checks < maxChecks && (checks == 0 || timer.Elapsed.TotalMilliseconds < milliseconds);
        void StageCommands(BlockWorld next, HashSet<Vector3Int> changed, List<(Command, BlockMutationResult)> results,
            ref int checks, System.Diagnostics.Stopwatch timer, double milliseconds, int maxChecks)
        {
            bool ownsPublicationWindow = _stagePublicationWindow == null;
            if (ownsPublicationWindow) _stagePublicationWindow = _gravityJournal?.Window ?? SelectPublicationWindow();
            if (_gravityJournal == null) StartWindowGravityJournal(next, _stagePublicationWindow);
            if (!ReferenceEquals(_gravityJournal.Next, next)) throw new InvalidOperationException("Gravity journal belongs to another staging world.");
            try
            {
                CaptureStageConstructionZones(); _stageBuildWorld = next;
                StageCommandsCore(next, changed, results, ref checks, timer, milliseconds, maxChecks);
            }
            finally { _stageBuildWorld = null; _stageConstructionZonesActive = false; _stageConstructionZoneCount = 0; if (ownsPublicationWindow) _stagePublicationWindow = null; }
        }
        void StageCommandsCore(BlockWorld next, HashSet<Vector3Int> changed, List<(Command, BlockMutationResult)> results,
            ref int checks, System.Diagnostics.Stopwatch timer, double milliseconds, int maxChecks)
        {
            while (_commands.Count > 0 && HasStageBudget(checks, maxChecks, timer, milliseconds))
            {
                var command = _commands.Peek();
                if (!CanRunPublicationCommand(command)) break;
                if (command.Spread)
                {
                    if (results.Count != 0 || changed.Count != 0) break;
                    StageSpread(command, next, changed, results, ref checks, timer, milliseconds, maxChecks); return;
                }
                if (!FairForegroundStage && TryStageUnboundedBlastBatch(next, changed, results, ref checks, timer, milliseconds, maxChecks))
                {
                    // A completed blast prefix keeps its original result ledger.
                    // Surface-only staging may fill the same captured window with
                    // the next FIFO command; an unfinished blast stays the barrier.
                    if (SurfaceOnly && _blastBatch == null) continue;
                    return;
                }
                if (command.Build && _cohortIndexPending) break;
                var checkpoint = CheckpointCommand(command);
                if (command.Build && command.Cells == null && FullyProtectedVerticalBuild(command))
                {
                    // A refused command still consumes stage work and completes via
                    // the ordinary result/publication path exactly once.
                    checks++; CommandWorkChecks++;
                    BuildsFullyProtected++;
                    command.Done = true; results.Add((command, default)); _commands.Dequeue();
                    continue;
                }
                if (!command.Build && !PrepareDestruction(command, timer, ref checks, milliseconds, maxChecks)) break;
                if (command.Build) command.Cells ??= BuildCandidates(command).GetEnumerator();
                var result = new BlockMutationResult(); bool limited = false;
                while (HasStageBudget(checks, maxChecks, timer, milliseconds))
                {
                    if (command.Build && checks + StaticWallAnchorMaxQueryChecks + 1 > maxChecks) { limited = true; break; }
                    checks++; CommandWorkChecks++;
                    if (command.Build && command.Total.Added + result.Added >= command.Count || !command.Build && command.Total.RemovedEnemy + result.RemovedEnemy >= command.MaxEnemy && (command.EnemyOnly || command.Total.RemovedFriendly + result.RemovedFriendly >= command.MaxFriendly)) { command.Done = true; break; }
                    if (command.Build)
                    {
                        if (!command.Cells.MoveNext()) { command.Done = true; break; }
                        var cell = command.Cells.Current; var key = BlockWorld.ChunkAt(cell);
                        checkpoint.Visited.AddLast(cell);
                        command.LastCandidate = cell; command.HasCandidate = true; BuildCandidatesVisited++;
                        if (!next.InBounds(cell)) { BuildOutsideBounds++; continue; }
                        if (next.TryGet(cell, out _)) { BuildAlreadyOccupied++; continue; }
                        if (SurfaceOnly ? !SurfaceBuildAllowed(cell, command.Normal) : MapOverlap(cell)) continue;
                        if (!TryCurrentBuildOverlap(cell, ref checks, maxChecks, timer, milliseconds, out bool movingOccupied))
                        { checkpoint.Visited.RemoveLast(); command.Cells = Prepend(cell, command.Cells).GetEnumerator(); limited = true; break; }
                        if (movingOccupied) { BuildMovingOccupied++; continue; }
                        if (!FitsBudget(_stagePublicationWindow, changed, cell)) { BuildDeferredByChunkBudget++; checkpoint.Visited.RemoveLast(); command.Cells = Prepend(cell, command.Cells).GetEnumerator(); limited = true; break; }
                        checks += StaticWallAnchorMaxQueryChecks; CommandWorkChecks += StaticWallAnchorMaxQueryChecks;
                        var value = BlockCell.Create(command.Team, SurfaceOnly || StaticMapWallAnchor(cell));
                        if (SurfaceOnly) value.Flags |= 2;
                        if (next.Set(cell, value)) { result.Added++; IncludeCell(changed, cell); QueueGravity(cell); if (!SurfaceOnly) QueueGravityStartupAddition(cell); RecordAddedBuildSlot(command, cell); }
                    }
                    else
                    {
                        if (command.TargetIndex >= command.Targets.Length) { command.Done = true; break; }
                        var target = command.Targets[command.TargetIndex]; bool friendly = target.Owner == command.Team;
                        if (friendly && command.Total.RemovedFriendly + result.RemovedFriendly >= command.MaxFriendly || !friendly && command.Total.RemovedEnemy + result.RemovedEnemy >= command.MaxEnemy)
                        { command.TargetIndex++; continue; }
                        if (target.Run != 0)
                        {
                            // Frozen source addresses remain valid across cohort fractures.
                            // Alias lookup is part of admission, including miss paths.
                            const int lookupCost = BlockWorld.MaxCohortPieceCells + 2;
                            if (checks + lookupCost > maxChecks || !HasStageBudget(checks, maxChecks, timer, milliseconds)) { limited = true; break; }
                            checks += lookupCost; CommandWorkChecks += lookupCost;
                            if (!next.TryGetFallingCell(target.Run, target.Index, out var run, out int resolvedIndex)) { command.TargetIndex++; continue; }
                            var column = new Vector2Int(run.Bottom.x, run.Bottom.z);
                            int motionCost = run.Cohort == null ? 0 : EstimateQueueMotionWork(new GravityMotionId(run.Cohort.Id, true), column);
                            int removeCost = next.EstimateRemoveFallingCellWork(target.Run, target.Index) + motionCost + EstimateQueueGravityWork(run.Bottom + Vector3Int.up * resolvedIndex);
                            if (checks + removeCost > maxChecks || !HasStageBudget(checks, maxChecks, timer, milliseconds)) { limited = true; break; }
                            checks += removeCost; CommandWorkChecks += removeCost; command.TargetIndex++;
                            if (run.Cells[resolvedIndex].Owner != target.Owner || !next.RemoveFallingCell(target.Run, target.Index, out _, out _)) continue;
                            QueueGravity(run.Bottom + Vector3Int.up * resolvedIndex);
                            if (run.Cohort != null) QueueMotion(new GravityMotionId(run.Cohort.Id, true), column, run.Cohort.Bottom.y, run.Cohort.TopExclusive);
                        }
                        else
                        {
                            if (!next.TryGet(target.Cell, out var own) || own.Owner != target.Owner) { command.TargetIndex++; continue; }
                            var key = BlockWorld.ChunkAt(target.Cell);
                            if (!FitsBudget(_stagePublicationWindow, changed, target.Cell)) { limited = true; break; }
                            command.TargetIndex++; next.Set(target.Cell, default); IncludeCell(changed, target.Cell); QueueGravity(target.Cell); if (!SurfaceOnly) QueueGravityStartupRemoval(target.Cell);
                        }
                        if (friendly) result.RemovedFriendly++; else result.RemovedEnemy++;
                    }
                }
                if (command.Build && command.Total.Added + result.Added >= command.Count) command.Done = true;
                if (!command.Build && command.TargetIndex >= command.Targets.Length) command.Done = true;
                results.Add((command, result));
                if (command.Done) _commands.Dequeue();
                else break;
                if (limited) break;
            }
        }
        static IEnumerable<Vector3Int> Prepend(Vector3Int cell, IEnumerator<Vector3Int> remaining)
        { yield return cell; while (remaining.MoveNext()) yield return remaining.Current; }
        bool PrepareDestruction(Command command, System.Diagnostics.Stopwatch timer, ref int checks, double milliseconds, int maxChecks)
        {
            if (command.Targets != null) return true;
            if (command.Sorting == null)
            {
                command.Prepared ??= new List<DestroyTarget>(); command.Preparing ??= DestroyCandidates(command).GetEnumerator();
                while (HasStageBudget(checks, maxChecks, timer, milliseconds))
                {
                    checks++; CommandWorkChecks++;
                    if (!command.Preparing.MoveNext())
                    {
                        command.Preparing.Dispose(); command.Preparing = null;
                        var captured = command.Prepared; command.Prepared = null;
                        if (captured.Count <= 64)
                        { command.Targets = captured.ToArray(); Array.Sort(command.Targets, CompareDestroyTargets); return true; }
                        // Only complete large blast sets may change deletion order.
                        // Any finite team quota retains its original nearest-first order.
                        bool groupByChunk = !command.EnemyOnly && command.MaxEnemy == int.MaxValue && command.MaxFriendly == int.MaxValue;
                        Comparison<DestroyTarget> compare = groupByChunk ? CompareDestroyTargetsBySourceChunk : CompareDestroyTargets;
                        command.Sorting = Task.Run(async () =>
                        {
                            using var admission = await WorkerAdmissions.EnterAsync(true, CancellationToken.None).ConfigureAwait(false);
                            if (!admission.TryEnter()) throw new InvalidOperationException("Destruction sort lost worker admission.");
                            var values = captured.ToArray(); Array.Sort(values, compare); return values;
                        });
                        break;
                    }
                    var target = command.Preparing.Current; if (target.Owner != 0) command.Prepared.Add(target);
                }
            }
            if (command.Sorting == null || !command.Sorting.IsCompleted) return false;
            if (command.Sorting.IsFaulted || command.Sorting.IsCanceled)
            { _stageTransformsSynced = false; FailedTransactions++; Debug.LogWarning("[BLOCK] Destruction candidate preparation failed."); command.Targets = Array.Empty<DestroyTarget>(); return true; }
            command.Targets = command.Sorting.Result; return true;
        }
        static int CompareDestroyTargetsBySourceChunk(DestroyTarget a, DestroyTarget b)
        {
            var x = BlockWorld.ChunkAt(a.Cell); var y = BlockWorld.ChunkAt(b.Cell);
            int order = x.x.CompareTo(y.x); if (order != 0) return order;
            order = x.y.CompareTo(y.y); if (order != 0) return order;
            order = x.z.CompareTo(y.z); return order != 0 ? order : CompareDestroyTargets(a, b);
        }
        static int CompareDestroyTargets(DestroyTarget a, DestroyTarget b)
        {
            int order = a.Distance.CompareTo(b.Distance); if (order != 0) return order;
            order = a.Cell.x.CompareTo(b.Cell.x); if (order != 0) return order;
            order = a.Cell.y.CompareTo(b.Cell.y); if (order != 0) return order;
            order = a.Cell.z.CompareTo(b.Cell.z); if (order != 0) return order;
            order = a.Run.CompareTo(b.Run); return order != 0 ? order : a.Index.CompareTo(b.Index);
        }
        const float ReservationBucketWidth = 4f, ReservationSafeCoordinate = 65536f;
        const int MaxReservationBucketsPerCommand = 64;
        readonly struct ReservationBucketRange
        {
            public readonly int MinX, MinZ, MaxX, MaxZ;
            public ReservationBucketRange(int minX, int minZ, int maxX, int maxZ)
            { MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ; }
        }
        static bool SafeReservationPoint(Vector2 point) => float.IsFinite(point.x) && float.IsFinite(point.y)
            && Mathf.Abs(point.x) <= ReservationSafeCoordinate && Mathf.Abs(point.y) <= ReservationSafeCoordinate;
        static bool TryReservationBucket(Vector2 point, out Vector2Int bucket)
        {
            bucket = default;
            if (!SafeReservationPoint(point)) return false;
            bucket = new Vector2Int((int)Math.Floor((double)point.x / ReservationBucketWidth),
                (int)Math.Floor((double)point.y / ReservationBucketWidth));
            return true;
        }
        static bool TryReservationBucketRange(Command command, out ReservationBucketRange range)
        {
            range = default;
            var point = new Vector2(command.Point.x, command.Point.z);
            float radius = Mathf.Max(command.Radius, command.EnemyOnly ? 0 : command.FriendlyRadius) + BlockRadiusPadding;
            if (!SafeReservationPoint(point) || !float.IsFinite(radius) || !float.IsFinite(radius * radius)) return false;
            // This is only a conservative candidate box. The original float
            // squared-distance predicate below remains the final authority.
            // In the safe coordinate domain subtraction error is less than 1m;
            // 2*r also leaves a wide margin around rounded positive squares.
            double reach = 2d * radius + 1d;
            double minX = Math.Floor(((double)point.x - reach) / ReservationBucketWidth);
            double minZ = Math.Floor(((double)point.y - reach) / ReservationBucketWidth);
            double maxX = Math.Floor(((double)point.x + reach) / ReservationBucketWidth);
            double maxZ = Math.Floor(((double)point.y + reach) / ReservationBucketWidth);
            if (!double.IsFinite(minX) || !double.IsFinite(minZ) || !double.IsFinite(maxX) || !double.IsFinite(maxZ)
                || minX <= int.MinValue || minZ <= int.MinValue || maxX >= int.MaxValue || maxZ >= int.MaxValue) return false;
            long width = (long)maxX - (long)minX + 1, height = (long)maxZ - (long)minZ + 1;
            if (width <= 0 || height <= 0 || width > MaxReservationBucketsPerCommand || height > MaxReservationBucketsPerCommand
                || width * height > MaxReservationBucketsPerCommand) return false;
            range = new ReservationBucketRange((int)minX, (int)minZ, (int)maxX, (int)maxZ);
            return true;
        }
        void RegisterReservation(Command command)
        {
            if (!TryReservationBucketRange(command, out var range)) { _reservationFallback.Add(command); return; }
            _reservationRanges.Add(command, range);
            for (int x = range.MinX; x <= range.MaxX; x++)
                for (int z = range.MinZ; z <= range.MaxZ; z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!_reservationBuckets.TryGetValue(key, out var commands)) _reservationBuckets.Add(key, commands = new HashSet<Command>());
                    commands.Add(command);
                }
        }
        void UnregisterReservation(Command command)
        {
            _reservationFallback.Remove(command);
            if (!_reservationRanges.TryGetValue(command, out var range)) return;
            _reservationRanges.Remove(command);
            for (int x = range.MinX; x <= range.MaxX; x++)
                for (int z = range.MinZ; z <= range.MaxZ; z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!_reservationBuckets.TryGetValue(key, out var commands)) continue;
                    commands.Remove(command);
                    if (commands.Count == 0) _reservationBuckets.Remove(key);
                }
        }
        bool ReservationsCoverColumn(Vector2 center, HashSet<Command> commands)
        {
            foreach (var command in commands)
            {
                if (command.Done || command.Completed) continue;
                ReservationCommandChecks++;
                float radius = Mathf.Max(command.Radius, command.EnemyOnly ? 0 : command.FriendlyRadius) + BlockRadiusPadding;
                if ((center - new Vector2(command.Point.x, command.Point.z)).sqrMagnitude <= radius * radius) return true;
            }
            return false;
        }
        bool ReservedColumn(Vector2Int column)
        {
            if (_reservedColumns.TryGetValue(column, out bool reserved)) return reserved;
            var center = new Vector2((column.x + .5f) * BlockWorld.CellSize, (column.y + .5f) * BlockWorld.CellSize);
            if (!TryReservationBucket(center, out var bucket)) reserved = ReservationsCoverColumn(center, _destructionReservations);
            else reserved = _reservationBuckets.TryGetValue(bucket, out var commands) && ReservationsCoverColumn(center, commands)
                || ReservationsCoverColumn(center, _reservationFallback);
            _reservedColumns[column] = reserved; return reserved;
        }
        bool StageGravity(BlockWorld next, HashSet<Vector3Int> changed, ref int checks, System.Diagnostics.Stopwatch timer, double milliseconds, int maxChecks)
        {
            if (SurfaceOnly) return false;
            bool mutation;
            if (_settleFirst)
            {
                mutation = StageFalling(next, changed, ref checks, timer, milliseconds, maxChecks);
                mutation |= StageUnstableColumns(next, changed, ref checks, timer, milliseconds, maxChecks);
            }
            else
            {
                mutation = StageUnstableColumns(next, changed, ref checks, timer, milliseconds, maxChecks);
                mutation |= StageFalling(next, changed, ref checks, timer, milliseconds, maxChecks);
            }
            // Use a different cadence from command/gravity priority so each gravity
            // operation also gets the first chunk slots during sustained combat.
            if (_gravityFirst) _settleFirst = !_settleFirst;
            return mutation;
        }
        bool StageUnstableColumns(BlockWorld next, HashSet<Vector3Int> changed, ref int checks, System.Diagnostics.Stopwatch timer, double milliseconds, int maxChecks)
        {
            if (_gravityJournal == null) StartGravityJournal(next);
            return StageCohortColumns(next, changed, ref checks, timer, milliseconds, maxChecks);
        }
        bool StageFalling(BlockWorld next, HashSet<Vector3Int> changed, ref int checks, System.Diagnostics.Stopwatch timer, double milliseconds, int maxChecks)
        {
            if (_gravityJournal == null) StartGravityJournal(next);
            return StageCohortFalling(next, changed, ref checks, timer, milliseconds, maxChecks);
        }
        static int CompareFallingWork(FallingWork a, FallingWork b)
        { int order = a.Height.CompareTo(b.Height); return order != 0 ? order : a.Id.CompareTo(b.Id); }
        void SelectReadyRuns(BlockWorld world)
        {
            _readyRuns.Clear(); _fallingColumnHeads.Clear();
            // Falling runs in one column cannot pass each other. Float subtraction
            // can order their equally clamped visual heights backwards. Only the
            // lowest occupied source run may become a settlement candidate.
            foreach (var run in world.Falling.Values)
            {
                FallingSelectionChecks++;
                int first = 0; while (first < run.Cells.Length && !run.Cells[first].Occupied) first++;
                if (first == run.Cells.Length) continue;
                var column = new Vector2Int(run.Bottom.x, run.Bottom.z); int lowest = run.Bottom.y + first;
                if (!_fallingColumnHeads.TryGetValue(column, out var head) || lowest < head.lowest ||
                    lowest == head.lowest && run.Id < head.id)
                    _fallingColumnHeads[column] = (run.Id, lowest);
            }
            foreach (var run in world.Falling.Values)
            {
                var column = new Vector2Int(run.Bottom.x, run.Bottom.z);
                if (!_fallingColumnHeads.TryGetValue(column, out var head) || head.id != run.Id) continue;
                if (ReservedColumn(column)) continue;
                if (run.LandingKnown && !LandingDirty(run) && run.Offset(_now) + .001f < run.LandingOffset) continue;
                var work = new FallingWork(run, _now);
                if (_readyRuns.Count < 64)
                {
                    _readyRuns.Add(work); int child = _readyRuns.Count - 1;
                    while (child > 0)
                    { int parent = (child - 1) / 2; if (CompareFallingWork(_readyRuns[parent], _readyRuns[child]) >= 0) break;
                        (_readyRuns[parent], _readyRuns[child]) = (_readyRuns[child], _readyRuns[parent]); child = parent; }
                }
                else if (CompareFallingWork(work, _readyRuns[0]) < 0)
                {
                    _readyRuns[0] = work; int parent = 0;
                    while (parent * 2 + 1 < _readyRuns.Count)
                    { int child = parent * 2 + 1; if (child + 1 < _readyRuns.Count && CompareFallingWork(_readyRuns[child + 1], _readyRuns[child]) > 0) child++;
                        if (CompareFallingWork(_readyRuns[parent], _readyRuns[child]) >= 0) break;
                        (_readyRuns[parent], _readyRuns[child]) = (_readyRuns[child], _readyRuns[parent]); parent = child; }
                }
            }
            _readyRuns.Sort(CompareFallingWork);
        }
        void FindLanding(BlockWorld world, FallingRun run)
        {
            int firstOccupied = 0; while (firstOccupied < run.Cells.Length && !run.Cells[firstOccupied].Occupied) firstOccupied++;
            if (firstOccupied == run.Cells.Length) return;
            var lowest = run.Bottom + Vector3Int.up * firstOccupied;
            var currentPosition = BlockWorld.Center(lowest) - Vector3.up * run.Offset(_now);
            int current = Mathf.FloorToInt(currentPosition.y / BlockWorld.CellSize);
            int minimum = Mathf.CeilToInt(world.Bottom / BlockWorld.CellSize);
            int hits = Physics.RaycastNonAlloc(currentPosition, Vector3.down, _rayHits, Mathf.Max(0, currentPosition.y - world.Bottom + BlockWorld.CellSize), SurfaceMapMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++) if (_staticColliders.Contains(_rayHits[i].collider) && _rayHits[i].normal.y > .2f)
                minimum = Math.Max(minimum, StaticLandingCellY(_rayHits[i].point.y));
            int landing = minimum;
            for (int y = Math.Min(lowest.y, current); y >= minimum; y--)
            {
                var candidate = new Vector3Int(run.Bottom.x, y, run.Bottom.z);
                // A previous run can settle into this run's old, already-clamped landing
                // position in the same transaction. It must become support, not a permanent
                // overlap that makes Settle reject this run forever.
                if (world.TryGet(candidate, out _)) { landing = y + 1; break; }
                if (world.TryGet(candidate + Vector3Int.down, out _)) { landing = y; break; }
            }
            bool occupied;
            do
            {
                occupied = false;
                for (int i = firstOccupied; i < run.Cells.Length; i++)
                {
                    if (!run.Cells[i].Occupied) continue;
                    var destination = new Vector3Int(run.Bottom.x, landing + i - firstOccupied, run.Bottom.z);
                    if (!world.TryGet(destination, out _)) continue;
                    landing = destination.y + 1 - (i - firstOccupied); occupied = true; break;
                }
            } while (occupied && landing < Mathf.RoundToInt(BlockWorld.Top / BlockWorld.CellSize));
            run.LandingKnown = true; run.LandingOffset = Mathf.Max(0, (lowest.y - landing) * BlockWorld.CellSize);
        }
        void BeginTransaction(BlockWorld next, HashSet<Vector3Int> changed, bool remote, List<(Command, BlockMutationResult)> results)
            => BeginWindowTransaction(next, changed, remote, results, remote
                ? _publicationPolicy.Capture(BlockPublicationOperation.Metadata) : CaptureLegacyPublicationWindow(results));
        void BeginWindowTransaction(BlockWorld next, HashSet<Vector3Int> changed, bool remote,
            List<(Command, BlockMutationResult)> results, BlockPublicationWindow window)
        {
            if (window == null || !remote && changed.Count > window.MaximumChangedKeys)
                throw new InvalidOperationException("The complete terrain footprint cannot fit its captured publication window.");
            if (!remote && results != null && RequiresBuildOccupancyGuard())
            { BeginBuildOccupancyPreparation(next, changed, results, window); return; }
            var pending = new Pending { Gravity = !remote && _gravityJournal != null && ReferenceEquals(_gravityJournal.Next, next) ? _gravityJournal : null, Epoch = _epoch, CreatedAt = _now, Next = next, Window = window, Changed = changed.OrderBy(k => k.x).ThenBy(k => k.y).ThenBy(k => k.z).ToArray(), Remote = remote };
            pending.CommandCheckpoints = _stageCommandCheckpoints; _stageCommandCheckpoints = null;
            pending.StaticProofs = pending.Gravity?.StaticProofs;
            if (ReferenceEquals(pending.Gravity, _gravityJournal)) _gravityJournal = null;
            pending.TrackWall = PendingTransactionFinished != null || remote && RemoteTransferObservationEnabled;
            if (pending.TrackWall) { pending.WallId = ++_pendingWallId; pending.CreatedWallStamp = PendingWallNow(); _pendingWork.created++; }
            if (results != null) pending.Results.AddRange(results);
            pending.ForegroundWork = remote || pending.Results.Count != 0 || (pending.CommandCheckpoints?.Count ?? 0) != 0;
            TagFairPending(pending);
            ApplyDependentPublicationContext(pending);
            ProtectBuildOverlapRepairPending(pending);
            var keys = pending.Changed;
            // Headless peers still publish the same collision world and scores.
            // Their null graphics device has no consumer for render geometry.
            bool needsRenderMesh = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            pending.Pieces = new Prepared[keys.Length]; pending.PieceUploaded = new bool[keys.Length];
            // Keep potentially large remote-key enumeration off main. Each piece
            // enters Task.Run before WaitAsync, even when the semaphore is available.
            pending.Generate = Task.Run(async () =>
            {
                if (pending.TrackWall) pending.WorkerGroupEnteredStamp = PendingWallNow();
                var started = new List<Task<Prepared>>(keys.Length);
                try
                {
                    foreach (var key in keys)
                        started.Add(Task.Run(async () =>
                        {
                            long awaitStart = pending.TrackWall ? PendingWallNow() : 0;
                            using var admission = await WorkerAdmissions.EnterAsync(pending.ForegroundWork, pending.AdmissionStop.Token).ConfigureAwait(false);
                            if (!admission.TryEnter()) throw new OperationCanceledException(pending.AdmissionStop.Token);
                            long acquired = pending.TrackWall ? PendingWallNow() : 0;
                            return PrepareGenerationPiece(next, key, needsRenderMesh, pending.TrackWall, awaitStart, acquired);
                        }));
                    var tasks = started.ToArray();
                    pending.Scheduled.SetResult(tasks);
                    return await FinishGenerationGroup(pending, tasks).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    pending.Scheduled.TrySetException(exception);
                    // A scheduling exception still joins every successfully started worker.
                    try { await Task.WhenAll(started).ConfigureAwait(false); } catch (Exception) { }
                    throw;
                }
            });
            _pending = pending;
            // Finite scans can finish a stage without producing a pending
            // transaction. The retry owns its original lease until an
            // actual local continuation takes that same window.
            if (!remote && ReferenceEquals(_gravityRetryPublicationWindow, window)) _gravityRetryPublicationWindow = null;
        }
        static async Task<Prepared[]> FinishGenerationGroup(Pending pending, Task<Prepared>[] tasks)
        {
            var pieces = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (pending.TrackWall) pending.WorkerGroupFinishedStamp = PendingWallNow();
            return pieces;
        }
        static Exception CompletedFailure(Task task)
        {
            if (!task.IsCompleted) return null;
            if (task.IsFaulted) return task.Exception;
            return task.IsCanceled ? new TaskCanceledException(task) : null;
        }
        static void ObserveFault(Task task) { if (task != null && task.IsFaulted) _ = task.Exception; }
        void AdvancePending(bool allowPublication)
        {
            var pending = _pending; if (pending == null) return;
            ProtectBuildOverlapRepairPending(pending);
            if (pending.Epoch != _epoch) { pending.AbortReason = PendingAbortReason.EpochChanged; AbortPending(); return; }
            if (!ValidateFairPending(pending)) return;
            if (pending.PrivateGravity && (_remoteQueued != null || !ReferenceEquals(World, pending.PrivateSource)
                || Revision != pending.PrivateSourceRevision || Round != pending.PrivateSourceRound || World.MutationGeneration != pending.PrivateSourceMutation))
            { pending.AbortReason = PendingAbortReason.PrivateSourceChanged; PrivatePreparationAborts++; AbortPending(); return; }
            var failure = CompletedFailure(pending.Generate);
            if (failure != null) { Fail(pending, failure); return; }
            // Retain the original completed-generation observation even when the
            // following Upload fails. Early pieces still do not require this gate.
            if (pending.Generate.Status == TaskStatus.RanToCompletion && pending.TrackWall && pending.GenerationObservedWallStamp == 0)
                pending.GenerationObservedWallStamp = PendingWallNow();
            if (pending.Generating == null)
            {
                var scheduled = pending.Scheduled.Task;
                if (!scheduled.IsCompleted) { if (pending.TrackWall) _pendingWork.generateNotReady++; return; }
                failure = CompletedFailure(scheduled);
                if (failure != null) { Fail(pending, failure); return; }
                pending.Generating = scheduled.Result;
            }
            // Observe an already-started Bake fault without waiting for other generation.
            foreach (var piece in pending.Pieces)
                if (piece?.Bake != null && (failure = CompletedFailure(piece.Bake)) != null) { Fail(pending, failure); return; }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int visited = 0;
            while (visited++ < pending.Generating.Length && pending.Uploaded < pending.Pieces.Length && timer.Elapsed.TotalMilliseconds < 1)
            {
                int index = pending.UploadScanCursor; pending.UploadScanCursor = (index + 1) % pending.Generating.Length;
                if (pending.PieceUploaded[index]) continue;
                var generated = pending.Generating[index];
                if (!generated.IsCompleted) continue;
                failure = CompletedFailure(generated);
                if (failure != null) { Fail(pending, failure); return; }
                // Task completion is the memory barrier; no shared partial result is read.
                var piece = generated.Result; pending.Pieces[index] = piece;
                using var marker = UploadCpu.Auto();
                if (pending.TrackWall)
                {
                    piece.UploadStartStamp = PendingWallNow();
                    pending.UploadedBeforeGroupCompletion |= !pending.Generate.IsCompleted;
                }
                try
                {
                    if (piece.Collision.Triangles.Length == 0)
                    { piece.Render = piece.Collision = null; piece.Bake = Task.CompletedTask; }
                    else
                    {
                        piece.RenderMesh = piece.Render?.Upload("Block render " + piece.Key); piece.CollisionMesh = piece.Collision.Upload("Block collision " + piece.Key);
                        // Native setters own copied arrays; Bake owns Mesh until its task ends.
                        piece.Render = piece.Collision = null;
                        int id = piece.CollisionMesh.GetInstanceID();
                        piece.Bake = Task.Run(async () =>
                        {
                            using var admission = await WorkerAdmissions.EnterAsync(pending.ForegroundWork, pending.AdmissionStop.Token).ConfigureAwait(false);
                            if (!admission.TryEnter()) throw new OperationCanceledException(pending.AdmissionStop.Token);
                            try { if (pending.TrackWall) piece.BakeStartStamp = PendingWallNow(); Physics.BakeMesh(id, false, Cooking); }
                            finally { if (pending.TrackWall) piece.BakeEndStamp = PendingWallNow(); }
                        });
                        piece.View = MakeView(piece);
                    }
                    pending.PieceUploaded[index] = true; pending.Uploaded++;
                }
                catch (Exception exception) { Fail(pending, exception); return; }
                finally { if (pending.TrackWall) piece.UploadEndStamp = PendingWallNow(); }
            }
            // Group completion remains a final correctness/diagnostic gate, not an upload barrier.
            if (!pending.Generate.IsCompleted) { if (pending.TrackWall) _pendingWork.generateNotReady++; return; }
            failure = CompletedFailure(pending.Generate);
            if (failure != null) { Fail(pending, failure); return; }
            if (pending.TrackWall && pending.GenerationObservedWallStamp == 0) pending.GenerationObservedWallStamp = PendingWallNow();
            if (pending.Uploaded < pending.Pieces.Length) { if (pending.TrackWall) _pendingWork.uploadRemaining++; return; }
            // The old five disjoint wall fields remain observation prefixes/tails.
            // Earlier overlapping upload/Bake work remains in GenerationObservedMs;
            // Pipeline separately reports actual activity and overlap windows, without subtraction.
            if (pending.TrackWall && pending.UploadCompleteWallStamp == 0)
                pending.UploadCompleteWallStamp = pending.Pieces.Length == 0 ? pending.GenerationObservedWallStamp : PendingWallNow();
            if (pending.Pieces.Any(p => p.Bake == null || !p.Bake.IsCompleted)) { if (pending.TrackWall) _pendingWork.bakeNotReady++; return; }
            foreach (var piece in pending.Pieces)
                if ((failure = CompletedFailure(piece.Bake)) != null) { Fail(pending, failure); return; }
            if (pending.TrackWall && pending.BakeObservedWallStamp == 0)
                pending.BakeObservedWallStamp = pending.ReadyWallStamp = pending.Pieces.Length == 0 ? pending.GenerationObservedWallStamp : PendingWallNow();
            if (!allowPublication) { if (pending.TrackWall) _pendingWork.readyAwaitTick++; return; }
            if (_budgetFrame != Time.frameCount) { _budgetFrame = Time.frameCount; _publishedThisFrame = 0; }
            // Complete captured-window admission occurs after Publish preflight,
            // immediately before any visible collider is disabled.
            Publish(pending);
        }
        View MakeView(Prepared piece)
        {
            var obj = new GameObject("Block chunk " + piece.Key) { layer = TerrainLayer }; obj.SetActive(false);
            obj.transform.SetParent(transform, false); obj.transform.localPosition = (Vector3)BlockWorld.ChunkOrigin(piece.Key) * BlockWorld.CellSize;
            var collider = obj.AddComponent<MeshCollider>(); collider.convex = false; collider.cookingOptions = Cooking;
            if (piece.RenderMesh != null)
            {
                obj.AddComponent<MeshFilter>().sharedMesh = piece.RenderMesh;
                obj.AddComponent<MeshRenderer>().sharedMaterial = _material;
            }
            return new View { Object = obj, Collider = collider, RenderMesh = piece.RenderMesh, CollisionMesh = piece.CollisionMesh };
        }
        void Publish(Pending pending)
        {
            using var marker = PublishCpu.Auto();
            if (pending.TrackWall) pending.PublishEnterWallStamp = PendingWallNow();
            // Revalidate the exact static support query before any visible swap.
            // The locked source still supplies all stable-cell facts.
            if (!ValidateSpreadPublication(pending)) { RetrySpreadPublication(pending); return; }
            if (!ValidateOwnActivationProofs(pending)) { RetryOwnActivationPublication(pending); return; }
            if (!ValidateStaticProofs(pending.StaticProofs))
            { StaticProofInvalidations++; Fail(pending, new InvalidOperationException("Static support changed after private landing proof.")); return; }
            if (!ValidateGravitySupportPublication(pending)) { RetryGravitySupportPublication(pending); return; }
            if (!ValidateBuildPublication(pending)) { RetryBuildPublication(pending); return; }
            if (!ValidateHistoricalRecoveryPublication(pending)) { RetryHistoricalRecoveryPublication(pending); return; }
            if (!ValidateGravityBindingPublication(pending)) { RetryGravityBindingPublication(pending); return; }
            EnsurePublicationFrame();
            // A remote full install is indivisible and is not a local attack
            // key window. Its complete Changed array is retained and measured.
            if (!_publicationFrameBudget.TryReserve(pending.Window, pending.Remote ? 0 : pending.Changed.Length,
                out var reservation, out var admission))
            {
                if (admission == BlockPublicationAdmission.InvalidWindow || admission == BlockPublicationAdmission.AnotherAtomicAdmissionActive)
                { Fail(pending, new InvalidOperationException("Invalid captured publication admission: " + admission)); return; }
                if (admission == BlockPublicationAdmission.DeferCpuUntilNextFrame) PublicationCpuDeferrals++;
                else if (pending.TrackWall) _pendingWork.frameChunkBudgetBlocked++;
                return;
            }
            using var reservationScope = reservation;
            if (Dynamic != null && pending.Pieces.Length != 0)
            {
                _changingSupportBounds.Clear();
                foreach (var piece in pending.Pieces)
                {
                    if (_views.TryGetValue(piece.Key, out var prior) && prior.Collider != null)
                        _changingSupportBounds.Add(prior.Collider.bounds);
                    // Inactive new colliders have empty native bounds. Transform
                    // their uploaded mesh bounds before enabling any geometry.
                    if (piece.View != null && piece.CollisionMesh != null)
                    {
                        var bounds = piece.CollisionMesh.bounds;
                        var matrix = piece.View.Object.transform.localToWorldMatrix;
                        var extents = Abs(matrix.MultiplyVector(new Vector3(bounds.extents.x, 0, 0)))
                            + Abs(matrix.MultiplyVector(new Vector3(0, bounds.extents.y, 0)))
                            + Abs(matrix.MultiplyVector(new Vector3(0, 0, bounds.extents.z)));
                        _changingSupportBounds.Add(new Bounds(matrix.MultiplyPoint3x4(bounds.center), extents * 2));
                    }
                }
                Dynamic.NotifySupportGeometryChanging(_changingSupportBounds);
            }
            // No query, event, coroutine or await may occur between these swaps.
            foreach (var piece in pending.Pieces) if (_views.TryGetValue(piece.Key, out var prior)) { prior.Collider.enabled = false; prior.Object.SetActive(false); }
            try
            {
                foreach (var piece in pending.Pieces) if (piece.View != null) { piece.View.Collider.sharedMesh = piece.CollisionMesh; piece.View.Object.SetActive(true); }
                if (!pending.Remote) pending.Next.ActivateRebasePendingCohorts(_now);
                ApplyBuildPublicationCaps(pending);
                ApplyGravityBindingContinuity(pending);
            }
            catch (Exception exception)
            {
                foreach (var piece in pending.Pieces) { if (piece.View != null) piece.View.Object.SetActive(false); if (_views.TryGetValue(piece.Key, out var prior)) { prior.Object.SetActive(true); prior.Collider.enabled = true; } }
                Physics.SyncTransforms(); Fail(pending, exception); return;
            }
            var oldWorld = World;
            World = pending.Next;
            ResetBuildOccupancyQueryCache();
            if (pending.Remote) { ResetGravityCohorts(); ResetBlockSpread(_authority && _accepting); } else CommitGravityJournal(pending.Gravity);
            foreach (var piece in pending.Pieces)
            {
                if (_views.Remove(piece.Key, out var prior)) { _colliderChunks.Remove(prior.Collider); Dispose(prior); }
                if (piece.View != null) { _views.Add(piece.Key, piece.View); _colliderChunks.Add(piece.View.Collider, piece.Key); }
                piece.View = null; piece.RenderMesh = piece.CollisionMesh = null;
            }
            Physics.SyncTransforms(); _pending = null; _publishedThisFrame += pending.Changed.Length; PublishedChunks += pending.Changed.Length;
            // Native work may overrun; charge it once without a new post-swap rejection.
            reservation.Commit();
            CommitBuildOverlapRepair(pending);
            // Retain a successful paid partial before any completion callback
            // can clear the round and release all queued command leases.
            foreach (var pair in pending.Results)
                if (!pair.command.Done && pair.command.OrdinaryMainAttack && pair.result.Changed > 0)
                    pair.command.RetryWindow ??= pending.Window;
            // The world/collider commit is complete. Observe before recovery or
            // primary callbacks, whose existing exception propagation is preserved.
            FairPublicationCommitted(pending);
            DisposePendingAdmissions(pending);
            RecordPendingTerminal(pending, PendingWallOutcome.Published);
            HistoricalRecoveryPublished(pending);
            RecoverEmbeddedPlayers();
            foreach (var pair in pending.Results)
            {
                var command = pair.command; command.Total.Added += pair.result.Added; command.Total.RemovedEnemy += pair.result.RemovedEnemy; command.Total.RemovedFriendly += pair.result.RemovedFriendly; command.Total.Revision = Revision;
                if (command.Done) Complete(command, command.Total);
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var remotePublicationScope = ObserveRemotePublicationScope(pending);
#endif
            PublishedCommit?.Invoke(new BlockTerrainCommit(Revision, Round, pending.Changed));
        }
        void Complete(Command command, BlockMutationResult result)
        {
            if (command.Completed) return; command.Completed = true; command.RetryWindow = null;
            CompleteSpread(command);
            CommandsCompleted++;
            bool removedReservation = _destructionReservations.Remove(command);
            if (removedReservation) { UnregisterReservation(command); _reservedColumns.Clear(); }
            ObserveFairCommandCompletion(removedReservation);
            result.OrdinaryMainAttack = command.OrdinaryMainAttack;
            var callback = command.Callback; command.Callback = null; command.Cells?.Dispose(); command.Preparing?.Dispose();
            command.Source = null; command.Prepared = null; command.Targets = null; command.Sorting = null;
            if (result.Changed > 0) MutationCompleted?.Invoke(result, Math.Max(0, _now - command.CreatedAt) * 1000);
            callback?.Invoke(result);
        }
        void Fail(Pending pending, Exception exception)
        {
            if (pending.TrackWall) pending.WallFailure = true;
            RecordPendingTerminal(pending, PendingWallOutcome.Failed);
            FailedTransactions++; Debug.LogWarning("[BLOCK] Atomic terrain transaction rejected: " + exception?.Message);
            // Failed publication discards its gravity journal; committed proofs retry unchanged.
            foreach (var pair in pending.Results) { if (_commands.Count > 0 && ReferenceEquals(_commands.Peek(), pair.command)) _commands.Dequeue(); Complete(pair.command, pair.command.Total); }
            AbortPendingCore(PreserveFairSeedAfterFailure(pending));
        }
        void AbortPending() => AbortPendingCore(false);
        void AbortPendingCore(bool preserveFairSeed)
        {
            CancelBuildOverlapRepair();
            if (!preserveFairSeed) CancelFairScheduling();
            AbortPrivatePreparation();
            if (_pending == null) return;
            if (_pending.RecoveryGuard != null) LastHistoricalRecoveryOutcome = HistoricalRecoveryOutcome.Cancelled;
            RecordPendingTerminal(_pending, _pending.WallFailure ? PendingWallOutcome.Failed : PendingWallOutcome.Aborted);
            _pending.AdmissionStop.Cancel();
            foreach (var pair in _pending.Results) Complete(pair.command, default); _retired.Enqueue(new Retired { Pending = _pending }); _pending = null;
        }
        static void DisposePendingAdmissions(Pending pending)
        {
            if (Interlocked.Exchange(ref pending.AdmissionsDisposed, 1) == 0) pending.AdmissionStop.Dispose();
        }
        void CleanupRetired()
        {
            int count = _retired.Count;
            while (count-- > 0 && _retired.Count != 0)
            {
                var retired = _retired.Dequeue(); var pending = retired.Pending;
                if (!pending.Generate.IsCompleted || pending.Pieces != null && pending.Pieces.Any(p => p?.Bake != null && !p.Bake.IsCompleted)) { _retired.Enqueue(retired); continue; }
                ObserveFault(pending.Generate); ObserveFault(pending.Scheduled.Task);
                if (pending.Pieces != null) foreach (var piece in pending.Pieces)
                {
                    if (piece == null) continue; ObserveFault(piece.Bake);
                    if (piece.View != null) Dispose(piece.View);
                    else { DestroyOwnedObject(piece.RenderMesh); DestroyOwnedObject(piece.CollisionMesh); }
                }
                DisposePendingAdmissions(pending);
                ObserveFairRetiredCompletion(pending);
            }
        }
        static void DestroyOwnedObject(UnityEngine.Object obj) { if (obj == null) return; if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); }
        static void Dispose(View view)
        {
            if (view == null) return;
            // Clear publishes an empty world before deferred Play Mode destruction.
            // Withdraw collision and visibility synchronously; keep mesh retirement unchanged.
            if (view.Collider != null) view.Collider.enabled = false;
            if (view.Object != null) view.Object.SetActive(false);
            DestroyOwnedObject(view.Object); DestroyOwnedObject(view.RenderMesh); DestroyOwnedObject(view.CollisionMesh);
        }

        internal static bool IsBlockGeometry(Collider collider)
        {
            if (collider == null) return false;
            var owner = collider.GetComponentInParent<BlockTerrain>();
            // Production attaches this component to the arena root, which also
            // contains authored map colliders. Only the registered static map
            // is exempt; unknown/unpublished block descendants remain blocked.
            return owner != null && !owner._staticColliders.Contains(collider);
        }
        public bool TryContact(RaycastHit hit, out BlockContact contact) => TryContact(hit.collider, hit.point, hit.normal, out contact);
        public bool TryContact(Collider collider, Vector3 point, Vector3 normal, out BlockContact contact)
        {
            contact = default; if (!Initialized || collider == null) return false;
            if (collider == _fallProxy)
            {
                Vector3 sample = point - normal * .002f;
                foreach (var run in World.Falling.Values)
                {
                    var bottom = BlockWorld.Center(run.Bottom) - Vector3.up * run.Offset(_now);
                    var runBounds = new Bounds(bottom + Vector3.up * ((run.Cells.Length - 1) * BlockWorld.CellSize * .5f),
                        new Vector3(BlockWorld.CellSize, run.Cells.Length * BlockWorld.CellSize, BlockWorld.CellSize) + Vector3.one * .001f);
                    if (!runBounds.Contains(sample)) continue;
                    for (int i = 0; i < run.Cells.Length; i++)
                    {
                        var fallingCell = run.Cells[i]; if (!fallingCell.Occupied) continue; var p = bottom + Vector3.up * (i * BlockWorld.CellSize);
                        if (!new Bounds(p, Vector3.one * (BlockWorld.CellSize + .001f)).Contains(sample)) continue;
                        contact = new BlockContact { Owner = fallingCell.Owner, Point = point, Normal = normal, OrientationNormal = normal,
                            Cell = run.Bottom + Vector3Int.up * i, Stable = false, Revision = Revision }; return true;
                    }
                }
                return false;
            }
            if (!_colliderChunks.ContainsKey(collider)) return false;
            var cell = BlockWorld.CellAt(point - normal * .002f);
            if (!World.TryGet(cell, out var own)) return false;
            contact = new BlockContact { Owner = own.Owner, Point = point, Normal = normal, OrientationNormal = QueryOrientation(point, normal, own.Owner), Stable = true,
                Kind = own.SurfaceAttached ? BlockContactKind.SurfaceAttached : BlockContactKind.Legacy,
                Climbable = Mathf.Abs(normal.y) < .7f, Cell = cell, Chunk = BlockWorld.ChunkAt(cell), Revision = World.ChunkRevision(BlockWorld.ChunkAt(cell)) };
            return true;
        }
        public bool TryGetGround(Vector3 feet, out BlockContact contact, float slopeLimit = 45)
        {
            contact = default;
            int mask = SurfaceOnly ? PlayerMotorSimulation.WorldMask : PlayerMotorSimulation.WorldMask | (1 << TerrainLayer);
            return Physics.Raycast(feet + Vector3.up * .08f, Vector3.down, out var hit, .16f, mask, QueryTriggerInteraction.Ignore)
                && hit.normal.y >= Mathf.Cos(slopeLimit * Mathf.Deg2Rad) &&
                (TrySurfaceContactFromMap(hit, out contact) || TryContact(hit, out contact));
        }
        public Vector3 QueryOrientation(Vector3 point, Vector3 physicalNormal, byte team, float footprint = .35f)
        {
            if (!Initialized || physicalNormal.y < .7f) return physicalNormal;
            var center = BlockWorld.CellAt(point - physicalNormal * .002f); if (!World.TryGet(center, out var own) || own.Owner != team) return physicalNormal;
            int range = Mathf.Clamp(Mathf.CeilToInt(footprint / BlockWorld.CellSize), 1, 8);
            float left = SurfaceHeight(center, Vector3Int.left, range, team), right = SurfaceHeight(center, Vector3Int.right, range, team);
            float back = SurfaceHeight(center, new Vector3Int(0, 0, -1), range, team), front = SurfaceHeight(center, new Vector3Int(0, 0, 1), range, team);
            var normal = new Vector3(-(right - left) / (2 * range * BlockWorld.CellSize), 1, -(front - back) / (2 * range * BlockWorld.CellSize)).normalized;
            return normal.y >= Mathf.Cos(45 * Mathf.Deg2Rad) ? normal : physicalNormal;
        }
        float SurfaceHeight(Vector3Int center, Vector3Int direction, int range, byte team)
        {
            var at = center; float height = (center.y + 1) * BlockWorld.CellSize;
            for (int i = 0; i < range; i++)
            {
                var next = at + direction; bool found = false;
                for (int dy = 1; dy >= -1; dy--)
                { var candidate = next + Vector3Int.up * dy; if (World.TryGet(candidate, out var own) && own.Owner == team && !World.TryGet(candidate + Vector3Int.up, out _)) { at = candidate; height = (at.y + 1) * BlockWorld.CellSize; found = true; break; } }
                if (!found) break;
            }
            return height;
        }
        public bool RaycastFalling(Vector3 origin, Vector3 direction, float distance, float radius, out BlockFallingHit hit, byte? ignoreTeam = null)
        {
            hit = default; if (!Initialized || direction.sqrMagnitude < .00001f) return false;
            direction.Normalize(); var ray = new Ray(origin, direction); float nearest = distance; bool found = false;
            foreach (var run in World.Falling.Values)
            {
                var bottom = BlockWorld.Center(run.Bottom) - Vector3.up * run.Offset(_now);
                var bounds = new Bounds(bottom + Vector3.up * ((run.Cells.Length - 1) * BlockWorld.CellSize * .5f), new Vector3(BlockWorld.CellSize, run.Cells.Length * BlockWorld.CellSize, BlockWorld.CellSize) + Vector3.one * (radius * 2));
                float entry = 0; if (!bounds.Contains(origin) && !bounds.IntersectRay(ray, out entry) || entry > nearest) continue;
                for (int i = 0; i < run.Cells.Length; i++)
                {
                    var own = run.Cells[i]; if (!own.Occupied || ignoreTeam.HasValue && own.Owner == ignoreTeam.Value) continue;
                    var p = bottom + Vector3.up * (i * BlockWorld.CellSize);
                    if (!BlockSphereBoxSweep.TrySweep(origin, direction, nearest, radius,
                        new Bounds(p, Vector3.one * BlockWorld.CellSize), out float d, out var point, out var normal)) continue;
                    nearest = d;
                    hit = new BlockFallingHit { Owner = own.Owner, Point = point, Normal = normal, Distance = nearest, RunId = run.Id,
                        Cell = run.Bottom + Vector3Int.up * i, Collider = _fallProxy }; found = true;
                }
            }
            return found;
        }
        void LateUpdate()
        {
            if (!Initialized || _material == null || World.Falling.Count == 0 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsInstancing) return;
            using var marker = FallingRenderCpu.Auto();
            BeginGravityStartupPresentation();
            int count = 0;
            foreach (var run in World.Falling.Values)
            {
                float offset = run.Offset(_now);
                bool queuedStartupPresentation = false;
                for (int i = 0; i < run.Cells.Length; i++)
                {
                    byte team = run.Cells[i].Owner; if (team == 0) continue;
                    var p = BlockWorld.Center(run.Bottom + Vector3Int.up * i) - Vector3.up * offset;
                    _fallMatrices[count] = Matrix4x4.TRS(p, Quaternion.identity, Vector3.one * BlockWorld.CellSize);
                    _fallColors[count] = team == 1 ? PrototypeArena.Pink : PrototypeArena.Blue; count++;
                    if (!queuedStartupPresentation) { QueueGravityStartupPresentation(run, offset); queuedStartupPresentation = true; }
                    if (count == _fallMatrices.Length) { DrawFalling(count); count = 0; }
                }
            }
            if (count > 0) DrawFalling(count);
            CommitGravityStartupPresentation(_now);
        }
        void DrawFalling(int count)
        {
            _fallProperties.SetVectorArray("_TeamColor", _fallColors);
            Graphics.DrawMeshInstanced(_fallMesh, 0, _material, _fallMatrices, count, _fallProperties, ShadowCastingMode.On, true, TerrainLayer);
        }
        async void OnDestroy()
        {
            ClearSafetyState();
            ResetBuildQueriesCache();
            ResetBuildOccupancyQueryCache();
            ResetBlockSpread();
            _destroying = true; Dynamic?.DisposeWorld(); _epoch++; AbortPending(); _blastBatch = null;
            ObserveRemoteInstallDiscarded(_remoteQueued, "DiscardedDestroy");
            while (_commands.Count > 0) Complete(_commands.Dequeue(), default);
            foreach (var view in _views.Values) Dispose(view); _views.Clear();
            DestroyOwnedObject(_material); DestroyOwnedObject(_fallMesh);
            if (Current == this) Current = null;
            // BakeMesh owns mesh data until its task ends. Destruction must not free it early.
            foreach (var retired in _retired.ToArray())
            {
                try { await retired.Pending.Generate; } catch (Exception) { }
                // A generation fault must never bypass already-started Bake ownership.
                try { if (retired.Pending.Pieces != null) await Task.WhenAll(retired.Pending.Pieces.Where(p => p?.Bake != null).Select(p => p.Bake)); }
                catch (Exception) { }
            }
            CleanupRetired();
        }
    }
}
