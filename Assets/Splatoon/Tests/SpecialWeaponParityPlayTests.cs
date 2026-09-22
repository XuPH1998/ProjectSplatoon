#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class SpecialWeaponParityPlayTests
    {
        static IEnumerator Wait(Func<bool> ready)
        {
            double end=Time.realtimeSinceStartupAsDouble+45;
            while(!ready()&&Time.realtimeSinceStartupAsDouble<end)yield return null;
            Assert.That(ready(),Is.True,"Host initialization");
        }

        [UnityTest]
        public IEnumerator TrizookaPiercesPlayersAndUpgradesSplashToDirectWithoutStacking()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var first=match.AddTestBot(prefab);var second=match.AddTestBot(prefab);
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            double now=match.NetworkManager.ServerTime.Time;
            Vector3 origin=new(2000,10,2000);GameObject wall=null;
            void Place(PrototypePlayer player,Vector3 at,byte team)
            {
                var s=player.Snapshot.Value;s.Position=at;s.Team=team;s.Health=1000;s.LifeState=PlayerLifeState.Alive;
                s.ProtectedUntil=0;s.SpecialPhase=SpecialPhase.Charging;s.SpecialChargeLockedUntil=0;s.Swimming=s.CompactBody=false;s.AirHumanOffset=0;s.PaperPose=PaperPose.None;
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.position=at;cc.enabled=true;
                player.Snapshot.Value=s;player.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            }
            SpecialWeaponService.Entity Shot(Vector3 from)
            {
                var s=host.Snapshot.Value;s.SpecialWeaponId=1;s.SpecialAction++;s.SpecialAttack++;host.Snapshot.Value=s;
                var e=match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);
                e.State.Position=e.State.P0=e.State.P1=e.State.P2=from;
                e.State.Direction=Vector3.forward;e.State.Velocity=Vector3.forward*e.Config.P.projectileSpeed;
                return e;
            }
            void Frames(int frames)
            {for(int i=0;i<frames;i++){now+=1d/60;match.SpecialWeapons.Step(now,1f/60,match.Players);}}
            var blast=typeof(SpecialWeaponService).GetMethod("Blast",BindingFlags.Instance|BindingFlags.NonPublic);
            try
            {
                Place(host,origin+Vector3.back*10,1);Place(first,origin+Vector3.forward*2,2);Place(second,origin+Vector3.forward*5,2);
                Vector3 muzzle=origin+Vector3.up*GameplayConfig.GetHero(first.Snapshot.Value.HeroId).StandingHeight*.5f;
                var e=Shot(muzzle);Frames(3);
                Assert.That(e.State.P1,Is.EqualTo(muzzle),"Second lobe has not launched before frame 4");
                Assert.That(e.State.P2,Is.EqualTo(muzzle),"Third lobe has not launched before frame 8");
                Frames(6);
                Assert.That(first.Snapshot.Value.Health,Is.EqualTo(780).Within(.001),"Three lobes deliver only 220 total to first player");
                Assert.That(second.Snapshot.Value.Health,Is.EqualTo(780).Within(.001),"Same volley continues through first player");
                Assert.That(e.State.LiveLobes,Is.EqualTo(7),"Player contacts do not explode or consume lobes");
                match.SpecialWeapons.Clear();Place(first,origin+Vector3.forward*2,2);Place(second,origin+Vector3.right*30,2);
                e=Shot(muzzle);
                Vector3 splash=first.transform.position+Vector3.up*GameplayConfig.GetHero(first.Snapshot.Value.HeroId).StandingHeight*.5f+Vector3.left*e.Config.P.innerRadius*.8f;
                blast.Invoke(match.SpecialWeapons,new object[]{e,splash,null,true});
                Assert.That(first.Snapshot.Value.Health,Is.EqualTo(947).Within(.001),"Initial near splash deals 53");
                blast.Invoke(match.SpecialWeapons,new object[]{e,splash,null,true});
                Assert.That(first.Snapshot.Value.Health,Is.EqualTo(947).Within(.001),"Overlapping equal splash is not added twice");
                Frames(6);
                Assert.That(first.Snapshot.Value.Health,Is.EqualTo(780).Within(.001),"Later direct adds 167, bringing this volley to 220");
                blast.Invoke(match.SpecialWeapons,new object[]{e,splash,null,true});
                Assert.That(first.Snapshot.Value.Health,Is.EqualTo(780).Within(.001),"Splash after direct cannot add more damage");
                match.SpecialWeapons.Clear();Place(first,origin+Vector3.right*30,2);
                var deploy=first.Snapshot.Value;deploy.SpecialWeaponId=3;deploy.SpecialAction++;first.Snapshot.Value=deploy;
                var sonar=match.SpecialWeapons.Spawn(first,deploy,now,match.State.Value.Round);
                sonar.State.Position=origin+Vector3.forward*2;sonar.State.Phase=SpecialEntityPhase.Active;sonar.State.Changed=now;sonar.State.Expires=now+10;
                var targetObject=new GameObject("ParitySonarTarget");sonar.Target=targetObject.AddComponent<SpecialWeaponTarget>();sonar.Target.Initialize(match.SpecialWeapons,sonar);Physics.SyncTransforms();
                e=Shot(muzzle);
                splash=sonar.State.Position+Vector3.up*.7f+Vector3.left*e.Config.P.innerRadius*.8f;
                blast.Invoke(match.SpecialWeapons,new object[]{e,splash,null,true});
                Assert.That(sonar.State.Health,Is.EqualTo(427).Within(.001));
                Frames(16);
                Assert.That(sonar.State.Health,Is.EqualTo(260).Within(.001),"Deployment also receives at most 220, upgrading its earlier 53 splash");
                match.SpecialWeapons.Clear();Place(first,origin+Vector3.forward*6,2);
                wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=origin+Vector3.forward*2+Vector3.up*2;
                wall.transform.localScale=new Vector3(10,10,.5f);Physics.SyncTransforms();
                e=Shot(muzzle);Frames(16);
                Assert.That(first.Snapshot.Value.Health,Is.EqualTo(1000),"World geometry stops lobes before the player");
                Assert.That(e.Removed,Is.True,"All three field impacts remove the volley");
            }
            finally
            {
                if(wall!=null)UnityEngine.Object.Destroy(wall);
                match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;
            }
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ReefsliderMotorUsesFixedDirectionBrakingWallAndRecoveryFrames()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;match.enabled=false;
            var enemy=match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            foreach(var player in match.Players)player.enabled=false;
            var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);
            Vector3 origin=new(2000,10,2000);
            ground.transform.position=origin-Vector3.up*.5f;ground.transform.localScale=new Vector3(100,1,100);
            wall.transform.position=origin+Vector3.forward*40+Vector3.up*2;wall.transform.localScale=new Vector3(10,4,1);Physics.SyncTransforms();
            var step=typeof(PrototypePlayer).GetMethod("Step",BindingFlags.NonPublic|BindingFlags.Instance);
            var restore=typeof(PrototypePlayer).GetMethod("DiagnosticPlace",BindingFlags.NonPublic|BindingFlags.Instance);
            var config=SpecialWeaponConfigService.Current.Get(5);
            double start=match.NetworkManager.ServerTime.Time;
            PlayerSnapshot state=default;Splatoon.Networking.PlayerInputFrame input=default;
            void Reset()
            {
                match.SpecialWeapons.Clear();restore.Invoke(host,new object[]{origin+Vector3.up*.08f,0f});
                state=host.Snapshot.Value;state.SpecialWeaponId=5;state.SpecialPoints=200;state.SpecialPhase=SpecialPhase.Charging;state.SpecialChargeLockedUntil=0;state.Grounded=true;
                input=new Splatoon.Networking.PlayerInputFrame{HeroRevision=state.HeroRevision,SpecialSequence=state.SpecialConsumed+1,Look=Vector2.zero,JumpSequence=state.ConsumedJump};
                host.Snapshot.Value=state;
            }
            void Frame(int frame)
            {
                host.Snapshot.Value=state;
                var args=new object[]{state,input,1f/60,start+frame/60d,Splatoon.Networking.MatchPhase.Practice};
                step.Invoke(host,args);state=(PlayerSnapshot)args[0];host.Snapshot.Value=state;
                Physics.SyncTransforms();
            }
            try
            {
                Reset();
                void Enemy(Vector3 at)
                {
                    restore.Invoke(enemy,new object[]{at,0f});var victim=enemy.Snapshot.Value;
                    victim.Team=state.Team==1?(byte)2:(byte)1;victim.Health=1000;victim.ProtectedUntil=0;victim.SpecialPhase=SpecialPhase.Charging;
                    enemy.Snapshot.Value=victim;enemy.SwimBody?.ApplyCollision(victim);Physics.SyncTransforms();
                }
                state.SpecialDirection=Vector3.forward;state.SpecialAction++;
                Enemy(origin+Vector3.forward*config.P.rideDetectOffsetZ+Vector3.up*3);
                match.SpecialWeapons.ReefContact(host,state,state.Position,start);
                Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(1000),"Airborne proxy clears the ride contact sphere");
                Enemy(origin+Vector3.forward*config.P.rideDetectOffsetZ);
                match.SpecialWeapons.ReefContact(host,state,state.Position,start);
                Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(780),"Forward offset sphere hits the body proxy");
                match.SpecialWeapons.ReefContact(host,state,state.Position,start);
                Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(780),"One ride cannot repeatedly damage the same life");
                Enemy(origin+Vector3.right*30);
                Reset();Frame(0);
                Assert.That(state.SpecialPhase,Is.EqualTo(SpecialPhase.Starting),"Q activation: "+state.SpecialFailure);
                for(int frame=1;frame<38;frame++)Frame(frame);
                Assert.That(state.SpecialRideStage,Is.Zero);Assert.That(Mathf.Abs(state.Position.z-origin.z),Is.LessThan(.001f));
                Frame(38);Assert.That(state.SpecialRideStage,Is.EqualTo(1));
                Assert.That(state.Position.z,Is.GreaterThan(origin.z),"Actual CharacterController moves at frame 38");
                input.Look=new Vector2(90,0);input.Move=Vector2.right;input.JumpSequence++;
                for(int frame=39;frame<=91;frame++)Frame(frame);
                Assert.That(Mathf.Abs(state.Position.x-origin.x),Is.LessThan(.001f),"Look and movement input cannot steer the ride");
                Assert.That(state.SpecialRideStage,Is.EqualTo(1));
                Frame(92);Assert.That(state.SpecialRideStage,Is.EqualTo(2));
                Assert.That(state.SpecialBurstAt-start,Is.EqualTo(130d/60).Within(.000001));
                for(int frame=93;frame<130;frame++)Frame(frame);
                Assert.That(state.SpecialRideStage,Is.EqualTo(2));
                Frame(130);Assert.That(state.SpecialPhase,Is.EqualTo(SpecialPhase.Recovering));
                Assert.That(match.SpecialWeapons.Entities.Count,Is.EqualTo(1),"One authoritative blast at the burst frame");
                for(int frame=131;frame<158;frame++)Frame(frame);
                Assert.That(state.SpecialPhase,Is.EqualTo(SpecialPhase.Recovering));Frame(158);
                Assert.That(state.SpecialPhase,Is.EqualTo(SpecialPhase.Charging));
                Reset();Frame(0);input.Fire=true;
                for(int frame=1;frame<53;frame++)Frame(frame);
                Assert.That(state.SpecialRideStage,Is.EqualTo(1),"Held brake is ignored until 15 ride frames");
                Frame(53);Assert.That(state.SpecialRideStage,Is.EqualTo(2));
                Assert.That(state.SpecialBurstAt-start,Is.EqualTo(91d/60).Within(.000001));
                foreach(bool oldEquipment in new[]{false,true})
                {
                    Reset();Frame(0);for(int frame=1;frame<=52;frame++)Frame(frame);
                    input.CancelFire=!oldEquipment;if(oldEquipment)input.HeroRevision=state.HeroRevision-1;
                    input.Fire=false;Frame(53);Assert.That(state.SpecialRideStage,Is.EqualTo(1));
                    input.CancelFire=false;input.HeroRevision=state.HeroRevision;input.Fire=true;
                    Frame(54);Assert.That(state.SpecialRideStage,Is.EqualTo(1),$"After {(oldEquipment?"old equipment":"menu/focus cancellation")}, held fire must wait for release before braking");
                    input.Fire=false;Frame(55);input.Fire=true;Frame(56);
                    Assert.That(state.SpecialRideStage,Is.EqualTo(2),"A fresh post-cancellation press still brakes");
                }
                wall.transform.position=origin+Vector3.forward*2+Vector3.up*2;Physics.SyncTransforms();
                Reset();Frame(0);
                int stopped=0;for(int frame=1;frame<92;frame++){Frame(frame);if(state.SpecialRideStage==2){stopped=frame;break;}}
                Assert.That(stopped,Is.InRange(39,91),"World wall triggers automatic braking before timeout");
                Assert.That(state.Position.z,Is.LessThan(origin.z+2),"Controller cannot pass through the wall");
                Assert.That(state.SpecialBurstAt-start,Is.EqualTo((stopped+38)/60d).Within(.000001));
            }
            finally
            {
                UnityEngine.Object.Destroy(ground);UnityEngine.Object.Destroy(wall);match.SpecialWeapons.Clear();match.enabled=true;
                foreach(var player in match.Players)player.enabled=true;
            }
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator TornadoVerticalDamageTicksAndBombCancellation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var lower=match.AddTestBot(prefab);var outside=match.AddTestBot(prefab);var upper=match.AddTestBot(prefab);
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            Vector3 center=new(2000,20,2000);double start=match.NetworkManager.ServerTime.Time;
            var config=SpecialWeaponConfigService.Current.Get(2);
            void Place(PrototypePlayer player,Vector3 at,byte team)
            {
                var s=player.Snapshot.Value;s.Position=at;s.Team=team;s.Health=1000;s.LifeState=PlayerLifeState.Alive;s.ProtectedUntil=0;
                s.SpecialPhase=SpecialPhase.Charging;s.Swimming=s.CompactBody=false;s.PaperPose=PaperPose.None;s.AirHumanOffset=0;
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.position=at;cc.enabled=true;
                player.Snapshot.Value=s;player.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            }
            SpecialWeaponService.Entity Tornado()
            {
                var s=host.Snapshot.Value;s.SpecialWeaponId=2;s.SpecialAction++;s.SpecialAttack++;host.Snapshot.Value=s;
                var e=match.SpecialWeapons.Spawn(host,s,start,match.State.Value.Round);e.State.Position=center;e.State.Velocity=Vector3.zero;
                e.State.Phase=SpecialEntityPhase.Warning;e.State.Changed=start;e.State.Expires=start+config.P.effectDelay+config.P.effectDuration;return e;
            }
            SubWeaponService.Entity Bomb(PrototypePlayer owner,int id,Vector3 at,Vector3 velocity,double now)
            {
                var s=owner.Snapshot.Value;s.SubWeaponId=id;s.SubAction++;owner.Snapshot.Value=s;
                return match.SubWeapons.Spawn(owner,s,0,now,match.State.Value.Round,new SubLaunchSolution{Position=at,Velocity=velocity,Normal=Vector3.up});
            }
            try
            {
                Place(host,center+Vector3.back*30,1);Place(lower,center+Vector3.down*2,2);
                Place(outside,center+Vector3.down*(config.P.damageHeightDown+.01f),2);
                Place(upper,center+Vector3.up*(config.P.damageHeight-.01f),2);
                var tornado=Tornado();
                for(int frame=1;frame<=133;frame++)
                {
                    match.SpecialWeapons.Step(start+frame/60d,1f/60,match.Players);
                    int ticks=frame<82?0:Math.Min(11,1+(frame-82)/5);
                    Assert.That(lower.Snapshot.Value.Health,Is.EqualTo(1000-37.5f*ticks).Within(.001),$"lower floor, frame {frame}");
                    Assert.That(upper.Snapshot.Value.Health,Is.EqualTo(1000-37.5f*ticks).Within(.001),$"upper boundary, frame {frame}");
                    Assert.That(outside.Snapshot.Value.Health,Is.EqualTo(1000),$"below lower boundary, frame {frame}");
                }
                Assert.That(tornado.Removed,Is.True);
                match.SpecialWeapons.Clear();tornado=Tornado();double active=start+config.P.effectDelay+.5;
                for(int id=1;id<=7;id++)
                {
                    match.SubWeapons.Clear();match.SubWeapons.Effects.Clear();match.SubWeapons.Lifecycle.Clear();
                    var bomb=Bomb(lower,id,center,Vector3.zero,active);Assert.That(bomb,Is.Not.Null);
                    bomb.Fuse=active;bomb.GroundAge=100; // Cancellation wins even when the fuse is due.
                    match.SubWeapons.Step(active,1f/60,match.Players);
                    Assert.That(bomb.Removed,Is.True,$"sub {id} removed without explosion");
                    Assert.That(match.SubWeapons.Effects.Count,Is.Zero,$"sub {id} no explosion effects");
                    Assert.That(match.SubWeapons.Entities.Count,Is.Zero,$"sub {id} no secondary bomblets");
                    Assert.That(match.SubWeapons.Lifecycle.Exists(e=>e.State.Id==bomb.State.Id&&e.Kind==SubLifecycleKind.Remove),Is.True,"Reliable removal event");
                }
                var flying=Bomb(lower,1,center+Vector3.left*8,Vector3.right*1200,active);
                match.SubWeapons.Step(active,1f/60,match.Players);
                Assert.That(flying.Removed,Is.True,"Swept flight detects crossing even when both ends are outside");
                match.SubWeapons.Clear();var friendly=Bomb(host,1,center,Vector3.zero,active);
                match.SubWeapons.Step(active,1f/60,match.Players);Assert.That(friendly.Removed,Is.False,"Friendly bomb is not erased");
                match.SubWeapons.Clear();var warning=Bomb(lower,1,center,Vector3.zero,start+1);
                match.SubWeapons.Step(start+1,1f/60,match.Players);Assert.That(warning.Removed,Is.False,"Warning does not erase bombs");
                match.SubWeapons.Clear();var expired=Bomb(lower,1,center,Vector3.zero,start+config.P.effectDelay+config.P.effectDuration);
                match.SubWeapons.Step(start+config.P.effectDelay+config.P.effectDuration,1f/60,match.Players);Assert.That(expired.Removed,Is.False,"Expired field does not erase bombs");
                match.SubWeapons.Clear();var high=Bomb(lower,1,center+Vector3.up*(config.P.damageHeight+2),Vector3.zero,active);
                match.SubWeapons.Step(active,1f/60,match.Players);Assert.That(high.Removed,Is.False,"Above the field remains safe");
            }
            finally{match.SpecialWeapons.Clear();match.SubWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ReplacingThirdMineKeepsChargeProducedInsidePlayerSimulation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;match.enabled=false;
            foreach(var player in match.Players)player.enabled=false;
            try
            {
                Vector3 point=new(0,0,-20);
                Assert.That(Physics.Raycast(point+Vector3.up*3,Vector3.down,out var floor,6,PlayerMotorSimulation.WorldMask),Is.True);
                var place=typeof(PrototypePlayer).GetMethod("DiagnosticPlace",BindingFlags.Instance|BindingFlags.NonPublic);
                place.Invoke(host,new object[]{floor.point+Vector3.up*.08f,0f});
                var s=host.Snapshot.Value;s.SubWeaponId=8;s.SpecialWeaponId=1;s.SpecialPhase=SpecialPhase.Charging;
                s.SpecialPoints=0;s.SpecialChargeLockedUntil=0;s.SubNeedsRelease=false;s.SubPhase=SubWeaponPhase.Starting;
                s.SubReleaseAt=0;s.SubRecoveryUntil=s.AttackRecoveryUntil=0;s.Ink=100;s.Grounded=true;host.Snapshot.Value=s;
                PrototypeArena.Current.ClearPaint();double now=match.NetworkManager.ServerTime.Time;
                var config=PlayerLoadout.SubWeapon(s);Assert.That(config.Deployment.maxCount,Is.EqualTo(2));
                for(int i=0;i<2;i++)
                {
                    var e=match.SubWeapons.Spawn(host,s,0,now,match.State.Value.Round,new SubLaunchSolution{Position=floor.point+Vector3.right*(i==0?3:-3),Normal=Vector3.up,Surface=floor.collider});
                    Assert.That(e,Is.Not.Null);
                }
                var input=new Splatoon.Networking.PlayerInputFrame{HeroRevision=s.HeroRevision,SpecialSequence=s.SpecialConsumed,JumpSequence=s.ConsumedJump};
                typeof(PrototypePlayer).GetField("_lastInput",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(host,input);
                typeof(PrototypePlayer).GetField("_lastReceivedAt",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(host,now);
                var queued=typeof(PrototypePlayer).GetField("_serverInputs",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(host);
                queued.GetType().GetMethod("Clear").Invoke(queued,null);
                s.SubPhase=SubWeaponPhase.Idle;host.Snapshot.Value=s;
                for(int frame=0;frame<12;frame++)host.Simulate(1f/60,now+frame/60d,Splatoon.Networking.MatchPhase.Practice);
                s=host.Snapshot.Value;s.SubPhase=SubWeaponPhase.Starting;s.SubReleaseAt=0;s.SubNeedsRelease=false;host.Snapshot.Value=s;
                now+=12d/60;
                host.Simulate(1f/60,now,Splatoon.Networking.MatchPhase.Practice);
                Assert.That(host.Snapshot.Value.SubAction,Is.EqualTo(s.SubAction+1),"Third mine commitment: "+host.Snapshot.Value.SubFailure);
                Assert.That(PrototypeArena.Current.PinkArea+PrototypeArena.Current.BlueArea,Is.GreaterThan(0),"Old mine produced real ground coverage");
                Assert.That(host.Snapshot.Value.SpecialPoints,Is.GreaterThan(0),"Commit must not overwrite paint credit from the replaced mine");
            }
            finally{match.SubWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SonarWavesFollowSeparatedFloorsWallsAndAuthoritativeTime()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            var enemy=match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            var geometry=new System.Collections.Generic.List<GameObject>();
            float scale=SpecialWeaponDefaults.Scale;Vector3 origin=new(2000,20,2000);
            double now=match.NetworkManager.ServerTime.Time+10;
            GameObject Box(Vector3 at,Vector3 size)
            {var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.transform.position=at;go.transform.localScale=size;geometry.Add(go);return go;}
            void Place(PrototypePlayer player,Vector3 at,byte team,MovementMode movement=MovementMode.Human)
            {
                var s=player.Snapshot.Value;s.Position=at;s.Team=team;s.Health=100;s.LifeState=PlayerLifeState.Alive;s.ProtectedUntil=0;
                s.SpecialPhase=SpecialPhase.Charging;s.SpecialChargeLockedUntil=0;s.Swimming=s.CompactBody=false;s.AirHumanOffset=0;s.PaperPose=PaperPose.None;
                s.Movement=movement;s.MarkedUntilPink=s.MarkedUntilBlue=0;
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.position=at;cc.enabled=true;
                player.Snapshot.Value=s;player.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            }
            void Frames(int n){for(int f=0;f<n;f++){now+=1d/60;match.SpecialWeapons.Step(now,1f/60,match.Players);}}
            try
            {
                Box(origin+Vector3.down*.25f,new Vector3(1,.5f,1));
                var floor=Box(origin+new Vector3(4,-.25f,0),new Vector3(2,.5f,2));
                var wall=Box(origin+new Vector3(2,1,0),new Vector3(.2f,12,4));
                // There is deliberately no floor between source and target; the wall also
                // blocks direct sight, but neither must stop the cylindrical wave.
                var cases=new[]{(2.6f*scale,0f,false,true), (3f*scale,0f,false,false),
                    (-5f*scale,0f,false,true),(-5.4f*scale,0f,false,false),
                    (0f,1f,false,false),(0f,1f,true,true),(0f,0f,false,true)};
                foreach(var c in cases)
                {
                    match.SpecialWeapons.Clear();floor.transform.position=origin+new Vector3(4,c.Item1-.25f,0);
                    wall.transform.position=origin+new Vector3(c.Item3?4.5f:2,1,0);
                    Place(host,origin+Vector3.back*10,1);
                    Place(enemy,origin+new Vector3(4,c.Item1+c.Item2,0),2,c.Item3?MovementMode.WallInk:MovementMode.Human);
                    Physics.SyncTransforms();var s=host.Snapshot.Value;s.SpecialWeaponId=3;s.SpecialAction++;host.Snapshot.Value=s;
                    var sonar=match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);
                    sonar.State.Position=origin+Vector3.up*.2f;sonar.State.Velocity=Vector3.down*2;
                    for(int f=0;f<30&&sonar.State.Phase==SpecialEntityPhase.Flying;f++)Frames(1);
                    Assert.That(sonar.State.Phase,Is.EqualTo(SpecialEntityPhase.Active));
                    double landed=sonar.State.Changed;double firstHit=0;
                    for(int f=1;f<=220;f++)
                    {
                        Frames(1);
                        if(firstHit==0&&enemy.Snapshot.Value.Health<100)firstHit=now;
                    }
                    Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(c.Item4?55:100).Within(.001),$"Floor height {c.Item1}, airborne {c.Item2}, wall ink {c.Item3}");
                    if(c.Item4)
                    {
                        Assert.That(firstHit,Is.GreaterThanOrEqualTo(landed+90d/60));
                        Assert.That(enemy.Snapshot.Value.MarkedUntilPink,Is.EqualTo(firstHit+8).Within(.00001),"Wave marker must use its authoritative simulation tick, not wall-clock execution speed");
                    }
                }
            }
            finally
            {
                foreach(var go in geometry)UnityEngine.Object.Destroy(go);
                match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;
            }
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RainSeparatesRoofFloorsAndPersistsAfterOwnerDeath()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var upper=match.AddTestBot(prefab);var lower=match.AddTestBot(prefab);var exposed=match.AddTestBot(prefab);
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            Vector3 origin=new(2200,20,2200);double now=match.NetworkManager.ServerTime.Time+20;
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.transform.position=origin+Vector3.up*2.75f;roof.transform.localScale=new Vector3(6,.5f,16);
            void Place(PrototypePlayer player,Vector3 at,byte team)
            {
                var s=player.Snapshot.Value;s.Position=at;s.Team=team;s.Health=100;s.LifeState=PlayerLifeState.Alive;s.ProtectedUntil=0;
                s.SpecialPhase=SpecialPhase.Charging;s.SpecialChargeLockedUntil=0;s.RainRecoveryUntil=0;s.Swimming=s.CompactBody=false;s.AirHumanOffset=0;s.PaperPose=PaperPose.None;
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.position=at;cc.enabled=true;
                player.Snapshot.Value=s;player.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            }
            try
            {
                Place(host,origin+Vector3.left,1);Place(upper,origin+Vector3.up*3,2);Place(lower,origin,2);Place(exposed,origin+Vector3.right*5,2);
                var s=host.Snapshot.Value;s.SpecialWeaponId=4;s.SpecialAction++;host.Snapshot.Value=s;
                var rain=match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);
                rain.State.Position=origin;rain.State.Direction=Vector3.forward;rain.State.Phase=SpecialEntityPhase.Warning;
                rain.State.Changed=now-rain.Config.P.effectDelay;rain.State.Expires=now+20;
                for(int f=1;f<=60;f++)
                {
                    if(f==31)
                    {
                        double locked=host.Snapshot.Value.SpecialChargeLockedUntil;
                        Place(host,origin+Vector3.up*3+Vector3.left,1);
                        var moved=host.Snapshot.Value;moved.SpecialChargeLockedUntil=locked;host.Snapshot.Value=moved;
                    }
                    if(f==41){var dead=host.Snapshot.Value;dead.Health=0;dead.LifeState=PlayerLifeState.Dead;host.Snapshot.Value=dead;}
                    now+=1d/60;match.SpecialWeapons.Step(now,1f/60,match.Players);
                    Assert.That(rain.Removed,Is.False,"Owner death preserves deployed rain");
                    Assert.That(upper.Snapshot.Value.Health,Is.EqualTo(100-.4f*f).Within(.002),$"Upper floor rain frame {f}");
                    Assert.That(exposed.Snapshot.Value.Health,Is.EqualTo(100-.4f*f).Within(.002),$"Exposed lower floor frame {f}");
                    Assert.That(lower.Snapshot.Value.Health,Is.EqualTo(100),"Solid roof shelters only the floor beneath it");
                    if(f<=30)Assert.That(host.Snapshot.Value.RainRecoveryUntil,Is.Zero,"Friendly rain also respects the roof");
                    if(f>=31&&f<=40)
                    {
                        Assert.That(host.Snapshot.Value.Health,Is.EqualTo(100),"Friendly rain never inflicts damage");
                        Assert.That(host.Snapshot.Value.RainRecoveryUntil,Is.EqualTo(now+.1).Within(.00001),"Exposed friend receives the healing flag each simulation frame");
                    }
                }
                Assert.That(host.Snapshot.Value.SpecialChargeLockedUntil,Is.GreaterThan(now+6.9),"Owner death preserves the effect-start charge lock");
            }
            finally
            {
                UnityEngine.Object.Destroy(roof);match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;
            }
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ThrowPreviewAndFrameRateSamplingMatchAuthorityAtWalls()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            Vector3 origin=new(2400,20,2400);
            var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);ground.transform.position=origin+Vector3.down*.25f;ground.transform.localScale=new Vector3(20,.5f,20);
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=origin+new Vector3(0,3,3);wall.transform.localScale=new Vector3(10,6,.2f);
            Physics.SyncTransforms();double now=match.NetworkManager.ServerTime.Time;
            try
            {
                foreach(int id in new[]{2,3,4})
                {
                    match.SpecialWeapons.Clear();var s=host.Snapshot.Value;s.SpecialWeaponId=id;s.SpecialAction++;host.Snapshot.Value=s;
                    var authority=match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);
                    authority.State.Position=origin+Vector3.up*2;authority.State.Velocity=Vector3.forward*40;
                    var start=authority.State;var config=authority.Config;Vector3 preview=start.Position,velocity=start.Velocity;
                    var trajectory=new System.Collections.Generic.List<Vector3>{preview};
                    bool previewLanded=false;
                    for(int f=0;f<180&&!previewLanded;f++)
                    {previewLanded=SpecialWeaponService.AdvanceThrow(config,ref preview,ref velocity,out _);trajectory.Add(preview);}
                    Assert.That(previewLanded,Is.True);
                    for(int f=0;f<180&&authority.State.Phase==SpecialEntityPhase.Flying;f++)
                    {now+=1d/60;match.SpecialWeapons.Step(now,1f/60,match.Players);}
                    Assert.That(authority.State.Phase,Is.Not.EqualTo(SpecialEntityPhase.Flying));
                    Assert.That(Vector3.Distance(preview,authority.State.Position),Is.LessThan(.00001f),"Preview and Host deployment contact");
                    if(id==3)
                    {
                        Assert.That(authority.State.Position.y,Is.EqualTo(origin.y+.025f).Within(.002),"Sonar continues down to the floor after hitting the wall");
                        Assert.That(authority.State.Normal.y,Is.GreaterThan(.99));
                    }
                    else Assert.That(authority.State.Normal.z,Is.LessThan(-.99),"Locator and rain generator activate at wall contact");
                    foreach(int fps in new[]{30,60,144})
                    {
                        var predicted=new SpecialWeaponService.Entity(start,config);
                        for(int f=1;f<=fps*3;f++)
                        {
                            SpecialWeaponService.SampleThrow(predicted,start.Born+f/(double)fps);
                            int tick=Math.Min(trajectory.Count-1,(int)Math.Floor(f*60d/fps+1e-6));
                            Assert.That(predicted.ThrowFrames,Is.EqualTo(tick),$"Type {id}, {fps} FPS, sample {f}");
                            Assert.That(Vector3.Distance(predicted.State.Position,trajectory[tick]),Is.LessThan(.00001f),"Every sampled position, not only the eventual landing");
                        }
                        Assert.That(predicted.ThrowLanded,Is.True);
                        Assert.That(Vector3.Distance(predicted.State.Position,authority.State.Position),Is.LessThan(.00001f),$"Type {id}, {fps} FPS display sampling");
                        Assert.That(predicted.State.Velocity,Is.EqualTo(Vector3.zero));
                        var stopped=predicted.State.Position;SpecialWeaponService.SampleThrow(predicted,start.Born+3);
                        Assert.That(predicted.State.Position,Is.EqualTo(stopped),"Repeated rendering of one timestamp cannot advance a throw");
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(ground);UnityEngine.Object.Destroy(wall);match.SpecialWeapons.Clear();match.enabled=true;
                foreach(var player in match.Players)player.enabled=true;
            }
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RememberedLoadoutRestoresInPlayingSpawnAndWaitsThroughSettlement()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null&&PrototypeMatch.Current.InitialSyncComplete&&!PrototypePlayer.Local.HeroChangePending);
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;match.enabled=false;host.enabled=false;
            int hero=host.Snapshot.Value.HeroId;
            string subKey="Loadout.Sub."+hero,specialKey="Loadout.Special."+hero;
            bool hadSub=PlayerPrefs.HasKey(subKey),hadSpecial=PlayerPrefs.HasKey(specialKey);
            int savedSub=PlayerPrefs.GetInt(subKey),savedSpecial=PlayerPrefs.GetInt(specialKey);
            var restore=typeof(PrototypePlayer).GetMethod("RestoreInitialLoadout",BindingFlags.Instance|BindingFlags.NonPublic);
            var applied=typeof(PrototypePlayer).GetField("_initialLoadoutApplied",BindingFlags.Instance|BindingFlags.NonPublic);
            void Defaults()
            {
                host.Respawn();var s=host.Snapshot.Value;s.SubWeaponId=1;s.SpecialWeaponId=1;s.SpecialPoints=123;host.Snapshot.Value=s;
                applied.SetValue(host,false);
            }
            void Process()
            {host.Simulate(1f/60,match.NetworkManager.ServerTime.Time,match.State.Value.Phase);host.RefreshHeroChangeStatus();}
            try
            {
                new PlayerLoadout(hero,8,4).Save();
                var phase=match.State.Value;phase.Phase=Splatoon.Networking.MatchPhase.Playing;phase.EndsAt=match.NetworkManager.ServerTime.Time+300;match.State.Value=phase;
                Defaults();Assert.That(PrototypeArena.Current.IsInHeroChangeZone(host.Snapshot.Value.Team,host.Snapshot.Value.Position),Is.True);
                restore.Invoke(host,null);Process();
                Assert.That(host.Snapshot.Value.SubWeaponId,Is.EqualTo(8),"Saved sub must restore when initially synchronized in a playing match");
                Assert.That(host.Snapshot.Value.SpecialWeaponId,Is.EqualTo(4));
                Assert.That(host.Snapshot.Value.SpecialPoints,Is.Zero,"Actual restoration follows normal atomic loadout rules");
                var before=host.Snapshot.Value;restore.Invoke(host,null);Process();
                Assert.That(host.Snapshot.Value.HeroRevision,Is.EqualTo(before.HeroRevision),"No duplicate restore revision");

                phase.Phase=Splatoon.Networking.MatchPhase.Finished;match.State.Value=phase;Defaults();
                restore.Invoke(host,null);Process();
                Assert.That(host.Snapshot.Value.SubWeaponId,Is.EqualTo(1));
                Assert.That((bool)applied.GetValue(host),Is.False,"Settlement must defer, not consume initial restoration");
                phase.Phase=Splatoon.Networking.MatchPhase.Practice;match.State.Value=phase;
                restore.Invoke(host,null);Process();
                Assert.That(host.Snapshot.Value.SubWeaponId,Is.EqualTo(8));Assert.That(host.Snapshot.Value.SpecialWeaponId,Is.EqualTo(4));
                var selection=PlayerLoadout.From(host.Snapshot.Value);
                var edgeField=typeof(PrototypePlayer).GetField("_specialSequence",BindingFlags.Instance|BindingFlags.NonPublic);
                var edgeState=host.Snapshot.Value;edgeState.SpecialConsumed=3;host.Snapshot.Value=edgeState;
                edgeField.SetValue(host,9u); // A locally captured Q never acknowledged in the old life.
                host.Respawn();
                Assert.That(PlayerLoadout.From(host.Snapshot.Value).Matches(selection),Is.True,"Ordinary respawn keeps restored loadout");
                Assert.That((uint)edgeField.GetValue(host),Is.EqualTo(3),"Respawn must scope local Q edges to the new authoritative life");
                edgeField.SetValue(host,9u);
                host.RequestLoadoutChange(new PlayerLoadout(hero,1,1),HeroSelectionOrigin.Warmup);Process();
                Assert.That(host.Snapshot.Value.SubWeaponId,Is.EqualTo(1));
                Assert.That((uint)edgeField.GetValue(host),Is.EqualTo(host.Snapshot.Value.SpecialConsumed),"New equipment also rebases unacknowledged local Q");
            }
            finally
            {
                if(hadSub)PlayerPrefs.SetInt(subKey,savedSub);else PlayerPrefs.DeleteKey(subKey);
                if(hadSpecial)PlayerPrefs.SetInt(specialKey,savedSpecial);else PlayerPrefs.DeleteKey(specialKey);
                PlayerPrefs.Save();applied.SetValue(host,true);match.enabled=true;host.enabled=true;
            }
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SonarInitialSensorUsesBodyOverlapIncludingHeadAndCompactForm()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            var enemy=match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            var p=SpecialWeaponConfigService.Current.Get(3).P;var shape=HeroBodyShape.For(enemy.Snapshot.Value.HeroId);
            Vector3 origin=new(2600,20,2600);double now=match.NetworkManager.ServerTime.Time+10;
            float radius=.615f*p.waveRadius;
            var cases=new[]{
                (2f,-p.waveHeightDown-shape.Height+.1f,false,true,"head enters below lower plane"),
                (2f,-p.waveHeightDown-shape.Height+.1f,true,false,"flat body stays below lower plane"),
                (2f,0f,true,true,"flat body inside sensor"),
                (2f,-p.waveHeightDown-shape.Height-.1f,false,false,"entire body below lower plane"),
                (2f,p.waveHeightUp+.1f,false,false,"entire body above upper plane"),
                (radius+shape.Radius*.5f,0f,false,true,"capsule side enters horizontal rim"),
                (radius+shape.Radius+.1f,0f,false,false,"capsule outside horizontal rim"),
                (radius+shape.Radius*.8f,-p.waveHeightDown-shape.Height+shape.Radius*.2f,false,false,"rounded corner misses despite overlapping bounds")};
            try
            {
                foreach(var c in cases)
                {
                    match.SpecialWeapons.Clear();now+=1;
                    var s=enemy.Snapshot.Value;s.Position=origin+new Vector3(c.Item1,c.Item2,0);s.Team=2;s.Health=100;s.LifeState=PlayerLifeState.Alive;
                    s.ProtectedUntil=0;s.SpecialPhase=SpecialPhase.Charging;s.MarkedUntilPink=s.MarkedUntilBlue=0;s.AirHumanOffset=0;
                    s.Swimming=s.CompactBody=c.Item3;s.Grounded=true;s.Movement=MovementMode.Human;s.PaperPose=PaperPose.None;
                    var cc=enemy.GetComponent<CharacterController>();cc.enabled=false;enemy.transform.position=s.Position;shape.Apply(cc,c.Item3);cc.enabled=true;
                    enemy.Snapshot.Value=s;enemy.SwimBody.ApplyCollision(s);Physics.SyncTransforms();
                    var owner=host.Snapshot.Value;owner.SpecialWeaponId=3;owner.SpecialAction++;owner.Team=1;host.Snapshot.Value=owner;
                    var sonar=match.SpecialWeapons.Spawn(host,owner,now,match.State.Value.Round);
                    sonar.State.Position=origin;sonar.State.Phase=SpecialEntityPhase.Active;sonar.State.Changed=now-20d/60;
                    match.SpecialWeapons.Step(now,1f/60,match.Players);
                    Assert.That(enemy.Snapshot.Value.MarkedUntilPink>now,Is.EqualTo(c.Item4),c.Item5);
                    Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100),"Initial sensor never deals wave damage");
                }
                // Exercise the captured silhouette directly: a tiny sensor at an
                // occupied cell must hit in every orientation; empty AABB space must not.
                var paper=enemy.Snapshot.Value;paper.Swimming=paper.CompactBody=true;paper.PaperPose=PaperPose.None;
                enemy.Snapshot.Value=paper;enemy.SwimBody.ApplyCollision(paper);Physics.SyncTransforms();
                var body=enemy.SwimBody;Assert.That(body.FlatHitActive,Is.True);
                var rects=body.HitRects;Assert.That(rects.Count,Is.GreaterThan(0));
                float xmin=float.PositiveInfinity,xmax=float.NegativeInfinity,ymin=xmin,ymax=xmax;
                foreach(var rect in rects){xmin=Mathf.Min(xmin,rect.xMin);xmax=Mathf.Max(xmax,rect.xMax);ymin=Mathf.Min(ymin,rect.yMin);ymax=Mathf.Max(ymax,rect.yMax);}
                Vector2 empty=default;float clearance=0;
                for(int y=0;y<=24;y++)for(int x=0;x<=24;x++)
                {
                    Vector2 point=new(Mathf.Lerp(xmin,xmax,x/24f),Mathf.Lerp(ymin,ymax,y/24f));float nearest=float.PositiveInfinity;
                    foreach(var rect in rects)nearest=Mathf.Min(nearest,Vector2.Distance(point,new Vector2(Mathf.Clamp(point.x,rect.xMin,rect.xMax),Mathf.Clamp(point.y,rect.yMin,rect.yMax))));
                    if(nearest>clearance){clearance=nearest;empty=point;}
                }
                Assert.That(clearance,Is.GreaterThan(.04f),"Captured silhouette must provide real empty space within its bounds");
                foreach(var rotation in new[]{Quaternion.identity,Quaternion.Euler(90,0,0),Quaternion.Euler(37,29,13)})
                {
                    body.HitVolume.transform.SetPositionAndRotation(origin,rotation);Physics.SyncTransforms();
                    for(int i=0;i<rects.Count;i++)
                    {
                        Vector2 point=rects[i].center;Vector3 world=body.HitVolume.transform.TransformPoint(new Vector3(point.x,point.y,0));
                        Assert.That(InkExplosionRules.IntersectsCylinder(enemy,world,.005f,.005f,.005f),Is.True,$"Occupied silhouette cell {i}, rotation {rotation}");
                    }
                    Vector3 hole=body.HitVolume.transform.TransformPoint(new Vector3(empty.x,empty.y,0));
                    Assert.That(InkExplosionRules.IntersectsCylinder(enemy,hole,.005f,.005f,.005f),Is.False,$"Empty silhouette bounds, rotation {rotation}");
                    Vector2 center=rects[0].center;
                    Vector3 outsideThickness=body.HitVolume.transform.TransformPoint(new Vector3(center.x,center.y,body.Profile.Thickness*.5f+.04f));
                    Assert.That(InkExplosionRules.IntersectsCylinder(enemy,outsideThickness,.005f,.005f,.005f),Is.False,$"Outside silhouette thickness, rotation {rotation}");
                }
                Debug.Log($"[SONAR-BODY] Checked {rects.Count} occupied cells in 3 orientations, empty-bounds clearance={clearance}");
            }
            finally {match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator FriendlyRainActuallyHealsAfterDelayWithoutStackingAndStopsUnderRoof()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null&&PrototypeMatch.Current.InitialSyncComplete);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;match.enabled=false;host.enabled=false;
            var hero=GameplayConfig.GetHero(host.Snapshot.Value.HeroId);Vector3 origin=new(2800,20,2800);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=origin+Vector3.down*.25f;floor.transform.localScale=new Vector3(20,.5f,20);
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.transform.position=origin+Vector3.up*3;roof.transform.localScale=new Vector3(20,.5f,20);roof.SetActive(false);
            double start=match.NetworkManager.ServerTime.Time+10,now=start;const float dt=1f/60;
            try
            {
                var s=host.Snapshot.Value;s.Position=origin;s.Health=20;s.LastDamageAt=start;s.ProtectedUntil=0;s.RainRecoveryUntil=0;
                s.SpecialPhase=SpecialPhase.Charging;s.SpecialWeaponId=4;s.SpecialPoints=0;s.Swimming=s.CompactBody=false;s.PaperPose=PaperPose.None;s.AirHumanOffset=0;
                var cc=host.GetComponent<CharacterController>();cc.enabled=false;host.transform.position=origin;cc.enabled=true;host.Snapshot.Value=s;host.SwimBody.ApplyCollision(s);
                for(int i=0;i<2;i++)
                {
                    var rain=match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);
                    rain.State.Phase=SpecialEntityPhase.Active;rain.State.Position=origin+Vector3.up*6;rain.State.Direction=Vector3.zero;rain.State.Expires=now+8;
                }
                Physics.SyncTransforms();Assert.That(hero.HealthRecoverDelay,Is.EqualTo(1),"No-gear damage recovery delay remains 60 frames");
                for(int frame=1;frame<=90;frame++)
                {
                    now=start+frame/60d;
                    // Match.FixedUpdate runs player resources before the storm service.
                    host.Simulate(dt,now,Splatoon.Networking.MatchPhase.Practice);match.SpecialWeapons.Step(now,dt,match.Players);
                    float expected=20+Mathf.Max(0,frame-59)*hero.SwimHealthRecoverRate/60;
                    Assert.That(host.Snapshot.Value.Health,Is.EqualTo(expected).Within(.002),$"Frame {frame}: human in two friendly clouds, no stacked healing");
                }
                roof.SetActive(true);Physics.SyncTransforms();
                for(int frame=91;frame<=105;frame++)
                {
                    now=start+frame/60d;float before=host.Snapshot.Value.Health;
                    host.Simulate(dt,now,Splatoon.Networking.MatchPhase.Practice);match.SpecialWeapons.Step(now,dt,match.Players);
                    if(frame>=100)Assert.That(host.Snapshot.Value.Health-before,Is.EqualTo(hero.HealthRecoverRate/60).Within(.002),"Roof returns healing to ordinary rate after existing rain flag expires");
                }
                var hurt=host.Snapshot.Value;hurt.Health=20;hurt.LastDamageAt=now;host.Snapshot.Value=hurt;
                double hitAt=now;roof.SetActive(false);Physics.SyncTransforms();
                for(int frame=1;frame<=60;frame++)
                {
                    now=hitAt+frame/60d;host.Simulate(dt,now,Splatoon.Networking.MatchPhase.Practice);match.SpecialWeapons.Step(now,dt,match.Players);
                    Assert.That(host.Snapshot.Value.Health,Is.EqualTo(frame<60?20:20+hero.SwimHealthRecoverRate/60).Within(.002),"New damage restarts full delay despite friendly rain");
                }
            }
            finally{UnityEngine.Object.Destroy(floor);UnityEngine.Object.Destroy(roof);match.SpecialWeapons.Clear();match.enabled=true;host.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SpecialBlastUsesEnabledHumanAndPaperHitProxies()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            var enemy=match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            var blast=typeof(SpecialWeaponService).GetMethod("Blast",BindingFlags.Instance|BindingFlags.NonPublic);
            var controller=enemy.GetComponent<CharacterController>();Vector3 origin=new(3000,20,3000);GameObject wall=null;
            void Place(float distance,bool paper,bool controllerEnabled)
            {
                var s=enemy.Snapshot.Value;s.Team=2;s.Health=1000;s.LifeState=PlayerLifeState.Alive;s.ProtectedUntil=0;s.SpecialPhase=SpecialPhase.Charging;
                s.Position=origin+Vector3.right*distance;s.Swimming=s.CompactBody=paper;s.AirHumanOffset=0;
                s.Movement=paper?MovementMode.WallInk:MovementMode.Human;s.PaperPose=paper?PaperPose.Wall:PaperPose.None;
                s.PaperCenter=s.Position+Vector3.up*.9f;s.PaperRotation=Quaternion.identity;
                controller.enabled=false;enemy.transform.position=s.Position;controller.enabled=controllerEnabled;
                enemy.Snapshot.Value=s;enemy.SwimBody.ApplyCollision(s);Physics.SyncTransforms();
            }
            try
            {
                foreach(int id in new[]{1,5})foreach(bool paper in new[]{false,true})foreach(bool enabled in new[]{false,true})
                {
                    var config=SpecialWeaponConfigService.Current.Get(id);
                    var entity=new SpecialWeaponService.Entity(new SpecialEntityState{Owner=host.PlayerId,Team=1,Type=config.Type,Round=match.State.Value.Round},config);
                    Place(config.P.outerRadius+5,paper,enabled);
                    blast.Invoke(match.SpecialWeapons,new object[]{entity,origin+Vector3.up*.9f,null,false});
                    Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(1000),$"Special {id}, paper={paper}, controller={enabled}: target outside blast must not be hit");
                    Place(config.P.innerRadius*.5f,paper,enabled);
                    blast.Invoke(match.SpecialWeapons,new object[]{entity,origin+Vector3.up*.9f,null,false});
                    Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(1000-config.P.innerDamage).Within(.001),$"Special {id}, paper={paper}, controller={enabled}: active hit proxy takes inner blast");
                }
                foreach(int id in new[]{1,5})
                {
                    var config=SpecialWeaponConfigService.Current.Get(id);Place(0,true,true);
                    var flat=enemy.Snapshot.Value;flat.PaperPose=PaperPose.Ground;flat.Movement=MovementMode.GroundInk;
                    flat.PaperCenter=origin;flat.PaperRotation=Quaternion.Euler(90,0,0);enemy.Snapshot.Value=flat;
                    HeroBodyShape.For(flat.HeroId).Apply(controller,true);enemy.SwimBody.ApplyCollision(flat);Physics.SyncTransforms();
                    var entity=new SpecialWeaponService.Entity(new SpecialEntityState{Owner=host.PlayerId,Team=1,Type=config.Type,Round=match.State.Value.Round},config);
                    blast.Invoke(match.SpecialWeapons,new object[]{entity,origin+Vector3.up*(config.P.innerRadius+.2f),null,false});
                    Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(1000-config.P.outerDamage).Within(.001),$"Special {id}: flat ground silhouette takes outer damage; compact movement capsule must not promote it to inner damage");
                }
                var reef=SpecialWeaponConfigService.Current.Get(5);Place(2,true,false);
                wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=origin+new Vector3(1,1,0);wall.transform.localScale=new Vector3(.2f,4,4);Physics.SyncTransforms();
                var blocked=new SpecialWeaponService.Entity(new SpecialEntityState{Owner=host.PlayerId,Team=1,Type=reef.Type,Round=match.State.Value.Round},reef);
                blast.Invoke(match.SpecialWeapons,new object[]{blocked,origin+Vector3.up*.9f,null,false});
                Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(1000),"Wall still blocks explosion to paper hit proxy");
            }
            finally{if(wall!=null)UnityEngine.Object.Destroy(wall);controller.enabled=true;match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ReefsliderPeripheralSplashesPaintAfterBurstAndOwnerDeath()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;match.enabled=false;host.enabled=false;
            Assert.That(Physics.Raycast(new Vector3(0,3,-20),Vector3.down,out var floor,6,PlayerMotorSimulation.WorldMask),Is.True);
            double born=match.NetworkManager.ServerTime.Time+10;var config=SpecialWeaponConfigService.Current.Get(5);
            try
            {
                PrototypeArena.Current.ClearPaint();
                var s=host.Snapshot.Value;s.Position=floor.point+Vector3.up*.02f;s.Health=100;s.LifeState=PlayerLifeState.Alive;s.SpecialWeaponId=5;s.SpecialAction++;
                s.SpecialPhase=SpecialPhase.Recovering;s.SpecialPoints=0;s.SpecialChargeLockedUntil=born+10;s.Swimming=s.CompactBody=false;s.AirHumanOffset=0;
                host.Snapshot.Value=s;
                var reef=match.SpecialWeapons.Spawn(host,s,born,match.State.Value.Round);uint initialPaint=match.PaintSequence;
                Assert.That(match.SpecialWeapons.Lifecycle[match.SpecialWeapons.Lifecycle.Count-1].State.Expires,Is.EqualTo(reef.State.Expires),"Reliable activation carries final fragment lifetime");
                var starts=(Vector3[])reef.ReefSplashPositions.Clone();var velocities=(Vector3[])reef.ReefSplashVelocities.Clone();
                Assert.That(reef.ReefSplashPositions.Length,Is.EqualTo(15));
                for(int i=0;i<15;i++)
                {
                    Assert.That(reef.ReefSplashPositions[i].y,Is.EqualTo(s.Position.y+.3f*SpecialWeaponDefaults.Scale).Within(.0001));
                    float speed=reef.ReefSplashVelocities[i].magnitude;
                    Assert.That(speed,Is.InRange(config.P.reefSplashSpeedMin-.0001f,config.P.reefSplashSpeedMax+.0001f));
                    float pitch=Mathf.Asin(reef.ReefSplashVelocities[i].y/speed)*Mathf.Rad2Deg;Assert.That(pitch,Is.InRange(0,30.0001f));
                }
                for(int frame=1;frame<=120;frame++)
                {
                    if(frame==2){var dead=host.Snapshot.Value;dead.Health=0;dead.LifeState=PlayerLifeState.Dead;host.Snapshot.Value=dead;}
                    match.SpecialWeapons.Step(born+frame/60d,1f/60,match.Players);
                }
                Assert.That(match.PaintSequence,Is.GreaterThan(initialPaint),"Peripheral flights produce authoritative paint beyond the immediate disk");
                float farthest=0;
                for(int i=0;i<15;i++)
                {
                    Assert.That(reef.ReefSplashDone[i],Is.True,$"Splash {i} contacts arena geometry even after owner death");
                    Vector3 d=reef.ReefSplashPositions[i]-s.Position;farthest=Mathf.Max(farthest,new Vector2(d.x,d.z).magnitude);
                }
                Assert.That(farthest+config.P.reefSplashPaintRadius,Is.GreaterThan(config.P.paintRadius),"Peripheral ink extends beyond the central disk");
                Assert.That(host.Snapshot.Value.SpecialPoints,Is.Zero,"Dead/locked owner receives no self charge");
                Assert.That(reef.Removed,Is.True,"Completed splashes release authority entity");
                uint finishedPaint=match.PaintSequence;match.SpecialWeapons.Step(born+3,1f/60,match.Players);Assert.That(match.PaintSequence,Is.EqualTo(finishedPaint),"No repeated landing stamps");
                host.Snapshot.Value=s;
                var batched=match.SpecialWeapons.Spawn(host,s,born+4,match.State.Value.Round);
                batched.ReefSplashPositions=(Vector3[])starts.Clone();batched.ReefSplashVelocities=(Vector3[])velocities.Clone();
                for(int sample=1;sample<=60;sample++)match.SpecialWeapons.Step(born+4+sample/30d,1f/30,match.Players);
                for(int i=0;i<15;i++)Assert.That(Vector3.Distance(batched.ReefSplashPositions[i],reef.ReefSplashPositions[i]),Is.LessThan(.00001f),$"Splash {i}: 30Hz callbacks preserve the same 60Hz contact point");
                Assert.That(batched.Removed,Is.True);
                var cancelled=match.SpecialWeapons.Spawn(host,s,born+7,match.State.Value.Round);uint beforeClear=match.PaintSequence;
                match.SpecialWeapons.ClearOwner(host.PlayerId);match.SpecialWeapons.Step(born+8,1f/60,match.Players);
                Assert.That(cancelled.Removed,Is.True);Assert.That(match.PaintSequence,Is.EqualTo(beforeClear),"Changing loadout clears pending peripheral paint");
                Debug.Log($"[REEF-SPLASH] All 15 landed, furthest centre={farthest}, paint events={finishedPaint-initialPaint}");
            }
            finally{match.SpecialWeapons.Clear();match.enabled=true;host.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator AirborneReefsliderWaitsForLandingBeforeStartupAndInvincibility()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;match.enabled=false;host.enabled=false;
            Vector3 origin=new(3200,20,3200);var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=origin+Vector3.down*.5f;floor.transform.localScale=new Vector3(100,1,100);
            var place=typeof(PrototypePlayer).GetMethod("DiagnosticPlace",BindingFlags.Instance|BindingFlags.NonPublic);
            var step=typeof(PrototypePlayer).GetMethod("Step",BindingFlags.Instance|BindingFlags.NonPublic);
            place.Invoke(host,new object[]{origin+Vector3.up*20,0f});Physics.SyncTransforms();
            var state=host.Snapshot.Value;state.Grounded=false;state.VerticalSpeed=0;state.SpecialWeaponId=5;state.SpecialPoints=200;state.SpecialPhase=SpecialPhase.Charging;state.SpecialChargeLockedUntil=0;
            var input=new Splatoon.Networking.PlayerInputFrame{HeroRevision=state.HeroRevision,SpecialSequence=state.SpecialConsumed+1,Look=Vector2.zero};
            double start=match.NetworkManager.ServerTime.Time;var config=SpecialWeaponConfigService.Current.Get(5);
            void Frame(int frame){host.Snapshot.Value=state;var args=new object[]{state,input,1f/60,start+frame/60d,Splatoon.Networking.MatchPhase.Practice};step.Invoke(host,args);state=(PlayerSnapshot)args[0];host.Snapshot.Value=state;Physics.SyncTransforms();}
            try
            {
                int landed=-1;
                for(int frame=0;frame<240;frame++)
                {
                    Frame(frame);if(state.Grounded){landed=frame;break;}
                    Assert.That(state.SpecialPhase,Is.EqualTo(SpecialPhase.Starting));
                    Assert.That(state.SpecialRideSpeed,Is.Zero,"Airborne activation cannot begin the dash before landing");
                    Assert.That(SpecialWeaponSimulation.Invincible(state,config,start+frame/60d),Is.False,"Waiting for the floor is vulnerable");
                    Assert.That(state.SpecialPoints,Is.EqualTo(200),"Gauge starts consumption with grounded preparation");
                }
                Assert.That(landed,Is.GreaterThan(38),"Fixture falls longer than the entire grounded startup");
                Assert.That(state.SpecialStartedAt,Is.EqualTo(start+landed/60d).Within(.000001));
                Assert.That(state.SpecialPoints,Is.Zero);
                for(int offset=1;offset<=38;offset++)
                {
                    Frame(landed+offset);
                    Assert.That(SpecialWeaponSimulation.Invincible(state,config,start+(landed+offset)/60d),Is.EqualTo(offset>=25),$"Post-landing invincibility frame {offset}");
                    Assert.That(state.SpecialRideStage,Is.EqualTo(offset<38?0:1),$"Post-landing startup frame {offset}");
                }
                Assert.That(state.Position.z,Is.GreaterThan(origin.z));
            }
            finally{UnityEngine.Object.Destroy(floor);match.SpecialWeapons.Clear();match.enabled=true;host.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator StormContactsDeploymentsAndDestroyedSonarRetainsOnlyEmittedWaves()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;match.enabled=false;host.enabled=false;
            Vector3 origin=new(3400,20,3400);double now=match.NetworkManager.ServerTime.Time;
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=origin+Vector3.down*.5f;floor.transform.localScale=new Vector3(20,1,20);
            var targetObject=new GameObject("Functional deployment target");
            var wallConfig=SubWeaponConfigService.Current.GetById(13);
            var target=targetObject.AddComponent<SubWeaponTarget>();
            target.Initialize(null,new SubEntityState{Id=900,Team=2,Type=SubWeaponType.SplashWall,Phase=SubEntityPhase.Active,Position=origin+Vector3.forward*2,Changed=now-10},wallConfig);
            var config=SpecialWeaponConfigService.Current.Get(4);
            SpecialWeaponService.Entity Spawn(int id)
            {
                var s=host.Snapshot.Value;s.SpecialWeaponId=id;s.SpecialAction++;s.SpecialChargeLockedUntil=now+100;s.SpecialPhase=SpecialPhase.Charging;host.Snapshot.Value=s;
                return match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);
            }
            void Tick(){now+=1d/60;match.SpecialWeapons.Step(now,1f/60,match.Players);}
            try
            {
                Physics.SyncTransforms();var rain=Spawn(4);rain.State.Position=origin+Vector3.up;rain.State.Velocity=Vector3.forward*config.P.throwSpeed;
                var predicted=new SpecialWeaponService.Entity(rain.State,config);
                Vector3 preview=rain.State.Position,previewVelocity=rain.State.Velocity;bool previewContact=false;
                for(int i=0;i<60&&!previewContact;i++)previewContact=SpecialWeaponService.AdvanceThrow(config,ref preview,ref previewVelocity,out _);
                Assert.That(previewContact,Is.True);
                for(int i=0;i<60&&rain.State.Phase==SpecialEntityPhase.Flying;i++)Tick();
                Assert.That(rain.State.Phase,Is.EqualTo(SpecialEntityPhase.Warning),"Rain generator activates on a deployed wall");
                Assert.That(rain.State.Normal.z,Is.LessThan(0),"Activation is on wall front, not the floor behind it");
                Assert.That(rain.State.Position,Is.EqualTo(preview),"Preview and Host select the same object contact");
                SpecialWeaponService.SampleThrow(predicted,now);Assert.That(predicted.ThrowLanded,Is.True);
                Assert.That(predicted.State.Position,Is.EqualTo(rain.State.Position),"Prediction lands on the same object");
                Assert.That(SpecialWeaponService.ThrowCollision(config,target.HitCollider.bounds.center,Vector3.forward,out var embedded),Is.True,"Spawn overlapping a deployment must activate instead of escaping it");
                Assert.That(embedded.Collider,Is.SameAs(target.HitCollider));
                for(int i=0;i<120&&rain.State.Phase!=SpecialEntityPhase.Active;i++)Tick();
                Assert.That(rain.State.Phase,Is.EqualTo(SpecialEntityPhase.Active),"Object impact proceeds into actual rain");
                match.SpecialWeapons.Clear();targetObject.SetActive(false);Physics.SyncTransforms();
                var sprinklerObject=new GameObject("Functional trigger deployment");
                try
                {
                    var sprinkler=sprinklerObject.AddComponent<SubWeaponTarget>();
                    sprinkler.Initialize(null,new SubEntityState{Id=901,Team=1,Type=SubWeaponType.Sprinkler,Phase=SubEntityPhase.Active,Position=origin+Vector3.up+Vector3.forward*2},SubWeaponConfigService.Current.GetById(12));
                    Physics.SyncTransforms();Assert.That(sprinkler.HitCollider.isTrigger,Is.True);
                    Assert.That(SpecialWeaponService.ThrowCollision(config,origin+Vector3.up,Vector3.forward*4,out var triggerHit),Is.True,"Trigger deployment bodies participate in generator contact");
                    Assert.That(triggerHit.Collider,Is.SameAs(sprinkler.HitCollider));
                }
                finally{sprinklerObject.SetActive(false);UnityEngine.Object.Destroy(sprinklerObject);}
                foreach(bool emitFirst in new[]{false,true})
                {
                    var sonar=Spawn(3);sonar.State.Position=origin+Vector3.up*.3f;sonar.State.Velocity=Vector3.down*2;
                    for(int i=0;i<90&&sonar.Target==null;i++)Tick();
                    Assert.That(sonar.Target,Is.Not.Null,"Sonar exposes an actual damage target after deployment");
                    var originalTarget=sonar.Target;originalTarget.Damage(sonar.State.Team,float.MaxValue);
                    Assert.That(sonar.Target,Is.SameAs(originalTarget),"Friendly damage does not destroy the sonar");
                    if(emitFirst)for(int i=0;i<300&&sonar.State.Pulse==0;i++)Tick();
                    if(emitFirst)Assert.That(sonar.State.Pulse,Is.GreaterThan(0));
                    int emitted=sonar.State.Pulse;originalTarget.Damage((byte)(3-sonar.State.Team),float.MaxValue);
                    Assert.That(sonar.Target,Is.Null,"Enemy destruction removes the hit collider immediately");
                    Assert.That(originalTarget.gameObject.activeSelf,Is.False);
                    Assert.That(sonar.Removed,Is.EqualTo(!emitFirst),"Only already emitted waves outlive destruction");
                    Assert.That(SpecialWeaponSimulation.CanCharge(host.Snapshot.Value,now),Is.True,"Destroying own sonar releases charge lock");
                    for(int i=0;i<600&&!sonar.Removed;i++){Tick();Assert.That(sonar.State.Pulse,Is.EqualTo(emitted),"Destroyed sonar cannot emit another wave");}
                    Assert.That(sonar.Removed,Is.True,"Remaining waves finish and remove their authority entity");
                    match.SpecialPresentation.Apply(match.SpecialWeapons.Capture(),match.SpecialWeapons.Watermark);
                    Assert.That(match.SpecialPresentation.Count,Is.Zero,"Snapshot compensation clears destroyed objects and spent waves");
                }
            }
            finally{targetObject.SetActive(false);UnityEngine.Object.Destroy(targetObject);UnityEngine.Object.Destroy(floor);match.SpecialWeapons.Clear();match.enabled=true;host.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SpecialKnockbackAndAirRecoilMoveThroughCollisionAndReset()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready);
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null);
            var host=PrototypePlayer.Local;var match=PrototypeMatch.Current;
            var enemy=match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            Vector3 origin=new(3600,20,3600);double now=match.NetworkManager.ServerTime.Time+10;
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=origin+Vector3.down*.5f;floor.transform.localScale=new Vector3(50,1,50);
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=origin+new Vector3(3,2,0);wall.transform.localScale=new Vector3(.2f,4,10);wall.SetActive(false);
            var place=typeof(PrototypePlayer).GetMethod("DiagnosticPlace",BindingFlags.Instance|BindingFlags.NonPublic);
            var step=typeof(PrototypePlayer).GetMethod("Step",BindingFlags.Instance|BindingFlags.NonPublic);
            void Place(PrototypePlayer p,Vector3 at,byte team)
            {place.Invoke(p,new object[]{at,0f});var s=p.Snapshot.Value;s.Team=team;s.Health=100;s.ProtectedUntil=0;s.SpecialPhase=SpecialPhase.Charging;s.SpecialWeaponId=1;s.SpecialImpulseVelocity=Vector3.zero;s.SpecialImpulseRemaining=0;p.Snapshot.Value=s;p.SwimBody.ApplyCollision(s);Physics.SyncTransforms();}
            try
            {
                Place(host,origin+Vector3.back*10,1);Place(enemy,origin+new Vector3(2,.05f,0),2);
                var config=SpecialWeaponConfigService.Current.Get(1);var blast=typeof(SpecialWeaponService).GetMethod("Blast",BindingFlags.Instance|BindingFlags.NonPublic);
                var entity=new SpecialWeaponService.Entity(new SpecialEntityState{Owner=host.PlayerId,Team=1,Type=config.Type,Round=match.State.Value.Round},config);
                blast.Invoke(match.SpecialWeapons,new object[]{entity,origin+Vector3.up*.9f,null,false});
                Assert.That(enemy.Snapshot.Value.Health,Is.LessThan(100));Assert.That(enemy.Snapshot.Value.SpecialImpulseVelocity.x,Is.GreaterThan(0),"Enemy blast queues outward force");
                using(var writer=new Unity.Netcode.FastBufferWriter(2048,Unity.Collections.Allocator.Temp))
                {
                    writer.WriteNetworkSerializable(enemy.Snapshot.Value);
                    using var reader=new Unity.Netcode.FastBufferReader(writer,Unity.Collections.Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot roundtrip);
                    Assert.That(roundtrip.SpecialImpulseVelocity,Is.EqualTo(enemy.Snapshot.Value.SpecialImpulseVelocity),"Snapshot retains pending force for prediction");
                }
                Vector3 before=enemy.Snapshot.Value.Position;enemy.Simulate(1f/60,now,Splatoon.Networking.MatchPhase.Practice);
                Assert.That(enemy.Snapshot.Value.Position.x,Is.GreaterThan(before.x),"Force causes actual movement without directional input");
                wall.SetActive(true);Physics.SyncTransforms();enemy.ReceiveSpecialKnockback(Vector3.right,10000,.98f,10);
                for(int f=0;f<60;f++)enemy.Simulate(1f/60,now+f/60d,Splatoon.Networking.MatchPhase.Practice);
                Assert.That(enemy.Snapshot.Value.Position.x,Is.LessThan(wall.GetComponent<Collider>().bounds.min.x),"Knockback cannot teleport through a wall");
                enemy.Respawn();Assert.That(enemy.Snapshot.Value.SpecialImpulseVelocity,Is.EqualTo(Vector3.zero),"Respawn removes previous life force");
                wall.SetActive(false);Physics.SyncTransforms();
                var reef=SpecialWeaponConfigService.Current.Get(5);
                Place(enemy,origin+Vector3.right*((reef.P.innerRadius+reef.P.outerRadius)*.5f)+Vector3.up*.05f,2);
                var reefBlast=new SpecialWeaponService.Entity(new SpecialEntityState{Owner=host.PlayerId,Team=1,Type=reef.Type,Round=match.State.Value.Round},reef);
                blast.Invoke(match.SpecialWeapons,new object[]{reefBlast,origin+Vector3.up*.9f,null,false});
                Assert.That(enemy.Snapshot.Value.IsAlive,Is.True);Assert.That(enemy.Snapshot.Value.SpecialImpulseVelocity.x,Is.GreaterThan(0),"Surviving Reefslider splash pushes outward");
                foreach(int id in new[]{2,3})
                {
                    Place(enemy,origin+Vector3.right*(id==3?.5f:2)+Vector3.up*.05f,2);
                    var owner=host.Snapshot.Value;owner.SpecialWeaponId=id;owner.SpecialAction++;host.Snapshot.Value=owner;
                    var effect=match.SpecialWeapons.Spawn(host,owner,now,match.State.Value.Round);
                    effect.State.Position=origin;effect.State.Phase=SpecialEntityPhase.Active;effect.State.Changed=id==2?now-effect.Config.P.expandSeconds:now;
                    if(id==3){var body=new GameObject("Contact sonar force fixture");effect.Target=body.AddComponent<SpecialWeaponTarget>();effect.Target.Initialize(match.SpecialWeapons,effect);Physics.SyncTransforms();}
                    match.SpecialWeapons.Step(now,1f/60,match.Players);
                    Assert.That(enemy.Snapshot.Value.SpecialImpulseVelocity.x,Is.GreaterThan(0),$"Special {id} contact/field applies force");
                    match.SpecialWeapons.Clear();
                }
                wall.SetActive(false);Place(host,origin+Vector3.up*10,1);
                var state=host.Snapshot.Value;state.Grounded=false;state.SpecialPhase=SpecialPhase.Active;state.SpecialRemaining=3;state.SpecialUntil=now+10;state.SpecialReadyAt=now;state.SpecialShotAt=now;state.SpecialStartedAt=now-1;
                var input=new Splatoon.Networking.PlayerInputFrame{HeroRevision=state.HeroRevision,SpecialSequence=state.SpecialConsumed,Look=Vector2.zero};
                var args=new object[]{state,input,1f/60,now,Splatoon.Networking.MatchPhase.Practice};step.Invoke(host,args);state=(PlayerSnapshot)args[0];
                Assert.That(state.SpecialImpulseVelocity.z,Is.LessThan(0),"Committed airborne Trizooka shot queues backward recoil");
                Vector3 shotPosition=state.Position;args=new object[]{state,input,1f/60,now+1d/60,Splatoon.Networking.MatchPhase.Practice};step.Invoke(host,args);state=(PlayerSnapshot)args[0];
                Assert.That(state.Position.z,Is.LessThan(shotPosition.z),"Recoil moves the real controller");
                SpecialWeaponSimulation.Interrupt(ref state);Assert.That(state.SpecialImpulseVelocity,Is.EqualTo(Vector3.zero),"Cancellation/downing clears force");
            }
            finally{wall.SetActive(false);UnityEngine.Object.Destroy(wall);UnityEngine.Object.Destroy(floor);match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }

        [UnityTearDown]public IEnumerator Cleanup()
        {if(!Application.isPlaying)yield break;if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)yield return PrototypeApp.Current.Leave().ToCoroutine();yield return new ExitPlayMode();}
    }
}
#endif
