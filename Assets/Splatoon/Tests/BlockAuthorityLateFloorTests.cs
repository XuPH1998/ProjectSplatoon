#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    // Written acceptance candidate, not executed. Copy into the integrated test assembly
    // only under the parent's test/run scope. Dynamic state comes exclusively from
    // QueueBuild / QueueDestroy / Tick; reflection only registers the test map collider.
    public sealed class BlockAuthorityLateFloorTests
    {
        const double Started = 10, RebuildAt = 10.05, SafeStageAt = 10.1, SafePublishAt = 10.15;
        static readonly Vector3Int FloorCell = new(2, 0, 2);
        GameObject _root;
        BlockTerrain _terrain;
        BlockWorld _originalWorld;
        FallingCohort _original;
        Vector3Int _sourceCell;
        byte[] _frozen;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Real authority late floor contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            var floor = new GameObject("Map floor with top zero");
            floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(BlockWorld.Center(FloorCell).x, -.05f,
                BlockWorld.Center(FloorCell).z);
            var collider = floor.AddComponent<BoxCollider>();
            collider.size = new Vector3(2, .1f, 2);
            var field = typeof(BlockTerrain).GetField("_staticColliders",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((HashSet<Collider>)field.GetValue(_terrain)).Add(collider);
            Physics.SyncTransforms();
            _terrain.Tick(true, Started, .016f);
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
        public IEnumerator RebuildingAnOccupiedFloorIsRejectedWithoutLiftingItsAuthoritySurvivor()
        {
            yield return SeedAuthorityRoot(1);
            int rebuilt = 0;
            BlockMutationResult rebuiltResult = default;
            Vector3 point = BlockWorld.Center(FloorCell) - Vector3.up * .051f;
            _terrain.Tick(true, RebuildAt, .016f);
            Assert.That(_original.Offset(RebuildAt), Is.EqualTo(.0122625f).Within(1e-6));
            float oldBottom = _sourceCell.y * BlockWorld.CellSize - _original.Offset(RebuildAt);
            float floorTop = (FloorCell.y + 1) * BlockWorld.CellSize;
            Assert.That(oldBottom, Is.LessThan(floorTop - 1e-6),
                "A normally proved root has already crossed the newly built floor.");
            _terrain.QueueBuild(point, Vector3.up, 1, 1, 0, false,
                result => { rebuilt++; rebuiltResult = result; });
            yield return TickUntil(RebuildAt, () => rebuilt == 1 && _terrain.PendingTransactions == 0);

            Assert.That(rebuilt, Is.EqualTo(1));
            Assert.That(rebuiltResult.Added, Is.Zero, "Current physical occupancy must reject the new floor.");
            Assert.That(_terrain.World.TryGet(FloorCell, out _), Is.False);
            Assert.That(SurvivorBottom(_terrain.World, _sourceCell, RebuildAt), Is.EqualTo(oldBottom).Within(1e-6));
            Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.PinkCount, Is.Zero);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SafeNewFloorPreservesActualPublicationMotionAndFutureContact()
        {
            yield return SeedAuthorityRoot(5);
            _terrain.Tick(true, SafeStageAt, .016f);
            int callbacks = 0;
            BlockMutationResult outcome = default;
            var added = FloorCell + Vector3Int.up;
            bool observed = false;
            FallingCohort published = null;
            Action<BlockTerrainCommit> observe = commit =>
            {
                if (observed || !_terrain.World.TryGet(added, out var owner) || owner.Owner != 1) return;
                observed = true;
                Assert.That(_terrain.SimulationTime, Is.EqualTo(SafePublishAt));
                published = _terrain.World.Cohorts.Values.Single(c => c.Bottom == _sourceCell);
                Assert.That(published.Phase, Is.EqualTo(FallingCohortPhase.Falling));
                Assert.That(published.Offset(SafePublishAt), Is.EqualTo(_original.Offset(SafePublishAt)).Within(1e-6));
                Assert.That(published.Speed(SafePublishAt), Is.EqualTo(_original.Speed(SafePublishAt)).Within(1e-6));
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(added) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                    result => { callbacks++; outcome = result; });
                _terrain.Tick(true, SafeStageAt, .016f);
                yield return TickUntil(SafePublishAt, () => callbacks == 1 && _terrain.PendingTransactions == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(observed, Is.True);
            Assert.That(outcome.Added, Is.EqualTo(1));
            Assert.That(published, Is.Not.Null);
            // Evaluate the first publication directly, without a later settle transaction.
            Assert.That(published.Offset(10.3), Is.EqualTo(.3f).Within(1e-6));
            Assert.That(published.Speed(10.3), Is.Zero.Within(1e-6));
            Assert.That(_sourceCell.y * BlockWorld.CellSize - published.Offset(10.3), Is.EqualTo(.2f).Within(1e-6));
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AStageSafeFloorIsRejectedAfterPublishedMotionAdvancesThroughIt()
        {
            yield return SeedAuthorityRoot(5);
            _terrain.Tick(true, SafeStageAt, .016f);
            long starts = _terrain.BuildOccupancyPreparationStarts;
            int callbacks = 0;
            BlockMutationResult outcome = default;
            var added = FloorCell + Vector3Int.up;
            float floorTop = (added.y + 1) * BlockWorld.CellSize;
            Assert.That(_sourceCell.y * BlockWorld.CellSize - _original.Offset(SafeStageAt), Is.GreaterThan(floorTop));
            _terrain.QueueBuild(BlockWorld.Center(added) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                result => { callbacks++; outcome = result; });
            yield return TickUntil(SafeStageAt, () => _terrain.BuildOccupancyPreparationStarts > starts
                && BuildPreparationActive());
            Assert.That(callbacks, Is.Zero);
            Assert.That(_sourceCell.y * BlockWorld.CellSize - _original.Offset(10.3), Is.LessThan(floorTop));
            // Source/revision are unchanged, but actual physical occupancy advances.
            // The retry must restore the command cursor and finish with Added0 once.
            yield return TickUntil(10.3, () => callbacks == 1 && _terrain.PendingTransactions == 0);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(outcome.Added, Is.Zero);
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AuthorityLossDuringBuildPreparationRestoresFifoWithoutDuplicateCommands()
        {
            yield return SeedAuthorityRoot(5);
            _terrain.Tick(true, SafeStageAt, .016f);
            long starts = _terrain.BuildOccupancyPreparationStarts;
            var order = new List<int>();
            var added = FloorCell + Vector3Int.up;
            var other = FloorCell + Vector3Int.right * 3;
            _terrain.QueueBuild(BlockWorld.Center(added) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                result => { order.Add(1); Assert.That(result.Added, Is.EqualTo(1)); });
            _terrain.QueueBuild(BlockWorld.Center(other) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                result => { order.Add(2); Assert.That(result.Added, Is.EqualTo(1)); });
            yield return TickUntil(SafeStageAt, () => _terrain.BuildOccupancyPreparationStarts > starts
                && BuildPreparationActive());
            Assert.That(order, Is.Empty);
            Assert.That(_terrain.World.TryGet(added, out _), Is.False);
            Assert.That(_terrain.World.TryGet(other, out _), Is.False);
            _terrain.Tick(false, SafeStageAt, .016f);
            Assert.That(BuildPreparationActive(), Is.False);
            Assert.That(order, Is.Empty);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
            yield return TickUntil(SafePublishAt, () => order.Count == 2 && _terrain.PendingTransactions == 0);
            CollectionAssert.AreEqual(new[] { 1, 2 }, order);
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(2));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AuthorityLossDuringGuardedPendingCannotPublishTheDraft()
        {
            yield return SeedAuthorityRoot(5);
            _terrain.Tick(true, SafeStageAt, .016f);
            int callbacks = 0;
            var added = FloorCell + Vector3Int.up;
            _terrain.QueueBuild(BlockWorld.Center(added) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                result => { callbacks++; Assert.That(result.Added, Is.EqualTo(1)); });
            yield return TickUntil(SafeStageAt, GuardedPendingActive);
            Assert.That(callbacks, Is.Zero);
            Assert.That(_terrain.World.TryGet(added, out _), Is.False);
            _terrain.Tick(false, SafePublishAt, .016f);
            Assert.That(GuardedPendingActive(), Is.False);
            Assert.That(_terrain.World.TryGet(added, out _), Is.False);
            Assert.That(callbacks, Is.Zero, "Authority loss must restore the unpublished command without an early completion.");
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
            yield return TickUntil(SafePublishAt, () => callbacks == 1 && _terrain.PendingTransactions == 0);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ABuildPublicationRetryReplaysAMixedDestructionExactlyOnce()
        {
            yield return SeedAuthorityRoot(5);
            var victim = FloorCell + Vector3Int.right * 3;
            int seeded = 0;
            _terrain.QueueBuild(BlockWorld.Center(victim) - Vector3.up * .051f, Vector3.up, 2, 1, 0, false,
                result => { seeded++; Assert.That(result.Added, Is.EqualTo(1)); });
            yield return TickUntil(Started, () => seeded == 1 && _terrain.PendingTransactions == 0);
            Assert.That(_terrain.BlueCount, Is.EqualTo(3));
            _terrain.Tick(true, SafeStageAt, .016f);
            long starts = _terrain.BuildOccupancyPreparationStarts;
            long retries = _terrain.BuildPublicationRetries;
            int buildCallbacks = 0, destroyCallbacks = 0;
            BlockMutationResult buildOutcome = default, destroyOutcome = default;
            var added = FloorCell + Vector3Int.up;
            _terrain.QueueBuild(BlockWorld.Center(added) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                result => { buildCallbacks++; buildOutcome = result; });
            _terrain.QueueDestroy(BlockWorld.Center(victim), .0001f, 0, 1, true,
                result => { destroyCallbacks++; destroyOutcome = result; }, 1, 0);
            yield return TickUntil(SafeStageAt, () => _terrain.BuildOccupancyPreparationStarts > starts
                && BuildPreparationActive());
            Assert.That(buildCallbacks + destroyCallbacks, Is.Zero);
            yield return TickUntil(10.3, () => buildCallbacks == 1 && destroyCallbacks == 1
                && _terrain.PendingTransactions == 0);
            Assert.That(_terrain.BuildPublicationRetries, Is.GreaterThan(retries));
            Assert.That(buildCallbacks, Is.EqualTo(1));
            Assert.That(destroyCallbacks, Is.EqualTo(1));
            Assert.That(buildOutcome.Added, Is.Zero);
            Assert.That(destroyOutcome.RemovedEnemy, Is.EqualTo(1));
            Assert.That(_terrain.World.TryGet(victim, out _), Is.False);
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ANewDynamicCellAboveTheActualRootCannotReverseItsSourceOrderAndLiftIt()
        {
            yield return SeedAuthorityRoot(5);
            // Select the real alternating command-first cadence through ordinary
            // fixed-time authority ticks; do not write scheduler or dynamic state.
            for (int step = 0; step < 8 && GravityFirst(); step++)
            {
                _terrain.Tick(true, Started, .016f);
                yield return null;
            }
            Assert.That(GravityFirst(), Is.False);
            var added = FloorCell + Vector3Int.up * 2;
            const double actualAt = 10.3;
            float oldBottom = _sourceCell.y * BlockWorld.CellSize - _original.Offset(actualAt);
            Assert.That(oldBottom, Is.EqualTo(.1f).Within(1e-6));
            Assert.That(added.y * BlockWorld.CellSize, Is.EqualTo(oldBottom + BlockWorld.CellSize).Within(1e-6));
            // This finite authority build touches the old root's actual top at.2.
            // New sourceY2 is physically ABOVE old sourceY5 after its.4 fall.
            int callbacks = 0;
            BlockMutationResult outcome = default;
            bool observed = false;
            float firstBottom = float.NaN;
            Action<BlockTerrainCommit> observe = commit =>
            {
                if (observed || callbacks == 0 || outcome.Added != 1) return;
                observed = true;
                firstBottom = BlueRootBottom(_terrain.World, actualAt);
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(added) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                    result => { callbacks++; outcome = result; });
                yield return TickUntil(actualAt, () => callbacks == 1 && _terrain.PendingTransactions == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(outcome.Added, Is.EqualTo(1), "The top-touching build has no physical volume intersection.");
            Assert.That(observed, Is.True);
            Assert.That(firstBottom, Is.EqualTo(oldBottom).Within(1e-6),
                "Source-coordinate ordering must not make a physically higher new cell support/lift the lower old root.");
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator MixedGuardedBuildPreservesCrossChunkCellsAndRechecksANewMotionColumn()
        {
            yield return SeedAuthorityRoot(5);
            _terrain.Tick(true, SafeStageAt, .016f);
            // Observe ordinary gravity-first cadence so new columns remain stable
            // in this publication, then acquire their own motion on later ticks.
            yield return TickUntil(SafeStageAt, () => _terrain.PendingTransactions == 0 && GravityFirst());
            var added = new[]
            {
                FloorCell + Vector3Int.up,
                FloorCell + Vector3Int.up + Vector3Int.left,
                FloorCell + Vector3Int.up + Vector3Int.left * 2,
                FloorCell + Vector3Int.up + Vector3Int.left * 3
            };
            Assert.That(BlockWorld.ChunkAt(added[0]), Is.Not.EqualTo(BlockWorld.ChunkAt(added[3])));
            Assert.That((bool)ReadWitness(_terrain, "_cohortIndexPending"), Is.False);
            Assert.That(HasMotionHead(added[0]), Is.True);
            foreach (var cell in added.Skip(1)) Assert.That(HasMotionHead(cell), Is.False);
            uint emptyColumnRevision = _terrain.Revision;
            int callbacks = 0, overlapCallbacks = 0;
            BlockMutationResult outcome = default, overlapOutcome = default;
            FallingCohort published = null;
            bool observed = false;
            Action<BlockTerrainCommit> observe = commit =>
            {
                if (observed || callbacks == 0) return;
                observed = true;
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(outcome.Added, Is.EqualTo(added.Length));
                Assert.That(_terrain.SimulationTime, Is.EqualTo(SafePublishAt));
                AssertStableCollider(added[0], 1);
                // Earlier paid segments may already have detached an unsupported
                // slot. At this frozen instant its real motion still has zero offset.
                foreach (var cell in added.Skip(1)) AssertPublishedCell(cell, 1);
                Assert.That(_terrain.PinkCount, Is.EqualTo(4));
                Assert.That(_terrain.BlueCount, Is.EqualTo(2));
                published = _terrain.World.Cohorts.Values.Single(c => c.Bottom == _sourceCell);
                Assert.That(published.Offset(SafePublishAt), Is.EqualTo(_original.Offset(SafePublishAt)).Within(1e-6));
                Assert.That(published.Speed(SafePublishAt), Is.EqualTo(_original.Speed(SafePublishAt)).Within(1e-6));
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(added[0]) + Vector3.right * .051f, Vector3.left,
                    1, added.Length, 0, false, result => { callbacks++; outcome = result; });
                yield return TickUntil(SafeStageAt, BuildPreparationActive);
                var preparation = ReadWitness(_terrain, "_buildOccupancyPreparation");
                var slots = ((IEnumerable)ReadWitness(preparation, "Slots")).Cast<object>()
                    .Select(slot => (Vector3Int)ReadWitness(slot, "Cell")).ToArray();
                // Stopwatch admission may split this attack across publications.
                // The first real guard must retain actual attack slots; the final
                // callback and settled colliders below prove the complete attack.
                Assert.That(slots, Is.Not.Empty);
                foreach (var cell in slots) Assert.That(added.Contains(cell), Is.True);
                Assert.That(ReadWitness(preparation, "Source"), Is.SameAs(_terrain.World));
                yield return TickUntil(SafePublishAt, () => callbacks == 1 && _terrain.PendingTransactions == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(observed, Is.True);
            Assert.That(published, Is.Not.Null);
            Assert.That(published.Offset(10.3), Is.EqualTo(.3f).Within(1e-6));
            Assert.That(published.Speed(10.3), Is.Zero.Within(1e-6));
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());

            var newSource = added[3];
            yield return TickUntil(SafePublishAt, () => _terrain.World.Cohorts.Values.Any(c =>
                c.Bottom == newSource && c.Phase == FallingCohortPhase.Falling));
            var newMotion = _terrain.World.Cohorts.Values.Single(c => c.Bottom == newSource);
            Assert.That(newMotion.StartedAt, Is.EqualTo(SafePublishAt));
            Assert.That(newMotion.LandingKnown, Is.True);
            Assert.That(newMotion.LandingOffset, Is.EqualTo(.1f).Within(1e-6));
            Assert.That(HasMotionHead(newSource), Is.True);
            Assert.That(_terrain.Revision, Is.GreaterThan(emptyColumnRevision));
            const double overlapAt = SafePublishAt + .05, settledAt = SafePublishAt + .4;
            float oldBottom = newSource.y * BlockWorld.CellSize - newMotion.Offset(overlapAt);
            var overlapCell = newSource + Vector3Int.down;
            Assert.That(oldBottom, Is.GreaterThan(overlapCell.y * BlockWorld.CellSize));
            Assert.That(oldBottom, Is.LessThan((overlapCell.y + 1) * BlockWorld.CellSize));
            _terrain.Tick(true, overlapAt, .016f);
            _terrain.QueueBuild(BlockWorld.Center(overlapCell) - Vector3.up * .051f, Vector3.up,
                1, 1, 0, false, result => { overlapCallbacks++; overlapOutcome = result; });
            yield return TickUntil(overlapAt, () => overlapCallbacks == 1 && _terrain.PendingTransactions == 0);
            Assert.That(overlapCallbacks, Is.EqualTo(1));
            Assert.That(overlapOutcome.Added, Is.Zero,
                "A new source must inspect this now-moving column instead of reusing its earlier empty-head classification.");
            Assert.That(_terrain.World.TryGet(overlapCell, out _), Is.False);
            Assert.That(SurvivorBottom(_terrain.World, newSource, overlapAt), Is.EqualTo(oldBottom).Within(1e-6));
            Assert.That(_terrain.PinkCount, Is.EqualTo(4));
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            yield return TickUntil(settledAt, () => _terrain.PendingTransactions == 0 && _terrain.World.Falling.Count == 0
                && added.Skip(1).All(cell => _terrain.World.TryGet(cell + Vector3Int.down, out var value) && value.Owner == 1));
            AssertStableCollider(added[0], 1);
            foreach (var cell in added.Skip(1)) AssertStableCollider(cell + Vector3Int.down, 1);
            AssertStableCollider(FloorCell, 2);
            AssertStableCollider(FloorCell + Vector3Int.up * 2, 2);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(overlapCallbacks, Is.EqualTo(1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(4));
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(_frozen, _originalWorld.EncodeSnapshot());

            static object ReadWitness(object owner, string name)
            {
                var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null, "Read-only real authority witness: " + name);
                return field.GetValue(owner);
            }
            bool HasMotionHead(Vector3Int cell)
            {
                var heads = (IDictionary)ReadWitness(_terrain, "_cohortHeads");
                var column = new Vector2Int(cell.x, cell.z);
                return heads.Contains(column) && ((IEnumerable)heads[column]).Cast<object>().Any();
            }
            void AssertPublishedCell(Vector3Int cell, byte owner)
            {
                if (_terrain.World.TryGet(cell, out var stable))
                {
                    Assert.That(stable.Owner, Is.EqualTo(owner));
                    return;
                }
                int matches = 0;
                foreach (var run in _terrain.World.Falling.Values)
                {
                    if (run.Bottom.x != cell.x || run.Bottom.z != cell.z) continue;
                    int index = cell.y - run.Bottom.y;
                    if (index < 0 || index >= run.Cells.Length || !run.Cells[index].Occupied) continue;
                    matches++;
                    Assert.That(run.Cells[index].Owner, Is.EqualTo(owner));
                    Assert.That(run.Offset(_terrain.SimulationTime), Is.Zero.Within(1e-6));
                }
                Assert.That(matches, Is.EqualTo(1), "A published slot must exist once as stable or actual zero-offset motion.");
            }
            void AssertStableCollider(Vector3Int cell, byte owner)
            {
                Assert.That(_terrain.World.TryGet(cell, out var value) && value.Owner == owner, Is.True);
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.forward * .08f, Vector3.back,
                    out var hit, .16f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
                Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
                Assert.That(contact.Stable && contact.Owner == owner && contact.Cell == cell, Is.True);
                Assert.That(contact.Revision, Is.EqualTo(_terrain.Revision));
            }
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator PaidFloorAtRoundedContactPreservesWirePaymentsAndFinalColliders()
        {
            // Select the ordinary gravity-first cadence before installing the
            // source. No scheduler, pending, journal or cap is written by reflection.
            if (!GravityFirst())
            {
                int calibrationCallbacks = 0;
                long calibrationStages = _terrain.AuthorityStageCount;
                long calibrationCandidates = _terrain.BuildCandidatesVisited;
                var calibration = new BlockAttackToken(new Splatoon.Combat.PaintCredit(
                    970001, 1, _terrain.Round, 1, Splatoon.Combat.PaintAttackKind.Main, true), 0, 32, 8, 2);
                int calibrationReserved = calibration.ReserveBuild(1, false);
                Assert.That(calibrationReserved, Is.EqualTo(1));
                // Outside the normal axis: the first MoveNext is exhaustion,
                // so a cold candidate iterator cannot split two visits across stages.
                _terrain.QueueBuild(new Vector3(0, 1000, 0), Vector3.up, 1, calibrationReserved, 0, false,
                    result =>
                    {
                        Assert.That(++calibrationCallbacks, Is.EqualTo(1));
                        Assert.That(result.Added, Is.Zero);
                        calibration.CompleteBuild(calibrationReserved, false, result.Added);
                    }, ordinaryMainAttack: true);
                yield return TickUntil(Started, () => calibrationCallbacks == 1 && _terrain.PendingTransactions == 0);
                Assert.That(calibration.Added, Is.Zero); Assert.That(calibration.BuildRemaining, Is.EqualTo(32));
                Assert.That(_terrain.AuthorityStageCount - calibrationStages, Is.EqualTo(1),
                    "The empty iterator must consume exactly one real authority stage.");
                Assert.That(_terrain.BuildCandidatesVisited, Is.EqualTo(calibrationCandidates),
                    "Normal-axis clipping must finish without yielding an outside candidate.");
            }
            Assert.That(GravityFirst(), Is.True, "A real empty-result command must select the next gravity-first stage.");

            var seed = new BlockWorld(new Vector2(32, 64));
            var upperCell = new Vector3Int(FloorCell.x, 10, FloorCell.z);
            var lowerCell = new Vector3Int(FloorCell.x, 3, FloorCell.z);
            var addedCell = new Vector3Int(FloorCell.x, 2, FloorCell.z);
            float rawUpper = BitConverter.Int32BitsToSingle(0x3f333334);
            float canonicalCap = 7 * BlockWorld.CellSize;
            Assert.That(BitConverter.SingleToInt32Bits(canonicalCap), Is.EqualTo(0x3f333333));
            // Upper-first creation makes it the first ordinary indexed motion.
            // These are legal standalone descriptors with their own map-floor caps.
            ulong upperId = AddSeed(upperCell, rawUpper, 10 * BlockWorld.CellSize);
            ulong lowerId = AddSeed(lowerCell, .2f, 3 * BlockWorld.CellSize);
            seed.Seal(1, seed.RevisionKeys.ToArray());
            byte[] seedBytes = seed.EncodeSnapshot();
            CollectionAssert.AreEqual(seedBytes, BlockWorld.DecodeSnapshot(seedBytes).EncodeSnapshot());
            Assert.That(_terrain.ApplyRemoteSnapshot(seedBytes), Is.True);
            for (int frame = 0; frame < 2000 && (_terrain.Revision != 1 || _terrain.PendingTransactions != 0); frame++)
            { _terrain.Tick(false, Started, .016f); yield return null; }
            Assert.That(_terrain.Revision, Is.EqualTo(1)); Assert.That(_terrain.PendingTransactions, Is.Zero);
            CollectionAssert.AreEqual(seedBytes, _terrain.ExportSnapshot());
            Assert.That(GravityFirst(), Is.True, "Remote installation must not consume an authority phase.");
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot(), previous = frozen;
            uint previousRevision = source.Revision;
            Assert.That(source.Cohorts[upperId].LowerSupport, Is.Null);
            Assert.That(source.Cohorts[lowerId].LowerSupport, Is.Null);
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(2));

            var token = new BlockAttackToken(new Splatoon.Combat.PaintCredit(
                970002, 1, _terrain.Round, 1, Splatoon.Combat.PaintAttackKind.Main, true), 0, 32, 8, 2);
            int reserved = token.ReserveBuild(1, false), callbacks = 0, publications = 0;
            Assert.That(reserved, Is.EqualTo(1));
            BlockMutationResult paidResult = default;
            object paidCommand = null;
            bool paidPublished = false, overlapWitnessed = false;
            var observedPendings = new List<object>();
            Action<BlockTerrainCommit> observe = commit =>
            {
                var current = _terrain.World;
                Assert.That(commit.Revision, Is.EqualTo(current.Revision));
                Assert.That(commit.Revision, Is.GreaterThan(previousRevision));
                byte[] snapshot = current.EncodeSnapshot();
                var full = BlockWorld.DecodeSnapshot(snapshot);
                var cumulative = BlockWorld.DecodeSnapshot(frozen).WithDelta(current.EncodeDelta(source.Revision));
                var consecutive = BlockWorld.DecodeSnapshot(previous).WithDelta(current.EncodeDelta(previousRevision));
                CollectionAssert.AreEqual(snapshot, full.EncodeSnapshot(), "Every actual publication must pass the unchanged strict snapshot decoder.");
                CollectionAssert.AreEqual(snapshot, cumulative.EncodeSnapshot(), "The frozen-source delta must reconstruct every actual publication exactly.");
                CollectionAssert.AreEqual(snapshot, consecutive.EncodeSnapshot(), "The consecutive delta must reconstruct every actual publication exactly.");
                foreach (var copy in new[] { current, full, cumulative, consecutive })
                {
                    Assert.That(copy.PinkCount, Is.EqualTo(token.Added)); Assert.That(copy.BlueCount, Is.EqualTo(2));
                    var cells = copy.Chunks.Values.SelectMany(chunk => chunk.Cells)
                        .Concat(copy.Falling.Values.SelectMany(run => run.Cells)).Where(cell => cell.Occupied).ToArray();
                    Assert.That(cells.Count(cell => cell.Owner == 1), Is.EqualTo(token.Added));
                    Assert.That(cells.Count(cell => cell.Owner == 2), Is.EqualTo(2));
                    Assert.That(cells.Length, Is.EqualTo(2 + token.Added));
                }
                CollectionAssert.AreEqual(frozen, source.EncodeSnapshot(), "The original published source remains immutable.");
                if (!paidPublished && callbacks != 0)
                {
                    // Publish clears terrain._pending before this event. The
                    // saved real object now contains its final preflight values.
                    var pending = observedPendings.SingleOrDefault(candidate =>
                        ReferenceEquals(Read<BlockWorld>(candidate, "Next"), current));
                    Assert.That(pending, Is.Not.Null, "The paid publication must be bound to its saved real guarded pending.");
                    var results = ((IEnumerable)Read<object>(pending, "Results")).Cast<object>().ToArray();
                    Assert.That(results.Any(result => ReferenceEquals(Read<object>(result, "Item1"), paidCommand)), Is.True,
                        "The witnessed pending must contain this actual reserved paid command.");
                    var buildGuard = Read<object>(pending, "BuildGuard");
                    Assert.That(Read<BlockWorld>(buildGuard, "Source"), Is.SameAs(source));
                    var gravity = Read<object>(pending, "Gravity");
                    var continuity = Read<IDictionary>(gravity, "BindingContinuity");
                    var caps = Read<IDictionary>(buildGuard, "Caps");
                    object buildCap = null;
                    foreach (DictionaryEntry pair in caps)
                    {
                        var group = Read<FallingCohort>(pair.Value, "Group");
                        if (group != null && group.Id == upperId)
                        { Assert.That(buildCap, Is.Null); buildCap = pair.Value; }
                    }
                    Assert.That(buildCap, Is.Not.Null, "The paid floor must constrain the same real upper ID.");
                    Assert.That(Read<bool>(buildCap, "PublicationApplies"), Is.True,
                        "The paid floor requires a finite applicable cap, independently of continuity scheduling.");
                    AssertBits(Read<float>(buildCap, "Cap"), 0x3f333333, "finite build cap");
                    AssertBits(Read<float>(Read<object>(buildCap, "Sample"), "Offset"), 0x3f333334, "immutable build raw sample");
                    AssertBits(Read<float>(buildCap, "PublicationOffset"), 0x3f333333, "build selected pose");
                    AssertBits(Read<float>(buildCap, "PublicationCap"), 0x3f333333, "build final cap");
                    var upper = current.Cohorts[upperId];
                    Assert.That(Read<FallingCohort>(buildCap, "Group"), Is.SameAs(upper));
                    // The exact same-pending intersection is a coverage diagnostic.
                    // Actual cap, raw build sample, published pose and all functional oracles stay mandatory.
                    overlapWitnessed = continuity.Contains(upperId);
                    if (overlapWitnessed)
                    {
                        object guard = continuity[upperId];
                        Assert.That(guard, Is.Not.Null);
                        AssertBits(Read<float>(guard, "Offset"), 0x3f333334, "immutable continuity raw sample");
                        AssertBits(Read<float>(guard, "PublicationOffset"), 0x3f333333, "continuity selected pose");
                        AssertBits(Read<float>(guard, "PublicationCap"), 0x3f333333, "continuity final cap");
                        Assert.That(Read<FallingCohort>(guard, "Next"), Is.SameAs(upper));
                        Assert.That(Read<float>(guard, "PublicationOffset"), Is.EqualTo(Read<float>(buildCap, "PublicationOffset")),
                            "When both real writers exist, they must select the same publication pose.");
                        Assert.That(upper.LowerSupport, Is.Not.Null); Assert.That(upper.LowerSupport.Id, Is.EqualTo(lowerId));
                        Assert.That(upper.LowerSupport.Offset(Started), Is.EqualTo(.2f));
                        Assert.That(upper.LowerSupport.Offset(Started) + (10 - 4) * BlockWorld.CellSize, Is.GreaterThan(canonicalCap),
                            "The new finite build floor, rather than the farther live support, must select this contact.");
                    }
                    else TestContext.WriteLine("same-pending overlap NOT OBSERVED; no claim of two-writer branch coverage.");
                    AssertBits(upper.InitialOffset, 0x3f333333, "published initial pose");
                    AssertBits(upper.LandingOffset, 0x3f333333, "published landing cap");
                    Assert.That(upper.InitialSpeed, Is.Zero);
                    Assert.That(upperCell.y * BlockWorld.CellSize - upper.Offset(Started),
                        Is.GreaterThanOrEqualTo((addedCell.y + 1) * BlockWorld.CellSize));
                    Assert.That(current, Is.Not.SameAs(source));
                    Assert.That(callbacks, Is.EqualTo(1)); Assert.That(paidResult.Added, Is.EqualTo(1));
                    Assert.That(paidResult.RemovedEnemy + paidResult.RemovedFriendly, Is.Zero);
                    Assert.That(paidResult.OrdinaryMainAttack, Is.True);
                    Assert.That(token.Added, Is.EqualTo(1)); Assert.That(token.BuildRemaining, Is.EqualTo(31));
                    Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(32));
                    AssertNativeCell(addedCell, 1);
                    paidPublished = true;
                }
                previous = snapshot; previousRevision = current.Revision; publications++;
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(addedCell) - Vector3.up * .051f, Vector3.up,
                    1, reserved, 0, false, result =>
                    {
                        Assert.That(++callbacks, Is.EqualTo(1), "One reserved payment must complete only once.");
                        paidResult = result; token.CompleteBuild(reserved, false, result.Added);
                    }, ordinaryMainAttack: true);
                paidCommand = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Last();
                for (int frame = 0; frame < 2000 && !paidPublished; frame++)
                {
                    RememberPending(); _terrain.Tick(true, Started, .016f); RememberPending();
                    yield return null;
                }
                Assert.That(paidPublished, Is.True,
                    "The actual reserved paid command must publish its real finite cap, strict wire and native collider; no private state injection is permitted.");
                for (int frame = 0; frame < 2000 && !Drained(); frame++)
                {
                    RememberPending(); _terrain.Tick(true, Math.Min(Started + 2, Started + (frame + 1) * .01), .016f); RememberPending();
                    yield return null;
                }
                Assert.That(Drained(), Is.True, "All three actual cells must complete normal gravity and native publication.");
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(publications, Is.GreaterThan(0)); Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(token.Added, Is.EqualTo(1)); Assert.That(token.BuildRemaining, Is.EqualTo(31));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            AssertNativeCell(FloorCell, 2); AssertNativeCell(FloorCell + Vector3Int.up, 1);
            AssertNativeCell(FloorCell + Vector3Int.up * 2, 2);
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            TestContext.WriteLine("same-pending overlap witnessed=" + overlapWitnessed + "; paid publication and complete functional oracles passed.");

            ulong AddSeed(Vector3Int cell, float offset, float landing)
            {
                seed.Set(cell, BlockCell.Create(2));
                var group = seed.BeginCohort(cell, 1);
                seed.DetachCohortPiece(group.Id, 1, Started, out _);
                group.LandingKnown = true; group.LandingOffset = landing;
                seed.PreparePendingCohortsForPublication(out _); seed.ActivateRebasePendingCohorts(Started);
                group = seed.EditCohort(group.Id, out _);
                group.StartedAt = Started; group.InitialOffset = offset; group.InitialSpeed = 0;
                group.LandingKnown = true; group.LandingOffset = landing;
                return group.Id;
            }
            void RememberPending()
            {
                var pending = Read<object>(_terrain, "_pending");
                if (pending != null && Read<object>(pending, "BuildGuard") != null && !observedPendings.Contains(pending))
                    observedPendings.Add(pending);
            }
            bool Drained() => _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0
                && _terrain.World.Falling.Count == 0 && _terrain.World.Cohorts.Count == 0;
            void AssertNativeCell(Vector3Int cell, byte owner)
            {
                Assert.That(_terrain.World.TryGet(cell, out var value) && value.Owner == owner, Is.True);
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.forward * .08f, Vector3.back,
                    out var hit, .16f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
                Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
                Assert.That(contact.Stable && contact.Owner == owner && contact.Cell == cell, Is.True);
                Assert.That(contact.Revision, Is.EqualTo(_terrain.Revision));
            }
            static void AssertBits(float actual, int expected, string witness)
                => Assert.That(BitConverter.SingleToInt32Bits(actual), Is.EqualTo(expected), witness);
            static T Read<T>(object owner, string field)
            {
                Assert.That(owner, Is.Not.Null, "Read-only real publication witness owner: " + field);
                var member = owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(member, Is.Not.Null, "Read-only real publication witness field: " + field);
                return (T)member.GetValue(owner);
            }
        }

        IEnumerator SeedAuthorityRoot(int height)
        {
            _sourceCell = FloorCell + Vector3Int.up * height;
            int built = 0, destroyed = 0;
            BlockMutationResult buildResult = default, destroyResult = default;
            _terrain.QueueBuild(BlockWorld.Center(FloorCell) - Vector3.up * .051f, Vector3.up, 2,
                height + 1, 0, false, result => { built++; buildResult = result; });
            yield return TickUntil(Started, () => built == 1 && _terrain.PendingTransactions == 0);
            Assert.That(buildResult.Added, Is.EqualTo(height + 1));
            Assert.That(_terrain.World.TryGet(FloorCell, out _), Is.True);
            Assert.That(_terrain.World.TryGet(_sourceCell, out _), Is.True);
            Assert.That(_terrain.World.Falling.Count, Is.Zero);
            int removed = height == 1 ? 1 : 4;
            Vector3 hit = height == 1 ? BlockWorld.Center(FloorCell)
                : new Vector3(BlockWorld.Center(FloorCell).x, .3f, BlockWorld.Center(FloorCell).z);
            _terrain.QueueDestroy(hit, height == 1 ? .0001f : .1001f, 0, 1, true,
                result => { destroyed++; destroyResult = result; }, removed, 0);
            yield return TickUntil(Started, () => destroyed == 1 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => c.Bottom == _sourceCell
                    && c.Phase == FallingCohortPhase.Falling));
            Assert.That(destroyResult.RemovedEnemy, Is.EqualTo(removed));
            _originalWorld = _terrain.World;
            _original = _originalWorld.Cohorts.Values.Single(c => c.Bottom == _sourceCell);
            Assert.That(_original.LandingKnown, Is.True);
            Assert.That(_original.LandingOffset, Is.EqualTo(height == 1 ? .1f : .4f).Within(1e-6));
            Assert.That(_original.StartedAt, Is.EqualTo(Started));
            _frozen = _originalWorld.EncodeSnapshot();
        }

        bool BuildPreparationActive() => typeof(BlockTerrain).GetField("_buildOccupancyPreparation",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_terrain) != null;

        bool GuardedPendingActive()
        {
            var pending = typeof(BlockTerrain).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_terrain);
            return pending != null && pending.GetType().GetField("BuildGuard")?.GetValue(pending) != null;
        }

        bool GravityFirst() => (bool)typeof(BlockTerrain).GetField("_gravityFirst",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_terrain);

        static float BlueRootBottom(BlockWorld world, double now)
        {
            float bottom = float.PositiveInfinity;
            for (int y = 1; y < 16; y++)
                if (world.TryGet(new Vector3Int(FloorCell.x, y, FloorCell.z), out var stable) && stable.Owner == 2)
                    bottom = Mathf.Min(bottom, y * BlockWorld.CellSize);
            foreach (var run in world.Falling.Values)
            {
                if (run.Bottom.x != FloorCell.x || run.Bottom.z != FloorCell.z) continue;
                for (int index = 0; index < run.Cells.Length; index++)
                    if (run.Cells[index].Occupied && run.Cells[index].Owner == 2)
                        bottom = Mathf.Min(bottom, (run.Bottom.y + index) * BlockWorld.CellSize - run.Offset(now));
            }
            Assert.That(float.IsFinite(bottom), Is.True, "The old Blue survivor must still exist exactly once.");
            return bottom;
        }

        IEnumerator TickUntil(double now, Func<bool> ready)
        {
            for (int step = 0; step < 2000 && !ready(); step++)
            {
                _terrain.Tick(true, now, .016f);
                yield return null;
            }
            Assert.That(ready(), Is.True, "Bounded acceptance pump did not reach its observable state.");
        }

        static float SurvivorBottom(BlockWorld world, Vector3Int sourceCell, double now)
        {
            if (world.TryGet(sourceCell, out _)) return sourceCell.y * BlockWorld.CellSize;
            foreach (var run in world.Falling.Values)
            {
                if (run.Bottom.x != sourceCell.x || run.Bottom.z != sourceCell.z) continue;
                int index = sourceCell.y - run.Bottom.y;
                if (index >= 0 && index < run.Cells.Length && run.Cells[index].Occupied)
                    return sourceCell.y * BlockWorld.CellSize - run.Offset(now);
            }
            Assert.Fail("The old authority survivor disappeared instead of publishing exactly once.");
            return float.NaN;
        }
    }
}
#endif
