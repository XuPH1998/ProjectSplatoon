#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    public static class WeaponReferenceEditorChecks
    {
        static readonly TestRunnerApi Api;
        static double _nextPoll;
        const string Running = "WeaponReference.TestsRunning";
        static WeaponReferenceEditorChecks()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>(); Api.RegisterCallbacks(new Results());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(Running, false)) return;
            if (File.Exists("Temp/WeaponReference/run-editmode")) { File.Delete("Temp/WeaponReference/run-editmode"); Run(); }
            else if (File.Exists("Temp/WeaponReference/run-playmode")) { File.Delete("Temp/WeaponReference/run-playmode"); Play(); }
        }
        [MenuItem("喷墨对战/验证/武器参考与测量 EditMode")]
        public static void Run() => Start("EditMode", new[] {
            "Splatoon.Tests.WeaponReferenceTests", "Splatoon.Tests.WeaponSelectionTests", "Splatoon.Tests.GameplayConfigTests",
            "Splatoon.Tests.InkSimulationTests", "Splatoon.Tests.InkCollisionTests", "Splatoon.Tests.InkCoverageTests",
            "Splatoon.Tests.WeaponReferenceMeasurementTests.CaptureAllFiveWeaponsWithRealProjectileAndOwnershipCode",
            "Splatoon.Tests.WeaponReferenceMeasurementTests.RepeatingMeasurementUsesIdenticalSeededTrajectoryAndCoverage" });
        [MenuItem("喷墨对战/验证/武器参考与测量 PlayMode")]
        public static void Play() => Start("PlayMode", new[] { "Splatoon.Tests.WeaponReferenceMeasurementTests.PlayModeUsesTheSameMeasuredPhysicsAndCoverage" });
        static void Start(string mode, string[] names)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(Running, false)) return;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) { Debug.LogError("请先保存场景，再运行武器验证。"); return; }
            Directory.CreateDirectory("Logs/WeaponReference");
            SessionState.SetString("WeaponReference.TestMode", mode); SessionState.SetBool(Running, true);
            File.WriteAllText("Logs/WeaponReference/started.txt", mode + " " + System.DateTime.UtcNow.ToString("O"));
            Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = names }));
        }
        sealed class Results : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                if (!SessionState.GetBool(Running, false)) return;
                string mode = SessionState.GetString("WeaponReference.TestMode", "EditMode");
                TestRunnerApi.SaveResultToFile(result, "Logs/WeaponReference/" + mode + "-tests.xml");
                SessionState.SetBool(Running, false);
                Debug.Log($"[WeaponReference] {mode} passed={result.PassCount}, failed={result.FailCount}");
            }
        }
    }
}
#endif
