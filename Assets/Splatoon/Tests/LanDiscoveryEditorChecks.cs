#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    // Opt-in validation for an already-open editor; no player build and no exit of the user's editor.
    [InitializeOnLoad]
    public static class LanDiscoveryEditorChecks
    {
        private const string Request = "Temp/RoomDiscovery/run-editor-checks";
        private static TestRunnerApi _api;
        private static double _nextCheck;
        static LanDiscoveryEditorChecks() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextCheck) return;
            _nextCheck = EditorApplication.timeSinceStartup + 1;
            if (File.Exists("Temp/RoomDiscovery/run-lobby-smoke") && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            { File.Delete("Temp/RoomDiscovery/run-lobby-smoke"); Play(); return; }
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            File.Delete(Request); Run();
        }
        [MenuItem("喷墨对战/验证/房间发现与地图配置 EditMode")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || _api != null) return;
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/room-discovery-editor-started.txt",System.DateTime.UtcNow.ToString("O"));
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new Results());
            _api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode,
                testNames = new[] {"Splatoon.Tests.LanDiscoveryTests", "Splatoon.Tests.GameplayConfigTests", "Splatoon.Tests.LanRoomCodeTests"} }));
        }
        [MenuItem("喷墨对战/验证/房间列表 PlayMode")]
        public static void Play()
        {
            if(Application.platform!=RuntimePlatform.WindowsEditor) { Debug.LogError("原生鼠标交互验证需要 Windows Unity 编辑器。"); return; }
            for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)
                if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) { Debug.LogError("请先保存场景，再运行房间列表验证。"); return; }
            var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if(scene.path!="Assets/Scenes/Main/Boot.unity") { Debug.LogError("请先打开 Boot 场景，再运行房间列表验证。"); return; }
            SessionState.SetBool("RoomDiscovery.PlaySmoke",true);EditorApplication.EnterPlaymode();
        }
        private sealed class Results : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                TestRunnerApi.SaveResultToFile(result, "Logs/room-discovery-editor.xml");
                Debug.Log($"[RoomDiscovery] EditMode passed={result.PassCount}, failed={result.FailCount}");
                Object.DestroyImmediate(_api); _api = null;
            }
        }
    }
}
#endif
