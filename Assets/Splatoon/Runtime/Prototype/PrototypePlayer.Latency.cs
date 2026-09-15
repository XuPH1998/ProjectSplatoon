using Splatoon.Networking;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        public GameplayLatency GameLatency { get; } = new();
        void UpdateGameLatency()
        {
            if (IsSpawned && ControlsLocalPlayer && !IsServer && NetworkManager.IsConnectedClient &&
                GameLatency.TryBegin(Time.realtimeSinceStartupAsDouble, out uint id)) LatencyRequestRpc(id);
        }
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void LatencyRequestRpc(uint id, RpcParams rpc = default)
        {
            if (!IsTestBot && rpc.Receive.SenderClientId == OwnerClientId) LatencyReplyRpc(id);
        }
        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        void LatencyReplyRpc(uint id)
        {
            if (ControlsLocalPlayer && !IsServer) GameLatency.Complete(id, Time.realtimeSinceStartupAsDouble);
        }
    }
}
