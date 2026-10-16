#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    // Actual production writer, JsonUtility, filesystem locks and native marker calls.
    // JSON DTOs below only read independent diagnostic output; no writer is reimplemented.
    public sealed class BlockBenchmarkReportObservationTests
    {
        [Serializable] public sealed class Marker { public bool beginAttempted, beginReturned, endAttempted, endReturned; }
        [Serializable] public sealed class Report { public long reportId; public bool final; public string boundary; public Marker total, dto, serialization, byteCount; }
        [Serializable] public sealed class Write { public long reportId, destinationBytes, expectedUtf8PayloadBytes; public string outcome; public Marker io, lengthObservation; }
        [Serializable] public sealed class Error { public string where, exceptionType; public int hResult; }
        [Serializable] public sealed class Trace { public bool incomplete, sidecarWriteFailed; public Report[] reports; public Write[] writes; public Error[] errors; }

        sealed class Attached : IDisposable
        {
            public readonly object Benchmark, Observer;
            public readonly string Output, Sidecar;
            public Attached(PendingCollectorHarness collector)
            {
                Benchmark = PendingObservationReflection.Get(collector, "_benchmark");
                Output = (string)PendingObservationReflection.Get(collector, "_output"); Sidecar = Output + ".report-observation.json";
                Type type = Benchmark.GetType().Assembly.GetType("Splatoon.Prototype.BlockBenchmarkReportObservation", true);
                Observer = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Output }, null);
                PendingObservationReflection.Set(Benchmark, "_reportObservation", Observer);
            }
            public void Hook(Action<string> hook) => PendingObservationReflection.Set(Observer, "_faultInjectionForTests", hook);
            public Trace Read()
            {
                PendingObservationReflection.Call(Observer, "WriteOnExit", (bool)PendingObservationReflection.Get(Benchmark, "_finalReportWritten"));
                return JsonUtility.FromJson<Trace>(File.ReadAllText(Sidecar));
            }
            public bool HasExactError(Exception error) => ((Exception[])PendingObservationReflection.Get(Observer, "_errorExceptions")).Any(x => ReferenceEquals(x, error));
            public void Dispose()
            {
                PendingObservationReflection.Set(Benchmark, "_reportObservation", null);
                foreach (string file in new[] { Sidecar, Sidecar + ".write-failure.json" })
                    if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(Sidecar)) Directory.Delete(Sidecar); // Dedicated empty fixture directory only.
            }
        }
        static void Balanced(Marker marker)
        {
            if (marker == null) return;
            Assert.That(marker.endAttempted, Is.EqualTo(marker.beginReturned));
            Assert.That(marker.endReturned, Is.EqualTo(marker.endAttempted));
            Assert.That(marker.beginReturned, Is.EqualTo(marker.beginAttempted), "managed fault tests do not simulate native Begin failure");
        }
        static void Balanced(Trace trace)
        {
            foreach (var row in trace.reports ?? Array.Empty<Report>())
            { Balanced(row.total); Balanced(row.dto); Balanced(row.serialization); Balanced(row.byteCount); }
            foreach (var row in trace.writes ?? Array.Empty<Write>()) { Balanced(row.io); Balanced(row.lengthObservation); }
        }
        static void Written(PendingCollectorHarness collector, Attached attached)
        {
            Assert.That(PendingObservationReflection.Get(attached.Benchmark, "_finalReportWritten"), Is.True);
            Assert.That(PendingObservationReflection.Get(attached.Benchmark, "_pendingReport"), Is.Null);
            Assert.That(JsonUtility.FromJson<BlockTerrainBenchmark.Result>(File.ReadAllText(attached.Output)).pendingCompletionSamples, Is.EqualTo(1));
            Assert.That(collector.Result.errors, Is.Zero);
        }

        [Test]
        public void HeldRealReaderRetainsOldBytesThenRetriesOneFrozenReportWithoutReserializing()
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart)); collector.WriteReport(false);
            string previous = File.ReadAllText(attached.Output), frozen;
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            using (var reader = new FileStream(attached.Output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                collector.WriteReport(true); frozen = (string)PendingObservationReflection.Get(attached.Benchmark, "_pendingReport");
                Assert.That(File.ReadAllText(attached.Output), Is.EqualTo(previous));
                Assert.That(File.ReadAllText(attached.Output + ".writing"), Is.EqualTo(frozen));
                collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
                Assert.That(collector.Result.pendingCompletionSamples, Is.EqualTo(3));
                Assert.That(PendingObservationReflection.Get(attached.Benchmark, "_pendingReport"), Is.EqualTo(frozen));
            }
            PendingObservationReflection.Call(attached.Benchmark, "TryWriteReport");
            Assert.That(File.ReadAllText(attached.Output), Is.EqualTo(frozen));
            Assert.That(JsonUtility.FromJson<BlockTerrainBenchmark.Result>(frozen).pendingCompletionSamples, Is.EqualTo(2));
            Trace trace = attached.Read(); Assert.That(trace.incomplete, Is.False); Assert.That(trace.reports.Length, Is.EqualTo(2));
            long finalId = trace.reports.Single(x => x.final).reportId;
            var writes = trace.writes.Where(x => x.reportId == finalId).ToArray();
            Assert.That(writes.Select(x => x.outcome), Is.EqualTo(new[] { "retryable-io", "success" }));
            Assert.That(writes[1].destinationBytes, Is.EqualTo(new FileInfo(attached.Output).Length));
            Assert.That(writes[1].expectedUtf8PayloadBytes, Is.EqualTo(new FileInfo(attached.Output).Length)); Balanced(trace);
        }

        [TestCase("begin_report.before")]
        [TestCase("begin_report.after")]
        [TestCase("begin_serialization.before")]
        [TestCase("begin_serialization.after")]
        [TestCase("end_serialization")]
        [TestCase("serialized")]
        [TestCase("begin_write.before")]
        [TestCase("begin_write.after")]
        [TestCase("write.dispose")]
        [TestCase("destination_length")]
        [TestCase("report.dispose")]
        public void ObservationFaultCannotChangeTheActualSuccessfulProductionWrite(string point)
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            var sentinel = new InvalidOperationException("observer fault at " + point);
            attached.Hook(where => { if (where == point) throw sentinel; });
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            Assert.DoesNotThrow(() => collector.WriteReport(true)); Written(collector, attached);
            Assert.That(attached.HasExactError(sentinel), Is.True); Trace trace = attached.Read();
            Assert.That(trace.incomplete, Is.True); Balanced(trace);
            Assert.That(PendingObservationReflection.Get(attached.Observer, "_activeReport"), Is.Null);
            Assert.That(PendingObservationReflection.Get(attached.Observer, "_activeWrite"), Is.Null);
        }

        [TestCase("write.failed")]
        [TestCase("report.failed")]
        [TestCase("write.dispose")]
        [TestCase("report.dispose")]
        public void ObservationFaultCannotMaskARealTerminalDirectoryIoException(string point)
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            var sentinel = new InvalidOperationException("observer fault at " + point);
            attached.Hook(where => { if (where == point) throw sentinel; });
            string parentFile = attached.Output + ".parent-file"; File.WriteAllText(parentFile, "real fixture parent file");
            try
            {
                PendingObservationReflection.Set(attached.Benchmark, "_output", Path.Combine(parentFile, "report.json"));
                IOException original = Assert.Throws<IOException>(() => collector.WriteReport(true));
                Assert.That(original, Is.Not.SameAs(sentinel)); Assert.That(attached.HasExactError(sentinel), Is.True);
                Assert.That(File.ReadAllText(parentFile), Is.EqualTo("real fixture parent file"));
                Assert.That(PendingObservationReflection.Get(attached.Benchmark, "_finalReportWritten"), Is.False);
                Assert.That(PendingObservationReflection.Get(attached.Benchmark, "_pendingReport"), Is.Not.Null);
                Assert.That(collector.Result.errors, Is.Zero); Trace trace = attached.Read(); Assert.That(trace.incomplete, Is.True); Balanced(trace);
            }
            finally { PendingObservationReflection.Set(attached.Benchmark, "_output", attached.Output); File.Delete(parentFile); }
        }

        [Test]
        public void ObservationFailureInsideTheRealLockRetryDoesNotDiscardTheFrozenJson()
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart)); collector.WriteReport(false);
            string previous = File.ReadAllText(attached.Output), frozen;
            var sentinel = new InvalidOperationException("observer retry handler fault");
            attached.Hook(where => { if (where == "write.failed") throw sentinel; });
            using (var reader = new FileStream(attached.Output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.DoesNotThrow(() => collector.WriteReport(true));
                frozen = (string)PendingObservationReflection.Get(attached.Benchmark, "_pendingReport");
                Assert.That(frozen, Is.Not.Null); Assert.That(File.ReadAllText(attached.Output), Is.EqualTo(previous));
                Assert.That((double)PendingObservationReflection.Get(attached.Benchmark, "_nextWrite"), Is.GreaterThan(0));
            }
            Assert.DoesNotThrow(() => PendingObservationReflection.Call(attached.Benchmark, "TryWriteReport"));
            Assert.That(File.ReadAllText(attached.Output), Is.EqualTo(frozen)); Written(collector, attached);
            Assert.That(attached.HasExactError(sentinel), Is.True); Trace trace = attached.Read(); Assert.That(trace.incomplete, Is.True); Balanced(trace);
        }

        [TestCase("begin_report.after")]
        [TestCase("begin_write.after")]
        public void ReentrantObserverIsRefusedAndTheRealWriterStillCompletes(string point)
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            attached.Hook(where =>
            {
                if (where != point) return;
                if (point == "begin_report.after") PendingObservationReflection.Call(attached.Observer, "BeginReport", true, "reentry", 1, 1.0, 0.0, false, 0, 0);
                else PendingObservationReflection.Call(attached.Observer, "BeginWrite", "reentry", 1, 1.0, 0.0, false);
            });
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            Assert.DoesNotThrow(() => collector.WriteReport(true)); Written(collector, attached);
            Trace trace = attached.Read(); Assert.That(trace.incomplete, Is.True); Balanced(trace);
        }

        [Test]
        public void ARealSidecarCreateNewFailureLeavesTheFormalReportExactAndRecordsTheDiagnosticFailure()
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart)); collector.WriteReport(true);
            byte[] formalBytes = File.ReadAllBytes(attached.Output); Directory.CreateDirectory(attached.Sidecar);
            Assert.DoesNotThrow(() => PendingObservationReflection.Call(attached.Observer, "WriteOnExit", true));
            Assert.That(File.ReadAllBytes(attached.Output), Is.EqualTo(formalBytes)); Written(collector, attached);
            var failure = JsonUtility.FromJson<Trace>(File.ReadAllText(attached.Sidecar + ".write-failure.json"));
            Assert.That(failure.incomplete, Is.True); Assert.That(failure.sidecarWriteFailed, Is.True);
            Assert.That(failure.errors.Any(x => x.where == "sidecar.write"), Is.True);
        }

        [Test]
        public void ActualReportWritesIdentifyWaitingWarmupSamplingAndClosedSamplingSeparately()
        {
            using var collector = new PendingCollectorHarness(null); using var attached = new Attached(collector);
            collector.Receive(PendingCollectorHarness.Sample(collector.WindowStart));
            PendingObservationReflection.Set(attached.Benchmark, "_begin", -1.0);
            PendingObservationReflection.Set(attached.Benchmark, "_ready", -1.0); collector.WriteReport(false);
            PendingObservationReflection.Set(attached.Benchmark, "_ready", 0.0); collector.WriteReport(false);
            PendingObservationReflection.Set(attached.Benchmark, "_begin", 1.0); collector.WriteReport(false);
            collector.Done(); collector.WriteReport(true); Written(collector, attached);
            Trace trace = attached.Read(); Assert.That(trace.incomplete, Is.False);
            Assert.That(trace.reports.Select(x => x.boundary), Is.EqualTo(new[] { "waiting", "warmup", "during_sampling", "after_sampling" })); Balanced(trace);
        }
    }
}
#endif
