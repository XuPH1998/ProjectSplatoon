#if UNITY_EDITOR
using System.IO;
using System;
using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Splatoon.Networking;
using UnityEditor;
using Unity.Collections;
using Unity.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class WeaponAlignmentTests
    {
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        [Test] public void ConfigurationTargets()
        {
            const double scale = 18.0 / 24.037;
            for (int i=1;i<=7;i++)
            {
                var w=GameplayConfig.GetWeapon(i); WeaponConfigValidation.Validate(w);
                Assert.That(w.ReferenceRules, Is.True);
                var hero=GameplayConfig.GetHero(i);
                Assert.That(hero.MoveSpeed,Is.EqualTo((i==4?.104:i==6||i==3?.088:.096)*60*scale).Within(.00001));
                Assert.That(hero.SwimSpeed,Is.EqualTo((i==4?.2016:i==6||i==3?.1728:.192)*60*scale).Within(.00001));
            }
            Assert.That(GameplayConfig.GetWeapon(1).FireRate,Is.EqualTo(10));
            Assert.That(GameplayConfig.GetWeapon(1).ShotInk,Is.EqualTo(.92f));
            Assert.That(GameplayConfig.GetWeapon(4).DamageReduceStartSeconds,Is.EqualTo(4.0/60).Within(1e-8));
            var spinner=GameplayConfig.GetWeapon(6);
            Assert.That(spinner.SplatlingFirstChargeSeconds,Is.EqualTo(2));
            Assert.That(SplatlingSimulation.Rounds(spinner,2),Is.EqualTo(33));
            Assert.That(SplatlingSimulation.Rounds(spinner,2.5),Is.EqualTo(66));
            Assert.That(spinner.ShotInk*66,Is.EqualTo(35).Within(.0001));
            Assert.That(GameplayConfig.GetWeapon(5).CollisionExplosionPaintRadius,Is.EqualTo(2.3*scale).Within(.00001));
            Assert.That(GameplayConfig.GetWeapon(7).Damage,Is.EqualTo(32));
        }
        [TestCase(1,108)] [TestCase(2,71)] [TestCase(4,125)] [TestCase(5,14)] [TestCase(7,48)]
        public void ActualSimulationEmitsExactFullTankCount(int hero,int expected)
        {
            var w=GameplayConfig.GetWeapon(hero);
            var s=new PlayerSnapshot {HeroId=hero,Health=100,Ink=100,Grounded=true,Team=1};
            for(int frame=0;frame<3000;frame++) WeaponSimulation.Step(ref s,new PlayerInputFrame {Fire=true,FireSequence=1,Sequence=(uint)frame+1},w,frame/60.0,false,true);
            Assert.That(s.ShotSequence,Is.EqualTo(expected));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void PhasedTrajectoryMatchesIndependentFrameIntegrator(int hero)
        {
            var w=GameplayConfig.GetWeapon(hero);
            foreach(float pitch in new[]{-30f,0f,30f})
            {
                Vector3 initial=Quaternion.Euler(pitch,0,0)*Vector3.forward*((w.SpeedMin+w.SpeedMax)*.5f);
                var position=Vector3.zero;var velocity=initial;
                int straight=(int)Math.Round(w.StraightSeconds*60),brake=(int)Math.Round(w.BrakeSeconds*60);
                int count=Math.Min(120,(int)(w.Lifetime*60));
                for(int frame=0;frame<count;frame++)
                {
                    if(frame==straight)velocity=Vector3.ClampMagnitude(velocity,w.ReferenceBrakeEndSpeed);
                    if(frame>=straight)
                    {float drag=frame<straight+brake?w.ReferenceBrakeDrag:w.ReferenceFreeDrag;float g=frame<straight+brake?w.ReferenceBrakeGravity:w.ProjectileGravity;velocity=velocity*(1-drag)+Vector3.down*(g/60);}
                    position+=velocity/60;
                    Assert.That(Vector3.Distance(InkBallistics.Position(Vector3.zero,initial,w,(frame+1)/60.0),position),Is.LessThanOrEqualTo(.0001),$"{hero}/{pitch}/{frame}");
                }
            }
        }
        [Test] public void BubbleCadenceGrowthAndMotionArePerProjectile()
        {
            var w=GameplayConfig.GetWeapon(7);var s=new PlayerSnapshot{HeroId=7,Health=100,Ink=100,Grounded=true,Team=1};
            var frames=new System.Collections.Generic.List<int>();
            for(int f=0;f<61;f++)if(WeaponSimulation.Step(ref s,new PlayerInputFrame{Fire=true,FireSequence=1,Sequence=(uint)f+1},w,f/60.0,false,true))frames.Add(f);
            CollectionAssert.AreEqual(new[]{6,11,16,21,38,43,48,53},frames);
            float prior=float.PositiveInfinity;
            for(byte i=0;i<4;i++)
            {
                var shot=new InkShot{Configuration=w,VolleyIndex=i,Velocity=ReferenceBallistics.BubbleLaunch(Vector3.forward,w,i,true)};
                Assert.That(shot.Velocity.z,Is.LessThan(prior));prior=shot.Velocity.z;
                Assert.That(shot.Velocity.y/shot.Velocity.z,Is.EqualTo(.2f).Within(.00001));
                Assert.That(ReferenceBallistics.BubbleRadius(shot,0,0,true),Is.EqualTo(ReferenceBallistics.BubbleRadius(shot,1,0,true)*.1f).Within(.00001));
                var segment=InkBounce.Initial(shot);
                Assert.That(segment.PositionAt(.2,shot),Is.EqualTo(InkBallistics.Position(shot,w,.2)));
            }
        }
        [Test] public void DirectionalPaintAndClippingRoundTrip()
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            var stamp=new PaintStamp {Normal=Vector3.up,Direction=Vector3.forward,Radius=1,DepthScale=2,Hardness=.55f,Strength=1,
                ShapeSeed=InkShapeAtlas.Pack(7,16000),ClipEnabled=true,Clip0=Vector4.one*1.5f,Clip1=Vector4.one*2.5f};
            using var writer=new FastBufferWriter(256,Allocator.Temp);writer.WriteNetworkSerializable(stamp);
            using var reader=new FastBufferReader(writer,Allocator.Temp);reader.ReadNetworkSerializable(out PaintStamp restored);
            Assert.That(restored.Direction,Is.EqualTo(stamp.Direction));Assert.That(restored.DepthScale,Is.EqualTo(2));
            Assert.That(restored.Clip1,Is.EqualTo(stamp.Clip1));
            for(float x=-2;x<2;x+=.125f)for(float z=-2;z<2;z+=.125f)
                Assert.That(InkShapeAtlas.Coverage(new Vector3(x,0,z),restored),Is.EqualTo(InkShapeAtlas.Coverage(new Vector3(x,0,z),stamp)));
            stamp.ClipEnabled=false;var round=stamp;round.DepthScale=1;
            for(float x=-1;x<=1;x+=.125f)for(float z=-1;z<=1;z+=.125f)
                Assert.That(InkShapeAtlas.Coverage(new Vector3(x,0,z*2),stamp),Is.EqualTo(InkShapeAtlas.Coverage(new Vector3(x,0,z),round)).Within(.00001));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void DriverRatesHaveIdenticalCoverage(int hero)
        {
            var a=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"continuous",30,8,null);
            foreach(int hz in new[]{60,144})
            {
                var b=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"continuous",hz,8,null);
                Assert.That(b.gridHash,Is.EqualTo(a.gridHash),$"{hero}/{hz}");Assert.That(b.paintStamps,Is.EqualTo(a.paintStamps));
            }
        }
        [Test] public void ShotgunPreservesFrozenPhysicsAndPaintBaseline()
        {
            WeaponConfigService.Current.SetForEditor(3, LegacyShotgunFixture.Create());
            foreach(string scenario in new[]{"flat","high-drop","wall-middle","continuous"})
            {
                var old=JsonUtility.FromJson<WeaponReferenceMeasurements.Result>(File.ReadAllText($"Tools/ValidationData/WeaponAlignment/Baseline/Measurements/w3-q0-{scenario}-60.json"));
                var current=WeaponReferenceMeasurements.Capture(3,0,scenario,60,scenario=="continuous"?20:1,null);
                Assert.That(current.gridHash,Is.EqualTo(old.gridHash),scenario);Assert.That(current.paintStamps,Is.EqualTo(old.paintStamps));
                Assert.That(current.maxOwnedForward,Is.EqualTo(old.maxOwnedForward));
            }
        }
        [Test] public void ReferenceParametersAreFrozenAndSigned()
        {
            var asset=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(1).WeaponConfigPath));
            try
            {
                var old=asset.Snapshot();asset.referenceBrakeGravity+=1;asset.paintDepthMax+=.2f;
                Assert.That(asset.Snapshot().SameValues(old),Is.False);
                Assert.That(old.ReferenceBrakeGravity,Is.EqualTo(GameplayConfig.GetWeapon(1).ReferenceBrakeGravity));
                Assert.That(old.RequiresRestart(asset.Snapshot()),Is.False);
                asset.referenceRules=false;Assert.That(old.RequiresRestart(asset.Snapshot()),Is.True);
            }
            finally{UnityEngine.Object.DestroyImmediate(asset);}
        }
        [Test, Explicit("Writes the review measurement artifacts")] public void CaptureAfter()
        {
            const string root="Reports/WeaponAlignment/After";
            Directory.CreateDirectory(root+"/Direct"); Directory.CreateDirectory(root+"/Actions");
            for(int hero=1;hero<=7;hero++)
            {
                foreach(string scenario in new[]{"flat","high-drop","wall-middle","continuous"})
                    WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,scenario,60,scenario=="continuous"?20:1,root+"/Direct");
                var w=GameplayConfig.GetWeapon(hero);
                foreach(string scenario in new[]{"flat","high-drop","wall-middle","continuous","moving","sweep"})
                {
                    var r=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,scenario,60,scenario=="continuous"||scenario=="moving"||scenario=="sweep"?(hero==6?2:20):1,root+"/Actions",fullActions:true);
                    Assert.That(r.impacts,Is.EqualTo(r.emittedProjectiles)); Assert.That(r.ownedArea,Is.GreaterThan(0));
                }
                if(hero!=6)
                {
                    int count=Mathf.FloorToInt((100+PrototypeRules.InkTolerance)/w.ShotInk);
                    var tank=WeaponReferenceMeasurements.Capture(hero,0,"tank",60,count,root+"/Actions",fullActions:true,fullTank:true);
                    Assert.That(tank.emittedProjectiles,Is.EqualTo(count*w.PelletCount*(WeaponSimulation.IsBubble(w)?4:1)));
                }
            }
            var spinner=GameplayConfig.GetWeapon(6);
            foreach(float charge in new[]{HeroFlatRange.MinimumCharge(spinner),(float)(spinner.SplatlingFirstChargeSeconds/spinner.ChargeSeconds)})
            {
                var measured=WeaponReferenceMeasurements.Capture(6,charge,"flat",60,1,root+"/Actions",fullActions:true);
                Assert.That(measured.emittedProjectiles,Is.EqualTo(charge<.1f?1:33));
            }
        }
        [Test, Explicit("Replays frozen sources to render the old ownership grid; never overwrites baseline")] public void RenderFrozenBaseline()
        {
            HeroMigrationTests.Load(rows=>
            {
                foreach(var before in SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponAlignment/Baseline/Values.json")).Children)
                {
                    var row=rows.Children.Single(r=>r["id"].AsInt==before["id"].AsInt);
                    WeaponTimeFixture.ToReferenceFrames(before);
                    foreach(var key in before.Keys)row[key]=before[key];
                    row["referenceRules"]=false;row["referenceSpreadEnabled"]=false;
                    if(!before.HasKey("motionMode"))row["motionMode"]=0;
                }
            });
            const string output="Reports/WeaponAlignment/BeforeReplayed";Directory.CreateDirectory(output);
            for(int hero=1;hero<=7;hero++)foreach(string scenario in new[]{"flat","high-drop","wall-middle","continuous"})
            {
                var result=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,scenario,60,scenario=="continuous"?20:1,output);
                var before=JsonUtility.FromJson<WeaponReferenceMeasurements.Result>(File.ReadAllText($"Tools/ValidationData/WeaponAlignment/Baseline/Measurements/w{hero}-q{(hero==6?60:0)}-{scenario}-60.json"));
                Assert.That(result.gridHash,Is.EqualTo(before.gridHash),$"{hero}/{scenario}");
            }
        }
        [Test, Explicit("Measures three charge releases from a real 100-ink tank, including the retained slow refill rule")] public void CaptureSpinnerTank()
        {
            const string output="Reports/WeaponAlignment/After/Actions";Directory.CreateDirectory(output);
            var result=WeaponReferenceMeasurements.Capture(6,1,"tank",60,3,output,fullActions:true,fullTank:true);
            Assert.That(result.emittedProjectiles,Is.GreaterThan(132));Assert.That(result.impacts,Is.EqualTo(result.emittedProjectiles));
            File.WriteAllText(output+"/spinner-tank-notes.txt",$"100 initial ink, no ResourceSimulation/refill calls; 3 charge-release cycles. The existing empty-ink slow-charge rule supplies ink internally. Emitted={result.emittedProjectiles}; net tank debit={result.inkSpent:R}; generated reservation={Math.Max(0,result.emittedProjectiles*GameplayConfig.GetWeapon(6).ShotInk-result.inkSpent):R}. This is not a finite maximum-magazine claim.");
        }
        [Test, Explicit("Only run before implementing a new baseline; frozen evidence must not be overwritten.")] public void CaptureBaseline()
        {
            const string output = "Reports/WeaponAlignment/Before";
            Assert.That(Directory.Exists(output), Is.False, "Baseline is immutable; do not overwrite it.");
            Directory.CreateDirectory(output);
            WeaponReferenceMeasurements.LoadTables();
            for (int hero = 1; hero <= 7; hero++)
                foreach (string scenario in new[] { "flat", "high-drop", "wall-middle", "continuous" })
                    WeaponReferenceMeasurements.Capture(hero, hero == 6 ? 1 : 0, scenario, 60, scenario == "continuous" ? 20 : 1, output);
            File.WriteAllText(output + "/conditions.txt", "Pre-change direct Spawn measurements, 1/20 projectiles (not complete bubble groups or spinner charge cycles); Unity " + Application.unityVersion);
        }
    }
}
#endif
