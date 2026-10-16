#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Prototype;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class AmmoDebugPlayTests
    {
        const string Output = "Reports/InkFlightReference/AmmoMerge";
        static readonly List<UnityEngine.Object> Fixtures = new();
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
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying && PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            foreach (var fixture in Fixtures)
                if (fixture != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(fixture);
                    else UnityEngine.Object.DestroyImmediate(fixture);
                }
            Fixtures.Clear();
            if (!Application.isPlaying) yield break;
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
            app.CaptureMouse(false);
            bool matchEnabled = match.enabled, playerEnabled = player.enabled;
            var presentation = InkPresentation.Current; bool presentationEnabled = presentation.enabled;
            match.enabled = false; player.enabled = false; presentation.enabled = false; presentation.Clear();
            Assert.That(presentation.BlockMode, Is.True, "Ammo debug uses the default block presentation");
            var weapon = WeaponConfigService.Current.Source(1); var ammo = weapon.ammoConfig;
            var savedAmmo = UnityEngine.Object.Instantiate(ammo); var savedWeapon = UnityEngine.Object.Instantiate(weapon);
            Fixtures.Add(savedAmmo); Fixtures.Add(savedWeapon);
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
                var oldShot = PaidDebugShot(player, match, 0xe0000001u);
                presentation.Spawn(oldShot);
                var blockFlight = BlockFlightHistoryAssertions.Flight(presentation);
                BlockFlightHistoryAssertions.AssertFrozen(presentation, oldShot, prior);
                Assert.That(player.CharacterView.GetComponentsInChildren<InkMuzzleEmitter>(true), Is.Empty,
                    "Default block firing cannot revive the old particle muzzle");
                uint paidSequence = player.Snapshot.Value.ShotSequence;
                float paidMaterial = player.Snapshot.Value.Ink;
                var terrain = match.Blocks.ExportSnapshot();
                int active = blockFlight.ActiveShots, debris = blockFlight.DebrisCount;
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
                BlockFlightHistoryAssertions.AssertFrozen(presentation, oldShot, prior);
                Assert.That(blockFlight.ActiveShots, Is.EqualTo(active), "Reload alone cannot synthesize another block shot");
                Assert.That(blockFlight.DebrisCount, Is.EqualTo(debris), "Reload alone cannot synthesize fragments");
                Assert.That(player.Snapshot.Value.ShotSequence, Is.EqualTo(paidSequence));
                Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(paidMaterial));
                CollectionAssert.AreEqual(terrain, match.Blocks.ExportSnapshot(), "Reload alone cannot build or dismantle terrain");
                Assert.That(presentation.VersionPoolCount, Is.Zero);
                var newShot = PaidDebugShot(player, match, 0xe0000002u);
                presentation.Spawn(newShot);
                BlockFlightHistoryAssertions.AssertFrozen(presentation, newShot, current);
                BlockFlightHistoryAssertions.AssertFrozen(presentation, oldShot, prior);
                Assert.That(newShot.Configuration.Ammo.MuzzleBurstCount, Is.EqualTo(17));
                Assert.That(blockFlight.ActiveShots, Is.EqualTo(active + 1));
                Assert.That(BlockFlightHistoryAssertions.Flight(presentation), Is.SameAs(blockFlight), "Ammo revisions share the block renderer");
                Assert.That(player.CharacterView.GetComponentsInChildren<InkMuzzleEmitter>(true), Is.Empty,
                    "Reloaded Ammo keeps the default block muzzle path");
                revision = WeaponConfigService.Current.Revision(1);
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.SameValues(prior.Ammo), Is.True);
                revision = WeaponConfigService.Current.Revision(1); Undo.PerformRedo();
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.MuzzleBurstCount, Is.EqualTo(17));

                replacementFlight = UnityEngine.Object.Instantiate(ammo.flightPrefab); replacementFlight.name = ammo.flightPrefab.name;
                replacementMuzzle = UnityEngine.Object.Instantiate(ammo.muzzlePrefab); replacementMuzzle.name = ammo.muzzlePrefab.name;
                Fixtures.Add(replacementFlight.gameObject); Fixtures.Add(replacementMuzzle.gameObject);
                replacementFlight.gameObject.SetActive(false); replacementMuzzle.gameObject.SetActive(false);
                revision = WeaponConfigService.Current.Revision(1);
                ammo.flightPrefab = replacementFlight; ammo.muzzlePrefab = replacementMuzzle;
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.FlightPrefab, Is.SameAs(replacementFlight));
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.MuzzlePrefab, Is.SameAs(replacementMuzzle));

                replacementAmmo = UnityEngine.Object.Instantiate(ammo); replacementAmmo.name = ammo.name;
                Fixtures.Add(replacementAmmo);
                revision = WeaponConfigService.Current.Revision(1); weapon.ammoConfig = replacementAmmo;
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.Source, Is.SameAs(replacementAmmo));
                var lastValid = GameplayConfig.GetWeapon(1);
                uint lastValidRevision = WeaponConfigService.Current.Revision(1);
                replacementAmmo.burstInterval = float.NaN;
                yield return Wait(() => app.WeaponDebugStatus.StartsWith("修改未应用"), "Invalid ammo is rejected");
                Assert.That(GameplayConfig.GetWeapon(1), Is.SameAs(lastValid));
                Assert.That(WeaponConfigService.Current.Revision(1), Is.EqualTo(lastValidRevision));
                replacementAmmo.burstInterval = ammo.burstInterval;
                revision = WeaponConfigService.Current.Revision(1); weapon.ammoConfig = ammo;
                yield return Applied(app, 1, revision);
                yield return CapacityScenario(app, ammo);
                File.AppendAllText(Output + "/host-reload.txt", "PASS: nested Ammo scalars, same-name prefab replacement, Ammo replacement, Undo/Redo, frozen old block shot and trajectory, new paid debug-shot revision, reload has no shot/material/terrain effects, action preservation, invalid edit rejection.\n");
            }
            finally
            {
                EditorUtility.CopySerialized(savedAmmo, ammo); EditorUtility.CopySerialized(savedWeapon, weapon);
                if (!ammoDirty) EditorUtility.ClearDirty(ammo); if (!weaponDirty) EditorUtility.ClearDirty(weapon);
                Undo.ClearUndo(ammo); UnityEngine.Object.Destroy(savedAmmo); UnityEngine.Object.Destroy(savedWeapon);
                if (presentation != null) { presentation.Clear(); presentation.enabled = presentationEnabled; }
                if (match != null) match.enabled = matchEnabled;
                if (player != null) player.enabled = playerEnabled;
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
            Assert.That(InkPresentation.Current, Is.Null, "Leaving room releases the block renderer and retained revisions");
        }

        static InkShot PaidDebugShot(PrototypePlayer player, PrototypeMatch match, uint id)
        {
            // The debug room isolates simulation while checking reload history. Pay
            // through the real weapon state machine, then submit its visual event.
            var config = GameplayConfig.GetWeapon(1); var state = player.Snapshot.Value;
            float material = state.Ink; uint sequence = state.ShotSequence;
            double now = Math.Max(match.NetworkManager.ServerTime.Time, state.NextShotAt);
            state.WeaponPhase = WeaponPhase.Idle; state.AttackNeedsRelease = false;
            var input = new PlayerInputFrame { Sequence = state.FireBurstSequence + 1,
                Fire = true, FireSequence = state.ConsumedFire + 1, ReleaseSequence = state.ConsumedRelease };
            bool fired = WeaponSimulation.Step(ref state, input, config, now, false, true, out var result);
            if (!fired)
            {
                now += config.StartSeconds + 1.0 / 60;
                fired = WeaponSimulation.Step(ref state, input, config, now, false, true, out result);
            }
            Assert.That(fired, Is.True, "Debug history sample uses an accepted paid weapon emission");
            Assert.That(state.ShotSequence, Is.EqualTo(sequence + 1));
            Assert.That(material - state.Ink, Is.EqualTo(WeaponSimulation.InkCost(config, result.Charge)).Within(.003f));
            state.SimulatedAt = now; player.Snapshot.Value = state;
            var geometry = WeaponLaunch.Geometry(new Vector3(1000, 1000, 995), Vector3.forward,
                new Vector3(1000, 1000, 1000), config, result.Charge);
            var shot = WeaponLaunch.Representative(geometry, config, result.Charge);
            shot.Id = id; shot.ActionId = result.ActionId; shot.ShotSequence = state.ShotSequence;
            shot.Round = match.State.Value.Round; shot.Born = now; shot.Team = state.Team;
            shot.HeroId = 1; shot.Shooter = player.PlayerId; shot.HeroRevision = state.HeroRevision;
            shot.Lifecycle = state.Revision; shot.ConfigurationRevision = WeaponConfigService.Current.Revision(1);
            return shot;
        }

        static IEnumerator CapacityScenario(PrototypeApp app, AmmoConfigAsset ammo)
        {
            var presentation = InkPresentation.Current; bool enabled = presentation.enabled;
            presentation.enabled = false; presentation.Clear();
            var shots = new List<InkShot>();
            try
            {
                ammo.visualLifetime = 10;
                uint revision = WeaponConfigService.Current.Revision(1);
                yield return Applied(app, 1, revision);
                uint round = PrototypeMatch.Current.State.Value.Round;
                BlockFlightPresentation renderer = null; Mesh mesh = null;
                // Keep the isolated presentation clock used by the original test.
                // Each retained shot is sampled at its own Born + .03, independently
                // of the real time spent editing thirteen different revisions.
                for (int i = 0; i <= InkPresentation.VersionPoolLimit; i++)
                {
                    if (i > 0)
                    {
                        revision = WeaponConfigService.Current.Revision(1); ammo.maxRibbonGap += .01f;
                        yield return Applied(app, 1, revision);
                    }
                    var config = GameplayConfig.GetWeapon(1);
                    var shot = new InkShot { Id = 0xf0000000u + (uint)i, Round = round, HeroId = 1,
                        Team = 1, Shooter = ulong.MaxValue, ActionId = (ulong)i + 1, Seed = 123,
                        Born = PrototypeMatch.Current.NetworkManager.ServerTime.Time,
                        Origin = Vector3.one * 1000, Velocity = Vector3.forward * 32,
                        Configuration = config, ConfigurationRevision = WeaponConfigService.Current.Revision(1) };
                    shots.Add(shot); presentation.Spawn(shot);
                    renderer ??= BlockFlightHistoryAssertions.Flight(presentation); mesh ??= renderer.CubeMesh;
                    Assert.That(BlockFlightHistoryAssertions.Flight(presentation), Is.SameAs(renderer));
                    Assert.That(renderer.CubeMesh, Is.SameAs(mesh), "Every Ammo revision shares the same cube mesh");
                    Assert.That(renderer.ActiveShots, Is.EqualTo(i + 1), "Legal revisions cannot drop older block flights");
                    Assert.That(renderer.Dropped, Is.Zero);
                    Assert.That(presentation.VersionPoolCount, Is.Zero, "Block revisions do not allocate old particle version pools");
                    foreach (var retained in shots)
                        BlockFlightHistoryAssertions.AssertFrozen(presentation, retained, retained.Configuration);
                }
                revision = WeaponConfigService.Current.Revision(1); ammo.maxRibbonGap = 2.5f;
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.MaxRibbonGap, Is.EqualTo(2.5f));
                revision = WeaponConfigService.Current.Revision(1); ammo.maxRibbonGap = 2.7f;
                yield return Applied(app, 1, revision);
                Assert.That(GameplayConfig.GetWeapon(1).Ammo.MaxRibbonGap, Is.EqualTo(2.7f));
                Assert.That(renderer.ActiveShots, Is.EqualTo(shots.Count), "Latest edit cannot retire retained shots");
                foreach (var retained in shots)
                    BlockFlightHistoryAssertions.AssertFrozen(presentation, retained, retained.Configuration);
                Assert.That(presentation.Streams, Is.Empty); Assert.That(presentation.VersionPoolCount, Is.Zero);
                double expiredAt = shots.Max(s => s.Born + s.Configuration.Lifetime) + .1;
                presentation.UpdateFlights(expiredAt, Camera.main);
                Assert.That(renderer.ActiveShots, Is.Zero, "Each block flight expires at its own frozen weapon lifetime");
                Assert.That(presentation.ActiveShots, Is.Zero);
                presentation.ClearFlights(expiredAt);
                foreach (var shot in shots) presentation.Spawn(shot);
                Assert.That(renderer.ActiveShots, Is.Zero, "Old events cannot revive flights across the clear cutoff");
                File.AppendAllText(Output + "/host-reload.txt", "PASS: 13 retained Ammo revisions share one block renderer and mesh, preserve individual snapshots/trajectories, apply latest legal edits without particle-pool blocking, expire and reject stale replay. The presentation clock is isolated; the 2048-shot hard limit is covered by BlockPresentationTests.\n");
            }
            finally { presentation.Clear(); presentation.enabled = enabled; }
        }
    }

    // Read-only test inspection keeps production APIs unchanged while proving that
    // the retained visual, configuration history and actual trajectory agree.
    internal static class BlockFlightHistoryAssertions
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static BlockFlightPresentation Flight(InkPresentation presentation)
        {
            var flight = (BlockFlightPresentation)typeof(InkPresentation).GetField("_blocks", Fields).GetValue(presentation);
            Assert.That(flight, Is.Not.Null, "The block renderer has accepted a real visual event");
            return flight;
        }
        public static InkShot Retained(InkPresentation presentation, uint round, uint id)
        {
            var flight = Flight(presentation);
            var visuals = (Array)typeof(BlockFlightPresentation).GetField("_shots", Fields).GetValue(flight);
            var shotField = visuals.GetType().GetElementType().GetField("Shot", Fields);
            for (int index = 0; index < flight.ActiveShots; index++)
            {
                var shot = (InkShot)shotField.GetValue(visuals.GetValue(index));
                if (shot.Round == round && shot.Id == id) return shot;
            }
            Assert.Fail("Missing retained block visual " + round + ":" + id);
            return default;
        }
        public static void AssertFrozen(InkPresentation presentation, InkShot original, WeaponRuntimeConfig expected)
        {
            var retained = Retained(presentation, original.Round, original.Id);
            Assert.That(retained.Configuration, Is.SameAs(expected));
            Assert.That(retained.ConfigurationRevision, Is.EqualTo(original.ConfigurationRevision));
            Assert.That(WeaponConfigService.Current.ForShot(original.HeroId, original.ConfigurationRevision), Is.SameAs(expected));
            Assert.That(Flight(presentation).TryPosition(original.Round, original.Id, original.Born + .03, out var position), Is.True);
            Assert.That(Vector3.Distance(position, InkBallistics.Position(original, expected, .03)), Is.LessThan(.0001f),
                "The old flight evaluates its immutable weapon snapshot after reload");
        }
    }
}
#endif
