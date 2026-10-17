using System;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // Diagnostic labels only. They do not change validation, retirement,
        // captured windows, paid-command ownership or publication admission.
        public enum PendingAbortReason
        {
            Unclassified, EpochChanged, PrivateSourceChanged,
            FairBackgroundYield, FairReservationChanged, FairContextChanged, FairSourceChanged,
            OwnActivationMissingProof, OwnActivationGenerationChanged, OwnActivationLandingState,
            OwnActivationSourceSpan, OwnActivationLandingOffset,
            SupportDraftIdentity, SupportSnapshotIdentity, SupportNativeBoundsChanged,
            SupportRayBufferFull, SupportRayNowOccupied,
            BuildSourceChanged, BuildMovingOverlap, BuildAuthorityCancellation,
            BindingPoseAboveNewCap, HistoricalRecoveryConflict,
            HistoricalRecoverySourceChanged, HistoricalRecoveryWitnessGone
        }
        [Serializable] public struct PublicationRetryDiagnostics
        {
            public long trackedAborts, lastAbortId;
            public long[] abortsByReason;
            public long buildRepairArmed, buildRepairPublished, buildRepairNoChange, buildRepairNoReady, buildRepairCancelled, buildRepairRejected, buildRepairFairDeclined;
            public bool buildRepairActive;
            public long buildRepairTargetSelected, buildRepairDependencySelected, buildRepairTargetServed;
            public long buildRepairTargetBudgetDeferred, buildRepairTargetBlocked, buildRepairUnrelatedPublished, buildRepairPreparationHandoffs, buildRepairPriorityDebt;
            public ulong buildRepairTargetMotion, buildRepairServiceMotion;
            public bool buildRepairTargetCohort, buildRepairServiceCohort, buildRepairWaitingPreparation;
            public int buildRepairColumnX, buildRepairColumnZ;
            public uint lastAbortRevision;
            public double lastAbortSimulationTime;
            public PendingAbortReason lastAbortReason;
            public long ownActivationDeferrals, staticProofInvalidations, supportRetries, bindingDeferrals;
            public long buildRetries, buildMovingOccupied, buildPreparationStarts, buildPreparationStages, buildPreparationWork;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public long buildPreparationObservedStarts, buildPreparationZeroPreparedStarts;
#endif
            public ulong buildPreparationProgress;
            public int buildPreparationPhase;
            public bool buildPreparationActive, buildPreparationRetry, buildPreparationReady;
            public long headPreparationStarts, headPreparationStages, headPreparationWork, headPreparationAborts;
            public ulong headPreparationProgress;
            public long privatePreparationStarts, privatePreparationStages, privatePreparationWork, privatePreparationAborts;
            public ulong privatePreparationProgress;
            public long historicalStarts, historicalStages, historicalWork, historicalRecovered, historicalConflicts;
            public long historicalNoLongerInvalidMissingTarget, historicalNoLongerInvalidInactivePhase;
            public long historicalNoLongerInvalidClassifiedValid, historicalNoLongerInvalidWitnessGone, historicalOwnedCloneStarts;
            public ulong historicalProgress;
            public HistoricalRecoveryOutcome historicalOutcome;
            public long fairYields, fairRebuilds, fairReservationRestages, fairContextInvalidations;
            public long fairForegroundPublications, fairDependencyPublications, fairForegroundPreparationPublications;
            public long fairBackgroundPublications, fairMetadataPublications, fairCompleteBlockedScans;
            public long fairUnclassifiedRestages, fairExpiredScans, fairScanWork, fairInputsDuringSeed;
            public long fairPhysicalFailures, fairRetiredPhysicalFailures, fairRetiredCompletions, fairPreparationRetirements;
            public bool fairSeedActive, fairSeedForeground;
            public double fairSeedSeconds;
            public ulong fairInputVersion, fairReservationVersion, fairWorkVersion;
            public long cpuDeferrals, lastBuildPublicationWork, lastBindingPublicationWork;
        }
        readonly long[] _publicationAbortsByReason = new long[(int)PendingAbortReason.HistoricalRecoveryWitnessGone + 1];
        long _publicationTrackedAborts, _publicationLastAbortId;
        uint _publicationLastAbortRevision;
        double _publicationLastAbortSimulationTime;
        PendingAbortReason _publicationLastAbortReason;
        void ObservePublicationAbort(Pending pending)
        {
            // Called once inside the existing tracked terminal boundary after
            // WallReported is set, never per cell, worker or frame poll.
            _publicationTrackedAborts++;
            _publicationAbortsByReason[(int)pending.AbortReason]++;
            _publicationLastAbortId = pending.WallId;
            _publicationLastAbortRevision = Revision;
            _publicationLastAbortSimulationTime = _now;
            _publicationLastAbortReason = pending.AbortReason;
        }
        // Absolute lifetime counters; compare start/end or adjacent 1s samples.
        // trackedAborts covers only TrackWall transactions, as PendingWork does.
        public PublicationRetryDiagnostics InspectPublicationRetry() => new()
        {
            trackedAborts = _publicationTrackedAborts, lastAbortId = _publicationLastAbortId,
            abortsByReason = (long[])_publicationAbortsByReason.Clone(),
            buildRepairArmed = _buildRepairArmed, buildRepairPublished = _buildRepairPublished,
            buildRepairNoChange = _buildRepairNoChange, buildRepairNoReady = _buildRepairNoReady,
            buildRepairCancelled = _buildRepairCancelled, buildRepairRejected = _buildRepairRejected,
            buildRepairFairDeclined = _buildRepairFairDeclined,
            buildRepairActive = _buildOverlapRepair != null,
            buildRepairTargetSelected = _buildRepairTargetSelected, buildRepairDependencySelected = _buildRepairDependencySelected,
            buildRepairTargetServed = _buildRepairTargetServed, buildRepairTargetBudgetDeferred = _buildRepairTargetBudgetDeferred,
            buildRepairTargetBlocked = _buildRepairTargetBlocked, buildRepairUnrelatedPublished = _buildRepairUnrelatedPublished,
            buildRepairPreparationHandoffs = _buildRepairPreparationHandoffs, buildRepairPriorityDebt = _buildRepairPriorityDebt,
            buildRepairTargetMotion = _buildOverlapRepair?.Motion.Id ?? 0, buildRepairTargetCohort = _buildOverlapRepair?.Motion.Cohort ?? false,
            buildRepairServiceMotion = _buildOverlapRepair?.ServiceMotion.Id ?? 0, buildRepairServiceCohort = _buildOverlapRepair?.ServiceMotion.Cohort ?? false,
            buildRepairWaitingPreparation = _buildOverlapRepair?.Preparation != null,
            buildRepairColumnX = _buildOverlapRepair?.Column.x ?? 0, buildRepairColumnZ = _buildOverlapRepair?.Column.y ?? 0,
            lastAbortRevision = _publicationLastAbortRevision, lastAbortSimulationTime = _publicationLastAbortSimulationTime,
            lastAbortReason = _publicationLastAbortReason,
            ownActivationDeferrals = OwnLandingPublicationDeferrals, staticProofInvalidations = StaticProofInvalidations,
            supportRetries = GravitySupportPublicationRetries, bindingDeferrals = GravityBindingPublicationDeferrals,
            buildRetries = BuildPublicationRetries, buildMovingOccupied = BuildMovingOccupied,
            buildPreparationStarts = BuildOccupancyPreparationStarts, buildPreparationStages = BuildOccupancyPreparationStages,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            buildPreparationObservedStarts = _buildPreparationObservedStarts, buildPreparationZeroPreparedStarts = _buildPreparationZeroPreparedStarts,
#endif
            buildPreparationWork = BuildOccupancyPreparationWork, buildPreparationProgress = BuildOccupancyProgressVersion,
            buildPreparationActive = _buildOccupancyPreparation != null, buildPreparationPhase = _buildOccupancyPreparation?.Phase ?? -1,
            buildPreparationRetry = _buildOccupancyPreparation?.Retry ?? false, buildPreparationReady = _buildOccupancyPreparation?.Ready ?? false,
            headPreparationStarts = HeadPreparationStarts, headPreparationStages = HeadPreparationStages,
            headPreparationWork = HeadPreparationWork, headPreparationAborts = HeadPreparationAborts, headPreparationProgress = HeadPreparationProgressVersion,
            privatePreparationStarts = PrivatePreparationStarts, privatePreparationStages = PrivatePreparationStages,
            privatePreparationWork = PrivatePreparationWork, privatePreparationAborts = PrivatePreparationAborts,
            privatePreparationProgress = PrivatePreparationProgressVersion,
            historicalStarts = HistoricalRecoveryStarts, historicalStages = HistoricalRecoveryStages, historicalWork = HistoricalRecoveryWork,
            historicalRecovered = HistoricalRecoveryCount, historicalConflicts = HistoricalRecoveryPublicationConflicts,
            historicalNoLongerInvalidMissingTarget = HistoricalNoLongerInvalidMissingTarget,
            historicalNoLongerInvalidInactivePhase = HistoricalNoLongerInvalidInactivePhase,
            historicalNoLongerInvalidClassifiedValid = HistoricalNoLongerInvalidClassifiedValid,
            historicalNoLongerInvalidWitnessGone = HistoricalNoLongerInvalidWitnessGone, historicalOwnedCloneStarts = HistoricalOwnedCloneStarts,
            historicalProgress = HistoricalRecoveryProgressVersion, historicalOutcome = LastHistoricalRecoveryOutcome,
            fairYields = FairBackgroundYields, fairRebuilds = FairBackgroundRebuilds, fairReservationRestages = FairReservationRestages,
            fairContextInvalidations = FairContextInvalidations, fairForegroundPublications = FairForegroundAtomicPublications,
            fairDependencyPublications = FairDependencyAtomicPublications, fairForegroundPreparationPublications = FairForegroundPreparationPublications,
            fairBackgroundPublications = FairBackgroundTerrainPublications, fairMetadataPublications = FairBackgroundMetadataPublications,
            fairCompleteBlockedScans = FairCompleteBlockedScans, fairUnclassifiedRestages = FairUnclassifiedRestages,
            fairExpiredScans = FairExpiredScans, fairScanWork = FairScanWork, fairInputsDuringSeed = FairInputsDuringSeed,
            fairPhysicalFailures = FairPhysicalFailures, fairRetiredPhysicalFailures = FairRetiredPhysicalFailures,
            fairRetiredCompletions = FairRetiredCompletions, fairPreparationRetirements = FairPreparationRetirements,
            fairSeedActive = _fairSeed != null, fairSeedForeground = _fairSeed?.Foreground ?? false, fairSeedSeconds = FairCurrentSeedSeconds,
            fairInputVersion = _fairInputVersion, fairReservationVersion = _fairReservationVersion, fairWorkVersion = _fairWorkVersion,
            cpuDeferrals = PublicationCpuDeferrals, lastBuildPublicationWork = LastBuildPublicationWork,
            lastBindingPublicationWork = LastGravityBindingPublicationWork
        };
    }
}
