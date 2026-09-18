#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace Splatoon.Tests
{
    [InitializeOnLoad]
    public sealed class FoamValidationRunner:ICallbacks
    {
        const string Running="Foam.Validation.Running",Label="Foam.Validation.Label";
        static TestRunnerApi _api;
        static FoamValidationRunner(){_api=ScriptableObject.CreateInstance<TestRunnerApi>();_api.RegisterCallbacks(new FoamValidationRunner());EditorApplication.update+=Poll;}
        static void Poll()
        {
            const string request="Temp/FoamTerrain/tests";
            if(EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode||EditorUtility.scriptCompilationFailed||!File.Exists(request))return;
            var active=typeof(TestRunnerApi).GetMethod("IsRunActive",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            if(active==null||(bool)active.Invoke(null,null))return;
            for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)return;
            var lines=File.ReadAllLines(request);File.Delete(request);
            Directory.CreateDirectory("Reports/FoamTerrain");SessionState.SetBool(Running,true);SessionState.SetString(Label,lines[0]);
            _api.Execute(new ExecutionSettings(new Filter{testMode=TestMode.EditMode,testNames=lines.Length>1?lines[1].Split(';'):new[]{"Splatoon.Tests.FoamTerrainTests"}}));
        }
        public void RunStarted(ITestAdaptor tests){if(SessionState.GetBool(Running,false))File.WriteAllText("Reports/FoamTerrain/test-status.txt","RUNNING "+DateTime.UtcNow.ToString("O"));}
        public void RunFinished(ITestResultAdaptor result)
        {
            if(!SessionState.GetBool(Running,false))return;
            TestRunnerApi.SaveResultToFile(result,"Reports/FoamTerrain/"+SessionState.GetString(Label,"tests")+".xml");
            File.WriteAllText("Reports/FoamTerrain/test-status.txt",$"passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");SessionState.SetBool(Running,false);
        }
        public void TestStarted(ITestAdaptor test){}
        public void TestFinished(ITestResultAdaptor result){}
    }
}
#endif
