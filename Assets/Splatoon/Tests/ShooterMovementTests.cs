#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Prototype;
using Splatoon.Networking;
using Splatoon.Painting;

namespace Splatoon.Tests
{
    public sealed class ShooterMovementTests
    {
        cfg.HeroConfig W => GameplayConfig.DefaultHero;
        [SetUp] public void Setup()
        {
            var tables=new cfg.Tables(name=>SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/"+name+".json")));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,tables);
        }
        [TearDown] public void Cleanup()
        {LubanConfigService.Current.Reset();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);}
        static PlayerSnapshot Alive()=>new(){Health=100,Ink=100,Team=1,Grounded=true};
        [TestCase(false,2)] [TestCase(true,10)]
        public void StartupAndContinuousCadenceUseReferenceFrames(bool emerged,int startup)
        {
            var s=Alive();var input=new PlayerInputFrame{Fire=true,FireSequence=1};var shots=new List<int>();
            for(int tick=0;tick<70;tick++)if(WeaponSimulation.Step(ref s,input,W,tick/60.0,emerged&&tick==0,true))shots.Add(tick);
            Assert.That(shots[0],Is.EqualTo(startup));
            for(int i=1;i<shots.Count;i++)Assert.That(shots[i]-shots[i-1],Is.EqualTo(6));
            Assert.That((shots[2]-shots[0])/60.0,Is.EqualTo(.2).Within(.00001));
        }
        [Test] public void ShortTapCompletesStartupAndCannotBypassCooldown()
        {
            var s=Alive();uint edge=0;var shots=new List<int>();
            for(int tick=0;tick<120;tick++)
            {
                bool down=tick%2==0;if(down)edge++;
                if(WeaponSimulation.Step(ref s,new PlayerInputFrame{Fire=down,FireSequence=edge},W,tick/60.0,false,true))shots.Add(tick);
            }
            Assert.That(shots.Count,Is.GreaterThan(10));
            for(int i=1;i<shots.Count;i++)Assert.That(shots[i]-shots[i-1],Is.GreaterThanOrEqualTo(6));
            s=Alive();WeaponSimulation.Step(ref s,new PlayerInputFrame{Fire=true,FireSequence=1},W,0,false,true);
            Assert.That(WeaponSimulation.Step(ref s,new PlayerInputFrame{FireSequence=1},W,2/60.0,false,true),Is.True);
        }
        [Test] public void FullTankFires108AndNeverGoesNegative()
        {
            var s=Alive();int shots=0;
            for(int i=0;i<1000;i++)if(WeaponSimulation.Step(ref s,new PlayerInputFrame{Fire=true,FireSequence=1},W,i/60.0,false,true))shots++;
            Assert.That(shots,Is.EqualTo(108));Assert.That(s.Ink,Is.EqualTo(.64).Within(.001));Assert.That(s.Ink,Is.GreaterThanOrEqualTo(0));
        }
        [Test] public void DamageHasExactAgeBoundariesAndThreeShotCloseKill()
        {
            Assert.That(WeaponSimulation.Damage(W,0),Is.EqualTo(36));Assert.That(WeaponSimulation.Damage(W,8/60.0),Is.EqualTo(36));
            Assert.That(WeaponSimulation.Damage(W,24/60.0),Is.EqualTo(27).Within(.0001));Assert.That(WeaponSimulation.Damage(W,40/60.0),Is.EqualTo(18));
            Assert.That(WeaponSimulation.Damage(W,2),Is.EqualTo(18));Assert.That(Mathf.CeilToInt(100/W.Damage),Is.EqualTo(3));
        }
        [Test] public void SwimmingCannotBypassRecoveryLock()
        {
            var s=Alive();s.Ink=40;s.Swimming=true;s.InkRecoverAt=20/60.0;
            ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,false,false,1f/60,19/60.0);Assert.That(s.Ink,Is.EqualTo(40));
            ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,false,false,1f/60,20/60.0);Assert.That(s.Ink,Is.EqualTo(40+100f/180).Within(.0001));
        }
        [TestCase(100,60)] [TestCase(59,59)] [TestCase(20,20)]
        public void EnemyInkIsNonlethalAndNeverHeals(float initial,float expected)
        {
            var s=Alive();s.Health=initial;
            for(int i=0;i<600;i++)ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,true,false,1f/60,i/60.0);
            Assert.That(s.Health,Is.EqualTo(expected).Within(.001));
        }
        [Test] public void HealthRecoveryWaitsAfterDamageAndEnemyInk()
        {
            var s=Alive();s.Health=30;s.LastDamageAt=2;
            ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,false,false,1f/60,2.99);Assert.That(s.Health,Is.EqualTo(30));
            ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,false,false,1f/60,3);Assert.That(s.Health,Is.EqualTo(30.5f));
            s.Swimming=true;ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,false,false,1f/60,3.02);Assert.That(s.Health,Is.EqualTo(31.5f));
        }
        [TestCase(30)] [TestCase(60)] [TestCase(144)]
        public void RenderRatesDoNotChangeFixedSimulationResults(int rate)
        {
            var s=Alive();double accumulator=0;int tick=0,shots=0;
            for(int frame=0;frame<rate*10;frame++)
            {
                accumulator+=1.0/rate;
                while(accumulator+1e-9>=1.0/60)
                {if(WeaponSimulation.Step(ref s,new PlayerInputFrame{Fire=true,FireSequence=1},W,tick++/60.0,false,true))shots++;accumulator-=1.0/60;}
            }
            Assert.That(tick,Is.EqualTo(600));Assert.That(shots,Is.EqualTo(100));Assert.That(s.Ink,Is.EqualTo(8).Within(.001));
        }
        [Test] public void HorizontalBallisticsUsesCurrentStraightAndBrakeTimings()
        {
            float age=(float)WeaponSimulation.Seconds(W.StraightFrames)+Mathf.Sqrt(2*1.4f/W.ProjectileGravity);
            var hit=InkBallistics.Position(Vector3.up*1.4f,Vector3.forward*W.SpeedMin,W,age);
            float straight=(float)WeaponSimulation.Seconds(W.StraightFrames),brake=(float)WeaponSimulation.Seconds(W.BrakeFrames);
            Assert.That(age,Is.GreaterThan(straight+brake));
            // Integrate the three speed phases independently. PaintRange is a tuning target,
            // and was not recalibrated when the live straight period changed from 4 to 10 frames.
            float expected=W.SpeedMin*(straight+brake*(1+W.BrakeSpeedMultiplier)*.5f+(age-straight-brake)*W.BrakeSpeedMultiplier);
            Assert.That(hit.y,Is.Zero.Within(.0001));Assert.That(hit.z,Is.EqualTo(expected).Within(.0001));
            Assert.That(InkBallistics.Position(Vector3.zero,Vector3.forward*31,W,4/60.0).y,Is.Zero);
        }
        PrototypeArena LoadArena()
        {
            var scene=EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity",OpenSceneMode.Single);
            var arena=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<PrototypeArena>()).Single();arena.InitializeRuntime();Physics.SyncTransforms();return arena;
        }
        [TestCase(2)] [TestCase(32)] [TestCase(62)]
        public void ReleasedSniperCanImmediatelySwimAndReplayOnFriendlyInk(int release)
        {
            var arena=LoadArena();var w=GameplayConfig.GetHero(5);
            // Keep this firing/swimming test off the tile seam and the spawn shield.
            var s=Alive();s.HeroId=w.Id;s.Position=arena.SpawnPoints[0].position+Vector3.forward;
            Assert.That(Physics.Raycast(s.Position+Vector3.up*.2f,Vector3.down,out var hit,1,PlayerMotorSimulation.WorldMask),Is.True);
            var floor=hit.collider.GetComponent<PaintSurface>();Assert.That(floor,Is.Not.Null);
            arena.Apply(new PaintStamp{SurfaceId=floor.SurfaceId,Position=hit.point,Normal=hit.normal,Radius=3,Hardness=1,Strength=1,Team=s.Team},true);
            Assert.That(arena.FloorOwner(s.Position),Is.EqualTo(s.Team));
            for(int tick=0;tick<=release;tick++)
                WeaponSimulation.Step(ref s,new PlayerInputFrame{Sequence=(uint)tick+1,Fire=tick<release,FireSequence=1},w,tick/60.0,false,true);
            Assert.That(s.ShotSequence,Is.EqualTo(1));var checkpoint=s;
            var go=new GameObject("Sniper swim probe");go.layer=8;
            var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;cc.skinWidth=.03f;
            try
            {
                var motor=new PlayerMotorSimulation(cc,arena);
                PlayerSnapshot Swim(PlayerSnapshot state)
                {
                    motor.Restore(state);
                    // Restore teleports the controller; establish a real grounded contact first.
                    cc.Move(Vector3.down*.2f);state.Position=go.transform.position;state.VerticalSpeed=-2;state.Grounded=cc.isGrounded;
                    Assert.That(state.Grounded,Is.True);
                    for(int tick=release+1;tick<=release+10;tick++)
                    {
                        var input=new PlayerInputFrame{Sequence=(uint)tick+1,FireSequence=1,Swim=true,Move=Vector2.up};
                        motor.Step(ref state,input,1f/60,tick/60.0,WeaponSimulation.WantsFire(state,input),w.ShootMoveSpeed);
                        Assert.That(state.Movement,Is.EqualTo(MovementMode.GroundInk));Assert.That(state.Swimming,Is.True);
                        Assert.That(WeaponSimulation.Step(ref state,input,w,tick/60.0,false,!state.Swimming&&motor.CanStand(state.Position)),Is.False);
                    }
                    return state;
                }
                var authority=Swim(checkpoint);var replay=Swim(checkpoint);
                Assert.That(authority.Position.z,Is.GreaterThan(checkpoint.Position.z));
                Assert.That(Vector3.Distance(replay.Position,authority.Position),Is.LessThan(.003));
                Assert.That(authority.NextShotAt,Is.EqualTo(checkpoint.NextShotAt));
                Assert.That(authority.InkRecoverAt,Is.EqualTo(checkpoint.InkRecoverAt));
                Assert.That(authority.Ink,Is.EqualTo(checkpoint.Ink));Assert.That(authority.ShotSequence,Is.EqualTo(1));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void WallPlanesAreIndependentAndDoNotAddScoreArea()
        {
            var arena=LoadArena();double area=arena.TotalArea;
            var surface=arena.Surfaces.Values.Single(s=>s.name=="PlatformBody_1");
            var region=surface.WallRegions.Single(r=>Vector3.Dot(r.Matrix(surface).MultiplyVector(Vector3.up),Vector3.right)>.99f);
            var point=region.Matrix(surface).MultiplyPoint3x4(Vector3.zero);
            arena.Apply(new PaintStamp{SurfaceId=surface.SurfaceId,Position=point,Normal=Vector3.right,Radius=1,Hardness=.8f,Strength=1,Team=1},true);
            Assert.That(surface.QueryRegion(point,Vector3.right,out var contact),Is.True);Assert.That(contact.Owner,Is.EqualTo(1));
            Assert.That(region.Grid.PinkArea,Is.GreaterThan(0));Assert.That(arena.PinkArea,Is.Zero);Assert.That(arena.TotalArea,Is.EqualTo(area));
            Assert.That(surface.WallRegions.Where(r=>r!=region).All(r=>r.Grid.PinkArea==0),Is.True);
            var snapshot=arena.CaptureOwnership();uint hash=arena.OwnershipHash();arena.ClearPaint();arena.RestoreOwnership(snapshot);Assert.That(arena.OwnershipHash(),Is.EqualTo(hash));
            Assert.That(arena.Surfaces.Values.Where(s=>s.name.Contains("Boundary")).SelectMany(s=>s.WallRegions).All(r=>!r.Climbable),Is.True);
        }
        [Test] public void OwnerAndAuthorityMotorReplayMatchWithoutLifecycleReset()
        {
            var arena=LoadArena();var go=new GameObject("Motor probe");go.layer=8;var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;cc.skinWidth=.03f;
            try
            {
                var motor=new PlayerMotorSimulation(cc,arena);var s=Alive();s.Position=new Vector3(0,.05f,-24);s.Revision=9;motor.Restore(s);Physics.SyncTransforms();var checkpoint=s;
                var input=new PlayerInputFrame{Move=Vector2.up,Look=Vector2.zero};
                for(int i=0;i<60;i++){motor.Step(ref s,input,1f/60,i/60.0,false);if(i==29)checkpoint=s;}
                var expected=s;motor.Restore(checkpoint);Physics.SyncTransforms();
                for(int i=30;i<60;i++)motor.Step(ref checkpoint,input,1f/60,i/60.0,false);
                Assert.That(Vector3.Distance(expected.Position,checkpoint.Position),Is.LessThan(.003));Assert.That(checkpoint.Revision,Is.EqualTo(9));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void OnlyLocomotionUsesSpeedParameter()
        {
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/GameResource/Characters/RifleGirl/Animations/RifleGirlCombat.controller");
            foreach(var layer in controller.layers)foreach(var child in layer.stateMachine.states)
                Assert.That(child.state.speedParameterActive,Is.EqualTo(child.state.name=="Locomotion"),child.state.name);
        }
        [Test] public void WallClimbMantleAndEnemyCoverUseActualController()
        {
            var arena=LoadArena();var surface=arena.Surfaces.Values.Single(s=>s.name=="PlatformBody_1");
            var region=surface.WallRegions.Single(r=>Vector3.Dot(r.Matrix(surface).MultiplyVector(Vector3.up),Vector3.right)>.99f);
            for(int i=0;i<region.Grid.Cells.Length;i++)region.Grid.Set(i,1);
            var go=new GameObject("Wall probe");go.layer=8;var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;cc.skinWidth=.03f;
            try
            {
                var motor=new PlayerMotorSimulation(cc,arena);var s=Alive();s.Position=new Vector3(13.42f,.04f,0);motor.Restore(s);
                var input=new PlayerInputFrame{Move=Vector2.up,Swim=true,Look=new Vector2(270,0)};var modes=new HashSet<MovementMode>();
                for(int i=0;i<150;i++){motor.Step(ref s,input,1f/60,i/60.0,false);modes.Add(s.Movement);if(modes.Contains(MovementMode.Mantle)&&s.Grounded)break;}
                Assert.That(modes.Contains(MovementMode.WallInk),Is.True,"wall entry");Assert.That(modes.Contains(MovementMode.Mantle),Is.True,"mantle");Assert.That(s.Position.y,Is.GreaterThan(2.9f));
                s=Alive();s.Position=new Vector3(13.42f,.04f,0);motor.Restore(s);motor.Step(ref s,input,1f/60,4,false);Assert.That(s.Movement,Is.EqualTo(MovementMode.WallInk));
                for(int i=0;i<region.Grid.Cells.Length;i++)region.Grid.Set(i,2);
                motor.Step(ref s,input,1f/60,4+1.0/60,false);Assert.That(s.Movement,Is.Not.EqualTo(MovementMode.WallInk));
                float oldY=s.Position.y;s.Health=0;s.RespawnsAt=10;s.VerticalSpeed=0;s.Position=new Vector3(13.5f,2,0);motor.Restore(s);
                for(int i=0;i<120;i++)motor.Step(ref s,input,1f/60,5+i/60.0,false);
                Assert.That(s.Movement,Is.EqualTo(MovementMode.Dead));Assert.That(s.Position.y,Is.InRange(-.05f,.15f));Assert.That(s.RespawnsAt,Is.EqualTo(10));Assert.That(cc.enabled,Is.False);
                s.Position=new Vector3(13.5f,1,0);s.VerticalSpeed=4;motor.Restore(s);motor.Step(ref s,input,1f/60,7,false);
                Assert.That(s.Position.y,Is.GreaterThan(1),"an upward airborne death retains gravity-driven motion");Assert.That(s.RespawnsAt,Is.EqualTo(10));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void PredictionSnapshotSerializesEverySimulationField()
        {
            object boxed=new PlayerSnapshot();int index=1;
            foreach(var field in typeof(PlayerSnapshot).GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance))
            {
                var type=field.FieldType;object value=type==typeof(Vector3)?new Vector3(index,index+.1f,index+.2f):type.IsEnum?Enum.ToObject(type,1):Convert.ChangeType(type==typeof(bool)?1:index,type);
                field.SetValue(boxed,value);index++;
            }
            var expected=(PlayerSnapshot)boxed;
            using var writer=new Unity.Netcode.FastBufferWriter(1024,Unity.Collections.Allocator.Temp);writer.WriteNetworkSerializable(expected);
            using var reader=new Unity.Netcode.FastBufferReader(writer,Unity.Collections.Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot actual);
            Assert.That(JsonUtility.ToJson(actual),Is.EqualTo(JsonUtility.ToJson(expected)));Assert.That(writer.Length,Is.LessThan(512));
        }
        [Test] public void RifleGirlLogicalMuzzleProjectileHitsFloorOnCorrectedTrajectory()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(100,.5f,100);
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var go=UnityEngine.Object.Instantiate(prefab);var player=go.GetComponent<PrototypePlayer>();
            try
            {
                var state=Alive();state.Position=Vector3.up*.04f;state.CurrentSpread=.000001f;go.transform.position=state.Position;Physics.SyncTransforms();
                var service=new InkProjectileService();service.Spawn(player,state,0,0);
                var shot=service.Spawned[0];
                var fallStart=InkBallistics.Position(shot,W,shot.GravityStartAge);
                Assert.That(shot.PostCorrectionVelocity.y,Is.Zero.Within(.00001));
                double impactAge=shot.GravityStartAge+Math.Sqrt(2*(fallStart.y-W.CollisionRadius)/W.ProjectileGravity);
                var expected=InkBallistics.Position(shot,W,impactAge);
                service.Simulate(W.Lifetime+.01);
                Assert.That(service.Impacts.Count,Is.EqualTo(1));Assert.That(service.Impacts[0].Hit,Is.True);
                var point=service.Impacts[0].Position;Assert.That(point.y,Is.Zero.Within(.04));
                Assert.That(point.z,Is.EqualTo(expected.z).Within(.01));
                Assert.That(point.x,Is.EqualTo(expected.x).Within(.01));
                Debug.Log($"[SHOOTER-RANGE] RifleGirl corrected floor={point.z:F3}m; configured tuning target={W.PaintRange:F1}m");
            }
            finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(floor);}
        }
        [Test] public void PressEdgeDuringHeldBurstDoesNotFireAnExtraShotAfterRelease()
        {
            HeroMigrationTests.LoadHistoricalWeapons();
            var state=Alive();int shots=0;
            for(int tick=0;tick<30;tick++)
            {
                var input=new PlayerInputFrame{Sequence=(uint)tick+1,Fire=tick<=8,FireSequence=tick<3?1u:2u};
                if(WeaponSimulation.Step(ref state,input,W,tick/60.0,false,true))shots++;
            }
            Assert.That(shots,Is.EqualTo(2));Assert.That(state.WeaponPhase,Is.EqualTo(WeaponPhase.Idle));
        }
        [Test] public void ReplayKeepsShotActionButCancelledBurstCannotSuppressANewBurst()
        {
            HeroMigrationTests.LoadHistoricalWeapons();
            var original=Alive();var first=new PlayerInputFrame{Sequence=101,Fire=true,FireSequence=1};
            WeaponSimulation.Step(ref original,first,W,0,false,true);var checkpoint=original;
            Assert.That(WeaponSimulation.Step(ref original,first,W,2/60.0,false,true),Is.True);
            WeaponSimulation.Step(ref checkpoint,first,W,2/60.0,false,true);Assert.That(checkpoint.ShotActionId,Is.EqualTo(original.ShotActionId));
            var corrected=Alive();var second=new PlayerInputFrame{Sequence=202,Fire=true,FireSequence=2};
            WeaponSimulation.Step(ref corrected,second,W,1,false,true);WeaponSimulation.Step(ref corrected,second,W,1+2/60.0,false,true);
            Assert.That(corrected.ShotSequence,Is.EqualTo(original.ShotSequence));Assert.That(corrected.ShotActionId,Is.Not.EqualTo(original.ShotActionId));
        }
        [Test] public void ContentSignatureIncludesCameraMuzzleColliderAndTurnCurve()
        {
            Splatoon.Painting.InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(Splatoon.Painting.InkShapeAtlas.AssetPath));
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var go=UnityEngine.Object.Instantiate(prefab);var player=go.GetComponent<PrototypePlayer>();var profile=UnityEngine.Object.Instantiate(player.Presentation);player.CharacterView.Profile=profile;
            try
            {
                byte[] tables={1,2,3};var baseline=GameplayContentSignature.Compute(tables,"topology",player);
                profile.CameraCollisionRadius+=.01f;CollectionAssert.AreNotEqual(baseline,GameplayContentSignature.Compute(tables,"topology",player));profile.CameraCollisionRadius-=.01f;
                baseline=GameplayContentSignature.Compute(tables,"topology",player);player.SimulationMuzzle.localPosition+=Vector3.up*.01f;
                CollectionAssert.AreNotEqual(baseline,GameplayContentSignature.Compute(tables,"topology",player));
                baseline=GameplayContentSignature.Compute(tables,"topology",player);go.GetComponent<CharacterController>().height+=.01f;
                CollectionAssert.AreNotEqual(baseline,GameplayContentSignature.Compute(tables,"topology",player));
                baseline=GameplayContentSignature.Compute(tables,"topology",player);profile.TurnLeftProgress=AnimationCurve.Linear(0,0,1,.5f);
                CollectionAssert.AreNotEqual(baseline,GameplayContentSignature.Compute(tables,"topology",player));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [Test] public void WallFireWaitsForStandingClearanceAndMantleRejectsBlockedTop()
        {
            var arena=LoadArena();var surface=arena.Surfaces.Values.Single(s=>s.name=="PlatformBody_1");
            var region=surface.WallRegions.Single(r=>Vector3.Dot(r.Matrix(surface).MultiplyVector(Vector3.up),Vector3.right)>.99f);
            for(int i=0;i<region.Grid.Cells.Length;i++)region.Grid.Set(i,1);
            var go=new GameObject("Clearance probe");go.layer=8;var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;cc.skinWidth=.03f;
            var block=GameObject.CreatePrimitive(PrimitiveType.Cube);block.transform.position=new Vector3(12.5f,4,0);block.transform.localScale=new Vector3(2,1,2);
            try
            {
                var motor=new PlayerMotorSimulation(cc,arena);var s=Alive();s.Position=new Vector3(13.42f,.04f,0);motor.Restore(s);
                var input=new PlayerInputFrame{Move=Vector2.up,Swim=true,Look=new Vector2(270,0)};
                for(int i=0;i<120;i++){motor.Step(ref s,input,1f/60,i/60.0,false);Assert.That(s.Movement,Is.Not.EqualTo(MovementMode.Mantle));}
                block.transform.position=new Vector3(13.6f,1.4f,0);block.transform.localScale=new Vector3(1,.25f,1);
                s=Alive();s.Position=new Vector3(13.42f,.04f,0);s.Swimming=true;motor.Restore(s);Physics.SyncTransforms();
                Assert.That(motor.CanStand(s.Position),Is.False);
                input.Fire=true;input.FireSequence=1;motor.Step(ref s,input,1f/60,4,true);
                Assert.That(WeaponSimulation.Step(ref s,input,W,4,true,!s.Swimming&&motor.CanStand(s.Position)),Is.False);
                Assert.That(s.Ink,Is.EqualTo(100));Assert.That(s.Swimming,Is.True);
            }
            finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(block);}
        }
        [Test] public void CoplanarSeamCanBeCrossedButEnemyNeighbourHasNoGrace()
        {
            var arena=LoadArena();var surface=arena.Surfaces.Values.Single(s=>s.name=="PlatformBody_1");
            var plane=surface.WallRegions.Single(r=>Vector3.Dot(r.Matrix(surface).MultiplyVector(Vector3.up),Vector3.right)>.99f);
            PaintRegion Half(int id,float offset)=>new(){Id=id,Origin=plane.Origin+plane.Rotation*(Vector3.right*offset),Rotation=plane.Rotation,Size=new Vector2(4.96f,plane.Size.y),Climbable=true};
            var left=Half(201,-2.52f);var right=Half(202,2.52f);
            surface.WallRegions=surface.WallRegions.Where(r=>r!=plane).Concat(new[]{left,right}).ToArray();surface.InitializeOwnership(.125f);
            foreach(var region in new[]{left,right})for(int i=0;i<region.Grid.Cells.Length;i++)region.Grid.Set(i,1);
            var go=new GameObject("Seam probe");go.layer=8;var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;cc.skinWidth=.03f;
            try
            {
                var motor=new PlayerMotorSimulation(cc,arena);var start=Alive();start.Position=new Vector3(13.37f,1.1f,-.07f);start.Swimming=true;start.Grounded=false;start.Movement=MovementMode.WallInk;start.WallNormal=Vector3.right;
                var input=new PlayerInputFrame{Move=Vector2.right,Swim=true,Look=new Vector2(270,0)};var state=start;motor.Restore(state);
                motor.Step(ref state,input,1f/60,1/60.0,false);motor.Step(ref state,input,1f/60,2/60.0,false);
                Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk));Assert.That(state.Position.z,Is.GreaterThan(.04f));
                for(int i=0;i<right.Grid.Cells.Length;i++)right.Grid.Set(i,2);
                state=start;motor.Restore(state);motor.Step(ref state,input,1f/60,1/60.0,false);
                Assert.That(state.Movement,Is.Not.EqualTo(MovementMode.WallInk));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
    }
}
#endif
