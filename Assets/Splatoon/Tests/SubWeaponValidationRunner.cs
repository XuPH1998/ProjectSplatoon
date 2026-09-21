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
    public sealed class SubWeaponValidationRunner : ICallbacks
    {
        static TestRunnerApi api;
        static SubWeaponValidationRunner() { api=ScriptableObject.CreateInstance<TestRunnerApi>();api.RegisterCallbacks(new SubWeaponValidationRunner());EditorApplication.update+=Poll; }
        static void Poll()
        {
            const string build="Temp/SubWeapons/build";
            if(File.Exists(build)&&!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating)
            {
                var running=typeof(TestRunnerApi).GetMethod("IsRunActive",BindingFlags.Static|BindingFlags.NonPublic);
                if(running==null||(bool)running.Invoke(null,null))return;
                string stamp=File.GetLastWriteTimeUtc(build).Ticks.ToString();
                if(SessionState.GetString("SubWeapon.BuildRefresh","")!=stamp){SessionState.SetString("SubWeapon.BuildRefresh",stamp);AssetDatabase.Refresh();return;}
                if(EditorUtility.scriptCompilationFailed)return;
                for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)return;
                File.Delete(build);Directory.CreateDirectory("Reports/SubWeapons");
                try{File.WriteAllText("Reports/SubWeapons/build-status.txt","RUNNING");Type.GetType("Splatoon.Editor.PrototypeBuilder, Splatoon.Editor").GetMethod("BuildWindowsTo").Invoke(null,new object[]{"Temp/SubWeapons/Player"});File.WriteAllText("Reports/SubWeapons/build-status.txt","PASS "+DateTime.UtcNow.ToString("O"));}
                catch(Exception e){File.WriteAllText("Reports/SubWeapons/build-status.txt",e.ToString());Debug.LogException(e);}return;
            }
            const string request="Temp/SubWeapons/tests";
            if(!File.Exists(request)||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            var active=typeof(TestRunnerApi).GetMethod("IsRunActive",BindingFlags.Static|BindingFlags.NonPublic);if(active==null||(bool)active.Invoke(null,null))return;
            string token=File.GetLastWriteTimeUtc(request).Ticks.ToString();
            if(SessionState.GetString("SubWeapon.Refresh","")!=token){SessionState.SetString("SubWeapon.Refresh",token);AssetDatabase.Refresh();return;}
            if(EditorUtility.scriptCompilationFailed)return;
            for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)return;
            var lines=File.ReadAllLines(request);File.Delete(request);Directory.CreateDirectory("Reports/SubWeapons");
            SessionState.SetString("SubWeapon.TestLabel",lines[0]);SessionState.SetBool("SubWeapon.TestsRunning",true);
            File.WriteAllText("Reports/SubWeapons/test-status.txt","RUNNING "+lines[0]);
            api.Execute(new ExecutionSettings(new Filter{testMode=TestMode.EditMode,testNames=lines[1].Split(';')}));
        }
        public void RunStarted(ITestAdaptor tests){} public void TestStarted(ITestAdaptor test){} public void TestFinished(ITestResultAdaptor result){}
        public void RunFinished(ITestResultAdaptor result)
        {
            if(!SessionState.GetBool("SubWeapon.TestsRunning",false))return;
            string label=SessionState.GetString("SubWeapon.TestLabel","tests");TestRunnerApi.SaveResultToFile(result,"Reports/SubWeapons/"+label+".xml");
            File.WriteAllText("Reports/SubWeapons/test-status.txt",$"{label}: passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");SessionState.SetBool("SubWeapon.TestsRunning",false);
        }
    }
}
#endif
