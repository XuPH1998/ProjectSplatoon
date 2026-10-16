#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    // Pins capture inputs only. Expected ownership hashes remain in their original files.
    public sealed class FrozenPaintReplayFixture : IDisposable
    {
        const string Inputs = "Tools/ValidationData/FrozenPaintReplay/inputs.json";
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        readonly WeaponConfigAsset _weapon;
        readonly AmmoConfigAsset _ammo;
        readonly cfg.Tables _previousTables;
        readonly WeaponRuntimeConfig _previousWeapon;
        readonly int _hero;
        readonly bool _legacyDirect;
        public readonly CharacterPresentationProfile Profile;

        static void Verify(string path, string expected)
        {
            using var hash = SHA256.Create();
            var actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Frozen input changed: " + path);
        }
        static T Load<T>(string path, JSONNode defaults) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            try
            {
                string yaml = File.ReadAllText(path);
                foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!defaults.HasKey(field.Name)) throw new InvalidOperationException("Unpinned historical field: " + field.Name);
                    var match = Regex.Match(yaml, "^  " + field.Name + @": (.*)$", RegexOptions.Multiline);
                    string value = match.Success ? match.Groups[1].Value.Trim() : defaults[field.Name].Value;
                    object parsed;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                    {
                        var guid = Regex.Match(value, @"guid: ([a-f0-9]+)");
                        // Ammo is loaded independently below; never inherit the live weapon's Ammo.
                        parsed = field.Name == "ammoConfig" || !guid.Success ? null :
                            AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value), field.FieldType);
                        if (field.Name != "ammoConfig" && guid.Success && parsed == null)
                            throw new InvalidOperationException("Missing frozen reference: " + path + "/" + field.Name);
                    }
                    else if (field.FieldType.IsEnum) parsed = Enum.ToObject(field.FieldType, int.Parse(value, Culture));
                    else if (field.FieldType == typeof(bool)) parsed = value == "1" || value == "true";
                    else if (field.FieldType == typeof(string)) parsed = value.Trim('"');
                    else parsed = Convert.ChangeType(value, field.FieldType, Culture);
                    field.SetValue(asset, parsed);
                }
                return asset;
            }
            catch { UnityEngine.Object.DestroyImmediate(asset); throw; }
        }
        static Vector3 Vector(JSONNode row, string key) => new(
            float.Parse(row[key]["x"].Value, Culture), float.Parse(row[key]["y"].Value, Culture), float.Parse(row[key]["z"].Value, Culture));
        static void Tables(cfg.Tables value) => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, value);

        public FrozenPaintReplayFixture(string era, int hero)
        {
            _hero = hero; _legacyDirect = era == "WeaponAlignment";
            var input = JSONNode.Parse(File.ReadAllText(Inputs));
            var frozen = input[era]; var row = frozen["weapons"][hero.ToString()];
            _previousTables = LubanConfigService.Current.Tables;
            _previousWeapon = GameplayConfig.GetWeapon(hero);
            try
            {
                foreach (string path in input["requiredFiles"].Keys) Verify(path, input["requiredFiles"][path].Value);
                Verify(row["weaponPath"].Value, row["weaponSha256"].Value);
                Verify(row["ammoPath"].Value, row["ammoSha256"].Value);
                _weapon = Load<WeaponConfigAsset>(row["weaponPath"].Value, frozen["weaponDefaults"]);
                _ammo = Load<AmmoConfigAsset>(row["ammoPath"].Value, frozen["ammoDefaults"]);
                _weapon.ammoConfig = _ammo;
                Profile = ScriptableObject.CreateInstance<CharacterPresentationProfile>();
                var p = row["profile"];
                Profile.DualWield = p["DualWield"].Value == "true";
                Profile.CameraPivot = Vector(p, "CameraPivot"); Profile.CameraOffset = Vector(p, "CameraOffset");
                Profile.AimPivot = Vector(p, "AimPivot"); Profile.MuzzlePosition = Vector(p, "MuzzlePosition");
                Profile.LeftMuzzlePosition = Vector(p, "LeftMuzzlePosition");
                Profile.CameraCollisionRadius = float.Parse(p["CameraCollisionRadius"].Value, Culture);
                Profile.CameraCollisionPadding = float.Parse(p["CameraCollisionPadding"].Value, Culture);
                var data = Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban", "*.json")
                    .ToDictionary(Path.GetFileNameWithoutExtension, file => JSONNode.Parse(File.ReadAllText(file)));
                foreach (string key in frozen["global"].Keys) data["tbglobal"][0][key] = frozen["global"][key];
                var historicalHero = frozen["heroes"].Children.Single(h => h["id"].AsInt == hero);
                var currentHero = data["tbhero"].Children.Single(h => h["id"].AsInt == hero);
                foreach (string key in historicalHero.Keys) currentHero[key] = historicalHero[key];
                Tables(new cfg.Tables(name => data[name]));
                WeaponConfigService.Current.SetForEditor(hero, _weapon.Snapshot());
            }
            catch { Dispose(); throw; }
        }
        public WeaponReferenceMeasurements.Options Options(uint seed = 0) => new()
        { Seed = seed, LegacyDirectIdentity = _legacyDirect, Profile = Profile };
        public void Dispose()
        {
            Tables(_previousTables);
            WeaponConfigService.Current.SetForEditor(_hero, _previousWeapon);
            if (Profile != null) UnityEngine.Object.DestroyImmediate(Profile);
            if (_weapon != null) UnityEngine.Object.DestroyImmediate(_weapon);
            if (_ammo != null) UnityEngine.Object.DestroyImmediate(_ammo);
        }
    }
}
#endif
