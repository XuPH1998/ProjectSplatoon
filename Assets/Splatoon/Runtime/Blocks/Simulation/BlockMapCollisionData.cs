using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public struct BlockStaticHit
    {
        public int FeatureId;
        public float3 Normal, Point;
        public float Fraction;
        // Positive means penetration; a contact collected within skin can be negative.
        public float Penetration;
        // Authored map values, before combining with the moving body's material.
        public float StaticFriction, DynamicFriction;
    }

    public enum BlockMapFeatureKind : byte { Box, Triangle, Convex }

    public struct BlockMapFeature
    {
        public BlockMapFeatureKind Kind;
        public int FeatureId, AxisStart, AxisCount, VertexStart, VertexCount, TriangleStart, TriangleCount;
        public float3 Center, Extents, A, B, C, PlaneNormal;
        public float StaticFriction, DynamicFriction;
    }

    public struct BlockMapAxis
    {
        public float3 Direction;
        public float Minimum, Maximum;
        public byte InternalEdge;
    }

    public struct BlockMapBvhNode
    {
        public float3 Minimum, Maximum;
        public int Left, Right, First, Count;
    }

    /// <summary>Read-only views owned by BlockMapCollisionData. Complete readers before disposal.</summary>
    public readonly struct BlockMapSnapshot
    {
        public readonly NativeArray<BlockMapFeature>.ReadOnly Features;
        public readonly NativeArray<BlockMapAxis>.ReadOnly Axes;
        public readonly NativeArray<float3>.ReadOnly Vertices;
        public readonly NativeArray<int>.ReadOnly Triangles;
        public readonly NativeArray<BlockMapBvhNode>.ReadOnly Nodes;
        public readonly NativeArray<int>.ReadOnly FeatureIndices;
        public bool IsCreated => Features.IsCreated && Nodes.IsCreated;
        public int FeatureCount => Features.IsCreated ? Features.Length : 0;

        internal BlockMapSnapshot(BlockMapCollisionData data)
        {
            Features = data.Features.AsReadOnly(); Axes = data.Axes.AsReadOnly();
            Vertices = data.Vertices.AsReadOnly(); Triangles = data.Triangles.AsReadOnly();
            Nodes = data.Nodes.AsReadOnly(); FeatureIndices = data.FeatureIndices.AsReadOnly();
        }
    }

    /// <summary>Single owner of an immutable geometry bake; snapshots never own or mutate its storage.</summary>
    public sealed class BlockMapCollisionData : IDisposable
    {
        public const int BakeVersion = 1;
        internal NativeArray<BlockMapFeature> Features;
        internal NativeArray<BlockMapAxis> Axes;
        internal NativeArray<float3> Vertices;
        internal NativeArray<int> Triangles;
        internal NativeArray<BlockMapBvhNode> Nodes;
        internal NativeArray<int> FeatureIndices;
        public string GeometryHash { get; }
        public BlockMapSnapshot Snapshot => new(this);

        internal BlockMapCollisionData(BlockMapFeature[] features, BlockMapAxis[] axes, float3[] vertices,
            int[] triangles, BlockMapBvhNode[] nodes, int[] indices, string hash, Allocator allocator)
        {
            GeometryHash = hash;
            try
            {
                Features = new NativeArray<BlockMapFeature>(features, allocator);
                Axes = new NativeArray<BlockMapAxis>(axes, allocator);
                Vertices = new NativeArray<float3>(vertices, allocator);
                Triangles = new NativeArray<int>(triangles, allocator);
                Nodes = new NativeArray<BlockMapBvhNode>(nodes, allocator);
                FeatureIndices = new NativeArray<int>(indices, allocator);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Features.IsCreated) Features.Dispose();
            if (Axes.IsCreated) Axes.Dispose();
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (Nodes.IsCreated) Nodes.Dispose();
            if (FeatureIndices.IsCreated) FeatureIndices.Dispose();
        }
    }
}
