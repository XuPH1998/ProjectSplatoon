using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Splatoon.Blocks;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    /// <summary>Pure world/wire contracts. No Editor, Player, mesh or timing acceptance is implied.</summary>
    public sealed class BlockCohortWorldWireTests
    {
        HistoricalBlockWorldHeightScope _historicalHeight;
        [SetUp] public void UseHistoricalWorldHeight() => _historicalHeight = new HistoricalBlockWorldHeightScope();
        [TearDown] public void RestoreWorldHeight() { _historicalHeight?.Dispose(); _historicalHeight = null; }

        static BlockWorld Seed(Vector3Int bottom, int count)
        {
            var world = new BlockWorld(new Vector2(8, 8), 7);
            for (int i = 0; i < count; i++) Assert.That(world.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)(1 + i % 2))), Is.True);
            world.Seal(1, world.Chunks.Keys.ToArray()); return world;
        }
        static FallingCohort FullCohort(BlockWorld world, Vector3Int bottom, int count, double stage, double publish)
        {
            var cohort = world.BeginCohort(bottom, count);
            while (world.Cohorts[cohort.Id].DetachCursor < cohort.TopExclusive)
            {
                int piece = Math.Min(16, cohort.TopExclusive - world.Cohorts[cohort.Id].DetachCursor);
                int estimate = world.EstimateDetachCohortPieceWork(cohort.Id, piece);
                world.DetachCohortPiece(cohort.Id, piece, stage, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            }
            Publish(world, publish);
            // The initial activation is legal and has a finite modeled floor. Historical World-only
            // fixtures then invalidate that proof to exercise already-moving lost-support state;
            // this does not authorize an unknown landing cap on any new natural activation.
            var active = world.EditCohort(cohort.Id, out _);
            active.LandingKnown = false; active.LandingOffset = 0; active.NeedsLandingProof = true;
            return active;
        }
        static void AdmitModeledLanding(BlockWorld world, ulong id, float landingOffset = 64f)
        {
            var cohort = world.EditCohort(id, out _);
            cohort.LandingKnown = true; cohort.LandingOffset = landingOffset; cohort.NeedsLandingProof = false;
        }
        static void Publish(BlockWorld world, double now)
        {
            // Pure World fixtures supply a finite modeled floor before the real preparation gate.
            // Detaching and already-moving descriptors are not certified by this helper.
            foreach (ulong id in world.Cohorts.Values.Where(c => c.Phase == FallingCohortPhase.AwaitActivation
                && (!c.LandingKnown || c.NeedsLandingProof)).Select(c => c.Id).ToArray()) AdmitModeledLanding(world, id);
            int estimate = world.EstimatePreparePendingCohortsWork(); world.PreparePendingCohortsForPublication(out int work);
            Assert.That(work, Is.LessThanOrEqualTo(estimate));
            world.Seal(world.Revision + 1, world.RevisionKeys.ToArray()); world.ActivateRebasePendingCohorts(now);
        }
        static FallingCohortSpan Span(FallingRun run, int first, int count)
            => new(run.Bottom + Vector3Int.up * first, run.Bottom.y + first + count, new[] { new FallingCohortPiece(run.Id, first, count) });
        static IReadOnlyList<FallingCohort> Fracture(BlockWorld world, FallingCohort cohort, IReadOnlyList<FallingCohortSpan> spans, double now)
        {
            int estimate = world.EstimateBeginCohortReplacementWork(cohort.Id, spans);
            var builder = world.BeginCohortReplacement(cohort.Id, spans, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            while (!builder.Ready)
            { builder.Step(world, 31, out int step); Assert.That(step, Is.LessThanOrEqualTo(31)); Assert.That(builder.Failed, Is.False); Assert.That(step, Is.GreaterThan(0)); }
            estimate = world.EstimateCommitCohortReplacementWork(builder);
            var children = world.CommitCohortReplacement(builder, now, out work); Assert.That(work, Is.LessThanOrEqualTo(estimate)); return children;
        }
        static void Remove(BlockWorld world, ulong id, int index)
        {
            int estimate = world.EstimateRemoveFallingCellWork(id, index);
            Assert.That(world.RemoveFallingCell(id, index, out _, out int work), Is.True); Assert.That(work, Is.LessThanOrEqualTo(estimate));
        }

        [Test]
        public void FinalPublicationActivatesEveryStoragePieceAtOneEpochAndProjectsAllMotionFields()
        {
            var bottom = new Vector3Int(-1, 40, -1); var source = Seed(bottom, 33); var next = source.Clone();
            var cohort = next.BeginCohort(bottom, 33);
            var low = next.DetachCohortPiece(cohort.Id, 16, 10, out _); Publish(next, 11);
            Assert.That(low.Offset(100), Is.Zero); Assert.That(cohort.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(next.TryGet(bottom + Vector3Int.up * 16, out _), Is.True);
            var publishedDormant = next.Clone(); var staging = next.Clone();
            staging.DetachCohortPiece(cohort.Id, 16, 12, out _); var high = staging.DetachCohortPiece(cohort.Id, 1, 13, out _);
            Assert.That(high.Offset(100), Is.Zero); Assert.That(staging.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation));
            Publish(staging, 15);
            foreach (var run in staging.Falling.Values)
            {
                Assert.That(run.Cohort, Is.SameAs(staging.Cohorts[cohort.Id])); Assert.That(run.StartedAt, Is.EqualTo(15));
                Assert.That(run.InitialOffset, Is.Zero); Assert.That(run.InitialSpeed, Is.Zero);
                Assert.That(run.Offset(15.25), Is.EqualTo(.5f * BlockWorld.Gravity * .25f * .25f).Within(.00001f));
                Assert.That(run.Speed(15.25), Is.EqualTo(BlockWorld.Gravity * .25f).Within(.00001f));
            }
            var edited = staging.EditFalling(low.Id); edited.LandingOffset = .2f; edited.LandingKnown = true; edited.InitialSpeed = 1;
            foreach (var run in staging.Falling.Values)
            { Assert.That(run.LandingKnown, Is.True); Assert.That(run.LandingOffset, Is.EqualTo(.2f)); Assert.That(run.InitialSpeed, Is.EqualTo(1)); }
            Assert.That(publishedDormant.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(publishedDormant.Falling[low.Id].Offset(100), Is.Zero);
            Assert.That(source.Falling, Is.Empty); Assert.That(source.PinkCount + source.BlueCount, Is.EqualTo(33));
        }

        [Test]
        public void CohortAndLegacyMotionCellsAndCountsRemainIsolatedInBothCopyDirections()
        {
            var bottom = new Vector3Int(0, 40, 0); var original = Seed(bottom, 18);
            var grouped = FullCohort(original, bottom, 17, 1, 2); var legacy = original.Detach(bottom + Vector3Int.up * 17, 1, 2);
            var snapshot = original.Clone(); var run = original.Falling[grouped.MemberIds[0]];
            snapshot.EditFalling(run.Id).LandingKnown = true; snapshot.EditFalling(run.Id).LandingOffset = .3f;
            Assert.That(original.Falling[run.Id].LandingKnown, Is.False); Assert.That(original.Cohorts[grouped.Id].LandingKnown, Is.False);
            original.EditFalling(run.Id).InitialSpeed = 2; Assert.That(snapshot.Falling[run.Id].InitialSpeed, Is.Zero);
            original.EditFalling(legacy.Id).StartedAt = 9; Assert.That(snapshot.Falling[legacy.Id].StartedAt, Is.EqualTo(2));
            snapshot.EditFalling(legacy.Id).InitialSpeed = 4; Assert.That(original.Falling[legacy.Id].InitialSpeed, Is.Zero);
            Remove(snapshot, run.Id, 0); Assert.That(original.Falling[run.Id].Cells[0].Occupied, Is.True);
            Assert.That(original.PinkCount + original.BlueCount, Is.EqualTo(18)); Assert.That(snapshot.PinkCount + snapshot.BlueCount, Is.EqualTo(17));
            Assert.That(original.Cohorts[grouped.Id].NeedsRegroup, Is.False); Assert.That(snapshot.Cohorts[grouped.Id].NeedsRegroup, Is.True);
        }

        [Test]
        public void DynamicLowerSupportCapsUpperMotionAndCopyOnWriteRebindsTheWholeSupportChain()
        {
            var world = Seed(new Vector3Int(0, 40, 0), 5);
            var lower = FullCohort(world, new Vector3Int(0, 40, 0), 1, 0, 10);
            var middle = FullCohort(world, new Vector3Int(0, 42, 0), 1, 0, 0);
            var upper = FullCohort(world, new Vector3Int(0, 44, 0), 1, 0, 0);
            world.SetCohortSupport(middle.Id, lower.Id, .1f, out _); world.SetCohortSupport(upper.Id, middle.Id, .1f, out _);
            Assert.That(world.Cohorts[upper.Id].Offset(10.1), Is.EqualTo(world.Cohorts[lower.Id].Offset(10.1) + .2f).Within(.00001f));
            Assert.That(world.Cohorts[upper.Id].Speed(10.1), Is.EqualTo(world.Cohorts[lower.Id].Speed(10.1)).Within(.00001f));
            var snapshot = world.Clone(); int estimate = world.EstimateEditCohortWork(lower.Id);
            var changed = world.EditCohort(lower.Id, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate)); changed.LandingKnown = true; changed.LandingOffset = .05f;
            Assert.That(world.Cohorts[middle.Id].LowerSupport, Is.SameAs(world.Cohorts[lower.Id]));
            Assert.That(world.Cohorts[upper.Id].LowerSupport, Is.SameAs(world.Cohorts[middle.Id]));
            Assert.That(snapshot.Cohorts[upper.Id].LowerSupport, Is.SameAs(snapshot.Cohorts[middle.Id]));
            Assert.That(snapshot.Cohorts[lower.Id].LandingKnown, Is.False);
            Assert.That(world.Cohorts[upper.Id].Offset(12), Is.EqualTo(.25f).Within(.00001f)); Assert.That(world.Cohorts[upper.Id].Speed(12), Is.Zero);
            Assert.That(snapshot.Cohorts[upper.Id].Offset(12), Is.GreaterThan(.25f));
            Remove(world, lower.MemberIds[0], 0);
            Assert.That(world.Cohorts[middle.Id].LowerSupport, Is.Null); Assert.That(snapshot.Cohorts[middle.Id].LowerSupport, Is.Not.Null);
            Assert.That(world.Cohorts[middle.Id].NeedsLandingProof, Is.True);
            Assert.That(world.Cohorts[upper.Id].Offset(12), Is.EqualTo(.25f).Within(.00001f), "removed lower support stays conservative until a budgeted proof replaces it");
            world.SetCohortSupport(middle.Id, 0, 0, out _);
            Assert.That(world.Cohorts[middle.Id].NeedsLandingProof, Is.False); Assert.That(world.Cohorts[upper.Id].Offset(12), Is.GreaterThan(.25f));
        }

        [Test]
        public void FractureSamplesActualPublicationPositionVelocityAndPreservesFrozenTargetIdentity()
        {
            var bottom = new Vector3Int(0, 80, 0); var world = Seed(bottom, 16); var parent = FullCohort(world, bottom, 16, 9, 10);
            var run = world.Falling[parent.MemberIds[0]]; var committed = world.Clone(); Remove(world, run.Id, 5);
            var children = Fracture(world, world.Cohorts[parent.Id], new[] { Span(run, 0, 5), Span(run, 6, 10) }, 10.1);
            Assert.That(children[0].MemberIds[0], Is.EqualTo(run.Id)); Assert.That(children[1].MemberIds[0], Is.GreaterThan(run.Id));
            Publish(world, 10.3); float expectedOffset = committed.Cohorts[parent.Id].Offset(10.3), expectedSpeed = committed.Cohorts[parent.Id].Speed(10.3);
            foreach (var child in children)
            { Assert.That(child.InitialOffset, Is.EqualTo(expectedOffset).Within(.00001f)); Assert.That(child.InitialSpeed, Is.EqualTo(expectedSpeed).Within(.00001f)); Assert.That(child.StartedAt, Is.EqualTo(10.3)); }
            Assert.That(world.TryGetFallingCell(run.Id, 10, out var fragment, out int index, out int work), Is.True);
            Assert.That(index, Is.EqualTo(10)); Assert.That(fragment.Bottom, Is.EqualTo(run.Bottom)); Assert.That(fragment.Cells.Length, Is.EqualTo(16));
            Assert.That(fragment.Cells[0].Occupied, Is.False); Assert.That(work, Is.EqualTo(2));
            Remove(world, run.Id, 10); Assert.That(committed.Falling[run.Id].Cells[10].Occupied, Is.True);
            Assert.That(world.PinkCount + world.BlueCount, Is.EqualTo(14)); Assert.That(committed.PinkCount + committed.BlueCount, Is.EqualTo(16));
        }

        [Test]
        public void RepeatedFracturesResolveOldIndexThroughAliasChainsAndDoNotDeleteHistoricalTombstones()
        {
            var world = Seed(new Vector3Int(0, 80, 0), 16); var parent = FullCohort(world, new Vector3Int(0, 80, 0), 16, 0, 1);
            var old = world.Falling[parent.MemberIds[0]]; Remove(world, old.Id, 5);
            var children = Fracture(world, world.Cohorts[parent.Id], new[] { Span(old, 0, 5), Span(old, 6, 10) }, 1.1); Publish(world, 1.2);
            var high = world.Falling[children[1].MemberIds[0]]; Remove(world, old.Id, 8);
            Fracture(world, world.Cohorts[high.Cohort.Id], new[] { Span(high, 6, 2), Span(high, 9, 7) }, 1.3); Publish(world, 1.4);
            Assert.That(world.TryGetFallingCell(old.Id, 10, out var newest, out int index, out int work), Is.True);
            Assert.That(newest.Id, Is.GreaterThan(high.Id)); Assert.That(index, Is.EqualTo(10)); Assert.That(work, Is.EqualTo(2), "reverse origins are rewritten directly rather than accumulating chains");
            var roundTrip = BlockWorld.DecodeSnapshot(world.EncodeSnapshot());
            Assert.That(roundTrip.TryGetFallingCell(old.Id, 10, out var decoded, out int decodedIndex), Is.True); Assert.That(decoded.Id, Is.EqualTo(newest.Id)); Assert.That(decodedIndex, Is.EqualTo(10));
            Remove(roundTrip, old.Id, 10); Assert.That(roundTrip.FallingTargetAliases.Count, Is.EqualTo(world.FallingTargetAliases.Count));
            Assert.That(roundTrip.TryGetFallingCell(old.Id, 10, out _, out _), Is.False); Assert.That(world.TryGetFallingCell(old.Id, 10, out _, out _), Is.True);
        }

        [Test]
        public void FractureRebasesClampedAndSupportConstrainedParentsWithZeroOrLowerInstantaneousVelocity()
        {
            var world = Seed(new Vector3Int(0, 60, 0), 33); var lower = FullCohort(world, new Vector3Int(0, 60, 0), 1, 0, 10);
            var upper = FullCohort(world, new Vector3Int(0, 62, 0), 16, 0, 1);
            world.EditCohort(lower.Id, out _).LandingKnown = true; world.EditCohort(lower.Id, out _).LandingOffset = .05f;
            world.SetCohortSupport(upper.Id, lower.Id, .1f, out _); var committed = world.Clone(); var run = world.Falling[upper.MemberIds[0]];
            Remove(world, run.Id, 5); var children = Fracture(world, world.Cohorts[upper.Id], new[] { Span(run, 0, 5), Span(run, 6, 10) }, 10.05);
            var decoded = BlockWorld.DecodeSnapshot(world.EncodeSnapshot()); Publish(world, 10.5); Publish(decoded, 10.5);
            foreach (var child in children)
            {
                Assert.That(child.InitialOffset, Is.EqualTo(committed.Cohorts[upper.Id].Offset(10.5)).Within(.00001f)); Assert.That(child.InitialOffset, Is.EqualTo(.15f).Within(.00001f));
                Assert.That(child.InitialSpeed, Is.Zero); Assert.That(decoded.Cohorts[child.Id].InitialOffset, Is.EqualTo(child.InitialOffset)); Assert.That(decoded.Cohorts[child.Id].InitialSpeed, Is.Zero);
            }
        }

        [Test]
        public void FractureDuringBarrierRetainsAnUnDetachedStaticTailUntilItsOwnFinalPublication()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 32); var parent = world.BeginCohort(bottom, 32);
            var run = world.DetachCohortPiece(parent.Id, 8, 1, out _); Publish(world, 2); Remove(world, run.Id, 3);
            var spans = new[] { Span(run, 0, 3), new FallingCohortSpan(bottom + Vector3Int.up * 4, bottom.y + 32, bottom.y + 8, new[] { new FallingCohortPiece(run.Id, 4, 4) }) };
            var children = Fracture(world, world.Cohorts[parent.Id], spans, 3); Publish(world, 4);
            Assert.That(children[0].Phase, Is.EqualTo(FallingCohortPhase.Falling)); Assert.That(children[1].Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(children[1].Offset(100), Is.Zero); Assert.That(world.TryGet(bottom + Vector3Int.up * 8, out _), Is.True);
            world.DetachCohortPiece(children[1].Id, 16, 5, out _); world.DetachCohortPiece(children[1].Id, 8, 6, out _); Publish(world, 7);
            Assert.That(world.Cohorts[children[1].Id].Phase, Is.EqualTo(FallingCohortPhase.Falling)); Assert.That(world.Cohorts[children[1].Id].StartedAt, Is.EqualTo(7));
            Assert.That(world.PinkCount + world.BlueCount, Is.EqualTo(31));
        }

        [Test]
        public void TrimmedTailUsesTheReadyQueueAndActivationCowsPublishedDormantMotion()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 32); var cohort = world.BeginCohort(bottom, 32);
            world.DetachCohortPiece(cohort.Id, 16, 1, out _); Publish(world, 2); var snapshot = world.Clone();
            int estimate = world.EstimateFinalizeCohortDetachmentWork(cohort.Id); world.FinishCohortDetachment(cohort.Id, bottom.y + 16, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            Publish(world, 8); Assert.That(world.Cohorts[cohort.Id].StartedAt, Is.EqualTo(8)); Assert.That(world.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.Falling));
            Assert.That(snapshot.Cohorts[cohort.Id].TopExclusive, Is.EqualTo(bottom.y + 32)); Assert.That(snapshot.Cohorts[cohort.Id].Offset(100), Is.Zero);
            Assert.That(world.TryGet(bottom + Vector3Int.up * 16, out _), Is.True);
            var empty = world.BeginCohort(new Vector3Int(1, 40, 0), 1); world.FinishCohortDetachment(empty.Id, 40, out _);
            Assert.That(world.Cohorts.ContainsKey(empty.Id), Is.False);
        }

        [Test]
        public void FourHundredCellAlternatingDamageBuildsIncrementallyAndCommitsBelowTheUnchangedWorkLimit()
        {
            var bottom = new Vector3Int(0, -80, 0); var world = Seed(bottom, 400); var parent = FullCohort(world, bottom, 400, 0, 1);
            var ids = parent.MemberIds.ToArray();
            foreach (ulong id in ids)
            {
                var run = world.Falling[id]; for (int i = 0; i < run.Cells.Length; i++) if (((run.Bottom.y - bottom.y + i) & 1) != 0) Remove(world, id, i);
            }
            var spans = new List<FallingCohortSpan>();
            foreach (ulong id in ids)
                if (world.Falling.TryGetValue(id, out var run)) for (int i = 0; i < run.Cells.Length; i++) if (run.Cells[i].Occupied) spans.Add(Span(run, i, 1));
            spans.Sort((a, b) => a.Bottom.y.CompareTo(b.Bottom.y));
            byte[] before = world.EncodeSnapshot(); int estimate = world.EstimateBeginCohortReplacementWork(parent.Id, spans);
            var builder = world.BeginCohortReplacement(parent.Id, spans, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            builder.Step(world, 53, out work); Assert.That(work, Is.LessThanOrEqualTo(53)); var journal = builder.Clone(out int cloneWork);
            Assert.That(cloneWork, Is.LessThanOrEqualTo(builder.EstimateCloneWork()));
            while (!builder.Ready)
            { Assert.That(builder.EstimateNextStepWork(), Is.LessThanOrEqualTo(17)); builder.Step(world, 53, out work); Assert.That(work, Is.InRange(1, 53)); Assert.That(builder.Failed, Is.False); }
            Assert.That(world.EncodeSnapshot(), Is.EqualTo(before)); Assert.That(journal.Ready, Is.False, "failed pending work cannot advance the committed journal");
            estimate = world.EstimateCommitCohortReplacementWork(builder); Assert.That(estimate, Is.LessThanOrEqualTo(4096));
            var children = world.CommitCohortReplacement(builder, 1.1, out work); Assert.That(work, Is.LessThanOrEqualTo(estimate)); Assert.That(children.Count, Is.EqualTo(200));
            Assert.That(world.PinkCount + world.BlueCount, Is.EqualTo(200)); Assert.That(world.Falling.Count, Is.EqualTo(200));
            Publish(world, 1.2); Assert.That(BlockWorld.DecodeSnapshot(world.EncodeSnapshot()).EncodeSnapshot(), Is.EqualTo(world.EncodeSnapshot()));
            Assert.That(journal.IsValid(world), Is.False); Assert.That(journal.Step(world, 53, out _), Is.False); Assert.That(journal.Failed, Is.True);
        }

        [Test]
        public void BuilderCloneProtectsPartialPieceAndConcurrentMembershipChangesInvalidateItsProof()
        {
            var bottom = new Vector3Int(0, 80, 0); var world = Seed(bottom, 16); var cohort = FullCohort(world, bottom, 16, 0, 1); var run = world.Falling[cohort.MemberIds[0]];
            Remove(world, run.Id, 5); var spans = new[] { Span(run, 0, 5), Span(run, 6, 10) }; var builder = world.BeginCohortReplacement(cohort.Id, spans, out _);
            for (int i = 0; i < 21; i++) builder.Step(world, builder.EstimateNextStepWork(), out _);
            var rollback = builder.Clone(out _);
            while (!builder.Ready) builder.Step(world, 31, out _);
            Assert.That(rollback.Ready, Is.False); while (!rollback.Ready) rollback.Step(world, 31, out _);
            var abortWorld = world.Clone(); var successful = world.Clone();
            var committed = successful.CommitCohortReplacement(builder, 1.1, out _); Assert.That(committed.Count, Is.EqualTo(2));
            Assert.That(abortWorld.EncodeSnapshot(), Is.EqualTo(world.EncodeSnapshot()));
            Remove(world, run.Id, 2); Assert.That(rollback.IsValid(world), Is.False); Assert.That(rollback.Step(world, 31, out _), Is.False);
        }

        [Test]
        public void PerCohortPublicationPreparationAndLegacyEpochsDoNotEnumerateUnrelatedMotion()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 3);
            var group = world.BeginCohort(bottom, 1); world.DetachCohortPiece(group.Id, 1, 1, out _);
            var legacy = world.Detach(bottom + Vector3Int.up * 2, 1, 2); var snapshot = world.Clone();
            AdmitModeledLanding(world, group.Id);
            int estimate = world.EstimatePrepareCohortForPublicationWork(group.Id); world.PrepareCohortForPublication(group.Id, out int work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            estimate = world.EstimatePrepareLegacyRunEpochsWork(); world.PrepareLegacyRunEpochsForPublication(out work); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            world.Seal(2, world.RevisionKeys.ToArray()); world.ActivateRebasePendingCohorts(7);
            Assert.That(world.Falling[legacy.Id].StartedAt, Is.EqualTo(7)); Assert.That(world.Cohorts[group.Id].StartedAt, Is.EqualTo(7));
            Assert.That(snapshot.Falling[legacy.Id].StartedAt, Is.EqualTo(2)); Assert.That(snapshot.Cohorts[group.Id].Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation));
        }

        [Test]
        public void PreparingAnUpperBeforeItsNewLowerDoesNotRevokeTheirSamePublicationBarrier()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 3);
            var upper = world.BeginCohort(bottom + Vector3Int.up * 2, 1); world.DetachCohortPiece(upper.Id, 1, 1, out _);
            var lower = world.BeginCohort(bottom, 1); world.DetachCohortPiece(lower.Id, 1, 1, out _);
            world.SetCohortSupport(upper.Id, lower.Id, .1f, out _); var snapshot = world.Clone();
            AdmitModeledLanding(world, upper.Id); AdmitModeledLanding(world, lower.Id);
            world.PrepareCohortForPublication(upper.Id, out _); world.PrepareCohortForPublication(lower.Id, out _);
            world.Seal(2, world.RevisionKeys.ToArray()); world.ActivateRebasePendingCohorts(5);
            Assert.That(world.Cohorts[upper.Id].Phase, Is.EqualTo(FallingCohortPhase.Falling)); Assert.That(world.Cohorts[lower.Id].Phase, Is.EqualTo(FallingCohortPhase.Falling));
            Assert.That(world.Cohorts[upper.Id].StartedAt, Is.EqualTo(5)); Assert.That(world.Cohorts[lower.Id].StartedAt, Is.EqualTo(5));
            Assert.That(snapshot.Cohorts[upper.Id].Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation)); Assert.That(snapshot.Cohorts[lower.Id].Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation));
        }

        [Test]
        public void SnapshotAndDeltaPreserveAllGroupedMotionMembershipAliasesTeamsAndOldBaseline()
        {
            var bottom = new Vector3Int(0, 80, 0); var baseline = Seed(bottom, 16); var parent = FullCohort(baseline, bottom, 16, 0, 1);
            var peer = BlockWorld.DecodeSnapshot(baseline.EncodeSnapshot()); var next = baseline.Clone(); var run = next.Falling[parent.MemberIds[0]];
            Remove(next, run.Id, 5); Fracture(next, next.Cohorts[parent.Id], new[] { Span(run, 0, 5), Span(run, 6, 10) }, 1.1); Publish(next, 1.2);
            var bytes = next.EncodeSnapshot(); Assert.That(BlockWorld.DecodeSnapshot(bytes).EncodeSnapshot(), Is.EqualTo(bytes));
            var repaired = peer.WithDelta(next.EncodeDelta(peer.Revision)); Assert.That(repaired.EncodeSnapshot(), Is.EqualTo(bytes));
            Assert.That(repaired.PinkCount, Is.EqualTo(next.PinkCount)); Assert.That(repaired.BlueCount, Is.EqualTo(next.BlueCount));
            Assert.That(peer.Falling[run.Id].Cells[5].Occupied, Is.True); Assert.That(peer.Cohorts.Count, Is.EqualTo(1));
            Assert.That(peer.FallingTargetAliases, Is.Empty); Assert.That(repaired.FallingTargetAliases.Count, Is.EqualTo(10));
            Assert.That(BitConverter.ToUInt32(bytes, 0), Is.EqualTo(0x334B4C42)); Assert.That(BitConverter.ToUInt32(next.EncodeDelta(peer.Revision), 0), Is.EqualTo(0x33444C42));
            Assert.That(GameplayContentSignature.BlockProtocolVersion, Is.EqualTo(BlockWorld.ProtocolVersion)); Assert.That(BlockWorld.ProtocolVersion, Is.EqualTo(3));
        }

        [Test]
        public void BatchEncodingMatchesEveryWireByteForGroupedAliasesLegacyMotionAndTombstones()
        {
            var bottom = new Vector3Int(0, 80, 0); var baseline = Seed(bottom, 16);
            var parent = FullCohort(baseline, bottom, 16, 0, 1); var peer = BlockWorld.DecodeSnapshot(baseline.EncodeSnapshot());
            var next = baseline.Clone(); var run = next.Falling[parent.MemberIds[0]];
            Remove(next, run.Id, 5); Fracture(next, next.Cohorts[parent.Id], new[] { Span(run, 0, 5), Span(run, 6, 10) }, 1.1); Publish(next, 1.2);
            var legacyBottom = new Vector3Int(-17, 20, 17); next.Set(legacyBottom, BlockCell.Create(2)); next.Detach(legacyBottom, 1, 1.3);
            var deleted = new Vector3Int(17, 2, -17); next.Set(deleted, BlockCell.Create(1)); next.Seal(next.Revision + 1, next.RevisionKeys.ToArray());
            next.Set(deleted, default); next.Set(new Vector3Int(-18, 3, -18), BlockCell.Create(2, true));
            next.Seal(next.Revision + 1, next.RevisionKeys.ToArray());
            Assert.That(next.FallingTargetAliases.Count, Is.EqualTo(10)); Assert.That(next.Falling.Values.Any(r => r.Cohort == null), Is.True);
            Assert.That(next.Cohorts.Count, Is.GreaterThan(0)); Assert.That(next.Chunks.ContainsKey(BlockWorld.ChunkAt(deleted)), Is.False);
            var requests = new[] { new BlockWorld.EncodingRequest(true), new BlockWorld.EncodingRequest(false, 0),
                new BlockWorld.EncodingRequest(false, 1), new BlockWorld.EncodingRequest(false, peer.Revision),
                new BlockWorld.EncodingRequest(false, next.Revision), new BlockWorld.EncodingRequest(true, 123),
                new BlockWorld.EncodingRequest(false, peer.Revision) };
            byte[] before = next.EncodeSnapshot(); var encoded = next.EncodeBatch(requests);
            for (int i = 0; i < requests.Length; i++)
                CollectionAssert.AreEqual(requests[i].Full ? before : next.EncodeDelta(requests[i].Since), encoded[i], "request " + i);
            Assert.That(encoded[0], Is.SameAs(encoded[5])); Assert.That(encoded[3], Is.SameAs(encoded[6]));
            CollectionAssert.AreEqual(before, BlockWorld.DecodeSnapshot(encoded[0]).EncodeSnapshot());
            CollectionAssert.AreEqual(before, peer.WithDelta(encoded[3]).EncodeSnapshot());
            CollectionAssert.AreEqual(before, next.EncodeSnapshot()); Assert.That(peer.FallingTargetAliases, Is.Empty);
            var duplicate = next.EncodeBatch(new[] { requests[3], requests[3], requests[3] });
            Assert.That(duplicate[0], Is.SameAs(duplicate[1])); Assert.That(duplicate[1], Is.SameAs(duplicate[2]));
            CollectionAssert.AreEqual(next.EncodeDelta(peer.Revision), duplicate[0]);
        }

        [Test]
        public void GroupSettleKeepsOneDisplacementAndRejectsStaleFractureWithoutDuplicatingCounts()
        {
            var bottom = new Vector3Int(0, 80, 0); var world = Seed(bottom, 17); var cohort = FullCohort(world, bottom, 17, 0, 1);
            var low = world.Falling[cohort.MemberIds[0]]; var high = world.Falling[cohort.MemberIds[1]];
            world.EditCohort(cohort.Id, out _).LandingKnown = true; world.EditCohort(cohort.Id, out _).LandingOffset = 1;
            var old = world.Clone(); var changed = new HashSet<Vector3Int>(); int estimate = world.EstimateSettleWork(low.Id);
            Assert.That(world.Settle(low.Id, low.Bottom + Vector3Int.down * 10, changed, out int work), Is.True); Assert.That(work, Is.LessThanOrEqualTo(estimate));
            Assert.That(world.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.Settling)); Assert.That(world.Falling[high.Id].LandingOffset, Is.EqualTo(1));
            Assert.That(world.PinkCount + world.BlueCount, Is.EqualTo(17)); Assert.That(old.Falling.Count, Is.EqualTo(2));
            Remove(world, high.Id, 0); Assert.That(world.Cohorts, Is.Empty); Assert.That(old.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.Falling));
            var other = Seed(bottom, 16); var otherCohort = FullCohort(other, bottom, 16, 0, 1); var damaged = other.Falling[otherCohort.MemberIds[0]];
            other.EditCohort(otherCohort.Id, out _).LandingKnown = true; other.EditCohort(otherCohort.Id, out _).LandingOffset = 1; Remove(other, damaged.Id, 4);
            Assert.That(other.Settle(damaged.Id, damaged.Bottom + Vector3Int.down * 10, new HashSet<Vector3Int>()), Is.False);
        }

        [Test]
        public void InvalidPieceAndIncompleteFractureAreAtomicAndLegacyFourHundredCellWireLimitRemains()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 32); var cohort = world.BeginCohort(bottom, 32); byte[] before = world.EncodeSnapshot();
            Assert.Throws<InvalidOperationException>(() => world.DetachCohortPiece(cohort.Id, 17, 1, out _)); Assert.That(world.EncodeSnapshot(), Is.EqualTo(before));
            var run = world.DetachCohortPiece(cohort.Id, 16, 1, out _); before = world.EncodeSnapshot();
            Assert.Throws<InvalidOperationException>(() => world.ReplaceCohort(cohort.Id, new[] { Span(run, 0, 5) }, 2, out _)); Assert.That(world.EncodeSnapshot(), Is.EqualTo(before));
            var legacy = Seed(new Vector3Int(0, -80, 0), 400); legacy.Detach(new Vector3Int(0, -80, 0), 400, 123.45);
            var decoded = BlockWorld.DecodeSnapshot(legacy.EncodeSnapshot()); Assert.That(decoded.Falling.Values.Single().Cells.Length, Is.EqualTo(400));
            Assert.That(decoded.Falling.Values.Single().Cohort, Is.Null); Assert.That(decoded.Falling.Values.Single().StartedAt, Is.EqualTo(123.45));
        }

        [TestCase("phase")]
        [TestCase("cursor")]
        [TestCase("epoch")]
        [TestCase("offset")]
        [TestCase("speed")]
        [TestCase("clamp")]
        [TestCase("orphan_member")]
        [TestCase("duplicate_member")]
        [TestCase("group_piece_17")]
        [TestCase("incomplete_intact")]
        public void SnapshotRejectsInvalidCohortDescriptorsMembershipAndStorage(string corruption)
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 16); var cohort = FullCohort(world, bottom, 16, 0, 1); var run = world.Falling[cohort.MemberIds[0]];
            switch (corruption)
            {
                case "phase": cohort.Phase = (FallingCohortPhase)99; break;
                case "cursor": cohort.DetachCursor = cohort.TopExclusive + 1; break;
                case "epoch": cohort.StartedAt = double.NaN; break;
                case "offset": cohort.InitialOffset = -1; break;
                case "speed": cohort.InitialSpeed = float.PositiveInfinity; break;
                case "clamp": cohort.InitialOffset = 2; cohort.LandingKnown = true; cohort.LandingOffset = 1; break;
                case "orphan_member": world.Falling.Clear(); break;
                case "duplicate_member": ((IList<ulong>)cohort.MemberIds).Add(run.Id); break;
                case "group_piece_17": run.Cells = run.Cells.Concat(new[] { default(BlockCell) }).ToArray(); break;
                case "incomplete_intact": run.Cells[5] = default; break;
            }
            Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(world.EncodeSnapshot()));
        }

        [Test]
        public void SnapshotRejectsSupportCyclesOrphansWrongSourceOrderAndOldProtocolMarkers()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 3); var lower = FullCohort(world, bottom, 1, 0, 1); var upper = FullCohort(world, bottom + Vector3Int.up * 2, 1, 0, 1);
            world.SetCohortSupport(upper.Id, lower.Id, .1f, out _); var bytes = world.EncodeSnapshot();
            Assert.That(BlockWorld.DecodeSnapshot(bytes).Cohorts[upper.Id].LowerSupport.Id, Is.EqualTo(lower.Id));
            lower.LowerSupport = upper; lower.SupportClearance = .1f; Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(world.EncodeSnapshot()));
            lower.LowerSupport = null; lower.SupportClearance = 0; world.Cohorts.Remove(lower.Id); Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(world.EncodeSnapshot()));
            bytes[3] = (byte)'1'; Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(bytes));
        }

        [Test]
        public void SnapshotRejectsAggregateLiveAndRetainedSupportDepthBeyondFourHundred()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 3);
            var lower = FullCohort(world, bottom, 1, 0, 1); var upper = FullCohort(world, bottom + Vector3Int.up * 2, 1, 0, 1);
            world.SetCohortSupport(upper.Id, lower.Id, .1f, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            var element = retained.FieldType.GetElementType(); var chain = Array.CreateInstance(element, 399);
            retained.SetValue(world.Cohorts[lower.Id], chain); world.Cohorts[lower.Id].NeedsLandingProof = true;
            Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(world.EncodeSnapshot()),
                "399 retained entries plus a live lower and live upper form 401 total motion entries");
            retained.SetValue(world.Cohorts[lower.Id], Array.CreateInstance(element, 398));
            Assert.That(BlockWorld.DecodeSnapshot(world.EncodeSnapshot()).Cohorts[upper.Id].LowerSupport.Id, Is.EqualTo(lower.Id));
        }

        static FallingCohortReplacement PrepareMembership(BlockWorld world, ulong parentId, IReadOnlyList<FallingCohortSpan> spans)
        {
            var proof = world.BeginCohortReplacement(parentId, spans, out _);
            while (!proof.Ready) { proof.Step(world, 127, out int work); Assert.That(work, Is.InRange(1, 127)); Assert.That(proof.Failed, Is.False); }
            return proof;
        }

        static void ProveModeledFloor(FallingPrivateReplacement preparation, BlockWorld source, Vector3Int referenceBottom, float referenceCap)
        {
            var request = preparation.LandingProofRequest;
            Assert.That(preparation.SourceWorld, Is.SameAs(source)); Assert.That(preparation.SourceRevision, Is.EqualTo(source.Revision));
            Assert.That(preparation.SourceRound, Is.EqualTo(source.Round)); Assert.That(preparation.SourceMutationGeneration, Is.EqualTo(source.MutationGeneration));
            Assert.That(request.SourceBottom.x, Is.EqualTo(referenceBottom.x)); Assert.That(request.SourceBottom.z, Is.EqualTo(referenceBottom.z));
            Assert.That(request.SourceTopExclusive, Is.GreaterThan(request.SourceBottom.y));
            Assert.That(request.SourceDetachCursor, Is.InRange(request.SourceBottom.y, request.SourceTopExclusive));
            float cap = referenceCap + (request.SourceBottom.y - referenceBottom.y) * BlockWorld.CellSize;
            Assert.That(float.IsFinite(cap), Is.True); Assert.That(cap, Is.GreaterThanOrEqualTo(request.SourceOffsetAtSourceTime));
            preparation.ProvideLandingProof(source, cap); Assert.That(preparation.WaitingForLandingProof, Is.False);
        }
        static void ProveNewChildFloor(FallingPrivateReplacement preparation, BlockWorld source, Vector3Int referenceBottom, float referenceCap)
        {
            var request = preparation.LandingProofRequest;
            Assert.That(request.TargetCohortId, Is.GreaterThanOrEqualTo(source.NextCohortId));
            Assert.That(source.Cohorts.ContainsKey(request.TargetCohortId), Is.False, "phase9 certifies the new child rather than a published owner/bridge");
            ProveModeledFloor(preparation, source, referenceBottom, referenceCap);
        }

        [Test]
        public void BudgetedCloneCopiesAllWireStateWithBoundedProgressAndProtectsOldMotionAndArrays()
        {
            var bottom = new Vector3Int(0, 40, 0); var world = Seed(bottom, 17); var parent = FullCohort(world, bottom, 17, 1, 2);
            var old = world.Falling[parent.MemberIds[0]]; Remove(world, old.Id, 5);
            var sourceBytes = world.EncodeSnapshot(); using var clone = world.BeginBudgetedClone(); long stages = 0;
            Assert.Throws<InvalidOperationException>(() => clone.TakeWorld());
            while (!clone.Ready)
            {
                ulong progress = clone.ProgressVersion; clone.Step(world, 127, out int work);
                Assert.That(work, Is.InRange(1, 127)); Assert.That(clone.ProgressVersion, Is.GreaterThan(progress));
                Assert.That(world.EncodeSnapshot(), Is.EqualTo(sourceBytes)); Assert.That(clone.Failed, Is.False); stages++;
                Assert.That(stages, Is.LessThanOrEqualTo(clone.MaximumWork));
            }
            var copy = clone.TakeWorld(); Assert.That(copy.EncodeSnapshot(), Is.EqualTo(sourceBytes));
            copy.EditFalling(old.Id).InitialSpeed = 3; Assert.That(world.Falling[old.Id].InitialSpeed, Is.Zero);
            Remove(copy, old.Id, 1); Assert.That(world.Falling[old.Id].Cells[1].Occupied, Is.True);
            world.EditFalling(old.Id).LandingKnown = true; world.EditFalling(old.Id).LandingOffset = .1f;
            Assert.That(copy.Falling[old.Id].LandingKnown, Is.False);
        }

        [Test]
        public void BudgetedAliasCloneFollowsEachMaximumLengthEndpointUnderFiveWorkAdmissions()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 1); var parent = FullCohort(source, bottom, 1, 0, 10);
            ulong liveId = parent.MemberIds[0];
            var aliases = (Dictionary<FallingCellAddress, FallingCellAddress>)typeof(BlockWorld)
                .GetField("_targetAliases", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
            for (int i = 0; i < 16; i++) aliases.Add(new FallingCellAddress((ulong)(1000 + i), 0),
                new FallingCellAddress(i == 15 ? liveId : (ulong)(1001 + i), 0));
            byte[] before = source.EncodeSnapshot(); using var clone = source.BeginBudgetedClone();
            while (!clone.Ready)
            {
                ulong progress = clone.ProgressVersion; clone.Step(source, 5, out int work);
                Assert.That(work, Is.InRange(1, 5)); Assert.That(clone.ProgressVersion, Is.GreaterThan(progress)); Assert.That(clone.Failed, Is.False);
                Assert.That(clone.ProcessedWork, Is.LessThanOrEqualTo(clone.MaximumWork)); Assert.That(source.EncodeSnapshot(), Is.EqualTo(before));
            }
            var copy = clone.TakeWorld(); Assert.That(copy.EncodeSnapshot(), Is.EqualTo(before));
            Assert.That(copy.TryGetFallingCell(1000, 0, out var run, out int index), Is.True); Assert.That(run.Id, Is.EqualTo(liveId)); Assert.That(index, Is.Zero);
        }

        [Test]
        public void PrivateFourHundredCellReplacementFinishesDuringEvery1024WorkStageWithoutPublishingPartialState()
        {
            var bottom = new Vector3Int(0, -80, 0); var source = Seed(bottom, 400); var parent = FullCohort(source, bottom, 400, 0, 10);
            var ids = parent.MemberIds.ToArray();
            foreach (ulong id in ids)
            {
                var run = source.Falling[id]; for (int i = 0; i < run.Cells.Length; i++) if (((run.Bottom.y - bottom.y + i) & 1) != 0) Remove(source, id, i);
            }
            var spans = new List<FallingCohortSpan>();
            foreach (ulong id in ids)
                if (source.Falling.TryGetValue(id, out var run)) for (int i = 0; i < run.Cells.Length; i++) if (run.Cells[i].Occupied) spans.Add(Span(run, i, 1));
            spans.Sort((a, b) => a.Bottom.y.CompareTo(b.Bottom.y)); var proof = PrepareMembership(source, parent.Id, spans);
            Assert.That(source.EstimateCommitCohortReplacementWork(proof), Is.GreaterThan(1024), "the old atomic path remains the captured failure counterexample");
            byte[] before = source.EncodeSnapshot(); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            Assert.Throws<InvalidOperationException>(() => preparation.TakePreparedWorld());
            Assert.Throws<InvalidOperationException>(() => { var ignored = preparation.Children; });
            long stages = 0; int proofCount = 0;
            while (!preparation.Ready)
            {
                ulong progress = preparation.ProgressVersion; preparation.Step(source, 1024, out int work);
                Assert.That(work, Is.InRange(1, 1024)); Assert.That(preparation.ProgressVersion, Is.GreaterThan(progress));
                Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.That(preparation.Failed, Is.False);
                if (preparation.WaitingForLandingProof) { ProveNewChildFloor(preparation, source, bottom, 64f); proofCount++; }
                stages++; Assert.That(stages, Is.LessThanOrEqualTo(preparation.MaximumWork));
            }
            Assert.That(proofCount, Is.EqualTo(200), "every fully detached new child supplies an independent own-floor proof");
            Assert.That(stages, Is.GreaterThan(1)); Assert.That(proof.Ready, Is.True, "private metadata must not consume the old membership proof");
            var next = preparation.TakePreparedWorld(); Assert.That(preparation.Children.Count, Is.EqualTo(200));
            Assert.That(next.PinkCount + next.BlueCount, Is.EqualTo(200)); Assert.That(next.Falling.Count, Is.EqualTo(200));
            long publicationEstimate = next.EstimatedPreparedPublicationWork;
            next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(1), "two hundred fragments share the exact same immutable source motion");
            Assert.That(next.LastPublicationWork, Is.LessThanOrEqualTo(publicationEstimate));
            foreach (var child in preparation.Children)
            {
                Assert.That(child.Phase, Is.EqualTo(FallingCohortPhase.Falling)); Assert.That(child.StartedAt, Is.EqualTo(10.5));
                Assert.That(child.InitialOffset, Is.EqualTo(source.Cohorts[parent.Id].Offset(10.5)).Within(.00001f));
                Assert.That(child.InitialSpeed, Is.EqualTo(source.Cohorts[parent.Id].Speed(10.5)).Within(.00001f));
            }
            Assert.That(BlockWorld.DecodeSnapshot(next.EncodeSnapshot()).EncodeSnapshot(), Is.EqualTo(next.EncodeSnapshot()));
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before));
        }

        [Test]
        public void PrivatePublicationKeepsTwoFrozenCommandsAddressesOwnersAndSourceTimeAcrossAFracture()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 10);
            var run = source.Falling[parent.MemberIds[0]]; byte frozenOwnerA = run.Cells[10].Owner, frozenOwnerB = run.Cells[12].Owner;
            float sourceTimeOffset = run.Offset(10.1); Remove(source, run.Id, 5);
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) });
            using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.Ready)
            {
                preparation.Step(source, 53, out int work); Assert.That(work, Is.InRange(1, 53));
                if (preparation.WaitingForLandingProof) ProveNewChildFloor(preparation, source, bottom, 64f);
            }
            var next = preparation.TakePreparedWorld(); next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.4);
            Assert.That(source.Falling[run.Id].Offset(10.1), Is.EqualTo(sourceTimeOffset));
            Assert.That(next.TryGetFallingCell(run.Id, 10, out var a, out int indexA), Is.True); Assert.That(a.Cells[indexA].Owner, Is.EqualTo(frozenOwnerA));
            Assert.That(next.TryGetFallingCell(run.Id, 12, out var b, out int indexB), Is.True); Assert.That(b.Cells[indexB].Owner, Is.EqualTo(frozenOwnerB));
            Remove(next, run.Id, 10); Remove(next, run.Id, 12);
            Assert.That(source.Falling[run.Id].Cells[10].Owner, Is.EqualTo(frozenOwnerA)); Assert.That(source.Falling[run.Id].Cells[12].Owner, Is.EqualTo(frozenOwnerB));
            Assert.That(next.PinkCount + next.BlueCount, Is.EqualTo(13));
        }

        [Test]
        public void PrivatePreparationDetectsSourceWritesRoundReplacementAndCancellationAndNeverReturnsPartialWorld()
        {
            var bottom = new Vector3Int(0, 40, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 1);
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5);
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) });
            using var stale = source.BeginPrivateReplacement(proof, 1.1); stale.Step(source, 31, out _);
            source.Set(new Vector3Int(1, 40, 0), BlockCell.Create(2)); Assert.That(stale.Step(source, 31, out _), Is.False);
            Assert.That(stale.Failed, Is.True); Assert.Throws<InvalidOperationException>(() => stale.TakePreparedWorld());
            using var replaced = source.BeginPrivateReplacement(proof, 1.1); var anotherRound = new BlockWorld(source.Dimensions, source.Round + 1);
            Assert.That(replaced.Step(anotherRound, 31, out _), Is.False); Assert.That(replaced.Failed, Is.True);
            using var canceled = source.BeginPrivateReplacement(proof, 1.1); canceled.Step(source, 31, out _); canceled.Cancel();
            Assert.That(canceled.Failed, Is.True); Assert.Throws<InvalidOperationException>(() => canceled.TakePreparedWorld());
            Assert.That(source.Cohorts.ContainsKey(parent.Id), Is.True); Assert.That(source.Falling[run.Id].Cells[5].Occupied, Is.False);
        }

        [Test]
        public void PrivateMaximumRetainedHistoryAndManyChildrenRemainWithinWorkBoundAndRoundTripAfterPublication()
        {
            // The source history remains immutable. Only a separately supplied stable-landing proof
            // lets the private first child replace retained history with a finite new trajectory.
            var bottom = new Vector3Int(0, -80, 0); var source = Seed(bottom, 400); var parent = FullCohort(source, bottom, 400, 0, 10);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 399)); parent.NeedsLandingProof = true;
            Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(source.EncodeSnapshot()), "the input has exactly 400 aggregate motion entries and is valid");
            var ids = parent.MemberIds.ToArray();
            foreach (ulong id in ids)
            {
                var run = source.Falling[id]; for (int i = 0; i < run.Cells.Length; i++) if (((run.Bottom.y - bottom.y + i) & 1) != 0) Remove(source, id, i);
            }
            var spans = new List<FallingCohortSpan>();
            foreach (ulong id in ids) if (source.Falling.TryGetValue(id, out var run))
                for (int i = 0; i < run.Cells.Length; i++) if (run.Cells[i].Occupied) spans.Add(Span(run, i, 1));
            spans.Sort((a, b) => a.Bottom.y.CompareTo(b.Bottom.y)); var proof = PrepareMembership(source, parent.Id, spans);
            using var preparation = source.BeginPrivateReplacement(proof, 10.1); int proofCount = 0;
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024)); Assert.That(preparation.Failed, Is.False);
                if (preparation.WaitingForLandingProof)
                {
                    var request = preparation.LandingProofRequest;
                    if (request.TargetCohortId == 0) Assert.That(request.LiveLowerId, Is.Zero);
                    else { Assert.That(request.LiveLowerId, Is.Not.Zero); Assert.That(source.Cohorts.ContainsKey(request.TargetCohortId), Is.False); }
                    ProveModeledFloor(preparation, source, bottom, .5f); proofCount++;
                }
                Assert.That(preparation.ProcessedWork, Is.LessThanOrEqualTo(preparation.MaximumWork));
            }
            var next = preparation.TakePreparedWorld(); next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            Assert.That(proofCount, Is.EqualTo(200), "the first retained-history proof and199 linked children each certify their own floor");
            Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()), "valid source motion must not publish an invalid cohort graph");
            Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(1));
            Assert.That(next.LastPublicationWork, Is.EqualTo(400 + 200 * 16));
        }

        [Test]
        public void RetainedHistoryWaitsForPrivateStableProofAndFutureMotionMatchesVerifiedLandingOracle()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 10);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 399)); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); byte[] sourceBytes = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) });
            using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.WaitingForLandingProof) { preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024)); }
            var request = preparation.LandingProofRequest;
            Assert.That(request.SourceBottom, Is.EqualTo(bottom)); Assert.That(request.SourceTopExclusive, Is.EqualTo(bottom.y + 5));
            Assert.That(request.SourceDetachCursor, Is.EqualTo(bottom.y + 5)); Assert.That(request.LiveLowerId, Is.Zero);
            Assert.That(request.SourceOffsetAtSourceTime, Is.EqualTo(parent.Offset(10.1))); Assert.Throws<InvalidOperationException>(() => preparation.TakePreparedWorld());
            Assert.That(preparation.Step(source, 1024, out int waitingWork), Is.False); Assert.That(waitingWork, Is.Zero);
            preparation.ProvideLandingProof(source, .5f);
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024));
                if (preparation.WaitingForLandingProof) ProveNewChildFloor(preparation, source, bottom, .5f);
            }
            var next = preparation.TakePreparedWorld(); next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            var first = preparation.Children[0]; Assert.That(first.InitialOffset, Is.EqualTo(parent.Offset(10.5))); Assert.That(first.InitialSpeed, Is.EqualTo(parent.Speed(10.5)));
            foreach (double now in new[] { 10.5, 10.6, 10.8, 11.0, 100.0 })
            {
                float elapsed = (float)(now - 10.5), raw = first.InitialOffset + first.InitialSpeed * elapsed + .5f * BlockWorld.Gravity * elapsed * elapsed;
                float expectedOffset = Mathf.Max(first.InitialOffset, Mathf.Min(raw, .5f));
                float expectedSpeed = raw >= .5f ? 0 : first.InitialSpeed + BlockWorld.Gravity * elapsed;
                Assert.That(first.Offset(now), Is.EqualTo(expectedOffset).Within(.00001f)); Assert.That(first.Speed(now), Is.EqualTo(expectedSpeed).Within(.00001f));
                Assert.That(first.Offset(now), Is.LessThanOrEqualTo(.5f));
            }
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
        }

        [Test]
        public void ActualPublicationRejectsVerifiedFloorCrossingBeforeAnyCohortWrite()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 10);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            var element = retained.FieldType.GetElementType(); var chain = Array.CreateInstance(element, 1);
            // A retained falling support leaves the parent free to reach the later publication counterexample.
            chain.SetValue(Activator.CreateInstance(element, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { FallingCohortPhase.Falling, 10d, 0f, 0f, false, 0f, 0f }, null), 0);
            retained.SetValue(parent, chain); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); byte[] sourceBytes = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.WaitingForLandingProof) preparation.Step(source, 1024, out _);
            preparation.ProvideLandingProof(source, .5f);
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024));
                if (preparation.WaitingForLandingProof) ProveNewChildFloor(preparation, source, bottom, .5f);
            }
            var next = preparation.TakePreparedWorld(); byte[] unpublished = next.EncodeSnapshot();
            Assert.That(parent.Offset(10.5), Is.GreaterThan(.5f)); Assert.Throws<InvalidOperationException>(() => next.ActivateRebasePendingCohorts(10.5));
            Assert.That(next.EncodeSnapshot(), Is.EqualTo(unpublished), "activation validates every shared scalar sample before writing any cohort fields");
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes)); Assert.That(preparation.Children[0].LandingOffset, Is.EqualTo(.5f));
            foreach (var child in preparation.Children) Assert.That(child.Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation));
        }

        [Test]
        public void RemovingClampedParentBottomReprovesTheSameStaticFloorInsteadOfHoveringOneCell()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 10);
            parent = source.EditCohort(parent.Id, out _); parent.LandingKnown = true; parent.LandingOffset = .2f;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 0); byte[] sourceBytes = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 1, 15) }); using var preparation = source.BeginPrivateReplacement(proof, 10.3);
            while (!preparation.WaitingForLandingProof) preparation.Step(source, 1024, out _);
            Assert.That(preparation.LandingProofRequest.SourceBottom, Is.EqualTo(bottom + Vector3Int.up));
            preparation.ProvideLandingProof(source, .2f + BlockWorld.CellSize); while (!preparation.Ready) preparation.Step(source, 1024, out _);
            var next = preparation.TakePreparedWorld(); next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            var first = preparation.Children[0]; Assert.That(first.InitialOffset, Is.EqualTo(.2f).Within(.00001f)); Assert.That(first.InitialSpeed, Is.Zero);
            Assert.That(first.Offset(10.6), Is.GreaterThan(.2f)); Assert.That(first.Offset(11), Is.EqualTo(.3f).Within(.00001f)); Assert.That(first.Speed(11), Is.Zero);
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
        }

        [Test]
        public void WaitingPrivateProofCanBeCanceledOrInvalidatedWithoutExposingWorldOrConsumingMembership()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 10);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 1)); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); byte[] sourceBytes = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) });
            using var canceled = source.BeginPrivateReplacement(proof, 10.1); while (!canceled.WaitingForLandingProof) canceled.Step(source, 1024, out _);
            canceled.Cancel(); Assert.That(canceled.WaitingForLandingProof, Is.False); Assert.That(canceled.Failed, Is.True);
            Assert.Throws<InvalidOperationException>(() => canceled.ProvideLandingProof(source, .5f)); Assert.Throws<InvalidOperationException>(() => canceled.TakePreparedWorld());
            Assert.That(proof.Ready, Is.True); Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes));
            using var invalidated = source.BeginPrivateReplacement(proof, 10.1); while (!invalidated.WaitingForLandingProof) invalidated.Step(source, 1024, out _);
            source.Set(bottom + Vector3Int.right, BlockCell.Create(2)); Assert.That(invalidated.Step(source, 1024, out _), Is.False);
            Assert.That(invalidated.Failed, Is.True); Assert.Throws<InvalidOperationException>(() => invalidated.ProvideLandingProof(source, .5f));
            Assert.Throws<InvalidOperationException>(() => invalidated.TakePreparedWorld());
        }

        [Test]
        public void TerminalOwnerProofRetainsEveryLiveEdgeAndRebasesTheOwnerInsteadOfDiscardingDynamicSupport()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 18); var lower = FullCohort(source, bottom, 1, 0, 10);
            var parent = FullCohort(source, bottom + Vector3Int.up * 2, 16, 0, 10); source.SetCohortSupport(parent.Id, lower.Id, .1f, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            retained.SetValue(source.Cohorts[lower.Id], Array.CreateInstance(retained.FieldType.GetElementType(), 398));
            parent = source.Cohorts[parent.Id]; var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); byte[] sourceBytes = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.WaitingForLandingProof) preparation.Step(source, 1024, out _);
            Assert.That(preparation.LandingProofRequest.LiveLowerId, Is.Zero);
            Assert.That(preparation.LandingProofRequest.TargetCohortId, Is.EqualTo(lower.Id));
            Assert.That(preparation.LandingProofRequest.SourceBottom, Is.EqualTo(lower.Bottom));
            preparation.ProvideLandingProof(source, .5f);
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024));
                if (preparation.WaitingForLandingProof) ProveNewChildFloor(preparation, source, bottom, .5f);
            }
            Assert.That(preparation.PreparedExistingCohorts.Count, Is.EqualTo(1)); var next = preparation.TakePreparedWorld();
            next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            var owner = next.Cohorts[lower.Id]; Assert.That(owner.InitialOffset, Is.EqualTo(source.Cohorts[lower.Id].Offset(10.5)));
            Assert.That(owner.InitialSpeed, Is.EqualTo(source.Cohorts[lower.Id].Speed(10.5))); Assert.That(owner.StartedAt, Is.EqualTo(10.5));
            Assert.That(preparation.Children[0].LowerSupport, Is.SameAs(owner)); Assert.That(preparation.Children[1].LowerSupport, Is.SameAs(preparation.Children[0]));
            Assert.That(owner.Offset(11), Is.EqualTo(.5f).Within(.00001f)); Assert.That(owner.Speed(11), Is.Zero);
            Assert.That(preparation.Children[0].Offset(11), Is.EqualTo(.6f).Within(.00001f)); Assert.That(preparation.Children[0].Speed(11), Is.Zero);
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
            Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(2)); Assert.That(next.LastPublicationWork, Is.LessThanOrEqualTo(4096));
        }

        [Test]
        public void LongLivePathWithTwoHundredRetainedEntriesNormalizesTerminalOwnerWithin400ProtocolBound()
        {
            var source = Seed(Vector3Int.zero, 215); var lowerIds = new List<ulong>();
            for (int y = 0; y < 199; y++)
            {
                var cohort = FullCohort(source, new Vector3Int(0, y, 0), 1, 0, 10);
                if (lowerIds.Count != 0) source.SetCohortSupport(cohort.Id, lowerIds[lowerIds.Count - 1], 0, out _);
                lowerIds.Add(cohort.Id);
            }
            var parent = FullCohort(source, new Vector3Int(0, 199, 0), 16, 0, 10); source.SetCohortSupport(parent.Id, lowerIds[198], 0, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldOwner = source.Cohorts[lowerIds[0]]; retained.SetValue(oldOwner, Array.CreateInstance(retained.FieldType.GetElementType(), 200)); oldOwner.NeedsLandingProof = true;
            var run = source.Falling[source.Cohorts[parent.Id].MemberIds[0]]; Remove(source, run.Id, 5);
            byte[] sourceBytes = source.EncodeSnapshot(); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(sourceBytes), "200 live descriptors plus200 retained entries exactly fill the400 motion bound");
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.Ready)
            {
                ulong progress = preparation.ProgressVersion; preparation.Step(source, 1024, out int work);
                Assert.That(work, Is.InRange(1, 1024)); Assert.That(preparation.ProgressVersion, Is.GreaterThan(progress)); Assert.That(preparation.Failed, Is.False);
                Assert.That(preparation.ProcessedWork, Is.LessThanOrEqualTo(preparation.MaximumWork)); Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes));
                if (preparation.WaitingForLandingProof)
                {
                    var request = preparation.LandingProofRequest;
                    if (request.TargetCohortId == lowerIds[0])
                    { Assert.That(request.SourceBottom, Is.EqualTo(Vector3Int.zero)); Assert.That(request.LiveLowerId, Is.Zero); preparation.ProvideLandingProof(source, .5f); }
                    else ProveNewChildFloor(preparation, source, Vector3Int.zero, .5f);
                }
            }
            var next = preparation.TakePreparedWorld(); long publicationEstimate = next.EstimatedPreparedPublicationWork;
            next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            Assert.That(next.Cohorts.Count, Is.EqualTo(201)); Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(2));
            Assert.That(next.LastPublicationWork, Is.EqualTo(201 + 400 + 3 * 16)); Assert.That(next.LastPublicationWork, Is.LessThanOrEqualTo(publicationEstimate));
            for (int i = 0; i < lowerIds.Count; i++)
            {
                var unchanged = next.Cohorts[lowerIds[i]];
                Assert.That(unchanged.Offset(10.5), Is.EqualTo(source.Cohorts[lowerIds[i]].Offset(10.5)).Within(.00001f));
                Assert.That(unchanged.Speed(10.5), Is.EqualTo(source.Cohorts[lowerIds[i]].Speed(10.5)).Within(.00001f));
                if (i != 0) Assert.That(unchanged.LowerSupport.Id, Is.EqualTo(lowerIds[i - 1]));
            }
            foreach (double now in new[] { 10.6, 10.8, 11.0, 100.0 })
            {
                float elapsed = (float)(now - 10.5), raw = .5f * BlockWorld.Gravity * elapsed * elapsed;
                float offset = Mathf.Min(raw, .5f), speed = raw >= .5f ? 0 : BlockWorld.Gravity * elapsed;
                foreach (ulong id in lowerIds) { Assert.That(next.Cohorts[id].Offset(now), Is.EqualTo(offset).Within(.00001f)); Assert.That(next.Cohorts[id].Speed(now), Is.EqualTo(speed).Within(.00001f)); }
                Assert.That(preparation.Children[0].Offset(now), Is.EqualTo(offset).Within(.00001f)); Assert.That(preparation.Children[0].Speed(now), Is.EqualTo(speed).Within(.00001f));
            }
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(sourceBytes)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
        }

        [Test]
        public void TerminalOwnerProofCancellationKeepsTheWholePublishedLivePathAndNeverExposesExistingEdits()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 18); var lower = FullCohort(source, bottom, 1, 0, 10);
            var parent = FullCohort(source, bottom + Vector3Int.up * 2, 16, 0, 10); source.SetCohortSupport(parent.Id, lower.Id, .1f, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            var owner = source.Cohorts[lower.Id]; retained.SetValue(owner, Array.CreateInstance(retained.FieldType.GetElementType(), 398)); owner.NeedsLandingProof = true;
            var run = source.Falling[source.Cohorts[parent.Id].MemberIds[0]]; Remove(source, run.Id, 5); byte[] before = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.WaitingForLandingProof) preparation.Step(source, 1024, out _);
            Assert.That(preparation.LandingProofRequest.TargetCohortId, Is.EqualTo(owner.Id));
            Assert.Throws<InvalidOperationException>(() => { var ignored = preparation.PreparedExistingCohorts; });
            preparation.ProvideLandingProof(source, .5f); preparation.Step(source, 127, out _); preparation.Cancel();
            Assert.That(preparation.Failed, Is.True); Assert.Throws<InvalidOperationException>(() => preparation.TakePreparedWorld());
            Assert.Throws<InvalidOperationException>(() => { var ignored = preparation.PreparedExistingCohorts; });
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.That(source.Cohorts[parent.Id].LowerSupport.Id, Is.EqualTo(owner.Id)); Assert.That(proof.Ready, Is.True);
        }

        [Test]
        public void ExistingDirectUpperRebindsAtExactPublicationAndFallsOntoTheNewTopFragmentWithoutHover()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 16); var parent = FullCohort(source, bottom, 16, 0, 10);
            var upperBottom = bottom + Vector3Int.up * 18; source.Set(upperBottom, BlockCell.Create(2));
            source.Seal(source.Revision + 1, source.RevisionKeys.ToArray()); var upper = FullCohort(source, upperBottom, 1, 0, 10);
            source.SetCohortSupport(upper.Id, parent.Id, .2f, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            parent = source.Cohorts[parent.Id]; retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 1)); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); Remove(source, run.Id, 15); byte[] before = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 9) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024));
                if (preparation.WaitingForLandingProof)
                {
                    ulong target = preparation.LandingProofRequest.TargetCohortId;
                    if (target == 0 || target == upper.Id)
                    {
                        bool upperProof = target == upper.Id;
                        Assert.That(target, Is.EqualTo(upperProof ? upper.Id : 0UL));
                        preparation.ProvideLandingProof(source, upperProof ? .5f + (98 - 80) * BlockWorld.CellSize : .5f);
                    }
                    else ProveNewChildFloor(preparation, source, bottom, .5f);
                }
            }
            Assert.That(preparation.PreparedExistingCohorts.Count, Is.EqualTo(1));
            var next = preparation.TakePreparedWorld(); long publicationEstimate = next.EstimatedPreparedPublicationWork;
            next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            var reboundUpper = next.Cohorts[upper.Id]; var newTop = preparation.Children[1];
            Assert.That(reboundUpper.Offset(10.5), Is.EqualTo(source.Cohorts[upper.Id].Offset(10.5)).Within(.00001f));
            Assert.That(reboundUpper.Speed(10.5), Is.EqualTo(source.Cohorts[upper.Id].Speed(10.5)).Within(.00001f));
            Assert.That(reboundUpper.InitialOffset, Is.EqualTo(.2f).Within(.00001f)); Assert.That(reboundUpper.InitialSpeed, Is.Zero);
            Assert.That(reboundUpper.LowerSupport, Is.SameAs(newTop)); Assert.That(reboundUpper.NeedsLandingProof, Is.False);
            Assert.That(reboundUpper.SupportClearance, Is.EqualTo(.3f).Within(.00001f));
            Assert.That(newTop.Offset(11), Is.EqualTo(.6f).Within(.00001f)); Assert.That(reboundUpper.Offset(11), Is.EqualTo(.9f).Within(.00001f));
            Assert.That(reboundUpper.Speed(11), Is.Zero);
            Assert.That(reboundUpper.Bottom.y * BlockWorld.CellSize - reboundUpper.Offset(11),
                Is.EqualTo(newTop.TopExclusive * BlockWorld.CellSize - newTop.Offset(11)).Within(.00001f));
            Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(2)); Assert.That(next.LastPublicationWork, Is.EqualTo(2 + 1 + 3 * 16));
            Assert.That(next.LastPublicationWork, Is.EqualTo(publicationEstimate));
            Assert.That(next.PinkCount, Is.EqualTo(source.PinkCount)); Assert.That(next.BlueCount, Is.EqualTo(source.BlueCount));
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
        }

        [Test]
        public void ThirtyTwoUpperBranchesCopy400EntryHistoriesUnderStageBudgetAndSampleTheir399EntryPrefixOnce()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 48); var parent = FullCohort(source, bottom, 16, 0, 10);
            var upperIds = new List<ulong>();
            for (int i = 0; i < 32; i++)
            {
                var upper = FullCohort(source, bottom + Vector3Int.up * (16 + i), 1, 0, 10.49);
                source.SetCohortSupport(upper.Id, parent.Id, i * BlockWorld.CellSize, out _); upperIds.Add(upper.Id);
            }
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            parent = source.Cohorts[parent.Id]; retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 398)); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); Remove(source, run.Id, 15);
            byte[] before = source.EncodeSnapshot(); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(before));
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 9) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            int proofCount = 0;
            while (!preparation.Ready)
            {
                ulong progress = preparation.ProgressVersion; preparation.Step(source, 1024, out int work);
                Assert.That(work, Is.InRange(1, 1024)); Assert.That(preparation.ProgressVersion, Is.GreaterThan(progress)); Assert.That(preparation.Failed, Is.False);
                Assert.That(preparation.ProcessedWork, Is.LessThanOrEqualTo(preparation.MaximumWork)); Assert.That(source.EncodeSnapshot(), Is.EqualTo(before));
                if (preparation.WaitingForLandingProof)
                {
                    ulong target = preparation.LandingProofRequest.TargetCohortId;
                    if (target == 0 || upperIds.Contains(target))
                    { Assert.That(target == 0 || upperIds.Contains(target), Is.True); preparation.ProvideLandingProof(source, .5f); }
                    else ProveNewChildFloor(preparation, source, bottom, .5f);
                    proofCount++;
                }
            }
            Assert.That(proofCount, Is.EqualTo(34), "first-child proof,32 existing bridges and the new high child's own proof"); Assert.That(preparation.PreparedExistingCohorts.Count, Is.EqualTo(32));
            var next = preparation.TakePreparedWorld(); long publicationEstimate = next.EstimatedPreparedPublicationWork;
            next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(33)); Assert.That(next.LastPublicationWork, Is.EqualTo(399 + 32 + 34 * 16));
            Assert.That(next.LastPublicationWork, Is.EqualTo(publicationEstimate));
            foreach (ulong id in upperIds)
            {
                Assert.That(next.Cohorts[id].InitialOffset, Is.EqualTo(source.Cohorts[id].Offset(10.5)).Within(.00001f));
                Assert.That(next.Cohorts[id].InitialSpeed, Is.EqualTo(source.Cohorts[id].Speed(10.5)).Within(.00001f));
                Assert.That(next.Cohorts[id].LowerSupport, Is.SameAs(preparation.Children[1]));
            }
            Assert.That(next.PinkCount, Is.EqualTo(source.PinkCount)); Assert.That(next.BlueCount, Is.EqualTo(source.BlueCount));
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
            // This is a World publication-work oracle, not Terrain occupancy or physics reachability acceptance.
        }

        [Test]
        public void UpperHistoryCopyAndUpperProofCancellationPreserveSourceBytesAndReusableMembership()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 19); var parent = FullCohort(source, bottom, 16, 0, 10);
            var upper = FullCohort(source, bottom + Vector3Int.up * 18, 1, 0, 10); source.SetCohortSupport(upper.Id, parent.Id, .2f, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            parent = source.Cohorts[parent.Id]; retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 398)); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); byte[] before = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) });
            var phase = typeof(FallingPrivateReplacement).GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var copying = source.BeginPrivateReplacement(proof, 10.1))
            {
                while ((int)phase.GetValue(copying) != 16)
                {
                    copying.Step(source, copying.EstimateNextStepWork(), out _);
                    if (copying.WaitingForLandingProof) { Assert.That(copying.LandingProofRequest.TargetCohortId, Is.Zero); copying.ProvideLandingProof(source, .5f); }
                }
                copying.Step(source, 8, out int copyWork); Assert.That(copyWork, Is.GreaterThan(0)); copying.Cancel();
                Assert.Throws<InvalidOperationException>(() => copying.TakePreparedWorld()); Assert.Throws<InvalidOperationException>(() => { var ignored = copying.PreparedExistingCohorts; });
            }
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.That(proof.Ready, Is.True);
            using (var waiting = source.BeginPrivateReplacement(proof, 10.1))
            {
                while (!waiting.WaitingForLandingProof || waiting.LandingProofRequest.TargetCohortId == 0)
                {
                    if (waiting.WaitingForLandingProof) waiting.ProvideLandingProof(source, .5f);
                    waiting.Step(source, 1024, out _);
                }
                Assert.That(waiting.LandingProofRequest.TargetCohortId, Is.EqualTo(upper.Id)); waiting.Cancel();
                Assert.That(waiting.WaitingForLandingProof, Is.False); Assert.Throws<InvalidOperationException>(() => waiting.ProvideLandingProof(source, 2.3f));
                Assert.Throws<InvalidOperationException>(() => waiting.TakePreparedWorld());
            }
            Assert.That(proof.Ready, Is.True); Assert.That(source.EncodeSnapshot(), Is.EqualTo(before));
            Assert.That(source.Cohorts[upper.Id].LowerSupport.Id, Is.EqualTo(parent.Id));
        }

        [Test]
        public void UpperWithNoPublishedDetachedCellsGetsNoPrivateStaticProofOrPublicationRebase()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 19); var parent = FullCohort(source, bottom, 16, 0, 10);
            var upper = source.BeginCohort(bottom + Vector3Int.up * 18, 1); source.SetCohortSupport(upper.Id, parent.Id, .2f, out _);
            var retained = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
            parent = source.Cohorts[parent.Id]; retained.SetValue(parent, Array.CreateInstance(retained.FieldType.GetElementType(), 398)); parent.NeedsLandingProof = true;
            var run = source.Falling[parent.MemberIds[0]]; Remove(source, run.Id, 5); byte[] before = source.EncodeSnapshot();
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            int proofCount = 0;
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out _);
                if (preparation.WaitingForLandingProof)
                {
                    Assert.That(preparation.LandingProofRequest.TargetCohortId, Is.Not.EqualTo(upper.Id), "the undetached upper still receives no private proof");
                    if (preparation.LandingProofRequest.TargetCohortId == 0) preparation.ProvideLandingProof(source, .5f);
                    else ProveNewChildFloor(preparation, source, bottom, .5f);
                    proofCount++;
                }
            }
            var next = preparation.TakePreparedWorld(); next.Seal(source.Revision + 1, Array.Empty<Vector3Int>()); next.ActivateRebasePendingCohorts(10.5);
            Assert.That(proofCount, Is.EqualTo(2), "first child and high child prove their own cap; the undetached upper does not"); Assert.That(next.LastPublicationMotionSamples, Is.EqualTo(1));
            var dormant = next.Cohorts[upper.Id]; Assert.That(dormant.MemberIds, Is.Empty); Assert.That(dormant.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(dormant.RebasePending, Is.False); Assert.That(dormant.NeedsLandingProof, Is.True); Assert.That(dormant.LowerSupport, Is.Null);
            Assert.That(next.TryGet(upper.Bottom, out _), Is.True); Assert.That(next.PinkCount, Is.EqualTo(source.PinkCount)); Assert.That(next.BlueCount, Is.EqualTo(source.BlueCount));
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(next.EncodeSnapshot()));
        }

        [Test]
        public void BudgetedOwnedCloneOwnsDescriptorAndRunObjectsButPreservesCellArrayCowAndSupportReferences()
        {
            var bottom = new Vector3Int(0, 80, 0); var source = Seed(bottom, 17);
            var lower = FullCohort(source, bottom, 1, 0, 10); var upper = FullCohort(source, bottom + Vector3Int.up, 16, 0, 10);
            source.SetCohortSupport(upper.Id, lower.Id, 0, out _);
            var legacyBottom = bottom + Vector3Int.right; source.Set(legacyBottom, BlockCell.Create(2)); var legacy = source.Detach(legacyBottom, 1, 10);
            byte[] before = source.EncodeSnapshot(); using var clone = source.BeginBudgetedOwnedClone();
            while (!clone.Ready)
            {
                clone.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024));
                Assert.That(clone.ProcessedWork, Is.LessThanOrEqualTo(clone.MaximumWork)); Assert.That(source.EncodeSnapshot(), Is.EqualTo(before));
            }
            var owned = clone.TakeWorld(); var ownedLower = owned.Cohorts[lower.Id]; var ownedUpper = owned.Cohorts[upper.Id];
            Assert.That(ownedUpper.LowerSupport, Is.SameAs(ownedLower)); Assert.That(ownedLower, Is.Not.SameAs(source.Cohorts[lower.Id]));
            Assert.That(ownedUpper, Is.Not.SameAs(source.Cohorts[upper.Id]));
            // Owned descriptors and run links are already private; no dependency traversal is required.
            int editEstimate = owned.EstimateEditCohortWork(lower.Id);
            Assert.That(editEstimate, Is.EqualTo(1));
            Assert.That(owned.EstimateEditFallingWork(ownedUpper.MemberIds[0]), Is.EqualTo(1)); Assert.That(owned.EstimateEditFallingWork(legacy.Id), Is.Zero);
            Assert.That(owned.EditCohort(lower.Id, out int editWork), Is.SameAs(ownedLower)); Assert.That(editWork, Is.EqualTo(editEstimate));
            ulong member = ownedUpper.MemberIds[0]; Assert.That(owned.Falling[member].Cohort, Is.SameAs(ownedUpper));
            Assert.That(owned.Falling[member], Is.Not.SameAs(source.Falling[member]));
            Assert.That(owned.Falling[member].Cells, Is.SameAs(source.Falling[member].Cells));
            Remove(owned, member, 5); Assert.That(owned.Falling[member].Cells, Is.Not.SameAs(source.Falling[member].Cells));
            Assert.That(source.Falling[member].Cells[5].Occupied, Is.True);
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(owned.EncodeSnapshot()));
        }

        [Test]
        public void PublicWorldRemoveLowerSupportThenFractureCanReachLateStableFloorCrossingWithoutForgedWire()
        {
            var lowerBottom = new Vector3Int(0, 4, 0); var source = Seed(lowerBottom, 17);
            var lower = FullCohort(source, lowerBottom, 1, 0, 10); var parent = FullCohort(source, lowerBottom + Vector3Int.up, 16, 0, 10);
            source.SetCohortSupport(parent.Id, lower.Id, 0, out _);
            // This is World API reachability after the helper's explicit historical-proof invalidation;
            // the new natural activation gate still requires a complete own landing proof.
            Remove(source, source.Cohorts[lower.Id].MemberIds[0], 0);
            var run = source.Falling[source.Cohorts[parent.Id].MemberIds[0]]; Remove(source, run.Id, 5);
            byte[] before = source.EncodeSnapshot(); Assert.DoesNotThrow(() => BlockWorld.DecodeSnapshot(before));
            var proof = PrepareMembership(source, parent.Id, new[] { Span(run, 0, 5), Span(run, 6, 10) }); using var preparation = source.BeginPrivateReplacement(proof, 10.1);
            while (!preparation.WaitingForLandingProof) preparation.Step(source, 1024, out _);
            Assert.That(preparation.LandingProofRequest.TargetCohortId, Is.Zero); Assert.That(preparation.LandingProofRequest.SourceBottom.y, Is.EqualTo(5));
            Assert.That(preparation.LandingProofRequest.SourceOffsetAtSourceTime, Is.LessThan(.5f));
            preparation.ProvideLandingProof(source, .5f); // A stable floor at physical y=0 is below the frozen source-time position.
            while (!preparation.Ready)
            {
                preparation.Step(source, 1024, out int work); Assert.That(work, Is.InRange(1, 1024));
                if (preparation.WaitingForLandingProof) ProveNewChildFloor(preparation, source, lowerBottom + Vector3Int.up, .5f);
            }
            var next = preparation.TakePreparedWorld(); byte[] unpublished = next.EncodeSnapshot();
            Assert.That(source.Cohorts[parent.Id].Offset(10.5), Is.GreaterThan(.5f));
            Assert.Throws<InvalidOperationException>(() => next.ActivateRebasePendingCohorts(10.5), "the World-layer counterexample needs a recovery contract if Terrain reachability is independently established");
            Assert.That(source.EncodeSnapshot(), Is.EqualTo(before)); Assert.That(next.EncodeSnapshot(), Is.EqualTo(unpublished));
        }
    }
}
