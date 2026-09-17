#if UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    public sealed class PistolGirlPlayTests
    {
        const string Output = "Reports/PistolGirl/20260917";
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 40;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, message);
        }

        [UnityTest] public IEnumerator HostLoadsPistolAndMeasuresRealCollisionDamageAndPresentation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }

        [UnityTearDown] public IEnumerator CleanupPlayMode()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }

        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom && app.IsWeaponDebugRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host ready");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            player.RequestHeroChange(4, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 4 && player.CharacterView != null &&
                player.CharacterView.Profile.name == "PistolGirlPresentation", "PistolGirl selected");
            app.CaptureMouse(false); match.enabled = false; player.enabled = false;
            var w = GameplayConfig.GetWeapon(4);
            Assert.That(w.FireMode, Is.EqualTo(WeaponFireMode.Automatic));
            Assert.That(w.FireRate, Is.EqualTo(12));
            Assert.That(player.CharacterView.Profile.ShotPlaybackSeconds, Is.EqualTo(.075f).Within(.000001f));
            var view = player.CharacterView;
            var visualState = player.Snapshot.Value;
            visualState.AttackNeedsRelease = false; visualState.ProtectedUntil = 0;
            visualState.Health = 100; visualState.Ink = 100; visualState.Grounded = true;
            visualState.Swimming = false; visualState.Yaw = visualState.BodyYaw = visualState.Pitch = 0;
            double start = player.NetworkManager.ServerTime.Time;
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
            int emitted = 0;
            for (int tick = 0; tick < 60; tick++)
            {
                var input = new PlayerInputFrame { Fire = true, FireSequence = visualState.ConsumedFire + 1, Sequence = (uint)tick + 1 };
                bool fired = WeaponSimulation.Step(ref visualState, input, w, start + tick / 60.0, false, true);
                view.Present(visualState, 1f / 60, start + tick / 60.0);
                if (fired)
                {
                    emitted++; view.Shot(0, visualState.ShotActionId, player.NetworkManager.ServerTime.Time);
                    match.Projectiles.Spawn(player, visualState, player.NetworkManager.ServerTime.Time, match.State.Value.Round);
                    InkPresentation.Current.Spawn(match.Projectiles.Spawned.Last());
                    Assert.That(view.Animator.GetLayerWeight(1), Is.GreaterThan(.5f));
                }
                view.Animator.Update(1f / 60);
                if (tick == 32) Capture(view, "continuous-fire");
                yield return null;
            }
            Assert.That(emitted, Is.EqualTo(12));
            Assert.That(InkPresentation.Current.ParticleCount, Is.GreaterThan(0));
            Assert.That(view.GetComponentInChildren<InkMuzzleEmitter>(), Is.Not.Null);
            match.Projectiles.Clear(); InkPresentation.Current.Clear();

            // Isolate a thin target surface on a real server-owned player. Distances
            // refer to swept projectile-center travel, not the far side of a capsule.
            foreach (var collider in player.GetComponentsInChildren<Collider>()) collider.enabled = false;
            var target = new GameObject("Pistol range measurement target"); target.transform.SetParent(player.transform);
            var box = target.AddComponent<BoxCollider>(); box.size = new Vector3(4, 4, .002f);
            var origin = new Vector3(1000, 1000, 1000);
            var csv = new StringBuilder("distanceM,firstDamage,hitsToDefeat,impactAgeSeconds,actualTravelM\n");
            try
            {
                foreach (var pair in new[] { (3f, 4), (5f, 4), (6f, 4), (7f, 5), (8f, 6), (9f, 7), (9.1f, 0) })
                {
                    var state = player.Snapshot.Value; state.Health = 100; state.Team = 2; state.ProtectedUntil = 0;
                    state.Position = origin + Vector3.forward * pair.Item1; state.Movement = MovementMode.Human;
                    player.Snapshot.Value = state; player.transform.position = state.Position;
                    target.transform.position = origin + Vector3.forward * (pair.Item1 + w.CollisionRadius - .002f);
                    Physics.SyncTransforms();
                    float firstDamage = 0; double hitAge = 0; float travel = 0; int hits = 0;
                    var projectiles = new InkProjectileService();
                    projectiles.TraceObserved = (shot, age, point) => { hitAge = age; travel = shot.Velocity.magnitude * InkBallistics.TravelTime(w, age); };
                    for (int i = 0; i < (pair.Item2 == 0 ? 2 : 8) && player.Snapshot.Value.Health > 0; i++)
                    {
                        projectiles.Clear(); double born = player.NetworkManager.ServerTime.Time;
                        projectiles.SpawnForMeasurement(new InkShot { Id = (uint)i + 1, Round = match.State.Value.Round,
                            HeroId = 4, Team = 1, Shooter = ulong.MaxValue, Seed = 71, Born = born, Origin = origin,
                            Velocity = Vector3.forward * w.SpeedMin, Configuration = w });
                        projectiles.Simulate(born + 1);
                        Assert.That(projectiles.Impacts.Count, Is.EqualTo(1));
                        var impact = projectiles.Impacts[0]; Assert.That(impact.Victim, Is.EqualTo(player.PlayerId));
                        if (i == 0) firstDamage = impact.Damage;
                        if (impact.Damage > 0) hits++;
                    }
                    Assert.That(hits, Is.EqualTo(pair.Item2), "distance=" + pair.Item1);
                    Assert.That(player.Snapshot.Value.Health, Is.EqualTo(pair.Item2 == 0 ? 100 : 0));
                    csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2},{3:R},{4:R}", pair.Item1, firstDamage, hits, hitAge, travel));
                }
                File.WriteAllText(Output + "/collision-damage.csv", csv.ToString());
                File.WriteAllText(Output + "/host-summary.txt", "PASS: real Host load, 12 shots/second, animation layer and visible particles; swept collisions at 3/5/6/7/8/9m defeat in 4/4/4/5/6/7 hits; 9.1m causes no damage. Host-only, not independent-client validation.\n");
            }
            finally { UnityEngine.Object.Destroy(target); }
            yield return app.Leave().ToCoroutine();
        }

        static void Capture(InkCharacterView view, string name)
        {
            var camera = new GameObject("Pistol capture").AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false;
            camera.transform.position = view.transform.position + new Vector3(2.7f, 1.5f, 2.3f);
            camera.transform.LookAt(view.transform.position + Vector3.up * 1.1f); camera.aspect = 1; camera.fieldOfView = 38;
            var rt = new RenderTexture(960, 960, 24); var old = RenderTexture.active;
            var texture = new Texture2D(960, 960, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); texture.Apply();
                File.WriteAllBytes(Output + "/" + name + ".png", texture.EncodeToPNG());
            }
            finally { RenderTexture.active = old; UnityEngine.Object.Destroy(camera.gameObject); UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(texture); }
        }
    }
}
#endif
