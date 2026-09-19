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
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BubbleShotgunPlayTests
    {
        const string Output = "Reports/BubbleShotgun/PlayMode";
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnityTest] public IEnumerator HostChecksDamageFriendliesPaperOcclusionPaintAndPresentation()
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
        static void Place(PrototypePlayer player, Vector3 position, byte team = 2, bool paper = false)
        {
            var s = player.Snapshot.Value; s.Health = s.Ink = 100; s.ProtectedUntil = 0; s.Position = position;
            s.Team = team; s.Swimming = paper; s.CompactBody = false; s.Grounded = true;
            s.Movement = paper ? MovementMode.GroundInk : MovementMode.Human; s.PaperPose = PaperPose.None;
            s.PlanarVelocity = Vector3.zero; s.VerticalSpeed = 0; s.AirHumanOffset = 0;
            player.GetComponent<CharacterController>().enabled = false; player.transform.SetPositionAndRotation(position, Quaternion.identity);
            player.GetComponent<CharacterController>().enabled = true;
            player.Snapshot.Value = s; player.SwimBody.ApplyCollision(s); Physics.SyncTransforms();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host ready");
            Assert.That(app.Heroes.All.Count(), Is.EqualTo(9));
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            host.RequestHeroChange(9, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 9 && host.CharacterView?.Profile.name == "BubbleShotgunGirlPresentation", "Select ninth hero");
            Assert.That(host.CharacterView.Profile.SingleShot, Is.True); Assert.That(host.SwimBody.Profile, Is.Not.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var enemy = match.AddTestBot(prefab); var second = match.AddTestBot(prefab);
            yield return Wait(() => enemy?.SwimBody != null && second?.SwimBody != null, "Authoritative bot bodies");
            app.CaptureMouse(false); match.enabled = false; foreach (var p in match.Players) p.enabled = false;
            var w = GameplayConfig.GetWeapon(9); uint id = 190000; var origin = new Vector3(20, 40, 20);
            Place(host, origin + Vector3.back * 4 - Vector3.up, 1); Place(second, origin + Vector3.right * 20);
            InkShot Make(Vector3 point, Vector3 velocity) => new() { Id = ++id, Round = match.State.Value.Round, HeroId = 9,
                Shooter = host.PlayerId, Team = 1, Born = match.NetworkManager.ServerTime.Time + .01, Origin = point,
                Velocity = velocity, Configuration = w, ConfigurationRevision = WeaponConfigService.Current.Revision(9), Seed = id, ActionId = id };
            InkProjectileService Fire(Vector3? start = null, double duration = .25)
            {
                var service = new InkProjectileService(); var shot = Make(start ?? origin, Vector3.forward * 18);
                service.SpawnForMeasurement(shot); service.Simulate(shot.Born + duration); return service;
            }
            Place(enemy, origin + Vector3.forward * 2 - Vector3.up * .9f);
            var direct = Fire(); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(45));
            Assert.That(direct.Impacts.Count(i => i.Victim == enemy.PlayerId && i.Damage > 0), Is.EqualTo(1));
            Fire(); Assert.That(enemy.Snapshot.Value.Health, Is.Zero, "Two direct hits defeat 100 HP");
            Place(enemy, origin + Vector3.forward * 2 - Vector3.up * .9f, 1);
            var friendly = Fire(); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(100)); Assert.That(friendly.ActiveCount, Is.EqualTo(1));
            Place(enemy, origin + Vector3.forward * 2 - Vector3.up * .2f, 2, true);
            Fire(); Assert.That(enemy.Snapshot.Value.Health, Is.LessThan(100), "Paper body is hittable");
            Place(enemy, origin + Vector3.right * 20);
            var blocker = new GameObject("Bubble shotgun thin wall"); blocker.transform.position = origin + Vector3.forward * 2;
            blocker.AddComponent<BoxCollider>().size = new Vector3(12, 12, .1f); Physics.SyncTransforms();
            Place(second, origin + Vector3.forward * 2.8f - Vector3.up * .9f);
            var blocked = Fire(); Assert.That(blocked.Explosions.Count, Is.EqualTo(1)); Assert.That(second.Snapshot.Value.Health, Is.EqualTo(100), "Thin wall shields splash");
            Place(second, origin + Vector3.right * 1.2f + Vector3.forward * 1.4f - Vector3.up * .9f);
            Fire(); Assert.That(second.Snapshot.Value.Health, Is.InRange(75f, 99.99f), "Exposed neighbour receives splash");
            Object.DestroyImmediate(blocker); Place(second, origin + Vector3.right * 20);

            var floor = Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)
                .Where(s => s.Scores && s.GetComponent<Collider>() != null && s.GetComponent<Collider>().bounds.size.y < 2)
                .OrderByDescending(s => s.GetComponent<Collider>().bounds.size.x * s.GetComponent<Collider>().bounds.size.z).First();
            var floorCollider = floor.GetComponent<Collider>();
            Assert.That(floorCollider.Raycast(new Ray(floorCollider.bounds.center + Vector3.up * 20, Vector3.down), out var floorHit, 40), Is.True);
            var paintOrigin = floorHit.point + Vector3.up * 1.3f;
            var paints = new List<PaintStamp>(); var paintService = new InkProjectileService { PaintObserved = paints.Add };
            var paintShot = Make(paintOrigin, Vector3.down * 18); paintService.SpawnForMeasurement(paintShot); paintService.Simulate(paintShot.Born + 4);
            Assert.That(paints.Count, Is.GreaterThan(0)); Assert.That(paints.Max(s => s.Radius), Is.GreaterThan(2));
            Assert.That(match.PaintSequence, Is.GreaterThan(0)); yield return null;
            Capture(paintOrigin, "wide-paint", new Vector3(4, 8, -6));

            var preservedShot = Make(origin, Vector3.forward * 18);
            var preservedService = new InkProjectileService(); preservedService.SpawnForMeasurement(preservedShot);
            match.enabled = host.enabled = true; host.RequestHeroChange(1, HeroSelectionOrigin.Debug);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 1, "Switch hero with bubble in flight");
            preservedService.Simulate(preservedShot.Born + 2);
            var preserved = new List<InkBubbleState>(); preservedService.CaptureBubbles(preserved);
            Assert.That(preserved.Single().Shot.Configuration, Is.SameAs(w));
            Assert.That(preserved.Single().Shot.HeroId, Is.EqualTo(9));
            host.RequestHeroChange(9, HeroSelectionOrigin.Debug);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 9 && host.CharacterView?.Profile.name == "BubbleShotgunGirlPresentation", "Return to bubble shotgun");
            match.enabled = host.enabled = false;

            var presentation = InkPresentation.Current; presentation.Clear();
            Place(host, origin - Vector3.up * 1.1f + Vector3.left * 1.8f, 1);
            host.CharacterView.transform.position = host.transform.position;
            for (int frame = 0; frame < 3; frame++) yield return null;
            var comparison = Make(origin, Vector3.forward * 18); presentation.Spawn(comparison);
            presentation.UpdateFlights(comparison.Born + .1, Camera.main);
            Capture(origin + Vector3.left * .6f, "head-size-comparison", new Vector3(2, .5f, -5));
            presentation.Clear(); var shown = new List<InkShot>();
            for (int i = 0; i < w.PelletCount; i++) { var s = Make(origin + Vector3.up * 2, InkBallistics.PelletVelocity(Vector3.forward, w, w.SpreadDegrees, i, 77)); shown.Add(s); presentation.Spawn(s); }
            double born = shown[0].Born;
            foreach (double age in new[] { .1, .3, 2.0, 3.9 })
            { presentation.UpdateFlights(born + age + BubbleFlightPresentation.RenderDelay, Camera.main); Assert.That(presentation.ActiveShots, Is.EqualTo(w.PelletCount)); Capture(origin + Vector3.up * 2 + Vector3.forward * 4, "configured-bubbles-" + age.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), new Vector3(5, 2, -7)); }
            var mesh = presentation.GetComponentsInChildren<Renderer>().First(r => r.gameObject.name.StartsWith("BubbleInk") && r.gameObject.activeInHierarchy);
            Assert.That(mesh.transform.localScale.x, Is.EqualTo(w.CollisionRadius * 2).Within(.001));
            var old = GameplayConfig.GetWeapon(9);
            var weaponCopy = Object.Instantiate(WeaponConfigService.Current.Source(9)); weaponCopy.speedMin = weaponCopy.speedMax = 9;
            WeaponConfigService.Current.Replace(9, weaponCopy.Snapshot());
            Assert.That(shown[0].Configuration.SpeedMin, Is.EqualTo(18)); WeaponConfigService.Current.Replace(9, old);
            Object.Destroy(weaponCopy);
            presentation.Clear(); var state = new InkBubbleState { Shot = shown[0], Segment = InkBounce.Initial(shown[0]) };
            presentation.RestoreBubble(state); presentation.RestoreBubble(state); presentation.UpdateFlights(born + 2.1, Camera.main);
            Assert.That(presentation.ActiveShots, Is.EqualTo(1), "Late restore and duplicate restore");
            var explosion = new InkExplosionEvent { Round = state.Shot.Round, ShotId = state.Shot.Id, HeroId = 9, ConfigurationRevision = state.Shot.ConfigurationRevision,
                Position = InkBallistics.Position(state.Shot, w, 4), Normal = Vector3.up, Team = 1, Seed = 77, Time = born + 4 };
            presentation.Explosion(explosion); presentation.Explosion(explosion); presentation.UpdateFlights(born + 4.11, Camera.main);
            Assert.That(presentation.ActiveShots, Is.Zero, "Explosion retires bubble on the same clock");
            Assert.That(presentation.transform.Cast<Transform>().Count(t => t.name == "Ink explosion" && t.gameObject.activeSelf), Is.EqualTo(1));
            foreach (var ps in presentation.GetComponentsInChildren<ParticleSystem>()) ps.Simulate(.06f, false, true, false);
            Capture(explosion.Position, "bubble-pop", new Vector3(4, 3, -6));
            presentation.Spawn(state.Shot); presentation.UpdateFlights(born + 4.12, Camera.main); Assert.That(presentation.ActiveShots, Is.Zero);
            var lateShot = Make(origin, Vector3.forward * 18);
            explosion.ShotId = lateShot.Id; explosion.Time = lateShot.Born + .2;
            presentation.Explosion(explosion); presentation.Spawn(lateShot); presentation.RestoreBubble(new InkBubbleState { Shot = lateShot, Segment = InkBounce.Initial(lateShot) });
            presentation.UpdateFlights(lateShot.Born + .31, Camera.main);
            Assert.That(presentation.ActiveShots, Is.Zero, "Explosion arriving before spawn and restore suppresses the old bubble");
            presentation.Clear(); presentation.Clear();
            app.OpenHeroSelection(HeroSelectionOrigin.Debug);
            typeof(PrototypeApp).GetField("_previewHeroId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, 9);
            typeof(PrototypeApp).GetField("_heroCardScroll", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, new Vector2(0, 110));
            yield return null; File.Delete(Output + "/ninth-hero-selection.png");
            ScreenCapture.CaptureScreenshot(Output + "/ninth-hero-selection.png");
            yield return Wait(() => File.Exists(Output + "/ninth-hero-selection.png"), "Selection screenshot");
            File.WriteAllText(Output + "/host-summary.txt", "PASS: nine heroes, direct 55/two-hit defeat, allies transparent, paper hit, occluded splash, wide real-map paint, five horizontal fan meshes with vertical jitter, duplicate/out-of-order restoration/explosion, immutable shots across hero and config changes, synchronized pop and clear. Host fixture only; independent clients and target-device performance not established.\n");
            yield return app.Leave().ToCoroutine();
        }
        static void Capture(Vector3 target, string name, Vector3 offset)
        {
            var camera = new GameObject("Bubble shotgun capture").AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false;
            camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing = false;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .19f, .22f);
            camera.transform.position = target + offset; camera.transform.LookAt(target); camera.aspect = 1; camera.fieldOfView = 45;
            var rt = new RenderTexture(960, 960, 24); var previous = RenderTexture.active; var texture = new Texture2D(960, 960, TextureFormat.RGB24, false);
            try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; texture.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); texture.Apply(); File.WriteAllBytes(Output + "/" + name + ".png", texture.EncodeToPNG()); }
            finally { RenderTexture.active = previous; Object.Destroy(camera.gameObject); Object.Destroy(rt); Object.Destroy(texture); }
        }
    }
}
#endif
