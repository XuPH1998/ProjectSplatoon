#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Pure managed geometry/ownership fixtures. No Upload, Editor scene,
    // Physics, performance probe, or production worker scheduler is exercised.
    // The Unity NUnit runner here has no NonParallelizable attribute;
    // run this fixture serially because it also inspects the static cache.
    public sealed class BlockMeshScratchReuseTests
    {
        static readonly Vector3Int Key = new(-1, -1, -1);
        static readonly Vector3Int[] Directions = { Vector3Int.left, Vector3Int.right,
            Vector3Int.down, Vector3Int.up, new(0, 0, -1), new(0, 0, 1) };
        static BlockWorld World(int seed)
        {
            var world = new BlockWorld(new Vector2(64, 64), (uint)seed);
            var random = new System.Random(seed);
            for (int i = 0; i < 96; i++)
                world.Set(BlockWorld.CellFromIndex(Key, random.Next(BlockWorld.CellsPerChunk)),
                    BlockCell.Create((byte)random.Next(1, 3), (i & 3) == 0));
            // Exercise all six neighbor lookups and negative-coordinate seams.
            for (int i = 0; i < Directions.Length; i++)
                world.Set(BlockWorld.CellFromIndex(Key + Directions[i], i * 257), BlockCell.Create((byte)(1 + i % 2)));
            world.Seal(1, world.Chunks.Keys.ToArray());
            return world;
        }
        static BlockMeshData Copy(BlockMeshData value) => new()
        {
            Vertices = (Vector3[])value.Vertices.Clone(), Normals = (Vector3[])value.Normals.Clone(),
            Colors = (Color32[])value.Colors.Clone(), Triangles = (int[])value.Triangles.Clone()
        };
        static void Equal(BlockMeshData expected, BlockMeshData actual, string stage)
        {
            CollectionAssert.AreEqual(expected.Vertices, actual.Vertices, stage + " every ordered vertex");
            CollectionAssert.AreEqual(expected.Normals, actual.Normals, stage + " every ordered normal");
            CollectionAssert.AreEqual(expected.Colors, actual.Colors, stage + " every owner/collision color");
            CollectionAssert.AreEqual(expected.Triangles, actual.Triangles, stage + " every ordered index/winding");
        }
        static void EqualOracle(BlockMeshData expected, BlockMeshData actual, string stage, bool collision)
        {
            CollectionAssert.AreEqual(expected.Vertices, actual.Vertices, stage + " every ordered vertex");
            CollectionAssert.AreEqual(expected.Triangles, actual.Triangles, stage + " every ordered index/winding");
            if (collision)
            { Assert.That(actual.Normals, Is.Empty); Assert.That(actual.Colors, Is.Empty); }
            else
            {
                CollectionAssert.AreEqual(expected.Normals, actual.Normals, stage + " every render normal");
                CollectionAssert.AreEqual(expected.Colors, actual.Colors, stage + " every render owner color");
            }
        }
        static void Independent(BlockMeshData first, BlockMeshData second)
        {
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.Vertices, Is.Not.SameAs(second.Vertices));
            if (first.Normals.Length != 0 && second.Normals.Length != 0) Assert.That(first.Normals, Is.Not.SameAs(second.Normals));
            if (first.Colors.Length != 0 && second.Colors.Length != 0) Assert.That(first.Colors, Is.Not.SameAs(second.Colors));
            Assert.That(first.Triangles, Is.Not.SameAs(second.Triangles));
            Assert.That(first.Vertices, Is.Not.SameAs(first.Normals));
        }
        static FieldInfo Field(string name) => typeof(BlockMeshBuilder).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        static int CachedCount() => (int)Field("_cachedScratch").GetValue(null);

        [TestCase(false)] [TestCase(true)]
        public void ReturnedArraysRemainOwnedAcrossSubsequentBuilds(bool collision)
        {
            var world = World(19); var before = world.EncodeSnapshot();
            var first = BlockMeshBuilder.Build(world, Key, collision);
            var saved = Copy(first); Assert.That(first.Vertices, Is.Not.Empty);
            var second = BlockMeshBuilder.Build(world, Key, collision);
            Independent(first, second);
            for (int i = 0; i < 8; i++)
            {
                var next = World(91 + i);
                EqualOracle(FrozenBlockMeshBuilder.Build(next, Key, false), BlockMeshBuilder.Build(next, Key, false), "later render", false);
                EqualOracle(FrozenBlockMeshBuilder.Build(next, Key, true), BlockMeshBuilder.Build(next, Key, true), "later collision", true);
                BlockMeshBuilder.Build(next, new Vector3Int(5, 5, 5), collision);
            }
            Equal(saved, first, "held result after scratch reuse");
            Equal(saved, second, "second held result after scratch reuse");
            first.Vertices[0] += Vector3.one;
            if (!collision) { first.Normals[0] = Vector3.zero; first.Colors[0] = new Color32(0, 0, 0, 0); }
            first.Triangles[0] = -1;
            Equal(saved, second, "caller mutation is isolated");
            EqualOracle(FrozenBlockMeshBuilder.Build(world, Key, collision), BlockMeshBuilder.Build(world, Key, collision), "fresh result after caller mutation", collision);
            CollectionAssert.AreEqual(before, world.EncodeSnapshot(), "all builds leave world unchanged");
        }

        [Test]
        public void RenderAndCollisionKeepDifferentOwnerMergeRules()
        {
            var world = new BlockWorld(new Vector2(64, 64));
            world.Set(Vector3Int.zero, BlockCell.Create(1)); world.Set(Vector3Int.right, BlockCell.Create(2));
            var render = BlockMeshBuilder.Build(world, Vector3Int.zero, false);
            var collision = BlockMeshBuilder.Build(world, Vector3Int.zero, true);
            var savedRender = Copy(render); var savedCollision = Copy(collision);
            Independent(render, collision);
            Assert.That(render.Triangles.Length, Is.GreaterThan(collision.Triangles.Length), "owner seam splits only render rectangles");
            EqualOracle(FrozenBlockMeshBuilder.Build(world, Vector3Int.zero, false), render, "render owner seam", false);
            EqualOracle(FrozenBlockMeshBuilder.Build(world, Vector3Int.zero, true), collision, "collision owner collapse", true);
            for (int i = 0; i < 4; i++)
            { BlockMeshBuilder.Build(world, Vector3Int.zero, true); BlockMeshBuilder.Build(world, Vector3Int.zero, false); }
            Equal(savedRender, render, "held render after collision/rebuilds");
            Equal(savedCollision, collision, "held collision after render/rebuilds");
        }

        [Test]
        public void ConcurrentBuildsKeepEveryOutputAndWorldIndependent()
        {
            const int workers = 8, rounds = 4;
            var worlds = Enumerable.Range(0, workers).Select(i => World(301 + i)).ToArray();
            var snapshots = worlds.Select(world => world.EncodeSnapshot()).ToArray();
            var expectedRender = worlds.Select(world => FrozenBlockMeshBuilder.Build(world, Key, false)).ToArray();
            var expectedCollision = worlds.Select(world => FrozenBlockMeshBuilder.Build(world, Key, true)).ToArray();
            using var barrier = new Barrier(workers);
            var tasks = Enumerable.Range(0, workers).Select(index => Task.Factory.StartNew(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture start barrier");
                var values = new BlockMeshData[rounds * 2];
                for (int round = 0; round < rounds; round++)
                {
                    values[round * 2] = BlockMeshBuilder.Build(worlds[index], Key, false);
                    values[round * 2 + 1] = BlockMeshBuilder.Build(worlds[index], Key, true);
                }
                return values;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), Is.True, "all managed geometry workers completed");
            var vertices = new HashSet<Vector3[]>(); var normals = new HashSet<Vector3[]>();
            var colors = new HashSet<Color32[]>(); var triangles = new HashSet<int[]>();
            for (int worker = 0; worker < workers; worker++)
            {
                for (int round = 0; round < rounds; round++) for (int kind = 0; kind < 2; kind++)
                {
                    var actual = tasks[worker].Result[round * 2 + kind];
                    EqualOracle(kind == 0 ? expectedRender[worker] : expectedCollision[worker], actual, "concurrent held output", kind == 1);
                    Assert.That(vertices.Add(actual.Vertices), Is.True, "unique nonempty vertex output");
                    if (kind == 0)
                    {
                        Assert.That(normals.Add(actual.Normals), Is.True, "unique nonempty render normal output");
                        Assert.That(colors.Add(actual.Colors), Is.True, "unique nonempty render color output");
                    }
                    else { Assert.That(actual.Normals, Is.Empty); Assert.That(actual.Colors, Is.Empty); }
                    Assert.That(triangles.Add(actual.Triangles), Is.True, "unique nonempty index output");
                }
                CollectionAssert.AreEqual(snapshots[worker], worlds[worker].EncodeSnapshot(), "concurrent readonly world");
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ExceptionAfterPartialEmissionReturnsCleanScratch(bool collision)
        {
            var world = new BlockWorld(new Vector2(64, 64));
            world.Set(Vector3Int.zero, BlockCell.Create(1)); world.Set(Vector3Int.right, BlockCell.Create(2));
            var first = BlockMeshBuilder.Build(world, Vector3Int.zero, collision); var saved = Copy(first);
            var chunk = world.Chunks[Vector3Int.zero]; var originalCells = chunk.Cells;
            var cellsField = typeof(BlockChunk).GetField(nameof(BlockChunk.Cells));
            var malformed = new BlockCell[originalCells.Length - 1]; Array.Copy(originalCells, malformed, malformed.Length);
            int cachedBefore = CachedCount(); Assert.That(cachedBefore, Is.GreaterThan(0));
            // Deliberately malformed fixture: the first direction emits geometry,
            // then the final cell access fails. No production hook is introduced.
            try
            {
                cellsField.SetValue(chunk, malformed);
                for (int i = 0; i < 4; i++)
                    Assert.Throws<IndexOutOfRangeException>(() => BlockMeshBuilder.Build(world, Vector3Int.zero, collision));
            }
            finally { cellsField.SetValue(chunk, originalCells); }
            Assert.That(CachedCount(), Is.EqualTo(cachedBefore), "throwing build returns its lease");
            var after = BlockMeshBuilder.Build(world, Vector3Int.zero, collision);
            EqualOracle(FrozenBlockMeshBuilder.Build(world, Vector3Int.zero, collision), after, "no dirty list/mask after exception", collision);
            Equal(saved, first, "previous caller arrays survive throwing and subsequent builds");
            Independent(first, after);
        }

        [Test]
        public void RetainedScratchCountAndCapacityAreBoundedAfterWorstCaseBuild()
        {
            var world = new BlockWorld(new Vector2(64, 64));
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                if (((x + y + z) & 1) == 0) world.Set(new Vector3Int(x, y, z), BlockCell.Create((byte)(1 + x % 2)));
            var value = BlockMeshBuilder.Build(world, Vector3Int.zero, false);
            EqualOracle(FrozenBlockMeshBuilder.Build(world, Vector3Int.zero, false), value, "worst-case isolated cells", false);
            long outputBytes = 12L * (value.Vertices.Length + (long)value.Normals.Length)
                + 4L * (value.Colors.Length + (long)value.Triangles.Length);
            long limit = (long)Field("MaxCachedScratchPayloadBytes").GetRawConstantValue();
            int countLimit = (int)Field("MaxCachedScratch").GetRawConstantValue();
            Assert.That(outputBytes, Is.GreaterThan(limit), "fixture really grows beyond retained capacity bound");
            var cache = (Array)Field("ScratchCache").GetValue(null); int cached = CachedCount();
            Assert.That(cache.Length, Is.EqualTo(countLimit)); Assert.That(cached, Is.InRange(0, countLimit));
            var unique = new HashSet<object>();
            for (int index = 0; index < cache.Length; index++)
            {
                var scratch = cache.GetValue(index);
                if (index >= cached) { Assert.That(scratch, Is.Null, "unused cache slot does not retain a lease"); continue; }
                Assert.That(scratch, Is.Not.Null); Assert.That(unique.Add(scratch), Is.True, "cached leases are distinct");
                long payload = (long)scratch.GetType().GetProperty("CapacityPayloadBytes").GetValue(scratch);
                Assert.That(payload, Is.LessThanOrEqualTo(limit), "oversized capacities never enter retained cache");
            }
        }
    }
}
#endif
