#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Reports-only installation draft: no compilation or runtime passes claimed.
    // A real native Move witness plus the production combined consumer. This is
    // intentionally a same-process map/controller test, not NGO or paid gameplay.
    [Category("BlockFixedGridActualDisplacement65")]
    public sealed class BlockFixedGridActualDisplacementPlayTests
    {
        const float Dt = 1f / 60;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo CombinedMove = typeof(PlayerMotorSimulation).GetMethod("MoveWithFreeBlocks", Private);
        PrototypeArena arena;
        BlockTerrain terrain;
        BoxCollider floor;
        PrototypePlayer player;
        CharacterController controller;
        PlayerMotorSimulation motor;
        static readonly float[] PlatformTops = { .265f, .270f, .275f };
        static readonly float[] PlatformCentersZ = { -.75f, .15f, 1.05f };
        const float PlatformCenterX = -2.55f;
        const string NativeCeilingName = "69 actual-displacement descending native ceiling";
        readonly List<BoxCollider> nativePlatforms = new();
        bool priorTerrainPlayerIgnore, capturedIgnore, capturedCursor;
        int cursorWrites, observerFaults;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> observations = new();
        readonly List<string> records = new();
        string reportPath;
        [Serializable] sealed class NativeOverlapRecord
        {
            public string name, type, root;
            public int instance, layer;
            public bool enabled, trigger, active;
        }
        [Serializable] sealed class NativeOverlapList { public NativeOverlapRecord[] colliders; }
        [Serializable] sealed class CapsuleRecord
        {
            public float Height, CompactHeight, Radius, SkinWidth, StepOffset;
            public Vector3 Center, CompactCenter;
        }
        [Serializable] sealed class SupportRecord
        {
            public uint FixedEpoch, FixedRound, FixedRevision, TerrainRevision, Generation;
            public ulong Id;
            public bool IsFixedGrid, IsSurfacePaint;
            public Vector3Int Cell;
            public int SurfaceFace, SurfaceU, SurfaceV;
        }
        [Serializable] sealed class SnapshotRecord
        {
            public Vector3 Position, HumanPosition, Velocity, PlanarVelocity, GroundNormal, PaperCenter, WallNormal, WallPoint;
            public Quaternion PaperRotation;
            public Vector2 PaperMove;
            public int HeroId, Movement, PaperPose, LifeState, SwimSource, AirSwimSource;
            public uint Revision, HeroRevision, SimulationTick, ConsumedJump;
            public byte Team;
            public bool CompactBody, Swimming, Grounded, FriendlyInkContact, Firing, InputTimedOut, CapsuleAvailable;
            public float VerticalSpeed, AirHumanOffset, Health, Ink, Yaw, Pitch, PaperAnimationTime;
            public double SimulatedAt;
            public CapsuleRecord Capsule;
            public SupportRecord Support;
        }
        [Serializable] sealed class InputRecord
        {
            public uint Tick, Sequence, Revision, HeroRevision, JumpSequence, FireSequence, ReleaseSequence;
            public Vector2 Move, Look;
            public bool Fire, Swim, CancelFire;
        }
        static string SnapshotJson(PlayerSnapshot state)
        {
            var shape = state.HeroId > 0 ? HeroBodyShape.For(state.HeroId) : default;
            string json = JsonUtility.ToJson(new SnapshotRecord {
                Position=state.Position, HumanPosition=PlayerMotorSimulation.HumanPosition(state),
                Velocity=state.Velocity, PlanarVelocity=state.PlanarVelocity, GroundNormal=state.GroundNormal,
                PaperCenter=state.PaperCenter, PaperRotation=state.PaperRotation, PaperMove=state.PaperMove,
                WallNormal=state.WallNormal, WallPoint=state.WallPoint, HeroId=state.HeroId,
                Movement=(int)state.Movement, PaperPose=(int)state.PaperPose, LifeState=(int)state.LifeState,
                SwimSource=(int)state.SwimSource, AirSwimSource=(int)state.AirSwimSource,
                Revision=state.Revision, HeroRevision=state.HeroRevision, SimulationTick=state.SimulationTick,
                ConsumedJump=state.ConsumedJump, Team=state.Team, CompactBody=state.CompactBody,
                Swimming=state.Swimming, Grounded=state.Grounded, FriendlyInkContact=state.FriendlyInkContact,
                Firing=state.Firing, InputTimedOut=state.InputTimedOut, CapsuleAvailable=state.HeroId > 0, VerticalSpeed=state.VerticalSpeed,
                AirHumanOffset=state.AirHumanOffset, Health=state.Health, Ink=state.Ink, Yaw=state.Yaw,
                Pitch=state.Pitch, PaperAnimationTime=state.PaperAnimationTime, SimulatedAt=state.SimulatedAt,
                Capsule=new CapsuleRecord { Height=shape.Height, CompactHeight=shape.CompactHeight,
                    Radius=shape.Radius, SkinWidth=shape.SkinWidth, StepOffset=shape.StepOffset,
                    Center=shape.Center, CompactCenter=shape.CompactCenter },
                Support=new SupportRecord { FixedEpoch=state.FixedBlockEpoch, FixedRound=state.FixedBlockRound,
                    FixedRevision=state.FixedBlockRevision, TerrainRevision=state.SupportTerrainRevision,
                    Id=state.SupportFixedId, Generation=state.SupportFixedGeneration, Cell=state.SupportCell,
                    IsFixedGrid=state.SupportIsFixedGrid, IsSurfacePaint=state.SupportIsSurfacePaint,
                    SurfaceFace=state.SupportSurfaceFace, SurfaceU=state.SupportSurfaceU, SurfaceV=state.SupportSurfaceV }
            });
            if (string.IsNullOrEmpty(json) || !json.Contains("\"Position\"") || !json.Contains("\"Support\""))
                throw new InvalidOperationException("Explicit Serializable SnapshotRecord did not serialize its required fields: " + json);
            return json;
        }
        static string InputJson(PlayerInputFrame input)
        {
            string json = JsonUtility.ToJson(new InputRecord { Tick=input.Tick, Sequence=input.Sequence,
                Revision=input.Revision, HeroRevision=input.HeroRevision, JumpSequence=input.JumpSequence,
                FireSequence=input.FireSequence, ReleaseSequence=input.ReleaseSequence, Move=input.Move,
                Look=input.Look, Fire=input.Fire, Swim=input.Swim, CancelFire=input.CancelFire });
            if (string.IsNullOrEmpty(json) || !json.Contains("\"Move\"") || !json.Contains("\"Sequence\""))
                throw new InvalidOperationException("Explicit Serializable InputRecord did not serialize its required fields: " + json);
            return json;
        }
        BlockFixedGridWorld World => terrain.FixedBlocks;

        [Serializable] sealed class Record
        {
            public string stage, test, unity, profile, topology, snapshot, worldBytes, detail;
            public Vector3 start, requested, nativeDelta, combinedDelta, actualPosition;
            public int nativeFlags, combinedFlags, count, motorCompletions, cursorWrites;
            public float radius, height, skinWidth, stepOffset, requestedDistance, actualDistance;
            public bool independentRequestedClear, independentActualBlocked, productionRequestedBlocked;
            public bool productionActualCrossed, productionEndpointOverlap;
            public uint epoch, round, revision, surfaceRevision;
            public string input, beforeSnapshot, nativeOverlaps;
            public Vector3 standingCenter, fixedClosest, fixedDelta;
            public Vector3Int fixedCell, directUnderCell;
            public bool canStand, nativeShapeFits, fixedStandingOverlap, fixedRawOverlap, directUnderSameTop;
            public bool controllerGrounded, beforeControllerGrounded;
            public int tickIndex, nativeOverlapCount, worldMask, frameId;
            public float standingRadius, standingHalfSegment, fixedDistance, independentFloorPenetration;
            public ulong fixedId;
            public uint fixedGeneration;
        }
        sealed class Witness
        {
            public PlayerSnapshot Initial;
            public Vector3 Requested, NativeDelta;
            public CollisionFlags NativeFlags;
            public int PlatformIndex = -1, GroundingMoves;
        }
        void EnsureReportPath()
        {
            if (!string.IsNullOrEmpty(reportPath)) return;
            string folder = "Reports/BlockSimulation20261018/ActualDisplacement65";
            Directory.CreateDirectory(folder);
            string safeName = string.Concat(TestContext.CurrentContext.Test.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            reportPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + safeName + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            TestContext.Out.WriteLine("ActualDisplacement65 diagnostic: " + Path.GetFullPath(reportPath));
        }
        void Log(Record record)
        {
            EnsureReportPath();
            record.frameId = Time.frameCount;
            record.test = TestContext.CurrentContext.Test.FullName;
            record.unity = Application.unityVersion;
            if (terrain != null && terrain.Initialized && terrain.FixedGridMode)
            {
                record.count = World.Count; record.epoch = World.Epoch; record.round = World.Round;
                record.revision = World.Revision; record.surfaceRevision = World.Surfaces.Revision;
            }
            if (player != null)
            {
                record.profile = player.SwimBody?.Profile?.ContentHash;
                record.actualPosition = player.transform.position;
                if (controller != null)
                {
                    record.radius = controller.radius; record.height = controller.height;
                    record.skinWidth = controller.skinWidth; record.stepOffset = controller.stepOffset;
                    record.controllerGrounded = controller.isGrounded;
                }
            }
            if (arena != null) record.topology = arena.BakedTopology;
            if (PrototypeApp.Current != null) record.cursorWrites = PrototypeApp.Current.SystemCursorWriteCount;
            string line = JsonUtility.ToJson(record);
            records.Add(line);
            // Immediate evidence survives a later failed assertion or reload.
            File.AppendAllText(reportPath, line + Environment.NewLine);
        }
        static void AssertCursorSuppressed()
        {
            Assert.That((bool)typeof(PrototypeApp).GetProperty("SystemCursorWritesSuppressed",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.True,
                "Runner must suppress system cursor writes before Bootstrap.");
        }
        [UnitySetUp] public IEnumerator Setup()
        {
            records.Clear(); observations.Clear(); capturedIgnore = capturedCursor = false;
            terrain = null; floor = null; arena = null; player = null; controller = null; motor = null;
            // Domain reload may reconstruct this fixture. Open the file only
            // after EnterPlayMode and before any runtime setup assertion.
            reportPath = null;
            AssertCursorSuppressed();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            // Complete authored arena before the runtime Awake/capture sequence.
            var authoredRoot = new GameObject("65 actual-displacement authored arena");
            var authored = authoredRoot.AddComponent<PrototypeArena>();
            authored.OwnershipCellSize = GameplayConfig.Map.CellSize;
            authored.SpawnPoints = new Transform[8];
            for (int i = 0; i < 8; i++)
            {
                var spawn = new GameObject("actual-displacement spawn " + i).transform;
                spawn.SetParent(authoredRoot.transform, false);
                spawn.position = new Vector3(i % 4 * 2 - 3, .04f, i < 4 ? -3 : 3);
                authored.SpawnPoints[i] = spawn;
            }
            var map = GameObject.CreatePrimitive(PrimitiveType.Cube);
            map.name = "65 actual-displacement original map floor";
            map.transform.SetParent(authoredRoot.transform, false);
            var size = new Vector3(8, .3f, 8); var center = new Vector3(0, -.15f, 0);
            var mesh = Object.Instantiate(map.GetComponent<MeshFilter>().sharedMesh);
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size) + center;
            mesh.vertices = vertices; mesh.RecalculateBounds(); map.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mapBox = map.GetComponent<BoxCollider>(); mapBox.size = size; mapBox.center = center;
            var paint = map.AddComponent<PaintSurface>(); paint.SurfaceId = 6531;
            paint.Scores = true; paint.WalkableSize = new Vector2(8, 8);
            paint.ShapeAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
            Assert.That(paint.ShapeAtlas, Is.Not.Null);
            // All native ledges exist before map capture/topology/runtime Awake.
            // Their complete independent paint/map adapters are real authored geometry.
            for (int index = 0; index < PlatformTops.Length; index++)
                AuthorNativePlatform(authoredRoot.transform, index, paint.ShapeAtlas);
            AuthorDescendingNativeCeiling(authoredRoot.transform, paint.ShapeAtlas);
            authored.BakedTopology = authored.ComputeTopology();
            yield return new EnterPlayMode();
            EnsureReportPath();
            Log(new Record { stage="setup.after-enter-play-mode", detail="Report identity initialized after the real domain transition." });
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            HeroMigrationTests.Load();
            arena = GameObject.Find("65 actual-displacement authored arena").GetComponent<PrototypeArena>();
            arena.InitializeRuntime();
            floor = GameObject.Find("65 actual-displacement original map floor").GetComponent<BoxCollider>();
            Assert.That(arena.SpawnPoints.Length, Is.EqualTo(8)); Assert.That(arena.Surfaces.Count, Is.EqualTo(5));
            Assert.That(arena.Surfaces[6531].Ownership, Is.Not.Null);
            Assert.That(arena.TotalArea, Is.GreaterThan(0));
            priorTerrainPlayerIgnore = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, 8); capturedIgnore = true;
            terrain = new GameObject("65 actual-displacement fixed terrain").AddComponent<BlockTerrain>();
            terrain.Initialize(arena); terrain.EnableFixedGridMode(); terrain.Tick(true, 10, Dt);
            Assert.That(terrain.Initialized && terrain.FixedGridMode && terrain.Dynamic == null, Is.True);
            Assert.That(terrain.IsAuthoredMapCollider(floor), Is.True);
            nativePlatforms.Clear();
            for (int index = 0; index < PlatformTops.Length; index++)
            {
                var platform = GameObject.Find(NativePlatformName(index)).GetComponent<BoxCollider>();
                nativePlatforms.Add(platform);
                Assert.That(terrain.IsAuthoredMapCollider(platform), Is.True);
                Assert.That(platform.transform.lossyScale, Is.EqualTo(Vector3.one));
                Assert.That(arena.Surfaces[6532 + index].Ownership, Is.Not.Null);
                Vector3 probe = new Vector3(PlatformCenterX, 2, PlatformCentersZ[index]);
                Assert.That(Physics.Raycast(probe, Vector3.down, out var topHit, 3,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(topHit.collider, Is.SameAs(platform));
                Assert.That(topHit.point.y, Is.EqualTo(PlatformTops[index]).Within(.000001f));
                Assert.That(terrain.TryGetSurfacePaint(topHit.collider, topHit.point, topHit.normal, out _), Is.True,
                    "Each native platform must have the real independent surface adapter.");
            }
            var ceiling = GameObject.Find(NativeCeilingName).GetComponent<BoxCollider>();
            Assert.That(terrain.IsAuthoredMapCollider(ceiling), Is.True);
            Assert.That(arena.Surfaces[6535].Ownership, Is.Not.Null);
            Assert.That(ceiling.enabled && !ceiling.isTrigger && ceiling.attachedRigidbody == null, Is.True);
            Log(new Record { stage="setup.native-ceiling-transform", requested=ceiling.transform.localScale,
                nativeDelta=ceiling.transform.lossyScale, detail="Authored local unit scale and unit-scale parent; lossy scale is derived from the rotated float matrix." });
            Assert.That(ceiling.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(ceiling.transform.parent.lossyScale, Is.EqualTo(Vector3.one));
            Assert.That(Vector3.Dot(ceiling.transform.up, Vector3.up), Is.GreaterThan(.99f));
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f)); Assert.That(BlockWorld.Top, Is.EqualTo(8f));
            Assert.That(BlockFixedGridWorld.MaximumStackLayers, Is.EqualTo(4));
            Assert.That(BlockFixedGridWorld.MaximumStackHeight, Is.EqualTo(1.2f));
            cursorWrites = PrototypeApp.Current.SystemCursorWriteCount; capturedCursor = true;
            Assert.That(cursorWrites, Is.Zero);
            // These are legal diagnostic initial map/grid states, not paid shots.
            int nativeBefore = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length;
            for (int z = -4; z <= 4; z++) for (int x = -7; x <= 7; x++) AddBase(new Vector3Int(x, 0, z));
            Assert.That(World.Count, Is.EqualTo(135));
            Assert.That(Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length, Is.EqualTo(nativeBefore),
                "Fixed occupancy must not be represented by native block colliders");
            Assert.That(terrain.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            Assert.That(prefab, Is.Not.Null);
            player = Object.Instantiate(prefab).GetComponent<PrototypePlayer>(); player.gameObject.layer = 8;
            player.SwimBody.Bind(PaperBodyTests.Profile("RifleGirl"));
            controller = player.GetComponent<CharacterController>(); motor = new PlayerMotorSimulation(controller, arena);
            observerFaults = PlayerMotorSimulation.MotorBranchObserverErrors;
            PlayerMotorSimulation.MotorBranchObserved += Observe;
            Log(new Record { stage="setup", detail="Real complete map and committed fixed cells; no native fixed collision objects.",
                worldBytes=Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true))) });
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe;
            Log(new Record { stage="cleanup", detail=TestContext.CurrentContext.Result.Outcome.ToString() });
            // Every record was already persisted before the corresponding assertion.
            if (capturedIgnore) Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, priorTerrainPlayerIgnore);
            bool untouched = !capturedCursor || PrototypeApp.Current == null || PrototypeApp.Current.SystemCursorWriteCount == cursorWrites;
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(untouched, Is.True, "No system cursor write in this fixture");
        }
        void Observe(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
        { if (ReferenceEquals(source, motor)) observations.Add(observation); }
        static string NativePlatformName(int index) => "65 actual-displacement native ledge " + index;
        static void AuthorNativePlatform(Transform parent, int index, Texture2D atlas)
        {
            var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = NativePlatformName(index); platform.transform.SetParent(parent, false);
            // x [-3,-2.1], 0.9m-wide z lanes fully contain the unchanged
            // 0.35m-radius character plus its original 0.03m skin on both sides.
            // This native geometry never represents an occupied data block.
            var size = new Vector3(.9f, .3f, .9f);
            var center = new Vector3(PlatformCenterX, PlatformTops[index] - .15f, PlatformCentersZ[index]);
            var filter = platform.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh); var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size) + center;
            mesh.vertices = vertices; mesh.RecalculateBounds(); filter.sharedMesh = mesh;
            var box = platform.GetComponent<BoxCollider>(); box.size = size; box.center = center;
            var paint = platform.AddComponent<PaintSurface>(); paint.SurfaceId = 6532 + index;
            paint.Scores = true; paint.WalkableSize = new Vector2(.9f, .9f); paint.ShapeAtlas = atlas;
        }
        static void AuthorDescendingNativeCeiling(Transform parent, Texture2D atlas)
        {
            // Genuine authored map geometry: the ordinary standing capsule
            // has overhead clearance, while the unchanged native auto-step
            // up pass can meet this descending underside. Native Move alone
            // must establish whether its side/down passes produce a witness.
            var ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ceiling.name = NativeCeilingName; ceiling.transform.SetParent(parent, false);
            ceiling.transform.position = new Vector3(-2.05f, 2.26f, PlatformCentersZ[1]);
            ceiling.transform.rotation = Quaternion.Euler(0, 0, -4f);
            var size = new Vector3(2.4f, .1f, .8f);
            var filter = ceiling.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh); var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size);
            mesh.vertices = vertices; mesh.RecalculateBounds(); filter.sharedMesh = mesh;
            var box = ceiling.GetComponent<BoxCollider>(); box.size = size; box.center = Vector3.zero;
            var paint = ceiling.AddComponent<PaintSurface>(); paint.SurfaceId = 6535;
            paint.Scores = true; paint.WalkableSize = new Vector2(size.x, size.z); paint.ShapeAtlas = atlas;
        }
        void AddBase(Vector3Int cell)
        {
            Vector3 center = BlockWorld.Center(cell);
            Assert.That(Physics.Raycast(new Vector3(center.x, 2, center.z), Vector3.down, out var hit, 3,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.collider, Is.SameAs(floor)); Assert.That(hit.point.y, Is.EqualTo(0).Within(.000001f));
            Assert.That(terrain.TryGetSurfacePaint(hit.collider, hit.point, hit.normal, out var paint), Is.True);
            if (paint.Owner != 1) Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, 1, 1, true).Added, Is.EqualTo(1));
            Assert.That(World.Build(new[] { cell }, 1, 1).Added, Is.EqualTo(1), World.LastFailure);
            Assert.That(World.TryGet(cell, out var block), Is.True);
            Assert.That(block.Support.BaseCellY, Is.Zero); Assert.That(block.Handle.IsValid, Is.True);
            Assert.That(BlockFixedGridWorld.GetBounds(block).max.y-block.Support.Height, Is.LessThanOrEqualTo(1.20001f));
        }
        PlayerSnapshot Initial(float feetY, byte team = 1, bool compact = false) => new()
        {
            HeroId=1, HeroRevision=1, Revision=1, Team=team, Health=100, Ink=50,
            LifeState=PlayerLifeState.Alive, Position=new Vector3(.15f, feetY, .15f),
            Grounded=true, Movement=MovementMode.Human, CompactBody=compact,
            FixedBlockEpoch=World.Epoch, FixedBlockRound=World.Round, FixedBlockRevision=World.Revision,
            GroundNormal=Vector3.up, SimulatedAt=10, SimulationTick=1
        };
        void Restore(PlayerSnapshot s)
        {
            motor.Restore(s, false); Physics.SyncTransforms(); observations.Clear();
            var shape=HeroBodyShape.For(s.HeroId);
            Assert.That(controller.radius, Is.EqualTo(shape.Radius));
            Assert.That(controller.height, Is.EqualTo(s.Swimming||s.CompactBody?shape.CompactHeight:shape.Height));
            Assert.That(controller.skinWidth, Is.EqualTo(shape.SkinWidth));
            Assert.That(controller.stepOffset, Is.EqualTo(shape.StepOffset));
            Assert.That(controller.transform.position, Is.EqualTo(s.Position));
        }
        static float DistanceSquared(Vector3 center, float halfSegment, Vector3Int cell)
        {
            var expanded=new Bounds(BlockWorld.Center(cell),Vector3.one*BlockWorld.CellSize+Vector3.up*(2*halfSegment));
            return (center-expanded.ClosestPoint(center)).sqrMagnitude;
        }
        bool IndependentOverlap(Vector3 center,float halfSegment,float radius)
            => World.Blocks.Values.Any(b=>DistanceSquared(center,halfSegment,b.Cell)<radius*radius-1e-7f);
        bool IndependentSweep(Vector3 center,float halfSegment,float radius,Vector3 delta)
        {
            foreach(var block in World.Blocks.Values)
            {
                // Squared distance to this convex expanded AABB along the linear
                // segment is convex. Include both endpoints and its bounded minimum.
                float lo=0,hi=1;
                for(int i=0;i<72;i++)
                {
                    float a=(2*lo+hi)/3,b=(lo+2*hi)/3;
                    if(DistanceSquared(center+delta*a,halfSegment,block.Cell)<DistanceSquared(center+delta*b,halfSegment,block.Cell))hi=b;
                    else lo=a;
                }
                float minimum=Mathf.Min(DistanceSquared(center,halfSegment,block.Cell),
                    Mathf.Min(DistanceSquared(center+delta,halfSegment,block.Cell),DistanceSquared(center+delta*((lo+hi)*.5f),halfSegment,block.Cell)));
                if(minimum<=radius*radius+1e-7f)return true;
            }
            return false;
        }
        void ProbeStanding(PlayerSnapshot state, string stage, int tickIndex = -1,
            PlayerInputFrame input = default, PlayerSnapshot before = default)
        {
            var shape = HeroBodyShape.For(state.HeroId);
            float radius = Mathf.Max(.05f, shape.Radius - .025f);
            float segment = Mathf.Max(0, shape.Height * .5f - radius);
            Vector3 center = state.Position + shape.Center;
            // Read the exact two production domains separately. These read-only
            // probes do not decide movement or weaken a collision predicate.
            var native = Physics.OverlapCapsule(center + Vector3.up * segment,
                center - Vector3.up * segment, radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            bool nativeFits = shape.Fits(state.Position, new Collider[32], PlayerMotorSimulation.WorldMask);
            bool fixedOverlap = BlockFixedGridQueries.OverlapCapsule(World, center, segment, radius,
                Vector3.up, out var standingHit, floorContactTolerance: .001f);
            bool rawOverlap = BlockFixedGridQueries.OverlapCapsule(World, center, segment, radius,
                Vector3.up, out var rawHit);
            var diagnosticHit = fixedOverlap ? standingHit : rawHit;
            Vector3 closest = default, delta = default;
            Vector3Int underCell = default; bool sameTop = false;
            float penetration = 0;
            if (fixedOverlap || rawOverlap)
            {
                var bounds = BlockFixedGridWorld.GetBounds(diagnosticHit.State);
                var expanded = bounds; expanded.Expand(Vector3.up * (2 * segment));
                closest = expanded.ClosestPoint(center); delta = center - closest;
                underCell = new Vector3Int(BlockWorld.CellAt(center).x, diagnosticHit.State.Cell.y, BlockWorld.CellAt(center).z);
                sameTop = World.TryGet(underCell, out var under) &&
                    Mathf.Abs(BlockFixedGridWorld.GetBounds(under).max.y - bounds.max.y) < .000001f;
                penetration = bounds.max.y - (center.y - segment - radius);
            }
            Log(new Record { stage=stage, tickIndex=tickIndex, snapshot=SnapshotJson(state),
                beforeSnapshot=SnapshotJson(before), input=InputJson(input),
                canStand=motor.CanStand(state.Position), nativeShapeFits=nativeFits,
                fixedStandingOverlap=fixedOverlap, fixedRawOverlap=rawOverlap,
                standingCenter=center, standingRadius=radius, standingHalfSegment=segment,
                fixedCell=diagnosticHit.State.Cell, fixedId=diagnosticHit.State.Handle.Id,
                fixedGeneration=diagnosticHit.State.Handle.Generation, fixedDistance=diagnosticHit.Distance,
                fixedClosest=closest, fixedDelta=delta, independentFloorPenetration=penetration,
                directUnderCell=underCell, directUnderSameTop=sameTop,
                worldMask=PlayerMotorSimulation.WorldMask, nativeOverlapCount=native.Length,
                nativeOverlaps=JsonUtility.ToJson(new NativeOverlapList { colliders=native.Select(c=>new NativeOverlapRecord {
                    name=c.name, type=c.GetType().FullName, root=c.transform.root.name,
                    instance=c.GetInstanceID(), layer=c.gameObject.layer, enabled=c.enabled,
                    trigger=c.isTrigger, active=c.gameObject.activeInHierarchy }).ToArray() }),
                detail="Diagnostics before assertions; original formal shape, 1mm floor-only standing tolerance and all pass predicates unchanged." });
        }

        Witness FindActualNativeStepWitness()
        {
            int tested=0;
            // These exact feet heights were actually observed in build60. Search
            // only ordinary short horizontal requests, with unchanged controller.
            foreach(float y in new[]{.29937440156936646f,.29973262548446655f})
            foreach(var requested in new[]{Vector3.right*.02f,Vector3.forward*.02f,Vector3.right*.04f,Vector3.forward*.04f})
            {
                tested++; var initial=Initial(y); Restore(initial);
                float radius=controller.radius-.001f,segment=Mathf.Max(0,controller.height*.5f-controller.radius);
                Vector3 center=initial.Position+controller.center;
                bool requestedClear=!IndependentSweep(center,segment,radius,requested);
                bool requestedHit=BlockFixedGridQueries.CastCapsule(World,center,segment,radius,requested,requested.magnitude,out _);
                Assert.That(requestedClear&&!requestedHit, Is.True,
                    "A raw-native mismatch witness must begin with a genuinely clear requested data sweep");
                bool beforeGrounded = controller.isGrounded;
                ProbeStanding(initial, "native-witness-before-move", tested);
                var flags=controller.Move(requested); Physics.SyncTransforms();
                Vector3 actual=controller.transform.position-initial.Position;
                bool independent=IndependentSweep(center,segment,radius,actual)||IndependentOverlap(center+actual,segment,radius-.001f);
                bool crossed=actual.sqrMagnitude>1e-12f&&BlockFixedGridQueries.CastCapsule(World,center,segment,radius,actual,actual.magnitude,out var nativeHit)&&
                    nativeHit.Distance<actual.magnitude-.001f;
                bool overlaps=terrain.FixedCapsuleOverlaps(center+actual,segment,radius-.001f);
                Log(new Record { stage="native-witness-search", start=initial.Position, requested=requested,nativeDelta=actual,nativeFlags=(int)flags,
                    requestedDistance=requested.magnitude,actualDistance=actual.magnitude,independentRequestedClear=requestedClear,
                    productionRequestedBlocked=requestedHit,independentActualBlocked=independent,productionActualCrossed=crossed,productionEndpointOverlap=overlaps,
                    beforeControllerGrounded=beforeGrounded });
                if(Vector3.Distance(actual,requested)<=.001f||!independent||!crossed&&!overlaps)continue;
                Restore(initial); var repeatFlags=controller.Move(requested); Physics.SyncTransforms();
                Assert.That(Vector3.Distance(controller.transform.position-initial.Position,actual),Is.LessThan(1e-5f),
                    "The real native step witness must repeat after the identical production restore");
                Assert.That(repeatFlags,Is.EqualTo(flags));
                return new Witness{Initial=initial,Requested=requested,NativeDelta=actual,NativeFlags=flags};
            }
            Log(new Record { stage="legacy-native-eight-untriggered", count=tested,
                detail="The original eight restore-only requests produced no real native crossing witness. Those failed attempts remain recorded; historical feet heights alone did not reproduce their preceding native state." });
            return FindNativePlatformStepWitness(tested);
        }

        PlayerSnapshot PrepareNativePlatform(int platformIndex, int groundingMoves)
        {
            Assert.That(platformIndex, Is.InRange(0, nativePlatforms.Count - 1));
            Assert.That(groundingMoves, Is.GreaterThan(0));
            // Platform 1 starts genuinely airborne below its authored ceiling;
            // the following real downward Move still establishes feet and grounding.
            var start = Initial(PlatformTops[platformIndex] + (platformIndex == 1 ? .08f : .7f));
            start.Position = new Vector3(PlatformCenterX, start.Position.y, PlatformCentersZ[platformIndex]);
            start.Grounded = false; start.Movement = MovementMode.Air;
            Restore(start);
            Vector3 airborneCenter = start.Position + controller.center;
            float airborneSegment = Mathf.Max(0, controller.height * .5f - controller.radius);
            Assert.That(Physics.CheckCapsule(airborneCenter - Vector3.up * airborneSegment,
                airborneCenter + Vector3.up * airborneSegment, controller.radius,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.False,
                "Real airborne precondition must not start embedded in the ceiling or support.");
            // Restore establishes only the airborne initial position. Each
            // candidate and every repeat/control replay builds native contact
            // state again using the exact same actual downward Moves.
            for (int move = 0; move < groundingMoves; move++)
            {
                Vector3 before = controller.transform.position;
                Vector3 request = Vector3.down * (move == 0 ? 1f : .05f);
                var flags = controller.Move(request); Physics.SyncTransforms();
                Log(new Record { stage="native-platform-grounding", tickIndex=move,
                    start=before, requested=request, nativeDelta=controller.transform.position-before,
                    nativeFlags=(int)flags, detail="platform=" + platformIndex + "; top=" + PlatformTops[platformIndex] +
                        "; groundingMoves=" + groundingMoves + "; actual native contact, no forced grounded or feet height." });
            }
            var actual = start;
            actual.Position = controller.transform.position;
            actual.Grounded = controller.isGrounded;
            actual.Movement = actual.Grounded ? MovementMode.Human : MovementMode.Air;
            Assert.That(Physics.Raycast(actual.Position + Vector3.up * .2f, Vector3.down, out var supportHit, 1,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(supportHit.collider, Is.SameAs(nativePlatforms[platformIndex]),
                "The actual native landing must remain above its intended authored platform.");
            Assert.That(supportHit.point.y, Is.EqualTo(PlatformTops[platformIndex]).Within(.000001f));
            Assert.That(supportHit.normal.y, Is.GreaterThan(.999f));
            AssertFormalStandingShape();
            Log(new Record { stage="native-platform-grounded-state", snapshot=SnapshotJson(actual),
                detail="platform=" + platformIndex + "; groundingMoves=" + groundingMoves +
                    "; snapshot feet/grounded copied only from the real native controller." });
            return actual;
        }

        void AssertFormalStandingShape()
        {
            var shape = HeroBodyShape.For(1);
            Assert.That(controller.radius, Is.EqualTo(shape.Radius));
            Assert.That(controller.height, Is.EqualTo(shape.Height));
            Assert.That(controller.center, Is.EqualTo(shape.Center));
            Assert.That(controller.skinWidth, Is.EqualTo(shape.SkinWidth));
            Assert.That(controller.stepOffset, Is.EqualTo(shape.StepOffset));
        }

        void PrepareWitness(Witness witness)
        {
            if (witness.PlatformIndex < 0) { Restore(witness.Initial); return; }
            var actual = PrepareNativePlatform(witness.PlatformIndex, witness.GroundingMoves);
            Assert.That(actual.Grounded, Is.True, "Every repeat/control must reproduce real native grounding.");
            Assert.That(Vector3.Distance(actual.Position, witness.Initial.Position), Is.LessThan(1e-5f),
                "Every native repeat, no-step control and consumer start must replay the same actual grounded feet.");
            // Do not Restore here: disabling/re-enabling the controller would
            // erase the native contact history that this platform supplies.
        }

        Witness FindNativePlatformStepWitness(int legacyTested)
        {
            int tested = 0;
            foreach (int platformIndex in new[] { 0, 1, 2 })
            foreach (int groundingMoves in new[] { 1, 2, 4 })
            foreach (float stride in new[] { .85f, .90f, 1.00f })
            {
                tested++;
                var initial = PrepareNativePlatform(platformIndex, groundingMoves);
                Vector3 requested = Vector3.right * stride;
                float radius=controller.radius-.001f, segment=Mathf.Max(0,controller.height*.5f-controller.radius);
                Vector3 center=initial.Position+controller.center;
                bool requestedClear=!IndependentSweep(center,segment,radius,requested);
                bool requestedHit=BlockFixedGridQueries.CastCapsule(World,center,segment,radius,requested,requested.magnitude,out _);
                // The strict target is still the original one-layer floor, and
                // original y=.3 +/-2mm assertions remain below. Ineligible
                // physical starts are recorded, never snapped into eligibility.
                bool eligible = initial.Grounded && Mathf.Abs(initial.Position.y - .3f) <= .002f &&
                    requestedClear && !requestedHit;
                Log(new Record { stage="native-platform-candidate", snapshot=SnapshotJson(initial),
                    requested=requested, independentRequestedClear=requestedClear, productionRequestedBlocked=requestedHit,
                    beforeControllerGrounded=controller.isGrounded,
                    detail="platform=" + platformIndex + "; groundingMoves=" + groundingMoves + "; stride=" + stride +
                        "; eligible=" + eligible + "; actual-foot target and all clear-request predicates required." });
                if (!eligible) continue;
                ProbeStanding(initial, "native-platform-before-move", tested);
                var flags=controller.Move(requested); Physics.SyncTransforms();
                Vector3 actual=controller.transform.position-initial.Position;
                bool independent=IndependentSweep(center,segment,radius,actual)||IndependentOverlap(center+actual,segment,radius-.001f);
                bool crossed=actual.sqrMagnitude>1e-12f&&BlockFixedGridQueries.CastCapsule(World,center,segment,radius,actual,actual.magnitude,out var nativeHit)&&
                    nativeHit.Distance<actual.magnitude-.001f;
                bool overlaps=terrain.FixedCapsuleOverlaps(center+actual,segment,radius-.001f);
                Log(new Record { stage="native-platform-witness-search", start=initial.Position, snapshot=SnapshotJson(initial),
                    requested=requested,nativeDelta=actual,nativeFlags=(int)flags,
                    requestedDistance=requested.magnitude,actualDistance=actual.magnitude,independentRequestedClear=requestedClear,
                    productionRequestedBlocked=requestedHit,independentActualBlocked=independent,productionActualCrossed=crossed,
                    productionEndpointOverlap=overlaps,beforeControllerGrounded=initial.Grounded,
                    detail="platform=" + platformIndex + "; groundingMoves=" + groundingMoves + "; tested=" + tested });
                if(Vector3.Distance(actual,requested)<=.001f||!independent||!crossed&&!overlaps)continue;
                var witness=new Witness { Initial=initial, Requested=requested, NativeDelta=actual, NativeFlags=flags,
                    PlatformIndex=platformIndex, GroundingMoves=groundingMoves };
                PrepareWitness(witness);
                var repeatFlags=controller.Move(requested); Physics.SyncTransforms();
                Vector3 repeatDelta=controller.transform.position-initial.Position;
                Log(new Record { stage="native-platform-repeat", start=initial.Position, requested=requested,
                    nativeDelta=repeatDelta,nativeFlags=(int)repeatFlags,
                    detail="Repeated complete real grounding precondition before the exact same native horizontal Move." });
                Assert.That(Vector3.Distance(repeatDelta,actual),Is.LessThan(1e-5f),
                    "The real platform step witness must repeat after identical actual native grounding.");
                Assert.That(repeatFlags,Is.EqualTo(flags));
                Assert.That(requestedClear&&!requestedHit,Is.True);
                return witness;
            }
            Assert.Fail("No real native step-down crossing witness after the original " + legacyTested +
                " restore-only attempts and " + tested + " actual-grounded native platform candidates. " +
                "All attempts remain recorded. Do not fake Move, feet height or grounded state, or relax the witness.");
            return null;
        }
        [UnityTest, Timeout(600000)] public IEnumerator NativeStepDownChangesActualSweepAndCombinedConsumerKeepsTheDataFloorLegal()
        {
            string original=Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true)));
            var witness=FindActualNativeStepWitness();
            Assert.That(witness.NativeDelta.y, Is.LessThan(-.001f),
                "The real raw-native witness must include downward displacement, not merely a changed sideways path.");
            // An explicit native control measures the safe retry candidate. This
            // temporary per-call setting is restored before testing production.
            PrepareWitness(witness); float authoredStep=controller.stepOffset;
            CollisionFlags noStepFlags;
            controller.stepOffset=0;
            try { noStepFlags=controller.Move(witness.Requested); }
            finally { controller.stepOffset=authoredStep; }
            Physics.SyncTransforms(); Vector3 noStepDelta=controller.transform.position-witness.Initial.Position;
            float controlRadius=controller.radius-.001f,controlSegment=Mathf.Max(0,controller.height*.5f-controller.radius);
            Vector3 controlCenter=witness.Initial.Position+controller.center;
            Assert.That(IndependentSweep(controlCenter,controlSegment,controlRadius,noStepDelta),Is.False);
            Assert.That(IndependentOverlap(controlCenter+noStepDelta,controlSegment,controlRadius-.001f),Is.False);
            Assert.That(Vector3.Dot(noStepDelta,witness.Requested.normalized),Is.GreaterThan(witness.Requested.magnitude*.9f));
            Assert.That(controller.stepOffset,Is.EqualTo(authoredStep));
            Log(new Record{stage="native-step-disabled-control",start=witness.Initial.Position,requested=witness.Requested,
                nativeDelta=noStepDelta,nativeFlags=(int)noStepFlags,detail="Temporary native control only; authored stepOffset restored before production consumer."});
            PrepareWitness(witness); float step=controller.stepOffset;
            Assert.That(CombinedMove,Is.Not.Null);
            CollisionFlags flags;
            try { flags=(CollisionFlags)CombinedMove.Invoke(motor,new object[]{witness.Requested,Dt}); }
            catch(TargetInvocationException e){throw e.InnerException??e;}
            Physics.SyncTransforms(); Vector3 delta=controller.transform.position-witness.Initial.Position;
            float radius=controller.radius-.001f,segment=Mathf.Max(0,controller.height*.5f-controller.radius);
            Vector3 center=witness.Initial.Position+controller.center;
            Assert.That(IndependentSweep(center,segment,radius,delta),Is.False,
                "Accepted actual movement must remain clear of fixed data, independently of production Cast");
            Assert.That(IndependentOverlap(center+delta,segment,radius-.001f),Is.False);
            Assert.That(Physics.CheckCapsule(center+delta-Vector3.up*segment,center+delta+Vector3.up*segment,
                radius,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore),Is.False);
            Assert.That(Vector3.Dot(delta,witness.Requested.normalized),Is.GreaterThan(witness.Requested.magnitude*.9f),
                "Rejecting the unexpected native step must preserve the legal horizontal stride");
            Assert.That(controller.transform.position.y,Is.EqualTo(.3f).Within(.002f));
            Assert.That(Vector3.Distance(delta,noStepDelta),Is.LessThan(.0001f),
                "Production combined move must reach the independently measured legal native no-step result");
            Assert.That(controller.stepOffset,Is.EqualTo(step));
            Assert.That(Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true))),Is.EqualTo(original));
            Log(new Record{stage="combined-consumer",start=witness.Initial.Position,requested=witness.Requested,nativeDelta=witness.NativeDelta,
                nativeFlags=(int)witness.NativeFlags,combinedDelta=delta,combinedFlags=(int)flags,
                detail="Real raw Move differs; production consumer validates actual movement, preserves safe travel and restores formal stepOffset."});
            yield return null;
        }
        [UnityTest, Timeout(600000)] public IEnumerator OrdinaryMotorExitsCompactAndTravelsSafelyAtBothActualOneLayerContactHeights()
        {
            string original=Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true)));
            foreach(byte team in new byte[]{1,2})
            foreach(float feetY in new[]{.29937440156936646f,.29973262548446655f})
            {
                var s=Initial(feetY,team,true); Restore(s);
                var input=new PlayerInputFrame{Revision=s.Revision,HeroRevision=s.HeroRevision,Look=Vector2.zero,
                    Move=new Vector2(.5f,0),Fire=true,FireSequence=1};
                var weapon=GameplayConfig.GetWeapon(s.HeroId); float originalStep=controller.stepOffset;
                ProbeStanding(s, "ordinary-motor.initial");
                for(int i=0;i<60;i++)
                {
                    s.SimulationTick++; s.SimulatedAt+=Dt; input.Sequence++; input.Tick++;
                    var before = s;
                    observations.Clear(); motor.Step(ref s,input,Dt,s.SimulatedAt,true,weapon.ShootMoveSpeed,true);
                    ProbeStanding(s, "ordinary-motor.before-assert", i, input, before);
                    foreach (var observation in observations)
                        Log(new Record { stage="ordinary-motor.branch-before-assert", tickIndex=i,
                            beforeSnapshot=SnapshotJson(observation.Before), snapshot=SnapshotJson(observation.After),
                            requested=observation.RequestedMove, combinedFlags=(int)observation.Collisions,
                            detail=observation.Branch + "; standingEvaluated=" + observation.StandingEvaluated +
                                "; standingFits=" + observation.StandingFits + "; controllerGrounded=" + observation.ControllerGrounded });
                    Assert.That(observations.Count(o=>o.Branch==PlayerMotorSimulation.MotorTraceBranch.StepComplete),Is.EqualTo(1));
                    Assert.That(s.CompactBody||s.Swimming,Is.False); Assert.That(motor.CanStand(s.Position),Is.True);
                    Assert.That(s.Position.y,Is.EqualTo(.3f).Within(.002f));
                    Assert.That(controller.transform.position,Is.EqualTo(s.Position));
                    Assert.That(controller.stepOffset,Is.EqualTo(originalStep));
                    float segment=Mathf.Max(0,controller.height*.5f-controller.radius),radius=controller.radius-.002f;
                    Vector3 center=s.Position+controller.center;
                    Assert.That(IndependentOverlap(center,segment,radius),Is.False);
                    Assert.That(Physics.CheckCapsule(center-Vector3.up*segment,center+Vector3.up*segment,radius,
                        PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore),Is.False);
                    Log(new Record{stage="ordinary-motor",snapshot=SnapshotJson(s),requested=new Vector3(.5f,0,0),motorCompletions=1,
                        detail="Actual Motor.Step; this is not a paid weapon shot or authority outer-Step test."});
                }
                Assert.That(s.Position.x,Is.GreaterThan(.65f)); Assert.That(s.Grounded,Is.True);
                Assert.That(PlayerMotorSimulation.TryGetGround(s.Position,out var support),Is.True);
                Assert.That(support.Kind,Is.EqualTo(BlockContactKind.FixedGrid));
                Assert.That(support.Owner,Is.EqualTo(1)); Assert.That(World.TryGet(support.FixedHandle,out _),Is.True);
            }
            Assert.That(PlayerMotorSimulation.MotorBranchObserverErrors,Is.EqualTo(observerFaults));
            Assert.That(Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true))),Is.EqualTo(original));
            yield return null;
        }
    }
}
#endif
