#if UNITY_EDITOR
using System;
using System.Linq;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Splatoon.Config;
using Splatoon.Combat;

namespace Splatoon.Tests
{
    public sealed class AmmoPresentationTests
    {
        const string Rifle = "Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset";
        GameObject root;
        WeaponConfigAsset weapon;
        AmmoConfigAsset ammo;
        InkPresentation presentation;
        Camera camera;
        readonly ParticleSystem.Particle[] particles = new ParticleSystem.Particle[2048];

        static Recorder AllocationRecorder()
        {
            var recorder = Recorder.Get("GC.Alloc"); recorder.FilterToCurrentThread();
            recorder.enabled = false; recorder.enabled = true;
            var positiveControl = new byte[1024]; GC.KeepAlive(positiveControl);
            recorder.enabled = false;
            Assert.That(recorder.sampleBlockCount, Is.GreaterThan(0), "Profiler must detect a known managed allocation");
            return recorder;
        }

        [SetUp] public void Setup()
        {
            weapon = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(Rifle));
            ammo = UnityEngine.Object.Instantiate(weapon.ammoConfig); weapon.ammoConfig = ammo;
            root = new GameObject("Ammo versions"); presentation = root.AddComponent<InkPresentation>();
            camera = root.AddComponent<Camera>(); camera.transform.position = new Vector3(3, 2, -4);
        }
        [TearDown] public void Cleanup()
        {
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(weapon); UnityEngine.Object.DestroyImmediate(ammo);
        }
        InkShot Shot(uint id, WeaponRuntimeConfig config) => new()
        {
            Id = id, Round = 1, ActionId = id, Team = 1, Shooter = 1000, Seed = 123 + id,
            Origin = Vector3.up * 2, Velocity = Vector3.forward * 32, Born = 10, Configuration = config, ConfigurationRevision = id,
        };

        [Test] public void VersionPoolsKeepOldShotsBoundAndDelayAtCapacity()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            var costs = new string[InkPresentation.VersionPoolLimit + 1];
            costs[0] = "version,createCpuMs,managedAllocationCount";
            var recorder = AllocationRecorder();
            for (uint i = 1; i <= InkPresentation.VersionPoolLimit; i++)
            {
                ammo.visualLifetime = 1 + i * .1f;
                var shot = Shot(i, weapon.Snapshot());
                recorder.enabled = true;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                presentation.Spawn(shot);
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                recorder.enabled = false; int allocated = recorder.sampleBlockCount;
                costs[i] = FormattableString.Invariant($"{i},{elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3},{allocated}");
            }
            Directory.CreateDirectory("Reports/InkFlightReference/AmmoMerge");
            File.WriteAllLines("Reports/InkFlightReference/AmmoMerge/version-creation.csv", costs);
            Assert.That(presentation.VersionPoolCount, Is.EqualTo(12));
            ammo.visualLifetime = 3;
            Assert.That(presentation.TryPrepareAmmo(weapon.Snapshot().Ammo), Is.False);
            presentation.UpdateFlights(10.2, camera);
            int count = presentation.CopyParticles(1, particles);
            Assert.That(particles.Take(count).Select(p => Math.Round(p.startLifetime, 2)).Distinct().Count(), Is.EqualTo(12));
            Assert.That(presentation.ActiveShots, Is.EqualTo(12));
            presentation.Impact(new InkImpact { Round = 1, Id = 1 });
            Assert.That(presentation.TryPrepareAmmo(weapon.Snapshot().Ammo), Is.True);
            Assert.That(presentation.VersionPoolCount, Is.EqualTo(12));
            presentation.ClearFlights(10.3);
            presentation.Spawn(Shot(100, weapon.Snapshot()));
            presentation.UpdateFlights(10.4, camera);
            Assert.That(presentation.ParticleCount, Is.Zero, "Clear cutoff rejects old shots across every version");
        }

        [Test] public void WarmAggregateUpdateDoesNotAllocateAndImpactBeforeShotStaysComplete()
        {
            var shot = Shot(1, weapon.Snapshot());
            presentation.Impact(new InkImpact { Round = 1, Id = 1 }); presentation.Spawn(shot);
            Assert.That(presentation.VersionPoolCount, Is.Zero);
            shot.Id = 2; presentation.Spawn(shot);
            for (int i = 0; i < 8; i++) presentation.UpdateFlights(10.2, camera);
            var recorder = AllocationRecorder(); recorder.enabled = true;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < 60; i++) presentation.UpdateFlights(10.2 + i * .001, camera);
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            recorder.enabled = false; int allocated = recorder.sampleBlockCount;
            Assert.That(allocated, Is.Zero);
            Directory.CreateDirectory("Reports/InkFlightReference/AmmoMerge");
            File.WriteAllText("Reports/InkFlightReference/AmmoMerge/warm-update.txt",
                FormattableString.Invariant($"60 warmed UpdateFlights calls: GC.Alloc samples={allocated}; mean CPU={elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 60:F4} ms. Positive allocation control passed. Editor CPU call measurement; not a device GPU/frame-time result.\n"));
            Assert.That(presentation.CopyParticles(1, particles), Is.GreaterThan(0));
        }

        [Test] public void DiscreteShotsUseConfiguredParticleCount()
        {
            weapon.fireMode = WeaponFireMode.SemiAutomatic; ammo.particlesPerBurst = 5;
            var config = weapon.Snapshot();
            using var flight = new InkFlightPresentation(root.transform, config.Ammo);
            Assert.That(flight.Spawn(Shot(1, config), 10), Is.True);
            flight.Update(10.1, camera);
            Assert.That(flight.ActiveShots, Is.EqualTo(1));
            Assert.That(flight.ParticleCount, Is.EqualTo(5));
        }

        [Test] public void MuzzleRetirementKeepsExistingParticlesAndStopsFurtherBursts()
        {
            var emitter = presentation.CreateMuzzle(root.transform, weapon.Snapshot());
            emitter.Shot(123, 1, true, 10, 10);
            int count = emitter.System.particleCount;
            Assert.That(count, Is.EqualTo(ammo.muzzleBurstCount));
            emitter.Retire(root.transform);
            emitter.Present(true, true, 11); emitter.Shot(124, 1, true, 11, 11);
            Assert.That(emitter.System.particleCount, Is.EqualTo(count), "Retiring must not clear particles or emit more");
            Assert.That(emitter.BurstCount, Is.EqualTo(1));
            // Simulate advances particles but leaves the native emitter paused, so restore
            // the stop-emitting state used by automatic Play Mode simulation before IsAlive.
            emitter.System.Simulate(2, true, false);
            emitter.System.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Assert.That(emitter.HasLiveParticles, Is.False);
        }

        [Test] public void ExpiredAuthoritativeShotUsesBornExplosionRulesAfterAmmoChanges()
        {
            HeroMigrationTests.Load();
            try
            {
                weapon.lifetime = .1f; ammo.explosionEnabled = true;
                ammo.explosionPrefab = root; ammo.explosionRadius = 0;
                var old = weapon.Snapshot(); var shot = Shot(1, old); shot.HeroId = 1;
                shot.Origin = Vector3.one * 1000;
                var service = new InkProjectileService(); service.SpawnForMeasurement(shot);
                ammo.explosionEnabled = false; ammo.explosionPrefab = null;
                WeaponConfigService.Current.SetForEditor(1, weapon.Snapshot());
                service.Simulate(10.2);
                Assert.That(service.Explosions.Count, Is.EqualTo(1), "Old shot still explodes after disabling explosions");
                Assert.That(service.Explosions[0].ConfigurationRevision, Is.EqualTo(shot.ConfigurationRevision));
                Assert.That(service.Impacts.Count, Is.EqualTo(1));
                service.Simulate(10.3); Assert.That(service.Explosions.Count, Is.EqualTo(1));
                shot.Id = 2; shot.Configuration = weapon.Snapshot();
                service.SpawnForMeasurement(shot); service.Simulate(10.2);
                Assert.That(service.Explosions.Count, Is.EqualTo(1), "New shot uses disabled explosion");
            }
            finally { LubanConfigService.Current.Reset(); }
        }
    }
}
#endif
