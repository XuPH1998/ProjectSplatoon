using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        // NPCs are server-owned NetworkObjects, never fake network clients or player objects.
        public readonly NetworkVariable<bool> TestBot = new();
        public bool IsTestBot => TestBot.Value;
        bool ControlsLocalPlayer => IsOwner && !IsTestBot;
        public ulong PlayerId => IsTestBot ? (1UL << 63) | NetworkObjectId : OwnerClientId;
        bool _initialTestBot;
        int _botHero;
        Vector3 _botPosition;
        float _botYaw;
        public void InitializeTestBot(byte team, byte slot, int hero, Vector3 position, float yaw)
        {
            Initialize(team, slot); _initialTestBot = true; _botHero = hero; _botPosition = position; _botYaw = yaw;
        }
    }
}
