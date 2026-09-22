using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Networking;
using Unity.Netcode;
using UnityEngine;
namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        public readonly SpecialWeaponService SpecialWeapons=new();
        public SpecialWeaponPresentation SpecialPresentation {get;private set;}
        uint _specialSequence,_specialIncoming,_specialWatermark;int _specialPartsCount,_specialTick;ulong _specialParts;
        readonly List<SpecialEntityState> _specialStates=new(),_specialSend=new();
        void EnsureSpecialPresentation(){if(SpecialPresentation==null){SpecialPresentation=gameObject.AddComponent<SpecialWeaponPresentation>();SpecialPresentation.Match=this;}}
        void TickSpecialWeapons()
        {
            EnsureSpecialPresentation();
            for(int offset=0;offset<SpecialWeapons.Lifecycle.Count;offset+=12)
                SpecialEventsClientRpc(new NetworkBatch<SpecialLifecycleEvent>(SpecialWeapons.Lifecycle.GetRange(offset,Mathf.Min(12,SpecialWeapons.Lifecycle.Count-offset))));
            SpecialWeapons.Lifecycle.Clear();
            if(++_specialTick%3!=0)return;
            var states=SpecialWeapons.Capture();uint sequence=++_specialSequence;int parts=Mathf.Max(1,Mathf.CeilToInt(states.Count/16f));
            for(int part=0;part<parts;part++){_specialSend.Clear();for(int i=part*16;i<Mathf.Min(states.Count,part*16+16);i++)_specialSend.Add(states[i]);SpecialStatesClientRpc(new NetworkBatch<SpecialEntityState>(_specialSend),State.Value.Round,sequence,part,parts,SpecialWeapons.Watermark);}
        }
        [ClientRpc] void SpecialEventsClientRpc(NetworkBatch<SpecialLifecycleEvent> events)
        {try{EnsureSpecialPresentation();for(int i=0;i<events.Count;i++)if(events[i].State.Round==State.Value.Round&&State.Value.Phase!=MatchPhase.Finished)SpecialPresentation.Lifecycle(events[i]);}finally{events.Dispose();}}
        [ClientRpc] void SpecialStatesClientRpc(NetworkBatch<SpecialEntityState> states,uint round,uint sequence,int part,int parts,uint watermark)
        {
            try
            {
                EnsureSpecialPresentation();if(round!=State.Value.Round||State.Value.Phase==MatchPhase.Finished||sequence<_specialIncoming||parts<1||parts>64||part<0||part>=parts)return;
                if(sequence>_specialIncoming){_specialIncoming=sequence;_specialStates.Clear();_specialParts=0;_specialPartsCount=parts;_specialWatermark=watermark;}
                ulong bit=1UL<<part;if(parts!=_specialPartsCount||watermark!=_specialWatermark||(_specialParts&bit)!=0)return;
                _specialParts|=bit;for(int i=0;i<states.Count;i++)_specialStates.Add(states[i]);ulong complete=parts==64?ulong.MaxValue:(1UL<<parts)-1;
                if(_specialParts==complete)SpecialPresentation.Apply(_specialStates,watermark);
            }
            finally{states.Dispose();}
        }
        void ClearSpecialWeapons(){SpecialWeapons.Clear();SpecialPresentation?.Clear();_specialParts=0;_specialStates.Clear();}
    }
}
