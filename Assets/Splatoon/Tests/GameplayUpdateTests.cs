#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class GameplayUpdateTests
    {
        [SetUp] public void Load() => HeroMigrationTests.Load();
        [TearDown] public void Reset() => LubanConfigService.Current.Reset();
        static PlayerSnapshot Player(int hero = 2) => new() { HeroId = hero, Health = 100, Ink = 100, Team = 1, Revision = 1, Grounded = true };
        static bool Fire(ref PlayerSnapshot s, int tick, bool held, uint press = 1, bool cancel = false)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Sequence = (uint)tick + 1, Fire = held, FireSequence = press, CancelFire = cancel }, GameplayConfig.GetHero(s.HeroId), tick / 60.0, false, true);

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ReleaseBeforeCooldownNeverQueuesAnAutomaticShot(int hero)
        {
            var s = Player(hero); var w = GameplayConfig.GetHero(hero);
            for (int t = 0; t < 100; t++) Fire(ref s, t, t < w.StartFrames + w.FireIntervalFrames);
            Assert.That(s.ShotSequence, Is.EqualTo(1));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void DryHeldTriggerRecoversAndResumesWithoutInventingClicks(int hero)
        {
            var s = Player(hero); var w = GameplayConfig.GetHero(hero); s.Ink = w.ShotInk;
            for (int t = 0; t < 240; t++)
            {
                Fire(ref s, t, true);
                ResourceSimulation.Step(ref s, w, false, false, 1f / 60, t / 60.0);
            }
            Assert.That(s.ShotSequence, Is.GreaterThan(1)); Assert.That(s.Ink, Is.GreaterThanOrEqualTo(0));
            if (hero == 2) Assert.That(s.NextMuzzle, Is.EqualTo(s.ShotSequence % 2));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ForcedCancelRequiresReleaseAndRestartUsesStartup(int hero)
        {
            var s = Player(hero); for (int t = 0; t < 8; t++) Fire(ref s, t, true);
            Fire(ref s, 8, true, 1, true); uint count = s.ShotSequence;
            for (int t = 9; t < 60; t++) Assert.That(Fire(ref s, t, true), Is.False);
            Fire(ref s, 60, false);
            for (int t = 61; t < 61 + GameplayConfig.GetHero(hero).StartFrames; t++) Assert.That(Fire(ref s, t, true, 2), Is.False);
            Assert.That(Fire(ref s, 61 + GameplayConfig.GetHero(hero).StartFrames, true, 2), Is.True);
            Assert.That(s.ShotSequence, Is.EqualTo(count + 1));
        }
        [Test] public void HeldStateRoundTripReplaysIdenticalShotsHandsAndInk()
        {
            var a = Player(); for (int t = 0; t < 16; t++) Fire(ref a, t, true);
            Assert.That(a.SemiHoldStarted, Is.True);
            using var writer = new FastBufferWriter(2048, Allocator.Temp); writer.WriteNetworkSerializable(a);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot b);
            for (int t = 16; t < 90; t++) Assert.That(Fire(ref a, t, true), Is.EqualTo(Fire(ref b, t, true)));
            Assert.That(a.ShotActionId, Is.EqualTo(b.ShotActionId)); Assert.That(a.Ink, Is.EqualTo(b.Ink));
            Assert.That(a.NextMuzzle, Is.EqualTo(b.NextMuzzle)); Assert.That(a.ShotSequence, Is.EqualTo(b.ShotSequence));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ReusedHeldInputKeepsEveryShotActionDistinct(int hero)
        {
            var s = Player(hero); var w = GameplayConfig.GetHero(hero);
            var input = new PlayerInputFrame { Sequence = 7, FireSequence = 1, Fire = true };
            var actions = new List<ulong>();
            for (int tick = 0; tick < 120; tick++)
                if (WeaponSimulation.Step(ref s, input, w, tick / 60.0, false, true)) actions.Add(s.ShotActionId);
            Assert.That(actions.Count, Is.EqualTo(1 + (119 - w.StartFrames) / w.FireIntervalFrames));
            Assert.That(actions.Distinct().Count(), Is.EqualTo(actions.Count), "Repeated input packets must not deduplicate new shots");
        }
        [TestCase(0f, .5f)] [TestCase(-1f, .5f)] [TestCase(.6f, .5f)]
        [TestCase(float.NaN, .5f)] [TestCase(.5f, float.NaN)] [TestCase(.5f, float.PositiveInfinity)]
        public void InvalidTrailRangeIsRejected(float min, float max)
        {
            HeroMigrationTests.Load(rows => { rows[0]["trailRadiusMin"] = min; rows[0]["trailRadiusMax"] = max; });
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate());
        }
        static List<PaintStamp> CaptureTrail(int rate, bool fixedRadius = false)
        {
            HeroMigrationTests.Load(rows => {
                rows[0]["paintRadiusMin"] = rows[0]["paintRadiusMax"] = 3;
                if (fixedRadius) rows[0]["trailRadiusMin"] = rows[0]["trailRadiusMax"] = .5f;
            });
            var floor = new GameObject("Trail range test floor"); floor.SetActive(false);
            floor.transform.position = new Vector3(5000, 999.95f, 5000);
            floor.AddComponent<BoxCollider>().size = new Vector3(100, .1f, 100);
            var surface = floor.AddComponent<PaintSurface>(); surface.enabled = false; surface.SurfaceId = 901;
            floor.SetActive(true);
            var root = new GameObject("Trail test shooter"); root.SetActive(false); root.AddComponent<NetworkObject>();
            var player = root.AddComponent<PrototypePlayer>();
            var nozzle = new GameObject("Nozzle"); nozzle.transform.SetParent(root.transform); nozzle.transform.localPosition = Vector3.up * 1.5f;
            player.SimulationMuzzle = nozzle.transform;
            var stamps = new List<PaintStamp>();
            try
            {
                Physics.SyncTransforms(); var service = new InkProjectileService();
                service.PaintObserved = stamp => { if (stamp.Radius < 2) stamps.Add(stamp); };
                var state = Player(1); state.Position = new Vector3(5000, 1000.05f, 5000); state.CurrentSpread = .01f;
                service.Spawn(player, state, 0, 1);
                Assert.That(stamps.Count, Is.EqualTo(1), "The initial muzzle trail also uses the range");
                for (int frame = 1; frame <= rate * 4; frame++) service.Simulate(frame / (double)rate);
                return stamps;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(floor); Physics.SyncTransforms(); }
        }
        [Test] public void EveryTrailStampUsesAnAdvancingReproducibleRadius()
        {
            var a = CaptureTrail(60); var b = CaptureTrail(60);
            Assert.That(a.Count, Is.GreaterThan(3)); Assert.That(a.Select(s => s.Radius).Distinct().Count(), Is.GreaterThan(3));
            Assert.That(a.All(s => s.Radius >= .464f && s.Radius <= .696f), Is.True);
            Assert.That(a.Select(s => s.Radius), Is.EqualTo(b.Select(s => s.Radius)));
            Assert.That(a.Select(s => s.ShapeSeed), Is.EqualTo(b.Select(s => s.ShapeSeed)));
            Assert.That(a.Take(32).Select(s => InkShapeAtlas.Index(s.ShapeSeed)).Distinct().Count(), Is.EqualTo(Math.Min(a.Count, 32)));
        }
        [Test] public void FixedRadiusAndDifferentSimulationCallRatesAgree()
        {
            Assert.That(CaptureTrail(60, true).All(s => s.Radius == .5f), Is.True);
            var a = CaptureTrail(30); var b = CaptureTrail(144);
            Assert.That(a.Select(s => s.Radius), Is.EqualTo(b.Select(s => s.Radius)));
            Assert.That(a.Select(s => s.ShapeSeed), Is.EqualTo(b.Select(s => s.ShapeSeed)));
            Assert.That(a.Take(32).Select(s => InkShapeAtlas.Index(s.ShapeSeed)).Distinct().Count(), Is.EqualTo(Math.Min(a.Count, 32)));
            Assert.That(a.Select(s => s.Position), Is.EqualTo(b.Select(s => s.Position)));
        }
        [Test] public void TeamSwitchSupportsEmptyOppositionAndUsesOnlyFreeSlot()
        {
            var s = Player(); var other = Player(); other.Team = 2;
            Assert.That(TeamSelectionRules.Validate(s, 2, 0, 0, 1, MatchPhase.Practice, new[] { s, other }, out byte slot), Is.Null);
            Assert.That(slot, Is.EqualTo(1));
            Assert.That(TeamSelectionRules.Validate(s, 2, 0, 0, 1, MatchPhase.Practice, new[] { s }, out slot), Is.Null);
            Assert.That(slot, Is.Zero); Assert.That(PrototypeRules.CanStart(2, MatchPhase.Practice), Is.True);
        }
        [Test] public void SequentialSwitchesCannotOverfillOrReuseAClaimedSpawn()
        {
            var first = Player(); var second = Player(); second.Slot = 1; var blue = Player(); blue.Team = 2;
            var roster = new[] { first, second, blue };
            Assert.That(TeamSelectionRules.Validate(first, 2, 0, 0, 1, MatchPhase.Practice, roster, out byte slot), Is.Null);
            roster[0].Team = 2; roster[0].Slot = slot;
            Assert.That(TeamSelectionRules.Validate(second, 2, 0, 0, 1, MatchPhase.Practice, roster, out _), Does.Contain("已满"));
        }
        [Test] public void TeamSwitchRejectsDeadStaleInvalidAndNonPracticeRequests()
        {
            var s = Player(); var roster = new[] { s };
            foreach (var phase in new[] { MatchPhase.Playing, MatchPhase.Finished })
                Assert.That(TeamSelectionRules.Validate(s, 2, 0, 0, 1, phase, roster, out _), Is.Not.Null);
            Assert.That(TeamSelectionRules.Validate(s, 2, 0, 1, 1, MatchPhase.Practice, roster, out _), Is.Not.Null);
            Assert.That(TeamSelectionRules.Validate(s, 2, 0, 0, 2, MatchPhase.Practice, roster, out _), Is.Not.Null);
            Assert.That(TeamSelectionRules.Validate(s, 1, 0, 0, 1, MatchPhase.Practice, roster, out _), Is.Not.Null);
            s.Health = 0; Assert.That(TeamSelectionRules.Validate(s, 2, 0, 0, 1, MatchPhase.Practice, roster, out _), Does.Contain("重生"));
        }
    }
}
#endif
