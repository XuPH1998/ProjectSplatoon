using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    [Category("BlockSimulationPrototype")]
    public sealed class BlockCapsuleQueryTests
    {
        static BlockSimCapsule Capsule(float3 a, float3 b, float r) => new() { a = a, b = b, radius = r };

        [Test]
        public void HighSpeedCapsuleHitsThinWallAtTheRealSurface()
        {
            var capsule = Capsule(new float3(-2, .3f, 0), new float3(-2, 1.5f, 0), .3f);
            var wall = new BlockAabb(new float3(0, 1, 0), new float3(.01f, 2, 2));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, wall, new float3(4, 0, 0), out var hit), Is.EqualTo(BlockCapsuleSweepResult.Hit));
            Assert.That(hit.fraction, Is.EqualTo(.4225f).Within(.00001f));
            Assert.That(hit.normal.x, Is.LessThan(-.999f));
        }

        [Test]
        public void InflatedBoxCornerIsOnlyBroadphase()
        {
            var capsule = Capsule(new float3(-2, 0, .7f), new float3(-2, 0, .7f), .25f);
            var box = new BlockAabb(0, new float3(.5f));
            // The centre reaches the inflated AABB but stays outside its rounded corner.
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(1.3f, 0, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Miss));
        }

        [Test]
        public void SphericalCapCanLandOnBoxTop()
        {
            var capsule = Capsule(new float3(0, 2, 0), new float3(0, 3, 0), .25f);
            var box = new BlockAabb(0, new float3(.15f));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(0, -3, 0), out var hit), Is.EqualTo(BlockCapsuleSweepResult.Hit));
            Assert.That(hit.fraction, Is.EqualTo(1.6f / 3).Within(.00001f));
            Assert.That(hit.normal.y, Is.GreaterThan(.999f));
        }

        [Test]
        public void TouchingFloorDoesNotBlockHorizontalOrUpwardMotion()
        {
            var capsule = Capsule(new float3(0, .4f, 0), new float3(0, 1.4f, 0), .25f);
            var box = new BlockAabb(0, new float3(2, .15f, 2));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(1, 0, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Miss));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(0, 1, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Miss));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(0, -1, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Hit));
        }

        [Test]
        public void SegmentClosestPointIsNotLimitedToItsEndpoints()
        {
            var box = new BlockAabb(0, new float3(.5f));
            float squared = BlockCapsuleQueries.DistanceSquared(new float3(-3, .7f, 0), new float3(3, .7f, 0), box, out var onSegment, out var onBox);
            Assert.That(squared, Is.EqualTo(.04f).Within(.000001f));
            Assert.That(math.distance(onSegment, onBox), Is.EqualTo(.2f).Within(.000001f));
        }

        [Test]
        public void InitialOverlapReturnsFiniteSeparationAndInvalidInputIsExplicit()
        {
            var capsule = Capsule(new float3(0, -.1f, 0), new float3(0, .1f, 0), .25f);
            var box = new BlockAabb(0, new float3(.15f));
            Assert.That(BlockCapsuleQueries.Contact(capsule, box, 0, out var hit), Is.True);
            Assert.That(hit.separation, Is.LessThan(0));
            Assert.That(math.length(hit.normal), Is.EqualTo(1).Within(.000001f));
            capsule.radius = float.NaN;
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(1, 0, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Unresolved));
        }
    }
}
