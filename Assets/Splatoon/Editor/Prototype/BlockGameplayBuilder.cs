#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Splatoon.Config;

namespace Splatoon.Editor
{
    public static class BlockGameplayBuilder
    {
        [MenuItem("积木对战/配置/应用已批准的武器积木参数")]
        public static void ApplyWeaponDefaults()
        {
            var rules = new Dictionary<int,int> { {1,8},{2,12},{3,0},{4,6},{5,0},{6,8},{7,32},{8,4},{9,0} };
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + name + ".json")));
            foreach (var hero in tables.TbHero.DataList)
            {
                if (!rules.TryGetValue(hero.Id, out int remove)) throw new InvalidOperationException("积木武器规则缺少英雄 " + hero.Id);
                var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(hero.WeaponConfigPath);
                if (asset == null) throw new InvalidOperationException("武器配置缺失 " + hero.WeaponConfigPath);
                Undo.RecordObject(asset, "积木武器配置");
                asset.blockBuild = remove > 0; asset.blockEnemyRemoval = remove; asset.blockRemovalRadius = .20f;
                EditorUtility.SetDirty(asset);
                WeaponConfigValidation.Validate(asset.Snapshot());
                WeaponConfigService.Current.SetForEditor(hero.Id, asset.Snapshot(), asset);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[BLOCK] 已应用全部 9 把武器的积木建造与拆除规则");
        }
        public static void BuildWindows() => BuildWindowsTo("Builds/BlocksWindows");
        public static void BuildWindowsTo(string outputDirectory)
        {
            bool frameStats = PlayerSettings.enableFrameTimingStats;
            try { PlayerSettings.enableFrameTimingStats = true; PrototypeBuilder.BuildWindowsTo(outputDirectory); }
            finally { PlayerSettings.enableFrameTimingStats = frameStats; }
        }
    }
}
#endif
