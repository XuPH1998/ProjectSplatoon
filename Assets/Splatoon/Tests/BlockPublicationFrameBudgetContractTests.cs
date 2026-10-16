#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Splatoon.Blocks;

namespace Splatoon.Tests
{
    // Prepared source only. Real Stopwatch verifies accounting/admission logic;
    // bounded spin below is not an upload, native PhysX, or frame benchmark.
    public sealed class BlockPublicationFrameBudgetContractTests
    {
        static BlockPublicationWindow Window(double milliseconds = 10000, int frameKeys = 32)
            => new BlockPublicationPolicy(20, 8, 32, 32, 8, frameKeys, milliseconds).Capture(BlockPublicationOperation.Gravity);

        [Test] public void InvalidFrameOrIncompleteWindowIsNotAdmitted()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window();
            Assert.That(ledger.Evaluate(window, 1), Is.EqualTo(BlockPublicationAdmission.InvalidWindow));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.BeginFrame(-1));
            ledger.BeginFrame(1);
            Assert.That(ledger.Evaluate(null, 1), Is.EqualTo(BlockPublicationAdmission.InvalidWindow));
            Assert.That(ledger.Evaluate(window, -1), Is.EqualTo(BlockPublicationAdmission.InvalidWindow));
            Assert.That(ledger.Evaluate(window, 33), Is.EqualTo(BlockPublicationAdmission.InvalidWindow));
            Assert.That(ledger.TryReserve(window, 33, out var reservation, out var admission), Is.False);
            Assert.That(reservation, Is.Null); Assert.That(admission, Is.EqualTo(BlockPublicationAdmission.InvalidWindow));
        }

        [Test] public void AnOldExpandedSnapshotPublishesAfterSelectedPolicyDowngrades()
        {
            var selected = BlockPublicationPolicy.ExpandedUncalibrated(10);
            var old = selected.Capture(BlockPublicationOperation.Gravity);
            selected = BlockPublicationPolicy.CompatibilityEight(11);
            var freshOrdinary = selected.Capture(BlockPublicationOperation.OrdinaryAttack);
            var ledger = new BlockPublicationFrameBudget(); ledger.BeginFrame(1);
            Assert.That(ledger.TryReserve(old, 32, out var reservation, out _), Is.True);
            using (reservation) reservation.Commit();
            Assert.That(ledger.ChangedKeys, Is.EqualTo(32));
            Assert.That(ledger.Evaluate(freshOrdinary, 8), Is.EqualTo(BlockPublicationAdmission.DeferKeysUntilNextFrame));
            ledger.BeginFrame(2);
            Assert.That(ledger.Evaluate(freshOrdinary, 8), Is.EqualTo(BlockPublicationAdmission.Ready));
            Assert.That(ledger.Evaluate(old, 32), Is.EqualTo(BlockPublicationAdmission.Ready));
        }

        [Test] public void CompleteKeysDeferToTheNextFrameWithoutDroppingAny()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(); ledger.BeginFrame(1);
            Assert.That(ledger.TryReserve(window, 20, out var first, out _), Is.True); using (first) first.Commit();
            Assert.That(ledger.TryReserve(window, 13, out var second, out var admission), Is.False);
            Assert.That(second, Is.Null); Assert.That(admission, Is.EqualTo(BlockPublicationAdmission.DeferKeysUntilNextFrame));
            Assert.That(ledger.ChangedKeys, Is.EqualTo(20)); Assert.That(ledger.Commits, Is.EqualTo(1));
            ledger.BeginFrame(2);
            Assert.That(ledger.TryReserve(window, 13, out second, out _), Is.True); using (second) second.Commit();
            Assert.That(ledger.ChangedKeys, Is.EqualTo(13)); Assert.That(ledger.Commits, Is.EqualTo(1));
        }

        [Test] public void ReservationIsExclusiveAndFrameCannotChangeWhileItIsActive()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(); ledger.BeginFrame(1);
            Assert.That(ledger.TryReserve(window, 8, out var active, out _), Is.True);
            Assert.That(ledger.TryReserve(window, 8, out var duplicate, out var admission), Is.False);
            Assert.That(duplicate, Is.Null); Assert.That(admission, Is.EqualTo(BlockPublicationAdmission.AnotherAtomicAdmissionActive));
            Assert.Throws<InvalidOperationException>(() => ledger.BeginFrame(2));
            Assert.DoesNotThrow(() => ledger.BeginFrame(1)); active.Dispose();
            Assert.DoesNotThrow(() => ledger.BeginFrame(2));
        }

        [Test] public void CancelledReservationDoesNotChargeKeysOrCommitTwice()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(); ledger.BeginFrame(1);
            Assert.That(ledger.TryReserve(window, 32, out var cancelled, out _), Is.True);
            cancelled.Dispose(); cancelled.Dispose();
            Assert.That(ledger.Commits, Is.Zero); Assert.That(ledger.ChangedKeys, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => cancelled.Commit());
            Assert.That(ledger.TryReserve(window, 32, out var accepted, out _), Is.True);
            accepted.Commit(); Assert.Throws<InvalidOperationException>(() => accepted.Commit());
            accepted.Dispose(); Assert.That(ledger.Commits, Is.EqualTo(1)); Assert.That(ledger.ChangedKeys, Is.EqualTo(32));
        }

        [Test] public void TimingScopeChargesRealElapsedAndCannotNestOrCrossFrames()
        {
            var ledger = new BlockPublicationFrameBudget(); var scope = ledger.MeasureSynchronousWork(3);
            AwaitRealElapsed(ledger, .05);
            Assert.Throws<InvalidOperationException>(() => ledger.MeasureSynchronousWork(3));
            Assert.Throws<InvalidOperationException>(() => ledger.BeginFrame(4));
            scope.Dispose(); double first = ledger.ObservedSynchronousMilliseconds; scope.Dispose();
            Assert.That(ledger.ObservedSynchronousMilliseconds, Is.EqualTo(first));
            using (ledger.MeasureSynchronousWork(3)) AwaitRealElapsed(ledger, first + .05);
            Assert.That(ledger.ObservedSynchronousMilliseconds, Is.GreaterThan(first));
            double peak = ledger.PeakObservedSynchronousMilliseconds;
            Assert.That(peak, Is.GreaterThanOrEqualTo(ledger.ObservedSynchronousMilliseconds));
            ledger.BeginFrame(4); Assert.That(ledger.ObservedSynchronousMilliseconds, Is.Zero);
            Assert.That(ledger.PeakObservedSynchronousMilliseconds, Is.EqualTo(peak));
        }

        [Test] public void ExceptionDisposalRetainsElapsedButReleasesBothScopes()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(); ledger.BeginFrame(1);
            try
            {
                using (ledger.MeasureSynchronousWork(1))
                {
                    Assert.That(ledger.TryReserve(window, 8, out var reservation, out _), Is.True);
                    using (reservation) { AwaitRealElapsed(ledger, .05); throw new InvalidOperationException("simulated native failure before publication"); }
                }
            }
            catch (InvalidOperationException exception) { Assert.That(exception.Message, Does.Contain("simulated native")); }
            Assert.That(ledger.ObservedSynchronousMilliseconds, Is.GreaterThanOrEqualTo(.05));
            Assert.That(ledger.ChangedKeys, Is.Zero); Assert.That(ledger.Commits, Is.Zero);
            Assert.That(ledger.Evaluate(window, 8), Is.EqualTo(BlockPublicationAdmission.Ready));
            Assert.DoesNotThrow(() => ledger.BeginFrame(2));
        }

        [Test] public void ExhaustedRealCpuAfterACommitDefersUntilAFreshFrame()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(.01); ledger.BeginFrame(1);
            Assert.That(ledger.TryReserve(window, 1, out var first, out _), Is.True); using (first) first.Commit();
            using (ledger.MeasureSynchronousWork(1)) AwaitRealElapsed(ledger, .05);
            Assert.That(ledger.Evaluate(window, 1), Is.EqualTo(BlockPublicationAdmission.DeferCpuUntilNextFrame));
            ledger.BeginFrame(2); Assert.That(ledger.Evaluate(window, 1), Is.EqualTo(BlockPublicationAdmission.Ready));
        }

        [Test] public void AnIndivisibleFirstOverrunMakesProgressAndIsExplicitlyRecorded()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(.01);
            using (ledger.MeasureSynchronousWork(1))
            {
                AwaitRealElapsed(ledger, .05);
                Assert.That(ledger.TryReserve(window, 1, out var reservation, out var admission), Is.True);
                Assert.That(admission, Is.EqualTo(BlockPublicationAdmission.ReadyWithObservedCpuOverrun));
                using (reservation) reservation.Commit();
                Assert.That(ledger.ObservedOverrunAdmissions, Is.EqualTo(1));
                Assert.That(ledger.Evaluate(window, 1), Is.EqualTo(BlockPublicationAdmission.DeferCpuUntilNextFrame));
            }
            ledger.BeginFrame(2); Assert.That(ledger.ObservedOverrunAdmissions, Is.EqualTo(1));
        }

        [Test] public void AReservationCannotThrowANewCpuRejectionAfterTheVisibleSwap()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(.01); ledger.BeginFrame(1);
            Assert.That(ledger.TryReserve(window, 8, out var reservation, out var admission), Is.True);
            Assert.That(admission, Is.EqualTo(BlockPublicationAdmission.Ready));
            using (reservation) using (ledger.MeasureSynchronousWork(1))
            {
                AwaitRealElapsed(ledger, .05); // actual elapsed incurred after admission
                Assert.DoesNotThrow(() => reservation.Commit());
            }
            Assert.That(ledger.ChangedKeys, Is.EqualTo(8)); Assert.That(ledger.Commits, Is.EqualTo(1));
            Assert.That(ledger.ObservedOverrunAdmissions, Is.EqualTo(1));
        }

        [Test] public void CpuProgressFallbackNeverBypassesACompleteKeyLimit()
        {
            var ledger = new BlockPublicationFrameBudget(); var window = Window(.01);
            using (ledger.MeasureSynchronousWork(1))
            {
                AwaitRealElapsed(ledger, .05);
                Assert.That(ledger.Evaluate(window, 33), Is.EqualTo(BlockPublicationAdmission.InvalidWindow));
            }
        }

        [Test] public void MetadataWithZeroKeysStillCountsAsAnAtomicCommit()
        {
            var policy = new BlockPublicationPolicy(1, 8, 8, 8, 8, 8, .01);
            var window = policy.Capture(BlockPublicationOperation.Metadata); var ledger = new BlockPublicationFrameBudget();
            using (ledger.MeasureSynchronousWork(1))
            {
                Assert.That(ledger.TryReserve(window, 0, out var reservation, out _), Is.True); using (reservation) reservation.Commit();
                AwaitRealElapsed(ledger, .05);
                Assert.That(ledger.Commits, Is.EqualTo(1)); Assert.That(ledger.ChangedKeys, Is.Zero);
                Assert.That(ledger.Evaluate(window, 0), Is.EqualTo(BlockPublicationAdmission.DeferCpuUntilNextFrame));
            }
        }

        static void AwaitRealElapsed(BlockPublicationFrameBudget ledger, double atLeastMilliseconds)
        {
            var bound = Stopwatch.StartNew();
            while (ledger.ObservedSynchronousMilliseconds < atLeastMilliseconds && bound.Elapsed.TotalMilliseconds < 1000)
                Thread.SpinWait(64);
            Assert.That(ledger.ObservedSynchronousMilliseconds, Is.GreaterThanOrEqualTo(atLeastMilliseconds),
                "real Stopwatch accounting failed to advance within a bounded wait");
        }
    }
}
#endif
