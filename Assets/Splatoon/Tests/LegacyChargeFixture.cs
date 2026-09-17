#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using SimpleJSON;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    /// <summary>The retired charged launcher is a test fixture, never the live hero-5 contract.</summary>
    public static class LegacyChargeFixture
    {
        public static JSONNode ReferenceRow()
        {
            var rows = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponAssets/Migration-Baseline.json"));
            var row = JSONNode.Parse(rows.Children.Single(r => r["id"].AsInt == 5).ToString());
            WeaponTimeFixture.ToSeconds(row); return row;
        }
        public static WeaponRuntimeConfig Create(Action<WeaponConfigAsset> edit = null)
        {
            var source = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RocketLauncherGirl/RocketLauncherGirlWeaponConfig.asset");
            var copy = UnityEngine.Object.Instantiate(source);
            try
            {
                JsonUtility.FromJsonOverwrite(ReferenceRow().ToString(), copy);
                copy.motionMode = ProjectileMotionMode.Ballistic;
                copy.referenceRules = false; copy.referenceSpreadEnabled = false;
                edit?.Invoke(copy); return copy.Snapshot();
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }
        public static void Install() => WeaponConfigService.Current.SetForEditor(5, Create());
    }
}
#endif
