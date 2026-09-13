#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Splatoon.Combat;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class CombatGirlsBuilder
    {
        public const string SourceRoot = "Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack";
        public const string Root = "Assets/GameResource/Characters/RifleGirl";
        public const string CharacterPath = Root + "/Prefabs/RifleGirlVisual.prefab";
        public const string ProfilePath = Root + "/RifleGirlPresentation.asset";
        public const string ControllerPath = Root + "/Animations/RifleGirlCombat.controller";
        public const string WeaponPath = "Assets/GameResource/Weapons/RifleGirl/Prefabs/RifleGirlRifle.prefab";
        public const string PlayerPath = "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab";
        public const string PreviewPath = Root + "/Preview/RifleGirlPreview.unity";
        public static readonly string[] ClipNames = { "R_AimWalk_F", "R_AimWalk_B", "R_AimWalk_FL", "R_AimWalk_BR", "R_AimTurn_L90", "R_AimTurn_R90", "R_AimIdle", "R_AimIdle_AutoShoot", "R_Die_F", "R_Die_B" };
        static readonly List<string> Report = new();
        public static T Load<T>(string path) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new InvalidOperationException("资源缺失：" + path);
        static void Folder(string path) { Directory.CreateDirectory(path); AssetDatabase.Refresh(); }
        public static string ClipPath(string name) => SourceRoot + "/RifleGirl/Animations/" + (name.StartsWith("R_Die") ? "Normal/" : "Aiming/") + name + ".fbx";
        public static AnimationClip Clip(string name) => AssetDatabase.LoadAllAssetsAtPath(ClipPath(name)).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));

        [MenuItem("喷墨对战/角色/安装 RifleGirl")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出运行模式");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Report.Clear();
            Folder(Root + "/Animations"); Folder(Root + "/Prefabs"); Folder(Root + "/Preview"); Folder(Root + "/Materials"); Folder(Path.GetDirectoryName(WeaponPath));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (Shader.Find("Toon/Toon") == null) throw new InvalidOperationException("Unity Toon Shader 0.14.1-preview 未完成导入");
            var avatar = Load<Avatar>(SourceRoot + "/Humanoid_Bot/Models/Humanoid_F.fbx");
            if (!avatar.isHuman || !avatar.isValid) throw new InvalidOperationException("Humanoid_F Avatar 无效");
            // Repair sources first, before sampling root curves. Preserve each authored take/range.
            foreach (var name in ClipNames)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(ClipPath(name));
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther; importer.sourceAvatar = avatar;
                var authored = importer.clipAnimations;
                foreach (var c in authored)
                {
                    c.lockRootRotation = c.lockRootPositionXZ = c.lockRootHeightY = false;
                    c.keepOriginalOrientation = c.keepOriginalPositionXZ = false;
                    c.keepOriginalPositionY = true; c.loopPose = false;
                }
                importer.clipAnimations = authored;
                importer.SaveAndReimport();
            }
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>(ProfilePath);
            if (profile == null) { profile = ScriptableObject.CreateInstance<CharacterPresentationProfile>(); AssetDatabase.CreateAsset(profile, ProfilePath); }
            var raw = CreateSourceCharacter();
            try
            {
                profile.TurnLeftDuration = Clip("R_AimTurn_L90").length;
                profile.TurnRightDuration = Clip("R_AimTurn_R90").length;
                profile.TurnLeftProgress = ExtractTurnCurve(Clip("R_AimTurn_L90"), raw);
                profile.TurnRightProgress = ExtractTurnCurve(Clip("R_AimTurn_R90"), raw);
                for (int i = 0; i < 4; i++)
                {
                    var clip = Clip(ClipNames[i]);
                    float native = clip.averageSpeed.magnitude;
                    if (native < .05f) native = MeasureStrideSpeed(clip, raw, i < 2 ? Vector3.forward : Vector3.right);
                    if (!float.IsFinite(native) || native < .05f) throw new InvalidOperationException("无法标定动作步幅：" + clip.name);
                    profile.WalkPlayback[i] = 5 / native;
                    Report.Add($"{ClipNames[i]}: nativeSpeed={native:F5}, playbackAt5mps={profile.WalkPlayback[i]:F5}");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(raw); }
            foreach (var name in ClipNames)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(ClipPath(name));
                var clips = importer.clipAnimations;
                if (clips.Length != 1) throw new InvalidOperationException("每个所选 FBX 应有一个正式片段：" + name);
                var c = clips[0];
                bool looping = name.StartsWith("R_AimWalk") || name.StartsWith("R_AimIdle");
                c.loopTime = looping; c.loopPose = looping;
                // Turn yaw is extracted by Mecanim and applied once by BodyYaw.
                // Baking it into the pose would rotate the hips a second time.
                c.lockRootRotation = !name.Contains("AimTurn"); c.keepOriginalOrientation = true;
                c.lockRootPositionXZ = true; c.keepOriginalPositionXZ = true;
                c.lockRootHeightY = true; c.keepOriginalPositionY = true;
                if (name == "R_AimIdle_AutoShoot") { c.firstFrame = 15; c.lastFrame = 44; }
                importer.clipAnimations = clips; importer.SaveAndReimport();
            }
            profile.ShootDuration = Clip("R_AimIdle_AutoShoot").length;
            profile.DieForwardDuration = Clip("R_Die_F").length;
            profile.DieBackwardDuration = Clip("R_Die_B").length;
            var character = CreateSourceCharacter();
            var animator = character.GetComponent<Animator>();
            Clip("R_AimIdle").SampleAnimation(character, 0);
            CreateGameplayMaterials(character);
            var weapon = character.GetComponentsInChildren<ParentConstraint>(true).First().transform;
            var socket = Find(character, "Hand_R_Socket");
            UnityEngine.Object.DestroyImmediate(weapon.GetComponent<ParentConstraint>());
            weapon.SetParent(socket, false); weapon.localPosition = Vector3.zero; weapon.localRotation = Quaternion.identity;
            var muzzle = new GameObject("Muzzle").transform; muzzle.SetParent(weapon, false);
            muzzle.position = MeasureMuzzle(weapon); muzzle.rotation = Quaternion.identity;
            var heroWeapon = weapon.gameObject.AddComponent<HeroWeaponBindings>();
            heroWeapon.Nozzle = muzzle; heroWeapon.LeftGrip = Find(weapon.gameObject, "Left_Handle");
            var weaponPrefab = PrefabUtility.SaveAsPrefabAssetAndConnect(weapon.gameObject, WeaponPath, InteractionMode.AutomatedAction);
            character.name = "RifleGirlVisual";
            foreach (var transform in character.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 8;
            profile.CameraPivot = new Vector3(0, animator.GetBoneTransform(HumanBodyBones.Head).position.y - .04f, 0);
            profile.AimPivot = new Vector3(0, animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position.y, 0);
            profile.MuzzlePosition = character.transform.InverseTransformPoint(muzzle.position);
            Report.Add($"CameraPivot={profile.CameraPivot:R}; AimPivot={profile.AimPivot:R}; Muzzle={profile.MuzzlePosition:R}");
            animator.runtimeAnimatorController = CreateController(character, profile);
            animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var view = character.AddComponent<InkCharacterView>();
            view.Profile = profile; view.Animator = animator; view.Weapon = weapon; view.WeaponSocket = socket;
            view.LeftGrip = Find(weapon.gameObject, "Left_Handle"); view.Nozzle = muzzle;
            view.BoundWeaponPrefab = weaponPrefab; view.TeamMarker = CreateMarker(character.transform);
            view.SwimEffect = CreateSwimEffect(character.transform);
            // Restore the bind pose before saving, so Rebind and every animation share
            // the same canonical transforms rather than a sampled animation pose.
            animator.Rebind();
            var visual = PrefabUtility.SaveAsPrefabAsset(character, CharacterPath);
            EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets();
            UnityEngine.Object.DestroyImmediate(character);
            InstallPlayer(visual, profile);
            PrototypeBuilder.ConfigureAddressables();
            CreatePreviewScene();
            ValidateInstalled();
            Directory.CreateDirectory("Docs/CombatGirls"); File.WriteAllLines("Docs/CombatGirls/import-validation.txt", Report);
            AssetDatabase.SaveAssets();
            Debug.Log("[CombatGirls] Install and asset validation PASS");
        }

        public static GameObject CreateSourceCharacter()
        {
            var source = Load<GameObject>(SourceRoot + "/RifleGirl/Prefab/Rifle_Full_Body.prefab");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            foreach (var script in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
            foreach (var collider in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var rigidbody in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rigidbody);
            var animator = go.GetComponent<Animator>();
            animator.avatar = Load<Avatar>(SourceRoot + "/Humanoid_Bot/Models/Humanoid_F.fbx"); animator.applyRootMotion = false;
            return go;
        }

        static void CreateGameplayMaterials(GameObject character)
        {
            var instances = new Dictionary<Material, Material>();
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || source.shader.name != "Toon/Toon") continue;
                    if (!instances.TryGetValue(source, out var gameplay))
                    {
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
                        string path = Root + "/Materials/" + source.name + "_" + guid[..8] + ".mat";
                        gameplay = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (gameplay == null) { gameplay = new Material(source); AssetDatabase.CreateAsset(gameplay, path); }
                        else EditorUtility.CopySerialized(source, gameplay);
                        gameplay.name = source.name + " Gameplay";
                        // UTS clamps light RGB above one. The white reference light
                        // remains identical; the training ground's intensity 2 no
                        // longer clips the skin and hair to warm white.
                        gameplay.SetFloat("_Is_Filter_LightColor", 1);
                        EditorUtility.SetDirty(gameplay); instances.Add(source, gameplay);
                        Report.Add($"Material {path}: source={AssetDatabase.GetAssetPath(source)}, only _Is_Filter_LightColor {source.GetFloat("_Is_Filter_LightColor")} -> 1");
                    }
                    materials[i] = gameplay;
                }
                renderer.sharedMaterials = materials;
            }
        }

        internal static AnimationCurve ExtractTurnCurve(AnimationClip clip, GameObject source)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            string root = new[] { "RootQ", "MotionQ" }.FirstOrDefault(prefix => bindings.Any(b => b.propertyName == prefix + ".w"));
            float[] angles = new float[61];
            for (int i = 0; i < angles.Length; i++)
            {
                float t = clip.length * i / (angles.Length - 1f);
                Quaternion q;
                if (root != null)
                {
                    float Value(string axis) => AnimationUtility.GetEditorCurve(clip, bindings.First(b => b.propertyName == root + axis)).Evaluate(t);
                    q = new Quaternion(Value(".x"), Value(".y"), Value(".z"), Value(".w"));
                }
                else { clip.SampleAnimation(source, t); q = source.GetComponent<Animator>().bodyRotation; }
                angles[i] = i == 0 ? q.eulerAngles.y : angles[i - 1] + Mathf.DeltaAngle(angles[i - 1], q.eulerAngles.y);
            }
            float total = angles[^1] - angles[0];
            Report.Add($"{clip.name}: root={root ?? "Humanoid bodyRotation"}, rotation={total:F4}, length={clip.length:F4}");
            if (Mathf.Abs(total) < 30) throw new InvalidOperationException("转向根曲线未能提取：" + clip.name + " angle=" + total);
            var keys = new Keyframe[angles.Length];
            float last = 0;
            for (int i = 0; i < keys.Length; i++)
            { last = Mathf.Max(last, Mathf.Clamp01((angles[i] - angles[0]) / total)); keys[i] = new Keyframe(i / (keys.Length - 1f), last); }
            var curve = new AnimationCurve(keys);
            for (int i = 0; i < keys.Length; i++) { AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear); }
            return curve;
        }

        internal static float MeasureStrideSpeed(AnimationClip clip, GameObject source, Vector3 axis)
        {
            var animator = source.GetComponent<Animator>();
            var foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i <= 60; i++)
            {
                clip.SampleAnimation(source, clip.length * i / 60f);
                float at = Vector3.Dot(source.transform.InverseTransformPoint(foot.position), axis);
                min = Mathf.Min(min, at); max = Mathf.Max(max, at);
            }
            return 2 * (max - min) / clip.length;
        }

        internal static Vector3 MeasureMuzzle(Transform weapon)
        {
            var vertices = weapon.GetComponentsInChildren<MeshFilter>(true).SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v))).ToArray();
            if (vertices.Length == 0) throw new InvalidOperationException("枪械 MeshFilter 缺失");
            float tip = vertices.Max(v => v.z);
            var end = vertices.Where(v => v.z >= tip - .012f).ToArray();
            var sum = Vector3.zero; foreach (var v in end) sum += v;
            return sum / end.Length + Vector3.forward * .005f;
        }

        static AnimatorController CreateController(GameObject character, CharacterPresentationProfile profile)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller != null)
            {
                // Keep the controller GUID stable on repeated installs.
                foreach (var layer in controller.layers) UnityEngine.Object.DestroyImmediate(layer.stateMachine, true);
                controller.layers = Array.Empty<AnimatorControllerLayer>(); controller.parameters = Array.Empty<AnimatorControllerParameter>();
                foreach (var child in AssetDatabase.LoadAllAssetsAtPath(ControllerPath)) if (child != controller) UnityEngine.Object.DestroyImmediate(child, true);
            }
            else { controller = new AnimatorController(); AssetDatabase.CreateAsset(controller, ControllerPath); }
            controller.AddParameter("MoveX", AnimatorControllerParameterType.Float); controller.AddParameter("MoveY", AnimatorControllerParameterType.Float);
            controller.AddParameter("MovePlayback", AnimatorControllerParameterType.Float);
            controller.AddLayer("Base Layer"); var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Locomotion"); machine.defaultState = state; state.writeDefaultValues = false;
            state.speedParameter = "MovePlayback"; state.speedParameterActive = true;
            var tree = new BlendTree { name = "AimWalkFourDirections", blendType = BlendTreeType.SimpleDirectional2D, blendParameter = "MoveX", blendParameterY = "MoveY", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller); state.motion = tree;
            tree.AddChild(Clip("R_AimIdle"), Vector2.zero);
            var directions = new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right };
            for (int i = 0; i < 4; i++) tree.AddChild(Clip(ClipNames[i]), directions[i]);
            var motions = tree.children; for (int i = 0; i < 4; i++) motions[i + 1].timeScale = profile.WalkPlayback[i]; tree.children = motions;
            foreach (var pair in new[] { ("Air", "R_AimIdle"), ("TurnLeft", "R_AimTurn_L90"), ("TurnRight", "R_AimTurn_R90"), ("DieForward", "R_Die_F"), ("DieBackward", "R_Die_B") })
            { var s = machine.AddState(pair.Item1); s.motion = Clip(pair.Item2); s.writeDefaultValues = false; }
            string maskPath = Root + "/Animations/RifleUpperBody.mask";
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (mask == null) { mask = new AvatarMask { name = "RifleUpperBody" }; AssetDatabase.CreateAsset(mask, maskPath); }
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i,
                i == (int)AvatarMaskBodyPart.Body || i == (int)AvatarMaskBodyPart.Head || i == (int)AvatarMaskBodyPart.LeftArm || i == (int)AvatarMaskBodyPart.RightArm || i == (int)AvatarMaskBodyPart.LeftFingers || i == (int)AvatarMaskBodyPart.RightFingers);
            mask.transformCount = 0; mask.AddTransformPath(character.transform, true);
            for (int i = 0; i < mask.transformCount; i++) mask.SetTransformActive(i, mask.GetTransformPath(i).Contains("/spine_01"));
            EditorUtility.SetDirty(mask);
            controller.AddLayer("Shooting"); var layers = controller.layers;
            layers[0].defaultWeight = 1; layers[0].iKPass = false;
            layers[1].defaultWeight = 0; layers[1].avatarMask = mask; layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
            var shooting = layers[1].stateMachine.AddState("AutoShoot"); shooting.motion = Clip("R_AimIdle_AutoShoot"); shooting.writeDefaultValues = false;
            layers[1].stateMachine.defaultState = shooting; controller.layers = layers;
            EditorUtility.SetDirty(controller); return controller;
        }

        static Renderer CreateMarker(Transform parent)
        {
            string path = Root + "/TeamMarker.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(material, path); }
            var mesh = new Mesh { name = "TeamRing" }; const int segments = 48;
            var positions = new Vector3[segments * 2]; var triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                Vector3 direction = new Vector3(Mathf.Cos(i * Mathf.PI * 2 / segments), 0, Mathf.Sin(i * Mathf.PI * 2 / segments));
                positions[i * 2] = direction * .31f; positions[i * 2 + 1] = direction * .36f;
                int next = (i + 1) % segments;
                int n = i * 6; triangles[n] = i * 2; triangles[n + 1] = next * 2; triangles[n + 2] = i * 2 + 1;
                triangles[n + 3] = i * 2 + 1; triangles[n + 4] = next * 2; triangles[n + 5] = next * 2 + 1;
            }
            mesh.vertices = positions; mesh.triangles = triangles; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            string meshPath = Root + "/TeamRing.asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (old != null) { EditorUtility.CopySerialized(mesh, old); UnityEngine.Object.DestroyImmediate(mesh); mesh = old; }
            else AssetDatabase.CreateAsset(mesh, meshPath);
            var go = new GameObject("TeamMarker", typeof(MeshFilter), typeof(MeshRenderer)); go.layer = 8;
            go.transform.SetParent(parent, false); go.transform.localPosition = Vector3.up * .025f;
            go.GetComponent<MeshFilter>().sharedMesh = mesh; var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false; return renderer;
        }

        static ParticleSystem CreateSwimEffect(Transform parent)
        {
            var go = new GameObject("SwimInk"); go.transform.SetParent(parent, false); go.transform.localPosition = Vector3.up * .04f;
            var ps = go.AddComponent<ParticleSystem>(); var main = ps.main;
            main.loop = true; main.playOnAwake = false; main.startSpeed = .1f; main.startSize = .2f; main.startLifetime = .3f;
            var emission = ps.emission; emission.rateOverTime = 12;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = Load<Material>("Assets/GameResource/Effects/Ink/Materials/InkParticle.mat");
            InkCharacterView.ConfigureSwimEffect(ps);
            InkCharacterView.SetInkMesh(ps, Load<GameObject>("Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab").GetComponent<ParticleSystemRenderer>().mesh);
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); return ps;
        }

        static Transform Find(GameObject root, string name) => root.GetComponentsInChildren<Transform>(true).First(t => t.name == name);
        static void InstallPlayer(GameObject visual, CharacterPresentationProfile profile)
        {
            var player = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                var runtime = player.GetComponent<PrototypePlayer>();
                if (runtime.Visual != null) UnityEngine.Object.DestroyImmediate(runtime.Visual.gameObject);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(visual, player.transform);
                runtime.Visual = instance.transform; runtime.CharacterView = instance.GetComponent<InkCharacterView>(); runtime.BoundVisualPrefab = visual;
                runtime.SimulationAimPivot = profile.AimPivot; runtime.SimulationMuzzle.localPosition = profile.MuzzlePosition;
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
        }

        [MenuItem("喷墨对战/角色/打开 RifleGirl 预览场景")]
        public static void OpenPreview() => EditorSceneManager.OpenScene(PreviewPath);
        static void CreatePreviewScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(CharacterPath));
            var light = new GameObject("ReferenceKeyLight", typeof(Light)); light.GetComponent<Light>().type = LightType.Directional;
            light.GetComponent<Light>().color = Color.white; light.GetComponent<Light>().intensity = 1; light.transform.rotation = Quaternion.Euler(50, -116.5f, 0);
            RenderSettings.ambientMode = AmbientMode.Skybox; RenderSettings.ambientIntensity = 1;
            RenderSettings.ambientSkyColor = new Color(.21223079f, .22696589f, .2581829f);
            RenderSettings.ambientEquatorColor = new Color(.1878208f, .19120172f, .19461787f);
            RenderSettings.ambientGroundColor = new Color(.046665095f, .042311423f, .035601325f);
            var camera = new GameObject("Main Camera", typeof(Camera)); camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 1.25f, 3.8f); camera.transform.LookAt(new Vector3(0, 1, 0));
            camera.GetComponent<Camera>().backgroundColor = new Color(.36f, .39f, .43f); camera.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            camera.GetComponent<Camera>().fieldOfView = 35;
            EditorSceneManager.SaveScene(scene, PreviewPath);
        }

        public static void ValidateInstalled()
        {
            var visual = Load<GameObject>(CharacterPath); var view = visual.GetComponent<InkCharacterView>();
            var player = Load<GameObject>(PlayerPath).GetComponent<PrototypePlayer>();
            if (view == null || view.Profile == null || view.Nozzle == null || view.LeftGrip == null || view.WeaponSocket == null || view.TeamMarker == null) throw new InvalidOperationException("RifleGirl 外观绑定缺失");
            if (player.BoundVisualPrefab != visual || player.CharacterView.BoundWeaponPrefab != Load<GameObject>(WeaponPath)) throw new InvalidOperationException("正式网络预制体绑定错误");
            if (!view.Animator.avatar.isHuman || !view.Animator.avatar.isValid || view.Animator.applyRootMotion) throw new InvalidOperationException("Humanoid 或根运动设置错误");
            foreach (var t in visual.GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) != 0) throw new InvalidOperationException("正式外观存在丢失脚本：" + t.name);
            var clips = view.Animator.runtimeAnimatorController.animationClips.Distinct().ToArray();
            var movementController=(AnimatorController)view.Animator.runtimeAnimatorController;
            if(!movementController.parameters.Any(p=>p.name=="MovePlayback"))throw new InvalidOperationException("缺少移动动画独立播放倍率");
            foreach(var layer in movementController.layers)foreach(var child in layer.stateMachine.states)
                if(child.state.speedParameterActive!=(child.state.name=="Locomotion"))throw new InvalidOperationException("步频倍率只能作用于移动状态");
            if (clips.Length != 10) throw new InvalidOperationException("正式动画数量应为 10，实际=" + clips.Length);
            foreach (var clip in clips)
            {
                string path = AssetDatabase.GetAssetPath(clip);
                if (!ClipNames.Contains(Path.GetFileNameWithoutExtension(path))) throw new InvalidOperationException("未批准的动作引用：" + path);
            }
            foreach (var path in new[] { CharacterPath, WeaponPath, PlayerPath })
                foreach (var dep in AssetDatabase.GetDependencies(path, true))
                    if (dep.Contains("_Incoming") || dep.Contains("/Jammo/")) throw new InvalidOperationException("残留临时或旧角色依赖：" + dep);
            foreach (var material in visual.GetComponentsInChildren<Renderer>(true).Where(r => r != view.TeamMarker && !(r is ParticleSystemRenderer)).SelectMany(r => r.sharedMaterials).Distinct())
                if (material == null || material.shader.name != "Toon/Toon") throw new InvalidOperationException("角色原 Toon 材质未完整绑定");
            if (!Clip("R_AimIdle_AutoShoot").isLooping) throw new InvalidOperationException("AutoShoot 未循环");
            ValidateGameplayMaterials();
            Report.Add("Avatar, source art, 10-clip whitelist, bindings, looping and missing scripts: PASS");
        }

        static void ValidateGameplayMaterials()
        {
            var originals = AssetDatabase.FindAssets("t:Material", new[] { SourceRoot }).ToDictionary(g => g[..8], g => Load<Material>(AssetDatabase.GUIDToAssetPath(g)));
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { Root + "/Materials" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var source = originals[Path.GetFileNameWithoutExtension(path).Split('_').Last()];
                var target = Load<Material>(path); var shader = source.shader;
                if (target.shader != shader || target.renderQueue != source.renderQueue ||
                    !target.shaderKeywords.Where(k => shader.keywordSpace.FindKeyword(k).isValid).OrderBy(k => k)
                        .SequenceEqual(source.shaderKeywords.Where(k => shader.keywordSpace.FindKeyword(k).isValid).OrderBy(k => k)))
                    throw new InvalidOperationException("Toon shader/queue/keyword drift: " + path);
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    string property = shader.GetPropertyName(i); bool same;
                    if (property == "_Is_Filter_LightColor") { same = target.GetFloat(property) == 1; }
                    else same = shader.GetPropertyType(i) switch
                    {
                        ShaderPropertyType.Color => target.GetColor(property) == source.GetColor(property),
                        ShaderPropertyType.Vector => target.GetVector(property) == source.GetVector(property),
                        ShaderPropertyType.Texture => target.GetTexture(property) == source.GetTexture(property) && target.GetTextureOffset(property) == source.GetTextureOffset(property) && target.GetTextureScale(property) == source.GetTextureScale(property),
                        ShaderPropertyType.Int => target.GetInteger(property) == source.GetInteger(property),
                        _ => target.GetFloat(property) == source.GetFloat(property)
                    };
                    if (!same) throw new InvalidOperationException("Unexpected Toon material change: " + path + " / " + property);
                }
            }
        }
    }
}
#endif
