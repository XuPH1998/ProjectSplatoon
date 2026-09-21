#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class InkSeamTests
    {
        public const string Output = "Reports/InkSeams";
        PrototypeArena _arena;

        [SetUp] public void Setup()
        {
            WeaponReferenceMeasurements.LoadTables();
            var scene = EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity");
            _arena = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PrototypeArena>()).Single();
            _arena.InitializeRuntime(); Physics.SyncTransforms();
            Directory.CreateDirectory(Output);
        }

        [TearDown] public void Cleanup()
        {
            // Edit Mode scene destruction does not guarantee runtime OnDisable callbacks.
            if (_arena != null) foreach (var surface in _arena.Surfaces.Values) surface.ReleaseGraphics();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            LubanConfigService.Current.Reset();
        }

        [TestCase(1)] [TestCase(4)]
        public void RealProjectilesKeepGroundPlaneAtTileEdges(int hero)
        {
            var csv = new StringBuilder("hero,seam,heading,offset,impactX,impactY,impactZ,collisionNormalX,collisionNormalY,collisionNormalZ,paintNormalX,paintNormalY,paintNormalZ,receivers\n");
            int impacts = 0, rejected = 0;
            // Both axis-aligned splits and a four-tile junction, away from props and bridges.
            var seams = new[] { new Vector3(0, 0, -20), new Vector3(2, 0, -16), new Vector3(0, 0, -16) };
            foreach (var seam in seams)
            for (int heading = 0; heading < 8; heading++)
            foreach (float offset in new[] { -.15f, -.04f, -.005f, 0, .005f, .04f, .15f })
            {
                _arena.ClearPaint();
                var service = new InkProjectileService(); var stamps = new List<PaintStamp>();
                service.PaintObserved = stamps.Add;
                var direction = Quaternion.Euler(0, heading * 45, 0) * Vector3.forward;
                var target = seam + direction * offset;
                var shot = new InkShot { Id = 1, Round = 1, HeroId = hero, Team = 1, Shooter = 100000,
                    Seed = 9871, ShotSequence = 1, Origin = target - direction * .25f + Vector3.up * .25f,
                    Velocity = direction * 12 - Vector3.up * 12 };
                service.SpawnForMeasurement(shot); service.Simulate(.5);
                var impact = service.Impacts.Single(i => i.Hit);
                var stamp = stamps.First(s => Vector3.Distance(s.Position, impact.Position) < .01f);
                Assert.That(_arena.Surfaces[stamp.SurfaceId].name, Does.StartWith("Ground_"), "The seam fixture must actually hit the floor.");
                _arena.Apply(stamp, true); impacts++;
                string receivers = string.Join("|", _arena.Surfaces.Values.Where(s => s.HasPaint).Select(s => s.name));
                if (Vector3.Dot(stamp.Normal.normalized, Vector3.up) < .9999f) rejected++;
                csv.AppendLine(FormattableString.Invariant($"{hero},{Array.IndexOf(seams, seam)},{heading},{offset},{impact.Position.x},{impact.Position.y},{impact.Position.z},{impact.Normal.x},{impact.Normal.y},{impact.Normal.z},{stamp.Normal.x},{stamp.Normal.y},{stamp.Normal.z},{receivers}"));
            }
            File.WriteAllText(Output + $"/projectile-seams-hero-{hero}.csv", csv.ToString());
            TestContext.WriteLine($"Real projectile impacts={impacts}; non-coplanar paint normals={rejected}");
            Assert.That(rejected, Is.Zero, "A floor-edge collision normal must not exclude the adjacent floor from the same paint stamp.");
        }

        [TestCase(1, false)] [TestCase(4, false)] [TestCase(1, true)] [TestCase(4, true)]
        public void RealShotCrossesRampTop(int hero, bool downhill)
        {
            var ramp = _arena.Surfaces.Values.Single(s => s.name == "Ramp_-1_-1");
            var platform = _arena.Surfaces.Values.Single(s => s.name == "Platform_-1");
            var source = downhill ? platform : ramp;
            var destination = downhill ? ramp : platform;
            var join = new Vector3(-10.5f, 3, -5);
            var target = join + (downhill ? Vector3.forward : ramp.transform.forward * -1) * .12f;
            var normal = source.transform.up;
            var service = new InkProjectileService(); var stamps = new List<PaintStamp>();
            service.PaintObserved = stamps.Add;
            service.SpawnForMeasurement(new InkShot { Id = 1, Round = 1, HeroId = hero, Team = 1, Shooter = 100000,
                Seed = 9871, ShotSequence = 1, Origin = target + normal * .4f, Velocity = -normal * 20 });
            service.Simulate(.5);
            var impact = service.Impacts.Single(i => i.Hit);
            var stamp = stamps.First(s => Vector3.Distance(s.Position, impact.Position) < .01f);
            Assert.That(stamp.SurfaceId, Is.EqualTo(source.SurfaceId), "Actual shot must hit the intended side of the ramp join.");
            _arena.ClearPaint(); _arena.Apply(stamp, true);
            File.WriteAllText(Output + $"/ramp-hero-{hero}-{downhill}.txt",
                $"source={source.name}; target={destination.name}; normal={stamp.Normal}; receivers={string.Join("|", _arena.Surfaces.Values.Where(s => s.HasPaint).Select(s => s.name))}\n");
            Assert.That(destination.HasPaint, Is.True, "The same shot must continue over the shared ramp/platform edge.");
            Assert.That(destination.Ownership.PinkArea, Is.GreaterThan(0), "Destination gameplay ownership must receive the ink too.");
            foreach (var other in _arena.Surfaces.Values.Where(s => s.name.StartsWith("Ground_") || !s.Scores))
                Assert.That(other.HasPaint, Is.False, "Do not leak through the platform or onto walls: " + other.name);
            // The original network event must reproduce exactly the same unfolded footprint.
            uint before = _arena.OwnershipHash();
            _arena.ClearPaint(); _arena.Apply(stamp, true);
            Assert.That(_arena.OwnershipHash(), Is.EqualTo(before));
            stamp.Team = 2; stamp.Strength = 1;
            _arena.Apply(stamp, true);
            Assert.That(source.Ownership.BlueArea, Is.GreaterThan(0));
            Assert.That(destination.Ownership.BlueArea, Is.GreaterThan(0));
        }

        [Test] public void GroundSeamRequiresSharedEdgeAndDoesNotExpandShortBrush()
        {
            var ramp = _arena.Surfaces.Values.Single(s => s.name == "Ramp_-1_-1");
            var platform = _arena.Surfaces.Values.Single(s => s.name == "Platform_-1");
            var stamp = new PaintStamp { SurfaceId = ramp.SurfaceId, Position = ramp.transform.position,
                Normal = ramp.transform.up, Radius = .1f, Hardness = 1, Strength = 1, Team = 1 };
            _arena.Apply(stamp, true);
            Assert.That(platform.HasPaint, Is.False, "A stamp far from the hinge must not be copied to the platform.");
            _arena.ClearPaint();
            // An actual gap, even on otherwise adjoining faces, must not be bridged.
            platform.transform.position += Vector3.up * .03f;
            _arena.RegisterSurfaces();
            stamp.Position = new Vector3(-10.5f, 3, -5) - ramp.transform.forward * .1f;
            stamp.Radius = 1;
            _arena.Apply(stamp, true);
            Assert.That(platform.HasPaint, Is.False, "Separated surfaces must not form a seam link.");
        }

        [TestCase(-1, -1)] [TestCase(-1, 1)] [TestCase(1, -1)] [TestCase(1, 1)]
        public void AllRampTopsJoinOnlyTheirPlatforms(int side, int end)
        {
            var ramp = _arena.Surfaces.Values.Single(s => s.name == $"Ramp_{side}_{end}");
            var local = new Vector3(0, 0, ramp.WalkableSize.y / 2 - .1f);
            var stamp = new PaintStamp { SurfaceId = ramp.SurfaceId, Position = ramp.transform.TransformPoint(local),
                Normal = ramp.transform.up, Radius = .65f, Hardness = 1, Strength = 1, Team = 1, ShapeSeed = InkShapeAtlas.Pack(0, 1234) };
            _arena.Apply(stamp, true);
            var neighbours = _arena.Surfaces.Values.Where(s => s != ramp && s.HasPaint).ToArray();
            Assert.That(neighbours.Length, Is.EqualTo(1));
            Assert.That(neighbours[0].name, Is.EqualTo($"Platform_{side}"));
            Assert.That(neighbours[0].Ownership.PinkArea, Is.GreaterThan(0));
        }

        [Test, Timeout(900000), Explicit("Build the standalone client used by Tools/InkEdges/run_network_validation.py.")]
        public void BuildNetworkValidationPlayer()
        {
            Type.GetType("Splatoon.Editor.PrototypeBuilder, Splatoon.Editor").GetMethod("BuildWindowsTo")
                .Invoke(null, new object[] { "Temp/InkSeams/Player" });
        }
    }
}
#endif
