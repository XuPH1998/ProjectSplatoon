#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Natural scenarios exercise real public terrain commands and lifecycle calls.
    // Passing requires every original fairness, geometry and COW witness below.
    // Reflection only
    // registers real map colliders, reads witnesses, and obtains original Workers.
    // It never writes Pending, journal, seed, window, command, proof or task state.
    public sealed class BlockFairSchedulingNaturalQueueTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        const double Now = 10;
        static readonly Vector3Int Tower = new(-150, 0, 2);
        static readonly Vector3Int Urgent = new(-100, 0, 40);
        static readonly Vector3Int Middle = new(-80, 0, 40);
        static readonly Vector3Int TailHit = Tower + Vector3Int.up * 160;
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _far;
        SemaphoreSlim _workers;
        BlockWorkerQueue _admissions;
        int _held, _baseCallbacks, _mutationEvents;
        int _clearCalls;
        uint _clearNextRound;
        bool _arm;
        object _advancingPending, _urgent, _middle, _tail, _seed, _yielded;
        BlockWorld _yieldSource, _tailSource;
        byte[] _yieldBytes;
        double _tailSourceTime;
        BlockPublicationWindow _backgroundWindow;
        readonly List<string> _callbacks = new();
        readonly List<string> _atomic = new();
        readonly Dictionary<object, BlockPublicationWindow> _commandWindows = new();
        readonly Dictionary<object, string> _commandWindowCaptureDiagnostics = new();
        string _tailBeforeProof, _tailAfterProof;
        string _tailQueueTiming;
        long _lastForegroundScans, _lastForegroundRebuilds;
        int _foregroundObserved, _backgroundObserved;
        bool _middlePartial, _tailQueuedAfterForeground;
        bool _completionTailQueued, _completionTailSeedAlive;
        readonly HashSet<object> _observedPending = new();
        readonly HashSet<Task> _observedCommandTasks = new();
        readonly HashSet<Mesh> _observedMeshes = new();
        readonly HashSet<GameObject> _observedViews = new();

        void InitializeScenario()
        {
            _held = _baseCallbacks = _mutationEvents = _foregroundObserved = _backgroundObserved = 0;
            _clearCalls = 0; _clearNextRound = 0;
            _arm = true; _middlePartial = _tailQueuedAfterForeground = false;
            _completionTailQueued = _completionTailSeedAlive = false;
            _advancingPending = _urgent = _middle = _tail = _seed = _yielded = null;
            _workers = null; _admissions = null;
            _yieldSource = _tailSource = null; _yieldBytes = null; _backgroundWindow = null;
            _callbacks.Clear(); _atomic.Clear(); _commandWindows.Clear(); _commandWindowCaptureDiagnostics.Clear();
            _tailBeforeProof = _tailAfterProof = null;
            _tailQueueTiming = null;
            _lastForegroundScans = _lastForegroundRebuilds = 0;
            _root = new GameObject("Natural FIFO tail dependency contracts");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(70);
            _terrain.Initialize(null);
            _workers = ReadStatic<SemaphoreSlim>(typeof(BlockTerrain), "Workers");
            _admissions = ReadStatic<BlockWorkerQueue>(typeof(BlockTerrain), "WorkerAdmissions");
            MakeBox("Actual map floor", new Vector3(0, -.05f, 0), new Vector3(32, .1f, 64));
            _far = MakeBox("Registered native snapshot witness", new Vector3(40, 8, 40), Vector3.one);
            Physics.SyncTransforms();
            _terrain.MutationCompleted += OnMutation;
            _terrain.PublishedCommit += ObservePublication;
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            _arm = false;
            var terrain = _terrain;
            // Capture strong actual published/Pending resources and real Task
            // ownership before Clear/OnDestroy can transfer or clear fields.
            RememberActualPending(terrain);
            ReleaseWorkers();
            if (!ReferenceEquals(terrain, null))
            {
                terrain.MutationCompleted -= OnMutation;
                terrain.PublishedCommit -= ObservePublication;
                terrain.Clear(terrain.Round + 1);
            }
            if (_root != null) Object.DestroyImmediate(_root);
            double until = Time.realtimeSinceStartupAsDouble + 180;
            while (!FailureCleanupJoined(terrain) && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(FailureCleanupJoined(terrain), Is.True,
                "Actual Generate/Scheduled/member/Bake/Sorting Tasks, Pending CTS and retired records must join before leaving Play Mode.");
            // Queue idle and completed Tasks do not prove the awaited main-thread
            // cleanup or Unity's deferred resource destruction has finished.
            until = Time.realtimeSinceStartupAsDouble + 180;
            while (!PendingResourcesDestroyed() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(PendingResourcesDestroyed(), Is.True,
                "Retained actual Mesh/View references must be destroyed after their real native task joins.");
            ObserveJoinedPendingFaults();
            if (_workers != null) Assert.That(_workers.CurrentCount, Is.EqualTo(2));
            _observedPending.Clear(); _observedCommandTasks.Clear(); _observedMeshes.Clear(); _observedViews.Clear();
            _terrain = null; _root = null;
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
            // Publish transfers View ownership and clears Prepared mesh/view
            // fields. Capture actual published views before OnDestroy clears them.
            foreach (var view in Read<IDictionary>(terrain, "_views").Values.Cast<object>())
                RememberViewResources(view);
            // Complete clears Command.Sorting. Preserve the genuine Task before
            // destruction; its queue lease can end before its Task continuation.
            foreach (var command in ((IEnumerable)Read<object>(terrain, "_commands")).Cast<object>()
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
            => _observedMeshes.All(mesh => mesh == null) && _observedViews.All(view => view == null);
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
                if (pieces == null) continue;
                foreach (var piece in pieces)
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

        // Diagnostic adversarial-input characterization, not a claim about
        // the V3 failure's cause and not a replacement acceptance gate.
        [UnityTest, Timeout(600000)]
        public IEnumerator EightNonOverlappingReservationsCanRetireSuccessiveRealBackgroundsThenDrainWithoutLoss()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            InitializeScenario();
            yield return EightReservationRetirementScenario();
        }
        // The captured diagnostic local is created after EnterPlayMode's domain reload.
        IEnumerator EightReservationRetirementScenario()
        {
            BlockTerrain.PendingWallDiagnostic last = default;
            Action<BlockTerrain.PendingWallDiagnostic> terminal = sample => last = sample;
            _terrain.PendingTransactionFinished += terminal;
            try
            {
                yield return PrepareHeldBackground();
                // This scenario does not use urgent/middle/tail classifications.
                _terrain.PublishedCommit -= ObservePublication;
                var source = _terrain.World; var sourceBytes = source.EncodeSnapshot();
                uint revision = _terrain.Revision;
                long restages = _terrain.FairReservationRestages, callbacksBefore = _terrain.CommandsCompleted;
                int callbacks = 0;
                var commands = new List<object>();
                object retainedSeed = null;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    yield return Pump(() => PureBackgroundPending() != null, "reservation-churn-background-" + attempt);
                    var pending = PureBackgroundPending();
                    long wallId = Read<long>(pending, "WallId");
                    Assert.That(wallId, Is.GreaterThan(0));
                    _terrain.QueueDestroy(new Vector3(1000, 1, 1000), .001f, 0, 1, true, result =>
                    {
                        if (!_arm) return;
                        callbacks++;
                        Assert.That(result.Changed, Is.Zero);
                    }, 1, 0, false);
                    commands.Add(LastQueued());
                    Tick(true);
                    Assert.That(_terrain.FairReservationRestages, Is.EqualTo(restages + attempt + 1));
                    Assert.That(last.Id, Is.EqualTo(wallId));
                    Assert.That(last.Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Aborted));
                    Assert.That(last.AbortReason, Is.EqualTo(BlockTerrain.PendingAbortReason.FairReservationChanged));
                    Assert.That(_terrain.World, Is.SameAs(source)); Assert.That(_terrain.Revision, Is.EqualTo(revision));
                    CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
                    Assert.That(callbacks, Is.Zero, "No queued reservation receives free completion while every background candidate is retired.");
                    var seed = Read<object>(_terrain, "_fairSeed"); Assert.That(seed, Is.Not.Null);
                    if (retainedSeed == null) retainedSeed = seed;
                    else Assert.That(seed, Is.SameAs(retainedSeed), "Restaging must retain the original background lease.");
                    Assert.That(Read<BlockPublicationWindow>(seed, "BackgroundWindow"), Is.SameAs(_backgroundWindow));
                    if (attempt == 0) ReleaseWorkers();
                }
                // Stop introducing reservations. Complete all original commands
                // and the exact original background; do not extend a live clock
                // or discard cells to manufacture a completed drain.
                yield return Pump(() => callbacks == 8 && _terrain.PendingTransactions == 0
                    && TowerFallingCells(_terrain.World) == 319, "reservation-churn-stop-and-drain");
                Assert.That(_terrain.CommandsCompleted - callbacksBefore, Is.EqualTo(8));
                Assert.That(commands.All(command => Read<bool>(command, "Completed")), Is.True);
                Assert.That(commands.Distinct().Count(), Is.EqualTo(8));
                Assert.That(_baseCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(1));
                Assert.That(_terrain.BlueCount, Is.EqualTo(319)); Assert.That(_terrain.PinkCount, Is.Zero);
                AssertExactTower(false);
                Assert.That(_terrain.FairBackgroundTerrainPublications, Is.GreaterThan(0));
                Assert.That(_terrain.FailedTransactions, Is.Zero);
                CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            }
            finally { if (_terrain != null) _terrain.PendingTransactionFinished -= terminal; }
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator TailOnlyBlockerRequiresCompleteBackgroundAttemptBetweenEveryPartialCurrentHead()
        {
            // The current test owns the reload PC. Scene state and closures are
            // created only after this wrapper resumes in the new Play Mode domain.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            InitializeScenario();
            yield return TailOnlyBlockerScenario();
        }
        IEnumerator TailOnlyBlockerScenario()
        {
            yield return PrepareHeldBackground();
            QueueUrgent(); QueueMiddle(); QueueTail(true);
            yield return CaptureYield(); ReleaseWorkers();
            yield return FinishChain(true, true);
            CollectionAssert.AreEqual(new[] { "urgent", "middle", "tail" }, _callbacks);
            Assert.That(_middlePartial, Is.True, "The legal 512-budget build must actually split at its unchanged eight-key lease.");
            Assert.That(_terrain.FairDependencyAtomicPublications, Is.GreaterThan(0));
            Assert.That(_terrain.FairCompleteBlockedScans, Is.GreaterThan(0));
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator NewOverlappingTailFromActualPaidCallbackRequiresFreshProofAndPreservesFrozenTargets()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            InitializeScenario();
            yield return NewOverlappingTailScenario();
        }
        IEnumerator NewOverlappingTailScenario()
        {
            yield return PrepareHeldBackground();
            QueueUrgent(QueueCompletionTail); QueueMiddle();
            yield return CaptureYield(); ReleaseWorkers();
            yield return FinishChain(true, false);
            Assert.That(_completionTailQueued, Is.True);
            Assert.That(_tailQueuedAfterForeground, Is.True);
            if (_completionTailSeedAlive)
            {
                Assert.That(_terrain.FairInputsDuringSeed, Is.GreaterThan(0));
                Assert.That(_terrain.FairDependencyAtomicPublications, Is.GreaterThan(0));
            }
            Assert.That(_middlePartial, Is.True);
            CollectionAssert.AreEqual(new[] { "urgent", "middle", "tail" }, _callbacks);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator NewDisjointTailCannotAuthorizeIntermediaryBeforePublishableBackground()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            InitializeScenario();
            yield return NewDisjointTailScenario();
        }
        IEnumerator NewDisjointTailScenario()
        {
            yield return PrepareHeldBackground();
            QueueUrgent(() => QueueTail(false)); QueueMiddle();
            yield return CaptureYield(); ReleaseWorkers();
            yield return FinishChain(false, false);
            Assert.That(_tailQueuedAfterForeground, Is.True);
            Assert.That(_terrain.FairDependencyAtomicPublications, Is.Zero);
            int bg = _atomic.IndexOf("background"), middle = _atomic.IndexOf("middle");
            Assert.That(bg, Is.GreaterThanOrEqualTo(0));
            Assert.That(middle, Is.GreaterThan(bg), "A real disjoint background terrain commit must win first.");
            CollectionAssert.AreEqual(new[] { "urgent", "middle", "tail" }, _callbacks);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PaidForegroundCallbackClearEndsOldSeedAndCannotReplayOldTowerIntoNewRound()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            InitializeScenario();
            yield return PaidForegroundCallbackClearScenario();
        }
        IEnumerator PaidForegroundCallbackClearScenario()
        {
            yield return PrepareHeldBackground();
            uint nextRound = _clearNextRound = _terrain.Round + 1;
            _clearCalls = 0;
            QueueUrgent(ClearFromPaidCallback);
            yield return CaptureYield(); ReleaseWorkers();
            yield return Pump(ClearCalledOnce, "paid-callback-clear");
            Assert.That(Read<object>(_terrain, "_fairSeed"), Is.Null);
            for (int i = 0; i < 120; i++) { Tick(true); yield return null; }
            Assert.That(_clearCalls, Is.EqualTo(1)); Assert.That(_terrain.Round, Is.EqualTo(nextRound));
            Assert.That(_terrain.BlueCount + _terrain.PinkCount, Is.Zero);
            Assert.That(_terrain.World.Cohorts.Count + _terrain.World.Falling.Count, Is.Zero);
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_baseCallbacks, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { "urgent" }, _callbacks);
            CollectionAssert.AreEqual(_yieldBytes, _yieldSource.EncodeSnapshot());
            Assert.That(CollisionAt(Tower + Vector3Int.up * 319), Is.False);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PublicPolicyDowngradeAndRealNativeSupportRetryRetainOriginalBackgroundLease()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            InitializeScenario();
            UseQuantizedNativeFloorForBoundsRetry();
            yield return PublicPolicyDowngradeScenario();
        }
        void UseQuantizedNativeFloorForBoundsRetry()
        {
            // This fixture invalidates a retained native-bounds absence proof.
            // Keep the real floor in landing cell zero, outside the cell-one
            // support-probe bounds; the other four fixtures keep their floor.
            var floor = Read<HashSet<Collider>>(_terrain, "_staticColliders")
                .OfType<BoxCollider>().Single(collider => collider.name == "Actual map floor");
            floor.transform.position += Vector3.down * (BlockWorld.CellSize * .25f);
            Physics.SyncTransforms();
            Assert.That(floor.bounds.max.y, Is.EqualTo(-BlockWorld.CellSize * .25f).Within(.000001f));
            float probeLength = ReadStatic<float>(typeof(BlockTerrain), "StaticSupportProbeLength");
            int mask = Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            foreach (var cell in new[] { Tower, Urgent, Middle })
            {
                var point = BlockWorld.Center(cell);
                Assert.That(BlockWorld.CellAt(GroundPoint(cell) + Vector3.up * (BlockWorld.CellSize * .51f)),
                    Is.EqualTo(cell), "The paid build's first source address must remain unchanged.");
                Assert.That(Physics.Raycast(point, Vector3.down, out var hit, probeLength, mask, QueryTriggerInteraction.Ignore),
                    Is.True, "Grid-zero must retain real native floor support.");
                Assert.That(hit.collider, Is.SameAs(floor));
                Assert.That(hit.normal.y, Is.GreaterThan(.2f));
                Assert.That(Mathf.CeilToInt(hit.point.y / BlockWorld.CellSize - .0001f), Is.EqualTo(0));
                Assert.That(floor.bounds.max.y,
                    Is.LessThan(BlockWorld.Center(cell + Vector3Int.up).y - probeLength),
                    "All tower cells above the paid base must admit a real native-bounds negative proof.");
                Assert.That(point.y - BlockWorld.CellSize * .45f, Is.GreaterThan(floor.bounds.max.y),
                    "Grid-zero remains clear of the unchanged build overlap box.");
            }
        }
        IEnumerator PublicPolicyDowngradeScenario()
        {
            yield return PrepareHeldBackground();
            QueueUrgent(() => _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(71));
            QueueMiddle(); QueueTail(true);
            yield return CaptureYield(); ReleaseWorkers();
            yield return Pump(() => BackgroundPendingWithNativeSupportWitness() != null, "native-support-pending");
            var rejected = BackgroundPendingWithNativeSupportWitness();
            Assert.That(Read<BlockPublicationWindow>(rejected, "Window"), Is.SameAs(_backgroundWindow));
            Assert.That(_backgroundWindow.Policy.Version, Is.EqualTo(70));
            Assert.That(_backgroundWindow.MaximumChangedKeys, Is.EqualTo(32));
            var oldDraft = Read<BlockWorld>(rejected, "Next");
            long retries = _terrain.GravitySupportPublicationRetries;
            _far.transform.position += new Vector3(.25f, .1f, -.3f); Physics.SyncTransforms();
            yield return Pump(() => _terrain.GravitySupportPublicationRetries > retries, "native-support-retry");
            Assert.That(_terrain.GravitySupportPublicationRetries, Is.EqualTo(retries + 1));
            Assert.That(Read<object>(_terrain, "_fairSeed"), Is.SameAs(_seed));
            Assert.That(_terrain.World, Is.Not.SameAs(oldDraft));
            yield return FinishChain(true, true);
            Assert.That(_middlePartial, Is.True);
            Assert.That(_terrain.FairCapturedBackgroundPolicyVersion, Is.Zero, "The seed ends only after actual background terrain commit.");
        }

        IEnumerator PrepareHeldBackground()
        {
            var source = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            for (int y = 0; y < 320; y++) Assert.That(source.Set(Tower + Vector3Int.up * y, BlockCell.Create(2)), Is.True);
            source.Seal(_terrain.Revision + 1, source.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(source.EncodeSnapshot()), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, "remote-install", false);
            Assert.That(_terrain.BlueCount, Is.EqualTo(320));
            _terrain.QueueDestroy(BlockWorld.Center(Tower), 0, 0, 1, true, result =>
            {
                if (!_arm) return;
                _baseCallbacks++;
                Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                Assert.That(result.Added + result.RemovedFriendly, Is.Zero);
                Assert.That(_held, Is.Zero);
                // Acquire only permits genuinely returned by all prior paid work.
                for (int i = 0; i < 2; i++)
                {
                    Assert.That(_workers.Wait(0), Is.True, "Natural raw-permit witness unavailable; do not fabricate it.");
                    _held++;
                }
            }, 1, 0, true);
            yield return Pump(() => _baseCallbacks == 1 && _held == 2 && PureBackgroundPending() != null,
                "paid-base-and-pure-background");
            _yielded = PureBackgroundPending();
            _yieldSource = _terrain.World; _yieldBytes = _yieldSource.EncodeSnapshot();
            _backgroundWindow = Read<BlockPublicationWindow>(_yielded, "Window");
            Assert.That(_baseCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(1));
            Assert.That(_terrain.BlueCount, Is.EqualTo(319)); Assert.That(_held, Is.EqualTo(2));
            Assert.That(_backgroundWindow.MaximumChangedKeys, Is.EqualTo(32));
        }

        void QueueUrgent(Action after = null)
        {
            _terrain.QueueBuild(GroundPoint(Urgent), Vector3.up, 1, 32, 0, false, result =>
            {
                if (!_arm) return;
                _callbacks.Add("urgent");
                Assert.That(result.Added, Is.EqualTo(32));
                Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                if (_arm) after?.Invoke();
            }, true);
            _urgent = LastQueued();
        }
        void QueueMiddle()
        {
            // Accepted 512 main-attack maximum; the unchanged 32m top clips
            // candidates naturally at 320. The ordinary eight-key cap remains.
            _terrain.QueueBuild(GroundPoint(Middle), Vector3.up, 1, 512, 0, false, result =>
            { if (!_arm) return; _callbacks.Add("middle"); Assert.That(result.Added, Is.EqualTo(320)); }, true);
            _middle = LastQueued();
        }
        void QueueCompletionTail()
        {
            // Observe the actual whole-command callback. A paid partial can
            // already have allowed the original background to publish and end.
            Assert.That(_completionTailQueued, Is.False);
            Assert.That(_seed, Is.Not.Null);
            Assert.That(Read<bool>(_urgent, "Completed"), Is.True);
            Assert.That(Read<BlockMutationResult>(_urgent, "Total").Added, Is.EqualTo(32));
            CollectionAssert.AreEqual(new[] { "urgent" }, _callbacks);
            var currentSeed = Read<object>(_terrain, "_fairSeed");
            _completionTailSeedAlive = ReferenceEquals(currentSeed, _seed);
            if (_completionTailSeedAlive) Assert.That(_backgroundObserved, Is.Zero);
            else
            {
                Assert.That(currentSeed, Is.Null,
                    "The original seed must have ended, rather than been silently replaced.");
                Assert.That(_backgroundObserved, Is.EqualTo(1),
                    "A completed seed requires its actual nonempty background terrain publication.");
            }
            QueueTail(true);
            _completionTailQueued = true;
        }
        void QueueTail(bool overlap)
        {
            _tailQueueTiming ??= TailQueueTimingDiagnostics();
            _tailBeforeProof ??= CanonicalTowerProof("before-tail-queue");
            _tailQueuedAfterForeground = _terrain.FairForegroundAtomicPublications > 0;
            var point = overlap ? BlockWorld.Center(TailHit) : new Vector3(5.05f, 16.05f, 4.05f);
            _terrain.QueueDestroy(point, .20f, 0, 1, true, result =>
            {
                if (!_arm) return;
                _tailAfterProof ??= CanonicalTowerProof("actual-tail-callback");
                _callbacks.Add("tail"); Assert.That(result.RemovedEnemy, Is.EqualTo(overlap ? 5 : 0));
                Assert.That(result.Added + result.RemovedFriendly, Is.Zero);
            }, 8, 0, true);
            _tail = LastQueued(); _tailSource = Read<BlockWorld>(_tail, "Source");
            _tailSourceTime = Read<double>(_tail, "SourceTime");
        }
        IEnumerator CaptureYield()
        {
            yield return Pump(() => _terrain.FairBackgroundYields == 1 && Read<object>(_terrain, "_pending") != null,
                "foreground-yield");
            _seed = Read<object>(_terrain, "_fairSeed"); Assert.That(_seed, Is.Not.Null);
            Assert.That(Read<BlockPublicationWindow>(_seed, "BackgroundWindow"), Is.SameAs(_backgroundWindow));
            var pending = Read<object>(_terrain, "_pending");
            Assert.That(pending, Is.Not.SameAs(_yielded)); Assert.That(Read<bool>(pending, "FairForeground"), Is.True);
            Assert.That(_terrain.World, Is.SameAs(_yieldSource));
            Assert.That(_callbacks.Count, Is.Zero); Assert.That(_mutationEvents, Is.EqualTo(1));
        }
        IEnumerator FinishChain(bool destroyedTowerRows, bool dependencyExpected)
        {
            int expected = destroyedTowerRows ? 314 : 319;
            yield return Pump(() => _callbacks.Count == 3 && _terrain.PendingTransactions == 0
                && TowerCells(_terrain.World) == expected && StableTowerCells(_terrain.World) == 0
                && _terrain.UnstableColumns == 0, "chain-complete");
            Assert.That(_foregroundObserved, Is.GreaterThan(0)); Assert.That(_backgroundObserved, Is.GreaterThan(0));
            Assert.That(_backgroundObserved, Is.EqualTo(1), "Only one terminal terrain publication belongs to this original seed.");
            Assert.That(_terrain.FairBackgroundTerrainPublications, Is.GreaterThanOrEqualTo(1));
            Assert.That(Read<object>(_terrain, "_fairSeed"), Is.Null);
            if (dependencyExpected) Assert.That(_terrain.FairDependencyAtomicPublications, Is.GreaterThan(0),
                _terrain.FairDependencyAtomicPublications > 0 ? null
                    : "Tail queue timing [" + _tailQueueTiming + "]. "
                        + WitnessDiagnostics("dependency-required-after-chain", true, 0, 0));
            Assert.That(_baseCallbacks, Is.EqualTo(1)); Assert.That(_mutationEvents, Is.EqualTo(destroyedTowerRows ? 4 : 3));
            AssertExactTower(destroyedTowerRows);
            AssertExactBuild(Urgent, 32); AssertExactBuild(Middle, 320);
            Assert.That(_terrain.BlueCount, Is.EqualTo(expected)); Assert.That(_terrain.PinkCount, Is.EqualTo(352));
            CollectionAssert.AreEqual(_yieldBytes, _yieldSource.EncodeSnapshot());
            Assert.That(_terrain.FailedTransactions, Is.Zero); Assert.That(_terrain.StaticProofInvalidations, Is.Zero);
        }

        void ObservePublication(BlockTerrainCommit commit)
        {
            var pending = _advancingPending;
            if (pending == null || _seed == null) return;
            bool originalSeed = ReferenceEquals(Read<object>(pending, "FairSeed"), _seed);
            bool foreground = Read<bool>(pending, "FairForeground");
            if (originalSeed && !foreground)
            {
                Assert.That(Read<BlockPublicationWindow>(pending, "Window"), Is.SameAs(_backgroundWindow));
                if (Read<Vector3Int[]>(pending, "Changed").Length != 0)
                {
                    Assert.That(_terrain.FairBackgroundYields, Is.EqualTo(1), "No continuation renews credit before this actual background commit.");
                    _backgroundObserved++; _atomic.Add("background");
                }
                return;
            }
            var pairs = ((IEnumerable)Read<object>(pending, "Results")).Cast<object>().ToArray();
            foreach (var pair in pairs)
            {
                var command = Read<object>(pair, "Item1"); var result = Read<BlockMutationResult>(pair, "Item2");
                if (result.Changed == 0 && !Read<bool>(command, "Done")) continue;
                var window = Read<BlockPublicationWindow>(pending, "Window");
                if (_commandWindows.TryGetValue(command, out var original))
                    Assert.That(window, Is.SameAs(original), ReferenceEquals(window, original) ? null
                        : WindowFailureDiagnostics(command, pending, original, window));
                else
                {
                    _commandWindows.Add(command, window);
                    _commandWindowCaptureDiagnostics.Add(command, WindowContextDiagnostics(pending));
                }
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(window.MaximumChangedKeys));
                if (originalSeed && Read<bool>(pending, "FairDependency"))
                {
                    Assert.That(_terrain.FairCompleteBlockedScans, Is.GreaterThan(_lastForegroundScans),
                        "Each necessary CURRENT HEAD segment requires a new completed classifier.");
                    Assert.That(_terrain.FairBackgroundRebuilds, Is.GreaterThan(_lastForegroundRebuilds));
                    Assert.That(Read<object>(_terrain, "_fairLastDependencyHead"), Is.SameAs(command));
                    Assert.That(Read<object>(_terrain, "_fairLastDependencyBlocker"), Is.SameAs(_tail),
                        "Only the actual live tail reservation can explain this fixture's dependency.");
                    Assert.That(Read<Vector2Int>(_terrain, "_fairLastDependencyColumn"), Is.EqualTo(new Vector2Int(Tower.x, Tower.z)));
                    Assert.That(Read<ulong>(_terrain, "_fairLastDependencyReservationVersion"),
                        Is.EqualTo(Read<ulong>(pending, "FairReservationVersion")));
                    var proofSource = Read<BlockWorld>(_terrain, "_fairLastDependencySource");
                    Assert.That(proofSource, Is.Not.Null);
                    Assert.That(TowerCells(proofSource), Is.GreaterThan(0),
                        "A queued phantom work slot is not an actual source geometry blocker.");
                    var proofCell = Read<Vector3Int>(_terrain, "_fairLastDependencyCell");
                    ulong proofRun = Read<ulong>(_terrain, "_fairLastDependencyRun");
                    Assert.That(AtTower(proofCell), Is.True);
                    if (proofRun == 0)
                    {
                        Assert.That(proofSource.TryGet(proofCell, out var liveCell), Is.True);
                        Assert.That(liveCell.Occupied && !liveCell.Anchored, Is.True);
                    }
                    else
                    {
                        Assert.That(proofSource.Falling.TryGetValue(proofRun, out var liveRun), Is.True);
                        int offset = proofCell.y - liveRun.Bottom.y;
                        Assert.That(offset, Is.InRange(0, liveRun.Cells.Length - 1));
                        Assert.That(liveRun.Cells[offset].Occupied, Is.True);
                        Assert.That(liveRun.Bottom.x, Is.EqualTo(proofCell.x));
                        Assert.That(liveRun.Bottom.z, Is.EqualTo(proofCell.z));
                    }
                }
                if (originalSeed && foreground)
                {
                    _foregroundObserved++;
                    _lastForegroundScans = _terrain.FairCompleteBlockedScans;
                    _lastForegroundRebuilds = _terrain.FairBackgroundRebuilds;
                }
                string name = ReferenceEquals(command, _urgent) ? "urgent" : ReferenceEquals(command, _middle) ? "middle"
                    : ReferenceEquals(command, _tail) ? "tail" : "unknown";
                Assert.That(name, Is.Not.EqualTo("unknown")); _atomic.Add(name);
                if (ReferenceEquals(command, _middle) && result.Added > 0 && !Read<bool>(command, "Done")) _middlePartial = true;
            }
        }
        void Tick(bool authority)
        {
            if (_seed != null && _backgroundObserved == 0 && _terrain.Round == _yieldSource.Round)
                Assert.That(Read<object>(_terrain, "_fairSeed"), Is.SameAs(_seed),
                    "This fixture still has real old background terrain; physical retries and metadata cannot replace its seed.");
            if (_tail != null && !Read<bool>(_tail, "Completed"))
            {
                Assert.That(Read<BlockWorld>(_tail, "Source"), Is.SameAs(_tailSource));
                Assert.That(Read<double>(_tail, "SourceTime"), Is.EqualTo(_tailSourceTime));
            }
            _advancingPending = Read<object>(_terrain, "_pending");
            if (_advancingPending != null)
            { _observedPending.Add(_advancingPending); RememberPendingResources(_advancingPending); }
            try { _terrain.Tick(authority, Now, 1f / 60); }
            finally { _advancingPending = null; }
        }
        void ClearFromPaidCallback() { _clearCalls++; _terrain.Clear(_clearNextRound); }
        bool ClearCalledOnce() => _clearCalls == 1;
        IEnumerator Pump(Func<bool> done, string phase, bool authority = true)
        {
            double started = Time.realtimeSinceStartupAsDouble, until = started + 180;
            int frames;
            for (frames = 0; frames < 12000 && !done() && Time.realtimeSinceStartupAsDouble < until; frames++)
            { Tick(authority); yield return null; }
            bool ready = done();
            Assert.That(ready, Is.True, "Required natural witness missing; this prepared contract cannot claim a weaker pass."
                + (ready ? string.Empty : " " + WitnessDiagnostics(phase, authority, frames,
                    Time.realtimeSinceStartupAsDouble - started)));
        }
        string WitnessDiagnostics(string phase, bool authority, int frames, double elapsed)
        {
            string prefix = $"phase={phase}, frames={frames}/12000, elapsedSeconds={elapsed:F3}/180, authority={authority}";
            try
            {
                if (ReferenceEquals(_terrain, null)) return prefix + ", terrain=null";
                var world = _terrain.World;
                var pending = DiagnosticValue(_terrain, "_pending");
                var currentSeed = DiagnosticValue(_terrain, "_fairSeed");
                var columns = DiagnosticValue(_terrain, "_cohortColumnWork") as IDictionary;
                var motions = DiagnosticValue(_terrain, "_cohortMotionWork") as IDictionary;
                var towerColumn = new Vector2Int(Tower.x, Tower.z);
                var towerWork = columns != null && columns.Contains(towerColumn) ? columns[towerColumn] : null;
                // Failure-only inspection: 320 exact stable addresses and at most
                // 512 runs x 256 cells. Report completeness instead of hiding caps.
                int stableTower = 0, stableMin = int.MaxValue, stableMax = int.MinValue;
                if (world != null) for (int y = 0; y < 320; y++)
                    if (world.TryGet(Tower + Vector3Int.up * y, out _))
                    { stableTower++; stableMin = Math.Min(stableMin, y); stableMax = Math.Max(stableMax, y); }
                int fallingTower = 0, sampledRuns = 0, fallingMin = int.MaxValue, fallingMax = int.MinValue, fallingOutside320 = 0;
                bool complete = world != null && world.Falling.Count <= 512;
                if (world != null) foreach (var run in world.Falling.Values.Take(512))
                {
                    sampledRuns++;
                    if (!AtTower(run.Bottom)) continue;
                    if (run.Cells.Length > 256) complete = false;
                    for (int index = 0; index < Math.Min(256, run.Cells.Length); index++)
                        if (run.Cells[index].Occupied)
                        {
                            int y = run.Bottom.y + index;
                            fallingTower++; fallingMin = Math.Min(fallingMin, y); fallingMax = Math.Max(fallingMax, y);
                            if (y < 0 || y >= 320) fallingOutside320++;
                        }
                }
                var window = DiagnosticValue(pending, "Window") as BlockPublicationWindow;
                return prefix + $", round={_terrain.Round}, clearCalls={_clearCalls}, clearNextRound={_clearNextRound}"
                    + $", callbacks={_callbacks.Count}[{string.Join(",", _callbacks.Take(8))}], atomic={_atomic.Count}[{string.Join(",", _atomic.Take(8))}]"
                    + $", baseCallbacks={_baseCallbacks}, mutationEvents={_mutationEvents}, held={_held}, permits={_workers?.CurrentCount}, admissionsIdle={_admissions?.WhenIdle.IsCompleted}"
                    + $", commands={_terrain.CommandsCompleted}/{_terrain.CommandsQueued}, pendingTransactions={_terrain.PendingTransactions}, unstableColumns={_terrain.UnstableColumns}"
                    + $", stableTower={(world == null ? -1 : stableTower)}, stableSourceY={SourceYBounds(stableTower, stableMin, stableMax)}, fallingTowerSample={fallingTower}, fallingSourceY={SourceYBounds(fallingTower, fallingMin, fallingMax)}, fallingSourceOutside320={fallingOutside320}, sampledRuns={sampledRuns}/{world?.Falling.Count}, fallingSampleComplete={complete}"
                    + $", tailBeforeProof=[{_tailBeforeProof}], tailAfterProof=[{_tailAfterProof}]"
                    + $", columnWork={columns?.Count}, motionWork={motions?.Count}, towerWorkPresent={towerWork != null}, towerScan={DiagnosticValue(towerWork, "Scan")}, towerReady={DiagnosticValue(towerWork, "Ready")}, towerProving={DiagnosticValue(towerWork, "Proving")}, towerCohort={DiagnosticValue(towerWork, "Cohort")}"
                    + $", pendingPresent={pending != null}, pendingRemote={DiagnosticValue(pending, "Remote")}, pendingForeground={DiagnosticValue(pending, "FairForeground")}, pendingPureBackground={DiagnosticValue(pending, "PureAuthorityBackground")}, pendingPrivate={DiagnosticValue(pending, "PrivateGravity")}, pendingChanged={(DiagnosticValue(pending, "Changed") as Array)?.Length}, windowKeys={window?.MaximumChangedKeys}"
                    + $", seedPresent={currentSeed != null}, expectedSeedPresent={_seed != null}, sameSeed={ReferenceEquals(currentSeed, _seed)}, seedRound={DiagnosticValue(currentSeed, "Round")}, seedForeground={DiagnosticValue(currentSeed, "Foreground")}"
                    + $", fairYields={_terrain.FairBackgroundYields}, foregroundObserved={_foregroundObserved}, backgroundObserved={_backgroundObserved}, fairBackgroundPublications={_terrain.FairBackgroundTerrainPublications}, fairRebuilds={_terrain.FairBackgroundRebuilds}, fairScans={_terrain.FairCompleteBlockedScans}, fairDependencies={_terrain.FairDependencyAtomicPublications}, supportRetries={_terrain.GravitySupportPublicationRetries}";
            }
            catch (Exception exception) { return prefix + ", diagnosticReadFailed=" + exception.GetType().Name; }
        }
        static object DiagnosticValue(object owner, string name)
        {
            if (ReferenceEquals(owner, null)) return null;
            var field = owner.GetType().GetField(name, Fields);
            return field == null ? "missing:" + name : field.GetValue(owner);
        }
        static string DiagnosticIdentity(object value)
            => ReferenceEquals(value, null) ? "null" : value.GetType().FullName + "#" + RuntimeHelpers.GetHashCode(value);
        static string SourceYBounds(int count, int minimum, int maximum)
            => count == 0 ? "none" : minimum + ".." + maximum;
        string TailQueueTimingDiagnostics()
        {
            try
            {
                var currentSeed = DiagnosticValue(_terrain, "_fairSeed");
                var urgentTotal = DiagnosticValue(_urgent, "Total");
                string urgentAdded = urgentTotal is BlockMutationResult total ? total.Added.ToString() : "unavailable";
                return $"originalSeed={DiagnosticIdentity(_seed)}, currentSeed={DiagnosticIdentity(currentSeed)}"
                    + $", sameSeed={ReferenceEquals(_seed, currentSeed)}, originalSeedPresent={_seed != null}, currentSeedForeground={DiagnosticValue(currentSeed, "Foreground")}"
                    + $", backgroundObserved={_backgroundObserved}, atomicCount={_atomic.Count}, atomic=[{string.Join(",", _atomic)}]"
                    + $", urgentDone={DiagnosticValue(_urgent, "Done")}, urgentCompleted={DiagnosticValue(_urgent, "Completed")}, urgentTotalAdded={urgentAdded}"
                    + $", fairForegroundPublications={_terrain.FairForegroundAtomicPublications}, fairBackgroundPublications={_terrain.FairBackgroundTerrainPublications}";
            }
            catch (Exception exception) { return "diagnosticReadFailed=" + exception.GetType().Name; }
        }
        string CanonicalTowerProof(string phase)
        {
            try
            {
                var columns = DiagnosticValue(_terrain, "_cohortColumnWork") as IDictionary;
                var column = new Vector2Int(Tower.x, Tower.z);
                var work = columns != null && columns.Contains(column) ? columns[column] : null;
                return $"phase={phase}, source={DiagnosticIdentity(_terrain?.World)}, sourceRevision={_terrain?.Revision}, sourceTopExclusive=320"
                    + $", canonical={DiagnosticIdentity(work)}, scan={DiagnosticValue(work, "Scan")}, bottom={DiagnosticValue(work, "Bottom")}, top={DiagnosticValue(work, "Top")}, ready={DiagnosticValue(work, "Ready")}, proving={DiagnosticValue(work, "Proving")}, cohort={DiagnosticValue(work, "Cohort")}";
            }
            catch (Exception exception) { return "phase=" + phase + ", diagnosticReadFailed=" + exception.GetType().Name; }
        }
        string WindowContextDiagnostics(object pending)
            => $"pendingSeed={DiagnosticIdentity(DiagnosticValue(pending, "FairSeed"))}, currentSeed={DiagnosticIdentity(DiagnosticValue(_terrain, "_fairSeed"))}, fixtureSeed={DiagnosticIdentity(_seed)}, originalSeed={ReferenceEquals(DiagnosticValue(pending, "FairSeed"), _seed)}, foreground={DiagnosticValue(pending, "FairForeground")}, fairDependency={DiagnosticValue(pending, "FairDependency")}";
        static string WindowDescription(BlockPublicationWindow window)
        {
            if (window == null) return "null";
            var policy = window.Policy;
            return $"id={DiagnosticIdentity(window)}, operations={window.Operations}, cap={window.MaximumChangedKeys}, policy={DiagnosticIdentity(policy)}, version={policy.Version}, ordinaryKeys={policy.OrdinaryAttackKeys}, destructionKeys={policy.DestructionKeys}, gravityKeys={policy.GravityKeys}, metadataKeys={policy.MetadataKeys}, frameKeys={policy.FrameKeys}, frameMilliseconds={policy.FrameSynchronousMilliseconds}";
        }
        string WindowFailureDiagnostics(object command, object pending, BlockPublicationWindow original, BlockPublicationWindow current)
        {
            string name = ReferenceEquals(command, _urgent) ? "urgent" : ReferenceEquals(command, _middle) ? "middle"
                : ReferenceEquals(command, _tail) ? "tail" : "unknown";
            try
            {
                _commandWindowCaptureDiagnostics.TryGetValue(command, out string captured);
                return $"Same paid command changed publication-window identity: command={name}, commandIdentity={DiagnosticIdentity(command)}, current=[{WindowContextDiagnostics(pending)}], oldCapture=[{captured}], originalWindow=[{WindowDescription(original)}], currentWindow=[{WindowDescription(current)}], commandRetryWindow=[{WindowDescription(DiagnosticValue(command, "RetryWindow") as BlockPublicationWindow)}], tailBeforeProof=[{_tailBeforeProof}], tailAfterProof=[{_tailAfterProof}]";
            }
            catch (Exception exception) { return "command=" + name + ", diagnosticReadFailed=" + exception.GetType().Name; }
        }
        object PureBackgroundPending()
        {
            var pending = Read<object>(_terrain, "_pending");
            if (pending == null || !Read<bool>(pending, "PureAuthorityBackground") || Read<bool>(pending, "Remote")
                || Read<bool>(pending, "PrivateGravity") || Read<object>(pending, "BuildGuard") != null
                || Read<object>(pending, "RecoveryGuard") != null || Read<Vector3Int[]>(pending, "Changed").Length == 0) return null;
            if (((IEnumerable)Read<object>(pending, "Results")).Cast<object>().Any()) return null;
            var checkpoints = Read<object>(pending, "CommandCheckpoints");
            if (checkpoints != null && ((IEnumerable)checkpoints).Cast<object>().Any()) return null;
            return TowerFallingCells(Read<BlockWorld>(pending, "Next")) > TowerFallingCells(_terrain.World) ? pending : null;
        }
        object BackgroundPendingWithNativeSupportWitness()
        {
            var pending = PureBackgroundPending();
            if (pending == null || !ReferenceEquals(Read<object>(pending, "FairSeed"), _seed)) return null;
            var journal = Read<object>(pending, "Gravity");
            if (journal == null || !Read<bool>(journal, "RetainSupportSnapshot")) return null;
            var snapshot = Read<object>(journal, "SupportSnapshot");
            if (snapshot == null) return null;
            return Read<Array>(snapshot, "Bounds").Cast<object>()
                .Any(bound => ReferenceEquals(Read<Collider>(bound, "Collider"), _far)) ? pending : null;
        }
        void AssertExactTower(bool destroyed)
        {
            var entries = new List<(int y, byte owner)>();
            for (int y = 0; y < 320; y++)
                if (_terrain.World.TryGet(Tower + Vector3Int.up * y, out var cell)) entries.Add((y, cell.Owner));
            foreach (var run in _terrain.World.Falling.Values.Where(r => AtTower(r.Bottom)))
                for (int i = 0; i < run.Cells.Length; i++)
                    if (run.Cells[i].Occupied) entries.Add((run.Bottom.y + i, run.Cells[i].Owner));
            var expected = Enumerable.Range(1, 319).Where(y => !destroyed || y < 158 || y > 162).ToArray();
            CollectionAssert.AreEqual(expected, entries.OrderBy(e => e.y).Select(e => e.y).ToArray(),
                "Every surviving original source address occurs exactly once; deleted rows must be exactly the frozen five.");
            Assert.That(entries.All(e => e.owner == 2), Is.True);
            foreach (int y in expected) Assert.That(CollisionAt(Tower + Vector3Int.up * y), Is.False);
        }
        void AssertExactBuild(Vector3Int first, int count)
        {
            for (int y = 0; y < count; y++)
            {
                Assert.That(_terrain.World.TryGet(first + Vector3Int.up * y, out var cell), Is.True);
                Assert.That(cell.Owner, Is.EqualTo(1));
            }
            Assert.That(CollisionAt(first + Vector3Int.up * (count - 1)), Is.True);
        }
        void OnMutation(BlockMutationResult result, double _) { _mutationEvents++; }
        BoxCollider MakeBox(string name, Vector3 position, Vector3 size)
        {
            var item = new GameObject(name); item.transform.SetParent(_root.transform, false); item.transform.position = position;
            var collider = item.AddComponent<BoxCollider>(); collider.size = size;
            Read<HashSet<Collider>>(_terrain, "_staticColliders").Add(collider); return collider;
        }
        void ReleaseWorkers() { while (_held > 0) { _workers.Release(); _held--; } }
        object LastQueued() => ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Last();
        static Vector3 GroundPoint(Vector3Int cell) { var point = BlockWorld.Center(cell); point.y = 0; return point; }
        static bool AtTower(Vector3Int cell) => cell.x == Tower.x && cell.z == Tower.z;
        static int StableTowerCells(BlockWorld world) => Enumerable.Range(0, 320).Count(y => world.TryGet(Tower + Vector3Int.up * y, out _));
        static int TowerFallingCells(BlockWorld world) => world.Falling.Values.Where(r => AtTower(r.Bottom)).Sum(r => r.Cells.Count(c => c.Occupied));
        static int TowerCells(BlockWorld world) => StableTowerCells(world) + TowerFallingCells(world);
        static bool CollisionAt(Vector3Int cell) => Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .2f,
            Vector3.down, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);
        static T Read<T>(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null, "Missing owner for " + name);
            var field = owner.GetType().GetField(name, Fields); Assert.That(field, Is.Not.Null, owner.GetType().Name + "." + name);
            return (T)field.GetValue(owner);
        }
        static T ReadStatic<T>(Type owner, string name)
        {
            var field = owner.GetField(name, StaticFields); Assert.That(field, Is.Not.Null, owner.Name + "." + name);
            return (T)field.GetValue(null);
        }
    }
}
#endif
