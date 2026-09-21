#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Splatoon.Painting;

namespace Splatoon.Tests
{
    public sealed class InkShapeAtlasTests
    {
        [SetUp] public void ConfigureAtlas() => InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
        [Test] public void AtlasHasPaddedCellsAndContentHashUsesActualAlpha()
        {
            var texture = InkShapeAtlas.Texture;
            Assert.That(texture.width, Is.EqualTo(2048)); Assert.That(texture.height, Is.EqualTo(1024));
            var alpha = texture.GetPixels32().Select(p => p.a).ToArray();
            Assert.That(InkShapeAtlas.ComputeContentHash(alpha), Is.EqualTo(InkShapeAtlas.ContentHash));
            alpha[514 * texture.width + 512] ^= 1;
            Assert.That(InkShapeAtlas.ComputeContentHash(alpha), Is.Not.EqualTo(InkShapeAtlas.ContentHash));
            for (uint i = 0; i < 32; i++)
            {
                Assert.That(InkShapeAtlas.Sample(new Vector2(-.01f, .5f), i), Is.Zero);
                Assert.That(InkShapeAtlas.Sample(new Vector2(1.01f, .5f), i), Is.Zero);
                for (int n = 0; n <= 16; n++)
                {
                    Assert.That(InkShapeAtlas.Sample(new Vector2(n / 16f, 0), i), Is.Zero);
                    Assert.That(InkShapeAtlas.Sample(new Vector2(n / 16f, 1), i), Is.Zero);
                    Assert.That(InkShapeAtlas.Sample(new Vector2(0, n / 16f), i), Is.Zero);
                    Assert.That(InkShapeAtlas.Sample(new Vector2(1, n / 16f), i), Is.Zero);
                }
            }
        }
        [Test] public void MissingOrChangedAtlasCannotSilentlyShareContentSignature()
        {
            var original = InkShapeAtlas.Texture;
            var changed = UnityEngine.Object.Instantiate(original);
            try
            {
                var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab").GetComponent<Splatoon.Prototype.PrototypePlayer>();
                var baseline = Splatoon.Networking.GameplayContentSignature.Compute(new byte[] { 1 }, "map", player);
                Assert.That(Splatoon.Networking.GameplayContentSignature.PaintProtocolVersion, Is.EqualTo(10));
                InkShapeAtlas.Reset(); Assert.Throws<InvalidOperationException>(() => { var hash = InkShapeAtlas.ContentHash; });
                Assert.Throws<InvalidOperationException>(() => InkShapeAtlas.Configure(null));
                var pixel = changed.GetPixel(128, 128); pixel.a = pixel.a > .5f ? .4f : .8f;
                changed.SetPixel(128, 128, pixel); changed.Apply(false);
                InkShapeAtlas.Configure(changed);
                CollectionAssert.AreNotEqual(baseline, Splatoon.Networking.GameplayContentSignature.Compute(new byte[] { 1 }, "map", player));
            }
            finally { InkShapeAtlas.Reset(); InkShapeAtlas.Configure(original); UnityEngine.Object.DestroyImmediate(changed); }
        }
        static uint Select(InkShapeSelector selector, uint n, ulong shooter = 5, uint round = 2)
            => selector.Select(shooter, round, 12345, 17, 0, n, n % 7 == 0);
        [Test] public void EveryBagIsPermutationAndFirstFourExcludePreviousLastFour()
        {
            var selector = new InkShapeSelector();
            var indices = Enumerable.Range(0, 32 * 80).Select(n => InkShapeAtlas.Index(Select(selector, (uint)n))).ToArray();
            for (int b = 0; b < 80; b++)
            {
                Assert.That(indices.Skip(b * 32).Take(32).OrderBy(x => x), Is.EqualTo(Enumerable.Range(0, 32)));
                if (b > 0) Assert.That(indices.Skip(b * 32).Take(4).Intersect(indices.Skip(b * 32 - 4).Take(4)), Is.Empty);
            }
        }
        [Test] public void ShooterStreamsAreIndependentReproducibleAndResetWithRounds()
        {
            var a = new InkShapeSelector(); var b = new InkShapeSelector();
            for (uint n = 0; n < 128; n++)
            { Select(a, n, 9); Assert.That(Select(a, n), Is.EqualTo(Select(b, n))); }
            a.Clear(); b.Clear(); Assert.That(Select(a, 0), Is.EqualTo(Select(b, 0)));
            for (uint n = 1; n < 16; n++) Select(a, n);
            b.Clear(); Assert.That(Select(a, 0, round: 3), Is.EqualTo(Select(b, 0, round: 3)));
        }
        [Test] public void EncodedSelectionRoundTripsThroughNetworkWithoutRerolling()
        {
            for (int index = 0; index < 32; index++)
            {
                uint seed = InkShapeAtlas.Pack(index, (16384u << 6) | 32u);
                Assert.That(InkShapeAtlas.Index(seed), Is.EqualTo(index)); Assert.That(InkShapeAtlas.Mirrored(seed), Is.True);
                Assert.That(InkShapeAtlas.Rotation(seed), Is.EqualTo(Mathf.PI / 2).Within(.00001));
                var stamp = new PaintStamp { ShapeSeed = seed, Radius = .65f, Position = Vector3.one, Normal = Vector3.up, Team = 1, Hardness = .55f, Strength = 1 };
                using var writer = new FastBufferWriter(256, Allocator.Temp); writer.WriteNetworkSerializable(stamp);
                using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PaintStamp received);
                Assert.That(received.ShapeSeed, Is.EqualTo(seed)); Assert.That(received.Radius, Is.EqualTo(stamp.Radius));
                Assert.That(InkShapeAtlas.Coverage(Vector3.one, received), Is.EqualTo(InkShapeAtlas.Coverage(Vector3.one, stamp)));
            }
        }
        [TestCase(.01f)] [TestCase(.55f)] [TestCase(1f)]
        public void HardnessPreservesOpaqueCoreAndStrength(float hardness)
        {
            Assert.That(InkShapeAtlas.RemapCoverage(1, hardness, .8f), Is.EqualTo(.8f).Within(.00001));
            Assert.That(InkShapeAtlas.RemapCoverage(0, hardness, 1), Is.Zero);
        }
        [Test] public void FloorAndWallProjectionPreserveScaledCoordinatesAndMirror()
        {
            var floor = InkShapeAtlas.ProjectedUv(new Vector3(.25f, 0, .5f), Vector3.zero, Vector3.up, 1, 0);
            var wall = InkShapeAtlas.ProjectedUv(new Vector3(-.5f, 1, 0), Vector3.zero, Vector3.forward, 2, 0);
            Assert.That(wall, Is.EqualTo(floor));
            var mirror = InkShapeAtlas.ProjectedUv(new Vector3(.25f, 0, .5f), Vector3.zero, Vector3.up, 1, 32);
            Assert.That(mirror.x, Is.EqualTo(1 - floor.x)); Assert.That(mirror.y, Is.EqualTo(floor.y));
        }
        [Test] public void RotatedGridCandidateBoundsMatchFullGridEnumeration()
        {
            foreach (var rotation in new[] { Quaternion.identity, Quaternion.Euler(-90, 0, 0) })
            {
                var matrix = Matrix4x4.TRS(new Vector3(13, 5, -21), rotation, Vector3.one);
                var grid = new SurfaceOwnershipGrid(new Vector2(4, 4), .125f, null);
                for (int index = 0; index < 32; index++)
                {
                    grid.Clear(); var stamp = new PaintStamp { Position = matrix.MultiplyPoint3x4(new Vector3(.2f, 0, -.1f)),
                        Normal = rotation * Vector3.up, Radius = 1.5f, Hardness = .55f, Strength = 1, Team = 1,
                        ShapeSeed = InkShapeAtlas.Pack(index, 8192u << 6) };
                    grid.Apply(stamp, matrix, .5f, .034424f, 110);
                    for (int i = 0; i < grid.Cells.Length; i++)
                    {
                        var expected = InkCoverage.Accumulate(default, 1, InkShapeAtlas.Coverage(matrix.MultiplyPoint3x4(grid.Center(i)), stamp));
                        Assert.That(grid.State[i * 4], Is.EqualTo(expected.r), $"tile={index}, cell={i}");
                    }
                }
            }
        }
    }
}
#endif
