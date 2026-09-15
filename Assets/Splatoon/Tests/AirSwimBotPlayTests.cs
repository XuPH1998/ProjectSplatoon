#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class AirSwimBotPlayTests
    {
        const string Output = "Reports/AirSwimOutline";
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 35;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnityTest] public IEnumerator RealHostStaticBotIdentityDamageLifecycleAndOutline()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot and Addressables");
            var app = PrototypeApp.Current;
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var connect = app.Connect(true, "127.0.0.1", port);
            yield return Wait(() => !app.Busy, "Host connection"); connect.GetAwaiter().GetResult();
            var match = PrototypeMatch.Current; var host = PrototypePlayer.Local;
            Assert.That(host, Is.Not.Null); Assert.That(host.OwnerClientId, Is.Zero);
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            app.CaptureMouse(false);
            var bot = match.AddTestBot(prefab);
            Assert.That(bot, Is.Not.Null, match.TestBotMessage);
            Assert.That(bot.IsTestBot, Is.True); Assert.That(bot.OwnerClientId, Is.Zero);
            Assert.That(bot.PlayerId, Is.Not.EqualTo(host.PlayerId));
            Assert.That(PrototypePlayer.ByOwner[host.PlayerId], Is.SameAs(host));
            Assert.That(PrototypePlayer.ByOwner[bot.PlayerId], Is.SameAs(bot));
            Assert.That(PrototypePlayer.Local, Is.SameAs(host));
            Assert.That(bot.Snapshot.Value.Team, Is.EqualTo(3 - host.Snapshot.Value.Team));
            Assert.That(match.CanStartRound, Is.False, "Test bots do not fill the human start requirement");
            yield return null;
            var start = bot.Snapshot.Value;
            for (int i = 0; i < 90; i++) { bot.Simulate(1f / 60, app.Manager.ServerTime.Time, MatchPhase.Practice); yield return null; }
            Assert.That(Vector3.ProjectOnPlane(bot.Snapshot.Value.Position - start.Position, Vector3.up).magnitude, Is.LessThan(.001));
            Assert.That(bot.Snapshot.Value.ShotSequence, Is.Zero);
            Assert.That(bot.Snapshot.Value.Swimming, Is.False);
            var aim = new TpsAimSolver(); var origin = host.Snapshot.Value.Position + Vector3.up;
            var direction = bot.Snapshot.Value.Position + Vector3.up - origin;
            Assert.That(aim.ClosestCast(origin, direction, direction.magnitude + .5f, .01f, host.PlayerId, out var hit), Is.True);
            Assert.That(hit.Collider.GetComponentInParent<PrototypePlayer>(), Is.SameAs(bot), "Server ownership cannot make the bot immune to host shots");

            // Drive a fixed camera through the real URP feature, using the bound production hero renderers.
            match.enabled = host.enabled = bot.enabled = false;
            var camera = Camera.main; camera.transform.position = bot.Snapshot.Value.Position + new Vector3(0, 1.2f, -5);
            camera.transform.LookAt(bot.Snapshot.Value.Position + Vector3.up * .95f);
            app.CaptureMouse(true);
            bot.CharacterView.Present(bot.Snapshot.Value, .016f, app.Manager.ServerTime.Time);
            bot.CharacterView.Animator.Update(.2f);
            int enemyPixels = 0, friendlyPixels = 0, blockedPixels = 0;
            yield return Capture("enemy-outline", n => enemyPixels = n);
            var enemy = bot.Snapshot.Value; var friendly = enemy; friendly.Team = host.Snapshot.Value.Team;
            bot.Snapshot.Value = friendly; bot.CharacterView.Present(friendly, .016f, app.Manager.ServerTime.Time);
            yield return Capture("friendly-no-outline", n => friendlyPixels = n);
            bot.Snapshot.Value = enemy; bot.CharacterView.Present(enemy, .016f, app.Manager.ServerTime.Time);
            var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blocker.transform.position = bot.Snapshot.Value.Position + new Vector3(0, 1.1f, -1.5f);
            blocker.transform.localScale = new Vector3(3, 3, .3f);
            yield return Capture("wall-occlusion", n => blockedPixels = n);
            Object.Destroy(blocker); yield return null;
            int draws = bot.CharacterView.OutlineDraws.Where(d => d.Renderer.enabled && d.Renderer.gameObject.activeInHierarchy).Sum(d => d.Submeshes);
            File.WriteAllText(Output + "/outline-pixels.txt", $"enemy={enemyPixels}; friendly={friendlyPixels}; wall={blockedPixels}; active draws={draws}\n");
            Assert.That(enemyPixels, Is.GreaterThan(friendlyPixels + 20), "Visible enemy gets red hull pixels");
            Assert.That(blockedPixels, Is.LessThan(enemyPixels / 4), "Opaque wall hides red outline");
            var paper = enemy; paper.Swimming = true; paper.Grounded = false; paper.Movement = MovementMode.Air;
            paper.SwimSource = SwimSurface.Neutral;
            PaperPoseSimulation.Resolve(ref paper, enemy, bot.SwimBody.Profile, app.Manager.ServerTime.Time);
            bot.Snapshot.Value = paper; bot.CharacterView.Present(paper, .016f, app.Manager.ServerTime.Time);
            bot.SwimBody.ApplyCollision(paper); bot.SwimBody.Present(paper, Vector3.zero, Quaternion.identity);
            camera.transform.position = paper.PaperCenter + new Vector3(0, 3, -2);
            camera.transform.LookAt(paper.PaperCenter);
            yield return Capture("paper-enemy-outline", _ => { });
            bot.Snapshot.Value = enemy; bot.SwimBody.ApplyCollision(enemy); bot.SwimBody.Present(enemy, Vector3.zero, Quaternion.identity);
            bot.CharacterView.Present(enemy, .016f, app.Manager.ServerTime.Time);
            app.CaptureMouse(false); yield return Capture("host-bot-menu", _ => { });

            // Standard friendly-fire, death and respawn rules use the bot's separate combat identity.
            var damageState = bot.Snapshot.Value; damageState.ProtectedUntil = 0; bot.Snapshot.Value = damageState;
            bot.ReceiveDamage(damageState.Team, 20, Vector3.forward, host.PlayerId);
            Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(damageState.Health));
            bot.ReceiveDamage(host.Snapshot.Value.Team, 20, Vector3.forward, host.PlayerId);
            Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(damageState.Health - 20));
            bot.ReceiveDamage(host.Snapshot.Value.Team, 200, Vector3.forward, host.PlayerId);
            Assert.That(bot.Snapshot.Value.Health, Is.LessThanOrEqualTo(0));
            ulong botId = bot.PlayerId; uint revision = bot.Snapshot.Value.Revision;
            bot.Simulate(1f / 60, bot.Snapshot.Value.RespawnsAt + .1, MatchPhase.Practice);
            Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(GameplayConfig.GetHero(bot.Snapshot.Value.HeroId).MaxHealth));
            Assert.That(bot.Snapshot.Value.Revision, Is.GreaterThan(revision)); Assert.That(bot.PlayerId, Is.EqualTo(botId));
            for (int i = 0; i < 3; i++) Assert.That(match.AddTestBot(prefab), Is.Not.Null, match.TestBotMessage);
            Assert.That(match.TestBotCount, Is.EqualTo(4)); Assert.That(match.AddTestBot(prefab), Is.Null);
            Assert.That(PrototypePlayer.ByOwner.Count, Is.EqualTo(5));
            // Roster fixtures exercise the normal join path; they are not independent network clients.
            for (ulong id = 1; id <= 4; id++) match.AddPlayer(id, prefab);
            Assert.That(match.Players.Count, Is.EqualTo(8)); Assert.That(match.TestBotCount, Is.EqualTo(3), "A human join evicts the final occupied bot seat");
            Assert.That(match.Players.Count(p => !p.IsTestBot), Is.EqualTo(5));
            Assert.That(match.CanStartRound, Is.True);
            match.StartRound(); yield return null;
            Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Playing)); Assert.That(match.TestBotCount, Is.Zero);
            Assert.That(match.AddTestBot(prefab), Is.Null, "No test bot spawn during a match");
            var ended = match.State.Value; ended.Phase = MatchPhase.Finished; match.State.Value = ended;
            match.ReturnToRoom(); yield return null;
            Assert.That(match.AddTestBot(prefab), Is.Not.Null);
            match.ClearTestBots(); yield return null;
            Assert.That(match.TestBotCount, Is.Zero); Assert.That(PrototypePlayer.ByOwner.Count, Is.EqualTo(5));
            Assert.That(PrototypePlayer.Local, Is.SameAs(host));
            var binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var motor = (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", binding).GetValue(host);
            var airborne = host.Snapshot.Value;
            airborne.Position = new Vector3(0, 10, 0); airborne.Grounded = false; airborne.Movement = MovementMode.Air;
            airborne.Swimming = false; airborne.VerticalSpeed = -8; airborne.PlanarVelocity = Vector3.zero;
            airborne.Ink = 20; airborne.InkRecoverAt = 0; airborne.AirSwimSource = SwimSurface.Neutral;
            motor.Restore(airborne); host.Snapshot.Value = airborne;
            for (int tick = 0; tick < 60; tick++)
            {
                var input = new PlayerInputFrame { Revision = airborne.Revision, HeroRevision = airborne.HeroRevision, Swim = true, Move = Vector2.up };
                typeof(PrototypePlayer).GetField("_lastInput", binding).SetValue(host, input);
                typeof(PrototypePlayer).GetField("_lastReceivedAt", binding).SetValue(host, app.Manager.ServerTime.Time);
                host.Simulate(1f / 60, host.Snapshot.Value.SimulatedAt + 1f / 60, MatchPhase.Practice);
                Assert.That(host.Snapshot.Value.HasInkRecovery, Is.False);
            }
            var gliding = host.Snapshot.Value;
            Assert.That(gliding.Grounded, Is.False); Assert.That(gliding.VerticalSpeed, Is.EqualTo(-1.5f).Within(.001));
            Assert.That(gliding.PlanarVelocity.magnitude, Is.EqualTo(6).Within(.001));
            Assert.That(gliding.Ink, Is.EqualTo(30).Within(.001), "Air gets only normal recovery through the production authority step");
            host.CharacterView.Present(gliding, .016f, gliding.SimulatedAt);
            host.SwimBody.Present(gliding, Vector3.zero, Quaternion.identity);
            camera.transform.position = gliding.PaperCenter + new Vector3(2, 3, -4); camera.transform.LookAt(gliding.PaperCenter);
            yield return Capture("air-glide-host", _ => { });
            File.WriteAllText(Output + "/host-validation.txt", "PASS Boot/Addressables/real NGO host: independent server-owned bot identities; static/no fire/no swimming; host hit filtering; enemy/friendly damage; death and respawn; four-bot capacity; human seat priority; start clears bots; clear; enemy/team/wall GPU captures; production authority glide at 6 m/s, -1.5 m/s vertical and 10 ink/s normal recovery. Additional human roster actors are server fixtures, not independent clients.\n");
            var leave = app.Leave(); yield return Wait(() => !app.InRoom && !app.Busy, "Leave"); leave.GetAwaiter().GetResult();
        }
        static IEnumerator Capture(string name, Action<int> count)
        {
            Directory.CreateDirectory(Output);
            string path = Path.GetFullPath(Output + "/" + name + ".png");
            if (File.Exists(path)) File.Delete(path);
            bool menu = name == "host-bot-menu";
            PrototypeApp.Current.CaptureMouse(!menu);
            yield return null;
            PrototypeApp.Current.CaptureMouse(!menu);
            ScreenCapture.CaptureScreenshot(path);
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            do { PrototypeApp.Current.CaptureMouse(!menu); yield return null; }
            while (!File.Exists(path) && Time.realtimeSinceStartupAsDouble < deadline);
            Assert.That(File.Exists(path), Is.True, "Screenshot " + name);
            var texture = new Texture2D(2, 2); texture.LoadImage(File.ReadAllBytes(path));
            int red = 0;
            for (int y = texture.height / 4; y < texture.height * 3 / 4; y++)
                for (int x = texture.width / 3; x < texture.width * 2 / 3; x++)
                {
                    var pixel = texture.GetPixel(x, y);
                    if (pixel.r > .65f && pixel.g < .4f && pixel.b < .4f && pixel.r > pixel.g * 2) red++;
                }
            count(red); Object.Destroy(texture);
        }
        [UnityTearDown] public IEnumerator Cleanup() { if (Application.isPlaying) yield return new ExitPlayMode(); }
    }
}
#endif
