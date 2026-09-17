#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class RapidBlasterTests
    {
        WeaponRuntimeConfig W => GameplayConfig.GetWeapon(5);
        readonly List<GameObject> objects = new();
        [SetUp] public void Setup()
        {
            HeroMigrationTests.Load();
            // Exercise the immutable runtime snapshot directly. Historical test-row
            // adapters are tested separately and are not the production asset loader.
            var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(5).WeaponConfigPath);
            WeaponConfigService.Current.SetForEditor(5, asset.Snapshot());
        }
        [TearDown] public void Cleanup()
        { foreach (var go in objects) if (go != null) UnityEngine.Object.DestroyImmediate(go); LubanConfigService.Current.Reset(); }
        static PlayerSnapshot Alive() => new() { HeroId = 5, Health = 100, Ink = 100, Grounded = true, Team = 1, Revision = 1 };
        static bool Tick(ref PlayerSnapshot s, WeaponRuntimeConfig w, int tick, bool held = true, uint press = 1, bool cancel = false, bool emerged = false)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = held, FireSequence = press, Sequence = (uint)tick + 1, CancelFire = cancel }, w, tick / 60.0, emerged, true);

        [Test] public void LiveAssetMatchesPinnedReferenceAndHasExplosionVisual()
        {
            WeaponConfigValidation.Validate(W);
            var reference = SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/RapidBlaster/WeaponBlasterLight.1130.json"))["GameParameters"];
            Assert.That(W.FireMode, Is.EqualTo(WeaponFireMode.Blaster));
            Assert.That(W.Damage, Is.EqualTo(reference["DamageParam"]["ValueMax"].AsFloat / 10));
            Assert.That(W.ShotInk, Is.EqualTo(reference["WeaponParam"]["InkConsume"].AsFloat * 100).Within(.00001));
            Assert.That(W.BlasterRepeatSeconds, Is.EqualTo(35 / 60.0).Within(1e-8));
            Assert.That(W.Ammo.HasExplosionVisual, Is.True);
            Assert.That(InkFlightPresentation.IsContinuous(W), Is.False);
            Assert.That(GameplayConfig.GetHero(5).WeaponTypeName, Is.EqualTo("快速爆破枪"));
        }
        [TestCase(false, 8)] [TestCase(true, 12)]
        public void ShortClickCommitsExactlyOneShotAtStartup(bool emerged, int startup)
        {
            var s = Alive();
            for (int t = 0; t < startup; t++) Assert.That(Tick(ref s, W, t, t == 0, emerged: emerged), Is.False);
            Assert.That(Tick(ref s, W, startup, false), Is.True);
            for (int t = startup + 1; t < 120; t++) Assert.That(Tick(ref s, W, t, false), Is.False);
            Assert.That(s.Ink, Is.EqualTo(93)); Assert.That(s.ChargeElapsedSeconds, Is.Zero);
        }
        [Test] public void HeldFireHasExact35FrameCadenceAnd14ShotTank()
        {
            var s = Alive(); var frames = new List<int>();
            for (int t = 0; t <= 700; t++) if (Tick(ref s, W, t)) frames.Add(t);
            Assert.That(frames, Is.EqualTo(Enumerable.Range(0, 14).Select(i => 8 + i * 35)));
            Assert.That(s.Ink, Is.EqualTo(2));
        }
        [Test] public void CancelDoesNotReleaseACommittedStartupShot()
        {
            var s = Alive(); Tick(ref s, W, 0); Tick(ref s, W, 1, false, cancel: true);
            for (int t = 2; t < 80; t++) Tick(ref s, W, t, false);
            Assert.That(s.ShotSequence, Is.Zero); Assert.That(s.Ink, Is.EqualTo(100));
        }
        [Test] public void RecoverySurvivesCancelAndSwimCannotRecoverInkEarly()
        {
            var s = Alive(); for (int t = 0; t <= 8; t++) Tick(ref s, W, t);
            Assert.That(s.AttackRecoveryUntil, Is.EqualTo(28 / 60.0).Within(1e-8));
            Assert.That(s.InkRecoverAt, Is.EqualTo(55 / 60.0).Within(1e-8));
            Tick(ref s, W, 9, false, cancel: true);
            Assert.That(WeaponSimulation.RecoveryLocked(s, 27 / 60.0), Is.True);
            Assert.That(WeaponSimulation.RecoveryLocked(s, 28 / 60.0), Is.False);
            s.Swimming = s.FriendlyInkContact = true; s.SwimSource = SwimSurface.Friendly; s.Movement = MovementMode.GroundInk;
            ResourceSimulation.Step(ref s, GameplayConfig.GetHero(5), false, false, 1f / 60, 54 / 60.0);
            Assert.That(s.Ink, Is.EqualTo(93));
            ResourceSimulation.Step(ref s, GameplayConfig.GetHero(5), false, false, 1f / 60, 55 / 60.0);
            Assert.That(s.Ink, Is.GreaterThan(93));
        }
        [Test] public void FixedAirSpreadAppliesToFirstShotAndLandingImmediately()
        {
            var s = Alive(); s.Grounded = false;
            for (int t = 0; t <= 8; t++) Tick(ref s, W, t);
            Assert.That(s.LastShotSpread, Is.EqualTo(8)); Assert.That(s.SpreadProgress, Is.Zero);
            s.Grounded = true; Tick(ref s, W, 9, false); Assert.That(s.CurrentSpread, Is.Zero);
        }
        [Test] public void BlastDamageIsFlatAndCollisionExplosionUsesReducedRules()
        {
            Assert.That(InkExplosionRules.Damage(W.Ammo, false, 0), Is.EqualTo(35));
            Assert.That(InkExplosionRules.Damage(W.Ammo, false, W.Ammo.ExplosionRadius), Is.EqualTo(35));
            Assert.That(InkExplosionRules.Damage(W.Ammo, false, W.Ammo.ExplosionRadius + .001f), Is.Zero);
            Assert.That(InkExplosionRules.Damage(W.Ammo, true, 1), Is.EqualTo(17.5));
            Assert.That(InkExplosionRules.Damage(W.Ammo, true, 1.2f), Is.Zero);
            Assert.That(InkExplosionRules.Radius(W.Ammo, true), Is.EqualTo(1.16204f).Within(.00001));
        }
        [TestCase(85, 85)] [TestCase(85, 35)] [TestCase(35, 35, 35)]
        public void ExpectedDamageCombinationKillsOnlyOnLastHit(params float[] damages)
        {
            float hp = 100;
            for (int i = 0; i < damages.Length; i++)
            { hp = PrototypeRules.Damage(hp, damages[i], false, 0, 1); Assert.That(hp <= 0, Is.EqualTo(i == damages.Length - 1)); }
        }
        InkShot Shot(uint id = 1) => new() { HeroId = 5, Id = id, Team = 1, Round = 1, Shooter = 999,
            Origin = new Vector3(1000, 1000, 1000), Velocity = Vector3.forward * W.SpeedMin,
            Configuration = W, Born = 10, ActionId = id, Seed = id + 7 };
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void AirburstUsesSame15FrameTrajectoryAndExplodesOnlyOnce(int hz)
        {
            var service = new InkProjectileService(); var shot = Shot(); service.SpawnForMeasurement(shot);
            for (int i = 0; i < hz / 4; i++) service.Simulate(10 + (double)i / hz);
            Assert.That(service.Explosions, Is.Empty);
            service.Simulate(10.25);
            Assert.That(service.Explosions.Count, Is.EqualTo(1)); Assert.That(service.ActiveCount, Is.Zero);
            var point = service.Explosions[0].Position - shot.Origin;
            Assert.That(point.z, Is.EqualTo(11.14010).Within(.0002)); Assert.That(point.y, Is.EqualTo(-.367005).Within(.0002));
            service.Simulate(15); Assert.That(service.Explosions.Count, Is.EqualTo(1));
            service.Clear(); Assert.That(service.Explosions, Is.Empty);
        }
        [Test] public void WallImpactProducesEarlyCollisionExplosion()
        {
            var shot = Shot(); var wall = new GameObject("Blaster wall"); objects.Add(wall);
            wall.transform.position = shot.Origin + Vector3.forward * 5;
            wall.AddComponent<BoxCollider>().size = new Vector3(10, 10, .2f); Physics.SyncTransforms();
            var service = new InkProjectileService(); service.SpawnForMeasurement(shot); service.Simulate(10.25);
            Assert.That(service.Explosions.Count, Is.EqualTo(1)); Assert.That(service.Explosions[0].Collision, Is.True);
            Assert.That(service.Explosions[0].Position.z - shot.Origin.z, Is.LessThan(5));
        }
        [Test] public void NoExplosionPrefabDoesNotDisableGameplayAndOldShotsKeepSnapshot()
        {
            var source = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(5).WeaponConfigPath);
            var weapon = UnityEngine.Object.Instantiate(source); var ammo = UnityEngine.Object.Instantiate(source.ammoConfig);
            try
            {
                weapon.ammoConfig = ammo; ammo.explosionPrefab = null;
                var old = weapon.Snapshot(); Assert.That(old.Ammo.HasExplosion, Is.True); Assert.That(old.Ammo.HasExplosionVisual, Is.False);
                ammo.explosionEnabled = false; weapon.lifetime = 1;
                var shot = Shot(); shot.Configuration = old;
                var service = new InkProjectileService(); service.SpawnForMeasurement(shot); service.Simulate(10.25);
                Assert.That(service.Explosions.Count, Is.EqualTo(1)); Assert.That(old.Lifetime, Is.EqualTo(.25));
            }
            finally { UnityEngine.Object.DestroyImmediate(weapon); UnityEngine.Object.DestroyImmediate(ammo); }
        }
        [Test] public void RecoveryStateAndExplosionCauseSurviveNetworkRoundTrip()
        {
            var original = Alive(); original.AttackRecoveryUntil = .37125;
            using var writer = new FastBufferWriter(2048, Allocator.Temp);
            writer.WriteNetworkSerializable(original);
            using var reader = new FastBufferReader(writer, Allocator.Temp);
            reader.ReadNetworkSerializable(out PlayerSnapshot restored);
            Assert.That(restored.AttackRecoveryUntil, Is.EqualTo(original.AttackRecoveryUntil));
            var explosion = new InkExplosionEvent { Collision = true, ShotId = 71, Round = 4, Position = Vector3.one, Time = .1725 };
            using var eventWriter = new FastBufferWriter(512, Allocator.Temp);
            eventWriter.WriteNetworkSerializable(explosion);
            using var eventReader = new FastBufferReader(eventWriter, Allocator.Temp);
            eventReader.ReadNetworkSerializable(out InkExplosionEvent restoredExplosion);
            Assert.That(restoredExplosion.Collision, Is.True);
            Assert.That(restoredExplosion.ShotId, Is.EqualTo(explosion.ShotId));
            Assert.That(restoredExplosion.Time, Is.EqualTo(explosion.Time));
            for (int t = 0; t < 90; t++) { Tick(ref original, W, t); Tick(ref restored, W, t); }
            Assert.That(restored.ShotSequence, Is.EqualTo(original.ShotSequence)); Assert.That(restored.Ink, Is.EqualTo(original.Ink));
        }
        GameObject Object(string name, Vector3 position)
        { var go = new GameObject(name); objects.Add(go); go.transform.position = position; return go; }
        [Test] public void MotorKeepsRecoveryMovementAndBlocksPaperUntilExactBoundary()
        {
            var origin = new Vector3(500, .01f, 500);
            var floor = Object("Blaster movement floor", origin - Vector3.up * .51f);
            floor.AddComponent<BoxCollider>().size = new Vector3(30, 1, 30);
            var player = Object("Blaster movement probe", origin); player.layer = 8;
            var cc = player.AddComponent<CharacterController>(); cc.height = 1.8f; cc.radius = .35f; cc.center = Vector3.up * .9f;
            var motor = new PlayerMotorSimulation(cc); var s = Alive(); s.Position = origin; s.PlanarVelocity = Vector3.forward * W.ShootMoveSpeed;
            motor.Restore(s); Physics.SyncTransforms(); cc.Move(Vector3.down * .1f); s.Position = player.transform.position; s.Grounded = cc.isGrounded;
            s.AttackRecoveryUntil = 20 / 60.0;
            var input = new PlayerInputFrame { Swim = true, Move = Vector2.up };
            for (int t = 0; t < 20; t++)
            {
                motor.Step(ref s, input, 1f / 60, t / 60.0, false, W.ShootMoveSpeed);
                Assert.That(s.Swimming, Is.False); Assert.That(s.PlanarVelocity.magnitude, Is.EqualTo(W.ShootMoveSpeed).Within(.001));
            }
            motor.Step(ref s, input, 1f / 60, 20 / 60.0, false, W.ShootMoveSpeed);
            Assert.That(s.Swimming, Is.True);
        }
        [Test] public void TrailAndExplosionPaintAreIdenticalAcrossOuterRatesAndStayInFrontOfWall()
        {
            var shot = Shot(); shot.Origin = new Vector3(500, 1, 500);
            var floor = Object("Blaster paint floor", new Vector3(500, -.1f, 507));
            floor.AddComponent<BoxCollider>().size = new Vector3(30, .2f, 30);
            var surface = floor.AddComponent<PaintSurface>(); surface.SurfaceId = 90001;
            Physics.SyncTransforms();
            List<PaintStamp> Measure(int hz)
            {
                var stamps = new List<PaintStamp>(); var service = new InkProjectileService { PaintObserved = stamps.Add };
                service.SpawnForMeasurement(shot);
                for (int i = 1; i <= hz; i++) service.Simulate(shot.Born + (double)i / hz);
                Assert.That(service.Explosions.Count, Is.EqualTo(1)); return stamps;
            }
            var baseline = Measure(60);
            Assert.That(baseline.Count, Is.InRange(2, 12));
            Assert.That(baseline.Last().Radius, Is.EqualTo(W.Ammo.ExplosionPaintRadiusMin).Within(.00001));
            foreach (int hz in new[] { 30, 144 })
            {
                var actual = Measure(hz); Assert.That(actual.Count, Is.EqualTo(baseline.Count));
                for (int i = 0; i < baseline.Count; i++)
                { Assert.That(actual[i].Position, Is.EqualTo(baseline[i].Position)); Assert.That(actual[i].ShapeSeed, Is.EqualTo(baseline[i].ShapeSeed)); Assert.That(actual[i].Radius, Is.EqualTo(baseline[i].Radius)); }
            }
            var center = BlasterBallistics.Position(shot.Origin, shot.Velocity, W, .25);
            var wall = Object("Paint occluder", center + Vector3.right * .8f);
            wall.AddComponent<BoxCollider>().size = new Vector3(.1f, 5, 30); Physics.SyncTransforms();
            foreach (var clipped in Measure(60))
                Assert.That(clipped.Position.x + clipped.Radius, Is.LessThan(wall.transform.position.x - .05f));
        }
    }
}
#endif
