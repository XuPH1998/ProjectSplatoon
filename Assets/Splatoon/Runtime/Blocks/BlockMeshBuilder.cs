using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Blocks
{
    public sealed class BlockMeshData
    {
        public Vector3[] Vertices, Normals;
        public Color32[] Colors;
        public int[] Triangles;
        public Mesh Upload(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = Vertices;
            if (Normals.Length != 0) mesh.normals = Normals;
            if (Colors.Length != 0) mesh.colors32 = Colors;
            mesh.triangles = Triangles;
            mesh.RecalculateBounds(); return mesh;
        }
    }

    public static class BlockMeshBuilder
    {
        static readonly Vector3Int[] Axes = { Vector3Int.right, Vector3Int.up, new(0, 0, 1) };
        // Greedy coplanar rectangles retain the 10 cm appearance through world-space shader lines.
        // Only temporary build buffers are cached. Returned ToArray outputs
        // remain owned by each BlockMeshData across later builds and Upload.
        const int MaxCachedScratch = 2;
        const int ExposureMaskBytes = 6 * BlockWorld.ChunkSize * BlockWorld.ChunkSize * BlockWorld.ChunkSize;
        const long MaxCachedScratchPayloadBytes = 1024L * 1024;
        static readonly object ScratchGate = new();
        static readonly Scratch[] ScratchCache = new Scratch[MaxCachedScratch];
        static int _cachedScratch;
        sealed class Scratch
        {
            public readonly List<Vector3> Vertices = new(), Normals = new();
            public readonly List<Color32> Colors = new();
            public readonly List<int> Triangles = new();
            byte[] _mask, _exposure;
            public byte[] Exposure => _exposure ??= new byte[ExposureMaskBytes];
            public byte[] Mask => _mask ??= new byte[BlockWorld.ChunkSize * BlockWorld.ChunkSize];
            public long CapacityPayloadBytes => 12L * (Vertices.Capacity + (long)Normals.Capacity)
                + 4L * (Colors.Capacity + (long)Triangles.Capacity) + (_mask?.Length ?? 0) + (_exposure?.Length ?? 0);
            public void Clear()
            { Vertices.Clear(); Normals.Clear(); Colors.Clear(); Triangles.Clear(); }
        }
        static Scratch RentScratch()
        {
            lock (ScratchGate)
            {
                if (_cachedScratch != 0)
                {
                    int index = --_cachedScratch;
                    var scratch = ScratchCache[index]; ScratchCache[index] = null;
                    return scratch;
                }
            }
            return new Scratch();
        }
        static void ReturnScratch(Scratch scratch)
        {
            // Oversized buffers are discarded instead of retained per thread.
            if (scratch.CapacityPayloadBytes > MaxCachedScratchPayloadBytes) return;
            scratch.Clear();
            lock (ScratchGate)
            {
                if (_cachedScratch < MaxCachedScratch) ScratchCache[_cachedScratch++] = scratch;
            }
        }
        public static BlockMeshData Build(BlockWorld world, Vector3Int chunk, bool collision)
        {
            var scratch = RentScratch();
            try
            {
                var vertices = scratch.Vertices; var normals = scratch.Normals;
                var colors = scratch.Colors; var triangles = scratch.Triangles;
                world.Chunks.TryGetValue(chunk, out var current);
                var cells = current?.Cells;
                // Cells remain the geometry authority. Count is only a cheap hint;
                // confirm zero-count chunks in case an external writer bypassed Set.
                if (cells == null || current.Count == 0 && !HasOccupiedCell(cells))
                    return new BlockMeshData { Vertices = vertices.ToArray(), Normals = collision ? Array.Empty<Vector3>() : normals.ToArray(), Colors = collision ? Array.Empty<Color32>() : colors.ToArray(), Triangles = triangles.ToArray() };
                var mask = scratch.Mask;
                for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
                {
                    var n = Axes[axis] * sign; var u = Axes[(axis + 1) % 3]; var v = Axes[(axis + 2) % 3];
                    int stride = 1 << (4 * axis), uStride = 1 << (4 * ((axis + 1) % 3)), vStride = 1 << (4 * ((axis + 2) % 3));
                    world.Chunks.TryGetValue(chunk + n, out var neighbor);
                    var neighborCells = neighbor?.Cells;
                    for (int slice = 0; slice < BlockWorld.ChunkSize; slice++)
                    {
                        bool boundary = sign < 0 ? slice == 0 : slice == BlockWorld.ChunkSize - 1;
                        int adjacentOffset = boundary ? -sign * (BlockWorld.ChunkSize - 1) * stride : sign * stride;
                        var adjacentCells = boundary ? neighborCells : cells;
                        for (int b = 0; b < BlockWorld.ChunkSize; b++) for (int a = 0; a < BlockWorld.ChunkSize; a++)
                        {
                            int index = slice * stride + a * uStride + b * vStride;
                            var own = cells == null ? default : cells[index];
                            bool covered = adjacentCells != null && adjacentCells[index + adjacentOffset].Occupied;
                            mask[a + b * BlockWorld.ChunkSize] = own.Occupied && !covered ? collision ? (byte)1 : own.Owner : (byte)0;
                        }
                        for (int b = 0; b < BlockWorld.ChunkSize; b++) for (int a = 0; a < BlockWorld.ChunkSize; a++)
                        {
                            byte owner = mask[a + b * BlockWorld.ChunkSize]; if (owner == 0) continue;
                            int width = 1; while (a + width < BlockWorld.ChunkSize && mask[a + width + b * BlockWorld.ChunkSize] == owner) width++;
                            int height = 1;
                            for (; b + height < BlockWorld.ChunkSize; height++)
                            { bool same = true; for (int x = 0; x < width; x++) if (mask[a + x + (b + height) * BlockWorld.ChunkSize] != owner) { same = false; break; } if (!same) break; }
                            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) mask[a + x + (b + y) * BlockWorld.ChunkSize] = 0;
                            Vector3 p = (Vector3)(Axes[axis] * (slice + (sign > 0 ? 1 : 0)) + u * a + v * b) * BlockWorld.CellSize;
                            Vector3 du = (Vector3)u * (width * BlockWorld.CellSize), dv = (Vector3)v * (height * BlockWorld.CellSize);
                            int first = vertices.Count; vertices.Add(p); vertices.Add(p + du); vertices.Add(p + du + dv); vertices.Add(p + dv);
                            if (!collision)
                            {
                                Color32 color = owner == 1 ? new Color32(241, 71, 121, 255) : new Color32(125, 227, 232, 255);
                                for (int i = 0; i < 4; i++) { normals.Add(n); colors.Add(color); }
                            }
                            if (sign > 0) { triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2); triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3); }
                            else { triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1); triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2); }
                        }
                    }
                }
                return new BlockMeshData { Vertices = vertices.ToArray(), Normals = collision ? Array.Empty<Vector3>() : normals.ToArray(), Colors = collision ? Array.Empty<Color32>() : colors.ToArray(), Triangles = triangles.ToArray() };
            }
            finally { ReturnScratch(scratch); }
        }
        // One exposure scan feeds both exact greedy meshes. The owner-aware render
        // mask and owner-independent collision mask are merged separately.
        // renderReadyStamp is an optional diagnostic split: the render region
        // includes the shared scan; the collision region only emits collision.
        public static void BuildPair(BlockWorld world, Vector3Int chunk,
            out BlockMeshData render, out BlockMeshData collision,
            bool track, out long renderReadyStamp)
        {
            render = collision = null; renderReadyStamp = 0;
            var scratch = RentScratch();
            try
            {
                world.Chunks.TryGetValue(chunk, out var current);
                var cells = current?.Cells;
                if (cells == null || current.Count == 0 && !HasOccupiedCell(cells))
                {
                    render = TakeData(scratch, false);
                    renderReadyStamp = track ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    collision = TakeData(scratch, true); return;
                }
                var exposure = scratch.Exposure;
                int offset = 0;
                for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
                {
                    var n = Axes[axis] * sign;
                    int stride = 1 << (4 * axis), uStride = 1 << (4 * ((axis + 1) % 3)), vStride = 1 << (4 * ((axis + 2) % 3));
                    world.Chunks.TryGetValue(chunk + n, out var neighbor);
                    var neighborCells = neighbor?.Cells;
                    for (int slice = 0; slice < BlockWorld.ChunkSize; slice++)
                    {
                        bool boundary = sign < 0 ? slice == 0 : slice == BlockWorld.ChunkSize - 1;
                        int adjacentOffset = boundary ? -sign * (BlockWorld.ChunkSize - 1) * stride : sign * stride;
                        var adjacentCells = boundary ? neighborCells : cells;
                        for (int b = 0; b < BlockWorld.ChunkSize; b++) for (int a = 0; a < BlockWorld.ChunkSize; a++)
                        {
                            int index = slice * stride + a * uStride + b * vStride;
                            var own = cells[index];
                            bool covered = adjacentCells != null && adjacentCells[index + adjacentOffset].Occupied;
                            exposure[offset + a + b * BlockWorld.ChunkSize] = own.Occupied && !covered ? own.Owner : (byte)0;
                        }
                        offset += BlockWorld.ChunkSize * BlockWorld.ChunkSize;
                    }
                }
                render = EmitExposure(scratch, exposure, false);
                renderReadyStamp = track ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                scratch.Clear();
                collision = EmitExposure(scratch, exposure, true);
            }
            finally { ReturnScratch(scratch); }
        }
        static BlockMeshData TakeData(Scratch scratch, bool collision)
            => new() { Vertices = scratch.Vertices.ToArray(), Normals = collision ? Array.Empty<Vector3>() : scratch.Normals.ToArray(),
                Colors = collision ? Array.Empty<Color32>() : scratch.Colors.ToArray(), Triangles = scratch.Triangles.ToArray() };
        static BlockMeshData EmitExposure(Scratch scratch, byte[] exposure, bool collision)
        {
            var vertices = scratch.Vertices; var normals = scratch.Normals;
            var colors = scratch.Colors; var triangles = scratch.Triangles;
            var mask = scratch.Mask; int offset = 0;
            for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
            {
                var n = Axes[axis] * sign; var u = Axes[(axis + 1) % 3]; var v = Axes[(axis + 2) % 3];
                for (int slice = 0; slice < BlockWorld.ChunkSize; slice++)
                {
                    for (int i = 0; i < mask.Length; i++)
                    { byte owner = exposure[offset + i]; mask[i] = collision && owner != 0 ? (byte)1 : owner; }
                    offset += BlockWorld.ChunkSize * BlockWorld.ChunkSize;
                    for (int b = 0; b < BlockWorld.ChunkSize; b++) for (int a = 0; a < BlockWorld.ChunkSize; a++)
                    {
                        byte owner = mask[a + b * BlockWorld.ChunkSize]; if (owner == 0) continue;
                        int width = 1; while (a + width < BlockWorld.ChunkSize && mask[a + width + b * BlockWorld.ChunkSize] == owner) width++;
                        int height = 1;
                        for (; b + height < BlockWorld.ChunkSize; height++)
                        { bool same = true; for (int x = 0; x < width; x++) if (mask[a + x + (b + height) * BlockWorld.ChunkSize] != owner) { same = false; break; } if (!same) break; }
                        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) mask[a + x + (b + y) * BlockWorld.ChunkSize] = 0;
                        Vector3 p = (Vector3)(Axes[axis] * (slice + (sign > 0 ? 1 : 0)) + u * a + v * b) * BlockWorld.CellSize;
                        Vector3 du = (Vector3)u * (width * BlockWorld.CellSize), dv = (Vector3)v * (height * BlockWorld.CellSize);
                        int first = vertices.Count; vertices.Add(p); vertices.Add(p + du); vertices.Add(p + du + dv); vertices.Add(p + dv);
                        if (!collision)
                        {
                            Color32 color = owner == 1 ? new Color32(241, 71, 121, 255) : new Color32(125, 227, 232, 255);
                            for (int i = 0; i < 4; i++) { normals.Add(n); colors.Add(color); }
                        }
                        if (sign > 0) { triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2); triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3); }
                        else { triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1); triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2); }
                    }
                }
            }
            return TakeData(scratch, collision);
        }
        static bool HasOccupiedCell(BlockCell[] cells)
        {
            for (int i = 0; i < cells.Length; i++) if (cells[i].Occupied) return true;
            return false;
        }
    }
}
