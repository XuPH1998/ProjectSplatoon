#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Uses the complete frozen pre-cache Build already shipped with the cache
    // oracle. No Editor scene, mesh Upload, physics, worker or performance probe.
    public sealed class BlockMeshEmptyChunkTests
    {
        static readonly Vector3Int Key = new(-2, -2, -2);
        static readonly Vector3Int[] Directions = { Vector3Int.left, Vector3Int.right, Vector3Int.down,
            Vector3Int.up, new(0, 0, -1), new(0, 0, 1) };
        static BlockWorld World() => new(new Vector2(64, 64), 19);
        static void FillNeighbors(BlockWorld world, Vector3Int key)
        {
            foreach (var direction in Directions)
                for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
                    Assert.That(world.Set(BlockWorld.CellFromIndex(key + direction, i),
                        BlockCell.Create((byte)(1 + i % 2), (i & 3) == 0)), Is.True);
        }
        static BlockChunk MakeUnsealedEmpty(BlockWorld world, Vector3Int key)
        {
            var cell = BlockWorld.CellFromIndex(key, 0);
            Assert.That(world.Set(cell, BlockCell.Create(1)), Is.True);
            Assert.That(world.Set(cell, default), Is.True);
            Assert.That(world.Chunks.TryGetValue(key, out var chunk), Is.True);
            Assert.That(chunk.Count, Is.Zero); Assert.That(chunk.Cells.All(c => !c.Occupied), Is.True);
            return chunk;
        }
        static BlockMeshData Compare(BlockWorld world, Vector3Int key, bool collision)
        {
            var before = new WorldState(world);
            var expected = FrozenBlockMeshBuilder.Build(world, key, collision);
            before.AssertUnchanged(world, "complete frozen oracle");
            var actual = BlockMeshBuilder.Build(world, key, collision);
            before.AssertUnchanged(world, "candidate build");
            Assert.That(actual, Is.Not.SameAs(expected));
            Assert.That(actual.Vertices, Is.Not.Null); Assert.That(actual.Triangles, Is.Not.Null);
            Assert.That(actual.Normals, Is.Not.Null); Assert.That(actual.Colors, Is.Not.Null);
            CollectionAssert.AreEqual(expected.Vertices, actual.Vertices, "every vertex and order");
            CollectionAssert.AreEqual(expected.Triangles, actual.Triangles, "every index and winding");
            if (collision)
            { Assert.That(actual.Normals, Is.Empty); Assert.That(actual.Colors, Is.Empty); }
            else
            {
                CollectionAssert.AreEqual(expected.Normals, actual.Normals, "every normal and order");
                CollectionAssert.AreEqual(expected.Colors, actual.Colors, "every render owner color");
            }
            return actual;
        }
        static void Empty(BlockMeshData mesh)
        {
            Assert.That(mesh.Vertices, Is.Empty); Assert.That(mesh.Triangles, Is.Empty);
            Assert.That(mesh.Normals, Is.Empty); Assert.That(mesh.Colors, Is.Empty);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void MissingPositiveAndNegativeChunksKeepFourEmptyArrays(bool collision, bool negative)
        {
            var world = World(); var key = negative ? Key : new Vector3Int(2, 1, 3);
            Assert.That(world.Chunks.ContainsKey(key), Is.False);
            Empty(Compare(world, key, collision));
        }
        [TestCase(false)] [TestCase(true)]
        public void MissingChunkWithSixOccupiedMixedOwnerNeighborsRemainsEmpty(bool collision)
        {
            var world = World(); FillNeighbors(world, Key);
            Assert.That(world.Chunks.Count, Is.EqualTo(6)); Assert.That(world.Chunks.ContainsKey(Key), Is.False);
            Empty(Compare(world, Key, collision));
            foreach (var direction in Directions) Compare(world, Key + direction, collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void UnsealedZeroCountChunkAndAllSixNeighborsKeepOriginalGeometry(bool collision)
        {
            var world = World(); MakeUnsealedEmpty(world, Key); FillNeighbors(world, Key);
            Assert.That(world.Chunks.Count, Is.EqualTo(7));
            Empty(Compare(world, Key, collision));
            foreach (var direction in Directions) Compare(world, Key + direction, collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void SealRemovedChunkAndTombstoneKeepEmptyGeometryAndRevision(bool collision)
        {
            var world = World(); MakeUnsealedEmpty(world, Key); FillNeighbors(world, Key);
            world.Seal(6, world.Chunks.Keys.ToArray());
            Assert.That(world.Chunks.ContainsKey(Key), Is.False);
            Assert.That(world.RevisionKeys.Contains(Key), Is.True); Assert.That(world.ChunkRevision(Key), Is.EqualTo(6u));
            Empty(Compare(world, Key, collision));
            Assert.That(world.ChunkRevision(Key), Is.EqualTo(6u));
            foreach (var direction in Directions) Compare(world, Key + direction, collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void CowCloneRemovalSealAndReviveNeverChangeSourceGeometry(bool collision)
        {
            var source = World(); var first = BlockWorld.CellFromIndex(Key, 0);
            Assert.That(source.Set(first, BlockCell.Create(1, true)), Is.True); source.Seal(5, new[] { Key });
            var sourceBefore = new WorldState(source); var clone = source.Clone();
            Assert.That(clone.Chunks[Key], Is.SameAs(source.Chunks[Key]));
            Assert.That(clone.Set(first, default), Is.True);
            Assert.That(clone.Chunks[Key], Is.Not.SameAs(source.Chunks[Key])); Assert.That(clone.Chunks[Key].Count, Is.Zero);
            Assert.That(Compare(source, Key, collision).Vertices.Length, Is.GreaterThan(0));
            Empty(Compare(clone, Key, collision)); sourceBefore.AssertUnchanged(source, "clone removal");
            clone.Seal(6, new[] { Key }); Assert.That(clone.Chunks.ContainsKey(Key), Is.False);
            Assert.That(clone.ChunkRevision(Key), Is.EqualTo(6u)); Empty(Compare(clone, Key, collision));
            var revived = BlockWorld.CellFromIndex(Key, BlockWorld.CellsPerChunk - 1);
            Assert.That(clone.Set(revived, BlockCell.Create(2)), Is.True);
            Assert.That(Compare(clone, Key, collision).Vertices.Length, Is.GreaterThan(0));
            sourceBefore.AssertUnchanged(source, "clone revive"); Compare(source, Key, collision);
        }
        [TestCase(false)] [TestCase(true)]
        public void ZeroCountWithOccupiedLastCellFallsBackToCompleteOracle(bool collision)
        {
            var world = World(); var last = BlockWorld.CellFromIndex(Key, BlockWorld.CellsPerChunk - 1);
            Assert.That(world.Set(last, BlockCell.Create(2)), Is.True); world.Chunks[Key].Count = 0;
            // Deliberate public metadata mismatch, not a valid network snapshot.
            // Encode bytes are compared without Decode; Cells/Count are checked
            // directly so an invalid declaration cannot hide geometry mutation.
            Assert.That(Compare(world, Key, collision).Vertices.Length, Is.GreaterThan(0));
            Assert.That(world.Chunks[Key].Count, Is.Zero); Assert.That(world.Chunks[Key].Cells.Last().Occupied, Is.True);
        }
        [TestCase(false)] [TestCase(true)]
        public void NonzeroCountWithNoOccupiedCellsStillMatchesEmptyOracle(bool collision)
        {
            var world = World(); var chunk = MakeUnsealedEmpty(world, Key); chunk.Count = 7;
            Empty(Compare(world, Key, collision)); Assert.That(chunk.Count, Is.EqualTo(7));
        }
        [TestCase(3, false)] [TestCase(3, true)] [TestCase(255, false)] [TestCase(255, true)]
        public void UnoccupiedResidualOwnerFlagsAndDurabilityNeverGenerateFaces(int owner, bool collision)
        {
            var world = World(); var chunk = MakeUnsealedEmpty(world, Key);
            chunk.Cells[0] = new BlockCell { Owner = (byte)owner, Flags = 37, Durability = 123 };
            Assert.That(chunk.Cells[0].Occupied, Is.False); Empty(Compare(world, Key, collision));
            Assert.That(chunk.Cells[0].Owner, Is.EqualTo((byte)owner)); Assert.That(chunk.Cells[0].Flags, Is.EqualTo(37));
            Assert.That(chunk.Cells[0].Durability, Is.EqualTo(123));
        }
        [TestCase(false)] [TestCase(true)]
        public void EmptyBuildThenUnsealedReviveAndRemoveNeverCachesAnEmptyResult(bool collision)
        {
            var world = World(); MakeUnsealedEmpty(world, Key);
            var firstEmpty = Compare(world, Key, collision); Empty(firstEmpty);
            var last = BlockWorld.CellFromIndex(Key, BlockWorld.CellsPerChunk - 1);
            Assert.That(world.Set(last, BlockCell.Create(1)), Is.True);
            Assert.That(Compare(world, Key, collision).Vertices.Length, Is.GreaterThan(0));
            Assert.That(world.Set(last, default), Is.True);
            var secondEmpty = Compare(world, Key, collision); Empty(secondEmpty);
            Assert.That(secondEmpty, Is.Not.SameAs(firstEmpty));
        }

        sealed class ChunkState
        {
            internal readonly BlockChunk Reference;
            internal readonly BlockCell[] ArrayReference, Values;
            internal readonly int Count;
            internal readonly uint Revision;
            internal ChunkState(BlockChunk chunk)
            { Reference = chunk; ArrayReference = chunk.Cells; Values = (BlockCell[])chunk.Cells.Clone(); Count = chunk.Count; Revision = chunk.Revision; }
        }
        sealed class WorldState
        {
            readonly byte[] snapshot;
            readonly Dictionary<Vector3Int, ChunkState> chunks;
            readonly uint revision, round;
            readonly ulong nextRun;
            readonly int pink, blue;
            readonly Vector2 dimensions;
            readonly float bottom;
            internal WorldState(BlockWorld world)
            {
                snapshot = world.EncodeSnapshot(); chunks = world.Chunks.ToDictionary(p => p.Key, p => new ChunkState(p.Value));
                revision = world.Revision; round = world.Round; nextRun = world.NextRunId; pink = world.PinkCount; blue = world.BlueCount;
                dimensions = world.Dimensions; bottom = world.Bottom;
            }
            internal void AssertUnchanged(BlockWorld world, string stage)
            {
                CollectionAssert.AreEqual(snapshot, world.EncodeSnapshot(), stage + " snapshot bytes");
                CollectionAssert.AreEquivalent(chunks.Keys, world.Chunks.Keys, stage + " chunk keys");
                Assert.That(world.Revision, Is.EqualTo(revision), stage); Assert.That(world.Round, Is.EqualTo(round), stage);
                Assert.That(world.NextRunId, Is.EqualTo(nextRun), stage); Assert.That(world.PinkCount, Is.EqualTo(pink), stage);
                Assert.That(world.BlueCount, Is.EqualTo(blue), stage); Assert.That(world.Dimensions, Is.EqualTo(dimensions), stage);
                Assert.That(world.Bottom, Is.EqualTo(bottom), stage);
                foreach (var pair in chunks)
                {
                    var current = world.Chunks[pair.Key]; var before = pair.Value;
                    Assert.That(current, Is.SameAs(before.Reference), stage + " chunk reference");
                    Assert.That(current.Cells, Is.SameAs(before.ArrayReference), stage + " cells reference");
                    Assert.That(current.Count, Is.EqualTo(before.Count), stage + " count");
                    Assert.That(current.Revision, Is.EqualTo(before.Revision), stage + " chunk revision");
                    CollectionAssert.AreEqual(before.Values, current.Cells, stage + " all cell fields");
                }
            }
        }
    }
}
#endif
