using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    [Category("BlockSimulationPrototype")]
    public sealed class BlockGeometryArchiveTests
    {
        [Test]
        public void BakedGeometryRoundTripRetainsIdentityAndExactQueryResults()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0, -.1f, 0), new float3(4, .1f, 4), 1);
            builder.AddBox(new float3(2, 1, 0), new float3(.004f, 1, 2), 2);
            builder.AddTriangle(new float3(-2, 0, -2), new float3(-2, 0, 2), new float3(2, 1, 2), 3);
            using var original = builder.Build();
            byte[] bytes = BlockGeometryArchive.Encode(original);
            using var loaded = BlockGeometryArchive.Decode(bytes);
            Assert.That(loaded.GeometryHash, Is.EqualTo(original.GeometryHash));
            CollectionAssert.AreEqual(bytes, BlockGeometryArchive.Encode(loaded), "Archive encoding must be deterministic.");
            var random = new Unity.Mathematics.Random(1792294142);
            for (int i = 0; i < 300; i++)
            {
                float3 start = random.NextFloat3(new float3(-4, .5f, -4), new float3(4, 3, 4));
                float3 displacement = random.NextFloat3(new float3(-6), new float3(6));
                bool a = BlockMapQueries.Sweep(original.Snapshot, start, new float3(.15f), displacement, out var first);
                bool b = BlockMapQueries.Sweep(loaded.Snapshot, start, new float3(.15f), displacement, out var second);
                Assert.That(b, Is.EqualTo(a));
                if (!a) continue;
                Assert.That(second.FeatureId, Is.EqualTo(first.FeatureId));
                Assert.That(second.Fraction, Is.EqualTo(first.Fraction));
                Assert.That(second.Normal, Is.EqualTo(first.Normal));
                Assert.That(second.Point, Is.EqualTo(first.Point));
            }
        }

        [Test]
        public void EmptyGeometryRoundTripRemainsAValidEmptyWorld()
        {
            using var original = new BlockMapCollisionBuilder().Build();
            using var loaded = BlockGeometryArchive.Decode(BlockGeometryArchive.Encode(original));
            Assert.That(loaded.Snapshot.IsCreated, Is.True);
            Assert.That(loaded.Snapshot.FeatureCount, Is.Zero);
            Assert.That(BlockMapQueries.Sweep(loaded.Snapshot, 0, new float3(.15f), new float3(0, -1, 0), out _), Is.False);
        }

        [Test]
        public void CorruptionTruncationAndOldVersionsAreRejectedBeforePublishingGeometry()
        {
            var builder = new BlockMapCollisionBuilder(); builder.AddBox(0, new float3(1));
            using var original = builder.Build();
            byte[] valid = BlockGeometryArchive.Encode(original);
            var truncated = new byte[valid.Length - 1]; Array.Copy(valid, truncated, truncated.Length);
            Assert.Throws<InvalidDataException>(() => BlockGeometryArchive.Decode(truncated));
            foreach (int offset in new[] { 0, 8, 12, 16, 40, 80, valid.Length - 1 })
            {
                byte[] corrupted = (byte[])valid.Clone(); corrupted[offset] ^= 0x40;
                Assert.Throws<InvalidDataException>(() => BlockGeometryArchive.Decode(corrupted), "Corruption offset " + offset);
            }
            byte[] trailing = new byte[valid.Length + 1]; Array.Copy(valid, trailing, valid.Length);
            Assert.Throws<InvalidDataException>(() => BlockGeometryArchive.Decode(trailing));
        }

        [Test]
        public void MixedClosedConvexAndCoplanarTrianglesRoundTripWithoutChangingTheBakeIdentity()
        {
            var builder = new BlockMapCollisionBuilder();
            AddConvexCube(builder);
            builder.AddTriangle(new float3(-3,0,-3), new float3(3,0,3), new float3(3,0,-3), 2);
            builder.AddTriangle(new float3(-3,0,-3), new float3(-3,0,3), new float3(3,0,3), 3);
            builder.AddSignatureData(new byte[] { 7, 23, 91, 0 });
            using var original = builder.Build();
            byte[] bytes = BlockGeometryArchive.Encode(original);
            using var loaded = BlockGeometryArchive.Decode(bytes);
            Assert.That(loaded.GeometryHash, Is.EqualTo(original.GeometryHash), "Load validation must not replace the captured source-metadata identity.");
            CollectionAssert.AreEqual(bytes, BlockGeometryArchive.Encode(loaded));
        }

        [TestCase("missing axis")]
        [TestCase("axis range")]
        [TestCase("nonunit axis")]
        [TestCase("wrong projection")]
        [TestCase("wrong edge flag")]
        [TestCase("wrong feature bounds")]
        public void CorrectChecksumDoesNotMakeMalformedBoxQueryDataValid(string mutation)
        {
            var builder = new BlockMapCollisionBuilder(); builder.AddBox(0, new float3(1));
            using var original = builder.Build();
            byte[] archive = BlockGeometryArchive.Encode(original); int axis = 112 + 109;
            switch (mutation)
            {
                case "missing axis": Put(archive, 112 + 9, 1); break;
                case "axis range": Put(archive, 112 + 5, Count(archive, 20)); break;
                case "nonunit axis": Put(archive, axis, 2f); break;
                case "wrong projection": Put(archive, axis + 12, -100f); break;
                case "wrong edge flag": archive[axis + 20] = 1; break;
                case "wrong feature bounds": Put(archive, 112 + 41, .5f); break;
            }
            RejectWithValidChecksum(archive);
        }

        [TestCase("zero normal")]
        [TestCase("degenerate triangle")]
        [TestCase("geometry outside bounds")]
        public void CorrectChecksumCannotHideInvalidFiniteTriangleGeometry(string mutation)
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddTriangle(new float3(-1,0,-1), new float3(1,0,-1), new float3(0,0,1));
            using var original = builder.Build(); byte[] archive = BlockGeometryArchive.Encode(original);
            switch (mutation)
            {
                case "zero normal": Array.Clear(archive, 112 + 89, 12); break;
                case "degenerate triangle":
                    Array.Copy(archive, 112 + 53, archive, 112 + 65, 12);
                    Array.Copy(archive, 112 + 53, archive, 112 + 77, 12); break;
                case "geometry outside bounds": Put(archive, 112 + 53, 100f); break;
            }
            RejectWithValidChecksum(archive);
        }

        [TestCase("empty convex")]
        [TestCase("invalid vertex index")]
        [TestCase("changed convex vertex")]
        public void CorrectChecksumCannotHideIncompleteOrInconsistentConvexData(string mutation)
        {
            var builder = new BlockMapCollisionBuilder(); AddConvexCube(builder);
            using var original = builder.Build(); byte[] archive = BlockGeometryArchive.Encode(original);
            int vertices = 112 + Count(archive, 16) * 109 + Count(archive, 20) * 21;
            int triangles = vertices + Count(archive, 24) * 12;
            switch (mutation)
            {
                case "empty convex": Put(archive, 112 + 17, 0); Put(archive, 112 + 25, 0); break;
                case "invalid vertex index": Put(archive, triangles, Count(archive, 24)); break;
                case "changed convex vertex": Put(archive, vertices, -10f); break;
            }
            RejectWithValidChecksum(archive);
        }

        [TestCase("cyclic root")]
        [TestCase("escaped child")]
        [TestCase("duplicate feature")]
        [TestCase("leaf range")]
        public void CorrectChecksumDoesNotBypassBvhStructureValidation(string mutation)
        {
            var builder = new BlockMapCollisionBuilder();
            for (int i = 0; i < 6; i++) builder.AddBox(new float3(i * 3, 0, 0), new float3(1), i + 1);
            using var original = builder.Build(); byte[] archive = BlockGeometryArchive.Encode(original);
            int nodes = 112 + Count(archive, 16) * 109 + Count(archive, 20) * 21 + Count(archive, 24) * 12 + Count(archive, 28) * 4;
            int indices = nodes + Count(archive, 32) * 40;
            switch (mutation)
            {
                case "cyclic root": Put(archive, nodes + 24, 0); break;
                case "escaped child": Put(archive, nodes + 40, -100f); break;
                case "duplicate feature": Put(archive, indices + 4, Count(archive, indices)); break;
                case "leaf range": Put(archive, nodes + 40 + 32, 6); break;
            }
            RejectWithValidChecksum(archive);
        }

        static void RejectWithValidChecksum(byte[] archive)
        {
            var hashInput = new byte[32 + archive.Length - 112];
            Array.Copy(archive, 40, hashInput, 0, 32); Array.Copy(archive, 112, hashInput, 32, archive.Length - 112);
            using var sha = SHA256.Create(); var digest = sha.ComputeHash(hashInput);
            Array.Copy(digest, 0, archive, 80, digest.Length);
            var error = Assert.Throws<InvalidDataException>(() => { using var invalid = BlockGeometryArchive.Decode(archive); });
            StringAssert.DoesNotContain("checksum", error.Message.ToLowerInvariant(), "The mutation must reach structural validation.");
        }

        static int Count(byte[] archive, int offset) => BitConverter.ToInt32(archive, offset);
        static void Put(byte[] archive, int offset, int value) => Array.Copy(BitConverter.GetBytes(value), 0, archive, offset, 4);
        static void Put(byte[] archive, int offset, float value) => Array.Copy(BitConverter.GetBytes(value), 0, archive, offset, 4);
        static void AddConvexCube(BlockMapCollisionBuilder builder)
        {
            float3[] vertices = { new(-1,-1,-1),new(1,-1,-1),new(1,1,-1),new(-1,1,-1),
                new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1) };
            int[] indices = { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 };
            builder.AddConvex(vertices, indices, 1);
        }
    }
}
