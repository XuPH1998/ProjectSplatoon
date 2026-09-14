#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class Match4v4PlayTests
    {
        const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
        const string Output="Reports/Match4v4";
        static IEnumerator Wait(Func<bool> ready,string label)
        {
            double end=Time.realtimeSinceStartupAsDouble+30;
            while(!ready() && Time.realtimeSinceStartupAsDouble<end) yield return null;
            Assert.That(ready(),Is.True,label);
        }
        [UnityTest] public IEnumerator RealHostCapacityCombatTabAndReturnToWarmup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            yield return Wait(()=>PrototypeApp.Current!=null && PrototypeApp.Current.Ready,"Boot ready");
            var app=PrototypeApp.Current;
            ushort port; using(var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0))) port=(ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var connect=app.Connect(true,"127.0.0.1",port);
            yield return Wait(()=>!app.Busy,"Host connect"); connect.GetAwaiter().GetResult();
            Assert.That(app.InRoom,Is.True,app.Error);
            var match=PrototypeMatch.Current; var local=PrototypePlayer.Local;
            var view=EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var approve=typeof(PrototypeApp).GetMethod("Approve",Private);
            var signature=(byte[])typeof(PrototypeApp).GetField("_signature",Private).GetValue(app);
            for(ulong id=1;id<8;id++)
            {
                var response=new NetworkManager.ConnectionApprovalResponse();
                approve.Invoke(app,new object[]{new NetworkManager.ConnectionApprovalRequest {ClientNetworkId=id,Payload=signature},response});
                Assert.That(response.Approved,Is.True,"approval "+id); match.AddPlayer(id,prefab);
            }
            var rejected=new NetworkManager.ConnectionApprovalResponse();
            approve.Invoke(app,new object[]{new NetworkManager.ConnectionApprovalRequest {ClientNetworkId=8,Payload=signature},rejected});
            Assert.That(rejected.Approved,Is.False); Assert.That(rejected.Reason,Does.Contain("8"));
            match.AddPlayer(8,prefab); Assert.That(match.Players.Count,Is.EqualTo(8));
            foreach(byte team in new byte[]{1,2})
            {
                var members=match.Players.Where(p=>p.Snapshot.Value.Team==team).ToArray();
                Assert.That(members.Length,Is.EqualTo(4));
                Assert.That(members.Select(p=>p.Snapshot.Value.Slot).Distinct().Count(),Is.EqualTo(4));
                Assert.That(members.Select(p=>p.Snapshot.Value.Position).Distinct().Count(),Is.EqualTo(4));
            }
            var victim=match.Players.First(p=>p.Snapshot.Value.Team==2);
            var assistant=match.Players.First(p=>p.Snapshot.Value.Team==1 && p!=local);
            void Unprotect(PrototypePlayer player) {var s=player.Snapshot.Value;s.ProtectedUntil=0;player.Snapshot.Value=s;}
            Unprotect(victim); victim.ReceiveDamage(1,10,Vector3.forward,assistant.OwnerClientId);
            Assert.That(assistant.Stats.Value.Assists,Is.Zero);
            app.OpenHeroSelection(HeroSelectionOrigin.Warmup); match.StartRound();
            yield return null;
            Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Playing)); Assert.That(app.Overlay,Is.EqualTo(GameplayOverlay.Game));
            Assert.That(PrototypeApp.StartNoticeActive(match.State.Value,app.Manager.ServerTime.Time),Is.True);
            yield return Capture("start-notice");
            victim.ReceiveDamage(1,1000,Vector3.forward,local.OwnerClientId);
            Assert.That(victim.Stats.Value.Deaths,Is.Zero,"spawn protection");
            Unprotect(victim); victim.ReceiveDamage(2,1000,Vector3.forward,victim.OwnerClientId);
            Assert.That(victim.Stats.Value.Deaths,Is.Zero,"friendly fire disabled");
            victim.ReceiveDamage(1,10,Vector3.forward,assistant.OwnerClientId);
            victim.ReceiveDamage(1,1000,Vector3.forward,local.OwnerClientId);
            Assert.That(local.Stats.Value.Kills,Is.EqualTo(1)); Assert.That(assistant.Stats.Value.Assists,Is.EqualTo(1));
            Assert.That(victim.Stats.Value.Deaths,Is.EqualTo(1));
            victim.ReceiveDamage(1,1000,Vector3.forward,local.OwnerClientId);
            Assert.That(local.Stats.Value.Kills,Is.EqualTo(1));
            victim.Respawn(); Assert.That(victim.Stats.Value.Deaths,Is.EqualTo(1));
            var falling=victim.Snapshot.Value; falling.Position=new Vector3(0,-6,0); falling.Grounded=false;
            ((PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor",Private).GetValue(victim)).Restore(falling);
            victim.Snapshot.Value=falling;
            victim.Simulate(1f/60,app.Manager.ServerTime.Time,MatchPhase.Playing);
            Assert.That(victim.Stats.Value.Deaths,Is.EqualTo(2),"fall death"); Assert.That(local.Stats.Value.Kills,Is.EqualTo(1));
            var originalKeyboard=Keyboard.current; var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                yield return Wait(()=>!PrototypeApp.StartNoticeActive(match.State.Value,app.Manager.ServerTime.Time),"Start notice expires after two server seconds");
                view.Focus(); app.CaptureMouse(true);
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab)); InputSystem.Update();
                Assert.That(app.ScoreboardVisible,Is.True); Assert.That(app.HasControl,Is.True); Assert.That(Cursor.lockState,Is.EqualTo(CursorLockMode.Locked));
                yield return Capture("scoreboard");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState()); InputSystem.Update(); Assert.That(app.ScoreboardVisible,Is.False);
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab)); InputSystem.Update(); app.CaptureMouse(false);
                Assert.That(app.ScoreboardVisible,Is.False); app.CaptureMouse(true);
                var surface=match.Arena.Surfaces.Values.First(s=>s.Scores);
                match.Paint(surface,surface.transform.position,Vector3.up,1,1,1,1);
                Assert.That(match.PaintSequence,Is.GreaterThan(0));
                var round=match.State.Value; uint playingRound=round.Round; round.EndsAt=app.Manager.ServerTime.Time-.01; match.State.Value=round;
                yield return Wait(()=>match.State.Value.Phase==MatchPhase.Finished,"Natural finish");
                Assert.That(app.ScoreboardVisible,Is.False);
                match.StartRound(); Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Finished));
                yield return Capture("finished");
                var teams=match.Players.Select(p=>(p.OwnerClientId,p.Snapshot.Value.Team,p.Snapshot.Value.HeroId)).ToArray();
                match.ReturnToRoom(); yield return null;
                Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Practice)); Assert.That(match.State.Value.Round,Is.GreaterThan(playingRound));
                Assert.That(match.State.Value.StartsAt,Is.Zero); Assert.That(match.State.Value.EndsAt,Is.Zero); Assert.That(match.PaintSequence,Is.Zero);
                Assert.That(match.Arena.PinkArea+match.Arena.BlueArea,Is.Zero); Assert.That(match.Projectiles.LiveShots(),Is.Empty);
                Assert.That(app.Overlay,Is.EqualTo(GameplayOverlay.RoomMenu)); Assert.That(app.InRoom,Is.True);
                CollectionAssert.AreEqual(teams,match.Players.Select(p=>(p.OwnerClientId,p.Snapshot.Value.Team,p.Snapshot.Value.HeroId)).ToArray());
                foreach(var player in match.Players) Assert.That(player.Stats.Value.Kills+player.Stats.Value.Deaths+player.Stats.Value.Assists,Is.Zero);
                yield return Capture("returned-room");
                match.StartRound(); yield return null; Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Playing));
                File.WriteAllText(Output+"/host-flow.txt","PASS actual Boot/Addressables/NGO host: 8 approval/roster slots and ninth rejection, protected/friendly damage, KDA/respawn, held Tab, game start, natural finish, warmup reset and next round. Seven other actors are server fixtures, not independent client connections.\n");
            }
            finally { InputSystem.RemoveDevice(keyboard); if(originalKeyboard!=null) originalKeyboard.MakeCurrent(); }
            var leave=app.Leave(); yield return Wait(()=>!app.InRoom && !app.Busy,"Leave"); leave.GetAwaiter().GetResult();
        }
        static IEnumerator Capture(string name)
        {
            Directory.CreateDirectory(Output);
            string path=Path.GetFullPath(Output+"/"+name+".png");
            if(File.Exists(path)) File.Delete(path);
            yield return null;
            ScreenCapture.CaptureScreenshot(path);
            // The DX12 editor captures on a later render frame. Keep this UI state
            // alive until the file is written instead of immediately advancing it.
            yield return null; yield return Wait(()=>File.Exists(path),"Screenshot "+name);
        }
        [UnityTearDown] public IEnumerator Cleanup() { if(Application.isPlaying) yield return new ExitPlayMode(); }
    }
}
#endif
