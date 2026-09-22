#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class AimBallisticsTests
    {
        public static readonly int[] Heroes = { 1, 4, 8, 2, 6, 5 };
        public static readonly string[] Names = { "", "RifleGirl", "DualPistolGirl", "ShotgunGirl", "PistolGirl", "RocketLauncherGirl", "MachineGunGirl", "BubbleGirl", "SplooshGirl", "BubbleShotgunGirl" };
        readonly List<Object> objects = new();
        static readonly Vector3 Origin = new(1000, 1000, 1000);
        [SetUp] public void Setup() { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load(); }
        [TearDown] public void Cleanup()
        { foreach (var o in objects) if (o != null) Object.DestroyImmediate(o); objects.Clear(); LubanConfigService.Current.Reset(); }
        GameObject Root(string name) { var go = new GameObject(name); objects.Add(go); return go; }
        PrototypePlayer Player(int hero)
        {
            var go = Root("Guide shooter"); go.AddComponent<NetworkObject>(); var p = go.AddComponent<PrototypePlayer>();
            p.GetComponent<CharacterController>().enabled = false;
            p.SimulationMuzzle = Root("Logical muzzle").transform; p.SimulationMuzzle.SetParent(go.transform);
            p.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{Names[hero]}/Prefabs/{Names[hero]}Visual.prefab").GetComponent<InkCharacterView>();
            return p;
        }
        static PlayerSnapshot State(int hero) => new() { HeroId = hero, Health = 100, Ink = 100, Team = 1, Grounded = true,
            Position = Origin, Revision = 1, ShotSequence = 1, BurstShotIndex = 1, FireBurstSequence = 7, LastShotCharge = 1 };
        static WeaponConfigAsset Asset(int hero) => AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(hero).WeaponConfigPath);

        [TestCase(1, 8, 2)] [TestCase(4, 4, 2)] [TestCase(8, 8, 2)]
        [TestCase(2, 7, 2)] [TestCase(6, 11, 0)] [TestCase(5, 15, 2)]
        public void SixProfilesHaveIndependentValidatedCapabilities(int hero, int frames, float inherit)
        {
            var w = GameplayConfig.GetWeapon(hero); WeaponConfigValidation.Validate(w);
            Assert.That(w.AimMode, Is.EqualTo(WeaponAimMode.WeaponReference));
            Assert.That(w.ShotGuideSeconds, Is.EqualTo(frames / 60.0).Within(1e-8));
            Assert.That(w.ShooterMoveForwardRate, Is.EqualTo(inherit));
            Assert.That(w.AngularSpread && w.DetailedPaint && w.InheritForwardMovement, Is.True);
            Assert.That(w.ShooterDetails, Is.False, "New capabilities do not enable the legacy action bundle");
            if (hero == 5) Assert.That(w.ReferenceBiasMin + w.ReferenceBiasMax + w.ReferenceBiasPerShot, Is.Zero);
        }
        [TestCase(3)] [TestCase(7)] [TestCase(9)] public void SpecialWeaponsRemainOnTheirOwnGeometryAndPaint(int hero)
        {
            var w = GameplayConfig.GetWeapon(hero);
            Assert.That(w.AimMode, Is.EqualTo(WeaponAimMode.CameraHit));
            Assert.That(w.AngularSpread || w.DetailedPaint || w.InheritForwardMovement, Is.False);
            WeaponConfigValidation.Validate(w);
        }

        [TestCaseSource(nameof(Heroes))] public void CameraHitDepthDoesNotSteerAndCameraRetractionPreservesForwardReferenceDistance(int hero)
        {
            var player = Player(hero); var state = State(hero); var solver = new TpsAimSolver();
            var initial = solver.Resolve(player, state, 0);
            var wall = Root("Movable aim surface").AddComponent<BoxCollider>(); wall.size = new Vector3(100, 100, .1f);
            foreach (float distance in new[] { 2f, 5f, 20f, 60f })
            {
                wall.transform.position = initial.Muzzle + Vector3.forward * distance; Physics.SyncTransforms();
                var aim = solver.Resolve(player, state, 0);
                Assert.That(Vector3.Distance(aim.InitialDirection, initial.InitialDirection), Is.LessThan(1e-6));
            }
            var w = GameplayConfig.GetWeapon(hero);
            float reference = InkBallistics.Position(Vector3.zero, Vector3.forward * WeaponLaunch.CenterSpeed(w, 1), w, w.ShotGuideSeconds).z;
            foreach (float distance in new[] { .1f, 3f, 10f })
            {
                var aim = WeaponLaunch.Geometry(Origin - Vector3.forward * distance + Vector3.up, Vector3.forward, Origin, w, 1);
                Assert.That(Vector3.Dot(aim.CorrectionPoint - Origin, Vector3.forward), Is.EqualTo(reference).Within(.0002));
            }
        }

        static IEnumerable<TestCaseData> Flights()
        {
            foreach (int hero in Heroes)
                foreach (float pitch in new[] { -60f, -30f, 0f, 30f, 60f })
                    yield return new TestCaseData(hero, pitch, 1f, false, hero == 2 ? 1 : 0, false);
            foreach (float charge in new[] { .15f, .5f, 1f }) foreach (bool released in new[] { false, true })
                yield return new TestCaseData(6, 0f, charge, released, 0, false);
            foreach (int hero in Heroes) yield return new TestCaseData(hero, 0f, 1f, false, 0, true);
        }
        [TestCaseSource(nameof(Flights))]
        public void GuideMatchesActualEmitterIncludingMovementChargeMuzzlesAndWorldContact(int hero, float pitch, float charge, bool released, int muzzle, bool wall)
        {
            var w = WeaponAssetTests.Changed(hero, a => a.speedMin = a.speedMax = (a.speedMin + a.speedMax) * .5f);
            WeaponConfigService.Current.SetForEditor(hero, w);
            var state = State(hero); state.Pitch = pitch; state.LastShotCharge = charge;
            state.PlanarVelocity = new Vector3(3, 7, -2);
            state.NextMuzzle = state.LastShotMuzzle = (byte)muzzle;
            state.WeaponPhase = released ? WeaponPhase.Firing : WeaponPhase.Charging;
            state.SplatlingRemaining = released ? 10 : 0; state.SplatlingReleasedCharge = charge; state.SplatlingChargeSeconds = charge * w.ChargeSeconds;
            var p = Player(hero); var solver = new TpsAimSolver();
            if (wall) { var box = Root("Guide intercept").AddComponent<BoxCollider>(); box.transform.position = Origin + Vector3.forward * 2; box.size = new Vector3(100, 100, .1f); }
            Physics.SyncTransforms();
            var aim = solver.Resolve(p, state, state.NextMuzzle);
            var forecast = WeaponImpactPrediction.Guide(solver, aim, w, state, p.PlayerId);
            var trace = new List<(double age, Vector3 point)>();
            var service = new InkProjectileService { TraceObserved = (_, age, point) => trace.Add((age, point)) };
            service.Spawn(p, state, 1, 1); service.Simulate(1 + w.ShotGuideSeconds + 1e-7);
            var actual = trace.Last();
            Assert.That(Vector3.Distance(actual.point, forecast.Point), Is.LessThan(.002f));
            if (!wall) Assert.That(forecast.Age, Is.EqualTo(w.ShotGuideSeconds).Within(1e-8));
            Assert.That(service.Spawned[0].Velocity, Is.EqualTo(WeaponLaunch.Velocity(aim, w, charge, state.PlanarVelocity, state.Yaw)));
            if (!wall && hero != 5) Assert.That(service.ActiveCount, Is.EqualTo(1), "Guide horizon does not expire the projectile");
        }

        [Test] public void GuideCannotHighlightAnEnemyBeyondItsTimeWindowAndDoesNotDamageWithinIt()
        {
            var w = GameplayConfig.GetWeapon(1); var state = State(1);
            var aim = WeaponLaunch.Geometry(Origin - Vector3.forward * 5, Vector3.forward, Origin, w, 1);
            var shot = WeaponLaunch.Representative(aim, w, 1);
            var targetRoot = Root("Prediction target"); targetRoot.AddComponent<NetworkObject>();
            var target = targetRoot.AddComponent<PrototypePlayer>(); target.GetComponent<CharacterController>().enabled = false;
            targetRoot.AddComponent<BoxCollider>().size = Vector3.one * .2f;
            target.Snapshot.Value = new PlayerSnapshot { HeroId = 1, Health = 100, Team = 2 };
            var solver = new TpsAimSolver();
            targetRoot.transform.position = InkBallistics.Position(shot, w, w.ShotGuideSeconds + .2); Physics.SyncTransforms();
            Assert.That(WeaponImpactPrediction.Guide(solver, aim, w, state, 999).HasEnemyContact, Is.False);
            targetRoot.transform.position = InkBallistics.Position(shot, w, 2.0 / 60); Physics.SyncTransforms();
            Assert.That(WeaponImpactPrediction.Guide(solver, aim, w, state, 999).HasEnemyContact, Is.True);
            Assert.That(target.Snapshot.Value.Health, Is.EqualTo(100));
        }

        [TestCaseSource(nameof(Heroes))] public void MovementProjectionIsSignedAndIgnoresSideAndVerticalMotion(int hero)
        {
            var w = GameplayConfig.GetWeapon(hero);
            var aim = WeaponLaunch.Geometry(Origin - Vector3.forward * 4, Vector3.forward, Origin, w, 1);
            foreach (float z in new[] { -3f, 0f, 3f })
            {
                var baseVelocity = WeaponLaunch.Velocity(aim, w, 1, Vector3.zero, 0);
                var actual = WeaponLaunch.Velocity(aim, w, 1, new Vector3(5, 9, z), 0);
                Assert.That(Vector3.Distance(actual - baseVelocity, Vector3.forward * (z * w.ShooterMoveForwardRate)), Is.LessThan(.00002f));
            }
        }

        [TestCase(.01f)] [TestCase(.04f)] [TestCase(.25f)] [TestCase(.4f)] [TestCase(.5f)]
        public void AngularSpreadHasTheSpecifiedMedianTailSymmetryAndBound(float bias)
        {
            var w = GameplayConfig.GetWeapon(8); const int count = 20000; int belowMedian = 0, below90 = 0;
            double sx = 0, sy = 0; uint seed = 19317;
            float q90 = 17.49f * WeaponLaunch.BiasedMagnitude(.9f, bias);
            for (int i = 0; i < count; i++)
            {
                var d = WeaponLaunch.SampleDirection(Vector3.forward, w, 17.49f, 17.49f, bias, ref seed);
                float angle = Mathf.Atan2(new Vector2(d.x, d.y).magnitude, d.z) * Mathf.Rad2Deg;
                Assert.That(angle, Is.LessThanOrEqualTo(17.4901f));
                if (angle <= 17.49f * bias) belowMedian++;
                if (angle <= q90) below90++;
                sx += d.x; sy += d.y;
            }
            Assert.That(belowMedian / (double)count, Is.EqualTo(.5).Within(.015));
            Assert.That(below90 / (double)count, Is.EqualTo(.9).Within(.015));
            Assert.That(Math.Abs(sx / count) + Math.Abs(sy / count), Is.LessThan(.004));
        }
        [Test] public void ZeroBiasDoesNotResetAirAgeAndZeroEnvelopeAlwaysShootsStraight()
        {
            var w = GameplayConfig.GetWeapon(5); var s = State(5); SpreadSimulation.Reset(ref s, w);
            SpreadSimulation.Before(ref s, w, 0); s.Grounded = false;
            for (int i = 1; i <= 71; i++) SpreadSimulation.Before(ref s, w, i / 60.0);
            Assert.That(s.DualiesJumpAge, Is.GreaterThan(1));
            Assert.That(ReferenceSpreadSimulation.Bias(s, w), Is.Zero.Within(.00001));
            uint seed = 17;
            for (int i = 0; i < 100; i++)
                Assert.That(WeaponLaunch.SampleDirection(Vector3.forward, w, 0, 0, .5f, ref seed), Is.EqualTo(Vector3.forward));
        }
        [Test] public void PaintRandomConsumptionCannotChangeLaunchOrSpeed()
        {
            var w = GameplayConfig.GetWeapon(6); var aim = WeaponLaunch.Geometry(Origin - Vector3.forward * 5, Vector3.forward, Origin, w, .5f);
            var before = WeaponLaunch.Velocity(aim, w, .5f, Vector3.zero, 0, 3, 2, .25f, 91, true);
            for (uint i = 0; i < 100; i++) { uint seed = WeaponLaunch.Stream(91, WeaponLaunch.DropStream, i); ShooterDetailSimulation.SplashVelocity(Vector3.forward, w, ref seed); }
            Assert.That(WeaponLaunch.Velocity(aim, w, .5f, Vector3.zero, 0, 3, 2, .25f, 91, true), Is.EqualTo(before));
            Assert.That(WeaponLaunch.Stream(91, WeaponLaunch.SpreadStream), Is.Not.EqualTo(WeaponLaunch.Stream(91, WeaponLaunch.SpeedStream)));
        }
        [TestCaseSource(nameof(Heroes))] public void FractionalBudgetAndFootPhaseAreIndependent(int hero)
        {
            var w = GameplayConfig.GetWeapon(hero); int sum = 0;
            for (uint n = 1; n <= 40; n++)
            {
                ShooterDetailSimulation.Schedule(n, w, out int count, out float first, out _); sum += count;
                Assert.That(sum, Is.EqualTo((int)Math.Floor(n * Math.Round(w.ReferenceTrailBudget, 6) + 1e-8)));
                Assert.That(first, Is.EqualTo(w.ReferenceTrailStart + (n - 1) % w.ShooterSplitNum * w.TrailSpacing / w.ShooterSplitNum).Within(.00001));
            }
            Assert.That(ShooterDetailSimulation.Foot(1, 1, w), Is.EqualTo(hero != 8));
            if (hero == 2) { Assert.That(ShooterDetailSimulation.Foot(1, 2, w), Is.False); Assert.That(ShooterDetailSimulation.Foot(1, 6, w), Is.True); }
            if (hero == 8) { Assert.That(ShooterDetailSimulation.Foot(5, 19, w), Is.True); Assert.That(ShooterDetailSimulation.Foot(1, 20, w), Is.False); }
        }

        [Test] public void ConfigChangesAreSignedAndInFlightShotsKeepTheirSnapshot()
        {
            var old = GameplayConfig.GetWeapon(1); var aim = WeaponLaunch.Geometry(Origin - Vector3.forward * 5, Vector3.forward, Origin, old, 1);
            var shot = WeaponLaunch.Representative(aim, old, 1); var position = InkBallistics.Position(shot, old, .5);
            var changed = WeaponAssetTests.Changed(1, a => { a.shotGuideSeconds += 1.0/60; a.shooterMoveForwardRate += 1; a.shooterSplashSideSpeed += 1; });
            Assert.That(old.SameValues(changed), Is.False); WeaponConfigService.Current.Replace(1, changed);
            Assert.That(shot.Configuration, Is.SameAs(old)); Assert.That(InkBallistics.Position(shot, shot.Configuration, .5), Is.EqualTo(position));
            foreach (string field in new[] { "aimMode", "shotGuideSeconds", "inheritForwardMovement", "angularSpread", "detailedPaint", "footSequence", "footPhase" })
            {
                var copy = Object.Instantiate(Asset(1)); objects.Add(copy); var f = typeof(WeaponConfigAsset).GetField(field);
                object value = f.GetValue(copy);
                f.SetValue(copy, f.FieldType == typeof(bool) ? !(bool)value : f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, 1-(int)Convert.ChangeType(value, typeof(int))) : f.FieldType == typeof(int) ? (object)((int)value+1) : (double)value+.01);
                Assert.That(copy.Snapshot().SameValues(old), Is.False, field);
            }
        }

        public static WeaponRuntimeConfig LegacySnapshot(int hero)
        {
            var copy = Object.Instantiate(Asset(hero));
            try
            {
                var lines = File.ReadAllLines($"Tools/ValidationData/AimBallistics/Baseline/{Names[hero]}WeaponConfig.asset");
                foreach (string line in lines)
                {
                    if (!line.StartsWith("  ") || line.StartsWith("   ")) continue;
                    int colon = line.IndexOf(':'); if (colon < 0) continue;
                    var field = typeof(WeaponConfigAsset).GetField(line.Substring(2, colon - 2)); if (field == null) continue;
                    string value = line.Substring(colon + 1).Trim();
                    if (field.FieldType == typeof(bool)) field.SetValue(copy, value == "1");
                    else if (field.FieldType.IsEnum) field.SetValue(copy, Enum.ToObject(field.FieldType, int.Parse(value, CultureInfo.InvariantCulture)));
                    else if (field.FieldType == typeof(int) || field.FieldType == typeof(float) || field.FieldType == typeof(double))
                        field.SetValue(copy, Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture));
                }
                copy.aimMode = WeaponAimMode.CameraHit; copy.shotGuideSeconds = 0; copy.angularSpread = copy.detailedPaint = copy.inheritForwardMovement = false;
                copy.footSequence = FootSequenceBasis.ShotSequence; copy.footPhase = 0;
                return copy.Snapshot();
            }
            finally { Object.DestroyImmediate(copy); }
        }
    }
}
#endif
