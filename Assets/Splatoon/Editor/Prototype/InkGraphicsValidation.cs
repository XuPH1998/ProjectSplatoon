#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Painting;

namespace Splatoon.Editor
{
    public static class InkGraphicsValidation
    {
        // Explicit graphical batch validation, without modifying or saving scene assets.
        public static void Run()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Graphics validation requires a GPU");
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + name + ".json")));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
            GameplayConfig.Validate();
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);
            Directory.CreateDirectory("Reports/InkGraphics");
            foreach (var surface in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None))
            {
                var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
                var triangles = mesh.triangles; var vertices = mesh.vertices;
                Vector3 a = vertices[triangles[0]], b = vertices[triangles[1]], c = vertices[triangles[2]];
                Vector3 normal = surface.transform.TransformDirection(Vector3.Cross(b - a, c - a).normalized);
                Vector3 hit = surface.transform.TransformPoint((a + b + c) / 3);
                surface.Apply(new PaintStamp { Position = hit, Normal = normal, Radius = 1.5f, Hardness = .01f, Strength = 1, Team = 1 });
                var painted = Read(surface.Mask);
                int pixels = painted.GetPixels32().Count(c => c.a > 128 && c.r > 128);
                File.WriteAllBytes("Reports/InkGraphics/mask-" + surface.SurfaceId + ".png", painted.EncodeToPNG());
                if (pixels < 10) throw new InvalidOperationException("Empty painted mask: " + surface.name + " pixels=" + pixels);
                var bytes = painted.GetRawTextureData<byte>().ToArray(); var visual = InkStaticUpgrade.ReadVisual(surface);
                surface.Clear();
                var clear = Read(surface.Mask);
                if (clear.GetPixels32().Any(c => c.a != 0)) throw new InvalidOperationException("Clear failed: " + surface.name);
                surface.Restore(bytes, visual);
                var restored = Read(surface.Mask);
                if (!bytes.SequenceEqual(restored.GetRawTextureData<byte>().ToArray())) throw new InvalidOperationException("Restore differs: " + surface.name);
                Debug.Log($"[INK-GPU] surface={surface.SurfaceId} name={surface.name} paintedPixels={pixels} face/clear/restore=PASS");
                UnityEngine.Object.DestroyImmediate(painted); UnityEngine.Object.DestroyImmediate(clear); UnityEngine.Object.DestroyImmediate(restored);
                surface.SendMessage("OnDisable");
            }
            Debug.Log("[INK-GPU] All surfaces passed. Remaining RT bytes=" + PaintSurface.AllocatedBytes);
            EditorApplication.Exit(0);
        }
        static Texture2D Read(RenderTexture target)
        {
            var previous = RenderTexture.active; RenderTexture.active = target;
            var result = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            result.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); result.Apply(); RenderTexture.active = previous; return result;
        }
    }
}
#endif
