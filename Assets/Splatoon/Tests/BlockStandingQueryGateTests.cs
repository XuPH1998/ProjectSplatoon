#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Compare query guards against the production state-machine results.
    // Does not prove native query timing or replace a Player integration run.
    [Category("BlockFixedGrid")]
    public sealed class BlockStandingQueryGateTests
    {
        const double Now = 10;
        const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        SpecialWeaponConfigAsset _special;
        SubWeaponConfigAsset _sub;
        [SetUp] public void Setup()
        {
            HeroMigrationTests.Load();
            _special = ScriptableObject.CreateInstance<SpecialWeaponConfigAsset>(); SpecialWeaponDefaults.Apply(_special, SpecialWeaponType.Trizooka);
            _sub = ScriptableObject.CreateInstance<SubWeaponConfigAsset>(); SubWeaponDefaults.Apply(_sub, SubWeaponType.BurstBomb);
        }
        [TearDown] public void Cleanup()
        {
            Object.DestroyImmediate(_special); Object.DestroyImmediate(_sub);
            WeaponConfigService.Current.Clear(); LubanConfigService.Current.Reset();
        }
        static PlayerSnapshot State() => new()
        {
            HeroId = 1, HeroRevision = 2, Revision = 3, Team = 1, Health = 100, Ink = 100,
            LifeState = PlayerLifeState.Alive, Grounded = true, SpecialWeaponId = 1, SubWeaponId = 3,
            SpecialPhase = SpecialPhase.Charging, SpecialPoints = 200, SpecialUntil = 100
        };
        static PlayerInputFrame Input() => new() { Revision = 3, HeroRevision = 2, SpecialSequence = 1, SubPressSequence = 1, SubHeld = true };
        static bool SpecialNeeds(PlayerSnapshot s, PlayerInputFrame i) => (bool)typeof(PrototypePlayer)
            .GetMethod("NeedsSpecialStandingQuery", PrivateStatic).Invoke(null, new object[] { s, i, 200f });
        static bool SubNeeds(PlayerSnapshot s, PlayerInputFrame i) => (bool)typeof(PrototypePlayer)
            .GetMethod("NeedsSubStandingQuery", PrivateStatic).Invoke(null, new object[] { s, i, Now });
        static void Same(PlayerSnapshot expected, PlayerSnapshot actual, string label)
            => Assert.That(JsonUtility.ToJson(actual), Is.EqualTo(JsonUtility.ToJson(expected)), label);

        [TestCase("no-edge", false)] [TestCase("repeat", false)] [TestCase("cancel", false)]
        [TestCase("old-equipment", false)] [TestCase("dead", false)] [TestCase("bubble", false)]
        [TestCase("not-ready", false)] [TestCase("active", false)] [TestCase("recovering", false)]
        [TestCase("new-edge", true)] [TestCase("busy-priority", true)] [TestCase("swim-edge", true)]
        [TestCase("cancel-sub-only", true)]
        public void SpecialGuardPreservesActualStateMachineAndFailurePriority(string scenario, bool expectedQuery)
        {
            var state = State(); var input = Input();
            switch (scenario)
            {
                case "no-edge": input.SpecialSequence = 0; break;
                case "repeat": state.SpecialConsumed = 1; break;
                case "cancel": input.CancelFire = true; break;
                case "old-equipment": input.HeroRevision = 1; break;
                case "dead": state.Health = 0; break;
                case "bubble": state.LifeState = PlayerLifeState.Bubble; break;
                case "not-ready": state.SpecialPoints = 199; break;
                case "active": state.SpecialPhase = SpecialPhase.Active; break;
                case "recovering": state.SpecialPhase = SpecialPhase.Recovering; break;
                case "busy-priority": state.SubRecoveryUntil = 20; break;
                case "swim-edge": input.Swim = true; break;
                case "cancel-sub-only": input.CancelSub = true; break;
            }
            bool needs = SpecialNeeds(state, input); Assert.That(needs, Is.EqualTo(expectedQuery));
            foreach (bool actualStanding in new[] { false, true })
            {
                var original = state; var optimized = state;
                bool a = SpecialWeaponSimulation.Step(ref original, input, _special.Snapshot(), 200, 100, actualStanding, Now);
                bool b = SpecialWeaponSimulation.Step(ref optimized, input, _special.Snapshot(), 200, 100, !needs || actualStanding, Now);
                Assert.That(b, Is.EqualTo(a), scenario); Same(original, optimized, scenario);
                if (!needs)
                {
                    var alternate = state;
                    bool c = SpecialWeaponSimulation.Step(ref alternate, input, _special.Snapshot(), 200, 100, !actualStanding, Now);
                    Assert.That(c, Is.EqualTo(a)); Same(original, alternate, "The skipped value must be unobservable: " + scenario);
                }
                else if (!actualStanding) Assert.That(optimized.SpecialFailure, Is.EqualTo(SpecialFailure.NoSpace));
            }
        }

        [TestCase("no-edge", false)] [TestCase("repeat", false)] [TestCase("cancel-sub", false)]
        [TestCase("cancel-main", false)] [TestCase("old-equipment", false)] [TestCase("swim", false)]
        [TestCase("release-latch", false)] [TestCase("dead", false)] [TestCase("holding", false)]
        [TestCase("deferred-starting", false)] [TestCase("reuse-busy", false)]
        [TestCase("attack-busy", false)] [TestCase("sub-busy", false)]
        [TestCase("new-press", true)] [TestCase("no-ink-priority", true)]
        [TestCase("ready-inside-tolerance", true)] [TestCase("ready-outside-tolerance", false)]
        public void DeferredSubGuardPreservesActualStateMachineAndFailurePriority(string scenario, bool expectedQuery)
        {
            var state = State(); var input = Input();
            switch (scenario)
            {
                case "no-edge": input.SubPressSequence = 0; break;
                case "repeat": state.SubConsumedPress = 1; break;
                case "cancel-sub": input.CancelSub = true; break;
                case "cancel-main": input.CancelFire = true; break;
                case "old-equipment": input.HeroRevision = 1; break;
                case "swim": input.Swim = true; break;
                case "release-latch": state.SubNeedsRelease = true; input.SubHeld = false; break;
                case "dead": state.Health = 0; break;
                case "holding": state.SubPhase = SubWeaponPhase.Holding; break;
                case "deferred-starting": state.SubPhase = SubWeaponPhase.Starting; state.SubReleaseAt = Now; break;
                case "reuse-busy": state.SubReadyAt = Now + 1; break;
                case "attack-busy": state.AttackRecoveryUntil = Now + 1; break;
                case "sub-busy": state.SubRecoveryUntil = Now + 1; break;
                case "no-ink-priority": state.Ink = 0; break;
                case "ready-inside-tolerance": state.SubReadyAt = Now + 5e-9; break;
                case "ready-outside-tolerance": state.SubReadyAt = Now + 2e-8; break;
            }
            bool needs = SubNeeds(state, input); Assert.That(needs, Is.EqualTo(expectedQuery));
            foreach (bool actualStanding in new[] { false, true })
            {
                var original = state; var optimized = state;
                bool a = SubWeaponSimulation.Step(ref original, input, _sub.Snapshot(), 1f / 60, Now, actualStanding, SubWeaponFailure.InvalidGround, out float ca, deferCommit: true);
                bool b = SubWeaponSimulation.Step(ref optimized, input, _sub.Snapshot(), 1f / 60, Now, !needs || actualStanding, SubWeaponFailure.InvalidGround, out float cb, deferCommit: true);
                Assert.That(b, Is.EqualTo(a), scenario); Assert.That(cb, Is.EqualTo(ca)); Same(original, optimized, scenario);
                if (!needs)
                {
                    var alternate = state;
                    bool c = SubWeaponSimulation.Step(ref alternate, input, _sub.Snapshot(), 1f / 60, Now, !actualStanding, SubWeaponFailure.InvalidGround, out float cc, deferCommit: true);
                    Assert.That(c, Is.EqualTo(a)); Assert.That(cc, Is.EqualTo(ca)); Same(original, alternate, "The skipped value must be unobservable: " + scenario);
                }
                else if (!actualStanding) Assert.That(optimized.SubFailure, Is.EqualTo(SubWeaponFailure.NoStandingRoom));
            }
        }
    }
}
#endif
