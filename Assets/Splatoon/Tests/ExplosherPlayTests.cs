#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class ExplosherPlayTests
    {
        const string Output = "Reports/Explosher";
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 40;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, message);
        }
        [UnityTest] public IEnumerator HostChecksPenetrationDamageOcclusionReloadAndPresentation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static void Place(PrototypePlayer player, Vector3 position, byte team = 2, bool paper = false)
        {
            var s = player.Snapshot.Value;
            s.Health = s.Ink = 100; s.ProtectedUntil = 0; s.Position = position;
            s.Team = team; s.Swimming = paper; s.CompactBody = false; s.Grounded = true;
            s.Movement = paper ? MovementMode.GroundInk : MovementMode.Human;
            s.PaperPose = PaperPose.None; s.PlanarVelocity = Vector3.zero; s.VerticalSpeed = 0;
            player.GetComponent<CharacterController>().enabled = false;
            player.transform.SetPositionAndRotation(position, Quaternion.identity);
            player.GetComponent<CharacterController>().enabled = true;
            player.Snapshot.Value = s; player.SwimBody.ApplyCollision(s); Physics.SyncTransforms();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Host ready");
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            host.RequestHeroChange(3, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 3 &&
                host.CharacterView?.Profile.name == "ShotgunGirlPresentation", "Explosher selected");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var enemy = match.AddTestBot(prefab); var second = match.AddTestBot(prefab);
            Assert.That(enemy, Is.Not.Null, match.TestBotMessage); Assert.That(second, Is.Not.Null, match.TestBotMessage);
            yield return Wait(() => enemy.SwimBody != null && second.SwimBody != null, "Bot bodies ready");
            app.CaptureMouse(false); match.enabled = false; foreach (var p in match.Players) p.enabled = false;
            var w = GameplayConfig.GetWeapon(3); WeaponConfigValidation.Validate(w);
            var originalPosition = host.transform.position;
            var origin = new Vector3(1000,1001,1000); uint id = 30000;
            Place(host,origin-Vector3.up,1);
            var wall = new GameObject("Explosher thin wall"); wall.transform.position = origin + Vector3.forward*9;
            wall.AddComponent<BoxCollider>().size = new Vector3(12,12,.1f); Physics.SyncTransforms();
            InkProjectileService Fire(double duration = .25)
            {
                var service = new InkProjectileService(); double born = host.NetworkManager.ServerTime.Time;
                service.SpawnForMeasurement(new InkShot { Id=++id, ActionId=id, Round=match.State.Value.Round, HeroId=3,
                    Team=1,Shooter=host.PlayerId,Seed=id,Born=born,Origin=origin,
                    Velocity=ExplosherSimulation.Launch(Vector3.forward,w,true),Configuration=w,
                    ConfigurationRevision=WeaponConfigService.Current.Revision(3) });
                service.Simulate(born+duration);return service;
            }
            try
            {
                Place(enemy,origin+Vector3.forward*3-Vector3.up*.5f);
                Place(second,origin+Vector3.forward*5-Vector3.up*.3f);
                var through = Fire(.09);
                Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(45)); Assert.That(second.Snapshot.Value.Health,Is.EqualTo(45));
                Assert.That(through.ActiveCount,Is.EqualTo(1)); Assert.That(through.Explosions,Is.Empty);
                Assert.That(through.Impacts.All(x=>x.ContinuesProjectile),Is.True);
                through.Simulate(host.NetworkManager.ServerTime.Time+.25);
                Assert.That(through.Explosions.Count,Is.EqualTo(1));Assert.That(through.ActiveCount,Is.Zero);
                Assert.That(through.Impacts.Count(x=>x.Victim==enemy.PlayerId&&x.Damage>0),Is.EqualTo(1));
                Assert.That(through.Impacts.Count(x=>x.Victim==second.PlayerId&&x.Damage>0),Is.EqualTo(1));
                through.Simulate(host.NetworkManager.ServerTime.Time+5); Assert.That(through.Explosions.Count,Is.EqualTo(1));
                Fire();Assert.That(enemy.Snapshot.Value.Health,Is.Zero,"Two direct hits defeat");

                Place(second,origin+Vector3.right*20);
                Place(enemy,origin+Vector3.forward*8-Vector3.up*.1f);
                var combined = Fire(); Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(10),"55 + 35 must leave 10 HP");
                CollectionAssert.AreEqual(new[]{55f,35f},combined.Impacts.Where(x=>x.Victim==enemy.PlayerId&&x.Damage>0).Select(x=>x.Damage).ToArray());
                var center=combined.Explosions.Single().Position;
                Place(enemy,center+Vector3.right*1.5f-Vector3.forward*.5f-Vector3.up*.7f);
                Fire();Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(65));
                Fire();Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(30));
                Fire();Assert.That(enemy.Snapshot.Value.Health,Is.Zero,"Three pure blasts defeat");

                Place(second,origin+Vector3.forward*3-Vector3.up*.5f,1);
                Place(enemy,origin+Vector3.forward*5-Vector3.up*.3f);
                Fire();Assert.That(second.Snapshot.Value.Health,Is.EqualTo(100));Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(45));
                Place(enemy,origin+Vector3.forward*9.5f-Vector3.up*.1f);
                Fire();Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100),"Thin wall blocks direct and splash");
                Place(enemy,center+Vector3.right-Vector3.forward*1.3f-Vector3.up*.1f,2,true);
                Assert.That(enemy.SwimBody.FlatHitActive,Is.True);
                Assert.That(InkExplosionRules.ClosestPoint(enemy,center-Vector3.forward*w.ExplosherBlastOffset,out var paperPoint),Is.True);
                Assert.That(Vector3.Distance(paperPoint,center-Vector3.forward*w.ExplosherBlastOffset),Is.LessThan(w.Ammo.ExplosionRadius));
                Fire();Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(65),"Paper mesh splash");

                var source = WeaponConfigService.Current.Source(3);var clone=UnityEngine.Object.Instantiate(source);
                try
                {
                    WeaponConfigService.Current.SetForEditor(3,w,clone);
                    var before=host.Snapshot.Value;before.AttackRecoveryUntil=host.NetworkManager.ServerTime.Time+35/60.0;
                    before.AttackMoveUntil=host.NetworkManager.ServerTime.Time+55/60.0;host.Snapshot.Value=before;
                    var pending=Fire(.03);uint revision=WeaponConfigService.Current.Revision(3);
                    clone.damage=clone.damageMin=60;clone.explosherAirSpeed+=1;
                    yield return Wait(()=>{app.ApplyDebugWeaponChanges();return WeaponConfigService.Current.Revision(3)>revision;},"Hot reload");
                    Assert.That(GameplayConfig.GetWeapon(3).Damage,Is.EqualTo(60));
                    Assert.That(host.Snapshot.Value.AttackRecoveryUntil,Is.EqualTo(before.AttackRecoveryUntil));
                    Assert.That(host.Snapshot.Value.AttackMoveUntil,Is.EqualTo(before.AttackMoveUntil));
                    Assert.That(pending.LiveShots().Single().Configuration,Is.SameAs(w));
                    pending.Simulate(host.NetworkManager.ServerTime.Time+1);Assert.That(pending.Explosions.Count,Is.EqualTo(1));
                }
                finally{WeaponConfigService.Current.SetForEditor(3,w,source);UnityEngine.Object.DestroyImmediate(clone);}

                Place(host,originalPosition,1);
                var state=host.Snapshot.Value;state.AttackRecoveryUntil=state.AttackMoveUntil=0;state.AttackNeedsRelease=false;
                state.SemiHoldStarted=false;state.WeaponPhase=WeaponPhase.Idle;state.NextShotAt=state.WeaponReadyAt=0;
                double now=host.NetworkManager.ServerTime.Time;int emitted=0;
                for(int t=0;t<73;t++)
                {
                    bool fired=WeaponSimulation.Step(ref state,new PlayerInputFrame{Fire=true,FireSequence=99,Sequence=(uint)t+100},w,now+t/60.0,false,true);
                    host.CharacterView.Present(state,1f/60,now+t/60.0);
                    if(fired){emitted++;host.CharacterView.Shot(0,state.ShotActionId,host.NetworkManager.ServerTime.Time);}
                    host.CharacterView.Animator.Update(1f/60);
                    if(t==18)Capture(originalPosition+Vector3.up,"shot-animation");yield return null;
                }
                Assert.That(emitted,Is.EqualTo(2));
                var visualPoint=origin+new Vector3(20,3,0);
                var evt=combined.Explosions[0];evt.ShotId=++id;evt.Position=visualPoint;evt.Normal=Vector3.up;
                evt.Time=host.NetworkManager.ServerTime.Time+.001;
                InkPresentation.Current.Clear();InkPresentation.Current.Explosion(evt);InkPresentation.Current.Explosion(evt);
                foreach(var p in InkPresentation.Current.GetComponentsInChildren<ParticleSystem>())p.Simulate(.08f,false,true,false);
                Capture(evt.Position,"impact-splash",new Vector3(4,5,-6));
                InkPresentation.Current.Clear();
                var shown=new InkShot{Id=++id,Round=match.State.Value.Round,HeroId=3,Team=1,Shooter=host.PlayerId,
                    Configuration=w,Born=host.NetworkManager.ServerTime.Time+.01,Origin=visualPoint+Vector3.up*2,
                    Velocity=ExplosherSimulation.Launch(Vector3.back,w,true),Seed=id,ActionId=id};
                InkPresentation.Current.Spawn(shown);InkPresentation.Current.UpdateFlights(shown.Born+.075,Camera.main);
                Capture(InkBallistics.Position(shown,w,.075),"flight-blob",new Vector3(3,2,-4));
                File.WriteAllText(Output+"/host-summary.txt","PASS: production Host and Physics; 55/35/90; two direct / three splash defeats; two victims pierced; one hit per lifetime; nonterminal feedback; one collision explosion; allies transparent; thin wall shields; paper mesh; immutable in-flight config and independent deadlines across editor hot reload; existing animation driven on actual emissions.\n");
            }
            finally{UnityEngine.Object.DestroyImmediate(wall);}
            yield return app.Leave().ToCoroutine();
        }
        static void Capture(Vector3 target,string name,Vector3? offset=null)
        {
            var camera=new GameObject("Explosher validation camera").AddComponent<Camera>();camera.CopyFrom(Camera.main);camera.enabled=false;
            camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing=false;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.16f,.19f,.22f);
            camera.transform.position=target+(offset??new Vector3(3,2,4));camera.transform.LookAt(target);camera.aspect=1;camera.fieldOfView=45;
            var rt=new RenderTexture(960,960,24);var old=RenderTexture.active;var image=new Texture2D(960,960,TextureFormat.RGB24,false);
            try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,960,960),0,0);image.Apply();File.WriteAllBytes(Output+"/"+name+".png",image.EncodeToPNG());}
            finally{RenderTexture.active=old;UnityEngine.Object.Destroy(camera.gameObject);UnityEngine.Object.Destroy(rt);UnityEngine.Object.Destroy(image);}
        }
    }
}
#endif
