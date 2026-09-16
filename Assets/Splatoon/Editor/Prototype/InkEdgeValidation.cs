#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Painting;
using Splatoon.Prototype;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    public static class InkEdgeValidation
    {
        const string Output = "Reports/InkEdges";
        static readonly int[] Covers = { 27, 28, 29, 30, 33, 34, 35, 36 };
        [Serializable] sealed class Measurement
        {
            public string stage, gpu, islandFormat;
            public uint ownershipHash;
            public long residentBytes, checkpointBytes, peakBytes, unityReportedTextureBytes;
            public int compressedSnapshotBytes, stamps;
            public double paintSubmitMilliseconds;
            public SurfaceHash[] surfaces;
        }
        [Serializable] sealed class SurfaceHash { public int id, width, height; public uint stateHash; }
        [Serializable] sealed class CaptureTiming { public string stage, surface; public double renderAndReadbackP50Ms, renderAndReadbackP95Ms; }
        [Serializable] sealed class Results { public Measurement[] maps; public CaptureTiming[] timings; public string[] testedIslandFormats; }
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("[INK-EDGES] " + message); }
        static byte[] Bytes(RenderTexture texture)
        {
            var image = InkLookValidation.Read(texture);
            try { return image.GetRawTextureData<byte>().ToArray(); }
            finally { Object.DestroyImmediate(image); }
        }
        public static void Run()
        {
            try
            {
                Directory.CreateDirectory(Output);
                InkLookUpgrade.LoadConfig(); InkShapeAtlasEditor.Validate();
                Check(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "Graphics device required");
                InkLookValidation.ValidateShapeRegression();
                InkShapeValidation.ValidateTiles(Output);
                var formats = ValidateFormats();
                var before = MeasureMap(true); var after = MeasureMap(false);
                Check(before.ownershipHash == after.ownershipHash, "Resolution changed gameplay ownership");
                foreach (var previous in before.surfaces.Where(s => !Covers.Contains(s.id)))
                    Check(after.surfaces.Single(s => s.id == previous.id).stateHash == previous.stateHash, "Unchanged surface state changed");
                var timings = CaptureStages();
                File.WriteAllText(Output + "/graphics-results.json", JsonUtility.ToJson(new Results { maps = new[] { before, after }, timings = timings.ToArray(), testedIslandFormats = formats }, true));
                Check(PaintSurface.AllocatedBytes == 0 && PaintSurface.CheckpointBytes == 0, "Render textures leaked");
                Debug.Log("[INK-EDGES] ALL GRAPHICS CHECKS PASS"); EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static string[] ValidateFormats()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var material = new Material(Shader.Find("Splatoon/InkSurface"));
            var surface = InkLookValidation.Plane("Format fixture", 4, 128, material);
            surface.Apply(new PaintStamp { Position = Vector3.zero, Normal = Vector3.up, Radius = 1.4f, Hardness = .55f, Strength = 1, Team = 1, ShapeSeed = 16 });
            surface.FlushDisplay(); var expected = Bytes(surface.DisplayMask);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var source = (RenderTexture)typeof(PaintSurface).GetField("_islands", flags).GetValue(surface);
            var display = (Material)typeof(PaintSurface).GetField("_extend", flags).GetValue(surface);
            var tested = new List<string>();
            foreach (var format in new[] { GraphicsFormat.R8_UNorm, GraphicsFormat.R16_UNorm, GraphicsFormat.R16_SFloat })
            {
                if (!PaintTextureMemory.SupportsIslandFormat(format)) continue;
                var occupancy = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { graphicsFormat = format, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var result = new RenderTexture(surface.DisplayMask.descriptor);
                try
                {
                    Check(occupancy.Create() && occupancy.graphicsFormat == format, "Island format fallback was substituted"); result.Create();
                    Graphics.Blit(source, occupancy); display.SetTexture("_UVIslands", occupancy); Graphics.Blit(surface.Mask, result, display);
                    var actual = Bytes(result);
                    int maxDelta = actual.Select((b, i) => Math.Abs(b - expected[i])).Max();
                    Debug.Log($"[INK-EDGES] Format={format} source={surface.DisplayMask.graphicsFormat} target={result.graphicsFormat} srgbWrite={GL.sRGBWrite} maxDisplayByteDelta={maxDelta} changedBytes={actual.Where((b, i) => b != expected[i]).Count()} first=" + string.Join(";", Enumerable.Range(0, actual.Length / 4).Where(i => actual[i*4] != expected[i*4]).Take(3).Select(i => $"expected={expected[i*4]},{expected[i*4+1]},{expected[i*4+2]},{expected[i*4+3]} actual={actual[i*4]},{actual[i*4+1]},{actual[i*4+2]},{actual[i*4+3]}")));
                    Check(maxDelta == 0, "Island format changed display pixels: " + format + " maxDelta=" + maxDelta);
                    tested.Add(format.ToString());
                }
                finally { occupancy.Release(); result.Release(); Object.DestroyImmediate(occupancy); Object.DestroyImmediate(result); }
            }
            AsyncGPUReadback.WaitAllRequests(); surface.ReleaseGraphics(); Object.DestroyImmediate(material);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return tested.ToArray();
        }
        public static void RunFormats()
        {
            try { InkLookUpgrade.LoadConfig(); ValidateFormats(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static Measurement MeasureMap(bool baselineResolution)
        {
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);
            var arena = Object.FindFirstObjectByType<PrototypeArena>(); arena.RegisterSurfaces();
            if (baselineResolution) foreach (int id in Covers) { arena.Surfaces[id].Resolution /= 2; arena.Surfaces[id].ResolutionHeight /= 2; }
            arena.BakedTopology = arena.ComputeTopology(); arena.InitializeRuntime();
            var stamps = InkEdgeFixture.Stamps(arena).ToArray(); var watch = Stopwatch.StartNew();
            foreach (var stamp in stamps) arena.Apply(stamp, true);
            foreach (var surface in arena.Surfaces.Values) surface.FlushDisplay();
            watch.Stop(); AsyncGPUReadback.WaitAllRequests();
            var checkpoint = new PaintCheckpoint { Topology = arena.BakedTopology, Round = 1, Sequence = (uint)stamps.Length, Ownership = arena.CaptureOwnership() };
            var copies = new List<RenderTexture>(); var hashes = new List<SurfaceHash>();
            try
            {
                foreach (var surface in arena.Surfaces.Values)
                {
                    Check(surface.Mask != null && surface.HasPaint, "Map fixture missed surface " + surface.SurfaceId);
                    var copy = RenderTexture.GetTemporary(surface.Mask.descriptor); Graphics.CopyTexture(surface.Mask, copy); copies.Add(copy);
                    var bytes = Bytes(copy); checkpoint.Surfaces.Add(surface.SurfaceId, bytes);
                    hashes.Add(new SurfaceHash { id = surface.SurfaceId, width = surface.Resolution, height = surface.Height, stateHash = PaintSnapshotCodec.Hash(bytes) });
                }
                long copyBytes = copies.Sum(PaintTextureMemory.Bytes);
                Check(PaintSurface.AllocatedBytes + copyBytes <= 128L * 1048576, "Actual allocated checkpoint footprint exceeds budget");
                var encoded = PaintSnapshotCodec.Encode(checkpoint);
                var decoded = PaintSnapshotCodec.Decode(encoded, arena.BakedTopology, arena.OwnershipSizes(), arena.Surfaces.ToDictionary(p => p.Key, p => p.Value.TextureBytes));
                foreach (var pair in checkpoint.Surfaces) Check(decoded.Surfaces[pair.Key].SequenceEqual(pair.Value), "Checkpoint codec changed surface state");
                return new Measurement { stage = baselineResolution ? "32-density-single-channel" : "final", gpu = SystemInfo.graphicsDeviceName,
                    islandFormat = arena.Surfaces.Values.First().IslandFormat.ToString(), ownershipHash = arena.OwnershipHash(),
                    residentBytes = PaintSurface.AllocatedBytes, checkpointBytes = copyBytes, peakBytes = PaintSurface.AllocatedBytes + copyBytes,
                    unityReportedTextureBytes = Resources.FindObjectsOfTypeAll<RenderTexture>().Where(t => t.IsCreated() && (t.name.StartsWith("Ink ") || copies.Contains(t))).Sum(t => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t)),
                    compressedSnapshotBytes = encoded.Length, stamps = stamps.Length, paintSubmitMilliseconds = watch.Elapsed.TotalMilliseconds, surfaces = hashes.ToArray() };
            }
            finally
            {
                foreach (var copy in copies) RenderTexture.ReleaseTemporary(copy);
                foreach (var surface in arena.Surfaces.Values) surface.ReleaseGraphics();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        static List<CaptureTiming> CaptureStages()
        {
            var timings = new List<CaptureTiming>();
            var stages = new[] { "00-original", "01-edge-aa", "02-normal", "03-cover-density" };
            for (int stage = 0; stage < stages.Length; stage++)
            foreach (var kind in new[] { "floor", "cover-wall", "cover-top", "long-wall-seam" })
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camera = InkLookValidation.FixtureCamera(); camera.allowMSAA = false;
                camera.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.None;
                string shader = stage == 0 ? "Hidden/Splatoon/InkEdgeBaseline" : stage == 1 ? "Hidden/Splatoon/InkEdgeAAOnly" : "Splatoon/InkSurface";
                var material = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/TrainingGround/Materials/CoverConcrete.mat")) { shader = Shader.Find(shader) };
                var surface = InkLookValidation.Plane("Edge capture", 4, stage == 3 && kind.StartsWith("cover") ? 256 : 128, material);
                Vector3 focus = Vector3.zero;
                if (kind == "long-wall-seam")
                {
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube); var mesh = Object.Instantiate(box.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(box);
                    mesh.vertices = mesh.vertices.Select(p => Vector3.Scale(p, new Vector3(32, 3, .5f))).ToArray();
                    InkSurfaceAtlas.Rebuild(mesh, out int width, out int height);
                    Object.DestroyImmediate(surface.GetComponent<MeshFilter>().sharedMesh); surface.GetComponent<MeshFilter>().sharedMesh = mesh;
                    surface.Resolution = width; surface.ResolutionHeight = height;
                    focus = new Vector3(-16 + 32 / 3f, 0, .25f);
                    surface.Apply(new PaintStamp { Position = focus, Normal = Vector3.forward, Radius = 1.4f, Hardness = .55f, Strength = 1, Team = 1, ShapeSeed = InkShapeAtlas.Pack(16, 8192u << 6) });
                }
                else
                {
                    if (kind == "cover-wall") surface.transform.rotation = Quaternion.Euler(-90, 0, 0);
                    for (int i = 0; i < 4; i++) surface.Apply(new PaintStamp { Position = surface.transform.TransformPoint(new Vector3((i % 2 - .5f) * 1.25f, 0, (i / 2 - .5f) * .95f)),
                        Normal = surface.transform.up, Radius = i == 3 ? .28f : .9f, Hardness = .55f, Strength = 1, Team = (byte)(i == 2 ? 2 : 1), ShapeSeed = InkShapeAtlas.Pack(16 + i, (uint)(i * 7211) << 6) });
                }
                surface.FlushDisplay();
                string directory = Output + "/Screenshots/" + stages[stage]; Directory.CreateDirectory(directory);
                foreach (string view in new[] { "near", "far", "oblique" })
                {
                    var offset = view == "near" ? new Vector3(0, 3, -3) : view == "far" ? new Vector3(0, 9, -12) : new Vector3(3, 1.2f, -3);
                    if (kind == "long-wall-seam") offset = new Vector3(offset.x, -offset.z * .3f, offset.y);
                    camera.transform.position = focus + surface.transform.TransformVector(offset); camera.transform.LookAt(focus, kind == "cover-wall" ? surface.transform.forward : Vector3.up);
                    Capture(camera, directory + "/" + kind + "-" + view + "-1080.png", 1920, 1080);
                    Capture(camera, directory + "/" + kind + "-" + view + "-4k.png", 3840, 2160);
                }
                var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32); target.Create();
                var times = new List<double>();
                try
                {
                    for (int frame = 0; frame < 36; frame++)
                    {
                        float angle = (frame - 18) * .7f * Mathf.Deg2Rad;
                        var offset = kind == "long-wall-seam" ? new Vector3(4 * Mathf.Sin(angle), 1.5f, 4 * Mathf.Cos(angle)) : surface.transform.TransformVector(new Vector3(4 * Mathf.Sin(angle), 2.5f, -4 * Mathf.Cos(angle)));
                        camera.transform.position = focus + offset; camera.transform.LookAt(focus, kind == "cover-wall" ? surface.transform.forward : Vector3.up);
                        var watch = Stopwatch.StartNew(); Render(camera, target);
                        var readback = AsyncGPUReadback.Request(target); readback.WaitForCompletion(); watch.Stop(); Check(!readback.hasError, "Frame readback failed");
                        if (frame >= 6) times.Add(watch.Elapsed.TotalMilliseconds);
                        if (frame % 6 == 0) Capture(camera, directory + "/" + kind + "-orbit-" + frame.ToString("D2") + ".png", 1920, 1080);
                    }
                }
                finally { target.Release(); Object.DestroyImmediate(target); }
                times.Sort(); timings.Add(new CaptureTiming { stage = stages[stage], surface = kind, renderAndReadbackP50Ms = times[times.Count / 2], renderAndReadbackP95Ms = times[(int)(times.Count * .95)] });
                AsyncGPUReadback.WaitAllRequests(); surface.ReleaseGraphics(); Object.DestroyImmediate(surface.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(material);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Debug.Log("[INK-EDGES] Captured " + stages[stage] + " " + kind);
            }
            return timings;
        }
        static void Render(Camera camera, RenderTexture target) => RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
        static void Capture(Camera camera, string path, int width, int height)
        {
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32); target.Create();
            try
            {
                Render(camera, target); Render(camera, target); var image = InkLookValidation.Read(target);
                try
                {
                    Check(!ShaderUtil.ShaderHasError(Shader.Find("Splatoon/InkSurface")), "Shader compilation error");
                    Check(image.GetPixels32().Count(p => p.r > 250 && p.b > 250 && p.g < 5) < width * height / 20, "Shader error magenta in capture");
                    File.WriteAllBytes(path, image.EncodeToPNG());
                }
                finally { Object.DestroyImmediate(image); }
            }
            finally { target.Release(); Object.DestroyImmediate(target); }
        }
    }
}
#endif
