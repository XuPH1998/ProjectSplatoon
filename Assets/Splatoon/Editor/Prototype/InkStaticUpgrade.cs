#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkStaticUpgrade
    {
        public const string Output = "Reports/InkStaticUpgrade";
        const string FinePath = "Assets/GameResource/Environment/Ink/Textures/InkSurfaceFineNormal.jpg";
        const string ProbeRoot = "InkStaticReflections";
        [MenuItem("喷墨对战/墨迹静态表现/生成并应用")]
        public static void Apply()
        {
            if (Application.isPlaying) throw new InvalidOperationException("生成资源前请退出运行模式");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("请先保存当前场景修改");
            Directory.CreateDirectory(Output);
            GenerateAtlas();
            if (!File.Exists(FinePath)) AssetDatabase.CopyAsset("Assets/GameResource/Environment/Ink/Textures/splatNormal.jpg", FinePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(FinePath);
            importer.textureType = TextureImporterType.NormalMap; importer.mipmapEnabled = true; importer.sRGBTexture = false;
            importer.wrapMode = TextureWrapMode.Repeat; importer.filterMode = FilterMode.Trilinear; importer.anisoLevel = 4; importer.SaveAndReimport();
            Directory.CreateDirectory("Assets/Splatoon/Resources"); AssetDatabase.Refresh();
            var profile = AssetDatabase.LoadAssetAtPath<InkAppearanceProfile>(InkAppearanceProfile.AssetPath);
            if (profile == null) { profile = ScriptableObject.CreateInstance<InkAppearanceProfile>(); AssetDatabase.CreateAsset(profile, InkAppearanceProfile.AssetPath); }
            profile.DetailAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(InkAppearanceProfile.AtlasPath);
            profile.FineNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(FinePath); profile.Enabled = true;
            EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);
            ConfigureProbes(); SetEnabled(true); AssetDatabase.SaveAssets();
            long assetBytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(profile.DetailAtlas) + UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(profile.FineNormal);
            foreach (var probe in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
                if (probe.transform.parent != null && probe.transform.parent.name == ProbeRoot && probe.customBakedTexture != null)
                    assetBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(probe.customBakedTexture);
            File.WriteAllText(Output + "/asset-evidence.txt", $"Atlas: {InkAppearanceProfile.ContentHash}\nAccumulation version: {InkAppearanceProfile.AccumulationVersion}\n32 tiles x 256 x 256; RG height/roughness; source alpha unchanged.\nUnity reported profile textures + 3 cubemaps: {assetBytes} bytes (separate from paint RT budget).\n");
        }
        static void GenerateAtlas()
        {
            var shape = AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
            InkShapeAtlas.Configure(shape); var alpha = shape.GetPixels32(); var pixels = new Color32[alpha.Length];
            for (int tile = 0; tile < 32; tile++)
            {
                int ox = tile % 8 * 256, oy = tile / 8 * 256; double mass = 0, mx = 0, my = 0, xx = 0, yy = 0, xy = 0;
                for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { float w = alpha[(oy + y) * 2048 + ox + x].a / 255f; mass += w; mx += x * w; my += y * w; }
                float cx = (float)(mx / mass), cy = (float)(my / mass);
                for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { float w = alpha[(oy + y) * 2048 + ox + x].a / 255f, dx = x - cx, dy = y - cy; xx += dx * dx * w; yy += dy * dy * w; xy += dx * dy * w; }
                float angle = .5f * Mathf.Atan2((float)(2 * xy), (float)(xx - yy)), co = Mathf.Cos(angle), si = Mathf.Sin(angle);
                uint seed = InkShapeAtlas.Hash((uint)tile + 0x59a12u); float phase = (seed & 65535) / 65535f * 10;
                for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
                {
                    int i = (oy + y) * 2048 + ox + x;
                    float px = (x - cx) / 128, py = (y - cy) / 128, u = px * co + py * si, v = -px * si + py * co;
                    // Long shallow ridges follow each silhouette's principal direction. No per-stamp raised rim.
                    float broad = Mathf.PerlinNoise(u * 1.7f + phase + 20, v * 1.7f + tile + 30) - .5f;
                    float folds = Mathf.Sin(v * 24 + Mathf.Sin(u * 5 + phase) * 1.8f + phase) * .12f;
                    float h = Mathf.Clamp01(.5f + broad * .7f + folds);
                    float roughness = Mathf.Clamp01(.5f + broad * .35f + (Mathf.PerlinNoise(u * 5 + 50, v * 5 + phase + 50) - .5f) * .25f);
                    pixels[i] = new Color32((byte)Mathf.RoundToInt(h * 255), (byte)Mathf.RoundToInt(roughness * 255), 0, 255);
                }
            }
            var texture = new Texture2D(2048, 1024, TextureFormat.RGBA32, false, true); texture.SetPixels32(pixels); texture.Apply();
            File.WriteAllBytes(InkAppearanceProfile.AtlasPath, texture.EncodeToPNG());
            // Contact sheet exposes relief and the original silhouette together for authoring review.
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(pixels[i].r, pixels[i].r, pixels[i].r, alpha[i].a);
            texture.SetPixels32(pixels); texture.Apply(); File.WriteAllBytes(Output + "/detail-contact-sheet.png", texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(InkAppearanceProfile.AtlasPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(InkAppearanceProfile.AtlasPath);
            importer.textureType = TextureImporterType.Default; importer.sRGBTexture = false; importer.isReadable = true; importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear; importer.wrapMode = TextureWrapMode.Clamp; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048; importer.SaveAndReimport();
        }
        static void ConfigureProbes()
        {
            var root = GameObject.Find(ProbeRoot) ?? new GameObject(ProbeRoot);
            root.SetActive(true);
            string folder = "Assets/GameResource/Environment/TrainingGround/InkReflections"; Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            // BakeReflectionProbe only includes ReflectionProbeStatic renderers. Temporarily
            // mark authored mesh geometry, then restore flags so batching/navigation stay intact.
            var geometry = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
                .Where(r => r.gameObject.scene == root.scene && r.GetComponent<MeshFilter>() != null).ToArray();
            var flags = geometry.Select(r => GameObjectUtility.GetStaticEditorFlags(r.gameObject)).ToArray();
            foreach (var surface in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)) { surface.Clear(); surface.ReleaseGraphics(); }
            try
            {
            foreach (var renderer in geometry) GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) | StaticEditorFlags.ReflectionProbeStatic);
            for (int i = 0; i < 3; i++)
            {
                string name = "InkReflection_" + i; var child = root.transform.Find(name);
                var probe = child != null ? child.GetComponent<ReflectionProbe>() : new GameObject(name).AddComponent<ReflectionProbe>();
                probe.transform.SetParent(root.transform); probe.transform.position = new Vector3(0, 3, (i - 1) * 19);
                probe.size = new Vector3(30, 12, 28); probe.center = new Vector3(0, 0, 0); probe.resolution = 128;
                probe.boxProjection = true; probe.blendDistance = 5; probe.importance = 1; probe.intensity = 1;
                probe.clearFlags = ReflectionProbeClearFlags.Skybox; probe.cullingMask = ~0; probe.hdr = true;
                probe.mode = ReflectionProbeMode.Baked;
                string path = folder + "/" + name + ".exr";
                if (!Lightmapping.BakeReflectionProbe(probe, path)) throw new InvalidOperationException("反射探针烘焙失败：" + name);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                probe.mode = ReflectionProbeMode.Custom; probe.customBakedTexture = AssetDatabase.LoadAssetAtPath<Texture>(path);
                if (probe.customBakedTexture == null) throw new InvalidOperationException("反射贴图导入失败");
            }
            }
            finally { for (int i = 0; i < geometry.Length; i++) GameObjectUtility.SetStaticEditorFlags(geometry[i].gameObject, flags[i]); }
            var hashes = Enumerable.Range(0, 3).Select(i => PaintSnapshotCodec.Hash(File.ReadAllBytes(folder + "/InkReflection_" + i + ".exr"))).Distinct().Count();
            if (hashes != 3) throw new InvalidOperationException("训练场探针未捕获不同位置的场景几何");
            EditorSceneManager.MarkSceneDirty(root.scene); EditorSceneManager.SaveScene(root.scene);
        }
        [MenuItem("喷墨对战/墨迹静态表现/切换旧外观")]
        public static void LegacyAppearance() => SetEnabled(false);
        [MenuItem("喷墨对战/墨迹静态表现/切换新外观")]
        public static void NewAppearance() => SetEnabled(true);
        static void SetEnabled(bool enabled)
        {
            var profile = AssetDatabase.LoadAssetAtPath<InkAppearanceProfile>(InkAppearanceProfile.AssetPath);
            if (profile != null) { profile.Enabled = enabled; EditorUtility.SetDirty(profile); }
            foreach (string name in new[] { "TrainingConcrete", "CoverConcrete" })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/TrainingGround/Materials/" + name + ".mat");
                m.SetFloat("_InkAppearance", enabled ? 1 : 0);
                if (profile != null)
                {
                    m.SetTexture("_InkFineNormal", profile.FineNormal);
                    m.SetVector("_InkRelief", new Vector4(profile.EdgeHeight, profile.EdgeWidth, profile.Relief, profile.BroadRelief));
                    m.SetVector("_InkFinish", new Vector4(profile.Smoothness, profile.FineNormalStrength, profile.FineNormalTiling, profile.TeamGroove));
                }
                EditorUtility.SetDirty(m);
            }
            foreach (var s in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)) s.SetAppearance(enabled);
            var root = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == ProbeRoot);
            if (root != null) { root.SetActive(enabled); EditorSceneManager.MarkSceneDirty(root.scene); if (!Application.isPlaying) EditorSceneManager.SaveScene(root.scene); }
            AssetDatabase.SaveAssets();
        }
        internal static void LoadTables()
        {
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + name + ".json")));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables); GameplayConfig.Validate();
        }
        internal static byte[] Read(RenderTexture target)
        {
            var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            try { texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); return texture.GetRawTextureData<byte>().ToArray(); }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(texture); }
        }
        internal static byte[] ReadVisual(PaintSurface surface) => InkAppearanceProfile.PackVisual(Read(surface.VisualState));
    }
}
#endif
