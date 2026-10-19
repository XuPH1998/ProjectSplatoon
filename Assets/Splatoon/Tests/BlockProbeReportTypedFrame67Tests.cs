#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Splatoon.Prototype;
using UnityEngine;
using FrameRecord = Splatoon.Prototype.BlockFixedGridAcceptanceProbe.FrameRecord;
using Random = System.Random;

namespace Splatoon.Tests
{
    /// <summary>Reports-only candidate. Root owns installation and serialized execution.</summary>
    public sealed class BlockProbeReportTypedFrame67Tests
    {
        string _directory;
        static readonly Type Encoder = typeof(BlockProbeReportFile).GetNestedType("PublicFieldJson", BindingFlags.NonPublic);

        [SetUp] public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "BlockProbeReportTypedFrame67-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown] public void TearDown()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        [Serializable] sealed class Envelope
        {
            public object payload;
            public string note;
            public List<int> missingNumbers;
        }

        static IEnumerable<FieldInfo> Fields(Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!field.IsStatic && !field.IsLiteral && !field.IsInitOnly && !field.IsNotSerialized) yield return field;
        }

        static object NonDefault(Type type, Random random, ref int visited)
        {
            if (type == typeof(string)) return "积木 / \"quoted\" \\ \b\f\n\r\t \u0001 \ud83d\ude80 \ud800 " + random.Next(1, int.MaxValue);
            if (type == typeof(bool)) return true;
            if (type == typeof(byte)) return (byte)random.Next(1, 256);
            if (type == typeof(int)) return random.Next(1, int.MaxValue);
            if (type == typeof(uint)) return (uint)random.Next(1, int.MaxValue) + 0x80000000u;
            if (type == typeof(long)) return (long)random.Next(1, int.MaxValue) * int.MaxValue + random.Next(1, int.MaxValue);
            if (type == typeof(ulong)) return (ulong)random.Next(1, int.MaxValue) * uint.MaxValue + (uint)random.Next(1, int.MaxValue);
            if (type == typeof(float))
            {
                var bytes = new byte[4]; random.NextBytes(bytes); float value = BitConverter.ToSingle(bytes, 0);
                return !float.IsFinite(value) || value == 0 ? 1.2345678f : value;
            }
            if (type == typeof(double))
            {
                var bytes = new byte[8]; random.NextBytes(bytes); double value = BitConverter.ToDouble(bytes, 0);
                return !double.IsFinite(value) || value == 0 ? 1.2345678901234567 : value;
            }
            if (type == typeof(Vector3Int)) return new Vector3Int(-random.Next(1, int.MaxValue), random.Next(1, int.MaxValue), -random.Next(1, int.MaxValue));
            if (!type.IsValueType) throw new InvalidOperationException("Unexpected FrameRecord reference edge: " + type.FullName);
            object result = Activator.CreateInstance(type);
            foreach (var field in Fields(type))
            {
                visited++;
                field.SetValue(result, NonDefault(field.FieldType, random, ref visited));
            }
            return result;
        }

        static FrameRecord Sample(int seed)
        {
            int visited = 0;
            var result = (FrameRecord)NonDefault(typeof(FrameRecord), new Random(seed), ref visited);
            Assert.That(visited, Is.GreaterThan(250), "Every nested public data field receives a nondefault value, including repeated dependency instances.");
            return result;
        }

        static string Render(object source, bool useTyped)
        {
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            var constructor = Encoder.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(TextWriter), typeof(bool) }, null);
            Assert.That(constructor, Is.Not.Null, "The production utility exposes no new public API; this is the private nested parity oracle.");
            object encoder = null;
            try
            {
                encoder = constructor.Invoke(new object[] { text, useTyped });
                Encoder.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(encoder, new[] { source });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
            return text.ToString();
        }

        static void SameBytes(object source)
        { Assert.That(Render(source, true), Is.EqualTo(Render(source, false)), "Typed output must exactly equal the unchanged reflection branch."); }

        [TestCase(1729)]
        [TestCase(20263018)]
        public void EveryFrameFieldHasNondefaultRandomDataAndListArrayAndBoxedPathsMatchExactly(int seed)
        {
            var rows = new List<FrameRecord> { Sample(seed), Sample(seed + 1), Sample(seed + 2) };
            SameBytes(rows); SameBytes(rows.ToArray()); SameBytes(rows[0]);
            SameBytes(new Envelope { payload = rows });
        }

        [Test] public void EveryTypedNestedValueCanAlsoBeTheDirectRootWithoutChangingTheGenericContract()
        {
            var row = Sample(8917);
            SameBytes(row.mainThread); SameBytes(row.paperSampling); SameBytes(row.paperSampling.delta);
            SameBytes(row.dependency); SameBytes(row.dependency.Authority); SameBytes(row.dependency.Gate);
            SameBytes(row.dependency.Last); SameBytes(row.dependency.Last.WorldBegin);
        }

        [Test] public void SignedUnsignedLimitsNegativeZeroVectorCoordinatesAndStringsKeepExactFormatting()
        {
            var row = Sample(500);
            row.frameId = int.MinValue; row.observedAtFrameId = int.MaxValue;
            row.epoch = uint.MaxValue; row.queryPlayerNetworkId = ulong.MaxValue;
            row.metadataTimestamp = long.MinValue; row.observedTimestamp = long.MaxValue;
            row.cameraFieldOfView = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
            row.frameMilliseconds = BitConverter.Int64BitsToDouble(long.MinValue);
            var dependency = row.dependency; var authority = dependency.Authority;
            authority.SupportCell = new Vector3Int(int.MinValue, 7, int.MaxValue); dependency.Authority = authority; row.dependency = dependency;
            SameBytes(new List<FrameRecord> { row });
            string typed = Render(row, true);
            Assert.That(typed, Does.Contain("\"cameraFieldOfView\":-0.0"));
            Assert.That(typed, Does.Contain("\"frameMilliseconds\":-0.0"));
            Assert.That(typed, Does.Contain("\"SupportCell\":{\"x\":-2147483648,\"y\":7,\"z\":2147483647}"));
        }

        [Test] public void NullStringsZeroValuesAndEmptyCollectionsPreserveDefaultSchema()
        {
            SameBytes(default(FrameRecord)); SameBytes(new List<FrameRecord>()); SameBytes(Array.Empty<FrameRecord>());
            SameBytes(new Envelope { payload = new List<FrameRecord> { default } });
            SameBytes(new Envelope());
            Assert.That(Render(default(FrameRecord), true), Does.Contain("\"stage\":\"\""));
        }

        [TestCase(true, 0)] [TestCase(true, 1)] [TestCase(true, -1)]
        [TestCase(false, 0)] [TestCase(false, 1)] [TestCase(false, -1)]
        public void NonfiniteNumberInNestedFrameRetainsFailureAndPrivateFileCleanup(bool single, int kind)
        {
            var row = Sample(1024);
            if (single) row.cameraFieldOfView = kind == 0 ? float.NaN : kind > 0 ? float.PositiveInfinity : float.NegativeInfinity;
            else row.frameMilliseconds = kind == 0 ? double.NaN : kind > 0 ? double.PositiveInfinity : double.NegativeInfinity;
            var source = new Envelope { payload = new List<FrameRecord> { row } };
            Assert.Throws<InvalidDataException>(() => Render(source, false));
            Assert.Throws<InvalidDataException>(() => Render(source, true));
            Assert.Throws<InvalidDataException>(() => BlockProbeReportFile.Capture(Path.Combine(_directory, "nonfinite"), source));
            Assert.That(Directory.GetFiles(_directory), Is.Empty, "Neither sealed output nor partial private file may survive a failed capture.");
        }

        static string Outcome(object source, bool typed)
        {
            try { return "ok:" + Render(source, typed); }
            catch (InvalidDataException exception) { return "error:" + exception.Message; }
        }

        [TestCase(118)] [TestCase(121)] [TestCase(123)] [TestCase(124)]
        [TestCase(125)] [TestCase(126)] [TestCase(127)] [TestCase(128)] [TestCase(129)]
        public void NestedListElementAndEveryNestedValueKeepTheOriginalDepthBoundary(int wrappers)
        {
            foreach (object payload in new object[] { new List<FrameRecord> { Sample(111) }, new[] { Sample(222) }, new List<FrameRecord>() })
            {
                object source = payload;
                for (int i = 0; i < wrappers; i++) source = new Envelope { payload = source };
                Assert.That(Outcome(source, true), Is.EqualTo(Outcome(source, false)));
            }
        }

        [Test] public void ReferenceCycleStillFailsThroughTheOriginalFallback()
        {
            var source = new Envelope(); source.payload = source;
            Assert.Throws<InvalidDataException>(() => Render(source, false));
            Assert.Throws<InvalidDataException>(() => Render(source, true));
        }

        [Test] public void AlternateCultureCannotChangeTypedNumericBytes()
        {
            var original = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); SameBytes(new List<FrameRecord> { Sample(998) }); }
            finally { CultureInfo.CurrentCulture = original; }
        }
    }
}
#endif
