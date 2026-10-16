#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    // Reflection holds asynchronous boundaries, not a replacement terrain model.
    // The first test runs original authority generation/upload/Bake and colliders.
    public sealed class BlockPendingWallTests
    {
        GameObject _object;
        BlockTerrain _terrain;
        readonly List<BlockTerrain.PendingWallDiagnostic> _samples = new();

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _object = new GameObject("Pending observation terrain test");
            _terrain = _object.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            _samples.Clear(); _terrain.PendingTransactionFinished += Observe;
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_terrain != null) _terrain.PendingTransactionFinished -= Observe;
            if (_object != null) Object.DestroyImmediate(_object);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Observe(BlockTerrain.PendingWallDiagnostic sample) => _samples.Add(sample);

        [UnityTest]
        public IEnumerator AuthorityColliderPublicationHasOrderedSpansAndOneSuccessPopulation()
        {
            _terrain.PendingTransactionFinished -= Observe;
            var cells = Enumerable.Range(0, 8).Select(i => new Vector3Int(-56 + i * 16, 1, 5)).ToArray();
            var seed = new BlockWorld(new Vector2(32, 64));
            foreach (var cell in cells) seed.Set(cell + Vector3Int.down, BlockCell.Create(1, true));
            seed.Seal(1, seed.Chunks.Keys); Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            for (int i = 0; i < 1000 && _terrain.PendingTransactions != 0; i++)
            { _terrain.Tick(false, 1 + i * .016, .016f); yield return null; }
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            _terrain.PendingTransactionFinished += Observe;
            using var collector = new PendingCollectorHarness(_terrain);
            int callbacks = 0, publications = 0, maximumChunks = 0;
            bool observerSawAtomicCollision = true, observerPrecededPrimary = true;
            uint before = _terrain.Revision;
            Action<BlockTerrain.PendingWallDiagnostic> collect = sample =>
            {
                // Do not Assert inside an observer: the runtime catches observer exceptions.
                observerPrecededPrimary &= callbacks < cells.Length;
                foreach (var cell in cells)
                    if (_terrain.World.TryGet(cell, out _))
                        observerSawAtomicCollision &= Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f,
                            Vector3.down, out var hit, .6f, 1 << BlockTerrain.TerrainLayer)
                            && _terrain.TryContact(hit, out var contact) && contact.Cell == cell
                            && contact.Stable && contact.Revision == _terrain.ChunkRevision(BlockWorld.ChunkAt(cell));
                collector.Receive(sample);
            };
            _terrain.PendingTransactionFinished += collect;
            _terrain.PublishedCommit += commit => { publications++; maximumChunks = Math.Max(maximumChunks, commit.ChangedChunks.Length); };
            try
            {
                foreach (var cell in cells)
                    _terrain.QueueBuild(BlockWorld.Center(cell) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                        result => { Assert.That(result.Added, Is.EqualTo(1)); callbacks++; });
                _terrain.Tick(true, 20, .016f);
                Assert.That(_terrain.InspectPendingTransaction().present, Is.True);
                yield return WaitReady();
                Assert.That(_terrain.Revision, Is.EqualTo(before), "Update(false) may prepare but cannot publish");
                Assert.That(_samples, Is.Empty); Assert.That(callbacks, Is.Zero);
                yield return null; // A real ready wait spans a frame without fabricating timestamps.
                for (int i = 0; i < 1000 && callbacks != cells.Length; i++)
                { _terrain.Tick(true, 21 + i * .016, .016f); yield return null; }
                Assert.That(callbacks, Is.EqualTo(cells.Length)); Assert.That(publications, Is.GreaterThan(0));
                Assert.That(maximumChunks, Is.InRange(1, 8), "the original publication cap remains enforced");
                Assert.That(_samples.Count, Is.EqualTo(publications));
                Assert.That(_samples.Select(s => s.Id).Distinct().Count(), Is.EqualTo(_samples.Count));
                Assert.That(observerSawAtomicCollision, Is.True); Assert.That(observerPrecededPrimary, Is.True);
                var work = _terrain.InspectPendingWork();
                Assert.That(work.observerFailures, Is.Zero); Assert.That(work.created, Is.EqualTo(_samples.Count));
                Assert.That(work.published, Is.EqualTo(_samples.Count));
                foreach (var sample in _samples)
                {
                    Assert.That(sample.Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Published));
                    Assert.That(sample.Remote, Is.False); Assert.That(sample.Pieces, Is.InRange(1, 8));
                    AssertCompleteSpans(sample);
                }
                Assert.That(_samples[0].ReadyWaitMs, Is.GreaterThan(0));
                for (int metric = 0; metric < 6; metric++) Assert.That(collector.Values(metric).Count, Is.EqualTo(_samples.Count));
                Assert.That(collector.Result.pendingRetainedFullyObservedPublishedCompletions, Is.EqualTo(_samples.Count));
                Assert.That(collector.Result.pendingPartialPublishedCompletions, Is.Zero);
                Assert.That(collector.Result.pendingDroppedCompletions, Is.Zero);
                foreach (var cell in cells) Assert.That(_terrain.World.TryGet(cell, out var own) && own.Owner == 1, Is.True);
            }
            finally { _terrain.PendingTransactionFinished -= collect; }
        }

        [UnityTest]
        public IEnumerator RealZeroPieceDeltaStillPublishesAndUsesOneKnownReadyInstant()
        {
            var next = _terrain.World.Clone(); next.Seal(_terrain.Revision + 1, Array.Empty<Vector3Int>());
            Assert.That(_terrain.ApplyRemoteDelta(next.EncodeDelta(_terrain.Revision)), Is.True);
            int chunks = _terrain.PublishedChunks; _terrain.Tick(false, 1, .016f);
            yield return WaitReady();
            Assert.That(_terrain.Revision, Is.Zero); Assert.That(_samples, Is.Empty);
            yield return null; _terrain.Tick(false, 2, .016f);
            Assert.That(_terrain.Revision, Is.EqualTo(1)); Assert.That(_terrain.PublishedChunks, Is.EqualTo(chunks));
            Assert.That(_samples.Count, Is.EqualTo(1)); var sample = _samples.Single();
            Assert.That(sample.Remote, Is.True); Assert.That(sample.Pieces, Is.Zero); AssertCompleteSpans(sample);
            Assert.That(sample.GenerationObservedMs, Is.GreaterThan(0), "an empty pieces array still came through the original asynchronous generation path");
            Assert.That(sample.UploadMs, Is.Zero); Assert.That(sample.BakeObservedTailMs, Is.Zero);
            Assert.That(sample.ReadyWaitMs, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator HeldGenerationBakeAndFrameBudgetCannotAppearPublishedBeforeTheirGates()
        {
            using var batch = new PendingControlledBatch(_terrain, 1);
            batch.Advance(false); Assert.That(_samples, Is.Empty);
            Assert.That(_terrain.InspectPendingTransaction().phase, Is.EqualTo(BlockTerrain.PendingWallPhase.Generation));
            batch.CompleteGeneration(); batch.Advance(false);
            Assert.That(_terrain.InspectPendingTransaction().phase, Is.EqualTo(BlockTerrain.PendingWallPhase.Bake));
            Assert.That(_samples, Is.Empty); Assert.That(_terrain.Revision, Is.Zero);
            batch.CompleteBake(); batch.Advance(false);
            Assert.That(_terrain.InspectPendingTransaction().phase, Is.EqualTo(BlockTerrain.PendingWallPhase.ReadyToPublish));
            var budget = (BlockPublicationFrameBudget)PendingObservationReflection.Get(_terrain, "_publicationFrameBudget");
            var window = (BlockPublicationWindow)PendingObservationReflection.Get(batch.Pending, "Window");
            int heldFrame = Time.frameCount;
            budget.BeginFrame(heldFrame);
            Assert.That(budget.TryReserve(window, window.Policy.FrameKeys, out var reservation, out _), Is.True);
            using (reservation) reservation.Commit();
            batch.Advance(true); Assert.That(_samples, Is.Empty); Assert.That(_terrain.Revision, Is.Zero);
            var work = _terrain.InspectPendingWork();
            Assert.That(work.generateNotReady, Is.GreaterThan(0)); Assert.That(work.bakeNotReady, Is.GreaterThan(0));
            Assert.That(work.readyAwaitTick, Is.GreaterThan(0)); Assert.That(work.frameChunkBudgetBlocked, Is.GreaterThan(0));
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(heldFrame), "publication resumes only after a real frame boundary");
            batch.Advance(true);
            Assert.That(_samples.Single().Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Published));
            AssertCompleteSpans(_samples.Single());
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void GenerationOrBakeFaultAndCancellationRemainFailed(bool bake, bool cancel)
        {
            using var batch = new PendingControlledBatch(_terrain, bake ? 1 : 0);
            var before = _terrain.World;
            if (bake) { batch.CompleteGeneration(); batch.Advance(false); batch.RejectBake(cancel); }
            else batch.RejectGeneration(cancel);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[BLOCK\\] Atomic terrain transaction rejected:"));
            batch.Advance(true); batch.Advance(true);
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(_terrain.Revision, Is.Zero);
            Assert.That(_samples.Count, Is.EqualTo(1)); Assert.That(_samples[0].Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            Assert.That(_samples[0].PublishEntered, Is.False); Assert.That(_terrain.InspectPendingWork().published, Is.Zero);
            Assert.That(_terrain.InspectPendingWork().failed, Is.EqualTo(1)); Assert.That(_terrain.InspectPendingWork().aborted, Is.Zero);
        }

        [Test]
        public void EpochCancellationOfHeldWorkIsAbortedOnceWithoutChangingPublishedWorld()
        {
            using var batch = new PendingControlledBatch(_terrain, 0);
            var before = _terrain.World;
            var epoch = (uint)PendingObservationReflection.Get(_terrain, "_epoch");
            PendingObservationReflection.Set(_terrain, "_epoch", epoch + 1);
            batch.Advance(true); batch.CompleteGeneration(); batch.Advance(true);
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(_samples.Count, Is.EqualTo(1));
            Assert.That(_samples[0].Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Aborted));
            Assert.That(_samples[0].GenerationObserved, Is.False); Assert.That(_samples[0].PublishEntered, Is.False);
            Assert.That(_terrain.InspectPendingWork().failed, Is.Zero); Assert.That(_terrain.InspectPendingWork().published, Is.Zero);
            Assert.That(_terrain.InspectPendingWork().aborted, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void PrimarySuccessExceptionPropagatesAfterCommitAndOnePublishedTerminal(bool commitCallback)
        {
            using var batch = new PendingControlledBatch(_terrain, 0);
            var error = new InvalidOperationException("primary-success-sentinel"); int calls = 0;
            Action throwPrimary = () => { calls++; throw error; };
            if (commitCallback) _terrain.PublishedCommit += _ => throwPrimary();
            else batch.AddCommand(_ => throwPrimary());
            batch.CompleteGeneration();
            var actual = Assert.Throws<InvalidOperationException>(() => batch.Advance(true));
            Assert.That(actual, Is.SameAs(error)); Assert.That(calls, Is.EqualTo(1));
            Assert.That(_terrain.World, Is.SameAs(batch.Next)); Assert.That(_terrain.Revision, Is.EqualTo(1));
            Assert.That(_terrain.InspectPendingTransaction().present, Is.False);
            Assert.That(_samples.Count, Is.EqualTo(1)); Assert.That(_samples[0].Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Published));
            batch.Advance(true); Assert.That(_samples.Count, Is.EqualTo(1));
            Assert.That(_terrain.InspectPendingWork().observerFailures, Is.Zero);
        }

        [Test]
        public void PrimaryFailureExceptionPropagatesAndRetainedHeadCannotBeMisreadAsUnfinishedSuccess()
        {
            using var batch = new PendingControlledBatch(_terrain, 0);
            var before = _terrain.World; var error = new InvalidOperationException("primary-failure-sentinel"); int calls = 0;
            batch.AddCommand(_ => { calls++; throw error; }); batch.RejectGeneration(false);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[BLOCK\\] Atomic terrain transaction rejected:"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => batch.Advance(true)), Is.SameAs(error));
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(calls, Is.EqualTo(1));
            var head = _terrain.InspectPendingTransaction();
            Assert.That(head.present && head.terminalObserved, Is.True);
            Assert.That(head.terminalOutcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            Assert.That(_samples.Count, Is.EqualTo(1));
            batch.Abort(); batch.Abort(); Assert.That(calls, Is.EqualTo(1)); Assert.That(_samples.Count, Is.EqualTo(1));
            Assert.That(_samples[0].Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            Assert.That(_terrain.InspectPendingWork().published, Is.Zero); Assert.That(_terrain.InspectPendingWork().aborted, Is.Zero);
        }

        [Test]
        public void ObserverExceptionDoesNotPreventCommitPrimaryCallbackOrPublishedCommit()
        {
            using var batch = new PendingControlledBatch(_terrain, 0);
            int callbacks = 0, commits = 0; batch.AddCommand(_ => callbacks++);
            _terrain.PublishedCommit += _ => commits++;
            Action<BlockTerrain.PendingWallDiagnostic> badObserver = _ => throw new InvalidOperationException("observer-sentinel");
            _terrain.PendingTransactionFinished += badObserver;
            try
            {
                batch.CompleteGeneration(); Assert.DoesNotThrow(() => batch.Advance(true)); batch.Advance(true);
                Assert.That(_terrain.Revision, Is.EqualTo(1)); Assert.That(callbacks, Is.EqualTo(1)); Assert.That(commits, Is.EqualTo(1));
                Assert.That(_samples.Count, Is.EqualTo(1)); Assert.That(_terrain.InspectPendingWork().published, Is.EqualTo(1));
                Assert.That(_terrain.InspectPendingWork().observerFailures, Is.EqualTo(1));
            }
            finally { _terrain.PendingTransactionFinished -= badObserver; }
        }

        [Test]
        public void PrimaryAbortExceptionPropagatesAndAnEpochAbortCannotTurnIntoPublished()
        {
            using var batch = new PendingControlledBatch(_terrain, 0);
            var before = _terrain.World; var error = new InvalidOperationException("primary-abort-sentinel"); int calls = 0;
            batch.AddCommand(_ => { calls++; throw error; });
            var epoch = (uint)PendingObservationReflection.Get(_terrain, "_epoch");
            PendingObservationReflection.Set(_terrain, "_epoch", epoch + 1);
            Assert.That(Assert.Throws<InvalidOperationException>(() => batch.Advance(true)), Is.SameAs(error));
            var head = _terrain.InspectPendingTransaction();
            Assert.That(head.present && head.terminalObserved, Is.True);
            Assert.That(head.terminalOutcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Aborted));
            batch.Abort(); batch.Abort();
            Assert.That(calls, Is.EqualTo(1)); Assert.That(_samples.Count, Is.EqualTo(1));
            Assert.That(_samples[0].Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Aborted));
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(_terrain.InspectPendingWork().published, Is.Zero);
        }

        [Test]
        public void PublicationColliderFailureRollsBackAndRecordsFailureAfterPublishEntry()
        {
            using var batch = new PendingControlledBatch(_terrain, 1);
            var before = _terrain.World; batch.AddBrokenView(); batch.CompleteGeneration(); batch.CompleteBake();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[BLOCK\\] Atomic terrain transaction rejected:"));
            batch.Advance(true);
            Assert.That(_terrain.World, Is.SameAs(before)); Assert.That(_terrain.Revision, Is.Zero);
            Assert.That(_samples.Count, Is.EqualTo(1)); var sample = _samples.Single();
            Assert.That(sample.Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            Assert.That(sample.PublishEntered, Is.True); Assert.That(_terrain.InspectPendingWork().published, Is.Zero);
        }

        [Test]
        public void UploadCounterIncrementBeforeExceptionDoesNotBecomeAnUploadCompletedSample()
        {
            using var batch = new PendingControlledBatch(_terrain, 1);
            batch.RequireOriginalUpload(); batch.CompleteGeneration();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[BLOCK\\] Atomic terrain transaction rejected:"));
            for (int i = 0; i < 32 && _samples.Count == 0; i++) batch.Advance(true);
            Assert.That(_samples.Count, Is.EqualTo(1)); var sample = _samples.Single();
            Assert.That(sample.Outcome, Is.EqualTo(BlockTerrain.PendingWallOutcome.Failed));
            Assert.That(sample.GenerationObserved, Is.True); Assert.That(sample.UploadCompleted, Is.False);
            Assert.That(sample.BakeObserved, Is.False); Assert.That(sample.PublishEntered, Is.False);
            Assert.That(_terrain.Revision, Is.Zero);
        }

        [Test]
        public void WarmupCompletionIsCensoredAndFinalReportKeepsTheUncompletedActiveHead()
        {
            using var collector = new PendingCollectorHarness(_terrain);
            using var batch = new PendingControlledBatch(_terrain, 1);
            collector.Result.pendingAtStart = _terrain.InspectPendingTransaction();
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart - 1));
            Assert.That(collector.Result.pendingCompletionSamples, Is.EqualTo(1));
            Assert.That(collector.Result.pendingCensoredCompletions, Is.EqualTo(1));
            batch.CompleteGeneration(); batch.Advance(false); // Bake is still held at report end.
            collector.WriteReport(true);
            Assert.That(collector.Result.pendingAtStart.phase, Is.EqualTo(BlockTerrain.PendingWallPhase.Generation));
            var head = collector.Result.pendingAtEnd;
            Assert.That(head.present && head.tracked, Is.True); Assert.That(head.terminalObserved, Is.False);
            Assert.That(head.id, Is.EqualTo(collector.Result.pendingAtStart.id));
            Assert.That(head.phase, Is.EqualTo(BlockTerrain.PendingWallPhase.Bake)); Assert.That(head.ageMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(collector.Result.pendingWindowEndWallTimestamp, Is.GreaterThanOrEqualTo(collector.WindowStart));
            Assert.That(PendingObservationReflection.Get(_terrain, "_pending"), Is.SameAs(batch.Pending));
            Assert.That(collector.Result.pendingRetainedCompletions, Is.Zero);
            Assert.That(collector.Result.metrics.Where(m => m.name.StartsWith("pending")).All(m => !m.available && m.samples == 0), Is.True);
            Assert.That(_samples, Is.Empty, "reporting does not force a pending terminal");
            collector.Done(); collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            Assert.That(collector.Result.pendingCompletionSamples, Is.EqualTo(1), "events after sampling end are ignored");
        }

        IEnumerator WaitReady()
        {
            for (int i = 0; i < 1000; i++)
            {
                PendingObservationReflection.Call(_terrain, "AdvancePending", false);
                if (_terrain.InspectPendingTransaction().phase == BlockTerrain.PendingWallPhase.ReadyToPublish) yield break;
                yield return null;
            }
            Assert.Fail("Pending work never reached ready-to-publish.");
        }
        static void AssertCompleteSpans(BlockTerrain.PendingWallDiagnostic sample)
        {
            Assert.That(sample.GenerationObserved && sample.UploadCompleted && sample.BakeObserved && sample.PublishEntered, Is.True);
            var spans = new[] { sample.GenerationObservedMs, sample.UploadMs, sample.BakeObservedTailMs, sample.ReadyWaitMs, sample.PublicationMs };
            Assert.That(spans.All(x => double.IsFinite(x) && x >= 0), Is.True);
            Assert.That(sample.TotalMs, Is.EqualTo(spans.Sum()).Within(.000001), "five disjoint observed spans end at commit, before primary callbacks");
        }
    }

    public sealed class BlockPendingCollectorTests
    {
        [Test]
        public void FullPartialFailedAndAbortedTerminalsUseDifferentPopulations()
        {
            using var collector = new PendingCollectorHarness(null);
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, scale: 2));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, complete: false));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, outcome: BlockTerrain.PendingWallOutcome.Failed));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, outcome: BlockTerrain.PendingWallOutcome.Aborted));
            var result = collector.Result;
            Assert.That(result.pendingCompletionSamples, Is.EqualTo(5)); Assert.That(result.pendingRetainedCompletions, Is.EqualTo(5));
            Assert.That(result.pendingPublishedCompletions, Is.EqualTo(3)); Assert.That(result.pendingFullyObservedPublishedCompletions, Is.EqualTo(2));
            Assert.That(result.pendingPartialPublishedCompletions, Is.EqualTo(1));
            Assert.That(result.pendingRetainedFullyObservedPublishedCompletions, Is.EqualTo(2));
            Assert.That(result.pendingRetainedPartialPublishedCompletions, Is.EqualTo(1));
            Assert.That(result.pendingFailedCompletions, Is.EqualTo(1)); Assert.That(result.pendingAbortedCompletions, Is.EqualTo(1));
            for (int i = 0; i < 6; i++) Assert.That(collector.Values(i).Count, Is.EqualTo(2));
            CollectionAssert.AreEqual(new double[] { 4, 8 }, collector.Values(3), "ready waits are kept separate from publication spans");
            CollectionAssert.AreEqual(new double[] { 5, 10 }, collector.Values(4));
            CollectionAssert.AreEqual(new double[] { 15, 30 }, collector.Values(5));
            Assert.That(collector.Values(6).Count, Is.EqualTo(1)); Assert.That(collector.Values(7).Count, Is.EqualTo(1));
        }

        [Test]
        public void RetainedCapAppliesToWholeRecordsAndOverflowDoesNotAlterPhasePopulationOrFormalGates()
        {
            using var collector = new PendingCollectorHarness(null);
            int cap = collector.Cap; Assert.That(cap, Is.EqualTo(16384), "approved diagnostic record budget");
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, outcome: BlockTerrain.PendingWallOutcome.Failed));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, outcome: BlockTerrain.PendingWallOutcome.Aborted));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, complete: false));
            for (int i = 3; i < cap; i++) collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            collector.WriteReport(true); string gatesBefore = JsonUtility.ToJson(new PendingCollectorHarness.GateEnvelope { gates = collector.Result.gates });
            string metricsBefore = JsonUtility.ToJson(new PendingCollectorHarness.MetricEnvelope { metrics = collector.Result.metrics.Where(m => m.name.StartsWith("pending")).ToArray() });
            int[] retainedCounts = Enumerable.Range(0, 8).Select(i => collector.Values(i).Count).ToArray();
            foreach (var outcome in new[] { BlockTerrain.PendingWallOutcome.Failed, BlockTerrain.PendingWallOutcome.Aborted, BlockTerrain.PendingWallOutcome.Published })
                collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, outcome: outcome, scale: 1000000));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart, complete: false));
            collector.WriteReport(true); var result = collector.Result;
            Assert.That(result.pendingCompletionSamples, Is.EqualTo(cap + 4)); Assert.That(result.pendingRetainedCompletions, Is.EqualTo(cap));
            Assert.That(result.pendingDroppedCompletions, Is.EqualTo(4)); Assert.That(result.pendingCensoredCompletions, Is.Zero);
            Assert.That(result.pendingFullyObservedPublishedCompletions, Is.EqualTo(cap - 2));
            Assert.That(result.pendingRetainedFullyObservedPublishedCompletions, Is.EqualTo(cap - 3));
            Assert.That(result.pendingPartialPublishedCompletions, Is.EqualTo(2)); Assert.That(result.pendingRetainedPartialPublishedCompletions, Is.EqualTo(1));
            Assert.That(result.pendingPublishedCompletions + result.pendingFailedCompletions + result.pendingAbortedCompletions,
                Is.EqualTo(result.pendingRetainedCompletions + result.pendingDroppedCompletions));
            CollectionAssert.AreEqual(retainedCounts, Enumerable.Range(0, 8).Select(i => collector.Values(i).Count).ToArray());
            for (int i = 0; i < 6; i++) Assert.That(collector.Values(i).Count, Is.EqualTo(cap - 3));
            for (int i = 0; i < 8; i++) Assert.That(collector.Values(i).Capacity, Is.EqualTo(cap), "no diagnostic growth allocation inside the sample window");
            Assert.That(result.metrics.Single(m => m.name == "pendingPublishedToCommitTotalWallMs").max, Is.EqualTo(15));
            Assert.That(result.metrics.Single(m => m.name == "pendingPublishedToCommitTotalWallMs").p95, Is.EqualTo(15));
            Assert.That(result.metrics.Single(m => m.name == "pendingPublishedToCommitTotalWallMs").p99, Is.EqualTo(15));
            Assert.That(JsonUtility.ToJson(new PendingCollectorHarness.MetricEnvelope { metrics = result.metrics.Where(m => m.name.StartsWith("pending")).ToArray() }), Is.EqualTo(metricsBefore));
            Assert.That(JsonUtility.ToJson(new PendingCollectorHarness.GateEnvelope { gates = result.gates }), Is.EqualTo(gatesBefore));
        }
    }

    static class PendingObservationReflection
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static object Get(object target, string field) => target.GetType().GetField(field, Fields).GetValue(target);
        public static void Set(object target, string field, object value) => target.GetType().GetField(field, Fields).SetValue(target, value);
        public static object Call(object target, string method, params object[] arguments)
        {
            var selected = target.GetType().GetMethods(Fields).Single(m => m.Name == method && !m.ContainsGenericParameters
                && m.GetParameters().Length == arguments.Length
                && m.GetParameters().Select((p, i) => arguments[i] == null || p.ParameterType.IsInstanceOfType(arguments[i])).All(x => x));
            try { return selected.Invoke(target, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    sealed class PendingControlledBatch : IDisposable
    {
        readonly BlockTerrain _terrain;
        readonly object _generation;
        readonly TaskCompletionSource<bool> _bake = new();
        readonly Array _pieces;
        HashSet<int> _uploadMeshIds;
        public readonly object Pending;
        public readonly BlockWorld Next;
        public PendingControlledBatch(BlockTerrain terrain, int pieces)
        {
            _terrain = terrain;
            var pendingType = typeof(BlockTerrain).GetNestedType("Pending", BindingFlags.NonPublic);
            var preparedType = typeof(BlockTerrain).GetNestedType("Prepared", BindingFlags.NonPublic);
            _pieces = Array.CreateInstance(preparedType, pieces);
            for (int i = 0; i < pieces; i++)
            {
                var piece = Activator.CreateInstance(preparedType, true);
                PendingObservationReflection.Set(piece, "Key", Vector3Int.zero);
                PendingObservationReflection.Set(piece, "Bake", _bake.Task); _pieces.SetValue(piece, i);
            }
            var completionType = typeof(TaskCompletionSource<>).MakeGenericType(_pieces.GetType());
            _generation = Activator.CreateInstance(completionType);
            Pending = Activator.CreateInstance(pendingType, true);
            PendingObservationReflection.Set(Pending, "Window", BlockPublicationPolicy.CompatibilityEight().Capture(BlockPublicationOperation.Metadata));
            Next = terrain.World.Clone(); var changed = pieces == 0 ? Array.Empty<Vector3Int>() : new[] { Vector3Int.zero };
            Next.Seal(terrain.Revision + 1, changed);
            PendingObservationReflection.Set(Pending, "Epoch", PendingObservationReflection.Get(terrain, "_epoch"));
            PendingObservationReflection.Set(Pending, "TrackWall", true);
            PendingObservationReflection.Set(Pending, "WallId", 47L);
            PendingObservationReflection.Set(Pending, "CreatedWallStamp", System.Diagnostics.Stopwatch.GetTimestamp());
            PendingObservationReflection.Set(Pending, "Next", Next); PendingObservationReflection.Set(Pending, "Changed", changed);
            PendingObservationReflection.Set(Pending, "Generate", completionType.GetProperty("Task").GetValue(_generation));
            PendingObservationReflection.Set(Pending, "Uploaded", pieces);
            // This helper represents already-uploaded pieces with a held group
            // completion/Bake. Populate the immutable handoff, without changing
            // any original test body, callback, assertion or acceptance gate.
            var taskType = typeof(Task<>).MakeGenericType(preparedType);
            var tasks = Array.CreateInstance(taskType, pieces);
            var fromResult = typeof(Task).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Single(method => method.Name == "FromResult" && method.IsGenericMethodDefinition).MakeGenericMethod(preparedType);
            for (int i = 0; i < pieces; i++) tasks.SetValue(fromResult.Invoke(null, new[] { _pieces.GetValue(i) }), i);
            PendingObservationReflection.Set(Pending, "Pieces", _pieces);
            PendingObservationReflection.Set(Pending, "Generating", tasks);
            PendingObservationReflection.Set(Pending, "PieceUploaded", Enumerable.Repeat(true, pieces).ToArray());
            PendingObservationReflection.Call(PendingObservationReflection.Get(Pending, "Scheduled"), "SetResult", tasks);
            PendingObservationReflection.Set(terrain, "_pending", Pending);
        }
        public void CompleteGeneration() => PendingObservationReflection.Call(_generation, "SetResult", _pieces);
        public void CompleteBake() => _bake.TrySetResult(true);
        public void RejectGeneration(bool canceled)
        {
            if (canceled) PendingObservationReflection.Call(_generation, "SetCanceled");
            else PendingObservationReflection.Call(_generation, "SetException", new InvalidOperationException("generation-sentinel"));
        }
        public void RejectBake(bool canceled)
        { if (canceled) _bake.TrySetCanceled(); else _bake.TrySetException(new InvalidOperationException("bake-sentinel")); }
        public void RequireOriginalUpload()
        {
            _uploadMeshIds = Resources.FindObjectsOfTypeAll<Mesh>().Select(mesh => mesh.GetInstanceID()).ToHashSet();
            PendingObservationReflection.Set(Pending, "Uploaded", 0);
            ((bool[])PendingObservationReflection.Get(Pending, "PieceUploaded"))[0] = false;
            // Nonempty triangles enter the original upload try. Missing vertices
            // fail inside Upload and reach Fail; no successful upload is recorded.
            PendingObservationReflection.Set(_pieces.GetValue(0), "Collision",
                new BlockMeshData { Triangles = new[] { 0, 1, 2 } });
        }
        public void AddBrokenView()
        {
            var view = Activator.CreateInstance(typeof(BlockTerrain).GetNestedType("View", BindingFlags.NonPublic), true);
            var obj = new GameObject("Injected missing collider for rollback"); obj.SetActive(false);
            PendingObservationReflection.Set(view, "Object", obj);
            // The original publication setter throws and executes its original rollback.
            PendingObservationReflection.Set(_pieces.GetValue(0), "View", view);
        }
        public void Advance(bool publication) => PendingObservationReflection.Call(_terrain, "AdvancePending", publication);
        public void Abort() => PendingObservationReflection.Call(_terrain, "AbortPending");
        public void AddCommand(Action<BlockMutationResult> callback)
        {
            var commandType = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var command = Activator.CreateInstance(commandType, true);
            PendingObservationReflection.Set(command, "Done", true); PendingObservationReflection.Set(command, "Callback", callback);
            var pairType = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            ((IList)PendingObservationReflection.Get(Pending, "Results")).Add(Activator.CreateInstance(pairType, command, default(BlockMutationResult)));
        }
        public void Dispose()
        {
            PendingObservationReflection.Call(_generation, "TrySetResult", _pieces); _bake.TrySetResult(true);
            if (ReferenceEquals(PendingObservationReflection.Get(_terrain, "_pending"), Pending)) Abort();
            PendingObservationReflection.Call(_terrain, "CleanupRetired");
            // Upload's object initializer may allocate a Mesh before missing
            // Vertices throws. Only clean that newly created test collision mesh.
            if (_uploadMeshIds != null)
                foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                    if (!_uploadMeshIds.Contains(mesh.GetInstanceID()) && mesh.name == "Block collision " + Vector3Int.zero)
                        Object.DestroyImmediate(mesh);
        }
    }

    // Inactive component construction initializes managed fields but suppresses
    // Awake, Screen.SetResolution, fixtures, recorders and normal benchmark boot.
    sealed class PendingCollectorHarness : IDisposable
    {
        [Serializable] public sealed class GateEnvelope { public BlockTerrainBenchmark.Gate[] gates; }
        [Serializable] public sealed class MetricEnvelope { public BlockTerrainBenchmark.Metric[] metrics; }
        readonly GameObject _object;
        readonly BlockTerrainBenchmark _benchmark;
        readonly string _output;
        public readonly BlockTerrainBenchmark.Result Result;
        public readonly long WindowStart;
        public int Cap => (int)typeof(BlockTerrainBenchmark).GetField("PendingWallRecordCap", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        public PendingCollectorHarness(BlockTerrain terrain)
        {
            _object = new GameObject("Inactive pending collector test"); _object.SetActive(false);
            _benchmark = _object.AddComponent<BlockTerrainBenchmark>();
            Result = new BlockTerrainBenchmark.Result { scenario = "observation-test", role = "host", minimumBlocks = 0, minimumRealPlayers = 0 };
            WindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Result.pendingWindowWallTimestamp = WindowStart; Result.pendingWallClockFrequency = System.Diagnostics.Stopwatch.Frequency;
            Result.pendingWallRecordCap = Cap;
            PendingObservationReflection.Set(_benchmark, "_result", Result); PendingObservationReflection.Set(_benchmark, "_terrain", terrain);
            PendingObservationReflection.Set(_benchmark, "_begin", 1.0); PendingObservationReflection.Set(_benchmark, "_pendingWindowWallStart", WindowStart);
            _output = Path.Combine(Path.GetTempPath(), "Splatoon.PendingObservationTests-" + Guid.NewGuid().ToString("N") + ".json");
            PendingObservationReflection.Set(_benchmark, "_output", _output);
            var seriesType = typeof(BlockTerrainBenchmark).GetNestedType("Series", BindingFlags.NonPublic);
            var series = (IList)PendingObservationReflection.Get(_benchmark, "_series");
            foreach (var field in new[] { "_frames", "_gpu", "_main", "_render", "_frameMain", "_frameRender", "_ordinaryLatency" })
            {
                var value = Activator.CreateInstance(seriesType, new object[] { field, 1.0, 8 });
                PendingObservationReflection.Set(_benchmark, field, value);
                if (field == "_frames" || field == "_gpu") series.Add(value);
            }
            Assert.That(_object.activeInHierarchy, Is.False);
            Assert.That(PendingObservationReflection.Get(_benchmark, "_boot"), Is.EqualTo(0.0), "Awake must not run in this harness");
        }
        public List<double> Values(int index)
        {
            var metrics = (Array)PendingObservationReflection.Get(_benchmark, "_pendingMetrics");
            return (List<double>)PendingObservationReflection.Get(metrics.GetValue(index), "Values");
        }
        public void Receive(BlockTerrain.PendingWallDiagnostic sample) => PendingObservationReflection.Call(_benchmark, "OnPendingTransaction", sample);
        int _reportWriteAttempts;
        public void WriteReport(bool final)
        {
            int attempt = ++_reportWriteAttempts;
            try { PendingObservationReflection.Call(_benchmark, "WriteReport", final); }
            catch (IOException exception)
            {
                // Preserve the original writer exception and all test assertions.
                // No reader is opened and no write/retry/cleanup is performed here.
                try
                {
                    TestContext.Error.WriteLine("[PENDING-REPORT-WRITER-IO] attempt=" + attempt
                        + " final=" + final + " type=" + exception.GetType().FullName
                        + " hresult=0x" + exception.HResult.ToString("X8")
                        + " low16=" + (exception.HResult & 0xffff)
                        + " output=" + _output + " outputExists=" + File.Exists(_output)
                        + " writingExists=" + File.Exists(_output + ".writing")
                        + " pendingFinal=" + PendingObservationReflection.Get(_benchmark, "_pendingReportFinal")
                        + " finalWritten=" + PendingObservationReflection.Get(_benchmark, "_finalReportWritten")
                        + " pendingChars=" + ((string)PendingObservationReflection.Get(_benchmark, "_pendingReport"))?.Length
                        + " nextWrite=" + PendingObservationReflection.Get(_benchmark, "_nextWrite")
                        + " message=" + exception.Message);
                }
                catch (Exception observationException)
                {
                    // Diagnostic failure must not replace the original IOException.
                    try { TestContext.Error.WriteLine("[PENDING-REPORT-WRITER-OBSERVATION-FAILED] " + observationException); }
                    catch { }
                }
                throw;
            }
        }
        public void Done() => PendingObservationReflection.Set(_benchmark, "_done", true);
        public static BlockTerrain.PendingWallDiagnostic Sample(long created, bool complete = true,
            BlockTerrain.PendingWallOutcome outcome = BlockTerrain.PendingWallOutcome.Published, double scale = 1)
            => new(outcome, 1, created, false, 1, true, true, complete, true, scale, 2 * scale, 3 * scale, 4 * scale, 5 * scale, 15 * scale);
        public void Dispose()
        {
            Done(); Object.DestroyImmediate(_object);
            if (File.Exists(_output)) File.Delete(_output);
            if (File.Exists(_output + ".writing")) File.Delete(_output + ".writing");
        }
    }
}
#endif
