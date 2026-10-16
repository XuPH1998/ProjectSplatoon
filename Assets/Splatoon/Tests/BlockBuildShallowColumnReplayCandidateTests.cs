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
    public sealed class BlockBuildShallowColumnReplayCandidateTests
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
            yield return SetupAfterEnteringPlayMode();
        }

        // No mode instruction: callers enter at their own root iterator first.
        internal IEnumerator SetupAfterEnteringPlayMode()
        {
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
            yield return TeardownBeforeExitingPlayMode();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        // Join the same workers and destroy the same native resources before exit.
        internal IEnumerator TeardownBeforeExitingPlayMode()
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
            => RunOccupiedCandidateReplay(false);

        [UnityTest, Timeout(600000)]
        public IEnumerator AdvancingTicksAndOversizedColumnLearnPositiveWitnessesWithoutStarvingFollowingPaidBuild()
            => RunOccupiedCandidateReplay(true);

        IEnumerator RunOccupiedCandidateReplay(bool advancingTime)
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
            int frames = 0; bool observedPositiveWitness = false;
            for (; frames < PumpLimit && !Terminal(callbacks)
                && _terrain.BuildPublicationRetries - retryBaseline < RejectionFailureLimit; frames++)
            {
                RememberActualPending(_terrain);
                // Real Tick time advances on EVERY replay while both addresses
                // remain physically occupied. A same-time timestamp cannot pass.
                double now = advancingTime ? Epoch + (frames + 1) * .00001d : Epoch;
                if (advancingTime)
                {
                    Assert.That(PhysicalOwnersAt(publishedBefore, FirstOverlap, now), Is.GreaterThanOrEqualTo(1));
                    Assert.That(PhysicalOwnersAt(publishedBefore, SecondOverlap, now), Is.GreaterThanOrEqualTo(1));
                }
                _terrain.Tick(true, now, .016f);
                ObserveLargeColumnProof();
                RememberActualPending(_terrain);
                Assert.That(_terrain.SimulationTime, Is.EqualTo(now));
                if (advancingTime) observedPositiveWitness |= ObservePositiveWitnesses();
                AssertPublishedConservation(tokens);
                CollectionAssert.AreEqual(frozenBefore, publishedBefore.EncodeSnapshot(),
                    "All rejected drafts and eventual commits retain the old published source byte-for-byte.");
                yield return null;
            }
            string diagnostic = $"frames={frames} callbacks={callbacks[0]}/{callbacks[1]} "
                + $"retries={_terrain.BuildPublicationRetries - retryBaseline} "
                + $"movingOccupied={_terrain.BuildMovingOccupied} largestColumnQuery={_largestObservedColumnQuery} "
                + $"pending={_terrain.PendingTransactions} activeBuild={Read<object>(_terrain, "_buildOccupancyPreparation") != null}";
            if (advancingTime) Assert.That(observedPositiveWitness, Is.True, "A real final-publication rejection must install a bounded positive motion witness.");
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
            => RunRejectedPaymentAfterTimeAdvances(false);

        [UnityTest, Timeout(600000)]
        public IEnumerator LearnedPositiveWitnessRechecksCurrentMotionBeforeBuildingVacatedCells()
            => RunRejectedPaymentAfterTimeAdvances(true);

        IEnumerator RunRejectedPaymentAfterTimeAdvances(bool requirePositiveWitness)
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
                if (!advanced && _terrain.BuildPublicationRetries > retryBaseline)
                {
                    if (requirePositiveWitness) Assert.That(ObservePositiveWitnesses(), Is.True, "The clock advances only after a real positive witness has been learned.");
                    advanced = true;
                }
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

        [UnityTest, Timeout(600000)]
        public IEnumerator LearnedPositiveWitnessCannotSurviveSameSourceMutationBeforePublication()
        {
            var seed = MakePublishedChain();
            uint revision = _terrain.Revision + 1;
            seed.Seal(revision, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return PumpRemoteInstall(revision);
            var source = _terrain.World;
            var token = MakeToken(4); int reserved = token.ReserveBuild(2, false);
            int callbacks = 0; bool mutated = false, invalidatedBeforePublication = false;
            long retryBaseline = _terrain.BuildPublicationRetries;
            _terrain.QueueBuild(BlockWorld.Center(FirstOverlap) - Vector3.up * .051f,
                Vector3.up, 1, reserved, 0, false, result =>
                {
                    if (!_arm) return;
                    callbacks++;
                    Assert.That(callbacks, Is.EqualTo(1));
                    Assert.That(mutated, Is.True);
                    Assert.That(result.Added, Is.EqualTo(2), "Removed source blockers cannot leave a stale occupied-address fact.");
                    token.CompleteBuild(reserved, false, result.Added);
                    Physics.SyncTransforms();
                    AssertPublishedPaidCell(FirstOverlap); AssertPublishedPaidCell(SecondOverlap);
                }, true);
            RememberLastCommand();
            int frames = 0;
            for (; frames < PumpLimit && callbacks == 0; frames++)
            {
                RememberActualPending(_terrain);
                _terrain.Tick(true, Epoch, .016f);
                RememberActualPending(_terrain);
                if (!mutated && _terrain.BuildPublicationRetries > retryBaseline
                    && Read<object>(_terrain, "_pending") == null && ObservePositiveWitnesses())
                {
                    ObserveLargeColumnProof();
                    ulong beforeMutation = source.MutationGeneration;
                    // Public cell removal on the exact same source, not a direct
                    // write to cache, command iterator, scheduler or query graph.
                    foreach (var run in source.Falling.Values.ToArray())
                        for (int index = 0; index < run.Cells.Length; index++)
                            if (run.Cells[index].Occupied)
                                Assert.That(source.RemoveFallingCell(run.Id, index, out _, out _), Is.True);
                    Assert.That(ReferenceEquals(_terrain.World, source), Is.True);
                    Assert.That(_terrain.Revision, Is.EqualTo(revision));
                    Assert.That(source.MutationGeneration, Is.GreaterThan(beforeMutation));
                    Assert.That(PhysicalOwnersAt(source, FirstOverlap, Epoch), Is.Zero);
                    Assert.That(PhysicalOwnersAt(source, SecondOverlap, Epoch), Is.Zero);
                    mutated = true;
                }
                else if (mutated && ReferenceEquals(_terrain.World, source)
                    && Read<IDictionary>(_terrain, "_buildOverlapWitnesses").Count == 0)
                    invalidatedBeforePublication = true;
                if (callbacks == 0) yield return null;
            }
            Assert.That(mutated, Is.True, "Mutation must follow a real learned positive witness.");
            Assert.That(invalidatedBeforePublication, Is.True, "Source mutation must invalidate before a publication swap can hide the defect.");
            Assert.That(_largestObservedColumnQuery, Is.GreaterThan(4096));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(token.Added, Is.EqualTo(2));
            Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(token.BuildLimit));
        }

        bool ObservePositiveWitnesses()
        {
            var witnesses = Read<IDictionary>(_terrain, "_buildOverlapWitnesses");
            Assert.That(witnesses.Count, Is.LessThanOrEqualTo(4096));
            foreach (DictionaryEntry entry in witnesses)
            {
                var sample = Read<object>(entry.Value, "Sample");
                var nodes = Read<object>(sample, "Nodes");
                int count = (int)nodes.GetType().GetProperty("Count").GetValue(nodes);
                Assert.That(count, Is.InRange(1, BlockWorld.MaxCohortCells));
                var at = (Vector3Int)entry.Key;
                var from = Read<Vector3Int>(entry.Value, "SourceCell");
                Assert.That(from.x, Is.EqualTo(at.x)); Assert.That(from.z, Is.EqualTo(at.z));
            }
            return witnesses.Count != 0;
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
        // Candidate only: new controlled inputs over a real published snapshot.
        // This does not restore the original private queue, proof, map or checkpoint.
        [UnityTest, Timeout(600000)]
        public IEnumerator RecordedP2SnapshotBuildThenDestroyMustFinishAtAnAdvancingClock()
            => RunRecordedP2ControlledReplay(false);

        [UnityTest, Timeout(600000)]
        public IEnumerator RecordedP2SnapshotDestroyThenBuildMustFinishAtAnAdvancingClock()
            => RunRecordedP2ControlledReplay(true);

        [UnityTest, Timeout(600000)]
        public IEnumerator FiveShallowColumnsCannotAlternateBuildRejectionsAtAnAdvancingClock()
        {
            var seed = MakeShallowCappedColumns();
            yield return RunControlledReplay(seed.EncodeSnapshot(), Epoch, false, false);
        }

        IEnumerator RunRecordedP2ControlledReplay(bool destroyFirst)
        {
            string snapshot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..",
                "Reports/BlockTerrain/Windows24Preparation01/NetworkExactCpuPlayerPreparation01/NetworkP2RuntimeCpuRun01/p2-rtt0-loss0/4aa6502e/host.blocks.json.cpu-final.snapshot.bin"));
            byte[] wire = System.IO.File.ReadAllBytes(snapshot);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                Assert.That(BitConverter.ToString(sha.ComputeHash(wire)).Replace("-", "").ToLowerInvariant(),
                    Is.EqualTo("23b2624151a394fc33e0a7f13eebf3b7c7da78e939bb08882029287997eee756"));
            Assert.That(wire.Length, Is.EqualTo(26189));
            yield return RunControlledReplay(wire, 94.3034790405553, true, destroyFirst);
        }

        IEnumerator RunControlledReplay(byte[] wire, double firstNow, bool recorded, bool destroyFirst)
        {
            var decoded = BlockWorld.DecodeSnapshot(wire);
            uint installedRevision = decoded.Revision;
            int initialPink = decoded.PinkCount, initialBlue = decoded.BlueCount;
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return PumpRemoteInstall(installedRevision);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            var originalSource = _terrain.World;
            byte[] frozenSource = originalSource.EncodeSnapshot();
            _terrain.Tick(false, firstNow, 1f / 60f);

            if (recorded)
            {
                Assert.That(initialPink, Is.EqualTo(1471));
                Assert.That(initialBlue, Is.EqualTo(1489));
                Assert.That(_terrain.World.Falling.Count, Is.EqualTo(20));
                Assert.That(_terrain.World.Cohorts.Count, Is.EqualTo(61));
            }
            else
            {
                Assert.That(initialPink, Is.Zero);
                Assert.That(initialBlue, Is.EqualTo(20));
                Assert.That(_terrain.World.Falling.Count, Is.EqualTo(5));
            }
            foreach (var group in originalSource.Cohorts.Values)
                if (group.MemberIds.Count != 0)
                {
                    Assert.That(group.LowerSupport, Is.Null);
                    Assert.That(Read<Array>(group, "RetainedSupportMotion"), Is.Empty);
                    Assert.That(group.RebasePending, Is.False);
                    Assert.That(group.LandingKnown, Is.True);
                }
            foreach (var run in originalSource.Falling.Values)
            {
                Assert.That(run.Offset(firstNow), Is.EqualTo(run.LandingOffset).Within(1e-6f));
                Assert.That(run.Cohort.Speed(firstNow), Is.Zero);
            }

            int buildCount = recorded ? 8 : 1;
            byte buildTeam = recorded ? (byte)2 : (byte)1;
            var firstToken = new BlockAttackToken(new PaintCredit(101, buildTeam, 0, 1, PaintAttackKind.Main, true), 0, 32, 8, 2);
            var safeToken = MakeToken(102);
            int firstReserved = firstToken.ReserveBuild(buildCount, false);
            int safeReserved = safeToken.ReserveBuild(1, false);
            Assert.That(firstReserved, Is.EqualTo(buildCount));
            Assert.That(safeReserved, Is.EqualTo(1));
            int commandCount = recorded ? 3 : 2;
            var callbacks = new int[commandCount];
            var outcomes = new BlockMutationResult[commandCount];
            var callbackOrder = new List<int>();
            int buildIndex = recorded && destroyFirst ? 1 : 0;
            int destroyIndex = destroyFirst ? 0 : 1;
            int safeIndex = commandCount - 1;
            Action<int, BlockMutationResult> finish = (index, result) =>
            {
                if (!_arm) return;
                callbacks[index]++;
                Assert.That(callbacks[index], Is.EqualTo(1));
                outcomes[index] = result; callbackOrder.Add(index);
                if (index == buildIndex) firstToken.CompleteBuild(firstReserved, false, result.Added);
                if (index == safeIndex) safeToken.CompleteBuild(safeReserved, false, result.Added);
            };
            Action queueRecordedDestroy = () =>
            {
                // Exact public head input from row 100, not the original cursor/source.
                _terrain.QueueDestroy(new Vector3(-2.200000047683716f, .10000000149011612f, 22f),
                    .14000000059604645f, .14000000059604645f, 2, false,
                    result => finish(destroyIndex, result), 8, 8, false);
                RememberLastCommand();
            };
            if (recorded && destroyFirst) queueRecordedDestroy();
            // Recorded build is row 99, whose Total.Added was zero. No private
            // Cells cursor, RetryWindow, source or guard is transplanted.
            Vector3 buildPoint = recorded
                ? new Vector3(-3.2484922409057617f, .10000000149011612f, 22f)
                : BlockWorld.Center(new Vector3Int(2, 1, 2)) - Vector3.up * .051f;
            _terrain.QueueBuild(buildPoint, Vector3.up, buildTeam, firstReserved,
                recorded ? .20000000298023224f : .101f, false,
                result => finish(buildIndex, result), !recorded);
            RememberLastCommand();
            if (recorded && !destroyFirst) queueRecordedDestroy();
            _terrain.QueueBuild(BlockWorld.Center(SafeCell) - Vector3.up * .051f,
                Vector3.up, 1, safeReserved, 0, true, result => finish(safeIndex, result), true);
            RememberLastCommand();

            int overlapAborts = 0, unchangedRevisionAborts = 0, maximumUnchangedRevisionAborts = 0;
            uint abortRevision = _terrain.Revision;
            long failures = _terrain.FailedTransactions, gravityChecks = _terrain.GravityWorkChecks;
            int largestShallowQuery = 0;
            var guardColumnSets = new List<string>();
            Action<BlockTerrain.PendingWallDiagnostic> observer = sample =>
            {
                if (sample.Outcome == BlockTerrain.PendingWallOutcome.Published)
                    unchangedRevisionAborts = 0;
                if (sample.Outcome != BlockTerrain.PendingWallOutcome.Aborted
                    || sample.AbortReason != BlockTerrain.PendingAbortReason.BuildMovingOverlap) return;
                overlapAborts++;
                unchangedRevisionAborts = abortRevision == _terrain.Revision ? unchangedRevisionAborts + 1 : 1;
                abortRevision = _terrain.Revision;
                maximumUnchangedRevisionAborts = Math.Max(maximumUnchangedRevisionAborts, unchangedRevisionAborts);
            };
            _terrain.PendingTransactionFinished += observer;
            int frames = 0;
            try
            {
                for (; frames < PumpLimit && maximumUnchangedRevisionAborts < RejectionFailureLimit; frames++)
                {
                    if (callbacks.All(value => value == 1) && _terrain.PendingTransactions == 0
                        && _terrain.FallingCount == 0 && _terrain.UnstableColumns == 0) break;
                    RememberActualPending(_terrain);
                    double now = firstNow + (frames + 1) / 60d;
                    _terrain.Tick(true, now, 1f / 60f);
                    RememberActualPending(_terrain);
                    Assert.That(_terrain.SimulationTime, Is.EqualTo(now));
                    AssertControlledReplayConservation(initialPink, initialBlue, firstToken, safeToken);
                    CollectionAssert.AreEqual(frozenSource, originalSource.EncodeSnapshot());
                    var proof = Read<object>(_terrain, "_lastBuildOccupancyProof");
                    if (proof != null)
                    {
                        var columns = Read<IDictionary>(proof, "Columns");
                        foreach (DictionaryEntry entry in columns)
                            largestShallowQuery = Math.Max(largestShallowQuery, Read<int>(entry.Value, "QueryWork"));
                        string columnSet = string.Join(";", columns.Keys.Cast<Vector2Int>().OrderBy(at => at.x)
                            .ThenBy(at => at.y).Select(at => at.x + "," + at.y));
                        if (guardColumnSets.Count < 64 && (guardColumnSets.Count == 0 || guardColumnSets.Last() != columnSet))
                            guardColumnSets.Add(columnSet);
                    }
                    yield return null;
                }
            }
            finally { _terrain.PendingTransactionFinished -= observer; }
            string diagnostic = $"recorded={recorded} destroyFirst={destroyFirst} frames={frames} "
                + $"overlapAborts={overlapAborts} unchangedRevisionAborts={maximumUnchangedRevisionAborts} "
                + $"callbacks={string.Join("/", callbacks)} revision={_terrain.Revision} "
                + $"pending={_terrain.PendingTransactions} unstable={_terrain.UnstableColumns} falling={_terrain.FallingCount} "
                + $"largestQuery={largestShallowQuery} gravityChecks={_terrain.GravityWorkChecks - gravityChecks} "
                + $"columnSets=[{string.Join(" | ", guardColumnSets)}] retry={JsonUtility.ToJson(_terrain.InspectPublicationRetry())}";
            Debug.Log("[BLOCK_CONTROLLED_REPLAY_CANDIDATE] " + diagnostic);
            Assert.That(overlapAborts, Is.GreaterThanOrEqualTo(1),
                "A candidate without a real publication overlap rejection is inconclusive, not evidence of repair. " + diagnostic);
            Assert.That(largestShallowQuery, Is.LessThanOrEqualTo(3072),
                "This candidate must remain below the ordinary inline allowance; it is not the 84-dependency fixture. " + diagnostic);
            Assert.That(callbacks.All(value => value == 1), Is.True,
                "Original finite commands must terminate once; eight same-revision overlap aborts are a failing witness. " + diagnostic);
            CollectionAssert.AreEqual(Enumerable.Range(0, commandCount), callbackOrder, diagnostic);
            Assert.That(_terrain.PendingTransactions, Is.Zero, diagnostic);
            Assert.That(_terrain.FallingCount, Is.Zero, diagnostic);
            Assert.That(_terrain.UnstableColumns, Is.Zero, diagnostic);
            Assert.That(_terrain.GravityWorkChecks, Is.GreaterThan(gravityChecks), diagnostic);
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failures), diagnostic);
            Assert.That(outcomes[buildIndex].Added, Is.LessThanOrEqualTo(firstReserved));
            Assert.That(outcomes[safeIndex].Added, Is.EqualTo(1));
            Assert.That(firstToken.Added + firstToken.BuildRemaining, Is.EqualTo(firstToken.BuildLimit));
            Assert.That(safeToken.Added + safeToken.BuildRemaining, Is.EqualTo(safeToken.BuildLimit));
            if (!recorded)
            {
                Assert.That(outcomes[buildIndex].Added, Is.Zero);
                Assert.That(firstToken.TakePoints(), Is.Zero);
                foreach (var at in ShallowColumns)
                    Assert.That(_terrain.World.TryGet(new Vector3Int(at.x, 1, at.y), out var owner)
                        && owner.Owner == 2, Is.True);
            }
            else
            {
                Assert.That(outcomes[destroyIndex].RemovedEnemy, Is.LessThanOrEqualTo(8));
                Assert.That(outcomes[destroyIndex].RemovedFriendly, Is.LessThanOrEqualTo(8));
            }
            Physics.SyncTransforms();
            Assert.That(Physics.Raycast(BlockWorld.Center(SafeCell) + Vector3.up * .2f,
                Vector3.down, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
            byte[] finalWire = _terrain.ExportSnapshot();
            CollectionAssert.AreEqual(finalWire, BlockWorld.DecodeSnapshot(finalWire).EncodeSnapshot());
            CollectionAssert.AreEqual(frozenSource, originalSource.EncodeSnapshot());
            AssertControlledReplayConservation(initialPink, initialBlue, firstToken, safeToken);
        }

        static readonly Vector2Int[] ShallowColumns =
        {
            new(2, 2), new(2, 1), new(2, 3), new(1, 2), new(3, 2)
        };
        static BlockWorld MakeShallowCappedColumns()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            foreach (var at in ShallowColumns)
            {
                world.Set(new Vector3Int(at.x, 0, at.y), BlockCell.Create(2));
                for (int y = 4; y < 7; y++) world.Set(new Vector3Int(at.x, y, at.y), BlockCell.Create(2));
                var group = world.BeginCohort(new Vector3Int(at.x, 4, at.y), 3);
                world.DetachCohortPiece(group.Id, 3, Epoch, out _);
                world.SetCohortSupport(group.Id, 0, 0, out _);
                group = world.Cohorts[group.Id];
                group.LandingKnown = true; group.LandingOffset = .3f;
                group.InitialOffset = .3f; group.InitialSpeed = 0; group.NeedsLandingProof = false;
            }
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray());
            return world;
        }
        void AssertControlledReplayConservation(int initialPink, int initialBlue, params BlockAttackToken[] tokens)
        {
            int pink = 0, blue = 0;
            foreach (var chunk in _terrain.World.Chunks.Values)
                foreach (var value in chunk.Cells)
                    if (value.Occupied) { if (value.Owner == 1) pink++; else if (value.Owner == 2) blue++; }
            foreach (var run in _terrain.World.Falling.Values)
                foreach (var value in run.Cells)
                    if (value.Occupied) { if (value.Owner == 1) pink++; else if (value.Owner == 2) blue++; }
            int expectedPink = initialPink, expectedBlue = initialBlue, committedAdded = 0;
            foreach (var command in _commands)
            {
                var total = Read<BlockMutationResult>(command, "Total");
                byte team = Read<byte>(command, "Team"); committedAdded += total.Added;
                if (team == 1) { expectedPink += total.Added - total.RemovedFriendly; expectedBlue -= total.RemovedEnemy; }
                else { expectedBlue += total.Added - total.RemovedFriendly; expectedPink -= total.RemovedEnemy; }
            }
            Assert.That(pink, Is.EqualTo(expectedPink)); Assert.That(blue, Is.EqualTo(expectedBlue));
            Assert.That(_terrain.PinkCount, Is.EqualTo(pink)); Assert.That(_terrain.BlueCount, Is.EqualTo(blue));
            Assert.That(committedAdded, Is.LessThanOrEqualTo(tokens.Sum(token => token.BuildLimit - token.BuildRemaining)));
            Assert.That(tokens.Sum(token => token.Added), Is.LessThanOrEqualTo(committedAdded));
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
        // Reports-only candidate extension. The five existing test bodies are unchanged.
        enum LearnedCacheBoundary { EqualRevisionFullRepair, SameWorldMutation, QueuedRemoteInstall }
        static readonly Vector3Int LearnedLowerCell = new(2, 1, 2);
        static readonly Vector3Int LearnedUpperCell = new(2, 2, 2);
        static readonly Vector2Int LearnedColumn = new(2, 2);

        [UnityTest, Timeout(600000)]
        public IEnumerator APopulatedBuildColumnSurvivesEqualRevisionFullRepairWithoutBlockingPaidConstruction()
            => RunPopulatedSourceCacheBoundary(LearnedCacheBoundary.EqualRevisionFullRepair);

        [UnityTest, Timeout(600000)]
        public IEnumerator APopulatedBuildColumnIsInvalidatedBySameWorldMutationBeforeAnyPublication()
            => RunPopulatedSourceCacheBoundary(LearnedCacheBoundary.SameWorldMutation);

        [UnityTest, Timeout(600000)]
        public IEnumerator APaidBuildQueuedBehindRemoteRepairCannotReuseItsPopulatedOldColumn()
            => RunPopulatedSourceCacheBoundary(LearnedCacheBoundary.QueuedRemoteInstall);

        IEnumerator RunPopulatedSourceCacheBoundary(LearnedCacheBoundary boundary)
        {
            var seed = MakeSourceBoundarySeed();
            byte[] seedWire = seed.EncodeSnapshot();
            Assert.That(_terrain.ApplyRemoteSnapshot(seedWire), Is.True);
            yield return PumpRemoteInstall(seed.Revision);
            CollectionAssert.AreEqual(seedWire, _terrain.ExportSnapshot());
            Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.BlueCount, Is.EqualTo(4));

            var tokens = new[] { MakeToken(201), MakeToken(202), MakeToken(203) };
            var reserved = tokens.Select(token => token.ReserveBuild(1, false)).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, reserved);
            var callbacks = new int[3];
            var outcomes = new BlockMutationResult[3];
            var order = new List<int>();
            var cells = new[] { LearnedLowerCell, LearnedUpperCell, SafeCell };
            Action<int, BlockMutationResult> finish = (index, result) =>
            {
                if (!_arm) return;
                callbacks[index]++;
                Assert.That(callbacks[index], Is.EqualTo(1), "Each original finite payment completes exactly once.");
                Assert.That(result.OrdinaryMainAttack, Is.True);
                Assert.That(result.Added, Is.EqualTo(1), "An old occupied-mask entry must not reject a now-free paid cell.");
                Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                outcomes[index] = result;
                tokens[index].CompleteBuild(reserved[index], false, result.Added);
                order.Add(index);
                Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
                AssertPublishedPaidCell(cells[index]);
            };
            Action<int> queue = index =>
            {
                _terrain.QueueBuild(BlockWorld.Center(cells[index]) - Vector3.up * .051f,
                    Vector3.up, 1, reserved[index], 0, index == 2,
                    result => finish(index, result), true);
                RememberLastCommand();
            };
            long failedBefore = _terrain.FailedTransactions;
            long queuedBefore = _terrain.CommandsQueued, completedBefore = _terrain.CommandsCompleted;
            long retriesBefore = _terrain.BuildPublicationRetries;
            int overlapAborts = 0, learningFrames = 0, completionFrames = 0;
            Action<BlockTerrain.PendingWallDiagnostic> observer = sample =>
            {
                if (sample.Outcome == BlockTerrain.PendingWallOutcome.Aborted
                    && sample.AbortReason == BlockTerrain.PendingAbortReason.BuildMovingOverlap) overlapAborts++;
            };
            _terrain.PendingTransactionFinished += observer;
            try
            {
                queue(0);
                object learnedCache = null;
                for (; learningFrames < PumpLimit; learningFrames++)
                {
                    RememberActualPending(_terrain);
                    _terrain.Tick(true, Epoch, .016f);
                    RememberActualPending(_terrain);
                    Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
                    AssertSourceBoundaryConservation(4, tokens);
                    var candidate = Read<object>(_terrain, "_buildSourceQueryCache");
                    if (overlapAborts > 0 && callbacks[0] == 0 && Read<object>(_terrain, "_pending") == null
                        && candidate != null && Read<IDictionary>(candidate, "Columns").Contains(LearnedColumn))
                    {
                        learnedCache = candidate;
                        break; // No yield/extra Tick may erase the learned source before the boundary.
                    }
                    Assert.That(callbacks[0], Is.Zero, "Learning must not finish before the controlled source boundary.");
                    yield return null;
                }
                Assert.That(learnedCache, Is.Not.Null, "A real overlap rejection must leave a populated current-source cache.");
                Assert.That(overlapAborts, Is.GreaterThanOrEqualTo(1));
                Assert.That(_terrain.BuildPublicationRetries, Is.GreaterThan(retriesBefore));
                AssertPopulatedCurrentSourceCache(learnedCache);
                CollectionAssert.AreEqual(new[] { 0, 0, 0 }, callbacks);
                Assert.That(Read<BlockMutationResult>(_commands[0], "Total").Added, Is.Zero);
                Assert.That(_terrain.PendingTransactions, Is.GreaterThan(0));
                var learnedSource = _terrain.World;
                uint learnedRevision = _terrain.Revision, learnedRound = _terrain.Round;
                uint learnedEpoch = Read<uint>(_terrain, "_epoch");
                ulong learnedMutation = learnedSource.MutationGeneration;
                byte[] frozenLearnedSource = learnedSource.EncodeSnapshot();
                Assert.That(PhysicalOwnersAt(learnedSource, LearnedLowerCell, Epoch), Is.EqualTo(1));
                Assert.That(PhysicalOwnersAt(learnedSource, LearnedUpperCell, Epoch), Is.EqualTo(1));
                Assert.That(learnedSource.TryGet(LearnedLowerCell, out _), Is.False);
                Assert.That(learnedSource.TryGet(LearnedUpperCell, out _), Is.False);
                var blocked = Read<IDictionary>(_terrain, "_buildOverlapWitnesses");
                Assert.That(blocked.Contains(LearnedLowerCell), Is.True,
                    "The source boundary crosses a real positive witness learned by final-publication rejection.");
                Debug.Log($"[BLOCK_SOURCE_CACHE_LEARNED] boundary={boundary} frames={learningFrames} "
                    + $"revision={learnedRevision} mutation={learnedMutation} overlapAborts={overlapAborts} "
                    + $"queryWork={Read<int>(Read<IDictionary>(learnedCache, "Columns")[LearnedColumn], "QueryWork")}");

                bool mutationInvalidatedBeforePublication = false;
                byte[] expectedRetainedSource;
                if (boundary == LearnedCacheBoundary.SameWorldMutation)
                {
                    // Public payload removal changes MutationGeneration on the exact same World.
                    Assert.That(RemoveLearnedColumnPayload(learnedSource), Is.EqualTo(3));
                    Assert.That(ReferenceEquals(_terrain.World, learnedSource), Is.True);
                    Assert.That(_terrain.Revision, Is.EqualTo(learnedRevision));
                    Assert.That(_terrain.Round, Is.EqualTo(learnedRound));
                    Assert.That(Read<uint>(_terrain, "_epoch"), Is.EqualTo(learnedEpoch));
                    Assert.That(learnedSource.MutationGeneration, Is.GreaterThan(learnedMutation));
                    Assert.That(ReferenceEquals(Read<object>(_terrain, "_buildSourceQueryCache"), learnedCache), Is.True);
                    Assert.That(Read<ulong>(learnedCache, "Mutation"), Is.EqualTo(learnedMutation));
                    Assert.That(PhysicalOwnersAt(learnedSource, LearnedLowerCell, Epoch), Is.Zero);
                    Assert.That(PhysicalOwnersAt(learnedSource, LearnedUpperCell, Epoch), Is.Zero);
                    expectedRetainedSource = learnedSource.EncodeSnapshot();
                    queue(1); queue(2);
                }
                else
                {
                    // Same revision and round: full repair is not a newer-version shortcut.
                    var repair = BlockWorld.DecodeSnapshot(frozenLearnedSource);
                    Assert.That(RemoveLearnedColumnPayload(repair), Is.EqualTo(3));
                    Assert.That(repair.Revision, Is.EqualTo(learnedRevision));
                    Assert.That(repair.Round, Is.EqualTo(learnedRound));
                    byte[] repairWire = repair.EncodeSnapshot();
                    AssertPopulatedCurrentSourceCache(learnedCache);
                    Assert.That(_terrain.ApplyRemoteSnapshot(repairWire), Is.True);
                    var queued = Read<object>(_terrain, "_remoteQueued");
                    Assert.That(queued, Is.Not.Null);
                    var repairSource = Read<BlockWorld>(queued, "Next");
                    Assert.That(ReferenceEquals(repairSource, learnedSource), Is.False);
                    Assert.That(ReferenceEquals(_terrain.World, learnedSource), Is.True);
                    Assert.That(ReferenceEquals(Read<object>(_terrain, "_buildSourceQueryCache"), learnedCache), Is.True);
                    Assert.That(Read<ulong>(learnedCache, "Mutation"), Is.EqualTo(learnedSource.MutationGeneration));
                    Assert.That(_terrain.Revision, Is.EqualTo(learnedRevision));
                    CollectionAssert.AreEqual(frozenLearnedSource, _terrain.ExportSnapshot());
                    if (boundary == LearnedCacheBoundary.QueuedRemoteInstall)
                    {
                        // These genuinely paid commands exist while old source + learned cache + queued install coexist.
                        queue(1); queue(2);
                        CollectionAssert.AreEqual(new[] { 0, 0, 0 }, callbacks);
                        Assert.That(_terrain.PinkCount, Is.Zero);
                        Assert.That(ReferenceEquals(_terrain.World, learnedSource), Is.True);
                        Assert.That(Read<object>(_terrain, "_remoteQueued"), Is.Not.Null);
                    }
                    int remoteFrames = 0;
                    for (; remoteFrames < PumpLimit && (ReferenceEquals(_terrain.World, learnedSource)
                        || Read<object>(_terrain, "_pending") != null || Read<object>(_terrain, "_remoteQueued") != null); remoteFrames++)
                    {
                        RememberActualPending(_terrain);
                        _terrain.Tick(false, Epoch, .016f);
                        RememberActualPending(_terrain);
                        CollectionAssert.AreEqual(new[] { 0, 0, 0 }, callbacks,
                            "Remote repair must publish before any of the original paid commands complete.");
                        Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
                        CollectionAssert.AreEqual(frozenLearnedSource, learnedSource.EncodeSnapshot());
                        AssertSourceBoundaryConservation(ReferenceEquals(_terrain.World, learnedSource) ? 4 : 1, tokens);
                        yield return null;
                    }
                    Assert.That(remoteFrames, Is.LessThan(PumpLimit));
                    Assert.That(ReferenceEquals(_terrain.World, repairSource), Is.True);
                    Assert.That(_terrain.Revision, Is.EqualTo(learnedRevision));
                    Assert.That(_terrain.Round, Is.EqualTo(learnedRound));
                    Assert.That(Read<uint>(_terrain, "_epoch"), Is.EqualTo(learnedEpoch));
                    Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
                    Assert.That(Read<object>(_terrain, "_remoteQueued"), Is.Null);
                    Assert.That(Read<object>(_terrain, "_buildSourceQueryCache"), Is.Null);
                    CollectionAssert.AreEqual(repairWire, _terrain.ExportSnapshot());
                    Assert.That(PhysicalOwnersAt(_terrain.World, LearnedLowerCell, Epoch), Is.Zero);
                    Assert.That(PhysicalOwnersAt(_terrain.World, LearnedUpperCell, Epoch), Is.Zero);
                    if (boundary == LearnedCacheBoundary.EqualRevisionFullRepair) { queue(1); queue(2); }
                    expectedRetainedSource = frozenLearnedSource;
                }

                for (; completionFrames < PumpLimit; completionFrames++)
                {
                    if (callbacks.All(value => value == 1) && _terrain.PendingTransactions == 0
                        && _terrain.FallingCount == 0 && _terrain.UnstableColumns == 0) break;
                    RememberActualPending(_terrain);
                    _terrain.Tick(true, Epoch, .016f);
                    RememberActualPending(_terrain);
                    if (boundary == LearnedCacheBoundary.SameWorldMutation
                        && ReferenceEquals(_terrain.World, learnedSource)
                        && !ReferenceEquals(Read<object>(_terrain, "_buildSourceQueryCache"), learnedCache))
                        mutationInvalidatedBeforePublication = true;
                    Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
                    AssertSourceBoundaryConservation(1, tokens);
                    CollectionAssert.AreEqual(expectedRetainedSource, learnedSource.EncodeSnapshot());
                    yield return null;
                }
                string diagnostic = $"boundary={boundary} learningFrames={learningFrames} completionFrames={completionFrames} "
                    + $"callbacks={string.Join("/", callbacks)} overlapAborts={overlapAborts} "
                    + $"mutationInvalidatedBeforePublication={mutationInvalidatedBeforePublication} "
                    + $"pending={_terrain.PendingTransactions} falling={_terrain.FallingCount} unstable={_terrain.UnstableColumns} "
                    + $"pink={_terrain.PinkCount} blue={_terrain.BlueCount}";
                Debug.Log("[BLOCK_SOURCE_CACHE_BOUNDARY_RESULT] " + diagnostic);
                Assert.That(completionFrames, Is.LessThan(PumpLimit), diagnostic);
                if (boundary == LearnedCacheBoundary.SameWorldMutation)
                    Assert.That(mutationInvalidatedBeforePublication, Is.True,
                        "An eventual World publication reset must not mask the real same-reference invalidation. " + diagnostic);
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, callbacks, diagnostic);
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, order, "The original paid FIFO survives the source boundary. " + diagnostic);
                Assert.That(_terrain.CommandsQueued - queuedBefore, Is.EqualTo(3));
                Assert.That(_terrain.CommandsCompleted - completedBefore, Is.EqualTo(3));
                Assert.That(_terrain.FailedTransactions, Is.EqualTo(failedBefore));
                Assert.That(_terrain.PendingTransactions, Is.Zero);
                Assert.That(Read<object>(_terrain, "_dependentEditPreparation"), Is.Null);
                Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
                Assert.That(_terrain.FallingCount, Is.Zero);
                Assert.That(_terrain.UnstableColumns, Is.Zero);
                Assert.That(_terrain.PinkCount, Is.EqualTo(3));
                Assert.That(_terrain.BlueCount, Is.EqualTo(1));
                for (int index = 0; index < tokens.Length; index++)
                {
                    Assert.That(outcomes[index].Added, Is.EqualTo(1));
                    Assert.That(tokens[index].Added, Is.EqualTo(1));
                    Assert.That(tokens[index].BuildRemaining, Is.EqualTo(tokens[index].BuildLimit - 1));
                    Assert.That(tokens[index].Added + tokens[index].BuildRemaining, Is.EqualTo(tokens[index].BuildLimit));
                    Assert.That(tokens[index].TakePoints(), Is.EqualTo(1d / tokens[index].BlocksPerPoint).Within(1e-12));
                    Assert.That(tokens[index].TakePoints(), Is.Zero, "No retry or source change awards the payment twice.");
                    Assert.That(_terrain.World.TryGet(cells[index], out var value) && value.Owner == 1, Is.True);
                    Assert.That(PhysicalOwnersAt(_terrain.World, cells[index], Epoch), Is.Zero);
                    AssertPublishedPaidCell(cells[index]);
                }
                AssertSourceBoundaryConservation(1, tokens);
                byte[] finalWire = _terrain.ExportSnapshot();
                CollectionAssert.AreEqual(finalWire, BlockWorld.DecodeSnapshot(finalWire).EncodeSnapshot());
                CollectionAssert.AreEqual(expectedRetainedSource, learnedSource.EncodeSnapshot());
            }
            finally { _terrain.PendingTransactionFinished -= observer; }
        }

        void AssertPopulatedCurrentSourceCache(object cache)
        {
            Assert.That(cache, Is.Not.Null);
            Assert.That(ReferenceEquals(Read<object>(_terrain, "_buildSourceQueryCache"), cache), Is.True);
            Assert.That(ReferenceEquals(Read<BlockWorld>(cache, "Source"), _terrain.World), Is.True);
            Assert.That(Read<uint>(cache, "Epoch"), Is.EqualTo(Read<uint>(_terrain, "_epoch")));
            Assert.That(Read<uint>(cache, "Revision"), Is.EqualTo(_terrain.Revision));
            Assert.That(Read<uint>(cache, "Round"), Is.EqualTo(_terrain.Round));
            Assert.That(Read<ulong>(cache, "Mutation"), Is.EqualTo(_terrain.World.MutationGeneration));
            Assert.That(Read<object>(_terrain, "_remoteQueued"), Is.Null);
            var columns = Read<IDictionary>(cache, "Columns");
            Assert.That(columns.Contains(LearnedColumn), Is.True, "The exact column of the later paid build must really be learned.");
            var query = columns[LearnedColumn];
            Assert.That(Read<int>(query, "QueryWork"), Is.InRange(1, 3072));
            Assert.That(((IEnumerable)Read<object>(query, "Cells")).Cast<object>().Count(), Is.EqualTo(3));
            Assert.That(((IEnumerable)Read<object>(query, "Motions")).Cast<object>().Count(), Is.EqualTo(1));
            int minimum = Read<int>(query, "Minimum"), length = Read<int>(query, "Length");
            Assert.That(LearnedLowerCell.y, Is.InRange(minimum, minimum + length - 1));
            // The first lower payment can learn a narrower mask than the later upper payment needs.
            // Keep every frozen source cell so the full publication guard can validate either payment.
            CollectionAssert.AreEquivalent(new[] { new Vector3Int(2, 4, 2), new Vector3Int(2, 5, 2), new Vector3Int(2, 6, 2) },
                ((IEnumerable)Read<object>(query, "Cells")).Cast<object>().Select(cell => Read<Vector3Int>(cell, "SourceCell")).ToArray());
            Assert.That(length, Is.InRange(1, BlockWorld.MaxCohortCells));
            Assert.That(minimum, Is.GreaterThanOrEqualTo(Mathf.CeilToInt(_terrain.World.Bottom / BlockWorld.CellSize)));
            Assert.That(minimum + length, Is.LessThanOrEqualTo(Mathf.CeilToInt(BlockWorld.Top / BlockWorld.CellSize)));
            Assert.That(Read<bool[]>(query, "Moving").Length, Is.EqualTo(length));
            Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
        }

        static BlockWorld MakeSourceBoundarySeed()
        {
            // Keep the existing shallow-column motion verbatim, but expose the
            // target tower's side faces for independent native collision checks.
            var world = MakeShallowCappedColumns();
            var otherRuns = world.Falling.Values
                .Where(run => run.Bottom.x != LearnedColumn.x || run.Bottom.z != LearnedColumn.y).ToArray();
            foreach (var run in otherRuns)
                for (int index = 0; index < run.Cells.Length; index++)
                    if (run.Cells[index].Occupied)
                        Assert.That(world.RemoveFallingCell(run.Id, index, out _), Is.True);
            foreach (var column in ShallowColumns.Where(column => column != LearnedColumn))
                Assert.That(world.Set(new Vector3Int(column.x, 0, column.y), default), Is.True);
            world.Seal(world.Revision, world.RevisionKeys.ToArray());
            Assert.That(world.BlueCount, Is.EqualTo(4));
            Assert.That(world.Falling.Count, Is.EqualTo(1));
            Assert.That(world.Cohorts.Count, Is.EqualTo(1));
            return world;
        }

        static int RemoveLearnedColumnPayload(BlockWorld world)
        {
            // Resolve every live payload through public source addresses. No scheduler, cache or proof is written.
            var addresses = world.Falling.Values
                .Where(run => run.Bottom.x == LearnedColumn.x && run.Bottom.z == LearnedColumn.y)
                .SelectMany(run => Enumerable.Range(0, run.Cells.Length)
                    .Where(index => run.Cells[index].Occupied).Select(index => (Run: run.Id, Index: index))).ToArray();
            Assert.That(addresses.Length, Is.EqualTo(3));
            int before = world.BlueCount, removedCount = 0;
            foreach (var address in addresses)
            {
                Assert.That(world.RemoveFallingCell(address.Run, address.Index, out var removed), Is.True);
                Assert.That(removed.Owner, Is.EqualTo(2));
                removedCount++;
            }
            Assert.That(world.BlueCount, Is.EqualTo(before - removedCount));
            Assert.That(world.TryGet(new Vector3Int(LearnedColumn.x, 0, LearnedColumn.y), out var floor)
                && floor.Owner == 2, Is.True);
            return removedCount;
        }

        void AssertSourceBoundaryConservation(int expectedBlue, params BlockAttackToken[] tokens)
        {
            int pink = 0, blue = 0;
            foreach (var chunk in _terrain.World.Chunks.Values)
                foreach (var cell in chunk.Cells)
                    if (cell.Occupied) { if (cell.Owner == 1) pink++; else if (cell.Owner == 2) blue++; }
            foreach (var run in _terrain.World.Falling.Values)
                foreach (var cell in run.Cells)
                    if (cell.Occupied) { if (cell.Owner == 1) pink++; else if (cell.Owner == 2) blue++; }
            int paidAdded = _commands.Sum(command => Read<BlockMutationResult>(command, "Total").Added);
            Assert.That(pink, Is.EqualTo(paidAdded), "Independent actual payloads equal only committed finite paid results.");
            Assert.That(blue, Is.EqualTo(expectedBlue), "Exactly the three public boundary removals change blue payload count.");
            Assert.That(_terrain.PinkCount, Is.EqualTo(pink));
            Assert.That(_terrain.BlueCount, Is.EqualTo(blue));
            Assert.That(paidAdded, Is.LessThanOrEqualTo(tokens.Sum(token => token.BuildLimit - token.BuildRemaining)));
            Assert.That(tokens.Sum(token => token.Added), Is.LessThanOrEqualTo(paidAdded));
        }



        // Source intervals can overlap after a public removal leaves a sparse
        // parent and a new independent body uses the vacated source address.
        [UnityTest, Timeout(600000)]
        public IEnumerator AFracturedSourceSpanCannotBeBoundAsLowerSupport()
            => RunFracturedSourceSpanSupport(false);

        [UnityTest, Timeout(600000)]
        public IEnumerator AFracturedSourceSpanMustNotHideAnAdmissibleLowerSupport()
            => RunFracturedSourceSpanSupport(true);

        IEnumerator RunFracturedSourceSpanSupport(bool admissibleLower)
        {
            var seed = MakeFracturedSourceSpanSeed(admissibleLower, out ulong upperId,
                out ulong overlapId, out ulong expectedLowerId);
            int expectedBlue = admissibleLower ? 5 : 4;
            byte[] wire = seed.EncodeSnapshot();
            var decoded = BlockWorld.DecodeSnapshot(wire);
            CollectionAssert.AreEqual(wire, decoded.EncodeSnapshot());
            CollectionAssert.AreEqual(admissibleLower ? new ulong[] { 1, 2, 3 } : new ulong[] { 1, 2 },
                decoded.Cohorts.Values.Select(group => group.Id).ToArray(),
                "The real snapshot indexes the upper before the overlapping parent can regroup.");
            var overlap = decoded.Cohorts[overlapId];
            Assert.That(overlap.NeedsRegroup, Is.True);
            Assert.That(overlap.Bottom.y, Is.LessThan(decoded.Cohorts[upperId].Bottom.y));
            Assert.That(overlap.TopExclusive, Is.GreaterThan(decoded.Cohorts[upperId].Bottom.y));
            var overlapRun = decoded.Falling.Values.Single(run => run.Cohort.Id == overlapId);
            Assert.That(overlapRun.Cells[1].Occupied, Is.False);
            Assert.That(decoded.BlueCount, Is.EqualTo(expectedBlue));
            Assert.That(decoded.PinkCount, Is.Zero);
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return PumpRemoteInstall(decoded.Revision);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            var originalSource = _terrain.World;
            byte[] frozenSource = originalSource.EncodeSnapshot();
            long beforeChecks = _terrain.GravityWorkChecks, failedBefore = _terrain.FailedTransactions;
            bool bindingVisited = false;
            ulong observedLowerId = ulong.MaxValue;
            int bindingFrames = 0;
            // A cold bounded index may need several ordinary frames. The source
            // time stays fixed until the first real upper landing/binding visit.
            for (; bindingFrames < 240; bindingFrames++)
            {
                RememberActualPending(_terrain);
                _terrain.Tick(true, Epoch, 0);
                RememberActualPending(_terrain);
                var pending = Read<object>(_terrain, "_pending");
                var observed = pending == null ? _terrain.World : Read<BlockWorld>(pending, "Next");
                Assert.That(observed.Cohorts.ContainsKey(upperId), Is.True);
                Assert.That(observed.Cohorts.ContainsKey(overlapId), Is.True,
                    "A completed lower fracture cannot erase the baseline overlap witness.");
                Assert.That(observed.Cohorts[overlapId].NeedsRegroup, Is.True);
                observedLowerId = observed.Cohorts[upperId].LowerSupport?.Id ?? 0;
                object motion = null;
                if (pending != null)
                {
                    var journal = Read<object>(pending, "Gravity");
                    if (journal != null) motion = FindObservedCohortMotion(Read<IDictionary>(journal, "Motions"), upperId);
                }
                motion ??= FindObservedCohortMotion(Read<IDictionary>(_terrain, "_cohortMotionWork"), upperId);
                bindingVisited = motion != null && (Read<bool>(motion, "ProofKnown") || Read<int>(motion, "Phase") != 0);
                AssertControlledReplayConservation(0, expectedBlue);
                CollectionAssert.AreEqual(frozenSource, originalSource.EncodeSnapshot());
                if (bindingVisited) break;
                yield return null;
            }
            Assert.That(bindingFrames, Is.LessThan(240));
            Assert.That(bindingVisited, Is.True, "Indexing alone must not satisfy this regression.");
            Assert.That(observedLowerId, Is.EqualTo(expectedLowerId),
                "Skip the overlapping descriptor and continue to the next admissible lower, or no lower.");
            Assert.That(_terrain.GravityWorkChecks, Is.GreaterThan(beforeChecks));
            Debug.Log($"[BLOCK_SUPPORT_SOURCE_SPAN_RESULT] admissibleLower={admissibleLower} upper={upperId} overlap={overlapId} "
                + $"expectedLower={expectedLowerId} observedLower={observedLowerId} checks={_terrain.GravityWorkChecks - beforeChecks} "
                + $"frames={bindingFrames + 1} bindingVisited={bindingVisited} pink={_terrain.PinkCount} blue={_terrain.BlueCount} "
                + "sourceFrozen=True codecRoundTrip=True");

            int completionFrames = 0;
            for (; completionFrames < PumpLimit; completionFrames++)
            {
                if (_terrain.PendingTransactions == 0 && _terrain.FallingCount == 0 && _terrain.UnstableColumns == 0) break;
                double now = Epoch + (completionFrames + 1) / 60d;
                RememberActualPending(_terrain);
                _terrain.Tick(true, now, 1f / 60f);
                RememberActualPending(_terrain);
                Assert.That(_terrain.SimulationTime, Is.EqualTo(now));
                AssertControlledReplayConservation(0, expectedBlue);
                CollectionAssert.AreEqual(frozenSource, originalSource.EncodeSnapshot());
                foreach (var group in _terrain.World.Cohorts.Values)
                    if (group.LowerSupport != null)
                    {
                        Assert.That(group.LowerSupport.Id, Is.Not.EqualTo(group.Id));
                        Assert.That(group.LowerSupport.Bottom.x, Is.EqualTo(group.Bottom.x));
                        Assert.That(group.LowerSupport.Bottom.z, Is.EqualTo(group.Bottom.z));
                        Assert.That(group.LowerSupport.TopExclusive, Is.LessThanOrEqualTo(group.Bottom.y));
                        Assert.That(group.SupportClearance, Is.GreaterThanOrEqualTo(0));
                    }
                yield return null;
            }
            if (completionFrames == PumpLimit) LogSourceSpanCompletionDiagnostic(admissibleLower, completionFrames, upperId, overlapId, expectedLowerId);
            Assert.That(completionFrames, Is.LessThan(PumpLimit));
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.FallingCount, Is.Zero);
            Assert.That(_terrain.UnstableColumns, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failedBefore));
            AssertControlledReplayConservation(0, expectedBlue);
            Physics.SyncTransforms();
            for (int y = 0; y < expectedBlue; y++)
            {
                var cell = new Vector3Int(2, y, 2);
                Assert.That(_terrain.World.TryGet(cell, out var owner) && owner.Owner == 2, Is.True,
                    "All survivors settle into one contiguous physical column above the registered floor.");
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.right * .2f,
                    Vector3.left, out var native, .4f, 1 << BlockTerrain.TerrainLayer,
                    QueryTriggerInteraction.Ignore), Is.True, "Every settled survivor publishes real native collision.");
                Assert.That(_terrain.IsStableCollider(native.collider), Is.True);
                Assert.That(_terrain.TryContact(native, out var contact), Is.True);
                Assert.That(contact.Cell, Is.EqualTo(cell));
                Assert.That(contact.Owner, Is.EqualTo(2));
                Assert.That(contact.Stable, Is.True);
            }
            byte[] finalWire = _terrain.ExportSnapshot();
            var final = BlockWorld.DecodeSnapshot(finalWire);
            CollectionAssert.AreEqual(finalWire, final.EncodeSnapshot());
            Assert.That(final.BlueCount, Is.EqualTo(expectedBlue));
            Assert.That(final.PinkCount, Is.Zero);
            CollectionAssert.AreEqual(frozenSource, originalSource.EncodeSnapshot());
            Debug.Log($"[BLOCK_SUPPORT_SOURCE_SPAN_FINAL] admissibleLower={admissibleLower} completionFrames={completionFrames} "
                + $"pending={_terrain.PendingTransactions} falling={_terrain.FallingCount} unstable={_terrain.UnstableColumns} "
                + $"pink={_terrain.PinkCount} blue={_terrain.BlueCount} nativeCells={expectedBlue} codecRoundTrip=True sourceFrozen=True");
        }

        static object FindObservedCohortMotion(IDictionary motions, ulong cohortId)
        {
            foreach (DictionaryEntry pair in motions)
                if (Read<bool>(pair.Key, "Cohort") && Read<ulong>(pair.Key, "Id") == cohortId) return pair.Value;
            return null;
        }

        static BlockWorld MakeFracturedSourceSpanSeed(bool admissibleLower, out ulong upperId,
            out ulong overlapId, out ulong expectedLowerId)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            // Allocate this descriptor first through its public API; leave its
            // payload until the older moving parent vacates source address 17.
            var upper = world.BeginCohort(new Vector3Int(2, 17, 2), 1);
            upperId = upper.Id;
            for (int y = 16; y < 20; y++) world.Set(new Vector3Int(2, y, 2), BlockCell.Create(2));
            var lower = world.BeginCohort(new Vector3Int(2, 16, 2), 4);
            overlapId = lower.Id;
            var lowerRun = world.DetachCohortPiece(lower.Id, 4, Epoch, out _);
            world.SetCohortSupport(lower.Id, 0, 0, out _);
            lower = world.Cohorts[lower.Id];
            lower.LandingKnown = true; lower.LandingOffset = 1.6f;
            lower.InitialOffset = 1; lower.InitialSpeed = 0; lower.NeedsLandingProof = false;
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch);
            Assert.That(world.RemoveFallingCell(lowerRun.Id, 1, out var removed), Is.True);
            Assert.That(removed.Owner, Is.EqualTo(2));
            world.Set(new Vector3Int(2, 17, 2), BlockCell.Create(2));
            world.DetachCohortPiece(upperId, 1, Epoch, out _);
            world.SetCohortSupport(upperId, 0, 0, out _);
            upper = world.Cohorts[upperId];
            upper.LandingKnown = true; upper.LandingOffset = 1.7f;
            upper.InitialOffset = 0; upper.InitialSpeed = 0; upper.NeedsLandingProof = false;
            expectedLowerId = 0;
            if (admissibleLower)
            {
                world.Set(new Vector3Int(2, 3, 2), BlockCell.Create(2));
                var admissible = world.BeginCohort(new Vector3Int(2, 3, 2), 1);
                expectedLowerId = admissible.Id;
                world.DetachCohortPiece(admissible.Id, 1, Epoch, out _);
                world.SetCohortSupport(admissible.Id, 0, 0, out _);
                admissible = world.Cohorts[admissible.Id];
                admissible.LandingKnown = true; admissible.LandingOffset = .3f;
                admissible.InitialOffset = 0; admissible.InitialSpeed = 0; admissible.NeedsLandingProof = false;
            }
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray());
            return world;
        }



        void LogSourceSpanCompletionDiagnostic(bool admissibleLower, int completionFrames,
            ulong upperId, ulong overlapId, ulong expectedLowerId)
        {
            // Observation happens after the bounded pump, immediately before its
            // original assertion. It cannot admit, retry or extend any work.
            try
            {
                var text = new System.Text.StringBuilder();
                text.Append($"[BLOCK_SUPPORT_SOURCE_SPAN_COMPLETION_DIAGNOSTIC] admissibleLower={admissibleLower} "
                    + $"completionFrames={completionFrames} upper={upperId} overlap={overlapId} expectedLower={expectedLowerId} "
                    + $"time={_terrain.SimulationTime:R} revision={_terrain.Revision} pending={_terrain.PendingTransactions} "
                    + $"falling={_terrain.FallingCount} unstable={_terrain.UnstableColumns} pink={_terrain.PinkCount} blue={_terrain.BlueCount} ");
                text.Append("retry=").Append(JsonUtility.ToJson(_terrain.InspectPublicationRetry())).Append(' ');
                AppendSupportDiagnosticWorld(text, "published", _terrain.World, _terrain.SimulationTime, true);
                AppendSupportDiagnosticMap(text, "committedMotions", Read<IDictionary>(_terrain, "_cohortMotionWork"));
                AppendSupportDiagnosticMap(text, "committedColumns", Read<IDictionary>(_terrain, "_cohortColumnWork"));
                AppendSupportDiagnosticMap(text, "committedHeads", Read<IDictionary>(_terrain, "_cohortHeads"));
                AppendSupportDiagnosticMap(text, "rejectedBindings", Read<IDictionary>(_terrain, "_rejectedGravityBindings"));
                foreach (string name in new[] { "_pending", "_privateGravityPreparation", "_privateGravityNext",
                    "_privateGravityJournal", "_privateGravityProof", "_dependentEditPreparation", "_historicalRecoveryPreparation" })
                {
                    var field = _terrain.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object owner = field?.GetValue(_terrain);
                    text.Append(name).Append('=').Append(field == null ? "<absent>" : SupportDiagnosticFields(owner)).Append(' ');
                    if (owner == null) continue;
                    if (owner is BlockWorld ownerWorld) AppendSupportDiagnosticWorld(text, name + "World", ownerWorld, _terrain.SimulationTime, false);
                    foreach (var member in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .OrderBy(member => member.Name))
                    {
                        object value = member.GetValue(owner);
                        if (value is BlockWorld world) AppendSupportDiagnosticWorld(text, name + "." + member.Name, world, _terrain.SimulationTime, false);
                        if (value is IDictionary map) AppendSupportDiagnosticMap(text, name + "." + member.Name, map);
                        if (member.Name == "Gravity" && value != null)
                        {
                            text.Append(name).Append(".Gravity=").Append(SupportDiagnosticFields(value)).Append(' ');
                            foreach (var journalField in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                if (journalField.GetValue(value) is IDictionary journalMap)
                                    AppendSupportDiagnosticMap(text, name + ".Gravity." + journalField.Name, journalMap);
                        }
                    }
                }
                Debug.Log(text.ToString());
            }
            catch (Exception error)
            {
                // Diagnostic failure must never replace the existing assertion.
                Debug.Log($"[BLOCK_SUPPORT_SOURCE_SPAN_DIAGNOSTIC_ERROR] admissibleLower={admissibleLower} "
                    + $"completionFrames={completionFrames} type={error.GetType().FullName} message={error.Message}");
            }
        }

        static void AppendSupportDiagnosticWorld(System.Text.StringBuilder text, string name, BlockWorld world,
            double now, bool includeWire)
        {
            text.Append(name).Append("={").Append(SupportDiagnosticFields(world));
            foreach (var group in world.Cohorts.Values.OrderBy(group => group.Id))
            {
                text.Append(" cohort[").Append(group.Id).Append("]={").Append(SupportDiagnosticFields(group))
                    .Append(" currentOffset=").Append(group.Offset(now).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" currentSpeed=").Append(group.Speed(now).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                AppendSupportDiagnosticNestedFields(text, "cohort[" + group.Id + "]", group,
                    new[] { "RetainedSupportMotion", "RebaseMotion", "PreparedRebase" });
            }
            foreach (var run in world.Falling.Values.OrderBy(run => run.Id))
                text.Append(" run[").Append(run.Id).Append("]={").Append(SupportDiagnosticFields(run))
                    .Append(" occupiedIndexes=").Append(string.Join(",", Enumerable.Range(0, run.Cells.Length).Where(index => run.Cells[index].Occupied)))
                    .Append(" physicalBottom=").Append((run.Bottom.y * BlockWorld.CellSize - run.Offset(now)).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            if (includeWire)
            {
                byte[] wire = world.EncodeSnapshot();
                text.Append(" wireBytes=").Append(wire.Length).Append(" wireBase64=").Append(Convert.ToBase64String(wire));
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    text.Append(" wireSha256=").Append(BitConverter.ToString(sha.ComputeHash(wire)).Replace("-", "").ToLowerInvariant());
                try
                {
                    var decoded = BlockWorld.DecodeSnapshot(wire);
                    text.Append(" strictCodecRoundTrip=").Append(wire.SequenceEqual(decoded.EncodeSnapshot()));
                }
                catch (Exception error) { text.Append(" strictCodecError=").Append(error.GetType().FullName).Append(':').Append(error.Message); }
            }
            text.Append("} ");
        }

        static void AppendSupportDiagnosticMap(System.Text.StringBuilder text, string name, IDictionary map)
        {
            text.Append(name).Append("={count=").Append(map.Count);
            int count = 0;
            foreach (DictionaryEntry pair in map)
            {
                if (count++ == 64) { text.Append(" <truncated>"); break; }
                text.Append(" key[").Append(SupportDiagnosticFields(pair.Key)).Append("]=")
                    .Append(SupportDiagnosticFields(pair.Value));
                AppendSupportDiagnosticNestedFields(text, "mapValue", pair.Value, new[] { "NativeProof", "Replacement" });
                if (pair.Value is IEnumerable values && pair.Value is not string)
                {
                    int items = 0;
                    text.Append(" entries=[");
                    foreach (object value in values)
                    { if (items++ == 32) { text.Append("<truncated>"); break; } text.Append(SupportDiagnosticFields(value)).Append(';'); }
                    text.Append(']');
                }
            }
            text.Append("} ");
        }

        static void AppendSupportDiagnosticNestedFields(System.Text.StringBuilder text, string name,
            object owner, string[] fields)
        {
            if (owner == null) return;
            foreach (string fieldName in fields)
            {
                var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) continue;
                object value = field.GetValue(owner);
                text.Append(' ').Append(name).Append('.').Append(fieldName).Append('=').Append(SupportDiagnosticFields(value));
                if (value is IEnumerable values && value is not string)
                {
                    int count = 0;
                    text.Append('[');
                    foreach (object item in values)
                    { if (count++ == 32) { text.Append("<truncated>"); break; } text.Append(SupportDiagnosticFields(item)).Append(';'); }
                    text.Append(']');
                }
            }
        }

        static string SupportDiagnosticFields(object target)
        {
            if (target == null) return "null";
            var type = target.GetType();
            if (type.IsPrimitive || type.IsEnum || target is string || target is decimal)
                return Convert.ToString(target, System.Globalization.CultureInfo.InvariantCulture);
            var text = new System.Text.StringBuilder(type.Name).Append('{');
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(field => field.Name))
            {
                object value = field.GetValue(target);
                var valueType = value?.GetType();
                text.Append(field.Name).Append('=');
                if (value == null) text.Append("null");
                else if (valueType.IsPrimitive || valueType.IsEnum || value is string || value is decimal)
                    text.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                else if (value is Vector2Int || value is Vector3Int || value is Vector2 || value is Vector3)
                    text.Append(value);
                else if (value is FallingCohort group) text.Append("cohort:").Append(group.Id);
                else if (value is FallingRun run) text.Append("run:").Append(run.Id);
                else if (value is BlockWorld world) text.Append("world:rev").Append(world.Revision).Append("/mutation").Append(world.MutationGeneration);
                else if (value is ICollection collection) text.Append(valueType.Name).Append(":count").Append(collection.Count);
                else text.Append(valueType.Name).Append(":present");
                text.Append(';');
            }
            return text.Append('}').ToString();
        }

    }
}
#endif
