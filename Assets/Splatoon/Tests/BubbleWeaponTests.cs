#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BubbleWeaponTests
    {
        readonly List<UnityEngine.Object> _objects = new();
        static readonly Vector3 Origin = new(3000, 0, 3000);
        WeaponRuntimeConfig Weapon => GameplayConfig.GetWeapon(7);
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup()
        { foreach (var o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o); _objects.Clear(); LubanConfigService.Current.Reset(); }
        static PlayerSnapshot Alive() => new() { HeroId = 7, Health = 100, Ink = 100, Team = 1, Grounded = true, Revision = 1 };
        bool Step(ref PlayerSnapshot s, int tick, bool held = false, bool swim = false, bool cancel = false)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Sequence = (uint)tick + 1, FireSequence = 1, Fire = held, Swim = swim, CancelFire = cancel }, Weapon, tick / 60.0, false, true);
        [Test] public void QuickTapCommitsFourIndependentShotsAndOneInkPayment()
        {
            var s = Alive(); var ticks = new List<int>(); var ids = new List<ulong>();
            ulong animation = 0;
            for (int i = 0; i < 80; i++) if (Step(ref s, i))
            { ticks.Add(i); ids.Add(s.ShotActionId); if (animation == 0) animation = s.RightShotAction; Assert.That(s.RightShotAction, Is.EqualTo(animation)); }
            Assert.That(ticks, Is.EqualTo(new[] { 6, 9, 12, 15 }));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(4)); Assert.That(ids.Select(x => x >> 32).Distinct().Count(), Is.EqualTo(1));
            Assert.That(s.Ink, Is.EqualTo(92)); Assert.That(s.InkRecoverAt, Is.EqualTo(.9).Within(1e-6));
        }
        [Test] public void HeldFireMaintainsExactCycleUntilTwelveGroupsExhaustInk()
        {
            var s = Alive(); var ticks = new List<int>();
            for (int i = 0; i < 500; i++) if (Step(ref s, i, true)) ticks.Add(i);
            Assert.That(ticks, Is.EqualTo(Enumerable.Range(0, 12).SelectMany(g => new[] { 6, 9, 12, 15 }.Select(x => x + g * 33))));
            Assert.That(s.Ink, Is.EqualTo(4)); Assert.That(s.BurstRemaining, Is.Zero);
        }
        [Test] public void SwimmingWaitsForFourthBubbleButNotRecovery()
        {
            var s = Alive(); for (int i = 0; i <= 6; i++) Step(ref s, i);
            var input = new PlayerInputFrame { Swim = true, FireSequence = 1 };
            Assert.That(WeaponSimulation.WantsFire(s, input, Weapon, .12), Is.True);
            for (int i = 7; i <= 15; i++) Step(ref s, i, swim: true);
            Assert.That(s.ShotSequence, Is.EqualTo(4)); Assert.That(WeaponSimulation.WantsFire(s, input, Weapon, .26), Is.False);
            Assert.That(s.FireVisualUntil, Is.EqualTo(.65).Within(1e-6));
        }
        [TestCase(0)] [TestCase(7)]
        public void CancellationNeverRefundsAnEmittedGroupOrResurrectsIt(int cancelTick)
        {
            var s = Alive(); for (int i = 0; i < cancelTick; i++) Step(ref s, i);
            Step(ref s, cancelTick, cancel: true); for (int i = cancelTick + 1; i < 90; i++) Step(ref s, i);
            Assert.That(s.ShotSequence, Is.EqualTo(cancelTick == 0 ? 0 : 1)); Assert.That(s.Ink, Is.EqualTo(cancelTick == 0 ? 100 : 92));
        }
        [Test] public void InsufficientInkNeverEmitsPartialGroup()
        { var s = Alive(); s.Ink = 7.99f; for (int i = 0; i < 90; i++) Assert.That(Step(ref s, i, true), Is.False); Assert.That(s.Ink, Is.EqualTo(7.99f)); }
        [Test] public void FiringWhileSubmergedUsesEmergeStartupAndCompletesTheGroup()
        {
            var s = Alive(); s.Swimming = true;
            var input = new PlayerInputFrame { Sequence = 1, FireSequence = 1, Fire = true, Swim = true };
            Assert.That(WeaponSimulation.WantsFire(s, input, Weapon, 0), Is.True);
            s.Swimming = false; var ticks = new List<int>();
            for (int i = 0; i < 40; i++)
                if (WeaponSimulation.Step(ref s, input, Weapon, i / 60.0, i == 0, true)) ticks.Add(i);
            Assert.That(ticks, Is.EqualTo(new[] { 12, 15, 18, 21 })); Assert.That(s.Ink, Is.EqualTo(92));
        }
        [Test] public void SnapshotReplayRestoresRemainingBubblesWithoutDoublePayment()
        {
            var s = Alive(); for (int i = 0; i <= 9; i++) Step(ref s, i, true);
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(s);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot copy);
            for (int i = 10; i < 100; i++) Assert.That(Step(ref copy, i, true), Is.EqualTo(Step(ref s, i, true)));
            Assert.That(copy.Ink, Is.EqualTo(s.Ink)); Assert.That(copy.ShotActionId, Is.EqualTo(s.ShotActionId));
        }
        [Test] public void ThirtyDamageHasNoAgeDecayAndFourHitsDefeatHundredHealth()
        {
            float health = 100; for (int i = 0; i < 4; i++)
            { float damage = WeaponSimulation.Damage(Weapon, i * .7); Assert.That(damage, Is.EqualTo(30)); health = PrototypeRules.Damage(health, damage, false, 0, 10); if (i == 2) Assert.That(health, Is.EqualTo(10)); }
            Assert.That(health, Is.Zero);
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void FlatFloorProducesThreeBouncesThenOneTerminalContact(int rate)
        {
            Floor(); var service = Shot();
            for (int i = 1; i <= rate * 3; i++) service.Simulate((double)i / rate);
            Assert.That(service.Bounces.Count, Is.EqualTo(3)); Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.ActiveCount, Is.Zero);
            Assert.That(service.Bounces.Select(b => b.GroundBounces), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(service.Bounces[0].Position.z - Origin.z, Is.EqualTo(4.76).Within(.15));
            Assert.That(service.Impacts[0].Position.z - Origin.z, Is.InRange(17.1f, 17.9f));
        }
        [Test] public void WallReflectsWithoutSpendingGroundBounce()
        {
            Box("wall", Origin + new Vector3(0, 2, 2), new Vector3(5, 4, .2f));
            var service = Shot(); service.Simulate(.2);
            Assert.That(service.Bounces.Count, Is.EqualTo(1)); Assert.That(service.Bounces[0].GroundBounces, Is.Zero);
            Assert.That(service.Bounces[0].Velocity.z, Is.LessThan(0)); Assert.That(service.ActiveCount, Is.EqualTo(1));
        }
        [TestCase(30, 1)] [TestCase(60, 0)]
        public void SlopedColliderUsesItsActualSurfaceNormal(int degrees, int groundBounces)
        {
            var slope = new GameObject("bubble slope"); _objects.Add(slope);
            slope.transform.SetPositionAndRotation(Origin, Quaternion.Euler(degrees, 0, 0));
            slope.AddComponent<BoxCollider>().size = new Vector3(20, .1f, 20); Physics.SyncTransforms();
            var service = Shot(origin: Origin + Vector3.up * 3, velocity: Vector3.down * 14); service.Simulate(.22);
            Assert.That(service.Bounces.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(service.Bounces[0].GroundBounces, Is.EqualTo(groundBounces));
            Assert.That(Vector3.Dot(service.Bounces[0].Velocity, service.Bounces[0].Normal), Is.GreaterThan(0));
        }
        [Test] public void CeilingReversesVerticalTravelWithoutUsingGroundBounce()
        {
            Box("ceiling", Origin + Vector3.up * 3, new Vector3(10, .1f, 10));
            var service = Shot(velocity: Vector3.up * 14); service.Simulate(.2);
            Assert.That(service.Bounces.Count, Is.EqualTo(1)); Assert.That(service.Bounces[0].GroundBounces, Is.Zero);
            Assert.That(service.Bounces[0].Velocity.y, Is.LessThan(0));
        }
        [TestCase(0)] [TestCase(30)] [TestCase(45)] [TestCase(60)] [TestCase(90)] [TestCase(180)]
        public void GroundClassificationAndReflectionUseContactNormal(int angle)
        {
            var n = Quaternion.Euler(angle, 0, 0) * Vector3.up;
            var v = -n * 10 + Vector3.right * 4;
            var result = InkProjectileService.ReflectBubble(v, n, Weapon, out bool ground);
            Assert.That(ground, Is.EqualTo(angle <= 45)); Assert.That(Vector3.Dot(result, n), Is.GreaterThan(0));
            Assert.That(result.magnitude, Is.LessThan(v.magnitude));
        }
        [Test] public void RangeAndLifetimeRemainCumulativeAfterBounces()
        {
            Floor(); var copy = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/BubbleGirl/BubbleGirlWeaponConfig.asset")); _objects.Add(copy);
            copy.effectiveRange = 8; var service = Shot(copy.Snapshot()); service.Simulate(2.5);
            Assert.That(service.Bounces.Count, Is.EqualTo(1)); Assert.That(service.ActiveCount, Is.Zero);
            Assert.That(service.Impacts.Single().Position.z - Origin.z, Is.LessThan(8));
        }
        [Test] public void LifetimeEndsHighAltitudeFlightExactlyOnce()
        {
            var copy = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/BubbleGirl/BubbleGirlWeaponConfig.asset")); _objects.Add(copy);
            copy.effectiveRange = 100; // Isolate lifetime from the independent 24 m path limit.
            var service = Shot(copy.Snapshot(), Origin + Vector3.up * 100); service.Simulate(2.3); Assert.That(service.ActiveCount, Is.EqualTo(1));
            service.Simulate(2.5); service.Simulate(10); Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.ActiveCount, Is.Zero);
        }
        [Test] public void TightCornerCannotBounceOrPaintForever()
        {
            Box("left", Origin + new Vector3(-.3f, 1, 0), new Vector3(.2f, 4, 4));
            Box("right", Origin + new Vector3(.3f, 1, 0), new Vector3(.2f, 4, 4));
            Box("front", Origin + new Vector3(0, 1, .3f), new Vector3(4, 4, .2f));
            Floor(); var service = Shot(); service.Simulate(3);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.Bounces.Count, Is.LessThanOrEqualTo(6));
        }
        [Test] public void EachEmissionUsesCurrentAimAndLogicalMuzzleInsteadOfFrozenVolleyDirection()
        {
            var root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            _objects.Add(root); root.SetActive(false); var player = root.GetComponent<PrototypePlayer>();
            player.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/BubbleGirl/Prefabs/BubbleGirlVisual.prefab").GetComponent<InkCharacterView>();
            var service = new InkProjectileService(); var state = Alive(); state.FireBurstSequence = 10;
            for (int i = 0; i < 4; i++)
            { state.Position = Origin + new Vector3(i, 1, 0); state.Yaw = i * 15; state.BurstShotIndex = (uint)i + 1; service.Spawn(player, state, i * .05, 1); }
            Assert.That(service.Spawned.Count, Is.EqualTo(4));
            Assert.That(service.Spawned.Select(s => s.Origin).Distinct().Count(), Is.EqualTo(4));
            Assert.That(Vector3.Angle(service.Spawned[0].Velocity, service.Spawned[3].Velocity), Is.GreaterThan(40));
        }
        [Test] public void LateJoinCapturesLatestAuthoritativeSegmentAndRoundClearRemovesIt()
        {
            Floor(); var service = Shot(); service.Simulate(.9); var states = new List<InkBubbleState>(); service.CaptureBubbles(states);
            Assert.That(states.Count, Is.EqualTo(1)); Assert.That(states[0].Segment.Sequence, Is.EqualTo(2));
            using var writer = new FastBufferWriter(2048, Allocator.Temp); writer.WriteNetworkSerializable(states[0]);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out InkBubbleState decoded);
            Assert.That(decoded.Segment.Velocity, Is.EqualTo(states[0].Segment.Velocity)); Assert.That(decoded.Shot.Id, Is.EqualTo(1));
            service.Clear(); service.CaptureBubbles(states); Assert.That(states, Is.Empty); Assert.That(service.Bounces, Is.Empty);
        }
        [Test] public void MeshPresentationAcceptsDuplicateAndOutOfOrderSegmentsWithoutRewinding()
        {
            Floor(); var service = Shot(); service.Simulate(.9);
            var parent = new GameObject("bubble visuals"); _objects.Add(parent);
            using var visual = new BubbleFlightPresentation(parent.transform);
            visual.Bounce(service.Bounces[1], false); visual.Spawn(service.Spawned[0], .9);
            visual.Bounce(service.Bounces[0], false); visual.Bounce(service.Bounces[1], false);
            Assert.That(visual.ActiveCount, Is.EqualTo(1));
            Assert.That(visual.TryPosition(1, 1, .9, out var position), Is.True);
            Assert.That(Vector3.Distance(position, service.Bounces[1].PositionAt(.9, 18)), Is.LessThan(.001));
            visual.Complete(new InkImpact { Round = 1, Id = 1 }); Assert.That(visual.ActiveCount, Is.Zero);
        }
        [Test] public void NewHeroUsesOwnProfileAndSharedShotgunMotionAssets()
        {
            var newView = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/BubbleGirl/Prefabs/BubbleGirlVisual.prefab").GetComponent<InkCharacterView>();
            var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/ShotgunGirl/Prefabs/ShotgunGirlVisual.prefab").GetComponent<InkCharacterView>();
            Assert.That(newView.Profile, Is.Not.SameAs(source.Profile)); Assert.That(newView.Animator.runtimeAnimatorController, Is.SameAs(source.Animator.runtimeAnimatorController));
            Assert.That(newView.Profile.Paper, Is.Not.Null); Assert.That(newView.Profile.SingleShot, Is.True);
            WeaponConfigValidation.Validate(Weapon); Assert.That(Weapon.Ammo.AmmoId, Is.EqualTo(7)); Assert.That(Weapon.Ammo.BubblePrefab, Is.Not.Null);
            Assert.That(GameplayConfig.GetHero(7).DisplayName, Is.EqualTo("沫澜"));
        }
        [Test] public void TerminalEventUsesRenderClockAndCannotResurrectFromLatePackets()
        {
            var go = new GameObject("bubble terminal ordering"); _objects.Add(go);
            var presentation = go.AddComponent<InkPresentation>();
            var shot = Shot().Spawned[0]; shot.Born = 5;
            presentation.Spawn(shot);
            var impact = new InkImpact { Id = shot.Id, Round = shot.Round, Time = 5.5, Position = shot.Origin + Vector3.forward * 7 };
            presentation.Impact(impact); presentation.Impact(impact);
            presentation.UpdateFlights(5.55, null); Assert.That(presentation.ActiveShots, Is.EqualTo(1));
            presentation.UpdateFlights(5.61, null); Assert.That(presentation.ActiveShots, Is.Zero);
            presentation.Spawn(shot); presentation.Bounce(new InkBounce { Id = shot.Id, Round = shot.Round, Sequence = 1, Time = 5.2 });
            presentation.UpdateFlights(5.7, null); Assert.That(presentation.ActiveShots, Is.Zero);
        }
        [Test] public void SelectionStatsUseGroupUnitsAndRangeIncludesActualBounces()
        {
            Floor();
            var profile = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/BubbleGirl/Prefabs/BubbleGirlVisual.prefab").GetComponent<InkCharacterView>().Profile;
            var g = GameplayConfig.Global;
            var stats = HeroSelectionStats.Create(Weapon, profile, g.AimCorrectionDistance, g.AimFarCorrectionDistance);
            Assert.That(stats[0].Note, Does.Contain("4 颗")); Assert.That(stats[1].Value, Does.Contain("组/秒"));
            Assert.That(stats[3].Note, Does.Contain("弹跳"));
            var shot = HeroFlatRange.ReferenceShot(Weapon, profile, 0, g.AimCorrectionDistance, g.AimFarCorrectionDistance);
            var expected = HeroFlatRange.Calculate(shot, Weapon);
            shot.Origin += Origin; shot.HeroId = 7; shot.Id = 19; shot.Shooter = 9000;
            var service = new InkProjectileService(); service.SpawnForMeasurement(shot); service.Simulate(3);
            var delta = service.Impacts.Single().Position - shot.Origin;
            Assert.That(new Vector2(delta.x, delta.z).magnitude, Is.EqualTo(expected.Distance).Within(.05));
            System.IO.Directory.CreateDirectory("Reports/BubbleGirl");
            System.IO.File.WriteAllLines("Reports/BubbleGirl/selection-stats.txt", stats.Select(s => s.Label + ": " + s.Value + " (" + s.Note + ")"));
        }
        [Test] public void EmbeddedStartTerminatesOnceWithoutSpendingFakeTravelTime()
        {
            Box("embedded wall", Origin + Vector3.up * 1.2f, Vector3.one);
            var service = Shot(); service.Simulate(.2);
            Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.ActiveCount, Is.Zero);
            Assert.That(service.Bounces.Count, Is.LessThanOrEqualTo(6));
        }
        [Test] public void EightSustainedShootersStayWithinPoolAndRecordSimulationAndWireCosts()
        {
            Floor(); var service = new InkProjectileService(); uint id = 0; int peak = 0, shots = 0, bounces = 0, impacts = 0;
            long wireBytes = 0; var timings = new List<double>(); var watch = new System.Diagnostics.Stopwatch();
            for (int tick = 0; tick < 600; tick++)
            {
                double time = tick / 60.0; int offset = tick % 33;
                if (offset <= 9 && offset % 3 == 0) for (int player = 0; player < 8; player++)
                {
                    service.SpawnForMeasurement(new InkShot { Id = ++id, Round = 1, HeroId = 7, Team = (byte)(player % 2 + 1), Shooter = (ulong)(9000 + player),
                        Seed = id, Born = time, Origin = Origin + new Vector3(player * .5f - 2, 1.219139f, 0), Velocity = Vector3.forward * 14, Configuration = Weapon });
                }
                watch.Restart(); service.Simulate(time); watch.Stop();
                if (tick > 120) timings.Add(watch.Elapsed.TotalMilliseconds);
                peak = Math.Max(peak, service.ActiveCount); shots += service.Spawned.Count; bounces += service.Bounces.Count; impacts += service.Impacts.Count;
                using (var writer = new FastBufferWriter(32768, Allocator.Temp))
                {
                    foreach (var s in service.Spawned) writer.WriteNetworkSerializable(s);
                    foreach (var b in service.Bounces) writer.WriteNetworkSerializable(b);
                    foreach (var impact in service.Impacts) writer.WriteNetworkSerializable(impact);
                    wireBytes += writer.Length;
                }
                service.Spawned.Clear(); service.Bounces.Clear(); service.Impacts.Clear();
            }
            Assert.That(peak, Is.LessThan(BubbleFlightPresentation.Capacity)); Assert.That(shots, Is.GreaterThan(500)); Assert.That(bounces, Is.GreaterThan(1000));
            timings.Sort(); System.IO.Directory.CreateDirectory("Reports/BubbleGirl");
            System.IO.File.WriteAllText("Reports/BubbleGirl/simulation-budget.txt", FormattableString.Invariant(
                $"durationSeconds=10\nshooters=8\nshots={shots}\nbounces={bounces}\nimpacts={impacts}\npeakActive={peak}\nsimulationMeanMs={timings.Average():F4}\nsimulationP95Ms={timings[(int)(timings.Count * .95)]:F4}\nprojectilePayloadBytes={wireBytes}\nPayload excludes RPC/transport, player snapshots and paint. This is an Editor simulation microbenchmark, not target-device frame time.\n"));
        }
        void Floor() => Box("floor", Origin + Vector3.down * .1f, new Vector3(100, .2f, 100));
        void Box(string name, Vector3 position, Vector3 size)
        { var go = new GameObject(name); _objects.Add(go); go.transform.position = position; go.AddComponent<BoxCollider>().size = size; Physics.SyncTransforms(); }
        InkProjectileService Shot(WeaponRuntimeConfig config = null, Vector3? origin = null, Vector3? velocity = null)
        {
            var service = new InkProjectileService();
            service.SpawnForMeasurement(new InkShot { Id = 1, Round = 1, HeroId = 7, Team = 1, Shooter = 9000, Seed = 42,
                Origin = origin ?? Origin + Vector3.up * 1.219139f, Velocity = velocity ?? Vector3.forward * 14, Configuration = config ?? Weapon });
            return service;
        }
    }
}
#endif
