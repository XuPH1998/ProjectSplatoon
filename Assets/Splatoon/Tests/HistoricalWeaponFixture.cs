#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SimpleJSON;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    // Independent complete Git inputs; this scope replaces only one hero snapshot.
    public sealed class HistoricalWeaponFixture : IDisposable
    {
        const string DirectoryPath = "Tools/ValidationData/HistoricalWeaponContracts/";
        const string ManifestSha256 = "98874487f226fc9e938981086b88c35c98ec15c2de0eb9d76e1cdbef87e98de9";
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        readonly int _hero;
        readonly WeaponRuntimeConfig _previous;
        readonly WeaponConfigAsset _weapon;
        readonly AmmoConfigAsset _ammo;
        bool _disposed;

        static void Verify(string path, string expected)
        {
            using var hash = SHA256.Create();
            var actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Historical weapon input changed: " + path);
        }
        static T Load<T>(JSONNode source, JSONNode defaults) where T : ScriptableObject
        {
            string path = DirectoryPath + source["file"].Value;
            Verify(path, source["sha256"].Value);
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
                        // Match the independently pinned Ammo below, never the live weapon's Ammo.
                        parsed = field.Name == "ammoConfig" || !guid.Success ? null :
                            AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value), field.FieldType);
                        if (field.Name != "ammoConfig" && guid.Success)
                        {
                            var fileId = Regex.Match(value, @"fileID: (-?\d+)");
                            if (parsed == null || !fileId.Success ||
                                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier((UnityEngine.Object)parsed, out string actualGuid, out long actualFileId) ||
                                actualGuid != guid.Groups[1].Value || actualFileId != long.Parse(fileId.Groups[1].Value, Culture))
                                throw new InvalidOperationException("Historical reference identity mismatch: " + path + "/" + field.Name);
                        }
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
        public HistoricalWeaponFixture(string contract, cfg.HeroConfig hero, Action<WeaponConfigAsset> edit = null)
        {
            _hero = hero.Id; _previous = WeaponConfigService.Current.Get(hero);
            try
            {
                Verify(DirectoryPath + "inputs.json", ManifestSha256);
                var input = JSONNode.Parse(File.ReadAllText(DirectoryPath + "inputs.json"))["contracts"][contract];
                if (input["hero"].AsInt != _hero) throw new InvalidOperationException("Historical contract hero mismatch: " + contract);
                _weapon = Load<WeaponConfigAsset>(input["weapon"], input["weaponDefaults"]);
                _ammo = Load<AmmoConfigAsset>(input["ammo"], input["ammoDefaults"]);
                _weapon.ammoConfig = _ammo;
                edit?.Invoke(_weapon);
                WeaponConfigService.Current.SetForEditor(_hero, _weapon.Snapshot());
            }
            catch { Dispose(); throw; }
        }
        public static string CaptureDirectory(string name)
        {
            string path = "Reports/WeaponAlignment/HistoricalContracts/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", Culture)
                + "-" + Guid.NewGuid().ToString("N") + "/" + name;
            Directory.CreateDirectory(path); return path;
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            WeaponConfigService.Current.SetForEditor(_hero, _previous);
            if (_weapon != null) UnityEngine.Object.DestroyImmediate(_weapon);
            if (_ammo != null) UnityEngine.Object.DestroyImmediate(_ammo);
        }
    }
}
#endif
