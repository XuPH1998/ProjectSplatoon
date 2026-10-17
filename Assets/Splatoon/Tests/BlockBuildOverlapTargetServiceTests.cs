#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // Reports-only candidate. All scenario state comes from public BlockWorld
    // construction/snapshot and paid Terrain attacks. Reflection only observes
    // actual owners, and reuses the existing joined native/worker teardown.
    public sealed class BlockBuildOverlapTargetServiceTests
    {
        const double Epoch = 10;
        const int PumpLimit = 6000, UnrelatedCount = 128;
        static readonly Vector2Int[] TargetColumns = { new(2, 2), new(2, 1), new(2, 3), new(1, 2), new(3, 2) };
        static readonly Vector3Int SafeCell = new(30, 0, 30);
        BlockBuildShallowColumnReplayCandidateTests _fixture;
        BlockTerrain _terrain;
        bool _callbacksLive;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _fixture = new BlockBuildShallowColumnReplayCandidateTests();
            yield return _fixture.SetupAfterEnteringPlayMode();
            _terrain = Read<BlockTerrain>(_fixture, "_terrain");
            _callbacksLive = true;
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            _callbacksLive = false;
            if (_fixture != null) yield return _fixture.TeardownBeforeExitingPlayMode();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _terrain = null; _fixture = null;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ManyUnrelatedMotionsCannotClaimServiceOfTheActuallyRejectedPaidHead()
            => Run(false);

        [UnityTest, Timeout(600000)]
        public IEnumerator ASelectedLowerDependencyMustBelongToTheExactConflictingColumnAndActuallySettle()
            => Run(true);

        IEnumerator Run(bool lowerDependency)
        {
            var seed = MakeSeed(lowerDependency, out var targets, out var lowers, out var payloadMarkers);
            byte[] wire = seed.EncodeSnapshot();
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            for (int frame = 0; frame < PumpLimit
                && (_terrain.Revision != seed.Revision || Read<object>(_terrain, "_pending") != null); frame++)
            {
                ObservePending(); _terrain.Tick(false, Epoch, 1f / 60f); ObservePending();
                yield return null;
            }
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
            var immutableSource = _terrain.World;
            byte[] immutableWire = immutableSource.EncodeSnapshot();
            int initialBlue = immutableSource.BlueCount;
            long failedBefore = _terrain.FailedTransactions;
            _terrain.Tick(false, Epoch, 1f / 60f);

            var tokens = new[] { MakeToken(101), MakeToken(102) };
            int[] reservations = tokens.Select(token => token.ReserveBuild(1, false)).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 1 }, reservations);
            var callbacks = new int[2]; var order = new List<int>();
            var results = new BlockMutationResult[2];
            Action<int, BlockMutationResult> finish = (index, result) =>
            {
                tokens[index].CompleteBuild(reservations[index], false, result.Added);
                if (!_callbacksLive) return;
                callbacks[index]++; results[index] = result; order.Add(index);
            };
            int overlapAborts = 0;
            bool unrelatedPresentAtRejection = false, exactTargetStableObserved = false, exactLowerStableObserved = false;
            bool exactTargetSettledBeforePaid = false, badCreditedPublication = false;
            var repairs = new List<object>(); var seenRepairs = new HashSet<object>();
            var selectedMotions = new HashSet<ulong>(); var selectedDependencies = new HashSet<ulong>();
            var actualPendings = new Dictionary<long, object>();
            var attributedPendings = new HashSet<object>();
            var activeAttributedPendings = new HashSet<object>();
            var terminalRepairPendings = new List<object>();
            int publishedAttributedPendingObservations = 0;
            object originalHead = null;
            ulong firstTarget = 0, firstLower = 0;
            Vector2Int firstColumn = default;
            var initial = _terrain.InspectPublicationRetry();

            void ObserveRepair(object repair)
            {
                if (repair == null) return;
                if (seenRepairs.Add(repair))
                {
                    repairs.Add(repair);
                    var source = Read<BlockWorld>(repair, "Source");
                    var column = Read<Vector2Int>(repair, "Column");
                    var motion = Read<object>(repair, "Motion");
                    ulong id = Read<ulong>(motion, "Id");
                    Assert.That(Read<bool>(motion, "Cohort"), Is.True);
                    Assert.That(targets.TryGetValue(column, out var expected), Is.True);
                    Assert.That(id, Is.EqualTo(expected), "Repair must retain the real rejected source motion.");
                    Assert.That(Read<object>(repair, "Head"), Is.SameAs(originalHead));
                    Assert.That(Read<object>(repair, "Window"), Is.SameAs(Read<object>(originalHead, "RetryWindow")));
                    var sourceCell = Read<Vector3Int>(repair, "SourceCell");
                    var conflictCell = Read<Vector3Int>(repair, "ConflictCell");
                    Assert.That(new Vector2Int(sourceCell.x, sourceCell.z), Is.EqualTo(column));
                    Assert.That(new Vector2Int(conflictCell.x, conflictCell.z), Is.EqualTo(column));
                    Assert.That(source.Falling.TryGetValue(Read<ulong>(repair, "SourceRun"), out var run), Is.True);
                    int index = Read<int>(repair, "SourceIndex");
                    Assert.That(run.Cells[index].Occupied, Is.True);
                    Assert.That(run.Bottom + Vector3Int.up * index, Is.EqualTo(sourceCell));
                    if (firstTarget == 0)
                    {
                        firstTarget = id; firstColumn = column;
                        firstLower = lowerDependency ? lowers[column] : 0;
                    }
                }
                var service = Read<object>(repair, "ServiceMotion");
                ulong serviceId = Read<ulong>(service, "Id");
                if (serviceId == 0) return; // Armed, but not selected yet.
                var at = Read<Vector2Int>(repair, "Column");
                ulong targetId = Read<ulong>(Read<object>(repair, "Motion"), "Id");
                Assert.That(Read<bool>(service, "Cohort"), Is.True);
                bool dependency = Read<bool>(repair, "Dependency");
                if (dependency)
                {
                    Assert.That(lowerDependency, Is.True, "An unrelated column cannot be the target dependency.");
                    Assert.That(serviceId, Is.EqualTo(lowers[at]));
                    Assert.That(serviceId, Is.Not.EqualTo(targetId));
                    selectedDependencies.Add(serviceId);
                }
                else Assert.That(serviceId, Is.EqualTo(targetId));
                selectedMotions.Add(serviceId);
            }
            void ObserveOwners()
            {
                ObserveRepair(Read<object>(_terrain, "_buildOverlapRepair"));
                var pending = Read<object>(_terrain, "_pending");
                if (pending != null)
                {
                    actualPendings[Read<long>(pending, "WallId")] = pending;
                    ObserveRepair(Read<object>(pending, "BuildOverlapRepair"));
                    var window = Read<BlockPublicationWindow>(pending, "Window");
                    if (!Read<bool>(pending, "Remote") && window != null)
                        Assert.That(Read<Vector3Int[]>(pending, "Changed").Length, Is.LessThanOrEqualTo(window.MaximumChangedKeys));
                    ValidateAttributedPending(pending, true);
                }
                // A no-change commit can retire the record in the same Tick;
                // previously observed record references still expose selection.
                foreach (var repair in repairs.ToArray()) ObserveRepair(repair);
                // Terminal callbacks retain actual previously observed references;
                // do not query _pending after publication has cleared that field.
                foreach (var completed in terminalRepairPendings.ToArray()) ValidateAttributedPending(completed, false);
                terminalRepairPendings.Clear();
            }
            void ValidateAttributedPending(object pending, bool active)
            {
                var repair = Read<object>(pending, "BuildOverlapRepair");
                if (repair == null) return;
                var journal = Read<object>(pending, "Gravity");
                var visit = journal == null ? null : Read<object>(journal, "BuildRepair");
                Assert.That(visit, Is.Not.Null, "An attached repair pending needs an actual journal visit.");
                Assert.That(Read<object>(visit, "Owner"), Is.SameAs(repair));
                Assert.That(Read<bool>(visit, "Served"), Is.True);
                Assert.That(Read<object>(visit, "Motion"), Is.EqualTo(Read<object>(repair, "ServiceMotion")));
                ObserveRepair(repair);
                attributedPendings.Add(pending);
                if (active) activeAttributedPendings.Add(pending);
            }
            void ObservePhysicalPayload()
            {
                if (firstTarget == 0) return;
                bool stable = MarkedPayloadStable(_terrain.World, payloadMarkers[firstTarget]);
                exactTargetStableObserved |= stable;
                exactTargetSettledBeforePaid |= stable && callbacks[0] == 0;
                if (lowerDependency)
                    exactLowerStableObserved |= MarkedPayloadStable(_terrain.World, payloadMarkers[firstLower]);
            }
            Action<BlockTerrain.PendingWallDiagnostic> terminal = sample =>
            {
                if (sample.Outcome == BlockTerrain.PendingWallOutcome.Published
                    && actualPendings.TryGetValue(sample.Id, out var actual)
                    && Read<object>(actual, "BuildOverlapRepair") != null)
                {
                    publishedAttributedPendingObservations++;
                    terminalRepairPendings.Add(actual);
                    var journal = Read<object>(actual, "Gravity");
                    var visit = journal == null ? null : Read<object>(journal, "BuildRepair");
                    badCreditedPublication |= visit == null || !Read<bool>(visit, "Served")
                        || !ReferenceEquals(Read<object>(visit, "Owner"), Read<object>(actual, "BuildOverlapRepair"));
                }
                if (sample.Outcome != BlockTerrain.PendingWallOutcome.Aborted
                    || sample.AbortReason != BlockTerrain.PendingAbortReason.BuildMovingOverlap) return;
                overlapAborts++;
                unrelatedPresentAtRejection |= _terrain.World.Falling.Values.Any(run =>
                    !TargetColumns.Contains(new Vector2Int(run.Bottom.x, run.Bottom.z)));
            };
            _terrain.PendingTransactionFinished += terminal;
            try
            {
                int attackY = lowerDependency ? 4 : 1;
                _terrain.QueueBuild(BlockWorld.Center(new Vector3Int(2, attackY, 2)) - Vector3.up * .051f,
                    Vector3.up, 1, reservations[0], .101f, false, result => finish(0, result), true);
                RememberCommand();
                originalHead = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().First();
                _terrain.QueueBuild(BlockWorld.Center(SafeCell) - Vector3.up * .051f,
                    Vector3.up, 1, reservations[1], 0, true, result => finish(1, result), true);
                RememberCommand();
                int frames = 0;
                for (; frames < PumpLimit; frames++)
                {
                    ObserveOwners(); ObservePhysicalPayload(); ObservePending();
                    if (callbacks.All(count => count == 1) && _terrain.PendingTransactions == 0
                        && _terrain.FallingCount == 0 && _terrain.UnstableColumns == 0) break;
                    bool hadQueuedCommands = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Any();
                    double now = Epoch + (frames + 1) / 60d;
                    _terrain.Tick(true, now, 1f / 60f);
                    ObservePending(); ObserveOwners(); ObservePhysicalPayload();
                    bool hasQueuedCommands = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Any();
                    // Completion can remove the final queued command early in a
                    // Tick. That transition legitimately admits the alone limit.
                    Assert.That(_terrain.GravityChecksThisFrame, Is.LessThanOrEqualTo(hadQueuedCommands && hasQueuedCommands
                        ? BlockPublicationPolicy.GravityChecksWithCommands : BlockPublicationPolicy.GravityChecksAlone));
                    Assert.That(_terrain.BlueCount, Is.EqualTo(initialBlue));
                    Assert.That(_terrain.PinkCount, Is.EqualTo(tokens.Sum(token => token.Added)));
                    Assert.That(IndependentCounts(_terrain.World), Is.EqualTo((_terrain.PinkCount, _terrain.BlueCount)));
                    AssertMarkedPayloadConserved(_terrain.World, payloadMarkers.Values.SelectMany(markers => markers));
                    AssertNoStableMovingOverlap(_terrain.World, now);
                    CollectionAssert.AreEqual(immutableWire, immutableSource.EncodeSnapshot());
                    Assert.That(callbacks, Is.All.LessThanOrEqualTo(1));
                    yield return null;
                }
                var final = _terrain.InspectPublicationRetry();
                string diagnostic = $"dependency={lowerDependency} frames={frames} overlap={overlapAborts} "
                    + $"target={firstTarget} lower={firstLower} column={firstColumn} repairOwners={repairs.Count} "
                    + $"selected=[{string.Join(",", selectedMotions)}] dependencies=[{string.Join(",", selectedDependencies)}] "
                    + $"targetStableObserved={exactTargetStableObserved} lowerStableObserved={exactLowerStableObserved} "
                    + $"targetStableBeforePaid={exactTargetSettledBeforePaid} attributedPending={attributedPendings.Count} "
                    + $"activeAttributedPending={activeAttributedPendings.Count} "
                    + $"publishedAttributedPending={publishedAttributedPendingObservations} "
                    + $"callbacks={string.Join(",", callbacks)} retry={JsonUtility.ToJson(final)}";
                Debug.Log("[BLOCK_EXACT_REPAIR_TARGET_CANDIDATE] " + diagnostic);
                Assert.That(overlapAborts, Is.GreaterThan(0), "A fixture without actual reason 18 cannot pass. " + diagnostic);
                Assert.That(unrelatedPresentAtRejection, Is.True, diagnostic);
                Assert.That(selectedMotions, Is.Not.Empty, diagnostic);
                Assert.That(Counter(final, "buildRepairTargetSelected"), Is.GreaterThan(Counter(initial, "buildRepairTargetSelected")), diagnostic);
                Assert.That(Counter(final, "buildRepairTargetServed"), Is.GreaterThan(Counter(initial, "buildRepairTargetServed")), diagnostic);
                Assert.That(attributedPendings.Count, Is.GreaterThan(0),
                    "This leaf must observe an actual attributed pending/journal; zero is missing natural coverage. " + diagnostic);
                Assert.That(activeAttributedPendings.Count, Is.GreaterThan(0), diagnostic);
                Assert.That(publishedAttributedPendingObservations, Is.GreaterThan(0), diagnostic);
                Assert.That(exactTargetStableObserved, Is.True, diagnostic);
                Assert.That(MarkedPayloadStable(_terrain.World, payloadMarkers[firstTarget]), Is.True,
                    "The exact original payload must be stable after draining, including any conservative spread moves. " + diagnostic);
                if (lowerDependency)
                {
                    Assert.That(selectedDependencies, Is.Not.Empty, diagnostic);
                    Assert.That(Counter(final, "buildRepairDependencySelected"), Is.GreaterThan(Counter(initial, "buildRepairDependencySelected")), diagnostic);
                    Assert.That(exactLowerStableObserved, Is.True, diagnostic);
                    Assert.That(MarkedPayloadStable(_terrain.World, payloadMarkers[firstLower]), Is.True, diagnostic);
                }
                Assert.That(badCreditedPublication, Is.False, diagnostic);
                CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks, diagnostic);
                CollectionAssert.AreEqual(new[] { 0, 1 }, order, diagnostic);
                Assert.That(results[0].Added, Is.Zero, diagnostic);
                Assert.That(results[1].Added, Is.EqualTo(1), diagnostic);
                foreach (var token in tokens)
                    Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(token.BuildLimit), diagnostic);
                Assert.That(tokens[0].TakePoints(), Is.Zero, diagnostic);
                Assert.That(_terrain.FailedTransactions, Is.EqualTo(failedBefore), diagnostic);
                Assert.That(_terrain.PendingTransactions, Is.Zero, diagnostic);
                Assert.That(_terrain.FallingCount, Is.Zero, diagnostic);
                Assert.That(_terrain.UnstableColumns, Is.Zero, diagnostic);
                Physics.SyncTransforms();
                Assert.That(Physics.Raycast(BlockWorld.Center(SafeCell) + Vector3.up * .2f,
                    Vector3.down, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True, diagnostic);
                byte[] finalWire = _terrain.ExportSnapshot();
                CollectionAssert.AreEqual(finalWire, BlockWorld.DecodeSnapshot(finalWire).EncodeSnapshot());
            }
            finally { _terrain.PendingTransactionFinished -= terminal; }
        }

        static BlockWorld MakeSeed(bool paired, out Dictionary<Vector2Int, ulong> targets, out Dictionary<Vector2Int, ulong> lowers,
            out Dictionary<ulong, ushort[]> payloadMarkers)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            targets = new Dictionary<Vector2Int, ulong>(); lowers = new Dictionary<Vector2Int, ulong>();
            payloadMarkers = new Dictionary<ulong, ushort[]>();
            for (int index = 0; index < UnrelatedCount; index++)
                AddCapped(world, new Vector2Int(-100 + 4 * (index % 16), -100 + 4 * (index / 16)), 4, .3f);
            for (int targetIndex = 0; targetIndex < TargetColumns.Length; targetIndex++)
            {
                var column = TargetColumns[targetIndex];
                ushort lowerMarker = (ushort)((paired ? 45 : 61) + 3 * targetIndex);
                ulong lower = AddCapped(world, column, 4, .3f, true, lowerMarker);
                payloadMarkers[lower] = Enumerable.Range(lowerMarker, 3).Select(marker => (ushort)marker).ToArray();
                lowers[column] = paired ? lower : 0;
                if (!paired) { targets[column] = lower; continue; }
                ushort upperMarker = (ushort)(76 + 3 * targetIndex);
                ulong upper = AddCapped(world, column, 8, .4f, false, upperMarker);
                payloadMarkers[upper] = Enumerable.Range(upperMarker, 3).Select(marker => (ushort)marker).ToArray();
                world.SetCohortSupport(upper, lower, .1f, out _);
                var group = world.Cohorts[upper];
                group.LandingKnown = true; group.LandingOffset = .4f;
                group.InitialOffset = .4f; group.InitialSpeed = 0; group.NeedsLandingProof = false;
                targets[column] = upper;
            }
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray()); return world;
        }
        static ulong AddCapped(BlockWorld world, Vector2Int at, int sourceBottom, float offset, bool baseCell = true, ushort marker = 0)
        {
            if (baseCell) world.Set(new Vector3Int(at.x, 0, at.y), BlockCell.Create(2));
            for (int y = sourceBottom; y < sourceBottom + 3; y++)
            {
                var cell = BlockCell.Create(2);
                // Public seed identity only: block runtime does not use the
                // positive durability magnitude as a motion/attack parameter.
                if (marker != 0) cell.Durability = (ushort)(marker + y - sourceBottom);
                world.Set(new Vector3Int(at.x, y, at.y), cell);
            }
            var group = world.BeginCohort(new Vector3Int(at.x, sourceBottom, at.y), 3);
            world.DetachCohortPiece(group.Id, 3, Epoch, out _); world.SetCohortSupport(group.Id, 0, 0, out _);
            group = world.Cohorts[group.Id]; group.LandingKnown = true; group.LandingOffset = offset;
            group.InitialOffset = offset; group.InitialSpeed = 0; group.NeedsLandingProof = false;
            return group.Id;
        }
        static bool MarkedPayloadStable(BlockWorld world, ushort[] markers)
        {
            var counts = markers.ToDictionary(marker => marker, _ => 0);
            foreach (var chunk in world.Chunks.Values)
                foreach (var cell in chunk.Cells)
                    if (cell.Occupied && counts.ContainsKey(cell.Durability))
                    {
                        if (cell.Owner != 2 || cell.Flags != 0) return false;
                        counts[cell.Durability]++;
                    }
            foreach (var run in world.Falling.Values)
                foreach (var cell in run.Cells)
                    if (cell.Occupied && counts.ContainsKey(cell.Durability)) return false;
            return counts.Values.All(count => count == 1);
        }
        static void AssertMarkedPayloadConserved(BlockWorld world, IEnumerable<ushort> markers)
        {
            var counts = markers.ToDictionary(marker => marker, _ => 0);
            void Count(BlockCell cell)
            {
                if (!cell.Occupied || !counts.ContainsKey(cell.Durability)) return;
                Assert.That(cell.Owner, Is.EqualTo(2)); Assert.That(cell.Flags, Is.Zero);
                counts[cell.Durability]++;
            }
            foreach (var chunk in world.Chunks.Values) foreach (var cell in chunk.Cells) Count(cell);
            foreach (var run in world.Falling.Values) foreach (var cell in run.Cells) Count(cell);
            foreach (var pair in counts)
                Assert.That(pair.Value, Is.EqualTo(1),
                    $"Complete source payload marker {pair.Key} must survive once across stable/falling ownership and spread.");
        }
        static (int Pink, int Blue) IndependentCounts(BlockWorld world)
        {
            int pink = 0, blue = 0;
            foreach (var chunk in world.Chunks.Values)
                foreach (var cell in chunk.Cells)
                    if (cell.Occupied) { if (cell.Owner == 1) pink++; else if (cell.Owner == 2) blue++; }
            foreach (var run in world.Falling.Values)
                foreach (var cell in run.Cells)
                    if (cell.Occupied) { if (cell.Owner == 1) pink++; else if (cell.Owner == 2) blue++; }
            return (pink, blue);
        }
        static void AssertNoStableMovingOverlap(BlockWorld world, double now)
        {
            foreach (var run in world.Falling.Values)
            {
                float offset = run.Offset(now);
                for (int index = 0; index < run.Cells.Length; index++)
                {
                    if (!run.Cells[index].Occupied) continue;
                    float bottom = (run.Bottom.y + index) * BlockWorld.CellSize - offset;
                    int first = Mathf.FloorToInt((bottom + .000001f) / BlockWorld.CellSize);
                    int last = Mathf.CeilToInt((bottom + BlockWorld.CellSize - .000001f) / BlockWorld.CellSize) - 1;
                    for (int y = first; y <= last; y++)
                        Assert.That(world.TryGet(new Vector3Int(run.Bottom.x, y, run.Bottom.z), out _), Is.False,
                            $"Published stable/moving overlap: run={run.Id} sourceIndex={index} row={y} at={now:R}");
                }
            }
        }
        static BlockAttackToken MakeToken(ulong owner)
            => new(new PaintCredit(owner, 1, 0, 1, PaintAttackKind.Main, true), 0, 32, 8, 2);
        static long Counter(object snapshot, string field) => Convert.ToInt64(Read<object>(snapshot, field));
        void ObservePending() => ObserveFixture("RememberActualPending", _terrain);
        void RememberCommand() => ObserveFixture("RememberLastCommand");
        void ObserveFixture(string name, params object[] args)
            => typeof(BlockBuildShallowColumnReplayCandidateTests)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_fixture, args);
        static T Read<T>(object target, string field)
        {
            Assert.That(target, Is.Not.Null, field);
            var member = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(member, Is.Not.Null, field);
            return (T)member.GetValue(target);
        }
    }
}
#endif
