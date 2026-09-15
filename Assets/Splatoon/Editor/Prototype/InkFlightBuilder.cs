#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;

namespace Splatoon.Editor
{
    public static class InkFlightBuilder
    {
        public const string Root = "Assets/GameResource/Effects/Ink/";
        public const string ProfilePath = Root + "InkFlightProfile.asset";
        [MenuItem("喷墨对战/内容/配置枪口和飞行墨水")]
        public static void Build()
        {
            var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layer = tags.FindProperty("layers").GetArrayElementAtIndex(InkFlightProfile.Layer);
            if (!string.IsNullOrEmpty(layer.stringValue) && layer.stringValue != "InkFlight") throw new InvalidOperationException("Layer 11 is already used");
            layer.stringValue = "InkFlight"; tags.ApplyModifiedPropertiesWithoutUndo();
            var profile = AssetDatabase.LoadAssetAtPath<InkFlightProfile>(ProfilePath);
            if (profile == null) { profile = ScriptableObject.CreateInstance<InkFlightProfile>(); AssetDatabase.CreateAsset(profile, ProfilePath); }
            profile.FlightPrefab = Load("InkFlight"); profile.MuzzlePrefab = Load("InkMuzzle");
            EditorUtility.SetDirty(profile);
            var shader = Shader.Find("Hidden/Splatoon/InkFlightComposite");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Flight composite shader failed to import");
            string matPath = Root + "Materials/InkFlightComposite.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, matPath); }
            material.shader = shader; material.SetFloat("_PreserveHue", 1); material.SetFloat("_CheckOcclusion", 1);
            EditorUtility.SetDirty(material);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            RenderMetaballsScreenSpace feature = null;
            foreach (var f in renderer.rendererFeatures) if (f.name == "InkFlightMetaballs") feature = f as RenderMetaballsScreenSpace;
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<RenderMetaballsScreenSpace>(); feature.name = "InkFlightMetaballs";
                AssetDatabase.AddObjectToAsset(feature, renderer); renderer.rendererFeatures.Add(feature);
            }
            feature.Event = RenderPassEvent.AfterRenderingTransparents;
            feature.PassTag = "InkFlightMetaballs"; feature.FlightComposite = true;
            feature.FilterSettings.LayerMask = 1 << InkFlightProfile.Layer;
            feature.FilterSettings.RenderQueueType = RenderQueueType.Transparent;
            feature.BlitMaterial = material;
            feature.WriteDepthMaterial = AssetDatabase.LoadAssetAtPath<Material>(Root + "Shaders/Shader Graphs_WriteToDepth.mat");
            feature.CopyDepthShader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "Shaders/CopyDepth.shader");
            feature.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "Shaders/KawaseBlur.shader");
            feature.BlurPasses = 3; feature.BlurDistance = .54f; feature.SetActive(true); feature.Create();
            var data = new SerializedObject(renderer);
            foreach (string property in new[] { "m_OpaqueLayerMask", "m_TransparentLayerMask" })
            { var mask = data.FindProperty(property); mask.intValue &= ~(1 << InkFlightProfile.Layer); }
            data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(feature); EditorUtility.SetDirty(renderer);
            // Patch only the existing presentation binding. Never regenerate the training map.
            foreach (string path in new[] { TrainingGroundBuilder.ScenePath, "Assets/GameResource/Gameplay/Prototype/PrototypeArena.unity" })
            {
                var scene = EditorSceneManager.OpenScene(path);
                foreach (var effects in UnityEngine.Object.FindObjectsByType<InkPresentation>(FindObjectsSortMode.None))
                { effects.FlightProfile = profile; EditorUtility.SetDirty(effects); }
                EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[INK-FLIGHT] Source flight/muzzle profile and isolated pass installed; existing map and impact assets retained.");
        }
        static ParticleSystem Load(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/" + name + ".prefab").GetComponent<ParticleSystem>();
        public static void BuildBatch() { Build(); EditorApplication.Exit(0); }
    }
}
#endif
