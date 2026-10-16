#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Splatoon.Networking;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class WeaponPresentationPlayTests
    {
        static readonly string Output = "Reports/InkFlightReference/WeaponPlayMode/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        static IEnumerator Wait(Func<bool> condition, string message, double seconds = 20)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(condition(), Is.True, message);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator FlightInkAndShotgunGripInRealHost()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }

        static IEnumerator Scenario()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null), "GPU required");
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Addressables bootstrap");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host player and ink presentation");
            var player = PrototypePlayer.Local;
            var match = PrototypeMatch.Current;
            var flightPresentation = InkPresentation.Current;
            var staleCamera = new GameObject("Retired projectile camera").AddComponent<Camera>();
            staleCamera.enabled = false;
            var cameraField = typeof(InkPresentation).GetField("_camera", BindingFlags.Instance | BindingFlags.NonPublic);
            cameraField.SetValue(flightPresentation, staleCamera);
            yield return null;
            Assert.That(cameraField.GetValue(flightPresentation), Is.SameAs(Camera.main), "Automatic LateUpdate must replace a disabled, non-null camera");
            UnityEngine.Object.Destroy(staleCamera.gameObject);
            var camera = new GameObject("Weapon repair capture").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1; camera.fieldOfView = 35;
            var report = new List<string>();
            // An editor pause / shader hitch must not make newly fired projectiles
            // appear older than their lifetime to the presentation clock.
            System.Threading.Thread.Sleep(2000);
            yield return new WaitForFixedUpdate();
            yield return null;
            var live = player.Snapshot.Value;
            live.Position = new Vector3(0, 5, 0); live.Pitch = live.Yaw = 0;
            double clockAge = player.NetworkManager.ServerTime.Time - live.SimulatedAt;
            match.Projectiles.Spawn(player, live, live.SimulatedAt, match.State.Value.Round);
            File.WriteAllText(Output + "/clock.txt", "new shot age at spawn=" + clockAge.ToString("F6") + "s\n");
            Assert.That(clockAge, Is.InRange(-.04, .05), "Simulation and presentation clocks after hitch");
            yield return Wait(() => flightPresentation.BlockCubeCount > 0, "Newly fired cubes visible after an editor hitch; initial age=" + clockAge, 2);
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
            foreach (int hero in Enumerable.Range(1, 9))
            {
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Select hero " + hero);
                // Observe the live Animator after normal Update/LateUpdate, including
                // the same downward aim seen in the reported shotgun screenshot.
                if (hero == 3)
                {
                    Assert.That(player.CharacterView.Animator.avatar.humanDescription.human.Single(b => b.humanName == "Hips").boneName, Is.EqualTo("pelvis"));
                    foreach (float pitch in new[] { 0f, 35f, -45f })
                    {
                        for (int i = 0; i < 15; i++)
                        {
                            var s = player.Snapshot.Value; s.Pitch = pitch; s.Yaw = s.BodyYaw = 0; player.Snapshot.Value = s;
                            yield return null;
                        }
                        var view = player.CharacterView;
                        Assert.That(Vector3.Distance(view.Animator.GetBoneTransform(HumanBodyBones.LeftHand).position, view.LeftGrip.position), Is.LessThan(.045f));
                        camera.transform.position = player.transform.position + new Vector3(2.4f, 1.4f, 1.6f);
                        camera.transform.LookAt(player.transform.position + new Vector3(0, 1.15f, .3f));
                        Capture(camera, "shotgun-aim-" + pitch);
                    }
                    // Hero 3 is the live one-projectile Explosher. Exercise actual
                    // authored startup/payment and authority Spawn, preserving its grip.
                    using var paid = new BlockDefaultContractFixture(match);
                    var token = paid.PayMain(player, 3); var beforeShot = player.Snapshot.Value;
                    Assert.That(WeaponSimulation.IsExplosher(GameplayConfig.GetWeapon(3)), Is.True);
                    Assert.That(beforeShot.Ink, Is.EqualTo(100 - 11.7f).Within(.001));
                    Assert.That(token.BuildLimit, Is.Zero);
                    match.Projectiles.Clear(); flightPresentation.Clear();
                    match.Projectiles.Spawn(player, beforeShot, paid.Now, match.State.Value.Round);
                    Assert.That(match.Projectiles.Spawned.Count, Is.EqualTo(1), "Paid live Explosher emits one immutable projectile");
                    Assert.That(match.Projectiles.Spawned[0].BlockAttack, Is.SameAs(token), "Spawn shares the paid attack budget");
                    player.CharacterView.Present(beforeShot, 1f / 60, paid.Now);
                    player.CharacterView.Shot(0, beforeShot.ShotActionId, paid.Now);
                    player.CharacterView.Animator.Update(1f / 60);
                    // A paid single shot fades in over the authored presentation interval.
                    // Complete that finite 60 Hz sample before evaluating the shot pose.
                    const float shotDt = 1f / 60;
                    float shotFade = Mathf.Min(player.CharacterView.Profile.BlendSeconds,
                        Mathf.Max(.01f, player.CharacterView.Profile.ShotPlaybackSeconds) * .1f);
                    int shotFrames = Mathf.CeilToInt(shotFade / shotDt);
                    for (int frame = 1; frame < shotFrames; frame++)
                    {
                        player.CharacterView.Present(beforeShot, shotDt, paid.Now + frame * shotDt);
                        player.CharacterView.Animator.Update(shotDt);
                    }
                    Assert.That(player.CharacterView.Animator.GetLayerWeight(1), Is.GreaterThan(.5f));
                    Capture(camera, "explosher-moving-shot");
                    paid.FlushEvents();
                }
                foreach (byte team in new byte[] { 1, 2 })
                {
                    match.Projectiles.Clear(); flightPresentation.Clear();
                    // Allow the previous frame's Graphics.DrawMeshInstanced submissions
                    // to retire, then capture the control before submitting this sample.
                    yield return null;
                    var w = GameplayConfig.GetWeapon(hero); double born = player.NetworkManager.ServerTime.Time + .01;
                    double age = .03; double delay = WeaponSimulation.UsesBubbleMesh(w) ? BubbleFlightPresentation.RenderDelay : 0;
                    var shots = new List<InkShot>();
                    var origin = new Vector3(0, 12, -20);
                    for (byte pellet = 0; pellet < w.PelletCount; pellet++)
                    {
                        var shot = new InkShot { Id = (uint)(1000 + hero * 20 + team * 8 + pellet),
                            HeroId = hero, Team = team, Shooter = ulong.MaxValue, Round = match.State.Value.Round,
                            ActionId = (ulong)(100 + hero), PelletIndex = pellet, Seed = (uint)(100 + pellet), Born = born,
                            Origin = origin, Velocity = w.PelletCount > 1 ? InkBallistics.PelletVelocity(Vector3.forward, w, w.SpreadDegrees, pellet, 123) :
                                WeaponSimulation.IsExplosher(w) ? ExplosherSimulation.Launch(Vector3.forward, w, true) : Vector3.forward * w.SpeedMin,
                            Configuration = w, ConfigurationRevision = WeaponConfigService.Current.Revision(hero) };
                        shots.Add(shot);
                    }
                    var center = shots.Select(shot => InkBallistics.Position(shot, w, age)).Aggregate(Vector3.zero, (sum, point) => sum + point) / shots.Count;
                    camera.transform.position = center + new Vector3(1.2f, .5f, -2); camera.transform.LookAt(center);
                    string label = "hero-" + hero + "-team-" + team;
                    var hidden = Capture(camera, label + "-control");
                    // Keep the same host shot RPC used by a live authoritative service.
                    using (var rpc = new BlockDefaultContractFixture(match))
                    {
                        foreach (var shot in shots) match.Projectiles.SpawnForMeasurement(shot);
                        rpc.FlushEvents();
                    }
                    var flight = BlockDefaultContractFixture.Flights(flightPresentation);
                    flightPresentation.UpdateFlights(born + age + delay, camera);
                    int perShot = hero == 3 ? 4 : 1;
                    var matrices = BlockDefaultContractFixture.RenderedInstances(flightPresentation, team, shots.Count * perShot);
                    Assert.That(flightPresentation.ParticleCount + flightPresentation.VersionPoolCount, Is.Zero);
                    Assert.That(flightPresentation.Streams, Is.Empty);
                    Assert.That(flight.CubeMesh.vertexCount, Is.EqualTo(24));
                    for (int i = 0; i < shots.Count; i++)
                    {
                        Assert.That(flight.TryPosition(shots[i].Round, shots[i].Id, born + age, out var actual), Is.True);
                        Assert.That(Vector3.Distance(actual, InkBallistics.Position(shots[i], w, age)), Is.LessThan(.001), "Actual cube trajectory retains the immutable shot physics");
                        Assert.That(matrices.Skip(i * perShot).Take(perShot).All(m => m.GetColumn(0).magnitude > .01f && m.GetColumn(1).magnitude > .01f && m.GetColumn(2).magnitude > .01f), Is.True);
                    }
                    var visible = Capture(camera, label);
                    int changed = visible.Zip(hidden, (a, b) => Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b)).Count(d => d > 30);
                    Assert.That(changed, Is.GreaterThan(25), label + " must contribute visible opaque cube pixels through the real camera");
                    report.Add(label + ": cubes=" + matrices.Length + ", visiblePixels=" + changed + ", host RPC + GPU PASS");
                }
            }
            player.RequestHeroChange(3, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 3, "Return to shotgun");
            var state = player.Snapshot.Value; state.ProtectedUntil = 0; state.Swimming = true; player.Snapshot.Value = state;
            uint life = state.Revision;
            player.ReceiveDamage((byte)(state.Team == 1 ? 2 : 1), 200, Vector3.forward);
            Assert.That(player.Snapshot.Value.LifeState, Is.EqualTo(PlayerLifeState.Bubble));
            Assert.That(player.Snapshot.Value.Movement, Is.EqualTo(MovementMode.Bubble));
            yield return Wait(() => player.Snapshot.Value.Revision > life && player.Snapshot.Value.Health > 0,
                "Natural bubble expiration and Explosher respawn", GameplayConfig.Mode.BubbleSeconds + GameplayConfig.Mode.RespawnSeconds + 5);
            yield return null;
            Assert.That(player.CharacterView.Weapon.parent, Is.SameAs(player.CharacterView.WeaponSocket));
            Assert.That(player.CharacterView.Animator.GetCurrentAnimatorStateInfo(0).IsName("Locomotion"), Is.True, "Respawn must evaluate its pose immediately");
            camera.transform.position = player.transform.position + new Vector3(2.4f, 1.4f, 1.6f);
            camera.transform.LookAt(player.transform.position + new Vector3(0, 1.15f, .3f));
            Capture(camera, "shotgun-respawn");
            report.Add("Explosher: live idle/down/up aim, support grip, authored paid one-projectile emission, hero switching and downed-bubble/expiration/respawn PASS");
            UnityEngine.Object.Destroy(camera.gameObject);
            yield return app.Leave().ToCoroutine();
            File.WriteAllLines(Output + "/results.txt", report);
        }

        static Color32[] Capture(Camera camera, string label)
        {
            // Reuse the project's URP capture path without taking a test assembly
            // dependency on editor-only URP tooling.
            var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor")
                .GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
            type.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { camera, label, Output });
            var texture = new Texture2D(2, 2); texture.LoadImage(File.ReadAllBytes(Output + "/" + label + ".png"));
            var pixels = texture.GetPixels32(); UnityEngine.Object.Destroy(texture); return pixels;
        }
    }
}
#endif
