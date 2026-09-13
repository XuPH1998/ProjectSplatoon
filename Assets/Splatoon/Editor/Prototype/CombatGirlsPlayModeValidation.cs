#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.AddressableAssets;

namespace Splatoon.Editor
{
    public static class CombatGirlsPlayModeValidation
    {
        public static void Run()
        {
            CombatGirlsBuilder.ValidateInstalled();
            // Asset Database play mode exercises scene/content bindings without a
            // player build; release bundles are validated separately.
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            settings.ActivePlayModeDataBuilderIndex = 0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            EditorApplication.EnterPlaymode();
        }
    }
}
#endif
