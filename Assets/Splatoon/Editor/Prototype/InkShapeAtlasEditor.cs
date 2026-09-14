#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Painting;

namespace Splatoon.Editor
{
    public static class InkShapeAtlasEditor
    {
        public const string Root = "Assets/GameResource/Effects/Ink/Textures";
        public const string Original = Root + "/InkSplatAtlas-Reference.png";
        private const string OriginalHash = "49da548eddd6a0f7e820869a715658bed4adf4f3b072b3a662572dc81337f839";
        public static Texture2D Load() => AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath)
            ?? throw new InvalidOperationException("请先生成并打包 32 款落墨图集");
        public static void ImportMask(string path, int size)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.isReadable = true;
            importer.sRGBTexture = false; importer.mipmapEnabled = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = false;
            importer.npotScale = TextureImporterNPOTScale.None; importer.maxTextureSize = size;
            importer.filterMode = FilterMode.Bilinear; importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            foreach (string platform in new[] { "Standalone", "Android", "iPhone" }) importer.ClearPlatformTextureSettings(platform);
            importer.SaveAndReimport();
        }
        private static Color32[] CellPixels(Texture2D source, int index, bool original)
        {
            const int size = InkShapeAtlas.CellSize;
            var result = new Color32[size * size];
            var raw = original ? source.GetPixels32() : null;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                if (original) result[y * size + x] = raw[(index / 4 * size + y) * source.width + index % 4 * size + x];
                else
                {
                    var color = (Color32)source.GetPixelBilinear((x + .5f) / size, (y + .5f) / size);
                    result[y * size + x] = new Color32(0, 0, 0, color.a);
                }
            }
            return result;
        }
        private static Color32[] Compose(bool exportIndividuals)
        {
            var old = AssetDatabase.LoadAssetAtPath<Texture2D>(Original);
            if (old == null || old.width != 1024 || old.height != 1024) throw new InvalidOperationException("原 16 款图集不存在");
            var pixels = new Color32[InkShapeAtlas.Width * InkShapeAtlas.Height];
            if (exportIndividuals) Directory.CreateDirectory(Root + "/Shapes");
            for (int index = 0; index < InkShapeAtlas.Count; index++)
            {
                var source = index < 16 ? old : AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Sources/InkSplat-{index:D2}.png");
                if (source == null) throw new InvalidOperationException($"缺少落墨原图 {index}");
                var cell = CellPixels(source, index, index < 16);
                for (int y = 0; y < InkShapeAtlas.CellSize; y++)
                    Array.Copy(cell, y * InkShapeAtlas.CellSize, pixels,
                        (index / InkShapeAtlas.Columns * InkShapeAtlas.CellSize + y) * InkShapeAtlas.Width + index % InkShapeAtlas.Columns * InkShapeAtlas.CellSize, InkShapeAtlas.CellSize);
                if (exportIndividuals && index >= 16)
                {
                    var individual = new Texture2D(InkShapeAtlas.CellSize, InkShapeAtlas.CellSize, TextureFormat.RGBA32, false, true);
                    try { individual.SetPixels32(cell); individual.Apply(); File.WriteAllBytes($"{Root}/Shapes/InkSplat-{index:D2}.png", individual.EncodeToPNG()); }
                    finally { UnityEngine.Object.DestroyImmediate(individual); }
                }
            }
            return pixels;
        }
        [MenuItem("喷墨对战/内容/打包32款落墨图集")]
        public static void Build()
        {
            for (int index = 16; index < InkShapeAtlas.Count; index++) ImportMask($"{Root}/Sources/InkSplat-{index:D2}.png", 1024);
            var atlas = new Texture2D(InkShapeAtlas.Width, InkShapeAtlas.Height, TextureFormat.RGBA32, false, true);
            try { atlas.SetPixels32(Compose(true)); atlas.Apply(); File.WriteAllBytes(InkShapeAtlas.AssetPath, atlas.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(atlas); }
            ImportMask(InkShapeAtlas.AssetPath, 2048);
            for (int index = 16; index < InkShapeAtlas.Count; index++) ImportMask($"{Root}/Shapes/InkSplat-{index:D2}.png", 256);
            InkShapeAtlas.Reset(); Validate();
            foreach (string path in new[] { TrainingGroundBuilder.ScenePath, "Assets/GameResource/Gameplay/Prototype/PrototypeArena.unity" })
            {
                var scene = EditorSceneManager.OpenScene(path); bool changed = false;
                foreach (var surface in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PaintSurface>(true)))
                    if (surface.ShapeAtlas != Load()) { surface.ShapeAtlas = Load(); EditorUtility.SetDirty(surface); changed = true; }
                if (changed) { EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); }
            }
            AssetDatabase.SaveAssets();
        }
        [MenuItem("喷墨对战/验证/不规则落墨图集")]
        public static void Validate()
        {
            using var sha = SHA256.Create();
            var originalHash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Original))).Replace("-", "").ToLowerInvariant();
            if (originalHash != OriginalHash) throw new InvalidOperationException("原有 16 款素材发生变化");
            var atlas = Load(); var importer = (TextureImporter)AssetImporter.GetAtPath(InkShapeAtlas.AssetPath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(InkShapeAtlas.AssetPath)) || importer.textureCompression != TextureImporterCompression.Uncompressed)
                throw new InvalidOperationException("图集 GUID 或压缩设置错误");
            var expected = Compose(false); var actual = atlas.GetPixels32();
            if (expected.Length != actual.Length || expected.Where((c, i) => c.a != actual[i].a).Any()) throw new InvalidOperationException("图集 Alpha 与源素材不一致，请重新打包");
            InkShapeAtlas.Configure(atlas, true);
            Debug.Log($"[INK-SHAPE] count={InkShapeAtlas.Count} size={atlas.width}x{atlas.height} alphaLayoutHash={InkShapeAtlas.ContentHash} PASS");
        }
        [MenuItem("喷墨对战/内容/绑定不规则落墨图集到当前地图")]
        public static void BindCurrentScene()
        {
            Validate(); var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var surfaces = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PaintSurface>(true)).ToArray();
            if (surfaces.Length == 0) throw new InvalidOperationException("当前场景没有 PaintSurface");
            Undo.RecordObjects(surfaces, "Bind ink shape atlas");
            foreach (var surface in surfaces) { surface.ShapeAtlas = Load(); EditorUtility.SetDirty(surface); }
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        }
    }
}
#endif
