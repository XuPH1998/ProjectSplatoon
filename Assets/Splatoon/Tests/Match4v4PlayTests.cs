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
using Splatoon.Blocks;
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
        [UnityTest, Timeout(600000)] public IEnumerator RealHostCapacityCombatTabAndReturnToWarmup()
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
            var signature=app.Manager.NetworkConfig.ConnectionData;
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
            // Assert protection at the spawn boundary. GPU screenshot/readback can outlast
            // the protection window on a loaded Editor and must not decide this gameplay check.
            Assert.That(victim.Snapshot.Value.ProtectedUntil, Is.GreaterThan(app.Manager.ServerTime.Time));
            victim.ReceiveDamage(1,1000,Vector3.forward,local.OwnerClientId);
            Assert.That(victim.Stats.Value.Deaths,Is.Zero,"spawn protection");
            yield return null;
            Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Playing)); Assert.That(app.Overlay,Is.EqualTo(GameplayOverlay.Game));
            Assert.That(PrototypeApp.StartNoticeActive(match.State.Value,app.Manager.ServerTime.Time),Is.True);
            yield return Capture("start-notice");
            Unprotect(victim); victim.ReceiveDamage(2,1000,Vector3.forward,victim.OwnerClientId);
            Assert.That(victim.Stats.Value.Deaths,Is.Zero,"friendly fire disabled");
            victim.ReceiveDamage(1,10,Vector3.forward,assistant.OwnerClientId);
            victim.ReceiveDamage(1,1000,Vector3.forward,local.OwnerClientId);
            // Lethal damage downs the victim into the existing rescue bubble; death credit follows execution.
            var downed = victim.Snapshot.Value;
            Assert.That(downed.IsBubble, Is.True); Assert.That(downed.Health, Is.Zero);
            Assert.That(local.Stats.Value.Kills, Is.Zero); Assert.That(assistant.Stats.Value.Assists, Is.Zero); Assert.That(victim.Stats.Value.Deaths, Is.Zero);
            Assert.That(match.CombatStats.Get(local.PlayerId).Kills, Is.Zero); Assert.That(match.CombatStats.Get(assistant.PlayerId).Assists, Is.Zero);
            Assert.That(match.CombatStats.Get(victim.PlayerId).Deaths, Is.Zero, "Downing cannot record a completed death");
            Assert.That(downed.DiedAt, Is.Zero); Assert.That(downed.RespawnsAt, Is.Zero);
            double executionNow = app.Manager.ServerTime.Time;
            Assert.That(executionNow, Is.LessThan(downed.BubbleUntil), "Execute the active bubble using the existing public fixture API");
            victim.EndBubble(BubbleOutcome.Executed, executionNow);
            var executed = victim.Snapshot.Value;
            Assert.That(executed.IsDead, Is.True); Assert.That(executed.BubbleResult, Is.EqualTo(BubbleOutcome.Executed));
            Assert.That(executed.RespawnsAt - executed.DiedAt, Is.EqualTo(GameplayConfig.Mode.RespawnSeconds).Within(.001));
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
                Assert.That(app.ScoreboardVisible,Is.True); Assert.That(app.HasControl,Is.True);
                if (!Application.isBatchMode)
                {
                    bool canCaptureSystemCursor = PrototypeApp.CanCaptureSystemCursor;
                    Assert.That(Cursor.lockState,Is.EqualTo(canCaptureSystemCursor ? CursorLockMode.Locked : CursorLockMode.None));
                    Assert.That(Cursor.visible,Is.EqualTo(!canCaptureSystemCursor));
                }
                yield return Capture("scoreboard");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState()); InputSystem.Update(); Assert.That(app.ScoreboardVisible,Is.False);
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab)); InputSystem.Update(); app.CaptureMouse(false);
                Assert.That(app.ScoreboardVisible,Is.False); app.CaptureMouse(true);
                var terrain=match.Blocks;
                Assert.That(terrain,Is.Not.Null,"Default gameplay uses block terrain");
                yield return Wait(()=>terrain.PendingTransactions==0,"Default terrain idle before legacy feedback");
                Assert.That(terrain.PinkCount+terrain.BlueCount,Is.Zero,"No unpaid blocks at round start");
                var surface=match.Arena.Surfaces.Values.First(s=>s.Scores);
                uint paintBefore=match.PaintSequence, terrainBefore=terrain.Revision;
                long queuedBefore=terrain.CommandsQueued;
                match.Paint(surface,surface.transform.position,Vector3.up,1,1,1,1);
                Assert.That(match.PaintSequence,Is.EqualTo(paintBefore),"Legacy feedback cannot create a paint sequence"); Assert.That(match.PaintSequence,Is.Zero);
                Assert.That(terrain.PinkCount+terrain.BlueCount,Is.Zero,"Legacy feedback cannot mint free blocks");
                Assert.That(terrain.Revision,Is.EqualTo(terrainBefore));
                Assert.That(terrain.CommandsQueued,Is.EqualTo(queuedBefore),"Legacy feedback cannot queue free terrain");
                // Pay once with the public weapon simulation and real server time. This server
                // actor fixture is not a remote client or an end-to-end projectile-input test.
                var paidState=assistant.Snapshot.Value; var weapon=GameplayConfig.GetWeapon(paidState.HeroId);
                Assert.That(weapon.FireMode,Is.EqualTo(WeaponFireMode.Automatic));
                Assert.That(weapon.BlockBuild,Is.True); Assert.That(BlockAttackService.ExplosiveMain(weapon),Is.False);
                Assert.That(assistant.IsOwner,Is.False);
                var paidInput=new PlayerInputFrame {Revision=paidState.Revision,HeroRevision=paidState.HeroRevision,
                    FireSequence=paidState.ConsumedFire,ReleaseSequence=paidState.ConsumedRelease};
                float materialBefore=paidState.Ink; uint shotBefore=paidState.ShotSequence;
                Assert.That(WeaponSimulation.Step(ref paidState,paidInput,weapon,app.Manager.ServerTime.Time,false,true),Is.False,"Ordinary release clears any input-timeout latch");
                paidInput.Fire=true; bool paid=false; double paymentDeadline=Time.realtimeSinceStartupAsDouble+30;
                while(!paid && Time.realtimeSinceStartupAsDouble<paymentDeadline)
                {
                    paid=WeaponSimulation.Step(ref paidState,paidInput,weapon,app.Manager.ServerTime.Time,false,true);
                    if(!paid) yield return null;
                }
                Assert.That(paid,Is.True,"Normal paid automatic main attack");
                Assert.That(paidState.ShotSequence,Is.EqualTo(shotBefore+1));
                Assert.That(materialBefore-paidState.Ink,Is.EqualTo(WeaponSimulation.InkCost(weapon,paidState.LastShotCharge)).Within(.001f));
                Assert.That(assistant.Snapshot.Value.Revision,Is.EqualTo(paidState.Revision));
                Assert.That(assistant.Snapshot.Value.HeroRevision,Is.EqualTo(paidState.HeroRevision));
                Assert.That(assistant.Snapshot.Value.ShotSequence,Is.EqualTo(shotBefore));
                ((PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor",Private).GetValue(assistant)).Restore(paidState);
                assistant.Snapshot.Value=paidState;
                float paidMaterial=paidState.Ink, lastMaterial=paidMaterial;
                var token=BlockAttackService.Main(assistant.PlayerId,paidState,terrain.Round,weapon);
                Assert.That(token,Is.Not.Null); Assert.That(token.Credit.IsBlockPoints,Is.True); Assert.That(token.Credit.Kind,Is.EqualTo(PaintAttackKind.Main));
                Assert.That(BlockAttackService.Main(assistant.PlayerId,paidState,terrain.Round,weapon),Is.SameAs(token));
                Assert.That(assistant.Snapshot.Value.Ink,Is.EqualTo(paidMaterial),"Issuing a paid token cannot debit again");
                int requested=Math.Min(16,token.BuildLimit); Assert.That(requested,Is.GreaterThan(0));
                Assert.That(Physics.Raycast(new Vector3(0,4,-20),Vector3.down,out var buildFloor,10,PlayerMotorSimulation.WorldMask),Is.True,"Actual map floor outside the spawn fixture");
                var unpublished=terrain.World;
                const uint buildEntity=0x89ffff01u;
                BlockAttackService.Build(token,buildFloor.point,buildFloor.normal,.35f,false,true,requested,buildEntity,1);
                Assert.That(terrain.World,Is.SameAs(unpublished),"Queueing cannot publish ahead of mesh/collision");
                Assert.That(terrain.CommandsQueued,Is.EqualTo(queuedBefore+1));
                Assert.That(assistant.Snapshot.Value.Ink,Is.EqualTo(paidMaterial),"Queueing terrain cannot debit again");
                double publicationDeadline=Time.realtimeSinceStartupAsDouble+30;
                bool published=false;
                while(!published && Time.realtimeSinceStartupAsDouble<publicationDeadline)
                {
                    var observed=assistant.Snapshot.Value;
                    Assert.That(observed.Revision,Is.EqualTo(paidState.Revision)); Assert.That(observed.HeroRevision,Is.EqualTo(paidState.HeroRevision));
                    Assert.That(observed.ShotSequence,Is.EqualTo(paidState.ShotSequence),"The accepted payment remains a single shot");
                    Assert.That(observed.Ink,Is.GreaterThanOrEqualTo(lastMaterial-.001f),"Normal recovery is allowed; asynchronous publication cannot debit again");
                    Assert.That(observed.Ink,Is.GreaterThanOrEqualTo(paidMaterial-.001f)); lastMaterial=observed.Ink;
                    published=terrain.PendingTransactions==0 && token.Added==requested &&
                        match.State.Value.PinkBlockCount==terrain.PinkCount && match.State.Value.BlueBlockCount==terrain.BlueCount &&
                        match.State.Value.TerrainRevision==terrain.Revision;
                    if(!published) yield return null;
                }
                Assert.That(published,Is.True,"Real asynchronous paid block/collider/score publication");
                Assert.That(terrain.Revision,Is.GreaterThan(terrainBefore));
                Assert.That(terrain.PinkCount,Is.EqualTo(requested)); Assert.That(terrain.BlueCount,Is.Zero);
                var firstBuiltCell=BlockWorld.CellAt(buildFloor.point+buildFloor.normal*(BlockWorld.CellSize*.51f));
                Assert.That(Physics.Raycast(BlockWorld.Center(firstBuiltCell)+Vector3.up*.2f,Vector3.down,out var blockHit,.4f,1<<BlockTerrain.TerrainLayer),Is.True,"Published block collision");
                Assert.That(terrain.TryContact(blockHit,out var blockContact),Is.True);
                Assert.That(blockContact.Stable,Is.True); Assert.That(blockContact.Owner,Is.EqualTo(paidState.Team));
                uint builtRevision=terrain.Revision; long builtCommands=terrain.CommandsQueued; float materialAtReplay=assistant.Snapshot.Value.Ink;
                BlockAttackService.Build(token,buildFloor.point,buildFloor.normal,.35f,false,true,requested,buildEntity,1);
                Assert.That(terrain.CommandsQueued,Is.EqualTo(builtCommands),"Duplicate paid contact cannot build twice");
                Assert.That(terrain.Revision,Is.EqualTo(builtRevision)); Assert.That(terrain.PinkCount,Is.EqualTo(requested));
                Assert.That(assistant.Snapshot.Value.Ink,Is.EqualTo(materialAtReplay));
                var round=match.State.Value; uint playingRound=round.Round; round.EndsAt=app.Manager.ServerTime.Time-.01; match.State.Value=round;
                yield return Wait(()=>match.State.Value.Phase==MatchPhase.Finished,"Shortened-deadline finish");
                Assert.That(app.ScoreboardVisible,Is.False);
                match.StartRound(); Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Finished));
                yield return Capture("finished");
                var teams=match.Players.Select(p=>(p.OwnerClientId,p.Snapshot.Value.Team,p.Snapshot.Value.HeroId)).ToArray();
                match.ReturnToRoom(); yield return null;
                Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Practice)); Assert.That(match.State.Value.Round,Is.GreaterThan(playingRound));
                Assert.That(match.State.Value.StartsAt,Is.Zero); Assert.That(match.State.Value.EndsAt,Is.Zero); Assert.That(match.PaintSequence,Is.Zero);
                Assert.That(match.Arena.PinkArea+match.Arena.BlueArea,Is.Zero); Assert.That(match.Projectiles.LiveShots(),Is.Empty);
                Assert.That(match.State.Value.PinkBlockCount+match.State.Value.BlueBlockCount,Is.Zero); Assert.That(match.State.Value.TerrainRevision,Is.Zero);
                Assert.That(terrain.PinkCount+terrain.BlueCount,Is.Zero); Assert.That(terrain.Revision,Is.Zero);
                Assert.That(terrain.World.Chunks,Is.Empty); Assert.That(terrain.World.Falling,Is.Empty); Assert.That(terrain.PendingTransactions,Is.Zero);
                Assert.That(app.Overlay,Is.EqualTo(GameplayOverlay.RoomMenu)); Assert.That(app.InRoom,Is.True);
                CollectionAssert.AreEqual(teams,match.Players.Select(p=>(p.OwnerClientId,p.Snapshot.Value.Team,p.Snapshot.Value.HeroId)).ToArray());
                foreach(var player in match.Players) Assert.That(player.Stats.Value.Kills+player.Stats.Value.Deaths+player.Stats.Value.Assists,Is.Zero);
                yield return Capture("returned-room");
                match.StartRound(); yield return null; Assert.That(match.State.Value.Phase,Is.EqualTo(MatchPhase.Playing));
                Assert.That(terrain.PinkCount+terrain.BlueCount,Is.Zero); Assert.That(terrain.Revision,Is.Zero);
                Assert.That(match.State.Value.PinkBlockCount+match.State.Value.BlueBlockCount,Is.Zero); Assert.That(match.State.Value.TerrainRevision,Is.Zero);
                File.WriteAllText(Output+"/host-flow.txt","PASS actual Boot/Addressables/NGO host: 8 approval/roster slots and ninth rejection, protected/friendly damage, KDA/respawn, held Tab, game start, rejected legacy free feedback, single paid main attack and real asynchronous block/collider/score publication without duplicate material debit, shortened-deadline finish, block-cleared warmup reset and next round. Seven other actors are server fixtures, not independent client connections.\n");
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
        [UnityTest, Timeout(600000)]
        public IEnumerator RealHostNatural180SecondBlockVictoryAndPracticeReset()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return NaturalBlockRound(false);
            yield return new ExitPlayMode();
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator RealHostNatural180SecondBlockDrawAndPracticeReset()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return NaturalBlockRound(true);
            yield return new ExitPlayMode();
        }
        static IEnumerator NaturalRoundWait(Func<bool> ready, string label)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        static IEnumerator NaturalRoundCapture(string path)
        {
            Assert.That(File.Exists(path), Is.False, "Preserve any prior screenshot");
            yield return null;
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return NaturalRoundWait(() => File.Exists(path), "Rendered screenshot " + path);
            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length, Is.GreaterThan(8));
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8).ToArray());
        }
        static IEnumerator NaturalBlockRound(bool draw)
        {
            yield return NaturalRoundWait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Real Boot ready");
            var app = PrototypeApp.Current;
            Assert.That(GameplayConfig.Mode.MatchSeconds, Is.EqualTo(180f), "Use the real three-minute configuration");
            Assert.That(Time.timeScale, Is.EqualTo(1f), "Never accelerate or pause the simulation");
            Assert.That(Time.captureFramerate, Is.Zero, "No synthetic frame clock");
            Assert.That(Time.fixedDeltaTime, Is.EqualTo(1f / 60).Within(.000001f));
            Assert.That(Application.isBatchMode, Is.False, "Retain the actual rendered IMGUI result");
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            ushort port;
            do { using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port; }
            while (port == 47777);
            var connect = app.Connect(true, "127.0.0.1", port);
            yield return NaturalRoundWait(() => !app.Busy, "Real ordinary host connect");
            connect.GetAwaiter().GetResult();
            Assert.That(app.InRoom, Is.True, app.Error);
            Assert.That(app.IsWeaponDebugRoom, Is.False);
            var match = PrototypeMatch.Current;
            yield return NaturalRoundWait(() => match != null && match.Blocks != null && match.InitialSyncComplete && PrototypePlayer.Local != null, "Default block host ready");
            var terrain = match.Blocks;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var approval = new NetworkManager.ConnectionApprovalResponse();
            typeof(PrototypeApp).GetMethod("Approve", Private).Invoke(app, new object[] {
                new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 1, Payload = app.Manager.NetworkConfig.ConnectionData }, approval });
            Assert.That(approval.Approved, Is.True, approval.Reason);
            match.AddPlayer(1, prefab); // One server-side actor fixture; this is not an independent remote client.
            Assert.That(match.CanStartRound, Is.True, "Use the normal two-team start precondition");
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            view.Show(); view.Focus();
            var originalKeyboard = Keyboard.current; var originalMouse = Mouse.current;
            var keyboard = InputSystem.AddDevice<Keyboard>(); var mouse = InputSystem.AddDevice<Mouse>();
            try
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.QueueStateEvent(mouse, new MouseState()); InputSystem.Update();
                string directory = Path.GetFullPath("Reports/BlockTerrain/FunctionalCoverage89/NaturalRound/Executions/" +
                    (draw ? "draw-" : "victory-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                double wallStarted = Time.realtimeSinceStartupAsDouble;
                match.StartRound(); // The same production entry called by the room button / Enter key.
                var started = match.State.Value;
                Assert.That(started.Phase, Is.EqualTo(MatchPhase.Playing));
                Assert.That(started.EndsAt - started.StartsAt, Is.EqualTo(180d).Within(.000001d));
                Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero);
                yield return null;
                Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Game), "Production start transition");
                Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10, PlayerMotorSimulation.WorldMask), Is.True, "Actual map floor");
                int pink = draw ? 4 : 6, blue = draw ? 4 : 3;
                var center = BlockWorld.CellAt(floor.point + Vector3.up * .05f);
                var cells = new List<Vector3Int>();
                var seed = new BlockWorld(terrain.World.Dimensions, started.Round, terrain.World.Bottom);
                for (int team = 1; team <= 2; team++)
                    for (int i = 0; i < (team == 1 ? pink : blue); i++)
                    {
                        var cell = center + Vector3Int.right * i + Vector3Int.forward * (team == 1 ? 0 : 4);
                        Assert.That(seed.Set(cell, BlockCell.Create((byte)team, true)), Is.True);
                        cells.Add(cell);
                    }
                var unpublished = terrain.World;
                Assert.That(terrain.QueueSeed(seed), Is.True, "Diagnostic fixture uses the production asynchronous collider publication");
                Assert.That(terrain.World, Is.SameAs(unpublished), "Queuing cannot publish raw data ahead of colliders");
                yield return NaturalRoundWait(() => terrain.PendingTransactions == 0 && terrain.PinkCount == pink && terrain.BlueCount == blue, "Atomic seeded terrain publication");
                Assert.That(match.State.Value.PinkBlockCount, Is.EqualTo(pink));
                Assert.That(match.State.Value.BlueBlockCount, Is.EqualTo(blue));
                Assert.That(match.State.Value.TerrainRevision, Is.EqualTo(terrain.Revision));
                foreach (var cell in cells)
                {
                    Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up, Vector3.down, out var hit, 2, 1 << BlockTerrain.TerrainLayer), Is.True);
                    Assert.That(terrain.TryContact(hit, out var contact), Is.True);
                    Assert.That(contact.Stable, Is.True, "Fixture cells remain supported during the full round");
                }
                double firstFinishWallSeconds = -1;
                // Observe at least 180 real wall-clock seconds. Never mutate the deadline, timeScale,
                // Phase, simulation clock, or manually pump Tick/ServerTick/FinishRound/settlement.
                while (Time.realtimeSinceStartupAsDouble - wallStarted < 180 || match.State.Value.Phase == MatchPhase.Playing)
                {
                    Assert.That(Time.realtimeSinceStartupAsDouble - wallStarted, Is.LessThan(210), "Natural finish watchdog; the product remains exactly 180 seconds");
                    Assert.That(Time.timeScale, Is.EqualTo(1f)); Assert.That(Time.captureFramerate, Is.Zero);
                    Assert.That(GameplayConfig.Mode.MatchSeconds, Is.EqualTo(180f));
                    Assert.That(match.enabled && match.Players.All(p => p != null && p.enabled), Is.True, "Keep the normal production simulation running");
                    var observed = match.State.Value;
                    Assert.That(observed.Round, Is.EqualTo(started.Round));
                    Assert.That(observed.StartsAt, Is.EqualTo(started.StartsAt)); Assert.That(observed.EndsAt, Is.EqualTo(started.EndsAt));
                    Assert.That(observed.Phase, Is.EqualTo(MatchPhase.Playing).Or.EqualTo(MatchPhase.Finished));
                    Assert.That(observed.PinkBlockCount, Is.EqualTo(pink)); Assert.That(observed.BlueBlockCount, Is.EqualTo(blue));
                    if (observed.Phase == MatchPhase.Finished)
                    {
                        if (firstFinishWallSeconds < 0) firstFinishWallSeconds = Time.realtimeSinceStartupAsDouble - wallStarted;
                        Assert.That(app.Manager.ServerTime.Time, Is.GreaterThanOrEqualTo(started.EndsAt));
                        Assert.That(observed.TerrainFrozenAt, Is.GreaterThanOrEqualTo(started.EndsAt));
                    }
                    yield return null;
                }
                var finished = match.State.Value;
                double observedWallSeconds = Time.realtimeSinceStartupAsDouble - wallStarted;
                if (firstFinishWallSeconds < 0) firstFinishWallSeconds = observedWallSeconds;
                Assert.That(observedWallSeconds, Is.GreaterThanOrEqualTo(180d));
                Assert.That(observedWallSeconds, Is.LessThan(210d), "Natural finish watchdog also applies on the terminal observation");
                Assert.That(app.Manager.NetworkConfig.TickRate, Is.EqualTo((uint)GameplayConfig.Global.NetworkTickRate));
                Assert.That(firstFinishWallSeconds, Is.GreaterThanOrEqualTo(180d - 1d / app.Manager.NetworkConfig.TickRate),
                    "First natural finish may differ by only one production network-tick sample; the round configuration and total real observation stay 180 seconds");
                Assert.That(finished.Phase, Is.EqualTo(MatchPhase.Finished));
                Assert.That(finished.TerrainFrozenAt - started.StartsAt, Is.GreaterThanOrEqualTo(180d));
                Assert.That(finished.PinkBlockCount, Is.EqualTo(terrain.PinkCount)); Assert.That(finished.BlueBlockCount, Is.EqualTo(terrain.BlueCount));
                int winner = PrototypeRules.Winner(finished.PinkBlockCount, finished.BlueBlockCount);
                Assert.That(winner, Is.EqualTo(draw ? 0 : 1), "Winner must use surviving block counts");
                yield return NaturalRoundWait(() => app.Overlay == GameplayOverlay.RoomMenu, "Natural production result overlay");
                string finishedPng = Path.Combine(directory, "finished.png");
                var frozenBytes = terrain.ExportSnapshot();
                yield return NaturalRoundCapture(finishedPng);
                CollectionAssert.AreEqual(frozenBytes, terrain.ExportSnapshot(), "The finished terrain remains frozen while the real result is rendered");
                var roster = match.Players.Select(p => (p.OwnerClientId, p.Snapshot.Value.Team, p.Snapshot.Value.HeroId)).ToArray();
                match.ReturnToRoom();
                yield return NaturalRoundWait(() => match.State.Value.Phase == MatchPhase.Practice && app.Overlay == GameplayOverlay.RoomMenu, "Return to real Practice overlay");
                var practice = match.State.Value;
                Assert.That(practice.Round, Is.GreaterThan(started.Round)); Assert.That(terrain.Round, Is.EqualTo(practice.Round));
                Assert.That(practice.StartsAt, Is.Zero); Assert.That(practice.EndsAt, Is.Zero); Assert.That(practice.TerrainFrozenAt, Is.Zero);
                Assert.That(practice.PinkBlockCount + practice.BlueBlockCount, Is.Zero); Assert.That(practice.TerrainRevision, Is.Zero);
                Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero); Assert.That(terrain.World.Chunks, Is.Empty); Assert.That(terrain.World.Falling, Is.Empty);
                Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(match.Projectiles.LiveShots(), Is.Empty);
                CollectionAssert.AreEqual(roster, match.Players.Select(p => (p.OwnerClientId, p.Snapshot.Value.Team, p.Snapshot.Value.HeroId)).ToArray());
                string practicePng = Path.Combine(directory, "practice-cleared.png");
                yield return NaturalRoundCapture(practicePng);
                match.StartRound(); yield return null;
                Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Playing));
                Assert.That(match.Blocks.PinkCount + match.Blocks.BlueCount, Is.Zero, "The next normal round cannot retain earlier blocks");
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonUtility.ToJson(new NaturalRoundResult {
                    naturalWallSeconds = observedWallSeconds, firstFinishWallSeconds = firstFinishWallSeconds,
                    configuredSeconds = GameplayConfig.Mode.MatchSeconds, simulationScale = Time.timeScale,
                    round = started.Round, StartsAt = started.StartsAt, EndsAt = started.EndsAt, TerrainFrozenAt = finished.TerrainFrozenAt,
                    PinkBlockCount = finished.PinkBlockCount, BlueBlockCount = finished.BlueBlockCount, winner = winner,
                    expectedChineseResult = draw ? "平局" : "粉队获胜", actualFinishedOverlay = GameplayOverlay.RoomMenu.ToString(),
                    finishedPng = finishedPng, practicePng = practicePng, priorRoundCleared = true, nextNormalRoundEmpty = true,
                    independentRemoteClientValidated = false, chineseHudPixelsReviewed = false, blockGridOrManualFeelValidated = false
                }, true));
                var leave = app.Leave(); yield return NaturalRoundWait(() => !app.InRoom && !app.Busy, "Leave ordinary host");
                leave.GetAwaiter().GetResult();
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard); InputSystem.RemoveDevice(mouse);
                if (originalKeyboard != null) originalKeyboard.MakeCurrent();
                if (originalMouse != null) originalMouse.MakeCurrent();
            }
        }
        [Serializable]
        sealed class NaturalRoundResult
        {
            public double naturalWallSeconds, firstFinishWallSeconds, configuredSeconds;
            public float simulationScale;
            public uint round;
            public double StartsAt, EndsAt, TerrainFrozenAt;
            public int PinkBlockCount, BlueBlockCount, winner;
            public string expectedChineseResult, actualFinishedOverlay, finishedPng, practicePng;
            public bool priorRoundCleared, nextNormalRoundEmpty;
            public bool independentRemoteClientValidated, chineseHudPixelsReviewed, blockGridOrManualFeelValidated;
        }
        // Short diagnosis only. A pass here never accepts a natural 180-second round.
        [UnityTest, Timeout(600000)]
        public IEnumerator RealHostClockStartSamplingTrace()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return ClockStartSamplingScenario();
            yield return new ExitPlayMode();
        }

        static readonly FieldInfo ClockTraceAppSignature = typeof(PrototypeApp).GetField("_signature", Private);
        static readonly FieldInfo ClockTraceSimulationTime = typeof(PrototypeMatch).GetField("_simulationTime", Private);
        static readonly FieldInfo ClockTraceFrameOffset = typeof(PrototypeMatch).GetField("_networkFrameOffset", Private);

        static IEnumerator ClockStartSamplingScenario()
        {
            string directory = Path.GetFullPath("Reports/BlockTerrain/SerialExecution88/ClockTrace94/Executions/clock-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
            var result = new ClockStartTraceResult { outputDirectory = directory };
            PrototypeApp app = null;
            NetworkManager manager = null;
            Keyboard keyboard = null, originalKeyboard = null;
            Mouse mouse = null, originalMouse = null;
            Action serverStarted = null;
            bool firstSpawnObserved = false;
            double roundWallStart = -1;
            void Sample(string label) { result.samples.Add(ReadClockStartSample(label, app)); result.lastCheckpoint = label; }
            void ObserveSpawn()
            {
                var current = PrototypeMatch.Current;
                if (!firstSpawnObserved && current != null && current.IsSpawned)
                {
                    firstSpawnObserved = true;
                    Sample("matchOnSpawnFirstObservable");
                }
            }
            try
            {
                Sample("playModeEnteredBeforeBootReady");
                yield return NaturalRoundWait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Clock trace real Boot ready");
                app = PrototypeApp.Current;
                Sample("bootReadyBeforeConnect");
                result.loadedAssemblies = new[] {
                    ReadClockTraceAssembly(typeof(PrototypeApp).Assembly),
                    ReadClockTraceAssembly(typeof(GameplayConfig).Assembly),
                    ReadClockTraceAssembly(typeof(Match4v4PlayTests).Assembly)
                };
                result.configuration180Verified = GameplayConfig.Mode.MatchSeconds == 180f;
                Assert.That(result.configuration180Verified, Is.True, "Trace retains the actual 180-second configuration");
                manager = app.Manager;
                serverStarted = () => { Sample("ngoOnServerStartedCallback"); ObserveSpawn(); };
                manager.OnServerStarted += serverStarted;
                ushort port;
                do { using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port; }
                while (port == 47777);
                result.hostPort = port;
                Sample("immediatelyBeforeConnectCall");
                var connect = app.Connect(true, "127.0.0.1", port);
                Sample("connectCallReturned"); ObserveSpawn();
                yield return NaturalRoundWait(() => { ObserveSpawn(); return !app.Busy; }, "Clock trace ordinary host connect");
                Sample("connectCompletedObservable");
                connect.GetAwaiter().GetResult();
                Assert.That(app.InRoom, Is.True, app.Error);
                Assert.That(app.IsWeaponDebugRoom, Is.False, "Use an ordinary host");
                var match = PrototypeMatch.Current;
                yield return NaturalRoundWait(() => {
                    ObserveSpawn();
                    return match != null && match.IsSpawned && match.Blocks != null && match.InitialSyncComplete && PrototypePlayer.Local != null;
                }, "Clock trace ordinary block host ready");
                Sample("hostReadyBeforeSynchronousFixture");
                // Preserve the natural fixture's synchronous actor/view/device order.
                // No clock-maturity yield is inserted before StartRound: this diagnoses the original boundary.
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
                var approval = new NetworkManager.ConnectionApprovalResponse();
                typeof(PrototypeApp).GetMethod("Approve", Private).Invoke(app, new object[] {
                    new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 1, Payload = app.Manager.NetworkConfig.ConnectionData }, approval });
                Assert.That(approval.Approved, Is.True, approval.Reason);
                match.AddPlayer(1, prefab);
                Sample("actorAdded");
                Assert.That(match.CanStartRound, Is.True, "Normal two-team start precondition");
                var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                view.Show(); view.Focus();
                Sample("gameViewShownAndFocused");
                originalKeyboard = Keyboard.current; originalMouse = Mouse.current;
                keyboard = InputSystem.AddDevice<Keyboard>(); mouse = InputSystem.AddDevice<Mouse>();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.QueueStateEvent(mouse, new MouseState()); InputSystem.Update();
                Sample("inputDevicesAddedAndEmptyInputUpdated");
                Directory.CreateDirectory(directory);
                Sample("immediatelyBeforeStartRound");
                roundWallStart = Time.realtimeSinceStartupAsDouble;
                result.roundWallStart = roundWallStart;
                match.StartRound();
                Sample("immediatelyAfterStartRound");
                var started = match.State.Value;
                result.normalRoundStarted = started.Phase == MatchPhase.Playing;
                result.startFrame = Time.frameCount;
                result.round = started.Round; result.StartsAt = started.StartsAt; result.EndsAt = started.EndsAt;
                Assert.That(result.normalRoundStarted, Is.True, "Use the normal production StartRound entry");
                Assert.That(started.EndsAt - started.StartsAt, Is.EqualTo(180d).Within(.000001d), "Never shorten the production round");
                // Observe only ordinary yielded frames, at most eight frames or six real seconds.
                // A main-thread stall cannot be preempted; retain that overrun, cleanly leave, then fail the bound.
                while (result.normalFramesObserved < 8 && Time.realtimeSinceStartupAsDouble - roundWallStart < 6d)
                {
                    yield return null;
                    result.normalFramesObserved++;
                    Sample("afterOrdinaryFrame-" + result.normalFramesObserved);
                }
                Sample("beforeOrdinaryLeave");
                result.roundWallSecondsBeforeLeave = Time.realtimeSinceStartupAsDouble - roundWallStart;
                result.activeObservationWithin10Seconds = result.roundWallSecondsBeforeLeave <= 10d;
                var leave = app.Leave();
                Sample("ordinaryLeaveCallReturned");
                yield return NaturalRoundWait(() => {
                    if (result.roundWallSecondsUntilHostStopped < 0 && !manager.IsListening && !manager.ShutdownInProgress)
                    {
                        result.roundWallSecondsUntilHostStopped = Time.realtimeSinceStartupAsDouble - roundWallStart;
                        Sample("ordinaryHostStoppedFirstObservable");
                    }
                    return !app.InRoom && !app.Busy && !manager.IsListening && !manager.ShutdownInProgress;
                }, "Clock trace ordinary leave cleanup");
                leave.GetAwaiter().GetResult();
                Sample("ordinaryLeaveCleanupObserved");
                result.leaveCleanupVerified = !app.InRoom && !app.Busy && !manager.IsListening && !manager.ShutdownInProgress
                    && PrototypeMatch.Current == null && PrototypePlayer.Local == null;
                result.activeObservationWithin10Seconds &= result.roundWallSecondsUntilHostStopped >= 0
                    && result.roundWallSecondsUntilHostStopped <= 10d;
                result.diagnosticPass = result.configuration180Verified && result.normalRoundStarted
                    && result.leaveCleanupVerified && result.activeObservationWithin10Seconds;
                Assert.That(result.leaveCleanupVerified, Is.True, "Normal host, room, match and local-player cleanup");
                Assert.That(result.activeObservationWithin10Seconds, Is.True, "Finite clock trace exceeded ten real seconds; preserve the overrun");
            }
            finally
            {
                // No known clock difference is asserted. Evidence survives an unrelated setup/cleanup failure.
                if (manager != null)
                {
                    if (serverStarted != null) manager.OnServerStarted -= serverStarted;
                }
                if (keyboard != null) InputSystem.RemoveDevice(keyboard);
                if (mouse != null) InputSystem.RemoveDevice(mouse);
                if (originalKeyboard != null) originalKeyboard.MakeCurrent();
                if (originalMouse != null) originalMouse.MakeCurrent();
                result.inputDeviceCleanupExecuted = true;
                result.finishedUtc = DateTime.UtcNow.ToString("O");
                result.matchSpawnObserved = firstSpawnObserved;
                result.finalStatus = result.diagnosticPass ? "DIAGNOSTIC_PASS_NOT_NATURAL180_ACCEPTANCE" : "DIAGNOSTIC_INCOMPLETE_OR_FAILED";
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "clock-trace.json");
                if (File.Exists(path)) throw new IOException("Preserve prior clock trace: " + path);
                File.WriteAllText(path, JsonUtility.ToJson(result, true));
                Debug.Log("[ClockTrace94] " + result.finalStatus + " " + path);
            }
        }

        static ClockStartSample ReadClockStartSample(string label, PrototypeApp app)
        {
            var sample = new ClockStartSample {
                label = label, wall = Time.realtimeSinceStartupAsDouble, frame = Time.frameCount,
                unscaledTime = Time.unscaledTimeAsDouble, unscaledDelta = Time.unscaledDeltaTime,
                unityTime = Time.timeAsDouble, fixedTime = Time.fixedTimeAsDouble,
                fixedDelta = Time.fixedDeltaTime, maximumDelta = Time.maximumDeltaTime,
                timeScale = Time.timeScale, captureFramerate = Time.captureFramerate
            };
            var manager = app != null ? app.Manager : null;
            sample.hasApp = app != null; sample.hasManager = manager != null;
            if (app != null) { sample.appBusy = app.Busy; sample.inRoom = app.InRoom; }
            if (manager != null)
            {
                sample.isListening = manager.IsListening;
                sample.isServer = manager.IsServer; sample.isHost = manager.IsHost;
                sample.hasNgoClock = manager.NetworkTickSystem != null && manager.NetworkTimeSystem != null;
                sample.ngoTickRate = manager.NetworkConfig.TickRate;
                if (sample.hasNgoClock)
                {
                    sample.ngoServer = manager.ServerTime.Time; sample.ngoLocal = manager.LocalTime.Time;
                    sample.ngoServerTick = manager.ServerTime.Tick;
                    sample.ngoTimeSystemServer = manager.NetworkTimeSystem.ServerTime;
                    sample.ngoTimeSystemLocal = manager.NetworkTimeSystem.LocalTime;
                }
            }
            var match = PrototypeMatch.Current;
            sample.hasMatch = match != null;
            if (match != null)
            {
                sample.matchIsSpawned = match.IsSpawned;
                var state = match.State.Value;
                sample.phase = state.Phase.ToString(); sample.round = state.Round;
                sample.StartsAt = state.StartsAt; sample.EndsAt = state.EndsAt;
                sample.TerrainFrozenAt = state.TerrainFrozenAt;
                sample.hasSimulationReadback = ClockTraceSimulationTime != null && ClockTraceFrameOffset != null;
                if (sample.hasSimulationReadback)
                {
                    sample.simulationTime = (double)ClockTraceSimulationTime.GetValue(match);
                    sample.networkFrameOffset = (double)ClockTraceFrameOffset.GetValue(match);
                }
            }
            var luban = LubanConfigService.Current;
            sample.lubanReady = luban.IsReady;
            sample.lubanSignatureHex = ClockTraceHex(luban.ContentSignature);
            if (luban.IsReady)
            {
                var mode = GameplayConfig.Mode;
                sample.modeId = mode.Id; sample.modeDisplayName = mode.Name; sample.configuredSeconds = mode.MatchSeconds;
            }
            sample.appSignatureFieldObserved = ClockTraceAppSignature != null && app != null;
            byte[] appSignature = sample.appSignatureFieldObserved ? ClockTraceAppSignature.GetValue(app) as byte[] : null;
            sample.appGameplaySignatureHash = ClockTraceHash(appSignature);
            var payload = manager != null ? manager.NetworkConfig.ConnectionData : null;
            sample.connectionPayloadBytes = payload != null ? payload.Length : 0;
            if (payload != null && payload.Length >= 37)
            {
                var actualSignature = new byte[32]; Buffer.BlockCopy(payload, 4, actualSignature, 0, actualSignature.Length);
                sample.connectionSignatureHash = ClockTraceHash(actualSignature);
                sample.connectionPayloadMatchesAppSignature = PlayerConnectionPayload.TryDecode(payload, appSignature, out _, out var error);
                sample.connectionValidationError = sample.connectionPayloadMatchesAppSignature ? "" : error;
            }
            sample.wallReadComplete = Time.realtimeSinceStartupAsDouble;
            return sample;
        }

        static string ClockTraceHex(byte[] bytes) => bytes == null ? "" : BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        static string ClockTraceHash(byte[] bytes)
        {
            if (bytes == null) return "";
            using var sha = System.Security.Cryptography.SHA256.Create();
            return ClockTraceHex(sha.ComputeHash(bytes));
        }
        static ClockTraceAssembly ReadClockTraceAssembly(Assembly assembly) => new ClockTraceAssembly {
            name = assembly.GetName().Name, loadedMvid = assembly.ManifestModule.ModuleVersionId.ToString(), location = assembly.Location
        };

        [Serializable]
        sealed class ClockStartTraceResult
        {
            public int schema = 1, normalFramesObserved, startFrame;
            public bool diagnosticOnly = true, natural180AcceptancePassed = false, independentRemoteClientValidated = false;
            public bool configuration180Verified, normalRoundStarted, leaveCleanupVerified, activeObservationWithin10Seconds;
            public bool diagnosticPass, inputDeviceCleanupExecuted, matchSpawnObserved;
            public string outputDirectory, finishedUtc, finalStatus, lastCheckpoint;
            public uint playerProtocolVersion = PlayerSnapshot.ProtocolVersion;
            public int paintProtocolVersion = GameplayContentSignature.PaintProtocolVersion;
            public int weaponSimulationVersion = GameplayContentSignature.WeaponSimulationVersion;
            public int blockProtocolVersion = GameplayContentSignature.BlockProtocolVersion;
            public string spawnObservationMeaning = "First observer sees Current.IsSpawned; not the exact OnNetworkSpawn callback timestamp";
            public double roundWallStart, roundWallSecondsBeforeLeave, StartsAt, EndsAt;
            public double roundWallSecondsUntilHostStopped = -1;
            public uint round;
            public ushort hostPort;
            public ClockTraceAssembly[] loadedAssemblies;
            public List<ClockStartSample> samples = new();
        }
        [Serializable]
        sealed class ClockTraceAssembly { public string name, loadedMvid, location; }
        [Serializable]
        sealed class ClockStartSample
        {
            public string label, phase, modeDisplayName, lubanSignatureHex, appGameplaySignatureHash, connectionSignatureHash, connectionValidationError;
            public bool hasApp, appBusy, inRoom, hasManager, isListening, isServer, isHost, hasNgoClock, hasMatch, matchIsSpawned;
            public bool hasSimulationReadback, lubanReady, appSignatureFieldObserved, connectionPayloadMatchesAppSignature;
            public int frame, captureFramerate, ngoServerTick, modeId, connectionPayloadBytes;
            public uint round, ngoTickRate;
            public float timeScale, configuredSeconds;
            public double wall, wallReadComplete, unscaledTime, unscaledDelta, unityTime, fixedTime, fixedDelta, maximumDelta;
            public double ngoServer, ngoLocal, ngoTimeSystemServer, ngoTimeSystemLocal, StartsAt, EndsAt, TerrainFrozenAt, simulationTime, networkFrameOffset;
        }

        [UnityTearDown] public IEnumerator Cleanup() { if(Application.isPlaying) yield return new ExitPlayMode(); }
    }
}
#endif
