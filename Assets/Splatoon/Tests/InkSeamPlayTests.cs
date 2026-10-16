#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
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

        [UnityTest, Timeout(600000)] public IEnumerator HostShootsAcrossSeamsAndRestoresPaint()
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
            var app = PrototypeApp.Current; ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && Camera.main != null && PrototypeMatch.Current != null &&
                PrototypeMatch.Current.BlockSyncReady, "Normal block host ready");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current; var arena = match.Arena; var terrain = match.Blocks;
            var camera = new GameObject("Block seam capture").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1;
            var target = new Vector3(-10.5f, 3, -5);
            camera.transform.position = target + new Vector3(2, 4, -4);
            camera.transform.LookAt(target); camera.fieldOfView = 42;
            string output = PaidBlockHostPlayRig.NewOutput("Seams");
            var ramp = arena.Surfaces.Values.Single(s => s.name == "Ramp_-1_-1");
            var platform = arena.Surfaces.Values.Single(s => s.name == "Platform_-1");
            var rampPoint = ramp.transform.TransformPoint(new Vector3(.05f, 0, ramp.WalkableSize.y / 2 - .4f));
            var platformPoint = platform.transform.TransformPoint(new Vector3(.05f, 0, -platform.WalkableSize.y / 2 + .4f));
            Assert.That(ramp.GetComponent<Collider>().Raycast(new Ray(rampPoint + ramp.transform.up, -ramp.transform.up), out var rampHit, 2), Is.True, "Real ramp side of the shared edge");
            Assert.That(platform.GetComponent<Collider>().Raycast(new Ray(platformPoint + Vector3.up, Vector3.down), out var platformHit, 2), Is.True, "Real platform side of the shared edge");
            try
            {
                using (var rig = new PaidBlockHostPlayRig(app, player))
                {
                    yield return rig.Ready(); yield return rig.SelectTeam(1);
                    foreach (int hero in new[] { 1, 4 })
                    {
                        yield return rig.SelectHero(hero);
                        int accepted = 0, added = 0; float material = 0;
                        var priorOwners = PaidBlockHostPlayRig.StableOwners(terrain.World);
                        for (int shot = 0; shot < 12; shot++)
                        {
                            // The old single ink-brush unfold is retired. Actual paid shots
                            // target both physical sides while the normal match continues.
                            bool onRamp = shot < 6;
                            var point = (onRamp ? rampHit.point : platformHit.point) + Vector3.right * ((shot % 6 - 2.5f) * .12f);
                            var preferredFeetSeed = point + (onRamp ? Vector3.back : Vector3.forward) * 1.7f;
                            var feetSeed = ChooseFiringFeetSeed(player, point, preferredFeetSeed, hero, shot, onRamp);
                            yield return rig.FireAt(point, feetSeed, observeCompletionGravity: true);
                            Assert.That(rig.ContactPositions.Count, Is.GreaterThan(0), "The paid trajectory collides with the real seam structure");
                            Assert.That(rig.LastTokens.Sum(t => t.Added), Is.GreaterThan(0), "Paid seam attack builds, including on earlier friendly blocks");
                            accepted += rig.AcceptedShots; material += rig.MaterialPaid; added += rig.LastTokens.Sum(t => t.Added);
                            if (shot == 0) Capture(camera, $"hero-{hero}-first", output);
                        }
                        Capture(camera, $"hero-{hero}-sweep", output);
                        var owners = PaidBlockHostPlayRig.StableOwners(terrain.World);
                        var fresh = owners.Where(p => !priorOwners.ContainsKey(p.Key)).ToArray();
                        Assert.That(accepted, Is.GreaterThanOrEqualTo(12)); Assert.That(added, Is.GreaterThan(0));
                        Assert.That(fresh.Any(p => MapSurfaceBelow(p.Key, terrain) == ramp), Is.True, "New gameplay blocks over the real ramp");
                        Assert.That(fresh.Any(p => MapSurfaceBelow(p.Key, terrain) == platform), Is.True, "New gameplay blocks over the real platform");
                        Assert.That(fresh.Select(p => BlockWorld.ChunkAt(p.Key)).Distinct().Count(), Is.GreaterThanOrEqualTo(2), "The paid seam structure crosses sparse chunk boundaries");
                        foreach (var cell in fresh)
                        {
                            var beneath = MapSurfaceBelow(cell.Key, terrain);
                            Assert.That(beneath == ramp || beneath == platform, Is.True, "No construction leaks through the elevated seam onto lower ground or walls");
                            Assert.That(cell.Value, Is.EqualTo(player.Snapshot.Value.Team));
                        }
                        PaidBlockHostPlayRig.AssertStableCollision(terrain);
                        Assert.That(rig.Publications, Is.GreaterThan(0), "Normal asynchronous atomic publication was observed");
                        // Positive restore contract now belongs to terrain produced by the gun.
                        // Same-head full repair recreates colliders without injecting free fixture cells.
                        yield return rig.Wait(() => terrain.PendingTransactions == 0 && terrain.FallingCount == 0, "Stable seam before exact snapshot repair");
                        var snapshot = terrain.ExportSnapshot(); var source = terrain.World;
                        uint revision = terrain.Revision; int pink = terrain.PinkCount, blue = terrain.BlueCount;
                        var decoded = BlockWorld.DecodeSnapshot(snapshot);
                        CollectionAssert.AreEqual(snapshot, decoded.EncodeSnapshot(), "Full gameplay snapshot codec is byte exact");
                        CollectionAssert.AreEquivalent(owners, PaidBlockHostPlayRig.StableOwners(decoded), "Every decoded cell retains its owner and position");
                        int commits = rig.Publications;
                        Assert.That(terrain.ApplyRemoteSnapshot(snapshot), Is.True, "Production full repair accepts the current published snapshot");
                        Assert.That(terrain.World, Is.SameAs(source), "Full repair cannot publish raw cells ahead of rebuilt collision");
                        yield return rig.Wait(() => terrain.PendingTransactions == 0 && !ReferenceEquals(terrain.World, source) && rig.Publications > commits, "Normal full snapshot mesh/collider publication");
                        CollectionAssert.AreEqual(snapshot, terrain.ExportSnapshot(), "Repaired gameplay terrain restores every snapshot byte");
                        CollectionAssert.AreEquivalent(owners, PaidBlockHostPlayRig.StableOwners(terrain.World));
                        Assert.That(terrain.Revision, Is.EqualTo(revision));
                        Assert.That(terrain.PinkCount, Is.EqualTo(pink)); Assert.That(terrain.BlueCount, Is.EqualTo(blue));
                        Assert.That(match.State.Value.TerrainRevision, Is.EqualTo(revision));
                        Assert.That(match.State.Value.PinkBlockCount, Is.EqualTo(pink)); Assert.That(match.State.Value.BlueBlockCount, Is.EqualTo(blue));
                        PaidBlockHostPlayRig.AssertStableCollision(terrain);
                        // Production terrain is Blocks. Arena paint textures stay unallocated;
                        // explicit legacy Restore would revive a retired gameplay path here.
                        AssertRetiredLegacyPaint(arena);
                        File.WriteAllText(Path.Combine(output, $"host-hero-{hero}.txt"),
                            $"PASS normal Boot host: {accepted} paid emissions, material={material}, added={added}; both physical seam sides receive owned blocks across chunks, no lower-ground/wall leak. Full current-head block snapshot repairs every byte, position/owner, score, version and collider. Legacy GPU paint planes remain unallocated; all CPU compatibility regions stay unpainted. No independent remote client or screenshot-pixel review claimed.\n");
                    }
                }
                yield return app.Leave().ToCoroutine();
                yield return Wait(() => BlockTerrain.Current == null && InkPresentation.Current == null, "Leave retires seam terrain and flight presentation");
            }
            finally { if (camera != null) UnityEngine.Object.Destroy(camera.gameObject); }
        }

        static Vector3 ChooseFiringFeetSeed(PrototypePlayer player, Vector3 target, Vector3 preferredSeed,
            int hero, int shot, bool onRamp)
        {
            var motor = (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(player);
            Assert.That(motor, Is.Not.Null, "The real player motor supplies the standing clearance check.");
            var controller = player.GetComponent<CharacterController>();
            var attempts = new List<string>(25);
            // Existing shots can build uneven steps around the next prescribed
            // firing point. Find a grounded full-body placement using native
            // geometry; the unchanged rig repeats both checks before emission.
            // This query never clears terrain, restores the player or changes aim.
            Physics.SyncTransforms();
            var directions = new[] { Vector3.right, Vector3.left, Vector3.back, Vector3.forward,
                new Vector3(1, 0, -1).normalized, new Vector3(-1, 0, -1).normalized,
                new Vector3(1, 0, 1).normalized, new Vector3(-1, 0, 1).normalized };
            for (int candidate = 0; candidate < 25; candidate++)
            {
                var offset = candidate == 0 ? Vector3.zero
                    : directions[(candidate - 1) % directions.Length] * (.4f * (1 + (candidate - 1) / directions.Length));
                var seed = preferredSeed + offset;
                // Use the exact floor probe and .04m feet lift of PlaceAndAim.
                bool found = Physics.Raycast(seed + Vector3.up * 6, Vector3.down, out var floor, 20,
                    PlayerMotorSimulation.WorldMask);
                if (!found) { attempts.Add($"candidate={candidate} seed={seed:R} floor=none"); continue; }
                var feet = floor.point + Vector3.up * .04f;
                bool fits = motor.CanStand(feet);
                string native = $"candidate={candidate} seed={seed:R} floor={floor.collider.name}/{floor.collider.GetInstanceID()} layer={floor.collider.gameObject.layer} point={floor.point:R} normal={floor.normal:R} feet={feet:R} fits={fits}";
                attempts.Add(native);
                if (!fits) continue;
                Debug.Log($"[Seam firing placement] hero={hero} shot={shot + 1} side={(onRamp ? "ramp" : "platform")} target={target:R} preferredSeed={preferredSeed:R} {native}");
                return seed;
            }
            Assert.Fail($"No grounded native firing placement fits the real body within 1.2m. hero={hero} shot={shot + 1} side={(onRamp ? "ramp" : "platform")} target={target:R} preferredSeed={preferredSeed:R} playerPosition={player.Snapshot.Value.Position:R} controllerPosition={controller.transform.position:R} height={controller.height:R} radius={controller.radius:R} center={controller.center:R} boundsCenter={controller.bounds.center:R} boundsExtents={controller.bounds.extents:R}\n"
                + string.Join("\n", attempts));
            return preferredSeed; // Assert.Fail always throws; there is no unverified fallback.
        }

        static PaintSurface MapSurfaceBelow(Vector3Int cell, BlockTerrain terrain)
        {
            var center = BlockWorld.Center(cell);
            return Physics.Raycast(center + Vector3.up * .05f, Vector3.down, out var hit, 5,
                PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore)
                ? hit.collider.GetComponentInParent<PaintSurface>() : null;
        }

        static void AssertRetiredLegacyPaint(PrototypeArena arena)
        {
            Assert.That(arena.PinkArea + arena.BlueArea, Is.Zero,
                "Paid seam blocks and their snapshot repair do not activate legacy ink ownership");
            foreach (var surface in arena.Surfaces.Values)
            {
                string label = "Retired legacy paint: " + surface.name;
                Assert.That(surface.HasPaint, Is.False, label + " is not active");
                Assert.That(surface.PendingPaintCount, Is.Zero, label + " has no queued stamps");
                Assert.That(surface.Mask, Is.Null, label + " has no raw GPU plane");
                Assert.That(surface.DisplayMask, Is.Null, label + " has no display GPU plane");
                Assert.That(surface.VisualState, Is.Null, label + " has no visual GPU plane");
                // Ground and wall CPU compatibility grids retain authored blocked
                // cells (255); those cells are topology, not either team's paint.
                foreach (var region in surface.GameplayRegions)
                {
                    string regionLabel = label + " region=" + region.Id;
                    Assert.That(region.Grid, Is.Not.Null, regionLabel + " retains its CPU compatibility grid");
                    Assert.That(region.Grid.Cells.All(owner => owner == 0 || owner == 255), Is.True,
                        regionLabel + " has no legacy team ownership");
                    Assert.That(region.Grid.State.All(value => value == 0), Is.True,
                        regionLabel + " has no accumulated legacy paint state");
                    Assert.That(region.Grid.PinkArea, Is.Zero, regionLabel + " has no pink painted area");
                    Assert.That(region.Grid.BlueArea, Is.Zero, regionLabel + " has no blue painted area");
                }
            }
        }

        static void Capture(Camera camera, string name, string output)
        {
            var target = RenderTexture.GetTemporary(960, 960, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var texture = new Texture2D(960, 960, TextureFormat.RGBA32, false, false);
            try
            {
                InkPresentation.Current.UpdateFlights(PrototypeApp.Current.Manager.ServerTime.Time, camera);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); texture.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(texture); }
        }
    }
}
#endif
