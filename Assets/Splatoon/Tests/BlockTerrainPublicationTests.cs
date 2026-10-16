#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;

namespace Splatoon.Tests
{
    public sealed class BlockTerrainPublicationTests
    {
        [UnitySetUp] public IEnumerator Setup()
        { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); yield return new EnterPlayMode(); }
        [UnityTearDown] public IEnumerator Teardown()
        { yield return new ExitPlayMode(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        [UnityTest]
        public IEnumerator RemovingBoundaryCellPublishesTheAdjacentChunkNewlyExposedCollisionFace()
        {
            var obj = new GameObject("Block boundary publication test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var left = new Vector3Int(15, 1, 1); var right = new Vector3Int(16, 1, 1);
                var incoming = new BlockWorld(new Vector2(32, 64));
                incoming.Set(left, BlockCell.Create(2, true)); incoming.Set(right, BlockCell.Create(2, true));
                yield return Install(terrain, incoming); bool done = false;
                terrain.QueueDestroy(BlockWorld.Center(right), .001f, 0, 1, true, result => done = true, 1, 0);
                for (int tick = 0; tick < 1000 && !done; tick++) { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(done, Is.True); Assert.That(terrain.World.TryGet(right, out _), Is.False);
                Assert.That(Physics.Raycast(new Vector3(1.8f, .15f, .15f), Vector3.left, out var hit, .4f, 1 << BlockTerrain.TerrainLayer), Is.True);
                Assert.That(hit.point.x, Is.EqualTo(1.6f).Within(.001));
                Assert.That(terrain.TryContact(hit, out var contact), Is.True); Assert.That(contact.Cell, Is.EqualTo(left));
                Assert.That(contact.Revision, Is.EqualTo(terrain.Revision), "the neighbor's exposed collision face belongs to the same published transaction");
            }
            finally { Object.DestroyImmediate(obj); }
        }
        static IEnumerator Install(BlockTerrain terrain, BlockWorld incoming)
        {
            incoming.Seal(terrain.Revision + 1, incoming.RevisionKeys.ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(incoming.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++)
            { terrain.Tick(false, tick * .016, .016f); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero);
        }
        static IEnumerator DrainRemote(BlockTerrain terrain, double now = 20)
        {
            for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++)
            { terrain.Tick(false, now + tick * .016, .016f); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero);
        }
        static MeshCollider TopCollider(Vector3Int cell)
        {
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                out var hit, .6f, 1 << BlockTerrain.TerrainLayer), Is.True);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>()); return (MeshCollider)hit.collider;
        }
        [UnityTest]
        public IEnumerator OrdinaryDestructionRemovesTheNearestActualEnemyCell()
        {
            var obj = new GameObject("Block nearest destruction test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var far = Vector3Int.zero; var near = new Vector3Int(2, 1, 0);
                var incoming = new BlockWorld(new Vector2(32, 64));
                incoming.Set(far, BlockCell.Create(2, true)); incoming.Set(near, BlockCell.Create(2, true));
                yield return Install(terrain, incoming); bool done = false; int removed = 0;
                terrain.QueueDestroy(BlockWorld.Center(near), 1, 0, 1, true, result => { done = true; removed = result.RemovedEnemy; }, 1, 0);
                for (int tick = 0; tick < 1000 && !done; tick++) { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(done, Is.True); Assert.That(removed, Is.EqualTo(1));
                Assert.That(terrain.World.TryGet(near, out _), Is.False); Assert.That(terrain.World.TryGet(far, out _), Is.True);
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator DestructionCandidatesDoNotIncludeBlocksAddedAfterAttackCreation()
        {
            var obj = new GameObject("Block destruction snapshot test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var old = new Vector3Int(4, 0, 0); var later = Vector3Int.zero;
                var incoming = new BlockWorld(new Vector2(32, 64)); incoming.Set(old, BlockCell.Create(2, true));
                yield return Install(terrain, incoming); bool done = false;
                terrain.QueueDestroy(BlockWorld.Center(later), 1, 0, 1, true, result => done = true, 1, 0);
                var newer = terrain.World.Clone(); newer.Set(later, BlockCell.Create(2, true));
                newer.Seal(terrain.Revision + 1, newer.RevisionKeys.ToArray());
                Assert.That(terrain.ApplyRemoteSnapshot(newer.EncodeSnapshot()), Is.True);
                for (int tick = 0; tick < 1000 && !done; tick++) { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(done, Is.True); Assert.That(terrain.World.TryGet(old, out _), Is.False);
                Assert.That(terrain.World.TryGet(later, out _), Is.True, "the nearer future block was not present in the attack's candidate snapshot");
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator SameRevisionSnapshotRemainsPendingUntilItsDataAndColliderArePublished()
        {
            var obj = new GameObject("Block same revision publication test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var incoming = new BlockWorld(new Vector2(32, 64)); incoming.Set(Vector3Int.zero, BlockCell.Create(1, true));
                incoming.Seal(terrain.Revision, incoming.Chunks.Keys);
                Assert.That(terrain.ApplyRemoteSnapshot(incoming.EncodeSnapshot()), Is.True);
                Assert.That(terrain.Revision, Is.EqualTo(incoming.Revision), "numeric equality does not mean the full snapshot is published");
                Assert.That(terrain.PendingTransactions, Is.GreaterThan(0)); Assert.That(terrain.PinkCount, Is.Zero);
                terrain.Tick(false, 1, .016f);
                var update = typeof(BlockTerrain).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int frame = 0; frame < 8; frame++) { update.Invoke(terrain, null); yield return null; }
                Assert.That(terrain.PinkCount, Is.Zero, "Update may prepare meshes but may not publish data or collision");
                Assert.That(terrain.PendingTransactions, Is.GreaterThan(0));
                for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++)
                { terrain.Tick(false, 1 + tick * .016, .016f); yield return null; }
                Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.PinkCount, Is.EqualTo(1));
                Assert.That(terrain.Revision, Is.EqualTo(incoming.Revision));
                Assert.That(Physics.Raycast(new Vector3(.05f, .2f, .05f), Vector3.down, out var hit, .2f, 1 << BlockTerrain.TerrainLayer), Is.True);
                Assert.That(terrain.TryContact(hit, out var contact), Is.True);
                Assert.That(contact.Stable, Is.True); Assert.That(contact.Owner, Is.EqualTo(1)); Assert.That(contact.Revision, Is.EqualTo(incoming.Revision));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator BuildBacklogAndReadyFallingBothPublishWithoutDroppingCommands()
        {
            var obj = new GameObject("Block scheduling backlog test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var incoming = new BlockWorld(new Vector2(32, 64));
                for (int i = 0; i < 82; i++)
                {
                    int x = -80 + i * 2;
                    incoming.Set(new Vector3Int(x, 0, 1), BlockCell.Create(1, true));
                    var cell = new Vector3Int(x, 10, 1); incoming.Set(cell, BlockCell.Create(2));
                    var run = incoming.Detach(cell, 1, -10); run.LandingKnown = true; run.LandingOffset = .9f;
                }
                var build = new Vector3Int(120, 1, 30);
                incoming.Set(build + Vector3Int.down, BlockCell.Create(1, true));
                yield return Install(terrain, incoming);
                int completed = 0, added = 0, publications = 0;
                terrain.PublishedCommit += commit => { publications++; Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8)); };
                for (int i = 0; i < 4096; i++)
                    terrain.QueueBuild(BlockWorld.Center(build) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                        result => { completed++; added += result.Added; });
                for (int tick = 0; tick < 1000 && (completed != 4096 || terrain.FallingCount != 0); tick++)
                { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(completed, Is.EqualTo(4096)); Assert.That(added, Is.EqualTo(1));
                Assert.That(terrain.FallingCount, Is.Zero); Assert.That(terrain.BlueCount, Is.EqualTo(82));
                Assert.That(terrain.PinkCount, Is.EqualTo(84)); Assert.That(publications, Is.GreaterThan(0));
                Assert.That(terrain.CommandProgressStages, Is.GreaterThan(0)); Assert.That(terrain.GravityProgressStages, Is.GreaterThan(0));
                Assert.That(terrain.ReservationCommandChecks, Is.Zero, "a build backlog does not participate in attack reservations");
                for (int i = 0; i < 82; i++)
                    Assert.That(terrain.World.TryGet(new Vector3Int(-80 + i * 2, 1, 1), out var cell) && cell.Owner == 2, Is.True);
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator SustainedNewChunksCannotStarveFallingSettlement()
        {
            var obj = new GameObject("Block chunk slot fairness test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var incoming = new BlockWorld(new Vector2(32, 64));
                var falling = new Vector3Int(130, 10, 100); incoming.Set(falling + Vector3Int.down * 10, BlockCell.Create(2, true));
                incoming.Set(falling, BlockCell.Create(2));
                var run = incoming.Detach(falling, 1, -10); run.LandingKnown = true; run.LandingOffset = .9f;
                var builds = new Vector3Int[144];
                for (int i = 0; i < builds.Length; i++)
                {
                    builds[i] = new Vector3Int(-130 + i % 12 * 16, 1, -200 + i / 12 * 16);
                    incoming.Set(builds[i] + Vector3Int.down, BlockCell.Create(1, true));
                }
                yield return Install(terrain, incoming);
                int completed = 0, added = 0; bool settledDuringBacklog = false;
                terrain.PublishedCommit += commit => Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                foreach (var cell in builds)
                    terrain.QueueBuild(BlockWorld.Center(cell) - Vector3.up * .051f, Vector3.up, 1, 1, 0, false,
                        result => { completed++; added += result.Added; });
                for (int tick = 0; tick < 1000 && (completed != builds.Length || terrain.FallingCount != 0); tick++)
                {
                    terrain.Tick(true, 20 + tick * .016, .016f);
                    if (terrain.FallingCount == 0 && completed < builds.Length) settledDuringBacklog = true;
                    yield return null;
                }
                Assert.That(settledDuringBacklog, Is.True, "ready falling must publish while new chunks are still being built");
                Assert.That(completed, Is.EqualTo(builds.Length)); Assert.That(added, Is.EqualTo(builds.Length));
                Assert.That(terrain.World.TryGet(new Vector3Int(130, 1, 100), out var landed) && landed.Owner == 2, Is.True);
                Assert.That(terrain.PinkCount, Is.EqualTo(builds.Length * 2)); Assert.That(terrain.BlueCount, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator BudgetedExplosionCompletesItsFullStableAndFallingCandidateSnapshot()
        {
            var obj = new GameObject("Block full explosion scheduling test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var incoming = new BlockWorld(new Vector2(32, 64));
                for (int i = 0; i < 100; i++) incoming.Set(new Vector3Int(i % 10, 1, i / 10), BlockCell.Create((byte)(i % 2 + 1), true));
                var falling = new Vector3Int(5, 10, 5);
                for (int i = 0; i < 3; i++) incoming.Set(falling + Vector3Int.up * i, BlockCell.Create((byte)(i % 2 + 1)));
                var run = incoming.Detach(falling, 3, -10); run.LandingKnown = true; run.LandingOffset = .8f;
                yield return Install(terrain, incoming); bool done = false; BlockMutationResult result = default;
                terrain.PublishedCommit += commit => Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                terrain.QueueDestroy(new Vector3(.5f, .2f, .5f), 2, 2, 1, false, value => { done = true; result = value; });
                for (int tick = 0; tick < 1000 && !done; tick++) { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(done, Is.True); Assert.That(result.RemovedEnemy, Is.EqualTo(51)); Assert.That(result.RemovedFriendly, Is.EqualTo(52));
                Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero); Assert.That(terrain.FallingCount, Is.Zero);
                Assert.That(terrain.ReservationCommandChecks, Is.GreaterThan(0), "queued destruction must reserve its falling candidates during preparation");
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator ClampedColumnRunsSettleWithoutPassingTheirLowerRun()
        {
            var obj = new GameObject("Block clamped column ordering regression");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var incoming = new BlockWorld(new Vector2(32, 64));
                incoming.Set(new Vector3Int(5, 29, 5), BlockCell.Create(1, true));
                var lower = new Vector3Int(5, 31, 5); incoming.Set(lower, BlockCell.Create(2));
                var upper = new Vector3Int(5, 32, 5);
                for (int i = 0; i < 16; i++) incoming.Set(upper + Vector3Int.up * i, BlockCell.Create(2));
                var lowRun = incoming.Detach(lower, 1, -10); lowRun.LandingKnown = true; lowRun.LandingOffset = .1f;
                var highRun = incoming.Detach(upper, 16, -10); highRun.LandingKnown = true; highRun.LandingOffset = .2f;
                Assert.That(BlockWorld.Center(upper).y - highRun.Offset(20),
                    Is.LessThan(BlockWorld.Center(lower).y - lowRun.Offset(20)),
                    "The actual binary32 visual-height comparison picks the upper run first");
                yield return Install(terrain, incoming);
                int publications = 0; var chunksPerFrame = new Dictionary<int, int>();
                terrain.PublishedCommit += commit =>
                {
                    publications++; Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                    chunksPerFrame.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length;
                    chunksPerFrame[Time.frameCount] = count; Assert.That(count, Is.LessThanOrEqualTo(8));
                    Assert.That(terrain.PinkCount, Is.EqualTo(1)); Assert.That(terrain.BlueCount, Is.EqualTo(17));
                };
                for (int tick = 0; tick < 300 && (terrain.FallingCount != 0 || terrain.PendingTransactions != 0 || terrain.UnstableColumns != 0); tick++)
                { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(publications, Is.GreaterThan(0)); Assert.That(terrain.FallingCount, Is.Zero);
                Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.UnstableColumns, Is.Zero);
                for (int y = 30; y <= 46; y++) Assert.That(terrain.World.TryGet(new Vector3Int(5, y, 5), out var cell)
                    && cell.Owner == 2, Is.True, "Every real settled cell must remain in source order");
                Assert.That(terrain.World.TryGet(new Vector3Int(5, 47, 5), out _), Is.False);
                Assert.That(terrain.World.Chunks.Values.Sum(chunk => chunk.Count), Is.EqualTo(18));
                Assert.That(TopCollider(new Vector3Int(5, 46, 5)).enabled, Is.True,
                    "The real published MeshCollider must expose the final stack top");
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator TallTowerThatChangesChunkAlignmentCanFinishFalling()
        {
            var obj = new GameObject("Block tall run landing budget test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var incoming = new BlockWorld(new Vector2(32, 64));
                incoming.Set(new Vector3Int(5, -1, 5), BlockCell.Create(1, true));
                var support = new Vector3Int(5, 1, 5); incoming.Set(support, BlockCell.Create(2, true));
                for (int y = 2; y <= 126; y++) incoming.Set(new Vector3Int(5, y, 5), BlockCell.Create(1));
                yield return Install(terrain, incoming); bool removed = false;
                terrain.PublishedCommit += commit => Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                terrain.QueueDestroy(BlockWorld.Center(support), .001f, 0, 1, true, result => removed = result.RemovedEnemy == 1, 1, 0);
                for (int tick = 0; tick < 2000 && (!removed || terrain.PendingTransactions != 0 || terrain.UnstableColumns != 0 || terrain.FallingCount != 0); tick++)
                { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                Assert.That(removed, Is.True); Assert.That(terrain.PendingTransactions, Is.Zero);
                Assert.That(terrain.UnstableColumns, Is.Zero); Assert.That(terrain.FallingCount, Is.Zero);
                Assert.That(terrain.PinkCount, Is.EqualTo(126)); Assert.That(terrain.BlueCount, Is.Zero);
                for (int y = 0; y < 125; y++) Assert.That(terrain.World.TryGet(new Vector3Int(5, y, 5), out var cell) && cell.Owner == 1, Is.True);
                Assert.That(terrain.World.TryGet(new Vector3Int(5, 125, 5), out _), Is.False);
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator BoundaryDeletionDeltaPublishesItsTombstoneAndNeighborWithoutRebuildingDistantChunks()
        {
            var obj = new GameObject("Block precise delta publication test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var left = new Vector3Int(15, 1, 1); var right = new Vector3Int(16, 1, 1); var far = new Vector3Int(48, 1, 1);
                var source = new BlockWorld(new Vector2(32, 64));
                foreach (var cell in new[] { left, right, far }) source.Set(cell, BlockCell.Create(2, true));
                source.Seal(1, source.Chunks.Keys.ToArray());
                Assert.That(terrain.ApplyRemoteSnapshot(source.EncodeSnapshot()), Is.True); yield return DrainRemote(terrain);
                var farCollider = TopCollider(far); var farMesh = farCollider.sharedMesh; int publishedChunks = terrain.PublishedChunks;
                var next = source.Clone(); next.Set(right, default);
                var changed = new[] { BlockWorld.ChunkAt(left), BlockWorld.ChunkAt(right) }; next.Seal(2, changed);
                BlockTerrainCommit commit = default; int publications = 0;
                terrain.PublishedCommit += value => { commit = value; publications++; Assert.That(terrain.BlueCount, Is.EqualTo(2)); };
                Assert.That(terrain.ApplyRemoteDelta(next.EncodeDelta(1)), Is.True);
                Assert.That(terrain.BlueCount, Is.EqualTo(3)); Assert.That(terrain.Revision, Is.EqualTo(1));
                yield return DrainRemote(terrain);
                Assert.That(publications, Is.EqualTo(1)); CollectionAssert.AreEquivalent(changed, commit.ChangedChunks);
                Assert.That(terrain.PublishedChunks - publishedChunks, Is.EqualTo(2)); Assert.That(terrain.Revision, Is.EqualTo(2));
                Assert.That(terrain.ChunkRevision(BlockWorld.ChunkAt(right)), Is.EqualTo(2), "the deleted chunk's tombstone remains a support revision");
                Assert.That(TopCollider(far), Is.SameAs(farCollider)); Assert.That(TopCollider(far).sharedMesh, Is.SameAs(farMesh));
                Assert.That(Physics.Raycast(new Vector3(1.8f, .15f, .15f), Vector3.left, out var hit, .4f, 1 << BlockTerrain.TerrainLayer), Is.True);
                Assert.That(hit.point.x, Is.EqualTo(1.6f).Within(.001));
                Assert.That(terrain.TryContact(hit, out var contact), Is.True); Assert.That(contact.Cell, Is.EqualTo(left));
                Assert.That(contact.Revision, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator FallingOnlyDeltaPublishesAtomicallyWithoutRebuildingStableCollision()
        {
            var obj = new GameObject("Block falling-only delta publication test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var stable = new Vector3Int(5, 1, 5); var falling = new Vector3Int(6, 20, 5);
                var source = new BlockWorld(new Vector2(32, 64)); source.Set(stable, BlockCell.Create(1, true));
                source.Set(falling, BlockCell.Create(2)); var run = source.Detach(falling, 1, 0);
                source.Seal(1, source.RevisionKeys.ToArray());
                Assert.That(terrain.ApplyRemoteSnapshot(source.EncodeSnapshot()), Is.True); yield return DrainRemote(terrain);
                var collider = TopCollider(stable); var mesh = collider.sharedMesh; int publishedChunks = terrain.PublishedChunks;
                var next = source.Clone(); Assert.That(next.RemoveFallingCell(run.Id, 0, out _), Is.True);
                next.Seal(2, new Vector3Int[0]); int publications = 0;
                terrain.PublishedCommit += value =>
                {
                    publications++; Assert.That(value.ChangedChunks, Is.Empty); Assert.That(terrain.Revision, Is.EqualTo(2));
                    Assert.That(terrain.BlueCount, Is.Zero); Assert.That(terrain.FallingCount, Is.Zero);
                };
                Assert.That(terrain.ApplyRemoteDelta(next.EncodeDelta(1)), Is.True); terrain.Tick(false, 30, .016f);
                var update = typeof(BlockTerrain).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int frame = 0; frame < 8; frame++) { update.Invoke(terrain, null); yield return null; }
                Assert.That(terrain.Revision, Is.EqualTo(1)); Assert.That(terrain.BlueCount, Is.EqualTo(1));
                Assert.That(terrain.FallingCount, Is.EqualTo(1)); Assert.That(publications, Is.Zero);
                yield return DrainRemote(terrain, 31);
                Assert.That(publications, Is.EqualTo(1)); Assert.That(terrain.PublishedChunks, Is.EqualTo(publishedChunks));
                Assert.That(terrain.PinkCount, Is.EqualTo(1)); Assert.That(TopCollider(stable), Is.SameAs(collider));
                Assert.That(TopCollider(stable).sharedMesh, Is.SameAs(mesh));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator RemoteInstallsCannotRegressPendingVersionsOrOverwriteQueuedPublications()
        {
            var obj = new GameObject("Block remote version ordering test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var cell = new Vector3Int(5, 1, 5); var added = new Vector3Int(6, 1, 5);
                var source = new BlockWorld(new Vector2(32, 64)); source.Set(cell, BlockCell.Create(1, true)); source.Seal(8, source.Chunks.Keys);
                Assert.That(terrain.ApplyRemoteSnapshot(source.EncodeSnapshot()), Is.True); yield return DrainRemote(terrain);
                var stale = source.Clone(); stale.Seal(9, new Vector3Int[0]);
                var ten = source.Clone(); ten.Set(cell, BlockCell.Create(2, true)); ten.Seal(10, ten.Chunks.Keys);
                var eleven = ten.Clone(); eleven.Set(added, BlockCell.Create(1, true)); eleven.Seal(11, eleven.Chunks.Keys);
                var twelve = eleven.Clone(); twelve.Seal(12, new Vector3Int[0]); var publications = new List<uint>();
                terrain.PublishedCommit += commit => publications.Add(commit.Revision);
                Assert.That(terrain.ApplyRemoteSnapshot(ten.EncodeSnapshot()), Is.True);
                Assert.That(terrain.ApplyRemoteSnapshot(stale.EncodeSnapshot()), Is.False, "the queued version is newer than the published world");
                Assert.That(terrain.ApplyRemoteDelta(stale.EncodeDelta(8)), Is.False);
                terrain.Tick(false, 30, .016f); Assert.That(terrain.Revision, Is.EqualTo(8));
                Assert.That(terrain.ApplyRemoteSnapshot(stale.EncodeSnapshot()), Is.False, "the pending version is newer than the published world");
                Assert.That(terrain.ApplyRemoteDelta(stale.EncodeDelta(8)), Is.False, "a delta cannot stage against the old published baseline while a newer install is pending");
                Assert.That(terrain.ApplyRemoteSnapshot(eleven.EncodeSnapshot()), Is.True);
                Assert.That(terrain.ApplyRemoteSnapshot(twelve.EncodeSnapshot()), Is.False, "a queued full install must receive its own exact publication");
                yield return DrainRemote(terrain, 31);
                CollectionAssert.AreEqual(new uint[] { 10, 11 }, publications);
                Assert.That(terrain.Revision, Is.EqualTo(11)); Assert.That(terrain.BlueCount, Is.EqualTo(1)); Assert.That(terrain.PinkCount, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator IdenticalSameRevisionFullSnapshotRepairsAllLiveChunkColliders()
        {
            var obj = new GameObject("Block identical full repair test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var near = new Vector3Int(5, 1, 5); var far = new Vector3Int(48, 1, 5);
                var source = new BlockWorld(new Vector2(32, 64)); source.Set(near, BlockCell.Create(1, true)); source.Set(far, BlockCell.Create(2, true));
                source.Seal(1, source.Chunks.Keys.ToArray()); var bytes = source.EncodeSnapshot();
                Assert.That(terrain.ApplyRemoteSnapshot(bytes), Is.True); yield return DrainRemote(terrain);
                var nearCollider = TopCollider(near); var farCollider = TopCollider(far); int publishedChunks = terrain.PublishedChunks;
                nearCollider.sharedMesh = null; Physics.SyncTransforms();
                Assert.That(Physics.Raycast(BlockWorld.Center(near) + Vector3.up * .5f, Vector3.down, .6f, 1 << BlockTerrain.TerrainLayer), Is.False);
                CollectionAssert.AreEqual(bytes, terrain.ExportSnapshot(), "the data and revision match even though live collision needs repair");
                int publications = 0; terrain.PublishedCommit += commit => { publications++; Assert.That(commit.ChangedChunks.Length, Is.EqualTo(2)); };
                Assert.That(terrain.ApplyRemoteSnapshot(bytes), Is.True); Assert.That(terrain.PendingTransactions, Is.GreaterThan(0));
                terrain.Tick(false, 30, .016f);
                var update = typeof(BlockTerrain).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int frame = 0; frame < 8; frame++) { update.Invoke(terrain, null); yield return null; }
                Assert.That(publications, Is.Zero); Assert.That(TopCollider(far), Is.SameAs(farCollider));
                yield return DrainRemote(terrain, 31);
                Assert.That(publications, Is.EqualTo(1)); Assert.That(terrain.PublishedChunks - publishedChunks, Is.EqualTo(2));
                Assert.That(terrain.Revision, Is.EqualTo(1)); Assert.That(terrain.PinkCount, Is.EqualTo(1)); Assert.That(terrain.BlueCount, Is.EqualTo(1));
                Assert.That(TopCollider(near), Is.Not.SameAs(nearCollider)); Assert.That(TopCollider(far), Is.Not.SameAs(farCollider));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        static void AssertCollisionWithdrawnBeforeTheNextFrame(BlockTerrain terrain, Vector3Int cell, MeshCollider prior)
        {
            Assert.That(prior.enabled, Is.False, "deferred destruction must not leave published collision enabled");
            Assert.That(prior.gameObject.activeInHierarchy, Is.False, "the retired view must already be invisible");
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                .6f, 1 << BlockTerrain.TerrainLayer), Is.False, "the old structure must not obstruct same-frame physics queries");
            Assert.That(terrain.TryContact(prior, BlockWorld.Center(cell) + Vector3.up * .05f, Vector3.up, out _), Is.False);
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator ClearWithdrawsBothTeamsCollisionBeforeReturningAndCancelsQueuedAttackOnce()
        {
            var obj = new GameObject("Block same-frame clear collision test");
            try
            {
                Assert.That(Application.isPlaying, Is.True, "EditMode DestroyImmediate would hide this regression");
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.Initialize(null);
                var pink = new Vector3Int(5, 1, 5); var blue = new Vector3Int(80, 1, 5);
                var source = new BlockWorld(new Vector2(32, 64), 4);
                source.Set(pink, BlockCell.Create(1, true)); source.Set(blue, BlockCell.Create(2, true));
                yield return Install(terrain, source);
                var pinkCollider = TopCollider(pink); var blueCollider = TopCollider(blue);
                int callbacks = 0, mutations = 0; var publications = new List<BlockTerrainCommit>();
                BlockMutationResult cancelled = default;
                terrain.PublishedCommit += commit => publications.Add(commit);
                terrain.MutationCompleted += (_, _) => mutations++;
                terrain.QueueDestroy(BlockWorld.Center(blue), .001f, 0, 1, true,
                    result => { callbacks++; cancelled = result; }, 1, 0);
                Assert.That(terrain.PendingTransactions, Is.GreaterThan(0));
                long completed = terrain.CommandsCompleted;
                terrain.Clear(5);
                // These checks intentionally run before any yield, while Destroy is deferred.
                Assert.That(terrain.Round, Is.EqualTo(5)); Assert.That(terrain.Revision, Is.Zero);
                Assert.That(terrain.PinkCount, Is.Zero); Assert.That(terrain.BlueCount, Is.Zero);
                Assert.That(terrain.FallingCount, Is.Zero); Assert.That(terrain.PendingTransactions, Is.Zero);
                AssertCollisionWithdrawnBeforeTheNextFrame(terrain, pink, pinkCollider);
                AssertCollisionWithdrawnBeforeTheNextFrame(terrain, blue, blueCollider);
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(cancelled.Changed, Is.Zero);
                Assert.That(terrain.CommandsCompleted - completed, Is.EqualTo(1));
                Assert.That(mutations, Is.Zero); Assert.That(publications, Is.Empty);
                var empty = BlockWorld.DecodeSnapshot(terrain.ExportSnapshot());
                Assert.That(empty.Round, Is.EqualTo(5)); Assert.That(empty.Revision, Is.Zero);
                Assert.That(empty.PinkCount + empty.BlueCount, Is.Zero); Assert.That(empty.Chunks, Is.Empty);
                for (int frame = 0; frame < 4; frame++) { terrain.Tick(false, 30 + frame * .016, .016f); yield return null; }
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero);
                var next = new BlockWorld(new Vector2(32, 64), 5); var nextCell = new Vector3Int(-20, 1, 5);
                next.Set(nextCell, BlockCell.Create(2, true)); yield return Install(terrain, next);
                Assert.That(publications.Count, Is.EqualTo(1)); Assert.That(publications[0].Round, Is.EqualTo(5));
                Assert.That(terrain.PinkCount, Is.Zero); Assert.That(terrain.BlueCount, Is.EqualTo(1));
                Assert.That(TopCollider(nextCell), Is.Not.Null); Assert.That(callbacks, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator FinishRoundKeepsItsPublishedCollisionAndClearReopensConstructionCommands()
        {
            var obj = new GameObject("Block finish freeze and clear collision test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.Initialize(null);
                var oldCell = new Vector3Int(5, 1, 5);
                var source = new BlockWorld(new Vector2(32, 64), 6); source.Set(oldCell, BlockCell.Create(1, true));
                yield return Install(terrain, source); var prior = TopCollider(oldCell); uint revision = terrain.Revision;
                terrain.FinishRound(20);
                Assert.That(terrain.PinkCount, Is.EqualTo(1)); Assert.That(terrain.Revision, Is.EqualTo(revision));
                Assert.That(prior.enabled, Is.True); Assert.That(prior.gameObject.activeInHierarchy, Is.True);
                Assert.That(TopCollider(oldCell), Is.SameAs(prior));
                Assert.That(terrain.TryContact(prior, BlockWorld.Center(oldCell) + Vector3.up * .05f, Vector3.up, out var contact), Is.True);
                Assert.That(contact.Stable, Is.True); Assert.That(contact.Revision, Is.EqualTo(revision));
                int frozenCallbacks = 0;
                terrain.QueueBuild(BlockWorld.Center(oldCell), Vector3.up, 1, 1, 0, true,
                    result => { frozenCallbacks++; Assert.That(result.Changed, Is.Zero); });
                Assert.That(frozenCallbacks, Is.EqualTo(1)); Assert.That(terrain.PendingTransactions, Is.Zero);
                terrain.Tick(true, 21, .016f);
                Assert.That(terrain.PinkCount, Is.EqualTo(1)); Assert.That(TopCollider(oldCell), Is.SameAs(prior));
                terrain.Clear(7);
                AssertCollisionWithdrawnBeforeTheNextFrame(terrain, oldCell, prior);
                Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero);
                var support = new Vector3Int(-20, 0, 5); var next = new BlockWorld(new Vector2(32, 64), 7);
                next.Set(support, BlockCell.Create(1, true)); yield return Install(terrain, next);
                int builtCallbacks = 0; BlockMutationResult built = default;
                var buildPoint = BlockWorld.Center(support) + Vector3.up * .05f;
                terrain.QueueBuild(buildPoint, Vector3.up, 1, 1, 0, false,
                    result => { builtCallbacks++; built = result; });
                for (int tick = 0; tick < 1000 && builtCallbacks == 0; tick++)
                { terrain.Tick(true, 30 + tick * .016, .016f); yield return null; }
                Assert.That(builtCallbacks, Is.EqualTo(1)); Assert.That(built.Added, Is.EqualTo(1));
                for (int tick = 0; tick < 1000 && (terrain.PendingTransactions != 0 || terrain.UnstableColumns != 0); tick++)
                { terrain.Tick(true, 50 + tick * .016, .016f); yield return null; }
                Assert.That(terrain.PinkCount, Is.EqualTo(2)); Assert.That(terrain.Round, Is.EqualTo(7));
                Assert.That(TopCollider(support + Vector3Int.up), Is.Not.Null);
                Assert.That(frozenCallbacks, Is.EqualTo(1)); Assert.That(terrain.PendingTransactions, Is.Zero);
            }
            finally { Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        public IEnumerator NewRoundFullSnapshotCannotBeOverwrittenByAnOldRoundPendingTransaction()
        {
            var obj = new GameObject("Block new round publication cancellation test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var source = new BlockWorld(new Vector2(32, 64), 4); source.Set(new Vector3Int(5, 1, 5), BlockCell.Create(1, true));
                source.Seal(8, source.Chunks.Keys.ToArray());
                Assert.That(terrain.ApplyRemoteSnapshot(source.EncodeSnapshot()), Is.True); yield return DrainRemote(terrain);
                var oldCell = new Vector3Int(5, 1, 5); var oldCollider = TopCollider(oldCell);
                var oldPending = source.Clone(); oldPending.Seal(10, oldPending.Chunks.Keys.ToArray());
                var nextRound = new BlockWorld(new Vector2(32, 64), 5); var newCell = new Vector3Int(80, 1, 5);
                nextRound.Set(newCell, BlockCell.Create(2, true)); nextRound.Seal(1, nextRound.Chunks.Keys.ToArray());
                var publications = new List<BlockTerrainCommit>(); terrain.PublishedCommit += commit => publications.Add(commit);
                Assert.That(terrain.ApplyRemoteSnapshot(oldPending.EncodeSnapshot()), Is.True); terrain.Tick(false, 30, .016f);
                Assert.That(terrain.PendingTransactions, Is.GreaterThan(0)); Assert.That(terrain.Revision, Is.EqualTo(8));
                Assert.That(terrain.ApplyRemoteSnapshot(nextRound.EncodeSnapshot()), Is.True);
                // A newer round clears the old world synchronously before its new meshes are ready.
                Assert.That(terrain.Round, Is.EqualTo(5)); Assert.That(terrain.Revision, Is.Zero);
                Assert.That(terrain.PinkCount + terrain.BlueCount, Is.Zero);
                AssertCollisionWithdrawnBeforeTheNextFrame(terrain, oldCell, oldCollider);
                Assert.That(terrain.ApplyRemoteSnapshot(oldPending.EncodeSnapshot()), Is.False);
                yield return DrainRemote(terrain, 31);
                var update = typeof(BlockTerrain).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int frame = 0; frame < 8; frame++) { update.Invoke(terrain, null); yield return null; }
                Assert.That(publications.Count, Is.EqualTo(1)); Assert.That(publications[0].Round, Is.EqualTo(5)); Assert.That(publications[0].Revision, Is.EqualTo(1));
                Assert.That(terrain.Round, Is.EqualTo(5)); Assert.That(terrain.Revision, Is.EqualTo(1));
                Assert.That(terrain.PinkCount, Is.Zero); Assert.That(terrain.BlueCount, Is.EqualTo(1)); Assert.That(TopCollider(newCell), Is.Not.Null);
            }
            finally { Object.DestroyImmediate(obj); }
        }
    }
}
#endif
