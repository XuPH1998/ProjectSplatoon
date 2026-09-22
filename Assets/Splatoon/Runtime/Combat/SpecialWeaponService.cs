using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEngine;
namespace Splatoon.Combat
{
    public enum SpecialEntityPhase:byte { Flying, Warning, Active, Removed }
    public struct SpecialEntityState:INetworkSerializable
    {
        public uint Id,Round,Life,HeroRevision,Action,Attack,Version;
        public ulong Owner;
        public byte Team, LiveLobes;
        public SpecialWeaponType Type;
        public SpecialEntityPhase Phase;
        public Vector3 Position,Velocity,Direction,Normal,P0,P1,P2;
        public double Born,Changed,Expires,SampledAt;
        public float Health,Radius;
        public int Pulse;
        public void NetworkSerialize<T>(BufferSerializer<T> s)where T:IReaderWriter
        {
            s.SerializeValue(ref Id);s.SerializeValue(ref Round);s.SerializeValue(ref Life);s.SerializeValue(ref HeroRevision);s.SerializeValue(ref Action);s.SerializeValue(ref Attack);s.SerializeValue(ref Version);s.SerializeValue(ref Owner);s.SerializeValue(ref Team);s.SerializeValue(ref LiveLobes);s.SerializeValue(ref Type);s.SerializeValue(ref Phase);
            s.SerializeValue(ref Position);s.SerializeValue(ref Velocity);s.SerializeValue(ref Direction);s.SerializeValue(ref Normal);s.SerializeValue(ref P0);s.SerializeValue(ref P1);s.SerializeValue(ref P2);
            s.SerializeValue(ref Born);s.SerializeValue(ref Changed);s.SerializeValue(ref Expires);s.SerializeValue(ref SampledAt);s.SerializeValue(ref Health);s.SerializeValue(ref Radius);s.SerializeValue(ref Pulse);
        }
    }
    public struct SpecialLifecycleEvent:INetworkSerializable
    {
        public uint Sequence;public SpecialEntityState State;
        public void NetworkSerialize<T>(BufferSerializer<T> s)where T:IReaderWriter{s.SerializeValue(ref Sequence);s.SerializeValue(ref State);}
    }
    public sealed class SpecialWeaponService
    {
        public sealed class Entity
        {
            public SpecialEntityState State;
            public readonly SpecialWeaponRuntimeConfig Config;
            public readonly HashSet<(ulong,uint)> Hit=new();
            public readonly Dictionary<(ulong,uint),float> PlayerDamage=new();
            public readonly HashSet<(ulong,uint,int)> WaveHits=new();
            public readonly Dictionary<uint,float> ObjectDamage=new();
            public readonly Dictionary<uint,float> SpecialObjectDamage=new();
            public readonly HashSet<(uint,int,bool)> WaveObjectHits=new();
            public readonly Dictionary<(ulong,uint),double> ContactAt=new();
            public SpecialWeaponTarget Target;
            public Vector3[] RainPositions,RainVelocities;
            public Vector3[] LobeCenters,LobeVelocities;
            public int[] LobeFrames;
            public int ThrowFrames;
            public bool ThrowLanded;
            public Vector3[] ReefSplashPositions,ReefSplashVelocities;
            public bool[] ReefSplashDone;
            public int ReefSplashFrames;
            public double[] RainReadyAt;
            public double NextPaint, NextContact, NextDamage;
            public uint PaintOrdinal;
            public bool Removed;
            public Entity(SpecialEntityState s,SpecialWeaponRuntimeConfig c){State=s;Config=c;}
        }
        readonly List<Entity> _entities=new();
        readonly HashSet<(ulong,uint)> _rainDamaged=new();
        readonly List<SpecialEntityState> _capture=new();
        readonly TpsAimSolver _aim=new();
        readonly Dictionary<(ulong,uint),HashSet<(ulong,uint)>> _reefHits=new();
        uint _id,_version;
        double _now;
        public uint Watermark=>_version;
        public readonly List<SpecialLifecycleEvent> Lifecycle=new();
        public IReadOnlyList<Entity> Entities=>_entities;
        void Publish(Entity e){e.State.SampledAt=_now;e.State.Version=++_version;Lifecycle.Add(new SpecialLifecycleEvent{Sequence=_version,State=e.State});}
        void Phase(Entity e,SpecialEntityPhase phase){e.State.Phase=phase;e.State.Changed=_now;Publish(e);}
        public static void Launch(PrototypePlayer player,PlayerSnapshot s,SpecialWeaponRuntimeConfig c,out Vector3 position,out Vector3 velocity)
        {
            var aim=new TpsAimSolver().Resolve(player,s,0);
            position=PlayerMotorSimulation.HumanPosition(s)+Vector3.up*GameplayConfig.GetHero(s.HeroId).StandingHeight*.75f;
            Vector3 direction=(aim.AimPoint-position).normalized;
            velocity=c.Type==SpecialWeaponType.Trizooka?direction*c.P.projectileSpeed:direction*c.P.throwSpeed+Vector3.up*c.P.throwLift;
            if(c.Type!=SpecialWeaponType.Trizooka&&c.Type!=SpecialWeaponType.WaveBreaker){velocity+=new Vector3(s.Velocity.x,0,s.Velocity.z)*1.6f;velocity.y+=Mathf.Min(.16f*60*SpecialWeaponDefaults.Scale,Mathf.Max(0,s.Velocity.y)*4);}
        }
        public Entity Spawn(PrototypePlayer player,PlayerSnapshot s,double now,uint round)
        {
            _now=now;var c=SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId);
            Launch(player,s,c,out var position,out var velocity);
            var state=new SpecialEntityState{Id=++_id,Round=round,Life=s.Revision,HeroRevision=s.HeroRevision,Action=s.SpecialAction,Attack=s.SpecialAttack,Owner=player.PlayerId,Team=s.Team,Type=c.Type,
                Phase=SpecialEntityPhase.Flying,Position=position,Velocity=velocity,Direction=Quaternion.Euler(0,s.Yaw,0)*Vector3.forward,Normal=Vector3.up,Born=now,Changed=now,Expires=now+10,LiveLobes=7,Health=c.P.health,P0=position,P1=position,P2=position};
            var e=new Entity(state,c);_entities.Add(e);Publish(e);
            if(c.Type==SpecialWeaponType.Reefslider)
            {e.State.Position=PlayerMotorSimulation.HumanPosition(s)+Vector3.up*.6f*SpecialWeaponDefaults.Scale;e.State.Radius=c.P.outerRadius;Blast(e,e.State.Position,null,false);StartReefSplashes(e,PlayerMotorSimulation.HumanPosition(s));e.State.Expires=now+3;Phase(e,SpecialEntityPhase.Active);}
            return e;
        }
        public IReadOnlyList<SpecialEntityState> Capture(){_capture.Clear();foreach(var e in _entities)if(!e.Removed){e.State.SampledAt=_now;_capture.Add(e.State);}return _capture;}
        public void Step(double now,float dt,IReadOnlyList<PrototypePlayer> players)
        {
            _now=now;_rainDamaged.Clear();
            foreach(var e in _entities)
            {
                if(e.Removed)continue;
                if(!PrototypePlayer.ByOwner.TryGetValue(e.State.Owner,out var owner)||owner.Snapshot.Value.HeroRevision!=e.State.HeroRevision||owner.Snapshot.Value.Team!=e.State.Team){Remove(e);continue;}
                if(now+1e-6>=e.State.Expires){Remove(e);continue;}
                if(e.State.Phase==SpecialEntityPhase.Flying){if(e.State.Type==SpecialWeaponType.Trizooka)StepTrizooka(e,dt);else StepThrow(e,dt);}
                else if(e.State.Type==SpecialWeaponType.TripleInkstrike)StepTornado(e,players,dt);
                else if(e.State.Type==SpecialWeaponType.WaveBreaker)StepSonar(e,players);
                else if(e.State.Type==SpecialWeaponType.InkStorm)StepStorm(e,players,dt);
                else if(e.State.Type==SpecialWeaponType.Reefslider)StepReefSplashes(e,now);
                e.State.SampledAt=now;
            }
            _entities.RemoveAll(e=>e.Removed);
        }
        void StartReefSplashes(Entity e,Vector3 feet)
        {
            var p=e.Config.P;int count=p.reefSplashCount;
            e.ReefSplashPositions=new Vector3[count];e.ReefSplashVelocities=new Vector3[count];e.ReefSplashDone=new bool[count];
            uint random=e.State.Id*2654435761u+e.State.Action;
            float Next(){random^=random<<13;random^=random>>17;random^=random<<5;return (random&0xffffff)/16777216f;}
            float phase=Next()*Mathf.PI*2;
            for(int i=0;i<count;i++)
            {
                float yaw=phase+i*Mathf.PI*2/count,pitch=Next()*p.reefSplashPitchMax*Mathf.Deg2Rad,speed=Mathf.Lerp(p.reefSplashSpeedMin,p.reefSplashSpeedMax,Next());
                e.ReefSplashPositions[i]=feet+Vector3.up*p.reefSplashOffsetY;
                e.ReefSplashVelocities[i]=new Vector3(Mathf.Cos(yaw)*Mathf.Cos(pitch),Mathf.Sin(pitch),Mathf.Sin(yaw)*Mathf.Cos(pitch))*speed;
            }
        }
        void StepReefSplashes(Entity e,double now)
        {
            if(e.ReefSplashDone==null)return;var p=e.Config.P;
            int target=Math.Min(180,Math.Max(0,(int)Math.Floor((now-e.State.Born)*60+1e-6)));
            while(e.ReefSplashFrames<target)
            {
                e.ReefSplashFrames++;
                for(int i=0;i<e.ReefSplashDone.Length;i++)
                {
                    if(e.ReefSplashDone[i])continue;
                    Vector3 from=e.ReefSplashPositions[i],velocity=e.ReefSplashVelocities[i];
                    Vector3 delta=velocity/60+Vector3.down*p.reefSplashGravity/(2*60*60);
                    e.ReefSplashVelocities[i]=velocity+Vector3.down*p.reefSplashGravity/60;
                    if(Physics.Raycast(from,delta.normalized,out var hit,delta.magnitude,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))
                    {e.ReefSplashPositions[i]=hit.point;e.ReefSplashDone[i]=true;PaintSurface(e,hit,p.reefSplashPaintRadius);}
                    else e.ReefSplashPositions[i]=from+delta;
                }
            }
            bool done=true;foreach(bool landed in e.ReefSplashDone)done&=landed;
            if(done&&now-e.State.Born>=.3)Remove(e);
        }
        static Vector3 Lobe(Vector3 velocity,Vector3 direction,SpecialParameters p,double age,int i,Vector3 center)
        {
            Vector3 axis=velocity.sqrMagnitude>.0001f?velocity.normalized:direction;
            Vector3 right=Vector3.Cross(axis,Mathf.Abs(axis.y)>.95f?Vector3.forward:Vector3.up).normalized;
            Vector3 up=Vector3.Cross(right,axis);float angle=(float)age*14+i*Mathf.PI*2/3;
            float r=p.orbitalRadius*Mathf.Clamp01((float)age/Mathf.Max(.0001f,p.orbitalTime));
            return center+(right*Mathf.Cos(angle)+up*Mathf.Sin(angle))*r;
        }
        void StepTrizooka(Entity e,float dt)
        {
            var p=e.Config.P;
            for(int i=0;i<3;i++)
            {
                if((e.State.LiveLobes&(1<<i))==0)continue;
                if(!SampleTrizookaLobe(e,i,_now,out var to))continue;
                double age=_now-e.State.Born-i*(double)p.lobeDelay;
                var velocity=e.LobeVelocities[i];
                Vector3 from=i==0?e.State.P0:i==1?e.State.P1:e.State.P2;
                Vector3 delta=to-from;float length=delta.magnitude;
                float fr=Mathf.Lerp(p.fieldRadiusStart,p.fieldRadiusEnd,(float)age/Mathf.Max(.0001f,p.fieldRadiusTime));
                float pr=Mathf.Lerp(p.playerRadiusStart,p.playerRadiusEnd,(float)age/Mathf.Max(.0001f,p.playerRadiusTime));
                bool wh=_aim.ClosestCast(from,delta,length,fr,e.State.Owner,out var wall,false,e.State.Team);
                bool embedded=_aim.Overlap(from,fr,e.State.Owner,-velocity.normalized,out var overlap,false,e.State.Team);
                if(embedded){wall=overlap;wh=true;}
                // A lobe pierces players and stops only at terrain/objects. Splash contacts do
                // not exclude a later direct hit: the volley deals the highest damage once.
                float playerDistance=wh?Mathf.Min(length,wall.Distance):length;
                while(!embedded&&(_aim.Overlap(from,pr,e.State.Owner,-velocity.normalized,out var player,true,e.State.Team,e.Hit)||
                    _aim.ClosestCast(from,delta,playerDistance,pr,e.State.Owner,out player,true,e.State.Team,e.Hit)))
                {
                    var victim=player.Collider.GetComponentInParent<PrototypePlayer>();
                    ApplyVolleyDamage(e,victim,p.directDamage,velocity);
                }
                if(wh)
                {
                    var h=wall;
                    to=h.Point;e.State.Normal=h.Normal;
                    var sub=h.Collider.GetComponent<SubWeaponTarget>();if(sub!=null)PrototypeMatch.Current.SubWeapons.DamageObject(sub.Id,e.State.Team,VolleyObjectIncrement(e.ObjectDamage,sub.Id,p.directDamage)*SpecialObjectDamage.ToSub(e.State.Type,sub.Type));
                    var target=h.Collider.GetComponent<SpecialWeaponTarget>();if(target!=null)target.Damage(e.State.Team,VolleyObjectIncrement(e.SpecialObjectDamage,target.Id,p.directDamage));
                    Blast(e,to,null,true);e.State.LiveLobes=(byte)(e.State.LiveLobes&~(1<<i));
                    if(i==0)e.State.P0=to;else if(i==1)e.State.P1=to;else e.State.P2=to;
                    Publish(e);
                }
                if(i==0)e.State.P0=to;else if(i==1)e.State.P1=to;else e.State.P2=to;
            }
            e.State.Position=e.LobeCenters[0];e.State.Velocity=e.LobeVelocities[0];
            if(e.State.LiveLobes==0)Remove(e);
        }
        public static bool SampleTrizookaLobe(Entity e,int i,double now,out Vector3 position)
        {
            var p=e.Config.P;double age=now-e.State.Born-i*(double)p.lobeDelay;position=e.State.Position;
            if(age< -1e-6)return false;
            if(e.LobeCenters==null)
            {
                e.LobeCenters=new[]{e.State.Position,e.State.Position,e.State.Position};
                e.LobeVelocities=new[]{e.State.Velocity,e.State.Velocity,e.State.Velocity};e.LobeFrames=new int[3];
            }
            int frame=Math.Max(0,(int)Math.Floor(age*60+1e-5));
            while(e.LobeFrames[i]<frame)AdvanceTrizooka(ref e.LobeCenters[i],ref e.LobeVelocities[i],e.LobeFrames[i]++,p);
            position=Lobe(e.LobeVelocities[i],e.State.Direction,p,age,i,e.LobeCenters[i]);return true;
        }
        // Completed reference frames, not render delta time. Each of the three lobes has its own clock.
        public static void AdvanceTrizooka(ref Vector3 position,ref Vector3 velocity,int completedFrames,SpecialParameters p)
        {
            int straight=Mathf.RoundToInt(p.straightSeconds*60);
            if(completedFrames>=straight)
            {
                if(completedFrames==straight)velocity=Vector3.ClampMagnitude(velocity,p.brakeMaxSpeed);
                bool brake=completedFrames<straight+Mathf.RoundToInt(p.brakeSeconds*60);
                velocity*=1-(brake?p.brakeDrag:p.freeDrag);
                velocity+=Vector3.down*(brake?p.brakeGravity:p.freeGravity)/60;
            }
            position+=velocity/60;
        }
        static readonly TpsAimSolver StormThrowAim=new();
        public static bool ThrowCollision(SpecialWeaponRuntimeConfig c,Vector3 from,Vector3 delta,out TpsCollision hit)
        {
            hit=default;
            if(c.Type==SpecialWeaponType.InkStorm)
            {
                // Generator contact is physical, not a damage query: include both
                // coloured teams' deployment bodies, but never player hit proxies.
                // Neutral team 0 selects both player teams without inventing an invalid owner ID.
                if(StormThrowAim.Overlap(from,c.P.throwRadius,0,delta.sqrMagnitude>0?-delta.normalized:Vector3.up,out hit,false,0,floatingMesh:true))
                {TpsAimSolver.UnembedFloatingContact(ref hit,c.P.throwRadius);return true;}
                return StormThrowAim.ClosestCast(from,delta,delta.magnitude,c.P.throwRadius,0,out hit,false,0);
            }
            if(!Physics.SphereCast(from,c.P.throwRadius,delta.normalized,out var h,delta.magnitude,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))return false;
            hit=new TpsCollision{Collider=h.collider,Point=h.point,Normal=h.normal,Center=from+delta.normalized*h.distance,Distance=h.distance};return true;
        }
        // Shared by authority, trajectory preview and fixed-frame visual prediction.
        // Returns a deployment contact, not a sonar's intermediate wall contact.
        public static bool AdvanceThrow(SpecialWeaponRuntimeConfig c,ref Vector3 position,ref Vector3 velocity,out Vector3 normal,float dt=1f/60)
        {
            var p=c.P;normal=Vector3.zero;velocity*=Mathf.Exp(-p.throwDrag*dt);
            Vector3 delta=velocity*dt+Vector3.down*p.throwGravity*dt*dt*.5f;velocity+=Vector3.down*p.throwGravity*dt;
            if(!ThrowCollision(c,position,delta,out var hit)){position+=delta;return false;}
            normal=hit.Normal;
            if(c.Type==SpecialWeaponType.WaveBreaker&&normal.y<.6f)
            {
                position=hit.Center+normal*(p.throwRadius+.01f);velocity=Vector3.ProjectOnPlane(velocity,normal);return false;
            }
            position=hit.Point+normal*.025f;velocity=Vector3.zero;return true;
        }
        public static void SampleThrow(Entity e,double now)
        {
            int frames=Math.Max(0,(int)Math.Floor(Math.Min(10,now-e.State.Born)*60+1e-6));
            while(!e.ThrowLanded&&e.ThrowFrames<frames)
            {
                e.ThrowLanded=AdvanceThrow(e.Config,ref e.State.Position,ref e.State.Velocity,out var normal);
                if(normal.sqrMagnitude>0)e.State.Normal=normal;e.ThrowFrames++;
            }
            e.State.P0=e.State.P1=e.State.P2=e.State.Position;
            // Predicted display deliberately does not start gameplay effects on contact.
            e.State.SampledAt=now;
        }
        void StepThrow(Entity e,float dt)
        {
            var p=e.Config.P;
            bool landed=AdvanceThrow(e.Config,ref e.State.Position,ref e.State.Velocity,out var normal,dt);
            if(normal.sqrMagnitude>0)e.State.Normal=normal;
            if(landed)
            {
                e.State.Expires=_now+p.effectDelay+p.effectDuration;
                if(e.State.Type==SpecialWeaponType.WaveBreaker)
                {
                    e.State.Expires=_now+p.effectDuration;Phase(e,SpecialEntityPhase.Active);
                    var go=new GameObject("WaveBreakerTarget_"+e.State.Id);e.Target=go.AddComponent<SpecialWeaponTarget>();e.Target.Initialize(this,e);
                }
                else Phase(e,SpecialEntityPhase.Warning);
            }
        }
        void StepTornado(Entity e,IReadOnlyList<PrototypePlayer> players,float dt)
        {
            var p=e.Config.P;
            if(e.State.Phase==SpecialEntityPhase.Warning){if(_now+1e-6<e.State.Changed+p.effectDelay)return;Phase(e,SpecialEntityPhase.Active);e.State.Expires=_now+p.effectDuration;}
            float spread=Mathf.Clamp01((float)(_now-e.State.Changed)/p.expandSeconds);e.State.Radius=p.innerRadius*spread;
            bool damageTick=_now+1e-6>=e.NextDamage;if(damageTick)e.NextDamage=_now+5d/60;
            foreach(var player in players)
            {
                var s=player.Snapshot.Value;if(!damageTick||!s.IsAlive||s.Team==e.State.Team)continue;Vector3 d=s.Position-e.State.Position;
                if(d.y>=-p.damageHeightDown&&d.y<=p.damageHeight&&new Vector2(d.x,d.z).magnitude<=e.State.Radius+GameplayConfig.GetHero(s.HeroId).BodyRadius)
                    DamageWithKnockback(e,player,p.damagePerSecond*5f/60,d,p.knockbackAcceleration);
            }
            if(damageTick)DamageObjects(e,e.State.Position,e.State.Radius,p.damagePerSecond*5f/60,false,p.damageHeight);
            if(_now+1e-6>=e.NextPaint){PaintDisk(e,e.State.Position,Mathf.Lerp(.5f*SpecialWeaponDefaults.Scale,p.paintRadius,spread));e.NextPaint=_now+p.paintInterval;}
        }
        void StepSonar(Entity e,IReadOnlyList<PrototypePlayer> players)
        {
            var p=e.Config.P;double age=_now-e.State.Changed;
            int pulses=e.State.Health<=0?e.State.Pulse:age+1e-6<p.waveFirst?0:Math.Min(3,1+(int)((age-p.waveFirst+1e-6)/p.waveInterval));
            if(e.State.Pulse!=pulses){e.State.Pulse=pulses;Publish(e);}
            if(pulses==3&&e.State.Health>0)DestroyGenerator(e,false);
            DamageWaveObjects(e,pulses,age);
            foreach(var player in players)
            {
                var s=player.Snapshot.Value;if(!s.IsAlive||s.Team==e.State.Team)continue;
                Vector3 feet=PlayerMotorSimulation.HumanPosition(s);Vector3 d=feet-e.State.Position;
                float distance=new Vector2(d.x,d.z).magnitude;
                for(int wave=0;wave<pulses;wave++)
                {
                    double a=age-p.waveFirst-wave*p.waveInterval;if(a<0||a>p.waveLifetime)continue;
                    float radius=p.waveRadius*(float)(a/p.waveLifetime),width=p.waveRadius/p.waveLifetime/60+GameplayConfig.GetHero(s.HeroId).BodyRadius;
                    if(Mathf.Abs(distance-radius)>width||d.y>p.waveHeightUp||d.y< -p.waveHeightDown)continue;
                    // Follow the local floor; an airborne body whose feet clear the wave is safe.
                    if(s.Movement!=MovementMode.WallInk)
                    {
                        if(!Physics.Raycast(feet+Vector3.up*.1f,Vector3.down,out var floor,Mathf.Max(p.waveHeightDown,p.waveHeightUp)+1,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))continue;
                        if(feet.y-floor.point.y>.35f*SpecialWeaponDefaults.Scale)continue;
                    }
                    if(!e.WaveHits.Add((player.PlayerId,s.Revision,wave)))continue;
                    player.ReceiveDamage(e.State.Team,p.innerDamage,d,e.State.Owner);Mark(player,e.State.Team,p.markSeconds);
                }
                if(e.State.Health>0&&age<57d/60)
                {
                    float frame=(float)age*60,ratio=frame<4?Mathf.Lerp(0,.19f,frame/4):frame<10?Mathf.Lerp(.19f,.4f,(frame-4)/6):Mathf.Lerp(.4f,.615f,(frame-10)/8);
                    if(InkExplosionRules.IntersectsCylinder(player,e.State.Position,ratio*p.waveRadius,p.waveHeightDown,p.waveHeightUp)&&e.WaveHits.Add((player.PlayerId,s.Revision,-1)))Mark(player,e.State.Team,45d/60);
                }
                if(e.Target!=null&&Vector3.Distance(e.Target.HitCollider.ClosestPoint(feet+Vector3.up*.5f),feet+Vector3.up*.5f)<GameplayConfig.GetHero(s.HeroId).BodyRadius)
                {
                    var key=(player.PlayerId,s.Revision);if(!e.ContactAt.TryGetValue(key,out double next)||_now>=next){e.ContactAt[key]=_now+.5;DamageWithKnockback(e,player,p.directDamage,d,p.contactKnockbackAcceleration);}
                }
            }
        }
        void DamageWaveObjects(Entity e,int pulses,double age)
        {
            var p=e.Config.P;
            bool Crossing(Vector3 point,int wave)
            {var d=point-e.State.Position;double a=age-p.waveFirst-wave*p.waveInterval;return a>=0&&a<=p.waveLifetime&&d.y<=p.waveHeightUp&&d.y>=-p.waveHeightDown&&Mathf.Abs(new Vector2(d.x,d.z).magnitude-p.waveRadius*(float)(a/p.waveLifetime))<=p.waveRadius/p.waveLifetime/60+.15f;}
            for(int wave=0;wave<pulses;wave++)
            {
                foreach(var t in PrototypeMatch.Current.SubWeapons.Entities)
                    if(!t.Removed&&t.State.Health>0&&t.State.Team!=e.State.Team&&Crossing(t.State.Position,wave)&&e.WaveObjectHits.Add((t.State.Id,wave,false)))
                        PrototypeMatch.Current.SubWeapons.DamageObject(t.State.Id,e.State.Team,p.innerDamage*SpecialObjectDamage.ToSub(e.State.Type,t.State.Type));
                foreach(var t in _entities)
                    if(t.State.Health>0&&t.State.Team!=e.State.Team&&Crossing(t.State.Position,wave)&&e.WaveObjectHits.Add((t.State.Id,wave,true)))DamageObject(t.State.Id,e.State.Team,p.innerDamage*2);
            }
        }
        void Mark(PrototypePlayer player,byte team,double seconds)
        {var s=player.Snapshot.Value;if(!s.IsAlive)return;double until=_now+seconds;if(team==1)s.MarkedUntilPink=Math.Max(s.MarkedUntilPink,until);else s.MarkedUntilBlue=Math.Max(s.MarkedUntilBlue,until);player.Snapshot.Value=s;}
        void StepStorm(Entity e,IReadOnlyList<PrototypePlayer> players,float dt)
        {
            var p=e.Config.P;
            if(e.State.Phase==SpecialEntityPhase.Warning){if(_now+1e-6<e.State.Changed+p.effectDelay)return;e.State.Position+=Vector3.up*p.cloudHeight;Phase(e,SpecialEntityPhase.Active);e.State.Expires=_now+p.effectDuration;SetOwnerLock(e,_now+p.chargeLock);}
            e.State.Position+=e.State.Direction*p.cloudSpeed*dt;e.State.Radius=p.innerRadius;
            foreach(var player in players)
            {
                var s=player.Snapshot.Value;if(!s.IsAlive)continue;Vector3 point=s.Position+Vector3.up*GameplayConfig.GetHero(s.HeroId).StandingHeight*.5f;
                Vector3 d=point-e.State.Position;if(d.y>0||new Vector2(d.x,d.z).magnitude>p.innerRadius||!Visible(new Vector3(point.x,e.State.Position.y,point.z),point))continue;
                if(s.Team!=e.State.Team){if(_rainDamaged.Add((player.PlayerId,s.Revision)))player.ReceiveDamage(e.State.Team,p.damagePerSecond*dt,Vector3.down,e.State.Owner);}
                else{s.RainRecoveryUntil=_now+.1;player.Snapshot.Value=s;}
            }
            // All 72 gameplay drops have independent fixed-step flights. The renderer may show fewer lines.
            if(e.RainPositions==null)
            {
                e.RainPositions=new Vector3[p.rainDrops];e.RainVelocities=new Vector3[p.rainDrops];e.RainReadyAt=new double[p.rainDrops];
                for(int i=0;i<p.rainDrops;i++)e.RainReadyAt[i]=_now+i*.5/p.rainDrops;
            }
            for(int i=0;i<p.rainDrops;i++)
            {
                if(e.RainReadyAt[i]>_now)continue;
                if(e.RainReadyAt[i]>0)
                {
                    uint n=++e.PaintOrdinal;float angle=n*2.39996323f,radius=p.innerRadius*Mathf.Sqrt((n%p.rainDrops+.5f)/p.rainDrops);
                    e.RainPositions[i]=e.State.Position+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*radius;e.RainVelocities[i]=Vector3.zero;e.RainReadyAt[i]=0;
                }
                Vector3 velocity=e.RainVelocities[i]*Mathf.Pow(1-p.rainDrag,dt*60);
                Vector3 delta=velocity*dt+Vector3.down*p.rainGravity*dt*dt*.5f;velocity+=Vector3.down*p.rainGravity*dt;
                if(Physics.SphereCast(e.RainPositions[i],p.rainFieldRadius,delta.normalized,out var hit,delta.magnitude,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))
                {PaintSurface(e,hit,p.paintRadius);e.RainReadyAt[i]=_now+dt;}
                else{e.RainPositions[i]+=delta;e.RainVelocities[i]=velocity;if(e.RainPositions[i].y< -10)e.RainReadyAt[i]=_now+dt;}
            }
            DamageObjects(e,e.State.Position,p.innerRadius,p.damagePerSecond*dt,true,100);
        }
        void Blast(Entity e,Vector3 position,PrototypePlayer direct,bool deduplicate)
        {
            var p=e.Config.P;
            foreach(var player in PrototypeMatch.Current.Players)
            {
                var s=player.Snapshot.Value;if(player==direct||!s.IsAlive||s.Team==e.State.Team)continue;
                if(!InkExplosionRules.ClosestPoint(player,position,out var point))continue;
                float d=Vector3.Distance(point,position);
                float damage=d<=p.innerRadius?p.innerDamage:d<=p.outerRadius?p.outerDamage:0;
                if(damage>0&&Visible(position,point)){if(deduplicate)ApplyVolleyDamage(e,player,damage,point-position);else DamageWithKnockback(e,player,damage,point-position,p.knockbackAcceleration);}
            }
            DamageObjects(e,position,p.outerRadius,p.outerDamage,false,0,p.innerRadius,p.innerDamage);
            PaintDisk(e,position,p.paintRadius);
        }
        // Sample the current tick's expanding field even when subs are stepped before specials.
        public bool AbsorbsBomb(Vector3 from,Vector3 to,float radius,byte team,double now)
        {
            foreach(var e in _entities)
            {
                if(e.Removed||e.State.Team==team||e.State.Type!=SpecialWeaponType.TripleInkstrike||e.State.Phase==SpecialEntityPhase.Flying)continue;
                var p=e.Config.P;double start=e.State.Changed+(e.State.Phase==SpecialEntityPhase.Warning?p.effectDelay:0);
                double age=now-start;if(age< -1e-6||age+1e-6>=p.effectDuration)continue;
                float r=p.innerRadius*Mathf.Clamp01((float)age/p.expandSeconds);
                if(SegmentTouchesCylinder(from-e.State.Position,to-e.State.Position,radius+r,-p.damageHeightDown-radius,p.damageHeight+radius))return true;
            }
            return false;
        }
        static bool SegmentTouchesCylinder(Vector3 a,Vector3 b,float radius,float bottom,float top)
        {
            Vector3 d=b-a;float lo=0,hi=1;
            if(Mathf.Abs(d.y)<.000001f){if(a.y<bottom||a.y>top)return false;}
            else{float first=(bottom-a.y)/d.y,last=(top-a.y)/d.y;lo=Mathf.Max(0,Mathf.Min(first,last));hi=Mathf.Min(1,Mathf.Max(first,last));if(lo>hi)return false;}
            float planar=d.x*d.x+d.z*d.z;
            float t=planar>0?Mathf.Clamp(-(a.x*d.x+a.z*d.z)/planar,lo,hi):lo;
            Vector3 point=a+d*t;return point.x*point.x+point.z*point.z<=radius*radius;
        }
        static void ApplyVolleyDamage(Entity e,PrototypePlayer player,float damage,Vector3 direction)
        {
            var key=(player.PlayerId,player.Snapshot.Value.Revision);
            if(damage>=e.Config.P.directDamage)e.Hit.Add(key);
            e.PlayerDamage.TryGetValue(key,out float previous);
            if(damage<=previous)return;
            e.PlayerDamage[key]=damage;
            DamageWithKnockback(e,player,damage-previous,direction,e.Config.P.knockbackAcceleration);
        }
        static void DamageWithKnockback(Entity e,PrototypePlayer player,float damage,Vector3 direction,float acceleration)
        {
            float before=player.Snapshot.Value.Health;
            player.ReceiveDamage(e.State.Team,damage,direction,e.State.Owner);
            if(player.Snapshot.Value.Health<before&&acceleration>0)
                player.ReceiveSpecialKnockback(direction,acceleration,e.Config.P.knockbackRetention,e.Config.P.knockbackDistance);
        }
        static float VolleyObjectIncrement(Dictionary<uint,float> ledger,uint id,float damage)
        {ledger.TryGetValue(id,out float previous);if(damage<=previous)return 0;ledger[id]=damage;return damage-previous;}
        void DamageObjects(Entity e,Vector3 origin,float radius,float outer,bool rain,float height=0,float innerRadius=0,float inner=0)
        {
            var match=PrototypeMatch.Current;
            foreach(var target in match.SubWeapons.Entities)
            {
                if(target.Removed||target.State.Health<=0||target.State.Team==e.State.Team)continue;
                Vector3 point=target.HitTarget!=null?target.HitTarget.HitCollider.ClosestPoint(origin):target.State.Position,d=point-origin;
                float distance=height>0?new Vector2(d.x,d.z).magnitude:d.magnitude;
                if(distance>radius||(height>0&&(rain?d.y>0||d.y< -height:d.y< -e.Config.P.damageHeightDown||d.y>height)))continue;
                if(!rain&&!Visible(origin,point))continue;
                if(rain&&!Visible(new Vector3(point.x,origin.y,point.z),point))continue;
                float damage=distance<=innerRadius?inner:outer;
                if(e.State.Type==SpecialWeaponType.Trizooka)damage=VolleyObjectIncrement(e.ObjectDamage,target.State.Id,damage);
                match.SubWeapons.DamageObject(target.State.Id,e.State.Team,damage*SpecialObjectDamage.ToSub(e.State.Type,target.State.Type));
            }
            foreach(var target in _entities)
            {
                if(target.Removed||target.State.Health<=0||target.State.Team==e.State.Team)continue;
                Vector3 d=target.State.Position-origin;float distance=height>0?new Vector2(d.x,d.z).magnitude:d.magnitude;
                if(distance>radius||(height>0&&(rain?d.y>0||d.y< -height:d.y< -e.Config.P.damageHeightDown||d.y>height)))continue;
                Vector3 sight=rain?new Vector3(target.State.Position.x,origin.y,target.State.Position.z):origin;
                if(!Visible(sight,target.State.Position))continue;
                float damage=distance<=innerRadius?inner:outer;
                if(e.State.Type==SpecialWeaponType.Trizooka)damage=VolleyObjectIncrement(e.SpecialObjectDamage,target.State.Id,damage);
                DamageObject(target.State.Id,e.State.Team,damage*SpecialObjectDamage.ToSonar(e.State.Type));
            }
        }
        public static bool Visible(Vector3 from,Vector3 to)=>!Physics.Linecast(from,to,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore);
        void PaintSurface(Entity e,RaycastHit hit,float radius)
        {
            var surface=hit.collider.GetComponentInParent<PaintSurface>();if(surface==null||radius<=0)return;
            Vector3 point=hit.point;var normal=hit.normal;var stamp=new PaintStamp{Position=point,Normal=normal};InkShapeAtlas.StampBasis(stamp,out var tangent,out var bitangent);
            Vector4 clip0=default,clip1=default;
            for(int i=0;i<8;i++){float a=i*Mathf.PI/4;Vector3 dir=tangent*Mathf.Cos(a)+bitangent*Mathf.Sin(a);float extent=radius*1.414214f;
                if(Physics.Raycast(point+normal*.025f,dir,out var edge,extent,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))extent=Mathf.Max(0,edge.distance-.025f);
                if(i<4)clip0[i]=extent;else clip1[i-4]=extent;}
            PrototypeMatch.Current.Paint(surface,point,normal,radius,e.State.Team,.65f,1,e.State.Id*2654435761u+ ++e.PaintOrdinal,clipEnabled:true,clip0:clip0,clip1:clip1,
                credit:new PaintCredit(e.State.Owner,e.State.Team,e.State.Round,e.State.HeroRevision,PaintAttackKind.Special));
        }
        void PaintDisk(Entity e,Vector3 position,float radius)
        {
            if(Physics.Raycast(position+Vector3.up*.1f,Vector3.down,out var floor,radius+2,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))PaintSurface(e,floor,radius);
            // Splash paints actual nearby receiver planes, with radial clipping at obstacles.
            for(int i=0;i<8;i++){float a=i*Mathf.PI/4;Vector3 d=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));if(Physics.Raycast(position,d,out var hit,radius,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))PaintSurface(e,hit,Mathf.Sqrt(Mathf.Max(0,radius*radius-hit.distance*hit.distance)));}
        }
        public void ReefContact(PrototypePlayer owner,PlayerSnapshot s,Vector3 previous,double now)
        {
            var key=(owner.PlayerId,s.SpecialAction);if(!_reefHits.TryGetValue(key,out var hits)){hits=new();_reefHits[key]=hits;}
            var p=SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId).P;
            Vector3 offset=Vector3.up*p.rideDetectOffsetY+s.SpecialDirection*p.rideDetectOffsetZ;
            Vector3 from=previous+offset,delta=s.Position-previous;
            var examined=new HashSet<(ulong,uint)>(hits);
            // Sweep the reference contact sphere against the same live hit proxies used by
            // bullets. Foot-point distances incorrectly hit airborne targets and miss body edges.
            while(_aim.Overlap(from,p.ridePlayerRadius,owner.PlayerId,-s.SpecialDirection,out var contact,true,s.Team,examined)||
                _aim.ClosestCast(from,delta,delta.magnitude,p.ridePlayerRadius,owner.PlayerId,out contact,true,s.Team,examined))
            {
                var target=contact.Collider.GetComponentInParent<PrototypePlayer>();var t=target.Snapshot.Value;
                examined.Add((target.PlayerId,t.Revision));
                Vector3 end=from+delta;
                Vector3 point=target.SwimBody!=null&&target.SwimBody.FlatHitActive?target.SwimBody.ClosestHitPoint(end):contact.Collider.ClosestPoint(end);
                if(Visible(from,point)||Visible(end,point)){hits.Add((target.PlayerId,t.Revision));target.ReceiveDamage(s.Team,p.directDamage,s.SpecialDirection,owner.PlayerId);}
            }
            // Rut uses actual ray receiver; no charge while riding.
            if(s.SimulationTick%3==0){var e=new Entity(new SpecialEntityState{Owner=owner.PlayerId,Team=s.Team,Round=PrototypeMatch.Current.State.Value.Round,HeroRevision=s.HeroRevision,Id=s.SpecialAction},SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId));PaintDisk(e,s.Position,p.rideRutRadius);}
        }
        public void DamageObject(uint id,byte team,float damage)
        {foreach(var e in _entities)if(!e.Removed&&e.State.Id==id&&e.State.Team!=team&&e.State.Health>0){e.State.Health=Mathf.Max(0,e.State.Health-damage);if(e.State.Health<=0)DestroyGenerator(e,true);else Publish(e);return;}}
        void SetOwnerLock(Entity e,double until)
        {if(PrototypePlayer.ByOwner.TryGetValue(e.State.Owner,out var owner)){var s=owner.Snapshot.Value;if(s.HeroRevision==e.State.HeroRevision&&s.SpecialAction==e.State.Action){s.SpecialChargeLockedUntil=until;owner.Snapshot.Value=s;}}}
        void DestroyGenerator(Entity e,bool destroyed)
        {
            if(e.Target!=null){e.Target.gameObject.SetActive(false);UnityEngine.Object.Destroy(e.Target.gameObject);e.Target=null;}
            e.State.Health=0;if(destroyed)SetOwnerLock(e,_now);
            if(e.State.Pulse==0){Remove(e);return;}
            e.State.Expires=e.State.Changed+e.Config.P.waveFirst+(e.State.Pulse-1)*e.Config.P.waveInterval+e.Config.P.waveLifetime;
            Publish(e);
        }
        public void DamageFromMainExplosion(InkShot shot,Vector3 origin,bool collision,uint? directObject=null)
        {if(shot.Configuration?.Ammo==null)return;foreach(var e in _entities)if(!e.Removed&&e.Target!=null&&e.State.Team!=shot.Team&&!(shot.Configuration.Ammo.ExcludeDirectHitFromExplosion&&directObject==e.State.Id)){Vector3 p=e.Target.HitCollider.ClosestPoint(origin);if(Visible(origin,p))DamageObject(e.State.Id,shot.Team,InkExplosionRules.Damage(shot.Configuration.Ammo,collision,Vector3.Distance(p,origin))*SpecialObjectDamage.FromMain(shot.Configuration,true));}}
        public void DamageFromSub(byte team,SubWeaponType type,Vector3 origin,float innerRadius,float outerRadius,float inner,float outer)
        {foreach(var e in _entities)if(!e.Removed&&e.Target!=null&&e.State.Team!=team){Vector3 p=e.Target.HitCollider.ClosestPoint(origin);float d=Vector3.Distance(p,origin);if(d<=outerRadius&&Visible(origin,p))DamageObject(e.State.Id,team,(d<=innerRadius?inner:outer)*SpecialObjectDamage.FromSub(type));}}
        void Remove(Entity e){if(e.Removed)return;e.Removed=true;e.State.Phase=SpecialEntityPhase.Removed;Publish(e);if(e.Target!=null){e.Target.gameObject.SetActive(false);UnityEngine.Object.Destroy(e.Target.gameObject);}}
        public void ClearOwner(ulong owner){foreach(var e in _entities)if(e.State.Owner==owner)Remove(e);var keys=new List<(ulong,uint)>(_reefHits.Keys);foreach(var k in keys)if(k.Item1==owner)_reefHits.Remove(k);}
        public void Clear(){foreach(var e in _entities)Remove(e);_entities.Clear();_capture.Clear();_reefHits.Clear();}
    }
    public sealed class SpecialWeaponTarget:MonoBehaviour
    {
        SpecialWeaponService _service;public uint Id;public byte Team;public Collider HitCollider;
        public void Initialize(SpecialWeaponService service,SpecialWeaponService.Entity e){_service=service;InitializePresentation(e.State);}
        public void InitializePresentation(SpecialEntityState state){Id=state.Id;Team=state.Team;gameObject.layer=SubWeaponTarget.Layer;transform.position=state.Position;var c=gameObject.AddComponent<CapsuleCollider>();c.radius=.75f*SpecialWeaponDefaults.Scale;c.height=2.75f*SpecialWeaponDefaults.Scale;c.center=Vector3.up*c.height*.5f;c.isTrigger=true;HitCollider=c;}
        public bool CanHit(ulong owner,byte? team)=>team.HasValue?team.Value!=Team:!PrototypePlayer.ByOwner.TryGetValue(owner,out var p)||p.Snapshot.Value.Team!=Team;
        public void Damage(byte team,float amount)=>_service?.DamageObject(Id,team,amount);
        public void Hit(InkShot shot,float damage)=>Damage(shot.Team,damage*SpecialObjectDamage.FromMain(shot.Configuration));
    }
    public static class SpecialObjectDamage
    {
        public static float FromMain(WeaponRuntimeConfig c,bool explosion=false)=>c?.WeaponPrefabAddress switch
        { "Weapon/RocketLauncher"=>2, "Weapon/Shotgun"=>explosion?1:1.25f, _=>1 };
        public static float FromSub(SubWeaponType t)=>t==SubWeaponType.FizzyBomb?1.8f:t==SubWeaponType.Torpedo?2:1;
        public static float ToSonar(SpecialWeaponType t)=>t switch{SpecialWeaponType.InkStorm=>3,SpecialWeaponType.Reefslider=>5,SpecialWeaponType.TripleInkstrike or SpecialWeaponType.WaveBreaker=>2,_=>1};
        public static float ToSub(SpecialWeaponType source,SubWeaponType target)
        {
            bool wall=target==SubWeaponType.SplashWall, sprinkler=target==SubWeaponType.Sprinkler;
            return source switch
            {
                SpecialWeaponType.Trizooka=>wall?6:1,
                SpecialWeaponType.TripleInkstrike=>2,
                SpecialWeaponType.WaveBreaker=>wall||sprinkler?100:1,
                SpecialWeaponType.InkStorm=>wall?3:target==SubWeaponType.Torpedo?.25f:1,
                SpecialWeaponType.Reefslider=>wall||sprinkler?5:1,_=>1
            };
        }
    }
}
