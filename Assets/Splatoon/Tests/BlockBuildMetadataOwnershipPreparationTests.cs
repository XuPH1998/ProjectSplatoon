using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Reports-only candidate: not compiled or executed, and no terrain integration.
    public sealed class BlockBuildMetadataOwnershipPreparationTests
    {
        const double Epoch = 10, Publication = 10.1;
        const int Budget = 1024;

        [Test]
        public void CapAndOutsideContinuityWritersMatchOwnedOracleWithoutChangingSourceOrUnrelatedColumns()
        {
            var source = Chain(124, 3, out ulong root, out ulong continuity, out ulong legacy);
            byte[] frozen = source.EncodeSnapshot(); var draft = Staged(source);
            var staleRoot = draft.Cohorts[root]; var staleRun = draft.Falling[staleRoot.MemberIds[0]];
            var oracle = FullyOwned(draft);
            // Deliberately start above the cap root; a later lower copy must
            // repair every already-private upper, regardless of input ordering.
            ulong upper = draft.Cohorts.Values.Single(c => c.Bottom.x == 0 && c.Bottom.y == 60).Id;
            var targets = new[] { C(upper), C(root), C(continuity), L(legacy), C(root), L(legacy) };
            using var owner = new BlockBuildMetadataOwnershipPreparation(source, draft, targets, targets.Length);
            Finish(owner, source, draft, () => AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot()));
            Assert.That(owner.TakeWorld(source, draft), Is.SameAs(draft));
            Assert.That(draft.Cohorts[root], Is.Not.SameAs(staleRoot));
            Assert.That(draft.Falling[staleRun.Id], Is.Not.SameAs(staleRun));
            foreach (var group in source.Cohorts.Values)
            {
                bool affected = group.Bottom.x == 0 || group.Id == continuity;
                Assert.That(ReferenceEquals(draft.Cohorts[group.Id], group), Is.EqualTo(!affected));
                foreach (ulong id in group.MemberIds)
                {
                    Assert.That(ReferenceEquals(draft.Falling[id], source.Falling[id]), Is.EqualTo(!affected));
                    Assert.That(draft.Falling[id].Cells, Is.SameAs(source.Falling[id].Cells));
                }
            }
            Assert.That(draft.Falling[legacy], Is.Not.SameAs(source.Falling[legacy]));
            Assert.That(draft.Falling[legacy].Cells, Is.SameAs(source.Falling[legacy].Cells));
            ApplyWriters(source, draft, root, continuity, legacy);
            ApplyWriters(source, oracle, root, continuity, legacy);
            CollectionAssert.AreEqual(oracle.EncodeSnapshot(), draft.EncodeSnapshot());
            foreach (double at in new[] { Publication, Publication + .01, Publication + .1, Publication + 1, Publication + 10 })
                foreach (ulong id in source.Cohorts.Keys)
                {
                    Assert.That(draft.Cohorts[id].Offset(at), Is.EqualTo(oracle.Cohorts[id].Offset(at)).Within(2e-6f));
                    Assert.That(draft.Cohorts[id].Speed(at), Is.EqualTo(oracle.Cohorts[id].Speed(at)).Within(2e-6f));
                }
            Assert.That(draft.Cohorts[root].Offset(Publication + 10), Is.EqualTo(.45f).Within(2e-6f));
            Assert.That(draft.Falling[legacy].Offset(Publication + 10), Is.EqualTo(.4f).Within(2e-6f));
            AssertGraph(draft); AssertCounts(source, draft); AssertWire(source, draft);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            Assert.Throws<InvalidOperationException>(() => owner.TakeWorld(source, draft));
        }

        [Test]
        public void HistoricalAliasAndCellPayloadRemainCowAfterTheReadyResultIsEdited()
        {
            var source = AliasWorld(out ulong oldRun); byte[] frozen = source.EncodeSnapshot();
            Assert.That(source.TryGetFallingCell(oldRun, 2, out var endpoint, out int index), Is.True);
            var draft = Staged(source); ulong cohort = endpoint.Cohort.Id;
            using var owner = new BlockBuildMetadataOwnershipPreparation(source, draft, new[] { C(cohort) }, 1);
            Finish(owner, source, draft, () => AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot()));
            owner.TakeWorld(source, draft);
            Assert.That(draft.TryGetFallingCell(oldRun, 2, out var local, out int mapped), Is.True);
            Assert.That((local.Id, mapped), Is.EqualTo((endpoint.Id, index)));
            Assert.That(local, Is.Not.SameAs(endpoint)); Assert.That(local.Cells, Is.SameAs(endpoint.Cells));
            CollectionAssert.AreEquivalent(source.FallingTargetAliases, draft.FallingTargetAliases);
            AssertWire(source, draft);
            int teamCount = endpoint.Cells[index].Owner == 1 ? draft.PinkCount : draft.BlueCount;
            Assert.That(draft.RemoveFallingCell(oldRun, 2, out var removed), Is.True);
            Assert.That(local.Cells, Is.Not.SameAs(endpoint.Cells));
            Assert.That(endpoint.Cells[index].Occupied, Is.True);
            Assert.That(removed.Owner == 1 ? draft.PinkCount : draft.BlueCount, Is.EqualTo(teamCount - 1));
            Assert.That(source.TryGetFallingCell(oldRun, 2, out _, out _), Is.True);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot()); AssertGraph(draft);
        }

        [Test]
        public void CancellingInsideADependentCopyExposesNoResultAndKeepsPublishedBytes()
        {
            var source = Chain(124, 1, out ulong root, out _, out _); byte[] frozen = source.EncodeSnapshot();
            var draft = Staged(source);
            using var owner = new BlockBuildMetadataOwnershipPreparation(source, draft, new[] { C(root) }, 1);
            owner.Step(source, draft, Budget, out int work);
            Assert.That(work, Is.InRange(1, Budget)); Assert.That(owner.Ready, Is.False);
            owner.Cancel(); Assert.That(owner.Failed, Is.True);
            Assert.Throws<InvalidOperationException>(() => owner.TakeWorld(source, draft));
            Assert.Throws<InvalidOperationException>(() => draft.Clone());
            Assert.Throws<InvalidOperationException>(() => draft.BeginBudgetedOwnedClone());
            Assert.Throws<InvalidOperationException>(() => draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>()));
            Assert.That(owner.Step(source, draft, Budget, out int after), Is.False); Assert.That(after, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [Test]
        public void CancellingBetweenCompleteClosuresStillReturnsNoPartiallyOwnedDraft()
        {
            var source = Chain(124, 1, out ulong root, out _, out ulong legacy); byte[] frozen = source.EncodeSnapshot();
            var draft = Staged(source);
            using var owner = new BlockBuildMetadataOwnershipPreparation(source, draft, new[] { L(legacy), C(root) }, 2);
            owner.Step(source, draft, 8, out int work);
            Assert.That(work, Is.EqualTo(8)); Assert.That(owner.Ready, Is.False);
            Assert.That(draft.Falling[legacy], Is.Not.SameAs(source.Falling[legacy]));
            Assert.That(draft.Cohorts[root], Is.SameAs(source.Cohorts[root]));
            owner.Cancel();
            Assert.Throws<InvalidOperationException>(() => owner.TakeWorld(source, draft));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            // This graph is consistent between completed closures, so BlockWorld
            // itself is not locked here. The terrain adapter must discard draft;
            // it may publish only an owner.TakeWorld result after Ready.
        }

        [TestCase("source")]
        [TestCase("draft")]
        [TestCase("source-reference")]
        [TestCase("draft-reference")]
        public void ChangedWorldTokenCancelsBeforeAnotherAdmittedUnit(string change)
        {
            var source = Chain(124, 1, out ulong root, out _, out _); var draft = Staged(source);
            using var owner = new BlockBuildMetadataOwnershipPreparation(source, draft, new[] { C(root) }, 1);
            owner.Step(source, draft, Budget, out _);
            var currentSource = source; var currentDraft = draft;
            if (change == "source") source.Set(new Vector3Int(7, 1, 0), BlockCell.Create(1));
            if (change == "draft") draft.Set(new Vector3Int(7, 1, 0), BlockCell.Create(1));
            if (change == "source-reference") currentSource = BlockWorld.DecodeSnapshot(source.EncodeSnapshot());
            if (change == "draft-reference") currentDraft = BlockWorld.DecodeSnapshot(source.EncodeSnapshot());
            byte[] afterExternalChange = source.EncodeSnapshot(); ulong progress = owner.ProgressVersion;
            Assert.That(owner.Step(currentSource, currentDraft, Budget, out int work), Is.False);
            Assert.That(work, Is.Zero); Assert.That(owner.ProgressVersion, Is.EqualTo(progress));
            Assert.That(owner.Failed, Is.True);
            Assert.Throws<InvalidOperationException>(() => owner.TakeWorld(source, draft));
            CollectionAssert.AreEqual(afterExternalChange, source.EncodeSnapshot());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreparedActivationIsRejectedBeforeAnyRegistrationIsConsumed(bool legacy)
        {
            var source = Chain(2, 0, out _, out _, out _); var draft = Staged(source);
            var at = new Vector3Int(9, 20, 0); draft.Set(at, BlockCell.Create(2));
            ulong id;
            if (legacy)
            {
                var run = draft.Detach(at, 1, Epoch); id = run.Id;
                run.LandingKnown = true; run.LandingOffset = 1;
                draft.PrepareLegacyRunEpochsForPublication(out _);
            }
            else
            {
                var group = draft.BeginCohort(at, 1); id = group.Id;
                draft.DetachCohortPiece(id, 1, Epoch, out _);
                group.LandingKnown = true; group.LandingOffset = 1;
                draft.PrepareCohortForPublication(id, out _);
            }
            long prepared = draft.EstimatedPreparedPublicationWork; byte[] before = draft.EncodeSnapshot();
            Assert.That(prepared, Is.GreaterThan(0));
            Assert.Throws<ArgumentException>(() => new BlockBuildMetadataOwnershipPreparation(source, draft,
                new[] { new BlockBuildMetadataWriteTarget(id, !legacy) }, 1));
            Assert.That(draft.EstimatedPreparedPublicationWork, Is.EqualTo(prepared));
            CollectionAssert.AreEqual(before, draft.EncodeSnapshot());
            draft.ActivateRebasePendingCohorts(Publication);
            Assert.That(draft.EstimatedPreparedPublicationWork, Is.Zero);
            Assert.That(legacy ? draft.Falling[id].StartedAt : draft.Cohorts[id].StartedAt, Is.EqualTo(Publication));
            if (!legacy) Assert.That(draft.Cohorts[id].Phase, Is.EqualTo(FallingCohortPhase.Falling));
        }

        [TestCase(124)]
        [TestCase(400)]
        public void CompleteClosureUsesOnlyAdmittedUnitsAndNeverTouchesUnrelatedColumns(int count)
        {
            var small = Chain(count, 0, out ulong smallRoot, out _, out _);
            var large = Chain(count, 64, out ulong largeRoot, out _, out _);
            var smallDraft = Staged(small); var largeDraft = Staged(large);
            byte[] frozen = large.EncodeSnapshot();
            using var first = new BlockBuildMetadataOwnershipPreparation(small, smallDraft, new[] { C(smallRoot) }, 1);
            using var second = new BlockBuildMetadataOwnershipPreparation(large, largeDraft, new[] { C(largeRoot) }, 1);
            ulong progress = second.ProgressVersion;
            Assert.That(second.Step(large, largeDraft, second.EstimateNextStepWork() - 1, out int refused), Is.False);
            Assert.That(refused, Is.Zero); Assert.That(second.ProgressVersion, Is.EqualTo(progress));
            Finish(first, small, smallDraft);
            Finish(second, large, largeDraft, () => AssertSnapshotBytesEqual(frozen, large.EncodeSnapshot()));
            Assert.That(second.ProcessedWork, Is.EqualTo(first.ProcessedWork), "Logical closure work must not scan unrelated world metadata.");
            foreach (var group in large.Cohorts.Values.Where(c => c.Bottom.x != 0))
            {
                Assert.That(largeDraft.Cohorts[group.Id], Is.SameAs(group));
                foreach (ulong id in group.MemberIds) Assert.That(largeDraft.Falling[id], Is.SameAs(large.Falling[id]));
            }
            AssertGraph(largeDraft); AssertCounts(large, largeDraft); AssertWire(large, largeDraft);
        }

        static BlockBuildMetadataWriteTarget C(ulong id) => new(id, true);
        static BlockBuildMetadataWriteTarget L(ulong id) => new(id, false);
        static BlockWorld Staged(BlockWorld source)
        {
            var draft = source.Clone(); draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); return draft;
        }
        static void AssertSnapshotBytesEqual(byte[] expected, byte[] actual)
        {
            // Every successful observation compares the complete freshly encoded snapshot.
            // Only a mismatch needs NUnit's detailed collection comparison.
            bool equal = expected.Length == actual.Length;
            if (equal)
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != actual[i]) { equal = false; break; }
            if (!equal) CollectionAssert.AreEqual(expected, actual);
            Assert.That(equal, Is.True);
        }
        static void Finish(BlockBuildMetadataOwnershipPreparation owner, BlockWorld source, BlockWorld draft, Action observe = null)
        {
            for (int i = 0; !owner.Ready && i < 20000; i++)
            {
                int admission = owner.EstimateNextStepWork(); Assert.That(admission, Is.InRange(1, Budget));
                ulong progress = owner.ProgressVersion;
                owner.Step(source, draft, admission, out int work);
                Assert.That(work, Is.InRange(1, admission)); Assert.That(owner.Failed, Is.False);
                Assert.That(owner.ProgressVersion, Is.GreaterThan(progress));
                Assert.That(owner.ProcessedWork, Is.LessThanOrEqualTo(owner.MaximumWork)); observe?.Invoke();
            }
            Assert.That(owner.Ready, Is.True); Assert.That(owner.IsValid(source, draft), Is.True);
        }
        static BlockWorld FullyOwned(BlockWorld draft)
        {
            using var clone = draft.BeginBudgetedOwnedClone();
            for (int i = 0; !clone.Ready && i < 20000; i++) clone.Step(draft, Budget, out _);
            Assert.That(clone.Ready, Is.True); return clone.TakeWorld();
        }
        static void ApplyWriters(BlockWorld source, BlockWorld next, ulong root, ulong continuity, ulong legacy)
        {
            var cap = next.Cohorts[root]; var raw = source.Cohorts[root];
            cap.StartedAt = Publication; cap.InitialOffset = raw.Offset(Publication); cap.InitialSpeed = raw.Speed(Publication);
            cap.LandingKnown = true; cap.LandingOffset = .45f;
            var keep = next.Cohorts[continuity]; var original = source.Cohorts[continuity];
            keep.StartedAt = Publication; keep.InitialOffset = original.Offset(Publication); keep.InitialSpeed = original.Speed(Publication);
            var run = next.Falling[legacy]; var originalRun = source.Falling[legacy];
            run.StartedAt = Publication; run.InitialOffset = originalRun.Offset(Publication); run.InitialSpeed = originalRun.Speed(Publication);
            run.LandingKnown = true; run.LandingOffset = .4f;
        }
        static BlockWorld Chain(int count, int unrelatedCount, out ulong root, out ulong continuity, out ulong legacy)
        {
            var world = new BlockWorld(new Vector2(64, 64), 7); int bottom = count == 400 ? -80 : 0;
            ulong lower = 0; root = continuity = 0;
            for (int i = 0; i < count; i++)
            {
                var group = AddAwait(world, new Vector3Int(0, bottom + i, 0), 1, (byte)(1 + i % 2));
                world.SetCohortSupport(group.Id, lower, 0, out _); group = world.Cohorts[group.Id];
                group.LandingKnown = true; group.LandingOffset = (count == 400 ? 0 : 1) + i * BlockWorld.CellSize;
                if (i == 0) root = group.Id; lower = group.Id;
            }
            for (int i = 0; i < unrelatedCount; i++)
            {
                var group = AddAwait(world, new Vector3Int(i + 2, 20, 0), 1, 2);
                group.LandingKnown = true; group.LandingOffset = 1;
                if (i == 0) continuity = group.Id;
            }
            var cell = new Vector3Int(1, 20, 2);
            world.Set(cell, BlockCell.Create(1)); world.Set(cell + Vector3Int.up, BlockCell.Create(2));
            var run = world.Detach(cell, 2, Epoch); legacy = run.Id; run.LandingKnown = true; run.LandingOffset = 1;
            world.PreparePendingCohortsForPublication(out _); world.PrepareLegacyRunEpochsForPublication(out _);
            world.Seal(2, world.RevisionKeys.ToArray()); world.ActivateRebasePendingCohorts(Epoch); return world;
        }
        static FallingCohort AddAwait(BlockWorld world, Vector3Int at, int count, byte team)
        {
            for (int i = 0; i < count; i++) world.Set(at + Vector3Int.up * i, BlockCell.Create(team));
            var group = world.BeginCohort(at, count); world.DetachCohortPiece(group.Id, count, Epoch, out _); return group;
        }
        static BlockWorld AliasWorld(out ulong oldRun)
        {
            var world = new BlockWorld(new Vector2(64, 64), 7); var at = new Vector3Int(0, 20, 0);
            var parent = AddAwait(world, at, 3, 1); parent.LandingKnown = true; parent.LandingOffset = 1;
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            oldRun = parent.MemberIds.Single(); world.RemoveFallingCell(oldRun, 1, out _);
            var children = world.ReplaceCohort(parent.Id, new[]
            {
                new FallingCohortSpan(at, 21, new[] { new FallingCohortPiece(oldRun, 0, 1) }),
                new FallingCohortSpan(at + Vector3Int.up * 2, 23, new[] { new FallingCohortPiece(oldRun, 2, 1) })
            }, Epoch, out _);
            foreach (var child in children) { child.LandingKnown = true; child.LandingOffset = 2; child.NeedsLandingProof = false; }
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(2, world.RevisionKeys.ToArray()); return world;
        }
        static void AssertCounts(BlockWorld source, BlockWorld current)
        { Assert.That(current.PinkCount, Is.EqualTo(source.PinkCount)); Assert.That(current.BlueCount, Is.EqualTo(source.BlueCount)); }
        static void AssertGraph(BlockWorld world)
        {
            foreach (var group in world.Cohorts.Values)
            {
                if (group.LowerSupport != null) Assert.That(group.LowerSupport, Is.SameAs(world.Cohorts[group.LowerSupport.Id]));
                foreach (ulong id in group.MemberIds) Assert.That(world.Falling[id].Cohort, Is.SameAs(group));
            }
        }
        static void AssertWire(BlockWorld source, BlockWorld current)
        {
            var snapshot = BlockWorld.DecodeSnapshot(current.EncodeSnapshot());
            var delta = BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(current.EncodeDelta(source.Revision));
            CollectionAssert.AreEqual(current.EncodeSnapshot(), snapshot.EncodeSnapshot());
            CollectionAssert.AreEqual(current.EncodeSnapshot(), delta.EncodeSnapshot()); AssertGraph(snapshot); AssertGraph(delta);
        }
    }
}
