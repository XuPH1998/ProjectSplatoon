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
    // Real Terrain.Tick/atomic collision publication under continuously queued
    // ordinary attacks. Tokens reserve an existing prepaid budget once; weapon
    // material deduction itself is covered by the existing attack/weapon suite.
    public sealed class BlockDependentEditTerrainTests
    {
        HistoricalBlockWorldHeightScope _historicalHeight;
        const double Epoch = 10;
        const int PumpLimit = 4000, ChainLength = 124, BottomY = 16;
        const float GroundCap = BottomY * BlockWorld.CellSize;
        GameObject _root, _floor;
        BlockTerrain _terrain;
        ulong _rootCohort;
        readonly List<BlockAttackToken> _tokens = new();
        readonly List<object> _commands = new();
        readonly List<int> _callbacks = new();
        readonly HashSet<int> _completed = new();
        readonly List<BlockAttackToken> _directTokens = new();
        readonly List<string> _commandOrder = new(), _terminalOrder = new();
        readonly HashSet<int> _destroyCompleted = new();
        readonly HashSet<object> _observedPending = new();
        readonly HashSet<Task> _observedCommandTasks = new();
        readonly HashSet<Mesh> _observedMeshes = new();
        readonly HashSet<GameObject> _observedViews = new();
        readonly HashSet<Material> _observedMaterials = new();
        BlockWorkerQueue _admissions;
        SemaphoreSlim _workers;
        bool _arm;
        int _issued, _destroyIssued, _initialBlocks;
        static readonly Vector3Int Sentinel = new(20, 0, 20);

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return SetupScenario();
        }
        IEnumerator SetupScenario()
        {
            _arm = true;
            _observedPending.Clear(); _observedCommandTasks.Clear(); _observedMeshes.Clear();
            _observedViews.Clear(); _observedMaterials.Clear(); _admissions = null; _workers = null;
            yield return WaitForBootstrapInitialization();
            HeroMigrationTests.Load(); BlockAttackService.Clear();
            _historicalHeight = new HistoricalBlockWorldHeightScope();
            _tokens.Clear(); _commands.Clear(); _callbacks.Clear(); _completed.Clear(); _issued = 0;
            _directTokens.Clear(); _commandOrder.Clear(); _terminalOrder.Clear(); _destroyCompleted.Clear(); _destroyIssued = 0;
            _root = new GameObject("Targeted dependent metadata Terrain contracts");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(3701); _terrain.Initialize(null);
            _admissions = ReadStatic<BlockWorkerQueue>("WorkerAdmissions");
            _workers = ReadStatic<SemaphoreSlim>("Workers");
            _floor = new GameObject("Registered map floor for fresh own landing proof");
            _floor.transform.SetParent(_root.transform, false); _floor.transform.position = new Vector3(0, -.05f, 0);
            _floor.AddComponent<BoxCollider>().size = new Vector3(32, .1f, 64);
            Read<HashSet<Collider>>(_terrain, "_staticColliders").Add(_floor.GetComponent<Collider>());
            Physics.SyncTransforms(); _terrain.Tick(false, Epoch, .016f);
            yield return InstallChain(ChainLength);
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
            _historicalHeight?.Dispose(); _historicalHeight = null;
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
        public IEnumerator Fresh124ChainObtainsItsOwnFloorWithContinuousPrepaidOrdinaryLoadAndCapturedPolicy()
        {
            var published = _terrain.World; byte[] before = published.EncodeSnapshot();
            object firstOwner = null; BlockPublicationWindow captured = null; bool sawMatchingPending = false;
            for (int frame = 0; frame < PumpLimit && !RootCap(GroundCap); frame++)
            {
                QueuePaid(); QueuePaid();
                Assert.That(QueuedCount(), Is.GreaterThan(0));
                _terrain.Tick(true, Epoch, .016f);
                var owner = Read<object>(_terrain, "_dependentEditPreparation");
                if (owner != null && firstOwner == null)
                {
                    firstOwner = owner; captured = Read<BlockPublicationWindow>(Read<object>(owner, "Context"), "Window");
                    Assert.That(captured.Policy.Version, Is.EqualTo(3701));
                    _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(3702);
                }
                var pending = Read<object>(_terrain, "_pending");
                if (firstOwner != null && pending != null
                    && ReferenceEquals(Read<object>(pending, "Gravity"), Read<object>(Read<object>(firstOwner, "Context"), "Journal")))
                {
                    Assert.That(Read<BlockPublicationWindow>(pending, "Window"), Is.SameAs(captured)); sawMatchingPending = true;
                }
                CollectionAssert.AreEqual(before, published.EncodeSnapshot());
                AssertConserved(ChainLength);
                yield return null;
            }
            Assert.That(firstOwner, Is.Not.Null, "The fresh 124 first edit must use the real finite owner under load.");
            Assert.That(RootCap(GroundCap), Is.True, "Floor update must finish before the ordinary queue is allowed to drain.");
            Assert.That(sawMatchingPending, Is.True, "The admitted owner must reach normal atomic pending publication.");
            Assert.That(_terrain.DependentEditPreparationStages, Is.GreaterThan(0));
            Assert.That(_terrain.DependentEditPreparationWork, Is.GreaterThan(0));
            Assert.That(_terrain.MaximumDependentEditPreparationChecks, Is.InRange(1, 1024));
            yield return Drain(); AssertPaymentsAndFifo(); AssertConserved(ChainLength);
            AssertWire(published, _terrain.World); CollectionAssert.AreEqual(before, published.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator Fresh123IncludesItsMandatory34CheckPrefixAndObtainsItsOwnFloor()
            => FreshBoundary(123, 1054, true);

        [UnityTest, Timeout(600000)]
        public IEnumerator Fresh118IsTheFirstOneCellChainThatCannotFitTheUnchanged1024Stage()
            => FreshBoundary(118, 1029, true);

        [UnityTest, Timeout(600000)]
        public IEnumerator Fresh117ExactlyFits1024AndUsesTheOrdinaryMotionPath()
            => FreshBoundary(117, 1024, false);

        IEnumerator FreshBoundary(int length, int required, bool needsPreparation)
        {
            yield return InstallChain(length);
            var source = _terrain.World; byte[] before = source.EncodeSnapshot();
            int atomic = source.Clone().EstimateSetCohortSupportWork(_rootCohort) + BlockWorld.MaxCohortCells + 4;
            Assert.That(atomic + 34, Is.EqualTo(required));
            long starts = _terrain.DependentEditPreparationStarts; bool sawOwner = false;
            for (int frame = 0; frame < PumpLimit && !RootCap(GroundCap); frame++)
            {
                QueuePaid(); QueuePaid(); Assert.That(QueuedCount(), Is.GreaterThan(0));
                _terrain.Tick(true, Epoch, .016f);
                sawOwner |= Read<object>(_terrain, "_dependentEditPreparation") != null;
                CollectionAssert.AreEqual(before, source.EncodeSnapshot()); AssertConserved(length);
                yield return null;
            }
            Assert.That(RootCap(GroundCap), Is.True, "The final cap must publish before ordinary inputs stop.");
            Assert.That(sawOwner, Is.EqualTo(needsPreparation));
            Assert.That(_terrain.DependentEditPreparationStarts > starts, Is.EqualTo(needsPreparation),
                "Transient work from other motions must not opt the exact 117 boundary into preparation.");
            Assert.That(_terrain.MaximumDependentEditPreparationChecks, Is.LessThanOrEqualTo(1024));
            yield return Drain(); AssertPaymentsAndFifo(); AssertConserved(length); AssertWire(source, _terrain.World);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator CombinedPaidAndGravityDraftDoesNotRestartForContinuouslyQueuedFrozenDestruction()
        {
            yield return InstallChain(ChainLength, true); yield return WaitForOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation"); var context = Read<object>(owner, "Context");
            Assert.That(Read<bool>(context, "PureBackground"), Is.False);
            Assert.That(Read<object>(context, "FairSeed"), Is.Null);
            var source = Read<BlockWorld>(context, "Source"); byte[] before = source.EncodeSnapshot();
            long retries = _terrain.DependentEditPreparationRetries; bool sawPending = false;
            for (int frame = 0; frame < PumpLimit && !RootCap(GroundCap); frame++)
            {
                QueuePaid(); QueueFrozenDestroy();
                Assert.That(Read<ulong>(_terrain, "_fairReservationVersion"), Is.GreaterThan(Read<ulong>(context, "ReservationVersion")));
                _terrain.Tick(true, Epoch, .016f);
                var pending = Read<object>(_terrain, "_pending");
                if (pending != null && ReferenceEquals(Read<object>(pending, "Gravity"), Read<object>(context, "Journal")))
                { AssertCapturedContext(context, pending); sawPending = true; }
                Assert.That(_terrain.DependentEditPreparationRetries, Is.EqualTo(retries),
                    "An ordinary combined draft has no background reservation lease to invalidate.");
                CollectionAssert.AreEqual(before, source.EncodeSnapshot()); AssertConserved(ChainLength + 1);
                yield return null;
            }
            Assert.That(sawPending, Is.True); Assert.That(RootCap(GroundCap), Is.True);
            yield return Drain(); AssertPaymentsAndFifo(); AssertConserved(ChainLength + 1);
            Assert.That(_directTokens.Sum(token => token.RemovedEnemy), Is.EqualTo(1),
                "The mixed tail contains real frozen destruction of the initial stable sentinel.");
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator MovingPaidBuildRetainsOriginalContextAcrossItsRealCloneAndTwoReservationChanges()
        {
            yield return InstallChain(ChainLength, true); yield return WaitForMovingOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation"); var context = Read<object>(owner, "Context");
            var journal = Read<object>(context, "Journal"); var draft = Read<BlockWorld>(owner, "Draft");
            var source = Read<BlockWorld>(context, "Source"); byte[] before = source.EncodeSnapshot();
            Assert.That(Read<bool>(context, "PureBackground"), Is.False);
            Assert.That(Read<object>(context, "FairSeed"), Is.Null);
            Assert.That(((IEnumerable)Read<object>(owner, "Slots")).Cast<object>().Any(), Is.True);
            Assert.That(Read<bool>(owner, "MovingBuild"), Is.True);
            long retries = _terrain.DependentEditPreparationRetries;
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(3702);
            QueueFrozenDestroy(); // Actual reservation change during the metadata owner.
            Assert.That(Read<ulong>(_terrain, "_fairReservationVersion"), Is.GreaterThan(Read<ulong>(context, "ReservationVersion")));
            object wrapper = null; bool sawClonedDraft = false, sawPending = false;
            for (int frame = 0; frame < PumpLimit && !RootCap(GroundCap); frame++)
            {
                QueuePaid(); _terrain.Tick(true, Epoch, .016f);
                var currentWrapper = Read<object>(_terrain, "_buildOccupancyPreparation");
                if (currentWrapper != null && ReferenceEquals(Read<object>(currentWrapper, "Journal"), journal))
                {
                    if (wrapper == null)
                    {
                        wrapper = currentWrapper;
                        Assert.That(Read<object>(_terrain, "_dependentPublicationContext"), Is.SameAs(context));
                        ulong beforeReservation = Read<ulong>(_terrain, "_fairReservationVersion");
                        QueueFrozenDestroy(); // Another reservation change while BuildGuard owns the journal.
                        Assert.That(Read<ulong>(_terrain, "_fairReservationVersion"), Is.GreaterThan(beforeReservation));
                    }
                    Assert.That(Read<BlockPublicationWindow>(currentWrapper, "Window"), Is.SameAs(Read<BlockPublicationWindow>(context, "Window")));
                    sawClonedDraft |= !ReferenceEquals(Read<BlockWorld>(journal, "Next"), draft);
                }
                var pending = Read<object>(_terrain, "_pending");
                if (pending != null && ReferenceEquals(Read<object>(pending, "Gravity"), journal))
                {
                    Assert.That(wrapper, Is.Not.Null, "The same-column Add must reach the real delayed build guard.");
                    Assert.That(Read<object>(pending, "BuildGuard"), Is.SameAs(wrapper));
                    Assert.That(Read<BlockWorld>(journal, "Next"), Is.Not.SameAs(draft)); sawClonedDraft = true;
                    AssertCapturedContext(context, pending); sawPending = true;
                    Assert.That(Read<object>(_terrain, "_dependentPublicationContext"), Is.Null);
                }
                Assert.That(_terrain.DependentEditPreparationRetries, Is.EqualTo(retries));
                CollectionAssert.AreEqual(before, source.EncodeSnapshot()); AssertConserved(ChainLength + 1);
                yield return null;
            }
            Assert.That(wrapper, Is.Not.Null); Assert.That(sawClonedDraft, Is.True); Assert.That(sawPending, Is.True);
            Assert.That(RootCap(GroundCap), Is.True, RootCapFailureDiagnostic(GroundCap));
            Assert.That(_terrain.MaximumDependentEditPreparationChecks, Is.InRange(1, 1024));
            yield return Drain(); AssertPaymentsAndFifo(); AssertConserved(ChainLength + 1);
            Assert.That(_directTokens.Sum(token => token.RemovedEnemy), Is.EqualTo(1)); AssertWire(source, _terrain.World);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator AuthorityLossDuringMovingBuildWrapperCancelsItsOriginalStampAndReplaysPaymentsOnce()
        {
            yield return WaitForMovingOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation"); var context = Read<object>(owner, "Context");
            var journal = Read<object>(context, "Journal"); var source = Read<BlockWorld>(context, "Source");
            byte[] before = source.EncodeSnapshot();
            for (int frame = 0; frame < PumpLimit && Read<object>(_terrain, "_buildOccupancyPreparation") == null; frame++)
            { QueuePaid(); _terrain.Tick(true, Epoch, .016f); yield return null; }
            var wrapper = Read<object>(_terrain, "_buildOccupancyPreparation");
            Assert.That(wrapper, Is.Not.Null); Assert.That(Read<object>(wrapper, "Journal"), Is.SameAs(journal));
            Assert.That(Read<object>(_terrain, "_dependentPublicationContext"), Is.SameAs(context));
            int callbacks = _callbacks.Count; _terrain.Tick(false, Epoch, .016f);
            Assert.That(Read<object>(_terrain, "_dependentPublicationContext"), Is.Null);
            Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
            Assert.That(_terrain.World, Is.SameAs(source)); Assert.That(_callbacks.Count, Is.EqualTo(callbacks));
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            for (int frame = 0; frame < PumpLimit && !RootCap(GroundCap); frame++)
            { QueuePaid(); _terrain.Tick(true, Epoch, .016f); yield return null; }
            Assert.That(RootCap(GroundCap), Is.True); yield return Drain();
            AssertPaymentsAndFifo(); AssertConserved(ChainLength); AssertWire(source, _terrain.World);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator AuthorityLossCancelsOnlyItsDraftAndRestoresTheSamePaidCursors()
        {
            yield return WaitForOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation");
            var source = Read<BlockWorld>(Read<object>(owner, "Context"), "Source"); byte[] before = source.EncodeSnapshot();
            int callbacks = _callbacks.Count; QueuePaid();
            _terrain.Tick(false, Epoch, .016f);
            Assert.That(Read<object>(_terrain, "_dependentEditPreparation"), Is.Null);
            Assert.That(_terrain.World, Is.SameAs(source)); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            Assert.That(_callbacks.Count, Is.EqualTo(callbacks), "Authority-loss replay must not terminally complete an unpublished payment.");
            for (int frame = 0; frame < PumpLimit && !RootCap(GroundCap); frame++)
            { QueuePaid(); _terrain.Tick(true, Epoch, .016f); yield return null; }
            Assert.That(RootCap(GroundCap), Is.True); yield return Drain();
            AssertPaymentsAndFifo(); AssertConserved(ChainLength); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator RoundClearRestoresUnpublishedPaidOwnershipAndNeverPublishesTheOldDraft()
        {
            yield return WaitForOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation");
            var source = Read<BlockWorld>(Read<object>(owner, "Context"), "Source"); byte[] before = source.EncodeSnapshot();
            QueuePaid(); uint nextRound = _terrain.Round + 1; RememberActualPending(_terrain); _terrain.Clear(nextRound);
            Assert.That(_terrain.Round, Is.EqualTo(nextRound)); Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
            Assert.That(Read<object>(_terrain, "_dependentEditPreparation"), Is.Null);
            Assert.That(_completed.Count, Is.EqualTo(_issued));
            CollectionAssert.AreEqual(Enumerable.Range(0, _issued).ToArray(), _callbacks);
            for (int i = 0; i < 12; i++) { _terrain.Tick(true, Epoch, .016f); yield return null; }
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
            Assert.That(_completed.Count, Is.EqualTo(_issued)); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator NativeFloorMovedDuringCopyIsRequeriedBeforeTheOriginalCapPublishes()
        {
            yield return WaitForOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation");
            var source = Read<BlockWorld>(Read<object>(owner, "Context"), "Source"); byte[] before = source.EncodeSnapshot();
            _floor.transform.position = new Vector3(0, .45f, 0); Physics.SyncTransforms();
            const float raisedCap = GroundCap - .5f;
            for (int frame = 0; frame < PumpLimit && !RootCap(raisedCap); frame++)
            {
                QueuePaid(); _terrain.Tick(true, Epoch, .016f);
                CollectionAssert.AreEqual(before, source.EncodeSnapshot()); yield return null;
            }
            Assert.That(RootCap(raisedCap), Is.True, "Ready metadata must rerun its own native floor query against the final draft.");
            Assert.That(_terrain.MaximumDependentEditPreparationChecks, Is.InRange(1, 1024));
            yield return Drain(); AssertPaymentsAndFifo(); AssertConserved(ChainLength);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator RemoteSourceReplacementRetiresThePrivateOwnerAndPreservesQueuedCallbacks()
        {
            yield return WaitForOwner();
            var owner = Read<object>(_terrain, "_dependentEditPreparation");
            var source = Read<BlockWorld>(Read<object>(owner, "Context"), "Source"); byte[] before = source.EncodeSnapshot();
            QueuePaid(); uint revision = _terrain.Revision + 10;
            var replacement = new BlockWorld(source.Dimensions, source.Round, source.Bottom);
            replacement.Set(new Vector3Int(20, 0, 20), BlockCell.Create(2, true));
            replacement.Seal(revision, replacement.Chunks.Keys.ToArray());
            RememberActualPending(_terrain);
            Assert.That(_terrain.ApplyRemoteSnapshot(replacement.EncodeSnapshot()), Is.True);
            yield return Pump(false, () => _terrain.Revision == revision && Read<object>(_terrain, "_pending") == null);
            Assert.That(Read<object>(_terrain, "_dependentEditPreparation"), Is.Null);
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.World.Cohorts, Is.Empty); CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            yield return Drain(); AssertPaymentsAndFifo();
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        IEnumerator WaitForOwner()
        {
            for (int frame = 0; frame < PumpLimit && Read<object>(_terrain, "_dependentEditPreparation") == null; frame++)
            { QueuePaid(); QueuePaid(); _terrain.Tick(true, Epoch, .016f); yield return null; }
            Assert.That(Read<object>(_terrain, "_dependentEditPreparation"), Is.Not.Null);
        }
        IEnumerator WaitForMovingOwner()
        {
            for (int frame = 0; frame < PumpLimit && Read<object>(_terrain, "_dependentEditPreparation") == null; frame++)
            {
                // The real Add is in the motion column but above every old body;
                // it must invoke BuildGuard without becoming the root's floor.
                int y = BottomY + ChainLength + 8 + frame % 150;
                QueuePaid(BlockWorld.Center(new Vector3Int(2, y, 2)) - Vector3.up * .05f, 1);
                QueuePaid(); _terrain.Tick(true, Epoch, .016f); yield return null;
            }
            var owner = Read<object>(_terrain, "_dependentEditPreparation");
            Assert.That(owner, Is.Not.Null);
            Assert.That(Read<bool>(owner, "MovingBuild"), Is.True,
                "A command queued after owner capture does not exercise transferred moving-build state.");
            Assert.That(Read<HashSet<Vector2Int>>(owner, "MovingColumns"), Does.Contain(new Vector2Int(2, 2)));
            Assert.That(((IEnumerable)Read<object>(owner, "Slots")).Cast<object>().Any(slot =>
            { var cell = Read<Vector3Int>(slot, "Cell"); return cell.x == 2 && cell.z == 2; }), Is.True);
        }
        IEnumerator InstallChain(int length, bool withSentinel = false)
        {
            var seed = MakeChain(length, withSentinel); _initialBlocks = length + (withSentinel ? 1 : 0);
            _rootCohort = seed.Cohorts.Values.Single(c => c.Bottom.y == BottomY).Id;
            int atomic = seed.Clone().EstimateSetCohortSupportWork(_rootCohort) + BlockWorld.MaxCohortCells + 4;
            Assert.That(atomic, Is.EqualTo(5 * length + 405));
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            byte[] wire = seed.EncodeSnapshot(); RememberActualPending(_terrain); Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return Pump(false, () => _terrain.Revision == seed.Revision && Read<object>(_terrain, "_pending") == null);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(_initialBlocks));
        }
        IEnumerator Pump(bool authority, Func<bool> complete)
        {
            for (int frame = 0; frame < PumpLimit && !complete(); frame++)
            { _terrain.Tick(authority, Epoch, .016f); yield return null; }
            bool terminal = complete();
            string diagnostic = null;
            if (!terminal)
            {
                try { diagnostic = RootCapFailureDiagnostic(GroundCap).Replace("RootCap unmet after unchanged pump; ", "Pump predicate false after unchanged limit; ").Replace("expected=", "groundCapReference=") + "; head=[" + JsonUtility.ToJson(_terrain.InspectCommandHead()) + "]; fair=[" + FairFailureDiagnostic() + "]"; }
                catch (Exception exception) { diagnostic = "Terrain diagnostic unavailable: " + exception.GetType().Name + ": " + exception.Message; }
                diagnostic = $"Pump authority={authority} inferredCaller={(authority ? "Drain" : "InstallChain|RemoteSourceReplacementRetiresThePrivateOwnerAndPreservesQueuedCallbacks")}; " + diagnostic;
            }
            Assert.That(terminal, Is.True, terminal ? null : "The real atomic Terrain lane did not reach its required terminal state. " + diagnostic);
        }
        IEnumerator Drain() => Pump(true, () => _completed.Count == _issued && _destroyCompleted.Count == _destroyIssued
            && Read<object>(_terrain, "_pending") == null && Read<object>(_terrain, "_dependentEditPreparation") == null
            && Read<object>(_terrain, "_buildOccupancyPreparation") == null);
        void QueuePaid(Vector3? at = null, int count = 32)
        {
            int id = _issued++; var token = new BlockAttackToken(
                new PaintCredit((ulong)id + 1, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, 32, 8, 2);
            int reserved = token.ReserveBuild(count, false); Assert.That(reserved, Is.EqualTo(count)); _tokens.Add(token);
            _commandOrder.Add("B" + id);
            _terrain.QueueBuild(at ?? new Vector3(1.05f, .001f, .75f), Vector3.up, 1, reserved, 0, true, result =>
            {
                if (!_arm) return;
                Assert.That(_completed.Add(id), Is.True, "One payment cannot receive a second completion callback.");
                Assert.That(result.OrdinaryMainAttack, Is.True);
                token.CompleteBuild(reserved, false, result.Added); _callbacks.Add(id); _terminalOrder.Add("B" + id);
            }, true);
            _commands.Add(((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Last());
        }
        void QueueFrozenDestroy()
        {
            int id = _destroyIssued++;
            var token = new BlockAttackToken(new PaintCredit(1000000UL + (ulong)id, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, 0, 8, 2);
            int reserved = token.ReserveDirect(1); Assert.That(reserved, Is.EqualTo(1)); _directTokens.Add(token);
            _commandOrder.Add("D" + id);
            _terrain.QueueDestroy(BlockWorld.Center(Sentinel), .06f, 0, 1, true, result =>
            {
                if (!_arm) return;
                Assert.That(_destroyCompleted.Add(id), Is.True); Assert.That(result.OrdinaryMainAttack, Is.True);
                token.CompleteDirect(reserved, result.RemovedEnemy); _terminalOrder.Add("D" + id);
            }, reserved, 0, true);
            var command = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Last();
            Assert.That(Read<BlockWorld>(command, "Source"), Is.SameAs(_terrain.World)); _commands.Add(command);
        }
        int QueuedCount() => ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Count();
        bool RootCap(float expected) => _terrain.World.Cohorts.TryGetValue(_rootCohort, out var group)
            && group.LandingKnown && !group.NeedsLandingProof && Math.Abs(group.LandingOffset - expected) < 2e-6f;
        string RootCapFailureDiagnostic(float expected)
        {
            string Describe(BlockWorld world)
            {
                if (world == null) return "null";
                if (!world.Cohorts.TryGetValue(_rootCohort, out var root))
                    return $"rev={world.Revision} round={world.Round} rootMissing cohorts={world.Cohorts.Count}";
                return FormattableString.Invariant($"rev={world.Revision} round={world.Round} root={root.Id} phase={root.Phase} bottom={root.Bottom} known={root.LandingKnown} needsProof={root.NeedsLandingProof} cap={root.LandingOffset:R} expected={expected:R} lower={root.LowerSupport?.Id ?? 0} members={root.MemberIds.Count} retained={Read<Array>(root, "RetainedSupportMotion").Length}");
            }
            var pending = Read<object>(_terrain, "_pending");
            var build = Read<object>(_terrain, "_buildOccupancyPreparation");
            var owner = Read<object>(_terrain, "_dependentEditPreparation");
            var journal = pending == null ? null : Read<object>(pending, "Gravity");
            string pendingState = pending == null ? "null" : $"generate={Read<Task>(pending, "Generate")?.Status} uploaded={Read<int>(pending, "Uploaded")} pieces={Read<Array>(pending, "Pieces")?.Length ?? -1} buildGuard={Read<object>(pending, "BuildGuard") != null} abort={Read<object>(pending, "AbortReason")}";
            return $"RootCap unmet after unchanged pump; published=[{Describe(_terrain.World)}]; pending=[{pendingState}] next=[{Describe(pending == null ? null : Read<BlockWorld>(pending, "Next"))}]; journalNext=[{Describe(journal == null ? null : Read<BlockWorld>(journal, "Next"))}]; build={build != null} buildPhase={(build == null ? -1 : Read<int>(build, "Phase"))} owner={owner != null} context={Read<object>(_terrain, "_dependentPublicationContext") != null} queued={QueuedCount()} callbacks={_completed.Count}/{_issued} destroys={_destroyCompleted.Count}/{_destroyIssued} reservation={Read<ulong>(_terrain, "_fairReservationVersion")} dependentRetry={_terrain.DependentEditPreparationRetries} buildRetry={_terrain.BuildPublicationRetries} fairRestage={_terrain.FairReservationRestages} failures={_terrain.FailedTransactions} gravityStages={_terrain.GravityProgressStages}; retries=[{JsonUtility.ToJson(_terrain.InspectPublicationRetry())}]";
        }
        void AssertConserved(int initial)
        {
            int published = _commands.Sum(command => { var total = Read<BlockMutationResult>(command, "Total");
                return total.Added - total.RemovedEnemy - total.RemovedFriendly; });
            string diagnostic = "Metadata copies, falling and replay must not change the published surviving totals.";
            if (_terrain.PinkCount + _terrain.BlueCount != initial + published)
            {
                try { diagnostic += ConservationFailureDiagnostic(initial, published); }
                catch (Exception exception) { diagnostic += " Conservation diagnostic unavailable: " + exception.GetType().Name + ": " + exception.Message; }
            }
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(initial + published), diagnostic);
        }
        string FairFailureDiagnostic()
        {
            var seed = Read<object>(_terrain, "_fairSeed");
            if (seed == null) return "null";
            var scan = Read<object>(seed, "Scan");
            string state = scan == null ? "null" : FormattableString.Invariant($"finished={Read<bool>(scan, "Finished")} sleeping={Read<bool>(scan, "HasSleeping")} blocker={Read<bool>(scan, "HasBlocker")} expiresAt={Read<double>(scan, "ExpiresAt"):R} phase={Read<int>(scan, "Phase")} cursor={Read<int>(scan, "Cursor")} columns={Read<int>(scan, "ColumnCount")} motions={Read<int>(scan, "MotionCount")}");
            return FormattableString.Invariant($"now={_terrain.SimulationTime:R} foreground={Read<bool>(seed, "Foreground")} dependency={Read<bool>(seed, "Dependency")} hasHead={Read<object>(seed, "Head") != null}; scan=[{state}]");
        }
        string ConservationFailureDiagnostic(int initial, int published)
        {
            // Read actual occupied payloads once, after the original oracle failed.
            // Cached chunk.Count/team totals are not the independent payload count.
            var world = _terrain.World;
            int stablePink = 0, stableBlue = 0, fallingPink = 0, fallingBlue = 0;
            // These independent payload counters only run after the unchanged oracle fails.
            // Array order: stable pink/blue, falling pink/blue; XZ never changes in these motions.
            var chainColumn = new int[4]; var buildColumn = new int[4]; var sentinelColumnTotals = new int[4];
            void CountColumn(Vector3Int cell, byte owner, bool falling)
            {
                var totals = cell.x == 2 && cell.z == 2 ? chainColumn
                    : cell.x == 10 && cell.z == 7 ? buildColumn
                    : cell.x == 20 && cell.z == 20 ? sentinelColumnTotals : null;
                if (totals != null && (owner == 1 || owner == 2)) totals[(falling ? 2 : 0) + (owner == 1 ? 0 : 1)]++;
            }
            foreach (var pair in world.Chunks)
                for (int index = 0; index < pair.Value.Cells.Length; index++)
                {
                    var cell = pair.Value.Cells[index]; if (!cell.Occupied) continue;
                    if (cell.Owner == 1) stablePink++; else if (cell.Owner == 2) stableBlue++;
                    CountColumn(BlockWorld.CellFromIndex(pair.Key, index), cell.Owner, false);
                }
            var fallingSentinel = new System.Text.StringBuilder();
            foreach (var run in world.Falling.Values)
            {
                bool sentinelColumn = run.Bottom.x == Sentinel.x && run.Bottom.z == Sentinel.z;
                float offset = sentinelColumn ? run.Offset(_terrain.SimulationTime) : 0;
                for (int index = 0; index < run.Cells.Length; index++)
                {
                    var cell = run.Cells[index]; if (!cell.Occupied) continue;
                    if (cell.Owner == 1) fallingPink++; else if (cell.Owner == 2) fallingBlue++;
                    CountColumn(run.Bottom, cell.Owner, true);
                    if (!sentinelColumn) continue;
                    var sourceCell = run.Bottom + Vector3Int.up * index;
                    float bottom = sourceCell.y * BlockWorld.CellSize - offset;
                    int first = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize);
                    int last = Mathf.CeilToInt((bottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
                    if (sourceCell == Sentinel || Sentinel.y >= first && Sentinel.y <= last)
                        fallingSentinel.Append(FormattableString.Invariant($" [run={run.Id} index={index} owner={cell.Owner} source={sourceCell} offset={offset:R} actualY={first}..{last}]"));
                }
            }
            int added = 0, removedEnemy = 0, removedFriendly = 0;
            var changedCommands = new System.Text.StringBuilder();
            for (int index = 0; index < _commands.Count; index++)
            {
                var command = _commands[index]; var total = Read<BlockMutationResult>(command, "Total");
                added += total.Added; removedEnemy += total.RemovedEnemy; removedFriendly += total.RemovedFriendly;
                if (total.Changed != 0)
                    changedCommands.Append($" [{_commandOrder[index]} build={Read<bool>(command, "Build")} done={Read<bool>(command, "Done")} completed={Read<bool>(command, "Completed")} point={Read<Vector3>(command, "Point")} added={total.Added} enemy={total.RemovedEnemy} friendly={total.RemovedFriendly} revision={total.Revision}]");
            }
            bool stableSentinel = world.TryGet(Sentinel, out var sentinel);
            int tokenAdded = _tokens.Sum(token => token.Added), tokenRemoved = _directTokens.Sum(token => token.RemovedEnemy);
            return FormattableString.Invariant($"\nConservation failure (read-only; original oracle unchanged): initial={initial} installed={_initialBlocks} ledgerNet={published} actualTeam={world.PinkCount}/{world.BlueCount} revision={world.Revision} round={world.Round} now={_terrain.SimulationTime:R}")
                + $"; payloadStable={stablePink}/{stableBlue} payloadFalling={fallingPink}/{fallingBlue} payloadTotal={stablePink + stableBlue + fallingPink + fallingBlue}"
                + $"; chainColumn(2,2)=stablePink:{chainColumn[0]},stableBlue:{chainColumn[1]},fallingPink:{chainColumn[2]},fallingBlue:{chainColumn[3]}"
                + $"; buildColumn(10,7)=stablePink:{buildColumn[0]},stableBlue:{buildColumn[1]},fallingPink:{buildColumn[2]},fallingBlue:{buildColumn[3]}"
                + $"; sentinelColumn(20,20)=stablePink:{sentinelColumnTotals[0]},stableBlue:{sentinelColumnTotals[1]},fallingPink:{sentinelColumnTotals[2]},fallingBlue:{sentinelColumnTotals[3]}"
                + $"; sentinelStable={stableSentinel} owner={sentinel.Owner} anchored={sentinel.Anchored} sentinelFallingSourceOrActual={fallingSentinel}"
                + $"; commandTotals=added:{added},enemy:{removedEnemy},friendly:{removedFriendly},net:{added - removedEnemy - removedFriendly} retained:{_commands.Count},unique:{new HashSet<object>(_commands).Count}"
                + $"; tokenTotals=added:{tokenAdded},removedEnemy:{tokenRemoved},net:{tokenAdded - tokenRemoved} callbackBuild={_completed.Count}/{_issued} callbackDestroy={_destroyCompleted.Count}/{_destroyIssued} fifoRecorded={_terminalOrder.Count}/{_commandOrder.Count}"
                + $"; buildPreparation={_terrain.BuildOccupancyPreparationStarts}/{_terrain.BuildOccupancyPreparationStages} movingOccupied={_terrain.BuildMovingOccupied}; failures={_terrain.FailedTransactions} buildRetries={_terrain.BuildPublicationRetries} dependentRetries={_terrain.DependentEditPreparationRetries} supportRetries={_terrain.GravitySupportPublicationRetries} fairRestages={_terrain.FairReservationRestages}"
                + $"; changedCommandTotals={changedCommands}";
        }

        void AssertPaymentsAndFifo()
        {
            Assert.That(_completed.Count, Is.EqualTo(_issued));
            Assert.That(_destroyCompleted.Count, Is.EqualTo(_destroyIssued));
            CollectionAssert.AreEqual(Enumerable.Range(0, _issued).ToArray(), _callbacks);
            CollectionAssert.AreEqual(_commandOrder, _terminalOrder, "Build and frozen destruction terminal callbacks retain shared FIFO order.");
            foreach (var token in _tokens)
            {
                Assert.That(token.Added, Is.InRange(0, 32));
                Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(32));
            }
            foreach (var token in _directTokens)
                Assert.That(token.RemovedEnemy + token.DirectRemaining, Is.EqualTo(8));
        }
        static void AssertCapturedContext(object context, object pending)
        {
            Assert.That(Read<BlockWorld>(pending, "FairSource"), Is.SameAs(Read<BlockWorld>(context, "Source")));
            Assert.That(Read<uint>(pending, "FairSourceRevision"), Is.EqualTo(Read<uint>(context, "Revision")));
            Assert.That(Read<uint>(pending, "FairSourceRound"), Is.EqualTo(Read<uint>(context, "Round")));
            Assert.That(Read<ulong>(pending, "FairSourceMutation"), Is.EqualTo(Read<ulong>(context, "Mutation")));
            Assert.That(Read<object>(pending, "FairSeed"), Is.SameAs(Read<object>(context, "FairSeed")));
            Assert.That(Read<bool>(pending, "FairForeground"), Is.EqualTo(Read<bool>(context, "Foreground")));
            Assert.That(Read<bool>(pending, "FairDependency"), Is.EqualTo(Read<bool>(context, "Dependency")));
            Assert.That(Read<ulong>(pending, "FairReservationVersion"), Is.EqualTo(Read<ulong>(context, "ReservationVersion")));
            Assert.That(Read<BlockPublicationWindow>(pending, "Window"), Is.SameAs(Read<BlockPublicationWindow>(context, "Window")));
            Assert.That(Read<BlockPublicationWindow>(pending, "Window").Policy.Version, Is.EqualTo(3701));
        }
        static BlockWorld MakeChain(int length = ChainLength, bool withSentinel = false)
        {
            var world = new BlockWorld(new Vector2(32, 64)); ulong lower = 0;
            if (withSentinel) world.Set(Sentinel, BlockCell.Create(2, true));
            for (int i = 0; i < length; i++) world.Set(new Vector3Int(2, BottomY + i, 2), BlockCell.Create((byte)(1 + i % 2)));
            for (int i = 0; i < length; i++)
            {
                var group = world.BeginCohort(new Vector3Int(2, BottomY + i, 2), 1);
                world.DetachCohortPiece(group.Id, 1, Epoch, out _); world.SetCohortSupport(group.Id, lower, 0, out _);
                group = world.Cohorts[group.Id]; group.LandingKnown = true; group.LandingOffset = 2 + i * BlockWorld.CellSize;
                group.NeedsLandingProof = false; lower = group.Id;
            }
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray()); return world;
        }
        static T Read<T>(object target, string field)
            => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        static void AssertWire(BlockWorld source, BlockWorld current)
        {
            byte[] wire = current.EncodeSnapshot();
            CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(wire).EncodeSnapshot());
            CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(current.EncodeDelta(source.Revision)).EncodeSnapshot());
        }
    }
}
#endif
