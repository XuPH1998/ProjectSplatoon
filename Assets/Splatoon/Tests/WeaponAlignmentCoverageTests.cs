#if UNITY_EDITOR
using System;
using System.Linq;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Painting;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class WeaponAlignmentCoverageTests
    {
        [SetUp] public void Setup() { HeroMigrationTests.Load(); InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath)); }
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();

        [TestCase(0)] [TestCase(90)]
        public void DirectionDepthAndVisibilityMatchGpuPixelsAndCpuBounds(int rotation)
        {
            var go=new GameObject("Directional coverage validation"); var mesh=new Mesh(); Texture2D read=null;
            try
            {
                mesh.vertices=new[]{new Vector3(-4,0,-4),new Vector3(-4,0,4),new Vector3(4,0,4),new Vector3(4,0,-4)};
                mesh.normals=Enumerable.Repeat(Vector3.up,4).ToArray(); mesh.uv2=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right};
                mesh.triangles=new[]{0,1,2,0,2,3}; mesh.RecalculateBounds();
                go.transform.rotation=Quaternion.Euler(rotation,19,0);
                var surface=go.AddComponent<PaintSurface>(); surface.Resolution=128;surface.ResolutionHeight=128;
                surface.ShapeAtlas=InkShapeAtlas.Texture;surface.PainterShader=Shader.Find("Splatoon/InkTexturePainter");surface.DisplayShader=Shader.Find("Splatoon/InkDisplay");
                go.GetComponent<MeshFilter>().sharedMesh=mesh;
                var matrix=go.transform.localToWorldMatrix;
                var stamp=new PaintStamp{Team=1,Normal=matrix.MultiplyVector(Vector3.up),Direction=matrix.MultiplyVector(new Vector3(1,0,2)),
                    Position=matrix.MultiplyPoint3x4(new Vector3(.3f,0,-.2f)),Radius=1.2f,DepthScale=1.8f,Hardness=.55f,Strength=1,
                    ShapeSeed=InkShapeAtlas.Pack(11,700123),ClipEnabled=true,Clip0=new Vector4(3,1.1f,1.1f,3),Clip1=new Vector4(3,3,.8f,.8f)};
                surface.Apply(stamp);surface.FlushDisplay();
                read=new Texture2D(128,128,TextureFormat.RGBA32,false,true); var previous=RenderTexture.active;
                try { RenderTexture.active=surface.Mask;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply(); }
                finally { RenderTexture.active=previous; }
                var pixels=read.GetPixels32(); var grid=new SurfaceOwnershipGrid(new Vector2(8,8),8f/128,null);
                grid.Apply(stamp,matrix,.5f,.034424f,110);
                int painted=0;
                for(int z=0;z<128;z++)for(int x=0;x<128;x++)
                {
                    var point=matrix.MultiplyPoint3x4(new Vector3((x+.5f)/16-4,0,(z+.5f)/16-4));
                    var expected=InkCoverage.Accumulate(default,1,InkShapeAtlas.Coverage(point,stamp));
                    Assert.That(pixels[z*128+x].r,Is.EqualTo(expected.r).Within(1),$"GPU {x}/{z}");
                    Assert.That(grid.State[(z*128+x)*4],Is.EqualTo(expected.r),$"CPU bounds {x}/{z}");
                    if(expected.r>128)painted++;
                }
                Assert.That(painted,Is.GreaterThan(100));
            }
            finally { if(read!=null)UnityEngine.Object.DestroyImmediate(read);UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(mesh); }
        }
        [TestCase(1)] [TestCase(5)] [TestCase(6)]
        public void BiasAccumulatesOnlyOnShotsAndJumpAgeRestoresAcrossReplay(int hero)
        {
            var w=GameplayConfig.GetWeapon(hero); var s=new PlayerSnapshot{HeroId=hero,Health=100,Ink=100,Grounded=true,Team=1};
            ReferenceSpreadSimulation.Reset(ref s,w);float first=ReferenceSpreadSimulation.Bias(s,w);
            ReferenceSpreadSimulation.After(ref s,new PlayerInputFrame{Fire=true},w,true,true);
            Assert.That(s.LastShotSpreadBias,Is.EqualTo(first));Assert.That(s.DualiesGroundBias,Is.GreaterThanOrEqualTo(first));
            s.Grounded=false;ReferenceSpreadSimulation.Before(ref s,w,0);ReferenceSpreadSimulation.Before(ref s,w,w.ReferenceJumpStart);
            Assert.That(ReferenceSpreadSimulation.Bias(s,w),Is.EqualTo(w.ReferenceJumpBias).Within(.00001));
            using var writer=new Unity.Netcode.FastBufferWriter(4096,Unity.Collections.Allocator.Temp);writer.WriteNetworkSerializable(s);
            using var reader=new Unity.Netcode.FastBufferReader(writer,Unity.Collections.Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot copy);
            for(int i=1;i<=120;i++)
            {
                double time=w.ReferenceJumpStart+i/60.0;
                ReferenceSpreadSimulation.Before(ref s,w,time);ReferenceSpreadSimulation.Before(ref copy,w,time);
                Assert.That(ReferenceSpreadSimulation.Bias(copy,w),Is.EqualTo(ReferenceSpreadSimulation.Bias(s,w)));
            }
            Assert.That(ReferenceSpreadSimulation.Bias(s,w),Is.EqualTo(s.DualiesGroundBias).Within(.00001));
        }
        [TestCase(6)] [TestCase(7)]
        public void CompleteActionsMatchAcrossDriverRates(int hero)
        {
            var a=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"sweep",30,2,null,fullActions:true);
            foreach(int hz in new[]{60,144})
            {
                var b=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"sweep",hz,2,null,fullActions:true);
                Assert.That(b.gridHash,Is.EqualTo(a.gridHash));Assert.That(b.emittedProjectiles,Is.EqualTo(hero==6?132:8));
                Assert.That(b.inkSpent,Is.EqualTo(a.inkSpent));
            }
        }
        [Test] public void EveryAddedFieldChangesFrozenConfigurationSignature()
        {
            var original=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(1).WeaponConfigPath));
            try
            {
                var fields=typeof(WeaponConfigAsset).GetFields(); int start=Array.FindIndex(fields,f=>f.Name=="referenceRules");
                Assert.That(start,Is.GreaterThanOrEqualTo(0));
                foreach(var field in fields.Skip(start).Where(f=>f.DeclaringType==typeof(WeaponConfigAsset)))
                {
                    object before=field.GetValue(original);var frozen=original.Snapshot();
                    if(field.FieldType==typeof(bool))field.SetValue(original,!(bool)before);
                    else if(field.FieldType==typeof(float))field.SetValue(original,(float)before+.123f);
                    else if(field.FieldType==typeof(double))field.SetValue(original,(double)before+.123);
                    else if(field.FieldType==typeof(int))field.SetValue(original,(int)before+1);
                    else continue;
                    Assert.That(original.Snapshot().SameValues(frozen),Is.False,field.Name);field.SetValue(original,before);
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(original);}
        }
        [Test] public void LateAcceptedShotCatchesUpWithoutRepeatingOlderImpacts()
        {
            var w=GameplayConfig.GetWeapon(5);var service=new InkProjectileService();
            var shot=new InkShot{Id=1,HeroId=5,Round=1,Team=1,Seed=2,Configuration=w,Origin=new Vector3(4000,4000,4000),Velocity=Vector3.forward*w.SpeedMin};
            service.SpawnForMeasurement(shot);service.Simulate(.25);
            shot.Id=2;shot.Origin+=Vector3.right*10;service.SpawnForMeasurement(shot);service.Simulate(.25);
            Assert.That(service.Explosions.Select(x=>x.ShotId),Is.EquivalentTo(new uint[]{1,2}));
            service.Simulate(6);Assert.That(service.Explosions.Count,Is.EqualTo(2));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void SourceMovementTargetsReachTheActualMotor(int hero)
        {
            var floor=new GameObject("Alignment motor floor");var root=new GameObject("Alignment motor probe"){layer=8};
            try
            {
                floor.transform.position=new Vector3(6000,0,6000);var box=floor.AddComponent<BoxCollider>();box.size=new Vector3(100,1,100);box.center=Vector3.down*.5f;
                var surface=floor.AddComponent<PaintSurface>();surface.Scores=true;surface.WalkableSize=new Vector2(100,100);surface.InitializeOwnership(.5f);
                for(int i=0;i<surface.Ownership.Cells.Length;i++)surface.Ownership.Set(i,1);
                var cc=root.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;cc.skinWidth=.03f;
                var motor=new PlayerMotorSimulation(cc);
                foreach(bool swim in new[]{false,true})
                {
                    var state=new PlayerSnapshot{HeroId=hero,Team=1,Health=100,Ink=100,Grounded=true,Position=floor.transform.position+Vector3.up*.04f};
                    motor.Restore(state);Physics.SyncTransforms();
                    for(int f=0;f<90;f++)motor.Step(ref state,new PlayerInputFrame{Move=Vector2.up,Swim=swim},1f/60,f/60.0,false);
                    Assert.That(state.PlanarVelocity.magnitude,Is.EqualTo(swim?GameplayConfig.GetHero(hero).SwimSpeed:GameplayConfig.GetHero(hero).MoveSpeed).Within(.001));
                    Assert.That(state.Swimming,Is.EqualTo(swim));
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(floor);Physics.SyncTransforms();}
        }
    }
}
#endif
