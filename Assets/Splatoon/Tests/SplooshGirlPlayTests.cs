#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
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
    public sealed class SplooshGirlPlayTests
    {
        const string Output = "Reports/SplooshGirl";
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 50;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnityTest] public IEnumerator HostChecksSelectionCeilingHitsPaperAndHotReload()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode(); yield return Scenario(); yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static void Place(PrototypePlayer player, Vector3 position, byte team = 1)
        {
            var s = player.Snapshot.Value; s.HeroId = 8; s.Health = s.Ink = 100; s.ProtectedUntil = 0; s.Position = position;
            s.Team = team; s.Swimming = s.CompactBody = false; s.Grounded = true; s.Movement = MovementMode.Human;
            s.PaperPose = PaperPose.None; s.AirHumanOffset = 0; s.PlanarVelocity = Vector3.zero; s.VerticalSpeed = 0;
            player.Snapshot.Value = s; new PlayerMotorSimulation(player.GetComponent<CharacterController>()).Restore(s);
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null, "Host player ready");
            Assert.That(app.Heroes.PortraitCount, Is.EqualTo(LubanConfigService.Current.Tables.TbHero.DataList.Count));
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            host.RequestHeroChange(8, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 8, "Small hero selected");
            Assert.That(host.GetComponent<CharacterController>().height, Is.EqualTo(1.5f));
            Assert.That(host.CharacterView.Animator.avatar.isValid, Is.True);
            for (int frame = 0; frame < 10; frame++) yield return null;
            Capture(host.transform.position + Vector3.up * .8f, "play-character");
            var original = host.transform.position; var bot = match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            Assert.That(bot, Is.Not.Null, match.TestBotMessage);
            app.CaptureMouse(false); match.enabled = false; foreach (var p in match.Players) p.enabled = false;
            var origin = new Vector3(1000, 1000, 1000); Place(host, origin);
            var roof = new GameObject("Small hero ceiling"); roof.transform.position = origin + Vector3.up * 1.7f;
            roof.AddComponent<BoxCollider>().size = new Vector3(3, .2f, 3); Physics.SyncTransforms();
            double Now() => host.NetworkManager.ServerTime.Time;
            try
            {
                host.RequestHeroChange(1, HeroSelectionOrigin.Warmup); host.Simulate(1f / 60, Now(), MatchPhase.Practice); host.RefreshHeroChangeStatus();
                Assert.That(host.Snapshot.Value.HeroId, Is.EqualTo(8)); Assert.That(host.HeroChangeMessage, Does.Contain("空间不足"));
                roof.SetActive(false); Physics.SyncTransforms();
                host.RequestHeroChange(1, HeroSelectionOrigin.Warmup); host.Simulate(1f / 60, Now(), MatchPhase.Practice); host.RefreshHeroChangeStatus();
                Assert.That(host.Snapshot.Value.HeroId, Is.EqualTo(1)); Assert.That(host.GetComponent<CharacterController>().height, Is.EqualTo(1.8f));
                roof.SetActive(true);var compact=host.Snapshot.Value;compact.Swimming=compact.CompactBody=true;host.Snapshot.Value=compact;
                new PlayerMotorSimulation(host.GetComponent<CharacterController>()).Restore(compact);Physics.SyncTransforms();
                host.RequestHeroChange(2, HeroSelectionOrigin.Warmup); host.Simulate(1f / 60, Now(), MatchPhase.Practice); host.RefreshHeroChangeStatus();
                Assert.That(host.Snapshot.Value.HeroId, Is.EqualTo(2), "same-size legacy switch remains available while compact");
                host.RequestHeroChange(8, HeroSelectionOrigin.Warmup); host.Simulate(1f / 60, Now(), MatchPhase.Practice); host.RefreshHeroChangeStatus();
                Assert.That(host.Snapshot.Value.HeroId, Is.EqualTo(8)); roof.SetActive(false); Place(host, origin);
                Place(bot, origin + Vector3.forward * 2, 2);
                // Exercise the same capsule used by an independent remote player.
                bot.GetComponent<CharacterController>().enabled = false; bot.SwimBody.ApplyCollision(bot.Snapshot.Value); Physics.SyncTransforms();
                var w = GameplayConfig.GetWeapon(8); uint id = 85000;
                InkProjectileService Fire(Vector3 muzzle, Vector3 direction)
                {
                    var service = new InkProjectileService(); double born = Now();
                    service.SpawnForMeasurement(new InkShot { Id = ++id, ActionId = id, Round = match.State.Value.Round, HeroId = 8, Team = 1,
                        Shooter = host.PlayerId, Seed = id, Born = born, Origin = muzzle, Velocity = direction * w.SpeedMin, Configuration = w,
                        ConfigurationRevision = WeaponConfigService.Current.Revision(8) });
                    service.Simulate(born + .2); return service;
                }
                Fire(origin + Vector3.up * 1.8f, Vector3.forward);
                Fire(origin + Vector3.up * .8f + Vector3.right * .6f, Vector3.forward);
                Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(100), "shots outside the new capsule miss");
                foreach (float height in new[] { 1.35f, .8f, .35f })
                {
                    float before = bot.Snapshot.Value.Health;
                    Fire(origin + Vector3.up * height, Vector3.forward);
                    Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(Mathf.Max(0,before-38)), "new upper body, torso and legs remain hittable");
                }
                Assert.That(bot.Snapshot.Value.Movement, Is.EqualTo(MovementMode.Dead));
                bot.SwimBody.ApplyCollision(bot.Snapshot.Value);
                Assert.That(bot.SwimBody.UsesHitProxy, Is.False);
                bot.Respawn(); Assert.That(bot.Snapshot.Value.HeroId, Is.EqualTo(8));
                Place(bot, origin + Vector3.forward * 2, 2);
                var paper = bot.Snapshot.Value; paper.Swimming = true; paper.SwimSource = SwimSurface.Friendly; paper.Movement = MovementMode.GroundInk;
                PaperPoseSimulation.Resolve(ref paper, default, bot.SwimBody.Profile, Now()); bot.Snapshot.Value = paper; bot.SwimBody.ApplyCollision(paper); Physics.SyncTransforms();
                Assert.That(bot.SwimBody.FlatHitActive, Is.True);
                var rect = bot.SwimBody.HitRects.OrderByDescending(r => r.width * r.height).First();
                var point = bot.SwimBody.HitVolume.transform.TransformPoint(new Vector3(rect.center.x, rect.center.y, 0));
                Fire(point + Vector3.up, Vector3.down); Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(62));
                Fire(point + Vector3.up, Vector3.down); Fire(point + Vector3.up, Vector3.down);
                Assert.That(bot.Snapshot.Value.Health, Is.Zero, "paper silhouette can receive a lethal hit");
                bot.SwimBody.ApplyCollision(bot.Snapshot.Value); Assert.That(bot.SwimBody.UsesHitProxy, Is.False);
                bot.Respawn(); Assert.That(bot.GetComponent<CharacterController>().height, Is.EqualTo(1.5f));
                var blocker = roof; blocker.SetActive(true); blocker.transform.position = origin + Vector3.up * .8f + Vector3.forward;
                blocker.GetComponent<BoxCollider>().size = new Vector3(3, 3, .1f); Place(bot, origin + Vector3.forward * 2, 2); Physics.SyncTransforms();
                Fire(origin + Vector3.up * .8f, Vector3.forward); Assert.That(bot.Snapshot.Value.Health, Is.EqualTo(100), "wall blocks shot");
                var solver = new TpsAimSolver(); blocker.transform.position = origin + host.Presentation.MuzzlePosition; Physics.SyncTransforms();
                Assert.That(solver.Resolve(host, host.Snapshot.Value, 0).MuzzleBlocked, Is.True, "small muzzle cannot shoot through wall");
                var pivot=origin+host.Presentation.CameraPivot;blocker.transform.position=pivot+host.Presentation.CameraOffset*.5f;
                blocker.GetComponent<BoxCollider>().size=Vector3.one*.3f;Physics.SyncTransforms();
                Assert.That(Vector3.Distance(pivot,PrototypePlayer.CameraPosition(pivot,Quaternion.identity,host.Presentation)),Is.LessThan(host.Presentation.CameraOffset.magnitude*.6f));
                blocker.SetActive(false); Physics.SyncTransforms();
                var source = WeaponConfigService.Current.Source(8); var clone = Object.Instantiate(source);
                try
                {
                    WeaponConfigService.Current.SetForEditor(8, w, clone); uint revision = WeaponConfigService.Current.Revision(8);
                    var service = new InkProjectileService(); service.SpawnForMeasurement(new InkShot { Id = ++id, ActionId = id, HeroId = 8, Team = 1, Origin = origin + Vector3.up * 50, Velocity = Vector3.forward * w.SpeedMin, Configuration = w, Born = Now() });
                    clone.shooterPaintNearRadius += .25f;
                    yield return Wait(() => { app.ApplyDebugWeaponChanges(); return WeaponConfigService.Current.Revision(8) > revision; }, "Detailed config hot reload");
                    Assert.That(service.LiveShots().Single().Configuration.ShooterPaintNearRadius, Is.EqualTo(w.ShooterPaintNearRadius));
                    Assert.That(GameplayConfig.GetWeapon(8).ShooterPaintNearRadius, Is.EqualTo(w.ShooterPaintNearRadius + .25f));
                }
                finally { WeaponConfigService.Current.SetForEditor(8, w, source); Object.Destroy(clone); }
                Place(host, original);
                SplooshSummerPlayEvidence.CaptureTransitions(host);
                Place(bot, original + Vector3.forward * 2, 2);
                SplooshSummerPlayEvidence.CaptureHitVolumes(bot);
                File.WriteAllText(Output + "/playmode.txt", "PASS: actual Boot/Addressables Host; configured portraits; hero 8; low ceiling rejects 8 -> 1; clear space accepts 8 -> 1 -> 8; new capsule receives 38/38/38; standing and paper death disable hit proxies; respawn restores 1.5m body; wall blocks damage and embedded muzzle; immutable in-flight detail configuration across hot reload; rendered transitions and collision evidence.\n");
            }
            finally { Object.Destroy(roof); }
            yield return app.Leave().ToCoroutine();
        }
        static void Capture(Vector3 target, string name)
        {
            var camera = new GameObject("Sploosh acceptance camera").AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false;
            camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing = false;
            camera.transform.position = target + new Vector3(1.5f, .4f, 2.3f); camera.transform.LookAt(target); camera.aspect = 1; camera.fieldOfView = 38;
            var rt = new RenderTexture(960, 960, 24); var old = RenderTexture.active; var image = new Texture2D(960, 960, TextureFormat.RGB24, false);
            try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); image.Apply(); File.WriteAllBytes(Output + "/" + name + ".png", image.EncodeToPNG()); }
            finally { RenderTexture.active = old; Object.Destroy(camera.gameObject); Object.Destroy(rt); Object.Destroy(image); }
        }
    }
}
#endif
