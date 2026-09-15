#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Prototype;
using Splatoon.Combat;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class HeroChangeZoneSetup
    {
        static double _nextPoll;
        static HeroChangeZoneSetup() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/HeroChangeZone/prepare";
            if (!File.Exists(request)) return;
            File.Delete(request);
            try { Prepare(); File.WriteAllText("Temp/HeroChangeZone/ready.txt", DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText("Temp/HeroChangeZone/error.txt", e.ToString()); Debug.LogException(e); }
        }

        [MenuItem("喷墨对战/地图/增补出生区换英雄触发器")]
        public static void Prepare()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出运行模式");
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(TrainingGroundBuilder.ScenePath);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (!opened && scene.isDirty) throw new InvalidOperationException("请先保存地图中的未保存编辑");
            if (opened) scene = EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var arena = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PrototypeArena>()).Single();
                AddMissingZones(arena);
                Validate(arena);
                // These triggers do not change paint or walkability. Retain all baked surface data.
                Physics.SyncTransforms(); arena.RegisterSurfaces();
                arena.BakedTopology = arena.ComputeTopology();
                TrainingGroundBuilder.Validate(arena);
                EditorUtility.SetDirty(arena); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }

        public static void AddMissingZones(PrototypeArena arena)
        {
            var zones = arena.GetComponentsInChildren<HeroChangeZone>(true);
            for (byte team = 1; team <= 2; team++)
            {
                if (zones.Any(z => z.Team == team)) continue;
                var group = arena.transform.Find("06 Hero Change Zones");
                if (group == null)
                {
                    group = new GameObject("06 Hero Change Zones").transform;
                    group.SetParent(arena.transform, false);
                }
                var go = new GameObject(team == 1 ? "Pink_HeroChangeZone" : "Blue_HeroChangeZone");
                go.transform.SetParent(group, false);
                go.transform.position = new Vector3(0, 2, team == 1 ? -28 : 28);
                var zone = go.AddComponent<HeroChangeZone>(); zone.Team = team;
                zone.Volume.isTrigger = true; zone.Volume.size = new Vector3(16, 4, 6);
            }
            arena.RegisterHeroChangeZones();
        }

        public static void Validate(PrototypeArena arena)
        {
            var zones = arena.GetComponentsInChildren<HeroChangeZone>(true);
            foreach (var zone in zones)
            {
                var box = zone.Volume;
                if (zone.Team < 1 || zone.Team > 2 || box == null || !box.isTrigger ||
                    box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0)
                    throw new InvalidOperationException("换英雄触发区阵营、碰撞器或尺寸无效：" + zone.name);
            }
            arena.RegisterHeroChangeZones();
            if (arena.SpawnPoints == null || arena.SpawnPoints.Length != 2 * TeamSelectionRules.Capacity)
                throw new InvalidOperationException("换英雄区域需要八个出生点");
            for (int i = 0; i < arena.SpawnPoints.Length; i++)
                if (arena.SpawnPoints[i] == null || !arena.IsInHeroChangeZone((byte)(i / TeamSelectionRules.Capacity + 1), arena.SpawnPoints[i].position))
                    throw new InvalidOperationException("出生点未被本方有效换英雄区域覆盖：" + i);
        }
    }
}
#endif
