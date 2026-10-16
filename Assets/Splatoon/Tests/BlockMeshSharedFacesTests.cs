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
    // Pure geometry/ownership regression; no Physics, Upload or timing acceptance.
    // Run this fixture serially because its cache assertions inspect/reset static state.
    public sealed class BlockMeshSharedFacesTests
    {
        static readonly Vector3Int Key = new(-1, -1, -1);
        static readonly Vector3Int[] Directions = { Vector3Int.left, Vector3Int.right, Vector3Int.down,
            Vector3Int.up, new(0, 0, -1), new(0, 0, 1) };
        static BlockWorld RandomWorld(int seed)
        {
            var world = new BlockWorld(new Vector2(64, 64), (uint)seed);
            var random = new System.Random(seed);
            for (int i = 0; i < 200; i++)
                world.Set(BlockWorld.CellFromIndex(Key, random.Next(BlockWorld.CellsPerChunk)),
                    BlockCell.Create((byte)random.Next(1, 3), i % 5 == 0));
            foreach (var direction in Directions)
                for (int i = 0; i < 16; i++) world.Set(BlockWorld.CellFromIndex(Key + direction, i * 257), BlockCell.Create(2));
            return world;
        }
        static BlockMeshData Copy(BlockMeshData value) => new()
        {
            Vertices = (Vector3[])value.Vertices.Clone(), Normals = (Vector3[])value.Normals.Clone(),
            Colors = (Color32[])value.Colors.Clone(), Triangles = (int[])value.Triangles.Clone()
        };
        static void EqualData(BlockMeshData expected, BlockMeshData actual, bool collision)
        {
            CollectionAssert.AreEqual(expected.Vertices, actual.Vertices, "ordered vertices");
            CollectionAssert.AreEqual(expected.Triangles, actual.Triangles, "ordered winding/index stream");
            if (collision) { Assert.That(actual.Normals, Is.Empty); Assert.That(actual.Colors, Is.Empty); }
            else
            { CollectionAssert.AreEqual(expected.Normals, actual.Normals); CollectionAssert.AreEqual(expected.Colors, actual.Colors); }
        }
        static void Compare(BlockWorld world, Vector3Int key)
        {
            byte[] before = world.EncodeSnapshot();
            BlockMeshBuilder.BuildPair(world, key, out var render, out var collision, false, out long split);
            Assert.That(split, Is.Zero);
            EqualData(FrozenBlockMeshBuilder.Build(world, key, false), render, false);
            EqualData(FrozenBlockMeshBuilder.Build(world, key, true), collision, true);
            CollectionAssert.AreEqual(before, world.EncodeSnapshot());
        }
        static FieldInfo BuilderField(string name)
            => typeof(BlockMeshBuilder).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        static int CachedCount() => (int)BuilderField("_cachedScratch").GetValue(null);
        static Array Cache() => (Array)BuilderField("ScratchCache").GetValue(null);
        static object CacheGate() => BuilderField("ScratchGate").GetValue(null);
        static void ClearCache()
        {
            lock (CacheGate())
            {
                var cache = Cache(); Array.Clear(cache, 0, cache.Length);
                BuilderField("_cachedScratch").SetValue(null, 0);
            }
        }
        static object[] CacheSlots()
        {
            lock (CacheGate()) return Cache().Cast<object>().ToArray();
        }
        static void AssertReturnedLeases(int count, object[] slots)
        {
            lock (CacheGate())
            {
                Assert.That(CachedCount(), Is.EqualTo(count), "throwing scan returns the cached lease count");
                var cache = Cache(); Assert.That(cache.Length, Is.EqualTo(slots.Length));
                for (int i = 0; i < cache.Length; i++)
                {
                    if (slots[i] == null) Assert.That(cache.GetValue(i), Is.Null, "unused slot stays empty");
                    else Assert.That(cache.GetValue(i), Is.SameAs(slots[i]), "the same scratch returns to its slot");
                }
            }
        }
        static T ScratchField<T>(object scratch, string name)
            => (T)scratch.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(scratch);
        static long ListCapacityBytes(object scratch)
        {
            long vertices = (long)ScratchField<List<Vector3>>(scratch, "Vertices").Capacity * 3 * sizeof(float);
            long normals = (long)ScratchField<List<Vector3>>(scratch, "Normals").Capacity * 3 * sizeof(float);
            long colors = (long)ScratchField<List<Color32>>(scratch, "Colors").Capacity * 4 * sizeof(byte);
            long indices = (long)ScratchField<List<int>>(scratch, "Triangles").Capacity * sizeof(int);
            return vertices + normals + colors + indices;
        }
        static long IndependentCapacityBytes(object scratch)
            => ListCapacityBytes(scratch) + (ScratchField<byte[]>(scratch, "_mask")?.Length ?? 0)
                + (ScratchField<byte[]>(scratch, "_exposure")?.Length ?? 0);
        static long ReportedCapacityBytes(object scratch)
            => (long)scratch.GetType().GetProperty("CapacityPayloadBytes").GetValue(scratch);
        static void AssertEmptyScratchLists(object scratch)
        {
            Assert.That(ScratchField<List<Vector3>>(scratch, "Vertices").Count, Is.Zero);
            Assert.That(ScratchField<List<Vector3>>(scratch, "Normals").Count, Is.Zero);
            Assert.That(ScratchField<List<Color32>>(scratch, "Colors").Count, Is.Zero);
            Assert.That(ScratchField<List<int>>(scratch, "Triangles").Count, Is.Zero);
        }

        [Test]
        public void SharedScanMatchesFrozenGeometryAcrossAllSeamsOwnersHolesAndEmptyChunks()
        {
            Compare(new BlockWorld(new Vector2(64, 64)), Key);
            var empty = new BlockWorld(new Vector2(64, 64));
            empty.Set(BlockWorld.ChunkOrigin(Key), BlockCell.Create(1)); empty.Set(BlockWorld.ChunkOrigin(Key), default);
            Compare(empty, Key);
            for (int shape = 0; shape < 3; shape++)
            {
                var world = new BlockWorld(new Vector2(64, 64));
                for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
                {
                    var cell = BlockWorld.CellFromIndex(Key, i);
                    if (shape == 2 && ((cell.x + cell.y + cell.z) & 1) != 0) continue;
                    world.Set(cell, BlockCell.Create((byte)(shape == 0 || (cell.x & 1) == 0 ? 1 : 2)));
                }
                Compare(world, Key);
                foreach (var direction in Directions)
                {
                    var neighbor = world.Clone();
                    for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
                        neighbor.Set(BlockWorld.CellFromIndex(Key + direction, i), BlockCell.Create(2));
                    Compare(neighbor, Key);
                }
            }
            foreach (int seed in new[] { 41, 129, 1739 }) Compare(RandomWorld(seed), Key);
            var staleCount = RandomWorld(422);
            staleCount.Chunks[Key].Count = 0; Compare(staleCount, Key);
        }

        [Test]
        public void PairOutputsRemainIndependentOfEachOtherLaterBuildsAndCallerWrites()
        {
            var world = RandomWorld(523);
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            BlockMeshBuilder.BuildPair(world, Key, out var render, out var collision, true, out long split);
            long after = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.That(split, Is.InRange(before, after));
            Assert.That(render.Vertices, Is.Not.SameAs(collision.Vertices));
            Assert.That(render.Triangles, Is.Not.SameAs(collision.Triangles));
            var savedRender = Copy(render); var savedCollision = Copy(collision);
            for (int seed = 600; seed < 604; seed++)
                BlockMeshBuilder.BuildPair(RandomWorld(seed), Key, out _, out _, false, out _);
            EqualData(savedRender, render, false); EqualData(savedCollision, collision, true);
            render.Vertices[0] = new Vector3(99, 98, 97); render.Colors[0] = new Color32(1, 2, 3, 4);
            collision.Vertices[0] = new Vector3(-99, -98, -97); collision.Triangles[0] = 100;
            BlockMeshBuilder.BuildPair(world, Key, out var freshRender, out var freshCollision, false, out _);
            EqualData(savedRender, freshRender, false); EqualData(savedCollision, freshCollision, true);
        }

        [Test]
        public void EightConcurrentPairsRetainDistinctArraysAndReadOnlyWorlds()
        {
            const int workers = 8;
            var worlds = Enumerable.Range(0, workers).Select(i => RandomWorld(700 + i)).ToArray();
            var before = worlds.Select(world => world.EncodeSnapshot()).ToArray();
            using var cancellation = new CancellationTokenSource();
            using var barrier = new Barrier(workers + 1);
            var tasks = new Task<BlockMeshData[]>[workers];
            int arrived = 0;
            try
            {
                for (int i = 0; i < workers; i++)
                {
                    int index = i;
                    tasks[i] = Task.Factory.StartNew(() =>
                    {
                        Interlocked.Increment(ref arrived);
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token))
                            throw new TimeoutException("all eight pair workers must reach the start barrier");
                        var output = new BlockMeshData[6];
                        for (int round = 0; round < 3; round++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            BlockMeshBuilder.BuildPair(worlds[index], Key, out output[round * 2], out output[round * 2 + 1], false, out _);
                        }
                        return output;
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token), Is.True,
                    "release phase zero only when all eight dedicated workers arrive");
                Assert.That(Volatile.Read(ref arrived), Is.EqualTo(workers));
                Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), Is.True, "all pair workers complete");
                var vertices = new HashSet<Vector3[]>(); var indices = new HashSet<int[]>();
                var normals = new HashSet<Vector3[]>(); var colors = new HashSet<Color32[]>();
                for (int i = 0; i < tasks.Length; i++)
                {
                    for (int n = 0; n < tasks[i].Result.Length; n++)
                    {
                        var data = tasks[i].Result[n]; bool collision = (n & 1) != 0;
                        EqualData(FrozenBlockMeshBuilder.Build(worlds[i], Key, collision), data, collision);
                        Assert.That(vertices.Add(data.Vertices), Is.True); Assert.That(indices.Add(data.Triangles), Is.True);
                        if (!collision)
                        { Assert.That(normals.Add(data.Normals), Is.True); Assert.That(colors.Add(data.Colors), Is.True); }
                    }
                    CollectionAssert.AreEqual(before[i], worlds[i].EncodeSnapshot());
                }
            }
            finally
            {
                // Cancellation releases a failed/incomplete barrier before disposing it.
                cancellation.Cancel();
                var started = tasks.Where(task => task != null).Cast<Task>().ToArray();
                bool completed;
                try { completed = Task.WaitAll(started, TimeSpan.FromSeconds(30)); }
                catch (AggregateException) { completed = started.All(task => task.IsCompleted); }
                Assert.That(completed, Is.True, "every started worker is joined during fixture cleanup");
            }
        }

        [Test]
        public void FailedSharedScanReturnsItsLeaseAndCannotLeakPartialExposure()
        {
            ClearCache();
            var world = RandomWorld(843); var chunk = world.Chunks[Key]; var original = chunk.Cells;
            var field = typeof(BlockChunk).GetField(nameof(BlockChunk.Cells));
            var shortCells = new BlockCell[original.Length - 1]; Array.Copy(original, shortCells, shortCells.Length);
            BlockMeshBuilder.BuildPair(world, Key, out var render, out var collision, false, out _);
            var savedRender = Copy(render); var savedCollision = Copy(collision);
            int cachedBefore = CachedCount(); Assert.That(cachedBefore, Is.EqualTo(1), "a clean-cache small pair retains one lease");
            var slotsBefore = CacheSlots();
            Assert.That(slotsBefore.Length, Is.EqualTo(2)); Assert.That(slotsBefore[0], Is.Not.Null);
            Assert.That(slotsBefore[1], Is.Null);
            Assert.That(ReportedCapacityBytes(slotsBefore[0]), Is.LessThanOrEqualTo(1048576));
            try
            {
                field.SetValue(chunk, shortCells);
                for (int i = 0; i < 4; i++)
                {
                    Assert.Throws<IndexOutOfRangeException>(() =>
                        BlockMeshBuilder.BuildPair(world, Key, out _, out _, false, out _));
                    AssertReturnedLeases(cachedBefore, slotsBefore);
                    AssertEmptyScratchLists(slotsBefore[0]);
                }
            }
            finally { field.SetValue(chunk, original); }
            AssertReturnedLeases(cachedBefore, slotsBefore);
            Compare(world, Key); EqualData(savedRender, render, false); EqualData(savedCollision, collision, true);
        }

        [Test]
        public void SharedExposureBytesAreIncludedInTheExistingBoundedScratchCache()
        {
            ClearCache();
            var small = new BlockWorld(new Vector2(64, 64));
            var origin = BlockWorld.ChunkOrigin(Key);
            small.Set(origin, BlockCell.Create(1)); small.Set(origin + Vector3Int.right, BlockCell.Create(2));
            var smallBefore = small.EncodeSnapshot();
            EqualData(FrozenBlockMeshBuilder.Build(small, Key, false), BlockMeshBuilder.Build(small, Key, false), false);
            Assert.That(CachedCount(), Is.EqualTo(1));
            var initialSlots = CacheSlots(); var smallScratch = initialSlots[0];
            Assert.That(initialSlots.Length, Is.EqualTo(2)); Assert.That(initialSlots[1], Is.Null);
            Assert.That(smallScratch, Is.Not.Null);
            Assert.That(ScratchField<byte[]>(smallScratch, "_exposure"), Is.Null, "legacy build does not allocate the new bank");
            Assert.That(ScratchField<byte[]>(smallScratch, "_mask").Length, Is.EqualTo(256));
            long listBytesBefore = ListCapacityBytes(smallScratch);
            long independentBefore = IndependentCapacityBytes(smallScratch);
            long reportedBefore = ReportedCapacityBytes(smallScratch);
            Assert.That(reportedBefore, Is.EqualTo(independentBefore), "legacy actual list and mask capacities");
            BlockMeshBuilder.BuildPair(small, Key, out var smallRender, out var smallCollision, false, out _);
            EqualData(FrozenBlockMeshBuilder.Build(small, Key, false), smallRender, false);
            EqualData(FrozenBlockMeshBuilder.Build(small, Key, true), smallCollision, true);
            CollectionAssert.AreEqual(smallBefore, small.EncodeSnapshot());
            AssertReturnedLeases(1, initialSlots);
            var exposure = ScratchField<byte[]>(smallScratch, "_exposure");
            Assert.That(exposure, Is.Not.Null); Assert.That(exposure.Length, Is.EqualTo(24576));
            Assert.That(ListCapacityBytes(smallScratch), Is.EqualTo(listBytesBefore), "same small geometry does not grow its lists");
            Assert.That(ScratchField<byte[]>(smallScratch, "_mask").Length, Is.EqualTo(256));
            long independentAfter = IndependentCapacityBytes(smallScratch);
            long reportedAfter = ReportedCapacityBytes(smallScratch);
            Assert.That(independentAfter - independentBefore, Is.EqualTo(24576), "actual capacity increase is exactly the exposure array");
            Assert.That(reportedAfter, Is.EqualTo(independentAfter), "all four actual list capacities, mask and exposure are counted");
            Assert.That(reportedAfter - reportedBefore, Is.EqualTo(24576), "reported payload includes the exact new bank");
            Assert.That(reportedAfter, Is.LessThanOrEqualTo(1048576), "this inspected shared-pair lease is really retained");
            var world = new BlockWorld(new Vector2(64, 64));
            for (int i = 0; i < BlockWorld.CellsPerChunk; i++)
            {
                var cell = BlockWorld.CellFromIndex(Key, i);
                if (((cell.x + cell.y + cell.z) & 1) == 0) world.Set(cell, BlockCell.Create((byte)(1 + (cell.x & 1))));
            }
            Compare(world, Key);
            Assert.That(IndependentCapacityBytes(smallScratch), Is.GreaterThan(1048576), "checkerboard really grows the previously cached lease beyond the bound");
            Assert.That(ReportedCapacityBytes(smallScratch), Is.EqualTo(IndependentCapacityBytes(smallScratch)));
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var cache = (Array)typeof(BlockMeshBuilder).GetField("ScratchCache", flags).GetValue(null);
            int cached = (int)typeof(BlockMeshBuilder).GetField("_cachedScratch", flags).GetValue(null);
            Assert.That(cache.Length, Is.EqualTo(2)); Assert.That(cached, Is.InRange(0, 2));
            Assert.That(cached, Is.Zero, "oversized shared-pair lease is discarded instead of retained");
            for (int i = 0; i < cache.Length; i++)
            {
                var scratch = cache.GetValue(i);
                if (i >= cached) { Assert.That(scratch, Is.Null); continue; }
                long bytes = (long)scratch.GetType().GetProperty("CapacityPayloadBytes").GetValue(scratch);
                Assert.That(bytes, Is.LessThanOrEqualTo(1048576));
            }
        }
    }
}
#endif
