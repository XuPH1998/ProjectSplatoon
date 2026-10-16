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
    // Focused motion ownership contracts. These are not formal Player timing tests.
    public sealed class BlockGravityCohortMotionCopyTests
    {
        static BlockWorld Column(Vector3Int bottom, int count)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int i = 0; i < count; i++) world.Set(bottom + Vector3Int.up * i, BlockCell.Create(2));
            return world;
        }

        [Test]
        public void ActivatingTheFinalPieceCannotChangeTheRetainedDormantWorld()
        {
            var bottom = new Vector3Int(2, 20, 2);
            var original = Column(bottom, 32);
            var cohort = original.BeginCohort(bottom, 32);
            var first = original.DetachCohortPiece(cohort.Id, 16, 1, out _);
            original.Seal(1, original.RevisionKeys.ToArray());
            byte[] frozen = original.EncodeSnapshot();

            var next = original.Clone();
            var last = next.DetachCohortPiece(cohort.Id, 16, 5, out _);
            var proved = next.EditCohort(cohort.Id, out _);
            proved.LandingKnown = true; proved.LandingOffset = bottom.y * BlockWorld.CellSize;
            next.PreparePendingCohortsForPublication(out _);
            next.Seal(2, next.RevisionKeys.ToArray());
            next.ActivateRebasePendingCohorts(10);

            Assert.That(next.Cohorts[cohort.Id], Is.Not.SameAs(cohort));
            Assert.That(next.Falling[first.Id], Is.Not.SameAs(first));
            Assert.That(next.Falling[first.Id].Cohort, Is.SameAs(next.Cohorts[cohort.Id]));
            Assert.That(last.Cohort, Is.SameAs(next.Cohorts[cohort.Id]));
            Assert.That(next.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.Falling));
            Assert.That(next.Falling[first.Id].StartedAt, Is.EqualTo(10));
            Assert.That(last.StartedAt, Is.EqualTo(10));
            Assert.That(next.Falling[first.Id].Offset(10.2), Is.GreaterThan(0));
            Assert.That(last.Offset(10.2), Is.EqualTo(next.Falling[first.Id].Offset(10.2)).Within(1e-6));

            Assert.That(original.Falling[first.Id], Is.SameAs(first));
            Assert.That(first.Cohort, Is.SameAs(cohort));
            Assert.That(cohort.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(cohort.DetachCursor, Is.EqualTo(36));
            CollectionAssert.AreEqual(new[] { first.Id }, cohort.MemberIds);
            Assert.That(first.Offset(100), Is.Zero.Within(1e-6));
            Assert.That(original.TryGet(bottom + Vector3Int.up * 16, out _), Is.True);
            CollectionAssert.AreEqual(frozen, original.EncodeSnapshot(), "activation cannot mutate a published source snapshot");
        }

        [Test]
        public void PublishingFractureRebasesChildrenWithoutChangingRetainedMotionOrCells()
        {
            var bottom = new Vector3Int(2, 20, 2);
            var original = Column(bottom, 32);
            var cohort = original.BeginCohort(bottom, 32);
            var first = original.DetachCohortPiece(cohort.Id, 16, 1, out _);
            var second = original.DetachCohortPiece(cohort.Id, 16, 1, out _);
            cohort.LandingKnown = true; cohort.LandingOffset = bottom.y * BlockWorld.CellSize;
            original.PreparePendingCohortsForPublication(out _);
            original.Seal(1, original.RevisionKeys.ToArray());
            original.ActivateRebasePendingCohorts(10);
            byte[] frozen = original.EncodeSnapshot();
            const double publication = 10.2;
            float expectedOffset = cohort.Offset(publication), expectedSpeed = cohort.Speed(publication);

            var next = original.Clone();
            Assert.That(next.RemoveFallingCell(first.Id, 7, out var removed, out _), Is.True);
            Assert.That(removed.Owner, Is.EqualTo(2));
            var spans = new[]
            {
                new FallingCohortSpan(bottom, 27, 27,
                    new[] { new FallingCohortPiece(first.Id, 0, 7) }),
                new FallingCohortSpan(bottom + Vector3Int.up * 8, 52, 52,
                    new[] { new FallingCohortPiece(first.Id, 8, 8), new FallingCohortPiece(second.Id, 0, 16) })
            };
            var children = next.ReplaceCohort(cohort.Id, spans, 10.1, out _);
            foreach (var child in children)
            { child.LandingKnown = true; child.LandingOffset = child.Bottom.y * BlockWorld.CellSize; }
            next.PreparePendingCohortsForPublication(out _);
            next.Seal(2, Array.Empty<Vector3Int>());
            next.ActivateRebasePendingCohorts(publication);

            Assert.That(children.Count, Is.EqualTo(2));
            foreach (var child in children)
            {
                Assert.That(child, Is.Not.SameAs(cohort));
                Assert.That(child.StartedAt, Is.EqualTo(publication));
                Assert.That(child.InitialOffset, Is.EqualTo(expectedOffset).Within(1e-6));
                Assert.That(child.InitialSpeed, Is.EqualTo(expectedSpeed).Within(1e-6));
                foreach (ulong member in child.MemberIds)
                    Assert.That(next.Falling[member].Cohort, Is.SameAs(child));
            }
            Assert.That(next.BlueCount, Is.EqualTo(31));
            Assert.That(original.BlueCount, Is.EqualTo(32));
            Assert.That(original.Falling[first.Id].Cells[7].Occupied, Is.True);
            Assert.That(original.Falling[first.Id].Cohort, Is.SameAs(cohort));
            Assert.That(original.Falling[second.Id].Cohort, Is.SameAs(cohort));
            Assert.That(cohort.NeedsRegroup, Is.False);
            Assert.That(cohort.Offset(publication), Is.EqualTo(expectedOffset).Within(1e-6));
            Assert.That(cohort.Speed(publication), Is.EqualTo(expectedSpeed).Within(1e-6));
            CollectionAssert.AreEqual(frozen, original.EncodeSnapshot(), "damage, fracture, and publication rebase must be transaction-owned");
        }
    }

    public sealed class BlockGravityCohortIntegrationTests
    {
        const double Epoch = 10;
        static readonly Vector3Int Bottom = new(2, 20, 2);
        static readonly Vector3Int TailTop = new(2, 51, 2);
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Gravity cohort contact and publication contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
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
        public IEnumerator DormantDetachRemovesPublishedSourceCollisionAndKeepsTheStableTailSupportingPlayers()
        {
            yield return InstallColumn();
            var source = _terrain.World;
            byte[] frozen = source.EncodeSnapshot();
            AssertStableSide(Bottom);
            AssertStableSide(TailTop);

            var next = source.Clone();
            var cohort = next.BeginCohort(Bottom, 32);
            var run = next.DetachCohortPiece(cohort.Id, 16, Epoch, out _);
            next.Seal(source.Revision + 1, SourceKeys(Bottom, 16));
            int publications = 0;
            Action<BlockTerrainCommit> observe = commit =>
            {
                publications++;
                Assert.That(commit.ChangedChunks.Length, Is.InRange(1, 8));
                Assert.That(_terrain.World.TryGet(Bottom, out _), Is.False);
                Assert.That(SideRay(Bottom, out _), Is.False, "the actual published stable collider no longer contains detached source cells");
                AssertStableSide(TailTop);
                AssertDormantContact(Bottom, run.Id);
                AssertGround(TailTop, true);
                AssertGround(Bottom, false);
                Assert.That(source.TryGet(Bottom, out _), Is.True, "the retained pre-publication World remains unchanged");
                CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            };
            _terrain.PublishedCommit += observe;
            try
            {
                Assert.That(_terrain.ApplyRemoteDelta(next.EncodeDelta(source.Revision)), Is.True);
                _terrain.Tick(false, Epoch, .016f);
                // Actual Update/worker progress may prepare collision, but publication remains Tick-owned.
                for (int frame = 0; frame < 8; frame++)
                {
                    yield return null;
                    Assert.That(_terrain.World, Is.SameAs(source));
                    Assert.That(publications, Is.Zero);
                    AssertStableSide(Bottom);
                }
                yield return DrainRemote(Epoch);
                Assert.That(publications, Is.EqualTo(1));
                Assert.That(_terrain.World.Cohorts[cohort.Id].Phase, Is.EqualTo(FallingCohortPhase.Detaching));
                Assert.That(_terrain.World.Falling[run.Id].Offset(Epoch + 100), Is.Zero.Within(1e-6));
                AssertDormantContact(Bottom, run.Id);
                AssertStableSide(TailTop);
            }
            finally { _terrain.PublishedCommit -= observe; }
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator RealAuthorityActivationAndMovingContactCannotMutateTheRetainedDormantWorld()
        {
            yield return InstallColumn();
            var next = _terrain.World.Clone();
            var cohort = next.BeginCohort(Bottom, 32);
            var first = next.DetachCohortPiece(cohort.Id, 16, Epoch, out _);
            uint previous = _terrain.Revision;
            next.Seal(previous + 1, SourceKeys(Bottom, 16));
            Assert.That(_terrain.ApplyRemoteDelta(next.EncodeDelta(previous)), Is.True);
            yield return DrainRemote(Epoch);
            var dormantWorld = _terrain.World;
            var dormant = dormantWorld.Cohorts[cohort.Id];
            byte[] frozen = dormantWorld.EncodeSnapshot();
            AssertDormantContact(Bottom, first.Id);
            AssertGround(TailTop, true);

            bool activated = false;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                if (!_terrain.World.Cohorts.TryGetValue(cohort.Id, out var live) || live.Phase != FallingCohortPhase.Falling) return;
                activated = true;
                Assert.That(live.MemberIds.Sum(id => _terrain.World.Falling[id].Cells.Count(cell => cell.Occupied)), Is.EqualTo(32));
                Assert.That(live.DetachCursor, Is.EqualTo(52));
                Assert.That(live.StartedAt, Is.EqualTo(Epoch));
                foreach (ulong id in live.MemberIds)
                {
                    Assert.That(_terrain.World.Falling[id].StartedAt, Is.EqualTo(Epoch));
                    Assert.That(_terrain.World.Falling[id].Offset(Epoch), Is.Zero.Within(1e-6));
                }
                Assert.That(SideRay(Bottom, out _), Is.False);
                Assert.That(SideRay(TailTop, out _), Is.False, "the final stable source tail must be absent at activation publication");
                AssertGround(TailTop, false);
                Assert.That(dormant.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
                Assert.That(dormant.MemberIds.Count, Is.EqualTo(1));
                Assert.That(dormantWorld.TryGet(TailTop, out _), Is.True);
                CollectionAssert.AreEqual(frozen, dormantWorld.EncodeSnapshot());
            };
            _terrain.PublishedCommit += observe;
            try
            {
                for (int tick = 0; tick < 1000 && !activated; tick++)
                {
                    _terrain.Tick(true, Epoch, .016f);
                    yield return null;
                }
                Assert.That(activated, Is.True, "the remaining source tail must eventually publish and activate the complete cohort");
                Assert.That(_terrain.FailedTransactions, Is.Zero);
                _terrain.Tick(false, Epoch + .2, .2f);
                var movingRun = _terrain.World.Falling[first.Id];
                float offset = movingRun.Offset(_terrain.SimulationTime);
                Assert.That(offset, Is.GreaterThan(0));
                Assert.That(movingRun.Cohort, Is.Not.SameAs(dormant));
                Vector3 movedCenter = BlockWorld.Center(Bottom) - Vector3.up * offset;
                Assert.That(_terrain.RaycastFalling(movedCenter + Vector3.right * .2f, Vector3.left, .3f, 0, out var hit), Is.True);
                Assert.That(hit.Cell, Is.EqualTo(Bottom));
                Assert.That(hit.RunId, Is.EqualTo(first.Id));
                Assert.That(hit.Point.y, Is.EqualTo(movedCenter.y).Within(.0001));
                Assert.That(_terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True);
                Assert.That(contact.Cell, Is.EqualTo(Bottom));
                Assert.That(contact.Stable, Is.False, "a moving cohort is attackable but never stable player support");
                Assert.That(SideRayAt(movedCenter, out _), Is.False);
                Assert.That(_terrain.TryGetGround(movedCenter + Vector3.up * (BlockWorld.CellSize * .5f), out _), Is.False);
                Assert.That(dormant.Offset(_terrain.SimulationTime), Is.Zero.Within(1e-6));
                CollectionAssert.AreEqual(frozen, dormantWorld.EncodeSnapshot(), "real publication activation and movement must not alter the old snapshot");
            }
            finally { _terrain.PublishedCommit -= observe; }
        }

        IEnumerator InstallColumn()
        {
            var incoming = new BlockWorld(new Vector2(32, 64));
            for (int i = 0; i < 32; i++) incoming.Set(Bottom + Vector3Int.up * i, BlockCell.Create(2));
            incoming.Seal(_terrain.Revision + 1, incoming.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(incoming.EncodeSnapshot()), Is.True);
            yield return DrainRemote(Epoch);
            Assert.That(_terrain.BlueCount, Is.EqualTo(32));
        }

        IEnumerator DrainRemote(double now)
        {
            for (int tick = 0; tick < 1000 && _terrain.PendingTransactions != 0; tick++)
            {
                _terrain.Tick(false, now, .016f);
                yield return null;
            }
            Assert.That(_terrain.PendingTransactions, Is.Zero, "bounded Editor fixture drain; not a formal Player timing gate");
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        static Vector3Int[] SourceKeys(Vector3Int bottom, int count)
        {
            var include = typeof(BlockTerrain).GetMethod("IncludeCell", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(include, Is.Not.Null);
            var changed = new HashSet<Vector3Int>();
            for (int i = 0; i < count; i++) include.Invoke(null, new object[] { changed, bottom + Vector3Int.up * i });
            Assert.That(changed.Count, Is.LessThanOrEqualTo(8));
            return changed.ToArray();
        }

        static bool SideRay(Vector3Int cell, out RaycastHit hit) => SideRayAt(BlockWorld.Center(cell), out hit);
        static bool SideRayAt(Vector3 center, out RaycastHit hit)
            => Physics.Raycast(center + Vector3.right * .2f, Vector3.left, out hit, .3f,
                1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);

        void AssertStableSide(Vector3Int cell)
        {
            Assert.That(SideRay(cell, out var hit), Is.True);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
            Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell));
            Assert.That(contact.Owner, Is.EqualTo(2));
            Assert.That(contact.Stable, Is.True);
            Assert.That(contact.Revision, Is.EqualTo(_terrain.ChunkRevision(BlockWorld.ChunkAt(cell))));
        }

        void AssertDormantContact(Vector3Int cell, ulong runId)
        {
            Assert.That(_terrain.RaycastFalling(BlockWorld.Center(cell) + Vector3.right * .2f, Vector3.left, .3f, 0, out var hit), Is.True);
            Assert.That(hit.Cell, Is.EqualTo(cell));
            Assert.That(hit.RunId, Is.EqualTo(runId));
            Assert.That(_terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell));
            Assert.That(contact.Owner, Is.EqualTo(2));
            Assert.That(contact.Stable, Is.False, "published detached dormant geometry is targetable but cannot qualify as stable support");
            Assert.That(contact.Revision, Is.EqualTo(_terrain.Revision));
        }

        void AssertGround(Vector3Int cell, bool expected)
        {
            Vector3 feet = BlockWorld.Center(cell) + Vector3.up * (BlockWorld.CellSize * .5f);
            bool found = _terrain.TryGetGround(feet, out var contact);
            Assert.That(found, Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(contact.Cell, Is.EqualTo(cell));
                Assert.That(contact.Stable, Is.True);
            }
        }
    }
}
#endif
