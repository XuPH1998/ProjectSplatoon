#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    public static class InkFlightValidation
    {
        public const string Output = "Reports/InkFlightReference";
        const string Fixtures = "Assets/Splatoon/Tests/VisualReference/InkFlight/";
        static Camera camera;
        static RenderTexture target;
        static RenderMetaballsScreenSpace feature;
        static Material productionComposite;
        static AmmoRuntimeConfig profile;
        static readonly List<string> notes = new();
        static GameObject root, wall;
        static InkFlightPresentation flight;
        static WeaponRuntimeConfig referenceWeapon;
        static readonly List<double> cpu = new(), submit = new(), gpu = new();
        static readonly FrameTiming[] timings = new FrameTiming[1];
        static int perfFrame;
        static long allocations;
        static bool buildAfterCapture;
        static string resultsFile = "/visual-results.txt";

        public static void CaptureAndBuildPlayer() { buildAfterCapture = true; Run(); }
        public static void PerformanceAndBuildPlayer()
        {
            Directory.CreateDirectory(Output); buildAfterCapture=true; resultsFile="/performance-final.txt";
            try { Setup(); BeginPerformance(); EditorApplication.update+=PerformanceTick; }
            catch(Exception ex) { Fail(ex); }
        }

        public static void BuildPlayer()
        {
            // Player-only timing / real client acceptance is the reason for this explicit build.
            PlayerSettings.enableFrameTimingStats = true;
            PrototypeBuilder.BuildWindows();
            EditorApplication.Exit(0);
        }

        public static void MuzzleOnly()
        {
            Directory.CreateDirectory(Output);
            try { Setup(); CaptureMuzzle(true); CaptureMuzzle(false); File.WriteAllLines(Output + "/muzzle-diagnostics.txt", notes); Cleanup(); EditorApplication.Exit(0); }
            catch (Exception ex) { Fail(ex); }
        }

        public static void OcclusionOnly()
        {
            Directory.CreateDirectory(Output);
            try { Setup(); CheckOcclusion(); File.WriteAllLines(Output + "/occlusion-results.txt", notes); Cleanup(); EditorApplication.Exit(0); }
            catch (Exception ex) { Fail(ex); }
        }

        public static void Run()
        {
            Directory.CreateDirectory(Output);
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new InvalidOperationException("GPU required");
                Setup();
                notes.Add($"Unity {Application.unityVersion}; {SystemInfo.graphicsDeviceName}; source = current local MixAndJam; 1920x1080, 60Hz fixed simulation, seed 137.");
                notes.Add("Source uses native particle emission/ballistics; restored mode uses the same source curves on authoritative flight samples and the approved pink/mint hue adaptation. These are visual comparisons, not pixel-identical fluid simulation claims.");
                foreach (string scenario in new[] { "continuous", "sweep", "short-stop" })
                { CaptureFlight(scenario, true); CaptureFlight(scenario, false); }
                CaptureMuzzle(true); CaptureMuzzle(false);
                CaptureWeapons(); CheckOcclusion(); CheckProtected();
                File.WriteAllLines(Output + "/visual-results.txt", notes);
                BeginPerformance(); EditorApplication.update += PerformanceTick;
            }
            catch (Exception ex) { Fail(ex); }
        }
        static void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            root = new GameObject("Ink flight comparison");
            var light = new GameObject("Key light").AddComponent<Light>(); light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(40,-35,0); light.color = new Color(1,.95f,.88f); light.intensity = 1.15f;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(.48f,.52f,.55f);
            camera = new GameObject("Ink flight camera").AddComponent<Camera>(); camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.18f,.22f,.25f);
            camera.fieldOfView = 42; camera.nearClipPlane = .03f; camera.farClipPlane = 80; camera.allowHDR = true; camera.allowMSAA = false;
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            camera.transform.position = new Vector3(6,3.4f,-2); camera.transform.LookAt(new Vector3(0,1.4f,5));
            target = new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32); target.Create(); camera.aspect = 16f/9;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.SetColor("_BaseColor",new Color(.62f,.65f,.65f)); material.SetFloat("_Smoothness",.15f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(0,-.12f,8);floor.transform.localScale = new Vector3(25,.2f,30);floor.GetComponent<Renderer>().sharedMaterial = material;
            wall = GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position = new Vector3(0,1.5f,5);wall.transform.localScale = new Vector3(8,6,.25f);wall.GetComponent<Renderer>().sharedMaterial = material;wall.SetActive(false);
            var referenceAmmo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(Fixtures + "ReferenceAmmoConfig.asset");
            profile = new AmmoRuntimeConfig(referenceAmmo);
            feature = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset").rendererFeatures.OfType<RenderMetaballsScreenSpace>().Single(f=>f.name=="InkFlightMetaballs");
            productionComposite = feature.BlitMaterial;
            var w = ScriptableObject.CreateInstance<WeaponConfigAsset>();
            w.fireMode = WeaponFireMode.Automatic;w.pelletCount=1;w.fireRate=1/.03f;w.speedMin=20;w.speedMax=25;w.projectileGravity=19.62f;w.lifetime=1;w.brakeSeconds=1;w.brakeSpeedMultiplier=1;
            w.ammoConfig = referenceAmmo; referenceWeapon = new WeaponRuntimeConfig(w);Object.DestroyImmediate(w);
        }
        static void Composite(bool source)
        {
            feature.BlitMaterial = source ? AssetDatabase.LoadAssetAtPath<Material>(Fixtures+"ReferenceStepAndClip.mat") : productionComposite;
            feature.Event = source ? RenderPassEvent.AfterRenderingOpaques : RenderPassEvent.AfterRenderingTransparents;
            feature.Create();
        }
        static ParticleSystem Native(string name)
        {
            var ps = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Fixtures+"Reference"+name+".prefab"),root.transform).GetComponent<ParticleSystem>();
            ps.gameObject.layer=InkFlightPresentation.Layer;ps.transform.position=Vector3.up*2.4f;
            ps.useAutoRandomSeed=false;ps.randomSeed=137;ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);ps.Play(false);return ps;
        }
        static InkShot ReferenceShot(uint id,double born,Vector3 origin,Quaternion rotation,byte team=1,ulong shooter=1)
        {
            uint seed=id*747796405u+137;float speed=Mathf.Lerp(20,25,InkBallistics.Random01(ref seed));
            return new InkShot{Id=id,Round=1,ActionId=id,Seed=seed,Shooter=shooter,HeroId=1,Team=team,Born=born,Origin=origin,Velocity=rotation*Vector3.forward*speed,Configuration=referenceWeapon,Lifecycle=1,HeroRevision=1};
        }
        static void CaptureFlight(string scenario,bool source)
        {
            Composite(source);string folder=Output+"/"+scenario+"-"+(source?"reference":"restored");Directory.CreateDirectory(folder);
            ParticleSystem native=source?Native("Flight"):null;
            if(!source)flight=new InkFlightPresentation(root.transform,profile);
            double next=0;uint id=1;
            for(int frame=0;frame<84;frame++)
            {
                double time=frame/60.0;bool emitting=time<(scenario=="short-stop"?.18:.85);
                Vector3 origin=new Vector3(scenario=="sweep"?Mathf.Sin((float)time*4)*.65f:0,2.4f,0);
                Quaternion rotation=Quaternion.Euler(0,scenario=="sweep"?Mathf.Sin((float)time*3)*12:0,0);
                if(source)
                {
                    native.transform.SetPositionAndRotation(origin,rotation);
                    if(!emitting)native.Stop(false,ParticleSystemStopBehavior.StopEmitting);
                    native.Simulate(1f/60,false,false,false);native.Pause(false);
                }
                else
                {
                    while(emitting&&next<=time+1e-7){flight.Spawn(ReferenceShot(id++,next,origin,rotation),time);next+=.03;}
                    flight.Update(time,camera);
                }
                Capture(folder+"/"+frame.ToString("D3")+".png");
            }
            notes.Add(scenario+" "+(source?"source":"restored")+": 84 frames at 60Hz including emission stop.");
            if(native!=null)Object.DestroyImmediate(native.gameObject);if(flight!=null){flight.Dispose();flight=null;}
        }
        static void CaptureMuzzle(bool source)
        {
            Composite(source);var oldPosition=camera.transform.position;var oldRotation=camera.transform.rotation;
            camera.transform.position=new Vector3(1,2.7f,-1.2f);camera.transform.LookAt(new Vector3(0,2.4f,.1f));
            var ps=source?Native("Muzzle"):Object.Instantiate(profile.MuzzlePrefab,root.transform);
            ps.transform.position=Vector3.up*2.4f;
            InkMuzzleEmitter emitter=null;
            if(!source){emitter=ps.gameObject.AddComponent<InkMuzzleEmitter>();emitter.Initialize(profile);emitter.Shot(137,1,true,0,0);}
            string folder=Output+"/muzzle-"+(source?"reference":"restored");Directory.CreateDirectory(folder);
            for(int i=0;i<42;i++)
            {
                double t=i/60.0;
                if(source&&t>=.3)ps.Stop(false,ParticleSystemStopBehavior.StopEmitting);
                emitter?.Present(t<.3,true,t);ps.Simulate(1f/60,false,false,false);ps.Pause(false);
                if(i==6)
                {
                    var drops=new ParticleSystem.Particle[160];int count=ps.GetParticles(drops);
                    notes.Add($"muzzle source={source} count={count} bounds={ps.GetComponent<Renderer>().bounds}");
                    for(int n=0;n<Math.Min(3,count);n++)notes.Add($"drop {n}: position={drops[n].position} size={drops[n].startSize3D} current={drops[n].GetCurrentSize3D(ps)} lifetime={drops[n].startLifetime} remaining={drops[n].remainingLifetime}");
                }
                Capture(folder+"/"+i.ToString("D3")+".png");
            }
            Object.DestroyImmediate(ps.gameObject);camera.transform.SetPositionAndRotation(oldPosition,oldRotation);
        }
        static void CaptureWeapons()
        {
            Composite(false);
            foreach(string guid in AssetDatabase.FindAssets("t:WeaponConfigAsset",new[]{"Assets/GameResource/Weapons"}))
            {
                var asset=AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(AssetDatabase.GUIDToAssetPath(guid));var w=new WeaponRuntimeConfig(asset);
                flight?.Dispose(); flight=new InkFlightPresentation(root.transform,w.Ammo);
                foreach(byte team in new byte[]{1,2})
                {
                    flight.Clear();int pellets=Mathf.Max(1,w.PelletCount);
                    for(int i=0;i<pellets;i++){var s=ReferenceShot((uint)i+1,0,Vector3.up*2.4f,Quaternion.Euler(0,(i-(pellets-1)*.5f)*2,0),team);s.Configuration=w;s.Velocity=s.Velocity.normalized*w.SpeedMin;s.PelletIndex=(byte)i;flight.Spawn(s,0);}
                    flight.Update(.08,camera);Capture(Output+"/weapon-"+asset.name+"-team"+team+".png");
                    notes.Add(asset.name+" team="+team+" particles="+flight.ParticleCount+" lanes="+flight.ActiveGroups);
                }
            }
            flight.Dispose();flight=null;
        }
        static void CheckOcclusion()
        {
            Composite(false);wall.SetActive(true);var oldPosition=camera.transform.position;var oldRotation=camera.transform.rotation;
            camera.transform.position=new Vector3(0,2,0);camera.transform.LookAt(new Vector3(0,2,8));
            var baseline=Pixels();flight=new InkFlightPresentation(root.transform,profile);
            for(uint i=1;i<=15;i++)flight.Spawn(ReferenceShot(i,0,new Vector3((i%5-2)*.2f,2,7),Quaternion.identity),0);
            flight.Update(.04,camera);var painted=Pixels();int changed=Difference(baseline,painted);
            Capture(Output+"/wall-occlusion.png");notes.Add("Fully hidden flight changed pixels="+changed);
            if(changed>0)throw new InvalidOperationException("Flying ink is visible through the occluder: "+changed);
            flight.Clear();wall.transform.localScale=new Vector3(2,6,.25f);baseline=Pixels();
            for(uint i=1;i<=31;i++)flight.Spawn(ReferenceShot(i,0,new Vector3(((int)i-16)*.12f,2,7),Quaternion.identity),0);
            flight.Update(.04,camera);painted=Pixels();
            int left=Mathf.CeilToInt(camera.WorldToViewportPoint(new Vector3(-1,2,4.875f)).x*target.width)+2;
            int right=Mathf.FloorToInt(camera.WorldToViewportPoint(new Vector3(1,2,4.875f)).x*target.width)-2;
            int behind=0,visible=0;
            for(int y=0;y<target.height;y++)for(int x=0;x<target.width;x++)
            {
                int at=y*target.width+x;
                if(baseline[at].r==painted[at].r&&baseline[at].g==painted[at].g&&baseline[at].b==painted[at].b)continue;
                if(x>=left&&x<=right)behind++;else visible++;
            }
            notes.Add($"Partial wall: occluded interior changed pixels={behind}; visible outside={visible}; silhouette margin=2 px.");
            Capture(Output+"/wall-edge-occlusion.png");
            if(behind!=0||visible<25)throw new InvalidOperationException("Partial wall occlusion failed");
            flight.Dispose();flight=null;wall.SetActive(false);camera.transform.SetPositionAndRotation(oldPosition,oldRotation);
        }
        static void CheckProtected()
        {
            Composite(false);var ps=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(InkFlightBuilder.Root+"Prefabs/InkImpact.prefab"),root.transform);
            ps.transform.position=new Vector3(0,1.4f,2);ps.transform.rotation=Quaternion.LookRotation(Vector3.up);
            var effect=ps.GetComponent<InkImpactEffect>();effect.Play(PrototypeArena.TeamColor(1),137);effect.Splash.Simulate(.12f,true,false,false);effect.Splash.Pause(true);
            feature.SetActive(false);var before=Pixels();feature.SetActive(true);var after=Pixels();
            int changed=Difference(before,after);notes.Add("Existing impact, old pipeline vs isolated flight pass: changed pixels="+changed);
            if(changed!=0)throw new InvalidOperationException("Flight pass changed protected impact rendering");
            Capture(Output+"/protected-impact.png");Object.DestroyImmediate(ps);
            var swim=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(InkFlightBuilder.Root+"Prefabs/InkStream.prefab"),root.transform).GetComponent<ParticleSystem>();
            swim.transform.position=new Vector3(0,1.4f,2);InkCharacterView.ConfigureSwimEffect(swim);swim.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=swim.main;main.startColor=PrototypeArena.TeamColor(2);main.startSpeed=.3f;var emission=swim.emission;emission.enabled=false;
            swim.Emit(12);swim.Simulate(.1f,false,false,false);swim.Pause(false);
            feature.SetActive(false);before=Pixels();feature.SetActive(true);after=Pixels();changed=Difference(before,after);
            notes.Add("Existing swim, old pipeline vs isolated flight pass: changed pixels="+changed);if(changed!=0)throw new InvalidOperationException("Flight pass changed swim rendering");
            Capture(Output+"/protected-swim.png");Object.DestroyImmediate(swim.gameObject);
        }
        static int Difference(Color32[] a,Color32[] b){int count=0;for(int i=0;i<a.Length;i++)if(a[i].r!=b[i].r||a[i].g!=b[i].g||a[i].b!=b[i].b)count++;return count;}
        static void Render()=>RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
        static Texture2D Read()
        {Render();var previous=RenderTexture.active;RenderTexture.active=target;var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();RenderTexture.active=previous;return image;}
        static Color32[] Pixels(){var image=Read();var pixels=image.GetPixels32();Object.DestroyImmediate(image);return pixels;}
        static void Capture(string path){var image=Read();File.WriteAllBytes(path,image.EncodeToPNG());Object.DestroyImmediate(image);}
        static void BeginPerformance()
        {
            target.Release();Object.DestroyImmediate(target);target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);target.Create();
            flight=new InkFlightPresentation(root.transform,profile);perfFrame=0;allocations=0;cpu.Clear();submit.Clear();gpu.Clear();
        }
        static void PerformanceTick()
        {
            try
            {
                double time=perfFrame/60.0;long before=GC.GetAllocatedBytesForCurrentThread();long start=Stopwatch.GetTimestamp();
                if(perfFrame%2==0)for(uint player=0;player<8;player++)
                {var s=ReferenceShot((uint)perfFrame*8+player+1,time,new Vector3((player-3.5f)*.35f,2.4f,0),Quaternion.identity,(byte)(player%2+1),player+1);flight.Spawn(s,time);}
                flight.Update(time,camera);long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
                double elapsed=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;start=Stopwatch.GetTimestamp();Render();double submitted=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
                FrameTimingManager.CaptureFrameTimings();
                if(perfFrame>=120){cpu.Add(elapsed);submit.Add(submitted);allocations+=allocated;if(FrameTimingManager.GetLatestTimings(1,timings)>0&&timings[0].gpuFrameTime>0)gpu.Add(timings[0].gpuFrameTime);}
                if(++perfFrame<480)return;
                notes.Add($"720p Editor 8 cosmetic emitters, 240 accepted shots/s: {cpu.Count} samples; CPU P50={Percentile(cpu,.5):F3} P95={Percentile(cpu,.95):F3}ms; submission P95={Percentile(submit,.95):F3}ms; managed flight allocation={allocations}; particles={flight.ParticleCount}; lanes={flight.ActiveGroups}; dropped={flight.DroppedSamples}.");
                notes.Add(gpu.Count>0?$"Editor whole-frame GPU P95={Percentile(gpu,.95):F3}ms; not a standalone Player benchmark.":"GPU frame timing unavailable; render-submission CPU time is not GPU timing.");
                notes.Add("Screen pass texture descriptors: RGBA8 ink + two RGBA8 blur + R32 float linear depth + D32 depth = 20 bytes/pixel, 17.58 MiB at 720p / 39.55 MiB at 1080p, separate from persistent paint textures.");
                File.WriteAllLines(Output+resultsFile,notes);EditorApplication.update-=PerformanceTick;Cleanup();
                if(buildAfterCapture)BuildPlayer();else EditorApplication.Exit(0);
            }
            catch(Exception ex){Fail(ex);}
        }
        static double Percentile(List<double> data,double p){data.Sort();return data[(int)((data.Count-1)*p)];}
        static void Cleanup(){flight?.Dispose();flight=null;if(feature!=null){feature.BlitMaterial=productionComposite;feature.Event=RenderPassEvent.AfterRenderingTransparents;feature.SetActive(true);feature.Create();}if(target!=null){target.Release();Object.DestroyImmediate(target);}}
        static void Fail(Exception ex){EditorApplication.update-=PerformanceTick;Cleanup();File.WriteAllText(Output+"/failure.txt",ex.ToString());UnityEngine.Debug.LogException(ex);EditorApplication.Exit(1);}
    }
}
#endif
