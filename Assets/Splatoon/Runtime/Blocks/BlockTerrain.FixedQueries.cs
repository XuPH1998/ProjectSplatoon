using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        static readonly ProfilerMarker FixedSafetyCapsuleQueryCpu = new("Splatoon.Fixed.SafetyCapsuleQuery");
        static readonly ProfilerMarker FixedSafetyBoxQueryCpu = new("Splatoon.Fixed.SafetyBoxQuery");

        public static BlockContact FixedContact(BlockFixedGridHit hit) => new()
        {
            Owner = hit.State.Owner, Cell = hit.State.Cell, Chunk = BlockWorld.ChunkAt(hit.State.Cell),
            Kind = BlockContactKind.FixedGrid, FixedHandle = hit.State.Handle, Revision = hit.State.Revision,
            Point = hit.Point, Normal = hit.Normal, OrientationNormal = hit.Normal,
            Stable = true, Climbable = Mathf.Abs(hit.Normal.y) < .7f
        };

        public bool TryCastFixed(Vector3 origin, Vector3 direction, float distance, float radius, out BlockFixedGridHit hit)
        {
            hit = default;
            return FixedGridMode && BlockFixedGridQueries.Cast(FixedBlocks, origin, direction, distance, radius, out hit);
        }

        public bool TryOverlapFixed(Vector3 origin, float radius, Vector3 fallback, out BlockFixedGridHit hit)
        {
            hit = default;
            return FixedGridMode && BlockFixedGridQueries.OverlapSphere(FixedBlocks, origin, radius, fallback, out hit);
        }

        public bool TrySupportFixed(Vector3 feet, float radius, float reach, out BlockFixedGridHit hit)
        {
            hit = default;
            return FixedGridMode && BlockFixedGridQueries.Support(FixedBlocks, feet, radius, reach, out hit);
        }

        public bool FixedCapsuleOverlaps(Vector3 center, float halfSegment, float radius)
            => FixedGridMode && BlockFixedGridQueries.OverlapCapsule(FixedBlocks, center, halfSegment, radius, Vector3.up, out _, .00001f);

        public bool FixedBoxOverlaps(Vector3 center, Vector3 half, Quaternion rotation)
            => FixedGridMode && BlockFixedGridQueries.OverlapBox(FixedBlocks, center, half, rotation);

        BlockWorld FixedSafetyOccupancy => _fixedSafetyCandidate ?? (FixedGridMode ? FixedBlocks?.Storage : null);

        bool FixedSafetyCapsuleOverlaps(Vector3 center, float halfSegment, float radius)
        {
            using var timing = FixedSafetyCapsuleQueryCpu.Auto();
            var occupancy = FixedSafetyOccupancy;
            if (occupancy == null) return false;
            Vector3 extent = new(radius, radius + halfSegment, radius);
            Vector3Int low = BlockWorld.CellAt(center - extent), high = BlockWorld.CellAt(center + extent);
            int firstChunkX = BlockWorld.ChunkAt(low).x, lastChunkX = BlockWorld.ChunkAt(high).x;
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++)
            for (int chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
            {
                int originX = BlockWorld.ChunkOrigin(new Vector3Int(chunkX, 0, 0)).x;
                uint occupied = FixedSafetyXRow(occupancy, low.x, high.x, originX, y, z);
                while (occupied != 0)
                {
                    int x = originX + math.tzcnt(occupied); occupied &= occupied - 1;
                    var cell = new Vector3Int(x, y, z);
                    var bounds = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize);
                    bounds.Expand(Vector3.up * (halfSegment * 2));
                    if ((center - bounds.ClosestPoint(center)).sqrMagnitude < radius * radius - .000001f) return true;
                }
            }
            return false;
        }

        bool FixedSafetyBoxOverlaps(Vector3 center, Vector3 half, Quaternion rotation)
        {
            using var timing = FixedSafetyBoxQueryCpu.Auto();
            var occupancy = FixedSafetyOccupancy;
            if (occupancy == null) return false;
            Vector3 axisX = rotation * Vector3.right, axisY = rotation * Vector3.up, axisZ = rotation * Vector3.forward;
            Vector3 extent = Abs(axisX) * half.x + Abs(axisY) * half.y + Abs(axisZ) * half.z;
            Vector3Int low = BlockWorld.CellAt(center - extent), high = BlockWorld.CellAt(center + extent);
            int firstChunkX = BlockWorld.ChunkAt(low).x, lastChunkX = BlockWorld.ChunkAt(high).x;
            Vector3 cellHalf = Vector3.one * (BlockWorld.CellSize * .5f);
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++)
            for (int chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
            {
                int originX = BlockWorld.ChunkOrigin(new Vector3Int(chunkX, 0, 0)).x;
                uint occupied = FixedSafetyXRow(occupancy, low.x, high.x, originX, y, z);
                while (occupied != 0)
                {
                    int x = originX + math.tzcnt(occupied); occupied &= occupied - 1;
                    var cell = new Vector3Int(x, y, z);
                    if (AabbIntersectsBox(BlockWorld.Center(cell), cellHalf, center, half, axisX, axisY, axisZ)) return true;
                }
            }
            return false;
        }

        static uint FixedSafetyXRow(BlockWorld occupancy, int lowX, int highX, int originX, int y, int z)
        {
            int first = Mathf.Max(0, lowX - originX), last = Mathf.Min(BlockWorld.ChunkSize - 1, highX - originX);
            if (first > last) return 0;
            // Each local row has sixteen x bits. A full row shifts by 16, never 32;
            // clipping both ends keeps negative/cross-chunk ranges in the old z/y/x order.
            uint mask = ((1u << (last - first + 1)) - 1u) << first;
            return (uint)occupancy.OccupiedRow(new Vector3Int(originX, y, z), 0) & mask;
        }
    }
}
