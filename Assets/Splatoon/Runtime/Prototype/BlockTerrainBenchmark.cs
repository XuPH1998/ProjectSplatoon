using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Prototype
{
    /// <summary>Explicit Development Player benchmark of normal main-camera frames and real terrain publication.</summary>
    public sealed class BlockTerrainBenchmark : MonoBehaviour
    {
        public static bool ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!Requested && InputMode.Length == 0) return false;
            var state = player.Snapshot.Value;
            input.Move = Vector2.zero; input.Swim = input.Fire = false;
            input.CancelFire = false;
            bool active = _instance != null && _instance._begin >= 0 && !_instance._done;
            if (!Requested) active = PrototypeMatch.Current != null && PrototypeMatch.Current.InitialSyncComplete;
            bool combat = Case == "combat250k" || Case == "steady250k" || InputMode == "combat";
            var direction = (Case == "tower32m" ? new Vector3(0, 16, 4.5f) : new Vector3(state.Position.x, .2f, 0))
                - (state.Position + Vector3.up * 1.5f);
            input.Look = new Vector2(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg,
                -Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg);
            input.Fire = active && combat && !state.AttackNeedsRelease && state.IsAlive;
            return true;
#else
            return false;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static string Arg(string key, string fallback)
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        static double Number(string key, double fallback)
            => double.TryParse(Arg(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number) && number > 0 ? number : fallback;
        static readonly string Case = Arg("-blockBenchmark", "");
        static readonly string InputMode = Arg("-blockBenchmarkInput", "");
        static readonly string AcceptanceProfile = Arg("-blockAcceptanceProfile", "strict");
        static bool PrototypeProfile => AcceptanceProfile == "prototype-v1";
        public static readonly bool Requested = Array.IndexOf(Environment.GetCommandLineArgs(), "-blockBenchmark") >= 0;
        static BlockTerrainBenchmark _instance;

        [Serializable] public sealed class Metric
        {
            public string name;
            public bool available;
            public int samples;
            public double p50, p95, p99, max, mean;
        }
        [Serializable] public sealed class Gate
        {
            public string name, status, detail;
            public double value, limit;
            public bool diagnosticOnly;
        }
        [Serializable] public sealed class Actor
        {
            public string player;
            public uint shots;
        }
        [Serializable] public sealed class QueueHeadSample
        {
            public double seconds;
            public int pending, unstableColumns;
            public uint terrainRevision;
            public BlockTerrain.CommandHeadDiagnostic command;
            public BlockTerrain.BuildWorkDiagnostics buildWork;
            public BlockTerrain.PendingWorkDiagnostics pendingWork;
            public BlockTerrain.PendingHeadDiagnostic pendingTransaction;
            public BlockTerrain.PublicationRetryDiagnostics publicationRetry;
        }
        [Serializable] public sealed class Spike
        {
            public int frame, pendingTransactions, unstableColumns;
            public uint terrainRevision;
            public bool queueSnapshotAvailable, mainThreadAvailable, renderThreadAvailable, gcAvailable;
            public bool terrainTickAvailable, terrainPublishAvailable, terrainUploadAvailable, fallingRenderAvailable;
            public double seconds, frameIntervalMs, mainThreadMs, renderThreadMs, gcBytes, queueAgeMs;
            public double terrainTickMs, terrainPublishMs, terrainUploadMs, fallingRenderMs;
        }
        [Serializable] public sealed class Result
        {
            public string scenario, role, stage, unity, device, cpu, reason, environment, qualityName, graphicsApi, operatingSystem;
            public string renderingThreadingMode, renderTimingSource, mainCameraCapture;
            public string acceptanceProfile, acceptanceScope;
            public double ordinaryPublishP95LimitMs, blastCompletionLimitMs;
            public bool frameSpikeBlocking, supportQueueClearBlocking, strictPerformancePassed;
            public int qualityLevel, vSyncCount, targetFrameRate, cpuCores, systemMemoryMb, graphicsMemoryMb;
            public int screenWidth, screenHeight, displayWidth, displayHeight;
            public bool complete, passed, graphics, fixtureReady;
            public int width, height, samples, renderedFrames, framesWithoutMainCamera, minimumRealPlayers;
            public int initialBlocks, minimumBlocks, maximumBlocks, finalBlocks, peakFallingBlocks, detachedBlocks;
            public int peakPending, peakUnstableColumns, failedTransactions, errors, longFrames33, longFrames50;
            public int blastsIssued, blastsCompleted, blastsWithRemoval, supportsIssued, supportsRemoved;
            public double seconds, requiredSeconds, requestedSeconds, queueAgeMaxMs, blastCompletionMs, supportQueueClearMs, cascadeCompleteMs;
            public float fixtureTopMeters, blastEnemyRadius, blastFriendlyRadius;
            public int fixtureColumns;
            public bool finalColliderAttempted, finalColliderComplete, finalColliderPassed;
            public int finalColliderCells, finalColliderFaces, finalColliderOriginalColumns, finalColliderMismatches;
            public uint finalColliderRevision;
            public double finalColliderSimulationTime, finalColliderMs;
            public string finalColliderFirstFailure;
            public float fixtureBottomMeters, fixtureMinColumnHeightMeters, fixtureMaxColumnHeightMeters;
            public long terrainPublications, publishedChunks, gcTotalBytes;
            public long authorityStages, commandWorkChecks, gravityWorkChecks, fallingSelectionChecks, reservationCommandChecks;
            public long commandProgressStages, gravityProgressStages;
            public double eightActorShootingCoverage;
            public double gpuTimingCoverage, mainTimingCoverage, renderTimingCoverage;
            public Metric[] metrics;
            public Gate[] gates;
            public Actor[] actors;
            public BlockTerrain.PublicationRetryDiagnostics publicationRetryAtStart, publicationRetryAtEnd;
            public BlockTerrain.BuildWorkDiagnostics buildWorkAtStart, buildWorkAtEnd;
            public BlockTerrain.PendingWorkDiagnostics pendingWorkAtStart, pendingWorkAtEnd;
            public BlockTerrain.PendingHeadDiagnostic pendingAtStart, pendingAtEnd;
            public long pendingWindowWallTimestamp, pendingWindowEndWallTimestamp, pendingWallClockFrequency;
            public long pendingCompletionSamples, pendingCensoredCompletions, pendingRetainedCompletions, pendingDroppedCompletions;
            public long pendingPublishedCompletions, pendingFailedCompletions, pendingAbortedCompletions;
            public long pendingFullyObservedPublishedCompletions, pendingPartialPublishedCompletions;
            public long pendingRetainedFullyObservedPublishedCompletions, pendingRetainedPartialPublishedCompletions;
            public int pendingWallRecordCap;
            public string pendingPhaseWallSemantics = "disjoint observation boundaries: generation prefix includes early Upload/Bake; upload and Bake fields are the remaining observed tails, not complete activity";
            public string pendingWorkerCriticalInnerIntervalSemantics = "legacy InnerTaskDispatch name; interval is semaphore-acquired to BuildBodyStart; no inner Task.Run is scheduled by this pipeline";
            public string pendingWorkerMeshSplitSemantics = "Rendered pieces: RenderBuild includes shared exposure scan and render emission/output; CollisionBuild begins at renderReady and includes scratch clear, collision emission/output and scratch return. Collision-only pieces use legacy Build, including its scan. These are elapsed wall regions, not CPU time; individual regions across versions do not represent equal work.";
            public string pendingPipelineOverlaySemantics = "piece elapsed sums and first-to-last overlap windows; may contain gaps/concurrency; not CPU time and not additive to phase wall or each other";
            public long pendingPipelineAvailableCompletions, pendingPipelineMissingCompletions, pendingPipelineEarlyUploadCompletions;
            public long pendingWorkerAvailableCompletions, pendingWorkerMissingCompletions;
            public long pendingWorkerPieces, pendingWorkerRenderedPieces, pendingWorkerRenderQuads, pendingWorkerCollisionQuads;
            public long pendingWorkerOutputArrayLogicalBytes;
            public double pendingWorkerSemaphoreAwaitElapsedSumMs, pendingWorkerRenderBuildElapsedSumMs, pendingWorkerCollisionBuildElapsedSumMs;
            public QueueHeadSample[] queueHeads;
            public Spike[] spikes;
        }
        sealed class Series
        {
            public readonly string Name;
            public readonly List<double> Values;
            public ProfilerRecorder Recorder;
            public double Sum, Max;
            public double LastFrameValue;
            public bool LastFrameAvailable;
            int _lastRecorderCount;
            public readonly double Scale;
            public Series(string name, double scale = 1, int capacity = 65536) { Name = name; Scale = scale; Values = new List<double>(capacity); }
            public void Add(double value) { Values.Add(value); Sum += value; Max = Math.Max(Max, value); }
            public double Record(bool requirePositive = false)
            {
                LastFrameValue = 0;
                LastFrameAvailable = Recorder.Valid && Recorder.IsRunning;
                if (!LastFrameAvailable) return 0;
                int count = Recorder.Count; bool fresh = count > _lastRecorderCount; _lastRecorderCount = count;
                double value = fresh ? Recorder.LastValue * Scale : 0;
                LastFrameAvailable = fresh && double.IsFinite(value) && value >= 0 && (!requirePositive || value > 0);
                // Count stops advancing when a wrapping buffer is full. Clear only this
                // recorder's history so old values cannot masquerade as fresh timings.
                if (count >= Recorder.Capacity) { Recorder.Reset(); Recorder.Start(); _lastRecorderCount = 0; }
                if (!LastFrameAvailable) return 0;
                LastFrameValue = value; Add(value); return value;
            }
            public Metric Finish(bool percentiles)
            {
                double[] sorted = percentiles ? Values.ToArray() : Array.Empty<double>();
                if (percentiles) Array.Sort(sorted);
                double At(double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * q) - 1)];
                return new Metric { name = Name, available = Values.Count > 0, samples = Values.Count,
                    p50 = At(.5), p95 = At(.95), p99 = At(.99), max = Max, mean = Values.Count > 0 ? Sum / Values.Count : 0 };
            }
        }
        sealed class ActorSample
        {
            public uint Previous, Shots;
            public double LastShot = -1;
        }
        readonly List<Series> _series = new();
        readonly Dictionary<ulong, ActorSample> _actors = new();
        readonly HashSet<ulong> _seenRuns = new();
        readonly List<Spike> _spikes = new(64);
        readonly List<QueueHeadSample> _queueHeads = new(720);
        readonly List<Vector3Int> _supports = new();
        readonly List<Vector3> _blastPoints = new();
        readonly HashSet<Collider> _map = new();
        readonly RaycastHit[] _hits = new RaycastHit[64];
        readonly Collider[] _fixtureOverlaps = new Collider[128];
        readonly List<Bounds> _fixtureProtection = new();
        readonly List<Bounds> _fixtureActors = new();
        readonly Dictionary<Vector2Int, bool> _fixtureProtectedColumns = new();
        readonly FrameTiming[] _timings = new FrameTiming[1];
        BlockFrameTimingObservation _frameTimingObservation;
        BlockBenchmarkCpuWindowCapture _cpuWindowCapture;
        BlockBenchmarkReportObservation _reportObservation;
        Series _frames, _gpu, _gc, _main, _render, _frameMain, _frameRender, _tick, _publish, _upload, _falling, _latency, _ordinaryLatency;
        // Transaction series use existing aggregation without entering the frame
        // recorder loop. Bound whole records; no per-cell/per-poll samples.
        const int PendingWallRecordCap = 16384;
        readonly Series[] _pendingMetrics = {
            new("pendingGenerationObservedWallMs", capacity: PendingWallRecordCap), new("pendingUploadWallMs", capacity: PendingWallRecordCap),
            new("pendingBakeObservedTailWallMs", capacity: PendingWallRecordCap), new("pendingReadyToPublishWaitWallMs", capacity: PendingWallRecordCap),
            new("pendingPublishToCommitWallMs", capacity: PendingWallRecordCap), new("pendingPublishedToCommitTotalWallMs", capacity: PendingWallRecordCap),
            new("pendingFailedToRejectTotalWallMs", capacity: PendingWallRecordCap), new("pendingAbortedToAbortTotalWallMs", capacity: PendingWallRecordCap) };
        readonly Series[] _pendingWorkerMetrics = {
            new("pendingGenerationCriticalInitialDispatchWallMs", capacity: PendingWallRecordCap),
            new("pendingGenerationCriticalSemaphoreAwaitElapsedMs", capacity: PendingWallRecordCap),
            new("pendingGenerationCriticalInnerTaskDispatchWallMs", capacity: PendingWallRecordCap),
            new("pendingGenerationCriticalRenderBuildElapsedMs", capacity: PendingWallRecordCap),
            new("pendingGenerationCriticalCollisionBuildElapsedMs", capacity: PendingWallRecordCap),
            new("pendingGenerationCriticalBuildBodyOtherWallMs", capacity: PendingWallRecordCap),
            new("pendingGenerationGroupJoinTailWallMs", capacity: PendingWallRecordCap),
            new("pendingGenerationCompletionToMainObservedWallMs", capacity: PendingWallRecordCap) };
        readonly Series[] _pendingPipelineMetrics = {
            new("pendingPipelineFirstUploadFromCreatedWallMs", capacity: PendingWallRecordCap),
            new("pendingPipelineUploadPieceElapsedSumMs", capacity: PendingWallRecordCap),
            new("pendingPipelineBakePieceElapsedSumMs", capacity: PendingWallRecordCap),
            new("pendingPipelineUploadGenerationOverlapWindowMs", capacity: PendingWallRecordCap),
            new("pendingPipelineBakeGenerationOverlapWindowMs", capacity: PendingWallRecordCap) };
        void OnRetainedPendingPipeline(BlockTerrain.PendingPipelineDiagnostic pipeline)
        {
            if (!pipeline.Available) { _result.pendingPipelineMissingCompletions++; return; }
            _result.pendingPipelineAvailableCompletions++;
            if (pipeline.UploadedBeforeGroupCompletion) _result.pendingPipelineEarlyUploadCompletions++;
            _pendingPipelineMetrics[0].Add(pipeline.FirstUploadFromCreatedMs);
            _pendingPipelineMetrics[1].Add(pipeline.UploadPieceActiveElapsedMs);
            _pendingPipelineMetrics[2].Add(pipeline.BakeActiveElapsedMs);
            _pendingPipelineMetrics[3].Add(pipeline.UploadGenerationOverlapWindowMs);
            _pendingPipelineMetrics[4].Add(pipeline.BakeGenerationOverlapWindowMs);
        }
        void OnRetainedPendingWorker(BlockTerrain.PendingGenerationWorkerDiagnostic worker)
        {
            if (!worker.Available) { _result.pendingWorkerMissingCompletions++; return; }
            _result.pendingWorkerAvailableCompletions++;
            _pendingWorkerMetrics[0].Add(worker.InitialToCriticalAwaitMs);
            _pendingWorkerMetrics[1].Add(worker.CriticalSemaphoreAwaitMs);
            _pendingWorkerMetrics[2].Add(worker.CriticalInnerDispatchMs);
            _pendingWorkerMetrics[3].Add(worker.CriticalRenderBuildMs);
            _pendingWorkerMetrics[4].Add(worker.CriticalCollisionBuildMs);
            _pendingWorkerMetrics[5].Add(worker.CriticalBodyOtherMs);
            _pendingWorkerMetrics[6].Add(worker.GroupJoinTailMs);
            _pendingWorkerMetrics[7].Add(worker.CompletionToMainObservedMs);
            foreach (var piece in worker.Pieces)
            {
                _result.pendingWorkerPieces++; if (piece.Rendered) _result.pendingWorkerRenderedPieces++;
                _result.pendingWorkerRenderQuads += piece.RenderIndices / 6;
                _result.pendingWorkerCollisionQuads += piece.CollisionIndices / 6;
                _result.pendingWorkerOutputArrayLogicalBytes += piece.OutputArrayLogicalBytes;
                // Sums over pieces overlap because two slots run concurrently.
                // Do not add these totals to critical-path wall spans.
                _result.pendingWorkerSemaphoreAwaitElapsedSumMs += piece.SemaphoreAwaitElapsedMs;
                _result.pendingWorkerRenderBuildElapsedSumMs += piece.RenderBuildElapsedMs;
                _result.pendingWorkerCollisionBuildElapsedSumMs += piece.CollisionBuildElapsedMs;
            }
        }
        long _pendingWindowWallStart;
        Result _result;
        BlockTerrain _terrain;
        double _boot, _begin = -1, _ready = -1, _duration, _requiredDuration, _timeout, _warmup;
        double _nextReport, _nextProbe, _attackBegin = -1;
        double _nextWrite;
        double _nextQueueHead;
        ulong _lastGpuTimestamp;
        uint _seedRevision;
        uint _queueRevision;
        int _queueFrame = -1, _queuePending, _queueUnstable;
        double _queueAgeMs;
        long _stagesAtStart, _commandChecksAtStart, _gravityChecksAtStart, _selectionAtStart, _reservationsAtStart;
        long _commandProgressAtStart, _gravityProgressAtStart;
        int _target, _activeFrames, _activityFrames, _renderedAtLastSample, _renderedFrame = -1, _failedAtStart;
        bool _seeding, _seeded, _done, _attackIssued, _fixtureFailed, _pendingReportFinal, _finalReportWritten;
        bool _captureRequested;
        BlockBackbufferObservation _backbufferObservation;
        string _output, _pendingReport, _fixtureFailureReason;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Requested) return;
            var go = new GameObject("Block terrain benchmark"); DontDestroyOnLoad(go); go.AddComponent<BlockTerrainBenchmark>();
        }
        void Awake()
        {
            _instance = this; _boot = Time.realtimeSinceStartupAsDouble;
            _requiredDuration = Case switch { "steady250k" => PrototypeProfile ? 180 : 600, "combat250k" => 300, "tower32m" => 60,
                "blast8" => 120, "cascade20k" => 120, "scattered100k" => 180, _ => 0 };
            _duration = Number("-blockBenchmarkDuration", _requiredDuration > 0 ? _requiredDuration : 1);
            _timeout = Number("-blockBenchmarkTimeout", 300); _warmup = Number("-blockBenchmarkWarmup", 10);
            _target = Case == "scattered100k" ? 100000 : Case == "combat250k" || Case == "steady250k" || Case == "blast8" ? 250000 : 0;
            _output = Path.GetFullPath(Arg("-blockBenchmarkOutput", "Reports/BlockTerrain/Performance/" + Case + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json"));
            _result = new Result { scenario = Case, stage = "waiting", unity = Application.unityVersion,
                acceptanceProfile = AcceptanceProfile,
                acceptanceScope = PrototypeProfile ? "First playable release: full workloads and frame percentiles; rare full-frame spikes and extreme support-clear latency remain recorded risks. This is not strict stable-60 acceptance."
                    : "Original strict performance gates, including the full-frame spike and extreme support-clear limits.",
                ordinaryPublishP95LimitMs = PrototypeProfile ? 200 : 80, blastCompletionLimitMs = PrototypeProfile ? 2000 : 250,
                frameSpikeBlocking = !PrototypeProfile, supportQueueClearBlocking = !PrototypeProfile,
                device = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType,
                graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, requiredSeconds = _requiredDuration,
                qualityLevel = QualitySettings.GetQualityLevel(), qualityName = QualitySettings.names[QualitySettings.GetQualityLevel()],
                vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                cpuCores = SystemInfo.processorCount, systemMemoryMb = SystemInfo.systemMemorySize, graphicsMemoryMb = SystemInfo.graphicsMemorySize,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), operatingSystem = SystemInfo.operatingSystem,
                renderingThreadingMode = SystemInfo.renderingThreadingMode.ToString(),
                screenWidth = Screen.width, screenHeight = Screen.height, displayWidth = Screen.currentResolution.width, displayHeight = Screen.currentResolution.height,
                requestedSeconds = _duration, minimumRealPlayers = int.MaxValue, minimumBlocks = int.MaxValue,
                blastCompletionMs = -1, supportQueueClearMs = -1, cascadeCompleteMs = -1,
                environment = "Normal main-camera Development Player frames; process/device identity does not establish physical multi-machine validation." };
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-blockBenchmarkFrameTimingObservation") >= 0)
                _frameTimingObservation = new BlockFrameTimingObservation(_output + ".frame-timing-observation.json", _result);
            _cpuWindowCapture = BlockBenchmarkCpuWindowCapture.CreateIfRequested(_output);
            _reportObservation = BlockBenchmarkReportObservation.CreateIfRequested(_output);
            _series.Add(_frames = new Series("frameIntervalMs")); _series.Add(_gpu = new Series("gpuFrameMs"));
            _main = AddRecorder("mainThreadMs", ProfilerCategory.Internal, "Main Thread");
            _render = AddRecorder("renderThreadMs", ProfilerCategory.Internal, "Render Thread");
            _gc = AddRecorder("gcBytes", ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            _tick = AddRecorder("terrainTickMs", ProfilerCategory.Scripts, "Splatoon.Blocks.Tick");
            _publish = AddRecorder("terrainPublishMs", ProfilerCategory.Scripts, "Splatoon.Blocks.Publish");
            _upload = AddRecorder("terrainUploadMs", ProfilerCategory.Scripts, "Splatoon.Blocks.Upload");
            _falling = AddRecorder("fallingRenderMs", ProfilerCategory.Scripts, "Splatoon.Blocks.FallingRender");
            AddRecorder("terrainTransferEncodeMs", ProfilerCategory.Scripts, "Splatoon.Blocks.TransferEncode");
            AddRecorder("terrainTransferSendMs", ProfilerCategory.Scripts, "Splatoon.Blocks.TransferSend");
            _series.Add(_frameMain = new Series("frameTimingCpuMainThreadMs"));
            _series.Add(_frameRender = new Series("frameTimingCpuRenderThreadMs"));
            _series.Add(_latency = new Series("allTerrainCommandPublishLatencyMs"));
            _series.Add(_ordinaryLatency = new Series("ordinaryMainAttackPublishLatencyMs"));
            Application.logMessageReceived += OnLog;
            RenderPipelineManager.endCameraRendering += OnCamera;
            Camera.onPostRender += OnBuiltinCamera;
            _backbufferObservation = BlockBackbufferObservation.CreateIfRequested(_output, Case);
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            WriteReport(false);
            if (AcceptanceProfile != "strict" && !PrototypeProfile) Finish(false, "Unknown acceptance profile. Use strict or prototype-v1.");
            if (_requiredDuration == 0) Finish(false, "Unknown case. Use steady250k, combat250k, tower32m, blast8, cascade20k, or scattered100k.");
        }
        Series AddRecorder(string name, ProfilerCategory category, string marker, double scale = .000001)
        {
            var series = new Series(name, scale) { Recorder = ProfilerRecorder.StartNew(category, marker, 16384,
                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame) };
            _series.Add(series); return series;
        }
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _result.errors++;
                _cpuWindowCapture?.AbortOnErrorLog(_result.samples);
            }
        }
        void OnCamera(ScriptableRenderContext context, Camera camera) => CountRender(camera);
        void OnBuiltinCamera(Camera camera) => CountRender(camera);
        IEnumerator CaptureMainCamera()
        {
            yield return new WaitForEndOfFrame();
            string path = Path.GetFullPath(Arg("-blockBenchmarkCapture", ""));
            if (_begin >= 0 || Camera.main == null || Camera.main.targetTexture != null || Screen.width != 1920 || Screen.height != 1080) yield break;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            ScreenCapture.CaptureScreenshot(path);
            _result.mainCameraCapture = path;
        }
        void CountRender(Camera camera)
        {
            _backbufferObservation?.ObserveCamera(camera, _begin, _done, _result.samples);
            if (_begin < 0 || _done || camera != Camera.main || _renderedFrame == Time.frameCount) return;
            _renderedFrame = Time.frameCount; _result.renderedFrames++;
            _queueFrame = Time.frameCount; _queuePending = _terrain.PendingTransactions;
            _queueUnstable = _terrain.UnstableColumns; _queueRevision = _terrain.Revision;
            _queueAgeMs = _terrain.OldestPendingSeconds * 1000;
        }
        void OnMutation(BlockMutationResult result, double latency)
        {
            if (_begin < 0 || _done) return;
            _latency.Add(latency);
            // Only actual primary main-attack contacts qualify. Trails, diagnostic probes,
            // blast commands, sub weapons and specials retain the default false marker.
            if (result.OrdinaryMainAttack && result.Changed > 0) _ordinaryLatency.Add(latency);
        }
        void OnPendingTransaction(BlockTerrain.PendingWallDiagnostic sample)
        {
            if (_begin < 0 || _done) return;
            _result.pendingCompletionSamples++;
            if (sample.CreatedWallTimestamp < _pendingWindowWallStart)
            { _result.pendingCensoredCompletions++; return; }
            bool published = sample.Outcome == BlockTerrain.PendingWallOutcome.Published;
            bool fullyObserved = published && sample.GenerationObserved && sample.UploadCompleted && sample.BakeObserved && sample.PublishEntered;
            if (published)
            {
                _result.pendingPublishedCompletions++;
                if (fullyObserved) _result.pendingFullyObservedPublishedCompletions++;
                else _result.pendingPartialPublishedCompletions++;
            }
            else if (sample.Outcome == BlockTerrain.PendingWallOutcome.Failed) _result.pendingFailedCompletions++;
            else _result.pendingAbortedCompletions++;
            // Cap whole terminal records so every retained complete success has
            // the same phase population. Overflow never changes formal gates.
            if (_result.pendingRetainedCompletions >= PendingWallRecordCap)
            { _result.pendingDroppedCompletions++; return; }
            _result.pendingRetainedCompletions++;
            if (sample.Outcome == BlockTerrain.PendingWallOutcome.Published)
            {
                if (!fullyObserved) { _result.pendingRetainedPartialPublishedCompletions++; return; }
                _result.pendingRetainedFullyObservedPublishedCompletions++;
                _pendingMetrics[0].Add(sample.GenerationObservedMs);
                _pendingMetrics[1].Add(sample.UploadMs);
                _pendingMetrics[2].Add(sample.BakeObservedTailMs);
                _pendingMetrics[3].Add(sample.ReadyWaitMs);
                _pendingMetrics[4].Add(sample.PublicationMs);
                _pendingMetrics[5].Add(sample.TotalMs);
                OnRetainedPendingWorker(sample.Worker);
                OnRetainedPendingPipeline(sample.Pipeline);
            }
            else if (sample.Outcome == BlockTerrain.PendingWallOutcome.Failed) _pendingMetrics[6].Add(sample.TotalMs);
            else _pendingMetrics[7].Add(sample.TotalMs);
        }

        void OnCommit(BlockTerrainCommit commit)
        {
            if (_begin < 0 || _done) return;
            _result.terrainPublications++; _result.publishedChunks += commit.ChangedChunks.Length;
            if (Case != "cascade20k" && Case != "tower32m") return;
            foreach (var run in _terrain.World.Falling.Values) if (_seenRuns.Add(run.Id))
                foreach (var cell in run.Cells) if (cell.Occupied) _result.detachedBlocks++;
        }
        void Update()
        {
            try { UpdateBenchmark(); }
            catch (Exception)
            {
                _cpuWindowCapture?.Close("benchmark_update_exception", _result != null ? _result.samples : 0);
                _cpuWindowCapture?.Write();
                throw;
            }
        }
        void UpdateBenchmark()
        {
            if (_pendingReport != null && Time.realtimeSinceStartupAsDouble >= _nextWrite) TryWriteReport();
            if (_done)
            {
                _reportObservation?.WriteIfFinalWritten(_finalReportWritten);
                _frameTimingObservation?.ReadTail(Time.frameCount, Time.realtimeSinceStartupAsDouble);
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble; var match = PrototypeMatch.Current;
            if (now - _boot > _timeout && _begin < 0) { Finish(false, "Readiness/fixture timeout."); return; }
            bool ready = match != null && match.IsSpawned && match.InitialSyncComplete && BlockTerrain.Current != null
                && BlockTerrain.Current.Initialized && match.State.Value.Phase == MatchPhase.Practice;
            if (!ready) { if (_begin >= 0) Finish(false, "Match or terrain stopped being ready, or left Practice."); return; }
            int realPlayers = CountPlayers();
            if (realPlayers != 8)
            { _ready = -1; if (_begin >= 0) Finish(false, "Expected eight real player objects throughout sampling."); return; }
            if (_terrain == null)
            {
                _terrain = BlockTerrain.Current; _terrain.MutationCompleted += OnMutation; _terrain.PublishedCommit += OnCommit;
                _terrain.PendingTransactionFinished += OnPendingTransaction;
                _result.role = match.IsServer ? "host" : "client";
            }
            if (match.IsServer)
            {
                foreach (var player in PrototypePlayer.ByOwner.Values)
                {
                    if (player == null || !player.IsSpawned || player.IsTestBot) continue;
                    var state = player.Snapshot.Value;
                    if (state.Ink < 90 || state.ProtectedUntil < match.NetworkManager.ServerTime.Time + 10)
                    { state.Ink = 100; state.ProtectedUntil = match.NetworkManager.ServerTime.Time + 30; player.Snapshot.Value = state; }
                }
                if (!_seeding && !_seeded) { _seeding = true; _result.stage = "fixture"; StartCoroutine(CreateFixture(match)); }
            }
            if (_fixtureFailed) { Finish(false, _fixtureFailureReason ?? "Fixture could not satisfy the requested workload in this map."); return; }
            int blocks = _terrain.PinkCount + _terrain.BlueCount;
            bool fixtureReady = _begin >= 0 || (match.IsServer ? _seeded && _terrain.Revision >= _seedRevision
                : blocks >= (_target > 0 ? _target : Case == "cascade20k" ? 20100 : 20000));
            if (!fixtureReady) { _ready = -1; return; }
            _result.fixtureReady = true;
            if (_ready < 0) _ready = now;
            if (_begin < 0)
            {
                if (!_captureRequested && Arg("-blockBenchmarkCapture", "").Length > 0)
                { _captureRequested = true; StartCoroutine(CaptureMainCamera()); }
                if (now - _ready < _warmup)
                {
                    _frameTimingObservation?.ReadWarmup(false, Time.frameCount, now);
                    return;
                }
                _frameTimingObservation?.ReadWarmup(true, Time.frameCount, now);
                _backbufferObservation?.Close(); _backbufferObservation = null;
                _begin = now; _nextReport = now + 30; _nextProbe = now + 1;
                _result.stage = "sampling"; _result.width = Screen.width; _result.height = Screen.height;
                _result.initialBlocks = _result.minimumBlocks = blocks; _result.minimumRealPlayers = 8;
                _failedAtStart = _terrain.FailedTransactions;
                _stagesAtStart = _terrain.AuthorityStageCount; _commandChecksAtStart = _terrain.CommandWorkChecks;
                _gravityChecksAtStart = _terrain.GravityWorkChecks; _selectionAtStart = _terrain.FallingSelectionChecks;
                _reservationsAtStart = _terrain.ReservationCommandChecks;
                _commandProgressAtStart = _terrain.CommandProgressStages; _gravityProgressAtStart = _terrain.GravityProgressStages;
                _result.buildWorkAtStart = _terrain.InspectBuildWork(); _nextQueueHead = now;
                _result.publicationRetryAtStart = _terrain.InspectPublicationRetry();
                _pendingWindowWallStart = _result.pendingWindowWallTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _result.pendingWallClockFrequency = System.Diagnostics.Stopwatch.Frequency;
                _frameTimingObservation?.StartWindow(Time.frameCount, now, _pendingWindowWallStart);
                _result.pendingWallRecordCap = PendingWallRecordCap;
                _result.pendingWorkAtStart = _terrain.InspectPendingWork();
                _result.pendingAtStart = _terrain.InspectPendingTransaction();
                CaptureActors(now, false); _renderedAtLastSample = _result.renderedFrames;
                _cpuWindowCapture?.StartWindow(_result.role, Time.frameCount, _begin, _pendingWindowWallStart);
                Debug.Log("[BLOCK-BENCHMARK] Sampling " + Case + ", " + _duration.ToString(CultureInfo.InvariantCulture) + " seconds; full gate requires " + _requiredDuration + ".");
                return;
            }
            if (Screen.width != 1920 || Screen.height != 1080 || Camera.main == null || Camera.main.targetTexture != null)
            { Finish(false, "Normal main-camera backbuffer must remain 1920x1080."); return; }
            _result.samples++; _result.seconds = now - _begin;
            _cpuWindowCapture?.ObserveSample(_result.samples);
            _result.minimumRealPlayers = Math.Min(_result.minimumRealPlayers, realPlayers);
            _result.minimumBlocks = Math.Min(_result.minimumBlocks, blocks); _result.maximumBlocks = Math.Max(_result.maximumBlocks, blocks);
            _result.finalBlocks = blocks; _result.peakFallingBlocks = Math.Max(_result.peakFallingBlocks, _terrain.FallingCount);
            _result.peakPending = Math.Max(_result.peakPending, _terrain.PendingTransactions);
            _result.peakUnstableColumns = Math.Max(_result.peakUnstableColumns, _terrain.UnstableColumns);
            _result.queueAgeMaxMs = Math.Max(_result.queueAgeMaxMs, _terrain.OldestPendingSeconds * 1000);
            _result.authorityStages = _terrain.AuthorityStageCount - _stagesAtStart;
            _result.commandWorkChecks = _terrain.CommandWorkChecks - _commandChecksAtStart;
            _result.gravityWorkChecks = _terrain.GravityWorkChecks - _gravityChecksAtStart;
            _result.fallingSelectionChecks = _terrain.FallingSelectionChecks - _selectionAtStart;
            _result.reservationCommandChecks = _terrain.ReservationCommandChecks - _reservationsAtStart;
            _result.commandProgressStages = _terrain.CommandProgressStages - _commandProgressAtStart;
            _result.gravityProgressStages = _terrain.GravityProgressStages - _gravityProgressAtStart;
            if (now >= _nextQueueHead)
            {
                _nextQueueHead = now + 1;
                _queueHeads.Add(new QueueHeadSample { seconds = now - _begin, pending = _terrain.PendingTransactions,
                    unstableColumns = _terrain.UnstableColumns, terrainRevision = _terrain.Revision,
                    command = _terrain.InspectCommandHead(), buildWork = _terrain.InspectBuildWork(),
                    pendingWork = _terrain.InspectPendingWork(), pendingTransaction = _terrain.InspectPendingTransaction(),
                    publicationRetry = _terrain.InspectPublicationRetry() });
            }
            if (_result.renderedFrames == _renderedAtLastSample) _result.framesWithoutMainCamera++;
            _renderedAtLastSample = _result.renderedFrames;
            double ms = Time.unscaledDeltaTime * 1000.0; _frames.Add(ms);
            for (int i = 2; i < _series.Count - 2; i++)
                if (_series[i] != _frameMain && _series[i] != _frameRender) _series[i].Record(i == 2 || i == 3);
            _result.gcTotalBytes += _gc.LastFrameAvailable ? (long)_gc.LastFrameValue : 0;
            if (ms > 33.333) { _result.longFrames33++; CaptureSpike(now, ms); }
            if (ms > 50) _result.longFrames50++;
            var timingPoll = _frameTimingObservation != null
                ? _frameTimingObservation.BeginSample(Time.frameCount, _result.samples, now, now - _begin, _lastGpuTimestamp, _gpu.Values.Count)
                : default;
            FrameTimingManager.CaptureFrameTimings();
            if (timingPoll.retained) timingPoll.captureWallEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            uint timingCount = FrameTimingManager.GetLatestTimings(1, _timings);
            if (timingPoll.retained) timingPoll.formalReadWallEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            if (timingCount > 0 && _timings[0].frameStartTimestamp != _lastGpuTimestamp)
            {
                var timing = _timings[0]; _lastGpuTimestamp = timing.frameStartTimestamp;
                if (double.IsFinite(timing.gpuFrameTime) && timing.gpuFrameTime > 0) _gpu.Add(timing.gpuFrameTime);
                if (double.IsFinite(timing.cpuMainThreadFrameTime) && timing.cpuMainThreadFrameTime > 0) _frameMain.Add(timing.cpuMainThreadFrameTime);
                if (double.IsFinite(timing.cpuRenderThreadFrameTime) && timing.cpuRenderThreadFrameTime > 0) _frameRender.Add(timing.cpuRenderThreadFrameTime);
            }
            if (timingPoll.retained) _frameTimingObservation.EndSample(ref timingPoll, timingCount, _timings[0], _lastGpuTimestamp, _gpu.Values.Count);
            CaptureActors(now, true);
            if (match.IsServer) DriveScenario(now);
            if (now >= _nextReport) { _nextReport = now + 30; WriteReport(false); }
            if (_result.seconds >= _duration) Finish(true, "Requested sampling duration reached.");
        }
        void CaptureSpike(double now, double interval)
        {
            // LastValue is the last completed frame's aggregate, matching this Update's
            // frame interval. Queue state is captured at that same main-camera frame.
            int frame = Time.frameCount - 1; bool queueAvailable = _queueFrame == frame;
            _spikes.Add(new Spike
            {
                frame = frame, seconds = now - _begin, frameIntervalMs = interval,
                mainThreadAvailable = _main.LastFrameAvailable, mainThreadMs = _main.LastFrameValue,
                renderThreadAvailable = _render.LastFrameAvailable, renderThreadMs = _render.LastFrameValue,
                gcAvailable = _gc.LastFrameAvailable, gcBytes = _gc.LastFrameValue,
                terrainTickAvailable = _tick.LastFrameAvailable, terrainTickMs = _tick.LastFrameValue,
                terrainPublishAvailable = _publish.LastFrameAvailable, terrainPublishMs = _publish.LastFrameValue,
                terrainUploadAvailable = _upload.LastFrameAvailable, terrainUploadMs = _upload.LastFrameValue,
                fallingRenderAvailable = _falling.LastFrameAvailable, fallingRenderMs = _falling.LastFrameValue,
                queueSnapshotAvailable = queueAvailable, terrainRevision = queueAvailable ? _queueRevision : 0,
                pendingTransactions = queueAvailable ? _queuePending : 0,
                unstableColumns = queueAvailable ? _queueUnstable : 0, queueAgeMs = queueAvailable ? _queueAgeMs : 0
            });
        }
        static int CountPlayers()
        {
            int count = 0;
            foreach (var player in PrototypePlayer.ByOwner.Values) if (player != null && player.IsSpawned && !player.IsTestBot) count++;
            return count;
        }
        void CaptureActors(double now, bool sample)
        {
            int firing = 0;
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                if (player == null || !player.IsSpawned || player.IsTestBot) continue;
                uint sequence = player.Snapshot.Value.ShotSequence;
                if (!_actors.TryGetValue(player.PlayerId, out var actor))
                    _actors.Add(player.PlayerId, actor = new ActorSample { Previous = sequence });
                if (sequence != actor.Previous)
                { actor.Shots += unchecked(sequence - actor.Previous); actor.Previous = sequence; actor.LastShot = now; }
                if (actor.LastShot >= 0 && now - actor.LastShot <= 2) firing++;
            }
            if (sample && now - _begin > 3) { _activityFrames++; if (firing == 8) _activeFrames++; }
            _result.eightActorShootingCoverage = _activityFrames > 0 ? (double)_activeFrames / _activityFrames : 0;
        }
        bool Floor(int x, int z, out int y)
        {
            var origin = new Vector3((x + .5f) * BlockWorld.CellSize, BlockWorld.Top + 1, (z + .5f) * BlockWorld.CellSize);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, BlockWorld.Top - _terrain.World.Bottom + 2,
                PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity; y = 0;
            if (count == _hits.Length)
            { _fixtureFailed = true; return FixtureReject("the floor ray query overflowed."); }
            for (int i = 0; i < count; i++) if (_map.Contains(_hits[i].collider) && _hits[i].normal.y > .9f && _hits[i].distance < best)
            { best = _hits[i].distance; y = Mathf.CeilToInt(_hits[i].point.y / BlockWorld.CellSize - .0001f); }
            return !float.IsPositiveInfinity(best);
        }
        bool FixtureReject(string reason)
        { _fixtureFailureReason = "Fixture eligibility failed: " + reason; return false; }
        const float FixtureBoundaryTolerance = .00001f;
        static bool FixtureFinite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        static bool FixtureValid(Bounds bounds) => FixtureFinite(bounds.center) && FixtureFinite(bounds.extents)
            && bounds.extents.x > 0 && bounds.extents.y > 0 && bounds.extents.z > 0;
        bool CaptureFixtureGeometry()
        {
            // Capture once before seeding and again after every seeding yield has completed.
            // Public component APIs only; no per-cell reflection or live scene traversal.
            _fixtureProtection.Clear(); _fixtureActors.Clear(); _fixtureProtectedColumns.Clear();
            bool Finite(Vector3 value) => FixtureFinite(value);
            Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
            bool Valid(Bounds bounds) => FixtureValid(bounds);
            bool Reject(string reason) => FixtureReject(reason);
            bool Capture(Bounds bounds, string geometry)
            {
                if (!Valid(bounds)) return Reject(geometry + " has missing or invalid bounds.");
                bounds.Expand(FixtureBoundaryTolerance * 2);
                _fixtureActors.Add(bounds); return true;
            }
            try
            {
                // This query is repeated immediately before QueueSeed, after all seeding yields.
                Physics.SyncTransforms();
                var arena = PrototypeArena.Current;
                if (arena == null) return Reject("the arena is unavailable.");
                int protectedTeams = 0;
                foreach (var zone in arena.GetComponentsInChildren<HeroChangeZone>(true))
                {
                    if (zone == null) return Reject("a protection component is unavailable.");
                    if (!zone.isActiveAndEnabled) continue;
                    var volume = zone.Volume;
                    if (volume == null || !volume.enabled || !volume.gameObject.activeInHierarchy || !volume.isTrigger
                        || zone.Team < 1 || zone.Team > 2 || !Valid(volume.bounds))
                        return Reject("an enabled protection zone has missing or invalid geometry.");
                    protectedTeams |= 1 << zone.Team;
                    // Retain the tower guard's conservative XZ bounds; y never limits protection.
                    _fixtureProtection.Add(volume.bounds);
                }
                if (protectedTeams != 6) return Reject("both teams' enabled protection geometry is required.");
                int realPlayers = 0;
                foreach (var player in PrototypePlayer.ByOwner.Values)
                {
                    if (player == null || !player.IsSpawned) return Reject("a current player is unavailable or not spawned.");
                    if (!player.IsTestBot) realPlayers++;
                    var state = player.Snapshot.Value;
                    var controller = player.GetComponent<CharacterController>();
                    if (controller == null || !controller.enabled || !controller.gameObject.activeInHierarchy
                        || !Finite(state.Position) || !Finite(controller.transform.position)
                        || !Finite(controller.transform.lossyScale) || controller.transform.lossyScale != Vector3.one
                        || !Finite(controller.transform.up) || Vector3.Dot(controller.transform.up, Vector3.up) < .9999f)
                        return Reject("a player's authoritative capsule geometry is unavailable or nonstandard.");
                    if (!Capture(controller.bounds, "the live motor capsule")) return false;
                    var authoritativeController = controller.bounds;
                    authoritativeController.center += state.Position - controller.transform.position;
                    if (!Capture(authoritativeController, "the motor capsule at its authoritative position")) return false;
                    var shape = HeroBodyShape.For(state.HeroId);
                    float height = state.Swimming || state.CompactBody ? shape.CompactHeight : shape.Height;
                    if (!float.IsFinite(height) || height <= 0 || !float.IsFinite(shape.Radius) || shape.Radius <= 0)
                        return Reject("the authoritative capsule dimensions are invalid.");
                    height = Mathf.Max(height, shape.Radius * 2);
                    if (!Capture(new Bounds(state.Position + Vector3.up * (height * .5f),
                        new Vector3(shape.Radius * 2, height, shape.Radius * 2)), "the authoritative capsule")) return false;
                    if (state.IsBubble)
                    {
                        if (!float.IsFinite(state.BubbleRadius) || state.BubbleRadius <= 0 || !Finite(state.BubbleCenter))
                            return Reject("the authoritative rescue bubble geometry is unavailable.");
                        if (!Capture(new Bounds(state.BubbleCenter, Vector3.one * (state.BubbleRadius * 2)), "the authoritative rescue bubble")) return false;
                    }
                    // RescueBubbleView creates this direct child and destroys its collider;
                    // Collider queries alone cannot capture the displayed bubble's occupied box.
                    var bubble = player.transform.Find("待救泡泡");
                    var bubbleRenderer = bubble != null ? bubble.GetComponent<Renderer>() : null;
                    bool liveBubble = bubble != null && bubble.gameObject.activeInHierarchy;
                    if (state.IsBubble && (!liveBubble || bubbleRenderer == null || !bubbleRenderer.enabled))
                        return Reject("the live rescue bubble geometry is unavailable.");
                    if (liveBubble && (bubbleRenderer == null || !bubbleRenderer.enabled))
                        return Reject("the live rescue bubble has missing or disabled render geometry.");
                    if (liveBubble && !Capture(bubbleRenderer.bounds, "the live rescue bubble")) return false;
                    var body = player.SwimBody;
                    if (body != null && body.CapsuleHitVolume != null && body.CapsuleHitVolume.enabled
                        && body.CapsuleHitVolume.gameObject.activeInHierarchy
                        && !Capture(body.CapsuleHitVolume.bounds, "the live capsule hit proxy")) return false;
                    if (body != null && body.HitVolume != null && body.HitVolume.enabled && body.HitVolume.gameObject.activeInHierarchy)
                    {
                        if (body.HitVolume.sharedMesh == null) return Reject("the live paper hit proxy has no mesh.");
                        if (!Capture(body.HitVolume.bounds, "the live paper hit proxy")) return false;
                    }
                    if (!state.ShowsSwimBody) continue;
                    var profile = body != null ? body.Profile ?? player.Presentation?.Paper : null;
                    var rotation = state.PaperRotation;
                    if (body == null || !body.isActiveAndEnabled || body.HitVolume == null || !body.HitVolume.enabled
                        || !body.HitVolume.gameObject.activeInHierarchy || body.HitVolume.sharedMesh == null || profile == null
                        || !Finite(state.PaperCenter) || !float.IsFinite(profile.Size.x) || profile.Size.x <= 0
                        || !float.IsFinite(profile.Size.y) || profile.Size.y <= 0 || !float.IsFinite(profile.Thickness) || profile.Thickness <= 0
                        || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
                        || Mathf.Abs(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w - 1) > .001f
                        || state.PaperPose != PaperPose.Ground && state.PaperPose != PaperPose.Wall
                            && state.PaperPose != PaperPose.Air && state.PaperPose != PaperPose.Mantle)
                        return Reject("the authoritative paper pose or required hit geometry is unavailable.");
                    // Full rotated profile box conservatively encloses every animated silhouette rect.
                    Vector3 half = Abs(rotation * Vector3.right) * (profile.Size.x * .5f)
                        + Abs(rotation * Vector3.up) * (profile.Size.y * .5f)
                        + Abs(rotation * Vector3.forward) * (profile.Thickness * .5f);
                    if (!Capture(new Bounds(state.PaperCenter, half * 2), "the authoritative paper profile")) return false;
                }
                return realPlayers == 8 || Reject("eight current real players are required at seed submission.");
            }
            catch (Exception exception)
            { return Reject("geometry query threw " + exception.GetType().Name + ": " + exception.Message); }
        }
        static bool FixtureXZIntersects(Bounds a, Bounds b)
            => a.min.x <= b.max.x + FixtureBoundaryTolerance && a.max.x >= b.min.x - FixtureBoundaryTolerance
                && a.min.z <= b.max.z + FixtureBoundaryTolerance && a.max.z >= b.min.z - FixtureBoundaryTolerance;
        bool FixtureVolumeClear(Bounds volume, string geometry)
        {
            if (!FixtureValid(volume)) return FixtureReject(geometry + " has invalid volume.");
            foreach (var protection in _fixtureProtection)
                if (FixtureXZIntersects(volume, protection)) return FixtureReject("a protection column intersects the full " + geometry + " footprint.");
            foreach (var actor in _fixtureActors)
                if (volume.Intersects(actor)) return FixtureReject("occupied actor geometry intersects the full " + geometry + " volume.");
            return true;
        }
        bool TowerFixtureClear()
            => FixtureVolumeClear(new Bounds(new Vector3(0, 16, 4.5f), new Vector3(1, 32, 1)), "tower");
        bool FixtureCellClear(Vector3Int cell)
        {
            var bounds = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize);
            var column = new Vector2Int(cell.x, cell.z);
            if (!_fixtureProtectedColumns.TryGetValue(column, out bool protectedColumn))
            {
                foreach (var protection in _fixtureProtection)
                    if (FixtureXZIntersects(bounds, protection)) { protectedColumn = true; break; }
                _fixtureProtectedColumns.Add(column, protectedColumn);
            }
            if (protectedColumn) return false;
            foreach (var actor in _fixtureActors) if (bounds.Intersects(actor)) return false;
            return true;
        }
        static bool FixtureNeighboursClear(BlockWorld seed, Vector3Int cell)
            => !seed.TryGet(cell + Vector3Int.left, out _) && !seed.TryGet(cell + Vector3Int.right, out _)
                && !seed.TryGet(cell + Vector3Int.down, out _) && !seed.TryGet(cell + Vector3Int.up, out _)
                && !seed.TryGet(cell + new Vector3Int(0, 0, -1), out _) && !seed.TryGet(cell + new Vector3Int(0, 0, 1), out _);
        bool FixtureMapClear(Vector3Int cell)
        {
            int mask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            int count = Physics.OverlapBoxNonAlloc(BlockWorld.Center(cell), Vector3.one * (BlockWorld.CellSize * .45f),
                _fixtureOverlaps, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
            if (count == _fixtureOverlaps.Length)
            { _fixtureFailed = true; return FixtureReject("the fixture map-overlap query overflowed."); }
            for (int i = 0; i < count; i++) if (_map.Contains(_fixtureOverlaps[i])) return false;
            return true;
        }
        bool FixtureGroundSupported(Vector3Int cell)
        {
            int mask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            int count = Physics.RaycastNonAlloc(BlockWorld.Center(cell), Vector3.down, _hits,
                BlockWorld.CellSize * 1.25f, mask, QueryTriggerInteraction.Ignore);
            if (count == _hits.Length)
            { _fixtureFailed = true; return FixtureReject("the fixture ground-support ray query overflowed."); }
            for (int i = 0; i < count; i++)
                if (_map.Contains(_hits[i].collider) && _hits[i].normal.y > .2f) return true;
            return false;
        }
        bool FixtureWallClear(Vector3Int cell, Vector3 normal)
        {
            var center = BlockWorld.Center(cell);
            int mask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            int count = Physics.RaycastNonAlloc(center, -normal, _hits, .125f, mask, QueryTriggerInteraction.Ignore);
            if (count == _hits.Length)
            { _fixtureFailed = true; return FixtureReject("the wall ray query overflowed."); }
            float nearest = float.PositiveInfinity; Vector3 hitNormal = default;
            for (int i = 0; i < count; i++) if (_map.Contains(_hits[i].collider) && _hits[i].distance < nearest)
            { nearest = _hits[i].distance; hitNormal = _hits[i].normal; }
            if (!float.IsFinite(nearest) || nearest < 0 || nearest > .125f || Vector3.Dot(hitNormal, normal) < .9f) return false;
            return FixtureMapClear(cell);
        }
        bool FixtureSeedClear(BlockWorld seed)
        {
            // No yields from this fresh geometry capture through QueueSeed. Traverse only
            // allocated chunks and actual occupied cells, never empty protected-column volume.
            if (!CaptureFixtureGeometry()) return false;
            foreach (var pair in seed.Chunks)
                for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
                {
                    if (!pair.Value.Cells[i].Occupied) continue;
                    var cell = BlockWorld.CellFromIndex(pair.Key, i);
                    if (!FixtureCellClear(cell)) return FixtureReject("a seeded cell intersects current protection or actor geometry: " + cell);
                    if (Case == "scattered100k" && !FixtureNeighboursClear(seed, cell))
                        return FixtureReject("scattered seeded cells share a face: " + cell);
                }
            return true;
        }
        IEnumerator CreateFixture(PrototypeMatch match)
        {
            var arena = PrototypeArena.Current;
            if (!CaptureFixtureGeometry()) { _fixtureFailed = true; yield break; }
            foreach (var collider in arena.GetComponentsInChildren<Collider>(true)) if (!collider.isTrigger) _map.Add(collider);
            var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int required = _target == 250000 ? 275000 : _target;
            Bounds gravityVolume = default;
            if (Case == "cascade20k" || Case == "tower32m")
            {
                bool tower = Case == "tower32m";
                int firstZ = tower ? 40 : -5, endZ = tower ? 50 : 5;
                var floors = new int[100]; int floorIndex = 0;
                // Prove the complete fixed workload before writing any gravity fixture cell.
                for (int z = firstZ; z < endZ; z++) for (int x = -5; x < 5; x++)
                {
                    if (!Floor(x, z, out int floor) || tower && floor != 0) { _fixtureFailed = true; yield break; }
                    int top = tower ? 320 : floor + 201;
                    if (top > 320 || !seed.InBounds(new Vector3Int(x, floor, z)) || !seed.InBounds(new Vector3Int(x, top - 1, z)))
                    { _fixtureFailed = true; yield break; }
                    floors[floorIndex] = floor;
                    var column = new Bounds(new Vector3((x + .5f) * BlockWorld.CellSize, (floor + top) * BlockWorld.CellSize * .5f,
                        (z + .5f) * BlockWorld.CellSize), new Vector3(BlockWorld.CellSize, (top - floor) * BlockWorld.CellSize, BlockWorld.CellSize));
                    if (floorIndex++ == 0) gravityVolume = column; else gravityVolume.Encapsulate(column);
                }
                if (tower ? !TowerFixtureClear() : !FixtureVolumeClear(gravityVolume, "cascade"))
                { _fixtureFailed = true; yield break; }
                floorIndex = 0;
                for (int z = firstZ; z < endZ; z++) for (int x = -5; x < 5; x++)
                {
                    int floor = floors[floorIndex++];
                    int top = tower ? 320 : floor + 201;
                    _supports.Add(new Vector3Int(x, floor, z));
                    int columnCells = 0, columnBottom = int.MaxValue, columnTop = int.MinValue;
                    for (int y = floor; y < top; y++)
                    {
                        var cell = new Vector3Int(x, y, z);
                        bool added = seed.Set(cell, BlockCell.Create(2, y == floor));
                        if (!added) { _fixtureFailed = true; yield break; }
                        if (tower)
                        {
                            columnCells++; columnBottom = Math.Min(columnBottom, cell.y); columnTop = Math.Max(columnTop, cell.y + 1);
                        }
                        _result.fixtureTopMeters = Math.Max(_result.fixtureTopMeters, (y + 1) * BlockWorld.CellSize);
                    }
                    if (tower)
                    {
                        if (columnCells != 320 || columnBottom != 0 || columnTop != 320) { _fixtureFailed = true; yield break; }
                        float bottomMeters = columnBottom * BlockWorld.CellSize, heightMeters = (columnTop - columnBottom) * BlockWorld.CellSize;
                        bool firstColumn = _result.fixtureColumns == 0;
                        _result.fixtureBottomMeters = firstColumn ? bottomMeters : Math.Min(_result.fixtureBottomMeters, bottomMeters);
                        _result.fixtureMinColumnHeightMeters = firstColumn ? heightMeters : Math.Min(_result.fixtureMinColumnHeightMeters, heightMeters);
                        _result.fixtureMaxColumnHeightMeters = Math.Max(_result.fixtureMaxColumnHeightMeters, heightMeters);
                        _result.fixtureColumns++;
                    }
                    if (timer.Elapsed.TotalMilliseconds > 2) { yield return null; timer.Restart(); }
                }
                if (_supports.Count != 100 || seed.PinkCount != 0 || seed.BlueCount != (tower ? 32000 : 20100)
                    || tower && _result.fixtureColumns != 100)
                { _fixtureFailed = true; yield break; }
            }
            else
            {
                int minX = Mathf.CeilToInt(-seed.Dimensions.x / (2 * BlockWorld.CellSize));
                int maxX = Mathf.FloorToInt(seed.Dimensions.x / (2 * BlockWorld.CellSize));
                int minZ = Mathf.CeilToInt(-seed.Dimensions.y / (2 * BlockWorld.CellSize));
                int maxZ = Mathf.FloorToInt(seed.Dimensions.y / (2 * BlockWorld.CellSize));
                for (int z = minZ; z < maxZ && seed.PinkCount + seed.BlueCount < required; z++)
                    for (int x = minX; x < maxX && seed.PinkCount + seed.BlueCount < required; x++)
                    {
                        if (Case == "scattered100k" && ((x + z) & 1) != 0) continue;
                        if (Floor(x, z, out int floor))
                        {
                            int layers = Case == "scattered100k" ? 1 : 3;
                            for (int layer = 0; layer < layers && seed.PinkCount + seed.BlueCount < required; layer++)
                            {
                                var cell = new Vector3Int(x, floor + layer, z);
                                // Never seed an unsupported upper layer after skipping its base.
                                if (!seed.InBounds(cell) || !FixtureCellClear(cell)
                                    || layer > 0 && !seed.TryGet(cell + Vector3Int.down, out _)
                                    || !FixtureMapClear(cell) || layer == 0 && !FixtureGroundSupported(cell)
                                    || Case == "scattered100k" && !FixtureNeighboursClear(seed, cell)) break;
                                if (!seed.Set(cell, BlockCell.Create(x < 0 ? (byte)1 : (byte)2, layer == 0)))
                                { _fixtureFailed = true; yield break; }
                                _result.fixtureTopMeters = Math.Max(_result.fixtureTopMeters, (cell.y + 1) * BlockWorld.CellSize);
                            }
                        }
                        if (_fixtureFailed) yield break;
                        if (timer.Elapsed.TotalMilliseconds > 2) { yield return null; timer.Restart(); }
                    }
                if (Case == "scattered100k" && seed.PinkCount + seed.BlueCount < required)
                {
                    // Preserve every legal ground checkerboard cell, then add genuinely
                    // isolated first-layer cells attached to the two real map side walls.
                    for (int side = 0; side < 2 && seed.PinkCount + seed.BlueCount < required; side++)
                    {
                        int x = side == 0 ? maxX - 1 : minX;
                        Vector3 normal = side == 0 ? Vector3.left : Vector3.right;
                        for (int z = -318; z < 318 && seed.PinkCount + seed.BlueCount < required; z++)
                            for (int y = 2; y < 30 && seed.PinkCount + seed.BlueCount < required; y++)
                            {
                                if (((y + z) & 1) != 0) continue;
                                var cell = new Vector3Int(x, y, z);
                                if (!seed.InBounds(cell) || seed.TryGet(cell, out _) || !FixtureCellClear(cell)
                                    || !FixtureNeighboursClear(seed, cell)) continue;
                                if (FixtureWallClear(cell, normal))
                                {
                                    if (!seed.Set(cell, BlockCell.Create(x < 0 ? (byte)1 : (byte)2, true)))
                                    { _fixtureFailed = true; yield break; }
                                    _result.fixtureTopMeters = Math.Max(_result.fixtureTopMeters, (cell.y + 1) * BlockWorld.CellSize);
                                }
                                if (_fixtureFailed) yield break;
                                if (timer.Elapsed.TotalMilliseconds > 2) { yield return null; timer.Restart(); }
                            }
                    }
                }
                if (seed.PinkCount + seed.BlueCount != required)
                { _fixtureFailureReason = "The legal fixture space could not satisfy the full requested workload."; _fixtureFailed = true; yield break; }
            }
            for (int i = 0; i < 8; i++)
            {
                float x = (i % 4 - 1.5f) * 3, z = -15 + (i / 4) * 6;
                if (Floor(Mathf.FloorToInt(x / BlockWorld.CellSize), Mathf.FloorToInt(z / BlockWorld.CellSize), out int floor))
                    _blastPoints.Add(new Vector3(x, (floor + 1.5f) * BlockWorld.CellSize, z));
                if (_fixtureFailed) yield break;
            }
            if (Case == "blast8" && _blastPoints.Count != 8) { _fixtureFailed = true; yield break; }
            if (!FixtureSeedClear(seed)) { _fixtureFailed = true; yield break; }
            if (Case == "tower32m" && !TowerFixtureClear()
                || Case == "cascade20k" && !FixtureVolumeClear(gravityVolume, "cascade"))
            { _fixtureFailed = true; yield break; }
            _seedRevision = _terrain.Revision + 1;
            if (!_terrain.QueueSeed(seed)) { _fixtureFailed = true; yield break; }
            _seeded = true;
        }
        void DriveScenario(double now)
        {
            if ((Case == "combat250k" || Case == "steady250k") && now >= _nextProbe)
            {
                _nextProbe = now + 1;
                var player = PrototypePlayer.Local;
                if (player != null)
                    _terrain.QueueBuild(player.Snapshot.Value.Position + Vector3.forward * 2 + Vector3.up * .1f,
                        Vector3.up, player.Snapshot.Value.Team, 1, .1f, false);
            }
            if (!_attackIssued && now - _begin >= 2 && (Case == "blast8" || Case == "cascade20k" || Case == "tower32m"))
            {
                _attackIssued = true; _attackBegin = now;
                if (Case == "blast8")
                {
                    _result.blastEnemyRadius = (float)Number("-blockBenchmarkBlastRadius", 2);
                    _result.blastFriendlyRadius = _result.blastEnemyRadius * .5f;
                    foreach (var point in _blastPoints)
                    {
                        _result.blastsIssued++;
                        _terrain.QueueDestroy(point, _result.blastEnemyRadius, _result.blastFriendlyRadius, 1, false, result =>
                        {
                            _result.blastsCompleted++; if (result.RemovedEnemy + result.RemovedFriendly > 0) _result.blastsWithRemoval++;
                            if (_result.blastsCompleted == 8) _result.blastCompletionMs = (Time.realtimeSinceStartupAsDouble - _attackBegin) * 1000;
                        });
                    }
                }
                else foreach (var cell in _supports)
                {
                    _result.supportsIssued++;
                    _terrain.QueueDestroy(BlockWorld.Center(cell), .0001f, 0, 1, true,
                        result => _result.supportsRemoved += result.RemovedEnemy, 1, 0);
                }
            }
            if (_attackIssued && Case != "blast8" && _result.supportsRemoved == _supports.Count
                && _terrain.LossOfSupportReady)
            {
                if (_result.supportQueueClearMs < 0) _result.supportQueueClearMs = (now - _attackBegin) * 1000;
                if (_terrain.FallingCount == 0 && _result.cascadeCompleteMs < 0)
                    _result.cascadeCompleteMs = (now - _attackBegin) * 1000;
            }
        }
        void VerifyFinalSettledColliders()
        {
            if (_result.role != "host" || (Case != "tower32m" && Case != "cascade20k")) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _result.finalColliderAttempted = true;
            if (_terrain == null || !_terrain.Initialized || _terrain.PendingTransactions != 0
                || _terrain.UnstableColumns != 0 || _terrain.FallingCount != 0)
            {
                _result.finalColliderFirstFailure = "The complete terrain has not settled; native verification cannot replace settlement.";
                return;
            }
            var world = _terrain.World;
            _result.finalColliderRevision = world.Revision;
            _result.finalColliderSimulationTime = _terrain.SimulationTime;
            Physics.SyncTransforms();
            var directions = new[] { Vector3Int.right, Vector3Int.left, Vector3Int.up,
                Vector3Int.down, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) };
            var columnTops = new Dictionary<Vector2Int, int>();
            foreach (var pair in world.Chunks)
            {
                for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
                {
                    var own = pair.Value.Cells[index];
                    if (!own.Occupied) continue;
                    _result.finalColliderCells++;
                    var cell = BlockWorld.CellFromIndex(pair.Key, index);
                    var column = new Vector2Int(cell.x, cell.z);
                    if (!columnTops.TryGetValue(column, out int top) || cell.y > top) columnTops[column] = cell.y;
                    foreach (var direction in directions)
                    {
                        if (world.TryGet(cell + direction, out _)) continue;
                        _result.finalColliderFaces++;
                        var normal = (Vector3)direction;
                        var face = BlockWorld.Center(cell) + normal * (BlockWorld.CellSize * .5f);
                        var ray = new Ray(face + normal * (BlockWorld.CellSize * .25f), -normal);
                        bool matches = Physics.Raycast(ray, out var hit, BlockWorld.CellSize * .5f,
                            1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore)
                            && _terrain.IsStableCollider(hit.collider)
                            && Vector3.Distance(hit.point, face) <= .001f
                            && Vector3.Dot(hit.normal, normal) >= .999f
                            && _terrain.TryContact(hit, out var contact) && contact.Stable
                            && contact.Cell == cell && contact.Owner == own.Owner
                            && contact.Chunk == pair.Key && contact.Revision == pair.Value.Revision;
                        if (matches) continue;
                        _result.finalColliderMismatches++;
                        if (string.IsNullOrEmpty(_result.finalColliderFirstFailure))
                            _result.finalColliderFirstFailure = $"Native face/contact mismatch at cell {cell}, outward {direction}.";
                    }
                }
            }
            // Start above the original fixture: a retained original upper collider
            // must not mask the newly settled top, even when a correct collider also exists.
            int checkedColumns = 0;
            foreach (var support in _supports)
            {
                checkedColumns++;
                var column = new Vector2Int(support.x, support.z);
                bool matches = columnTops.TryGetValue(column, out int top);
                if (matches)
                {
                    var surface = new Vector3((support.x + .5f) * BlockWorld.CellSize,
                        (top + 1) * BlockWorld.CellSize, (support.z + .5f) * BlockWorld.CellSize);
                    var origin = new Vector3(surface.x, BlockWorld.Top + BlockWorld.CellSize * .5f, surface.z);
                    matches = Physics.Raycast(origin, Vector3.down, out var hit,
                        origin.y - surface.y + BlockWorld.CellSize * .25f,
                        1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore)
                        && _terrain.IsStableCollider(hit.collider)
                        && Vector3.Distance(hit.point, surface) <= .001f
                        && Vector3.Dot(hit.normal, Vector3.up) >= .999f
                        && _terrain.TryContact(hit, out var contact) && contact.Stable
                        && contact.Cell == new Vector3Int(support.x, top, support.z);
                }
                if (matches) continue;
                _result.finalColliderMismatches++;
                if (string.IsNullOrEmpty(_result.finalColliderFirstFailure))
                    _result.finalColliderFirstFailure = $"Original fixture column {column} has a missing or retained upper native collider.";
            }
            _result.finalColliderOriginalColumns = checkedColumns;
            _result.finalColliderComplete = checkedColumns == 100 && columnTops.Count == 100
                && ReferenceEquals(_terrain.World, world)
                && _terrain.Revision == _result.finalColliderRevision && _terrain.FallingCount == 0
                && _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0;
            _result.finalColliderPassed = _result.finalColliderComplete && _result.finalColliderFaces > 0
                && _result.finalColliderCells == world.PinkCount + world.BlueCount
                && _result.finalColliderMismatches == 0;
            _result.finalColliderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d
                / System.Diagnostics.Stopwatch.Frequency;
        }
        void Finish(bool complete, string reason)
        {
            if (_done) return; _done = true;
            _backbufferObservation?.Close(); _backbufferObservation = null;
            _result.complete = complete; _result.stage = complete ? "finished" : "incomplete"; _result.reason = reason;
            if (_begin >= 0) _result.seconds = Time.realtimeSinceStartupAsDouble - _begin;
            _result.failedTransactions = _terrain != null ? _terrain.FailedTransactions - _failedAtStart : 0;
            _cpuWindowCapture?.Close(complete ? "sampling_complete" : "sampling_incomplete", _result.samples);
            WriteReport(true);
            _cpuWindowCapture?.Write();
            _reportObservation?.WriteIfFinalWritten(_finalReportWritten);
        }
        void Quit() { if (_finalReportWritten) Application.Quit(_result.passed ? 0 : 5); }
        Gate Check(string name, bool available, bool passed, string detail, double value = 0, double limit = 0, bool diagnosticOnly = false)
            => new() { name = name, status = available ? passed ? "pass" : "fail" : "unverified", detail = detail, value = value, limit = limit, diagnosticOnly = diagnosticOnly };
        void WriteReport(bool final)
        {
            var observedReport = _reportObservation?.BeginReport(final, _result.stage, _result.samples,
                _begin, _ready, _done, _queueHeads.Count, _spikes.Count);
            try
            {
                // Capture the report boundary before percentile aggregation adds
                // report-writing CPU time to the outstanding transaction's age.
                if (_terrain != null)
                {
                    _result.pendingWindowEndWallTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    _result.pendingWorkAtEnd = _terrain.InspectPendingWork();
                    _result.publicationRetryAtEnd = _terrain.InspectPublicationRetry();
                    _result.pendingAtEnd = _terrain.InspectPendingTransaction();
                }
                if (final && _frameTimingObservation != null)
                    _frameTimingObservation.EndWindow(Time.frameCount, Time.realtimeSinceStartupAsDouble,
                        _result.pendingWindowEndWallTimestamp != 0 ? _result.pendingWindowEndWallTimestamp : System.Diagnostics.Stopwatch.GetTimestamp(),
                        _result.samples, _gpu.Values.Count);
                _result.metrics = new Metric[_series.Count + _pendingMetrics.Length + _pendingWorkerMetrics.Length + _pendingPipelineMetrics.Length];
                for (int i = 0; i < _series.Count; i++) _result.metrics[i] = _series[i].Finish(final);
                for (int i = 0; i < _pendingMetrics.Length; i++) _result.metrics[_series.Count + i] = _pendingMetrics[i].Finish(final);
                for (int i = 0; i < _pendingWorkerMetrics.Length; i++) _result.metrics[_series.Count + _pendingMetrics.Length + i] = _pendingWorkerMetrics[i].Finish(final);
                for (int i = 0; i < _pendingPipelineMetrics.Length; i++) _result.metrics[_series.Count + _pendingMetrics.Length + _pendingWorkerMetrics.Length + i] = _pendingPipelineMetrics[i].Finish(final);
                _result.gpuTimingCoverage = _result.samples > 0 ? (double)_gpu.Values.Count / _result.samples : 0;
                _result.mainTimingCoverage = _result.samples > 0 ? (double)Math.Max(_main.Values.Count, _frameMain.Values.Count) / _result.samples : 0;
                _result.renderTimingCoverage = _result.samples > 0 ? (double)Math.Max(_render.Values.Count, _frameRender.Values.Count) / _result.samples : 0;
                _result.renderTimingSource = _frameRender.Values.Count > _render.Values.Count ? "FrameTiming.cpuRenderThreadFrameTime" : "ProfilerRecorder.Render Thread";
                _result.spikes = _spikes.ToArray();
                _result.queueHeads = _queueHeads.ToArray();
                if (_terrain != null) _result.buildWorkAtEnd = _terrain.InspectBuildWork();
                // The sampled window and percentiles are closed before the full native scan.
                if (final) VerifyFinalSettledColliders();
                var gates = new List<Gate>();
                gates.Add(Check("acceptanceProfile", true, AcceptanceProfile == "strict" || PrototypeProfile,
                    "The named profile must be recognized; prototype-v1 is distinct from strict and short-duration diagnostics."));
                gates.Add(Check("completeDuration", true, final && _result.complete && _result.seconds >= _requiredDuration,
                    "Short CLI durations produce diagnostics only.", _result.seconds, _requiredDuration));
                gates.Add(Check("developmentPlayer", true, !Application.isEditor, "Editor frames are not Player performance evidence."));
                gates.Add(Check("normal1080pMainCamera", true, _result.graphics && _result.width == 1920 && _result.height == 1080
                    && _result.renderedFrames >= _result.samples && _result.samples > 0 && _result.framesWithoutMainCamera <= 1,
                    "Every sampled normal backbuffer frame must have one actual main-camera render; no substitute camera or render texture."));
                gates.Add(Check("eightRealPlayers", true, _result.minimumRealPlayers == 8, "Test bots do not count.", _result.minimumRealPlayers, 8));
                gates.Add(Check("gpuTiming", _gpu.Values.Count > 0, _result.gpuTimingCoverage >= .8,
                    "Unique positive finite GPU timings must cover at least 80% of sampled frames; missing data is unverified.", _result.gpuTimingCoverage, .8));
                gates.Add(Check("mainAndRenderTiming", Math.Max(_main.Values.Count, _frameMain.Values.Count) > 0 && Math.Max(_render.Values.Count, _frameRender.Values.Count) > 0,
                    _result.mainTimingCoverage >= .8 && _result.renderTimingCoverage >= .8,
                    "Each thread must have positive finite timings in at least 80% of sampled frames.", Math.Min(_result.mainTimingCoverage, _result.renderTimingCoverage), .8));
                var frame = _result.metrics[0];
                gates.Add(Check("frameP95", final && frame.samples > 0, frame.p95 <= 15.5, "Approved frame-time threshold.", frame.p95, 15.5));
                gates.Add(Check("frameP99", final && frame.samples > 0, frame.p99 <= 16.67, "Approved frame-time threshold.", frame.p99, 16.67));
                gates.Add(Check("no33msSpikes", final && frame.samples > 0, _result.longFrames33 == 0,
                    "Every spike is retained in this conservative full-frame record; prototype-v1 reports the risk without declaring strict acceptance.", frame.max, 33.333, diagnosticOnly: PrototypeProfile));
                gates.Add(Check("terrainTransactions", true, _result.errors == 0 && _result.failedTransactions == 0, "Errors or failed transactions invalidate a run."));
                gates.Add(Check("workload", _result.fixtureReady, _target == 0 || _result.minimumBlocks >= _target,
                    "Surviving blocks include falling blocks; no reduced fixture count.", _result.minimumBlocks, _target));
                if (Case == "combat250k" || Case == "steady250k")
                {
                    gates.Add(Check("eightContinuousShooters", _activityFrames > 0, _result.eightActorShootingCoverage >= .9,
                        "All eight actual ShotSequence values must keep advancing; a two-second gap makes a sampled interval inactive.", _result.eightActorShootingCoverage, .9));
                    var ordinary = _ordinaryLatency.Finish(final);
                    gates.Add(Check("ordinaryPublishLatency", _result.role == "host" && final && ordinary.samples > 0, ordinary.p95 <= _result.ordinaryPublishP95LimitMs,
                        "Actual primary main-attack building and direct enemy-removal contacts only; excludes trails, probes, blasts, sub weapons and specials.", ordinary.p95, _result.ordinaryPublishP95LimitMs));
                }
                if (Case == "blast8")
                    gates.Add(Check("eightSimultaneousBlasts", _result.role == "host", _result.blastsIssued == 8 && _result.blastsCompleted == 8
                        && _result.blastsWithRemoval == 8 && _result.blastEnemyRadius >= 2 && _result.blastCompletionMs >= 0 && _result.blastCompletionMs <= _result.blastCompletionLimitMs,
                        "Eight unbounded destruction commands issued in the same frame; every complete radius must finish.", _result.blastCompletionMs, _result.blastCompletionLimitMs));
                if (Case == "tower32m" || Case == "cascade20k")
                {
                    gates.Add(Check("finalSettledNativeColliders", _result.role == "host" && final
                        && _result.finalColliderAttempted && _result.finalColliderComplete, _result.finalColliderPassed,
                        "Every exposed cell face must match actual PhysX terrain geometry/contact; all 100 original columns are cast from above the old fixture to reject retained upper colliders. The scan runs after sampling.",
                        _result.finalColliderMismatches, 0));
                    gates.Add(Check("supportQueueClear", _result.role == "host", _result.supportsRemoved == 100 && _result.supportQueueClearMs >= 0 && _result.supportQueueClearMs <= 500,
                        "All 100 supports removed, loss-of-support columns scanned and corresponding detach commits published; prototype-v1 defers only the 500ms optimization target.", _result.supportQueueClearMs, 500, diagnosticOnly: PrototypeProfile));
                    gates.Add(Check("cascadeCompletion", _result.role == "host", _result.cascadeCompleteMs >= 0 && _result.detachedBlocks >= 20000,
                        "Count each detached run once; completion requires no remaining falling blocks.", _result.detachedBlocks, 20000));
                    if (PrototypeProfile && Case == "cascade20k")
                        gates.Add(Check("cascadeSurvivors", _result.role == "host" && final && _result.fixtureReady,
                            _result.initialBlocks == 20100 && _result.supportsIssued == 100 && _result.supportsRemoved == 100
                            && _result.detachedBlocks == 20000 && _result.minimumBlocks == 20000 && _result.finalBlocks == 20000
                            && _terrain != null && _terrain.PinkCount == 0 && _terrain.BlueCount == 20000
                            && _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0,
                            "Exactly 20100 seed cells minus 100 supports leave 20000 surviving and fully settled cells; deferred latency never waives conservation or completion.",
                            _result.finalBlocks, 20000));
                    if (Case == "tower32m")
                    {
                        gates.Add(Check("towerHeight", _result.role == "host" && _result.fixtureReady,
                            _result.fixtureColumns == 100 && _result.fixtureBottomMeters == 0 && _result.fixtureTopMeters == 32
                            && _result.fixtureTopMeters - _result.fixtureBottomMeters == 32
                            && _result.fixtureMinColumnHeightMeters == 32 && _result.fixtureMaxColumnHeightMeters == 32,
                            "All 100 ground-zero columns contain 320 successful cell writes and span a full 32 metres; an absolute top alone is insufficient.",
                            _result.fixtureTopMeters - _result.fixtureBottomMeters, 32));
                        gates.Add(Check("towerSurvivors", _result.role == "host" && final && _result.fixtureReady,
                            _result.initialBlocks == 32000 && _result.supportsIssued == 100 && _result.supportsRemoved == 100
                            && _result.detachedBlocks == 31900 && _result.minimumBlocks == 31900 && _result.finalBlocks == 31900
                            && _terrain != null && _terrain.PinkCount == 0 && _terrain.BlueCount == 31900
                            && _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0,
                            "Exactly 32000 seed cells minus 100 supports leave 31900 detached, surviving and fully settled cells; no reduced or residual fixture.",
                            _result.finalBlocks, 31900));
                    }
                }
                _result.gates = gates.ToArray(); _result.passed = final && _result.complete;
                foreach (var gate in gates) _result.passed &= gate.status == "pass" || PrototypeProfile && gate.diagnosticOnly;
                _result.strictPerformancePassed = _result.passed && AcceptanceProfile == "strict";
                var actors = new List<Actor>(); foreach (var pair in _actors) actors.Add(new Actor { player = pair.Key.ToString(CultureInfo.InvariantCulture), shots = pair.Value.Shots });
                _result.actors = actors.ToArray();
                if (_result.minimumBlocks == int.MaxValue) _result.minimumBlocks = 0;
                if (_result.minimumRealPlayers == int.MaxValue) _result.minimumRealPlayers = 0;
                // Serialize a complete immutable report once. Retries must not recompute
                // final percentiles or change the sampled interval while a reader holds it.
                observedReport?.BeginSerialization();
                _pendingReport = JsonUtility.ToJson(_result, true);
                observedReport?.EndSerialization();
                _pendingReportFinal = final; observedReport?.Serialized(_pendingReport);
                _nextWrite = 0; TryWriteReport();
            }
            catch (Exception exception)
            {
                observedReport?.Failed(exception);
                throw;
            }
            finally { observedReport?.Dispose(); }
        }
        void TryWriteReport()
        {
            var observedWrite = _reportObservation?.BeginWrite(_result.stage, _result.samples, _begin, _ready, _done);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_output));
                string temporary = _output + ".writing"; File.WriteAllText(temporary, _pendingReport);
                if (File.Exists(_output)) File.Replace(temporary, _output, null); else File.Move(temporary, _output);
            }
            catch (IOException exception) when (IsReportWriteRetryable(exception))
            {
                // Sharing/lock failures and ReplaceFile error 1175 keep both names.
                // Retain the complete prior file and the immutable pending JSON;
                // retry later rather than deleting or rewriting the old report.
                _nextWrite = Time.realtimeSinceStartupAsDouble + .1;
                observedWrite?.Failed("retryable-io", exception, _nextWrite); return;
            }
            catch (Exception exception)
            {
                observedWrite?.Failed("terminal-exception", exception);
                throw;
            }
            finally { observedWrite?.Dispose(); }
            _pendingReport = null;
            if (!_pendingReportFinal) return;
            _finalReportWritten = true;
            Debug.Log("[BLOCK-BENCHMARK] " + (_result.passed ? "PASS " : "NOT PASSED ") + _output);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-blockBenchmarkQuit") >= 0) Invoke(nameof(Quit), 3);
        }
        static bool IsReportWriteRetryable(IOException exception)
        {
            int error = exception.HResult & 0xffff;
            // 1176/1177 may have moved the previous file and are not safe retries.
            return error == 32 || error == 33 || error == 1175;
        }
        void OnApplicationQuit()
        {
            try { if (!_done) Finish(false, "Player exited before the requested benchmark completed."); }
            finally
            {
                _cpuWindowCapture?.Close("application_quit", _result != null ? _result.samples : 0);
                _cpuWindowCapture?.Write();
                _reportObservation?.WriteOnExit(_finalReportWritten);
            }
        }
        void OnDestroy()
        {
            _backbufferObservation?.Close(); _backbufferObservation = null;
            _cpuWindowCapture?.Close("benchmark_destroyed", _result != null ? _result.samples : 0);
            _cpuWindowCapture?.Write();
            _reportObservation?.WriteOnExit(_finalReportWritten);
            if (_instance == this) _instance = null;
            Application.logMessageReceived -= OnLog; RenderPipelineManager.endCameraRendering -= OnCamera; Camera.onPostRender -= OnBuiltinCamera;
            if (_terrain != null) { _terrain.MutationCompleted -= OnMutation; _terrain.PublishedCommit -= OnCommit; _terrain.PendingTransactionFinished -= OnPendingTransaction; }
            foreach (var series in _series) series.Recorder.Dispose();
            _frameTimingObservation?.Write();
        }
#endif
    }
}
