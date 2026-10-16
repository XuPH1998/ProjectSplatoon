using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockDependentCohortPreparationTests
    {
        const double Epoch = 10;
        const int Maximum = 1024;

        [Test]
        public void Fresh124PreparationKeepsPublishedBytesAndLeavesUnrelatedColumnShared()
        {
            var source = Chain(124, true); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            ulong root = Root(draft).Id; var unrelated = draft.Cohorts.Values.Single(c => c.Bottom.x == 2);
            ulong unrelatedRun = unrelated.MemberIds.Single();
            Assert.That(draft.EstimateSetCohortSupportWork(root) + BlockWorld.MaxCohortCells + 4, Is.EqualTo(1025));
            using var builder = draft.BeginBudgetedCohortEdit(root);
            Finish(builder, draft, () => CollectionAssert.AreEqual(before, source.EncodeSnapshot()));
            Assert.That(draft.Cohorts[unrelated.Id], Is.SameAs(source.Cohorts[unrelated.Id]));
            Assert.That(draft.Falling[unrelatedRun], Is.SameAs(source.Falling[unrelatedRun]));
            foreach (var cohort in draft.Cohorts.Values.Where(c => c.Bottom.x == 0))
            {
                Assert.That(cohort, Is.Not.SameAs(source.Cohorts[cohort.Id]));
                foreach (ulong run in cohort.MemberIds) Assert.That(draft.Falling[run], Is.Not.SameAs(source.Falling[run]));
            }
            int estimate = draft.EstimateSetCohortSupportWork(root);
            Assert.That(estimate + BlockWorld.MaxCohortCells + 4, Is.LessThanOrEqualTo(Maximum));
            draft.SetCohortSupport(root, 0, 0, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            draft.Cohorts[root].LandingOffset = .45f;
            foreach (double at in new[] { Epoch, Epoch + .1, Epoch + .5, Epoch + 5 })
            {
                float elapsed = (float)(at - Epoch), expected = Mathf.Min(.5f * BlockWorld.Gravity * elapsed * elapsed, .45f);
                foreach (var cohort in draft.Cohorts.Values.Where(c => c.Bottom.x == 0))
                    Assert.That(cohort.Offset(at), Is.EqualTo(expected).Within(2e-6f));
            }
            AssertGraph(draft); AssertCounts(source, draft);
            draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); AssertWire(source, draft);
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void CancelledPartialCopyCannotBePublishedSharedOrEditedAndNeverTouchesItsPublishedSource()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            using var builder = draft.BeginBudgetedCohortEdit(Root(draft).Id);
            builder.Step(draft, Maximum, out int work); Assert.That(work, Is.InRange(1, Maximum));
            Assert.That(builder.Ready, Is.False); builder.Cancel(); Assert.That(builder.Failed, Is.True);
            Assert.Throws<InvalidOperationException>(() => draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>()));
            Assert.Throws<InvalidOperationException>(() => draft.Clone());
            Assert.Throws<InvalidOperationException>(() => draft.BeginBudgetedOwnedClone());
            Assert.Throws<InvalidOperationException>(() => draft.EditCohort(Root(draft).Id, out _));
            Assert.That(builder.Step(draft, Maximum, out int after), Is.False); Assert.That(after, Is.Zero);
            CollectionAssert.AreEqual(before, source.EncodeSnapshot()); AssertCounts(source, draft);
        }

        [Test]
        public void DraftSemanticMutationRejectsItsOldBuilderBeforeTheNextAdmittedUnit()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            using var builder = draft.BeginBudgetedCohortEdit(Root(draft).Id);
            builder.Step(draft, Maximum, out _);
            Assert.That(draft.Set(new Vector3Int(3, 0, 0), BlockCell.Create(2)), Is.True);
            Assert.That(builder.Step(draft, Maximum, out int work), Is.False); Assert.That(work, Is.Zero);
            Assert.That(builder.Failed, Is.True);
            Assert.Throws<InvalidOperationException>(() => draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>()));
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void DifferentDraftCannotAdvanceOrConsumeAnotherOwnersPartialPreparation()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot(); var foreign = source.Clone(); var draft = source.Clone();
            using var builder = draft.BeginBudgetedCohortEdit(Root(draft).Id);
            builder.Step(draft, Maximum, out _);
            Assert.That(builder.Step(foreign, Maximum, out int work), Is.False); Assert.That(work, Is.Zero);
            Assert.That(builder.Failed, Is.True); Assert.That(foreign.EstimateSetCohortSupportWork(Root(foreign).Id) + 404, Is.EqualTo(1025));
            CollectionAssert.AreEqual(before, source.EncodeSnapshot()); CollectionAssert.AreEqual(before, foreign.EncodeSnapshot());
        }

        [Test]
        public void APreparedSharedPrefixOutsideTheClosureSurvivesTargetedReleaseAndDoesNotStrandTheReceipt()
        {
            var source = Chain(124, true); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            var top = Await(draft, draft.Cohorts.Values.Single(c => c.Bottom.x == 0 && c.Bottom.y == 123).Id);
            var unrelated = Await(draft, draft.Cohorts.Values.Single(c => c.Bottom.x == 2).Id);
            var motionField = Field("RebaseMotion"); Type nodeType = motionField.FieldType.GetElementType();
            var prefix = Array.CreateInstance(nodeType, 1);
            foreach (var cohort in new[] { top, unrelated })
            {
                cohort.RebasePending = true; motionField.SetValue(cohort, Array.CreateInstance(nodeType, 2));
                Field("RebasePrefix").SetValue(cohort, prefix);
                draft.PrepareCohortForPublication(cohort.Id, out _);
            }
            object unrelatedSample = Field("PreparedRebase").GetValue(unrelated);
            Assert.That(Prepared(draft), Does.Contain(top.Id)); Assert.That(Prepared(draft), Does.Contain(unrelated.Id));
            using var builder = draft.BeginBudgetedCohortEdit(Root(draft).Id);
            Finish(builder, draft, () => CollectionAssert.AreEqual(before, source.EncodeSnapshot()));
            Assert.That(Prepared(draft).Contains(top.Id), Is.False); Assert.That(Prepared(draft), Does.Contain(unrelated.Id));
            Assert.That(Field("PreparedRebase").GetValue(draft.Cohorts[unrelated.Id]), Is.SameAs(unrelatedSample));
            Assert.That(Field("PreparedRebase").GetValue(draft.Cohorts[top.Id]), Is.Null);
            Assert.That(draft.EstimateSetCohortSupportWork(Root(draft).Id) + 404, Is.EqualTo(407));
            long retainedWork = draft.EstimatedPreparedPublicationWork;
            draft.SetCohortSupport(Root(draft).Id, 0, 0, out int work); Assert.That(work, Is.EqualTo(3));
            Assert.That(Prepared(draft), Does.Contain(unrelated.Id)); Assert.That(draft.EstimatedPreparedPublicationWork, Is.EqualTo(retainedWork));
            AssertGraph(draft); AssertCounts(source, draft); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void NewPreparationAfterCompletionInvalidatesTheReceiptEvenWithoutASemanticMutation()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            var top = Await(draft, draft.Cohorts.Values.Single(c => c.Bottom.y == 123).Id);
            using var builder = draft.BeginBudgetedCohortEdit(Root(draft).Id); Finish(builder, draft);
            ulong mutation = draft.MutationGeneration;
            draft.PrepareCohortForPublication(top.Id, out _);
            Assert.That(draft.MutationGeneration, Is.EqualTo(mutation));
            Assert.That(builder.IsValid(draft), Is.False);
            Assert.That(draft.EstimateEditCohortWork(Root(draft).Id), Is.EqualTo(2 * 124 - 1));
            draft.EditCohort(Root(draft).Id, out int work); Assert.That(work, Is.EqualTo(2 * 124 - 1));
            Assert.That(Prepared(draft), Is.Empty); AssertGraph(draft); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void SemanticChangeAfterCompletionRevokesTheReceiptWithUnrelatedPreparedProofsIntact()
        {
            var source = Chain(124, true); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            var unrelated = Await(draft, draft.Cohorts.Values.Single(c => c.Bottom.x == 2).Id);
            draft.PrepareCohortForPublication(unrelated.Id, out _);
            using var builder = draft.BeginBudgetedCohortEdit(Root(draft).Id); Finish(builder, draft);
            Assert.That(draft.EstimateEditCohortWork(Root(draft).Id), Is.EqualTo(1));
            draft.Set(new Vector3Int(3, 0, 0), BlockCell.Create(2));
            Assert.That(builder.IsValid(draft), Is.False);
            Assert.That(draft.EstimateEditCohortWork(Root(draft).Id), Is.EqualTo(2 * 124 - 1));
            Assert.That(Prepared(draft), Does.Contain(unrelated.Id)); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void CompletedReceiptDoesNotAliasTheIndependentObjectsOfABudgetedOwnedClone()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            ulong root = Root(draft).Id;
            using var builder = draft.BeginBudgetedCohortEdit(root); Finish(builder, draft);
            byte[] prepared = draft.EncodeSnapshot();
            using var clone = draft.BeginBudgetedOwnedClone();
            for (int step = 0; !clone.Ready && step < 10000; step++)
            {
                clone.Step(draft, Maximum, out int work); Assert.That(work, Is.InRange(1, Maximum));
                Assert.That(clone.Failed, Is.False);
            }
            Assert.That(clone.Ready, Is.True); var independent = clone.TakeWorld();
            Assert.That(builder.IsValid(draft), Is.True);
            Assert.That(draft.EstimateSetCohortSupportWork(root), Is.EqualTo(3));
            foreach (var cohort in draft.Cohorts.Values)
            {
                Assert.That(independent.Cohorts[cohort.Id], Is.Not.SameAs(cohort));
                foreach (ulong member in cohort.MemberIds)
                    Assert.That(independent.Falling[member], Is.Not.SameAs(draft.Falling[member]));
            }
            AssertGraph(draft); AssertGraph(independent); CollectionAssert.AreEqual(prepared, independent.EncodeSnapshot());
            draft.SetCohortSupport(root, 0, 0, out int editWork); Assert.That(editWork, Is.EqualTo(3));
            draft.Cohorts[root].LandingOffset = .45f;
            Assert.That(builder.IsValid(draft), Is.False);
            Assert.That(draft.Cohorts[root].Offset(Epoch + 1), Is.EqualTo(.45f).Within(2e-6f));
            Assert.That(independent.Cohorts[root].Offset(Epoch + 1), Is.EqualTo(1f).Within(2e-6f));
            AssertCounts(source, draft); AssertCounts(source, independent);
            CollectionAssert.AreEqual(prepared, independent.EncodeSnapshot()); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        static void Finish(BlockCohortEditPreparation builder, BlockWorld draft, Action observe = null)
        {
            ulong mutation = draft.MutationGeneration;
            for (int step = 0; !builder.Ready && step < 10000; step++)
            {
                ulong progress = builder.ProgressVersion; builder.Step(draft, Maximum, out int work);
                Assert.That(work, Is.InRange(1, Maximum)); Assert.That(builder.ProgressVersion, Is.GreaterThan(progress));
                Assert.That(builder.ProcessedWork, Is.LessThanOrEqualTo(builder.MaximumWork));
                Assert.That(builder.Failed, Is.False); Assert.That(draft.MutationGeneration, Is.EqualTo(mutation)); observe?.Invoke();
            }
            Assert.That(builder.Ready, Is.True); Assert.That(builder.IsValid(draft), Is.True);
        }
        static BlockWorld Chain(int count, bool unrelated = false)
        {
            var world = new BlockWorld(new Vector2(8, 8), 7);
            for (int y = 0; y < count; y++) world.Set(new Vector3Int(0, y, 0), BlockCell.Create((byte)(1 + y % 2)));
            if (unrelated) world.Set(new Vector3Int(2, 10, 0), BlockCell.Create(2));
            world.Seal(1, world.RevisionKeys.ToArray()); ulong lower = 0;
            for (int y = 0; y < count; y++)
            {
                var group = world.BeginCohort(new Vector3Int(0, y, 0), 1); world.DetachCohortPiece(group.Id, 1, Epoch, out _);
                world.SetCohortSupport(group.Id, lower, 0, out _); group = world.Cohorts[group.Id];
                group.LandingKnown = true; group.LandingOffset = 1f + y * BlockWorld.CellSize; lower = group.Id;
            }
            if (unrelated)
            {
                var group = world.BeginCohort(new Vector3Int(2, 10, 0), 1); world.DetachCohortPiece(group.Id, 1, Epoch, out _);
                group.LandingKnown = true; group.LandingOffset = 1;
            }
            world.PreparePendingCohortsForPublication(out _); world.Seal(2, world.RevisionKeys.ToArray());
            world.ActivateRebasePendingCohorts(Epoch); return world;
        }
        static FallingCohort Root(BlockWorld world) => world.Cohorts.Values.Single(c => c.Bottom.x == 0 && c.Bottom.y == 0);
        static FallingCohort Await(BlockWorld world, ulong id)
        {
            var result = world.EditCohort(id, out _); result.Phase = FallingCohortPhase.AwaitActivation;
            result.LandingKnown = true; result.NeedsLandingProof = false;
            ((HashSet<ulong>)typeof(BlockWorld).GetField("_pendingCohortIds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world)).Add(id);
            return result;
        }
        static FieldInfo Field(string name) => typeof(FallingCohort).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        static HashSet<ulong> Prepared(BlockWorld world)
            => (HashSet<ulong>)typeof(BlockWorld).GetField("_preparedCohortIds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world);
        static void AssertGraph(BlockWorld world)
        {
            foreach (var cohort in world.Cohorts.Values)
            {
                if (cohort.LowerSupport != null) Assert.That(cohort.LowerSupport, Is.SameAs(world.Cohorts[cohort.LowerSupport.Id]));
                foreach (ulong member in cohort.MemberIds) Assert.That(world.Falling[member].Cohort, Is.SameAs(cohort));
            }
        }
        static void AssertCounts(BlockWorld source, BlockWorld current)
        { Assert.That(current.PinkCount, Is.EqualTo(source.PinkCount)); Assert.That(current.BlueCount, Is.EqualTo(source.BlueCount)); }
        static void AssertWire(BlockWorld source, BlockWorld current)
        {
            var snapshot = BlockWorld.DecodeSnapshot(current.EncodeSnapshot());
            var delta = BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(current.EncodeDelta(source.Revision));
            CollectionAssert.AreEqual(current.EncodeSnapshot(), snapshot.EncodeSnapshot());
            CollectionAssert.AreEqual(current.EncodeSnapshot(), delta.EncodeSnapshot()); AssertGraph(snapshot); AssertGraph(delta);
        }
    }
}
