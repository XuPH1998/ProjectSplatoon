using System.IO;
using Splatoon.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class EnemyOutlineBuilder
    {
        static EnemyOutlineBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            const string request = "Temp/AirSwimOutline/install";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(request)) return;
            File.Delete(request); Install();
        }
        [MenuItem("喷墨对战/表现/安装敌人红色描边")]
        public static void Install()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Splatoon/Runtime/Rendering/EnemyOutline.shader");
            foreach (string path in new[] { "Assets/Settings/PC_Renderer.asset", "Assets/Settings/Mobile_Renderer.asset" })
            {
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (renderer == null || shader == null) throw new System.InvalidOperationException("缺少描边 Shader 或渲染器：" + path);
                var feature = renderer.rendererFeatures.Find(f => f is EnemyOutlineFeature) as EnemyOutlineFeature;
                if (feature == null)
                {
                    feature = ScriptableObject.CreateInstance<EnemyOutlineFeature>(); feature.name = "Enemy red outline";
                    AssetDatabase.AddObjectToAsset(feature, renderer); renderer.rendererFeatures.Add(feature);
                }
                feature.OutlineShader = shader; feature.SetActive(true); feature.Create();
                // Keep URP's recovery map aligned with the serialized feature list.
                var serialized = new SerializedObject(renderer);
                var map = serialized.FindProperty("m_RendererFeatureMap"); map.arraySize = renderer.rendererFeatures.Count;
                for (int i = 0; i < map.arraySize; i++)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out string guid, out long id);
                    map.GetArrayElementAtIndex(i).longValue = id;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(feature); renderer.SetDirty(); EditorUtility.SetDirty(renderer);
            }
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory("Reports/AirSwimOutline");
            File.WriteAllText("Reports/AirSwimOutline/installed.txt", "PC and Mobile renderer features installed; " + System.DateTime.UtcNow.ToString("O"));
        }
    }
}
