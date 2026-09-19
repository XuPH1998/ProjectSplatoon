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
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BubbleShotgunTests
    {
        readonly List<Object> objects = new();
        static readonly Vector3 Origin = new(200, 50, 200);
        WeaponRuntimeConfig W => GameplayConfig.GetWeapon(9);
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup()
        { foreach (var o in objects) if (o != null) Object.DestroyImmediate(o); objects.Clear(); LubanConfigService.Current.Reset(); }
        GameObject Root(string name) { var go = new GameObject(name); objects.Add(go); return go; }
        InkShot Shot(uint id = 1, double born = 0, Vector3? origin = null, Vector3? velocity = null) => new()
        { Id = id, Round = 1, HeroId = 9, Shooter = 9000, Team = 1, Seed = 77, ActionId = id, Born = born,
            Origin = origin ?? Origin, Velocity = velocity ?? Vector3.forward * 18, Configuration = W };
        InkProjectileService Fire(InkShot? shot = null)
        { var s = new InkProjectileService(); s.SpawnForMeasurement(shot ?? Shot()); return s; }
        GameObject Box(Vector3 position, Vector3 size, int surface = 0)
        {
            var go = Root("Bubble shotgun obstacle"); go.transform.position = position; go.AddComponent<BoxCollider>().size = size;
            if (surface > 0) go.AddComponent<PaintSurface>().SurfaceId = surface;
            Physics.SyncTransforms(); return go;
        }
        static PlayerSnapshot Alive() => new() { HeroId = 9, Health = 100, Ink = 100, Team = 1, Revision = 1, Grounded = true };
        bool Tick(ref PlayerSnapshot s, int tick, bool held, bool cancel = false) => WeaponSimulation.Step(ref s,
            new PlayerInputFrame { Sequence = (uint)tick + 1, FireSequence = 1, Fire = held, CancelFire = cancel }, W, tick / 60.0, false, true);
        WeaponRuntimeConfig ConfiguredPellets(int count)
        {
            var source = Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(9).WeaponConfigPath));
            objects.Add(source); source.pelletCount = count;
            var config = source.Snapshot(); WeaponConfigValidation.Validate(config); return config;
        }

        [Test] public void IndependentAssetsAndBodyReuseAreValid()
        {
            WeaponConfigValidation.Validate(W);
            Assert.That(GameplayConfig.GetHero(9).WeaponTypeName, Is.EqualTo("爆泡霰弹枪"));
            Assert.That(GameplayConfig.GetHero(9).StandingHeight, Is.EqualTo(GameplayConfig.GetHero(3).StandingHeight));
            Assert.That(W.Ammo.Source, Is.Not.SameAs(GameplayConfig.GetWeapon(7).Ammo.Source));
            Assert.That(W.Ammo.BubblePrefab, Is.SameAs(GameplayConfig.GetWeapon(7).Ammo.BubblePrefab));
            Assert.That(W.CollisionRadius * 2, Is.GreaterThanOrEqualTo(1.2f));
            Assert.That(W.Ammo.AmmoId, Is.EqualTo(9));
            Assert.That(GameplayConfig.GetWeapon(3).MotionMode, Is.EqualTo(ProjectileMotionMode.Explosher));
            Assert.That(GameplayConfig.GetWeapon(7).MotionMode, Is.EqualTo(ProjectileMotionMode.BouncingBubble));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(5)] [TestCase(8)]
        public void QuickTapPaysOnceAndFormalEmitterUsesConfiguredPelletCount(int count)
        {
            var original = W;
            WeaponConfigService.Current.Replace(9, ConfiguredPellets(count));
            var state = Alive(); var ticks = new List<int>();
            for (int i = 0; i < 90; i++) if (Tick(ref state, i, i == 0)) ticks.Add(i);
            Assert.That(ticks, Is.EqualTo(new[] { 6 })); Assert.That(state.Ink, Is.EqualTo(84));
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            objects.Add(go); go.SetActive(false); var player = go.GetComponent<PrototypePlayer>();
            player.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>(GameplayConfig.GetHero(9).CharacterPrefabAddress.Replace("Character/", "Assets/GameResource/Characters/") + "/Prefabs/BubbleShotgunGirlVisual.prefab").GetComponent<InkCharacterView>();
            state.Position = Origin;
            var service = new InkProjectileService(); service.Spawn(player, state, .1, 1);
            Assert.That(service.Spawned.Count, Is.EqualTo(count));
            Assert.That(service.Spawned.Select(s => s.Id).Distinct().Count(), Is.EqualTo(count));
            Assert.That(service.Spawned.Select(s => s.ActionId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(service.Spawned.Select(s => s.Born).Distinct().Count(), Is.EqualTo(1));
            Assert.That(service.ActiveCount, Is.EqualTo(count));
            Assert.That(service.Spawned.All(s => float.IsFinite(s.Velocity.x) && float.IsFinite(s.Velocity.y) && float.IsFinite(s.Velocity.z)), Is.True);
            if (count == 1)
            {
                var aim = new TpsAimSolver().Resolve(player, state, 0);
                var local = Quaternion.Inverse(Quaternion.LookRotation(aim.InitialDirection)) * service.Spawned[0].Velocity;
                Assert.That(local.x, Is.EqualTo(0).Within(.0001), "Single floating bubbles stay horizontally centred");
                Assert.That(Mathf.Abs(Mathf.Asin(local.normalized.y) * Mathf.Rad2Deg), Is.LessThanOrEqualTo(W.FloatingPitchSpreadDegrees));
            }
            StringAssert.Contains($"{count} 颗齐射", WeaponDisplay.Mechanism(W));
            StringAssert.Contains($"每轮 {count} 颗", WeaponDisplay.Cadence(W));
            if (count == 1) StringAssert.Contains("单发水平居中", WeaponDisplay.Spread(W));
            var authored = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(9).WeaponConfigPath);
            Assert.That(authored.pelletCount, Is.EqualTo(original.PelletCount), "Runtime replacement must preserve the authored asset");
        }
        [Test] public void HoldMaintainsOneSecondCadenceUntilInkExhausted()
        {
            var state = Alive(); var ticks = new List<int>();
            for (int i = 0; i < 420; i++) if (Tick(ref state, i, true)) ticks.Add(i);
            Assert.That(ticks, Is.EqualTo(new[] { 6, 66, 126, 186, 246, 306 })); Assert.That(state.Ink, Is.EqualTo(4));
        }
        [Test] public void CancelledStartupAndInsufficientInkDoNotEmit()
        {
            var state = Alive(); Tick(ref state, 0, true); Tick(ref state, 1, true, true);
            for (int i = 2; i < 100; i++) Assert.That(Tick(ref state, i, true), Is.False);
            Assert.That(state.Ink, Is.EqualTo(100)); state = Alive(); state.Ink = 15.99f;
            for (int i = 0; i < 100; i++) Assert.That(Tick(ref state, i, true), Is.False);
        }
        [TestCase(1, 0f)] [TestCase(2, 60f)] [TestCase(3, 30f)] [TestCase(5, 15f)] [TestCase(8, 60f / 7)]
        public void FanUsesConfiguredCountAndIndependentVerticalRandomness(int count, float step)
        {
            var w = ConfiguredPellets(count);
            var v = Enumerable.Range(0, count).Select(i => InkBallistics.PelletVelocity(Vector3.forward, w, w.SpreadDegrees, i, 77)).ToArray();
            for (int i = 0; i < count; i++)
            {
                float yaw = Mathf.Atan2(v[i].x, v[i].z) * Mathf.Rad2Deg;
                float pitch = Mathf.Asin(v[i].normalized.y) * Mathf.Rad2Deg;
                Assert.That(yaw, Is.EqualTo(count == 1 ? 0 : -30 + i * step).Within(.0001));
                Assert.That(Mathf.Abs(pitch), Is.LessThanOrEqualTo(3));
                Assert.That(v[i].magnitude, Is.EqualTo(18).Within(.0001));
                Assert.That(v[i], Is.EqualTo(InkBallistics.PelletVelocity(Vector3.forward, w, w.SpreadDegrees, i, 77)));
                Assert.That(v[i], Is.EqualTo(InkBallistics.PelletVelocity(Vector3.forward, w, w.JumpSpreadDegrees, i, 77)));
            }
            Assert.That(v.Select(p => p.y).Distinct().Count(), Is.EqualTo(count));
            Assert.That(v[0].y, Is.Not.EqualTo(InkBallistics.PelletVelocity(Vector3.forward, w, w.SpreadDegrees, 0, 88).y));
        }
        [TestCase(0)] [TestCase(9)]
        public void InvalidPelletCountsAreRejected(int count)
            => Assert.Throws<InvalidOperationException>(() => ConfiguredPellets(count));
        [Test] public void VerticalRandomnessIsFrozenInTheWeaponSnapshotAndContentSignature()
        {
            var source = Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(9).WeaponConfigPath)); objects.Add(source);
            var before = source.Snapshot(); source.floatingPitchSpreadDegrees = 5; var after = source.Snapshot();
            Assert.That(before.FloatingPitchSpreadDegrees, Is.EqualTo(3));
            Assert.That(after.FloatingPitchSpreadDegrees, Is.EqualTo(5));
            Assert.That(before.SameValues(after), Is.False);
        }
        [TestCase(.05, .9, 18)] [TestCase(.1, 1.8, 18)] [TestCase(.2, 3.159, 9.18)]
        [TestCase(.3, 3.636, .36)] [TestCase(4, 4.968, .36)]
        public void AnalyticFlightHasTheAuthoredThreePhases(double age, double distance, double speed)
        {
            var shot = Shot(); Assert.That(InkBallistics.Position(shot, W, age).z - Origin.z, Is.EqualTo(distance).Within(.00005));
            Assert.That(InkBallistics.Velocity(shot, W, age).z, Is.EqualTo(speed).Within(.00002));
            Assert.That(WeaponSimulation.Damage(W, age), Is.EqualTo(55));
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void LifetimeIsFourSecondsAndExplodesExactlyOnceAcrossDriverRates(int rate)
        {
            var service = Fire(); for (int i = 1; i < 4 * rate; i++) service.Simulate(i / (double)rate);
            Assert.That(service.ActiveCount, Is.EqualTo(1)); Assert.That(service.Explosions, Is.Empty);
            service.Simulate(4); service.Simulate(8);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Explosions.Count, Is.EqualTo(1));
            Assert.That(service.Explosions[0].Time, Is.EqualTo(4)); Assert.That(service.Impacts.Single().Time, Is.EqualTo(4));
            Assert.That(service.Explosions[0].Position.z - Origin.z, Is.EqualTo(4.968).Within(.00005));
        }
        [TestCase(0)] [TestCase(30)] [TestCase(60)] [TestCase(90)] [TestCase(180)]
        public void GroundSlopeWallAndCeilingContactTerminateAtSphereCentre(int angle)
        {
            Vector3 direction = Quaternion.Euler(angle, 0, 0) * Vector3.forward;
            var box = Box(Origin + direction * 2, new Vector3(8, 8, .1f)); box.transform.rotation = Quaternion.Euler(angle, 0, 0); Physics.SyncTransforms();
            var service = Fire(Shot(velocity: direction * 18)); service.Simulate(.2);
            Assert.That(service.Explosions.Count, Is.EqualTo(1)); Assert.That(service.Bounces, Is.Empty);
            Assert.That(Vector3.Distance(service.Explosions[0].Position, Origin), Is.EqualTo(1.35).Within(.002));
            Assert.That(service.Explosions[0].Collision, Is.True);
        }
        [Test] public void PaintUsesItsOwnLargerSphereAndDoesNotLeakThroughAnObstacle()
        {
            var service = Fire(); var end = InkBallistics.Position(Shot(), W, 4);
            Box(end + Vector3.down * 2.7f, new Vector3(12, .1f, 12), 991);
            Box(end + Vector3.forward * 1, new Vector3(10, 10, .1f));
            Box(end + Vector3.forward * 2, new Vector3(10, 10, .1f), 992);
            // Isolate expiry paint from the flight by submitting a shot ending at the same location above these planes.
            var stamps = new List<PaintStamp>(); service.PaintObserved = stamps.Add; service.Simulate(4);
            Assert.That(stamps.Any(s => s.SurfaceId == 991), Is.True, "Paint probe must reach beyond the 2.5m damage radius");
            Assert.That(stamps.Any(s => s.SurfaceId == 992), Is.False, "Occluded paint surface");
            Assert.That(stamps.All(s => s.ClipEnabled && s.Radius <= 2.8f), Is.True);
        }
        [Test] public void EmbeddedSpawnExplodesOnTheNearSideAndPredictionUsesTheSameSphereCentre()
        {
            Box(Origin, new Vector3(12, 12, .1f));
            var service = Fire(); service.Simulate(.1);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Explosions.Count, Is.EqualTo(1));
            Assert.That(service.Explosions[0].Position.z, Is.LessThan(Origin.z - .65f));
            var aim = new TpsAimSolution { Muzzle = Origin, InitialDirection = Vector3.forward };
            Assert.That(WeaponImpactPrediction.TryPredict(new TpsAimSolver(), aim, W, Alive(), 9000, out var predicted), Is.True);
            Assert.That(Vector3.Distance(predicted, service.Explosions[0].Position), Is.LessThan(.001f));
        }
        [TestCase(-75)] [TestCase(0)] [TestCase(75)]
        public void BlockedFormalMuzzleNeverSpawnsBeyondTheWall(float pitch)
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            objects.Add(go); go.SetActive(false); var player = go.GetComponent<PrototypePlayer>();
            player.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/BubbleShotgunGirl/Prefabs/BubbleShotgunGirlVisual.prefab").GetComponent<InkCharacterView>();
            var state = Alive(); state.Position = Origin; state.Pitch = pitch;
            var solver = new TpsAimSolver(); var clear = solver.Resolve(player, state, 0);
            Box(clear.Pivot, new Vector3(12, 12, .1f));
            var aim = solver.Resolve(player, state, 0); Assert.That(aim.MuzzleBlocked, Is.True);
            Assert.That(WeaponImpactPrediction.TryPredict(solver, aim, W, state, player.PlayerId, out var predicted), Is.True);
            var service = new InkProjectileService(); service.Spawn(player, state, 0, 1);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Explosions.Count, Is.EqualTo(W.PelletCount));
            Assert.That(service.Spawned.All(s => s.Origin.z < clear.Pivot.z), Is.True);
            Assert.That(service.Explosions.All(e => Vector3.Distance(e.Position, predicted) < .001f), Is.True);
        }
        [Test] public void FlatPaintEnvelopeUsesTheSphereProjection()
        {
            var range = new WeaponRangeMetrics(W);
            Assert.That(range.Flat, Is.EqualTo(4.968f).Within(.0001f));
            Assert.That(range.PaintEnvelope, Is.InRange(7.57f, 7.58f));
        }
        [Test] public void NonConvexMapOverlapFindsTheActualSurfaceWithoutClosestPointWarnings()
        {
            var go = Root("Non-convex floor"); go.transform.position = Origin - Vector3.up * .3f;
            var mesh = new Mesh { vertices = new[] { new Vector3(-4,0,-4), new Vector3(-4,0,4), new Vector3(4,0,4), new Vector3(4,0,-4) }, triangles = new[] { 0,1,2,0,2,3 } };
            objects.Add(mesh); var collider = go.AddComponent<MeshCollider>(); collider.sharedMesh = mesh; Physics.SyncTransforms();
            var solver = new TpsAimSolver();
            Assert.That(solver.Overlap(Origin, .6f, 9000, Vector3.back, out var contact, floatingMesh: true), Is.True);
            Assert.That(contact.Point.y, Is.EqualTo(Origin.y - .3f).Within(.001f));
            var service = Fire(); service.Simulate(.1); Assert.That(service.Explosions.Count, Is.EqualTo(1));
            Assert.That(service.Explosions[0].Position, Is.EqualTo(Origin));
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }
        [Test] public void NoPaintIsEmittedDuringFlightAndExpiryFloorFootprintIsWide()
        {
            Box(Origin + Vector3.down * 1.3f, new Vector3(20, .1f, 20), 993);
            var service = new InkProjectileService(); var stamps = new List<PaintStamp>(); service.PaintObserved = stamps.Add;
            service.SpawnForMeasurement(Shot()); service.Simulate(.5); Assert.That(stamps, Is.Empty);
            service.Simulate(4); Assert.That(stamps.Count, Is.EqualTo(1)); Assert.That(stamps[0].Radius, Is.GreaterThan(2.5f));
        }
        [Test] public void FloorOwnershipStopsAtTheThinCornerWalls()
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            Box(Origin - Vector3.up * 1.3f, new Vector3(30, .1f, 30), 995);
            Box(Origin + new Vector3(1.5f, 0, 5), new Vector3(.1f, 8, 30));
            Box(Origin + new Vector3(0, 0, 6), new Vector3(30, 8, .1f));
            var stamps = new List<PaintStamp>(); var service = new InkProjectileService { PaintObserved = stamps.Add };
            service.SpawnForMeasurement(Shot()); service.Simulate(4);
            Assert.That(stamps.Any(s => s.SurfaceId == 995), Is.True);
            foreach (var stamp in stamps.Where(s => s.SurfaceId == 995))
                for (float x = -3; x < 4; x += .125f) for (float z = 2; z < 9; z += .125f)
                    if (x >= 1.6f || z >= 6.1f)
                        Assert.That(InkShapeAtlas.Coverage(Origin + new Vector3(x, -1.25f, z), stamp), Is.LessThan(.5f));
        }
        [Test] public void SplashFalloffAndCollisionExpiryParity()
        {
            foreach (bool collision in new[] { false, true })
            {
                Assert.That(InkExplosionRules.Damage(W.Ammo, collision, 0), Is.EqualTo(25));
                Assert.That(InkExplosionRules.Damage(W.Ammo, collision, 1.25f), Is.EqualTo(12.5f));
                Assert.That(InkExplosionRules.Damage(W.Ammo, collision, 2.5f), Is.Zero);
            }
            Assert.That(PrototypeRules.Damage(PrototypeRules.Damage(100, 55, false, 0, 10), 55, false, 0, 10), Is.Zero);
        }
        [Test] public void LateJoinSnapshotAndMeshMatchTheSlowTrajectoryWithoutBounceMessages()
        {
            var shot = Shot(); var service = Fire(shot); service.Simulate(2);
            var states = new List<InkBubbleState>(); service.CaptureBubbles(states); Assert.That(states.Count, Is.EqualTo(1));
            using var writer = new FastBufferWriter(2048, Allocator.Temp); writer.WriteNetworkSerializable(states[0]);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out InkBubbleState decoded); decoded.Shot.Configuration = W;
            using var visual = new BubbleFlightPresentation(Root("Restored bubble pool").transform);
            Assert.That(visual.Spawn(decoded.Shot, 2), Is.True); Assert.That(visual.Spawn(decoded.Shot, 2), Is.False);
            visual.Update(2 + BubbleFlightPresentation.RenderDelay);
            Assert.That(visual.TryPosition(shot.Round, shot.Id, 2, out var position), Is.True);
            Assert.That(position, Is.EqualTo(InkBallistics.Position(shot, W, 2)));
            Assert.That(visual.ActiveCount, Is.EqualTo(1)); visual.Clear(); visual.Clear(); Assert.That(visual.ActiveCount, Is.Zero);
            service.Clear(); service.CaptureBubbles(states); Assert.That(states, Is.Empty);
        }
        [Test] public void EightShootersRemainWithinBubblePoolCapacity()
        {
            var service = new InkProjectileService(); uint id = 0; int peak = 0; var times = new List<double>(); var watch = new System.Diagnostics.Stopwatch();
            for (int tick = 0; tick < 600; tick++)
            {
                double time = tick / 60.0;
                if (tick % 60 == 0) for (int player = 0; player < 8; player++) for (int pellet = 0; pellet < W.PelletCount; pellet++)
                    service.SpawnForMeasurement(Shot(++id, time, Origin + Vector3.right * player * 2,
                        InkBallistics.PelletVelocity(Vector3.forward, W, W.SpreadDegrees, pellet, (uint)tick + 1)));
                watch.Restart(); service.Simulate(time); watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
                peak = Math.Max(peak, service.ActiveCount); service.Spawned.Clear(); service.Impacts.Clear(); service.Explosions.Clear();
            }
            Assert.That(peak, Is.LessThanOrEqualTo(8 * W.PelletCount * 5)); service.Simulate(14); Assert.That(service.ActiveCount, Is.Zero);
            times.Sort(); Directory.CreateDirectory("Reports/BubbleShotgun");
            File.WriteAllText("Reports/BubbleShotgun/simulation-budget.txt", $"Eight shooters, 10 seconds, {id} bubbles; peak {peak}; simulation p95 {times[(int)(times.Count*.95)]:F4} ms. Editor CPU fixture; not device frame time.\n");
        }
        [Test] public void SustainedBubblesRecycleAllVisualsAndWidePaintStamps()
        {
            Box(Origin + Vector3.down * 1.3f, new Vector3(50, .1f, 50), 994);
            var service = new InkProjectileService(); var stamps = new List<PaintStamp>(); service.PaintObserved = stamps.Add;
            using var visual = new BubbleFlightPresentation(Root("Eight shooter presentation").transform);
            uint id = 0; int peak = 0; var times = new List<double>(); var watch = new System.Diagnostics.Stopwatch();
            for (int tick = 0; tick <= 600; tick++)
            {
                double now = tick / 60.0;
                if (tick < 360 && tick % 60 == 0) for (int player = 0; player < 8; player++) for (int pellet = 0; pellet < W.PelletCount; pellet++)
                {
                    var shot = Shot(++id, now, Origin + Vector3.right * player * 2);
                    service.SpawnForMeasurement(shot); Assert.That(visual.Spawn(shot, now), Is.True);
                }
                watch.Restart(); service.Simulate(now); watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
                visual.Update(now + BubbleFlightPresentation.RenderDelay); peak = Math.Max(peak, visual.ActiveCount);
            }
            Assert.That(stamps.Count, Is.EqualTo(8 * 6 * W.PelletCount)); Assert.That(visual.ActiveCount, Is.Zero);
            Assert.That(visual.Dropped, Is.Zero); Assert.That(peak, Is.LessThanOrEqualTo(8 * W.PelletCount * 5));
            times.Sort(); File.WriteAllText("Reports/BubbleShotgun/paint-pool-budget.txt",
                $"Eight shooters, six volleys each, {stamps.Count} explosion stamps; visual peak {peak}, dropped {visual.Dropped}; simulation and paint-query p95 {times[(int)(times.Count*.95)]:F4} ms, max {times.Last():F4} ms. Editor fixture includes collision and paint occlusion queries; no arena upload/GPU/device claim.\n");
        }
        [Test] public void NinthCardIsReachableWithoutMovingTheDetailsAndConfirm()
        {
            float height = HeroSelectionLayout.ContentHeight(9);
            Assert.That(height, Is.GreaterThan(HeroSelectionLayout.CardViewport.height));
            var card = HeroSelectionLayout.Card(8); card.y -= height - HeroSelectionLayout.CardViewport.height;
            Assert.That(card.yMin, Is.GreaterThanOrEqualTo(HeroSelectionLayout.CardViewport.yMin));
            Assert.That(card.yMax, Is.LessThanOrEqualTo(HeroSelectionLayout.CardViewport.yMax));
            Assert.That(HeroSelectionLayout.Confirm.y, Is.EqualTo(616));
        }
    }
}
#endif
