#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class SwimMovementTests
    {
        const float Dt = 1f / 60;
        CharacterController controller;
        PlayerMotorSimulation motor;
        PaintSurface floor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        double now;
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            var ground = new GameObject("Paintable ground");
            var box = ground.AddComponent<BoxCollider>(); box.size = new Vector3(100, 1, 100); box.center = Vector3.down * .5f;
            floor = ground.AddComponent<PaintSurface>(); floor.Scores = true; floor.WalkableSize = new Vector2(100, 100); floor.InitializeOwnership(.5f);
            var player = new GameObject("Swim motor") { layer = 8 };
            controller = player.AddComponent<CharacterController>(); controller.height = 1.8f; controller.center = Vector3.up * .9f;
            controller.radius = .35f; controller.skinWidth = .03f; controller.stepOffset = .3f;
            motor = new PlayerMotorSimulation(controller);
            state = new PlayerSnapshot { HeroId = 1, Team = 1, Health = 100, Ink = 100, Position = Vector3.up * .04f, Grounded = true, Revision = 5 };
            motor.Restore(state); input = new PlayerInputFrame { Swim = true, Move = Vector2.up }; now = 0;
        }
        [TearDown] public void Cleanup()
        {
            LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Paint(byte team) { for (int i = 0; i < floor.Ownership.Cells.Length; i++) floor.Ownership.Set(i, team); }
        void Tick(bool fire = false) { now += Dt; motor.Step(ref state, input, Dt, now, fire); }
        void Run(int count) { for (int i = 0; i < count; i++) Tick(); }

        [TestCase(0, 3, SwimSurface.Neutral)] [TestCase(1, 8, SwimSurface.Friendly)]
        public void SurfaceChoosesSpeedRecoveryAndModel(byte team, float speed, SwimSurface source)
        {
            Paint(team); Run(90);
            Assert.That(state.Swimming, Is.True); Assert.That(state.SwimSource, Is.EqualTo(source));
            Assert.That(state.PlanarVelocity.magnitude, Is.EqualTo(speed).Within(.002));
            Assert.That(state.ShowsSwimBody, Is.EqualTo(team == 0)); Assert.That(state.HasInkRecovery, Is.EqualTo(team == 1));
            state.Health = state.Ink = 30; state.LastDamageAt = -10;
            ResourceSimulation.Step(ref state, GameplayConfig.DefaultHero, false, false, Dt, now);
            Assert.That(state.Health, Is.EqualTo(30 + (team == 1 ? 60 : 30) * Dt).Within(.001));
            Assert.That(state.Ink, Is.EqualTo(30 + (team == 1 ? GameplayConfig.DefaultHero.SwimRecoverInk : 10) * Dt).Within(.001));
        }
        [Test] public void NonPaintableGroundAllowsNeutralButAirDoesNot()
        {
            Object.DestroyImmediate(floor); Run(60);
            Assert.That(state.Swimming, Is.True); Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.Neutral));
            Assert.That(PrototypeArena.TryGetGround(new Vector3(0, 10, 0), out _), Is.False);
            state.Position = Vector3.up * 10; state.Swimming = false; state.Grounded = false; state.VerticalSpeed = 0;
            motor.Restore(state); Tick(); Assert.That(state.Swimming, Is.False);
        }
        [Test] public void EnemyPaintCancelsImmediatelyAndCannotBeEntered()
        {
            Paint(1); Run(5); Paint(2); Tick();
            Assert.That(state.Swimming, Is.False); Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.None));
            Run(60); Assert.That(state.Swimming, Is.False);
            Assert.That(state.PlanarVelocity.magnitude, Is.EqualTo(5 * GameplayConfig.DefaultHero.EnemyInkMultiplier).Within(.002));
        }
        [TestCase(0)] [TestCase(1)]
        public void JumpKeepsSourceAndSpeedUntilLanding(byte source)
        {
            Paint(source); Run(60); input.JumpSequence = 1; Tick();
            Assert.That(state.Grounded, Is.False); Assert.That(state.Swimming, Is.True); Assert.That(state.Movement, Is.EqualTo(MovementMode.Air));
            var origin = state.SwimSource; Paint(source == 0 ? (byte)1 : (byte)0);
            for (int i = 0; i < 12; i++)
            {
                Tick(); Assert.That(state.SwimSource, Is.EqualTo(origin)); Assert.That(state.Swimming, Is.True);
                Assert.That(state.ShowsSwimBody, Is.EqualTo(source == 0)); Assert.That(state.HasInkRecovery, Is.False);
                Assert.That(state.PlanarVelocity.magnitude, Is.EqualTo(source == 0 ? 3 : 8).Within(.002));
            }
            for (int i = 0; i < 120 && !state.Grounded; i++) Tick();
            Assert.That(state.Grounded, Is.True); Assert.That(state.SwimSource, Is.Not.EqualTo(origin));
        }
        [Test] public void AirReleaseCannotReenterAndEnemyLandingExits()
        {
            Paint(1); Run(5); input.JumpSequence = 1; Tick(); input.Swim = false; Tick();
            Assert.That(state.Swimming, Is.False); input.Swim = true; Run(5); Assert.That(state.Swimming, Is.False);
            Run(90); Assert.That(state.Swimming, Is.True); input.JumpSequence++; Tick(); Paint(2);
            for (int i = 0; i < 120 && !state.Grounded; i++) Tick();
            Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.False);
        }
        [Test] public void LowCeilingDefersStandingWithoutEnemyInkInvisibility()
        {
            input.Move = Vector2.zero; Paint(1); Run(5);
            var ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube); ceiling.transform.position = Vector3.up * 1.2f;
            ceiling.transform.localScale = new Vector3(3, .25f, 3); Physics.SyncTransforms();
            Assert.That(motor.CanStand(state.Position), Is.False); Paint(2); Tick();
            Assert.That(state.Swimming, Is.False); Assert.That(state.CompactBody, Is.True); Assert.That(state.ShowsSwimBody, Is.True);
            Assert.That(state.HasInkRecovery, Is.False); Assert.That(controller.height, Is.EqualTo(.7f));
            input.Fire = true; Tick(true); Assert.That(state.CompactBody, Is.True);
            Object.DestroyImmediate(ceiling); Tick(); Assert.That(state.CompactBody, Is.False); Assert.That(controller.height, Is.EqualTo(1.8f));
        }
        [TestCase(0)] [TestCase(1)]
        public void AirSnapshotReplaysSameMovement(byte source)
        {
            Paint(source); Run(40); input.JumpSequence++; Run(5); var checkpoint = state; double savedTime = now;
            Run(10); var expected = state;
            state = checkpoint; now = savedTime; motor.Restore(state); Run(10);
            Assert.That(Vector3.Distance(state.Position, expected.Position), Is.LessThan(.002));
            Assert.That(state.SwimSource, Is.EqualTo(expected.SwimSource)); Assert.That(state.Revision, Is.EqualTo(5));
        }
        [Test] public void FriendlyParticlesRequireSurfaceMotionAndNeverEmitInAirOrNeutral()
        {
            Paint(1); Run(8); Assert.That(InkCharacterView.ShouldEmitSwimEffect(state), Is.True);
            input.Move = Vector2.zero; Run(12); Assert.That(InkCharacterView.ShouldEmitSwimEffect(state), Is.False);
            input.Move = Vector2.up; Run(8); input.JumpSequence++; Tick(); Assert.That(InkCharacterView.ShouldEmitSwimEffect(state), Is.False);
            state.SwimSource = SwimSurface.Neutral; state.Grounded = true; Assert.That(InkCharacterView.ShouldEmitSwimEffect(state), Is.False);
            state.SwimSource = SwimSurface.Friendly; state.Grounded = false; state.Movement = MovementMode.WallInk;
            state.WallNormal = Vector3.right; state.Velocity = Vector3.up; Assert.That(InkCharacterView.ShouldEmitSwimEffect(state), Is.True);
        }
        [TestCase(0)] [TestCase(5)] [TestCase(-1)]
        public void InvalidNeutralSpeedRejected(float value)
        {
            HeroMigrationTests.Load(rows => rows[0]["neutralSwimSpeed"] = value);
            Assert.Throws<System.InvalidOperationException>(() => GameplayConfig.Validate());
        }
        [Test] public void SharedPrefabMatchesBoundsAndFriendlyModelIsHidden()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var instance = Object.Instantiate(prefab); var player = instance.GetComponent<PrototypePlayer>();
            try
            {
                var body = player.SwimBody; Assert.That(body, Is.Not.Null);
                var bounds = body.HitVolume.sharedMesh.bounds;
                Assert.That(Vector3.Distance(bounds.size, new Vector3(.7f, .4f, 1)), Is.LessThan(.001));
                Assert.That(body.HitVolume.sharedMesh, Is.SameAs(body.BodyRenderer.GetComponent<MeshFilter>().sharedMesh));
                state.Swimming = true; state.SwimSource = SwimSurface.Neutral;
                body.ApplyCollision(state); body.Present(state, Vector3.zero, Quaternion.identity);
                Assert.That(body.FlatHitActive && body.BodyRenderer.enabled, Is.True);
                var pose = body.HitVolume.transform.position;
                body.Present(state, Vector3.right, Quaternion.Euler(0, 45, 0));
                Assert.That(body.HitVolume.transform.position, Is.EqualTo(pose));
                state.SwimSource = SwimSurface.Friendly; body.ApplyCollision(state); body.Present(state, Vector3.zero, Quaternion.identity);
                Assert.That(body.FlatHitActive || body.BodyRenderer.enabled, Is.False);
                state.Health = 0; body.ApplyCollision(state); Assert.That(body.CapsuleHitVolume.enabled || body.FlatHitActive, Is.False);
            }
            finally { Object.DestroyImmediate(instance); }
        }
    }
}
#endif
