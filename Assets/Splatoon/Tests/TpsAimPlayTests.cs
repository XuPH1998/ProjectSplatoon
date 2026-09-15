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
        const string Output = "Reports/TpsConvergence/PlayMode";
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
        static void FlushPresentation(PrototypeMatch match)
        {
            typeof(PrototypeMatch).GetMethod("ServerTick", Private).Invoke(match, null);
            typeof(InkPresentation).GetMethod("LateUpdate", Private).Invoke(InkPresentation.Current, null);
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
            var flightPresentation = InkPresentation.Current.Flight;
            int visiblePixels = 0;
            foreach (int hero in new[] { 1, 2, 3, 4, 5 })
            {
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Switch hero " + hero);
                SetPose(player, feet); yield return null;
                foreach (float distance in new[] { 5f, 12f })
                {
                    SetPose(player, feet);
                    var state = player.Snapshot.Value; state.CurrentSpread=state.LastShotSpread=.000001f;state.LastShotVerticalSpread=.000001f; state.LastShotCharge = 1;
                    var aim = solver.Resolve(player, state, state.LastShotMuzzle);
                    var target = Box("TPS target", aim.CameraOrigin + aim.Forward * distance, new Vector3(3, 3, .08f));
                    var impacts = new List<InkImpact>();
                    var lastTrace = new Dictionary<uint, string>();
                    match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.Flight.Clear();
                    double born = player.NetworkManager.ServerTime.Time;
                    match.Projectiles.Spawn(player, state, born, match.State.Value.Round);
                    var shots = match.Projectiles.Spawned.ToArray();
                    var resolved = solver.Resolve(player, state, state.LastShotMuzzle);
                    Debug.Log($"[TPS-CONVERGENCE] hero={hero} distance={distance} muzzle={resolved.Muzzle:F4} blocked={resolved.MuzzleBlocked} aim={resolved.AimPoint:F4} target={target.transform.position:F4} immediateImpacts={match.Projectiles.Impacts.Count}");
                    Assert.That(shots.Length, Is.EqualTo(GameplayConfig.GetWeapon(hero).PelletCount));
                    Assert.That(shots.All(s => s.PostCorrectionVelocity == s.Velocity), Is.True, "No projectile turns after convergence");
                    var expectedTarget = distance <= 6 ? resolved.CameraOrigin + resolved.Forward * 6 : resolved.AimHit.Point;
                    Assert.That(Vector3.Distance(resolved.CorrectionPoint, expectedTarget), Is.LessThan(.0001f));
                    Assert.That(Vector3.Angle(shots[0].Velocity, expectedTarget - resolved.Muzzle), Is.LessThan(.03f));
                    match.Projectiles.TraceObserved = (shot, age, point) =>
                    {
                        lastTrace[shot.Id] = $"id={shot.Id} age={age:F4} point={point:F4}";
                        // Record the authority's actual terminal target position before ServerTick clears its events.
                        if (Mathf.Abs(point.z - (target.transform.position.z - .04f)) < .02f)
                            impacts.Add(new InkImpact { Position = point, Id = shot.Id });
                    };
                    double hitDeadline = Time.realtimeSinceStartupAsDouble + 3;
                    while (impacts.Count < shots.Length && Time.realtimeSinceStartupAsDouble < hitDeadline) yield return null;
                    Assert.That(impacts.Count, Is.GreaterThanOrEqualTo(shots.Length), $"Authoritative target hit hero={hero} distance={distance}; last traces: {string.Join("; ", lastTrace.Values)}");
                    foreach (var impact in impacts)
                    {
                        var shot = shots.Single(s => s.Id == impact.Id);
                        var weapon = GameplayConfig.GetWeapon(hero);
                        float contactZ = target.transform.position.z - .04f;
                        // Solve contact with the front plane, including the projectile radius and gravity.
                        double low = 0, high = weapon.Lifetime;
                        for (int i = 0; i < 40; i++)
                        {
                            double mid = (low + high) * .5;
                            if (InkBallistics.Position(shot, weapon, mid).z < contactZ - weapon.CollisionRadius) low = mid;
                            else high = mid;
                        }
                        Vector3 expected = InkBallistics.Position(shot, weapon, (low + high) * .5) + Vector3.forward * weapon.CollisionRadius;
                        Assert.That(Vector3.Distance(impact.Position, expected), Is.LessThan(.003f), "Authority contact follows falling trajectory, not reticle height");
                    }
                    report.Add($"Hero {hero}: camera target {distance:F0}m, {shots.Length} authoritative projectiles hit; single launch direction verified");
                    match.Projectiles.TraceObserved = null;
                    if (hero == 1) Capture(Camera.main, distance <= 6 ? "rifle-near-target" : "rifle-far-target");
                    // EnterPlayMode tests may resume before deferred Destroy is processed.
                    // Remove the old target from physics before resolving the next shot.
                    target.SetActive(false); Physics.SyncTransforms(); Object.Destroy(target); yield return null;
                }
                // Show real RPC-delivered particles in clear air, at configured speed/spread and gravity.
                SetPose(player, feet + Vector3.up * 4);
                var live = player.Snapshot.Value; live.LastShotCharge = 1;
                var miss = solver.Resolve(player, live, live.LastShotMuzzle);
                Assert.That(miss.AimHit.Collider, Is.Null, "Clear-air case has no hit within 100 metres");
                Assert.That(Vector3.Distance(miss.CorrectionPoint, miss.CameraOrigin + miss.Forward * 50), Is.LessThan(.0001f));
                match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.Flight.Clear();
                match.Projectiles.Spawn(player, live, player.NetworkManager.ServerTime.Time - .05, match.State.Value.Round);
                var flight = match.Projectiles.Spawned[0]; var w = GameplayConfig.GetWeapon(hero);
                // GPU captures can take longer than a projectile's lifetime on an importing editor.
                // Sample a fixed live age without yielding between host RPC dispatch and capture.
                FlushPresentation(match);
                var particles = new ParticleSystem.Particle[2048]; int count = flightPresentation.CopyParticles(live.Team, particles);
                var stream = flightPresentation.Streams.First(p => p.particleCount > 0);
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
                // Compare the previous six-metre bend with the new fixed launch direction.
                var solution = solver.Resolve(player, live, live.LastShotMuzzle);
                var centerShot = flight; centerShot.Velocity = solution.InitialDirection * flight.Velocity.magnitude;
                InkBallistics.ApplyCorrection(ref centerShot, solution, w);
                var priorShot = centerShot;
                Vector3 oldDelta = solution.CameraOrigin + solution.Forward * 6 - solution.Muzzle;
                priorShot.Velocity = oldDelta.normalized * centerShot.Velocity.magnitude;
                priorShot.FirstSegmentLength = oldDelta.magnitude;
                priorShot.PostCorrectionVelocity = solution.Forward * centerShot.Velocity.magnitude;
                for (int i = 0; i <= 120; i++)
                {
                    double age = i / 120.0;
                    var oldPoint = InkBallistics.Position(priorShot, w, age) - feet;
                    var newPoint = InkBallistics.Position(centerShot, w, age) - feet;
                    csv.Add(string.Join(",", new[] { hero.ToString(), age.ToString("F6", CultureInfo.InvariantCulture), oldPoint.x.ToString("F6", CultureInfo.InvariantCulture), oldPoint.y.ToString("F6", CultureInfo.InvariantCulture), oldPoint.z.ToString("F6", CultureInfo.InvariantCulture), newPoint.x.ToString("F6", CultureInfo.InvariantCulture), newPoint.y.ToString("F6", CultureInfo.InvariantCulture), newPoint.z.ToString("F6", CultureInfo.InvariantCulture) }));
                }
                report.Add($"Hero {hero}: host RPC + GPU particles={count}, visiblePixels={changed}, fallStart={flight.GravityStartAge:F5}s");
            }
            // Sample before a six-metre convergence point, after the weapon straight period.
            // Flush the real host RPC and render in this frame so clock scheduling cannot skip the interval.
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.Flight.Clear();
            var gravityWeapon = GameplayConfig.GetWeapon(3);
            var gravityAim = TpsAimSolver.Geometry(Vector3.up * 20, Vector3.forward, new Vector3(-.6f, 20, 0), 6, 6, float.PositiveInfinity);
            var gravityShot = new InkShot { Id = uint.MaxValue, HeroId = 3, Team = player.Snapshot.Value.Team,
                Shooter = ulong.MaxValue, Round = match.State.Value.Round, Seed = 71, Origin = gravityAim.Muzzle,
                Velocity = gravityAim.InitialDirection * gravityWeapon.SpeedMin };
            InkBallistics.ApplyCorrection(ref gravityShot, gravityAim, gravityWeapon);
            double convergenceAge = InkBallistics.CorrectionAge(gravityShot, gravityWeapon);
            double gravityAge = (gravityShot.GravityStartAge + convergenceAge) * .5;
            Assert.That(gravityAge, Is.GreaterThan(gravityShot.GravityStartAge).And.LessThan(convergenceAge));
            gravityShot.Born = player.NetworkManager.ServerTime.Time - gravityAge;
            match.Projectiles.SpawnForMeasurement(gravityShot);
            FlushPresentation(match);
            var gravityStream = flightPresentation;
            var gravityParticles = new ParticleSystem.Particle[32];
            Assert.That(gravityStream.CopyParticles(gravityShot.Team, gravityParticles), Is.GreaterThan(0), "Host RPC delivered shot before convergence");
            var expectedPosition = InkBallistics.Position(gravityShot, gravityWeapon, gravityAge);
            Assert.That(Vector3.Distance(gravityParticles[0].position, expectedPosition), Is.LessThan(.0001f), "GPU stream uses complete authority trajectory");
            var noGravity = gravityShot; noGravity.GravityStartAge = 100;
            float drop = InkBallistics.Position(noGravity, gravityWeapon, gravityAge).y - gravityParticles[0].position.y;
            Assert.That(drop, Is.GreaterThan(.001f), "Actual rendered particle falls before convergence");
            var gravityCamera = new GameObject("Before-convergence gravity evidence camera").AddComponent<Camera>();
            gravityCamera.CopyFrom(Camera.main); gravityCamera.enabled = false; gravityCamera.aspect = 1;
            gravityCamera.transform.position = expectedPosition + new Vector3(.6f, .15f, -.6f); gravityCamera.transform.LookAt(expectedPosition);
            Capture(gravityCamera, "gravity-before-convergence");
            Object.Destroy(gravityCamera.gameObject);
            report.Add($"GPU particle before convergence: age={gravityAge:F6}s, targetAge={convergenceAge:F6}s, gravityStart={gravityShot.GravityStartAge:F6}s, drop={drop:F6}m; RPC and authority position agree");
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.Flight.Clear();
            player.RequestHeroChange(1, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 1, "Return to rifle");
            SetPose(player, feet);
            var before = player.Snapshot.Value;
            var input = new PlayerInputFrame { Fire = false, FireSequence = before.ConsumedFire, Revision = before.Revision,
                HeroRevision = before.HeroRevision, Look = Vector2.zero, Move = Vector2.right };
            ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(player)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(player, input);
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(player, player.NetworkManager.ServerTime.Time);
            double now = player.NetworkManager.ServerTime.Time;
            // Switching heroes requires a released input before accepting the next press.
            player.Simulate(1f / 60, now, match.State.Value.Phase);
            input.Fire = true; input.FireSequence++;
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(player, input);
            for (int tick = 1; tick <= WeaponTimeFixture.ReferenceFrames(GameplayConfig.GetWeapon(1).StartSeconds) + 3; tick++)
                player.Simulate(1f / 60, now + tick / 60.0, match.State.Value.Phase);
            Assert.That(player.Snapshot.Value.ShotSequence, Is.GreaterThan(before.ShotSequence));
            Assert.That(player.Snapshot.Value.Position.x, Is.GreaterThan(before.Position.x));
            player.CharacterView.Shot(0);
            typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
            Assert.That(player.CharacterView.CameraKick.sqrMagnitude, Is.GreaterThan(0));
            var current = player.PresentedState; current.Yaw = player.Look.x; current.Pitch = player.Look.y;
            var expectedAim = solver.Resolve(player, current, current.NextMuzzle);
            var projectedAim = TpsAimSolver.ReticleViewport(Camera.main, expectedAim.AimPoint);
            float kickOffset = Vector2.Distance(projectedAim, Vector2.one * .5f);
            Assert.That(kickOffset, Is.GreaterThan(1e-7f), "Camera kick has a measurable projection offset");
            // Test tracking relative to the actual shake, which decays with editor frame time.
            // A fixed centre reticle must fail even when the remaining kick is very small.
            Assert.That(Vector2.Distance(player.ReticleViewport, projectedAim), Is.LessThan(kickOffset * .1f));
            report.Add("Moving real input fires; camera kick retained; live reticle follows logical target projection");
            var blocker = Box("Muzzle blocker", expectedAim.Muzzle, Vector3.one * .1f);
            typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
            Assert.That(player.MuzzleBlocked, Is.True, "Live HUD obstruction state");
            report.Add("Embedded muzzle updates live HUD obstruction state");
            Capture(Camera.main, "rifle-moving-obstruction");
            blocker.SetActive(false); platform.SetActive(false); Physics.SyncTransforms();
            Object.Destroy(blocker); Object.Destroy(platform);
            yield return null;
            SetPose(player, PrototypeArena.Spawn(player.Snapshot.Value.Team, player.Snapshot.Value.Slot));
            var paintState = player.Snapshot.Value; paintState.Pitch = 55; paintState.CurrentSpread=paintState.LastShotSpread=.000001f;paintState.LastShotVerticalSpread=.000001f;
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.Flight.Clear();
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
