#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class GameplayUpdatePlayTests
    {
        static async UniTask Wait(Func<bool> condition, string message)
        {
            try { await UniTask.WaitUntil(condition).Timeout(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { Assert.Fail(message); }
        }
        [UnityTest] public IEnumerator WarmupTeamChangeRespawnsAndRejectsFullDeadAndExpiredRequests()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            yield return Scenario().ToCoroutine();
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator ExitAfterFailure()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
        }
        static async UniTask Scenario()
        {
            await Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Addressables bootstrap");
            var app = PrototypeApp.Current;
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            EditorWindow activeView = null;
            try
            {
                await app.Connect(true, "127.0.0.1", port);
                Assert.That(app.InRoom, Is.True, app.Error);
                await Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current != null, "Host spawn");
                var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
                app.OpenHeroSelection(HeroSelectionOrigin.Warmup); player.RequestHeroChange(2, HeroSelectionOrigin.Warmup);
                await Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 2, "Hero selection");
                app.CloseOverlay(); app.CaptureMouse(false);
                var state = player.Snapshot.Value; state.Health = 34; state.Ink = 12;
                state.ChargeTicks = 30; state.WeaponPhase = WeaponPhase.Charging;
                state.LastDamageAt = state.InkRecoverAt = player.NetworkManager.ServerTime.Time + 100;
                player.Snapshot.Value = state;
                uint life = state.Revision, shots = state.ShotSequence; var visual = player.Visual;
                Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.RoomMenu));

                // Capture the real menu, then use its public request entry point. Physical
                // mouse interaction is separate from this deterministic host integration test.
                var view = Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault(w => w.GetType().Name == "GameView")
                    ?? EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                view.Show(); view.Focus(); view.Repaint();
                activeView = view; EditorApplication.update += view.Repaint;
                await UniTask.Delay(200);
                Directory.CreateDirectory("Reports/GameplayUpdate");
                ScreenCapture.CaptureScreenshot(Path.GetFullPath("Reports/GameplayUpdate/warmup-before-click.png"));
                await UniTask.Delay(100);
                Assert.That(player.TeamChangeUnavailableReason, Is.Null);
                player.RequestTeamChange();
                await Wait(() => player.Snapshot.Value.Team == 2 && !player.TeamChangePending, "Menu request switches to blue");
                var switched = player.Snapshot.Value;
                Assert.That(switched.Revision, Is.EqualTo(life + 1)); Assert.That(switched.HeroId, Is.EqualTo(2));
                Assert.That(switched.Health, Is.EqualTo(GameplayConfig.GetHero(2).MaxHealth));
                Assert.That(switched.Ink, Is.EqualTo(GameplayConfig.GetHero(2).MaxInk));
                Assert.That(switched.ChargeTicks, Is.Zero); Assert.That(switched.ShotSequence, Is.EqualTo(shots));
                Assert.That(Vector3.Distance(switched.Position, PrototypeArena.Spawn(2, switched.Slot)), Is.LessThan(.15));
                Assert.That(switched.Yaw, Is.EqualTo(180)); Assert.That(player.Visual, Is.SameAs(visual));
                Assert.That(switched.ProtectedUntil, Is.GreaterThan(player.NetworkManager.ServerTime.Time));
                Directory.CreateDirectory("Reports/GameplayUpdate");
                ScreenCapture.CaptureScreenshot(Path.GetFullPath("Reports/GameplayUpdate/warmup-team-menu.png"));
                await UniTask.Delay(300);

                // Additional spawned server actors exercise the real roster/slot allocator.
                // They are fixtures, not remote clients or physical LAN validation.
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
                match.AddPlayer(42, prefab);
                player.RequestTeamChange(); await Wait(() => !player.TeamChangePending && player.Snapshot.Value.Team == 1, "Switch creates 2:0");
                Assert.That(match.Players.Count(p => p.Snapshot.Value.Team == 1), Is.EqualTo(2));
                Assert.That(match.Players.Where(p => p.Snapshot.Value.Team == 1).Select(p => p.Snapshot.Value.Slot).Distinct().Count(), Is.EqualTo(2));
                match.StartRound(); Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Practice), "Single-team start rejected");
                match.AddPlayer(43, prefab);
                match.StartRound(); Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Playing), "2:1 start allowed");
                life = player.Snapshot.Value.Revision; player.RequestTeamChange();
                Assert.That(player.TeamChangePending, Is.False); Assert.That(player.Snapshot.Value.Revision, Is.EqualTo(life));
                var phase = match.State.Value; phase.Phase = MatchPhase.Finished; match.State.Value = phase; match.ReturnToRoom();
                for (ulong id=44;id<=48;id++) match.AddPlayer(id,prefab);
                Assert.That(player.TeamChangeUnavailableReason, Does.Contain("已满")); player.RequestTeamChange();
                Assert.That(player.TeamChangePending, Is.False); Assert.That(player.Snapshot.Value.Team, Is.EqualTo(1));
                var departing = match.Players.Last(p => p.Snapshot.Value.Team == 2); ulong departingId=departing.OwnerClientId;
                departing.NetworkObject.Despawn(); match.RemovePlayer(departingId);
                state = player.Snapshot.Value; state.Health = 0; state.RespawnsAt = player.NetworkManager.ServerTime.Time + 100; player.Snapshot.Value = state;
                Assert.That(player.TeamChangeUnavailableReason, Does.Contain("重生")); player.RequestTeamChange();
                Assert.That(player.TeamChangePending, Is.False);
                player.Respawn(); life = player.Snapshot.Value.Revision;
                player.RequestTeamChange(); player.RequestTeamChange(); // one queued request only
                player.Respawn(); // queued request's lifecycle is now stale
                await Wait(() => !player.TeamChangePending, "Stale lifecycle request receives rejection");
                Assert.That(player.Snapshot.Value.Revision, Is.EqualTo(life + 1)); Assert.That(player.Snapshot.Value.Team, Is.EqualTo(1));
                Assert.That(player.TeamChangeMessage, Does.Contain("已变化"));
                player.RequestTeamChange(); phase = match.State.Value; phase.Phase = MatchPhase.Playing; match.State.Value = phase;
                await Wait(() => !player.TeamChangePending, "Request arriving after start is rejected");
                Assert.That(player.Snapshot.Value.Team, Is.EqualTo(1)); Assert.That(player.TeamChangeMessage, Does.Contain("热身"));
                File.WriteAllText("Reports/GameplayUpdate/playmode-validation.txt",
                    "PASS: warmup menu request entry; authority reply; selected hero retained; team spawn, health/ink, protection and camera yaw reset; no shot during menu; 2:0 rejected, 2:1 starts; full/dead/duplicate/stale/phase rejection. Additional roster members are server fixtures. Physical mouse interaction, remote client and physical LAN remain separate acceptance.\n");
            }
            finally
            {
                if (activeView != null) EditorApplication.update -= activeView.Repaint;
                if (app != null && app.InRoom) await app.Leave();
            }
        }
    }
}
#endif
