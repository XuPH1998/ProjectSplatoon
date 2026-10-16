#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SimpleJSON;
using Cysharp.Threading.Tasks;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine;

namespace Splatoon.Tests
{
    // Overlay only the approved tuning on in-memory historical expectations.
    // The original migration records on disk remain immutable.
    public static class PistolGirlTuningFixture
    {
        public const string AssetPath = "Assets/GameResource/Weapons/PistolGirl/PistolGirlWeaponConfig.asset";
        public const string RecordPath = "Tools/ValidationData/WeaponAssets/PistolGirl-SplashTuning.json";
        public static void Apply(int hero, JSONNode expected, bool referenceFrames = false)
        {
            if (hero != 4) return;
            var record = JSONNode.Parse(File.ReadAllText(RecordPath));
            var values = record["values"];
            foreach (string key in record["preservedValues"].Keys) values[key] = record["preservedValues"][key];
            if (referenceFrames) WeaponTimeFixture.ToReferenceFrames(values);
            foreach (string key in values.Keys) expected[key] = values[key];
        }
    }

    public sealed class PistolGirlTuningTests
    {
        static bool UsesRuntimeBootstrap => TestContext.CurrentContext.Test.MethodName
            == nameof(FixedAimPaintRecordsFloorConnectivityAndWallCoverage);
        [SetUp] public void Setup()
        {
            // UTF replays ordinary SetUp after the EnterPlayMode domain reload.
            // This real Boot case owns its configuration through the app lifecycle.
            if (UsesRuntimeBootstrap)
            {
                TestContext.WriteLine($"[PISTOL-BOOTSTRAP-LIFECYCLE] stage=setup playing={Application.isPlaying} appPresent={PrototypeApp.Current != null} appReady={PrototypeApp.Current?.Ready ?? false} appBusy={PrototypeApp.Current?.Busy ?? false}");
                return;
            }
            HeroMigrationTests.Load();
        }
        [TearDown] public void Cleanup()
        {
            if (UsesRuntimeBootstrap)
            {
                TestContext.WriteLine($"[PISTOL-BOOTSTRAP-LIFECYCLE] stage=cleanup playing={Application.isPlaying} appPresent={PrototypeApp.Current != null} appReady={PrototypeApp.Current?.Ready ?? false} appBusy={PrototypeApp.Current?.Busy ?? false}");
                return;
            }
            LubanConfigService.Current.Reset();
        }
        static PlayerSnapshot Alive() => new() { HeroId = 4, Health = 100, Ink = 100, Grounded = true, Team = 1 };
        static PlayerInputFrame Input(int tick, bool held = true) => new() { Sequence = (uint)tick + 1, FireSequence = 1, Fire = held };

        [Test] public void HeldFireEmitsEveryFiveTicksAndKeepsGroundAirAndLandingAccurate()
        {
            var w = GameplayConfig.GetWeapon(4); var s = Alive(); var ticks = new List<int>();
            for (int tick = 0; tick < 180; tick++)
            {
                s.Grounded = tick < 60 || tick >= 120;
                if (WeaponSimulation.Step(ref s, Input(tick), w, tick / 60.0, false, true))
                {
                    ticks.Add(tick);
                    Assert.That(s.LastShotSpread, Is.Zero);
                    Assert.That(s.LastShotVerticalSpread, Is.Zero);
                    uint seed = (uint)tick + 123;
                    Assert.That(Vector3.Angle(Vector3.forward, InkBallistics.LaunchVelocity(Vector3.forward, w, ref seed, s.LastShotSpread)), Is.LessThan(.001f));
                }
                Assert.That(s.CurrentSpread, Is.Zero); Assert.That(s.CurrentVerticalSpread, Is.Zero);
            }
            Assert.That(ticks, Is.EqualTo(Enumerable.Range(0, 36).Select(i => 2 + i * 5)));
            Assert.That(s.Ink, Is.EqualTo(100 - 36 * .8f).Within(.001f));
            Assert.That((ticks[3] - ticks[0]) / 60.0, Is.EqualTo(.25));
        }

        [TestCase("release")] [TestCase("empty")] [TestCase("death")] [TestCase("cancel")] [TestCase("submerged")]
        public void ContinuousFireStopsWithoutExtraShots(string reason)
        {
            var w = GameplayConfig.GetWeapon(4); var s = Alive();
            for (int tick = 0; tick <= 12; tick++) WeaponSimulation.Step(ref s, Input(tick), w, tick / 60.0, false, true);
            uint before = s.ShotSequence;
            if (reason == "empty") s.Ink = .79f;
            if (reason == "death") s.Health = 0;
            if (reason == "submerged") s.Swimming = true;
            for (int tick = 13; tick < 75; tick++)
            {
                var input = Input(tick, reason != "release"); input.CancelFire = reason == "cancel";
                Assert.That(WeaponSimulation.Step(ref s, input, w, tick / 60.0, false, !s.Swimming), Is.False);
            }
            Assert.That(s.ShotSequence, Is.EqualTo(before)); Assert.That(s.Ink, Is.GreaterThanOrEqualTo(0));
        }

        [Test] public void FinalShotLocksRecoveryForOneThirdSecond()
        {
            var w = GameplayConfig.GetWeapon(4); var s = Alive();
            for (int tick = 0; tick <= 12; tick++) WeaponSimulation.Step(ref s, Input(tick), w, tick / 60.0, false, true);
            Assert.That(s.InkRecoverAt, Is.EqualTo(12.0 / 60 + 1.0 / 3).Within(1e-8));
            for (int tick = 13; tick <= 32; tick++)
            {
                WeaponSimulation.Step(ref s, Input(tick, false), w, tick / 60.0, false, true);
                float before = s.Ink;
                ResourceSimulation.Step(ref s, GameplayConfig.GetHero(4), false, false, 1f / 60, tick / 60.0);
                Assert.That(s.Ink - before, Is.EqualTo(tick < 32 ? 0 : 10f / 60).Within(.00001f));
            }
        }

        [TestCase(0, 4)] [TestCase(4, 4)] [TestCase(8, 5)] [TestCase(12, 5)] [TestCase(16, 6)] [TestCase(20, 8)]
        public void ReferenceAgeProducesApprovedDamageBands(int frame, int hits)
        {
            var w=GameplayConfig.GetWeapon(4); double age=frame/60.0;
            float damage=WeaponSimulation.Damage(w,age);
            Assert.That(Mathf.CeilToInt(100/damage),Is.EqualTo(hits));
            float distance=InkBallistics.Position(Vector3.zero,Vector3.forward*w.SpeedMin,w,age).z;
            Assert.That(InkBallistics.AgeAtDistance(w,w.SpeedMin,distance),Is.EqualTo(age).Within(.00001));
            Assert.That(InkBallistics.Position(Vector3.zero,Vector3.forward*w.SpeedMin,w,age).y,frame<=2?Is.EqualTo(0):Is.LessThan(0));
        }

        [Test] public void ShotAnimationFinishesBeforeTheNextAutomaticRound()
        {
            var w = GameplayConfig.GetWeapon(4);
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>("Assets/GameResource/Characters/PistolGirl/PistolGirlPresentation.asset");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/GameResource/Characters/PistolGirl/Animations/PistolGirlCombat.controller");
            var shot = controller.layers[1].stateMachine.states.Single(s => s.state.name == "Shot").state;
            Assert.That(profile.SingleShot, Is.True, "Per-shot presentation remains separate from automatic gameplay");
            Assert.That(profile.ShotPlaybackSeconds, Is.EqualTo(.9f / w.FireRate).Within(.000001f));
            Assert.That(((AnimationClip)shot.motion).length / shot.speed, Is.EqualTo(profile.ShotPlaybackSeconds).Within(.000001f));
            Assert.That(profile.ShotPlaybackSeconds, Is.LessThan(WeaponSimulation.FireInterval(w)));
        }

        [UnityTest, Timeout(600000)] public IEnumerator FixedAimPaintRecordsFloorConnectivityAndWallCoverage()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            // UTF restores the outer enumerator's program counter, not its
            // managed closure fields. Allocate the scenario after that reload.
            yield return FixedAimScenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator FixedAimScenario()
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypeApp.Current == null || !PrototypeApp.Current.Ready) && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(PrototypeApp.Current?.Ready, Is.True, PrototypeApp.Current?.Error);
            var app = PrototypeApp.Current;
            yield return app.StartWeaponDebugRoom().ToCoroutine();
            end = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypePlayer.Local == null || PrototypeMatch.Current?.Blocks == null) && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(PrototypePlayer.Local, Is.Not.Null); Assert.That(PrototypeMatch.Current?.Blocks, Is.Not.Null);
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            host.RequestHeroChange(4, HeroSelectionOrigin.Warmup);
            end = Time.realtimeSinceStartupAsDouble + 45;
            while ((host.HeroChangePending || host.Snapshot.Value.HeroId != 4) && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(host.Snapshot.Value.HeroId, Is.EqualTo(4));
            app.CaptureMouse(false); match.enabled = false; foreach (var p in match.Players) p.enabled = false;
            using var blocks = new BlockDefaultContractFixture(match);
            var remote = host.Snapshot.Value; remote.Team = 1; remote.Position = new Vector3(12, .05f, -20);
            host.Snapshot.Value = remote; new PlayerMotorSimulation(host.GetComponent<CharacterController>()).Restore(remote);
            Physics.SyncTransforms(); yield return blocks.Seed(blocks.Empty());
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10,
                PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer)), Is.True);
            Assert.That(floor.normal.y, Is.GreaterThan(.99));
            var weapon = GameplayConfig.GetWeapon(4); uint id = 470000;
            uint paintBefore = match.PaintSequence; int added = 0; double points = host.Snapshot.Value.SpecialPoints;
            double materialIssued = 0; int budgetIssued = 0;
            var service = new InkProjectileService { PaintObserved = _ => Assert.Fail("Paid default marks cannot paint surface ownership") };
            foreach (bool wall in new[] { false, true })
            {
                if (wall) yield return blocks.Seed(blocks.Empty());
                RaycastHit target = floor;
                if (wall)
                {
                    Assert.That(Physics.Raycast(new Vector3(12, 1.4f, -29), Vector3.back, out target, 5,
                        PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer)), Is.True);
                    Assert.That(target.collider.GetComponentInParent<PaintSurface>(), Is.Not.Null);
                    Assert.That(Mathf.Abs(target.normal.y), Is.LessThan(.01));
                    Assert.That(match.Arena.IsInHeroChangeZone(1, target.point) || match.Arena.IsInHeroChangeZone(2, target.point), Is.False);
                    added = 0; points = host.Snapshot.Value.SpecialPoints;
                }
                for (int shotIndex = 0; shotIndex < 12; shotIndex++)
                {
                    service.Clear(); int count = blocks.Terrain.PinkCount;
                    var shot = blocks.PaidShot(host, 4, ++id, target.point + target.normal * 1.4f, -target.normal * weapon.SpeedMin);
                    var token = shot.BlockAttack; float material = host.Snapshot.Value.Ink; double beforePoints = host.Snapshot.Value.SpecialPoints;
                    materialIssued += .8f;
                    int cumulativeBudget = (int)Math.Floor(materialIssued * 32 + 1e-8);
                    Assert.That(token.BuildLimit, Is.EqualTo(cumulativeBudget - budgetIssued), "Each authored .8 material payment retains its fractional 32-cell allowance across shots");
                    budgetIssued = cumulativeBudget;
                    service.SpawnForMeasurement(shot); service.Simulate(shot.Born + weapon.Lifetime + 2);
                    Assert.That(service.Impacts.Any(i => i.Id == shot.Id && i.Hit), Is.True, "Fixed aim physically reaches the floor/wall or the preceding layer");
                    yield return blocks.Publish(() => token.Added > 0);
                    Assert.That(token.Added, Is.InRange(1, token.BuildLimit));
                    Assert.That(token.RemovedEnemy, Is.Zero); Assert.That(blocks.Terrain.BlueCount, Is.Zero);
                    Assert.That(blocks.Terrain.PinkCount - count, Is.EqualTo(token.Added));
                    Assert.That(host.Snapshot.Value.Ink, Is.EqualTo(material), "Publication never charges the paid shot twice");
                    Assert.That(host.Snapshot.Value.SpecialPoints - beforePoints,
                        Is.EqualTo(Math.Min(2, token.Added / 32.0)).Within(.00001));
                    Assert.That(match.PaintSequence, Is.EqualTo(paintBefore)); added += token.Added;
                }
                // Final checks require all accepted construction and gravity to settle.
                yield return blocks.Publish(() => blocks.Terrain.UnstableColumns == 0
                    && blocks.Terrain.FallingCount == 0 && blocks.Terrain.World.Falling.Count == 0
                    && blocks.Terrain.World.Cohorts.Count == 0, maximumFrames: 1800);
                // The retained scale is 12 paid fixed-aim shots per receiving plane.
                Assert.That(blocks.Terrain.PinkCount, Is.EqualTo(added));
                Assert.That(host.Snapshot.Value.SpecialPoints - points, Is.EqualTo(added / 32.0).Within(.00001));
                var owners = PaidBlockHostPlayRig.StableOwners(blocks.Terrain.World);
                Assert.That(owners.Count, Is.GreaterThan(0));
                Assert.That(owners.Count, Is.EqualTo(blocks.Terrain.PinkCount), "The complete surviving result is now stable");
                PaidBlockHostPlayRig.AssertStableCollision(blocks.Terrain);
                if (!wall)
                {
                    var occupied = new HashSet<Vector3Int>(owners.Keys);
                    var seen = new HashSet<Vector3Int>(); var pending = new Queue<Vector3Int>();
                    var center = owners.Keys.OrderBy(c => Vector3.Distance(BlockWorld.Center(c), floor.point)).First();
                    pending.Enqueue(center); seen.Add(center);
                    while (pending.Count > 0)
                    {
                        var c = pending.Dequeue();
                        foreach (var d in new[] { Vector3Int.up, Vector3Int.down, Vector3Int.right, Vector3Int.left, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) })
                            if (occupied.Contains(c + d) && seen.Add(c + d)) pending.Enqueue(c + d);
                    }
                    TestContext.WriteLine("[PISTOL-SETTLED-TOPOLOGY] " +
                        "Face connectivity is diagnostic; every column requires native map support"
                        + $" floor={floor.point:R} seed={center} stable={occupied.Count} connected={seen.Count} falling={blocks.Terrain.FallingCount}"
                        + $" pending={blocks.Terrain.PendingTransactions} unstableColumns={blocks.Terrain.UnstableColumns} cohorts={blocks.Terrain.World.Cohorts.Count}"
                        + " disconnected=" + string.Join(";", occupied.Where(c => !seen.Contains(c)).OrderBy(c => c.y).ThenBy(c => c.x).ThenBy(c => c.z)
                            .Take(16).Select(c =>
                            {
                                bool mapHit = Physics.Raycast(BlockWorld.Center(c), Vector3.down, out var proof, 40,
                                    PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore);
                                return $"{c} bottom={c.y * BlockWorld.CellSize:R} below={occupied.Contains(c + Vector3Int.down)}"
                                    + $" mapHit={mapHit} sameFloor={mapHit && proof.collider == floor.collider}"
                                    + $" mapPoint={proof.point:R} mapNormal={proof.normal:R} bottomGap={proof.distance - BlockWorld.CellSize * .5f:R}";
                            })));
                    var registeredMap = (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(blocks.Terrain);
                    Assert.That(registeredMap, Is.Not.Null, "Use the actual arena map registered by terrain initialization");
                    Assert.That(floor.point.y, Is.EqualTo(0).Within(.0001f), "This fixed-floor scenario uses the real arena zero plane");
                    foreach (var column in owners.Keys.GroupBy(c => new Vector2Int(c.x, c.z)))
                    {
                        var cells = column.OrderBy(c => c.y).ToArray();
                        for (int i = 1; i < cells.Length; i++)
                            Assert.That(cells[i].y, Is.EqualTo(cells[i - 1].y + 1), $"No unsupported gap in floor column {column.Key}");
                        var root = cells[0];
                        Assert.That(Physics.Raycast(BlockWorld.Center(root), Vector3.down, out var mapProof, BlockWorld.CellSize * 1.5f,
                            PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore), Is.True,
                            $"Floor column {column.Key} has a native map root");
                        Assert.That(registeredMap.Contains(mapProof.collider), Is.True, $"Root {root} uses an actually registered static map collider");
                        Assert.That(mapProof.collider.GetComponentInParent<PrototypeArena>(), Is.SameAs(match.Arena), "Root belongs to this real arena");
                        Assert.That(mapProof.normal.y, Is.GreaterThan(.99f), $"Root {root} rests on the receiving ground plane");
                        Assert.That(mapProof.point.y, Is.EqualTo(floor.point.y).Within(.0001f), "Adjacent map tiles may supply the same zero plane");
                        Assert.That(root.y * BlockWorld.CellSize, Is.EqualTo(mapProof.point.y).Within(.0001f), $"Root {root} has no unsupported vertical gap");
                    }
                    Assert.That(owners.Keys.Select(c => c.y).Distinct().Count(), Is.GreaterThan(1), "Repeated impacts physically build height");
                }
                else Assert.That(owners.Keys.Any(c => blocks.Terrain.World.TryGet(c, out var value) && value.Anchored), Is.True,
                    "The real map wall supplies anchored first-layer support");
            }
            blocks.Dispose(); yield return app.Leave().ToCoroutine();
        }
        [UnityTearDown] public IEnumerator CleanupPlayMode()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }

        static float ConnectedInkForward(PaintSurface floor, Vector3 origin)
        {
            var grid = floor.Ownership; int seed = -1; float nearest = float.PositiveInfinity;
            for (int i = 0; i < grid.Cells.Length; i++)
            {
                if (grid.Cells[i] != 1) continue;
                var point = floor.transform.TransformPoint(grid.Center(i)) - origin;
                float distance = (point - Vector3.forward).sqrMagnitude;
                if (distance < nearest) { nearest = distance; seed = i; }
            }
            Assert.That(seed, Is.GreaterThanOrEqualTo(0));
            Assert.That(nearest, Is.LessThan(.25f));
            var seen = new bool[grid.Cells.Length]; var pending = new Queue<int>(); pending.Enqueue(seed); seen[seed] = true;
            float forward = 0;
            while (pending.Count > 0)
            {
                int i = pending.Dequeue(), x = i % grid.Columns, z = i / grid.Columns;
                forward = Mathf.Max(forward, (floor.transform.TransformPoint(grid.Center(i)) - origin).z);
                void Visit(int n) { if (!seen[n] && grid.Cells[n] == 1) { seen[n] = true; pending.Enqueue(n); } }
                if (x > 0) Visit(i - 1); if (x + 1 < grid.Columns) Visit(i + 1);
                if (z > 0) Visit(i - grid.Columns); if (z + 1 < grid.Rows) Visit(i + grid.Columns);
            }
            return forward;
        }
    }
}
#endif
