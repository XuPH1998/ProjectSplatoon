using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Splatoon.Painting;

namespace Splatoon.Tests
{
    public sealed class InkEdgeTests
    {
        [Test]
        public void IslandFormatPrefersR8AndNeverFallsBackToFourChannels()
        {
            Assert.That(PaintTextureMemory.SelectIslandFormat(_ => true), Is.EqualTo(GraphicsFormat.R8_UNorm));
            Assert.That(PaintTextureMemory.SelectIslandFormat(f => f != GraphicsFormat.R8_UNorm), Is.EqualTo(GraphicsFormat.R16_UNorm));
            Assert.That(PaintTextureMemory.SelectIslandFormat(f => f == GraphicsFormat.R16_SFloat), Is.EqualTo(GraphicsFormat.R16_SFloat));
            Assert.Throws<NotSupportedException>(() => PaintTextureMemory.SelectIslandFormat(_ => false));
        }

        [Test]
        public void BudgetIncludesCheckpointAndSharesScratchOnlyForEqualSizes()
        {
            var objects = new[] { new GameObject("A"), new GameObject("B"), new GameObject("C") };
            try
            {
                var surfaces = new PaintSurface[3];
                for (int i = 0; i < 3; i++)
                {
                    surfaces[i] = objects[i].AddComponent<PaintSurface>();
                    surfaces[i].Resolution = 32; surfaces[i].ResolutionHeight = i == 2 ? 64 : 32;
                }
                Assert.That(PaintTextureMemory.PeakBytes(surfaces, 1), Is.EqualTo(32L * 32 * (4 * 17 + 3 * 6)));
                Assert.That(PaintTextureMemory.PeakBytes(surfaces, 2), Is.EqualTo(32L * 32 * (4 * 18 + 3 * 6)));
                Assert.That(surfaces[2].TextureBytes, Is.EqualTo(32 * 64 * 4));
            }
            finally { foreach (var go in objects) UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
