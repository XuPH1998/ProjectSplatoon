#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Tests
{
    // Pure data geometry and the production motor's static cast wrapper.
    // Native controller stepping/sliding remains covered by the existing PlayMode suites.
    [Category("BlockFixedGrid"), Category("BlockFixedGridInitialContact69")]
    public sealed class BlockFixedGridInitialContact69Tests
    {
        const float Radius = .349f, HalfSegment = .55f;
        static readonly Vector3 Start = new(.15f, 1.2f, -.3485f);
        static readonly MethodInfo MotorCast = typeof(PlayerMotorSimulation).GetMethod("CastFixedMotor",
            BindingFlags.Static | BindingFlags.NonPublic);
        static float AcceptedRadius => Mathf.Max(0, Radius - .001f - .00001f);
        static float InitialTolerance => Radius - AcceptedRadius;

        sealed class Environment : IBlockFixedGridEnvironment
        {
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            { support = new BlockFixedGridMapSupport(6902, .08f, new BlockSurfaceKey(6902, cell.x, cell.z)); return true; }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embedded,
                bool relocating, out string reason) { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
        }

        static BlockFixedGridWorld World()
        {
            var surface = new BlockSurfaceWorld(69);
            var keys = new Vector2Int[289]; int at = 0;
            for (int z = -8; z <= 8; z++) for (int x = -8; x <= 8; x++) keys[at++] = new Vector2Int(x, z);
            surface.RegisterFace(6902, Vector3.zero, Vector3.right, Vector3.forward, keys);
            foreach (var key in keys) surface.TrySet(new BlockSurfaceKey(6902, key.x, key.y), 1, out _);
            var world = new BlockFixedGridWorld(new BlockWorld(new Vector2(32, 32), 69), surface, new Environment());
            AddColumn(world, 0); return world;
        }

        static void AddColumn(BlockFixedGridWorld world, int z, int x = 0)
        {
            for (int y = 0; y < 4; y++)
                Assert.That(world.Build(new[] { new Vector3Int(x, y, z) }, 1, 1).Added,
                    Is.EqualTo(1), world.LastFailure);
        }

        static bool EndpointOverlaps(BlockFixedGridWorld world, Vector3 center)
            => BlockFixedGridQueries.OverlapCapsule(world, center, HalfSegment, Radius - .001f,
                Vector3.up, out _, .00001f);

        static bool CastMotor(BlockFixedGridWorld world, Vector3 center, Vector3 direction, float distance,
            out BlockFixedGridHit hit)
        {
            Assert.That(MotorCast, Is.Not.Null, "Install the frozen two-file motor candidate before this fast lane.");
            object[] args = { world, center, HalfSegment, Radius, direction, distance, default(BlockFixedGridHit) };
            bool blocked = (bool)MotorCast.Invoke(null, args); hit = (BlockFixedGridHit)args[6]; return blocked;
        }

        static BlockFixedGridHit AssertShallowStart(BlockFixedGridWorld world)
        {
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.3f));
            Assert.That(EndpointOverlaps(world, Start), Is.False);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, Start, HalfSegment, Radius,
                Vector3.back, .03f, out var exact), Is.True);
            Assert.That(exact.Distance, Is.LessThanOrEqualTo(.000001f));
            Assert.That(exact.State.Cell, Is.EqualTo(new Vector3Int(0, 2, 0)));
            Assert.That(Vector3.Dot(exact.Normal, Vector3.back), Is.GreaterThan(.99999f));
            return exact;
        }

        [TestCase(false, .03f), TestCase(false, .9f), TestCase(true, .03f), TestCase(true, .9f)]
        public void AcceptedShallowContactCanSeparateOrMoveTangentiallyInShortAndLongCasts(bool tangent, float distance)
        {
            var world = World(); AssertShallowStart(world);
            var direction = tangent ? Vector3.right : Vector3.back;
            Assert.That(CastMotor(world, Start, direction, distance, out _), Is.False);
            Assert.That(EndpointOverlaps(world, Start + direction * distance), Is.False);
            Assert.That(world.Count, Is.EqualTo(4));
        }

        [TestCase(.03f), TestCase(.9f)]
        public void AcceptedShallowContactStillBlocksInwardShortAndLongMotion(float distance)
        {
            var world = World(); var original = AssertShallowStart(world);
            Assert.That(CastMotor(world, Start, Vector3.forward, distance, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(original.State.Handle));
            Assert.That(hit.Distance, Is.LessThanOrEqualTo(.000001f));
        }

        [Test] public void NearlyTangentialButInwardMotionDoesNotReceiveANegativeDotTolerance()
        {
            var world = World(); AssertShallowStart(world);
            var direction = (Vector3.right + Vector3.forward * .0000001f).normalized;
            Assert.That(CastMotor(world, Start, direction, .03f, out var hit), Is.True);
            Assert.That(hit.Distance, Is.LessThanOrEqualTo(.000001f));
        }

        [TestCase(.28f), TestCase(2f)]
        public void EscapingFirstContactStillHitsAnotherOneCellThickColumnInShortAndLongCasts(float distance)
        {
            var world = World(); AddColumn(world, -4); AssertShallowStart(world);
            Assert.That(CastMotor(world, Start, Vector3.back, distance, out var hit), Is.True);
            // The two vertically adjacent candidates have identical TOI; lower stable ID wins.
            Assert.That(hit.State.Cell, Is.EqualTo(new Vector3Int(0, 2, -4)));
            Assert.That(hit.Distance, Is.EqualTo(.2025f).Within(.00001f));
            Assert.That(EndpointOverlaps(world, Start + Vector3.back * (hit.Distance - .001f)), Is.False);
            Assert.That(world.Count, Is.EqualTo(8));
        }

        [TestCase(-.34f), TestCase(.15f)]
        public void DeepOverlapAndInsideExpandedBoxCannotEscapeThroughTheShallowMotorException(float z)
        {
            var world = World(); var center = new Vector3(Start.x, Start.y, z);
            Assert.That(EndpointOverlaps(world, center), Is.True);
            Assert.That(CastMotor(world, center, Vector3.back, .03f, out var hit), Is.True);
            Assert.That(hit.Distance, Is.LessThanOrEqualTo(.000001f));
            Assert.That(BlockFixedGridQueries.CastCapsule(world, center, HalfSegment, Radius,
                Vector3.back, .03f, out hit, InitialTolerance), Is.True);
            Assert.That(hit.Distance, Is.LessThanOrEqualTo(.000001f));
        }

        [TestCase(-.00001f), TestCase(.00001f)]
        public void TheOriginalEndpointEffectiveRadiusControlsBothSidesOfTheEligibilityBoundary(float gap)
        {
            var world = World(); var center = new Vector3(Start.x, Start.y, -(AcceptedRadius + gap));
            bool deep = gap < 0;
            Assert.That(EndpointOverlaps(world, center), Is.EqualTo(deep));
            Assert.That(CastMotor(world, center, Vector3.back, .03f, out _), Is.EqualTo(deep));
            Assert.That(BlockFixedGridQueries.CastCapsule(world, center, HalfSegment, Radius,
                Vector3.back, .03f, out _, InitialTolerance), Is.EqualTo(deep));
        }

        [Test] public void SkippingTheFirstShallowT0CannotHideAnotherSimultaneousInwardT0()
        {
            var world = World(); AddColumn(world, 0, 3);
            float z = -Mathf.Sqrt(.3485f * .3485f - .3f * .3f);
            var center = new Vector3(.6f, Start.y, z);
            Assert.That(EndpointOverlaps(world, center), Is.False);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, center, HalfSegment, Radius,
                Vector3.right, .03f, out var first), Is.True);
            Assert.That(first.State.Cell, Is.EqualTo(new Vector3Int(0, 2, 0)));
            Assert.That(Vector3.Dot(Vector3.right, first.Normal), Is.GreaterThan(0));
            Assert.That(CastMotor(world, center, Vector3.right, .03f, out var hit), Is.True);
            Assert.That(hit.State.Cell, Is.EqualTo(new Vector3Int(3, 2, 0)));
            Assert.That(hit.Distance, Is.LessThanOrEqualTo(.000001f));
            Assert.That(Vector3.Dot(Vector3.right, hit.Normal), Is.LessThan(0));
        }

        [TestCase(0f), TestCase(-.001f), TestCase(float.NaN), TestCase(float.PositiveInfinity), TestCase(Radius)]
        public void DefaultAndInvalidInitialTolerancePreserveTheExistingExactHit(float tolerance)
        {
            var world = World(); var original = AssertShallowStart(world);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, Start, HalfSegment, Radius,
                Vector3.back, .03f, out var hit, tolerance), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(original.State.Handle));
            Assert.That(hit.Distance, Is.EqualTo(original.Distance));
            Assert.That(hit.Normal, Is.EqualTo(original.Normal)); Assert.That(hit.Point, Is.EqualTo(original.Point));
        }

        [Test] public void ZeroDirectionRemainsAnOverlapEvenWithOptedInInitialTolerance()
        {
            var world = World(); AssertShallowStart(world);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, Start, HalfSegment, Radius,
                Vector3.zero, .03f, out var original), Is.True);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, Start, HalfSegment, Radius,
                Vector3.zero, .03f, out var hit, InitialTolerance), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(original.State.Handle));
            Assert.That(hit.Distance, Is.EqualTo(original.Distance));
        }

        [Test] public void DifferentActualDirectionIsRecheckedInsteadOfInheritingTheRequestedEscape()
        {
            var world = World(); AssertShallowStart(world);
            Assert.That(CastMotor(world, Start, Vector3.back, .03f, out _), Is.False);
            Assert.That(CastMotor(world, Start, Vector3.forward, .03f, out var actualHit), Is.True);
            Assert.That(actualHit.Distance, Is.LessThanOrEqualTo(.000001f));
        }
    }
}
#endif
