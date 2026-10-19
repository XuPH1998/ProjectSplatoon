#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Reports-only regression candidate. Not compiled or executed by its author.
    // This is a constructed boundary fixture, not a reconstruction of round-1 68.
    [Category("BlockFixedGrid"), Category("BlockFixedGridMovementStall69")]
    public sealed class BlockFixedGridMovementStall69Tests
    {
        const float Dt = 1f / 60;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo CombinedMove = typeof(PlayerMotorSimulation).GetMethod("MoveWithFreeBlocks", Private);
        static readonly PropertyInfo TerrainCurrent = typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.Current));
        static readonly PropertyInfo ArenaCurrent = typeof(PrototypeArena).GetProperty(nameof(PrototypeArena.Current));
        // A .3m column ends at y=1.2. The lower cap is within the mover's
        // initial-penetration band, but outside its accepted endpoint radius.
        static readonly Vector3 Start = new(.15f, 1.09599161f, -.2472946f);
        static readonly Vector3 Outward = Vector3.back * .03f;
        GameObject _root;
        BlockTerrain _terrain, _priorTerrain;
        PrototypeArena _arena, _priorArena;
        CharacterController _controller;
        PlayerMotorSimulation _motor;
        cfg.Tables _priorTables;
        readonly bool[] _oldLayerIgnores = new bool[32];
        bool _capturedLayers;
        Material _ownedMaterial;
        Mesh _ownedFallMesh;
        string _worldBytes;
        uint _revision;
        int _completions, _requestedMoves, _zeroMoves, _jumpCompletions;
        uint _jumpBefore, _jumpAfter;
        int _observerErrors;

        [OneTimeSetUp] public void LoadConfiguration()
        {
            _priorTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }
        [OneTimeTearDown] public void RestoreConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _priorTables);

        [SetUp] public void Setup()
        {
            Assert.That(Application.isPlaying, Is.False, "Use the isolated EditMode lane, without Boot or sockets.");
            Assert.That(CombinedMove, Is.Not.Null);
            // As with the existing consumer fixture, the runner owns a disposable scene.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _priorTerrain = BlockTerrain.Current; _priorArena = PrototypeArena.Current;
            _capturedLayers = false;
            for (int layer = 0; layer < 32; layer++) _oldLayerIgnores[layer] = Physics.GetIgnoreLayerCollision(BlockTerrain.TerrainLayer, layer);
            _capturedLayers = true;
            _root = new GameObject("69 constructed fixed movement boundary");
            _arena = _root.AddComponent<PrototypeArena>();
            var floor = new GameObject("69 native original floor"); floor.transform.SetParent(_root.transform, false);
            floor.AddComponent<PaintSurface>().SurfaceId = 6901;
            var box = floor.AddComponent<BoxCollider>(); box.center = new Vector3(0, -.15f, 0); box.size = new Vector3(6, .3f, 6);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(_arena); _terrain.EnableFixedGridMode();
            _ownedMaterial = (Material)typeof(BlockTerrain).GetField("_material", Private).GetValue(_terrain);
            _ownedFallMesh = (Mesh)typeof(BlockTerrain).GetField("_fallMesh", Private).GetValue(_terrain);
            Physics.SyncTransforms();
            Assert.That(_terrain.TryGetSurfacePaint(box, new Vector3(.15f, 0, .15f), Vector3.up, out var paint), Is.True);
            if (paint.Owner != 1) Assert.That(_terrain.PaintSurfaceCell(paint.Key, 1), Is.True);
            // Build before creating/placing the actor. Ordinary admission would
            // reject a new cell overlapping the complete .35m live capsule.
            for (int y = 0; y < 4; y++)
            {
                Assert.That(_terrain.FixedBlocks.Build(new[] { new Vector3Int(0, y, 0) }, 1, 1).Added,
                    Is.EqualTo(1), _terrain.FixedBlocks.LastFailure);
                Assert.That(_terrain.FixedBlocks.TryGet(new Vector3Int(0, y, 0), out _), Is.True);
            }
            Assert.That(_terrain.FixedBlocks.Count, Is.EqualTo(4));
            var actor = new GameObject("69 real native capsule") { layer = 8 }; actor.transform.SetParent(_root.transform, false);
            _controller = actor.AddComponent<CharacterController>(); HeroBodyShape.For(1).Apply(_controller, false);
            _controller.slopeLimit = 45; _controller.minMoveDistance = .001f;
            _controller.detectCollisions = true; _controller.enableOverlapRecovery = true;
            _motor = new PlayerMotorSimulation(_controller, _arena); RestoreStart();
            _worldBytes = Convert.ToBase64String(BlockFixedGridCodec.Encode(_terrain.FixedBlocks.Capture(true)));
            _revision = _terrain.FixedBlocks.Revision;
            _observerErrors = PlayerMotorSimulation.MotorBranchObserverErrors;
            _completions = _requestedMoves = _zeroMoves = _jumpCompletions = 0; _jumpBefore = _jumpAfter = 0;
        }
        [TearDown] public void Cleanup()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe;
            try { BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root); }
            finally
            {
                if (BlockTerrain.Current == _terrain) TerrainCurrent.SetValue(null, _priorTerrain);
                if (PrototypeArena.Current == _arena) ArenaCurrent.SetValue(null, _priorArena);
                if (_root != null) Object.DestroyImmediate(_root);
                if (_ownedMaterial != null) Object.DestroyImmediate(_ownedMaterial);
                if (_ownedFallMesh != null) Object.DestroyImmediate(_ownedFallMesh);
                _ownedMaterial = null; _ownedFallMesh = null;
                if (_capturedLayers) for (int layer = 0; layer < 32; layer++) Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, layer, _oldLayerIgnores[layer]);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        static PlayerSnapshot Standing() => new()
        {
            HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1, Slot = 0, Health = 100, Ink = 50,
            LifeState = PlayerLifeState.Alive, Position = Start, Grounded = true, Movement = MovementMode.Human,
            GroundNormal = Vector3.up, SimulatedAt = 10, SimulationTick = 1
        };
        void RestoreStart() { _motor.Restore(Standing(), false); Physics.SyncTransforms(); }
        void Shape(out Vector3 center, out float segment, out float castRadius)
        {
            center = _controller.transform.position + _controller.center;
            segment = Mathf.Max(0, _controller.height * .5f - _controller.radius);
            castRadius = Mathf.Max(.01f, _controller.radius - .001f);
        }
        void AssertNativeAndEndpointClear(bool requireFullNativeClear = false)
        {
            Shape(out var center, out float segment, out float radius);
            Assert.That(_terrain.FixedCapsuleOverlaps(center, segment, radius - .001f), Is.False,
                "The exact production endpoint validator must accept every retained pose.");
            // Initial witness is fully clear. After ordinary gravity reaches the
            // native floor, retain the production recovery's skin-inset envelope
            // so normal CharacterController skin contact is not a false failure.
            // This does not change or relax the separate data-block endpoint.
            float nativeRadius = requireFullNativeClear ? _controller.radius :
                Mathf.Max(.01f, _controller.radius - Mathf.Max(.005f, _controller.skinWidth));
            Assert.That(Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment,
                nativeRadius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.False,
                requireFullNativeClear ? "The initial witness must be fully map-clear." :
                    "The accepted native pose must pass the existing skin-inset recovery envelope.");
            Assert.That(_controller.stepOffset, Is.EqualTo(.3f));
        }
        void AssertBandAndNativeControls()
        {
            Assert.That(_controller.height, Is.EqualTo(1.8f)); Assert.That(_controller.radius, Is.EqualTo(.35f));
            Assert.That(_controller.skinWidth, Is.EqualTo(.03f)); Assert.That(BlockWorld.CellSize, Is.EqualTo(.3f));
            Shape(out var center, out float segment, out float radius);
            Assert.That(segment, Is.EqualTo(.55f).Within(.000001f));
            Assert.That(_motor.CanStand(Start), Is.True);
            AssertNativeAndEndpointClear(true);
            Assert.That(BlockFixedGridQueries.CastCapsule(_terrain.FixedBlocks, center, segment, radius,
                Outward, Outward.magnitude, out var hit), Is.True, "This test requires the precise initial-penetration band.");
            Assert.That(hit.State.Cell, Is.EqualTo(new Vector3Int(0, 3, 0)));
            Assert.That(hit.Distance, Is.LessThanOrEqualTo(.000001f));
            Assert.That(Vector3.Dot(Outward, hit.Normal), Is.GreaterThan(0), "The request separates from this block.");
            Assert.That(PlayerMotorSimulation.TryGetGround(Start, out var ground), Is.True);
            Assert.That(ground.Kind, Is.EqualTo(BlockContactKind.FixedGrid));
            TestContext.Out.WriteLine($"constructed.initial start={Start:R} castRadius={radius:R} endpointEffectiveRadius={radius - .001f - .00001f:R} hit={hit.State.Cell} distance={hit.Distance:R} normal={hit.Normal:R}");
            var before = _controller.transform.position;
            _controller.Move(Vector3.zero);
            Assert.That(Vector3.Distance(_controller.transform.position, before), Is.LessThan(.000001f), "Native Move(0) must keep this map-clear witness unchanged.");
            _controller.Move(Outward);
            Assert.That(Vector3.Distance(_controller.transform.position - before, Outward), Is.LessThan(.0001f),
                "The exact same native capsule must freely execute the outward request.");
            RestoreStart();
        }
        void AssertWorldUnchanged()
        {
            Assert.That(_terrain.FixedBlocks.Revision, Is.EqualTo(_revision));
            Assert.That(Convert.ToBase64String(BlockFixedGridCodec.Encode(_terrain.FixedBlocks.Capture(true))), Is.EqualTo(_worldBytes));
            Assert.That(_terrain.Dynamic, Is.Null); Assert.That(_root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(PlayerMotorSimulation.MotorBranchObserverErrors, Is.EqualTo(_observerErrors));
        }

        [Test, Timeout(60000)] public void EndpointAdmittedRoundedEdgeMustAllowOutwardTravelInRepeatedProductionMoves()
        {
            AssertBandAndNativeControls();
            int zeroSteps = 0;
            for (int tick = 0; tick < 60; tick++)
            {
                var before = _controller.transform.position;
                var flags = (CollisionFlags)CombinedMove.Invoke(_motor, new object[] { Outward, Dt });
                float actual = Vector3.Distance(before, _controller.transform.position);
                if (actual <= .000001f) zeroSteps++;
                AssertNativeAndEndpointClear();
                if (tick == 0 || tick == 59) TestContext.Out.WriteLine($"constructed.combined tick={tick} before={before:R} after={_controller.transform.position:R} requested={Outward:R} flags={flags} zeroSteps={zeroSteps}");
            }
            AssertWorldUnchanged();
            Assert.That(Vector3.Dot(_controller.transform.position - Start, Vector3.back), Is.GreaterThan(.04f),
                $"A map-clear outward request cannot remain at an endpoint-admitted start for 60 steps. zeroSteps={zeroSteps}; constructed case, not 68 replay.");
        }

        void Observe(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorBranchObservation o)
        {
            if (!ReferenceEquals(motor, _motor)) return;
            if (o.Branch == PlayerMotorSimulation.MotorTraceBranch.DestinationMove)
            {
                if (o.RequestedMove.sqrMagnitude > .000001f) _requestedMoves++;
                if ((o.After.Position - o.Before.Position).sqrMagnitude <= 1e-12f) _zeroMoves++;
            }
            if (o.Branch != PlayerMotorSimulation.MotorTraceBranch.StepComplete) return;
            _completions++;
            if (o.Before.ConsumedJump != o.After.ConsumedJump)
            { _jumpCompletions++; _jumpBefore = o.Before.ConsumedJump; _jumpAfter = o.After.ConsumedJump; }
        }
        [Test, Timeout(60000)] public void OrdinaryMotorWithConsumedJumpMustEscapeTheSameBandUsingExistingBranchObservation()
        {
            AssertBandAndNativeControls();
            var state = Standing();
            var input = new PlayerInputFrame { Revision = 1, HeroRevision = 1, Move = Vector2.down, Look = Vector2.zero };
            PlayerMotorSimulation.MotorBranchObserved += Observe;
            try
            {
                for (int tick = 0; tick < 60; tick++)
                {
                    state.SimulationTick++; state.SimulatedAt += Dt; input.Sequence++; input.Tick++;
                    if (tick == 30) input.JumpSequence = 1;
                    _motor.Step(ref state, input, Dt, state.SimulatedAt, false);
                    AssertNativeAndEndpointClear();
                    Assert.That(_controller.transform.position, Is.EqualTo(state.Position));
                }
            }
            finally { PlayerMotorSimulation.MotorBranchObserved -= Observe; }
            TestContext.Out.WriteLine($"constructed.motor start={Start:R} end={state.Position:R} completions={_completions} requestedMoves={_requestedMoves} zeroMoves={_zeroMoves} jumpCompletions={_jumpCompletions} jumpBefore={_jumpBefore} jumpAfter={_jumpAfter} consumedJump={state.ConsumedJump}");
            AssertWorldUnchanged();
            Assert.That(_completions, Is.EqualTo(60)); Assert.That(_requestedMoves, Is.EqualTo(60));
            Assert.That(_jumpCompletions, Is.EqualTo(1)); Assert.That(_jumpBefore, Is.Zero); Assert.That(_jumpAfter, Is.EqualTo(1));
            Assert.That(Vector3.ProjectOnPlane(state.Position - Start, Vector3.up).magnitude, Is.GreaterThan(.04f),
                $"Ordinary motor received real requests and consumed jump but never escaped. zeroMoves={_zeroMoves}; constructed case, not 68 replay.");
        }
    }
}
#endif
