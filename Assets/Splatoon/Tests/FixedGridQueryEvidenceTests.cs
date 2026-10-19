#if UNITY_EDITOR
using System;
using NUnit.Framework;
using Splatoon.Prototype;
using static Splatoon.Prototype.PrototypePlayer;

namespace Splatoon.Tests
{
    [Category("FixedGridQueryEvidence")]
    public sealed class FixedGridQueryEvidenceTests
    {
        static FixedQueryWorldVersion World(uint revision = 22) => new()
        { Available = true, TerrainObjectId = 41, Epoch = 3, Round = 7, Revision = revision, SurfaceRevision = revision };

        static FixedQueryActorDependency Actor(uint required = 22) => new()
        { Life = 8, HeroRevision = 2, SimulationTick = 99, Epoch = 3, Round = 7, RequiredRevision = required, RequiresDependency = true };

        static FixedQueryReplayGate Gate(uint required = 22) => new()
        { Epoch = 3, Round = 7, RequiredRevision = required, RoundKnown = true, InitialSyncComplete = true, FixedSyncReady = true };

        static FixedQueryExecution Execution(uint required = 22) => new()
        {
            Token = 1, Frame = 100, Source = Actor(required), Gate = Gate(required),
            WorldBegin = World(), WorldEnd = World(), Completed = true, SameWorldObject = true
        };

        [Test] public void AdvertisedLaterCommitDoesNotBlockSatisfiedActualDependency()
        {
            const uint advertised = 29;
            var result = ValidateFixedQueryExecution(Execution());
            Assert.That(advertised, Is.GreaterThan(result.WorldBegin.Revision));
            Assert.That(result.Source.Ready && result.Gate.Ready && result.Valid, Is.True);
        }

        [Test] public void MissingDependencyWithNoQueryRecordsARealPause()
        {
            var actor = Actor(29); var gate = Gate(29); gate.TerrainReplayPending = true;
            var counters = new FixedQueryCounters();
            Assert.That(FixedQueryActorReady(actor, World()), Is.False);
            Assert.That(FixedQueryGateReady(gate, World()), Is.False);
            Assert.That(counters.StartedSteps, Is.Zero); Assert.That(counters.CompletedSteps, Is.Zero);
            Assert.That(counters.InvalidQuerySteps, Is.Zero);
        }

        [Test] public void ExecutingAgainstMissingActualDependencyIsInvalid()
        {
            var result = ValidateFixedQueryExecution(Execution(29));
            Assert.That(result.Source.Ready, Is.False); Assert.That(result.Valid, Is.False);
        }

        [Test] public void EqualRevisionFromAnotherEpochIsNotAValidDependency()
        {
            var execution = Execution(); execution.Source.Epoch++;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
        }

        [Test] public void EqualRevisionFromAnotherRoundIsNotAValidDependency()
        {
            var execution = Execution(); execution.Source.Round++;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
        }

        [Test] public void ExactCommitRequiresTheSameFixedSupportHandle()
        {
            var execution = Execution(); execution.Source.SupportIsFixedGrid = true;
            execution.Source.SupportReady = false;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
            execution.Source.SupportReady = true;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.True);
        }

        [Test] public void NewerCommittedWorldCanRemoveAnOldSupportAndResume()
        {
            var execution = Execution(21); execution.Source.SupportIsFixedGrid = true;
            execution.Source.SupportReady = false;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.True);
        }

        [Test] public void RetainedReplayRequirementCannotBeHiddenByOlderPresentedState()
        {
            var execution = Execution(); execution.Gate.RequiredRevision = 29;
            var result = ValidateFixedQueryExecution(execution);
            Assert.That(result.Source.Ready, Is.True); Assert.That(result.Gate.Ready, Is.False);
            Assert.That(result.Valid, Is.False);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void EveryClientPauseGateRejectsAnActualQuery(int pending)
        {
            var execution = Execution();
            switch (pending)
            {
                case 0: execution.Gate.InitialSyncComplete = false; break;
                case 1: execution.Gate.FixedSyncReady = false; break;
                case 2: execution.Gate.PredictionPaused = true; break;
                case 3: execution.Gate.SyncReplayPending = true; break;
                case 4: execution.Gate.TerrainReplayPending = true; break;
                case 5: execution.Gate.MovementBoundaryPending = true; break;
            }
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
        }

        [Test] public void AuthorityQueriesDoNotUseClientReplayPauseFlags()
        {
            var execution = Execution(); execution.Gate = default; execution.Gate.IsAuthority = true;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.True);
            execution.Source.RequiredRevision++;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
        }

        [Test] public void MultipleIntervalsRetainAnEarlierInvalidQuery()
        {
            var counters = new FixedQueryCounters();
            counters.Begin(); counters.Finish(ValidateFixedQueryExecution(Execution()));
            counters.Begin(); counters.Finish(ValidateFixedQueryExecution(Execution(29)));
            counters.Begin(); counters.Finish(ValidateFixedQueryExecution(Execution()));
            Assert.That(counters.StartedSteps, Is.EqualTo(3)); Assert.That(counters.CompletedSteps, Is.EqualTo(3));
            Assert.That(counters.InvalidQuerySteps, Is.EqualTo(1));
        }

        [Test] public void AnInspectionWithNoIntervalsCannotClaimAQuery()
        {
            var snapshot = new FixedQueryDependencySnapshot { World = World(), Authority = Actor(), Presented = Actor(), Gate = Gate() };
            Assert.That(snapshot.StartedSteps, Is.Zero); Assert.That(snapshot.CompletedSteps, Is.Zero);
            Assert.That(snapshot.Last.Token, Is.Zero); Assert.That(snapshot.Last.Completed, Is.False);
        }

        [Test] public void CommitChangingInsideAnIntervalInvalidatesItsQueryView()
        {
            var execution = Execution(); execution.WorldEnd.Revision++;
            var result = ValidateFixedQueryExecution(execution);
            Assert.That(result.SameWorld, Is.False); Assert.That(result.Valid, Is.False);
            var counters = new FixedQueryCounters(); counters.Begin(); counters.Finish(result);
            Assert.That(counters.DifferentWorldSteps, Is.EqualTo(1)); Assert.That(counters.InvalidQuerySteps, Is.EqualTo(1));
        }

        [Test] public void ReplacingWorldObjectWithIdenticalVersionsIsStillDetected()
        {
            var execution = Execution(); execution.SameWorldObject = false;
            Assert.That(ValidateFixedQueryExecution(execution).SameWorld, Is.False);
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
        }

        [Test] public void AnExceptionLeavesStartedDifferentFromCompleted()
        {
            var counters = new FixedQueryCounters(); counters.Begin();
            var execution = Execution(); execution.Completed = false;
            counters.Finish(ValidateFixedQueryExecution(execution));
            Assert.That(counters.StartedSteps, Is.EqualTo(1)); Assert.That(counters.CompletedSteps, Is.Zero);
            Assert.That(counters.AbortedSteps, Is.EqualTo(1)); Assert.That(counters.InvalidQuerySteps, Is.EqualTo(1));
        }

        [Test] public void ExplicitFixedDependencyIsCheckedBeforeDeadOrTimedOutExemptions()
        {
            var execution = Execution(29); execution.Source.RequiresDependency = false;
            execution.Gate.RequiredRevision = 0; execution.Gate.Epoch = 0;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
            execution.Source.Epoch = 0;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.True);
        }

        [Test] public void ZeroTokenCannotBePresentedAsAnExecutedQuery()
        {
            var execution = Execution(); execution.Token = 0;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
        }

        [Test] public void SourceWithoutFixedEpochStillHonorsAnExplicitLegacySupportDependency()
        {
            var execution = Execution(); execution.Source.Epoch = 0; execution.Source.SupportRevision = 7;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.False);
            execution.Source.SupportReady = true;
            Assert.That(ValidateFixedQueryExecution(execution).Valid, Is.True);
        }

        [Test] public void PureQueryEvidenceValidationAndCountersAllocateNoManagedMemory()
        {
            var execution = Execution(); var counters = new FixedQueryCounters();
            for (int i = 0; i < 64; i++) { counters.Begin(); counters.Finish(ValidateFixedQueryExecution(execution)); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) { counters.Begin(); counters.Finish(ValidateFixedQueryExecution(execution)); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero); Assert.That(counters.CompletedSteps, Is.EqualTo(1088));
        }
    }
}
#endif
