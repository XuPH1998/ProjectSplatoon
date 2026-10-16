#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class AimBallisticsPlayTests
    {
        const string Output = "Reports/AimBallistics/PlayMode";
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly List<GameObject> Fixtures = new();
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnityTest, Timeout(600000)] public IEnumerator HostRendersAllSixGuidesFiresAndFreezesHotReloadedShots()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode(); yield return Scenario(); yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            foreach (var go in Fixtures) if (go != null) Object.Destroy(go); Fixtures.Clear();
            typeof(CameraReticleCapture).GetMethod("RestoreGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "/status.txt", "RUNNING: Editor Host acceptance");
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current;
            ushort port;
            using (var socket = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                port = (ushort)((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom && !app.IsWeaponDebugRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && Camera.main != null &&
                PrototypeMatch.Current != null && PrototypeMatch.Current.BlockSyncReady &&
                InkPresentation.Current != null && PrototypePlayer.Local.CharacterView != null, "Normal host player, blocks and camera");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            var resize = (UniTask)typeof(CameraReticleCapture).GetMethod("Resize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { view, 1280, 720 });
            yield return resize.ToCoroutine();
            Vector3 feet = (Vector3)typeof(CameraReticleCapture).GetMethod("FindClearFloor", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            var rows = new List<string> { "hero,case,mainX,mainY,directionX,directionY,blocked,duplicateImpact,added,removedEnemy,actualShots" };
            var solver = new TpsAimSolver();
            var surface = match.Arena.Surfaces.Values.Single(s => s.name == "EndBoundary_-1");
            var collider = surface.GetComponent<Collider>(); var center = collider.bounds.center; center.x = 12;
            Assert.That(collider.Raycast(new Ray(center + Vector3.forward * 3, Vector3.back), out var hit, 5), Is.True);
            Assert.That(match.Arena.IsInHeroChangeZone(1, hit.point) || match.Arena.IsInHeroChangeZone(2, hit.point), Is.False);
            foreach (int hero in AimBallisticsTests.Heroes)
            {
                match.enabled = true; player.enabled = true; player.GetComponent<CharacterController>().enabled = true;
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero &&
                    player.CharacterView.Profile.name == AimBallisticsTests.Names[hero] + "Presentation", "Hero " + hero);
                match.enabled = false; player.enabled = false;
                var current = GameplayConfig.GetWeapon(hero);
                try
                {
                foreach (string label in new[] { "before", "standing", "up30", "down30", "forward", "backward", "near-wall", "camera-wall" })
                {
                    var w = label == "before" ? AimBallisticsTests.LegacySnapshot(hero) : current;
                    WeaponConfigService.Current.SetForEditor(hero, w);
                    var state = player.Snapshot.Value; state.Position = feet; state.Yaw = state.BodyYaw = 0;
                    state.Pitch = label == "up30" ? -30 : label == "down30" ? 30 : 0;
                    state.Health = state.Ink = 100; state.Grounded = true; state.Swimming = state.CompactBody = false;
                    state.AirHumanOffset = state.CameraRebaseOffset = 0; state.PaperPose = PaperPose.None; state.Movement = MovementMode.Human;
                    state.Firing = false; state.WeaponPhase = WeaponPhase.Idle; state.SplatlingRemaining = 0;
                    state.PlanarVelocity = Vector3.forward * (label == "forward" ? 3 : label == "backward" ? -3 : 0);
                    state.VerticalSpeed = 0; state.NextMuzzle = state.LastShotMuzzle = (byte)(hero == 2 && label == "down30" ? 1 : 0);
                    state.CurrentSpread = w.SpreadDegrees; state.CurrentVerticalSpread = hero == 6 ? w.SplatlingPitchSpread : w.SpreadDegrees;
                    player.GetComponent<CharacterController>().enabled = false; player.transform.position = feet;
                    player.Snapshot.Value = state; player.SwimBody.ApplyCollision(state);
                    typeof(PrototypePlayer).GetField("_look", Private).SetValue(player, new Vector2(0, state.Pitch));
                    Physics.SyncTransforms();
                    GameObject wall = null;
                    if (label.EndsWith("wall"))
                    {
                        var aim = solver.Resolve(player, state, state.NextMuzzle);
                        wall = GameObject.CreatePrimitive(PrimitiveType.Cube); Fixtures.Add(wall); wall.name = "Aim acceptance " + label;
                        wall.transform.position = label == "near-wall" ? aim.Muzzle : (aim.CameraOrigin + aim.Pivot) * .5f;
                        wall.transform.localScale = label == "near-wall" ? Vector3.one * .5f : new Vector3(3, 3, .15f);
                        Physics.SyncTransforms();
                    }
                    app.CaptureMouse(true);
                    typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null); player.CharacterView.Animator.Update(.1f);
                    var resolved = solver.Resolve(player, state, state.NextMuzzle);
                    if (label != "before")
                    {
                        var prediction = WeaponImpactPrediction.Guide(solver, resolved, w, state, player.PlayerId);
                        Assert.That(Vector2.Distance(player.ReticleViewport, TpsAimSolver.ReticleViewport(Camera.main, prediction.Point)), Is.LessThan(.00001));
                        Assert.That(player.ImpactReticleVisible, Is.False);
                        Assert.That(player.GuideSpreadHalfSize.x + player.GuideSpreadHalfSize.y, Is.GreaterThan(0));
                        if (label == "near-wall")
                        {
                            Assert.That(player.MuzzleBlocked, Is.True);
                            Assert.That(player.GuideSpreadHalfSize, Is.EqualTo(Vector2.one * 6),
                                "An immediate obstruction must not turn muzzle/contact separation into angular spread");
                        }
                    }
                    yield return null; yield return null;
                    string image = Path.GetFullPath($"{Output}/hero-{hero}-{label}.png"); if (File.Exists(image)) File.Delete(image);
                    ScreenCapture.CaptureScreenshot(image); yield return Wait(() => File.Exists(image), "HUD image " + label);
                    rows.Add(FormattableString.Invariant($"{hero},{label},{player.ReticleViewport.x:R},{player.ReticleViewport.y:R},{player.DirectionReticleViewport.x:R},{player.DirectionReticleViewport.y:R},{player.MuzzleBlocked},{player.ImpactReticleVisible},0,0,0"));
                    if (wall != null) { Object.Destroy(wall); yield return null; Physics.SyncTransforms(); }
                }
                }
                finally
                {
                    WeaponConfigService.Current.SetForEditor(hero, current);
                    match.enabled = true; player.enabled = true; player.GetComponent<CharacterController>().enabled = true;
                }
                using (var rig = new PaidBlockHostPlayRig(app, player))
                {
                    yield return rig.Ready();
                    bool demolition = BlockAttackService.ExplosiveMain(current);
                    var target = demolition ? hit.point + Vector3.right * 1.8f : hit.point;
                    if (demolition)
                    {
                        yield return rig.SelectTeam(2); yield return rig.SelectHero(1);
                        for (int attempt = 0; attempt < 12 && !PaidBlockHostPlayRig.StableOwners(match.Blocks.World)
                            .Any(p => p.Value == 2 && Vector3.Distance(BlockWorld.Center(p.Key), target) < 1.1f); attempt++)
                            yield return rig.FireAt(target, target + hit.normal * 4);
                        Assert.That(PaidBlockHostPlayRig.StableOwners(match.Blocks.World)
                            .Any(p => p.Value == 2 && Vector3.Distance(BlockWorld.Center(p.Key), target) < 1.1f), Is.True,
                            "The explosion target is made by actual paid enemy shots");
                        yield return rig.SelectTeam(1);
                    }
                    yield return rig.SelectHero(hero);
                    int pinkBefore = match.Blocks.PinkCount, blueBefore = match.Blocks.BlueCount;
                    using var movingContacts = new PublishedMovingContactObservation(match, player, rig);
                    yield return rig.FireAt(target, target + hit.normal * 4);
                    var tokens = rig.LastTokens;
                    Debug.Log($"Paid geometry contacts hero={hero} nativeStableOrMap={rig.ContactPositions.Count} publishedMoving={movingContacts.Count}\n{movingContacts.Diagnostics}");
                    Assert.That(rig.ContactPositions.Count + movingContacts.Count, Is.GreaterThan(0), "Actual host projectile reaches published geometry");
                    Assert.That(rig.DebrisSeen, Is.True, "Real host shot/impact events produce block fragments");
                    Assert.That(rig.Publications, Is.GreaterThan(0), "The actual attack publishes collision, geometry and score");
                    Assert.That(tokens.All(t => t.Added <= t.BuildLimit && t.BuildLimit <= BlockGameplaySettings.MainAttackMaxBlocks &&
                        t.AuxiliaryLimit == (int)Math.Floor(t.BuildLimit * (double)BlockGameplaySettings.AuxiliaryBudgetFraction)), Is.True,
                        "Pellets, foot and trail generation share the paid attack budget");
                    if (demolition)
                    {
                        Assert.That(tokens.All(t => t.BuildLimit == 0 && t.Added == 0), Is.True);
                        Assert.That(tokens.Sum(t => t.RemovedEnemy), Is.GreaterThan(0));
                        Assert.That(match.Blocks.BlueCount, Is.LessThan(blueBefore));
                        Assert.That(match.Blocks.PinkCount, Is.LessThanOrEqualTo(pinkBefore));
                    }
                    else
                    {
                        Assert.That(tokens.Sum(t => t.Added), Is.GreaterThan(0), "Every ordinary weapon performs a paid build");
                        Assert.That(match.Blocks.PinkCount, Is.GreaterThan(pinkBefore));
                    }
                    PaidBlockHostPlayRig.AssertStableCollision(match.Blocks);
                    rows.Add($"{hero},host-fire,0,0,0,0,false,false,{tokens.Sum(t => t.Added)},{tokens.Sum(t => t.RemovedEnemy)},{rig.AcceptedShots}");
                }
            }
            yield return app.Leave().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local == null && InkPresentation.Current == null, "Normal host leaves cleanly");
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            Assert.That(app.InRoom && app.IsWeaponDebugRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null && PrototypeMatch.Current != null, "Debug host presentation");
            player = PrototypePlayer.Local; match = PrototypeMatch.Current;
            app.CaptureMouse(false);
            var original = GameplayConfig.GetWeapon(1); var clone = Object.Instantiate(WeaponConfigService.Current.Source(1));
            var originalSource = WeaponConfigService.Current.Source(1);
            var hotPresentation = InkPresentation.Current; bool presentationEnabled = hotPresentation.enabled;
            hotPresentation.enabled = false; hotPresentation.Clear();
            try
            {
                WeaponConfigService.Current.SetForEditor(1, original, clone);
                var aim = WeaponLaunch.Geometry(new Vector3(1000, 1000, 995), Vector3.forward, new Vector3(1000, 1000, 1000), original, 1);
                var shot = WeaponLaunch.Representative(aim, original, 1); shot.HeroId = 1; shot.Id = 99001; shot.ActionId = 99001; shot.Team = 1;
                shot.Born = match.NetworkManager.ServerTime.Time; shot.Round = match.State.Value.Round; shot.ConfigurationRevision = WeaponConfigService.Current.Revision(1);
                var service = new InkProjectileService(); service.SpawnForMeasurement(shot); service.Simulate(shot.Born + .03);
                hotPresentation.Spawn(shot); BlockFlightHistoryAssertions.AssertFrozen(hotPresentation, shot, original);
                uint revision = WeaponConfigService.Current.Revision(1); clone.shotGuideSeconds += 1.0 / 60; clone.shooterMoveForwardRate += 1;
                double deadline = Time.realtimeSinceStartupAsDouble + 30;
                while (WeaponConfigService.Current.Revision(1) == revision && Time.realtimeSinceStartupAsDouble < deadline)
                { app.ApplyDebugWeaponChanges(); yield return null; }
                Assert.That(WeaponConfigService.Current.Revision(1), Is.GreaterThan(revision), app.WeaponDebugStatus);
                Assert.That(service.LiveShots().Single().Configuration, Is.SameAs(original));
                Assert.That(GameplayConfig.GetWeapon(1).ShotGuideSeconds, Is.GreaterThan(original.ShotGuideSeconds));
                BlockFlightHistoryAssertions.AssertFrozen(hotPresentation, shot, original);
                var reloaded = GameplayConfig.GetWeapon(1);
                var newAim = WeaponLaunch.Geometry(new Vector3(1000, 1000, 995), Vector3.forward,
                    new Vector3(1000, 1000, 1000), reloaded, 1);
                var newShot = WeaponLaunch.Representative(newAim, reloaded, 1);
                newShot.HeroId = 1; newShot.Id = 99002; newShot.ActionId = 99002;
                newShot.Team = 1; newShot.Round = shot.Round; newShot.Born = match.NetworkManager.ServerTime.Time;
                newShot.ConfigurationRevision = WeaponConfigService.Current.Revision(1);
                hotPresentation.Spawn(newShot); BlockFlightHistoryAssertions.AssertFrozen(hotPresentation, newShot, reloaded);
            }
            finally
            {
                WeaponConfigService.Current.SetForEditor(1, original, originalSource); Object.Destroy(clone);
                if (hotPresentation != null) { hotPresentation.Clear(); hotPresentation.enabled = presentationEnabled; }
            }
            var mismatch = (byte[])app.Manager.NetworkConfig.ConnectionData.Clone(); mismatch[0] ^= 1;
            var response = new Unity.Netcode.NetworkManager.ConnectionApprovalResponse();
            app.Manager.ConnectionApprovalCallback(new Unity.Netcode.NetworkManager.ConnectionApprovalRequest { ClientNetworkId = 900001, Payload = mismatch }, response);
            Assert.That(response.Approved, Is.False);
            File.WriteAllLines(Output + "/observations.csv", rows);
            typeof(CameraReticleCapture).GetMethod("RestoreGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            yield return app.Leave().ToCoroutine();
            Fixtures.Clear();
            File.WriteAllText(Output + "/status.txt", "PASS: normal Editor Host, six weapons, live LateUpdate/HUD screenshots, real paid InputSystem/server/projectile/block paths including Splatling charge/release and an explosion against a paid enemy wall, atomic collision/score and shared budgets. Debug-room hot reload freezes old block-flight snapshots and trajectories while new shots use the current revision; incompatible content rejected. Not a separate client or physical LAN test; final test outcome is in playmode.xml.");
        }

        // Fixture-local evidence for the separately published moving voxel data.
        // The old native map/stable-collider observer remains unchanged. This
        // branch does not accept an arbitrary disabled Collider, synthesize a
        // collision, or claim to have captured the original BlockQuery identity.
        sealed class PublishedMovingContactObservation : IDisposable
        {
            static readonly FieldInfo FallingProxy = typeof(BlockTerrain).GetField("_fallProxy", Private);
            readonly PrototypeMatch _match;
            readonly PrototypePlayer _player;
            readonly PaidBlockHostPlayRig _rig;
            static readonly FieldInfo PaymentSource = typeof(PaidBlockHostPlayRig).GetField("_paymentSource", Private);
            static readonly FieldInfo WatchingPayments = typeof(PaidBlockHostPlayRig).GetField("_watchPayments", Private);
            readonly List<string> _witnesses = new();
            public int Count { get; private set; }
            public string Diagnostics => string.Join("\n", _witnesses);
            public PublishedMovingContactObservation(PrototypeMatch match, PrototypePlayer player, PaidBlockHostPlayRig rig)
            {
                _match = match; _player = player; _rig = rig;
                match.Projectiles.ContactObserved += Observe;
            }
            static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
            void Observe(InkShot shot, Collider collider, Vector3 point, Vector3 normal, double age)
            {
                var terrain = _match != null ? _match.Blocks : null;
                var token = shot.BlockAttack;
                if (_match == null || !ReferenceEquals(_match, PrototypeMatch.Current) || !_match.IsServer ||
                    terrain == null || !ReferenceEquals(terrain, BlockTerrain.Current) || !terrain.Initialized ||
                    collider == null || FallingProxy == null || !ReferenceEquals(FallingProxy.GetValue(terrain), collider) ||
                    collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy ||
                    (PlayerMotorSimulation.WorldMask & (1 << collider.gameObject.layer)) == 0 ||
                    collider.GetComponentInParent<PrototypePlayer>() != null || !Finite(point) || !Finite(normal) ||
                    !double.IsFinite(age) || age < 0 || Mathf.Abs(normal.magnitude - 1) > .00001f || token == null ||
                    PaymentSource == null || WatchingPayments == null || !(bool)WatchingPayments.GetValue(_rig) ||
                    !_rig.LastTokens.Any(value => ReferenceEquals(value, token))) return;
                // Read the same source as the existing paid rig after its real
                // neutral-input acknowledgment, rather than assuming the earlier
                // SelectHero snapshot cannot change while FireAt waits.
                var paymentSource = (PlayerSnapshot)PaymentSource.GetValue(_rig);
                var world = terrain.World; uint revision = world.Revision, round = world.Round; double time = terrain.SimulationTime;
                if (token.Credit.Kind != PaintAttackKind.Main || !token.Credit.IsBlockPoints ||
                    shot.Shooter != _player.PlayerId || token.Credit.Owner != shot.Shooter ||
                    token.Credit.Team != shot.Team || shot.Team != paymentSource.Team ||
                    token.Credit.Round != shot.Round || shot.Round != round || round != _match.State.Value.Round ||
                    token.Credit.Equipment != shot.HeroRevision || shot.HeroRevision != paymentSource.HeroRevision ||
                    shot.Lifecycle != paymentSource.Revision || shot.HeroId != paymentSource.HeroId) return;
                if (!terrain.TryContact(collider, point, normal, out var contact) ||
                    !contact.IsBlock || contact.Stable || contact.Revision != revision) return;
                // A shared edge/face can belong to several valid adjacent cubes.
                // Require a surface-compatible occupied witness for the returned
                // contact's cell/owner, without inventing an original query ID.
                // The sample and geometry tolerance use the existing falling
                // contact path and sphere contact oracle.
                const float tolerance = .000004f;
                Vector3 sample = point - normal * .002f; int compatible = 0, selectedMatches = 0, index = -1;
                ulong runId = 0; Vector3Int cell = contact.Cell; byte owner = contact.Owner;
                foreach (var run in world.Falling.Values)
                {
                    float offset = run.Offset(time);
                    if (!float.IsFinite(offset)) continue;
                    for (int i = 0; i < run.Cells.Length; i++)
                    {
                        if (!run.Cells[i].Occupied) continue;
                        var at = run.Bottom + Vector3Int.up * i;
                        var center = BlockWorld.Center(at) - Vector3.up * offset;
                        var cube = new Bounds(center, Vector3.one * BlockWorld.CellSize);
                        var relative = point - center; float half = BlockWorld.CellSize * .5f;
                        float faceError = Mathf.Min(Mathf.Abs(Mathf.Abs(relative.x) - half),
                            Mathf.Abs(Mathf.Abs(relative.y) - half), Mathf.Abs(Mathf.Abs(relative.z) - half));
                        if (!cube.Contains(sample) || Vector3.Distance(cube.ClosestPoint(point), point) > tolerance ||
                            faceError > tolerance || Vector3.Dot(normal, relative) <= 0) continue;
                        compatible++;
                        if (at == contact.Cell && run.Cells[i].Owner == contact.Owner)
                        {
                            selectedMatches++;
                            if (index < 0) { runId = run.Id; index = i; }
                        }
                    }
                }
                if (selectedMatches < 1 || !ReferenceEquals(world, terrain.World) ||
                    terrain.SimulationTime != time || world.Revision != revision || world.Round != round) return;
                Count++;
                if (_witnesses.Count < 8)
                    _witnesses.Add(FormattableString.Invariant($"published-moving shot={shot.Id} round={round} revision={revision} time={time:R} run={runId} index={index} cell=({cell.x},{cell.y},{cell.z}) owner={owner} compatibleCells={compatible} selectedMatches={selectedMatches} originalQueryIdentity=notObserved point=({point.x:R},{point.y:R},{point.z:R}) normal=({normal.x:R},{normal.y:R},{normal.z:R}) age={age:R} paid=true proxy={collider.GetInstanceID()} locomotionCollider=false"));
            }
            public void Dispose()
            {
                if (_match != null) _match.Projectiles.ContactObserved -= Observe;
            }
        }
    }
}
#endif
