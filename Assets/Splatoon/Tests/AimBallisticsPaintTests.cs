#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class AimBallisticsPaintTests
    {
        readonly List<Object> objects = new();
        const string Output = "Reports/AimBallistics/Measurements";
        static readonly Vector3 Origin = new(1600, 5, 1600);
        [SetUp] public void Setup() { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load(); InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath)); }
        [TearDown] public void Cleanup() { foreach (var o in objects) if (o != null) Object.DestroyImmediate(o); objects.Clear(); LubanConfigService.Current.Reset(); }
        GameObject Root(string name) { var go = new GameObject(name); objects.Add(go); return go; }
        PaintSurface Surface(int id, Vector3 center, Vector2 size, Quaternion rotation)
        {
            var go = Root("Detailed ink surface " + id); go.SetActive(false); go.transform.SetPositionAndRotation(center, rotation);
            var box = go.AddComponent<BoxCollider>(); box.center = Vector3.down * .05f; box.size = new Vector3(size.x, .1f, size.y);
            var surface = go.AddComponent<PaintSurface>(); surface.enabled = false; surface.SurfaceId = id; surface.Scores = id == 1; surface.WalkableSize = size;
            if (id == 2) surface.WallRegions = new[] { new PaintRegion { Id = 1, Size = size, Climbable = true } };
            surface.InitializeOwnership(.125f); go.SetActive(true); return surface;
        }
        [TestCaseSource(typeof(AimBallisticsTests), nameof(AimBallisticsTests.Heroes))]
        public void CoverageAndEventsAreDeterministicAcrossDriverRatesAndSeeds(int hero)
        {
            var current = GameplayConfig.GetWeapon(hero);
            foreach (string scenario in new[] { "flat", "up30", "down30", "high-drop", "wall-near", "wall-middle", "wall-far", "slope", "occluded" })
            {
                float charge = hero == 6 ? .25f : 1; int actions = hero == 6 ? 1 : 10;
                for (uint seed = 0; seed < 2; seed++)
                {
                    var directory = $"{Output}/After/seed-{seed}"; Directory.CreateDirectory(directory);
                    var baseline = WeaponReferenceMeasurements.Capture(hero, charge, scenario, 60, actions, directory, fullActions: true,
                        options: new WeaponReferenceMeasurements.Options { Seed = seed });
                    Assert.That(baseline.emittedProjectiles, Is.GreaterThan(0)); Assert.That(baseline.paintStamps, Is.GreaterThan(0));
                    foreach (int rate in new[] { 30, 144 })
                    {
                        var actual = WeaponReferenceMeasurements.Capture(hero, charge, scenario, rate, actions, null, fullActions: true,
                            options: new WeaponReferenceMeasurements.Options { Seed = seed });
                        Assert.That(actual.gridHash, Is.EqualTo(baseline.gridHash), $"hero={hero}, {scenario}, seed={seed}, rate={rate}");
                        Assert.That(actual.paintStamps, Is.EqualTo(baseline.paintStamps));
                        Assert.That(actual.impacts, Is.EqualTo(baseline.impacts));
                        Assert.That(actual.inkSpent, Is.EqualTo(baseline.inkSpent));
                    }
                }
                var before = $"{Output}/Before"; Directory.CreateDirectory(before);
                try
                {
                    WeaponConfigService.Current.SetForEditor(hero, AimBallisticsTests.LegacySnapshot(hero));
                    WeaponReferenceMeasurements.Capture(hero, charge, scenario, 60, actions, before, fullActions: true);
                }
                finally { WeaponConfigService.Current.SetForEditor(hero, current); }
            }
        }

        [TestCase(1, false)] [TestCase(2, false)] [TestCase(4, false)] [TestCase(5, false)] [TestCase(6, false)] [TestCase(8, false)]
        [TestCase(1, true)] [TestCase(2, true)] [TestCase(4, true)] [TestCase(5, true)] [TestCase(6, true)] [TestCase(8, true)]
        public void WallFlowDetachesAndUnpaintableCanopyStopsFallingInk(int hero, bool blocked)
        {
            Surface(1, new Vector3(Origin.x, 0, Origin.z), new Vector2(20, 20), Quaternion.identity);
            Surface(2, Origin + Vector3.forward * 1.5f, new Vector2(12, 1), Quaternion.Euler(-90, 0, 0));
            if (blocked) { var canopy = Root("Unpaintable canopy"); canopy.transform.position = Origin + new Vector3(0, -2, 1.4f); canopy.AddComponent<BoxCollider>().size = new Vector3(20, .05f, 20); }
            Physics.SyncTransforms();
            var w = WeaponAssetTests.Changed(hero, a => a.referenceTrailBudget = 0);
            var stamps = new List<PaintStamp>(); var service = new InkProjectileService { PaintObserved = stamps.Add };
            service.SpawnForMeasurement(new InkShot { Id = 17, ActionId = 1, ShotSequence = 1, HeroId = hero, Team = 1, Seed = 31,
                Origin = Origin, Velocity = Vector3.forward * w.SpeedMin, Configuration = w, Charge = 1 });
            service.Simulate(10);
            Assert.That(stamps.Count(s => s.SurfaceId == 2), Is.GreaterThan(2));
            Assert.That(stamps.Any(s => s.SurfaceId == 1), Is.EqualTo(!blocked));
        }

        [TestCaseSource(typeof(AimBallisticsTests), nameof(AimBallisticsTests.Heroes))]
        public void ImmediateMuzzleObstructionKeepsFootPaintButCannotBackfillTrailBudget(int hero)
        {
            var ground = Surface(1, new Vector3(Origin.x, 0, Origin.z), new Vector2(20, 20), Quaternion.identity);
            var playerRoot = Root("Foot emitter"); playerRoot.AddComponent<NetworkObject>(); var p = playerRoot.AddComponent<PrototypePlayer>();
            p.GetComponent<CharacterController>().enabled = false; p.SimulationMuzzle = Root("Logical muzzle").transform;
            p.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{AimBallisticsTests.Names[hero]}/Prefabs/{AimBallisticsTests.Names[hero]}Visual.prefab").GetComponent<InkCharacterView>();
            var state = new PlayerSnapshot { HeroId = hero, Team = 1, Health = 100, Ink = 100, Grounded = true,
                Position = new Vector3(Origin.x, .04f, Origin.z), Revision = 1, ShotSequence = 1, BurstShotIndex = hero == 8 ? 5u : 1u, LastShotCharge = 1 };
            var aim = new TpsAimSolver().Resolve(p, state, 0);
            var blocker = Root("Embedded unpaintable muzzle"); blocker.transform.position = aim.Muzzle; blocker.AddComponent<BoxCollider>().size = Vector3.one * .5f;
            Physics.SyncTransforms();
            Assert.That(new TpsAimSolver().Resolve(p, state, 0).MuzzleBlocked, Is.True);
            // Disable explosion paint to isolate foot/trail scheduling from the specialized explosion path.
            var original = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(hero).WeaponConfigPath);
            var clone = Object.Instantiate(original); objects.Add(clone); var ammo = Object.Instantiate(original.ammoConfig); objects.Add(ammo);
            clone.ammoConfig = ammo; ammo.explosionPaint = false;
            var w = clone.Snapshot(); WeaponConfigService.Current.SetForEditor(hero, w);
            var stamps = new List<PaintStamp>(); var service = new InkProjectileService { PaintObserved = stamps.Add };
            service.Spawn(p, state, 0, 1); service.Simulate(10);
            Assert.That(stamps.Count, Is.EqualTo(1), "One independent foot drop and no skipped-distance trail drops");
            Assert.That(stamps[0].Radius, Is.EqualTo(w.ReferenceFootRadius));
            Assert.That(stamps[0].Position.y, Is.EqualTo(0).Within(.002));
        }

        [TestCase(1)] [TestCase(4)] [TestCase(8)] [TestCase(2)] [TestCase(6)]
        public void ImpactShapeUsesDistanceIncidenceAndFallHeight(int hero)
        {
            var w = GameplayConfig.GetWeapon(hero);
            Assert.That(ShooterDetailSimulation.ImpactRadius(w.ShooterPaintNearDistance, w), Is.EqualTo(w.ShooterPaintNearRadius).Within(.00001));
            Assert.That(ShooterDetailSimulation.ImpactRadius(w.PaintDistanceMiddle, w), Is.EqualTo(w.PaintRadiusMax).Within(.00001));
            Assert.That(ShooterDetailSimulation.ImpactRadius(w.PaintDistanceFar, w), Is.EqualTo(w.PaintRadiusMin).Within(.00001));
            Assert.That(ShooterDetailSimulation.ImpactDepth(Vector3.down, Vector3.up, 0, 0, w), Is.EqualTo(w.PaintDepthMin).Within(.00001));
            Assert.That(ShooterDetailSimulation.ImpactDepth(Vector3.forward, Vector3.up, 0, 0, w), Is.EqualTo(w.PaintDepthMax).Within(.00001));
            Assert.That(ShooterDetailSimulation.ImpactDepth(Vector3.forward, Vector3.up, w.ShooterFallHeightMax, 1, w), Is.EqualTo(w.PaintDepthBreakMin).Within(.00001));
        }
    }
}
#endif
