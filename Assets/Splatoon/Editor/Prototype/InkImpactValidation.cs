#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;
using Splatoon.Prototype;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    /// <summary>GPU captures and matched four-emitter Editor measurements, never a player-build benchmark.</summary>
    public static class InkImpactValidation
    {
        const string Output = "Reports/InkImpact";
        const string Legacy = "Assets/InkImpactBaseline/InkImpactLegacy.prefab";
        static Camera camera;
        static RenderTexture target;
        static GameObject wall, slope;
        static readonly List<GameObject> effects = new();
        static readonly List<double> cpu = new(), submission = new(), gpu = new();
        static readonly FrameTiming[] timings = new FrameTiming[1];
        static readonly List<string> report = new();
        static int frame, pass;
        static float[] until;
        static bool[] active;
        static bool hasLegacy;

        public static void BuildAndRun() { InkImpactBuilder.Build(); Run(); }

        public static void Run()
        {
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new InvalidOperationException("GPU required");
                Directory.CreateDirectory(Output);
                Setup();
                hasLegacy = AssetDatabase.LoadAssetAtPath<GameObject>(Legacy) != null;
                report.Clear();
                report.Add($"Unity={Application.unityVersion}; GPU={SystemInfo.graphicsDeviceName}; quality={QualitySettings.names[QualitySettings.GetQualityLevel()]}; size=1280x720");
                report.Add("Standalone Editor render requests. Four simulated emitters; no physical multiplayer or player-build performance claim.");
                if (!hasLegacy) report.Add("Legacy fixture absent: before-capture and comparison omitted.");
                foreach (string surface in new[] { "wall", "floor", "slope" })
                    CaptureSequence(surface, false);
                if (hasLegacy) CaptureSequence("wall", true);
                CaptureVariants();
                // A wall must fully occlude the effect from its reverse side.
                CheckOcclusion();
                pass = hasLegacy ? 0 : 1;
                BeginPerformance();
                EditorApplication.update += Tick;
            }
            catch (Exception e) { Fail(e); }
        }

        static void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(.48f, .52f, .55f);
            var light = new GameObject("Impact key light").AddComponent<Light>(); light.type = LightType.Directional;
            light.intensity = 1.15f; light.transform.rotation = Quaternion.Euler(38, -35, 0);
            camera = new GameObject("Impact camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.28f, .34f, .36f);
            camera.fieldOfView = 42; camera.nearClipPlane = .03f; camera.farClipPlane = 80;
            camera.allowHDR = true; camera.allowMSAA = false;
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32); target.Create(); camera.aspect = 1280f / 720;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.SetColor("_BaseColor", new Color(.43f,.44f,.43f));
            material.SetFloat("_Smoothness", .16f);
            wall = Surface("Wall", new Vector3(0, 1.3f, -.13f), new Vector3(8, 5, .25f), material);
            Surface("Floor", new Vector3(0, -.13f, 1), new Vector3(12, .25f, 12), material);
            slope = Surface("Slope", new Vector3(0, .25f, 0), new Vector3(5, .15f, 3), material);
            slope.transform.rotation = Quaternion.Euler(35, 0, 0); slope.SetActive(false);
        }
        static GameObject Surface(string name, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.position = position; go.transform.localScale = scale; go.GetComponent<Renderer>().sharedMaterial = material; return go;
        }
        static void Place(string surface, bool side = false)
        {
            wall.SetActive(surface == "wall"); slope.SetActive(surface == "slope");
            Vector3 point = Point(surface);
            camera.transform.position = point + (surface == "wall" ? new Vector3(side ? 3.8f : 1.4f, .45f, side ? 2.2f : 4.2f) : new Vector3(2.6f, 3.3f, 3.5f));
            camera.transform.LookAt(point + Vector3.up * .08f);
        }
        static Vector3 Point(string surface) => surface == "wall" ? new Vector3(0, 1.4f, 0) : surface == "slope" ? slope.transform.TransformPoint(Vector3.up * .5f) : Vector3.zero;
        static Vector3 Normal(string surface) => surface == "wall" ? Vector3.forward : surface == "slope" ? slope.transform.up : Vector3.up;
        static void CreateEffect(bool legacy)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(legacy ? Legacy : InkImpactBuilder.PrefabPath);
            if (prefab == null) throw new InvalidOperationException("Missing impact prefab");
            effects.Add(Object.Instantiate(prefab));
        }
        static void Play(GameObject go, Vector3 point, Vector3 normal, uint id, byte team)
        {
            go.SetActive(true); go.transform.SetPositionAndRotation(point + normal * .02f, Quaternion.LookRotation(normal));
            var effect = go.GetComponent<InkImpactEffect>();
            if (effect != null) effect.Play(PrototypeArena.TeamColor(team), InkImpactEffect.Seed(new InkImpact { Round = 1, Id = id }));
            else
            {
                var root = go.GetComponent<ParticleSystem>(); root.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>())
                { ps.useAutoRandomSeed = false; ps.randomSeed = id + 1; var main = ps.main; main.startColor = PrototypeArena.TeamColor(team); }
                root.Play(false);
            }
        }
        static void Simulate(GameObject go, float age)
        { var ps = go.GetComponent<ParticleSystem>(); ps.Simulate(age, true, false, false); ps.Pause(true); }
        static void CaptureSequence(string surface, bool legacy)
        {
            ClearEffects(); CreateEffect(legacy); Place(surface);
            string folder = Output + "/" + (legacy ? "before-" : "after-") + surface;
            Directory.CreateDirectory(folder);
            for (int i = 0; i <= 42; i++)
            {
                Play(effects[0], Point(surface), Normal(surface), 137, 1);
                Simulate(effects[0], Mathf.Max(.001f, i / 60f));
                Capture(folder + "/" + i.ToString("D3") + ".png");
            }
            Place(surface, true); Play(effects[0], Point(surface), Normal(surface), 137, 1); Simulate(effects[0], .12f);
            Capture(Output + "/" + (legacy ? "before-" : "after-") + surface + "-side.png");
            report.Add((legacy ? "before " : "after ") + surface + ": 43 frames at 60fps plus side view");
            ClearEffects();
        }
        static void CaptureVariants()
        {
            Place("wall");
            Directory.CreateDirectory(Output + "/variants"); CreateEffect(false);
            for (uint i = 1; i <= 20; i++)
            { Play(effects[0], Point("wall"), Vector3.forward, i, (byte)(i % 2 + 1)); Simulate(effects[0], .10f); Capture(Output + "/variants/" + i.ToString("D2") + ".png"); }
            ClearEffects();
            for (int i = 0; i < 32; i++)
            {
                CreateEffect(false); uint seed = (uint)(i + 1); float angle = i * 2.399963f;
                Vector3 p = Point("wall") + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * (.15f + .2f * (i % 4));
                Play(effects[i], p, Vector3.forward, seed, (byte)(i % 2 + 1)); Simulate(effects[i], .025f + i % 8 * .035f);
            }
            Capture(Output + "/dense-four-emitters.png"); ClearEffects();
        }
        static void CheckOcclusion()
        {
            Place("wall"); camera.transform.position = new Vector3(0, 1.4f, -3); camera.transform.LookAt(Point("wall"));
            var baseline = Pixels(); CreateEffect(false); Play(effects[0], Point("wall"), Vector3.forward, 17, 1); Simulate(effects[0], .08f);
            var withInk = Pixels(); int changed = 0;
            for (int i = 0; i < baseline.Length; i++) if (Math.Abs(baseline[i].r-withInk[i].r)+Math.Abs(baseline[i].g-withInk[i].g)+Math.Abs(baseline[i].b-withInk[i].b) > 12) changed++;
            report.Add("Reverse-wall visible changed pixels=" + changed);
            if (changed > 10) throw new InvalidOperationException("Impact leaks through the wall: " + changed);
            ClearEffects();
        }
        static Color32[] Pixels()
        {
            Render(); var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0); texture.Apply(); var pixels = texture.GetPixels32();
            Object.DestroyImmediate(texture); RenderTexture.active = previous; return pixels;
        }
        static void Capture(string path)
        {
            Render(); var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0); texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG()); Object.DestroyImmediate(texture); RenderTexture.active = previous;
        }
        static void Render() => RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });

        static void BeginPerformance()
        {
            ClearEffects(); Place("wall");
            for (int i = 0; i < 96; i++) { CreateEffect(pass == 0); effects[i].SetActive(false); }
            until = new float[96]; active = new bool[96]; frame = 0; cpu.Clear(); submission.Clear(); gpu.Clear();
        }
        static void Tick()
        {
            try
            {
                float now = frame / 60f; var watch = Stopwatch.StartNew();
                for (int i = 0; i < 96; i++) if (active[i])
                {
                    var root = effects[i].GetComponent<ParticleSystem>(); root.Simulate(1f/60, true, false, false); root.Pause(true);
                    var effect = effects[i].GetComponent<InkImpactEffect>();
                    if (now >= until[i] || (effect != null && !effect.IsAlive)) { active[i] = false; root.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); effects[i].SetActive(false); }
                }
                // Four emitters, each shooting eight pellets every 0.2 simulated seconds.
                if (frame % 12 == 0) for (int n = 0; n < 32; n++)
                {
                    int index = Array.FindIndex(active, a => !a); if (index < 0) break;
                    active[index] = true; until[index] = now + (pass == 0 ? 1.3f : .8f);
                    Vector3 point = Point("wall") + new Vector3((n / 8 - 1.5f) * .48f + Mathf.Sin(n * 2.4f) * .2f, Mathf.Cos(n * 2.4f) * .25f, 0);
                    Play(effects[index], point, Vector3.forward, (uint)(frame * 32 + n + 1), (byte)(n / 8 % 2 + 1));
                }
                double update = watch.Elapsed.TotalMilliseconds; watch.Restart(); Render(); double render = watch.Elapsed.TotalMilliseconds;
                FrameTimingManager.CaptureFrameTimings();
                if (frame >= 60)
                {
                    cpu.Add(update); submission.Add(render);
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) gpu.Add(timings[0].gpuFrameTime);
                }
                frame++;
                if (frame < 240) return;
                report.Add($"{(pass == 0 ? "before" : "after")}: simulated 4 emitters x 8 pellets / .2s, pool=96, samples={cpu.Count}; CPU emit/simulate P50={Percentile(cpu,.5):F3}ms P95={Percentile(cpu,.95):F3}ms; render submission P50={Percentile(submission,.5):F3}ms P95={Percentile(submission,.95):F3}ms; " +
                    (gpu.Count > 0 ? $"Editor GPU frame P95={Percentile(gpu,.95):F3}ms ({gpu.Count} samples; whole Editor frame)" : "GPU frame timing unavailable in this Editor; submission time is not GPU time."));
                if (pass++ == 0) { BeginPerformance(); return; }
                EditorApplication.update -= Tick; ClearEffects(); target.Release(); Object.DestroyImmediate(target);
                File.WriteAllLines(Output + "/visual-performance.txt", report);
                UnityEngine.Debug.Log("[INK-IMPACT] GPU captures, occlusion and matched Editor measurement complete.");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e) { Fail(e); }
        }
        static double Percentile(List<double> values, double fraction) { values.Sort(); return values[(int)((values.Count - 1) * fraction)]; }
        static void ClearEffects() { foreach (var go in effects) Object.DestroyImmediate(go); effects.Clear(); }
        static void Fail(Exception e)
        {
            EditorApplication.update -= Tick; ClearEffects();
            File.WriteAllText(Output + "/failure.txt", e.ToString()); UnityEngine.Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
#endif
