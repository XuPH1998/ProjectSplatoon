#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class CharacterPresentationTests
    {
        CharacterPresentationProfile _profile;
        [SetUp] public void SetUp() => _profile = ScriptableObject.CreateInstance<CharacterPresentationProfile>();
        [TearDown] public void TearDown() => Object.DestroyImmediate(_profile);
        static PlayerSnapshot Alive(float aim = 0) => new() { Health = 100, Ink = 100, Yaw = aim, Grounded = true };

        [TestCase(45, 0)] [TestCase(46, 1)] [TestCase(314, -1)] [TestCase(359, 0)]
        public void StationaryTurnUsesShortestAngleAndThreshold(float yaw, int direction)
        { var s = Alive(yaw); CharacterFacing.Step(ref s, _profile, 1f / 30, 10); Assert.That(s.TurnDirection, Is.EqualTo(direction)); Assert.That(s.BodyYaw, Is.EqualTo(0)); }

        [Test] public void TurnSamplesAuthoredCurveAndContinuesAcrossNinetyDegrees()
        {
            _profile.TurnRightProgress = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.5f, .2f), new Keyframe(1, 1));
            var s = Alive(175); CharacterFacing.Step(ref s, _profile, .01f, 10);
            CharacterFacing.Step(ref s, _profile, .01f, 10.5);
            Assert.That(s.BodyYaw, Is.EqualTo(18).Within(.01f));
            CharacterFacing.Step(ref s, _profile, .01f, 11);
            Assert.That(s.BodyYaw, Is.EqualTo(90).Within(.01f)); Assert.That(s.TurnStartedAt, Is.EqualTo(11));
            CharacterFacing.Step(ref s, _profile, .01f, 12);
            Assert.That(s.BodyYaw, Is.EqualTo(180).Within(.01f)); Assert.That(s.TurnDirection, Is.Zero);
        }

        [TestCase("move")] [TestCase("air")] [TestCase("swim")]
        public void LocomotionInterruptsTurnAndLimitsBodyRotation(string reason)
        {
            var s = Alive(90); CharacterFacing.Step(ref s, _profile, .01f, 10);
            if (reason == "move") s.Velocity = Vector3.forward * 5;
            if (reason == "air") s.Grounded = false;
            if (reason == "swim") s.Swimming = true;
            CharacterFacing.Step(ref s, _profile, .1f, 10.1);
            Assert.That(s.TurnDirection, Is.Zero); Assert.That(s.BodyYaw, Is.EqualTo(54).Within(.01f));
        }

        [Test] public void DeathCancelsTurningWithoutRotatingBody()
        { var s = Alive(90); s.Health = 0; s.BodyYaw = 20; s.TurnDirection = 1; CharacterFacing.Step(ref s, _profile, 1, 10); Assert.That(s.BodyYaw, Is.EqualTo(20)); Assert.That(s.TurnDirection, Is.Zero); }

        [TestCase(0, 0, 1, 1)] [TestCase(0, 0, -1, 0)] [TestCase(0, 1, 0, 0)]
        [TestCase(90, 1, 0, 1)] [TestCase(90, -1, 0, 0)] [TestCase(0, 0, 0, 0)]
        public void DeathDirectionUsesIncomingTravelAndVictimBody(float yaw, float x, float z, int expected)
        { Assert.That(CharacterFacing.DeathDirection(yaw, new Vector3(x, 0, z)), Is.EqualTo(expected)); }

        [Test] public void SnapshotRoundTripRetainsAnimationTimelineForLateJoin()
        {
            var expected = new PlayerSnapshot { Position = new Vector3(1, 2, 3), Velocity = Vector3.right * 5, Yaw = 350, BodyYaw = 300, Pitch = -20,
                Health = 0, Ink = 73, Team = 2, Slot = 1, Revision = 19, TurnDirection = -1, TurnStartYaw = 340,
                TurnStartedAt = 1.2345, FireStartedAt = 9.876, DeathDirection = 1, DiedAt = 1234.567, RespawnsAt = 1237.567, ProtectedUntil = 0, Grounded = true };
            using var writer = new FastBufferWriter(512, Allocator.Temp);
            writer.WriteNetworkSerializable(expected);
            using var reader = new FastBufferReader(writer, Allocator.Temp);
            reader.ReadNetworkSerializable(out PlayerSnapshot actual);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test] public void FourDirectionGraphAndUpperBodyMaskMatchApprovedBindings()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/GameResource/Characters/RifleGirl/Animations/RifleGirlCombat.controller");
            Assert.That(controller, Is.Not.Null);
            var tree = (BlendTree)controller.layers[0].stateMachine.states.Single(s => s.state.name == "Locomotion").state.motion;
            var left = tree.children.Single(c => c.position == Vector2.left);
            var right = tree.children.Single(c => c.position == Vector2.right);
            Assert.That(AssetDatabase.GetAssetPath(left.motion), Does.EndWith("R_AimWalk_FL.fbx"));
            Assert.That(AssetDatabase.GetAssetPath(right.motion), Does.EndWith("R_AimWalk_BR.fbx"));
            Assert.That(tree.children.Length, Is.EqualTo(5));
            Assert.That(controller.animationClips.Distinct().Count(), Is.EqualTo(10));
            var mask = controller.layers[1].avatarMask;
            foreach (var part in new[] { AvatarMaskBodyPart.Root, AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg, AvatarMaskBodyPart.LeftFootIK, AvatarMaskBodyPart.RightFootIK })
                Assert.That(mask.GetHumanoidBodyPartActive(part), Is.False, part.ToString());
            Assert.That(mask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm), Is.True);
            Assert.That(controller.animationClips.Single(c => AssetDatabase.GetAssetPath(c).EndsWith("R_AimIdle_AutoShoot.fbx")).isLooping, Is.True);
        }
    }
}
#endif
