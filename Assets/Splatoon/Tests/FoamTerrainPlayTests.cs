#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class FoamTerrainPlayTests
    {
        [UnityTearDown]public IEnumerator Cleanup()
        {
            if(!Application.isPlaying)yield break;
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static IEnumerator Wait(Func<bool> ready,string label)
        {
            double end=Time.realtimeSinceStartupAsDouble+40;
            while(!ready()&&Time.realtimeSinceStartupAsDouble<end)yield return null;
            Assert.That(ready(),Is.True,label);
        }
        [UnityTest]public IEnumerator AuthoredMapAllWeaponsCollisionMotorAndRoundReset()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready,"Boot and resources");
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom,Is.True,app.Error);
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null,"Host player");
            app.CaptureMouse(false);
            var match=PrototypeMatch.Current;var arena=match.Arena;var world=arena.Foam;var host=PrototypePlayer.Local;
            match.enabled=false;host.enabled=false;
            Assert.That(world,Is.Not.Null);Assert.That(world.Patches.Count,Is.GreaterThanOrEqualTo(15));
            var evidence=new System.Text.StringBuilder();
            foreach(var patch in world.Patches.Values)
            {
                if(patch.Surface.name.StartsWith("Ramp_")&&patch.Surface.Scores)
                {
                    Assert.That(patch.Neighbours.Any(p=>p.Surface.name.StartsWith("Ground_")),Is.True,"Ramp must join the floor: "+patch.Surface.name);
                    Assert.That(patch.Neighbours.Any(p=>p.Surface.name.StartsWith("Platform_")&&p.Surface.Scores),Is.True,"Ramp must join the platform: "+patch.Surface.name);
                }
                evidence.AppendLine(patch.Surface.name+":"+patch.Key+" neighbours="+string.Join(",",patch.Neighbours.Select(p=>p.Surface.name)));
            }
            var location=new Vector3(2,0,-12);
            Assert.That(Physics.Raycast(location+Vector3.up*2,Vector3.down,out var ground,3,PlayerMotorSimulation.WorldMask),Is.True);
            var surface=ground.collider.GetComponentInParent<PaintSurface>();Assert.That(surface.Scores,Is.True);
            location=ground.point;
            uint id=200000;
            for(int hero=1;hero<=8;hero++)
            {
                world.Clear();var service=new InkProjectileService();var config=GameplayConfig.GetWeapon(hero);
                double born=host.NetworkManager.ServerTime.Time;
                for(int shot=0;shot<8;shot++)
                    service.SpawnForMeasurement(new InkShot{Id=++id,ActionId=id,Round=match.State.Value.Round,Shooter=host.PlayerId,HeroId=hero,Team=1,Seed=id,Born=born,
                        Origin=location+Vector3.up*2,Velocity=Vector3.down*20,Charge=1,Configuration=config,ConfigurationRevision=WeaponConfigService.Current.Revision(hero)});
                for(int tick=1;tick<=180;tick++){service.Simulate(born+tick/60d);if(tick%3==0)world.Commit();}
                Assert.That(world.Revision,Is.GreaterThan(0),"Weapon "+hero+" must create terrain through projectile paint");
                Assert.That(world.TryPatch(surface,location,Vector3.up,out var patch),Is.True);
                patch.Sample(location,out var top,out _,out byte owner);Assert.That(top.y,Is.GreaterThan(location.y+.001f),"Weapon "+hero);
                Assert.That(owner,Is.EqualTo(1));evidence.AppendLine("weapon="+hero+" top="+top.y+" revision="+world.Revision);
                service.Clear();
            }
            world.Clear();
            var stamp=new PaintStamp{SurfaceId=surface.SurfaceId,Position=location,Normal=Vector3.up,Team=1,Radius=2,DepthScale=1,Hardness=1,Strength=1,ShapeSeed=InkShapeAtlas.Pack(0,123)};
            for(int i=0;i<35;i++){world.Queue(surface,stamp,.25f);world.Commit();}
            world.TryPatch(surface,location,Vector3.up,out var support);support.Sample(location,out var initialTop,out _,out _);
            var solver=new TpsAimSolver();Assert.That(solver.ClosestCast(initialTop-Vector3.up*.02f,Vector3.forward,2,0,host.PlayerId,out var insideHit),Is.True);
            Assert.That(insideHit.Collider.GetComponent<FoamChunk>(),Is.Not.Null,"Embedded ray must hit solid foam");
            var controller=host.GetComponent<CharacterController>();
            var motor=new PlayerMotorSimulation(controller);var state=new PlayerSnapshot{HeroId=1,Health=100,Ink=100,Team=1,Position=initialTop+Vector3.up*.03f,Grounded=true};
            motor.Restore(state);double time=0;
            for(int tick=0;tick<15;tick++)motor.Step(ref state,default,1f/60,time+=1f/60,false);
            Assert.That(state.Grounded,Is.True);Assert.That(state.FoamSupportRegionKey,Is.EqualTo(support.Key));
            float oldFeet=state.Position.y;
            for(int i=0;i<4;i++){world.Queue(surface,stamp,.25f);world.Commit();motor.Step(ref state,default,1f/60,time+=1f/60,false);}
            Assert.That(state.Position.y,Is.GreaterThan(oldFeet+.01f),"Grounded character follows rising support");
            var input=new PlayerInputFrame{Swim=true};for(int tick=0;tick<30;tick++)motor.Step(ref state,input,1f/60,time+=1f/60,false);
            Assert.That(state.Swimming&&state.Grounded,Is.True,"Paper remains grounded on foam");
            FoamSupport.Sample(support,state,host.SwimBody.Profile,out var paperTop,out _,out _);
            Assert.That(state.Position.y,Is.EqualTo(paperTop.y).Within(.04f));
            evidence.AppendLine("human and paper rise support PASS; paper feet="+state.Position.y);
            host.Snapshot.Value=state;host.SwimBody.ApplyCollision(state);
            var camera=Camera.main;camera.transform.position=location+new Vector3(5,4,-6);camera.transform.LookAt(initialTop);
            Directory.CreateDirectory("Reports/FoamTerrain");
            var target=new RenderTexture(1280,720,24);var image=new Texture2D(1280,720,TextureFormat.RGB24,false);var previous=RenderTexture.active;
            try
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,new UnityEngine.Rendering.RenderPipeline.StandardRequest{destination=target});
                RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();File.WriteAllBytes("Reports/FoamTerrain/foam-playmode.png",image.EncodeToPNG());
            }
            finally{RenderTexture.active=previous;target.Release();UnityEngine.Object.Destroy(target);UnityEngine.Object.Destroy(image);}
            // New growth must respect the real actor and its dynamic headroom at commit time.
            arena.ClearPaint();state.Swimming=state.CompactBody=false;state.Grounded=true;state.Movement=MovementMode.Human;state.FoamSupportRegionKey=0;state.Position=location+Vector3.up*.03f;
            motor.Restore(state);host.Snapshot.Value=state;
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.name="Foam ceiling acceptance";
            roof.transform.localScale=new Vector3(3,.1f,3);roof.transform.position=location+Vector3.up*(HeroBodyShape.For(1).Height+.18f);Physics.SyncTransforms();
            var commit=typeof(PrototypeMatch).GetMethod("CommitFoam",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            try
            {
                world.Queue(surface,stamp,10);commit.Invoke(match,new object[]{true});support.Sample(location,out var limited,out _,out _);
                Assert.That(limited.y-location.y,Is.LessThan(.2f),"Actor headroom limits the deposit before collision installation");
            }
            finally{UnityEngine.Object.Destroy(roof);}
            yield return null;
            arena.ClearPaint();typeof(PrototypeMatch).GetMethod("ResetFoamSync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(match,null);
            state.Position=location+Vector3.up*1.1f;state.Grounded=false;state.Movement=MovementMode.Air;state.FoamSupportRegionKey=0;
            motor.Restore(state);host.Snapshot.Value=state;world.Queue(surface,stamp,15);commit.Invoke(match,new object[]{true});
            support.Sample(location,out var belowAir,out _,out _);Assert.That(belowAir.y,Is.LessThan(state.Position.y),"Growing foam cannot swallow an airborne actor");
            evidence.AppendLine("dynamic headroom and airborne growth caps PASS");
            arena.ClearPaint();Assert.That(world.Revision,Is.Zero);Assert.That(world.PendingCount,Is.Zero);
            Assert.That(world.Capture().All(v=>v==0),Is.True);evidence.AppendLine("round terrain clear PASS");
            File.WriteAllText("Reports/FoamTerrain/playmode-evidence.txt",evidence.ToString());
        }
    }
}
#endif
