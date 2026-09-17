#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class AmmoDebugPlayTests
    {
        const string Output = "Reports/InkFlightReference/AmmoMerge";
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, message);
        }
        static IEnumerator Applied(PrototypeApp app, int hero, uint previous)
        {
            yield return Wait(() => { app.ApplyDebugWeaponChanges(); return WeaponConfigService.Current.Revision(hero) > previous; }, "Ammo reload: " + app.WeaponDebugStatus);
        }
        [UnityTest] public IEnumerator HostAmmoReloadKeepsHistoryAndNormalRoomIsolation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "/host-reload.txt", "");
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom && app.IsWeaponDebugRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host presentation");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            player.RequestHeroChange(1, HeroSelectionOrigin.Warmup);
            yield return Wait(() => player.Snapshot.Value.HeroId == 1 && !player.HeroChangePending, "Rifle");
            app.CaptureMouse(false); match.enabled = false; player.enabled = false;
            var weapon = WeaponConfigService.Current.Source(1); var ammo = weapon.ammoConfig;
            var savedAmmo = UnityEngine.Object.Instantiate(ammo); var savedWeapon = UnityEngine.Object.Instantiate(weapon);
            savedAmmo.name = ammo.name; savedWeapon.name = weapon.name;
            bool ammoDirty = EditorUtility.IsDirty(ammo), weaponDirty = EditorUtility.IsDirty(weapon);
            ParticleSystem replacementFlight = null, replacementMuzzle = null;
            AmmoConfigAsset replacementAmmo = null;
            try
            {
                uint revision = WeaponConfigService.Current.Revision(1);
                var prior = GameplayConfig.GetWeapon(1); var state = player.Snapshot.Value;
                state.AttackNeedsRelease = false; state.Health = 100; player.Snapshot.Value = state;
                uint heroRevision = state.HeroRevision;
                player.CharacterView.Present(state, .016f, state.SimulatedAt);
                player.CharacterView.Shot(0, 900001);
                var oldMuzzle = player.CharacterView.GetComponentInChildren<InkMuzzleEmitter>();
                Assert.That(oldMuzzle, Is.Not.Null);
                // Do not edit the weapon itself: the nested Ammo asset must trigger observation.
                using (var edited = new SerializedObject(ammo))
                {
                    edited.FindProperty("burstInterval").floatValue = .045f;
                    edited.FindProperty("particlesPerBurst").intValue = 3;
                    edited.FindProperty("visualLifetime").floatValue = .75f;
                    edited.FindProperty("muzzleInterval").floatValue = .09f;
                    edited.FindProperty("muzzleBurstCount").intValue = 17;
                    edited.FindProperty("maxRibbonGap").floatValue = .8f;
                    edited.FindProperty("satelliteSpread").floatValue = .02f;
                    edited.FindProperty("explosionEnabled").boolValue = true;
                    edited.FindProperty("explosionPrefab").objectReferenceValue = InkPresentation.Current.ImpactPrefab.gameObject;
                    edited.FindProperty("explosionRadius").floatValue = 3;
                    edited.FindProperty("explosionDamage").floatValue = 15;
                    edited.FindProperty("explosionPaint").boolValue = true;
                    edited.FindProperty("explosionPaintRadiusMin").floatValue = .3f;
                    edited.FindProperty("explosionPaintRadiusMax").floatValue = .7f;
                    edited.ApplyModifiedProperties();
                }
                yield return Applied(app, 1, revision);
                var current = GameplayConfig.GetWeapon(1);
                Assert.That(current.Ammo.SameValues(new AmmoRuntimeConfig(ammo)), Is.True);
                Assert.That(WeaponConfigService.Current.ForShot(1, revision), Is.SameAs(prior));
                Assert.That(prior.Ammo.MuzzleBurstCount, Is.EqualTo(savedAmmo.muzzleBurstCount));
                Assert.That(player.Snapshot.Value.HeroRevision, Is.EqualTo(heroRevision));
                Assert.That(player.Snapshot.Value.AttackNeedsRelease, Is.False, "Ammo edit must not cancel the action");
                var newMuzzle = player.CharacterView.GetComponentInChildren<InkMuzzleEmitter>();
                Assert.That(newMuzzle, Is.Not.SameAs(oldMuzzle)); Assert.That(newMuzzle.Ammo.MuzzleBurstCount, Is.EqualTo(17));
                Assert.That(newMuzzle.BurstCount, Is.Zero, "Reload alone must not synthesize another muzzle action");
                yield return Wait(() => oldMuzzle == null, "Retired muzzle drains naturally and is destroyed");
                revision = WeaponConfigService.Current.Revision(1);
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.SameValues(prior.Ammo), Is.True);
                revision = WeaponConfigService.Current.Revision(1); Undo.PerformRedo();
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.MuzzleBurstCount, Is.EqualTo(17));

                replacementFlight = UnityEngine.Object.Instantiate(ammo.flightPrefab); replacementFlight.name = ammo.flightPrefab.name;
                replacementMuzzle = UnityEngine.Object.Instantiate(ammo.muzzlePrefab); replacementMuzzle.name = ammo.muzzlePrefab.name;
                replacementFlight.gameObject.SetActive(false); replacementMuzzle.gameObject.SetActive(false);
                revision = WeaponConfigService.Current.Revision(1);
                ammo.flightPrefab = replacementFlight; ammo.muzzlePrefab = replacementMuzzle;
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.FlightPrefab, Is.SameAs(replacementFlight));
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.MuzzlePrefab, Is.SameAs(replacementMuzzle));

                replacementAmmo = UnityEngine.Object.Instantiate(ammo); replacementAmmo.name = ammo.name;
                revision = WeaponConfigService.Current.Revision(1); weapon.ammoConfig = replacementAmmo;
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.Source, Is.SameAs(replacementAmmo));
                var lastValid = GameplayConfig.GetWeapon(1);
                replacementAmmo.burstInterval = float.NaN;
                yield return Wait(() => app.WeaponDebugStatus.StartsWith("修改未应用"), "Invalid ammo is rejected");
                Assert.That(GameplayConfig.GetWeapon(1), Is.SameAs(lastValid));
                replacementAmmo.burstInterval = ammo.burstInterval;
                revision = WeaponConfigService.Current.Revision(1); weapon.ammoConfig = ammo;
                yield return Applied(app, 1, revision);
                yield return CapacityScenario(app, ammo);
                File.AppendAllText(Output + "/host-reload.txt", "PASS: nested Ammo scalars, same-name prefab replacement, Ammo replacement, Undo/Redo, old revision, action preservation, muzzle rebind, invalid edit rejection.\n");
            }
            finally
            {
                EditorUtility.CopySerialized(savedAmmo, ammo); EditorUtility.CopySerialized(savedWeapon, weapon);
                if (!ammoDirty) EditorUtility.ClearDirty(ammo); if (!weaponDirty) EditorUtility.ClearDirty(weapon);
                Undo.ClearUndo(ammo); UnityEngine.Object.Destroy(savedAmmo); UnityEngine.Object.Destroy(savedWeapon);
            }
            yield return app.Leave().ToCoroutine();
            if (replacementFlight != null) UnityEngine.Object.Destroy(replacementFlight.gameObject);
            if (replacementMuzzle != null) UnityEngine.Object.Destroy(replacementMuzzle.gameObject);
            if (replacementAmmo != null) UnityEngine.Object.Destroy(replacementAmmo);

            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            ushort port = (ushort)((System.Net.IPEndPoint)socket.LocalEndPoint).Port; socket.Close();
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom && !app.IsWeaponDebugRoom, Is.True, app.Error);
            var normal = GameplayConfig.GetWeapon(1); var normalSource = WeaponConfigService.Current.Source(1).ammoConfig;
            float savedInterval = normalSource.burstInterval;
            try
            {
                normalSource.burstInterval = .087f;
                for (int i = 0; i < 15; i++) { app.ApplyDebugWeaponChanges(); yield return null; }
                Assert.That(GameplayConfig.GetWeapon(1), Is.SameAs(normal));
                Assert.That(normal.Ammo.BurstInterval, Is.EqualTo(savedInterval));
                File.AppendAllText(Output + "/host-reload.txt", "PASS: normal multiplayer room remains frozen.\n");
            }
            finally { normalSource.burstInterval = savedInterval; }
            yield return app.Leave().ToCoroutine();
            yield return null;
            Assert.That(InkPresentation.Current, Is.Null, "Leaving room releases all version pools");
        }

        static IEnumerator CapacityScenario(PrototypeApp app, AmmoConfigAsset ammo)
        {
            var presentation = InkPresentation.Current; presentation.enabled = false; presentation.Clear();
            ammo.visualLifetime = 10;
            uint revision = WeaponConfigService.Current.Revision(1);
            yield return Applied(app, 1, revision);
            double born = PrototypeMatch.Current.NetworkManager.ServerTime.Time;
            uint round = PrototypeMatch.Current.State.Value.Round;
            for (int i = 0; i < InkPresentation.VersionPoolLimit; i++)
            {
                if (i > 0)
                {
                    revision = WeaponConfigService.Current.Revision(1); ammo.maxRibbonGap += .01f;
                    yield return Applied(app, 1, revision);
                }
                var config = GameplayConfig.GetWeapon(1);
                presentation.Spawn(new InkShot { Id = 0xf0000000u + (uint)i, Round = round, HeroId = 1,
                    Team = 1, Shooter = ulong.MaxValue, ActionId = (ulong)i + 1, Seed = 123,
                    Born = PrototypeMatch.Current.NetworkManager.ServerTime.Time, Origin = Vector3.one * 1000, Velocity = Vector3.forward * 32,
                    Configuration = config, ConfigurationRevision = WeaponConfigService.Current.Revision(1) });
            }
            Assert.That(presentation.VersionPoolCount, Is.EqualTo(InkPresentation.VersionPoolLimit));
            revision = WeaponConfigService.Current.Revision(1);
            ammo.maxRibbonGap = 2.5f;
            yield return Wait(() => { app.ApplyDebugWeaponChanges(); return app.WeaponDebugStatus.StartsWith("等待旧墨弹结束"); }, "Capacity waits");
            Assert.That(WeaponConfigService.Current.Revision(1), Is.EqualTo(revision));
            ammo.maxRibbonGap = 2.7f;
            yield return Wait(() => { app.ApplyDebugWeaponChanges(); return app.WeaponDebugStatus.StartsWith("等待旧墨弹结束"); }, "Latest pending edit");
            // Allow observation of the latest asset value before freeing a version slot.
            for (int i = 0; i < 4; i++) yield return null;
            presentation.Impact(new InkImpact { Round = round, Id = 0xf0000000u });
            yield return Applied(app, 1, revision);
            Assert.That(GameplayConfig.GetWeapon(1).Ammo.MaxRibbonGap, Is.EqualTo(2.7f));
            Assert.That(presentation.VersionPoolCount, Is.LessThanOrEqualTo(InkPresentation.VersionPoolLimit));
            presentation.Clear(); presentation.enabled = true;
            File.AppendAllText(Output + "/host-reload.txt", "PASS: 12 active versions block revision commit; latest pending edit applies after old shot completion.\n");
        }
    }
}
#endif
