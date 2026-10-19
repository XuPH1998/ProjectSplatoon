#if UNITY_EDITOR
using System;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    /// <summary>Independent double-precision convex-distance oracle; no native Physics queries.</summary>
    [Category("BlockSimulationPrototype")]
    public sealed class BlockCapsuleOracleTests
    {
        [Test]
        public void SegmentBoxDistanceMatchesAnIndependentDoublePrecisionMinimizer()
        {
            var random = new System.Random(710213);
            for (int sample = 0; sample < 768; sample++)
            {
                float3 center = Next(random, -4, 4), extents = Next(random, .005f, 1.7f);
                float3 a = Next(random, -7, 7), b = sample % 17 == 0 ? a : Next(random, -7, 7);
                var box = new BlockAabb(center, extents);
                double expected = ReferenceDistanceSquared(a, b, box);
                float actual = BlockCapsuleQueries.DistanceSquared(a, b, box, out var onSegment, out var onBox);
                Assert.That(actual, Is.EqualTo(expected).Within(Math.Max(.00003, expected * .000003)), $"sample {sample}");
                Assert.That(math.distance(onBox, math.clamp(onBox, box.Min, box.Max)), Is.LessThan(.00001f));
                Assert.That(math.lengthsq(onSegment - onBox), Is.EqualTo(actual).Within(.00003f));
                float3 delta = b - a;
                if (math.lengthsq(delta) > .000001f)
                {
                    float parameter = math.dot(onSegment - a, delta) / math.lengthsq(delta);
                    Assert.That(parameter, Is.InRange(-.00001f, 1.00001f));
                    Assert.That(math.distance(onSegment, a + delta * parameter), Is.LessThan(.00002f));
                }
            }
        }

        [Test]
        public void SlantedSegmentDistanceAndContactUseTheInteriorOfTheSegment()
        {
            var box = new BlockAabb(new float3(0), new float3(1));
            var capsule = new BlockSimCapsule { a = new float3(-3, 2, -.5f), b = new float3(3, 2, .5f), radius = 1.01f };
            float squared = BlockCapsuleQueries.DistanceSquared(capsule.a, capsule.b, box, out var point, out var onBox);
            Assert.That(squared, Is.EqualTo(1).Within(.00001f));
            Assert.That(point.x, Is.InRange(-1.00001f, 1.00001f));
            Assert.That(math.distance(point, capsule.a), Is.GreaterThan(1));
            Assert.That(math.distance(point, capsule.b), Is.GreaterThan(1));
            Assert.That(onBox.y, Is.EqualTo(1).Within(.00001f));
            Assert.That(BlockCapsuleQueries.Contact(capsule, box, 0, out var hit), Is.True);
            Assert.That(hit.separation, Is.EqualTo(-.01f).Within(.00001f));
            Assert.That(math.distance(hit.normal, new float3(0, 1, 0)), Is.LessThan(.00001f));
        }

        [Test]
        public void RoundedCapsuleCornerDoesNotBehaveLikeAnExpandedBox()
        {
            var box = new BlockAabb(new float3(0), new float3(1));
            var outside = new BlockSimCapsule { a = new float3(1.18f, 1.18f, -.2f), b = new float3(1.18f, 1.18f, .2f), radius = .2f };
            Assert.That(outside.Bounds.Overlaps(box), Is.True, "Broad phase intentionally overlaps at this rounded corner.");
            Assert.That(BlockCapsuleQueries.Contact(outside, box, 0, out _), Is.False);
            var inside = outside; inside.a.x = inside.b.x = 1.12f; inside.a.y = inside.b.y = 1.12f;
            Assert.That(BlockCapsuleQueries.Contact(inside, box, 0, out var hit), Is.True);
            Assert.That(hit.separation, Is.EqualTo(math.sqrt(2 * .12f * .12f) - .2f).Within(.00001f));
        }

        [Test]
        public void ASlantedCapsuleSweepFindsTheRealFaceBeforeTheEndPosition()
        {
            var box = new BlockAabb(new float3(0), new float3(1));
            var capsule = new BlockSimCapsule { a = new float3(-3, -.5f, -.3f), b = new float3(-2.5f, .5f, .3f), radius = .2f };
            var result = BlockCapsuleQueries.Sweep(capsule, box, new float3(5, 0, 0), out var hit);
            Assert.That(result, Is.EqualTo(BlockCapsuleSweepResult.Hit));
            Assert.That(hit.fraction, Is.EqualTo((2.5f - 1 - .2f) / 5).Within(.0001f));
            Assert.That(hit.normal.x, Is.LessThan(-.999f));
            Assert.That(hit.point.x, Is.EqualTo(-1).Within(.00001f));
        }

        [Test]
        public void TouchingTangentialAndSeparatingSweepsDoNotBlockMovement()
        {
            var box = new BlockAabb(new float3(0), new float3(1));
            var capsule = new BlockSimCapsule { a = new float3(0, 1.2f, -.3f), b = new float3(0, 1.2f, .3f), radius = .2f };
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(.5f, 0, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Miss));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(0, 1, 0), out _), Is.EqualTo(BlockCapsuleSweepResult.Miss));
            Assert.That(BlockCapsuleQueries.Sweep(capsule, box, new float3(0, -1, 0), out var hit), Is.EqualTo(BlockCapsuleSweepResult.Hit));
            Assert.That(hit.fraction, Is.EqualTo(0).Within(.00001f));
            Assert.That(hit.normal.y, Is.GreaterThan(.999f));
        }

        static float3 Next(System.Random random, float min, float max)
            => new((float)(min + random.NextDouble() * (max - min)), (float)(min + random.NextDouble() * (max - min)),
                (float)(min + random.NextDouble() * (max - min)));

        static double ReferenceDistanceSquared(float3 a, float3 b, BlockAabb box)
        {
            // Squared distance to a convex box along a segment is convex in t.
            // Minimise it numerically in doubles, independently of production's breakpoint/quadratic method.
            double lo = 0, hi = 1;
            for (int i = 0; i < 120; i++)
            {
                double first = lo + (hi - lo) / 3, second = hi - (hi - lo) / 3;
                if (At(first) <= At(second)) hi = second; else lo = first;
            }
            return Math.Min(At(0), Math.Min(At(1), At((lo + hi) * .5)));
            double At(double t)
            {
                double squared = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    double value = (double)a[axis] + ((double)b[axis] - a[axis]) * t;
                    double minimum = (double)box.center[axis] - box.extents[axis], maximum = (double)box.center[axis] + box.extents[axis];
                    double distance = value < minimum ? minimum - value : value > maximum ? value - maximum : 0;
                    squared += distance * distance;
                }
                return squared;
            }
        }
    }
}
#endif
