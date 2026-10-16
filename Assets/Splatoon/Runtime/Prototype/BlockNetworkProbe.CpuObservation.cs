#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockNetworkProbe
    {
        const string NetworkFinalSnapshotFlag = "-blockNetworkFinalSnapshot";
        bool _networkFinalSnapshotRequested, _networkFinalSnapshotAttempted;
        [Serializable] sealed class NetworkFinalSnapshotReport
        {
            public int schema = 1, unityFrame, command, connectionGeneration, matchObjectId, terrainObjectId, snapshotBytes;
            public string scope = "Explicit Development host failed-drain terminal capture only; no Profiler started by this feature. Full published BLK3 plus bounded read-only private scalars; not a restart checkpoint or formal acceptance.";
            public string session, label, stage, nextStage, error, snapshotPath, snapshotSha256;
            public bool existingCpuFlagPresent, snapshotCaptured, snapshotWritten, privateCaptured, samePublishedWorldAfterCapture, complete;
            public uint round, revision;
            public ulong mutation, nextRunId, nextCohortId;
            public int pink, blue, fallingRuns, cohorts;
            public double terrainTime;
            public long beginStamp, captureEndStamp, stopwatchFrequency = Stopwatch.Frequency;
            public BlockTerrain.FinalGravityState gravity;
        }
        void CaptureNetworkFinalSnapshot(PrototypeMatch match, string nextStage)
        {
            if (!_networkFinalSnapshotRequested || _networkFinalSnapshotAttempted || _stage != "drain"
                || (nextStage != "quit-clients" && nextStage != "quit") || match == null || !match.IsSpawned || !match.IsServer) return;
            _networkFinalSnapshotAttempted = true;
            var report = new NetworkFinalSnapshotReport { session = _report.session, label = _report.label, stage = _stage, nextStage = nextStage,
                command = _command, connectionGeneration = _report.connectionGenerations, unityFrame = Time.frameCount,
                matchObjectId = match.GetInstanceID(), existingCpuFlagPresent = _networkCpuRequested, beginStamp = Stopwatch.GetTimestamp() };
            byte[] bytes = null;
            try
            {
                var terrain = match.Blocks;
                if (terrain == null || !terrain.Initialized || terrain.World == null) throw new InvalidOperationException("No initialized published terrain at failed-drain exit.");
                var published = terrain.World;
                report.terrainObjectId = terrain.GetInstanceID(); report.round = published.Round; report.revision = published.Revision;
                report.mutation = published.MutationGeneration; report.nextRunId = published.NextRunId; report.nextCohortId = published.NextCohortId;
                report.terrainTime = terrain.SimulationTime; report.pink = published.PinkCount; report.blue = published.BlueCount;
                report.fallingRuns = published.Falling.Count; report.cohorts = published.Cohorts.Count;
                // Synchronous main-thread boundary: raw dictionary/scalar reads only; no motion evaluation, cloning or physics queries.
                try { report.gravity = terrain.InspectFinalGravityState(published); report.privateCaptured = true; }
                catch (Exception error) { report.error = "Private terminal capture: " + error; }
                bytes = published.EncodeSnapshot(); report.snapshotBytes = bytes.Length; report.snapshotCaptured = true;
                report.samePublishedWorldAfterCapture = ReferenceEquals(published, terrain.World) && published.Round == report.round
                    && published.Revision == report.revision && published.MutationGeneration == report.mutation && terrain.SimulationTime == report.terrainTime;
            }
            catch (Exception error) { report.error = (report.error == null ? "" : report.error + "\n") + "Published terminal capture: " + error; }
            finally { report.captureEndStamp = Stopwatch.GetTimestamp(); }
            try
            {
                if (bytes != null)
                {
                    string path = _output + ".final.snapshot.bin";
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        report.snapshotSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.Write(bytes, 0, bytes.Length);
                    report.snapshotPath = path; report.snapshotWritten = true;
                }
            }
            catch (Exception error) { report.error = (report.error == null ? "" : report.error + "\n") + "Terminal binary write: " + error; }
            report.complete = report.error == null && report.snapshotWritten && report.privateCaptured && report.samePublishedWorldAfterCapture;
            try
            {
                using (var stream = new FileStream(_output + ".final-snapshot.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(report, true));
            }
            catch { /* An absent/unwritable diagnostic receipt is not success; never alter the formal error stream or control flow. */ }
        }

        const string NetworkCpuFlag = "-blockNetworkCpuProfile";
        const int NetworkCpuRowCap = 2048, NetworkCpuPendingCap = 16384, NetworkCpuMotionCap = 64, NetworkCpuBindingCap = 512;
        [Serializable] sealed class NetworkCpuBinding
        {
            public int ordinal, unityFrame, sampleOrdinal, matchObjectId, terrainObjectId, probeConnectionGeneration;
            public long stopwatchStamp;
            public string role;
            public bool terrainAvailable;
        }
        [Serializable] sealed class NetworkCpuRun
        {
            public ulong id, cohort;
            public Vector3Int bottom;
            public int cells, initialOffsetBits, initialSpeedBits, landingOffsetBits, offsetBits, speedBits;
            public long startedAtBits;
            public bool landingKnown;
            public byte[] owners;
        }
        [Serializable] sealed class NetworkCpuCohort
        {
            public ulong id, membershipRevision, lowerSupport;
            public Vector3Int bottom;
            public int topExclusive, detachCursor, members, initialOffsetBits, initialSpeedBits, landingOffsetBits;
            public int supportClearanceBits, offsetBits, speedBits, retainedSupportSegments, rebaseSegments;
            public long startedAtBits;
            public string phase;
            public bool landingKnown, needsRegroup, needsLandingProof, rebasePending;
        }
        [Serializable] sealed class NetworkCpuRow
        {
            public string stage, boundary, nextStage, role;
            public int bindingOrdinal, matchObjectId, terrainObjectId, probeConnectionGeneration;
            public bool terrainAvailable;
            public int unityFrame, sampleOrdinal, command, pending, unstable, fallingRuns, fallingCells;
            public uint round, revision;
            public long stopwatchStamp, realtimeDoubleBits, queued, completed, authorityStages, commandChecks, gravityChecks;
            public double terrainTime, lastCommandStageMs, lastGravityStageMs, synchronousMs;
            public BlockTerrain.CommandHeadDiagnostic commandHead;
            public BlockTerrain.BuildWorkDiagnostics buildWork;
            public BlockTerrain.PendingWorkDiagnostics pendingWork;
            public BlockTerrain.PendingHeadDiagnostic pendingHead;
            public BlockTerrain.PublicationRetryDiagnostics retry;
            public long dependentStarts, dependentStages, dependentWork, dependentRetries, dependentAborts;
            public ulong dependentProgress;
            public NetworkCpuRun[] publishedRuns;
            public NetworkCpuCohort[] publishedCohorts;
            public int publishedCohortTotal;
            public bool publishedRunsTruncated, publishedCohortsTruncated;
            public double observerElapsedMs;
        }
        [Serializable] sealed class NetworkCpuPending
        {
            public long id, createdStamp, observedStamp;
            public string stage, outcome, abortReason;
            public int unityFrame, sampleOrdinal, pieces;
            public bool remote, generationObserved, uploadComplete, bakeObserved, publishEntered;
            public uint observedRound, observedRevision;
            public int bindingOrdinal, matchObjectId, terrainObjectId;
            public string role;
            public double observedTerrainTime;
            public bool pipelineAvailable, uploadedBeforeGroupCompletion;
            public double firstUploadMs, activeUploadElapsedMs, activeBakeElapsedMs, uploadOverlapMs, bakeOverlapMs;
            public double generationMs, uploadTailMs, bakeTailMs, readyWaitMs, publicationMs, totalMs;
            public bool workerAvailable;
            public double initialDispatchMs, semaphoreMs, innerDispatchMs, renderBuildMs, collisionBuildMs, bodyOtherMs, joinTailMs, completionToMainMs;
        }
        [Serializable] sealed class NetworkCpuReport
        {
            public int schema = 1, samples, droppedRows, droppedPending, droppedBindings, observerErrors;
            public long stopwatchFrequency = Stopwatch.Frequency;
            public string scope = "Explicit selected Development network Player CPU diagnosis; default-off. Ordered same-process metadata and observer stamps require native chunk readback. No formal gate/input/hash changes. Wall spans include scheduling and may overlap; local completion does not prove raw completeness.";
            public string reason, error, snapshotPath, snapshotSha256, snapshotError, snapshotStage, snapshotNextStage;
            public bool startAttempted, closed, observationComplete, snapshotAttempted, snapshotCaptured, snapshotWritten;
            public string snapshotScope = "Published BlockWorld wire only; no private work queues, scheduling checkpoint, CharacterControllers or PhysX state.";
            public uint snapshotRound, snapshotRevision;
            public double snapshotTerrainTime, snapshotSerializationMs;
            public int snapshotUnityFrame, snapshotSampleOrdinal, snapshotRowIndex, snapshotBindingOrdinal, snapshotMatchObjectId, snapshotTerrainObjectId;
            public long snapshotBeginStamp, snapshotEndStamp;
            public bool rawActiveAtSnapshot;
            public int snapshotBytes, snapshotFallingRuns, snapshotFallingCells;
            public List<NetworkCpuBinding> bindings = new();
            public List<NetworkCpuRow> rows = new();
            public List<NetworkCpuPending> pending = new();
        }
        bool _networkCpuRequested, _networkCpuClosed, _networkCpuWritten, _networkCpuWriting;
        int _networkCpuSamples, _networkCpuLastFrame = int.MinValue, _networkCpuBindingOrdinal;
        double _networkCpuNextRow;
        string _networkCpuStopReason;
        BlockBenchmarkCpuWindowCapture _networkCpu;
        NetworkCpuReport _networkCpuReport;
        BlockTerrain _networkCpuTerrain;
        PrototypeMatch _networkCpuMatch;
        string _networkCpuRole = "unavailable";
        byte[] _networkCpuSnapshot;

        void ConfigureNetworkCpuObservation(string[] existingArguments)
        {
            // Reuses Awake's original argument read. No observer/native state/allocation on the false path.
            // Only the explicitly flagged process opts in; no flag is propagated to other participants.
            _networkCpuRequested = Array.IndexOf(existingArguments, NetworkCpuFlag) >= 0;
            _networkFinalSnapshotRequested = Array.IndexOf(existingArguments, NetworkFinalSnapshotFlag) >= 0;
        }
        void Update()
        {
            if (!_networkCpuRequested) { UpdateProbe(); return; }
            try
            {
                // Keep the frame bridge during transient disconnects/unspawned matches after capture starts.
                if (_networkCpuReport != null && !_networkCpuClosed) BindNetworkCpuTerrain(PrototypeMatch.Current);
                SampleNetworkCpu(Time.realtimeSinceStartupAsDouble);
                UpdateProbe();
            }
            catch { CloseNetworkCpuObservation("probe_update_exception"); throw; }
            finally
            {
                if (_networkCpuStopReason != null) CloseNetworkCpuObservation(_networkCpuStopReason);
            }
        }
        void ObserveNetworkCpu(PrototypeMatch match, double now)
        {
            if (!_networkCpuRequested || _networkCpuClosed || !match.InitialSyncComplete) return;
            try
            {
                if (_networkCpu == null)
                {
                    _networkCpuReport = new NetworkCpuReport();
                    var arguments = Environment.GetCommandLineArgs();
                    foreach (string flag in new[] { "-blockBenchmarkCpuProfile", "-profiler-enable", "-profiler-log-file",
                        "-profiler-capture-frame-count", "-profiler-maxusedmemory", "-deepprofiling" })
                        foreach (string argument in arguments)
                            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Do not combine network CPU observation with " + flag);
                    // Fresh independent outputs only. Never redirect an existing runtime Profiler.
                    foreach (string path in new[] { _output + ".cpu-observation.json", _output + ".cpu-final.snapshot.bin" })
                        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Preserve existing CPU diagnostic output: " + path);
                    _networkCpu = BlockBenchmarkCpuWindowCapture.CreateIfRequested(_output, NetworkCpuFlag);
                    if (_networkCpu == null) throw new InvalidOperationException("Explicit network CPU helper was not created.");
                    BindNetworkCpuTerrain(match);
                    _networkCpu.StartWindow(match.IsServer ? "host" : "client", Time.frameCount, now, Stopwatch.GetTimestamp());
                    _networkCpuReport.startAttempted = true;
                    if (!_networkCpu.Active) { _networkCpuStopReason = "capture_start_failed"; return; }
                }
                SampleNetworkCpu(now);
            }
            catch (Exception error) { NetworkCpuObserverError(error); _networkCpuStopReason = "observer_exception"; }
        }
        void BindNetworkCpuTerrain(PrototypeMatch current)
        {
            try { BindNetworkCpuTerrainCore(current); }
            catch (Exception error) { NetworkCpuObserverError(error); _networkCpuStopReason = "terrain_binding_exception"; }
        }
        void BindNetworkCpuTerrainCore(PrototypeMatch current)
        {
            bool live = current != null && current.IsSpawned && current.Blocks != null && current.Blocks.Initialized;
            var match = live ? current : null; var terrain = live ? current.Blocks : null;
            string role = live ? current.IsServer ? "host" : "client" : "unavailable";
            if (ReferenceEquals(match, _networkCpuMatch) && ReferenceEquals(terrain, _networkCpuTerrain) && role == _networkCpuRole) return;
            if (_networkCpuTerrain != null) _networkCpuTerrain.PendingTransactionFinished -= OnNetworkCpuPending;
            _networkCpuMatch = match; _networkCpuTerrain = terrain;
            _networkCpuRole = role;
            if (terrain != null) terrain.PendingTransactionFinished += OnNetworkCpuPending;
            var report = _networkCpuReport; ++_networkCpuBindingOrdinal;
            if (report.bindings.Count == NetworkCpuBindingCap) { report.droppedBindings++; return; }
            report.bindings.Add(new NetworkCpuBinding { ordinal = _networkCpuBindingOrdinal,
                unityFrame = Time.frameCount, sampleOrdinal = _networkCpuSamples, stopwatchStamp = Stopwatch.GetTimestamp(),
                matchObjectId = match != null ? match.GetInstanceID() : 0, terrainObjectId = terrain != null ? terrain.GetInstanceID() : 0,
                role = _networkCpuRole, terrainAvailable = terrain != null, probeConnectionGeneration = _report.connectionGenerations });
        }
        void SampleNetworkCpu(double now)
        {
            if (_networkCpu == null || _networkCpuClosed || !_networkCpu.Active || _networkCpuLastFrame == Time.frameCount) return;
            _networkCpuLastFrame = Time.frameCount;
            try
            {
                _networkCpu.ObserveSample(++_networkCpuSamples);
                if (!_networkCpu.Active) { _networkCpuStopReason ??= "capture_stopped_during_sample"; return; }
                if (now >= _networkCpuNextRow)
                {
                    _networkCpuNextRow = now + 1;
                    RecordNetworkCpuRow("one_second", null);
                }
            }
            catch (Exception error) { NetworkCpuObserverError(error); _networkCpuStopReason = "observer_exception"; }
        }
        void AbortNetworkCpuOnLog()
        {
            if (!_networkCpuRequested || _networkCpuClosed || _networkCpu == null) return;
            // Original helper defers a setter-reentrant error until configuration unwinds.
            _networkCpuStopReason = "probe_error_log";
            _networkCpu.AbortOnErrorLog(_networkCpuSamples);
        }
        void BeforeNetworkCpuStageChange(PrototypeMatch match, string nextStage)
        {
            CaptureNetworkFinalSnapshot(match, nextStage);
            if (_networkCpuReport == null || _networkCpuClosed) return;
            try
            {
                RecordNetworkCpuRow("stage_change_before_control", nextStage);
                // The harness exits an unsuccessful drain through quit-clients, then quit.
                // Preserve the actual last drain world before gravity continues during cleanup.
                if (_stage == "drain" && (nextStage == "quit-clients" || nextStage == "quit"))
                    CaptureNetworkCpuSnapshot(nextStage);
            }
            catch (Exception error) { NetworkCpuObserverError(error); }
        }
        void RecordNetworkCpuRow(string boundary, string nextStage)
        {
            var report = _networkCpuReport; var terrain = _networkCpuTerrain;
            if (report == null) return;
            if (report.rows.Count == NetworkCpuRowCap) { report.droppedRows++; return; }
            if (terrain == null || !terrain.Initialized)
            {
                report.rows.Add(new NetworkCpuRow { stage = _stage, boundary = boundary, nextStage = nextStage,
                    unityFrame = Time.frameCount, sampleOrdinal = _networkCpuSamples, command = _command,
                    stopwatchStamp = Stopwatch.GetTimestamp(), realtimeDoubleBits = BitConverter.DoubleToInt64Bits(Time.realtimeSinceStartupAsDouble),
                    role = _networkCpuRole, bindingOrdinal = _networkCpuBindingOrdinal, probeConnectionGeneration = _report.connectionGenerations });
                return;
            }
            long begin = Stopwatch.GetTimestamp();
            var row = new NetworkCpuRow { stage = _stage, boundary = boundary, nextStage = nextStage,
                role = _networkCpuRole, terrainAvailable = true, bindingOrdinal = _networkCpuBindingOrdinal,
                matchObjectId = _networkCpuMatch != null ? _networkCpuMatch.GetInstanceID() : 0,
                terrainObjectId = terrain.GetInstanceID(), probeConnectionGeneration = _report.connectionGenerations,
                unityFrame = Time.frameCount, sampleOrdinal = _networkCpuSamples, command = _command,
                stopwatchStamp = Stopwatch.GetTimestamp(), realtimeDoubleBits = BitConverter.DoubleToInt64Bits(Time.realtimeSinceStartupAsDouble),
                round = terrain.Round, revision = terrain.Revision, terrainTime = terrain.SimulationTime,
                pending = terrain.PendingTransactions, unstable = terrain.UnstableColumns,
                fallingRuns = terrain.World.Falling.Count, fallingCells = terrain.FallingCount,
                queued = terrain.CommandsQueued, completed = terrain.CommandsCompleted,
                authorityStages = terrain.AuthorityStageCount, commandChecks = terrain.CommandWorkChecks, gravityChecks = terrain.GravityWorkChecks,
                lastCommandStageMs = terrain.LastCommandStageMilliseconds, lastGravityStageMs = terrain.LastGravityStageMilliseconds,
                synchronousMs = terrain.TerrainSynchronousMilliseconds, commandHead = terrain.InspectCommandHead(),
                buildWork = terrain.InspectBuildWork(), pendingWork = terrain.InspectPendingWork(),
                pendingHead = terrain.InspectPendingTransaction(), retry = terrain.InspectPublicationRetry(),
                dependentStarts = terrain.DependentEditPreparationStarts, dependentStages = terrain.DependentEditPreparationStages,
                dependentWork = terrain.DependentEditPreparationWork, dependentRetries = terrain.DependentEditPreparationRetries,
                dependentAborts = terrain.DependentEditPreparationAborts, dependentProgress = terrain.DependentEditPreparationProgressVersion };
            ReadNetworkCpuPublishedMotion(terrain, row);
            row.observerElapsedMs = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;
            report.rows.Add(row);
        }
        static void ReadNetworkCpuPublishedMotion(BlockTerrain terrain, NetworkCpuRow row)
        {
            var world = terrain.World; double now = terrain.SimulationTime;
            row.publishedRunsTruncated = world.Falling.Count > NetworkCpuMotionCap;
            row.publishedRuns = new NetworkCpuRun[Math.Min(NetworkCpuMotionCap, world.Falling.Count)];
            int at = 0;
            foreach (var run in world.Falling.Values)
            {
                if (at == row.publishedRuns.Length) break;
                var owners = new byte[run.Cells.Length];
                for (int i = 0; i < owners.Length; i++) owners[i] = run.Cells[i].Owner;
                row.publishedRuns[at++] = new NetworkCpuRun { id = run.Id, cohort = run.Cohort?.Id ?? 0,
                    bottom = run.Bottom, cells = run.Cells.Length, owners = owners, landingKnown = run.LandingKnown,
                    startedAtBits = BitConverter.DoubleToInt64Bits(run.StartedAt),
                    initialOffsetBits = BitConverter.SingleToInt32Bits(run.InitialOffset), initialSpeedBits = BitConverter.SingleToInt32Bits(run.InitialSpeed),
                    landingOffsetBits = BitConverter.SingleToInt32Bits(run.LandingOffset),
                    offsetBits = BitConverter.SingleToInt32Bits(run.Offset(now)), speedBits = BitConverter.SingleToInt32Bits(run.Speed(now)) };
            }
            row.publishedCohortTotal = world.Cohorts.Count;
            row.publishedCohortsTruncated = world.Cohorts.Count > NetworkCpuMotionCap;
            row.publishedCohorts = new NetworkCpuCohort[Math.Min(NetworkCpuMotionCap, world.Cohorts.Count)]; at = 0;
            foreach (var group in world.Cohorts.Values)
            {
                if (at == row.publishedCohorts.Length) break;
                row.publishedCohorts[at++] = new NetworkCpuCohort { id = group.Id, membershipRevision = group.MembershipRevision,
                    lowerSupport = group.LowerSupport?.Id ?? 0, bottom = group.Bottom, topExclusive = group.TopExclusive,
                    detachCursor = group.DetachCursor, members = group.MemberIds.Count, phase = group.Phase.ToString(),
                    landingKnown = group.LandingKnown, needsRegroup = group.NeedsRegroup, needsLandingProof = group.NeedsLandingProof,
                    rebasePending = group.RebasePending, startedAtBits = BitConverter.DoubleToInt64Bits(group.StartedAt),
                    initialOffsetBits = BitConverter.SingleToInt32Bits(group.InitialOffset), initialSpeedBits = BitConverter.SingleToInt32Bits(group.InitialSpeed),
                    landingOffsetBits = BitConverter.SingleToInt32Bits(group.LandingOffset), supportClearanceBits = BitConverter.SingleToInt32Bits(group.SupportClearance),
                    offsetBits = BitConverter.SingleToInt32Bits(group.Offset(now)), speedBits = BitConverter.SingleToInt32Bits(group.Speed(now)),
                    retainedSupportSegments = group.RetainedSupportMotion.Length, rebaseSegments = group.RebaseMotion.Length };
            }
        }
        void OnNetworkCpuPending(BlockTerrain.PendingWallDiagnostic value)
        {
            try
            {
                var report = _networkCpuReport;
                if (report == null || _networkCpuClosed) return;
                if (report.pending.Count == NetworkCpuPendingCap) { report.droppedPending++; return; }
                var worker = value.Worker; var pipeline = value.Pipeline; var terrain = _networkCpuTerrain;
                report.pending.Add(new NetworkCpuPending { id = value.Id, createdStamp = value.CreatedWallTimestamp,
                    observedStamp = Stopwatch.GetTimestamp(), stage = _stage, unityFrame = Time.frameCount,
                    sampleOrdinal = _networkCpuSamples, pieces = value.Pieces, remote = value.Remote,
                    role = _networkCpuRole, bindingOrdinal = _networkCpuBindingOrdinal,
                    matchObjectId = _networkCpuMatch != null ? _networkCpuMatch.GetInstanceID() : 0,
                    terrainObjectId = terrain != null ? terrain.GetInstanceID() : 0,
                    observedRound = terrain != null ? terrain.Round : 0, observedRevision = terrain != null ? terrain.Revision : 0,
                    observedTerrainTime = terrain != null ? terrain.SimulationTime : 0,
                    outcome = value.Outcome.ToString(), abortReason = value.AbortReason.ToString(),
                    pipelineAvailable = pipeline.Available, uploadedBeforeGroupCompletion = pipeline.UploadedBeforeGroupCompletion,
                    firstUploadMs = pipeline.FirstUploadFromCreatedMs, activeUploadElapsedMs = pipeline.UploadPieceActiveElapsedMs,
                    activeBakeElapsedMs = pipeline.BakeActiveElapsedMs, uploadOverlapMs = pipeline.UploadGenerationOverlapWindowMs,
                    bakeOverlapMs = pipeline.BakeGenerationOverlapWindowMs,
                    generationObserved = value.GenerationObserved, uploadComplete = value.UploadCompleted, bakeObserved = value.BakeObserved, publishEntered = value.PublishEntered,
                    generationMs = value.GenerationObservedMs, uploadTailMs = value.UploadMs, bakeTailMs = value.BakeObservedTailMs,
                    readyWaitMs = value.ReadyWaitMs, publicationMs = value.PublicationMs, totalMs = value.TotalMs,
                    workerAvailable = worker.Available, initialDispatchMs = worker.InitialToCriticalAwaitMs, semaphoreMs = worker.CriticalSemaphoreAwaitMs,
                    innerDispatchMs = worker.CriticalInnerDispatchMs, renderBuildMs = worker.CriticalRenderBuildMs, collisionBuildMs = worker.CriticalCollisionBuildMs,
                    bodyOtherMs = worker.CriticalBodyOtherMs, joinTailMs = worker.GroupJoinTailMs, completionToMainMs = worker.CompletionToMainObservedMs });
            }
            catch (Exception error) { NetworkCpuObserverError(error); }
        }
        void CaptureNetworkCpuSnapshot(string nextStage)
        {
            var report = _networkCpuReport; var terrain = _networkCpuTerrain;
            if (report == null || report.snapshotAttempted) return;
            report.snapshotAttempted = true;
            // Published wire state only: not a serialization of private queues, CharacterControllers or PhysX.
            report.snapshotBeginStamp = Stopwatch.GetTimestamp();
            report.snapshotBindingOrdinal = _networkCpuBindingOrdinal;
            report.snapshotMatchObjectId = _networkCpuMatch != null ? _networkCpuMatch.GetInstanceID() : 0;
            report.snapshotTerrainObjectId = terrain != null ? terrain.GetInstanceID() : 0;
            report.snapshotUnityFrame = Time.frameCount; report.snapshotSampleOrdinal = _networkCpuSamples;
            report.snapshotRowIndex = report.rows.Count - 1; report.rawActiveAtSnapshot = _networkCpu?.Active ?? false;
            report.snapshotStage = _stage; report.snapshotNextStage = nextStage;
            try
            {
                if (terrain == null || !terrain.Initialized) throw new InvalidOperationException("No initialized published terrain at the requested snapshot boundary.");
                report.snapshotRound = terrain.Round; report.snapshotRevision = terrain.Revision;
                report.snapshotTerrainTime = terrain.SimulationTime;
                report.snapshotFallingRuns = terrain.World.Falling.Count; report.snapshotFallingCells = terrain.FallingCount;
                _networkCpuSnapshot = terrain.ExportSnapshot(); report.snapshotBytes = _networkCpuSnapshot.Length;
                report.snapshotCaptured = true;
            }
            catch (Exception error) { report.snapshotError = error.ToString(); NetworkCpuObserverError(error); }
            finally
            {
                report.snapshotEndStamp = Stopwatch.GetTimestamp();
                report.snapshotSerializationMs = (report.snapshotEndStamp - report.snapshotBeginStamp) * 1000.0 / Stopwatch.Frequency;
            }
        }
        void NetworkCpuObserverError(Exception error)
        {
            if (_networkCpuReport == null) return;
            _networkCpuReport.observerErrors++; _networkCpuReport.error ??= error.ToString();
            // No logging from an error observer: avoid recursion and changing the formal error report.
        }
        void CloseNetworkCpuObservation(string reason)
        {
            if (_networkCpuReport == null) return;
            if (!_networkCpuClosed)
            {
                _networkCpuClosed = true;
                try { _networkCpu?.Close(reason, _networkCpuSamples); }
                catch (Exception error) { NetworkCpuObserverError(error); }
                finally
                {
                    if (_networkCpuTerrain != null) _networkCpuTerrain.PendingTransactionFinished -= OnNetworkCpuPending;
                }
                _networkCpuReport.closed = true; _networkCpuReport.reason = reason;
                _networkCpuReport.samples = _networkCpuSamples;
                try { RecordNetworkCpuRow("closed_after_profiler_restore", null); CaptureNetworkCpuSnapshot(null); }
                catch (Exception error) { NetworkCpuObserverError(error); }
                _networkCpuReport.observationComplete = _networkCpuStopReason == null && _networkCpuReport.observerErrors == 0
                    && _networkCpuReport.startAttempted && _networkCpuSamples > 0 && _networkCpuReport.droppedRows == 0
                    && _networkCpuReport.droppedPending == 0 && _networkCpuReport.droppedBindings == 0
                    && _networkCpuReport.snapshotCaptured;
            }
            WriteNetworkCpuObservation();
        }
        void WriteNetworkCpuObservation()
        {
            if (!_networkCpuClosed || _networkCpuWritten || _networkCpuWriting) return;
            _networkCpuWriting = true;
            try
            {
                _networkCpu?.Write();
                var report = _networkCpuReport;
                if (_networkCpuSnapshot != null && !report.snapshotWritten && report.snapshotError == null)
                {
                    try
                    {
                        string path = _output + ".cpu-final.snapshot.bin";
                        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.Write(_networkCpuSnapshot, 0, _networkCpuSnapshot.Length);
                        using (var hash = System.Security.Cryptography.SHA256.Create())
                            report.snapshotSha256 = BitConverter.ToString(hash.ComputeHash(_networkCpuSnapshot)).Replace("-", "").ToLowerInvariant();
                        report.snapshotPath = path; report.snapshotWritten = true;
                    }
                    catch (Exception error) { report.snapshotError = error.ToString(); NetworkCpuObserverError(error); report.observationComplete = false; }
                }
                using (var stream = new FileStream(_output + ".cpu-observation.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(report, true));
                _networkCpuWritten = true;
            }
            catch (Exception error) { NetworkCpuObserverError(error); _networkCpuWritten = File.Exists(_output + ".cpu-observation.json"); }
            finally { _networkCpuWriting = false; }
        }
        void OnApplicationQuit() => CloseNetworkCpuObservation("application_quit");
    }
}
#endif
