#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Explicit command-line-only acceptance harness. Never runs in a normal room.
    public sealed class SpecialWeaponNetworkSmoke : MonoBehaviour
    {
        static SpecialWeaponNetworkSmoke instance;
        readonly HashSet<SpecialWeaponType> seen=new();
        readonly HashSet<int> own=new(),remote=new();
        readonly Dictionary<ulong,uint> actions=new();
        readonly List<string> errors=new();
        bool host,ready,ending,initial,lateJoin,reset,independentLoadouts;
        int stage=-2,maxPlayers;float started;uint hash,paint;
        SpecialInputCacheProbe inputProbe;
        static string Arg(string key,string fallback)=>HeroSelectionSmoke.Arg(key,fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]static void Initialize()
        {
            if(instance!=null||!Environment.GetCommandLineArgs().Contains("-specialRole"))return;
            var go=new GameObject("Special network acceptance");DontDestroyOnLoad(go);instance=go.AddComponent<SpecialWeaponNetworkSmoke>();
        }
        void Start()=>Run().Forget();
        async UniTask Run()
        {
            started=Time.realtimeSinceStartup;host=Arg("-specialRole","host")=="host";Application.logMessageReceived+=Log;
            string probeRoot=Arg("-specialInputProbe","");if(probeRoot.Length>0)inputProbe=new SpecialInputCacheProbe(probeRoot,host);
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                if(!host)await UniTask.Delay(TimeSpan.FromSeconds(4));
                await PrototypeApp.Current.Connect(host,"127.0.0.1",ushort.Parse(Arg("-specialPort","20680")));
                if(!PrototypeApp.Current.InRoom)throw new Exception(PrototypeApp.Current.Error);ready=true;
            }
            catch(Exception e){Finish(e.ToString()).Forget();}
        }
        static int Stage(double now)=>now<15?-1:Math.Min(4,(int)((now-15)/12));
        void Log(string text,string stack,LogType type){if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert)errors.Add(text);}
        void Update()
        {
            if(ending)return;
            if(Time.realtimeSinceStartup-started>150){Finish("Timeout").Forget();return;}
            var match=PrototypeMatch.Current;var local=PrototypePlayer.Local;if(!ready||match==null||local==null)return;
            double now=local.NetworkManager.ServerTime.Time;maxPlayers=Math.Max(maxPlayers,PrototypePlayer.ByOwner.Count);
            if(host&&!initial)
            {
                initial=true;var s=local.Snapshot.Value;s.SpecialWeaponId=3;local.Snapshot.Value=s;
                var e=match.SpecialWeapons.Spawn(local,s,now,match.State.Value.Round);e.State.Position=s.Position+Vector3.up*.5f;e.State.Velocity=Vector3.down*2;
            }
            int next=Stage(now);
            if(next!=stage)
            {
                stage=next;
                if(host&&stage>=0)
                {
                    match.SpecialWeapons.Clear();
                    foreach(var p in match.Players)
                    {
                        var s=p.Snapshot.Value;SpecialWeaponSimulation.Interrupt(ref s);
                        s.SubWeaponId=p==local?1:13;s.SpecialWeaponId=stage+1;s.HeroRevision++;s.SpecialPoints=200;s.SpecialChargeLockedUntil=0;s.SpecialFailure=SpecialFailure.None;s.SpecialReadyAt=0;s.AttackRecoveryUntil=s.SubRecoveryUntil=0;
                        s.Ink=s.Health=100;s.ProtectedUntil=now+12;p.Snapshot.Value=s;
                    }
                }
            }
            if(match.SpecialPresentation!=null)
            {
                seen.UnionWith(match.SpecialPresentation.ObservedTypes);
                if(!host&&now<15&&match.SpecialPresentation.ObservedTypes.Contains(SpecialWeaponType.WaveBreaker))lateJoin=true;
            }
            var peers=PrototypePlayer.ByOwner.Values.ToArray();
            if(peers.Length==2){var a=peers[0].Snapshot.Value;var b=peers[1].Snapshot.Value;independentLoadouts|=a.HeroId==b.HeroId&&a.SubWeaponId!=b.SubWeaponId;}
            foreach(var p in PrototypePlayer.ByOwner.Values)
            {
                var s=p.Snapshot.Value;
                if(actions.TryGetValue(p.PlayerId,out uint previous)&&s.SpecialAction>previous&&stage>=0)(p==local?own:remote).Add(stage);
                actions[p.PlayerId]=s.SpecialAction;
            }
            if(now>79&&paint==0){hash=match.Arena.OwnershipHash();paint=host?match.PaintSequence:match.AppliedPaintSequence;}
            if(host&&now>82&&!reset){var state=match.State.Value;state.Phase=MatchPhase.Finished;match.State.Value=state;match.ReturnToRoom();reset=true;}
            if(!host&&now>84)reset|=match.State.Value.Phase==MatchPhase.Practice&&match.AppliedPaintSequence==0&&match.SpecialPresentation.Count==0;
            if(inputProbe!=null&&now>84&&reset)
            {
                try{inputProbe.Step(local,match);}catch(Exception e){Finish(e.ToString()).Forget();return;}
                if(inputProbe.Complete)Finish(null).Forget();
            }
            else if(inputProbe==null&&now>(host?95:87))Finish(null).Forget();
        }
        public static void ModifyInput(PrototypePlayer player,ref PlayerInputFrame input)
        {
            if(instance==null||!instance.ready||instance.ending)return;
            double now=player.NetworkManager.ServerTime.Time;int stage=Stage(now);double age=now-15-Math.Max(0,stage)*12;
            input.CancelFire=input.CancelSub=false;input.SubHeld=false;input.Move=Vector2.zero;input.Swim=false;input.Look=new Vector2(player.Snapshot.Value.Team==1?0:180,45);
            if(instance.inputProbe!=null&&now>82)
            {
                input.Fire=false;input.FireSequence=player.PresentedState.ConsumedFire;input.ReleaseSequence=player.PresentedState.ConsumedRelease;
                instance.inputProbe.ObserveInput(player,ref input);return;
            }
            input.SpecialSequence=(uint)(stage<0?0:stage+(age>=1?1:0));
            int cycle=age<1.5?0:Math.Min(3,1+(int)(age-1.5));double within=age-1.5-Math.Max(0,cycle-1);
            uint basis=(uint)Math.Max(0,stage)*3;
            input.Fire=stage>=0&&cycle>0&&within>=0&&within<.2;
            input.FireSequence=basis+(uint)cycle;input.ReleaseSequence=input.Fire?input.FireSequence-1:input.FireSequence;
        }
        async UniTask Finish(string error)
        {
            if(ending)return;ending=true;
            bool passed=error==null&&errors.Count==0&&maxPlayers>=2&&seen.Count==5&&own.Count==5&&remote.Count==5&&reset&&independentLoadouts&&(host||lateJoin);
            passed&=inputProbe==null||inputProbe.Complete;
            string path=Arg("-specialOutput","Reports/SpecialWeapons/network.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path,$"passed={passed}\nrole={(host?"host":"client")}\nseen={string.Join(",",seen)}\nown={string.Join(",",own)}\nremote={string.Join(",",remote)}\nindependentLoadouts={independentLoadouts}\nlateJoin={lateJoin}\nreset={reset}\nfinalPaintHash={hash}\nfinalPaintSequence={paint}\nerror={error}\nlogs={string.Join(" | ",errors)}");
            File.AppendAllText(path,$"\ninputCacheProbe={inputProbe?.Complete}\ninputCacheDetail={inputProbe?.Detail}\n");inputProbe?.RestorePreferences();
            if(PrototypeMatch.Current!=null)File.AppendAllText(path,$"resetState={PrototypeMatch.Current.State.Value.Phase}/{PrototypeMatch.Current.AppliedPaintSequence}/{PrototypeMatch.Current.SpecialPresentation?.Count}\n");
            Application.logMessageReceived-=Log;
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
            Application.Quit(passed?0:1);
        }
    }

    // Only constructed by the explicit independent-process acceptance flag.
    // Files coordinate the fixture; player state still crosses the actual NGO connection.
    sealed class SpecialInputCacheProbe
    {
        readonly string root;readonly bool host;
        static readonly System.Reflection.FieldInfo Counter=typeof(PrototypePlayer).GetField("_specialSequence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        int stage,frames;uint life,equipment,baseline,action;double changedAt;
        Vector3 forceStart;bool forceSeen;
        bool saved,hadSub,hadSpecial;int savedSub,savedSpecial;string subKey,specialKey;
        public bool Complete{get;private set;}
        public string Detail=>$"stage={stage};frames={frames};life={life};equipment={equipment};baseline={baseline};action={action};forceSeen={forceSeen}";
        public SpecialInputCacheProbe(string root,bool host){this.root=root;this.host=host;Directory.CreateDirectory(root);}
        bool Has(string name)=>File.Exists(Path.Combine(root,name));
        void Signal(string name)=>File.WriteAllText(Path.Combine(root,name),Detail);
        static void Require(bool value,string message){if(!value)throw new Exception("Q cache probe: "+message);}
        void Arm(PrototypePlayer local)
        {
            var s=local.Snapshot.Value;life=s.Revision;equipment=s.HeroRevision;baseline=s.SpecialConsumed;action=s.SpecialAction;frames=0;
            Counter.SetValue(local,baseline+7); // Captured locally, intentionally not sent in this old scope.
        }
        public void ObserveInput(PrototypePlayer local,ref PlayerInputFrame input)
        {
            if(host||stage==0)return;
            bool waitingOld=(stage==1&&input.Revision==life)||(stage==3&&input.HeroRevision==equipment);
            if(waitingOld){input.SpecialSequence=baseline;return;}
            // New-scope frames retain the counter copied by the real FixedUpdate.
            if(stage==1||stage==2||stage==3||stage==4)
            {
                Require(input.SpecialSequence==baseline,"unacknowledged old Q leaked into new input: "+Detail);
                frames++;
            }
        }
        public void Step(PrototypePlayer local,PrototypeMatch match)
        {
            if(Complete)return;double now=local.NetworkManager.ServerTime.Time;
            if(host)
            {
                if(stage==5){if(now-changedAt>2)Complete=true;return;}
                var peer=match.Players.FirstOrDefault(p=>p!=local&&!p.IsTestBot);if(peer==null)return;
                var s=peer.Snapshot.Value;
                if(stage==0&&Has("life-armed"))
                {action=s.SpecialAction;equipment=s.HeroRevision;s.SpecialPoints=200;peer.Snapshot.Value=s;peer.Respawn();stage=1;}
                else if(stage==1&&Has("equipment-armed")&&s.HeroRevision!=equipment)
                {Require(s.SpecialAction==action,"stale Q activated during respawn or loadout change");s.SpecialPoints=200;peer.Snapshot.Value=s;stage=2;}
                else if(stage==2&&Has("client-complete"))
                {Require(s.SpecialAction==action+1,"fresh client Q must activate exactly once on Host");Signal("host-complete");stage=3;changedAt=now;}
                else if(stage==3&&now-changedAt>1)
                {
                    forceStart=s.Position;var p=SpecialWeaponConfigService.Current.Get(1).P;
                    peer.ReceiveSpecialKnockback(Vector3.right,p.knockbackAcceleration,p.knockbackRetention,p.knockbackDistance);
                    forceSeen=peer.Snapshot.Value.SpecialImpulseVelocity.sqrMagnitude>0;Require(forceSeen,"Host did not queue force");
                    string temporary=Path.Combine(root,"host-force.tmp");File.WriteAllText(temporary,JsonUtility.ToJson(forceStart));File.Move(temporary,Path.Combine(root,"host-force"));stage=4;
                }
                else if(stage==4&&Has("client-force-verified"))
                {Require(s.Position.x>forceStart.x,"Authority did not move under force");Signal("host-force-complete");stage=5;changedAt=now;}
                return;
            }
            var current=local.Snapshot.Value;
            if(stage==0)
            {
                if(!match.InitialSyncComplete||local.HeroChangePending||!current.IsAlive)return;
                subKey="Loadout.Sub."+current.HeroId;specialKey="Loadout.Special."+current.HeroId;
                hadSub=PlayerPrefs.HasKey(subKey);hadSpecial=PlayerPrefs.HasKey(specialKey);savedSub=PlayerPrefs.GetInt(subKey);savedSpecial=PlayerPrefs.GetInt(specialKey);saved=true;
                Arm(local);stage=1;Signal("life-armed");
            }
            else if(stage==1&&current.Revision!=life)
            {Require((uint)Counter.GetValue(local)==baseline,"respawn did not rebase local counter");changedAt=now;stage=2;}
            else if(stage==2&&now-changedAt>1&&frames>=30)
            {
                Require(current.SpecialAction==action&&current.SpecialPoints==200,"respawn stale Q consumed charge");
                Arm(local);stage=3;Signal("equipment-armed");
                local.RequestLoadoutChange(new PlayerLoadout(current.HeroId,current.SubWeaponId==1?13:1,1),HeroSelectionOrigin.Warmup);
            }
            else if(stage==3&&current.HeroRevision!=equipment)
            {Require((uint)Counter.GetValue(local)==baseline,"equipment did not rebase local counter");changedAt=now;stage=4;}
            else if(stage==4&&now-changedAt>1&&frames>=30&&current.SpecialPoints==200)
            {
                Require(current.SpecialAction==action,"equipment stale Q activated");
                Counter.SetValue(local,baseline+1);stage=5;changedAt=now;
            }
            else if(stage==5&&now-changedAt>1&&current.SpecialAction==action+1)
            {Require(current.SpecialConsumed==baseline+1,"fresh Q was not acknowledged");Signal("client-complete");stage=6;}
            else if(stage==6&&Has("host-force"))
            {
                forceStart=JsonUtility.FromJson<Vector3>(File.ReadAllText(Path.Combine(root,"host-force")));
                forceSeen|=current.SpecialImpulseVelocity.sqrMagnitude>0;
                if(forceSeen&&current.Position.x>forceStart.x&&local.PresentedState.Position.x>forceStart.x){Signal("client-force-verified");stage=7;}
            }
            else if(stage==7&&Has("host-force-complete")){Complete=true;}
        }
        public void RestorePreferences()
        {
            if(!saved)return;
            if(hadSub)PlayerPrefs.SetInt(subKey,savedSub);else PlayerPrefs.DeleteKey(subKey);
            if(hadSpecial)PlayerPrefs.SetInt(specialKey,savedSpecial);else PlayerPrefs.DeleteKey(specialKey);
            PlayerPrefs.Save();saved=false;
        }
    }
}
#endif
