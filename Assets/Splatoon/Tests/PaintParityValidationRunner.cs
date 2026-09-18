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
    public sealed class PaintParityValidationRunner : ICallbacks
    {
        const string Root = "Reports/PaintParity1130", Running = "PaintParity.Running", Label = "PaintParity.Label";
        static TestRunnerApi api;
        static double next;
        static PaintParityValidationRunner()
        {
            api = ScriptableObject.CreateInstance<TestRunnerApi>(); api.RegisterCallbacks(new PaintParityValidationRunner());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var active = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
            if (active == null || (bool)active.Invoke(null, null)) return;
            const string build = "Temp/PaintParity1130/build";
            if (File.Exists(build))
            {
                string buildToken=File.GetLastWriteTimeUtc(build).Ticks.ToString();
                if(SessionState.GetString("PaintParity.BuildRefreshed","")!=buildToken)
                {SessionState.SetString("PaintParity.BuildRefreshed",buildToken);AssetDatabase.Refresh();return;}
                if(EditorUtility.scriptCompilationFailed)return;
                File.Delete(build); Directory.CreateDirectory(Root);
                try
                {
                    Type builder=null;
                    foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    { builder=assembly.GetType("Splatoon.Editor.PrototypeBuilder"); if(builder!=null)break; }
                    if(builder==null)throw new InvalidOperationException("Formal build entrypoint unavailable");
                    builder.GetMethod("BuildWindowsTo").Invoke(null,new object[]{"Builds/PaintParity1130"});
                    File.WriteAllText(Root+"/build-result.txt","PASS "+DateTime.UtcNow.ToString("O")+" Builds/PaintParity1130/InkLan.exe");
                }
                catch(Exception e) { File.WriteAllText(Root+"/build-result.txt",e.ToString()); Debug.LogException(e); }
                return;
            }
            const string request = "Temp/PaintParity1130/tests";
            if (!File.Exists(request)) return;
            string token = File.GetLastWriteTimeUtc(request).Ticks + ":" + File.ReadAllText(request);
            if (SessionState.GetString("PaintParity.Refreshed", "") != token)
            { SessionState.SetString("PaintParity.Refreshed", token); AssetDatabase.Refresh(); return; }
            if (EditorUtility.scriptCompilationFailed) return;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) return;
            var lines = File.ReadAllLines(request); File.Delete(request);
            Directory.CreateDirectory(Root); SessionState.SetString(Label, lines[0]); SessionState.SetBool(Running, true);
            File.WriteAllText(Root + "/test-status.txt", "RUNNING " + lines[0] + " " + DateTime.UtcNow.ToString("O"));
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = lines[1].Split(';') }));
        }
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
