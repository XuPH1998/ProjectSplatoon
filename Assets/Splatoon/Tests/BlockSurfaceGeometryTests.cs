#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockSurfacePaint")]
    public sealed class BlockSurfaceGeometryTests
    {
        static Vector2[] Square(float size) => new[] { Vector2.zero, new Vector2(size, 0), new Vector2(size, size),
            Vector2.zero, new Vector2(size, size), new Vector2(0, size) };

        [Test] public void CompleteSquaresKeepExactAreaAndPartialBoundaryDoesNotBecomeAFullCell()
        {
            Assert.That(BlockSurfaceGeometry.FullCells(Square(.6f)), Has.Count.EqualTo(4));
            Assert.That(BlockSurfaceGeometry.FullCells(Square(.59f)), Has.Count.EqualTo(1));
            Assert.That(BlockSurfaceGeometry.FullCells(Square(.29f)), Is.Empty);
        }

        [Test] public void DuplicatedTrianglesCannotFillTheMissingHalfOfASquare()
        {
            var triangle = new[] { Vector2.zero, new Vector2(.3f, 0), new Vector2(.3f, .3f) };
            var repeated = new List<Vector2>(); repeated.AddRange(triangle); repeated.AddRange(triangle);
            Assert.That(BlockSurfaceGeometry.FullCells(repeated), Is.Empty);
        }

        [Test] public void HoleAndDisconnectedChartsAreNotPaintedAcrossEmptySpace()
        {
            var triangles = new List<Vector2>(); triangles.AddRange(Square(.3f));
            foreach (var point in Square(.3f)) triangles.Add(point + new Vector2(.6f, 0));
            CollectionAssert.AreEquivalent(new[] { new Vector2Int(0, 0), new Vector2Int(2, 0) }, BlockSurfaceGeometry.FullCells(triangles));
        }

        static MeshCollider Patch(GameObject root, List<Mesh> meshes, string name, int surfaceId,
            float minX, float maxX, float minZ, float maxZ, float y = 0, bool reverse = false)
        {
            var child = new GameObject(name); child.transform.SetParent(root.transform, false);
            child.AddComponent<PaintSurface>().SurfaceId = surfaceId;
            var mesh = new Mesh { name = name, vertices = new[] { new Vector3(minX, y, minZ),
                new Vector3(minX, y, maxZ), new Vector3(maxX, y, maxZ), new Vector3(maxX, y, minZ) },
                triangles = reverse ? new[] { 0,2,1, 0,3,2 } : new[] { 0,1,2, 0,2,3 } };
            mesh.RecalculateBounds(); meshes.Add(mesh);
            var collider = child.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; return collider;
        }

        [Test] public void CoplanarColliderHalvesCompleteOneSquareWithStableSharedOwnership()
        {
            var root = new GameObject("One square split between map tiles"); var meshes = new List<Mesh>();
            try
            {
                var left = Patch(root, meshes, "Left tile", 30, 0, .15f, -.3f, 0);
                var right = Patch(root, meshes, "Right tile", 31, .15f, .3f, -.3f, 0);
                var leftOnly = new BlockSurfaceWorld(1); var rightOnly = new BlockSurfaceWorld(1);
                var a = BlockSurfaceGeometry.Capture(leftOnly, new[] { left });
                var b = BlockSurfaceGeometry.Capture(rightOnly, new[] { right });
                Assert.That(leftOnly.TryGetFace(a[left][0], out var leftFace), Is.True); Assert.That(leftFace.ValidCells, Is.Empty);
                Assert.That(rightOnly.TryGetFace(b[right][0], out var rightFace), Is.True); Assert.That(rightFace.ValidCells, Is.Empty);
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { left, right });
                Assert.That(world.FaceCount, Is.EqualTo(1)); CollectionAssert.AreEqual(catalog[left], catalog[right]);
                Assert.That(world.TryGetFace(catalog[left][0], out var shared), Is.True); Assert.That(shared.ValidCells, Has.Count.EqualTo(1));
                Assert.That(world.TryProject(shared.Id, new Vector3(.075f, 0, -.15f), out var leftKey), Is.True);
                Assert.That(world.TryProject(shared.Id, new Vector3(.225f, 0, -.15f), out var rightKey), Is.True);
                Assert.That(rightKey, Is.EqualTo(leftKey)); Assert.That(world.TrySet(leftKey, 1, out _), Is.True);
                Assert.That(world.TryGet(rightKey, out var paint), Is.True); Assert.That(paint.Owner, Is.EqualTo(1));
                Assert.That(world.PinkCount, Is.EqualTo(1), "The same physical square may not score once per source collider.");
                var reordered = new BlockSurfaceWorld(1);
                var reversed = BlockSurfaceGeometry.Capture(reordered, new[] { right, left, left });
                CollectionAssert.AreEqual(catalog[left], reversed[left]); CollectionAssert.AreEqual(catalog[right], reversed[right]);
                Assert.That(reordered.TopologyHash, Is.EqualTo(world.TopologyHash));
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        [Test] public void RealRayHitsAndEitherColliderResolveTheSameSquareAtATileSeam()
        {
            var root = new GameObject("Surface query across tile seam"); var meshes = new List<Mesh>();
            var layerPairs = new[] { new Vector2Int(29, 8), new Vector2Int(28, 28), new Vector2Int(28, 8), new Vector2Int(28, 29) };
            var ignored = new bool[layerPairs.Length];
            for (int i = 0; i < ignored.Length; i++) ignored[i] = Physics.GetIgnoreLayerCollision(layerPairs[i].x, layerPairs[i].y);
            try
            {
                var arena = root.AddComponent<Splatoon.Prototype.PrototypeArena>();
                var left = Patch(root, meshes, "Left tile", 32, 0, .15f, -.3f, 0, 6);
                var right = Patch(root, meshes, "Right tile", 33, .15f, .3f, -.3f, 0, 6);
                var terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(arena); terrain.EnableSurfaceMode();
                Physics.SyncTransforms();
                Assert.That(left.Raycast(new Ray(new Vector3(.075f, 6.1f, -.15f), Vector3.down), out var hitLeft, .2f), Is.True);
                Assert.That(right.Raycast(new Ray(new Vector3(.225f, 6.1f, -.15f), Vector3.down), out var hitRight, .2f), Is.True);
                Assert.That(terrain.TryGetSurfacePaint(left, hitLeft.point, hitLeft.normal, out var leftCell), Is.True);
                Assert.That(terrain.PaintSurfaceCell(leftCell.Key, 1), Is.True);
                Assert.That(terrain.TryGetSurfacePaint(right, hitRight.point, hitRight.normal, out var rightCell), Is.True);
                Assert.That(rightCell.Key, Is.EqualTo(leftCell.Key)); Assert.That(rightCell.Owner, Is.EqualTo(1));
                var seam = new Vector3(.15f, 6, -.15f);
                Assert.That(terrain.TryGetSurfacePaint(left, seam, Vector3.up, out var leftSeam), Is.True);
                Assert.That(terrain.TryGetSurfacePaint(right, seam, Vector3.up, out var rightSeam), Is.True);
                Assert.That(leftSeam.Key, Is.EqualTo(rightSeam.Key)); Assert.That(leftSeam.Owner, Is.EqualTo(1));
                Assert.That(terrain.TryGetSurfacePaint(seam, Vector3.up, out var raySelected), Is.True,
                    "The short ray may select either tile at the shared boundary.");
                Assert.That(raySelected.Key, Is.EqualTo(leftCell.Key)); Assert.That(raySelected.Owner, Is.EqualTo(1));
                Assert.That(terrain.PinkCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
                for (int i = 0; i < ignored.Length; i++) Physics.IgnoreLayerCollision(layerPairs[i].x, layerPairs[i].y, ignored[i]);
            }
        }

        [TestCase(.01f)] [TestCase(.000005f)]
        public void CoplanarUnionCannotFillARealHoleBetweenColliders(float gap)
        {
            var root = new GameObject("Coplanar tiles separated by real empty space"); var meshes = new List<Mesh>();
            try
            {
                var left = Patch(root, meshes, "Left of hole", 34, 0, .15f - gap * .5f, -.3f, 0);
                var right = Patch(root, meshes, "Right of hole", 35, .15f + gap * .5f, .3f, -.3f, 0);
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { left, right });
                Assert.That(world.FaceCount, Is.EqualTo(1)); Assert.That(world.TryGetFace(catalog[left][0], out var face), Is.True);
                Assert.That(face.ValidCells, Is.Empty); Assert.That(world.TryProject(face.Id, new Vector3(.15f, 0, -.15f), out _), Is.False);
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        [TestCase(.000005f)] [TestCase(.005f)]
        public void SeparateNearbyColliderPlanesKeepIndependentCharts(float separation)
        {
            var root = new GameObject("Nearby map planes are not the same physical surface"); var meshes = new List<Mesh>();
            try
            {
                var lower = Patch(root, meshes, "Lower", 36, 0, .3f, -.3f, 0);
                var upper = Patch(root, meshes, "Upper", 37, 0, .3f, -.3f, 0, separation);
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { lower, upper });
                Assert.That(world.FaceCount, Is.EqualTo(2)); Assert.That(catalog[lower][0], Is.Not.EqualTo(catalog[upper][0]));
                Assert.That(world.TryProject(catalog[lower][0], new Vector3(.15f, 0, -.15f), out var a), Is.True);
                Assert.That(world.TryProject(catalog[upper][0], new Vector3(.15f, separation, -.15f), out var b), Is.True);
                world.TrySet(a, 1, out _); world.TrySet(b, 2, out _);
                Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.BlueCount, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        [Test] public void OppositeFacesOnTheSamePlaneNeverShareTheirOwnership()
        {
            var root = new GameObject("Oriented sides of a zero thickness face"); var meshes = new List<Mesh>();
            try
            {
                var front = Patch(root, meshes, "Front", 38, 0, .3f, -.3f, 0);
                var back = Patch(root, meshes, "Back", 39, 0, .3f, -.3f, 0, reverse: true);
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { front, back });
                Assert.That(world.FaceCount, Is.EqualTo(2));
                Assert.That(world.TryGetFace(catalog[front][0], out var a), Is.True);
                Assert.That(world.TryGetFace(catalog[back][0], out var b), Is.True);
                Assert.That(Vector3.Dot(a.Normal, b.Normal), Is.EqualTo(-1).Within(.000001f));
                Assert.That(a.ValidCells, Has.Count.EqualTo(1)); Assert.That(b.ValidCells, Has.Count.EqualTo(1));
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        [Test] public void OriginalMapSizedAxisAlignedFacesRetainEveryCompleteSquareWithoutNumericalHoles()
        {
            var root = new GameObject("TrainingGround rectangle clipping precision"); var meshes = new List<Mesh>();
            try
            {
                var platform = new GameObject("PlatformBody_1 exact authored box"); platform.transform.SetParent(root.transform, false);
                platform.transform.position = new Vector3(10.5f, 1.48f, 0);
                platform.AddComponent<PaintSurface>().SurfaceId = 40;
                var box = platform.AddComponent<BoxCollider>(); box.size = new Vector3(5, 2.96f, 10);
                var ground = Patch(root, meshes, "Ground_0_0 exact authored footprint", 41, -16, 0, -32, -16);
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new Collider[] { box, ground });
                int checkedSides = 0;
                foreach (int id in catalog[box])
                    if (world.TryGetFace(id, out var face) && Mathf.Abs(face.Normal.x) > .9999f)
                    { Assert.That(face.ValidCells, Has.Count.EqualTo(288)); checkedSides++; }
                Assert.That(checkedSides, Is.EqualTo(2));
                Assert.That(world.TryGetFace(catalog[ground][0], out var floor), Is.True);
                Assert.That(floor.ValidCells, Has.Count.EqualTo(2756));
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        [Test] public void SlopedNonUniformlyScaledFacesUseWorldMetresAndSeparateBothWallSides()
        {
            var root = new GameObject("Surface geometry scaled fixture");
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 17;
                root.transform.SetPositionAndRotation(new Vector3(1.3f, .7f, -.9f), Quaternion.Euler(0, 27, 18));
                root.transform.localScale = new Vector3(2, 1, 3);
                var box = root.AddComponent<BoxCollider>(); box.size = new Vector3(1.2f, 1.2f, .02f);
                var world = new BlockSurfaceWorld(1);
                var faces = BlockSurfaceGeometry.Capture(world, new[] { box });
                Assert.That(faces[box], Has.Length.EqualTo(6));
                int paintedSides = 0;
                foreach (int id in faces[box])
                {
                    Assert.That(world.TryGetFace(id, out var face), Is.True);
                    Assert.That(face.U.magnitude, Is.EqualTo(1).Within(.00001));
                    Assert.That(face.V.magnitude, Is.EqualTo(1).Within(.00001));
                    Assert.That(Vector3.Dot(face.U, face.V), Is.EqualTo(0).Within(.00001));
                    if (face.ValidCells.Count == 0) continue;
                    var coordinate = face.ValidCells[0]; var key = new BlockSurfaceKey(id, coordinate.x, coordinate.y);
                    Assert.That(world.TrySet(key, 1, out _), Is.True); paintedSides++;
                    Assert.That(Vector3.Distance(face.Center(coordinate.x + 1, coordinate.y), face.Center(coordinate.x, coordinate.y)), Is.EqualTo(.3f).Within(.00001));
                }
                Assert.That(paintedSides, Is.EqualTo(2), "The thin wall's two broad faces have separate complete-square ownership");
                Assert.That(world.PinkCount, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test] public void MirroredBoxesStillMatchTheRealOutwardPhysicsNormal()
        {
            var root = new GameObject("Mirrored authored box");
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 18;
                root.transform.localScale = new Vector3(-1, 1, 1);
                var box = root.AddComponent<BoxCollider>(); box.size = new Vector3(1.2f, .3f, 1.2f);
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { box });
                Physics.SyncTransforms();
                Assert.That(box.Raycast(new Ray(new Vector3(.15f, 1, .15f), Vector3.down), out var hit, 2), Is.True);
                bool projected = false;
                foreach (int id in catalog[box])
                    if (world.TryGetFace(id, out var face) && Vector3.Dot(face.Normal, hit.normal) > .999f && world.TryProject(id, hit.point, out _)) projected = true;
                Assert.That(projected, Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(0f)] [TestCase(.005f)]
        public void NearbySameFacingMeshPlanesResolveTheActualHitAndUseAStableTie(float planeZ)
        {
            var root = new GameObject("Parallel surface identity fixture");
            var mesh = new Mesh { name = "Two same-facing planes five millimetres apart" };
            try
            {
                var arena = root.AddComponent<Splatoon.Prototype.PrototypeArena>();
                var map = new GameObject("Two parallel map faces"); map.transform.SetParent(root.transform, false);
                map.AddComponent<PaintSurface>().SurfaceId = 19;
                mesh.vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(.6f, 0, 0), new Vector3(.6f, .6f, 0), new Vector3(0, .6f, 0),
                    new Vector3(0, 0, .005f), new Vector3(.6f, 0, .005f), new Vector3(.6f, .6f, .005f), new Vector3(0, .6f, .005f)
                };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }; mesh.RecalculateBounds();
                var collider = map.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                var terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(arena); terrain.EnableSurfaceMode();
                var catalog = (Dictionary<Collider, int[]>)typeof(BlockTerrain).GetField("_surfacePaintFaces",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(terrain);
                int[] faces = catalog[collider]; Assert.That(faces, Has.Length.EqualTo(2));
                Physics.SyncTransforms();
                Assert.That(collider.Raycast(new Ray(new Vector3(.15f, .15f, planeZ + .001f), Vector3.back), out var hit, .002f), Is.True);
                Assert.That(hit.point.z, Is.EqualTo(planeZ).Within(.000001));
                int expected = 0;
                foreach (int id in faces)
                {
                    Assert.That(terrain.SurfacePaints.TryGetFace(id, out var face), Is.True);
                    Assert.That(Vector3.Dot(face.Normal, hit.normal), Is.GreaterThan(.999f));
                    Assert.That(terrain.SurfacePaints.TryProject(id, hit.point, out var key), Is.True,
                        "Both planes are within projection tolerance, so first-match selection is insufficient.");
                    bool actualPlane = Mathf.Abs(face.Origin.z - planeZ) < .000001f;
                    if (actualPlane) expected = id;
                    terrain.SurfacePaints.TrySet(key, actualPlane ? (byte)1 : (byte)2, out _);
                }
                Assert.That(expected, Is.Not.Zero);
                Assert.That(terrain.TryGetSurfacePaint(collider, hit.point, hit.normal, out var painted), Is.True);
                Assert.That(painted.Key.FaceId, Is.EqualTo(expected)); Assert.That(painted.Owner, Is.EqualTo(1));
                var midpoint = new Vector3(.15f, .15f, .0025f); int stableFace = Mathf.Min(faces[0], faces[1]);
                Assert.That(terrain.TryGetSurfacePaint(collider, midpoint, Vector3.forward, out var firstTie), Is.True);
                System.Array.Reverse(faces);
                Assert.That(terrain.TryGetSurfacePaint(collider, midpoint, Vector3.forward, out var reversedTie), Is.True);
                Assert.That(firstTie.Key.FaceId, Is.EqualTo(stableFace)); Assert.That(reversedTie.Key, Is.EqualTo(firstTie.Key));
            }
            finally
            {
                Object.DestroyImmediate(root); Object.DestroyImmediate(mesh);
                Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
            }
        }

        static Mesh SplitTriangleMesh(string name, Vector3[] vertices, int[] triangles)
        {
            var split = new Vector3[triangles.Length]; var indices = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; i++) { split[i] = vertices[triangles[i]]; indices[i] = i; }
            var mesh = new Mesh { name = name, vertices = split, triangles = indices }; mesh.RecalculateBounds(); return mesh;
        }

        static void AssertTriangleEnumerationStable(BlockSurfaceWorld world, MeshCollider collider, int[] faces)
        {
            int[] original = collider.sharedMesh.triangles, reversed = new int[original.Length];
            // Reverse triangle enumeration and cyclically rotate each triangle.
            // Geometry and winding remain identical; neither may change identity.
            for (int i = 0; i < original.Length; i += 3)
            {
                int source = original.Length - i - 3;
                reversed[i] = original[source + 1]; reversed[i + 1] = original[source + 2]; reversed[i + 2] = original[source];
            }
            try
            {
                collider.sharedMesh.triangles = reversed;
                var reordered = new BlockSurfaceWorld(world.Round);
                var catalog = BlockSurfaceGeometry.Capture(reordered, new[] { collider });
                Assert.That(reordered.TopologyHash, Is.EqualTo(world.TopologyHash));
                CollectionAssert.AreEqual(faces, catalog[collider]);
            }
            finally { collider.sharedMesh.triangles = original; }
        }

        [Test] public void OneMeshColliderKeepsFiveMicrometreParallelLayersSeparateAndOrderIndependent()
        {
            var root = new GameObject("Same collider distinct five-micrometre layers");
            var mesh = SplitTriangleMesh("Independent complete front-facing layers", new[]
            {
                new Vector3(0,0,0), new Vector3(.6f,0,0), new Vector3(.6f,.6f,0), new Vector3(0,.6f,0),
                new Vector3(0,0,.000005f), new Vector3(.6f,0,.000005f), new Vector3(.6f,.6f,.000005f), new Vector3(0,.6f,.000005f)
            }, new[] { 0,1,2, 0,2,3, 4,5,6, 4,6,7 });
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 42;
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { collider });
                Assert.That(catalog[collider], Has.Length.EqualTo(2));
                for (int i = 0; i < 2; i++)
                {
                    Assert.That(world.TryGetFace(catalog[collider][i], out var face), Is.True);
                    Assert.That(face.ValidCells, Has.Count.EqualTo(4));
                    Assert.That(face.Origin.z, Is.EqualTo(i == 0 ? 0 : .000005f).Within(.00000001f));
                    Assert.That(world.TryProject(face.Id, new Vector3(.15f, .15f, face.Origin.z), out var key), Is.True);
                    Assert.That(world.TrySet(key, (byte)(i + 1), out _), Is.True);
                }
                Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.BlueCount, Is.EqualTo(1));
                AssertTriangleEnumerationStable(world, collider, catalog[collider]);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [Test] public void NearParallelHalfTrianglesCannotProjectIntoAFalseCompleteSquare()
        {
            var root = new GameObject("Near-parallel noncoplanar halves");
            var mesh = SplitTriangleMesh("Same intercept but a vertex three hundred micrometres off plane", new[]
            {
                new Vector3(0,0,0), new Vector3(.3f,0,0), new Vector3(.3f,.3f,0),
                new Vector3(0,0,0), new Vector3(.3f,.3f,.0003f), new Vector3(0,.3f,0)
            }, new[] { 0,1,2, 3,4,5 });
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 43;
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { collider });
                Assert.That(catalog[collider], Has.Length.EqualTo(2));
                foreach (int id in catalog[collider])
                { Assert.That(world.TryGetFace(id, out var face), Is.True); Assert.That(face.ValidCells, Is.Empty); }
                AssertTriangleEnumerationStable(world, collider, catalog[collider]);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [TestCase(.000001f, true)] [TestCase(.000005f, false)]
        public void OriginalVertexCoplanarityBudgetAcceptsMicrometreNoiseButNotFiveMicrometreLayers(float displacement, bool merged)
        {
            var root = new GameObject("Bounded world-metre float plane noise");
            var mesh = SplitTriangleMesh("Complementary half squares on nearby planes", new[]
            {
                new Vector3(0,0,0), new Vector3(.3f,0,0), new Vector3(.3f,.3f,0),
                new Vector3(0,0,displacement), new Vector3(.3f,.3f,displacement), new Vector3(0,.3f,displacement)
            }, new[] { 0,1,2, 3,4,5 });
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 44;
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { collider });
                Assert.That(catalog[collider], Has.Length.EqualTo(merged ? 1 : 2));
                foreach (int id in catalog[collider])
                { Assert.That(world.TryGetFace(id, out var face), Is.True); Assert.That(face.ValidCells, Has.Count.EqualTo(merged ? 1 : 0)); }
                AssertTriangleEnumerationStable(world, collider, catalog[collider]);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [Test] public void CoplanarityRequiresEveryTrianglePairAndCannotGrowByTransitiveNoise()
        {
            var root = new GameObject("Three planes cannot chain a larger displacement");
            var vertices = new List<Vector3>(); var indices = new List<int>();
            foreach (float z in new[] { 0, .0000015f, .000003f })
            {
                int start = vertices.Count;
                vertices.AddRange(new[] { new Vector3(0,0,z), new Vector3(.3f,0,z), new Vector3(.3f,.3f,z), new Vector3(0,.3f,z) });
                indices.AddRange(new[] { start,start+1,start+2, start,start+2,start+3 });
            }
            var mesh = SplitTriangleMesh("Pairwise plane distance has a two-micrometre bound", vertices.ToArray(), indices.ToArray());
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 45;
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { collider });
                Assert.That(catalog[collider], Has.Length.EqualTo(2), "Adjacent 1.5-micrometre gaps may not transitively collapse a three-micrometre span.");
                AssertTriangleEnumerationStable(world, collider, catalog[collider]);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [TestCase(1f)] [TestCase(-1f)]
        public void ClosedConvexWedgeProjectsEveryActualFaceInWorldMetresIncludingMirrors(float mirror)
        {
            var root = new GameObject("Closed convex ramp source");
            var vertices = new[] { new Vector3(0, 0, 0), new Vector3(1.2f, 0, 0), new Vector3(0, 0, 1.2f),
                new Vector3(1.2f, 0, 1.2f), new Vector3(0, 1.2f, 1.2f), new Vector3(1.2f, 1.2f, 1.2f) };
            // The production ramp uses disconnected hard faces and mixed source
            // winding. Position welding must recognize the same closed solid.
            var mesh = SplitTriangleMesh("Disconnected closed wedge", vertices,
                new[] { 0,4,2, 0,2,1, 1,2,3, 1,3,5, 2,4,5, 2,5,3, 0,1,5, 0,5,4 });
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 20;
                root.transform.SetPositionAndRotation(new Vector3(.12f, .23f, -.31f), Quaternion.Euler(13, 27, 8));
                root.transform.localScale = new Vector3(mirror * 1.5f, .75f, 1.25f);
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; collider.convex = true;
                var world = new BlockSurfaceWorld(1); var catalog = BlockSurfaceGeometry.Capture(world, new[] { collider });
                Assert.That(catalog[collider], Has.Length.EqualTo(5)); Physics.SyncTransforms();
                int[][] polygons = { new[] { 0,2,4 }, new[] { 0,1,3,2 }, new[] { 1,5,3 }, new[] { 2,3,5,4 }, new[] { 0,4,5,1 } };
                Vector3[] normals = { Vector3.left, Vector3.down, Vector3.right, Vector3.forward, new Vector3(0, 1, -1).normalized };
                var matched = new HashSet<int>();
                for (int side = 0; side < polygons.Length; side++)
                {
                    Vector3 outward = root.transform.localToWorldMatrix.inverse.transpose.MultiplyVector(normals[side]).normalized;
                    BlockSurfaceFace face = default;
                    foreach (int id in catalog[collider])
                        if (world.TryGetFace(id, out var candidate) && Vector3.Dot(candidate.Normal, outward) > .99999f) face = candidate;
                    Assert.That(face.Id, Is.Not.Zero, "Every outward wedge face needs a separate chart.");
                    Assert.That(matched.Add(face.Id), Is.True); Assert.That(face.ValidCells, Is.Not.Empty);
                    var corners = new Vector3[polygons[side].Length];
                    for (int i = 0; i < corners.Length; i++) corners[i] = root.transform.TransformPoint(vertices[polygons[side][i]]);
                    double projectedTwiceArea = 0; float actualArea = 0;
                    for (int i = 0; i < corners.Length; i++)
                    {
                        var a = corners[i] - face.Origin; var b = corners[(i + 1) % corners.Length] - face.Origin;
                        Assert.That(Mathf.Abs(Vector3.Dot(a, face.Normal)), Is.LessThan(.00001f));
                        projectedTwiceArea += (double)Vector3.Dot(a, face.U) * Vector3.Dot(b, face.V) -
                            (double)Vector3.Dot(a, face.V) * Vector3.Dot(b, face.U);
                        if (i > 0 && i + 1 < corners.Length) actualArea += Vector3.Cross(corners[i] - corners[0], corners[i + 1] - corners[0]).magnitude * .5f;
                    }
                    Assert.That(System.Math.Abs(projectedTwiceArea) * .5, Is.EqualTo(actualArea).Within(.00001), "Chart area must equal the transformed face's actual square metres.");
                    Assert.That(face.ValidCells.Count * .09f, Is.LessThanOrEqualTo(actualArea + .00001f));
                    var cell = face.ValidCells[0]; var point = face.Center(cell.x, cell.y);
                    Assert.That(collider.Raycast(new Ray(point + outward * .1f, -outward), out var hit, .2f), Is.True);
                    Assert.That(Vector3.Dot(hit.normal, face.Normal), Is.GreaterThan(.9999f));
                    Assert.That(Vector3.Distance(hit.point, point), Is.LessThan(.0001f));
                    Assert.That(world.TryProject(face.Id, hit.point, out var key), Is.True);
                    Assert.That(key, Is.EqualTo(new BlockSurfaceKey(face.Id, cell.x, cell.y)));
                }
                Assert.That(matched.Count, Is.EqualTo(5));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [TestCase(0f, true)] [TestCase(.000003f, false)]
        public void InkV4RampHardEdgeJitterOnlyWeldsTheValidationCopyWithinTwoMicrometres(float extraCrack, bool accepted)
        {
            var root = new GameObject("Real InkV4 ramp hard-edge jitter");
            // Same disconnected source and sub-micrometre coordinates as the
            // extracted Ramp_-1_-1_Body_InkV4 payload. The roof alone was rebaked.
            var vertices = new[]
            {
                new Vector3(-2.5f,0,-2.99999976f), new Vector3(2.5f,0,-2.99999976f), new Vector3(2.5f,2.95999956f,2.99999952f),
                new Vector3(-2.5f,0,-2.99999976f), new Vector3(2.5f,2.95999956f,2.99999952f), new Vector3(-2.5f,2.95999956f,2.99999952f),
                new Vector3(-2.5f,0,-3), new Vector3(-2.5f,0,3), new Vector3(2.5f,0,-3),
                new Vector3(2.5f,0,-3), new Vector3(-2.5f,0,3), new Vector3(2.5f,0,3),
                new Vector3(-2.5f,0,-3), new Vector3(-2.5f,2.96f,3), new Vector3(-2.5f,0,3),
                new Vector3(2.5f,0,-3), new Vector3(2.5f,0,3), new Vector3(2.5f,2.96f,3),
                new Vector3(-2.5f,0,3), new Vector3(-2.5f,2.96f,3), new Vector3(2.5f,2.96f,3),
                new Vector3(-2.5f,0,3), new Vector3(2.5f,2.96f,3), new Vector3(2.5f,0,3)
            };
            for (int i = 0; i < 6; i++) vertices[i].z -= extraCrack;
            var triangles = new int[vertices.Length]; for (int i = 0; i < triangles.Length; i++) triangles[i] = i;
            var mesh = new Mesh { name = "Real disconnected InkV4 ramp source", vertices = vertices, triangles = triangles }; mesh.RecalculateBounds();
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 22;
                root.transform.position = new Vector3(-10.5f, 0, -8);
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; collider.convex = true;
                var world = new BlockSurfaceWorld(1);
                if (accepted)
                {
                    var catalog = BlockSurfaceGeometry.Capture(world, new[] { collider });
                    Assert.That(catalog[collider], Has.Length.EqualTo(5));
                    BlockSurfaceFace back = default;
                    foreach (int id in catalog[collider])
                        if (world.TryGetFace(id, out var face) && Vector3.Dot(face.Normal, Vector3.forward) > .99999f) back = face;
                    Assert.That(back.Id, Is.Not.Zero); Assert.That(back.ValidCells, Is.Not.Empty);
                    Assert.That(back.Normal, Is.EqualTo(Vector3.forward), "Projection keeps the actual unjittered back face, not the welded validation roof corners.");
                    Assert.That(back.Origin.z, Is.EqualTo(-5f));
                }
                else
                {
                    var error = Assert.Throws<System.NotSupportedException>(() => BlockSurfaceGeometry.Capture(world, new[] { collider }));
                    Assert.That(error.InnerException, Is.InstanceOf<System.ArgumentException>());
                    Assert.That(error.InnerException.Message, Does.Contain("closed and manifold"));
                    Assert.That(world.FaceCount, Is.Zero, "A crack larger than the two-micrometre weld bound is still rejected.");
                }
                CollectionAssert.AreEqual(vertices, mesh.vertices, "Validation may not mutate the source map mesh.");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [Test] public void ConcaveSourceMarkedConvexCannotMasqueradeAsItsCookedHull()
        {
            var root = new GameObject("Concave input with a different cooked hull");
            var vertices = new[] { new Vector3(-.6f, 0, -.6f), new Vector3(.6f, 0, -.6f), new Vector3(.6f, 0, .6f), new Vector3(-.6f, 0, .6f),
                new Vector3(-.6f, 1.2f, -.6f), new Vector3(.6f, 1.2f, -.6f), new Vector3(.6f, 1.2f, .6f), new Vector3(-.6f, 1.2f, .6f), new Vector3(0, .3f, 0) };
            var mesh = SplitTriangleMesh("Closed box with inward roof dent", vertices,
                new[] { 0,2,1, 0,3,2, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7, 4,5,8, 5,6,8, 6,7,8, 7,4,8 });
            try
            {
                root.AddComponent<PaintSurface>().SurfaceId = 21;
                var collider = root.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; collider.convex = true;
                var world = new BlockSurfaceWorld(1);
                var error = Assert.Throws<System.NotSupportedException>(() => BlockSurfaceGeometry.Capture(world, new[] { collider }));
                Assert.That(error.Message, Does.Contain("cooked hull"));
                Assert.That(error.InnerException, Is.InstanceOf<System.ArgumentException>());
                Assert.That(world.FaceCount, Is.Zero, "Rejected input must not publish source faces as the cooked convex collider.");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }
    }
}
#endif
