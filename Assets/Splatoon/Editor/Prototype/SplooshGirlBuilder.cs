#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    public static class SplooshGirlBuilder
    {
        public const string Root = "Assets/GameResource/Characters/SplooshGirl";
        public const string WeaponRoot = "Assets/GameResource/Weapons/SplooshGirl";
        public const string CharacterPath = Root + "/Prefabs/SplooshGirlVisual.prefab";
        public const string WeaponPath = WeaponRoot + "/Prefabs/SplooshGun.prefab";
        public const string Report = "Reports/SplooshGirl";
        const float S = 18f / 24.037f;
        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new Exception("Missing " + path);
        static T Copy<T>(string source, string path) where T : Object
        { if (!File.Exists(path) && !AssetDatabase.CopyAsset(source, path)) throw new Exception("Cannot copy " + source); return Load<T>(path); }
        [MenuItem("喷墨对战/角色/安装铃芽与广域标记枪")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first");
            foreach (var p in new[] { Root + "/Prefabs", WeaponRoot + "/Prefabs", Report, "Assets/GameResource/UI/HeroPortraits" }) Directory.CreateDirectory(p);
            AssetDatabase.Refresh();
            BuildConfig();
            var scene = EditorSceneManager.NewPreviewScene();
            try { BuildCharacter(scene); }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            PaperBodyBuilder.ConfigureHero("SplooshGirl", new Vector2(.6f, 1.2f));
            var profile = Load<CharacterPresentationProfile>(Root + "/SplooshGirlPresentation.asset");
            profile.Paper.CameraOffset = new Vector3(0, .65f, 0); EditorUtility.SetDirty(profile.Paper);
            PrototypeBuilder.ConfigureAddressables(); AssetDatabase.SaveAssets();
            File.WriteAllText(Report + "/assets.txt", "PASS: hero 8, 0.55 rifle, own Avatar/controller binding, measured muzzle, grip basis, live paper and Addressables\n");
        }
        public static void InstallBatch()
        { try { Install(); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
        public static void BuildBatch()
        { try { PrototypeBuilder.BuildWindowsTo("Builds/SplooshGirl"); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
        static void BuildConfig()
        {
            var ammo = Copy<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset", WeaponRoot + "/SplooshGirlAmmoConfig.asset");
            ammo.ammoId = 8; EditorUtility.SetDirty(ammo);
            var w = Copy<WeaponConfigAsset>("Assets/GameResource/Weapons/PistolGirl/PistolGirlWeaponConfig.asset", WeaponRoot + "/SplooshGirlWeaponConfig.asset");
            var source = SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SplooshGirl/WeaponShooterShort.1130.json"))["GameParameters"];
            float F(string group, string name) => source[group][name].AsFloat;
            w.weaponPrefabAddress = "Weapon/SplooshGun"; w.ammoConfig = ammo;
            w.fireMode = WeaponFireMode.Automatic; w.pelletCount = 1; w.muzzleMode = WeaponMuzzleMode.Single;
            w.fireRate = 60 / F("WeaponParam", "RepeatFrame"); w.shotInk = F("WeaponParam", "InkConsume") * 100;
            // Startup is a declared project assumption; PostDelay is independent.
            w.startSeconds = 2.0 / 60; w.emergeStartSeconds = 8.0 / 60;
            w.inkRecoverLockSeconds = F("WeaponParam", "InkRecoverStop") / 60.0;
            w.shootMoveSpeed = F("WeaponParam", "MoveSpeed") * 60 * S;
            w.damage = F("DamageParam", "ValueMax") / 10; w.damageMin = F("DamageParam", "ValueMin") / 10;
            w.damageReduceStartSeconds = F("DamageParam", "ReduceStartFrame") / 60.0; w.damageReduceEndSeconds = F("DamageParam", "ReduceEndFrame") / 60.0;
            w.speedMin = w.speedMax = F("MoveParam", "SpawnSpeed") * 60 * S;
            w.straightSeconds = F("MoveParam", "GoStraightToBrakeStateFrame") / 60.0;
            w.referenceBrakeEndSpeed = F("MoveParam", "GoStraightStateEndMaxSpeed") * 60 * S;
            w.projectileGravity = F("MoveParam", "FreeGravity") * 3600 * S;
            w.brakeSeconds = 4.0 / 60; w.referenceBrakeDrag = .36f; w.referenceBrakeGravity = .07f * 3600 * S; w.referenceFreeDrag = .02f;
            w.collisionRadius = F("CollisionParam", "InitRadiusForField") * S; w.referencePlayerRadius = F("CollisionParam", "InitRadiusForPlayer") * S;
            w.referenceRules = w.referenceSpreadEnabled = w.shooterDetails = true; w.motionMode = ProjectileMotionMode.ReferencePhased;
            w.spreadDegrees = F("WeaponParam", "Stand_DegSwerve"); w.jumpSpreadDegrees = F("WeaponParam", "Jump_DegSwerve");
            w.referenceBiasMin = F("WeaponParam", "Stand_DegBiasMin"); w.referenceBiasMax = F("WeaponParam", "Stand_DegBiasMax");
            w.referenceBiasPerShot = F("WeaponParam", "Stand_DegBiasKf"); w.referenceBiasRecovery = F("WeaponParam", "Stand_DegBiasDecrease") * 60;
            w.referenceJumpBias = F("WeaponParam", "Jump_DegBiasMax"); w.referenceJumpStart = F("WeaponParam", "Jump_DegBiasDecreaseStartFrame") / 60.0;
            w.referenceJumpEnd = F("WeaponParam", "Jump_DegBiasEndFrame") / 60.0;
            w.shooterPostSeconds = F("WeaponParam", "PostDelayFrame") / 60.0;
            w.shooterMoveForwardRate = F("spl__SpawnBulletAdditionMovePlayerParam", "ZRate");
            w.paintRadiusMin = F("PaintParam", "WidthHalfFar") * S; w.paintRadiusMax = F("PaintParam", "WidthHalfMiddle") * S;
            w.shooterPaintNearRadius = F("PaintParam", "WidthHalfNear") * S; w.shooterPaintNearDistance = F("PaintParam", "DistanceNear") * S;
            w.paintDistanceMiddle = F("PaintParam", "DistanceMiddle") * S; w.paintDistanceFar = 20 * S;
            w.paintDepthMin = F("PaintParam", "DepthScaleMin"); w.paintDepthMax = F("PaintParam", "DepthScaleMax");
            w.paintDepthBreakMin = F("PaintParam", "DepthScaleMinBreakFree"); w.paintDepthBreakMax = F("PaintParam", "DepthScaleMaxBreakFree");
            w.shooterPaintAngleMin = 10; w.shooterPaintAngleMax = 35; w.shooterFallHeightMin = 1.5f * S; w.shooterFallHeightMax = 10 * S;
            w.trailSpacing = F("SplashSpawnParam", "SpawnBetweenLength") * S; w.referenceTrailStart = F("SplashSpawnParam", "SpawnNearestLength") * S;
            w.referenceTrailBudget = F("SplashSpawnParam", "SpawnNum"); w.shooterSplitNum = (int)F("SplashSpawnParam", "SplitNum");
            w.referenceFootEvery = w.shooterSplitNum; w.referenceTrailRandomPhase = false;
            w.trailRadiusMin = w.trailRadiusMax = F("SplashPaintParam", "WidthHalf") * S; w.referenceFootRadius = F("SplashPaintParam", "WidthHalfNearest") * S;
            w.shooterSplashHeightMin = F("SplashPaintParam", "DepthMaxDropHeight") * S; w.shooterSplashHeightMax = F("SplashPaintParam", "DepthMinDropHeight") * S;
            w.shooterSplashDepthMin = 1; w.shooterSplashDepthMax = 1.2f; w.referenceFootDepth = w.trailDepthScale = 1.2f;
            w.shooterSplashSideSpeed = .055f * 60 * S; w.shooterSplashUpSpeed = .015f * 60 * S;
            w.shooterSplashForwardMin = .01f * 60 * S; w.shooterSplashForwardMax = .02f * 60 * S;
            w.shooterWallFirstMin = F("WallDropMoveParam", "FallPeriodFirstFrameMin") / 60.0; w.shooterWallFirstMax = F("WallDropMoveParam", "FallPeriodFirstFrameMax") / 60.0;
            w.shooterWallMiddle = F("WallDropMoveParam", "FallPeriodSecondFrame") / 60.0;
            w.shooterWallLastMin = F("WallDropMoveParam", "FallPeriodLastFrameMin") / 60.0; w.shooterWallLastMax = F("WallDropMoveParam", "FallPeriodLastFrameMax") / 60.0;
            w.shooterWallFirstSpeed = F("WallDropMoveParam", "FallPeriodFirstTargetSpeed") * 60 * S; w.wallDropSpeed = F("WallDropMoveParam", "FallPeriodSecondTargetSpeed") * 60 * S;
            w.wallDropSeconds = w.shooterWallFirstMax + w.shooterWallMiddle + w.shooterWallLastMax;
            w.wallDropRadius = F("WallDropCollisionPaintParam", "PaintRadiusFall") * S; w.wallDropGroundRadius = F("WallDropCollisionPaintParam", "PaintRadiusGround") * S;
            w.shooterWallShockRadius = F("WallDropCollisionPaintParam", "PaintRadiusShock") * S; w.shooterWallGravity = .008f * 3600 * S;
            w.effectiveRange = ReferenceBallistics.Position(Vector3.zero, Vector3.forward * w.speedMin, w.Snapshot(), F("WeaponParam", "ShotGuideFrame") / 60.0).z;
            WeaponConfigValidation.Validate(w.Snapshot()); EditorUtility.SetDirty(w); AssetDatabase.SaveAssets();
        }
        static void BuildCharacter(Scene scene)
        {
            var go = Object.Instantiate(Load<GameObject>("Assets/GameResource/Characters/RifleGirlChibi/Prefabs/RifleGirlChibi.prefab"));
            SceneManager.MoveGameObjectToScene(go, scene); go.name = "SplooshGirlVisual";
            foreach (var component in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(component);
            var animator = go.GetComponent<Animator>(); animator.applyRootMotion = false;
            var profile = Copy<CharacterPresentationProfile>("Assets/GameResource/Characters/RifleGirl/RifleGirlPresentation.asset", Root + "/SplooshGirlPresentation.asset");
            profile.CameraPivot = new Vector3(0, 1, 0); profile.CameraOffset = new Vector3(.45f, .12f, -2.8f);
            profile.CameraCollisionRadius = .15f; profile.CameraCollisionPadding = .06f; profile.CameraShake = .08f;
            profile.AnimationReferenceSpeed = .104f * 60 * S; profile.SingleShot = profile.DualWield = profile.Splatling = false;
            profile.WalkPlayback = Load<CharacterPresentationProfile>("Assets/GameResource/Characters/RifleGirl/RifleGirlPresentation.asset").WalkPlayback * 1.5f * profile.AnimationReferenceSpeed / 5f;
            var view = go.AddComponent<InkCharacterView>(); view.Animator = animator; view.Profile = profile;
            view.WeaponSocket = go.GetComponentsInChildren<Transform>().Single(t => t.name == "Hand_R_Socket");
            view.LeftWeaponSocket = go.GetComponentsInChildren<Transform>().Single(t => t.name == "Hand_L_Socket");
            var original = Load<GameObject>(CombatGirlsBuilder.CharacterPath).GetComponent<InkCharacterView>();
            view.TeamMarker = Object.Instantiate(original.TeamMarker.gameObject, go.transform).GetComponent<Renderer>();
            view.TeamMarker.transform.localPosition = new Vector3(0, 1.38f, 0); view.TeamMarker.transform.localScale *= 2f / 3;
            view.SwimEffect = Object.Instantiate(original.SwimEffect.gameObject, go.transform).GetComponent<ParticleSystem>();
            view.SwimEffect.transform.localScale *= 2f / 3;
            var rest = SimpleJSON.JSONNode.Parse(File.ReadAllText("ArtSource/Characters/RifleGirlChibi/Reference/chibi-rest-frames.json"));
            var q = rest["bones"].Children.Single(n => n["name"].Value == "hand_l")["rotation"];
            var basis = Quaternion.Inverse(new Quaternion(q["x"], q["y"], q["z"], q["w"])) * animator.GetBoneTransform(HumanBodyBones.LeftHand).rotation;
            var weapon = Object.Instantiate(Load<GameObject>(CombatGirlsBuilder.WeaponPath)); SceneManager.MoveGameObjectToScene(weapon, scene);
            weapon.name = "SplooshGun"; weapon.transform.localScale = Vector3.one * .55f;
            var bindings = weapon.GetComponent<HeroWeaponBindings>(); bindings.LeftGrip.localRotation *= basis;
            var weaponPrefab = PrefabUtility.SaveAsPrefabAsset(weapon, WeaponPath); Object.DestroyImmediate(weapon);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 8;
            PrefabUtility.SaveAsPrefabAsset(go, CharacterPath);
            weapon = Object.Instantiate(weaponPrefab, view.WeaponSocket, false); view.BindWeapon(weapon.GetComponent<HeroWeaponBindings>(), weaponPrefab);
            var state = new PlayerSnapshot { HeroId = 8, Health = 100, Grounded = true, Team = 1, Revision = 1 };
            view.Present(state, 0, 0); animator.Update(0);
            var clip = animator.runtimeAnimatorController.animationClips.First(c => c.name == "AimIdle");
            var graph = PlayableGraph.Create("Sploosh calibration"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var playable = AnimationClipPlayable.Create(graph, clip); AnimationPlayableOutput.Create(graph, "pose", animator).SetSourcePlayable(playable); graph.Play();
            playable.SetTime(clip.length / 3); graph.Evaluate(0); view.ApplyAim();
            profile.AimPivot = go.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Chest).position);
            profile.MuzzlePosition = go.transform.InverseTransformPoint(view.Nozzle.position); profile.LeftMuzzlePosition = profile.MuzzlePosition;
            EditorUtility.SetDirty(profile);
            view.TeamMarker.enabled = false;
            // Model calibration previews must not overwrite the authored UI portrait.
            Capture(go, scene, Report + "/hero-portrait-preview.png", 512);
            Capture(go, scene, Report + "/hero-front.png", 1000);
            graph.Destroy(); Object.DestroyImmediate(go);
        }
        static void Capture(GameObject actor, Scene scene, string path, int size)
        {
            var cameraObject = new GameObject("Sploosh portrait camera"); SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);
            camera.orthographic = true; camera.orthographicSize = .78f; camera.nearClipPlane = .01f;
            camera.transform.position = new Vector3(2.2f, 1.15f, 4); camera.transform.LookAt(new Vector3(0, .66f, 0));
            camera.backgroundColor = new Color(.19f, .17f, .26f); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            var lightObject = new GameObject("Portrait light"); SceneManager.MoveGameObjectToScene(lightObject, scene);
            var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f; light.transform.rotation = Quaternion.Euler(35, -25, 0);
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32); rt.Create(); camera.targetTexture = rt;
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
            var prior = RenderTexture.active; RenderTexture.active = rt; var image = new Texture2D(size, size, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, size, size), 0, 0); image.Apply(); File.WriteAllBytes(path, image.EncodeToPNG());
            RenderTexture.active = prior; camera.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(image);
            Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(lightObject);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return;
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer) { importer.mipmapEnabled = false; importer.SaveAndReimport(); }
        }
    }
}
#endif
