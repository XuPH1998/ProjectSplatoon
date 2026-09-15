#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class SwimPlayTests
    {
        const string Output = "Reports/PaperBody/PlayMode";
        const float Dt = 1f / 60;
        static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 25;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnityTest] public IEnumerator RealHostSharedModelHitsJumpAndLifecycle()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        [UnityTest] public IEnumerator RealHostRifleReleaseRecoversWhileSwimming()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return RifleRecoveryScenario();
            yield return new ExitPlayMode();
        }
        [UnityTest] public IEnumerator RealHostAllHeroesAirPaperLandsAtVisualContact()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return PaperLandingScenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator PaperLandingScenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Landing bootstrap");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return PrototypeApp.Current.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypePlayer.Local.SwimBody != null, "Landing host");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current; var arena = PrototypeArena.Current;
            match.enabled = player.enabled = false;
            Vector3 spawn = arena.SpawnPoints[0].position + Vector3.forward * 2;
            Assert.That(Physics.Raycast(spawn + Vector3.up * .2f, Vector3.down, out var floor, 2, PlayerMotorSimulation.WorldMask), Is.True);
            using var report = new StreamWriter(Output + "/air-paper-landing.csv");
            report.WriteLine("hero,lastAirHeight,landedHeight,landingSnap,ticks");
            for (int hero = 1; hero <= 6; hero++)
            {
                match.enabled = true; player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Landing hero " + hero);
                match.enabled = false;
                Reset(player, floor.point + Vector3.up * .04f, 1);
                for (int i = 0; i < 5; i++) Tick(player, false);
                for (int i = 0; i < 12; i++) Tick(player, false, 1);
                var human = player.Snapshot.Value; Assert.That(human.Grounded, Is.False);
                Tick(player, true, 1); var opened = player.Snapshot.Value;
                Assert.That(opened.PaperCenter.y, Is.EqualTo(human.Position.y + .9f + opened.VerticalSpeed * Dt).Within(.005f));
                bool captured = false, landed = false;
                for (int i = 0; i < 250; i++)
                {
                    var before = player.Snapshot.Value;
                    if (!captured && (hero == 1 || hero == 6) && before.PaperCenter.y - floor.point.y < .1f)
                    { Capture(player, "landing-hero-" + hero + "-air"); captured = true; }
                    Tick(player, true, 1); Present(player); var s = player.Snapshot.Value;
                    Assert.That(Vector3.Distance(player.SwimBody.BodyRenderer.transform.position, s.PaperCenter), Is.LessThan(.0001f));
                    Assert.That(Vector3.Distance(player.SwimBody.HitVolume.transform.position, s.PaperCenter), Is.LessThan(.0001f));
                    if (!s.Grounded) { Assert.That(s.HasInkRecovery, Is.False); continue; }
                    Assert.That(before.PaperCenter.y - floor.point.y, Is.LessThan(.11f));
                    Assert.That(s.PaperCenter.y - floor.point.y, Is.EqualTo(.03f).Within(.005f));
                    Assert.That(Mathf.Abs(before.PaperCenter.y - s.PaperCenter.y), Is.LessThan(.08f));
                    report.WriteLine($"{hero},{before.PaperCenter.y-floor.point.y:F5},{s.PaperCenter.y-floor.point.y:F5},{before.PaperCenter.y-s.PaperCenter.y:F5},{i+1}"); report.Flush();
                    if (hero == 1 || hero == 6) Capture(player, "landing-hero-" + hero + "-ground");
                    landed = true; break;
                }
                Assert.That(landed, Is.True, "hero " + hero);
                yield return null;
            }
        }

        static IEnumerator RifleRecoveryScenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Recovery bootstrap");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return PrototypeApp.Current.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypePlayer.Local.SwimBody != null, "Recovery host");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current; var arena = PrototypeArena.Current;
            Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(1));
            // Drive the production authority step explicitly so hardware input and
            // the regular match tick cannot interleave with the regression sequence.
            match.enabled = player.enabled = false;
            var spawn = arena.SpawnPoints[0].position + Vector3.forward * 2;
            Assert.That(Physics.Raycast(spawn + Vector3.up * .2f, Vector3.down, out var hit, 1, PlayerMotorSimulation.WorldMask), Is.True);
            var surface = hit.collider.GetComponentInParent<PaintSurface>();
            Assert.That(surface, Is.Not.Null);
            match.Paint(surface, hit.point, hit.normal, 6, 1, 1, 1, 121);
            var rows = new List<string>();
            var w = GameplayConfig.GetHero(1);
            foreach (bool moving in new[] { false, true }) foreach (int delay in new[] { 0, 1, 2 })
            {
                Reset(player, hit.point + Vector3.up * .04f, 1);
                var start = player.Snapshot.Value;
                WeaponSimulation.Cancel(ref start, default);
                start.Ink = 20; start.SwimWasHeld = false;
                player.Snapshot.Value = start;
                for (int i = 0; i < 8; i++) Tick(player, false);
                uint shotsBefore = player.Snapshot.Value.ShotSequence;
                int firingTicks = WeaponTimeFixture.ReferenceFrames(GameplayConfig.GetWeapon(w.Id).StartSeconds) + (int)Math.Ceiling(3 * WeaponSimulation.FireInterval(GameplayConfig.GetWeapon(w.Id)) * 60) + 1;
                for (int i = 0; i < firingTicks; i++) Tick(player, false, fire: true);
                var fired = player.Snapshot.Value;
                Assert.That(fired.ShotSequence, Is.GreaterThan(shotsBefore));
                Assert.That(fired.WeaponPhase, Is.EqualTo(WeaponPhase.Firing));
                int recoveryTicks = 0; float distance = 0;
                for (int tick = 0; tick < 240; tick++)
                {
                    var before = player.Snapshot.Value;
                    bool swim = tick >= delay;
                    Tick(player, swim, move: moving ? Vector2.up : Vector2.zero, yaw: moving ? tick * 6 : 0);
                    var s = player.Snapshot.Value;
                    distance += Vector3.Distance(before.Position, s.Position);
                    Assert.That(s.Grounded, Is.True, $"moving={moving}, delay={delay}, tick={tick}");
                    if (swim) Assert.That(s.HasInkRecovery, Is.True, "Live ground must remain friendly");
                    Assert.That(s.WeaponPhase, Is.EqualTo(tick == 0 ? WeaponPhase.Ending : WeaponPhase.Idle));
                    Assert.That(s.ShotSequence, Is.EqualTo(fired.ShotSequence));
                    Assert.That(s.NextShotAt, Is.EqualTo(fired.NextShotAt));
                    Assert.That(s.InkRecoverAt, Is.EqualTo(fired.InkRecoverAt));
                    bool recovering = tick > 0 && s.SimulatedAt + 1e-8 >= fired.InkRecoverAt;
                    float rate = s.HasInkRecovery ? w.SwimRecoverInk : w.RecoverInk;
                    float expected = recovering ? Mathf.Min(w.MaxInk, before.Ink + rate * Dt) : before.Ink;
                    Assert.That(s.Ink, Is.EqualTo(expected).Within(.0001f), "Authoritative ink tick " + tick);
                    if (recovering && before.Ink < w.MaxInk) recoveryTicks++;
                }
                Assert.That(recoveryTicks, Is.GreaterThan(0));
                Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(w.MaxInk));
                if (moving) Assert.That(distance, Is.GreaterThan(10));
                rows.Add($"moving={moving}, delayTicks={delay}, shots={fired.ShotSequence - shotsBefore}, inkAfterFire={fired.Ink}, inkAfter4s={player.Snapshot.Value.Ink}, recoveryTicks={recoveryTicks}, distance={distance}");
                match.Projectiles.Clear();
            }
            const string output = "Reports/InkRecovery";
            Directory.CreateDirectory(output);
            File.WriteAllLines(output + "/host-recovery.txt", rows);
            yield return PrototypeApp.Current.Leave().ToCoroutine();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        static PlayerMotorSimulation Motor(PrototypePlayer player) => (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(player);
        static void Tick(PrototypePlayer player, bool swim, uint jump = 0, Vector2 move = default, float yaw = 0, bool fire = false)
        {
            var s = player.Snapshot.Value;
            bool wasFire=((PlayerInputFrame)typeof(PrototypePlayer).GetField("_lastInput",Private).GetValue(player)).Fire;
            ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(player)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(player, new PlayerInputFrame {
                Revision = s.Revision, HeroRevision = s.HeroRevision, Sequence = s.AcknowledgedInput + 1, Swim = swim, JumpSequence = jump, Move = move, Look = new Vector2(yaw,0), Fire = fire,
                FireSequence = s.ConsumedFire + (fire && !wasFire ? 1u : 0u), ReleaseSequence = s.ConsumedRelease + (!fire && wasFire ? 1u : 0u) });
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(player, player.NetworkManager.ServerTime.Time);
            player.Simulate(Dt, s.SimulatedAt + Dt, PrototypeMatch.Current.State.Value.Phase); Physics.SyncTransforms();
        }
        static void Reset(PrototypePlayer player, Vector3 feet, byte team)
        {
            var s = player.Snapshot.Value; s.Position = feet; s.Team = team; s.Yaw = s.BodyYaw = s.Pitch = 0;
            s.PlanarVelocity = s.Velocity = Vector3.zero; s.VerticalSpeed = 0; s.Health = s.Ink = 100; s.ProtectedUntil = 0;
            s.Swimming = s.CompactBody = false; s.SwimSource = s.AirSwimSource = SwimSurface.None; s.PaperPose = PaperPose.None; s.PaperCenter=Vector3.zero; s.PaperRotation=Quaternion.identity; s.Grounded = true;
            s.AirHumanOffset = s.CameraRebaseOffset = 0;
            s.ConsumedJump = 0; s.Movement = MovementMode.Human; s.SimulatedAt = Math.Max(s.SimulatedAt,player.NetworkManager.ServerTime.Time);
            Motor(player).Restore(s); player.Snapshot.Value = s;
        }
        static void Present(PrototypePlayer player)
        {
            var s = player.Snapshot.Value;
            player.CharacterView.Present(s, Dt, s.SimulatedAt);
            player.SwimBody.Present(s, Vector3.zero, Quaternion.Euler(0, s.BodyYaw, 0));
        }
        static void Capture(PrototypePlayer player, string name)
        {
            Present(player); var camera = Camera.main; var feet = player.Snapshot.Value.Position;
            camera.transform.position = feet + new Vector3(1.3f, 1.2f, -2.1f); camera.transform.LookAt(feet + Vector3.up * .22f);
            var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor")
                .GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
            type.GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { camera, name, Output });
        }
        static Vector3 Target(PrototypePlayer player)
        {
            var p=PaperBodyTests.BodyPoint(player.SwimBody); var s=player.Snapshot.Value;
            return s.PaperCenter+s.PaperRotation*new Vector3(p.x,p.y,0);
        }
        static InkProjectileService ShotAt(PrototypePlayer player, Vector3 target, Vector3 direction)
        {
            var service=new InkProjectileService(); double born=player.NetworkManager.ServerTime.Time;
            service.SpawnForMeasurement(new InkShot { Id=8001,HeroId=1,Team=(byte)(player.Snapshot.Value.Team==1?2:1),
                Shooter=ulong.MaxValue,Born=born,Origin=target-direction*1.5f,Velocity=direction*31 });
            service.Simulate(born+.09); return service;
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(()=>PrototypeApp.Current!=null && PrototypeApp.Current.Ready,"Bootstrap ready");
            ushort port; using(var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0))) port=(ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return PrototypeApp.Current.Connect(true,"127.0.0.1",port).ToCoroutine();
            yield return Wait(()=>PrototypePlayer.Local!=null && PrototypePlayer.Local.SwimBody!=null,"Addressables paper host");
            var player=PrototypePlayer.Local; var match=PrototypeMatch.Current;
            var platform=GameObject.CreatePrimitive(PrimitiveType.Cube); platform.name="Paper validation platform";
            platform.transform.position=new Vector3(0,5,0); platform.transform.localScale=new Vector3(30,1,30);
            Vector3 feet=new(0,5.54f,0); Physics.SyncTransforms();
            for(int hero=1;hero<=5;hero++)
            {
                match.enabled=true; player.RequestHeroChange(hero,HeroSelectionOrigin.Warmup);
                yield return Wait(()=>!player.HeroChangePending && player.Snapshot.Value.HeroId==hero,"Hero "+hero);
                match.enabled=false;
                foreach(byte team in new byte[]{1,2})
                {
                    Reset(player,feet,team); for(int i=0;i<12;i++) Tick(player,true,move:Vector2.up);
                    var s=player.Snapshot.Value; Present(player);
                    Assert.That(s.SwimSource,Is.EqualTo(SwimSurface.Neutral)); Assert.That(s.PlanarVelocity.magnitude,Is.EqualTo(3).Within(.002));
                    Assert.That(s.PaperPose,Is.EqualTo(PaperPose.Ground));
                    Assert.That(Vector3.Dot(s.PaperRotation*Vector3.forward,Vector3.up),Is.GreaterThan(.999));
                    Assert.That(player.SwimBody.Profile,Is.SameAs(player.Presentation.Paper));
                    Assert.That(player.SwimBody.FlatHitActive && player.SwimBody.BodyRenderer.enabled,Is.True);
                    Assert.That(player.CharacterView.GetComponentsInChildren<SkinnedMeshRenderer>().Any(r=>r.enabled),Is.False);
                    Assert.That(player.CharacterView.SwimEffect.isEmitting,Is.False);
                    if(team==1) Capture(player,"paper-ground-hero-"+hero);
                    var groundRotation=s.PaperRotation;
                    Tick(player,true,1,Vector2.up); for(int i=0;i<8;i++) Tick(player,true,1,Vector2.up);
                    s=player.Snapshot.Value; Present(player);
                    Assert.That(s.PaperPose,Is.EqualTo(PaperPose.Air)); Assert.That(s.HasInkRecovery,Is.False);
                    Assert.That(Quaternion.Angle(s.PaperRotation,groundRotation),Is.LessThan(.001));
                    Vector3 target=Target(player),normal=s.PaperRotation*Vector3.forward;
                    var aim=new TpsAimSolver();
                    Assert.That(aim.ClosestCast(target+normal,-normal,2,.005f,ulong.MaxValue,out var hit),Is.True);
                    Assert.That(hit.Collider,Is.SameAs(player.SwimBody.HitVolume));
                    var shot=ShotAt(player,target,-normal); Assert.That(shot.Impacts.Count(i=>i.Damage>0),Is.EqualTo(1));
                    float health=player.Snapshot.Value.Health; Assert.That(health,Is.LessThan(100));
                    shot.Simulate(player.NetworkManager.ServerTime.Time+1); Assert.That(player.Snapshot.Value.Health,Is.EqualTo(health));
                    Tick(player,false,1); Assert.That(player.Snapshot.Value.Swimming,Is.False);
                    Tick(player,true,1); Assert.That(player.Snapshot.Value.Swimming,Is.True);
                    Assert.That(Vector3.Dot(player.Snapshot.Value.PaperRotation*Vector3.forward,Vector3.up),Is.GreaterThan(.999f));
                }
            }
            // Live map ink still owns eligibility and resource recovery.
            var spawn=PrototypeArena.Current.SpawnPoints[0].position+Vector3.forward*2;
            Assert.That(Physics.Raycast(spawn+Vector3.up*.2f,Vector3.down,out var ground,1,PlayerMotorSimulation.WorldMask),Is.True);
            var painted=ground.collider.GetComponentInParent<PaintSurface>(); Assert.That(painted,Is.Not.Null);
            match.Paint(painted,ground.point,ground.normal,3,1,1,1,121);
            Reset(player,ground.point+Vector3.up*.04f,1);
            for(int i=0;i<8;i++) { Tick(player,true,move:Vector2.up); Present(player); yield return null; }
            Assert.That(player.Snapshot.Value.SwimSource,Is.EqualTo(SwimSurface.Friendly));
            Assert.That(player.SwimBody.FlatHitActive && player.SwimBody.BodyRenderer.enabled,Is.True);
            Assert.That(player.CharacterView.SwimEffect.isEmitting,Is.True); Capture(player,"paper-friendly-ground");
            for(int i=0;i<15;i++) Tick(player,true);
            Present(player); Assert.That(player.CharacterView.SwimEffect.isEmitting,Is.False);
            Tick(player,true,1); for(int i=0;i<8;i++) Tick(player,true,1); Present(player);
            Assert.That(player.Snapshot.Value.PaperPose,Is.EqualTo(PaperPose.Air));
            Assert.That(player.SwimBody.FlatHitActive,Is.True); Assert.That(player.CharacterView.SwimEffect.isEmitting,Is.False);
            // A known planar fixture exercises hit placement, repaint and mantle.
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name="Paper climb fixture"; wall.SetActive(false);
            wall.transform.position=new Vector3(0,7.5f,6); wall.transform.localScale=new Vector3(8,4,4);
            var surface=wall.AddComponent<PaintSurface>(); surface.SurfaceId=999;
            surface.PainterShader=painted.PainterShader; surface.DisplayShader=painted.DisplayShader; surface.ShapeAtlas=painted.ShapeAtlas;
            surface.WallRegions=new[]{new PaintRegion { Id=1,Origin=new Vector3(0,0,-.5f),Rotation=Quaternion.FromToRotation(Vector3.up,Vector3.back),Size=Vector2.one,Climbable=true }};
            surface.Scores=true; surface.WalkableSize=Vector2.one; surface.InitializeOwnership(.025f); wall.SetActive(true);
            foreach(var region in surface.WallRegions) for(int i=0;i<region.Grid.Cells.Length;i++) region.Grid.Set(i,1);
            Physics.SyncTransforms();
            for(int hero=1;hero<=5;hero++)
            {
                match.enabled=true; player.RequestHeroChange(hero,HeroSelectionOrigin.Warmup);
                yield return Wait(()=>!player.HeroChangePending && player.Snapshot.Value.HeroId==hero,"Wall hero "+hero);
                match.enabled=false; Reset(player,new Vector3(0,5.54f,1.5f),1);
                for(int i=0;i<160 && player.Snapshot.Value.Movement!=MovementMode.WallInk;i++) Tick(player,true,move:Vector2.up);
                Present(player);
                Assert.That(player.Snapshot.Value.Movement,Is.EqualTo(MovementMode.WallInk),"wall entry hero "+hero);
                var attached=player.Snapshot.Value;
                Assert.That(Vector3.Dot(attached.PaperRotation*Vector3.forward,Vector3.back),Is.GreaterThan(.999));
                Assert.That(Mathf.Abs(attached.PaperCenter.z-3.97f),Is.LessThan(.002));
                var camera=Camera.main; camera.transform.position=new Vector3(2,7.5f,1); camera.transform.LookAt(attached.PaperCenter);
                var renderer=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Splatoon.Editor").GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
                renderer.GetMethod("Render",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{camera,"paper-wall-hero-"+hero,Output});
                Tick(player,true,1); Assert.That(player.Snapshot.Value.PaperPose,Is.EqualTo(PaperPose.Air));
                Assert.That(Vector3.Dot(player.Snapshot.Value.PaperRotation*Vector3.forward,Vector3.up),Is.GreaterThan(.999f));
                Motor(player).Restore(attached); player.Snapshot.Value=attached;
                foreach(var region in surface.WallRegions) for(int i=0;i<region.Grid.Cells.Length;i++) region.Grid.Set(i,2);
                Tick(player,true,move:Vector2.up);
                Assert.That(player.Snapshot.Value.Movement,Is.Not.EqualTo(MovementMode.WallInk),"enemy repaint detaches paper; at the foot it may land immediately");
                Assert.That(player.Snapshot.Value.HasInkRecovery,Is.False);
                foreach(var region in surface.WallRegions) for(int i=0;i<region.Grid.Cells.Length;i++) region.Grid.Set(i,1);
                Motor(player).Restore(attached); player.Snapshot.Value=attached;
                bool mantle=false;
                for(int i=0;i<180;i++) { Tick(player,true,move:Vector2.up); mantle|=player.Snapshot.Value.Movement==MovementMode.Mantle; if(mantle&&player.Snapshot.Value.Grounded) break; }
                Assert.That(mantle,Is.True,"mantle hero "+hero); Assert.That(player.Snapshot.Value.Position.y,Is.GreaterThan(9.4f));
                Assert.That(player.Snapshot.Value.PaperPose,Is.EqualTo(PaperPose.Ground));
            }
            foreach(byte owner in new byte[]{0,2})
            {
                foreach(var region in surface.WallRegions) for(int i=0;i<region.Grid.Cells.Length;i++) region.Grid.Set(i,owner);
                Reset(player,new Vector3(0,5.54f,3.55f),1); Tick(player,true,move:Vector2.up);
                Assert.That(player.Snapshot.Value.Movement==MovementMode.WallInk,Is.EqualTo(owner==0),"neutral/enemy wall entry");
            }
            // The raised fixture overlaps the authored platform's standing space.
            // Natural traversal must run against the map's own geometry only.
            platform.SetActive(false); wall.SetActive(false); Physics.SyncTransforms();
            yield return NaturalTrainingGroundTraversal(player,match);
            yield return AirSwitchingInTrainingGround(player,match);
            platform.SetActive(true); wall.SetActive(true); Physics.SyncTransforms();
            Reset(player,feet,1); Tick(player,true);
            var ceiling=GameObject.CreatePrimitive(PrimitiveType.Cube); ceiling.name="Paper low ceiling";
            ceiling.transform.position=new Vector3(0,6.5f,0); ceiling.transform.localScale=new Vector3(3,.3f,3); Physics.SyncTransforms();
            Tick(player,false); Present(player);
            Assert.That(player.Snapshot.Value.CompactBody && player.SwimBody.BodyRenderer.enabled,Is.True,"blocked standing preserves paper");
            Assert.That(player.Snapshot.Value.HasInkRecovery,Is.False);
            UnityEngine.Object.Destroy(ceiling); yield return null;
            Tick(player,false); Assert.That(player.Snapshot.Value.CompactBody,Is.False);
            Tick(player,true);
            // Snapshot serialization and remote hit placement do not depend on interpolated root transforms.
            var authoritative=player.Snapshot.Value;
            using(var writer=new Unity.Netcode.FastBufferWriter(1024,Unity.Collections.Allocator.Temp))
            {
                writer.WriteNetworkSerializable(authoritative);
                using var reader=new Unity.Netcode.FastBufferReader(writer,Unity.Collections.Allocator.Temp);
                reader.ReadNetworkSerializable(out PlayerSnapshot remote);
                var cc=player.GetComponent<CharacterController>(); cc.enabled=false; player.transform.position+=Vector3.right*.5f;
                player.SwimBody.ApplyCollision(remote); Physics.SyncTransforms();
                Assert.That(player.SwimBody.HitVolume.transform.position,Is.EqualTo(remote.PaperCenter));
                player.transform.position=authoritative.Position; cc.enabled=true;
            }
            player.ReceiveDamage(2,1000); Present(player); Assert.That(player.SwimBody.UsesHitProxy||player.SwimBody.BodyRenderer.enabled,Is.False);
            player.Respawn(); Present(player); Assert.That(player.Snapshot.Value.PaperPose,Is.EqualTo(PaperPose.None));
            File.WriteAllText(Output+"/result.txt","PASS: actual Boot/Addressables/NGO host; five heroes and both teams; ground/wall/air/mantle; friendly/neutral rules; authoritative projectile damage; remote snapshot hit placement; death/respawn. Independent client and physical LAN are not asserted by this test.\n");
        }
        static IEnumerator NaturalTrainingGroundTraversal(PrototypePlayer player, PrototypeMatch match)
        {
            var arena=PrototypeArena.Current;
            Assert.That(arena.gameObject.scene.name,Is.EqualTo("TrainingGround"));
            var surface=arena.Surfaces.Values.Single(s=>s.name=="PlatformBody_1");
            using var report=new StreamWriter(Output+"/natural-traversal.txt");
            report.WriteLine("Actual TrainingGround, Boot/Addressables/NGO host, production Simulate at 60 Hz; start x=15.5, wall x=13 (2.5 m). Input is driven by the test.\nhero\tground\tapproach\tentryTicks\tclimbMetres\texit");
            for(int hero=1;hero<=5;hero++)
            {
                match.enabled=true; player.RequestHeroChange(hero,HeroSelectionOrigin.Warmup);
                yield return Wait(()=>!player.HeroChangePending && player.Snapshot.Value.HeroId==hero,"Natural traversal hero "+hero);
                match.enabled=false;
                foreach(byte owner in new byte[]{0,1}) foreach(float diagonal in new[]{0f,.4f})
                {
                    arena.ClearPaint();
                    match.Paint(surface,new Vector3(13,1.5f,0),Vector3.right,4,1,1,1,400+(uint)hero);
                    if(owner==1)
                    {
                        Assert.That(Physics.Raycast(new Vector3(14.5f,.2f,0),Vector3.down,out var ground,.5f,PlayerMotorSimulation.WorldMask),Is.True);
                        match.Paint(ground.collider.GetComponentInParent<PaintSurface>(),ground.point,ground.normal,3,1,1,1,500+(uint)hero);
                    }
                    Reset(player,new Vector3(15.5f,.04f,0),1);
                    Vector2 move=new Vector2(diagonal,1).normalized; int ticks=0;
                    for(;ticks<160 && player.Snapshot.Value.Movement!=MovementMode.WallInk;ticks++) Tick(player,true,move:move,yaw:270);
                    Assert.That(player.Snapshot.Value.Movement,Is.EqualTo(MovementMode.WallInk),$"natural entry hero {hero}, ground {owner}, diagonal {diagonal}");
                    float y=player.Snapshot.Value.Position.y;
                    for(int i=0;i<15;i++) Tick(player,true,move:Vector2.up,yaw:270+i*17);
                    float climbed=player.Snapshot.Value.Position.y-y;
                    Assert.That(climbed,Is.EqualTo(1).Within(.02f));
                    for(int i=0;i<50 && !player.Snapshot.Value.Grounded;i++) Tick(player,true,move:Vector2.down,yaw:270);
                    Assert.That(player.Snapshot.Value.Grounded,Is.True,"wall to ground");
                    Assert.That(player.Snapshot.Value.SwimSource,Is.EqualTo(owner==1 ? SwimSurface.Friendly : SwimSurface.Neutral));
                    Tick(player,true,move:Vector2.up,yaw:270);
                    Assert.That(player.Snapshot.Value.Movement,Is.EqualTo(MovementMode.WallInk),"reenter after descending");
                    if(diagonal==0)
                    {
                        Tick(player,true,1,Vector2.up,270);
                        Assert.That(player.Snapshot.Value.Movement,Is.EqualTo(MovementMode.Air));
                        Assert.That(player.Snapshot.Value.VerticalSpeed,Is.GreaterThan(0));
                    }
                    else
                    {
                        bool mantle=false;
                        for(int i=0;i<160;i++)
                        {
                            var before=player.Snapshot.Value;
                            Tick(player,true,move:Vector2.up,yaw:270); mantle|=player.Snapshot.Value.Movement==MovementMode.Mantle;
                            var after=player.Snapshot.Value;
                            if(after.Movement!=before.Movement)
                            {
                                report.WriteLine($"transition {i}: {before.Movement} {before.Position:F4} -> {after.Movement} {after.Position:F4}; target {after.MantleTo:F4}"); report.Flush();
                            }
                            if(mantle && player.Snapshot.Value.Grounded) break;
                        }
                        Assert.That(mantle && player.Snapshot.Value.Grounded,Is.True,$"natural mantle; entered={mantle}; state={player.Snapshot.Value.Movement}; position={player.Snapshot.Value.Position:F4}");
                        Assert.That(player.Snapshot.Value.Position.y,Is.GreaterThan(2.9f));
                        Assert.That(player.Snapshot.Value.PaperPose,Is.EqualTo(PaperPose.Ground));
                    }
                    report.WriteLine($"{hero}\t{owner}\t{diagonal}\t{ticks}\t{climbed:F4}\t{(diagonal==0 ? "jump" : "mantle")}"); report.Flush();
                    Present(player); yield return null;
                }
            }
        }
        static IEnumerator AirSwitchingInTrainingGround(PrototypePlayer player, PrototypeMatch match)
        {
            var arena=PrototypeArena.Current;
            Assert.That(arena.gameObject.scene.name,Is.EqualTo("TrainingGround"));
            using var report=new StreamWriter(Output+"/air-switching.txt");
            report.WriteLine("Boot/Addressables/NGO host, production Simulate, actual TrainingGround; hero / takeoff / source / repeated horizontal switches");
            for(int hero=1;hero<=5;hero++)
            {
                match.enabled=true; player.RequestHeroChange(hero,HeroSelectionOrigin.Warmup);
                yield return Wait(()=>!player.HeroChangePending && player.Snapshot.Value.HeroId==hero,"Air hero "+hero);
                match.enabled=false;
                for(int kind=0;kind<4;kind++)
                {
                    arena.ClearPaint();
                    var start=new Vector3(14.5f,.04f,12);
                    if(kind==2)
                    {
                        Assert.That(Physics.Raycast(start+Vector3.up*.2f,Vector3.down,out var hit,.5f,PlayerMotorSimulation.WorldMask),Is.True);
                        match.Paint(hit.collider.GetComponentInParent<PaintSurface>(),hit.point,hit.normal,3,1,1,1,800+(uint)hero);
                    }
                    Reset(player,kind==3 ? new Vector3(12.5f,3.04f,0) : start,1);
                    uint jump=kind==3 ? 0u : 1u;
                    if(kind==3)
                    {
                        // A fresh controller can briefly report ungrounded while
                        // settling. Require actual departure from the platform.
                        for(int i=0;i<80;i++)
                        {
                            Tick(player,false,move:Vector2.up,yaw:90);
                            var falling=player.Snapshot.Value;
                            if(!falling.Grounded && falling.Position.x>13.7f && falling.Position.y<2.98f) break;
                        }
                    }
                    else
                    {
                        for(int i=0;i<8;i++) Tick(player,kind!=0);
                        Tick(player,kind!=0,jump);
                    }
                    var source=kind==2 ? SwimSurface.Friendly : SwimSurface.Neutral;
                    Assert.That(player.Snapshot.Value.Grounded,Is.False);
                    Assert.That(player.Snapshot.Value.AirSwimSource,Is.EqualTo(source));
                    foreach(float yaw in new[]{35f,155f})
                    {
                        Tick(player,false,jump); Assert.That(player.Snapshot.Value.Swimming,Is.False);
                        Tick(player,true,jump,yaw:yaw); var opened=player.Snapshot.Value;
                        Assert.That(opened.Swimming && !opened.Grounded,Is.True,$"hero={hero} kind={kind} swim={opened.Swimming} grounded={opened.Grounded} pos={opened.Position:F3}");
                        Assert.That(opened.SwimSource,Is.EqualTo(source)); Assert.That(opened.HasInkRecovery,Is.False);
                        Assert.That(Quaternion.Angle(opened.PaperRotation,PaperPoseSimulation.GroundRotation(Vector3.up,yaw)),Is.LessThan(.001f));
                        Tick(player,true,jump,yaw:yaw+70); Present(player);
                        Assert.That(Quaternion.Angle(player.Snapshot.Value.PaperRotation,opened.PaperRotation),Is.LessThan(.001f));
                        Assert.That(player.SwimBody.HitVolume.transform.position,Is.EqualTo(player.Snapshot.Value.PaperCenter));
                        Assert.That(player.SwimBody.FlatHitActive && player.SwimBody.BodyRenderer.enabled,Is.True);
                    }
                    report.WriteLine($"{hero} / {kind} / {source} / PASS"); report.Flush();
                }
                // A long unobstructed fall gives every weapon time to emerge,
                // fire (charge weapons release), and return to paper before landing.
                Reset(player,new Vector3(14.5f,20,12),1);
                var airborne=player.Snapshot.Value; airborne.Grounded=false; airborne.Movement=MovementMode.Air;
                // Hero setup temporarily runs the real match clock, whereas
                // these fixtures fast-forward Simulate. Start this isolated shot
                // after any cooldown retained from the preceding hero scenario.
                airborne.SimulatedAt=Math.Max(airborne.SimulatedAt,Math.Max(airborne.NextShotAt,airborne.WeaponReadyAt));
                Motor(player).Restore(airborne); player.Snapshot.Value=airborne;
                Tick(player,true); uint shots=player.Snapshot.Value.ShotSequence;
                for(int i=0;i<12;i++) Tick(player,false,fire:true);
                for(int i=0;i<35 && player.Snapshot.Value.ShotSequence==shots;i++) Tick(player,false);
                Assert.That(player.Snapshot.Value.ShotSequence,Is.GreaterThan(shots),$"air shot hero {hero}; phase={player.Snapshot.Value.WeaponPhase}; now={player.Snapshot.Value.SimulatedAt}; next={player.Snapshot.Value.NextShotAt}; ready={player.Snapshot.Value.WeaponReadyAt}");
                for(int i=0;i<20 && !player.Snapshot.Value.Swimming;i++) Tick(player,true);
                Assert.That(player.Snapshot.Value.Swimming && !player.Snapshot.Value.Grounded,Is.True,"air swim after shot hero "+hero);
                report.WriteLine($"{hero} / airborne fire and reentry / PASS"); report.Flush();
                yield return null;
            }
        }
    }
}
#endif
