#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Painting;

namespace Splatoon.Editor
{
    /// <summary>Real graphics-device regression; run in a disposable validation project.</summary>
    public static class InkShapeValidation
    {
        const string Output = "Reports/InkShapes32";
        static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException("[INK32] " + message); }
        public static void Run()
        {
            try
            {
                Directory.CreateDirectory(Output); Directory.CreateDirectory("Reports/InkLook/Screenshots");
                InkLookUpgrade.LoadConfig(); InkShapeAtlasEditor.Validate();
                Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "Graphics device required");
                InkLookValidation.ValidateShapeRegression();
                ValidateTiles(); ValidateSizes();
                File.WriteAllText(Output + "/gpu-result.txt", "PASS: all 32 tiles on floor/wall, rotation/reflection, CPU/GPU bytes, display binding, radius scaling, repeated mixed ink, snapshot continuation, map RT lifecycle, long-wall seams and backface isolation.\nGPU: " + SystemInfo.graphicsDeviceName);
                Debug.Log("[INK32] ALL GPU CHECKS PASS"); EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static PaintStamp Stamp(PaintSurface surface, int index, float radius, uint rotation = 0, bool mirror = false)
            => new() { Position = surface.transform.position, Normal = surface.transform.up, Radius = radius,
                Hardness = .55f, Strength = 1, Team = 1, ShapeSeed = InkShapeAtlas.Pack(index, (rotation << 6) | (mirror ? 32u : 0u)) };
        static Material Material() => new(AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Materials/PaintableWall.mat"))
            { shader = Shader.Find("Splatoon/InkSurface"), hideFlags = HideFlags.HideAndDontSave };
        public static void RunSizes()
        {
            try { InkLookUpgrade.LoadConfig(); InkShapeAtlasEditor.Validate(); ValidateSizes(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        static byte[] Bytes(RenderTexture rt)
        { var image = InkLookValidation.Read(rt); try { return image.GetRawTextureData<byte>().ToArray(); } finally { UnityEngine.Object.DestroyImmediate(image); } }
        static void Destroy(PaintSurface surface)
        {
            UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
            var mesh = surface.GetComponent<MeshFilter>().sharedMesh; surface.ReleaseGraphics();
            UnityEngine.Object.DestroyImmediate(surface.gameObject); UnityEngine.Object.DestroyImmediate(mesh);
        }
        static void ValidateTiles()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var material = Material(); int maxDelta = 0, samples = 0;
            foreach (bool wall in new[] { false, true })
            {
                var surface = InkLookValidation.Plane("All tiles", 4, 256, material);
                if (wall) surface.transform.rotation = Quaternion.Euler(-90, 0, 0);
                foreach (uint angle in new uint[] { 0, 8192, 23017 })
                for (int index = 0; index < 32; index++)
                {
                    surface.Clear(); var stamp = Stamp(surface, index, 1.6f, angle, (index & 1) != 0);
                    surface.Apply(stamp); surface.FlushDisplay(); var raw = Bytes(surface.Mask);
                    int nonzero = 0;
                    for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
                    {
                        var world = surface.transform.TransformPoint(new Vector3((x + .5f) / 64 - 2, 0, (y + .5f) / 64 - 2));
                        var expected = InkCoverage.Accumulate(default, 1, InkShapeAtlas.Coverage(world, stamp));
                        int delta = Math.Abs(raw[(y * 256 + x) * 4] - expected.r);
                        maxDelta = Math.Max(maxDelta, delta); samples++; if (raw[(y * 256 + x) * 4] > 0) nonzero++;
                        Check(delta <= 2, $"tile={index} wall={wall} angle={angle} pixel={x},{y} delta={delta}");
                    }
                    Check(nonzero > 100, "Empty tile " + index);
                    Check(Bytes(surface.DisplayMask).Where((_, i) => i % 4 == 3).Any(v => v > 0), "Empty display tile " + index);
                    var block = new MaterialPropertyBlock(); surface.GetComponent<Renderer>().GetPropertyBlock(block);
                    Check(block.GetTexture("_MaskTexture") == surface.DisplayMask, "Display material binding");
                    // Preserve appearance across a late join snapshot followed by mixed, repeated paint.
                    var next = Stamp(surface, (index + 17) % 32, .85f, 9321, true); next.Team = 2; next.Strength = .4f;
                    surface.Apply(next); surface.Apply(next); var uninterrupted = Bytes(surface.Mask);
                    surface.Clear(); surface.Restore(raw); surface.Apply(next); surface.Apply(next);
                    Check(uninterrupted.SequenceEqual(Bytes(surface.Mask)), "Snapshot continuation tile " + index);
                }
                Destroy(surface);
            }
            UnityEngine.Object.DestroyImmediate(material);
            File.WriteAllText(Output + "/tile-gpu.txt", $"samples={samples} maxByteDelta={maxDelta} floor/wall x 32 indices x 3 rotations; mirrors and snapshot continuation PASS");
            Debug.Log($"[INK32] tiles samples={samples} maxByteDelta={maxDelta} PASS");
        }
        struct Measurement { public int Width, Height, Pixels; }
        static Measurement Measure(PaintSurface surface, PaintStamp stamp)
        {
            surface.Clear(); surface.Apply(stamp); surface.FlushDisplay(); var raw = Bytes(surface.Mask);
            int minX = surface.Resolution, maxX = -1, minY = surface.Height, maxY = -1, count = 0;
            for (int y = 0; y < surface.Height; y++) for (int x = 0; x < surface.Resolution; x++)
                if (raw[(y * surface.Resolution + x) * 4 + 3] >= 128)
                { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); count++; }
            return new Measurement { Width = maxX - minX + 1, Height = maxY - minY + 1, Pixels = count };
        }
        static void ValidateSizes()
        {
            var csv = new StringBuilder("surface,hero,kind,radius_m,width_px,height_px,mask_pixels,mask_area_m2\n");
            var material = Material();
            foreach (bool wall in new[] { false, true })
            foreach (var hero in Splatoon.Config.LubanConfigService.Current.Tables.TbHero.DataList)
            foreach (bool trail in new[] { false, true })
            {
                var weapon = Splatoon.Config.GameplayConfig.GetWeapon(hero.Id);
                float min = trail ? weapon.TrailRadiusMin : weapon.PaintRadiusMin;
                float max = trail ? weapon.TrailRadiusMax : weapon.PaintRadiusMax;
                var surface = InkLookValidation.Plane("Radius measure", 4, 1024, material);
                if (wall) surface.transform.rotation = Quaternion.Euler(-90, 0, 0);
                Measurement first = default; float firstRadius = 0;
                foreach (float radius in new[] { min, (min + max) / 2, max })
                {
                    var m = Measure(surface, Stamp(surface, 16, radius, 7000, true));
                    Check(m.Pixels > 0, "Radius produced no pixels");
                    if (firstRadius == 0) { first = m; firstRadius = radius; }
                    else
                    {
                        float ratio = radius / firstRadius;
                        Check(Math.Abs((float)m.Width / first.Width - ratio) < .07f, "Width does not scale with radius");
                        Check(Math.Abs((float)m.Height / first.Height - ratio) < .07f, "Height does not scale with radius");
                        Check(Math.Abs((float)m.Pixels / first.Pixels / (ratio * ratio) - 1) < .07f, "Area does not scale with radius squared");
                    }
                    csv.AppendLine(FormattableString.Invariant($"{(wall ? "wall" : "floor")},{hero.Id},{(trail ? "trail" : "impact")},{radius:F6},{m.Width},{m.Height},{m.Pixels},{m.Pixels / 65536.0:F6}"));
                }
                Destroy(surface);
                // Equal framing for minimum/midpoint/maximum, on the same material.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Check(material != null && material.shader != null && material.shader.name == "Splatoon/InkSurface", "Comparison material was lost across scene changes");
                var camera = InkLookValidation.FixtureCamera(); surface = InkLookValidation.Plane("Radius comparison", 12, 1536, material);
                if (wall) surface.transform.rotation = Quaternion.Euler(-90, 0, 0);
                int i = 0;
                foreach (float radius in new[] { min, (min + max) / 2, max })
                {
                    var stamp = Stamp(surface, 16, radius, 7000, true);
                    stamp.Position = surface.transform.TransformPoint(new Vector3((i++ - 1) * 3.4f, 0, 0)); surface.Apply(stamp);
                }
                camera.orthographic = true; camera.orthographicSize = 3.3f;
                camera.transform.position = surface.transform.position + surface.transform.up * 10;
                camera.transform.rotation = Quaternion.LookRotation(-surface.transform.up, surface.transform.forward);
                InkLookValidation.Capture(camera, $"ink32-{(wall ? "wall" : "floor")}-hero{hero.Id}-{(trail ? "trail" : "impact")}-min-mid-max");
                Destroy(surface); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            UnityEngine.Object.DestroyImmediate(material); File.WriteAllText(Output + "/radius-measurements.csv", csv.ToString());
            Check(PaintSurface.AllocatedBytes == 0, "Validation leaked render textures");
            Debug.Log("[INK32] all weapon radius ranges scale on floor/wall PASS");
        }
    }
}
#endif
