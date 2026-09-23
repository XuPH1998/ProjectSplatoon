#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatoon.Tests
{
    // An explicit request file also permits repeatable validation in an already-open editor.
    [InitializeOnLoad]
    public static class UiLayoutValidationRunner
    {
        const string Request = "Temp/ui-layout-validation.request";
        const string Active = "Splatoon.UiLayoutValidation.Active";
        static TestRunnerApi _api;
        static UiLayoutValidationRunner()
        {
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new Results());
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            if(File.Exists("Temp/ui-layout-refresh.request")&&!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!SessionState.GetBool(Active,false))
            {
                File.Delete("Temp/ui-layout-refresh.request");AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);return;
            }
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || SessionState.GetBool(Active,false)) return;
            File.Delete(Request);
            Run();
        }
        [MenuItem("Splatoon/Validation/Combat and loadout UI")]
        public static void Run()
        {
            Directory.CreateDirectory("Reports/UiLayout");
            if(EditorApplication.isPlayingOrWillChangePlaymode) { File.WriteAllText("Reports/UiLayout/status.txt","Blocked: editor is already in Play Mode."); return; }
            for(int i=0;i<SceneManager.sceneCount;i++)
                if(SceneManager.GetSceneAt(i).isDirty) { File.WriteAllText("Reports/UiLayout/status.txt","Blocked: save open scene changes before UI validation."); return; }
            SessionState.SetBool(Active,true);
            File.WriteAllText("Reports/UiLayout/status.txt","Running focused layout and three-resolution UI interaction tests.");
            _api.Execute(new ExecutionSettings(new Filter {
                testMode=TestMode.EditMode,
                testNames=new[] {
                    "Splatoon.Tests.HeroSelectionPresentationTests.NineHeroesHaveDistinctNamedPortraitsRegisteredForPlayers",
                    "Splatoon.Tests.HeroSelectionPresentationTests.NineCardsStayWithinWindowAndPortraitsRemainSquare",
                    "Splatoon.Tests.HeroSelectionPresentationTests.HudEdgesAndWorldProjectionShareScreenSpaceWithoutStretch",
                    "Splatoon.Tests.HeroSelectionPresentationTests.EveryEquipmentOptionFitsAboveFixedFooter",
                    "Splatoon.Tests.HeroSelectionUiPlayTests.PortraitPickerAtAllThreeResolutions"
                }
            }));
        }
        sealed class Results : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                if(!SessionState.GetBool(Active,false))return;
                SessionState.SetBool(Active,false);
                TestRunnerApi.SaveResultToFile(result,"Reports/UiLayout/results.xml");
                File.WriteAllText("Reports/UiLayout/status.txt",$"Finished: passed={result.PassCount}, failed={result.FailCount}, skipped={result.SkipCount}\n{result.Message}");
            }
        }
    }
}
#endif
