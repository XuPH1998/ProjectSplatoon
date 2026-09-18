#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class PaintParityTests
    {
        public const string Root = "Reports/PaintParity1130";
        [SetUp] public void Setup() => WeaponReferenceMeasurements.LoadTables();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static WeaponReferenceMeasurements.Options Options(int seed, double window = 0, bool moving = false) =>
            new() { Seed = InkShapeAtlas.Hash((uint)seed + 0x51a70000u), EmissionWindow = window, ConstantMotion = moving };

        [TestCase(1)] [TestCase(4)] [TestCase(6)]
        public void DirectFixtureUsesDifferentShotSeeds(int hero)
        {
            var legacy=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"flat",60,20,null,
                options:new WeaponReferenceMeasurements.Options { LegacyDirectIdentity=true });
            var fixedSample=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"flat",60,20,null);
            Assert.That(legacy.uniqueShotSeeds,Is.EqualTo(1),"Reproduce the historical fixture defect");
            Assert.That(fixedSample.uniqueShotSeeds,Is.EqualTo(20));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void CompleteActionsAreDeterministicAcrossDrivers(int hero)
        {
            float charge=hero==6?1:0;
            var a=WeaponReferenceMeasurements.Capture(hero,charge,"flat",30,3,null,fullActions:true,options:Options(17));
            foreach(int hz in new[]{60,144})
            {
                var b=WeaponReferenceMeasurements.Capture(hero,charge,"flat",hz,3,null,fullActions:true,options:Options(17));
                Assert.That(b.gridHash,Is.EqualTo(a.gridHash),$"hero={hero} hz={hz}");
                Assert.That(b.emittedProjectiles,Is.EqualTo(a.emittedProjectiles));
                Assert.That(b.inkSpent,Is.EqualTo(a.inkSpent));
                Assert.That(b.firstPaintSeconds,Is.EqualTo(a.firstPaintSeconds).Within(1e-7));
                Assert.That(b.paintPayloadBytes,Is.EqualTo(a.paintPayloadBytes));
            }
        }
        [Test] public void EmptySpinnerWindowAndResourceCycleAreExplicit()
        {
            var empty=WeaponReferenceMeasurements.Capture(6,1,"flat",60,1,null,fullActions:true,options:Options(0,1));
            Assert.That(empty.emittedProjectiles,Is.Zero); Assert.That(empty.firstPaintSeconds,Is.EqualTo(-1));
            var cycle=WeaponReferenceMeasurements.Capture(6,1,"tank",60,3,null,fullActions:true,fullTank:true,options:Options(0));
            Assert.That(cycle.resourceMode,Is.EqualTo("resource-cycle"));Assert.That(cycle.inkGenerated,Is.GreaterThan(0));
            Assert.That(cycle.inkSpent,Is.GreaterThan(100));
        }

        [Test, Timeout(900000), Explicit("Frozen project measurements; does not change gameplay configuration")]
        public void CaptureBefore() => Capture("Before");
        [Test, Timeout(900000), Explicit("Post-change project measurements; original-game acceptance remains separate")]
        public void CaptureAfter() => Capture("After");
        [Test, Explicit("Supplement unchanged Explosher baseline with physical velocity inheritance")]
        public void CaptureInheritedMotionBefore() => CaptureInheritedMotion("Before");
        [Test, Explicit("Validate completed captures without overwriting frozen evidence")]
        public void ValidateCompletedCaptures()
        {
            foreach(string stage in new[]{"Before","After"})
            {
                Assert.That(File.Exists(Root+"/"+stage+"/complete.txt"),Is.True);
                var files=Directory.GetFiles(Root+"/"+stage,"seed-*.json",SearchOption.AllDirectories);
                Assert.That(files.Length,Is.EqualTo(3300),stage);
                foreach(var cohort in files.GroupBy(Path.GetDirectoryName))
                {
                    Assert.That(cohort.Count(),Is.EqualTo(30),cohort.Key);
                    var samples=cohort.Select(p=>JsonUtility.FromJson<WeaponReferenceMeasurements.Result>(File.ReadAllText(p))).ToArray();
                    Assert.That(samples.Select(s=>s.sampleSeed).Distinct().Count(),Is.EqualTo(30),cohort.Key);
                    Assert.That(samples.All(s=>s.floorArea>=0&&s.inkSpent>=0),Is.True,cohort.Key);
                }
            }
        }
        static void CaptureInheritedMotion(string stage)
        {
            string folder=Root+"/"+stage+"/w3/moving-inherited";Directory.CreateDirectory(folder);
            Assert.That(File.Exists(folder+"/seed-29.json"),Is.False,"Do not overwrite a complete cohort");
            for(int seed=0;seed<30;seed++)
            {
                var opt=Options(seed,3,true);opt.InheritMotion=true;
                var result=WeaponReferenceMeasurements.Capture(3,0,"moving",60,64,seed==0?folder:null,fullActions:true,options:opt);
                File.WriteAllText($"{folder}/seed-{seed:D2}.json",JsonUtility.ToJson(result,true));
            }
        }
        static void Capture(string stage)
        {
            string output=Root+"/"+stage;Directory.CreateDirectory(output);
            if(File.Exists(output+"/complete.txt"))throw new InvalidOperationException("Completed capture is immutable: "+output);
            var all=new List<WeaponReferenceMeasurements.Result>();
            for(int hero=1;hero<=7;hero++)
            {
                var w=GameplayConfig.GetWeapon(hero);float charge=hero==6?1:0;
                var cases=new List<(string name,string scenario,int actions,double window,float charge,bool tank)>();
                foreach(string scenario in new[]{"flat","up30","down30","high-drop","slope","wall-near","wall-middle","wall-far","occluded"})
                    cases.Add((scenario,scenario,1,0,charge,false));
                cases.Add(("three","flat",3,0,charge,false));
                cases.Add(("one-second","flat",64,1,charge,false));
                cases.Add(("three-seconds","flat",64,3,charge,false));
                cases.Add(("moving","moving",64,3,charge,false));
                cases.Add(("sweep","sweep",64,3,charge,false));
                cases.Add((hero==6?"resource-cycle":"tank","tank",hero==6?3:Mathf.FloorToInt((100+PrototypeRules.InkTolerance)/w.ShotInk),0,charge,true));
                if(hero==6)
                {
                    cases.Add(("minimum-charge","flat",1,0,HeroFlatRange.MinimumCharge(w),false));
                    cases.Add(("first-ring","flat",1,0,(float)(w.SplatlingFirstChargeSeconds/w.ChargeSeconds),false));
                    cases.Add(("moving-loaded","moving",1,0,(float)(w.SplatlingFirstChargeSeconds/w.ChargeSeconds),false));
                    cases.Add(("sweep-loaded","sweep",1,0,(float)(w.SplatlingFirstChargeSeconds/w.ChargeSeconds),false));
                }
                foreach(var c in cases)
                {
                    string folder=$"{output}/w{hero}/{c.name}";Directory.CreateDirectory(folder);
                    for(int seed=0;seed<30;seed++)
                    {
                        var opt=Options(seed,c.window,c.scenario=="moving"||c.scenario=="sweep");
                        var r=WeaponReferenceMeasurements.Capture(hero,c.charge,c.scenario,60,c.actions,seed==0?folder:null,fullActions:true,fullTank:c.tank,options:opt);
                        File.WriteAllText($"{folder}/seed-{seed:D2}.json",JsonUtility.ToJson(r,true));all.Add(r);
                    }
                    File.WriteAllText(Root+"/capture-progress.txt",$"{stage} hero {hero}: {c.name}; samples={all.Count}; {DateTime.UtcNow:O}");
                }
            }
            File.WriteAllText(output+"/complete.txt",$"{all.Count} project samples; targetValidated=false; {DateTime.UtcNow:O}");
            if(stage=="After")CaptureInheritedMotion(stage);
        }

        [Test] public void MeasureAtlasThroughActualOwnershipRules()
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            Directory.CreateDirectory(Root);
            var csv=new System.Text.StringBuilder("tile,hardness,strength,threshold,nominalRadius,ownedArea,ownedWidth,ownedDepth,idealDiskRatio\n");
            for(int tile=0;tile<InkShapeAtlas.Count;tile++)
            {
                var grid=new SurfaceOwnershipGrid(new Vector2(2,2),2f/256,null);
                grid.Apply(new PaintStamp{Position=Vector3.zero,Normal=Vector3.up,Radius=1,DepthScale=1,ShapeSeed=(uint)tile,Team=1,Hardness=.55f,Strength=1},Matrix4x4.identity,
                    GameplayConfig.Global.PaintThreshold,GameplayConfig.Global.PaintWorldUvScale,GameplayConfig.Global.PaintShapeNoiseScale);
                var points=Enumerable.Range(0,grid.Cells.Length).Where(i=>grid.Cells[i]==1).Select(grid.Center).ToArray();
                Assert.That(points.Length,Is.GreaterThan(0));
                csv.AppendLine(FormattableString.Invariant($"{tile},0.55,1,{GameplayConfig.Global.PaintThreshold},1,{grid.PinkArea},{points.Max(p=>p.x)-points.Min(p=>p.x)+grid.CellSize},{points.Max(p=>p.z)-points.Min(p=>p.z)+grid.CellSize},{grid.PinkArea/Math.PI}"));
            }
            File.WriteAllText(Root+"/atlas.csv",csv.ToString());
        }
    }
}
#endif
