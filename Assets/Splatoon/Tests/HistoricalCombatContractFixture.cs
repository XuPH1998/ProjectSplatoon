#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SimpleJSON;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    // Full actual historical monolithic rows, separate from any expected baseline.
    public sealed class HistoricalCombatContractFixture : IDisposable
    {
        const string DirectoryPath = "Tools/ValidationData/HistoricalCombatContracts/";
        const string ManifestSha256 = "7b5f83ddaf334dd776711e30196584f03f7aeecda6d34193e11d038512564fb6";
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        readonly int _hero;
        readonly WeaponRuntimeConfig _previous;
        readonly WeaponConfigAsset _weapon;
        readonly AmmoConfigAsset _ammo;
        bool _disposed;

        static void Verify(string path, string expected)
        {
            using var hash = SHA256.Create();
            string actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Historical combat input changed: " + path);
        }
        static object Parse(FieldInfo field, string value)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
            {
                var guid = Regex.Match(value, @"guid: ([a-f0-9]+)");
                if (!guid.Success) return null;
                var reference = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value), field.FieldType);
                var fileId = Regex.Match(value, @"fileID: (-?\d+)");
                if (reference == null || !fileId.Success ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reference, out string actualGuid, out long actualFileId) ||
                    actualGuid != guid.Groups[1].Value || actualFileId != long.Parse(fileId.Groups[1].Value, Culture))
                    throw new InvalidOperationException("Historical reference identity mismatch: " + field.Name + "/" + guid.Groups[1].Value);
                return reference;
            }
            if (field.FieldType.IsEnum) return Enum.ToObject(field.FieldType, int.Parse(value, Culture));
            if (field.FieldType == typeof(bool)) return value == "1" || value == "true";
            if (field.FieldType == typeof(string)) return value.Trim('"');
            return Convert.ChangeType(value, field.FieldType, Culture);
        }
        static void Assign(ScriptableObject target, JSONNode values, JSONNode defaults)
        {
            foreach (var field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!defaults.HasKey(field.Name)) throw new InvalidOperationException("Unpinned compatibility field: " + field.Name);
                var value = values.HasKey(field.Name) ? values[field.Name] : defaults[field.Name];
                field.SetValue(target, field.FieldType == typeof(double) && value.IsNumber
                    ? value.AsDouble : Parse(field, value.Value));
            }
        }
        public HistoricalCombatContractFixture(int hero, Action<WeaponConfigAsset> edit = null)
        {
            _hero = hero; _previous = GameplayConfig.GetWeapon(hero);
            try
            {
                Verify(DirectoryPath + "inputs.json", ManifestSha256);
                var input = JSONNode.Parse(File.ReadAllText(DirectoryPath + "inputs.json"));
                Verify(DirectoryPath + input["rows"]["file"].Value, input["rows"]["sha256"].Value);
                var rows = JSONNode.Parse(File.ReadAllText(DirectoryPath + input["rows"]["file"].Value));
                if (rows.Count != 5 || !rows.Children.Select(row => row["id"].AsInt).SequenceEqual(Enumerable.Range(1, 5)))
                    throw new InvalidOperationException("Historical combat rows must contain the complete original five heroes");
                var row = rows.Children.Single(value => value["id"].AsInt == hero);
                WeaponTimeFixture.ToSeconds(row);
                _weapon = ScriptableObject.CreateInstance<WeaponConfigAsset>();
                Assign(_weapon, row, input["weaponDefaults"]);
                var ammo = input["ammo"][hero.ToString()];
                string ammoPath = DirectoryPath + ammo["file"].Value;
                Verify(ammoPath, ammo["sha256"].Value);
                var values = new JSONObject();
                foreach (Match match in Regex.Matches(File.ReadAllText(ammoPath), @"^  (\w+): (.*)$", RegexOptions.Multiline))
                    values[match.Groups[1].Value] = match.Groups[2].Value.Trim();
                _ammo = ScriptableObject.CreateInstance<AmmoConfigAsset>();
                Assign(_ammo, values, input["ammoDefaults"]);
                _weapon.ammoConfig = _ammo;
                edit?.Invoke(_weapon);
                WeaponConfigService.Current.SetForEditor(hero, _weapon.Snapshot());
            }
            catch { Dispose(); throw; }
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
