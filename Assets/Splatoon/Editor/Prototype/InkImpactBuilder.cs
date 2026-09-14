#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Combat;

namespace Splatoon.Editor
{
    public static class InkImpactBuilder
    {
        const string Root = "Assets/GameResource/Effects/Ink/";
        public const string PrefabPath = Root + "Prefabs/InkImpact.prefab";

        [MenuItem("喷墨对战/内容/重建墨弹命中泼溅")]
        public static void Build()
        {
            Directory.CreateDirectory(Root + "Meshes");
            AssetDatabase.Refresh();
            var shader = Shader.Find("Splatoon/InkImpact");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("InkImpact shader is missing or has errors");
            var splashMat = Material("InkImpactSplash", shader, true);
            var dropMat = Material("InkImpactDroplet", shader, false);
            var quad = SaveMesh("InkImpactSheet", Sheet());
            var tear = SaveMesh("InkImpactTeardrop", Drop(true));
            var bead = SaveMesh("InkImpactBead", Drop(false));
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                // Edit the existing root and root ParticleSystem in place: scene/Addressable fileIDs survive.
                var splash = root.GetComponent<ParticleSystem>();
                var streaks = root.transform.GetChild(0).GetComponent<ParticleSystem>();
                streaks.name = "Streaks";
                var dropTransform = root.transform.Find("Droplets");
                var drops = dropTransform != null ? dropTransform.GetComponent<ParticleSystem>() : new GameObject("Droplets").AddComponent<ParticleSystem>();
                drops.transform.SetParent(root.transform, false);
                Configure(splash, quad, splashMat, 2, 0);
                Configure(streaks, tear, dropMat, 12, .45f);
                Configure(drops, bead, dropMat, 18, .8f);
                var size = splash.sizeOverLifetime;
                size.size = new ParticleSystem.MinMaxCurve(1, Curve(0, .12f, .25f, 1, 1, 1.08f));
                size = streaks.sizeOverLifetime; size.separateAxes = true;
                size.x = size.y = new ParticleSystem.MinMaxCurve(1, Curve(0, .85f, .22f, 1, 1, .05f));
                size.z = new ParticleSystem.MinMaxCurve(1, Curve(0, .7f, .25f, 1.12f, 1, .12f));
                size = drops.sizeOverLifetime;
                size.size = new ParticleSystem.MinMaxCurve(1, Curve(0, 1, .55f, .85f, 1, .05f));
                var effect = root.GetComponent<InkImpactEffect>() ?? root.AddComponent<InkImpactEffect>();
                effect.Splash = splash; effect.Streaks = streaks; effect.Droplets = drops;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            Debug.Log("[INK-IMPACT] Three-layer splash assets authored; existing prefab/root ParticleSystem references preserved.");
        }

        static Material Material(string name, Shader shader, bool atlas)
        {
            string path = Root + "Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader; material.SetFloat("_AtlasMask", atlas ? 1 : 0); material.SetFloat("_Smoothness", .55f);
            material.SetFloat("_Cull", atlas ? (float)CullMode.Off : (float)CullMode.Back);
            material.SetTexture("_ShapeAtlas", AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "Textures/InkSplatAtlas-Reference.png"));
            EditorUtility.SetDirty(material); return material;
        }
        static Mesh SaveMesh(string name, Mesh data)
        {
            string path = Root + "Meshes/" + name + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null) { mesh = data; mesh.name = name; AssetDatabase.CreateAsset(mesh, path); }
            else { EditorUtility.CopySerialized(data, mesh); mesh.name = name; UnityEngine.Object.DestroyImmediate(data); EditorUtility.SetDirty(mesh); }
            return mesh;
        }
        static void Configure(ParticleSystem ps, Mesh mesh, Material material, int cap, float gravity)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.gameObject.layer = 1; // TransparentFX is drawn normally, outside the InkVisual fusion pass.
            ps.transform.localPosition = Vector3.zero; ps.transform.localRotation = Quaternion.identity; ps.transform.localScale = Vector3.one;
            var main = ps.main; main.duration = .05f; main.loop = false; main.playOnAwake = false;
            main.startDelay = 0; main.startLifetime = .3f; main.startSpeed = 0;
            main.startSize3D = true; main.startSizeX = main.startSizeY = main.startSizeZ = 1;
            main.startRotation3D = true; main.startRotationX = main.startRotationY = main.startRotationZ = 0;
            main.startColor = Color.white; main.gravityModifier = gravity; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.simulationSpeed = 1; main.useUnscaledTime = false; main.scalingMode = ParticleSystemScalingMode.Local;
            main.maxParticles = cap; main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate; main.stopAction = ParticleSystemStopAction.None;
            ps.useAutoRandomSeed = false; ps.randomSeed = 1;
            var emission = ps.emission; emission.enabled = false; emission.SetBursts(Array.Empty<ParticleSystem.Burst>());
            var shape = ps.shape; shape.enabled = false;
            var sub = ps.subEmitters; sub.enabled = false; while (sub.subEmittersCount > 0) sub.RemoveSubEmitter(0);
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
            var size = ps.sizeOverLifetime; size.enabled = true; size.separateAxes = false;
            var color = ps.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, .65f), new GradientAlphaKey(0, 1) });
            color.color = gradient;
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.enabled = true; renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.mesh = mesh;
            renderer.sharedMaterials = new[] { material }; renderer.alignment = ParticleSystemRenderSpace.World;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.Distance; renderer.enableGPUInstancing = false;
            renderer.pivot = Vector3.zero; renderer.flip = Vector3.zero; renderer.maxParticleSize = 1;
            renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position,
                ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
                ParticleSystemVertexStream.AgePercent, ParticleSystemVertexStream.Custom1X });
        }
        static AnimationCurve Curve(params float[] values)
        {
            var keys = new Keyframe[values.Length / 2];
            for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(values[i * 2], values[i * 2 + 1]);
            return new AnimationCurve(keys);
        }
        static Mesh Sheet()
        {
            var mesh = new Mesh { vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        static Mesh Drop(bool tear)
        {
            const int rings = 9, sides = 10;
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float t = (float)r / rings, radius = Mathf.Sin(t * Mathf.PI) * .5f;
                if (tear) radius *= Mathf.Lerp(.12f, 1.3f, Mathf.Pow(t, .55f));
                float derivative = .5f * Mathf.PI * Mathf.Cos(t * Mathf.PI);
                if (tear) derivative = derivative * Mathf.Lerp(.12f, 1.3f, Mathf.Pow(t, .55f)) +
                    .5f * Mathf.Sin(t * Mathf.PI) * 1.18f * .55f * Mathf.Pow(Mathf.Max(.0001f, t), -.45f);
                for (int s = 0; s <= sides; s++)
                {
                    float a = s * Mathf.PI * 2 / sides;
                    vertices.Add(new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, t - .5f));
                    normals.Add(r == 0 ? Vector3.back : r == rings ? Vector3.forward : new Vector3(Mathf.Cos(a), Mathf.Sin(a), -derivative).normalized);
                    uv.Add(new Vector2((float)s / sides, t));
                    if (r == rings || s == sides) continue;
                    int i = r * (sides + 1) + s, j = i + sides + 1;
                    triangles.AddRange(new[] { i, i + 1, j, i + 1, j + 1, j });
                }
            }
            var mesh = new Mesh(); mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds(); return mesh;
        }
    }
}
#endif
