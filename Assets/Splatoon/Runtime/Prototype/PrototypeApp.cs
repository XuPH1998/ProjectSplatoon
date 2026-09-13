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
        private AsyncOperationHandle<GameObject> _characterContent, _weaponContent;
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
                Manager.NetworkConfig.ConnectionData = _signature;
                Manager.ConnectionApprovalCallback = Approve;
                Manager.OnClientConnectedCallback += ClientConnected;
                Manager.OnClientDisconnectCallback += ClientDisconnected;
                Session = new NgLanSessionService(Manager);
                Ready = true; Status = "准备就绪，可以创建或加入房间";
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
            response.Approved = request.Payload.SequenceEqual(_signature) && _admitted.Count < (int)GameplayConfig.Mode.MaxPlayers;
            response.CreatePlayerObject = false; response.Pending = false;
            if(response.Approved) _admitted.Add(request.ClientNetworkId);
            if (!response.Approved) response.Reason = !request.Payload.SequenceEqual(_signature) ? "协议或游戏内容不一致（需要玩家协议 6、墨水协议 5），请使用相同地图、配置和角色资源。" : "房间已满（最多 4 人）。";
        }
        private void ClientConnected(ulong id)
        { if (Manager.IsServer && PrototypeMatch.Current != null) PrototypeMatch.Current.AddPlayer(id, _playerPrefab.Result); }
        private void ClientDisconnected(ulong id)
        { _admitted.Remove(id); if (Manager.IsServer && PrototypeMatch.Current != null) PrototypeMatch.Current.RemovePlayer(id); }
        public async UniTask Connect(bool host, string address, ushort port)
        {
            if (!Ready || Busy || InRoom) return;
            Busy = true; Error = ""; Status = "正在加载场地…"; _progress = 0;
            _operation = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            try
            {
                _bootCamera = Camera.main;
                var bootScene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Main/Boot.unity");
                _bootRoots = bootScene.IsValid() ? bootScene.GetRootGameObjects().Where(x => x.activeSelf).ToArray() : Array.Empty<GameObject>();
                foreach (var root in _bootRoots) root.SetActive(false);
                _scene = await _loader.LoadAsync(GameplayConfig.Arena.SceneAddress, new Progress<float>(p => _progress = p), _operation.Token); _loaded = true;
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(_scene.Scene.Scene);
                if (PrototypeArena.Current == null) throw new InvalidOperationException("场景缺少地图组件");
                PrototypeArena.Current.InitializeRuntime();
                if (_bootCamera != null) _bootCamera.gameObject.SetActive(false);
                _playerPrefab = Addressables.LoadAssetAsync<GameObject>(PlayerAddress);
                _matchPrefab = Addressables.LoadAssetAsync<GameObject>(MatchAddress);
                await WaitForPrefab(_playerPrefab, _operation.Token); await WaitForPrefab(_matchPrefab, _operation.Token);
                _characterContent = Addressables.LoadAssetAsync<GameObject>(GameplayConfig.Character.VisualAddress);
                _weaponContent = Addressables.LoadAssetAsync<GameObject>(GameplayConfig.Weapon.PrefabAddress);
                await WaitForPrefab(_characterContent, _operation.Token); await WaitForPrefab(_weaponContent, _operation.Token);
                var bindings = _playerPrefab.Result.GetComponent<PrototypePlayer>();
                _signature = GameplayContentSignature.Compute(LubanConfigService.Current.ContentSignature, PrototypeArena.Current.BakedTopology, bindings);
                Manager.NetworkConfig.ConnectionData = _signature;
                if (bindings.BoundVisualPrefab != _characterContent.Result || bindings.CharacterView.BoundWeaponPrefab != _weaponContent.Result)
                    throw new InvalidOperationException("角色或武器配置地址与正式网络预制体绑定不一致，请同步资源绑定后重新构建。");
                Manager.AddNetworkPrefab(_playerPrefab.Result); Manager.AddNetworkPrefab(_matchPrefab.Result);
                Status = host ? "正在创建房间…" : "正在连接房主…";
                if (host)
                {
                    var result = await Session.StartHostAsync(new LanHostOptions(port), _operation.Token);
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
                RoomCode = LanRoomCode.Encode(host ? _localAddresses[_hostAddressIndex] : address, port);
                InRoom = true; CaptureMouse(true); Status = host ? "房主" : "已连接";
                Debug.Log("[LAN] 房间码=" + RoomCode);
                Debug.Log($"[LAN] Connected role={(host ? "host" : "client")} address={address} port={port}");
            }
            catch (Exception e)
            {
                Error = e is OperationCanceledException ? "连接已取消，或房间状态同步超时。" : ChineseText.Error(e.Message);
                Debug.LogWarning("[LAN] " + Error); await Cleanup(); Status = "准备就绪";
            }
            finally { Busy = false; _operation.Dispose(); _operation = null; }
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
            finally { Busy = false; _leaving = false; }
        }
        private async UniTask Cleanup()
        {
            InRoom = false; _overlay = GameplayOverlay.Game; RoomCode = ""; _copiedUntil = 0; CaptureMouse(false);
            if (Session != null) await Session.ShutdownAsync();
            _admitted.Clear();
            ReleasePrefab(ref _playerPrefab); ReleasePrefab(ref _matchPrefab);
            if (_characterContent.IsValid()) Addressables.Release(_characterContent); _characterContent = default;
            if (_weaponContent.IsValid()) Addressables.Release(_weaponContent); _weaponContent = default;
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
            UpdateOverlayInput();
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
            _operation?.Cancel(); Session?.Dispose();
            if (Manager != null) { Manager.Shutdown(); Destroy(Manager.gameObject); }
            LubanConfigService.Current.Reset(); if (Current == this) Current = null;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (_chineseFont != null) Destroy(_chineseFont);
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
                Panel(new Rect(0,0,1280,720),new Color(.055f,.075f,.10f));
                Panel(new Rect(60,90,8,520),PrototypeArena.Pink);
                GUI.Label(new Rect(92,104,540,74),"喷墨对战 / 局域网",_title);
                GUI.Label(new Rect(96,190,500,90),"墨流涂地赛 · 最多四人\n粉蓝两队，三分钟决胜负",_label);
                GUI.Label(new Rect(96,330,500,150),"WASD 移动　鼠标转动视角\n空格跳跃　鼠标左键持续射击\n按住 Shift 在己方墨水中潜行、回墨\nEsc 菜单　回车开始比赛（房主）",_small);
                GUI.Label(new Rect(96,515,500,95),"同一局域网内，房主创建房间后按 Esc，\n复制房间码发给伙伴；伙伴粘贴即可加入。\n房间码包含地址和端口，无需互联网。",_small);
                Panel(new Rect(660,55,560,605),new Color(.10f,.135f,.18f));
                GUI.Label(new Rect(695,73,490,34),"创建房间",_label);
                GUI.Label(new Rect(695,113,335,27),"房主地址（点击切换网卡）",_small);
                GUI.enabled=Ready&&!Busy;
                if(GUI.Button(new Rect(695,145,320,43),_localAddresses[_hostAddressIndex],_button)) CycleHostAddress();
                _port=GUI.TextField(new Rect(1030,145,150,43),_port,5,_field);
                GUI.Label(new Rect(1030,113,150,27),"UDP 端口",_small);
                if(GUI.Button(new Rect(695,199,485,44),"创建房间并生成房间码",_button)) ConnectFromUI(true);
                GUI.Label(new Rect(695,262,480,30),"填写房间码加入",_label);
                _roomCodeInput=GUI.TextField(new Rect(695,300,355,48),_roomCodeInput,80,_field);
                if(GUI.Button(new Rect(1065,300,115,48),"粘贴",_button)) _roomCodeInput=GUIUtility.systemCopyBuffer;
                if(GUI.Button(new Rect(695,360,485,44),"加入房间",_button)) ConnectRoomCode(_roomCodeInput).Forget();
                if(GUI.Button(new Rect(695,414,485,32),_advanced?"收起直接 IP 连接":"高级：使用 IPv4 地址直接连接",_small)) _advanced=!_advanced;
                if(_advanced)
                {
                    _ip=GUI.TextField(new Rect(695,453,320,43),_ip,45,_field);
                    if(GUI.Button(new Rect(1030,453,150,43),"直接加入",_button)) ConnectFromUI(false);
                    GUI.Label(new Rect(695,501,485,25),"使用上方 UDP 端口；同机测试可填 127.0.0.1。",_small);
                }
                GUI.enabled=true;
                GUI.Label(new Rect(695,535,485,28),Status,_small);
                if(Busy)
                {
                    Panel(new Rect(695,570,315*Mathf.Max(.04f,_progress),4),PrototypeArena.Blue);
                    if(Ready && GUI.Button(new Rect(1030,563,150,35),"取消连接",_button)) CancelConnection();
                }
                if(!string.IsNullOrEmpty(Error)) GUI.Label(new Rect(695,600,485,58),Error,_small);
                if(!Ready&&!Busy&&GUI.Button(new Rect(1030,560,150,35),"重试启动",_button)) Initialize().Forget();
                GUI.Label(new Rect(95,677,1100,30),"双方使用同一构建包。同机联机请选择回环地址；双机联机请选择双方可达的局域网网卡地址。",_small);
                return;
            }
            var match=PrototypeMatch.Current; var local=PrototypePlayer.Local;
            if(match==null||local==null) return;
            var state=match.State.Value; var player=local.PresentedState;
            var equipped = GameplayConfig.GetWeapon(player.WeaponId);
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
            ulong ping=Manager.IsHost?0:((UnityTransport)Manager.NetworkConfig.NetworkTransport).GetCurrentRtt(0);
            GUI.Label(new Rect(24,68,310,55),$"{Status}  /  {state.PlayerCount}/4\n延迟 {ping} 毫秒",_small);
            Panel(new Rect(24,578,330,116),new Color(.04f,.065f,.09f,.9f));
            GUI.Label(new Rect(42,590,290,32),$"{(player.Team==1?"粉队":"蓝队")}  /  生命 {player.Health:0}",_label);
            Panel(new Rect(42,635,285,15),new Color(.22f,.25f,.28f));
            Panel(new Rect(42,635,285*player.Ink/GameplayConfig.Character.MaxInk,15),PrototypeArena.TeamColor(player.Team));
            GUI.Label(new Rect(42,662,310,26),player.InkRecoverAt > player.SimulatedAt ? "射击后回墨锁定" : player.Ink < Splatoon.Combat.WeaponSimulation.InkCost(GameplayConfig.GetWeapon(player.WeaponId)) ? "墨量不足 / 松开射击回墨" : player.Swimming ? "潜墨中 / 快速回墨" : $"墨水 {player.Ink:0} / {GameplayConfig.Character.MaxInk:0}",_small);
            GUI.Label(new Rect(850,641,410,60),"左键射击　Shift 潜墨 / 回墨\nEsc 菜单 / 房间码　回车开始（房主）",_small);
            if (_captured && player.Health>0)
            {
                if (equipped.FireMode == 2)
                {
                    float charge = Splatoon.Combat.WeaponSimulation.ChargeRatio(player, equipped);
                    Panel(new Rect(570, 440, 140, 7), new Color(.2f,.23f,.27f));
                    Panel(new Rect(570, 440, 140 * charge, 7), PrototypeArena.TeamColor(player.Team));
                    bool limited = player.WeaponPhase == Splatoon.Combat.WeaponPhase.Charging && player.Ink < equipped.ShotInk &&
                        charge >= Mathf.Floor((player.Ink - equipped.ChargeMinInk) / (equipped.ShotInk - equipped.ChargeMinInk) * equipped.ChargeFrames) / equipped.ChargeFrames;
                    GUI.Label(new Rect(505,452,330,30), $"蓄力 {charge:P0} / " + (limited ? "墨量限制，松开发射" : "松开发射"), _small);
                }
                float gap = 5 + player.CurrentSpread;
                Color reticle = local.MuzzleBlocked ? Color.red : player.Ink < Splatoon.Combat.WeaponSimulation.InkCost(GameplayConfig.GetWeapon(player.WeaponId)) ? Color.yellow : Color.white;
                Panel(new Rect(639,360-gap-7,2,7),reticle); Panel(new Rect(639,360+gap,2,7),reticle);
                Panel(new Rect(640-gap-7,359,7,2),reticle); Panel(new Rect(640+gap,359,7,2),reticle);
                if (Time.unscaledTimeAsDouble < local.HitConfirmedUntil) GUI.Label(new Rect(628,347,90,35),local.LastHitKilled ? "× 击倒" : "×",_label);
                if (local.MuzzleBlocked) GUI.Label(new Rect(580,403,210,32),"枪口被遮挡",_small);
                if (player.Movement == Splatoon.Combat.MovementMode.WallInk) GUI.Label(new Rect(450,460,550,32),"W/S 上下　A/D 横移　空格跳离　松开 Shift 脱墙",_small);
            }
            if (state.Phase==MatchPhase.Practice) GUI.Label(new Rect(390,129,580,58),state.PlayerCount<2?"H 选择枪械 · 等待另一名玩家加入。":"H 选择枪械 · 房主按回车开始比赛。",_small);
            if(player.Health<=0) GUI.Label(new Rect(475,275,460,64),$"已被击倒！{Math.Max(0,player.RespawnsAt-Manager.ServerTime.Time):0.0} 秒后重生",_label);
            if(_overlay == GameplayOverlay.RoomMenu)
            {
                Panel(new Rect(430,222,420,295),new Color(.055f,.075f,.1f,.97f));
                string winner=PrototypeRules.Winner(state.PinkArea,state.BlueArea) switch {1=>"粉队获胜",2=>"蓝队获胜",_=>"平局"};
                GUI.Label(new Rect(463,242,360,44),state.Phase==MatchPhase.Finished?winner:"房间菜单",_label);
                if(state.Phase!=MatchPhase.Finished && GUI.Button(new Rect(465,303,350,48),"继续游戏",_button)) CaptureMouse(true);
                GUI.enabled=Manager.IsServer&&PrototypeRules.CanStart(state.PlayerCount,state.Phase,GameplayConfig.Mode.MinPlayers);
                if(GUI.Button(new Rect(465,367,350,48),state.Phase==MatchPhase.Finished?"再来一局":"开始比赛",_button)) {match.StartRound();CaptureMouse(true);}
                GUI.enabled=!Busy;
                if(GUI.Button(new Rect(465,432,350,48),"退出房间",_button)) Leave().Forget();
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
