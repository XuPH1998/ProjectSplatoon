#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // The deterministic tests below execute the real production scheduling and
    // proof routines, with no fabricated ProofKnown/cursor/head state. Their
    // stopped Stopwatch isolates the 4096-check branch; it is not a timing test.
    // The final test separately uses real Tick/bake/collider publication.
    public sealed class BlockGravityMotionBudgetFairnessTests
    {
        const double Epoch = 10;
        const int MaximumChecks = 4096;
        const double GravityMilliseconds = .75;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly Vector3Int Floor = new(2, 0, 2);
        GameObject _root;
        BlockTerrain _terrain;
        int _installedMotionCount;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Gravity motion fresh-stage admission contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            _terrain.Initialize(null);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator LowestReadyMotionGetsFreshBudgetAfterNineRealSupportWaits()
        {
            var seed = Seed(true, out ulong lowest, 11, true);
            yield return Install(seed);
            WarmProofsWithoutChangingPublishedWorld();
            Assert.That((int)Field(_terrain, "_cohortMotionCursor"), Is.Zero);
            var order = (IList)Field(_terrain, "_cohortMotionOrder");
            Assert.That(MotionId(order[9]), Is.EqualTo(lowest));

            // Tick(false) advances only the normal simulation clock here. The
            // authority index/proofs remain the naturally warmed private state.
            _terrain.Tick(false, Epoch + 10, .016f);
            var source = _terrain.World;
            byte[] frozen = source.EncodeSnapshot();
            Assert.That(source.Cohorts[lowest].Offset(Epoch + 10),
                Is.EqualTo(source.Cohorts[lowest].LandingOffset));

            var first = StageFalling();
            Assert.That(first.changed.Count, Is.Zero);
            Assert.That(first.next.Falling.Count, Is.EqualTo(11));
            Assert.That(first.checks, Is.EqualTo(3967),
                "Nine deep support waits spend 9*435; root visit/binding 34, Offset 1, head 16 and member 1 spend 3967.");
            Assert.That(Field(first.journal, "MotionVisitBudgetDeferred"), Is.EqualTo(true));
            CollectionAssert.AreEqual(frozen, first.next.EncodeSnapshot());
            CommitUnchangedJournal(first.next, first.journal);
            Assert.That((int)Field(_terrain, "_cohortMotionCursor"), Is.EqualTo(9),
                "The real 182-check settlement rejected by 129 remaining checks must get the next fresh stage.");

            var second = StageFalling();
            Assert.That(second.checks, Is.InRange(1, MaximumChecks));
            Assert.That(second.next.Cohorts.ContainsKey(lowest), Is.False);
            Assert.That(second.next.TryGet(Floor + Vector3Int.up, out var landed), Is.True);
            Assert.That(landed.Owner, Is.EqualTo(2));
            Assert.That(second.next.PinkCount + second.next.BlueCount, Is.EqualTo(12));
            Assert.That(source.Cohorts.ContainsKey(lowest), Is.True);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot(),
                "The real unpublished settlement draft cannot mutate the retained published World.");
            // Do not fake a collider publication for this changed draft.
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator MotionStillInFlightRotatesAfterItReceivesItsFreshBudget()
        {
            var seed = Seed(true, out ulong lowest, 12);
            yield return Install(seed);
            WarmProofsWithoutChangingPublishedWorld();
            var order = (IList)Field(_terrain, "_cohortMotionOrder");
            ulong deferred = MotionId(order[9]);
            Assert.That(deferred, Is.Not.EqualTo(lowest));
            Assert.That(_terrain.World.Cohorts[deferred].LowerSupport, Is.Not.Null);
            Assert.That(_terrain.World.Cohorts[deferred].LowerSupport.LowerSupport, Is.Not.Null);
            byte[] frozen = _terrain.World.EncodeSnapshot();
            var first = StageFalling();
            Assert.That(first.checks, Is.EqualTo(3950));
            Assert.That(Field(first.journal, "MotionVisitBudgetDeferred"), Is.EqualTo(true));
            CommitUnchangedJournal(first.next, first.journal);
            Assert.That((int)Field(_terrain, "_cohortMotionCursor"), Is.EqualTo(9));

            // Index 9 is a real depth-three upper, not the lowest root. Its
            // 400-check Offset is rejected after 3950 checks, with 146 remaining.
            // At Epoch every body is genuinely still at zero offset. Receiving
            // its complete trajectory check must not pin this geometric wait.
            var second = StageFalling();
            Assert.That(second.checks, Is.InRange(435, MaximumChecks));
            CommitUnchangedJournal(second.next, second.journal);
            int cursor = (int)Field(_terrain, "_cohortMotionCursor");
            Assert.That(MotionId(order[cursor]), Is.Not.EqualTo(deferred),
                "A complete not-yet-at-landing visit rotates; only a new admission failure retains a turn.");
            Assert.That(_terrain.FallingCount, Is.EqualTo(12));
            Assert.That(_terrain.World.Cohorts[deferred].Offset(Epoch),
                Is.LessThan(_terrain.World.Cohorts[deferred].LandingOffset));
            CollectionAssert.AreEqual(frozen, _terrain.World.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator BothSourceIdentityOrdersFinishThroughRealAtomicPublication()
        {
            foreach (bool topFirst in new[] { true, false })
            {
                var seed = Seed(topFirst, out _);
                yield return Install(seed);
                int publications = 0;
                Action<BlockTerrainCommit> observe = commit =>
                {
                    publications++;
                    Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                    Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(11));
                    var wire = _terrain.ExportSnapshot();
                    CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(wire).EncodeSnapshot());
                };
                _terrain.PublishedCommit += observe;
                try
                {
                    // Natural authority setup at exactly the source activation
                    // epoch prevents real settlement while proofs/index warm.
                    for (int frame = 0; frame < 3000 && !AllProofsKnown(); frame++)
                    { _terrain.Tick(true, Epoch, .016f); yield return null; }
                    Assert.That(AllProofsKnown(), Is.True);
                    Assert.That(_terrain.FallingCount, Is.EqualTo(10));
                    for (int frame = 0; frame < 3000 && (_terrain.FallingCount != 0 || _terrain.PendingTransactions != 0 || !_terrain.LossOfSupportReady); frame++)
                    { _terrain.Tick(true, Epoch + 10 + frame * .016, .016f); yield return null; }
                    Assert.That(_terrain.FallingCount, Is.Zero);
                    Assert.That(_terrain.PendingTransactions, Is.Zero);
                    Assert.That(_terrain.LossOfSupportReady, Is.True);
                    Assert.That(publications, Is.GreaterThan(0));
                    for (int y = 0; y <= 10; y++)
                    {
                        var cell = Floor + Vector3Int.up * y;
                        Assert.That(_terrain.World.TryGet(cell, out var value), Is.True);
                        Assert.That(value.Owner, Is.EqualTo(2));
                        Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.forward * .08f, Vector3.back,
                            out var hit, .16f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                        Assert.That(_terrain.TryContact(hit, out var contact) && contact.Stable && contact.Cell == cell, Is.True);
                    }
                }
                finally { _terrain.PublishedCommit -= observe; }
            }
        }

        static BlockWorld Seed(bool topFirst, out ulong lowest, int count = 10, bool lowestBeforeLevelOne = false)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            world.Set(Floor, BlockCell.Create(2, true));
            Assert.That(count, Is.GreaterThanOrEqualTo(3));
            Assert.That(!lowestBeforeLevelOne || topFirst, Is.True);
            var byLevel = new FallingCohort[count];
            int[] creationOrder = Enumerable.Range(0, count).ToArray();
            if (topFirst) Array.Reverse(creationOrder);
            if (lowestBeforeLevelOne)
                creationOrder = Enumerable.Range(2, count - 2).Reverse().Concat(new[] { 0, 1 }).ToArray();
            foreach (int level in creationOrder)
            {
                var bottom = Floor + Vector3Int.up * (2 + 2 * level);
                world.Set(bottom, BlockCell.Create(2));
                var group = world.BeginCohort(bottom, 1);
                world.DetachCohortPiece(group.Id, 1, Epoch, out _);
                group = world.EditCohort(group.Id, out _);
                group.LandingKnown = true;
                group.LandingOffset = (bottom.y - 1) * BlockWorld.CellSize;
                byLevel[level] = group;
            }
            for (int level = 1; level < count; level++)
                world.SetCohortSupport(byLevel[level].Id, byLevel[level - 1].Id, BlockWorld.CellSize, out _);
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray());
            lowest = byLevel[0].Id;
            var wire = world.EncodeSnapshot();
            CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(wire).EncodeSnapshot());
            return world;
        }

        IEnumerator Install(BlockWorld seed)
        {
            _installedMotionCount = seed.Falling.Count;
            Assert.That(_installedMotionCount, Is.GreaterThan(0));
            Assert.That(seed.Cohorts.Count, Is.EqualTo(_installedMotionCount));
            uint revision = _terrain.Revision + 1;
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            for (int frame = 0; frame < 3000 && (_terrain.Revision < revision || _terrain.PendingTransactions != 0); frame++)
            { _terrain.Tick(false, Epoch, .016f); yield return null; }
            Assert.That(_terrain.Revision, Is.EqualTo(revision));
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.FallingCount, Is.EqualTo(_installedMotionCount));
        }

        void WarmProofsWithoutChangingPublishedWorld()
        {
            // Indexing uses the production published source. Individual real
            // landing proofs then establish the precise unit-test precondition,
            // without spending or resetting the scheduler's motion cursor.
            var indexed = _terrain.World.Clone();
            Call("StartGravityJournal", indexed);
            try
            {
                var indexArgs = new object[] { indexed, 0, new Stopwatch(), GravityMilliseconds, MaximumChecks };
                Call("IndexGravityWork", indexArgs);
                Assert.That(Field(_terrain, "_cohortIndexPending"), Is.EqualTo(false));
                CommitUnchangedJournal(indexed, Field(_terrain, "_gravityJournal"));
            }
            finally { SetTransientJournal(null); }
            var order = ((IEnumerable)Field(_terrain, "_cohortMotionOrder")).Cast<object>().ToArray();
            foreach (var motion in order)
            {
                var next = _terrain.World.Clone();
                Call("StartGravityJournal", next);
                try
                {
                    var work = Call("EditMotion", motion);
                    var args = new object[] { next, new HashSet<Vector3Int>(), motion, next.Cohorts[MotionId(motion)], null,
                        work, 0, new Stopwatch(), GravityMilliseconds, MaximumChecks, true, 0 };
                    Call("StageMotionLandingAndSettle", args);
                    Assert.That((int)args[6], Is.InRange(1, MaximumChecks));
                    Assert.That(Field(work, "ProofKnown"), Is.EqualTo(true));
                    Assert.That(Field(work, "NativeProof"), Is.Not.Null);
                    CommitUnchangedJournal(next, Field(_terrain, "_gravityJournal"));
                }
                finally { SetTransientJournal(null); }
            }
            Assert.That(AllProofsKnown(), Is.True);
        }

        bool AllProofsKnown()
        {
            var motions = (IDictionary)Field(_terrain, "_cohortMotionWork");
            return _installedMotionCount > 0 && motions.Count == _installedMotionCount && motions.Values.Cast<object>().All(work =>
                (bool)Field(work, "ProofKnown") && Field(work, "NativeProof") != null);
        }

        (BlockWorld next, HashSet<Vector3Int> changed, int checks, object journal) StageFalling()
        {
            var next = _terrain.World.Clone(); var changed = new HashSet<Vector3Int>();
            Call("StartGravityJournal", next);
            try
            {
                var args = new object[] { next, changed, 0, new Stopwatch(), GravityMilliseconds, MaximumChecks };
                Call("StageCohortFalling", args);
                return (next, changed, (int)args[2], Field(_terrain, "_gravityJournal"));
            }
            finally { SetTransientJournal(null); }
        }

        void CommitUnchangedJournal(BlockWorld next, object journal)
        {
            CollectionAssert.AreEqual(_terrain.World.EncodeSnapshot(), next.EncodeSnapshot(),
                "Only a byte-identical published-world proof/index journal may use the scheduler-only seam.");
            Assert.That(Field(journal, "DependentEditRequest"), Is.Null);
            Assert.That(Field(journal, "PrivateRequest"), Is.Null);
            Assert.That(Field(journal, "RecoveryRequest"), Is.Null);
            Assert.That(Field(journal, "HeadPreparationColumn"), Is.Null);
            Assert.That(((IDictionary)Field(journal, "BindingContinuity")).Count, Is.Zero);
            Assert.That(((IDictionary)Field(journal, "OwnActivationProofs")).Count, Is.Zero);
            Call("CommitGravityJournal", journal);
        }

        void SetTransientJournal(object value) => typeof(BlockTerrain).GetField("_gravityJournal", Private).SetValue(_terrain, value);
        object Call(string name, params object[] arguments)
            => typeof(BlockTerrain).GetMethod(name, Private).Invoke(_terrain, arguments);
        static object Field(object owner, string name)
            => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);
        static ulong MotionId(object motion) => (ulong)Field(motion, "Id");
    }
}
#endif
