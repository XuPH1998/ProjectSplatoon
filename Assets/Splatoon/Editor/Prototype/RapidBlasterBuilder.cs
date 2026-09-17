#if UNITY_EDITOR
using System;
using System.IO;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class RapidBlasterBuilder
    {
        const string Root = "Assets/GameResource/Weapons/RocketLauncherGirl";
        public const string Report = "Reports/RapidBlaster";
        static double next;
        static RapidBlasterBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/RapidBlaster/install";
            if (!File.Exists(request)) return;
            File.Delete(request); Directory.CreateDirectory(Report);
            try { Install(); File.WriteAllText(Report + "/assets-result.txt", "PASS " + DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText(Report + "/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }

        [MenuItem("喷墨对战/武器/安装快速爆破枪特效")]
        public static void Install()
        {
            AssetDatabase.ImportAsset(Root + "/RocketLauncherGirlAmmoConfig.asset", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(Root + "/RocketLauncherGirlWeaponConfig.asset", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            string path = Root + "/RapidBlasterExplosion.prefab";
            if (!File.Exists(path))
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/RocketLauncherGirlInkMuzzle.prefab");
                if (source == null) throw new InvalidOperationException("缺少月兔枪口粒子资产");
                var go = UnityEngine.Object.Instantiate(source); go.name = "RapidBlasterExplosion";
                try
                {
                    foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        var main = ps.main; main.loop = false; main.playOnAwake = false; main.duration = .45f;
                        main.startLifetime = new ParticleSystem.MinMaxCurve(.16f, .32f);
                        main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 2.8f);
                        main.startSize = new ParticleSystem.MinMaxCurve(.04f, .11f);
                        main.maxParticles = 32; main.simulationSpace = ParticleSystemSimulationSpace.Local;
                        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                        var shape = ps.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = .05f;
                        var emission = ps.emission; emission.enabled = true; emission.rateOverTime = 0;
                        emission.SetBursts(new[] { new ParticleSystem.Burst(0, 28) });
                        var color = ps.colorOverLifetime; color.enabled = true;
                        var gradient = new Gradient();
                        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                            new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, .25f), new GradientAlphaKey(0, 1) });
                        color.color = gradient;
                        var renderer = ps.GetComponent<ParticleSystemRenderer>();
                        renderer.renderMode = ParticleSystemRenderMode.Billboard;
                        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                        var collision = ps.collision; collision.enabled = false;
                        var trigger = ps.trigger; trigger.enabled = false;
                    }
                    PrefabUtility.SaveAsPrefabAsset(go, path);
                }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            }
            var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(Root + "/RocketLauncherGirlAmmoConfig.asset");
            ammo.explosionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            EditorUtility.SetDirty(ammo); AssetDatabase.SaveAssetIfDirty(ammo);
            WeaponConfigValidation.Validate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(Root + "/RocketLauncherGirlWeaponConfig.asset").Snapshot());
        }
    }
}
#endif
