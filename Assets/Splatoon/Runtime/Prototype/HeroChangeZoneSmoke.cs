#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in two-process transport acceptance, enabled only by -heroZoneRole.</summary>
    public sealed class HeroChangeZoneSmoke : MonoBehaviour
    {
        readonly List<string> _checks = new();
        readonly List<string> _errors = new();
        bool _host;
        string _output;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            if (!Environment.GetCommandLineArgs().Contains("-heroZoneRole") || FindFirstObjectByType<HeroChangeZoneSmoke>() != null) return;
            var go = new GameObject("Hero change zone transport validation"); DontDestroyOnLoad(go); go.AddComponent<HeroChangeZoneSmoke>();
        }
        void Check(bool value, string label) { if (!value) throw new Exception(label); _checks.Add(label); }
        static async UniTask Wait(Func<bool> condition) => await UniTask.WaitUntil(condition).Timeout(TimeSpan.FromSeconds(45));
        void OnLog(string message, string stack, LogType type)
        { if ((type == LogType.Error || type == LogType.Exception) && _errors.Count < 10) _errors.Add(message); }
        async UniTaskVoid Start()
        {
            _host = HeroSelectionSmoke.Arg("-heroZoneRole", "host") == "host";
            _output = HeroSelectionSmoke.Arg("-heroZoneOutput", "Reports/HeroChangeZone/" + (_host ? "host" : "client") + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
            Application.logMessageReceived += OnLog;
            string error = null;
            try
            {
                await Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
                var app = PrototypeApp.Current;
                for (int i = 0; i < 8; i++)
                {
                    await app.Connect(_host, "127.0.0.1", ushort.Parse(HeroSelectionSmoke.Arg("-heroZonePort", "18543")));
                    if (app.InRoom) break;
                    await UniTask.Delay(500);
                }
                Check(app.InRoom, "real transport connected: " + app.Error);
                await Wait(() => PrototypePlayer.ByOwner.Count == 2);
                if (_host)
                {
                    var match = PrototypeMatch.Current; match.StartRound();
                    foreach (var player in match.Players)
                    {
                        var state = player.Snapshot.Value; state.Health = 70; state.Ink = 20;
                        state.LastDamageAt = state.InkRecoverAt = app.Manager.ServerTime.Time + 200;
                        player.Snapshot.Value = state;
                    }
                }
                await Wait(() => PrototypeMatch.Current.State.Value.Phase == MatchPhase.Playing && PrototypePlayer.Local.Snapshot.Value.Ink == 20);
                if (_host) await UniTask.WhenAll(AuthorityTransitions(), LocalScenario());
                else await LocalScenario();
                Check(_errors.Count == 0, "no runtime errors: " + string.Join(" | ", _errors));
            }
            catch (Exception e) { error = e.ToString(); }
            Application.logMessageReceived -= OnLog;
            File.WriteAllText(_output, "passed=" + (error == null) + "\nrole=" + (_host ? "host" : "client") + "\n" + string.Join("\n", _checks) + "\nerror=" + error);
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) await PrototypeApp.Current.Leave();
            Application.Quit(error == null ? 0 : 1);
        }
        static bool EveryoneHas(int hero) => PrototypeMatch.Current.Players.Count == 2 && PrototypeMatch.Current.Players.All(p => p.Snapshot.Value.HeroId == hero);
        async UniTask AuthorityTransitions()
        {
            var match = PrototypeMatch.Current;
            await Wait(() => EveryoneHas(2)); await UniTask.Delay(2000);
            foreach (var p in match.Players) p.DiagnosticPlace(new Vector3(10, .05f, p.Snapshot.Value.Team == 1 ? -20 : 20), p.Snapshot.Value.Team == 1 ? 0 : 180);
            await Wait(() => EveryoneHas(4)); await UniTask.Delay(1000);
            foreach (var p in match.Players) p.DiagnosticPlace(PrototypeArena.Spawn((byte)(3 - p.Snapshot.Value.Team), 0), p.Snapshot.Value.Team == 1 ? 0 : 180);
            await Wait(() => EveryoneHas(5)); await UniTask.Delay(1000);
            foreach (var p in match.Players) p.Respawn();
            await Wait(() => EveryoneHas(1));
            await UniTask.Delay(3000);
        }
        async UniTask Change(int hero, HeroSelectionOrigin origin)
        {
            var p = PrototypePlayer.Local; p.RequestHeroChange(hero, origin);
            await Wait(() => !p.HeroChangePending);
        }
        async UniTask Capture(string suffix)
        {
            string path = Path.ChangeExtension(_output, null) + "-" + suffix + ".png";
            ScreenCapture.CaptureScreenshot(path); await Wait(() => File.Exists(path));
        }
        async UniTask LocalScenario()
        {
            var app = PrototypeApp.Current; var p = PrototypePlayer.Local; var arena = PrototypeArena.Current;
            await UniTask.Delay(300);
            Check(app.HeroSelectionUnavailableReason(HeroSelectionOrigin.SpawnArea) == null, "own spawn is eligible in Playing");
            app.OpenHeroSelection(HeroSelectionOrigin.SpawnArea);
            Check(app.Overlay == GameplayOverlay.Heroes, "spawn selection opens");
            await Capture("picker");
            var before = p.Snapshot.Value;
            await Change(2, HeroSelectionOrigin.SpawnArea);
            Check(p.Snapshot.Value.HeroId == 2 && p.Snapshot.Value.Health == 70 && p.Snapshot.Value.Ink == 20, "authority switches while retaining resources");
            Check(p.Snapshot.Value.Revision == before.Revision && p.Snapshot.Value.ProtectedUntil == before.ProtectedUntil, "life and protection retained");
            await Wait(() => PrototypePlayer.ByOwner.Values.All(other => other.Snapshot.Value.HeroId == 2 && other.BoundVisualPrefab == app.Heroes.Get(2).CharacterPrefab));
            Check(true, "both players observe synchronized hero visuals");

            await Wait(() => !arena.IsInHeroChangeZone(p.PresentedState.Team, p.PresentedState.Position));
            await Wait(() => app.Overlay != GameplayOverlay.Heroes);
            await Change(3, HeroSelectionOrigin.SpawnArea);
            Check(p.Snapshot.Value.HeroId == 2 && p.HeroChangeMessage.Contains("本方出生区"), "server rejects outside-area request and closes picker");
            await Change(4, HeroSelectionOrigin.Debug); // Stage acknowledgement also verifies the independent debug rule.
            Check(p.Snapshot.Value.HeroId == 4, "DEBUG remains available outside spawn");

            await Wait(() => arena.IsInHeroChangeZone((byte)(3 - p.PresentedState.Team), p.PresentedState.Position));
            app.OpenHeroSelection(HeroSelectionOrigin.SpawnArea);
            Check(app.Overlay != GameplayOverlay.Heroes, "enemy spawn cannot open picker");
            await Change(3, HeroSelectionOrigin.SpawnArea);
            Check(p.Snapshot.Value.HeroId == 4 && p.HeroChangeMessage.Contains("本方出生区"), "server rejects enemy-spawn request");
            await Change(5, HeroSelectionOrigin.Debug);

            await Wait(() => arena.IsInHeroChangeZone(p.PresentedState.Team, p.PresentedState.Position));
            Check(app.HeroSelectionUnavailableReason(HeroSelectionOrigin.SpawnArea) == null, "respawn restores eligibility immediately");
            app.CaptureMouse(true); await Capture("hud");
            await Change(1, HeroSelectionOrigin.SpawnArea);
            Check(p.Snapshot.Value.HeroId == 1, "spawn switch succeeds after respawn");
            await Wait(() => PrototypePlayer.ByOwner.Values.All(other => other.Snapshot.Value.HeroId == 1 && other.BoundVisualPrefab == app.Heroes.Get(1).CharacterPrefab));
            Check(true, "remote model synchronized after respawn switch");
            await UniTask.Delay(1500);
        }
    }
}
#endif
