#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    [Category("PaperContourHandoff65")]
    public sealed class PaperContourHandoff65Tests
    {
        PaperBodyProfile profile;
        readonly List<GameObject> players = new();
        readonly List<PaperCapture> captures = new();

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
        }
        [TearDown] public void Cleanup()
        {
            foreach (var player in players)
            {
                var body = player == null ? null : player.GetComponentInChildren<SwimBody>();
                if (body != null) PaperBodyTests.ReleaseTestBody(body);
                if (player != null) Object.DestroyImmediate(player);
            }
            players.Clear();
            foreach (var capture in captures) capture.Dispose();
            captures.Clear();
            if (profile != null) Object.DestroyImmediate(profile);
            LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        SwimBody Body()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            Assert.That(prefab, Is.Not.Null);
            var root = Object.Instantiate(prefab); players.Add(root);
            var body = root.GetComponentInChildren<SwimBody>();
            Assert.That(body, Is.Not.Null); body.Bind(profile); return body;
        }
        PaperCapture Capture(bool contours = false)
        {
            var capture = contours ? PaperCapture.CreateContourEvaluator(profile) : new PaperCapture(profile);
            captures.Add(capture); return capture;
        }
        static PlayerSnapshot Pose(int frame)
            => new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Team = 1, Health = 100,
                LifeState = PlayerLifeState.Alive, Swimming = true, PaperPose = PaperPose.Ground,
                PaperRotation = Quaternion.identity, PaperAnimationTime = (frame + .25f) / PaperAnimation.FramesPerSecond,
                PaperMove = new Vector2(.25f, -.25f) };
        static void EvictShared(PaperCapture noise, ref int noiseFrame)
        {
            // New exact frame keys, through a separate evaluator: the body's
            // local64 is untouched. 257 inserts evict every older global256 key.
            for (int i = 0; i < 257; i++) noise.QueryContour(Pose(noiseFrame++));
        }
        static void SameShape(PaperCapture oracle, PaperCapture actual)
        {
            CollectionAssert.AreEqual(oracle.HitRects, actual.HitRects);
            CollectionAssert.AreEqual(oracle.HitMesh.vertices, actual.HitMesh.vertices);
            CollectionAssert.AreEqual(oracle.HitMesh.triangles, actual.HitMesh.triangles);
            Assert.That(actual.HitMesh.bounds, Is.EqualTo(oracle.HitMesh.bounds));
            Assert.That(actual.PosedBounds, Is.EqualTo(oracle.PosedBounds));
            Assert.That(actual.HitMesh, Is.Not.SameAs(oracle.HitMesh));
        }
        static Color32[] Pixels(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source; image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                image.Apply(); return image.GetPixels32();
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
        }

        sealed class LiveStamp
        {
            const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            readonly SwimBody body;
            readonly PaperCapture capture;
            readonly int version;
            readonly IReadOnlyList<Rect> rects;
            readonly Rect[] values;
            readonly Mesh hitMesh, renderMesh;
            readonly Vector3[] hitVertices;
            readonly int[] hitTriangles;
            readonly Bounds hitBounds, posedBounds;
            readonly RenderTexture texture;
            readonly Color32[] pixels;
            readonly uint textureUpdate;
            readonly bool hitEnabled, rendererEnabled;
            readonly Material[] materials;
            readonly Texture baseMap;
            readonly Color baseColor;
            readonly Vector4 texelSize;
            readonly float enemyOutline;
            readonly GameObject stage;
            readonly (Transform Transform, Vector3 Position, Quaternion Rotation, Vector3 Scale)[] transforms;
            readonly (string Name, object Value)[] sampleFields;
            readonly (Mesh Mesh, Vector3[] Vertices, Vector3[] Normals, Vector4[] Tangents, int[] Triangles, Bounds Bounds)[] parts;
            public LiveStamp(SwimBody source)
            {
                body = source; capture = body.Capture; Assert.That(capture, Is.Not.Null);
                version = capture.PoseVersion; rects = body.HitRects; values = rects.ToArray();
                hitMesh = body.HitVolume.sharedMesh; Assert.That(hitMesh, Is.Not.Null);
                hitVertices = hitMesh.vertices; hitTriangles = hitMesh.triangles; hitBounds = hitMesh.bounds;
                posedBounds = capture.PosedBounds; texture = capture.Texture; Assert.That(texture, Is.Not.Null);
                pixels = Pixels(texture); textureUpdate = texture.updateCount;
                hitEnabled = body.HitVolume.enabled; rendererEnabled = body.BodyRenderer.enabled;
                renderMesh = body.BodyRenderer.GetComponent<MeshFilter>().sharedMesh; materials = body.BodyRenderer.sharedMaterials;
                var block = new MaterialPropertyBlock(); body.BodyRenderer.GetPropertyBlock(block);
                baseMap = block.GetTexture("_BaseMap"); baseColor = block.GetColor("_BaseColor");
                texelSize = block.GetVector("_BaseMap_TexelSize"); enemyOutline = block.GetFloat("_EnemyOutline");
                stage = (GameObject)typeof(PaperCapture).GetField("_root", Hidden).GetValue(capture);
                transforms = stage.GetComponentsInChildren<Transform>(true)
                    .Concat(new[] { body.HitVolume.transform, body.BodyRenderer.transform, body.transform.root }).Distinct()
                    .Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToArray();
                sampleFields = new[] { "_frame", "_move", "_sampleKey", "_hasSample", "_bakedFrame", "_bakedMove", "_renderedVersion" }
                    .Select(name => (name, typeof(PaperCapture).GetField(name, Hidden).GetValue(capture))).ToArray();
                var posedParts = (IEnumerable)typeof(PaperCapture).GetField("_parts", Hidden).GetValue(capture);
                parts = posedParts.Cast<object>().Select(part =>
                {
                    var mesh = (Mesh)part.GetType().GetField("Mesh", BindingFlags.Instance | BindingFlags.Public).GetValue(part);
                    return (mesh, mesh.vertices, mesh.normals, mesh.tangents, mesh.triangles, mesh.bounds);
                }).ToArray();
            }
            public void AssertUnchanged()
            {
                Assert.That(body.Capture, Is.SameAs(capture)); Assert.That(capture.PoseVersion, Is.EqualTo(version));
                Assert.That(body.HitRects, Is.SameAs(rects)); CollectionAssert.AreEqual(values, body.HitRects);
                Assert.That(body.HitVolume.sharedMesh, Is.SameAs(hitMesh));
                CollectionAssert.AreEqual(hitVertices, hitMesh.vertices); CollectionAssert.AreEqual(hitTriangles, hitMesh.triangles);
                Assert.That(hitMesh.bounds, Is.EqualTo(hitBounds)); Assert.That(capture.PosedBounds, Is.EqualTo(posedBounds));
                Assert.That(capture.Texture, Is.SameAs(texture)); Assert.That(texture.updateCount, Is.EqualTo(textureUpdate));
                CollectionAssert.AreEqual(pixels, Pixels(texture));
                Assert.That(body.HitVolume.enabled, Is.EqualTo(hitEnabled)); Assert.That(body.BodyRenderer.enabled, Is.EqualTo(rendererEnabled));
                Assert.That(body.BodyRenderer.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(renderMesh));
                CollectionAssert.AreEqual(materials, body.BodyRenderer.sharedMaterials);
                var block = new MaterialPropertyBlock(); body.BodyRenderer.GetPropertyBlock(block);
                Assert.That(block.GetTexture("_BaseMap"), Is.SameAs(baseMap)); Assert.That(block.GetColor("_BaseColor"), Is.EqualTo(baseColor));
                Assert.That(block.GetVector("_BaseMap_TexelSize"), Is.EqualTo(texelSize)); Assert.That(block.GetFloat("_EnemyOutline"), Is.EqualTo(enemyOutline));
                foreach (var item in sampleFields)
                    Assert.That(typeof(PaperCapture).GetField(item.Name, Hidden).GetValue(capture), Is.EqualTo(item.Value), item.Name);
                var currentTransforms = stage.GetComponentsInChildren<Transform>(true)
                    .Concat(new[] { body.HitVolume.transform, body.BodyRenderer.transform, body.transform.root }).Distinct().ToArray();
                CollectionAssert.AreEqual(transforms.Select(t => t.Transform), currentTransforms);
                foreach (var item in transforms)
                {
                    Assert.That(item.Transform.localPosition, Is.EqualTo(item.Position));
                    Assert.That(item.Transform.localRotation, Is.EqualTo(item.Rotation));
                    Assert.That(item.Transform.localScale, Is.EqualTo(item.Scale));
                }
                foreach (var part in parts)
                {
                    CollectionAssert.AreEqual(part.Vertices, part.Mesh.vertices); CollectionAssert.AreEqual(part.Normals, part.Mesh.normals);
                    CollectionAssert.AreEqual(part.Tangents, part.Mesh.tangents); CollectionAssert.AreEqual(part.Triangles, part.Mesh.triangles);
                    Assert.That(part.Mesh.bounds, Is.EqualTo(part.Bounds));
                }
            }
        }

        [TestCase("RifleGirl", false)]
        [TestCase("MachineGunGirl", false)]
        [TestCase("RifleGirl", true)]
        public void LocalSurvivorRestoresOnlyItsFullExactKeyAndKeepsOriginalRender(string hero, bool changeThickness)
        {
            PaperSamplingDiagnostics.Enable();
            // A private profile identity prevents accidental prior-suite key reuse.
            profile = Object.Instantiate(PaperBodyTests.Profile(hero));
            var body = Body(); var a = Pose(3); var b = Pose(21);
            body.ApplyCollision(b);
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "A real B texture/material baseline and final texture comparison are required");
            body.Present(b, Vector3.zero, Quaternion.identity);
            var liveB = new LiveStamp(body);
            var retained = body.QueryTraversalRects(a); var frozen = retained.ToArray();
            liveB.AssertUnchanged();
            using var noise = PaperCapture.CreateContourEvaluator(profile);
            int noiseFrame = 1000; EvictShared(noise, ref noiseFrame);
            long sharedHits = PaperCapture.SharedPoseCacheHits;
            var counterBefore = PaperSamplingDiagnostics.ReadCumulative();
            Assert.That(body.QueryTraversalRects(a), Is.SameAs(retained));
            Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(sharedHits), "Still a body-local hit after global eviction");
            var counterAfter = PaperSamplingDiagnostics.ReadCumulative();
            Assert.That(counterAfter.localHit, Is.EqualTo(counterBefore.localHit + 1));
            Assert.That(counterAfter.traversalRequests, Is.EqualTo(counterBefore.traversalRequests + 1));
            Assert.That(counterAfter.localMiss, Is.EqualTo(counterBefore.localMiss));
            Assert.That(counterAfter.globalHit, Is.EqualTo(counterBefore.globalHit));
            Assert.That(counterAfter.realMiss, Is.EqualTo(counterBefore.realMiss));
            liveB.AssertUnchanged();
            if (changeThickness) profile.Thickness += .01f; // No candidate requery: offer a genuinely stale local hint.

            long bakes = PaperCapture.PoseBakeCount, misses = PaperCapture.SharedPoseCacheMisses;
            long handoffs = PaperCapture.LocalContourHandoffHits;
            int version = body.Capture.PoseVersion;
            counterBefore = PaperSamplingDiagnostics.ReadCumulative();
            body.ApplyCollision(a);
            Assert.That(body.Capture.PoseVersion, Is.EqualTo(version + 1));
            Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1));
            Assert.That(PaperCapture.LocalContourHandoffHits, Is.EqualTo(handoffs + (changeThickness ? 0 : 1)));
            Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(bakes + (changeThickness ? 1 : 0)));
            counterAfter = PaperSamplingDiagnostics.ReadCumulative();
            Assert.That(counterAfter.handoffHit, Is.EqualTo(counterBefore.handoffHit + (changeThickness ? 0 : 1)));
            Assert.That(counterAfter.hintReject, Is.EqualTo(counterBefore.hintReject + (changeThickness ? 1 : 0)));
            Assert.That(counterAfter.realMiss, Is.EqualTo(counterBefore.realMiss + (changeThickness ? 1 : 0)));
            Assert.That(counterAfter.queryRealMiss, Is.EqualTo(counterBefore.queryRealMiss));
            Assert.That(counterAfter.globalHit, Is.EqualTo(counterBefore.globalHit));
            Assert.That(counterAfter.liveNoop, Is.EqualTo(counterBefore.liveNoop));
            CollectionAssert.AreEqual(frozen, retained, "Published work never mutates the retained immutable value");

            // An ordinary independent uncached capture, never QueryContour and
            // never Sample on a contour-only evaluator, is the geometry oracle.
            using var oracle = new PaperCapture(profile);
            counterBefore = PaperSamplingDiagnostics.ReadCumulative();
            oracle.SampleUncachedForDiagnostics(a);
            counterAfter = PaperSamplingDiagnostics.ReadCumulative();
            Assert.That(counterAfter.realMiss, Is.EqualTo(counterBefore.realMiss), "Independent uncached oracle is not a natural cache miss");
            Assert.That(counterAfter.globalHit, Is.EqualTo(counterBefore.globalHit));
            Assert.That(counterAfter.liveNoop, Is.EqualTo(counterBefore.liveNoop));
            SameShape(oracle, body.Capture);
            if (!changeThickness)
            {
                // Handoff deliberately does not refill/refresh the global FIFO.
                // The original public no-hint path still misses and really bakes.
                using var noHint = new PaperCapture(profile);
                long before = PaperCapture.PoseBakeCount;
                noHint.Sample(a);
                Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(before + 1));
                SameShape(oracle, noHint);
                body.ApplyCollision(b); handoffs = PaperCapture.LocalContourHandoffHits;
                sharedHits = PaperCapture.SharedPoseCacheHits; bakes = PaperCapture.PoseBakeCount;
                counterBefore = PaperSamplingDiagnostics.ReadCumulative();
                body.ApplyCollision(a);
                Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(sharedHits + 1));
                Assert.That(PaperCapture.LocalContourHandoffHits, Is.EqualTo(handoffs), "Global hit remains the original preferred path");
                Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(bakes));
                counterAfter = PaperSamplingDiagnostics.ReadCumulative();
                Assert.That(counterAfter.globalHit, Is.EqualTo(counterBefore.globalHit + 1));
                Assert.That(counterAfter.queryGlobalHit, Is.EqualTo(counterBefore.queryGlobalHit));
                Assert.That(counterAfter.handoffHit, Is.EqualTo(counterBefore.handoffHit));
                Assert.That(counterAfter.realMiss, Is.EqualTo(counterBefore.realMiss));
            }
            version = body.Capture.PoseVersion; bakes = PaperCapture.PoseBakeCount;
            sharedHits = PaperCapture.SharedPoseCacheHits; misses = PaperCapture.SharedPoseCacheMisses;
            handoffs = PaperCapture.LocalContourHandoffHits;
            counterBefore = PaperSamplingDiagnostics.ReadCumulative();
            body.ApplyCollision(a);
            Assert.That(body.Capture.PoseVersion, Is.EqualTo(version), "Live same-key remains a complete noop");
            Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(bakes));
            Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(sharedHits));
            Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses));
            Assert.That(PaperCapture.LocalContourHandoffHits, Is.EqualTo(handoffs));
            counterAfter = PaperSamplingDiagnostics.ReadCumulative();
            Assert.That(counterAfter.liveNoop, Is.EqualTo(counterBefore.liveNoop + 1));
            Assert.That(counterAfter.globalHit, Is.EqualTo(counterBefore.globalHit));
            Assert.That(counterAfter.realMiss, Is.EqualTo(counterBefore.realMiss));
            Assert.That(counterAfter.handoffHit, Is.EqualTo(counterBefore.handoffHit));
            Assert.That(counterAfter.hintReject, Is.EqualTo(counterBefore.hintReject));
            Assert.That(counterAfter.HasValidPartitions(), Is.True);

            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "This case includes actual texture comparison; a graphicsless run cannot pass it");
            counterBefore = PaperSamplingDiagnostics.ReadCumulative();
            body.Capture.Render(); oracle.Render();
            counterAfter = PaperSamplingDiagnostics.ReadCumulative();
            Assert.That(counterAfter.deferredRenderBake, Is.EqualTo(counterBefore.deferredRenderBake + (changeThickness ? 0 : 1)));
            Assert.That(body.Capture.Texture, Is.Not.Null); Assert.That(oracle.Texture, Is.Not.Null);
            CollectionAssert.AreEqual(Pixels(oracle.Texture), Pixels(body.Capture.Texture));
            SameShape(oracle, body.Capture);
        }

        // Intentionally excluded from ordinary suites: this is an opt-in paired
        // microdiagnostic, not a formal performance gate or a historical64 A/B.
        // It times the SAME public ApplyCollision pipeline with and without the
        // local immutable hint in this new runtime; prep/oracles/asserts/logs are
        // outside the measured interval. Repeated eviction is intentionally costly.
        [Test, Explicit("Run serially only after correctness review; timing is diagnostic and not acceptance")]
        public void PairedHintAndNoHintPublicationTiming()
        {
            PaperSamplingDiagnostics.Enable();
            profile = Object.Instantiate(PaperBodyTests.Profile("RifleGirl"));
            var hintBody = Body(); var noHintBody = Body();
            var a = Pose(3); var b = Pose(21);
            hintBody.QueryTraversalRects(a); // noHintBody intentionally never has this local contour.
            var noise = Capture(true); int noiseFrame = 1000;
            var hinted = new List<double>(); var noHint = new List<double>();
            for (int pair = -2; pair < 32; pair++)
            for (int pass = 0; pass < 2; pass++)
            {
                bool useHint = ((pair + 2) % 2 == 0) == (pass == 0);
                var body = useHint ? hintBody : noHintBody;
                body.ApplyCollision(b); EvictShared(noise, ref noiseFrame);
                long beforeBake = PaperCapture.PoseBakeCount, beforeHandoff = PaperCapture.LocalContourHandoffHits;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                body.ApplyCollision(a);
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(beforeBake + (useHint ? 0 : 1)));
                Assert.That(PaperCapture.LocalContourHandoffHits, Is.EqualTo(beforeHandoff + (useHint ? 1 : 0)));
                if (pair >= 0) (useHint ? hinted : noHint).Add(elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
            Assert.That(hinted.Count, Is.EqualTo(32)); Assert.That(noHint.Count, Is.EqualTo(32));
            // Full raw ordered vectors preserve paired order and expose outliers.
            TestContext.WriteLine("hint_ms=" + string.Join(",", hinted.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            TestContext.WriteLine("no_hint_ms=" + string.Join(",", noHint.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            TestContext.WriteLine("Both include HitMesh restore/build and the ordinary PhysX collider publication; none includes Render.");
        }
    }
}
#endif
