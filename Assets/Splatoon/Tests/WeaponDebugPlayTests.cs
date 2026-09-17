#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class WeaponDebugPlayTests
    {
        const string Output = "Reports/WeaponAssets/PlayMode";
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double end = Time.realtimeSinceStartupAsDouble + 30;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, message);
        }
        [UnityTest] public IEnumerator DebugRoomReloadAndNormalRoomIsolation()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator Applied(PrototypeApp app, Func<bool> applied)
        {
            yield return Wait(() => { app.ApplyDebugWeaponChanges(); return applied(); }, "Apply valid reload: " + app.WeaponDebugStatus);
        }
        // Unity's batch runner does not render Game View IMGUI. A normal editor test
        // run additionally captures the real Game View target, including the HUD.
        static IEnumerator CaptureGameView(string name, int width, int height, float fov)
        {
            if (Application.isBatchMode) yield break;
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var type = assembly.GetType("UnityEditor.GameView");
            var view = EditorWindow.GetWindow(type);
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
            var sizes = sizesType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
            var getGroup = sizesType.GetMethod("GetGroup");
            var group = getGroup.Invoke(sizes, new[] { Enum.ToObject(getGroup.GetParameters()[0].ParameterType, 0) });
            var sizeType = assembly.GetType("UnityEditor.GameViewSize");
            var kindType = assembly.GetType("UnityEditor.GameViewSizeType");
            var size = Activator.CreateInstance(sizeType, Enum.ToObject(kindType, 1), width, height, "Weapon validation");
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            int count = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            type.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, count - 1);
            Camera.main.fieldOfView = fov;
            for (int i = 0; i < 5; i++) { view.Repaint(); yield return null; }
            RenderTexture target = null;
            for (var scan = type; scan != null && target == null; scan = scan.BaseType)
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (var field in scan.GetFields(flags).Where(f => typeof(RenderTexture).IsAssignableFrom(f.FieldType)))
                    target ??= field.GetValue(view) as RenderTexture;
                foreach (var property in scan.GetProperties(flags).Where(p => typeof(RenderTexture).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0))
                    target ??= property.GetValue(view) as RenderTexture;
            }
            if (target == null) File.WriteAllText(Output + "/gameview-members.txt", string.Join("\n", type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(m => m.ToString())));
            Assert.That(target, Is.Not.Null, "Game View render target");
            var previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    var pixels = image.GetPixels32(); var row = new Color32[target.width];
                    for (int y = 0; y < target.height / 2; y++)
                    {
                        int a = y * target.width, b = (target.height - 1 - y) * target.width;
                        Array.Copy(pixels, a, row, 0, row.Length); Array.Copy(pixels, b, pixels, a, row.Length); Array.Copy(row, 0, pixels, b, row.Length);
                    }
                    image.SetPixels32(pixels);
                }
                image.Apply();
                File.WriteAllBytes(Output + "/" + name + ".png", image.EncodeToPNG());
                File.AppendAllText(Output + "/screenshots.txt", $"{name}: {target.width}x{target.height}, vertical FOV {fov}\n");
                Assert.That(target.width, Is.EqualTo(width)); Assert.That(target.height, Is.EqualTo(height));
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(image); }
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot assets");
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error); Assert.That(app.IsWeaponDebugRoom, Is.True); Assert.That(app.RoomCode, Is.Empty);
            yield return Wait(() => PrototypePlayer.Local != null, "Local host");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            foreach (int id in Enumerable.Range(1, 6))
            {
                player.RequestHeroChange(id, HeroSelectionOrigin.Warmup);
                yield return Wait(() => player.Snapshot.Value.HeroId == id && !player.HeroChangePending, "Hero " + id);
            }
            match.StartRound(); Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Practice));
            var signature = app.Manager.NetworkConfig.ConnectionData;
            var response = new NetworkManager.ConnectionApprovalResponse();
            app.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 123, Payload = signature }, response);
            Assert.That(response.Approved, Is.False, "Remote connection with correct content is still rejected");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            Assert.That(match.AddTestBot(prefab), Is.Not.Null); match.ClearTestBots();
            Assert.That(match.Players.Count(p => p != null && p.IsSpawned && p.IsTestBot), Is.Zero);
            app.CaptureMouse(false); match.enabled = false; player.enabled = false;
            var asset = WeaponConfigService.Current.Source(6);
            var savedAsset = UnityEngine.Object.Instantiate(asset); savedAsset.name = asset.name;
            bool originallyDirty = EditorUtility.IsDirty(asset);
            var original = GameplayConfig.GetWeapon(6);
            try
            {
                // Freeze the host driver while real Update observes the edited asset. Each pending
                // transaction is applied at the same boundary used by Match.FixedUpdate.
                foreach (int until in new[] { 60, 174, 61 })
                {
                    var w = GameplayConfig.GetWeapon(6);
                    var s = player.Snapshot.Value;
                    s.Ink = 100; s.Health = 100; s.AttackNeedsRelease = false;
                    WeaponSimulation.Cancel(ref s, default); s.NextShotAt = s.BurstReadyAt = s.WeaponReadyAt = 0;
                    s.SpreadInitialized = false; s.ConsumedFire = 0;
                    for (int t = 0; t <= until; t++) WeaponSimulation.Step(ref s, new PlayerInputFrame { FireSequence = 1, Sequence = (uint)t + 1, Fire = t < 150 }, w, t / 60.0, false, true);
                    Assert.That(s.SplatlingReservedInk, Is.GreaterThan(0));
                    float expected = s.Ink + s.SplatlingReservedInk;
                    player.Snapshot.Value = s;
                    // Exercise the same SerializedProperty path as the Chinese Inspector.
                    using var edited = new SerializedObject(asset);
                    edited.FindProperty("fireRate").floatValue = until == 60 ? 12 : 15;
                    edited.FindProperty("shotInk").floatValue += .1f;
                    edited.FindProperty("splatlingFullShootSeconds").doubleValue += .337123456789;
                    if (until == 61) edited.FindProperty("fireMode").intValue = (int)WeaponFireMode.Automatic;
                    edited.ApplyModifiedProperties();
                    uint revision = WeaponConfigService.Current.Revision(6);
                    yield return Applied(app, () => WeaponConfigService.Current.Revision(6) > revision);
                    var after = player.Snapshot.Value;
                    Assert.That(after.Ink, Is.EqualTo(expected).Within(.0002)); Assert.That(after.SplatlingReservedInk, Is.Zero);
                    Assert.That(after.AttackNeedsRelease, Is.True); Assert.That(after.SplatlingRemaining, Is.Zero);
                    for (int t = 1; t <= 5; t++) Assert.That(WeaponSimulation.Step(ref after, new PlayerInputFrame { Fire = true, FireSequence = 1 }, GameplayConfig.GetWeapon(6), after.SimulatedAt + t / 60.0, false, true), Is.False);
                    WeaponSimulation.Step(ref after, default, GameplayConfig.GetWeapon(6), 10, false, true);
                    WeaponSimulation.Step(ref after, new PlayerInputFrame { Fire = true, FireSequence = 2 }, GameplayConfig.GetWeapon(6), 10.1, false, true);
                    Assert.That(after.WeaponPhase, Is.EqualTo(until == 61 ? WeaponPhase.Firing : WeaponPhase.Charging));
                }
                var before = GameplayConfig.GetWeapon(6); var visual = player.CharacterView;
                uint oldRevision = WeaponConfigService.Current.Revision(6);
                var state = player.Snapshot.Value; state.SpreadProgress = .5f; state.DualiesGroundBias = Mathf.Lerp(GameplayConfig.GetWeapon(6).ReferenceBiasMin,GameplayConfig.GetWeapon(6).ReferenceBiasMax,.5f); player.Snapshot.Value = state;
                var inFlight = new InkShot { HeroId = 6, Configuration = before, ConfigurationRevision = oldRevision, Born = 0,
                    Origin = new Vector3(5000, 100, 5000), Velocity = Vector3.forward * 100, Team = 1, Id = 9000, Seed = 19 };
                Vector3 observed = default; match.Projectiles.Clear(); match.Projectiles.TraceObserved = (_, _, p) => observed = p;
                match.Projectiles.SpawnForMeasurement(inFlight);
                asset.spreadDegrees = 4; asset.splatlingPitchSpread = 3; asset.spreadExpandSeconds = 2; asset.projectileGravity = 0; asset.referenceBrakeGravity = 0; asset.damage += 5;
                yield return Applied(app, () => WeaponConfigService.Current.Revision(6) > oldRevision);
                Assert.That(player.CharacterView, Is.SameAs(visual), "Numeric edits preserve the live character assembly");
                Assert.That(player.Snapshot.Value.SpreadProgress, Is.EqualTo(.5f).Within(.00001)); Assert.That(player.Snapshot.Value.CurrentSpread, Is.EqualTo(4));
                match.Projectiles.Simulate(.5);
                Assert.That(observed.y, Is.LessThan(100), "In-flight projectile retains gravity");
                Assert.That(WeaponConfigService.Current.ForShot(6, oldRevision), Is.SameAs(before));
                var newer = inFlight; newer.Id++; newer.Origin += Vector3.right * 10; newer.Configuration = GameplayConfig.GetWeapon(6);
                match.Projectiles.Clear(); match.Projectiles.SpawnForMeasurement(newer); match.Projectiles.Simulate(.5);
                Assert.That(observed.y, Is.EqualTo(100), "Next projectile uses the new zero gravity");
                var valid = GameplayConfig.GetWeapon(6);
                asset.spreadRecoverSeconds = -1;
                yield return Wait(() => app.WeaponDebugStatus.StartsWith("修改未应用"), "Invalid edit rejected");
                Assert.That(GameplayConfig.GetWeapon(6), Is.SameAs(valid));
                asset.spreadRecoverSeconds = .5f; asset.weaponPrefabAddress = "Assets/MissingWeaponForValidation.prefab";
                yield return null; yield return null;
                yield return Wait(() => app.WeaponDebugStatus.Contains("模型不存在"), "Invalid model rejected");
                Assert.That(GameplayConfig.GetWeapon(6), Is.SameAs(valid)); Assert.That(player.CharacterView, Is.SameAs(visual));
                asset.weaponPrefabAddress = "Assets/GameResource/Weapons/RifleGirl/Prefabs/RifleGirlRifle.prefab";
                uint modelRevision = WeaponConfigService.Current.Revision(6);
                yield return Applied(app, () => WeaponConfigService.Current.Revision(6) > modelRevision);
                Assert.That(app.Heroes.Get(6).WeaponConfig.WeaponPrefabAddress, Is.EqualTo(asset.weaponPrefabAddress));
                Assert.That(player.Snapshot.Value.AttackNeedsRelease, Is.True);
                Assert.That(player.CharacterView, Is.Not.SameAs(visual), "A different validated weapon prefab rebuilds the assembly");
                uint restoreRevision = WeaponConfigService.Current.Revision(6);
                EditorUtility.CopySerialized(savedAsset, asset);
                yield return Applied(app, () => WeaponConfigService.Current.Revision(6) > restoreRevision);
                // Capture the real Game HUD with three progress levels, including bottom charge rings.
                app.CaptureMouse(true);
                foreach (float progress in new[] { 0f, .5f, 1f })
                {
                    var s = player.Snapshot.Value; s.SpreadProgress = progress; s.AttackNeedsRelease = false;
                    s.WeaponPhase = WeaponPhase.Charging; s.SplatlingChargeSeconds = 130 / 60.0; s.SplatlingReservedInk = 20; s.Ink = 80;
                    SpreadSimulation.Refresh(ref s, GameplayConfig.GetWeapon(6)); player.Snapshot.Value = s;
                    yield return null; yield return null;
                    yield return CaptureGameView("reticle-" + progress.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), 1280, 720, 60);
                }
                yield return CaptureGameView("reticle-fov90-1600x900", 1600, 900, 90);
                yield return CaptureGameView("reticle-fov60-1024x768", 1024, 768, 60);
                foreach (int hero in new[] { 1, 3 })
                {
                    match.enabled = true; // Hero requests are committed by the authority simulation.
                    player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                    yield return Wait(() => player.Snapshot.Value.HeroId == hero && !player.HeroChangePending, "Reticle hero " + hero);
                    match.enabled = false;
                    var s = player.Snapshot.Value; s.SpreadProgress = hero == 1 ? 1 : 0;
                    SpreadSimulation.Refresh(ref s, GameplayConfig.GetWeapon(hero)); player.Snapshot.Value = s;
                    yield return CaptureGameView(hero == 1 ? "rifle-max" : "shotgun-base", 1280, 720, 60);
                }
            }
            finally
            {
                EditorUtility.CopySerialized(savedAsset, asset);
                if (!originallyDirty) EditorUtility.ClearDirty(asset);
                UnityEngine.Object.Destroy(savedAsset);
                match.Projectiles.TraceObserved = null; match.Projectiles.Clear();
            }
            yield return app.Leave().ToCoroutine();
            Assert.That(app.IsWeaponDebugRoom, Is.False); Assert.That(WeaponConfigService.Current.Source(6), Is.Null);
            yield return app.Connect(true, "127.0.0.1", 17993).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            var normal = GameplayConfig.GetWeapon(6); var source = WeaponConfigService.Current.Source(6); float damage = source.damage;
            try
            {
                source.damage += 1;
                for (int i = 0; i < 5; i++) yield return null;
                Assert.That(GameplayConfig.GetWeapon(6), Is.SameAs(normal)); Assert.That(normal.Damage, Is.EqualTo(damage));
            }
            finally { source.damage = damage; }
            yield return app.Leave().ToCoroutine();
            File.WriteAllText(Output + "/results.txt", "PASS: debug loopback host, six heroes, bots, infinite warmup, remote approval rejection; live charge/firing edits cancel and refund once; fresh press gate; spread progress preserved; old/new projectile gravity boundary; invalid config/model retention; model path replacement; normal-room frozen snapshot; exit clears cache.\n");
        }
    }
}
#endif
