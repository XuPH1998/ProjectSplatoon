#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockProbeReportFileTests
    {
        string _directory, _prefix;

        [SetUp] public void SetUp()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BlockProbeReportFileTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory); _prefix = System.IO.Path.Combine(_directory, "probe");
        }

        [TearDown] public void TearDown()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        enum SamplePhase { Unavailable = -1, Playing = 1, Finished = 2 }
        [Serializable] sealed class DefaultLeaf
        { public string scope = "Original default scope"; public List<int> actors = new(); public string failure; }
        [Serializable] sealed class SchemaSample
        {
            public int schema = 4, evidenceSchema = 4;
            public long signed = long.MinValue;
            public ulong unsigned = ulong.MaxValue;
            public uint revision = uint.MaxValue;
            public double seconds = 1.2345678901234567;
            public float height = 1.2345678f;
            public bool complete = true;
            public SamplePhase phase = SamplePhase.Playing;
            public Vector3 position = new(1.125f, -2.25f, 3.5f);
            public Vector3Int cell = new(-12, 7, 54);
            public string label = "积木终态 / \"quoted\" \\ path\r\nline";
            public string observerFailure;
            public DefaultLeaf blastPressure;
            public long[] cpuRawValues = { long.MinValue, -1, 0, long.MaxValue };
            public byte[] cpuRecorderStates = { 0, 1, 7, 255 };
            public List<double> frameMilliseconds = new() { .125, 22.2, 33.3, 1e-100 };
            public List<int> missingList;
            public int[] missingArray;
            [NonSerialized] public string ignored = "not serialized";
            public readonly string ignoredReadonly = "not serialized";
            public static string ignoredStatic = "not serialized";
            public string IgnoredProperty => throw new InvalidOperationException("A property getter must never run.");
        }

        [Test] public void PublicFieldSchemaRoundTripsAllRequiredKindsAndUnityNullDefaults()
        {
            var source = new SchemaSample();
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                using var file = BlockProbeReportFile.Capture(_prefix, source);
                string smallJson = ReadSmallJson(file.Path);
                Assert.That(smallJson, Does.Not.Contain("ignored"));
                Assert.That(smallJson, Does.Not.Contain("IgnoredProperty"));
                Assert.That(smallJson, Does.Contain("\"observerFailure\":\"\""));
                Assert.That(smallJson, Does.Contain("\"missingList\":[]"));
                Assert.That(smallJson, Does.Contain("\"missingArray\":[]"));
                Assert.That(smallJson, Does.Contain("\"scope\":\"Original default scope\""));
                Assert.That(smallJson, Does.Contain("\"actors\":[]"));
                var actual = JsonUtility.FromJson<SchemaSample>(smallJson);
                var original = JsonUtility.FromJson<SchemaSample>(JsonUtility.ToJson(source));
                Assert.That(actual.schema, Is.EqualTo(4)); Assert.That(actual.evidenceSchema, Is.EqualTo(4));
                Assert.That(actual.signed, Is.EqualTo(source.signed)); Assert.That(actual.unsigned, Is.EqualTo(source.unsigned));
                Assert.That(actual.revision, Is.EqualTo(source.revision)); Assert.That(actual.complete, Is.EqualTo(source.complete));
                Assert.That(actual.phase, Is.EqualTo(source.phase)); Assert.That(actual.position, Is.EqualTo(source.position));
                Assert.That(actual.cell, Is.EqualTo(source.cell)); Assert.That(actual.label, Is.EqualTo(source.label));
                Assert.That(BitConverter.DoubleToInt64Bits(actual.seconds), Is.EqualTo(BitConverter.DoubleToInt64Bits(source.seconds)));
                Assert.That(actual.height, Is.EqualTo(source.height));
                Assert.That(actual.cpuRawValues, Is.EqualTo(source.cpuRawValues));
                Assert.That(actual.cpuRecorderStates, Is.EqualTo(source.cpuRecorderStates));
                Assert.That(actual.frameMilliseconds, Is.EqualTo(source.frameMilliseconds));
                Assert.That(actual.observerFailure, Is.EqualTo(original.observerFailure));
                Assert.That(actual.blastPressure.scope, Is.EqualTo(original.blastPressure.scope));
                Assert.That(actual.blastPressure.failure, Is.EqualTo(original.blastPressure.failure));
                Assert.That(actual.blastPressure.actors, Is.EqualTo(original.blastPressure.actors));
                Assert.That(actual.missingList, Is.EqualTo(original.missingList));
                Assert.That(actual.missingArray, Is.EqualTo(original.missingArray));
                AssertFileIdentity(file);
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        }

        [Serializable] sealed class IntegerVectorSample { public Vector3Int cell; }
        [Test] public void IntegerVectorCoordinatesAreEmittedAndRoundTripWithoutFieldInitializers()
        {
            // The read DTO has no nonzero initializer; {} must not look correct.
            var source = new IntegerVectorSample { cell = new Vector3Int(-12, 7, 54) };
            using var file = BlockProbeReportFile.Capture(_prefix, source);
            string json = ReadSmallJson(file.Path);
            TestContext.WriteLine("[BlockProbeReportFile.IntegerVectorJson] " + json);
            Assert.That(json, Is.EqualTo("{\"cell\":{\"x\":-12,\"y\":7,\"z\":54}}"));
            var decoded = JsonUtility.FromJson<IntegerVectorSample>(json);
            Assert.That(decoded.cell, Is.EqualTo(source.cell));
        }

        [Serializable] sealed class StringSample { public string text; }
        [Test] public void StringsPreserveEveryUtf16CodeUnitWithoutRawControlsOrWholeStringCopies()
        {
            string original = "\0\u0001\b\f\n\r\t\"\\/中\u2028\u2029\ud83d\ude00\ud800x\udfff";
            using var file = BlockProbeReportFile.Capture(_prefix, new StringSample { text = original });
            string json = ReadSmallJson(file.Path);
            Assert.That(json, Does.StartWith("{\"text\":"));
            for (int i = 0; i < json.Length; i++) Assert.That((int)json[i], Is.InRange(32, 126));
            Assert.That(DecodeJsonString(json, "{\"text\":".Length), Is.EqualTo(original));
            AssertFileIdentity(file);
        }

        [Serializable] sealed class FloatingSample { public double value; public float single; }
        [Test] public void FiniteNumbersUseRoundTripFormattingIncludingNegativeZero()
        {
            double[] values = { double.Epsilon, double.MaxValue, -double.MaxValue, 1e-100, 1.2345678901234567,
                BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL)) };
            foreach (double value in values)
            {
                using var file = BlockProbeReportFile.Capture(_prefix, new FloatingSample { value = value, single = -0.0f });
                string json = ReadSmallJson(file.Path);
                int begin = json.IndexOf("\"value\":", StringComparison.Ordinal) + "\"value\":".Length;
                int comma = json.IndexOf(',', begin), brace = json.IndexOf('}', begin);
                int end = comma < 0 ? brace : Math.Min(comma, brace);
                string token = json.Substring(begin, end - begin);
                long expectedBits = BitConverter.DoubleToInt64Bits(value);
                if (value == 0 && expectedBits < 0)
                {
                    // Mono double.Parse normalizes -0.0 to positive zero. Validate
                    // the actual JSON token directly; the small runtime fixture
                    // is also independently decoded by the Python JSON oracle.
                    Assert.That(token, Is.EqualTo("-0.0"));
                    Assert.That(json, Is.EqualTo("{\"value\":-0.0,\"single\":-0.0}"));
                    TestContext.WriteLine("[BlockProbeReportFile.NegativeZeroJson] " + json);
                }
                else
                {
                    double parsed = double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
                    Assert.That(BitConverter.DoubleToInt64Bits(parsed), Is.EqualTo(expectedBits));
                }
                Assert.That(json, Does.Contain("\"single\":-0.0"));
            }
        }

        [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)] [TestCase(double.NegativeInfinity)]
        public void NonFiniteOrCyclicCaptureDoesNotPublishOrDamageAnExistingCapture(double invalid)
        {
            using var existing = BlockProbeReportFile.Capture(_prefix, new StringSample { text = "immutable earlier snapshot" });
            string originalHash = existing.Sha256;
            Assert.Throws<InvalidDataException>(() => BlockProbeReportFile.Capture(_prefix, new FloatingSample { value = invalid }));
            Assert.That(Directory.GetFiles(_directory), Is.EquivalentTo(new[] { existing.Path }));
            Assert.That(HashFile(existing.Path), Is.EqualTo(originalHash));
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        public void NonFiniteSingleInTypedArrayFailsWithoutPublishing(float invalid)
        {
            Assert.Throws<InvalidDataException>(() => BlockProbeReportFile.Capture(_prefix, new[] { 1f, invalid }));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Serializable] sealed class Node { public Node next; }
        [Test] public void ActualCyclesAndRecursiveNullDefaultsFailAndRetireOnlyTheirPrivateFiles()
        {
            string unrelated = System.IO.Path.Combine(_directory, "unrelated.txt"); File.WriteAllText(unrelated, "retain");
            var cycle = new Node(); cycle.next = cycle;
            Assert.Throws<InvalidDataException>(() => BlockProbeReportFile.Capture(_prefix, cycle));
            Assert.Throws<InvalidDataException>(() => BlockProbeReportFile.Capture(_prefix, new Node()));
            Assert.That(Directory.GetFiles(_directory), Is.EquivalentTo(new[] { unrelated }));
        }

        [Test] public void IndependentLeasesKeepTheSameSealedBytesUntilTheLastOwnerReleases()
        {
            var source = new StringSample { text = "point in time" };
            var owner = BlockProbeReportFile.Capture(_prefix, source);
            var checkpoint = owner.Retain(); var worker = checkpoint.Retain();
            string path = owner.Path, hash = owner.Sha256; long bytes = owner.Length;
            Assert.That(checkpoint, Is.Not.SameAs(owner)); Assert.That(worker, Is.Not.SameAs(checkpoint));
            source.text = "later mutable report";
            owner.Dispose(); owner.Dispose();
            Assert.Throws<ObjectDisposedException>(() => owner.Retain());
            Assert.That(File.Exists(path), Is.True); Assert.That(HashFile(path), Is.EqualTo(hash));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(bytes));
            checkpoint.Dispose(); checkpoint.Dispose(); Assert.That(File.Exists(path), Is.True);
            worker.Dispose(); worker.Dispose(); Assert.That(File.Exists(path), Is.False);
            Assert.Throws<ObjectDisposedException>(() => worker.Retain());
        }

        [Serializable] sealed class LargeRow { public int ordinal; public string text; }
        [Serializable] sealed class GeneratedPayload { public IList rows; }
        sealed class GeneratedRows : IList
        {
            readonly int _count;
            readonly string _text = new('x', 1024);
            internal long BaselineBytes, MaximumLiveDelta;
            internal int Reads;
            internal GeneratedRows(int count) { _count = count; }
            public object this[int index]
            {
                get
                {
                    if ((index & 1023) == 0 || index + 1 == _count)
                        MaximumLiveDelta = Math.Max(MaximumLiveDelta, GC.GetTotalMemory(true) - BaselineBytes);
                    Reads++; return new LargeRow { ordinal = index, text = _text };
                }
                set => throw new NotSupportedException();
            }
            public int Count => _count;
            public bool IsFixedSize => true;
            public bool IsReadOnly => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public int Add(object value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(object value) => throw new NotSupportedException();
            public int IndexOf(object value) => throw new NotSupportedException();
            public void Insert(int index, object value) => throw new NotSupportedException();
            public void Remove(object value) => throw new NotSupportedException();
            public void RemoveAt(int index) => throw new NotSupportedException();
            public void CopyTo(Array array, int index) => throw new NotSupportedException();
            public IEnumerator GetEnumerator() => throw new InvalidOperationException("Do not materialize generated rows.");
        }

        [Test] public void MultiMegabyteCaptureRetainsEveryRowWithBoundedLiveEncodingMemory()
        {
            const int count = 20000;
            var rows = new GeneratedRows(count); var source = new GeneratedPayload { rows = rows };
            rows.BaselineBytes = GC.GetTotalMemory(true);
            using var file = BlockProbeReportFile.Capture(_prefix, source);
            Assert.That(file.Length, Is.GreaterThan(20L * 1024 * 1024));
            Assert.That(rows.Reads, Is.EqualTo(count));
            // This samples retained live managed memory after forced collections while
            // the serializer is still active. It is not a process working-set metric.
            Assert.That(rows.MaximumLiveDelta, Is.LessThan(4L * 1024 * 1024));
            Assert.That(ReadAndCheckOrdinals(file.Path), Is.EqualTo(count));
            AssertFileIdentity(file); GC.KeepAlive(source);
        }

        static string ReadSmallJson(string path)
        {
            Assert.That(new FileInfo(path).Length, Is.LessThan(16384), "Only bounded schema fixtures may read a whole JSON string.");
            using var reader = new StreamReader(path, Encoding.UTF8, true, 1024);
            return reader.ReadToEnd();
        }

        static string HashFile(string path)
        {
            using var input = File.OpenRead(path); using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        static void AssertFileIdentity(BlockProbeReportFile file)
        {
            Assert.That(System.IO.Path.IsPathRooted(file.Path), Is.True);
            Assert.That(new FileInfo(file.Path).Length, Is.EqualTo(file.Length));
            Assert.That(HashFile(file.Path), Is.EqualTo(file.Sha256));
            Assert.That(file.Path, Does.EndWith(".sealed.json"));
        }

        static int ReadAndCheckOrdinals(string path)
        {
            const string token = "\"ordinal\":";
            int matched = 0, count = 0;
            using var reader = new StreamReader(path, Encoding.UTF8, true, 4096);
            int character;
            while ((character = reader.Read()) >= 0)
            {
                if (character == token[matched]) matched++;
                else matched = character == token[0] ? 1 : 0;
                if (matched != token.Length) continue;
                int ordinal = 0, digits = 0;
                while ((character = reader.Read()) >= '0' && character <= '9')
                { ordinal = checked(ordinal * 10 + character - '0'); digits++; }
                Assert.That(digits, Is.GreaterThan(0)); Assert.That(ordinal, Is.EqualTo(count++));
                Assert.That(character == ',' || character == '}', Is.True); matched = 0;
            }
            return count;
        }

        static string DecodeJsonString(string json, int quote)
        {
            Assert.That(json[quote], Is.EqualTo('"')); var value = new StringBuilder();
            for (int i = quote + 1; i < json.Length; i++)
            {
                char character = json[i]; if (character == '"') return value.ToString();
                if (character != '\\') { value.Append(character); continue; }
                char escaped = json[++i];
                switch (escaped)
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u': value.Append((char)ushort.Parse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: Assert.Fail("Invalid JSON escape."); break;
                }
            }
            Assert.Fail("Missing string terminator."); return null;
        }
    }
}
#endif
