#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Object=UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class SubWeaponPlayTests
    {
        static IEnumerator Wait(Func<bool> ready,string label)
        { double end=Time.realtimeSinceStartupAsDouble+45;while(!ready()&&Time.realtimeSinceStartupAsDouble<end)yield return null;Assert.That(ready(),Is.True,label); }
        [UnityTest]
        public IEnumerator ThirteenTypesHostDamagePaintLifecycleAndObjects()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready,"Boot ready");
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();Assert.That(app.InRoom,Is.True,app.Error);
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null,"Host ready");
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var enemy=match.AddTestBot(prefab);Assert.That(enemy,Is.Not.Null);
            app.CaptureMouse(false);match.enabled=false;foreach(var p in match.Players)p.enabled=false;
            var original=SubWeaponConfigService.Current.Source(host.Snapshot.Value.HeroId);
            var floor=Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None).Where(s=>s.Scores&&s.GetComponent<Collider>()!=null&&s.GetComponent<Collider>().bounds.size.y<2)
                .OrderByDescending(s=>s.GetComponent<Collider>().bounds.size.x*s.GetComponent<Collider>().bounds.size.z).First();
            var fc=floor.GetComponent<Collider>();Assert.That(fc.Raycast(new Ray(fc.bounds.center+Vector3.up*20,Vector3.down),out var ground,40),Is.True);
            Vector3 basePoint=ground.point, hostPoint=default; bool foundBase=false, foundHostPoint=false;
            for(int x=-12;x<=12&&!foundBase;x+=2) for(int z=-12;z<=12&&!foundBase;z+=2)
            {
                var probe=ground.point+new Vector3(x,0,z);
                if(!Physics.Raycast(probe+Vector3.up*6,Vector3.down,out var h,10,PlayerMotorSimulation.WorldMask)||h.collider!=fc)continue;
                if(!floor.QueryRegion(h.point,h.normal,out _)||Physics.CheckSphere(h.point+Vector3.up*1.3f,1.1f,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))continue;
                basePoint=h.point;foundBase=true;
            }
            Assert.That(foundBase,Is.True,"Find open combat fixture above the receiving floor");
            for(int x=-15;x<=15&&!foundHostPoint;x++) for(int z=-15;z<=15&&!foundHostPoint;z++)
            {
                var probe=basePoint+new Vector3(x,0,z);
                if(Vector3.Distance(probe,basePoint)<7||!Physics.Raycast(probe+Vector3.up*5,Vector3.down,out var h,8,PlayerMotorSimulation.WorldMask))continue;
                var candidate=host.Snapshot.Value;candidate.Position=h.point+Vector3.up*.04f;candidate.AirHumanOffset=0;candidate.Team=1;
                if(SubWeaponService.TryMineGround(candidate,out _)){hostPoint=candidate.Position;foundHostPoint=true;}
            }
            Assert.That(foundHostPoint,Is.True,"Find unobstructed paintable mine fixture");
            double now=match.NetworkManager.ServerTime.Time;var report=new List<string>();
            void Place(PrototypePlayer p,Vector3 position,byte team)
            {
                var s=p.Snapshot.Value;s.Position=position;s.Health=s.Ink=100;s.ProtectedUntil=0;s.Team=team;s.Swimming=false;s.Grounded=true;s.AirHumanOffset=0;s.CompactBody=false;s.PaperPose=PaperPose.None;s.Yaw=0;s.Pitch=45;
                var cc=p.GetComponent<CharacterController>();cc.enabled=false;p.transform.position=position;cc.enabled=true;p.Snapshot.Value=s;p.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            }
            SubWeaponService.Entity Spawn(SubWeaponType type,Vector3 point,float charge=0)
            {
                var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));SubWeaponConfigService.Current.Set(host.Snapshot.Value.HeroId,asset);
                var s=host.Snapshot.Value;s.SubAction++;host.Snapshot.Value=s;
                var e=match.SubWeapons.Spawn(host,s,charge,now,match.State.Value.Round);Assert.That(e,Is.Not.Null,type.ToString());
                if(type!=SubWeaponType.InkMine){e.State.Position=point;e.State.Velocity=Vector3.down*6;e.HitTarget?.UpdateState(e.State,e.Config,now);}return e;
            }
            void Step(float seconds)
            { int frames=Mathf.CeilToInt(seconds*60);for(int i=0;i<frames;i++){now+=1d/60;match.SubWeapons.Step(now,1f/60,match.Players);Physics.SyncTransforms();} }
            Place(host,hostPoint,1);Place(enemy,basePoint+Vector3.right*30,2);
            try
            {
                foreach(SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
                {
                    match.SubWeapons.Clear();Place(host,hostPoint,1);Place(enemy,basePoint+Vector3.right*30,2);
                    if(type==SubWeaponType.InkMine) match.Paint(floor,host.Snapshot.Value.Position,Vector3.up,2,1,1,1);
                    var e=Spawn(type,basePoint+Vector3.up*.6f,type==SubWeaponType.FizzyBomb?1:0);
                    if(type==SubWeaponType.AngleShooter){e.State.Position=basePoint+Vector3.up*1;e.State.Velocity=Vector3.forward*e.Config.Angle.speed;}
                    Step(.5f);
                    if(type==SubWeaponType.Sprinkler||type==SubWeaponType.SplashWall||type==SubWeaponType.PointSensor||type==SubWeaponType.ToxicMist||type==SubWeaponType.InkMine)
                        Assert.That(e.State.Phase,Is.EqualTo(SubEntityPhase.Active),type.ToString());
                    Step(4.5f);
                    if(type==SubWeaponType.FizzyBomb)Assert.That(match.SubWeapons.Effects.Count,Is.EqualTo(3),"Fully charged fizzy has three explosions");
                    if(type==SubWeaponType.SplatBomb||type==SubWeaponType.SuctionBomb||type==SubWeaponType.BurstBomb||type==SubWeaponType.CurlingBomb||type==SubWeaponType.Autobomb||type==SubWeaponType.Torpedo)
                        Assert.That(match.SubWeapons.Effects.Count,Is.GreaterThanOrEqualTo(1),type+" explodes");
                    if(type==SubWeaponType.SplashWall) {Step(3);Assert.That(e.Removed,Is.True,"Wall naturally depletes");}
                    report.Add(type+": ground activation/lifecycle PASS");
                }
                match.SubWeapons.Clear();Place(enemy,basePoint+Vector3.up*.04f,2);
                var burst=Spawn(SubWeaponType.BurstBomb,enemy.Snapshot.Value.Position+Vector3.up*(GameplayConfig.GetHero(enemy.Snapshot.Value.HeroId).StandingHeight*.5f)+Vector3.back*1);
                burst.State.Velocity=Vector3.forward*30;Step(.15f);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(40).Within(.01f),"Burst direct total 60, not 60+35");report.Add("Burst direct hit total: PASS");

                match.SubWeapons.Clear();Place(enemy,basePoint+Vector3.up*.04f,2);
                var sensor=Spawn(SubWeaponType.PointSensor,basePoint+Vector3.up*.4f);Step(.2f);
                Assert.That(enemy.Snapshot.Value.MarkedUntilPink,Is.GreaterThan(now+7),$"phase={sensor.State.Phase} removed={sensor.Removed} team={sensor.State.Team} pos={sensor.State.Position} enemy={enemy.Snapshot.Value.Position} health={enemy.Snapshot.Value.Health}");Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100));report.Add("Sensor marks without damage: PASS");

                match.SubWeapons.Clear();Place(enemy,basePoint+Vector3.up*.04f,2);
                var mist=Spawn(SubWeaponType.ToxicMist,basePoint+Vector3.up*.4f);Step(1.5f);
                Assert.That(enemy.Snapshot.Value.Ink,Is.LessThan(90));Assert.That(enemy.Snapshot.Value.MistMoveRate,Is.EqualTo(.6f).Within(.01));Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100));
                Place(enemy,basePoint+Vector3.right*30,2);Step(.1f);Assert.That(enemy.Snapshot.Value.MistExposure,Is.Zero);report.Add("Mist drain/slow/exit: PASS");

                match.SubWeapons.Clear();Place(host,hostPoint,1);match.Paint(floor,host.Snapshot.Value.Position,Vector3.up,4,1,1,1);
                var mine1=Spawn(SubWeaponType.InkMine,basePoint);Spawn(SubWeaponType.InkMine,basePoint);Spawn(SubWeaponType.InkMine,basePoint);
                Assert.That(mine1.Removed,Is.True);Assert.That(match.SubWeapons.Count(host.PlayerId,SubWeaponType.InkMine),Is.EqualTo(2));report.Add("Third mine detonates oldest: PASS");

                match.SubWeapons.Clear();var torpedo=Spawn(SubWeaponType.Torpedo,basePoint+Vector3.up*5);
                Assert.That(match.SubWeapons.CanUse(host.PlayerId,host.Snapshot.Value,torpedo.Config),Is.EqualTo(SubWeaponFailure.ActiveLimit));
                match.SubWeapons.DamageObject(torpedo.State.Id,2,20);Assert.That(torpedo.Removed,Is.True);Assert.That(match.SubWeapons.Effects.Any(e=>e.Kind==SubEffectKind.Explosion),Is.False);report.Add("Torpedo active limit and shot-down without explosion: PASS");

                match.SubWeapons.Clear();var sprinkler=Spawn(SubWeaponType.Sprinkler,basePoint+Vector3.up*.4f);Step(.25f);
                var dead=host.Snapshot.Value;dead.Health=0;host.Snapshot.Value=dead;Step(.1f);Assert.That(sprinkler.Removed,Is.True);Place(host,hostPoint,1);report.Add("Owner death removes sprinkler: PASS");

                for(int hero=1;hero<=9;hero++)
                {
                    match.SubWeapons.Clear();var target=Spawn(SubWeaponType.Torpedo,basePoint+new Vector3(20,20,20));
                    target.State.Team=2;target.State.Health=1000;Object.DestroyImmediate(target.HitTarget.gameObject);
                    var go=new GameObject("TestSubTarget");target.HitTarget=go.AddComponent<SubWeaponTarget>();target.HitTarget.Initialize(match.SubWeapons,target.State,target.Config);Physics.SyncTransforms();
                    var weapon=GameplayConfig.GetWeapon(hero);var shot=new InkShot { Id=(uint)(8000+hero),Round=match.State.Value.Round,HeroId=hero,Shooter=host.PlayerId,Team=1,Born=now,Origin=target.State.Position+Vector3.back*2,Velocity=Vector3.forward*30,Configuration=weapon,ConfigurationRevision=WeaponConfigService.Current.Revision(hero),Seed=123,Charge=1 };
                    var service=new InkProjectileService();service.SpawnForMeasurement(shot);service.Simulate(now+.3);
                    Assert.That(target.State.Health,Is.LessThan(1000),"Main weapon "+hero+" damages deployable");service.Clear();
                }
                report.Add("All nine main weapons hit sub objects: PASS");
                var seed=host.Snapshot.Value;seed.SubConsumedPress=seed.SubConsumedRelease=17;host.Snapshot.Value=seed;host.Respawn();
                var respawn=host.Snapshot.Value;var neutral=(Splatoon.Networking.PlayerInputFrame)typeof(PrototypePlayer).GetField("_lastInput",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(host);
                var sub=GameplayConfig.GetSubWeapon(respawn.HeroId);
                SubWeaponSimulation.Step(ref respawn,neutral,sub,.033f,now,true,SubWeaponFailure.None,out _);
                var retained=neutral;retained.SubPressSequence=retained.SubReleaseSequence=17;retained.HeroRevision=respawn.HeroRevision;
                SubWeaponSimulation.Step(ref respawn,retained,sub,.033f,now+.033,true,SubWeaponFailure.None,out _);
                Assert.That(respawn.SubPhase,Is.EqualTo(SubWeaponPhase.Idle),"Respawn input baseline never replays an old E press");Assert.That(respawn.Ink,Is.EqualTo(100));
                report.Add("Respawn retained input edges do not auto-throw: PASS");Place(host,hostPoint,1);
                Assert.That(match.PaintSequence,Is.GreaterThan(0));
                match.SubWeapons.Clear();var wall=Spawn(SubWeaponType.SplashWall,basePoint+Vector3.up*.4f);Step(.8f);
                Assert.That(wall.HitTarget.HitCollider.enabled,Is.True);Assert.That(Physics.GetIgnoreCollision(wall.HitTarget.HitCollider,host.GetComponent<CharacterController>()),Is.True);
                Assert.That(Physics.GetIgnoreCollision(wall.HitTarget.HitCollider,enemy.GetComponent<CharacterController>()),Is.False);report.Add("Wall friendly passage/enemy solid: PASS");
                match.SubPresentation.Apply(match.SubWeapons.Capture());app.CaptureMouse(true);
                Camera.main.transform.position=basePoint+new Vector3(5,4,-8);Camera.main.transform.LookAt(basePoint+Vector3.up*1.3f);yield return null;
                Directory.CreateDirectory("Reports/SubWeapons");ScreenCapture.CaptureScreenshot("Reports/SubWeapons/host.png");yield return null;
                File.WriteAllLines("Reports/SubWeapons/host-scenarios.txt",report);
            }
            finally {match.SubWeapons.Clear();SubWeaponConfigService.Current.Set(host.Snapshot.Value.HeroId,original);match.enabled=true;foreach(var p in match.Players)p.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        { if(!Application.isPlaying)yield break;if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)yield return PrototypeApp.Current.Leave().ToCoroutine();yield return new ExitPlayMode(); }
    }
}
#endif
