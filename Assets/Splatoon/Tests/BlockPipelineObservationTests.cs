#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Arithmetic and collector contract only: synthetic stamps are deliberately
    // labelled and do not represent Unity/Player generation, Upload or Bake speed.
    public sealed class BlockPipelineObservationTests
    {
        static Task ArrayResult(Array pieces)
        {
            var fromResult = typeof(Task).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Single(method => method.Name == "FromResult" && method.IsGenericMethodDefinition).MakeGenericMethod(pieces.GetType());
            return (Task)fromResult.Invoke(null, new object[] { pieces });
        }
        static BlockTerrain.PendingPipelineDiagnostic Observe(object pending)
            => (BlockTerrain.PendingPipelineDiagnostic)PipelineReflection.CallStatic("ObservePipeline", pending);
        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        [Test] public void OverlayRequiresEveryActualCompletionAndKeepsWindowsSeparateFromElapsedSums()
        {
            object pending = WorkerObservationSynthetic.Pending();
            var pieces = (Array)PipelineReflection.Get(pending, "Pieces");
            PipelineReflection.Set(pending, "TrackWall", true);
            PipelineReflection.Set(pending, "Generate", ArrayResult(pieces));
            PipelineReflection.Set(pending, "UploadCompleteWallStamp", 5600L);
            PipelineReflection.Set(pending, "BakeObservedWallStamp", 6100L);
            PipelineReflection.Set(pending, "UploadedBeforeGroupCompletion", true);
            long[][] stamps = { new long[] { 3000, 4000, 3500, 5100 }, new long[] { 4500, 5500, 4700, 6000 } };
            for (int i = 0; i < pieces.Length; i++)
            {
                object piece = pieces.GetValue(i);
                PipelineReflection.Set(piece, "UploadStartStamp", stamps[i][0]); PipelineReflection.Set(piece, "UploadEndStamp", stamps[i][1]);
                PipelineReflection.Set(piece, "BakeStartStamp", stamps[i][2]); PipelineReflection.Set(piece, "BakeEndStamp", stamps[i][3]);
                PipelineReflection.Set(piece, "Bake", Task.CompletedTask);
            }
            object first = pieces.GetValue(0); var held = new TaskCompletionSource<bool>();
            try
            {
                PipelineReflection.Set(first, "Bake", held.Task);
                Assert.That(Observe(pending).Available, Is.False, "unfinished Bake stamps cannot be sampled");
                var fault = Task.FromException(new InvalidOperationException("overlay-fault-sentinel"));
                PipelineReflection.Set(first, "Bake", fault); Assert.That(Observe(pending).Available, Is.False); _ = fault.Exception;
                var canceled = new TaskCompletionSource<bool>(); canceled.SetCanceled();
                PipelineReflection.Set(first, "Bake", canceled.Task); Assert.That(Observe(pending).Available, Is.False);
                PipelineReflection.Set(first, "Bake", Task.CompletedTask);
                var sample = Observe(pending); Assert.That(sample.Available && sample.UploadedBeforeGroupCompletion, Is.True);
                Assert.That(sample.FirstUploadFromCreatedMs, Is.EqualTo(Ms(2000)).Within(1e-9));
                Assert.That(sample.UploadPieceActiveElapsedMs, Is.EqualTo(Ms(2000)).Within(1e-9));
                Assert.That(sample.BakeActiveElapsedMs, Is.EqualTo(Ms(2900)).Within(1e-9));
                Assert.That(sample.UploadGenerationOverlapWindowMs, Is.EqualTo(Ms(1800)).Within(1e-9));
                Assert.That(sample.BakeGenerationOverlapWindowMs, Is.EqualTo(Ms(1300)).Within(1e-9));
                Assert.That(sample.UploadGenerationOverlapWindowMs, Is.Not.EqualTo(sample.UploadPieceActiveElapsedMs),
                    "first-to-last window can include gaps; its name must remain a window");
                PipelineReflection.Set(pending, "GenerationObservedWallStamp", 0L); Assert.That(Observe(pending).Available, Is.False);
                PipelineReflection.Set(pending, "GenerationObservedWallStamp", 5300L);
                PipelineReflection.Set(pending, "BakeObservedWallStamp", 0L); Assert.That(Observe(pending).Available, Is.False);
                PipelineReflection.Set(pending, "BakeObservedWallStamp", 6100L);
                PipelineReflection.Set(pending, "TrackWall", false); Assert.That(Observe(pending).Available, Is.False);
            }
            finally { held.TrySetResult(true); }
        }

        static BlockTerrain.PendingWallDiagnostic WithPipeline(BlockTerrain.PendingWallDiagnostic old,
            BlockTerrain.PendingPipelineDiagnostic pipeline)
            => new(old.Outcome, old.Id, old.CreatedWallTimestamp, old.Remote, old.Pieces,
                old.GenerationObserved, old.UploadCompleted, old.BakeObserved, old.PublishEntered,
                old.GenerationObservedMs, old.UploadMs, old.BakeObservedTailMs, old.ReadyWaitMs,
                old.PublicationMs, old.TotalMs, old.Worker, pipeline);
        static double[] Values(object series) => ((System.Collections.Generic.List<double>)PipelineReflection.Get(series, "Values")).ToArray();

        [Test] public void CollectorRetainsSameFullSuccessPopulationCapsWholeRecordsAndSerializesExplicitSemantics()
        {
            using var collector = new PendingCollectorHarness(null);
            object benchmark = PipelineReflection.Get(collector, "_benchmark");
            var metrics = (Array)PipelineReflection.Get(benchmark, "_pendingPipelineMetrics");
            var pipeline = new BlockTerrain.PendingPipelineDiagnostic(true, 2, 3, 4, 1, 2);
            collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart - 1), pipeline));
            collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart), pipeline));
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart)); // missing overlay remains explicit
            collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart, complete: false), pipeline));
            collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart, outcome: BlockTerrain.PendingWallOutcome.Failed), pipeline));
            collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart, outcome: BlockTerrain.PendingWallOutcome.Aborted), pipeline));
            Assert.That(collector.Result.pendingPipelineAvailableCompletions, Is.EqualTo(1));
            Assert.That(collector.Result.pendingPipelineMissingCompletions, Is.EqualTo(1));
            Assert.That(collector.Result.pendingPipelineEarlyUploadCompletions, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { 2.0, 3.0, 4.0, 1.0, 2.0 }, metrics.Cast<object>().Select(series => Values(series).Single()).ToArray());
            Assert.That(collector.Result.pendingRetainedFullyObservedPublishedCompletions,
                Is.EqualTo(collector.Result.pendingPipelineAvailableCompletions + collector.Result.pendingPipelineMissingCompletions));
            for (int i = 5; i < collector.Cap; i++)
                collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart), pipeline));
            collector.WriteReport(true);
            string gates = JsonUtility.ToJson(new PendingCollectorHarness.GateEnvelope { gates = collector.Result.gates });
            string observed = JsonUtility.ToJson(new PendingCollectorHarness.MetricEnvelope { metrics = collector.Result.metrics.Where(metric => metric.name.StartsWith("pending")).ToArray() });
            long available = collector.Result.pendingPipelineAvailableCompletions;
            long missing = collector.Result.pendingPipelineMissingCompletions;
            long early = collector.Result.pendingPipelineEarlyUploadCompletions;
            foreach (var outcome in new[] { BlockTerrain.PendingWallOutcome.Published, BlockTerrain.PendingWallOutcome.Failed, BlockTerrain.PendingWallOutcome.Aborted })
                collector.Receive(WithPipeline(PendingCollectorHarness.Sample(collector.WindowStart, outcome: outcome, scale: 1000000),
                    new BlockTerrain.PendingPipelineDiagnostic(true, 2000000, 3000000, 4000000, 1000000, 2000000)));
            collector.WriteReport(true);
            Assert.That(collector.Result.pendingDroppedCompletions, Is.EqualTo(3));
            Assert.That(collector.Result.pendingPipelineAvailableCompletions, Is.EqualTo(available));
            Assert.That(collector.Result.pendingPipelineMissingCompletions, Is.EqualTo(missing));
            Assert.That(collector.Result.pendingPipelineEarlyUploadCompletions, Is.EqualTo(early));
            Assert.That(JsonUtility.ToJson(new PendingCollectorHarness.MetricEnvelope { metrics = collector.Result.metrics.Where(metric => metric.name.StartsWith("pending")).ToArray() }), Is.EqualTo(observed));
            Assert.That(JsonUtility.ToJson(new PendingCollectorHarness.GateEnvelope { gates = collector.Result.gates }), Is.EqualTo(gates));
            foreach (var series in metrics)
                Assert.That(((System.Collections.Generic.List<double>)PipelineReflection.Get(series, "Values")).Capacity, Is.EqualTo(collector.Cap));
            string json = JsonUtility.ToJson(collector.Result);
            StringAssert.Contains("pendingPhaseWallSemantics", json); StringAssert.Contains("not complete activity", json);
            StringAssert.Contains("pendingPipelineOverlaySemantics", json); StringAssert.Contains("not CPU time", json);
            Assert.That(collector.Result.metrics.Single(metric => metric.name == "pendingPipelineBakePieceElapsedSumMs").p99, Is.EqualTo(4));
            Assert.That(collector.Result.metrics.Single(metric => metric.name == "pendingPipelineUploadGenerationOverlapWindowMs").p99, Is.EqualTo(1));
            // The original disjoint total still uses its original sample and population.
            Assert.That(collector.Result.metrics.Single(metric => metric.name == "pendingPublishedToCommitTotalWallMs").p99, Is.EqualTo(15));
        }
    }
}
#endif
