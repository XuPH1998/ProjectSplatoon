#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Acceptance contracts written in Reports only. NOT compiled or executed.
    // Historical seeds are explicitly wire-valid but geometrically invalid.
    // They do not claim ordinary QueueBuild/QueueDestroy can reach these states.
    // RequestClassifiedRecovery isolates the private classifier when a natural
    // authority trigger is absent; its use is called out by each relevant test.
    public sealed class BlockHistoricalRecoveryTests
    {
        const double Now = 10;
        const int PumpLimit = 3000;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly Vector2 Dimensions = new(32, 64);
        static readonly Vector2Int Column = new(2, 2);
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Historical geometry classifier acceptance");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            var floor = new GameObject("Registered map floor top zero");
            floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(.25f, -.05f, .25f);
            var collider = floor.AddComponent<BoxCollider>();
            collider.size = new Vector3(2, .1f, 2);
            ((HashSet<Collider>)Field(_terrain, "_staticColliders")).Add(collider);
            Physics.SyncTransforms();
            _terrain.Tick(false, Now, .016f);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator WireValidStablePenetrationHasANaturalAuthorityRecoveryTrigger()
        {
            var seed = StablePenetration(out ulong cohort, out var addresses);
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var frozen = source.EncodeSnapshot();
            var expected = CaptureCells(source);
            var before = Counts(source);
            int recoveredCommits = 0;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (_terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.Recovered) return;
                recoveredCommits++;
                AssertConserved(source, _terrain.World, expected, before, addresses);
                var repaired = _terrain.World.Cohorts[cohort];
                Assert.That(repaired.Bottom.y * BlockWorld.CellSize - repaired.Offset(Now),
                    Is.EqualTo(.1f).Within(1e-6), "Minimal free grid span must start above the occupied Y0 cube.");
                Assert.That(repaired.Speed(Now), Is.Zero.Within(1e-6));
                Assert.That(repaired.StartedAt, Is.EqualTo(Now));
                AssertWireCopies(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try
            {
                // No classifier-request reflection: exercise the ordinary authority trigger.
                yield return Pump(Now, true, () => _terrain.HistoricalRecoveryCount == 1);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(recoveredCommits, Is.EqualTo(1));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot(), "A repair must not mutate the published source.");
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SettlingHistoricalPenetrationCannotSkipClassificationAndSettleUpward()
        {
            var seed = StablePenetration(out ulong cohort, out var addresses);
            seed.EditCohort(cohort, out _).Phase = FallingCohortPhase.Settling;
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var expected = CaptureCells(source);
            var before = Counts(source);
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (_terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.Recovered) return;
                seen = true;
                AssertConserved(source, _terrain.World, expected, before, addresses);
                AssertWireCopies(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(Now, true, () => _terrain.HistoricalRecoveryCount == 1); }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(seen, Is.True,
                "An existing Settling descriptor is a published motion, not a new span allowed to be settled upward.");
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator BelowBottomHistoricalCohortRecoversWithoutDroppingAnyTeamCell()
        {
            var seed = NewWorld();
            var target = AddCohort(seed, -79, new byte[] { 1, 2 }, .15f, .15f);
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var expected = CaptureCells(source);
            var before = Counts(source);
            var addresses = AllAddresses(source);
            Assert.That(target.Bottom.y * BlockWorld.CellSize - target.Offset(Now), Is.LessThan(source.Bottom));
            RequestClassifiedRecovery(target.Id, true);
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (_terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.Recovered) return;
                seen = true;
                var repaired = _terrain.World.Cohorts[target.Id];
                Assert.That(repaired.Bottom.y * BlockWorld.CellSize - repaired.Offset(Now),
                    Is.EqualTo(source.Bottom).Within(1e-6));
                AssertConserved(source, _terrain.World, expected, before, addresses);
                AssertWireCopies(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(Now, true, () => _terrain.HistoricalRecoveryCount == 1); }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(seen, Is.True);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator DynamicDynamicPenetrationIsClassifiedEvenWithoutStableOrStaticOverlap()
        {
            var seed = NewWorld();
            var lower = AddCohort(seed, 10, new byte[] { 1 }, .5f, .5f); // actual .5-.6
            var upper = AddCohort(seed, 20, new byte[] { 2 }, 1.45f, 1.45f); // actual .55-.65
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var expected = CaptureCells(source);
            var before = Counts(source);
            var addresses = AllAddresses(source);
            Assert.That(PhysicalOverlap(source, lower.Id, upper.Id, Now), Is.True);
            // Explicit classifier request; this contract does not claim natural admission.
            RequestClassifiedRecovery(upper.Id, true);
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (_terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.Recovered) return;
                seen = true;
                Assert.That(PhysicalOverlap(_terrain.World, lower.Id, upper.Id, Now), Is.False);
                AssertConserved(source, _terrain.World, expected, before, addresses);
                AssertWireCopies(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(Now, true, () => _terrain.HistoricalRecoveryCount == 1); }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(seen, Is.True, "NoLongerInvalid is incorrect while two actual occupied cubes penetrate.");
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator PureDynamicHistoricalPenetrationHasANaturalAuthorityRecoveryTrigger()
        {
            var seed = NewWorld();
            var lower = AddCohort(seed, 10, new byte[] { 1 }, .5f, .5f);
            var upper = AddCohort(seed, 20, new byte[] { 2 }, 1.45f, 1.45f);
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var expected = CaptureCells(source);
            var before = Counts(source);
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (_terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.Recovered) return;
                seen = true;
                Assert.That(PhysicalOverlap(_terrain.World, lower.Id, upper.Id, Now), Is.False);
                AssertConserved(source, _terrain.World, expected, before, AllAddresses(source));
                AssertWireCopies(source, _terrain.World, AllAddresses(source));
            };
            _terrain.PublishedCommit += observe;
            try
            {
                // No private request or scheduler mutation: the new binding
                // publication guard must route the illegal source to classification.
                yield return Pump(Now, true, () => _terrain.HistoricalRecoveryCount == 1);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(seen, Is.True);
            Assert.That(_terrain.GravityBindingPublicationDeferrals, Is.GreaterThan(0));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ASafeNaturalSupportBindingKeepsTheActualPublicationPoseAndSpeed()
        {
            var seed = NewWorld();
            AddCohort(seed, 10, new byte[] { 1 }, 0, 1);
            var upper = AddCohort(seed, 20, new byte[] { 2 }, 0, 2);
            yield return InstallHistorical(seed);
            var original = _terrain.World.Cohorts[upper.Id];
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (seen || !_terrain.World.Cohorts.TryGetValue(upper.Id, out var bound) || bound.LowerSupport == null) return;
                seen = true;
                double publishedAt = _terrain.SimulationTime;
                Assert.That(bound.Offset(publishedAt), Is.EqualTo(original.Offset(publishedAt)).Within(1e-6));
                Assert.That(bound.Speed(publishedAt), Is.EqualTo(original.Speed(publishedAt)).Within(1e-6));
                Assert.That(bound.Offset(publishedAt + .01), Is.EqualTo(original.Offset(publishedAt + .01)).Within(1e-6),
                    "Attaching a safe future support must not release old elapsed free fall or lose accumulated velocity.");
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(10.2, true, () => seen); }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That(_terrain.GravityBindingPublicationDeferrals, Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator HistoricalRecoveryKeepsAnOldFractureAliasEndpointAcrossSnapshotAndDelta()
        {
            var seed = PreparedAliasDraft(out var children, out var aliases);
            seed.ActivateRebasePendingCohorts(Now);
            var target = seed.EditCohort(children[1], out _);
            target.InitialOffset = 2.15f; target.InitialSpeed = 0;
            target.LandingKnown = true; target.LandingOffset = 2.15f;
            seed.Set(At(0), BlockCell.Create(2, true));
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var expected = CaptureCells(source);
            var before = Counts(source);
            Assert.That(source.TryGetFallingCell(aliases[0].Run, aliases[0].Index, out var endpoint, out int index), Is.True);
            ulong endpointId = endpoint.Id;
            Assert.That(index, Is.EqualTo(aliases[0].Index));
            RequestClassifiedRecovery(target.Id, true);
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (_terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.Recovered) return;
                seen = true;
                AssertConserved(source, _terrain.World, expected, before, aliases);
                Assert.That(_terrain.World.TryGetFallingCell(aliases[0].Run, aliases[0].Index, out var repaired, out int repairedIndex), Is.True);
                Assert.That(repaired.Id, Is.EqualTo(endpointId));
                Assert.That(repairedIndex, Is.EqualTo(index));
                AssertWireCopies(source, _terrain.World, aliases);
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(Now, true, () => _terrain.HistoricalRecoveryCount == 1); }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(seen, Is.True);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator NoCapacityTerminatesOnceForTheSamePublishedWorldToken()
        {
            var seed = FullCapacityHistoricalColumn(out ulong target);
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var frozen = source.EncodeSnapshot();
            var before = Counts(source);
            RequestClassifiedRecovery(target, true);
            yield return Pump(Now, true, () => _terrain.LastHistoricalRecoveryOutcome
                == BlockTerrain.HistoricalRecoveryOutcome.NoCapacity && _terrain.PendingTransactions == 0);
            long starts = _terrain.HistoricalRecoveryStarts;
            for (int repeat = 0; repeat < 64; repeat++)
            {
                Assert.That(RequestClassifiedRecovery(target, true, false), Is.False);
                _terrain.Tick(true, Now, .016f);
                yield return null;
            }
            Assert.That(_terrain.HistoricalRecoveryStarts, Is.EqualTo(starts));
            Assert.That(_terrain.HistoricalRecoveryNoCapacity, Is.EqualTo(1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero, "NoCapacity must never count as recovery success.");
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.World, Is.SameAs(source));
            Assert.That(Counts(_terrain.World), Is.EqualTo(before));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator FullCapacityHistoricalColumnAlsoNeedsANaturalAuthorityTerminal()
        {
            var seed = FullCapacityHistoricalColumn(out _);
            yield return InstallHistorical(seed);
            // This deliberately does not force private admission. It catches
            // work.Landing > sourceBottom returning forever before classification.
            yield return Pump(Now, true, () => _terrain.LastHistoricalRecoveryOutcome
                == BlockTerrain.HistoricalRecoveryOutcome.NoCapacity && _terrain.PendingTransactions == 0);
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That(_terrain.HistoricalRecoveryNoCapacity, Is.EqualTo(1));
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AuthorityLossDuringRecoveryPreparationCancelsWithoutPublishing()
        {
            var seed = StablePenetration(out ulong target, out _);
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var frozen = source.EncodeSnapshot();
            RequestClassifiedRecovery(target, true);
            yield return Pump(Now, true, () => Field(_terrain, "_historicalRecoveryPreparation") != null);
            _terrain.Tick(false, Now, .016f);
            Assert.That(Field(_terrain, "_historicalRecoveryPreparation"), Is.Null);
            Assert.That(Field(_terrain, "_historicalRecoveryRequest"), Is.Null);
            Assert.That(_terrain.LastHistoricalRecoveryOutcome, Is.EqualTo(BlockTerrain.HistoricalRecoveryOutcome.Cancelled));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That(_terrain.World, Is.SameAs(source));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            for (int step = 0; step < 8; step++) { _terrain.Tick(false, Now, .016f); yield return null; }
            Assert.That(_terrain.World, Is.SameAs(source));
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AuthorityLossDuringGuardedPendingRecordsCancelledAndRetainsTheSource()
        {
            var seed = StablePenetration(out ulong target, out _);
            yield return InstallHistorical(seed);
            var source = _terrain.World;
            var frozen = source.EncodeSnapshot();
            RequestClassifiedRecovery(target, true);
            yield return Pump(Now, true, RecoveryPending);
            // Pump checks immediately after Tick and returns before yielding a frame.
            _terrain.Tick(false, Now, .016f);
            Assert.That(RecoveryPending(), Is.False);
            Assert.That(_terrain.LastHistoricalRecoveryOutcome, Is.EqualTo(BlockTerrain.HistoricalRecoveryOutcome.Cancelled));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            for (int step = 0; step < 8; step++) { _terrain.Tick(false, Now, .016f); yield return null; }
            Assert.That(_terrain.World, Is.SameAs(source));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ANoLongerInvalidWitnessMustFinishWithoutAColumnConflictTerminal()
        {
            var seed = NewWorld();
            var lower = AddCohort(seed, 10, new byte[] { 1 }, .5f, .5f);
            var upper = AddCohort(seed, 20, new byte[] { 2 }, 1.45f, 1.9f);
            yield return InstallHistorical(seed);
            Assert.That(PhysicalOverlap(_terrain.World, lower.Id, upper.Id, Now), Is.True);
            RequestClassifiedRecovery(upper.Id, true);
            yield return Pump(Now, true, RecoveryPending);
            Assert.That(PhysicalOverlap(_terrain.World, lower.Id, upper.Id, 10.2), Is.False);
            yield return Pump(10.2, true, () => _terrain.LastHistoricalRecoveryOutcome
                == BlockTerrain.HistoricalRecoveryOutcome.NoLongerInvalid && _terrain.PendingTransactions == 0);
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That((bool)Invoke(_terrain, "HistoricalRecoveryTerminal", Column), Is.False,
                "A naturally resolved witness must not freeze all legal motions in its column.");
        }

        [Test]
        [Timeout(600000)]
        public void OwnedCloneKeepsPreparedSharedPrefixesLegacyEpochsAndAliases()
        {
            var source = PreparedAliasDraft(out var children, out var aliases);
            var frozen = source.EncodeSnapshot();
            long preparedWork = source.EstimatedPreparedPublicationWork;
            Assert.That(preparedWork, Is.GreaterThan(0));
            var clone = source.BeginBudgetedOwnedClone();
            int steps = 0;
            while (!clone.Ready && steps++ < 10000)
            {
                int admission = clone.EstimateNextStepWork();
                ulong version = clone.ProgressVersion;
                clone.Step(source, admission, out int actual);
                Assert.That(actual, Is.LessThanOrEqualTo(admission));
                Assert.That(clone.Failed, Is.False);
                Assert.That(clone.ProgressVersion, Is.GreaterThan(version));
            }
            Assert.That(clone.Ready, Is.True);
            Assert.That(clone.ProcessedWork, Is.LessThanOrEqualTo(clone.MaximumWork));
            var owned = clone.TakeWorld();
            Assert.That(owned.EstimatedPreparedPublicationWork, Is.EqualTo(preparedWork));
            foreach (ulong id in children)
            {
                Assert.That(owned.Cohorts[id], Is.Not.SameAs(source.Cohorts[id]));
                var copiedSample = Field(owned.Cohorts[id], "PreparedRebase");
                var sourceSample = Field(source.Cohorts[id], "PreparedRebase");
                Assert.That(copiedSample, Is.Not.Null);
                Assert.That(copiedSample, Is.Not.SameAs(sourceSample));
                Assert.That(Field(copiedSample, "Prefix"), Is.Not.SameAs(Field(sourceSample, "Prefix")));
                Assert.That(Field(owned.Cohorts[id], "VerifiedPrivateLanding"), Is.EqualTo(true));
            }
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            owned.ActivateRebasePendingCohorts(10.1);
            Assert.That(source.EstimatedPreparedPublicationWork, Is.EqualTo(preparedWork),
                "Publishing an owned clone must not consume the source's registrations.");
            Assert.That(owned.EstimatedPreparedPublicationWork, Is.Zero);
            foreach (ulong id in children)
            {
                Assert.That(owned.Cohorts[id].Phase, Is.EqualTo(FallingCohortPhase.Falling));
                Assert.That(owned.Cohorts[id].StartedAt, Is.EqualTo(10.1));
                Assert.That(owned.Cohorts[id].InitialOffset, Is.EqualTo(.05f + .5f * BlockWorld.Gravity * .1f * .1f).Within(1e-6));
            }
            var legacy = owned.Falling.Values.Single(run => run.Cohort == null);
            Assert.That(legacy.StartedAt, Is.EqualTo(10.1));
            Assert.That(source.Falling[legacy.Id].StartedAt, Is.EqualTo(Now));
            source.ActivateRebasePendingCohorts(10.1);
            CollectionAssert.AreEqual(source.EncodeSnapshot(), owned.EncodeSnapshot());
            owned.Seal(source.Revision + 1, Array.Empty<Vector3Int>());
            AssertWireCopies(source, owned, aliases);
        }

        [Test]
        [Timeout(600000)]
        public void CancellingAnOwnedDraftCloneKeepsPreparedRegistryAndCellStateUntouched()
        {
            var source = PreparedAliasDraft(out _, out _);
            var frozen = source.EncodeSnapshot();
            long work = source.EstimatedPreparedPublicationWork;
            var clone = source.BeginBudgetedOwnedClone();
            clone.Step(source, clone.EstimateNextStepWork(), out _);
            clone.Cancel();
            Assert.That(clone.Failed, Is.True);
            Assert.That(source.EstimatedPreparedPublicationWork, Is.EqualTo(work));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            Assert.Throws<InvalidOperationException>(() => clone.TakeWorld());
            source.ActivateRebasePendingCohorts(10.1);
            Assert.That(source.EstimatedPreparedPublicationWork, Is.Zero);
        }

        static BlockWorld NewWorld() => new(Dimensions);
        static Vector3Int At(int y) => new(Column.x, y, Column.y);

        static FallingCohort AddCohort(BlockWorld world, int y, byte[] owners, float offset, float landing)
        {
            for (int i = 0; i < owners.Length; i++) world.Set(At(y + i), BlockCell.Create(owners[i]));
            var cohort = world.BeginCohort(At(y), owners.Length);
            int left = owners.Length;
            while (left > 0) { int count = Math.Min(left, BlockWorld.MaxCohortPieceCells); world.DetachCohortPiece(cohort.Id, count, Now, out _); left -= count; }
            cohort.LandingKnown = true; cohort.LandingOffset = landing;
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Now);
            cohort = world.EditCohort(cohort.Id, out _);
            cohort.InitialOffset = offset; cohort.InitialSpeed = 0; cohort.LandingKnown = true; cohort.LandingOffset = landing;
            return cohort;
        }

        static BlockWorld StablePenetration(out ulong target, out FallingCellAddress[] addresses)
        {
            var world = NewWorld();
            world.Set(At(0), BlockCell.Create(2, true));
            var cohort = AddCohort(world, 20, new byte[] { 1, 2, 1 }, 1.95f, 1.95f);
            target = cohort.Id; addresses = AllAddresses(world);
            Assert.That(cohort.Bottom.y * BlockWorld.CellSize - cohort.Offset(Now), Is.EqualTo(.05f).Within(1e-6));
            return world;
        }

        static BlockWorld FullCapacityHistoricalColumn(out ulong target)
        {
            var world = NewWorld();
            var cohort = AddCohort(world, 3, new byte[] { 1 }, .3f, .3f);
            target = cohort.Id;
            int first = Mathf.CeilToInt(world.Bottom / BlockWorld.CellSize);
            int end = Mathf.CeilToInt(BlockWorld.Top / BlockWorld.CellSize);
            for (int y = first; y < end; y++) Assert.That(world.Set(At(y), BlockCell.Create(2, true)), Is.True);
            return world;
        }

        static BlockWorld PreparedAliasDraft(out ulong[] children, out FallingCellAddress[] aliases)
        {
            var world = NewWorld();
            var lower = AddCohort(world, 2, new byte[] { 2 }, 0, 0);
            var parent = AddCohort(world, 20, new byte[] { 1, 2, 1 }, .05f, 1.8f);
            world.SetCohortSupport(parent.Id, lower.Id, 1.7f, out _);
            var run = world.Falling[parent.MemberIds.Single()];
            Assert.That(world.RemoveFallingCell(run.Id, 1, out _), Is.True);
            var result = world.ReplaceCohort(parent.Id, new[]
            {
                new FallingCohortSpan(At(20), 21, new[] { new FallingCohortPiece(run.Id, 0, 1) }),
                new FallingCohortSpan(At(22), 23, new[] { new FallingCohortPiece(run.Id, 2, 1) })
            }, Now, out _);
            children = result.Select(child => child.Id).ToArray();
            // Derive the exact immutable prefix of the genuine captured chain.
            // Reflection only enables the clone's optimized-prefix contract.
            var full = (Array)Field(result[0], "RebaseMotion");
            Assert.That(full.Length, Is.EqualTo(2));
            var prefix = Array.CreateInstance(full.GetType().GetElementType(), 1);
            Array.Copy(full, prefix, 1);
            foreach (var child in result)
            {
                SetField(child, "RebasePrefix", prefix);
                child.LandingKnown = true; child.LandingOffset = 2f; child.NeedsLandingProof = false;
                SetField(child, "VerifiedPrivateLanding", true);
                SetField(child, "VerifiedPrivateLandingOffset", 2f);
            }
            world.PreparePendingCohortsForPublication(out _);
            var legacyCell = new Vector3Int(5, 20, 5);
            world.Set(legacyCell, BlockCell.Create(2));
            world.Detach(legacyCell, 1, Now);
            world.PrepareLegacyRunEpochsForPublication(out _);
            aliases = new[] { new FallingCellAddress(run.Id, 2) };
            Assert.That(world.FallingTargetAliases.Count, Is.GreaterThan(0));
            Assert.That(world.TryGetFallingCell(run.Id, 2, out var endpoint, out int index), Is.True);
            Assert.That(endpoint.Id, Is.GreaterThan(run.Id));
            Assert.That(index, Is.EqualTo(2));
            world.Seal(1, world.RevisionKeys.ToArray());
            return world;
        }

        IEnumerator InstallHistorical(BlockWorld seed)
        {
            seed.Seal(1, seed.RevisionKeys.ToArray());
            byte[] wire = seed.EncodeSnapshot();
            var checkedWire = BlockWorld.DecodeSnapshot(wire);
            CollectionAssert.AreEqual(wire, checkedWire.EncodeSnapshot());
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return Pump(Now, false, () => _terrain.Revision == 1 && _terrain.PendingTransactions == 0);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
        }

        bool RequestClassifiedRecovery(ulong id, bool cohort, bool expected = true)
        {
            // Prepare only the index facts of the installed immutable source.
            // This is a private-branch harness, not a natural authority path.
            SetField(_terrain, "_cohortAuthorityInitialized", true);
            if ((bool)Field(_terrain, "_cohortIndexPending"))
            {
                Invoke(_terrain, "StartGravityJournal", _terrain.World);
                for (int step = 0; step < 1000 && (bool)Field(_terrain, "_cohortIndexPending"); step++)
                {
                    var args = new object[] { _terrain.World, 0, Stopwatch.StartNew(), 1000d, 4096 };
                    Method(_terrain, "IndexGravityWork").Invoke(_terrain, args);
                }
                Assert.That(Field(_terrain, "_cohortIndexPending"), Is.EqualTo(false));
                SetField(_terrain, "_gravityJournal", null);
            }
            var motionType = typeof(BlockTerrain).GetNestedType("GravityMotionId", BindingFlags.NonPublic);
            var motion = Activator.CreateInstance(motionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { id, cohort }, null);
            bool accepted = (bool)Invoke(_terrain, "RequestHistoricalRecovery", motion, Column);
            Assert.That(accepted, Is.EqualTo(expected));
            return accepted;
        }

        bool RecoveryPending()
        {
            var pending = Field(_terrain, "_pending");
            return pending != null && Field(pending, "RecoveryGuard") != null;
        }

        IEnumerator Pump(double now, bool authority, Func<bool> done)
        {
            for (int step = 0; step < PumpLimit && !done(); step++)
            {
                _terrain.Tick(authority, now, .016f);
                if (done()) yield break;
                yield return null;
            }
            Assert.That(done(), Is.True, "Finite historical acceptance contract did not reach its declared outcome.");
        }

        static (int Pink, int Blue) Counts(BlockWorld world) => (world.PinkCount, world.BlueCount);
        static Dictionary<FallingCellAddress, BlockCell> CaptureCells(BlockWorld world)
        {
            var result = new Dictionary<FallingCellAddress, BlockCell>();
            foreach (var run in world.Falling.Values)
                for (int index = 0; index < run.Cells.Length; index++)
                    if (run.Cells[index].Occupied) result.Add(new FallingCellAddress(run.Id, index), run.Cells[index]);
            return result;
        }
        static FallingCellAddress[] AllAddresses(BlockWorld world) => CaptureCells(world).Keys.ToArray();
        static void AssertConserved(BlockWorld source, BlockWorld repaired,
            Dictionary<FallingCellAddress, BlockCell> expected, (int Pink, int Blue) counts, FallingCellAddress[] addresses)
        {
            Assert.That(Counts(repaired), Is.EqualTo(counts));
            Assert.That(CaptureCells(repaired).Count, Is.EqualTo(expected.Count));
            foreach (var pair in expected)
            {
                Assert.That(repaired.TryGetFallingCell(pair.Key.Run, pair.Key.Index, out var run, out int index), Is.True);
                Assert.That(run.Id, Is.EqualTo(pair.Key.Run));
                Assert.That(index, Is.EqualTo(pair.Key.Index));
                Assert.That(run.Cells[index].Owner, Is.EqualTo(pair.Value.Owner));
                Assert.That(run.Cells[index].Flags, Is.EqualTo(pair.Value.Flags));
                Assert.That(run.Cells[index].Durability, Is.EqualTo(pair.Value.Durability));
            }
            Assert.That(source.NextRunId, Is.EqualTo(repaired.NextRunId));
            Assert.That(source.NextCohortId, Is.EqualTo(repaired.NextCohortId));
            foreach (var pair in source.FallingTargetAliases)
                Assert.That(repaired.FallingTargetAliases[pair.Key], Is.EqualTo(pair.Value));
            AssertAddressesEqual(source, repaired, addresses);
        }
        static void AssertWireCopies(BlockWorld source, BlockWorld repaired, FallingCellAddress[] addresses)
        {
            var snapshot = BlockWorld.DecodeSnapshot(repaired.EncodeSnapshot());
            CollectionAssert.AreEqual(repaired.EncodeSnapshot(), snapshot.EncodeSnapshot());
            var delta = BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(repaired.EncodeDelta(source.Revision));
            CollectionAssert.AreEqual(repaired.EncodeSnapshot(), delta.EncodeSnapshot());
            AssertAddressesEqual(repaired, snapshot, addresses);
            AssertAddressesEqual(repaired, delta, addresses);
        }
        static void AssertAddressesEqual(BlockWorld left, BlockWorld right, FallingCellAddress[] addresses)
        {
            foreach (var address in addresses)
            {
                Assert.That(left.TryGetFallingCell(address.Run, address.Index, out var original, out int originalIndex), Is.True);
                Assert.That(right.TryGetFallingCell(address.Run, address.Index, out var copy, out int copyIndex), Is.True);
                Assert.That(copy.Id, Is.EqualTo(original.Id));
                Assert.That(copyIndex, Is.EqualTo(originalIndex));
                Assert.That(copy.Cells[copyIndex].Owner, Is.EqualTo(original.Cells[originalIndex].Owner));
            }
        }
        static bool PhysicalOverlap(BlockWorld world, ulong a, ulong b, double now)
        {
            var first = world.Cohorts[a]; var second = world.Cohorts[b];
            float bottomA = first.Bottom.y * BlockWorld.CellSize - first.Offset(now);
            float bottomB = second.Bottom.y * BlockWorld.CellSize - second.Offset(now);
            float topA = first.TopExclusive * BlockWorld.CellSize - first.Offset(now);
            float topB = second.TopExclusive * BlockWorld.CellSize - second.Offset(now);
            return bottomA < topB - 1e-6f && bottomB < topA - 1e-6f;
        }
        static object Field(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field " + name);
            return field.GetValue(target);
        }
        static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field " + name);
            field.SetValue(target, value);
        }
        static MethodInfo Method(object target, string name)
        {
            var method = target.GetType().GetMethod(name, Private);
            Assert.That(method, Is.Not.Null, "Missing method " + name);
            return method;
        }
        static object Invoke(object target, string name, params object[] args) => Method(target, name).Invoke(target, args);
    }
}
#endif
