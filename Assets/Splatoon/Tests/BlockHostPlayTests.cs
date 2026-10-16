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
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>Default block mode has separate acceptance; legacy paint gates keep their original assertions.</summary>
    public sealed class BlockHostPlayTests
    {
        const float Dt = 1f / 60;
        static IEnumerator Wait(Func<bool> ready, string label, double seconds = 45)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (!ready() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        static int Highest(BlockWorld world) => world.Chunks.SelectMany(pair => Enumerable.Range(0, BlockWorld.CellsPerChunk)
            .Where(i => pair.Value.Cells[i].Occupied).Select(i => BlockWorld.CellFromIndex(pair.Key, i).y)).DefaultIfEmpty(int.MinValue).Max();
        static void PublishScore(PrototypeMatch match)
        {
            typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(match, null);
            Assert.That(match.State.Value.PinkBlockCount, Is.EqualTo(match.Blocks.PinkCount));
            Assert.That(match.State.Value.BlueBlockCount, Is.EqualTo(match.Blocks.BlueCount));
            Assert.That(match.State.Value.TerrainRevision, Is.EqualTo(match.Blocks.Revision));
        }
        [UnityTest, Timeout(600000)] public IEnumerator RealHostPaidProjectileAtomicStackBlastGravityAndScore()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            // Unity restores only the wrapper's iterator state after the domain reload.
            // Create every scenario closure after entering Play Mode.
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator Scenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks != null && InkPresentation.Current != null, "Default block host");
            var match = PrototypeMatch.Current; var host = PrototypePlayer.Local; var presentation = InkPresentation.Current;
            Assert.That(match, Is.Not.Null, "Current match after connection"); Assert.That(host, Is.Not.Null, "Current host after connection");
            Assert.That(presentation, Is.Not.Null, "Current impact presentation after connection");
            var terrain = match.Blocks; terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            match.enabled = false; foreach (var player in match.Players) player.enabled = false; app.CaptureMouse(false);
            var serverTick = (Action)Delegate.CreateDelegate(typeof(Action), match,
                typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic));
            match.NetworkManager.NetworkTickSystem.Tick -= serverTick;
            double now = match.NetworkManager.ServerTime.Time;
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10, PlayerMotorSimulation.WorldMask), Is.True, "Actual map floor");
            var point = floor.point; var center = BlockWorld.CellAt(point + Vector3.up * .05f); uint round = match.State.Value.Round;
            bool checkCollision = true, checkBudget = true; int commits = 0; var chunksPerFrame = new Dictionary<int, int>();
            void Published(BlockTerrainCommit commit)
            {
                commits++; Assert.That(terrain.World.Revision, Is.EqualTo(commit.Revision));
                Assert.That(match.State.Value.TerrainRevision, Is.EqualTo(commit.Revision), "the match snapshot publishes the same terrain version before observers run");
                Assert.That(match.State.Value.PinkBlockCount, Is.EqualTo(terrain.PinkCount));
                Assert.That(match.State.Value.BlueBlockCount, Is.EqualTo(terrain.BlueCount));
                if (checkBudget)
                {
                    chunksPerFrame.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length; chunksPerFrame[Time.frameCount] = count;
                    Assert.That(count, Is.LessThanOrEqualTo(8), "Authority publications respect the eight-chunk frame budget");
                }
                if (!checkCollision || terrain.PinkCount == 0) return;
                Assert.That(Physics.Raycast(point + Vector3.up * 6, Vector3.down, out var hit, 12, 1 << BlockTerrain.TerrainLayer), Is.True, "Published cells have active collision");
                Assert.That(terrain.TryContact(hit, out var contact), Is.True);
                Assert.That(terrain.World.TryGet(contact.Cell, out var cell), Is.True);
                Assert.That(cell.Owner, Is.EqualTo(contact.Owner), "World, renderer collision, contact and revision publish together");
            }
            terrain.PublishedCommit += Published;
            IEnumerator Pump(Func<bool> ready, bool projectile = false, int maximumFrames = 1200)
            {
                for (int frame = 0; frame < maximumFrames && !ready(); frame++)
                {
                    now += Dt; if (projectile) match.Projectiles.Simulate(now); terrain.Tick(true, now, Dt); yield return null;
                }
                Assert.That(ready(), Is.True, "Queued authority terrain work completes");
            }
            try
            {
                terrain.Clear(round); BlockAttackService.Clear(); match.Projectiles.Clear(); presentation.Clear();
                var state = host.Snapshot.Value; state.HeroId = 1; state.Team = 1; state.Health = 100; state.LifeState = PlayerLifeState.Alive;
                state.SpecialWeaponId = 1; state.SpecialPhase = SpecialPhase.Charging; state.SpecialChargeLockedUntil = 0; state.SpecialPoints = 0;
                state.ShotSequence++; state.LastShotCharge = 0; state.Position = point + Vector3.back * 6 + Vector3.up;
                var controller = host.GetComponent<CharacterController>(); controller.enabled = false; host.transform.position = state.Position; controller.enabled = true;
                var weapon = GameplayConfig.GetWeapon(1); float payment = WeaponSimulation.InkCost(weapon); state.Ink = 100 - payment; host.Snapshot.Value = state; Physics.SyncTransforms();
                var token = BlockAttackService.Main(host.PlayerId, state, round, weapon); var original = terrain.World; uint originalPaint = match.AppliedPaintSequence;
                Assert.That(token.BuildLimit, Is.GreaterThan(0));
                match.Projectiles.SpawnForMeasurement(new InkShot { Id = 800001, Round = round, Shooter = host.PlayerId, HeroId = 1, Team = 1,
                    Lifecycle = state.Revision, HeroRevision = state.HeroRevision, ShotSequence = state.ShotSequence, ActionId = state.ShotSequence,
                    Seed = 117, Born = now, Origin = point + Vector3.up * 1.2f, Velocity = Vector3.down * 32, Configuration = weapon, BlockAttack = token });
                Assert.That(terrain.PinkCount, Is.Zero, "A spawned shot cannot score unpublished cells"); Assert.That(host.Snapshot.Value.SpecialPoints, Is.Zero);
                yield return Pump(() => token.Added > 0 && terrain.PendingTransactions == 0, true);
                Assert.That(original.PinkCount, Is.Zero, "The previous world remains immutable across publication");
                Assert.That(token.Added, Is.InRange(1, token.BuildLimit)); Assert.That(terrain.PinkCount, Is.EqualTo(token.Added));
                Assert.That(host.Snapshot.Value.Ink, Is.EqualTo(100 - payment).Within(.001), "Terrain callbacks do not charge the material payment again");
                Assert.That(host.Snapshot.Value.SpecialPoints, Is.EqualTo(Math.Min(token.PointLimit, token.Added / (double)token.BlocksPerPoint)).Within(.00001));
                Assert.That(match.AppliedPaintSequence, Is.EqualTo(originalPaint), "Default block impacts do not produce surface paint");
                PublishScore(match); int beforeHeight = Highest(terrain.World), beforeCount = terrain.PinkCount;

                state = host.Snapshot.Value; state.ShotSequence++; state.Ink -= payment; host.Snapshot.Value = state;
                var second = BlockAttackService.Main(host.PlayerId, state, round, weapon);
                BlockAttackService.Build(second, point, Vector3.up, .22f, false, true, entityId: 800002, eventOrdinal: 1);
                int reservedBalance = second.BuildRemaining;
                BlockAttackService.Build(second, point, Vector3.up, .22f, false, true, entityId: 800002, eventOrdinal: 1);
                Assert.That(second.BuildRemaining, Is.EqualTo(reservedBalance), "Replaying the same impact cannot reserve another payment");
                yield return Pump(() => second.Added > 0 && terrain.PendingTransactions == 0);
                Assert.That(terrain.PinkCount - beforeCount, Is.EqualTo(second.Added)); Assert.That(second.Added, Is.LessThanOrEqualTo(second.BuildLimit));
                Assert.That(Highest(terrain.World), Is.GreaterThan(beforeHeight), "Repeated fire stacks real cubes above existing friendly cubes"); PublishScore(match);

                checkCollision = false; checkBudget = false;
                var blastSeed = new BlockWorld(terrain.World.Dimensions, round, terrain.World.Bottom);
                for (int x = -4; x <= 4; x++) { blastSeed.Set(center + new Vector3Int(x, 0, -8), BlockCell.Create(1, true)); blastSeed.Set(center + new Vector3Int(x, 0, 8), BlockCell.Create(2, true)); }
                Assert.That(terrain.QueueSeed(blastSeed), Is.True); yield return Pump(() => terrain.PendingTransactions == 0);
                yield return null; checkBudget = true;
                bool blasted = false; BlockMutationResult blastResult = default;
                terrain.QueueDestroy(BlockWorld.Center(center), 1, .55f, 1, false, result => { blastResult = result; blasted = true; });
                yield return Pump(() => blasted && terrain.PendingTransactions == 0);
                Assert.That(blastResult.RemovedEnemy, Is.EqualTo(9)); Assert.That(blastResult.RemovedFriendly, Is.Zero);
                Assert.That(terrain.PinkCount, Is.EqualTo(9)); Assert.That(terrain.BlueCount, Is.Zero, "Same-distance friendly cubes survive the smaller friendly blast radius"); PublishScore(match);

                // Use the loaded BurstBomb, its paid input simulation, and the real
                // SubWeaponService collision/explosion path. Seed changes themselves pay no credit.
                match.SubWeapons.Clear();
                state = host.Snapshot.Value; state.SpecialPoints = 0; state.Ink = 100; host.Snapshot.Value = state;
                Assert.That(host.PlayerId, Is.Zero, "Sub block credit also reaches the real host owner zero");
                SubWeaponService.Entity PaidBurst()
                {
                    var s = host.Snapshot.Value; s.SubWeaponId = SubWeaponConfigService.Id(SubWeaponType.BurstBomb);
                    s.SubPhase = SubWeaponPhase.Idle; s.SubNeedsRelease = false;
                    s.SubReadyAt = s.SubRecoveryUntil = s.AttackRecoveryUntil = 0;
                    var config = PlayerLoadout.SubWeapon(s); float inkBefore = s.Ink;
                    var input = new PlayerInputFrame { HeroRevision = s.HeroRevision, SubHeld = true,
                        SubPressSequence = s.SubConsumedPress + 1, SubReleaseSequence = s.SubConsumedRelease };
                    Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, now, true,
                        match.SubWeapons.CanUse(host.PlayerId, s, config), out _), Is.False, "Holding cannot pay a sub attack");
                    now += Dt; input.SubHeld = false; input.SubReleaseSequence++;
                    Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, now, true,
                        match.SubWeapons.CanUse(host.PlayerId, s, config), out _), Is.False, "Release retains the authored startup");
                    now += config.Common.startup + Dt;
                    Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, now, true,
                        match.SubWeapons.CanUse(host.PlayerId, s, config), out float charge), Is.True, "Actual sub input commits once");
                    Assert.That(s.Ink, Is.EqualTo(inkBefore - config.Common.inkCost).Within(.001));
                    host.Snapshot.Value = s;
                    var entity = match.SubWeapons.Spawn(host, s, charge, now, round, new SubLaunchSolution {
                        Position = BlockWorld.Center(center) + Vector3.up * .8f, Velocity = Vector3.down * 6, Normal = Vector3.up });
                    Assert.That(entity, Is.Not.Null); Assert.That(entity.BlockAttack, Is.Not.Null);
                    Assert.That(entity.BlockAttack.Credit.Kind, Is.EqualTo(PaintAttackKind.Sub));
                    Assert.That(match.SubWeapons.Lifecycle.Any(e => e.State.Id == entity.State.Id && e.Kind == SubLifecycleKind.Spawn), Is.True);
                    return entity;
                }
                foreach (bool enemyPresent in new[] { true, false })
                {
                    checkBudget = false;
                    var subSeed = new BlockWorld(terrain.World.Dimensions, round, terrain.World.Bottom);
                    for (int x = -4; x <= 4; x++)
                    {
                        subSeed.Set(center + new Vector3Int(x, 0, -3), BlockCell.Create(1, true));
                        if (enemyPresent) subSeed.Set(center + new Vector3Int(x, 0, 3), BlockCell.Create(2, true));
                    }
                    double pointsBefore = host.Snapshot.Value.SpecialPoints;
                    Assert.That(terrain.QueueSeed(subSeed), Is.True); yield return Pump(() => terrain.PendingTransactions == 0);
                    yield return null; checkBudget = true;
                    Assert.That(host.Snapshot.Value.SpecialPoints, Is.EqualTo(pointsBefore), "Fixture publication cannot award attack credit");
                    int pinkBefore = terrain.PinkCount, blueBefore = terrain.BlueCount; uint subPaint = match.PaintSequence;
                    var publishedBeforeBlast = terrain.World; var sub = PaidBurst(); float paidSubInk = host.Snapshot.Value.Ink;
                    for (int frame = 0; frame < 180 && !match.SubWeapons.Effects.Any(e => e.EntityId == sub.State.Id && e.Kind == SubEffectKind.Explosion); frame++)
                    {
                        now += Dt; match.SubWeapons.Step(now, Dt, match.Players); yield return null;
                    }
                    Assert.That(match.SubWeapons.Effects.Count(e => e.EntityId == sub.State.Id && e.Kind == SubEffectKind.Explosion), Is.EqualTo(1));
                    Assert.That(sub.Removed, Is.True, "The actual BurstBomb contact ended its entity");
                    Assert.That(terrain.PendingTransactions, Is.GreaterThan(0), "A real service blast enqueues terrain work");
                    Assert.That(terrain.World, Is.SameAs(publishedBeforeBlast));
                    Assert.That(terrain.PinkCount, Is.EqualTo(pinkBefore)); Assert.That(terrain.BlueCount, Is.EqualTo(blueBefore));
                    Assert.That(host.Snapshot.Value.SpecialPoints, Is.EqualTo(pointsBefore), "An emitted explosion cannot credit unpublished removals");
                    yield return Pump(() => terrain.PendingTransactions == 0);
                    int removedEnemy = blueBefore - terrain.BlueCount;
                    Assert.That(pinkBefore - terrain.PinkCount, Is.EqualTo(9), "Nearby friendly cells are really destroyed");
                    Assert.That(removedEnemy, Is.EqualTo(enemyPresent ? 9 : 0));
                    Assert.That(sub.BlockAttack.Added, Is.Zero); Assert.That(sub.BlockAttack.RemovedEnemy, Is.EqualTo(removedEnemy));
                    double expectedCredit = Math.Min(BlockGameplaySettings.SubSpecialPointCap, removedEnemy / (double)BlockGameplaySettings.BlocksPerSpecialPoint);
                    Assert.That(host.Snapshot.Value.SpecialPoints - pointsBefore, Is.EqualTo(expectedCredit).Within(.00001),
                        enemyPresent ? "Only actual enemy removal pays the sub credit" : "Destroying only friendly blocks pays no credit");
                    Assert.That(sub.BlockAttack.AwardedPoints, Is.EqualTo(expectedCredit).Within(.00001));
                    Assert.That(host.Snapshot.Value.Ink, Is.EqualTo(paidSubInk).Within(.001), "Publication cannot charge sub material again");
                    Assert.That(match.PaintSequence, Is.EqualTo(subPaint), "The real sub explosion emits no legacy paint");
                    double pointsAfter = host.Snapshot.Value.SpecialPoints;
                    now += Dt; match.SubWeapons.Step(now, Dt, match.Players); terrain.Tick(true, now, Dt); yield return null;
                    Assert.That(host.Snapshot.Value.SpecialPoints, Is.EqualTo(pointsAfter), "Stepping a removed sub cannot award twice");
                    Assert.That(match.SubWeapons.Effects.Count(e => e.EntityId == sub.State.Id && e.Kind == SubEffectKind.Explosion), Is.EqualTo(1));
                    PublishScore(match);
                }

                checkBudget = false; var gravitySeed = new BlockWorld(terrain.World.Dimensions, round, terrain.World.Bottom);
                for (int y = 0; y <= 28; y++) gravitySeed.Set(center + Vector3Int.up * y, BlockCell.Create((byte)(y % 2 + 1), y == 0));
                Assert.That(terrain.QueueSeed(gravitySeed), Is.True); yield return Pump(() => terrain.PendingTransactions == 0); yield return null; checkBudget = true;
                bool cut = false; BlockMutationResult cutResult = default;
                terrain.QueueDestroy(BlockWorld.Center(center + Vector3Int.up * 10), 1.05f, 1.05f, 1, false, result => { cutResult = result; cut = true; });
                yield return Pump(() => cut && terrain.World.Falling.Count > 0);
                int survivingPink = terrain.PinkCount, survivingBlue = terrain.BlueCount;
                Assert.That(survivingPink + survivingBlue, Is.EqualTo(29 - cutResult.Changed));
                var falling = terrain.World.Falling.Values.First(); float startOffset = falling.Offset(now);
                now += Dt; terrain.Tick(true, now, Dt); yield return null;
                Assert.That(falling.Offset(now), Is.GreaterThan(startOffset), "Unsupported runs move downward under the authority gravity clock");
                PublishScore(match);
                yield return Pump(() => terrain.World.Falling.Count == 0 && terrain.PendingTransactions == 0);
                Assert.That(terrain.PinkCount, Is.EqualTo(survivingPink)); Assert.That(terrain.BlueCount, Is.EqualTo(survivingBlue), "Landing transfers cells without duplicating score");
                Assert.That(terrain.World.Chunks.Values.Sum(chunk => chunk.Count), Is.EqualTo(survivingPink + survivingBlue)); PublishScore(match);

                int pink = terrain.PinkCount, blue = terrain.BlueCount; float health = host.Snapshot.Value.Health; double points = host.Snapshot.Value.SpecialPoints;
                presentation.BlockBurst(point, Vector3.up, 1, 987, now, 2); presentation.UpdateFlights(now + .02, null);
                Assert.That(presentation.BlockDebrisCount, Is.GreaterThan(0)); Assert.That(presentation.HasCompositeContent(1 << 11), Is.False);
                Assert.That(terrain.PinkCount, Is.EqualTo(pink)); Assert.That(terrain.BlueCount, Is.EqualTo(blue));
                Assert.That(host.Snapshot.Value.Health, Is.EqualTo(health)); Assert.That(host.Snapshot.Value.SpecialPoints, Is.EqualTo(points), "Cosmetic block debris has no gameplay effects");
                Assert.That(terrain.FailedTransactions, Is.Zero); Assert.That(commits, Is.GreaterThan(3));
                Directory.CreateDirectory("Reports/BlockTerrain/HostPlayMode");
                File.WriteAllText("Reports/BlockTerrain/HostPlayMode/result.txt", "PASS: real Boot host paid projectile -> block collision publication, budget and capped credit; repeated friendly stacking; smaller friendly blast radius; paid BurstBomb service publishes enemy-removal income and friendly-only zero income; unsupported mixed-team runs fall and land without score duplication; cosmetic debris cannot mutate terrain/health/credit. This gate does not certify a remote client, rendered appearance or performance.");
            }
            finally
            {
                if (match.NetworkManager.NetworkTickSystem != null) match.NetworkManager.NetworkTickSystem.Tick += serverTick;
                terrain.PublishedCommit -= Published; match.Projectiles.Clear(); match.SubWeapons.Clear(); presentation.Clear(); match.enabled = true; foreach (var player in match.Players) if (player != null) player.enabled = true;
            }
            yield return app.Leave().ToCoroutine();
        }
        [UnityTest, Timeout(600000)] public IEnumerator RealMapWallImpactAnchorsOnlyFirstLayerAndUnsupportedExtensionFalls()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return WallAnchorScenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator WallAnchorScenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks != null, "Default block host");
            var match = PrototypeMatch.Current; var terrain = match.Blocks; var host = PrototypePlayer.Local;
            match.enabled = false; foreach (var player in match.Players) player.enabled = false; app.CaptureMouse(false);
            var serverTick = (Action)Delegate.CreateDelegate(typeof(Action), match,
                typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic));
            match.NetworkManager.NetworkTickSystem.Tick -= serverTick;
            double now = match.NetworkManager.ServerTime.Time; uint shotId = 810000;
            Vector3Int outerCell = default; FallingRun observedFalling = null;
            void Published(BlockTerrainCommit commit)
            {
                if (observedFalling != null) return;
                var run = terrain.World.Falling.Values.FirstOrDefault(value => value.Bottom.x == outerCell.x && value.Bottom.z == outerCell.z
                    && outerCell.y >= value.Bottom.y && outerCell.y < value.Bottom.y + value.Cells.Length
                    && value.Cells[outerCell.y - value.Bottom.y].Occupied);
                if (run != null) observedFalling = run;
            }
            IEnumerator Pump(Func<bool> ready, string label)
            {
                for (int frame = 0; frame < 1200 && !ready(); frame++)
                {
                    now += Dt; match.Projectiles.Simulate(now); terrain.Tick(true, now, Dt); yield return null;
                }
                Assert.That(ready(), Is.True, label);
            }
            InkShot Fire(Vector3 origin, Vector3 direction)
            {
                var state = host.Snapshot.Value; state.HeroId = 8; state.ShotSequence++;
                state.FireBurstSequence = state.ShotSequence; state.LastShotCharge = 0;
                var weapon = GameplayConfig.GetWeapon(8); state.Ink -= WeaponSimulation.InkCost(weapon); host.Snapshot.Value = state;
                var token = BlockAttackService.Main(host.PlayerId, state, match.State.Value.Round, weapon);
                Assert.That(token.BuildLimit, Is.GreaterThan(0));
                var shot = new InkShot { Id = ++shotId, Round = match.State.Value.Round, Shooter = host.PlayerId, HeroId = 8, Team = state.Team,
                    Lifecycle = state.Revision, HeroRevision = state.HeroRevision, ShotSequence = state.ShotSequence, ActionId = state.ShotSequence,
                    Seed = 117, Born = now, Origin = origin, Velocity = direction.normalized * 32, Configuration = weapon, BlockAttack = token };
                // Same paid measurement firing helper as the existing Host/weapon integration gates.
                // Resolve/ApplyPaint choose map anchoring versus friendly-block extension; no QueueBuild flags are set by this test.
                match.Projectiles.SpawnForMeasurement(shot); return shot;
            }
            try
            {
                terrain.Clear(match.State.Value.Round); BlockAttackService.Clear(); match.Projectiles.Clear();
                var initial = host.Snapshot.Value; initial.Team = 1; initial.Health = 100; initial.LifeState = PlayerLifeState.Alive;
                initial.Ink = 100; initial.SpecialPoints = 0; initial.SpecialPhase = SpecialPhase.Charging; host.Snapshot.Value = initial;
                float payment = WeaponSimulation.InkCost(GameplayConfig.GetWeapon(8));
                var wall = match.Arena.GetComponentsInChildren<Collider>(true)
                    .Single(value => value.name == "CenterCover_1_-1" && !value.isTrigger);
                Assert.That(wall.enabled && wall.gameObject.activeInHierarchy, Is.True);
                Physics.SyncTransforms();
                var origin = wall.bounds.center + Vector3.right * (wall.bounds.extents.x + .6f);
                Assert.That(Physics.Raycast(origin, Vector3.left, out var mapHit, .8f,
                    PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(mapHit.collider, Is.SameAs(wall), "Use the saved map wall, not a synthetic anchor");
                Assert.That(Vector3.Dot(mapHit.normal, Vector3.right), Is.GreaterThan(.999f));
                var firstShot = Fire(origin, Vector3.left);
                yield return Pump(() => firstShot.BlockAttack.Added > 0 && terrain.PendingTransactions == 0
                    && match.Projectiles.Impacts.Any(value => value.Id == firstShot.Id && value.Hit), "Real map-hit projectile publishes its paid construction");
                var firstImpact = match.Projectiles.Impacts.First(value => value.Id == firstShot.Id && value.Hit);
                Assert.That(Vector3.Distance(firstImpact.Position, mapHit.point), Is.LessThan(.08f));
                Assert.That(Vector3.Dot(firstImpact.Normal, mapHit.normal), Is.GreaterThan(.999f));
                var firstCell = BlockWorld.CellAt(firstImpact.Position + firstImpact.Normal * (BlockWorld.CellSize * .51f));
                Assert.That(terrain.World.TryGet(firstCell, out var first), Is.True);
                Assert.That(first.Owner, Is.EqualTo(1)); Assert.That(first.Anchored, Is.True,
                    "Only the real projectile's map-impact route and StaticAnchor can create this anchor");
                // Find the bottom of this suspended, genuinely generated wall column.
                var wallBottom = firstCell;
                while (terrain.World.TryGet(wallBottom + Vector3Int.down, out _)) wallBottom += Vector3Int.down;
                Assert.That(terrain.World.TryGet(wallBottom, out var wallBase) && wallBase.Anchored, Is.True);
                Assert.That(Physics.Raycast(BlockWorld.Center(wallBottom), Vector3.down, BlockWorld.CellSize * 1.25f,
                    PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore), Is.False,
                    "The generated first layer is suspended, rather than resting on the static floor");
                for (int frame = 0; frame < 30; frame++) { now += Dt; terrain.Tick(true, now, Dt); yield return null; }
                Assert.That(terrain.World.TryGet(firstCell, out first) && first.Anchored, Is.True);
                Assert.That(terrain.World.Falling, Is.Empty);
                Assert.That(firstShot.BlockAttack.Added, Is.InRange(1, firstShot.BlockAttack.BuildLimit));
                Assert.That(terrain.PinkCount, Is.EqualTo(firstShot.BlockAttack.Added));
                Assert.That(host.Snapshot.Value.Ink, Is.EqualTo(100 - payment).Within(.001));

                Assert.That(Physics.Raycast(BlockWorld.Center(firstCell) + Vector3.right * .6f, Vector3.left,
                    out var blockHit, 1, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(terrain.TryContact(blockHit, out var contact), Is.True);
                Assert.That(contact.Cell, Is.EqualTo(firstCell)); Assert.That(contact.Owner, Is.EqualTo(1));
                outerCell = BlockWorld.CellAt(blockHit.point + blockHit.normal * (BlockWorld.CellSize * .51f));
                Assert.That(outerCell, Is.EqualTo(firstCell + Vector3Int.right));
                Assert.That(terrain.World.TryGet(outerCell + Vector3Int.down, out _), Is.False);
                Assert.That(Physics.Raycast(BlockWorld.Center(outerCell), Vector3.down, BlockWorld.CellSize * 1.25f,
                    PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore), Is.False);
                terrain.PublishedCommit += Published;
                var outerShot = Fire(blockHit.point + blockHit.normal * .6f, -blockHit.normal);
                yield return Pump(() => outerShot.BlockAttack.Added > 0 && observedFalling != null
                    && match.Projectiles.Impacts.Any(value => value.Id == outerShot.Id && value.Hit), "Real friendly-block impact generates the falling extension");
                var outerImpact = match.Projectiles.Impacts.First(value => value.Id == outerShot.Id && value.Hit);
                Assert.That(BlockWorld.CellAt(outerImpact.Position + outerImpact.Normal * (BlockWorld.CellSize * .51f)), Is.EqualTo(outerCell));
                int fallingIndex = outerCell.y - observedFalling.Bottom.y;
                Assert.That(observedFalling.Cells[fallingIndex].Owner, Is.EqualTo(1));
                Assert.That(observedFalling.Cells[fallingIndex].Anchored, Is.False);
                Assert.That(outerShot.BlockAttack.Added, Is.InRange(1, outerShot.BlockAttack.BuildLimit));
                int surviving = firstShot.BlockAttack.Added + outerShot.BlockAttack.Added;
                Assert.That(terrain.PinkCount, Is.EqualTo(surviving), "Published falling cells remain alive and count");
                Assert.That(host.Snapshot.Value.Ink, Is.EqualTo(100 - 2 * payment).Within(.001));
                float before = observedFalling.Offset(now);
                now += .1; terrain.Tick(true, now, .1f); yield return null;
                Assert.That(observedFalling.Offset(now), Is.GreaterThan(before));
                yield return Pump(() => terrain.World.Falling.Count == 0 && terrain.PendingTransactions == 0, "Extension lands");
                Assert.That(terrain.World.TryGet(firstCell, out first) && first.Anchored, Is.True);
                Assert.That(terrain.World.TryGet(outerCell, out _), Is.False);
                Assert.That(terrain.PinkCount, Is.EqualTo(surviving)); Assert.That(terrain.BlueCount, Is.Zero);
                Assert.That(Physics.Raycast(BlockWorld.Center(outerCell) + Vector3.up * .5f, Vector3.down,
                    out var landingHit, 4, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(terrain.TryContact(landingHit, out var landed), Is.True);
                Assert.That(landed.Cell.x, Is.EqualTo(outerCell.x)); Assert.That(landed.Cell.z, Is.EqualTo(outerCell.z));
                Assert.That(landed.Cell.y, Is.LessThan(outerCell.y)); Assert.That(landed.Owner, Is.EqualTo(1));
                Assert.That(terrain.FailedTransactions, Is.Zero);
            }
            finally
            {
                terrain.PublishedCommit -= Published;
                if (match.NetworkManager.NetworkTickSystem != null) match.NetworkManager.NetworkTickSystem.Tick += serverTick;
                match.enabled = true; foreach (var player in match.Players) if (player != null) player.enabled = true;
            }
            yield return app.Leave().ToCoroutine();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
    }
}
#endif
