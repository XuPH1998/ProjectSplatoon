#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Splatoon.Tests
{
    // Overlay only the approved tuning on in-memory historical expectations.
    // The original migration records on disk remain immutable.
    public static class PistolGirlTuningFixture
    {
        public const string AssetPath = "Assets/GameResource/Weapons/PistolGirl/PistolGirlWeaponConfig.asset";
        public const string RecordPath = "Tools/ValidationData/WeaponAssets/PistolGirl-SplashTuning.json";
        public static void Apply(int hero, JSONNode expected, bool referenceFrames = false)
        {
            if (hero != 4) return;
            var record = JSONNode.Parse(File.ReadAllText(RecordPath));
            var values = record["values"];
            foreach (string key in record["preservedValues"].Keys) values[key] = record["preservedValues"][key];
            if (referenceFrames) WeaponTimeFixture.ToReferenceFrames(values);
            foreach (string key in values.Keys) expected[key] = values[key];
        }
    }

    public sealed class PistolGirlTuningTests
    {
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static PlayerSnapshot Alive() => new() { HeroId = 4, Health = 100, Ink = 100, Grounded = true, Team = 1 };
        static PlayerInputFrame Input(int tick, bool held = true) => new() { Sequence = (uint)tick + 1, FireSequence = 1, Fire = held };

        [Test] public void HeldFireEmitsEveryFiveTicksAndKeepsGroundAirAndLandingAccurate()
        {
            var w = GameplayConfig.GetWeapon(4); var s = Alive(); var ticks = new List<int>();
            for (int tick = 0; tick < 180; tick++)
            {
                s.Grounded = tick < 60 || tick >= 120;
                if (WeaponSimulation.Step(ref s, Input(tick), w, tick / 60.0, false, true))
                {
                    ticks.Add(tick);
                    Assert.That(s.LastShotSpread, Is.Zero);
                    Assert.That(s.LastShotVerticalSpread, Is.Zero);
                    uint seed = (uint)tick + 123;
                    Assert.That(Vector3.Angle(Vector3.forward, InkBallistics.LaunchVelocity(Vector3.forward, w, ref seed, s.LastShotSpread)), Is.LessThan(.001f));
                }
                Assert.That(s.CurrentSpread, Is.Zero); Assert.That(s.CurrentVerticalSpread, Is.Zero);
            }
            Assert.That(ticks, Is.EqualTo(Enumerable.Range(0, 36).Select(i => 2 + i * 5)));
            Assert.That(s.Ink, Is.EqualTo(100 - 36 * .8f).Within(.001f));
            Assert.That((ticks[3] - ticks[0]) / 60.0, Is.EqualTo(.25));
        }

        [TestCase("release")] [TestCase("empty")] [TestCase("death")] [TestCase("cancel")] [TestCase("submerged")]
        public void ContinuousFireStopsWithoutExtraShots(string reason)
        {
            var w = GameplayConfig.GetWeapon(4); var s = Alive();
            for (int tick = 0; tick <= 12; tick++) WeaponSimulation.Step(ref s, Input(tick), w, tick / 60.0, false, true);
            uint before = s.ShotSequence;
            if (reason == "empty") s.Ink = .79f;
            if (reason == "death") s.Health = 0;
            if (reason == "submerged") s.Swimming = true;
            for (int tick = 13; tick < 75; tick++)
            {
                var input = Input(tick, reason != "release"); input.CancelFire = reason == "cancel";
                Assert.That(WeaponSimulation.Step(ref s, input, w, tick / 60.0, false, !s.Swimming), Is.False);
            }
            Assert.That(s.ShotSequence, Is.EqualTo(before)); Assert.That(s.Ink, Is.GreaterThanOrEqualTo(0));
        }

        [Test] public void FinalShotLocksRecoveryForOneThirdSecond()
        {
            var w = GameplayConfig.GetWeapon(4); var s = Alive();
            for (int tick = 0; tick <= 12; tick++) WeaponSimulation.Step(ref s, Input(tick), w, tick / 60.0, false, true);
            Assert.That(s.InkRecoverAt, Is.EqualTo(12.0 / 60 + 1.0 / 3).Within(1e-8));
            for (int tick = 13; tick <= 32; tick++)
            {
                WeaponSimulation.Step(ref s, Input(tick, false), w, tick / 60.0, false, true);
                float before = s.Ink;
                ResourceSimulation.Step(ref s, GameplayConfig.GetHero(4), false, false, 1f / 60, tick / 60.0);
                Assert.That(s.Ink - before, Is.EqualTo(tick < 32 ? 0 : 10f / 60).Within(.00001f));
            }
        }

        [TestCase(3f, 4)] [TestCase(5f, 4)] [TestCase(6f, 4)]
        [TestCase(7f, 5)] [TestCase(8f, 6)] [TestCase(9f, 7)]
        public void BallisticAgeProducesTheApprovedDamageBands(float distance, int hits)
        {
            var w = GameplayConfig.GetWeapon(4);
            double age = InkBallistics.AgeAtDistance(w, w.SpeedMin, distance);
            float damage = WeaponSimulation.Damage(w, age);
            Assert.That(Mathf.CeilToInt(100 / damage), Is.EqualTo(hits));
            Assert.That(InkBallistics.Position(Vector3.zero, Vector3.forward * w.SpeedMin, w, age).z, Is.EqualTo(distance).Within(.0001f));
            Assert.That(InkBallistics.Position(Vector3.zero, Vector3.forward * w.SpeedMin, w, age).y, Is.LessThan(0), "Zero spread does not remove gravity");
        }

        [Test] public void ShotAnimationFinishesBeforeTheNextAutomaticRound()
        {
            var w = GameplayConfig.GetWeapon(4);
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>("Assets/GameResource/Characters/PistolGirl/PistolGirlPresentation.asset");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/GameResource/Characters/PistolGirl/Animations/PistolGirlCombat.controller");
            var shot = controller.layers[1].stateMachine.states.Single(s => s.state.name == "Shot").state;
            Assert.That(profile.SingleShot, Is.True, "Per-shot presentation remains separate from automatic gameplay");
            Assert.That(profile.ShotPlaybackSeconds, Is.EqualTo(.9f / w.FireRate).Within(.000001f));
            Assert.That(((AnimationClip)shot.motion).length / shot.speed, Is.EqualTo(profile.ShotPlaybackSeconds).Within(.000001f));
            Assert.That(profile.ShotPlaybackSeconds, Is.LessThan(WeaponSimulation.FireInterval(w)));
        }

        [Test] public void FixedAimPaintIsNarrowAndContinuousOnFloorAndWall()
        {
            const string output = "Reports/PistolGirl/20260917/paint";
            Directory.CreateDirectory(output);
            float connected = 0;
            var a = WeaponReferenceMeasurements.Capture(4, 0, "flat", 60, 12, output,
                (floor, origin) => connected = ConnectedInkForward(floor, origin));
            var b = WeaponReferenceMeasurements.Capture(4, 0, "wall-middle", 60, 12, output);
            Assert.That(a.paintStamps, Is.GreaterThan(0));
            Assert.That(a.ownedWidth, Is.LessThanOrEqualTo(2.5f));
            // A narrow irregular lane can skirt the exact x=0 line. Check the
            // connected ink from the first forward trail stamp (about z=1m),
            // separately from the existing foot puddle behind the muzzle.
            File.WriteAllText(output + "/connected-lane.txt", connected.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Assert.That(connected, Is.GreaterThanOrEqualTo(6f));
            Assert.That(b.paintStamps, Is.GreaterThan(0));
            Assert.That(b.impacts, Is.EqualTo(12));
        }

        static float ConnectedInkForward(PaintSurface floor, Vector3 origin)
        {
            var grid = floor.Ownership; int seed = -1; float nearest = float.PositiveInfinity;
            for (int i = 0; i < grid.Cells.Length; i++)
            {
                if (grid.Cells[i] != 1) continue;
                var point = floor.transform.TransformPoint(grid.Center(i)) - origin;
                float distance = (point - Vector3.forward).sqrMagnitude;
                if (distance < nearest) { nearest = distance; seed = i; }
            }
            Assert.That(seed, Is.GreaterThanOrEqualTo(0));
            Assert.That(nearest, Is.LessThan(.25f));
            var seen = new bool[grid.Cells.Length]; var pending = new Queue<int>(); pending.Enqueue(seed); seen[seed] = true;
            float forward = 0;
            while (pending.Count > 0)
            {
                int i = pending.Dequeue(), x = i % grid.Columns, z = i / grid.Columns;
                forward = Mathf.Max(forward, (floor.transform.TransformPoint(grid.Center(i)) - origin).z);
                void Visit(int n) { if (!seen[n] && grid.Cells[n] == 1) { seen[n] = true; pending.Enqueue(n); } }
                if (x > 0) Visit(i - 1); if (x + 1 < grid.Columns) Visit(i + 1);
                if (z > 0) Visit(i - grid.Columns); if (z + 1 < grid.Rows) Visit(i + grid.Columns);
            }
            return forward;
        }
    }
}
#endif
