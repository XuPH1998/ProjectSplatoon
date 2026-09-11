#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Netcode;
using Splatoon.Combat;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkMigrationBuilder
    {
        const string Incoming = "Assets/Art/_Incoming/InkReference/MixAndJam.unity";
        const string CharacterPath = "Assets/GameResource/Characters/Jammo/Prefabs/JammoVisual.prefab";
        const string WeaponPath = "Assets/GameResource/Weapons/Splattershot/Prefabs/Splattershot.prefab";
        const string StreamPath = "Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab";
        const string ImpactPath = "Assets/GameResource/Effects/Ink/Prefabs/InkImpact.prefab";
        const string ArenaPath = "Assets/GameResource/Gameplay/Prototype/PrototypeArena.unity";
        const string PlayerPath = "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab";
        static T Load<T>(string path) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new InvalidOperationException("资源缺失：" + path);
        static Transform Find(GameObject root, string name) => root.GetComponentsInChildren<Transform>(true).First(t => t.name == name);
        static void Folder(string path) { Directory.CreateDirectory(path); AssetDatabase.Refresh(); }
        static void Unpack(GameObject root) { if (PrefabUtility.IsPartOfPrefabInstance(root)) PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction); }
        static void StripMissing(GameObject root) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject); }
        static void Layer(GameObject root, int layer) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer; }
        public static void InstallAndBuild() { Install(); PrototypeBuilder.BuildWindows(); }
        public static void Install()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(Incoming)) throw new InvalidOperationException("首次安装前运行 Tools/InkMigration/import_reference.py");
            SetInkLayer();
            Folder(Path.GetDirectoryName(CharacterPath)); Folder(Path.GetDirectoryName(WeaponPath)); Folder(Path.GetDirectoryName(StreamPath));
            var scene = EditorSceneManager.OpenScene(Incoming, OpenSceneMode.Single);
            var source = scene.GetRootGameObjects().First(g => g.name == "Jammo_Player");
            Unpack(source); StripMissing(source);
            foreach (var collider in source.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            var main = Find(source, "Main Visual Particle");
            Vector3 muzzle = source.transform.InverseTransformPoint(main.position);
            SaveParticles(Find(source, "Splash Particle").gameObject, ImpactPath, false);
            var stream = UnityEngine.Object.Instantiate(main.gameObject); stream.name = "InkStream";
            foreach (Transform child in stream.transform.Cast<Transform>().ToArray()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            SaveParticles(stream, StreamPath, true); UnityEngine.Object.DestroyImmediate(stream);
            UnityEngine.Object.DestroyImmediate(main.gameObject);
            var weapon = Find(source, "Splattershot");
            PrefabUtility.SaveAsPrefabAssetAndConnect(weapon.gameObject, WeaponPath, InteractionMode.AutomatedAction);
            var animator = source.GetComponent<Animator>(); animator.applyRootMotion = false;
            // The reference scene points at an Avatar GUID absent from the reference repository.
            // Its clips are generic transform curves, so rebuild a matching generic Avatar.
            string avatarPath = "Assets/GameResource/Characters/Jammo/Models/JammoAvatar.asset";
            var avatar = AvatarBuilder.BuildGenericAvatar(source, "jammo_mixamo_rig"); avatar.name = "JammoAvatar";
            var savedAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
            if (savedAvatar == null) { AssetDatabase.CreateAsset(avatar, avatarPath); savedAvatar = avatar; }
            else { EditorUtility.CopySerialized(avatar, savedAvatar); UnityEngine.Object.DestroyImmediate(avatar); }
            animator.avatar = savedAvatar;
            animator.runtimeAnimatorController = CreateAnimator();
            source.name = "JammoVisual"; source.tag = "Untagged"; Layer(source, 8);
            var view = source.AddComponent<InkCharacterView>(); view.Animator = animator; view.AimRoot = Find(source, "Parent"); view.Nozzle = Find(source, "Nozzle");
            view.BoundWeaponPrefab = Load<GameObject>(WeaponPath);
            view.TeamRenderers = source.GetComponentsInChildren<Renderer>(true).Where(r => r.name == "scarf_low" || r.name == "head_mohawk_low" || r.name == "tank").ToArray();
            if (view.TeamRenderers.Length == 0) throw new InvalidOperationException("人物队色部件未找到");
            var swim = new GameObject("SwimInk"); swim.transform.SetParent(source.transform, false); swim.transform.localPosition = Vector3.up * .04f;
            var swimPS = swim.AddComponent<ParticleSystem>(); var swimMain = swimPS.main; swimMain.loop = true; swimMain.playOnAwake = false; swimMain.startSpeed = .1f; swimMain.startSize = .2f; swimMain.startLifetime = .3f;
            var swimEmission = swimPS.emission; swimEmission.rateOverTime = 12; swim.GetComponent<ParticleSystemRenderer>().sharedMaterial = Load<Material>("Assets/GameResource/Effects/Ink/Materials/InkParticle.mat");
            view.SwimEffect = swimPS; swimPS.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var visualPrefab = PrefabUtility.SaveAsPrefabAsset(source, CharacterPath);
            Debug.Log($"[InkInstall] Muzzle={muzzle}, renderers={source.GetComponentsInChildren<Renderer>().Length}, rigs={source.GetComponentsInChildren<Rig>().Length}");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var player = new GameObject("PrototypePlayer"); player.layer = 8; player.AddComponent<NetworkObject>();
            var cc = player.AddComponent<CharacterController>(); cc.height = 1.8f; cc.radius = .35f; cc.center = Vector3.up * .9f; cc.stepOffset = .3f; cc.skinWidth = .03f;
            var runtime = player.AddComponent<PrototypePlayer>();
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(visualPrefab, player.transform); visual.transform.localPosition = Vector3.zero; visual.transform.localRotation = Quaternion.identity;
            runtime.Visual = visual.transform; runtime.CharacterView = visual.GetComponent<InkCharacterView>();
            runtime.BoundVisualPrefab = visualPrefab; runtime.SimulationAimPivot = runtime.CharacterView.AimRoot.localPosition;
            var logicalMuzzle = new GameObject("SimulationMuzzle"); logicalMuzzle.transform.SetParent(player.transform, false); logicalMuzzle.transform.localPosition = muzzle; runtime.SimulationMuzzle = logicalMuzzle.transform;
            PrefabUtility.SaveAsPrefabAsset(player, PlayerPath); UnityEngine.Object.DestroyImmediate(player);
            UpgradeArena(); ConfigureRenderer(); RefreshEffects(); PrototypeBuilder.ConfigureAddressables();
            AssetDatabase.SaveAssets(); ValidateInstalled();
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            // Temporary scene has served its purpose; no shipped asset may depend on it.
            AssetDatabase.DeleteAsset("Assets/Art/_Incoming/InkReference");
            Debug.Log("[InkInstall] Formal character, weapon, effects, surfaces and renderer installed.");
        }
        static void SaveParticles(GameObject original, string path, bool stream)
        {
            var go = UnityEngine.Object.Instantiate(original); go.transform.SetParent(null); go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); go.transform.localScale = Vector3.one;
            StripMissing(go); go.name = stream ? "InkStream" : "InkImpact"; Layer(go, 9);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main; main.playOnAwake = false; main.loop = stream; main.stopAction = ParticleSystemStopAction.None;
                var collision = ps.collision; collision.enabled = false;
                var renderer = ps.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = Load<Material>("Assets/GameResource/Effects/Ink/Materials/InkParticle.mat");
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            if (stream) { var sub = go.GetComponent<ParticleSystem>().subEmitters; sub.enabled = false; }
            PrefabUtility.SaveAsPrefabAsset(go, path); UnityEngine.Object.DestroyImmediate(go);
        }
        public static void RefreshEffects()
        {
            var material = Load<Material>("Assets/GameResource/Effects/Ink/Materials/InkParticle.mat");
            material.SetColor("_BaseColor", Color.white); material.SetColor("_Color", Color.white); EditorUtility.SetDirty(material);
            var root = PrefabUtility.LoadPrefabContents(ImpactPath);
            try
            {
                var main = root.GetComponent<ParticleSystem>().main; main.startSize = .5f;
                foreach (Transform child in root.transform) child.localPosition = Vector3.zero;
                PrefabUtility.SaveAsPrefabAsset(root, ImpactPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
        }
        static AnimatorController CreateAnimator()
        {
            string path = "Assets/GameResource/Characters/Jammo/Animations/JammoCombat.controller";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            foreach (string name in new[] { "X", "Y", "Blend", "VerticalSpeed" }) controller.AddParameter(name, AnimatorControllerParameterType.Float);
            controller.AddParameter("shooting", AnimatorControllerParameterType.Bool); controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool); controller.AddParameter("Land", AnimatorControllerParameterType.Trigger);
            var machine = controller.layers[0].stateMachine; var locomotion = machine.AddState("Strafing"); machine.defaultState = locomotion;
            var tree = new BlendTree { name = "Directional locomotion", blendType = BlendTreeType.FreeformDirectional2D, blendParameter = "X", blendParameterY = "Y", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller); locomotion.motion = tree;
            string root = "Assets/GameResource/Characters/Jammo/Animations/";
            tree.AddChild(Load<AnimationClip>(root + "Mixamo-Gun/Rifle Aim Idle.anim"), Vector2.zero);
            tree.AddChild(Load<AnimationClip>(root + "Mixamo-Gun/Rifle Run Forward.anim"), Vector2.up);
            tree.AddChild(Load<AnimationClip>(root + "Mixamo-Gun/Rifle Run Backwards.anim"), Vector2.down);
            tree.AddChild(Load<AnimationClip>(root + "Mixamo-Gun/Rifle Run Left.anim"), Vector2.left);
            tree.AddChild(Load<AnimationClip>(root + "Mixamo-Gun/Rifle Run Right.anim"), Vector2.right);
            var jump = machine.AddState("Jump"); jump.motion = RetargetClip(root + "Jump.anim", root + "JammoJump.anim");
            var land = machine.AddState("Land"); land.motion = RetargetClip(root + "Falling To Landing.anim", root + "JammoLand.anim");
            var takeoff = locomotion.AddTransition(jump); takeoff.hasExitTime = false; takeoff.duration = .08f; takeoff.AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");
            var landing = jump.AddTransition(land); landing.hasExitTime = false; landing.duration = .08f; landing.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            var resume = land.AddTransition(locomotion); resume.hasExitTime = true; resume.exitTime = .5f; resume.duration = .12f;
            EditorUtility.SetDirty(controller); return controller;
        }
        static AnimationClip RetargetClip(string source, string destination)
        {
            var original = Load<AnimationClip>(source); var clip = UnityEngine.Object.Instantiate(original); clip.name = Path.GetFileNameWithoutExtension(destination);
            foreach (var binding in AnimationUtility.GetCurveBindings(original))
            {
                if (!binding.path.StartsWith("Armature.001", StringComparison.Ordinal)) continue;
                AnimationUtility.SetEditorCurve(clip, binding, null);
                var mapped = binding; mapped.path = "jammo_mixamo_rig" + binding.path.Substring("Armature.001".Length);
                AnimationUtility.SetEditorCurve(clip, mapped, AnimationUtility.GetEditorCurve(original, binding));
            }
            var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(destination);
            if (old != null) { EditorUtility.CopySerialized(clip, old); UnityEngine.Object.DestroyImmediate(clip); return old; }
            AssetDatabase.CreateAsset(clip, destination); return clip;
        }
        static void UpgradeArena()
        {
            var scene = EditorSceneManager.OpenScene(ArenaPath, OpenSceneMode.Single);
            var arena = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PrototypeArena>()).Single(); arena.LayoutVersion = 2;
            string meshRoot = "Assets/GameResource/Environment/Ink/Meshes"; Folder(meshRoot);
            var painters = arena.GetComponentsInChildren<MeshFilter>().Where(f => !f.name.StartsWith("出生标记")).OrderBy(f => f.name, StringComparer.Ordinal).ToArray();
            int id = 0;
            foreach (var filter in painters)
            {
                var mesh = UnityEngine.Object.Instantiate(filter.sharedMesh); mesh.name = "PaintAtlas_" + (++id);
                mesh.uv2 = BuildPaintUV(mesh, filter.name == "地面");
                string meshPath = meshRoot + "/" + mesh.name + ".asset";
                var old = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath); if (old != null) { EditorUtility.CopySerialized(mesh, old); UnityEngine.Object.DestroyImmediate(mesh); mesh = old; } else AssetDatabase.CreateAsset(mesh, meshPath);
                filter.sharedMesh = mesh;
                var surface = filter.GetComponent<PaintSurface>() ?? filter.gameObject.AddComponent<PaintSurface>(); surface.SurfaceId = id; surface.Scores = filter.name == "地面"; surface.Resolution = surface.Scores ? 1024 : 512;
                surface.PainterShader = Shader.Find("Splatoon/InkTexturePainter"); surface.ExtendShader = Shader.Find("TNTC/ExtendIslands");
                var material = UnityEngine.Object.Instantiate(Load<Material>("Assets/GameResource/Environment/Ink/Materials/PaintableWall.mat")); material.name = "Surface_" + id;
                var original = filter.GetComponent<MeshRenderer>().sharedMaterial;
                material.SetColor("Color_863351f5ceea4c998ef51baab6dd758b", original.HasProperty("_BaseColor") ? original.GetColor("_BaseColor") : Color.gray);
                string materialPath = "Assets/GameResource/Environment/Ink/Materials/" + material.name + ".mat";
                var oldMat = AssetDatabase.LoadAssetAtPath<Material>(materialPath); if (oldMat != null) { EditorUtility.CopySerialized(material, oldMat); UnityEngine.Object.DestroyImmediate(material); material = oldMat; } else AssetDatabase.CreateAsset(material, materialPath);
                filter.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            if (arena.SpawnPoints != null) foreach (var point in arena.SpawnPoints) if (point != null) UnityEngine.Object.DestroyImmediate(point.gameObject);
            arena.SpawnPoints = new Transform[4];
            for (int i = 0; i < 4; i++) { var go = new GameObject("Spawn_" + (i + 1)); go.transform.SetParent(arena.transform, false); go.transform.position = new Vector3(i % 2 == 0 ? -4 : 4, .1f, i < 2 ? -13 : 13); arena.SpawnPoints[i] = go.transform; }
            var presentation = arena.GetComponent<InkPresentation>() ?? arena.gameObject.AddComponent<InkPresentation>();
            presentation.StreamPrefab = Load<GameObject>(StreamPath).GetComponent<ParticleSystem>(); presentation.ImpactPrefab = Load<GameObject>(ImpactPath).GetComponent<ParticleSystem>();
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        }
        static Vector2[] BuildPaintUV(Mesh mesh, bool ground)
        {
            var vertices = mesh.vertices; var normals = mesh.normals; var uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 n = normals[i], p = vertices[i]; int face; Vector2 at;
                if (n.y > .5f) { face = 0; at = new Vector2(p.x + .5f, p.z + .5f); }
                else if (n.y < -.5f) { face = 1; at = new Vector2(p.x + .5f, p.z + .5f); }
                else if (n.x > .5f) { face = 2; at = new Vector2(p.z + .5f, p.y + .5f); }
                else if (n.x < -.5f) { face = 3; at = new Vector2(p.z + .5f, p.y + .5f); }
                else if (n.z > .5f) { face = 4; at = new Vector2(p.x + .5f, p.y + .5f); }
                else { face = 5; at = new Vector2(p.x + .5f, p.y + .5f); }
                if (ground) uv[i] = face == 0 ? new Vector2(.005f, .005f) + at * .99f : new Vector2(-2 - face, -2);
                else uv[i] = new Vector2(face % 3 / 3f + .006f, face / 3 / 2f + .006f) + Vector2.Scale(at, new Vector2(1f / 3 - .012f, .5f - .012f));
            }
            return uv;
        }
        static void SetInkLayer()
        {
            var tags = new SerializedObject(Load<UnityEngine.Object>("ProjectSettings/TagManager.asset")); var layers = tags.FindProperty("layers");
            if (!string.IsNullOrEmpty(layers.GetArrayElementAtIndex(9).stringValue) && layers.GetArrayElementAtIndex(9).stringValue != "InkVisual") throw new InvalidOperationException("Layer 9 已被其他用途占用");
            layers.GetArrayElementAtIndex(9).stringValue = "InkVisual"; tags.ApplyModifiedPropertiesWithoutUndo();
        }
        static void ConfigureRenderer()
        {
            var renderer = Load<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            var feature = renderer.rendererFeatures.OfType<RenderMetaballsScreenSpace>().FirstOrDefault();
            if (feature == null) { feature = ScriptableObject.CreateInstance<RenderMetaballsScreenSpace>(); feature.name = "InkMetaballs"; AssetDatabase.AddObjectToAsset(feature, renderer); renderer.rendererFeatures.Add(feature); }
            feature.Event = RenderPassEvent.AfterRenderingTransparents; feature.FilterSettings.LayerMask = 1 << 9; feature.FilterSettings.RenderQueueType = RenderQueueType.Transparent;
            feature.BlitMaterial = Load<Material>("Assets/GameResource/Effects/Ink/Shaders/Shader Graphs_StepAndClip.mat"); feature.WriteDepthMaterial = Load<Material>("Assets/GameResource/Effects/Ink/Shaders/Shader Graphs_WriteToDepth.mat"); feature.BlurPasses = 3; feature.BlurDistance = .54f;
            var serialized = new SerializedObject(renderer); serialized.FindProperty("m_OpaqueLayerMask").intValue &= ~(1 << 9); serialized.FindProperty("m_TransparentLayerMask").intValue &= ~(1 << 9); serialized.FindProperty("m_RenderingMode").intValue = 0; serialized.ApplyModifiedPropertiesWithoutUndo();
            renderer.SetDirty(); EditorUtility.SetDirty(feature); EditorUtility.SetDirty(renderer);
            // Explicit shader references prevent stripping of shaders formerly found only by name.
            feature.CopyDepthShader = Load<Shader>("Assets/GameResource/Effects/Ink/Shaders/CopyDepth.shader"); feature.BlurShader = Load<Shader>("Assets/GameResource/Effects/Ink/Shaders/KawaseBlur.shader");
        }
        public static void ValidateInstalled()
        {
            foreach (string path in new[] { CharacterPath, WeaponPath, StreamPath, ImpactPath, PlayerPath, TrainingGroundBuilder.ScenePath })
            {
                Load<UnityEngine.Object>(path);
                foreach (var dependency in AssetDatabase.GetDependencies(path, true))
                    if (dependency.Contains("_Incoming")) throw new InvalidOperationException("正式资源仍引用临时导入目录：" + dependency);
            }
            var player = Load<GameObject>(PlayerPath).GetComponent<PrototypePlayer>();
            if (player.CharacterView == null || player.SimulationMuzzle == null || player.CharacterView.Animator.avatar == null) throw new InvalidOperationException("角色模型或枪口绑定缺失");
            foreach (var go in Load<GameObject>(CharacterPath).GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject) != 0) throw new InvalidOperationException("正式角色存在丢失脚本：" + go.name);
            Debug.Log("[InkInstall] Resource validation passed.");
        }
    }
}
#endif
