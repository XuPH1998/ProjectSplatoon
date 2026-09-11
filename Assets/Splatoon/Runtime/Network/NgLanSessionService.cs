using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Networking.Transport;
using UnityEngine;

namespace Splatoon.Networking
{
    public sealed class NgLanSessionService : ILanSessionService
    {
        public NetworkSessionState State { get; private set; } = NetworkSessionState.Offline;
        public async UniTask<HostResult> StartHostAsync(LanHostOptions options)
        {
            State = NetworkSessionState.Starting;
            var manager = EnsureManager();
            if (manager == null) { State = NetworkSessionState.Failed; return new HostResult(false, "NetworkManager.Singleton is missing."); }
            if (manager.NetworkConfig.NetworkTransport is UnityTransport transport) transport.SetConnectionData("127.0.0.1", options.Port);
            bool ok = manager.StartHost();
            State = ok ? NetworkSessionState.Hosting : NetworkSessionState.Failed;
            await UniTask.CompletedTask;
            return new HostResult(ok, ok ? null : "NGO failed to start host.");
        }
        public async UniTask<JoinResult> JoinAsync(LanJoinOptions options)
        {
            State = NetworkSessionState.Joining;
            var manager = EnsureManager();
            if (manager == null) { State = NetworkSessionState.Failed; return new JoinResult(false, "NetworkManager.Singleton is missing."); }
            if (manager.NetworkConfig.NetworkTransport is UnityTransport transport) transport.SetConnectionData(options.Address, options.Port);
            bool ok = manager.StartClient();
            State = ok ? NetworkSessionState.Connected : NetworkSessionState.Failed;
            await UniTask.CompletedTask;
            return new JoinResult(ok, ok ? null : "NGO failed to start client.");
        }
        public async UniTask ShutdownAsync()
        {
            NetworkManager.Singleton?.Shutdown();
            State = NetworkSessionState.Offline;
            await UniTask.CompletedTask;
        }
        private static NetworkManager EnsureManager()
        {
            if (NetworkManager.Singleton != null) return NetworkManager.Singleton;
            var go = new GameObject("NetworkManager");
            Object.DontDestroyOnLoad(go);
            var manager = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();
            manager.NetworkConfig.NetworkTransport = transport;
            return manager;
        }
    }
}
