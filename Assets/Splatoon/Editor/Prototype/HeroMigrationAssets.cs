#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Splatoon.Combat;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class HeroMigrationAssets
    {
        static HeroMigrationAssets() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!File.Exists("Temp/HeroMigration/install-assets")) return;
            File.Delete("Temp/HeroMigration/install-assets");
            try { Directory.CreateDirectory("Reports/HeroMigration"); Install(); File.WriteAllText("Reports/HeroMigration/assets-result.txt", "PASS"); }
            catch (Exception e) { File.WriteAllText("Reports/HeroMigration/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/内容/安装英雄模型绑定")]
        public static void Install()
        {
            var weapon = PrefabUtility.LoadPrefabContents(CombatGirlsBuilder.WeaponPath);
            try
            {
                var binding = weapon.GetComponent<HeroWeaponBindings>() ?? weapon.AddComponent<HeroWeaponBindings>();
                binding.Nozzle = weapon.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Muzzle");
                binding.LeftGrip = weapon.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Left_Handle");
                PrefabUtility.SaveAsPrefabAsset(weapon, CombatGirlsBuilder.WeaponPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(weapon); }
            PrototypeBuilder.ConfigureAddressables();
            Debug.Log("[HeroMigration] Hero weapon bindings and Addressables installed.");
        }
    }
}
#endif
