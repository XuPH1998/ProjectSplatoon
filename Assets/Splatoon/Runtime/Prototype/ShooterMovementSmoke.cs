#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Config;

namespace Splatoon.Prototype
{
    // Explicit diagnostic opt-in; never runs in ordinary gameplay.
    public sealed class ShooterMovementSmoke : MonoBehaviour
    {
        static ShooterMovementSmoke _instance;
        public static bool Active => _instance != null;
        bool _host,_ready,_placed,_killed,_ending;
        bool _fullRound,_roundStarted,_roundFinished,_roundReset,_reconnected,_reconnecting,_ownershipRequested,_paintedRound,_resetOwnershipRequested;
        readonly List<string> _errors=new();
        bool _timeoutCheck,_timeoutObserved,_timeoutRecovered,_wallPredictionLead,_movePredictionLead;
        double _start;
        int _expected;
        float _wallStart;
        readonly HashSet<MovementMode> _modes=new();
        readonly HashSet<MovementMode> _authorityModes=new();
        readonly List<float> _frames=new();
        bool _fire,_respawn,_captured,_capturedFire;uint _life;
        long _peakPaintBytes;int _maxPlayers,_peakPending;float _maxCorrection;ulong _rtt;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            if(_instance!=null||!Environment.GetCommandLineArgs().Contains("-shooterRole"))return;
            var go=new GameObject("ShooterMovementSmoke");DontDestroyOnLoad(go);_instance=go.AddComponent<ShooterMovementSmoke>();
        }
        static string Arg(string key,string fallback)
        {var args=Environment.GetCommandLineArgs();int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
        async UniTaskVoid Start()
        {
            _host=Arg("-shooterRole","host")=="host";_expected=int.Parse(Arg("-shooterPlayers","2"));_wallStart=Time.realtimeSinceStartup;
            _fullRound=Environment.GetCommandLineArgs().Contains("-shooterFullRound");Application.logMessageReceived+=OnLog;
            _timeoutCheck=Environment.GetCommandLineArgs().Contains("-shooterTimeoutCheck");
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                for(int attempt=0;attempt<(_host?1:5);attempt++)
                {
                    await PrototypeApp.Current.Connect(_host,"127.0.0.1",ushort.Parse(Arg("-shooterPort","18013")));
                    if(PrototypeApp.Current.InRoom)break;
                    await UniTask.Delay(TimeSpan.FromSeconds(1));
                }
                if(!PrototypeApp.Current.InRoom)throw new InvalidOperationException(PrototypeApp.Current.Error);
                _ready=true;
            }
            catch(Exception e){Finish(e.Message);}
        }
        void Update()
        {
            if(_ending)return;
            if(Time.realtimeSinceStartup-_wallStart>(_fullRound?600:300)){Finish("Timed out");return;}
            var match=PrototypeMatch.Current;var player=PrototypePlayer.Local;
            if(!_ready||match==null||player==null)return;
            if(_host&&!_placed&&match.Players.Count==_expected)
            {
                foreach(var p in match.Players)
                {
                    int side=p.Snapshot.Value.Team==1?1:-1;float z=p.Snapshot.Value.Slot==0?0:2.5f;
                    var surface=match.Arena.Surfaces.Values.Single(s=>s.name=="PlatformBody_"+side);
                    for(float y=.1f;y<3;y+=.4f)for(float along=-.9f;along<=.9f;along+=.4f)
                        match.Paint(surface,new Vector3(side*13,y,z+along),Vector3.right*side,1,p.Snapshot.Value.Team,.8f,1);
                    foreach(float x in new[]{side*13.5f,side*12.5f})
                        foreach(float height in new[]{.15f,3.15f})
                            if(Physics.Raycast(new Vector3(x,height,z),Vector3.down,out var hit,.3f,PlayerMotorSimulation.WorldMask))
                            {var ground=hit.collider.GetComponent<PaintSurface>();if(ground!=null)match.Paint(ground,hit.point,hit.normal,1.2f,p.Snapshot.Value.Team,.8f,1);}
                    p.DiagnosticPlace(new Vector3(side*13.42f,.04f,z),side==1?270:90);
                }
                _placed=true;
            }
            if(_start==0&&player.Snapshot.Value.Revision>=2)
            {_start=player.Snapshot.Value.SimulatedAt;_life=player.Snapshot.Value.Revision;Debug.Log("[SHOOTER-SMOKE] Started "+Arg("-shooterRole","host"));}
            if(_start==0)return;
            double age=player.NetworkManager.ServerTime.Time-_start;var state=player.PresentedState;
            _modes.Add(state.Movement);_fire|=state.Firing;_respawn|=state.Revision>_life;_frames.Add(Time.unscaledDeltaTime*1000);
            _authorityModes.Add(player.Snapshot.Value.Movement);_peakPaintBytes=Math.Max(_peakPaintBytes,PaintSurface.AllocatedBytes);
            if(!_host)
            {
                _wallPredictionLead|=state.Movement==MovementMode.WallInk&&player.Snapshot.Value.Movement!=MovementMode.WallInk;
                _movePredictionLead|=player.PendingInputCount>0&&Vector3.Distance(state.Position,player.Snapshot.Value.Position)>.01f;
                if(_timeoutCheck&&age>8)_timeoutObserved|=player.Snapshot.Value.InputTimedOut;
                if(_timeoutObserved&&age>9.5)_timeoutRecovered|=!player.Snapshot.Value.InputTimedOut;
            }
            _maxPlayers=Math.Max(_maxPlayers,PrototypePlayer.ByOwner.Count);_peakPending=Math.Max(_peakPending,player.PendingInputCount);_maxCorrection=Mathf.Max(_maxCorrection,player.LastCorrectionDistance);
            _rtt=((Unity.Netcode.Transports.UTP.UnityTransport)player.NetworkManager.NetworkConfig.NetworkTransport).GetCurrentRtt(0);
            if(age>5&&!_captured){_captured=true;Capture("ink");}
            if(age>6.5&&!_capturedFire){_capturedFire=true;Capture("fire");}
            if(_host&&age>11&&!_killed){foreach(var p in match.Players)p.ReceiveDamage((byte)(p.Snapshot.Value.Team==1?2:1),200,Vector3.forward);_killed=true;}
            if(!_fullRound) { if(age>(_host?21:19))Finish(null);return; }
            if(_host&&age>20&&!_roundStarted){match.StartRound();_roundStarted=true;}
            if(_host&&age>25&&!_paintedRound)
            {
                foreach(var surface in match.Arena.Surfaces.Values)
                    foreach(var region in surface.WallRegions.Where(r=>r.Climbable))
                    {var matrix=region.Matrix(surface);match.Paint(surface,matrix.MultiplyPoint3x4(Vector3.zero),matrix.MultiplyVector(Vector3.up),1,1,.8f,1);}
                _paintedRound=true;
            }
            if(!_host&&age>35&&!_reconnecting&&!_reconnected)Reconnect().Forget();
            if(age>55&&!_ownershipRequested&&match.InitialSyncComplete){_ownershipRequested=true;player.VerifyPaintDiagnosticRpc(match.Arena.OwnershipHash());}
            _roundFinished|=match.State.Value.Phase==MatchPhase.Finished;
            double resetAt=23+GameplayConfig.Mode.MatchSeconds;
            if(_host&&age>resetAt&&!_roundReset){match.StartRound();_roundReset=true;}
            if(!_host&&match.State.Value.Round>=2)_roundReset=true;
            if(_roundReset&&age>resetAt+1&&!_resetOwnershipRequested){_resetOwnershipRequested=true;player.VerifyPaintDiagnosticRpc(match.Arena.OwnershipHash());}
            if(age>resetAt+(_host?7:5))Finish(null);
        }
        public static void ModifyInput(PrototypePlayer player,ref PlayerInputFrame input)
        {
            var s=_instance;if(s==null||!s._ready)return;
            input.Move=Vector2.zero;input.Fire=input.Swim=false;
            input.JumpSequence=player.PresentedState.ConsumedJump;
            if(s._start==0)return;
            double age=player.NetworkManager.ServerTime.Time-s._start;
            input.Look=new Vector2(player.PresentedState.Team==1?270:90,0);
            if(age<1)input.Swim=true;
            else if(age<4.5){input.Swim=true;if(player.PresentedState.Position.y<2.9f||!player.PresentedState.Grounded)input.Move=Vector2.up;}
            else if(age<5.5)input.Swim=true;
            else if(age<7)input.Fire=true;
            else if(age<9){input.JumpSequence=1;input.Move=Vector2.down;}
            else if(age<11){input.Look.x+=80;input.Fire=true;}
            if(s._timeoutCheck&&age>=7.5&&age<10){input.Move=Vector2.right;input.Fire=true;}
        }
        public static bool SuppressInputSend(PrototypePlayer player)
        {
            if(_instance==null||!_instance._timeoutCheck||_instance._start==0)return false;
            double age=player.NetworkManager.ServerTime.Time-_instance._start;
            return age>=8&&age<9.2;
        }
        async void Finish(string error)
        {
            if(_ending)return;_ending=true;
            var p=PrototypePlayer.Local;var arena=PrototypeArena.Current;
            bool pass=error==null&&_errors.Count==0&&_fire&&_respawn&&_maxPlayers==_expected&&_peakPaintBytes<=128L*1048576&&new[]{MovementMode.GroundInk,MovementMode.WallInk,MovementMode.Mantle,MovementMode.Air,MovementMode.Dead}.All(m=>_modes.Contains(m)&&_authorityModes.Contains(m));
            if(_fullRound)pass&=_roundFinished&&_roundReset&&_resetOwnershipRequested&&(_host||_reconnected)&&p!=null&&p.DiagnosticOwnershipVerified;
            if(!_host)pass&=_wallPredictionLead&&_movePredictionLead;
            if(_timeoutCheck&&!_host)pass&=_timeoutObserved&&_timeoutRecovered;
            _frames.Sort();
            string report="passed="+pass+"\nrole="+Arg("-shooterRole","host")+"\nerror="+error+"\nmodes="+string.Join(",",_modes)+"\nfire="+_fire+"\nrespawn="+_respawn+
                "\nauthorityModes="+string.Join(",",_authorityModes)+"\nmaxPlayers="+_maxPlayers+"\npeakPaintMiB="+_peakPaintBytes/1048576.0+"\nrttMs="+_rtt+"\npeakPending="+_peakPending+"\nmaxCorrection="+_maxCorrection+
                "\ncorrections="+(p!=null?p.CorrectionCount:0)+"\npending="+(p!=null?p.PendingInputCount:0)+"\np95FrameMs="+(_frames.Count>0?_frames[(int)((_frames.Count-1)*.95f)]:0)+
                "\nregionBytes="+(arena!=null?arena.RegionGrids().Values.Sum(g=>(long)g.SnapshotBytes):0)+"\nscoreArea="+(arena!=null?arena.TotalArea:0)+
                "\nfullRound="+_fullRound+"\nroundFinished="+_roundFinished+"\nroundReset="+_roundReset+"\nreconnected="+_reconnected+"\nownershipVerified="+(p!=null&&p.DiagnosticOwnershipVerified)+
                "\nwallPredictionLead="+_wallPredictionLead+"\nmovePredictionLead="+_movePredictionLead+"\ninputTimeout="+_timeoutObserved+"\ntimeoutRecovered="+_timeoutRecovered+"\nruntimeErrors="+string.Join(" | ",_errors);
            string output=Arg("-shooterOutput","Temp/ShooterSmoke.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));File.WriteAllText(output,report);Debug.Log("[SHOOTER-SMOKE] "+report);
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(pass?0:1);
#else
            Application.Quit(pass?0:1);
#endif
        }
        async UniTask Reconnect()
        {
            _reconnecting=true;_ready=false;
            try
            {
                await PrototypeApp.Current.Leave();
                await PrototypeApp.Current.Connect(false,"127.0.0.1",ushort.Parse(Arg("-shooterPort","18013")));
                if(!PrototypeApp.Current.InRoom)throw new InvalidOperationException(PrototypeApp.Current.Error);
                _reconnected=true;_ready=true;
            }
            catch(Exception e){Finish(e.Message);}
        }
        void OnLog(string message,string stack,LogType type)
        {if((type==LogType.Error||type==LogType.Exception)&&!stack.Contains("UnityEditor.Search")&&_errors.Count<12)_errors.Add(message);}
        void OnDestroy(){Application.logMessageReceived-=OnLog;if(_instance==this)_instance=null;}
        static void Capture(string stage)
        {
            var camera=Camera.main;if(camera==null||SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)return;
            var rt=new RenderTexture(1280,720,24);rt.Create();var prior=RenderTexture.active;
            var request=new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest{destination=rt};
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,request);RenderTexture.active=rt;
            var texture=new Texture2D(1280,720,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,1280,720),0,0);texture.Apply();
            File.WriteAllBytes(Path.ChangeExtension(Arg("-shooterOutput","Temp/ShooterSmoke.txt"),null)+"-"+stage+".png",texture.EncodeToPNG());
            RenderTexture.active=prior;rt.Release();Destroy(rt);Destroy(texture);
        }
    }
}
#endif
