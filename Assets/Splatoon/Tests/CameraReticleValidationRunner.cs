#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    static class CameraReticleValidationRunner
    {
        static CameraReticleValidationRunner() => EditorApplication.update += Poll;
        static void Poll()
        {
            const string request = "Temp/CameraReticle/build";
            if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var active = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
            if (active == null || (bool)active.Invoke(null, null)) return;
            string token = File.GetLastWriteTimeUtc(request).Ticks.ToString();
            if (SessionState.GetString("CameraReticle.BuildRefresh", "") != token)
            { SessionState.SetString("CameraReticle.BuildRefresh", token); AssetDatabase.Refresh(); return; }
            if (EditorUtility.scriptCompilationFailed) return;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) return;
            File.Delete(request); Directory.CreateDirectory("Reports/CameraReticle");
            try
            {
                File.WriteAllText("Reports/CameraReticle/build-status.txt", "RUNNING " + DateTime.UtcNow.ToString("O"));
                Type.GetType("Splatoon.Editor.PrototypeBuilder, Splatoon.Editor").GetMethod("BuildWindowsTo")
                    .Invoke(null, new object[] { "Temp/CameraReticle/Player" });
                File.WriteAllText("Reports/CameraReticle/build-status.txt", "PASS " + DateTime.UtcNow.ToString("O"));
            }
            catch (Exception e) { File.WriteAllText("Reports/CameraReticle/build-status.txt", e.ToString()); Debug.LogException(e); }
        }
    }
}
#endif
