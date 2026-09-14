#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class TpsAimTests
    {
        static readonly Vector3 Origin = new(1000, 1000, 1000);
        readonly List<GameObject> _objects = new();
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
        }
        [TearDown] public void Cleanup()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear(); LubanConfigService.Current.Reset();
        }
        GameObject Root(string name) { var o = new GameObject(name); _objects.Add(o); return o; }
        BoxCollider Box(Vector3 position, Vector3 size)
        {
            var o = Root("Aim obstacle"); o.transform.position = position;
            var box = o.AddComponent<BoxCollider>(); box.size = size; Physics.SyncTransforms(); return box;
        }
        PrototypePlayer Player(int hero = 1)
        {
            var o = Root("Shooter"); o.AddComponent<NetworkObject>(); var p = o.AddComponent<PrototypePlayer>();
            p.GetComponent<CharacterController>().enabled = false;
            p.SimulationMuzzle = Root("Muzzle").transform; p.SimulationMuzzle.SetParent(o.transform);
            string[] names = { "", "RifleGirl", "DualPistolGirl", "ShotgunGirl", "PistolGirl", "RocketLauncherGirl" };
            p.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{names[hero]}/Prefabs/{names[hero]}Visual.prefab").GetComponent<InkCharacterView>();
            return p;
        }
        static TpsAimSolution Geometry(float hit, float correction = 6, float pitch = 0, float far = 50)
        {
            var rotation = Quaternion.Euler(pitch, 17, 0);
            return TpsAimSolver.Geometry(Origin, rotation * Vector3.forward, Origin + rotation * new Vector3(-.6f, -.1f, 4.5f), correction, far, hit);
        }
        static InkShot Shot(TpsAimSolution aim, int hero = 1)
        {
            var w = GameplayConfig.GetHero(hero);
            var shot = new InkShot { Id = 1, HeroId = hero, Seed = 71, Team = 1, Shooter = ulong.MaxValue,
                Origin = aim.Muzzle, Velocity = aim.InitialDirection * w.SpeedMin };
            InkBallistics.ApplyCorrection(ref shot, aim, w); return shot;
        }
        static void Equal(Vector3 actual, Vector3 expected, float tolerance = .0003f) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tolerance));

        [TestCase(0f)] [TestCase(4f)] [TestCase(5f)] [TestCase(6f)]
        public void NearAndBoundaryAimAtNearConvergence(float distance)
        {
            var aim = Geometry(distance); var shot = Shot(aim); var w = GameplayConfig.GetHero(1);
            Equal(aim.ExitDirection, aim.InitialDirection);
            Equal(aim.AimPoint, aim.CameraOrigin + aim.Forward * distance);
            Equal(aim.CorrectionPoint, aim.CameraOrigin + aim.Forward * 6);
            Equal(aim.InitialDirection, (aim.CorrectionPoint - aim.Muzzle).normalized);
            Equal(InkBallistics.Position(shot, w, InkBallistics.CorrectionAge(shot, w)), aim.CorrectionPoint);
        }
        [TestCase(6.001f, 0f)] [TestCase(20f, -45f)] [TestCase(80f, 35f)]
        [TestCase(100f, 0f)] [TestCase(float.PositiveInfinity, 65f)] [TestCase(101f, 0f)]
        public void FarOrMissKeepsMuzzleToTargetDirection(float distance, float pitch)
        {
            var aim = Geometry(distance, 6, pitch); var shot = Shot(aim); var w = GameplayConfig.GetHero(1);
            float targetDistance = distance <= 100 ? distance : 50;
            Equal(aim.CorrectionPoint, aim.CameraOrigin + aim.Forward * targetDistance);
            Equal(aim.AimPoint, aim.CorrectionPoint);
            Equal(aim.InitialDirection, (aim.CorrectionPoint - aim.Muzzle).normalized);
            Equal(shot.PostCorrectionVelocity, shot.Velocity);
            double crossing = InkBallistics.CorrectionAge(shot, w);
            foreach (double age in new[] { crossing * .5, crossing, crossing + .2 })
                Equal(InkBallistics.Position(shot, w, age), InkBallistics.Position(shot.Origin, shot.Velocity, w, age));
            Equal(InkBallistics.Velocity(shot, w, crossing - 1e-6), InkBallistics.Velocity(shot, w, crossing + 1e-6));
        }
        [TestCase(5f)] [TestCase(6f)] [TestCase(20f)] [TestCase(80f)]
        public void FarParameterDoesNotClampHits(float distance)
        {
            var a = Geometry(distance, far: 30); var b = Geometry(distance, far: 150);
            Equal(a.AimPoint, b.AimPoint); Equal(a.CorrectionPoint, b.CorrectionPoint);
            Equal(a.InitialDirection, b.InitialDirection);
        }
        [TestCase(30f)] [TestCase(50f)] [TestCase(150f)]
        public void MissUsesConfiguredFarPoint(float far)
        {
            var aim = Geometry(float.PositiveInfinity, far: far);
            Equal(aim.AimPoint, aim.CameraOrigin + aim.Forward * far);
            Equal(aim.CorrectionPoint, aim.AimPoint);
        }
        [TestCase(5f, -35f, -.6f)] [TestCase(5f, 40f, .6f)]
        [TestCase(20f, -35f, .6f)] [TestCase(float.PositiveInfinity, 40f, -.6f)]
        public void SpreadUsesEachMuzzleDirectionOnce(float hit, float pitch, float side)
        {
            var rotation = Quaternion.Euler(pitch, 17, 0);
            var aim = TpsAimSolver.Geometry(Vector3.zero, rotation * Vector3.forward,
                rotation * new Vector3(side, -.1f, 4.5f), 6, 50, hit);
            var w = GameplayConfig.GetHero(1); uint seed = 71, repeatSeed = 71;
            var shot = Shot(aim);
            shot.Velocity = InkBallistics.LaunchVelocity(aim.InitialDirection, w, ref seed, 5);
            Vector3 expected = InkBallistics.LaunchVelocity((aim.CorrectionPoint - aim.Muzzle).normalized, w, ref repeatSeed, 5);
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            Assert.That(seed, Is.EqualTo(repeatSeed));
            Assert.That(shot.Velocity, Is.EqualTo(expected));
            Assert.That(shot.PostCorrectionVelocity, Is.EqualTo(expected));
        }
        [TestCase(5f)] [TestCase(20f)] [TestCase(80f)] [TestCase(120f)]
        public void ResolveKeepsHundredMetreProbeIndependentOfFarConfig(float distance)
        {
            var player = Player(); var state = new PlayerSnapshot { HeroId = 1, Position = Origin, Team = 1 };
            var solver = new TpsAimSolver(); var initial = solver.Resolve(player, state, 0);
            var target = Box(initial.CameraOrigin + initial.Forward * distance, new Vector3(3, 3, .08f));
            var aim = solver.Resolve(player, state, 0);
            if (distance <= 100)
            {
                Assert.That(aim.AimHit.Collider, Is.EqualTo(target));
                Equal(aim.AimPoint, aim.AimHit.Point);
                Equal(aim.CorrectionPoint, aim.CameraOrigin + aim.Forward * Mathf.Max(6, distance - .04f));
            }
            else
            {
                Assert.That(aim.AimHit.Collider, Is.Null);
                Equal(aim.AimPoint, aim.CameraOrigin + aim.Forward * 50);
            }
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void NearObstacleStopsActualFlightBeforeConvergence(int frameRate)
        {
            var aim = TpsAimSolver.Geometry(Origin, Vector3.forward, Origin + new Vector3(-.6f, 0, 4.5f), 6, 50, 5);
            var target = Box(Origin + Vector3.forward * 5.04f, new Vector3(3, 3, .08f));
            var shot = Shot(aim); var service = new InkProjectileService();
            service.SpawnForMeasurement(shot);
            for (int i = 1; i <= frameRate; i++) service.Simulate((double)i / frameRate);
            Assert.That(service.Impacts.Count, Is.EqualTo(1));
            Assert.That(service.Impacts[0].Hit, Is.True);
            Assert.That(service.Impacts[0].Position.z, Is.EqualTo(target.bounds.min.z).Within(.002));
            Assert.That(service.Impacts[0].Position.z, Is.LessThan(aim.CorrectionPoint.z));
        }
        [Test] public void ObstructionStopsAtCloseAimDepthAndUsesNewLaunchLine()
        {
            var solver = new TpsAimSolver();
            var aim = TpsAimSolver.Geometry(Origin, Vector3.forward, Origin + Vector3.left * 3, 6, 50, 3);
            var target = Box(aim.AimPoint + Vector3.forward * .04f, new Vector3(.1f, .1f, .08f));
            aim.AimHit = new TpsCollision { Collider = target, Point = aim.AimPoint, Distance = 3 };
            // The target is off the near-clamped firing line. A wall beyond its depth must not warn.
            Box(Origin + new Vector3(0, 0, 4.5f), new Vector3(10, 2, .08f));
            Assert.That(solver.IsObstructed(aim, .01f, ulong.MaxValue), Is.False);
            Box(aim.Muzzle + aim.InitialDirection * 1.5f, Vector3.one * .08f);
            Assert.That(solver.IsObstructed(aim, .01f, ulong.MaxValue), Is.True);
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void GravityStartsBeforeLongConvergenceAndPersistsPastTarget(int hero)
        {
            var aim = TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, Vector3.left * .6f, 6, 14, float.PositiveInfinity);
            var shot = Shot(aim, hero); var w = GameplayConfig.GetHero(hero);
            double bend = InkBallistics.CorrectionAge(shot, w);
            double straight = WeaponSimulation.Seconds(w.StraightFrames);
            Assert.That(bend, Is.GreaterThan(straight));
            Assert.That(shot.GravityStartAge, Is.EqualTo(straight).Within(1e-7));
            Assert.That(InkBallistics.Position(shot, w, straight).y, Is.Zero.Within(.00001));
            double middle = (straight + bend) * .5;
            Assert.That(InkBallistics.Position(shot, w, middle).y, Is.EqualTo(-.5 * w.ProjectileGravity * Math.Pow(middle - straight, 2)).Within(.00001));
            Equal(InkBallistics.Position(shot, w, bend), aim.CorrectionPoint + Vector3.down * (float)(.5 * w.ProjectileGravity * Math.Pow(bend - straight, 2)));
            Assert.That(InkBallistics.Position(shot, w, bend + .2).y, Is.EqualTo(-.5 * w.ProjectileGravity * Math.Pow(bend + .2 - straight, 2)).Within(.00001));
            Equal(InkBallistics.Position(shot, w, bend - 1e-6), InkBallistics.Position(shot, w, bend + 1e-6));
            Assert.That(InkBallistics.Velocity(shot, w, bend - 1e-6).y, Is.EqualTo(-w.ProjectileGravity * (bend - straight)).Within(.0001));
            Assert.That(InkBallistics.Velocity(shot, w, bend + 1e-6).y, Is.EqualTo(-w.ProjectileGravity * (bend - straight)).Within(.0001));
            Equal(InkBallistics.Velocity(shot, w, bend + .2), (InkBallistics.Position(shot, w, bend + .2001) - InkBallistics.Position(shot, w, bend + .1999)) / .0002f, .02f);
            // A vanished near target does not postpone gravity or change the straight baseline.
            var near = Shot(TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, Vector3.left * .6f, 20, 50, 14), hero);
            Equal(near.Velocity, near.PostCorrectionVelocity);
            Assert.That(near.GravityStartAge, Is.EqualTo(shot.GravityStartAge));
            Assert.That(InkBallistics.Position(near, w, middle).y, Is.EqualTo(InkBallistics.Position(shot, w, middle).y).Within(.00001));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void ShortCorrectionDoesNotStartGravityBeforeOriginalStraightPeriod(int hero)
        {
            var aim = TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, new Vector3(-.6f, 0, 4.5f), 6, 6, float.PositiveInfinity);
            var shot = Shot(aim, hero); var w = GameplayConfig.GetHero(hero);
            double straight = WeaponSimulation.Seconds(w.StraightFrames), bend = InkBallistics.CorrectionAge(shot, w);
            Assert.That(bend, Is.LessThan(straight));
            Assert.That(InkBallistics.Position(shot, w, (straight + bend) * .5).y, Is.Zero.Within(.00001));
            Assert.That(InkBallistics.Position(shot, w, straight + .1).y, Is.EqualTo(-.5f * w.ProjectileGravity * .01f).Within(.00001));
        }
        [TestCase(-45f)] [TestCase(35f)]
        public void PitchedConvergencePreservesGravityDisplacementAndVelocity(float pitch)
        {
            var rotation = Quaternion.Euler(pitch, 17, 0);
            var aim = TpsAimSolver.Geometry(Vector3.zero, rotation * Vector3.forward, rotation * new Vector3(-.6f, -.1f, 0), 6, 14, float.PositiveInfinity);
            var shot = Shot(aim); var baseline = shot; baseline.GravityStartAge = 100;
            var w = GameplayConfig.GetHero(1); double bend = InkBallistics.CorrectionAge(shot, w);
            foreach (double age in new[] { bend - .01, bend, bend + .01 })
            {
                float fall = (float)age - shot.GravityStartAge;
                Equal(InkBallistics.Position(shot, w, age) - InkBallistics.Position(baseline, w, age), Vector3.down * (.5f * w.ProjectileGravity * fall * fall));
                Equal(InkBallistics.Velocity(shot, w, age) - InkBallistics.Velocity(baseline, w, age), Vector3.down * (w.ProjectileGravity * fall));
            }
            Equal(InkBallistics.Position(shot, w, bend - 1e-6), InkBallistics.Position(shot, w, bend + 1e-6));
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void GravityCanHitFloorBeforeCorrectionCompletes(int frameRate)
        {
            Box(new Vector3(0, -.25f, 0), new Vector3(100, .5f, 100));
            var aim = TpsAimSolver.Geometry(Vector3.up * .3f, Vector3.forward, new Vector3(-.6f, .3f, 0), 6, 30, float.PositiveInfinity);
            var shot = Shot(aim); var w = GameplayConfig.GetHero(1);
            var service = new InkProjectileService(); service.SpawnForMeasurement(shot);
            for (int i = 1; i <= frameRate; i++) service.Simulate((double)i / frameRate);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Impacts.Count, Is.EqualTo(1));
            Assert.That(service.Impacts[0].Hit, Is.True); Assert.That(service.Impacts[0].Position.y, Is.Zero.Within(.001));
            double age = shot.GravityStartAge + Math.Sqrt(2 * (.3f - w.CollisionRadius) / w.ProjectileGravity);
            Assert.That(age, Is.LessThan(InkBallistics.CorrectionAge(shot, w)));
            Assert.That(service.Impacts[0].Position.z, Is.EqualTo(InkBallistics.Position(shot, w, age).z).Within(.01));
        }
        [TestCase(0f)] [TestCase(.1f)] [TestCase(4f)] [TestCase(12f)] [TestCase(100f)]
        public void DistanceInverseIncludesBraking(float distance)
        {
            var w = GameplayConfig.GetHero(1);
            double age = InkBallistics.AgeAtDistance(w, w.SpeedMin, distance);
            Assert.That(w.SpeedMin * InkBallistics.TravelTime(w, age), Is.EqualTo(distance).Within(.0001f));
        }
        [TestCase(6f)] [TestCase(7f)]
        public void ConvergenceAtOrBehindMuzzleNeverLaunchesBackward(float muzzleDepth)
        {
            var aim = TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, Vector3.forward * muzzleDepth, 6, 50, 4);
            Assert.That(aim.FirstSegmentLength, Is.Zero); Equal(aim.InitialDirection, Vector3.forward);
        }
        [Test] public void ShotRoundTripPreservesEntireTrajectoryAfterShooterMoves()
        {
            var w = GameplayConfig.GetHero(1); var aim = Geometry(20); var shot = Shot(aim);
            using var writer = new FastBufferWriter(1024, Allocator.Temp); writer.WriteNetworkSerializable(shot);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out InkShot copy);
            Assert.That(copy.FirstSegmentLength, Is.EqualTo(shot.FirstSegmentLength));
            Assert.That(copy.GravityStartAge, Is.EqualTo(shot.GravityStartAge));
            Equal(copy.PostCorrectionVelocity, shot.PostCorrectionVelocity);
            aim.Muzzle += Vector3.one * 50; aim.Forward = Vector3.back;
            foreach (double age in new[] { 0, .025, .1, .4, 1.0 }) Equal(InkBallistics.Position(copy, w, age), InkBallistics.Position(shot, w, age));
        }
        [Test] public void ShotgunSpreadDoesNotReconvergeAndPreservesSeededPattern()
        {
            var aim = Geometry(5); var w = GameplayConfig.GetHero(3); var points = new List<Vector3>();
            for (int pellet = 0; pellet < w.PelletCount; pellet++)
            {
                var shot = Shot(aim, 3);
                shot.Velocity = InkBallistics.PelletVelocity(aim.InitialDirection, w, w.SpreadDegrees, pellet, 77);
                Vector3 sampled = shot.Velocity;
                InkBallistics.ApplyCorrection(ref shot, aim, w);
                Equal(sampled, shot.Velocity);
                Assert.That(Vector3.Angle(aim.InitialDirection, shot.Velocity), Is.EqualTo(Vector3.Angle(aim.ExitDirection, shot.PostCorrectionVelocity)).Within(.002f));
                Vector3 position = InkBallistics.Position(shot, w, InkBallistics.CorrectionAge(shot, w));
                Assert.That(Vector3.Distance(position, aim.CorrectionPoint), Is.GreaterThan(.01));
                foreach (var p in points) Assert.That(Vector3.Distance(position, p), Is.GreaterThan(.01));
                points.Add(position);
            }
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void CollisionSweepsBothLegsWhenOneFixedStepCrossesBend(int frameRate)
        {
            HeroMigrationTests.Load(rows => rows[0]["collisionRadius"] = .01f);
            Box(Origin + new Vector3(1, 0, .2f), new Vector3(.06f, .2f, .06f));
            var shot = new InkShot { Id = 1, HeroId = 1, Shooter = ulong.MaxValue, Team = 1, Seed = 13,
                Origin = Origin, Velocity = Vector3.right * 240, FirstSegmentLength = 1,
                PostCorrectionVelocity = Vector3.forward * 240, GravityStartAge = 1 };
            var service = new InkProjectileService(); service.SpawnForMeasurement(shot);
            for (int i = 1; i <= frameRate; i++) service.Simulate((double)i / frameRate);
            Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(service.Impacts[0].Hit, Is.True);
            Assert.That(service.Impacts[0].Position.x, Is.EqualTo(Origin.x + 1).Within(.03));
            Assert.That(service.Impacts[0].Position.z, Is.EqualTo(Origin.z + .17f).Within(.01));
        }
        [Test] public void VisibleTargetCanStillBeBlockedByMuzzlePath()
        {
            var player = Player(); var state = new PlayerSnapshot { HeroId = 1, Position = Origin, Team = 1, CurrentSpread = .000001f };
            var solver = new TpsAimSolver(); var aim = solver.Resolve(player, state, 0);
            Box(aim.CameraOrigin + aim.Forward * 12, new Vector3(4, 4, .1f));
            aim = solver.Resolve(player, state, 0);
            Box(Vector3.Lerp(aim.Muzzle, aim.CorrectionPoint, .5f), new Vector3(.08f, 2, .08f));
            aim = solver.Resolve(player, state, 0);
            Assert.That(aim.MuzzleBlocked, Is.False);
            Assert.That(solver.IsObstructed(aim, GameplayConfig.GetHero(1).CollisionRadius, player.OwnerClientId), Is.True);
            var service = new InkProjectileService(); service.Spawn(player, state, 0, 1); service.Simulate(.2);
            Assert.That(service.Impacts.Count, Is.EqualTo(1));
            Assert.That(service.Impacts[0].Position.z, Is.LessThan(aim.CorrectionPoint.z));
        }
        [Test] public void EmbeddedMuzzleResolvesImmediatelyWithoutFlightOrTrail()
        {
            var player = Player(); var state = new PlayerSnapshot { HeroId = 1, Position = Origin, Team = 1 };
            var muzzle = Origin + player.MuzzleOffset(0);
            Box(muzzle, Vector3.one * .1f);
            int paint = 0; var service = new InkProjectileService { PaintObserved = _ => paint++ };
            service.Spawn(player, state, 0, 1); service.Simulate(1);
            Assert.That(service.ActiveCount, Is.Zero); Assert.That(service.Impacts.Count, Is.EqualTo(1)); Assert.That(paint, Is.Zero);
        }
        [Test] public void CameraCollisionAndReticleProjectionUseLogicalAimWithoutShake()
        {
            var player = Player(); var state = new PlayerSnapshot { HeroId = 1, Position = Vector3.zero, Team = 1, Yaw = 10, Pitch = 25 };
            var solver = new TpsAimSolver(); var before = solver.Resolve(player, state, 0);
            Box(Vector3.Lerp(before.Pivot, before.CameraOrigin, .65f), Vector3.one * .4f);
            var aim = solver.Resolve(player, state, 0);
            Assert.That(Vector3.Distance(aim.Pivot, aim.CameraOrigin), Is.LessThan(Vector3.Distance(before.Pivot, before.CameraOrigin)));
            var camera = Root("Render camera").AddComponent<Camera>(); camera.aspect = 16f / 9;
            camera.transform.SetPositionAndRotation(aim.CameraOrigin, Quaternion.Euler(state.Pitch, state.Yaw, 0));
            Assert.That(Vector2.Distance(TpsAimSolver.ReticleViewport(camera, aim.AimPoint), Vector2.one * .5f), Is.LessThan(.0001));
            camera.transform.rotation *= Quaternion.Euler(-.12f, .035f, 0);
            Vector2 point = TpsAimSolver.ReticleViewport(camera, aim.AimPoint);
            Assert.That(Vector2.Distance(point, Vector2.one * .5f), Is.GreaterThan(.0001));
            var ray = camera.ViewportPointToRay(point);
            Assert.That(Vector3.Cross(ray.direction, (aim.AimPoint - ray.origin).normalized).magnitude, Is.LessThan(.0001));
        }
        [Test] public void MeasureFloorLandingBeforeAndAfterForNormalAndCompressedCamera()
        {
            Box(new Vector3(0, -.25f, 0), new Vector3(200, .5f, 200));
            var csv = new List<string> { "hero,camera,old_floor_x,old_floor_z,new_floor_x,new_floor_z,delta_z,old_fall_start,new_fall_start" };
            var traces = new List<string> { "hero,camera,trajectory,age,x,y,z" };
            string N(double number) => number.ToString("F6", CultureInfo.InvariantCulture);
            foreach (int hero in new[] { 1, 2, 3, 4, 5 })
            {
                var player = Player(hero); var w = GameplayConfig.GetHero(hero);
                foreach (float compression in new[] { 1f, .05f })
                {
                    string mode = compression == 1 ? "normal" : "compressed";
                    Vector3 camera = player.Presentation.CameraPivot + player.Presentation.CameraOffset * compression;
                    Vector3 muzzle = player.MuzzleOffset(0);
                    var aim = TpsAimSolver.Geometry(camera, Vector3.forward, muzzle, 6, 50, float.PositiveInfinity);
                    var corrected = Shot(aim, hero);
                    // Previous no-hit rule bent at six metres; its gravity clock was already independent.
                    var legacy = corrected; legacy.Id = 2;
                    Vector3 oldDelta = camera + Vector3.forward * 6 - muzzle;
                    legacy.Velocity = oldDelta.normalized * w.SpeedMin;
                    legacy.FirstSegmentLength = oldDelta.magnitude;
                    legacy.PostCorrectionVelocity = Vector3.forward * w.SpeedMin;
                    Vector3 Land(InkShot shot, string kind)
                    {
                        var simulation = new InkProjectileService();
                        simulation.TraceObserved = (s, age, point) => traces.Add(string.Join(",", hero, mode, kind, N(age), N(point.x), N(point.y), N(point.z)));
                        simulation.SpawnForMeasurement(shot); simulation.Simulate(w.Lifetime);
                        Assert.That(simulation.Impacts.Count, Is.EqualTo(1)); Assert.That(simulation.Impacts[0].Hit, Is.True);
                        Assert.That(simulation.Impacts[0].Position.y, Is.Zero.Within(.001));
                        return simulation.Impacts[0].Position;
                    }
                    var oldPoint = Land(legacy, "old"); var newPoint = Land(corrected, "corrected");
                    csv.Add(string.Join(",", hero, mode, N(oldPoint.x), N(oldPoint.z), N(newPoint.x), N(newPoint.z), N(newPoint.z - oldPoint.z), N(legacy.GravityStartAge), N(corrected.GravityStartAge)));
                }
            }
            Directory.CreateDirectory("Reports/TpsConvergence");
            File.WriteAllLines("Reports/TpsConvergence/floor-comparison.csv", csv);
            File.WriteAllLines("Reports/TpsConvergence/floor-trajectories.csv", traces);
        }
    }
}
#endif
