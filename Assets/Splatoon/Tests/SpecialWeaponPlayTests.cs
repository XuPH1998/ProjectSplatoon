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
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class SpecialWeaponPlayTests
    {
        static IEnumerator Wait(Func<bool> ready,string label)
        {double end=Time.realtimeSinceStartupAsDouble+45;while(!ready()&&Time.realtimeSinceStartupAsDouble<end)yield return null;Assert.That(ready(),Is.True,label);}
        [UnityTest]
        public IEnumerator HostSpecialsCoveragePersistenceAndPresentation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");yield return new EnterPlayMode();
            yield return Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready,"Boot");
            var app=PrototypeApp.Current;yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null&&PrototypeMatch.Current!=null,"Host");
            var match=PrototypeMatch.Current;var host=PrototypePlayer.Local;
            var enemy=match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            app.CaptureMouse(false);match.enabled=false;foreach(var player in match.Players)player.enabled=false;
            var arena=PrototypeArena.Current;Vector3 point=new(0,0,-20);
            Assert.That(Physics.Raycast(point+Vector3.up*3,Vector3.down,out var floor,6,PlayerMotorSimulation.WorldMask),Is.True);
            point=floor.point;var surface=floor.collider.GetComponentInParent<PaintSurface>();Assert.That(surface.Scores,Is.True);
            double now=match.NetworkManager.ServerTime.Time;
            void Place(PrototypePlayer player,Vector3 at,byte team)
            {
                var s=player.Snapshot.Value;s.Position=at;s.Health=s.Ink=100;s.Team=team;s.LifeState=PlayerLifeState.Alive;s.ProtectedUntil=0;s.SpecialPhase=SpecialPhase.Charging;s.SpecialChargeLockedUntil=0;s.SpecialPoints=0;s.Swimming=s.CompactBody=false;s.PaperPose=PaperPose.None;s.Grounded=true;s.AirHumanOffset=0;
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.position=at;cc.enabled=true;player.Snapshot.Value=s;player.SwimBody?.ApplyCollision(s);Physics.SyncTransforms();
            }
            SpecialWeaponService.Entity Spawn(int id,Vector3 at)
            {
                var s=host.Snapshot.Value;s.SpecialWeaponId=id;s.SpecialAction++;s.SpecialAttack++;host.Snapshot.Value=s;
                var e=match.SpecialWeapons.Spawn(host,s,now,match.State.Value.Round);e.State.Position=e.State.P0=e.State.P1=e.State.P2=at;e.State.Velocity=Vector3.down*2;return e;
            }
            void Step(int frames){for(int i=0;i<frames;i++){now+=1d/60;match.SpecialWeapons.Step(now,1f/60,match.Players);Physics.SyncTransforms();}}
            GameObject roof=null;
            try
            {
                Place(host,point+Vector3.back*8,1);Place(enemy,point+Vector3.right*20,2);arena.ClearPaint();
                var credit=new PaintCredit(host.PlayerId,1,match.State.Value.Round,host.Snapshot.Value.HeroRevision,PaintAttackKind.Main);
                match.Paint(surface,point,Vector3.up,2,1,1,1,shapeSeed:123,credit:credit);
                double first=host.Snapshot.Value.SpecialPoints,area=arena.LastPaintGainedArea;
                Assert.That(first,Is.GreaterThan(0),"Player ID zero receives authoritative coverage");
                Assert.That(first,Is.EqualTo(PaintCredit.Points(area)).Within(.00001));
                match.Paint(surface,point,Vector3.up,2,1,1,1,shapeSeed:123,credit:credit);
                Assert.That(host.Snapshot.Value.SpecialPoints,Is.EqualTo(PaintCredit.Points(arena.PinkArea)).Within(.00001),"Only new fringe cells count on overlapping brushes");
                for(int i=0;i<12;i++)match.Paint(surface,point,Vector3.up,2,1,1,1,shapeSeed:123,credit:credit);
                double saturated=host.Snapshot.Value.SpecialPoints;
                match.Paint(surface,point,Vector3.up,2,1,1,1,shapeSeed:123,credit:credit);
                Assert.That(host.Snapshot.Value.SpecialPoints,Is.EqualTo(saturated),"Saturated friendly overlap gives zero credit");
                match.Paint(surface,point,Vector3.up,2,2,1,1,shapeSeed:123);
                double beforeReclaim=arena.PinkArea;
                match.Paint(surface,point,Vector3.up,2,1,1,1,shapeSeed:123,credit:credit);
                Assert.That(host.Snapshot.Value.SpecialPoints-saturated,Is.EqualTo(PaintCredit.Points(arena.PinkArea-beforeReclaim)).Within(.00001),"Enemy reclaim credits actual new cells");
                // A normal respawn retains a deployment lock and both selections.
                var saved=host.Snapshot.Value;saved.SpecialChargeLockedUntil=now+8;saved.SubWeaponId=13;saved.SpecialWeaponId=4;host.Snapshot.Value=saved;host.Respawn();
                Assert.That(host.Snapshot.Value.SpecialChargeLockedUntil,Is.EqualTo(saved.SpecialChargeLockedUntil));Assert.That(host.Snapshot.Value.SubWeaponId,Is.EqualTo(13));
                Place(host,point+Vector3.back*8,1);Place(enemy,point+Vector3.right*2,2);
                var sonar=Spawn(3,point+Vector3.up*.2f);for(int f=0;f<30&&sonar.State.Phase==SpecialEntityPhase.Flying;f++)Step(1);Assert.That(sonar.Target,Is.Not.Null);
                // First wave hits only once and applies the marker.
                Step(140);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(55).Within(.01));Assert.That(enemy.Snapshot.Value.MarkedUntilPink,Is.GreaterThan(0));
                Assert.That(sonar.State.Pulse,Is.EqualTo(1));match.SpecialWeapons.DamageObject(sonar.State.Id,2,480);
                Assert.That(sonar.Removed,Is.False,"Emitted waves outlive the destroyed generator");Assert.That(sonar.Target,Is.Null);
                Step(220);Assert.That(sonar.Removed,Is.True);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(55).Within(.01),"Destroyed generator emits no later pulses");
                match.SpecialWeapons.Clear();Place(enemy,point+Vector3.right*2+Vector3.up*1.5f,2);
                sonar=Spawn(3,point+Vector3.up*.2f);Step(150);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100),"Jump clears the floor wave");
                match.SpecialWeapons.Clear();Place(enemy,point+Vector3.right*30,2);
                sonar=Spawn(3,point+Vector3.up*.2f);
                for(int f=0;f<30&&sonar.State.Phase==SpecialEntityPhase.Flying;f++)Step(1);
                Assert.That(sonar.State.Phase,Is.EqualTo(SpecialEntityPhase.Active));
                for(int frame=1;frame<=550;frame++)
                {
                    if(frame==100){var dead=host.Snapshot.Value;dead.Health=0;dead.LifeState=PlayerLifeState.Dead;host.Snapshot.Value=dead;}
                    Step(1);
                    int expected=frame<90?0:frame<240?1:frame<390?2:3;
                    Assert.That(sonar.State.Pulse,Is.EqualTo(expected),$"Sonar pulse at landed frame {frame}");
                    Assert.That(sonar.Removed,Is.EqualTo(frame==550),$"Owner death must not remove deployment or emitted waves at {frame}");
                    if(frame==390)Assert.That(sonar.Target,Is.Null,"Generator retires after third emission while waves continue");
                }
                Place(host,point+Vector3.back*8,1);
                match.SpecialWeapons.Clear();Place(enemy,point+Vector3.right*.5f,2);
                var tornado=Spawn(2,point+Vector3.up*.2f);for(int f=0;f<30&&tornado.State.Phase==SpecialEntityPhase.Flying;f++)Step(1);Assert.That(tornado.State.Phase,Is.EqualTo(SpecialEntityPhase.Warning));
                Step(80);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100),"Warning is harmless");Step(15);Assert.That(enemy.Snapshot.Value.Health,Is.LessThan(100));
                match.SpecialWeapons.Clear();Place(enemy,point+Vector3.right*20,2);
                var rain=Spawn(4,point+Vector3.up*.2f);Step(40);Assert.That(rain.State.Phase,Is.EqualTo(SpecialEntityPhase.Active));
                Assert.That(host.Snapshot.Value.SpecialChargeLockedUntil,Is.GreaterThan(now+7));
                Vector3 cloud=rain.State.Position;Step(60);Assert.That(Vector3.Distance(cloud,rain.State.Position),Is.GreaterThan(0));
                match.SpecialWeapons.Clear();Place(enemy,point+Vector3.right*.5f,2);
                rain=Spawn(4,point+Vector3.up*.2f);Step(40);
                var rain2=Spawn(4,point+Vector3.up*.2f);rain2.State.Phase=SpecialEntityPhase.Active;rain2.State.Position=rain.State.Position;rain2.State.Changed=now;rain2.State.Expires=now+8;
                Place(enemy,point+Vector3.right*.5f,2);
                roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.transform.position=point+Vector3.up*3;roof.transform.localScale=new Vector3(20,1,20);Physics.SyncTransforms();
                Step(60);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(100),"A solid roof blocks rain on the lower floor");
                UnityEngine.Object.Destroy(roof);roof=null;yield return null;Physics.SyncTransforms();
                Step(60);Assert.That(enemy.Snapshot.Value.Health,Is.EqualTo(76).Within(.01),"Overlapping hostile clouds do not double DPS");
                // All five authority entities can be reconstructed solely from a snapshot.
                match.SpecialWeapons.Clear();Place(host,point+Vector3.back*8,1);Place(enemy,point+Vector3.right*20,2);
                for(int id=1;id<=5;id++)Spawn(id,point+new Vector3((id-3)*2,2,0));
                match.SpecialPresentation.Apply(match.SpecialWeapons.Capture(),match.SpecialWeapons.Watermark);
                Assert.That(match.SpecialPresentation.Count,Is.EqualTo(5));
                Camera.main.transform.position=point+new Vector3(8,8,-14);Camera.main.transform.LookAt(point+Vector3.up*2);app.CaptureMouse(true);
                yield return null;Directory.CreateDirectory("Reports/SpecialWeapons");ScreenCapture.CaptureScreenshot("Reports/SpecialWeapons/five-specials.png");yield return null;
                match.SpecialWeapons.Clear();match.SpecialPresentation.Clear();var hs=host.Snapshot.Value;hs.SpecialPhase=SpecialPhase.Charging;host.Snapshot.Value=hs;
                app.OpenHeroSelection(HeroSelectionOrigin.Warmup);yield return null;ScreenCapture.CaptureScreenshot("Reports/SpecialWeapons/loadout.png");yield return null;
                File.WriteAllText("Reports/SpecialWeapons/host-scenarios.txt","PASS: real floor seam/repaint credit, owner zero, respawn loadout/lock, sonar damage/mark/jump/destruction, tornado warning/damage, moving rain lock, five snapshot presentations.");
            }
            finally{if(roof!=null)UnityEngine.Object.Destroy(roof);match.SpecialWeapons.Clear();match.enabled=true;foreach(var player in match.Players)player.enabled=true;}
            yield return app.Leave().ToCoroutine();yield return new ExitPlayMode();
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {if(!Application.isPlaying)yield break;if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)yield return PrototypeApp.Current.Leave().ToCoroutine();yield return new ExitPlayMode();}
    }
}
#endif
