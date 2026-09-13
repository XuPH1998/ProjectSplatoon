using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;

namespace Splatoon.Prototype
{
    /// <summary>Explicit editor/development diagnostics; inactive without -cgSmokeRole.</summary>
    public sealed class RifleGirlSmoke : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Serializable] sealed class Result
        {
            public bool passed;
            public string role, error;
            public int maxPlayers, snapshots, screenWidth, screenHeight;
            public bool firing, turn, death, respawn, swimming, jump, reconnect, roundFinished;
            public bool remoteMove, remoteFire, remoteTurn, remoteDeath, remoteRespawn;
            public long releasedPaintBytes;
            public double duration, p95FrameMilliseconds;
            public List<string> events = new();
        }
        static RifleGirlSmoke _instance;
        public static bool Active => _instance != null;
        bool _host, _ready, _ending, _killedBack, _killedForward, _reconnecting, _didReconnect, _roundStarted, _painted, _visualOnly;
        bool _sawDead, _captured, _roundKillBack, _roundKillForward, _remoteWasDead;
        float _wallStart;
        double _startedAt;
        ushort _port;
        readonly Result _result = new();
        readonly List<float> _frameTimes = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            if (_instance != null || !Environment.GetCommandLineArgs().Contains("-cgSmokeRole")) return;
            var go = new GameObject("RifleGirlSmoke"); DontDestroyOnLoad(go); _instance = go.AddComponent<RifleGirlSmoke>();
        }
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        async UniTaskVoid Start()
        {
            _wallStart = Time.realtimeSinceStartup; _host = Arg("-cgSmokeRole", "host") == "host";
            _visualOnly = Environment.GetCommandLineArgs().Contains("-cgSmokeVisualOnly");
            _port = ushort.Parse(Arg("-cgSmokePort", "17992")); _result.role = _host ? "host" : "client";
            try
            {
                await UniTask.WaitUntil(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
                await PrototypeApp.Current.Connect(_host, "127.0.0.1", _port);
                if (!PrototypeApp.Current.InRoom) throw new InvalidOperationException(PrototypeApp.Current.Error);
                await UniTask.WaitUntil(() => PrototypePlayer.Local != null);
                _startedAt = PrototypePlayer.Local.NetworkManager.ServerTime.Time; _ready = true;
                _result.events.Add("Connected with RifleGirl content and character protocol " + PlayerSnapshot.ProtocolVersion);
                Debug.Log("[RIFLE-SMOKE] Connected role=" + _result.role);
            }
            catch (Exception ex) { Fail(ex); }
        }

        public static void ModifyInput(PrototypePlayer player, ref PlayerInputFrame frame)
        {
            var smoke = _instance; if (smoke == null || !smoke._ready || smoke._ending) return;
            double age = player.NetworkManager.ServerTime.Time - smoke._startedAt;
            float facing = player.Snapshot.Value.Team == 1 ? 0 : 180;
            frame.Move = Vector2.zero; frame.Fire = frame.Swim = false; frame.Look = new Vector2(facing, 12);
            if (age >= 1 && age < 2) frame.Move = Vector2.up;
            else if (age < 3 && age >= 2) frame.Move = Vector2.down;
            else if (age < 4 && age >= 3) frame.Move = Vector2.left;
            else if (age < 5 && age >= 4) frame.Move = Vector2.right;
            else if (age < 8 && age >= 5) { frame.Fire = true; frame.Move.x = .25f; }
            else if (age < 10 && age >= 8) frame.Look.x = facing + 80;
            else if (age < 11 && age >= 10) { frame.JumpSequence = 1; frame.Look.y = -20; }
            else if (age < 13 && age >= 11) { frame.JumpSequence = 1; frame.Swim = true; }
            else if (age >= 24 && age < 31) frame.Fire = true;
            else if (age >= 40)
            {
                int stage = (int)((age - 40) % 20) / 5;
                frame.Move = stage switch { 0 => Vector2.up, 1 => Vector2.down, 2 => Vector2.left, _ => Vector2.right };
                frame.Fire = stage != 3; frame.Swim = stage == 3;
                if ((age - 40) % 20 >= 16 && (age - 40) % 20 < 18)
                { frame.Move = Vector2.zero; frame.Swim = false; frame.Look.x = facing + 80; }
            }
            if (age >= 10) frame.JumpSequence = 1;
        }

        void Update()
        {
            if (_ending) return;
            if (Time.realtimeSinceStartup - _wallStart > 330) { Fail(new TimeoutException("RifleGirl Play Mode validation timed out")); return; }
            if (!_ready || _reconnecting || PrototypePlayer.Local == null || PrototypeMatch.Current == null) return;
            try
            {
                var local = PrototypePlayer.Local; double now = local.NetworkManager.ServerTime.Time, age = now - _startedAt;
                var match = PrototypeMatch.Current; var state = local.Snapshot.Value;
                _result.duration = age; _result.snapshots++; _result.maxPlayers = Math.Max(_result.maxPlayers, match.Players.Count > 0 ? match.Players.Count : PrototypePlayer.ByOwner.Count);
                _result.screenWidth = Screen.width; _result.screenHeight = Screen.height;
                _frameTimes.Add(Time.unscaledDeltaTime * 1000);
                _result.firing |= state.Firing; _result.turn |= state.TurnDirection != 0;
                _result.swimming |= state.Swimming; _result.jump |= !state.Grounded && state.Health > 0;
                if (state.Health <= 0)
                {
                    _sawDead = _result.death = true;
                    if (now - state.DiedAt > .2 && !local.CharacterView.Animator.enabled) throw new InvalidOperationException("Dead character was hidden");
                    if (state.Firing || state.Swimming) throw new InvalidOperationException("Dead character still firing/swimming");
                }
                else if (_sawDead) { _result.respawn = true; }
                if (_host)
                {
                    if (!_painted && age > 10.8)
                    {
                        local.ValidateMapSwimming(); _result.swimming = true; _painted = true;
                        _result.events.Add("Actual controller ground/bridge/ramp swimming, recovery and enemy-ink slowdown passed");
                    }
                    if (!_killedBack && age > 14) { KillPlayers(false); _killedBack = true; }
                    if (!_killedForward && age > 20) { KillPlayers(true); _killedForward = true; }
                    if (!_roundStarted && age > 40 && match.State.Value.PlayerCount >= 2)
                    { match.StartRound(); _roundStarted = true; _result.events.Add("Started full configured " + GameplayConfig.Mode.MatchSeconds + " second round"); }
                    if (match.State.Value.Phase == MatchPhase.Playing)
                    {
                        double roundAge = now - (match.State.Value.EndsAt - GameplayConfig.Mode.MatchSeconds);
                        if (!_roundKillBack && roundAge > 15) { KillPlayers(false); _roundKillBack = true; }
                        if (!_roundKillForward && roundAge > 22) { KillPlayers(true); _roundKillForward = true; }
                    }
                }
                else if (!_didReconnect && age > 12) Reconnect().Forget();
                if (!_captured && state.Firing && age > 25)
                { Capture(_visualOnly ? "playmode-trainingground-final" : "playmode-" + _result.role); _captured = true; }
                if (_visualOnly && age > 28) { Complete().Forget(); return; }
                if (match.State.Value.Phase == MatchPhase.Finished)
                {
                    _result.roundFinished = true;
                    if (state.Firing) throw new InvalidOperationException("Round finished but shooting persisted");
                    if (now - match.State.Value.EndsAt > (_host ? 6 : 2)) Complete().Forget();
                }
                foreach (var player in PrototypePlayer.ByOwner.Values)
                {
                    if (player.CharacterView.Profile == null || player.CharacterView.BoundWeaponPrefab == null || player.CharacterView.BoundWeaponPrefab.name != "RifleGirlRifle")
                        throw new InvalidOperationException("Peer visual binding mismatch");
                    if (!player.IsOwner)
                    {
                        var peer = player.Snapshot.Value;
                        _result.remoteMove |= peer.Velocity.sqrMagnitude > 1;
                        _result.remoteFire |= peer.Firing && player.CharacterView.Animator.GetLayerWeight(1) > .5f;
                        _result.remoteTurn |= peer.TurnDirection != 0;
                        if (peer.Health <= 0) _remoteWasDead = _result.remoteDeath = true;
                        else if (_remoteWasDead) _result.remoteRespawn = true;
                    }
                }
            }
            catch (Exception ex) { Fail(ex); }
        }
        void KillPlayers(bool forward)
        {
            foreach (var player in PrototypeMatch.Current.Players)
            {
                var s = player.Snapshot.Value; var direction = Quaternion.Euler(0, s.BodyYaw, 0) * Vector3.forward * (forward ? 1 : -1);
                player.ReceiveDamage((byte)(s.Team == 1 ? 2 : 1), 9999, direction);
                if (player.Snapshot.Value.DeathDirection != (forward ? 1 : 0)) throw new InvalidOperationException("Authority selected wrong death direction");
            }
            _result.events.Add("Authority " + (forward ? "forward" : "backward") + " death direction passed");
        }
        async UniTask Reconnect()
        {
            _didReconnect = _reconnecting = true;
            try
            {
            await PrototypeApp.Current.Leave();
            if (PaintSurface.AllocatedBytes != 0) throw new InvalidOperationException("Paint textures leaked on disconnect");
            await UniTask.Delay(1500, ignoreTimeScale: true);
            await PrototypeApp.Current.Connect(false, "127.0.0.1", _port);
            if (!PrototypeApp.Current.InRoom) { Fail(new InvalidOperationException("Reconnect failed: " + PrototypeApp.Current.Error)); return; }
            await UniTask.WaitUntil(() => PrototypePlayer.Local != null);
            _result.reconnect = true; _reconnecting = false; _result.events.Add("Disconnect/rejoin and content rebind passed");
            }
            catch (Exception ex) { Fail(ex); }
        }
        async UniTask Complete()
        {
            _ending = true;
            if (!_result.firing || (_host && (!_result.turn || !_result.death || !_result.respawn)) || (!_visualOnly && (_result.maxPlayers < 2 || !_result.roundFinished || !_result.remoteMove || !_result.remoteFire || !_result.remoteDeath || !_result.remoteRespawn || (!_host && !_result.reconnect))))
            { Fail(new InvalidOperationException("Required Play Mode scenarios were not observed")); return; }
            await PrototypeApp.Current.Leave();
            _result.releasedPaintBytes = PaintSurface.AllocatedBytes;
            if (_result.releasedPaintBytes != 0) { Fail(new InvalidOperationException("Paint textures not released")); return; }
            _frameTimes.Sort(); _result.p95FrameMilliseconds = _frameTimes[(int)((_frameTimes.Count - 1) * .95f)];
            _result.passed = true; Save(); Debug.Log("[RIFLE-SMOKE] PASS role=" + _result.role); Exit(0);
        }
        void Fail(Exception ex) { _ending = true; _result.error = ex.ToString(); _result.passed = false; Save(); Debug.LogException(ex); Exit(1); }
        void Save() { Directory.CreateDirectory("Docs/CombatGirls"); File.WriteAllText("Docs/CombatGirls/" + (_visualOnly ? "playmode-visual-final" : "playmode-" + _result.role) + ".json", JsonUtility.ToJson(_result, true)); }
        static void Exit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
        static void Capture(string name)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Camera.main == null) return;
            var rt = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            var old = RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(Camera.main, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                RenderTexture.active = rt; var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
                Directory.CreateDirectory("Docs/CombatGirls/Screenshots"); File.WriteAllBytes("Docs/CombatGirls/Screenshots/" + name + ".png", texture.EncodeToPNG()); Destroy(texture);
            }
            finally { RenderTexture.active = old; RenderTexture.ReleaseTemporary(rt); }
        }
#endif
    }
}
