#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Netcode;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class PrototypeBuilder
    {
        private const string Root = "Assets/GameResource/Gameplay/Prototype";
        [MenuItem("喷墨对战/内容/安装正式喷墨资源")]
        public static void SetupGraybox() => CombatGirlsBuilder.Install();
        public static void ConfigureAddressables()
        {
            var settings=AddressableAssetSettingsDefaultObject.GetSettings(true);
            var group=settings.FindGroup("Splatoon Local")??settings.CreateGroup("Splatoon Local",false,false,true,null,typeof(BundledAssetGroupSchema),typeof(ContentUpdateGroupSchema));
            var schema=group.GetSchema<BundledAssetGroupSchema>();
            foreach (var stale in group.entries.Where(e => string.IsNullOrEmpty(e.AssetPath)).ToArray()) settings.RemoveAssetEntry(stale.guid);
            schema.BuildPath.SetVariableByName(settings,AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings,AddressableAssetSettings.kLocalLoadPath);
            schema.BundleMode=BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            // 逐项注册 JSON，避免将文件夹地址误当作可加载的 TextAsset 列表。
            var rootEntry=settings.FindAssetEntry(AssetDatabase.AssetPathToGUID("Assets/GameResource"));if(rootEntry!=null)settings.RemoveAssetEntry(rootEntry.guid);
            foreach(string path in Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban","*.json"))
            {
                var entry=settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path),group);entry.address=Path.GetFileNameWithoutExtension(path);entry.SetLabel("Luban",true,true);
            }
            var legacy = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(Root+"/PrototypeArena.unity"));
            if (legacy != null) settings.RemoveAssetEntry(legacy.guid);
            AddAddress(settings,group,TrainingGroundBuilder.ScenePath,PrototypeApp.ArenaAddress);
            AddAddress(settings,group,Root+"/Prefabs/PrototypePlayer.prefab",PrototypeApp.PlayerAddress);
            AddAddress(settings,group,Root+"/Prefabs/PrototypeMatch.prefab",PrototypeApp.MatchAddress);
            foreach (var obsolete in group.entries.Where(e => e.address == "Character/Jammo" || e.address == "Weapon/Splattershot").ToArray()) settings.RemoveAssetEntry(obsolete.guid);
            AddAddress(settings,group,CombatGirlsBuilder.CharacterPath,"Character/RifleGirl");
            AddAddress(settings,group,CombatGirlsBuilder.WeaponPath,"Weapon/RifleGirlRifle");
            foreach (var pack in CombatGirlsHeroBuilder.Definitions)
            {
                if (!File.Exists(pack.CharacterPath) || !File.Exists(pack.WeaponPath)) continue;
                AddAddress(settings,group,pack.CharacterPath,"Character/"+pack.name);
                AddAddress(settings,group,pack.WeaponPath,"Weapon/"+pack.weapon);
            }
            AddAddress(settings,group,"Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab","Effects/InkStream");
            AddAddress(settings,group,"Assets/GameResource/Effects/Ink/Prefabs/InkImpact.prefab","Effects/InkImpact");
            settings.BuildAddressablesWithPlayerBuild=AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            EditorUtility.SetDirty(settings);AssetDatabase.SaveAssets();
        }
        private static void AddAddress(AddressableAssetSettings settings,AddressableAssetGroup group,string path,string address)
        {settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path),group).address=address;}
        [MenuItem("喷墨对战/构建/Windows 正式资源版本")]
        public static void BuildWindows()
        {
            CombatGirlsBuilder.ValidateInstalled();
            TrainingGroundBuilder.ValidateSavedScene();
            ConfigureAddressables();
            AddressableAssetSettings.BuildPlayerContent(out var content);
            if(!string.IsNullOrEmpty(content.Error))throw new BuildFailedException(content.Error);
            Directory.CreateDirectory("Builds/Windows");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{
                scenes=new[]{"Assets/Scenes/Main/Boot.unity"},locationPathName="Builds/Windows/InkLan.exe",target=BuildTarget.StandaloneWindows64,
                options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded)throw new BuildFailedException("Windows 构建失败："+report.summary.result);
            Debug.Log("[PrototypeBuilder] Windows 构建成功：Builds/Windows/InkLan.exe");
        }
    }
}
#endif
