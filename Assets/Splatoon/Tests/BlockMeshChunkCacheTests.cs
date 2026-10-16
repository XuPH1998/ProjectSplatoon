#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockMeshChunkCacheTests
    {
        static readonly Vector3Int[] Directions = { Vector3Int.left, Vector3Int.right, Vector3Int.down,
            Vector3Int.up, new(0, 0, -1), new(0, 0, 1) };
        static void Compare(BlockWorld world, Vector3Int chunk, bool collision)
        {
            byte[] before = world.EncodeSnapshot();
            var expected = FrozenBlockMeshBuilder.Build(world, chunk, collision);
            var actual = BlockMeshBuilder.Build(world, chunk, collision);
            CollectionAssert.AreEqual(expected.Vertices, actual.Vertices, "vertex order and exact coordinates");
            CollectionAssert.AreEqual(expected.Triangles, actual.Triangles, "winding and greedy rectangle order");
            if (collision)
            { Assert.That(actual.Normals, Is.Empty); Assert.That(actual.Colors, Is.Empty); }
            else
            {
                CollectionAssert.AreEqual(expected.Normals, actual.Normals);
                CollectionAssert.AreEqual(expected.Colors, actual.Colors, "every render owner color");
            }
            CollectionAssert.AreEqual(before, world.EncodeSnapshot(), "read-only generation leaves all state unchanged");
        }
        static void Fill(BlockWorld world, Vector3Int chunk, Func<int, byte> owner)
        {
            for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
            {
                byte value = owner(i);
                if (value != 0) world.Set(BlockWorld.CellFromIndex(chunk, i), BlockCell.Create(value, (i & 3) == 0));
            }
        }
        [TestCase(false)] [TestCase(true)]
        public void EmptyAndMissingChunksHaveExactlyTheOriginalEmptyGeometry(bool collision)
        {
            var world = new BlockWorld(new Vector2(64, 64));
            Compare(world, new Vector3Int(-3, -2, 4), collision);
            world.Set(new Vector3Int(-17, 1, 16), BlockCell.Create(1));
            world.Set(new Vector3Int(-17, 1, 16), default);
            Compare(world, new Vector3Int(-2, 0, 1), collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void SixChunkSeamsAndMixedOwnersKeepEveryOriginalExteriorFace(bool collision)
        {
            var world = new BlockWorld(new Vector2(64, 64));
            var key = new Vector3Int(-1, -1, -1);
            Fill(world, key, i => (byte)(1 + i % 2));
            foreach (var direction in Directions)
                Fill(world, key + direction, i => (byte)(i % 5 == 0 ? 0 : 1 + i % 2));
            Compare(world, key, collision);
            foreach (var direction in Directions) Compare(world, key + direction, collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void FullyEnclosedChunkAndInteriorVoidsKeepOriginalGreedyRectangles(bool collision)
        {
            var world = new BlockWorld(new Vector2(64, 64));
            Fill(world, Vector3Int.zero, _ => 1);
            foreach (var direction in Directions) Fill(world, direction, _ => 2);
            Compare(world, Vector3Int.zero, collision);
            for (int x = 4; x <= 11; x++) for (int y = 5; y <= 10; y++) for (int z = 6; z <= 9; z++)
                world.Set(new Vector3Int(x, y, z), default);
            Compare(world, Vector3Int.zero, collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void DetachedMixedRunAndItsHolesNeverContributeStaticGeometry(bool collision)
        {
            var world = new BlockWorld(new Vector2(64, 64));
            var bottom = new Vector3Int(-1, 15, -1);
            for (int y = 0; y < 5; y++) world.Set(bottom + Vector3Int.up * y, BlockCell.Create((byte)(1 + y % 2)));
            var run = world.Detach(bottom, 5, 1);
            world.RemoveFallingCell(run.Id, 2, out _);
            Compare(world, new Vector3Int(-1, 0, -1), collision);
            Compare(world, new Vector3Int(-1, 1, -1), collision);
        }
        [TestCase(17, false)] [TestCase(17, true)] [TestCase(119, false)] [TestCase(119, true)]
        [TestCase(991, false)] [TestCase(991, true)]
        public void SeededSparseAndDenseNegativeCoordinateWorldsMatchTheFrozenBuilder(int seed, bool collision)
        {
            var random = new System.Random(seed); var world = new BlockWorld(new Vector2(64, 64));
            for (int x = -1; x <= 0; x++) for (int y = -1; y <= 0; y++) for (int z = -1; z <= 0; z++)
                Fill(world, new Vector3Int(x, y, z), _ => (byte)(random.Next(100) < seed % 71 + 10 ? random.Next(1, 3) : 0));
            for (int x = -1; x <= 0; x++) for (int y = -1; y <= 0; y++) for (int z = -1; z <= 0; z++)
                Compare(world, new Vector3Int(x, y, z), collision);
        }
    }
}


namespace Splatoon.Tests
{
    static class FrozenBlockMeshBuilder
    {
        static readonly Vector3Int[] Axes = { Vector3Int.right, Vector3Int.up, new(0, 0, 1) };
        // Greedy coplanar rectangles retain the 10 cm appearance through world-space shader lines.
        public static BlockMeshData Build(BlockWorld world, Vector3Int chunk, bool collision)
        {
            var vertices = new List<Vector3>(); var normals = new List<Vector3>();
            var colors = new List<Color32>(); var triangles = new List<int>();
            var mask = new byte[BlockWorld.ChunkSize * BlockWorld.ChunkSize]; var origin = BlockWorld.ChunkOrigin(chunk);
            for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
            {
                var n = Axes[axis] * sign; var u = Axes[(axis + 1) % 3]; var v = Axes[(axis + 2) % 3];
                for (int slice = 0; slice < BlockWorld.ChunkSize; slice++)
                {
                    for (int b = 0; b < BlockWorld.ChunkSize; b++) for (int a = 0; a < BlockWorld.ChunkSize; a++)
                    {
                        var cell = origin + Axes[axis] * slice + u * a + v * b;
                        mask[a + b * BlockWorld.ChunkSize] = world.TryGet(cell, out var own) && !world.TryGet(cell + n, out _) ? collision ? (byte)1 : own.Owner : (byte)0;
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
                        Color32 color = owner == 1 ? new Color32(241, 71, 121, 255) : new Color32(125, 227, 232, 255);
                        for (int i = 0; i < 4; i++) { normals.Add(n); colors.Add(color); }
                        if (sign > 0) { triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2); triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3); }
                        else { triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1); triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2); }
                    }
                }
            }
            return new BlockMeshData { Vertices = vertices.ToArray(), Normals = normals.ToArray(), Colors = colors.ToArray(), Triangles = triangles.ToArray() };
        }
    }
}

#endif
