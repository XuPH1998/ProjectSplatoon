#if UNITY_EDITOR
using System;
using System.Collections;
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
    // Private classifier boundary harness, deliberately separate from natural
    // authority discovery and from the unchanged original100 Player gates.
    // Every index/classifier admission retains4096/.75ms production limits.
    public sealed class BlockGravityHistoricalScaleBoundaryTests
    {
        const double Now = 10;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Full400 classifier boundary acceptance");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            // No registered static geometry: the full-height seed has a legal
            // lower boundary at World.Bottom and must complete all pair checks.
            _terrain.Tick(false, Now, .016f);
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
        public IEnumerator Full400CellClassifierYieldsAndFinishesNoLongerInvalidWithoutPublication()
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            int first = Mathf.CeilToInt(seed.Bottom / BlockWorld.CellSize);
            int end = Mathf.CeilToInt(BlockWorld.Top / BlockWorld.CellSize);
            Assert.That(end - first, Is.EqualTo(400));
            var bottom = new Vector3Int(2, first, 2);
            for (int y = first; y < end; y++)
                seed.Set(new Vector3Int(2, y, 2), BlockCell.Create((byte)(y % 2 == 0 ? 1 : 2)));
            var group = seed.BeginCohort(bottom, end - first);
            for (int left = end - first; left > 0;)
            {
                int count = Math.Min(left, BlockWorld.MaxCohortPieceCells);
                seed.DetachCohortPiece(group.Id, count, Now, out _); left -= count;
            }
            // Modeled stationary seed declares its own floor before first activation.
            group = seed.EditCohort(group.Id, out _); group.LandingKnown = true; group.LandingOffset = 0;
            seed.PreparePendingCohortsForPublication(out _); seed.ActivateRebasePendingCohorts(Now);
            seed.Seal(1, seed.RevisionKeys.ToArray()); byte[] wire = seed.EncodeSnapshot();
            CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(wire).EncodeSnapshot());
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            for (int step = 0; step < 3000 && (_terrain.Revision != 1 || _terrain.PendingTransactions != 0); step++)
            { _terrain.Tick(false, Now, .016f); yield return null; }
            Assert.That(_terrain.Revision, Is.EqualTo(1)); Assert.That(_terrain.PendingTransactions, Is.Zero);
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            Assert.That(source.Falling.Values.Sum(run => run.Cells.Count(cell => cell.Occupied)), Is.EqualTo(400));
            // Explicit private admission is necessary for a geometrically legal
            // body. This does not claim it has a natural authority repair trigger.
            typeof(BlockTerrain).GetField("_cohortAuthorityInitialized", Private).SetValue(_terrain, true);
            Invoke("StartGravityJournal", source);
            for (int step = 0; step < 3000 && (bool)Field("_cohortIndexPending"); step++)
            {
                var args = new object[] { source, 0, Stopwatch.StartNew(), .75d, 4096 };
                Method("IndexGravityWork").Invoke(_terrain, args);
                Assert.That((int)args[1], Is.LessThanOrEqualTo(4096));
                yield return null;
            }
            Assert.That(Field("_cohortIndexPending"), Is.EqualTo(false));
            typeof(BlockTerrain).GetField("_gravityJournal", Private).SetValue(_terrain, null);
            var motionType = typeof(BlockTerrain).GetNestedType("GravityMotionId", BindingFlags.NonPublic);
            var motion = Activator.CreateInstance(motionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { group.Id, true }, null);
            Assert.That((bool)Invoke("RequestHistoricalRecovery", motion, new Vector2Int(2, 2)), Is.True);
            long workBefore = _terrain.HistoricalRecoveryWork, stagesBefore = _terrain.HistoricalRecoveryStages;
            int pairsBefore = 0, pairsAfter = 0;
            Action<BlockTerrainCommit> unexpected = _ => Assert.Fail("A legal400-cell classifier must not publish or settle while classifying.");
            _terrain.PublishedCommit += unexpected;
            try
            {
                for (int step = 0; step < 4000 && _terrain.LastHistoricalRecoveryOutcome != BlockTerrain.HistoricalRecoveryOutcome.NoLongerInvalid; step++)
                {
                    var before = Field("_historicalRecoveryPreparation");
                    if (before != null && (int)Read(before, "Phase") == 12) pairsBefore++;
                    long checks = _terrain.GravityWorkChecks;
                    _terrain.Tick(true, Now, .016f);
                    Assert.That(_terrain.GravityWorkChecks - checks, Is.LessThanOrEqualTo(4096));
                    var after = Field("_historicalRecoveryPreparation");
                    if (after != null && (int)Read(after, "Phase") == 12) pairsAfter++;
                    CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
                    Assert.That(_terrain.World, Is.SameAs(source));
                    if (_terrain.LastHistoricalRecoveryOutcome == BlockTerrain.HistoricalRecoveryOutcome.NoLongerInvalid) break;
                    yield return null;
                }
            }
            finally { _terrain.PublishedCommit -= unexpected; }
            Assert.That(_terrain.LastHistoricalRecoveryOutcome, Is.EqualTo(BlockTerrain.HistoricalRecoveryOutcome.NoLongerInvalid));
            Assert.That(pairsBefore, Is.GreaterThan(1)); Assert.That(pairsAfter, Is.GreaterThan(1));
            Assert.That(_terrain.HistoricalRecoveryWork - workBefore, Is.GreaterThanOrEqualTo(4L * 400 * 400));
            Assert.That(_terrain.HistoricalRecoveryStages - stagesBefore, Is.GreaterThan(1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero); Assert.That(_terrain.HistoricalRecoveryNoCapacity, Is.Zero);
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.Revision, Is.EqualTo(1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(200)); Assert.That(_terrain.BlueCount, Is.EqualTo(200));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, _terrain.ExportSnapshot());
        }

        [Test]
        [Timeout(600000)]
        public void CohortAdmissionRejects401BeforeChangingTheSourceOrAllocatingIdentity()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            byte[] before = world.EncodeSnapshot(); ulong next = world.NextCohortId;
            Assert.Throws<ArgumentOutOfRangeException>(() => world.BeginCohort(new Vector3Int(2, -80, 2), 401));
            Assert.That(world.NextCohortId, Is.EqualTo(next));
            CollectionAssert.AreEqual(before, world.EncodeSnapshot());
        }

        object Field(string name) => Read(_terrain, name);
        static object Read(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field " + name); return field.GetValue(target);
        }
        MethodInfo Method(string name) => typeof(BlockTerrain).GetMethod(name, Private);
        object Invoke(string name, params object[] args) => Method(name).Invoke(_terrain, args);
    }
}
#endif
