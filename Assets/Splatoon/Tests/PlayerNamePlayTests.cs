#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class PlayerNamePlayTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const string Output = "Reports/PlayerNames/Visual";
        const string PreferenceBackup = "Temp/PlayerNames/preference.json";
        [Serializable] public sealed class SavedPreference { public bool had; public string name; }
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnityTest] public IEnumerator HostAdmissionNamesSurviveLifecycleAndLobbyReload()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferenceBackup));
            if (!File.Exists(PreferenceBackup)) File.WriteAllText(PreferenceBackup, JsonUtility.ToJson(new SavedPreference
                { had = PlayerPrefs.HasKey(PlayerNames.PreferenceKey), name = PlayerPrefs.GetString(PlayerNames.PreferenceKey) }));
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Restart Boot");
            Assert.That(PrototypeApp.Current.SavedUsername, Is.EqualTo("下局玩家"));
            yield return new ExitPlayMode();
        }

        static IEnumerator Scenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current;
            Assert.That(app.SaveUsername(" 墨水房主 "), Is.True);
            Assert.That(app.SavedUsername, Is.EqualTo("墨水房主"));
            Assert.That(app.SaveUsername("\n"), Is.False);
            Assert.That(app.SaveUsername("墨水房主"), Is.True);
            yield return Capture("lobby");
            ushort port; using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var connect = app.Connect(true, "127.0.0.1", port);
            yield return Wait(() => !app.Busy, "Host connect"); connect.GetAwaiter().GetResult();
            Assert.That(app.InRoom, Is.True, app.Error);
            var local = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            Assert.That(local.DisplayName, Is.EqualTo("墨水房主"));
            Assert.That(local.Username.CanClientWrite(1), Is.False);
            Assert.That(app.SaveUsername("比赛中改名"), Is.False);
            var signature = (byte[])typeof(PrototypeApp).GetField("_signature", Private).GetValue(app);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            void Admit(ulong id, string name)
            {
                var response = new NetworkManager.ConnectionApprovalResponse();
                app.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest
                    { ClientNetworkId = id, Payload = PlayerConnectionPayload.Encode(signature, name) }, response);
                Assert.That(response.Approved, Is.True, response.Reason); match.AddPlayer(id, prefab);
            }
            Admit(1, "其他玩家名字一二三四五六七八九十");
            var remote = match.Players.Single(p => p.OwnerClientId == 1);
            Assert.That(remote.DisplayName, Is.EqualTo("其他玩家名字一二三四五六七八九十"));
            remote.Respawn(); Assert.That(remote.DisplayName, Does.StartWith("其他玩家"));
            local.RequestHeroChange(local.Snapshot.Value.HeroId == 1 ? 2 : 1, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !local.HeroChangePending, "Hero change");
            Assert.That(local.DisplayName, Is.EqualTo("墨水房主"));
            match.StartRound(); yield return null;
            Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Playing));
            Assert.That(remote.DisplayName, Does.StartWith("其他玩家"));
            yield return Wait(() => !PrototypeApp.StartNoticeActive(match.State.Value, app.Manager.ServerTime.Time), "Start notice ends");
            var oldKeyboard = Keyboard.current;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
                app.CaptureMouse(true);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Tab)); InputSystem.Update();
                Assert.That(app.ScoreboardVisible, Is.True);
                yield return Capture("scoreboard");
                var style = (GUIStyle)typeof(PrototypeApp).GetField("_scoreName", Private).GetValue(app);
                Assert.That(style.richText, Is.False);
                string fitted = PrototypeApp.FitPlayerName(remote.DisplayName, "  ·  你", style, 300);
                Assert.That(style.CalcSize(new GUIContent(fitted)).x, Is.LessThanOrEqualTo(300));
                Assert.That(fitted, Does.EndWith("  ·  你"));
            }
            finally { InputSystem.RemoveDevice(keyboard); if (oldKeyboard != null) oldKeyboard.MakeCurrent(); }
            var state = match.State.Value; state.Phase = MatchPhase.Finished; match.State.Value = state;
            match.ReturnToRoom(); yield return null;
            Assert.That(remote.DisplayName, Does.StartWith("其他玩家"));
            // Freeze simulation and pose the two real network objects for a readable name-tag capture.
            match.enabled = false; local.enabled = false; remote.enabled = false;
            app.CaptureMouse(true);
            remote.transform.position = new Vector3(0, 100, 10);
            remote.CharacterView.Animator.Update(0);
            var camera = Camera.main;
            camera.transform.SetPositionAndRotation(new Vector3(0, 101.5f, 4), Quaternion.identity);
            yield return null;
            Assert.That(PrototypeApp.TryNameTagPosition(camera, remote.NameHeadPosition, remote.NameTagPosition, out _), Is.True);
            yield return Capture("remote-name");
            var remoteState = remote.Snapshot.Value; remoteState.Swimming = true; remote.Snapshot.Value = remoteState;
            Assert.That(PrototypeApp.NameTagEligible(false, remote.PresentedState), Is.False);
            yield return Capture("swim-name-hidden");
            remoteState.Swimming = false; remote.Snapshot.Value = remoteState;
            // A disconnect must remove admission metadata before another join is assigned a name.
            remote.NetworkObject.Despawn();
            typeof(PrototypeApp).GetMethod("ClientDisconnected", Private).Invoke(app, new object[] { 1UL });
            Assert.That(app.AdmittedUsername(1), Is.EqualTo("玩家 2"));
            Admit(1, "重新加入"); Assert.That(match.Players.Single(p => p.OwnerClientId == 1).DisplayName, Is.EqualTo("重新加入"));
            var leave = app.Leave(); yield return Wait(() => !app.Busy, "Leave"); leave.GetAwaiter().GetResult();
            Assert.That(PlayerNames.Load(), Is.EqualTo("墨水房主"));
            Assert.That(app.SaveUsername("下局玩家"), Is.True);
        }

        static IEnumerator Capture(string name)
        {
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            Directory.CreateDirectory(Output);
            string path = Path.GetFullPath(Output + "/" + name + ".png");
            if (File.Exists(path)) File.Delete(path);
            yield return null;
            ScreenCapture.CaptureScreenshot(path);
            yield return null; yield return Wait(() => File.Exists(path), "Screenshot " + name);
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (File.Exists(PreferenceBackup))
            {
                var saved = JsonUtility.FromJson<SavedPreference>(File.ReadAllText(PreferenceBackup));
                if (saved.had) PlayerPrefs.SetString(PlayerNames.PreferenceKey, saved.name); else PlayerPrefs.DeleteKey(PlayerNames.PreferenceKey);
                PlayerPrefs.Save(); File.Delete(PreferenceBackup);
            }
        }
    }
}
#endif
