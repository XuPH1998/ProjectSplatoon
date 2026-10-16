#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Receive-body branch fixture only. No socket, real peer, NGO wire or host ACK is represented.
    public sealed class TerrainDependencyFixtureTransport : NetworkTransport
    {
        public override ulong ServerClientId => 0;
        public int Sends { get; private set; }
        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery) { Sends++; }
        public override NetworkEvent PollEvent(out ulong id, out ArraySegment<byte> payload, out float time)
        { id = 0; payload = default; time = Time.realtimeSinceStartup; return NetworkEvent.Nothing; }
        public override bool StartClient() => true;
        public override bool StartServer() => false;
        public override void DisconnectRemoteClient(ulong id) { }
        public override void DisconnectLocalClient() { }
        public override ulong GetCurrentRtt(ulong id) => 0;
        public override void Shutdown() { }
        public override void Initialize(NetworkManager manager = null) { }
    }

    internal static class TerrainDependencyFixtureReflection
    {
        internal const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        internal static MemberInfo Member(Type type, string name, bool isStatic = false)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var flags = (isStatic ? AllStatic : AllInstance) | BindingFlags.DeclaredOnly;
                var field = current.GetField(name, flags); if (field != null) return field;
                var property = current.GetProperty(name, flags); if (property != null) return property;
            }
            Assert.Fail("Required V2 fixture member missing: " + type.FullName + "." + name); return null;
        }
        internal static object Read(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null, "Read owner for " + name);
            var member = Member(owner.GetType(), name);
            return member is FieldInfo field ? field.GetValue(owner) : ((PropertyInfo)member).GetValue(owner);
        }
        internal static T Read<T>(object owner, string name) => (T)Read(owner, name);
        internal static void Write(object owner, string name, object value)
        {
            var member = Member(owner.GetType(), name);
            if (member is FieldInfo field) field.SetValue(owner, value); else ((PropertyInfo)member).SetValue(owner, value);
        }
        internal static object ReadStatic(Type type, string name)
        {
            var member = Member(type, name, true);
            return member is FieldInfo field ? field.GetValue(null) : ((PropertyInfo)member).GetValue(null);
        }
        internal static void WriteStatic(Type type, string name, object value)
        {
            var member = Member(type, name, true);
            if (member is FieldInfo field) field.SetValue(null, value); else ((PropertyInfo)member).SetValue(null, value);
        }
        internal static MethodInfo Method(Type type, string name, int argumentCount)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var candidates = current.GetMethods(AllInstance | BindingFlags.DeclaredOnly)
                    .Where(method => method.Name == name && method.GetParameters().Length == argumentCount).ToArray();
                if (candidates.Length == 1) return candidates[0];
                Assert.That(candidates.Length, Is.LessThanOrEqualTo(1), "Ambiguous fixture method: " + name);
            }
            Assert.Fail("Required fixture method missing: " + type.FullName + "." + name); return null;
        }
        internal static object Call(object owner, string name, params object[] arguments)
        {
            try { return Method(owner.GetType(), name, arguments.Length).Invoke(owner, arguments); }
            catch (TargetInvocationException error)
            {
                // Preserve the real production/RPC throw site for fixture failures.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                throw;
            }
        }
        internal static IList Rows(object report, string name) => (IList)Read(report, name);
        internal static object Nested(Type owner, string name)
        {
            var type = owner.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(type, Is.Not.Null, "Required nested report type: " + name);
            return Activator.CreateInstance(type, true);
        }
    }

    internal sealed class TerrainDependencyFixtureSubscription : IDisposable
    {
        readonly EventInfo _event;
        readonly Delegate _handler;
        readonly Action<object, object> _capture;
        internal TerrainDependencyFixtureSubscription(Type owner, string eventName, Action<object, object> capture)
        {
            _capture = capture;
            _event = owner.GetEvent(eventName, TerrainDependencyFixtureReflection.AllStatic);
            Assert.That(_event, Is.Not.Null, "Required provider event: " + eventName);
            var args = _event.EventHandlerType.GetGenericArguments();
            Assert.That(args.Length, Is.EqualTo(2), "Provider must retain the Action<TSender,TValue> contract");
            var method = GetType().GetMethod(nameof(Capture), BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(args);
            _handler = Delegate.CreateDelegate(_event.EventHandlerType, this, method);
            _event.AddEventHandler(null, _handler);
        }
        void Capture<TSender, TValue>(TSender sender, TValue value) => _capture(sender, value);
        public void Dispose() => _event.RemoveEventHandler(null, _handler);
    }

    internal sealed class TerrainDependencyObservationV2FixtureRig : IDisposable
    {
        internal readonly BlockTerrain Terrain;
        internal readonly PrototypeMatch Match;
        internal readonly BlockNetworkProbe Probe;
        internal readonly NetworkManager Manager;
        internal readonly List<object> TransferObservations = new();
        internal readonly List<object> InstallObservations = new();
        internal object MainReport => TerrainDependencyFixtureReflection.Read(Probe, "_report");
        internal object TraceReport => TerrainDependencyFixtureReflection.Read(Probe, "_terrainDependencies");
        readonly List<GameObject> _objects = new();
        readonly List<IDisposable> _subscriptions = new();
        readonly List<(EventInfo source, Delegate handler)> _probeSubscriptions = new();
        readonly PrototypeMatch _priorMatch;
        readonly string _output;
        readonly Action<BlockTerrainCommit> _published;
        double _now = 10;

        internal TerrainDependencyObservationV2FixtureRig(uint round = 4)
        {
            Assert.That(NetworkManager.Singleton, Is.Null, "Use an empty scene; do not borrow the running project's NGO manager");
            _priorMatch = PrototypeMatch.Current;
            var managerObject = Make("Terrain dependency branch fixture manager", true);
            var transport = managerObject.AddComponent<TerrainDependencyFixtureTransport>();
            Manager = managerObject.AddComponent<NetworkManager>();
            Manager.NetworkConfig = new NetworkConfig();
            Manager.NetworkConfig.NetworkTransport = transport;
            Manager.NetworkConfig.EnableSceneManagement = false;
            Manager.NetworkConfig.ClientConnectionBufferTimeout = 120;
            Assert.That(Manager.StartClient(), Is.True, "Dummy transport must satisfy generated RPC guards");

            var terrainObject = Make("Terrain dependency real asynchronous terrain", true);
            Terrain = terrainObject.AddComponent<BlockTerrain>(); Terrain.Initialize(null); Terrain.Clear(round);
            var matchObject = Make("Terrain dependency receive-body fixture match", false);
            var networkObject = matchObject.AddComponent<NetworkObject>();
            Match = matchObject.AddComponent<PrototypeMatch>();
            BindFixtureNetworkBehaviour(Match, networkObject, 9001);
            TerrainDependencyFixtureReflection.Write(Match, "Blocks", Terrain);
            TerrainDependencyFixtureReflection.Write(Match, "_paintRound", round);
            TerrainDependencyFixtureReflection.Write(Match, "InitialSyncComplete", true);
            TerrainDependencyFixtureReflection.Write(Match, "_blockObservedTerrain", Terrain);
            TerrainDependencyFixtureReflection.WriteStatic(typeof(PrototypeMatch), "Current", Match);
            _published = commit => TerrainDependencyFixtureReflection.Call(Match, "OnBlockPublished", commit);
            Terrain.PublishedCommit += _published;

            // Never activate: Awake would write an unrelated report and may run the command-line harness.
            Probe = Make("Terrain dependency inactive collector fixture", false).AddComponent<BlockNetworkProbe>();
            var main = TerrainDependencyFixtureReflection.Nested(typeof(BlockNetworkProbe), "Report");
            TerrainDependencyFixtureReflection.Write(main, "session", "fixture-" + Guid.NewGuid().ToString("N"));
            TerrainDependencyFixtureReflection.Write(main, "label", "V2 branch fixture");
            TerrainDependencyFixtureReflection.Write(main, "role", "client");
            TerrainDependencyFixtureReflection.Write(Probe, "_report", main);
            var trace = TerrainDependencyFixtureReflection.Nested(typeof(BlockNetworkProbe), "TerrainDependenciesReport");
            TerrainDependencyFixtureReflection.Write(trace, "session", TerrainDependencyFixtureReflection.Read(main, "session"));
            TerrainDependencyFixtureReflection.Write(trace, "label", "V2 branch fixture");
            _output = Path.GetFullPath("Reports/BlockTerrain/PredictionAudit/TerrainDependencyObservation16/V2/FixtureRuntime/" +
                TerrainDependencyFixtureReflection.Read(main, "session") + ".json");
            TerrainDependencyFixtureReflection.Write(trace, "sourceProbePath", _output);
            TerrainDependencyFixtureReflection.Write(trace, "clockFrequency", System.Diagnostics.Stopwatch.Frequency);
            TerrainDependencyFixtureReflection.Write(Probe, "_output", _output);
            TerrainDependencyFixtureReflection.Write(Probe, "_terrainDependencies", trace);
            SetBaseline("_terrainDependencyProviderBaseline", typeof(PrototypePlayer), "ReplayDependencyProviderErrors");
            SetBaseline("_terrainTransferProviderBaseline", typeof(PrototypeMatch), "TerrainTransferProviderErrors");
            SetBaseline("_terrainInstallProviderBaseline", typeof(BlockTerrain), "RemoteInstallProviderErrors");
            TerrainDependencyFixtureReflection.Write(Probe, "_terrainDependencySubscribed", true);
            // Main consumer is first, exactly as normal Awake; source token is set only after its real append.
            SubscribeProbe(typeof(PrototypePlayer), "ReconcileObserved", "OnReconcileObserved");
            SubscribeProbe(typeof(PrototypePlayer), "ReplayDependencyObserved", "OnReplayDependencyObserved");
            SubscribeProbe(typeof(PrototypeMatch), "TerrainTransferObserved", "OnTerrainTransferObserved");
            SubscribeProbe(typeof(BlockTerrain), "RemoteInstallObserved", "OnRemoteInstallObserved");
            SubscribeProbe(typeof(PrototypePlayer), "ReconcileObserved", "OnTerrainDependencyReconcile");
            _subscriptions.Add(new TerrainDependencyFixtureSubscription(typeof(PrototypeMatch), "TerrainTransferObserved",
                (sender, value) => { if (ReferenceEquals(sender, Match)) TransferObservations.Add(value); }));
            _subscriptions.Add(new TerrainDependencyFixtureSubscription(typeof(BlockTerrain), "RemoteInstallObserved",
                (sender, value) => { if (ReferenceEquals(sender, Terrain)) InstallObservations.Add(value); }));
        }
        GameObject Make(string name, bool active)
        { var value = new GameObject(name); value.SetActive(active); _objects.Add(value); return value; }
        void SetBaseline(string target, Type owner, string source)
        { TerrainDependencyFixtureReflection.Write(Probe, target, TerrainDependencyFixtureReflection.ReadStatic(owner, source)); }
        void SubscribeProbe(Type owner, string eventName, string methodName)
        {
            var source = owner.GetEvent(eventName, TerrainDependencyFixtureReflection.AllStatic);
            Assert.That(source, Is.Not.Null, eventName);
            var handler = Delegate.CreateDelegate(source.EventHandlerType, Probe,
                TerrainDependencyFixtureReflection.Method(typeof(BlockNetworkProbe), methodName, 2));
            source.AddEventHandler(null, handler); _probeSubscriptions.Add((source, handler));
        }
        internal void BindFixtureNetworkBehaviour(NetworkBehaviour behaviour, NetworkObject obj, ulong id)
        {
            // Fixture metadata, not a claim that NGO spawned or transmitted this object.
            TerrainDependencyFixtureReflection.Write(obj, "NetworkManagerOwner", Manager);
            TerrainDependencyFixtureReflection.Write(obj, "NetworkObjectId", id);
            TerrainDependencyFixtureReflection.Write(obj, "IsSpawned", true);
            TerrainDependencyFixtureReflection.Write(behaviour, "m_NetworkManager", Manager);
            TerrainDependencyFixtureReflection.Write(behaviour, "m_NetworkObject", obj);
            TerrainDependencyFixtureReflection.Write(behaviour, "RpcTarget", Manager.RpcTarget);
            TerrainDependencyFixtureReflection.Write(behaviour, "IsSpawned", true);
            TerrainDependencyFixtureReflection.Write(behaviour, "IsServer", false);
            TerrainDependencyFixtureReflection.Write(behaviour, "IsClient", true);
            TerrainDependencyFixtureReflection.Write(behaviour, "IsHost", false);
            TerrainDependencyFixtureReflection.Call(behaviour, "InitializeVariables");
        }
        internal void Receive(BlockTransferFragment fragment)
        {
            var stage = TerrainDependencyFixtureReflection.Member(typeof(NetworkBehaviour), "__rpc_exec_stage") as FieldInfo;
            Assert.That(stage, Is.Not.Null);
            var prior = stage.GetValue(Match);
            try
            {
                stage.SetValue(Match, Enum.ToObject(stage.FieldType, 1)); // NGO __RpcExecStage.Execute in installed package.
                TerrainDependencyFixtureReflection.Call(Match, "BlockFragmentClientRpc", fragment, default(ClientRpcParams));
            }
            finally { stage.SetValue(Match, prior); }
        }
        internal void ReceiveAll(byte[] bytes, uint id, uint revision, bool full, uint baseline = 0, bool corrupt = false)
        {
            int count = (bytes.Length + BlockTransferFragment.PayloadBytes - 1) / BlockTransferFragment.PayloadBytes;
            for (int index = 0; index < count; index++)
            {
                var part = Part(bytes, Terrain.Round, id, baseline, revision, full, index);
                if (corrupt && index == 0) part.Data[0] ^= 1;
                Receive(part);
            }
        }
        internal static BlockTransferFragment Part(byte[] bytes, uint round, uint id, uint baseline, uint revision, bool full, int index)
        {
            int offset = index * BlockTransferFragment.PayloadBytes;
            var data = new byte[Math.Min(BlockTransferFragment.PayloadBytes, bytes.Length - offset)];
            Buffer.BlockCopy(bytes, offset, data, 0, data.Length);
            return new BlockTransferFragment { Round = round, Id = id, BaseRevision = baseline, Revision = revision,
                Full = full, Hash = BlockTransferFragment.Checksum(bytes), Length = bytes.Length, Index = index, Data = data };
        }
        internal object ApplyScoped(byte[] bytes, uint id, uint revision)
        {
            var part = Part(bytes, Terrain.Round, id, 0, revision, true, 0);
            var scope = TerrainDependencyFixtureReflection.Call(Terrain, "ObserveRemoteTransferScope", part, Match.GetInstanceID());
            try
            {
                Assert.That(Terrain.ApplyRemoteSnapshot(bytes), Is.True);
                return TerrainDependencyFixtureReflection.Read(scope, "Accepted");
            }
            finally { ((IDisposable)scope).Dispose(); }
        }
        internal void Tick()
        { _now += 1d / 60; Terrain.Tick(false, _now, 1f / 60); }
        internal void PollOriginalAck() => TerrainDependencyFixtureReflection.Call(Match, "UpdateBlockSync");
        internal void ResetOriginalSync() => TerrainDependencyFixtureReflection.Call(Match, "ResetBlockSync");
        internal BlockTransferAssembly Incoming => (BlockTransferAssembly)TerrainDependencyFixtureReflection.Read(Match, "_blockIncoming");
        internal object FinishSidecar(bool formalReconcileComplete = true)
        {
            TerrainDependencyFixtureReflection.Write(MainReport, "complete", true);
            TerrainDependencyFixtureReflection.Write(MainReport, "reconcileEventsComplete", formalReconcileComplete);
            TerrainDependencyFixtureReflection.Write(MainReport, "reconcileEventsIncluded", true);
            var before = JsonUtility.ToJson(MainReport); int exit = TerrainDependencyFixtureReflection.Read<int>(Probe, "_exitCode");
            TerrainDependencyFixtureReflection.Call(Probe, "FinishTerrainDependencyCollection");
            Assert.That(JsonUtility.ToJson(MainReport), Is.EqualTo(before), "Sidecar Finish cannot edit formal report");
            Assert.That(TerrainDependencyFixtureReflection.Read<int>(Probe, "_exitCode"), Is.EqualTo(exit));
            Assert.That(TerrainDependencyFixtureReflection.Read<bool>(Probe, "TerrainDependenciesWritten"), Is.True,
                TerrainDependencyFixtureReflection.Read<string>(Probe, "TerrainDependenciesWriteError"));
            Assert.That(File.Exists(_output + ".terrain-dependencies.json"), Is.True);
            return TraceReport;
        }
        public void Dispose()
        {
            // Remove all fixture-created subscriptions even if the production Stop method already removed its own.
            foreach (var subscription in _subscriptions) subscription.Dispose();
            foreach (var subscription in _probeSubscriptions) subscription.source.RemoveEventHandler(null, subscription.handler);
            if (Terrain != null) Terrain.PublishedCommit -= _published;
            TerrainDependencyFixtureReflection.WriteStatic(typeof(PrototypeMatch), "Current", _priorMatch);
            foreach (var obj in _objects)
            {
                if (obj == null) continue;
                foreach (var behaviour in obj.GetComponents<NetworkBehaviour>())
                    TerrainDependencyFixtureReflection.Write(behaviour, "IsSpawned", false);
                var net = obj.GetComponent<NetworkObject>(); if (net != null) TerrainDependencyFixtureReflection.Write(net, "IsSpawned", false);
            }
            if (Manager != null) Manager.Shutdown();
            for (int index = _objects.Count - 1; index >= 0; index--) if (_objects[index] != null) Object.DestroyImmediate(_objects[index]);
        }
    }
}
#endif
