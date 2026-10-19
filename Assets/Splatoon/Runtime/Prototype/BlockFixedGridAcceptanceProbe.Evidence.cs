#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockFixedGridAcceptanceProbe
    {
        // More than a complete 180-second run at the configured 120 Hz render cap.
        // Growth is retained and counted; no frame/event or collision is truncated.
        const int FrameRecordCapacity = 32768;
        const int RecorderCapacity = 65536;

        [Flags] public enum FrameRejection
        {
            None = 0, MissingMatch = 1, MatchNotSpawned = 2, MissingLocalPlayer = 4,
            NotPlaying = 8, InitialSyncIncomplete = 16, FixedSyncIncomplete = 32,
            MissingFixedWorld = 64, Resolution = 128, MainCamera = 256,
            MainCameraNotRendered = 512, InvalidInterval = 1024,
            NonconsecutiveFrame = 2048, NullGraphics = 4096, InvalidFixedQuery = 8192
        }
        [Flags] public enum EventRejection
        {
            None = 0, NotCombat = 1, MissingMatch = 2, NotAuthority = 4,
            NotPlaying = 8, SourceRoundMismatch = 16, InvalidElapsed = 32,
            EvidenceRoundMismatch = 64, Finished = 128
        }
        [Serializable] public sealed class FrameEvidence
        {
            public int schema = 4, cpuMarkerCount, frameRecordInitialCapacity = FrameRecordCapacity,
                recorderCapacity = RecorderCapacity;
            public long stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;
            public int observedCombatFrameCallbacks, combatFrames, playingFrames, eligibleFrames, rejectedPlayingFrames, rejectedCombatFrames,
                unknownPhaseFrames, missingFrameIds, duplicateFrameIds, recorderWrappedFrames,
                frameBufferGrowths, cpuBufferGrowths, paidBufferGrowths, actualBufferGrowths;
            public long observedPaidCallbacks, observedActualCallbacks, lastEventSequence;
            public int firstFrameId = -1, lastFrameId = -1;
            public double firstFrameRealtime = -1, lastFrameRealtime, firstEligibleElapsed = -1, lastEligibleElapsed;
            public bool observationComplete, eventObservationComplete, authorityEventBuffersReserved;
            public string frameClock = "frameId identifies the completed application frame. LateUpdate snapshots its metadata; endCameraRendering confirms its main camera; observedAtFrameId is the next Update. frameMilliseconds=(observedFrameStartUnscaled-frameStartUnscaled)*1000. Stopwatch timestamps are monotonic and use stopwatchFrequency.";
            public string recorderClock = "ProfilerRecorder completed-frame samples are read at the next Update. All recorders are polled every application frame, including outside combat. cpuOffset/cpuCount index parallel cpuRawValues/cpuRecorderStates/cpuSampleCounts arrays in cpu[] marker order. CPU raw values are nanoseconds; gc.raw is bytes. States: valid=1, running=2, fresh=4, wrapped=8. A valid running uncalled sparse marker has raw=0, calls=0, fresh=false. Built-in mainThread/renderThread/gc require fresh=true for available; physics/custom sparse markers accept a valid running uncalled zero. Summary metrics contain available Playing samples only. Unavailable is not zero cost. Wrapped recorders are explicitly untrusted and make observationComplete=false.";
            public string frameRejectionBits = "1 MissingMatch;2 MatchNotSpawned;4 MissingLocalPlayer;8 NotPlaying;16 InitialSyncIncomplete;32 FixedSyncIncomplete;64 MissingFixedWorld;128 Resolution;256 MainCamera;512 MainCameraNotRendered;1024 InvalidInterval;2048 NonconsecutiveFrame;4096 NullGraphics;8192 InvalidFixedQuery";
            public string eventRejectionBits = "1 NotCombat;2 MissingMatch;4 NotAuthority;8 NotPlaying;16 SourceRoundMismatch;32 InvalidElapsed;64 EvidenceRoundMismatch;128 Finished";
            public string phaseLegend = "-1 unavailable;0 Practice;1 Playing;2 Finished";
            public string sourceKindLegend = "0 Main;1 Sub;2 Special";
            public string operationLegend = "0 System;1 Build;2 Direct;3 Blast";
            public string selection = "Every application frame whose LateUpdate stage is combat is retained, including missing match/sync/local/camera and non-Playing frames. eligible is exactly rejection==0. Frame/CPU metrics and completedFixedStepsPerFrame include every Playing row; rejected Playing rows still prevent frameGatePassed. No wait, camera or dependency failure is silently removed from timing.";
            public string fixedQueryContract = "dependency is a detached value snapshot of actual Authority/Presented/retained Gate and the last completed query interval. advertisedRevision is only the Match broadcast watermark. queryStepsDelta and allQueryStepsDelta count actual query intervals, not simulation ticks; each CanStand and motor interval is separate. Zero means no query and makes no version-use claim. Pending/no-query frames remain in timing; invalidQueryStepsDelta or allInvalidQueryStepsDelta above zero fails. Deltas span consecutive LateUpdate samples, including all query intervals in multi-step frames. The last interval records source and begin/end committed views; aggregate counters retain an earlier invalid interval in the same frame.";
            public string completeness = "observationComplete checks structural retention and eligibility of observed combat segments only; it does not assert a 180-second run, eight players, full workload, network matrix, latency, or performance pass. eventObservationComplete checks callback/raw counts and last sequence; independently verify every shared sequence and original source join, then verify full duration/workload against the requested lane.";
            public string authoritySteps = "authorityFixedSteps counts distinct completed production authority physics ticks observed during this application frame; client zero is expected. It is not the count of Physics.Simulate calls or client prediction steps.";
            public string eventScope = "All Paid/Actual observer callbacks before terminal Finish are retained across all stages, with a shared receipt sequence. Event context times/phase are the receipt view; source preserves original round/owner/life/equipment/action/kind/configRevision. One payment can have many entity/ordinal effects and no-change commits. Only eligible events contribute to 15-second workload windows. Join raw payment and actual by the complete source identity, never just a 15-second window; compare committedRound separately.";
        }
        [Serializable] public struct RecorderValue
        {
            public long raw, calls;
            public int count;
            public bool valid, running, fresh, wrapped, available;
            public byte State => (byte)((valid ? 1 : 0) | (running ? 2 : 0) | (fresh ? 4 : 0) | (wrapped ? 8 : 0));
        }
        [Serializable] public sealed class PaperSamplingEvidence
        {
            public int schema = 1, counterSchema = PaperSamplingDiagnostics.Schema;
            public bool implemented = true, enabled = true, complete;
            public string introducedIn = "fixedgrid65";
            public string scope = "Process-wide natural production traversal/live/render calls only; no forced misses. Development opt-in. Counters are not milliseconds.";
            public string clock = "Each producer records actual Time.frameCount in an 8-slot preallocated ring. LateUpdate touches zero-event buckets. Next Update reads completed FrameRecord.frameId, including that frame's render events and excluding next-frame FixedUpdate. A missing/overwritten bucket is unavailable, never zero.";
            public uint generation;
            public int availableFrames, unavailableFrames, generationChanges, regressions, invalidPartitions;
            public int firstFrameId = -1, lastFrameId = -1;
            public PaperSamplingDiagnostics.Counts baseline, latestCumulative, retainedDeltas, playingDeltas;
        }
        [Serializable] public struct FrameRecord
        {
            public int frameId, observedAtFrameId, phase, players, generation, command, width, height,
                cameraId, cameraPixelWidth, cameraPixelHeight, cameraTargetDisplay, authorityFixedSteps, rejection, cpuOffset, cpuCount, combatSegment;
            public long metadataTimestamp, observedTimestamp;
            public string stage;
            public uint epoch, round, revision, surfaceRevision, requiredRevision, requiredEpoch, requiredRound, advertisedRevision, captureTick, networkTick;
            public PrototypePlayer.FixedQueryDependencySnapshot dependency;
            public int queryPlayerInstanceId;
            public ulong queryPlayerNetworkId;
            public long queryStepsDelta, invalidQueryStepsDelta, allQuerySteps, allInvalidQuerySteps, allQueryStepsDelta, allInvalidQueryStepsDelta,
                allQueryTokenErrors, allQueryTokenErrorsDelta;
            public bool dependencyAvailable, dependencyReady, replayPending, queryCounterRegression;
            public bool matchAvailable, matchSpawned, localPlayerAvailable, initialSyncComplete, fixedSyncReady,
                fixedWorldAvailable, authority, graphics, playing, eligible, mainCameraEnabled, mainCameraActive,
                mainCameraTargetTexture, mainCameraRendered, cameraOrthographic;
            public float cameraFieldOfView;
            public double frameStartUnscaled, observedFrameStartUnscaled, metadataRealtime, observedRealtime,
                serverTime, roundStartsAt, elapsed, configuredSeconds, frameMilliseconds;
            public RecorderValue mainThread, renderThread, physics, gc;
            public PaperSamplingDiagnostics.FrameSnapshot paperSampling;
            public bool paperCounterRegression, paperPartitionInvalid;
        }
        [Serializable] public struct SourceRecord
        {
            public uint round, life, equipment, configRevision;
            public ulong owner, action;
            public int team, kind, hero, sub;
        }
        [Serializable] public struct EventContext
        {
            public long sequence, monotonicTimestamp;
            public int frameId, phase, command, generation, rejection;
            public string stage;
            public bool eligible, authority, matchAvailable;
            public uint currentRound;
            public uint currentEpoch, currentRevision, currentSurfaceRevision;
            public double receiptRealtime, elapsed, roundStartsAt;
        }
        [Serializable] public struct PaymentRecord
        {
            public SourceRecord source;
            public EventContext context;
            public double material, serverTime;
            public uint networkTick;
        }
        [Serializable] public struct ActualCountRecord
        {
            public int newFixed, newPaint, enemyPaintReplaced, enemyFixedRemoved, friendlyFixedRemoved,
                enemyPaintRemoved, friendlyPaintRemoved, converted, relocated;
        }
        [Serializable] public struct ActualRecord
        {
            public SourceRecord source;
            public EventContext context;
            public int operation;
            public uint entity, ordinal, epoch, committedRound, revision, surfaceRevision, networkTick;
            public double serverTime, milliseconds;
            public bool hadChanges, auxiliary, hadContact;
            public ActualCountRecord counts;
        }

        FrameRecord _pendingFrame;
        bool _hasPendingFrame;
        bool _previousLateUpdateCombat;
        int _combatSegment;
        long _rawEventSequence;
        long _lastQuerySteps, _lastInvalidQuerySteps, _lastAllQuerySteps, _lastAllInvalidQuerySteps, _lastAllQueryTokenErrors;
        int _lastQueryPlayer;

        void InitializeFrameEvidence()
        {
            int capacity = checked(FrameRecordCapacity * _cpu.Count);
            _report.cpuRawValues.Capacity = capacity;
            _report.cpuRecorderStates.Capacity = capacity;
            _report.cpuSampleCounts.Capacity = capacity;
            _report.frameEvidence.cpuMarkerCount = _cpu.Count;
            _report.completedFixedStepsPerFrame.Capacity = FrameRecordCapacity;
        }
        void LateUpdate()
        {
            using var marker = FrameEvidenceCpu.Auto();
            PaperSamplingDiagnostics.TouchFrame();
            int steps = _completedFixedStepsThisFrame;
            _completedFixedStepsThisFrame = 0;
            var local = PrototypePlayer.Local;
            bool dependencyAvailable = local != null && local.IsSpawned;
            var dependency = dependencyAvailable ? local.InspectFixedQueryDependency() : default;
            int queryPlayer = dependencyAvailable ? local.GetInstanceID() : 0;
            bool samePlayer = queryPlayer != 0 && queryPlayer == _lastQueryPlayer;
            long queryStepsDelta = dependency.StartedSteps - (samePlayer ? _lastQuerySteps : 0);
            long invalidQueryStepsDelta = dependency.InvalidQuerySteps - (samePlayer ? _lastInvalidQuerySteps : 0);
            long allQuerySteps = PrototypePlayer.AllFixedQuerySteps, allInvalidQuerySteps = PrototypePlayer.AllInvalidFixedQuerySteps;
            long allQueryTokenErrors = PrototypePlayer.AllFixedQueryTokenErrors;
            long allQueryStepsDelta = allQuerySteps - _lastAllQuerySteps, allInvalidQueryStepsDelta = allInvalidQuerySteps - _lastAllInvalidQuerySteps;
            long allQueryTokenErrorsDelta = allQueryTokenErrors - _lastAllQueryTokenErrors;
            bool queryCounterRegression = queryStepsDelta < 0 || invalidQueryStepsDelta < 0 || allQueryStepsDelta < 0 || allInvalidQueryStepsDelta < 0 || allQueryTokenErrorsDelta < 0;
            _lastQueryPlayer = queryPlayer; _lastQuerySteps = dependency.StartedSteps; _lastInvalidQuerySteps = dependency.InvalidQuerySteps;
            _lastAllQuerySteps = allQuerySteps; _lastAllInvalidQuerySteps = allInvalidQuerySteps;
            _lastAllQueryTokenErrors = allQueryTokenErrors;
            bool combat = !_finished && _stage == "combat";
            if (combat && !_previousLateUpdateCombat) _combatSegment++;
            _previousLateUpdateCombat = combat;
            if (!combat) return;
            _report.frameEvidence.observedCombatFrameCallbacks++;
            if (_hasPendingFrame)
            {
                // Normally Update has already drained the previous frame. Retain an
                // explicit gap if Unity did not invoke it, rather than invent a row.
                _report.frameEvidence.missingFrameIds++;
                _hasPendingFrame = false;
            }
            var match = PrototypeMatch.Current;
            bool spawned = match != null && match.IsSpawned;
            var state = spawned ? match.State.Value : default;
            var world = spawned ? match.Blocks?.FixedBlocks : null;
            var camera = Camera.main;
            bool managerReady = spawned && match.NetworkManager != null && match.NetworkManager.IsListening;
            double serverTime = managerReady ? match.NetworkManager.ServerTime.Time : 0;
            _pendingFrame = new FrameRecord
            {
                frameId = Time.frameCount, stage = _stage, phase = spawned ? (int)state.Phase : -1,
                command = _command, generation = _report.connectionGenerations, combatSegment = _combatSegment,
                frameStartUnscaled = Time.unscaledTimeAsDouble, metadataRealtime = Time.realtimeSinceStartupAsDouble,
                metadataTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), serverTime = serverTime,
                roundStartsAt = state.StartsAt, elapsed = serverTime - state.StartsAt,
                configuredSeconds = GameplayConfig.Mode.MatchSeconds,
                matchAvailable = match != null, matchSpawned = spawned, players = spawned ? state.PlayerCount : 0,
                localPlayerAvailable = PrototypePlayer.Local != null && PrototypePlayer.Local.IsSpawned,
                initialSyncComplete = spawned && match.InitialSyncComplete,
                fixedSyncReady = spawned && match.FixedGridBlockSyncReady,
                fixedWorldAvailable = world != null, authority = spawned && match.IsServer,
                graphics = _report.graphics, playing = spawned && state.Phase == MatchPhase.Playing,
                epoch = world?.Epoch ?? 0, round = spawned ? state.Round : 0, revision = world?.Revision ?? 0,
                surfaceRevision = world?.Surfaces.Revision ?? 0, advertisedRevision = state.FixedBlockRevision,
                dependency = dependency, dependencyAvailable = dependencyAvailable,
                queryPlayerInstanceId = queryPlayer, queryPlayerNetworkId = dependencyAvailable ? local.NetworkObjectId : 0,
                dependencyReady = dependencyAvailable && dependency.Gate.Ready && dependency.Authority.Ready && dependency.Presented.Ready,
                replayPending = dependency.Gate.PredictionPaused || dependency.Gate.SyncReplayPending || dependency.Gate.TerrainReplayPending || dependency.Gate.MovementBoundaryPending,
                requiredEpoch = dependency.Gate.Epoch, requiredRound = dependency.Gate.Round, requiredRevision = dependency.Gate.RequiredRevision,
                queryStepsDelta = queryStepsDelta, invalidQueryStepsDelta = invalidQueryStepsDelta,
                allQuerySteps = allQuerySteps, allInvalidQuerySteps = allInvalidQuerySteps,
                allQueryStepsDelta = allQueryStepsDelta, allInvalidQueryStepsDelta = allInvalidQueryStepsDelta,
                allQueryTokenErrors = allQueryTokenErrors, allQueryTokenErrorsDelta = allQueryTokenErrorsDelta,
                queryCounterRegression = queryCounterRegression,
                captureTick = spawned ? match.FixedGridCaptureTick : 0,
                networkTick = managerReady ? (uint)match.NetworkManager.ServerTime.Tick : 0,
                width = Screen.width, height = Screen.height, cameraId = camera != null ? camera.GetInstanceID() : 0,
                mainCameraEnabled = camera != null && camera.enabled,
                mainCameraActive = camera != null && camera.gameObject.activeInHierarchy,
                mainCameraTargetTexture = camera != null && camera.targetTexture != null,
                cameraPixelWidth = camera != null ? camera.pixelWidth : 0, cameraPixelHeight = camera != null ? camera.pixelHeight : 0,
                cameraTargetDisplay = camera != null ? camera.targetDisplay : -1,
                cameraOrthographic = camera != null && camera.orthographic,
                cameraFieldOfView = camera != null ? camera.fieldOfView : 0,
                authorityFixedSteps = steps
            };
            _hasPendingFrame = true;
        }
        void CompletePreviousFrame()
        {
            using var marker = FrameEvidenceCpu.Auto();
            // Read even outside combat so sparse markers cannot carry an old value
            // into a later frame. These are structs and preallocated list writes.
            var main = _main.Read(); var render = _render.Read();
            var physics = _physics.Read(); var gc = _gc.Read();
            bool retain = _hasPendingFrame && !_finished;
            var row = _pendingFrame;
            if (retain)
            {
                row.observedAtFrameId = Time.frameCount;
                row.observedRealtime = Time.realtimeSinceStartupAsDouble;
                row.observedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                row.observedFrameStartUnscaled = Time.unscaledTimeAsDouble;
                row.frameMilliseconds = (row.observedFrameStartUnscaled - row.frameStartUnscaled) * 1000;
                row.mainThread = main; row.renderThread = render; row.physics = physics; row.gc = gc;
                row.paperSampling = PaperSamplingDiagnostics.ReadFrame(row.frameId);
                ObservePaperSampling(ref row);
                row.cpuOffset = _report.cpuRawValues.Count; row.cpuCount = _cpu.Count;
                row.rejection = (int)RejectFrame(row); row.eligible = row.rejection == 0;
            }
            bool wrapped = main.wrapped || render.wrapped || physics.wrapped || gc.wrapped;
            foreach (var item in _cpu)
            {
                var value = item.series.Read();
                wrapped |= value.wrapped;
                if (!retain) continue;
                if (_report.cpuRawValues.Count == _report.cpuRawValues.Capacity ||
                    _report.cpuRecorderStates.Count == _report.cpuRecorderStates.Capacity ||
                    _report.cpuSampleCounts.Count == _report.cpuSampleCounts.Capacity)
                    _report.frameEvidence.cpuBufferGrowths++;
                _report.cpuRawValues.Add(value.raw); _report.cpuRecorderStates.Add(value.State);
                _report.cpuSampleCounts.Add(value.calls);
                if (row.playing) item.series.Append(value);
            }
            _hasPendingFrame = false;
            if (!retain) return;
            var evidence = _report.frameEvidence;
            if (wrapped) evidence.recorderWrappedFrames++;
            evidence.combatFrames++;
            if (row.playing) evidence.playingFrames++;
            if (row.phase < 0) evidence.unknownPhaseFrames++;
            if (!row.eligible) evidence.rejectedCombatFrames++;
            if (row.playing && !row.eligible) evidence.rejectedPlayingFrames++;
            if (evidence.lastFrameId >= 0)
            {
                int difference = row.frameId - evidence.lastFrameId;
                if (difference <= 0) evidence.duplicateFrameIds++;
                // Leaving combat deliberately ends a segment. Reconnects and
                // command changes within combat cannot excuse a missing frame.
                else if (difference > 1 && _report.frameRecords.Count > 0)
                {
                    var previous = _report.frameRecords[^1];
                    if (previous.combatSegment == row.combatSegment)
                        evidence.missingFrameIds += difference - 1;
                }
            }
            if (evidence.firstFrameId < 0) { evidence.firstFrameId = row.frameId; evidence.firstFrameRealtime = row.metadataRealtime; }
            evidence.lastFrameId = row.frameId; evidence.lastFrameRealtime = row.observedRealtime;
            if (_report.frameRecords.Count == _report.frameRecords.Capacity) evidence.frameBufferGrowths++;
            _report.frameRecords.Add(row);
            if (row.playing)
            {
                if (row.mainCameraRendered) _report.renderedFrames++;
                if (((FrameRejection)row.rejection & (FrameRejection.MainCamera | FrameRejection.MainCameraNotRendered)) != 0)
                    _report.invalidCameraFrames++;
                if (((FrameRejection)row.rejection & FrameRejection.Resolution) != 0) _report.invalidResolutionFrames++;
            }
            if (row.eligible)
            {
                evidence.eligibleFrames++;
                if (evidence.firstEligibleElapsed < 0) evidence.firstEligibleElapsed = row.elapsed;
                evidence.lastEligibleElapsed = row.elapsed;
            }
            if (!row.playing) return;
            if (_sampleAt < 0) _sampleAt = row.metadataRealtime;
            _frames.Add(row.frameMilliseconds); _report.sampledSeconds = row.observedRealtime - _sampleAt;
            _report.minimumPlayers = Math.Min(_report.minimumPlayers, row.players);
            _report.width = row.width; _report.height = row.height;
            _report.configuredSeconds = row.configuredSeconds; _report.elapsedServerSeconds = row.elapsed;
            _main.Append(main); _render.Append(render); _physics.Append(physics); _gc.Append(gc);
            _report.completedFixedStepsPerFrame.Add(row.authorityFixedSteps);
        }
        static FrameRejection RejectFrame(FrameRecord row)
        {
            var result = FrameRejection.None;
            if (!row.matchAvailable) result |= FrameRejection.MissingMatch;
            if (!row.matchSpawned) result |= FrameRejection.MatchNotSpawned;
            if (!row.localPlayerAvailable) result |= FrameRejection.MissingLocalPlayer;
            if (!row.playing) result |= FrameRejection.NotPlaying;
            if (!row.initialSyncComplete) result |= FrameRejection.InitialSyncIncomplete;
            if (!row.fixedSyncReady) result |= FrameRejection.FixedSyncIncomplete;
            if (!row.fixedWorldAvailable) result |= FrameRejection.MissingFixedWorld;
            if (row.width != 1920 || row.height != 1080 || row.cameraPixelWidth != 1920 || row.cameraPixelHeight != 1080)
                result |= FrameRejection.Resolution;
            if (row.cameraId == 0 || !row.mainCameraEnabled || !row.mainCameraActive || row.mainCameraTargetTexture ||
                row.cameraOrthographic || row.cameraTargetDisplay != 0) result |= FrameRejection.MainCamera;
            if (!row.mainCameraRendered) result |= FrameRejection.MainCameraNotRendered;
            if (!double.IsFinite(row.frameMilliseconds) || row.frameMilliseconds <= 0) result |= FrameRejection.InvalidInterval;
            if (row.observedAtFrameId != row.frameId + 1) result |= FrameRejection.NonconsecutiveFrame;
            if (!row.graphics) result |= FrameRejection.NullGraphics;
            if (row.invalidQueryStepsDelta > 0 || row.allInvalidQueryStepsDelta > 0 || row.allQueryTokenErrorsDelta > 0 || row.queryCounterRegression)
                result |= FrameRejection.InvalidFixedQuery;
            return result;
        }
        void ObservePaperSampling(ref FrameRecord row)
        {
            var evidence = _report.paperSampling;
            var sample = row.paperSampling;
            if (!sample.available || !sample.enabled || sample.schema != PaperSamplingDiagnostics.Schema)
            { evidence.unavailableFrames++; return; }
            evidence.availableFrames++;
            bool generationChanged = sample.generation != evidence.generation;
            if (generationChanged) { evidence.generationChanges++; evidence.generation = sample.generation; }
            row.paperCounterRegression = !generationChanged && !sample.cumulative.IsMonotonicFrom(
                evidence.firstFrameId >= 0 ? evidence.latestCumulative : evidence.baseline);
            row.paperPartitionInvalid = !sample.delta.HasValidPartitions() || !sample.cumulative.HasValidPartitions();
            if (row.paperCounterRegression) evidence.regressions++;
            if (row.paperPartitionInvalid) evidence.invalidPartitions++;
            if (evidence.firstFrameId < 0) evidence.firstFrameId = row.frameId;
            evidence.lastFrameId = row.frameId;
            evidence.latestCumulative = sample.cumulative;
            evidence.retainedDeltas = PaperSamplingDiagnostics.Counts.Sum(evidence.retainedDeltas, sample.delta);
            if (row.playing) evidence.playingDeltas = PaperSamplingDiagnostics.Counts.Sum(evidence.playingDeltas, sample.delta);
        }
        void CompleteFrameEvidence()
        {
            var evidence = _report.frameEvidence;
            evidence.observationComplete = evidence.combatFrames > 0 && evidence.playingFrames > 0 &&
                !_hasPendingFrame && evidence.combatFrames == evidence.observedCombatFrameCallbacks &&
                evidence.rejectedPlayingFrames == 0 && evidence.unknownPhaseFrames == 0 &&
                evidence.missingFrameIds == 0 && evidence.duplicateFrameIds == 0 && evidence.recorderWrappedFrames == 0 &&
                evidence.playingFrames == _frames.Count && _report.frameRecords.Count == evidence.combatFrames &&
                _report.cpuRawValues.Count == evidence.combatFrames * _cpu.Count &&
                _report.cpuRecorderStates.Count == _report.cpuRawValues.Count &&
                _report.cpuSampleCounts.Count == _report.cpuRawValues.Count;
            evidence.eventObservationComplete = evidence.observedPaidCallbacks == _report.paidEvents.Count &&
                evidence.observedActualCallbacks == _report.actualEvents.Count &&
                evidence.lastEventSequence == evidence.observedPaidCallbacks + evidence.observedActualCallbacks;
            var paper = _report.paperSampling;
            paper.complete = paper.availableFrames == _report.frameRecords.Count && paper.availableFrames > 0 &&
                paper.unavailableFrames == 0 && paper.generationChanges == 0 && paper.regressions == 0 && paper.invalidPartitions == 0;
        }
        void ReserveAuthorityEventEvidence()
        {
            if (_report.frameEvidence.authorityEventBuffersReserved) return;
            _report.paidEvents.Capacity = Math.Max(_report.paidEvents.Capacity, 8192);
            _report.actualEvents.Capacity = Math.Max(_report.actualEvents.Capacity, 65536);
            _report.frameEvidence.authorityEventBuffersReserved = true;
        }
        static SourceRecord OriginalSource(BlockAttackObservation.Source source) => new()
        {
            round = source.Round, owner = source.Owner, life = source.Life, equipment = source.Equipment,
            action = source.Action, team = source.Team, kind = (int)source.Kind, hero = source.Hero,
            sub = source.SubWeaponId, configRevision = source.ConfigRevision
        };
        EventContext CaptureEventContext(uint sourceRound, double serverTime, bool eligible)
        {
            var match = PrototypeMatch.Current;
            bool spawned = match != null && match.IsSpawned;
            var state = spawned ? match.State.Value : default;
            double elapsed = spawned ? serverTime - state.StartsAt : 0;
            var rejection = EventRejection.None;
            if (_stage != "combat") rejection |= EventRejection.NotCombat;
            if (!spawned) rejection |= EventRejection.MissingMatch;
            if (!spawned || !match.IsServer) rejection |= EventRejection.NotAuthority;
            if (!spawned || state.Phase != MatchPhase.Playing) rejection |= EventRejection.NotPlaying;
            if (spawned && state.Round != sourceRound) rejection |= EventRejection.SourceRoundMismatch;
            if (!double.IsFinite(elapsed) || elapsed < 0) rejection |= EventRejection.InvalidElapsed;
            if (_report.workloadEvidence.round != 0 && _report.workloadEvidence.round != sourceRound)
                rejection |= EventRejection.EvidenceRoundMismatch;
            if (_finished) rejection |= EventRejection.Finished;
            var world = spawned ? match.Blocks?.FixedBlocks : null;
            _report.frameEvidence.lastEventSequence = ++_rawEventSequence;
            return new EventContext { sequence = _rawEventSequence, monotonicTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                frameId = Time.frameCount, receiptRealtime = Time.realtimeSinceStartupAsDouble, stage = _stage,
                phase = spawned ? (int)state.Phase : -1, currentRound = spawned ? state.Round : 0,
                currentEpoch = world?.Epoch ?? 0, currentRevision = world?.Revision ?? 0, currentSurfaceRevision = world?.Surfaces.Revision ?? 0,
                command = _command, generation = _report.connectionGenerations, eligible = eligible,
                authority = spawned && match.IsServer, matchAvailable = spawned,
                rejection = (int)rejection, elapsed = double.IsFinite(elapsed) ? elapsed : 0, roundStartsAt = state.StartsAt };
        }
        void RecordPaidEvent(BlockAttackObservation.Payment payment, bool eligible)
        {
            _report.frameEvidence.observedPaidCallbacks++;
            ReserveAuthorityEventEvidence();
            if (_report.paidEvents.Count == _report.paidEvents.Capacity) _report.frameEvidence.paidBufferGrowths++;
            _report.paidEvents.Add(new PaymentRecord { source = OriginalSource(payment.Source),
                context = CaptureEventContext(payment.Source.Round, payment.ServerTime, eligible),
                material = payment.Material, serverTime = payment.ServerTime, networkTick = payment.NetworkTick });
        }
        void RecordActualEvent(BlockAttackObservation.ActualResult actual, bool eligible)
        {
            _report.frameEvidence.observedActualCallbacks++;
            ReserveAuthorityEventEvidence();
            if (_report.actualEvents.Count == _report.actualEvents.Capacity) _report.frameEvidence.actualBufferGrowths++;
            var counts = actual.Counts;
            _report.actualEvents.Add(new ActualRecord { source = OriginalSource(actual.Source),
                context = CaptureEventContext(actual.Source.Round, actual.ServerTime, eligible), operation = (int)actual.Operation,
                entity = actual.Entity, ordinal = actual.Ordinal, epoch = actual.Epoch, committedRound = actual.Round,
                revision = actual.Revision, surfaceRevision = actual.SurfaceRevision, networkTick = actual.NetworkTick,
                serverTime = actual.ServerTime, milliseconds = actual.Milliseconds,
                auxiliary = actual.Auxiliary, hadContact = actual.HadContact, hadChanges = actual.HadChanges,
                counts = new ActualCountRecord { newFixed = counts.NewFixed, newPaint = counts.NewPaint,
                    enemyPaintReplaced = counts.EnemyPaintReplaced, enemyFixedRemoved = counts.EnemyFixedRemoved,
                    friendlyFixedRemoved = counts.FriendlyFixedRemoved, enemyPaintRemoved = counts.EnemyPaintRemoved,
                    friendlyPaintRemoved = counts.FriendlyPaintRemoved, converted = counts.Converted, relocated = counts.Relocated } });
        }
    }
}
#endif
