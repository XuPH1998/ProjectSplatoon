#if UNITY_EDITOR
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class HeroChangeZonePlayTests
    {
        static IEnumerator Wait(Func<bool> predicate, string message)
        {
            double end = Time.realtimeSinceStartupAsDouble + 20;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(predicate(), Is.True, message);
        }
        static void OverlayUpdate(PrototypeApp app) => typeof(PrototypeApp).GetMethod("UpdateOverlayInput", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
        static void Press(PrototypeApp app, Keyboard keyboard, Key key)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key)); InputSystem.Update(); OverlayUpdate(app);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
        }
        [UnityTest] public IEnumerator HostEnforcesSpawnAreaAndKeepsHeroStateThroughInputsAndRespawn()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        [UnityTest] public IEnumerator SpawnAreaUiAtUltrawide()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            HeroUiSmoke.RequestedWidth = 2560; HeroUiSmoke.RequestedHeight = 1080;
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            try { yield return HeroUiSmoke.RunInEditorAsync(port).ToCoroutine(); }
            finally { HeroUiSmoke.RequestedWidth = 1280; HeroUiSmoke.RequestedHeight = 720; }
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Addressables ready");
            var app = PrototypeApp.Current;
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            var keyboard = InputSystem.AddDevice<Keyboard>(); var mouse = InputSystem.AddDevice<Mouse>();
            try
            {
                var phase = match.State.Value; phase.Phase = MatchPhase.Playing; phase.Round++; phase.EndsAt = app.Manager.ServerTime.Time + 300;
                match.State.Value = phase; player.Respawn();
                OverlayUpdate(app);
                Assert.That(app.HeroSelectionUnavailableReason(HeroSelectionOrigin.SpawnArea), Is.Null);
                Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
                Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game));
                Press(app, keyboard, Key.H); Press(app, keyboard, Key.Escape); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game));

                // The real server RPC processes a change while the selection UI is open.
                Press(app, keyboard, Key.H);
                var state = player.Snapshot.Value; state.Health = 70; state.Ink = 20; state.InkRecoverAt = app.Manager.ServerTime.Time + 100;
                state.LastDamageAt = app.Manager.ServerTime.Time + 100; player.Snapshot.Value = state;
                player.RequestHeroChange(2, HeroSelectionOrigin.SpawnArea);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 2, "Spawn switch applied through RPC");
                var changed = player.Snapshot.Value;
                Assert.That(changed.Health, Is.EqualTo(70)); Assert.That(changed.Ink, Is.EqualTo(20));
                Assert.That(changed.ProtectedUntil, Is.EqualTo(state.ProtectedUntil)); Assert.That(changed.Revision, Is.EqualTo(state.Revision));
                Assert.That(Vector3.Distance(changed.Position, state.Position), Is.LessThan(.15f));
                Assert.That(player.BoundVisualPrefab, Is.SameAs(app.Heroes.Get(2).CharacterPrefab));

                // Moving away closes the overlay and keeps a held mouse click blocked until release.
                InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Left)); InputSystem.Update();
                state = player.Snapshot.Value; state.Position = new Vector3(10, .05f, -20); player.Snapshot.Value = state;
                OverlayUpdate(app); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game));
                Assert.That((bool)typeof(PrototypeApp).GetField("_fireInputBlocked", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app), Is.True);
                Assert.That(app.CanFireInput, Is.False);
                InputSystem.QueueStateEvent(mouse, new MouseState()); InputSystem.Update(); OverlayUpdate(app);
                Assert.That((bool)typeof(PrototypeApp).GetField("_fireInputBlocked", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app), Is.False);
                Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game));
                player.RequestHeroChange(3, HeroSelectionOrigin.SpawnArea);
                yield return Wait(() => !player.HeroChangePending, "Outside area request rejected");
                Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(2)); Assert.That(player.HeroChangeMessage, Does.Contain("本方出生区"));

                // Authority checks the position at application, after a request has been queued.
                player.Respawn();
                player.RequestHeroChange(3, HeroSelectionOrigin.SpawnArea);
                state = player.Snapshot.Value; state.Position = new Vector3(10, .05f, -20); player.Snapshot.Value = state;
                player.Simulate(1f / 60, app.Manager.ServerTime.Time, MatchPhase.Playing);
                yield return Wait(() => !player.HeroChangePending, "Queued request revalidated");
                Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(2));
                Assert.That(player.HeroChangeMessage, Does.Contain("本方出生区"));

                state = player.Snapshot.Value; state.Position = PrototypeArena.Spawn((byte)(3 - state.Team), 0); player.Snapshot.Value = state;
                Assert.That(app.HeroSelectionUnavailableReason(HeroSelectionOrigin.SpawnArea), Does.Contain("本方出生区"));
                Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game));

                player.Respawn(); Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
                state = player.Snapshot.Value; state.Health = 0; state.RespawnsAt = app.Manager.ServerTime.Time + 10; player.Snapshot.Value = state;
                OverlayUpdate(app); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game));
                player.RequestHeroChange(3, HeroSelectionOrigin.SpawnArea);
                yield return Wait(() => !player.HeroChangePending, "Dead request rejected");
                Assert.That(player.HeroChangeMessage, Does.Contain("重生"));
                player.Respawn(); Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
                phase = match.State.Value; phase.Phase = MatchPhase.Finished; match.State.Value = phase;
                OverlayUpdate(app); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.RoomMenu));
                Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.RoomMenu));

                match.ReturnToRoom();
                // Warmup remains unrestricted; changing team gives access only to the new base.
                player.RequestTeamChange();
                yield return Wait(() => !player.TeamChangePending && player.Snapshot.Value.Team == 2, "Switch team in warmup");
                state = player.Snapshot.Value; state.Position = new Vector3(10, .05f, 20); player.Snapshot.Value = state;
                OverlayUpdate(app); Press(app, keyboard, Key.H); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
                phase = match.State.Value; phase.Phase = MatchPhase.Playing; phase.EndsAt = app.Manager.ServerTime.Time + 300; match.State.Value = phase;
                player.Respawn(); OverlayUpdate(app); Press(app, keyboard, Key.H);
                Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
                player.RequestHeroChange(3, HeroSelectionOrigin.SpawnArea);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 3, "Blue spawn switch applied");
                Press(app, keyboard, Key.Escape);
                state = player.Snapshot.Value; state.Position = new Vector3(10, .05f, 20); player.Snapshot.Value = state;
                app.OpenHeroSelection(HeroSelectionOrigin.Debug); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
                app.CloseOverlay(); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Debug));
            }
            finally { InputSystem.RemoveDevice(keyboard); InputSystem.RemoveDevice(mouse); }
            yield return app.Leave().ToCoroutine();
        }
    }
}
#endif
