#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Real admission/transactions and map physics. Actor membership is supplied explicitly;
    // rendered Player, network and performance acceptance remain separate.
    [Category("BlockFixedGridAdmission")]
    public sealed class BlockFixedGridAdmissionTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        static readonly Type ActorType = typeof(BlockTerrain).GetNestedType("SafetyActor", BindingFlags.NonPublic);
        static readonly Type AdmissionType = typeof(BlockTerrain).GetNestedType("FixedAdmissionActor", BindingFlags.NonPublic);
        static readonly MethodInfo ExactOccupancy = typeof(BlockTerrain).GetMethod("CellOccupiesActor", PrivateStatic);
        static readonly MethodInfo AdmissionOccupancy = AdmissionType.GetMethod("Occupies");
        readonly Dictionary<ulong, PrototypePlayer> _previousPlayers = new();
        readonly List<PrototypePlayer> _players = new();
        readonly bool[] _terrainLayerIgnores = new bool[32];
        GameObject _root;
        PrototypeArena _arena;
        BoxCollider _floor;
        BlockTerrain _terrain;
        cfg.Tables _previousTables;
        const float FloorTop = .08f;
        static readonly Vector3Int A = Vector3Int.zero, B = new(3, 0, 0), C = new(6, 0, 0);

        [OneTimeSetUp] public void LoadConfiguration()
        {
            _previousTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }

        [OneTimeTearDown] public void RestoreConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _previousTables);

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _previousPlayers.Clear();
            foreach (var pair in PrototypePlayer.ByOwner) _previousPlayers.Add(pair.Key, pair.Value);
            PrototypePlayer.ByOwner.Clear(); _players.Clear();
            for (int layer = 0; layer < 32; layer++) _terrainLayerIgnores[layer] = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, layer);
            _root = new GameObject("Fixed admission contracts");
            _arena = _root.AddComponent<PrototypeArena>();
            _floor = MapBox("Off-grid supporting floor", new Vector3(0, FloorTop - .15f, 0), new Vector3(6, .3f, 6), 1501);
        }

        [TearDown] public void Cleanup()
        {
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                foreach (var player in _players) if (player != null) Spawned(player, false);
                PrototypePlayer.ByOwner.Clear();
                if (_root != null) Object.DestroyImmediate(_root);
                foreach (var pair in _previousPlayers) if (pair.Value != null) PrototypePlayer.ByOwner.Add(pair.Key, pair.Value);
                for (int layer = 0; layer < 32; layer++) Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, layer, _terrainLayerIgnores[layer]);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        BoxCollider MapBox(string name, Vector3 center, Vector3 size, int surfaceId)
        {
            var go = new GameObject(name); go.transform.SetParent(_root.transform, false);
            go.AddComponent<PaintSurface>().SurfaceId = surfaceId;
            var box = go.AddComponent<BoxCollider>(); box.center = center; box.size = size; return box;
        }

        void Initialize()
        {
            Physics.SyncTransforms(); _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(_arena); _terrain.EnableFixedGridMode(); Physics.SyncTransforms();
        }

        static Vector3 MapPoint(Vector3Int cell)
        { var point = BlockWorld.Center(cell); point.y = FloorTop; return point; }

        void Paint(params Vector3Int[] cells)
        {
            foreach (var cell in cells)
            {
                Assert.That(_terrain.TryGetSurfacePaint(_floor, MapPoint(cell), Vector3.up, out var paint), Is.True);
                if (paint.Owner != 1) Assert.That(_terrain.PaintSurfaceCell(paint.Key, 1), Is.True);
            }
        }

        BlockMutationResult Impact(Vector3Int cell)
            => _terrain.BuildFixedImpact(MapPoint(cell), Vector3.up, 0, 1, 1, true);

        static PlayerSnapshot State(Vector3 feet) => new()
        {
            HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1, Health = 100, Ink = 50,
            LifeState = PlayerLifeState.Alive, Position = feet, Grounded = true,
            Movement = MovementMode.Human, GroundNormal = Vector3.up, PaperRotation = Quaternion.identity
        };

        PrototypePlayer Player(Vector3 feet)
        {
            var go = new GameObject("Explicit admission actor") { layer = 8 }; go.transform.SetParent(_root.transform, false);
            go.AddComponent<NetworkObject>(); go.AddComponent<CharacterController>();
            var player = go.AddComponent<PrototypePlayer>(); Place(player, State(feet));
            Spawned(player, true); _players.Add(player); PrototypePlayer.ByOwner.Add((ulong)_players.Count, player);
            return player;
        }

        static void Spawned(PrototypePlayer player, bool value)
            => typeof(NetworkBehaviour).GetProperty(nameof(NetworkBehaviour.IsSpawned)).SetValue(player, value);

        static void Place(PrototypePlayer player, PlayerSnapshot state)
        {
            var controller = player.GetComponent<CharacterController>(); controller.enabled = false;
            HeroBodyShape.For(state.HeroId).Apply(controller, state.Swimming || state.CompactBody);
            player.transform.position = state.Position; controller.enabled = true;
            player.Snapshot.Value = state; Physics.SyncTransforms();
        }

        object Call(string name, params object[] args)
        {
            try { return typeof(BlockTerrain).GetMethod(name, PrivateInstance).Invoke(_terrain, args); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }

        T Field<T>(string name) => (T)typeof(BlockTerrain).GetField(name, PrivateInstance).GetValue(_terrain);
        void SetField(string name, object value) => typeof(BlockTerrain).GetField(name, PrivateInstance).SetValue(_terrain, value);
        void Stage(BlockFixedGridTransaction transaction, IEnumerable<Vector3Int> candidates, int count = 16)
            => Call("BuildFixedCandidates", transaction, candidates, (byte)1, count);

        HeroChangeZone Zone(Vector3 center)
        {
            var go = new GameObject("Admission exclusion volume"); go.transform.SetParent(_root.transform, false);
            go.transform.position = center; var zone = go.AddComponent<HeroChangeZone>();
            zone.Volume.isTrigger = true; zone.Volume.size = new Vector3(.28f, 2, .28f);
            Physics.SyncTransforms(); Call("InitializeSafety"); return zone;
        }

        [Test] public void SeparateProductionImpactsRefreshPlayerAdmissionAfterMovement()
        {
            Initialize(); Paint(A); var player = Player(MapPoint(A));
            uint revision = _terrain.FixedBlocks.Revision;
            Assert.That(Impact(A).Added, Is.Zero, "A paid impact cannot create a fixed cube through the actor capsule.");
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision));
            Place(player, State(new Vector3(-2, FloorTop, -2)));
            Assert.That(Impact(A).Added, Is.EqualTo(1), "A subsequent attack must not reuse the previous actor snapshot.");
            Assert.That(_terrain.FixedBlocks.TryGet(A, out _), Is.True);
            Assert.That(Field<object>("_activeFixedAdmissionActors"), Is.Null);
        }

        [Test] public void SeparateProductionImpactsRefreshMovedAndDisabledProtectionVolumes()
        {
            Initialize(); Paint(A, B, C);
            var zone = Zone(MapPoint(A));
            Assert.That(Impact(A).Added, Is.Zero);
            zone.transform.position = MapPoint(B); Physics.SyncTransforms();
            Assert.That(Impact(A).Added, Is.EqualTo(1), "Moving a protected volume invalidates the next attack's cached column proof.");
            Assert.That(Impact(B).Added, Is.Zero);
            zone.Volume.enabled = false;
            Assert.That(Impact(B).Added, Is.EqualTo(1), "A disabled trigger must not remain protected by an earlier capture.");
            zone.Volume.enabled = true; zone.transform.position = MapPoint(C); Physics.SyncTransforms();
            Assert.That(Impact(C).Added, Is.Zero);
            Assert.That(Field<bool>("_stageConstructionZonesActive"), Is.False);
        }

        [Test] public void CandidateEnumerationUsesOneActorSnapshotThenTheNextAttackRefreshesIt()
        {
            Initialize(); Paint(A, B); var player = Player(MapPoint(A));
            IEnumerable<Vector3Int> Candidates()
            {
                Place(player, State(MapPoint(B)));
                yield return A; yield return B;
            }
            using (var tx = _terrain.FixedBlocks.BeginMutation())
            {
                Stage(tx, Candidates());
                Assert.That(tx.TryGet(A, out _), Is.False, "Capture happened before enumerating candidates.");
                Assert.That(tx.TryGet(B, out _), Is.True, "Every candidate in this build uses the same actor snapshot.");
                Assert.That(tx.TryCommit(out var result, out var error), Is.True, error);
                Assert.That(result.Added, Is.EqualTo(1));
            }
            Assert.That(Impact(A).Added, Is.EqualTo(1), "A later attack sees the actor's updated position.");
        }

        [Test] public void ThrowingCandidateEnumerationReleasesActorAndZoneSnapshots()
        {
            Initialize(); Paint(A, B); var player = Player(MapPoint(A)); var zone = Zone(MapPoint(A));
            uint revision = _terrain.FixedBlocks.Revision;
            IEnumerable<Vector3Int> Candidates()
            {
                Place(player, State(MapPoint(B))); zone.transform.position = new Vector3(-2, 1, -2);
                yield return new Vector3Int(100000, 0, 0);
                throw new InvalidOperationException("Admission enumeration aborted by fixture.");
            }
            using (var tx = _terrain.FixedBlocks.BeginMutation())
                Assert.Throws<InvalidOperationException>(() => Stage(tx, Candidates()));
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision));
            Assert.That(_terrain.FixedBlocks.Count, Is.Zero);
            Assert.That(Field<object>("_activeFixedAdmissionActors"), Is.Null);
            Assert.That(Field<bool>("_stageConstructionZonesActive"), Is.False);
            Assert.That(Impact(A).Added, Is.EqualTo(1), "An exception cannot leave either old exclusion snapshot installed.");
            Assert.That(Impact(B).Added, Is.Zero, "The next build still checks the actor's actual new position.");
        }

        [Test] public void ReentrantBuildRestoresOuterActorAndBorrowedProtectionSnapshots()
        {
            Initialize(); Paint(A, B, C); var player = Player(MapPoint(A)); var zone = Zone(MapPoint(C));
            IEnumerable<Vector3Int> OuterCandidates()
            {
                Place(player, State(MapPoint(B))); zone.transform.position = new Vector3(-2, 1, -2);
                using (var nested = _terrain.FixedBlocks.BeginMutation())
                {
                    Stage(nested, new[] { A, B, C });
                    Assert.That(nested.TryGet(A, out _), Is.True, "The nested build owns a fresh actor capture.");
                    Assert.That(nested.TryGet(B, out _), Is.False);
                    Assert.That(nested.TryGet(C, out _), Is.False, "An existing outer staging scope owns its zone snapshot.");
                }
                yield return A; yield return B; yield return C;
            }
            using (var tx = _terrain.FixedBlocks.BeginMutation())
            {
                Stage(tx, OuterCandidates());
                Assert.That(tx.TryGet(A, out _), Is.False, "Returning from a nested build restores the outer actor view.");
                Assert.That(tx.TryGet(B, out _), Is.True);
                Assert.That(tx.TryGet(C, out _), Is.False, "Nested cleanup must not release the outer zone stage.");
                Assert.That(tx.TryCommit(out var result, out var error), Is.True, error);
                Assert.That(result.Added, Is.EqualTo(1));
            }
            Assert.That(Field<object>("_activeFixedAdmissionActors"), Is.Null);
            Assert.That(Field<bool>("_stageConstructionZonesActive"), Is.False);
            Assert.That(Impact(A).Added, Is.EqualTo(1));
            Assert.That(Impact(C).Added, Is.EqualTo(1), "The next independent attack captures the zone's new transform.");
        }

        [Test] public void ExactlyFullOverlapBufferStillAdmitsOnlyTheSupportingFacePenetration()
        {
            Initialize(); Paint(A); SetField("_fixedAdmissionOverlaps", new Collider[1]);
            Assert.That(Impact(A).Added, Is.EqualTo(1), "The 8 cm permitted base embedding must survive the repeated complete overlap query and narrow phase.");
            Assert.That(_terrain.FixedBlocks.TryGet(A, out var block), Is.True);
            Assert.That(block.Support.Height, Is.EqualTo(FloorTop).Within(.00001f));
            var buffer = Field<Collider[]>("_fixedAdmissionOverlaps");
            Assert.That(buffer.Length, Is.GreaterThan(1), "A full buffer has not established query completeness.");
            Assert.That(buffer, Is.All.Null, "The retained query buffer cannot retain per-attack collider references.");
        }

        [TestCase(1)] [TestCase(16)]
        public void SaturatedOverlapQueryCannotHideWallsBehindThePermittedFloor(int capacity)
        {
            for (int i = 0; i < capacity; i++)
                MapBox("Thin blocking wall " + i, new Vector3(.28f, .7f, .15f), new Vector3(.04f, 1.4f, .5f), 1600 + i);
            Initialize(); Paint(A); SetField("_fixedAdmissionOverlaps", new Collider[capacity]);
            uint revision = _terrain.FixedBlocks.Revision;
            Assert.That(Impact(A).Added, Is.Zero, "A permitted support collider cannot exempt any other wall in the complete overlap result.");
            Assert.That(_terrain.FixedBlocks.Count, Is.Zero); Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(revision));
            var buffer = Field<Collider[]>("_fixedAdmissionOverlaps");
            Assert.That(buffer.Length, Is.GreaterThan(capacity)); Assert.That(buffer, Is.All.Null);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void ConservativeAdmissionHasExactlyTheOriginalOccupancyForRotatedAndExtendedContours(int pose)
        {
            var player = Player(new Vector3(.071f, .123f, -.093f)); var state = player.Snapshot.Value;
            IReadOnlyList<Rect> rects = null;
            if (pose != 0)
            {
                state.Swimming = pose != 1; state.CompactBody = pose == 1;
                state.PaperPose = pose == 2 ? PaperPose.Ground : pose == 3 ? PaperPose.Wall : PaperPose.Air;
                state.PaperCenter = state.Position + new Vector3(.2f, 1.1f, .1f);
                state.PaperRotation = pose switch
                {
                    2 => Quaternion.Euler(90, 17, 0), 3 => Quaternion.Euler(0, 35, 0),
                    4 => Quaternion.Euler(32, 147, 79), 5 => Quaternion.Euler(-23, 58, 41),
                    7 => Quaternion.Euler(45, 29, -19), _ => Quaternion.identity
                };
                rects = pose switch
                {
                    1 or 6 => Array.Empty<Rect>(),
                    5 => new[] { new Rect(2.55f, -.1f, .3f, .2f), new Rect(-1.4f, .8f, .2f, .7f) },
                    7 => null,
                    _ => new[] { new Rect(-.42f, -.85f, .35f, 1.7f), new Rect(.12f, -.1f, .7f, .2f) }
                };
            }
            object actor = Activator.CreateInstance(ActorType, new object[] { player, state, rects });
            CompareToExactOracle(actor, state, rects);
        }

        [Test] public void CompactCapsuleBoundContainsTheWholeSphereWhenDiameterExceedsRequestedHeight()
        {
            var player = Player(new Vector3(.15f, 0, .15f)); var state = player.Snapshot.Value;
            state.CompactBody = true; state.PaperPose = PaperPose.Air;
            object actor = Activator.CreateInstance(ActorType, new object[] { player, state, Array.Empty<Rect>() });
            // An adversarial valid radius exposes the native capsule minimum height;
            // the original exact predicate uses the same whole end-cap sphere.
            var controller = player.GetComponent<CharacterController>(); controller.radius = .5f; controller.height = 1.6f;
            ActorType.GetField("Shape").SetValue(actor, new HeroBodyShape(controller));
            object admission = Activator.CreateInstance(AdmissionType, new[] { actor });
            var upper = new Vector3Int(0, 3, 0);
            Assert.That((bool)ExactOccupancy.Invoke(null, new[] { (object)upper, actor }), Is.True);
            Assert.That((bool)AdmissionOccupancy.Invoke(admission, new object[] { upper }), Is.True,
                "A 0.7 m requested compact height with 0.5 m radius still occupies its full 1 m sphere.");
            CompareToExactOracle(actor, state, Array.Empty<Rect>());
        }

        static void CompareToExactOracle(object actor, PlayerSnapshot state, IReadOnlyList<Rect> rects)
        {
            object admission = Activator.CreateInstance(AdmissionType, new[] { actor });
            var cells = new HashSet<Vector3Int>();
            void Around(Vector3 point)
            {
                var origin = BlockWorld.CellAt(point);
                for (int y = -3; y <= 9; y++) for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++)
                    cells.Add(origin + new Vector3Int(x, y, z));
            }
            Around(state.Position); Around(state.PaperCenter);
            if (rects != null) foreach (var rect in rects)
                Around(state.PaperCenter + state.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0));
            int occupied = 0, clear = 0;
            foreach (var cell in cells)
            {
                bool exact = (bool)ExactOccupancy.Invoke(null, new[] { (object)cell, actor });
                bool optimized = (bool)AdmissionOccupancy.Invoke(admission, new object[] { cell });
                Assert.That(optimized, Is.EqualTo(exact), "Broad-phase optimization changed admission at " + cell);
                if (exact) occupied++; else clear++;
            }
            Assert.That(occupied, Is.GreaterThan(0)); Assert.That(clear, Is.GreaterThan(0));
        }
    }
}
#endif
