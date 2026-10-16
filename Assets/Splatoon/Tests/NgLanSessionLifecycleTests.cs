#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Run with the preserved EditMode runner, serially. EnterPlayMode is explicit:
    // the existing saved preview scene must be loaded BEFORE entry. Renaming an
    // unsaved scene does not survive Unity's PlayMode scene reload.
    [Serializable]
    public sealed class NgLanSessionLifecycleTests
    {
        const string NoBootstrapScene = "RifleGirlChibiPreview";
        const string NoBootstrapScenePath = "Assets/GameResource/Characters/RifleGirlChibi/Preview/RifleGirlChibiPreview.unity";
        const string StoppedFallback = "连接在完成加入前已停止，请检查房主是否在线并重新加入。";
        const double SettleSeconds = 5;
        GameObject _root;
        NetworkManager _manager;
        NgLanSessionService _service;
        CancellationTokenSource _lifetime;
        Task _configLoad, _shutdown;
        Task<JoinResult> _join;
        bool _configurationWasReady, _configurationObserved, _restoreBackground, _originalBackground;
        // Serialize cleanup observations if the runner reconstructs this fixture.
        [SerializeField] bool _cleanupComplete, _managerReleased;
        int _starts, _connections, _disconnects, _transportFailures, _stops;
        double _joinStartedAt, _joinReturnedAt;
        StopSnapshot _beforeStop, _afterStop;
        readonly List<StateSnapshot> _states = new();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _root = null; _manager = null; _service = null; _lifetime = null;
            _configLoad = null; _shutdown = null; _join = null;
            _configurationObserved = false; _restoreBackground = false;
            Assert.That(Application.isPlaying, Is.False, "Use EditMode, not an already-running PlayMode suite.");
            Assert.That(SessionState.GetBool("RoomDiscovery.PlaySmoke", false), Is.False,
                "A pending lobby smoke request must not launch beside this fixture.");
            var scene = EditorSceneManager.OpenScene(NoBootstrapScenePath, OpenSceneMode.Single);
            Assert.That(scene.name, Is.EqualTo(NoBootstrapScene));
            yield return new EnterPlayMode();
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(NoBootstrapScene));
            Assert.That(PrototypeApp.Current, Is.Null, "The application bootstrap must have been skipped.");
            Assert.That(Object.FindFirstObjectByType<PrototypeApp>(FindObjectsInactive.Include), Is.Null);
            Assert.That(Object.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include), Is.Null);
            Assert.That(NetworkManager.Singleton, Is.Null);

            _lifetime = new CancellationTokenSource();
            _configurationWasReady = LubanConfigService.Current.IsReady;
            if (!_configurationWasReady)
            {
                Assert.That(LubanConfigService.Current.Tables, Is.Null, "Require a clean uninitialized config state.");
                Assert.That(LubanConfigService.Current.ContentSignature, Is.Null);
            }
            _configurationObserved = true;
            _configLoad = LubanConfigService.Current.InitializeAsync(_lifetime.Token).AsTask();
            yield return Wait(() => _configLoad.IsCompleted, 30, "original Luban assets loaded");
            _configLoad.GetAwaiter().GetResult();
            Assert.That(GameplayConfig.Global.ConnectionTimeout, Is.EqualTo(20), "Do not shorten the application timer.");

            _states.Clear(); _starts = _connections = _disconnects = _transportFailures = _stops = 0;
            _join = null; _shutdown = null; _joinReturnedAt = -1;
            _originalBackground = Application.runInBackground; _restoreBackground = true;
            _root = new GameObject("Real pending Join lifecycle fixture");
            _manager = _root.AddComponent<NetworkManager>();
            var transport = _root.AddComponent<UnityTransport>();
            _manager.NetworkConfig = new NetworkConfig
            { NetworkTransport = transport, EnableSceneManagement = false, ConnectionApproval = true };
            Assert.That(_manager.NetworkConfig.ClientConnectionBufferTimeout, Is.EqualTo(10),
                "Leave NGO's original approval configuration unchanged.");
            Assert.That(transport.UseWebSockets, Is.False, "Use real local UDP transport.");
            Assert.That(UnityTransport.s_DriverConstructor, Is.Null, "Do not substitute a test transport driver.");
            _manager.OnClientStarted += ClientStarted;
            _manager.OnClientConnectedCallback += ClientConnected;
            _manager.OnClientDisconnectCallback += ClientDisconnected;
            _manager.OnTransportFailure += TransportFailed;
            // Multicast order observes this same REAL event around the service callback.
            _manager.OnClientStopped += BeforeStopped;
            _service = new NgLanSessionService(_manager);
            _service.StateChanged += StateChanged;
            _manager.OnClientStopped += AfterStopped;
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator RealClientStoppedWakesPendingJoinBeforeOriginalTimeout()
        {
            StartPendingJoin(); yield return null; AssertJoining();
            _manager.Shutdown(true);
            yield return Wait(() => _stops == 1 && _join.IsCompleted, SettleSeconds, "real stop wakes Join");
            var result = _join.GetAwaiter().GetResult();
            AssertCleanStop();
            Assert.That(_beforeStop.State, Is.EqualTo(NetworkSessionState.Joining));
            Assert.That(_beforeStop.Error, Is.Null);
            Assert.That(_afterStop.State, Is.EqualTo(NetworkSessionState.Failed));
            Assert.That(_afterStop.StateChanges, Is.EqualTo(_beforeStop.StateChanges + 1),
                "The actual stopped callback produces the failure before the async catch resumes.");
            string stoppedError = ChineseText.Error(_beforeStop.Reason, StoppedFallback);
            Assert.That(_afterStop.Error, Is.EqualTo(stoppedError));
            Assert.That(result.Success, Is.False); Assert.That(result.Error, Is.EqualTo(stoppedError));
            Assert.That(_service.State, Is.EqualTo(NetworkSessionState.Failed));
            Assert.That(_service.LastError, Is.EqualTo(stoppedError));
            Assert.That(_joinReturnedAt - _afterStop.At, Is.InRange(0d, SettleSeconds));
            Assert.That(_joinReturnedAt - _joinStartedAt, Is.LessThan(10d),
                "Failure must be clearly earlier than the unchanged 20-second application timeout.");
            Debug.Log($"[LAN JOIN FIXTURE] case=UnexpectedStopped result=false joinSeconds={_joinReturnedAt - _joinStartedAt:F3} " +
                $"stoppedToReturnSeconds={_joinReturnedAt - _afterStop.At:F3} originalTimeout=20 starts={_starts} stops={_stops}");
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator ServiceShutdownDoesNotAddStoppedFailureToPendingJoin()
        {
            StartPendingJoin(); yield return null; AssertJoining();
            _shutdown = _service.ShutdownAsync().AsTask();
            yield return Wait(() => _stops == 1 && _shutdown.IsCompleted && _join.IsCompleted,
                SettleSeconds, "intentional shutdown and original Join continuation finish");
            _shutdown.GetAwaiter().GetResult(); var result = _join.GetAwaiter().GetResult();
            AssertCleanStop();
            Assert.That(_beforeStop.State, Is.EqualTo(NetworkSessionState.Joining));
            Assert.That(_afterStop.State, Is.EqualTo(_beforeStop.State));
            Assert.That(_afterStop.Error, Is.EqualTo(_beforeStop.Error));
            Assert.That(_afterStop.StateChanges, Is.EqualTo(_beforeStop.StateChanges),
                "Intentional stopped must add no failure notification within the real event.");
            Assert.That(result.Success, Is.False, "The existing pending Join may fail after Shutdown sets Offline.");
            Assert.That(result.Error, Does.Not.Contain("完成加入前已停止"));
            foreach (var state in _states)
                Assert.That(state.Error ?? "", Does.Not.Contain("完成加入前已停止"));
            Debug.Log($"[LAN JOIN FIXTURE] case=IntentionalShutdown stoppedBefore={_beforeStop.State} " +
                $"stoppedAfter={_afterStop.State} originalJoinResult={result.Success} finalState={_service.State}");
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator DisposedServiceIgnoresRealStoppedUntilCallerCancelsJoin()
        {
            StartPendingJoin(); yield return null; AssertJoining();
            int changes = _states.Count; string error = _service.LastError;
            _service.Dispose();
            _manager.Shutdown(true);
            yield return Wait(() => _stops == 1, SettleSeconds, "real stopped still reaches fixture observers");
            for (int frame = 0; frame < 3; frame++) yield return null;
            AssertCleanStop();
            Assert.That(_beforeStop.State, Is.EqualTo(NetworkSessionState.Joining));
            Assert.That(_afterStop.State, Is.EqualTo(NetworkSessionState.Joining));
            Assert.That(_service.State, Is.EqualTo(NetworkSessionState.Joining));
            Assert.That(_service.LastError, Is.EqualTo(error));
            Assert.That(_states.Count, Is.EqualTo(changes), "Disposed callbacks cannot notify the old service.");
            Assert.That(_join.IsCompleted, Is.False, "Observe unsubscription before cleanup cancellation changes state.");
            Assert.That(Time.realtimeSinceStartupAsDouble - _joinStartedAt, Is.LessThan(10d));
            Debug.Log($"[LAN JOIN FIXTURE] case=Disposed stoppedObserved={_stops} " +
                $"oldServiceState={_service.State} joinCompletedBeforeCleanup={_join.IsCompleted}");
            _lifetime.Cancel();
            yield return Wait(() => _join.IsCompleted, SettleSeconds, "original caller cancellation joins the abandoned task");
            var result = _join.GetAwaiter().GetResult();
            Assert.That(result.Success, Is.False); Assert.That(result.Error, Is.EqualTo("连接已取消。"));
        }

        void StartPendingJoin()
        {
            // Reserve an OS-selected loopback UDP port, then close it: no server/listener is kept alive.
            ushort port;
            do
            {
                using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                port = checked((ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port);
            } while (!LanDiscoveryProtocol.ValidGamePort(port));
            _joinStartedAt = Time.realtimeSinceStartupAsDouble;
            _join = ObserveJoinAsync(port).AsTask();
            AssertJoining();
        }

        async UniTask<JoinResult> ObserveJoinAsync(ushort port)
        {
            var result = await _service.JoinAsync(new LanJoinOptions("127.0.0.1", port), _lifetime.Token);
            _joinReturnedAt = Time.realtimeSinceStartupAsDouble;
            return result;
        }

        void AssertJoining()
        {
            Assert.That(_starts, Is.EqualTo(1)); Assert.That(_join.IsCompleted, Is.False);
            Assert.That(_service.State, Is.EqualTo(NetworkSessionState.Joining));
            Assert.That(_manager.IsListening, Is.True); Assert.That(_manager.IsConnectedClient, Is.False);
            Assert.That(_connections + _disconnects + _transportFailures + _stops, Is.Zero,
                "The scenario must reach pending Join without another callback providing its failure.");
        }

        void AssertCleanStop()
        {
            Assert.That(_starts, Is.EqualTo(1), "No retry or second StartClient.");
            Assert.That(_stops, Is.EqualTo(1)); Assert.That(_beforeStop.WasHost, Is.False);
            Assert.That(_connections + _disconnects + _transportFailures, Is.Zero,
                "This oracle isolates the real OnClientStopped path.");
            Assert.That(_manager.IsListening, Is.False); Assert.That(_manager.ShutdownInProgress, Is.False);
        }

        void ClientStarted() => _starts++;
        void ClientConnected(ulong id) => _connections++;
        void ClientDisconnected(ulong id) => _disconnects++;
        void TransportFailed() => _transportFailures++;
        void StateChanged(NetworkSessionState state) => _states.Add(new StateSnapshot(state, _service.LastError));
        void BeforeStopped(bool wasHost)
        { _stops++; _beforeStop = new StopSnapshot(_service, _manager, _states.Count, wasHost); }
        void AfterStopped(bool wasHost)
        { _afterStop = new StopSnapshot(_service, _manager, _states.Count, wasHost); }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            _lifetime?.Cancel();
            if (_manager != null && (_manager.IsListening || _manager.ShutdownInProgress)) _manager.Shutdown(true);
            double end = Time.realtimeSinceStartupAsDouble + SettleSeconds;
            while (((_join != null && !_join.IsCompleted) || (_shutdown != null && !_shutdown.IsCompleted) ||
                (_configLoad != null && !_configLoad.IsCompleted) ||
                (_manager != null && (_manager.IsListening || _manager.ShutdownInProgress))) &&
                Time.realtimeSinceStartupAsDouble < end) yield return null;
            _cleanupComplete = (_join == null || _join.IsCompleted) && (_shutdown == null || _shutdown.IsCompleted) &&
                (_configLoad == null || _configLoad.IsCompleted) &&
                (_manager == null || (!_manager.IsListening && !_manager.ShutdownInProgress));
            if (_service != null) { _service.StateChanged -= StateChanged; _service.Dispose(); }
            if (_manager != null)
            {
                _manager.OnClientStarted -= ClientStarted; _manager.OnClientConnectedCallback -= ClientConnected;
                _manager.OnClientDisconnectCallback -= ClientDisconnected; _manager.OnTransportFailure -= TransportFailed;
                _manager.OnClientStopped -= BeforeStopped; _manager.OnClientStopped -= AfterStopped;
            }
            if (_root != null) Object.DestroyImmediate(_root);
            _managerReleased = NetworkManager.Singleton == null &&
                Object.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include) == null;
            if (_configurationObserved && !_configurationWasReady) LubanConfigService.Current.Reset();
            if (_restoreBackground) Application.runInBackground = _originalBackground;
            _lifetime?.Dispose();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(_cleanupComplete, Is.True, "Join/config/shutdown tasks and the actual transport must settle before exit.");
            Assert.That(_managerReleased, Is.True, "The owned manager and its singleton must be removed.");
            // The parent runner's scene/settings preserver restores the pre-run Editor scene setup.
        }

        static IEnumerator Wait(Func<bool> ready, double seconds, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        readonly struct StateSnapshot
        {
            public readonly NetworkSessionState State; public readonly string Error;
            public StateSnapshot(NetworkSessionState state, string error) { State = state; Error = error; }
        }
        readonly struct StopSnapshot
        {
            public readonly NetworkSessionState State;
            public readonly string Error, Reason;
            public readonly int StateChanges;
            public readonly bool WasHost;
            public readonly double At;
            public StopSnapshot(NgLanSessionService service, NetworkManager manager, int changes, bool wasHost)
            { State = service.State; Error = service.LastError; Reason = manager.DisconnectReason;
                StateChanges = changes; WasHost = wasHost; At = Time.realtimeSinceStartupAsDouble; }
        }
    }
}
#endif
