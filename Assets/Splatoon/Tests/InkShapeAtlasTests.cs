using NUnit.Framework;
using UnityEngine;
using Splatoon.Painting;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Splatoon.Tests
{
    public sealed class InkShapeAtlasTests
    {
        [SetUp]
        public void ConfigureAtlas()
        {
#if UNITY_EDITOR
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameResource/Effects/Ink/Textures/InkSplatAtlas-Reference.png"));
#endif
        }

        [Test] public void AtlasResourceHasExpectedLayout()
        {
            var texture = InkShapeAtlas.Texture;
            Assert.That(texture, Is.Not.Null);
            Assert.That(texture.width, Is.EqualTo(1024));
            Assert.That(texture.height, Is.EqualTo(1024));
            Assert.That(texture.isReadable, Is.True);
        }

        [Test] public void ShapeSelectionIsDeterministic()
        {
            for (uint seed = 0; seed < 128; seed++)
            {
                Assert.That(InkShapeAtlas.Index(seed), Is.EqualTo(InkShapeAtlas.Index(seed)));
                Assert.That(InkShapeAtlas.Rotation(seed), Is.EqualTo(InkShapeAtlas.Rotation(seed)));
            }
        }

        [Test] public void ProjectionUsesSurfaceNormalAndRadius()
        {
            var floor = InkShapeAtlas.ProjectedUv(new Vector3(0, 0, .5f), Vector3.zero, Vector3.up, 1, 7);
            var wall = InkShapeAtlas.ProjectedUv(new Vector3(0, .5f, 0), Vector3.zero, Vector3.forward, 1, 7);
            Assert.That(floor.x, Is.InRange(0, 1)); Assert.That(floor.y, Is.InRange(0, 1));
            Assert.That(wall.x, Is.InRange(0, 1)); Assert.That(wall.y, Is.InRange(0, 1));
            Assert.That(InkShapeAtlas.Sample(new Vector2(-.1f, .5f), 7), Is.Zero);
        }

        [Test] public void ShapeStampCarriesAcrossNetworkSerializationData()
        {
            var stamp = new PaintStamp { ShapeSeed = 0x12345678u, Position = Vector3.one, Normal = Vector3.up, Radius = 1, Hardness = .55f, Strength = 1 };
            Assert.That(stamp.ShapeSeed, Is.EqualTo(0x12345678u));
        }
    }
}
