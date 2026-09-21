#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class AimBallisticsPlayTests
    {
        const string Output = "Reports/AimBallistics/PlayMode";
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly List<GameObject> Fixtures = new();
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnityTest] public IEnumerator HostRendersAllSixGuidesFiresAndFreezesHotReloadedShots()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode(); yield return Scenario(); yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            foreach (var go in Fixtures) if (go != null) Object.Destroy(go); Fixtures.Clear();
            typeof(CameraReticleCapture).GetMethod("RestoreGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "/status.txt", "RUNNING: Editor Host acceptance");
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && Camera.main != null, "Host player and camera");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            var resize = (UniTask)typeof(CameraReticleCapture).GetMethod("Resize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { view, 1280, 720 });
            yield return resize.ToCoroutine();
            Vector3 feet = (Vector3)typeof(CameraReticleCapture).GetMethod("FindClearFloor", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            var rows = new List<string> { "hero,case,mainX,mainY,directionX,directionY,blocked,duplicateImpact,paintCount,actualShots" };
            var solver = new TpsAimSolver();
            foreach (int hero in AimBallisticsTests.Heroes)
            {
                match.enabled = true; player.enabled = true; player.GetComponent<CharacterController>().enabled = true;
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero &&
                    player.CharacterView.Profile.name == AimBallisticsTests.Names[hero] + "Presentation", "Hero " + hero);
                match.enabled = false; player.enabled = false;
                var current = GameplayConfig.GetWeapon(hero);
                foreach (string label in new[] { "before", "standing", "up30", "down30", "forward", "backward", "near-wall", "camera-wall" })
                {
                    var w = label == "before" ? AimBallisticsTests.LegacySnapshot(hero) : current;
                    WeaponConfigService.Current.SetForEditor(hero, w);
                    var state = player.Snapshot.Value; state.Position = feet; state.Yaw = state.BodyYaw = 0;
                    state.Pitch = label == "up30" ? -30 : label == "down30" ? 30 : 0;
                    state.Health = state.Ink = 100; state.Grounded = true; state.Swimming = state.CompactBody = false;
                    state.AirHumanOffset = state.CameraRebaseOffset = 0; state.PaperPose = PaperPose.None; state.Movement = MovementMode.Human;
                    state.Firing = false; state.WeaponPhase = WeaponPhase.Idle; state.SplatlingRemaining = 0;
                    state.PlanarVelocity = Vector3.forward * (label == "forward" ? 3 : label == "backward" ? -3 : 0);
                    state.VerticalSpeed = 0; state.NextMuzzle = state.LastShotMuzzle = (byte)(hero == 2 && label == "down30" ? 1 : 0);
                    state.CurrentSpread = w.SpreadDegrees; state.CurrentVerticalSpread = hero == 6 ? w.SplatlingPitchSpread : w.SpreadDegrees;
                    player.GetComponent<CharacterController>().enabled = false; player.transform.position = feet;
                    player.Snapshot.Value = state; player.SwimBody.ApplyCollision(state);
                    typeof(PrototypePlayer).GetField("_look", Private).SetValue(player, new Vector2(0, state.Pitch));
                    Physics.SyncTransforms();
                    GameObject wall = null;
                    if (label.EndsWith("wall"))
                    {
                        var aim = solver.Resolve(player, state, state.NextMuzzle);
                        wall = GameObject.CreatePrimitive(PrimitiveType.Cube); Fixtures.Add(wall); wall.name = "Aim acceptance " + label;
                        wall.transform.position = label == "near-wall" ? aim.Muzzle : (aim.CameraOrigin + aim.Pivot) * .5f;
                        wall.transform.localScale = label == "near-wall" ? Vector3.one * .5f : new Vector3(3, 3, .15f);
                        Physics.SyncTransforms();
                    }
                    app.CaptureMouse(true);
                    typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null); player.CharacterView.Animator.Update(.1f);
                    var resolved = solver.Resolve(player, state, state.NextMuzzle);
                    if (label != "before")
                    {
                        var prediction = WeaponImpactPrediction.Guide(solver, resolved, w, state, player.PlayerId);
                        Assert.That(Vector2.Distance(player.ReticleViewport, TpsAimSolver.ReticleViewport(Camera.main, prediction.Point)), Is.LessThan(.00001));
                        Assert.That(player.ImpactReticleVisible, Is.False);
                        Assert.That(player.GuideSpreadHalfSize.x + player.GuideSpreadHalfSize.y, Is.GreaterThan(0));
                        if (label == "near-wall")
                        {
                            Assert.That(player.MuzzleBlocked, Is.True);
                            Assert.That(player.GuideSpreadHalfSize, Is.EqualTo(Vector2.one * 6),
                                "An immediate obstruction must not turn muzzle/contact separation into angular spread");
                        }
                    }
                    yield return null; yield return null;
                    string image = Path.GetFullPath($"{Output}/hero-{hero}-{label}.png"); if (File.Exists(image)) File.Delete(image);
                    ScreenCapture.CaptureScreenshot(image); yield return Wait(() => File.Exists(image), "HUD image " + label);
                    rows.Add(FormattableString.Invariant($"{hero},{label},{player.ReticleViewport.x:R},{player.ReticleViewport.y:R},{player.DirectionReticleViewport.x:R},{player.DirectionReticleViewport.y:R},{player.MuzzleBlocked},{player.ImpactReticleVisible},0,0"));
                    if (wall != null) { Object.Destroy(wall); yield return null; Physics.SyncTransforms(); }
                }
                WeaponConfigService.Current.SetForEditor(hero, current);
                var shotState = player.Snapshot.Value; shotState.Pitch = 35; shotState.PlanarVelocity = Vector3.zero;
                shotState.LastShotCharge = 1; shotState.LastShotSpread = shotState.LastShotVerticalSpread = 0;
                shotState.ShotSequence = 1; shotState.BurstShotIndex = hero == 8 ? 5u : 1u;
                int paints = 0; var service = new InkProjectileService { PaintObserved = _ => paints++ };
                double born = match.NetworkManager.ServerTime.Time; service.Spawn(player, shotState, born, match.State.Value.Round); service.Simulate(born + current.Lifetime + 5);
                Assert.That(service.Spawned.Count, Is.EqualTo(1)); Assert.That(service.Impacts.Count, Is.GreaterThan(0)); Assert.That(paints, Is.GreaterThan(0));
                rows.Add($"{hero},host-fire,0,0,0,0,false,false,{paints},{service.Spawned.Count}");
            }
            var original = GameplayConfig.GetWeapon(1); var clone = Object.Instantiate(WeaponConfigService.Current.Source(1));
            try
            {
                WeaponConfigService.Current.SetForEditor(1, original, clone);
                var aim = WeaponLaunch.Geometry(new Vector3(1000, 1000, 995), Vector3.forward, new Vector3(1000, 1000, 1000), original, 1);
                var shot = WeaponLaunch.Representative(aim, original, 1); shot.HeroId = 1; shot.Id = 99001; shot.ActionId = 99001;
                shot.Born = match.NetworkManager.ServerTime.Time; shot.Round = match.State.Value.Round; shot.ConfigurationRevision = WeaponConfigService.Current.Revision(1);
                var service = new InkProjectileService(); service.SpawnForMeasurement(shot); service.Simulate(shot.Born + .03);
                uint revision = WeaponConfigService.Current.Revision(1); clone.shotGuideSeconds += 1.0 / 60; clone.shooterMoveForwardRate += 1;
                double deadline = Time.realtimeSinceStartupAsDouble + 30;
                while (WeaponConfigService.Current.Revision(1) == revision && Time.realtimeSinceStartupAsDouble < deadline)
                { app.ApplyDebugWeaponChanges(); yield return null; }
                Assert.That(WeaponConfigService.Current.Revision(1), Is.GreaterThan(revision), app.WeaponDebugStatus);
                Assert.That(service.LiveShots().Single().Configuration, Is.SameAs(original));
                Assert.That(GameplayConfig.GetWeapon(1).ShotGuideSeconds, Is.GreaterThan(original.ShotGuideSeconds));
            }
            finally { WeaponConfigService.Current.SetForEditor(1, original); Object.Destroy(clone); }
            var mismatch = (byte[])app.Manager.NetworkConfig.ConnectionData.Clone(); mismatch[0] ^= 1;
            var response = new Unity.Netcode.NetworkManager.ConnectionApprovalResponse();
            app.Manager.ConnectionApprovalCallback(new Unity.Netcode.NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 900001, Payload = mismatch }, response);
            Assert.That(response.Approved, Is.False);
            File.WriteAllLines(Output + "/observations.csv", rows);
            typeof(CameraReticleCapture).GetMethod("RestoreGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            yield return app.Leave().ToCoroutine();
            Fixtures.Clear();
            File.WriteAllText(Output + "/status.txt", "PASS: Editor Host, six weapons, live LateUpdate/HUD screenshots, real projectile/paint paths, hot reload freezes in-flight config, incompatible content rejected. Not a separate client or physical LAN test; final test outcome is in playmode.xml.");
        }
    }
}
#endif
