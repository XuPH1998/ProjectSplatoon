#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    // The first real regroup publication is the observable contract boundary.
    // A later activation publication cannot repair a failed first-publication assertion.
    public sealed class BlockGravityCohortPublicationEpochTests
    {
        HistoricalBlockWorldHeightScope _historicalHeight;
        const double Started = 10, CapturedAt = 10.1, PublicationAt = 10.3;
        static readonly Vector3Int Bottom = new(2, 80, 2);
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            _historicalHeight = new HistoricalBlockWorldHeightScope();
            _root = new GameObject("Cohort first regroup publication epoch contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _historicalHeight?.Dispose(); _historicalHeight = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator MovingFractureRebasesAtItsFirstRegroupPublicationWithCurrentPositiveVelocity()
            => FractureAtFirstPublication(false);

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ClampedFractureRebasesAtItsFirstRegroupPublicationWithZeroInstantaneousVelocity()
            => FractureAtFirstPublication(true);

        IEnumerator FractureAtFirstPublication(bool clamped)
        {
            if (clamped) RegisterFloor();
            var seed = new BlockWorld(new Vector2(32, 64));
            for (int i = 0; i < 16; i++) seed.Set(Bottom + Vector3Int.up * i, BlockCell.Create(2));
            var parent = seed.BeginCohort(Bottom, 16);
            var run = seed.DetachCohortPiece(parent.Id, 16, Started, out _);
            // Both modeled seeds own a complete cap: the real nearby floor
            // for clamped motion, or baseline ground for the moving case.
            parent.LandingKnown = true;
            parent.LandingOffset = clamped ? .2f : Bottom.y * BlockWorld.CellSize;
            seed.PreparePendingCohortsForPublication(out _);
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            seed.ActivateRebasePendingCohorts(Started);
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && _terrain.PendingTransactions != 0; tick++)
            {
                _terrain.Tick(false, CapturedAt, .016f);
                yield return null;
            }
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.BlueCount, Is.EqualTo(16));
            Assert.That(_terrain.World.Cohorts[parent.Id].Phase, Is.EqualTo(FallingCohortPhase.Falling));

            var retainedWorld = _terrain.World;
            var retainedParent = retainedWorld.Cohorts[parent.Id];
            byte[] frozen = retainedWorld.EncodeSnapshot();
            var removedSource = Bottom + Vector3Int.up * 5;
            Vector3 point = BlockWorld.Center(removedSource) - Vector3.up * retainedParent.Offset(CapturedAt);
            int callbacks = 0, regroupPublications = 0;
            uint firstRegroupRevision = 0;
            FallingCohort[] firstChildren = null;
            _terrain.QueueDestroy(point, .0001f, 0, 1, true, result =>
            {
                callbacks++;
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                Assert.That(result.RemovedFriendly, Is.Zero);
            }, 1, 0, true);

            Action<BlockTerrainCommit> observe = commit =>
            {
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                if (_terrain.World.Cohorts.ContainsKey(parent.Id) || regroupPublications != 0) return;
                regroupPublications++;
                firstRegroupRevision = commit.Revision;
                Assert.That(_terrain.SimulationTime, Is.EqualTo(PublicationAt));
                firstChildren = _terrain.World.Cohorts.Values
                    .Where(child => child.Bottom.x == Bottom.x && child.Bottom.z == Bottom.z)
                    .OrderBy(child => child.Bottom.y).ToArray();
                Assert.That(firstChildren.Length, Is.EqualTo(2));
                float expectedOffset = retainedParent.Offset(PublicationAt);
                float expectedSpeed = retainedParent.Speed(PublicationAt);
                if (clamped)
                {
                    Assert.That(expectedOffset, Is.EqualTo(.2f).Within(1e-6));
                    Assert.That(expectedSpeed, Is.Zero.Within(1e-6));
                }
                else Assert.That(expectedSpeed, Is.GreaterThan(0));

                var exactSources = new HashSet<Vector3Int>();
                foreach (var child in firstChildren)
                {
                    Assert.That(child.DetachCursor, Is.EqualTo(child.TopExclusive));
                    Assert.That(child.Phase, Is.EqualTo(FallingCohortPhase.Falling),
                        "fully detached moving children must be active in the first replacement publication, without a dormant interval");
                    Assert.That(child.StartedAt, Is.EqualTo(PublicationAt));
                    Assert.That(child.InitialOffset, Is.EqualTo(expectedOffset).Within(1e-6),
                        "sample the source at actual first regroup publication, not at earlier staging or a later activation");
                    Assert.That(child.InitialSpeed, Is.EqualTo(expectedSpeed).Within(1e-6));
                    Assert.That(child.Offset(PublicationAt), Is.EqualTo(expectedOffset).Within(1e-6));
                    foreach (ulong member in child.MemberIds)
                    {
                        var fragment = _terrain.World.Falling[member];
                        Assert.That(fragment.Cohort, Is.SameAs(child));
                        Assert.That(fragment.StartedAt, Is.EqualTo(PublicationAt));
                        Assert.That(fragment.InitialSpeed, Is.EqualTo(expectedSpeed).Within(1e-6));
                        for (int i = 0; i < fragment.Cells.Length; i++)
                        {
                            if (!fragment.Cells[i].Occupied) continue;
                            Assert.That(fragment.Cells[i].Owner, Is.EqualTo(2));
                            Assert.That(exactSources.Add(fragment.Bottom + Vector3Int.up * i), Is.True);
                        }
                    }
                }
                Assert.That(exactSources.Count, Is.EqualTo(15));
                Assert.That(exactSources.Contains(removedSource), Is.False);
                for (int i = 0; i < 16; i++) if (i != 5)
                    Assert.That(exactSources.Contains(Bottom + Vector3Int.up * i), Is.True);
                Assert.That(_terrain.BlueCount, Is.EqualTo(15));
                Assert.That(retainedWorld.Falling[run.Id].Cells[5].Occupied, Is.True);
                Assert.That(retainedParent.Phase, Is.EqualTo(FallingCohortPhase.Falling));
                CollectionAssert.AreEqual(frozen, retainedWorld.EncodeSnapshot());
            };
            _terrain.PublishedCommit += observe;
            try
            {
                for (int tick = 0; tick < 2000 && regroupPublications == 0; tick++)
                {
                    long commandChecks = _terrain.CommandWorkChecks, gravityChecks = _terrain.GravityWorkChecks;
                    _terrain.Tick(true, PublicationAt, .016f);
                    Assert.That(_terrain.CommandWorkChecks - commandChecks + _terrain.GravityWorkChecks - gravityChecks,
                        Is.InRange(0L, 4096L));
                    yield return null;
                }
                Assert.That(regroupPublications, Is.EqualTo(1), "actual terrain regroup must publish; the bounded Editor loop is not a formal timing gate");
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(_terrain.FailedTransactions, Is.Zero);
                Assert.That(firstChildren, Is.Not.Null);
                // Evaluate the already-published descriptors without advancing another
                // transaction: a legitimate later settle publication is unrelated to
                // the first-publication activation contract tested above.
                double movementAt = PublicationAt + .02;
                Assert.That(_terrain.Revision, Is.EqualTo(firstRegroupRevision), "the sampled descriptors belong to the first regroup publication");
                foreach (var child in firstChildren)
                {
                    Assert.That(_terrain.World.Cohorts[child.Id], Is.SameAs(child));
                    Assert.That(child.StartedAt, Is.EqualTo(PublicationAt));
                    Assert.That(child.Offset(movementAt), Is.GreaterThanOrEqualTo(child.InitialOffset - 1e-6f));
                    if (!clamped) Assert.That(child.Offset(movementAt), Is.GreaterThan(child.InitialOffset));
                }
                CollectionAssert.AreEqual(frozen, retainedWorld.EncodeSnapshot());
                Assert.That(_terrain.BlueCount, Is.EqualTo(15));
            }
            finally { _terrain.PublishedCommit -= observe; }
        }

        void RegisterFloor()
        {
            var floor = new GameObject("Real static landing floor for zero velocity rebase");
            floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(BlockWorld.Center(Bottom).x, 7.75f, BlockWorld.Center(Bottom).z);
            var collider = floor.AddComponent<BoxCollider>();
            collider.size = new Vector3(2, .1f, 2); // Actual top7.8m, source bottom8m: displacement .2m.
            var field = typeof(BlockTerrain).GetField("_staticColliders", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((HashSet<Collider>)field.GetValue(_terrain)).Add(collider);
            Physics.SyncTransforms();
        }
    }
}
#endif
