#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    // The EditMode runner manually steps test enumerators, even after EnterPlayMode.
    // Use a real runtime coroutine for rendering yield instructions.
    internal sealed class BlockGameplayFramePump : MonoBehaviour
    {
        public Coroutine Capture(Action capture, Action<Exception> completed)
            => StartCoroutine(AtEndOfFrame(capture, completed));

        static IEnumerator AtEndOfFrame(Action capture, Action<Exception> completed)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Exception error = null;
            try { capture(); }
            catch (Exception exception) { error = exception; }
            completed(error);
        }
    }

    /// <summary>Test-only default terrain publication and real resource-payment fixture.</summary>
    internal sealed class BlockDefaultContractFixture : IDisposable
    {
        public const float Dt = 1f / 60;
        public readonly PrototypeMatch Match;
        public readonly BlockTerrain Terrain;
        public double Now;
        readonly Action _serverTick;
        bool _disposed;

        public BlockDefaultContractFixture(PrototypeMatch match)
        {
            Match = match; Terrain = match.Blocks;
            Assert.That(Terrain, Is.Not.Null, "Default block terrain must be installed");
            Assert.That(Terrain.Initialized, Is.True);
            Assert.That(BlockAttackService.Active, Is.True, "Never disable block mode to recover a legacy paint contract");
            Now = match.NetworkManager.ServerTime.Time;
            _serverTick = (Action)Delegate.CreateDelegate(typeof(Action), match,
                typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic));
            if (match.NetworkManager.NetworkTickSystem != null) match.NetworkManager.NetworkTickSystem.Tick -= _serverTick;
        }
        public void Tick()
        {
            Now += Dt; Terrain.Tick(true, Now, Dt); Physics.SyncTransforms();
        }
        public IEnumerator Publish(Func<bool> ready = null, int maximumFrames = 1800)
        {
            for (int frame = 0; frame < maximumFrames && (Terrain.PendingTransactions > 0 || ready != null && !ready()); frame++)
            { Tick(); yield return null; }
            Assert.That(Terrain.PendingTransactions, Is.Zero, "Finish the full requested terrain result, not a partial publication");
            Assert.That(ready == null || ready(), Is.True, "Expected real terrain result was published");
            Assert.That(Terrain.FailedTransactions, Is.Zero);
            Assert.That(Match.State.Value.PinkBlockCount, Is.EqualTo(Terrain.PinkCount));
            Assert.That(Match.State.Value.BlueBlockCount, Is.EqualTo(Terrain.BlueCount));
            Assert.That(Match.State.Value.TerrainRevision, Is.EqualTo(Terrain.Revision));
        }
        public IEnumerator Seed(BlockWorld world)
        {
            Assert.That(Terrain.PendingTransactions, Is.Zero, "Fixture seeding cannot cancel an accepted attack");
            var points = Match.Players.Select(p => p.Snapshot.Value.SpecialPoints).ToArray();
            Assert.That(Terrain.QueueSeed(world), Is.True);
            yield return Publish();
            CollectionAssert.AreEqual(points, Match.Players.Select(p => p.Snapshot.Value.SpecialPoints).ToArray(),
                "Diagnostic structure placement must never award resource-payment income");
        }
        public BlockWorld Empty() => new(Terrain.World.Dimensions, Terrain.Round, Terrain.World.Bottom);
        public byte[] Capture() => Terrain.World.EncodeSnapshot();
        public void Unchanged(byte[] before, string label)
        {
            Assert.That(Terrain.PendingTransactions, Is.Zero, label + ": no deferred terrain mutation");
            CollectionAssert.AreEqual(before, Capture(), label + ": positions, ownership, counts and revision are unchanged");
        }
        public void FlushEvents() => _serverTick();

        // Diagnostic support only: resource-payment acceptance uses PayMain/PaySub below.
        public BlockMutationResult SeedMapPaint(RaycastHit map, byte owner, float radius)
        {
            Assert.That(Terrain.IsAuthoredMapCollider(map.collider), Is.True);
            var result = Terrain.PaintSurfaceDisk(map.point, map.normal, radius, owner, 4096);
            Assert.That(Terrain.TryGetSurfacePaint(map.collider, map.point, map.normal, out var cell), Is.True);
            Assert.That(cell.Owner, Is.EqualTo(owner), "The complete test support route has its intended surface owner");
            return result;
        }

        [Serializable] sealed class GameplayFrame
        {
            public string stage, path, sha256, camera, utc, capturePhase, activeRenderTarget, error;
            public string overlayBefore, overlayAtCapture, overlayAfter;
            public int width, height, pinkSurface, blueSurface, pinkDynamic, blueDynamic;
            public int frame, screenWidth, screenHeight, cameraPixelWidth, cameraPixelHeight;
            public int activeRenderTargetWidth, activeRenderTargetHeight;
            public double fixedTime, realtime, elapsedSeconds;
            public bool normalMainCamera, captured;
            public bool gameplayCapturedBefore, gameplayCapturedAtCapture, gameplayCapturedAfter;
            public bool fireInputBlockedBefore, fireInputBlockedAtCapture, fireInputBlockedAfter, appStateRestored;
            public Vector3 cameraPosition, focus;
        }

        static void InspectGameplayFrame(GameplayFrame receipt, Camera camera)
        {
            receipt.utc = DateTime.UtcNow.ToString("o");
            receipt.frame = Time.frameCount; receipt.fixedTime = Time.fixedTimeAsDouble;
            receipt.realtime = Time.realtimeSinceStartupAsDouble;
            receipt.screenWidth = Screen.width; receipt.screenHeight = Screen.height;
            receipt.camera = camera != null ? camera.name : null;
            receipt.cameraPixelWidth = camera != null ? camera.pixelWidth : 0;
            receipt.cameraPixelHeight = camera != null ? camera.pixelHeight : 0;
            receipt.cameraPosition = camera != null ? camera.transform.position : default;
            receipt.normalMainCamera = camera != null && Camera.main == camera && camera.isActiveAndEnabled && camera.targetTexture == null;
            var active = RenderTexture.active;
            receipt.activeRenderTarget = active != null ? active.name : null;
            receipt.activeRenderTargetWidth = active != null ? active.width : 0;
            receipt.activeRenderTargetHeight = active != null ? active.height : 0;
        }

        // Capture the production main camera's real backbuffer. This is an inspectable
        // gameplay image, not an automated visual-correctness or network acceptance oracle.
        public IEnumerator CaptureGameplayFrame(string stage, Vector3 focus, Vector3 offset)
        {
            var camera = Camera.main;
            const string directory = "Reports/BlockSimulation20261018/GameplayImages";
            Directory.CreateDirectory(directory);
            string path = Path.GetFullPath(Path.Combine(directory, stage + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png"));
            var receipt = new GameplayFrame { stage = stage, path = path, focus = focus, capturePhase = "setup" };
            InspectGameplayFrame(receipt, camera);
            if (!receipt.normalMainCamera)
            {
                receipt.error = "Gameplay images require the active production main camera and its original backbuffer";
                File.WriteAllText(Path.ChangeExtension(path, ".failure.json"), JsonUtility.ToJson(receipt, true));
                Assert.Fail(receipt.error);
            }
            var app = PrototypeApp.Current;
            Assert.That(app, Is.Not.Null, "The gameplay capture keeps the existing application and HUD");
            const BindingFlags appFields = BindingFlags.Instance | BindingFlags.NonPublic;
            var capturedField = typeof(PrototypeApp).GetField("_captured", appFields);
            var overlayField = typeof(PrototypeApp).GetField("_overlay", appFields);
            var fireBlockedField = typeof(PrototypeApp).GetField("_fireInputBlocked", appFields);
            Assert.That(capturedField, Is.Not.Null); Assert.That(overlayField, Is.Not.Null); Assert.That(fireBlockedField, Is.Not.Null);
            bool capturedBefore = (bool)capturedField.GetValue(app);
            var overlayBefore = (GameplayOverlay)overlayField.GetValue(app);
            bool fireBlockedBefore = (bool)fireBlockedField.GetValue(app);
            receipt.gameplayCapturedBefore = capturedBefore; receipt.overlayBefore = overlayBefore.ToString();
            receipt.fireInputBlockedBefore = fireBlockedBefore;
            Assert.That(capturedBefore, Is.False, "Gameplay screenshot fixtures must already release logical mouse capture");
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            Texture2D texture = null;
            GameObject captureObject = null;
            BlockGameplayFramePump pump = null;
            Coroutine capture = null;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool completed = false; Exception error = null;
                try
                {
                    // Hide only the room menu for this image. Logical capture remains false;
                    // do not call CaptureMouse/CloseOverlay or touch the system cursor.
                    overlayField.SetValue(app, GameplayOverlay.Game);
                    // These fixtures disable automatic player simulation. Invoke the
                    // existing presentation path once so images display the tested state.
                    var present = typeof(PrototypePlayer).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                    foreach (var player in Match.Players)
                        if (player != null && !player.enabled) present.Invoke(player, null);
                    camera.transform.SetPositionAndRotation(focus + offset, Quaternion.LookRotation(-offset));
                    captureObject = new GameObject("BlockGameplayFramePump") { hideFlags = HideFlags.HideAndDontSave };
                    pump = captureObject.AddComponent<BlockGameplayFramePump>();
                    receipt.capturePhase = "waiting-runtime-end-of-frame";
                    capture = pump.Capture(() =>
                    {
                        receipt.capturePhase = "runtime-end-of-frame";
                        InspectGameplayFrame(receipt, camera);
                        Assert.That(PrototypeApp.Current, Is.SameAs(app), "The same application still owns the gameplay HUD");
                        receipt.gameplayCapturedAtCapture = (bool)capturedField.GetValue(app);
                        receipt.overlayAtCapture = ((GameplayOverlay)overlayField.GetValue(app)).ToString();
                        receipt.fireInputBlockedAtCapture = (bool)fireBlockedField.GetValue(app);
                        Assert.That(receipt.gameplayCapturedAtCapture, Is.False, "The screenshot must not acquire gameplay or system mouse capture");
                        Assert.That(receipt.overlayAtCapture, Is.EqualTo(GameplayOverlay.Game.ToString()), "The room menu must not cover the gameplay milestone");
                        Assert.That(receipt.normalMainCamera, Is.True, "The original production camera still renders its backbuffer");
                        var viewport = camera.WorldToViewportPoint(focus);
                        Assert.That(viewport.z > 0 && viewport.x > 0 && viewport.x < 1 && viewport.y > 0 && viewport.y < 1,
                            Is.True, "The captured gameplay milestone is inside the main-camera view");
                        texture = ScreenCapture.CaptureScreenshotAsTexture();
                        Assert.That(texture, Is.Not.Null);
                        receipt.width = texture.width; receipt.height = texture.height; receipt.captured = true;
                    }, exception => { error = exception; completed = true; });
                }
                catch (Exception exception)
                {
                    InspectGameplayFrame(receipt, camera);
                    receipt.error = exception.ToString(); receipt.elapsedSeconds = deadline.Elapsed.TotalSeconds;
                    File.WriteAllText(Path.ChangeExtension(path, ".failure.json"), JsonUtility.ToJson(receipt, true));
                    throw;
                }
                while (!completed && deadline.Elapsed.TotalSeconds < 10) yield return null;
                receipt.elapsedSeconds = deadline.Elapsed.TotalSeconds;
                if (!completed || error != null)
                {
                    if (!completed) InspectGameplayFrame(receipt, camera);
                    receipt.error = error?.ToString() ?? "The runtime end-of-frame capture did not complete within 10 seconds";
                    string failure = Path.ChangeExtension(path, ".failure.json");
                    File.WriteAllText(failure, JsonUtility.ToJson(receipt, true));
                    Assert.Fail(receipt.error + "\nCapture diagnostics: " + failure);
                }
                try
                {
                    byte[] png = texture.EncodeToPNG();
                    File.WriteAllBytes(path, png);
                    using var digest = SHA256.Create();
                    receipt.sha256 = BitConverter.ToString(digest.ComputeHash(png)).Replace("-", "");
                    receipt.pinkSurface = Terrain.SurfacePaints.PinkCount; receipt.blueSurface = Terrain.SurfacePaints.BlueCount;
                    receipt.pinkDynamic = Terrain.Dynamic.PinkCount; receipt.blueDynamic = Terrain.Dynamic.BlueCount;
                }
                catch (Exception exception)
                {
                    receipt.error = exception.ToString();
                    File.WriteAllText(Path.ChangeExtension(path, ".failure.json"), JsonUtility.ToJson(receipt, true));
                    throw;
                }
            }
            finally
            {
                try
                {
                    if (pump != null && capture != null) pump.StopCoroutine(capture);
                    if (captureObject != null) UnityEngine.Object.Destroy(captureObject);
                    if (texture != null) UnityEngine.Object.Destroy(texture);
                    if (camera != null) camera.transform.SetPositionAndRotation(position, rotation);
                }
                finally
                {
                    if (app != null)
                    {
                        capturedField.SetValue(app, capturedBefore); overlayField.SetValue(app, overlayBefore);
                        fireBlockedField.SetValue(app, fireBlockedBefore);
                        receipt.gameplayCapturedAfter = (bool)capturedField.GetValue(app);
                        receipt.overlayAfter = ((GameplayOverlay)overlayField.GetValue(app)).ToString();
                        receipt.fireInputBlockedAfter = (bool)fireBlockedField.GetValue(app);
                        receipt.appStateRestored = receipt.gameplayCapturedAfter == capturedBefore &&
                            receipt.overlayAfter == overlayBefore.ToString() && receipt.fireInputBlockedAfter == fireBlockedBefore;
                    }
                    if (!receipt.appStateRestored) receipt.error = (receipt.error ?? "") + "\nGameplay screenshot application state was not restored";
                    if (receipt.error != null || receipt.captured && receipt.sha256 != null)
                        File.WriteAllText(Path.ChangeExtension(path, receipt.error != null ? ".failure.json" : ".json"), JsonUtility.ToJson(receipt, true));
                    Assert.That(receipt.appStateRestored, Is.True, "The screenshot restores logical capture, overlay and fire-input blocking exactly");
                }
            }
            TestContext.Out.WriteLine("Gameplay backbuffer: " + path + " SHA256 " + receipt.sha256);
        }

        public BlockAttackToken PayMain(PrototypePlayer owner, int hero)
        {
            var s = owner.Snapshot.Value; var weapon = GameplayConfig.GetWeapon(hero);
            s.HeroId = hero; s.Health = 100; s.LifeState = PlayerLifeState.Alive;
            s.Ink = 100; s.WeaponPhase = WeaponPhase.Idle; s.AttackNeedsRelease = false;
            s.BurstRemaining = 0; s.NextShotAt = s.BurstReadyAt = s.AttackRecoveryUntil = 0;
            s.SpecialPhase = SpecialPhase.Charging; s.SpecialChargeLockedUntil = 0;
            var input = new PlayerInputFrame { Sequence = s.ShotSequence + 1, HeroRevision = s.HeroRevision,
                Fire = true, FireSequence = s.ConsumedFire + 1, ReleaseSequence = s.ConsumedRelease };
            float before = s.Ink; bool emitted = false; WeaponFireResult fire = default;
            for (int frame = 0; frame < 240 && !emitted; frame++)
            { emitted = WeaponSimulation.Step(ref s, input, weapon, Now, false, true, out fire); if (!emitted) Now += Dt; }
            Assert.That(emitted, Is.True, "Loaded weapon input must really emit the paid attack");
            Assert.That(before - s.Ink, Is.EqualTo(WeaponSimulation.InkCost(weapon, fire.Charge)).Within(.001),
                "One accepted attack pays the original authored material cost");
            owner.Snapshot.Value = s;
            var token = BlockAttackService.Main(owner.PlayerId, s, Terrain.Round, weapon);
            Assert.That(token, Is.Not.Null);
            Assert.That(token.Credit.IsBlockPoints, Is.True);
            return token;
        }
        public InkShot PaidShot(PrototypePlayer owner, int hero, uint id, Vector3 origin, Vector3 velocity)
        {
            var token = PayMain(owner, hero); var s = owner.Snapshot.Value;
            return new InkShot { Id = id, Round = Terrain.Round, Shooter = owner.PlayerId, Team = s.Team,
                HeroId = hero, Lifecycle = s.Revision, HeroRevision = s.HeroRevision, Seed = id + 7,
                ShotSequence = s.ShotSequence, VolleyIndex = (byte)Math.Max(0, s.BurstShotIndex - 1),
                Born = Now, Origin = origin, Velocity = velocity, ActionId = s.ShotActionId,
                Configuration = GameplayConfig.GetWeapon(hero), ConfigurationRevision = WeaponConfigService.Current.Revision(hero), BlockAttack = token };
        }
        public float PaySub(PrototypePlayer owner, SubWeaponType type, float desiredCharge = 0)
        {
            var s = owner.Snapshot.Value; s.SubWeaponId = SubWeaponConfigService.Id(type);
            s.Health = 100; s.LifeState = PlayerLifeState.Alive; s.Ink = 100;
            s.SubPhase = SubWeaponPhase.Idle; s.SubNeedsRelease = false;
            s.SubReadyAt = s.SubRecoveryUntil = s.AttackRecoveryUntil = 0;
            var config = PlayerLoadout.SubWeapon(s); float before = s.Ink; uint action = s.SubAction;
            var input = new PlayerInputFrame { HeroRevision = s.HeroRevision, SubHeld = true,
                SubPressSequence = s.SubConsumedPress + 1, SubReleaseSequence = s.SubConsumedRelease };
            for (int frame = 0; frame < 600; frame++)
            {
                Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, Now, true,
                    Match.SubWeapons.CanUse(owner.PlayerId, s, config), out _), Is.False, "Holding does not pay or spawn");
                Now += Dt; if (config.Charge(s.SubChargeSeconds) + 1e-6 >= desiredCharge) break;
            }
            Assert.That(s.SubCharge + 1e-6, Is.GreaterThanOrEqualTo(desiredCharge));
            input.SubHeld = false; input.SubReleaseSequence++;
            bool paid = SubWeaponSimulation.Step(ref s, input, config, Dt, Now, true,
                Match.SubWeapons.CanUse(owner.PlayerId, s, config), out float charge);
            for (int frame = 0; frame < 240 && !paid; frame++)
            {
                Now += Dt;
                paid = SubWeaponSimulation.Step(ref s, input, config, Dt, Now, true,
                    Match.SubWeapons.CanUse(owner.PlayerId, s, config), out charge);
            }
            Assert.That(paid, Is.True, "Actual sub input reaches its authored paid commitment");
            Assert.That(s.SubAction, Is.EqualTo(action + 1));
            Assert.That(before - s.Ink, Is.EqualTo(config.Common.inkCost).Within(.001));
            owner.Snapshot.Value = s; return charge;
        }
        public static BlockFlightPresentation Flights(InkPresentation presentation)
        {
            var flight = (BlockFlightPresentation)typeof(InkPresentation).GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(presentation);
            Assert.That(flight, Is.Not.Null, "Actual instance renderer was created"); return flight;
        }
        public static Matrix4x4[] RenderedInstances(InkPresentation presentation, byte team, int count)
        {
            var flight = Flights(presentation);
            var matrices = (Matrix4x4[][])typeof(BlockFlightPresentation).GetField("_matrices", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flight);
            Assert.That(count, Is.InRange(1, BlockFlightPresentation.InstanceBatch));
            Assert.That(presentation.BlockCubeCount, Is.EqualTo(count), "This isolated render contains only the requested team's shots");
            // Flush resets the active write cursor, but retains the just-submitted transforms.
            return matrices[team - 1].Take(count).ToArray();
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (Match != null && Match.NetworkManager != null && Match.NetworkManager.NetworkTickSystem != null)
                Match.NetworkManager.NetworkTickSystem.Tick += _serverTick;
        }
    }
}
#endif
