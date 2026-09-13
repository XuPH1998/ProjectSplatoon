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
        const string Output = "Logs/WeaponPresentationRepair/PlayMode";
        static IEnumerator Wait(Func<bool> condition, string message, double seconds = 20)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(condition(), Is.True, message);
        }

        [UnityTest]
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
            var streams = (ParticleSystem[])typeof(InkPresentation).GetField("_streams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(InkPresentation.Current);
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
            yield return Wait(() => streams[live.Team-1].particleCount > 0, "Newly fired ink visible after an editor hitch; initial age=" + clockAge, 2);
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
            foreach (int hero in new[] { 1, 2, 3, 4, 5 })
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
                    var beforeShot = player.Snapshot.Value;
                    var input = new PlayerInputFrame { Fire = true, FireSequence = beforeShot.ConsumedFire + 1,
                        Revision = beforeShot.Revision, HeroRevision = beforeShot.HeroRevision,
                        Look = new Vector2(0, 15), Move = Vector2.right };
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", flags).GetValue(player)).Clear();
                    typeof(PrototypePlayer).GetField("_lastInput", flags).SetValue(player, input);
                    typeof(PrototypePlayer).GetField("_lastReceivedAt", flags).SetValue(player, player.NetworkManager.ServerTime.Time);
                    match.Projectiles.Clear(); InkPresentation.Current.Clear();
                    double shotTime = player.NetworkManager.ServerTime.Time;
                    for (int tick = 0; tick < 4; tick++) player.Simulate(1f/60, shotTime + tick/60.0, match.State.Value.Phase);
                    Assert.That(player.Snapshot.Value.ShotSequence, Is.EqualTo(beforeShot.ShotSequence + 1));
                    Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(beforeShot.Ink - 4).Within(.001f));
                    Assert.That(match.Projectiles.Spawned.Count, Is.EqualTo(8), "Live shotgun emits eight pellets through authority");
                    yield return null; yield return null;
                    Assert.That(player.CharacterView.Animator.GetLayerWeight(1), Is.GreaterThan(.5f));
                    Capture(camera, "shotgun-moving-shot");
                }
                foreach (byte team in new byte[] { 1, 2 })
                {
                    match.Projectiles.Clear(); InkPresentation.Current.Clear();
                    double born = player.NetworkManager.ServerTime.Time;
                    // Put the flight in clear air, keeping actual configured speed,
                    // gravity, lifetime, the host RPC path, and team material.
                    Vector3 origin = new Vector3(0, 5, 0);
                    var w = GameplayConfig.GetHero(hero);
                    for (byte pellet = 0; pellet < w.PelletCount; pellet++)
                        match.Projectiles.SpawnForMeasurement(new InkShot { Id = (uint)(1000 + hero * 20 + team * 8 + pellet),
                            HeroId = hero, Team = team, Shooter = ulong.MaxValue, Round = match.State.Value.Round,
                            ActionId = (ulong)(100 + hero), PelletIndex = pellet, Seed = (uint)(100 + pellet), Born = born,
                            Origin = origin, Velocity = (hero == 3 ? InkBallistics.PelletVelocity(Vector3.forward, w, w.SpreadDegrees, pellet, 123) : Vector3.forward * w.SpeedMin) });
                    var stream = streams[team - 1];
                    yield return Wait(() => stream.particleCount > 0, "Live flying particles hero=" + hero + " team=" + team, 2);
                    yield return null;
                    var particles = new ParticleSystem.Particle[1024]; int count = stream.GetParticles(particles);
                    Assert.That(count, Is.GreaterThanOrEqualTo(w.PelletCount));
                    Vector3 center = particles.Take(count).Aggregate(Vector3.zero, (sum, p) => sum + p.position) / count;
                    camera.transform.position = center + new Vector3(2, .7f, -2); camera.transform.LookAt(center);
                    var renderer = stream.GetComponent<ParticleSystemRenderer>();
                    var mesh = new Mesh(); renderer.BakeMesh(mesh, camera, ParticleSystemBakeMeshOptions.Default);
                    Assert.That(mesh.bounds.size.sqrMagnitude, Is.GreaterThan(.001f), "Noncollapsed flight geometry");
                    UnityEngine.Object.Destroy(mesh);
                    string label = "hero-" + hero + "-team-" + team;
                    var visible = Capture(camera, label);
                    renderer.enabled = false; var hidden = Capture(camera, label + "-control"); renderer.enabled = true;
                    int changed = visible.Zip(hidden, (a, b) => Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b)).Count(d => d > 30);
                    Assert.That(changed, Is.GreaterThan(25), label + " must contribute visible pixels on camera");
                    report.Add(label + ": particles=" + count + ", visiblePixels=" + changed + ", host RPC + GPU PASS");
                }
            }
            player.RequestHeroChange(3, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 3, "Return to shotgun");
            var state = player.Snapshot.Value; state.ProtectedUntil = 0; state.Swimming = true; player.Snapshot.Value = state;
            uint life = state.Revision;
            player.ReceiveDamage((byte)(state.Team == 1 ? 2 : 1), 200, Vector3.forward);
            yield return Wait(() => player.Snapshot.Value.Revision > life && player.Snapshot.Value.Health > 0, "Shotgun timed respawn", 5);
            yield return null;
            Assert.That(player.CharacterView.Weapon.parent, Is.SameAs(player.CharacterView.WeaponSocket));
            Assert.That(player.CharacterView.Animator.GetCurrentAnimatorStateInfo(0).IsName("Locomotion"), Is.True, "Respawn must evaluate its pose immediately");
            camera.transform.position = player.transform.position + new Vector3(2.4f, 1.4f, 1.6f);
            camera.transform.LookAt(player.transform.position + new Vector3(0, 1.15f, .3f));
            Capture(camera, "shotgun-respawn");
            report.Add("Shotgun: live idle/down/up aim, support grip, hero switching and swim-death-respawn PASS");
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
