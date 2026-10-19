#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Install the actual saved configuration, with ownership-guarded restoration.
    // These collision/lifecycle fixtures do not install or clear weapon assets.
    sealed class AdmissionShape65Configuration : IDisposable
    {
        readonly cfg.Tables _previous, _installed;
        readonly bool _previousReady;
        static readonly PropertyInfo Tables = typeof(LubanConfigService).GetProperty("Tables");
        static readonly PropertyInfo Ready = typeof(LubanConfigService).GetProperty("IsReady");
        internal AdmissionShape65Configuration()
        {
            var service = LubanConfigService.Current; _previous = service.Tables; _previousReady = service.IsReady;
            _installed = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            Tables.SetValue(service, _installed); Ready.SetValue(service, true);
            Assert.That(service.IsReady && ReferenceEquals(service.Tables, _installed), Is.True);
        }
        public void Dispose()
        {
            var service = LubanConfigService.Current;
            if (ReferenceEquals(service.Tables, _installed)) { Tables.SetValue(service, _previous); Ready.SetValue(service, _previousReady); }
        }
    }

    // Real production owner, default physics scene and scoped admission. No injected support adapter.
    [Category("BlockFixedGridAdmissionShape65")]
    public sealed class BlockFixedGridAdmissionShape65Tests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        const float FloorTop = .08f;
        GameObject _root, _owner;
        PrototypeArena _arena;
        BlockTerrain _terrain;
        Collider _map;
        Mesh _mesh;
        AdmissionShape65Configuration _configuration;
        sealed class OwnerLease
        {
            public GameObject Owner;
            public BlockTerrain Terrain;
            public BoxCollider Helper;
        }
        readonly List<OwnerLease> _owners = new();
        readonly Dictionary<int, BoxCollider> _createdHelpers = new();
        HashSet<int> _baselineHelperIds;
        readonly bool[] _previousLayers = new bool[32];
        static readonly Vector3 Center = BlockWorld.Center(Vector3Int.zero);
        static readonly Vector3 MapPoint = new(Center.x, FloorTop, Center.z);

        [OneTimeSetUp] public void LoadConfiguration() => _configuration = new AdmissionShape65Configuration();
        [OneTimeTearDown] public void RestoreConfiguration() => _configuration?.Dispose();

        [SetUp] public void Setup()
        {
            Assert.That(Application.isPlaying, Is.False, "Run this fixture in EditMode; PlayMode lifecycle has a separate category.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _owners.Clear(); _createdHelpers.Clear();
            _baselineHelperIds = new HashSet<int>(AllHelpers().Select(c => c.GetInstanceID()));
            for (int i = 0; i < 32; i++) _previousLayers[i] = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, i);
            _root = new GameObject("Admission shape65 real map");
            _arena = _root.AddComponent<PrototypeArena>();
        }

        [TearDown] public void Cleanup()
        {
            var errors = new List<string>();
            foreach (var lease in _owners)
            {
                if (lease.Terrain == null) continue;
                try { DisposeEditOwner(lease.Terrain); }
                catch (Exception error) { errors.Add(error.ToString()); }
            }
            int[] leaked = _createdHelpers.Where(pair => pair.Value != null).Select(pair => pair.Key).ToArray();
            // Preserve the production-cleanup failure, then retire only exact references
            // journaled during this fixture's own EnableFixedGridMode calls.
            foreach (var pair in _createdHelpers)
                if (pair.Value != null) { pair.Value.enabled = false; Object.DestroyImmediate(pair.Value.gameObject); }
            if (_root != null) Object.DestroyImmediate(_root);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, i, _previousLayers[i]);
            Assert.That(errors, Is.Empty);
            Assert.That(leaked, Is.Empty, "Explicit production EditMode disposal must retire every helper before fallback cleanup.");
            Assert.That(OwnedHelpers(), Is.Zero, "No helper created during this fixture may remain.");
            Assert.That(AllHelpers().Select(c => c.GetInstanceID()).OrderBy(id => id),
                Is.EqualTo(_baselineHelperIds.OrderBy(id => id)), "Unrelated helper identities must be preserved.");
        }

        static void DisposeEditOwner(BlockTerrain terrain)
        {
            Assert.That(Application.isPlaying, Is.False);
            // Actual probe V3 established that ordinary EditMode DestroyImmediate does
            // not dispatch this MonoBehaviour's OnDestroy. Invoke the production cleanup
            // explicitly for the fixed-grid resources; only the separate PlayMode test
            // claims Unity dispatch. Do not invoke async-void OnDestroy from EditMode.
            var method = typeof(BlockTerrain).GetMethod("DisposeFixedGrid", Fields);
            Assert.That(method, Is.Not.Null); method.Invoke(terrain, null);
        }

        void BoxMap()
        {
            var go = new GameObject("Off-grid real map floor"); go.transform.SetParent(_root.transform, false);
            go.AddComponent<PaintSurface>().SurfaceId = 65101;
            var box = go.AddComponent<BoxCollider>(); box.center = new Vector3(0, FloorTop - .15f, 0);
            box.size = new Vector3(6, .3f, 6); _map = box;
        }

        void MeshMap(bool ceiling)
        {
            // Both parts belong to the SAME Collider which production may exempt as support.
            // Floor triangles face up; the wall faces the cube, or the ceiling faces down.
            var floor = new[] { new Vector3(-1.8f, FloorTop, -1.8f), new Vector3(-1.8f, FloorTop, 1.8f),
                new Vector3(1.8f, FloorTop, 1.8f), new Vector3(1.8f, FloorTop, -1.8f) };
            var obstacle = ceiling ? new[] {
                new Vector3(-.6f, .27f, -.6f), new Vector3(.6f, .27f, -.6f),
                new Vector3(.6f, .27f, .6f), new Vector3(-.6f, .27f, .6f)
            } : new[] {
                new Vector3(.28f, -.08f, -.6f), new Vector3(.28f, -.08f, .6f),
                new Vector3(.28f, .65f, .6f), new Vector3(.28f, .65f, -.6f)
            };
            _mesh = new Mesh { name = ceiling ? "Same mesh floor and low ceiling" : "Same mesh floor and side wall",
                vertices = floor.Concat(obstacle).ToArray(), triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 } };
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            var go = new GameObject(_mesh.name); go.transform.SetParent(_root.transform, false);
            go.AddComponent<PaintSurface>().SurfaceId = ceiling ? 65103 : 65102;
            var collider = go.AddComponent<MeshCollider>(); collider.sharedMesh = _mesh; _map = collider;
        }

        void Initialize()
        {
            Physics.SyncTransforms();
            _owner = new GameObject("Explicit fixed terrain owner"); _owner.transform.SetParent(_root.transform, false);
            _terrain = _owner.AddComponent<BlockTerrain>();
            var lease = new OwnerLease { Owner = _owner, Terrain = _terrain }; _owners.Add(lease);
            _terrain.Initialize(_arena);
            var before = new HashSet<int>(AllHelpers().Select(c => c.GetInstanceID()));
            try { _terrain.EnableFixedGridMode(); }
            finally
            {
                lease.Helper = Helper;
                foreach (var helper in AllHelpers())
                    if (!before.Contains(helper.GetInstanceID())) _createdHelpers[helper.GetInstanceID()] = helper;
            }
            _terrain.Tick(true, 1, 1f / 60); Physics.SyncTransforms();
        }

        BoxCollider Helper => (BoxCollider)typeof(BlockTerrain).GetField("_fixedAdmissionBox", Fields).GetValue(_terrain);
        Scene HelperScene => (Scene)typeof(BlockTerrain).GetField("_fixedAdmissionScene", Fields).GetValue(_terrain);
        static int[] NativeIds() => Resources.FindObjectsOfTypeAll<Collider>().Select(c => c.GetInstanceID()).OrderBy(id => id).ToArray();
        static BoxCollider[] AllHelpers() => Resources.FindObjectsOfTypeAll<BoxCollider>()
            .Where(c => c != null && c.name == "Fixed grid admission shape").ToArray();
        int OwnedHelpers() => AllHelpers().Count(c => !_baselineHelperIds.Contains(c.GetInstanceID()));

        BlockFixedGridMapSupport Support()
        {
            Assert.That(_map.Raycast(new Ray(new Vector3(Center.x, .18f, Center.z), Vector3.down), out var hit, .5f), Is.True);
            Assert.That(hit.collider, Is.SameAs(_map)); Assert.That(hit.point.y, Is.EqualTo(FloorTop).Within(.00001f));
            Assert.That(hit.normal.y, Is.GreaterThan(.999f));
            Assert.That(_terrain.TryGetSurfacePaint(_map, hit.point, hit.normal, out var paint), Is.True);
            return new BlockFixedGridMapSupport(paint.Key.FaceId, hit.point.y, paint.Key);
        }

        void Paint()
        {
            var support = Support();
            Assert.That(_terrain.PaintSurfaceCell(support.SurfaceKey, 1), Is.True);
        }

        internal static bool Penetration(BlockTerrain terrain, Vector3 center, Collider map, out Vector3 direction, out float depth)
        {
            var method = typeof(BlockTerrain).GetMethod("TryFixedAdmissionPenetration", Fields);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { center, map, false, Vector3.zero, 0f };
            Assert.That((bool)method.Invoke(terrain, arguments), Is.True, "The real production narrow-phase call must be operational.");
            direction = (Vector3)arguments[3]; depth = (float)arguments[4];
            return (bool)arguments[2];
        }

        static void AssertInvisible(BoxCollider helper)
        {
            Assert.That(helper.enabled, Is.False);
            var at = helper.transform.position;
            Assert.That(Physics.OverlapBox(at, Vector3.one * .2f, Quaternion.identity, ~0,
                QueryTriggerInteraction.Collide).Any(c => c == helper), Is.False);
            Assert.That(Physics.RaycastAll(at - Vector3.right, Vector3.right, 2, ~0,
                QueryTriggerInteraction.Collide).Any(h => h.collider == helper), Is.False);
            Assert.That(Physics.BoxCastAll(at - Vector3.right, Vector3.one * .05f, Vector3.right,
                Quaternion.identity, 2, ~0, QueryTriggerInteraction.Collide).Any(h => h.collider == helper), Is.False);
        }

        [Test] public void ScopedShapeProvesLegalEmbeddingAndIsInvisibleBeforeAndAfterEachCall()
        {
            BoxMap(); Initialize(); var helper = Helper; var scene = HelperScene;
            Assert.That(helper != null && !helper.enabled && helper.isTrigger && helper.gameObject.activeInHierarchy, Is.True);
            Assert.That(helper.transform.parent, Is.Null); Assert.That(helper.transform.lossyScale, Is.EqualTo(Vector3.one));
            Assert.That(helper.attachedRigidbody, Is.Null); Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            Assert.That(scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene), Is.True);
            Assert.That(helper.gameObject.scene.handle, Is.EqualTo(_owner.scene.handle));
            AssertInvisible(helper);
            // Real positive controls prove that these exact query shapes can detect an enabled helper.
            try
            {
                helper.enabled = true; Physics.SyncTransforms(); var at = helper.transform.position;
                Assert.That(Physics.OverlapBox(at, Vector3.one * .2f, Quaternion.identity, ~0,
                    QueryTriggerInteraction.Collide), Does.Contain(helper));
                Assert.That(Physics.RaycastAll(at - Vector3.right, Vector3.right, 2, ~0,
                    QueryTriggerInteraction.Collide).Any(h => h.collider == helper), Is.True);
                Assert.That(Physics.BoxCastAll(at - Vector3.right, Vector3.one * .05f, Vector3.right,
                    Quaternion.identity, 2, ~0, QueryTriggerInteraction.Collide).Any(h => h.collider == helper), Is.True);
            }
            finally { helper.enabled = false; }
            AssertInvisible(helper);
            Assert.That(Penetration(_terrain, Center, _map, out var direction, out float depth), Is.True);
            Assert.That(direction, Is.EqualTo(Vector3.up)); Assert.That(depth, Is.EqualTo(.0799f).Within(.0001f));
            AssertInvisible(helper);
            Assert.That(Penetration(_terrain, Center + Vector3.up, _map, out _, out _), Is.False);
            AssertInvisible(helper);
            Paint(); int[] before = NativeIds();
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(1)); Assert.That(NativeIds(), Is.EqualTo(before));
            AssertInvisible(helper);
            Assert.That(OwnedHelpers(), Is.EqualTo(1)); Assert.That(_root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void SameSupportingMeshCannotExemptItsSideWallOrLowCeiling(bool ceiling)
        {
            MeshMap(ceiling); Initialize(); var support = Support(); Paint();
            var helper = Helper; AssertInvisible(helper);
            Assert.That(Penetration(_terrain, Center, _map, out var direction, out float depth), Is.True,
                "The scoped enabled helper must return a genuine same-mesh proof; unresolved geometry is a failure.");
            float maximumVertical = support.Height - (Center.y - (BlockWorld.CellSize * .5f - .0001f));
            bool oldMtdRejects = Vector3.Dot(direction, Vector3.up) < .99f || direction.y <= .05f ||
                depth / Mathf.Max(.05f, direction.y) > maximumVertical + .001f;
            TestContext.Out.WriteLine("Actual same-mesh native MTV: direction=" + direction.ToString("R") +
                " depth=" + depth.ToString("R") + " oldSideOrDepthReject=" + oldMtdRejects);
            AssertExtraFaceRejected(_map, Center, MapPoint, Vector3.up);
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("support-side-overlap").Or.EqualTo("support-depth").Or.EqualTo("support-extra-face"));
            var world = _terrain.FixedBlocks; uint revision = world.Revision, paintRevision = world.Surfaces.Revision;
            int pink = world.PinkCount, blue = world.BlueCount; ulong nextId = world.NextId;
            var result = _terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true);
            Assert.That(result.Added, Is.Zero); Assert.That(world.Count, Is.Zero); Assert.That(world.NextId, Is.EqualTo(nextId));
            Assert.That(world.Revision, Is.EqualTo(revision)); Assert.That(world.Surfaces.Revision, Is.EqualTo(paintRevision));
            Assert.That(world.PinkCount, Is.EqualTo(pink)); Assert.That(world.BlueCount, Is.EqualTo(blue));
            Assert.That(_terrain.FailedTransactions, Is.Zero); AssertInvisible(helper);
        }

        void AssertExtraFaceRejected(Collider collider, Vector3 center, Vector3 supportPoint, Vector3 supportNormal)
        {
            var method = typeof(BlockTerrain).GetMethod("FixedAdmissionMeshClear", Fields);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { collider, center, Vector3.one * (BlockWorld.CellSize * .5f - .0001f), supportPoint, supportNormal, null };
            Assert.That((bool)method.Invoke(_terrain, arguments), Is.False, "The complete production finite-face proof must reject this actual non-support barrier.");
            Assert.That((string)arguments[5], Is.EqualTo("support-extra-face"));
            AssertInvisible(Helper);
        }

        [TestCase(false)] [TestCase(true)]
        public void AConcaveFloorAndAnOutOfFootprintCeilingRemainLegal(bool outsideCeiling)
        {
            MeshMap(true);
            if (outsideCeiling)
            {
                var vertices = _mesh.vertices;
                for (int i = 4; i < vertices.Length; i++) vertices[i] += Vector3.right * 3;
                _mesh.vertices = vertices;
            }
            else _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            var meshCollider = (MeshCollider)_map; meshCollider.sharedMesh = null; meshCollider.sharedMesh = _mesh;
            Initialize(); var support = Support(); Paint();
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.True, reason);
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            AssertInvisible(Helper);
        }

        System.Collections.IDictionary MeshProofs => (System.Collections.IDictionary)typeof(BlockTerrain)
            .GetField("_fixedAdmissionMeshProofs", Fields).GetValue(_terrain);

        static void AssertProofStorage(object proof, bool created)
        {
            object data = proof.GetType().GetField("Data", BindingFlags.Instance | BindingFlags.Public).GetValue(proof);
            foreach (string name in new[] { "Features", "Axes", "Vertices", "Triangles", "Nodes", "FeatureIndices" })
            {
                object array = data.GetType().GetField(name, Fields).GetValue(data);
                Assert.That((bool)array.GetType().GetProperty("IsCreated").GetValue(array), Is.EqualTo(created), name);
            }
        }

        void RecookCeiling(bool enabled)
        {
            _mesh.triangles = enabled ? new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 } : new[] { 0, 1, 2, 0, 2, 3 };
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            var source = (MeshCollider)_map; source.sharedMesh = null; source.sharedMesh = _mesh; Physics.SyncTransforms();
        }

        [Test] public void ExplicitMapChangeRebakesTheSameMeshAndAffectsTheNextAdmission()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.True, reason);
            var firstProof = MeshProofs[_map]; AssertProofStorage(firstProof, true);
            var changed = _map.bounds; RecookCeiling(true); changed.Encapsulate(_map.bounds);
            Assert.That(_terrain.NotifyFixedMapSupportChanged(changed), Is.True, _terrain.FixedBlocks.LastFailure);
            Assert.That(MeshProofs[_map], Is.Not.SameAs(firstProof)); AssertProofStorage(firstProof, false);
            AssertExtraFaceRejected(_map, Center, MapPoint, Vector3.up);
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("support-extra-face"));
            var secondProof = MeshProofs[_map]; changed = _map.bounds; RecookCeiling(false); changed.Encapsulate(_map.bounds);
            Assert.That(_terrain.NotifyFixedMapSupportChanged(changed), Is.True, _terrain.FixedBlocks.LastFailure);
            Assert.That(MeshProofs[_map], Is.Not.SameAs(secondProof)); AssertProofStorage(secondProof, false);
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.True, reason);
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.Zero); AssertInvisible(Helper);
        }

        [Test] public void ExplicitMapBakeFailureKeepsThePublishedCommitAndBothOldQueryCaches()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            var before = PublishedState(); Vector3 blankPoint = MapPoint + Vector3.right * .6f;
            Assert.That(_terrain.TryGetSurfacePaint(_map, blankPoint, Vector3.up, out var blank), Is.True);
            Assert.That(blank.Owner, Is.Zero); var blankKey = blank.Key;
            var world = _terrain.FixedBlocks; uint revision = world.Revision, paintRevision = world.Surfaces.Revision;
            int pink = world.PinkCount, blue = world.BlueCount; ulong nextId = world.NextId;
            var proofs = MeshProofs; var proof = proofs[_map]; AssertProofStorage(proof, true);
            var layers = (System.Collections.IDictionary)typeof(BlockTerrain).GetField("_fixedMapLayers", Fields).GetValue(_terrain);
            var column = new Vector2Int(0, 0); Assert.That(layers.Contains(column), Is.True); var oldLayer = layers[column];
            var changed = _map.bounds; _mesh.UploadMeshData(true); Assert.That(_mesh.isReadable, Is.False);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("map support geometry bake failed: .*not readable"));
            Assert.That(_terrain.NotifyFixedMapSupportChanged(changed), Is.False);
            Assert.That(MeshProofs, Is.SameAs(proofs)); Assert.That(MeshProofs[_map], Is.SameAs(proof)); AssertProofStorage(proof, true);
            Assert.That(MapLayers, Is.SameAs(layers)); Assert.That(layers[column], Is.SameAs(oldLayer));
            Assert.That(world.Count, Is.EqualTo(1)); Assert.That(world.Revision, Is.EqualTo(revision)); Assert.That(world.NextId, Is.EqualTo(nextId));
            Assert.That(world.Surfaces.Revision, Is.EqualTo(paintRevision));
            Assert.That(world.PinkCount, Is.EqualTo(pink)); Assert.That(world.BlueCount, Is.EqualTo(blue));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(1)); AssertInvisible(Helper);
            AssertPublishedState(before); AssertDirtyMapBlocksEveryCreationEntrance(before, support, blankPoint, blankKey);
        }

        [Test] public void ExplicitMapRefreshRecoversDetectedProofDriftWithoutPoisoningTheNativeShape()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.True, reason);
            var changed = _map.bounds; _map.transform.position += Vector3.right * .01f; Physics.SyncTransforms(); changed.Encapsulate(_map.bounds);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Supporting mesh proof unavailable; construction rejected before commit"));
            for (int i = 0; i < 2; i++)
            {
                Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.False);
                Assert.That(reason, Is.EqualTo("admission-mesh-proof-unavailable"));
            }
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(1)); AssertInvisible(Helper);
            Assert.That(_terrain.NotifyFixedMapSupportChanged(changed), Is.True, _terrain.FixedBlocks.LastFailure);
            Assert.That(_terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.True, reason);
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(1)); AssertInvisible(Helper);
        }

        [Test] public void RoundClearReusesImmutableProofAndExplicitDisposeReleasesItIdempotently()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var proof = MeshProofs[_map]; AssertProofStorage(proof, true);
            _terrain.EnableFixedGridMode(); Assert.That(MeshProofs[_map], Is.SameAs(proof));
            _terrain.Clear(_terrain.Round + 1); Assert.That(MeshProofs[_map], Is.SameAs(proof)); AssertProofStorage(proof, true);
            var dispose = typeof(BlockTerrain).GetMethod("DisposeFixedGrid", Fields); Assert.That(dispose, Is.Not.Null);
            dispose.Invoke(_terrain, null); Assert.That(MeshProofs.Count, Is.Zero); AssertProofStorage(proof, false);
            Assert.That(Helper == null, Is.True);
            Assert.DoesNotThrow(() => dispose.Invoke(_terrain, null)); Assert.That(MeshProofs.Count, Is.Zero); AssertProofStorage(proof, false);
        }

        // Insert into the existing BlockFixedGridAdmissionShape65Tests fixture.
        // Uses its real map, terrain, configuration and cleanup; no injected environment.
        System.Collections.IDictionary MapLayers => (System.Collections.IDictionary)typeof(BlockTerrain)
            .GetField("_fixedMapLayers", Fields).GetValue(_terrain);
        bool MapRefreshFailed => (bool)typeof(BlockTerrain)
            .GetField("_fixedAdmissionMapRefreshFailed", Fields).GetValue(_terrain);
        object ActiveMapChange => typeof(BlockTerrain).GetField("_activeFixedMapChange", Fields).GetValue(_terrain);

        sealed class PublishedFixedState
        {
            internal BlockFixedGridWorld World;
            internal BlockWorld Storage;
            internal BlockSurfaceWorld Surfaces;
            internal BlockFixedGridSnapshot Snapshot;
            internal int Count, Pink, Blue;
        }

        PublishedFixedState PublishedState()
        {
            var world = _terrain.FixedBlocks;
            return new PublishedFixedState { World = world, Storage = world.Storage, Surfaces = world.Surfaces,
                Snapshot = world.Capture(true), Count = world.Count, Pink = world.PinkCount, Blue = world.BlueCount };
        }

        void AssertPublishedState(PublishedFixedState before)
        {
            var world = _terrain.FixedBlocks; var actual = world.Capture(true); var expected = before.Snapshot;
            Assert.That(world, Is.SameAs(before.World));
            Assert.That(world.Storage, Is.SameAs(before.Storage)); Assert.That(_terrain.World, Is.SameAs(before.Storage));
            Assert.That(world.Surfaces, Is.SameAs(before.Surfaces));
            Assert.That(world.Count, Is.EqualTo(before.Count));
            Assert.That(world.PinkCount, Is.EqualTo(before.Pink)); Assert.That(world.BlueCount, Is.EqualTo(before.Blue));
            Assert.That(actual.Full, Is.EqualTo(expected.Full)); Assert.That(actual.Frozen, Is.EqualTo(expected.Frozen));
            Assert.That(actual.Epoch, Is.EqualTo(expected.Epoch)); Assert.That(actual.Round, Is.EqualTo(expected.Round));
            Assert.That(actual.BaseRevision, Is.EqualTo(expected.BaseRevision)); Assert.That(actual.Revision, Is.EqualTo(expected.Revision));
            Assert.That(actual.SurfaceBaseRevision, Is.EqualTo(expected.SurfaceBaseRevision));
            Assert.That(actual.SurfaceRevision, Is.EqualTo(expected.SurfaceRevision)); Assert.That(actual.NextId, Is.EqualTo(expected.NextId));
            Assert.That(actual.Upserts, Is.EqualTo(expected.Upserts)); Assert.That(actual.Removed, Is.EqualTo(expected.Removed));
            Assert.That(actual.SurfaceBytes, Is.EqualTo(expected.SurfaceBytes), "Every committed paint byte remains unchanged.");
            foreach (var state in expected.Upserts)
            {
                Assert.That(world.TryGet(state.Handle, out var found), Is.True);
                Assert.That(found, Is.EqualTo(state), "Stable identity and all state fields remain unchanged.");
            }
        }

        // Call after either capture failure or late reconciliation failure. The upper
        // candidate is nonembedded: this guards against putting the dirty check only
        // inside the supporting-collider exemption or only on the public impact path.
        void AssertDirtyMapBlocksEveryCreationEntrance(PublishedFixedState before,
            BlockFixedGridMapSupport support, Vector3 blankPoint, BlockSurfaceKey blankKey)
        {
            var world = _terrain.FixedBlocks;
            Assert.That(MapRefreshFailed, Is.True); Assert.That(ActiveMapChange, Is.Null);
            Assert.That(world.Environment.TryGetMapSupport(new Vector3Int(0, 1, 0), out _), Is.False);
            Assert.That(world.Environment.PlacementAllowed(new Vector3Int(0, 1, 0), support, false, false, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("map-support-refresh-unavailable"));
            Assert.That(((IBlockFixedGridPaintAdmission)world.Environment).PaintAllowed(blankKey, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("map-support-refresh-unavailable"));
            Assert.That(world.Build(new[] { new Vector3Int(0, 1, 0) }, 1, 1).Added, Is.Zero);
            Assert.That(_terrain.BuildFixedImpact(blankPoint, Vector3.up, 0, 1, 1, true).Added, Is.Zero);
            Assert.That(world.Surfaces.TryGet(blankKey, out var blank), Is.True); Assert.That(blank.Owner, Is.Zero);
            AssertPublishedState(before); AssertInvisible(Helper);
        }

        [Test] public void ARealLateMapReconciliationFailureRetainsBothPublishedCachesUntilSuccessfulRetry()
        {
            // Reuse RecookCeiling's triangle toggle with MeshMap(false): the extra
            // vertices form a vertical wall, which cannot become a downward-ray
            // support regardless of the global backface-query preference.
            MeshMap(false); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            var before = PublishedState(); var proofs = MeshProofs; var proof = proofs[_map];
            var layers = MapLayers; var column = new Vector2Int(0, 0);
            Assert.That(layers.Contains(column), Is.True); var oldLayer = layers[column]; AssertProofStorage(proof, true);
            Vector3 blankPoint = MapPoint + Vector3.right * .6f;
            Assert.That(_terrain.TryGetSurfacePaint(_map, blankPoint, Vector3.up, out var blank), Is.True);
            Assert.That(blank.Owner, Is.Zero); var blankKey = blank.Key;
            var changed = _map.bounds; RecookCeiling(true); changed.Encapsulate(_map.bounds);

            // Capture succeeds; real existing-block reconciliation encounters the new
            // finite side wall and rejects placement before paint classification.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Atomic commit rejected: support-extra-face"));
            Assert.That(_terrain.NotifyFixedMapSupportChanged(changed), Is.False);
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(1));
            Assert.That(MeshProofs, Is.SameAs(proofs)); Assert.That(MeshProofs[_map], Is.SameAs(proof)); AssertProofStorage(proof, true);
            Assert.That(MapLayers, Is.SameAs(layers)); Assert.That(MapLayers[column], Is.SameAs(oldLayer));
            AssertPublishedState(before);
            AssertDirtyMapBlocksEveryCreationEntrance(before, support, blankPoint, blankKey);
            Assert.That(MeshProofs, Is.SameAs(proofs)); Assert.That(MeshProofs[_map], Is.SameAs(proof)); AssertProofStorage(proof, true);
            Assert.That(MapLayers, Is.SameAs(layers)); Assert.That(MapLayers[column], Is.SameAs(oldLayer));

            RecookCeiling(false);
            // Deliberately excludes (0,0). Its previously failed bounds must remain
            // pending; passing just this fresh bounds cannot invalidate that column.
            var unrelatedRetryBounds = new Bounds(new Vector3(1.2f, FloorTop, 1.2f), Vector3.one * .02f);
            Assert.That(unrelatedRetryBounds.Contains(MapPoint), Is.False);
            Assert.That(_terrain.NotifyFixedMapSupportChanged(unrelatedRetryBounds), Is.True, before.World.LastFailure);
            Assert.That(MapRefreshFailed, Is.False); Assert.That(ActiveMapChange, Is.Null);
            Assert.That(MeshProofs, Is.Not.SameAs(proofs)); Assert.That(MeshProofs[_map], Is.Not.SameAs(proof));
            AssertProofStorage(proof, false); AssertProofStorage(MeshProofs[_map], true);
            Assert.That(MapLayers, Is.Not.SameAs(layers)); Assert.That(MapLayers[column], Is.Not.SameAs(oldLayer));
            AssertPublishedState(before);
            Assert.That(before.World.Environment.TryGetMapSupport(new Vector3Int(0, 1, 0), out _), Is.True);
            Assert.That(before.World.Build(new[] { new Vector3Int(0, 1, 0) }, 1, 1).Added, Is.EqualTo(1));
            Assert.That(before.World.Count, Is.EqualTo(2)); Assert.That(_terrain.FailedTransactions, Is.EqualTo(1));
            AssertInvisible(Helper);
        }

        void AdmissionMeshFromFaces(string label, int surfaceId, Vector3[] vertices, int[] triangles)
        {
            _mesh = new Mesh { name = label, vertices = vertices, triangles = triangles };
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            var map = new GameObject(label); map.transform.SetParent(_root.transform, false);
            map.AddComponent<PaintSurface>().SurfaceId = surfaceId;
            var collider = map.AddComponent<MeshCollider>(); collider.convex = false;
            collider.sharedMesh = _mesh; _map = collider;
            Physics.SyncTransforms();
        }

        void AssertActualCompleteMeshAdmission(bool allowed, string label)
        {
            Initialize(); var support = Support(); Paint();
            var helper = Helper; AssertInvisible(helper);
            Assert.That(Penetration(_terrain, Center, _map, out var direction, out float depth), Is.True,
                "This actual floor intersection must reach the operational native penetration path.");
            TestContext.Out.WriteLine(label + " native MTV: direction=" + direction.ToString("R") + " depth=" + depth.ToString("R"));
            var proof = typeof(BlockTerrain).GetMethod("FixedAdmissionMeshClear", Fields);
            Assert.That(proof, Is.Not.Null);
            object[] arguments = { _map, Center, Vector3.one * (BlockWorld.CellSize * .5f - .0001f), MapPoint, Vector3.up, null };
            Assert.That((bool)proof.Invoke(_terrain, arguments), Is.EqualTo(allowed), "Actual clipped finite-face production proof: " + label);
            if (allowed) Assert.That(arguments[5], Is.Null);
            else Assert.That((string)arguments[5], Is.EqualTo("support-extra-face"));
            AssertInvisible(helper);
            bool actual = _terrain.FixedBlocks.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason);
            TestContext.Out.WriteLine(label + " native-plus-proof PlacementAllowed=" + actual + " reason=" + reason);
            Assert.That(actual, Is.EqualTo(allowed), "The real complete admission path must agree; a native-MTD rejection of a legal case remains a real failure.");
            var world = _terrain.FixedBlocks; uint revision = world.Revision, paintRevision = world.Surfaces.Revision;
            int pink = world.PinkCount, blue = world.BlueCount; ulong nextId = world.NextId;
            int[] nativeBefore = NativeIds();
            var result = _terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true);
            Assert.That(result.Added, Is.EqualTo(allowed ? 1 : 0));
            Assert.That(world.Count, Is.EqualTo(allowed ? 1 : 0));
            Assert.That(world.Surfaces.Revision, Is.EqualTo(paintRevision));
            Assert.That(world.BlueCount, Is.EqualTo(blue)); Assert.That(NativeIds(), Is.EqualTo(nativeBefore));
            if (allowed)
            {
                Assert.That(world.TryGet(Vector3Int.zero, out var block), Is.True);
                Assert.That(block.Handle.IsValid, Is.True); Assert.That(block.Owner, Is.EqualTo(1));
                Assert.That(block.Support.Height, Is.EqualTo(FloorTop).Within(.000001f));
                Assert.That(world.PinkCount, Is.EqualTo(pink + 1));
                Assert.That(world.Revision, Is.GreaterThan(revision)); Assert.That(world.NextId, Is.GreaterThan(nextId));
            }
            else
            {
                Assert.That(reason, Is.EqualTo("support-extra-face"));
                Assert.That(world.Revision, Is.EqualTo(revision)); Assert.That(world.NextId, Is.EqualTo(nextId));
                Assert.That(world.PinkCount, Is.EqualTo(pink));
            }
            Assert.That(_terrain.FailedTransactions, Is.Zero); AssertInvisible(helper);
        }


        bool CachedMeshClear(Vector3 center, Vector3 half, Vector3 point, Vector3 normal, out string reason)
        {
            var method = typeof(BlockTerrain).GetMethod("FixedAdmissionMeshClear", Fields);
            object[] args = { _map, center, half, point, normal, null };
            bool result = (bool)method.Invoke(_terrain, args); reason = (string)args[5]; return result;
        }

        int ProofCacheEntries(object proof) => (int)proof.GetType().GetProperty("QueryCacheEntries").GetValue(proof);

        [Test] public void MeshProofCacheUsesEveryExactFloatComponentAndCachesBothResults()
        {
            MeshMap(false); Initialize();
            Vector3 half = Vector3.one * (BlockWorld.CellSize * .5f - .0001f);
            Vector3 clearCenter = Center + Vector3.left * .6f;
            var before = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("support-extra-face"));
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("support-extra-face"));
            Assert.That(CachedMeshClear(clearCenter, half, MapPoint, Vector3.up, out reason), Is.True, reason);
            Assert.That(CachedMeshClear(clearCenter, half, MapPoint, Vector3.up, out reason), Is.True, reason);
            var warm = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(warm.Misses - before.Misses, Is.EqualTo(2));
            Assert.That(warm.Hits - before.Hits, Is.EqualTo(2));
            // Every argument component differs by exactly one IEEE-754 bit. Approximate
            // Vector3 equality or a cell-only key would incorrectly reuse these queries.
            var baseline = new[] { clearCenter, half, MapPoint, Vector3.up };
            for (int argument = 0; argument < 4; argument++) for (int component = 0; component < 3; component++)
            {
                var values = (Vector3[])baseline.Clone(); var changed = values[argument];
                int bits = Unity.Mathematics.math.asint(changed[component]);
                changed[component] = Unity.Mathematics.math.asfloat(bits + 1); values[argument] = changed;
                Assert.That(CachedMeshClear(values[0], values[1], values[2], values[3], out reason), Is.True, reason);
            }
            var after = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(after.Misses - warm.Misses, Is.EqualTo(12));
            Assert.That(after.Hits, Is.EqualTo(warm.Hits));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [Test] public void WarmMeshProofStillObservesLiveTemporaryObstacleEnableDisableAndDelete()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            var environment = _terrain.FixedBlocks.Environment;
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.True, reason);
            var warm = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            // Added AFTER initialization: deliberately not a member of the authored map.
            var obstacle = new GameObject("Admission68 fresh temporary obstacle"); obstacle.transform.SetParent(_root.transform, false);
            var collider = obstacle.AddComponent<BoxCollider>(); collider.center = Center; collider.size = Vector3.one * .12f;
            Physics.SyncTransforms();
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("obstacle"));
            collider.enabled = false; Physics.SyncTransforms();
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.True, reason);
            collider.enabled = true; Physics.SyncTransforms();
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("obstacle"));
            Object.DestroyImmediate(obstacle); Physics.SyncTransforms();
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.True, reason);
            var after = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(after.Hits - warm.Hits, Is.GreaterThanOrEqualTo(4));
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(1)); AssertInvisible(Helper);
        }

        [Test] public void CachedMeshProofCannotBypassLiveDriftReplacementOrFailedMapRefresh()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            var half = Vector3.one * (BlockWorld.CellSize * .5f - .0001f);
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out var reason), Is.True, reason);
            object oldProof = MeshProofs[_map]; Assert.That(ProofCacheEntries(oldProof), Is.GreaterThan(0));
            var before = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            var changed = _map.bounds; _map.transform.position += Vector3.right * .01f; Physics.SyncTransforms(); changed.Encapsulate(_map.bounds);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Supporting mesh proof unavailable; construction rejected before commit"));
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("admission-mesh-proof-unavailable"));
            Assert.That(_terrain.CaptureFixedAdmissionMeshQueryCacheStatistics().Queries, Is.EqualTo(before.Queries),
                "Live Matches must run before querying even a warmed proof.");
            Assert.That(_terrain.NotifyFixedMapSupportChanged(changed), Is.True, _terrain.FixedBlocks.LastFailure);
            Assert.That(MeshProofs[_map], Is.Not.SameAs(oldProof)); Assert.That(ProofCacheEntries(oldProof), Is.Zero);
            AssertProofStorage(oldProof, false);
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out reason), Is.True, reason);
            object retainedProof = MeshProofs[_map]; before = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            _mesh.UploadMeshData(true);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Atomic commit rejected: map support geometry bake failed"));
            Assert.That(_terrain.NotifyFixedMapSupportChanged(_map.bounds), Is.False);
            Assert.That(MeshProofs[_map], Is.SameAs(retainedProof));
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("map-support-refresh-unavailable"));
            Assert.That(_terrain.CaptureFixedAdmissionMeshQueryCacheStatistics().Queries, Is.EqualTo(before.Queries),
                "A failed staged refresh cannot consult the retained clear result.");
            AssertInvisible(Helper);
        }

        [Test] public void MeshProofCacheFaultsAreNeverPublishedAsClearResults()
        {
            MeshMap(true); RecookCeiling(false); Initialize();
            var half = Vector3.one * (BlockWorld.CellSize * .5f - .0001f);
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out var reason), Is.True, reason);
            var before = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Supporting mesh proof unavailable; construction rejected before commit"));
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.zero, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("admission-mesh-proof-unavailable"));
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.zero, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("admission-mesh-proof-unavailable"));
            var failed = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(failed.Entries, Is.EqualTo(before.Entries));
            Assert.That(failed.Misses - before.Misses, Is.EqualTo(2));
            Assert.That(failed.Hits, Is.EqualTo(before.Hits));
            Assert.That(CachedMeshClear(Center, half, MapPoint, Vector3.up, out reason), Is.True, reason);
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(1)); AssertInvisible(Helper);
        }

        [Test] public void MeshProofCacheCapacityEvictsOnlyMemoizedWorkAndRechecksRealAdmission()
        {
            MeshMap(true); RecookCeiling(false); Initialize(); var support = Support(); Paint();
            var environment = _terrain.FixedBlocks.Environment;
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.True, reason);
            var half = Vector3.one * (BlockWorld.CellSize * .5f - .0001f);
            var proof = MeshProofs[_map];
            int maximum = (int)proof.GetType().GetField("MaximumQueryCacheEntries").GetRawConstantValue();
            Assert.That(maximum, Is.EqualTo(4096));
            for (int i = 0; i < maximum; i++)
            {
                Assert.That(CachedMeshClear(new Vector3(20 + i, Center.y, Center.z), half, MapPoint, Vector3.up, out reason), Is.True, reason);
                Assert.That(ProofCacheEntries(proof), Is.InRange(1, maximum));
            }
            var evicted = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(evicted.CapacityClears, Is.GreaterThan(0));
            Assert.That(evicted.SumOfProofPeakEntries, Is.EqualTo(maximum));
            Assert.That(environment.PlacementAllowed(Vector3Int.zero, support, true, false, out reason), Is.True, reason);
            var reread = _terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            Assert.That(reread.Misses, Is.EqualTo(evicted.Misses + 1), "The evicted local proof is computed again.");
            Assert.That(_terrain.BuildFixedImpact(MapPoint, Vector3.up, 0, 1, 1, true).Added, Is.EqualTo(1));
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(1)); Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [Test] public void ClosedThinConcavePlatformPermitsItsRealBottomLayerEmbedding()
        {
            const float edge = 1.8f, underside = .02f;
            AdmissionMeshFromFaces("Closed thin supporting platform", 65110, new[] {
                new Vector3(-edge, underside, -edge), new Vector3(-edge, underside, edge),
                new Vector3(edge, underside, edge), new Vector3(edge, underside, -edge),
                new Vector3(-edge, FloorTop, -edge), new Vector3(-edge, FloorTop, edge),
                new Vector3(edge, FloorTop, edge), new Vector3(edge, FloorTop, -edge)
            }, new[] {
                4,5,6, 4,6,7, 0,2,1, 0,3,2,
                0,1,5, 0,5,4, 3,7,6, 3,6,2,
                0,4,7, 0,7,3, 1,2,6, 1,6,5
            });
            var source = (MeshCollider)_map;
            Assert.That(source.convex, Is.False); Assert.That(source.bounds.min.y, Is.EqualTo(underside).Within(.000001f));
            Assert.That(source.bounds.max.y, Is.EqualTo(FloorTop).Within(.000001f));
            Assert.That(Center.y - (BlockWorld.CellSize * .5f - .0001f), Is.LessThan(underside));
            AssertActualCompleteMeshAdmission(true, "Closed thin concave support top=.08 underside=.02");
        }

        [TestCase(false)] [TestCase(true)]
        public void CrossingTriangleUsesOnlyItsFinitePortionAboveTheActualSupport(bool aboveEntersCell)
        {
            AdmissionMeshFromFaces("Finite cross-plane support boundary", aboveEntersCell ? 65112 : 65111, new[] {
                new Vector3(-1.8f, FloorTop, -1.8f), new Vector3(-1.8f, FloorTop, 1.8f),
                new Vector3(1.8f, FloorTop, 1.8f), new Vector3(1.8f, FloorTop, -1.8f),
                new Vector3(-.5f, .02f, .15f), new Vector3(.5f, .02f, .15f),
                new Vector3(aboveEntersCell ? .5f : 3f, .2f, .15f)
            }, new[] { 0,1,2, 0,2,3, 4,5,6 });
            AssertActualCompleteMeshAdmission(!aboveEntersCell,
                aboveEntersCell ? "Above-support finite triangle intersects admission cell" : "Only below-support portion intersects admission cell");
        }

        [Test] public void ResetReusesOneShapeAndExplicitEditModeDisposalPreservesTheGameplayScene()
        {
            BoxMap(); Initialize(); var helper = Helper; var scene = HelperScene; int id = helper.GetInstanceID();
            _terrain.EnableFixedGridMode(); Assert.That(Helper, Is.SameAs(helper)); Assert.That(OwnedHelpers(), Is.EqualTo(1));
            _terrain.Clear(_terrain.Round + 1); Assert.That(Helper, Is.SameAs(helper)); Assert.That(HelperScene.handle, Is.EqualTo(scene.handle));
            Assert.That(!helper.enabled && scene.isLoaded, Is.True);
            DisposeEditOwner(_terrain);
            Assert.That(helper == null, Is.True, "Explicit production EditMode disposal owns the independent helper.");
            Object.DestroyImmediate(_owner); _owner = null; _terrain = null;
            Assert.That(helper == null, Is.True); Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            Assert.That(OwnedHelpers(), Is.Zero);
            Initialize(); Assert.That(Helper.GetInstanceID(), Is.Not.EqualTo(id)); Assert.That(OwnedHelpers(), Is.EqualTo(1));
        }

        [Test] public void OwnerTransformsCannotScaleTheShapeAndMapPoseStillControlsPenetration()
        {
            BoxMap(); Initialize(); var helper = Helper;
            _owner.transform.SetPositionAndRotation(new Vector3(3, 2, -1), Quaternion.Euler(17, 31, 9));
            _owner.transform.localScale = new Vector3(2, .5f, 3);
            Assert.That(helper.transform.parent, Is.Null); Assert.That(helper.transform.lossyScale, Is.EqualTo(Vector3.one));
            Assert.That(helper.transform.rotation, Is.EqualTo(Quaternion.identity));
            var box = (BoxCollider)_map;
            box.transform.SetPositionAndRotation(new Vector3(-2, 1, 3), Quaternion.Euler(0, 31, 14));
            box.transform.localScale = new Vector3(1.4f, .75f, .9f); Physics.SyncTransforms();
            Vector3 normal = box.transform.rotation * Vector3.up;
            Vector3 top = box.transform.TransformPoint(box.center + Vector3.up * box.size.y * .5f);
            Vector3 candidate = top + normal * .07f;
            Assert.That(Penetration(_terrain, candidate, box, out var direction, out float depth), Is.True);
            float projection = (BlockWorld.CellSize * .5f - .0001f) * (Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z));
            Assert.That(Vector3.Dot(direction, normal), Is.GreaterThan(.9999f)); Assert.That(depth, Is.EqualTo(projection - .07f).Within(.001f)); AssertInvisible(helper);
        }

        [TestCase("enabled")] [TestCase("inactive")] [TestCase("size")] [TestCase("scale")]
        [TestCase("trigger")] [TestCase("parent")] [TestCase("destroyed")] [TestCase("reentrant")]
        public void AnInvalidScopedShapeCannotSilentlyAdmitThroughTheSupportExemption(string invalid)
        {
            BoxMap(); Initialize(); var support = Support(); Paint(); var helper = Helper;
            switch (invalid)
            {
                case "enabled": helper.enabled = true; break;
                case "inactive": helper.gameObject.SetActive(false); break;
                case "size": helper.size *= 2; break;
                case "scale": helper.transform.localScale = new Vector3(1, 2, 1); break;
                case "trigger": helper.isTrigger = false; break;
                case "parent": helper.transform.SetParent(_owner.transform, false); break;
                case "reentrant": typeof(BlockTerrain).GetField("_fixedAdmissionInUse", Fields).SetValue(_terrain, true); break;
                case "destroyed": Object.DestroyImmediate(helper.gameObject); break;
            }
            var world = _terrain.FixedBlocks; uint revision = world.Revision, paintRevision = world.Surfaces.Revision;
            ulong nextId = world.NextId; int score = world.PinkCount;
            LogAssert.Expect(LogType.Error, "[BLOCK FIXED] Admission shape unavailable; construction rejected before commit.");
            Assert.That(world.Environment.PlacementAllowed(Vector3Int.zero, support, true, false, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("admission-shape-unavailable"));
            Assert.That(world.Build(new[] { Vector3Int.zero }, 1, 1).Added, Is.Zero);
            Assert.That(world.Count, Is.Zero); Assert.That(world.Revision, Is.EqualTo(revision)); Assert.That(world.NextId, Is.EqualTo(nextId));
            Assert.That(world.Surfaces.Revision, Is.EqualTo(paintRevision)); Assert.That(world.PinkCount, Is.EqualTo(score));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(1), "Repeated candidates retain one explicit fault for this owner, not a silent skip or log flood.");
        }
    }

    [Category("BlockFixedGridAdmissionShapePlay65")]
    public sealed class BlockFixedGridAdmissionShapePlay65Tests
    {
        GameObject _root, _owner;
        BlockTerrain _terrain;
        BoxCollider _floor, _helper;
        Scene _scene;
        AdmissionShape65Configuration _configuration;

        const string AuthoredArenaName = "Admission65 authored lifecycle arena";
        const string AuthoredFloorName = "Admission65 authored lifecycle floor";

        [UnitySetUp] public IEnumerator SetupActualRuntime()
        {
            Assert.That(Application.isPlaying, Is.False, "Run this category through the EditMode UnityTest runner.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Match the actual-displacement/native-step fixtures: author every required
            // map, ownership and topology input before the real runtime Awake sequence.
            // This configuration scope ends before EnterPlayMode and carries no tables
            // or iterator-local configuration object through the domain transition.
            using (new AdmissionShape65Configuration())
            {
                var authoredRoot = new GameObject(AuthoredArenaName);
                var authored = authoredRoot.AddComponent<PrototypeArena>();
                authored.OwnershipCellSize = GameplayConfig.Map.CellSize;
                authored.SpawnPoints = new Transform[8];
                for (int i = 0; i < 8; i++)
                {
                    var spawn = new GameObject("admission lifecycle spawn " + i).transform;
                    spawn.SetParent(authoredRoot.transform, false);
                    spawn.position = new Vector3((i % 4 - 1.5f) * 1.2f, .08f, i < 4 ? -1.8f : 1.8f);
                    authored.SpawnPoints[i] = spawn;
                }
                var map = GameObject.CreatePrimitive(PrimitiveType.Cube); map.name = AuthoredFloorName;
                map.transform.SetParent(authoredRoot.transform, false);
                var size = new Vector3(6, .3f, 6); var center = new Vector3(0, -.07f, 0);
                var mesh = Object.Instantiate(map.GetComponent<MeshFilter>().sharedMesh);
                mesh.name = "Admission65 authored lifecycle floor mesh";
                var vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size) + center;
                mesh.vertices = vertices; mesh.RecalculateBounds(); map.GetComponent<MeshFilter>().sharedMesh = mesh;
                var floor = map.GetComponent<BoxCollider>(); floor.center = center; floor.size = size;
                var paint = map.AddComponent<PaintSurface>(); paint.SurfaceId = 65104;
                paint.Scores = true; paint.WalkableSize = new Vector2(6, 6);
                paint.ShapeAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
                Assert.That(paint.ShapeAtlas, Is.Not.Null);
                Assert.That(paint.GetComponentInParent<PrototypeArena>(), Is.SameAs(authored));
                authored.BakedTopology = authored.ComputeTopology();
            }
            yield return new EnterPlayMode();
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            _configuration = new AdmissionShape65Configuration();
            _root = GameObject.Find(AuthoredArenaName);
            Assert.That(_root, Is.Not.Null);
            var arena = _root.GetComponent<PrototypeArena>(); arena.InitializeRuntime();
            Assert.That(arena.SpawnPoints.Length, Is.EqualTo(8)); Assert.That(arena.Surfaces.Count, Is.EqualTo(1));
            Assert.That(arena.Surfaces[65104].Ownership, Is.Not.Null); Assert.That(arena.TotalArea, Is.GreaterThan(0));
            _floor = GameObject.Find(AuthoredFloorName).GetComponent<BoxCollider>();
            Assert.That(_floor.GetComponentInParent<PrototypeArena>(), Is.SameAs(arena));
            // Keep the terrain outside the authored map hierarchy, as in the existing
            // runtime movement fixtures; its private proxy cannot change map topology.
            _owner = new GameObject("Play65 explicit terrain");
            _terrain = _owner.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableFixedGridMode();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            _helper = (BoxCollider)typeof(BlockTerrain).GetField("_fixedAdmissionBox", fields).GetValue(_terrain);
            _scene = (Scene)typeof(BlockTerrain).GetField("_fixedAdmissionScene", fields).GetValue(_terrain);
            Assert.That(_owner.activeInHierarchy && _terrain.enabled, Is.True, "The owner must take the actual runtime OnDestroy path.");
            Assert.That(arena.gameObject.activeInHierarchy, Is.True);
            Assert.That(_floor.enabled && _floor.gameObject.activeInHierarchy, Is.True, "Map collision geometry is real and active.");
            Assert.That(_terrain.IsAuthoredMapCollider(_floor), Is.True);
            Assert.That(!_helper.enabled && _helper.isTrigger && _helper.gameObject.activeInHierarchy, Is.True);
            Assert.That(_helper.attachedRigidbody, Is.Null);
            Assert.That(_scene.IsValid() && _scene.isLoaded && _scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene), Is.True);
            Physics.SyncTransforms();
        }

        [UnityTest] public IEnumerator DeferredOwnerDestructionRetiresTheIndependentShapeAndPreservesTheGameplayScene()
        {
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(BlockFixedGridAdmissionShape65Tests.Penetration(_terrain, BlockWorld.Center(Vector3Int.zero),
                _floor, out var direction, out float depth), Is.True);
            Assert.That(direction, Is.EqualTo(Vector3.up)); Assert.That(depth, Is.EqualTo(.0799f).Within(.0001f));
            Assert.That(_helper.enabled, Is.False);
            Assert.That(Physics.OverlapBox(_helper.transform.position, Vector3.one * .2f, Quaternion.identity, ~0,
                QueryTriggerInteraction.Collide).Any(c => c == _helper), Is.False);
            Object.Destroy(_owner);
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (_helper != null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(_helper == null, Is.True); Assert.That(_scene.IsValid() && _scene.isLoaded, Is.True);
            Assert.That(_root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
        }

        [UnityTearDown] public IEnumerator CleanupActualRuntime()
        {
            if (_owner != null) Object.Destroy(_owner);
            if (_root != null) Object.Destroy(_root);
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (_helper != null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            bool helperRetiredByProduction = _helper == null;
            // Keep failure evidence, then remove only our exact helper reference so
            // a failed lifecycle assertion cannot contaminate the following category.
            if (_helper != null)
            {
                _helper.enabled = false; Object.Destroy(_helper.gameObject);
                yield return null;
            }
            _configuration?.Dispose(); _configuration = null;
            if (Application.isPlaying) yield return new ExitPlayMode();
            // ExitPlayMode restores the original authored Editor scene. Clean its own
            // original mesh/root explicitly; no unrelated helper is searched/deleted.
            var authoredRoot = GameObject.Find(AuthoredArenaName);
            if (authoredRoot != null)
            {
                var floor = authoredRoot.GetComponentsInChildren<MeshFilter>(true)
                    .FirstOrDefault(filter => filter.name == AuthoredFloorName);
                var mesh = floor != null ? floor.sharedMesh : null;
                Object.DestroyImmediate(authoredRoot);
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
            Assert.That(helperRetiredByProduction, Is.True, "Unity runtime OnDestroy must retire the independent helper before fallback cleanup.");
        }

    }
}
#endif
