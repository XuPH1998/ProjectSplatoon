#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
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
        [UnityTest, Timeout(600000)] public IEnumerator HostChecksActualHitsPaperOcclusionAndPresentation()
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
            s.Health = 100; s.Ink = 100; s.LifeState = PlayerLifeState.Alive; s.BubbleUntil = 0; s.ProtectedUntil = 0; s.Position = position;
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

            // Default blast cosmetics are bounded opaque fragments. Retain both
            // teams, normal/early collision radii, duplicate and stale event rejection,
            // real-camera pixels, wall depth occlusion and complete natural retirement.
            using var blocks = new BlockDefaultContractFixture(match);
            var terrainBeforeVisuals = blocks.Capture(); double visualPoints = host.Snapshot.Value.SpecialPoints;
            var presentation = InkPresentation.Current;
            var camera = new GameObject("Default blaster fragment acceptance").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1; camera.fieldOfView = 45;
            var visible = new Vector3(0, 8, -20); camera.transform.position = visible + new Vector3(0, 1, -5); camera.transform.LookAt(visible);
            try
            {
                foreach (bool collision in new[] { false, true }) foreach (byte team in new byte[] { 1, 2 })
                {
                    presentation.Clear(); yield return null;
                    var hidden = Pixels(camera);
                    var evt = splash.Explosions[0]; evt.ShotId = ++id; evt.Team = team; evt.Collision = collision; evt.Position = visible;
                    evt.Time = host.NetworkManager.ServerTime.Time + .001;
                    float radius = InkExplosionRules.Radius(w.Ammo, collision);
                    int expected = Mathf.Clamp(Mathf.CeilToInt(radius * 18), 16, 64);
                    presentation.Explosion(evt); presentation.Explosion(evt);
                    Assert.That(presentation.BlockDebrisCount, Is.EqualTo(expected), "The duplicate ID emits exactly one complete radius-scaled fragment burst");
                    presentation.UpdateFlights(evt.Time + .08, camera);
                    Assert.That(presentation.BlockCubeCount, Is.EqualTo(expected));
                    Assert.That(presentation.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);
                    var shown = Pixels(camera);
                    Assert.That(shown.Zip(hidden, (a, b) => Mathf.Abs(a.r-b.r)+Mathf.Abs(a.g-b.g)+Mathf.Abs(a.b-b.b) > 20).Count(changed => changed),
                        Is.GreaterThan(25), "Configured cube fragments visibly render for both teams and both blast radii");
                    blocks.Unchanged(terrainBeforeVisuals, "Cosmetic blast cannot mutate structure or scores");
                    Assert.That(host.Snapshot.Value.SpecialPoints, Is.EqualTo(visualPoints));
                    // At .3 s all generated .49..7 s lifetimes are still active.
                    presentation.UpdateFlights(evt.Time + .3, camera); Assert.That(presentation.BlockDebrisCount, Is.EqualTo(expected));
                    presentation.UpdateFlights(evt.Time + .71, camera); Assert.That(presentation.BlockDebrisCount, Is.Zero);
                    presentation.Clear(); evt.ShotId = ++id; evt.Time = host.NetworkManager.ServerTime.Time - 1;
                    presentation.Explosion(evt); Assert.That(presentation.BlockDebrisCount, Is.Zero);
                }
                // A fully covering opaque wall must reject every fragment pixel.
                var depthWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    depthWall.transform.position = visible + Vector3.back * 2; depthWall.transform.localScale = new Vector3(10, 10, .3f);
                    Physics.SyncTransforms(); presentation.Clear(); yield return null;
                    var hidden = Pixels(camera); var evt = splash.Explosions[0]; evt.ShotId = ++id; evt.Position = visible;
                    evt.Time = host.NetworkManager.ServerTime.Time + .001;
                    presentation.Explosion(evt); presentation.UpdateFlights(evt.Time + .08, camera);
                    Assert.That(Pixels(camera).Zip(hidden, (a, b) => Mathf.Abs(a.r-b.r)+Mathf.Abs(a.g-b.g)+Mathf.Abs(a.b-b.b) > 6).Count(changed => changed),
                        Is.LessThan(20), "Opaque wall hides the actual instance draw");
                }
                finally { UnityEngine.Object.DestroyImmediate(depthWall); }
            }
            finally { presentation.Clear(); UnityEngine.Object.DestroyImmediate(camera.gameObject); }

            // Paid collision against a clear real floor removes the complete prescribed
            // mixed target set. The outer pair independently witnesses the 50% friendly radius.
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10,
                PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer)), Is.True);
            var seed = blocks.Empty(); var anchor = BlockWorld.CellAt(floor.point + Vector3.up * .05f);
            for (int x = -4; x <= 4; x++)
            {
                Assert.That(seed.Set(anchor + new Vector3Int(x, 0, -3), BlockCell.Create(1, true)), Is.True);
                Assert.That(seed.Set(anchor + new Vector3Int(x, 0, 3), BlockCell.Create(2, true)), Is.True);
            }
            var outerFriendly = anchor + new Vector3Int(9, 0, 0); var outerEnemy = anchor + new Vector3Int(-9, 0, 0);
            Assert.That(seed.Set(outerFriendly, BlockCell.Create(1, true)), Is.True);
            Assert.That(seed.Set(outerEnemy, BlockCell.Create(2, true)), Is.True);
            yield return blocks.Seed(seed); int pinkBefore = blocks.Terrain.PinkCount, blueBefore = blocks.Terrain.BlueCount;
            uint legacyBefore = match.PaintSequence; double pointsBefore = host.Snapshot.Value.SpecialPoints;
            var paidShot = blocks.PaidShot(host, 5, ++id, floor.point + Vector3.up * 1.3f, Vector3.down * w.SpeedMin);
            var payment = paidShot.BlockAttack; float paidMaterial = host.Snapshot.Value.Ink;
            var demolition = new InkProjectileService { PaintObserved = _ => Assert.Fail("Explosive mains cannot build legacy paint") };
            demolition.SpawnForMeasurement(paidShot); demolition.Simulate(paidShot.Born + 2);
            Assert.That(demolition.Explosions.Count, Is.EqualTo(1));
            Assert.That(payment.BuildLimit, Is.Zero);
            yield return blocks.Publish(() => payment.RemovedEnemy > 0);
            var blast = demolition.Explosions.Single(); float collisionRadius = InkExplosionRules.Radius(w.Ammo, blast.Collision);
            Assert.That(blast.Collision, Is.True);
            Assert.That(Vector3.Distance(BlockWorld.Center(outerFriendly), blast.Position), Is.InRange(collisionRadius * .5f + .051f, collisionRadius));
            Assert.That(Vector3.Distance(BlockWorld.Center(outerEnemy), blast.Position), Is.LessThan(collisionRadius));
            for (int x = -4; x <= 4; x++) foreach (int z in new[] { -3, 3 })
                Assert.That(blocks.Terrain.World.TryGet(anchor + new Vector3Int(x, 0, z), out _), Is.False, "Every inner-row target must be removed");
            Assert.That(blocks.Terrain.World.TryGet(outerEnemy, out _), Is.False);
            Assert.That(blocks.Terrain.World.TryGet(outerFriendly, out var survivingFriendly) && survivingFriendly.Owner == 1, Is.True);
            Assert.That(blueBefore - blocks.Terrain.BlueCount, Is.EqualTo(10));
            Assert.That(pinkBefore - blocks.Terrain.PinkCount, Is.EqualTo(9));
            Assert.That(payment.RemovedEnemy, Is.EqualTo(10)); Assert.That(payment.Added, Is.Zero);
            Assert.That(host.Snapshot.Value.Ink, Is.EqualTo(paidMaterial)); Assert.That(match.PaintSequence, Is.EqualTo(legacyBefore));
            Assert.That(host.Snapshot.Value.SpecialPoints - pointsBefore, Is.EqualTo(10 / 32.0).Within(.00001));
            Capture(floor.point, "training-block-destruction", new Vector3(4, 5, -6));
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
            File.WriteAllText(Output + "/host-summary.txt", "PASS: Host training load and rapid firing animation; real direct 85 / blast 35 / collision blast 17.5; all three defeat combinations; actual blast geometry boundary; friend pass-through and immunity; wall collision/occlusion; two simultaneous victims; paper mesh; current and stale event rejection; both team opaque fragment pixels; depth occlusion; paid complete mixed-block demolition with differential radii; real editor hot reload retains old shot trajectory and earned recovery. Independent clients and devices not tested.\n");
            yield return app.Leave().ToCoroutine();
        }
        static Color32[] Pixels(Camera camera)
        {
            var target = new RenderTexture(640, 640, 24); var previous = RenderTexture.active;
            var image = new Texture2D(640, 640, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); image.Apply(); return image.GetPixels32();
            }
            finally { camera.targetTexture = null; RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(image); }
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
