using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    /// <summary>Versioned, checked geometry payload shared by offline bakes and load-time fixtures.</summary>
    public static class BlockGeometryArchive
    {
        public const int ArchiveVersion = 1;
        const ulong Magic = 0x3150414D49534C42UL; // BLSIMAP1
        const int HeaderBytes = 112;

        public static byte[] Encode(BlockMapCollisionData geometry)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            var map = geometry.Snapshot;
            if (!map.IsCreated) throw new ObjectDisposedException(nameof(geometry));
            byte[] sourceHash = ParseHash(geometry.GeometryHash);
            byte[] payload;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    for (int i = 0; i < map.Features.Length; i++)
                    {
                        var f = map.Features[i];
                        writer.Write((byte)f.Kind); writer.Write(f.FeatureId); writer.Write(f.AxisStart); writer.Write(f.AxisCount);
                        writer.Write(f.VertexStart); writer.Write(f.VertexCount); writer.Write(f.TriangleStart); writer.Write(f.TriangleCount);
                        Write(writer, f.Center); Write(writer, f.Extents); Write(writer, f.A); Write(writer, f.B); Write(writer, f.C);
                        Write(writer, f.PlaneNormal); writer.Write(f.StaticFriction); writer.Write(f.DynamicFriction);
                    }
                    for (int i = 0; i < map.Axes.Length; i++)
                    {
                        var a = map.Axes[i]; Write(writer, a.Direction); writer.Write(a.Minimum); writer.Write(a.Maximum); writer.Write(a.InternalEdge);
                    }
                    for (int i = 0; i < map.Vertices.Length; i++) Write(writer, map.Vertices[i]);
                    for (int i = 0; i < map.Triangles.Length; i++) writer.Write(map.Triangles[i]);
                    for (int i = 0; i < map.Nodes.Length; i++)
                    {
                        var n = map.Nodes[i]; Write(writer, n.Minimum); Write(writer, n.Maximum);
                        writer.Write(n.Left); writer.Write(n.Right); writer.Write(n.First); writer.Write(n.Count);
                    }
                    for (int i = 0; i < map.FeatureIndices.Length; i++) writer.Write(map.FeatureIndices[i]);
                }
                payload = stream.ToArray();
            }
            using var output = new MemoryStream(checked(HeaderBytes + payload.Length));
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(ArchiveVersion); writer.Write(BlockMapCollisionData.BakeVersion);
                writer.Write(map.Features.Length); writer.Write(map.Axes.Length); writer.Write(map.Vertices.Length);
                writer.Write(map.Triangles.Length); writer.Write(map.Nodes.Length); writer.Write(map.FeatureIndices.Length);
                writer.Write(sourceHash); writer.Write((long)payload.Length);
                writer.Write(Digest(sourceHash, payload, 0, payload.Length)); writer.Write(payload);
            }
            return output.ToArray();
        }

        public static BlockMapCollisionData Decode(byte[] archive, Allocator allocator = Allocator.Persistent)
        {
            if (archive == null) throw new ArgumentNullException(nameof(archive));
            if (archive.Length < HeaderBytes) throw new InvalidDataException("Incomplete block geometry header.");
            using var stream = new MemoryStream(archive, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (reader.ReadUInt64() != Magic || reader.ReadInt32() != ArchiveVersion)
                throw new InvalidDataException("Unsupported block geometry archive version.");
            if (reader.ReadInt32() != BlockMapCollisionData.BakeVersion)
                throw new InvalidDataException("Block collision bake version differs; rebuild the map.");
            int nf = reader.ReadInt32(), na = reader.ReadInt32(), nv = reader.ReadInt32();
            int nt = reader.ReadInt32(), nn = reader.ReadInt32(), ni = reader.ReadInt32();
            if (nf < 0 || na < 0 || nv < 0 || nt < 0 || nn < 0 || ni != nf)
                throw new InvalidDataException("Invalid block geometry array counts.");
            byte[] sourceHash = reader.ReadBytes(32);
            long payloadLength = reader.ReadInt64();
            long expectedLength = (long)nf * 109 + (long)na * 21 + (long)nv * 12 + (long)nt * 4 + (long)nn * 40 + (long)ni * 4;
            if (payloadLength != expectedLength || payloadLength != archive.LongLength - HeaderBytes)
                throw new InvalidDataException("Block geometry lengths do not match; refusing a partial map.");
            byte[] checksum = reader.ReadBytes(32);
            byte[] actual = Digest(sourceHash, archive, HeaderBytes, archive.Length - HeaderBytes);
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != checksum[i]) throw new InvalidDataException("Block geometry source or payload checksum mismatch.");
            var features = new BlockMapFeature[nf]; var axes = new BlockMapAxis[na]; var vertices = new float3[nv];
            var triangles = new int[nt]; var nodes = new BlockMapBvhNode[nn]; var indices = new int[ni];
            for (int i = 0; i < nf; i++) features[i] = new BlockMapFeature
            {
                Kind = (BlockMapFeatureKind)reader.ReadByte(), FeatureId = reader.ReadInt32(), AxisStart = reader.ReadInt32(), AxisCount = reader.ReadInt32(),
                VertexStart = reader.ReadInt32(), VertexCount = reader.ReadInt32(), TriangleStart = reader.ReadInt32(), TriangleCount = reader.ReadInt32(),
                Center = Read3(reader), Extents = Read3(reader), A = Read3(reader), B = Read3(reader), C = Read3(reader), PlaneNormal = Read3(reader),
                StaticFriction = reader.ReadSingle(), DynamicFriction = reader.ReadSingle()
            };
            for (int i = 0; i < na; i++) axes[i] = new BlockMapAxis
            { Direction = Read3(reader), Minimum = reader.ReadSingle(), Maximum = reader.ReadSingle(), InternalEdge = reader.ReadByte() };
            for (int i = 0; i < nv; i++) vertices[i] = Read3(reader);
            for (int i = 0; i < nt; i++) triangles[i] = reader.ReadInt32();
            for (int i = 0; i < nn; i++) nodes[i] = new BlockMapBvhNode
            { Minimum = Read3(reader), Maximum = Read3(reader), Left = reader.ReadInt32(), Right = reader.ReadInt32(), First = reader.ReadInt32(), Count = reader.ReadInt32() };
            for (int i = 0; i < ni; i++) indices[i] = reader.ReadInt32();
            Validate(features, axes, vertices, triangles, nodes, indices);
            return new BlockMapCollisionData(features, axes, vertices, triangles, nodes, indices,
                BitConverter.ToString(sourceHash).Replace("-", ""), allocator);
        }

        static void Validate(BlockMapFeature[] features, BlockMapAxis[] axes, float3[] vertices, int[] triangles,
            BlockMapBvhNode[] nodes, int[] indices)
        {
            var ids = new HashSet<int>();
            foreach (var f in features)
            {
                if (f.Kind > BlockMapFeatureKind.Convex || f.FeatureId <= 0 || !ids.Add(f.FeatureId) ||
                    !Range(f.AxisStart, f.AxisCount, axes.Length) || f.AxisCount == 0 ||
                    !Range(f.VertexStart, f.VertexCount, vertices.Length) ||
                    !Range(f.TriangleStart, (long)f.TriangleCount * 3, triangles.Length) ||
                    !Finite(f.Center) || !Finite(f.Extents) || math.any(f.Extents < 0) ||
                    !Finite(f.A) || !Finite(f.B) || !Finite(f.C) || !Finite(f.PlaneNormal) ||
                    !math.isfinite(f.StaticFriction) || !math.isfinite(f.DynamicFriction) || f.StaticFriction < 0 || f.DynamicFriction < 0)
                    throw new InvalidDataException("Invalid block geometry feature.");
                for (int t = 0; t < f.TriangleCount * 3; t++)
                    if ((uint)triangles[f.TriangleStart + t] >= f.VertexCount) throw new InvalidDataException("Invalid convex geometry index.");
            }
            foreach (var a in axes)
                if (!Finite(a.Direction) || math.abs(math.lengthsq(a.Direction) - 1) > .001f ||
                    !math.isfinite(a.Minimum) || !math.isfinite(a.Maximum) || a.Minimum > a.Maximum || a.InternalEdge > 1)
                    throw new InvalidDataException("Invalid block geometry SAT axis.");
            foreach (var v in vertices) if (!Finite(v)) throw new InvalidDataException("Invalid block geometry vertex.");
            if (features.Length == 0)
            {
                if (nodes.Length != 0) throw new InvalidDataException("Empty geometry has a nonempty BVH.");
                ValidateCanonicalGeometry(features, axes, vertices, triangles, nodes, indices);
                return;
            }
            if (nodes.Length == 0) throw new InvalidDataException("Geometry has no BVH root.");
            var seenNodes = new bool[nodes.Length]; var seenFeatures = new bool[features.Length];
            var stack = new Stack<(int index, int depth)>(); stack.Push((0, 1)); int visited = 0;
            while (stack.Count != 0)
            {
                var next = stack.Pop(); int index = next.index;
                // Queries use a 127-entry traversal stack. Median builder depth is <=32;
                // refuse an externally modified archive that could exceed the query storage.
                if (next.depth > 64) throw new InvalidDataException("Block geometry BVH exceeds supported traversal depth.");
                if ((uint)index >= nodes.Length || seenNodes[index]) throw new InvalidDataException("Invalid or cyclic block geometry BVH.");
                seenNodes[index] = true; visited++;
                var n = nodes[index];
                if (!Finite(n.Minimum) || !Finite(n.Maximum) || math.any(n.Minimum > n.Maximum) || n.Count < 0)
                    throw new InvalidDataException("Invalid block geometry BVH bounds.");
                if (n.Count == 0)
                {
                    if ((uint)n.Left >= nodes.Length || (uint)n.Right >= nodes.Length)
                        throw new InvalidDataException("Invalid block geometry BVH branch.");
                    var l = nodes[n.Left]; var r = nodes[n.Right];
                    if (!Contains(n, l) || !Contains(n, r)) throw new InvalidDataException("Block geometry BVH child escaped its parent.");
                    stack.Push((n.Right, next.depth + 1)); stack.Push((n.Left, next.depth + 1));
                }
                else
                {
                    if (!Range(n.First, n.Count, indices.Length)) throw new InvalidDataException("Invalid block geometry BVH leaf.");
                    for (int k = n.First; k < n.First + n.Count; k++)
                    {
                        int f = indices[k];
                        if ((uint)f >= features.Length || seenFeatures[f]) throw new InvalidDataException("Missing or duplicate BVH feature.");
                        seenFeatures[f] = true;
                        // Builder bounds are expanded for finite zero-thickness triangles.
                        var shape = features[f];
                        if (math.any(shape.Center - shape.Extents < n.Minimum - .00001f) ||
                            math.any(shape.Center + shape.Extents > n.Maximum + .00001f))
                            throw new InvalidDataException("Block geometry feature escaped its BVH leaf.");
                    }
                }
            }
            if (visited != nodes.Length) throw new InvalidDataException("Unreachable block geometry BVH nodes.");
            foreach (bool seen in seenFeatures) if (!seen) throw new InvalidDataException("A block geometry feature has no BVH leaf.");
            ValidateCanonicalGeometry(features, axes, vertices, triangles, nodes, indices);
        }

        static void ValidateCanonicalGeometry(BlockMapFeature[] features, BlockMapAxis[] axes, float3[] vertices,
            int[] triangles, BlockMapBvhNode[] nodes, int[] indices)
        {
            // A valid checksum is not proof that a payload is a valid collision bake.
            // Rebuild every derived value from the primary geometry using the versioned builder:
            // this also proves closed convexity, triangle area, full SAT axes/projections,
            // finite-shape bounds, coplanar edge flags and canonical BVH completeness.
            // Source metadata is represented by the existing source hash and is not reauthored here.
            var builder = new BlockMapCollisionBuilder();
            try
            {
                foreach (var feature in features)
                {
                    if (feature.Kind == BlockMapFeatureKind.Box)
                        builder.AddBox(feature.Center, feature.Extents, feature.FeatureId, feature.StaticFriction, feature.DynamicFriction);
                    else if (feature.Kind == BlockMapFeatureKind.Triangle)
                        builder.AddTriangle(feature.A, feature.B, feature.C, feature.FeatureId, feature.StaticFriction, feature.DynamicFriction);
                    else
                    {
                        var shapeVertices = new float3[feature.VertexCount];
                        var shapeIndices = new int[checked(feature.TriangleCount * 3)];
                        Array.Copy(vertices, feature.VertexStart, shapeVertices, 0, shapeVertices.Length);
                        Array.Copy(triangles, feature.TriangleStart, shapeIndices, 0, shapeIndices.Length);
                        builder.AddConvex(shapeVertices, shapeIndices, feature.FeatureId, feature.StaticFriction, feature.DynamicFriction);
                    }
                }
                using var canonical = builder.Build(Allocator.Temp);
                var expected = canonical.Snapshot;
                if (features.Length != expected.Features.Length || axes.Length != expected.Axes.Length ||
                    vertices.Length != expected.Vertices.Length || triangles.Length != expected.Triangles.Length ||
                    nodes.Length != expected.Nodes.Length || indices.Length != expected.FeatureIndices.Length)
                    throw new InvalidDataException("Block geometry derived array lengths differ from the canonical bake.");
                for (int i = 0; i < features.Length; i++)
                {
                    var a = features[i]; var b = expected.Features[i];
                    if (a.Kind != b.Kind || a.FeatureId != b.FeatureId || a.AxisStart != b.AxisStart || a.AxisCount != b.AxisCount ||
                        a.VertexStart != b.VertexStart || a.VertexCount != b.VertexCount || a.TriangleStart != b.TriangleStart ||
                        a.TriangleCount != b.TriangleCount || !Equal(a.Center, b.Center) || !Equal(a.Extents, b.Extents) ||
                        !Equal(a.A, b.A) || !Equal(a.B, b.B) || !Equal(a.C, b.C) || !Equal(a.PlaneNormal, b.PlaneNormal) ||
                        a.StaticFriction != b.StaticFriction || a.DynamicFriction != b.DynamicFriction)
                        throw new InvalidDataException("Block geometry feature differs from its canonical shape.");
                }
                for (int i = 0; i < axes.Length; i++)
                {
                    var a = axes[i]; var b = expected.Axes[i];
                    if (!Equal(a.Direction, b.Direction) || a.Minimum != b.Minimum || a.Maximum != b.Maximum || a.InternalEdge != b.InternalEdge)
                        throw new InvalidDataException("Block geometry SAT axes or projections differ from the canonical bake.");
                }
                for (int i = 0; i < vertices.Length; i++)
                    if (!Equal(vertices[i], expected.Vertices[i])) throw new InvalidDataException("Block geometry vertices differ from their canonical bake.");
                for (int i = 0; i < triangles.Length; i++)
                    if (triangles[i] != expected.Triangles[i]) throw new InvalidDataException("Block geometry indices differ from their canonical bake.");
                for (int i = 0; i < nodes.Length; i++)
                {
                    var a = nodes[i]; var b = expected.Nodes[i];
                    if (!Equal(a.Minimum, b.Minimum) || !Equal(a.Maximum, b.Maximum) || a.Left != b.Left ||
                        a.Right != b.Right || a.First != b.First || a.Count != b.Count)
                        throw new InvalidDataException("Block geometry BVH differs from the canonical bake.");
                }
                for (int i = 0; i < indices.Length; i++)
                    if (indices[i] != expected.FeatureIndices[i]) throw new InvalidDataException("Block geometry BVH ordering differs from the canonical bake.");
            }
            catch (ArgumentException error)
            { throw new InvalidDataException("Block geometry primary shape is invalid.", error); }
            catch (OverflowException error)
            { throw new InvalidDataException("Block geometry primary shape size overflowed.", error); }
        }

        static bool Equal(float3 a, float3 b) => math.all(a == b);

        static bool Contains(BlockMapBvhNode a, BlockMapBvhNode b) =>
            math.all(a.Minimum <= b.Minimum) && math.all(a.Maximum >= b.Maximum);
        static bool Range(int first, long count, int length) => first >= 0 && count >= 0 && (long)first + count <= length;
        static bool Finite(float3 value) => math.all(math.isfinite(value));
        static void Write(BinaryWriter writer, float3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        static float3 Read3(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        static byte[] Digest(byte[] sourceHash, byte[] data, int first, int length)
        {
            using var sha = SHA256.Create();
            sha.TransformBlock(sourceHash, 0, sourceHash.Length, sourceHash, 0);
            sha.TransformFinalBlock(data, first, length);
            return sha.Hash;
        }
        static byte[] ParseHash(string value)
        {
            if (value == null || value.Length != 64) throw new InvalidDataException("Expected the full block geometry SHA-256 identity.");
            var result = new byte[32];
            for (int i = 0; i < result.Length; i++)
                if (!byte.TryParse(value.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out result[i]))
                    throw new InvalidDataException("Invalid block geometry identity.");
            return result;
        }
    }
}
