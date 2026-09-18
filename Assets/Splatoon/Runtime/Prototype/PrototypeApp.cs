using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using Splatoon.Config;
using Splatoon.Loading;
using Splatoon.Networking;
using Splatoon.Combat;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp : MonoBehaviour
    {
        public const string ArenaAddress = "maps/TrainingGround";
        public const string PlayerAddress = "Prototype/Player";
        public const string MatchAddress = "Prototype/Match";
        public static PrototypeApp Current { get; private set; }
        public bool Ready { get; private set; }
        public bool Busy { get; private set; }
        public bool InRoom { get; private set; }
        public bool IsWeaponDebugRoom { get; private set; }
        public bool HasControl => InRoom && _captured && _overlay == GameplayOverlay.Game && !Busy && Application.isFocused;
        public string Error { get; private set; } = "";
        public string Status { get; private set; } = "正在初始化…";
        public NetworkManager Manager { get; private set; }
        public NgLanSessionService Session { get; private set; }
        private readonly AddressableSceneLoader _loader = new();
        private SceneLoadHandle _scene;
        private bool _loaded, _captured, _leaving;
        private CancellationTokenSource _operation;
        private AsyncOperationHandle<GameObject> _playerPrefab, _matchPrefab;
        public HeroContentService Heroes { get; } = new();
        private byte[] _signature;
        private readonly HashSet<ulong> _admitted = new();
        private float _progress;
        private string _ip = "127.0.0.1", _port = "7777", _roomCodeInput = "";
        private string[] _localAddresses;
        private int _hostAddressIndex;
        private ushort _activePort;
        private bool _advanced;
        private float _copiedUntil;
        private Font _chineseFont;
        public string RoomCode { get; private set; } = "";
        private Camera _bootCamera;
        private GameObject[] _bootRoots;
        private GUIStyle _title, _label, _small, _button, _field;
        public float Progress => _progress;
        private void Awake()
        {
            Current = this; Application.runInBackground = true; Application.targetFrameRate = 120;
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            _localAddresses = LanRoomCode.LocalAddresses();
            SavedUsername = _usernameInput = PlayerNames.Load();
            Initialize().Forget();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            gameObject.AddComponent<PrototypeSmoke>();
#endif
        }
        public async UniTask Initialize()
        {
            if (Busy) return;
            Busy = true; Error = ""; Status = "正在加载本地资源…";
            _operation = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            try
            {
                await Addressables.InitializeAsync().Task;
                await LubanConfigService.Current.InitializeAsync(_operation.Token);
                await WeaponConfigService.Current.InitializeAsync(LubanConfigService.Current.Tables.TbHero.DataList, _operation.Token);
                GameplayConfig.Validate();
                Time.fixedDeltaTime = 1f / GameplayConfig.Global.SimulationRate;
                _port = GameplayConfig.Global.DefaultPort.ToString();
                _signature = LubanConfigService.Current.ContentSignature;
                var go = new GameObject("NetworkManager"); DontDestroyOnLoad(go);
                Manager = go.AddComponent<NetworkManager>(); var transport = go.AddComponent<UnityTransport>();
                // A checkpoint sends several fragmented chunks together; keep room for a burst after a delayed editor frame.
                transport.MaxPacketQueueSize = Math.Max(256, GameplayConfig.Global.ChunksPerFrame * ((GameplayConfig.Global.SnapshotChunkBytes + 1023) / 1024) * 32);
                Manager.NetworkConfig = new NetworkConfig();
                Manager.NetworkConfig.NetworkTransport = transport; Manager.NetworkConfig.EnableSceneManagement = false;
                Manager.NetworkConfig.TickRate = (uint)GameplayConfig.Global.NetworkTickRate; Manager.NetworkConfig.ConnectionApproval = true;
                Manager.NetworkConfig.ConnectionData = PlayerConnectionPayload.Encode(_signature, SavedUsername);
                Manager.ConnectionApprovalCallback = Approve;
                Manager.OnClientConnectedCallback += ClientConnected;
                Manager.OnClientDisconnectCallback += ClientDisconnected;
                Session = new NgLanSessionService(Manager);
                Ready = true; Status = "准备就绪，可以创建或加入房间";
                _discovery.StartBrowsing();
                Debug.Log("[LAN] Ready. Config loaded from Luban.");
            }
            catch (Exception e)
            {
                Error = ChineseText.Error(e.Message); Status = "启动失败"; Debug.LogException(e);
                Session?.Dispose(); Session = null;
                if (Manager != null) { Destroy(Manager.gameObject); Manager = null; }
            }
            finally { Busy = false; _operation.Dispose(); _operation = null; }
        }
        private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            bool valid = PlayerConnectionPayload.TryDecode(request.Payload, _signature, out var name, out var error);
            response.Approved = valid && (!IsWeaponDebugRoom || request.ClientNetworkId == NetworkManager.ServerClientId) && _admitted.Count < (int)GameplayConfig.Mode.MaxPlayers;
            response.CreatePlayerObject = false; response.Pending = false;
            if (response.Approved) { _admitted.Add(request.ClientNetworkId); _admittedNames[request.ClientNetworkId] = name; }
            else response.Reason = !valid ? error : IsWeaponDebugRoom ? "单机武器调试房不接受其他玩家。" : $"房间已满（最多 {GameplayConfig.Mode.MaxPlayers} 人）。";
        }
        private void ClientConnected(ulong id)
        { if (Manager.IsServer && PrototypeMatch.Current != null) PrototypeMatch.Current.AddPlayer(id, _playerPrefab.Result); }
        private void ClientDisconnected(ulong id)
        { _admitted.Remove(id); _admittedNames.Remove(id); if (Manager.IsServer && PrototypeMatch.Current != null) PrototypeMatch.Current.RemovePlayer(id); }
        public async UniTask Connect(bool host, string address, ushort port, bool weaponDebug = false)
        {
            if (!Ready || Busy || InRoom) return;
            if (!SaveUsername(_usernameInput)) { Error = _usernameStatus; return; }
            if (!LanDiscoveryProtocol.ValidGamePort(port)) { Error = "游戏端口须为 1～65535，且不能使用房间发现端口 47777。"; return; }
#if !UNITY_EDITOR
            if (weaponDebug) { Error = "武器调试房仅编辑器可用"; return; }
#endif
            IsWeaponDebugRoom = weaponDebug && host;
            _discovery.Stop();
            Busy = true; Error = ""; Status = "正在加载场地…"; _progress = 0;
            _operation = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            try
            {
                _bootCamera = Camera.main;
                var bootScene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Main/Boot.unity");
                _bootRoots = bootScene.IsValid() ? bootScene.GetRootGameObjects().Where(x => x.activeSelf).ToArray() : Array.Empty<GameObject>();
                foreach (var root in _bootRoots) root.SetActive(false);
                _scene = await _loader.LoadAsync(GameplayConfig.Map.SceneAddress, new Progress<float>(p => _progress = p), _operation.Token); _loaded = true;
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(_scene.Scene.Scene);
                if (PrototypeArena.Current == null) throw new InvalidOperationException("场景缺少地图组件");
                PrototypeArena.Current.InitializeRuntime();
                if (_bootCamera != null) _bootCamera.gameObject.SetActive(false);
                _playerPrefab = Addressables.LoadAssetAsync<GameObject>(PlayerAddress);
                _matchPrefab = Addressables.LoadAssetAsync<GameObject>(MatchAddress);
                await WaitForPrefab(_playerPrefab, _operation.Token); await WaitForPrefab(_matchPrefab, _operation.Token);
                await WeaponConfigService.Current.InitializeAsync(LubanConfigService.Current.Tables.TbHero.DataList, _operation.Token, IsWeaponDebugRoom);
                await Heroes.InitializeAsync(LubanConfigService.Current.Tables.TbHero.DataList, _operation.Token);
                var bindings = _playerPrefab.Result.GetComponent<PrototypePlayer>();
                _signature = GameplayContentSignature.Compute(LubanConfigService.Current.ContentSignature, PrototypeArena.Current.BakedTopology, bindings, Heroes.All);
                Manager.NetworkConfig.ConnectionData = PlayerConnectionPayload.Encode(_signature, SavedUsername);
                Manager.AddNetworkPrefab(_playerPrefab.Result); Manager.AddNetworkPrefab(_matchPrefab.Result);
                Status = host ? "正在创建房间…" : "正在连接房主…";
                if (host)
                {
                    var result = await Session.StartHostAsync(new LanHostOptions(port, IsWeaponDebugRoom), _operation.Token);
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                    var match = Instantiate(_matchPrefab.Result).GetComponent<PrototypeMatch>();
                    match.GetComponent<NetworkObject>().Spawn(true);
                    foreach (ulong id in Manager.ConnectedClientsIds) match.AddPlayer(id, _playerPrefab.Result);
                }
                else
                {
                    var result = await Session.JoinAsync(new LanJoinOptions(address, port), _operation.Token);
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                    using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token);
                    using var readyTimer = readyTimeout.CancelAfterSlim(TimeSpan.FromSeconds(GameplayConfig.Global.ConnectionTimeout));
                    await UniTask.WaitUntil(() => PrototypeMatch.Current != null && PrototypeMatch.Current.InitialSyncComplete && PrototypePlayer.Local != null, cancellationToken: readyTimeout.Token);
                }
                _activePort = port;
                if (host) { int index = Array.IndexOf(_localAddresses, address); if (index >= 0) _hostAddressIndex = index; }
                RoomCode = IsWeaponDebugRoom ? "" : LanRoomCode.Encode(host ? _localAddresses[_hostAddressIndex] : address, port);
                InRoom = true; CaptureMouse(true); Status = IsWeaponDebugRoom ? "单机武器调试" : host ? "房主" : "已连接";
                if (host && !IsWeaponDebugRoom) { _discoveryRoomId = Guid.NewGuid(); _discovery.StartAdvertising(DiscoverySnapshot); }
                Debug.Log("[LAN] 房间码=" + RoomCode);
                Debug.Log($"[LAN] Connected role={(host ? "host" : "client")} address={address} port={port}");
            }
            catch (Exception e)
            {
                Error = e is OperationCanceledException ? "连接已取消，或房间状态同步超时。" : ChineseText.Error(e.Message);
                Debug.LogWarning("[LAN] " + Error); await Cleanup(); Status = "准备就绪";
            }
            finally { Busy = false; _operation.Dispose(); _operation = null; if (!InRoom && !_destroying) _discovery.StartBrowsing(); }
        }
        private static async UniTask WaitForPrefab(AsyncOperationHandle<GameObject> handle, CancellationToken token)
        {
            while (!handle.IsDone) await UniTask.Yield(token);
            token.ThrowIfCancellationRequested();
            if (handle.Status != AsyncOperationStatus.Succeeded) throw new InvalidOperationException("网络预制体加载失败，请重新构建本地资源。", handle.OperationException);
        }
        public void CancelConnection() => _operation?.Cancel();
        public async UniTask Leave(string reason = "")
        {
            if (_leaving || Busy) return;
            _leaving = true; Busy = true; Error = reason;
            try { await Cleanup(); Status = "准备就绪"; Debug.Log("[LAN] Returned to menu. " + reason); }
            catch (Exception e) { Error = ChineseText.Error(e.Message); Debug.LogException(e); }
            finally { Busy = false; _leaving = false; if (!_destroying) _discovery.StartBrowsing(); }
        }
        private async UniTask Cleanup()
        {
            _discovery.Stop();
            InRoom = false; _overlay = GameplayOverlay.Game; RoomCode = ""; _copiedUntil = 0; CaptureMouse(false);
            if (Session != null) await Session.ShutdownAsync();
            _admitted.Clear(); _admittedNames.Clear(); _nameCamera = null;
            ReleasePrefab(ref _playerPrefab); ReleasePrefab(ref _matchPrefab);
            Heroes.Clear(); _heroStats.Clear(); WeaponConfigService.Current.Clear(); IsWeaponDebugRoom = false;
#if UNITY_EDITOR
            ClearDebugWeaponChanges();
#endif
            var boot = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Main/Boot.unity");
            if (boot.IsValid() && boot.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(boot);
            if (_loaded) { await _loader.UnloadAsync(_scene); _loaded = false; }
            if (_bootRoots != null) foreach (var root in _bootRoots) if (root != null) root.SetActive(true);
            _bootRoots = null;
            if (_bootCamera != null) _bootCamera.gameObject.SetActive(true);
        }
        private void ReleasePrefab(ref AsyncOperationHandle<GameObject> handle)
        {
            if (!handle.IsValid()) return;
            if (handle.Status == AsyncOperationStatus.Succeeded) Manager.RemoveNetworkPrefab(handle.Result);
            Addressables.Release(handle); handle = default;
        }
        public void CaptureMouse(bool capture)
        { _captured = capture; if (capture) { _overlay = GameplayOverlay.Game; _fireInputBlocked = true; }
          else if (InRoom && _overlay == GameplayOverlay.Game) _overlay = GameplayOverlay.RoomMenu; Cursor.lockState = capture ? CursorLockMode.Locked : CursorLockMode.None; Cursor.visible = !capture; }
        private void Update()
        {
            _discovery.Tick();
#if UNITY_EDITOR
            ObserveDebugWeaponAssets();
#endif
            UpdateOverlayInput();
            UpdateMatchFeedback();
            if (!InRoom || Busy) return;
            if (Session.State == NetworkSessionState.Failed) { Leave(Session.LastError).Forget(); return; }
            var k = Keyboard.current;
            if (k != null && k.enterKey.wasPressedThisFrame && HasControl && Manager.IsServer && PrototypeMatch.Current != null) PrototypeMatch.Current.StartRound();
            if (PrototypeMatch.Current != null && PrototypeMatch.Current.State.Value.Phase == MatchPhase.Finished) CaptureMouse(false);
        }
        private void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            _overlay = InRoom ? GameplayOverlay.RoomMenu : GameplayOverlay.Game;
            CaptureMouse(false);
        }
        private void OnDestroy()
        {
            _destroying = true; _discovery.Dispose();
#if UNITY_EDITOR
            ClearDebugWeaponChanges();
#endif
            _operation?.Cancel(); Session?.Dispose();
            if (Manager != null) { Manager.Shutdown(); Destroy(Manager.gameObject); }
            Heroes.Dispose();
            LubanConfigService.Current.Reset(); if (Current == this) Current = null;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (_chineseFont != null) Destroy(_chineseFont);
            if (_startClip != null) Destroy(_startClip);
        }
        private static void Panel(Rect r, Color color) { var old = GUI.color; GUI.color = color; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old; }
        private void Styles()
        {
            if (_title != null) return;
            _chineseFont = ChineseText.CreateFont();
            _title = new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 23, padding = new RectOffset(12,12,10,10) };
            foreach (var style in new[] { _title, _label, _small, _button, _field }) style.font = _chineseFont;
            Debug.Log($"[中文字体] {_chineseFont.name}，房间涂墨字形={_chineseFont.HasCharacter('房') && _chineseFont.HasCharacter('间') && _chineseFont.HasCharacter('涂') && _chineseFont.HasCharacter('墨')}");
        }
        private void DrawAppGUI()
        {
            Styles(); GUI.matrix = Matrix4x4.Scale(new Vector3(Screen.width / 1280f, Screen.height / 720f, 1));
            if (!InRoom)
            {
                DrawLobby();
                return;
            }
            var match=PrototypeMatch.Current; var local=PrototypePlayer.Local;
            if(match==null||local==null) return;
            DrawPlayerNames();
            var state=match.State.Value; var player=local.PresentedState;
            var equipped = GameplayConfig.GetHero(player.HeroId);
            var weapon = GameplayConfig.GetWeapon(player.HeroId);
            GUI.Label(new Rect(24,542,340,30),equipped.DisplayName,_label);
            double total=Math.Max(.0001,state.TotalArea);
            Panel(new Rect(350,22,580,92),new Color(.04f,.065f,.09f,.92f));
            GUI.color=PrototypeArena.Pink; GUI.Label(new Rect(378,37,200,35),$"粉队  {state.PinkArea/total:P1}",_label);
            GUI.color=PrototypeArena.Blue; GUI.Label(new Rect(704,37,215,35),$"蓝队  {state.BlueArea/total:P1}",_label); GUI.color=Color.white;
            double remaining=state.Phase==MatchPhase.Playing?Math.Max(0,state.EndsAt-Manager.ServerTime.Time):0;
            string clock=state.Phase==MatchPhase.Practice?"热身":state.Phase==MatchPhase.Finished?"已结束":$"{(int)remaining/60:00}:{(int)remaining%60:00}";
            GUI.Label(new Rect(575,38,155,35),clock,_label);
            Panel(new Rect(378,86,524,8),new Color(.25f,.28f,.3f));
            Panel(new Rect(378,86,(float)(524*state.PinkArea/total),8),PrototypeArena.Pink);
            Panel(new Rect(902-(float)(524*state.BlueArea/total),86,(float)(524*state.BlueArea/total),8),PrototypeArena.Blue);
            string latency = Manager.IsHost ? "房主 · 本机裁决" :
                local.GameLatency.TryRead(Time.realtimeSinceStartupAsDouble, out double ping) ? $"游戏往返 {ping:0} 毫秒" : "暂无有效测量";
            GUI.Label(new Rect(24,68,310,55),$"{Status}  /  {state.PlayerCount}/{GameplayConfig.Mode.MaxPlayers}\n{latency}",_small);
            Panel(new Rect(24,578,330,116),new Color(.04f,.065f,.09f,.9f));
            GUI.Label(new Rect(42,590,290,32),$"{(player.Team==1?"粉队":"蓝队")}  /  生命 {player.Health:0}",_label);
            Panel(new Rect(42,635,285,15),new Color(.22f,.25f,.28f));
            Panel(new Rect(42,635,285*player.Ink/equipped.MaxInk,15),PrototypeArena.TeamColor(player.Team));
            if (weapon.FireMode != WeaponFireMode.Splatling || !(_captured && player.Health > 0 && (Splatoon.Combat.SplatlingSimulation.Charging(player) || player.SplatlingRemaining > 0)))
                GUI.Label(new Rect(42,662,310,26),player.InkRecoverAt > player.SimulatedAt ? "射击后回墨锁定" : player.Ink < Splatoon.Combat.WeaponSimulation.InkCost(weapon) ? (weapon.FireMode == WeaponFireMode.Splatling ? "墨量不足 / 可慢速蓄力" : Splatoon.Combat.WeaponSimulation.IsSemi(weapon) ? "墨量不足 / 回墨后继续射击" : "墨量不足 / 松开射击回墨") : player.HasInkRecovery ? "潜墨中 / 快速回墨" : player.Swimming ? (player.Movement == Splatoon.Combat.MovementMode.Air ? "空中弦化 / 普通回墨" : "弦化中 / 普通回墨") : $"墨水 {player.Ink:0} / {equipped.MaxInk:0}",_small);
            GUI.Label(new Rect(820,625,440,82),(weapon.FireMode == WeaponFireMode.Splatling ? "左键按住蓄力／松开持续射击" : Splatoon.Combat.WeaponSimulation.IsSemi(weapon) ? "左键点击单发／长按连续" : Splatoon.Combat.WeaponSimulation.IsCharge(weapon) ? "左键蓄力，松开发射" : "左键按住射击") + "　Shift 弦化\n按住 Tab 查看战绩　Esc 房间菜单\n" + (IsWeaponDebugRoom ? "H 选择英雄 · 调试房保持热身" : "回车开始（房主）"),_small);
            if (_captured && player.Health>0)
            {
                if (weapon.FireMode == WeaponFireMode.Charge)
                {
                    float charge = Splatoon.Combat.WeaponSimulation.ChargeRatio(player, weapon);
                    Panel(new Rect(570, 440, 140, 7), new Color(.2f,.23f,.27f));
                    Panel(new Rect(570, 440, 140 * charge, 7), PrototypeArena.TeamColor(player.Team));
                    bool limited = player.WeaponPhase == Splatoon.Combat.WeaponPhase.Charging && player.Ink < weapon.ShotInk &&
                        charge >= WeaponSimulation.AffordableChargeSeconds(weapon, player.Ink) / weapon.ChargeSeconds;
                    GUI.Label(new Rect(505,452,330,30), $"蓄力 {charge:P0} / " + (limited ? "墨量限制，松开发射" : "松开发射"), _small);
                }
                Vector2 reticleCenter = new(local.ReticleViewport.x * 1280, (1 - local.ReticleViewport.y) * 720);
                if (weapon.FireMode == WeaponFireMode.Splatling) DrawSplatlingHud(player, weapon, new Vector2(640, 520));
                if (!player.Swimming && local.ImpactReticleVisible)
                    DrawImpactReticle(new Vector2(local.ImpactReticleViewport.x * 1280, (1 - local.ImpactReticleViewport.y) * 720));
                DrawSpreadReticle(local, player, weapon, reticleCenter);
                if (Time.unscaledTimeAsDouble < local.HitConfirmedUntil) GUI.Label(new Rect(reticleCenter.x-12,reticleCenter.y-13,90,35),local.LastHitKilled ? "× 击倒" : "×",_label);
                if (local.MuzzleBlocked) GUI.Label(new Rect(reticleCenter.x-60,reticleCenter.y+43,210,32),"枪口被遮挡",_small);
                if (player.Movement == Splatoon.Combat.MovementMode.WallInk) GUI.Label(new Rect(450,460,550,32),"W/S 上下　A/D 横移　空格跳离　松开 Shift 脱墙",_small);
            }
#if UNITY_EDITOR
            if (IsWeaponDebugRoom) DrawWeaponDebugHud(player);
#endif
            if (state.Phase==MatchPhase.Practice) GUI.Label(new Rect(390,129,580,58), IsWeaponDebugRoom
                ? "单机武器调试 · 无限热身\nH 选择英雄 · Esc 添加 / 移除 BOT 或定位武器资产"
                : "H 选择英雄 · Esc 房间菜单可添加测试 BOT · 双方有真人后开始。",_small);
            if(player.Health<=0) GUI.Label(new Rect(475,275,460,64),$"已被击倒！{Math.Max(0,player.RespawnsAt-Manager.ServerTime.Time):0.0} 秒后重生",_label);
            if (state.Phase == MatchPhase.Playing && _overlay == GameplayOverlay.Game && HeroSelectionUnavailableReason(HeroSelectionOrigin.SpawnArea) == null)
                GUI.Label(new Rect(390,129,580,38), "出生区 · H 更换英雄", _small);
            if(_overlay == GameplayOverlay.RoomMenu)
            {
                bool practice = state.Phase == MatchPhase.Practice;
                float top = practice ? (Manager.IsHost ? 104 : 158) : 222;
                Panel(new Rect(430,top,420,practice ? (Manager.IsHost ? 416 : 360) : 295),new Color(.055f,.075f,.1f,.97f));
                string winner=PrototypeRules.Winner(state.PinkArea,state.BlueArea) switch {1=>"粉队获胜",2=>"蓝队获胜",_=>"平局"};
                GUI.Label(new Rect(463,top+20,360,44),state.Phase==MatchPhase.Finished?winner:"房间菜单",_label);
                if(state.Phase!=MatchPhase.Finished && GUI.Button(new Rect(465,top+70,350,44),"继续游戏",_button)) CaptureMouse(true);
                float extra = practice ? (Manager.IsHost ? 112 : 56) : 0;
                if (practice)
                {
                    byte target = (byte)(3 - local.Snapshot.Value.Team);
                    int count = PrototypePlayer.ByOwner.Values.Count(p => p != null && p.IsSpawned && p.Snapshot.Value.Team == target);
                    string reason = local.TeamChangeUnavailableReason;
                    GUI.enabled = !Busy && reason == null;
                    string label = local.TeamChangePending ? "更换中…" : $"更换队伍 · {(target == 1 ? "粉队" : "蓝队")} {count}/{TeamSelectionRules.Capacity}";
                    if (GUI.Button(new Rect(465,top+126,350,44),label,_button)) local.RequestTeamChange();
                    if (Manager.IsHost)
                    {
                        GUI.enabled = !Busy && match.CanAddTestBot;
                        if (GUI.Button(new Rect(465,top+182,172,44),"添加敌方 BOT",_button)) match.AddTestBot(_playerPrefab.Result);
                        GUI.enabled = !Busy && match.TestBotCount > 0;
                        if (GUI.Button(new Rect(643,top+182,172,44),"清除 BOT",_button)) match.ClearTestBots();
                    }
                    GUI.Label(new Rect(463,top+248+extra,360,50),reason ?? (!string.IsNullOrEmpty(local.TeamChangeMessage) ? local.TeamChangeMessage : match.TestBotMessage),_small);
                }
                bool finished = state.Phase == MatchPhase.Finished;
                GUI.enabled = !Busy && Manager.IsServer && (finished || match.CanStartRound);
                string roundAction = finished ? (Manager.IsServer ? "返回房间" : "等待房主返回房间") : "开始比赛";
                if (GUI.Button(new Rect(465,top+134+extra,350,44),roundAction,_button))
                { if (finished) match.ReturnToRoom(); else match.StartRound(); }
                GUI.enabled=!Busy;
                if(GUI.Button(new Rect(465,top+198+extra,350,44),"退出房间",_button)) Leave().Forget();
                GUI.enabled=true;
                DrawRoomCode();
            }
        }
        public async UniTask ConnectRoomCode(string code)
        {
            if (!Ready || Busy || InRoom) return;
            if (!LanRoomCode.TryDecode(code, out var endpoint, out var error)) { Error = error; return; }
            await Connect(false, endpoint.Address, endpoint.Port);
        }
        private void CycleHostAddress()
        {
            _hostAddressIndex = (_hostAddressIndex + 1) % _localAddresses.Length;
            _copiedUntil = 0;
            if (InRoom && Manager.IsHost) RoomCode = LanRoomCode.Encode(_localAddresses[_hostAddressIndex], _activePort);
        }
        private void DrawRoomCode()
        {
            if (IsWeaponDebugRoom) return;
            if (!string.IsNullOrEmpty(_discovery.LastError)) GUI.Label(new Rect(330,637,650,55),_discovery.LastError,_small);
            Panel(new Rect(330,532,620,100),new Color(.055f,.075f,.1f,.97f));
            GUI.Label(new Rect(350,540,395,30),"房间码：" + RoomCode,_label);
            if(GUI.Button(new Rect(778,540,150,35),Time.unscaledTime<_copiedUntil?"已复制":"复制房间码",_button))
            { GUIUtility.systemCopyBuffer=RoomCode; _copiedUntil=Time.unscaledTime+2; }
            if(Manager.IsHost)
            {
                if(GUI.Button(new Rect(350,586,575,30),"切换分享地址："+_localAddresses[_hostAddressIndex]+"（端口 "+_activePort+"）",_small)) CycleHostAddress();
            }
            else GUI.Label(new Rect(350,586,575,30),"伙伴可使用相同房间码加入当前房间。",_small);
        }
        private void ConnectFromUI(bool host)
        {
            if(!ushort.TryParse(_port,out ushort port)||port==0) {Error="请输入 1～65535 的端口。";return;}
            Connect(host,host ? _localAddresses[_hostAddressIndex] : _ip.Trim(),port).Forget();
        }
    }
}
