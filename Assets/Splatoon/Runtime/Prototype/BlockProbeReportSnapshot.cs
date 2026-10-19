#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Splatoon.Prototype
{
    /// <summary>
    /// Freezes a stable public-field report graph without JSON or DTO constructors.
    /// Call synchronously while the caller owns all source mutations. The returned
    /// graph has the same DTO/list/array types and must be privately owned by its
    /// receiving worker; this utility does not expose or schedule that worker.
    /// </summary>
    public static class BlockProbeReportSnapshot
    {
        const int MaximumDepth = 128;
        enum Shape { Value, String, List, Array, Dto }

        sealed class Field
        {
            internal readonly FieldInfo Info;
            internal readonly Schema Schema;
            internal Field(FieldInfo info, Schema schema) { Info = info; Schema = schema; }
        }

        sealed class Schema
        {
            internal readonly Type Type;
            internal Shape Shape;
            internal Schema Element;
            internal Func<object, object> CopyValueList;
            internal Func<int, IList> NewReferenceList;
            internal Field[] References = Array.Empty<Field>();
            internal bool Complete;
            internal bool Immutable => Shape is Shape.Value or Shape.String;
            internal Schema(Type type) { Type = type; }
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        // Exact schema is built once per runtime Type and published only after all
        // reachable declarations pass. A failed build never poisons the cache.
        static readonly object SchemaGate = new();
        static readonly Dictionary<Type, Schema> Schemas = new();
        static readonly Func<object, object> MemberwiseCopy = (Func<object, object>)typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)
            .CreateDelegate(typeof(Func<object, object>));
        static readonly MethodInfo CopyValueListMethod = typeof(BlockProbeReportSnapshot)
            .GetMethod(nameof(CopyList), BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo NewReferenceListMethod = typeof(BlockProbeReportSnapshot)
            .GetMethod(nameof(NewList), BindingFlags.NonPublic | BindingFlags.Static);

        // Private diagnostics are consumed by the bounded reflection fixture.
        // They hold counts only and never retain a source or frozen graph.
        struct Statistics
        {
            public long visitedNodes, classClones, fastListCopies, fastArrayCopies,
                copiedValueElements, deepElementCopies, reusedAliases;
            public int maximumReferenceDepth;
        }

        /// <summary>
        /// Returns a fully independent mutable-storage graph. Strings are shared;
        /// value bits are copied exactly, including signed zero and non-finite bits.
        /// All schema/cycle/unsupported-type errors throw before any graph escapes.
        /// </summary>
        public static object Capture(object payload) => CaptureWithStatistics(payload, out _);

        static object CaptureWithStatistics(object payload, out Statistics statistics)
        {
            statistics = default;
            if (payload == null) return null;
            var context = new Context();
            object copy = context.Clone(payload, GetSchema(payload.GetType()), 0);
            statistics = context.Stats;
            return copy;
        }

        static Schema GetSchema(Type type)
        {
            lock (SchemaGate)
            {
                if (Schemas.TryGetValue(type, out var known)) return known;
                var pending = new Dictionary<Type, Schema>();
                var root = BuildSchema(type, pending);
                foreach (var pair in pending)
                    if (!pair.Value.Complete) throw new InvalidDataException("Incomplete snapshot schema: " + pair.Key.FullName);
                foreach (var pair in pending) Schemas.Add(pair.Key, pair.Value);
                return root;
            }
        }

        static Schema BuildSchema(Type type, Dictionary<Type, Schema> pending)
        {
            if (Schemas.TryGetValue(type, out var cached)) return cached;
            if (pending.TryGetValue(type, out var building)) return building;
            RejectUnsupported(type);
            var schema = new Schema(type); pending.Add(type, schema);
            if (type == typeof(string)) schema.Shape = Shape.String;
            else if (type.IsEnum || SupportedScalar(type)) schema.Shape = Shape.Value;
            else if (type.IsArray)
            {
                if (type.GetArrayRank() != 1 || type != type.GetElementType().MakeArrayType())
                    throw Unsupported(type, "Only ordinary zero-based one-dimensional arrays are supported");
                schema.Shape = Shape.Array;
                schema.Element = BuildSchema(type.GetElementType(), pending);
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                schema.Shape = Shape.List;
                Type element = type.GetGenericArguments()[0];
                schema.Element = BuildSchema(element, pending);
                if (schema.Element.Immutable)
                    schema.CopyValueList = (Func<object, object>)CopyValueListMethod.MakeGenericMethod(element).CreateDelegate(typeof(Func<object, object>));
                else schema.NewReferenceList = (Func<int, IList>)NewReferenceListMethod.MakeGenericMethod(element).CreateDelegate(typeof(Func<int, IList>));
            }
            else if (typeof(IEnumerable).IsAssignableFrom(type))
                throw Unsupported(type, "Only concrete List<T> and ordinary one-dimensional arrays are supported");
            else if (type.IsValueType)
            {
                // Inspect private/readonly fields too: a value-container fast copy
                // is safe only if no hidden mutable reference can be shared.
                schema.Shape = Shape.Value;
                foreach (var field in InstanceFields(type))
                {
                    var child = BuildSchema(field.FieldType, pending);
                    if (!child.Immutable)
                        throw Unsupported(type, "Value types may contain only values and strings; mutable field " + field.Name);
                }
            }
            else
            {
                if (!type.IsClass || !type.IsDefined(typeof(SerializableAttribute), false))
                    throw Unsupported(type, "Reference DTOs must be concrete [Serializable] classes");
                schema.Shape = Shape.Dto;
                var references = new List<Field>();
                foreach (var field in InstanceFields(type))
                {
                    var child = BuildSchema(field.FieldType, pending);
                    if (child.Immutable) continue; // MemberwiseClone already copied its exact value bits.
                    if (!SerializablePublic(field))
                        throw Unsupported(type, "Hidden, readonly or [NonSerialized] mutable reference field " + field.Name);
                    references.Add(new Field(field, child));
                }
                schema.References = references.ToArray();
            }
            schema.Complete = true;
            return schema;
        }

        static bool SerializablePublic(FieldInfo field) => field.IsPublic && !field.IsStatic &&
            !field.IsLiteral && !field.IsInitOnly && !field.IsNotSerialized;

        static IEnumerable<FieldInfo> InstanceFields(Type type)
        {
            for (Type current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic) yield return field;
        }

        static bool SupportedScalar(Type type)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: case TypeCode.Char:
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal: return true;
                default: return false;
            }
        }

        static void RejectUnsupported(Type type)
        {
            if (type == typeof(object) || type.IsInterface || type.IsAbstract || type.IsPointer || type.IsByRef || type.ContainsGenericParameters ||
                Nullable.GetUnderlyingType(type) != null || type == typeof(DateTime) || type == typeof(IntPtr) || type == typeof(UIntPtr) ||
                typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type))
                throw Unsupported(type, "Unsupported report snapshot declaration");
        }

        static InvalidDataException Unsupported(Type type, string reason) => new(reason + ": " + type.FullName);

        // List<T>'s ICollection<T> constructor performs one Count-sized bulk copy.
        // No element reflection, boxing, visitors, callbacks or intermediate JSON.
        static object CopyList<T>(object source) => new List<T>((List<T>)source);
        static IList NewList<T>(int count) => new List<T>(count);

        sealed class Context
        {
            readonly Dictionary<object, object> _copies = new(ReferenceComparer.Instance);
            readonly HashSet<object> _active = new(ReferenceComparer.Instance);
            internal Statistics Stats;

            internal object Clone(object source, Schema schema, int depth)
            {
                if (source == null) return null;
                if (depth > MaximumDepth) throw new InvalidDataException("Report snapshot exceeds the supported reference depth.");
                if (source.GetType() != schema.Type)
                    throw Unsupported(source.GetType(), "Polymorphic runtime DTO type differs from its declared field/element type " + schema.Type.FullName);
                Stats.visitedNodes++;
                Stats.maximumReferenceDepth = Math.Max(Stats.maximumReferenceDepth, depth);
                if (schema.Shape == Shape.String) return source;
                // A direct boxed value root also receives its own box. Returning
                // the original box would let reflection mutation alter the snapshot.
                if (schema.Shape == Shape.Value) return MemberwiseCopy(source);
                if (_active.Contains(source)) throw new InvalidDataException("Cyclic report snapshot graph.");
                if (_copies.TryGetValue(source, out var previous)) { Stats.reusedAliases++; return previous; }
                _active.Add(source);
                try
                {
                    switch (schema.Shape)
                    {
                        case Shape.List:
                        {
                            var list = (IList)source;
                            if (schema.Element.Immutable)
                            {
                                object result = schema.CopyValueList(source);
                                _copies.Add(source, result); Stats.fastListCopies++; Stats.copiedValueElements += list.Count;
                                return result;
                            }
                            int count = list.Count;
                            var copy = schema.NewReferenceList(count); _copies.Add(source, copy);
                            for (int i = 0; i < count; i++)
                            { copy.Add(Clone(list[i], schema.Element, depth + 1)); Stats.deepElementCopies++; }
                            return copy;
                        }
                        case Shape.Array:
                        {
                            var array = (Array)source;
                            if (array.GetLowerBound(0) != 0) throw Unsupported(schema.Type, "Array lower bound is not zero");
                            if (schema.Element.Immutable)
                            {
                                object result = array.Clone();
                                _copies.Add(source, result); Stats.fastArrayCopies++; Stats.copiedValueElements += array.Length;
                                return result;
                            }
                            var copy = Array.CreateInstance(schema.Element.Type, array.Length); _copies.Add(source, copy);
                            for (int i = 0; i < array.Length; i++)
                            { copy.SetValue(Clone(array.GetValue(i), schema.Element, depth + 1), i); Stats.deepElementCopies++; }
                            return copy;
                        }
                        case Shape.Dto:
                        {
                            object copy = MemberwiseCopy(source); _copies.Add(source, copy); Stats.classClones++;
                            foreach (var field in schema.References)
                                field.Info.SetValue(copy, Clone(field.Info.GetValue(source), field.Schema, depth + 1));
                            return copy;
                        }
                        default: throw new InvalidDataException("Uninitialized report snapshot schema.");
                    }
                }
                finally { _active.Remove(source); }
            }
        }
    }
}
#endif
