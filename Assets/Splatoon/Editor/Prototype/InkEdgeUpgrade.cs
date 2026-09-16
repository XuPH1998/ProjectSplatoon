#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkEdgeUpgrade
    {
        [MenuItem("喷墨对战/内容/升级墨水边缘与掩体精度")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出运行模式");
            if (Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
                throw new InvalidOperationException("请先保存当前场景中的修改");
            InkLookUpgrade.LoadConfig();
            var scene = EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);
            var arena = UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();
            Apply(arena);
            // Geometry and ownership regions are unchanged. Only texture sizes affect this hash.
            arena.BakedTopology = arena.ComputeTopology(); EditorUtility.SetDirty(arena);
            TrainingGroundBuilder.Validate(arena);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[INK-EDGES] Upgrade complete. Eight covers at 64 texels/metre; AA unchanged.");
        }

        public static void Apply(PrototypeArena arena)
        {
            arena.RegisterSurfaces();
            foreach (int id in new[] { 27, 28, 29, 30, 33, 34, 35, 36 })
            {
                bool forward = (id & 1) != 0;
                if (!arena.Surfaces.TryGetValue(id, out var surface) ||
                    !surface.name.StartsWith(forward ? "ForwardCover_" : "CenterCover_", StringComparison.Ordinal))
                    throw new InvalidOperationException("墨水精度升级找不到预期掩体：" + id);
                int height = forward ? 640 : 384;
                if (!((surface.Resolution == 256 && surface.Height == height / 2) ||
                      (surface.Resolution == 512 && surface.Height == height)))
                    throw new InvalidOperationException("掩体纹理尺寸已被其他修改改变：" + surface.name);
            }
            foreach (int id in new[] { 27, 28, 29, 30, 33, 34, 35, 36 })
            {
                var surface = arena.Surfaces[id];
                surface.Resolution = 512; surface.ResolutionHeight = (id & 1) != 0 ? 640 : 384;
                EditorUtility.SetDirty(surface);
            }
            foreach (var material in arena.Surfaces.Values.Select(s => s.GetComponent<Renderer>().sharedMaterial).Distinct())
            {
                if (material == null || material.shader.name != "Splatoon/InkSurface") continue;
                material.SetFloat("_InkEdgeAAScale", 1);
                material.SetFloat("_InkEdgeNormalStrength", .12f);
                material.SetFloat("_InkEdgeSmoothness", .55f);
                EditorUtility.SetDirty(material);
            }
            if (PaintTextureMemory.PeakBytes(arena.Surfaces.Values) > 128L * 1048576)
                throw new InvalidOperationException("墨水精度升级超过 128 MiB 峰值预算");
        }
    }
}
#endif
