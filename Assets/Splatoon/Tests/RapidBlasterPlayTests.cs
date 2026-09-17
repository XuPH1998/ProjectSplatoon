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

namespace Splatoon.Tests
{
    public sealed class RapidBlasterPlayTests
    {
        const string Output = "Reports/RapidBlaster";
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 40;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, message);
        }
        [UnityTest] public IEnumerator HostChecksActualHitsPaperOcclusionAndPresentation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static void Place(PrototypePlayer player, Vector3 position, byte team = 2, bool paper = false)
        {
            var s = player.Snapshot.Value;
            s.Health = 100; s.Ink = 100; s.ProtectedUntil = 0; s.Position = position;
            s.Team = team; s.Swimming = paper; s.CompactBody = false; s.Grounded = true;
            s.Movement = paper ? MovementMode.GroundInk : MovementMode.Human;
            s.PaperPose = PaperPose.None; s.PlanarVelocity = Vector3.zero; s.VerticalSpeed = 0;
            player.GetComponent<CharacterController>().enabled = false;
            player.transform.SetPositionAndRotation(position, Quaternion.identity);
            player.GetComponent<CharacterController>().enabled = true;
            player.Snapshot.Value = s;
            player.SwimBody.ApplyCollision(s);
            Physics.SyncTransforms();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host ready");
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            host.RequestHeroChange(5, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 5 &&
                host.CharacterView?.Profile.name == "RocketLauncherGirlPresentation", "Blaster selected");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var enemy = match.AddTestBot(prefab); Assert.That(enemy, Is.Not.Null, match.TestBotMessage);
            var second = match.AddTestBot(prefab); Assert.That(second, Is.Not.Null, match.TestBotMessage);
            yield return Wait(() => enemy.SwimBody != null && second.SwimBody != null, "Bot bodies ready");
            app.CaptureMouse(false); match.enabled = false;
            foreach (var p in match.Players) p.enabled = false;
            var w = GameplayConfig.GetWeapon(5); WeaponConfigValidation.Validate(w);
            var view = host.CharacterView; var visual = host.Snapshot.Value;
            visual.Health = visual.Ink = 100; visual.AttackNeedsRelease = false; visual.Swimming = false; visual.Grounded = true;
            double now = host.NetworkManager.ServerTime.Time;
            int emitted = 0;
            for (int t = 0; t < 60; t++)
            {
                bool fired = WeaponSimulation.Step(ref visual, new PlayerInputFrame { Fire = true, FireSequence = 1, Sequence = (uint)t + 1 }, w, now + t / 60.0, false, true);
                view.Present(visual, 1f / 60, now + t / 60.0);
                if (fired) { emitted++; view.Shot(0, visual.ShotActionId, host.NetworkManager.ServerTime.Time); }
                view.Animator.Update(1f / 60);
                if (t == 10) Capture(view.transform.position + Vector3.up, "shot-animation");
                yield return null;
            }
            Assert.That(emitted, Is.EqualTo(2)); Assert.That(visual.ChargeElapsedSeconds, Is.Zero);
            // Controlled empty space isolates the production roster, physics and damage path.
            var origin = new Vector3(1000, 1001, 1000);
            var center = BlasterBallistics.Position(origin, Vector3.forward * w.SpeedMin, w, .25);
            Place(host, origin - Vector3.up, 1);
            Place(second, origin + Vector3.right * 20);
            uint id = 10000;
            InkProjectileService Fire(Vector3 start)
            {
                var service = new InkProjectileService(); double born = host.NetworkManager.ServerTime.Time;
                service.SpawnForMeasurement(new InkShot { Id = ++id, ActionId = id, Round = match.State.Value.Round, HeroId = 5,
                    Team = 1, Shooter = host.PlayerId, Seed = id, Born = born, Origin = start,
                    Velocity = Vector3.forward * w.SpeedMin, Configuration = w,
                    ConfigurationRevision = WeaponConfigService.Current.Revision(5) });
                service.Simulate(born + .25);
                Assert.That(service.Explosions.Count, Is.EqualTo(1));
                return service;
            }
            Place(enemy, origin + Vector3.forward * 5 - Vector3.up);
            var direct = Fire(origin);
            Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(15), "Direct victim must receive only 85");
            Assert.That(direct.Explosions[0].Collision, Is.True);
            Assert.That(direct.Impacts.Count(i => i.Victim == enemy.PlayerId && i.Damage > 0), Is.EqualTo(1));
            Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.Zero, "Two directs defeat");

            var splashPosition = center + Vector3.right * 1.6f - Vector3.up * .7f;
            Place(enemy, splashPosition);
            var splash = Fire(origin);
            Assert.That(splash.Explosions[0].Collision, Is.False);
            Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(65));
            Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(30));
            Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.Zero, "Three normal blasts defeat");
            Place(enemy, origin + Vector3.forward * 5 - Vector3.up); Fire(origin);
            var low = enemy.Snapshot.Value;
            Place(enemy, splashPosition); low.Position = splashPosition; enemy.Snapshot.Value = low;
            Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.Zero, "Direct plus normal blast defeats");

            Place(enemy, splashPosition); Place(second, center - Vector3.right * 1.6f - Vector3.up * .7f);
            var multi = Fire(origin);
            Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(65)); Assert.That(second.Snapshot.Value.Health, Is.EqualTo(65));
            Assert.That(multi.Impacts.Count(i => i.Damage > 0), Is.EqualTo(2));
            multi.Simulate(host.NetworkManager.ServerTime.Time + 2);
            Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(65), "No repeat splash");

            Place(second, origin + Vector3.right * 20);
            float capsuleRadius = enemy.GetComponent<CharacterController>().radius;
            foreach (float margin in new[] { -.025f, .025f })
            {
                var position = center + Vector3.right * (w.Ammo.ExplosionRadius + capsuleRadius + margin) - Vector3.up * .9f;
                Place(enemy, position);
                // CharacterController's queried geometry includes its native skin treatment.
                // Position the actual queried surface at the intended boundary, not its bounds.
                var closest = enemy.GetComponent<CharacterController>().ClosestPoint(center);
                position -= Vector3.right * (Vector3.Distance(center, closest) - w.Ammo.ExplosionRadius - margin);
                Place(enemy, position);
                closest = enemy.GetComponent<CharacterController>().ClosestPoint(center);
                Assert.That(Vector3.Distance(center, closest), Is.EqualTo(w.Ammo.ExplosionRadius + margin).Within(.003));
                Fire(origin);
                Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(margin < 0 ? 65 : 100), "Actual hit shape at blast boundary");
            }

            Place(second, origin + Vector3.forward * 4 - Vector3.up, 1);
            Place(enemy, origin + Vector3.forward * 7 - Vector3.up);
            Fire(origin); Assert.That(second.Snapshot.Value.Health, Is.EqualTo(100));
            Assert.That(host.Snapshot.Value.Health, Is.EqualTo(100)); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(15), "Ally is transparent");

            Place(enemy, origin + Vector3.forward * 5 - Vector3.up);
            Place(second, origin + Vector3.forward * 5 + Vector3.right * .85f - Vector3.up);
            Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(15));
            Assert.That(second.Snapshot.Value.Health, Is.EqualTo(82.5f), "Early collision splash is half damage");

            Place(enemy, splashPosition); Place(second, origin + Vector3.right * 20);
            var wall = new GameObject("Blaster occlusion wall");
            try
            {
                wall.transform.position = center + Vector3.right * .8f;
                wall.AddComponent<BoxCollider>().size = new Vector3(.15f, 6, 6); Physics.SyncTransforms();
                Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(100), "Wall shields blast");
                wall.transform.position = origin + Vector3.forward * 4;
                wall.GetComponent<BoxCollider>().size = new Vector3(6, 6, .15f); Physics.SyncTransforms();
                var early = Fire(origin); Assert.That(early.Explosions[0].Collision, Is.True);
                Assert.That(early.Explosions[0].Position.z - origin.z, Is.LessThan(4));
            }
            finally { UnityEngine.Object.DestroyImmediate(wall); }

            Place(enemy, center + Vector3.right * 1.6f - Vector3.up * .1f, 2, true);
            Assert.That(enemy.SwimBody.FlatHitActive, Is.True);
            Assert.That(InkExplosionRules.ClosestPoint(enemy, center, out var paperPoint), Is.True);
            Assert.That(Vector3.Distance(center, paperPoint), Is.LessThan(w.Ammo.ExplosionRadius));
            Fire(origin); Assert.That(enemy.Snapshot.Value.Health, Is.EqualTo(65), "Paper takes one blast using its current mesh");

            // Render the authoritative event in the real training scene and exercise duplicate rejection.
            var visible = visual.Position + Vector3.up * 2;
            foreach (byte team in new byte[] { 1, 2 })
            {
                var evt = splash.Explosions[0]; evt.ShotId = ++id; evt.Team = team; evt.Position = visible;
                var presentation = InkPresentation.Current; presentation.Clear(); evt.Time = host.NetworkManager.ServerTime.Time + .001;
                presentation.Explosion(evt); presentation.Explosion(evt);
                var roots = Enumerable.Range(0, presentation.transform.childCount).Select(i => presentation.transform.GetChild(i)).Where(t => t.name == "Ink explosion" && t.gameObject.activeSelf).ToArray();
                Assert.That(roots.Length, Is.EqualTo(1));
                Assert.That(roots[0].localScale.x, Is.EqualTo(w.Ammo.ExplosionRadius).Within(.00001));
                foreach (var ps in roots[0].GetComponentsInChildren<ParticleSystem>())
                { Assert.That(ps.main.startColor.color, Is.EqualTo(PrototypeArena.TeamColor(team))); ps.Simulate(.10f, false, true, false); }
                Capture(visible, "ink-explosion-team-" + team, new Vector3(3, 2, 7));
                // A same-round delayed event after clearing must not restore the burst.
                presentation.Clear(); evt.ShotId = ++id; evt.Time = host.NetworkManager.ServerTime.Time - 1;
                presentation.Explosion(evt);
                Assert.That(Enumerable.Range(0, presentation.transform.childCount).Select(i => presentation.transform.GetChild(i)).Count(t => t.name == "Ink explosion" && t.gameObject.activeSelf), Is.Zero);
            }
            yield return RapidBlasterExplosionChecks.Check(splash.Explosions[0], visual, host, view.Profile);
            int actualStamps = 0;
            Vector3 paintCenter = default, paintNormal = Vector3.up;
            var paint = new InkProjectileService { PaintObserved = stamp => { actualStamps++; paintCenter = stamp.Position; paintNormal = stamp.Normal; } };
            double paintBorn = host.NetworkManager.ServerTime.Time;
            var paintOrigin = visual.Position + Vector3.up * 1.2f;
            paint.SpawnForMeasurement(new InkShot { Id = ++id, Round = match.State.Value.Round, HeroId = 5, Team = 1,
                Shooter = host.PlayerId, Born = paintBorn, Origin = paintOrigin, Velocity = Vector3.forward * w.SpeedMin, Configuration = w });
            paint.Simulate(paintBorn + .25);
            Assert.That(actualStamps, Is.GreaterThan(0), "Training scene receives authoritative paint");
            foreach (var surface in PrototypeArena.Current.Surfaces.Values) surface.FlushDisplay();
            Capture(paintCenter, "training-paint", paintNormal * 4 + Vector3.up * 2 + Vector3.right * 3);
            // Use a detached source clone to exercise the real editor transaction without
            // changing the user's weapon asset, including a shot already in flight.
            var source = WeaponConfigService.Current.Source(5);
            var clone = UnityEngine.Object.Instantiate(source);
            try
            {
                WeaponConfigService.Current.SetForEditor(5, w, clone);
                var before = host.Snapshot.Value; before.AttackRecoveryUntil = host.NetworkManager.ServerTime.Time + 20 / 60.0;
                host.Snapshot.Value = before;
                var pending = new InkProjectileService(); double born = host.NetworkManager.ServerTime.Time;
                var oldShot = new InkShot { Id = ++id, Round = match.State.Value.Round, HeroId = 5, Team = 1, Shooter = host.PlayerId,
                    Origin = origin + Vector3.up * 20, Velocity = Vector3.forward * w.SpeedMin, Born = born, Configuration = w };
                pending.SpawnForMeasurement(oldShot);
                uint revision = WeaponConfigService.Current.Revision(5);
                clone.blasterRepeatSeconds = .7; clone.blasterBrakeDrag = .2f; clone.damage = clone.damageMin = 90;
                yield return Wait(() => { app.ApplyDebugWeaponChanges(); return WeaponConfigService.Current.Revision(5) > revision; }, "Blaster hot reload");
                Assert.That(GameplayConfig.GetWeapon(5).BlasterRepeatSeconds, Is.EqualTo(.7));
                Assert.That(GameplayConfig.GetWeapon(5).Damage, Is.EqualTo(90));
                Assert.That(host.Snapshot.Value.AttackRecoveryUntil, Is.EqualTo(before.AttackRecoveryUntil), "Reload does not erase earned recovery");
                pending.Simulate(born + .25);
                Assert.That(pending.Explosions.Single().Position, Is.EqualTo(BlasterBallistics.Position(oldShot.Origin, oldShot.Velocity, w, .25)));
                Assert.That(w.Damage, Is.EqualTo(85));
            }
            finally { WeaponConfigService.Current.SetForEditor(5, w, source); UnityEngine.Object.DestroyImmediate(clone); }
            File.WriteAllText(Output + "/host-summary.txt", "PASS: Host training load and rapid firing animation; real direct 85 / blast 35 / collision blast 17.5; all three defeat combinations; actual blast geometry boundary; friend pass-through and immunity; wall collision/occlusion; two simultaneous victims; paper mesh; current and stale event rejection; both team particle colors; authoritative training paint; real editor hot reload retains old shot trajectory and earned recovery. Independent clients and devices not tested.\n");
            yield return app.Leave().ToCoroutine();
        }
        static void Capture(Vector3 target, string name, Vector3? offset = null)
        {
            var camera = new GameObject("Blaster validation camera").AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false;
            camera.transform.position = target + (offset ?? new Vector3(3, 2, 4)); camera.transform.LookAt(target); camera.aspect = 1; camera.fieldOfView = 45;
            var rt = new RenderTexture(960, 960, 24); var old = RenderTexture.active;
            var texture = new Texture2D(960, 960, TextureFormat.RGB24, false);
            try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; texture.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); texture.Apply(); File.WriteAllBytes(Output + "/" + name + ".png", texture.EncodeToPNG()); }
            finally { RenderTexture.active = old; UnityEngine.Object.Destroy(camera.gameObject); UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(texture); }
        }
    }
}
#endif
