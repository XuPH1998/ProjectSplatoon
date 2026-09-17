#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Splatoon.Tests
{
    // Runs inside the existing Host scenario, with the real arena and presentation pool.
    static class RapidBlasterExplosionChecks
    {
        const string Output = "Reports/RapidBlaster/SphericalExplosion";
        static Transform[] Active(InkPresentation p) => p.transform.Cast<Transform>()
            .Where(t => t.name == "Ink explosion" && t.gameObject.activeSelf).ToArray();

        public static IEnumerator Check(InkExplosionEvent source, PlayerSnapshot visual, PrototypePlayer host, CharacterPresentationProfile profile)
        {
            Directory.CreateDirectory(Output);
            var presentation = InkPresentation.Current;
            var w = GameplayConfig.GetWeapon(5);
            uint id = 900000;
            var samples = new ParticleSystem.Particle[32];
            var evidence = new StringBuilder("team,collision,seconds,particles,max_normalized_extent,changed_pixels,root_instance\n");
            var camera = new GameObject("Blaster third person evidence").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 16f / 9;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            var hostPosition = host.transform.position; var hostRotation = host.transform.rotation;
            // The center lane is clear of the spawn shield; keep the production camera offset/FOV.
            visual.Position = new Vector3(0, .05f, -20); visual.Yaw = 0; visual.Pitch = -9;
            host.transform.SetPositionAndRotation(visual.Position, Quaternion.identity);
            host.CharacterView.Present(visual, 0, host.NetworkManager.ServerTime.Time);
            Physics.SyncTransforms();
            var shotOrigin = visual.Position + Vector3.up * 1.4f;
            var forward = Quaternion.Euler(-9, 0, 0) * Vector3.forward;
            var rotation = Quaternion.LookRotation(forward);
            var center = BlasterBallistics.Position(shotOrigin, forward * w.SpeedMin, w, .25);
            camera.transform.SetPositionAndRotation(PrototypePlayer.CameraPosition(visual.Position +
                PrototypePlayer.CameraPivotOffset(visual, profile), rotation, profile), rotation);
            Vector3 cameraPosition = camera.transform.position;
            Quaternion cameraRotation = camera.transform.rotation;
            source.Position = center; source.Normal = -forward; source.Seed = 6701;
            int pooledInstance = 0;
            GameObject wall = null;
            try
            {
                foreach (bool collision in new[] { false, true }) foreach (byte team in new byte[] { 1, 2 })
                {
                    presentation.Clear();
                    bool fusionBefore = presentation.HasCompositeContent(1 << InkFlightPresentation.Layer);
                    var background = Capture(camera, $"{(collision ? "collision" : "airburst")}-team-{team}-background");
                    var evt = source; evt.ShotId = ++id; evt.Collision = collision; evt.Team = team;
                    evt.Time = host.NetworkManager.ServerTime.Time + .001;
                    presentation.Explosion(evt); presentation.Explosion(evt);
                    var roots = Active(presentation); Assert.That(roots.Length, Is.EqualTo(1), "Duplicate event must not add another sphere");
                    Assert.That(presentation.HasCompositeContent(1 << InkFlightPresentation.Layer), Is.EqualTo(fusionBefore),
                        "The sphere must not change fusion demand from other active muzzle systems");
                    var root = roots[0]; var systems = root.GetComponentsInChildren<ParticleSystem>();
                    if (pooledInstance != 0) Assert.That(root.GetInstanceID(), Is.EqualTo(pooledInstance), "Reuse the cleared burst across teams and sizes");
                    pooledInstance = root.GetInstanceID();
                    Assert.That(systems.Length, Is.EqualTo(3));
                    Assert.That(root.localScale.x, Is.EqualTo(InkExplosionRules.Radius(w.Ammo, collision)).Within(.00001));
                    foreach (var ps in systems)
                    {
                        Assert.That(ps.particleCount, Is.Zero, "A reused burst must not retain previous particles");
                        Assert.That(ps.main.startColor.color, Is.EqualTo(PrototypeArena.TeamColor(team)));
                        Assert.That(ps.gameObject.layer, Is.EqualTo(LayerMask.NameToLayer("TransparentFX")));
                        var renderer = ps.GetComponent<ParticleSystemRenderer>();
                        Assert.That(ShaderUtil.ShaderHasError(renderer.sharedMaterial.shader), Is.False);
                        Assert.That(renderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Mesh));
                    }
                    Assert.That(systems.Select(ps => ps.randomSeed).Distinct().Count(), Is.EqualTo(3));
                    foreach (float time in new[] { .03f, .08f, .16f, .30f })
                    {
                        int count = 0; float extent = 0;
                        foreach (var ps in systems)
                        {
                            ps.Simulate(time, false, true, false);
                            int live = ps.GetParticles(samples); count += live;
                            for (int i = 0; i < live; i++)
                                extent = Mathf.Max(extent, samples[i].position.magnitude + samples[i].GetCurrentSize(ps) * .5f);
                        }
                        Assert.That(extent, Is.LessThanOrEqualTo(1.001f), "All visual layers remain inside the blast radius");
                        Assert.That(systems[0].particleCount, Is.EqualTo(time < .26f ? 1 : 0));
                        Assert.That(count, Is.LessThanOrEqualTo(25));
                        if (time > .26f) Assert.That(count, Is.GreaterThan(0), "Only breakup droplets remain at 0.30 s");
                        string name = $"{(collision ? "collision" : "airburst")}-team-{team}-{Mathf.RoundToInt(time * 1000):D3}ms";
                        var pixels = Capture(camera, name);
                        int visiblePixels = background.Zip(pixels, (a, b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 12).Count(x => x);
                        if (Mathf.Abs(time - .08f) < .001f)
                            Assert.That(visiblePixels, Is.GreaterThan(collision ? 100 : 500), "The peak burst must be visible from the gameplay camera");
                        evidence.AppendLine(FormattableString.Invariant($"{team},{collision},{time:F2},{count},{extent:F4},{visiblePixels},{root.GetInstanceID()}"));
                        if (!collision && team == 1 && Mathf.Abs(time - .08f) < .001f)
                        {
                            foreach (int side in new[] { 90, 180 })
                            {
                                camera.transform.position = center + Quaternion.Euler(0, side, 0) * (cameraPosition - center);
                                camera.transform.LookAt(center); Capture(camera, "airburst-side-" + side);
                            }
                            camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
                        }
                    }
                    foreach (var ps in systems) { ps.Simulate(.39f, false, true, false); Assert.That(ps.particleCount, Is.Zero); }
                }

                // Render a fully occluded burst against the same frozen scene: pixel equality proves depth occlusion.
                wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name = "Sphere burst depth occluder";
                wall.transform.SetPositionAndRotation(center - forward * (w.Ammo.ExplosionRadius + 1), rotation);
                wall.transform.localScale = new Vector3(10, 10, .3f);
                presentation.Clear(); var hidden = Capture(camera, "wall-without-burst");
                source.ShotId = ++id; source.Time = host.NetworkManager.ServerTime.Time + .001; source.Team = 1;
                presentation.Explosion(source);
                foreach (var ps in Active(presentation).Single().GetComponentsInChildren<ParticleSystem>()) ps.Simulate(.08f, false, true, false);
                var occluded = Capture(camera, "wall-occluded-burst");
                int changed = hidden.Zip(occluded, (a, b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 6).Count(x => x);
                Assert.That(changed, Is.LessThan(20), "Wall-hidden burst must not shine through opaque depth");
                // Collision burst sits on the visible wall face, using the authoritative early-blast radius.
                presentation.Clear(); source.ShotId = ++id; source.Collision = true;
                source.Position = wall.transform.position - forward * .16f; source.Time = host.NetworkManager.ServerTime.Time + .001;
                presentation.Explosion(source);
                foreach (var ps in Active(presentation).Single().GetComponentsInChildren<ParticleSystem>()) ps.Simulate(.08f, false, true, false);
                Capture(camera, "wall-collision-visible");
                UnityEngine.Object.DestroyImmediate(wall); wall = null;

                // Natural playback checks that delayed droplets survive until completion, then pool without accumulation.
                presentation.Clear(); source.Position = center;
                Assert.That(presentation.isActiveAndEnabled, Is.True);
                for (int shot = 0; shot < 4; shot++)
                {
                    source.ShotId = ++id; source.Time = host.NetworkManager.ServerTime.Time + .001; source.Team = (byte)(shot % 2 + 1);
                    float started = Time.time;
                    presentation.Explosion(source);
                    Assert.That(Active(presentation).Length, Is.EqualTo(1));
                    while (Time.time < started + .30f) yield return null;
                    if (Time.time < started + .38f)
                        Assert.That(Active(presentation).Length, Is.EqualTo(1), "Delayed droplets may not be recycled early");
                    while (Time.time < started + .55f) yield return null;
                    yield return null; // Allow LateUpdate to recycle after the frame's test continuation.
                    Assert.That(Active(presentation).Length, Is.Zero, $"Completed bursts must leave no active sphere, elapsed={Time.time - started:F3}");
                }
                File.WriteAllText(Output + "/samples.csv", evidence.ToString());
                File.WriteAllText(Output + "/result.txt", "PASS: 3D sphere mesh, both team colors, normal/collision scale, 0.03/0.08/0.16/0.30 s frames, side views, normalized extent <= 1, per-layer seeds, duplicate rejection, pool reset, delayed droplet lifetime, opaque-wall GPU occlusion. Bloom disabled; actual gameplay camera offset/FOV retained. Host only; independent clients and devices not tested.\n");
            }
            finally
            {
                presentation.Clear(); host.transform.SetPositionAndRotation(hostPosition, hostRotation);
                if (wall != null) UnityEngine.Object.DestroyImmediate(wall); UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        static Color32[] Capture(Camera camera, string name)
        {
            var rt = new RenderTexture(1280, 720, 24); var old = RenderTexture.active;
            var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
                File.WriteAllBytes(Output + "/" + name + ".png", texture.EncodeToPNG()); return texture.GetPixels32();
            }
            finally { camera.targetTexture = null; RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
#endif
