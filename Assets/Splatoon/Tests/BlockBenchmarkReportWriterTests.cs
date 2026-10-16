#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockBenchmarkReportWriterTests
    {
        [Test]
        public void HeldReaderKeepsOldReportAndReleasedRetryWritesTheExactFrozenFinalJson()
        {
            using var collector = new PendingCollectorHarness(null);
            var benchmark = PendingObservationReflection.Get(collector, "_benchmark");
            string output = (string)PendingObservationReflection.Get(collector, "_output");
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            collector.WriteReport(false);
            string previous = File.ReadAllText(output);
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            string frozen;
            using (var reader = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                collector.WriteReport(true); // Actual production Replace, with delete sharing denied.
                frozen = (string)PendingObservationReflection.Get(benchmark, "_pendingReport");
                Assert.That(frozen, Is.Not.Null.And.Not.Empty);
                Assert.That(PendingObservationReflection.Get(benchmark, "_pendingReportFinal"), Is.True);
                Assert.That(PendingObservationReflection.Get(benchmark, "_finalReportWritten"), Is.False);
                Assert.That(File.ReadAllText(output), Is.EqualTo(previous), "the previous complete report stays readable");
                Assert.That(File.ReadAllText(output + ".writing"), Is.EqualTo(frozen));
                Assert.That(JsonUtility.FromJson<BlockTerrainBenchmark.Result>(frozen).pendingCompletionSamples, Is.EqualTo(2));

                // Retry must not recalculate the already closed report population.
                collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
                Assert.That(collector.Result.pendingCompletionSamples, Is.EqualTo(3));
                Assert.That(PendingObservationReflection.Get(benchmark, "_pendingReport"), Is.EqualTo(frozen));
            }
            PendingObservationReflection.Call(benchmark, "TryWriteReport");
            Assert.That(File.ReadAllText(output), Is.EqualTo(frozen));
            Assert.That(JsonUtility.FromJson<BlockTerrainBenchmark.Result>(File.ReadAllText(output)).pendingCompletionSamples, Is.EqualTo(2));
            Assert.That(PendingObservationReflection.Get(benchmark, "_pendingReport"), Is.Null);
            Assert.That(PendingObservationReflection.Get(benchmark, "_finalReportWritten"), Is.True);
            Assert.That(File.Exists(output + ".writing"), Is.False);
        }

        [Test]
        public void UnrelatedDirectoryIoFailurePropagatesAndCannotAppearFinallyWritten()
        {
            using var collector = new PendingCollectorHarness(null);
            var benchmark = PendingObservationReflection.Get(collector, "_benchmark");
            string original = (string)PendingObservationReflection.Get(collector, "_output");
            string parentFile = original + ".parent-file";
            File.WriteAllText(parentFile, "dedicated fixture file, not a directory");
            try
            {
                PendingObservationReflection.Set(benchmark, "_output", Path.Combine(parentFile, "report.json"));
                Assert.Throws<IOException>(() => collector.WriteReport(true));
                Assert.That(PendingObservationReflection.Get(benchmark, "_finalReportWritten"), Is.False);
                Assert.That(PendingObservationReflection.Get(benchmark, "_pendingReport"), Is.Not.Null);
                Assert.That(File.ReadAllText(parentFile), Is.EqualTo("dedicated fixture file, not a directory"));
                Assert.That(File.Exists(original), Is.False);
            }
            finally
            {
                PendingObservationReflection.Set(benchmark, "_output", original);
                File.Delete(parentFile);
            }
        }
    }
}
#endif
