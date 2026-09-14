#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using System.Security.Cryptography;

namespace Splatoon.Editor
{
    public static class InkShapeAtlasEditor
    {
        const string AssetPath = "Assets/GameResource/Effects/Ink/Textures/InkSplatAtlas-Reference.png";
        [MenuItem("喷墨对战/验证/不规则落墨图集")]
        public static void Validate()
        {
            var importer = AssetImporter.GetAtPath(AssetPath) as TextureImporter;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetPath);
            if (texture == null || texture.width != 1024 || texture.height != 1024) throw new InvalidOperationException("落墨图集必须为 1024x1024");
            if (importer == null || !importer.isReadable || importer.mipmapEnabled || importer.sRGBTexture) throw new InvalidOperationException("落墨图集导入设置必须为可读、无 Mipmap、Linear");
            var hash = BitConverter.ToString(SHA256.Create().ComputeHash(File.ReadAllBytes(AssetPath))).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(hash, Splatoon.Painting.InkShapeAtlas.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"落墨图集哈希不匹配：{hash}");
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
            {
                bool hasAlpha = false;
                for (int py = y * 256 + 16; py < (y + 1) * 256 - 16 && !hasAlpha; py += 8)
                    for (int px = x * 256 + 16; px < (x + 1) * 256 - 16; px += 8)
                        if (texture.GetPixel(px, py).a > .05f) { hasAlpha = true; break; }
                if (!hasAlpha) throw new InvalidOperationException($"落墨图集第 {y * 4 + x} 格为空");
                for (int p = 0; p < 256; p += 4)
                {
                    if (texture.GetPixel(x * 256 + p, y * 256).a > .01f || texture.GetPixel(x * 256 + p, y * 256 + 255).a > .01f ||
                        texture.GetPixel(x * 256, y * 256 + p).a > .01f || texture.GetPixel(x * 256 + 255, y * 256 + p).a > .01f)
                        throw new InvalidOperationException($"落墨图集第 {y * 4 + x} 格边界不透明，可能发生贴图溢色");
                }
            }
            Debug.Log($"[INK-SHAPE] atlas={AssetPath} size={texture.width}x{texture.height} hash={Splatoon.Painting.InkShapeAtlas.ContentHash} PASS");
        }

        [MenuItem("喷墨对战/内容/绑定不规则落墨图集到当前地图")]
        public static void BindCurrentScene()
        {
            Validate();
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetPath);
            var surfaces = UnityEngine.Object.FindObjectsByType<Splatoon.Painting.PaintSurface>(FindObjectsSortMode.None);
            foreach (var surface in surfaces) { surface.ShapeAtlas = atlas; EditorUtility.SetDirty(surface); }
            if (surfaces.Length == 0) throw new InvalidOperationException("当前场景没有 PaintSurface");
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            Debug.Log($"[INK-SHAPE] bound atlas={atlas.name} surfaces={surfaces.Length} PASS");
        }
    }
}
#endif
