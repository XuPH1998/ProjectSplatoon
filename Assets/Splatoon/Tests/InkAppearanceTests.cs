using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using Splatoon.Painting;
using UnityEngine.Experimental.Rendering;

namespace Splatoon.Tests
{
    public sealed class InkAppearanceTests
    {
        [Test] public void VisualStorageUsesTwoChannelsWithExplicitFallback()
        {
            Assert.That(PaintTextureMemory.SelectVisualFormat(_ => true), Is.EqualTo(GraphicsFormat.R8G8_UNorm));
            Assert.That(PaintTextureMemory.SelectVisualFormat(f => f == GraphicsFormat.R8G8B8A8_UNorm), Is.EqualTo(GraphicsFormat.R8G8B8A8_UNorm));
            Assert.Throws<NotSupportedException>(() => PaintTextureMemory.SelectVisualFormat(_ => false));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, InkAppearanceProfile.PackVisual(new byte[] { 1, 2, 0, 255, 3, 4, 255, 0 }));
        }
        [Test] public void EncoderRejectsMissingWrongAndUnpairedVisualData()
        {
            var state = new PaintCheckpoint { Topology = "paired" }; state.Surfaces[7] = new byte[4];
            Assert.Throws<InvalidDataException>(() => PaintSnapshotCodec.Encode(state));
            state.VisualSurfaces[8] = new byte[2]; Assert.Throws<InvalidDataException>(() => PaintSnapshotCodec.Encode(state));
            state.VisualSurfaces.Clear(); state.VisualSurfaces[7] = new byte[4]; Assert.Throws<InvalidDataException>(() => PaintSnapshotCodec.Encode(state));
            Assert.Throws<InvalidOperationException>(() => PaintSurface.ValidatePair(new byte[4], null, 4));
        }
        [TestCase(5, false)] [TestCase(6, false)] [TestCase(6, true)]
        public void DecoderRejectsOldMissingAndTruncatedVisualPlanes(int version, bool truncated)
        {
            using var output = new MemoryStream();
            using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true))
            using (var w = new BinaryWriter(zip))
            {
                w.Write(version); w.Write(1u); w.Write(12u); w.Write("paired");
                w.Write(0); w.Write(1); w.Write(7); w.Write(4); w.Write(new byte[] { 255, 0, 1, 255 });
                w.Write(truncated ? 1 : 0);
                if (truncated) { w.Write(7); w.Write(2); w.Write((byte)128); }
            }
            if (truncated) Assert.Throws<EndOfStreamException>(() => PaintSnapshotCodec.Decode(output.ToArray(), "paired", new Dictionary<int, int>(), new Dictionary<int, int> { [7] = 4 }));
            else Assert.Throws<InvalidDataException>(() => PaintSnapshotCodec.Decode(output.ToArray(), "paired", new Dictionary<int, int>(), new Dictionary<int, int> { [7] = 4 }));
        }
    }
}
