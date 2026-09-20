#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class BubbleShotgunBuilder
    {
        public const string Root = "Assets/GameResource/Weapons/BubbleShotgunGirl";
        public const string CharacterRoot = "Assets/GameResource/Characters/BubbleShotgunGirl";
        public const string CharacterPath = CharacterRoot + "/Prefabs/BubbleShotgunGirlVisual.prefab";
        public const string WeaponPath = Root + "/BubbleShotgunGirlWeaponConfig.asset";
        public const string Report = "Reports/BubbleShotgun";
        static double next;
        static BubbleShotgunBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/BubbleShotgun/install";
            if (!File.Exists(request)) return;
            File.Delete(request); Directory.CreateDirectory(Report);
            try { Install(); File.WriteAllText(Report + "/assets-result.txt", "PASS " + DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText(Report + "/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/角色/安装泡霰爆泡霰弹枪")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请在编辑模式安装英雄");
            Directory.CreateDirectory(Root + "/Prefabs"); Directory.CreateDirectory(CharacterRoot + "/Prefabs");
            Directory.CreateDirectory(Report); AssetDatabase.Refresh();
            const string source = "Assets/GameResource/Characters/ShotgunGirl";
            Copy(source + "/ShotgunGirlPresentation.asset", CharacterRoot + "/BubbleShotgunGirlPresentation.asset");
            Copy(source + "/Prefabs/ShotgunGirlVisual.prefab", CharacterPath);
            Copy("Assets/GameResource/Weapons/ShotgunGirl/Prefabs/Shotgun.prefab", Root + "/Prefabs/BubbleShotgun.prefab");
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>(CharacterRoot + "/BubbleShotgunGirlPresentation.asset");
            var character = PrefabUtility.LoadPrefabContents(CharacterPath);
            float headSize;
            try
            {
                character.name = "BubbleShotgunGirlVisual"; character.GetComponent<InkCharacterView>().Profile = profile;
                headSize = MeasureHead(character);
                CameraFramingBuilder.Apply(character.GetComponent<InkCharacterView>());
                PrefabUtility.SaveAsPrefabAsset(character, CharacterPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(character); }
            float diameter = Mathf.Max(1.2f, headSize * 3);
            string ammoPath = Root + "/BubbleShotgunGirlAmmoConfig.asset";
            bool newAmmo = !File.Exists(ammoPath), newWeapon = !File.Exists(WeaponPath);
            Copy("Assets/GameResource/Weapons/BubbleGirl/BubbleGirlAmmoConfig.asset", ammoPath);
            Copy("Assets/GameResource/Weapons/RocketLauncherGirl/RapidBlasterExplosion.prefab", Root + "/BubbleShotgunExplosion.prefab");
            var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(ammoPath);
            if (newAmmo)
            {
                ammo.name = "BubbleShotgunGirlAmmoConfig"; ammo.ammoId = 9;
                ammo.visualLifetime = 4; ammo.explosionEnabled = true;
                ammo.explosionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/BubbleShotgunExplosion.prefab");
                ammo.explosionRadius = 2.5f; ammo.explosionDamage = 25;
                ammo.explosionConstantDamage = false; ammo.excludeDirectHitFromExplosion = true;
                ammo.collisionExplosionRadiusRate = ammo.collisionExplosionDamageRate = 1;
                ammo.explosionPaint = true; ammo.explosionPaintRadiusMin = ammo.explosionPaintRadiusMax = 2.8f;
                ammo.bubbleBounceAudio = null; EditorUtility.SetDirty(ammo);
            }
            if (newWeapon)
            {
                var w = ScriptableObject.CreateInstance<WeaponConfigAsset>();
                w.name = "BubbleShotgunGirlWeaponConfig"; w.ammoConfig = ammo; w.weaponPrefabAddress = "Weapon/BubbleShotgun";
                w.fireMode = WeaponFireMode.SemiAutomatic; w.motionMode = ProjectileMotionMode.FloatingBubble;
                w.fireRate = 1; w.shotInk = 16; w.startSeconds = .1; w.emergeStartSeconds = .2;
                w.inkRecoverLockSeconds = 1; w.shootMoveSpeed = 2.5f; w.burstCount = 1; w.pelletCount = 3; w.semiBufferSeconds = .1;
                w.speedMin = w.speedMax = 18; w.projectileGravity = .05f; w.lifetime = 4; w.collisionRadius = diameter / 2;
                w.straightSeconds = .1; w.brakeSeconds = .2; w.brakeSpeedMultiplier = .02f; w.effectiveRange = 6;
                w.spreadDegrees = w.jumpSpreadDegrees = w.baseSpreadDegrees = w.baseJumpSpreadDegrees = 30;
                w.floatingPitchSpreadDegrees = 3;
                w.damage = w.damageMin = 55; w.damageReduceStartSeconds = 0; w.damageReduceEndSeconds = 4;
                // Required generic brush fields stay valid. Floating bubbles only paint on explosion.
                w.paintRadiusMin = w.paintRadiusMax = 2.8f; w.paintHardness = .55f; w.paintStrength = 1;
                w.trailSpacing = 1; w.trailRadiusMin = w.trailRadiusMax = .2f; w.trailMaxDrop = 0;
                AssetDatabase.CreateAsset(w, WeaponPath);
            }
            WeaponConfigValidation.Validate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(WeaponPath).Snapshot());
            PrototypeBuilder.ConfigureAddressables(); AssetDatabase.SaveAssets();
            File.WriteAllText(Report + "/dimensions.txt", $"Head maximum dimension: {headSize:R} m\nBubble diameter: {diameter:R} m\nRatio: {diameter / headSize:R}\nHead measured from active face mesh in standing prefab, excluding hair and decorations.\n");
        }
        public static float MeasureHead(GameObject character)
        {
            var face = character.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.gameObject.activeInHierarchy && r.name.StartsWith("Face", StringComparison.Ordinal)).ToArray();
            if (face.Length == 0) throw new InvalidOperationException("ShotgunGirl 缺少启用的头部网格");
            var mesh = new Mesh(); var bounds = new Bounds(); bool first = true;
            try
            {
                foreach (var renderer in face)
                {
                    renderer.BakeMesh(mesh);
                    foreach (var vertex in mesh.vertices)
                    {
                        Vector3 point = character.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                        if (first) { bounds = new Bounds(point, Vector3.zero); first = false; } else bounds.Encapsulate(point);
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
            float size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (!float.IsFinite(size) || size <= .01f) throw new InvalidOperationException("头部尺寸测量失败");
            return size;
        }
        static void Copy(string source, string target)
        { if (!File.Exists(target) && !AssetDatabase.CopyAsset(source, target)) throw new IOException("Cannot copy " + source + " to " + target); }
    }
}
#endif
