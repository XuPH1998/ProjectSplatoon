#if UNITY_EDITOR
// Reports-only acceptance candidate. Not compiled or executed by its author.
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // These callbacks are invoked by Unity's actual player loop. Tests never
    // invoke them, alter Time/frameCount, or synthesize a diagnostic event.
    [DefaultExecutionOrder(30000)]
    internal sealed class PaperSamplingFrameClock65Driver : MonoBehaviour
    {
        internal bool Producing, TouchOnly, Witness, ZeroObserved, AgeEightObserved;
        internal SwimBody Body;
        internal string Failure;
        internal int Attempts, FirstTouchFrame = -1, LastTouchFrame = -1;
        internal uint LastTouchGeneration;
        internal int LateFrame = -1, FixedFrame = -1, ReadAtFrame = -1;
        internal int LateOrder, RenderOrder, FixedOrder, ReadOrder;
        internal int SealedPoseVersion, FixedPoseVersion, SealedRenderedVersion;
        internal bool TextureCreated;
        internal BlockFixedGridAcceptanceProbe.FrameRecord PreviousRow;
        internal PaperSamplingDiagnostics.FrameSnapshot BeforeRender, Sealed, Completed, CurrentAtRead;
        internal PaperSamplingDiagnostics.FrameSnapshot ZeroFrame, AgeSeven, AgeEight;
        internal PaperSamplingDiagnostics.Counts CumulativeAtRead;
        int _order, _pose = 10000;
        bool _pending, _fixedObserved;
        static readonly FieldInfo RenderedVersion = typeof(PaperCapture).GetField(
            "_renderedVersion", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static PlayerSnapshot Pose(int frame) => new PlayerSnapshot
        {
            HeroId = 1, HeroRevision = 1, Team = 1, Health = 100,
            LifeState = PlayerLifeState.Alive, Swimming = true, PaperPose = PaperPose.Ground,
            PaperRotation = Quaternion.identity,
            PaperAnimationTime = (frame + .25f) / PaperAnimation.FramesPerSecond,
            PaperMove = new Vector2(.25f, -.25f)
        };

        void LateUpdate()
        {
            if (!Producing || Witness || Failure != null) return;
            try
            {
                PaperSamplingDiagnostics.TouchFrame();
                LastTouchFrame = Time.frameCount;
                LastTouchGeneration = PaperSamplingDiagnostics.Generation;
                if (FirstTouchFrame < 0) FirstTouchFrame = LastTouchFrame;
                if (TouchOnly)
                {
                    if (LastTouchFrame == FirstTouchFrame + 7)
                        AgeSeven = PaperSamplingDiagnostics.ReadFrame(FirstTouchFrame);
                    if (LastTouchFrame == FirstTouchFrame + 8)
                    {
                        AgeEight = PaperSamplingDiagnostics.ReadFrame(FirstTouchFrame);
                        AgeEightObserved = true;
                    }
                    return;
                }
                Attempts++;
                LateFrame = Time.frameCount; LateOrder = ++_order;
                var state = Pose(_pose++);
                // Real candidate computation supplies a shared contour. The live
                // capture consumes it through ApplyCollision, so its own stage
                // still requires the original deferred BakePose inside Render.
                Body.QueryTraversalRects(state);
                Body.ApplyCollision(state);
                BeforeRender = PaperSamplingDiagnostics.ReadFrame(LateFrame);
                Body.Present(state, Vector3.zero, Quaternion.identity);
                RenderOrder = ++_order;
                SealedPoseVersion = Body.Capture.PoseVersion;
                SealedRenderedVersion = (int)RenderedVersion.GetValue(Body.Capture);
                TextureCreated = Body.Capture.Texture != null && Body.Capture.Texture.IsCreated();
                PreviousRow = new BlockFixedGridAcceptanceProbe.FrameRecord { frameId = LateFrame };
                Sealed = PaperSamplingDiagnostics.ReadFrame(PreviousRow.frameId);
                _pending = true; _fixedObserved = false;
            }
            catch (Exception error) { Failure = error.ToString(); Producing = false; }
        }

        void FixedUpdate()
        {
            if (!Producing || TouchOnly || !_pending || _fixedObserved || Failure != null) return;
            // Some application frames have no FixedUpdate. Only a naturally
            // occurring immediately-next-frame witness can satisfy this case.
            if (Time.frameCount != PreviousRow.frameId + 1) return;
            try
            {
                Body.ApplyCollision(Pose(_pose++)); // New exact key; real live sample, no Present.
                FixedFrame = Time.frameCount; FixedOrder = ++_order;
                FixedPoseVersion = Body.Capture.PoseVersion;
                _fixedObserved = true;
            }
            catch (Exception error) { Failure = error.ToString(); Producing = false; }
        }

        void Update()
        {
            if (!Producing || Witness || Failure != null) return;
            try
            {
                if (TouchOnly)
                {
                    if (!ZeroObserved && FirstTouchFrame >= 0 && Time.frameCount == FirstTouchFrame + 1)
                    {
                        ZeroFrame = PaperSamplingDiagnostics.ReadFrame(FirstTouchFrame);
                        ZeroObserved = true;
                    }
                    return;
                }
                if (!_pending || !_fixedObserved || Time.frameCount != PreviousRow.frameId + 1) return;
                ReadAtFrame = Time.frameCount; ReadOrder = ++_order;
                // Exactly the production completed-row API: do not obtain an
                // old frame by subtracting process cumulative values now.
                Completed = PaperSamplingDiagnostics.ReadFrame(PreviousRow.frameId);
                CurrentAtRead = PaperSamplingDiagnostics.ReadFrame(Time.frameCount);
                CumulativeAtRead = PaperSamplingDiagnostics.ReadCumulative();
                Witness = true; Producing = false;
            }
            catch (Exception error) { Failure = error.ToString(); Producing = false; }
        }
    }

    [Category("PaperSamplingFrameClock65")]
    public sealed class PaperSamplingFrameClock65Tests
    {
        GameObject _root;
        PaperSamplingFrameClock65Driver _driver;
        bool _fixtureOwnsConfig;
        static readonly MethodInfo ResetHook = typeof(PaperSamplingDiagnostics).GetMethod(
            "Reset", BindingFlags.Static | BindingFlags.NonPublic);

        static void ResetActualSubsystemHook()
        {
            Assert.That(ResetHook, Is.Not.Null);
            var attribute = ResetHook.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.loadType, Is.EqualTo(RuntimeInitializeLoadType.SubsystemRegistration));
            ResetHook.Invoke(null, null); // Execute the original hook; never write private counters/buckets.
        }

        [UnitySetUp]
        public IEnumerator Setup()
        {
            var suppressed = typeof(PrototypeApp).GetProperty("SystemCursorWritesSuppressed",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(suppressed, Is.Not.Null);
            Assert.That((bool)suppressed.GetValue(null), Is.True,
                "Run serially with the existing no-system-cursor test runner.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _fixtureOwnsConfig = false;
            yield return WaitForBootstrapInitialization();
            _fixtureOwnsConfig = true;
            HeroMigrationTests.Load();
            Assert.That(Time.timeScale, Is.GreaterThan(0), "A normal advancing player loop is required; this test does not change its clock.");
            ResetActualSubsystemHook(); PaperSamplingDiagnostics.Enable();
            _root = new GameObject("Paper sampling real frame-clock fixture");
            _driver = _root.AddComponent<PaperSamplingFrameClock65Driver>();
            Assert.That(SwimBody.ActiveBodies.Count, Is.EqualTo(0), "The empty scene isolates the actual producer, not its implementation.");
        }

        static IEnumerator WaitForBootstrapInitialization()
        {
            // EnterPlayMode starts the real bootstrap even in an empty scene.
            // Fixture injection clears weapon handles, so first let the app
            // finish owning all of its asynchronous Addressables operations.
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypeApp.Current == null || PrototypeApp.Current.Busy)
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            var app = PrototypeApp.Current;
            string diagnostic = $"Real bootstrap before frame-clock fixture config injection: present={app != null}"
                + $" ready={app?.Ready} busy={app?.Busy} status={app?.Status} error={app?.Error}";
            Assert.That(app, Is.Not.Null, diagnostic);
            Assert.That(app.Busy, Is.False, diagnostic);
            Assert.That(app.Ready, Is.True, diagnostic);
            Assert.That(app.Error, Is.Empty, diagnostic);
            Assert.That(LubanConfigService.Current.IsReady, Is.True, diagnostic);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_driver != null) _driver.Producing = false;
            if (_driver != null && _driver.Body != null) PaperBodyTests.ReleaseTestBody(_driver.Body);
            if (_root != null) Object.DestroyImmediate(_root);
            ResetActualSubsystemHook();
            // A bootstrap timeout/failure never grants this fixture ownership
            // of its config handles. ExitPlayMode then cancels the real app.
            if (_fixtureOwnsConfig) LubanConfigService.Current.Reset();
            _fixtureOwnsConfig = false;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        SwimBody CreateRealBody()
        {
            var profile = PaperBodyTests.Profile("RifleGirl");
            Assert.That(profile, Is.Not.Null); Assert.That(profile.CapturePrefab, Is.Not.Null);
            var bodyRoot = new GameObject("Real SwimBody without network/input scripts");
            bodyRoot.SetActive(false); bodyRoot.transform.SetParent(_root.transform, false);
            var display = new GameObject("Display", typeof(MeshFilter), typeof(MeshRenderer));
            display.transform.SetParent(bodyRoot.transform, false);
            var hit = new GameObject("Live hit mesh", typeof(MeshCollider));
            hit.transform.SetParent(bodyRoot.transform, false);
            var capsule = bodyRoot.AddComponent<CapsuleCollider>();
            var body = bodyRoot.AddComponent<SwimBody>();
            body.Profile = profile; body.BodyRenderer = display.GetComponent<MeshRenderer>();
            body.HitVolume = hit.GetComponent<MeshCollider>(); body.CapsuleHitVolume = capsule;
            bodyRoot.SetActive(true);
            Assert.That(body.Profile, Is.SameAs(profile), "Use the original authored profile, not a test replacement.");
            return body;
        }

        static IEnumerator WaitFor(PaperSamplingFrameClock65Driver driver, Func<bool> ready, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + 60;
            int first = Time.frameCount;
            while (!ready() && driver.Failure == null && Time.realtimeSinceStartupAsDouble < until && Time.frameCount - first < 600)
                yield return null;
            Assert.That(driver.Failure, Is.Null, label + ": real callback failure");
            Assert.That(ready(), Is.True, label + ": no natural callback witness within 600 frames/60 seconds; no clocks or events were synthesized.");
        }

        static void Available(PaperSamplingDiagnostics.FrameSnapshot sample, int frame, uint generation)
        {
            Assert.That(sample.schema, Is.EqualTo(PaperSamplingDiagnostics.Schema));
            Assert.That(sample.enabled, Is.True); Assert.That(sample.available, Is.True);
            Assert.That(sample.frameId, Is.EqualTo(frame)); Assert.That(sample.generation, Is.EqualTo(generation));
            Assert.That(sample.delta.HasValidPartitions(), Is.True);
            Assert.That(sample.cumulative.HasValidPartitions(), Is.True);
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator CompletedFrameRetainsRealLateRenderAndExcludesNextFramesRealFixedSample()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null),
                "The witness requires an actual successful Render; null graphics cannot pass.");
            _driver.Body = CreateRealBody(); _driver.Producing = true;
            yield return WaitFor(_driver, () => _driver.Witness, "LateUpdate Render -> next FixedUpdate sample -> Update read");
            uint generation = _driver.Sealed.generation;
            Assert.That(_driver.Attempts, Is.GreaterThan(0));
            Assert.That(_driver.PreviousRow.frameId, Is.EqualTo(_driver.LateFrame));
            Assert.That(_driver.FixedFrame, Is.EqualTo(_driver.PreviousRow.frameId + 1));
            Assert.That(_driver.ReadAtFrame, Is.EqualTo(_driver.FixedFrame));
            Assert.That(_driver.LateOrder, Is.LessThan(_driver.RenderOrder));
            Assert.That(_driver.RenderOrder, Is.LessThan(_driver.FixedOrder));
            Assert.That(_driver.FixedOrder, Is.LessThan(_driver.ReadOrder));
            Assert.That(_driver.TextureCreated, Is.True);
            Assert.That(_driver.SealedRenderedVersion, Is.EqualTo(_driver.SealedPoseVersion));
            Assert.That(_driver.FixedPoseVersion, Is.EqualTo(_driver.SealedPoseVersion + 1), "A new real live pose was sampled before the read.");
            Available(_driver.Sealed, _driver.PreviousRow.frameId, generation);
            Available(_driver.Completed, _driver.PreviousRow.frameId, generation);
            Available(_driver.CurrentAtRead, _driver.FixedFrame, generation);
            Assert.That(_driver.Sealed.delta.deferredRenderBake, Is.EqualTo(_driver.BeforeRender.delta.deferredRenderBake + 1),
                "The old frame includes the real deferred Render bake after its collision sample.");
            Assert.That(_driver.Completed.delta, Is.EqualTo(_driver.Sealed.delta));
            Assert.That(_driver.Completed.cumulative, Is.EqualTo(_driver.Sealed.cumulative), "A later FixedUpdate cannot mutate a completed frame's totals.");
            Assert.That(_driver.CurrentAtRead.delta, Is.Not.EqualTo(default(PaperSamplingDiagnostics.Counts)),
                "The next frame contains an observed event from its real new-key sample, whichever original cache branch it takes.");
            Assert.That(_driver.CurrentAtRead.delta.liveNoop, Is.EqualTo(0));
            Assert.That(_driver.CurrentAtRead.delta.traversalRequests, Is.EqualTo(0), "The prior frame's candidate request cannot leak into this bucket.");
            Assert.That(_driver.CurrentAtRead.delta.queryRealMiss, Is.EqualTo(0));
            Assert.That(_driver.CurrentAtRead.delta.deferredRenderBake, Is.EqualTo(0), "No Present occurred in that FixedUpdate.");
            Assert.That(_driver.CumulativeAtRead, Is.Not.EqualTo(_driver.Sealed.cumulative),
                "The current process cumulative really contains the new event, so rereading an old global total would be observably wrong.");
            Assert.That(_driver.CurrentAtRead.cumulative, Is.EqualTo(_driver.CumulativeAtRead));
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator ActualZeroEventLateUpdateIsAvailableAndOldBucketExpiresAtEightRealFrames()
        {
            _driver.TouchOnly = true; _driver.Producing = true;
            yield return WaitFor(_driver, () => _driver.ZeroObserved && _driver.AgeEightObserved, "Zero-event actual LateUpdate and eight-frame overwrite");
            uint generation = PaperSamplingDiagnostics.Generation;
            Available(_driver.ZeroFrame, _driver.FirstTouchFrame, generation);
            Assert.That(_driver.ZeroFrame.delta, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            Assert.That(_driver.ZeroFrame.cumulative, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            Available(_driver.AgeSeven, _driver.FirstTouchFrame, generation);
            Assert.That(_driver.AgeSeven.delta, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            Assert.That(_driver.AgeSeven.cumulative, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            Assert.That(_driver.AgeEight.available, Is.False, "The actual frame F+8 LateUpdate reused the physical eight-slot ring position.");
            Assert.That(_driver.AgeEight.enabled, Is.True);
            Assert.That(_driver.AgeEight.frameId, Is.EqualTo(_driver.FirstTouchFrame));
            Assert.That(PaperSamplingDiagnostics.ReadFrame(_driver.FirstTouchFrame).available, Is.False);
            var latest = PaperSamplingDiagnostics.ReadFrame(_driver.LastTouchFrame);
            Available(latest, _driver.LastTouchFrame, generation);
            Assert.That(latest.delta, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator OriginalSubsystemResetCannotResurrectOldDataWhenFrameAndSlotAreReused()
        {
            _driver.Body = CreateRealBody();
            _driver.Body.ApplyCollision(PaperSamplingFrameClock65Driver.Pose(80000 + Time.frameCount));
            var old = PaperSamplingDiagnostics.ReadFrame(Time.frameCount);
            Available(old, Time.frameCount, PaperSamplingDiagnostics.Generation);
            Assert.That(old.cumulative, Is.Not.EqualTo(default(PaperSamplingDiagnostics.Counts)),
                "Reset must clear a genuinely nonzero stream produced by the original sampler, without forcing its cache branch.");
            ResetActualSubsystemHook();
            uint next = PaperSamplingDiagnostics.Generation;
            Assert.That(next, Is.EqualTo(unchecked(old.generation + 1)));
            Assert.That(PaperSamplingDiagnostics.Enabled, Is.False);
            var disabled = PaperSamplingDiagnostics.ReadFrame(old.frameId);
            Assert.That(disabled.available, Is.False); Assert.That(disabled.enabled, Is.False);
            Assert.That(disabled.generation, Is.EqualTo(next));
            Assert.That(PaperSamplingDiagnostics.ReadCumulative(), Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            PaperSamplingDiagnostics.Enable();
            var enabled = PaperSamplingDiagnostics.ReadFrame(old.frameId);
            Assert.That(enabled.available, Is.False, "Re-enabling cannot resurrect a bucket from the prior generation.");
            Assert.That(enabled.enabled, Is.True); Assert.That(enabled.generation, Is.EqualTo(next));
            // Reuse the exact same real frame ID through the original API,
            // before yielding. A generation guard must defeat stale data even
            // when the bucket slot AND frame ID are identical to the old value.
            PaperSamplingDiagnostics.TouchFrame();
            var sameFrame = PaperSamplingDiagnostics.ReadFrame(old.frameId);
            Available(sameFrame, old.frameId, next);
            Assert.That(sameFrame.delta, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            Assert.That(sameFrame.cumulative, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            _driver.TouchOnly = true; _driver.Producing = true;
            yield return WaitFor(_driver, () => _driver.LastTouchGeneration == next, "Actual post-reset LateUpdate");
            var fresh = PaperSamplingDiagnostics.ReadFrame(_driver.LastTouchFrame);
            Available(fresh, _driver.LastTouchFrame, next);
            Assert.That(fresh.delta, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)));
            var oldIdNow = PaperSamplingDiagnostics.ReadFrame(old.frameId);
            Assert.That(oldIdNow.generation, Is.EqualTo(next));
            if (oldIdNow.available)
                Assert.That(oldIdNow.cumulative, Is.EqualTo(default(PaperSamplingDiagnostics.Counts)), "A reused frame ID exposes only the new generation.");
            Assert.That(old.generation, Is.Not.EqualTo(fresh.generation), "The detached old value remains old; it is not a live bucket reference.");
        }
    }
}
#endif
