using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Splatoon.Tests.Reference;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class FrameOptimizationTests
    {
        const string Output = "Reports/FourPlayerPerformance";
        [SetUp] public void Setup()
        {
            HeroMigrationTests.Load();
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
        }
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static PaintStamp Stamp(int n, Matrix4x4 matrix) => new()
        {
            Sequence = (uint)n + 1, Round = 1, Team = (byte)(n % 2 + 1),
            Position = matrix.MultiplyPoint3x4(new Vector3(Mathf.Sin(n * .7f) * 2, 0, Mathf.Cos(n * .4f) * 2)),
            Normal = matrix.MultiplyVector(Vector3.up).normalized, Radius = .4f + n % 7 * .19f,
            Hardness = .15f + n % 5 * .17f, Strength = .2f + n % 4 * .2f,
            ShapeSeed = InkShapeAtlas.Hash((uint)n * 7297 + 17)
        };
        [Test] public void CachedOwnershipMatchesOriginalBytesAcrossTransformsAndSettings()
        {
            var current = new SurfaceOwnershipGrid(new Vector2(8.07f, 7.81f), .125f, new[] { 0, 17 });
            var reference = new ReferenceOwnershipGrid(new Vector2(8.07f, 7.81f), .125f, new[] { 0, 17 });
            for (int phase = 0; phase < 4; phase++)
            {
                var matrix = Matrix4x4.TRS(new Vector3(phase, phase * 2, 3), Quaternion.Euler(phase * 30, 15, 0), Vector3.one);
                float scale = .034424f + phase * .01f, noise = 4 + phase;
                for (int n = 0; n < 160; n++)
                {
                    var stamp = Stamp(n, matrix);
                    current.Apply(stamp, matrix, .5f, scale, noise); reference.Apply(stamp, matrix, .5f, scale, noise);
                }
                CollectionAssert.AreEqual(reference.Capture(), current.Capture(), "phase " + phase);
                Assert.That(current.PinkArea, Is.EqualTo(reference.PinkArea)); Assert.That(current.BlueArea, Is.EqualTo(reference.BlueArea));
                var snapshot = current.Capture(); current.Clear(); current.Restore(snapshot);
                CollectionAssert.AreEqual(reference.Capture(), current.Capture());
            }
        }
        [Test] public void CachedCpuPaintBenchmarkUsesTheSameStampSequence()
        {
            var current = new SurfaceOwnershipGrid(new Vector2(8, 8), .125f, null);
            var reference = new ReferenceOwnershipGrid(new Vector2(8, 8), .125f, null);
            var stamps = Enumerable.Range(0, 300).Select(n => Stamp(n, Matrix4x4.identity)).ToArray();
            foreach (var stamp in stamps) { current.Apply(stamp, Matrix4x4.identity, .5f, .034424f, 5); reference.Apply(stamp, Matrix4x4.identity, .5f, .034424f, 5); }
            var results = new List<string> { "repeat,referenceMs,optimizedMs" };
            for (int repeat = 0; repeat < 5; repeat++)
            {
                var timer = Stopwatch.StartNew();
                foreach (var stamp in stamps) reference.Apply(stamp, Matrix4x4.identity, .5f, .034424f, 5);
                double before = timer.Elapsed.TotalMilliseconds; timer.Restart();
                foreach (var stamp in stamps) current.Apply(stamp, Matrix4x4.identity, .5f, .034424f, 5);
                results.Add(FormattableString.Invariant($"{repeat},{before:F4},{timer.Elapsed.TotalMilliseconds:F4}"));
            }
            CollectionAssert.AreEqual(reference.Capture(), current.Capture());
            Directory.CreateDirectory(Output); File.WriteAllLines(Output + "/cpu-paint.csv", results);
        }
        static Mesh Plane()
        {
            var mesh = new Mesh { name = "Batch paint atlas with empty border" };
            mesh.vertices = new[] { new Vector3(-4,0,-4), new Vector3(-4,0,4), new Vector3(4,0,4), new Vector3(4,0,-4) };
            mesh.normals = Enumerable.Repeat(Vector3.up, 4).ToArray();
            mesh.uv2 = new[] { new Vector2(.1f,.1f), new Vector2(.1f,.9f), new Vector2(.9f,.9f), new Vector2(.9f,.1f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; mesh.RecalculateBounds(); return mesh;
        }
        static byte[] Pixels(RenderTexture rt)
        {
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            var old = RenderTexture.active;
            try { RenderTexture.active = rt; texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); texture.Apply(); return texture.GetRawTextureData<byte>().ToArray(); }
            finally { RenderTexture.active = old; Object.DestroyImmediate(texture); }
        }
        static void LegacyDraw(Material painter, Mesh mesh, Matrix4x4 matrix, RenderTexture mask, RenderTexture scratch, PaintStamp stamp)
        {
            painter.SetFloat("_PrepareUV", 0); painter.SetVector("_PainterPosition", stamp.Position); painter.SetVector("_PainterNormal", stamp.Normal);
            painter.SetFloat("_Radius", stamp.Radius); painter.SetFloat("_Hardness", stamp.Hardness); painter.SetFloat("_Strength", stamp.Strength);
            painter.SetFloat("_PainterTeam", stamp.Team); painter.SetTexture("_ShapeAtlas", InkShapeAtlas.Texture);
            painter.SetInteger("_ShapeIndex", InkShapeAtlas.Index(stamp.ShapeSeed)); painter.SetVector("_ShapeTransform", InkShapeAtlas.Transform(stamp.ShapeSeed));
            painter.SetVector("_ShapeLayout", new Vector4(InkShapeAtlas.Columns, InkShapeAtlas.Rows, InkShapeAtlas.CellSize, 0));
            painter.SetTexture("_MainTex", scratch);
            var cmd = CommandBufferPool.Get("Original paint oracle");
            cmd.Blit(mask, scratch); cmd.SetRenderTarget(mask); cmd.DrawMesh(mesh, matrix, painter);
            Graphics.ExecuteCommandBuffer(cmd); CommandBufferPool.Release(cmd);
        }
        [TestCase(1)] [TestCase(2)] [TestCase(17)] [TestCase(64)]
        public void BatchedGpuPaintMatchesOriginalBytesAndKeepsMaskIdentity(int count)
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null));
            var go = new GameObject("Batch paint"); var second = new GameObject("Shared scratch sibling"); var mesh = Plane();
            var legacy = new Material(Shader.Find("Hidden/Splatoon/FrameReferencePainter"));
            RenderTexture mask = null, scratch = null;
            try
            {
                PaintSurface Create(GameObject obj)
                {
                    var surface = obj.AddComponent<PaintSurface>(); surface.Resolution = 128; surface.ResolutionHeight = 96;
                    surface.ShapeAtlas = InkShapeAtlas.Texture; surface.PainterShader = Shader.Find("Splatoon/InkTexturePainter");
                    Assert.That(ShaderUtil.ShaderHasError(surface.PainterShader), Is.False);
                    surface.DisplayShader = Shader.Find("Splatoon/InkDisplay"); obj.GetComponent<MeshFilter>().sharedMesh = mesh;
                    return surface;
                }
                var surface = Create(go); var sibling = Create(second);
                go.transform.SetPositionAndRotation(new Vector3(3, 1, 2), Quaternion.Euler(27, 16, 0));
                var matrix = go.transform.localToWorldMatrix;
                var first = Stamp(100, matrix); surface.Apply(first); var identity = surface.Mask; surface.Clear();
                mask = new RenderTexture(identity.descriptor); mask.Create(); scratch = new RenderTexture(identity.descriptor); scratch.filterMode = FilterMode.Bilinear; scratch.Create();
                var previous = RenderTexture.active; RenderTexture.active = mask; GL.Clear(false,true,Color.clear); RenderTexture.active = previous;
                for (int i = 0; i < count; i++) { var stamp = Stamp(i, matrix); surface.Apply(stamp); LegacyDraw(legacy, mesh, matrix, mask, scratch, stamp); }
                Assert.That(surface.PendingPaintCount, Is.EqualTo(count));
                sibling.Apply(Stamp(8, Matrix4x4.identity)); sibling.FlushDisplay();
                long beforeFlushCopies = FramePerformance.PaintCopies, beforeFlushSubmits = FramePerformance.PaintSubmissions;
                surface.FlushDisplay(); Assert.That(surface.Mask, Is.SameAs(identity));
                Assert.That(FramePerformance.PaintCopies - beforeFlushCopies, Is.EqualTo(count % 2));
                Assert.That(FramePerformance.PaintSubmissions - beforeFlushSubmits, Is.EqualTo(1));
                var expected = Pixels(mask); CollectionAssert.AreEqual(expected, Pixels(surface.Mask));
                // A checkpoint read flushes pending work without publishing a different texture identity.
                var extra = Stamp(101, matrix); surface.Apply(extra); LegacyDraw(legacy, mesh, matrix, mask, scratch, extra);
                CollectionAssert.AreEqual(Pixels(mask), Pixels(surface.Mask)); Assert.That(surface.PendingPaintCount, Is.Zero);
                surface.Apply(first);
                Assert.Throws<InvalidOperationException>(() => surface.Restore(new byte[1]));
                Assert.That(surface.PendingPaintCount, Is.EqualTo(1), "Rejected restores must not discard pending paint");
                surface.Restore(expected); CollectionAssert.AreEqual(expected, Pixels(surface.Mask));
                surface.Apply(first); surface.Clear(); Assert.That(Pixels(surface.Mask).All(x => x == 0), Is.True);
            }
            finally
            {
                go.GetComponent<PaintSurface>()?.ReleaseGraphics(); second.GetComponent<PaintSurface>()?.ReleaseGraphics();
                Object.DestroyImmediate(go); Object.DestroyImmediate(second); Object.DestroyImmediate(mesh); Object.DestroyImmediate(legacy);
                if (mask != null) { mask.Release(); Object.DestroyImmediate(mask); } if (scratch != null) { scratch.Release(); Object.DestroyImmediate(scratch); }
            }
        }
        [Test] public void FlightCacheMatchesOriginalParticlesAndRibbonAfterImpacts()
        {
            var a = new GameObject("Optimized flight"); var b = new GameObject("Original flight"); var camera = a.AddComponent<Camera>();
            camera.transform.position = new Vector3(3, 2, -4);
            var weapon = new WeaponRuntimeConfig(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset"));
            var current = new InkFlightPresentation(a.transform, weapon.Ammo);
            var reference = new ReferenceInkFlightPresentation(b.transform, weapon.Ammo);
            try
            {
                for (uint i = 1; i <= 36; i++)
                {
                    var shot = new InkShot { Id = i, Round = 1, Team = (byte)(i % 2 + 1), Shooter = i % 4, Seed = i * 317,
                        Born = 10 + i * .011, Origin = Vector3.up * 2, Velocity = new Vector3(.3f, .1f, 1) * 32,
                        PostCorrectionVelocity = Vector3.forward * 31, FirstSegmentLength = 2, GravityStartAge = .04f, Configuration = weapon };
                    Assert.That(current.Spawn(shot, 10.4), Is.EqualTo(reference.Spawn(shot, 10.4)));
                }
                var p = new ParticleSystem.Particle[2048]; var q = new ParticleSystem.Particle[2048];
                for (int frame = 0; frame < 30; frame++)
                {
                    if (frame % 3 == 0) { var impact = new InkImpact { Round = 1, Id = (uint)frame + 1 }; current.Complete(impact); reference.Complete(impact); }
                    double now = 10.4 + frame * .017; current.Update(now, camera); reference.Update(now, camera);
                    for (byte team = 1; team <= 2; team++)
                    {
                        int count = current.CopyParticles(team, p); Assert.That(count, Is.EqualTo(reference.CopyParticles(team, q)));
                        for (int i = 0; i < count; i++)
                        {
                            Assert.That(p[i].position, Is.EqualTo(q[i].position)); Assert.That(p[i].velocity, Is.EqualTo(q[i].velocity));
                            Assert.That(p[i].randomSeed, Is.EqualTo(q[i].randomSeed)); Assert.That(p[i].remainingLifetime, Is.EqualTo(q[i].remainingLifetime));
                        }
                    }
                    var meshesA = a.GetComponentsInChildren<MeshFilter>(); var meshesB = b.GetComponentsInChildren<MeshFilter>();
                    Assert.That(meshesA.Length, Is.EqualTo(meshesB.Length));
                    for (int i = 0; i < meshesA.Length; i++)
                    {
                        var x = meshesA[i].sharedMesh; var y = meshesB[i].sharedMesh;
                        CollectionAssert.AreEqual(y.triangles, x.triangles);
                        foreach (int index in x.triangles.Distinct())
                        {
                            Assert.That(x.vertices[index], Is.EqualTo(y.vertices[index])); Assert.That(x.normals[index], Is.EqualTo(y.normals[index]));
                            Assert.That(x.uv[index], Is.EqualTo(y.uv[index])); Assert.That(x.colors[index], Is.EqualTo(y.colors[index]));
                        }
                    }
                }
                var times = new List<string> { "repeat,referenceMs,optimizedMs" };
                for (int repeat = 0; repeat < 5; repeat++)
                {
                    var timer = Stopwatch.StartNew(); for (int i = 0; i < 180; i++) reference.Update(10.95, camera);
                    double before = timer.Elapsed.TotalMilliseconds; timer.Restart(); for (int i = 0; i < 180; i++) current.Update(10.95, camera);
                    times.Add(FormattableString.Invariant($"{repeat},{before:F4},{timer.Elapsed.TotalMilliseconds:F4}"));
                }
                Directory.CreateDirectory(Output); File.WriteAllLines(Output + "/flight-cpu.csv", times);
            }
            finally { current.Dispose(); reference.Dispose(); Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }
        [Test] public void UnusedFlightLanesDoNotAllocateNativeVertexBuffers()
        {
            var root = new GameObject("Lazy native ribbons"); var camera = root.AddComponent<Camera>();
            var weapon = new WeaponRuntimeConfig(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset"));
            var flight = new InkFlightPresentation(root.transform, weapon.Ammo);
            try
            {
                var meshes = root.GetComponentsInChildren<MeshFilter>(true).Select(x => x.sharedMesh).ToArray();
                Assert.That(meshes.All(m => m.vertexCount == 0), Is.True);
                flight.Spawn(new InkShot { Id = 1, Round = 1, Team = 1, Seed = 17, Born = 10, Velocity = Vector3.forward * 32, Configuration = weapon },10);
                flight.Update(10.1,camera);
                Assert.That(meshes.Count(m => m.vertexCount > 0), Is.EqualTo(1));
                flight.Clear(); Assert.That(meshes.Count(m => m.vertexCount > 0), Is.EqualTo(1), "Retain the used lane for allocation-free reuse");
            }
            finally { flight.Dispose(); Object.DestroyImmediate(root); }
        }
        [Test] public void InkCameraGatePreservesReferenceCamerasAndExcludesPaperLayer()
        {
            var feature = ScriptableObject.CreateInstance<RenderMetaballsScreenSpace>(); var go = new GameObject("Camera gate");
            try
            {
                var camera = go.AddComponent<Camera>(); feature.FilterSettings.LayerMask = 1 << 11;
                camera.cullingMask = 1 << PaperCapture.Layer; Assert.That(feature.ShouldRender(camera), Is.False);
                camera.cullingMask = 1 << 11; Assert.That(feature.ShouldRender(camera), Is.True);
            }
            finally { Object.DestroyImmediate(feature); Object.DestroyImmediate(go); }
        }
        [Test] public void EmptyFlightGateKeepsMuzzleAndRetirementTails()
        {
            var root = new GameObject("Composite lifetime"); var presentation = root.AddComponent<InkPresentation>();
            var weapon = new WeaponRuntimeConfig(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset"));
            try
            {
                Assert.That(presentation.HasCompositeContent(1 << 11), Is.False);
                var muzzle = presentation.CreateMuzzle(root.transform, weapon); muzzle.Shot(123, 1, true, 10, 10);
                Assert.That(presentation.HasCompositeContent(1 << 11), Is.True);
                muzzle.Present(false, true, 10.01); Assert.That(presentation.HasCompositeContent(1 << 11), Is.True);
                muzzle.Retire(root.transform); Assert.That(presentation.HasCompositeContent(1 << 11), Is.True);
                muzzle.System.Simulate(2, true, false); muzzle.System.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Assert.That(presentation.HasCompositeContent(1 << 11), Is.False);
                Assert.That(presentation.HasCompositeContent(1 << 13), Is.True, "Unknown effect layers remain conservative");
            }
            finally { Object.DestroyImmediate(root); }
        }
        [Test] public void AuthoredMapBatchAndNeighbourOwnershipMatchOriginal()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity", UnityEditor.SceneManagement.OpenSceneMode.Additive);
            var legacy = new Material(Shader.Find("Hidden/Splatoon/FrameReferencePainter"));
            var states = new Dictionary<int, RenderTexture>(); var scratch = new Dictionary<int, RenderTexture>();
            var ownership = new Dictionary<int, ReferenceOwnershipGrid>();
            try
            {
                var arena = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<PrototypeArena>()).Single();
                arena.RegisterSurfaces();
                foreach (var surface in arena.Surfaces.Values)
                {
                    surface.InitializeOwnership(GameplayConfig.Map.CellSize);
                    foreach (var region in surface.GameplayRegions) ownership.Add(region.Key(surface), new ReferenceOwnershipGrid(region.Size, region.Grid.CellSize, region == surface.GameplayRegions.First() && surface.Scores ? surface.BlockedCells : region.BlockedCells));
                }
                void ApplyLegacy(PaintSurface surface, PaintStamp stamp)
                {
                    if (!states.TryGetValue(surface.SurfaceId, out var rt))
                    {
                        rt = new RenderTexture(surface.Resolution, surface.Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                        rt.filterMode = FilterMode.Point; rt.Create(); states.Add(surface.SurfaceId, rt);
                        var temp = new RenderTexture(rt.descriptor) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; temp.Create(); scratch.Add(surface.SurfaceId,temp);
                        var old = RenderTexture.active; RenderTexture.active = rt; GL.Clear(false,true,Color.clear); RenderTexture.active = old;
                    }
                    LegacyDraw(legacy, surface.GetComponent<MeshFilter>().sharedMesh, surface.transform.localToWorldMatrix, rt, scratch[surface.SurfaceId], stamp);
                    foreach (var region in surface.GameplayRegions)
                    {
                        var matrix = surface.transform.localToWorldMatrix * Matrix4x4.TRS(region.Origin, region.Rotation, Vector3.one);
                        if (Vector3.Dot(matrix.MultiplyVector(Vector3.up).normalized, stamp.Normal) < .95f || Mathf.Abs(matrix.inverse.MultiplyPoint3x4(stamp.Position).y) > .06f) continue;
                        ownership[region.Key(surface)].Apply(stamp, matrix, GameplayConfig.Global.PaintThreshold, GameplayConfig.Global.PaintWorldUvScale, GameplayConfig.Global.PaintShapeNoiseScale);
                    }
                }
                uint sequence = 0;
                foreach (var source in arena.Surfaces.Values) foreach (var region in source.GameplayRegions)
                for (int n = 0; n < 3; n++)
                {
                    var matrix = source.transform.localToWorldMatrix * Matrix4x4.TRS(region.Origin, region.Rotation, Vector3.one);
                    var stamp = Stamp((int)sequence++, matrix); stamp.SurfaceId = source.SurfaceId; stamp.Radius = 2;
                    stamp.Position = matrix.MultiplyPoint3x4(new Vector3(n == 0 ? 0 : region.Size.x / 2 * (n == 1 ? 1 : -1),0,0));
                    arena.Apply(stamp,true); ApplyLegacy(source,stamp);
                    foreach (var neighbour in arena.Surfaces.Values)
                    {
                        if (neighbour == source) continue;
                        foreach (var adjacent in neighbour.GameplayRegions)
                        {
                            var m = neighbour.transform.localToWorldMatrix * Matrix4x4.TRS(adjacent.Origin, adjacent.Rotation, Vector3.one);
                            var inverse = m.inverse; var local = inverse.MultiplyPoint3x4(stamp.Position); var extent = InkShapeAtlas.LocalExtents(stamp,inverse);
                            if (Vector3.Dot(m.MultiplyVector(Vector3.up).normalized,stamp.Normal)<.9999f || Mathf.Abs(local.y)>.005f || Mathf.Abs(local.x)>adjacent.Size.x/2+extent.x || Mathf.Abs(local.z)>adjacent.Size.y/2+extent.y) continue;
                            ApplyLegacy(neighbour,stamp); break;
                        }
                    }
                }
                foreach (var surface in arena.Surfaces.Values)
                {
                    if (states.TryGetValue(surface.SurfaceId,out var rt)) CollectionAssert.AreEqual(Pixels(rt),Pixels(surface.Mask),"GPU surface "+surface.SurfaceId);
                    foreach (var region in surface.GameplayRegions) CollectionAssert.AreEqual(ownership[region.Key(surface)].Capture(),region.Grid.Capture(),"CPU region "+region.Key(surface));
                }
                Directory.CreateDirectory(Output); File.WriteAllText(Output+"/map-equivalence.txt",$"{sequence} stamps; {states.Count} GPU surfaces; {ownership.Count} CPU regions: byte-identical to original algorithms.\n");
            }
            finally
            {
                foreach (var rt in states.Values) { rt.Release(); Object.DestroyImmediate(rt); }
                foreach (var rt in scratch.Values) { rt.Release(); Object.DestroyImmediate(rt); }
                foreach (var surface in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PaintSurface>())) surface.ReleaseGraphics();
                Object.DestroyImmediate(legacy); UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
