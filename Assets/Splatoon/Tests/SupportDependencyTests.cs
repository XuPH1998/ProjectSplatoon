#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    /// <summary>
    /// Support invalidation is observed before another physical tick can legitimately wake neighbours.
    /// All fixtures use the agreed 0.30 m translation-only cube geometry and existing public APIs.
    /// </summary>
    [Category("BlockSimulationPrototype")]
    public sealed class SupportDependencyTests
    {
        const float Dt = 1f / 60f, Edge = .30f, Half = .15f;
        static readonly float3[] FarObservers = { new(100, 0, 100) };

        static BlockSimulation World(int capacity = 1)
        {
            var world = new BlockSimulation(capacity);
            world.SetObservers(FarObservers);
            return world;
        }

        static BlockSimHandle Add(BlockSimulation world, ulong id, float3 position,
            BlockSimMode mode = BlockSimMode.Resting, uint generation = 1, uint epoch = 1, uint round = 1,
            float3 velocity = default)
        {
            var handle = new BlockSimHandle(epoch, round, id, generation);
            Assert.That(world.Add(new BlockSimState
            {
                handle = handle, owner = 1, position = position, velocity = velocity, mode = mode
            }), Is.True, "Fixture identity must be live exactly once: " + handle);
            return handle;
        }

        static BlockSimHandle[] Column(BlockSimulation world, ulong firstId, int height, float x = 0,
            uint generation = 1, uint epoch = 1, uint round = 1)
        {
            var handles = new BlockSimHandle[height];
            for (int y = 0; y < height; y++)
                handles[y] = Add(world, firstId + (ulong)y, new float3(x, Half + y * Edge, 0),
                    generation: generation, epoch: epoch, round: round);
            return handles;
        }

        static BlockMapCollisionBuilder Floor()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0, -.1f, 0), new float3(20, .1f, 20), 1);
            return builder;
        }

        static BlockMapCollisionBuilder Pads(bool left)
        {
            var builder = new BlockMapCollisionBuilder();
            // A lower catch floor keeps the test finite without supporting either initial column.
            builder.AddBox(new float3(0, -1.1f, 0), new float3(20, .1f, 20), 1);
            if (left) builder.AddBox(new float3(0, -.1f, 0), new float3(.6f, .1f, .6f), 2);
            builder.AddBox(new float3(3, -.1f, 0), new float3(.6f, .1f, .6f), 3);
            return builder;
        }

        static BlockSimState Read(BlockSimulation world, BlockSimHandle handle)
        {
            Assert.That(world.TryGet(handle, out var state), Is.True, "Missing live identity " + handle);
            Assert.That(math.all(math.isfinite(state.position)) && math.all(math.isfinite(state.velocity)),
                Is.True, "Non-finite state for " + handle);
            return state;
        }

        static void StepChecked(BlockSimulation world, BlockMapSnapshot map, int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                world.Step(map, Dt);
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired),
                    "The support fixture must not hide collision candidate loss.");
            }
        }

        static void Prime(BlockSimulation world, BlockMapSnapshot map, params BlockSimHandle[] handles)
        {
            var expected = new BlockSimMode[handles.Length];
            for (int i = 0; i < handles.Length; i++) expected[i] = Read(world, handles[i]).mode;
            for (int tick = 0; tick < 6; tick++)
            {
                StepChecked(world, map);
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero, "Fixture initialization must not fault.");
                for (int i = 0; i < handles.Length; i++)
                    Assert.That(Read(world, handles[i]).mode, Is.EqualTo(expected[i]),
                        "Exactly supported, distant fixture bodies must remain asleep or Frozen.");
            }
        }

        static BlockSimState[] Capture(BlockSimulation world, params BlockSimHandle[] handles)
        {
            var states = new BlockSimState[handles.Length];
            for (int i = 0; i < handles.Length; i++) states[i] = Read(world, handles[i]);
            return states;
        }

        static void AssertMode(BlockSimulation world, BlockSimMode mode, params BlockSimHandle[] handles)
        {
            foreach (var handle in handles)
                Assert.That(Read(world, handle).mode, Is.EqualTo(mode), "Unexpected immediate mode for " + handle);
        }

        static void AssertSameState(BlockSimState actual, BlockSimState expected, bool includeMode = true)
        {
            Assert.That(actual.handle, Is.EqualTo(expected.handle));
            Assert.That(actual.owner, Is.EqualTo(expected.owner));
            Assert.That(math.all(actual.position == expected.position), Is.True, "Unexpected pose change for " + expected.handle);
            Assert.That(math.all(actual.velocity == expected.velocity), Is.True, "Unexpected velocity change for " + expected.handle);
            Assert.That(actual.poseVersion, Is.EqualTo(expected.poseVersion));
            if (includeMode) Assert.That(actual.mode, Is.EqualTo(expected.mode), "Unexpected mode change for " + expected.handle);
        }

        static void AssertUnchanged(BlockSimulation world, params BlockSimState[] states)
        {
            foreach (var state in states) AssertSameState(Read(world, state.handle), state);
        }

        static BlockSimHandle[] Together(params BlockSimHandle[][] groups)
        {
            var handles = new List<BlockSimHandle>();
            foreach (var group in groups) handles.AddRange(group);
            return handles.ToArray();
        }

        [Test]
        public void RemovingAColumnTopDoesNotWakeOrChangeItsLowerLayers()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var column = Column(world, 1, 5);
            Prime(world, map.Snapshot, column);
            var lower = Capture(world, column[0], column[1], column[2], column[3]);
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.Remove(column[4]), Is.True);

            AssertUnchanged(world, lower);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.Zero);
            Assert.That(world.Count, Is.EqualTo(4));
        }

        [Test]
        public void RemovingAMiddleLayerImmediatelyWakesOnlyItsUpperDependents()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var column = Column(world, 1, 5);
            Prime(world, map.Snapshot, column);
            var lower = Capture(world, column[0], column[1]);
            var upper = Capture(world, column[3], column[4]);
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.Remove(column[2]), Is.True);

            AssertUnchanged(world, lower);
            AssertMode(world, BlockSimMode.Active, column[3], column[4]);
            foreach (var state in upper) AssertSameState(Read(world, state.handle), state, false);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(2));
            StepChecked(world, map.Snapshot, 12);
            foreach (var state in upper)
                Assert.That(Read(world, state.handle).position.y, Is.LessThan(state.position.y - .05f),
                    "Invalidation must enable genuine falling rather than only changing a mode flag.");
            Assert.That(world.Count, Is.EqualTo(4));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void TouchingColumnsDoNotTransmitSupportDeletionSidewaysOrDownward()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var left = Column(world, 1, 4);
            var right = Column(world, 11, 4, Edge); // Exact side contact at every layer.
            Prime(world, map.Snapshot, Together(left, right));
            var unaffected = Capture(world, right);
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.Remove(left[0]), Is.True);

            AssertMode(world, BlockSimMode.Active, left[1], left[2], left[3]);
            AssertUnchanged(world, unaffected);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(3));
            // Do not assert that the right column stays asleep after later real impacts.
        }

        [Test]
        public void OneOfTwoSupportsCanBeRemovedWithoutWakingTheUpperBranch()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var left = Add(world, 1, new float3(-Half, Half, 0));
            var right = Add(world, 2, new float3(Half, Half, 0));
            var top = Add(world, 3, new float3(0, Half + Edge, 0));
            var cap = Add(world, 4, new float3(0, Half + 2 * Edge, 0));
            Prime(world, map.Snapshot, left, right, top, cap);
            var unchanged = Capture(world, right, top, cap);
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.Remove(left), Is.True);
            AssertUnchanged(world, unchanged);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.Zero);

            Assert.That(world.Remove(right), Is.True);
            AssertMode(world, BlockSimMode.Active, top, cap);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(2));
        }

        [Test]
        public void AReconvergingDiamondRetainsTheOtherRootAndInvalidatesEachDependentOnce()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var leftRoot = Add(world, 1, new float3(-Half, Half, 0));
            var rightRoot = Add(world, 2, new float3(Half, Half, 0));
            var leftBranch = Add(world, 3, new float3(-Half, Half + Edge, 0));
            var rightBranch = Add(world, 4, new float3(Half, Half + Edge, 0));
            var joined = Add(world, 5, new float3(0, Half + 2 * Edge, 0));
            var cap = Add(world, 6, new float3(0, Half + 3 * Edge, 0));
            Prime(world, map.Snapshot, leftRoot, rightRoot, leftBranch, rightBranch, joined, cap);
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.Remove(leftRoot), Is.True);
            AssertMode(world, BlockSimMode.Active, leftBranch);
            AssertMode(world, BlockSimMode.Resting, rightRoot, rightBranch, joined, cap);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(1));

            Assert.That(world.Remove(rightRoot), Is.True);
            AssertMode(world, BlockSimMode.Active, leftBranch, rightBranch, joined, cap);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(4),
                "The common upper branch must be processed once, while the previously active branch is not counted again.");
        }

        [Test]
        public void DeletingAFrozenRootWakesItsDependentsWithoutUsingItsSideContactAsABridge()
        {
            using var map = new BlockMapCollisionBuilder().Build(Allocator.Persistent);
            using var world = World();
            // No map floor exists: Frozen identity itself must be an independent support root.
            var frozen = Add(world, 1, new float3(0, 2, 0), BlockSimMode.Frozen);
            var top = Add(world, 2, new float3(0, 2 + Edge, 0));
            var cap = Add(world, 3, new float3(0, 2 + 2 * Edge, 0));
            var adjacent = new[]
            {
                Add(world, 11, new float3(Edge, 2, 0), BlockSimMode.Frozen),
                Add(world, 12, new float3(Edge, 2 + Edge, 0)),
                Add(world, 13, new float3(Edge, 2 + 2 * Edge, 0))
            };
            Prime(world, map.Snapshot, Together(new[] { frozen, top, cap }, adjacent));
            var unchanged = Capture(world, adjacent);
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.Remove(frozen), Is.True);

            AssertMode(world, BlockSimMode.Active, top, cap);
            AssertUnchanged(world, unchanged);
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(2));
        }

        [TestCase(BlockSimMode.Resting)]
        [TestCase(BlockSimMode.Frozen)]
        public void MovingASupportInvalidatesItsOldUpperDependentsOnly(BlockSimMode supportMode)
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var support = Add(world, 1, new float3(0, Half, 0), supportMode);
            var top = Add(world, 2, new float3(0, Half + Edge, 0));
            var cap = Add(world, 3, new float3(0, Half + 2 * Edge, 0));
            var adjacent = Column(world, 11, 3, Edge);
            Prime(world, map.Snapshot, Together(new[] { support, top, cap }, adjacent));
            var unchanged = Capture(world, adjacent);
            var moved = Read(world, support);
            moved.position.x = -3;
            int wakes = world.Diagnostics.SupportWakes;

            Assert.That(world.SetState(moved), Is.True);

            AssertMode(world, BlockSimMode.Active, top, cap);
            AssertUnchanged(world, unchanged);
            Assert.That(Read(world, support).mode, Is.EqualTo(supportMode));
            Assert.That(Read(world, support).position.x, Is.EqualTo(-3));
            Assert.That(world.Diagnostics.SupportWakes - wakes, Is.EqualTo(2));
        }

        [Test]
        public void ANotifiedLocalMapRootRemovalDropsOnlyTheColumnAboveThatPatch()
        {
            using var original = Pads(true).Build(Allocator.Persistent);
            using var replacement = Pads(false).Build(Allocator.Persistent);
            using var world = World();
            var left = Column(world, 1, 4);
            var right = Column(world, 11, 4, 3);
            Prime(world, original.Snapshot, Together(left, right));
            var unchanged = Capture(world, right);
            var before = Capture(world, left);

            world.NotifySupportChanged(new BlockAabb(new float3(0, -.1f, 0), new float3(.6f, .1f, .6f)));
            AssertUnchanged(world, unchanged);
            StepChecked(world, replacement.Snapshot);

            AssertMode(world, BlockSimMode.Active, left);
            AssertUnchanged(world, unchanged);
            foreach (var state in before)
                Assert.That(Read(world, state.handle).position.y, Is.LessThan(state.position.y - .0002f));
            Assert.That(world.Diagnostics.SupportWakes, Is.EqualTo(4));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void SleepingSupportEdgesSurviveManyTicksWithoutFreshSleepingContacts()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var column = Column(world, 1, 8);
            Prime(world, map.Snapshot, column);
            StepChecked(world, map.Snapshot, 120);
            AssertMode(world, BlockSimMode.Resting, column);
            Assert.That(world.Diagnostics.DynamicContactCount, Is.Zero,
                "This fixture must exercise retained support while fresh sleeping/sleeping contacts are absent.");
            var lower = Capture(world, column[0], column[1], column[2]);

            Assert.That(world.Remove(column[3]), Is.True);

            AssertUnchanged(world, lower);
            AssertMode(world, BlockSimMode.Active, column[4], column[5], column[6], column[7]);
        }

        [Test]
        public void SeparatedColumnsOnOneMapFeatureDoNotShareAMomentumIsland()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var left = Column(world, 1, 3);
            var right = Column(world, 11, 3, 3);
            Prime(world, map.Snapshot, Together(left, right));
            var unchanged = Capture(world, right);

            Assert.That(world.ApplyPush(left[0], new float3(1, 0, 0), 3, 100 * Dt), Is.True);

            AssertMode(world, BlockSimMode.Active, left);
            AssertUnchanged(world, unchanged);
            StepChecked(world, map.Snapshot);
            AssertUnchanged(world, unchanged);
        }

        [Test]
        public void SwapBackKeepsSupportEdgesAttachedToStableIdentities()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var discard = Add(world, 999, new float3(8, Half, 0));
            var left = Column(world, 1, 3);
            var right = Column(world, 11, 3, 3); // Its last upper body moves into slot zero.
            Prime(world, map.Snapshot, Together(new[] { discard }, left, right));
            var all = Capture(world, Together(left, right));

            Assert.That(world.Remove(discard), Is.True);
            AssertUnchanged(world, all);
            Assert.That(world.Remove(right[1]), Is.True);

            AssertUnchanged(world, CaptureBefore(all, left));
            AssertMode(world, BlockSimMode.Resting, right[0]);
            AssertMode(world, BlockSimMode.Active, right[2]);
            Assert.That(world.Count, Is.EqualTo(5));
        }

        static BlockSimState[] CaptureBefore(BlockSimState[] states, BlockSimHandle[] handles)
        {
            var selected = new List<BlockSimState>();
            foreach (var handle in handles)
            foreach (var state in states)
                if (state.handle == handle) { selected.Add(state); break; }
            Assert.That(selected.Count, Is.EqualTo(handles.Length));
            return selected.ToArray();
        }

        [TestCase(1u, 1u, 2u)]
        [TestCase(1u, 2u, 1u)]
        [TestCase(2u, 1u, 1u)]
        public void ReusedIdWithADifferentCompleteHandleCannotInheritOrMutateOldSupportEdges(
            uint epoch, uint round, uint generation)
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var old = Column(world, 1, 2);
            Prime(world, map.Snapshot, old);
            Assert.That(world.Remove(old[0]), Is.True);
            var current = Column(world, 1, 2, 3, generation, epoch, round);
            Prime(world, map.Snapshot, current);
            var unchanged = Capture(world, current);

            Assert.That(world.Remove(old[0]), Is.False);
            var stale = new BlockSimState { handle = old[0], owner = 1, position = new float3(3, Half, 0), mode = BlockSimMode.Frozen };
            Assert.That(world.SetState(stale), Is.False);
            AssertUnchanged(world, unchanged);
            Assert.That(world.TryGet(old[1], out _), Is.True, "Old and new upper IDs are separate live complete handles.");

            Assert.That(world.Remove(current[0]), Is.True);
            AssertMode(world, BlockSimMode.Active, current[1]);
            Assert.That(world.TryGet(old[1], out _), Is.True);
            Assert.That(world.Count, Is.EqualTo(2));
        }

        [Test]
        public void ClearRemovesSupportDependenciesAcrossEpochAndRoundReuse()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = World();
            var old = Column(world, 1, 3);
            Prime(world, map.Snapshot, old);
            world.Clear();
            Assert.That(world.Count, Is.Zero);
            var current = Column(world, 1, 3, 3, epoch: 2, round: 2);
            Prime(world, map.Snapshot, current);
            var unchanged = Capture(world, current);

            Assert.That(world.Remove(old[0]), Is.False);
            AssertUnchanged(world, unchanged);
            Assert.That(world.Remove(current[1]), Is.True);
            AssertMode(world, BlockSimMode.Resting, current[0]);
            AssertMode(world, BlockSimMode.Active, current[2]);
            foreach (var handle in old) Assert.That(world.TryGet(handle, out _), Is.False);
        }

        [Test]
        public void FailedTickRestoresStatesAndRetainsPendingMapSupportValidationForRetry()
        {
            using var original = Pads(true).Build(Allocator.Persistent);
            using var replacement = Pads(false).Build(Allocator.Persistent);
            using var retried = World();
            using var control = World();
            var left = Column(retried, 1, 3);
            var right = Column(retried, 11, 3, 3);
            Column(control, 1, 3); Column(control, 11, 3, 3);
            var handles = Together(left, right);
            Prime(retried, original.Snapshot, handles);
            Prime(control, original.Snapshot, handles);
            var changed = new BlockAabb(new float3(0, -.1f, 0), new float3(.6f, .1f, .6f));
            retried.NotifySupportChanged(changed); control.NotifySupportChanged(changed);

            // Public finite inputs deterministically exercise the existing bounded-sweep failure.
            // Its range exceeds addressable cells; preparation rejects it before attempting allocation.
            var poison = Add(retried, 999, new float3(10, 2, 10), BlockSimMode.Active,
                velocity: new float3(1e20f, 0, 0));
            var before = Capture(retried, Together(handles, new[] { poison }));
            long tick = retried.Diagnostics.TickCount;
            long faults = retried.Diagnostics.SolverFaults;

            var error = Assert.Throws<InvalidOperationException>(() => retried.Step(replacement.Snapshot, Dt));
            StringAssert.Contains("sweep capacity", error.Message);
            AssertUnchanged(retried, before);
            Assert.That(retried.Diagnostics.TickCount, Is.EqualTo(tick));
            Assert.That(retried.Diagnostics.SolverFaults, Is.EqualTo(faults + 1));

            Assert.That(retried.Remove(poison), Is.True);
            StepChecked(retried, replacement.Snapshot);
            StepChecked(control, replacement.Snapshot);
            foreach (var handle in handles) AssertSameState(Read(retried, handle), Read(control, handle));
            AssertMode(retried, BlockSimMode.Active, left);
            AssertMode(retried, BlockSimMode.Resting, right);
            Assert.That(retried.Diagnostics.SupportWakes, Is.EqualTo(control.Diagnostics.SupportWakes));

            // The successful retry must also publish usable dependencies, not merely match one pose.
            Assert.That(retried.Remove(right[1]), Is.True);
            Assert.That(control.Remove(right[1]), Is.True);
            foreach (var handle in new[] { right[0], right[2] })
                AssertSameState(Read(retried, handle), Read(control, handle));
            AssertMode(retried, BlockSimMode.Resting, right[0]);
            AssertMode(retried, BlockSimMode.Active, right[2]);
        }
    }
}
#endif
