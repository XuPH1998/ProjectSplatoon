using System;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridQueryTests
    {
        sealed class Environment : IBlockFixedGridEnvironment
        {
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            { support = new BlockFixedGridMapSupport(11, .08f, new BlockSurfaceKey(11, cell.x, cell.z)); return true; }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embedded, bool relocating, out string reason)
            { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
        }

        static BlockFixedGridWorld World()
        {
            var surface = new BlockSurfaceWorld(7);
            var keys = new Vector2Int[81]; int at = 0;
            for (int z = -4; z <= 4; z++) for (int x = -4; x <= 4; x++) keys[at++] = new Vector2Int(x, z);
            surface.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, keys);
            foreach (var key in keys) surface.TrySet(new BlockSurfaceKey(11, key.x, key.y), 1, out _);
            return new BlockFixedGridWorld(new BlockWorld(new Vector2(32, 32), 7), surface, new Environment());
        }

        static BlockFixedGridState Add(BlockFixedGridWorld world, Vector3Int cell)
        {
            Assert.That(world.Build(new[] { cell }, 1, 1).Added, Is.EqualTo(1), world.LastFailure);
            Assert.That(world.TryGet(cell, out var state), Is.True); return state;
        }

        [TestCase(0f, 0f, 1.3f)]
        [TestCase(.65f, 0f, 1.3677124f)]
        [TestCase(.62f, .64f, 1.4225403f)]
        public void SphereSweepUsesRoundedFaceEdgeAndCorner(float y, float z, float expected)
        {
            Assert.That(BlockFixedGridQueries.SweepSphereBox(new Vector3(-2, y, z), Vector3.right, 4, .2f,
                new Bounds(Vector3.zero, Vector3.one), out var time, out var normal), Is.True);
            Assert.That(time, Is.EqualTo(expected).Within(.00001f));
            Vector3 center = new Vector3(-2, y, z) + Vector3.right * time;
            Vector3 point = new Bounds(Vector3.zero, Vector3.one).ClosestPoint(center);
            Assert.That((center - point).magnitude, Is.EqualTo(.2f).Within(.000001f));
            Assert.That(Vector3.Dot(normal, (center - point).normalized), Is.GreaterThan(.99999f));
        }

        [Test] public void ExpandedSlabCornerDoesNotProduceFalseSphereHit()
            => Assert.That(BlockFixedGridQueries.SweepSphereBox(new Vector3(-2, .65f, .65f), Vector3.right, 4, .2f,
                new Bounds(Vector3.zero, Vector3.one), out _, out _), Is.False);

        [Test] public void ThinCubeCannotBeSkippedByLongSweepAndRayStartsInside()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * .3f);
            Assert.That(BlockFixedGridQueries.SweepSphereBox(Vector3.left * 100, Vector3.right, 200, 0,
                bounds, out var time, out _), Is.True);
            Assert.That(time, Is.EqualTo(99.85f).Within(.00001f));
            Assert.That(BlockFixedGridQueries.SweepSphereBox(Vector3.zero, Vector3.right, 1, 0,
                bounds, out time, out _), Is.True);
            Assert.That(time, Is.Zero);
        }

        [Test] public void CastSeesCommittedCubeWithoutAnyUnityObject()
        {
            var world = World(); var state = Add(world, Vector3Int.zero);
            Assert.That(BlockFixedGridQueries.Cast(world, new Vector3(.15f, .15f, -1), Vector3.forward, 2, .05f, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(state.Handle));
            Assert.That(hit.Distance, Is.EqualTo(.95f).Within(.000001f));
            Assert.That(hit.Point.z, Is.Zero.Within(.000001f));
            Assert.That(hit.Normal, Is.EqualTo(Vector3.back));
        }

        [Test] public void NegativeGridCoordinatesUseTheSameContinuousSweep()
        {
            var world = World(); var state = Add(world, new Vector3Int(-3, 0, -4));
            Assert.That(BlockFixedGridQueries.Cast(world, new Vector3(-.75f, .15f, -3), Vector3.forward, 3, 0, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(state.Handle)); Assert.That(hit.Distance, Is.EqualTo(1.8f).Within(.000001f));
            Assert.That(hit.Point.z, Is.EqualTo(-1.2f).Within(.000001f));
        }

        [Test] public void CapsuleSweepIncludesTheFullUprightSegment()
        {
            var world = World(); Add(world, Vector3Int.zero);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, new Vector3(-.2f, .8f, .15f), .6f, .1f,
                Vector3.right, 1, out var hit), Is.True);
            Assert.That(hit.Distance, Is.EqualTo(.1f).Within(.000001f));
            Assert.That(Vector3.Distance(hit.Normal, Vector3.left), Is.LessThan(.000001f));
        }

        [Test] public void ExistingTopContactCanSlideAndSeparateButCannotMoveDownward()
        {
            var world = World(); Add(world, Vector3Int.zero);
            var center = new Vector3(.15f, .4f, .15f);
            Assert.That(BlockFixedGridQueries.Cast(world, center, Vector3.right, .1f, .1f, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Cast(world, center, Vector3.up, .1f, .1f, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Cast(world, center, Vector3.down, .1f, .1f, out var hit), Is.True);
            Assert.That(hit.Distance, Is.LessThan(.00001f)); Assert.That(hit.Normal.y, Is.GreaterThan(.9999f));
        }

        [Test] public void OptionalFloorContactToleranceOnlyRelaxesFlatUpwardTouching()
        {
            var world = World(); Add(world, Vector3Int.zero);
            Assert.That(BlockFixedGridQueries.OverlapCapsule(world, new Vector3(.15f, .3993744f, .15f), 0, .1f,
                Vector3.up, out _), Is.True, "The unrelaxed query still reports the original sub-millimetre penetration.");
            Assert.That(BlockFixedGridQueries.OverlapCapsule(world, new Vector3(.15f, .3993744f, .15f), 0, .1f,
                Vector3.up, out _, floorContactTolerance: .001f), Is.False);
            Assert.That(BlockFixedGridQueries.OverlapCapsule(world, new Vector3(.15f, .398f, .15f), 0, .1f,
                Vector3.up, out _, floorContactTolerance: .001f), Is.True);
            Assert.That(BlockFixedGridQueries.OverlapCapsule(world, new Vector3(-.0998f, .15f, .15f), 0, .1f,
                Vector3.up, out _, floorContactTolerance: .001f), Is.True, "A 0.2mm side intrusion remains blocked.");
            Assert.That(BlockFixedGridQueries.OverlapCapsule(world, new Vector3(.15f, -.0998f, .15f), 0, .1f,
                Vector3.up, out _, floorContactTolerance: .001f), Is.True, "A 0.2mm ceiling intrusion remains blocked.");
        }

        [Test] public void StableSupportComesFromExposedTopOnlyAndRespectsReach()
        {
            var world = World(); Add(world, Vector3Int.zero); var top = Add(world, Vector3Int.up);
            Assert.That(BlockFixedGridQueries.Support(world, new Vector3(.15f, .6001f, .15f), .2f, .08f, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(top.Handle)); Assert.That(hit.Point.y, Is.EqualTo(.6f).Within(.000001f));
            Assert.That(BlockFixedGridQueries.Support(world, new Vector3(.15f, 2, .15f), .2f, .08f, out _), Is.False);
        }

        [Test] public void RotatedBoxUsesSeparatingAxesBeyondItsBroadBounds()
        {
            var world = World(); Add(world, Vector3Int.zero);
            Quaternion rotation = Quaternion.Euler(0, 45, 0); Vector3 half = new(.2f, .05f, .02f);
            Assert.That(BlockFixedGridQueries.OverlapBox(world, new Vector3(.38f, .15f, .38f), half, rotation), Is.False);
            Assert.That(BlockFixedGridQueries.OverlapBox(world, new Vector3(.28f, .15f, .28f), half, rotation), Is.True);
        }

        [Test] public void RemovingBottomMovesSameIdentityAndAllQueriesUseNewCellImmediately()
        {
            var world = World(); var bottom = Add(world, Vector3Int.zero); var upper = Add(world, Vector3Int.up);
            Assert.That(world.Destroy(new[] { bottom.Handle }, 2, 1, 0).RemovedEnemy, Is.EqualTo(1));
            Assert.That(world.TryGet(upper.Handle, out var moved), Is.True); Assert.That(moved.Cell, Is.EqualTo(Vector3Int.zero));
            Assert.That(moved.Revision, Is.GreaterThan(upper.Revision));
            Assert.That(BlockFixedGridQueries.Cast(world, new Vector3(.15f, .45f, -1), Vector3.forward, 2, 0, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Cast(world, new Vector3(.15f, .15f, -1), Vector3.forward, 2, 0, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(upper.Handle));
            Assert.That(world.TryGet(bottom.Handle, out _), Is.False);
        }


        // Pure committed-data queries only. These cases do not create Unity
        // objects or replace the actual native-displacement witness/PlayMode gate.
        const float SeamRadius65 = .325f, SeamHalfSegment65 = .575f, SeamFloorTolerance65 = .001f;
        static Vector3 SeamCenter65(float x, float z, float feet = .29937440156936646f)
            => new(x, feet + .9f, z);
        static bool SeamOverlap65(BlockFixedGridWorld world, Vector3 center, out BlockFixedGridHit hit, float tolerance = SeamFloorTolerance65)
            => BlockFixedGridQueries.OverlapCapsule(world, center, SeamHalfSegment65, SeamRadius65,
                Vector3.up, out hit, floorContactTolerance: tolerance);
        static BlockFixedGridState AddOwner65(BlockFixedGridWorld world, Vector3Int cell, byte owner)
        {
            world.Surfaces.TrySet(new BlockSurfaceKey(11, cell.x, cell.z), owner, out _);
            Assert.That(world.Build(new[] { cell }, owner, 1).Added, Is.EqualTo(1), world.LastFailure);
            Assert.That(world.TryGet(cell, out var state), Is.True); return state;
        }

        [TestCase(.29937440156936646f)]
        [TestCase(.29973262548446655f)]
        public void FloorToleranceSharesTheActualSubMillimetreContactAcrossAnXSeam(float feet)
        {
            var world = World(); Add(world, Vector3Int.zero); Add(world, Vector3Int.right);
            var center = SeamCenter65(.29116711f, .15f, feet);
            Assert.That(SeamOverlap65(world, center, out _, 0), Is.True,
                "Keep the original exact overlap; the production standing-only option supplies the tolerance.");
            Assert.That(SeamOverlap65(world, center, out _), Is.False,
                "The recorded ordinary-motor contact remains the same floor across an internal tile seam.");
        }

        [TestCase(0, -1)]
        [TestCase(1, -1)]
        [TestCase(1, 0)]
        public void FloorToleranceWorksAcrossNegativeAndZSeams(int axis, int start)
        {
            var world = World();
            var under = axis == 0 ? new Vector3Int(start, 0, 0) : new Vector3Int(0, 0, start);
            var adjacent = under + (axis == 0 ? Vector3Int.right : new Vector3Int(0, 0, 1));
            Add(world, under); Add(world, adjacent);
            float nearSeam = (start + 1) * BlockWorld.CellSize - .00883289f;
            var center = SeamCenter65(axis == 0 ? nearSeam : .15f, axis == 1 ? nearSeam : .15f);
            Assert.That(SeamOverlap65(world, center, out _, 0), Is.True);
            Assert.That(SeamOverlap65(world, center, out _), Is.False);
        }

        [TestCase(1, 1)]
        [TestCase(1, -1)]
        [TestCase(-1, 1)]
        [TestCase(-1, -1)]
        public void FloorToleranceSharesAFullyFilledFourTileCorner(int dx, int dz)
        {
            var world = World(); Add(world, Vector3Int.zero);
            Add(world, new Vector3Int(dx, 0, 0)); Add(world, new Vector3Int(0, 0, dz));
            Add(world, new Vector3Int(dx, 0, dz));
            float x = dx > 0 ? BlockWorld.CellSize - .00883289f : .00883289f;
            float z = dz > 0 ? BlockWorld.CellSize - .00883289f : .00883289f;
            var center = SeamCenter65(x, z);
            Assert.That(SeamOverlap65(world, center, out _, 0), Is.True);
            Assert.That(SeamOverlap65(world, center, out _), Is.False,
                "Both orthogonal same-height bridges really exist; the diagonal is an internal floor corner.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void FloorToleranceDoesNotBridgeADiagonalOrAnLShapedHole(bool bridgeX, bool bridgeZ)
        {
            var world = World(); Add(world, Vector3Int.zero);
            if (bridgeX) Add(world, Vector3Int.right);
            if (bridgeZ) Add(world, new Vector3Int(0, 0, 1));
            var diagonal = Add(world, new Vector3Int(1, 0, 1));
            var center = SeamCenter65(.29116711f, .29116711f);
            Assert.That(SeamOverlap65(world, center, out var hit), Is.True,
                "A diagonal tolerance extension requires BOTH orthogonal filled coplanar bridges.");
            Assert.That(hit.State.Handle, Is.EqualTo(diagonal.Handle),
                "The remaining exact diagonal edge must be the reported obstruction.");
        }

        [Test] public void FloorToleranceDoesNotInventSupportOverAnEmptyColumnOrOuterEdge()
        {
            var world = World(); var edge = Add(world, Vector3Int.right);
            var center = SeamCenter65(.29116711f, .15f);
            Assert.That(world.TryGet(Vector3Int.zero, out _), Is.False);
            Assert.That(SeamOverlap65(world, center, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(edge.Handle));
            Assert.That(hit.Normal.x, Is.LessThan(0), "Retain the exposed rounded edge instead of extending a nonexistent floor.");
        }

        [TestCase(.0009f, false)]
        [TestCase(.0011f, true)]
        [TestCase(.002f, true)]
        public void FloorToleranceUsesVerticalDepthAndNeverADeeperRoundedRadiusAllowance(float penetration, bool expected)
        {
            var world = World(); var support = Add(world, Vector3Int.zero); Add(world, Vector3Int.right);
            float top = BlockFixedGridWorld.GetBounds(support).max.y;
            var center = new Vector3(.29116711f, (float)((double)top + SeamHalfSegment65 + SeamRadius65 - penetration), .15f);
            double actualDepth = top - ((double)center.y - SeamHalfSegment65 - SeamRadius65);
            Assert.That(actualDepth > SeamFloorTolerance65, Is.EqualTo(penetration > SeamFloorTolerance65));
            Assert.That(SeamOverlap65(world, center, out _), Is.EqualTo(expected));
        }

        [Test] public void FloorToleranceKeepsTheNearestRepresentablePositionsOnBothSidesOfOneMillimetreDistinct()
        {
            var world = World(); var support = Add(world, Vector3Int.zero); Add(world, Vector3Int.right);
            double top = BlockFixedGridWorld.GetBounds(support).max.y;
            float threshold = (float)(top + SeamHalfSegment65 + SeamRadius65 - SeamFloorTolerance65);
            int bits = BitConverter.SingleToInt32Bits(threshold);
            float above = BitConverter.Int32BitsToSingle(bits + 1), below = BitConverter.Int32BitsToSingle(bits - 1);
            double shallowDepth = top - ((double)above - SeamHalfSegment65 - SeamRadius65);
            double deepDepth = top - ((double)below - SeamHalfSegment65 - SeamRadius65);
            Assert.That(shallowDepth, Is.LessThan(SeamFloorTolerance65));
            Assert.That(deepDepth, Is.GreaterThan(SeamFloorTolerance65));
            Assert.That(SeamOverlap65(world, new Vector3(.29116711f, above, .15f), out _), Is.False);
            Assert.That(SeamOverlap65(world, new Vector3(.29116711f, below, .15f), out _), Is.True,
                "No extra contact epsilon or radius shrink may extend the existing one-millimetre vertical budget.");
        }

        [TestCase(-.0002f, false)]
        [TestCase(.0002f, true)]
        public void FloorTolerancePreservesTheExactRoundedEdgeOfTheNextHigherCell(float horizontalIntrusion, bool expected)
        {
            var world = World(); var floor = Add(world, Vector3Int.zero);
            Add(world, new Vector3Int(2, 0, 0)); var upper = Add(world, new Vector3Int(2, 1, 0));
            Bounds box = BlockFixedGridWorld.GetBounds(upper);
            var center = SeamCenter65(0, .15f);
            double dy = center.y - ((double)box.max.y + SeamHalfSegment65);
            Assert.That(dy, Is.GreaterThan(0).And.LessThan(SeamRadius65));
            double exactHorizontalReach = Math.Sqrt((double)SeamRadius65 * SeamRadius65 - dy * dy);
            center.x = (float)(box.min.x - exactHorizontalReach + horizontalIntrusion);
            Assert.That(BlockWorld.CellAt(center).x, Is.Zero, "The existing lower floor really lies below the center.");
            Assert.That(SeamOverlap65(world, center, out var hit), Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(hit.State.Handle, Is.EqualTo(upper.Handle));
                Assert.That(hit.Normal.x, Is.LessThan(0)); Assert.That(hit.Normal.y, Is.GreaterThan(0));
            }
            Assert.That(world.TryGet(floor.Handle, out _), Is.True);
        }

        [TestCase(0, -.0002f, false)]
        [TestCase(0, .0002f, true)]
        [TestCase(1, -.0002f, false)]
        [TestCase(1, .0002f, true)]
        public void FloorToleranceKeepsIndependentWallAndCeilingQueriesExact(int ceiling, float intrusion, bool expected)
        {
            var world = World(); var block = Add(world, Vector3Int.zero);
            const float radius = .1f;
            Vector3 center = ceiling == 0 ? new Vector3(-radius + intrusion, .15f, .15f)
                : new Vector3(.15f, -radius + intrusion, .15f);
            bool actual = BlockFixedGridQueries.OverlapCapsule(world, center, 0, radius, Vector3.up,
                out var hit, floorContactTolerance: SeamFloorTolerance65);
            Assert.That(actual, Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(hit.State.Handle, Is.EqualTo(block.Handle));
                Assert.That(ceiling == 0 ? hit.Normal.x : hit.Normal.y, Is.LessThan(-.999f));
            }
        }

        [Test] public void ToleratedFloorNeverShortCircuitsASimultaneousHigherObstruction()
        {
            var world = World(); Add(world, Vector3Int.zero);
            Add(world, Vector3Int.right); var upper = Add(world, new Vector3Int(1, 1, 0));
            var center = SeamCenter65(.29116711f, .15f);
            Assert.That(SeamOverlap65(world, center, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(upper.Handle),
                "Skipping a tolerated floor candidate must continue checking every other cell.");
        }

        [TestCase(1, 2)]
        [TestCase(2, 1)]
        public void CoplanarSupportDoesNotDependOnTheTwoBlocksHavingTheSameTeam(int underOwner, int adjacentOwner)
        {
            var world = World(); AddOwner65(world, Vector3Int.zero, (byte)underOwner);
            AddOwner65(world, Vector3Int.right, (byte)adjacentOwner);
            Assert.That(SeamOverlap65(world, SeamCenter65(.29116711f, .15f), out _), Is.False);
        }

        [Test] public void DefaultAndExplicitZeroFloorToleranceKeepTheExactOriginalHit()
        {
            var world = World(); Add(world, Vector3Int.zero); Add(world, Vector3Int.right);
            var center = SeamCenter65(.29116711f, .15f);
            uint revision = world.Revision;
            bool original = BlockFixedGridQueries.OverlapCapsule(world, center, SeamHalfSegment65, SeamRadius65, Vector3.up, out var a);
            bool zero = SeamOverlap65(world, center, out var b, 0);
            Assert.That(original && zero, Is.True); Assert.That(a.State.Handle, Is.EqualTo(b.State.Handle));
            Assert.That(a.Point, Is.EqualTo(b.Point)); Assert.That(a.Normal, Is.EqualTo(b.Normal));
            Assert.That(a.Distance, Is.EqualTo(b.Distance)); Assert.That(world.Revision, Is.EqualTo(revision));
        }

        [Test] public void NonFiniteQueryInputsCannotEnterGridTraversal()
        {
            var world = World(); Add(world, Vector3Int.zero);
            Assert.That(BlockFixedGridQueries.Cast(world, new Vector3(float.NaN, 0, 0), Vector3.forward, 2, 0, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Cast(world, Vector3.zero, Vector3.forward, float.PositiveInfinity, 0, out _), Is.False);
            Assert.That(BlockFixedGridQueries.CastCapsule(world, Vector3.zero, -1, .1f, Vector3.forward, 2, out _), Is.False);
        }
    }
}
