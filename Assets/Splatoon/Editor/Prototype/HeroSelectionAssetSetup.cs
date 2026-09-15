#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class HeroSelectionAssetSetup
    {
        static double nextPoll;
        static HeroSelectionAssetSetup() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            const string request = "Temp/HeroSelection/prepare-assets";
            if (!File.Exists(request)) return;
            File.Delete(request);
            try { Prepare(); File.WriteAllText("Temp/HeroSelection/assets-ready.txt", "ready " + DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText("Temp/HeroSelection/assets-error.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/内容/配置英雄头像")]
        public static void Prepare()
        {
            const string directory = "Assets/GameResource/UI/HeroPortraits";
            foreach (string file in Directory.GetFiles(directory, "*Portrait.png"))
            {
                string path = file.Replace('\\', '/');
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Default; importer.sRGBTexture = true;
                importer.mipmapEnabled = false; importer.isReadable = false; importer.maxTextureSize = 512;
                importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            PrototypeBuilder.ConfigureAddressables();
        }
    }
}
#endif
