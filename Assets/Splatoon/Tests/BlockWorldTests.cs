using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Splatoon.Blocks;

namespace Splatoon.Tests
{
    public sealed class BlockWorldTests
    {
        [Test]
        public void NegativeCoordinatesUseFloorDivisionAndStableLocalIndices()
        {
            var cell = BlockWorld.CellAt(new Vector3(-.01f, .05f, -.11f));
            Assert.That(cell, Is.EqualTo(new Vector3Int(-1, 0, -2)));
            Assert.That(BlockWorld.ChunkAt(cell), Is.EqualTo(new Vector3Int(-1, 0, -1)));
            Assert.That(BlockWorld.CellFromIndex(BlockWorld.ChunkAt(cell), BlockWorld.Index(cell)), Is.EqualTo(cell));
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.10f)); Assert.That(BlockWorld.ChunkSize, Is.EqualTo(16));
        }
        [Test]
        public void TopBoundaryAllowsLastCellButRejectsAbove8Metres()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            Assert.That(world.Set(new Vector3Int(0, 79, 0), BlockCell.Create(1)), Is.True);
            Assert.That(world.Set(new Vector3Int(0, 80, 0), BlockCell.Create(1)), Is.False);
            Assert.That(world.Set(new Vector3Int(160, 0, 0), BlockCell.Create(1)), Is.False);
            Assert.That(world.PinkCount, Is.EqualTo(1));
        }
        [Test]
        public void StagingCopyCannotChangePublishedOwnershipOrCount()
        {
            var world = new BlockWorld(Vector2.one); world.Set(Vector3Int.zero, BlockCell.Create(1)); world.Seal(1, world.Chunks.Keys.ToArray());
            var copy = world.Clone(); copy.Set(Vector3Int.zero, BlockCell.Create(2)); copy.Seal(2, copy.Chunks.Keys.ToArray());
            Assert.That(world.TryGet(Vector3Int.zero, out var published), Is.True); Assert.That(published.Owner, Is.EqualTo(1));
            Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(copy.PinkCount, Is.Zero); Assert.That(copy.BlueCount, Is.EqualTo(1));
        }
        [Test]
        public void OnlyAnchorOrSupportDirectlyBelowHoldsAStaticBlock()
        {
            var world = new BlockWorld(Vector2.one); var wall = new Vector3Int(0, 10, 0);
            world.Set(wall, BlockCell.Create(1, true)); world.Set(wall + Vector3Int.right, BlockCell.Create(1));
            Assert.That(world.HasSupport(wall), Is.True); Assert.That(world.HasSupport(wall + Vector3Int.right), Is.False);
            world.Set(wall + Vector3Int.right + Vector3Int.down, BlockCell.Create(2));
            Assert.That(world.HasSupport(wall + Vector3Int.right), Is.True);
        }
        [TestCase(-8f, -80, -80)]
        [TestCase(0f, 0, 0)]
        [TestCase(-7.78f, -77, -78)]
        [TestCase(.22f, 3, 2)]
        public void MinimumLandingIsSupportedWithoutSupportingSuspendedOrOutOfBoundsCells(float bottom, int landingY, int lowestLegalY)
        {
            var world = new BlockWorld(Vector2.one, bottom: bottom);
            var landing = new Vector3Int(-1, landingY, -1);
            var lowest = new Vector3Int(-1, lowestLegalY, -1);
            Assert.That(world.InBounds(landing), Is.True); Assert.That(world.InBounds(lowest), Is.True);
            Assert.That(world.HasSupport(landing, _ => false), Is.True);
            Assert.That(world.HasSupport(lowest, _ => false), Is.True);
            Assert.That(world.HasSupport(landing + Vector3Int.up, _ => false), Is.False);
            Assert.That(world.InBounds(lowest + Vector3Int.down), Is.False);
            Assert.That(world.HasSupport(lowest + Vector3Int.down, _ => false), Is.False);
            Assert.That(world.HasSupport(new Vector3Int(5, landingY, -1), _ => false), Is.False);

            // Potential-cell queries retain the existing map and directly-below support paths.
            Assert.That(world.HasSupport(landing + Vector3Int.up, _ => true), Is.True);
            Assert.That(world.Set(landing, BlockCell.Create(1)), Is.True);
            Assert.That(world.HasSupport(landing + Vector3Int.up, _ => false), Is.True);
        }
        [TestCase(-8f, -80)]
        [TestCase(0f, 0)]
        [TestCase(-7.78f, -78)]
        [TestCase(.22f, 2)]
        public void MinimumLandingRemainsSupportedAfterDetachSettleAndSnapshot(float bottom, int landingY)
        {
            var world = new BlockWorld(Vector2.one, bottom: bottom);
            var landing = new Vector3Int(-1, landingY, -1);
            Assert.That(world.Set(landing, BlockCell.Create(1)), Is.True);
            Assert.That(world.Set(landing + Vector3Int.up, BlockCell.Create(2)), Is.True);
            var run = world.Detach(landing, 2, 0);
            Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.BlueCount, Is.EqualTo(1));
            Assert.That(world.Settle(run.Id, landing, new HashSet<Vector3Int>()), Is.True);
            world.Seal(1, world.RevisionKeys.ToArray());
            byte[] snapshot = world.EncodeSnapshot();
            foreach (var restored in new[] { world, world.Clone(), BlockWorld.DecodeSnapshot(snapshot) })
            {
                Assert.That(restored.TryGet(landing, out var pink), Is.True);
                Assert.That(pink.Owner, Is.EqualTo(1)); Assert.That(pink.Anchored, Is.False);
                Assert.That(restored.TryGet(landing + Vector3Int.up, out var blue), Is.True);
                Assert.That(blue.Owner, Is.EqualTo(2)); Assert.That(blue.Anchored, Is.False);
                Assert.That(restored.HasSupport(landing, _ => false), Is.True);
                Assert.That(restored.HasSupport(landing + Vector3Int.up, _ => false), Is.True);
                Assert.That(restored.PinkCount, Is.EqualTo(1)); Assert.That(restored.BlueCount, Is.EqualTo(1));
                Assert.That(restored.Falling, Is.Empty); Assert.That(restored.NextRunId, Is.EqualTo(2ul));
                CollectionAssert.AreEqual(snapshot, restored.EncodeSnapshot());
            }
        }
        [Test]
        public void FallingKeepsBothTeamsAliveCountUntilExplicitDestruction()
        {
            var world = new BlockWorld(Vector2.one); var bottom = new Vector3Int(0, 20, 0);
            world.Set(bottom, BlockCell.Create(1)); world.Set(bottom + Vector3Int.up, BlockCell.Create(2));
            var run = world.Detach(bottom, 2, 10);
            Assert.That(world.TryGet(bottom, out _), Is.False); Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.BlueCount, Is.EqualTo(1));
            Assert.That(run.Offset(11), Is.EqualTo(4.905f).Within(.001));
            Assert.That(world.RemoveFallingCell(run.Id, 1, out var removed), Is.True); Assert.That(removed.Owner, Is.EqualTo(2));
            Assert.That(world.BlueCount, Is.Zero); Assert.That(world.PinkCount, Is.EqualTo(1));
        }
        [Test]
        public void FallingEditsAreCopyOnWriteAndLandingDoesNotDuplicateScore()
        {
            var world = new BlockWorld(Vector2.one); var bottom = new Vector3Int(0, 20, 0); world.Set(bottom, BlockCell.Create(1));
            var run = world.Detach(bottom, 1, 0); var copy = world.Clone();
            copy.RemoveFallingCell(run.Id, 0, out _); Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.Falling[run.Id].Cells[0].Owner, Is.EqualTo(1));
            var changed = new HashSet<Vector3Int>(); Assert.That(world.Settle(run.Id, Vector3Int.zero, changed), Is.True);
            Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.Falling, Is.Empty); Assert.That(changed, Does.Contain(Vector3Int.zero));
        }
        [Test]
        public void FallingTrajectoryAndCellsRemainIsolatedAcrossWorldCopies()
        {
            var world = new BlockWorld(Vector2.one); var bottom = new Vector3Int(0, 20, 0);
            world.Set(bottom, BlockCell.Create(1)); var run = world.Detach(bottom, 1, 0); var copy = world.Clone();
            copy.EditFalling(run.Id).LandingOffset = 1; copy.EditFalling(run.Id).LandingKnown = true;
            Assert.That(world.Falling[run.Id].LandingKnown, Is.False); Assert.That(world.Falling[run.Id].LandingOffset, Is.Zero);
            world.EditFalling(run.Id).InitialSpeed = 2; Assert.That(copy.Falling[run.Id].InitialSpeed, Is.Zero);
            world.RemoveFallingCell(run.Id, 0, out _);
            Assert.That(copy.Falling[run.Id].Cells[0].Owner, Is.EqualTo(1)); Assert.That(copy.PinkCount, Is.EqualTo(1)); Assert.That(world.PinkCount, Is.Zero);
        }
        [Test]
        public void FallingBroadPhaseKeepsInsideOriginAtZeroAndDoesNotHitRunHoles()
        {
            var obj = new GameObject("Block falling ray broad phase test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.Initialize(null);
                var bottom = new Vector3Int(0, 20, 0);
                terrain.World.Set(bottom, BlockCell.Create(1)); terrain.World.Set(bottom + Vector3Int.up, BlockCell.Create(2));
                var run = terrain.World.Detach(bottom, 2, 0); var origin = BlockWorld.Center(bottom);
                Assert.That(terrain.RaycastFalling(origin, Vector3.right, 1, 0, out var hit), Is.True);
                Assert.That(hit.Distance, Is.Zero); Assert.That(hit.Owner, Is.EqualTo(1)); Assert.That(hit.Cell, Is.EqualTo(bottom));
                Assert.That(terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True);
                Assert.That(contact.Stable, Is.False); Assert.That(contact.Owner, Is.EqualTo(1));
                terrain.World.RemoveFallingCell(run.Id, 0, out _);
                Assert.That(terrain.RaycastFalling(origin, Vector3.right, 1, 0, out _), Is.False, "the broad run bounds include an empty cell but the exact query must reject it");
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }
        [Test]
        public void OccupiedLandingIsRejectedWithoutPartiallyMovingTheRun()
        {
            var world = new BlockWorld(Vector2.one); var bottom = new Vector3Int(0, 20, 0);
            world.Set(bottom, BlockCell.Create(1)); world.Set(Vector3Int.zero, BlockCell.Create(2)); var run = world.Detach(bottom, 1, 0);
            Assert.That(world.Settle(run.Id, Vector3Int.zero, new HashSet<Vector3Int>()), Is.False);
            Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.BlueCount, Is.EqualTo(1)); Assert.That(world.Falling.Count, Is.EqualTo(1));
        }
        [Test]
        public void FallingRunRecomputesLandingAboveAnAlreadySettledColumn()
        {
            var obj = new GameObject("Block occupied landing test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.Tick(false, 100, .016f);
                var world = new BlockWorld(Vector2.one, bottom: 0);
                for (int y = 0; y < 16; y++) world.Set(new Vector3Int(0, y, 0), BlockCell.Create(2, y == 0));
                for (int y = 20; y < 25; y++) world.Set(new Vector3Int(0, y, 0), BlockCell.Create(1));
                var run = world.Detach(new Vector3Int(0, 20, 0), 5, 0);
                run.LandingKnown = true; run.LandingOffset = 2;
                typeof(BlockTerrain).GetMethod("FindLanding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(terrain, new object[] { world, run });
                Assert.That(run.LandingOffset, Is.EqualTo(.4f).Within(.001));
                Assert.That(world.Settle(run.Id, new Vector3Int(0, 16, 0), new HashSet<Vector3Int>()), Is.True);
                Assert.That(world.Falling, Is.Empty); Assert.That(world.PinkCount, Is.EqualTo(5)); Assert.That(world.BlueCount, Is.EqualTo(16));
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }
        [Test]
        public void SnapshotRoundTripsStableAndFallingCellsAndPreservesServerStartTime()
        {
            var world = new BlockWorld(new Vector2(32, 64), 7); world.Set(Vector3Int.zero, BlockCell.Create(1, true));
            var bottom = new Vector3Int(-1, 20, 0); world.Set(bottom, BlockCell.Create(2)); world.Detach(bottom, 1, 123.45);
            world.Seal(9, world.Chunks.Keys.ToArray()); var bytes = world.EncodeSnapshot(); var decoded = BlockWorld.DecodeSnapshot(bytes);
            Assert.That(decoded.Round, Is.EqualTo(7)); Assert.That(decoded.Revision, Is.EqualTo(9)); Assert.That(decoded.PinkCount, Is.EqualTo(1)); Assert.That(decoded.BlueCount, Is.EqualTo(1));
            Assert.That(decoded.Falling.Values.Single().StartedAt, Is.EqualTo(123.45)); Assert.That(decoded.EncodeSnapshot(), Is.EqualTo(bytes));
        }
        [Test]
        public void DeltaIncludesDeletedChunksAndRejectsTheWrongBaseline()
        {
            var world = new BlockWorld(Vector2.one, 1); world.Set(Vector3Int.zero, BlockCell.Create(1)); world.Seal(1, world.Chunks.Keys.ToArray());
            var peer = BlockWorld.DecodeSnapshot(world.EncodeSnapshot()); var next = world.Clone(); next.Set(Vector3Int.zero, default); next.Seal(2, new[] { Vector3Int.zero });
            var delta = next.EncodeDelta(1); var repaired = peer.WithDelta(delta);
            Assert.That(repaired.Revision, Is.EqualTo(2)); Assert.That(repaired.PinkCount, Is.Zero); Assert.That(repaired.Chunks, Is.Empty);
            Assert.Throws<InvalidDataException>(() => repaired.WithDelta(delta)); Assert.That(peer.PinkCount, Is.EqualTo(1));
        }
        [Test]
        public void SnapshotRejectsCorruptionAndExtraTrailingData()
        {
            var world = new BlockWorld(Vector2.one); var bytes = world.EncodeSnapshot(); bytes[0] ^= 1;
            Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(bytes));
            var trailing = world.EncodeSnapshot().Concat(new byte[] { 1 }).ToArray();
            Assert.Throws<InvalidDataException>(() => BlockWorld.DecodeSnapshot(trailing));
        }
        [Test]
        public void FullSnapshotKeepsDeletedChunkRevisionForSupportReconciliation()
        {
            var world = new BlockWorld(Vector2.one); world.Set(Vector3Int.zero, BlockCell.Create(1)); world.Seal(1, world.Chunks.Keys.ToArray());
            world.Set(Vector3Int.zero, default); world.Seal(2, new[] { Vector3Int.zero });
            var decoded = BlockWorld.DecodeSnapshot(world.EncodeSnapshot());
            Assert.That(decoded.Chunks, Is.Empty); Assert.That(decoded.ChunkRevision(Vector3Int.zero), Is.EqualTo(2));
        }
        [Test]
        public void CopyOnWriteAlsoProtectsSnapshotWhenTheOriginalWorldIsEdited()
        {
            var original = new BlockWorld(Vector2.one); original.Set(Vector3Int.zero, BlockCell.Create(1)); var snapshot = original.Clone();
            original.Set(Vector3Int.zero, BlockCell.Create(2));
            Assert.That(snapshot.TryGet(Vector3Int.zero, out var cell), Is.True); Assert.That(cell.Owner, Is.EqualTo(1));
        }
        [Test]
        public void GreedyCollisionMeshHidesInternalFacesAndMergesAdjacentOwners()
        {
            var world = new BlockWorld(Vector2.one); world.Set(Vector3Int.zero, BlockCell.Create(1)); world.Set(Vector3Int.right, BlockCell.Create(2));
            var collision = BlockMeshBuilder.Build(world, Vector3Int.zero, true); var render = BlockMeshBuilder.Build(world, Vector3Int.zero, false);
            Assert.That(collision.Triangles.Length, Is.EqualTo(36)); Assert.That(render.Triangles.Length, Is.GreaterThan(collision.Triangles.Length));
            Assert.That(collision.Vertices.Length, Is.EqualTo(24));
        }
        [Test]
        public void RoundFinishCancelsUnpublishedCommandsExactlyOnceWithoutScore()
        {
            var obj = new GameObject("Block cancellation test");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.Initialize(null); int callbacks = 0;
                terrain.QueueBuild(Vector3.zero, Vector3.up, 1, 20, .2f, false, result => { callbacks++; Assert.That(result.Changed, Is.Zero); });
                terrain.FinishRound(); terrain.Tick(true, 1, .016f);
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.PinkCount, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }
        // Independent row oracle reads the former Cells layout, never the new
        // row setter or prefix helper to compute an expected bit.
        static void AssertOccupiedRowsMatchCells(BlockWorld world)
        {
            foreach (var pair in world.Chunks)
            {
                var origin = pair.Key * 16;
                for (int axis = 0; axis < 3; axis++) for (int a = 0; a < 16; a++) for (int b = 0; b < 16; b++)
                {
                    ushort expected = 0;
                    for (int bit = 0; bit < 16; bit++)
                    {
                        int x = axis == 0 ? bit : a;
                        int y = axis == 1 ? bit : axis == 0 ? a : b;
                        int z = axis == 2 ? bit : b;
                        if (pair.Value.Cells[x + 16 * y + 256 * z].Occupied) expected |= (ushort)(1 << bit);
                    }
                    var local = axis == 0 ? new Vector3Int(0, a, b)
                        : axis == 1 ? new Vector3Int(a, 0, b) : new Vector3Int(a, b, 0);
                    Assert.That(world.OccupiedRow(origin + local, axis), Is.EqualTo(expected),
                        $"chunk={pair.Key}, axis={axis}, row=({a},{b})");
                }
            }
        }
        static Vector3Int[] OccupiedRowSeed(BlockWorld world)
        {
            var random = new System.Random(1739);
            var cells = Enumerable.Range(0, 96).Select(_ => new Vector3Int(random.Next(-20, 21), random.Next(-5, 29), random.Next(-20, 21))).Distinct().ToArray();
            foreach (var cell in cells) world.Set(cell, BlockCell.Create(1));
            return cells;
        }
        [Test] public void OccupiedRowsTrackSetClearOwnerAndDurabilityWithoutChangingTheirMeaning()
        {
            var world = new BlockWorld(new Vector2(8, 8)); var cells = OccupiedRowSeed(world);
            AssertOccupiedRowsMatchCells(world);
            for (int i = 0; i < cells.Length; i++)
            {
                if (i % 3 == 0) world.Set(cells[i], default);
                else world.Set(cells[i], new BlockCell { Owner = 2, Flags = 1, Durability = 7 });
            }
            AssertOccupiedRowsMatchCells(world);
            foreach (var cell in cells) world.Set(cell, BlockCell.Create(1, true));
            AssertOccupiedRowsMatchCells(world);
            foreach (var cell in cells) world.Set(cell, default);
            AssertOccupiedRowsMatchCells(world); Assert.That(world.PinkCount + world.BlueCount, Is.Zero);
        }
        [Test] public void OccupiedRowArraysAreIndependentAcrossBothDirectionsOfCopyOnWrite()
        {
            var source = new BlockWorld(new Vector2(8, 8)); var cells = OccupiedRowSeed(source);
            var copy = source.Clone(); byte[] copyBefore = copy.EncodeSnapshot();
            source.Set(cells[0], default); source.Set(cells[1], BlockCell.Create(2));
            CollectionAssert.AreEqual(copyBefore, copy.EncodeSnapshot());
            byte[] sourceBefore = source.EncodeSnapshot(); copy.Set(cells[2], default);
            CollectionAssert.AreEqual(sourceBefore, source.EncodeSnapshot());
            AssertOccupiedRowsMatchCells(source); AssertOccupiedRowsMatchCells(copy);
        }
        [Test] public void SnapshotAndDeltaReadChunkRebuildAllRowsWithoutAddingProtocolBytes()
        {
            var source = new BlockWorld(new Vector2(8, 8), 19); var cells = OccupiedRowSeed(source);
            source.Seal(11, source.Chunks.Keys.ToArray()); byte[] bytes = source.EncodeSnapshot();
            var peer = BlockWorld.DecodeSnapshot(bytes); CollectionAssert.AreEqual(bytes, peer.EncodeSnapshot());
            AssertOccupiedRowsMatchCells(peer);
            var next = source.Clone(); var deleted = BlockWorld.ChunkAt(cells[0]);
            foreach (var cell in cells.Where(c => BlockWorld.ChunkAt(c) == deleted)) next.Set(cell, default);
            next.Set(new Vector3Int(31, 31, -31), BlockCell.Create(2));
            var changed = source.Chunks.Keys.Concat(next.Chunks.Keys).Distinct().ToArray(); next.Seal(12, changed);
            var installed = peer.WithDelta(next.EncodeDelta(11));
            CollectionAssert.AreEqual(next.EncodeSnapshot(), installed.EncodeSnapshot()); AssertOccupiedRowsMatchCells(installed);
            var rebuilt = installed.Clone(); rebuilt.Set(cells[0], BlockCell.Create(2)); rebuilt.Seal(13, new[] { deleted });
            var reinstalled = installed.WithDelta(rebuilt.EncodeDelta(12));
            CollectionAssert.AreEqual(rebuilt.EncodeSnapshot(), reinstalled.EncodeSnapshot()); AssertOccupiedRowsMatchCells(reinstalled);
        }
        [Test] public void DetachRemoveAndSettleKeepOnlyStaticCellsInOccupiedRows()
        {
            var world = new BlockWorld(new Vector2(8, 8)); var bottom = new Vector3Int(-17, 20, 15);
            for (int i = 0; i < 5; i++) world.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)(i % 2 + 1)));
            AssertOccupiedRowsMatchCells(world); var run = world.Detach(bottom, 5, 12);
            AssertOccupiedRowsMatchCells(world); Assert.That(world.OccupiedRow(bottom, 1), Is.Zero);
            world.RemoveFallingCell(run.Id, 2, out _); AssertOccupiedRowsMatchCells(world);
            Assert.That(world.Settle(run.Id, new Vector3Int(-17, -3, 15), new HashSet<Vector3Int>()), Is.True);
            AssertOccupiedRowsMatchCells(world); Assert.That(world.Falling, Is.Empty);
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void FullSixteenBitRowsAndMissingNegativeChunksHaveExactMasks(int axis)
        {
            var world = new BlockWorld(new Vector2(8, 8)); var origin = new Vector3Int(-32, -16, 16);
            var step = axis == 0 ? Vector3Int.right : axis == 1 ? Vector3Int.up : new Vector3Int(0, 0, 1);
            for (int i = 0; i < 16; i++) world.Set(origin + step * i, BlockCell.Create(2));
            Assert.That(world.OccupiedRow(origin, axis), Is.EqualTo(ushort.MaxValue));
            world.Set(origin + step * 15, default); Assert.That(world.OccupiedRow(origin, axis), Is.EqualTo(32767));
            world.Set(origin, default); Assert.That(world.OccupiedRow(origin, axis), Is.EqualTo(32766));
            int chunks = world.Chunks.Count; Assert.That(world.OccupiedRow(new Vector3Int(-1000, -1000, -1000), axis), Is.Zero);
            Assert.That(world.Chunks.Count, Is.EqualTo(chunks)); AssertOccupiedRowsMatchCells(world);
        }
        [Test] public void OccupiedRowsRejectAnUnknownAxisInsteadOfReadingTheWrongPlane()
        {
            var world = new BlockWorld(Vector2.one);
            Assert.Throws<ArgumentOutOfRangeException>(() => world.OccupiedRow(Vector3Int.zero, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.OccupiedRow(Vector3Int.zero, 3));
        }

        [Test]
        public void UnsealedDetachSnapshotAndDeltaKeepBothTeamsAndLegacyMotionWithoutStableCells()
        {
            var source = new BlockWorld(new Vector2(8, 8), 23); var bottom = new Vector3Int(0, 15, 0);
            for (int i = 0; i < 18; i++) Assert.That(source.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)(1 + i % 2))), Is.True);
            source.Seal(7, source.Chunks.Keys.ToArray());
            var run = source.Detach(bottom, 18, 123.45);
            // Leave the real zero-count static chunks unsealed: fallen cells still count and retain their epoch.
            var chunksBefore = source.Chunks.ToDictionary(p => p.Key, p => p.Value); var cellsBefore = run.Cells;
            Assert.That(chunksBefore.Count, Is.EqualTo(3)); Assert.That(chunksBefore.Values.All(c => c.Count == 0), Is.True);
            Assert.That(source.PinkCount, Is.EqualTo(9)); Assert.That(source.BlueCount, Is.EqualTo(9));
            byte[] snapshot = source.EncodeSnapshot(); var decoded = BlockWorld.DecodeSnapshot(snapshot);
            var baseline = new BlockWorld(source.Dimensions, source.Round, source.Bottom);
            for (int i = 0; i < 18; i++) Assert.That(baseline.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)(1 + i % 2))), Is.True);
            baseline.Seal(0, baseline.Chunks.Keys.ToArray());
            Assert.That(baseline.PinkCount, Is.EqualTo(9)); Assert.That(baseline.BlueCount, Is.EqualTo(9));
            Assert.That(baseline.Chunks.Count, Is.EqualTo(3));
            var repaired = baseline.WithDelta(source.EncodeDelta(0), out var changed);
            CollectionAssert.AreEquivalent(new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 2, 0) }, changed);
            foreach (var restored in new[] { decoded, repaired })
            {
                Assert.That(restored.Round, Is.EqualTo(source.Round)); Assert.That(restored.Revision, Is.EqualTo(7u));
                Assert.That(restored.Chunks, Is.Empty); Assert.That(restored.PinkCount, Is.EqualTo(9)); Assert.That(restored.BlueCount, Is.EqualTo(9));
                Assert.That(restored.Falling.Count, Is.EqualTo(1)); var fallen = restored.Falling[run.Id];
                Assert.That(fallen.Cohort, Is.Null); Assert.That(fallen.Bottom, Is.EqualTo(bottom)); Assert.That(fallen.StartedAt, Is.EqualTo(123.45));
                Assert.That(fallen.Cells.Length, Is.EqualTo(18));
                CollectionAssert.AreEqual(cellsBefore.Select(c => (c.Owner, c.Flags, c.Durability)).ToArray(), fallen.Cells.Select(c => (c.Owner, c.Flags, c.Durability)).ToArray());
                foreach (var key in chunksBefore.Keys) Assert.That(restored.ChunkRevision(key), Is.EqualTo(source.ChunkRevision(key)));
                CollectionAssert.AreEqual(snapshot, restored.EncodeSnapshot());
            }
            Assert.That(source.Falling[run.Id], Is.SameAs(run)); Assert.That(run.Cells, Is.SameAs(cellsBefore));
            foreach (var pair in chunksBefore) { Assert.That(source.Chunks[pair.Key], Is.SameAs(pair.Value)); Assert.That(pair.Value.Count, Is.Zero); }
            CollectionAssert.AreEqual(snapshot, source.EncodeSnapshot()); Assert.That(baseline.Revision, Is.Zero);
            Assert.That(baseline.Chunks.Count, Is.EqualTo(3)); Assert.That(baseline.Falling, Is.Empty);
            Assert.That(baseline.PinkCount, Is.EqualTo(9)); Assert.That(baseline.BlueCount, Is.EqualTo(9));
            for (int i = 0; i < 18; i++) { Assert.That(baseline.TryGet(bottom + Vector3Int.up * i, out var cell), Is.True); Assert.That(cell.Owner, Is.EqualTo((byte)(1 + i % 2))); }
        }

        [Test]
        public void RebuiltChunkOverlapsOldTombstoneAndKeepsLiveCellsAndExactRevisionInSnapshotAndDelta()
        {
            var source = new BlockWorld(new Vector2(8, 8), 24); var first = new Vector3Int(1, 32, 1); var second = first + Vector3Int.right;
            var key = BlockWorld.ChunkAt(first); Assert.That(source.Set(first, BlockCell.Create(1)), Is.True); source.Seal(1, new[] { key });
            Assert.That(source.Set(first, default), Is.True); source.Seal(2, new[] { key });
            Assert.That(source.Chunks, Is.Empty); Assert.That(source.ChunkRevision(key), Is.EqualTo(2u));
            Assert.That(source.Set(first, BlockCell.Create(2)), Is.True); Assert.That(source.Set(second, BlockCell.Create(1)), Is.True);
            // Set revives a real chunk beside its retained tombstone; no Seal or synthetic metadata edits hide that overlap.
            var liveChunk = source.Chunks[key]; var liveCells = liveChunk.Cells;
            Assert.That(source.ChunkRevision(key), Is.Zero, "the present chunk has priority over hidden tombstone history");
            byte[] snapshot = source.EncodeSnapshot(); var decoded = BlockWorld.DecodeSnapshot(snapshot);
            var baseline = new BlockWorld(source.Dimensions, source.Round, source.Bottom);
            Assert.That(baseline.Set(first, BlockCell.Create(2)), Is.True); Assert.That(baseline.Set(second, BlockCell.Create(1)), Is.True);
            baseline.Seal(0, new[] { key });
            Assert.That(baseline.PinkCount, Is.EqualTo(1)); Assert.That(baseline.BlueCount, Is.EqualTo(1));
            Assert.That(baseline.Chunks[key].Count, Is.EqualTo(2));
            var repaired = baseline.WithDelta(source.EncodeDelta(0), out var changed);
            CollectionAssert.AreEquivalent(new[] { key }, changed);
            foreach (var restored in new[] { decoded, repaired })
            {
                Assert.That(restored.Round, Is.EqualTo(source.Round)); Assert.That(restored.Revision, Is.EqualTo(2u));
                Assert.That(restored.Chunks.Count, Is.EqualTo(1)); Assert.That(restored.Chunks[key].Count, Is.EqualTo(2));
                Assert.That(restored.ChunkRevision(key), Is.Zero); Assert.That(restored.PinkCount, Is.EqualTo(1)); Assert.That(restored.BlueCount, Is.EqualTo(1));
                Assert.That(restored.TryGet(first, out var blue), Is.True); Assert.That(blue.Owner, Is.EqualTo(2));
                Assert.That(restored.TryGet(second, out var pink), Is.True); Assert.That(pink.Owner, Is.EqualTo(1));
                Assert.That(restored.Falling, Is.Empty); CollectionAssert.AreEqual(snapshot, restored.EncodeSnapshot());
            }
            Assert.That(source.Chunks[key], Is.SameAs(liveChunk)); Assert.That(source.Chunks[key].Cells, Is.SameAs(liveCells));
            CollectionAssert.AreEqual(snapshot, source.EncodeSnapshot());
            // Clearing the rebuilt chunk without another Seal must also produce an explicit deletion, once per key.
            Assert.That(source.Set(first, default), Is.True); Assert.That(source.Set(second, default), Is.True);
            byte[] emptied = source.EncodeSnapshot(); var emptySnapshot = BlockWorld.DecodeSnapshot(emptied);
            var emptyDelta = baseline.WithDelta(source.EncodeDelta(0), out var emptyChanged);
            CollectionAssert.AreEquivalent(new[] { key }, emptyChanged);
            foreach (var restored in new[] { emptySnapshot, emptyDelta })
            {
                Assert.That(restored.Chunks, Is.Empty); Assert.That(restored.PinkCount + restored.BlueCount, Is.Zero);
                Assert.That(restored.Revision, Is.EqualTo(2u)); Assert.That(restored.ChunkRevision(key), Is.Zero);
                Assert.That(restored.Falling, Is.Empty); CollectionAssert.AreEqual(emptied, restored.EncodeSnapshot());
            }
            Assert.That(source.Chunks[key], Is.SameAs(liveChunk)); Assert.That(liveChunk.Count, Is.Zero);
            Assert.That(baseline.Chunks.Count, Is.EqualTo(1)); Assert.That(baseline.Falling, Is.Empty); Assert.That(baseline.Revision, Is.Zero);
            Assert.That(baseline.PinkCount, Is.EqualTo(1)); Assert.That(baseline.BlueCount, Is.EqualTo(1));
            Assert.That(baseline.TryGet(first, out var originalBlue), Is.True); Assert.That(originalBlue.Owner, Is.EqualTo(2));
            Assert.That(baseline.TryGet(second, out var originalPink), Is.True); Assert.That(originalPink.Owner, Is.EqualTo(1));
        }

        [Test]
        public void BatchEncodingKeepsUnsealedRebuiltTombstonesAndEmptyChunksInDeltaMembership()
        {
            var world = new BlockWorld(new Vector2(8, 8), 24); var cell = new Vector3Int(1, 32, 1); var key = BlockWorld.ChunkAt(cell);
            world.Set(cell, BlockCell.Create(1)); world.Seal(1, new[] { key });
            world.Set(cell, default); world.Seal(2, new[] { key });
            world.Set(cell, BlockCell.Create(2)); world.Set(cell + Vector3Int.right, BlockCell.Create(1));
            Assert.That(world.ChunkRevision(key), Is.Zero); Assert.That(world.Revision, Is.EqualTo(2u));
            var requests = new[] { new BlockWorld.EncodingRequest(true), new BlockWorld.EncodingRequest(false, 0),
                new BlockWorld.EncodingRequest(false, 1), new BlockWorld.EncodingRequest(false, 2), new BlockWorld.EncodingRequest(false, 1) };
            foreach (bool remove in new[] { false, true })
            {
                if (remove) { world.Set(cell, default); world.Set(cell + Vector3Int.right, default); }
                byte[] before = world.EncodeSnapshot(); var chunk = world.Chunks[key]; var cells = chunk.Cells;
                var encoded = world.EncodeBatch(requests);
                for (int i = 0; i < requests.Length; i++)
                    CollectionAssert.AreEqual(requests[i].Full ? before : world.EncodeDelta(requests[i].Since), encoded[i], "request " + i + " removed=" + remove);
                Assert.That(encoded[2], Is.SameAs(encoded[4]));
                Assert.That(BitConverter.ToInt32(encoded[1], 16), Is.EqualTo(1));
                Assert.That(BitConverter.ToInt32(encoded[2], 16), Is.EqualTo(1));
                Assert.That(BitConverter.ToInt32(encoded[3], 16), Is.Zero);
                Assert.That(world.Chunks[key], Is.SameAs(chunk)); Assert.That(chunk.Cells, Is.SameAs(cells));
                CollectionAssert.AreEqual(before, world.EncodeSnapshot());
            }
        }

        [Test]
        public void BatchEncodingNeverReusesBytesAcrossSameRevisionMutationWorldOrRoundChanges()
        {
            var world = new BlockWorld(new Vector2(8, 8), 7); var bottom = new Vector3Int(0, 20, 0);
            world.Set(bottom, BlockCell.Create(1)); var run = world.Detach(bottom, 1, 2); world.Seal(5, world.RevisionKeys.ToArray());
            var requests = new[] { new BlockWorld.EncodingRequest(true), new BlockWorld.EncodingRequest(false, 4),
                new BlockWorld.EncodingRequest(false, 4) };
            var first = world.EncodeBatch(requests); uint revision = world.Revision; ulong generation = world.MutationGeneration;
            run.InitialSpeed = 1.25f; // Real public setter changes wire bytes without bumping either scalar.
            Assert.That(world.Revision, Is.EqualTo(revision)); Assert.That(world.MutationGeneration, Is.EqualTo(generation));
            var second = world.EncodeBatch(requests);
            Assert.That(second[0], Is.Not.EqualTo(first[0])); Assert.That(second[0], Is.Not.SameAs(first[0]));
            CollectionAssert.AreEqual(world.EncodeSnapshot(), second[0]); CollectionAssert.AreEqual(world.EncodeDelta(4), second[1]);
            Assert.That(second[1], Is.SameAs(second[2]));
            world.EditFalling(run.Id).LandingKnown = true; world.EditFalling(run.Id).LandingOffset = .5f;
            var third = world.EncodeBatch(requests); CollectionAssert.AreEqual(world.EncodeSnapshot(), third[0]);
            Assert.That(third[0], Is.Not.EqualTo(second[0])); Assert.That(world.Revision, Is.EqualTo(revision));
            foreach (uint round in new[] { 7u, 8u })
            {
                var replacement = new BlockWorld(world.Dimensions, round); replacement.Set(Vector3Int.zero, BlockCell.Create(2, true));
                replacement.Seal(revision, replacement.RevisionKeys.ToArray()); var encoded = replacement.EncodeBatch(requests);
                CollectionAssert.AreEqual(replacement.EncodeSnapshot(), encoded[0]); CollectionAssert.AreEqual(replacement.EncodeDelta(4), encoded[1]);
                Assert.That(encoded[0], Is.Not.SameAs(third[0])); Assert.That(encoded[0], Is.Not.EqualTo(third[0]));
            }
            Assert.That(world.EncodeBatch(Array.Empty<BlockWorld.EncodingRequest>()), Is.Empty);
            Assert.Throws<ArgumentOutOfRangeException>(() => world.EncodeBatch(new[] { new BlockWorld.EncodingRequest(true), new BlockWorld.EncodingRequest(false, revision + 1) }));
        }

    }
}
