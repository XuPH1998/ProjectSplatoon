using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        struct HeroRequest { public uint Id, Round, Life; public int Hero; public HeroSelectionOrigin Origin; }
        HeroRequest? _heroRequest;
        uint _heroRequestId, _lastHeroRequest, _awaitHeroRevision;
        bool _heroReplyReceived;
        public bool HeroChangePending { get; private set; }
        public string HeroChangeMessage { get; private set; } = "";
        public void RequestHeroChange(int heroId, HeroSelectionOrigin origin)
        {
            if (!ControlsLocalPlayer || !IsSpawned || HeroChangePending || TeamChangePending || PrototypeMatch.Current == null) return;
            HeroChangePending = true; _heroReplyReceived = false; HeroChangeMessage = "切换中…";
            ChangeHeroRpc(heroId, origin, ++_heroRequestId, PrototypeMatch.Current.State.Value.Round, Snapshot.Value.Revision);
        }
        [Rpc(SendTo.Server)]
        void ChangeHeroRpc(int heroId, HeroSelectionOrigin origin, uint request, uint round, uint life, RpcParams rpc = default)
        {
            if (IsTestBot || rpc.Receive.SenderClientId != OwnerClientId || request <= _lastHeroRequest) return;
            _lastHeroRequest = request;
            if (_heroRequest.HasValue || _teamRequest.HasValue)
            { HeroChangeReplyRpc(request, Snapshot.Value.HeroRevision, "正在处理切换请求"); return; }
            _heroRequest = new HeroRequest { Id = request, Hero = heroId, Origin = origin, Round = round, Life = life };
        }
        void ApplyHeroRequest(ref PlayerSnapshot s, MatchPhase phase)
        {
            if (!_heroRequest.HasValue) return;
            var request = _heroRequest.Value; _heroRequest = null;
            string error = HeroSelectionRules.Validate(s, request.Hero, request.Origin, request.Round,
                PrototypeMatch.Current.State.Value.Round, request.Life, phase, HeroSelectionRules.Development,
                PrototypeArena.Current != null && PrototypeArena.Current.IsInHeroChangeZone(s.Team, s.Position));
            if (error == null && s.HeroId != request.Hero)
            {
                var current = HeroBodyShape.For(s.HeroId); var target = HeroBodyShape.For(request.Hero);
                bool grows = target.Height > current.Height || target.Radius > current.Radius || target.CompactHeight > current.CompactHeight;
                if (grows && !_motor.CanFitHero(request.Hero, PlayerMotorSimulation.HumanPosition(s)))
                    error = "空间不足，无法切换到该英雄";
            }
            if (error == null && HeroSelectionRules.Apply(ref s, request.Hero, phase == MatchPhase.Practice, _lastInput))
            { EnsureHeroPresentation(s.HeroId); _motor.Restore(s); }
            HeroChangeReplyRpc(request.Id, s.HeroRevision, error ?? "当前英雄");
        }
        [Rpc(SendTo.Owner)]
        void HeroChangeReplyRpc(uint request, uint revision, string message)
        {
            if (request != _heroRequestId) return;
            _heroReplyReceived = true; _awaitHeroRevision = revision; HeroChangeMessage = message;
            RefreshHeroChangeStatus();
        }
        public void RefreshHeroChangeStatus()
        {
            if (HeroChangePending && _heroReplyReceived && Snapshot.Value.HeroRevision >= _awaitHeroRevision)
                HeroChangePending = false;
        }
    }
}
