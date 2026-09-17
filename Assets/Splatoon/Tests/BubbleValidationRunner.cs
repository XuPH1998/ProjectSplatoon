#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    public sealed class BubbleValidationRunner : ICallbacks
    {
        const string Report = "Reports/BubbleGirl";
        static TestRunnerApi _api;
        static bool _running;
        static BubbleValidationRunner() { EditorApplication.update += Poll; }
        static void Poll()
        {
            if (_running || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/BubbleGirl/test";
            if (!File.Exists(request)) return;
            string names = File.ReadAllText(request).Trim(); File.Delete(request);
            Run(names.Length == 0 ? "Splatoon.Tests.BubbleWeaponTests" : names);
        }
        public static void RunBatch() => Run("Splatoon.Tests.BubbleWeaponTests;Splatoon.Tests.BubblePlayTests");
        static void Run(string names)
        {
            Directory.CreateDirectory(Report); _running = true;
            _api = ScriptableObject.CreateInstance<TestRunnerApi>(); _api.RegisterCallbacks(new BubbleValidationRunner());
            _api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = names.Split(';') }));
        }
        public void RunStarted(ITestAdaptor testsToRun) { File.WriteAllText(Report + "/test-status.txt", "RUNNING " + DateTime.UtcNow.ToString("O")); }
        public void RunFinished(ITestResultAdaptor result)
        {
            TestRunnerApi.SaveResultToFile(result, Report + "/tests.xml");
            File.WriteAllText(Report + "/test-status.txt", $"{result.ResultState}: passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");
            _running = false;
            if (Application.isBatchMode) EditorApplication.Exit(result.FailCount == 0 ? 0 : 1);
        }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
    }
}
#endif
