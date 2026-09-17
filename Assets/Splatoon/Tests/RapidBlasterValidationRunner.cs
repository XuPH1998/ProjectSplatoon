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
    public sealed class RapidBlasterValidationRunner : ICallbacks
    {
        const string Report = "Reports/RapidBlaster", Running = "RapidBlaster.Running", Label = "RapidBlaster.Label";
        static TestRunnerApi api;
        static double next;
        static RapidBlasterValidationRunner()
        {
            api = ScriptableObject.CreateInstance<TestRunnerApi>(); api.RegisterCallbacks(new RapidBlasterValidationRunner());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (SessionState.GetBool(Running, false) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/RapidBlaster/tests";
            if (!File.Exists(request)) return;
            var active = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
            if (active != null && (bool)active.Invoke(null, null)) return;
            // Let any existing test operation finish before taking the editor.
            if (SessionState.GetBool("GameplayUpdate.Running", false) || SessionState.GetBool("SwimAdjustment.Running", false)) return;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) return;
            string[] lines = File.ReadAllLines(request); File.Delete(request);
            Run(lines.Length > 0 ? lines[0] : "editmode", lines.Length > 1 ? lines[1] : "Splatoon.Tests.RapidBlasterTests");
        }
        static void Run(string label, string names)
        {
            Directory.CreateDirectory(Report); SessionState.SetBool(Running, true); SessionState.SetString(Label, label);
            File.WriteAllText(Report + "/test-status.txt", "RUNNING " + label + " " + DateTime.UtcNow.ToString("O"));
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = names.Split(';') }));
        }
        public void RunStarted(ITestAdaptor tests) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor test) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (!SessionState.GetBool(Running, false)) return;
            if (!ContainsRapidBlaster(result)) return;
            string label = SessionState.GetString(Label, "editmode");
            TestRunnerApi.SaveResultToFile(result, Report + "/" + label + ".xml");
            File.WriteAllText(Report + "/test-status.txt", $"{label}: passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");
            SessionState.SetBool(Running, false);
        }
        static bool ContainsRapidBlaster(ITestResultAdaptor result)
        {
            if (result.Test.FullName.Contains("Splatoon.Tests.RapidBlaster")) return true;
            foreach (var child in result.Children) if (ContainsRapidBlaster(child)) return true;
            return false;
        }
    }
}
#endif
