#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    // A dedicated result sink prevents another editor validation run overwriting this report.
    [InitializeOnLoad]
    public sealed class WeaponAlignmentValidationRunner : ICallbacks
    {
        const string Root = "Reports/WeaponAlignment", Running = "WeaponAlignment.Running", Label = "WeaponAlignment.Label";
        static TestRunnerApi api;
        static double next;
        static WeaponAlignmentValidationRunner()
        {
            api = ScriptableObject.CreateInstance<TestRunnerApi>(); api.RegisterCallbacks(new WeaponAlignmentValidationRunner());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            // Unity 1.6's runner exposes this query internally. Fail closed if its API changes.
            var active = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
            if (active == null || (bool)active.Invoke(null, null)) return;
            // Recover a completed run after a domain reload without dropping a queued request.
            if (SessionState.GetBool(Running, false)) SessionState.SetBool(Running, false);
            const string request = "Temp/WeaponAlignment/tests";
            if (!File.Exists(request)) return;
            string token = File.GetLastWriteTimeUtc(request).Ticks + ":" + File.ReadAllText(request);
            if (SessionState.GetString("WeaponAlignment.Refreshed", "") != token)
            {
                SessionState.SetString("WeaponAlignment.Refreshed", token);
                AssetDatabase.Refresh();
                return;
            }
            if (EditorUtility.scriptCompilationFailed) return;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) return;
            var lines = File.ReadAllLines(request); File.Delete(request);
            if (lines.Length != 2 || string.IsNullOrWhiteSpace(lines[1])) throw new InvalidOperationException("Expected label and semicolon-separated test names");
            Directory.CreateDirectory(Root); SessionState.SetString(Label, lines[0]); SessionState.SetBool(Running, true);
            File.WriteAllText(Root + "/test-status.txt", "RUNNING " + lines[0] + " " + DateTime.UtcNow.ToString("O"));
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = lines[1].Split(';') }));
        }
        static bool ContainsAlignment(ITestResultAdaptor result) => result.Test.FullName.StartsWith("Splatoon.Tests.WeaponAlignment") || result.Children.Any(ContainsAlignment);
        public void RunStarted(ITestAdaptor tests) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (!SessionState.GetBool(Running, false)) return;
            string label = SessionState.GetString(Label, "tests");
            TestRunnerApi.SaveResultToFile(result, Root + "/" + label + ".xml");
            File.WriteAllText(Root + "/test-status.txt", $"{label}: passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");
            SessionState.SetBool(Running, false);
        }
    }
}
#endif

