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
    // Reports-only prepared source: not compiled, executed, or performance accepted.
    // The support rejection is caused by a real registered native collider edit.
    // Reflection registers map colliders and reads witnesses; it creates no Pending,
    // journal, Window, proof, falling body, command checkpoint, or synthetic task.
    public sealed class BlockGravitySupportRetryWindowTests
    {
        HistoricalBlockWorldHeightScope _historicalHeight;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const double Now = 10;
        static readonly Vector3Int FloorCell = new(-150, 0, 2);
        GameObject _root;
        BlockTerrain _terrain;
        HashSet<Collider> _registered;
        BoxCollider _far;
        int _paidCallbacks, _mutationEvents;
        BlockMutationResult _baseResult;

        [UnitySetUp] public IEnumerator Setup()
        {
            _paidCallbacks = _mutationEvents = 0; _baseResult = default;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            _historicalHeight = new HistoricalBlockWorldHeightScope();
            _root = new GameObject("Native support retry immutable lease contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(60);
            _terrain.Initialize(null);
            _registered = Read<HashSet<Collider>>(_terrain, "_staticColliders");
            MakeBox("Map floor", new Vector3(0, -.05f, 0), new Vector3(32, .1f, 64));
            _far = MakeBox("Unrelated registered native object", new Vector3(40, 8, 40), Vector3.one);
            Physics.SyncTransforms();
            _terrain.MutationCompleted += OnMutation;
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_terrain != null) _terrain.MutationCompleted -= OnMutation;
            if (_root != null) Object.DestroyImmediate(_root);
            _historicalHeight?.Dispose(); _historicalHeight = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void OnMutation(BlockMutationResult result, double _) { _mutationEvents++; }

        [UnityTest, Timeout(600000)]
        public IEnumerator NaturalGravityOnlySupportRetryKeepsOriginalLeaseRecomputesAllCellsAndPaysNoFreeResult()
        {
            yield return PreparePureBackground();
            var rejected = Read<object>(_terrain, "_pending");
            AssertPureBackground(rejected);
            AssertNativeSnapshotWitness(rejected);
            var originalWindow = Read<BlockPublicationWindow>(rejected, "Window");
            Assert.That(originalWindow.Policy.Version, Is.EqualTo(60));
            Assert.That(originalWindow.MaximumChangedKeys, Is.EqualTo(32));
            var source = _terrain.World; var sourceBytes = source.EncodeSnapshot();
            var rejectedNext = Read<BlockWorld>(rejected, "Next");
            int paidBefore = _paidCallbacks, mutationBefore = _mutationEvents;
            uint revisionBefore = _terrain.Revision;
            long retriesBefore = _terrain.GravitySupportPublicationRetries;
            Assert.That(_terrain.BlueCount, Is.EqualTo(319));
            Assert.That(CollisionAt(FloorCell + Vector3Int.up * 319), Is.True);

            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(61);
            _far.transform.position += new Vector3(.25f, .1f, -.3f);
            Physics.SyncTransforms();
            yield return Pump(() => _terrain.GravitySupportPublicationRetries > retriesBefore);
            Assert.That(_terrain.GravitySupportPublicationRetries, Is.EqualTo(retriesBefore + 1));
            Assert.That(_terrain.World, Is.SameAs(source));
            Assert.That(_terrain.World, Is.Not.SameAs(rejectedNext));
            Assert.That(_terrain.Revision, Is.EqualTo(revisionBefore));
            CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            Assert.That(_paidCallbacks, Is.EqualTo(paidBefore));
            Assert.That(_mutationEvents, Is.EqualTo(mutationBefore));
            // Tick may already consume the finite empty-checkpoint preparation;
            // whichever owner is live must retain the SAME admitted lease.
            var preparation = Read<object>(_terrain, "_buildOccupancyPreparation");
            var retryOwner = preparation != null ? Read<BlockPublicationWindow>(preparation, "Window")
                : Read<BlockPublicationWindow>(_terrain, "_gravityRetryPublicationWindow");
            Assert.That(retryOwner, Is.SameAs(originalWindow));

            yield return Pump(() => Read<object>(_terrain, "_pending") != null);
            var replacement = Read<object>(_terrain, "_pending");
            // A budgeted retry may first publish proof/cohort metadata. The
            // original window owns that first real atomic continuation as well;
            // do not require its first replacement to detach an actual piece.
            AssertNoAttackPending(replacement);
            Assert.That(replacement, Is.Not.SameAs(rejected));
            Assert.That(Read<BlockPublicationWindow>(replacement, "Window"), Is.SameAs(originalWindow));
            var replacementNext = Read<BlockWorld>(replacement, "Next");
            Assert.That(replacementNext, Is.Not.SameAs(rejectedNext), "Retry must recompute a fresh draft, never install the rejected Next.");
            var replacementJournal = Read<object>(replacement, "Gravity");
            if (replacementJournal != null)
                Assert.That(Read<BlockWorld>(replacementJournal, "Next"), Is.SameAs(replacementNext));
            int replacementCommits = 0;
            Action<BlockTerrainCommit> observe = commit =>
            {
                if (!ReferenceEquals(_terrain.World, replacementNext)) return;
                replacementCommits++;
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(originalWindow.MaximumChangedKeys));
                Assert.That(_terrain.BlueCount, Is.EqualTo(319));
                Assert.That(_terrain.PinkCount, Is.Zero);
                Assert.That(_paidCallbacks, Is.EqualTo(paidBefore));
                Assert.That(_mutationEvents, Is.EqualTo(mutationBefore));
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(() => replacementCommits == 1); }
            finally { _terrain.PublishedCommit -= observe; }
            yield return Pump(() => SourceCells(_terrain.World) == 319 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => AtColumn(c.Bottom) && c.Phase == FallingCohortPhase.Falling));
            Assert.That(replacementCommits, Is.EqualTo(1));
            Assert.That(_paidCallbacks, Is.EqualTo(1));
            Assert.That(_baseResult.RemovedEnemy, Is.EqualTo(1));
            Assert.That(_mutationEvents, Is.EqualTo(1), "Gravity retry and movement do not earn attack charge.");
            Assert.That(_terrain.BlueCount, Is.EqualTo(319)); Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(SourceCells(_terrain.World), Is.EqualTo(319));
            Assert.That(_terrain.World.Falling.Values.Where(r => AtColumn(r.Bottom)).SelectMany(r => r.Cells)
                .Where(c => c.Occupied).All(c => c.Owner == 2), Is.True);
            var sourceAddresses = _terrain.World.Falling.Values.Where(r => AtColumn(r.Bottom))
                .SelectMany(r => r.Cells.Select((cell, offset) => (cell, y: r.Bottom.y + offset)))
                .Where(item => item.cell.Occupied).Select(item => item.y).OrderBy(y => y).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(1, 319).ToArray(), sourceAddresses,
                "Every original surviving source address must occur exactly once, with no missing or duplicated row.");
            for (int y = 1; y < 320; y++)
                Assert.That(CollisionAt(FloorCell + Vector3Int.up * y), Is.False,
                    "A falling cell is still counted but must not retain any stable character collider: " + y);
            Assert.That(_terrain.FailedTransactions, Is.Zero); Assert.That(_terrain.StaticProofInvalidations, Is.Zero);
            CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            for (int i = 0; i < 20; i++) { _terrain.Tick(true, Now, 1f / 60); yield return null; }
            Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(1));
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ClearingAfterActualSupportRejectionCannotReplayOldLeaseOrOldGravityInNewRound()
        {
            yield return PreparePureBackground();
            var rejected = Read<object>(_terrain, "_pending"); AssertPureBackground(rejected);
            AssertNativeSnapshotWitness(rejected);
            var oldWindow = Read<BlockPublicationWindow>(rejected, "Window");
            long retriesBefore = _terrain.GravitySupportPublicationRetries;
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(62);
            _far.transform.position += Vector3.right * .25f; Physics.SyncTransforms();
            yield return Pump(() => _terrain.GravitySupportPublicationRetries > retriesBefore);
            Assert.That(_terrain.GravitySupportPublicationRetries, Is.EqualTo(retriesBefore + 1));
            var preparation = Read<object>(_terrain, "_buildOccupancyPreparation");
            Assert.That(preparation != null ? Read<BlockPublicationWindow>(preparation, "Window")
                : Read<BlockPublicationWindow>(_terrain, "_gravityRetryPublicationWindow"), Is.SameAs(oldWindow));
            int commits = 0; Action<BlockTerrainCommit> observe = _ => commits++;
            _terrain.PublishedCommit += observe;
            uint nextRound = _terrain.Round + 1;
            try
            {
                _terrain.Clear(nextRound);
                for (int i = 0; i < 120; i++) { _terrain.Tick(true, Now, 1f / 60); yield return null; }
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(_terrain.Round, Is.EqualTo(nextRound)); Assert.That(commits, Is.Zero);
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.BlueCount + _terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.World.Falling.Count + _terrain.World.Cohorts.Count, Is.Zero);
            Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(1));
            Assert.That(Read<BlockPublicationWindow>(_terrain, "_gravityRetryPublicationWindow"), Is.Null);
            Assert.That(CollisionAt(FloorCell + Vector3Int.up * 319), Is.False);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator LateOrdinaryDestroyAfterActualSupportRejectionCannotBeStarvedByRetainedBackgroundLease()
        {
            yield return PreparePureBackground();
            var rejected = Read<object>(_terrain, "_pending");
            AssertPureBackground(rejected); AssertNativeSnapshotWitness(rejected);
            var originalWindow = Read<BlockPublicationWindow>(rejected, "Window");
            Assert.That(originalWindow.Policy.Version, Is.EqualTo(60));
            Assert.That(originalWindow.MaximumChangedKeys, Is.EqualTo(32));
            var source = _terrain.World; var sourceBytes = source.EncodeSnapshot();
            var rejectedNext = Read<BlockWorld>(rejected, "Next");
            long retriesBefore = _terrain.GravitySupportPublicationRetries;
            long scansBefore = _terrain.FairCompleteBlockedScans;
            long dependencyBefore = _terrain.FairDependencyAtomicPublications;
            var hit = FloorCell + Vector3Int.up * 319;
            Assert.That(_terrain.BlueCount, Is.EqualTo(319));
            Assert.That(source.TryGet(hit, out var target), Is.True);
            Assert.That(target.Owner, Is.EqualTo(2));
            Assert.That(CollisionAt(hit), Is.True);

            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(63);
            _far.transform.position += new Vector3(.25f, .1f, -.3f); Physics.SyncTransforms();
            yield return Pump(() => _terrain.GravitySupportPublicationRetries > retriesBefore);
            Assert.That(_terrain.GravitySupportPublicationRetries, Is.EqualTo(retriesBefore + 1));
            Assert.That(_terrain.World, Is.SameAs(source));
            Assert.That(_terrain.World, Is.Not.SameAs(rejectedNext));
            CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
            var preparation = Read<object>(_terrain, "_buildOccupancyPreparation");
            Assert.That(preparation != null ? Read<BlockPublicationWindow>(preparation, "Window")
                : Read<BlockPublicationWindow>(_terrain, "_gravityRetryPublicationWindow"), Is.SameAs(originalWindow));
            Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(1));

            int lateCallbacks = 0;
            BlockMutationResult lateResult = default;
            object lateCommand = null, originalSeed = null, completedScan = null;
            BlockPublicationWindow foregroundWindow = null;
            var actualScans = new HashSet<object>();
            bool observedBackgroundScan = false, observedForeground = false;
            // Queue immediately after the actual rejection, without another Tick.
            // Even radius zero reserves the entire XZ column in real production.
            _terrain.QueueDestroy(BlockWorld.Center(hit), 0, 0, 1, true, result =>
            {
                lateCallbacks++; lateResult = result;
                Assert.That(lateCallbacks, Is.EqualTo(1));
                Assert.That(observedForeground, Is.True, "A real complete blocked scan must precede the paid foreground stage.");
                Assert.That(completedScan, Is.Not.Null);
                Assert.That(_terrain.FairDependencyAtomicPublications, Is.GreaterThan(dependencyBefore));
                Assert.That(Read<object>(_terrain, "_fairLastDependencyHead"), Is.SameAs(lateCommand));
                Assert.That(Read<object>(_terrain, "_fairLastDependencyBlocker"), Is.SameAs(lateCommand));
                var seed = Read<object>(_terrain, "_fairSeed");
                Assert.That(seed, Is.SameAs(originalSeed));
                Assert.That(Read<BlockPublicationWindow>(seed, "BackgroundWindow"), Is.SameAs(originalWindow));
                Assert.That(Read<BlockPublicationWindow>(seed, "ForegroundWindow"), Is.SameAs(foregroundWindow));
                Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                Assert.That(result.Added + result.RemovedFriendly, Is.Zero);
                Assert.That(result.OrdinaryMainAttack, Is.True);
                Assert.That(_terrain.BlueCount, Is.EqualTo(318)); Assert.That(_terrain.PinkCount, Is.Zero);
                Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(2));
            }, 1, 0, true);
            // Automatic spread may already have a background quantum in FIFO.
            // The queued paid command must still be the unique non-spread item.
            lateCommand = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>()
                .Single(command => !Read<bool>(command, "Spread"));
            Assert.That(Read<bool>(lateCommand, "OrdinaryMainAttack"), Is.True);
            Assert.That(Read<bool>(lateCommand, "Build"), Is.False);
            Assert.That(Read<BlockWorld>(lateCommand, "Source"), Is.SameAs(source));
            Assert.That(Read<double>(lateCommand, "SourceTime"), Is.EqualTo(Now));
            Assert.That(((IEnumerable)Read<object>(_terrain, "_destructionReservations")).Cast<object>()
                .Any(command => ReferenceEquals(command, lateCommand)), Is.True);

            bool ObserveRetryAndFairState()
            {
                CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
                if (lateCallbacks == 0)
                {
                    Assert.That(_terrain.BlueCount, Is.EqualTo(319)); Assert.That(_terrain.PinkCount, Is.Zero);
                    Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(1));
                }
                var seed = Read<object>(_terrain, "_fairSeed");
                if (seed != null)
                {
                    if (originalSeed == null) originalSeed = seed;
                    Assert.That(seed, Is.SameAs(originalSeed));
                    Assert.That(Read<BlockPublicationWindow>(seed, "BackgroundWindow"), Is.SameAs(originalWindow));
                    Assert.That(_terrain.FairCapturedBackgroundPolicyVersion, Is.EqualTo(60));
                    var scan = Read<object>(seed, "Scan");
                    if (scan != null && lateCallbacks == 0)
                    {
                        Assert.That(Read<bool>(seed, "Foreground"), Is.False);
                        Assert.That(Read<BlockWorld>(scan, "Source"), Is.SameAs(_terrain.World));
                        Assert.That(Read<uint>(scan, "Revision"), Is.EqualTo(_terrain.Revision));
                        Assert.That(Read<uint>(scan, "Round"), Is.EqualTo(_terrain.Round));
                        Assert.That(Read<int>(scan, "ColumnCount") + Read<int>(scan, "MotionCount"), Is.GreaterThan(0));
                        actualScans.Add(scan); observedBackgroundScan = true;
                    }
                    if (Read<bool>(seed, "Foreground") && lateCallbacks == 0)
                    {
                        Assert.That(observedBackgroundScan, Is.True);
                        Assert.That(_terrain.FairCompleteBlockedScans, Is.GreaterThan(scansBefore));
                        Assert.That(Read<object>(seed, "Head"), Is.SameAs(lateCommand));
                        Assert.That(Read<bool>(seed, "Dependency"), Is.True);
                        Assert.That(Read<object>(_terrain, "_fairLastDependencyHead"), Is.SameAs(lateCommand));
                        Assert.That(Read<object>(_terrain, "_fairLastDependencyBlocker"), Is.SameAs(lateCommand));
                        var scanSource = Read<BlockWorld>(_terrain, "_fairLastDependencySource");
                        completedScan = actualScans.FirstOrDefault(actual => Read<bool>(actual, "Finished")
                            && Read<bool>(actual, "HasBlocker") && ReferenceEquals(Read<object>(actual, "Blocker"), lateCommand)
                            && ReferenceEquals(Read<BlockWorld>(actual, "Source"), scanSource));
                        Assert.That(completedScan, Is.Not.Null, "Only a runtime-created, fully completed latest-source scan can authorize FIFO service.");
                        Assert.That(Read<int>(completedScan, "Phase"), Is.EqualTo(1));
                        Assert.That(Read<int>(completedScan, "Cursor"), Is.EqualTo(Read<int>(completedScan, "MotionCount")));
                        Assert.That(_terrain.FairLastCompleteScanColumns, Is.EqualTo(Read<int>(completedScan, "ColumnCount")));
                        Assert.That(_terrain.FairLastCompleteScanMotions, Is.EqualTo(Read<int>(completedScan, "MotionCount")));
                        Assert.That(Read<Vector2Int>(completedScan, "BlockerColumn"), Is.EqualTo(new Vector2Int(FloorCell.x, FloorCell.z)));
                        Assert.That(AtColumn(Read<Vector3Int>(completedScan, "BlockerCell")), Is.True);
                        Assert.That(Read<ulong>(completedScan, "ReservationVersion"),
                            Is.EqualTo(Read<ulong>(_terrain, "_fairLastDependencyReservationVersion")));
                        var currentForegroundWindow = Read<BlockPublicationWindow>(seed, "ForegroundWindow");
                        if (foregroundWindow == null) foregroundWindow = currentForegroundWindow;
                        Assert.That(currentForegroundWindow, Is.SameAs(foregroundWindow));
                        Assert.That(foregroundWindow, Is.Not.SameAs(originalWindow));
                        Assert.That(foregroundWindow.Policy.Version, Is.EqualTo(63));
                        Assert.That(foregroundWindow.MaximumChangedKeys, Is.EqualTo(8));
                        Assert.That(foregroundWindow.Includes(BlockPublicationOperation.OrdinaryAttack
                            | BlockPublicationOperation.Destruction | BlockPublicationOperation.Gravity), Is.True);
                        observedForeground = true;
                    }
                }
                return lateCallbacks == 1;
            }
            yield return Pump(ObserveRetryAndFairState);
            Assert.That(observedBackgroundScan && observedForeground, Is.True);
            Assert.That(lateCallbacks, Is.EqualTo(1)); Assert.That(lateResult.RemovedEnemy, Is.EqualTo(1));
            Assert.That(lateResult.Added + lateResult.RemovedFriendly, Is.Zero);
            yield return Pump(() => SourceCells(_terrain.World) == 318 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => AtColumn(c.Bottom) && c.Phase == FallingCohortPhase.Falling));
            Assert.That(_terrain.BlueCount, Is.EqualTo(318)); Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_baseResult.RemovedEnemy, Is.EqualTo(1));
            Assert.That(_baseResult.Added + _baseResult.RemovedFriendly, Is.Zero);
            Assert.That(lateCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(2));
            var fallingCells = _terrain.World.Falling.Values.Where(r => AtColumn(r.Bottom))
                .SelectMany(r => r.Cells.Select((cell, offset) => (cell, y: r.Bottom.y + offset)))
                .Where(item => item.cell.Occupied).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(1, 318).ToArray(), fallingCells.Select(item => item.y).OrderBy(y => y).ToArray(),
                "Only the two actually paid source addresses may disappear; every other original row occurs once.");
            Assert.That(fallingCells.All(item => item.cell.Owner == 2), Is.True);
            Assert.That(_terrain.World.Chunks.Values.Sum(chunk => chunk.Cells.Count(cell => cell.Occupied)), Is.Zero);
            Assert.That(_terrain.World.Falling.Values.Sum(run => run.Cells.Count(cell => cell.Occupied)), Is.EqualTo(318));
            for (int y = 0; y < 320; y++)
            {
                Assert.That(_terrain.World.TryGet(FloorCell + Vector3Int.up * y, out _), Is.False);
                Assert.That(CollisionAt(FloorCell + Vector3Int.up * y), Is.False,
                    "Removed or falling source rows must have no stable character collider: " + y);
            }
            Assert.That(_terrain.FailedTransactions, Is.Zero); Assert.That(_terrain.StaticProofInvalidations, Is.Zero);
            Assert.That(_terrain.GravitySupportPublicationRetries, Is.EqualTo(retriesBefore + 1));
            CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            for (int i = 0; i < 20; i++) { _terrain.Tick(true, Now, 1f / 60); yield return null; }
            Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(lateCallbacks, Is.EqualTo(1));
            Assert.That(_mutationEvents, Is.EqualTo(2)); Assert.That(_terrain.BlueCount, Is.EqualTo(318));
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        IEnumerator PreparePureBackground()
        {
            var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            for (int y = 0; y < 320; y++) Assert.That(seed.Set(FloorCell + Vector3Int.up * y, BlockCell.Create(2)), Is.True);
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, false);
            Assert.That(_terrain.BlueCount, Is.EqualTo(320)); Assert.That(_paidCallbacks + _mutationEvents, Is.Zero);
            _terrain.QueueDestroy(BlockWorld.Center(FloorCell), 0, 0, 1, true,
                result => { _paidCallbacks++; _baseResult = result; }, 1, 0, true);
            yield return Pump(() => PureBackgroundPending() != null);
            Assert.That(_paidCallbacks, Is.EqualTo(1)); Assert.That(_baseResult.RemovedEnemy, Is.EqualTo(1));
            Assert.That(_baseResult.Added + _baseResult.RemovedFriendly, Is.Zero);
            Assert.That(_mutationEvents, Is.EqualTo(1));
        }
        object PureBackgroundPending()
        {
            var pending = Read<object>(_terrain, "_pending");
            if (pending == null || Read<bool>(pending, "Remote") || Read<object>(pending, "BuildGuard") != null) return null;
            var journal = Read<object>(pending, "Gravity");
            if (journal == null || !Read<bool>(journal, "RetainSupportSnapshot")) return null;
            if (((IEnumerable)Read<object>(pending, "Results")).Cast<object>().Any()) return null;
            return SourceCells(Read<BlockWorld>(pending, "Next")) > SourceCells(_terrain.World) ? pending : null;
        }
        static void AssertPureBackground(object pending)
        {
            AssertNoAttackPending(pending);
            Assert.That(Read<bool>(pending, "PrivateGravity"), Is.False);
            Assert.That(Read<object>(pending, "RecoveryGuard"), Is.Null);
            var checkpoints = Read<object>(pending, "CommandCheckpoints");
            Assert.That(checkpoints == null || !((IEnumerable)checkpoints).Cast<object>().Any(), Is.True);
            Assert.That(Read<bool>(Read<object>(pending, "Gravity"), "RetainSupportSnapshot"), Is.True);
        }
        void AssertNativeSnapshotWitness(object pending)
        {
            var journal = Read<object>(pending, "Gravity");
            var next = Read<BlockWorld>(pending, "Next");
            var snapshot = Read<object>(journal, "SupportSnapshot");
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(Read<BlockWorld>(snapshot, "Draft"), Is.SameAs(next));
            Assert.That(Read<BlockWorld>(journal, "SupportProofDraft"), Is.SameAs(next));
            Assert.That(Read<Array>(snapshot, "Bounds").Cast<object>().Any(bound =>
                ReferenceEquals(Read<Collider>(bound, "Collider"), _far)), Is.True,
                "The real native object we edit must belong to the exact retained absence snapshot.");
        }
        static void AssertNoAttackPending(object pending)
        {
            Assert.That(pending, Is.Not.Null);
            Assert.That(Read<bool>(pending, "Remote"), Is.False);
            Assert.That(Read<object>(pending, "BuildGuard"), Is.Null);
            Assert.That(((IEnumerable)Read<object>(pending, "Results")).Cast<object>().Any(), Is.False);
        }
        BoxCollider MakeBox(string name, Vector3 position, Vector3 size)
        {
            var item = new GameObject(name); item.transform.SetParent(_root.transform, false); item.transform.position = position;
            var collider = item.AddComponent<BoxCollider>(); collider.size = size; _registered.Add(collider); return collider;
        }
        static bool AtColumn(Vector3Int cell) => cell.x == FloorCell.x && cell.z == FloorCell.z;
        static int SourceCells(BlockWorld world) => world.Falling.Values.Where(r => AtColumn(r.Bottom)).Sum(r => r.Cells.Count(c => c.Occupied));
        static bool CollisionAt(Vector3Int cell) => Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .2f,
            Vector3.down, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);
        static T Read<T>(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null, "Missing owner for " + name);
            var field = owner.GetType().GetField(name, Fields); Assert.That(field, Is.Not.Null, owner.GetType().Name + "." + name);
            return (T)field.GetValue(owner);
        }
        IEnumerator Pump(Func<bool> done, bool authority = true)
        {
            double until = Time.realtimeSinceStartupAsDouble + 180;
            for (int i = 0; i < 6000 && !done() && Time.realtimeSinceStartupAsDouble < until; i++)
            { _terrain.Tick(authority, Now, 1f / 60); yield return null; }
            Assert.That(done(), Is.True, "Required natural pending/retry/full-survivor witness was not reached; no pass is claimed.");
        }
    }
}
#endif
