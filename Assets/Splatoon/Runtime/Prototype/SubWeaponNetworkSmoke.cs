#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in two-process acceptance. Only activated by -subWeaponRole.</summary>
    public sealed class SubWeaponNetworkSmoke : MonoBehaviour
    {
        static SubWeaponNetworkSmoke instance;
        bool host,ready,ending,lateJoin,marked,misted,reset,disconnected,captured;
        int stage=-2,maxPlayers;float started;uint hash,paint;string failure;
        readonly List<string> errors=new();readonly HashSet<SubWeaponType> seen=new();
        readonly HashSet<int> ownUses=new(),remoteUses=new();
        readonly HashSet<SubWeaponType> ownFlights=new(),remoteFlights=new(),persistent=new();
        float anchorError;
        int diagnosticSecond=-1;
        double disconnectedAt;
        readonly HashSet<int> retriedStages=new(),inputTimeoutStages=new();
        readonly List<string> diagnostics=new(){"time,stage,owner,local,health,ink,action,phase,failure,needs_release,press,release,input_timeout,ack,life,hero_revision"};
        readonly List<AsyncOperationHandle<SubWeaponConfigAsset>> handles=new();
        readonly Dictionary<SubWeaponType,SubWeaponConfigAsset> assets=new();
        readonly Dictionary<ulong,uint> actions=new();
        readonly Dictionary<ulong,Vector3> anchors=new();
        static string Arg(string key,string fallback)=>HeroSelectionSmoke.Arg(key,fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if(instance!=null||!Environment.GetCommandLineArgs().Contains("-subWeaponRole"))return;
            var go=new GameObject("Subweapon network acceptance");DontDestroyOnLoad(go);instance=go.AddComponent<SubWeaponNetworkSmoke>();
        }
        void Start()=>Run().Forget();
        async UniTask Run()
        {
            host=Arg("-subWeaponRole","host")=="host";started=Time.realtimeSinceStartup;Application.logMessageReceived+=OnLog;
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                foreach(SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
                {
                    var h=Addressables.LoadAssetAsync<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));handles.Add(h);
                    while(!h.IsDone)await UniTask.Yield();if(h.Status!=AsyncOperationStatus.Succeeded)throw new Exception("Missing "+type);assets[type]=h.Result;
                }
                if(!host)await UniTask.Delay(TimeSpan.FromSeconds(8));
                await PrototypeApp.Current.Connect(host,"127.0.0.1",ushort.Parse(Arg("-subWeaponPort","18618")));
                if(!PrototypeApp.Current.InRoom)throw new Exception(PrototypeApp.Current.Error);
                ready=true;
            }
            catch(Exception e){Finish(e.ToString()).Forget();}
        }
        static int Stage(double now)=>now<20?-1:Math.Min(14,(int)((now-20)/6));
        static SubWeaponType TypeFor(int n)=>n<13?(SubWeaponType)n:n==13?SubWeaponType.PointSensor:SubWeaponType.ToxicMist;
        void Update()
        {
            if(ending)return;
            if(Time.realtimeSinceStartup-started>190){Finish("Timeout").Forget();return;}
            var match=PrototypeMatch.Current;var local=PrototypePlayer.Local;
            if(!ready||match==null||local==null)return;
            double now=local.NetworkManager.ServerTime.Time;int current=Stage(now);
            if((int)now!=diagnosticSecond)
            {
                diagnosticSecond=(int)now;
                foreach(var player in PrototypePlayer.ByOwner.Values)
                {
                    var snapshot=player.Snapshot.Value;
                    if(snapshot.InputTimedOut&&current>=0&&now<110)inputTimeoutStages.Add(current);
                    diagnostics.Add(FormattableString.Invariant($"{now:F3},{current},{player.PlayerId},{player==local},{snapshot.Health},{snapshot.Ink},{snapshot.SubAction},{snapshot.SubPhase},{snapshot.SubFailure},{snapshot.SubNeedsRelease},{snapshot.SubConsumedPress},{snapshot.SubConsumedRelease},{snapshot.InputTimedOut},{snapshot.AcknowledgedInput},{snapshot.Revision},{snapshot.HeroRevision}"));
                }
            }
            maxPlayers=Math.Max(maxPlayers,PrototypePlayer.ByOwner.Count);
            if(host&&now<15&&local.Snapshot.Value.HeroId!=2&&!local.HeroChangePending)local.RequestHeroChange(2,HeroSelectionOrigin.Warmup);
            if(current!=stage)
            {
                stage=current;
                if(stage>=0)
                {
                    // Both processes install the same deterministic sequence after admission.
                    // This is isolated harness setup, not a network configuration override API.
                    foreach(var hero in LubanConfigService.Current.Tables.TbHero.DataList)SubWeaponConfigService.Current.Set(hero.Id,assets[TypeFor(stage)]);
                    if(host)
                    {
                        match.SubWeapons.Clear();
                        foreach(var p in match.Players)
                        {
                            var s=p.Snapshot.Value;
                            if(!anchors.ContainsKey(p.PlayerId))anchors[p.PlayerId]=s.Position;
                            s.Ink=s.Health=100;s.ProtectedUntil=0;s.SubFailure=SubWeaponFailure.None;
                            s.MarkedUntilPink=s.MarkedUntilBlue=0;s.MistUntil=s.MistExposure=0;s.MistDrainRate=0;s.MistMoveRate=1;
                            p.Snapshot.Value=s;
                        }
                    }
                }
            }
            if(match.SubPresentation!=null)foreach(var s in match.SubPresentation.States)
            {
                seen.Add(s.Type);if(!host&&now<20&&s.Type==SubWeaponType.SplashWall)lateJoin=true;
            }
            if(match.SubPresentation!=null)
            {
                var presentation=match.SubPresentation;seen.UnionWith(presentation.ObservedTypes);
                foreach(var flight in presentation.ObservedFlightOwners)(flight.owner==local.PlayerId?ownFlights:remoteFlights).Add(flight.type);
                persistent.UnionWith(presentation.ObservedPersistent);anchorError=Math.Max(anchorError,presentation.MaximumAnchorError);
            }
            foreach(var p in PrototypePlayer.ByOwner.Values)
            {
                var s=p.Snapshot.Value;
                if(actions.TryGetValue(p.PlayerId,out uint previous)&&s.SubAction>previous&&stage>=0)
                { if(p==local)ownUses.Add(stage);else remoteUses.Add(stage); }
                actions[p.PlayerId]=s.SubAction;
            }
            // Place the host's authoritative status entities on the remote player after
            // they were created through E/input/network/simulation, so statuses cross the wire.
            if(host&&stage>=13)
            {
                var victim=match.Players.FirstOrDefault(p=>p!=local);
                if(victim!=null)foreach(var e in match.SubWeapons.Entities)
                    if(e.State.Owner==local.PlayerId&&!e.Removed&&e.State.Phase==SubEntityPhase.Active)
                        e.State.Position=victim.Snapshot.Value.Position+Vector3.up*.5f;
            }
            var state=local.Snapshot.Value;
            marked|=state.MarkedUntilPink>now||state.MarkedUntilBlue>now;
            misted|=state.MistUntil>now&&state.MistMoveRate<.95f&&state.Ink<90;
            if(now>112&&!captured)
            { captured=match.InitialSyncComplete;hash=match.Arena.OwnershipHash();paint=host?match.PaintSequence:match.AppliedPaintSequence;Capture(host?"network-host.png":"network-client.png"); }
            if(host&&now>114&&!reset){var s=match.State.Value;s.Phase=MatchPhase.Finished;match.State.Value=s;match.ReturnToRoom();reset=true;}
            if(!host&&now>116)reset=match.State.Value.Phase==MatchPhase.Practice&&match.AppliedPaintSequence==0&&match.SubPresentation.EntityIds.Count==0;
            if(host&&now>120&&maxPlayers>=2&&match.Players.Count==1&&!disconnected){disconnected=true;disconnectedAt=now;}
            // A lost disconnect datagram falls back to UTP's configured inactivity timeout (30 s by default).
            double disconnectDeadline=118+((UnityTransport)local.NetworkManager.NetworkConfig.NetworkTransport).DisconnectTimeoutMS/1000d+5;
            if(host?now>123&&disconnected||now>disconnectDeadline:now>118)Finish(null).Forget();
        }
        public static void ModifyInput(PrototypePlayer player,ref PlayerInputFrame input)
        {
            if(instance==null||!instance.ready||instance.ending)return;
            double now=player.NetworkManager.ServerTime.Time;int stage=Stage(now);double age=now-20-Math.Max(0,stage)*6;
            input.Move=Vector2.zero;input.Fire=input.Swim=false;input.CancelFire=input.CancelSub=false;
            input.Look=new Vector2(player.PresentedState.Team==1?0:180,70);
            bool retry=stage>=0&&age>=3.5&&age<5&&!instance.ownUses.Contains(stage);
            if(retry)instance.retriedStages.Add(stage);
            input.SubHeld=stage<0?instance.host&&now>=4&&now<5.5:now<110&&(age>=1&&age<2.5||retry);
        }
        void Capture(string name)
        {
            string dir=Path.GetDirectoryName(Path.GetFullPath(Arg("-subWeaponOutput","Reports/SubWeapons/network.txt")));Directory.CreateDirectory(dir);
            var camera=new GameObject("Subweapon capture camera").AddComponent<Camera>();camera.CopyFrom(Camera.main);camera.enabled=false;
            camera.transform.SetPositionAndRotation(Camera.main.transform.position,Camera.main.transform.rotation);
            camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing=false;
            var target=new RenderTexture(1280,720,24);target.Create();var previous=RenderTexture.active;var pixels=new Texture2D(1280,720,TextureFormat.RGB24,false);
            try
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest{destination=target});
                RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(dir,name),pixels.EncodeToPNG());
            }
            finally{RenderTexture.active=previous;target.Release();Destroy(camera.gameObject);Destroy(target);Destroy(pixels);}
        }
        void OnLog(string message,string stack,LogType type)
        {if((type==LogType.Error||type==LogType.Exception)&&errors.Count<15)errors.Add(message);}
        async UniTask Finish(string error)
        {
            if(ending)return;ending=true;failure=error;
            bool passed=error==null&&errors.Count==0&&anchorError<=.01f&&ownFlights.Count>=12&&remoteFlights.Count>=12&&persistent.Count>=5&&maxPlayers>=2&&seen.Count==13&&ownUses.Count>=13&&remoteUses.Count>=13&&captured&&paint>0&&reset&&(host?disconnected:lateJoin&&marked&&misted);
            string path=Arg("-subWeaponOutput","Reports/SubWeapons/network.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllLines(Path.ChangeExtension(path,"stages.csv"),diagnostics);
            File.WriteAllText(path,$"passed={passed}\nrole={(host?"host":"client")}\nerror={failure}\nmaxPlayers={maxPlayers}\nseen={string.Join(",",seen.OrderBy(x=>x))}\nownUses={string.Join(",",ownUses.OrderBy(x=>x))}\nremoteUses={string.Join(",",remoteUses.OrderBy(x=>x))}\nlateJoinWall={lateJoin}\nmarked={marked}\nmisted={misted}\nfinalPaintHash={hash}\nfinalPaintSequence={paint}\nroundReset={reset}\nremoteDisconnected={disconnected}\nerrors={string.Join(" | ",errors)}\nmaxAnchorError={anchorError}\nownFlights={string.Join(",",ownFlights.OrderBy(x=>x))}\nremoteFlights={string.Join(",",remoteFlights.OrderBy(x=>x))}\npersistent={string.Join(",",persistent.OrderBy(x=>x))}\nEnvironment=two independent processes on one PC; not two physical computers\n");
            File.AppendAllText(path,FormattableString.Invariant($"disconnectObservedAt={disconnectedAt:F3}\n"));
            File.AppendAllText(path,$"retriedStages={string.Join(",",retriedStages.OrderBy(x=>x))}\ninputTimeoutStages={string.Join(",",inputTimeoutStages.OrderBy(x=>x))}\n");
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
            foreach(var h in handles)if(h.IsValid())Addressables.Release(h);
            Application.Quit(passed?0:1);
        }
        void OnDestroy(){Application.logMessageReceived-=OnLog;if(instance==this)instance=null;}
    }
}
#endif
