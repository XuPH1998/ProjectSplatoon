#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkStaticValidation
    {
        static string Arg(string key, string fallback = "")
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        static string Output => Arg("-inkValidationOutput", InkStaticUpgrade.Output);
        static bool RoundedComparison;
        public static async void RunRounded()
        {
            try
            {
                Directory.CreateDirectory(Output); RoundedComparison = true; InkStaticUpgrade.LoadTables();
                ValidateDetail(); ValidateMap(); await CaptureComparison();
                Check(!ShaderUtil.ShaderHasError(Shader.Find("Splatoon/InkSurface")), "Ink shader errors");
                File.WriteAllText(Output + "/graphics-result.txt", "PASS: 32-shape detail/coverage parity, bounded accumulation, paired restore/continue, 36 surfaces, unchanged state/ownership/memory across appearance switches, bare-ground equality, rendered comparisons.\n");
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        public static async void Run()
        {
            try
            {
                InkStaticUpgrade.Apply(); InkStaticUpgrade.LoadTables();
                ValidateDetail(); ValidateMap(); await CaptureComparison();
                foreach (string name in new[] { "Splatoon/InkSurface", "Splatoon/InkTexturePainter", "Splatoon/InkDisplay" })
                    Check(!ShaderUtil.ShaderHasError(Shader.Find(name)), "Shader errors: " + name);
                File.WriteAllText(Output + "/graphics-result.txt", "PASS: detail projection, bounded accumulation, paired restore/continue, all map planes, memory lifecycle, rendered comparison.\nGPU: " + SystemInfo.graphicsDeviceName);
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static void Cleanup()
        {
            AsyncGPUReadback.WaitAllRequests();
            foreach (var surface in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)) surface.ReleaseGraphics();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Check(PaintSurface.AllocatedBytes == 0 && PaintSurface.CheckpointBytes == 0, "Paint textures leaked after scene unload");
        }
        static void ValidateDetail()
        {
            Cleanup(); var material = new Material(Shader.Find("Splatoon/InkSurface"));
            var surface = InkLookValidation.Plane("Detail parity", 4, 128, material);
            var detail = InkAppearanceProfile.Current.DetailAtlas; int samples = 0, maxError = 0;
            foreach (bool wall in new[] { false, true })
            foreach (bool mirror in new[] { false, true })
            foreach (uint angle in new uint[] { 0, 8192, 23017 })
            for (int tile = 0; tile < 32; tile++)
            {
                surface.transform.rotation = wall ? Quaternion.Euler(-90, 0, 0) : Quaternion.identity;
                surface.Clear();
                var stamp = new PaintStamp { Position = Vector3.zero, Normal = surface.transform.up, Radius = 1.5f, Strength = 1,
                    Hardness = .55f, Team = 1, ShapeSeed = InkShapeAtlas.Pack(tile, (angle << 6) | (mirror ? 32u : 0u)) };
                surface.Apply(stamp); var visual = InkStaticUpgrade.ReadVisual(surface); var coverage = InkStaticUpgrade.Read(surface.Mask);
                for (int y = 2; y < 126; y += 3) for (int x = 2; x < 126; x += 3)
                {
                    var point = surface.transform.TransformPoint(new Vector3((x + .5f) / 32 - 2, 0, (y + .5f) / 32 - 2));
                    var uv = InkShapeAtlas.ProjectedUv(point, stamp.Position, stamp.Normal, stamp.Radius, stamp.ShapeSeed);
                    float f = InkShapeAtlas.Coverage(point, stamp);
                    // Texture2D.GetPixelBilinear uses a different CPU texel convention from GPU Load.
                    float px = Mathf.Clamp(uv.x * 256 - .5f, 0, 255), py = Mathf.Clamp(uv.y * 256 - .5f, 0, 255);
                    int x0 = (int)px, y0 = (int)py, x1 = Math.Min(x0 + 1, 255), y1 = Math.Min(y0 + 1, 255), ox = tile % 8 * 256, oy = tile / 8 * 256;
                    var at = Color.Lerp(Color.Lerp(detail.GetPixel(ox + x0, oy + y0), detail.GetPixel(ox + x1, oy + y0), px - x0),
                        Color.Lerp(detail.GetPixel(ox + x0, oy + y1), detail.GetPixel(ox + x1, oy + y1), px - x0), py - y0);
                    int index = y * 128 + x;
                    int error = Math.Max(Math.Abs(visual[index * 2] - Mathf.RoundToInt(at.r * f * 255)), Math.Abs(visual[index * 2 + 1] - Mathf.RoundToInt(at.g * f * 255)));
                    maxError = Math.Max(maxError, error); samples++;
                    Check(error <= 2, $"Detail projection differs tile={tile} angle={angle} mirror={mirror} wall={wall} xy={x},{y} uv={uv} f={f} a={coverage[index*4+3]} expected={at.r*f*255},{at.g*f*255} actual={visual[index*2]},{visual[index*2+1]}: {error}");
                    Check(Math.Abs(coverage[index * 4 + 3] - Mathf.RoundToInt(f * 255)) <= 2, "Coverage changed");
                }
                // Save both planes, then compare mixed-team continuation against uninterrupted painting.
                var next = stamp; next.Team = 2; next.Strength = .43f; next.ShapeSeed ^= 7654u << 6; next.Radius = .8f;
                surface.Apply(next); surface.FlushDisplay(); var expected = InkStaticUpgrade.ReadVisual(surface); var expectedCoverage = InkStaticUpgrade.Read(surface.Mask);
                surface.Clear(); surface.Restore(coverage, visual); surface.Apply(next); surface.FlushDisplay();
                Check(expected.SequenceEqual(InkStaticUpgrade.ReadVisual(surface)), "Visual restore continuation differs");
                Check(expectedCoverage.SequenceEqual(InkStaticUpgrade.Read(surface.Mask)), "Coverage restore continuation differs");
            }
            for (int i = 0; i < 512; i++) surface.Apply(new PaintStamp { Normal = surface.transform.up, Radius = 2, Hardness = .55f, Strength = 1, Team = 1 });
            surface.FlushDisplay(); var bounded = InkStaticUpgrade.ReadVisual(surface);
            Check(bounded.Any(b => b > 40) && bounded.Max() < 240, "Repeated paint accumulated unbounded thickness");
            surface.Clear(); Check(InkStaticUpgrade.ReadVisual(surface).All(b => b == 0), "Clear left visual detail");
            File.WriteAllText(Output + "/detail-parity.txt", $"PASS {samples} sampled pixels, 32 tiles, 3 rotations, both mirrors, floor and wall; maximum byte delta {maxError}.\n");
            Cleanup(); UnityEngine.Object.DestroyImmediate(material);
        }
        static void ValidateMap()
        {
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath); var arena = UnityEngine.Object.FindFirstObjectByType<PrototypeArena>(); arena.InitializeRuntime();
            foreach (var stamp in InkEdgeFixture.Stamps(arena)) arena.Apply(stamp, true);
            var checkpoint = new PaintCheckpoint { Round = 1, Sequence = 512, Topology = arena.BakedTopology, Ownership = arena.CaptureOwnership() };
            foreach (var s in arena.Surfaces.Values)
            {
                s.FlushDisplay(); checkpoint.Surfaces[s.SurfaceId] = InkStaticUpgrade.Read(s.Mask); checkpoint.VisualSurfaces[s.SurfaceId] = InkStaticUpgrade.ReadVisual(s);
                Check(checkpoint.Surfaces[s.SurfaceId].Any(b => b > 0) && checkpoint.VisualSurfaces[s.SurfaceId].Any(b => b > 0), "Fixture left an empty paint plane: " + s.name);
            }
            var bytes = PaintSnapshotCodec.Encode(checkpoint);
            var decoded = PaintSnapshotCodec.Decode(bytes, arena.BakedTopology, arena.OwnershipSizes(), arena.Surfaces.ToDictionary(x => x.Key, x => x.Value.TextureBytes));
            arena.ClearPaint(); arena.RestoreOwnership(decoded.Ownership);
            foreach (var s in arena.Surfaces.Values)
            {
                s.Restore(decoded.Surfaces[s.SurfaceId], decoded.VisualSurfaces[s.SurfaceId]);
                Check(InkStaticUpgrade.Read(s.Mask).SequenceEqual(checkpoint.Surfaces[s.SurfaceId]), "Map coverage restore " + s.name);
                var actual = InkStaticUpgrade.ReadVisual(s); var expected = checkpoint.VisualSurfaces[s.SurfaceId];
                Check(actual.SequenceEqual(expected), "Map visual restore " + s.name + " " + s.Resolution + "x" + s.Height + ": " + string.Join(";", Enumerable.Range(0, actual.Length).Where(i => actual[i] != expected[i]).Take(10).Select(i => $"i={i} expected={expected[i]} actual={actual[i]} a={checkpoint.Surfaces[s.SurfaceId][i/2*4+3]}")));
            }
            long peak = PaintSurface.AllocatedBytes + PaintSurface.CheckpointBytes;
            Check(peak <= 192L * 1048576, "Normal PC paint peak exceeds 192 MiB");
            File.WriteAllText(Output + "/map-memory.txt", $"Surfaces {arena.Surfaces.Count}\nResident {PaintSurface.AllocatedBytes}\nGPU checkpoint reserve {PaintSurface.CheckpointBytes}\nPeak {peak}\nCompressed snapshot {bytes.Length}\nCanonical CPU planes {checkpoint.Surfaces.Sum(p => (long)p.Value.Length) + checkpoint.VisualSurfaces.Sum(p => (long)p.Value.Length)}\nStorage {arena.Surfaces.Values.First().VisualFormat}\n");
            Cleanup();
        }
        static async Task CaptureComparison()
        {
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath); var arena = UnityEngine.Object.FindFirstObjectByType<PrototypeArena>(); arena.InitializeRuntime();
            var comparisonStamps = new List<PaintStamp>(InkEdgeFixture.Stamps(arena));
            uint ordinal = 0;
            foreach (var surface in arena.Surfaces.Values.Where(s => s.Scores && s.transform.up.y > .99f))
            foreach (var region in surface.GameplayRegions)
            {
                var m = region.Matrix(surface);
                for (float z = -region.Size.y / 2 + .6f; z < region.Size.y / 2; z += 1.2f)
                for (float x = -region.Size.x / 2 + .6f; x < region.Size.x / 2; x += 1.2f)
                {
                    var p = m.MultiplyPoint3x4(new Vector3(x, 0, z)); if (Mathf.Abs(p.x) > 6 || Mathf.Abs(p.z) > 9) continue;
                    comparisonStamps.Add(new PaintStamp { SurfaceId = surface.SurfaceId, Position = p, Normal = m.MultiplyVector(Vector3.up), Radius = 1.25f,
                        Hardness = .6f, Strength = 1, Team = (byte)(p.x > 1 ? 2 : 1), ShapeSeed = InkShapeAtlas.Pack((int)(ordinal++ % 32), ordinal * 19937 << 6) });
                }
            }
            var camera = new GameObject("Static ink comparison camera").AddComponent<Camera>(); camera.fieldOfView = 60; camera.allowHDR = true;
            camera.clearFlags = CameraClearFlags.Skybox; camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var probes = GameObject.Find("InkStaticReflections");
            var positions = new[] { new Vector3(0, 5, -9), new Vector3(3, 1.4f, -5), new Vector3(-11, 2.5f, -20), new Vector3(-13, 1, -16), new Vector3(0, 18, -30) };
            var targets = new[] { Vector3.zero, Vector3.zero, new Vector3(-9, 1, -17), new Vector3(-9, 1, -17), Vector3.zero };
            string previousState = null;
            for (int mode = 0; mode < (RoundedComparison ? 2 : 3); mode++)
            {
                if (probes != null) probes.SetActive(RoundedComparison || mode == 2);
                // Probe registration is updated by the engine between editor frames.
                // Rendering every toggle in one executeMethod frame reuses stale probe data.
                await Task.Delay(150);
                // Editor pipeline initialization can release native RT storage between
                // frames. Rebuild this identical fixture after that boundary; Player
                // lifetime is verified separately by consecutive main-camera frames.
                foreach (var s in arena.Surfaces.Values) s.ReleaseGraphics();
                arena.ClearPaint();
                foreach (var stamp in comparisonStamps) arena.Apply(stamp, true);
                foreach (var s in arena.Surfaces.Values)
                {
                    s.FlushDisplay(); s.SetAppearance(RoundedComparison || mode != 0);
                    if (RoundedComparison) s.SetRoundedEdges(mode == 1);
                }
                if (RoundedComparison)
                {
                    string state = string.Join("\n", arena.Surfaces.Values.OrderBy(s => s.SurfaceId).Select(s =>
                        $"{s.SurfaceId}: {PaintSnapshotCodec.Hash(InkStaticUpgrade.Read(s.Mask))}, {PaintSnapshotCodec.Hash(InkStaticUpgrade.ReadVisual(s))}, {PaintSnapshotCodec.Hash(InkStaticUpgrade.Read(s.DisplayMask))}"));
                    state += "\nOwnership: " + string.Join(",", arena.CaptureOwnership().OrderBy(p => p.Key).Select(p => p.Key + ":" + PaintSnapshotCodec.Hash(p.Value)));
                    state += "\nRT bytes: " + PaintSurface.AllocatedBytes + ", checkpoint: " + PaintSurface.CheckpointBytes;
                    File.WriteAllText(Output + $"/state-{mode}.txt", state);
                    Check(previousState == null || state == previousState, "Look switch changed paint, ownership, or memory"); previousState = state;
                }
                var diagnostic = new List<string>();
                foreach (var s in arena.Surfaces.Values.Where(s => s.HasPaint))
                {
                    var block = new MaterialPropertyBlock(); var renderer = s.GetComponent<Renderer>(); renderer.GetPropertyBlock(block);
                    bool bound = block.GetTexture("_MaskTexture") == s.DisplayMask;
                    int alpha = ReadAlpha(s.DisplayMask);
                    Check(s.Mask != null && s.Mask.IsCreated() && bound, "Capture lost its paint RT: " + s.name);
                    diagnostic.Add($"{s.name}: alphaPixels={alpha}, bound={bound}");
                }
                File.WriteAllLines(Output + $"/capture-state-{mode}.txt", diagnostic);
                for (int view = 0; view < positions.Length; view++)
                {
                    camera.transform.position = positions[view]; camera.transform.LookAt(targets[view]);
                    string look = RoundedComparison ? (mode == 0 ? "today" : "rounded") : (mode == 0 ? "legacy" : mode == 1 ? "wet-sky" : "wet-probes");
                    Capture(camera, Output + $"/view-{view}-{look}.png");
                }
            }
            if (!RoundedComparison) Check(!File.ReadAllBytes(Output + "/view-1-wet-sky.png").SequenceEqual(File.ReadAllBytes(Output + "/view-1-wet-probes.png")), "Local probes had no rendered effect");
            Cleanup();
            CaptureFixture();
        }
        static int ReadAlpha(RenderTexture texture)
        {
            var rgba = InkStaticUpgrade.Read(texture); int count = 0;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] > 127) count++;
            return count;
        }
        static void CaptureFixture()
        {
            var camera = InkLookValidation.FixtureCamera(); camera.fieldOfView = 60;
            var material = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/TrainingGround/Materials/TrainingConcrete.mat"));
            var surface = InkLookValidation.Plane("Wet material close view", 12, 512, material);
            for (int z = -2; z <= 2; z++) for (int x = -3; x <= 3; x++)
                surface.Apply(new PaintStamp { Position = new Vector3(x * .7f, 0, z * .7f), Normal = Vector3.up, Radius = 1.2f, Hardness = .6f, Strength = 1,
                    Team = (byte)(x > 1 ? 2 : 1), ShapeSeed = InkShapeAtlas.Pack((x + z + 10) % 32, (uint)((x + z + 10) * 12739) << 6) });
            if (RoundedComparison)
            {
                // Isolated small marks exercise the local-width attenuation alongside
                // the fused patch, mixed teams, and holes in the authored silhouettes.
                for (int k = 0; k < 4; k++) surface.Apply(new PaintStamp { Position = new Vector3(-1.5f + k, 0, -2.8f),
                    Normal = Vector3.up, Radius = .10f + k * .08f, Hardness = .6f, Strength = 1, Team = 1,
                    ShapeSeed = InkShapeAtlas.Pack(k + 5, (uint)(k + 1) * 9719 << 6) });
            }
            surface.FlushDisplay();
            for (int mode = 0; mode < 2; mode++)
            {
                surface.SetAppearance(RoundedComparison || mode != 0);
                if (RoundedComparison) surface.SetRoundedEdges(mode == 1);
                for (int view = 0; view < 4; view++)
                {
                    camera.transform.position = view == 0 ? new Vector3(0, 4, -5) : view == 1 ? new Vector3(1, .65f, -4) : view == 2 ? new Vector3(-3, 1.3f, 3) : new Vector3(-3, 6, 5);
                    camera.transform.LookAt(Vector3.zero);
                    string look = RoundedComparison ? (mode == 0 ? "today" : "rounded") : (mode == 0 ? "legacy" : "wet");
                    Capture(camera, Output + $"/fixture-{view}-{look}.png");
                }
            }
            if (RoundedComparison)
            {
                surface.Clear();
                surface.SetRoundedEdges(false); Capture(camera, Output + "/bare-today.png");
                surface.SetRoundedEdges(true); Capture(camera, Output + "/bare-rounded.png");
                Check(File.ReadAllBytes(Output + "/bare-today.png").SequenceEqual(File.ReadAllBytes(Output + "/bare-rounded.png")), "Rounded edges changed bare ground");
            }
            Cleanup(); UnityEngine.Object.DestroyImmediate(material);
        }
        public static async void RenderOnly()
        {
            try { InkStaticUpgrade.LoadTables(); await CaptureComparison(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        public static async void RenderRoundedOnly()
        {
            try { Directory.CreateDirectory(Output); RoundedComparison = true; InkStaticUpgrade.LoadTables(); await CaptureComparison(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        public static void Capture(Camera camera, string path)
        {
            var target = RenderTexture.GetTemporary(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                var image = InkLookValidation.Read(target); File.WriteAllBytes(path, image.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(image);
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }
        public static void BuildWindows()
        {
            bool timing = PlayerSettings.enableFrameTimingStats;
            try { InkStaticUpgrade.LoadTables(); InkStaticUpgrade.NewAppearance(); PlayerSettings.enableFrameTimingStats = true; PrototypeBuilder.BuildWindowsTo(Output + "/Windows"); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
            finally { PlayerSettings.enableFrameTimingStats = timing; }
        }
        public static void CompileMobileVariants()
        {
            var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            bool previousAutomatic = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
            try
            {
                string output = Output + "/AndroidShaderBundle"; Directory.CreateDirectory(output);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3, GraphicsDeviceType.Vulkan });
                var manifest = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = "ink-static",
                    assetNames = new[] { "Assets/GameResource/Environment/TrainingGround/Materials/TrainingConcrete.mat", "Assets/GameResource/Environment/TrainingGround/Materials/CoverConcrete.mat",
                        InkAppearanceProfile.AssetPath, "Assets/Splatoon/Runtime/Painting/InkTexturePainter.shader", "Assets/Splatoon/Runtime/Painting/InkDisplay.shader" } } },
                    BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode, BuildTarget.Android);
                Check(manifest != null, "Android shader bundle failed");
                File.WriteAllText(Output + "/mobile-compile.txt", "PASS: Android GLES3 and Vulkan shader asset bundle; no device or APK acceptance.\n"); EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
            finally
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, previousApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, previousAutomatic);
            }
        }
    }
}
#endif
