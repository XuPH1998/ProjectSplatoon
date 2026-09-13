#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Prototype;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class HeroMigrationPlayTests
    {
        static IEnumerator Wait(Func<bool> condition, string message, double seconds = 15)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(condition(), Is.True, message);
        }
        [UnityTest] public IEnumerator RealAppLoadsSelectsRespawnsAndReleasesHeroModels()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            // Create coroutine closures after the domain reload performed by EnterPlayMode.
            yield return RunRoomScenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator RunRoomScenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Real Addressables bootstrap");
            var app = PrototypeApp.Current;
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error); Assert.That(app.Heroes.AssetCount, Is.EqualTo(2));
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current != null, "Host local player spawn");
            var player = PrototypePlayer.Local;
            var match = PrototypeMatch.Current;
            Assert.That(player, Is.Not.Null, "Local player after spawn wait");
            Assert.That(match, Is.Not.Null, "Match after spawn wait");
            Assert.That(player.Visual, Is.Not.Null, "Hero visual after spawn");
            Assert.That(player.NetworkObject, Is.Not.Null, "Player network root after spawn");
            var firstVisual = player.Visual;
            var playerId = player.NetworkObjectId;
            Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(1));
            app.OpenHeroSelection(HeroSelectionOrigin.Warmup); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
            for (int hero = 2; hero <= 5; hero++)
            {
                var before = player.Snapshot.Value; before.Ink = 20; before.InkRecoverAt = player.NetworkManager.ServerTime.Time + 100; player.Snapshot.Value = before;
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Select hero " + hero);
                Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(100)); Assert.That(player.Snapshot.Value.Revision, Is.EqualTo(before.Revision));
                Assert.That(player.Snapshot.Value.ShotSequence, Is.EqualTo(before.ShotSequence));
                Assert.That(player.Visual, Is.SameAs(firstVisual)); Assert.That(player.NetworkObjectId, Is.EqualTo(playerId));
                Assert.That(player.CharacterView.GetComponentsInChildren<HeroWeaponBindings>(true).Length, Is.EqualTo(1));
                Assert.That(player.BoundVisualPrefab, Is.SameAs(app.Heroes.Get(hero).CharacterPrefab));
                Assert.That(player.CharacterView.BoundWeaponPrefab, Is.SameAs(app.Heroes.Get(hero).WeaponPrefab));
            }
            Directory.CreateDirectory("Logs/HeroMigration");
            var phase = match.State.Value; phase.Phase = MatchPhase.Playing; phase.EndsAt = player.NetworkManager.ServerTime.Time + 100; match.State.Value = phase;
            var state = player.Snapshot.Value; state.Ink = 20; state.Health = 70; state.LastDamageAt = player.NetworkManager.ServerTime.Time; state.InkRecoverAt = player.NetworkManager.ServerTime.Time + 100;
            uint life = state.Revision; player.Snapshot.Value = state;
            app.OpenHeroSelection(HeroSelectionOrigin.Debug); player.RequestHeroChange(2, HeroSelectionOrigin.Debug);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 2, "DEBUG selects hero");
            Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(20)); Assert.That(player.Snapshot.Value.Health, Is.EqualTo(70));
            Assert.That(player.Snapshot.Value.Revision, Is.EqualTo(life));
            player.RequestHeroChange(999, HeroSelectionOrigin.Debug);
            yield return Wait(() => !player.HeroChangePending, "Invalid hero request replied");
            Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(2)); Assert.That(player.HeroChangeMessage, Does.Contain("不存在"));
            // Test-only table variation proves respawn reads the selected row, not the default hero.
            HeroMigrationTests.Load(rows => { rows[1]["maxHealth"] = 80; rows[1]["maxInk"] = 60; });
            player.Respawn(); Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(2));
            Assert.That(player.Snapshot.Value.Health, Is.EqualTo(80)); Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(60));
            HeroMigrationTests.Load();
            yield return app.Leave().ToCoroutine(); Assert.That(app.Heroes.AssetCount, Is.Zero); Assert.That(PrototypePlayer.Local, Is.Null);
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine(); Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null, "Reconnected host local player spawn");
            Assert.That(PrototypePlayer.Local.Snapshot.Value.HeroId, Is.EqualTo(1));
            yield return app.Leave().ToCoroutine();
            File.WriteAllText("Logs/HeroMigration/play-validation.txt", "PASS: real Addressables + NGO host; 5 hero models; warmup/DEBUG/invalid selection; selected-hero respawn; leave/reenter default; resource release.\nRemote client and physical LAN are separate acceptance items.\n");
            yield return HeroUiSmoke.RunInEditorAsync(port).ToCoroutine();
        }
    }
}
#endif
