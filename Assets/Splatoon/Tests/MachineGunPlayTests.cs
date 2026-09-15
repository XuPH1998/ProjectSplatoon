#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class MachineGunPlayTests
    {
        const string Output="Reports/CombatGirls/MachineGunGirl/PlayMode";
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
        static IEnumerator Wait(Func<bool> ready,string message)
        {
            double end=Time.realtimeSinceStartupAsDouble+35;
            while(!ready()&&Time.realtimeSinceStartupAsDouble<end)yield return null;
            Assert.That(ready(),Is.True,message);
        }
        [UnityTest] public IEnumerator RealHostMachineGunLifecycleAndFourPlayerVolley()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static void Input(PrototypePlayer player,PlayerInputFrame input)
        {
            ((SortedDictionary<uint,PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs",Flags).GetValue(player)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput",Flags).SetValue(player,input);
            typeof(PrototypePlayer).GetField("_lastReceivedAt",Flags).SetValue(player,player.NetworkManager.ServerTime.Time);
        }
        static void Present(PrototypePlayer player,double now)
        {
            var state=player.Snapshot.Value;var view=player.CharacterView;
            player.Visual.localRotation=Quaternion.Euler(0,state.BodyYaw,0);
            view.Present(state,1f/60,now);view.Animator.Update(1f/60);view.ApplyAim();
            player.SwimBody?.Present(state,Vector3.zero,Quaternion.identity);
        }
        static void Capture(Camera camera,PrototypePlayer player,string name)
        {
            camera.transform.position=player.transform.position+new Vector3(2.6f,1.55f,2.2f);
            camera.transform.LookAt(player.transform.position+new Vector3(0,1.05f,.28f));
            var rt=new RenderTexture(960,960,24);var previous=RenderTexture.active;
            var texture=new Texture2D(960,960,TextureFormat.RGB24,false);
            try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,960,960),0,0);texture.Apply();File.WriteAllBytes(Output+"/"+name+".png",texture.EncodeToPNG());}
            finally{camera.targetTexture=null;RenderTexture.active=previous;UnityEngine.Object.Destroy(rt);UnityEngine.Object.Destroy(texture);}
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            Assert.That(SystemInfo.graphicsDeviceType,Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready,"Boot configuration and addressables");
            var app=PrototypeApp.Current;ushort port;
            using(var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))port=(ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true,"127.0.0.1",port).ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null,"Host spawn");
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;
            host.RequestHeroChange(6,HeroSelectionOrigin.Warmup);
            yield return Wait(()=>!host.HeroChangePending&&host.Snapshot.Value.HeroId==6,"Select ID 6 through real request");
            yield return null;
            Assert.That(app.Heroes.All.Count(),Is.EqualTo(6));Assert.That(app.Heroes.AssetCount,Is.EqualTo(12));
            Assert.That(host.CharacterView.Profile.Splatling,Is.True);Assert.That(host.SwimBody.Profile,Is.Not.Null);
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var players=new List<PrototypePlayer>{host};
            for(int i=0;i<3;i++){var bot=match.AddTestBot(prefab);Assert.That(bot,Is.Not.Null);players.Add(bot);Assert.That(bot.Snapshot.Value.HeroId,Is.EqualTo(6));}
            app.CaptureMouse(false);match.enabled=false;
            foreach(var p in players)p.enabled=false;
            var camera=new GameObject("MachineGun validation camera").AddComponent<Camera>();camera.CopyFrom(Camera.main);camera.enabled=false;camera.aspect=1;camera.fieldOfView=38;
            var rows=new StringBuilder("tick,charge,freeInk,reservedInk,remaining,shotSequence,phase\n");
            double start=app.Manager.ServerTime.Time+1;var w=GameplayConfig.GetHero(6);
            foreach(var p in players)
            {
                var s=p.Snapshot.Value;
                Input(p,new PlayerInputFrame{Revision=s.Revision,HeroRevision=s.HeroRevision,Look=new Vector2(s.Yaw,0)});
                p.Simulate(1f/60,start-1/60.0,MatchPhase.Practice);
            }
            match.Projectiles.Clear();var timings=new List<double>();int peak=0;uint sequence=100;
            var emitted=new Dictionary<uint,InkShot>();
            for(int tick=0;tick<=415;tick++)
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();double now=start+tick/60.0;
                foreach(var p in players)
                {
                    var s=p.Snapshot.Value;
                    Input(p,new PlayerInputFrame{Sequence=++sequence,Revision=s.Revision,HeroRevision=s.HeroRevision,FireSequence=1,Fire=tick<150,Look=new Vector2(s.Yaw,0)});
                    p.Simulate(1f/60,now,MatchPhase.Practice);Present(p,now);
                }
                peak=Math.Max(peak,match.Projectiles.ActiveCount);match.Projectiles.Simulate(now);
                foreach(var shot in match.Projectiles.Spawned)emitted[shot.Id]=shot;
                timings.Add(watch.Elapsed.TotalMilliseconds);
                var state=host.Snapshot.Value;
                rows.AppendLine($"{tick},{state.SplatlingCharge:R},{state.Ink:R},{state.SplatlingReservedInk:R},{state.SplatlingRemaining},{state.ShotSequence},{state.WeaponPhase}");
                if(tick==120||tick==160||tick==400||tick==413)Capture(camera,host,"phase-"+tick);
                if(tick%30==0)yield return null;
            }
            File.WriteAllText(Output+"/authority-timeline.csv",rows.ToString());
            Assert.That(emitted.Count,Is.EqualTo(264),"Four authoritative magazines, 66 rounds each");
            foreach(var p in players){Assert.That(p.Snapshot.Value.ShotSequence,Is.EqualTo(66));Assert.That(p.Snapshot.Value.Ink,Is.EqualTo(65).Within(.002));Assert.That(p.Snapshot.Value.SplatlingReservedInk,Is.Zero);}
            Assert.That(emitted.Values.All(s=>s.Charge==1),Is.True);
            foreach(float pitch in new[]{-65f,-45f,0f,35f,75f})
            {
                var s=host.Snapshot.Value;s.Pitch=pitch;host.Snapshot.Value=s;
                for(int i=0;i<15;i++)Present(host,start+7+i/60.0);
                var view=host.CharacterView;
                Assert.That(Vector3.Distance(view.Animator.GetBoneTransform(HumanBodyBones.LeftHand).position,view.LeftGrip.position),Is.LessThan(.045),"Support grip at pitch "+pitch);
                Capture(camera,host,"aim-"+pitch);
            }
            // Entering ink cancels even with the firing trigger still held.
            var active=host.Snapshot.Value;active.SplatlingReservedInk=10;active.Ink=55;active.SplatlingRemaining=20;active.WeaponPhase=WeaponPhase.Firing;active.AttackNeedsRelease=false;active.SwimWasHeld=false;
            host.Snapshot.Value=active;
            Input(host,new PlayerInputFrame{Revision=active.Revision,HeroRevision=active.HeroRevision,Fire=true,FireSequence=2,Swim=true,Look=new Vector2(active.Yaw,0)});
            host.Simulate(1f/60,start+8,MatchPhase.Practice);
            Assert.That(host.Snapshot.Value.SplatlingRemaining,Is.Zero);Assert.That(host.Snapshot.Value.SplatlingReservedInk,Is.Zero);
            Assert.That(host.Snapshot.Value.Swimming,Is.True);Present(host,start+8);Capture(camera,host,"swim-cancel");
            var dead=host.Snapshot.Value;dead.ProtectedUntil=0;host.Snapshot.Value=dead;uint life=dead.Revision;
            host.ReceiveDamage((byte)(3-dead.Team),200,Vector3.forward);
            Assert.That(host.Snapshot.Value.Health,Is.LessThanOrEqualTo(0));Assert.That(host.Snapshot.Value.Swimming,Is.False);
            Present(host,host.Snapshot.Value.DiedAt+1);Capture(camera,host,"death");
            host.Simulate(1f/60,host.Snapshot.Value.RespawnsAt+.01,MatchPhase.Practice);Present(host,host.Snapshot.Value.SimulatedAt);
            Assert.That(host.Snapshot.Value.Revision,Is.GreaterThan(life));Assert.That(host.Snapshot.Value.HeroId,Is.EqualTo(6));
            Assert.That(host.Snapshot.Value.SplatlingRemaining,Is.Zero);Assert.That(host.CharacterView.Animator.enabled,Is.True);
            Assert.That(host.CharacterView.Weapon.parent,Is.SameAs(host.CharacterView.WeaponSocket));Capture(camera,host,"respawn");
            timings.Sort();
            File.WriteAllText(Output+"/results.txt",$"PASS: GPU URP host boot, sixth hero selection/preload, 3 bots inherit ID6, four magazines = 264 unique rounds, total 35 ink each, full damage identity, five aim angles (-65 to 75) and left grip, swim cancel, swim death, respawn.\nPeak active projectiles={peak}\nSynchronous four-player simulation P95 ms={timings[(int)(timings.Count*.95)]:F3} (not render frame time)\nGPU={SystemInfo.graphicsDeviceName}\nResolution=960x960\n");
            yield return CompareFourModels(app, camera, players);
            UnityEngine.Object.Destroy(camera.gameObject);
            yield return app.Leave().ToCoroutine();
        }
        static IEnumerator CompareFourModels(PrototypeApp app,Camera camera,List<PrototypePlayer> players)
        {
            var main=Camera.main;bool wasEnabled=main!=null&&main.enabled;if(main!=null)main.enabled=false;
            foreach(var p in players)p.Visual.gameObject.SetActive(false);
            var rt=new RenderTexture(960,960,24);camera.targetTexture=rt;camera.enabled=true;
            var csv=new StringBuilder("hero,frames,p50FrameMs,p95FrameMs,medianDrawCalls,medianBatches,allocatedMiB\n");
            var roots=new List<GameObject>();var binders=new List<HeroViewBinder>();
            try
            {
                foreach(int hero in new[]{1,6})
                {
                    var center=new Vector3(2000,1000,2000);
                    for(int i=0;i<4;i++)
                    {
                        var root=new GameObject("Four model performance "+i);root.layer=8;root.transform.position=center+Vector3.right*(i-1.5f)*1.3f;
                        roots.Add(root);var binder=new HeroViewBinder(root.transform);binder.Apply(app.Heroes.Get(hero));binders.Add(binder);
                    }
                    camera.transform.position=center+new Vector3(0,2,9);camera.transform.LookAt(center+Vector3.up);camera.fieldOfView=45;
                    var frameTimes=new List<float>();var draws=new List<int>();var batches=new List<int>();double started=Time.timeAsDouble;
                    for(int frame=0;frame<160;frame++)
                    {
                        foreach(var binder in binders)
                        {
                            var state=new PlayerSnapshot{HeroId=hero,Health=100,Ink=65,Grounded=true,Team=1,Revision=1,Firing=true,FireStartedAt=started,
                                WeaponPhase=WeaponPhase.Firing,SplatlingRemaining=hero==6?66:0};
                            binder.View.Present(state,Time.unscaledDeltaTime,Time.timeAsDouble);binder.View.Animator.Update(Time.unscaledDeltaTime);binder.View.ApplyAim();
                        }
                        yield return null;
                        if(frame>=40){frameTimes.Add(Time.unscaledDeltaTime*1000);draws.Add(UnityEditor.UnityStats.drawCalls);batches.Add(UnityEditor.UnityStats.batches);}
                    }
                    frameTimes.Sort();draws.Sort();batches.Sort();
                    csv.AppendLine($"{hero},120,{frameTimes[60]:F3},{frameTimes[114]:F3},{draws[60]},{batches[60]},{UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()/1048576.0:F2}");
                    foreach(var binder in binders)binder.Dispose();binders.Clear();foreach(var root in roots)UnityEngine.Object.Destroy(root);roots.Clear();
                    yield return null;yield return null;
                }
                File.WriteAllText(Output+"/four-model-performance.csv",csv.ToString());
                File.WriteAllText(Output+"/performance-conditions.txt","Same GPU/editor/URP settings, 960x960 RenderTexture, one active camera, four firing character/weapon assemblies, fixed framing, 40 warmup + 120 sampled rendered frames per hero. Entire six-hero catalog stays loaded. Includes editor overhead; excludes network transport and projectile simulation. Authority volley timing and projectile peak are recorded separately in results.txt. Release/target-device performance remains unmeasured.");
            }
            finally
            {
                foreach(var binder in binders)binder.Dispose();foreach(var root in roots)UnityEngine.Object.Destroy(root);
                camera.enabled=false;camera.targetTexture=null;UnityEngine.Object.Destroy(rt);
                if(main!=null)main.enabled=wasEnabled;
                foreach(var p in players)if(p!=null)p.Visual.gameObject.SetActive(true);
            }
        }
    }
}
#endif
