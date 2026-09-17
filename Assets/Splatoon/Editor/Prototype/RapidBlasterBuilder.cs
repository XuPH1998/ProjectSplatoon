#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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

        [MenuItem("喷墨对战/武器/重建快速爆破枪球形墨爆")]
        public static void Install()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "/RapidBlasterExplosion.shader");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("球形墨爆 Shader 缺失或编译失败");
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/RapidBlasterExplosion.mat");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, Root + "/RapidBlasterExplosion.mat");
            }
            material.shader = shader;
            EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material);
            var mesh = SaveMesh();
            string path = Root + "/RapidBlasterExplosion.prefab";
            bool existing = File.Exists(path);
            // Editing prefab contents preserves the root fileID and GUID used by AmmoConfig.
            var go = existing ? PrefabUtility.LoadPrefabContents(path) : new GameObject("RapidBlasterExplosion");
            try
            {
                go.name = "RapidBlasterExplosion";
                var core = go.GetComponent<ParticleSystem>();
                if (core == null) core = go.AddComponent<ParticleSystem>();
                Configure(core, mesh, material, 1, 0, .26f, 2, 0, 0);
                Configure(Child(go, "EdgeInk"), mesh, material, 8, 0, .26f, .64f, .64f, 0);
                Configure(Child(go, "Droplets"), mesh, material, 16, .13f, .25f, .18f, .35f, 2.1f);
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally { if (existing) PrefabUtility.UnloadPrefabContents(go); else UnityEngine.Object.DestroyImmediate(go); }
            var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(Root + "/RocketLauncherGirlAmmoConfig.asset");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (ammo.explosionPrefab != prefab)
            { ammo.explosionPrefab = prefab; EditorUtility.SetDirty(ammo); AssetDatabase.SaveAssetIfDirty(ammo); }
            WeaponConfigValidation.Validate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(Root + "/RocketLauncherGirlWeaponConfig.asset").Snapshot());
        }

        static ParticleSystem Child(GameObject root, string name)
        {
            var child = root.transform.Find(name);
            if (child == null) { child = new GameObject(name).transform; child.SetParent(root.transform, false); }
            var particles = child.GetComponent<ParticleSystem>();
            return particles != null ? particles : child.gameObject.AddComponent<ParticleSystem>();
        }

        static void Configure(ParticleSystem ps, Mesh mesh, Material material, int count, float delay, float life, float diameter, float radius, float speed)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.gameObject.layer = 1; // TransparentFX: normal depth-tested rendering, outside flight fusion.
            ps.transform.localPosition = Vector3.zero; ps.transform.localRotation = Quaternion.identity; ps.transform.localScale = Vector3.one;
            var main = ps.main; main.loop = false; main.playOnAwake = false; main.duration = .05f;
            main.startDelay = delay; main.startLifetime = life;
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * .8f, speed);
            main.startSize3D = false; main.startSize = new ParticleSystem.MinMaxCurve(diameter * (speed > 0 ? .5f : 1), diameter);
            main.startRotation3D = true;
            main.startRotationX = main.startRotationY = main.startRotationZ = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
            main.startColor = Color.white; main.gravityModifier = 0; main.simulationSpeed = 1; main.useUnscaledTime = false;
            main.maxParticles = count; main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy; main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.stopAction = ParticleSystemStopAction.None;
            var shape = ps.shape; shape.enabled = radius > 0; shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius; shape.radiusThickness = speed > 0 ? .8f : 0;
            shape.scale = Vector3.one; shape.position = Vector3.zero; shape.rotation = Vector3.zero;
            var emission = ps.emission; emission.enabled = true; emission.rateOverTime = emission.rateOverDistance = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0, (short)count) });
            var size = ps.sizeOverLifetime; size.enabled = true; size.separateAxes = false;
            size.size = new ParticleSystem.MinMaxCurve(1, speed > 0
                ? Curve(0, .7f, .2f, 1, .6f, .8f, 1, .02f)
                : Curve(0, .12f, .06f / life, 1, .13f / life, 1, 1, .92f));
            var color = ps.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, .5f), new GradientAlphaKey(0, 1) });
            color.color = gradient;
            var collision = ps.collision; collision.enabled = false;
            var trigger = ps.trigger; trigger.enabled = false;
            var velocity = ps.velocityOverLifetime; velocity.enabled = false;
            var limit = ps.limitVelocityOverLifetime; limit.enabled = false;
            var force = ps.forceOverLifetime; force.enabled = false;
            var noise = ps.noise; noise.enabled = false;
            var rotation = ps.rotationOverLifetime; rotation.enabled = false;
            var rotationSpeed = ps.rotationBySpeed; rotationSpeed.enabled = false;
            var colorSpeed = ps.colorBySpeed; colorSpeed.enabled = false;
            var sizeSpeed = ps.sizeBySpeed; sizeSpeed.enabled = false;
            var sheet = ps.textureSheetAnimation; sheet.enabled = false;
            var trails = ps.trails; trails.enabled = false;
            var lights = ps.lights; lights.enabled = false;
            var custom = ps.customData; custom.enabled = false;
            var sub = ps.subEmitters; sub.enabled = false; while (sub.subEmittersCount > 0) sub.RemoveSubEmitter(0);
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.enabled = true; renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.mesh = mesh;
            renderer.sharedMaterials = new[] { material }; renderer.alignment = ParticleSystemRenderSpace.Local;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.Distance; renderer.enableGPUInstancing = false;
            renderer.pivot = Vector3.zero; renderer.flip = Vector3.zero; renderer.maxParticleSize = 1;
            renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position,
                ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
                ParticleSystemVertexStream.AgePercent });
        }

        static AnimationCurve Curve(params float[] values)
        {
            var keys = new Keyframe[values.Length / 2];
            for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(values[i * 2], values[i * 2 + 1]);
            return new AnimationCurve(keys);
        }

        static Mesh SaveMesh()
        {
            // A diameter-one closed surface: hierarchy scale is the gameplay blast radius.
            const int rings = 16, sides = 24;
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
            float maximum = 0;
            for (int r = 0; r <= rings; r++) for (int s = 0; s <= sides; s++)
            {
                float latitude = r * Mathf.PI / rings, longitude = s * Mathf.PI * 2 / sides;
                var n = new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
                float wave = 1 + .035f * Mathf.Sin(n.x * 7 + n.y * 3) * Mathf.Cos(n.z * 6 - n.y * 2) + .02f * Mathf.Sin(n.y * 8 + n.z * 3);
                maximum = Mathf.Max(maximum, wave);
                vertices.Add(n * wave); normals.Add(n); uv.Add(new Vector2((float)s / sides, (float)r / rings));
                if (r == rings || s == sides) continue;
                int i = r * (sides + 1) + s, j = i + sides + 1;
                triangles.AddRange(new[] { i, i + 1, j, i + 1, j + 1, j });
            }
            for (int i = 0; i < vertices.Count; i++) vertices[i] *= .5f / maximum;
            var data = new Mesh { name = "RapidBlasterInkSphere" };
            data.SetVertices(vertices); data.SetNormals(normals); data.SetUVs(0, uv); data.SetTriangles(triangles, 0); data.RecalculateBounds();
            string path = Root + "/RapidBlasterInkSphere.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null) { mesh = data; AssetDatabase.CreateAsset(mesh, path); }
            else { EditorUtility.CopySerialized(data, mesh); UnityEngine.Object.DestroyImmediate(data); EditorUtility.SetDirty(mesh); }
            AssetDatabase.SaveAssetIfDirty(mesh); return mesh;
        }
    }
}
#endif
