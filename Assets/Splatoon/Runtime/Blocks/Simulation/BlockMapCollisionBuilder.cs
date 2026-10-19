using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    /// <summary>Load-time managed builder. No geometry construction occurs in simulation jobs.</summary>
    public sealed class BlockMapCollisionBuilder
    {
        sealed class Shape
        {
            public BlockMapFeature Feature;
            public float3[] Vertices;
            public int[] Triangles;
            public byte InternalEdges;
        }
        readonly List<Shape> _shapes = new();
        readonly List<byte[]> _metadata = new();
        readonly HashSet<int> _ids = new();
        int _nextId = 1;
        const float GeometryEpsilon = .00001f;
        static readonly float3 X = new(1, 0, 0), Y = new(0, 1, 0), Z = new(0, 0, 1);

        public int AddBox(float3 center, float3 extents, int featureId = 0,
            float staticFriction = .6f, float dynamicFriction = .6f)
        {
            Validate(center); Validate(extents);
            if (math.any(extents <= 0)) throw new ArgumentOutOfRangeException(nameof(extents));
            var feature = NewFeature(BlockMapFeatureKind.Box, featureId, staticFriction, dynamicFriction);
            feature.Center = center; feature.Extents = extents;
            _shapes.Add(new Shape { Feature = feature });
            return feature.FeatureId;
        }

        public int AddTriangle(float3 a, float3 b, float3 c, int featureId = 0,
            float staticFriction = .6f, float dynamicFriction = .6f)
        {
            Validate(a); Validate(b); Validate(c);
            float3 cross = math.cross(b - a, c - a);
            if (math.lengthsq(cross) <= 1e-16f) throw new ArgumentException("Degenerate map triangle.");
            var feature = NewFeature(BlockMapFeatureKind.Triangle, featureId, staticFriction, dynamicFriction);
            feature.A = a; feature.B = b; feature.C = c; feature.PlaneNormal = math.normalize(cross);
            float3 min = math.min(a, math.min(b, c)), max = math.max(a, math.max(b, c));
            feature.Center = (min + max) * .5f; feature.Extents = (max - min) * .5f;
            _shapes.Add(new Shape { Feature = feature });
            return feature.FeatureId;
        }

        /// <summary>Closed convex solid, including initial-inside queries. Inputs are copied and validated.</summary>
        public int AddConvex(float3[] vertices, int[] triangles, int featureId = 0,
            float staticFriction = .6f, float dynamicFriction = .6f)
        {
            if (vertices == null || triangles == null || vertices.Length < 4 || triangles.Length < 12 || triangles.Length % 3 != 0)
                throw new ArgumentException("A convex map solid requires vertices and a closed triangle mesh.");
            var copiedVertices = (float3[])vertices.Clone(); var copiedTriangles = (int[])triangles.Clone();
            float3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity), centroid = 0;
            foreach (var v in copiedVertices) { Validate(v); min = math.min(min, v); max = math.max(max, v); centroid += v; }
            centroid /= copiedVertices.Length;
            var edges = new Dictionary<EdgeKey, int>();
            for (int t = 0; t < copiedTriangles.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                    if ((uint)copiedTriangles[t + k] >= copiedVertices.Length) throw new ArgumentException("Convex triangle index is out of range.");
                float3 a = copiedVertices[copiedTriangles[t]], b = copiedVertices[copiedTriangles[t + 1]], c = copiedVertices[copiedTriangles[t + 2]];
                float3 cross = math.cross(b - a, c - a);
                if (math.lengthsq(cross) <= 1e-16f) throw new ArgumentException("Convex map solid has a degenerate triangle.");
                float3 normal = math.normalize(cross);
                if (math.dot(normal, centroid - a) > 0) normal = -normal;
                foreach (var v in copiedVertices)
                    if (math.dot(normal, v - a) > GeometryEpsilon) throw new ArgumentException("Declared convex map mesh is not convex; bake a convex hull explicitly.");
                AddEdgeCount(edges, new EdgeKey(a, b)); AddEdgeCount(edges, new EdgeKey(b, c)); AddEdgeCount(edges, new EdgeKey(c, a));
            }
            foreach (var edge in edges)
                if (edge.Value != 2) throw new ArgumentException("Convex map solid must be closed and manifold after position welding.");
            float volume = 0;
            for (int t = 0; t < copiedTriangles.Length; t += 3)
            {
                float3 a = copiedVertices[copiedTriangles[t]] - centroid, b = copiedVertices[copiedTriangles[t + 1]] - centroid,
                    c = copiedVertices[copiedTriangles[t + 2]] - centroid;
                volume += math.abs(math.dot(a, math.cross(b, c))) / 6;
            }
            if (volume <= 1e-12f) throw new ArgumentException("A convex map solid must enclose nonzero volume.");
            var feature = NewFeature(BlockMapFeatureKind.Convex, featureId, staticFriction, dynamicFriction);
            feature.Center = (min + max) * .5f; feature.Extents = (max - min) * .5f;
            _shapes.Add(new Shape { Feature = feature, Vertices = copiedVertices, Triangles = copiedTriangles });
            return feature.FeatureId;
        }

        // Includes source topology, transforms, flags and material policy even when two sources
        // happen to produce identical world-space primitives. Copied to prevent later mutation.
        public void AddSignatureData(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            _metadata.Add((byte[])data.Clone());
        }

        public BlockMapCollisionData Build(Allocator allocator = Allocator.Persistent)
        {
            WeldInternalTriangleEdges();
            var features = new List<BlockMapFeature>(_shapes.Count);
            var axes = new List<BlockMapAxis>(); var vertices = new List<float3>(); var triangles = new List<int>();
            foreach (var shape in _shapes)
            {
                var feature = shape.Feature;
                feature.AxisStart = axes.Count; feature.VertexStart = vertices.Count; feature.TriangleStart = triangles.Count;
                var localAxes = new List<BlockMapAxis>();
                AddAxis(localAxes, shape, X); AddAxis(localAxes, shape, Y); AddAxis(localAxes, shape, Z);
                if (feature.Kind == BlockMapFeatureKind.Triangle)
                {
                    AddAxis(localAxes, shape, feature.PlaneNormal);
                    AddEdgeAxes(localAxes, shape, feature.B - feature.A, (shape.InternalEdges & 1) != 0);
                    AddEdgeAxes(localAxes, shape, feature.C - feature.B, (shape.InternalEdges & 2) != 0);
                    AddEdgeAxes(localAxes, shape, feature.A - feature.C, (shape.InternalEdges & 4) != 0);
                }
                else if (feature.Kind == BlockMapFeatureKind.Convex)
                {
                    var uniqueEdges = new HashSet<EdgeKey>();
                    for (int t = 0; t < shape.Triangles.Length; t += 3)
                    {
                        float3 a = shape.Vertices[shape.Triangles[t]], b = shape.Vertices[shape.Triangles[t + 1]], c = shape.Vertices[shape.Triangles[t + 2]];
                        AddAxis(localAxes, shape, math.cross(b - a, c - a));
                        uniqueEdges.Add(new EdgeKey(a, b)); uniqueEdges.Add(new EdgeKey(b, c)); uniqueEdges.Add(new EdgeKey(c, a));
                    }
                    // Iterating source triangles preserves deterministic axis ordering; the set is only membership.
                    for (int t = 0; t < shape.Triangles.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            float3 a = shape.Vertices[shape.Triangles[t + e]], b = shape.Vertices[shape.Triangles[t + (e + 1) % 3]];
                            if (uniqueEdges.Remove(new EdgeKey(a, b))) AddEdgeAxes(localAxes, shape, b - a, false);
                        }
                    vertices.AddRange(shape.Vertices); triangles.AddRange(shape.Triangles);
                    feature.VertexCount = shape.Vertices.Length; feature.TriangleCount = shape.Triangles.Length / 3;
                }
                feature.AxisCount = localAxes.Count; axes.AddRange(localAxes); features.Add(feature);
            }
            int[] indices = new int[features.Count]; for (int i = 0; i < indices.Length; i++) indices[i] = i;
            var nodes = new List<BlockMapBvhNode>();
            if (indices.Length > 0) BuildNode(features, indices, 0, indices.Length, nodes);
            return new BlockMapCollisionData(features.ToArray(), axes.ToArray(), vertices.ToArray(), triangles.ToArray(),
                nodes.ToArray(), indices, HashGeometry(), allocator);
        }

        BlockMapFeature NewFeature(BlockMapFeatureKind kind, int id, float staticFriction, float dynamicFriction)
        {
            if (!math.isfinite(staticFriction) || !math.isfinite(dynamicFriction) || staticFriction < 0 || dynamicFriction < 0)
                throw new ArgumentOutOfRangeException(nameof(staticFriction));
            if (id == 0) { while (_ids.Contains(_nextId)) _nextId++; id = _nextId++; }
            if (id < 1 || !_ids.Add(id)) throw new ArgumentException("Map feature IDs must be positive and unique.");
            return new BlockMapFeature { Kind = kind, FeatureId = id, StaticFriction = staticFriction, DynamicFriction = dynamicFriction };
        }

        static void Validate(float3 value)
        { if (!math.all(math.isfinite(value))) throw new ArgumentException("Map geometry must be finite."); }
        static void AddEdgeCount(Dictionary<EdgeKey, int> edges, EdgeKey key)
        { edges.TryGetValue(key, out int count); edges[key] = count + 1; }

        static void AddEdgeAxes(List<BlockMapAxis> axes, Shape shape, float3 edge, bool internalEdge)
        {
            AddAxis(axes, shape, math.cross(edge, X), internalEdge);
            AddAxis(axes, shape, math.cross(edge, Y), internalEdge);
            AddAxis(axes, shape, math.cross(edge, Z), internalEdge);
        }
        static void AddAxis(List<BlockMapAxis> axes, Shape shape, float3 axis, bool internalEdge = false)
        {
            float lengthSq = math.lengthsq(axis); if (lengthSq < 1e-16f) return;
            axis *= math.rsqrt(lengthSq);
            if (axis.x < -1e-7f || math.abs(axis.x) <= 1e-7f && axis.y < -1e-7f || math.abs(axis.x) <= 1e-7f && math.abs(axis.y) <= 1e-7f && axis.z < 0) axis = -axis;
            for (int i = 0; i < axes.Count; i++)
                if (math.abs(math.dot(axes[i].Direction, axis)) > .999999f)
                {
                    // A genuine face or exterior edge sharing this axis takes priority.
                    if (!internalEdge && axes[i].InternalEdge != 0) { var item = axes[i]; item.InternalEdge = 0; axes[i] = item; }
                    return;
                }
            float min, max; var f = shape.Feature;
            if (f.Kind == BlockMapFeatureKind.Box)
            { float center = math.dot(f.Center, axis), radius = math.dot(f.Extents, math.abs(axis)); min = center - radius; max = center + radius; }
            else if (f.Kind == BlockMapFeatureKind.Triangle)
            { float a = math.dot(f.A, axis), b = math.dot(f.B, axis), c = math.dot(f.C, axis); min = math.min(a, math.min(b, c)); max = math.max(a, math.max(b, c)); }
            else
            { min = float.PositiveInfinity; max = float.NegativeInfinity; foreach (var v in shape.Vertices) { float p = math.dot(v, axis); min = math.min(min, p); max = math.max(max, p); } }
            axes.Add(new BlockMapAxis { Direction = axis, Minimum = min, Maximum = max, InternalEdge = internalEdge ? (byte)1 : (byte)0 });
        }

        void WeldInternalTriangleEdges()
        {
            var edges = new Dictionary<EdgeKey, List<(int shape, int edge)>>();
            for (int i = 0; i < _shapes.Count; i++)
            {
                var shape = _shapes[i]; shape.InternalEdges = 0;
                if (shape.Feature.Kind != BlockMapFeatureKind.Triangle) continue;
                var f = shape.Feature;
                Add(new EdgeKey(f.A, f.B), i, 0); Add(new EdgeKey(f.B, f.C), i, 1); Add(new EdgeKey(f.C, f.A), i, 2);
            }
            foreach (var edge in edges.Values)
            {
                if (edge.Count != 2) continue;
                var a = _shapes[edge[0].shape]; var b = _shapes[edge[1].shape];
                if (math.abs(math.dot(a.Feature.PlaneNormal, b.Feature.PlaneNormal)) < .999999f ||
                    math.abs(math.dot(a.Feature.PlaneNormal, b.Feature.A - a.Feature.A)) > GeometryEpsilon) continue;
                float3 start = EdgeVertex(a.Feature, edge[0].edge), end = EdgeVertex(a.Feature, (edge[0].edge + 1) % 3);
                float sideA = math.dot(math.cross(end - start, EdgeVertex(a.Feature, (edge[0].edge + 2) % 3) - start), a.Feature.PlaneNormal);
                float sideB = math.dot(math.cross(end - start, EdgeVertex(b.Feature, (edge[1].edge + 2) % 3) - start), a.Feature.PlaneNormal);
                // Overlapping/duplicate triangles sharing an exterior edge do not make it an internal seam.
                if (sideA * sideB >= 0) continue;
                a.InternalEdges |= (byte)(1 << edge[0].edge); b.InternalEdges |= (byte)(1 << edge[1].edge);
            }
            void Add(EdgeKey key, int shape, int edge)
            { if (!edges.TryGetValue(key, out var values)) edges.Add(key, values = new List<(int, int)>()); values.Add((shape, edge)); }
        }

        static float3 EdgeVertex(BlockMapFeature feature, int index) => index == 0 ? feature.A : index == 1 ? feature.B : feature.C;

        static int BuildNode(List<BlockMapFeature> features, int[] indices, int first, int count, List<BlockMapBvhNode> nodes)
        {
            float3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            for (int i = first; i < first + count; i++) { var f = features[indices[i]]; min = math.min(min, f.Center - f.Extents); max = math.max(max, f.Center + f.Extents); }
            int index = nodes.Count; nodes.Add(default);
            if (count <= 4) { nodes[index] = new BlockMapBvhNode { Minimum = min, Maximum = max, First = first, Count = count, Left = -1, Right = -1 }; return index; }
            float3 size = max - min; int axis = size.y > size.x ? 1 : 0; if (size.z > size[axis]) axis = 2;
            Array.Sort(indices, first, count, Comparer<int>.Create((a, b) =>
            { int order = features[a].Center[axis].CompareTo(features[b].Center[axis]); return order != 0 ? order : features[a].FeatureId.CompareTo(features[b].FeatureId); }));
            int leftCount = count / 2;
            int left = BuildNode(features, indices, first, leftCount, nodes), right = BuildNode(features, indices, first + leftCount, count - leftCount, nodes);
            nodes[index] = new BlockMapBvhNode { Minimum = min, Maximum = max, Left = left, Right = right, First = -1 };
            return index;
        }

        string HashGeometry()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(BlockMapCollisionData.BakeVersion); writer.Write("finite-double-sided-triangles;closed-convex-solids;continuous-aabb");
            writer.Write(_metadata.Count); foreach (var bytes in _metadata) { writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write(_shapes.Count);
            foreach (var shape in _shapes)
            {
                var f = shape.Feature; writer.Write((byte)f.Kind); writer.Write(f.FeatureId);
                Write(writer, f.Center); Write(writer, f.Extents); writer.Write(f.StaticFriction); writer.Write(f.DynamicFriction);
                if (f.Kind == BlockMapFeatureKind.Triangle) { Write(writer, f.A); Write(writer, f.B); Write(writer, f.C); writer.Write(shape.InternalEdges); }
                if (f.Kind != BlockMapFeatureKind.Convex) continue;
                writer.Write(shape.Vertices.Length); foreach (var v in shape.Vertices) Write(writer, v);
                writer.Write(shape.Triangles.Length); foreach (int index in shape.Triangles) writer.Write(index);
            }
            writer.Flush(); using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
        internal static void Write(BinaryWriter writer, float3 value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }

        readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            readonly float3 _a, _b;
            public EdgeKey(float3 a, float3 b) { if (Compare(a, b) <= 0) { _a = a; _b = b; } else { _a = b; _b = a; } }
            static int Compare(float3 a, float3 b)
            { int n = a.x.CompareTo(b.x); if (n != 0) return n; n = a.y.CompareTo(b.y); return n != 0 ? n : a.z.CompareTo(b.z); }
            public bool Equals(EdgeKey other) => _a.Equals(other._a) && _b.Equals(other._b);
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => unchecked((int)(math.hash(_a) * 397u ^ math.hash(_b)));
        }
    }
}
