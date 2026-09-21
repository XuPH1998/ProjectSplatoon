#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class InkSimulationTests
    {
        [Test] public void BallisticPositionIncludesRealFlightTimeAndDrop()
        {
            Vector3 p = InkBallistics.Position(new Vector3(0, 2, 0), Vector3.forward * 20, 19.62f, .5);
            Assert.That(p.z, Is.EqualTo(10).Within(.0001)); Assert.That(p.y, Is.EqualTo(-.4525).Within(.0001));
            Assert.That(InkBallistics.Position(Vector3.one, Vector3.forward, 19.62f, 0), Is.EqualTo(Vector3.one));
        }
        [Test] public void BallisticEquationIsIndependentOfVisualFrameRate()
        {
            var expected = InkBallistics.Position(Vector3.up, Vector3.forward * 22.5f, 19.62f, .5);
            foreach (int rate in new[] { 30, 60, 120, 144 })
            {
                double time = 0; while (time < .5) time = Math.Min(.5, time + 1.0 / rate);
                Assert.That(Vector3.Distance(InkBallistics.Position(Vector3.up, Vector3.forward * 22.5f, 19.62f, time), expected), Is.LessThan(.00001));
            }
        }
        [Test] public void BrushAndGridUseSameOwnershipThreshold()
        {
            float radius = InkBrush.OwnershipRadius(1.5f, .01f, 1, .5f);
            Assert.That(radius, Is.EqualTo(.7575).Within(.0001));
            Assert.That(InkBrush.Coverage(radius - .001f, 1.5f, .01f, 1), Is.GreaterThan(.5f));
            Assert.That(InkBrush.Coverage(radius + .001f, 1.5f, .01f, 1), Is.LessThan(.5f));
            var grid = new PaintGrid(256, .125f, p => p.x > 15);
            grid.Paint(Vector3.zero, radius, 1, null);
            Assert.That(grid.Pink, Is.GreaterThan(0));
            grid.Paint(Vector3.zero, radius, 2, null); Assert.That(grid.Pink, Is.Zero);
            Assert.That(InkBrush.OwnershipRadius(1, .1f, .2f, .5f), Is.Zero);
        }
        [Test] public void SnapshotRoundTripPreservesLargeGridAndEveryPaintedSurface()
        {
            var state = new PaintCheckpoint { Round = 3, Sequence = 521, Topology = "test-v4", Ownership = new System.Collections.Generic.Dictionary<int, byte[]> { [1] = new byte[65536 * 5] } };
            state.Ownership[1][65535] = 2; state.Ownership[1][0] = 255; state.Surfaces[7] = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 };
            state.VisualSurfaces[7] = new byte[] { 64, 128, 90, 180 };
            var encoded = PaintSnapshotCodec.Encode(state);
            var result = PaintSnapshotCodec.Decode(encoded, "test-v4", new Dictionary<int, int> { [1] = 65536 * 5 }, new Dictionary<int, int> { [7] = 8 });
            Assert.That(result.Round, Is.EqualTo(3)); Assert.That(result.Sequence, Is.EqualTo(521));
            CollectionAssert.AreEqual(state.VisualSurfaces[7], result.VisualSurfaces[7]); CollectionAssert.AreEqual(state.Ownership[1], result.Ownership[1]); CollectionAssert.AreEqual(state.Surfaces[7], result.Surfaces[7]);
        }
        [Test] public void SnapshotRejectsUnknownSurfacesAndDifferentTopology()
        {
            var state = new PaintCheckpoint { Topology = "test-v4", Ownership = new System.Collections.Generic.Dictionary<int, byte[]> { [1] = new byte[65536 * 5] } }; state.Surfaces[1] = new byte[4]; state.VisualSurfaces[1] = new byte[2]; var bytes = PaintSnapshotCodec.Encode(state);
            Assert.Throws<InvalidDataException>(() => PaintSnapshotCodec.Decode(bytes, "test-v4", new Dictionary<int, int> { [1] = 4096 }, new Dictionary<int, int> { [1] = 4 }));
            Assert.Throws<InvalidDataException>(() => PaintSnapshotCodec.Decode(bytes, "test-v4", new Dictionary<int, int> { [1] = 65536 * 5 }, new Dictionary<int, int> { [2] = 4 }));
        }
        [Test] public void SnapshotChecksumDetectsCorruptedChunk()
        {
            var bytes = new byte[] { 1, 2, 3, 4 }; uint expected = PaintSnapshotCodec.Hash(bytes); bytes[2] ^= 1;
            Assert.That(PaintSnapshotCodec.Hash(bytes), Is.Not.EqualTo(expected));
        }
        [Test] public void FullTankSupports108StandardShooterShots()
        {
            float ink = 100;
            for (int i = 0; i < 108; i++) Assert.That(PrototypeRules.Spend(ref ink, .92f), Is.True);
            Assert.That(ink, Is.EqualTo(.64).Within(.001));
            Assert.That(PrototypeRules.Spend(ref ink, .92f), Is.False); Assert.That(ink, Is.GreaterThanOrEqualTo(0));
        }
    }
}
#endif
