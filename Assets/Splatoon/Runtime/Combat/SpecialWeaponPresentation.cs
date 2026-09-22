using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;
namespace Splatoon.Combat
{
    // Presentation consumes authority events/snapshots. No damage, paint or charge is produced here.
    public sealed class SpecialWeaponPresentation:MonoBehaviour
    {
        public PrototypeMatch Match;
        sealed class View
        {
            public SpecialEntityState State;public GameObject Root,Body;public LineRenderer[] Lines;public Transform[] Lobes;
            public SpecialWeaponTarget Target;public bool Predicted;public double Until;public SpecialWeaponRuntimeConfig Config;
            public SpecialWeaponService.Entity Flight;
        }
        readonly Dictionary<uint,View> _views=new();
        readonly Dictionary<uint,uint> _removed=new();
        readonly Dictionary<(ulong,uint,uint,uint),View> _predicted=new();
        readonly Dictionary<ulong,(GameObject root,int type,uint action)> _held=new();
        readonly List<GameObject> _effects=new();
        readonly HashSet<(ulong,uint,uint)> _sounds=new();
        LineRenderer _preview;GameObject _landing;readonly List<Vector3> _points=new();
        uint _snapshotWatermark;
        static (ulong,uint,uint,uint) Key(SpecialEntityState s)=>(s.Owner,s.Life,s.HeroRevision,s.Attack);
        static double Clock=>PrototypeMatch.Current!=null?PrototypeMatch.Current.NetworkManager.ServerTime.Time:Time.timeAsDouble;
        public int Count=>_views.Count;
        public readonly HashSet<SpecialWeaponType> ObservedTypes=new();
        void Sound(SpecialWeaponRuntimeConfig c,Vector3 position,bool use)
        {var clip=use?c.UseAudio:c.EffectAudio;if(clip!=null)AudioSource.PlayClipAtPoint(clip,position,.25f);}
        public void Lifecycle(SpecialLifecycleEvent e)
        {
            var s=e.State;ObservedTypes.Add(s.Type);if(e.Sequence<=_snapshotWatermark&&!_views.ContainsKey(s.Id))return;
            if(_removed.TryGetValue(s.Id,out uint revision)&&revision>=s.Version)return;
            if(s.Phase==SpecialEntityPhase.Removed)
            {
                if(_views.TryGetValue(s.Id,out var old)&&old.State.Version>s.Version)return;
                _removed[s.Id]=s.Version;
                if(_views.Remove(s.Id,out var v)){if(s.Type==SpecialWeaponType.Trizooka||s.Health<=0)Impact(v.Config,s);Destroy(v.Root);}
                return;
            }
            Upsert(s);
        }
        public void Apply(IReadOnlyList<SpecialEntityState> states,uint watermark)
        {
            if(watermark<_snapshotWatermark)return;_snapshotWatermark=watermark;var live=new HashSet<uint>();
            foreach(var s in states){live.Add(s.Id);if(!_removed.TryGetValue(s.Id,out uint dead)||dead<s.Version)Upsert(s);}
            var ids=new List<uint>(_views.Keys);foreach(uint id in ids)if(!live.Contains(id)&&_views[id].State.Version<=watermark){Destroy(_views[id].Root);_views.Remove(id);_removed[id]=watermark;}
            // Ordered reliable streams + watermark make older tombstones redundant.
            var gone=new List<uint>(_removed.Keys);foreach(uint id in gone)if(_removed[id]<watermark)_removed.Remove(id);
        }
        void Upsert(SpecialEntityState s)
        {
            ObservedTypes.Add(s.Type);
            if(_views.TryGetValue(s.Id,out var v)){if(s.Version<v.State.Version||s.SampledAt<v.State.SampledAt)return;v.State=s;return;}
            if(_predicted.Remove(Key(s),out v)){v.Predicted=false;v.State=s;}
            else v=Create(s,SpecialWeaponConfigService.Current.Get((int)s.Type));
            _views[s.Id]=v;
            if(s.Type==SpecialWeaponType.Reefslider)Impact(v.Config,s);
        }
        View Create(SpecialEntityState s,SpecialWeaponRuntimeConfig c)
        {
            var root=new GameObject("Special_"+s.Type+"_"+s.Id);root.transform.SetParent(transform,false);
            var v=new View{State=s,Root=root,Config=c,Lines=new LineRenderer[3],Lobes=new Transform[3]};
            if(c.EntityPrefab!=null){v.Body=Instantiate(c.EntityPrefab,root.transform);foreach(var col in v.Body.GetComponentsInChildren<Collider>())col.enabled=false;Tint(v.Body,s.Team);}
            for(int i=0;i<3;i++)v.Lines[i]=Line(root.transform,c,s.Team,.08f);
            if(s.Type==SpecialWeaponType.Trizooka)for(int i=0;i<3;i++)
            {var b=GameObject.CreatePrimitive(PrimitiveType.Sphere);Destroy(b.GetComponent<Collider>());b.transform.SetParent(root.transform);b.transform.localScale=Vector3.one*.35f;b.GetComponent<Renderer>().sharedMaterial=c.Material;Tint(b,s.Team);v.Lobes[i]=b.transform;}
            root.transform.position=s.Position;return v;
        }
        static void Tint(GameObject go,byte team)
        {var block=new MaterialPropertyBlock();block.SetColor("_BaseColor",PrototypeArena.TeamColor(team));block.SetColor("_Color",PrototypeArena.TeamColor(team));foreach(var r in go.GetComponentsInChildren<Renderer>())r.SetPropertyBlock(block);}
        static LineRenderer Line(Transform parent,SpecialWeaponRuntimeConfig c,byte team,float width)
        {var go=new GameObject("SpecialLine");go.transform.SetParent(parent,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=c.Material;l.useWorldSpace=true;l.widthMultiplier=width;l.startColor=l.endColor=PrototypeArena.TeamColor(team);l.positionCount=0;return l;}
        static void Ring(LineRenderer l,Vector3 center,float radius)
        {l.positionCount=65;for(int i=0;i<65;i++){float a=i*Mathf.PI/32;l.SetPosition(i,center+new Vector3(Mathf.Cos(a),0,Mathf.Sin(a))*radius);}}
        void Impact(SpecialWeaponRuntimeConfig c,SpecialEntityState s)
        {
            var go=new GameObject("SpecialImpact");go.transform.position=s.Position;go.transform.SetParent(transform);var l=Line(go.transform,c,s.Team,.18f);
            Ring(l,s.Position,Mathf.Max(.5f,c.P.paintRadius));Destroy(go,.25f);Sound(c,s.Position,false);
        }
        public void Predict(PrototypePlayer player,PlayerSnapshot s)
        {
            var c=SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId);SpecialWeaponService.Launch(player,s,c,out var pos,out var velocity);
            var state=new SpecialEntityState{Owner=player.PlayerId,Life=s.Revision,HeroRevision=s.HeroRevision,Action=s.SpecialAction,Attack=s.SpecialAttack,Round=Match.State.Value.Round,Team=s.Team,Type=c.Type,Position=pos,Velocity=velocity,Born=s.SpecialAttackAt,SampledAt=s.SpecialAttackAt,Phase=SpecialEntityPhase.Flying,LiveLobes=7,P0=pos,P1=pos,P2=pos};
            var key=Key(state);if(_predicted.ContainsKey(key))return;foreach(var v in _views.Values)if(Key(v.State)==key)return;
            var view=Create(state,c);view.Predicted=true;view.Until=Clock+2;
            view.Flight=new SpecialWeaponService.Entity(state,c);
            _predicted[key]=view;
        }
        void Update()
        {
            if(Match==null)return;double now=Clock;
            foreach(var v in _views.Values)Present(v,now-(Match.IsServer?0:.1));
            var stale=new List<(ulong,uint,uint,uint)>();
            foreach(var kv in _predicted)
            {
                var v=kv.Value;var s=v.State;
                if(now>v.Until||!PrototypePlayer.ByOwner.TryGetValue(s.Owner,out var p)||p.PresentedState.HeroRevision!=s.HeroRevision){stale.Add(kv.Key);continue;}
                if(s.Type==SpecialWeaponType.Trizooka)
                {
                    var flight=v.Flight;
                    for(int i=0;i<3;i++)
                    {
                        if((flight.State.LiveLobes&(1<<i))==0||!SpecialWeaponService.SampleTrizookaLobe(flight,i,now,out var to))continue;
                        Vector3 from=i==0?flight.State.P0:i==1?flight.State.P1:flight.State.P2,delta=to-from;
                        float age=(float)(now-s.Born-i*(double)v.Config.P.lobeDelay);
                        float radius=Mathf.Lerp(v.Config.P.fieldRadiusStart,v.Config.P.fieldRadiusEnd,age/v.Config.P.fieldRadiusTime);
                        if(Physics.SphereCast(from,radius,delta.normalized,out var hit,delta.magnitude,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))
                        {to=hit.point;flight.State.LiveLobes=(byte)(flight.State.LiveLobes&~(1<<i));}
                        if(i==0)flight.State.P0=to;else if(i==1)flight.State.P1=to;else flight.State.P2=to;
                    }
                    flight.State.SampledAt=now;v.State=flight.State;
                }
                else if(s.Type!=SpecialWeaponType.Reefslider)
                {
                    SpecialWeaponService.SampleThrow(v.Flight,now);v.State=v.Flight.State;
                }
                Present(v,now);
            }
            foreach(var k in stale){Destroy(_predicted[k].Root);_predicted.Remove(k);}
            PresentHeld(now);Preview();
        }
        void Present(View v,double now)
        {
            var s=v.State;var p=v.Config.P;
            float extrap=Mathf.Clamp((float)(now-s.SampledAt),0,.15f);Vector3 position=s.Position+s.Velocity*extrap;
            if(s.Type==SpecialWeaponType.InkStorm&&s.Phase==SpecialEntityPhase.Active)position=s.Position+s.Direction*p.cloudSpeed*extrap;
            v.Root.transform.position=position;
            if(!Match.IsServer&&s.Type==SpecialWeaponType.WaveBreaker)
            {
                bool target=s.Phase==SpecialEntityPhase.Active&&s.Health>0;
                if(target&&v.Target==null){var go=new GameObject("RemoteSonarTarget");go.transform.SetParent(v.Root.transform);v.Target=go.AddComponent<SpecialWeaponTarget>();v.Target.InitializePresentation(s);}
                if(v.Target!=null){v.Target.gameObject.SetActive(target);v.Target.transform.position=position;}
            }
            if(v.Body!=null)v.Body.SetActive(s.Type!=SpecialWeaponType.Trizooka&&(s.Type!=SpecialWeaponType.WaveBreaker||s.Health>0||s.Phase==SpecialEntityPhase.Flying));
            for(int i=0;i<3;i++)v.Lines[i].positionCount=0;
            switch(s.Type)
            {
                case SpecialWeaponType.Trizooka:
                    for(int i=0;i<3;i++){bool visible=(s.LiveLobes&(1<<i))!=0&&now+1e-6>=s.Born+i*(double)p.lobeDelay;v.Lobes[i].gameObject.SetActive(visible);if(!visible)continue;var point=(i==0?s.P0:i==1?s.P1:s.P2)+s.Velocity*extrap;v.Lobes[i].position=point;var l=v.Lines[i];l.positionCount=2;l.SetPosition(0,point);l.SetPosition(1,point-s.Velocity*.035f);}break;
                case SpecialWeaponType.TripleInkstrike:
                    if(s.Phase==SpecialEntityPhase.Warning)Ring(v.Lines[0],position+.04f*Vector3.up,p.innerRadius);
                    else if(s.Phase==SpecialEntityPhase.Active){float radius=p.innerRadius*Mathf.Clamp01((float)(now-s.Changed)/p.expandSeconds);var l=v.Lines[0];l.positionCount=100;for(int i=0;i<100;i++){float a=i*.45f+(float)now*8;float y=i/99f*p.damageHeight;l.SetPosition(i,position+new Vector3(Mathf.Cos(a)*radius,y,Mathf.Sin(a)*radius));}}break;
                case SpecialWeaponType.WaveBreaker:
                    if(s.Phase!=SpecialEntityPhase.Active)break;
                    for(int i=0;i<s.Pulse;i++){double age=now-s.Changed-p.waveFirst-i*p.waveInterval;if(age>=0&&age<=p.waveLifetime)Ring(v.Lines[i],position+Vector3.up*.05f,p.waveRadius*(float)(age/p.waveLifetime));}
                    if(v.Body!=null)v.Body.transform.localPosition=Vector3.up*(.1f+Mathf.Abs(Mathf.Sin((float)(now-s.Changed)*Mathf.PI/(p.waveInterval)))*.2f);break;
                case SpecialWeaponType.InkStorm:
                    if(s.Phase==SpecialEntityPhase.Active){if(v.Body!=null)v.Body.transform.localScale=new Vector3(p.innerRadius*2,1,p.innerRadius*2);Ring(v.Lines[0],position+Vector3.down*p.cloudHeight,p.innerRadius);
                        var l=v.Lines[1];l.positionCount=24;for(int i=0;i<12;i++){float a=i*2.39996f;Vector3 q=position+new Vector3(Mathf.Cos(a),0,Mathf.Sin(a))*p.innerRadius*.75f;l.SetPosition(i*2,q);l.SetPosition(i*2+1,q+Vector3.down*(p.cloudHeight*.8f));}}break;
                case SpecialWeaponType.Reefslider:
                    if(now-s.Born<.3)Ring(v.Lines[0],position,p.outerRadius);else v.Lines[0].positionCount=0;
                    if(v.Body!=null)v.Body.SetActive(now-s.Born<.3);break;
            }
        }
        void PresentHeld(double now)
        {
            var visible=new HashSet<ulong>();
            foreach(var player in PrototypePlayer.ByOwner.Values)
            {
                if(player==null)continue;var s=player.PresentedState;if(!s.IsAlive||!SpecialWeaponSimulation.Active(s))continue;
                int type=s.SpecialWeaponId;var c=SpecialWeaponConfigService.Current.Get(type);visible.Add(player.PlayerId);
                if(!_held.TryGetValue(player.PlayerId,out var old)||old.type!=type||old.action!=s.SpecialAction)
                {
                    if(old.root!=null)Destroy(old.root);var go=c.HeldPrefab!=null?Instantiate(c.HeldPrefab,transform):new GameObject("SpecialHeld");Tint(go,s.Team);_held[player.PlayerId]=(go,type,s.SpecialAction);old=_held[player.PlayerId];
                    if(_sounds.Add((player.PlayerId,s.HeroRevision,s.SpecialAction)))Sound(c,player.transform.position,true);
                }
                old.root.transform.SetPositionAndRotation(player.transform.position+Quaternion.Euler(0,s.Yaw,0)*(c.Type==SpecialWeaponType.Reefslider?new Vector3(0,.25f,.3f):new Vector3(.3f,1.1f,.5f)),Quaternion.Euler(s.Pitch,s.Yaw,0));
                old.root.SetActive(!s.Swimming);
            }
            var keys=new List<ulong>(_held.Keys);foreach(var id in keys)if(!visible.Contains(id)){Destroy(_held[id].root);_held.Remove(id);}
        }
        void Preview()
        {
            var player=PrototypePlayer.Local;if(player==null){if(_preview!=null)_preview.positionCount=0;return;}
            var s=player.PresentedState;var c=SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId);
            bool show=s.IsAlive&&s.SpecialAiming&&SpecialWeaponSimulation.Active(s)&&c.Type!=SpecialWeaponType.Trizooka;
            if(!show){if(_preview!=null)_preview.positionCount=0;return;}
            if(_preview==null)_preview=Line(transform,c,s.Team,.045f);
            _preview.sharedMaterial=c.Material;SpecialWeaponService.Launch(player,s,c,out var point,out var velocity);_points.Clear();_points.Add(point);
            for(int i=0;i<180;i++)
            {
                bool landed=SpecialWeaponService.AdvanceThrow(c,ref point,ref velocity,out _);
                if(landed){_points.Add(point);break;}if(i%3==0)_points.Add(point);
            }
            _preview.positionCount=_points.Count;_preview.SetPositions(_points.ToArray());
        }
        public void Clear()
        {foreach(var v in _views.Values)Destroy(v.Root);foreach(var v in _predicted.Values)Destroy(v.Root);foreach(var v in _held.Values)Destroy(v.root);_views.Clear();_predicted.Clear();_held.Clear();_removed.Clear();_sounds.Clear();_snapshotWatermark=0;if(_preview!=null)_preview.positionCount=0;}
        void OnDestroy()=>Clear();
    }
}
