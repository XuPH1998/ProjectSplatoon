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
    public sealed class SpecialWeaponValidationRunner : ICallbacks
    {
        static TestRunnerApi api;
        static SpecialWeaponValidationRunner() { api=ScriptableObject.CreateInstance<TestRunnerApi>();api.RegisterCallbacks(new SpecialWeaponValidationRunner());EditorApplication.update+=Poll; }
        static void Poll()
        {
            const string build="Temp/SpecialWeapons/build";
            if(File.Exists(build)&&!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating)
            {
                var running=typeof(TestRunnerApi).GetMethod("IsRunActive",BindingFlags.Static|BindingFlags.NonPublic);
                if(running==null||(bool)running.Invoke(null,null))return;
                string stamp=File.GetLastWriteTimeUtc(build).Ticks.ToString();
                if(SessionState.GetString("SpecialWeapon.BuildRefresh","")!=stamp){SessionState.SetString("SpecialWeapon.BuildRefresh",stamp);AssetDatabase.Refresh();return;}
                if(EditorUtility.scriptCompilationFailed)return;
                for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)return;
                File.Delete(build);Directory.CreateDirectory("Reports/SpecialWeapons");
                try{File.WriteAllText("Reports/SpecialWeapons/build-status.txt","RUNNING");Type.GetType("Splatoon.Editor.PrototypeBuilder, Splatoon.Editor").GetMethod("BuildWindowsTo").Invoke(null,new object[]{"Temp/SpecialWeapons/Player"});File.WriteAllText("Reports/SpecialWeapons/build-status.txt","PASS "+DateTime.UtcNow.ToString("O"));}
                catch(Exception e){File.WriteAllText("Reports/SpecialWeapons/build-status.txt",e.ToString());Debug.LogException(e);}return;
            }
            const string request="Temp/SpecialWeapons/tests";
            if(!File.Exists(request)||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            var active=typeof(TestRunnerApi).GetMethod("IsRunActive",BindingFlags.Static|BindingFlags.NonPublic);if(active==null||(bool)active.Invoke(null,null))return;
            string token=File.GetLastWriteTimeUtc(request).Ticks.ToString();
            if(SessionState.GetString("SpecialWeapon.Refresh","")!=token){SessionState.SetString("SpecialWeapon.Refresh",token);AssetDatabase.Refresh();return;}
            if(EditorUtility.scriptCompilationFailed)return;
            for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)return;
            var lines=File.ReadAllLines(request);File.Delete(request);Directory.CreateDirectory("Reports/SpecialWeapons");
            SessionState.SetString("SpecialWeapon.TestLabel",lines[0]);SessionState.SetBool("SpecialWeapon.TestsRunning",true);
            File.WriteAllText("Reports/SpecialWeapons/test-status.txt","RUNNING "+lines[0]);
            api.Execute(new ExecutionSettings(new Filter{testMode=TestMode.EditMode,testNames=lines[1].Split(';')}));
        }
        public void RunStarted(ITestAdaptor tests){} public void TestStarted(ITestAdaptor test){} public void TestFinished(ITestResultAdaptor result){}
        public void RunFinished(ITestResultAdaptor result)
        {
            if(!SessionState.GetBool("SpecialWeapon.TestsRunning",false))return;
            string label=SessionState.GetString("SpecialWeapon.TestLabel","tests");TestRunnerApi.SaveResultToFile(result,"Reports/SpecialWeapons/"+label+".xml");
            File.WriteAllText("Reports/SpecialWeapons/test-status.txt",$"{label}: passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}");SessionState.SetBool("SpecialWeapon.TestsRunning",false);
        }
    }
}
#endif
