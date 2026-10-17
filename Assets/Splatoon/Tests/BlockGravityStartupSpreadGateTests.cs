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
    // Real production queue/stage/commit contracts. They deliberately use
    // anchored stable cells so serving startup cannot require worker Tasks.
    public sealed class BlockGravityStartupSpreadGateTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        readonly Vector3Int _cell = new(2, 20, 2);

        [SetUp]
        public void Setup()
        {
            _root = new GameObject("Committed startup visits before spread quanta");
            _terrain = _root.AddComponent<BlockTerrain>();
            typeof(BlockTerrain).GetProperty("World").SetValue(_terrain, new BlockWorld(new Vector2(32, 64), 3));
            Set(_terrain, "_now", 10d);
        }
        [TearDown] public void Teardown() => Object.DestroyImmediate(_root);

        [Test]
        public void NewCauseWaitsForACommittedBoundedVisitAndDiscardCannotEarnThePermit()
        {
            AddStableCause(_cell, 10);
            Call("DirtySpread", new Vector2Int(_cell.x, _cell.z));
            Call("ScheduleSpreadWork");
            Assert.That(Commands(), Is.Empty, "The first spread quantum must give a newly published cause a turn.");
            var discarded = StageColumns(30);
            Assert.That(Read<ulong>(Read<object>(discarded, "Startup"), "ServedVisits"), Is.EqualTo(1UL));
            Assert.That(_terrain.GravityStartupServedVersion, Is.Zero);
            Call("ScheduleSpreadWork"); Assert.That(Commands(), Is.Empty);
            Start(_terrain.World.Clone()); // Discard that private bounded visit.
            Assert.That(_terrain.GravityStartupServedVersion, Is.Zero);
            Call("ScheduleSpreadWork"); Assert.That(Commands(), Is.Empty);

            var admitted = StageColumns(30); Call("CommitGravityJournal", admitted);
            Assert.That(_terrain.GravityStartupServedVersion, Is.EqualTo(1UL));
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.EqualTo(1), "The queue is deliberately still nonempty.");
            Call("ScheduleSpreadWork");
            Assert.That(Commands().Length, Is.EqualTo(1));
            Assert.That(Read<bool>(Commands().Single(), "Spread"), Is.True);
            Call("ScheduleSpreadWork"); Assert.That(Commands().Length, Is.EqualTo(1), "One visit allows one queued quantum.");
        }

        [Test]
        public void OrdinaryCursorServingAStartupTicketAllowsSpreadDuringRepeatedStableConstruction()
        {
            AddStableCause(_cell, 10);
            Call("DirtySpread", new Vector2Int(_cell.x, _cell.z));
            Set(_terrain, "_startupColumnTurn", false); // Exercise the ordinary cursor hitting urgent work.
            Call("ScheduleSpreadWork"); Assert.That(Commands(), Is.Empty);
            var ordinary = StageColumns(28); Call("CommitGravityJournal", ordinary);
            Assert.That(_terrain.GravityStartupServedVersion, Is.EqualTo(1UL));
            Call("ScheduleSpreadWork"); Assert.That(Commands().Length, Is.EqualTo(1));
            RunOneRealSpreadQuantum();

            for (int round = 0; round < 8; round++)
            {
                AddStableCause(_cell + Vector3Int.up * (round + 1), 11 + round);
                Call("DirtySpread", new Vector2Int(_cell.x, _cell.z));
                Set(_terrain, "_spreadNextAt", 0d);
                Call("ScheduleSpreadWork");
                Assert.That(Commands(), Is.Empty, "Every executed quantum requires a new bounded startup visit.");
                ulong before = _terrain.GravityStartupServedVersion;
                for (int visit = 0; visit < 12 && Commands().Length == 0; visit++)
                {
                    Call("CommitGravityJournal", StageColumns(30));
                    Call("ScheduleSpreadWork");
                }
                Assert.That(Commands().Length, Is.EqualTo(1), "Stable continuous building must not permanently suppress spreading.");
                Assert.That(_terrain.GravityStartupServedVersion, Is.GreaterThan(before));
                RunOneRealSpreadQuantum();
            }
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(9));
            Assert.That(_terrain.SpreadMoves, Is.Zero, "Anchored fixture cells must remain fixed while real search quanta complete.");
            Assert.That(_terrain.World.BlueCount, Is.EqualTo(9));
        }

        [Test]
        public void AQueuedFifoQuantumIsRetainedAndRoundResetCannotReuseOldVisitCredit()
        {
            Call("DirtySpread", new Vector2Int(_cell.x, _cell.z));
            Call("ScheduleSpreadWork"); var queued = Commands().Single();
            AddStableCause(_cell, 10);
            Call("ScheduleSpreadWork");
            Assert.That(Commands().Single(), Is.SameAs(queued), "A later cause cannot withdraw an already queued FIFO command.");
            _terrain.Clear(4);
            Assert.That(Commands(), Is.Empty);
            Assert.That(_terrain.GravityStartupServedVersion, Is.Zero);
            Assert.That(Read<bool>(_terrain, "_spreadStartupWaiting"), Is.False);
            Assert.That(Read<ulong>(_terrain, "_spreadStartupWaitVersion"), Is.Zero);
            AddStableCause(_cell, 12);
            Call("DirtySpread", new Vector2Int(_cell.x, _cell.z));
            Set(_terrain, "_spreadNextAt", 0d);
            Call("ScheduleSpreadWork");
            Assert.That(Commands(), Is.Empty, "The new round must serve its own newly published cause.");
        }

        [Test]
        public void EnteringAStageOrAdmittingOnlyItsRejectedRemainderDoesNotEarnSpreadCredit()
        {
            AddStableCause(_cell, 10); Call("DirtySpread", new Vector2Int(_cell.x, _cell.z));
            Call("ScheduleSpreadWork");
            Call("CommitGravityJournal", StageColumns(1));
            Assert.That(_terrain.GravityStartupServedVersion, Is.Zero);
            Call("ScheduleSpreadWork"); Assert.That(Commands(), Is.Empty);
            // Selector + entry COW fit, but the actual first support row does not.
            Call("CommitGravityJournal", StageColumns(29));
            Assert.That(_terrain.GravityStartupServedVersion, Is.Zero);
            Call("ScheduleSpreadWork"); Assert.That(Commands(), Is.Empty);
            Call("CommitGravityJournal", StageColumns(30));
            Assert.That(_terrain.GravityStartupServedVersion, Is.GreaterThan(0));
            Call("ScheduleSpreadWork"); Assert.That(Commands().Length, Is.EqualTo(1));
        }

        [Test]
        public void ACompletedEmptyQueueCannotBankPriorityDebtForTheNextCause()
        {
            AddStableCause(_cell, 10);
            for (int i = 0; i < 8 && _terrain.GravityStartupPriorityColumns != 0; i++)
                Call("CommitGravityJournal", StageColumns(4096));
            Assert.That(_terrain.GravityStartupPriorityColumns, Is.Zero);
            Assert.That(Read<long>(_terrain, "_startupColumnDebt"), Is.Zero);
            Assert.That(Read<long>(_terrain, "_startupMotionDebt"), Is.Zero);
            Assert.That(Read<bool>(_terrain, "_startupColumnTurn"), Is.True);
            Assert.That(Read<bool>(_terrain, "_startupMotionTurn"), Is.True);
            AddStableCause(_cell + Vector3Int.up, 20);
            Assert.That(Read<long>(_terrain, "_startupColumnDebt"), Is.Zero);
            Assert.That(Read<bool>(_terrain, "_startupColumnTurn"), Is.True);
            var ticket = ((IDictionary)Read<object>(_terrain, "_startupColumns"))[new Vector2Int(_cell.x, _cell.z)];
            Assert.That(Read<double>(ticket, "AddedAt"), Is.EqualTo(20d), "A retired event cannot give the next cause an old timestamp.");
        }

        void AddStableCause(Vector3Int cell, double now)
        {
            var world = _terrain.World; var journal = Start(world);
            Assert.That(world.Set(cell, BlockCell.Create(2, true)), Is.True);
            Call("QueueGravity", cell); Call("QueueGravityStartupAddition", cell);
            Set(_terrain, "_now", now); Call("CommitGravityJournal", journal);
        }
        object StageColumns(int maximum)
        {
            var journal = Start(_terrain.World.Clone());
            var arguments = new object[] { Read<BlockWorld>(journal, "Next"), new HashSet<Vector3Int>(), 0, new Stopwatch(), 1000d, maximum };
            typeof(BlockTerrain).GetMethod("StageCohortColumns", Private).Invoke(_terrain, arguments);
            Assert.That((int)arguments[2], Is.LessThanOrEqualTo(maximum));
            return journal;
        }
        void RunOneRealSpreadQuantum()
        {
            var command = Commands().Single(); var next = _terrain.World.Clone();
            Set(_terrain, "_stagePublicationWindow", _terrain.PublicationPolicy.Capture(BlockPublicationOperation.Gravity));
            var journal = Start(next);
            var commandType = command.GetType();
            var resultType = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            var results = Activator.CreateInstance(typeof(List<>).MakeGenericType(resultType));
            var arguments = new object[] { command, next, new HashSet<Vector3Int>(), results, 0, new Stopwatch(), 1000d, 4096 };
            typeof(BlockTerrain).GetMethod("StageSpread", Private).Invoke(_terrain, arguments);
            Assert.That(Read<bool>(command, "Done"), Is.True);
            Assert.That(Commands(), Is.Empty);
            Call("CommitGravityJournal", journal); Call("Complete", command, default(BlockMutationResult));
            Assert.That(Read<bool>(_terrain, "_spreadQueued"), Is.False);
            Set(_terrain, "_stagePublicationWindow", null);
        }
        object[] Commands() => ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().ToArray();
        object Start(BlockWorld world) { Call("StartGravityJournal", world); return Read<object>(_terrain, "_gravityJournal"); }
        object Call(string name, params object[] arguments) => typeof(BlockTerrain).GetMethod(name, Private).Invoke(_terrain, arguments);
        static T Read<T>(object target, string field) => (T)target.GetType().GetField(field, Private | BindingFlags.Public).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Private | BindingFlags.Public).SetValue(target, value);
    }
}
#endif
