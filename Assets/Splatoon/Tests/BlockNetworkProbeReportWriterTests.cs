#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Prototype;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Real JsonUtility and production Write; inactive collector, no session or socket.
    public sealed class BlockNetworkProbeReportWriterTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static Type Nested(string name) => typeof(BlockNetworkProbe).GetNestedType(name, BindingFlags.NonPublic);
        static object Get(object value, string name) => value.GetType().GetField(name, Fields | Private).GetValue(value);
        static void Set(object value, string name, object field) => value.GetType().GetField(name, Fields | Private).SetValue(value, field);
        static IList Rows(object value, string name) => (IList)Get(value, name);
        static object New(string name) => Activator.CreateInstance(Nested(name), true);
        static object Read(string json) => JsonUtility.FromJson(json, Nested("Report"));
        static string Canonical(string json) => JsonUtility.ToJson(Read(json));

        static object Invoke(MethodInfo method, object owner, params object[] arguments)
        {
            try { return method.Invoke(owner, arguments); }
            catch (TargetInvocationException error)
            { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        static void Stream(TextWriter writer, object report) => Invoke(typeof(BlockNetworkProbe).GetMethod(
            "WriteReportJson", BindingFlags.Static | BindingFlags.NonPublic), null, writer, report);

        static void FillScalars(object value, int seed)
        {
            int ordinal = seed;
            foreach (var field in value.GetType().GetFields(Fields))
            {
                ordinal++;
                var type = field.FieldType;
                if (type == typeof(bool)) field.SetValue(value, true);
                else if (type == typeof(int)) field.SetValue(value, ordinal);
                else if (type == typeof(uint)) field.SetValue(value, (uint)ordinal);
                else if (type == typeof(ulong)) field.SetValue(value, ulong.MaxValue - (ulong)ordinal);
                else if (type == typeof(long)) field.SetValue(value, 9007199254740993L + ordinal);
                else if (type == typeof(byte)) field.SetValue(value, (byte)(ordinal % 250 + 1));
                else if (type == typeof(float)) field.SetValue(value, ordinal + .25f);
                else if (type == typeof(double)) field.SetValue(value, ordinal + .125);
                else if (type == typeof(string)) field.SetValue(value, field.Name + "中文\\\"\n\r\t\b\f\0\u001f");
                else if (type == typeof(Vector3)) field.SetValue(value, new Vector3(ordinal, -ordinal, .25f));
                else if (type == typeof(Vector3Int)) field.SetValue(value, new Vector3Int(ordinal, -ordinal, 3));
                else if (type == typeof(Quaternion)) field.SetValue(value, new Quaternion(.1f, .2f, .3f, .4f));
            }
        }
        static T Sample<T>(int seed) where T : class
        { var value = JsonUtility.FromJson<T>("{}"); FillScalars(value, seed); return value; }

        static object Population(int count)
        {
            var report = New("Report"); FillScalars(report, 101);
            Set(report, "current", Sample<BlockNetworkProbe.TerrainSample>(501));
            var fall = New("FallCoverageReport"); FillScalars(fall, 301);
            Set(fall, "drainState", Sample<BlockNetworkProbe.TerrainSample>(701));
            Rows(fall, "drainInputs").Add(Sample<BlockNetworkProbe.DrainInputSample>(801));
            var command = New("FallCommandSample"); FillScalars(command, 901); Rows(fall, "commands").Add(command);
            var publication = New("FallPublishSample"); FillScalars(publication, 1001);
            var cell = New("FallCellSample"); FillScalars(cell, 1101); Rows(publication, "cells").Add(cell);
            Rows(fall, "publications").Add(publication); Set(report, "fallCoverage", fall);
            Rows(report, "states").Add(Sample<BlockNetworkProbe.TerrainSample>(1201));
            Rows(report, "states").Add(Sample<BlockNetworkProbe.TerrainSample>(1301));
            Rows(report, "corrections").Add(Sample<BlockNetworkProbe.CorrectionSample>(1401));
            for (int index = 0; index < count; index++)
            {
                var row = Sample<BlockNetworkProbe.ReconcileEventSample>(index + 1501);
                row.eventId = 9007199254740993L + index;
                row.oldPredicted = Sample<BlockNetworkProbe.ReconcileStateSample>(index + 2501);
                row.authority = Sample<BlockNetworkProbe.ReconcileStateSample>(index + 3501);
                row.replayed = Sample<BlockNetworkProbe.ReconcileStateSample>(index + 4501);
                Rows(report, "reconcileEvents").Add(row);
            }
            var recovery = Sample<BlockNetworkProbe.SafetyRecoveryEventSample>(5501);
            recovery.before = Sample<BlockNetworkProbe.ReconcileStateSample>(5601);
            recovery.after = Sample<BlockNetworkProbe.ReconcileStateSample>(5701);
            Rows(report, "safetyRecoveryEvents").Add(recovery);
            var motor = Sample<BlockNetworkProbe.MotorBranchEventSample>(5801);
            motor.before = Sample<BlockNetworkProbe.MotorStateSample>(5901);
            motor.after = Sample<BlockNetworkProbe.MotorStateSample>(6001);
            Rows(report, "motorBranchEvents").Add(motor);
            foreach (string name in new[] { "motorBranchErrorMessages", "errorMessages" })
            { Rows(report, name).Add("中文\\\"\n\r\t\b\f\0\u001f"); Rows(report, name).Add(""); Rows(report, name).Add(null); }
            Set(report, "reconcileEventCount", count);
            Set(report, "safetyRecoveryEventCount", 1); Set(report, "motorBranchEventCount", 1);
            return report;
        }

        sealed class Harness : IDisposable
        {
            internal readonly object Report;
            internal readonly BlockNetworkProbe Probe;
            internal readonly string DirectoryPath, Output;
            readonly GameObject _owner;
            internal Harness(int count = 17)
            {
                DirectoryPath = Path.Combine(Path.GetTempPath(), "BlockNetworkProbeWriter-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DirectoryPath); Output = Path.Combine(DirectoryPath, "report.json");
                _owner = new GameObject("Inactive report writer fixture"); _owner.SetActive(false);
                Probe = _owner.AddComponent<BlockNetworkProbe>(); Report = Population(count);
                Set(Probe, "_report", Report); Set(Probe, "_output", Output);
                Set(Probe, "_motorBranchProviderBaseline", PlayerMotorSimulation.MotorBranchObserverErrors);
            }
            internal bool Write() => (bool)Invoke(typeof(BlockNetworkProbe).GetMethod("Write", Private), Probe);
            public void Dispose() { Object.DestroyImmediate(_owner); Directory.Delete(DirectoryPath, true); }
        }

        [Test]
        public void MetadataCoversEveryReportFieldWithItsOriginalType()
        {
            var names = Nested("ReportJsonMetadata").GetFields(Fields).ToDictionary(field => field.Name, field => field.FieldType);
            foreach (var field in Nested("Report").GetFields(Fields).Where(field => field.Name == "fallCoverage" || typeof(IList).IsAssignableFrom(field.FieldType)))
                names.Add(field.Name, field.FieldType);
            Assert.That(names.Keys, Is.EquivalentTo(Nested("Report").GetFields(Fields).Select(field => field.Name)));
            foreach (var field in Nested("Report").GetFields(Fields)) Assert.That(names[field.Name], Is.EqualTo(field.FieldType), field.Name);
            var fallNames = Nested("FallCoverageJsonMetadata").GetFields(Fields).ToDictionary(field => field.Name, field => field.FieldType);
            foreach (var field in Nested("FallCoverageReport").GetFields(Fields).Where(field => typeof(IList).IsAssignableFrom(field.FieldType)))
                fallNames.Add(field.Name, field.FieldType);
            Assert.That(fallNames.Keys, Is.EquivalentTo(Nested("FallCoverageReport").GetFields(Fields).Select(field => field.Name)));
            foreach (var field in Nested("FallCoverageReport").GetFields(Fields)) Assert.That(fallNames[field.Name], Is.EqualTo(field.FieldType), field.Name);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void ProductionWriteRetainsLegacyJsonValuesEveryRowAndAllThreeStates(bool final, bool motor)
        {
            using var fixture = new Harness(257);
            foreach (var field in Nested("Report").GetFields(Fields).Where(field => field.FieldType == typeof(bool)))
                field.SetValue(fixture.Report, final);
            Set(fixture.Probe, "_finished", final); Set(fixture.Probe, "_motorTraceEnabled", motor);
            Set(fixture.Report, "motorTraceEnabled", motor); Set(fixture.Report, "complete", final);
            var events = Rows(fixture.Report, "reconcileEvents");
            var recovery = Rows(fixture.Report, "safetyRecoveryEvents");
            var branches = Rows(fixture.Report, "motorBranchEvents");
            var frozen = events.Cast<object>().Select(JsonUtility.ToJson).ToArray();
            Assert.That(fixture.Write(), Is.True);
            string actual = File.ReadAllText(fixture.Output);
            Assert.That(Rows(fixture.Report, "reconcileEvents"), Is.SameAs(events));
            Assert.That(Rows(fixture.Report, "safetyRecoveryEvents"), Is.SameAs(recovery));
            Assert.That(Rows(fixture.Report, "motorBranchEvents"), Is.SameAs(branches));
            Assert.That(events.Cast<object>().Select(JsonUtility.ToJson), Is.EqualTo(frozen), "Writing cannot mutate captured states");
            string expected;
            try
            {
                if (!final) { Set(fixture.Report, "reconcileEvents", null); Set(fixture.Report, "safetyRecoveryEvents", null); }
                if (!final || !motor) Set(fixture.Report, "motorBranchEvents", null);
                expected = JsonUtility.ToJson(fixture.Report);
            }
            finally
            { Set(fixture.Report, "reconcileEvents", events); Set(fixture.Report, "safetyRecoveryEvents", recovery); Set(fixture.Report, "motorBranchEvents", branches); }
            Assert.That(Canonical(actual), Is.EqualTo(Canonical(expected)), "Legacy FromJson schema, values, native escaping and list order must match");
            var decoded = Read(actual);
            Assert.That(Get(decoded, "reconcileEventsIncluded"), Is.EqualTo(final));
            Assert.That(Get(decoded, "motorBranchEventsIncluded"), Is.EqualTo(final && motor));
            Assert.That(Rows(decoded, "reconcileEvents").Count, Is.EqualTo(final ? 257 : 0));
            if (final)
                for (int index = 0; index < 257; index++)
                {
                    var row = (BlockNetworkProbe.ReconcileEventSample)Rows(decoded, "reconcileEvents")[index];
                    Assert.That(row.eventId, Is.EqualTo(9007199254740993L + index));
                    Assert.That(JsonUtility.ToJson(row), Is.EqualTo(frozen[index]));
                }
            // Null inline FallCoverageReport follows the original Unity serializer too.
            Set(fixture.Report, "fallCoverage", null);
            using var streamed = new StringWriter(); Stream(streamed, fixture.Report);
            Assert.That(Canonical(streamed.ToString()), Is.EqualTo(Canonical(JsonUtility.ToJson(fixture.Report))));
        }

        [Test]
        public void HeldReaderRetainsPreviousReportAndReleasedRetryPublishesTheFullPopulation()
        {
            using var fixture = new Harness(65); Assert.That(fixture.Write(), Is.True);
            string previous = File.ReadAllText(fixture.Output);
            Set(fixture.Probe, "_finished", true); Set(fixture.Report, "complete", true);
            object waiting;
            using (var reader = new FileStream(fixture.Output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(fixture.Write(), Is.False);
                Assert.That(File.ReadAllText(fixture.Output), Is.EqualTo(previous));
                waiting = Read(File.ReadAllText(fixture.Output + ".writing"));
                Assert.That(Rows(waiting, "reconcileEvents").Count, Is.EqualTo(65));
                Assert.That(Get(waiting, "reconcileEventsIncluded"), Is.True);
            }
            Assert.That(fixture.Write(), Is.True);
            var published = Read(File.ReadAllText(fixture.Output));
            Set(waiting, "duration", Get(published, "duration")); // Original retry refreshes wall-clock duration.
            Assert.That(JsonUtility.ToJson(published), Is.EqualTo(JsonUtility.ToJson(waiting)));
            Assert.That(File.Exists(fixture.Output + ".writing"), Is.False);
        }

        [Test]
        public void SerializationAndDirectoryFailuresCannotReplacePreviousReport()
        {
            using var fixture = new Harness(); Assert.That(fixture.Write(), Is.True);
            string previous = File.ReadAllText(fixture.Output); Set(fixture.Probe, "_finished", true);
            var events = Rows(fixture.Report, "reconcileEvents"); events.Add(null);
            Assert.Throws<InvalidOperationException>(() => fixture.Write());
            Assert.That(File.ReadAllText(fixture.Output), Is.EqualTo(previous));
            Assert.That(Rows(fixture.Report, "reconcileEvents"), Is.SameAs(events));
            events.RemoveAt(events.Count - 1);
            string parentFile = Path.Combine(fixture.DirectoryPath, "parent-file"); File.WriteAllText(parentFile, "retain");
            Set(fixture.Probe, "_output", Path.Combine(parentFile, "report.json"));
            Assert.That(fixture.Write(), Is.False);
            Assert.That(File.ReadAllText(parentFile), Is.EqualTo("retain"));
            Assert.That(File.ReadAllText(fixture.Output), Is.EqualTo(previous));
            Assert.That(Rows(fixture.Report, "reconcileEvents"), Is.SameAs(events));
        }

        sealed class BoundedSink : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            internal long Characters;
            public override void Write(char value) => Characters++;
            public override void Write(string value)
            { Assert.That(value.Length, Is.LessThanOrEqualTo(16384), "An entire event array/report must not be one serialization write"); Characters += value.Length; }
        }
        [Test]
        public void LargePopulationStreamsBoundedWritesWithoutRetainingOutputJson()
        {
            var report = Population(1025); using var sink = new BoundedSink(); Stream(sink, report);
            Assert.That(sink.Characters, Is.GreaterThan(1025L * 1000));
            Assert.That(Rows(report, "reconcileEvents").Count, Is.EqualTo(1025));
        }
    }
}
#endif
