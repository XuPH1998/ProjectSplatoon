#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Explicit CLI-only acceptance in independent development players.</summary>
    public sealed class ExplosherNetworkSmoke : MonoBehaviour
    {
        static ExplosherNetworkSmoke instance;
        public static bool Active => instance != null;
        bool host,ready,ending,killed,reset,sawDeath,respawned,synced,resyncRequested;
        float wallStart; uint shots,life; long emitted; int players,visible,restored;
        float correction; double nextFixture; uint finalHash,finalSequence; bool capturedPaint,switchedWithFlight; int appliedSnapshots;
        readonly List<string> errors=new();
        static string Arg(string key,string fallback)=>HeroSelectionSmoke.Arg(key,fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if(instance!=null||!Environment.GetCommandLineArgs().Contains("-explosherRole"))return;
            var go=new GameObject("Explosher network acceptance");DontDestroyOnLoad(go);instance=go.AddComponent<ExplosherNetworkSmoke>();
        }
        async UniTaskVoid Start()
        {
            host=Arg("-explosherRole","host")=="host";wallStart=Time.realtimeSinceStartup;
            Application.logMessageReceived+=OnLog;
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                string gate=Arg("-explosherGate","");
                if(!host&&gate.Length>0)await UniTask.WaitUntil(()=>File.Exists(gate));
                await PrototypeApp.Current.Connect(host,"127.0.0.1",ushort.Parse(Arg("-explosherPort","18337")));
                if(!PrototypeApp.Current.InRoom)throw new Exception(PrototypeApp.Current.Error);ready=true;
            }
            catch(Exception e){Finish(e.ToString());}
        }
        void Update()
        {
            if(ending)return;
            if(Time.realtimeSinceStartup-wallStart>180){Finish("Timed out");return;}
            var match=PrototypeMatch.Current;var player=PrototypePlayer.Local;
            if(!ready||match==null||player==null)return;
            double age=player.NetworkManager.ServerTime.Time;var state=player.Snapshot.Value;
            if(life==0)life=state.Revision;
            int desired=host&&age>=12&&age<13?1:3;
            if(state.HeroId!=desired&&!player.HeroChangePending&&state.Health>0)
            {
                if(desired==1)switchedWithFlight|=match.Projectiles.LiveShots().Any(s=>s.HeroId==3);
                player.RequestHeroChange(desired,HeroSelectionOrigin.Warmup);
            }
            if(state.ShotSequence>shots)emitted+=state.ShotSequence-shots;shots=state.ShotSequence;
            players=Math.Max(players,PrototypePlayer.ByOwner.Count);correction=Mathf.Max(correction,player.LastCorrectionDistance);
            if(InkPresentation.Current!=null)
            {visible=Math.Max(visible,InkPresentation.Current.ActiveShots);restored=Math.Max(restored,InkPresentation.Current.ExplosherRestoredCount);}
            synced|=match.InitialSyncComplete&&!host;
            if(host&&state.HeroId==3&&age>4&&age<16&&age>=nextFixture)
            {
                // A high, isolated flight deliberately spans the late-join transfer.
                var fixture=state;fixture.Position+=Vector3.up*60;fixture.Pitch=0;fixture.Yaw=0;
                fixture.LastShotSpread=fixture.LastShotVerticalSpread=0;fixture.ShotSequence+=10000;
                match.Projectiles.Spawn(player,fixture,age,match.State.Value.Round);nextFixture=age+.8;
                string gate=Arg("-explosherGate","");if(gate.Length>0&&!File.Exists(gate))File.WriteAllText(gate,"Host has active Explosher flight and prior paint.");
            }
            if(!host&&age>18&&!resyncRequested)
            {
                // Invoke the normal reliable snapshot request; no synthetic paint state.
                typeof(PrototypeMatch).GetMethod("RequestSnapshotRpc",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
                    .Invoke(match,new object[]{default(Unity.Netcode.RpcParams)});resyncRequested=true;
            }
            sawDeath|=state.Health<=0;respawned|=sawDeath&&state.Health>0&&state.HeroId==3&&state.Revision>life;
            if(host&&age>25&&!killed)
            {foreach(var p in match.Players)p.ReceiveDamage((byte)(p.Snapshot.Value.Team==1?2:1),200,Vector3.forward);killed=true;}
            if(host&&age>39&&!reset)
            {var m=match.State.Value;m.Phase=MatchPhase.Finished;match.State.Value=m;match.ReturnToRoom();reset=true;}
            if(!host&&match.State.Value.Round>0)reset=true;
            if(age>55&&!capturedPaint)
            {finalHash=match.Arena.OwnershipHash();finalSequence=match.AppliedPaintSequence;capturedPaint=match.InitialSyncComplete;}
            if(age>(host?67:59))Finish(null);
        }
        public static void ModifyInput(PrototypePlayer player,ref PlayerInputFrame input)
        {
            if(instance==null||!instance.ready||instance.ending)return;
            double age=player.NetworkManager.ServerTime.Time;
            input.Move=Vector2.zero;input.Swim=age%12>=7.8;
            input.Look=new Vector2((player.PresentedState.Team==1?0:180)+18,8);
            input.Fire=age<48&&!input.Swim&&player.PresentedState.HeroId==3&&!player.HeroChangePending;
        }
        void OnLog(string message,string stack,LogType type)
        {
            if(message.StartsWith("[INK] Snapshot applied"))appliedSnapshots++;
            if((type==LogType.Error||type==LogType.Exception)&&errors.Count<12)errors.Add(message);
        }
        async void Finish(string error)
        {
            if(ending)return;ending=true;
            // Stop at 48 s to compare a settled paint grid. Twelve owner shots still
            // exercise multiple ink/recovery cycles despite late join and forced death.
            bool passed=error==null&&errors.Count==0&&players>=2&&emitted>=12&&visible>0&&respawned&&reset&&capturedPaint&&
                (host?switchedWithFlight:(synced&&restored>0&&resyncRequested&&appliedSnapshots>=2));
            string report=$"passed={passed}\nrole={(host?"host":"client")}\nerror={error}\nmaxPlayers={players}\nlocalShots={emitted}\npeakVisible={visible}\nrestoredFlights={restored}\ninitialPaintSync={synced}\nresyncRequested={resyncRequested}\nrespawnRetained={respawned}\nroundReset={reset}\nmaxCorrection={correction}\n";
            foreach(NetworkTrafficKind kind in Enum.GetValues(typeof(NetworkTrafficKind)))if(kind!=NetworkTrafficKind.Count)report+=$"{kind}PayloadBytes={NetworkTrafficCounters.GetBytes(kind)}\n";
            report+="runtimeErrors="+string.Join(" | ",errors)+"\nTwo independent processes on one PC; not physical two-machine or device acceptance.\n";
            report+=$"finalPaintHash={finalHash}\nfinalPaintSequence={finalSequence}\nfinalPaintSyncComplete={capturedPaint}\n";
            report+=$"appliedSnapshots={appliedSnapshots}\nchangedHeroWithFlight={switchedWithFlight}\n";
            string path=Arg("-explosherOutput","Reports/Explosher/network.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));File.WriteAllText(path,report);
            Debug.Log("[EXPLOSHER-NETWORK] "+report);
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(passed?0:1);
#else
            Application.Quit(passed?0:1);
#endif
        }
        void OnDestroy(){Application.logMessageReceived-=OnLog;if(instance==this)instance=null;}
    }
}
#endif
