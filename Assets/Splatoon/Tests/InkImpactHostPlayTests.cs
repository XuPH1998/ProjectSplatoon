#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Combat;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class InkImpactHostPlayTests
    {
        const string Output = "Reports/InkImpact/HostPlayMode";
        static IEnumerator Wait(Func<bool> predicate, string message, double seconds = 20)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(predicate(), Is.True, message);
        }

        [UnityTest] public IEnumerator RealMapProjectileRpcPaintAndCosmeticIsolation()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }

        // Construct captured waits only after the EnterPlayMode domain reload.
        static IEnumerator Scenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current; yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null && PrototypeMatch.Current != null &&
                PrototypePlayer.Local.CharacterView != null && Camera.main != null, "Host, hero content and camera");
            yield return null;
            Directory.CreateDirectory(Output);
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            Assert.That(match.NetworkManager, Is.Not.Null, "Spawned match network manager");
            var presentation = InkPresentation.Current;
            var surface = UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)
                .First(s => s.GetComponent<Collider>() != null && s.GetComponent<Collider>().bounds.size.y > 2 &&
                    s.GetComponent<Collider>().bounds.size.x > 5 && s.GetComponent<Collider>().bounds.size.z < 1);
            var collider = surface.GetComponent<Collider>(); var center = collider.bounds.center;
            Assert.That(collider.Raycast(new Ray(center + Vector3.forward * 3, Vector3.back), out var hit, 5), Is.True);
            var camera = new GameObject("Real map impact capture").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1; camera.fieldOfView = 42;
            camera.transform.position = hit.point + hit.normal * 3.2f + Vector3.up * .3f + Vector3.right * .7f;
            camera.transform.LookAt(hit.point);
            for (int hero = 1; hero <= 5; hero++)
            {
                presentation.Clear();
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Select hero " + hero);
                var state = player.Snapshot.Value;
                var shot = new InkShot { Id = (uint)(10000 + hero), Round = match.State.Value.Round, Seed = (uint)(900 + hero),
                    HeroId = hero, Team = state.Team, Shooter = player.OwnerClientId, Born = match.NetworkManager.ServerTime.Time,
                    Origin = hit.point + hit.normal * .8f, Velocity = -hit.normal * 20, Lifecycle = state.Revision,
                    HeroRevision = state.HeroRevision, ActionId = (ulong)(10000 + hero), ShotSequence = state.ShotSequence + 1 };
                uint beforePaint = match.AppliedPaintSequence;
                match.Projectiles.SpawnForMeasurement(shot);
                yield return Wait(() => presentation.GetComponentsInChildren<InkImpactEffect>().Any(e => e.IsAlive), "Authoritative impact RPC starts layered effect", 3);
                yield return Wait(() => match.AppliedPaintSequence > beforePaint, "Authoritative impact paints the real map", 3);
                var active = presentation.GetComponentsInChildren<InkImpactEffect>().First(e => e.IsAlive);
                Assert.That(active.Streaks.particleCount + active.Droplets.particleCount, Is.GreaterThan(0));
                var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor").GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
                type.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { camera, "hero-" + hero, Output });
                uint painted = match.Arena.OwnershipHash(); float health = player.Snapshot.Value.Health;
                for (uint i = 0; i < 16; i++) presentation.Impact(new InkImpact { Id = 20000 + i, Round = shot.Round,
                    Position = hit.point, Normal = hit.normal, Team = (byte)(i % 2 + 1), Hit = true });
                Assert.That(match.Arena.OwnershipHash(), Is.EqualTo(painted), "Visual splashes cannot expand ink ownership");
                Assert.That(player.Snapshot.Value.Health, Is.EqualTo(health), "Visual splashes cannot deal damage");
            }
            UnityEngine.Object.Destroy(camera.gameObject);
            presentation.Clear();
            yield return app.Leave().ToCoroutine();
            yield return Wait(() => InkPresentation.Current == null, "Leaving unloads the pooled effects");
            File.WriteAllText(Output + "/result.txt", "PASS: real Boot/TrainingGround host, five heroes, authoritative projectile -> impact RPC -> layered particles + paint; cosmetic-only impacts preserve ownership and health; room leave cleans effects. Physical remote client not tested.");
        }
    }
}
#endif
