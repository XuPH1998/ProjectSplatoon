#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    [Category("BlockSimulationPrototype")]
    public sealed class BlockBroadphaseIndexTests
    {
        static readonly MethodInfo BuildPairs = typeof(BlockSimulation).GetMethod("BuildPairs", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo PairStorage = typeof(BlockSimulation).GetField("_pairs", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo HandleStorage = typeof(BlockSimulation).GetField("_handles", BindingFlags.NonPublic | BindingFlags.Instance);

        static BlockSimState State(ulong id, float3 position, BlockSimMode mode = BlockSimMode.Frozen, float3 velocity = default, uint generation = 1) =>
            new BlockSimState { handle = new BlockSimHandle(1, 1, id, generation), owner = 1, position = position, velocity = velocity, mode = mode };

        static ulong StablePair(ulong a, ulong b) => (math.min((uint)a, (uint)b) * (1UL << 32)) | math.max((uint)a, (uint)b);

        [TestCase(0f)]
        [TestCase(.01f)]
        [TestCase(.05f)]
        public void ActiveSweepsAndPassiveCentersMatchAnIndependentCompleteEnvelopeOracle(float delta)
        {
            using var world = new BlockSimulation(1);
            var states = new Dictionary<ulong, BlockSimState>();
            var random = new System.Random(571802);
            const int count = 96;
            for (int i = count - 1; i >= 0; i--)
            {
                float3 position = new((float)random.NextDouble() * 6 - 3, (float)random.NextDouble() * 3 + .2f,
                    (float)random.NextDouble() * 6 - 3);
                float3 velocity = new((float)random.NextDouble() * 120 - 60, (float)random.NextDouble() * 20 - 10,
                    (float)random.NextDouble() * 120 - 60);
                BlockSimMode mode = i % 3 == 0 ? BlockSimMode.Frozen : i % 3 == 1 ? BlockSimMode.Resting : BlockSimMode.Active;
                if (i == 0) { position = new float3(-2, 2, 0); velocity = new float3(240, 0, 0); mode = BlockSimMode.Active; }
                if (i == 1) { position = new float3(2, 2, 0); velocity = new float3(-240, 0, 0); mode = BlockSimMode.Active; }
                var state = State((ulong)i + 1, position, mode, velocity);
                states.Add(state.handle.id, state); Assert.That(world.Add(state), Is.True);
            }
            BuildPairs.Invoke(world, new object[] { delta });
            var pairs = (NativeList<ulong>)PairStorage.GetValue(world);
            var handles = (NativeList<BlockSimHandle>)HandleStorage.GetValue(world);
            var actual = new HashSet<ulong>(); ulong previous = 0;
            for (int p = 0; p < pairs.Length; p++)
            {
                ulong key = StablePair(handles[(int)(pairs[p] >> 32)].id, handles[(int)(uint)pairs[p]].id);
                Assert.That(actual.Add(key), Is.True, "Multiple overlapping hash cells must have exactly one pair owner.");
                if (p > 0) Assert.That(key, Is.GreaterThan(previous), "Pair order must follow stable handles despite reverse insertion order.");
                previous = key;
            }
            var expected = new HashSet<ulong>();
            for (ulong a = 1; a <= count; a++) for (ulong b = a + 1; b <= count; b++)
            {
                BlockSimState sa = states[a], sb = states[b];
                // Newly added resting bodies request support validation. Frozen/frozen pairs are irrelevant.
                if (sa.mode == BlockSimMode.Frozen && sb.mode == BlockSimMode.Frozen) continue;
                float3 va = sa.mode == BlockSimMode.Active ? sa.velocity : float3.zero;
                float3 vb = sb.mode == BlockSimMode.Active ? sb.velocity : float3.zero;
                float3 extent = new float3(BlockSimulation.Edge * .5f + BlockSimulation.ContactSkin +
                    BlockSimulation.MaxPositionCorrection * BlockSimulation.PositionIterations);
                float3 aMin = math.min(sa.position, sa.position + va * delta) - extent;
                float3 aMax = math.max(sa.position, sa.position + va * delta) + extent;
                float3 bMin = math.min(sb.position, sb.position + vb * delta) - extent;
                float3 bMax = math.max(sb.position, sb.position + vb * delta) + extent;
                if (math.all(aMin <= bMax) && math.all(aMax >= bMin)) expected.Add(StablePair(a, b));
            }
            CollectionAssert.AreEquivalent(expected, actual, "Every complete moving envelope must be represented, including a moving target's distant start.");
            Assert.That(world.Diagnostics.CandidateRequired, Is.EqualTo(actual.Count));
            Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(actual.Count));
            if (delta >= .01f) Assert.That(actual.Contains(StablePair(1, 2)), Is.True, "Both bodies move; indexing only their initial centers misses this crossing.");
        }

        [Test]
        public void PersistentCentersSurviveCrossCellQueriesTeleportsSwapBackAndGenerationReuse()
        {
            using var world = new BlockSimulation(1);
            var states = new Dictionary<BlockSimHandle, BlockSimState>();
            var results = new List<BlockSimHandle>();
            for (ulong id = 1; id <= 24; id++)
            {
                var state = State(id, new float3((float)(id % 6) * 2 + .59f, 1, (float)(id / 6) * -2 - .59f));
                Assert.That(world.Add(state), Is.True); states.Add(state.handle, state);
            }
            // This tiny query is across the .6m hash boundary from the body's center, but inside its face.
            var first = states[new BlockSimHandle(1, 1, 1)];
            var face = new BlockAabb(first.position + new float3(.149f, 0, 0), new float3(.0001f));
            world.Query(face, results); CollectionAssert.AreEqual(new[] { first.handle }, results);
            var last = states[new BlockSimHandle(1, 1, 24)];
            last.position = new float3(-8.61f, 2.39f, -7.19f);
            Assert.That(world.SetState(last), Is.True); states[last.handle] = last;
            var removed = new BlockSimHandle(1, 1, 3);
            Assert.That(world.Remove(removed), Is.True); states.Remove(removed);
            var replacement = State(3, new float3(8.59f, 2.39f, 7.19f), generation: 2);
            Assert.That(world.Add(replacement), Is.True); states.Add(replacement.handle, replacement);
            Assert.That(world.TryGet(removed, out _), Is.False);
            foreach (var state in states.Values)
            {
                var query = new BlockAabb(state.position + new float3(-.149f, .149f, -.149f), new float3(.0002f));
                world.Query(query, results);
                var expected = new HashSet<BlockSimHandle>();
                foreach (var candidate in states.Values)
                    if (query.Overlaps(new BlockAabb(candidate.position, new float3(BlockSimulation.Edge * .5f)))) expected.Add(candidate.handle);
                CollectionAssert.AreEquivalent(expected, results, "Center index missed a corner after state or dense-slot mutation.");
                Assert.That(new HashSet<BlockSimHandle>(results).Count, Is.EqualTo(results.Count));
            }
            world.Clear();
            var nextRound = new BlockSimState { handle = new BlockSimHandle(2, 2, 1), owner = 2, mode = BlockSimMode.Frozen,
                position = new float3(-.61f, 1, -.61f) };
            Assert.That(world.Add(nextRound), Is.True);
            world.Query(new BlockAabb(nextRound.position, new float3(.001f)), results);
            CollectionAssert.AreEqual(new[] { nextRound.handle }, results);
        }
    }
}
#endif
