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
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class BubblePlayTests
    {
        const string Output = "Reports/BubbleGirl/PlayMode";
        static IEnumerator Wait(Func<bool> predicate, string message, double seconds = 35)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(predicate(), Is.True, message);
        }
        [UnityTest] public IEnumerator RealHostHeroDamageBouncePaintAndVisuals()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponConfigAsset", new[] { "Assets/GameResource/Weapons" }))
            {
                var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(AssetDatabase.GUIDToAssetPath(guid));
                try { WeaponConfigValidation.Validate(asset.Snapshot()); }
                catch (Exception e) { Assert.Fail(asset.name + ": " + e.Message + "\n" + JsonUtility.ToJson(asset) + "\n" + JsonUtility.ToJson(asset.ammoConfig)); }
            }
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap all seven heroes");
            ushort port; using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current; yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null && Camera.main != null, "Host and presentation");
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            host.RequestHeroChange(7, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !host.HeroChangePending && host.Snapshot.Value.HeroId == 7, "Select BubbleGirl");
            yield return null; app.CaptureMouse(false);
            Assert.That(app.Heroes.All.Count(), Is.EqualTo(7)); Assert.That(host.CharacterView.Profile.SingleShot, Is.True);
            Assert.That(host.SwimBody.Profile, Is.Not.Null); Assert.That(host.CharacterView.Weapon.parent, Is.SameAs(host.CharacterView.WeaponSocket));
            var camera = new GameObject("Bubble validation camera").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1; camera.fieldOfView = 40;
            camera.transform.position = host.transform.position + new Vector3(2.6f, 1.4f, 2.6f);
            camera.transform.LookAt(host.transform.position + Vector3.up * 1.05f); Capture(camera, "hero-standing");
            var botPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var victim = match.AddTestBot(botPrefab); Assert.That(victim, Is.Not.Null);
            match.enabled = false; host.enabled = victim.enabled = false;
            var target = victim.Snapshot.Value; target.Team = (byte)(host.Snapshot.Value.Team == 1 ? 2 : 1);
            target.Health = 100; target.ProtectedUntil = 0; target.Position = new Vector3(1000, 1, 1000); target.Swimming = false;
            victim.Snapshot.Value = target; victim.transform.position = target.Position; Physics.SyncTransforms();
            uint id = 90000; var health = new List<float>();
            var friendly = target; friendly.Team = host.Snapshot.Value.Team; victim.Snapshot.Value = friendly;
            var passThrough = MakeShot(host, match, ++id, target.Position + Vector3.up * .85f + Vector3.back * 1.2f, Vector3.forward * 14);
            match.Projectiles.SpawnForMeasurement(passThrough); match.Projectiles.Simulate(passThrough.Born + .13);
            Assert.That(victim.Snapshot.Value.Health, Is.EqualTo(100)); Assert.That(match.Projectiles.ActiveCount, Is.EqualTo(1));
            Assert.That(match.Projectiles.Impacts, Is.Empty, "Friendly bodies cannot consume bubbles");
            match.Projectiles.Clear(); victim.Snapshot.Value = target;
            passThrough.Id = ++id; passThrough.Shooter = victim.PlayerId;
            match.Projectiles.SpawnForMeasurement(passThrough); match.Projectiles.Simulate(passThrough.Born + .13);
            Assert.That(match.Projectiles.ActiveCount, Is.EqualTo(1), "Shooter body cannot consume its own bubble");
            match.Projectiles.Clear();
            for (int i = 0; i < 4; i++)
            {
                var shot = MakeShot(host, match, ++id, target.Position + Vector3.up * .85f + Vector3.back * 1.2f, Vector3.forward * 14);
                match.Projectiles.SpawnForMeasurement(shot); match.Projectiles.Simulate(shot.Born + .13);
                health.Add(victim.Snapshot.Value.Health);
            }
            Assert.That(health, Is.EqualTo(new[] { 70f, 40f, 10f, 0f }));
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
            var surface = UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)
                .First(s => s.GetComponent<Collider>() != null && s.GetComponent<Collider>().bounds.size.y > 2 && s.GetComponent<Collider>().bounds.size.x > 5 && s.GetComponent<Collider>().bounds.size.z < 1);
            var wall = surface.GetComponent<Collider>(); var center = wall.bounds.center;
            Assert.That(wall.Raycast(new Ray(center + Vector3.forward * 3, Vector3.back), out var hit, 5), Is.True);
            var paint = new List<PaintStamp>(); match.Projectiles.PaintObserved = paint.Add;
            var bounceShot = MakeShot(host, match, ++id, hit.point + hit.normal * .9f, -hit.normal * 14);
            match.Projectiles.SpawnForMeasurement(bounceShot); match.Projectiles.Simulate(bounceShot.Born + .1);
            Assert.That(match.Projectiles.Bounces.Count, Is.EqualTo(1)); Assert.That(paint.Count, Is.GreaterThan(0));
            Assert.That(match.Projectiles.ActiveCount, Is.EqualTo(1), "Wall contact must not terminate the bubble");
            uint paintSequence = match.PaintSequence;
            yield return Wait(() => InkPresentation.Current.GetComponentsInChildren<Renderer>().Any(r => r.gameObject.name.StartsWith("BubbleInk") && r.gameObject.activeInHierarchy), "Host RPC creates pooled bubble mesh", 3);
            camera.transform.position = hit.point + hit.normal * 5 + Vector3.up * .6f + Vector3.right * 2;
            camera.transform.LookAt(hit.point + hit.normal * .5f);
            InkPresentation.Current.UpdateFlights(match.Projectiles.Bounces.Count > 0 ? match.Projectiles.Bounces[0].Time + .14 : bounceShot.Born + .19, camera);
            Capture(camera, "wall-bounce");
            Assert.That(match.PaintSequence, Is.EqualTo(paintSequence), "Rendering cannot add gameplay ink");
            match.Projectiles.Clear(); InkPresentation.Current.Clear(); match.Projectiles.PaintObserved = null;
            // Four simultaneous mesh bodies with a shared physical timeline, in a clear view above the arena.
            InkPresentation.Current.ClearFlights(); // This isolated capture intentionally starts before now.
            double born = match.NetworkManager.ServerTime.Time;
            Vector3 origin = host.transform.position + Vector3.up * 6;
            for (int i = 0; i < 4; i++)
            {
                var shot = MakeShot(host, match, ++id, origin, Vector3.forward * 14); shot.Born = born - .28 + i * .05;
                InkPresentation.Current.Spawn(shot);
            }
            InkPresentation.Current.UpdateFlights(born + .1, Camera.main);
            camera.transform.position = origin + new Vector3(5, 1, 2.87f); camera.transform.LookAt(origin + Vector3.forward * 2.87f + Vector3.down * .45f);
            Capture(camera, "four-bubbles");
            Assert.That(InkPresentation.Current.ActiveShots, Is.EqualTo(4));
            var before = match.Arena.OwnershipHash();
            InkPresentation.Current.Clear(); Assert.That(match.Arena.OwnershipHash(), Is.EqualTo(before));
            UnityEngine.Object.Destroy(camera.gameObject);
            match.enabled = true; host.enabled = true;
            yield return app.Leave().ToCoroutine();
            File.WriteAllText(Output + "/result.txt", "PASS: seven heroes load; BubbleGirl select, weapon grip and paper binding; authoritative projectile damage 70/40/10/0; wall reflection retains bubble and paints the real map; Host RPC mesh; four-body GPU captures; visual clearing preserves paint. Independent remote machines and target-device performance are separate acceptance gates.");
        }
        static InkShot MakeShot(PrototypePlayer host, PrototypeMatch match, uint id, Vector3 origin, Vector3 velocity)
        {
            var state = host.Snapshot.Value;
            return new InkShot { Id = id, Round = match.State.Value.Round, Seed = id + 7, HeroId = 7, Team = state.Team, Shooter = host.PlayerId,
                Born = match.NetworkManager.ServerTime.Time, Origin = origin, Velocity = velocity, Lifecycle = state.Revision, HeroRevision = state.HeroRevision,
                ActionId = ((ulong)id << 32) | 1, Configuration = GameplayConfig.GetWeapon(7) };
        }
        static void Capture(Camera camera, string label)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor").GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
            type.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { camera, label, Output });
        }
    }
}
#endif
