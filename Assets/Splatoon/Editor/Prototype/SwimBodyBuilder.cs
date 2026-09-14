using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class SwimBodyBuilder
    {
        public const string Folder = "Assets/GameResource/Characters/Shared/Swim";
        public const string PrefabPath = Folder + "/SwimBody.prefab";
        static SwimBodyBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            const string request = "Temp/SwimAdjustment/install-assets";
            if (!File.Exists(request)) return;
            File.Delete(request);
            Directory.CreateDirectory("Reports/SwimAdjustment");
            try { Install(); File.WriteAllText("Reports/SwimAdjustment/assets-result.txt", "PASS"); }
            catch (Exception e) { File.WriteAllText("Reports/SwimAdjustment/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }

        [MenuItem("喷墨对战/内容/安装纸片潜墨模型")]
        public static void Install() => PaperBodyBuilder.Install();
    }
}
