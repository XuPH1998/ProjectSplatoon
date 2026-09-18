#if UNITY_EDITOR
using System;
using System.IO;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class ExplosherBuilder
    {
        const string Root = "Assets/GameResource/Weapons/ShotgunGirl";
        static double next;
        static ExplosherBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            if (File.Exists("Temp/Explosher/build"))
            {
                File.Delete("Temp/Explosher/build"); Directory.CreateDirectory("Reports/Explosher");
                try { PrototypeBuilder.BuildWindows(); File.WriteAllText("Reports/Explosher/build-result.txt", "PASS " + DateTime.UtcNow.ToString("O") + " Builds/Windows/InkLan.exe"); }
                catch (Exception e) { File.WriteAllText("Reports/Explosher/build-result.txt", e.ToString()); Debug.LogException(e); }
                return;
            }
            const string request = "Temp/Explosher/install";
            if (!File.Exists(request)) return;
            File.Delete(request); Directory.CreateDirectory("Reports/Explosher");
            try { Install(); File.WriteAllText("Reports/Explosher/assets-result.txt", "PASS " + DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText("Reports/Explosher/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/武器/重建爆炸泼桶表现")]
        public static void Install()
        {
            var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(Root + "/ShotgunGirlAmmoConfig.asset");
            const string flightPath = Root + "/ExplosherFlight.prefab";
            if (!File.Exists(flightPath)) AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(ammo.flightPrefab), flightPath);
            var flight = PrefabUtility.LoadPrefabContents(flightPath);
            try
            {
                flight.name = "ExplosherFlight";
                var ps = flight.GetComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main; main.startSize = 1.048384f;
                var size = ps.sizeOverLifetime; size.enabled = false;
                PrefabUtility.SaveAsPrefabAsset(flight, flightPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(flight); }
            const string explosionPath = Root + "/ExplosherExplosion.prefab";
            if (!File.Exists(explosionPath)) AssetDatabase.CopyAsset("Assets/GameResource/Weapons/RocketLauncherGirl/RapidBlasterExplosion.prefab", explosionPath);
            var explosion = PrefabUtility.LoadPrefabContents(explosionPath);
            try
            {
                explosion.name = "ExplosherExplosion";
                foreach (var ps in explosion.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    var main = ps.main; main.startSize3D = true; main.simulationSpace = ParticleSystemSimulationSpace.Local;
                    ps.GetComponent<ParticleSystemRenderer>().alignment = ParticleSystemRenderSpace.Local;
                    float size = ps.gameObject == explosion ? 2 : ps.name == "EdgeInk" ? .6f : .18f;
                    main.startSizeX = size; main.startSizeY = size; main.startSizeZ = size * .28f;
                    var shape = ps.shape; shape.scale = new Vector3(1, 1, .3f);
                }
                PrefabUtility.SaveAsPrefabAsset(explosion, explosionPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(explosion); }
            ammo.flightPrefab = AssetDatabase.LoadAssetAtPath<ParticleSystem>(flightPath);
            ammo.explosionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(explosionPath);
            EditorUtility.SetDirty(ammo); AssetDatabase.SaveAssetIfDirty(ammo);
            WeaponConfigValidation.Validate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(Root + "/ShotgunGirlWeaponConfig.asset").Snapshot());
        }
    }
}
#endif
