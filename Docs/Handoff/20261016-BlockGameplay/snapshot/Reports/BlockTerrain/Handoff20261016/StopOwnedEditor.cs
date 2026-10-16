using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatoon.ReportGuards
{
    public static class StopOwnedEditor
    {
        public static object Run()
        {
            if (Process.GetCurrentProcess().Id != 78316
                || !string.Equals(System.IO.Path.GetFullPath(Application.dataPath),
                    "D:\\XPHUNITY\\ProjectSplatoon\\Assets", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the owned ProjectSplatoon Editor may stop.");
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
                throw new InvalidOperationException("Do not interrupt active Unity work.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Preserve an unsaved scene; no shutdown requested.");
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
            return new Dictionary<string, object> {
                { "pid", Process.GetCurrentProcess().Id }, { "quitScheduled", true },
                { "dirtyScenes", false }, { "userRequestedDevelopmentStop", true },
                { "utc", DateTime.UtcNow.ToString("o") }
            };
        }
    }
}
