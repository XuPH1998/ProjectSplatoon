#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in, separate-process transport acceptance. Inactive in normal sessions.</summary>
    public sealed class BubbleNetworkSmoke : MonoBehaviour
    {
        static BubbleNetworkSmoke _instance;
        public static bool Active => _instance != null;
        bool _host, _ready, _ending, _killed, _reset, _sawDeath, _respawned, _lateJoin;
        float _wallStart;
        uint _shots, _initialLife;
        int _peakPlayers, _peakProjectiles, _peakVisuals, _bounces;
        long _emitted;
        float _maxCorrection;
        readonly List<float> _frames = new();
        readonly List<string> _errors = new();
        static string Arg(string key, string fallback) => HeroSelectionSmoke.Arg(key, fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if (_instance != null || !Environment.GetCommandLineArgs().Contains("-bubbleRole")) return;
            var go = new GameObject("Bubble network acceptance"); DontDestroyOnLoad(go); _instance = go.AddComponent<BubbleNetworkSmoke>();
        }
        async UniTaskVoid Start()
        {
            _host = Arg("-bubbleRole", "host") == "host"; _wallStart = Time.realtimeSinceStartup;
            Application.logMessageReceived += OnLog;
            try
            {
                await UniTask.WaitUntil(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
                var gate = Arg("-bubbleJoinGate", "");
                if (!_host && gate.Length > 0) await UniTask.WaitUntil(() => File.Exists(gate));
                await PrototypeApp.Current.Connect(_host, "127.0.0.1", ushort.Parse(Arg("-bubblePort", "18237")));
                if (!PrototypeApp.Current.InRoom) throw new Exception(PrototypeApp.Current.Error);
                _ready = true;
            }
            catch (Exception e) { Finish(e.Message); }
        }
        void Update()
        {
            if (_ending) return;
            if (Time.realtimeSinceStartup - _wallStart > 180) { Finish("Timed out"); return; }
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            if (!_ready || match == null || player == null) return;
            double age = player.NetworkManager.ServerTime.Time; var state = player.Snapshot.Value;
            if (_initialLife == 0) _initialLife = state.Revision;
            if (state.HeroId != 7 && !player.HeroChangePending && state.Health > 0)
                player.RequestHeroChange(7, HeroSelectionOrigin.Warmup);
            if (state.ShotSequence > _shots) _emitted += state.ShotSequence - _shots;
            _shots = state.ShotSequence;
            _peakPlayers = Math.Max(_peakPlayers, PrototypePlayer.ByOwner.Count);
            _peakProjectiles = Math.Max(_peakProjectiles, match.Projectiles.ActiveCount);
            _maxCorrection = Mathf.Max(_maxCorrection, player.LastCorrectionDistance);
            if (age > 5) _frames.Add(Time.unscaledDeltaTime * 1000);
            if (InkPresentation.Current != null)
            {
                _peakVisuals = Math.Max(_peakVisuals, InkPresentation.Current.ActiveShots);
                _bounces = Math.Max(_bounces, InkPresentation.Current.BubbleBounceCount);
                _lateJoin |= InkPresentation.Current.BubbleRestoredCount > 0;
            }
            if (_host && match.Projectiles.ActiveCount >= 4)
            {
                string gate = Arg("-bubbleJoinGate", "");
                if (gate.Length > 0 && !File.Exists(gate)) File.WriteAllText(gate, "Authoritative bubbles are in flight.");
            }
            _sawDeath |= state.Health <= 0;
            _respawned |= _sawDeath && state.Health > 0 && state.HeroId == 7 && state.Revision > _initialLife;
            if (_host && age > 23 && !_killed)
            {
                foreach (var p in match.Players) p.ReceiveDamage((byte)(p.Snapshot.Value.Team == 1 ? 2 : 1), 200, Vector3.forward);
                _killed = true;
            }
            if (_host && age > 37 && !_reset)
            {
                var m = match.State.Value; m.Phase = MatchPhase.Finished; match.State.Value = m;
                match.ReturnToRoom(); _reset = true;
            }
            if (!_host && match.State.Value.Round > 0) _reset = true;
            if (age > (_host ? 68 : 60)) Finish(null);
        }
        public static void ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
            var smoke = _instance;
            if (smoke == null || !smoke._ready || smoke._ending) return;
            double age = player.NetworkManager.ServerTime.Time;
            input.Move = Vector2.zero; input.Swim = age % 8 >= 5.8;
            input.Look = new Vector2((player.PresentedState.Team == 1 ? 0 : 180) + 18, 5);
            input.Fire = !input.Swim && player.PresentedState.HeroId == 7 && !player.HeroChangePending;
        }
        void OnLog(string message, string stack, LogType type)
        { if ((type == LogType.Error || type == LogType.Exception) && _errors.Count < 12) _errors.Add(message); }
        async void Finish(string error)
        {
            if (_ending) return; _ending = true;
            bool passed = error == null && _errors.Count == 0 && _peakPlayers >= 2 && _emitted > 40 && _bounces > 20 &&
                _peakVisuals >= 4 && _respawned && _reset && (_host || _lateJoin);
            _frames.Sort();
            string report = $"passed={passed}\nrole={(_host ? "host" : "client")}\nerror={error}\nmaxPlayers={_peakPlayers}\nlocalShots={_emitted}\nbounceEvents={_bounces}\nlateJoinRestored={_lateJoin}\nrespawnRetained={_respawned}\nroundReset={_reset}\npeakAuthoritative={_peakProjectiles}\npeakVisible={_peakVisuals}\nmaxCorrection={_maxCorrection}\nframeP95Ms={(_frames.Count > 0 ? _frames[(int)((_frames.Count - 1) * .95)] : 0)}\n";
            foreach (NetworkTrafficKind kind in Enum.GetValues(typeof(NetworkTrafficKind)))
                if (kind != NetworkTrafficKind.Count) report += $"{kind}PayloadBytes={NetworkTrafficCounters.GetBytes(kind)}\n";
            report += "runtimeErrors=" + string.Join(" | ", _errors) + "\nPayload counters exclude transport overhead. Two processes share one PC; frame times are not target-device performance.\n";
            string path = Arg("-bubbleOutput", "Reports/BubbleGirl/network.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); File.WriteAllText(path, report); Debug.Log("[BUBBLE-NETWORK] " + report);
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) await PrototypeApp.Current.Leave();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(passed ? 0 : 1);
#else
            Application.Quit(passed ? 0 : 1);
#endif
        }
        void OnDestroy() { Application.logMessageReceived -= OnLog; if (_instance == this) _instance = null; }
    }
}
#endif
