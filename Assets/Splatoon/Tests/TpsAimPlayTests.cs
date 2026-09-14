#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class TpsAimPlayTests
    {
        const string Output = "Reports/TpsAim/PlayMode";
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static IEnumerator Wait(Func<bool> ready, string reason, double seconds = 20)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, reason);
        }
        [UnityTest]
        public IEnumerator HostFireReticleAndRenderedFlightAcrossFiveHeroes()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator ExitAfterFailure()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        static GameObject Box(string name, Vector3 position, Vector3 scale)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = name;
            box.transform.position = position; box.transform.localScale = scale; Physics.SyncTransforms(); return box;
        }
        static void SetPose(PrototypePlayer player, Vector3 position)
        {
            var state = player.Snapshot.Value; state.Position = position; state.Yaw = state.Pitch = state.BodyYaw = 0;
            state.Velocity = state.PlanarVelocity = Vector3.zero; state.VerticalSpeed = 0; state.Grounded = true; state.Ink = 100;
            player.Snapshot.Value = state; player.transform.position = position;
            typeof(PrototypePlayer).GetField("_look", Private).SetValue(player, Vector2.zero);
            Physics.SyncTransforms();
        }
        static void Capture(Camera camera, string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor")
                .GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
            type.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { camera, name, Output });
        }
        static IEnumerator Scenario()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap ready");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current; yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Real host and presentation");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current; var solver = new TpsAimSolver();
            var platform = Box("TPS test platform", new Vector3(0, 5, 0), new Vector3(30, 1, 40));
            Vector3 feet = new(0, 5.54f, 0);
            var report = new List<string>();
            var csv = new List<string> { "hero,age,old_x,old_y,old_z,new_x,new_y,new_z" };
            var streams = (ParticleSystem[])typeof(InkPresentation).GetField("_streams", Private).GetValue(InkPresentation.Current);
            int visiblePixels = 0;
            foreach (int hero in new[] { 1, 2, 3, 4, 5 })
            {
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Switch hero " + hero);
                SetPose(player, feet); yield return null;
                foreach (float distance in new[] { 5f, 12f })
                {
                    SetPose(player, feet);
                    var state = player.Snapshot.Value; state.CurrentSpread = .000001f; state.LastShotCharge = 1;
                    var aim = solver.Resolve(player, state, state.LastShotMuzzle);
                    var target = Box("TPS target", aim.CameraOrigin + aim.Forward * distance, new Vector3(3, 3, .08f));
                    var impacts = new List<InkImpact>();
                    match.Projectiles.Clear(); InkPresentation.Current.Clear();
                    double born = player.NetworkManager.ServerTime.Time;
                    match.Projectiles.Spawn(player, state, born, match.State.Value.Round);
                    var shots = match.Projectiles.Spawned.ToArray();
                    Assert.That(shots.Length, Is.EqualTo(GameplayConfig.GetHero(hero).PelletCount));
                    Assert.That(shots.All(s => s.PostCorrectionVelocity.sqrMagnitude > 0), Is.True);
                    match.Projectiles.TraceObserved = (shot, age, point) =>
                    {
                        // Record the authority's actual terminal target position before ServerTick clears its events.
                        if (Mathf.Abs(point.z - (target.transform.position.z - .04f)) < .02f)
                            impacts.Add(new InkImpact { Position = point, Id = shot.Id });
                    };
                    yield return Wait(() => impacts.Count >= shots.Length, "Authoritative target hit hero=" + hero + " distance=" + distance, 3);
                    foreach (var impact in impacts)
                    {
                        Assert.That(Mathf.Abs(impact.Position.x - aim.AimPoint.x), Is.LessThan(.03f));
                        // Far shots can already be falling: reticle is not a gravity compensator.
                        if (distance <= 6) Assert.That(Mathf.Abs(impact.Position.y - aim.AimPoint.y), Is.LessThan(.03f));
                    }
                    report.Add($"Hero {hero}: camera target {distance:F0}m, {shots.Length} authoritative projectiles hit; correction data present");
                    match.Projectiles.TraceObserved = null;
                    if (hero == 1 && distance == 12) Capture(Camera.main, "rifle-far-target");
                    Object.Destroy(target); yield return null;
                }
                // Show real RPC-delivered particles in clear air, at configured speed/spread and gravity.
                SetPose(player, feet + Vector3.up * 4);
                var live = player.Snapshot.Value; live.LastShotCharge = 1;
                match.Projectiles.Clear(); InkPresentation.Current.Clear();
                match.Projectiles.Spawn(player, live, player.NetworkManager.ServerTime.Time, match.State.Value.Round);
                var flight = match.Projectiles.Spawned[0]; var w = GameplayConfig.GetHero(hero);
                yield return Wait(() => streams[live.Team - 1].particleCount >= w.PelletCount, "RPC flying particles hero " + hero, 2);
                var stream = streams[live.Team - 1]; var particles = new ParticleSystem.Particle[1024]; int count = stream.GetParticles(particles);
                Assert.That(count, Is.GreaterThanOrEqualTo(w.PelletCount));
                var camera = new GameObject("Flight evidence camera").AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1;
                Vector3 center = particles.Take(count).Aggregate(Vector3.zero, (sum, p) => sum + p.position) / count;
                camera.transform.position = center + new Vector3(1.8f, .6f, -1.8f); camera.transform.LookAt(center);
                string label = "hero-" + hero + "-flight";
                Capture(camera, label);
                var renderer = stream.GetComponent<ParticleSystemRenderer>(); renderer.enabled = false; Capture(camera, label + "-control"); renderer.enabled = true;
                var shown = new Texture2D(2, 2); var hidden = new Texture2D(2, 2);
                shown.LoadImage(File.ReadAllBytes(Output + "/" + label + ".png")); hidden.LoadImage(File.ReadAllBytes(Output + "/" + label + "-control.png"));
                int changed = shown.GetPixels32().Zip(hidden.GetPixels32(), (a, b) => Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b)).Count(d => d > 30);
                Assert.That(changed, Is.GreaterThan(25), "Corrected particles contribute visible GPU pixels"); visiblePixels += changed;
                Object.Destroy(shown); Object.Destroy(hidden); Object.Destroy(camera.gameObject);
                // Compare the old aim-at-hit/far-point formula to a zero-spread corrected center shot.
                var solution = solver.Resolve(player, live, live.LastShotMuzzle);
                var centerShot = flight; centerShot.Velocity = solution.InitialDirection * flight.Velocity.magnitude;
                InkBallistics.ApplyCorrection(ref centerShot, solution, w);
                Vector3 oldVelocity = (solution.AimPoint - solution.Muzzle).normalized * flight.Velocity.magnitude;
                for (int i = 0; i <= 120; i++)
                {
                    double age = i / 120.0;
                    var oldPoint = InkBallistics.Position(solution.Muzzle, oldVelocity, w, age) - feet;
                    var newPoint = InkBallistics.Position(centerShot, w, age) - feet;
                    csv.Add(string.Join(",", new[] { hero.ToString(), age.ToString("F6", CultureInfo.InvariantCulture), oldPoint.x.ToString("F6", CultureInfo.InvariantCulture), oldPoint.y.ToString("F6", CultureInfo.InvariantCulture), oldPoint.z.ToString("F6", CultureInfo.InvariantCulture), newPoint.x.ToString("F6", CultureInfo.InvariantCulture), newPoint.y.ToString("F6", CultureInfo.InvariantCulture), newPoint.z.ToString("F6", CultureInfo.InvariantCulture) }));
                }
                report.Add($"Hero {hero}: host RPC + GPU particles={count}, visiblePixels={changed}, fallStart={flight.GravityStartAge:F5}s");
            }
            player.RequestHeroChange(1, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 1, "Return to rifle");
            SetPose(player, feet);
            var before = player.Snapshot.Value;
            var input = new PlayerInputFrame { Fire = true, FireSequence = before.ConsumedFire + 1, Revision = before.Revision,
                HeroRevision = before.HeroRevision, Look = Vector2.zero, Move = Vector2.right };
            ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(player)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(player, input);
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(player, player.NetworkManager.ServerTime.Time);
            double now = player.NetworkManager.ServerTime.Time;
            for (int tick = 0; tick < 4; tick++) player.Simulate(1f / 60, now + tick / 60.0, match.State.Value.Phase);
            Assert.That(player.Snapshot.Value.ShotSequence, Is.GreaterThan(before.ShotSequence));
            Assert.That(player.Snapshot.Value.Position.x, Is.GreaterThan(before.Position.x));
            player.CharacterView.Shot(0); yield return null;
            Assert.That(player.CharacterView.CameraKick.sqrMagnitude, Is.GreaterThan(0));
            var current = player.PresentedState; current.Yaw = player.Look.x; current.Pitch = player.Look.y;
            var expectedAim = solver.Resolve(player, current, current.NextMuzzle);
            Assert.That(Vector2.Distance(player.ReticleViewport, TpsAimSolver.ReticleViewport(Camera.main, expectedAim.AimPoint)), Is.LessThan(.0001));
            Assert.That(Vector2.Distance(player.ReticleViewport, Vector2.one * .5f), Is.GreaterThan(.00001));
            report.Add("Moving real input fires; camera kick retained; live reticle follows logical target projection");
            var blocker = Box("Muzzle blocker", expectedAim.Muzzle, Vector3.one * .1f);
            yield return null; yield return null;
            Assert.That(player.MuzzleBlocked, Is.True, "Live HUD obstruction state");
            report.Add("Embedded muzzle updates live HUD obstruction state");
            Capture(Camera.main, "rifle-moving-obstruction");
            Object.Destroy(blocker); Object.Destroy(platform);
            yield return null;
            SetPose(player, PrototypeArena.Spawn(player.Snapshot.Value.Team, player.Snapshot.Value.Slot));
            var paintState = player.Snapshot.Value; paintState.Pitch = 55; paintState.CurrentSpread = .000001f;
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
            match.Projectiles.Spawn(player, paintState, player.NetworkManager.ServerTime.Time, match.State.Value.Round);
            uint paintBeforeFlight = match.PaintSequence;
            yield return Wait(() => match.PaintSequence > paintBeforeFlight, "Corrected flight paints authored arena surface", 3);
            report.Add("Corrected real shot produces authoritative paint on authored TrainingGround surface");
            File.WriteAllLines(Output + "/trajectories.csv", csv);
            File.WriteAllLines(Output + "/results.txt", report.Append("Total visible changed pixels: " + visiblePixels));
            yield return app.Leave().ToCoroutine();
        }
    }
}
#endif
