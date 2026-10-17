#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // These tests cover production queue/commit/bitmap logic without entering
    // Play Mode. They do not establish a wall-clock startup or rendering SLA.
    public sealed class BlockGravityStartupTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;

        [SetUp]
        public void Setup()
        {
            _root = new GameObject("Gravity startup journal contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            SetWorld(new BlockWorld(new Vector2(32, 64), 3));
        }

        [TearDown]
        public void Teardown() => Object.DestroyImmediate(_root);

        [Test]
        public void StagedSupportRemovalAndDiscardDoNotMutateCommittedPriorityOrCursors()
        {
            var published = _terrain.World;
            var draft = published.Clone();
            var journal = Start(draft);
            Call("QueueGravityStartupRemoval", new Vector3Int(2, 8, 2));
            var startup = Read<object>(journal, "Startup");
            var ticket = ((IDictionary)Read<object>(startup, "Columns"))[new Vector2Int(2, 2)];
            Assert.That(Read<double>(ticket, "RemovedAt"), Is.EqualTo(-1));
            Set(startup, "ColumnCursor", 99);
            Set(startup, "ColumnTurn", false);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Assert.That(Read<int>(_terrain, "_startupColumnCursor"), Is.Zero);
            Assert.That(Read<bool>(_terrain, "_startupColumnTurn"), Is.True);
            Start(published.Clone()); // Discard the unpublished journal.
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty);
            Assert.That(_terrain.World, Is.SameAs(published));
        }

        [Test]
        public void ProofOnlyCommitCannotAssignSupportRemovalTimeToUnpublishedDraft()
        {
            var journal = Start(_terrain.World.Clone());
            Call("QueueGravityStartupRemoval", new Vector3Int(2, 8, 2));
            Set(_terrain, "_now", 10d);
            Call("CommitGravityStartup", journal);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty);
        }

        [Test]
        public void PublishedRemovalActivationAndDisplacedDrawHaveSeparateObservedTimes()
        {
            var support = new Vector3Int(2, 9, 2);
            var body = support + Vector3Int.up;
            var source = _terrain.World;
            Assert.That(source.Set(support, BlockCell.Create(2)), Is.True);
            Assert.That(source.Set(body, BlockCell.Create(2)), Is.True);
            source.Seal(7, source.RevisionKeys.ToArray());
            var removal = source.Clone();
            Assert.That(removal.Set(support, default), Is.True);
            var removedJournal = Start(removal);
            Call("QueueGravityStartupRemoval", support);
            Publish(removal, 8, 10);
            Call("CommitGravityStartup", removedJournal);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.EqualTo(1));
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty);

            var falling = removal.Clone();
            var journal = Start(falling);
            var group = falling.BeginCohort(body, 1);
            var id = MotionId(group.Id);
            Call("QueueMotion", id, new Vector2Int(body.x, body.z), body.y, body.y + 1);
            Call("QueueGravityStartupMotion", id, new Vector2Int(body.x, body.z));
            var run = falling.DetachCohortPiece(group.Id, 1, 10.1, out _);
            group = falling.Cohorts[group.Id];
            group.LandingKnown = true;
            group.LandingOffset = .5f;
            group.NeedsLandingProof = false;
            falling.PrepareCohortForPublication(group.Id, out _);
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty,
                "A prepared activation is still private until its matching World commits.");
            // The final column piece can publish without editing its already
            // committed motion proof again in this particular stage.
            ((IDictionary)Read<object>(journal, "Motions")).Clear();
            ((IDictionary)Read<object>(journal, "OwnActivationProofs")).Add(group.Id, New("GravityMotionWork"));
            falling.ActivateRebasePendingCohorts(10.2);
            Publish(falling, 9, 10.2);
            Call("CommitGravityStartup", journal);
            var sample = _terrain.InspectGravityStartupSamples().Single();
            Assert.That(sample.supportRemovedAt, Is.EqualTo(10));
            Assert.That(sample.activatedAt, Is.EqualTo(10.2));
            Assert.That(sample.activationLatencyMs, Is.EqualTo(200).Within(.0001));
            Assert.That(sample.removalRevision, Is.EqualTo(8));
            Assert.That(sample.activationRevision, Is.EqualTo(9));
            Assert.That(sample.firstPresentedAt, Is.EqualTo(-1));

            Call("BeginGravityStartupPresentation");
            Call("QueueGravityStartupPresentation", run, sample.activationOffset);
            Call("CommitGravityStartupPresentation", 10.21);
            Assert.That(sample.firstPresentedAt, Is.EqualTo(-1), "Zero displacement is not a first moving draw.");
            Call("BeginGravityStartupPresentation");
            for (int i = 0; i < 32; i++) Call("QueueGravityStartupPresentation", run, sample.activationOffset + .01f);
            Assert.That(((ICollection)Read<object>(_terrain, "_startupPresentation")).Count, Is.EqualTo(1),
                "Multiple storage runs/draw batches of one cohort produce one observation.");
            Assert.That(sample.firstPresentedAt, Is.EqualTo(-1), "Queueing matrices does not confirm a submitted draw.");
            Call("BeginGravityStartupPresentation"); // An aborted renderer pass is discarded.
            Call("CommitGravityStartupPresentation", 10.22);
            Assert.That(sample.firstPresentedAt, Is.EqualTo(-1));
            Call("QueueGravityStartupPresentation", run, sample.activationOffset + .02f);
            Call("CommitGravityStartupPresentation", 10.25);
            Assert.That(sample.firstPresentedAt, Is.EqualTo(10.25));
            Assert.That(sample.presentationLatencyMs, Is.EqualTo(50).Within(.0001));
            Assert.That(sample.totalLatencyMs, Is.EqualTo(250).Within(.0001));
        }

        [Test]
        public void PriorityColumnAndOrdinaryColumnAlternateAndRetainBudgetDeferredTurns()
        {
            int top = Mathf.RoundToInt(BlockWorld.Top / BlockWorld.CellSize);
            var urgent = new Vector2Int(2, 2);
            foreach (var column in new[] { new Vector2Int(0, 2), new Vector2Int(1, 2), urgent })
            {
                var work = New("GravityColumnWork"); Set(work, "Scan", top - 16);
                Call("AddCommittedColumn", column, work);
            }
            var removed = Start(_terrain.World);
            Call("QueueGravityStartupRemoval", new Vector3Int(urgent.x, top - 16, urgent.y));
            Set(_terrain, "_now", 10d);
            Call("CommitGravityStartup", removed);

            var first = StageColumns(30);
            var firstStartup = Read<object>(first, "Startup");
            Assert.That(Read<bool>(firstStartup, "ColumnTurn"), Is.False);
            Assert.That(Read<int>(first, "ColumnCursor"), Is.Zero);
            Assert.That(Read<int>(((IDictionary)Read<object>(first, "Columns"))[urgent], "Scan"), Is.EqualTo(top));
            Call("CommitGravityJournal", first);

            var second = StageColumns(30);
            var secondStartup = Read<object>(second, "Startup");
            Assert.That(Read<int>(second, "ColumnCursor"), Is.EqualTo(1), "Normal cursor advances on its intervening visit.");
            Assert.That(Read<bool>(secondStartup, "ColumnTurn"), Is.True);
            Assert.That(Read<int>(secondStartup, "ColumnCursor"), Is.Zero,
                "The urgent operation rejected by the stage remainder retains its fresh-stage cursor.");
        }

        [Test]
        public void AlternateJournalConstructorLazilyCreatesStartupState()
        {
            var journal = Start(_terrain.World);
            Set(journal, "Startup", null);
            Call("QueueGravityStartupRemoval", new Vector3Int(2, 1, 2));
            Assert.That(Read<object>(journal, "Startup"), Is.Not.Null);
            Call("CommitGravityStartup", journal);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.EqualTo(1));
            Call("ResetGravityStartup");
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Assert.That(_terrain.GravityStartupPriorityMotions, Is.Zero);
            Assert.That(_terrain.GravityStartupSamplesDropped, Is.Zero);
        }

        [Test]
        public void PriorityMotionWaitingOnHistoricalRecoveryStillAllowsOrdinaryVisits()
        {
            var world = _terrain.World;
            var queued = Start(world);
            for (ulong id = 1; id <= 3; id++)
            {
                var column = new Vector2Int((int)id, 2);
                world.Cohorts.Add(id, new FallingCohort { Id = id, Bottom = new Vector3Int(column.x, 10, 2),
                    TopExclusive = 11, DetachCursor = 10, Phase = FallingCohortPhase.Detaching });
                Call("QueueMotion", MotionId(id), column, 10, 11);
            }
            Call("CommitGravityJournal", queued);
            var removal = Start(world);
            Call("QueueGravityStartupRemoval", new Vector3Int(3, 9, 2));
            Call("QueueGravityStartupMotion", MotionId(3), new Vector2Int(3, 2));
            Call("CommitGravityStartup", removal);
            Set(_terrain, "_historicalTerminalSource", world);
            Set(_terrain, "_historicalTerminalMutation", world.MutationGeneration);
            var terminals = (IDictionary)Read<object>(_terrain, "_historicalTerminals");
            var outcomeType = terminals.GetType().GetGenericArguments()[1];
            for (int x = 1; x <= 3; x++) terminals.Add(new Vector2Int(x, 2), Enum.ToObject(outcomeType, 0));
            var journal = Start(world.Clone());
            var arguments = new object[] { Read<BlockWorld>(journal, "Next"), new HashSet<Vector3Int>(), 0, new Stopwatch(), .75d, 5 };
            typeof(BlockTerrain).GetMethod("StageCohortFalling", Private).Invoke(_terrain, arguments);
            Assert.That((int)arguments[2], Is.EqualTo(5));
            Assert.That(Read<int>(journal, "MotionCursor"), Is.EqualTo(2),
                "A bounded historical wait consumes the urgent turn and lets ordinary terminal peers rotate.");
            Assert.That(Read<int>(Read<object>(journal, "Startup"), "MotionCursor"), Is.EqualTo(1));
        }

        [Test]
        public void DiagnosticSamplesRemainBoundedAndReportDroppedObservations()
        {
            const int capacity = 1024;
            var world = _terrain.World;
            var removal = Start(world);
            Call("QueueGravityStartupRemoval", new Vector3Int(2, 9, 2));
            Call("CommitGravityStartup", removal);
            var journal = Start(world);
            for (ulong id = 1; id <= capacity + 1; id++)
            {
                var column = new Vector2Int(2, 2);
                world.Cohorts.Add(id, new FallingCohort { Id = id, Bottom = new Vector3Int(2, 10, 2),
                    TopExclusive = 11, DetachCursor = 11, Phase = FallingCohortPhase.Falling });
                Call("QueueMotion", MotionId(id), column, 10, 11);
                Call("QueueGravityStartupMotion", MotionId(id), column);
            }
            Set(_terrain, "_now", 10d);
            Call("CommitGravityStartup", journal);
            Assert.That(_terrain.InspectGravityStartupSamples().Length, Is.EqualTo(capacity));
            Assert.That(_terrain.InspectGravityStartupSamples()[0].cohort, Is.EqualTo(2));
            Assert.That(_terrain.GravityStartupSamplesDropped, Is.EqualTo(1));
            Assert.That(((IDictionary)Read<object>(_terrain, "_startupAwaitPresentation")).Count, Is.EqualTo(capacity));
            Assert.That(_terrain.GravityStartupPriorityMotions, Is.Zero);
        }

        [Test]
        public void LandingBitmapMatchesCellScanAcrossNegativeRowsHolesAndOwnSourceSpan()
        {
            var random = new System.Random(90317);
            var query = typeof(BlockTerrain).GetMethod("TryGravityLandingRow", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var column in new[] { new Vector2Int(-17, -17), new Vector2Int(-1, 15), new Vector2Int(16, 16) })
            {
                var world = new BlockWorld(new Vector2(32, 64));
                for (int y = -64; y < 64; y++)
                    if (random.Next(4) == 0) world.Set(new Vector3Int(column.x, y, column.y), BlockCell.Create(1, random.Next(2) == 0));
                for (int trial = 0; trial < 256; trial++)
                {
                    int minimum = random.Next(-64, 63), scan = random.Next(minimum, 64);
                    var group = trial % 3 == 0 ? null : new FallingCohort
                    {
                        Phase = trial % 3 == 1 ? FallingCohortPhase.Detaching : FallingCohortPhase.Falling,
                        DetachCursor = random.Next(-64, 64), TopExclusive = random.Next(-64, 65)
                    };
                    int expected = minimum;
                    for (int y = scan; y >= minimum; y--)
                    {
                        if (group != null && group.Phase == FallingCohortPhase.Detaching
                            && y >= group.DetachCursor && y < group.TopExclusive) continue;
                        if (!world.TryGet(new Vector3Int(column.x, y, column.y), out _)) continue;
                        expected = y + 1; break;
                    }
                    int actual = minimum, cursor = scan, queries = 0;
                    while (cursor >= minimum)
                    {
                        var arguments = new object[] { world, column, cursor, minimum, group, 0, 0 };
                        bool found = (bool)query.Invoke(null, arguments); queries++;
                        if (found) { actual = (int)arguments[5] + 1; break; }
                        int next = (int)arguments[6]; Assert.That(next, Is.LessThan(cursor)); cursor = next;
                    }
                    Assert.That(actual, Is.EqualTo(expected), $"column={column}, scan={scan}, minimum={minimum}, trial={trial}");
                    Assert.That(queries, Is.LessThanOrEqualTo((scan - minimum + 16 + (minimum & 15)) / 16));
                }
            }
        }

        [Test]
        public void NewBuildCauseStartsAtMatchingPublicationAndKeepsEarliestUnresolvedTime()
        {
            var cell = new Vector3Int(2, 20, 2); var column = new Vector2Int(2, 2);
            var draft = _terrain.World.Clone();
            var journal = Start(draft);
            draft.Set(cell, BlockCell.Create(1)); Call("QueueGravity", cell); Call("QueueGravityStartupAddition", cell);
            Set(_terrain, "_now", 9d);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Publish(draft, 1, 10d); Call("CommitGravityJournal", journal);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.EqualTo(1));
            var committed = ((IDictionary)Read<object>(_terrain, "_startupColumns"))[column];
            Assert.That(Read<double>(committed, "AddedAt"), Is.EqualTo(10d));
            Assert.That(Read<double>(committed, "RemovedAt"), Is.EqualTo(-1d));

            var more = draft.Clone(); var nextJournal = Start(more);
            more.Set(cell + Vector3Int.up, BlockCell.Create(2));
            Call("QueueGravity", cell + Vector3Int.up); Call("QueueGravityStartupAddition", cell + Vector3Int.up);
            var staged = ((IDictionary)Read<object>(Read<object>(nextJournal, "Startup"), "Columns"))[column];
            Assert.That(staged, Is.Not.SameAs(committed), "A pending edit must not mutate the committed ticket.");
            Assert.That(Read<double>(committed, "AddedAt"), Is.EqualTo(10d));
            Publish(more, 2, 12d); Call("CommitGravityJournal", nextJournal);
            var merged = ((IDictionary)Read<object>(_terrain, "_startupColumns"))[column];
            Assert.That(Read<double>(merged, "AddedAt"), Is.EqualTo(10d), "Repeated fire cannot push the oldest unresolved trigger back.");
            Assert.That(Read<uint>(merged, "AdditionRevision"), Is.EqualTo(1u));
            Assert.That(Read<uint>(merged, "Generation"), Is.EqualTo(2u));
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.EqualTo(1));
        }

        [Test]
        public void DiscardedGenerationEditCannotResetCommittedCheckOrOriginTimes()
        {
            var cell = new Vector3Int(2, 20, 2); var column = new Vector2Int(2, 2);
            var initial = Start(_terrain.World);
            Call("QueueGravity", cell); Call("QueueGravityStartupAddition", cell);
            Set(_terrain, "_now", 10d); Call("CommitGravityJournal", initial);
            var checkedJournal = Start(_terrain.World.Clone());
            Set(_terrain, "_now", 11d); Call("ObserveGravityStartupCheck", column);
            Call("CommitGravityJournal", checkedJournal);
            var committed = ((IDictionary)Read<object>(_terrain, "_startupColumns"))[column];
            Assert.That(Read<double>(committed, "CheckStartedAt"), Is.EqualTo(11d));
            var discarded = Start(_terrain.World.Clone());
            Call("QueueGravity", cell); Call("QueueGravityStartupAddition", cell);
            Set(_terrain, "_now", 12d); Call("ObserveGravityStartupCheck", column);
            var pending = ((IDictionary)Read<object>(Read<object>(discarded, "Startup"), "Columns"))[column];
            Assert.That(Read<uint>(pending, "Generation"), Is.EqualTo(2u));
            Assert.That(Read<double>(pending, "CheckStartedAt"), Is.EqualTo(12d));
            Start(_terrain.World.Clone());
            Assert.That(Read<uint>(committed, "Generation"), Is.EqualTo(1u));
            Assert.That(Read<double>(committed, "CheckStartedAt"), Is.EqualTo(11d));
            Assert.That(Read<double>(committed, "AddedAt"), Is.EqualTo(10d));
        }

        [Test]
        public void NewBuildAndRemovalKeepSeparateTimesAndPublishProofDrawPhases()
        {
            var cell = new Vector3Int(2, 20, 2); var column = new Vector2Int(2, 2);
            var added = Start(_terrain.World);
            Call("QueueGravity", cell); Call("QueueGravityStartupAddition", cell);
            Set(_terrain, "_now", 10d); Call("CommitGravityJournal", added);
            var removed = Start(_terrain.World);
            Call("QueueGravity", cell - Vector3Int.up); Call("QueueGravityStartupRemoval", cell - Vector3Int.up);
            Set(_terrain, "_now", 11d); Call("CommitGravityJournal", removed);
            var draft = _terrain.World.Clone(); draft.Set(cell, BlockCell.Create(1));
            var journal = Start(draft); Set(_terrain, "_now", 11.1d); Call("ObserveGravityStartupCheck", column);
            var group = draft.BeginCohort(cell, 1); var id = MotionId(group.Id);
            Call("QueueMotion", id, column, cell.y, cell.y + 1); Call("QueueGravityStartupMotion", id, column);
            Set(_terrain, "_now", 11.2d); Call("ObserveGravityStartupProof", id);
            var run = draft.DetachCohortPiece(group.Id, 1, 11.2d, out _);
            group = draft.Cohorts[group.Id]; group.LandingKnown = true; group.LandingOffset = .5f; group.NeedsLandingProof = false;
            draft.PrepareCohortForPublication(group.Id, out _); draft.ActivateRebasePendingCohorts(11.3d);
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty);
            Publish(draft, 3, 11.3d); Call("CommitGravityJournal", journal);
            var sample = _terrain.InspectGravityStartupSamples().Single();
            Assert.That(sample.reason, Is.EqualTo("NewBuild+SupportRemoval"));
            Assert.That(sample.createdAt, Is.EqualTo(10d)); Assert.That(sample.supportRemovedAt, Is.EqualTo(11d));
            Assert.That(sample.triggerAt, Is.EqualTo(10d)); Assert.That(sample.columnGeneration, Is.EqualTo(2u));
            Assert.That(sample.queueLatencyMs, Is.EqualTo(1100).Within(.0001));
            Assert.That(sample.proofLatencyMs, Is.EqualTo(100).Within(.0001));
            Assert.That(sample.publicationLatencyMs, Is.EqualTo(100).Within(.0001));
            Assert.That(sample.activationLatencyMs, Is.EqualTo(1300).Within(.0001));
            Call("BeginGravityStartupPresentation"); Call("QueueGravityStartupPresentation", run, sample.activationOffset + .02f);
            Call("CommitGravityStartupPresentation", 11.4d);
            Assert.That(sample.totalLatencyMs, Is.EqualTo(1400).Within(.0001));
        }

        [Test]
        public void SupportedAndAnchoredColumnsRetirePriorityWithoutCreatingActivationSamples()
        {
            var world = _terrain.World; var initial = Start(world);
            foreach (int x in new[] { 2, 3 })
            {
                var cell = new Vector3Int(x, 20, 2);
                world.Set(cell, BlockCell.Create(1, true));
                if (x == 3) world.Set(cell + Vector3Int.up, BlockCell.Create(2));
                Call("QueueGravity", cell); Call("QueueGravityStartupAddition", cell);
            }
            Set(_terrain, "_now", 10d); Call("CommitGravityJournal", initial);
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.EqualTo(2));
            for (int i = 0; i < 8 && _terrain.GravityStartupPriorityColumns != 0; i++)
                Call("CommitGravityJournal", StageColumns(4096));
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Assert.That(_terrain.GravityStartupPriorityMotions, Is.Zero);
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty);
            Assert.That(_terrain.World.Cohorts, Is.Empty);
        }

        [Test]
        public void PrioritySharePaysActualCheckDebtAndAdvancesTheOrdinaryCursor()
        {
            int top = Mathf.RoundToInt(BlockWorld.Top / BlockWorld.CellSize); var urgent = new Vector2Int(2, 2);
            foreach (var column in new[] { new Vector2Int(0, 2), new Vector2Int(1, 2), urgent })
            { var work = New("GravityColumnWork"); Set(work, "Scan", top - 16); Call("AddCommittedColumn", column, work); }
            var added = Start(_terrain.World); Call("QueueGravityStartupAddition", new Vector3Int(urgent.x, top - 16, urgent.y));
            Set(_terrain, "_now", 10d); Call("CommitGravityStartup", added);
            var journal = StageColumns(60); var startup = Read<object>(journal, "Startup");
            var columns = (IDictionary)Read<object>(journal, "Columns");
            Assert.That(Read<int>(columns[urgent], "Scan"), Is.EqualTo(top));
            Assert.That(Read<int>(columns[new Vector2Int(0, 2)], "Scan"), Is.EqualTo(top));
            Assert.That(Read<int>(journal, "ColumnCursor"), Is.EqualTo(1));
            // Urgent selection+work cost 30, ordinary work costs 28 and pays 56;
            // a second admitted selection costs 2 even though its row defers.
            Assert.That(Read<long>(startup, "ColumnDebt"), Is.EqualTo(-24));
            Assert.That(Read<int>(startup, "ColumnCursor"), Is.EqualTo(1),
                "The deferred urgent row restores the raw cursor captured before its selection; commit normalizes it.");
            Call("CommitGravityJournal", journal);
            Assert.That(Read<int>(_terrain, "_startupColumnCursor"), Is.Zero,
                "The single-entry committed order normalizes the restored cursor without skipping its turn.");
            Assert.That(Read<long>(_terrain, "_startupColumnDebt"), Is.EqualTo(-24));
            Call("ResetGravityStartup");
            Assert.That(Read<long>(_terrain, "_startupColumnDebt"), Is.Zero);
            Assert.That(Read<long>(_terrain, "_startupMotionDebt"), Is.Zero);
        }

        object StageColumns(int maximum)
        {
            var journal = Start(_terrain.World.Clone());
            var arguments = new object[] { Read<BlockWorld>(journal, "Next"), new HashSet<Vector3Int>(), 0, new Stopwatch(), .75d, maximum };
            typeof(BlockTerrain).GetMethod("StageCohortColumns", Private).Invoke(_terrain, arguments);
            Assert.That((int)arguments[2], Is.LessThanOrEqualTo(maximum));
            return journal;
        }
        object Start(BlockWorld next) { Call("StartGravityJournal", next); return Read<object>(_terrain, "_gravityJournal"); }
        void Publish(BlockWorld world, uint revision, double now)
        { world.Seal(revision, world.RevisionKeys.ToArray()); SetWorld(world); Set(_terrain, "_now", now); }
        void SetWorld(BlockWorld world) => typeof(BlockTerrain).GetProperty("World").SetValue(_terrain, world);
        object Call(string name, params object[] arguments) => typeof(BlockTerrain).GetMethod(name, Private).Invoke(_terrain, arguments);
        static object New(string name) => Activator.CreateInstance(typeof(BlockTerrain).GetNestedType(name, BindingFlags.NonPublic), true);
        static object MotionId(ulong id) => Activator.CreateInstance(typeof(BlockTerrain).GetNestedType("GravityMotionId", BindingFlags.NonPublic),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { id, true }, null);
        static T Read<T>(object target, string field) => (T)target.GetType().GetField(field, Private | BindingFlags.Public).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Private | BindingFlags.Public).SetValue(target, value);
    }
}
#endif
