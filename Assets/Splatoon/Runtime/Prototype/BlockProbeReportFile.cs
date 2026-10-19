#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Splatoon.Prototype
{
    /// <summary>
    /// A lease on one immutable, private JSON file. Capture runs synchronously while
    /// its public-field DTO is stable. Background publishers retain the resulting
    /// file, never the live DTO. Memory used by encoding does not scale with rows.
    /// </summary>
    public sealed partial class BlockProbeReportFile : IDisposable
    {
        internal const int BufferCharacters = 16384;
        internal const int MaximumDepth = 128;
        public readonly string Path;
        public readonly string Sha256;
        public readonly long Length;

        sealed class Ownership
        {
            internal readonly object Gate = new();
            internal readonly string Path, Sha256;
            internal readonly long Length;
            internal int Leases = 1;
            internal Ownership(string path, string sha256, long length)
            { Path = path; Sha256 = sha256; Length = length; }
        }

        readonly Ownership _ownership;
        bool _disposed;

        BlockProbeReportFile(Ownership ownership)
        {
            _ownership = ownership;
            Path = ownership.Path; Sha256 = ownership.Sha256; Length = ownership.Length;
        }

        /// <summary>
        /// pathPrefix identifies a destination directory and filename prefix, not a
        /// replaceable output. A successful capture owns a new unique .sealed.json
        /// file. A failure returns no lease and never overwrites another capture.
        /// Call only outside the measured interval; the caller must not mutate DTOs
        /// during this synchronous operation.
        /// </summary>
        public static BlockProbeReportFile Capture(string pathPrefix, object payload)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix)) throw new ArgumentException("A capture path prefix is required.", nameof(pathPrefix));
            string prefix = System.IO.Path.GetFullPath(pathPrefix);
            string suffix = Guid.NewGuid().ToString("N");
            string writing = prefix + "." + suffix + ".writing";
            string sealedPath = prefix + "." + suffix + ".sealed.json";
            bool createdWriting = false, createdSealed = false;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(prefix));
                using (var file = new FileStream(writing, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferCharacters, FileOptions.SequentialScan))
                {
                    createdWriting = true;
                    using (var text = new StreamWriter(file, new UTF8Encoding(false, true), BufferCharacters, true))
                    {
                        new PublicFieldJson(text).Write(payload);
                        text.Flush();
                    }
                    file.Flush(true);
                }
                string hash;
                using (var file = new FileStream(writing, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferCharacters, FileOptions.SequentialScan))
                using (var sha = SHA256.Create()) hash = Hex(sha.ComputeHash(file));
                long length = new FileInfo(writing).Length;
                // Both names are in the same directory and private to this capture.
                // No reader receives the filename until encoding, flush and hash succeed.
                File.Move(writing, sealedPath); createdWriting = false; createdSealed = true;
                return new BlockProbeReportFile(new Ownership(sealedPath, hash, length));
            }
            catch (Exception failure)
            {
                Exception cleanup = null;
                if (createdWriting) try { File.Delete(writing); } catch (Exception error) { cleanup = error; }
                if (createdSealed) try { File.Delete(sealedPath); } catch (Exception error)
                    { cleanup = cleanup == null ? error : new AggregateException(cleanup, error); }
                if (cleanup != null) throw new AggregateException("Report capture failed and its private file cleanup also failed.", failure, cleanup);
                throw;
            }
        }

        public BlockProbeReportFile Retain()
        {
            lock (_ownership.Gate)
            {
                if (_disposed || _ownership.Leases <= 0) throw new ObjectDisposedException(nameof(BlockProbeReportFile));
                if (_ownership.Leases == int.MaxValue) throw new InvalidOperationException("Report file lease count exhausted.");
                var lease = new BlockProbeReportFile(_ownership);
                _ownership.Leases++;
                return lease;
            }
        }

        public void Dispose()
        {
            lock (_ownership.Gate)
            {
                if (_disposed) return;
                if (_ownership.Leases == 1)
                {
                    // On an IO failure the final lease remains live and can retry.
                    // A successful Dispose never silently leaves its owned file behind.
                    File.Delete(Path);
                }
                _ownership.Leases--; _disposed = true;
            }
        }

        static string Hex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var result = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            { result[i * 2] = digits[bytes[i] >> 4]; result[i * 2 + 1] = digits[bytes[i] & 15]; }
            return new string(result);
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        sealed partial class PublicFieldJson
        {
            readonly TextWriter _text;
            readonly Dictionary<Type, FieldInfo[]> _fields = new();
            readonly HashSet<object> _ancestors = new(ReferenceComparer.Instance);
            readonly HashSet<Type> _defaultAncestors = new();
            readonly bool _useTyped;
            internal PublicFieldJson(TextWriter text) : this(text, true) { }
            // The bounded parity oracle can invoke this non-public overload with false.
            // Production Capture always selects the default true constructor.
            internal PublicFieldJson(TextWriter text, bool useTyped)
            { _text = text; _useTyped = useTyped; if (useTyped) EnsureTypedSchema(); }
            internal void Write(object value) => Value(value, value?.GetType(), 0);

            void Value(object value, Type declaredType, int depth)
            {
                if (depth > MaximumDepth) throw new InvalidDataException("Report JSON exceeds the supported nesting depth.");
                if (value == null) { DefaultValue(declaredType, depth); return; }
                Type type = value.GetType();
                if (type.IsEnum) { EnumValue(value, Enum.GetUnderlyingType(type)); return; }
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.String: String((string)value); return;
                    case TypeCode.Char: String(((char)value).ToString()); return;
                    case TypeCode.Boolean: _text.Write((bool)value ? "true" : "false"); return;
                    case TypeCode.Byte: _text.Write(((byte)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.SByte: _text.Write(((sbyte)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.Int16: _text.Write(((short)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.UInt16: _text.Write(((ushort)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.Int32: _text.Write(((int)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.UInt32: _text.Write(((uint)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.Int64: _text.Write(((long)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.UInt64: _text.Write(((ulong)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.Decimal: _text.Write(((decimal)value).ToString(CultureInfo.InvariantCulture)); return;
                    case TypeCode.Single: Single((float)value); return;
                    case TypeCode.Double: Double((double)value); return;
                    case TypeCode.DateTime: throw new InvalidDataException("DateTime must be captured as an explicit report string or number.");
                }
                if (value is UnityEngine.Object || value is Delegate || value is IDictionary || type.IsPrimitive)
                    throw new InvalidDataException("Unsupported report JSON type: " + type.FullName);
                // Unity serializes Vector3Int coordinates despite their property
                // accessors. Keep that exact report shape without evaluating
                // arbitrary DTO properties or exposing private implementation fields.
                if (value is UnityEngine.Vector3Int cell)
                {
                    _text.Write("{\"x\":"); _text.Write(cell.x.ToString(CultureInfo.InvariantCulture));
                    _text.Write(",\"y\":"); _text.Write(cell.y.ToString(CultureInfo.InvariantCulture));
                    _text.Write(",\"z\":"); _text.Write(cell.z.ToString(CultureInfo.InvariantCulture));
                    _text.Write('}'); return;
                }
                bool reference = !type.IsValueType;
                if (reference && !_ancestors.Add(value)) throw new InvalidDataException("Cyclic report DTO graph.");
                try
                {
                    if (_useTyped && TryWriteTyped(value, depth)) return;
                    // The large parallel raw CPU arrays and scalar timing series
                    // use typed indexed reads: no per-number reflection or boxing.
                    if (Numbers(value, depth + 1)) return;
                    if (value is IList list)
                    {
                        if (value is Array array && array.Rank != 1) throw new InvalidDataException("Only one-dimensional report arrays are supported.");
                        Type elementType = ElementType(type);
                        _text.Write('[');
                        for (int i = 0; i < list.Count; i++)
                        { if (i != 0) _text.Write(','); Value(list[i], elementType, depth + 1); }
                        _text.Write(']'); return;
                    }
                    if (value is IEnumerable) throw new InvalidDataException("Report collections must be indexed lists or arrays.");
                    if (!_fields.TryGetValue(type, out var fields))
                    {
                        fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                        _fields.Add(type, fields);
                    }
                    _text.Write('{'); bool first = true;
                    foreach (var field in fields)
                    {
                        // Match the serializable public-field surface of the probe DTOs.
                        // Properties, static/const, readonly and [NonSerialized] are absent.
                        if (field.IsStatic || field.IsLiteral || field.IsInitOnly || field.IsNotSerialized) continue;
                        if (!first) _text.Write(','); first = false;
                        String(field.Name); _text.Write(':'); Value(field.GetValue(value), field.FieldType, depth + 1);
                    }
                    _text.Write('}');
                }
                finally { if (reference) _ancestors.Remove(value); }
            }

            bool Numbers(object value, int elementDepth)
            {
                switch (value)
                {
                    case IList<long> longs: Integers(longs, elementDepth); return true;
                    case IList<ulong> ulongs: Integers(ulongs, elementDepth); return true;
                    case IList<int> ints: Integers(ints, elementDepth); return true;
                    case IList<uint> uints: Integers(uints, elementDepth); return true;
                    case IList<byte> bytes: Integers(bytes, elementDepth); return true;
                    case IList<double> doubles:
                        CheckElements(doubles.Count, elementDepth); _text.Write('[');
                        for (int i = 0; i < doubles.Count; i++) { if (i != 0) _text.Write(','); Double(doubles[i]); }
                        _text.Write(']'); return true;
                    case IList<float> singles:
                        CheckElements(singles.Count, elementDepth); _text.Write('[');
                        for (int i = 0; i < singles.Count; i++) { if (i != 0) _text.Write(','); Single(singles[i]); }
                        _text.Write(']'); return true;
                    default: return false;
                }
            }

            void Integers<T>(IList<T> values, int elementDepth) where T : struct, IFormattable
            {
                CheckElements(values.Count, elementDepth); _text.Write('[');
                for (int i = 0; i < values.Count; i++)
                { if (i != 0) _text.Write(','); _text.Write(values[i].ToString(null, CultureInfo.InvariantCulture)); }
                _text.Write(']');
            }

            static void CheckElements(int count, int elementDepth)
            {
                if (count != 0 && elementDepth > MaximumDepth)
                    throw new InvalidDataException("Report JSON exceeds the supported nesting depth.");
            }

            void Single(float value)
            {
                if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite Single in report JSON.");
                if (value == 0 && BitConverter.DoubleToInt64Bits(value) < 0) _text.Write("-0.0");
                else _text.Write(value.ToString("R", CultureInfo.InvariantCulture));
            }

            void Double(double value)
            {
                if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite Double in report JSON.");
                if (value == 0 && BitConverter.DoubleToInt64Bits(value) < 0) _text.Write("-0.0");
                else _text.Write(value.ToString("R", CultureInfo.InvariantCulture));
            }

            void DefaultValue(Type type, int depth)
            {
                // JsonUtility's existing probe schema emits empty strings/collections
                // and initialized default nested DTO fields for null references. Keep
                // that readable schema without constructing a duplicate full Report.
                if (type == typeof(string)) { String(string.Empty); return; }
                if (type != null && (type.IsArray || typeof(IList).IsAssignableFrom(type)))
                { _text.Write("[]"); return; }
                if (type == null || type == typeof(object) || type.IsInterface || type.IsAbstract || Nullable.GetUnderlyingType(type) != null)
                { _text.Write("null"); return; }
                if (typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type))
                    throw new InvalidDataException("Unsupported null report JSON type: " + type.FullName);
                if (!_defaultAncestors.Add(type)) throw new InvalidDataException("Recursive null-default report DTO type: " + type.FullName);
                try { Value(Activator.CreateInstance(type, true), type, depth); }
                finally { _defaultAncestors.Remove(type); }
            }

            static Type ElementType(Type type)
            {
                if (type.IsArray) return type.GetElementType();
                for (Type current = type; current != null; current = current.BaseType)
                    if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(List<>)) return current.GetGenericArguments()[0];
                return typeof(object);
            }

            void EnumValue(object value, Type underlying)
            {
                bool unsigned = underlying == typeof(byte) || underlying == typeof(ushort) ||
                    underlying == typeof(uint) || underlying == typeof(ulong);
                _text.Write(unsigned ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }

            void String(string value)
            {
                const string digits = "0123456789abcdef";
                _text.Write('"');
                foreach (char codeUnit in value)
                {
                    switch (codeUnit)
                    {
                        case '"': _text.Write("\\\""); break;
                        case '\\': _text.Write("\\\\"); break;
                        case '\b': _text.Write("\\b"); break;
                        case '\f': _text.Write("\\f"); break;
                        case '\n': _text.Write("\\n"); break;
                        case '\r': _text.Write("\\r"); break;
                        case '\t': _text.Write("\\t"); break;
                        default:
                            if (codeUnit < 32 || codeUnit >= 127)
                            {
                                // Escape UTF-16 code units individually, including surrogate
                                // pairs/unpaired surrogates. No whole-string encoded copy.
                                _text.Write("\\u"); _text.Write(digits[(codeUnit >> 12) & 15]);
                                _text.Write(digits[(codeUnit >> 8) & 15]); _text.Write(digits[(codeUnit >> 4) & 15]);
                                _text.Write(digits[codeUnit & 15]);
                            }
                            else _text.Write(codeUnit);
                            break;
                    }
                }
                _text.Write('"');
            }
        }
    }
}
#endif
