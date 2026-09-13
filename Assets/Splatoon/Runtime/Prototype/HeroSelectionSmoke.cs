#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Painting;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in real transport integration driver. Never active in ordinary gameplay.</summary>
    public sealed class HeroSelectionSmoke : MonoBehaviour
    {
        static HeroSelectionSmoke _instance;
        public static bool Active => _instance != null;
        public static string Arg(string key,string fallback)
        {var args=Environment.GetCommandLineArgs();int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
        bool _host,_ready,_ending,_roundStarted,_killed,_roundReset,_reconnected,_reconnecting;
        double _start;float _wallStart;int _expected,_lastDesired,_maxPlayers,_peakProjectiles;uint _shots,_life;
        bool _respawnRetained,_fullCharge,_remoteMixed;float _maxCorrection;long _peakPaint;
        readonly HashSet<int> _equipped=new(),_fired=new();readonly HashSet<ulong> _placed=new();
        readonly List<string> _errors=new();readonly List<float> _frames=new();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {if(_instance!=null||!Environment.GetCommandLineArgs().Contains("-weaponRole"))return;var go=new GameObject("HeroSelectionSmoke");DontDestroyOnLoad(go);_instance=go.AddComponent<HeroSelectionSmoke>();}
        async UniTaskVoid Start()
        {
            _host=Arg("-weaponRole","host")=="host";_expected=int.Parse(Arg("-weaponPlayers","2"));_wallStart=Time.realtimeSinceStartup;
            Application.logMessageReceived+=OnLog;
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                await UniTask.Delay(TimeSpan.FromSeconds(double.Parse(Arg("-weaponJoinDelay","0"))));
                for(int attempt=0;attempt<6;attempt++)
                {
                    await PrototypeApp.Current.Connect(_host,"127.0.0.1",ushort.Parse(Arg("-weaponPort","18213")));
                    if(PrototypeApp.Current.InRoom)break;
                    await UniTask.Delay(TimeSpan.FromSeconds(1));
                }
                if(!PrototypeApp.Current.InRoom)throw new Exception(PrototypeApp.Current.Error);
                _ready=true;
            }
            catch(Exception e){Finish(e.Message);}
        }
        void Update()
        {
            if(_ending)return;if(Time.realtimeSinceStartup-_wallStart>360){Finish("Timed out");return;}
            var match=PrototypeMatch.Current;var p=PrototypePlayer.Local;if(!_ready||match==null||p==null)return;
            if(_host)foreach(var other in match.Players)if(_placed.Add(other.OwnerClientId))
            {var s=other.Snapshot.Value;other.DiagnosticPlace(PrototypeArena.Spawn(s.Team,s.Slot),s.Team==1?0:180);}
            if(_start==0&&p.Snapshot.Value.Revision>=2&&PrototypePlayer.ByOwner.Count>=Math.Min(_expected,2)){_start=p.NetworkManager.ServerTime.Time;_life=p.Snapshot.Value.Revision;}
            if(_start==0)return;double age=p.NetworkManager.ServerTime.Time-_start;var state=p.Snapshot.Value;
            _equipped.Add(state.HeroId);if(state.ShotSequence!=_shots){_shots=state.ShotSequence;_fired.Add(state.HeroId);_fullCharge|=state.HeroId==5&&state.LastShotCharge>=1;}
            _frames.Add(Time.unscaledDeltaTime*1000);_maxPlayers=Math.Max(_maxPlayers,PrototypePlayer.ByOwner.Count);
            _peakProjectiles=Math.Max(_peakProjectiles,match.Projectiles.ActiveCount);_peakPaint=Math.Max(_peakPaint,PaintSurface.AllocatedBytes);
            _maxCorrection=Mathf.Max(_maxCorrection,p.LastCorrectionDistance);
            _remoteMixed|=PrototypePlayer.ByOwner.Values.Select(x=>x.Snapshot.Value.HeroId).Distinct().Count()>1;
            int desired=age<40 ? ((int)(age/4)+(int)p.OwnerClientId)%5+1 : 2;
            if(state.Health>0&&!p.HeroChangePending&&state.HeroId!=desired&&desired!=_lastDesired)
            {_lastDesired=desired;p.RequestHeroChange(desired,match.State.Value.Phase==MatchPhase.Practice?HeroSelectionOrigin.Warmup:HeroSelectionOrigin.Debug);}
            if(!p.HeroChangePending&&state.HeroId!=desired)_lastDesired=0;
            if(_host&&age>22&&!_roundStarted&&match.Players.Count>=2){match.StartRound();_roundStarted=true;}
            if(_host&&age>49&&!_killed){foreach(var other in match.Players)other.ReceiveDamage((byte)(other.Snapshot.Value.Team==1?2:1),200,Vector3.forward);_killed=true;}
            if(age>54&&state.Health>0&&state.Revision>_life)_respawnRetained|=state.HeroId==2;
            if(_host&&age>56&&!_roundReset)
            {var m=match.State.Value;m.Phase=MatchPhase.Finished;match.State.Value=m;match.StartRound();_roundReset=true;}
            if(!_host&&match.State.Value.Round>=2)_roundReset=true;
            if(!_host&&age>61&&Arg("-weaponReconnect","0")=="1"&&!_reconnected&&!_reconnecting)Reconnect().Forget();
            if(age>(_host?88:74))Finish(null);
        }
        public static void ModifyInput(PrototypePlayer p,ref PlayerInputFrame input)
        {
            var s=_instance;if(s==null||!s._ready||s._start==0)return;
            double age=p.NetworkManager.ServerTime.Time-s._start;
            input.Move=Vector2.zero;input.Swim=false;input.JumpSequence=p.PresentedState.ConsumedJump;
            input.Look=new Vector2((p.PresentedState.Team==1?0:180)+(p.PresentedState.Slot==0?-25:25),5);
            var weapon=Splatoon.Config.GameplayConfig.GetHero(p.PresentedState.HeroId);
            input.Fire=(WeaponSimulation.IsSemi(weapon) ? age%(.05+weapon.FireIntervalFrames/60.0)<.065 : age%3<1.9) && !p.HeroChangePending;
            if(age>40&&age<48)input.Move=new Vector2(Mathf.Sin((float)age)*.2f,0);
        }
        async UniTask Reconnect()
        {
            _reconnecting=true;_ready=false;
            try
            {
                await PrototypeApp.Current.Leave();await PrototypeApp.Current.Connect(false,"127.0.0.1",ushort.Parse(Arg("-weaponPort","18213")));
                if(!PrototypeApp.Current.InRoom||PrototypePlayer.Local.Snapshot.Value.HeroId!=1)throw new Exception("Reconnect did not restore default weapon");
                _reconnected=true;_lastDesired=0;_shots=PrototypePlayer.Local.Snapshot.Value.ShotSequence;_ready=true;
            }catch(Exception e){Finish(e.Message);}
        }
        void OnLog(string message,string stack,LogType type)
        {if((type==LogType.Error||type==LogType.Exception)&&!stack.Contains("UnityEditor.Search")&&_errors.Count<12)_errors.Add(message);}
        async void Finish(string error)
        {
            if(_ending)return;_ending=true;
            bool pass=error==null&&_errors.Count==0&&_maxPlayers==_expected&&_equipped.Count==5&&_fired.Count==5&&_fullCharge&&_remoteMixed&&_respawnRetained&&_roundReset&&_peakPaint<=128L*1048576;
            if(!_host&&Arg("-weaponReconnect","0")=="1")pass&=_reconnected;
            _frames.Sort();var p=PrototypePlayer.Local;
            int snapshotBytes;
            using(var writer=new Unity.Netcode.FastBufferWriter(1024,Unity.Collections.Allocator.Temp))
            {if(p!=null)writer.WriteNetworkSerializable(p.Snapshot.Value);snapshotBytes=writer.Length;}
            string report=$"passed={pass}\nrole={Arg("-weaponRole","host")}\nerror={error}\nequipped={string.Join(",",_equipped)}\nfired={string.Join(",",_fired)}\nfullCharge={_fullCharge}\nremoteMixed={_remoteMixed}\nmaxPlayers={_maxPlayers}\nrespawnRetained={_respawnRetained}\nroundReset={_roundReset}\nreconnected={_reconnected}\npeakProjectiles={_peakProjectiles}\npeakPaintMiB={_peakPaint/1048576.0}\nmaxCorrection={_maxCorrection}\nsnapshotBytes={snapshotBytes}\np95FrameMs={(_frames.Count>0?_frames[(int)((_frames.Count-1)*.95)]:0)}\nruntimeErrors={string.Join(" | ",_errors)}";
            string output=Arg("-weaponOutput","Temp/WeaponSmoke.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));File.WriteAllText(output,report);Debug.Log("[WEAPON-SMOKE] "+report);
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(pass?0:1);
#else
            Application.Quit(pass?0:1);
#endif
        }
        void OnDestroy(){Application.logMessageReceived-=OnLog;if(_instance==this)_instance=null;}
    }
}
#endif
