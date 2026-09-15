#if UNITY_EDITOR
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
    public sealed class PaperLandingTests
    {
        const float Dt = 1f / 60;
        static readonly string[] Heroes = { "RifleGirl", "DualPistolGirl", "ShotgunGirl", "PistolGirl", "RocketLauncherGirl", "MachineGunGirl" };
        CharacterController controller;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        PaintSurface floor;
        double now;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            var ground = new GameObject("Landing floor");
            var box = ground.AddComponent<BoxCollider>(); box.size = new Vector3(40, 1, 40); box.center = Vector3.down * .5f;
            floor = ground.AddComponent<PaintSurface>(); floor.Scores = true; floor.WalkableSize = new Vector2(40, 40); floor.InitializeOwnership(.25f);
            var root = new GameObject("Paper landing motor") { layer = 8 };
            controller = root.AddComponent<CharacterController>(); controller.height = 1.8f; controller.center = Vector3.up * .9f;
            controller.radius = .35f; controller.skinWidth = .03f; controller.stepOffset = .3f;
            motor = new PlayerMotorSimulation(controller);
            state = new PlayerSnapshot { HeroId = 1, Health = 100, Ink = 20, Team = 1, Position = Vector3.up * .04f, Grounded = true };
            input = default; now = 0; motor.Restore(state);
        }
        [TearDown] public void Cleanup()
        {
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Tick() { now += Dt; motor.Step(ref state, input, Dt, now, false); }
        void Run(int count) { for (int i = 0; i < count; i++) Tick(); }
        void Paint(byte owner) { for (int i = 0; i < floor.Ownership.Cells.Length; i++) floor.Ownership.Set(i, owner); }
        void JumpThenOpen(int hero = 1)
        {
            state.HeroId = hero; Run(5); input.JumpSequence++; Run(12);
            Assert.That(state.Grounded, Is.False); Assert.That(state.ShowsSwimBody, Is.False);
            var human = state; input.Swim = true; Tick();
            Assert.That(state.PaperCenter.y, Is.EqualTo(human.Position.y + .9f + state.VerticalSpeed * Dt).Within(.003f), "opening preserves the visible sheet height");
            Assert.That(PlayerMotorSimulation.HumanPosition(state).y, Is.EqualTo(human.Position.y + state.VerticalSpeed * Dt).Within(.003f));
            Assert.That(state.Position.y + PrototypePlayer.CameraPivotOffset(state, null).y,
                Is.EqualTo(human.Position.y + 1.5f + state.VerticalSpeed * Dt).Within(.003f), "coordinate conversion cannot jump the camera");
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void EveryHeroLandsOnlyWhenItsVisibleSheetReachesTheFloor(int hero)
        {
            Paint(1); JumpThenOpen(hero);
            int lowAirTicks = 0;
            for (int tick = 0; tick < 240 && !state.Grounded; tick++)
            {
                var before = state; Tick();
                if (!state.Grounded)
                {
                    Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Air));
                    Assert.That(state.HasInkRecovery, Is.False);
                    if (state.PaperCenter.y < .8f) lowAirTicks++;
                }
                else
                {
                    Assert.That(before.PaperCenter.y, Is.LessThan(.11f), "last airborne sheet must already be at the floor");
                    Assert.That(state.PaperCenter.y, Is.EqualTo(.03f).Within(.003f));
                    Assert.That(Mathf.Abs(state.PaperCenter.y - before.PaperCenter.y), Is.LessThan(.08f), "no 0.9 metre landing snap");
                }
            }
            Assert.That(state.Grounded, Is.True); Assert.That(lowAirTicks, Is.GreaterThan(15));
            Assert.That(state.PaperPose, Is.EqualTo(PaperPose.Ground)); Assert.That(state.HasInkRecovery, Is.True);
            Assert.That(state.AirHumanOffset, Is.Zero);
        }

        [Test] public void RepeatedAirSwitchesPreserveHumanTrajectoryAndVelocity()
        {
            state.Position = Vector3.up * 12; state.Grounded = false; state.Movement = MovementMode.Air; state.VerticalSpeed = -2;
            motor.Restore(state);
            for (int i = 0; i < 20; i++)
            {
                var before = state; input.Swim = i % 2 == 0; Tick();
                Assert.That(PlayerMotorSimulation.HumanPosition(state).y,
                    Is.EqualTo(PlayerMotorSimulation.HumanPosition(before).y + state.VerticalSpeed * Dt).Within(.003f));
                Assert.That(state.Velocity.y, Is.EqualTo(state.VerticalSpeed).Within(.003f));
                Assert.That(state.VerticalSpeed, Is.LessThan(0)); Assert.That(state.Grounded, Is.False);
                Assert.That(state.ShowsSwimBody, Is.EqualTo(input.Swim));
            }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void GroundEffectsAndJumpWaitForContactAndReplayAcrossLanding(byte owner)
        {
            Paint(owner); JumpThenOpen();
            while (state.PaperCenter.y > .65f || state.VerticalSpeed > 0) Tick();
            Assert.That(state.Grounded, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            PlayerSnapshot saved;
            using (var writer = new Unity.Netcode.FastBufferWriter(2048, Unity.Collections.Allocator.Temp))
            {
                writer.WriteNetworkSerializable(state);
                using var reader = new Unity.Netcode.FastBufferReader(writer, Unity.Collections.Allocator.Temp);
                reader.ReadNetworkSerializable(out saved);
            }
            Assert.That(saved.AirHumanOffset, Is.EqualTo(state.AirHumanOffset));
            Assert.That(saved.CameraRebaseOffset, Is.EqualTo(state.CameraRebaseOffset));
            double checkpoint = now; var savedInput = input;
            void Land()
            {
                for (int i = 0; i < 90 && !state.Grounded; i++)
                {
                    input.JumpSequence++; Tick();
                    Assert.That(state.VerticalSpeed, Is.LessThanOrEqualTo(0), "jump requests in the last air segment cannot jump");
                    if (!state.Grounded) Assert.That(state.HasInkRecovery, Is.False);
                }
                Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.EqualTo(owner != 2));
                Assert.That(state.HasInkRecovery, Is.EqualTo(owner == 1));
            }
            Land(); var expected = state;
            state = saved; now = checkpoint; input = savedInput; motor.Restore(state); Land();
            Assert.That(Vector3.Distance(state.Position, expected.Position), Is.LessThan(.001f));
            Assert.That(Vector3.Distance(state.PaperCenter, expected.PaperCenter), Is.LessThan(.001f));
            Assert.That(state.ConsumedJump, Is.EqualTo(expected.ConsumedJump));
            input.JumpSequence++; Tick(); Assert.That(state.VerticalSpeed, Is.GreaterThan(0));
        }

        [Test] public void ReleaseNearTheFloorDefersHumanShapeWithoutPenetratingTheFloor()
        {
            JumpThenOpen();
            while (state.PaperCenter.y > .5f || state.VerticalSpeed > 0) Tick();
            input.Swim = false; Tick();
            Assert.That(state.Grounded, Is.False); Assert.That(state.CompactBody, Is.True);
            Assert.That(state.Position.y, Is.GreaterThan(-.05f));
            for (int i = 0; i < 90 && !state.Grounded; i++) Tick();
            Assert.That(state.Grounded, Is.True); Assert.That(state.CompactBody, Is.False);
            Assert.That(state.ShowsSwimBody, Is.False); Assert.That(controller.height, Is.EqualTo(1.8f));
            Assert.That(state.Position.y, Is.InRange(-.05f, .05f));
        }

        [TestCase(1)] [TestCase(6)]
        public void RenderAndHitProxyShareTheLastAirFrameAndLandingFrame(int hero)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab");
            var body = Object.Instantiate(prefab, controller.transform).GetComponent<SwimBody>();
            body.Bind(PaperBodyTests.Profile(Heroes[hero - 1]));
            JumpThenOpen(hero);
            while (state.PaperCenter.y > .15f || state.VerticalSpeed > 0) Tick();
            for (int i = 0; i < 20; i++)
            {
                body.ApplyCollision(state); body.Present(state, Vector3.up * .5f, Quaternion.identity);
                Assert.That(Vector3.Distance(body.BodyRenderer.transform.position, state.PaperCenter), Is.LessThan(.0001f));
                Assert.That(Vector3.Distance(body.HitVolume.transform.position, state.PaperCenter), Is.LessThan(.0001f));
                if (state.Grounded) break;
                Tick();
            }
            Assert.That(state.Grounded, Is.True);
        }

        [Test] public void FallingPastAPlatformEdgeDoesNotLandOnTheLowerHumanOrigin()
        {
            Object.DestroyImmediate(floor.gameObject);
            var platform = new GameObject("Narrow platform"); var box = platform.AddComponent<BoxCollider>();
            box.size = new Vector3(2, 1, 2); box.center = Vector3.down * .5f;
            state.Position = new Vector3(0, 1, 0); state.Grounded = false; state.Movement = MovementMode.Air; state.VerticalSpeed = -1.5f;
            motor.Restore(state); input.Swim = true; Tick();
            input.Move = Vector2.right;
            for (int i = 0; i < 90; i++) { Tick(); Assert.That(state.Grounded, Is.False); }
            Assert.That(state.PaperCenter.y, Is.LessThan(.03f));
        }

        [TestCase(15)] [TestCase(30)]
        public void SlopedLandingContactsTheSheetFootprintBeforeAligningToTheSlope(float slope)
        {
            Object.DestroyImmediate(floor.gameObject);
            var ramp = new GameObject("Landing slope"); var box = ramp.AddComponent<BoxCollider>();
            box.size = new Vector3(20, 1, 20); box.center = Vector3.down * .5f;
            ramp.transform.rotation = Quaternion.Euler(0, 0, slope); Physics.SyncTransforms();
            state.Position = Vector3.up * 3; state.Grounded = false; state.Movement = MovementMode.Air; state.VerticalSpeed = -1.5f;
            motor.Restore(state); input.Swim = true;
            for (int i = 0; i < 250 && !state.Grounded; i++)
            {
                var before = state; Tick();
                if (!state.Grounded) continue;
                var normal = ramp.transform.up;
                // On a slope the leading part of the horizontal sheet reaches
                // the surface before its centre. Check its projected footprint.
                float support = Mathf.Abs(Vector3.Dot(before.PaperRotation * Vector3.right, normal)) * .45f
                    + Mathf.Abs(Vector3.Dot(before.PaperRotation * Vector3.up, normal)) * .9f;
                Assert.That(Vector3.Dot(before.PaperCenter, normal) - support, Is.LessThan(.08f));
                Assert.That(Vector3.Dot(state.PaperCenter, normal), Is.EqualTo(.03f).Within(.005f));
                Assert.That(Vector3.Dot(state.PaperRotation * Vector3.forward, normal), Is.GreaterThan(.999f));
            }
            Assert.That(state.Grounded, Is.True);
        }
    }
}
#endif
