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
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Reports-only prepared contracts. Not compiled or executed.
    // Reflection reads production objects only. Every admission/task/mesh is
    // created by public terrain commands and the original production pipeline.
    // Serial: this fixture temporarily owns the original static Workers permits.
    public sealed class BlockWorkerTerrainAdmissionTests
    {
        const int Frames = 3000;
        const double Epoch = 10, InversionAt = 10.25;
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly Vector3Int OldCell = new(2, 0, 2), AddedCell = new(6, 1, 6);
        GameObject _root;
        BlockTerrain _terrain;
        SemaphoreSlim _workers;
        BlockWorkerQueue _admissions;
        RawPermits _raw;
        int _publications, _callbacks;
        BlockMutationResult _result;
        readonly List<BlockTerrain.PendingWallDiagnostic> _terminals = new();
        readonly HashSet<object> _observedPending = new();
        readonly HashSet<Task> _observedCommandTasks = new();
        readonly HashSet<Mesh> _observedMeshes = new();
        readonly HashSet<GameObject> _observedViews = new();
        bool _tearingDown;

        sealed class Capture
        {
            public object Pending;
            public BlockWorld Source;
            public byte[] SourceBytes;
            public Task Generate, Scheduled;
            public Task[] Generating;
            public CancellationTokenSource Stop;
            public CancellationToken Token;
            public long WallId;
            public readonly List<Task> Bakes = new();
            public readonly List<Mesh> Meshes = new();
            public readonly List<GameObject> Views = new();
        }

        // Each entry corresponds to an ACTUAL original SemaphoreSlim.WaitAsync.
        // No queue lease, synthetic Task or native-work marker is supplied.
        sealed class RawPermits
        {
            sealed class Owned { public Task Acquire; public bool Released; }
            readonly SemaphoreSlim _physical;
            readonly CancellationTokenSource _stop = new();
            readonly List<Owned> _owned = new();
            bool _cancelled, _disposed;
            public RawPermits(SemaphoreSlim physical) { _physical = physical; }
            public Task Request()
            {
                var entry = new Owned { Acquire = _physical.WaitAsync(_stop.Token) };
                _owned.Add(entry); return entry.Acquire;
            }
            public int Held => _owned.Count(e => !e.Released && e.Acquire.Status == TaskStatus.RanToCompletion);
            public bool Joined => _owned.All(e => e.Acquire.IsCompleted);
            public void ReleaseOne()
            {
                var entry = _owned.First(e => !e.Released && e.Acquire.Status == TaskStatus.RanToCompletion);
                entry.Released = true; _physical.Release();
            }
            public void ReleaseCompleted()
            {
                foreach (var entry in _owned)
                {
                    if (entry.Acquire.IsFaulted) _ = entry.Acquire.Exception;
                    if (entry.Released || entry.Acquire.Status != TaskStatus.RanToCompletion) continue;
                    entry.Released = true; _physical.Release();
                }
            }
            public void CancelAndReleaseCompleted()
            {
                if (!_cancelled) { _cancelled = true; _stop.Cancel(); }
                ReleaseCompleted();
            }
            public void FinishCleanup()
            {
                CancelAndReleaseCompleted();
                Assert.That(Joined, Is.True, "Every actual raw acquisition must join before its CTS is disposed.");
                Assert.That(Held, Is.Zero);
                if (!_disposed) { _disposed = true; _stop.Dispose(); }
            }
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _tearingDown = false;
            _workers = Static<SemaphoreSlim>("Workers");
            _admissions = Static<BlockWorkerQueue>("WorkerAdmissions");
            Assert.That(Read<SemaphoreSlim>(_admissions, "_physical"), Is.SameAs(_workers));
            yield return Await(() => _admissions.WhenIdle.IsCompleted && _workers.CurrentCount == 2,
                "Prior production workers must finish before serial permit ownership begins.");
            _raw = new RawPermits(_workers);
            _root = new GameObject("Real terrain worker-admission lifetime contracts");
            var layout = new GameObject("Inactive arena for public map collider registration");
            layout.transform.SetParent(_root.transform, false); layout.SetActive(false);
            var arena = layout.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(32, 64); arena.SpawnPoints = Array.Empty<Transform>();
            var floor = new GameObject("Real native floor with top zero");
            floor.transform.SetParent(layout.transform, false); floor.transform.position = new Vector3(0, -.1f, 0);
            var box = floor.AddComponent<BoxCollider>(); box.size = new Vector3(32, .2f, 64);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena);
            floor.transform.SetParent(_root.transform, true); Physics.SyncTransforms();
            Assert.That(box.bounds.max.y, Is.Zero.Within(.000001));
            _terrain.PublishedCommit += OnPublication;
            _terrain.PendingTransactionFinished += OnTerminal;
            var seed = new BlockWorld(arena.Dimensions, _terrain.Round);
            Assert.That(seed.Set(OldCell, BlockCell.Create(2, true)), Is.True);
            seed.Seal(1, seed.Chunks.Keys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return Pump(false, Epoch, () => _terrain.PendingTransactions == 0 && _terrain.BlueCount == 1,
                "Initial public snapshot must really publish native collision.");
            Assert.That(NativeTop(OldCell), Is.Not.Null);
            _callbacks = _publications = 0; _result = default; _terminals.Clear();
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            // Keep the actual managed Terrain and every observed Pending across
            // Unity's fake-null destruction. Queue idle is NOT a Task-group or
            // async OnDestroy completion gate.
            _tearingDown = true;
            var terrain = _terrain;
            RememberActualPending(terrain);
            _raw?.CancelAndReleaseCompleted();
            if (_root != null) Object.DestroyImmediate(_root);
            for (int frame = 0; frame < Frames && !FailureCleanupJoined(terrain); frame++)
            { _raw?.CancelAndReleaseCompleted(); yield return null; }
            Assert.That(FailureCleanupJoined(terrain), Is.True,
                "Failure teardown must join actual Generate, scheduled/member and Bake Tasks, CTS disposal, and complete retired cleanup before exiting Play Mode.");
            // Unity destruction is deferred even after native tasks have joined.
            // Retain actual piece references through this independent gate.
            yield return Await(PendingResourcesDestroyed,
                "Every actually observed Pending Mesh/View must be destroyed after its joined retirement.");
            ObserveJoinedPendingFaults();
            _raw?.FinishCleanup();
            if (_admissions != null) Assert.That(_admissions.WhenIdle.IsCompleted, Is.True);
            if (_workers != null) Assert.That(_workers.CurrentCount, Is.EqualTo(2), "Return exactly the original two permits.");
            _observedPending.Clear(); _observedCommandTasks.Clear(); _observedMeshes.Clear(); _observedViews.Clear();
            _raw = null; _terrain = null; _root = null;
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
            if (_raw != null && !_raw.Joined || _admissions != null && !_admissions.WhenIdle.IsCompleted) return false;
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

        [UnityTest, Timeout(600000)] public IEnumerator PublicBuildWaitingGenerationClear()
            => FinallyRelease(WaitingGeneration(true, false));
        [UnityTest, Timeout(600000)] public IEnumerator PublicBuildWaitingGenerationDestroy()
            => FinallyRelease(WaitingGeneration(true, true));
        [UnityTest, Timeout(600000)] public IEnumerator PublicSnapshotWaitingGenerationClear()
            => FinallyRelease(WaitingGeneration(false, false));
        [UnityTest, Timeout(600000)] public IEnumerator PublicSnapshotWaitingGenerationDestroy()
            => FinallyRelease(WaitingGeneration(false, true));
        [UnityTest, Timeout(600000)] public IEnumerator RealUploadWaitingBakeClear()
            => FinallyRelease(WaitingBake(false));
        [UnityTest, Timeout(600000)] public IEnumerator RealUploadWaitingBakeDestroy()
            => FinallyRelease(WaitingBake(true));
        [UnityTest, Timeout(600000)] public IEnumerator NaturalGuardedBuildAuthorityLossCancelsWaitingGeneration()
            => FinallyRelease(GuardedAuthorityLoss());

        IEnumerator FinallyRelease(IEnumerator body)
        {
            try { while (body.MoveNext()) yield return body.Current; }
            finally
            {
                try { (body as IDisposable)?.Dispose(); }
                finally { _raw?.CancelAndReleaseCompleted(); }
            }
        }
        IEnumerator HoldBoth()
        {
            var first = _raw.Request(); var second = _raw.Request();
            yield return Await(() => first.IsCompleted && second.IsCompleted, "Own both real physical permits.");
            Assert.That(first.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(second.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(_raw.Held, Is.EqualTo(2)); Assert.That(_workers.CurrentCount, Is.Zero);
        }
        void QueueSnapshot()
        {
            var next = _terrain.World.Clone();
            Assert.That(next.Set(AddedCell, BlockCell.Create(1, true)), Is.True);
            Assert.That(next.Chunks.Count, Is.EqualTo(1), "Single actual generation gives the raw reacquisition a complete barrier.");
            next.Seal(_terrain.Revision + 1, next.Chunks.Keys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(next.EncodeSnapshot()), Is.True);
        }
        void QueueBuild(Vector3Int cell)
            => _terrain.QueueBuild(BlockWorld.Center(cell) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                result => { _callbacks++; _result = result; });
        IEnumerator WaitingGeneration(bool build, bool destroy)
        {
            var prior = NativeTop(OldCell); var source = _terrain.World; var bytes = source.EncodeSnapshot();
            yield return HoldBoth();
            if (build) QueueBuild(new Vector3Int(6, 0, 6)); else QueueSnapshot();
            Capture captured = null;
            yield return Pump(build, Epoch, () => TryCaptureWaitingGeneration(out captured), "Observe actual queued generation.");
            Assert.That(captured.Source, Is.SameAs(source));
            AssertUnpublished(captured, prior, OldCell); Assert.That(_callbacks, Is.Zero);
            Assert.That(Read<Array>(captured.Pending, "Pieces").Cast<object>().All(p => p == null), Is.True);
            Assert.That(Read<int>(captured.Pending, "AdmissionsDisposed"), Is.Zero);
            Cancel(destroy);
            Assert.That(captured.Token.IsCancellationRequested, Is.True);
            Assert.That(build ? _callbacks : 0, Is.EqualTo(build ? 1 : 0));
            Assert.That(_result.Changed, Is.Zero);
            if (!destroy)
            {
                Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
                Assert.That(_terrain.Round, Is.EqualTo(source.Round + 1));
                Assert.That(NativeTop(OldCell), Is.Null);
            }
            AssertRetained(bytes, source);
            yield return JoinRetirement(captured, false);
            Assert.That(captured.Generate.IsCanceled, Is.True);
            Assert.That(captured.Generating.All(t => t.IsCanceled), Is.True);
            Assert.That(captured.Scheduled.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            AssertLogicalIdleWhileRawHeld(); AssertTerminalOnce(captured);
            for (int frame = 0; frame < 4; frame++) yield return null;
            Assert.That(_callbacks, Is.EqualTo(build ? 1 : 0)); Assert.That(_publications, Is.Zero);
            if (!destroy) yield return ReleaseAndProveFutureWork();
        }
        IEnumerator WaitingBake(bool destroy)
        {
            var prior = NativeTop(OldCell);
            yield return HoldBoth(); QueueSnapshot();
            Capture captured = null;
            yield return Pump(false, Epoch, () => TryCaptureWaitingGeneration(out captured), "Observe single real queued generation.");
            Assert.That(captured.Generating.Length, Is.EqualTo(1));
            // No yield between release and reacquisition. The production queue's
            // already waiting dispatcher receives the released permit first.
            // This real raw waiter is then ahead of any future main-thread Bake.
            _raw.ReleaseOne(); var reacquire = _raw.Request();
            yield return Await(() => reacquire.IsCompleted && captured.Generate.IsCompleted, "Real generation returns its permit to the raw waiter.");
            Assert.That(reacquire.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(captured.Generate.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(_raw.Held, Is.EqualTo(2));
            yield return Await(() => CaptureActualWaitingBake(captured), "Production Update must upload and enqueue a real native Bake task.");
            AssertUnpublished(captured, prior, OldCell);
            Assert.That(captured.Bakes.Count, Is.EqualTo(1));
            Assert.That(captured.Meshes.All(mesh => mesh != null && mesh.vertexCount > 0), Is.True);
            Assert.That(captured.Views.All(view => view != null && !view.activeInHierarchy), Is.True);
            Assert.That(NativeTop(AddedCell), Is.Null);
            var source = captured.Source; var bytes = captured.SourceBytes;
            Cancel(destroy);
            Assert.That(captured.Token.IsCancellationRequested, Is.True);
            // Only conditional unfinished-task ownership is asserted. Cancellation
            // may finish before this main-thread observation; no native entry is inferred.
            if (captured.Bakes.Any(task => !task.IsCompleted))
            {
                Assert.That(captured.Meshes.All(mesh => mesh != null), Is.True);
                Assert.That(Read<int>(captured.Pending, "AdmissionsDisposed"), Is.Zero);
            }
            yield return JoinRetirement(captured, true);
            Assert.That(captured.Bakes.All(task => task.IsCanceled), Is.True);
            Assert.That(captured.Generate.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            AssertLogicalIdleWhileRawHeld(); AssertTerminalOnce(captured); AssertRetained(bytes, source);
            yield return Await(() => captured.Meshes.All(mesh => mesh == null) && captured.Views.All(view => view == null),
                "Deferred Unity destruction must release every actually uploaded mesh and view after task join.");
            Assert.That(_callbacks, Is.Zero); Assert.That(_publications, Is.Zero);
            if (!destroy) yield return ReleaseAndProveFutureWork();
        }
        IEnumerator GuardedAuthorityLoss()
        {
            _terrain.Clear(_terrain.Round + 1);
            int built = 0, removed = 0;
            _terrain.QueueBuild(BlockWorld.Center(OldCell) - Vector3.up * .051f, Vector3.up, 2, 6, 0, false,
                result => { built++; if (!_tearingDown) Assert.That(result.Added, Is.EqualTo(6)); });
            yield return Pump(true, Epoch, () => built == 1 && _terrain.PendingTransactions == 0, "Public six-cell column.");
            _terrain.QueueDestroy(new Vector3(BlockWorld.Center(OldCell).x, .3f, BlockWorld.Center(OldCell).z), .1001f, 0, 1, true,
                result => { removed++; if (!_tearingDown) Assert.That(result.RemovedEnemy, Is.EqualTo(4)); }, 4, 0);
            yield return Pump(true, Epoch, () => removed == 1 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => c.Bottom == OldCell + Vector3Int.up * 5 && c.Phase == FallingCohortPhase.Falling),
                "Natural moving old body; no supplied falling state.");
            var cohort = _terrain.World.Cohorts.Values.Single(c => c.Bottom == OldCell + Vector3Int.up * 5);
            Assert.That(cohort.StartedAt, Is.EqualTo(Epoch));
            Assert.That(cohort.LandingOffset, Is.EqualTo(.4f).Within(.000001));
            float oldBottom = .5f - cohort.Offset(InversionAt);
            Assert.That(.3f, Is.GreaterThan(oldBottom + .1f + .000001f));
            Assert.That(cohort.Offset(InversionAt), Is.LessThan(cohort.LandingOffset));
            _publications = _callbacks = 0; _result = default; _terminals.Clear();
            long starts = _terrain.BuildOccupancyPreparationStarts;
            QueueBuild(OldCell + Vector3Int.up * 3);
            object preparation = null;
            yield return Pump(true, InversionAt, () => (preparation = Read<object>(_terrain, "_buildOccupancyPreparation")) != null,
                "Public build must naturally create a private occupancy preparation before worker generation.");
            Assert.That(_terrain.BuildOccupancyPreparationStarts, Is.EqualTo(starts + 1));
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
            Assert.That(_admissions.WhenIdle.IsCompleted, Is.True);
            // Earlier legitimate gravity commits are outside this admission
            // lifetime witness. The guarded build callback must still be pending.
            Assert.That(_callbacks, Is.Zero); _publications = 0; _terminals.Clear();
            yield return HoldBoth(); // No Tick/command staging occurs during these acquisition yields.
            Capture captured = null;
            yield return Pump(true, InversionAt, () => TryCaptureWaitingGeneration(out captured) && Read<object>(captured.Pending, "BuildGuard") != null,
                "Actual BuildGuard pending must queue real generation behind both raw holders.");
            Assert.That(Read<object>(captured.Pending, "BuildGuard"), Is.SameAs(preparation));
            Assert.That(Read<BlockWorld>(preparation, "Source"), Is.SameAs(captured.Source));
            var prior = NativeTop(OldCell); AssertUnpublished(captured, prior, OldCell);
            long retries = _terrain.BuildPublicationRetries;
            _terrain.Tick(false, InversionAt, .016f);
            Assert.That(captured.Token.IsCancellationRequested, Is.True, "Corrected authority-loss path cancels actual waiting admissions.");
            Assert.That(_terrain.BuildPublicationRetries, Is.EqualTo(retries + 1));
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
            Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
            Assert.That(_terrain.World, Is.SameAs(captured.Source));
            Assert.That(NativeTop(OldCell), Is.SameAs(prior)); Assert.That(_callbacks, Is.Zero);
            yield return JoinRetirement(captured, false);
            Assert.That(captured.Generate.IsCanceled, Is.True); Assert.That(captured.Generating.All(task => task.IsCanceled), Is.True);
            AssertLogicalIdleWhileRawHeld(); AssertTerminalOnce(captured);
            AssertRetained(captured.SourceBytes, captured.Source); Assert.That(_publications, Is.Zero);
            _terrain.Clear(_terrain.Round + 1); // Restored public command is completed exactly once by the public reset.
            Assert.That(_callbacks, Is.EqualTo(1)); Assert.That(_result.Changed, Is.Zero);
            for (int frame = 0; frame < 4; frame++) yield return null;
            Assert.That(_callbacks, Is.EqualTo(1));
            yield return ReleaseAndProveFutureWork();
        }
        void Cancel(bool destroy)
        {
            if (destroy) { Object.DestroyImmediate(_root); _root = null; }
            else _terrain.Clear(_terrain.Round + 1);
        }
        IEnumerator JoinRetirement(Capture captured, bool baked)
        {
            // Reads the managed terrain reference even after Unity OnDestroy.
            // No private Update/CleanupRetired method is invoked.
            yield return Await(() => captured.Generate.IsCompleted && captured.Bakes.All(task => task.IsCompleted)
                && Read<int>(captured.Pending, "AdmissionsDisposed") == 1 && !RetiredContains(captured.Pending)
                && _admissions.WhenIdle.IsCompleted,
                "Actual Generate/Bake joins must precede final Pending resource disposal.");
            Assert.That(captured.Scheduled.IsCompleted, Is.True);
            Assert.That(captured.Generating.All(task => task.IsCompleted), Is.True);
            Assert.That(captured.Generate.IsFaulted, Is.False);
            Assert.That(captured.Bakes.All(task => !task.IsFaulted), Is.True);
            Assert.That(Read<int>(captured.Pending, "AdmissionsDisposed"), Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => { _ = captured.Stop.Token; });
            Assert.That(baked ? captured.Bakes.Count : 0, Is.EqualTo(baked ? 1 : 0));
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
        }
        IEnumerator ReleaseAndProveFutureWork()
        {
            _raw.ReleaseCompleted();
            Assert.That(_raw.Held, Is.Zero); Assert.That(_workers.CurrentCount, Is.EqualTo(2));
            var next = new BlockWorld(_terrain.World.Dimensions, _terrain.Round);
            Assert.That(next.Set(AddedCell, BlockCell.Create(1, true)), Is.True);
            next.Seal(1, next.Chunks.Keys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(next.EncodeSnapshot()), Is.True);
            yield return Pump(false, Epoch + 1, () => _terrain.PendingTransactions == 0 && _terrain.PinkCount == 1,
                "Following real terrain work must acquire the same original permits and publish.");
            Assert.That(_terrain.BlueCount, Is.Zero); Assert.That(NativeTop(AddedCell), Is.Not.Null);
            Assert.That(_publications, Is.EqualTo(1));
            yield return Await(() => _admissions.WhenIdle.IsCompleted, "Future actual Bake releases its lease.");
            Assert.That(_workers.CurrentCount, Is.EqualTo(2));
        }
        bool TryCaptureWaitingGeneration(out Capture captured)
        {
            captured = null; var pending = Read<object>(_terrain, "_pending");
            if (pending == null) return false;
            var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task");
            if (scheduled.Status != TaskStatus.RanToCompletion) return false;
            var tasks = Property<Task[]>(scheduled, "Result");
            if (tasks.Length == 0 || tasks.Any(task => task.IsCompleted)) return false;
            var generate = Read<Task>(pending, "Generate");
            if (generate.IsCompleted || QueueWaiting() != tasks.Length) return false;
            Assert.That(Read<bool>(pending, "ForegroundWork"), Is.True);
            Assert.That(Read<int>(pending, "Uploaded"), Is.Zero);
            var stop = Read<CancellationTokenSource>(pending, "AdmissionStop");
            captured = new Capture { Pending = pending, Source = _terrain.World, SourceBytes = _terrain.ExportSnapshot(),
                Generate = generate, Scheduled = scheduled, Generating = tasks, Stop = stop, Token = stop.Token,
                WallId = Read<long>(pending, "WallId") };
            _observedPending.Add(pending);
            return true;
        }
        bool CaptureActualWaitingBake(Capture captured)
        {
            Assert.That(Read<object>(_terrain, "_pending"), Is.SameAs(captured.Pending));
            if (captured.Bakes.Count != 0) return true; // Observation predicates may be evaluated more than once.
            var pieces = Read<Array>(captured.Pending, "Pieces");
            if (Read<int>(captured.Pending, "Uploaded") != 1 || pieces.Length != 1 || pieces.GetValue(0) == null) return false;
            var piece = pieces.GetValue(0); var bake = Read<Task>(piece, "Bake");
            if (bake == null || bake.IsCompleted || QueueWaiting() != 1) return false;
            Assert.That(Read<long>(piece, "UploadStartStamp"), Is.GreaterThan(0));
            Assert.That(Read<long>(piece, "UploadEndStamp"), Is.GreaterThanOrEqualTo(Read<long>(piece, "UploadStartStamp")));
            Assert.That(Read<long>(piece, "BakeStartStamp"), Is.Zero);
            Assert.That(Read<long>(piece, "BakeEndStamp"), Is.Zero);
            var mesh = Read<Mesh>(piece, "CollisionMesh"); Assert.That(mesh, Is.Not.Null);
            var view = Read<object>(piece, "View"); Assert.That(view, Is.Not.Null);
            captured.Bakes.Add(bake); captured.Meshes.Add(mesh);
            var render = Read<Mesh>(piece, "RenderMesh"); if (render != null) captured.Meshes.Add(render);
            captured.Views.Add(Read<GameObject>(view, "Object"));
            _observedMeshes.UnionWith(captured.Meshes); _observedViews.UnionWith(captured.Views);
            var collider = Read<MeshCollider>(view, "Collider");
            Assert.That(collider.sharedMesh, Is.Null, "Real pending view cannot expose its collision before atomic publication.");
            return true;
        }
        void AssertUnpublished(Capture captured, Collider prior, Vector3Int oldCell)
        {
            Assert.That(_terrain.World, Is.SameAs(captured.Source));
            AssertRetained(captured.SourceBytes, captured.Source);
            Assert.That(NativeTop(oldCell), Is.SameAs(prior));
            Assert.That(prior, Is.Not.Null); Assert.That(prior.enabled && prior.gameObject.activeInHierarchy, Is.True);
            Assert.That(_publications, Is.Zero);
        }
        void AssertLogicalIdleWhileRawHeld()
        {
            Assert.That(_raw.Held, Is.EqualTo(2)); Assert.That(_workers.CurrentCount, Is.Zero);
            Assert.That(_admissions.WhenIdle.IsCompleted, Is.True,
                "Terrain cancellation must retire the queue's dispatcher even while external permits remain held.");
            Assert.That(QueueWaiting(), Is.Zero);
            lock (Read<object>(_admissions, "_gate")) Assert.That(Read<int>(_admissions, "_leased"), Is.Zero);
        }
        void AssertTerminalOnce(Capture captured)
        {
            var samples = _terminals.Where(sample => sample.Id == captured.WallId).ToArray();
            Assert.That(captured.WallId, Is.GreaterThan(0)); Assert.That(samples.Length, Is.EqualTo(1));
            Assert.That(samples[0].Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Aborted));
            Assert.That(samples[0].PublishEntered, Is.False);
        }
        int QueueWaiting()
        {
            lock (Read<object>(_admissions, "_gate"))
                return Property<int>(Read<object>(_admissions, "_foreground"), "Count")
                    + Property<int>(Read<object>(_admissions, "_background"), "Count");
        }
        bool RetiredContains(object pending)
            => ((IEnumerable)Read<object>(_terrain, "_retired")).Cast<object>().Any(entry => ReferenceEquals(Read<object>(entry, "Pending"), pending));
        static void AssertRetained(byte[] bytes, BlockWorld source) => CollectionAssert.AreEqual(bytes, source.EncodeSnapshot());
        static Collider NativeTop(Vector3Int cell)
        {
            var point = BlockWorld.Center(cell); float top = (cell.y + 1) * BlockWorld.CellSize;
            return Physics.RaycastAll(new Vector3(point.x, top + .02f, point.z), Vector3.down, .04f,
                Physics.AllLayers, QueryTriggerInteraction.Ignore).Where(hit => hit.collider is MeshCollider
                    && Mathf.Abs(hit.point.y - top) < .001f).Select(hit => hit.collider).FirstOrDefault();
        }
        IEnumerator Pump(bool authority, double now, Func<bool> ready, string reason)
        {
            for (int frame = 0; frame < Frames && !ready(); frame++)
            { _terrain.Tick(authority, now, .016f); if (ready()) break; yield return null; }
            Assert.That(ready(), Is.True, reason + " Missing natural witness fails; it is not skipped or fabricated.");
        }
        static IEnumerator Await(Func<bool> ready, string reason)
        {
            for (int frame = 0; frame < Frames && !ready(); frame++) yield return null;
            Assert.That(ready(), Is.True, reason);
        }
        void OnPublication(BlockTerrainCommit commit) { _publications++; }
        void OnTerminal(BlockTerrain.PendingWallDiagnostic sample) { _terminals.Add(sample); }
        static T Static<T>(string name)
        {
            var field = typeof(BlockTerrain).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, name); return (T)field.GetValue(null);
        }
        static T Read<T>(object target, string name)
        {
            Assert.That(target, Is.Not.Null, name); var field = target.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, name); return (T)field.GetValue(target);
        }
        static T Property<T>(object target, string name)
        {
            Assert.That(target, Is.Not.Null, name); var property = target.GetType().GetProperty(name);
            Assert.That(property, Is.Not.Null, name); return (T)property.GetValue(target);
        }
    }
}
#endif
