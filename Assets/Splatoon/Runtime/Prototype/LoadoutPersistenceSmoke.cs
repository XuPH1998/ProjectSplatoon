#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Explicit process-restart fixture; never enabled in ordinary rooms.
    public sealed class LoadoutPersistenceSmoke:MonoBehaviour
    {
        [Serializable] public sealed class Entry { public int Hero,Sub,Special;public bool HadSub,HadSpecial; }
        [Serializable] public sealed class Backup { public Entry[] Entries; }
        string root,phase;bool ending;float started;
        static string Arg(string key,string fallback)=>HeroSelectionSmoke.Arg(key,fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]static void Initialize()
        {
            if(!Environment.GetCommandLineArgs().Contains("-loadoutPhase"))return;
            var go=new GameObject("Loadout process persistence acceptance");DontDestroyOnLoad(go);go.AddComponent<LoadoutPersistenceSmoke>();
        }
        void Start(){root=Arg("-loadoutRoot","");phase=Arg("-loadoutPhase","");started=Time.realtimeSinceStartup;Run().Forget();}
        void Update(){if(!ending&&Time.realtimeSinceStartup-started>75)Finish("Timeout").Forget();}
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        async UniTask Wait(Func<bool> predicate)=>await UniTask.WaitUntil(predicate).Timeout(TimeSpan.FromSeconds(35));
        async UniTask Run()
        {
            try
            {
                Require(root.Length>0,"Missing isolated fixture directory");Directory.CreateDirectory(root);
                await Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
                string backupPath=Path.Combine(root,"preferences-original.json");
                if(phase=="restore")
                {
                    var backup=JsonUtility.FromJson<Backup>(File.ReadAllText(backupPath));
                    foreach(var e in backup.Entries)
                    {
                        string sub="Loadout.Sub."+e.Hero,special="Loadout.Special."+e.Hero;
                        if(e.HadSub)PlayerPrefs.SetInt(sub,e.Sub);else PlayerPrefs.DeleteKey(sub);
                        if(e.HadSpecial)PlayerPrefs.SetInt(special,e.Special);else PlayerPrefs.DeleteKey(special);
                    }
                    PlayerPrefs.Save();await Finish(null);return;
                }
                int first=GameplayConfig.Mode.HeroId,second=first==2?1:2;
                var primary=new PlayerLoadout(first,8,4);var alternate=new PlayerLoadout(second,3,5);
                if(phase=="seed")
                {
                    Require(!File.Exists(backupPath),"Refusing to overwrite original preferences backup");
                    var backup=new Backup{Entries=new[]{first,second}.Select(hero=>new Entry{Hero=hero,HadSub=PlayerPrefs.HasKey("Loadout.Sub."+hero),HadSpecial=PlayerPrefs.HasKey("Loadout.Special."+hero),Sub=PlayerPrefs.GetInt("Loadout.Sub."+hero),Special=PlayerPrefs.GetInt("Loadout.Special."+hero)}).ToArray()};
                    File.WriteAllText(backupPath,JsonUtility.ToJson(backup,true));primary.Save();alternate.Save();await Finish(null);return;
                }
                Require(PlayerLoadout.Remembered(first).Matches(primary)&&PlayerLoadout.Remembered(second).Matches(alternate),"New process did not read both per-hero saved loadouts");
                bool host=phase=="host";var app=PrototypeApp.Current;
                await app.Connect(host,"127.0.0.1",ushort.Parse(Arg("-loadoutPort","20670")));
                Require(app.InRoom,app.Error);await Wait(()=>PrototypeMatch.Current!=null&&PrototypePlayer.Local!=null);
                var match=PrototypeMatch.Current;
                if(host)
                {
                    var state=match.State.Value;state.Phase=MatchPhase.Playing;state.EndsAt=match.NetworkManager.ServerTime.Time+300;match.State.Value=state;
                    File.WriteAllText(Path.Combine(root,"host-playing"),"ready");
                    await Wait(()=>match.Players.Any(p=>p!=PrototypePlayer.Local&&PlayerLoadout.From(p.Snapshot.Value).Matches(primary)&&p.Snapshot.Value.HeroRevision>0));
                    File.WriteAllText(Path.Combine(root,"host-saw-initial"),"restored through authority");
                    await Wait(()=>match.Players.Any(p=>p!=PrototypePlayer.Local&&PlayerLoadout.From(p.Snapshot.Value).Matches(alternate)));
                    File.WriteAllText(Path.Combine(root,"host-saw-alternate"),"hero switch retained its saved sub and special");
                    await Wait(()=>File.Exists(Path.Combine(root,"client-verified")));
                    await Finish(null);return;
                }
                await Wait(()=>match.InitialSyncComplete&&!PrototypePlayer.Local.HeroChangePending&&PlayerLoadout.From(PrototypePlayer.Local.Snapshot.Value).Matches(primary));
                Require(match.State.Value.Phase==MatchPhase.Playing,"Must join a match already playing");
                Require(PrototypePlayer.Local.Snapshot.Value.HeroRevision>0,"Must apply saved loadout, not only see initial defaults");
                await Wait(()=>File.Exists(Path.Combine(root,"host-saw-initial")));
                PrototypePlayer.Local.RequestHeroChange(second,HeroSelectionOrigin.SpawnArea);
                await Wait(()=>!PrototypePlayer.Local.HeroChangePending&&PlayerLoadout.From(PrototypePlayer.Local.Snapshot.Value).Matches(alternate));
                await Wait(()=>File.Exists(Path.Combine(root,"host-saw-alternate")));
                File.WriteAllText(Path.Combine(root,"client-verified"),"restart + playing late join + per-hero switch");
                await Finish(null);
            }
            catch(Exception e){await Finish(e.ToString());}
        }
        async UniTask Finish(string error)
        {
            if(ending)return;ending=true;
            File.WriteAllText(Path.Combine(root,phase+".txt"),$"passed={error==null}\nphase={phase}\nerror={error}\n");
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
            Application.Quit(error==null?0:1);
        }
    }
}
#endif
