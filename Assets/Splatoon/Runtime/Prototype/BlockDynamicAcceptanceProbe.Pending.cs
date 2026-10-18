#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Splatoon.Blocks;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        public sealed partial class NaturalReport
        {
            public string oldestPendingClock = "BlockTerrain.SimulationTime minus oldest command/pending CreatedAt; simulation seconds, not wall seconds";
            public string pendingTerminalClock = "Stopwatch elapsed wall milliseconds; scalar copies only, no worker piece arrays retained";
            public bool detailedPendingObservationRequested, detailedPendingObservationStarted, pendingEndCaptured;
            public int pendingTerminalCapacity = 2048, pendingTerminalsDropped;
            public string pendingEndPhase;
            public double pendingEndSimulationTime;
            public BlockTerrain.PendingWorkDiagnostics pendingWorkAtEnd;
            public BlockTerrain.PublicationRetryDiagnostics publicationRetryAtEnd;
            public List<WorkloadPendingTerminal> pendingTerminals = new();
        }
        [Serializable] public sealed class WorkloadPendingTerminal
        {
            public long id;
            public int pieces;
            public string outcome, abortReason;
            public bool workerAvailable, generationObserved, uploadCompleted, bakeObserved, publishEntered;
            public double generationObservedMs, uploadMs, bakeTailMs, readyWaitMs, publicationMs, totalMs;
            public double workerCompletionToMainObservedMs;
        }
        BlockTerrain _workloadPendingTerrain;
        bool _workloadPendingDisposed;

        void ObserveWorkloadPending(PrototypeMatch match)
        {
            var report = _report.natural;
            report.detailedPendingObservationRequested = _cpuCapture != null;
            // Subscribing enables production wall tracking. Formal runs without
            // the explicit native CPU-profile request never install this observer.
            if (_cpuCapture == null || _workloadPendingDisposed || match == null || !match.IsServer) return;
            var terrain = match.Blocks;
            if (terrain == _workloadPendingTerrain) return;
            if (_workloadPendingTerrain != null) _workloadPendingTerrain.PendingTransactionFinished -= WorkloadPendingFinished;
            _workloadPendingTerrain = terrain;
            if (terrain == null) return;
            terrain.PendingTransactionFinished += WorkloadPendingFinished;
            report.detailedPendingObservationStarted = true;
        }
        void WorkloadPendingFinished(BlockTerrain.PendingWallDiagnostic value)
        {
            if (_stage != "combat" || _workloadPendingDisposed) return;
            var report = _report.natural;
            if (report.pendingTerminals.Count >= report.pendingTerminalCapacity)
            { report.pendingTerminalsDropped++; return; }
            // Worker contains a per-piece array. Read only the requested scalar
            // and availability flag; neither the diagnostic nor its arrays escape.
            report.pendingTerminals.Add(new WorkloadPendingTerminal { id = value.Id, pieces = value.Pieces,
                outcome = value.Outcome.ToString(), abortReason = value.AbortReason.ToString(),
                generationObserved = value.GenerationObserved, uploadCompleted = value.UploadCompleted,
                bakeObserved = value.BakeObserved, publishEntered = value.PublishEntered,
                generationObservedMs = value.GenerationObservedMs, uploadMs = value.UploadMs,
                bakeTailMs = value.BakeObservedTailMs, readyWaitMs = value.ReadyWaitMs,
                publicationMs = value.PublicationMs, totalMs = value.TotalMs,
                workerAvailable = value.Worker.Available,
                workerCompletionToMainObservedMs = value.Worker.CompletionToMainObservedMs });
        }
        void CaptureWorkloadPendingEnd()
        {
            if (_report.natural.pendingEndCaptured || _match == null || !_match.IsServer || _match.Blocks == null ||
                _match.State.Value.Phase != MatchPhase.Finished) return;
            var report = _report.natural; var terrain = _match.Blocks;
            report.pendingEndCaptured = true; report.pendingEndPhase = _match.State.Value.Phase.ToString();
            report.pendingEndSimulationTime = terrain.SimulationTime;
            report.pendingWorkAtEnd = terrain.InspectPendingWork();
            report.publicationRetryAtEnd = terrain.InspectPublicationRetry();
            DisposeWorkloadPending();
        }
        void DisposeWorkloadPending()
        {
            if (_workloadPendingTerrain != null) _workloadPendingTerrain.PendingTransactionFinished -= WorkloadPendingFinished;
            _workloadPendingTerrain = null; _workloadPendingDisposed = true;
        }
    }
}
#endif
