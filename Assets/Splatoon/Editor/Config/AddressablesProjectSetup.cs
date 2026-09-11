#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Splatoon.Editor
{
    public static class AddressablesProjectSetup
    {
        [MenuItem("Project Splatoon/Config/Setup Addressables Root")]
        public static void Setup()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (settings == null) { EditorUtility.DisplayDialog("Addressables", "Could not create settings.", "OK"); return; }
            var group = settings.FindGroup("Splatoon Local") ?? settings.CreateGroup("Splatoon Local", false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            const string root = "Assets/GameResource";
            var guid = AssetDatabase.AssetPathToGUID(root);
            if (!string.IsNullOrEmpty(guid) && settings.FindAssetEntry(guid) == null)
            {
                var entry = settings.CreateOrMoveEntry(guid, group);
                entry.address = root;
                entry.SetLabel("Splatoon", true);
            }
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Addressables", "Configured Assets/GameResource as the runtime root.", "OK");
        }
    }
}
#endif
