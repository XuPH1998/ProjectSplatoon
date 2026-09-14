#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    public static class GameplayUpdateEditorChecks
    {
        const string Output = "Reports/GameplayUpdate";
        const string Running = "GameplayUpdate.Running";
        static readonly TestRunnerApi Api;
        static double nextPoll;
        [Serializable] public sealed class Request { public string label; public string[] names; }
        static GameplayUpdateEditorChecks()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>(); Api.RegisterCallbacks(new Results());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(Running, false)) return;
            const string path = "Temp/CodexGameplay/run-tests.json";
            if (!File.Exists(path)) return;
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(path)); File.Delete(path);
            Start(request);
        }
        [MenuItem("喷墨对战/验证/落墨与换队及长按射击")]
        public static void Run() => Start(new Request { label = "editmode", names = new[] {
            "Splatoon.Tests.GameplayUpdateTests", "Splatoon.Tests.CombatGirlsWeaponTests", "Splatoon.Tests.GameplayConfigTests",
            "Splatoon.Tests.HeroMigrationTests", "Splatoon.Tests.HeroSelectionTests", "Splatoon.Tests.WeaponReferenceTests",
            "Splatoon.Tests.InkCollisionTests", "Splatoon.Tests.InkSimulationTests", "Splatoon.Tests.PrototypeRulesTests" } });
        static void Start(Request request)
        {
            Directory.CreateDirectory(Output);
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                { File.WriteAllText(Output + "/blocked.txt", "当前场景有未保存修改，测试未启动。"); return; }
            SessionState.SetString("GameplayUpdate.Label", request.label); SessionState.SetBool(Running, true);
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
                var label = SessionState.GetString("GameplayUpdate.Label", "editmode");
                TestRunnerApi.SaveResultToFile(result, Output + "/" + label + "-tests.xml");
                File.WriteAllText(Output + "/" + label + "-summary.txt", $"passed={result.PassCount}; failed={result.FailCount}; skipped={result.SkipCount}");
                SessionState.SetBool(Running, false);
            }
        }
    }
}
#endif
