#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BlockWallAnchorTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root, _layout;
        PrototypeArena _arena;
        BlockTerrain _terrain;

        [SetUp]
        public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("Native first wall layer anchor contract");
            _layout = new GameObject("Inactive registered map layout");
            _layout.transform.SetParent(_root.transform, false); _layout.SetActive(false);
            _arena = _layout.AddComponent<PrototypeArena>();
            _arena.Dimensions = new Vector2(32, 64); _arena.SpawnPoints = Array.Empty<Transform>();
            var terrainRoot = new GameObject("Terrain anchor query");
            terrainRoot.transform.SetParent(_root.transform, false);
            _terrain = terrainRoot.AddComponent<BlockTerrain>();
        }

        [TearDown]
        public void Teardown()
        {
            Object.DestroyImmediate(_root);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        BoxCollider Wall(Vector3 normal, Vector3 point, bool registered = true)
        {
            var wall = new GameObject("Real map wall " + normal);
            wall.transform.SetParent(registered ? _layout.transform : _root.transform, false);
            wall.transform.SetPositionAndRotation(point - normal * .1f, Quaternion.LookRotation(normal, Vector3.up));
            var box = wall.AddComponent<BoxCollider>(); box.size = new Vector3(4, 4, .2f); return box;
        }

        void ActivateMap()
        {
            _terrain.Initialize(_arena);
            foreach (var collider in _layout.GetComponentsInChildren<Collider>(true))
                collider.transform.SetParent(_root.transform, true);
            Physics.SyncTransforms();
        }

        bool Anchor(Vector3Int cell) => (bool)typeof(BlockTerrain).GetMethod("StaticMapWallAnchor", Private)
            .Invoke(_terrain, new object[] { cell });

        [TestCase(1, 0, 0f)] [TestCase(-1, 0, 0f)]
        [TestCase(0, 1, 0f)] [TestCase(0, -1, 0f)]
        [TestCase(1, 0, .03f)] [TestCase(-1, 0, .03f)]
        [TestCase(0, 1, .03f)] [TestCase(0, -1, .03f)]
        [TestCase(1, 0, .004f)] [TestCase(-1, 0, .004f)]
        public void OnlyTheFirstQuantizedLayerAnchorsForEveryCardinalFace(int x, int z, float offset)
        {
            var normal = new Vector3(x, 0, z);
            Wall(normal, normal * offset + Vector3.up); ActivateMap();
            int first = offset > .005f ? 1 : 0;
            var cell = new Vector3Int(x > 0 ? first : x < 0 ? -first - 1 : 0,
                10, z > 0 ? first : z < 0 ? -first - 1 : 0);
            var direction = new Vector3Int(x, 0, z);
            Assert.That(Anchor(cell), Is.True, "The first buildable row must remain attached without a block below.");
            Assert.That(Anchor(cell + direction), Is.False, "A second wall layer must not inherit the map anchor.");
            Assert.That(Anchor(cell + direction * 2), Is.False);
            _terrain.World.Set(cell, BlockCell.Create(1, Anchor(cell)));
            _terrain.World.Set(cell + direction, BlockCell.Create(2, Anchor(cell + direction)));
            Assert.That(_terrain.World.HasSupport(cell, _ => false), Is.True);
            Assert.That(_terrain.World.HasSupport(cell + direction, _ => false), Is.False);
        }

        [TestCase(0f, 0)] [TestCase(.03f, 1)]
        public void DiagonalWallAnchorsItsFirstBuildableFrontierWithoutGrantingAnotherRow(float offset, int firstX)
        {
            var normal = new Vector3(1, 0, 1).normalized;
            Wall(normal, normal * offset + Vector3.up); ActivateMap();
            var cell = new Vector3Int(firstX, 10, 0);
            Assert.That(Anchor(cell), Is.True);
            Assert.That(Anchor(cell + Vector3Int.right), Is.False);
            Assert.That(Anchor(cell + Vector3Int.forward), Is.False);
        }

        [Test]
        public void SlantedWallUsesTheConstructionEnvelopeAndDoesNotAnchorTheOuterLayer()
        {
            var normal = new Vector3(1, .5f, 0).normalized;
            var cell = new Vector3Int(0, 10, 0);
            float envelope = BlockWorld.CellSize * .45f * (Mathf.Abs(normal.x) + Mathf.Abs(normal.y));
            Wall(normal, BlockWorld.Center(cell) - normal * (envelope + .005f)); ActivateMap();
            Assert.That(Anchor(cell), Is.True);
            Assert.That(Anchor(cell + Vector3Int.right), Is.False);
        }

        [Test]
        public void ACornerCanAnchorEitherRealWallButDoesNotAnchorAnOuterDiagonalCell()
        {
            Wall(Vector3.right, Vector3.up); Wall(Vector3.forward, Vector3.up); ActivateMap();
            var first = new Vector3Int(0, 10, 0);
            Assert.That(Anchor(first), Is.True);
            Assert.That(Anchor(first + Vector3Int.right), Is.True, "This cell still belongs to the first Z wall layer.");
            Assert.That(Anchor(first + Vector3Int.forward), Is.True, "This cell still belongs to the first X wall layer.");
            Assert.That(Anchor(first + Vector3Int.right + Vector3Int.forward), Is.False);
        }

        [Test]
        public void OnlyActiveNonTriggerRegisteredMapGeometryCanAnchor()
        {
            var registered = Wall(Vector3.right, Vector3.up);
            Wall(Vector3.forward, Vector3.up, false); ActivateMap();
            var cell = new Vector3Int(0, 10, 0);
            Assert.That(Anchor(cell), Is.True);
            registered.enabled = false; Physics.SyncTransforms();
            Assert.That(Anchor(cell), Is.False, "An unrelated real collider cannot substitute for a registered map wall.");
            registered.enabled = true; registered.gameObject.SetActive(false); Physics.SyncTransforms();
            Assert.That(Anchor(cell), Is.False);
            registered.gameObject.SetActive(true); registered.isTrigger = true; Physics.SyncTransforms();
            Assert.That(Anchor(cell), Is.False);
            registered.isTrigger = false; Physics.SyncTransforms(); Assert.That(Anchor(cell), Is.True);
        }


        BoxCollider NarrowWall(Vector3 normal, Vector3Int cell, float gap = .005f, bool registered = true)
        {
            var wall = new GameObject("Finite narrow map wall " + normal);
            wall.transform.SetParent(registered ? _layout.transform : _root.transform, false);
            var tangent = Vector3.Cross(Vector3.up, normal).normalized;
            float half = BlockWorld.CellSize * .45f;
            float envelope = half * (Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z));
            var face = BlockWorld.Center(cell) - normal * (envelope + gap);
            wall.transform.SetPositionAndRotation(face - normal * .1f + tangent * .03f,
                Quaternion.LookRotation(normal, Vector3.up));
            var box = wall.AddComponent<BoxCollider>(); box.size = new Vector3(.01f, 4, .2f);
            return box;
        }

        [TestCase(1, 0, .005f)] [TestCase(-1, 0, .005f)]
        [TestCase(0, 1, .005f)] [TestCase(0, -1, .005f)]
        [TestCase(1, 0, .001f)] [TestCase(1, 0, .03f)] [TestCase(1, 0, .099f)]
        public void NarrowWallEndsAnchorTheActualFirstLayerWithoutGrantingAnOuterOrDiagonalLayer(int x, int z, float gap)
        {
            var normal = new Vector3(x, 0, z);
            var cell = new Vector3Int(0, 10, 0);
            var wall = NarrowWall(normal, cell, gap); ActivateMap();
            int mask = Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            Assert.That(Array.IndexOf(Physics.OverlapBox(BlockWorld.Center(cell),
                Vector3.one * (BlockWorld.CellSize * .45f), Quaternion.identity, mask,
                QueryTriggerInteraction.Ignore), wall), Is.EqualTo(-1), "Fixture starts in a buildable empty envelope.");
            var outward = new Vector3Int(x, 0, z);
            var beyondEnd = new Vector3Int(z, 0, -x);
            Assert.That(Anchor(cell), Is.True, "A finite wall patch within the swept neighbour must fix its first layer.");
            Assert.That(Anchor(cell + outward), Is.False, "The adjacent outward layer remains mobile.");
            Assert.That(Anchor(cell + outward * 2), Is.False);
            Assert.That(Anchor(cell + beyondEnd), Is.False,
                "A diagonal ray into the wall end cannot grant an anchor beyond the finite construction face.");
        }

        [TestCase(1f, 0f, 1f)] [TestCase(1f, .5f, 0f)]
        public void NarrowDiagonalAndSlantedWallsKeepTheirFirstBuildableLayer(float x, float y, float z)
        {
            var normal = new Vector3(x, y, z).normalized;
            var cell = new Vector3Int(0, 10, 0);
            NarrowWall(normal, cell); ActivateMap();
            Assert.That(Anchor(cell), Is.True);
            Assert.That(Anchor(cell + Vector3Int.right), Is.False);
            if (z != 0) Assert.That(Anchor(cell + Vector3Int.forward), Is.False);
        }

        [Test]
        public void InitialFloorOverlapCannotSupplyAnArtificialHorizontalSweepNormal()
        {
            var floor = new GameObject("Overlapping horizontal map fixture");
            floor.transform.SetParent(_layout.transform, false);
            floor.transform.position = new Vector3(0, .505f, 0);
            floor.AddComponent<BoxCollider>().size = new Vector3(6, 1.01f, 6);
            ActivateMap();
            Assert.That(Anchor(new Vector3Int(0, 10, 0)), Is.False,
                "Distance-zero overlap sentinels are not actual wall-face hits.");
        }

        [Test]
        public void SaturatedSweepBufferCannotTurnUnregisteredGeometryIntoAWallAnchor()
        {
            var cell = new Vector3Int(0, 10, 0);
            for (int i = 0; i < 80; i++) NarrowWall(Vector3.right, cell, registered: false);
            // Keep a registered wall in the broadphase neighbourhood but beyond
            // the one-cell sweep. It cannot validate any unregistered near hit.
            Wall(Vector3.left, new Vector3(.25f, 1.05f, .05f)); ActivateMap();
            int mask = Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            var hits = new RaycastHit[64];
            Assert.That(Physics.BoxCastNonAlloc(BlockWorld.Center(cell), Vector3.one * (BlockWorld.CellSize * .45f),
                Vector3.left, hits, Quaternion.identity, BlockWorld.CellSize, mask,
                QueryTriggerInteraction.Ignore), Is.EqualTo(hits.Length), "Fixture fills the native NonAlloc buffer.");
            Assert.That(Anchor(cell), Is.False, "Only returned hits on registered real map faces can anchor.");
        }

        [Test]
        public void FloorsAndCeilingsNeverGrantWallAnchors()
        {
            foreach (float y in new[] { .95f, 1.15f })
            {
                var surface = new GameObject("Horizontal native map surface");
                surface.transform.SetParent(_layout.transform, false); surface.transform.position = Vector3.up * y;
                surface.AddComponent<BoxCollider>().size = new Vector3(6, .1f, 6);
            }
            ActivateMap();
            Assert.That(Anchor(new Vector3Int(0, 10, 0)), Is.False);
        }
    }
}
#endif
