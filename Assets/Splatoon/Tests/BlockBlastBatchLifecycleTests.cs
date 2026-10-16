#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // All stages/rollback/publications run
    // through Tick. Reflection reads real pending/checkpoint state, registers
    // a map collider, and (one named fault test) replaces a completed Bake task.
    // Ordinary build and destruction leases publish separately. Guard fixtures
    // observe a real build retry/cancellation, then the subsequent blast batch.
    // No private stage/validator/RestoreCheckpoint/Fail method is invoked.
    public sealed class BlockBlastBatchLifecycleTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const double Started = 10, SafeAt = 10.1, RejectAt = 10.3;
        const int PumpLimit = 4000;
        static readonly Vector3Int Floor = new(2, 0, 2);
        GameObject _root;
        BlockTerrain _terrain;
        readonly Dictionary<int, int> _frameChunks = new();
        readonly List<int> _callbackOrder = new();
        readonly List<BlockTerrain.PendingWallOutcome> _outcomes = new();
        int[] _callbacks;
        BlockMutationResult[] _results;
        object[] _commands;
        Vector3Int[] _victims;
        BlockWorld _source;
        byte[] _sourceBytes;
        double _sourceTime;
        object _witness;

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Real blast batch lifecycle contracts");
            _terrain = _root.AddComponent<BlockTerrain>();
            // These lifecycle contracts deliberately require partial publications.
            // The production default is covered by BlockPublicationPolicy tests.
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            _terrain.Initialize(null);
            var ground = new GameObject("Registered map ground top zero");
            ground.transform.SetParent(_root.transform, false);
            ground.transform.position = new Vector3(.25f, -.05f, .25f);
            var collider = ground.AddComponent<BoxCollider>(); collider.size = new Vector3(2, .1f, 2);
            ((HashSet<Collider>)Read<object>(_terrain, "_staticColliders")).Add(collider);
            Physics.SyncTransforms();
            _frameChunks.Clear(); _callbackOrder.Clear(); _outcomes.Clear();
            _terrain.PublishedCommit += ObservePublication;
            _terrain.PendingTransactionFinished += ObserveTerminal;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_terrain != null)
            {
                _terrain.PublishedCommit -= ObservePublication;
                _terrain.PendingTransactionFinished -= ObserveTerminal;
            }
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)] public IEnumerator FinitePrefixNineBlastsAndFiniteSuffixPreserveRealFifoAndEarliestCredit()
        {
            yield return InstallVictims(18);
            _terrain.Tick(true, SafeAt, .016f);
            var prefix = new Vector3Int(-50, 1, -50);
            var suffix = new Vector3Int(-34, 1, -50);
            var seed = _terrain.World.Clone(); seed.Set(prefix, BlockCell.Create(2, true)); seed.Set(suffix, BlockCell.Create(2, true));
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return Pump(false, SafeAt, () => _terrain.PendingTransactions == 0);
            _terrain.Tick(false, SafeAt, .016f);
            BeginCallbacks(11);
            _terrain.QueueDestroy(BlockWorld.Center(prefix), 0, 0, 1, true, Result(0), 1, 0);
            QueueBlasts(1, 9);
            _terrain.QueueDestroy(BlockWorld.Center(suffix), 0, 0, 1, true, Result(10), 1, 0);
            RetainCommands();
            bool witnessedEight = false;
            for (int frame = 0; frame < PumpLimit && _callbacks.Any(c => c == 0); frame++)
            {
                _terrain.Tick(true, SafeAt, .016f);
                var pending = Read<object>(_terrain, "_pending");
                var checkpoint = BatchCheckpoint(pending == null ? null : Read<object>(pending, "CommandCheckpoints"));
                if (checkpoint != null)
                {
                    var batch = Read<object>(checkpoint, "BlastBatch");
                    var members = Read<object[]>(batch, "Commands");
                    Assert.That(members.Length, Is.EqualTo(8));
                    for (int i = 0; i < 8; i++) Assert.That(members[i], Is.SameAs(_commands[i + 1]));
                    witnessedEight = true;
                }
                AssertQueuedReferencesAreUnique();
                yield return null;
            }
            Assert.That(witnessedEight, Is.True, "Real command publication must include an eight-member checkpoint; ordinary FIFO alone is insufficient coverage.");
            AssertCallbacksOnce();
            CollectionAssert.AreEqual(Enumerable.Range(0, 11), _callbackOrder);
            Assert.That(_results[0].RemovedEnemy, Is.EqualTo(1));
            Assert.That(_results[1].RemovedEnemy, Is.EqualTo(_victims.Length));
            for (int i = 2; i < 10; i++) Assert.That(_results[i].Changed, Is.Zero);
            Assert.That(_results[10].RemovedEnemy, Is.EqualTo(1));
            Assert.That(_terrain.BlueCount, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            foreach (var cell in _victims.Append(prefix).Append(suffix)) AssertRemovedCellAndCollision(cell);
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)] public IEnumerator RealBuildGuardRetryPrecedesPartialBatchAndPreservesNineCommandFifo()
            => RetryPreparedBuild(partial: true, loseAuthority: false);
        [UnityTest, Timeout(600000)] public IEnumerator RealBuildGuardRetryPrecedesFullyDequeuedBatchAndRetiresItsMembersOnce()
            => RetryPreparedBuild(partial: false, loseAuthority: false);
        [UnityTest, Timeout(600000)] public IEnumerator AuthorityLossCancelsRealGuardedBuildBeforeBatchWithoutDuplicatingQueue()
            => RetryPreparedBuild(partial: true, loseAuthority: true);

        IEnumerator RetryPreparedBuild(bool partial, bool loseAuthority)
        {
            yield return InstallVictims(partial ? 18 : 3);
            yield return CreateNaturalMovingRoot();
            BeginCallbacks(10);
            long retries = _terrain.BuildPublicationRetries, failed = _terrain.FailedTransactions;
            _terrain.Tick(true, SafeAt, .016f);
            _terrain.QueueBuild(BuildPoint(Floor + Vector3Int.up), Vector3.up, 1, 1, 0, false, Result(0));
            QueueBlasts(1, 9); RetainCommands();
            yield return Pump(true, SafeAt, () =>
            {
                var pending = Read<object>(_terrain, "_pending");
                return pending != null && Read<object>(pending, "BuildGuard") != null;
            });
            _witness = Read<object>(_terrain, "_pending");
            var buildWindow = Read<BlockPublicationWindow>(_witness, "Window");
            Assert.That(buildWindow.Includes(BlockPublicationOperation.OrdinaryAttack), Is.True);
            Assert.That(buildWindow.Includes(BlockPublicationOperation.Destruction), Is.False,
                "A guarded ordinary build cannot admit the later destruction batch into its captured lease.");
            // Moving-build preparation transfers checkpoint ownership to the
            // guard before creating Pending; Pending.CommandCheckpoints is null.
            var guard = Read<object>(_witness, "BuildGuard");
            Assert.That(Read<BlockPublicationWindow>(guard, "Window"), Is.SameAs(buildWindow));
            var checkpoints = ((IEnumerable)Read<object>(guard, "Checkpoints")).Cast<object>().ToArray();
            Assert.That(checkpoints.Any(cp => ReferenceEquals(Read<object>(cp, "Command"), _commands[0])), Is.True);
            Assert.That(BatchCheckpoint(checkpoints), Is.Null);
            Assert.That(Read<BlockWorld>(_witness, "Next").BlueCount, Is.EqualTo(_terrain.BlueCount));
            AssertOriginalQueue(1);
            var savedIndexes = _commands.Skip(1).Select(c => Read<int>(c, "TargetIndex")).ToArray();
            Assert.That(savedIndexes.All(index => index == 0), Is.True);
            Assert.That(_callbacks.Sum(), Is.Zero);
            AssertVictimCollisionMatchesPublishedWorld();
            var witnessSource = _terrain.World; uint revision = _terrain.Revision;
            if (loseAuthority)
            {
                _terrain.Tick(false, RejectAt, .016f);
                Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
            }
            else
            {
                yield return Pump(true, RejectAt, () => _terrain.BuildPublicationRetries > retries);
                Assert.That(_terrain.World, Is.SameAs(witnessSource)); Assert.That(_terrain.Revision, Is.EqualTo(revision));
                Assert.That(_callbacks.Sum(), Is.Zero);
                AssertVictimCollisionMatchesPublishedWorld();
                // Allow only the real Retry cursor to restore the queue; do not invoke its hook.
                yield return Pump(true, RejectAt, () => Read<object>(_terrain, "_buildOccupancyPreparation") == null);
            }
            Assert.That(_terrain.World, Is.SameAs(witnessSource)); Assert.That(_terrain.Revision, Is.EqualTo(revision));
            Assert.That(_callbacks.Sum(), Is.Zero);
            AssertVictimCollisionMatchesPublishedWorld();
            AssertOriginalQueue(0);
            for (int i = 0; i < savedIndexes.Length; i++)
                Assert.That(Read<int>(_commands[i + 1], "TargetIndex"), Is.EqualTo(savedIndexes[i]));
            Assert.That(Read<object>(_terrain, "_blastBatch"), Is.Null);
            AssertFrozenInputsBeforeComplete();
            yield return CompleteSubsequentBlastBatch(partial);
            // A small completed batch can retire before the independent gravity
            // cursor publishes landing. The original root has already reached
            // its .4m cap at RejectAt; keep that clock and await real publication.
            yield return Pump(true, RejectAt, () => _terrain.PendingTransactions == 0 && _terrain.FallingCount == 0);
            AssertCallbacksOnce(); CollectionAssert.AreEqual(Enumerable.Range(0, 10), _callbackOrder);
            Assert.That(_terrain.BuildPublicationRetries, Is.GreaterThan(retries));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failed));
            Assert.That(_results[0].Added, Is.Zero);
            Assert.That(_results[1].RemovedEnemy, Is.EqualTo(_victims.Length));
            for (int i = 2; i < 10; i++) Assert.That(_results[i].Changed, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            foreach (var cell in _victims) AssertRemovedCellAndCollision(cell);
            Assert.That(_terrain.World.TryGet(Floor + Vector3Int.up, out var survivor) && survivor.Owner == 2, Is.True);
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
        }

        IEnumerator CompleteSubsequentBlastBatch(bool partial)
        {
            bool witnessedBatch = false;
            for (int frame = 0; frame < PumpLimit && (_callbacks.Any(c => c == 0) || _terrain.PendingTransactions != 0); frame++)
            {
                _terrain.Tick(true, RejectAt, .016f);
                var pending = Read<object>(_terrain, "_pending");
                var checkpoint = BatchCheckpoint(pending == null ? null : Read<object>(pending, "CommandCheckpoints"));
                if (checkpoint != null)
                {
                    var batch = Read<object>(checkpoint, "BlastBatch");
                    var members = Read<object[]>(batch, "Commands");
                    Assert.That(members.Length, Is.EqualTo(8));
                    for (int i = 0; i < members.Length; i++) Assert.That(members[i], Is.SameAs(_commands[i + 1]));
                    bool actualDeletion = ((IEnumerable)Read<object>(pending, "Results")).Cast<object>()
                        .Sum(pair => Read<BlockMutationResult>(pair, "Item2").RemovedEnemy) > 0;
                    if (actualDeletion && members.Any(c => Read<int>(c, "TargetIndex") > 0)
                        && Read<bool>(batch, "Dequeued") == !partial)
                    {
                        Assert.That(Read<object>(pending, "BuildGuard"), Is.Null);
                        var window = Read<BlockPublicationWindow>(pending, "Window");
                        Assert.That(window.Includes(BlockPublicationOperation.Destruction), Is.True);
                        Assert.That(window.Includes(BlockPublicationOperation.OrdinaryAttack), Is.False);
                        Assert.That(_callbacks[0], Is.EqualTo(1));
                        Assert.That(_callbacks.Skip(1).Sum(), Is.Zero);
                        AssertOriginalQueue(partial ? 1 : 9);
                        AssertVictimCollisionMatchesPublishedWorld();
                        witnessedBatch = true;
                    }
                }
                AssertQueuedReferencesAreUnique();
                yield return null;
            }
            Assert.That(witnessedBatch, Is.True,
                "The retried/cancelled build must be followed by an actual partial/full eight-member blast publication.");
            Assert.That(_callbacks.All(c => c == 1) && _terrain.PendingTransactions == 0, Is.True);
        }

        [UnityTest, Timeout(600000)] public IEnumerator ClearAfterARealPartialPublicationRetiresAllMembersOnceAndDropsUnpublishedDraft()
        {
            yield return InstallVictims(24); BeginCallbacks(8);
            _terrain.Tick(false, SafeAt, .016f); QueueBlasts(0, 8); RetainCommands();
            yield return FindPartialPendingAfterPublishedCredit();
            var oldWorld = _terrain.World; var oldBytes = oldWorld.EncodeSnapshot();
            var totals = _commands.Select(c => Read<BlockMutationResult>(c, "Total")).ToArray();
            Assert.That(totals.Sum(r => r.RemovedEnemy), Is.GreaterThan(0));
            Assert.That(_callbacks.Sum(), Is.Zero);
            _terrain.Clear(_terrain.Round + 1);
            AssertCallbacksOnce(); Assert.That(Queue(), Is.Empty);
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
            Assert.That(Read<object>(_terrain, "_blastBatch"), Is.Null);
            // Current round-cancellation contract returns default, unlike Fail(Total).
            // It must never credit the discarded pending deletions.
            Assert.That(_results.All(r => r.Changed == 0), Is.True);
            foreach (var command in _commands) Assert.That(Read<object>(command, "Source"), Is.Null);
            CollectionAssert.AreEqual(oldBytes, oldWorld.EncodeSnapshot());
            for (int frame = 0; frame < 12; frame++) { _terrain.Tick(true, 20, .016f); yield return null; }
            AssertCallbacksOnce(); Assert.That(_terrain.PendingTransactions, Is.Zero);
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)] public IEnumerator CompletedBakeFaultRunsActualFailAndReturnsOnlyEarlierPublishedTotals()
        {
            yield return InstallVictims(24); BeginCallbacks(8);
            _terrain.Tick(false, SafeAt, .016f); QueueBlasts(0, 8); RetainCommands();
            yield return FindPartialPendingAfterPublishedCredit();
            var pending = Read<object>(_terrain, "_pending");
            var before = _terrain.World; uint revision = _terrain.Revision;
            var bytes = before.EncodeSnapshot(); long failed = _terrain.FailedTransactions;
            var totals = _commands.Select(c => Read<BlockMutationResult>(c, "Total")).ToArray();
            Assert.That(totals.Sum(r => r.RemovedEnemy), Is.GreaterThan(0));
            Assert.That(((IEnumerable)Read<object>(pending, "Results")).Cast<object>()
                .Sum(pair => Read<BlockMutationResult>(pair, "Item2").RemovedEnemy), Is.GreaterThan(0));
            Array pieces = null;
            for (int frame = 0; frame < PumpLimit; frame++)
            {
                Assert.That(Read<object>(_terrain, "_pending"), Is.SameAs(pending));
                pieces = Read<Array>(pending, "Pieces");
                if (pieces != null && pieces.Length > 0 && Read<int>(pending, "Uploaded") == pieces.Length
                    && pieces.Cast<object>().All(piece => Read<Task>(piece, "Bake")?.IsCompleted == true)) break;
                // Normal Update prepares upload/Bake while never publishing.
                yield return null;
            }
            Assert.That(pieces, Is.Not.Null); Assert.That(pieces.Length, Is.GreaterThan(0));
            Assert.That(pieces.Cast<object>().All(piece => Read<Task>(piece, "Bake")?.IsCompleted == true), Is.True);
            // Explicit bounded fault injection after actual workers finish. Keep
            // all generated Pieces and meshes owned by the normal retired path.
            Set(pieces.GetValue(0), "Bake", Task.FromException(new InvalidOperationException("Synthetic completed batch bake fault")));
            LogAssert.Expect(LogType.Warning, new Regex("\\[BLOCK\\] Atomic terrain transaction rejected: .*Synthetic completed batch bake fault"));
            _terrain.Tick(true, SafeAt, .016f);
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failed + 1));
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(_terrain.Revision, Is.EqualTo(revision));
            CollectionAssert.AreEqual(bytes, before.EncodeSnapshot());
            AssertVictimCollisionMatchesPublishedWorld();
            AssertCallbacksOnce(); Assert.That(Queue(), Is.Empty);
            for (int i = 0; i < 8; i++)
            {
                Assert.That(_results[i].RemovedEnemy, Is.EqualTo(totals[i].RemovedEnemy));
                Assert.That(_results[i].RemovedFriendly, Is.EqualTo(totals[i].RemovedFriendly));
                Assert.That(Read<object>(_commands[i], "Source"), Is.Null);
            }
            Assert.That(_outcomes.Contains(BlockTerrain.PendingWallOutcome.Failed), Is.True);
            Assert.That(_results.Sum(r => r.RemovedEnemy), Is.EqualTo(_source.BlueCount - before.BlueCount));
            for (int frame = 0; frame < 12; frame++) { _terrain.Tick(false, SafeAt, .016f); yield return null; }
            AssertCallbacksOnce(); CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
        }

        IEnumerator InstallVictims(int count)
        {
            _source = null; _frameChunks.Clear();
            _victims = Enumerable.Range(0, count).Select(i => count <= 3 ? new Vector3Int(34 + i, 1, 34)
                : new Vector3Int(34 + i % 6 * 16, 1, 34 + i / 6 * 16)).ToArray();
            var seed = new BlockWorld(new Vector2(32, 64), _terrain.Round);
            foreach (var cell in _victims) seed.Set(cell, BlockCell.Create(2, true));
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return Pump(false, Started, () => _terrain.PendingTransactions == 0);
            _terrain.Tick(false, Started, .016f);
        }
        IEnumerator CreateNaturalMovingRoot()
        {
            int built = 0, destroyed = 0;
            _terrain.QueueBuild(BuildPoint(Floor), Vector3.up, 2, 6, 0, false, result =>
            { built++; Assert.That(result.Added, Is.EqualTo(6)); });
            yield return Pump(true, Started, () => built == 1 && _terrain.PendingTransactions == 0);
            _terrain.QueueDestroy(new Vector3(.25f, .3f, .25f), .1001f, 0, 1, true, result =>
            { destroyed++; Assert.That(result.RemovedEnemy, Is.EqualTo(4)); }, 4, 0);
            yield return Pump(true, Started, () => destroyed == 1 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => c.Bottom == Floor + Vector3Int.up * 5 && c.Phase == FallingCohortPhase.Falling));
            var moving = _terrain.World.Cohorts.Values.Single(c => c.Bottom == Floor + Vector3Int.up * 5);
            Assert.That(moving.LandingKnown, Is.True); Assert.That(moving.LandingOffset, Is.EqualTo(.4f).Within(1e-6));
            Assert.That(moving.StartedAt, Is.EqualTo(Started));
            Assert.That(moving.Offset(RejectAt), Is.EqualTo(moving.LandingOffset).Within(1e-6));
        }
        void BeginCallbacks(int count)
        {
            _callbacks = new int[count]; _results = new BlockMutationResult[count]; _callbackOrder.Clear();
        }
        Action<BlockMutationResult> Result(int id) => result =>
        {
            _callbacks[id]++; _results[id] = result; _callbackOrder.Add(id);
            Assert.That(_callbacks[id], Is.EqualTo(1), "Each original paid command retires once.");
        };
        void QueueBlasts(int firstId, int count)
        {
            var low = _victims.Select(BlockWorld.Center).Aggregate(Vector3.Min);
            var high = _victims.Select(BlockWorld.Center).Aggregate(Vector3.Max);
            var center = (low + high) * .5f; float radius = Vector3.Distance(low, high) * .5f + .1f;
            for (int i = 0; i < count; i++) _terrain.QueueDestroy(center, radius, radius * .5f, 1, false, Result(firstId + i));
        }
        void RetainCommands()
        {
            _commands = Queue(); _source = _terrain.World; _sourceBytes = _source.EncodeSnapshot();
            _sourceTime = Read<double>(_commands.First(c => !Read<bool>(c, "Build")), "SourceTime");
            AssertFrozenInputsBeforeComplete();
        }
        IEnumerator FindPartialPendingAfterPublishedCredit()
        {
            _witness = null;
            for (int frame = 0; frame < PumpLimit; frame++)
            {
                _terrain.Tick(true, SafeAt, .016f);
                var pending = Read<object>(_terrain, "_pending");
                var cp = BatchCheckpoint(pending == null ? null : Read<object>(pending, "CommandCheckpoints"));
                if (cp != null && !Read<bool>(Read<object>(cp, "BlastBatch"), "Dequeued")
                    && _commands.Sum(c => Read<BlockMutationResult>(c, "Total").RemovedEnemy) > 0
                    && ((IEnumerable)Read<object>(pending, "Results")).Cast<object>()
                        .Sum(pair => Read<BlockMutationResult>(pair, "Item2").RemovedEnemy) > 0)
                { _witness = pending; break; }
                Assert.That(_callbacks.Sum(), Is.Zero, "Need earlier publication plus later still-partial pending, not final callback.");
                yield return null;
            }
            Assert.That(_witness, Is.Not.Null, "Required actual earlier publication and subsequent partial deletion pending were not reached.");
            AssertFrozenInputsBeforeComplete(); AssertQueuedReferencesAreUnique();
        }
        IEnumerator Pump(bool authority, double now, Func<bool> ready)
        {
            for (int frame = 0; frame < PumpLimit && !ready(); frame++)
            { _terrain.Tick(authority, now, .016f); yield return null; }
            Assert.That(ready(), Is.True, "Bounded real Tick pump did not reach its required witness; no synthetic success/skip.");
        }
        static object BatchCheckpoint(object list)
        {
            if (list == null) return null;
            return ((IEnumerable)list).Cast<object>().FirstOrDefault(cp => Read<object>(cp, "BlastBatch") != null);
        }
        object[] Queue() => ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().ToArray();
        void AssertOriginalQueue(int first)
        {
            var queue = Queue(); Assert.That(queue.Length, Is.EqualTo(_commands.Length - first));
            for (int i = 0; i < queue.Length; i++) Assert.That(queue[i], Is.SameAs(_commands[i + first]));
            AssertQueuedReferencesAreUnique();
        }
        void AssertQueuedReferencesAreUnique()
        {
            var queue = Queue();
            for (int i = 0; i < queue.Length; i++) for (int j = i + 1; j < queue.Length; j++)
                Assert.That(ReferenceEquals(queue[i], queue[j]), Is.False, "No command reference can appear twice in real queue.");
        }
        void AssertFrozenInputsBeforeComplete()
        {
            foreach (var command in _commands)
                if (!Read<bool>(command, "Build") && !Read<bool>(command, "Completed"))
                {
                    Assert.That(Read<BlockWorld>(command, "Source"), Is.SameAs(_source));
                    Assert.That(Read<double>(command, "SourceTime"), Is.EqualTo(_sourceTime));
                }
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
        }
        void AssertCallbacksOnce() => CollectionAssert.AreEqual(Enumerable.Repeat(1, _callbacks.Length), _callbacks);
        void AssertRemovedCellAndCollision(Vector3Int cell)
        {
            Assert.That(_terrain.World.TryGet(cell, out _), Is.False);
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .08f, Vector3.down, .16f,
                1 << BlockTerrain.TerrainLayer), Is.False);
        }
        void AssertVictimCollisionMatchesPublishedWorld()
        {
            foreach (var cell in _victims)
            {
                bool exists = _terrain.World.TryGet(cell, out _);
                bool collision = Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .08f, Vector3.down, .16f,
                    1 << BlockTerrain.TerrainLayer);
                Assert.That(collision, Is.EqualTo(exists), "Published cell and real collider differ at " + cell);
            }
        }
        void ObservePublication(BlockTerrainCommit commit)
        {
            if (_source == null) return; // QueueSeed is a full remote diagnostic install, not an authority eight-chunk stage.
            _frameChunks.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length;
            _frameChunks[Time.frameCount] = count; Assert.That(count, Is.LessThanOrEqualTo(8));
            int stable = _terrain.World.Chunks.Values.Sum(chunk => chunk.Cells.Count(cell => cell.Occupied));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(stable + _terrain.FallingCount));
        }
        // Diagnostic observers swallow exceptions in runtime, so only record here.
        void ObserveTerminal(BlockTerrain.PendingWallDiagnostic sample) => _outcomes.Add(sample.Outcome);
        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
        static T Read<T>(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null, "Required real owner of field " + name);
            var field = owner.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, "Required current fixture field " + name);
            return (T)field.GetValue(owner);
        }
        static void Set(object owner, string name, object value)
        {
            var field = owner.GetType().GetField(name, Fields); Assert.That(field, Is.Not.Null); field.SetValue(owner, value);
        }
    }
}
#endif
