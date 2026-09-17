#if UNITY_EDITOR
using System;
using System.IO;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class BubbleGirlBuilder
    {
        public const string Root = "Assets/GameResource/Weapons/BubbleGirl";
        public const string CharacterRoot = "Assets/GameResource/Characters/BubbleGirl";
        public const string CharacterPath = CharacterRoot + "/Prefabs/BubbleGirlVisual.prefab";
        public const string WeaponPath = Root + "/BubbleGirlWeaponConfig.asset";
        public const string Report = "Reports/BubbleGirl";
        static double _next;
        static BubbleGirlBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/BubbleGirl/install";
            if (!File.Exists(request)) return;
            File.Delete(request); Directory.CreateDirectory(Report);
            try { Install(); File.WriteAllText(Report + "/assets-result.txt", "PASS " + DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText(Report + "/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/角色/安装沫澜泡泡枪")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请在编辑模式安装英雄");
            Directory.CreateDirectory(Root + "/Prefabs"); Directory.CreateDirectory(CharacterRoot + "/Prefabs");
            AssetDatabase.Refresh();
            const string source = "Assets/GameResource/Characters/ShotgunGirl";
            Copy(source + "/ShotgunGirlPresentation.asset", CharacterRoot + "/BubbleGirlPresentation.asset");
            Copy(source + "/Prefabs/ShotgunGirlVisual.prefab", CharacterPath);
            Copy("Assets/GameResource/Weapons/ShotgunGirl/Prefabs/Shotgun.prefab", Root + "/Prefabs/BubbleGun.prefab");
            Copy("Assets/GameResource/UI/HeroPortraits/ShotgunGirlPortrait.png", "Assets/GameResource/UI/HeroPortraits/BubbleGirlPortrait.png");
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>(CharacterRoot + "/BubbleGirlPresentation.asset");
            var character = PrefabUtility.LoadPrefabContents(CharacterPath);
            try
            {
                character.name = "BubbleGirlVisual"; character.GetComponent<InkCharacterView>().Profile = profile;
                PrefabUtility.SaveAsPrefabAsset(character, CharacterPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(character); }
            PaperBodyBuilder.ConfigureHero("BubbleGirl");
            Copy("Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlInkMuzzle.prefab", Root + "/BubbleGirlInkMuzzle.prefab");
            var muzzleObject = PrefabUtility.LoadPrefabContents(Root + "/BubbleGirlInkMuzzle.prefab");
            try
            {
                muzzleObject.name = "BubbleGirlInkMuzzle";
                var main = muzzleObject.GetComponent<ParticleSystem>().main;
                main.startSize = .045f; main.startSpeed = .8f; main.startLifetime = .15f;
                PrefabUtility.SaveAsPrefabAsset(muzzleObject, Root + "/BubbleGirlInkMuzzle.prefab");
            }
            finally { PrefabUtility.UnloadPrefabContents(muzzleObject); }
            string materialPath = Root + "/BubbleInk.mat";
            if (!File.Exists(materialPath)) AssetDatabase.CreateAsset(new Material(Shader.Find("Splatoon/BubbleInk")), materialPath);
            string bubblePath = Root + "/BubbleInk.prefab";
            if (!File.Exists(bubblePath))
            {
                var bubble = GameObject.CreatePrimitive(PrimitiveType.Sphere); bubble.name = "BubbleInk";
                UnityEngine.Object.DestroyImmediate(bubble.GetComponent<Collider>());
                var renderer = bubble.GetComponent<MeshRenderer>(); renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                PrefabUtility.SaveAsPrefabAsset(bubble, bubblePath); UnityEngine.Object.DestroyImmediate(bubble);
            }
            Sound("BubbleShot", .13f, 820, 170, .14f);
            Sound("BubbleBounce", .085f, 530, 180, .05f);
            Sound("BubblePop", .17f, 1200, 110, .55f);
            string ammoPath = Root + "/BubbleGirlAmmoConfig.asset";
            bool newAmmo = !File.Exists(ammoPath);
            Copy("Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlAmmoConfig.asset", ammoPath);
            var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(ammoPath);
            if (newAmmo)
            {
                ammo.name = "BubbleGirlAmmoConfig"; ammo.ammoId = 7; ammo.burstInterval = .05f;
                ammo.visualLifetime = 2.4f; ammo.particlesPerBurst = 1; ammo.muzzleBurstCount = 6; ammo.muzzleInterval = .05f;
                ammo.bubblePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(bubblePath);
                ammo.muzzlePrefab = AssetDatabase.LoadAssetAtPath<ParticleSystem>(Root + "/BubbleGirlInkMuzzle.prefab");
                ammo.bubbleShotAudio = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/BubbleShot.wav");
                ammo.bubbleBounceAudio = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/BubbleBounce.wav");
                ammo.bubblePopAudio = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/BubblePop.wav");
                ammo.explosionEnabled = false; ammo.explosionPrefab = null; EditorUtility.SetDirty(ammo);
            }
            bool newWeapon = !File.Exists(WeaponPath);
            Copy("Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlWeaponConfig.asset", WeaponPath);
            var weapon = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(WeaponPath);
            if (newWeapon)
            {
                weapon.name = "BubbleGirlWeaponConfig"; weapon.ammoConfig = ammo;
                weapon.weaponPrefabAddress = "Weapon/BubbleGun";
                weapon.fireMode = WeaponFireMode.BubbleVolley; weapon.motionMode = ProjectileMotionMode.BouncingBubble;
                weapon.burstCount = 4; weapon.pelletCount = 1; weapon.fireRate = 1f / .55f;
                weapon.bubbleVolleySeconds = .55; weapon.bubbleIntervalSeconds = .05;
                weapon.bubbleGroundBounces = 3; weapon.bubbleMaxBounces = 6;
                weapon.bubbleNormalRetention = .72f; weapon.bubbleTangentRetention = .9f; weapon.bubbleWallRetention = .9f;
                weapon.shotInk = 8; weapon.startSeconds = .1; weapon.emergeStartSeconds = .2;
                weapon.inkRecoverLockSeconds = .65; weapon.shootMoveSpeed = 2.8f;
                weapon.speedMin = weapon.speedMax = 14; weapon.projectileGravity = 18; weapon.lifetime = 2.4f;
                weapon.collisionRadius = .18f; weapon.effectiveRange = 24; weapon.damage = weapon.damageMin = 30;
                weapon.spreadDegrees = weapon.jumpSpreadDegrees = weapon.baseSpreadDegrees = weapon.baseJumpSpreadDegrees = 0;
                weapon.straightSeconds = 0; weapon.brakeSpeedMultiplier = 1; weapon.semiBufferSeconds = 0;
                weapon.paintRadiusMin = .65f; weapon.paintRadiusMax = .85f;
                weapon.trailSpacing = .6f; weapon.trailRadiusMin = .2f; weapon.trailRadiusMax = .3f; weapon.trailMaxDrop = 1;
                EditorUtility.SetDirty(weapon);
            }
            WeaponConfigValidation.Validate(weapon.Snapshot());
            PrototypeBuilder.ConfigureAddressables(); AssetDatabase.SaveAssets();
            Debug.Log("BUBBLE_GIRL_ASSETS_PASS");
        }
        static void Copy(string from, string to)
        { if (!File.Exists(to) && !AssetDatabase.CopyAsset(from, to)) throw new IOException("Cannot copy " + from + " to " + to); }
        static void Sound(string name, float seconds, float from, float to, float noise)
        {
            string path = Root + "/" + name + ".wav"; if (File.Exists(path)) return;
            const int rate = 22050; int count = (int)(seconds * rate); uint seed = 127; double phase = 0;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                for (int i = 0; i < count; i++)
                {
                    float t = (float)i / count; phase += Math.PI * 2 * Mathf.Lerp(from, to, t) / rate;
                    float random = InkBallistics.Random01(ref seed) * 2 - 1;
                    float envelope = Mathf.Sin(Mathf.Min(1, t * 20) * Mathf.PI * .5f) * Mathf.Exp(-t * 6) * (1 - t);
                    writer.Write((short)(Mathf.Clamp((float)Math.Sin(phase) * (1 - noise) + random * noise, -1, 1) * envelope * 25000));
                }
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
#endif
