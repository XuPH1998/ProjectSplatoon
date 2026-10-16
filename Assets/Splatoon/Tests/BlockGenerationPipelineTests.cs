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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Scheduling/lifetime tests use real original geometry, Upload, Bake and Publish.
    // Reflection holds completion boundaries; these are not performance measurements.
    // Run serially: the original two-permit Workers semaphore is deliberately held.
    public sealed class BlockGenerationPipelineTests
    {
        GameObject _object;
        BlockTerrain _terrain;
        readonly List<PipelineHeldGroup> _groups = new();
        readonly List<BlockTerrain.PendingWallDiagnostic> _samples = new();
        static readonly Vector3Int[] Cells = { new(5, 1, 5), new(21, 1, 5), new(37, 1, 5) };

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _object = new GameObject("Real piece pipeline regression");
            _terrain = _object.AddComponent<BlockTerrain>(); _terrain.Initialize(null); _terrain.enabled = false;
            _samples.Clear(); _groups.Clear(); _terrain.PendingTransactionFinished += Observe;
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            // Unblock both real generation handoffs and all real semaphore waits first.
            foreach (var group in _groups) group.ReleasePermitsAndCompleteRemaining();
            foreach (var group in _groups)
            {
                yield return Wait(() => group.Generate.IsCompleted, "all started generation tasks joined in cleanup");
                foreach (var bake in group.StartedBakes())
                    yield return Wait(() => bake.IsCompleted, "all started real Bake tasks joined in cleanup");
            }
            if (_terrain != null) _terrain.PendingTransactionFinished -= Observe;
            if (_object != null) Object.DestroyImmediate(_object);
            yield return null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Observe(BlockTerrain.PendingWallDiagnostic sample) => _samples.Add(sample);
        static IEnumerator Wait(Func<bool> predicate, string reason)
        {
            for (int i = 0; i < 1000 && !predicate(); i++) yield return null;
            Assert.That(predicate(), Is.True, reason);
        }
        IEnumerator Start(int count, Action<PipelineHeldGroup> receive, bool headless = false)
        {
            var next = _terrain.World.Clone();
            var changed = new HashSet<Vector3Int>();
            for (int i = 0; i < count; i++)
            { next.Set(Cells[i], BlockCell.Create((byte)(i % 2 + 1), true)); changed.Add(BlockWorld.ChunkAt(Cells[i])); }
            next.Seal(_terrain.Revision + 1, changed);
            PipelineReflection.Call(_terrain, "BeginTransaction", next, changed, true, null);
            object pending = PipelineReflection.Get(_terrain, "_pending");
            var original = (Task)PipelineReflection.Get(pending, "Generate");
            yield return Wait(() => original.IsCompleted, "original real generation completes before boundary injection");
            Assert.That(original.Status, Is.EqualTo(TaskStatus.RanToCompletion), original.Exception?.ToString());
            Array pieces = (Array)original.GetType().GetProperty("Result").GetValue(original);
            Assert.That(pieces.Length, Is.EqualTo(count));
            Assert.That((int)PipelineReflection.Get(pending, "Uploaded"), Is.Zero, "disabled component has uploaded nothing");
            if (headless)
            {
                // Explicitly exercise the production headless builder even with an Editor graphics device.
                for (int i = 0; i < count; i++)
                {
                    var key = (Vector3Int)PipelineReflection.Get(pieces.GetValue(i), "Key");
                    long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    pieces.SetValue(PipelineReflection.CallStatic("PrepareGenerationPiece", next, key, false, true, stamp, stamp), i);
                }
            }
            var group = new PipelineHeldGroup(_terrain, pending, pieces); _groups.Add(group); receive(group);
        }
        IEnumerator UploadOne(PipelineHeldGroup group)
        {
            for (int i = 0; i < 1000 && group.Uploaded == 0; i++) { group.Advance(false); yield return null; }
            Assert.That(group.Uploaded, Is.EqualTo(1));
        }
        IEnumerator Ready(PipelineHeldGroup group)
        {
            for (int i = 0; i < 1000 && !group.Ready; i++) { group.Advance(false); yield return null; }
            Assert.That(group.Ready, Is.True, "whole group generation/upload/Bake must succeed");
        }
        void AssertUnpublished(BlockWorld before)
        {
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(_terrain.Revision, Is.Zero);
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.Zero);
            Assert.That(_samples, Is.Empty);
            foreach (var cell in Cells)
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down, .6f,
                    1 << BlockTerrain.TerrainLayer), Is.False, "prepared mesh cannot supply published collision");
        }
        static void ExpectFailure() => LogAssert.Expect(LogType.Warning,
            new System.Text.RegularExpressions.Regex("\\[BLOCK\\] Atomic terrain transaction rejected:"));

        [UnityTest] public IEnumerator LaterReadyPieceUploadsAndStartsBakeBeforeFirstGenerationWithoutPartialPublish()
        {
            PipelineHeldGroup group = null; yield return Start(2, value => group = value);
            var before = _terrain.World; group.HoldBothPermits(); group.Complete(1);
            yield return UploadOne(group);
            Assert.That(group.Generate.IsCompleted, Is.False); Assert.That(group.Installed.GetValue(0), Is.Null);
            Assert.That(group.Installed.GetValue(1), Is.SameAs(group.Generated.GetValue(1)));
            var piece = group.Generated.GetValue(1); var bake = (Task)PipelineReflection.Get(piece, "Bake");
            Assert.That(bake, Is.Not.Null); Assert.That(bake.IsCompleted, Is.False);
            var view = PipelineReflection.Get(piece, "View");
            Assert.That(((GameObject)PipelineReflection.Get(view, "Object")).activeSelf, Is.False);
            Assert.That(((MeshCollider)PipelineReflection.Get(view, "Collider")).sharedMesh, Is.Null);
            AssertUnpublished(before);
            group.ReleasePermits(); yield return Wait(() => bake.IsCompleted, "the early real Bake completes");
            Assert.That(bake.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            group.Advance(true); AssertUnpublished(before); // group generation is still held
            group.Complete(0); yield return Ready(group); AssertUnpublished(before);
            yield return null; group.Advance(true);
            Assert.That(_terrain.World, Is.SameAs(group.Next)); Assert.That(_terrain.Revision, Is.EqualTo(1));
            Assert.That(_terrain.World.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.World.BlueCount, Is.EqualTo(1));
            Assert.That(_samples.Count, Is.EqualTo(1));
            foreach (var cell in Cells.Take(2))
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down, out var hit,
                    .6f, 1 << BlockTerrain.TerrainLayer) && _terrain.TryContact(hit, out var contact)
                    && contact.Cell == cell && contact.Stable && contact.Revision == 1, Is.True);
            var sample = _samples.Single();
            Assert.That(sample.Pipeline.Available && sample.Pipeline.UploadedBeforeGroupCompletion, Is.True);
            Assert.That(sample.Pipeline.FirstUploadFromCreatedMs, Is.GreaterThan(0));
            Assert.That(sample.Pipeline.UploadPieceActiveElapsedMs, Is.GreaterThan(0));
            Assert.That(sample.Pipeline.BakeActiveElapsedMs, Is.GreaterThan(0));
            Assert.That(sample.Pipeline.UploadGenerationOverlapWindowMs, Is.GreaterThan(0));
            Assert.That(sample.Pipeline.BakeGenerationOverlapWindowMs, Is.GreaterThan(0));
            WorkerObservationAssertions.Partition(sample); // old disjoint wall/worker partition remains exact
        }

        [UnityTest] public IEnumerator GenerationFaultCancelsWaitingBakeAndRetainsMeshesUntilTaskJoin()
        {
            PipelineHeldGroup group = null; yield return Start(2, value => group = value);
            var before = _terrain.World; group.HoldBothPermits(); group.Complete(1); yield return UploadOne(group);
            var mesh = group.CollisionMesh(1); var bake = group.Bake(1);
            yield return Wait(() => group.WaitingBakeAdmission, "the uploaded Bake is waiting for a physical worker");
            group.AssertWaitingBake(1);
            group.Reject(0, false); yield return Wait(() => group.Generate.IsCompleted, "whole fault group joins");
            ExpectFailure(); group.Advance(true);
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(group.Retired, Is.EqualTo(1));
            Assert.That(group.AdmissionCancelled, Is.True); Assert.That(mesh != null, Is.True);
            group.Cleanup();
            if (!bake.IsCompleted)
            { Assert.That(group.Retired, Is.EqualTo(1)); Assert.That(mesh != null, Is.True, "unfinished Bake task retains its native mesh"); }
            Assert.That(_samples.Single().Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            Assert.That(_samples.Single().Pipeline.Available, Is.False, "failed Bake/generation observations expose no overlay");
            yield return Wait(() => bake.IsCompleted, "waiting Bake cancels without acquiring held permits");
            group.AssertCancelledBake(1);
            yield return Wait(() => group.AdmissionsIdle, "waiting admission dispatcher retires without raw permits");
            group.AssertBothPermitsHeld();
            group.Cleanup(); yield return null;
            Assert.That(group.Retired, Is.Zero); Assert.That(mesh == null, Is.True);
            group.ReleasePermits();
        }

        [UnityTest] public IEnumerator PieceCancellationRetainsTheOtherGenerationAndBakeUntilBothActuallyFinish()
        {
            PipelineHeldGroup group = null; yield return Start(3, value => group = value);
            var before = _terrain.World; group.HoldBothPermits(); group.Complete(1); yield return UploadOne(group);
            var mesh = group.CollisionMesh(1); var bake = group.Bake(1);
            yield return Wait(() => group.WaitingBakeAdmission, "the uploaded Bake is waiting for a physical worker");
            group.AssertWaitingBake(1); group.Reject(0, true);
            Assert.That(group.Generate.IsCompleted, Is.False, "third generation is still owned");
            ExpectFailure(); group.Advance(true); group.Cleanup();
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(group.Retired, Is.EqualTo(1));
            Assert.That(group.AdmissionCancelled, Is.True); Assert.That(mesh != null, Is.True);
            yield return Wait(() => bake.IsCompleted, "waiting Bake cancels before the third generation joins");
            group.AssertCancelledBake(1);
            yield return Wait(() => group.AdmissionsIdle, "waiting admission dispatcher retires without raw permits");
            group.AssertBothPermitsHeld();
            group.Cleanup(); Assert.That(group.Retired, Is.EqualTo(1)); Assert.That(mesh != null, Is.True);
            group.Complete(2); yield return Wait(() => group.Generate.IsCompleted, "all canceled-group children joined");
            Assert.That(group.Generate.IsCanceled, Is.True);
            group.Cleanup(); yield return null;
            Assert.That(group.Retired, Is.Zero); Assert.That(mesh == null, Is.True);
            group.ReleasePermits();
        }

        [UnityTest] public IEnumerator ActualOnDestroyJoinsCanceledWaitingBakeEvenWhenGenerationHasFaulted()
        {
            PipelineHeldGroup group = null; yield return Start(2, value => group = value);
            group.HoldBothPermits(); group.Complete(1); yield return UploadOne(group);
            var mesh = group.CollisionMesh(1); var bake = group.Bake(1);
            yield return Wait(() => group.WaitingBakeAdmission, "the uploaded Bake is waiting for a physical worker");
            group.AssertWaitingBake(1);
            group.Reject(0, false); yield return Wait(() => group.Generate.IsCompleted, "generation fails before destruction");
            _terrain.PendingTransactionFinished -= Observe;
            Object.DestroyImmediate(_object); _object = null; _terrain = null;
            Assert.That(group.AdmissionCancelled, Is.True);
            if (!bake.IsCompleted)
            { Assert.That(mesh != null, Is.True, "OnDestroy retains meshes until its independent Bake join"); Assert.That(group.Retired, Is.EqualTo(1)); }
            yield return Wait(() => bake.IsCompleted, "OnDestroy cancels waiting Bake without acquiring held permits");
            group.AssertCancelledBake(1);
            yield return Wait(() => group.AdmissionsIdle, "waiting admission dispatcher retires without raw permits");
            group.AssertBothPermitsHeld();
            yield return Wait(() => mesh == null && group.Retired == 0, "OnDestroy releases only after Bake completion");
            group.ReleasePermits();
        }

        [UnityTest] public IEnumerator HeadlessPreparedPiecesKeepColliderOnlyAndTheWholeGroupPublicationGate()
        {
            PipelineHeldGroup group = null; yield return Start(2, value => group = value, true);
            var before = _terrain.World; group.Complete(1); yield return UploadOne(group);
            var piece = group.Generated.GetValue(1);
            Assert.That(PipelineReflection.Get(piece, "RenderMesh"), Is.Null);
            Assert.That(group.CollisionMesh(1), Is.Not.Null);
            var view = PipelineReflection.Get(piece, "View");
            Assert.That(((GameObject)PipelineReflection.Get(view, "Object")).GetComponent<MeshRenderer>(), Is.Null);
            AssertUnpublished(before); group.Complete(0); yield return Ready(group); AssertUnpublished(before);
            group.Advance(true); Assert.That(_terrain.Revision, Is.EqualTo(1));
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.EqualTo(2));
            foreach (var cell in Cells.Take(2))
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down, out var hit,
                    .6f, 1 << BlockTerrain.TerrainLayer) && _terrain.TryContact(hit, out var contact) && contact.Cell == cell, Is.True);
            Assert.That(_samples.Single().Worker.Pieces.All(p => !p.Rendered && p.RenderIndices == 0), Is.True);
        }

        [UnityTest] public IEnumerator CompletedEarlyBakeFaultIsObservedWhileAnotherGenerationIsHeld()
        { yield return EarlyBakeRejected(false); }
        [UnityTest] public IEnumerator CompletedEarlyBakeCancellationIsObservedWhileAnotherGenerationIsHeld()
        { yield return EarlyBakeRejected(true); }
        IEnumerator EarlyBakeRejected(bool cancel)
        {
            PipelineHeldGroup group = null; yield return Start(2, value => group = value);
            var before = _terrain.World; group.Complete(1); yield return UploadOne(group);
            Task actualBake = group.Bake(1); yield return Wait(() => actualBake.IsCompleted, "actual Bake joins before injecting its terminal rejection");
            Assert.That(actualBake.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            var rejection = new TaskCompletionSource<bool>();
            if (cancel) rejection.SetCanceled(); else rejection.SetException(new InvalidOperationException("bake-boundary-sentinel"));
            PipelineReflection.Set(group.Generated.GetValue(1), "Bake", rejection.Task);
            Assert.That(group.Generate.IsCompleted, Is.False); ExpectFailure(); group.Advance(true);
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(group.Retired, Is.EqualTo(1));
            Assert.That(_samples.Single().Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            group.Complete(0); yield return Wait(() => group.Generate.IsCompleted, "retired generation is fully joined");
            group.Cleanup(); yield return null; Assert.That(group.Retired, Is.Zero);
        }
    }

    static class PipelineReflection
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
        public static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
        public static object Call(object target, string method, params object[] args)
            => target.GetType().GetMethods(Flags).Single(m => m.Name == method && !m.ContainsGenericParameters
                && m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(b => b))
                .Invoke(target, args);
        public static object CallStatic(string method, params object[] args)
            => typeof(BlockTerrain).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == method
                && m.GetParameters().Length == args.Length).Invoke(null, args);
    }
    sealed class PipelineHeldGroup
    {
        readonly BlockTerrain _terrain;
        readonly object[] _completion;
        readonly SemaphoreSlim _workers;
        readonly BlockWorkerQueue _admissions;
        readonly CancellationToken _admissionToken;
        readonly List<Task> _actualBakes = new();
        int _ownedPermits;
        public readonly object Pending;
        public readonly Array Generated, Installed;
        public readonly Task Generate;
        public BlockWorld Next => (BlockWorld)PipelineReflection.Get(Pending, "Next");
        public int Uploaded => (int)PipelineReflection.Get(Pending, "Uploaded");
        public int Retired => (int)PipelineReflection.Get(_terrain, "_retired").GetType().GetProperty("Count").GetValue(PipelineReflection.Get(_terrain, "_retired"));
        public PipelineHeldGroup(BlockTerrain terrain, object pending, Array generated)
        {
            _terrain = terrain; Pending = pending; Generated = generated;
            Type prepared = generated.GetType().GetElementType();
            Type completion = typeof(TaskCompletionSource<>).MakeGenericType(prepared);
            Type task = typeof(Task<>).MakeGenericType(prepared);
            var tasks = Array.CreateInstance(task, generated.Length); _completion = new object[generated.Length];
            Installed = Array.CreateInstance(prepared, generated.Length);
            for (int i = 0; i < tasks.Length; i++)
            {
                _completion[i] = Activator.CreateInstance(completion, new object[] { TaskCreationOptions.RunContinuationsAsynchronously });
                tasks.SetValue(completion.GetProperty("Task").GetValue(_completion[i]), i);
            }
            var scheduleType = typeof(TaskCompletionSource<>).MakeGenericType(tasks.GetType());
            object schedule = Activator.CreateInstance(scheduleType, new object[] { TaskCreationOptions.RunContinuationsAsynchronously });
            PipelineReflection.Call(schedule, "SetResult", tasks);
            PipelineReflection.Set(pending, "Scheduled", schedule); PipelineReflection.Set(pending, "Generating", null);
            PipelineReflection.Set(pending, "Pieces", Installed); PipelineReflection.Set(pending, "PieceUploaded", new bool[generated.Length]);
            PipelineReflection.Set(pending, "Uploaded", 0); PipelineReflection.Set(pending, "UploadScanCursor", 0);
            PipelineReflection.Set(pending, "WorkerGroupFinishedStamp", 0L);
            Generate = (Task)PipelineReflection.CallStatic("FinishGenerationGroup", pending, tasks);
            PipelineReflection.Set(pending, "Generate", Generate);
            _workers = (SemaphoreSlim)typeof(BlockTerrain).GetField("Workers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _admissions = (BlockWorkerQueue)typeof(BlockTerrain).GetField("WorkerAdmissions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _admissionToken = ((CancellationTokenSource)PipelineReflection.Get(pending, "AdmissionStop")).Token;
        }
        public void Complete(int index) => PipelineReflection.Call(_completion[index], "TrySetResult", Generated.GetValue(index));
        public void Reject(int index, bool cancel)
        {
            if (cancel) PipelineReflection.Call(_completion[index], "TrySetCanceled");
            else PipelineReflection.Call(_completion[index], "TrySetException", new InvalidOperationException("generation-boundary-sentinel"));
        }
        public void HoldBothPermits()
        { while (_workers.Wait(0)) _ownedPermits++; Assert.That(_ownedPermits, Is.EqualTo(2), "no unrelated worker is active"); }
        public bool WaitingBakeAdmission
        {
            get
            {
                lock (PipelineReflection.Get(_admissions, "_gate"))
                    return new[] { "_foreground", "_background" }.Any(queue => ((IEnumerable)PipelineReflection.Get(_admissions, queue))
                        .Cast<object>().Any(request => ((CancellationToken)PipelineReflection.Get(request, "Token")).Equals(_admissionToken)));
            }
        }
        public bool AdmissionsIdle => _admissions.WhenIdle.IsCompleted;
        public bool AdmissionCancelled => _admissionToken.IsCancellationRequested;
        public void AssertBothPermitsHeld()
        { Assert.That(_ownedPermits, Is.EqualTo(2)); Assert.That(_workers.CurrentCount, Is.Zero); }
        public void AssertWaitingBake(int index)
        {
            AssertBothPermitsHeld(); Assert.That(WaitingBakeAdmission, Is.True); Assert.That(Bake(index).IsCompleted, Is.False);
            Assert.That((long)PipelineReflection.Get(Generated.GetValue(index), "BakeStartStamp"), Is.Zero);
            Assert.That((long)PipelineReflection.Get(Generated.GetValue(index), "BakeEndStamp"), Is.Zero);
        }
        public void AssertCancelledBake(int index)
        {
            Assert.That(Bake(index).IsCanceled, Is.True);
            Assert.That((long)PipelineReflection.Get(Generated.GetValue(index), "BakeStartStamp"), Is.Zero, "waiting admission never entered Physics.BakeMesh");
            Assert.That((long)PipelineReflection.Get(Generated.GetValue(index), "BakeEndStamp"), Is.Zero);
        }
        public void ReleasePermits() { while (_ownedPermits > 0) { _workers.Release(); _ownedPermits--; } }
        public void ReleasePermitsAndCompleteRemaining()
        { ReleasePermits(); for (int i = 0; i < _completion.Length; i++) Complete(i); }
        public void Advance(bool publication)
        {
            PipelineReflection.Call(_terrain, "AdvancePending", publication);
            foreach (var piece in Generated)
                if (PipelineReflection.Get(piece, "Bake") is Task bake && !_actualBakes.Contains(bake)) _actualBakes.Add(bake);
        }
        public void Cleanup() => PipelineReflection.Call(_terrain, "CleanupRetired");
        public Task Bake(int index) => (Task)PipelineReflection.Get(Generated.GetValue(index), "Bake");
        public Mesh CollisionMesh(int index) => (Mesh)PipelineReflection.Get(Generated.GetValue(index), "CollisionMesh");
        public IEnumerable<Task> StartedBakes() => _actualBakes.Concat(Generated.Cast<object>()
            .Select(piece => PipelineReflection.Get(piece, "Bake") as Task).Where(task => task != null)).Distinct();
        public bool Ready => Generate.Status == TaskStatus.RanToCompletion && Uploaded == Installed.Length
            && Installed.Cast<object>().All(piece => piece != null && PipelineReflection.Get(piece, "Bake") is Task bake && bake.Status == TaskStatus.RanToCompletion);
    }
}
#endif
