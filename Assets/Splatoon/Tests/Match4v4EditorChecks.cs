#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    public static class Match4v4EditorChecks
    {
        const string Output="Reports/Match4v4";
        const string Running="Match4v4.Running";
        static readonly TestRunnerApi api;
        static double next;
        static Match4v4EditorChecks()
        {
            api=ScriptableObject.CreateInstance<TestRunnerApi>(); api.RegisterCallbacks(new Results());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if(EditorApplication.timeSinceStartup<next) return;
            next=EditorApplication.timeSinceStartup+1;
            if(EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed || SessionState.GetBool(Running,false)) return;
            const string request="Temp/Match4v4/run";
            if(!File.Exists(request)) return;
            string command=File.ReadAllText(request).Trim(); File.Delete(request); Directory.CreateDirectory(Output);
            try
            {
                if(command=="bake")
                {
                    Type.GetType("Splatoon.Editor.TrainingGroundBuilder, Splatoon.Editor",true).GetMethod("UpgradeFourPlayerSpawns").Invoke(null,null);
                    File.WriteAllText(Output+"/bake.txt","PASS "+DateTime.UtcNow.ToString("O")); return;
                }
                for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)
                    if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Unsaved scene: save it before running tests");
                SessionState.SetString("Match4v4.Output",command);
                SessionState.SetBool(Running,true);
                string[] tests=command=="play" ? new[]{"Splatoon.Tests.Match4v4PlayTests"} :
                    new[]{"Splatoon.Tests.Match4v4Tests","Splatoon.Tests.GameplayConfigTests","Splatoon.Tests.PrototypeRulesTests","Splatoon.Tests.GameplayUpdateTests",
                    "Splatoon.Tests.PaperTraversalTests","Splatoon.Tests.SwimMovementTests","Splatoon.Tests.ShooterMovementTests","Splatoon.Tests.LanDiscoveryTests"};
                File.WriteAllText(Output+"/"+command+"-started.txt",DateTime.UtcNow.ToString("O"));
                api.Execute(new ExecutionSettings(new Filter {testMode=TestMode.EditMode,testNames=tests}));
            }
            catch(Exception ex) { SessionState.SetBool(Running,false); File.WriteAllText(Output+"/error.txt",ex.ToString()); Debug.LogException(ex); }
        }
        sealed class Results : ICallbacks
        {
            public void RunStarted(ITestAdaptor tests) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                if(!SessionState.GetBool(Running,false)) return;
                string command=SessionState.GetString("Match4v4.Output","edit");
                TestRunnerApi.SaveResultToFile(result,Output+"/"+command+".xml");
                File.WriteAllText(Output+"/"+command+"-result.txt",$"passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");
                SessionState.SetBool(Running,false);
            }
        }
    }
}
#endif
