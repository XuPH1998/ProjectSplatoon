#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Real production Upload/Bake/publication; small deterministic geometry is not a performance substitute.
    public sealed class BlockCollisionMemoryTests
    {
        GameObject _object;
        BlockTerrain _terrain;
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static object Get(object value, string field) => value.GetType().GetField(field, AllInstance).GetValue(value);
        static void Call(BlockTerrain terrain, string name, params object[] args)
            => typeof(BlockTerrain).GetMethod(name, PrivateInstance).Invoke(terrain, args);
        static bool WaitingAdmission(BlockWorkerQueue admissions, CancellationToken token)
        {
            lock (Get(admissions, "_gate"))
                return new[] { "_foreground", "_background" }.Any(queue => ((IEnumerable)Get(admissions, queue))
                    .Cast<object>().Any(request => ((CancellationToken)Get(request, "Token")).Equals(token)));
        }
        static MeshColliderCookingOptions Cooking => (MeshColliderCookingOptions)Convert.ToInt32(typeof(BlockTerrain)
            .GetField("Cooking", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue());

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _object = new GameObject("Vertex-only collision memory fixture");
            _terrain = _object.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_object != null) Object.DestroyImmediate(_object);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static void VertexOnly(Mesh mesh)
        {
            Assert.That(mesh, Is.Not.Null); Assert.That(mesh.isReadable, Is.True, "real bake and runtime collider retain the existing readable mesh contract");
            Assert.That(mesh.HasVertexAttribute(VertexAttribute.Normal), Is.False);
            Assert.That(mesh.HasVertexAttribute(VertexAttribute.Color), Is.False);
        }
        IEnumerator Install(BlockWorld world)
        {
            world.Seal(_terrain.Revision + 1, world.Chunks.Keys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int frame = 0; frame < 1000 && _terrain.PendingTransactions != 0; frame++)
            { _terrain.Tick(false, frame * .016, .016f); yield return null; }
            Assert.That(_terrain.PendingTransactions, Is.Zero);
        }

        [UnityTest] public IEnumerator InitializedFallingRenderMeshRetainsFullNormalsAndWhiteColors()
        {
            var mesh = (Mesh)Get(_terrain, "_fallMesh");
            var world = new BlockWorld(Vector2.one); world.Set(Vector3Int.zero, BlockCell.Create(1));
            var original = FrozenBlockMeshBuilder.Build(world, Vector3Int.zero, false);
            Assert.That(mesh, Is.Not.Null); Assert.That(mesh.isReadable, Is.True);
            Assert.That(mesh.HasVertexAttribute(VertexAttribute.Normal), Is.True);
            Assert.That(mesh.HasVertexAttribute(VertexAttribute.Color), Is.True);
            CollectionAssert.AreEqual(original.Vertices.Select(v => v / BlockWorld.CellSize - Vector3.one * .5f).ToArray(), mesh.vertices);
            CollectionAssert.AreEqual(original.Triangles, mesh.triangles);
            CollectionAssert.AreEqual(original.Normals, mesh.normals);
            CollectionAssert.AreEqual(Enumerable.Repeat(new Color32(255, 255, 255, 255), original.Vertices.Length).ToArray(), mesh.colors32);
            Assert.That(mesh.normals.Length, Is.EqualTo(mesh.vertexCount));
            yield return null;
        }
        [UnityTest] public IEnumerator VertexOnlyMeshRealBakeAndPublishedColliderPreserveSeamsHolesAndContacts()
        {
            var cells = new[] { new Vector3Int(-1, 2, -1), new Vector3Int(0, 2, -1), new Vector3Int(2, 2, -1) };
            var incoming = new BlockWorld(new Vector2(32, 64));
            for (int i = 0; i < cells.Length; i++) incoming.Set(cells[i], BlockCell.Create((byte)(i % 2 + 1), true));
            Mesh direct = null;
            try
            {
                var key = BlockWorld.ChunkAt(cells[0]);
                var original = FrozenBlockMeshBuilder.Build(incoming, key, true);
                var geometry = BlockMeshBuilder.Build(incoming, key, true);
                CollectionAssert.AreEqual(original.Vertices, geometry.Vertices);
                CollectionAssert.AreEqual(original.Triangles, geometry.Triangles);
                Assert.That(geometry.Normals, Is.Empty); Assert.That(geometry.Colors, Is.Empty);
                direct = geometry.Upload("Real vertex-only BakeMesh fixture"); VertexOnly(direct);
                Physics.BakeMesh(direct.GetInstanceID(), false, Cooking);
                CollectionAssert.AreEqual(original.Vertices, direct.vertices);
                CollectionAssert.AreEqual(original.Triangles, direct.triangles);

                yield return Install(incoming); // Production does the second real async bake and atomic collider publication.
                foreach (var cell in cells)
                {
                    Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                        out var hit, .6f, 1 << BlockTerrain.TerrainLayer), Is.True);
                    Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
                    var collider = (MeshCollider)hit.collider; VertexOnly(collider.sharedMesh);
                    Assert.That(collider.convex, Is.False); Assert.That(collider.cookingOptions, Is.EqualTo(Cooking));
                    var frozen = FrozenBlockMeshBuilder.Build(incoming, BlockWorld.ChunkAt(cell), true);
                    CollectionAssert.AreEqual(frozen.Vertices, collider.sharedMesh.vertices, "complete collision positions after production bake");
                    CollectionAssert.AreEqual(frozen.Triangles, collider.sharedMesh.triangles, "complete indices and winding after production bake");
                    Assert.That(hit.point.y, Is.EqualTo(.3f).Within(.001)); Assert.That(hit.normal.y, Is.EqualTo(1).Within(.001));
                    Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
                    Assert.That(contact.Cell, Is.EqualTo(cell)); Assert.That(contact.Owner, Is.EqualTo((byte)(Array.IndexOf(cells, cell) % 2 + 1)));
                    Assert.That(contact.Stable, Is.True); Assert.That(contact.Revision, Is.EqualTo(_terrain.Revision));
                }
                Assert.That(Physics.Raycast(new Vector3(.15f, .8f, -.05f), Vector3.down,
                    .8f, 1 << BlockTerrain.TerrainLayer), Is.False, "the hole between cells must remain empty");
                Assert.That(_terrain.PinkCount, Is.EqualTo(2)); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
                Assert.That(_terrain.FailedTransactions, Is.Zero);
            }
            finally { if (direct != null) Object.DestroyImmediate(direct); }
        }

        [UnityTest] public IEnumerator RemovedLastChunkReleasesEmptyManagedOutputsBeforeAtomicRemoval()
        {
            var cell = new Vector3Int(5, 2, 5); var incoming = new BlockWorld(new Vector2(32, 64));
            incoming.Set(cell, BlockCell.Create(1, true)); yield return Install(incoming);
            var empty = new BlockWorld(new Vector2(32, 64)); empty.Seal(_terrain.Revision + 1, Array.Empty<Vector3Int>());
            Assert.That(_terrain.ApplyRemoteSnapshot(empty.EncodeSnapshot()), Is.True);
            _terrain.Tick(false, 10, .016f); _terrain.enabled = false;
            try
            {
                object pending = Get(_terrain, "_pending"); var generate = (Task)Get(pending, "Generate");
                for (int frame = 0; frame < 1000 && !generate.IsCompleted; frame++) yield return null;
                Assert.That(generate.IsCompleted, Is.True); Assert.That(generate.IsFaulted || generate.IsCanceled, Is.False, generate.Exception?.ToString());
                Call(_terrain, "AdvancePending", false);
                var pieces = (Array)Get(pending, "Pieces"); Assert.That(pieces.Length, Is.EqualTo(1)); var piece = pieces.GetValue(0);
                Assert.That(Get(piece, "Render"), Is.Null); Assert.That(Get(piece, "Collision"), Is.Null);
                Assert.That(Get(piece, "RenderMesh"), Is.Null); Assert.That(Get(piece, "CollisionMesh"), Is.Null);
                Assert.That(Get(piece, "View"), Is.Null); Assert.That(((Task)Get(piece, "Bake")).IsCompleted, Is.True);
                Assert.That(_terrain.PinkCount, Is.EqualTo(1), "empty preparation does not remove the published cell before a simulation tick");
                _terrain.Tick(false, 11, .016f);
                Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero); Assert.That(_terrain.PendingTransactions, Is.Zero);
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                    .6f, 1 << BlockTerrain.TerrainLayer), Is.False);
            }
            finally { _terrain.enabled = true; }
        }

        [UnityTest] public IEnumerator AbortAfterUploadCancelsWaitingBakeAndReleasesMeshesOnlyAfterJoin()
        {
            var cell = new Vector3Int(5, 2, 5); var world = new BlockWorld(new Vector2(32, 64));
            world.Set(cell, BlockCell.Create(2, true)); world.Seal(1, world.Chunks.Keys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            _terrain.Tick(false, 1, .016f); _terrain.enabled = false;
            object pending = Get(_terrain, "_pending"); Assert.That(pending, Is.Not.Null);
            var generate = (Task)Get(pending, "Generate");
            for (int frame = 0; frame < 1000 && !generate.IsCompleted; frame++) yield return null;
            Assert.That(generate.IsCompleted, Is.True); Assert.That(generate.IsFaulted || generate.IsCanceled, Is.False, generate.Exception?.ToString());

            var workers = (SemaphoreSlim)typeof(BlockTerrain).GetField("Workers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var admissions = (BlockWorkerQueue)typeof(BlockTerrain).GetField("WorkerAdmissions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            int held = 0;
            try
            {
                while (workers.Wait(0)) held++;
                Assert.That(held, Is.EqualTo(2), "generation is complete and no unrelated terrain operation owns a worker");
                Call(_terrain, "AdvancePending", false);
                var pieces = (Array)Get(pending, "Pieces"); Assert.That(pieces.Length, Is.EqualTo(1));
                var piece = pieces.GetValue(0); var bake = (Task)Get(piece, "Bake");
                var mesh = (Mesh)Get(piece, "CollisionMesh"); var render = (Mesh)Get(piece, "RenderMesh");
                var view = Get(piece, "View"); var viewObject = (GameObject)Get(view, "Object");
                VertexOnly(mesh); Assert.That(bake, Is.Not.Null); Assert.That(bake.IsCompleted, Is.False);
                Assert.That(Get(piece, "Render"), Is.Null); Assert.That(Get(piece, "Collision"), Is.Null);
                Assert.That(viewObject.activeSelf, Is.False); var published = _terrain.World;
                var stop = (CancellationTokenSource)Get(pending, "AdmissionStop"); var token = stop.Token;
                for (int frame = 0; frame < 1000 && !WaitingAdmission(admissions, token); frame++) yield return null;
                Assert.That(WaitingAdmission(admissions, token), Is.True, "the real Bake task has queued its admission");
                Assert.That(workers.CurrentCount, Is.Zero); Assert.That(bake.IsCompleted, Is.False);
                Call(_terrain, "AbortPending"); Call(_terrain, "CleanupRetired");
                Assert.That(token.IsCancellationRequested, Is.True);
                Assert.That(_terrain.World, Is.SameAs(published)); Assert.That(_terrain.BlueCount, Is.Zero);
                // Holding both permits proves that Bake has not entered native work.
                // Waiting admission can cancel immediately; unfinished tasks still own their meshes.
                for (int frame = 0; frame < 1000 && !bake.IsCompleted; frame++)
                {
                    Assert.That(mesh != null && viewObject != null, Is.True, "unfinished Bake task retains its uploaded objects");
                    Assert.That((int)Get(pending, "AdmissionsDisposed"), Is.Zero);
                    Call(_terrain, "CleanupRetired"); yield return null;
                }
                Assert.That(bake.IsCompleted, Is.True); Assert.That(bake.IsCanceled, Is.True);
                Assert.That(generate.Status, Is.EqualTo(TaskStatus.RanToCompletion));
                for (int frame = 0; frame < 1000 && !admissions.WhenIdle.IsCompleted; frame++) yield return null;
                Assert.That(admissions.WhenIdle.IsCompleted, Is.True, "canceled admission retires while the raw permits remain held");
                Assert.That(held, Is.EqualTo(2)); Assert.That(workers.CurrentCount, Is.Zero);
                Call(_terrain, "CleanupRetired"); yield return null;
                Assert.That(mesh == null, Is.True, "completed retirement destroys the native collision mesh");
                Assert.That(render == null, Is.True, "completed retirement destroys its render mesh when present");
                Assert.That(viewObject == null, Is.True); Assert.That(_terrain.PendingTransactions, Is.Zero);
                Assert.That(_terrain.World, Is.SameAs(published)); Assert.That(_terrain.FailedTransactions, Is.Zero);
                Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                    .6f, 1 << BlockTerrain.TerrainLayer), Is.False, "aborted geometry never becomes published collision");
            }
            finally
            {
                while (held > 0) { workers.Release(); held--; }
                _terrain.enabled = true;
            }
        }
    }
}
#endif
