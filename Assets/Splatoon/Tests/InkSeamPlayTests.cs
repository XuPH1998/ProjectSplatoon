#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class InkSeamPlayTests
    {
        static IEnumerator Wait(Func<bool> ready, string message)
        {
            double end = Time.realtimeSinceStartupAsDouble + 40;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, message);
        }

        [UnityTest] public IEnumerator HostShootsAcrossSeamsAndRestoresPaint()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }

        static IEnumerator Scenario()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && Camera.main != null, "Host ready");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current; var arena = match.Arena;
            app.CaptureMouse(false);
            var camera = new GameObject("Seam capture").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1;
            var target = new Vector3(-10.5f, 3, -5);
            camera.transform.position = target + new Vector3(2, 4, -4);
            camera.transform.LookAt(target); camera.fieldOfView = 42;
            Directory.CreateDirectory(InkSeamTests.Output);
            var stamps = new List<PaintStamp>();
            match.Projectiles.PaintObserved = stamps.Add;
            foreach (int hero in new[] { 1, 4 })
            {
                match.enabled = true; player.enabled = true;
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Hero selected");
                match.enabled = false; player.enabled = false;
                arena.ClearPaint(); match.Projectiles.Clear(); stamps.Clear();
                var state = player.Snapshot.Value;
                state.Position = target + new Vector3(0, -.46f, -1);
                state.Pitch = 45; state.Yaw = state.BodyYaw = 0;
                state.ProtectedUntil = 0; state.Ink = 100; state.Health = 100; state.Grounded = true;
                double now = match.NetworkManager.ServerTime.Time;
                for (int shot = 0; shot < 12; shot++)
                {
                    state.ShotSequence++; state.FireBurstSequence += 2;
                    state.Pitch = 45; state.Yaw = state.BodyYaw = -15 + shot * 3;
                    double born = now + shot * .2;
                    match.Projectiles.Spawn(player, state, born, match.State.Value.Round);
                    match.Projectiles.Simulate(born + .2);
                    foreach (var s in arena.Surfaces.Values) s.FlushDisplay();
                    yield return null;
                    if (shot == 0) Capture(camera, $"hero-{hero}-first");
                }
                match.Projectiles.Simulate(now + 4);
                foreach (var s in arena.Surfaces.Values) s.FlushDisplay();
                Capture(camera, $"hero-{hero}-sweep");
                Assert.That(stamps.Count, Is.GreaterThan(12));
                Assert.That(arena.Surfaces.Values.Single(s => s.name == "Ramp_-1_-1").Ownership.PinkArea, Is.GreaterThan(0));
                Assert.That(arena.Surfaces.Values.Single(s => s.name == "Platform_-1").Ownership.PinkArea, Is.GreaterThan(0));
                var ownership = arena.CaptureOwnership();
                var raw = arena.Surfaces.Values.ToDictionary(s => s.SurfaceId, s => Read(s.Mask));
                var visual = arena.Surfaces.Values.ToDictionary(s => s.SurfaceId, s => InkAppearanceProfile.PackVisual(Read(s.VisualState)));
                var display = arena.Surfaces.Values.ToDictionary(s => s.SurfaceId, s => Read(s.DisplayMask));
                arena.ClearPaint(); arena.RestoreOwnership(ownership);
                foreach (var s in arena.Surfaces.Values)
                {
                    s.Restore(raw[s.SurfaceId], visual[s.SurfaceId]);
                    CollectionAssert.AreEqual(visual[s.SurfaceId], InkAppearanceProfile.PackVisual(Read(s.VisualState)), "Visual restore: " + s.name);
                    CollectionAssert.AreEqual(raw[s.SurfaceId], Read(s.Mask), "Raw restore: " + s.name);
                    CollectionAssert.AreEqual(display[s.SurfaceId], Read(s.DisplayMask), "Display restore: " + s.name);
                }
                File.WriteAllText(InkSeamTests.Output + $"/host-hero-{hero}.txt", $"PASS: {stamps.Count} stamps through real Spawn; Mask and DisplayMask restore exactly on all surfaces.\n");
            }
            match.Projectiles.PaintObserved = null;
            UnityEngine.Object.Destroy(camera.gameObject);
            yield return app.Leave().ToCoroutine();
        }

        static byte[] Read(RenderTexture target)
        {
            var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            try { texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); return texture.GetRawTextureData<byte>().ToArray(); }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(texture); }
        }

        static void Capture(Camera camera, string name)
        {
            var target = RenderTexture.GetTemporary(960, 960, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var texture = new Texture2D(960, 960, TextureFormat.RGBA32, false, false);
            try
            {
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); texture.Apply();
                File.WriteAllBytes(InkSeamTests.Output + "/" + name + ".png", texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(texture); }
        }
    }
}
#endif
