using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Splatoon.Blocks;

namespace Splatoon.Tests
{
    // Pure world contracts: no Play Mode, physics or performance substitutions.
    public sealed class BlockBulkDetachContractTests
    {
        static BlockWorld Seed(Vector3Int bottom, int count)
        {
            var world = new BlockWorld(new Vector2(16, 16), 39);
            for (int i = 0; i < count; i++)
                Assert.That(world.Set(bottom + Vector3Int.up * i,
                    new BlockCell { Owner = (byte)(1 + i % 2), Durability = (ushort)(1 + i * 113) }), Is.True);
            foreach (var cell in new[] { bottom + Vector3Int.down, bottom + Vector3Int.up * count,
                bottom + Vector3Int.left, bottom + Vector3Int.right, bottom + new Vector3Int(0, 0, -1),
                bottom + new Vector3Int(0, 0, 1) })
                if (world.InBounds(cell)) world.Set(cell, BlockCell.Create(2, true));
            return world;
        }

        static Dictionary<Vector3Int, BlockCell> StableCells(BlockWorld world)
        {
            var cells = new Dictionary<Vector3Int, BlockCell>();
            foreach (var chunk in world.Chunks)
                for (int i = 0; i < chunk.Value.Cells.Length; i++)
                    if (chunk.Value.Cells[i].Occupied)
                        cells.Add(BlockWorld.CellFromIndex(chunk.Key, i), chunk.Value.Cells[i]);
            return cells;
        }

        static void EqualCell(BlockCell actual, BlockCell expected)
        {
            Assert.That(actual.Owner, Is.EqualTo(expected.Owner));
            Assert.That(actual.Flags, Is.EqualTo(expected.Flags));
            Assert.That(actual.Durability, Is.EqualTo(expected.Durability));
        }

        // Expected masks come from cell positions, independent of span masks and indexing.
        static void EqualStable(BlockWorld world, Dictionary<Vector3Int, BlockCell> expected)
        {
            var actual = StableCells(world);
            CollectionAssert.AreEquivalent(expected.Keys, actual.Keys);
            foreach (var cell in expected) EqualCell(actual[cell.Key], cell.Value);
            foreach (var chunk in world.Chunks)
            {
                Assert.That(chunk.Value.Count, Is.EqualTo(expected.Keys.Count(c => BlockWorld.ChunkAt(c) == chunk.Key)));
                var origin = BlockWorld.ChunkOrigin(chunk.Key);
                for (int axis = 0; axis < 3; axis++)
                    for (int a = 0; a < 16; a++) for (int b = 0; b < 16; b++)
                    {
                        ushort bits = 0;
                        var first = axis == 0 ? new Vector3Int(0, a, b)
                            : axis == 1 ? new Vector3Int(a, 0, b) : new Vector3Int(a, b, 0);
                        var step = axis == 0 ? Vector3Int.right : axis == 1 ? Vector3Int.up : new Vector3Int(0, 0, 1);
                        for (int bit = 0; bit < 16; bit++)
                            if (expected.ContainsKey(origin + first + step * bit)) bits |= (ushort)(1 << bit);
                        Assert.That(world.OccupiedRow(origin + first, axis), Is.EqualTo(bits));
                    }
            }
        }

        [Test]
        public void EveryAlignmentAndNegativeColumnKeepsCellsRowsCountsAndPublishedSource()
        {
            foreach (var column in new[] { (X: -17, Z: 15), (X: -32, Z: 19), (X: -25, Z: 31) })
                foreach (int baseY in new[] { -32, 16 }) for (int localY = 0; localY < 16; localY++)
                foreach (int count in new[] { 1, 2, 15, 16 })
                {
                    var bottom = new Vector3Int(column.X, baseY + localY, column.Z);
                    var source = Seed(bottom, count); var sourceExpected = StableCells(source);
                    var expected = new Dictionary<Vector3Int, BlockCell>(sourceExpected);
                    var sourceBytes = source.EncodeSnapshot(); var copy = source.Clone();
                    int pink = copy.PinkCount, blue = copy.BlueCount;
                    ulong generation = copy.MutationGeneration, nextRun = copy.NextRunId;
                    var expectedRun = Enumerable.Range(0, count).Select(i => expected[bottom + Vector3Int.up * i]).ToArray();
                    foreach (int i in Enumerable.Range(0, count)) expected.Remove(bottom + Vector3Int.up * i);
                    var run = copy.Detach(bottom, count, 123.45);
                    Assert.That(run.Id, Is.EqualTo(nextRun)); Assert.That(copy.NextRunId, Is.EqualTo(nextRun + 1));
                    Assert.That(run.Bottom, Is.EqualTo(bottom)); Assert.That(run.StartedAt, Is.EqualTo(123.45));
                    Assert.That(run.Cohort, Is.Null); Assert.That(run.Cells.Length, Is.EqualTo(count));
                    for (int i = 0; i < count; i++) EqualCell(run.Cells[i], expectedRun[i]);
                    Assert.That(copy.PinkCount, Is.EqualTo(pink)); Assert.That(copy.BlueCount, Is.EqualTo(blue));
                    Assert.That(copy.MutationGeneration, Is.EqualTo(generation + (ulong)count));
                    EqualStable(copy, expected); EqualStable(source, sourceExpected);
                    CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
                    var detachedBytes = copy.EncodeSnapshot();
                    CollectionAssert.AreEqual(detachedBytes, BlockWorld.DecodeSnapshot(detachedBytes).EncodeSnapshot());
                    source.Set(bottom, BlockCell.Create(2, true));
                    sourceExpected[bottom] = BlockCell.Create(2, true);
                    CollectionAssert.AreEqual(detachedBytes, copy.EncodeSnapshot());
                    EqualStable(source, sourceExpected); EqualStable(copy, expected);
                    var changedSource = source.EncodeSnapshot(); copy.Set(bottom + Vector3Int.right, default);
                    expected.Remove(bottom + Vector3Int.right);
                    CollectionAssert.AreEqual(changedSource, source.EncodeSnapshot());
                    EqualStable(source, sourceExpected); EqualStable(copy, expected);
                }
        }

        [Test]
        public void MissingOrAnchoredCellsInEitherChunkRejectBeforeIdOrCopyOnWriteMutation()
        {
            var bottom = new Vector3Int(-17, 15, -17);
            foreach (int failureIndex in new[] { 0, 1, 8, 15 }) foreach (bool anchored in new[] { false, true })
            {
                var source = Seed(bottom, 16);
                source.Set(bottom + Vector3Int.up * failureIndex, anchored ? BlockCell.Create(1, true) : default);
                var copy = source.Clone(); var bytes = copy.EncodeSnapshot(); var sourceBytes = source.EncodeSnapshot();
                var references = copy.Chunks.ToDictionary(p => p.Key, p => p.Value);
                ulong nextRun = copy.NextRunId, generation = copy.MutationGeneration;
                Assert.Throws<InvalidOperationException>(() => copy.Detach(bottom, 16, 8));
                Assert.That(copy.NextRunId, Is.EqualTo(nextRun)); Assert.That(copy.MutationGeneration, Is.EqualTo(generation));
                foreach (var chunk in references) Assert.That(copy.Chunks[chunk.Key], Is.SameAs(chunk.Value));
                CollectionAssert.AreEqual(bytes, copy.EncodeSnapshot()); CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
                EqualStable(copy, StableCells(source));
            }
        }

        [Test]
        public void EmptyAndAbsentSourceChunksRejectWithoutAllocatingOrChangingOtherRows()
        {
            var bottom = new Vector3Int(-25, 15, 19);
            foreach (bool upper in new[] { false, true }) foreach (bool sealEmpty in new[] { false, true })
            {
                var source = Seed(bottom, 16);
                var key = BlockWorld.ChunkAt(bottom) + (upper ? Vector3Int.up : Vector3Int.zero);
                foreach (var cell in StableCells(source).Keys.Where(c => BlockWorld.ChunkAt(c) == key).ToArray())
                    source.Set(cell, default);
                if (sealEmpty) source.Seal(1, source.Chunks.Keys.ToArray());
                Assert.That(source.Chunks.ContainsKey(key), Is.EqualTo(!sealEmpty));
                var expected = StableCells(source); var copy = source.Clone();
                var bytes = copy.EncodeSnapshot(); var sourceBytes = source.EncodeSnapshot();
                var references = copy.Chunks.ToDictionary(p => p.Key, p => p.Value);
                ulong nextRun = copy.NextRunId, generation = copy.MutationGeneration;
                Assert.Throws<InvalidOperationException>(() => copy.Detach(bottom, 16, 8));
                Assert.That(copy.NextRunId, Is.EqualTo(nextRun)); Assert.That(copy.MutationGeneration, Is.EqualTo(generation));
                foreach (var chunk in references) Assert.That(copy.Chunks[chunk.Key], Is.SameAs(chunk.Value));
                CollectionAssert.AreEqual(bytes, copy.EncodeSnapshot()); CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
                EqualStable(copy, expected); EqualStable(source, expected);
            }
        }

        [Test]
        public void ShortLegacyRunRetainsPublicationEpochAndFallingCellCopyOnWrite()
        {
            var bottom = new Vector3Int(-25, 15, 19); var source = Seed(bottom, 16);
            var run = source.Detach(bottom, 16, 5);
            source.PrepareLegacyRunEpochsForPublication(out _); source.ActivateRebasePendingCohorts(9);
            Assert.That(source.Falling[run.Id].StartedAt, Is.EqualTo(9));
            var stable = StableCells(source); byte[] sourceBytes = source.EncodeSnapshot(); var copy = source.Clone();
            var expected = run.Cells[1]; int pink = copy.PinkCount, blue = copy.BlueCount;
            Assert.That(copy.RemoveFallingCell(run.Id, 1, out var removed), Is.True); EqualCell(removed, expected);
            Assert.That(source.Falling[run.Id].Cells[1].Occupied, Is.True);
            Assert.That(copy.PinkCount, Is.EqualTo(pink - (expected.Owner == 1 ? 1 : 0)));
            Assert.That(copy.BlueCount, Is.EqualTo(blue - (expected.Owner == 2 ? 1 : 0)));
            CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            byte[] copyBytes = copy.EncodeSnapshot(); source.EditFalling(run.Id).InitialSpeed = 1.25f;
            CollectionAssert.AreEqual(copyBytes, copy.EncodeSnapshot());
            EqualStable(source, stable); EqualStable(copy, stable);
        }

        [Test]
        public void TopCellAndLegacyLongRunsRetainTheirOriginalDataAndBounds()
        {
            foreach (var span in new[] { (Bottom: new Vector3Int(-17, 319, 15), Count: 1),
                (Bottom: new Vector3Int(-17, 15, 15), Count: 17),
                (Bottom: new Vector3Int(-17, -80, 15), Count: 400) })
            {
                var world = Seed(span.Bottom, span.Count); var expected = StableCells(world);
                int pink = world.PinkCount, blue = world.BlueCount;
                var cells = Enumerable.Range(0, span.Count).Select(i => expected[span.Bottom + Vector3Int.up * i]).ToArray();
                var run = world.Detach(span.Bottom, span.Count, 20);
                for (int i = 0; i < span.Count; i++)
                { EqualCell(run.Cells[i], cells[i]); expected.Remove(span.Bottom + Vector3Int.up * i); }
                Assert.That(world.PinkCount, Is.EqualTo(pink)); Assert.That(world.BlueCount, Is.EqualTo(blue));
                EqualStable(world, expected);
                byte[] bytes = world.EncodeSnapshot(); CollectionAssert.AreEqual(bytes, BlockWorld.DecodeSnapshot(bytes).EncodeSnapshot());
            }
        }
    }
}
