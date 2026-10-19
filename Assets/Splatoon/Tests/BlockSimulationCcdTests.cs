using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    [Category("BlockSimulationPrototype")]
    public sealed class BlockSimulationCcdTests
    {
        const float Dt = 1f / 60;
        static BlockSimHandle Add(BlockSimulation world, ulong id, float3 position, float3 velocity, BlockSimMode mode = BlockSimMode.Active)
        {
            var handle = new BlockSimHandle(1, 1, id);
            Assert.That(world.Add(new BlockSimState { handle = handle, owner = 1, position = position, velocity = velocity, mode = mode }), Is.True);
            return handle;
        }

        [Test]
        public void OpposingHighSpeedBlocksDoNotExchangeSidesBetweenSubsteps()
        {
            using var world = new BlockSimulation(1);
            var left = Add(world, 17, new float3(-2, 2, 0), new float3(240, 0, 0));
            var right = Add(world, 3, new float3(2, 2, 0), new float3(-240, 0, 0));
            world.Step(default, Dt);
            Assert.That(world.TryGet(left, out var a) && world.TryGet(right, out _), Is.True);
            world.TryGet(right, out var b);
            Assert.That(b.position.x - a.position.x, Is.GreaterThanOrEqualTo(.298f), "Endpoint overlap alone can miss a complete side exchange.");
            Assert.That(a.position.x, Is.GreaterThan(-1));
            Assert.That(b.position.x, Is.LessThan(1));
            Assert.That(math.abs(a.velocity.x) + math.abs(b.velocity.x), Is.LessThan(.01f));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void SweptHashFindsEveryIndependentCollisionAcrossMultipleCells()
        {
            using var world = new BlockSimulation(2);
            var left = new BlockSimHandle[24]; var right = new BlockSimHandle[24];
            for (int i = 0; i < left.Length; i++)
            {
                float offset = .031f * i;
                left[i] = Add(world, (ulong)(1000 - i), new float3(-2 - offset, 2, i * 2), new float3(240, 0, 0));
                right[i] = Add(world, (ulong)(i + 1), new float3(2 + offset, 2, i * 2), new float3(-240, 0, 0));
            }
            for (int tick = 0; tick < 2; tick++)
            {
                world.Step(default, Dt);
                for (int i = 0; i < left.Length; i++)
                {
                    Assert.That(world.TryGet(left[i], out var a), Is.True); Assert.That(world.TryGet(right[i], out var b), Is.True);
                    Assert.That(b.position.x - a.position.x, Is.GreaterThanOrEqualTo(.298f), "Missing swept pair in lane " + i);
                }
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired));
            }
            Assert.That(world.Count, Is.EqualTo(48));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void CollisionWakesAnEntireTouchingRowWithinTheCurrentTick()
        {
            var builder = new BlockMapCollisionBuilder(); builder.AddBox(new float3(10, -.1f, 0), new float3(30, .1f, 3));
            using var map = builder.Build(); using var world = new BlockSimulation(1);
            world.SetObservers(new[] { new float3(100, 0, 100) });
            var row = new BlockSimHandle[80];
            for (int i = 0; i < row.Length; i++) row[i] = Add(world, (ulong)i + 1, new float3(i * .3f, .15f, 0), 0, BlockSimMode.Resting);
            world.Step(map.Snapshot, Dt);
            foreach (var handle in row)
            { world.TryGet(handle, out var state); Assert.That(state.mode, Is.EqualTo(BlockSimMode.Resting)); }
            Add(world, 1001, new float3(-.6f, .15f, 0), new float3(30, 0, 0));
            world.Step(map.Snapshot, Dt);
            foreach (var handle in row)
            {
                world.TryGet(handle, out var state);
                Assert.That(state.mode, Is.EqualTo(BlockSimMode.Active), "A same-tick contact wake cannot stop at a hash-cell or two-metre boundary.");
            }
            Assert.That(world.Count, Is.EqualTo(81));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void DeletingAFrozenSupportDoesNotLeaveItsRestingDependentSuspended()
        {
            var builder = new BlockMapCollisionBuilder(); builder.AddBox(new float3(0, -.1f, 0), new float3(4, .1f, 4));
            using var map = builder.Build(); using var world = new BlockSimulation(1);
            var support = Add(world, 1, new float3(0, .15f, 0), 0, BlockSimMode.Frozen);
            var top = Add(world, 2, new float3(0, .45f, 0), 0, BlockSimMode.Resting);
            world.Step(map.Snapshot, Dt);
            world.TryGet(top, out var before); Assert.That(before.mode, Is.EqualTo(BlockSimMode.Resting));
            Assert.That(world.Remove(support), Is.True);
            for (int tick = 0; tick < 90; tick++) world.Step(map.Snapshot, Dt);
            world.TryGet(top, out var after);
            Assert.That(after.position.y, Is.EqualTo(.15f).Within(.001f));
            Assert.That(after.mode, Is.EqualTo(BlockSimMode.Resting));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }
    }
}
