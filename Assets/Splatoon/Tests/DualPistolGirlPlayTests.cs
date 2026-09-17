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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class DualPistolGirlPlayTests
    {
        const string Output = "Reports/DualPistolGirl";
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double end = Time.realtimeSinceStartupAsDouble + 40;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, message);
        }
        [UnityTest] public IEnumerator HostLoadsDualiesAndUsesNormalDamageBeyondReferenceRange()
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
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host ready: " + app.Error);
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            player.RequestHeroChange(2, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 2 && player.CharacterView != null &&
                player.CharacterView.Profile.name == "DualPistolGirlPresentation", "DualPistolGirl selected");
            var w = GameplayConfig.GetWeapon(2); Assert.That(DualiesNormalSimulation.Enabled(w), Is.True);
            Assert.That(w.Damage, Is.EqualTo(36)); Assert.That(w.ShotInk, Is.EqualTo(1.4f));
            app.CaptureMouse(false); match.enabled = false; player.enabled = false;
            var view = player.CharacterView; var state = player.Snapshot.Value;
            state.AttackNeedsRelease = false; state.Health = state.Ink = 100; state.ProtectedUntil = 0;
            state.Grounded = true; state.Swimming = false; state.Yaw = state.BodyYaw = state.Pitch = 0;
            double start = player.NetworkManager.ServerTime.Time; int rounds = 0;
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
            for (int t = 0; t < 60; t++)
            {
                bool fire = WeaponSimulation.Step(ref state, new PlayerInputFrame { Fire = true, FireSequence = 1, Sequence = (uint)t + 1 }, w, start + t / 60.0, false, true);
                view.Present(state, 1f / 60, start + t / 60.0);
                if (fire)
                {
                    Assert.That(state.LastShotMuzzle, Is.EqualTo(rounds % 2)); rounds++;
                    view.Shot(state.LastShotMuzzle, state.ShotActionId, player.NetworkManager.ServerTime.Time);
                    match.Projectiles.Spawn(player, state, player.NetworkManager.ServerTime.Time, match.State.Value.Round);
                    InkPresentation.Current.Spawn(match.Projectiles.Spawned.Last());
                }
                view.Animator.Update(1f / 60); yield return null;
            }
            Assert.That(rounds, Is.EqualTo(7)); Assert.That(view.LeftWeapon, Is.Not.Null); Assert.That(view.LeftNozzle, Is.Not.Null);
            Assert.That(InkPresentation.Current.ParticleCount, Is.GreaterThan(0));
            match.Projectiles.Clear(); InkPresentation.Current.Clear();

            foreach (var collider in player.GetComponentsInChildren<Collider>()) collider.enabled = false;
            var target = new GameObject("Dualies server damage target"); target.transform.SetParent(player.transform);
            var box = target.AddComponent<BoxCollider>(); box.size = new Vector3(4, 30, .002f);
            var origin = Vector3.one * 1000;
            InkProjectileService Fire()
            {
                var service = new InkProjectileService();
                service.SpawnForMeasurement(new InkShot { Id = 1, ShotSequence = 1, Seed = 71, Round = match.State.Value.Round,
                    HeroId = 2, Team = 1, Shooter = ulong.MaxValue, Origin = origin, Velocity = Vector3.forward * w.SpeedMin, Configuration = w });
                service.Simulate(w.Lifetime); return service;
            }
            void RestoreTarget(float distance)
            {
                var victim = player.Snapshot.Value; victim.Team = 2; victim.Health = 100; victim.ProtectedUntil = 0;
                victim.Position = origin + Vector3.forward * distance; victim.Movement = MovementMode.Human;
                player.Snapshot.Value = victim; player.transform.position = victim.Position;
                target.transform.position = origin + Vector3.forward * distance;
                Physics.SyncTransforms();
            }
            try
            {
                foreach (float distance in new[] { 4f, 9f, 12f })
                {
                    RestoreTarget(distance); var service = Fire();
                    Assert.That(service.Impacts.Count, Is.EqualTo(1)); var impact = service.Impacts[0];
                    Assert.That(impact.Victim, Is.EqualTo(player.PlayerId)); Assert.That(impact.Damage, Is.InRange(18f, 36f));
                    if (distance == 4) Assert.That(impact.Damage, Is.EqualTo(36));
                    if (distance == 12) Assert.That(distance, Is.GreaterThan(w.EffectiveRange), "Reference range is not a damage cutoff");
                }
                RestoreTarget(9);
                var wall = new GameObject("Dualies world obstruction"); wall.transform.position = origin + Vector3.forward * 4;
                wall.AddComponent<BoxCollider>().size = new Vector3(10, 10, .05f);
                try
                {
                    Physics.SyncTransforms(); var service = Fire();
                    Assert.That(service.Impacts.Single().Damage, Is.Zero); Assert.That(player.Snapshot.Value.Health, Is.EqualTo(100));
                }
                finally { UnityEngine.Object.DestroyImmediate(wall); }
                // A player at the edge of the larger hit radius is hit; identical world geometry is missed.
                box.size = Vector3.one * .02f; RestoreTarget(4);
                target.transform.position = origin + new Vector3(.19f, 0, 4); Physics.SyncTransforms();
                Assert.That(Fire().Impacts.Single().Damage, Is.EqualTo(36));
                box.enabled = false;
                // Exclude the server player's other hit proxies during the world-only probe.
                var excluded = player.Snapshot.Value; excluded.Health = 0; player.Snapshot.Value = excluded;
                var edge = new GameObject("Dualies field radius probe"); edge.transform.position = target.transform.position;
                edge.AddComponent<BoxCollider>().size = box.size;
                try
                {
                    Physics.SyncTransforms(); var probe = Fire().Impacts.Single();
                    Assert.That(probe.Hit, Is.False, $"Unexpected world contact at {probe.Position}, victim={probe.Victim}, damage={probe.Damage}");
                }
                finally { UnityEngine.Object.DestroyImmediate(edge); }
                File.WriteAllText(Output + "/host-summary.txt", "PASS: real Boot and Host hero load, alternating muzzle presentation and particles; normal 36 damage, damage at 12m beyond reference range, wall occlusion and separate player/world radii. Host only; no independent client or physical two-machine acceptance.\n");
            }
            finally { UnityEngine.Object.Destroy(target); }
            yield return app.Leave().ToCoroutine();
        }
    }
}
#endif
