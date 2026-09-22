#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in separate-process acceptance. Files coordinate the test, never gameplay authority.</summary>
    public sealed class RescueBubbleNetworkSmoke : MonoBehaviour
    {
        static RescueBubbleNetworkSmoke _instance;
        public static bool Active => _instance != null;
        string _role, _root;
        string _lastPaintHash;
        bool _ready, _ending, _rescuedRemote, _rescuedHost, _executed, _expired, _late;
        int _stage, _sentStage, _seenStage;
        uint _interact;
        ulong _target;
        uint _targetLife;
        float _started, _stageAt;
        Vector3 _bubbleStart;
        float _remoteDistance;
        float _nextTrace;
        PrototypePlayer _remote;
        readonly System.Collections.Generic.List<string> _errors = new();
        static string Arg(string key, string fallback) => HeroSelectionSmoke.Arg(key, fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if (_instance != null || !Environment.GetCommandLineArgs().Contains("-rescueRole")) return;
            var go = new GameObject("Rescue bubble network acceptance"); DontDestroyOnLoad(go); _instance = go.AddComponent<RescueBubbleNetworkSmoke>();
        }
        async UniTaskVoid Start()
        {
            _role = Arg("-rescueRole", "host"); _root = Arg("-rescueOutput", "Temp/DownedBubble/Network"); Directory.CreateDirectory(_root);
            _started = Time.realtimeSinceStartup; Application.runInBackground = true; Application.logMessageReceived += Log;
            try
            {
                await UniTask.WaitUntil(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
                if (_role != "host") await UniTask.WaitUntil(() => File.Exists(_root + (_role == "late" ? "/join-late" : "/host-ready")));
                PrototypeApp.Current.SaveUsername("Bubble-" + _role);
                await PrototypeApp.Current.Connect(_role == "host", "127.0.0.1", ushort.Parse(Arg("-rescuePort", "18437")));
                if (!PrototypeApp.Current.InRoom) throw new Exception(PrototypeApp.Current.Error);
                _ready = true;
                if (_role == "host") File.WriteAllText(_root + "/host-ready", "ready");
            }
            catch (Exception e) { Finish(e.ToString()); }
        }
        static void Place(PrototypePlayer player, Vector3 position, byte team)
        {
            var s = player.Snapshot.Value; s.Team = team; player.Snapshot.Value = s; player.DiagnosticPlace(position, 0);
        }
        static void Near(PrototypePlayer actor, PrototypePlayer target, byte team)
        {
            var s = target.Snapshot.Value;
            Place(actor, s.BubbleCenter + Vector3.right * (s.BubbleRadius + .25f) - HeroBodyShape.For(actor.Snapshot.Value.HeroId).Center, team);
        }
        void Stage(int stage)
        {
            _stage = stage; _stageAt = Time.realtimeSinceStartup;
            File.WriteAllText(_root + "/lives-" + stage, PrototypePlayer.Local.Snapshot.Value.Revision + "," + _remote.Snapshot.Value.Revision);
            File.WriteAllText(_root + "/stage", stage.ToString());
            Debug.Log("[RESCUE-NET] stage=" + stage);
        }
        int ReadStage()
        { try { return int.TryParse(File.ReadAllText(_root + "/stage"), out int value) ? value : 0; } catch (IOException) { return 0; } }
        void Update()
        {
            if (_ending) return;
            if (Time.realtimeSinceStartup - _started > 150) { Finish("Timed out at stage " + _stage); return; }
            if (!_ready || PrototypeMatch.Current == null || PrototypePlayer.Local == null) return;
            var match = PrototypeMatch.Current; var local = PrototypePlayer.Local;
            if (_role != "host")
            {
                _stage = ReadStage();
                var target = _stage == 1 || _stage == 4 ? (_role == "late" ? match.Players.FirstOrDefault(p => p.Username.Value.ToString() == "Bubble-client") : local)
                    : PrototypePlayer.ByOwner.Values.FirstOrDefault(p => p.OwnerClientId == 0 && !p.IsTestBot);
                // The server-only roster is empty on clients; player snapshots are indexed by owner on every peer.
                if (_role == "late" && _stage == 4) target = PrototypePlayer.ByOwner.Values.FirstOrDefault(p => p.Username.Value.ToString() == "Bubble-client");
                if (target != null && target.PresentedState.IsBubble && _seenStage != _stage)
                {
                    _seenStage = _stage; _late |= _role == "late";
                    File.WriteAllText(_root + "/" + _role + "-saw-" + _stage, target.PresentedState.BubbleUntil.ToString("R"));
                }
                if (_stage == 5)
                {
                    string hash = PrototypeArena.Current.OwnershipHash().ToString();
                    if (hash != _lastPaintHash) { File.WriteAllText(_root + "/" + _role + "-paint", hash); _lastPaintHash = hash; }
                    if (_role == "late" && !_late) { Finish("Late join did not restore bubble"); return; }
                }
                if (File.Exists(_root + "/finished")) Finish(null);
                return;
            }
            _remote ??= match.Players.FirstOrDefault(p => !p.IsTestBot && p != local && p.Username.Value.ToString() == "Bubble-client");
            if (_remote == null) return;
            if (Time.realtimeSinceStartup > _nextTrace)
            {
                _nextTrace = Time.realtimeSinceStartup + 1;
                var trace = _remote.Snapshot.Value;
                File.AppendAllText(_root + "/authority.csv", $"{match.NetworkManager.ServerTime.Time:R},{_stage},{match.State.Value.Phase},{trace.LifeState},{trace.BubbleResult},{trace.Health},{trace.Position},{trace.BubbleUntil:R},{trace.RespawnsAt:R}\n");
            }
            try
            {
                if (_stage == 0)
                {
                    match.StartRound(); Place(local, PrototypeArena.Spawn(1, 0), 1); Place(_remote, PrototypeArena.Spawn(2, 0), 2);
                    _remote.ReceiveDamage(1, 10000, Vector3.forward, local.PlayerId); _bubbleStart = _remote.Snapshot.Value.Position;
                    Near(local, _remote, 2); Stage(1);
                }
                else if (_stage == 1)
                {
                    _remoteDistance = Mathf.Max(_remoteDistance, Vector3.Distance(Vector3.ProjectOnPlane(_remote.Snapshot.Value.Position - _bubbleStart, Vector3.up), Vector3.zero));
                    if (_remote.Snapshot.Value.IsAlive)
                    {
                        Require(_remoteDistance > .3f, "Remote bubble movement did not reach authority"); _rescuedRemote = true;
                        Place(local, PrototypeArena.Spawn(1, 0), 1); Place(_remote, PrototypeArena.Spawn(2, 0), 2);
                        local.ReceiveDamage(2, 10000, Vector3.forward, _remote.PlayerId); Near(_remote, local, 1); Stage(2);
                    }
                }
                else if (_stage == 2 && local.Snapshot.Value.IsAlive)
                {
                    _rescuedHost = true;
                    Place(local, PrototypeArena.Spawn(1, 0), 1); Place(_remote, PrototypeArena.Spawn(2, 0), 2);
                    local.ReceiveDamage(2, 10000, Vector3.forward, _remote.PlayerId); Near(_remote, local, 2); Stage(3);
                }
                else if (_stage == 3 && local.Snapshot.Value.IsDead)
                {
                    Require(local.Snapshot.Value.BubbleResult == BubbleOutcome.Executed, "Expected enemy F execution"); _executed = true;
                    Place(local, PrototypeArena.Spawn(1, 0), 1); Place(_remote, PrototypeArena.Spawn(2, 0), 2);
                    _remote.ReceiveDamage(1, 10000, Vector3.forward, local.PlayerId); Stage(4); File.WriteAllText(_root + "/join-late", "ready");
                }
                else if (_stage == 4)
                {
                    _expired |= _remote.Snapshot.Value.IsDead && _remote.Snapshot.Value.BubbleResult == BubbleOutcome.Expired;
                    if (_expired && _remote.Snapshot.Value.IsAlive)
                    { Require(File.Exists(_root + "/late-saw-4"), "Late join did not observe bubble"); Stage(5); }
                }
                else if (_stage == 5 && Time.realtimeSinceStartup - _stageAt > 3 && File.Exists(_root + "/client-paint") && File.Exists(_root + "/late-paint"))
                {
                    string hash = PrototypeArena.Current.OwnershipHash().ToString();
                    bool equal;
                    try { equal = File.ReadAllText(_root + "/client-paint") == hash && File.ReadAllText(_root + "/late-paint") == hash; }
                    catch (IOException) { return; }
                    if (!equal && Time.realtimeSinceStartup - _stageAt < 15) return;
                    Require(equal, "Paint parity mismatch");
                    File.WriteAllText(_root + "/finished", "PASS"); Finish(null);
                }
            }
            catch (Exception e) { Finish(e.ToString()); }
        }
        public static void ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
            var smoke = _instance; if (smoke == null || !smoke._ready || smoke._ending) return;
            input.Move = Vector2.zero; input.Fire = input.Swim = input.SubHeld = false; input.CancelFire = input.CancelSub = true;
            input.Look = Vector2.zero; input.InteractSequence = smoke._interact;
            input.InteractTarget = smoke._target; input.InteractTargetLife = smoke._targetLife;
            if (smoke._role == "client" && smoke._stage == 1 && player.PresentedState.IsBubble) input.Move = Vector2.right;
            bool hostRescue = smoke._role == "host" && smoke._stage == 1 && Time.realtimeSinceStartup - smoke._stageAt > 2;
            bool clientPoke = smoke._role == "client" && (smoke._stage == 2 || smoke._stage == 3);
            if ((!hostRescue && !clientPoke) || smoke._sentStage == smoke._stage || !player.PresentedState.IsAlive) return;
            uint hostLife = 0;
            if (clientPoke)
            {
                // The test coordinator can advance before a delayed snapshot arrives. Wait for both new lives.
                try
                {
                    var lives = File.ReadAllText(smoke._root + "/lives-" + smoke._stage).Split(',');
                    hostLife = uint.Parse(lives[0]);
                    if (player.PresentedState.Revision != uint.Parse(lives[1])) return;
                }
                catch (IOException) { return; }
            }
            var target = PrototypePlayer.ByOwner.Values.FirstOrDefault(p => p != player && p.PresentedState.IsBubble &&
                (!clientPoke || p.PresentedState.Revision == hostLife) &&
                PrototypePlayer.CanInteract(player.PresentedState, p.PresentedState, p.PresentedState.Revision, player.NetworkManager.ServerTime.Time));
            if (target == null) return;
            smoke._sentStage = smoke._stage; input.InteractSequence = ++smoke._interact;
            input.InteractTarget = smoke._target = target.PlayerId; input.InteractTargetLife = smoke._targetLife = target.PresentedState.Revision;
        }
        static void Require(bool result, string error) { if (!result) throw new Exception(error); }
        void Log(string message, string stack, LogType kind)
        { if ((kind == LogType.Error || kind == LogType.Exception) && _errors.Count < 12) _errors.Add(message); }
        async void Finish(string error)
        {
            if (_ending) return; _ending = true;
            bool pass = error == null && _errors.Count == 0 && (_role != "host" || (_rescuedHost && _rescuedRemote && _executed && _expired));
            string result = $"passed={pass}\nrole={_role}\nstage={_stage}\nremoteMovement={_remoteDistance}\nrescuedRemote={_rescuedRemote}\nrescuedHost={_rescuedHost}\nexecuted={_executed}\nexpired={_expired}\nlateJoin={_late}\nerror={error}\nruntimeErrors={string.Join(" | ", _errors)}\n";
            File.WriteAllText(_root + "/" + _role + "-result.txt", result); Debug.Log("[RESCUE-NET] " + result);
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) await PrototypeApp.Current.Leave();
            Application.Quit(pass ? 0 : 1);
        }
        void OnDestroy() { Application.logMessageReceived -= Log; if (_instance == this) _instance = null; }
    }
}
#endif
