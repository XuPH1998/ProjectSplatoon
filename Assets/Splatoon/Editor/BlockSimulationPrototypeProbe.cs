#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Splatoon.Blocks.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    /// <summary>
    /// Paced, data-only prototype measurement. It never loads scenes, starts Players, or touches OS input/focus.
    /// Run returns immediately; inspect StatusPath or call ReadStatus while Editor.update drives the run.
    /// </summary>
    public static class BlockSimulationPrototypeProbe
    {
        public const int WorkerCount = 2, WarmupTicks = 600, MeasurementTicks = 1800;
        public const double MaximumTickSeconds = 30;
        public const int EvidenceWindowTicks = 360;
        const int EvidenceWindowCount = MeasurementTicks / EvidenceWindowTicks;
        const float HorizontalSpeedThreshold = .001f, SubstantiveExcursion = .01f, ImpulseThreshold = .00001f;
        const float Dt = 1f / 60f;
        const double TickMs = 1000.0 / 60;
        const string ReportsRoot = "Reports/BlockSimulation20261018";
        static RunContext _run;
        public static bool IsRunning => _run != null;
        public static string StatusPath { get; private set; }
        public static string LastOutputDirectory { get; private set; }

        [Serializable] public sealed class SourceIdentity { public string path, sha256; public long bytes; }

        [Serializable] public sealed class RunReport
        {
            public string schema = "block-simulation-prototype-v6", runId, startedUtc, finishedUtc, status, error;
            public string unityVersion, runtimeMvid, processor, operatingSystem, outputDirectory;
            public string editorProcessStartedUtc, editorProcessName, editorExecutablePath, sourceManifestPath, sourceManifestSha256;
            public int editorProcessId;
            public double maximumTickSeconds = MaximumTickSeconds;
            public int processorCount, systemMemoryMb, requestedWorkers = WorkerCount, originalWorkers, observedWorkers;
            public bool burstEnabled, allocationPositiveControlPassed, threadByteCounterSupported, workerCountRestored, sourceIdentityUnchanged, passed;
            public long allocationBaselineEvents, allocationMainControlEvents, allocationWorkerBaselineEvents, allocationWorkerControlEvents, threadByteCounterControlBytes;
            public int solverSubsteps = BlockSimulation.Substeps, velocityIterations = BlockSimulation.VelocityIterations,
                positionIterations = BlockSimulation.PositionIterations;
            public int warmupTicks = WarmupTicks, measurementTicksPerCase = MeasurementTicks;
            public int evidenceWindowTicks = EvidenceWindowTicks;
            public float horizontalSpeedThreshold = HorizontalSpeedThreshold, substantiveExcursionMeters = SubstantiveExcursion, sampledImpulseThreshold = ImpulseThreshold;
            public string scope = "CPU gate covers push admission + complete BlockSimulation.Step + committed state publication. Step includes real map/body contacts, two substeps, broadphase, CCD, solve, support, wake and commit. Independent validation is timed separately. No rendering, networking, CharacterController integration or formal Player acceptance.";
            public string allocationScope = "All-thread GC.Alloc allocation event count (sum of ProfilerRecorderSample.Count), gated strictly around each measured tick with SumAllSamplesInFrame. Value is nanoseconds, not bytes. No-allocation, main-thread and prestarted-worker controls must pass. The current-thread byte counter is diagnostic and used only if its 4096-byte control succeeds. Editor IO is outside capture.";
            public string completenessScope = "Grow-or-fault native buffers, independent 513-body query/contact growth regression and per-tick body/lifecycle/finite-state conservation. Performance contact totals alone do not prove every geometric pair or TOI.";
            public string workloadEvidenceScope = "Five 360-tick windows. Commands are counted by full handle; horizontal motion uses committed state. Pressure is a positive lateral normal impulse in the final-substep retained contact ledger at current touching AABBs, not total tick impulse or proof every command adds momentum. Case1 requires all 4000 driven IDs to receive all commands and observe pressure each window; every tick must have horizontal velocity, committed displacement or lateral pressure, and every window at least one 1cm excursion. Per-ID 1cm coverage is reported, not an individual hard gate. A zero terminal velocity during real displacement/pressure is valid. Synthetic workload is not the production eight-player force budget. Evidence copy/analysis is validation time; CPU gate remains Drive+Step+CopyStates.";
            public string progressIoScope = "Progress JSON uses closed temporary files and atomic replacement with ordinary Flush, not forced disk persistence. It is visible to the OS and watchdog after publication but does not promise power-loss durability. Wall decomposes into initialMeasurementMilliseconds (ready IO + first callback wait), callbackMilliseconds and callbackGapMilliseconds. Tick and progress IO timings are nested diagnostics, not additive to callback totals; none is subtracted from the unadjusted throughput wall clock. Final manifests/summary retain forced disk flush.";
            public string solverDiagnosticScope = "Normal/support diagnostics are captured by value after the measured CPU and GC windows. CSV columns preserve every field of BlockNormalDiagnostics and BlockSupportDiagnostics; these are per-Step counters/timings, with nested support timings not additive. Warm-up diagnostics are separate from measurement samples; breadcrumbs contain the last returned warm-up tick. Terminal diagnostics may describe a rolled-back failed Step: normal fields retain that attempt and support fields may include rollback repairs, while general committed tick counters can remain at the preceding tick. No diagnostic IO is excluded from the unadjusted throughput wall clock.";
            public SourceIdentity[] inputs;
            public CaseReport[] cases;
        }

        [Serializable] public sealed class CaseReport
        {
            public string name, geometryHash, startedUtc, finishedUtc, error;
            public int expectedBodies, expectedResting, drivenBodies, completedWarmupTicks, completedTicks;
            public bool cpuBudgetPassed, zeroManagedGcPassed, workloadPassed, sustained60HzPassed, solverIntegrityPassed, passed;
            public double stepP50Ms, stepP95Ms, stepP99Ms, stepPeakMs, stepMeanMs, tickP95Ms, tickP99Ms, tickPeakMs;
            public double simulationP95Ms, simulationP99Ms, simulationPeakMs, admissionP95Ms, publicationP95Ms, validationP95Ms;
            public double p95BudgetMs, p99BudgetMs, wallSeconds, actualTicksPerSecond, expectedSeconds = MeasurementTicks / 60.0;
            public double solverDebtEndMs, solverDebtPeakMs, maxSchedulingLateMs;
            public int measurementCallbacks, breadcrumbWrites, statusWrites, inactiveWorkloadTicks;
            public double initialMeasurementMilliseconds, callbackMilliseconds, callbackGapMilliseconds, callbackGapPeakMilliseconds;
            public double breadcrumbIoMilliseconds, breadcrumbIoPeakMilliseconds, statusIoMilliseconds, statusIoPeakMilliseconds;
            public double measuredFullTickMilliseconds, measuredSimulationMilliseconds, measuredValidationMilliseconds;
            public int maxSchedulingBacklogTicks, endDueBacklogTicks, minActive = int.MaxValue, maxActive, minResting = int.MaxValue, maxResting;
            public int minContacts = int.MaxValue, maxContacts, minDynamicContacts = int.MaxValue, maxDynamicContacts;
            public int minMoving = int.MaxValue, maxMoving, minPairs = int.MaxValue, maxPairs, maxHashEntries, candidateMismatchTicks;
            public int minHorizontalMoving = int.MaxValue, maxHorizontalMoving, minHorizontalDisplacing = int.MaxValue, maxHorizontalDisplacing;
            public int minLoadedDynamicContacts = int.MaxValue, maxLoadedDynamicContacts, minLateralPressureContacts = int.MaxValue, maxLateralPressureContacts;
            public int minPressureParticipants = int.MaxValue, maxPressureParticipants, completedEvidenceWindows;
            public bool evidenceWindowsPassed;
            public WindowReport[] evidenceWindows;
            public int maximumCandidatesRequired, maximumCandidatesEmitted;
            public int gc0Collections, gc1Collections, gc2Collections, nonzeroGcTicks, recorderOverflowTicks, invalidRecorderTicks;
            public long mainThreadAllocatedBytes, allThreadGcMarkerNanoseconds, gcAllocationEvents, steadyCapacityGrowths, solverFaults;
            public float maxPenetration, maxCorrection;
            public ulong finalStateHash;
        }

        [Serializable] public sealed class WindowReport
        {
            public int firstTick, completedTicks, drivenBodies, fullyCommandedBodies, horizontalMovingBodies, oneCentimeterExcursionBodies, pressureBodies;
            public int minHorizontalMoving = int.MaxValue, minHorizontalDisplacing = int.MaxValue;
            public long successfulCommands, sampledLateralPressureContacts;
            public float maxHorizontalExcursion;
            public double horizontalMovingCoverage, oneCentimeterExcursionCoverage, pressureCoverage;
            public bool passed;
        }

        struct BodyWindowEvidence
        {
            public float3 origin;
            public int successfulCommands, horizontalMovingTicks, horizontalDisplacingTicks, pressureObservedTicks;
            public double horizontalPath;
            public float maxHorizontalExcursion;
        }

        [Serializable] sealed class Status
        {
            public string runId, status, phase, outputDirectory, error, utc;
            public int caseIndex, completedWarmupTicks, completedTicks, targetTicks;
            public bool running, workerCountRestored;
        }

        [Serializable] sealed class SourceManifest
        {
            public string schema = "block-simulation-prototype-sources-v1", runId, startedUtc, unityVersion, runtimeMvid;
            public string editorProcessStartedUtc, editorProcessName, editorExecutablePath;
            public int editorProcessId;
            public double maximumTickSeconds;
            public SourceIdentity[] inputs;
        }

        [Serializable] sealed class Breadcrumb
        {
            public string schema = "block-simulation-prototype-breadcrumb-v2", runId, utc, status, phase, caseName;
            public string sourceManifestPath, sourceManifestSha256, runtimeMvid, editorProcessStartedUtc;
            public long sequence;
            public int editorProcessId, caseIndex, tickIndex, batchLastTickIndex, completedWarmupTicks, completedTicks;
            public double maximumTickSeconds;
            public bool running;
            public int lastReturnedWarmupTick = -1;
            public SolverSample lastReturnedWarmupDiagnostic;
        }

        [Serializable] struct SolverSample
        {
            public long solverTick;
            public int solverFaultCode;
            public long normalCalls, normalFastPaths, normalCertificateChecks, normalStalls;
            public long normalBurstCalls, normalManagedCalls, normalInitialConstraintChecks, normalAdjacencyChecks, normalTreeNodeVisits;
            public long normalAdjacencyVisits, normalAdjacencyDuplicateSkips;
            public int normalMaxTreeNodes, normalFaultReason, normalWorstContact;
            public float normalFeasibilityResidual, normalComplementarityResidual, normalRawComplementarity;
            public float normalPlaneResidual, normalConeResidual, normalVelocityTolerance, normalOutputQuantization, normalAllowedDisplacement;
            public int supportRefreshCalls, supportMapBatches;
            public long supportDirtyBodies, supportGeometryDirtyBodies, supportMapQueries, supportMovedBodies;
            public double supportMovementMs, supportPoseMs, supportMapBatchMs, supportMapBurstMs;
            public double supportEdgeRepairMs, supportClosureMs, supportCleanupMs, supportRefreshMs, supportTotalMs;
        }

        [Serializable] sealed class TerminalSolverDiagnostic
        {
            public string schema = "block-simulation-prototype-terminal-solver-v1", runId, utc, status, caseName, error;
            public int caseIndex, completedWarmupTicks, completedTicks;
            public bool measurementPhase;
            public SolverSample diagnostics;
        }

        struct TickSample
        {
            public int tick, active, resting, moving, pairs, contacts, dynamicContacts, hashEntries, schedulingBacklog, candidatesRequired, candidatesEmitted;
            public int horizontalMoving, horizontalDisplacing, loadedDynamicContacts, lateralPressureContacts, pressureParticipants;
            public double stepMs, simulationMs, admissionMs, publicationMs, validationMs, tickMs, wallSeconds, lateMs, solverDebtMs;
            public double beginWakeMs, broadphaseMs, contactsMs, velocityMs, moveProjectMs, finishCommitMs;
            public int broadphaseCalls, broadphaseAllActiveCalls;
            public double broadphasePrepareMs, broadphaseCountMs, broadphasePrefixMs, broadphaseEmitMs, broadphaseSortMs;
            public long allocatedBytes, allThreadGcNanoseconds, gcEvents, capacityGrowths, faults;
            public bool recorderValid, recorderOverflow;
            public float maxPenetration, maxCorrection;
            public long axisPivots, axisChecks, normalBlockPasses, obliqueUpdates, frictionRetractions;
            public SolverSample solver;
        }

        sealed class Fixture : IDisposable
        {
            public BlockSimulation world;
            public BlockMapCollisionData map;
            public BlockSimHandle[] driven;
            public List<BlockSimState> states;
            public uint[] seenIds;
            public uint[] pressureSeenIds;
            public float3[] positions;
            public BodyWindowEvidence[] bodyWindows;
            public WindowReport[] windows;
            public List<BlockSimContactEvidence> contactEvidence;
            public uint validationVersion;
            public int moving, horizontalMoving, horizontalDisplacing, loadedDynamicContacts, lateralPressureContacts, pressureParticipants;
            public int expectedBodies, expectedResting;

            public void Dispose()
            {
                try { world?.Dispose(); }
                finally { map?.Dispose(); }
            }
        }

        sealed class RunContext
        {
            public RunReport report;
            public Fixture fixture;
            public CaseReport current;
            public TickSample[] samples;
            public SolverSample[] warmupSamples;
            public int caseIndex, warmupDone, measuredDone, gc0, gc1, gc2;
            public long phaseStarted, lastStatus, baselineGrowth, breadcrumbSequence, callbackStarted, previousCallbackEnded;
            public double solverDebt;
            public ProfilerRecorder allocationRecorder;
            public bool recorderCreated, measuring;
        }

        [MenuItem("积木对战/验证/数据积木原型性能（独立报告，2 Workers）")]
        public static void Run()
        {
            if (_run != null) throw new InvalidOperationException("A block simulation prototype probe is already running.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Start the prototype probe only in an idle, compiled Editor outside Play Mode.");
            string runId = "Prototype-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string output = Path.GetFullPath(Path.Combine(ReportsRoot, runId));
            if (Directory.Exists(output)) throw new IOException("Refusing to overwrite an existing prototype run.");
            Directory.CreateDirectory(output);
            LastOutputDirectory = output; StatusPath = Path.Combine(output, "status.json");
            var report = new RunReport
            {
                runId = runId, startedUtc = Utc(), status = "running", outputDirectory = output,
                unityVersion = Application.unityVersion, runtimeMvid = typeof(BlockSimulation).Module.ModuleVersionId.ToString(),
                processor = SystemInfo.processorType, processorCount = SystemInfo.processorCount,
                systemMemoryMb = SystemInfo.systemMemorySize, operatingSystem = SystemInfo.operatingSystem,
                burstEnabled = BurstCompiler.Options.EnableBurstCompilation,
                originalWorkers = JobsUtility.JobWorkerCount, inputs = CaptureSources(), cases = new CaseReport[2]
            };
            _run = new RunContext { report = report, samples = new TickSample[MeasurementTicks], warmupSamples = new SolverSample[WarmupTicks] };
            AssemblyReloadEvents.beforeAssemblyReload += Abort;
            EditorApplication.quitting += Abort;
            try
            {
                SaveSourceManifest(_run);
                SaveBreadcrumb(_run, "allocation-controls", -1, -1);
                SaveStatus(_run, "allocation-controls");
                JobsUtility.JobWorkerCount = WorkerCount;
                report.observedWorkers = JobsUtility.JobWorkerCount;
                if (report.observedWorkers != WorkerCount) throw new InvalidOperationException("The two-worker limit was not applied.");
                _run.allocationRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 4096,
                    ProfilerRecorderOptions.SumAllSamplesInFrame);
                _run.recorderCreated = true; _run.allocationRecorder.Stop();
                VerifyAllocationRecorder(_run);
                StartCase(_run);
                EditorApplication.update += Update;
                UnityEngine.Debug.Log("[BlockSimulationPrototype] Started: " + output);
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception exception) { Finish("failed", exception.ToString()); throw; }
        }

        public static string ReadStatus()
        {
            string result = StatusPath != null && File.Exists(StatusPath) ? File.ReadAllText(StatusPath) : "No prototype run has started in this domain.";
            UnityEngine.Debug.Log(result); return result;
        }

        public static void Abort()
        {
            if (_run != null) Finish("aborted", "Stopped before completion, Editor shutdown or assembly reload.");
        }

        static void VerifyAllocationRecorder(RunContext run)
        {
            // A zero event count is meaningful only if this process observes both main and worker allocations.
            // This control is outside warm-up and measured ticks, and does not enter any GC budget.
            var recorder = run.allocationRecorder;
            recorder.Reset(); recorder.Start();
            recorder.Stop();
            run.report.allocationBaselineEvents = AllocationEvents(recorder);
            recorder.Reset(); recorder.Start();
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            var control = new byte[4096];
            control[0] = 1; GC.KeepAlive(control);
            run.report.threadByteCounterControlBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            recorder.Stop();
            run.report.allocationMainControlEvents = AllocationEvents(recorder);
            run.report.threadByteCounterSupported = run.report.threadByteCounterControlBytes >= 4096;
            using var ready = new AutoResetEvent(false);
            using var start = new AutoResetEvent(false);
            using var done = new AutoResetEvent(false);
            var worker = new Thread(() =>
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    ready.Set(); start.WaitOne();
                    if (pass == 2)
                    { var workerControl = new byte[4096]; workerControl[0] = 1; GC.KeepAlive(workerControl); }
                    done.Set();
                }
            }) { IsBackground = true };
            worker.Start(); ready.WaitOne();
            start.Set(); done.WaitOne(); ready.WaitOne(); // Warm the identical handshake outside recording.
            recorder.Reset(); recorder.Start();
            start.Set(); done.WaitOne();
            recorder.Stop();
            run.report.allocationWorkerBaselineEvents = AllocationEvents(recorder);
            ready.WaitOne();
            recorder.Reset(); recorder.Start();
            start.Set(); done.WaitOne();
            recorder.Stop(); worker.Join();
            run.report.allocationWorkerControlEvents = AllocationEvents(recorder);
            run.report.allocationPositiveControlPassed = recorder.Valid && !recorder.WrappedAround &&
                run.report.allocationBaselineEvents == 0 && run.report.allocationWorkerBaselineEvents == 0 &&
                run.report.allocationMainControlEvents > 0 && run.report.allocationWorkerControlEvents > 0;
            run.allocationRecorder = recorder;
            if (!run.report.allocationPositiveControlPassed)
                throw new InvalidOperationException("The all-thread GC.Alloc recorder did not observe the allocation positive control.");
        }

        static long AllocationEvents(ProfilerRecorder recorder)
        {
            long events = 0;
            for (int sample = 0; sample < recorder.Count; sample++) events += recorder.GetSample(sample).Count;
            return events;
        }

        static void Update()
        {
            var run = _run;
            if (run == null) return;
            if (run.measuring)
            {
                long started = Stopwatch.GetTimestamp();
                if (run.previousCallbackEnded != 0)
                {
                    double gap = Milliseconds(started - run.previousCallbackEnded);
                    run.current.callbackGapMilliseconds += gap;
                    run.current.callbackGapPeakMilliseconds = Math.Max(run.current.callbackGapPeakMilliseconds, gap);
                }
                else run.current.initialMeasurementMilliseconds = Milliseconds(started - run.phaseStarted);
                run.callbackStarted = started;
                run.current.measurementCallbacks++;
            }
            try
            {
                CheckExternalFault(run);
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("Editor state changed during the isolated prototype run.");
                if (JobsUtility.JobWorkerCount != WorkerCount) throw new InvalidOperationException("Job worker count changed during measurement.");
                if (!run.measuring)
                {
                    // Warm-up is explicitly unpaced and excluded from all performance samples.
                    for (int batch = 0; batch < 8 && run.warmupDone < WarmupTicks; batch++)
                    {
                        // Persist the exact next warm tick before entering jobs that can block the main thread.
                        SaveBreadcrumb(run, "warming-tick", run.warmupDone, run.warmupDone);
                        long tickStarted = Stopwatch.GetTimestamp();
                        Drive(run.fixture, run.warmupDone);
                        run.fixture.world.Step(run.fixture.map.Snapshot, Dt);
                        run.fixture.world.CopyStates(run.fixture.states);
                        ValidateAndHash(run.fixture);
                        run.warmupSamples[run.warmupDone] = CaptureSolverSample(run.fixture.world);
                        CheckTickDuration(run, "warming", run.warmupDone, tickStarted);
                        CheckExternalFault(run);
                        run.warmupDone++;
                        run.current.completedWarmupTicks = run.warmupDone;
                    }
                    if (run.warmupDone == WarmupTicks)
                    {
                        run.baselineGrowth = run.fixture.world.Diagnostics.CapacityGrowths;
                        run.gc0 = GC.CollectionCount(0); run.gc1 = GC.CollectionCount(1); run.gc2 = GC.CollectionCount(2);
                        run.solverDebt = 0; run.measuring = true; run.phaseStarted = Stopwatch.GetTimestamp();
                        run.previousCallbackEnded = 0;
                        SaveBreadcrumb(run, "measurement-ready", 0, 0);
                        SaveStatus(run, "measuring");
                    }
                }
                else
                {
                    // Snapshot due work at callback entry. A time slice yields to the Editor; all pending ticks remain due.
                    double elapsed = SecondsSince(run.phaseStarted);
                    int due = Math.Min(MeasurementTicks, 1 + (int)Math.Floor(elapsed * 60));
                    int backlog = Math.Max(0, due - run.measuredDone);
                    run.current.maxSchedulingBacklogTicks = Math.Max(run.current.maxSchedulingBacklogTicks, backlog);
                    long callbackStarted = Stopwatch.GetTimestamp();
                    // Measurement breadcrumbs describe a batch range, not an exact in-flight tick.
                    // IO is outside CPU/GC windows; phaseStarted is never adjusted, so throughput includes it.
                    if (run.measuredDone < due) SaveBreadcrumb(run, "measurement-batch", run.measuredDone, due - 1);
                    while (run.measuredDone < due)
                    {
                        MeasureTick(run, backlog);
                        if (SecondsSince(callbackStarted) >= .05) break;
                    }
                    if (run.measuredDone == MeasurementTicks)
                    {
                        CheckExternalFault(run);
                        CompleteCase(run);
                        if (++run.caseIndex == run.report.cases.Length) { Finish("completed", null); return; }
                        StartCase(run);
                    }
                }
                if (SecondsSince(run.lastStatus) >= 1) SaveStatus(run, run.measuring ? "measuring" : "warming");
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception exception) { EndMeasurementCallback(run); Finish("failed", exception.ToString()); }
            finally { EndMeasurementCallback(run); }
        }

        static void EndMeasurementCallback(RunContext run)
        {
            if (run.callbackStarted == 0) return;
            long ended = Stopwatch.GetTimestamp();
            run.current.callbackMilliseconds += Milliseconds(ended - run.callbackStarted);
            run.previousCallbackEnded = ended;
            run.callbackStarted = 0;
        }

        static void StartCase(RunContext run)
        {
            run.warmupDone = run.measuredDone = 0; run.measuring = false;
            run.current = null;
            SaveBreadcrumb(run, "case-setup", 0, 0);
            SaveStatus(run, "case-setup");
            run.fixture?.Dispose(); run.fixture = null;
            run.fixture = CreateFixture(run.caseIndex);
            run.current = new CaseReport
            {
                name = run.caseIndex == 0 ? "5000-resting-plus-64-driven" : "4000-active-contact-and-push",
                expectedBodies = run.fixture.expectedBodies, expectedResting = run.fixture.expectedResting,
                drivenBodies = run.fixture.driven.Length, geometryHash = run.fixture.map.GeometryHash,
                evidenceWindows = run.fixture.windows,
                startedUtc = Utc(), p95BudgetMs = run.caseIndex == 0 ? 1.5 : 6,
                p99BudgetMs = run.caseIndex == 0 ? 0 : 10
            };
            run.report.cases[run.caseIndex] = run.current;
            Array.Clear(run.samples, 0, run.samples.Length);
            Array.Clear(run.warmupSamples, 0, run.warmupSamples.Length);
            SaveBreadcrumb(run, "warming-ready", 0, 0);
            SaveStatus(run, "warming");
        }

        static Fixture CreateFixture(int caseIndex)
        {
            var fixture = new Fixture();
            try
            {
                var builder = new BlockMapCollisionBuilder();
                builder.AddBox(new float3(0, -.1f, 0), new float3(40, .1f, 40), 1);
                float centerX = caseIndex == 0 ? -9 : 0;
                float half = caseIndex == 0 ? 1.5f : 10.4f;
                builder.AddBox(new float3(centerX - half - .1f, .5f, 0), new float3(.1f, .5f, half + .2f), 2);
                builder.AddBox(new float3(centerX + half + .1f, .5f, 0), new float3(.1f, .5f, half + .2f), 3);
                builder.AddBox(new float3(centerX, .5f, -half - .1f), new float3(half, .5f, .1f), 4);
                builder.AddBox(new float3(centerX, .5f, half + .1f), new float3(half, .5f, .1f), 5);
                fixture.map = builder.Build(Allocator.Persistent);
                fixture.expectedBodies = caseIndex == 0 ? 5064 : 4000;
                fixture.expectedResting = caseIndex == 0 ? 5000 : 0;
                fixture.world = new BlockSimulation(32);
                fixture.states = new List<BlockSimState>(fixture.expectedBodies);
                fixture.seenIds = new uint[fixture.expectedBodies + 1];
                fixture.pressureSeenIds = new uint[fixture.expectedBodies + 1];
                fixture.positions = new float3[fixture.expectedBodies + 1];
                fixture.bodyWindows = new BodyWindowEvidence[(fixture.expectedBodies + 1) * EvidenceWindowCount];
                fixture.contactEvidence = new List<BlockSimContactEvidence>(fixture.expectedBodies * 16);
                fixture.windows = new WindowReport[EvidenceWindowCount];
                for (int w = 0; w < fixture.windows.Length; w++)
                    fixture.windows[w] = new WindowReport { firstTick = w * EvidenceWindowTicks, drivenBodies = caseIndex == 0 ? 64 : 4000 };
                fixture.driven = new BlockSimHandle[caseIndex == 0 ? 64 : 4000];
                fixture.world.SetObservers(caseIndex == 0 ? new[] { new float3(-9, 0, 0) } : new[] { new float3(100, 0, 100) });
                ulong id = 0;
                if (caseIndex == 0)
                    for (int i = 0; i < 5000; i++) AddFixtureBody(fixture, ++id,
                        new float3((i % 50) * .302f, .15f, (i / 50) * .302f));
                for (int i = 0; i < fixture.driven.Length; i++)
                {
                    int width = caseIndex == 0 ? 8 : 64;
                    float x = caseIndex == 0 ? -10.05f + i % width * .30f : (i % width - 31.5f) * .30f;
                    float z = caseIndex == 0 ? -1.05f + i / width * .30f : (i / width - 31) * .30f;
                    fixture.driven[i] = AddFixtureBody(fixture, ++id, new float3(x, .15f, z));
                }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        static BlockSimHandle AddFixtureBody(Fixture fixture, ulong id, float3 position)
        {
            var handle = new BlockSimHandle(1, 1, id);
            if (!fixture.world.Add(new BlockSimState { handle = handle, owner = (byte)(1 + id % 2), position = position, mode = BlockSimMode.Active }))
                throw new InvalidOperationException("Failed to populate the exact, unreduced workload.");
            fixture.positions[(int)id] = position;
            return handle;
        }

        static void Drive(Fixture fixture, int tick, int measuredTick = -1)
        {
            // Opposing neighbours really push into one another; the shared Z direction changes every 90 ticks.
            // Each body receives a finite impulse request. No teleport, velocity reset or sleep override is used.
            float z = ((tick / 90) & 1) == 0 ? .4f : -.4f;
            for (int i = 0; i < fixture.driven.Length; i++)
            {
                float x = ((i + tick / 120) & 1) == 0 ? .8f : -.8f;
                if (!fixture.world.ApplyPush(fixture.driven[i], math.normalize(new float3(x, 0, z)), .9f, .08f))
                    throw new InvalidOperationException("A driven full handle rejected its push command.");
                if (measuredTick >= 0)
                {
                    int index = (measuredTick / EvidenceWindowTicks) * (fixture.expectedBodies + 1) + (int)fixture.driven[i].id;
                    fixture.bodyWindows[index].successfulCommands++;
                }
            }
        }

        static void MeasureTick(RunContext run, int schedulingBacklog)
        {
            var recorder = run.allocationRecorder;
            recorder.Reset(); recorder.Start();
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long tickStarted = Stopwatch.GetTimestamp();
            Drive(run.fixture, WarmupTicks + run.measuredDone, run.measuredDone);
            long stepStarted = Stopwatch.GetTimestamp();
            run.fixture.world.Step(run.fixture.map.Snapshot, Dt);
            long stepFinished = Stopwatch.GetTimestamp();
            run.fixture.world.CopyStates(run.fixture.states);
            long publicationFinished = Stopwatch.GetTimestamp();
            ulong hash = ValidateAndHash(run.fixture, run.measuredDone);
            long tickFinished = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            recorder.Stop();
            long allThreadNanoseconds = 0;
            for (int sample = 0; sample < recorder.Count; sample++) allThreadNanoseconds += recorder.GetSample(sample).Value;
            long allocationEvents = AllocationEvents(recorder);
            bool valid = recorder.Valid, overflow = recorder.WrappedAround;
            run.allocationRecorder = recorder;
            var diagnostic = run.fixture.world.Diagnostics;
            double fullTickMs = Milliseconds(tickFinished - tickStarted);
            run.solverDebt = Math.Max(0, run.solverDebt + fullTickMs - TickMs);
            double late = Math.Max(0, Milliseconds(tickStarted - run.phaseStarted) - run.measuredDone * TickMs);
            var current = run.current;
            current.minActive = Math.Min(current.minActive, diagnostic.ActiveCount); current.maxActive = Math.Max(current.maxActive, diagnostic.ActiveCount);
            current.minResting = Math.Min(current.minResting, diagnostic.RestingCount); current.maxResting = Math.Max(current.maxResting, diagnostic.RestingCount);
            current.minPairs = Math.Min(current.minPairs, diagnostic.PairCount); current.maxPairs = Math.Max(current.maxPairs, diagnostic.PairCount);
            current.minContacts = Math.Min(current.minContacts, diagnostic.ContactCount); current.maxContacts = Math.Max(current.maxContacts, diagnostic.ContactCount);
            current.minDynamicContacts = Math.Min(current.minDynamicContacts, diagnostic.DynamicContactCount);
            current.maxDynamicContacts = Math.Max(current.maxDynamicContacts, diagnostic.DynamicContactCount);
            current.minMoving = Math.Min(current.minMoving, run.fixture.moving); current.maxMoving = Math.Max(current.maxMoving, run.fixture.moving);
            current.minHorizontalMoving = Math.Min(current.minHorizontalMoving, run.fixture.horizontalMoving); current.maxHorizontalMoving = Math.Max(current.maxHorizontalMoving, run.fixture.horizontalMoving);
            current.minHorizontalDisplacing = Math.Min(current.minHorizontalDisplacing, run.fixture.horizontalDisplacing); current.maxHorizontalDisplacing = Math.Max(current.maxHorizontalDisplacing, run.fixture.horizontalDisplacing);
            current.minLoadedDynamicContacts = Math.Min(current.minLoadedDynamicContacts, run.fixture.loadedDynamicContacts); current.maxLoadedDynamicContacts = Math.Max(current.maxLoadedDynamicContacts, run.fixture.loadedDynamicContacts);
            current.minLateralPressureContacts = Math.Min(current.minLateralPressureContacts, run.fixture.lateralPressureContacts); current.maxLateralPressureContacts = Math.Max(current.maxLateralPressureContacts, run.fixture.lateralPressureContacts);
            current.minPressureParticipants = Math.Min(current.minPressureParticipants, run.fixture.pressureParticipants); current.maxPressureParticipants = Math.Max(current.maxPressureParticipants, run.fixture.pressureParticipants);
            if (run.fixture.horizontalMoving == 0 && run.fixture.horizontalDisplacing == 0 && run.fixture.lateralPressureContacts == 0)
                current.inactiveWorkloadTicks++;
            current.measuredFullTickMilliseconds += fullTickMs;
            current.measuredSimulationMilliseconds += Milliseconds(publicationFinished - tickStarted);
            current.measuredValidationMilliseconds += Milliseconds(tickFinished - publicationFinished);
            current.maxHashEntries = Math.Max(current.maxHashEntries, diagnostic.HashEntryCount);
            current.maximumCandidatesRequired = Math.Max(current.maximumCandidatesRequired, diagnostic.CandidateRequired);
            current.maximumCandidatesEmitted = Math.Max(current.maximumCandidatesEmitted, diagnostic.CandidateEmitted);
            if (diagnostic.CandidateRequired != diagnostic.CandidateEmitted) current.candidateMismatchTicks++;
            current.mainThreadAllocatedBytes += allocated; current.allThreadGcMarkerNanoseconds += allThreadNanoseconds;
            current.gcAllocationEvents += allocationEvents;
            if ((run.report.threadByteCounterSupported && allocated != 0) || allocationEvents != 0) current.nonzeroGcTicks++;
            if (!valid) current.invalidRecorderTicks++;
            if (overflow) current.recorderOverflowTicks++;
            current.solverDebtPeakMs = Math.Max(current.solverDebtPeakMs, run.solverDebt);
            current.maxSchedulingLateMs = Math.Max(current.maxSchedulingLateMs, late);
            current.maxPenetration = Math.Max(current.maxPenetration, diagnostic.MaxPenetration);
            current.maxCorrection = Math.Max(current.maxCorrection, diagnostic.MaxCorrection);
            current.finalStateHash = hash;
            run.samples[run.measuredDone] = new TickSample
            {
                tick = run.measuredDone, stepMs = Milliseconds(stepFinished - stepStarted), tickMs = fullTickMs,
                simulationMs = Milliseconds(publicationFinished - tickStarted), admissionMs = Milliseconds(stepStarted - tickStarted),
                publicationMs = Milliseconds(publicationFinished - stepFinished), validationMs = Milliseconds(tickFinished - publicationFinished),
                beginWakeMs = diagnostic.BeginWakeMilliseconds, broadphaseMs = diagnostic.BroadphaseMilliseconds,
                broadphaseCalls = diagnostic.BroadphaseCalls, broadphaseAllActiveCalls = diagnostic.BroadphaseAllActiveCalls,
                broadphasePrepareMs = diagnostic.BroadphasePrepareMilliseconds, broadphaseCountMs = diagnostic.BroadphaseCountMilliseconds,
                broadphasePrefixMs = diagnostic.BroadphasePrefixMilliseconds, broadphaseEmitMs = diagnostic.BroadphaseEmitMilliseconds,
                broadphaseSortMs = diagnostic.BroadphaseSortMilliseconds,
                contactsMs = diagnostic.ContactsMilliseconds, velocityMs = diagnostic.VelocityMilliseconds,
                moveProjectMs = diagnostic.MoveProjectMilliseconds, finishCommitMs = diagnostic.FinishCommitMilliseconds,
                wallSeconds = SecondsSince(run.phaseStarted), lateMs = late, solverDebtMs = run.solverDebt,
                allocatedBytes = allocated, allThreadGcNanoseconds = allThreadNanoseconds, gcEvents = allocationEvents,
                recorderValid = valid, recorderOverflow = overflow, active = diagnostic.ActiveCount, resting = diagnostic.RestingCount,
                moving = run.fixture.moving, dynamicContacts = diagnostic.DynamicContactCount,
                horizontalMoving = run.fixture.horizontalMoving, horizontalDisplacing = run.fixture.horizontalDisplacing,
                loadedDynamicContacts = run.fixture.loadedDynamicContacts, lateralPressureContacts = run.fixture.lateralPressureContacts, pressureParticipants = run.fixture.pressureParticipants,
                pairs = diagnostic.PairCount, contacts = diagnostic.ContactCount, hashEntries = diagnostic.HashEntryCount,
                candidatesRequired = diagnostic.CandidateRequired, candidatesEmitted = diagnostic.CandidateEmitted,
                capacityGrowths = diagnostic.CapacityGrowths, faults = diagnostic.SolverFaults,
                maxPenetration = diagnostic.MaxPenetration, maxCorrection = diagnostic.MaxCorrection, schedulingBacklog = schedulingBacklog,
                axisPivots = diagnostic.AxisPivots, axisChecks = diagnostic.AxisConstraintChecks,
                normalBlockPasses = diagnostic.NormalBlockPasses, obliqueUpdates = diagnostic.ObliqueUpdates, frictionRetractions = diagnostic.FrictionRetractions,
                solver = CaptureSolverSample(run.fixture.world)
            };
            run.measuredDone++; current.completedTicks = run.measuredDone;
            CheckTickDuration(run, "measurement", run.measuredDone - 1, tickStarted);
            CheckExternalFault(run);
        }

        static void CheckTickDuration(RunContext run, string phase, int tickIndex, long started)
        {
            double elapsed = SecondsSince(started);
            if (elapsed > MaximumTickSeconds)
                throw new TimeoutException($"Prototype {phase} tick {tickIndex} in case {run.caseIndex} took {elapsed:F3} s, exceeding {MaximumTickSeconds:F0} s. The run failed; no simulated ticks were skipped.");
        }

        static void CheckExternalFault(RunContext run)
        {
            string path = Path.Combine(run.report.outputDirectory, "external-watchdog-fault.json");
            if (File.Exists(path)) throw new TimeoutException("External watchdog recorded an incomplete/overlong prototype operation: " + path);
        }

        static ulong ValidateAndHash(Fixture fixture, int measuredTick = -1)
        {
            if (fixture.world.Count != fixture.expectedBodies || fixture.states.Count != fixture.expectedBodies)
                throw new InvalidOperationException("The workload lost or duplicated a live body.");
            ulong hash = 1469598103934665603;
            fixture.moving = fixture.horizontalMoving = fixture.horizontalDisplacing = 0;
            fixture.loadedDynamicContacts = fixture.lateralPressureContacts = fixture.pressureParticipants = 0;
            int windowIndex = measuredTick < 0 ? -1 : measuredTick / EvidenceWindowTicks;
            int windowOffset = windowIndex * (fixture.expectedBodies + 1);
            if (++fixture.validationVersion == 0)
            {
                Array.Clear(fixture.seenIds, 0, fixture.seenIds.Length);
                Array.Clear(fixture.pressureSeenIds, 0, fixture.pressureSeenIds.Length);
                fixture.validationVersion = 1;
            }
            for (int i = 0; i < fixture.states.Count; i++)
            {
                var state = fixture.states[i];
                if (!state.handle.IsValid || state.handle.epoch != 1 || state.handle.round != 1 || state.handle.generation != 1 ||
                    state.handle.id < 1 || state.handle.id > (ulong)fixture.expectedBodies ||
                    state.owner != (byte)(1 + state.handle.id % 2) ||
                    !math.all(math.isfinite(state.position)) || !math.all(math.isfinite(state.velocity)))
                    throw new InvalidOperationException("A committed body has invalid lifecycle or non-finite data.");
                int identity = (int)state.handle.id;
                if (fixture.seenIds[identity] == fixture.validationVersion)
                    throw new InvalidOperationException("The workload duplicated a stable body identity.");
                fixture.seenIds[identity] = fixture.validationVersion;
                if (math.lengthsq(state.velocity) > .000001f) fixture.moving++;
                float horizontalSpeed = math.length(state.velocity.xz);
                float horizontalDelta = math.length(state.position.xz - fixture.positions[identity].xz);
                bool isDriven = identity > fixture.expectedResting;
                if (isDriven && horizontalSpeed > HorizontalSpeedThreshold) fixture.horizontalMoving++;
                if (isDriven && horizontalDelta > .000001f) fixture.horizontalDisplacing++;
                if (measuredTick >= 0)
                {
                    ref BodyWindowEvidence body = ref fixture.bodyWindows[windowOffset + identity];
                    if (measuredTick % EvidenceWindowTicks == 0) body.origin = fixture.positions[identity];
                    body.horizontalPath += horizontalDelta;
                    body.maxHorizontalExcursion = math.max(body.maxHorizontalExcursion, math.length(state.position.xz - body.origin.xz));
                    if (horizontalSpeed > HorizontalSpeedThreshold) body.horizontalMovingTicks++;
                    if (horizontalDelta > .000001f) body.horizontalDisplacingTicks++;
                }
                fixture.positions[identity] = state.position;
                uint3 bits = math.asuint(state.position);
                hash = (hash ^ state.handle.id) * 1099511628211;
                hash = (hash ^ bits.x) * 1099511628211; hash = (hash ^ bits.y) * 1099511628211; hash = (hash ^ bits.z) * 1099511628211;
            }
            // This is sampled final-substep retained ledger evidence. Read before the next Drive mutation.
            // Copy/analysis belongs to validation, after the CPU-gated state publication timestamp.
            fixture.world.CopyContactEvidence(fixture.contactEvidence);
            for (int i = 0; i < fixture.contactEvidence.Count; i++)
            {
                var contact = fixture.contactEvidence[i];
                int a = ValidateEvidenceHandle(fixture, contact.A);
                if (!math.all(math.isfinite(contact.Normal)) || !math.all(math.isfinite(contact.TangentImpulse)) ||
                    !math.isfinite(contact.Separation) || !math.isfinite(contact.NormalImpulse) || contact.NormalImpulse < -ImpulseThreshold)
                    throw new InvalidOperationException("Contact evidence contains invalid solver data.");
                if (!contact.B.IsValid) continue; // Floor/map support cannot prove block-to-block lateral pressure.
                int b = ValidateEvidenceHandle(fixture, contact.B);
                if (a == b) throw new InvalidOperationException("Contact evidence refers to the same body twice.");
                float currentSeparation = math.cmax(math.abs(fixture.positions[a] - fixture.positions[b])) - BlockSimulation.Edge;
                if (currentSeparation > BlockSimulation.ContactSkin + .00001f) continue;
                if (contact.NormalImpulse > ImpulseThreshold) fixture.loadedDynamicContacts++;
                float lateralNormalImpulse = contact.NormalImpulse * math.length(contact.Normal.xz);
                if (lateralNormalImpulse <= ImpulseThreshold) continue;
                fixture.lateralPressureContacts++;
                ObservePressure(fixture, a, windowOffset, measuredTick);
                ObservePressure(fixture, b, windowOffset, measuredTick);
            }
            if (measuredTick >= 0)
            {
                var window = fixture.windows[windowIndex];
                window.completedTicks++;
                window.minHorizontalMoving = Math.Min(window.minHorizontalMoving, fixture.horizontalMoving);
                window.minHorizontalDisplacing = Math.Min(window.minHorizontalDisplacing, fixture.horizontalDisplacing);
                window.sampledLateralPressureContacts += fixture.lateralPressureContacts;
                if (window.completedTicks == EvidenceWindowTicks) CompleteEvidenceWindow(fixture, windowIndex);
            }
            return hash;
        }

        static int ValidateEvidenceHandle(Fixture fixture, BlockSimHandle handle)
        {
            if (!handle.IsValid || handle.epoch != 1 || handle.round != 1 || handle.generation != 1 ||
                handle.id < 1 || handle.id > (ulong)fixture.expectedBodies || fixture.seenIds[(int)handle.id] != fixture.validationVersion)
                throw new InvalidOperationException("Contact evidence contains a stale or foreign full handle.");
            return (int)handle.id;
        }

        static void ObservePressure(Fixture fixture, int identity, int windowOffset, int measuredTick)
        {
            if (fixture.pressureSeenIds[identity] == fixture.validationVersion) return;
            fixture.pressureSeenIds[identity] = fixture.validationVersion;
            if (identity > fixture.expectedResting) fixture.pressureParticipants++;
            if (measuredTick >= 0) fixture.bodyWindows[windowOffset + identity].pressureObservedTicks++;
        }

        static void CompleteEvidenceWindow(Fixture fixture, int index)
        {
            var window = fixture.windows[index];
            int offset = index * (fixture.expectedBodies + 1);
            for (int i = 0; i < fixture.driven.Length; i++)
            {
                var body = fixture.bodyWindows[offset + (int)fixture.driven[i].id];
                window.successfulCommands += body.successfulCommands;
                if (body.successfulCommands == EvidenceWindowTicks) window.fullyCommandedBodies++;
                if (body.horizontalMovingTicks > 0) window.horizontalMovingBodies++;
                if (body.maxHorizontalExcursion >= SubstantiveExcursion) window.oneCentimeterExcursionBodies++;
                if (body.pressureObservedTicks > 0) window.pressureBodies++;
                window.maxHorizontalExcursion = math.max(window.maxHorizontalExcursion, body.maxHorizontalExcursion);
            }
            window.horizontalMovingCoverage = (double)window.horizontalMovingBodies / fixture.driven.Length;
            window.oneCentimeterExcursionCoverage = (double)window.oneCentimeterExcursionBodies / fixture.driven.Length;
            window.pressureCoverage = (double)window.pressureBodies / fixture.driven.Length;
            window.passed = window.completedTicks == EvidenceWindowTicks && window.fullyCommandedBodies == fixture.driven.Length &&
                window.oneCentimeterExcursionBodies > 0 && window.sampledLateralPressureContacts > 0 &&
                (fixture.expectedResting != 0 || window.pressureBodies == fixture.driven.Length);
        }

        static void CompleteCase(RunContext run)
        {
            var current = run.current;
            // Take the wall-clock terminal before summary sorting/CSV/report IO.
            current.wallSeconds = SecondsSince(run.phaseStarted);
            EndMeasurementCallback(run);
            current.actualTicksPerSecond = run.measuredDone / current.wallSeconds;
            int due = 1 + (int)Math.Floor(current.wallSeconds * 60);
            current.endDueBacklogTicks = Math.Max(0, due - run.measuredDone);
            current.finishedUtc = Utc();
            current.solverDebtEndMs = run.solverDebt;
            var step = new double[MeasurementTicks]; var ticks = new double[MeasurementTicks];
            var simulation = new double[MeasurementTicks]; var admission = new double[MeasurementTicks];
            var publication = new double[MeasurementTicks]; var validation = new double[MeasurementTicks];
            double sum = 0;
            for (int i = 0; i < MeasurementTicks; i++)
            {
                var sample = run.samples[i]; step[i] = sample.stepMs; ticks[i] = sample.tickMs; sum += step[i];
                simulation[i] = sample.simulationMs; admission[i] = sample.admissionMs;
                publication[i] = sample.publicationMs; validation[i] = sample.validationMs;
            }
            Array.Sort(step); Array.Sort(ticks);
            Array.Sort(simulation); Array.Sort(admission); Array.Sort(publication); Array.Sort(validation);
            current.stepMeanMs = sum / MeasurementTicks;
            current.stepP50Ms = Percentile(step, .5); current.stepP95Ms = Percentile(step, .95); current.stepP99Ms = Percentile(step, .99);
            current.stepPeakMs = step[step.Length - 1];
            current.tickP95Ms = Percentile(ticks, .95); current.tickP99Ms = Percentile(ticks, .99); current.tickPeakMs = ticks[ticks.Length - 1];
            current.simulationP95Ms = Percentile(simulation, .95); current.simulationP99Ms = Percentile(simulation, .99);
            current.simulationPeakMs = simulation[simulation.Length - 1]; current.admissionP95Ms = Percentile(admission, .95);
            current.publicationP95Ms = Percentile(publication, .95); current.validationP95Ms = Percentile(validation, .95);
            var diagnostic = run.fixture.world.Diagnostics;
            current.steadyCapacityGrowths = diagnostic.CapacityGrowths - run.baselineGrowth;
            current.solverFaults = diagnostic.SolverFaults;
            current.gc0Collections = GC.CollectionCount(0) - run.gc0; current.gc1Collections = GC.CollectionCount(1) - run.gc1; current.gc2Collections = GC.CollectionCount(2) - run.gc2;
            current.cpuBudgetPassed = current.simulationP95Ms <= current.p95BudgetMs &&
                (current.p99BudgetMs == 0 || current.simulationP99Ms <= current.p99BudgetMs);
            current.zeroManagedGcPassed = current.nonzeroGcTicks == 0 && current.invalidRecorderTicks == 0 && current.recorderOverflowTicks == 0;
            current.evidenceWindowsPassed = true;
            foreach (var window in current.evidenceWindows)
            {
                if (window.completedTicks == EvidenceWindowTicks) current.completedEvidenceWindows++;
                current.evidenceWindowsPassed &= window.passed;
            }
            current.workloadPassed = current.evidenceWindowsPassed && (run.caseIndex == 0
                ? current.minResting >= 5000 && current.maxActive <= 64 && current.maxActive > 0
                : current.minActive == 4000 && current.maxResting == 0 && current.minDynamicContacts > 0) && current.inactiveWorkloadTicks == 0;
            current.sustained60HzPassed = current.completedTicks == MeasurementTicks &&
                current.wallSeconds <= MeasurementTicks / 60.0 + .05 && current.endDueBacklogTicks <= 1;
            current.solverIntegrityPassed = current.solverFaults == 0 && current.minContacts > 0 && current.candidateMismatchTicks == 0;
            current.passed = current.cpuBudgetPassed && current.zeroManagedGcPassed && current.workloadPassed &&
                current.sustained60HzPassed && current.solverIntegrityPassed && current.steadyCapacityGrowths == 0;
            WriteWarmupSamples(Path.Combine(run.report.outputDirectory, current.name + "-warmup-diagnostics.csv"), run.warmupSamples, run.warmupDone);
            WriteSamples(Path.Combine(run.report.outputDirectory, current.name + "-ticks.csv"), run.samples, run.measuredDone);
            WriteBodyEvidence(Path.Combine(run.report.outputDirectory, current.name + "-body-windows.csv"), run.fixture, run.measuredDone);
            File.WriteAllText(Path.Combine(run.report.outputDirectory, current.name + "-summary.json"), JsonUtility.ToJson(current, true), new UTF8Encoding(false));
            SaveStatus(run, "case-completed");
        }

        static void Finish(string status, string error)
        {
            var run = _run;
            if (run == null) return;
            string externalFault = Path.Combine(run.report.outputDirectory, "external-watchdog-fault.json");
            if (File.Exists(externalFault))
            {
                status = "failed";
                error = (error ?? "") + "\nExternal watchdog fault evidence: " + externalFault;
            }
            EditorApplication.update -= Update;
            AssemblyReloadEvents.beforeAssemblyReload -= Abort;
            EditorApplication.quitting -= Abort;
            run.report.status = status; run.report.error = error; run.report.finishedUtc = Utc();
            if (run.fixture?.world != null)
                try
                {
                    var terminal = new TerminalSolverDiagnostic
                    {
                        runId = run.report.runId, utc = Utc(), status = status, caseName = run.current?.name,
                        error = error, caseIndex = run.caseIndex, completedWarmupTicks = run.warmupDone,
                        completedTicks = run.measuredDone, measurementPhase = run.measuring,
                        diagnostics = CaptureSolverSample(run.fixture.world)
                    };
                    WriteDurableJson(Path.Combine(run.report.outputDirectory, "terminal-solver-diagnostics.json"), JsonUtility.ToJson(terminal, true));
                }
                catch (Exception exception) { run.report.error = (run.report.error ?? "") + "\nTerminal solver diagnostics: " + exception; }
            if (run.current != null && run.current.finishedUtc == null)
            {
                run.current.error = error; run.current.finishedUtc = Utc();
                try { WriteWarmupSamples(Path.Combine(run.report.outputDirectory, run.current.name + "-partial-warmup-diagnostics.csv"), run.warmupSamples, run.warmupDone); }
                catch (Exception exception) { run.report.error = (run.report.error ?? "") + "\nPartial warm-up diagnostics: " + exception; }
                if (run.measuring)
                    try
                    {
                        WriteSamples(Path.Combine(run.report.outputDirectory, run.current.name + "-partial-ticks.csv"), run.samples, run.measuredDone);
                        WriteBodyEvidence(Path.Combine(run.report.outputDirectory, run.current.name + "-partial-body-windows.csv"), run.fixture, run.measuredDone);
                    }
                    catch (Exception exception) { run.report.error = (run.report.error ?? "") + "\nPartial CSV: " + exception; }
            }
            try { if (run.recorderCreated) run.allocationRecorder.Dispose(); }
            catch (Exception exception) { run.report.error = (run.report.error ?? "") + "\nRecorder disposal: " + exception; }
            try { run.fixture?.Dispose(); }
            catch (Exception exception) { run.report.error = (run.report.error ?? "") + "\nFixture disposal: " + exception; }
            finally
            {
                try { JobsUtility.JobWorkerCount = run.report.originalWorkers; run.report.workerCountRestored = JobsUtility.JobWorkerCount == run.report.originalWorkers; }
                catch (Exception exception) { run.report.error = (run.report.error ?? "") + "\nWorker restoration: " + exception; }
            }
            try
            {
                run.report.sourceIdentityUnchanged = SameSources(run.report.inputs, CaptureSources());
                bool allCases = status == "completed" && run.report.cases.Length == 2;
                foreach (var result in run.report.cases) allCases &= result != null && result.passed;
                run.report.passed = allCases && run.report.burstEnabled && run.report.allocationPositiveControlPassed &&
                    run.report.workerCountRestored && run.report.sourceIdentityUnchanged && string.IsNullOrEmpty(run.report.error) &&
                    !File.Exists(externalFault);
                WriteDurableJson(Path.Combine(run.report.outputDirectory, "summary.json"), JsonUtility.ToJson(run.report, true));
                SaveBreadcrumb(run, "terminal", run.measuredDone, run.measuredDone);
                SaveStatus(run, status);
            }
            finally { _run = null; }
            UnityEngine.Debug.Log("[BlockSimulationPrototype] " + status + "; passed=" + run.report.passed + "; " + run.report.outputDirectory);
        }

        static void SaveStatus(RunContext run, string phase)
        {
            long started = Stopwatch.GetTimestamp();
            var status = new Status
            {
                runId = run.report.runId, status = run.report.status, phase = phase, utc = Utc(),
                outputDirectory = run.report.outputDirectory, caseIndex = run.caseIndex,
                completedWarmupTicks = run.warmupDone, completedTicks = run.measuredDone, targetTicks = MeasurementTicks,
                running = run.report.status == "running", workerCountRestored = run.report.workerCountRestored, error = run.report.error
            };
            WriteAtomicJson(StatusPath, JsonUtility.ToJson(status, true), false);
            run.lastStatus = Stopwatch.GetTimestamp();
            if (run.measuring && run.current != null && run.current.finishedUtc == null)
            {
                double elapsed = Milliseconds(run.lastStatus - started);
                run.current.statusWrites++; run.current.statusIoMilliseconds += elapsed;
                run.current.statusIoPeakMilliseconds = Math.Max(run.current.statusIoPeakMilliseconds, elapsed);
            }
        }

        static void SaveSourceManifest(RunContext run)
        {
            var report = run.report;
            using (var process = Process.GetCurrentProcess())
            {
                report.editorProcessId = process.Id;
                report.editorProcessStartedUtc = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                report.editorProcessName = process.ProcessName;
                report.editorExecutablePath = process.MainModule.FileName;
            }
            report.sourceManifestPath = Path.Combine(report.outputDirectory, "source-identity.json");
            var manifest = new SourceManifest
            {
                runId = report.runId, startedUtc = report.startedUtc, unityVersion = report.unityVersion, runtimeMvid = report.runtimeMvid,
                editorProcessId = report.editorProcessId, editorProcessStartedUtc = report.editorProcessStartedUtc,
                editorProcessName = report.editorProcessName, editorExecutablePath = report.editorExecutablePath,
                maximumTickSeconds = MaximumTickSeconds, inputs = report.inputs
            };
            WriteDurableJson(report.sourceManifestPath, JsonUtility.ToJson(manifest, true));
            using var sha = SHA256.Create();
            report.sourceManifestSha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(report.sourceManifestPath))).Replace("-", "").ToLowerInvariant();
        }

        static void SaveBreadcrumb(RunContext run, string phase, int tickIndex, int batchLastTickIndex)
        {
            long started = Stopwatch.GetTimestamp();
            var breadcrumb = new Breadcrumb
            {
                runId = run.report.runId, utc = Utc(), sequence = ++run.breadcrumbSequence, status = run.report.status,
                running = run.report.status == "running", phase = phase, caseIndex = run.caseIndex,
                caseName = run.current?.name ?? (run.caseIndex == 0 ? "5000-resting-plus-64-driven" : "4000-active-contact-and-push"),
                tickIndex = tickIndex, batchLastTickIndex = batchLastTickIndex,
                completedWarmupTicks = run.warmupDone, completedTicks = run.measuredDone,
                editorProcessId = run.report.editorProcessId, editorProcessStartedUtc = run.report.editorProcessStartedUtc,
                sourceManifestPath = run.report.sourceManifestPath, sourceManifestSha256 = run.report.sourceManifestSha256,
                runtimeMvid = run.report.runtimeMvid, maximumTickSeconds = MaximumTickSeconds,
                lastReturnedWarmupTick = run.warmupDone - 1,
                lastReturnedWarmupDiagnostic = run.warmupDone == 0 ? default : run.warmupSamples[run.warmupDone - 1]
            };
            WriteAtomicJson(Path.Combine(run.report.outputDirectory, "breadcrumb.json"), JsonUtility.ToJson(breadcrumb, true), false);
            if (run.measuring && run.current != null && run.current.finishedUtc == null)
            {
                double elapsed = Milliseconds(Stopwatch.GetTimestamp() - started);
                run.current.breadcrumbWrites++; run.current.breadcrumbIoMilliseconds += elapsed;
                run.current.breadcrumbIoPeakMilliseconds = Math.Max(run.current.breadcrumbIoPeakMilliseconds, elapsed);
            }
        }

        static void WriteDurableJson(string path, string json) => WriteAtomicJson(path, json, true);

        static void WriteAtomicJson(string path, string json, bool forceDiskFlush)
        {
            // A completed atomic replacement is the progress signal observed by an external watchdog.
            // Progress needs process-visible publication, not a disk barrier per tick/batch. The unadjusted
            // throughput clock includes all IO. Manifests and terminal summaries still force disk persistence.
            string temporary = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(forceDiskFlush);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        static void WriteSamples(string path, TickSample[] samples, int count)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("tick,stepMs,simulationMs,admissionMs,publicationMs,validationMs,fullTickMs,beginWakeMs,broadphaseMs,contactsMs,velocityMs,moveProjectMs,finishCommitMs,wallSeconds,schedulingLateMs,schedulingBacklogTicks,solverDebtMs,active,resting,moving,pairs,contacts,dynamicContacts,hashEntries,candidatesRequired,candidatesEmitted,threadManagedBytes,allThreadGcAllocNanoseconds,gcAllocationEvents,recorderValid,recorderOverflow,capacityGrowths,solverFaults,maxPenetration,maxCorrection,axisPivots,axisChecks,normalBlockPasses,obliqueUpdates,frictionRetractions,horizontalMoving,horizontalDisplacing,loadedDynamicContacts,lateralPressureContacts,pressureParticipants,broadphaseCalls,broadphaseAllActiveCalls,broadphasePrepareMs,broadphaseCountMs,broadphasePrefixMs,broadphaseEmitMs,broadphaseSortMs," + SolverCsvHeader);
            for (int i = 0; i < count; i++)
            {
                var s = samples[i];
                writer.WriteLine(string.Join(",", s.tick, F(s.stepMs), F(s.simulationMs), F(s.admissionMs), F(s.publicationMs), F(s.validationMs), F(s.tickMs), F(s.beginWakeMs), F(s.broadphaseMs), F(s.contactsMs),
                    F(s.velocityMs), F(s.moveProjectMs), F(s.finishCommitMs), F(s.wallSeconds), F(s.lateMs), s.schedulingBacklog,
                    F(s.solverDebtMs), s.active, s.resting, s.moving, s.pairs, s.contacts, s.dynamicContacts, s.hashEntries, s.candidatesRequired, s.candidatesEmitted,
                    s.allocatedBytes, s.allThreadGcNanoseconds, s.gcEvents,
                    s.recorderValid ? 1 : 0, s.recorderOverflow ? 1 : 0, s.capacityGrowths, s.faults, F(s.maxPenetration), F(s.maxCorrection),
                    s.axisPivots, s.axisChecks, s.normalBlockPasses, s.obliqueUpdates, s.frictionRetractions,
                    s.horizontalMoving, s.horizontalDisplacing, s.loadedDynamicContacts, s.lateralPressureContacts, s.pressureParticipants, s.broadphaseCalls, s.broadphaseAllActiveCalls, F(s.broadphasePrepareMs),
                    F(s.broadphaseCountMs), F(s.broadphasePrefixMs), F(s.broadphaseEmitMs), F(s.broadphaseSortMs)) + "," + SolverCsv(s.solver));
            }
        }

        static SolverSample CaptureSolverSample(BlockSimulation world)
        {
            var normal = world.NormalDiagnostics;
            var support = world.SupportDiagnostics;
            var diagnostic = world.Diagnostics;
            return new SolverSample
            {
                solverTick = diagnostic.TickCount, solverFaultCode = diagnostic.FaultCode,
                normalCalls = normal.Calls, normalFastPaths = normal.FastPaths,
                normalCertificateChecks = normal.CertificateChecks, normalStalls = normal.Stalls,
                normalBurstCalls = normal.BurstCalls, normalManagedCalls = normal.ManagedCalls,
                normalInitialConstraintChecks = normal.InitialConstraintChecks, normalAdjacencyChecks = normal.AdjacencyChecks,
                normalAdjacencyVisits = normal.AdjacencyVisits, normalAdjacencyDuplicateSkips = normal.AdjacencyDuplicateSkips,
                normalTreeNodeVisits = normal.TreeNodeVisits, normalMaxTreeNodes = normal.MaxTreeNodes,
                normalFaultReason = normal.FaultReason, normalWorstContact = normal.WorstContact,
                normalFeasibilityResidual = normal.FeasibilityResidual, normalComplementarityResidual = normal.ComplementarityResidual,
                normalRawComplementarity = normal.RawComplementarity, normalPlaneResidual = normal.PlaneResidual,
                normalConeResidual = normal.ConeResidual, normalVelocityTolerance = normal.VelocityTolerance,
                normalOutputQuantization = normal.OutputQuantization, normalAllowedDisplacement = normal.AllowedDisplacement,
                supportRefreshCalls = support.RefreshCalls, supportMapBatches = support.MapBatches,
                supportDirtyBodies = support.DirtyBodies, supportGeometryDirtyBodies = support.GeometryDirtyBodies,
                supportMapQueries = support.MapQueries, supportMovedBodies = support.MovedBodies,
                supportMovementMs = support.MovementMilliseconds, supportPoseMs = support.PoseMilliseconds,
                supportMapBatchMs = support.MapBatchMilliseconds, supportMapBurstMs = support.MapBurstMilliseconds,
                supportEdgeRepairMs = support.EdgeRepairMilliseconds, supportClosureMs = support.ClosureMilliseconds,
                supportCleanupMs = support.CleanupMilliseconds, supportRefreshMs = support.RefreshMilliseconds,
                supportTotalMs = support.TotalMilliseconds
            };
        }

        const string SolverCsvHeader = "solverTick,solverFaultCode,normalCalls,normalFastPaths,normalCertificateChecks,normalStalls,normalBurstCalls,normalManagedCalls,normalInitialConstraintChecks,normalAdjacencyChecks,normalAdjacencyVisits,normalAdjacencyDuplicateSkips,normalTreeNodeVisits,normalMaxTreeNodes,normalFaultReason,normalWorstContact,normalFeasibilityResidual,normalComplementarityResidual,normalRawComplementarity,normalPlaneResidual,normalConeResidual,normalVelocityTolerance,normalOutputQuantization,normalAllowedDisplacement,supportRefreshCalls,supportMapBatches,supportDirtyBodies,supportGeometryDirtyBodies,supportMapQueries,supportMovedBodies,supportMovementMs,supportPoseMs,supportMapBatchMs,supportMapBurstMs,supportEdgeRepairMs,supportClosureMs,supportCleanupMs,supportRefreshMs,supportTotalMs";

        // Formatting and boxing occur only during CSV publication, outside measured CPU/GC windows.
        static string SolverCsv(SolverSample s) => string.Join(",",
            s.solverTick, s.solverFaultCode, s.normalCalls, s.normalFastPaths, s.normalCertificateChecks, s.normalStalls,
            s.normalBurstCalls, s.normalManagedCalls, s.normalInitialConstraintChecks, s.normalAdjacencyChecks,
            s.normalAdjacencyVisits, s.normalAdjacencyDuplicateSkips,
            s.normalTreeNodeVisits, s.normalMaxTreeNodes, s.normalFaultReason, s.normalWorstContact,
            F(s.normalFeasibilityResidual), F(s.normalComplementarityResidual), F(s.normalRawComplementarity),
            F(s.normalPlaneResidual), F(s.normalConeResidual), F(s.normalVelocityTolerance), F(s.normalOutputQuantization), F(s.normalAllowedDisplacement),
            s.supportRefreshCalls, s.supportMapBatches, s.supportDirtyBodies, s.supportGeometryDirtyBodies, s.supportMapQueries, s.supportMovedBodies,
            F(s.supportMovementMs), F(s.supportPoseMs), F(s.supportMapBatchMs), F(s.supportMapBurstMs),
            F(s.supportEdgeRepairMs), F(s.supportClosureMs), F(s.supportCleanupMs), F(s.supportRefreshMs), F(s.supportTotalMs));

        static void WriteWarmupSamples(string path, SolverSample[] samples, int count)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("warmupTick," + SolverCsvHeader);
            for (int i = 0; i < count; i++) writer.WriteLine(i.ToString(CultureInfo.InvariantCulture) + "," + SolverCsv(samples[i]));
        }

        static void WriteBodyEvidence(string path, Fixture fixture, int measuredTicks)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("epoch,round,id,generation,owner,driven,window,firstTick,completedWindowTicks,successfulCommands,horizontalMovingTicks,horizontalDisplacingTicks,horizontalPathMeters,maxHorizontalExcursionMeters,pressureObservedTicks,oneCentimeterExcursion");
            for (int windowIndex = 0; windowIndex < EvidenceWindowCount; windowIndex++)
            {
                int completed = Math.Min(EvidenceWindowTicks, Math.Max(0, measuredTicks - windowIndex * EvidenceWindowTicks));
                if (completed == 0) break;
                int offset = windowIndex * (fixture.expectedBodies + 1);
                for (int identity = 1; identity <= fixture.expectedBodies; identity++)
                {
                    var body = fixture.bodyWindows[offset + identity];
                    writer.WriteLine(string.Join(",", 1, 1, identity, 1, 1 + identity % 2, identity > fixture.expectedResting ? 1 : 0,
                        windowIndex, windowIndex * EvidenceWindowTicks, completed, body.successfulCommands, body.horizontalMovingTicks,
                        body.horizontalDisplacingTicks, F(body.horizontalPath), F(body.maxHorizontalExcursion), body.pressureObservedTicks,
                        body.maxHorizontalExcursion >= SubstantiveExcursion ? 1 : 0));
                }
            }
        }

        static SourceIdentity[] CaptureSources()
        {
            var paths = new List<string>(Directory.GetFiles("Assets/Splatoon/Runtime/Blocks/Simulation", "*.cs", SearchOption.AllDirectories));
            paths.Add("Assets/Splatoon/Editor/BlockSimulationPrototypeProbe.cs");
            paths.Add("Assets/Splatoon/Tests/BlockSimulationPrototypeTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockSimulationCcdTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockSimulationSlopeTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockCapsuleQueryTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockCapsuleOracleTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockMapCollisionTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockGeometryArchiveTests.cs");
            paths.Add("Assets/Splatoon/Tests/BlockBroadphaseIndexTests.cs");
            paths.Add("Assets/Splatoon/Tests/SupportDependencyTests.cs");
            paths.Add("ProjectSettings/ProjectVersion.txt");
            foreach (var path in Directory.GetFiles("ProjectSettings", "*Burst*", SearchOption.TopDirectoryOnly)) paths.Add(path);
            paths.Add("Reports/BlockSimulation20261018/Tools/WatchPrototypeProbe.ps1");
            paths.Add("Reports/BlockSimulation20261018/PrototypeWorkloadEvidenceContract.md");
            paths.Add("Assets/Splatoon/Splatoon.asmdef"); paths.Add("Assets/Splatoon/Editor/Splatoon.Editor.asmdef");
            paths.Add("Assets/Splatoon/Tests/Splatoon.Tests.asmdef"); paths.Add("Packages/manifest.json"); paths.Add("Packages/packages-lock.json");
            paths.Sort(StringComparer.Ordinal);
            var result = new SourceIdentity[paths.Count];
            using var sha = SHA256.Create();
            for (int i = 0; i < paths.Count; i++)
            {
                byte[] bytes = File.ReadAllBytes(paths[i]); byte[] hash = sha.ComputeHash(bytes);
                result[i] = new SourceIdentity { path = paths[i].Replace('\\', '/'), bytes = bytes.LongLength, sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() };
            }
            return result;
        }

        static bool SameSources(SourceIdentity[] before, SourceIdentity[] after)
        {
            if (before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
                if (before[i].path != after[i].path || before[i].bytes != after[i].bytes || before[i].sha256 != after[i].sha256) return false;
            return true;
        }

        static double Percentile(double[] sorted, double fraction) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Utc() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        static double SecondsSince(long start) => (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
    }
}
#endif
