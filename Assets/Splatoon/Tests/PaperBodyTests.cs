#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class PaperBodyTests
    {
        public static readonly string[] Heroes={"RifleGirl","DualPistolGirl","ShotgunGirl","PistolGirl","RocketLauncherGirl"};
        public static readonly string[] ContourHeroes = Heroes.Concat(new[] { "SplooshGirl", "MachineGunGirl", "BubbleGirl" }).ToArray();
        public static PaperBodyProfile Profile(string hero) => AssetDatabase.LoadAssetAtPath<PaperBodyProfile>($"Assets/GameResource/Characters/Shared/Paper/{hero}/Paper.asset");
        [OneTimeSetUp] public void ClearPreviousTestPreviews()
        {
            ReleaseTestBodies();
            // Edit Mode does not run SwimBody's normal lifecycle callbacks.
            // Close only orphaned paper stages left by earlier interrupted tests.
            var scenes=Resources.FindObjectsOfTypeAll<PaperCaptureRig>()
                .Where(r=>r.transform.root.name=="Paper capture stage" && EditorSceneManager.IsPreviewScene(r.gameObject.scene))
                .Select(r=>r.gameObject.scene).Distinct().ToArray();
            foreach(var scene in scenes) EditorSceneManager.ClosePreviewScene(scene);
        }
        [SetUp] public void Setup() { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single); HeroMigrationTests.Load(); }
        [TearDown] public void Cleanup() { ReleaseTestBodies(); LubanConfigService.Current.Reset(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single); }
        public static void ReleaseTestBody(SwimBody body) => typeof(SwimBody)
            .GetMethod("ReleaseCapture",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(body,null);
        public static void ReleaseTestBodies()
        {
            foreach(var body in Object.FindObjectsByType<SwimBody>(FindObjectsInactive.Include,FindObjectsSortMode.None)) ReleaseTestBody(body);
        }
        static Texture2D Read(RenderTexture rt)
        {
            var old=RenderTexture.active; RenderTexture.active=rt;
            var texture=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
            texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); texture.Apply(); RenderTexture.active=old; return texture;
        }
        [TestCaseSource(nameof(Heroes))] public void LiveAnimationChangesPixelsAndHitPoseAndReplaysExactly(string hero)
        {
            var p=Profile(hero); Assert.That(p.CapturePrefab,Is.Not.Null); Assert.That(p.ContentHash.Length,Is.EqualTo(64));
            Assert.That(p.CapturePrefab.GetComponentsInChildren<MonoBehaviour>(true).All(c=>c is PaperCaptureRig),Is.True);
            Assert.That(p.CapturePrefab.GetComponent<PaperCaptureRig>().Nozzle,Is.Not.Null);
            Assert.That(p.CapturePrefab.GetComponentsInChildren<MeshFilter>().All(f=>f.sharedMesh.isReadable),Is.True,"weapon mesh is readable in a player build");
            Assert.That(p.CapturePrefab.GetComponentsInChildren<Collider>(true),Is.Empty);
            Assert.That(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(p)).Any(path=>path.EndsWith("/Paper.png")),Is.False);
            using var capture=new PaperCapture(p);
            var state=new PlayerSnapshot { PaperMove=Vector2.up,PaperAnimationTime=.13f };
            capture.Sample(state); capture.Render(); var first=capture.HitMesh.vertices;
            var image=Read(capture.Texture); var pixels=image.GetPixels32();
            Assert.That(capture.Texture.width,Is.EqualTo(512)); Assert.That(capture.Texture.height,Is.EqualTo(1024));
            Assert.That(capture.HitRects.Count,Is.GreaterThan(4)); Assert.That(capture.HitMesh.bounds.size.z,Is.EqualTo(.04f).Within(.00001));
            Assert.That(pixels.Count(c=>c.a>127),Is.InRange(10000,400000));
            int visible=0,extra=0,missing=0;
            for(int y=0;y<128;y++) for(int x=0;x<64;x++)
            {
                Vector2 uv=new((x+.5f)/64,(y+.5f)/128);
                bool drawn=image.GetPixelBilinear(uv.x,uv.y).a>=.5f;
                bool hit=capture.HitRects.Any(r=>r.Contains(new Vector2((uv.x-.5f)*p.Size.x,(uv.y-.5f)*p.Size.y)));
                if(drawn) visible++; if(hit&&!drawn) extra++; if(drawn&&!hit) missing++;
            }
            Assert.That(extra/(float)visible,Is.LessThan(.06f),"silhouette false positives");
            Assert.That(missing/(float)visible,Is.LessThan(.06f),"silhouette missing body");
            Directory.CreateDirectory("Reports/PaperBody/LiveCapture");
            File.WriteAllBytes("Reports/PaperBody/LiveCapture/"+hero+"-a.png",image.EncodeToPNG()); Object.DestroyImmediate(image);
            state.PaperAnimationTime=.63f; capture.Sample(state); capture.Render(); image=Read(capture.Texture);
            var second=capture.HitMesh.vertices;
            Assert.That(second.SequenceEqual(first),Is.False,"animated hit outline");
            Assert.That(image.GetPixels32().Where((pixel,i)=>!pixel.Equals(pixels[i])).Count(),Is.GreaterThan(1000),"live pixels change");
            File.WriteAllBytes("Reports/PaperBody/LiveCapture/"+hero+"-b.png",image.EncodeToPNG()); Object.DestroyImmediate(image);
            state.PaperAnimationTime=.13f; capture.Sample(state);
            Assert.That(capture.HitMesh.vertices,Is.EqualTo(first),"correction replay");
            using var peer=new PaperCapture(p); peer.Sample(state);
            Assert.That(peer.HitMesh.vertices,Is.EqualTo(first),"independent capture agrees");
            Assert.That(peer.HitMesh,Is.Not.SameAs(capture.HitMesh));
        }
        public static Vector2 BodyPoint(SwimBody body) => body.HitRects.OrderByDescending(r=>r.width*r.height).First().center;
        static void SameCapturedShape(PaperCapture expected, PaperCapture actual)
        {
            CollectionAssert.AreEqual(expected.HitRects, actual.HitRects, "explicit contour rectangles");
            CollectionAssert.AreEqual(expected.HitMesh.vertices, actual.HitMesh.vertices, "independent hit vertices");
            CollectionAssert.AreEqual(expected.HitMesh.triangles, actual.HitMesh.triangles, "independent hit triangles");
            Assert.That(actual.HitMesh.bounds, Is.EqualTo(expected.HitMesh.bounds));
            Assert.That(actual.PosedBounds, Is.EqualTo(expected.PosedBounds));
            foreach (var point in new[] { Vector3.zero, new Vector3(-.4f, .7f, .2f), new Vector3(.6f, -.8f, -.1f) })
                Assert.That(actual.ClosestPoint(point), Is.EqualTo(expected.ClosestPoint(point)), "closest hit point");
        }
        [TestCaseSource(nameof(ContourHeroes))]
        public void ContourOnlyQueryMatchesUncachedFullSamplesWithoutHitMeshOrRender(string hero)
        {
            using var contours = PaperCapture.CreateContourEvaluator(Profile(hero));
            using var live = new PaperCapture(Profile(hero));
            var states = new[]
            {
                new PlayerSnapshot { PaperAnimationTime = .101f, PaperMove = new Vector2(.2f, -.2f) },
                new PlayerSnapshot { PaperAnimationTime = .108f, PaperMove = new Vector2(.24f, -.24f) },
                new PlayerSnapshot { PaperAnimationTime = .63f, PaperMove = new Vector2(-.751f, .629f) },
                new PlayerSnapshot { PaperAnimationTime = 1023.99f, PaperMove = Vector2.right },
                new PlayerSnapshot { PaperAnimationTime = 0, PaperMove = Vector2.zero }
            };
            foreach (var state in states)
            {
                var rects = contours.QueryContour(state);
                using var oracle = new PaperCapture(Profile(hero)); oracle.SampleUncachedForDiagnostics(state);
                CollectionAssert.AreEqual(oracle.HitRects, rects, "same explicit frame, eighth-move and contour order");
                live.Sample(state); SameCapturedShape(oracle, live);
                Assert.That(contours.HitMesh, Is.Null, "candidate queries never construct a hit Mesh");
                Assert.That(contours.PoseVersion, Is.Zero, "candidate queries never publish a live pose");
                contours.Render(); Assert.That(contours.Texture, Is.Null, "candidate evaluators never allocate a render texture");
            }
        }
        [Test]
        public void ContourValuesAreSharedReadOnlyAndSurviveOtherSamplesAndCaptureDisposal()
        {
            IReadOnlyList<Rect> retained;
            Rect[] frozen;
            using (var producer = PaperCapture.CreateContourEvaluator(Profile("RifleGirl")))
            using (var peer = PaperCapture.CreateContourEvaluator(Profile("RifleGirl")))
            {
                var state = new PlayerSnapshot { PaperAnimationTime = .137f, PaperMove = Vector2.up };
                retained = producer.QueryContour(state); frozen = retained.ToArray();
                long bakes = PaperCapture.PoseBakeCount;
                Assert.That(peer.QueryContour(state), Is.SameAs(retained), "independent captures retain one immutable snapshot");
                Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(bakes), "a shared contour needs no second pose bake");
                var list = retained as IList<Rect>;
                Assert.That(list, Is.Not.Null); Assert.That(list.IsReadOnly, Is.True);
                Assert.Throws<NotSupportedException>(() => list[0] = default);
                var later = state; later.PaperAnimationTime = .737f; later.PaperMove = Vector2.left;
                producer.QueryContour(later); peer.QueryContour(later);
                CollectionAssert.AreEqual(frozen, retained, "a later projection cannot mutate a retained contour");
            }
            CollectionAssert.AreEqual(frozen, retained, "capture disposal/cache clear cannot mutate retained managed values");
        }
        [TestCase("RifleGirl")]
        [TestCase("ShotgunGirl")]
        public void SharedPoseCacheMatchesFreshUncachedShapesAndFinalRenderedReplay(string hero)
        {
            var profile = Profile(hero);
            using var contours = PaperCapture.CreateContourEvaluator(profile);
            using var warm = new PaperCapture(profile);
            using var live = new PaperCapture(profile);
            var random = new System.Random(72417);
            var states = Enumerable.Range(0, 8).Select(_ => new PlayerSnapshot
            {
                PaperAnimationTime = (float)(random.NextDouble() * 1000),
                PaperMove = new Vector2((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1))
            }).ToArray();
            // Nonmonotonic correction/replay order exercises an older cached
            // pose after several later poses, across independent capture stages.
            foreach (int index in new[] { 5, 1, 7, 0, 6, 2, 4, 3, 1, 5 })
            {
                var state = states[index]; var rects = contours.QueryContour(state); warm.Sample(state);
                long hits = PaperCapture.SharedPoseCacheHits, bakes = PaperCapture.PoseBakeCount;
                Assert.That(live.Sample(state), Is.True);
                Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(hits + 1));
                Assert.That(PaperCapture.PoseBakeCount, Is.EqualTo(bakes), "a shared contour needs no repeated skin bake");
                using var fresh = new PaperCapture(profile); fresh.SampleUncachedForDiagnostics(state);
                SameCapturedShape(fresh, live);
                CollectionAssert.AreEqual(rects, live.HitRects, "contour-only cache entries restore the original live hit geometry");
                Assert.That(live.HitMesh, Is.Not.SameAs(warm.HitMesh));
            }
            var final = states[5];
            using var reference = new PaperCapture(profile); reference.SampleUncachedForDiagnostics(final);
            long renderBakes = PaperCapture.DeferredRenderBakeCount;
            live.Render(); reference.Render();
            Assert.That(PaperCapture.DeferredRenderBakeCount, Is.EqualTo(renderBakes + 1), "only the final displayed explicit key is baked");
            var actualImage = Read(live.Texture); var expectedImage = Read(reference.Texture);
            try { CollectionAssert.AreEqual(expectedImage.GetPixels32(), actualImage.GetPixels32(), "cache hits render the final simulated pose"); }
            finally { Object.DestroyImmediate(actualImage); Object.DestroyImmediate(expectedImage); }
            SameCapturedShape(reference, live);
        }
        [Test]
        public void SharedPoseCacheInvalidatesShapeAndContentAndRetainsEvictedValues()
        {
            var profile = Object.Instantiate(Profile("RifleGirl"));
            try
            {
                using var producer = new PaperCapture(profile);
                using var consumer = new PaperCapture(profile);
                using var contours = PaperCapture.CreateContourEvaluator(profile);
                var state = new PlayerSnapshot { PaperAnimationTime = .13f, PaperMove = new Vector2(.2f, -.2f) };
                producer.Sample(state); consumer.Sample(state);
                var retainedContour = contours.QueryContour(state); var retainedContourValues = retainedContour.ToArray();
                Action verifyChangedShape = () =>
                {
                    long misses = PaperCapture.SharedPoseCacheMisses;
                    Assert.That(producer.Sample(state), Is.True, "shape/content changes invalidate even an unchanged animation key");
                    Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1));
                    using var fresh = new PaperCapture(profile); fresh.SampleUncachedForDiagnostics(state);
                    SameCapturedShape(fresh, producer);
                    long hits = PaperCapture.SharedPoseCacheHits;
                    consumer.Sample(state);
                    Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(hits + 1));
                    SameCapturedShape(fresh, consumer);
                };
                profile.CaptureCenter += new Vector3(.01f, -.02f, 0); verifyChangedShape();
                profile.CaptureHeight += .04f; verifyChangedShape();
                profile.Size += new Vector2(.02f, .03f); verifyChangedShape();
                profile.Thickness += .01f; verifyChangedShape();
                profile.ContentHash += "-changed";
                // A fresh clone has a different captured content identity;
                // warm it separately after verifying the older clone's miss.
                long misses = PaperCapture.SharedPoseCacheMisses;
                producer.Sample(state); consumer.Sample(state);
                Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1));
                using (var fresh = new PaperCapture(profile))
                { fresh.SampleUncachedForDiagnostics(state); SameCapturedShape(fresh, producer); SameCapturedShape(fresh, consumer); }
                var originalPrefab = profile.CapturePrefab;
                profile.CapturePrefab = Profile("ShotgunGirl").CapturePrefab;
                misses = PaperCapture.SharedPoseCacheMisses; producer.Sample(state);
                Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1), "current prefab invalidates the old clone's same-key sample");
                using (var rebound = new PaperCapture(profile))
                using (var peer = new PaperCapture(profile))
                using (var oracle = new PaperCapture(profile))
                {
                    misses = PaperCapture.SharedPoseCacheMisses; rebound.Sample(state);
                    Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1), "an old clone cannot populate a newly rebound rig's cache");
                    long hits = PaperCapture.SharedPoseCacheHits; peer.Sample(state);
                    Assert.That(PaperCapture.SharedPoseCacheHits, Is.EqualTo(hits + 1));
                    oracle.SampleUncachedForDiagnostics(state); SameCapturedShape(oracle, peer);
                }
                profile.CapturePrefab = originalPrefab;
                using var otherProfile = new PaperCapture(Profile("ShotgunGirl"));
                misses = PaperCapture.SharedPoseCacheMisses; otherProfile.Sample(state);
                Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1), "hero and rig identity cannot alias");
                // Eviction may drop cache ownership, never mutate a retained
                // capture's contour/mesh or turn future misses into stale hits.
                var finalRects = consumer.HitRects.ToArray(); var finalVertices = consumer.HitMesh.vertices;
                for (int i = 0; i < 270; i++)
                { var later = state; later.PaperAnimationTime = (i + 70.25f) / PaperAnimation.FramesPerSecond; producer.Sample(later); }
                CollectionAssert.AreEqual(finalRects, consumer.HitRects);
                CollectionAssert.AreEqual(finalVertices, consumer.HitMesh.vertices);
                CollectionAssert.AreEqual(retainedContourValues, retainedContour, "global FIFO eviction cannot mutate a retained immutable contour");
                misses = PaperCapture.SharedPoseCacheMisses; producer.Sample(state);
                Assert.That(PaperCapture.SharedPoseCacheMisses, Is.EqualTo(misses + 1), "bounded cache recomputes an evicted exact key");
            }
            finally { Object.DestroyImmediate(profile); }
        }
        [TestCaseSource(nameof(Heroes))] public void HeldWeaponIdleAndMovementMatchTheStandingView(string hero)
        {
            var source=AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{hero}/Prefabs/{hero}Visual.prefab");
            var weapon=AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/GameResource/Weapons/"+hero+"/Prefabs"})
                .Select(g=>AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
                .Single(go=>go.GetComponent<HeroWeaponBindings>() != null);
            int id=Array.IndexOf(Heroes,hero)+1;
            var root=new GameObject("standing reference");
            using var binder=new HeroViewBinder(root.transform);
            binder.Apply(new HeroContent(GameplayConfig.GetHero(id),source,weapon));
            var captured=Object.Instantiate(Profile(hero).CapturePrefab); var rig=captured.GetComponent<PaperCaptureRig>();
            try
            {
                var view=binder.View;
                Assert.That(rig.Animator.runtimeAnimatorController,Is.SameAs(view.Animator.runtimeAnimatorController));
                foreach(var move in new[]{Vector2.zero,Vector2.up,Vector2.down,Vector2.left,Vector2.right})
                {
                    var s=new PlayerSnapshot { HeroId=id,Health=100,Grounded=true,Revision=1,Velocity=new Vector3(move.x,0,move.y)*5 };
                    view.Present(s,1,1);
                    view.Animator.SetFloat("MoveX",move.x); view.Animator.SetFloat("MoveY",move.y); view.Animator.SetFloat("MovePlayback",1);
                    view.Animator.Play("Base Layer.Locomotion",0,0); view.Animator.Update(0);
                    float duration=view.Animator.GetCurrentAnimatorStateInfo(0).length;
                    view.Animator.Play("Base Layer.Locomotion",0,.37f/duration); view.Animator.Update(0); view.ApplyAim();
                    rig.Evaluate(.37f,move);
                    foreach(var bone in new[]{HumanBodyBones.Head,HumanBodyBones.LeftHand,HumanBodyBones.RightHand,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot})
                    {
                        var expected=view.transform.InverseTransformPoint(view.Animator.GetBoneTransform(bone).position);
                        var actual=rig.transform.InverseTransformPoint(rig.Animator.GetBoneTransform(bone).position);
                        Assert.That(Vector3.Distance(expected,actual),Is.LessThan(.002f),hero+" "+move+" "+bone);
                    }
                }
            }
            finally { Object.DestroyImmediate(captured); binder.Dispose(); Object.DestroyImmediate(root); }
        }
        [Test] public void LiveCaptureBoundsAndCostAcrossAnimationCycle()
        {
            Directory.CreateDirectory("Reports/PaperBody");
            using var report=new StreamWriter("Reports/PaperBody/live-cost.txt");
            foreach (var hero in Heroes)
            {
                var profile=Profile(hero); using var capture=new PaperCapture(profile);
                var state=new PlayerSnapshot { PaperMove=Vector2.up };
                capture.Sample(state); capture.Render();
                var timer=new System.Diagnostics.Stopwatch(); double sample=0,render=0;
                for(int i=1;i<=30;i++)
                {
                    state.PaperAnimationTime=i/30f;
                    timer.Restart(); capture.Sample(state); timer.Stop(); sample+=timer.Elapsed.TotalMilliseconds;
                    timer.Restart(); capture.Render(); timer.Stop(); render+=timer.Elapsed.TotalMilliseconds;
                    var b=capture.PosedBounds; var center=profile.CaptureCenter;
                    Assert.That(b.min.x,Is.GreaterThan(center.x-profile.CaptureHeight/4));
                    Assert.That(b.max.x,Is.LessThan(center.x+profile.CaptureHeight/4));
                    Assert.That(b.min.y,Is.GreaterThan(center.y-profile.CaptureHeight/2));
                    Assert.That(b.max.y,Is.LessThan(center.y+profile.CaptureHeight/2));
                }
                report.WriteLine($"{hero}: CPU pose + silhouette {sample/30:F3} ms; render request CPU {render/30:F3} ms; {capture.HitMesh.vertexCount} hit vertices. Editor warm sample, not GPU or release-frame timing.");
            }
        }
        public static Vector2 LimbGap(SwimBody body)
        {
            for(float y=-.6f;y<.25f;y+=.014f)
            {
                var spans=body.HitRects.Where(r=>y>r.yMin && y<r.yMax).OrderBy(r=>r.xMin).ToArray();
                for(int i=1;i<spans.Length;i++) if(spans[i].xMin-spans[i-1].xMax>.045f) return new Vector2((spans[i].xMin+spans[i-1].xMax)/2,y);
            }
            throw new InvalidOperationException("No visible limb gap in "+body.Profile.name);
        }
        [TestCaseSource(nameof(Heroes))] public void FrontBackSideAndLimbGapUsePaperRatherThanRectangle(string hero)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab");
            var body=Object.Instantiate(prefab).GetComponent<SwimBody>(); body.Bind(Profile(hero));
            var state=new PlayerSnapshot { Health=100,Swimming=true,SwimSource=SwimSurface.Friendly,PaperPose=PaperPose.Wall,PaperCenter=new Vector3(0,2,0),PaperRotation=Quaternion.identity };
            body.ApplyCollision(state); Physics.SyncTransforms();
            Vector2 center=BodyPoint(body),gap=LimbGap(body);
            var hitPoint=state.PaperCenter+new Vector3(center.x,center.y,0); var gapPoint=state.PaperCenter+new Vector3(gap.x,gap.y,0);
            var aim=new TpsAimSolver();
            foreach(var normal in new[]{Vector3.forward,Vector3.back})
            {
                Assert.That(aim.ClosestCast(hitPoint+normal,-normal,2,0,ulong.MaxValue,out var hit),Is.True); Assert.That(hit.Collider,Is.SameAs(body.HitVolume));
                Assert.That(aim.ClosestCast(gapPoint+normal,-normal,2,.003f,ulong.MaxValue,out _),Is.False,"limb gap");
            }
            Assert.That(aim.Overlap(hitPoint,.025f,ulong.MaxValue,Vector3.forward,out var overlap),Is.True);
            Assert.That(overlap.Collider,Is.SameAs(body.HitVolume));
            Assert.That(aim.ClosestCast(hitPoint+Vector3.right,Vector3.left,2,0,ulong.MaxValue,out _),Is.True,"thin sheet still has side thickness");
            var outside=hitPoint+Vector3.forward*.06f;
            Assert.That(aim.ClosestCast(outside+Vector3.right,Vector3.left,2,.005f,ulong.MaxValue,out _),Is.False);
            Assert.That(aim.ClosestCast(outside+Vector3.right,Vector3.left,2,.06f,ulong.MaxValue,out _),Is.True,"projectile radius is preserved");
            body.Present(state,Vector3.right*.2f,Quaternion.Euler(0,90,0));
            Assert.That(body.HitVolume.transform.position,Is.EqualTo(state.PaperCenter));
            Assert.That(Quaternion.Angle(body.BodyRenderer.transform.rotation,state.PaperRotation),Is.LessThan(.01));
            state.Health=0; body.ApplyCollision(state); body.Present(state,Vector3.zero,Quaternion.identity);
            Assert.That(body.UsesHitProxy || body.BodyRenderer.enabled,Is.False);
        }
        [Test] public void WallOccludesPaperAndCameraCannotCrossIt()
        {
            var wall=new GameObject("wall"); wall.AddComponent<BoxCollider>().size=new Vector3(5,5,.25f);
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab");
            var body=Object.Instantiate(prefab).GetComponent<SwimBody>();
            var s=new PlayerSnapshot { Health=100,Swimming=true,PaperPose=PaperPose.Wall,PaperCenter=new Vector3(0,0,.17f),PaperRotation=Quaternion.identity };
            body.ApplyCollision(s); Physics.SyncTransforms(); var point=BodyPoint(body);
            var aim=new TpsAimSolver();
            Assert.That(aim.ClosestCast(new Vector3(point.x,point.y,-2),Vector3.forward,4,.01f,ulong.MaxValue,out var hit),Is.True);
            Assert.That(hit.Collider,Is.SameAs(wall.GetComponent<BoxCollider>()));
            var camera=PrototypePlayer.CameraPosition(new Vector3(0,0,.5f),Quaternion.identity);
            Assert.That(camera.z,Is.GreaterThan(.125f));
        }
        [Test] public void AirFlattensWallFrameAndReplaysExactly()
        {
            var s=new PlayerSnapshot { HeroId=1,Health=100,Swimming=true,Movement=MovementMode.WallInk,WallNormal=Vector3.right,WallPoint=Vector3.zero,Position=new Vector3(.36f,1,0) };
            PaperPoseSimulation.Resolve(ref s,default,Profile("RifleGirl"),1); var before=s;
            s.Movement=MovementMode.Air; s.Position+=new Vector3(.2f,.1f,.3f); s.Yaw=175;
            s.Position.y=before.PaperCenter.y+.1f-Profile("RifleGirl").SurfaceOffset;
            PaperPoseSimulation.Resolve(ref s,before,Profile("RifleGirl"),2);
            Assert.That(Quaternion.Angle(s.PaperRotation,PaperPoseSimulation.GroundRotation(Vector3.up,s.Yaw)),Is.LessThan(.001));
            Assert.That(Vector3.Distance(s.PaperCenter-before.PaperCenter,new Vector3(.2f,.1f,.3f)),Is.LessThan(.001));
            var expected=s; s=before; s.Movement=MovementMode.Air; s.Position=expected.Position; s.Yaw=175;
            PaperPoseSimulation.Resolve(ref s,before,Profile("RifleGirl"),2);
            Assert.That(JsonUtility.ToJson(s),Is.EqualTo(JsonUtility.ToJson(expected)));
        }
        [Test] public void HitProxiesAreQueryableWithoutPhysicalContacts()
        {
            Assert.That(LayerMask.LayerToName(SwimBody.HitProxyLayer),Is.EqualTo("PlayerHitProxy"));
            for(int layer=0;layer<32;layer++) Assert.That(Physics.GetIgnoreLayerCollision(SwimBody.HitProxyLayer,layer),Is.True,"layer "+layer);
            Assert.That(Physics.GetIgnoreLayerCollision(8,8),Is.False,"player motors still collide");
            Assert.That(PlayerMotorSimulation.WorldMask & ((1<<8)|(1<<SwimBody.HitProxyLayer)),Is.Zero);
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab");
            var body=Object.Instantiate(prefab).GetComponent<SwimBody>();
            Assert.That(body.HitVolume.gameObject.layer,Is.EqualTo(SwimBody.HitProxyLayer));
            Assert.That(body.CapsuleHitVolume.gameObject.layer,Is.EqualTo(SwimBody.HitProxyLayer));
            Assert.That(body.HitVolume.convex || body.HitVolume.isTrigger,Is.False);
            var s=new PlayerSnapshot { Health=100,Swimming=true,PaperPose=PaperPose.Wall,PaperCenter=new Vector3(0,1,0),PaperRotation=Quaternion.identity };
            body.ApplyCollision(s); Physics.SyncTransforms();
            var point=BodyPoint(body); Vector3 target=s.PaperCenter+new Vector3(point.x,point.y,0);
            var go=new GameObject("crossing motor") { layer=8 }; var cc=go.AddComponent<CharacterController>();
            cc.height=.7f; cc.center=Vector3.up*.35f; cc.radius=.35f; cc.skinWidth=.03f;
            Vector3 start=target-Vector3.forward-Vector3.up*.35f;
            Vector3 Run(bool enabled)
            {
                cc.enabled=false; go.transform.position=start; cc.enabled=true;
                body.HitVolume.enabled=enabled; Physics.SyncTransforms();
                for(int i=0;i<40;i++) cc.Move(Vector3.forward*.05f);
                return go.transform.position;
            }
            var withHit=Run(true); var withoutHit=Run(false);
            Assert.That(Vector3.Distance(withHit,withoutHit),Is.LessThan(.0001f));
            Assert.That(withHit.z,Is.GreaterThan(.9f));
        }
        [TestCaseSource(nameof(Heroes))] public void NewAirPaperOpensHorizontallyAndKeepsItsPlaneUntilNextSwitch(string hero)
        {
            var profile=Profile(hero);
            var body=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/Shared/Swim/SwimBody.prefab")).GetComponent<SwimBody>();
            body.Bind(profile);
            var s=new PlayerSnapshot { Health=100,Position=new Vector3(0,10,0),Movement=MovementMode.Air,Yaw=65,Pitch=70 };
            foreach(float yaw in new[]{65f,210f})
            {
                var human=s; s.Swimming=true; s.Yaw=yaw; PaperPoseSimulation.Resolve(ref s,human,profile,1);
                Assert.That(Quaternion.Angle(s.PaperRotation,PaperPoseSimulation.GroundRotation(Vector3.up,yaw)),Is.LessThan(.001f));
                Assert.That(s.PaperCenter,Is.EqualTo(s.Position+Vector3.up*profile.SurfaceOffset));
                var opened=s; s.Position+=new Vector3(.2f,-.1f,.3f); s.Yaw+=95;
                PaperPoseSimulation.Resolve(ref s,opened,profile,2);
                Assert.That(Quaternion.Angle(s.PaperRotation,opened.PaperRotation),Is.LessThan(.001f));
                Assert.That(Vector3.Distance(s.PaperCenter-opened.PaperCenter,s.Position-opened.Position),Is.LessThan(.0001f));
                body.ApplyCollision(s); body.Present(s,Vector3.zero,Quaternion.identity);
                Assert.That(body.HitVolume.transform.position,Is.EqualTo(s.PaperCenter));
                Assert.That(Quaternion.Angle(body.BodyRenderer.transform.rotation,s.PaperRotation),Is.LessThan(.001f));
                var before=s; s.Swimming=false; PaperPoseSimulation.Resolve(ref s,before,profile,3);
                Assert.That(s.PaperPose,Is.EqualTo(PaperPose.None));
            }
        }
    }
}
#endif
