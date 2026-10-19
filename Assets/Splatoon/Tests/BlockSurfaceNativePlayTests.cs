#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Observe only genuine PhysX callbacks. This fixture component never invokes
    // the conversion path or synthesizes contacts from overlap queries.
    internal sealed class BlockNativeWallContactObserver : MonoBehaviour
    {
        internal Collider ExpectedWall, ExpectedBody;
        internal BlockDynamicBody ExpectedBlock;
        internal BlockDynamicWorld ExpectedWorld;
        internal ulong ExpectedId;
        internal uint ExpectedRound;
        internal bool SawContact { get; private set; }

        void OnCollisionEnter(Collision collision) => Observe(collision);
        void OnCollisionStay(Collision collision) => Observe(collision);

        void Observe(Collision collision)
        {
            if (ExpectedBlock == null || ExpectedWorld == null || ExpectedWorld.Round != ExpectedRound ||
                ExpectedBlock.World != ExpectedWorld || ExpectedBlock.Id != ExpectedId ||
                ExpectedBlock.Collider != ExpectedBody || !ExpectedWorld.Contains(ExpectedBlock)) return;
            for (int i = 0; i < collision.contactCount; i++)
            {
                var contact = collision.GetContact(i);
                bool expectedPair = contact.thisCollider == ExpectedWall && contact.otherCollider == ExpectedBody ||
                    contact.thisCollider == ExpectedBody && contact.otherCollider == ExpectedWall;
                if (expectedPair && Mathf.Abs(contact.normal.x) > .95f) SawContact = true;
            }
        }
    }

    [Category("BlockSurfacePaintNative")]
    public sealed class BlockSurfaceNativePlayTests
    {
        const string Preview = "Assets/GameResource/Characters/RifleGirlChibi/Preview/RifleGirlChibiPreview.unity";
        static readonly Vector3 FloorPoint = new(8.25f, 0, 8.25f);
        static readonly Vector3 WallPoint = new(9, .75f, 8.25f);
        static readonly (int a, int b)[] CollisionPairs =
        {
            (BlockTerrain.TerrainLayer, 8), (BlockDynamicWorld.Layer, BlockDynamicWorld.Layer),
            (BlockDynamicWorld.Layer, 8), (BlockDynamicWorld.Layer, BlockTerrain.TerrainLayer),
            (BlockDynamicWorld.Layer, 0)
        };
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _floor, _wall;
        bool[] _ignored;
        bool _savedPhysics;
        Vector3 _gravity;
        SimulationMode _simulationMode;
        float _timeScale, _fixedDeltaTime;
        BlockDynamicWorld World => _terrain.Dynamic;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            Assert.That(Application.isPlaying, Is.False, "Run through the isolated EditMode runner.");
            Assert.That(SessionState.GetBool("RoomDiscovery.PlaySmoke", false), Is.False);
            EditorSceneManager.OpenScene(Preview, OpenSceneMode.Single);
            yield return new EnterPlayMode();
            Assert.That(PrototypeApp.Current, Is.Null, "The preview must skip production game bootstrap.");
            Assert.That(BlockTerrain.Current, Is.Null);

            _ignored = new bool[CollisionPairs.Length];
            for (int i = 0; i < CollisionPairs.Length; i++)
                _ignored[i] = Physics.GetIgnoreLayerCollision(CollisionPairs[i].a, CollisionPairs[i].b);
            _gravity = Physics.gravity; _simulationMode = Physics.simulationMode;
            _timeScale = Time.timeScale; _fixedDeltaTime = Time.fixedDeltaTime; _savedPhysics = true;
            Physics.simulationMode = SimulationMode.FixedUpdate;
            Physics.gravity = new Vector3(0, -9.81f, 0);
            Time.timeScale = 1; Time.fixedDeltaTime = .02f;

            _root = new GameObject("Native free block surface contact fixture");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var floor = new GameObject("Isolated paintable native floor");
            floor.SetActive(false); floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(8.1f, -.15f, 8.1f);
            // The geometry adapter needs the authoring marker, not GPU paint textures.
            // Disable before activation so OnEnable cannot require production shaders.
            var paint = floor.AddComponent<PaintSurface>(); paint.enabled = false; paint.SurfaceId = 713;
            floor.GetComponent<MeshRenderer>().enabled = false;
            _floor = floor.AddComponent<BoxCollider>(); _floor.size = new Vector3(2.4f, .3f, 2.4f);
            floor.SetActive(true);
            var wall = new GameObject("Isolated paintable native wall");
            wall.SetActive(false); wall.transform.SetParent(_root.transform, false);
            wall.transform.position = new Vector3(9.15f, 1.2f, 8.1f);
            var wallPaint = wall.AddComponent<PaintSurface>(); wallPaint.enabled = false; wallPaint.SurfaceId = 714;
            wall.GetComponent<MeshRenderer>().enabled = false;
            _wall = wall.AddComponent<BoxCollider>(); _wall.size = new Vector3(.3f, 2.4f, 2.4f);
            wall.SetActive(true);
            var colliders = typeof(BlockTerrain).GetField("_staticColliders", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(colliders, Is.Not.Null);
            var authored = (HashSet<Collider>)colliders.GetValue(_terrain);
            authored.Add(_floor); authored.Add(_wall);
            _terrain.EnableSurfaceMode(); World.SetAuthority(true);
            Physics.IgnoreLayerCollision(BlockDynamicWorld.Layer, 0, false);
            Physics.SyncTransforms();
            Assert.That(_terrain.TryGetSurfacePaint(_floor, FloorPoint, Vector3.up, out _), Is.True);
            Assert.That(_terrain.TryGetSurfacePaint(_wall, WallPoint, Vector3.left, out _), Is.True);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_savedPhysics)
            {
                for (int i = 0; i < CollisionPairs.Length; i++)
                    Physics.IgnoreLayerCollision(CollisionPairs[i].a, CollisionPairs[i].b, _ignored[i]);
                Physics.gravity = _gravity; Physics.simulationMode = _simulationMode;
                Time.timeScale = _timeScale; Time.fixedDeltaTime = _fixedDeltaTime;
                _savedPhysics = false;
            }
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        BlockDynamicBody Drop()
        {
            Assert.That(World.TrySpawn(1, FloorPoint + Vector3.up * .9f, Quaternion.identity, out var body), Is.True);
            Assert.That(body.Body.isKinematic, Is.False); Assert.That(body.Body.useGravity, Is.True);
            return body;
        }

        BlockSurfaceFace FaceAt(Collider map, Vector3 point, Vector3 normal)
        {
            Assert.That(_terrain.TryGetSurfacePaint(map, point, normal, out var cell), Is.True);
            Assert.That(_terrain.SurfacePaints.TryGetFace(cell.Key.FaceId, out var face), Is.True);
            Assert.That(Vector3.Dot(face.Normal, normal), Is.GreaterThan(.99f), "The queried square belongs to the requested map-facing plane.");
            return face;
        }

        int PaintTop(byte owner) => PaintFace(_floor, FloorPoint, Vector3.up, owner);

        int PaintFace(Collider map, Vector3 point, Vector3 normal, byte owner)
        {
            var face = FaceAt(map, point, normal);
            int changed = 0;
            // A cube's corner contacts can straddle neighbouring cells. Cover the
            // whole face so an own-color contact cannot touch a bare fringe.
            foreach (var grid in face.ValidCells)
                if (_terrain.PaintSurfaceCell(new BlockSurfaceKey(face.Id, grid.x, grid.y), owner)) changed++;
            _terrain.NotifySurfacePaintChanged();
            return changed;
        }

        int CountFace(Collider map, Vector3 point, Vector3 normal, byte owner)
        {
            var face = FaceAt(map, point, normal);
            int count = 0;
            foreach (var grid in face.ValidCells)
            {
                Assert.That(_terrain.SurfacePaints.TryGet(new BlockSurfaceKey(face.Id, grid.x, grid.y), out var cell), Is.True);
                if (cell.Owner == owner) count++;
            }
            return count;
        }

        BlockDynamicBody LaunchAtWall()
        {
            var body = Drop();
            // Set initial motion only. Gravity and the normal FixedUpdate PhysX
            // solve must carry this independent body into the authored wall.
            body.Body.linearVelocity = Vector3.right * 3;
            return body;
        }

        IEnumerator ObserveNativeWallAndConversion(ulong id, string stage)
        {
            bool travelled = false, fell = false;
            double started = Time.fixedTimeAsDouble;
            double realtimeStarted = Time.realtimeSinceStartupAsDouble;
            // The EditMode runner can poll a yielded instruction between native
            // ticks. Bound by actual physics time, with a real-time deadlock cap.
            while (Time.fixedTimeAsDouble - started < 2 &&
                Time.realtimeSinceStartupAsDouble - realtimeStarted < 10 && World.TryGet(id, out _))
            {
                yield return new WaitForFixedUpdate();
                if (World.TryGet(id, out var body))
                {
                    travelled |= body.Body.position.x > FloorPoint.x + .1f;
                    fell |= body.Body.position.y < .85f;
                }
            }
            Assert.That(Time.fixedTimeAsDouble, Is.GreaterThan(started), stage + ": actual fixed frames must run.");
            Assert.That(travelled, Is.True, stage + ": observe real horizontal travel before wall contact.");
            Assert.That(fell, Is.True, stage + ": gravity remains active during wall approach.");
            Assert.That(World.TryGet(id, out _), Is.False, stage + ": native wall contact must consume the body within 2 physics seconds and 10 real seconds.");
            Assert.That(World.Count, Is.Zero);
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.Zero, "Wall conversion must not recreate voxel geometry.");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator NativeMovingWallContactConvertsOnlyBlankOrEnemyColor()
        {
            int floorCells = PaintTop(1); Assert.That(floorCells, Is.GreaterThan(4));
            var blankBody = LaunchAtWall(); ulong blankId = blankBody.Id;
            int blankTotal = _terrain.PinkCount + _terrain.BlueCount;
            uint blankRevision = _terrain.SurfacePaints.Revision;
            yield return ObserveNativeWallAndConversion(blankId, "Bare wall");
            Assert.That(CountFace(_wall, WallPoint, Vector3.left, 1), Is.EqualTo(1), "Exactly one wall square receives the body.");
            Assert.That(CountFace(_floor, FloorPoint, Vector3.up, 1), Is.EqualTo(floorCells), "The supporting floor stays own color.");
            Assert.That(_terrain.SurfacePaints.PinkCount, Is.EqualTo(floorCells + 1));
            Assert.That(_terrain.SurfacePaints.Revision, Is.EqualTo(blankRevision + 1));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(blankTotal));
            Assert.That(World.CreatedCount, Is.EqualTo(1)); Assert.That(World.RemovedCount, Is.EqualTo(1));

            _terrain.Clear(_terrain.Round + 1); World.SetAuthority(true);
            yield return new WaitForFixedUpdate();
            floorCells = PaintTop(1);
            int enemyCells = PaintFace(_wall, WallPoint, Vector3.left, 2); Assert.That(enemyCells, Is.GreaterThan(4));
            var enemyBody = LaunchAtWall(); ulong enemyId = enemyBody.Id;
            int pinkBefore = _terrain.PinkCount, blueBefore = _terrain.BlueCount;
            uint enemyRevision = _terrain.SurfacePaints.Revision;
            yield return ObserveNativeWallAndConversion(enemyId, "Enemy wall");
            Assert.That(CountFace(_wall, WallPoint, Vector3.left, 1), Is.EqualTo(1));
            Assert.That(CountFace(_wall, WallPoint, Vector3.left, 2), Is.EqualTo(enemyCells - 1), "Exactly one enemy wall square is replaced.");
            Assert.That(CountFace(_floor, FloorPoint, Vector3.up, 1), Is.EqualTo(floorCells));
            Assert.That(_terrain.PinkCount, Is.EqualTo(pinkBefore), "One owned body becomes one owned wall square.");
            Assert.That(_terrain.BlueCount, Is.EqualTo(blueBefore - 1));
            Assert.That(_terrain.SurfacePaints.Revision, Is.EqualTo(enemyRevision + 1));
            Assert.That(World.CreatedCount, Is.EqualTo(1)); Assert.That(World.RemovedCount, Is.EqualTo(1));

            _terrain.Clear(_terrain.Round + 1); World.SetAuthority(true);
            yield return new WaitForFixedUpdate();
            floorCells = PaintTop(1);
            int ownWallCells = PaintFace(_wall, WallPoint, Vector3.left, 1);
            var ownBody = LaunchAtWall(); ulong ownId = ownBody.Id;
            var observer = _wall.gameObject.AddComponent<BlockNativeWallContactObserver>();
            observer.ExpectedWall = _wall; observer.ExpectedBody = ownBody.Collider;
            observer.ExpectedBlock = ownBody; observer.ExpectedWorld = World;
            observer.ExpectedId = ownId; observer.ExpectedRound = World.Round;
            uint ownRevision = _terrain.SurfacePaints.Revision, ownLifecycle = World.LifecycleRevision;
            bool travelled = false, fell = false;
            double ownStarted = Time.fixedTimeAsDouble;
            double ownRealtimeStarted = Time.realtimeSinceStartupAsDouble;
            while (Time.fixedTimeAsDouble - ownStarted < 2 &&
                Time.realtimeSinceStartupAsDouble - ownRealtimeStarted < 10 && World.TryGet(ownId, out _))
            {
                yield return new WaitForFixedUpdate();
                if (World.TryGet(ownId, out var current))
                {
                    travelled |= current.Body.position.x > FloorPoint.x + .1f;
                    fell |= current.Body.position.y < .85f;
                }
            }
            Assert.That(Time.fixedTimeAsDouble - ownStarted, Is.GreaterThanOrEqualTo(2), "Observe two seconds of real native physics, not merely editor polling iterations.");
            Assert.That(travelled, Is.True); Assert.That(fell, Is.True);
            Assert.That(observer.SawContact, Is.True, "OnCollisionEnter/Stay must report real wall/body contact; surviving at a distance is insufficient.");
            Assert.That(World.TryGet(ownId, out ownBody), Is.True, "Repeated native contacts on own wall and floor retain the body.");
            Assert.That(ownBody.Body.isKinematic, Is.False); Assert.That(ownBody.Body.useGravity, Is.True);
            Assert.That(ownBody.Body.position.x, Is.InRange(FloorPoint.x + .1f, WallPoint.x - .1f), "The wall constrains the travelling cube on its approach side.");
            Assert.That(Mathf.Abs(ownBody.Body.linearVelocity.x), Is.LessThan(.1f), "The native wall contact arrests the initial 3 m/s approach.");
            Assert.That(World.Count, Is.EqualTo(1));
            Assert.That(CountFace(_wall, WallPoint, Vector3.left, 1), Is.EqualTo(ownWallCells));
            Assert.That(CountFace(_floor, FloorPoint, Vector3.up, 1), Is.EqualTo(floorCells));
            Assert.That(_terrain.SurfacePaints.Revision, Is.EqualTo(ownRevision));
            Assert.That(World.LifecycleRevision, Is.EqualTo(ownLifecycle));
            Assert.That(World.CreatedCount, Is.EqualTo(1)); Assert.That(World.RemovedCount, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(floorCells + ownWallCells + 1));
            Assert.That(_terrain.BlueCount, Is.Zero);
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.Zero);
        }

        IEnumerator ObserveNativeFallAndConversion(ulong id, string stage)
        {
            bool fell = false;
            double started = Time.fixedTimeAsDouble;
            for (int frame = 0; frame < 200 && World.TryGet(id, out _); frame++)
            {
                yield return new WaitForFixedUpdate();
                if (World.TryGet(id, out var body)) fell |= body.Body.position.y < .85f;
            }
            Assert.That(Time.fixedTimeAsDouble, Is.GreaterThan(started), stage + ": actual fixed frames must run.");
            Assert.That(fell, Is.True, stage + ": observe gravity moving the native body before contact.");
            Assert.That(World.TryGet(id, out _), Is.False, stage + ": native contact must consume the body within 200 fixed frames.");
            Assert.That(World.Count, Is.Zero);
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.Zero, "Native conversion must not recreate voxel geometry.");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator NativeLandingConservesPaintAndSleepingOwnColorWakesAfterRepaint()
        {
            var blankDrop = Drop(); ulong blankId = blankDrop.Id;
            int blankTotal = _terrain.PinkCount + _terrain.BlueCount;
            yield return ObserveNativeFallAndConversion(blankId, "Bare floor");
            Assert.That(_terrain.SurfacePaints.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(blankTotal));
            Assert.That(World.CreatedCount, Is.EqualTo(1)); Assert.That(World.RemovedCount, Is.EqualTo(1));

            _terrain.Clear(_terrain.Round + 1); World.SetAuthority(true);
            yield return new WaitForFixedUpdate();
            int enemyCells = PaintTop(2); Assert.That(enemyCells, Is.GreaterThan(4));
            var enemyDrop = Drop(); ulong enemyId = enemyDrop.Id;
            int pinkBefore = _terrain.PinkCount, blueBefore = _terrain.BlueCount;
            yield return ObserveNativeFallAndConversion(enemyId, "Enemy floor");
            Assert.That(_terrain.PinkCount, Is.EqualTo(pinkBefore), "One owned body becomes one owned square.");
            Assert.That(_terrain.BlueCount, Is.EqualTo(blueBefore - 1), "Native contact replaces exactly one enemy square.");
            Assert.That(_terrain.SurfacePaints.PinkCount, Is.EqualTo(1));
            Assert.That(World.RemovedCount, Is.EqualTo(1));

            _terrain.Clear(_terrain.Round + 1); World.SetAuthority(true);
            yield return new WaitForFixedUpdate();
            int ownCells = PaintTop(1); var ownDrop = Drop(); ulong ownId = ownDrop.Id;
            uint ownPaintRevision = _terrain.SurfacePaints.Revision, ownLifecycle = World.LifecycleRevision;
            bool fell = false;
            double ownStarted = Time.fixedTimeAsDouble;
            for (int frame = 0; frame < 300 && World.TryGet(ownId, out var current) && !current.Body.IsSleeping(); frame++)
            {
                yield return new WaitForFixedUpdate();
                if (World.TryGet(ownId, out current)) fell |= current.Body.position.y < .85f;
            }
            Assert.That(Time.fixedTimeAsDouble, Is.GreaterThan(ownStarted)); Assert.That(fell, Is.True);
            Assert.That(World.TryGet(ownId, out ownDrop), Is.True, "All native contacts on own color retain the body.");
            Assert.That(ownDrop.Body.IsSleeping(), Is.True, "The freely falling body must naturally settle within 300 fixed frames.");
            Assert.That(ownDrop.Body.position.y, Is.InRange(.14f, .19f));
            Assert.That(ownDrop.Body.isKinematic, Is.False); Assert.That(World.Count, Is.EqualTo(1));
            Assert.That(_terrain.SurfacePaints.Revision, Is.EqualTo(ownPaintRevision));
            Assert.That(World.LifecycleRevision, Is.EqualTo(ownLifecycle)); Assert.That(World.RemovedCount, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(ownCells + 1));

            long createdBeforeRepaint = World.CreatedCount;
            double repaintAt = Time.fixedTimeAsDouble;
            Assert.That(PaintTop(2), Is.EqualTo(ownCells));
            Assert.That(World.TryGet(ownId, out ownDrop), Is.True, "Repaint wakes; conversion waits for a later native contact.");
            Assert.That(ownDrop.Body.IsSleeping(), Is.False, "Changing the touching own floor to enemy color must wake the sleeper.");
            Assert.That(World.RemovedCount, Is.Zero); Assert.That(_terrain.SurfacePaints.PinkCount, Is.Zero);
            for (int frame = 0; frame < 100 && World.TryGet(ownId, out _); frame++)
                yield return new WaitForFixedUpdate();
            Assert.That(Time.fixedTimeAsDouble, Is.GreaterThan(repaintAt));
            Assert.That(World.TryGet(ownId, out _), Is.False, "A woken native contact must convert within 100 fixed frames.");
            Assert.That(World.Count, Is.Zero); Assert.That(World.CreatedCount, Is.EqualTo(createdBeforeRepaint));
            Assert.That(World.RemovedCount, Is.EqualTo(1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.EqualTo(ownCells - 1));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(ownCells));
        }
    }
}
#endif
