using System.Linq;
using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        struct TeamRequest { public uint Id, Round, Life; public byte Target; }
        TeamRequest? _teamRequest;
        uint _teamRequestId, _lastTeamRequest, _awaitTeamRevision;
        bool _teamReplyReceived;
        public bool TeamChangePending { get; private set; }
        public string TeamChangeMessage { get; private set; } = "";
        public string TeamChangeUnavailableReason
        {
            get
            {
                var match = PrototypeMatch.Current;
                if (!ControlsLocalPlayer || !IsSpawned || match == null || !match.InitialSyncComplete) return "正在同步房间";
                if (TeamChangePending || HeroChangePending) return "正在处理切换请求";
                var s = Snapshot.Value;
                return TeamSelectionRules.Validate(s, (byte)(3 - s.Team), match.State.Value.Round,
                    match.State.Value.Round, s.Revision, match.State.Value.Phase,
                    ByOwner.Values.Where(p => p != null && p.IsSpawned).Select(p => p.Snapshot.Value), out _);
            }
        }
        public void RequestTeamChange()
        {
            string error = TeamChangeUnavailableReason;
            if (error != null) { TeamChangeMessage = error; return; }
            TeamChangePending = true; _teamReplyReceived = false; TeamChangeMessage = "更换中…";
            ChangeTeamRpc((byte)(3 - Snapshot.Value.Team), ++_teamRequestId,
                PrototypeMatch.Current.State.Value.Round, Snapshot.Value.Revision);
        }
        [Rpc(SendTo.Server)]
        void ChangeTeamRpc(byte target, uint request, uint round, uint life, RpcParams rpc = default)
        {
            if (IsTestBot || rpc.Receive.SenderClientId != OwnerClientId || request <= _lastTeamRequest) return;
            _lastTeamRequest = request;
            if (_teamRequest.HasValue || _heroRequest.HasValue)
            { TeamChangeReplyRpc(request, Snapshot.Value.Revision, "正在处理切换请求"); return; }
            _teamRequest = new TeamRequest { Id = request, Target = target, Round = round, Life = life };
        }
        bool ApplyTeamRequest(MatchPhase phase)
        {
            if (!_teamRequest.HasValue) return false;
            var request = _teamRequest.Value; _teamRequest = null;
            var match = PrototypeMatch.Current;
            string error = TeamSelectionRules.Validate(Snapshot.Value, request.Target, request.Round,
                match.State.Value.Round, request.Life, phase,
                match.Players.Where(p => p != null && p.IsSpawned).Select(p => p.Snapshot.Value), out byte slot);
            if (error == null) Respawn(request.Target, slot, true);
            TeamChangeReplyRpc(request.Id, Snapshot.Value.Revision,
                error ?? (request.Target == 1 ? "已更换到粉队" : "已更换到蓝队"));
            return error == null;
        }
        [Rpc(SendTo.Owner)]
        void TeamChangeReplyRpc(uint request, uint revision, string message)
        {
            if (request != _teamRequestId) return;
            _teamReplyReceived = true; _awaitTeamRevision = revision; TeamChangeMessage = message;
            RefreshTeamChangeStatus();
        }
        public void RefreshTeamChangeStatus()
        {
            if (TeamChangePending && _teamReplyReceived && Snapshot.Value.Revision >= _awaitTeamRevision)
                TeamChangePending = false;
        }
    }
}
