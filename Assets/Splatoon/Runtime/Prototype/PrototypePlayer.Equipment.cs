using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        struct HeroRequest { public uint Id, Round, Life; public int Hero, Sub, Special; public uint Equipment; public HeroSelectionOrigin Origin; }
        HeroRequest? _heroRequest;
        uint _heroRequestId, _lastHeroRequest, _awaitHeroRevision;
        bool _heroReplyReceived, _initialLoadoutApplied;
        void RestoreInitialLoadout()
        {
            var match=PrototypeMatch.Current;var s=Snapshot.Value;
            if (_initialLoadoutApplied || match == null || !match.InitialSyncComplete || !s.IsAlive || HeroChangePending || TeamChangePending) return;
            var saved=PlayerLoadout.Remembered(s.HeroId);
            if(saved.Matches(PlayerLoadout.From(s))){_initialLoadoutApplied=true;return;}
            var origin=match.State.Value.Phase==MatchPhase.Playing?HeroSelectionOrigin.SpawnArea:HeroSelectionOrigin.Warmup;
            bool inSpawn=PrototypeArena.Current!=null&&PrototypeArena.Current.IsInHeroChangeZone(s.Team,s.Position);
            if(HeroSelectionRules.Availability(s,origin,match.State.Value.Phase,HeroSelectionRules.Development,inSpawn)!=null)return;
            // Completion is observed from the applied snapshot. A stale/rejected request
            // or a busy/settlement frame must not permanently consume restoration.
            RequestLoadoutChange(saved,origin);
        }
        public bool HeroChangePending { get; private set; }
        public string HeroChangeMessage { get; private set; } = "";
        public void RequestHeroChange(int heroId, HeroSelectionOrigin origin) => RequestLoadoutChange(PlayerLoadout.Remembered(heroId),origin);
        public void RequestLoadoutChange(PlayerLoadout loadout, HeroSelectionOrigin origin)
        {
            if (!ControlsLocalPlayer || !IsSpawned || HeroChangePending || TeamChangePending || PrototypeMatch.Current == null) return;
            HeroChangePending = true; _heroReplyReceived = false; HeroChangeMessage = "切换中…";
            ChangeHeroRpc(loadout.HeroId,loadout.SubWeaponId,loadout.SpecialWeaponId,Snapshot.Value.HeroRevision, origin, ++_heroRequestId, PrototypeMatch.Current.State.Value.Round, Snapshot.Value.Revision);
        }
        [Rpc(SendTo.Server)]
        void ChangeHeroRpc(int heroId,int sub,int special,uint equipment, HeroSelectionOrigin origin, uint request, uint round, uint life, RpcParams rpc = default)
        {
            if (IsTestBot || rpc.Receive.SenderClientId != OwnerClientId || request <= _lastHeroRequest) return;
            _lastHeroRequest = request;
            if (_heroRequest.HasValue || _teamRequest.HasValue)
            { HeroChangeReplyRpc(request, Snapshot.Value.HeroRevision, "正在处理切换请求"); return; }
            _heroRequest = new HeroRequest { Id = request, Hero = heroId, Sub=sub, Special=special, Equipment=equipment, Origin = origin, Round = round, Life = life };
        }
        void ApplyHeroRequest(ref PlayerSnapshot s, MatchPhase phase)
        {
            if (!_heroRequest.HasValue) return;
            var request = _heroRequest.Value; _heroRequest = null;
            string error = HeroSelectionRules.Validate(s, request.Hero, request.Origin, request.Round,
                PrototypeMatch.Current.State.Value.Round, request.Life, phase, HeroSelectionRules.Development,
                PrototypeArena.Current != null && PrototypeArena.Current.IsInHeroChangeZone(s.Team, s.Position));
            if(error==null)error=PlayerLoadout.Validate(new PlayerLoadout(request.Hero,request.Sub,request.Special));
            if(error==null&&request.Equipment!=s.HeroRevision)error="配装已变化，请重试";
            if (error == null && s.HeroId != request.Hero)
            {
                var current = HeroBodyShape.For(s.HeroId); var target = HeroBodyShape.For(request.Hero);
                bool grows = target.Height > current.Height || target.Radius > current.Radius || target.CompactHeight > current.CompactHeight;
                if (grows && !_motor.CanFitHero(request.Hero, PlayerMotorSimulation.HumanPosition(s)))
                    error = "空间不足，无法切换到该英雄";
            }
            var next=new PlayerLoadout(request.Hero,request.Sub,request.Special);
            if(error==null&&!PlayerLoadout.From(s).Matches(next))
            {
                bool heroChanged=HeroSelectionRules.Apply(ref s,request.Hero,phase==MatchPhase.Practice,_lastInput);
                if(!heroChanged){WeaponSimulation.Cancel(ref s,_lastInput,true);SubWeaponSimulation.Cancel(ref s,_lastInput);s.HeroRevision++;}
                s.SubWeaponId=request.Sub;s.SpecialWeaponId=request.Special;s.SpecialPoints=0;SpecialWeaponSimulation.Interrupt(ref s);s.SpecialChargeLockedUntil=0;
                s.SpecialConsumed=_lastInput.SpecialSequence;
                PrototypeMatch.Current.SubWeapons.ClearOwner(PlayerId);PrototypeMatch.Current.SpecialWeapons.ClearOwner(PlayerId);
                EnsureHeroPresentation(s.HeroId);_motor.Restore(s);
            }
            HeroChangeReplyRpc(request.Id, s.HeroRevision, error ?? "配装已确认");
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
                { HeroChangePending = false; if(HeroChangeMessage=="配装已确认")PlayerLoadout.From(Snapshot.Value).Save(); }
        }
    }
}
