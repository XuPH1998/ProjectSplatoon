#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class TpsOverlapTests
    {
        GameObject root;
        BlockTerrain terrain;
        readonly TpsAimSolver solver = new();
        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            root = new GameObject("Concave aiming acceptance");
            terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(null);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (root != null) Object.DestroyImmediate(root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        IEnumerator Publish(BlockWorld world)
        {
            world.Seal(terrain.Revision + 1, world.RevisionKeys.Concat(terrain.World.RevisionKeys).Distinct().ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++)
            { terrain.Tick(false, tick / 60.0, 1f / 60); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Physics.SyncTransforms();
        }
        [UnityTest] public IEnumerator StableCubeOverlapReturnsTheActualFaceAndRejectsEmptyChunkSpaceAndCorners()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            world.Set(Vector3Int.zero, BlockCell.Create(1, true));
            world.Set(new Vector3Int(8, 0, 8), BlockCell.Create(2, true));
            yield return Publish(world);
            var origin = new Vector3(.13f, .05f, .05f);
            Assert.That(solver.Overlap(origin, .04f, ulong.MaxValue, Vector3.back, out var hit, ignoreTeam: 1), Is.True,
                "Friendly blocks still stop projectiles when friendly players are ignored");
            Assert.That(hit.Collider, Is.TypeOf<MeshCollider>());
            Assert.That(((MeshCollider)hit.Collider).convex, Is.False);
            Assert.That(Vector3.Distance(hit.Point, new Vector3(.1f, .05f, .05f)), Is.LessThan(.0001f));
            Assert.That(Vector3.Distance(hit.Normal, Vector3.right), Is.LessThan(.0001f));
            Assert.That(hit.Distance, Is.EqualTo(.03f).Within(.0001f));
            Assert.That(terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(Vector3Int.zero)); Assert.That(contact.Owner, Is.EqualTo(1));
            Assert.That(contact.Revision, Is.EqualTo(terrain.Revision));
            Assert.That(solver.Overlap(new Vector3(.45f, .05f, .45f), .04f, ulong.MaxValue, Vector3.back, out _), Is.False,
                "A chunk's collider bounds are not occupied geometry");
            Assert.That(solver.Overlap(new Vector3(.13f, .13f, .05f), .04f, ulong.MaxValue, Vector3.back, out _), Is.False,
                "Sphere-to-cube distance must reject a corner outside the sphere, despite axiswise radius expansion");
            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest] public IEnumerator EmbeddedOverlapReadsPublishedCellsEvenWhenPhysXHasNoInteriorFace()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int z = 0; z < 3; z++) for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
                world.Set(new Vector3Int(x, y, z), BlockCell.Create(2, true));
            yield return Publish(world);
            var cell = Vector3Int.one; Vector3 origin = BlockWorld.Center(cell);
            Assert.That(solver.Overlap(origin, .005f, ulong.MaxValue, Vector3.left, out var hit), Is.True);
            Assert.That(hit.Point, Is.EqualTo(origin)); Assert.That(hit.Distance, Is.Zero);
            Assert.That(hit.Normal, Is.EqualTo(Vector3.left));
            Assert.That(terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell)); Assert.That(contact.Owner, Is.EqualTo(2));
            var empty = new BlockWorld(world.Dimensions);
            empty.Seal(terrain.Revision + 1, terrain.World.RevisionKeys.ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(empty.EncodeSnapshot()), Is.True);
            Assert.That(solver.Overlap(origin, .005f, ulong.MaxValue, Vector3.left, out _), Is.True,
                "A queued replacement cannot hide the published cells before collider/data publication");
            for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++)
            { terrain.Tick(false, 20 + tick / 60.0, 1f / 60); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero);
            Assert.That(solver.Overlap(origin, .005f, ulong.MaxValue, Vector3.left, out _), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest] public IEnumerator InteriorChunkWithoutSurfaceTrianglesStillProvidesAnOccupiedCellContact()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int z = -1; z <= 16; z++) for (int y = -1; y <= 16; y++) for (int x = -1; x <= 16; x++)
                world.Set(new Vector3Int(x, y, z), BlockCell.Create(1, true));
            yield return Publish(world);
            var cell = new Vector3Int(8, 8, 8); Vector3 origin = BlockWorld.Center(cell);
            Assert.That(terrain.TryOverlapStable(origin, .005f, Vector3.back, out var collider, out var contact, out float distance), Is.True);
            Assert.That(terrain.IsStableCollider(collider), Is.True, "Use a real published collider as the terrain identity");
            Assert.That(contact.Cell, Is.EqualTo(cell)); Assert.That(contact.Chunk, Is.EqualTo(Vector3Int.zero));
            Assert.That(contact.Revision, Is.EqualTo(terrain.World.ChunkRevision(Vector3Int.zero)));
            Assert.That(contact.Point, Is.EqualTo(origin)); Assert.That(distance, Is.Zero);
            Assert.That(terrain.TryContact(collider, contact.Point, contact.Normal, out var resolved), Is.True);
            Assert.That(resolved.Cell, Is.EqualTo(cell));
            Assert.That(solver.Overlap(origin, .005f, ulong.MaxValue, Vector3.back, out _), Is.True);
            LogAssert.NoUnexpectedReceived();
        }
        MeshCollider Plane(Vector3 position, Quaternion rotation)
        {
            var obj = new GameObject("Static concave map"); obj.transform.SetParent(root.transform);
            obj.transform.SetPositionAndRotation(position, rotation); obj.transform.localScale = new Vector3(2, 1, .5f);
            var mesh = new Mesh { vertices = new[] { new Vector3(-2, 0, -2), new Vector3(2, 0, -2), new Vector3(2, 0, 2), new Vector3(-2, 0, 2) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 } };
            mesh.RecalculateBounds(); var collider = obj.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; collider.convex = false;
            return collider;
        }
        [UnityTest] public IEnumerator StaticConcaveMapReturnsARealTransformedSurfaceContactWithoutClosestPoint()
        {
            var map = Plane(new Vector3(2, 1, -3), Quaternion.Euler(20, 25, 0));
            Mesh mesh = map.sharedMesh;
            try
            {
                Physics.SyncTransforms(); Vector3 point = map.transform.TransformPoint(new Vector3(.5f, 0, .5f));
                Vector3 normal = map.transform.up, origin = point + normal * .04f;
                Assert.That(solver.Overlap(origin, .08f, ulong.MaxValue, -normal, out var hit), Is.True);
                Assert.That(hit.Collider, Is.EqualTo(map));
                Assert.That(Vector3.Distance(hit.Point, point), Is.LessThan(.001f));
                Assert.That(Vector3.Dot(hit.Normal, normal), Is.GreaterThan(.999f));
                Assert.That(hit.Distance, Is.EqualTo(.04f).Within(.001f));
                // Native penetration ignores mesh backfaces: do not invent a hit
                // from the map's enclosing AABB when the surface query returns false.
                Assert.That(solver.Overlap(point - normal * .04f, .08f, ulong.MaxValue, normal, out _), Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally { Object.DestroyImmediate(map.gameObject); Object.DestroyImmediate(mesh); }
            yield return null;
        }
        [UnityTest] public IEnumerator FallingOverlapStillWinsTheNearestContactAndRespectsThePlayerOnlyFilter()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            world.Set(new Vector3Int(2, 1, 0), BlockCell.Create(2, true));
            world.Falling.Add(1, new FallingRun { Id = 1, Bottom = new Vector3Int(0, 1, 0), Cells = new[] { BlockCell.Create(1) }, StartedAt = 100 });
            world.NextRunId = 2; yield return Publish(world);
            Vector3 origin = new(.105f, .15f, .05f);
            Assert.That(terrain.TryOverlapStable(origin, .1f, Vector3.right, out var stableCollider, out _, out float stableDistance), Is.True);
            Assert.That(stableDistance, Is.GreaterThan(.09f));
            Assert.That(solver.Overlap(origin, .1f, ulong.MaxValue, Vector3.right, out var hit, ignoreTeam: 1), Is.True);
            Assert.That(hit.Collider, Is.Not.EqualTo(stableCollider)); Assert.That(hit.Distance, Is.Zero);
            Assert.That(terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True);
            Assert.That(contact.Stable, Is.False); Assert.That(contact.Owner, Is.EqualTo(1));
            Assert.That(solver.Overlap(origin, .1f, ulong.MaxValue, Vector3.right, out _, playersOnly: true, ignoreTeam: 1), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
