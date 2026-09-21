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
    public sealed class AimBallisticsValidationRunner : ICallbacks
    {
        const string DirectoryPath = "Reports/AimBallistics", RequestPath = "Temp/AimBallistics/tests.json";
        const string Running = "AimBallistics.Running";
        static readonly TestRunnerApi Api;
        [Serializable] public class Request { public string label; public string[] names; }
        static AimBallisticsValidationRunner()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            Api.RegisterCallbacks(new AimBallisticsValidationRunner());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (!File.Exists(RequestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(Running, false)) return;
            var active = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
            if (active == null || (bool)active.Invoke(null, null)) return;
            string token = File.GetLastWriteTimeUtc(RequestPath).Ticks.ToString();
            if (SessionState.GetString("AimBallistics.Refresh", "") != token)
            { SessionState.SetString("AimBallistics.Refresh", token); AssetDatabase.Refresh(); return; }
            Directory.CreateDirectory(DirectoryPath);
            if (EditorUtility.scriptCompilationFailed)
            { File.WriteAllText(DirectoryPath + "/status.txt", "BLOCKED: compilation failed"); return; }
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                { File.WriteAllText(DirectoryPath + "/status.txt", "BLOCKED: unsaved scene"); return; }
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath));
            File.Delete(RequestPath);
            SessionState.SetString("AimBallistics.Label", request.label);
            SessionState.SetBool(Running, true);
            File.WriteAllText(DirectoryPath + "/status.txt", "RUNNING " + request.label);
            // Play fixtures explicitly enter/exit Play Mode inside UnityTests; pure checks stay in EditMode.
            Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = request.names }));
        }
        public void RunStarted(ITestAdaptor test) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor test) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (!SessionState.GetBool(Running, false)) return;
            var label = SessionState.GetString("AimBallistics.Label", "tests");
            TestRunnerApi.SaveResultToFile(result, DirectoryPath + "/" + label + ".xml");
            File.WriteAllText(DirectoryPath + "/status.txt", $"{label}: passed={result.PassCount}; failed={result.FailCount}; skipped={result.SkipCount}");
            SessionState.SetBool(Running, false);
        }
    }
}
#endif
