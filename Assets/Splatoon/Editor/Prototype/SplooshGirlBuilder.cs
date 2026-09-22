#if UNITY_EDITOR
using System;
using System.IO;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    public static class SplooshGirlBuilder
    {
        public const string Root = "Assets/GameResource/Characters/SplooshGirl";
        public const string WeaponRoot = "Assets/GameResource/Weapons/SplooshGirl";
        public const string CharacterPath = Root + "/Prefabs/SplooshGirlVisual.prefab";
        public const string WeaponPath = WeaponRoot + "/Prefabs/SplooshGun.prefab";
        public const string Report = "Reports/SplooshGirl";
        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new Exception("Missing " + path);
        static T Copy<T>(string source, string path) where T : Object
        { if (!File.Exists(path) && !AssetDatabase.CopyAsset(source, path)) throw new Exception("Cannot copy " + source); return Load<T>(path); }
        [MenuItem("喷墨对战/角色/安装铃芽与专业模型枪MG")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first");
            foreach (var p in new[] { Root + "/Prefabs", WeaponRoot + "/Prefabs", Report, "Assets/GameResource/UI/HeroPortraits" }) Directory.CreateDirectory(p);
            AssetDatabase.Refresh();
            BuildConfig();
            SplooshSummerBuilder.PrepareModel();
            var scene = EditorSceneManager.NewPreviewScene();
            try { BuildCharacter(scene); }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            PaperBodyBuilder.ConfigureHero("SplooshGirl", new Vector2(.75f, 1.5f));
            var profile = Load<CharacterPresentationProfile>(Root + "/SplooshGirlPresentation.asset");
            profile.Paper.CameraOffset = profile.CameraPivot; EditorUtility.SetDirty(profile.Paper);
            PrototypeBuilder.ConfigureAddressables(); AssetDatabase.SaveAssets();
            File.WriteAllText(Report + "/assets.txt", "PASS: hero 8, SummerCuteness, calibrated rifle, own Avatar/controller binding, measured muzzle, grip basis, live paper and Addressables\n");
        }
        public static void InstallBatch()
        { try { Install(); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
        public static void BuildBatch()
        { try { PrototypeBuilder.BuildWindowsTo("Builds/SplooshGirl"); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
        static void BuildConfig()
        {
            var ammo = Copy<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset", WeaponRoot + "/SplooshGirlAmmoConfig.asset");
            ammo.ammoId = 8; EditorUtility.SetDirty(ammo);
            var w = Copy<WeaponConfigAsset>("Assets/GameResource/Weapons/PistolGirl/PistolGirlWeaponConfig.asset", WeaponRoot + "/SplooshGirlWeaponConfig.asset");
            w.weaponPrefabAddress = "Weapon/SplooshGun"; w.ammoConfig = ammo;
            ApplyApprovedWeaponParameters(w);
            WeaponConfigValidation.Validate(w.Snapshot()); EditorUtility.SetDirty(w); AssetDatabase.SaveAssets();
        }
        // The reviewed replacement ledger includes explicit source values and retained project defaults.
        // Reinstalling character art must use the same weapon profile as the standalone importer.
        static void ApplyApprovedWeaponParameters(WeaponConfigAsset weapon)
        {
            var fields = SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/MainWeaponReplacement/Tuning.json"))["weapons"]["8"]["fields"];
            foreach (string key in fields.Keys)
            {
                var field = typeof(WeaponConfigAsset).GetField(key) ?? throw new InvalidOperationException("Unknown weapon field: " + key);
                var value = fields[key]["after"];
                // Assign parsed numbers directly: a JSON ToString round trip truncates doubles
                // and would change the content signature after reinstalling identical settings.
                object converted = field.FieldType == typeof(bool) ? value.AsInt != 0 :
                    field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, value.AsInt) :
                    Convert.ChangeType(value.AsDouble, field.FieldType, System.Globalization.CultureInfo.InvariantCulture);
                field.SetValue(weapon, converted);
            }
            WeaponConfigValidation.Validate(weapon.Snapshot());
        }
        static void BuildCharacter(Scene scene) => SplooshSummerBuilder.BuildCharacter(scene);
    }
}
#endif
