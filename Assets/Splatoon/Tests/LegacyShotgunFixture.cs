#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    // Frozen pre-Explosher data keeps the retired pellet path covered independently.
    public static class LegacyShotgunFixture
    {
        static T Load<T>(string name) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            string yaml = File.ReadAllText("Tools/ValidationData/Explosher/Baseline/" + name + ".asset");
            foreach (var f in typeof(T).GetFields())
            {
                var match = Regex.Match(yaml, "^  " + f.Name + @": (.*)$", RegexOptions.Multiline);
                if (!match.Success) continue;
                string value = match.Groups[1].Value.Trim(); object parsed;
                if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                {
                    var guid = Regex.Match(value, @"guid: ([a-f0-9]+)");
                    parsed = guid.Success ? AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value), f.FieldType) : null;
                }
                else if (f.FieldType.IsEnum) parsed = Enum.ToObject(f.FieldType, int.Parse(value));
                else if (f.FieldType == typeof(bool)) parsed = value == "1";
                else if (f.FieldType == typeof(string)) parsed = value.Trim('"');
                else parsed = Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture);
                f.SetValue(asset, parsed);
            }
            return asset;
        }
        public static WeaponRuntimeConfig Create()
        {
            var weapon = Load<WeaponConfigAsset>("ShotgunGirlWeaponConfig");
            var ammo = Load<AmmoConfigAsset>("ShotgunGirlAmmoConfig");
            try { weapon.ammoConfig = ammo; return weapon.Snapshot(); }
            finally { UnityEngine.Object.DestroyImmediate(weapon); UnityEngine.Object.DestroyImmediate(ammo); }
        }
    }
}
#endif
