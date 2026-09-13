#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    /// <summary>Matched four-visual Editor comparison; not a player-build GPU benchmark.</summary>
    public static class CombatGirlsPerformanceValidation
    {
        [Serializable] sealed class Sample
        {
            public string character, device, quality;
            public int instances = 4, width = 1280, height = 720, samples, renderers, materialSlots, vertices, triangles;
            public long uniqueMeshBytes, uniqueTextureBytes;
            public double editorFrameP95Ms, renderSubmissionCpuP95Ms;
            public int editorDrawCallsP95, editorBatchesP95;
            public bool editorDrawCountersAvailable;
        }
        [Serializable] sealed class Report
        {
            public string limits = "Same machine, training scene, camera, 720p render request, quality and four animated visuals. Editor pacing and render submission CPU timings include Editor overhead; these are not GPU frame timings or four networked player build acceptance.";
            public List<Sample> results = new();
        }
        static readonly Report Output = new();
        static readonly List<double> Frames = new(), Submission = new();
        static readonly List<int> Draws = new(), Batches = new();
        static GameObject[] _characters;
        static GameObject _placements;
        static Vector3 _center;
        static Camera _camera;
        static RenderTexture _target;
        static Sample _sample;
        static int _pass, _frame;
        static double _previous;
        static readonly Stopwatch Clock = new();
        public static void Run()
        {
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new InvalidOperationException("GPU required");
                typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                    new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));
                EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity");
                _camera = Camera.main; _camera.enabled = false;
                Physics.SyncTransforms(); _center = FindClearCenter();
                _camera.transform.position = _center + new Vector3(0, 3, 7); _camera.transform.LookAt(_center + Vector3.up); _camera.aspect = 16f / 9; _camera.fieldOfView = 40;
                _target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32); _target.Create();
                Output.results.Clear(); _pass = 0; Begin(); EditorApplication.update += Tick;
            }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        static void Begin()
        {
            string path = _pass == 0 ? "Assets/GameResource/Characters/Jammo/Prefabs/JammoVisual.prefab" : CombatGirlsBuilder.CharacterPath;
            var prefab = CombatGirlsBuilder.Load<GameObject>(path);
            _placements = new GameObject("FourVisualPlacements");
            _characters = Enumerable.Range(0, 4).Select(i =>
            {
                var placement = new GameObject("Placement" + i).transform; placement.SetParent(_placements.transform);
                placement.position = _center + new Vector3((i % 2) * 2 - 1, 0, (i / 2) * 2 - 1);
                return UnityEngine.Object.Instantiate(prefab, placement);
            }).ToArray();
            foreach (var go in _characters)
            {
                foreach (var animator in go.GetComponentsInChildren<Animator>())
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false;
                    if (_pass == 0) { animator.SetFloat("X", 1); animator.SetFloat("Y", 0); animator.SetFloat("Blend", 1); animator.SetBool("Grounded", true); animator.SetBool("shooting", true); }
                }
                var view = go.GetComponent<InkCharacterView>(); if (view != null) view.InitializeBindings();
            }
            var renderers = _characters.SelectMany(g => g.GetComponentsInChildren<Renderer>()).Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToArray();
            var meshes = renderers.Select(r => r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh).Where(m => m != null).ToArray();
            var textures = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct()
                .SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture)).Where(t => t != null).Distinct().ToArray();
            _sample = new Sample { character = _pass == 0 ? "Jammo" : "RifleGirl", device = SystemInfo.graphicsDeviceName,
                quality = QualitySettings.names[QualitySettings.GetQualityLevel()], renderers = renderers.Length,
                materialSlots = renderers.Sum(r => r.sharedMaterials.Length), vertices = meshes.Sum(m => m.vertexCount),
                triangles = meshes.Sum(m => Enumerable.Range(0, m.subMeshCount).Sum(i => (int)m.GetIndexCount(i) / 3)),
                uniqueMeshBytes = meshes.Distinct().Sum(m => Profiler.GetRuntimeMemorySizeLong(m)), uniqueTextureBytes = textures.Sum(t => Profiler.GetRuntimeMemorySizeLong(t)) };
            _frame = 0; Frames.Clear(); Submission.Clear(); Draws.Clear(); Batches.Clear(); Clock.Restart(); _previous = 0;
        }
        static void Tick()
        {
            try
            {
                double now = Clock.Elapsed.TotalMilliseconds, dt = now - _previous; _previous = now;
                foreach (var go in _characters)
                {
                    var view = go.GetComponent<InkCharacterView>();
                    if (_pass == 1) view.Present(new PlayerSnapshot { Health = 100, Ink = 100, Grounded = true, Team = 1, Velocity = Vector3.right * 5, Firing = true }, 1f / 60, _frame / 60.0);
                    foreach (var animator in go.GetComponentsInChildren<Animator>()) animator.Update(1f / 60);
                    if (_pass == 1) view.ApplyAim();
                }
                double begin = Clock.Elapsed.TotalMilliseconds;
                RenderPipeline.SubmitRenderRequest(_camera, new UniversalRenderPipeline.SingleCameraRequest { destination = _target });
                double elapsed = Clock.Elapsed.TotalMilliseconds - begin;
                if (_frame++ >= 120) { Frames.Add(dt); Submission.Add(elapsed); Draws.Add(UnityStats.drawCalls); Batches.Add(UnityStats.batches); }
                if (_frame < 480) return;
                _sample.samples = Frames.Count;
                _sample.editorFrameP95Ms = Percentile(Frames); _sample.renderSubmissionCpuP95Ms = Percentile(Submission);
                _sample.editorDrawCallsP95 = (int)Percentile(Draws.Select(v => (double)v)); _sample.editorBatchesP95 = (int)Percentile(Batches.Select(v => (double)v));
                _sample.editorDrawCountersAvailable = _sample.editorDrawCallsP95 > 0;
                Output.results.Add(_sample);
                Capture(_pass == 0 ? "four-visual-jammo" : "four-visual-riflegirl");
                UnityEngine.Object.DestroyImmediate(_placements);
                if (++_pass < 2) { Begin(); return; }
                EditorApplication.update -= Tick; _target.Release(); UnityEngine.Object.DestroyImmediate(_target);
                File.WriteAllText("Docs/CombatGirls/four-visual-editor-comparison.json", JsonUtility.ToJson(Output, true));
                UnityEngine.Debug.Log("[CombatGirls-PERF] Matched four-visual Editor comparison complete"); EditorApplication.Exit(0);
            }
            catch (Exception ex) { EditorApplication.update -= Tick; UnityEngine.Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        static double Percentile(IEnumerable<double> values) { var sorted = values.OrderBy(x => x).ToArray(); return sorted[(int)((sorted.Length - 1) * .95)]; }
        static Vector3 FindClearCenter()
        {
            for (int z = -18; z <= 18; z += 2)
            for (int x = -8; x <= 8; x += 2)
            {
                Vector3 center = new(x, .02f, z), camera = center + new Vector3(0, 3, 7);
                bool clear = true;
                for (int i = 0; i < 4; i++)
                {
                    var feet = center + new Vector3((i % 2) * 2 - 1, 0, (i / 2) * 2 - 1);
                    if (!Physics.Raycast(feet + Vector3.up * .2f, Vector3.down, .3f, ~(1 << 8)) || Physics.CheckCapsule(feet + Vector3.up * .4f, feet + Vector3.up * 1.5f, .3f, ~(1 << 8))) { clear = false; break; }
                    foreach (float height in new[] { .2f, 1f, 1.5f })
                        if (Physics.Linecast(camera, feet + Vector3.up * height, ~(1 << 8))) clear = false;
                }
                if (clear) return center;
            }
            throw new InvalidOperationException("No clear four-visual benchmark location");
        }
        static void Capture(string name)
        {
            // Editor frameCount does not advance like Play Mode. Bake only the
            // evidence image after timing, so cached skinning cannot mix poses.
            var baked = new List<GameObject>();
            var meshes = new List<Mesh>();
            foreach (var skin in _placements.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.enabled))
            {
                var mesh = new Mesh(); skin.BakeMesh(mesh); meshes.Add(mesh);
                var go = new GameObject("EvidencePose", typeof(MeshFilter), typeof(MeshRenderer)); go.transform.SetParent(skin.transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh; go.GetComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                skin.enabled = false; baked.Add(go);
            }
            RenderPipeline.SubmitRenderRequest(_camera, new UniversalRenderPipeline.SingleCameraRequest { destination = _target });
            var old = RenderTexture.active; RenderTexture.active = _target;
            var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
            File.WriteAllBytes("Docs/CombatGirls/Screenshots/" + name + ".png", texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture); RenderTexture.active = old;
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
#endif
