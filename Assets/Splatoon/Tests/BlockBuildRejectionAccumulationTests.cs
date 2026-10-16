#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Regression for moving-build retry convergence and source/time invalidation.
    // The 84-cell source is constructed through BlockWorld's public snapshot API.
    // All attacks, replay, collision publication, budgets and callbacks use the
    // actual Terrain path. Reflection only observes owners and registers the map.
    public sealed class BlockBuildRejectionAccumulationTests
    {
        const double Epoch = 10;
        const int ChainLength = 84, BottomY = 16, PumpLimit = 2000;
        // An isolated two-address replay that still has no terminal after eight
        // publication rejections is a failing liveness oracle, not a production limit.
        const long RejectionFailureLimit = 8;
        static readonly Vector3Int FirstOverlap = new(2, 60, 2);
        static readonly Vector3Int SecondOverlap = new(2, 61, 2);
        static readonly Vector3Int SafeCell = new(30, 0, 30);
        GameObject _root, _floor;
        BlockTerrain _terrain;
        bool _arm;
        readonly List<object> _commands = new();
        readonly HashSet<object> _observedPending = new();
        readonly HashSet<Task> _observedCommandTasks = new();
        readonly HashSet<Mesh> _observedMeshes = new();
        readonly HashSet<GameObject> _observedViews = new();
        readonly HashSet<Material> _observedMaterials = new();
        BlockWorkerQueue _admissions;
        SemaphoreSlim _workers;
        int _largestObservedColumnQuery;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _arm = true;
            _commands.Clear(); _observedPending.Clear(); _observedCommandTasks.Clear();
            _observedMeshes.Clear(); _observedViews.Clear(); _observedMaterials.Clear();
            _admissions = null; _workers = null; _largestObservedColumnQuery = 0;
            yield return WaitForBootstrapInitialization();
            HeroMigrationTests.Load(); BlockAttackService.Clear();
            _root = new GameObject("Two real moving-build rejections under one published source");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(3801);
            _terrain.Initialize(null);
            _admissions = ReadStatic<BlockWorkerQueue>("WorkerAdmissions");
            _workers = ReadStatic<SemaphoreSlim>("Workers");
            _floor = new GameObject("Registered map floor with top zero");
            _floor.transform.SetParent(_root.transform, false);
            _floor.transform.position = new Vector3(0, -.05f, 0);
            _floor.AddComponent<BoxCollider>().size = new Vector3(32, .1f, 64);
            Read<HashSet<Collider>>(_terrain, "_staticColliders").Add(_floor.GetComponent<Collider>());
            Physics.SyncTransforms();
            _terrain.Tick(false, Epoch, .016f);
        }

        static IEnumerator WaitForBootstrapInitialization()
        {
            // EnterPlayMode starts the real bootstrap even in an empty scene.
            // Fixture config injection releases weapon handles, so wait until
            // Initialize has really finished owning its Addressables operations.
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypeApp.Current == null || PrototypeApp.Current.Busy)
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            var app = PrototypeApp.Current;
            string diagnostic = $"Real bootstrap before fixture config injection: present={app != null}"
                + $" ready={app?.Ready} busy={app?.Busy} status={app?.Status} error={app?.Error}";
            Assert.That(app, Is.Not.Null, diagnostic);
            Assert.That(app.Busy, Is.False, diagnostic);
            Assert.That(app.Ready, Is.True, diagnostic);
            Assert.That(app.Error, Is.Empty, diagnostic);
            Assert.That(LubanConfigService.Current.IsReady, Is.True, diagnostic);
        }
        [UnityTearDown]
        public IEnumerator Teardown()
        {
            // Only teardown disarms callbacks. All live test Clear/authority-loss
            // paths retain their original once-only, payment and FIFO assertions.
            _arm = false;
            var terrain = _terrain;
            // Capture actual ownership before Clear/OnDestroy clears Sorting or
            // transfers Pending to retired work; queue idle alone is insufficient.
            RememberActualPending(terrain);
            if (!ReferenceEquals(terrain, null)) terrain.Clear(terrain.Round + 1);
            if (_root != null) Object.DestroyImmediate(_root);
            double until = Time.realtimeSinceStartupAsDouble + 180;
            while (!FailureCleanupJoined(terrain) && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(FailureCleanupJoined(terrain), Is.True,
                "Actual Generate/Scheduled/member/Bake/Sorting Tasks, admissions and retired ownership must join before leaving Play Mode.");
            until = Time.realtimeSinceStartupAsDouble + 180;
            while (!PendingResourcesDestroyed() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(PendingResourcesDestroyed(), Is.True,
                "Actual native Mesh/View/Material ownership must finish Unity's deferred destruction before leaving Play Mode.");
            ObserveJoinedPendingFaults();
            if (_workers != null) Assert.That(_workers.CurrentCount, Is.EqualTo(2));
            _observedPending.Clear(); _observedCommandTasks.Clear(); _observedMeshes.Clear();
            _observedViews.Clear(); _observedMaterials.Clear(); _terrain = null; _root = _floor = null;
            BlockAttackService.Clear(); LubanConfigService.Current.Reset();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void RememberActualPending(BlockTerrain terrain)
        {
            if (ReferenceEquals(terrain, null)) return;
            var current = Read<object>(terrain, "_pending");
            if (current != null) _observedPending.Add(current);
            foreach (var entry in (IEnumerable)Read<object>(terrain, "_retired"))
                _observedPending.Add(Read<object>(entry, "Pending"));
            foreach (var pending in _observedPending) RememberPendingResources(pending);
            // Published ownership has moved out of Prepared; keep the genuine
            // resources strongly referenced until Unity reports destruction.
            foreach (var view in Read<IDictionary>(terrain, "_views").Values.Cast<object>())
                RememberViewResources(view);
            var fallMesh = Read<Mesh>(terrain, "_fallMesh"); var material = Read<Material>(terrain, "_material");
            if (!ReferenceEquals(fallMesh, null)) _observedMeshes.Add(fallMesh);
            if (!ReferenceEquals(material, null)) _observedMaterials.Add(material);
            // Include commands retained by this fixture and the real scheduler:
            // a private owner may already have removed one from the public queue.
            foreach (var command in _commands
                .Concat(((IEnumerable)Read<object>(terrain, "_commands")).Cast<object>())
                .Concat(((IEnumerable)Read<object>(terrain, "_destructionReservations")).Cast<object>()))
            {
                var sorting = Read<Task>(command, "Sorting");
                if (sorting != null) _observedCommandTasks.Add(sorting);
            }
        }
        bool FailureCleanupJoined(BlockTerrain terrain)
        {
            if (_admissions != null && !_admissions.WhenIdle.IsCompleted) return false;
            if (_observedCommandTasks.Any(task => !task.IsCompleted)) return false;
            // Unity's destroyed object compares null before async OnDestroy has
            // joined its Tasks. Retain and read the managed instance explicitly.
            if (!ReferenceEquals(terrain, null))
            {
                if (Read<object>(terrain, "_pending") != null) return false;
                if (((IEnumerable)Read<object>(terrain, "_retired")).Cast<object>().Any()) return false;
            }
            foreach (var pending in _observedPending)
            {
                if (!Read<Task>(pending, "Generate").IsCompleted) return false;
                var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task");
                if (!scheduled.IsCompleted) return false;
                if (scheduled.Status == TaskStatus.RanToCompletion
                    && Property<Task[]>(scheduled, "Result").Any(task => !task.IsCompleted)) return false;
                var pieces = Read<Array>(pending, "Pieces");
                if (pieces != null) foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var bake = Read<Task>(piece, "Bake");
                    if (bake != null && !bake.IsCompleted) return false;
                }
                if (Read<int>(pending, "AdmissionsDisposed") != 1) return false;
            }
            return true;
        }
        bool PendingResourcesDestroyed()
            => _observedMeshes.All(mesh => mesh == null) && _observedViews.All(view => view == null)
                && _observedMaterials.All(material => material == null);
        void RememberPendingResources(object pending)
        {
            var pieces = Read<Array>(pending, "Pieces");
            if (pieces == null) return;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                var render = Read<Mesh>(piece, "RenderMesh"); var collision = Read<Mesh>(piece, "CollisionMesh");
                if (!ReferenceEquals(render, null)) _observedMeshes.Add(render);
                if (!ReferenceEquals(collision, null)) _observedMeshes.Add(collision);
                RememberViewResources(Read<object>(piece, "View"));
            }
        }
        void RememberViewResources(object view)
        {
            if (view == null) return;
            var obj = Read<GameObject>(view, "Object");
            var render = Read<Mesh>(view, "RenderMesh"); var collision = Read<Mesh>(view, "CollisionMesh");
            if (!ReferenceEquals(obj, null)) _observedViews.Add(obj);
            if (!ReferenceEquals(render, null)) _observedMeshes.Add(render);
            if (!ReferenceEquals(collision, null)) _observedMeshes.Add(collision);
        }
        void ObserveJoinedPendingFaults()
        {
            foreach (var task in _observedCommandTasks) if (task.IsFaulted) _ = task.Exception;
            foreach (var pending in _observedPending)
            {
                var generate = Read<Task>(pending, "Generate");
                if (generate.IsFaulted) _ = generate.Exception;
                var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task");
                if (scheduled.IsFaulted) _ = scheduled.Exception;
                if (scheduled.Status == TaskStatus.RanToCompletion)
                    foreach (var task in Property<Task[]>(scheduled, "Result")) if (task.IsFaulted) _ = task.Exception;
                var pieces = Read<Array>(pending, "Pieces");
                if (pieces != null) foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var bake = Read<Task>(piece, "Bake"); if (bake != null && bake.IsFaulted) _ = bake.Exception;
                }
                var stop = Read<CancellationTokenSource>(pending, "AdmissionStop");
                Assert.Throws<ObjectDisposedException>(() => { _ = stop.Token; });
            }
        }
        static T Property<T>(object target, string name)
        {
            Assert.That(target, Is.Not.Null, name); var property = target.GetType().GetProperty(name);
            Assert.That(property, Is.Not.Null, name); return (T)property.GetValue(target);
        }
        static T ReadStatic<T>(string field)
            => (T)typeof(BlockTerrain).GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
        [UnityTest, Timeout(600000)]
        public IEnumerator TwoOccupiedCandidatesCannotAlternateTheirRejectionsOrStarveTheFollowingPaidBuild()
        {
            var seed = MakePublishedChain();
            uint installedRevision = _terrain.Revision + 1;
            seed.Seal(installedRevision, seed.RevisionKeys.ToArray());
            byte[] wire = seed.EncodeSnapshot();
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return PumpRemoteInstall(installedRevision);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            var publishedBefore = _terrain.World;
            byte[] frozenBefore = publishedBefore.EncodeSnapshot();
            Assert.That(PhysicalOwnersAt(publishedBefore, FirstOverlap, Epoch), Is.EqualTo(1));
            Assert.That(PhysicalOwnersAt(publishedBefore, SecondOverlap, Epoch), Is.EqualTo(1));
            Assert.That(publishedBefore.TryGet(FirstOverlap, out _), Is.False);
            Assert.That(publishedBefore.TryGet(SecondOverlap, out _), Is.False);
            Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.BlueCount, Is.EqualTo(ChainLength));

            var tokens = new[] { MakeToken(1), MakeToken(2) };
            int[] reserved = { tokens[0].ReserveBuild(2, false), tokens[1].ReserveBuild(1, false) };
            CollectionAssert.AreEqual(new[] { 2, 1 }, reserved);
            var order = new List<int>();
            var outcomes = new BlockMutationResult[2];
            int[] callbacks = { 0, 0 };
            Action<int, BlockMutationResult> finish = (index, result) =>
            {
                if (!_arm) return;
                callbacks[index]++;
                Assert.That(callbacks[index], Is.EqualTo(1), "Each original payment completes only once.");
                Assert.That(result.OrdinaryMainAttack, Is.True);
                outcomes[index] = result;
                tokens[index].CompleteBuild(reserved[index], false, result.Added);
                order.Add(index);
            };
            long retryBaseline = _terrain.BuildPublicationRetries;
            long failedBaseline = _terrain.FailedTransactions;
            _terrain.QueueBuild(BlockWorld.Center(FirstOverlap) - Vector3.up * .051f,
                Vector3.up, 1, reserved[0], 0, false, result => finish(0, result), true);
            RememberLastCommand();
            _terrain.QueueBuild(BlockWorld.Center(SafeCell) - Vector3.up * .051f,
                Vector3.up, 1, reserved[1], 0, true, result => finish(1, result), true);
            RememberLastCommand();
            int frames = 0;
            for (; frames < PumpLimit && !Terminal(callbacks)
                && _terrain.BuildPublicationRetries - retryBaseline < RejectionFailureLimit; frames++)
            {
                RememberActualPending(_terrain);
                _terrain.Tick(true, Epoch, .016f);
                ObserveLargeColumnProof();
                RememberActualPending(_terrain);
                Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
                AssertPublishedConservation(tokens);
                CollectionAssert.AreEqual(frozenBefore, publishedBefore.EncodeSnapshot(),
                    "All rejected drafts and eventual commits retain the old published source byte-for-byte.");
                yield return null;
            }
            string diagnostic = $"frames={frames} callbacks={callbacks[0]}/{callbacks[1]} "
                + $"retries={_terrain.BuildPublicationRetries - retryBaseline} "
                + $"movingOccupied={_terrain.BuildMovingOccupied} largestColumnQuery={_largestObservedColumnQuery} "
                + $"pending={_terrain.PendingTransactions} activeBuild={Read<object>(_terrain, "_buildOccupancyPreparation") != null}";
            Assert.That(_largestObservedColumnQuery, Is.GreaterThan(4096),
                "The real finite column proof must exceed the unchanged maximum ordinary inline query allowance. " + diagnostic);
            Assert.That(_terrain.BuildPublicationRetries - retryBaseline, Is.GreaterThanOrEqualTo(1),
                "The scenario must exercise an actual final-publication overlap rejection. " + diagnostic);
            Assert.That(Terminal(callbacks), Is.True,
                "Two fixed-source occupied candidates must be rejected finitely and allow the next original payment. " + diagnostic);
            CollectionAssert.AreEqual(new[] { 0, 1 }, order, "Original paid FIFO order survives every replay.");
            CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks);
            Assert.That(outcomes[0].Added, Is.Zero);
            Assert.That(outcomes[1].Added, Is.EqualTo(1));
            Assert.That(tokens[0].Added, Is.Zero);
            Assert.That(tokens[0].BuildRemaining, Is.EqualTo(tokens[0].BuildLimit));
            Assert.That(tokens[1].Added, Is.EqualTo(1));
            Assert.That(tokens[1].Added + tokens[1].BuildRemaining, Is.EqualTo(tokens[1].BuildLimit));
            Assert.That(tokens[0].TakePoints(), Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.BlueCount, Is.EqualTo(ChainLength));
            Assert.That(_terrain.World.TryGet(FirstOverlap, out _), Is.False);
            Assert.That(_terrain.World.TryGet(SecondOverlap, out _), Is.False);
            Assert.That(_terrain.World.TryGet(SafeCell, out var stable) && stable.Owner == 1, Is.True);
            Assert.That(PhysicalOwnersAt(_terrain.World, FirstOverlap, Epoch), Is.EqualTo(1));
            Assert.That(PhysicalOwnersAt(_terrain.World, SecondOverlap, Epoch), Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failedBaseline));
            Physics.SyncTransforms();
            foreach (var cell in new[] { FirstOverlap, SecondOverlap })
                Assert.That(Physics.OverlapBox(BlockWorld.Center(cell), Vector3.one * .04f,
                    Quaternion.identity, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.Empty,
                    "Rejected additions must leave no stable Terrain collider inside a moving survivor.");
            Assert.That(Physics.Raycast(BlockWorld.Center(SafeCell) + Vector3.up * .2f,
                Vector3.down, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True,
                "The following paid addition publishes its real stable collision.");
            byte[] finalWire = _terrain.ExportSnapshot();
            var decoded = BlockWorld.DecodeSnapshot(finalWire);
            Assert.That(decoded.PinkCount, Is.EqualTo(1));
            Assert.That(decoded.BlueCount, Is.EqualTo(ChainLength));
            CollectionAssert.AreEqual(finalWire, decoded.EncodeSnapshot());
            CollectionAssert.AreEqual(frozenBefore, publishedBefore.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ARealRejectedPaymentCanBuildBothFormerlyOccupiedCellsAfterTimeAdvances()
        {
            var first = new Vector3Int(2, 90, 2);
            var second = new Vector3Int(2, 91, 2);
            var seed = MakePublishedChain();
            uint installedRevision = _terrain.Revision + 1;
            seed.Seal(installedRevision, seed.RevisionKeys.ToArray());
            byte[] wire = seed.EncodeSnapshot();
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return PumpRemoteInstall(installedRevision);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            var publishedBefore = _terrain.World;
            byte[] frozenBefore = publishedBefore.EncodeSnapshot();
            foreach (var cell in new[] { first, second })
            {
                Assert.That(PhysicalOwnersAt(publishedBefore, cell, Epoch), Is.EqualTo(1));
                Assert.That(PhysicalOwnersAt(publishedBefore, cell, Epoch + 2), Is.Zero,
                    "The independently sampled old bodies genuinely vacate these addresses at the later clock.");
                Assert.That(publishedBefore.TryGet(cell, out _), Is.False);
            }
            var token = MakeToken(3);
            int reserved = token.ReserveBuild(2, false);
            Assert.That(reserved, Is.EqualTo(2));
            int callbacks = 0, publishedCellWitnesses = 0;
            bool advanced = false;
            var outcome = new BlockMutationResult();
            long retryBaseline = _terrain.BuildPublicationRetries;
            int failedBaseline = _terrain.FailedTransactions;
            _terrain.QueueBuild(BlockWorld.Center(first) - Vector3.up * .051f,
                Vector3.up, 1, reserved, 0, false, result =>
                {
                    if (!_arm) return;
                    callbacks++;
                    Assert.That(callbacks, Is.EqualTo(1), "The original payment completes once after its rejected draft.");
                    Assert.That(advanced, Is.True, "Completion must follow the actual rejection and real Tick clock change.");
                    Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch + 2));
                    Assert.That(result.OrdinaryMainAttack, Is.True);
                    outcome = result;
                    token.CompleteBuild(reserved, false, result.Added);
                    Assert.That(result.Added, Is.EqualTo(2),
                        "An old occupied-address fact must not permanently discard either now-empty candidate.");
                    // Observe the actual published phase at the callback boundary.
                    // Unsupported additions may already be falling in this same
                    // valid transaction; neither a map anchor nor a frozen phase
                    // is added just to manufacture a stable collision witness.
                    Physics.SyncTransforms();
                    foreach (var cell in new[] { first, second })
                    {
                        AssertPublishedPaidCell(cell);
                        publishedCellWitnesses++;
                    }
                }, true);
            RememberLastCommand();
            int frames = 0;
            for (; frames < PumpLimit && callbacks == 0; frames++)
            {
                RememberActualPending(_terrain);
                double now = advanced ? Epoch + 2 : Epoch;
                _terrain.Tick(true, now, .016f);
                ObserveLargeColumnProof();
                RememberActualPending(_terrain);
                Assert.That(_terrain.SimulationTime, Is.EqualTo(now));
                AssertPublishedConservation(token);
                CollectionAssert.AreEqual(frozenBefore, publishedBefore.EncodeSnapshot());
                if (!advanced && _terrain.BuildPublicationRetries > retryBaseline) advanced = true;
                if (callbacks == 0) yield return null;
            }
            string diagnostic = $"frames={frames} callbacks={callbacks} advanced={advanced} "
                + $"retries={_terrain.BuildPublicationRetries - retryBaseline} added={token.Added} "
                + $"largestColumnQuery={_largestObservedColumnQuery} pending={_terrain.PendingTransactions}";
            Assert.That(_terrain.BuildPublicationRetries - retryBaseline, Is.GreaterThanOrEqualTo(1), diagnostic);
            Assert.That(advanced, Is.True, diagnostic);
            Assert.That(_largestObservedColumnQuery, Is.GreaterThan(4096), diagnostic);
            Assert.That(callbacks, Is.EqualTo(1), diagnostic);
            Assert.That(publishedCellWitnesses, Is.EqualTo(2), diagnostic);
            Assert.That(outcome.Added, Is.EqualTo(2));
            Assert.That(outcome.RemovedEnemy + outcome.RemovedFriendly, Is.Zero);
            Assert.That(token.Added, Is.EqualTo(2));
            Assert.That(token.BuildRemaining, Is.EqualTo(token.BuildLimit - 2));
            Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(token.BuildLimit));
            Assert.That(_terrain.PinkCount, Is.EqualTo(2));
            Assert.That(_terrain.BlueCount, Is.EqualTo(ChainLength));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failedBaseline));
            byte[] finalWire = _terrain.ExportSnapshot();
            var decoded = BlockWorld.DecodeSnapshot(finalWire);
            Assert.That(decoded.PinkCount, Is.EqualTo(2));
            Assert.That(decoded.BlueCount, Is.EqualTo(ChainLength));
            CollectionAssert.AreEqual(finalWire, decoded.EncodeSnapshot());
            CollectionAssert.AreEqual(frozenBefore, publishedBefore.EncodeSnapshot());
        }

        void AssertPublishedConservation(params BlockAttackToken[] tokens)
        {
            // A command can publish paid partials before its once-only completion
            // callback updates the token. Count those committed results, then
            // independently count actual payloads rather than cached team totals.
            int published = _commands.Sum(command => Read<BlockMutationResult>(command, "Total").Added);
            int pink = 0, blue = 0;
            foreach (var chunk in _terrain.World.Chunks.Values)
                foreach (var cell in chunk.Cells)
                    if (cell.Occupied)
                    { if (cell.Owner == 1) pink++; else if (cell.Owner == 2) blue++; }
            foreach (var run in _terrain.World.Falling.Values)
                foreach (var cell in run.Cells)
                    if (cell.Occupied)
                    { if (cell.Owner == 1) pink++; else if (cell.Owner == 2) blue++; }
            Assert.That(blue, Is.EqualTo(ChainLength), "No original moving payload is lost or duplicated.");
            Assert.That(pink, Is.EqualTo(published), "Only committed paid partials add actual payloads.");
            Assert.That(_terrain.PinkCount, Is.EqualTo(pink));
            Assert.That(_terrain.BlueCount, Is.EqualTo(blue));
            Assert.That(published, Is.LessThanOrEqualTo(tokens.Sum(token => token.BuildLimit - token.BuildRemaining)),
                "Every published partial remains backed by the original finite material reservation.");
            Assert.That(tokens.Sum(token => token.Added), Is.LessThanOrEqualTo(published));
        }

        void AssertPublishedPaidCell(Vector3Int cell)
        {
            if (_terrain.World.TryGet(cell, out var stable))
            {
                Assert.That(stable.Owner, Is.EqualTo(1));
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.right * .2f,
                    Vector3.left, out var native, .4f, 1 << BlockTerrain.TerrainLayer,
                    QueryTriggerInteraction.Ignore), Is.True, "A stable published addition has real native collision.");
                Assert.That(_terrain.IsStableCollider(native.collider), Is.True);
                Assert.That(_terrain.TryContact(native, out var contact), Is.True);
                Assert.That(contact.Cell, Is.EqualTo(cell));
                Assert.That(contact.Owner, Is.EqualTo(1));
                Assert.That(contact.Stable, Is.True);
                return;
            }
            FallingRun found = null;
            int foundIndex = -1, owners = 0;
            foreach (var run in _terrain.World.Falling.Values)
                for (int index = 0; index < run.Cells.Length; index++)
                    if (run.Bottom + Vector3Int.up * index == cell && run.Cells[index].Occupied
                        && run.Cells[index].Owner == 1)
                    { found = run; foundIndex = index; owners++; }
            Assert.That(owners, Is.EqualTo(1), "A falling addition retains exactly one real paid cell at its source address.");
            Vector3 center = BlockWorld.Center(found.Bottom + Vector3Int.up * foundIndex)
                - Vector3.up * found.Offset(_terrain.SimulationTime);
            Assert.That(_terrain.RaycastFalling(center + Vector3.right * .2f, Vector3.left, .4f, 0,
                out var hit, 2), Is.True);
            Assert.That(hit.RunId, Is.EqualTo(found.Id));
            Assert.That(hit.Cell, Is.EqualTo(cell));
            Assert.That(hit.Owner, Is.EqualTo(1));
            Assert.That(hit.Collider.enabled, Is.False, "A moving hit proxy never becomes a locomotion collider.");
            Assert.That(_terrain.IsStableCollider(hit.Collider), Is.False);
            Assert.That(_terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var fallingContact), Is.True);
            Assert.That(fallingContact.Cell, Is.EqualTo(cell));
            Assert.That(fallingContact.Owner, Is.EqualTo(1));
            Assert.That(fallingContact.Stable, Is.False);
            Assert.That(fallingContact.Climbable, Is.False);
        }

        bool Terminal(int[] callbacks) => callbacks[0] == 1 && callbacks[1] == 1
            && _terrain.PendingTransactions == 0 && Read<object>(_terrain, "_dependentEditPreparation") == null
            && Read<object>(_terrain, "_buildOccupancyPreparation") == null;

        IEnumerator PumpRemoteInstall(uint revision)
        {
            int frame = 0;
            for (; frame < PumpLimit && (_terrain.Revision != revision || Read<object>(_terrain, "_pending") != null); frame++)
            {
                RememberActualPending(_terrain);
                _terrain.Tick(false, Epoch, .016f);
                RememberActualPending(_terrain);
                yield return null;
            }
            Assert.That(_terrain.Revision, Is.EqualTo(revision));
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
        }
        static BlockWorld MakePublishedChain()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            ulong lower = 0;
            for (int i = 0; i < ChainLength; i++)
                world.Set(new Vector3Int(2, BottomY + i, 2), BlockCell.Create(2));
            for (int i = 0; i < ChainLength; i++)
            {
                var group = world.BeginCohort(new Vector3Int(2, BottomY + i, 2), 1);
                world.DetachCohortPiece(group.Id, 1, Epoch, out _);
                world.SetCohortSupport(group.Id, lower, 0, out _);
                group = world.Cohorts[group.Id];
                group.LandingKnown = true;
                group.LandingOffset = (BottomY + i) * BlockWorld.CellSize;
                group.NeedsLandingProof = false;
                lower = group.Id;
            }
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray());
            return world;
        }
        static int PhysicalOwnersAt(BlockWorld world, Vector3Int cell, double now)
        {
            // An independent physical interval oracle, with no production cache,
            // occupancy masks, proof columns, or rejected-address dictionary.
            int count = 0;
            foreach (var run in world.Falling.Values)
            {
                if (run.Bottom.x != cell.x || run.Bottom.z != cell.z) continue;
                float displacement = run.Offset(now);
                for (int index = 0; index < run.Cells.Length; index++)
                {
                    if (!run.Cells[index].Occupied) continue;
                    float bottom = (run.Bottom.y + index) * BlockWorld.CellSize - displacement;
                    float candidateBottom = cell.y * BlockWorld.CellSize;
                    if (bottom < candidateBottom + BlockWorld.CellSize - 1e-6f
                        && bottom + BlockWorld.CellSize > candidateBottom + 1e-6f) count++;
                }
            }
            return count;
        }
        static BlockAttackToken MakeToken(ulong owner)
            => new(new PaintCredit(owner, 1, 0, 1, PaintAttackKind.Main, true), 0, 32, 8, 2);
        void RememberLastCommand()
            => _commands.Add(((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Last());
        void ObserveLargeColumnProof()
        {
            var proof = Read<object>(_terrain, "_lastBuildOccupancyProof");
            if (proof == null) return;
            var columns = Read<IDictionary>(proof, "Columns");
            foreach (DictionaryEntry entry in columns)
                if ((Vector2Int)entry.Key == new Vector2Int(2, 2))
                    _largestObservedColumnQuery = Math.Max(_largestObservedColumnQuery, Read<int>(entry.Value, "QueryWork"));
        }
        static T Read<T>(object target, string field)
            => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
    }
}
#endif
