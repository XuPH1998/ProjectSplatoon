#if UNITY_EDITOR
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class AirSwimTests
    {
        [SetUp] public void Load() => HeroMigrationTests.Load();
        [TearDown] public void Reset() => LubanConfigService.Current.Reset();

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void FallingBrakesToTerminalAndReleaseImmediatelyRestoresGravity(int hero)
        {
            var c = GameplayConfig.GetHero(hero);
            float v = -22;
            for (int i = 0; i < 90; i++)
            {
                float before = v; v = AirSwimSimulation.VerticalSpeed(v, true, c, 1f / 60);
                Assert.That(v, Is.LessThan(0)); Assert.That(v, Is.GreaterThanOrEqualTo(before));
            }
            Assert.That(v, Is.EqualTo(-1.5f).Within(.0001));
            Assert.That(AirSwimSimulation.VerticalSpeed(v, false, c, .1f), Is.EqualTo(-3.7f).Within(.0001));
        }
        [Test] public void SwitchingAtEveryTickCannotIncreaseJumpHeight()
        {
            var c = GameplayConfig.DefaultHero;
            float ordinary = c.JumpSpeed, toggled = ordinary;
            for (int tick = 0; ordinary > 0; tick++)
            {
                ordinary -= c.CharacterGravity / 60;
                toggled = AirSwimSimulation.VerticalSpeed(toggled, tick % 2 == 0, c, 1f / 60);
                Assert.That(toggled, Is.EqualTo(ordinary).Within(.0001));
            }
            for (int tick = 0; tick < 120; tick++)
            {
                toggled = AirSwimSimulation.VerticalSpeed(toggled, tick % 2 == 0, c, 1f / 60);
                Assert.That(toggled, Is.LessThan(0));
            }
        }
        [TestCase(MovementMode.GroundInk, false, SwimSurface.Friendly, true)]
        [TestCase(MovementMode.Air, false, SwimSurface.Friendly, true)]
        [TestCase(MovementMode.Mantle, false, SwimSurface.Friendly, true)]
        [TestCase(MovementMode.WallInk, false, SwimSurface.Friendly, false)]
        [TestCase(MovementMode.WallInk, false, SwimSurface.Neutral, true)]
        [TestCase(MovementMode.GroundInk, true, SwimSurface.Neutral, true)]
        public void UnsupportedOrNeutralStatesNeverFastRecover(MovementMode mode, bool ground, SwimSurface source, bool contact)
        {
            var s = new PlayerSnapshot { HeroId = 1, Health = 40, Ink = 20, Swimming = true, Grounded = ground,
                Movement = mode, SwimSource = source, FriendlyInkContact = contact, LastDamageAt = -100 };
            Assert.That(s.HasInkRecovery, Is.False);
            ResourceSimulation.Step(ref s, GameplayConfig.DefaultHero, false, false, .1f, 10);
            Assert.That(s.Ink, Is.EqualTo(21).Within(.0001));
        }
        [TestCase("airSwimSpeed", 0)] [TestCase("airSwimGravity", 0)] [TestCase("airSwimGravity", 23)]
        [TestCase("airSwimFallSpeed", 0)] [TestCase("airSwimBraking", 0)]
        public void InvalidGlideConfigurationRejected(string field, float value)
        {
            HeroMigrationTests.Load(rows => rows[0][field] = value);
            Assert.Throws<System.InvalidOperationException>(() => GameplayConfig.Validate());
        }
        [TestCase(1, 2, true)] [TestCase(2, 1, true)] [TestCase(1, 1, false)] [TestCase(2, 2, false)]
        [TestCase(0, 1, false)] [TestCase(1, 0, false)]
        public void EnemyOutlineUsesViewerTeam(byte target, byte viewer, bool expected)
        {
            var s = new PlayerSnapshot { Team = target, Health = 100 };
            Assert.That(EnemyOutlineRules.IsEnemy(s, viewer), Is.EqualTo(expected));
            s.Health = 0; Assert.That(EnemyOutlineRules.IsEnemy(s, viewer), Is.False);
        }
        [Test] public void MotorGlidesWithoutInkAndReplaysAcrossARelease()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Air glide motor") { layer = 8 };
            try
            {
                var controller = go.AddComponent<CharacterController>(); controller.height = 1.8f; controller.center = Vector3.up * .9f;
                controller.radius = .35f;
                var motor = new PlayerMotorSimulation(controller);
                var state = new PlayerSnapshot { HeroId = 1, Health = 100, Ink = 0, Team = 1, Position = Vector3.up * 30, VerticalSpeed = -12, Movement = MovementMode.Air };
                motor.Restore(state);
                var input = new PlayerInputFrame { Swim = true, Move = Vector2.up };
                for (int tick = 0; tick < 60; tick++) motor.Step(ref state, input, 1f / 60, tick / 60.0, false);
                Assert.That(state.Swimming, Is.True); Assert.That(state.HasInkRecovery, Is.False);
                Assert.That(state.VerticalSpeed, Is.EqualTo(-1.5f).Within(.0001));
                Assert.That(state.PlanarVelocity.magnitude, Is.EqualTo(6).Within(.0001));
                PlayerSnapshot saved;
                using (var writer = new Unity.Netcode.FastBufferWriter(2048, Unity.Collections.Allocator.Temp))
                {
                    writer.WriteNetworkSerializable(state);
                    using var reader = new Unity.Netcode.FastBufferReader(writer, Unity.Collections.Allocator.Temp);
                    reader.ReadNetworkSerializable(out saved);
                }
                void Run(ref PlayerSnapshot s)
                {
                    for (int tick = 0; tick < 45; tick++)
                    {
                        input.Swim = tick >= 10; input.Look = new Vector2(90, 30); input.JumpSequence = (uint)tick;
                        motor.Step(ref s, input, 1f / 60, 1 + tick / 60.0, tick < 10);
                        Assert.That(s.HasInkRecovery, Is.False); Assert.That(s.VerticalSpeed, Is.LessThan(0));
                    }
                }
                Run(ref state); motor.Restore(saved); Run(ref saved);
                Assert.That(Vector3.Distance(state.Position, saved.Position), Is.LessThan(.001));
                Assert.That(state.VerticalSpeed, Is.EqualTo(saved.VerticalSpeed));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
#endif
