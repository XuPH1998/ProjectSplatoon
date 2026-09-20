#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class SplooshGirlTests
    {
        readonly List<GameObject> objects = new();
        WeaponRuntimeConfig W => GameplayConfig.GetWeapon(8);
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup()
        { foreach (var go in objects) if (go != null) Object.DestroyImmediate(go); objects.Clear(); LubanConfigService.Current.Reset(); }
        static PlayerSnapshot Alive() => new() { HeroId=8,Health=100,Ink=100,Grounded=true,Team=1,Revision=1 };
        bool Tick(ref PlayerSnapshot s, int t, bool held=true, uint press=1, bool emerged=false) => WeaponSimulation.Step(ref s,
            new PlayerInputFrame { Fire=held,FireSequence=press,ReleaseSequence=held?0u:press,Sequence=(uint)t+1 },W,t/60.0,emerged,true);
        [Test] public void ReferenceValuesAndIndependentAssetsAreValid()
        {
            Assert.That(GameplayConfig.GetHero(8).DisplayName,Is.EqualTo("铃芽"));
            WeaponConfigValidation.Validate(W);
            var p=SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SplooshGirl/WeaponShooterShort.1130.json"))["GameParameters"];
            Assert.That(W.Damage,Is.EqualTo(p["DamageParam"]["ValueMax"].AsFloat/10));
            Assert.That(W.ShotInk,Is.EqualTo(p["WeaponParam"]["InkConsume"].AsFloat*100).Within(1e-6));
            Assert.That(WeaponSimulation.FireInterval(W),Is.EqualTo(5.0/60).Within(1e-8));
            Assert.That(W.Ammo.AmmoId,Is.EqualTo(8));
            for(int id=1;id<=7;id++)Assert.That(GameplayConfig.GetWeapon(id).ShooterDetails,Is.False);
            var v=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/SplooshGirl/Prefabs/SplooshGirlVisual.prefab").GetComponent<InkCharacterView>();
            Assert.That(v.Animator.avatar.isHuman&&v.Animator.avatar.isValid,Is.True);Assert.That(v.Profile.Paper.Size,Is.EqualTo(new Vector2(.75f,1.5f)));
            Assert.That(v.Profile.MuzzlePosition.y,Is.InRange(.65f,1.4f));
        }
        [TestCase(false,2)] [TestCase(true,8)]
        public void TankCadenceDamageAndIndependentRecovery(bool emerged,int first)
        {
            var s=Alive();var shots=new List<int>();
            for(int tick=0;tick<800;tick++) if(Tick(ref s,tick,emerged:emerged&&tick==0))shots.Add(tick);
            Assert.That(shots,Is.EqualTo(Enumerable.Range(0,125).Select(i=>first+i*5).ToArray()));
            Assert.That(s.Ink,Is.EqualTo(0).Within(.001));
            Assert.That(s.InkRecoverAt,Is.EqualTo((shots.Last()+15)/60.0).Within(1e-7));
            Assert.That(s.AttackRecoveryUntil,Is.EqualTo((shots.Last()+2)/60.0).Within(1e-7));
            Assert.That(WeaponSimulation.Damage(W,0,0),Is.EqualTo(38));
            Assert.That(WeaponSimulation.Damage(W,22.0/60),Is.EqualTo(19));
            Assert.That(Mathf.CeilToInt(100/W.Damage),Is.EqualTo(3));
        }
        [Test] public void ReleaseResetsCycleAndDeterministicFractionalBudget()
        {
            var s=Alive();var count=new List<int>();var feet=new List<int>();
            for(int t=0;t<51;t++)if(Tick(ref s,t))
            {ShooterDetailSimulation.Schedule(s.BurstShotIndex,W,out int n,out _,out bool foot);count.Add(n);if(foot)feet.Add((int)s.BurstShotIndex);}
            Assert.That(count.Take(5).Sum(),Is.EqualTo(7));Assert.That(count.Take(10).Sum(),Is.EqualTo(14));
            Assert.That(feet,Is.EqualTo(new[]{5,10}));
            Tick(ref s,52,false);Tick(ref s,53,false);for(int t=54;t<=56;t++)Tick(ref s,t,true,2);
            Assert.That(s.BurstShotIndex,Is.EqualTo(1));
            ShooterDetailSimulation.Schedule(s.BurstShotIndex,W,out int start,out float distance,out bool startFoot);
            Assert.That(start,Is.EqualTo(1));Assert.That(startFoot,Is.False);Assert.That(distance,Is.EqualTo(W.ReferenceTrailStart));
        }
        [Test] public void MovementInheritanceAndJumpBiasUseSnapshotState()
        {
            var initial=Vector3.forward*W.SpeedMin;
            var moving=ShooterDetailSimulation.InheritMovement(initial,new Vector3(3,8,2),0,W);
            Assert.That(moving-initial,Is.EqualTo(Vector3.forward*4));
            var s=Alive();SpreadSimulation.Reset(ref s,W);
            for(int t=0;t<105;t++)Tick(ref s,t);
            Assert.That(s.DualiesGroundBias,Is.EqualTo(.4f).Within(.00001));
            s.Grounded=false;for(int t=105;t<111;t++)Tick(ref s,t,false);
            Assert.That(s.CurrentSpread,Is.EqualTo(17.49f));
            Assert.That(ReferenceSpreadSimulation.Bias(s,W),Is.EqualTo(.4f).Within(.001));
        }
        [Test] public void BodyRestoreRemoteProxyAndCeilingClearanceAgree()
        {
            var go=new GameObject("Sploosh body");objects.Add(go);go.layer=8;
            var controller=go.AddComponent<CharacterController>();var motor=new PlayerMotorSimulation(controller);
            var s=Alive();s.Position=new Vector3(500,0,500);motor.Restore(s);
            Assert.That(controller.height,Is.EqualTo(1.5f));Assert.That(controller.radius,Is.EqualTo(.28f));
            var roof=new GameObject("low ceiling");objects.Add(roof);roof.transform.position=s.Position+Vector3.up*1.7f;roof.AddComponent<BoxCollider>().size=new Vector3(3,.2f,3);Physics.SyncTransforms();
            Assert.That(motor.CanFitHero(8,s.Position),Is.True);Assert.That(motor.CanFitHero(1,s.Position),Is.False);
            s.Swimming=true;motor.Restore(s);Assert.That(controller.height,Is.EqualTo(.625f));Assert.That(controller.center.y,Is.EqualTo(.3125f));
            s.Swimming=false;s.HeroId=1;motor.Restore(s);Assert.That(controller.height,Is.EqualTo(1.8f));
            s.HeroId=8;motor.Restore(s);Assert.That(controller.height,Is.EqualTo(1.5f));
        }
        [Test] public void AllTenRetargetedClipsKeepSupportGripReachable()
        {
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/SplooshGirl/Prefabs/SplooshGirlVisual.prefab");
            var go=Object.Instantiate(source);objects.Add(go);var view=go.GetComponent<InkCharacterView>();
            var gun=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Weapons/SplooshGirl/Prefabs/SplooshGun.prefab");
            var weapon=Object.Instantiate(gun,view.WeaponSocket,false);view.BindWeapon(weapon.GetComponent<HeroWeaponBindings>(),gun);
            var animator=view.Animator;var clips=animator.runtimeAnimatorController.animationClips.Distinct().ToArray();Assert.That(clips.Length,Is.EqualTo(10));
            float maximum=0;int samples=0;
            foreach(var clip in clips)foreach(float pitch in new[]{-45f,0,45f})
            {
                var state=Alive();state.Pitch=pitch;view.Present(state,0,0);animator.Rebind();animator.fireEvents=false;
                var graph=PlayableGraph.Create("Sploosh animation acceptance");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                try
                {
                    var playable=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"pose",animator).SetSourcePlayable(playable);graph.Play();
                    for(int i=0;i<=12;i++)
                    {
                        playable.SetTime(clip.length*i/12);graph.Evaluate(0);samples++;
                        if(!clip.name.StartsWith("Die"))
                        {view.ApplyAim();float error=Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftHand).position,view.LeftGrip.position);maximum=Mathf.Max(maximum,error);Assert.That(error,Is.LessThan(.01f),clip.name+" pitch="+pitch);}
                        foreach(var bone in animator.GetComponentsInChildren<Transform>())Assert.That(float.IsFinite(bone.position.x+bone.position.y+bone.position.z),Is.True);
                    }
                }
                finally{graph.Destroy();}
            }
            Directory.CreateDirectory("Reports/SplooshGirl");File.WriteAllText("Reports/SplooshGirl/animation.txt",$"clips={clips.Length}\nsamples={samples}\nmaxGripErrorMetres={maximum:R}\n");
        }
        [Test] public void RemoteCapsuleAndLivePaperUseTheSummerHero()
        {
            var go=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));objects.Add(go);
            var player=go.GetComponent<PrototypePlayer>();var body=player.SwimBody;
            body.Bind(PaperBodyTests.Profile("SplooshGirl"));
            var state=Alive();go.GetComponent<CharacterController>().enabled=false;body.ApplyCollision(state);
            Assert.That(body.CapsuleHitVolume.enabled,Is.True);Assert.That(body.CapsuleHitVolume.height,Is.EqualTo(1.5f));
            Assert.That(body.CapsuleHitVolume.radius,Is.EqualTo(.28f));Assert.That(body.CapsuleHitVolume.center.y,Is.EqualTo(.75f));
            state.Swimming=true;state.SwimSource=SwimSurface.Friendly;state.Movement=MovementMode.GroundInk;
            PaperPoseSimulation.Resolve(ref state,default,body.Profile,0);body.ApplyCollision(state);body.Present(state,Vector3.up*.5f,Quaternion.identity);
            Assert.That(body.FlatHitActive,Is.True);Assert.That(body.HitRects.Count,Is.GreaterThan(4));
            Assert.That(body.BodyRenderer.transform.position.y,Is.EqualTo(state.PaperCenter.y).Within(.00001f));
            Assert.That(body.CapsuleHitVolume.enabled,Is.False);
            PaperBodyTests.ReleaseTestBody(body);
        }
        [Test] public void PaperRetargetAndReplayUseTheSameSupportGrip()
        {
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/SplooshGirl/Prefabs/SplooshGirlVisual.prefab");
            var gun=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Weapons/SplooshGirl/Prefabs/SplooshGun.prefab");
            var root=new GameObject("Paper comparison");objects.Add(root);using var binder=new HeroViewBinder(root.transform);
            binder.Apply(new HeroContent(GameplayConfig.GetHero(8),source,gun));
            var captured=Object.Instantiate(PaperBodyTests.Profile("SplooshGirl").CapturePrefab);objects.Add(captured);var rig=captured.GetComponent<PaperCaptureRig>();
            foreach(var move in new[]{Vector2.zero,Vector2.up,Vector2.down,Vector2.left,Vector2.right})
            {
                var view=binder.View;var s=Alive();s.Velocity=new Vector3(move.x,0,move.y)*GameplayConfig.GetHero(8).MoveSpeed;view.Present(s,1,1);
                view.Animator.SetFloat("MoveX",move.x);view.Animator.SetFloat("MoveY",move.y);view.Animator.SetFloat("MovePlayback",1);
                view.Animator.Play("Base Layer.Locomotion",0,0);view.Animator.Update(0);float duration=view.Animator.GetCurrentAnimatorStateInfo(0).length;
                view.Animator.Play("Base Layer.Locomotion",0,.37f/duration);view.Animator.Update(0);view.ApplyAim();rig.Evaluate(.37f,move);
                foreach(var bone in new[]{HumanBodyBones.Head,HumanBodyBones.LeftHand,HumanBodyBones.RightHand,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot})
                    Assert.That(Vector3.Distance(view.transform.InverseTransformPoint(view.Animator.GetBoneTransform(bone).position),rig.transform.InverseTransformPoint(rig.Animator.GetBoneTransform(bone).position)),Is.LessThan(.002f),move+" "+bone);
            }
            using var capture=new PaperCapture(PaperBodyTests.Profile("SplooshGirl"));
            var state=Alive();state.PaperMove=Vector2.up;state.PaperAnimationTime=.13f;capture.Sample(state);var vertices=capture.HitMesh.vertices;
            state.PaperAnimationTime=.43f;capture.Sample(state);Assert.That(capture.HitMesh.vertices.SequenceEqual(vertices),Is.False);
            state.PaperAnimationTime=.13f;capture.Sample(state);CollectionAssert.AreEqual(vertices,capture.HitMesh.vertices);
        }
        [Test] public void AirHeroSwitchKeepsHumanFeetAndRoundTripPosition()
        {
            var s=Alive();s.Grounded=false;s.Swimming=true;s.Movement=MovementMode.Air;s.Position=new Vector3(50,4,50);s.AirHumanOffset=.72f;
            var feet=PlayerMotorSimulation.HumanPosition(s);var original=s.Position;
            Assert.That(HeroSelectionRules.Apply(ref s,1,true,default),Is.True);
            Assert.That(Vector3.Distance(PlayerMotorSimulation.HumanPosition(s),feet),Is.LessThan(.00001));Assert.That(s.AirHumanOffset,Is.EqualTo(.87f).Within(.00001));
            HeroSelectionRules.Apply(ref s,8,true,default);Assert.That(Vector3.Distance(s.Position,original),Is.LessThan(.00001));
        }
        [Test] public void SummerAirPaperLandsAtItsVisiblePlaneAndRestoresHumanHeight()
        {
            var floor=new GameObject("Small landing floor");objects.Add(floor);floor.transform.position=new Vector3(500,0,500);
            var box=floor.AddComponent<BoxCollider>();box.size=new Vector3(30,1,30);box.center=Vector3.down*.5f;
            var surface=floor.AddComponent<PaintSurface>();surface.Scores=true;surface.WalkableSize=new Vector2(30,30);surface.InitializeOwnership(.25f);
            for(int i=0;i<surface.Ownership.Cells.Length;i++)surface.Ownership.Set(i,1);
            var go=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));objects.Add(go);
            var body=go.GetComponent<PrototypePlayer>().SwimBody;body.Bind(PaperBodyTests.Profile("SplooshGirl"));
            var controller=go.GetComponent<CharacterController>();var motor=new PlayerMotorSimulation(controller);
            var s=Alive();s.Position=floor.transform.position+Vector3.up*3;s.Grounded=false;s.Movement=MovementMode.Air;s.VerticalSpeed=-2;motor.Restore(s);
            var input=new PlayerInputFrame{Swim=true};int low=0;
            for(int t=0;t<300&&!s.Grounded;t++)
            {
                var previous=s;motor.Step(ref s,input,1f/60,t/60.0,false);body.ApplyCollision(s);body.Present(s,Vector3.up*.3f,Quaternion.identity);
                Assert.That(body.BodyRenderer.transform.position.y,Is.EqualTo(s.PaperCenter.y).Within(.0001));
                if(s.Grounded)Assert.That(previous.PaperCenter.y,Is.LessThan(.12f));
                else if(s.PaperCenter.y<.7f)low++;
            }
            Assert.That(s.Grounded,Is.True);Assert.That(low,Is.GreaterThan(5));Assert.That(s.PaperCenter.y,Is.EqualTo(.03f).Within(.004));
            Assert.That(s.AirHumanOffset,Is.Zero);Assert.That(controller.height,Is.EqualTo(.625f));
            input.Swim=false;motor.Step(ref s,input,1f/60,6,false);Assert.That(controller.height,Is.EqualTo(1.5f));
            PaperBodyTests.ReleaseTestBody(body);
        }
        [TestCase(.15f,true)] [TestCase(.6f,false)] public void SummerControllerStepsOverLowButNotHighObstacles(float height,bool canCross)
        {
            var start=new Vector3(550,.04f,550);
            var floor=new GameObject("Step floor");objects.Add(floor);floor.transform.position=new Vector3(550,-.5f,550);floor.AddComponent<BoxCollider>().size=new Vector3(12,1,12);
            var step=new GameObject("Step obstacle");objects.Add(step);step.transform.position=new Vector3(550,height*.5f,550.9f);step.AddComponent<BoxCollider>().size=new Vector3(3,height,.7f);
            var go=new GameObject("Small controller"){layer=8};objects.Add(go);var controller=go.AddComponent<CharacterController>();var motor=new PlayerMotorSimulation(controller);
            var state=Alive();state.Position=start;motor.Restore(state);Physics.SyncTransforms();
            for(int t=0;t<60;t++)motor.Step(ref state,new PlayerInputFrame{Move=Vector2.up},1f/60,t/60.0,false);
            Assert.That(state.Position.z>551.7f,Is.EqualTo(canCross));Assert.That(controller.stepOffset,Is.EqualTo(.25f));
        }
        [Test] public void DetailedFieldsAreImmutableSignedAndValidated()
        {
            var source=AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/SplooshGirl/SplooshGirlWeaponConfig.asset");
            var clone=Object.Instantiate(source);
            try
            {
                var original=clone.Snapshot();clone.shooterPaintNearRadius+=.2f;var changed=clone.Snapshot();
                Assert.That(original.SameValues(changed),Is.False);Assert.That(original.ShooterPaintNearRadius,Is.EqualTo(source.shooterPaintNearRadius));
                Assert.That(original.RequiresRestart(changed),Is.False);clone.shooterSplitNum=3;Assert.That(original.RequiresRestart(clone.Snapshot()),Is.True);
                clone.shooterWallFirstMin=0;Assert.Throws<InvalidOperationException>(()=>WeaponConfigValidation.Validate(clone.Snapshot()));
            }
            finally{Object.DestroyImmediate(clone);}
        }
        [TestCase("flat")] [TestCase("up30")] [TestCase("down30")] [TestCase("high-drop")]
        [TestCase("wall-near")] [TestCase("wall-middle")] [TestCase("wall-far")] [TestCase("slope")] [TestCase("occluded")]
        public void PaintOwnershipMatchesAcrossRatesAndSeeds(string scenario)
        {
            const string path="Reports/SplooshGirl/Measurements";Directory.CreateDirectory(path);
            for(int seed=0;seed<3;seed++)
            {
                var seedPath=Path.Combine(path,"seed-"+seed);Directory.CreateDirectory(seedPath);
                var baseline=WeaponReferenceMeasurements.Capture(8,0,scenario,60,10,seedPath,fullActions:true,options:new WeaponReferenceMeasurements.Options { Seed=(uint)seed });
                Assert.That(baseline.emittedProjectiles,Is.EqualTo(10));Assert.That(baseline.paintStamps,Is.GreaterThan(0));
                foreach(int rate in new[]{30,144})
                {
                    var actual=WeaponReferenceMeasurements.Capture(8,0,scenario,rate,10,seedPath,fullActions:true,options:new WeaponReferenceMeasurements.Options { Seed=(uint)seed });
                    Assert.That(actual.gridHash,Is.EqualTo(baseline.gridHash),scenario+" seed="+seed+" rate="+rate);
                    Assert.That(actual.paintStamps,Is.EqualTo(baseline.paintStamps));Assert.That(actual.inkSpent,Is.EqualTo(baseline.inkSpent));
                }
            }
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void ExistingHeroesKeepFrozenPaintOwnership(int hero)
        {
            var baseline=SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SplooshGirl/LegacyPaintBaseline.json"))["records"];
            foreach(var row in baseline.Children.Where(r=>r["weapon"].AsInt==hero))
            {
                var current=WeaponReferenceMeasurements.Capture(hero,row["charge"].AsFloat,row["scenario"].Value,60,row["shotCount"].AsInt,null,fullActions:true,
                    options:new WeaponReferenceMeasurements.Options{Seed=(uint)row["sampleSeed"].AsLong});
                Assert.That(current.gridHash,Is.EqualTo(row["gridHash"].Value),hero+" "+row["scenario"].Value);
                Assert.That(current.paintStamps,Is.EqualTo(row["paintStamps"].AsInt));
                Assert.That(current.emittedProjectiles,Is.EqualTo(row["emittedProjectiles"].AsInt));
            }
        }
        [TestCase(false)] [TestCase(true)] public void SuspendedWallDripsFallOntoGroundAndRespectOccluders(bool blocked)
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            var origin=new Vector3(1500,5,1500);
            PaintSurface Surface(int id,Vector3 center,Vector2 size,Quaternion rotation)
            {
                var go=new GameObject("Detail paint "+id);objects.Add(go);go.SetActive(false);go.transform.SetPositionAndRotation(center,rotation);
                var box=go.AddComponent<BoxCollider>();box.center=Vector3.down*.05f;box.size=new Vector3(size.x,.1f,size.y);
                var p=go.AddComponent<PaintSurface>();p.enabled=false;p.SurfaceId=id;p.Scores=id==1;p.WalkableSize=size;
                if(id==2)p.WallRegions=new[]{new PaintRegion{Id=1,Size=size,Climbable=true}};
                p.InitializeOwnership(.125f);go.SetActive(true);return p;
            }
            Surface(1,new Vector3(origin.x,0,origin.z),new Vector2(10,10),Quaternion.identity);
            Surface(2,origin+Vector3.forward*1.5f,new Vector2(4,1),Quaternion.Euler(-90,0,0));
            if(blocked)
            {var roof=new GameObject("Unpaintable canopy");objects.Add(roof);roof.transform.position=origin+new Vector3(0,-2,1.4f);roof.AddComponent<BoxCollider>().size=new Vector3(10,.1f,10);}
            Physics.SyncTransforms();var stamps=new List<PaintStamp>();
            var source=AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/SplooshGirl/SplooshGirlWeaponConfig.asset");var clone=Object.Instantiate(source);
            try
            {
                clone.referenceTrailBudget=0;var config=clone.Snapshot();
                var service=new InkProjectileService();service.PaintObserved=stamps.Add;
                service.SpawnForMeasurement(new InkShot{Id=17,ActionId=1,HeroId=8,Team=1,Seed=31,Origin=origin,Velocity=Vector3.forward*config.SpeedMin,Configuration=config});
                for(int t=0;t<600;t++)service.Simulate(t/60.0);
                Assert.That(stamps.Count(s=>s.SurfaceId==2),Is.GreaterThan(2),"shock and staged wall flow");
                Assert.That(stamps.Any(s=>s.SurfaceId==1),Is.EqualTo(!blocked),"detached wall drop is physically obstructed");
                if(!blocked)Assert.That(stamps.Last(s=>s.SurfaceId==1).DepthScale,Is.EqualTo(1));
            }
            finally{Object.DestroyImmediate(clone);}
        }
    }
}
#endif
