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
        static TpsAimSolution Geometry(float hit, float correction = 6, float pitch = 0)
        {
            var rotation = Quaternion.Euler(pitch, 17, 0);
            return TpsAimSolver.Geometry(Origin, rotation * Vector3.forward, Origin + rotation * new Vector3(-.6f, -.1f, 4.5f), correction, hit);
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

        [TestCase(5f)] [TestCase(6f)]
        public void NearAndBoundaryAimDirectlyAtIntersection(float distance)
        {
            var aim = Geometry(distance); var shot = Shot(aim); var w = GameplayConfig.GetHero(1);
            Equal(aim.ExitDirection, aim.InitialDirection);
            Equal(aim.CorrectionPoint, aim.AimPoint);
            Equal(InkBallistics.Position(shot, w, InkBallistics.CorrectionAge(shot, w)), aim.AimPoint);
        }
        [TestCase(6.001f, 0f)] [TestCase(20f, -45f)] [TestCase(float.PositiveInfinity, 65f)]
        public void FarOrMissPassesCorrectionThenFollowsFullAimDirection(float distance, float pitch)
        {
            var aim = Geometry(distance, 6, pitch); var shot = Shot(aim); var w = GameplayConfig.GetHero(1);
            double bend = InkBallistics.CorrectionAge(shot, w);
            Equal(InkBallistics.Position(shot, w, bend), aim.CorrectionPoint);
            Equal(shot.PostCorrectionVelocity.normalized, aim.Forward);
            Equal(InkBallistics.Position(shot, w, bend * .5), Vector3.Lerp(aim.Muzzle, aim.CorrectionPoint, .5f));
            double after = bend + .01;
            Equal(InkBallistics.Position(shot, w, after), aim.CorrectionPoint + aim.Forward *
                (shot.Velocity.magnitude * InkBallistics.TravelTime(w, after) - aim.FirstSegmentLength));
            Assert.That(Vector3.Distance(InkBallistics.Position(shot, w, bend - 1e-6), InkBallistics.Position(shot, w, bend + 1e-6)), Is.LessThan(.001));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void GravityWaitsForCorrectionAndOriginalStraightPeriod(int hero)
        {
            var aim = TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, Vector3.left * .6f, 14, float.PositiveInfinity);
            var shot = Shot(aim, hero); var w = GameplayConfig.GetHero(hero);
            double bend = InkBallistics.CorrectionAge(shot, w);
            Assert.That(bend, Is.GreaterThan(WeaponSimulation.Seconds(w.StraightFrames)));
            Assert.That(InkBallistics.Position(shot, w, bend * .99).y, Is.Zero.Within(.00001));
            Assert.That(InkBallistics.Position(shot, w, bend + .2).y, Is.EqualTo(-.5f * w.ProjectileGravity * .2f * .2f).Within(.00001));
            Equal(InkBallistics.Velocity(shot, w, bend + .2), (InkBallistics.Position(shot, w, bend + .2001) - InkBallistics.Position(shot, w, bend + .1999)) / .0002f, .02f);
            // Near target moves away: straight direction remains, and falling starts at the saved target distance.
            var near = Shot(TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, Vector3.left * .6f, 20, 14), hero);
            Equal(near.Velocity, near.PostCorrectionVelocity);
            Assert.That(near.GravityStartAge, Is.EqualTo(shot.GravityStartAge));
        }
        [TestCase(0f)] [TestCase(.1f)] [TestCase(4f)] [TestCase(12f)] [TestCase(100f)]
        public void DistanceInverseIncludesBraking(float distance)
        {
            var w = GameplayConfig.GetHero(1);
            double age = InkBallistics.AgeAtDistance(w, w.SpeedMin, distance);
            Assert.That(w.SpeedMin * InkBallistics.TravelTime(w, age), Is.EqualTo(distance).Within(.0001f));
        }
        [TestCase(0f)] [TestCase(4f)] [TestCase(4.5f)]
        public void TargetsBehindOrAtMuzzleNeverLaunchBackward(float distance)
        {
            var aim = TpsAimSolver.Geometry(Vector3.zero, Vector3.forward, Vector3.forward * 4.5f, 6, distance);
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
            var aim = Geometry(20); var w = GameplayConfig.GetHero(3); var points = new List<Vector3>();
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
            var player = Player(); var state = new PlayerSnapshot { HeroId = 1, Position = Origin, Team = 1 };
            var solver = new TpsAimSolver(); var aim = solver.Resolve(player, state, 0);
            Box(aim.CameraOrigin + aim.Forward * 12, new Vector3(4, 4, .1f));
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
                    var aim = TpsAimSolver.Geometry(camera, Vector3.forward, muzzle, 6, float.PositiveInfinity);
                    var corrected = Shot(aim, hero);
                    var legacy = new InkShot { Id = 2, HeroId = hero, Shooter = ulong.MaxValue, Team = 1, Seed = 71,
                        Origin = muzzle, Velocity = (camera + Vector3.forward * 100 - muzzle).normalized * w.SpeedMin };
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
                    csv.Add(string.Join(",", hero, mode, N(oldPoint.x), N(oldPoint.z), N(newPoint.x), N(newPoint.z), N(newPoint.z - oldPoint.z), N(WeaponSimulation.Seconds(w.StraightFrames)), N(corrected.GravityStartAge)));
                }
            }
            Directory.CreateDirectory("Reports/TpsAim");
            File.WriteAllLines("Reports/TpsAim/floor-comparison.csv", csv);
            File.WriteAllLines("Reports/TpsAim/floor-trajectories.csv", traces);
        }
    }
}
#endif
