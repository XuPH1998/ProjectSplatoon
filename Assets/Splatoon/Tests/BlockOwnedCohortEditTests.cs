using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // World/COW/wire regression only. These tests do not certify the Terrain
    // preparation lease, native collision, throughput, or the network V2 cause.
    public sealed class BlockOwnedCohortEditTests
    {
        const double Epoch = 10;
        const int CommandGravityChecks = 1024;
        const int LandingTail = BlockWorld.MaxCohortCells + 4;

        [Test]
        public void Fresh124CellChainRetainsTheOversizedAdmissionCounterexample()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot();
            var draft = source.Clone(); ulong root = draft.Cohorts.Values.Single(c => c.Bottom.y == 0).Id;
            Assert.That(draft.EstimateSetCohortSupportWork(root) + LandingTail, Is.EqualTo(1025));
            Assert.That(draft.EstimateSetCohortSupportWork(root) + LandingTail, Is.GreaterThan(CommandGravityChecks),
                "The owned fast path does not close first-edit preparation and must not hide that remaining failure.");
            Assert.That(draft.Cohorts.Count, Is.EqualTo(124)); Assert.That(draft.Falling.Count, Is.EqualTo(124));
            Assert.That(draft.InBounds(new Vector3Int(0, 123, 0)), Is.True);
            Assert.That(BlockWorld.Center(new Vector3Int(0, 123, 0)).y, Is.LessThan(BlockWorld.Top));
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            CollectionAssert.AreEqual(before, BlockWorld.DecodeSnapshot(draft.EncodeSnapshot()).EncodeSnapshot());
        }

        [Test]
        public void Owned310CellChainChangesItsFloorWithinTheOriginalAdmissionAndPreservesOldMotion()
        {
            var source = Chain(310); byte[] before = source.EncodeSnapshot();
            var draft = Own(source); ulong root = draft.Cohorts.Values.Single(c => c.Bottom.y == 0).Id;
            Assert.That(2 * 310 + 405, Is.EqualTo(1025), "Captured old owned traversal plus landing-tail cost.");
            int admitted = draft.EstimateSetCohortSupportWork(root) + LandingTail;
            Assert.That(admitted, Is.LessThanOrEqualTo(CommandGravityChecks));
            for (int pass = 0; pass < 3; pass++)
            {
                ulong mutation = draft.MutationGeneration;
                int estimate = draft.EstimateSetCohortSupportWork(root);
                draft.SetCohortSupport(root, 0, 0, out int work);
                Assert.That(work, Is.LessThanOrEqualTo(estimate));
                Assert.That(work + LandingTail, Is.LessThanOrEqualTo(CommandGravityChecks));
                Assert.That(draft.MutationGeneration, Is.EqualTo(mutation + 1));
                draft.Cohorts[root].LandingKnown = true; draft.Cohorts[root].LandingOffset = .6f + pass * .1f;
                AssertGraph(draft);
                foreach (double at in new[] { Epoch, Epoch + .1, Epoch + .5, Epoch + 5 })
                {
                    float elapsed = (float)(at - Epoch);
                    float expected = Mathf.Min(.5f * BlockWorld.Gravity * elapsed * elapsed, .6f + pass * .1f);
                    foreach (var cohort in draft.Cohorts.Values)
                        Assert.That(cohort.Offset(at), Is.EqualTo(expected).Within(2e-6f));
                }
                CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            }
            Assert.That(draft.PinkCount, Is.EqualTo(source.PinkCount));
            Assert.That(draft.BlueCount, Is.EqualTo(source.BlueCount));
            draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>());
            AssertWire(source, draft); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void AFirstSharedEditStillCopiesEveryDependentDescriptorAndRunBeforeMutation()
        {
            var source = Chain(124); byte[] before = source.EncodeSnapshot(); var draft = source.Clone();
            ulong root = draft.Cohorts.Values.Single(c => c.Bottom.y == 0).Id;
            int estimate = draft.EstimateEditCohortWork(root);
            var edited = draft.EditCohort(root, out int work);
            Assert.That(estimate, Is.EqualTo(5 * 124 - 1)); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            foreach (var pair in source.Cohorts)
            {
                Assert.That(draft.Cohorts[pair.Key], Is.Not.SameAs(pair.Value));
                foreach (ulong member in pair.Value.MemberIds)
                    Assert.That(draft.Falling[member], Is.Not.SameAs(source.Falling[member]));
            }
            edited.LandingOffset = .4f; AssertGraph(draft);
            Assert.That(draft.Cohorts.Values.Single(c => c.Bottom.y == 123).Offset(11), Is.EqualTo(.4f).Within(2e-6f));
            Assert.That(source.Cohorts.Values.Single(c => c.Bottom.y == 123).Offset(11), Is.EqualTo(1f).Within(2e-6f));
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void PreparedUpperStillForcesDependentInvalidationEvenWhenTheRootIsOwned()
        {
            var source = Chain(3); byte[] before = source.EncodeSnapshot(); var draft = Own(source);
            ulong root = draft.Cohorts.Values.Single(c => c.Bottom.y == 0).Id;
            var upper = draft.EditCohort(draft.Cohorts.Values.Single(c => c.Bottom.y == 2).Id, out _);
            upper.Phase = FallingCohortPhase.AwaitActivation; upper.LandingKnown = true; upper.NeedsLandingProof = false;
            Ids(draft, "_pendingCohortIds").Add(upper.Id);
            draft.PrepareCohortForPublication(upper.Id, out _);
            Assert.That(Ids(draft, "_preparedCohortIds"), Does.Contain(upper.Id));
            Assert.That(Ids(draft, "_ownedCohorts"), Does.Contain(root));
            int estimate = draft.EstimateEditCohortWork(root);
            Assert.That(estimate, Is.EqualTo(5), "Three owned descriptors plus two lower-link repairs remain required.");
            draft.EditCohort(root, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            Assert.That(Ids(draft, "_preparedCohortIds"), Is.Empty);
            AssertGraph(draft); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void RemovingAnOwnedLowerKeepsSurvivorAddressesAndRetainsThePublishedSupportBound()
        {
            var source = Chain(3); byte[] before = source.EncodeSnapshot(); var draft = Own(source);
            var oldLower = source.Cohorts.Values.Single(c => c.Bottom.y == 0);
            var firstUpper = draft.Cohorts.Values.Single(c => c.Bottom.y == 1);
            var top = draft.Cohorts.Values.Single(c => c.Bottom.y == 2);
            ulong lowRun = oldLower.MemberIds.Single(); ulong upperRun = firstUpper.MemberIds.Single();
            int estimate = draft.EstimateRemoveFallingCellWork(lowRun, 0);
            Assert.That(draft.RemoveFallingCell(lowRun, 0, out var removed, out int work), Is.True);
            Assert.That(work, Is.LessThanOrEqualTo(estimate)); Assert.That(removed.Owner, Is.EqualTo(1));
            Assert.That(draft.Cohorts.ContainsKey(oldLower.Id), Is.False);
            Assert.That(firstUpper.LowerSupport, Is.Null); Assert.That(firstUpper.NeedsLandingProof, Is.True);
            Assert.That(top.LowerSupport, Is.SameAs(firstUpper));
            Assert.That(draft.TryGetFallingCell(upperRun, 0, out var survivor, out int index), Is.True);
            Assert.That(survivor.Id, Is.EqualTo(upperRun)); Assert.That(index, Is.Zero);
            foreach (double at in new[] { Epoch, Epoch + .1, Epoch + .5, Epoch + 5 })
                Assert.That(firstUpper.Offset(at), Is.EqualTo(source.Cohorts[firstUpper.Id].Offset(at)).Within(2e-6f));
            Assert.That(draft.PinkCount, Is.EqualTo(source.PinkCount - 1)); Assert.That(draft.BlueCount, Is.EqualTo(source.BlueCount));
            draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>());
            AssertWire(source, draft); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [Test]
        public void AnotherShallowCloneRevokesTheOwnedShortcutAndProtectsBothPriorWorlds()
        {
            var source = Chain(124); byte[] sourceBefore = source.EncodeSnapshot(); var owned = Own(source);
            byte[] ownedBefore = owned.EncodeSnapshot(); var next = owned.Clone();
            ulong root = next.Cohorts.Values.Single(c => c.Bottom.y == 0).Id;
            Assert.That(Ids(owned, "_ownedCohorts"), Is.Empty); Assert.That(Ids(next, "_ownedCohorts"), Is.Empty);
            Assert.That(next.EstimateSetCohortSupportWork(root) + LandingTail, Is.EqualTo(1025));
            next.EditCohort(root, out _).LandingOffset = .3f; AssertGraph(next);
            CollectionAssert.AreEqual(sourceBefore, source.EncodeSnapshot());
            CollectionAssert.AreEqual(ownedBefore, owned.EncodeSnapshot());
        }

        static BlockWorld Chain(int count)
        {
            var world = new BlockWorld(new Vector2(8, 8), 7);
            for (int y = 0; y < count; y++)
                Assert.That(world.Set(new Vector3Int(0, y, 0), BlockCell.Create((byte)(1 + y % 2))), Is.True);
            world.Seal(1, world.Chunks.Keys.ToArray()); ulong previous = 0;
            for (int y = 0; y < count; y++)
            {
                var group = world.BeginCohort(new Vector3Int(0, y, 0), 1);
                world.DetachCohortPiece(group.Id, 1, Epoch, out _);
                world.SetCohortSupport(group.Id, previous, 0, out _);
                group = world.Cohorts[group.Id]; group.LandingKnown = true; group.LandingOffset = 1f + y * BlockWorld.CellSize;
                group.NeedsLandingProof = false; previous = group.Id;
            }
            // Modeled caps are World-test setup, not native support acceptance.
            world.PreparePendingCohortsForPublication(out _);
            world.Seal(2, world.RevisionKeys.ToArray()); world.ActivateRebasePendingCohorts(Epoch);
            AssertGraph(world); Assert.That(world.PinkCount + world.BlueCount, Is.EqualTo(count));
            CollectionAssert.AreEqual(world.EncodeSnapshot(), BlockWorld.DecodeSnapshot(world.EncodeSnapshot()).EncodeSnapshot());
            return world;
        }
        static BlockWorld Own(BlockWorld source)
        {
            byte[] before = source.EncodeSnapshot(); using var builder = source.BeginBudgetedOwnedClone();
            for (int step = 0; !builder.Ready && step < 10000; step++)
            {
                ulong progress = builder.ProgressVersion; builder.Step(source, CommandGravityChecks, out int work);
                Assert.That(work, Is.InRange(1, CommandGravityChecks)); Assert.That(builder.ProgressVersion, Is.GreaterThan(progress));
                Assert.That(builder.Failed, Is.False); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            }
            Assert.That(builder.Ready, Is.True); var result = builder.TakeWorld();
            Assert.That(Ids(result, "_ownedCohorts").Count, Is.EqualTo(result.Cohorts.Count)); AssertGraph(result); return result;
        }
        static HashSet<ulong> Ids(BlockWorld world, string field)
            => (HashSet<ulong>)typeof(BlockWorld).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world);
        static void AssertGraph(BlockWorld world)
        {
            foreach (var cohort in world.Cohorts.Values)
            {
                if (cohort.LowerSupport != null) Assert.That(cohort.LowerSupport, Is.SameAs(world.Cohorts[cohort.LowerSupport.Id]));
                foreach (ulong member in cohort.MemberIds) Assert.That(world.Falling[member].Cohort, Is.SameAs(cohort));
            }
        }
        static void AssertWire(BlockWorld source, BlockWorld current)
        {
            var snapshot = BlockWorld.DecodeSnapshot(current.EncodeSnapshot());
            var delta = BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(current.EncodeDelta(source.Revision));
            CollectionAssert.AreEqual(current.EncodeSnapshot(), snapshot.EncodeSnapshot());
            CollectionAssert.AreEqual(current.EncodeSnapshot(), delta.EncodeSnapshot());
            AssertGraph(snapshot); AssertGraph(delta);
        }
    }
}
