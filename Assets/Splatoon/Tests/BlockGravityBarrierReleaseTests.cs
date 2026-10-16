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
    // Reports-only acceptance candidate: not compiled or executed.
    // The cohort cases enter exclusively through QueueBuild / QueueDestroy / Tick.
    // The legacy cases import a public BlockWorld.Detach / RemoveFallingCell seed
    // before entering the same real authority build and retirement path.
    // Reflection registers the map collider and READS the alternating scheduler.
    // It never calls a private gravity method or writes scheduler/barrier state.
    public sealed class BlockGravityBarrierReleaseTests
    {
        const double Epoch = 10, CohortBuildAt = 10.25, LegacyBuildAt = 10.32;
        const int PumpLimit = 3000;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly Vector3Int Floor = new(2, 0, 2);
        static readonly Vector3Int NewCell = new(2, 3, 2);
        GameObject _root;
        BlockTerrain _terrain;
        BlockWorld _retainedSource;
        byte[] _retainedBytes;
        FallingRun _oldRun;
        ulong _oldRunId, _oldCohortId;
        Vector3Int _oldCell;
        int _buildCallbacks, _destroyCallbacks;
        BlockMutationResult _buildResult, _destroyResult;
        bool _phantomObserved;
        double _observedPinkActivationAt = double.NaN;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Natural source inversion barrier release contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
            var floor = new GameObject("Registered map floor top zero");
            floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(BlockWorld.Center(Floor).x, -.05f,
                BlockWorld.Center(Floor).z);
            var collider = floor.AddComponent<BoxCollider>();
            collider.size = new Vector3(2, .1f, 2);
            var field = typeof(BlockTerrain).GetField("_staticColliders", Private);
            Assert.That(field, Is.Not.Null);
            ((HashSet<Collider>)field.GetValue(_terrain)).Add(collider);
            Physics.SyncTransforms();
            _terrain.Tick(true, Epoch, .016f);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ExistingMovingCohortSettlesBeforeTheInvertedNewBuildActivates()
        {
            yield return SeedNaturalCohort();
            yield return BuildInversion(CohortBuildAt);
            yield return HoldTheRealOccupiedBarrier(CohortBuildAt, false);
            yield return AwaitNewPinkMotion(10.35, true);
            yield return FinishAndCheck(10.7, true);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator DestroyingTheOldCohortLeavesAPhantomHeadThatCannotKeepTheNewBuildWeightless()
        {
            yield return PhantomRetirementOracle(false);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator LeadingLegacyHolesDoNotReleaseAnOccupiedBodyOrHoldTheBarrierAfterItSettles()
        {
            yield return SeedLegacyWithTwoLeadingHoles();
            yield return BuildInversion(LegacyBuildAt);
            yield return HoldTheRealOccupiedBarrier(LegacyBuildAt, true);
            yield return AwaitNewPinkMotion(10.4, true);
            yield return FinishAndCheck(10.7, true);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator DestroyingTheLastOccupiedLegacyCellReleasesItsPhantomHeadAndPreservesTheNewBuild()
        {
            yield return PhantomRetirementOracle(true);
        }

        IEnumerator PhantomRetirementOracle(bool legacy)
        {
            // Target preparation may consume more than one unchanged production
            // command slice. Require a real phantom witness, but allow bounded
            // fresh natural episodes instead of assuming one wall-time slice.
            for (int episode = 0; episode < 8; episode++)
            {
                double now = legacy ? LegacyBuildAt : CohortBuildAt;
                if (legacy) yield return SeedLegacyWithTwoLeadingHoles();
                else yield return SeedNaturalCohort();
                yield return BuildInversion(now);
                yield return HoldTheRealOccupiedBarrier(now, legacy);
                yield return DestroyOldBodyAndObservePhantom(now);
                yield return AwaitNewPinkMotion(now, false);
                yield return FinishAndCheck(10.7, false);
                if (_phantomObserved) yield break;
                // A command-first episode may clear the head before publication;
                // that legal outcome has still passed its retirement/liveness
                // assertions. Start another public round solely for coverage.
                _terrain.Clear(_terrain.Round + 1);
                _buildCallbacks = _destroyCallbacks = 0;
                _buildResult = _destroyResult = default;
                _observedPinkActivationAt = double.NaN;
                yield return null;
                _terrain.Tick(true, Epoch, .016f);
            }
            Assert.Fail("Eight natural retirement episodes did not publish a phantom witness; the phantom oracle coverage gate was not reached.");
        }

        IEnumerator SeedNaturalCohort()
        {
            int built = 0, destroyed = 0;
            _oldCell = Floor + Vector3Int.up * 5;
            _terrain.QueueBuild(BuildPoint(Floor), Vector3.up, 2, 6, 0, false,
                result => { built++; Assert.That(result.Added, Is.EqualTo(6)); });
            yield return Pump(Epoch, true, () => built == 1 && _terrain.PendingTransactions == 0);
            Assert.That(_terrain.World.Falling.Count, Is.Zero);
            _terrain.QueueDestroy(new Vector3(BlockWorld.Center(Floor).x, .3f,
                BlockWorld.Center(Floor).z), .1001f, 0, 1, true,
                result => { destroyed++; Assert.That(result.RemovedEnemy, Is.EqualTo(4)); }, 4, 0);
            yield return Pump(Epoch, true, () => destroyed == 1 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => c.Bottom == _oldCell
                    && c.Phase == FallingCohortPhase.Falling));
            var cohort = _terrain.World.Cohorts.Values.Single(c => c.Bottom == _oldCell);
            _oldCohortId = cohort.Id;
            _oldRunId = cohort.MemberIds.Single();
            _oldRun = _terrain.World.Falling[_oldRunId];
            Assert.That(cohort.StartedAt, Is.EqualTo(Epoch));
            Assert.That(cohort.LandingKnown, Is.True);
            Assert.That(cohort.LandingOffset, Is.EqualTo(.4f).Within(1e-6));
            RetainSource();
        }

        IEnumerator SeedLegacyWithTwoLeadingHoles()
        {
            int built = 0;
            _terrain.QueueBuild(BuildPoint(Floor), Vector3.up, 2, 1, 0, false,
                result => { built++; Assert.That(result.Added, Is.EqualTo(1)); });
            yield return Pump(Epoch, true, () => built == 1 && _terrain.PendingTransactions == 0);
            var seed = _terrain.World.Clone();
            var legacyBottom = Floor + Vector3Int.up * 5;
            for (int index = 0; index < 3; index++)
                seed.Set(legacyBottom + Vector3Int.up * index, BlockCell.Create(2));
            var run = seed.Detach(legacyBottom, 3, Epoch);
            Assert.That(seed.RemoveFallingCell(run.Id, 0, out _), Is.True);
            Assert.That(seed.RemoveFallingCell(run.Id, 1, out _), Is.True);
            run = seed.EditFalling(run.Id);
            run.LandingKnown = true;
            run.LandingOffset = .6f;
            _oldRunId = run.Id;
            _oldCohortId = 0;
            _oldCell = legacyBottom + Vector3Int.up * 2;
            seed.PrepareLegacyRunEpochsForPublication(out _);
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return Pump(Epoch, false, () => _terrain.PendingTransactions == 0);
            _oldRun = _terrain.World.Falling[_oldRunId];
            Assert.That(_oldRun.Cohort, Is.Null);
            Assert.That(_oldRun.Cells.Length, Is.EqualTo(3));
            Assert.That(_oldRun.Cells[0].Occupied || _oldRun.Cells[1].Occupied, Is.False);
            Assert.That(_oldRun.Cells[2].Owner, Is.EqualTo(2));
            Assert.That(_oldRun.StartedAt, Is.EqualTo(Epoch));
            RetainSource();
        }

        void RetainSource()
        {
            _retainedSource = _terrain.World;
            _retainedBytes = _retainedSource.EncodeSnapshot();
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.BuildGravityBarrierColumns, Is.Zero);
        }

        IEnumerator BuildInversion(double now)
        {
            // The new Y3 body is physically ABOVE the old body but below its
            // original source address Y5 (cohort) or occupied Y7 (legacy).
            float oldBottom = _oldCell.y * BlockWorld.CellSize - _oldRun.Offset(now);
            Assert.That(_oldRun.Offset(now), Is.LessThan(_oldRun.LandingOffset));
            Assert.That(NewCell.y, Is.LessThan(_oldCell.y));
            Assert.That(NewCell.y * BlockWorld.CellSize,
                Is.GreaterThan(oldBottom + BlockWorld.CellSize + .000001f));
            long deferrals = _terrain.BuildSourceOrderDeferrals;
            bool observed = false;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                if (observed || _buildCallbacks == 0) return;
                observed = true;
                Assert.That(_buildCallbacks, Is.EqualTo(1));
                Assert.That(_buildResult.Added, Is.EqualTo(1));
                Assert.That(_terrain.BuildSourceOrderDeferrals, Is.EqualTo(deferrals + 1));
                Assert.That(_terrain.BuildGravityBarrierColumns, Is.EqualTo(1));
                AssertPinkStillStable();
                AssertOldBluePosition(now);
                Assert.That(_terrain.World.Falling.ContainsKey(_oldRunId), Is.True);
                AssertRetainedSource();
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BuildPoint(NewCell), Vector3.up, 1, 1, 0, false,
                    result => { _buildCallbacks++; _buildResult = result; });
                yield return Pump(now, true, () => observed);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(observed, Is.True, "The real authority build must publish the inversion witness.");
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
        }

        IEnumerator HoldTheRealOccupiedBarrier(double now, bool legacy)
        {
            long visits = _terrain.BuildGravityBarrierVisits;
            for (int frame = 0; frame < 24; frame++)
            {
                _terrain.Tick(true, now, .016f);
                yield return null;
                Assert.That(_terrain.BuildGravityBarrierColumns, Is.EqualTo(1),
                    "The old body is still real and moving; leading holes cannot count as retirement.");
                AssertPinkStillStable();
                AssertOldBluePosition(now);
                if (legacy)
                {
                    var live = _terrain.World.Falling[_oldRunId];
                    Assert.That(live.Cells.Take(2).Any(cell => cell.Occupied), Is.False);
                    Assert.That(live.Cells[2].Occupied, Is.True);
                }
            }
            Assert.That(_terrain.BuildGravityBarrierVisits, Is.GreaterThan(visits),
                "Ordinary admitted column visits must actually scan the live barrier.");
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            AssertRetainedSource();
        }

        IEnumerator DestroyOldBodyAndObservePhantom(double now)
        {
            // Observe an existing gravity-first cadence by ordinary fixed-time
            // ticks. A matching removal stage caches the destruction reservation
            // before the command retires its owner and can expose a phantom.
            // Preparation is resumable, so a command-first completion is also a
            // legal outcome; the enclosing oracle retries only to obtain actual
            // phantom coverage, never to relax retirement/liveness assertions.
            yield return SelectGravityFirst(now);
            bool observed = false;
            long visitsAtPublication = -1;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                if (observed || _destroyCallbacks == 0) return;
                observed = true;
                Assert.That(_destroyCallbacks, Is.EqualTo(1));
                Assert.That(_destroyResult.RemovedEnemy, Is.EqualTo(1));
                Assert.That(_destroyResult.RemovedFriendly, Is.Zero);
                Assert.That(_terrain.World.Falling.ContainsKey(_oldRunId), Is.False);
                if (_oldCohortId != 0)
                    Assert.That(_terrain.World.Cohorts.ContainsKey(_oldCohortId), Is.False);
                _phantomObserved = _terrain.BuildGravityBarrierColumns == 1;
                if (_phantomObserved)
                {
                    Assert.That(_terrain.World.Falling.Count, Is.Zero);
                    Assert.That(_terrain.World.Cohorts.Count, Is.Zero);
                    visitsAtPublication = _terrain.BuildGravityBarrierVisits;
                    AssertPinkStillStable();
                }
                else
                {
                    Assert.That(_terrain.BuildGravityBarrierColumns, Is.Zero);
                    var pink = _terrain.World.Cohorts.Values.FirstOrDefault(c => c.Bottom == NewCell
                        && c.Phase == FallingCohortPhase.Falling);
                    if (pink != null) AssertPinkActivation(pink, now, false);
                }
                Assert.That(_terrain.BlueCount, Is.EqualTo(1));
                Assert.That(_terrain.PinkCount, Is.EqualTo(1));
                AssertRetainedSource();
            };
            _terrain.PublishedCommit += observe;
            try
            {
                Vector3 hit = BlockWorld.Center(_oldCell) - Vector3.up * _oldRun.Offset(now);
                _terrain.QueueDestroy(hit, .0001f, 0, 1, true,
                    result => { _destroyCallbacks++; _destroyResult = result; }, 1, 0);
                yield return Pump(now, true, () => observed);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(observed, Is.True);
            yield return Pump(now, true, () => _terrain.BuildGravityBarrierColumns == 0);
            if (_phantomObserved)
                Assert.That(_terrain.BuildGravityBarrierVisits, Is.GreaterThan(visitsAtPublication),
                    "An admitted scan must remove the phantom after the last occupied old cell disappears.");
        }

        IEnumerator SelectGravityFirst(double now)
        {
            var field = typeof(BlockTerrain).GetField("_gravityFirst", Private);
            Assert.That(field, Is.Not.Null);
            for (int frame = 0; frame < 256; frame++)
            {
                if (_terrain.PendingTransactions == 0 && (bool)field.GetValue(_terrain)) yield break;
                _terrain.Tick(true, now, .016f);
                yield return null;
            }
            Assert.Fail("Ordinary authority stages did not expose the alternating gravity-first cadence.");
        }

        IEnumerator AwaitNewPinkMotion(double now, bool oldSettled)
        {
            bool observed = _observedPinkActivationAt == now;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                var pink = _terrain.World.Cohorts.Values.FirstOrDefault(c => c.Bottom == NewCell
                    && c.Phase == FallingCohortPhase.Falling);
                if (pink == null || observed) return;
                observed = true;
                AssertPinkActivation(pink, now, oldSettled);
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(now, true, () => observed); }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(observed, Is.True, "Barrier retirement must permit an actual new gravity activation publication.");
        }

        void AssertPinkActivation(FallingCohort pink, double now, bool oldSettled)
        {
            Assert.That(_terrain.World.Falling.ContainsKey(_oldRunId), Is.False,
                "The new inverted source cannot activate while the old physical owner remains live.");
            if (_oldCohortId != 0)
                Assert.That(_terrain.World.Cohorts.ContainsKey(_oldCohortId), Is.False);
            Assert.That(_terrain.BuildGravityBarrierColumns, Is.Zero);
            Assert.That(pink.StartedAt, Is.EqualTo(now));
            Assert.That(pink.Offset(now), Is.Zero.Within(1e-6));
            Assert.That(pink.LandingKnown, Is.True);
            Assert.That(pink.LandingOffset, Is.EqualTo(oldSettled ? .1f : .2f).Within(1e-6));
            Assert.That(_terrain.World.TryGet(NewCell, out _), Is.False);
            Assert.That(pink.MemberIds.Sum(id => _terrain.World.Falling[id].Cells.Count(c => c.Occupied)), Is.EqualTo(1));
            Assert.That(_terrain.BlueCount, Is.EqualTo(oldSettled ? 2 : 1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            if (oldSettled) AssertOldBluePosition(now);
            AssertRetainedSource();
            _observedPinkActivationAt = now;
        }

        IEnumerator FinishAndCheck(double now, bool oldSettled)
        {
            yield return Pump(now, true, () => _terrain.PendingTransactions == 0
                && _terrain.World.Falling.Count == 0 && _terrain.World.Cohorts.Count == 0
                && _terrain.BuildGravityBarrierColumns == 0);
            var landing = Floor + Vector3Int.up * (oldSettled ? 2 : 1);
            Assert.That(_terrain.World.TryGet(landing, out var pink), Is.True);
            Assert.That(pink.Owner, Is.EqualTo(1));
            Assert.That(pink.Durability, Is.EqualTo(100));
            Assert.That(pink.Flags, Is.Zero);
            Assert.That(_terrain.World.TryGet(NewCell, out _), Is.False,
                "The previously deferred new block must leave its unsupported source position.");
            Assert.That(_terrain.World.TryGet(Floor, out var floor), Is.True);
            Assert.That(floor.Owner, Is.EqualTo(2));
            Assert.That(_terrain.BlueCount, Is.EqualTo(oldSettled ? 2 : 1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(_buildCallbacks, Is.EqualTo(1));
            Assert.That(_destroyCallbacks, Is.EqualTo(oldSettled ? 0 : 1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero,
                "A legal inversion and retirement must not need historical geometry repair.");
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            AssertRetainedSource();
        }

        void AssertPinkStillStable()
        {
            Assert.That(_terrain.World.TryGet(NewCell, out var pink), Is.True);
            Assert.That(pink.Owner, Is.EqualTo(1));
            Assert.That(_terrain.World.Cohorts.Values.Any(c => c.Bottom == NewCell), Is.False);
            Assert.That(_terrain.World.Falling.Values.Any(run => run.Cells.Any(cell => cell.Owner == 1)), Is.False);
        }

        void AssertOldBluePosition(double now)
        {
            float expected = _oldCell.y * BlockWorld.CellSize - _oldRun.Offset(now);
            float actual = float.NaN;
            int bodies = 0;
            for (int y = 1; y <= _oldCell.y; y++)
                if (_terrain.World.TryGet(new Vector3Int(Floor.x, y, Floor.z), out var own) && own.Owner == 2)
                { bodies++; actual = y * BlockWorld.CellSize; }
            foreach (var run in _terrain.World.Falling.Values)
                for (int index = 0; index < run.Cells.Length; index++)
                    if (run.Cells[index].Owner == 2)
                    { bodies++; actual = (run.Bottom.y + index) * BlockWorld.CellSize - run.Offset(now); }
            Assert.That(bodies, Is.EqualTo(1), "The old survivor must remain exactly one physical Blue cell.");
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6),
                "The inverted new build cannot lift, reset, duplicate or prematurely settle the old survivor.");
        }

        void AssertRetainedSource()
            => CollectionAssert.AreEqual(_retainedBytes, _retainedSource.EncodeSnapshot(),
                "Build, barrier scanning, retirement and activation cannot mutate the retained source World.");

        IEnumerator Pump(double now, bool authority, Func<bool> ready)
        {
            for (int frame = 0; frame < PumpLimit && !ready(); frame++)
            {
                _terrain.Tick(authority, now, .016f);
                yield return null;
            }
            Assert.That(ready(), Is.True,
                "Scheduling-friendly bounded Editor oracle did not reach its observable state; this is not a Player timing gate.");
        }

        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
    }
}
#endif
