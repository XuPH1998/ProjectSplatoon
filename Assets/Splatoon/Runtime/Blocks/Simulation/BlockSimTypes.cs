using System;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    [Serializable]
    public struct BlockSimHandle : IEquatable<BlockSimHandle>
    {
        public uint epoch, round, generation;
        public ulong id;

        public BlockSimHandle(uint epoch, uint round, ulong id, uint generation = 1)
        { this.epoch = epoch; this.round = round; this.id = id; this.generation = generation; }

        public bool IsValid => id != 0 && generation != 0;
        public bool Equals(BlockSimHandle other) => epoch == other.epoch && round == other.round &&
            id == other.id && generation == other.generation;
        public override bool Equals(object obj) => obj is BlockSimHandle other && Equals(other);
        public override int GetHashCode() => unchecked((((int)epoch * 397 ^ (int)round) * 397 ^ id.GetHashCode()) * 397 ^ (int)generation);
        public static bool operator ==(BlockSimHandle a, BlockSimHandle b) => a.Equals(b);
        public static bool operator !=(BlockSimHandle a, BlockSimHandle b) => !a.Equals(b);
        public override string ToString() => $"{epoch}/{round}/{id}/{generation}";
    }

    public enum BlockSimMode : byte { Active, Resting, Frozen }

    [Serializable]
    public struct BlockSimState
    {
        public BlockSimHandle handle;
        public byte owner;
        public float3 position, velocity;
        public BlockSimMode mode;
        public uint poseVersion;
    }

    [Serializable]
    public struct BlockAabb
    {
        public float3 center, extents;
        public BlockAabb(float3 center, float3 extents) { this.center = center; this.extents = extents; }
        public float3 Min => center - extents;
        public float3 Max => center + extents;
        public bool IsFinite => math.all(math.isfinite(center)) && math.all(math.isfinite(extents)) && math.all(extents >= 0);
        public bool Overlaps(BlockAabb other) => math.all(math.abs(center - other.center) <= extents + other.extents);
        public static BlockAabb FromMinMax(float3 min, float3 max) => new((min + max) * .5f, (max - min) * .5f);
        public BlockAabb Expanded(float distance) => new(center, extents + distance);
        public BlockAabb Swept(float3 displacement) => FromMinMax(math.min(Min, Min + displacement), math.max(Max, Max + displacement));
    }
}
