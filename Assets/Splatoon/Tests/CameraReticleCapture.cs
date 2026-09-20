#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Reproducible rendered Game View acceptance; trigger with Temp/CameraReticle/capture.
[InitializeOnLoad]
static class CameraReticleCapture
{
    const string Request = "Temp/CameraReticle/capture", Key = "CameraReticle.Capture";
    const string Output = "Reports/CameraReticle/After";
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static bool running;
    static CameraReticleCapture() { EditorApplication.update += Poll; }
    static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || running) return;
        if (Application.isPlaying && SessionState.GetBool(Key, false))
        { running = true; Run().Forget(); return; }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && SessionState.GetBool(Key + ".Restore", false))
        {
            SessionState.SetBool(Key + ".Restore", false);
            string scene = SessionState.GetString(Key + ".Scene", "");
            if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(Request)) return;
        if (File.Exists("Temp/WeaponAlignment/tests")) return;
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) return;
        var active = typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
        if (active == null) return;
        if ((bool)active.Invoke(null, null)) return;
        string token = File.GetLastWriteTimeUtc(Request).Ticks.ToString();
        if (SessionState.GetString(Key + ".Refresh", "") != token)
        { SessionState.SetString(Key + ".Refresh", token); AssetDatabase.Refresh(); return; }
        if (EditorUtility.scriptCompilationFailed) return;
        File.Delete(Request); Directory.CreateDirectory(Output);
        SessionState.SetString(Key + ".Scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
        SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
        EditorApplication.isPlaying = true;
    }
    static async UniTask Wait(Func<bool> ready)
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 40;
        while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) await UniTask.Yield();
        if (!ready()) throw new TimeoutException("Reticle capture state");
    }
    static object sizeGroup;
    static int originalSize = -1;
    static readonly System.Collections.Generic.List<int> customSizes = new();
    static EditorWindow gameView;
    static async UniTask Resize(EditorWindow view, int width, int height)
    {
        gameView = view;
        var type = view.GetType();
        var selected = type.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (originalSize < 0) originalSize = (int)selected.GetValue(view);
        var assembly = typeof(UnityEditor.Editor).Assembly;
        var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
        var sizes = sizesType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
        var getGroup = sizesType.GetMethod("GetGroup");
        sizeGroup = getGroup.Invoke(sizes, new[] { Enum.ToObject(getGroup.GetParameters()[0].ParameterType, 0) });
        var sizeType = assembly.GetType("UnityEditor.GameViewSize");
        var kindType = assembly.GetType("UnityEditor.GameViewSizeType");
        var size = Activator.CreateInstance(sizeType, Enum.ToObject(kindType, 1), width, height, "Camera reticle acceptance");
        int builtIn = (int)sizeGroup.GetType().GetMethod("GetBuiltinCount").Invoke(sizeGroup, null);
        sizeGroup.GetType().GetMethod("AddCustomSize").Invoke(sizeGroup, new[] { size });
        int count = (int)sizeGroup.GetType().GetMethod("GetTotalCount").Invoke(sizeGroup, null);
        customSizes.Add(count - 1 - builtIn);
        selected.SetValue(view, count - 1);
        for (int i = 0; i < 5; i++) { view.Repaint(); await UniTask.NextFrame(); }
        if (Screen.width != width || Screen.height != height) throw new Exception($"Resolution {Screen.width}x{Screen.height}, expected {width}x{height}");
    }
    static void RestoreGameView()
    {
        if (originalSize < 0 || gameView == null) return;
        gameView.GetType().GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(gameView, originalSize);
        for (int i = customSizes.Count - 1; i >= 0; i--) sizeGroup.GetType().GetMethod("RemoveCustomSize").Invoke(sizeGroup, new object[] { customSizes[i] });
        customSizes.Clear(); originalSize = -1;
    }
    static Vector3 FindClearFloor()
    {
        for (int z = -20; z <= 20; z += 4) for (int x = -10; x <= 10; x += 4)
        {
            if (!Physics.Raycast(new Vector3(x, 5, z), Vector3.down, out var floor, 10, PlayerMotorSimulation.WorldMask)) continue;
            if (floor.normal.y < .99f) continue;
            var feet = floor.point + Vector3.up * .04f;
            var pivot = feet + Vector3.up * 2.3f;
            var back = Quaternion.Euler(12, 0, 0) * Vector3.back;
            if (Physics.SphereCast(pivot, .5f, back, out _, 6, PlayerMotorSimulation.WorldMask)) continue;
            if (Physics.CheckCapsule(feet + Vector3.up * .4f, feet + Vector3.up * 1.8f, .3f, PlayerMotorSimulation.WorldMask)) continue;
            return feet;
        }
        throw new Exception("No unobstructed framing location found");
    }
    static async UniTask Run()
    {
        File.WriteAllText(Output + "/status.txt", "RUNNING " + DateTime.UtcNow.ToString("O"));
        var rows = new List<string> { "case,hero,main_x,main_y,expected_x,expected_y,impact_visible,impact_x,impact_y,world_hit,world_x,world_y,world_z,target_visible,hit_feedback,health,ink" };
        PrototypeApp app = null;
        try
        {
            await Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
            app = PrototypeApp.Current;
            await app.StartWeaponDebugRoom();
            await Wait(() => PrototypePlayer.Local != null);
            var player = PrototypePlayer.Local;
            player.RequestHeroChange(3, HeroSelectionOrigin.Warmup);
            await Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 3 && player.CharacterView.Profile.name == "ShotgunGirlPresentation");
            PrototypeMatch.Current.enabled = false; player.enabled = false;
            var feet = FindClearFloor();
            var framing = new List<string> { "case,top,bottom,height,boom,requested_boom,feet_x,feet_y,feet_z" };
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            view.Show(); view.Focus();
            await Resize(view, 1280, 720);
            app.CaptureMouse(true);
            var solver = new TpsAimSolver();
            async UniTask Capture(string label, float pitch, Vector3 position, bool swim = false, float health = 100, double? chargeSeconds = null, float ink = 100)
            {
                var s = player.Snapshot.Value;
                s.Position = position; s.Yaw = s.BodyYaw = 0; s.Pitch = pitch; s.Health = health; s.Ink = ink;
                s.Firing = label.Contains("moving-fire");
                s.WeaponPhase = chargeSeconds.HasValue ? WeaponPhase.Charging : s.Firing ? WeaponPhase.Firing : WeaponPhase.Idle;
                s.FireStartedAt = s.SimulatedAt; s.FireVisualUntil = s.SimulatedAt + 1;
                s.SplatlingChargeSeconds = chargeSeconds ?? 0; s.SplatlingRemaining = 0;
                s.Swimming = s.CompactBody = swim; s.Grounded = !label.Contains("jump"); s.PlanarVelocity = s.Firing ? Vector3.right * 2 : Vector3.zero; s.VerticalSpeed = 0;
                s.AirHumanOffset = s.CameraRebaseOffset = 0;
                s.CurrentSpread = WeaponSimulation.Spread(GameplayConfig.GetWeapon(s.HeroId), !s.Grounded, 1);
                s.CurrentVerticalSpread = s.CurrentSpread;
                if (s.Firing) s.Velocity = s.PlanarVelocity; else s.Velocity = Vector3.zero;
                s.PaperPose = PaperPose.None;
                PaperPoseSimulation.Resolve(ref s, default, player.SwimBody.Profile, s.SimulatedAt);
                s.Movement = swim ? MovementMode.GroundInk : MovementMode.Human;
                player.GetComponent<CharacterController>().enabled = false;
                player.transform.position = position; player.Snapshot.Value = s;
                player.SwimBody.ApplyCollision(s);
                typeof(PrototypePlayer).GetField("_look", Private).SetValue(player, new Vector2(0, pitch));
                Physics.SyncTransforms();
                typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
                player.CharacterView.Animator.Update(.1f);
                var aim = solver.Resolve(player, s, s.NextMuzzle);
                var expected = TpsAimSolver.ReticleViewport(Camera.main, aim.AimPoint);
                var w = GameplayConfig.GetWeapon(s.HeroId);
                bool hit = WeaponImpactPrediction.TryPredict(solver, aim, w, s, player.PlayerId, out var point);
                rows.Add(FormattableString.Invariant($"{label},{s.HeroId},{player.ReticleViewport.x:R},{player.ReticleViewport.y:R},{expected.x:R},{expected.y:R},{player.ImpactReticleVisible},{player.ImpactReticleViewport.x:R},{player.ImpactReticleViewport.y:R},{hit},{point.x:R},{point.y:R},{point.z:R},{player.TargetReticleVisible},{player.HitConfirmedUntil > Time.unscaledTimeAsDouble},{health},{ink}"));
                if (Vector2.Distance(player.ReticleViewport, expected) > .00001f) throw new Exception("Logical reticle drift: " + label);
                if (swim && (player.ImpactReticleVisible || player.TargetReticleVisible)) throw new Exception("Swim hint visible");
                if (Mathf.Abs(Camera.main.fieldOfView - 60) > .01f) throw new Exception("FOV not applied");
                File.WriteAllLines(Output + "/observations.csv", rows);
                app.CaptureMouse(true);
                await UniTask.NextFrame(); await UniTask.NextFrame();
                if (!swim)
                {
                    var builder = Type.GetType("Splatoon.Editor.CameraFramingBuilder, Splatoon.Editor");
                    var points = (List<Vector3>)builder.GetMethod("BodyVertices").Invoke(null, new object[] { player.CharacterView });
                    float top = 1, bottom = 0;
                    foreach (var vertex in points)
                    {
                        float y = 1 - Camera.main.WorldToViewportPoint(player.Visual.TransformPoint(vertex)).y;
                        top = Mathf.Min(top, y); bottom = Mathf.Max(bottom, y);
                    }
                    float boom = Vector3.Distance(Camera.main.transform.position, player.CameraPivot);
                    framing.Add(FormattableString.Invariant($"{label},{top:R},{bottom:R},{bottom-top:R},{boom:R},{player.Presentation.CameraOffset.magnitude:R},{position.x},{position.y},{position.z}"));
                    File.WriteAllLines(Output + "/framing.csv", framing);
                    if (label.Contains("standing") && (top < .58f || top > .63f || bottom < .84f || bottom > .9f || bottom - top < .25f || bottom - top > .3f))
                        throw new Exception($"Framing outside acceptance: {label} top={top} bottom={bottom}");
                }
                string path = Path.GetFullPath(Output + "/" + label + ".png");
                if (File.Exists(path)) File.Delete(path);
                ScreenCapture.CaptureScreenshot(path);
                await Wait(() => File.Exists(path));
            }
            async UniTask Baseline(int hero)
            {
                var original = player.Presentation;
                var savedPivot = original.CameraPivot; var savedOffset = original.CameraOffset; float savedFov = original.CameraVerticalFov;
                var data = SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/CameraReticle/baseline-camera.json"))["profiles"][hero - 1];
                Vector3 Read(string key) => new Vector3(data[key][0].AsFloat, data[key][1].AsFloat, data[key][2].AsFloat);
                var legacy = app.gameObject.AddComponent<Splatoon.Tests.CameraReticleBaselineHud>();
                try
                {
                    original.CameraPivot = Read("CameraPivot"); original.CameraOffset = Read("CameraOffset"); original.CameraVerticalFov = 65;
                    typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
                    app.enabled = false; legacy.Player = player;
                    await UniTask.NextFrame(); await UniTask.NextFrame();
                    string folder = "Reports/CameraReticle/BeforeReconstructed"; Directory.CreateDirectory(folder);
                    string path = Path.GetFullPath(folder + "/hero-" + hero + "-standing-720.png");
                    if (File.Exists(path)) File.Delete(path);
                    ScreenCapture.CaptureScreenshot(path); await Wait(() => File.Exists(path));
                }
                finally
                {
                    app.enabled = true; UnityEngine.Object.Destroy(legacy);
                    original.CameraPivot = savedPivot; original.CameraOffset = savedOffset; original.CameraVerticalFov = savedFov;
                    typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
                }
            }
            string[] profiles = { "", "RifleGirlPresentation", "DualPistolGirlPresentation", "ShotgunGirlPresentation", "PistolGirlPresentation", "RocketLauncherGirlPresentation", "MachineGunGirlPresentation", "BubbleGirlPresentation", "SplooshGirlPresentation", "BubbleShotgunGirlPresentation" };
            foreach (int hero in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
            {
                if (player.Snapshot.Value.HeroId != hero)
                {
                    PrototypeMatch.Current.enabled = true;
                    player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                    await Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero && player.CharacterView.Profile.name == profiles[hero]);
                    PrototypeMatch.Current.enabled = false;
                }
                var position = feet;
                await Capture("hero-" + hero + "-standing-720", 12, position);
                await Baseline(hero);
                await Capture("hero-" + hero + "-moving-fire-720", 12, position);
                await Capture("hero-" + hero + "-swim-720", 12, position, swim: true);
                await Capture("hero-" + hero + "-jump-720", 12, position + Vector3.up);
                await Capture("hero-" + hero + "-land-720", 12, position);
                var wall = new GameObject("Camera acceptance wall");
                wall.transform.position = player.CameraPivot + Quaternion.Euler(12, 0, 0) * player.Presentation.CameraOffset * .5f;
                wall.AddComponent<BoxCollider>().size = new Vector3(3, 3, .15f);
                await Capture("hero-" + hero + "-wall-720", 12, position);
                UnityEngine.Object.Destroy(wall); await UniTask.NextFrame();
                await Resize(view, 1920, 1080);
                await Capture("hero-" + hero + "-standing-1080", 12, position);
                await Resize(view, 1280, 960);
                await Capture("hero-" + hero + "-standing-4x3", 12, position);
                await Resize(view, 1280, 720);
            }
            // Focused HUD state fixtures. Enemy contact is prediction only; no damage or ConfirmHit call.
            PrototypeMatch.Current.enabled = true; player.RequestHeroChange(1, HeroSelectionOrigin.Warmup);
            await Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 1 && player.CharacterView.Profile.name == profiles[1]);
            PrototypeMatch.Current.enabled = false;
            await Capture("state-ground", 12, feet);
            var launch = solver.Resolve(player, player.Snapshot.Value, 0);
            var enemy = GameObject.CreatePrimitive(PrimitiveType.Cube); enemy.name = "HUD state target";
            enemy.transform.localScale = Vector3.one * .5f; enemy.transform.position = launch.Muzzle + launch.InitialDirection;
            var targetNetwork = enemy.AddComponent<Unity.Netcode.NetworkObject>();
            typeof(Unity.Netcode.NetworkObject).GetProperty("OwnerClientId").SetValue(targetNetwork, 999UL); var opponent = enemy.AddComponent<PrototypePlayer>(); opponent.enabled = false;
            typeof(Unity.Netcode.NetworkBehaviour).GetProperty("OwnerClientId").SetValue(opponent, 999UL);
            opponent.GetComponent<CharacterController>().enabled = false;
            opponent.Snapshot.Value = new PlayerSnapshot { Health = 100, Team = 2, HeroId = 1 };
            await Capture("state-enemy-no-fire", 12, feet);
            if (!player.TargetReticleVisible || player.HitConfirmedUntil > Time.unscaledTimeAsDouble) throw new Exception("Prediction/hit feedback were not independent");
            await Capture("state-enemy-held-no-fire", 12, feet);
            if (!player.TargetReticleVisible) throw new Exception("Target hint disappeared without firing");
            var friend = opponent.Snapshot.Value; friend.Team = 1; opponent.Snapshot.Value = friend;
            await Capture("state-friendly", 12, feet);
            if (player.TargetReticleVisible) throw new Exception("Friendly target hint");
            friend.Team = 2; opponent.Snapshot.Value = friend; enemy.transform.position = launch.CameraOrigin + launch.Forward * 150;
            await Capture("state-out-of-range", 12, feet);
            if (player.TargetReticleVisible) throw new Exception("Out of range target hint");
            enemy.transform.position = launch.Muzzle + launch.InitialDirection;
            var muzzleWall = GameObject.CreatePrimitive(PrimitiveType.Cube); muzzleWall.name = "HUD muzzle wall";
            muzzleWall.transform.position = launch.Muzzle + launch.InitialDirection * .3f; muzzleWall.transform.localScale = Vector3.one * .2f;
            await Capture("state-wall-and-low-ink", 12, feet, ink: 0);
            if (player.TargetReticleVisible || !player.MuzzleBlocked) throw new Exception("Occlusion hint priority");
            UnityEngine.Object.Destroy(muzzleWall); UnityEngine.Object.Destroy(enemy); await UniTask.NextFrame();
            await Capture("state-low-ink", 12, feet, ink: 0);
            await Capture("state-air", -60, feet);
            await Capture("state-confirmation-base", 12, feet);
            // HUD-only injection through the unchanged ConfirmHit entry point. Real received
            // projectile confirmations are checked separately in the two-process probe.
            async UniTask FeedbackImage(string label, bool? killed, bool focused = true)
            {
                app.CaptureMouse(focused);
                if (killed.HasValue) player.ConfirmHit(killed.Value);
                await UniTask.NextFrame();
                if (killed.HasValue && player.HitConfirmedUntil <= Time.unscaledTimeAsDouble) throw new Exception("Hit fixture missed feedback window");
                string path = Path.GetFullPath(Output + "/" + label + ".png");
                if (File.Exists(path)) File.Delete(path);
                ScreenCapture.CaptureScreenshot(path); await Wait(() => File.Exists(path));
            }
            await FeedbackImage("state-confirm-hit-injected", false);
            await FeedbackImage("state-confirm-kill-injected", true);
            await FeedbackImage("state-focus-released", null, false);
            await Capture("state-dead", 12, feet, health: 0);
            if (player.TargetReticleVisible || player.ImpactReticleVisible) throw new Exception("Dead hints");
            File.WriteAllText(Output + "/status.txt", "CAPTURED " + DateTime.UtcNow.ToString("O"));
        }
        catch (Exception e) { File.WriteAllText(Output + "/status.txt", e.ToString()); }
        finally
        {
            if (app != null && app.InRoom) await app.Leave();
            RestoreGameView();
            SessionState.SetBool(Key, false); SessionState.SetBool(Key + ".Restore", true); running = false; EditorApplication.isPlaying = false;
        }
    }
}
#endif
