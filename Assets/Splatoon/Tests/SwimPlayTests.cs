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
        const string Output = "Reports/SwimAdjustment/PlayMode";
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
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        static PlayerMotorSimulation Motor(PrototypePlayer player) => (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(player);
        static void Tick(PrototypePlayer player, bool swim, uint jump = 0, Vector2 move = default)
        {
            var s = player.Snapshot.Value;
            ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(player)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(player, new PlayerInputFrame {
                Revision = s.Revision, HeroRevision = s.HeroRevision, Sequence = s.AcknowledgedInput + 1, Swim = swim, JumpSequence = jump, Move = move });
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(player, player.NetworkManager.ServerTime.Time);
            player.Simulate(Dt, s.SimulatedAt + Dt, PrototypeMatch.Current.State.Value.Phase); Physics.SyncTransforms();
        }
        static void Reset(PrototypePlayer player, Vector3 feet, byte team)
        {
            var s = player.Snapshot.Value; s.Position = feet; s.Team = team; s.Yaw = s.BodyYaw = s.Pitch = 0;
            s.PlanarVelocity = s.Velocity = Vector3.zero; s.VerticalSpeed = 0; s.Health = s.Ink = 100; s.ProtectedUntil = 0;
            s.Swimming = s.CompactBody = false; s.SwimSource = SwimSurface.None; s.Grounded = true;
            s.ConsumedJump = 0; s.Movement = MovementMode.Human; s.SimulatedAt = player.NetworkManager.ServerTime.Time;
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
        static InkProjectileService ShotAt(PrototypePlayer player, float height, Vector3 direction = default)
        {
            if (direction == Vector3.zero) direction = Vector3.forward;
            var service = new InkProjectileService(); double born = player.NetworkManager.ServerTime.Time;
            service.SpawnForMeasurement(new InkShot { Id = 8001, HeroId = 1, Team = (byte)(player.Snapshot.Value.Team == 1 ? 2 : 1),
                Shooter = ulong.MaxValue, Born = born, Origin = player.Snapshot.Value.Position + Vector3.up * height - direction * 2,
                Velocity = direction * 31 });
            service.Simulate(born + .15); return service;
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap ready");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current; yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypePlayer.Local.SwimBody != null, "Addressables host includes shared swim body");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            var platform = GameObject.CreatePrimitive(PrimitiveType.Cube); platform.name = "Swim validation neutral platform";
            platform.transform.position = new Vector3(0, 5, 0); platform.transform.localScale = new Vector3(30, 1, 30);
            var trigger = GameObject.CreatePrimitive(PrimitiveType.Cube); trigger.name = "Ignored ordinary trigger";
            trigger.transform.position = new Vector3(0, 6, -1); trigger.GetComponent<BoxCollider>().isTrigger = true;
            trigger.GetComponent<Renderer>().enabled = false;
            Vector3 feet = new(0, 5.54f, 0); Physics.SyncTransforms();
            for (int hero = 1; hero <= 5; hero++)
            {
                match.enabled = true; player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Hero " + hero);
                match.enabled = false;
                var shared = player.SwimBody;
                foreach (byte team in new byte[] { 1, 2 })
                {
                    Reset(player, feet, team); for (int i = 0; i < 12; i++) Tick(player, true, move: Vector2.up);
                    var state = player.Snapshot.Value; Present(player);
                    Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.Neutral)); Assert.That(state.PlanarVelocity.magnitude, Is.EqualTo(3).Within(.002));
                    Assert.That(shared.BodyRenderer.enabled && shared.FlatHitActive, Is.True);
                    Assert.That(player.CharacterView.GetComponentsInChildren<SkinnedMeshRenderer>().Any(r => r.enabled), Is.False);
                    Assert.That(player.CharacterView.SwimEffect.isEmitting, Is.False);
                    var color = new MaterialPropertyBlock(); shared.BodyRenderer.GetPropertyBlock(color);
                    Assert.That(Vector4.Distance(color.GetColor("_BaseColor"), PrototypeArena.TeamColor(team)), Is.LessThan(.0001f));
                    if (hero == 1) Capture(player, "neutral-team-" + team);
                    Tick(player, true, 1, Vector2.up); for (int i = 0; i < 8; i++) Tick(player, true, 1, Vector2.up);
                    state = player.Snapshot.Value; Present(player);
                    Assert.That(state.Swimming && !state.Grounded && shared.BodyRenderer.enabled, Is.True);
                    if (hero == 1 && team == 1) Capture(player, "neutral-jump");
                    // Both the camera aim query and swept projectile must choose the compact volume.
                    var solver = new TpsAimSolver(); var from = state.Position + Vector3.up * .2f + Vector3.back * 2;
                    Assert.That(solver.ClosestCast(from, Vector3.forward, 4, .025f, ulong.MaxValue, out var hit), Is.True);
                    Assert.That(hit.Collider, Is.SameAs(shared.HitVolume));
                    var above = ShotAt(player, .6f); Assert.That(above.Impacts.Any(i => i.Damage > 0), Is.False);
                    var service = ShotAt(player, .2f); Assert.That(service.Impacts.Count(i => i.Damage > 0), Is.EqualTo(1));
                    float health = player.Snapshot.Value.Health; Assert.That(health, Is.LessThan(100));
                    service.Simulate(player.NetworkManager.ServerTime.Time + 1); Assert.That(player.Snapshot.Value.Health, Is.EqualTo(health));
                    Tick(player, false, 1); Assert.That(player.Snapshot.Value.Swimming, Is.False);
                    Tick(player, true, 1); Assert.That(player.Snapshot.Value.Swimming, Is.False);
                }
                Assert.That(player.SwimBody, Is.SameAs(shared));
            }
            // Exercise actual painted map contact and the existing particle system, then jump from it.
            var spawn = PrototypeArena.Current.SpawnPoints[0].position + Vector3.forward;
            Assert.That(Physics.Raycast(spawn + Vector3.up * .2f, Vector3.down, out var ground, 1, PlayerMotorSimulation.WorldMask), Is.True);
            var painted = ground.collider.GetComponentInParent<PaintSurface>(); Assert.That(painted, Is.Not.Null);
            match.Paint(painted, ground.point, ground.normal, 3, 1, 1, 1, 121);
            Reset(player, ground.point + Vector3.up * .04f, 1);
            for (int i = 0; i < 10; i++) { Tick(player, true, move: Vector2.up); Present(player); yield return null; }
            Assert.That(player.Snapshot.Value.SwimSource, Is.EqualTo(SwimSurface.Friendly));
            Assert.That(player.CharacterView.SwimEffect.isEmitting, Is.True);
            Assert.That(player.SwimBody.BodyRenderer.enabled, Is.False);
            Capture(player, "friendly-moving");
            for (int i = 0; i < 15; i++) Tick(player, true);
            Present(player); Assert.That(player.CharacterView.SwimEffect.isEmitting, Is.False);
            Tick(player, true, 1); for (int i = 0; i < 8; i++) Tick(player, true, 1);
            var friendly = player.Snapshot.Value; Present(player);
            Assert.That(friendly.Swimming && !friendly.Grounded, Is.True);
            Assert.That(friendly.SwimSource, Is.EqualTo(SwimSurface.Friendly));
            Assert.That(player.CharacterView.SwimEffect.isEmitting, Is.False);
            Assert.That(player.SwimBody.BodyRenderer.enabled || player.SwimBody.FlatHitActive, Is.False);
            var aim = new TpsAimSolver(); Vector3 shotDirection = Vector3.zero;
            var friendlyController = player.GetComponent<CharacterController>();
            var diagnostics = new List<string> { $"position={friendly.Position:F4} root={player.transform.position:F4} health={friendly.Health} enabled={friendlyController.enabled} height={friendlyController.height} center={friendlyController.center:F4} bounds={friendlyController.bounds} flat={player.SwimBody.FlatHitActive} trigger={friendlyController.isTrigger} valid={TpsAimSolver.Valid(friendlyController, ulong.MaxValue)} owner={player.OwnerClientId}" };
            foreach (var direction in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
            {
                var from = friendly.Position + Vector3.up * .5f - direction * 2;
                bool found = aim.ClosestCast(from, direction, 4, .025f, ulong.MaxValue, out var candidate);
                diagnostics.Add($"direction={direction} hit={(found ? candidate.Collider.name : "none")} distance={candidate.Distance} point={candidate.Point:F4}");
                diagnostics.Add($"direct={friendlyController.Raycast(new Ray(from, direction), out var directHit, 4)} point={directHit.point:F4}");
                if (found &&
                    candidate.Collider.GetComponentInParent<PrototypePlayer>() == player) { shotDirection = direction; break; }
            }
            diagnostics.Add("nearby=" + string.Join(",", Physics.OverlapSphere(friendly.Position + Vector3.up * .35f, 1, ~0, QueryTriggerInteraction.Collide).Select(c => c.name + ":" + c.GetType().Name)));
            File.WriteAllLines(Output + "/friendly-hit-diagnostics.txt", diagnostics);
            Assert.That(shotDirection, Is.Not.EqualTo(Vector3.zero), "A shot must have an unobstructed path past the spawn shield");
            Assert.That(ShotAt(player, .5f, shotDirection).Impacts.Any(i => i.Damage > 0), Is.True, "Invisible friendly form remains hittable");
            // Remote aim proxy preserves that same shape while remote movement is disabled.
            var controller = player.GetComponent<CharacterController>(); controller.enabled = false; player.SwimBody.ApplyCollision(friendly); Physics.SyncTransforms();
            var origin = friendly.Position + Vector3.up * .5f - shotDirection * 2;
            Assert.That(aim.ClosestCast(origin, shotDirection, 4, 0, ulong.MaxValue, out var remote), Is.True);
            Assert.That(remote.Collider, Is.SameAs(player.SwimBody.CapsuleHitVolume)); controller.enabled = true;
            player.ReceiveDamage(2, 1000); Present(player);
            Assert.That(player.SwimBody.FlatHitActive || player.SwimBody.CapsuleHitVolume.enabled || player.SwimBody.BodyRenderer.enabled, Is.False);
            Assert.That(player.CharacterView.Animator.enabled, Is.True);
            player.Respawn(); Present(player);
            Assert.That(player.Snapshot.Value.Swimming || player.Snapshot.Value.CompactBody, Is.False);
            Assert.That(player.SwimBody.BodyRenderer.enabled, Is.False);
            File.WriteAllText(Output + "/result.txt", "PASS: real Addressables + NGO host; five heroes and two team colors; neutral movement and airborne model; aimed and swept single damage; actual friendly painted map movement/idle particles and invisible jump hit volume; remote aim proxy; death/respawn. Physical LAN and independent remote-client playback remain separate acceptance.\n");
        }
    }
}
#endif
