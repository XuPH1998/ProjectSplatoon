#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Splatoon.Networking;
using System.Text;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class SubWeaponVisualPlayTests
    {
        const string Output = "Reports/SubWeapons/VisualFix";
        static IEnumerator Wait(Func<bool> ready)
        { double until=Time.realtimeSinceStartupAsDouble+45;while(!ready()&&Time.realtimeSinceStartupAsDouble<until)yield return null;Assert.That(ready(),Is.True); }

        sealed class Recording : IDisposable
        {
            readonly Process process;
            readonly RenderTexture target;
            readonly Texture2D pixels;
            readonly Camera camera;
            readonly StringBuilder errors=new();
            public int Frames { get; private set; }
            public Recording(Camera c,int fps,string file)
            {
                camera=c;target=new RenderTexture(640,360,24);target.Create();pixels=new Texture2D(640,360,TextureFormat.RGB24,false);
                if(!File.Exists("Temp/SubWeaponsFix/ffmpeg-path.txt"))return;
                var encoder=File.ReadAllText("Temp/SubWeaponsFix/ffmpeg-path.txt").Trim();
                process=new Process{StartInfo=new ProcessStartInfo(encoder,$"-hide_banner -loglevel error -y -f rawvideo -pixel_format rgb24 -video_size 640x360 -framerate {fps} -i pipe:0 -vf vflip -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p \"{Path.GetFullPath(file)}\"")
                    {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true}};
                process.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)errors.AppendLine(e.Data);};process.Start();process.BeginErrorReadLine();
            }
            public void Frame(string screenshot=null)
            {
                var old=RenderTexture.active;
                try
                {
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest{destination=target});
                    RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,640,360),0,0);pixels.Apply();
                    if(process!=null){var bytes=pixels.GetRawTextureData<byte>().ToArray();process.StandardInput.BaseStream.Write(bytes,0,bytes.Length);}
                    if(screenshot!=null)File.WriteAllBytes(screenshot,pixels.EncodeToPNG());Frames++;
                }
                finally{RenderTexture.active=old;}
            }
            public void Dispose()
            {
                if(process!=null){process.StandardInput.Close();if(!process.WaitForExit(30000)){process.Kill();throw new Exception("Video encoder timeout");}int code=process.ExitCode;process.Dispose();Assert.That(code,Is.Zero,errors.ToString());}
                target.Release();Object.Destroy(target);Object.Destroy(pixels);
            }
        }

        [UnityTest]
        public IEnumerator PreviewDeploymentAndRenderedLifecycleAtThirtySixtyAndOneTwenty()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            // Captured locals must be allocated after EnterPlayMode's domain reload.
            yield return RunAcceptance();
            yield return new ExitPlayMode();
        }
        static IEnumerator RunAcceptance()
        {
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();Assert.That(app.InRoom,Is.True,app.Error);
            yield return Wait(()=>PrototypeMatch.Current!=null&&PrototypePlayer.Local!=null);
            app=PrototypeApp.Current;
            Assert.That(app,Is.Not.Null,"Current app after connection");
            var match=PrototypeMatch.Current;
            Assert.That(match,Is.Not.Null,"Current match after connection");
            var host=PrototypePlayer.Local;
            Assert.That(host,Is.Not.Null,"Local player after connection");
            app.CaptureMouse(false);
            match.enabled=false;
            host.enabled=false;
            Assert.That(match.SubPresentation,Is.Not.Null,"Presentation initialized on network spawn");
            match.SubPresentation.enabled=false;
            var original=SubWeaponConfigService.Current.Source(host.Snapshot.Value.HeroId);
            var ownerRenderers=host.GetComponentsInChildren<Renderer>(true);
            var ownerVisibility=ownerRenderers.Select(r=>r.enabled).ToArray();
            var fixture=GameObject.CreatePrimitive(PrimitiveType.Cube);fixture.name="Subweapon visual acceptance floor";
            Vector3 origin=new(300,30,300);fixture.transform.position=origin+Vector3.down*.5f;fixture.transform.localScale=new Vector3(80,1,80);
            fixture.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(SubWeaponDefaults.Root+"Dark.mat");
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.name="Subweapon placement surface fixture";wall.SetActive(false);
            wall.GetComponent<Renderer>().sharedMaterial=fixture.GetComponent<Renderer>().sharedMaterial;
            var camera=new GameObject("Subweapon acceptance camera").AddComponent<Camera>();camera.enabled=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.15f,.19f);camera.fieldOfView=50;
            camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing=false;
            Directory.CreateDirectory(Output);
            var results=new List<string>{"type,fps,preview_error_m,max_anchor_error_m,frames,flight_observed,persistent_observed,preview_kind"};
            var surfaces=new List<string>{"type,surface,error_m,normal_error_deg"};
            var points=new List<Vector3>();var aim=new TpsAimSolver();double now=match.NetworkManager.ServerTime.Time+1;
            var s=host.Snapshot.Value;s.Health=s.Ink=100;s.Grounded=true;s.Swimming=false;s.CompactBody=false;s.AirHumanOffset=0;s.Yaw=90;s.Pitch=-5;
            if(!SubWeaponService.TryMineGround(s,out _))
            {
                bool found=false;
                foreach(var surface in Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None).Where(x=>x.Scores))
                {
                    var collider=surface.GetComponent<Collider>();if(collider==null)continue;
                    for(int x=-10;x<=10&&!found;x+=2)for(int z=-10;z<=10&&!found;z+=2)
                    {
                        var probe=collider.bounds.center+new Vector3(x,20,z);
                        if(!Physics.Raycast(probe,Vector3.down,out var hit,40,PlayerMotorSimulation.WorldMask))continue;
                        s.Position=hit.point+Vector3.up*.04f;found=SubWeaponService.TryMineGround(s,out _);
                    }
                    if(found)break;
                }
                Assert.That(found,Is.True,"Paintable mine placement");
            }
            var cc=host.GetComponent<CharacterController>();cc.enabled=false;host.transform.position=s.Position;cc.enabled=true;host.Snapshot.Value=s;host.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            var placementPosition=s.Position;
            int snapshotSteps=0;
            void Flush()
            {foreach(var e in match.SubWeapons.Lifecycle)match.SubPresentation.Lifecycle(e);match.SubWeapons.Lifecycle.Clear();foreach(var e in match.SubWeapons.Effects)match.SubPresentation.Effect(e);match.SubWeapons.Effects.Clear();if(++snapshotSteps%6==0)match.SubPresentation.Apply(match.SubWeapons.Capture(),match.SubWeapons.Watermark);}
            void Clear()
            {match.SubWeapons.Clear();Flush();match.SubPresentation.Clear();snapshotSteps=0;}
            SubWeaponService.Entity Spawn(SubWeaponConfigAsset asset,SubLaunchSolution launch,float charge)
            {
                Assert.That(launch.Valid,Is.True,asset.type+" launch: "+launch.Failure);
                SubWeaponConfigService.Current.Set(s.HeroId,asset);s.SubAction++;s.Ink=s.Health=100;host.Snapshot.Value=s;
                var entity=match.SubWeapons.Spawn(host,s,charge,now,match.State.Value.Round,launch);Assert.That(entity,Is.Not.Null,asset.type.ToString());Flush();return entity;
            }
            try
            {
                // Exercise the real player Step ordering: commit must use this frame's new movement and look.
                Clear();var burst=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(SubWeaponType.BurstBomb));SubWeaponConfigService.Current.Set(s.HeroId,burst);
                s.SubPhase=SubWeaponPhase.Starting;s.SubReleaseAt=now;s.SubNeedsRelease=false;s.Yaw=0;s.Pitch=15;s.Ink=s.Health=100;
                var input=new PlayerInputFrame{Revision=s.Revision,HeroRevision=s.HeroRevision,Look=new Vector2(90,35),Move=Vector2.up,SubPressSequence=s.SubConsumedPress,SubReleaseSequence=s.SubConsumedRelease};
                object[] args={s,input,1f/60,now,MatchPhase.Practice};typeof(PrototypePlayer).GetMethod("Step",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(host,args);
                s=(PlayerSnapshot)args[0];host.Snapshot.Value=s;
                var launched=match.SubWeapons.Entities.Last(e=>e.State.Owner==host.PlayerId);var expected=SubWeaponService.SolveLaunch(host,s,burst.Snapshot(),s.SubCharge);
                float launchError=Vector3.Distance(expected.Position,launched.State.Position);float velocityError=Vector3.Distance(expected.Velocity,launched.State.Velocity);
                Assert.That(launchError,Is.LessThan(.001));Assert.That(velocityError,Is.LessThan(.001));
                var predictionRoot=new GameObject("Prediction adoption fixture");var prediction=predictionRoot.AddComponent<SubWeaponPresentation>();prediction.Match=match;prediction.enabled=false;
                prediction.PredictThrow(host,s,expected);Assert.That(prediction.TryPredictedVisual(host.PlayerId,s.SubAction,out var predicted),Is.True);
                prediction.Lifecycle(match.SubWeapons.Lifecycle.Last(e=>e.Kind==SubLifecycleKind.Spawn));
                Assert.That(prediction.TryVisual(launched.State.Id,out var adopted),Is.True);Assert.That(adopted,Is.SameAs(predicted));Assert.That(prediction.PredictionCount,Is.Zero);
                File.WriteAllText(Path.Combine(Output,"launch-and-adoption.txt"),FormattableString.Invariant($"launchPositionError={launchError:F6}\nlaunchVelocityError={velocityError:F6}\nsameVisualInstance=True\n"));
                Object.Destroy(predictionRoot);
                // The movement test must not invalidate the paintable ground fixture used by mines.
                s.Position=placementPosition;s.Grounded=true;s.Swimming=false;s.CompactBody=false;s.AirHumanOffset=0;s.Yaw=90;s.Pitch=-5;
                cc.enabled=false;host.transform.position=s.Position;cc.enabled=true;host.Snapshot.Value=s;host.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
                Assert.That(SubWeaponService.TryMineGround(s,out _),Is.True,"Restored paintable mine placement");
                var playerPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
                var enemy=match.AddTestBot(playerPrefab);Assert.That(enemy,Is.Not.Null);enemy.enabled=false;
                void TargetAt(Vector3 position)
                {
                    var target=enemy.Snapshot.Value;target.Position=position;target.Team=2;target.Health=100;target.ProtectedUntil=0;
                    var controller=enemy.GetComponent<CharacterController>();controller.enabled=false;enemy.transform.position=position;controller.enabled=true;
                    enemy.Snapshot.Value=target;enemy.SwimBody?.ApplyCollision(target);Physics.SyncTransforms();
                }
                foreach(int fps in new[]{30,60,120})foreach(SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
                {
                    TargetAt(origin+Vector3.right*100);
                    // The owner's body stands directly above a freshly placed mine; isolate its visual in recordings.
                    for(int i=0;i<ownerRenderers.Length;i++)ownerRenderers[i].enabled=type!=SubWeaponType.InkMine&&ownerVisibility[i];
                    Clear();var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));var c=asset.Snapshot();float charge=type==SubWeaponType.FizzyBomb?1:type==SubWeaponType.CurlingBomb?.5f:0;
                    var launch=new SubLaunchSolution{Position=origin+Vector3.up*1.5f,Velocity=(Vector3.right+.08f*Vector3.up).normalized*c.Flight.speed+Vector3.up*c.Flight.lift,Normal=Vector3.up};
                    if(type==SubWeaponType.CurlingBomb){launch.Position=origin+Vector3.up*(c.Flight.radius+.04f);launch.Velocity=Vector3.right*Mathf.Lerp(c.Curling.speed.x,c.Curling.speed.y,charge);}
                    if(type==SubWeaponType.AngleShooter)launch.Velocity=Vector3.right*c.Angle.speed;
                    if(type==SubWeaponType.InkMine)launch=SubWeaponService.SolveLaunch(host,s,c,charge);
                    var identity=new SubEntityState{Owner=host.PlayerId,Team=s.Team,Yaw=s.Yaw,Charge=charge};
                    var preview=SubWeaponMotion.Preview(launch,c,identity,points,aim);
                    var bounds=new Bounds(launch.Position,Vector3.one);foreach(var point in points)bounds.Encapsulate(point);
                    float distance=Mathf.Max(7,bounds.extents.magnitude*1.5f+3);var center=bounds.center;
                    camera.transform.position=center+new Vector3(-.45f,.55f,-1).normalized*distance;camera.transform.LookAt(center);
                    var entity=Spawn(asset,launch,charge);double start=now,sim=start;bool measured=false,mineWarning=false;float error=0;
                    var previewLine=SubWeaponVisual.Line(null,c.Common.effectMaterial,.025f,true);previewLine.startColor=previewLine.endColor=new Color(.65f,.85f,1,.35f);previewLine.positionCount=points.Count;for(int i=0;i<points.Count;i++)previewLine.SetPosition(i,points[i]);
                    var marker=SubWeaponVisual.Line(null,c.Common.effectMaterial,.04f,true);marker.startColor=marker.endColor=Color.white;SubWeaponVisual.Ring(marker,preview.Position,.35f,preview.Normal);
                    double sprinklerEnd=preview.Time+c.Sprinkler.phases.x+c.Sprinkler.phases.y+1.5;
                    double mineTrigger=c.Mine.arm+.5;
                    float duration=type switch
                    {
                        SubWeaponType.Sprinkler=>(float)(sprinklerEnd+c.Sprinkler.dropletLifetime+1),
                        SubWeaponType.SuctionBomb=>(float)(preview.Time+c.Suction.fuse+1),
                        SubWeaponType.Autobomb=>(float)(preview.Time+c.Autobomb.searchDelay+c.Autobomb.fuse+1),
                        SubWeaponType.SplashWall=>(float)(preview.Time+c.Wall.expand+c.Wall.lifetime+1),
                        SubWeaponType.ToxicMist=>(float)(preview.Time+c.Mist.duration+1),
                        SubWeaponType.InkMine=>(float)(mineTrigger+c.Mine.fuse+1),
                        SubWeaponType.PointSensor=>(float)(preview.Time+c.Sensor.duration+1),
                        _=>Mathf.Max(2,(float)preview.Time+1.5f)
                    };
                    using(var poses=new StreamWriter(Path.Combine(Output,$"{type}-{fps}.csv")))
                    using(var recording=new Recording(camera,fps,Path.Combine(Output,$"{type}-{fps}.mp4")))
                    {
                        poses.WriteLine("frame,time,phase,removed,x,y,z,model_x,model_y,model_z,anchor_error_m,model_authority_error_m");
                        for(int frame=0;frame<=Mathf.CeilToInt(duration*fps);frame++)
                        {
                            double time=start+(double)frame/fps;
                            if(type==SubWeaponType.InkMine&&frame==(int)Math.Ceiling(mineTrigger*fps))TargetAt(entity.State.Position+Vector3.right*.75f);
                            if(type==SubWeaponType.Sprinkler&&frame==(int)Math.Ceiling(sprinklerEnd*fps)){match.SubWeapons.DamageObject(entity.State.Id,2,10000);Flush();}
                            while(sim+1d/60<=time+1e-7){sim+=1d/60;match.SubWeapons.Step(sim,1f/60,match.Players);Flush();}
                            match.SubPresentation.PresentAt(time,1f/fps);
                            bool visual=match.SubPresentation.TryVisual(entity.State.Id,out var view);
                            var model=visual&&view.Model!=null?view.Model.transform.position:entity.State.Position;
                            float anchor=visual?Vector3.Distance(model,view.Root.transform.position):0;
                            poses.WriteLine(FormattableString.Invariant($"{frame},{time-start:F5},{entity.State.Phase},{entity.Removed},{entity.State.Position.x:F5},{entity.State.Position.y:F5},{entity.State.Position.z:F5},{model.x:F5},{model.y:F5},{model.z:F5},{anchor:F6},{Vector3.Distance(model,entity.State.Position):F6}"));
                            Assert.That(anchor,Is.LessThan(.01f),type+" model anchor");
                            mineWarning|=entity.State.Phase==SubEntityPhase.Warning;
                            if(!entity.Removed&&time+1e-7<entity.State.Expires&&entity.State.Phase==SubEntityPhase.Active&&
                                (type==SubWeaponType.InkMine||type==SubWeaponType.PointSensor||type==SubWeaponType.ToxicMist||type==SubWeaponType.Sprinkler||type==SubWeaponType.SplashWall))
                                Assert.That(visual&&view.PersistentVisible,Is.True,type+" uninterrupted persistent effect");
                            if(!measured&&(type==SubWeaponType.InkMine||preview.End==SubPreviewEnd.Deployment&&entity.State.Phase==SubEntityPhase.Active||preview.End==SubPreviewEnd.Tracking&&entity.State.Phase==SubEntityPhase.Grounded||entity.Removed))
                            {
                                measured=true;error=Vector3.Distance(entity.State.Position,preview.Position);
                                if(!preview.Uncertain)Assert.That(error,Is.LessThan(.01f),type+" preview versus authoritative endpoint");
                            }
                            foreach(var ps in match.SubPresentation.GetComponentsInChildren<ParticleSystem>())if(ps.gameObject.activeInHierarchy)ps.Simulate(1f/fps,false,false);
                            if(entity.State.Phase==SubEntityPhase.Active || entity.State.Phase==SubEntityPhase.Warning)
                            {
                                var close=entity.State.Position+(type==SubWeaponType.InkMine?new Vector3(0,5,-.5f):new Vector3(-3,3,-5));float blend=1-Mathf.Exp(-4f/fps);
                                camera.transform.position=Vector3.Lerp(camera.transform.position,close,blend);camera.transform.LookAt(entity.State.Position+Vector3.up*.35f);
                            }
                            string screenshot=fps==60&&(frame==3||frame==fps||frame==fps*3||type==SubWeaponType.Sprinkler&&(frame==fps*9||frame==fps*24))?Path.Combine(Output,$"{type}-{frame}.png"):null;
                            recording.Frame(screenshot);
                            if(frame%30==0){File.WriteAllText(Path.Combine(Output,"progress.txt"),$"{type} {fps} FPS frame {frame}/{Mathf.CeilToInt(duration*fps)}");yield return null;}
                        }
                        bool flight=type==SubWeaponType.InkMine||match.SubPresentation.ObservedFlights.Contains(type);
                        bool persistent=match.SubPresentation.ObservedPersistent.Contains(type);
                        Assert.That(entity.Removed,Is.True,type+" recording must include gameplay end");
                        if(type==SubWeaponType.InkMine)Assert.That(mineWarning,Is.True,"Mine trigger warning recorded");
                        Assert.That(flight,Is.True,type+" actual flight feedback");
                        if(type==SubWeaponType.InkMine||type==SubWeaponType.PointSensor||type==SubWeaponType.ToxicMist||type==SubWeaponType.Sprinkler||type==SubWeaponType.SplashWall)Assert.That(persistent,Is.True,type+" persistent visual");
                        results.Add(FormattableString.Invariant($"{type},{fps},{error:F6},{match.SubPresentation.MaximumAnchorError:F6},{recording.Frames},{flight},{persistent},{preview.End}"));
                        now=sim+1;
                    }
                    Object.Destroy(previewLine.gameObject);Object.Destroy(marker.gameObject);
                    File.WriteAllLines(Path.Combine(Output,"results.csv"),results);
                }
                // Separate geometry fixtures cover attachments that the open-floor recording cannot exercise.
                foreach(var type in new[]{SubWeaponType.SuctionBomb,SubWeaponType.Sprinkler,SubWeaponType.SplashWall})foreach(string shape in new[]{"slope","wall","ceiling","corner","near"})
                {
                    Clear();var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));var c=asset.Snapshot();wall.SetActive(true);
                    wall.transform.rotation=Quaternion.identity;wall.transform.localScale=shape=="ceiling"?new Vector3(20,.5f,20):shape=="slope"?new Vector3(14,.5f,14):new Vector3(.5f,8,14);
                    wall.transform.position=origin+(shape=="ceiling"?Vector3.up*4:shape=="slope"?Vector3.up:Vector3.right*(shape=="near"?1:4)+Vector3.up*3);
                    if(shape=="slope")wall.transform.rotation=Quaternion.Euler(0,0,18);
                    if(shape=="corner")wall.transform.rotation=Quaternion.Euler(0,35,0);
                    Physics.SyncTransforms();var launch=new SubLaunchSolution{Position=origin+Vector3.up*2,Velocity=shape=="ceiling"?Vector3.up*20:Vector3.right*14,Normal=Vector3.up};
                    var preview=SubWeaponMotion.Preview(launch,c,new SubEntityState{Owner=host.PlayerId,Team=s.Team,Yaw=s.Yaw},points,aim);var entity=Spawn(asset,launch,0);
                    for(int step=0;step<600&&!entity.Removed&&entity.State.Phase!=SubEntityPhase.Active;step++){now+=1d/60;match.SubWeapons.Step(now,1f/60,match.Players);}
                    Assert.That(entity.State.Phase,Is.EqualTo(SubEntityPhase.Active),type+" "+shape);float error=Vector3.Distance(preview.Position,entity.State.Position);float normal=Vector3.Angle(preview.Normal,entity.State.Normal);
                    Assert.That(error,Is.LessThan(.01f),type+" "+shape);Assert.That(normal,Is.LessThan(1));surfaces.Add(FormattableString.Invariant($"{type},{shape},{error:F6},{normal:F4}"));
                    if(entity.Attached){var before=entity.State.Position;entity.Attachment.position+=Vector3.forward*.25f;now+=1d/60;match.SubWeapons.Step(now,1f/60,match.Players);Assert.That(Vector3.Distance(entity.State.Position,before+Vector3.forward*.25f),Is.LessThan(.01f));}
                    wall.SetActive(false);fixture.transform.position=origin+Vector3.down*.5f;Physics.SyncTransforms();
                }
                File.WriteAllLines(Path.Combine(Output,"surface-results.csv"),surfaces);
                var reuse=new List<string>{"type,throws,max_anchor_error_m"};
                foreach(SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
                {
                    float worst=0;
                    for(int use=0;use<20;use++)
                    {
                        Clear();var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));var c=asset.Snapshot();
                        var launch=new SubLaunchSolution{Position=origin+new Vector3(use*.3f,1.5f,0),Velocity=Vector3.right*10,Normal=Vector3.up};
                        if(type==SubWeaponType.InkMine)launch=SubWeaponService.SolveLaunch(host,s,c,0);
                        var e=Spawn(asset,launch,0);
                        for(int step=0;step<90&&!e.Removed;step++){now+=1d/60;match.SubWeapons.Step(now,1f/60,match.Players);Flush();match.SubPresentation.PresentAt(now,1f/60);worst=Mathf.Max(worst,match.SubPresentation.MaximumAnchorError);}
                        if(use%4==0){s.HeroRevision++;host.Snapshot.Value=s;now+=1d/60;match.SubWeapons.Step(now,1f/60,match.Players);Flush();}
                        if(use%4==1){var dead=s;dead.Health=0;host.Snapshot.Value=dead;now+=1d/60;match.SubWeapons.Step(now,1f/60,match.Players);Flush();s.Revision++;host.Snapshot.Value=s;}
                        Assert.That(worst,Is.LessThan(.01f),type+" repeated lifecycle "+use);
                    }
                    reuse.Add(FormattableString.Invariant($"{type},20,{worst:F6}"));yield return null;
                }
                File.WriteAllLines(Path.Combine(Output,"reuse-results.csv"),reuse);
                // Acquire a moving target after flight has begun, then record transform/search and chase.
                var tracking=new List<string>();
                foreach(var type in new[]{SubWeaponType.Autobomb,SubWeaponType.Torpedo})
                {
                    Clear();TargetAt(origin+Vector3.right*30);var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));
                    var launch=new SubLaunchSolution{Position=origin+Vector3.up*1.5f,Velocity=Vector3.right*12,Normal=Vector3.up};var e=Spawn(asset,launch,0);
                    bool seeking=false,transforming=false,warning=false;camera.transform.position=origin+new Vector3(-3,6,-9);camera.transform.LookAt(origin+Vector3.right*3);
                    using(var recording=new Recording(camera,60,Path.Combine(Output,$"{type}-tracking-60.mp4")))
                    for(int frame=0;frame<300;frame++)
                    {
                        // Torpedoes only acquire before landing; introduce the target while still airborne.
                        now+=1d/60;if(frame==(type==SubWeaponType.Torpedo?5:20))TargetAt(new Vector3(e.State.Position.x+3,origin.y+.04f,e.State.Position.z));
                        match.SubWeapons.Step(now,1f/60,match.Players);Flush();match.SubPresentation.PresentAt(now,1f/60);
                        seeking|=e.State.Phase==SubEntityPhase.Seeking;transforming|=e.State.Phase==SubEntityPhase.Transforming;warning|=e.State.Phase==SubEntityPhase.Warning;
                        foreach(var ps in match.SubPresentation.GetComponentsInChildren<ParticleSystem>())if(ps.gameObject.activeInHierarchy)ps.Simulate(1f/60,false,false);
                        recording.Frame(frame==60?Path.Combine(Output,$"{type}-tracking.png"):null);if(frame%30==0)yield return null;
                    }
                    Assert.That(seeking,Is.True,type+" seeks");if(type==SubWeaponType.Torpedo)Assert.That(transforming,Is.True);else Assert.That(warning,Is.True);
                    tracking.Add($"{type}: seeking={seeking}, transforming={transforming}, warning={warning}");
                }
                File.WriteAllLines(Path.Combine(Output,"tracking-results.txt"),tracking);
                File.WriteAllText(Path.Combine(Output,"progress.txt"),"PASS: 39 rendered sequences, 15 attachment fixtures, 260 repeated throws, 2 tracking recordings");
            }
            finally
            {
                for(int i=0;i<ownerRenderers.Length;i++)if(ownerRenderers[i]!=null)ownerRenderers[i].enabled=ownerVisibility[i];
                match.SubWeapons.Clear();SubWeaponConfigService.Current.Set(s.HeroId,original);match.enabled=true;host.enabled=true;match.SubPresentation.enabled=true;
                Object.Destroy(fixture);Object.Destroy(wall);Object.Destroy(camera.gameObject);
            }
            yield return app.Leave().ToCoroutine();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {if(!Application.isPlaying)yield break;if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)yield return PrototypeApp.Current.Leave().ToCoroutine();yield return new ExitPlayMode();}
    }
}
#endif
