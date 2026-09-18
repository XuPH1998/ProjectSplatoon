#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Cysharp.Threading.Tasks;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class FoamAppearancePlayTests
    {
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static IEnumerator Wait(Func<bool> ready)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, "Boot or room startup");
        }
        [UnityTest] public IEnumerator FixedInputsCaptureAndMeasure()
        {
            string label = File.Exists("Temp/FoamAppearance/label") ? File.ReadAllText("Temp/FoamAppearance/label").Trim() : "current";
            string output = "Reports/FoamAppearance/" + label;
            Directory.CreateDirectory(output);
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current != null);
            app.CaptureMouse(false);
            var match = PrototypeMatch.Current; var host = PrototypePlayer.Local;
            match.enabled = false; host.enabled = false;
            var world = match.Arena.Foam; var camera = Camera.main;
            Assert.That(Physics.Raycast(new Vector3(2, 2, -12), Vector3.down, out var hit, 3, Splatoon.Combat.PlayerMotorSimulation.WorldMask), Is.True);
            var floor = hit.collider.GetComponentInParent<PaintSurface>();
            var summary = new System.Text.StringBuilder("scenario,peakM,occupiedNodeCount,commitP95Ms,commitP99Ms,quietVersions\n");
            var timings = new System.Text.StringBuilder("scenario,index,milliseconds,dirtyChunks\n");
            string[] cases = { "single", "merge", "scatter", "enemy", "ramp", "platform" };
            foreach (string scenario in cases)
            {
                world.Clear(); var surface = floor; Vector3 center = hit.point;
                if (scenario == "ramp" || scenario == "platform")
                {
                    var patch = world.PatchValues.First(p => p.Surface.name.StartsWith(scenario == "ramp" ? "Ramp_" : "Platform_") && p.Surface.Scores);
                    surface = patch.Surface; center = patch.Origin;
                }
                camera.transform.position = center + new Vector3(6, 5, -7); camera.transform.LookAt(center + Vector3.up * .5f);
                var times = new List<double>(120);
                for (int i = 0; i < 120; i++)
                {
                    Vector3 offset = scenario == "merge" ? new Vector3(i % 2 == 0 ? -.65f : .65f, 0, 0)
                        : scenario == "scatter" ? new Vector3((i % 3 - 1) * 1.1f, 0, ((i / 3) % 3 - 1) * 1.1f) : Vector3.zero;
                    Vector3 position = center + offset;
                    world.TryPatch(surface, position, Vector3.up, out var patch);
                    if (patch != null) patch.Sample(position, out position, out _, out _);
                    byte team = (byte)(scenario == "enemy" && i >= 60 ? 2 : 1);
                    var stamp = new PaintStamp { SurfaceId = surface.SurfaceId, Position = position, Normal = patch?.Normal ?? Vector3.up,
                        Team = team, Radius = 2, DepthScale = 1, Hardness = 1, Strength = 1, ShapeSeed = InkShapeAtlas.Pack(0, (uint)(123 + i)) };
                    world.Queue(surface, stamp, .075f); world.Commit(false);
                    times.Add(world.LastCommitMilliseconds);
                    timings.AppendLine(FormattableString.Invariant($"{scenario},{i},{world.LastCommitMilliseconds:F6},{world.LastDirtyChunks}"));
                    if ((scenario == "single" || scenario == "enemy") && i % 5 == 0)
                        Capture(camera, output + $"/{scenario}-{i / 5:D3}.png");
                    yield return null;
                }
                for (int distance = 0; distance < 3; distance++)
                {
                    float scale = distance == 0 ? .65f : distance == 1 ? 1 : 2;
                    camera.transform.position = center + new Vector3(6, 5, -7) * scale;
                    camera.transform.LookAt(center + Vector3.up * .5f);
                    Capture(camera, output + $"/{scenario}-view-{distance}.png");
                }
                var bytes = world.Capture(); int peak = 0, occupied = 0;
                for (int i = 0; i < bytes.Length; i += 3) { int h = bytes[i] | bytes[i + 1] << 8; peak = Math.Max(peak, h); if (h > 0) occupied++; }
                File.WriteAllBytes(output + "/" + scenario + ".bin", bytes);
                uint revision = world.Revision;
                for (int i = 0; i < 120; i++) world.Commit(false);
                times.Sort();
                summary.AppendLine(FormattableString.Invariant($"{scenario},{peak * .001:F3},{occupied},{times[113]:F6},{times[118]:F6},{world.Revision - revision}"));
            }
            File.WriteAllText(output + "/metrics.csv", summary.ToString());
            File.WriteAllText(output + "/commits.csv", timings.ToString());
            File.WriteAllText(output + "/environment.txt", $"{SystemInfo.operatingSystem}\n{SystemInfo.processorType}\n{SystemInfo.graphicsDeviceName}\nUnity {Application.unityVersion}\nEditor rendered fixture; commit timings exclude capture and are not Player/GPU frame timings.\n");
        }
        static void Capture(Camera camera, string path)
        {
            var target = new RenderTexture(1280, 720, 24);
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
                File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(pixels); }
        }
    }
}
#endif
