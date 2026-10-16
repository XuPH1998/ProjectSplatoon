#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // Reports-only service accounting integration. QueueSeed supplies diagnostic
    // anchored targets; it does not prove these fixtures were built by gameplay.
    // Paid Main/Sub issuance and every construction/destruction use public
    // production entry points. Reflection only reads real queue/checkpoint state.
    public sealed class BlockBlastBatchPaidCreditTests
    {
        const float Dt = 1f / 60;
        const int PumpLimit = 4000;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PrototypeApp _app;
        PrototypeMatch _match;
        PrototypePlayer _host, _enemy;
        BlockTerrain _terrain;
        Action _serverTick;
        double _now;
        Vector3 _floor;
        bool _captureBlasts;
        readonly List<CreditObservation> _observations = new();
        readonly Dictionary<int, int> _frameChunks = new();
        Vector3Int[] _centres;

        readonly struct CreditObservation
        {
            public readonly BlockMutationResult Result;
            public readonly double HostPoints, EnemyPoints;
            public readonly bool CentresAbsentFromWorldAndCollision;
            public CreditObservation(BlockMutationResult result, double host, double enemy, bool absent)
            { Result = result; HostPoints = host; EnemyPoints = enemy; CentresAbsentFromWorldAndCollision = absent; }
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator EightPublicServiceBlastsPayPublishedMixedTeamResultsOnceWithinSharedCaps()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return Leave();
            yield return new ExitPlayMode();
        }

        IEnumerator Scenario()
        {
            // Captured accounting locals belong to this post-EnterPlay iterator;
            // the test framework does not preserve its display class across reload.
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            _app = PrototypeApp.Current; yield return _app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks != null, "Real block host");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local; _terrain = _match.Blocks;
            _enemy = _match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            Assert.That(_enemy, Is.Not.Null); Assert.That(_enemy.IsServer, Is.True);
            _app.CaptureMouse(false); _match.enabled = false;
            // NGO invokes its subscribed Tick even when the MonoBehaviour is disabled.
            _serverTick = (Action)Delegate.CreateDelegate(typeof(Action), _match,
                typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic));
            _match.NetworkManager.NetworkTickSystem.Tick -= _serverTick;
            foreach (var player in _match.Players) player.enabled = false;
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10, PlayerMotorSimulation.WorldMask), Is.True);
            _floor = floor.point; _now = _match.NetworkManager.ServerTime.Time;
            Place(_host, _floor + Vector3.back * 8, 1); Place(_enemy, _floor + Vector3.right * 13, 2);
            Assert.That(BlockAttackService.Active, Is.True);
            Assert.That(BlockGameplaySettings.BlocksPerMaterial, Is.EqualTo(32));
            Assert.That(BlockGameplaySettings.MainAttackMaxBlocks, Is.EqualTo(512));
            Assert.That(BlockGameplaySettings.AuxiliaryBudgetFraction, Is.EqualTo(.25f));
            Assert.That(BlockGameplaySettings.FriendlyBlastRadiusFraction, Is.EqualTo(.5f));
            Assert.That(BlockGameplaySettings.BlocksPerSpecialPoint, Is.EqualTo(32));
            Assert.That(BlockGameplaySettings.MainSpecialPointCap, Is.EqualTo(2));
            Assert.That(BlockGameplaySettings.SubSpecialPointCap, Is.EqualTo(10));
            Assert.That(_terrain, Is.Not.Null);
            Assert.That(_terrain.Initialized, Is.True);
            Assert.That(_terrain.World, Is.Not.Null, "The real host owns an initialized world before Clear");
            var policy = _terrain.PublicationPolicy;
            Assert.That(policy.OrdinaryAttackKeys, Is.EqualTo(8));
            Assert.That(policy.DestructionKeys, Is.EqualTo(32));
            Assert.That(policy.GravityKeys, Is.EqualTo(32));
            Assert.That(policy.FrameKeys, Is.EqualTo(32));
            BlockAttackService.Clear(); _terrain.Clear(_match.State.Value.Round);
            var clearedWorld = _terrain.World;
            Assert.That(clearedWorld, Is.Not.Null, "Clear publishes an empty world synchronously");
            Assert.That(clearedWorld.Round, Is.EqualTo(_match.State.Value.Round));
            var seed = new BlockWorld(clearedWorld.Dimensions, clearedWorld.Round, clearedWorld.Bottom);
            _centres = new[] { -9, -3, 3, 9 }.Select(x => BlockWorld.CellAt(_floor + new Vector3(x, 20, 0))).ToArray();
            // The geometry oracle below intentionally has no occlusion term.
            // Prove that real registered map colliders lie below this fixture;
            // never replace the static visibility query to make it pass.
            Assert.That(_match.Arena, Is.Not.Null, "Use the actual host map for the static visibility precondition");
            foreach (var collider in _match.Arena.GetComponentsInChildren<Collider>(true))
                if (collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger)
                    foreach (var centre in _centres) Assert.That(collider.bounds.max.y, Is.LessThan(BlockWorld.Center(centre).y - .1f),
                        "This accounting fixture requires unobstructed blast planes above the real map");
            for (int board = 0; board < _centres.Length; board++)
                for (int z = -20; z <= 20; z++) for (int x = -20; x <= 20; x++)
                {
                    var cell = _centres[board] + new Vector3Int(x, 0, z);
                    Assert.That(seed.InBounds(cell), Is.True);
                    byte team = board == 0 || (x + z) % 2 == 0 ? (byte)1 : (byte)2;
                    seed.Set(cell, BlockCell.Create(team, true));
                }
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, "Diagnostic targets actually published");
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(4 * 41 * 41));
            Assert.That(_host.Snapshot.Value.SpecialPoints + _enemy.Snapshot.Value.SpecialPoints, Is.Zero);
            foreach (var cell in _centres) AssertCollision(cell, true);

            var main = PaidMain(); var sub = PaidSub();
            var specialState = _host.Snapshot.Value; specialState.SpecialAction++;
            _host.Snapshot.Value = specialState;
            // Keep the player chargeable: zero special income must come from the
            // token/credit rule rather than an active-special charging lock.
            var special = BlockAttackService.Special(_host.PlayerId, specialState, _terrain.Round);
            Assert.That(special.PointLimit, Is.Zero); Assert.That(SpecialWeaponSimulation.CanCharge(specialState, _now), Is.True);
            float paidHostMaterial = _host.Snapshot.Value.Ink, paidEnemyMaterial = _enemy.Snapshot.Value.Ink;
            long queuedBeforeBuild = _terrain.CommandsQueued;
            BlockAttackService.Build(main, _floor + new Vector3(5, 0, 5), Vector3.up, .8f, true, true, 512, 101, 1);
            BlockAttackService.Build(main, _floor + new Vector3(7, 0, 5), Vector3.up, .8f, false, true, 512, 101, 1);
            var builds = Queue(); Assert.That(builds.Length, Is.EqualTo(2));
            int reservedAuxiliary = Read<int>(builds[0], "Count");
            int reservedImpact = Read<int>(builds[1], "Count");
            Assert.That(reservedAuxiliary, Is.InRange(1, main.AuxiliaryLimit));
            Assert.That(reservedAuxiliary + reservedImpact, Is.EqualTo(main.BuildLimit));
            Assert.That(main.BuildRemaining, Is.Zero);
            // Same emitted entity/ordinal, but trail and impact have distinct kinds.
            // Replaying either kind may neither reserve material nor enqueue again.
            BlockAttackService.Build(main, _floor + new Vector3(5, 0, 5), Vector3.up, .8f, true, true, 512, 101, 1);
            BlockAttackService.Build(main, _floor + new Vector3(7, 0, 5), Vector3.up, .8f, false, true, 512, 101, 1);
            Assert.That(_terrain.CommandsQueued - queuedBeforeBuild, Is.EqualTo(2));
            Assert.That(main.Added, Is.Zero); Assert.That(main.AwardedPoints, Is.Zero);
            Assert.That(_host.Snapshot.Value.SpecialPoints, Is.Zero, "Unpublished construction pays nothing");
            yield return Pump(() => _terrain.PendingTransactions == 0, "Both public service builds complete");
            var buildResults = builds.Select(c => Read<BlockMutationResult>(c, "Total")).ToArray();
            foreach (var command in builds) Assert.That(Read<bool>(command, "Completed"), Is.True);
            Assert.That(buildResults[0].Added, Is.InRange(1, main.AuxiliaryLimit));
            Assert.That(buildResults[1].Added, Is.InRange(1, reservedImpact));
            Assert.That(main.Added, Is.EqualTo(buildResults.Sum(r => r.Added)));
            Assert.That(main.Added, Is.LessThanOrEqualTo(main.BuildLimit));
            Assert.That(main.AuxiliaryLimit, Is.EqualTo((int)Math.Floor(main.BuildLimit * .25d)));
            Assert.That(main.BuildRemaining, Is.EqualTo(main.BuildLimit - main.Added));
            double constructionPoints = main.Added / 32d;
            Assert.That(constructionPoints, Is.GreaterThan(0).And.LessThan(2), "Friendly-only removal must be observed before main income has saturated");
            Assert.That(main.AwardedPoints, Is.EqualTo(constructionPoints).Within(1e-8));
            Assert.That(_host.Snapshot.Value.SpecialPoints, Is.EqualTo(constructionPoints).Within(1e-8));
            var constructed = Cells(_terrain.World).Keys.Where(c => !seed.TryGet(c, out _)).ToArray();
            Assert.That(constructed.Length, Is.EqualTo(main.Added));
            foreach (var cell in constructed) AssertCollision(cell, true);

            var source = _terrain.World; byte[] frozenSource = source.EncodeSnapshot();
            var allCells = Cells(source); var claimed = new HashSet<Vector3Int>();
            var tokens = new[] { main, main, sub, main, special, sub, main, sub };
            var boardIndices = new[] { 0, 1, 2, 1, 3, 1, 2, 2 };
            var entityIds = new uint[] { 101, 101, 201, 102, 301, 201, 102, 201 };
            var ordinals = new uint[] { 10, 11, 1, 11, 1, 2, 12, 3 };
            var points = boardIndices.Select(board => BlockWorld.Center(_centres[board])).ToArray();
            var expected = new BlockMutationResult[8];
            for (int index = 0; index < 8; index++)
                foreach (var cell in allCells)
                {
                    float radius = cell.Value == tokens[index].Credit.Team ? 1 : 2;
                    if ((BlockWorld.Center(cell.Key) - points[index]).sqrMagnitude > (radius + .051f) * (radius + .051f)
                        || !claimed.Add(cell.Key)) continue;
                    if (cell.Value == tokens[index].Credit.Team) expected[index].RemovedFriendly++;
                    else expected[index].RemovedEnemy++;
                }
            // Independent preconditions make the expected caps and zero-income
            // branch explicit, rather than allowing a tiny fixture to pass them.
            Assert.That(expected[0].RemovedEnemy, Is.Zero); Assert.That(expected[0].RemovedFriendly, Is.GreaterThan(0));
            Assert.That(expected[1].RemovedEnemy, Is.GreaterThan(64));
            Assert.That(expected[2].RemovedEnemy, Is.GreaterThan(320));
            Assert.That(expected[4].RemovedEnemy, Is.GreaterThan(0));
            Assert.That(expected[3].Changed + expected[7].Changed, Is.Zero, "Later overlapping attacks own no already claimed cells");
            long queuedBeforeBlasts = _terrain.CommandsQueued, completedBeforeBlasts = _terrain.CommandsCompleted;
            double sourceTime = _terrain.SimulationTime;
            for (int index = 0; index < 8; index++)
            {
                BlockAttackService.Blast(tokens[index], points[index], 2, entityIds[index], ordinals[index]);
                BlockAttackService.Blast(tokens[index], points[index], 2, entityIds[index], ordinals[index]);
            }
            var commands = Queue(); Assert.That(commands.Length, Is.EqualTo(8));
            Assert.That(_terrain.CommandsQueued - queuedBeforeBlasts, Is.EqualTo(8));
            foreach (var command in commands)
            {
                Assert.That(Read<BlockWorld>(command, "Source"), Is.SameAs(source));
                Assert.That(Read<double>(command, "SourceTime"), Is.EqualTo(sourceTime));
                Assert.That(Read<int>(command, "MaxEnemy"), Is.EqualTo(int.MaxValue));
                Assert.That(Read<int>(command, "MaxFriendly"), Is.EqualTo(int.MaxValue));
                Assert.That(Read<bool>(command, "EnemyOnly"), Is.False);
            }
            Assert.That(_terrain.World, Is.SameAs(source));
            Assert.That(main.RemovedEnemy + sub.RemovedEnemy + special.RemovedEnemy, Is.Zero);
            Assert.That(_enemy.Snapshot.Value.SpecialPoints, Is.Zero);
            _observations.Clear(); _frameChunks.Clear(); _captureBlasts = true;
            _terrain.MutationCompleted += ObserveCredit; _terrain.PublishedCommit += ObservePublication;
            bool batchWitness = false, replayedWhilePending = false;
            for (int frame = 0; frame < PumpLimit && _terrain.PendingTransactions != 0; frame++)
            {
                _now += Dt; _terrain.Tick(true, _now, Dt);
                var pending = Read<object>(_terrain, "_pending");
                if (pending != null)
                {
                    var checkpoints = Read<object>(pending, "CommandCheckpoints") as IEnumerable;
                    if (checkpoints != null) foreach (var checkpoint in checkpoints)
                    {
                        var batch = Read<object>(checkpoint, "BlastBatch"); if (batch == null) continue;
                        var members = Read<object[]>(batch, "Commands");
                        Assert.That(members.Length, Is.EqualTo(8));
                        for (int index = 0; index < 8; index++) Assert.That(members[index], Is.SameAs(commands[index]));
                        var window = Read<BlockPublicationWindow>(pending, "Window");
                        Assert.That(window, Is.Not.Null);
                        Assert.That(window.Policy, Is.SameAs(policy));
                        Assert.That(window.Includes(BlockPublicationOperation.Destruction), Is.True);
                        Assert.That(window.MaximumChangedKeys, Is.EqualTo(policy.DestructionKeys));
                        Assert.That(window.Policy.FrameKeys, Is.EqualTo(32));
                        Assert.That(Read<Vector3Int[]>(pending, "Changed").Length, Is.LessThanOrEqualTo(window.MaximumChangedKeys));
                        var journal = Read<object>(pending, "Gravity");
                        if (journal != null)
                        {
                            Assert.That(window.Includes(BlockPublicationOperation.Gravity), Is.True);
                            Assert.That(Read<BlockPublicationWindow>(journal, "Window"), Is.SameAs(window));
                        }
                        int draftRemovals = ((IEnumerable)Read<object>(pending, "Results")).Cast<object>()
                            .Sum(pair => Read<BlockMutationResult>(pair, "Item2").Changed);
                        if (Read<bool>(batch, "Captured") && draftRemovals > 0)
                        {
                            batchWitness = true;
                            if (!replayedWhilePending)
                            {
                                replayedWhilePending = true;
                                for (int index = 0; index < 8; index++) BlockAttackService.Blast(tokens[index], points[index], 2, entityIds[index], ordinals[index]);
                                Assert.That(_terrain.CommandsQueued - queuedBeforeBlasts, Is.EqualTo(8), "In-flight duplicate phases cannot become new batch members");
                            }
                        }
                    }
                }
                yield return null;
            }
            Assert.That(batchWitness, Is.True, "Need the actual eight-member captured batch checkpoint with real draft deletions; ordinary FIFO is not batch evidence");
            Assert.That(replayedWhilePending, Is.True);
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            Assert.That(_frameChunks.Count, Is.GreaterThan(0));
            Assert.That(_terrain.PublicationPolicy, Is.SameAs(policy));
            Assert.That(_frameChunks.Values.All(chunks => chunks <= policy.FrameKeys), Is.True, "Default shared frame publication cap remains intact");
            Assert.That(_terrain.CommandsCompleted - completedBeforeBlasts, Is.EqualTo(8));
            for (int index = 0; index < 8; index++)
            {
                Assert.That(Read<bool>(commands[index], "Completed"), Is.True);
                var actual = Read<BlockMutationResult>(commands[index], "Total");
                Assert.That(actual.Added, Is.Zero);
                Assert.That(actual.RemovedEnemy, Is.EqualTo(expected[index].RemovedEnemy), "Actual earliest enemy claim for participant " + index);
                Assert.That(actual.RemovedFriendly, Is.EqualTo(expected[index].RemovedFriendly), "Actual earliest friendly claim for participant " + index);
            }
            int[] changedParticipants = Enumerable.Range(0, 8).Where(i => expected[i].Changed > 0).ToArray();
            Assert.That(_observations.Count, Is.EqualTo(changedParticipants.Length));
            double expectedHostPoints = constructionPoints, expectedEnemyPoints = 0;
            int mainEnemy = 0, subEnemy = 0, specialEnemy = 0;
            for (int observation = 0; observation < changedParticipants.Length; observation++)
            {
                int index = changedParticipants[observation]; var sample = _observations[observation];
                Assert.That(sample.Result.RemovedEnemy, Is.EqualTo(expected[index].RemovedEnemy));
                Assert.That(sample.Result.RemovedFriendly, Is.EqualTo(expected[index].RemovedFriendly));
                Assert.That(sample.CentresAbsentFromWorldAndCollision, Is.True, "World and colliders already published before the service credit callback");
                Assert.That(sample.HostPoints, Is.EqualTo(expectedHostPoints).Within(1e-8));
                Assert.That(sample.EnemyPoints, Is.EqualTo(expectedEnemyPoints).Within(1e-8));
                if (ReferenceEquals(tokens[index], main)) mainEnemy += expected[index].RemovedEnemy;
                else if (ReferenceEquals(tokens[index], sub)) subEnemy += expected[index].RemovedEnemy;
                else specialEnemy += expected[index].RemovedEnemy;
                expectedHostPoints = Math.Min(2, (main.Added + mainEnemy) / 32d);
                expectedEnemyPoints = Math.Min(10, subEnemy / 32d);
            }
            Assert.That(main.RemovedEnemy, Is.EqualTo(mainEnemy)); Assert.That(sub.RemovedEnemy, Is.EqualTo(subEnemy));
            Assert.That(special.RemovedEnemy, Is.EqualTo(specialEnemy));
            Assert.That(main.AwardedPoints, Is.EqualTo(2)); Assert.That(sub.AwardedPoints, Is.EqualTo(10)); Assert.That(special.AwardedPoints, Is.Zero);
            Assert.That(_host.Snapshot.Value.SpecialPoints, Is.EqualTo(2).Within(1e-8));
            Assert.That(_enemy.Snapshot.Value.SpecialPoints, Is.EqualTo(10).Within(1e-8));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(allCells.Count - claimed.Count));
            Assert.That(_terrain.PinkCount, Is.EqualTo(allCells.Count(pair => pair.Value == 1 && !claimed.Contains(pair.Key))));
            Assert.That(_terrain.BlueCount, Is.EqualTo(allCells.Count(pair => pair.Value == 2 && !claimed.Contains(pair.Key))));
            Assert.That(_match.State.Value.PinkBlockCount, Is.EqualTo(_terrain.PinkCount));
            Assert.That(_match.State.Value.BlueBlockCount, Is.EqualTo(_terrain.BlueCount));
            foreach (var cell in allCells.Keys)
            {
                bool shouldRemain = !claimed.Contains(cell);
                bool present = _terrain.World.TryGet(cell, out var actualCell);
                Assert.That(present, Is.EqualTo(shouldRemain));
                if (shouldRemain) Assert.That(actualCell.Owner, Is.EqualTo(allCells[cell]), "Published surviving owner at " + cell);
            }
            CollectionAssert.AreEqual(frozenSource, source.EncodeSnapshot(), "Every source field remains immutable");
            var committedBytes = _terrain.ExportSnapshot(); int observationsBeforeReplay = _observations.Count;
            for (int index = 0; index < 8; index++) BlockAttackService.Blast(tokens[index], points[index], 2, entityIds[index], ordinals[index]);
            BlockAttackService.Build(main, _floor + new Vector3(5, 0, 5), Vector3.up, .8f, true, true, 512, 101, 1);
            BlockAttackService.Build(main, _floor + new Vector3(7, 0, 5), Vector3.up, .8f, false, true, 512, 101, 1);
            for (int frame = 0; frame < 12; frame++) { _now += Dt; _terrain.Tick(true, _now, Dt); yield return null; }
            Assert.That(_terrain.CommandsQueued - queuedBeforeBlasts, Is.EqualTo(8));
            Assert.That(_terrain.CommandsCompleted - completedBeforeBlasts, Is.EqualTo(8));
            Assert.That(_observations.Count, Is.EqualTo(observationsBeforeReplay));
            CollectionAssert.AreEqual(committedBytes, _terrain.ExportSnapshot());
            Assert.That(_host.Snapshot.Value.SpecialPoints, Is.EqualTo(2)); Assert.That(_enemy.Snapshot.Value.SpecialPoints, Is.EqualTo(10));
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidHostMaterial)); Assert.That(_enemy.Snapshot.Value.Ink, Is.EqualTo(paidEnemyMaterial));
        }

        BlockAttackToken PaidMain()
        {
            var s = _host.Snapshot.Value; var weapon = GameplayConfig.GetWeapon(1);
            s.WeaponPhase = WeaponPhase.Idle; s.AttackNeedsRelease = false; s.NextShotAt = s.BurstReadyAt = 0;
            var input = new PlayerInputFrame { HeroRevision = s.HeroRevision, Revision = s.Revision, Fire = true, FireSequence = s.ConsumedFire + 1 };
            float before = s.Ink; uint sequence = s.ShotSequence; bool paid = false;
            for (int frame = 0; frame < 120 && !paid; frame++) { _now += Dt; paid = WeaponSimulation.Step(ref s, input, weapon, _now, false, true); }
            Assert.That(paid, Is.True); Assert.That(s.ShotSequence, Is.EqualTo(sequence + 1));
            Assert.That(before - s.Ink, Is.EqualTo(WeaponSimulation.InkCost(weapon, s.LastShotCharge)).Within(.001));
            _host.Snapshot.Value = s;
            var token = BlockAttackService.Main(_host.PlayerId, s, _terrain.Round, weapon);
            Assert.That(token, Is.Not.Null); Assert.That(token.Credit.Kind, Is.EqualTo(PaintAttackKind.Main));
            Assert.That(token.BuildLimit, Is.EqualTo(Math.Min(512, (int)Math.Floor(WeaponSimulation.InkCost(weapon, s.LastShotCharge) * 32d + 1e-8))));
            Assert.That(BlockAttackService.Main(_host.PlayerId, s, _terrain.Round, weapon), Is.SameAs(token));
            s.ShotSequence--; Assert.That(BlockAttackService.Main(_host.PlayerId, s, _terrain.Round, weapon), Is.Null);
            return token;
        }
        BlockAttackToken PaidSub()
        {
            var s = _enemy.Snapshot.Value; s.SubWeaponId = SubWeaponConfigService.Id(SubWeaponType.FizzyBomb);
            s.SubPhase = SubWeaponPhase.Idle; s.SubNeedsRelease = false;
            s.SubReadyAt = s.SubRecoveryUntil = s.AttackRecoveryUntil = 0;
            var config = PlayerLoadout.SubWeapon(s); float before = s.Ink;
            var input = new PlayerInputFrame { HeroRevision = s.HeroRevision, Revision = s.Revision, SubHeld = true,
                SubPressSequence = s.SubConsumedPress + 1, SubReleaseSequence = s.SubConsumedRelease };
            Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, _now, true, SubWeaponFailure.None, out _), Is.False);
            for (int frame = 0; frame < 180 && s.SubCharge < 1; frame++)
            {
                _now += Dt;
                Assert.That(SubWeaponSimulation.Step(ref s, input, config, Dt, _now, true, SubWeaponFailure.None, out _), Is.False,
                    "Holding the full-charge deployment does not pay before release");
            }
            Assert.That(s.SubCharge, Is.EqualTo(1), "Three distinct later phase ordinals model a legitimately full-charge Fizzy payment");
            _now += Dt; input.SubHeld = false; input.SubReleaseSequence++;
            bool paid = SubWeaponSimulation.Step(ref s, input, config, Dt, _now, true, SubWeaponFailure.None, out _);
            for (int frame = 0; frame < 120 && !paid; frame++) { _now += Dt; paid = SubWeaponSimulation.Step(ref s, input, config, Dt, _now, true, SubWeaponFailure.None, out _); }
            Assert.That(paid, Is.True); Assert.That(before - s.Ink, Is.EqualTo(config.Common.inkCost).Within(.001));
            _enemy.Snapshot.Value = s;
            var token = BlockAttackService.Sub(_enemy.PlayerId, s, _terrain.Round, config);
            Assert.That(token, Is.Not.Null); Assert.That(token.Credit.Kind, Is.EqualTo(PaintAttackKind.Sub));
            Assert.That(BlockAttackService.Sub(_enemy.PlayerId, s, _terrain.Round, config), Is.SameAs(token));
            s.SubAction--; Assert.That(BlockAttackService.Sub(_enemy.PlayerId, s, _terrain.Round, config), Is.Null);
            return token;
        }
        void Place(PrototypePlayer player, Vector3 at, byte team)
        {
            var s = player.Snapshot.Value; s.HeroId = 1; s.Team = team; s.Health = s.Ink = 100;
            s.LifeState = PlayerLifeState.Alive; s.ProtectedUntil = 0; s.Swimming = s.CompactBody = false;
            s.PaperPose = PaperPose.None; s.Grounded = true; s.VerticalSpeed = s.AirHumanOffset = 0;
            s.SpecialWeaponId = 1; s.SpecialPhase = SpecialPhase.Charging; s.SpecialChargeLockedUntil = 0; s.SpecialPoints = 0;
            var controller = player.GetComponent<CharacterController>(); controller.enabled = false;
            player.transform.position = at; controller.enabled = true; player.Snapshot.Value = s;
            player.SwimBody?.ApplyCollision(s); Physics.SyncTransforms();
        }
        void ObserveCredit(BlockMutationResult result, double latency)
        {
            if (!_captureBlasts) return;
            bool absent = _centres.All(cell => !_terrain.World.TryGet(cell, out _) &&
                !Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .08f, Vector3.down, .16f, 1 << BlockTerrain.TerrainLayer));
            _observations.Add(new CreditObservation(result, _host.Snapshot.Value.SpecialPoints, _enemy.Snapshot.Value.SpecialPoints, absent));
        }
        void ObservePublication(BlockTerrainCommit commit)
        {
            if (!_captureBlasts) return;
            _frameChunks.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length; _frameChunks[Time.frameCount] = count;
        }
        void AssertCollision(Vector3Int cell, bool expected)
        {
            bool hit = Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .08f, Vector3.down, out var ray, .16f, 1 << BlockTerrain.TerrainLayer);
            Assert.That(hit, Is.EqualTo(expected));
            if (expected) { Assert.That(_terrain.TryContact(ray, out var contact), Is.True); Assert.That(contact.Stable, Is.True); Assert.That(contact.Cell, Is.EqualTo(cell)); }
        }
        IEnumerator Pump(Func<bool> ready, string label)
        {
            for (int frame = 0; frame < PumpLimit && !ready(); frame++) { _now += Dt; _terrain.Tick(true, _now, Dt); yield return null; }
            Assert.That(ready(), Is.True, label);
        }
        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        static Dictionary<Vector3Int, byte> Cells(BlockWorld world)
        {
            var result = new Dictionary<Vector3Int, byte>();
            foreach (var pair in world.Chunks) for (int index = 0; index < pair.Value.Cells.Length; index++)
                if (pair.Value.Cells[index].Occupied) result.Add(BlockWorld.CellFromIndex(pair.Key, index), pair.Value.Cells[index].Owner);
            return result;
        }
        object[] Queue() => ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().ToArray();
        static T Read<T>(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null, "Actual state owner for " + name);
            var field = owner.GetType().GetField(name, Fields); Assert.That(field, Is.Not.Null, "Current field " + name);
            return (T)field.GetValue(owner);
        }
        IEnumerator Leave()
        {
            _captureBlasts = false;
            if (_terrain != null) { _terrain.MutationCompleted -= ObserveCredit; _terrain.PublishedCommit -= ObservePublication; }
            if (_match != null && _serverTick != null && _match.NetworkManager.NetworkTickSystem != null) _match.NetworkManager.NetworkTickSystem.Tick += _serverTick;
            _serverTick = null; BlockAttackService.Clear();
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            yield return Leave(); yield return new ExitPlayMode();
        }
    }
}
#endif
