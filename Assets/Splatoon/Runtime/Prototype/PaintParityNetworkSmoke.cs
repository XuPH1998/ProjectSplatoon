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
    /// <summary>CLI-only independent-player acceptance. Never active in ordinary rooms.</summary>
    public sealed class PaintParityNetworkSmoke : MonoBehaviour
    {
        static PaintParityNetworkSmoke instance;
        public static bool Active => instance != null;
        bool host,ready,ending,resync,reset,killed,sawDeath,respawned,captured;
        float wallStart; double nextSwitch; int maxPlayers,appliedSnapshots,lastHero; uint lastShot,lastLife;
        uint finalHash,finalSequence; readonly int[] shots=new int[8]; readonly List<string> errors=new();
        static string Arg(string key,string fallback)=>HeroSelectionSmoke.Arg(key,fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if(instance!=null||!Environment.GetCommandLineArgs().Contains("-paintParityRole"))return;
            var go=new GameObject("Paint parity network acceptance");DontDestroyOnLoad(go);instance=go.AddComponent<PaintParityNetworkSmoke>();
        }
        async UniTaskVoid Start()
        {
            host=Arg("-paintParityRole","host")=="host";wallStart=Time.realtimeSinceStartup;Application.logMessageReceived+=OnLog;
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                if(!host)await UniTask.Delay(TimeSpan.FromSeconds(6));
                for(int attempt=0;attempt<6;attempt++)
                {
                    await PrototypeApp.Current.Connect(host,"127.0.0.1",ushort.Parse(Arg("-paintParityPort","18417")));
                    if(PrototypeApp.Current.InRoom)break;
                    await UniTask.Delay(TimeSpan.FromSeconds(1));
                }
                if(!PrototypeApp.Current.InRoom)throw new Exception(PrototypeApp.Current.Error);
                ready=true;
            }
            catch(Exception e){Finish(e.ToString());}
        }
        void Update()
        {
            if(ending)return;
            if(Time.realtimeSinceStartup-wallStart>220){Finish("Timed out");return;}
            var match=PrototypeMatch.Current;var p=PrototypePlayer.Local;
            if(!ready||match==null||p==null)return;
            double age=p.NetworkManager.ServerTime.Time;var state=p.Snapshot.Value;
            maxPlayers=Math.Max(maxPlayers,PrototypePlayer.ByOwner.Count);
            if(state.HeroId!=lastHero)Debug.Log($"[PAINT-PARITY] Selected hero={state.HeroId} at={age:F2}");
            if(state.HeroId==lastHero&&state.Revision==lastLife&&state.ShotSequence>lastShot)shots[state.HeroId]+=(int)(state.ShotSequence-lastShot);
            lastShot=state.ShotSequence;lastHero=state.HeroId;lastLife=state.Revision;
            int desired=age<12?1:Math.Min(7,1+(int)((age-12)/12));
            if(age<96&&state.Health>0&&!p.HeroChangePending&&state.HeroId!=desired&&age>=nextSwitch)
            {p.RequestHeroChange(desired,HeroSelectionOrigin.Warmup);nextSwitch=age+1;}
            if(!host&&age>100&&!resync)
            {
                typeof(PrototypeMatch).GetMethod("RequestSnapshotRpc",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
                    .Invoke(match,new object[]{default(Unity.Netcode.RpcParams)});resync=true;
            }
            if(age>112&&!captured)
            {finalHash=match.Arena.OwnershipHash();finalSequence=match.AppliedPaintSequence;captured=match.InitialSyncComplete;}
            if(host&&age>116&&!killed)
            {foreach(var other in match.Players)other.ReceiveDamage((byte)(other.Snapshot.Value.Team==1?2:1),200,Vector3.forward);killed=true;}
            sawDeath|=state.Health<=0;respawned|=sawDeath&&state.Health>0&&state.HeroId==7;
            if(host&&age>127&&!reset){var m=match.State.Value;m.Phase=MatchPhase.Finished;match.State.Value=m;match.ReturnToRoom();reset=true;}
            if(!host&&age>128)reset=match.State.Value.Phase==MatchPhase.Practice&&match.AppliedPaintSequence==0;
            if(age>(host?146:139))Finish(null);
        }
        public static void ModifyInput(PrototypePlayer p,ref PlayerInputFrame input)
        {
            var probe=instance;if(probe==null||!probe.ready||probe.ending)return;
            double age=p.NetworkManager.ServerTime.Time;
            double phase=age<12?age:(age-12)%12;
            input.Move=Vector2.zero;input.Look=new Vector2((p.PresentedState.Team==1?0:180)+22,8);
            bool spinner=WeaponSimulation.IsSplatling(GameplayConfig.GetWeapon(p.PresentedState.HeroId));
            // Hero changes deliberately require a release. Give both the immediate Host
            // and the delayed Client a full release interval before starting the next action.
            input.Fire=age<96&&!p.HeroChangePending&&phase>=1&&(spinner?phase<5:phase<8);
            input.Swim=age<96&&phase>9;
        }
        void OnLog(string message,string stack,LogType type)
        {
            if(message.StartsWith("[INK] Snapshot applied"))appliedSnapshots++;
            if((type==LogType.Error||type==LogType.Exception)&&errors.Count<12)errors.Add(message);
        }
        async void Finish(string error)
        {
            if(ending)return;ending=true;
            bool passed=error==null&&errors.Count==0&&maxPlayers>=2&&shots.Skip(1).All(n=>n>0)&&captured&&respawned&&reset&&(host||resync&&appliedSnapshots>=2);
            string report=$"passed={passed}\nrole={(host?"host":"client")}\nerror={error}\nshots={string.Join(",",shots.Skip(1))}\nmaxPlayers={maxPlayers}\nfinalPaintHash={finalHash}\nfinalPaintSequence={finalSequence}\ninitialSyncAndCapture={captured}\nresyncRequested={resync}\nappliedSnapshots={appliedSnapshots}\nrespawnRetained={respawned}\nroundReset={reset}\n";
            foreach(NetworkTrafficKind kind in Enum.GetValues(typeof(NetworkTrafficKind)))if(kind!=NetworkTrafficKind.Count)report+=$"{kind}PayloadBytes={NetworkTrafficCounters.GetBytes(kind)}\n";
            var field=typeof(PrototypeApp).GetField("_signature",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            var signature=field?.GetValue(PrototypeApp.Current) as byte[];
            report+="contentSignature="+(signature==null?"unavailable":BitConverter.ToString(signature).Replace("-","").ToLowerInvariant())+"\n";
            report+="runtimeErrors="+string.Join(" | ",errors)+"\nEnvironment=two independent processes on one PC; not physical two-machine/device acceptance\n";
            string path=Arg("-paintParityOutput","Reports/PaintParity1130/network.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));File.WriteAllText(path,report);
            Debug.Log("[PAINT-PARITY] "+report);
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
            Application.Quit(passed?0:1);
        }
        void OnDestroy(){Application.logMessageReceived-=OnLog;if(instance==this)instance=null;}
    }
}
#endif
