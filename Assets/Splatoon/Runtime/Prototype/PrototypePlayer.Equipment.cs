using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        struct EquipRequest { public uint Id, Round, Life; public int Weapon; public WeaponSelectionOrigin Origin; }
        EquipRequest? _equipRequest;
        uint _equipRequestId, _lastEquipRequest, _awaitEquipmentRevision;
        bool _equipReplyReceived;
        public bool WeaponChangePending { get; private set; }
        public string WeaponChangeMessage { get; private set; } = "";
        public void RequestWeaponChange(int weaponId, WeaponSelectionOrigin origin)
        {
            if (!IsOwner || !IsSpawned || WeaponChangePending || PrototypeMatch.Current == null) return;
            WeaponChangePending = true; _equipReplyReceived = false; WeaponChangeMessage = "切换中…";
            ChangeWeaponRpc(weaponId, origin, ++_equipRequestId, PrototypeMatch.Current.State.Value.Round, Snapshot.Value.Revision);
        }
        [Rpc(SendTo.Server)]
        void ChangeWeaponRpc(int weaponId, WeaponSelectionOrigin origin, uint request, uint round, uint life, RpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId || request <= _lastEquipRequest) return;
            _lastEquipRequest = request;
            _equipRequest = new EquipRequest { Id = request, Weapon = weaponId, Origin = origin, Round = round, Life = life };
        }
        void ApplyWeaponRequest(ref PlayerSnapshot s, MatchPhase phase)
        {
            if (!_equipRequest.HasValue) return;
            var request = _equipRequest.Value; _equipRequest = null;
            string error = WeaponSelectionRules.Validate(s, request.Weapon, request.Origin, request.Round,
                PrototypeMatch.Current.State.Value.Round, request.Life, phase, WeaponSelectionRules.Development);
            if (error == null) WeaponSelectionRules.Apply(ref s, request.Weapon, phase == MatchPhase.Practice, _lastInput);
            WeaponChangeReplyRpc(request.Id, s.EquipmentRevision, error ?? "已装备");
        }
        [Rpc(SendTo.Owner)]
        void WeaponChangeReplyRpc(uint request, uint revision, string message)
        {
            if (request != _equipRequestId) return;
            _equipReplyReceived = true; _awaitEquipmentRevision = revision; WeaponChangeMessage = message;
            RefreshWeaponChangeStatus();
        }
        public void RefreshWeaponChangeStatus()
        {
            if (WeaponChangePending && _equipReplyReceived && Snapshot.Value.EquipmentRevision >= _awaitEquipmentRevision)
                WeaponChangePending = false;
        }
    }
}
