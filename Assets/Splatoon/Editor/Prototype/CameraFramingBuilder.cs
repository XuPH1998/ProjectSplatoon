#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Splatoon.Combat;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class CameraFramingBuilder
    {
        public const string Report = "Reports/CameraReticle";
        static CameraFramingBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            const string request = "Temp/CameraReticle/calibrate";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorUtility.scriptCompilationFailed || !File.Exists(request)) return;
            var active = typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi).GetMethod("IsRunActive", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (active == null || (bool)active.Invoke(null, null)) return;
            string token = File.GetLastWriteTimeUtc(request).Ticks.ToString();
            if (SessionState.GetString("CameraReticle.CalibrationRefresh", "") != token)
            { SessionState.SetString("CameraReticle.CalibrationRefresh", token); AssetDatabase.Refresh(); return; }
            File.Delete(request); Directory.CreateDirectory(Report);
            try { CalibrateAll(); File.WriteAllText(Report + "/calibration-status.txt", "PASS"); }
            catch (Exception e) { File.WriteAllText(Report + "/calibration-status.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/相机/校准全部角色构图")]
        public static void CalibrateAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before calibration");
            Directory.CreateDirectory(Report);
            var rows = new List<string> { "hero,height,pivot_y,offset_x,offset_z,vertical_fov,top,bottom,height_fraction" };
            foreach (var guid in AssetDatabase.FindAssets("t:CharacterPresentationProfile", new[] { "Assets/GameResource/Characters" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string hero = Path.GetFileName(Path.GetDirectoryName(path));
                string prefab = Path.GetDirectoryName(path).Replace('\\', '/') + "/Prefabs/" + hero + "Visual.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefab) == null) continue;
                var root = PrefabUtility.LoadPrefabContents(prefab);
                try
                {
                    var view = root.GetComponent<InkCharacterView>();
                    var range = Apply(view);
                    var p = view.Profile;
                    rows.Add(FormattableString.Invariant($"{hero},{p.CameraReferenceHeight:R},{p.CameraPivot.y:R},{p.CameraOffset.x:R},{p.CameraOffset.z:R},{p.CameraVerticalFov:R},{range.x:R},{range.y:R},{range.y-range.x:R}"));
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets(); File.WriteAllLines(Report + "/camera-parameters.csv", rows);
        }
        // Calibrates only asset data. Never serializes the sampled pose back into the prefab.
        public static Vector2 Apply(InkCharacterView view)
        {
            var animator = view.Animator;
            animator.Rebind();
            animator.SetFloat("MoveX", 0); animator.SetFloat("MoveY", 0);
            animator.Play("Base Layer.Locomotion", 0, 0);
            for (int layer = 1; layer < animator.layerCount; layer++) animator.SetLayerWeight(layer, 0);
            animator.Update(.1f);
            var vertices = BodyVertices(view);
            if (vertices.Count == 0) throw new InvalidOperationException("No visible body mesh: " + view.name);
            float min = vertices.Min(v => v.y), height = vertices.Max(v => v.y) - min;
            if (height < .1f) throw new InvalidOperationException("Invalid body height: " + view.name);
            var p = view.Profile;
            p.CameraVerticalFov = 60; p.CameraReferenceHeight = height;
            p.CameraPivot = new Vector3(0, min + 1.35f * height, 0);
            p.CameraOffset = new Vector3(.05f * height, 0, -3 * height);
            var rotation = Quaternion.Euler(12, 0, 0);
            // Solve for 28.5% body height and 73.75% vertical midpoint at the canonical pose.
            for (int i = 0; i < 20; i++)
            {
                var range = Project(vertices, p, rotation);
                float span = range.y - range.x;
                if (Mathf.Abs(span - .285f) < .0002f && Mathf.Abs((range.x + range.y) * .5f - .7375f) < .0002f) break;
                p.CameraOffset.z *= Mathf.Clamp(span / .285f, .9f, 1.1f);
                p.CameraPivot.y += (.7375f - (range.x + range.y) * .5f) * 2 * Mathf.Abs(p.CameraOffset.z) * Mathf.Tan(30 * Mathf.Deg2Rad);
            }
            if (p.Paper != null) { p.Paper.CameraOffset = p.CameraPivot; EditorUtility.SetDirty(p.Paper); }
            EditorUtility.SetDirty(p);
            var final = Project(vertices, p, rotation);
            if (final.x < .58f || final.x > .63f || final.y < .84f || final.y > .9f || final.y - final.x < .25f || final.y - final.x > .3f)
                throw new InvalidOperationException("Camera framing did not converge: " + view.name);
            animator.Rebind();
            return final;
        }
        public static List<Vector3> BodyVertices(InkCharacterView view)
        {
            var points = new List<Vector3>();
            foreach (var renderer in view.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer == view.TeamMarker || renderer is ParticleSystemRenderer ||
                    renderer.GetComponentInParent<HeroWeaponBindings>() != null ||
                    (view.Weapon != null && renderer.transform.IsChildOf(view.Weapon)) ||
                    (view.LeftWeapon != null && renderer.transform.IsChildOf(view.LeftWeapon))) continue;
                Mesh mesh = null; bool baked = renderer is SkinnedMeshRenderer;
                if (renderer is SkinnedMeshRenderer skin) { mesh = new Mesh(); skin.BakeMesh(mesh); }
                else mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                foreach (var vertex in mesh.vertices) points.Add(view.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertex)));
                if (baked) Object.DestroyImmediate(mesh);
            }
            return points;
        }
        static Vector2 Project(List<Vector3> vertices, CharacterPresentationProfile p, Quaternion rotation)
        {
            Vector3 camera = p.CameraPivot + rotation * p.CameraOffset;
            var inverse = Quaternion.Inverse(rotation);
            float top = float.PositiveInfinity, bottom = float.NegativeInfinity;
            foreach (var vertex in vertices)
            {
                var local = inverse * (vertex - camera);
                float y = .5f - local.y / (2 * local.z * Mathf.Tan(30 * Mathf.Deg2Rad));
                top = Mathf.Min(top, y); bottom = Mathf.Max(bottom, y);
            }
            return new Vector2(top, bottom);
        }
    }
}
#endif
