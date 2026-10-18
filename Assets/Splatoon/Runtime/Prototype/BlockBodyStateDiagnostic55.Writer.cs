#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Splatoon.Prototype
{
    // Reports-only candidate. Bundle and its nested row/header DTOs are declared
    // by the collector partial. The caller seals those buffers before this call.
    internal static partial class BlockBodyStateDiagnostic55
    {
        internal sealed class WriteOutcome
        {
            public bool completed, windowComplete, deadlineExceeded;
            public string error, sha256, path;
            public long bytes, deadlineTimestamp, completedTimestamp;
            public double serializationMs, writeMs;
        }

        sealed class DiagnosticPayload
        {
            public Bundle bundle;
            public Validation validation;
        }

        sealed class Validation
        {
            public int schema = 1, captureCount;
            public bool copyComplete, complete;
            public CaptureValidation[] captures = new CaptureValidation[3];
        }

        sealed class CaptureValidation
        {
            public int index, duplicateBodyIds, duplicateNativeIds, duplicatePlayerOwners, duplicatePlayerComponents;
            public int invalidBodyRows, invalidPlayerRows;
            public bool buffersValid, copyComplete, complete;
        }

        sealed class DiagnosticWriteReceipt
        {
            public int schema = 1;
            public string session, status, path, error, sha256;
            public bool diagnosticOnly = true, windowComplete, deadlineExceeded;
            public long bytes, deadlineTimestamp, completedTimestamp;
            public double serializationMs, writeMs;
            public int captureCount;
            public string completedTimestampMeaning = "last deadline check before atomic receipt publication";
        }

        // One task owns serialization, hashing and both publications. It never
        // enqueues another writer or closes over the collector or Unity objects.
        internal static Task<WriteOutcome> StartWrite(Bundle bundle, string path, long deadlineTimestamp)
            => Task.Run(() => WriteBundle(bundle, path, deadlineTimestamp));

        static WriteOutcome WriteBundle(Bundle bundle, string path, long deadlineTimestamp)
        {
            var outcome = new WriteOutcome { path = path, deadlineTimestamp = deadlineTimestamp };
            Validation validation = null;
            string receiptPath = null;
            try
            {
                outcome.path = Path.GetFullPath(path);
                receiptPath = outcome.path + ".writer.json";
                RequireNewDestination(outcome.path);
                RequireNewDestination(receiptPath);
                if (bundle == null) throw new ArgumentNullException(nameof(bundle));
                if (deadlineTimestamp <= 0) throw new ArgumentOutOfRangeException(nameof(deadlineTimestamp));

                var clock = Stopwatch.StartNew();
                byte[] payload;
                try
                {
                    validation = ValidateRawBundle(bundle);
                    string json = new PrimitiveDtoJson().Serialize(new DiagnosticPayload { bundle = bundle, validation = validation });
                    payload = new UTF8Encoding(false).GetBytes(json);
                    outcome.bytes = payload.LongLength;
                    using (var sha = SHA256.Create())
                        outcome.sha256 = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
                }
                finally { outcome.serializationMs = clock.Elapsed.TotalMilliseconds; }

                // Keep the captured evidence even if serialization consumed the
                // deadline. The receipt, rather than a discarded bundle, records it.
                outcome.deadlineExceeded = Stopwatch.GetTimestamp() > deadlineTimestamp;
                clock.Restart();
                try { WriteNewAtomic(outcome.path, payload); }
                finally { outcome.writeMs = clock.Elapsed.TotalMilliseconds; }

                // These durations cover bundle serialization/hash and bundle
                // publication. Completion also requires the separate receipt.
                PublishReceipt(bundle, validation, receiptPath, outcome, null);
                // The receipt records its last pre-publication check. Its own IO
                // can still cross the deadline, so the process terminal status
                // must also acknowledge this actual post-receipt completion.
                outcome.completedTimestamp = Stopwatch.GetTimestamp();
                outcome.deadlineExceeded |= outcome.completedTimestamp > outcome.deadlineTimestamp;
                outcome.windowComplete &= !outcome.deadlineExceeded;
                outcome.completed = true;
            }
            catch (Exception error)
            {
                // Keep the original exception even if the failure receipt also
                // cannot be published. Existing output files are never replaced.
                outcome.error = error.ToString();
                outcome.windowComplete = false;
                if (receiptPath != null)
                {
                    try { PublishReceipt(bundle, validation, receiptPath, outcome, outcome.error); }
                    catch (Exception receiptError)
                    { outcome.error += "\nFailure receipt could not be published: " + receiptError; }
                }
            }
            return outcome;
        }

        static Validation ValidateRawBundle(Bundle bundle)
        {
            var validation = new Validation { captureCount = bundle.captureCount, copyComplete = bundle.complete,
                complete = bundle.complete && bundle.sealedWindow && bundle.captureCount == 3 };
            if (bundle.captureCount < 0 || bundle.captureCount > 3 ||
                bundle.captures != null && bundle.captures.Length != 3 ||
                bundle.captureCount != 0 && bundle.captures == null)
                throw new InvalidOperationException("Invalid raw diagnostic capture buffer/count.");
            for (int i = 0; i < bundle.captureCount; i++)
            {
                var raw = bundle.captures[i];
                var item = new CaptureValidation { index = i, copyComplete = raw != null && raw.complete };
                validation.captures[i] = item;
                item.buffersValid = raw != null && raw.bodies != null && raw.bodies.Length == 8192 &&
                    raw.players != null && raw.players.Length == 16 && raw.bodyVisited >= 0 && raw.bodyVisited <= 8192 &&
                    raw.playerVisited >= 0 && raw.playerVisited <= 16;
                if (!item.buffersValid) { validation.complete = false; continue; }

                var bodyIds = new HashSet<ulong>();
                var nativeIds = new HashSet<int>();
                for (int rowIndex = 0; rowIndex < raw.bodyVisited; rowIndex++)
                {
                    var row = raw.bodies[rowIndex];
                    if (row.error != 0 || row.readMask != 31 || !row.finite || !row.identityValid || !row.colliderPresent
                        || row.id == 0 || row.id != row.dictionaryKey || row.componentInstance == 0 || row.nativeInstance == 0
                        || !Numeric(row.position) || !Numeric(row.rotation) || !Numeric(row.velocity) || !Numeric(row.angularVelocity))
                        item.invalidBodyRows++;
                    // Unread/invalid zero sentinels have their own collector
                    // errors. They are not repeated identities. Owner zero,
                    // unlike these IDs, is a valid host player identity below.
                    if ((row.readMask & 1) != 0 && row.id != 0 && !bodyIds.Add(row.id)) item.duplicateBodyIds++;
                    if ((row.readMask & 2) != 0 && row.nativeInstance != 0 && !nativeIds.Add(row.nativeInstance)) item.duplicateNativeIds++;
                }
                var playerOwners = new HashSet<ulong>();
                var playerComponents = new HashSet<int>();
                for (int rowIndex = 0; rowIndex < raw.playerVisited; rowIndex++)
                {
                    var row = raw.players[rowIndex];
                    if (row.error != 0 || row.componentInstance == 0 || !row.snapshotRead || !row.finite
                        || !Numeric(row.position) || !Numeric(row.humanPosition) || !float.IsFinite(row.airHumanOffset) || !double.IsFinite(row.simulatedAt))
                        item.invalidPlayerRows++;
                    if (row.componentInstance == 0) continue;
                    if (!playerOwners.Add(row.owner)) item.duplicatePlayerOwners++;
                    if (!playerComponents.Add(row.componentInstance)) item.duplicatePlayerComponents++;
                }
                item.complete = item.copyComplete && raw.headerFinite && !raw.headerDrift && !raw.bodyOverflow && !raw.playerOverflow
                    && raw.start.expectedBodies == raw.bodyVisited && raw.end.expectedBodies == raw.bodyVisited && raw.bodyCaptured == raw.bodyVisited
                    && raw.start.expectedPlayers == raw.playerVisited && raw.end.expectedPlayers == raw.playerVisited && raw.playerCaptured == raw.playerVisited
                    && item.invalidBodyRows == 0 && item.invalidPlayerRows == 0 && item.duplicateBodyIds == 0 && item.duplicateNativeIds == 0 &&
                    item.duplicatePlayerOwners == 0 && item.duplicatePlayerComponents == 0;
                validation.complete &= item.complete;
            }
            // Raw Bundle/Capture fields are never changed here. The worker's
            // validation.complete is the data-validity result; receipt
            // windowComplete additionally requires the publication deadline.
            return validation;
        }
        static bool Numeric(V3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        static bool Numeric(Q4 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);

        static void PublishReceipt(Bundle bundle, Validation validation, string receiptPath, WriteOutcome outcome, string error)
        {
            outcome.completedTimestamp = Stopwatch.GetTimestamp();
            outcome.deadlineExceeded |= outcome.deadlineTimestamp <= 0 || outcome.completedTimestamp > outcome.deadlineTimestamp;
            outcome.windowComplete = error == null && validation != null && validation.complete && !outcome.deadlineExceeded;
            var receipt = new DiagnosticWriteReceipt
            {
                session = bundle == null ? null : bundle.session,
                captureCount = bundle == null ? 0 : bundle.captureCount,
                status = error != null ? "failed" : outcome.deadlineExceeded ? "deadline-exceeded" : "completed",
                path = outcome.path, error = error, windowComplete = outcome.windowComplete, deadlineExceeded = outcome.deadlineExceeded,
                deadlineTimestamp = outcome.deadlineTimestamp, completedTimestamp = outcome.completedTimestamp,
                bytes = outcome.bytes, sha256 = outcome.sha256,
                serializationMs = outcome.serializationMs, writeMs = outcome.writeMs
            };
            byte[] payload = new UTF8Encoding(false).GetBytes(new PrimitiveDtoJson().Serialize(receipt));
            // Serialization itself can cross the boundary. Check again directly
            // before writing; a failed/late receipt is still published once.
            long finalCheck = Stopwatch.GetTimestamp();
            if (!outcome.deadlineExceeded && finalCheck > outcome.deadlineTimestamp)
            {
                outcome.deadlineExceeded = true; outcome.windowComplete = false; outcome.completedTimestamp = finalCheck;
                receipt.deadlineExceeded = true; receipt.windowComplete = false; receipt.completedTimestamp = finalCheck;
                if (error == null) receipt.status = "deadline-exceeded";
                payload = new UTF8Encoding(false).GetBytes(new PrimitiveDtoJson().Serialize(receipt));
            }
            WriteNewAtomic(receiptPath, payload);
        }

        static void RequireNewDestination(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Diagnostic output already exists; refusing to replace it: " + path);
        }

        static void WriteNewAtomic(string path, byte[] payload)
        {
            RequireNewDestination(path);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
            bool created = false, moved = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                // Same-directory move publishes the complete file atomically;
                // this overload fails if another writer created the destination.
                File.Move(temporary, path);
                moved = true;
            }
            finally
            {
                if (created && !moved)
                {
                    try { File.Delete(temporary); }
                    catch { /* Preserve the original serialization/publication failure. */ }
                }
            }
        }

        // Public fields only, and DTO types nested directly in this diagnostic.
        // Property getters, Unity types, delegates and arbitrary framework
        // objects are deliberately unsupported, including when their value is null.
        sealed class PrimitiveDtoJson
        {
            readonly StringBuilder _json = new StringBuilder(65536);
            readonly Dictionary<Type, FieldInfo[]> _fields = new Dictionary<Type, FieldInfo[]>();
            static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

            public string Serialize(object value)
            {
                Value(value, 0);
                return _json.ToString();
            }

            static bool Number(Type type) => type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double);

            static bool Dto(Type type) => type.DeclaringType == typeof(BlockBodyStateDiagnostic55) &&
                !type.IsEnum && (type.IsValueType || type.IsClass && type.BaseType == typeof(object));

            static void RequireFieldType(Type type)
            {
                if (type == typeof(string) || type == typeof(bool) || Number(type) || Dto(type)) return;
                if (type.IsArray && type.GetArrayRank() == 1)
                { RequireFieldType(type.GetElementType()); return; }
                throw new InvalidOperationException("Unsupported diagnostic field type: " + type.FullName);
            }

            void Value(object value, int depth)
            {
                if (depth > 24) throw new InvalidOperationException("Diagnostic DTO nesting exceeds the bounded schema.");
                if (value == null) { _json.Append("null"); return; }
                if (value is string text) { String(text); return; }
                if (value is bool boolean) { _json.Append(boolean ? "true" : "false"); return; }
                if (value is float single)
                {
                    _json.Append(float.IsNaN(single) || float.IsInfinity(single) ? "null" : single.ToString("R", Invariant));
                    return;
                }
                if (value is double real)
                {
                    _json.Append(double.IsNaN(real) || double.IsInfinity(real) ? "null" : real.ToString("R", Invariant));
                    return;
                }
                Type type = value.GetType();
                if (Number(type)) { _json.Append(((IFormattable)value).ToString(null, Invariant)); return; }
                if (!Dto(type)) throw new InvalidOperationException("Unsupported diagnostic DTO type: " + type.FullName);

                if (!_fields.TryGetValue(type, out var fields))
                {
                    fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                    Array.Sort(fields, (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                    foreach (var field in fields) RequireFieldType(field.FieldType);
                    _fields.Add(type, fields);
                }
                _json.Append('{');
                for (int i = 0; i < fields.Length; i++)
                {
                    if (i != 0) _json.Append(',');
                    var field = fields[i];
                    String(field.Name); _json.Append(':');
                    object fieldValue = field.GetValue(value);
                    if (field.FieldType.IsArray) ArrayField(value, field.Name, (Array)fieldValue, depth + 1);
                    else Value(fieldValue, depth + 1);
                }
                _json.Append('}');
            }

            void ArrayField(object owner, string name, Array array, int depth)
            {
                int count, capacity;
                if (owner is Bundle bundle && name == "captures")
                { count = bundle.captureCount; capacity = 3; }
                else if (owner is Validation validation && name == "captures")
                { count = validation.captureCount; capacity = 3; }
                else if (owner is Capture capture && name == "bodies")
                { count = capture.bodyVisited; capacity = 8192; }
                else if (owner is Capture playerCapture && name == "players")
                { count = playerCapture.playerVisited; capacity = 16; }
                else throw new InvalidOperationException("Unexpected array outside the three bounded diagnostic buffers: " + name);
                // An invalid explicit request or Editor refusal can seal before
                // allocation. Preserve that zero-capture reason without allocating
                // buffers or treating a missing populated buffer as complete.
                if (owner is Bundle && name == "captures" && count == 0 && array == null)
                { _json.Append("[]"); return; }
                if (array == null || array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length != capacity || count < 0 || count > capacity)
                    throw new InvalidOperationException("Invalid diagnostic buffer/count: " + name);
                _json.Append('[');
                for (int i = 0; i < count; i++)
                {
                    if (i != 0) _json.Append(',');
                    Value(array.GetValue(i), depth + 1);
                }
                _json.Append(']');
            }

            void String(string value)
            {
                _json.Append('"');
                foreach (char character in value)
                {
                    switch (character)
                    {
                        case '"': _json.Append("\\\""); break;
                        case '\\': _json.Append("\\\\"); break;
                        case '\b': _json.Append("\\b"); break;
                        case '\f': _json.Append("\\f"); break;
                        case '\n': _json.Append("\\n"); break;
                        case '\r': _json.Append("\\r"); break;
                        case '\t': _json.Append("\\t"); break;
                        default:
                            if (character < 0x20 || char.IsSurrogate(character))
                            { _json.Append("\\u"); _json.Append(((int)character).ToString("x4", Invariant)); }
                            else _json.Append(character);
                            break;
                    }
                }
                _json.Append('"');
            }
        }
    }
}
#endif
