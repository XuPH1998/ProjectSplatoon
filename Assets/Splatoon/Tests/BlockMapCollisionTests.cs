#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    /// <summary>Geometry oracles independent of the block solver and its scheduling.</summary>
    [Category("BlockSimulationPrototype")]
    public sealed class BlockMapCollisionTests
    {
        readonly List<UnityEngine.Object> _owned = new();
        static readonly float3 Half = new(.15f);
        static readonly int[] ClosedBoxTriangles =
        { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 };

        [TearDown]
        public void ReleaseFixtures()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) if (_owned[i] != null) UnityEngine.Object.DestroyImmediate(_owned[i]);
            _owned.Clear();
        }

        T Own<T>(T value) where T : UnityEngine.Object { _owned.Add(value); return value; }
        GameObject Object(string name) => Own(new GameObject("Block map query fixture " + name));
        static void AssertNormal(float3 actual, float3 expected, float tolerance = .0001f)
            => Assert.That(math.distance(actual, expected), Is.LessThan(tolerance));

        [TestCase(1f)]
        [TestCase(-1f)]
        public void FiniteTriangleBlocksBothSidesWithTheExactPlaneTime(float side)
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddTriangle(new float3(-2, 0, -2), new float3(2, 0, -2), new float3(0, 0, 2), 7);
            using var data = builder.Build();
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(0, side * 2, 0), Half,
                new float3(0, -side * 4, 0), out var hit), Is.True);
            Assert.That(hit.FeatureId, Is.EqualTo(7));
            Assert.That(hit.Fraction, Is.EqualTo((2 - Half.y) / 4).Within(.00001f));
            AssertNormal(hit.Normal, new float3(0, side, 0));
            Assert.That(math.abs(hit.Point.y), Is.LessThan(.00001f));
        }

        [Test]
        public void FiniteTriangleDoesNotFillItsBoundsOrItsInfinitePlane()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddTriangle(new float3(0, 0, 0), new float3(4, 0, 0), new float3(0, 0, 4));
            using var data = builder.Build();
            // Inside the triangle's AABB but well beyond its diagonal edge.
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(3.5f, 2, 3.5f), Half,
                new float3(0, -4, 0), out _), Is.False);
            Assert.That(BlockMapQueries.Overlap(data.Snapshot, new float3(3.5f, 0, 3.5f), Half, out _), Is.False);
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(-1, 2, 1), Half,
                new float3(0, -4, 0), out _), Is.False);
        }

        [Test]
        public void CoplanarOpenQuadHasNoSideBarrierAcrossItsInternalDiagonal()
        {
            using var data = FlatQuad().Build();
            foreach (float height in new[] { .15f, .149f, .151f })
                foreach (float z in new[] { -.7f, 0, .7f })
                    Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(-1.5f, height, z), Half,
                        new float3(3, 0, 0), out _), Is.False,
                        $"An internal diagonal cannot stop tangent movement at y={height}, z={z}.");
        }

        [Test]
        public void CoplanarContactCollectionKeepsOnePlaneAndAppendsWithoutClearing()
        {
            using var data = FlatQuad().Build();
            using var contacts = new NativeList<BlockStaticHit>(Allocator.Temp);
            contacts.Add(new BlockStaticHit { FeatureId = 900 });
            BlockMapQueries.CollectContacts(data.Snapshot, new float3(0, .151f, 0), Half, contacts, .002f);
            Assert.That(contacts[0].FeatureId, Is.EqualTo(900));
            Assert.That(contacts.Length, Is.EqualTo(2), "The two coplanar triangles represent one translation constraint.");
            Assert.That(contacts[1].Penetration, Is.EqualTo(-.001f).Within(.00001f));
            AssertNormal(contacts[1].Normal, new float3(0, 1, 0));
        }

        [Test]
        public void TrainingGroundAuthoredRampKeepsItsTwentySixDegreePlaneAcrossTheSeam()
        {
            var scene = EditorSceneManager.OpenPreviewScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity");
            try
            {
                MeshCollider ramp = null;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var candidate in root.GetComponentsInChildren<MeshCollider>(true))
                        if (candidate.name == "Ramp_1_1" && !candidate.convex) ramp = candidate;
                Assert.That(ramp, Is.Not.Null, "The oracle must use the current authored ramp, not a replacement plane.");
                var vertices = ramp.sharedMesh.vertices; var indices = ramp.sharedMesh.triangles;
                Assert.That(indices.Length, Is.EqualTo(6));
                float3 a = ramp.transform.TransformPoint(vertices[indices[0]]), b = ramp.transform.TransformPoint(vertices[indices[1]]),
                    c = ramp.transform.TransformPoint(vertices[indices[2]]);
                float3 normal = math.normalize(math.cross(b - a, c - a)); if (normal.y < 0) normal = -normal;
                Assert.That(math.degrees(math.acos(normal.y)), Is.EqualTo(math.degrees(math.atan(.5f))).Within(.001f));
                float3 center = 0; foreach (var vertex in vertices) center += (float3)ramp.transform.TransformPoint(vertex); center /= vertices.Length;
                float3 tangent = math.normalizesafe(math.cross(normal, new float3(0, 0, 1)));
                if (math.lengthsq(tangent) < .5f) tangent = math.normalize(math.cross(normal, new float3(1, 0, 0)));
                float radius = math.dot(Half, math.abs(normal));
                using var data = BlockMapCollisionCapture.Capture(new Collider[] { ramp });
                for (int sample = -3; sample <= 3; sample++)
                {
                    float3 point = center + tangent * (sample * .35f);
                    Assert.That(BlockMapQueries.Sweep(data.Snapshot, point + normal * 2, Half, -normal * 4, out var hit), Is.True);
                    AssertNormal(hit.Normal, normal);
                    Assert.That(hit.Fraction, Is.EqualTo((2 - radius) / 4).Within(.0001f));
                }
                float3 start = center - tangent * 1.2f + normal * radius;
                Assert.That(BlockMapQueries.Sweep(data.Snapshot, start, Half, tangent * 2.4f, out _), Is.False,
                    "Continuous tangent travel across the authored quad's diagonal cannot create a side wall.");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void ClosedConvexSupportsOutsideAndSeparatesAnInsideBox()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddConvex(CubeVertices(new float3(0), new float3(1)), ClosedBoxTriangles, 23);
            using var data = builder.Build();
            Assert.That(BlockMapQueries.Overlap(data.Snapshot, new float3(0), Half, out var inside), Is.True);
            Assert.That(inside.Penetration, Is.EqualTo(1.15f).Within(.0001f));
            float3 escaped = inside.Normal * (inside.Penetration + .001f);
            Assert.That(BlockMapQueries.Overlap(data.Snapshot, escaped, Half, out _), Is.False);
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(3, 0, 0), Half, new float3(-4, 0, 0), out var hit), Is.True);
            Assert.That(hit.Fraction, Is.EqualTo((3 - 1 - .15f) / 4).Within(.00001f));
            AssertNormal(hit.Normal, new float3(1, 0, 0));
            Assert.That(BlockMapQueries.Overlap(data.Snapshot, new float3(2), Half, out _), Is.False);
        }

        [Test]
        public void ConvexRampSolidHasAnInteriorAndDoesNotOccupySpaceAboveItsSlope()
        {
            float3[] vertices = { new(-2.5f,0,-3),new(2.5f,0,-3),new(-2.5f,0,3),new(2.5f,0,3),new(-2.5f,3,3),new(2.5f,3,3) };
            int[] triangles = { 0,4,2,0,2,1,1,2,3,1,3,5,2,4,5,2,5,3,0,1,5,0,5,4 };
            var builder = new BlockMapCollisionBuilder(); builder.AddConvex(vertices, triangles);
            using var data = builder.Build();
            Assert.That(BlockMapQueries.Overlap(data.Snapshot, new float3(0, .5f, 0), new float3(.05f), out var inside), Is.True);
            Assert.That(inside.Penetration, Is.GreaterThan(0));
            Assert.That(BlockMapQueries.Overlap(data.Snapshot, new float3(0, 2, 0), Half, out _), Is.False);
        }

        [Test]
        public void BridgeUndersideSweepStaysBelowTheBridgeWhileItsGapRemainsEmpty()
        {
            var builder = new BlockMapCollisionBuilder(); builder.AddBox(new float3(0, 3, 0), new float3(3, .2f, 2));
            using var data = builder.Build();
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(0, 1, 0), Half, new float3(0, 4, 0), out var hit), Is.True);
            Assert.That(hit.Fraction, Is.EqualTo((2.8f - 1 - .15f) / 4).Within(.00001f));
            AssertNormal(hit.Normal, new float3(0, -1, 0));
            Assert.That(hit.Point.y, Is.EqualTo(2.8f).Within(.0001f));
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(-5, 1, 0), Half, new float3(10, 0, 0), out _), Is.False);
        }

        [Test]
        public void EscapingInitialOverlapStillStopsAtASecondThinWall()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0), new float3(.1f, 2, 2), 1);
            builder.AddBox(new float3(1, 0, 0), new float3(.004f, 2, 2), 2);
            using var data = builder.Build();
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(.2f, 0, 0), Half, new float3(2, 0, 0), out var hit), Is.True);
            Assert.That(hit.FeatureId, Is.EqualTo(2));
            Assert.That(hit.Fraction, Is.EqualTo((1 - .004f - .15f - .2f) / 2).Within(.00001f));
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(.2f, 0, 0), Half, new float3(-1, 0, 0), out var inward), Is.True);
            Assert.That(inward.FeatureId, Is.EqualTo(1)); Assert.That(inward.Fraction, Is.Zero);
        }

        [Test]
        public void RotatedAndScaledBoxCaptureUsesItsRealOrientedFaces()
        {
            var go = Object("rotated box"); go.transform.SetPositionAndRotation(new Vector3(3, 2, -1), Quaternion.Euler(0, 37, 0));
            go.transform.localScale = new Vector3(1.5f, 1, .8f);
            var box = go.AddComponent<BoxCollider>(); box.size = new Vector3(2, 2, .4f); box.center = new Vector3(.2f, 0, 0);
            using var data = BlockMapCollisionCapture.Capture(new Collider[] { box });
            Assert.That(data.Snapshot.Features[0].Kind, Is.EqualTo(BlockMapFeatureKind.Convex));
            float3 normal = go.transform.forward, face = go.transform.TransformPoint(box.center + Vector3.forward * .2f);
            float radius = math.dot(Half, math.abs(normal));
            Assert.That(BlockMapQueries.Sweep(data.Snapshot, face + normal * 2, Half, -normal * 4, out var hit), Is.True);
            AssertNormal(hit.Normal, normal);
            Assert.That(hit.Fraction, Is.EqualTo((2 - radius) / 4).Within(.0001f));
            Assert.That(math.abs(math.dot(hit.Point - face, normal)), Is.LessThan(.0001f));
        }

        [Test]
        public void UnsupportedSolidAndShearedBoxAreRejectedInsteadOfOmitted()
        {
            var sphere = Object("unsupported sphere").AddComponent<SphereCollider>();
            Assert.Throws<NotSupportedException>(() => { using var data = BlockMapCollisionCapture.Capture(new Collider[] { sphere }); });
            var parent = Object("nonuniform parent"); parent.transform.localScale = new Vector3(2, 1, 1);
            var child = Object("rotated child"); child.transform.SetParent(parent.transform, false); child.transform.localRotation = Quaternion.Euler(0, 45, 0);
            var box = child.AddComponent<BoxCollider>();
            Assert.Throws<NotSupportedException>(() => { using var data = BlockMapCollisionCapture.Capture(new Collider[] { box }); });
        }

        [Test]
        public void GeometrySignatureChangesWithTransformMaterialAndFilteringState()
        {
            var go = Object("signature box"); var box = go.AddComponent<BoxCollider>();
            string initial = CaptureHash(box);
            Assert.That(CaptureHash(box), Is.EqualTo(initial));
            go.transform.position = new Vector3(.1f, 0, 0); string moved = CaptureHash(box);
            Assert.That(moved, Is.Not.EqualTo(initial));
            var material = Own(new PhysicsMaterial("query test material") { staticFriction = .25f, dynamicFriction = .15f });
            box.sharedMaterial = material; string friction = CaptureHash(box); Assert.That(friction, Is.Not.EqualTo(moved));
            using (var data = BlockMapCollisionCapture.Capture(new Collider[] { box }))
            {
                Assert.That(BlockMapQueries.Sweep(data.Snapshot, new float3(3, 0, 0), Half, new float3(-4, 0, 0), out var hit), Is.True);
                Assert.That(hit.StaticFriction, Is.EqualTo(.25f)); Assert.That(hit.DynamicFriction, Is.EqualTo(.15f));
            }
            box.isTrigger = true;
            using (var data = BlockMapCollisionCapture.Capture(new Collider[] { box }))
            { Assert.That(data.GeometryHash, Is.Not.EqualTo(friction)); Assert.That(data.Snapshot.FeatureCount, Is.Zero); }
            box.isTrigger = false; box.enabled = false; string disabled = CaptureHash(box);
            Assert.That(disabled, Is.Not.EqualTo(friction));
            box.enabled = true; go.SetActive(false); Assert.That(CaptureHash(box), Is.Not.EqualTo(friction));
        }

        [Test]
        public void GeometrySignatureIncludesTriangleIndicesEvenWithIdenticalVertices()
        {
            var mesh = Own(new Mesh { name = "Map topology hash oracle" });
            mesh.vertices = new[] { new Vector3(-1,0,-1),new Vector3(1,0,-1),new Vector3(1,0,1),new Vector3(-1,0,1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            var collider = Object("signature mesh").AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
            string original = CaptureHash(collider);
            mesh.triangles = new[] { 0, 3, 1, 1, 3, 2 };
            Assert.That(CaptureHash(collider), Is.Not.EqualTo(original));
        }

        static string CaptureHash(Collider collider)
        { using var data = BlockMapCollisionCapture.Capture(new Collider[] { collider }); return data.GeometryHash; }
        static BlockMapCollisionBuilder FlatQuad()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddTriangle(new float3(-3,0,-3),new float3(3,0,3),new float3(3,0,-3), 1);
            builder.AddTriangle(new float3(-3,0,-3),new float3(-3,0,3),new float3(3,0,3), 2);
            return builder;
        }
        static float3[] CubeVertices(float3 center, float3 half)
            => new[] { center+new float3(-half.x,-half.y,-half.z),center+new float3(half.x,-half.y,-half.z),
                center+new float3(half.x,half.y,-half.z),center+new float3(-half.x,half.y,-half.z),
                center+new float3(-half.x,-half.y,half.z),center+new float3(half.x,-half.y,half.z),
                center+new float3(half.x,half.y,half.z),center+new float3(-half.x,half.y,half.z) };
    }
}
#endif
