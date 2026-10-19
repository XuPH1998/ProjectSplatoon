#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using NUnit.Framework;
using Splatoon.Prototype;
using UnityEngine;
using FrameRecord = Splatoon.Prototype.BlockFixedGridAcceptanceProbe.FrameRecord;

namespace Splatoon.Tests
{
    /// <summary>Reports-only candidate; root owns installation and serialized execution.</summary>
    public sealed class BlockProbeReportSnapshot67Tests
    {
        string _directory;
        static readonly Type ReportType = typeof(BlockFixedGridAcceptanceProbe).GetNestedType("Report", BindingFlags.NonPublic);
        static readonly MethodInfo CaptureStatistics = typeof(BlockProbeReportSnapshot).GetMethod("CaptureWithStatistics", BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp] public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "BlockProbeReportSnapshot67-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }
        [TearDown] public void TearDown()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsStatic && !f.IsLiteral && !f.IsInitOnly && !f.IsNotSerialized).ToArray();
        static bool Scalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(decimal);
        static object Uninitialized(Type type) => FormatterServices.GetUninitializedObject(type);

        // Populate every serializable field in the current actual Report, without
        // running its initializers (notably the 32768-row FrameRecord allocation).
        static object Populate(Type type, int seed, HashSet<Type> declarations)
        {
            if (type == typeof(string)) return "snapshot / 积木 \"quoted\" \\ \n " + seed;
            if (type == typeof(bool)) return true;
            if (type == typeof(byte)) return (byte)(1 + seed % 250);
            if (type == typeof(sbyte)) return (sbyte)(-1 - seed % 100);
            if (type == typeof(short)) return (short)(-1 - seed % 30000);
            if (type == typeof(ushort)) return (ushort)(1 + seed % 60000);
            if (type == typeof(char)) return (char)('a' + seed % 26);
            if (type == typeof(int)) return -10000 - seed;
            if (type == typeof(uint)) return 0x80000000u + (uint)seed;
            if (type == typeof(long)) return long.MinValue + seed;
            if (type == typeof(ulong)) return ulong.MaxValue - (ulong)seed;
            if (type == typeof(float)) return -1.2345678f - seed * .0001f;
            if (type == typeof(double)) return -1.2345678901234567 - seed * .0000001;
            if (type == typeof(decimal)) return -1.234567890123456789m - seed;
            if (type.IsEnum) return Enum.ToObject(type, 1);
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType(), 2);
                array.SetValue(Populate(type.GetElementType(), seed + 1, declarations), 0);
                array.SetValue(Populate(type.GetElementType(), seed + 2, declarations), 1);
                return array;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(type, new object[] { 37 });
                Type element = type.GetGenericArguments()[0];
                list.Add(Populate(element, seed + 1, declarations));
                list.Add(element.IsValueType || element == typeof(string) ? Populate(element, seed + 2, declarations) : null);
                return list;
            }
            if (type == typeof(Vector3)) return new Vector3(-seed - .25f, seed + .5f, -seed - .75f);
            if (type == typeof(Vector3Int)) return new Vector3Int(-seed - 11, seed + 13, -seed - 17);
            declarations.Add(type);
            object value = type.IsValueType ? Activator.CreateInstance(type) : Uninitialized(type);
            int ordinal = 0;
            foreach (var field in Fields(type)) field.SetValue(value, Populate(field.FieldType, seed + ++ordinal, declarations));
            return value;
        }
        static object FullReport(out HashSet<Type> declarations)
        {
            Assert.That(ReportType, Is.Not.Null);
            declarations = new HashSet<Type>();
            return Populate(ReportType, 1729, declarations);
        }
        static string Json(object payload)
        {
            using var file = BlockProbeReportFile.Capture(Path.Combine(Path.GetTempPath(), "snapshot67-" + Guid.NewGuid().ToString("N")), payload);
            return File.ReadAllText(file.Path);
        }
        static HashSet<object> MutableNodes(object root)
        {
            var result = new HashSet<object>(ReferenceComparer.Instance);
            void Visit(object node)
            {
                if (node == null || node is string || node.GetType().IsValueType) return;
                if (!result.Add(node)) return;
                if (node is IList list) { foreach (object child in list) Visit(child); return; }
                foreach (var field in Fields(node.GetType())) Visit(field.GetValue(node));
            }
            Visit(root); return result;
        }
        static object DifferentValue(object source, Type type)
        {
            if (type == typeof(string)) return "mutated source";
            if (Scalar(type)) return Activator.CreateInstance(type);
            object value = source ?? Activator.CreateInstance(type);
            foreach (var field in Fields(type))
                field.SetValue(value, field.FieldType.IsValueType || field.FieldType == typeof(string)
                    ? DifferentValue(field.GetValue(value), field.FieldType) : null);
            return value;
        }
        static void MutateEveryStorage(object root)
        {
            // Capture node identities before severing references, so every original
            // branch is mutated even after its parent's list/field is cleared.
            foreach (object node in MutableNodes(root))
            {
                if (node is Array array)
                {
                    Type element = array.GetType().GetElementType();
                    for (int i = 0; i < array.Length; i++) array.SetValue(element.IsValueType || element == typeof(string)
                        ? DifferentValue(array.GetValue(i), element) : null, i);
                }
                else if (node is IList list) list.Clear();
                else foreach (var field in Fields(node.GetType()))
                    field.SetValue(node, field.FieldType.IsValueType || field.FieldType == typeof(string)
                        ? DifferentValue(field.GetValue(node), field.FieldType) : null);
            }
        }
        static void AssertSeparated(object source, object frozen)
        {
            var original = MutableNodes(source); var copy = MutableNodes(frozen);
            Assert.That(copy.Count, Is.EqualTo(original.Count));
            foreach (object node in copy) Assert.That(original.Contains(node), Is.False, node.GetType().FullName);
        }
        static object Capture(object source, out object statistics)
        {
            var args = new object[] { source, null };
            try { object result = CaptureStatistics.Invoke(null, args); statistics = args[1]; return result; }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }
        static long Stat(object stats, string name) => Convert.ToInt64(stats.GetType().GetField(name).GetValue(stats));

        [Test] public void ActualFullReportEveryDeclaredBranchIsCopiedAndOriginalMutationCannotChangeItsBytes()
        {
            object source = FullReport(out var declarations);
            Assert.That(declarations.Count, Is.GreaterThanOrEqualTo(35));
            Assert.That(declarations.Sum(type => Fields(type).Length), Is.GreaterThanOrEqualTo(705));
            Assert.That(declarations.Any(type => type.Name == "ReconnectStageSample"), Is.True);
            foreach (string required in new[] { "reconnectStages", "current", "states", "finalBlocks", "actors", "blasts", "cpu", "workloadEvidence",
                "actorWindows", "routeSamples", "frameEvidence", "paperSampling", "frameRecords", "cpuRawValues", "cpuRecorderStates", "cpuSampleCounts",
                "paidEvents", "actualEvents", "commandReader", "writer", "checkpointProgressWriter", "blastPressure", "reportFullCaptureCount" })
                Assert.That(ReportType.GetField(required), Is.Not.Null, required);
            object frozen = BlockProbeReportSnapshot.Capture(source);
            Assert.That(frozen.GetType(), Is.EqualTo(ReportType));
            AssertSeparated(source, frozen);
            string expected = Json(source);
            Assert.That(Json(frozen), Is.EqualTo(expected));
            MutateEveryStorage(source);
            Assert.That(Json(source), Is.Not.EqualTo(expected));
            Assert.That(Json(frozen), Is.EqualTo(expected));
        }

        [Test] public void ActualReportAliasesRemainAliasesOnlyInsideTheNewGraph()
        {
            object source = FullReport(out _);
            var states = (IList)ReportType.GetField("states").GetValue(source);
            ReportType.GetField("current").SetValue(source, states[0]); states.Add(states[0]);
            object metric = ReportType.GetField("frames").GetValue(source);
            ReportType.GetField("mainThread").SetValue(source, metric);
            var cpu = (IList)ReportType.GetField("cpu").GetValue(source);
            cpu[0].GetType().GetField("time").SetValue(cpu[0], metric);
            object frozen = Capture(source, out var stats);
            var frozenStates = (IList)ReportType.GetField("states").GetValue(frozen);
            Assert.That(ReportType.GetField("current").GetValue(frozen), Is.SameAs(frozenStates[0]));
            Assert.That(frozenStates[2], Is.SameAs(frozenStates[0]));
            object frozenMetric = ReportType.GetField("frames").GetValue(frozen);
            Assert.That(ReportType.GetField("mainThread").GetValue(frozen), Is.SameAs(frozenMetric));
            var frozenCpu = (IList)ReportType.GetField("cpu").GetValue(frozen);
            Assert.That(frozenCpu[0].GetType().GetField("time").GetValue(frozenCpu[0]), Is.SameAs(frozenMetric));
            Assert.That(Stat(stats, "reusedAliases"), Is.GreaterThanOrEqualTo(4));
            AssertSeparated(source, frozen);
            Assert.That(Json(frozen), Is.EqualTo(Json(source)));
        }

        [Serializable] sealed class ReferenceRow { public int value; public string note; }
        [Serializable] sealed class Container
        {
            public List<ReferenceRow> rows;
            public ReferenceRow[] array;
            public List<string> names;
            public string optional;
        }
        [Test] public void ReferenceListsArraysNullsStringsAndCapacitiesRetainTheirContract()
        {
            var row = new ReferenceRow { value = 7, note = "immutable string" };
            var source = new Container { rows = new List<ReferenceRow>(4096) { row, null, row },
                array = new[] { row, null, row }, names = new List<string>(4096) { "name", null } };
            var frozen = (Container)BlockProbeReportSnapshot.Capture(source);
            Assert.That(frozen.rows.Capacity, Is.EqualTo(source.rows.Count));
            Assert.That(frozen.names.Capacity, Is.EqualTo(source.names.Count));
            Assert.That(frozen.rows[0], Is.SameAs(frozen.rows[2]));
            Assert.That(frozen.rows[0], Is.SameAs(frozen.array[0]));
            Assert.That(frozen.rows[0], Is.Not.SameAs(row));
            Assert.That(frozen.rows[1], Is.Null); Assert.That(frozen.array[1], Is.Null);
            Assert.That(frozen.names[0], Is.SameAs(source.names[0]));
            Assert.That(frozen.names[1], Is.Null); Assert.That(frozen.optional, Is.Null);
            row.value = 999; source.rows.Clear(); source.array[0] = null; source.names.Add("new");
            Assert.That(frozen.rows[0].value, Is.EqualTo(7));
            Assert.That(frozen.rows.Count, Is.EqualTo(3)); Assert.That(frozen.names.Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void ThirtyTwoThousandFrameRowsUseExactlyOneBulkContainerCopyWithNoPerRowVisitor(bool array)
        {
            const int count = 32768;
            var list = new List<FrameRecord>(count * 2);
            for (int i = 0; i < count; i++) list.Add(new FrameRecord { frameId = i + 1, stage = "same immutable stage", frameMilliseconds = 16.67 });
            object source = array ? list.ToArray() : list;
            object frozen = Capture(source, out var stats);
            Assert.That(Stat(stats, "visitedNodes"), Is.EqualTo(1));
            Assert.That(Stat(stats, "deepElementCopies"), Is.Zero);
            Assert.That(Stat(stats, "copiedValueElements"), Is.EqualTo(count));
            Assert.That(Stat(stats, array ? "fastArrayCopies" : "fastListCopies"), Is.EqualTo(1));
            Assert.That(Stat(stats, "classClones"), Is.Zero);
            var rows = (IList)frozen;
            Assert.That(((FrameRecord)rows[count - 1]).frameId, Is.EqualTo(count));
            if (!array) Assert.That(((List<FrameRecord>)frozen).Capacity, Is.EqualTo(count));
            if (array) ((FrameRecord[])source)[0] = default; else list[0] = default;
            Assert.That(((FrameRecord)rows[0]).frameId, Is.EqualTo(1));
        }

        [Serializable] struct Bits
        {
            public float negativeZero, nan;
            public double doubleNegativeZero, doubleNan;
            public long minimum;
            public ulong maximum;
            public Vector3Int coordinates;
            public string note;
        }
        [Test] public void BoxedAndContainerValueBitsRemainExactAndTheirBoxesAreIndependent()
        {
            var bits = new Bits { negativeZero = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0),
                nan = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x7fc01234)), 0),
                doubleNegativeZero = BitConverter.Int64BitsToDouble(long.MinValue), doubleNan = BitConverter.Int64BitsToDouble(0x7ff8000000001234),
                minimum = long.MinValue, maximum = ulong.MaxValue, coordinates = new Vector3Int(int.MinValue, 7, int.MaxValue), note = "same" };
            object source = bits; object frozenBox = BlockProbeReportSnapshot.Capture(source);
            Assert.That(frozenBox, Is.Not.SameAs(source));
            var copies = (List<Bits>)BlockProbeReportSnapshot.Capture(new List<Bits> { bits });
            foreach (var copy in new[] { (Bits)frozenBox, copies[0] })
            {
                Assert.That(BitConverter.ToInt32(BitConverter.GetBytes(copy.negativeZero), 0), Is.EqualTo(unchecked((int)0x80000000)));
                Assert.That(BitConverter.ToInt32(BitConverter.GetBytes(copy.nan), 0), Is.EqualTo(unchecked((int)0x7fc01234)));
                Assert.That(BitConverter.DoubleToInt64Bits(copy.doubleNegativeZero), Is.EqualTo(long.MinValue));
                Assert.That(BitConverter.DoubleToInt64Bits(copy.doubleNan), Is.EqualTo(0x7ff8000000001234));
                Assert.That(copy.minimum, Is.EqualTo(long.MinValue)); Assert.That(copy.maximum, Is.EqualTo(ulong.MaxValue));
                Assert.That(copy.coordinates, Is.EqualTo(bits.coordinates)); Assert.That(copy.note, Is.SameAs(bits.note));
            }
            typeof(Bits).GetField("minimum").SetValue(source, 0L);
            Assert.That(((Bits)frozenBox).minimum, Is.EqualTo(long.MinValue));
        }

        [Serializable] sealed class ConstructorGuard
        {
            public static int calls;
            public int value;
            public List<int> values;
            public int ExplodingProperty => throw new InvalidOperationException("Do not invoke properties");
            public ConstructorGuard() { calls++; throw new InvalidOperationException("Do not invoke constructors"); }
        }
        [Test] public void ClassConstructorsAndPropertyGettersAreNeverInvoked()
        {
            var source = (ConstructorGuard)Uninitialized(typeof(ConstructorGuard));
            source.value = 123; source.values = new List<int>(8192) { 7, 8 };
            int calls = ConstructorGuard.calls;
            var frozen = (ConstructorGuard)BlockProbeReportSnapshot.Capture(source);
            Assert.That(ConstructorGuard.calls, Is.EqualTo(calls));
            Assert.That(frozen.value, Is.EqualTo(123)); Assert.That(frozen.values, Is.EqualTo(new[] { 7, 8 }));
            Assert.That(frozen.values.Capacity, Is.EqualTo(2));
        }

        [Serializable] sealed class Node { public int value; public Node next; }
        [Serializable] sealed class ListNode { public int value; public List<ListNode> children; }
        [Test] public void RecursiveTypeDeclarationsAreAllowedButActualCyclesAreRejected()
        {
            var source = new Node { value = 1, next = new Node { value = 2 } };
            var frozen = (Node)BlockProbeReportSnapshot.Capture(source);
            Assert.That(frozen.next.value, Is.EqualTo(2)); Assert.That(frozen.next.next, Is.Null);
            source.next.next = source;
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(source));
            Assert.That(source.next.next, Is.SameAs(source));
        }
        [Test] public void RecursiveClassListDeclarationsSupportAcyclicGraphsAndRejectListCycles()
        {
            var leaf = new ListNode { value = 2, children = new List<ListNode>() };
            var source = new ListNode { value = 1, children = new List<ListNode> { leaf, leaf } };
            var frozen = (ListNode)BlockProbeReportSnapshot.Capture(source);
            Assert.That(frozen.children[0], Is.SameAs(frozen.children[1]));
            Assert.That(frozen.children[0], Is.Not.SameAs(leaf));
            leaf.children.Add(source);
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(source));
        }
        [Test] public void ReferenceDepthBoundaryIsExplicitAndFailureDoesNotMutateSource()
        {
            var root = new Node { value = 1 }; Node current = root;
            for (int i = 1; i <= 128; i++) current = current.next = new Node { value = i + 1 };
            Assert.DoesNotThrow(() => BlockProbeReportSnapshot.Capture(root));
            current.next = new Node { value = 130 };
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(root));
            Assert.That(current.next.value, Is.EqualTo(130));
        }

        [Serializable] sealed class HasObject { public object unknown; }
        [Serializable] sealed class HasInterface { public IList<int> unknown; }
        [Serializable] sealed class HasDictionary { public Dictionary<int, int> unknown; }
        [Serializable] sealed class HasUnityObject { public UnityEngine.Object unknown; }
        [Serializable] sealed class HasDelegate { public Action unknown; }
        [Serializable] sealed class HasUnsupportedStruct { public DateTime date; }
        [Serializable] sealed class HasNullable { public int? value; }
        [Serializable] sealed class HasHidden { List<int> hidden; }
        [Serializable] sealed class HasReadonly { public readonly List<int> values; }
        [Serializable] sealed class HasNonserialized { [NonSerialized] public List<int> values; }
        [Serializable] struct MutableStruct { public List<int> values; }
        [Serializable] sealed class ListSubclass : List<int> { }
        sealed class NotSerializable { public int value; }
        [Serializable] class BaseRow { public int value; }
        [Serializable] sealed class DerivedRow : BaseRow { public int extra; }
        [Serializable] sealed class Polymorphic { public BaseRow row; }
        static IEnumerable<Type> UnsupportedDeclarations()
        {
            yield return typeof(HasObject); yield return typeof(HasInterface); yield return typeof(HasDictionary);
            yield return typeof(HasUnityObject); yield return typeof(HasDelegate); yield return typeof(HasUnsupportedStruct);
            yield return typeof(HasNullable); yield return typeof(HasHidden); yield return typeof(HasReadonly);
            yield return typeof(HasNonserialized); yield return typeof(MutableStruct); yield return typeof(ListSubclass);
            yield return typeof(NotSerializable);
        }
        [TestCaseSource(nameof(UnsupportedDeclarations))]
        public void UnsupportedDeclaredReferencesAreRejectedEvenWhenTheirValueIsNull(Type type)
        { Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(type.IsValueType ? Activator.CreateInstance(type) : Uninitialized(type))); }

        [Test] public void MultidimensionalNonzeroBasedAndPolymorphicArraysOrFieldsAreRejected()
        {
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(new int[2, 2]));
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(Array.CreateInstance(typeof(int), new[] { 2 }, new[] { 1 })));
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(new Polymorphic { row = new DerivedRow() }));
            BaseRow[] derivedArray = new DerivedRow[1]; derivedArray[0] = new DerivedRow();
            // The runtime root is a concrete DerivedRow[] and is itself valid.
            Assert.DoesNotThrow(() => BlockProbeReportSnapshot.Capture(derivedArray));
            var source = new ArrayPolymorphic { rows = derivedArray };
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(source));
        }
        [Serializable] sealed class ArrayPolymorphic { public BaseRow[] rows; }

        [Test] public void FailedSchemaBuildDoesNotPoisonFutureCapturesAndRepeatedCapturesShareNoStorage()
        {
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(new HasObject()));
            var source = new Container { rows = new List<ReferenceRow> { new() { value = 17 } }, names = new List<string>() };
            var first = (Container)BlockProbeReportSnapshot.Capture(source);
            Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(new HasHidden()));
            var second = (Container)BlockProbeReportSnapshot.Capture(source);
            Assert.That(second.rows[0].value, Is.EqualTo(17)); AssertSeparated(first, second);
            Assert.That(BlockProbeReportSnapshot.Capture(null), Is.Null);
            Assert.That(BlockProbeReportSnapshot.Capture("same string"), Is.SameAs("same string"));
        }
        [Test] public void UnsupportedRootValuesAndOtherCollectionsAreRejected()
        {
            foreach (object source in new object[] { DateTime.UtcNow, IntPtr.Zero, UIntPtr.Zero, new HashSet<int> { 1 }, new Hashtable() })
                Assert.Throws<InvalidDataException>(() => BlockProbeReportSnapshot.Capture(source), source.GetType().FullName);
        }
    }
}
#endif
