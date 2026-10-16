#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    // Real Task/mesh/Bake/collider chains; no replacement terrain or per-cell timing.
    public sealed class BlockPendingWorkerRuntimeTests
    {
        GameObject _object;
        BlockTerrain _terrain;
        readonly List<BlockTerrain.PendingWallDiagnostic> _samples = new();
        static readonly Vector3Int[] Cells = Enumerable.Range(0, 8)
            .Select(i => new Vector3Int(-56 + i * 16, 1, 5)).ToArray();

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _object = new GameObject("Real pending worker observation fixture");
            _terrain = _object.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            _samples.Clear(); _terrain.PendingTransactionFinished += Observe;
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_terrain != null)
            {
                _terrain.PendingTransactionFinished -= Observe; _terrain.enabled = true;
                for (int i = 0; i < 1000 && _terrain.PendingTransactions != 0; i++)
                { _terrain.Tick(true, 100 + i * .016, .016f); yield return null; }
            }
            if (_object != null) Object.DestroyImmediate(_object);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Observe(BlockTerrain.PendingWallDiagnostic sample) => _samples.Add(sample);
        object Pending => WorkerObservationReflection.Get(_terrain, "_pending");
        static Task Generate(object pending) => (Task)WorkerObservationReflection.Get(pending, "Generate");
        static Array CompletedPieces(object pending) => (Array)Generate(pending).GetType().GetProperty("Result").GetValue(Generate(pending));
        IEnumerator WaitGeneration(object pending)
        {
            var task = Generate(pending);
            for (int i = 0; i < 1000 && !task.IsCompleted; i++) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Real generation never completed");
            Assert.That(task.IsFaulted || task.IsCanceled, Is.False, task.Exception?.ToString());
        }
        IEnumerator WaitReady(object untrackedPending = null)
        {
            for (int i = 0; i < 1000; i++)
            {
                WorkerObservationReflection.Call(_terrain, "AdvancePending", false);
                if (untrackedPending == null)
                {
                    if (_terrain.InspectPendingTransaction().phase == BlockTerrain.PendingWallPhase.ReadyToPublish) yield break;
                }
                else
                {
                    // Untracked is a diagnostic policy, not a production phase.
                    // Keep tracking frozen off and inspect the real original gates.
                    Assert.That(Pending, Is.SameAs(untrackedPending), "Untracked work must remain unpublished while waiting");
                    Assert.That((bool)WorkerObservationReflection.Get(untrackedPending, "TrackWall"), Is.False);
                    var head = _terrain.InspectPendingTransaction();
                    Assert.That(head.present && !head.tracked, Is.True);
                    Assert.That(head.phase, Is.EqualTo(BlockTerrain.PendingWallPhase.Untracked));
                    if (IsReady(untrackedPending)) yield break;
                }
                yield return null;
            }
            Assert.Fail("Real generation/upload/Bake never reached ready");
        }
        static bool IsReady(object pending)
        {
            // Same completion gates as AdvancePending, using real Task results.
            // This helper never stamps, completes, replaces or publishes work.
            var generate = Generate(pending);
            if (!generate.IsCompleted) return false;
            Assert.That(generate.IsFaulted || generate.IsCanceled, Is.False, generate.Exception?.ToString());
            var pieces = (Array)WorkerObservationReflection.Get(pending, "Pieces");
            if (pieces == null || (int)WorkerObservationReflection.Get(pending, "Uploaded") < pieces.Length) return false;
            foreach (var piece in pieces)
            {
                var bake = (Task)WorkerObservationReflection.Get(piece, "Bake");
                if (bake == null || !bake.IsCompleted) return false;
                Assert.That(bake.IsFaulted || bake.IsCanceled, Is.False, bake.Exception?.ToString());
            }
            return true;
        }
        IEnumerator Drain(bool authority = false, double startAt = 1)
        {
            for (int i = 0; i < 1000 && _terrain.PendingTransactions != 0; i++)
            { _terrain.Tick(authority, startAt + i * .016, .016f); yield return null; }
            Assert.That(_terrain.PendingTransactions, Is.Zero);
        }
        static BlockWorld EightWorld(bool includeTargets)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            foreach (var cell in Cells)
            {
                world.Set(cell + Vector3Int.down, BlockCell.Create(1, true));
                if (includeTargets) world.Set(cell, BlockCell.Create(1, true));
            }
            world.Seal(1, world.Chunks.Keys); return world;
        }
        void StartEmptyDelta()
        {
            var next = _terrain.World.Clone(); next.Seal(_terrain.Revision + 1, Array.Empty<Vector3Int>());
            Assert.That(_terrain.ApplyRemoteDelta(next.EncodeDelta(_terrain.Revision)), Is.True);
            _terrain.Tick(false, 1, .016f); Assert.That(Pending, Is.Not.Null);
        }

        [UnityTest] public IEnumerator EightRealAuthorityPiecesPartitionAndDelayedObservationPreservePublication()
        {
            _terrain.PendingTransactionFinished -= Observe;
            Assert.That(_terrain.ApplyRemoteSnapshot(EightWorld(false).EncodeSnapshot()), Is.True);
            _terrain.Tick(false, 1, .016f); yield return Drain();
            _terrain.PendingTransactionFinished += Observe;
            using var collector = new WorkerCollectorHarness(_terrain);
            var outcomes = new List<BlockMutationResult>();
            int publications = 0, largestPublication = 0;
            bool atomicCollision = true, beforePrimary = true;
            Action<BlockTerrain.PendingWallDiagnostic> collect = sample =>
            {
                // Save booleans only: diagnostic subscriber assertions are swallowed.
                int committedTargets = 0;
                foreach (var cell in Cells)
                {
                    if (!_terrain.World.TryGet(cell, out _)) continue;
                    committedTargets++;
                    atomicCollision &= Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                        out var hit, .6f, 1 << BlockTerrain.TerrainLayer) && _terrain.TryContact(hit, out var contact)
                        && contact.Cell == cell && contact.Stable && contact.Owner == 1
                        && contact.Revision == _terrain.ChunkRevision(BlockWorld.ChunkAt(cell));
                }
                beforePrimary &= outcomes.Count < committedTargets;
                collector.Receive(sample);
            };
            _terrain.PendingTransactionFinished += collect;
            _terrain.PublishedCommit += commit => { publications++; largestPublication = Math.Max(largestPublication, commit.ChangedChunks.Length); };
            try
            {
                foreach (var cell in Cells)
                    _terrain.QueueBuild(BlockWorld.Center(cell) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false, outcomes.Add);
                uint revision = _terrain.Revision; double now = 20;
                // The .75 ms command budget can legitimately stage a subset,
                // or none on a cold frame. Preserve it and wait for real work.
                for (int i = 0; i < 1000 && Pending == null; i++)
                { _terrain.Tick(true, now += .016, .016f); if (Pending == null) yield return null; }
                object pending = Pending; Assert.That(pending, Is.Not.Null);
                var changed = (Vector3Int[])WorkerObservationReflection.Get(pending, "Changed");
                _terrain.enabled = false;
                yield return WaitGeneration(pending);
                Array pieces = CompletedPieces(pending);
                var generatedCounts = WorkerObservationAssertions.CaptureLengths(pieces);
                Assert.That(pieces.Length, Is.InRange(1, 8));
                Assert.That((long)WorkerObservationReflection.Get(pending, "GenerationObservedWallStamp"), Is.Zero);
                long finished = (long)WorkerObservationReflection.Get(pending, "WorkerGroupFinishedStamp");
                Assert.That(finished, Is.GreaterThan(0));
                yield return null; yield return null; // Suppress the real main-thread observation for two frames.
                Assert.That((long)WorkerObservationReflection.Get(pending, "GenerationObservedWallStamp"), Is.Zero);
                _terrain.enabled = true; yield return WaitReady();
                Assert.That(_terrain.Revision, Is.EqualTo(revision)); Assert.That(outcomes, Is.Empty); Assert.That(_samples, Is.Empty);
                yield return null; _terrain.Tick(true, now += .016, .016f);
                Assert.That(_samples.Count, Is.EqualTo(1));
                var delayedSample = _samples[0]; WorkerObservationAssertions.Partition(delayedSample);
                Assert.That(delayedSample.Worker.CompletionToMainObservedMs, Is.GreaterThan(0));
                Assert.That(delayedSample.Worker.GroupFinishedStamp, Is.EqualTo(finished));
                CollectionAssert.AreEquivalent(changed, delayedSample.Worker.Pieces.Select(p => p.Key));
                WorkerObservationAssertions.Lengths(delayedSample.Worker, pieces, generatedCounts);
                for (int i = 0; i < 1000 && outcomes.Count < Cells.Length; i++)
                { _terrain.Tick(true, now += .016, .016f); yield return null; }
                Assert.That(outcomes.Count, Is.EqualTo(8)); Assert.That(outcomes.All(r => r.Added == 1), Is.True);
                Assert.That(publications, Is.GreaterThan(0)); Assert.That(largestPublication, Is.InRange(1, 8));
                Assert.That(atomicCollision && beforePrimary, Is.True); Assert.That(_samples.Count, Is.EqualTo(publications));
                foreach (var sample in _samples)
                { WorkerObservationAssertions.Partition(sample); Assert.That(sample.Pieces, Is.InRange(1, 8)); }
                Assert.That(_terrain.InspectPendingWork().observerFailures, Is.Zero);
                for (int i = 0; i < 6; i++) Assert.That(collector.OldValues(i).Count, Is.EqualTo(_samples.Count));
                for (int i = 0; i < 8; i++) Assert.That(collector.Values(i).Count, Is.EqualTo(_samples.Count));
                Assert.That(collector.Result.pendingWorkerAvailableCompletions, Is.EqualTo(_samples.Count));
                Assert.That(collector.Result.pendingWorkerMissingCompletions, Is.Zero);
                Assert.That(collector.Result.pendingWorkerPieces, Is.EqualTo(_samples.Sum(s => s.Worker.Pieces.Length)));
                Assert.That(collector.Result.pendingWorkerOutputArrayLogicalBytes,
                    Is.EqualTo(_samples.Sum(s => s.Worker.Pieces.Sum(p => p.OutputArrayLogicalBytes))));
                collector.WriteReport(true);
                Assert.That(collector.Result.metrics[0].name, Is.EqualTo("_frames"));
                foreach (var cell in Cells) Assert.That(_terrain.World.TryGet(cell, out var block) && block.Owner == 1, Is.True);
            }
            finally { _terrain.enabled = true; _terrain.PendingTransactionFinished -= collect; }
        }

        [UnityTest] public IEnumerator RealEmptyWorkerGroupIsAvailableAndWarmupRecordIsCensored()
        {
            StartEmptyDelta(); object warmup = Pending;
            using var collector = new WorkerCollectorHarness(_terrain);
            Action<BlockTerrain.PendingWallDiagnostic> collect = collector.Receive;
            _terrain.PendingTransactionFinished += collect;
            try
            {
                Assert.That((long)WorkerObservationReflection.Get(warmup, "CreatedWallStamp"), Is.LessThan(collector.WindowStart));
                yield return WaitReady(); _terrain.Tick(false, 2, .016f);
                yield return Drain(false, 2);
                StartEmptyDelta(); yield return WaitReady(); _terrain.Tick(false, 3, .016f);
                // Ready work may still await the next frame's CPU admission.
                // Count terminal samples only after both real commits publish.
                yield return Drain(false, 3);
                Assert.That(_samples.Count, Is.EqualTo(2));
                foreach (var sample in _samples)
                {
                    WorkerObservationAssertions.Partition(sample);
                    Assert.That(sample.Worker.Pieces, Is.Empty); Assert.That(sample.Worker.CriticalPieceIndex, Is.EqualTo(-1));
                    Assert.That(WorkerObservationAssertions.Spans(sample.Worker).Skip(1).Take(5).All(v => v == 0), Is.True);
                    Assert.That(sample.Worker.InitialToCriticalAwaitMs, Is.EqualTo(sample.Worker.OuterDispatchMs));
                }
                Assert.That(collector.Result.pendingCensoredCompletions, Is.EqualTo(1));
                Assert.That(collector.Result.pendingRetainedFullyObservedPublishedCompletions, Is.EqualTo(1));
                Assert.That(collector.Result.pendingWorkerAvailableCompletions, Is.EqualTo(1));
                Assert.That(collector.Result.pendingWorkerPieces, Is.Zero);
                for (int i = 0; i < 8; i++) Assert.That(collector.Values(i).Count, Is.EqualTo(1));
                Assert.That(_terrain.InspectPendingWork().observerFailures, Is.Zero);
            }
            finally { _terrain.PendingTransactionFinished -= collect; }
        }

        [UnityTest] public IEnumerator SubscriptionAfterCreationDoesNotTurnUntrackedWorkIntoTrackedWork()
        {
            _terrain.PendingTransactionFinished -= Observe;
            Assert.That(_terrain.ApplyRemoteSnapshot(EightWorld(true).EncodeSnapshot()), Is.True);
            _terrain.Tick(false, 1, .016f); object pending = Pending;
            Assert.That((bool)WorkerObservationReflection.Get(pending, "TrackWall"), Is.False);
            _terrain.enabled = false; _terrain.PendingTransactionFinished += Observe;
            try
            {
                yield return WaitGeneration(pending); Array pieces = CompletedPieces(pending);
                Assert.That((long)WorkerObservationReflection.Get(pending, "WorkerGroupEnteredStamp"), Is.Zero);
                Assert.That((long)WorkerObservationReflection.Get(pending, "WorkerGroupFinishedStamp"), Is.Zero);
                foreach (var piece in pieces)
                    Assert.That(WorkerObservationReflection.Get(piece, "Worker"), Is.EqualTo(default(BlockTerrain.PendingPieceWorkerDiagnostic)));
                _terrain.enabled = true; yield return WaitReady(pending); _terrain.Tick(false, 2, .016f);
                Assert.That(_samples, Is.Empty); Assert.That(_terrain.InspectPendingWork().created, Is.Zero);
                Assert.That(_terrain.InspectPendingWork().observerFailures, Is.Zero);
                foreach (var cell in Cells)
                    Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down, out var hit,
                        .6f, 1 << BlockTerrain.TerrainLayer) && _terrain.TryContact(hit, out var contact)
                        && contact.Cell == cell && contact.Owner == 1 && contact.Stable, Is.True);
            }
            finally { _terrain.enabled = true; }
        }

        [UnityTest] public IEnumerator OriginalTwoSemaphorePermitsProduceRealAwaitElapsedWithoutCapacityChange()
        {
            var workers = (SemaphoreSlim)typeof(BlockTerrain).GetField("Workers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            int owned = 0;
            try
            {
                while (workers.Wait(0)) owned++;
                Assert.That(owned, Is.EqualTo(2), "Fixture starts with no unrelated worker operation");
                Assert.That(_terrain.ApplyRemoteSnapshot(EightWorld(true).EncodeSnapshot()), Is.True);
                _terrain.Tick(false, 1, .016f); object pending = Pending; _terrain.enabled = false;
                for (int i = 0; i < 1000 && (long)WorkerObservationReflection.Get(pending, "WorkerGroupEnteredStamp") == 0; i++) yield return null;
                Assert.That((long)WorkerObservationReflection.Get(pending, "WorkerGroupEnteredStamp"), Is.GreaterThan(0));
                yield return null; Assert.That(Generate(pending).IsCompleted, Is.False);
                long releaseBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                while (owned > 0) { workers.Release(); owned--; }
                yield return WaitGeneration(pending); _terrain.enabled = true;
                yield return WaitReady(); _terrain.Tick(false, 2, .016f);
                var sample = _samples.Single(); WorkerObservationAssertions.Partition(sample);
                Assert.That(sample.Worker.Pieces.Any(p => p.SemaphoreAwaitStartStamp < releaseBegin), Is.True);
                Assert.That(sample.Worker.Pieces.All(p => p.SemaphoreAcquiredStamp >= releaseBegin), Is.True);
                Assert.That(sample.Worker.Pieces.Any(p => p.SemaphoreAwaitElapsedMs > 0), Is.True);
                Assert.That(workers.CurrentCount, Is.EqualTo(2));
                Assert.That(_terrain.InspectPendingWork().observerFailures, Is.Zero);
            }
            finally
            {
                while (owned > 0) { workers.Release(); owned--; }
                _terrain.enabled = true;
            }
        }
    }

    public sealed class BlockPendingWorkerScalarTests
    {
        [Test] public void RealHeadlessPieceKeepsCollisionAndUntrackedBuilderKeepsIdenticalOutputs()
        {
            var world = new BlockWorld(new Vector2(32, 64)); world.Set(new Vector3Int(5, 1, 5), BlockCell.Create(1, true));
            world.Seal(1, world.Chunks.Keys); long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            object headless = WorkerObservationReflection.CallStatic("PrepareGenerationPiece", world, Vector3Int.zero, false, true, stamp, stamp);
            var worker = (BlockTerrain.PendingPieceWorkerDiagnostic)WorkerObservationReflection.Get(headless, "Worker");
            Assert.That(WorkerObservationReflection.Get(headless, "Render"), Is.Null); Assert.That(worker.Rendered, Is.False);
            Assert.That(worker.RenderStartStamp, Is.Zero); Assert.That(worker.RenderEndStamp, Is.Zero);
            Assert.That(worker.RenderVertices + worker.RenderNormals + worker.RenderColors + worker.RenderIndices, Is.Zero);
            Assert.That(worker.RenderBuildElapsedMs, Is.Zero);
            Assert.That(worker.BuildBodyStartStamp, Is.GreaterThanOrEqualTo(stamp));
            Assert.That(worker.CollisionStartStamp, Is.GreaterThanOrEqualTo(worker.BuildBodyStartStamp));
            Assert.That(worker.CollisionEndStamp, Is.GreaterThanOrEqualTo(worker.CollisionStartStamp));
            Assert.That(worker.BuildBodyEndStamp, Is.GreaterThanOrEqualTo(worker.CollisionEndStamp));
            Assert.That(worker.CollisionVertices, Is.EqualTo(24)); Assert.That(worker.CollisionIndices, Is.EqualTo(36));
            Assert.That(worker.CollisionNormals, Is.Zero); Assert.That(worker.CollisionColors, Is.Zero);
            Assert.That(worker.OutputArrayLogicalBytes, Is.EqualTo(432), "one isolated collision cube: 24 Vector3 positions and 36 int indices");
            object tracked = WorkerObservationReflection.CallStatic("PrepareGenerationPiece", world, Vector3Int.zero, true, true, stamp, stamp);
            object untracked = WorkerObservationReflection.CallStatic("PrepareGenerationPiece", world, Vector3Int.zero, true, false, 0L, 0L);
            Assert.That(WorkerObservationReflection.Get(untracked, "Worker"), Is.EqualTo(default(BlockTerrain.PendingPieceWorkerDiagnostic)));
            foreach (string field in new[] { "Render", "Collision" })
            {
                var a = (BlockMeshData)WorkerObservationReflection.Get(tracked, field);
                var b = (BlockMeshData)WorkerObservationReflection.Get(untracked, field);
                CollectionAssert.AreEqual(a.Vertices, b.Vertices); CollectionAssert.AreEqual(a.Normals, b.Normals);
                CollectionAssert.AreEqual(a.Colors, b.Colors); CollectionAssert.AreEqual(a.Triangles, b.Triangles);
            }
        }

        [Test] public void LatestBodyEndSelectsCriticalPieceAndExactSpansDoNotDoubleCountParallelWork()
        {
            object pending = WorkerObservationSynthetic.Pending(); var worker = WorkerObservationSynthetic.Observe(pending);
            Assert.That(worker.Available, Is.True); Assert.That(worker.CriticalPieceIndex, Is.EqualTo(1));
            CollectionAssert.AreEqual(new long[] { 300, 500, 400, 300, 1300, 800, 200, 500 },
                WorkerObservationAssertions.Spans(worker).Select(v => (long)Math.Round(v * System.Diagnostics.Stopwatch.Frequency / 1000)));
            Assert.That(WorkerObservationAssertions.Spans(worker).Sum(), Is.EqualTo(WorkerObservationSynthetic.Ms(4300)).Within(1e-6));
            Assert.That(worker.OuterDispatchMs, Is.EqualTo(WorkerObservationSynthetic.Ms(100)).Within(1e-6));
            var originalPieces = (Array)WorkerObservationReflection.Get(pending, "Pieces");
            worker.Pieces[0].Key = new Vector3Int(99, 99, 99);
            Assert.That(((BlockTerrain.PendingPieceWorkerDiagnostic)WorkerObservationReflection.Get(originalPieces.GetValue(0), "Worker")).Key,
                Is.EqualTo(Vector3Int.zero), "copied array cannot modify Prepared.Worker");
            WorkerObservationSynthetic.ChangeWorker(pending, 0, "BuildBodyEndStamp", 4600L);
            Assert.That(WorkerObservationSynthetic.Observe(pending).CriticalPieceIndex, Is.Zero, "stable first index on equal latest body end");
        }

        [Test] public void MissingAndMalformedWorkerStampsAreUnavailableRatherThanZeroDurationSuccess()
        {
            Assert.That(default(BlockTerrain.PendingGenerationWorkerDiagnostic).Available, Is.False);
            var mutations = new (string scope, string field, object value)[] {
                ("pending", "GenerationObservedWallStamp", 0L), ("pending", "WorkerGroupEnteredStamp", 999L),
                ("pending", "WorkerGroupFinishedStamp", 5301L), ("pending", "Pieces", null),
                ("worker", "SemaphoreAwaitStartStamp", 1099L), ("worker", "SemaphoreAcquiredStamp", 1199L),
                ("worker", "BuildBodyStartStamp", 1799L), ("worker", "RenderStartStamp", 1999L),
                ("worker", "RenderEndStamp", 2099L), ("worker", "CollisionStartStamp", 2699L),
                ("worker", "CollisionEndStamp", 2799L), ("worker", "BuildBodyEndStamp", 4199L),
                ("worker", "BuildBodyEndStamp", 4900L), ("worker", "Rendered", false) };
            foreach (var mutation in mutations)
            {
                object pending = WorkerObservationSynthetic.Pending();
                if (mutation.scope == "pending") WorkerObservationReflection.Set(pending, mutation.field, mutation.value);
                else WorkerObservationSynthetic.ChangeWorker(pending, 0, mutation.field, mutation.value);
                Assert.That(WorkerObservationSynthetic.Observe(pending).Available, Is.False, mutation.field + "=" + mutation.value);
            }
            object nullPiece = WorkerObservationSynthetic.Pending();
            ((Array)WorkerObservationReflection.Get(nullPiece, "Pieces")).SetValue(null, 0);
            Assert.That(WorkerObservationSynthetic.Observe(nullPiece).Available, Is.False);
            object empty = WorkerObservationSynthetic.Pending();
            WorkerObservationReflection.Set(empty, "Pieces", Array.CreateInstance(typeof(BlockTerrain).GetNestedType("Prepared", BindingFlags.NonPublic), 0));
            var noPieces = WorkerObservationSynthetic.Observe(empty);
            Assert.That(noPieces.Available, Is.True); Assert.That(noPieces.CriticalPieceIndex, Is.EqualTo(-1));
            Assert.That(WorkerObservationAssertions.Spans(noPieces).Sum(), Is.EqualTo(WorkerObservationSynthetic.Ms(4300)).Within(1e-6));
        }

        [Test] public void WorkerCollectorSharesRetainedFullSuccessPopulationAndReportsRealFinalPercentiles()
        {
            using var collector = new WorkerCollectorHarness(null);
            var worker = WorkerObservationSynthetic.Observe(WorkerObservationSynthetic.Pending());
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, complete: false));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, BlockTerrain.PendingWallOutcome.Failed));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, BlockTerrain.PendingWallOutcome.Aborted));
            var result = collector.Result;
            Assert.That(result.pendingRetainedCompletions, Is.EqualTo(5));
            Assert.That(result.pendingRetainedFullyObservedPublishedCompletions, Is.EqualTo(2));
            Assert.That(result.pendingWorkerAvailableCompletions, Is.EqualTo(1)); Assert.That(result.pendingWorkerMissingCompletions, Is.EqualTo(1));
            for (int i = 0; i < 6; i++) Assert.That(collector.OldValues(i).Count, Is.EqualTo(2));
            for (int i = 0; i < 8; i++) CollectionAssert.AreEqual(new[] { WorkerObservationAssertions.Spans(worker)[i] }, collector.Values(i));
            Assert.That(result.pendingWorkerPieces, Is.EqualTo(2)); Assert.That(result.pendingWorkerRenderedPieces, Is.EqualTo(2));
            Assert.That(result.pendingWorkerRenderQuads, Is.EqualTo(12)); Assert.That(result.pendingWorkerCollisionQuads, Is.EqualTo(12));
            Assert.That(result.pendingWorkerOutputArrayLogicalBytes, Is.EqualTo(2496));
            Assert.That(result.pendingWorkerSemaphoreAwaitElapsedSumMs, Is.EqualTo(WorkerObservationSynthetic.Ms(1100)).Within(1e-6));
            Assert.That(result.pendingWorkerRenderBuildElapsedSumMs, Is.EqualTo(WorkerObservationSynthetic.Ms(900)).Within(1e-6));
            Assert.That(result.pendingWorkerCollisionBuildElapsedSumMs, Is.EqualTo(WorkerObservationSynthetic.Ms(2700)).Within(1e-6));
            collector.WriteReport(true);
            Assert.That(result.metrics[0].name, Is.EqualTo("_frames"));
            foreach (string name in WorkerCollectorHarness.Names)
            {
                var metric = result.metrics.Single(m => m.name == name); Assert.That(metric.samples, Is.EqualTo(1));
                Assert.That(metric.p50, Is.EqualTo(metric.mean)); Assert.That(metric.p95, Is.EqualTo(metric.mean));
                Assert.That(metric.p99, Is.EqualTo(metric.mean));
            }
        }

        [Test] public void WholeRecordCapFreezesWorkerAggregatesAndKeepsOriginalMetricsAndFormalGates()
        {
            using var collector = new WorkerCollectorHarness(null);
            var worker = WorkerObservationSynthetic.Observe(WorkerObservationSynthetic.Pending());
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart - 1, worker));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, BlockTerrain.PendingWallOutcome.Failed));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, BlockTerrain.PendingWallOutcome.Aborted));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, complete: false));
            int full = collector.Cap - 3; Assert.That(collector.Cap, Is.EqualTo(16384));
            for (int i = 0; i < full; i++) collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, i % 2 == 0 ? worker : default));
            collector.WriteReport(true);
            string metrics = JsonUtility.ToJson(new PendingCollectorHarness.MetricEnvelope { metrics = collector.Result.metrics.Where(m => m.name.StartsWith("pending")).ToArray() });
            string gates = JsonUtility.ToJson(new PendingCollectorHarness.GateEnvelope { gates = collector.Result.gates });
            string aggregates = JsonUtility.ToJson(new WorkerObservationCounters(collector.Result));
            int available = (full + 1) / 2;
            Assert.That(collector.Result.pendingWorkerAvailableCompletions, Is.EqualTo(available));
            Assert.That(collector.Result.pendingWorkerMissingCompletions, Is.EqualTo(full - available));
            Assert.That(collector.Result.pendingCensoredCompletions, Is.EqualTo(1));
            foreach (var outcome in new[] { BlockTerrain.PendingWallOutcome.Published, BlockTerrain.PendingWallOutcome.Failed, BlockTerrain.PendingWallOutcome.Aborted })
                collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart,
                    WorkerObservationSynthetic.Observe(WorkerObservationSynthetic.Pending(1000000)), outcome));
            collector.Receive(WorkerObservationSynthetic.Sample(collector.WindowStart, worker, complete: false));
            collector.WriteReport(true);
            Assert.That(collector.Result.pendingRetainedCompletions, Is.EqualTo(collector.Cap));
            Assert.That(collector.Result.pendingDroppedCompletions, Is.EqualTo(4));
            Assert.That(collector.Result.pendingWorkerAvailableCompletions + collector.Result.pendingWorkerMissingCompletions,
                Is.EqualTo(collector.Result.pendingRetainedFullyObservedPublishedCompletions));
            for (int i = 0; i < 6; i++) Assert.That(collector.OldValues(i).Count, Is.EqualTo(full));
            for (int i = 0; i < 8; i++)
            { Assert.That(collector.Values(i).Count, Is.EqualTo(available)); Assert.That(collector.Values(i).Capacity, Is.EqualTo(collector.Cap)); }
            Assert.That(JsonUtility.ToJson(new PendingCollectorHarness.MetricEnvelope { metrics = collector.Result.metrics.Where(m => m.name.StartsWith("pending")).ToArray() }), Is.EqualTo(metrics));
            Assert.That(JsonUtility.ToJson(new PendingCollectorHarness.GateEnvelope { gates = collector.Result.gates }), Is.EqualTo(gates));
            Assert.That(JsonUtility.ToJson(new WorkerObservationCounters(collector.Result)), Is.EqualTo(aggregates));
        }
    }

    static class WorkerObservationReflection
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static object Get(object target, string field) => target.GetType().GetField(field, Fields).GetValue(target);
        public static void Set(object target, string field, object value) => target.GetType().GetField(field, Fields).SetValue(target, value);
        public static object Call(object target, string method, params object[] arguments)
            => Invoke(target.GetType().GetMethods(Fields), target, method, arguments);
        public static object CallStatic(string method, params object[] arguments)
            => Invoke(typeof(BlockTerrain).GetMethods(BindingFlags.Static | BindingFlags.NonPublic), null, method, arguments);
        static object Invoke(MethodInfo[] methods, object target, string method, object[] arguments)
        {
            var selected = methods.Single(m => m.Name == method && !m.ContainsGenericParameters && m.GetParameters().Length == arguments.Length
                && m.GetParameters().Select((p, i) => arguments[i] == null || p.ParameterType.IsInstanceOfType(arguments[i])).All(x => x));
            try { return selected.Invoke(target, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
    static class WorkerObservationAssertions
    {
        public static double[] Spans(BlockTerrain.PendingGenerationWorkerDiagnostic w) => new[] {
            w.InitialToCriticalAwaitMs, w.CriticalSemaphoreAwaitMs, w.CriticalInnerDispatchMs, w.CriticalRenderBuildMs,
            w.CriticalCollisionBuildMs, w.CriticalBodyOtherMs, w.GroupJoinTailMs, w.CompletionToMainObservedMs };
        public static void Partition(BlockTerrain.PendingWallDiagnostic sample)
        {
            Assert.That(sample.Worker.Available, Is.True);
            Assert.That(sample.GenerationObserved && sample.UploadCompleted && sample.BakeObserved && sample.PublishEntered, Is.True);
            Assert.That(sample.Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Published));
            var spans = Spans(sample.Worker); Assert.That(spans.All(v => double.IsFinite(v) && v >= 0), Is.True);
            Assert.That(spans.Sum(), Is.EqualTo(sample.GenerationObservedMs).Within(1e-6));
            Assert.That(sample.GenerationObservedMs + sample.UploadMs + sample.BakeObservedTailMs + sample.ReadyWaitMs + sample.PublicationMs,
                Is.EqualTo(sample.TotalMs).Within(1e-6));
            if (sample.Worker.Pieces.Length > 0)
            {
                int critical = Enumerable.Range(0, sample.Worker.Pieces.Length).OrderByDescending(i => sample.Worker.Pieces[i].BuildBodyEndStamp).First();
                Assert.That(sample.Worker.CriticalPieceIndex, Is.EqualTo(critical));
            }
        }
        public static int[][] CaptureLengths(Array prepared)
        {
            var counts = new int[prepared.Length][];
            for (int i = 0; i < prepared.Length; i++)
            {
                var render = (BlockMeshData)WorkerObservationReflection.Get(prepared.GetValue(i), "Render");
                var collision = (BlockMeshData)WorkerObservationReflection.Get(prepared.GetValue(i), "Collision");
                counts[i] = new[] { render?.Vertices.Length ?? 0, render?.Normals.Length ?? 0,
                    render?.Colors.Length ?? 0, render?.Triangles.Length ?? 0,
                    collision.Vertices.Length, collision.Normals.Length, collision.Colors.Length, collision.Triangles.Length, render != null ? 1 : 0 };
            }
            return counts; // Values only: do not retain generated geometry to test its release.
        }
        public static void Lengths(BlockTerrain.PendingGenerationWorkerDiagnostic worker, Array prepared, int[][] generatedCounts)
        {
            Assert.That(worker.Pieces.Length, Is.EqualTo(prepared.Length));
            Assert.That(generatedCounts.Length, Is.EqualTo(prepared.Length));
            for (int i = 0; i < prepared.Length; i++)
            {
                var piece = prepared.GetValue(i); var p = worker.Pieces[i];
                Assert.That(WorkerObservationReflection.Get(piece, "Render"), Is.Null, "successful upload releases managed render geometry");
                Assert.That(WorkerObservationReflection.Get(piece, "Collision"), Is.Null, "successful upload releases managed collision geometry");
                Assert.That(p.Key, Is.EqualTo(WorkerObservationReflection.Get(piece, "Key")));
                CollectionAssert.AreEqual(generatedCounts[i].Take(8).ToArray(), new[] { p.RenderVertices, p.RenderNormals, p.RenderColors, p.RenderIndices,
                    p.CollisionVertices, p.CollisionNormals, p.CollisionColors, p.CollisionIndices }, "generation counts survive managed geometry release");
                Assert.That(p.Rendered, Is.EqualTo(generatedCounts[i][8] != 0), "render build presence is independent of empty geometry");
                Assert.That(p.RenderIndices % 6, Is.Zero); Assert.That(p.CollisionIndices % 6, Is.Zero);
                Assert.That(p.CollisionVertices % 4, Is.Zero); Assert.That(p.CollisionNormals, Is.Zero);
                Assert.That(p.CollisionColors, Is.Zero);
            }
        }
    }
    static class WorkerObservationSynthetic
    {
        public static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public static object Pending(long scale = 1)
        {
            object pending = Activator.CreateInstance(typeof(BlockTerrain).GetNestedType("Pending", BindingFlags.NonPublic), true);
            var type = typeof(BlockTerrain).GetNestedType("Prepared", BindingFlags.NonPublic); var pieces = Array.CreateInstance(type, 2);
            var stamps = new long[][] { new long[] { 1200, 1800, 2000, 2100, 2700, 2800, 4200, 4400 },
                new long[] { 1300, 1800, 2200, 2300, 2600, 2700, 4000, 4600 } };
            for (int i = 0; i < 2; i++)
            {
                var t = stamps[i]; object prepared = Activator.CreateInstance(type, true);
                var worker = new BlockTerrain.PendingPieceWorkerDiagnostic { Key = new Vector3Int(i, 0, 0), Rendered = true,
                    SemaphoreAwaitStartStamp = t[0] * scale, SemaphoreAcquiredStamp = t[1] * scale, BuildBodyStartStamp = t[2] * scale,
                    RenderStartStamp = t[3] * scale, RenderEndStamp = t[4] * scale, CollisionStartStamp = t[5] * scale,
                    CollisionEndStamp = t[6] * scale, BuildBodyEndStamp = t[7] * scale,
                    RenderVertices = 24, RenderNormals = 24, RenderColors = 24, RenderIndices = 36,
                    CollisionVertices = 24, CollisionNormals = 0, CollisionColors = 0, CollisionIndices = 36 };
                WorkerObservationReflection.Set(prepared, "Worker", worker); pieces.SetValue(prepared, i);
            }
            WorkerObservationReflection.Set(pending, "CreatedWallStamp", 1000 * scale);
            WorkerObservationReflection.Set(pending, "WorkerGroupEnteredStamp", 1100 * scale);
            WorkerObservationReflection.Set(pending, "WorkerGroupFinishedStamp", 4800 * scale);
            WorkerObservationReflection.Set(pending, "GenerationObservedWallStamp", 5300 * scale);
            WorkerObservationReflection.Set(pending, "Pieces", pieces); return pending;
        }
        public static void ChangeWorker(object pending, int index, string field, object value)
        {
            var pieces = (Array)WorkerObservationReflection.Get(pending, "Pieces"); object prepared = pieces.GetValue(index);
            object boxed = WorkerObservationReflection.Get(prepared, "Worker"); WorkerObservationReflection.Set(boxed, field, value);
            WorkerObservationReflection.Set(prepared, "Worker", boxed);
        }
        public static BlockTerrain.PendingGenerationWorkerDiagnostic Observe(object pending)
            => (BlockTerrain.PendingGenerationWorkerDiagnostic)WorkerObservationReflection.CallStatic("ObserveGenerationWorker", pending);
        public static BlockTerrain.PendingWallDiagnostic Sample(long created, BlockTerrain.PendingGenerationWorkerDiagnostic worker = default,
            BlockTerrain.PendingWallOutcome outcome = BlockTerrain.PendingWallOutcome.Published, bool complete = true)
        {
            double generation = worker.Available ? WorkerObservationAssertions.Spans(worker).Sum() : 1;
            return new BlockTerrain.PendingWallDiagnostic(outcome, 7, created, false, worker.Available ? worker.Pieces.Length : 1,
                true, true, complete, true, generation, 2, 3, 4, 5, generation + 14, worker);
        }
    }
    sealed class WorkerCollectorHarness : IDisposable
    {
        public static readonly string[] Names = { "pendingGenerationCriticalInitialDispatchWallMs", "pendingGenerationCriticalSemaphoreAwaitElapsedMs",
            "pendingGenerationCriticalInnerTaskDispatchWallMs", "pendingGenerationCriticalRenderBuildElapsedMs", "pendingGenerationCriticalCollisionBuildElapsedMs",
            "pendingGenerationCriticalBuildBodyOtherWallMs", "pendingGenerationGroupJoinTailWallMs", "pendingGenerationCompletionToMainObservedWallMs" };
        readonly PendingCollectorHarness _inner;
        readonly object _benchmark;
        public WorkerCollectorHarness(BlockTerrain terrain)
        { _inner = new PendingCollectorHarness(terrain); _benchmark = WorkerObservationReflection.Get(_inner, "_benchmark"); }
        public BlockTerrainBenchmark.Result Result => _inner.Result;
        public long WindowStart => _inner.WindowStart;
        public int Cap => _inner.Cap;
        public List<double> Values(int index)
            => (List<double>)WorkerObservationReflection.Get(((Array)WorkerObservationReflection.Get(_benchmark, "_pendingWorkerMetrics")).GetValue(index), "Values");
        public List<double> OldValues(int index) => _inner.Values(index);
        public void Receive(BlockTerrain.PendingWallDiagnostic sample) => _inner.Receive(sample);
        public void WriteReport(bool final) => _inner.WriteReport(final);
        public void Dispose() => _inner.Dispose();
    }
    [Serializable] sealed class WorkerObservationCounters
    {
        public long available, missing, pieces, rendered, renderQuads, collisionQuads, bytes;
        public double awaitSum, renderSum, collisionSum;
        public WorkerObservationCounters(BlockTerrainBenchmark.Result r)
        {
            available = r.pendingWorkerAvailableCompletions; missing = r.pendingWorkerMissingCompletions;
            pieces = r.pendingWorkerPieces; rendered = r.pendingWorkerRenderedPieces;
            renderQuads = r.pendingWorkerRenderQuads; collisionQuads = r.pendingWorkerCollisionQuads; bytes = r.pendingWorkerOutputArrayLogicalBytes;
            awaitSum = r.pendingWorkerSemaphoreAwaitElapsedSumMs; renderSum = r.pendingWorkerRenderBuildElapsedSumMs; collisionSum = r.pendingWorkerCollisionBuildElapsedSumMs;
        }
    }
}
#endif
