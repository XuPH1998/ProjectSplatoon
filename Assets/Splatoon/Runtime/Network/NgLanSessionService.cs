using System;
using Splatoon.Config;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Splatoon.Prototype;
using Unity.Netcode.Transports.UTP;

namespace Splatoon.Networking
{
    public sealed class NgLanSessionService : ILanSessionService, IDisposable
    {
        private readonly NetworkManager _manager;
        private bool _intentionalShutdown;
        private double _joinStartedAt = -1;
        private int _joinStageLogMask;
        public NetworkSessionState State { get; private set; }
        public string LastError { get; private set; }
        public event Action<NetworkSessionState> StateChanged;
        public NgLanSessionService(NetworkManager manager)
        {
            _manager = manager;
            manager.OnClientConnectedCallback += OnConnected;
            manager.OnClientDisconnectCallback += OnDisconnected;
            manager.OnClientStopped += OnClientStopped;
            manager.OnTransportFailure += OnTransportFailure;
        }
        private void SetState(NetworkSessionState state, string error = null)
        { State = state; LastError = error; StateChanged?.Invoke(state); }
        public async UniTask<HostResult> StartHostAsync(LanHostOptions options, CancellationToken token = default)
        {
            if (!LanDiscoveryProtocol.ValidGamePort(options.Port)) return new HostResult(false, "游戏端口无效，47777 为房间发现保留端口。");
            token.ThrowIfCancellationRequested();
            if (_manager.IsListening) return new HostResult(false, "已有房间正在运行，请先退出。");
            _intentionalShutdown = false;
            SetState(NetworkSessionState.Starting);
            try
            {
                ((UnityTransport)_manager.NetworkConfig.NetworkTransport).SetConnectionData("127.0.0.1", options.Port, options.LoopbackOnly ? "127.0.0.1" : "0.0.0.0");
                if (!_manager.StartHost()) throw new InvalidOperationException("创建房间失败，请检查 UDP 端口是否被占用。");
                SetState(NetworkSessionState.Hosting);
                await UniTask.CompletedTask;
                return new HostResult(true);
            }
            catch (Exception e) { SetState(NetworkSessionState.Failed, ChineseText.Error(e.Message, "创建房间失败，请检查端口是否被占用。")); return new HostResult(false, LastError); }
        }
        public async UniTask<JoinResult> JoinAsync(LanJoinOptions options, CancellationToken token = default)
        {
            if (!LanDiscoveryProtocol.ValidGamePort(options.Port)) return new JoinResult(false, "游戏端口无效，47777 为房间发现保留端口。");
            if (_manager.IsListening) return new JoinResult(false, "已有房间正在运行，请先退出。");
            if (!IPAddress.TryParse(options.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return new JoinResult(false, "请输入有效的 IPv4 地址。");
            _intentionalShutdown = false;
            _joinStartedAt = UnityEngine.Time.realtimeSinceStartupAsDouble;
            _joinStageLogMask = 0;
            SetState(NetworkSessionState.Joining);
            try
            {
                token.ThrowIfCancellationRequested();
                ((UnityTransport)_manager.NetworkConfig.NetworkTransport).SetConnectionData(options.Address, options.Port);
                LogJoinStage(1, "StartClient");
                if (!_manager.StartClient()) throw new InvalidOperationException("无法启动网络客户端，请重试。");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var timeoutTimer = timeout.CancelAfterSlim(TimeSpan.FromSeconds(GameplayConfig.Global.ConnectionTimeout));
                await UniTask.WaitUntil(() => State != NetworkSessionState.Joining, cancellationToken: timeout.Token);
                if (State != NetworkSessionState.Connected) throw new InvalidOperationException(LastError ?? "连接失败，请检查房主是否在线。");
                return new JoinResult(true);
            }
            catch (Exception e)
            {
                string error = e is OperationCanceledException ? (token.IsCancellationRequested ? "连接已取消。" : "连接超时，请检查房间码、房主地址、端口和防火墙。") : ChineseText.Error(e.Message, "连接失败，请检查房主是否在线及防火墙设置。");
                await ShutdownAsync();
                SetState(NetworkSessionState.Failed, error);
                return new JoinResult(false, error);
            }
            finally { _joinStartedAt = -1; }
        }
        public async UniTask ShutdownAsync()
        {
            _intentionalShutdown = true;
            LogJoinStage(32, "IntentionalShutdown");
            _manager.Shutdown();
            await UniTask.WaitUntil(() => !_manager.ShutdownInProgress && !_manager.IsListening);
            SetState(NetworkSessionState.Offline);
        }
        private void OnConnected(ulong id)
        {
            if (!_manager.IsServer && id == _manager.LocalClientId)
            {
                LogJoinStage(2, "LocalConnected", id);
                SetState(NetworkSessionState.Connected);
            }
        }
        private void OnDisconnected(ulong id)
        {
            LogJoinStage(4, "Disconnect", id);
            if (_intentionalShutdown || _manager.IsServer) return;
            SetState(NetworkSessionState.Failed, string.IsNullOrWhiteSpace(_manager.DisconnectReason) ? "与房主的连接已断开，请重新加入。" : ChineseText.Error(_manager.DisconnectReason, "与房主的连接已断开，请重新加入。"));
        }
        private void OnClientStopped(bool wasHost)
        {
            LogJoinStage(8, "ClientStopped", wasHost: wasHost);
            if (_intentionalShutdown || State != NetworkSessionState.Joining) return;
            string error = LastError;
            if (string.IsNullOrWhiteSpace(error))
                error = ChineseText.Error(_manager.DisconnectReason, "连接在完成加入前已停止，请检查房主是否在线并重新加入。");
            SetState(NetworkSessionState.Failed, error);
        }
        private void OnTransportFailure()
        {
            LogJoinStage(16, "TransportFailure");
            SetState(NetworkSessionState.Failed, "网络传输失败，请检查 UDP 端口和网卡。");
        }
        private void LogJoinStage(int bit, string stage, ulong? callbackId = null, bool? wasHost = null)
        {
            if (_joinStartedAt < 0 || (_joinStageLogMask & bit) != 0) return;
            _joinStageLogMask |= bit;
            // Diagnostics cannot change connection state, retry, routing or errors.
            try
            {
                string elapsed = (UnityEngine.Time.realtimeSinceStartupAsDouble - _joinStartedAt)
                    .ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                string reason = (_manager.DisconnectReason ?? "").Replace("\r", " ").Replace("\n", " ");
                UnityEngine.Debug.Log($"[LAN JOIN] stage={stage} elapsed={elapsed}s state={State} " +
                    $"localId={_manager.LocalClientId} callbackId={callbackId} wasHost={wasHost} " +
                    $"listening={_manager.IsListening} connected={_manager.IsConnectedClient} " +
                    $"shutdown={_manager.ShutdownInProgress} intentional={_intentionalShutdown} reason=\"{reason}\"");
            }
            catch { /* A missing diagnostic must not turn a join callback into a failure. */ }
        }
        public void Dispose()
        {
            _manager.OnClientConnectedCallback -= OnConnected;
            _manager.OnClientDisconnectCallback -= OnDisconnected;
            _manager.OnClientStopped -= OnClientStopped;
            _manager.OnTransportFailure -= OnTransportFailure;
        }
    }
}
