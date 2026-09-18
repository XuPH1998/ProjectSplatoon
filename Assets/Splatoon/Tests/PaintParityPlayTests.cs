#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using Cysharp.Threading.Tasks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Prototype;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class PaintParityPlayTests
    {
        [UnityTest] public IEnumerator PaintReloadFreezesInFlightShotsAndRejectsOldContent()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return PaintReloadScenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator PaintReloadScenario()
        {
            double deadline=Time.realtimeSinceStartupAsDouble+60;
            while((PrototypeApp.Current==null||!PrototypeApp.Current.Ready)&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
            Assert.That(PrototypeApp.Current!=null&&PrototypeApp.Current.Ready,Is.True);
            var app=PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom,Is.True,app.Error);
            var signature=(byte[])typeof(PrototypeApp).GetField("_signature",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app);
            var oldHex=File.ReadAllLines(PaintParityTests.Root+"/Baseline/room-signature.txt")[0];
            var oldSignature=Enumerable.Range(0,32).Select(i=>Convert.ToByte(oldHex.Substring(i*2,2),16)).ToArray();
            Assert.That(signature.SequenceEqual(oldSignature),Is.False);
            var response=new Unity.Netcode.NetworkManager.ConnectionApprovalResponse();
            typeof(PrototypeApp).GetMethod("Approve",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(app,new object[]{
                new Unity.Netcode.NetworkManager.ConnectionApprovalRequest{ClientNetworkId=99,Payload=PlayerConnectionPayload.Encode(oldSignature,"OldContent")},response});
            Assert.That(response.Approved,Is.False);Assert.That(response.Reason,Does.Contain("游戏内容不一致"));
            var source=WeaponConfigService.Current.Source(7);var w=GameplayConfig.GetWeapon(7);
            var clone=UnityEngine.Object.Instantiate(source);
            try
            {
                WeaponConfigService.Current.SetForEditor(7,w,clone);
                var service=new InkProjectileService();
                double born=app.Manager.ServerTime.Time;
                var shot=new InkShot{Id=90001,ActionId=90001,Round=PrototypeMatch.Current.State.Value.Round,HeroId=7,Team=1,Seed=123,
                    Born=born,Origin=new Vector3(1000,1000,1000),Velocity=Vector3.forward*w.SpeedMin,Configuration=w,
                    ConfigurationRevision=WeaponConfigService.Current.Revision(7)};
                service.SpawnForMeasurement(shot);service.Simulate(born+.03);
                uint revision=WeaponConfigService.Current.Revision(7);
                clone.referenceFootDepth=1.5f;
                deadline=Time.realtimeSinceStartupAsDouble+30;
                while(WeaponConfigService.Current.Revision(7)==revision&&Time.realtimeSinceStartupAsDouble<deadline)
                {app.ApplyDebugWeaponChanges();yield return null;}
                Assert.That(WeaponConfigService.Current.Revision(7),Is.GreaterThan(revision),app.WeaponDebugStatus);
                Assert.That(service.LiveShots().Single().Configuration.ReferenceFootDepth,Is.EqualTo(1.2f));
                Assert.That(service.LiveShots().Single().Configuration,Is.SameAs(w));
                var next=GameplayConfig.GetWeapon(7);Assert.That(next.ReferenceFootDepth,Is.EqualTo(1.5f));
                shot.Id++;shot.ActionId++;shot.Configuration=next;shot.ConfigurationRevision=WeaponConfigService.Current.Revision(7);
                service.SpawnForMeasurement(shot);
                Assert.That(service.LiveShots().Last().Configuration.ReferenceFootDepth,Is.EqualTo(1.5f));
                File.WriteAllText(PaintParityTests.Root+"/playmode.txt","PASS: real Editor Host hot reload; in-flight bubble retains depth 1.2, new bubble receives 1.5; frozen old content rejected by production approval callback.\ncontentSignature="+BitConverter.ToString(signature).Replace("-","").ToLowerInvariant()+"\n");
            }
            finally {WeaponConfigService.Current.SetForEditor(7,w,source);UnityEngine.Object.DestroyImmediate(clone);}
            yield return app.Leave().ToCoroutine();
        }
        [UnityTest, Explicit("Capture actual boot/addressables room signature before gameplay edits")]
        public IEnumerator FreezeBaselineRoomSignature()
        {
            const string path=PaintParityTests.Root+"/Baseline/room-signature.txt";
            Assert.That(File.Exists(path),Is.False,"Frozen baseline must not be overwritten");
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            double deadline=Time.realtimeSinceStartupAsDouble+60;
            while((PrototypeApp.Current==null||!PrototypeApp.Current.Ready)&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
            Assert.That(PrototypeApp.Current!=null&&PrototypeApp.Current.Ready,Is.True);
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(PrototypeApp.Current.InRoom,Is.True);
            var field=typeof(PrototypeApp).GetField("_signature",BindingFlags.Instance|BindingFlags.NonPublic);
            var signature=(byte[])field.GetValue(PrototypeApp.Current);
            Assert.That(signature.Length,Is.EqualTo(32));
            File.WriteAllText(path,BitConverter.ToString(signature).Replace("-","").ToLowerInvariant()+"\nActual Editor Host room handshake; "+DateTime.UtcNow.ToString("O"));
            yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if(!Application.isPlaying)yield break;
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
    }
}
#endif
