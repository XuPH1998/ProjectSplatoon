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
    // Complete independent builder contract + actual historical Ammo; only hero 9 is replaced.
    public sealed class HistoricalBubbleContractFixture : IDisposable
    {
        const string DirectoryPath = "Tools/ValidationData/HistoricalBubbleContracts/";
        const string ManifestSha256 = "ff1bca8b657ff4ff29b0e51ab293b22f78e131685a42c42f4ccaec0db2bb0da8";
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        readonly WeaponRuntimeConfig _previous;
        readonly WeaponConfigAsset _weapon;
        readonly AmmoConfigAsset _ammo;
        bool _disposed;

        static void Verify(string path, string expected)
        {
            using var hash = SHA256.Create();
            string actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Historical bubble input changed: " + path);
        }
        static JSONNode Manifest()
        {
            Verify(DirectoryPath + "inputs.json", ManifestSha256);
            return JSONNode.Parse(File.ReadAllText(DirectoryPath + "inputs.json"));
        }
        internal static void VerifySource(string key)
        {
            var source = Manifest()["sources"][key];
            Verify(DirectoryPath + source["file"].Value, source["sha256"].Value);
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
                    throw new InvalidOperationException("Historical bubble reference identity mismatch: " + field.Name);
                return reference;
            }
            if (field.FieldType.IsEnum) return Enum.ToObject(field.FieldType, int.Parse(value, Culture));
            if (field.FieldType == typeof(bool)) return value == "1" || value == "true";
            if (field.FieldType == typeof(string)) return value.Trim('"');
            return Convert.ChangeType(value, field.FieldType, Culture);
        }
        static void Assign(ScriptableObject target, JSONNode values)
        {
            foreach (var field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!values.HasKey(field.Name)) throw new InvalidOperationException("Unpinned historical bubble field: " + field.Name);
                field.SetValue(target, Parse(field, values[field.Name].Value));
            }
        }
        public HistoricalBubbleContractFixture()
        {
            _previous = GameplayConfig.GetWeapon(9);
            try
            {
                var input = Manifest();
                foreach (var source in input["sources"].Children)
                    Verify(DirectoryPath + source["file"].Value, source["sha256"].Value);
                _weapon = ScriptableObject.CreateInstance<WeaponConfigAsset>();
                Assign(_weapon, input["weaponValues"]);
                var ammoValues = JSONNode.Parse(input["ammoDefaults"].ToString());
                string ammoPath = DirectoryPath + input["sources"]["ammo"]["file"].Value;
                foreach (Match match in Regex.Matches(File.ReadAllText(ammoPath), @"^  (\w+): (.*)$", RegexOptions.Multiline))
                    if (ammoValues.HasKey(match.Groups[1].Value)) ammoValues[match.Groups[1].Value] = match.Groups[2].Value.Trim();
                _ammo = ScriptableObject.CreateInstance<AmmoConfigAsset>();
                Assign(_ammo, ammoValues); _weapon.ammoConfig = _ammo;
                var snapshot = _weapon.Snapshot(); WeaponConfigValidation.Validate(snapshot);
                WeaponConfigService.Current.Replace(9, snapshot);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_previous != null) WeaponConfigService.Current.Replace(9, _previous);
            if (_weapon != null) UnityEngine.Object.DestroyImmediate(_weapon);
            if (_ammo != null) UnityEngine.Object.DestroyImmediate(_ammo);
        }
    }

    public sealed class HistoricalHeroSelectionLayoutFixture
    {
        public HistoricalHeroSelectionLayoutFixture() => HistoricalBubbleContractFixture.VerifySource("layout");
        public readonly Rect Window = new(60, 40, 1160, 640);
        public readonly Rect Confirm = new(958, 616, 234, 42);
        public readonly Rect Close = new(1146, 62, 46, 36);
        public readonly Rect CardViewport = new(88, 154, 644, 432);
        public float ContentHeight(int count) => Mathf.Max(CardViewport.height, Mathf.CeilToInt(count / 2f) * 110 - 8);
        public Rect Card(int index) => new(88 + index % 2 * 318, 154 + index / 2 * 110, 304, 102);
        public Rect Portrait(int index) { var r = Card(index); return new Rect(r.x + 12, r.y + 11, 80, 80); }
        public Matrix4x4 Matrix(float width, float height)
        {
            float scale = Mathf.Min(width / 1280f, height / 720f);
            return Matrix4x4.TRS(new Vector3((width - 1280 * scale) * .5f, (height - 720 * scale) * .5f), Quaternion.identity, new Vector3(scale, scale, 1));
        }
    }
}
#endif
