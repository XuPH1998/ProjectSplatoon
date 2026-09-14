#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    public static class SwimAdjustmentEditorChecks
    {
        const string Output = "Reports/SwimAdjustment", Running = "SwimAdjustment.Running";
        static readonly TestRunnerApi Api;
        [Serializable] public sealed class Request { public string label; public string[] names; }
        static SwimAdjustmentEditorChecks()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>(); Api.RegisterCallbacks(new Results());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(Running, false)) return;
            const string refresh = "Temp/SwimAdjustment/refresh";
            if (File.Exists(refresh)) { File.Delete(refresh); AssetDatabase.Refresh(); return; }
            if (EditorUtility.scriptCompilationFailed) return;
            const string path = "Temp/SwimAdjustment/run-tests.json";
            if (!File.Exists(path)) return;
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(path)); File.Delete(path);
            Directory.CreateDirectory(Output);
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                { File.WriteAllText(Output + "/blocked.txt", "当前场景有未保存修改，测试未启动。"); return; }
            SessionState.SetString("SwimAdjustment.Label", request.label); SessionState.SetBool(Running, true);
            File.WriteAllText(Output + "/started.txt", request.label + " " + DateTime.UtcNow.ToString("O"));
            Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = request.names }));
        }
        sealed class Results : ICallbacks
        {
            public void RunStarted(ITestAdaptor test) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor test) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                if (!SessionState.GetBool(Running, false)) return;
                string label = SessionState.GetString("SwimAdjustment.Label", "editmode");
                TestRunnerApi.SaveResultToFile(result, Output + "/" + label + "-tests.xml");
                File.WriteAllText(Output + "/" + label + "-summary.txt", $"passed={result.PassCount}; failed={result.FailCount}; skipped={result.SkipCount}");
                SessionState.SetBool(Running, false);
            }
        }
    }
}
#endif
