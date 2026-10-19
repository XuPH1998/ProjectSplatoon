#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Pure EditMode data fixtures. No GameObject, collider, Player or Physics call.
    [Category("BlockFixedGridTransactionRows69")]
    public sealed class BlockFixedGridTransactionRows69Tests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo Target = typeof(BlockFixedGridTransaction).GetMethod("FindLandingTarget", Private);
        static readonly MethodInfo Column = typeof(BlockFixedGridTransaction).GetMethod("ColumnStates", Private);
        static readonly MethodInfo RemoveState = typeof(BlockFixedGridTransaction).GetMethod("Remove", Private);
        static readonly MethodInfo StageState = typeof(BlockFixedGridTransaction).GetMethod("StageUpsert", Private);
        static readonly FieldInfo Writes = typeof(BlockFixedGridTransaction).GetField("CellWrites", Private);
        static readonly FieldInfo Upserts = typeof(BlockFixedGridTransaction).GetField("Upserts", Private);
        static readonly FieldInfo Deleted = typeof(BlockFixedGridTransaction).GetField("Deleted", Private);
        static readonly FieldInfo Rows = typeof(BlockFixedGridTransaction).GetField("_verticalWrites", Private);
        static readonly FieldInfo Columns = typeof(BlockFixedGridTransaction).GetField("_upsertsByColumn", Private);

        sealed class Environment : IBlockFixedGridEnvironment, IBlockFixedGridCommitGuard,
            IBlockFixedGridRecoveryRollback, IBlockFixedGridPaintAdmission
        {
            internal float[] Heights;
            internal Func<Vector3Int, BlockFixedGridMapSupport> Override;
            internal BlockFixedGridTransaction Transaction;
            internal Vector3Int LastFrom;
            internal BlockFixedGridMapSupport LastSupport;
            internal bool RejectGuard, DenyPaint, DenyPlacement, ThrowRecovery;
            internal int PlacementCalls, RecoveryCalls, RollbackCalls;
            public bool TryGetMapSupport(Vector3Int from, out BlockFixedGridMapSupport support)
            {
                LastFrom = from;
                if (Override != null) { LastSupport = support = Override(from); return true; }
                float ceiling = (from.y + 1) * BlockWorld.CellSize - .00001f;
                for (int i = Heights.Length - 1; i >= 0; i--)
                    if (Heights[i] <= ceiling)
                    { LastSupport = support = Support(i, Heights[i], from.x, from.z); return true; }
                LastSupport = support = default; return false;
            }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support,
                bool embeddedBase, bool relocating, out string reason)
            {
                PlacementCalls++;
                if (Transaction != null)
                {
                    // Runs during actual Prepare as well as Build, before each reinsert.
                    var expected = LastFrom;
                    expected.y = LegacyTarget(Transaction, LastFrom, LastSupport.BaseCellY, out _);
                    Assert.That(cell, Is.EqualTo(expected), "Actual placement must equal the frozen old loop.");
                    Assert.That(support, Is.EqualTo(LastSupport));
                    Assert.That(embeddedBase, Is.EqualTo(cell.y == LastSupport.BaseCellY));
                }
                reason = "fixture placement rejected"; return !DenyPlacement;
            }
            public bool Visible(Vector3 from, Vector3 to) => true;
            public bool PaintAllowed(BlockSurfaceKey key, out string reason)
            { reason = "fixture paint rejected"; return !DenyPaint; }
            public bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason)
            { reason = "fixture guard rejected"; return !RejectGuard; }
            public void CommitPreparedRecovery()
            { RecoveryCalls++; if (ThrowRecovery) throw new InvalidOperationException("fixture recovery failed after publication"); }
            public void RollbackPreparedRecovery() { RollbackCalls++; }
        }

        sealed class Fixture
        {
            internal readonly Environment Env;
            internal readonly BlockFixedGridWorld World;
            internal Fixture(float[] heights = null, float bottom = -8)
            {
                heights ??= new[] { 0f };
                Env = new Environment { Heights = heights };
                var surface = new BlockSurfaceWorld(7);
                var squares = new List<Vector2Int>();
                for (int x = -24; x <= 24; x++) for (int z = -24; z <= 24; z++) squares.Add(new Vector2Int(x, z));
                for (int i = 0; i < heights.Length; i++)
                    surface.RegisterFace(11 + i, Vector3.up * heights[i], Vector3.right, Vector3.forward, squares);
                World = new BlockFixedGridWorld(new BlockWorld(new Vector2(80, 80), 7, bottom), surface, Env);
            }
            internal void Color(byte team = 1, int x = 0, int z = 0, int level = 0)
                => World.Surfaces.TrySet(new BlockSurfaceKey(11 + level, x, z), team, out _);
            internal BlockFixedGridState Seed(int fromY, byte team = 1, int x = 0, int z = 0)
            {
                Assert.That(World.Build(new[] { new Vector3Int(x, fromY, z) }, team, 1).Added, Is.EqualTo(1), World.LastFailure);
                return World.Blocks.Values.Where(s => s.Cell.x == x && s.Cell.z == z).OrderByDescending(s => s.Handle.Id).First();
            }
            internal BlockFixedGridTransaction Begin()
            { var tx = World.BeginMutation(); Env.Transaction = tx; return tx; }
        }

        static BlockFixedGridMapSupport Support(int level, float height, int x = 0, int z = 0)
            => new(100 + level, height, new BlockSurfaceKey(11 + level, x, z));
        static Dictionary<ulong, BlockFixedGridState> Staged(BlockFixedGridTransaction tx)
            => (Dictionary<ulong, BlockFixedGridState>)Upserts.GetValue(tx);
        static Dictionary<Vector3Int, ulong> CellWrites(BlockFixedGridTransaction tx)
            => (Dictionary<Vector3Int, ulong>)Writes.GetValue(tx);
        static Dictionary<ulong, BlockFixedGridHandle> Removed(BlockFixedGridTransaction tx)
            => (Dictionary<ulong, BlockFixedGridHandle>)Deleted.GetValue(tx);
        static BlockFixedGridWorld Owner(BlockFixedGridTransaction tx)
            => (BlockFixedGridWorld)typeof(BlockFixedGridTransaction).GetField("Owner", Private).GetValue(tx);
        static long Counter(BlockFixedGridTransaction tx, string field)
            => (long)typeof(BlockFixedGridTransaction).GetField(field, Private).GetValue(tx);
        static int ActualTarget(BlockFixedGridTransaction tx, Vector3Int from, int baseY)
            => (int)Target.Invoke(tx, new object[] { from, baseY });

        // Exact loop removed from production TryLanding. Keep this cell-based oracle
        // independent of OccupiedRow, _verticalWrites and the new highest-bit helper.
        static int LegacyTarget(BlockFixedGridTransaction tx, Vector3Int from, int baseY, out int visited)
        {
            int target = baseY; visited = 0;
            for (int y = from.y; y > baseY;)
            {
                y--; visited++;
                var below = new Vector3Int(from.x, y, from.z);
                if (!Owner(tx).Storage.InBounds(below)) break;
                if (!tx.TryGet(below, out _)) continue;
                target = y + 1; break;
            }
            return target;
        }
        // Frozen old ColumnStates algorithm expressed through the public published
        // states plus the original transaction dictionaries. It intentionally scans
        // every staged upsert, so the candidate index cannot validate itself.
        static BlockFixedGridState[] LegacyColumn(BlockFixedGridTransaction tx, Vector2Int column)
        {
            var result = new Dictionary<ulong, BlockFixedGridState>();
            foreach (var old in Owner(tx).Blocks.Values)
            {
                if (old.Cell.x != column.x || old.Cell.z != column.y || Removed(tx).ContainsKey(old.Handle.Id)) continue;
                var state = Staged(tx).TryGetValue(old.Handle.Id, out var changed) ? changed : old;
                if (state.Cell.x == column.x && state.Cell.z == column.y) result[state.Handle.Id] = state;
            }
            foreach (var pair in Staged(tx))
                if (pair.Value.Cell.x == column.x && pair.Value.Cell.z == column.y) result[pair.Key] = pair.Value;
            return result.Values.OrderBy(s => s.Cell.y).ThenBy(s => s.Handle.Id).ToArray();
        }
        static void AssertColumn(BlockFixedGridTransaction tx, Vector2Int column)
        {
            var actual = ((IEnumerable<BlockFixedGridState>)Column.Invoke(tx, new object[] { column })).ToArray();
            Assert.That(actual, Is.EqualTo(LegacyColumn(tx, column)), "Column " + column);
        }
        static void AssertRows(BlockFixedGridTransaction tx, System.Random random, int count = 40)
        {
            for (int i = 0; i < count; i++)
            {
                var from = new Vector3Int(random.Next(-20, 21), random.Next(-26, 26), random.Next(-20, 21));
                if (!Owner(tx).Storage.InBounds(from)) continue;
                int baseY = random.Next(-45, 27);
                Assert.That(ActualTarget(tx, from, baseY), Is.EqualTo(LegacyTarget(tx, from, baseY, out _)),
                    "from=" + from + ", base=" + baseY);
            }
        }
        static void AssertAuxiliaryIndexes(BlockFixedGridTransaction tx)
        {
            var index = (Dictionary<Vector2Int, HashSet<ulong>>)Columns.GetValue(tx);
            var indexed = new HashSet<ulong>();
            foreach (var pair in index)
            {
                Assert.That(pair.Value, Is.Not.Empty);
                foreach (ulong id in pair.Value)
                {
                    Assert.That(indexed.Add(id), Is.True);
                    Assert.That(Staged(tx).TryGetValue(id, out var state), Is.True);
                    Assert.That(new Vector2Int(state.Cell.x, state.Cell.z), Is.EqualTo(pair.Key));
                }
            }
            Assert.That(indexed.SetEquals(Staged(tx).Keys), Is.True);
            var rows = (System.Collections.IDictionary)Rows.GetValue(tx);
            var expected = new Dictionary<Vector3Int, (ushort written, ushort occupied)>();
            foreach (var pair in CellWrites(tx))
            {
                var key = new Vector3Int(pair.Key.x, pair.Key.y & ~15, pair.Key.z);
                expected.TryGetValue(key, out var row);
                ushort bit = (ushort)(1 << (pair.Key.y - key.y));
                row.written |= bit;
                if (pair.Value != 0) row.occupied |= bit;
                expected[key] = row;
            }
            Assert.That(rows.Count, Is.EqualTo(expected.Count));
            foreach (var pair in expected)
            {
                Assert.That(rows.Contains(pair.Key), Is.True);
                object actual = rows[pair.Key]; var type = actual.GetType();
                Assert.That(type.GetField("Written", Private).GetValue(actual), Is.EqualTo(pair.Value.written));
                Assert.That(type.GetField("Occupied", Private).GetValue(actual), Is.EqualTo(pair.Value.occupied));
            }
        }
        static void SameSnapshot(BlockFixedGridWorld world, BlockFixedGridSnapshot before)
        {
            var after = world.Capture(true);
            Assert.That(after.Epoch, Is.EqualTo(before.Epoch)); Assert.That(after.Round, Is.EqualTo(before.Round));
            Assert.That(after.Revision, Is.EqualTo(before.Revision)); Assert.That(after.SurfaceRevision, Is.EqualTo(before.SurfaceRevision));
            Assert.That(after.NextId, Is.EqualTo(before.NextId)); Assert.That(after.Upserts, Is.EqualTo(before.Upserts));
            Assert.That(after.Removed, Is.EqualTo(before.Removed)); Assert.That(after.SurfaceBytes, Is.EqualTo(before.SurfaceBytes));
        }

        [Test] public void RandomBuildDeleteAndPaintOverlaysMatchFrozenCellAndColumnOracles()
        {
            var f = new Fixture(new[] { -5.1f, 0f, 4.8f }); var random = new System.Random(690029);
            for (int level = 0; level < 3; level++)
                for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) f.Color(1, x, z, level);
            var published = new List<Vector3Int>();
            foreach (int baseY in new[] { -17, 0, 16 })
                for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++)
                { published.Add(new Vector3Int(x, baseY, z)); published.Add(new Vector3Int(x, baseY + 1, z)); }
            Assert.That(f.World.Build(published, 1, published.Count).Added, Is.EqualTo(published.Count));
            using var tx = f.Begin();
            for (int operation = 0; operation < 240; operation++)
            {
                int x = random.Next(-3, 4), z = random.Next(-3, 4), level = random.Next(3);
                int fromY = new[] { -14, 6, 23 }[level]; byte team = (byte)random.Next(1, 3);
                if (operation % 5 == 0)
                    tx.Paint(new[] { new BlockSurfaceKey(11 + level, x, z) }, team, 1);
                else if (operation % 3 == 0)
                {
                    var states = LegacyColumn(tx, new Vector2Int(x, z));
                    if (states.Length != 0) tx.Destroy(new[] { states[random.Next(states.Length)].Handle }, team);
                }
                else tx.Build(new[] { new Vector3Int(x, fromY, z), new Vector3Int(x, fromY - 1, z) }, team, 2);
                AssertAuxiliaryIndexes(tx); AssertRows(tx, random);
                for (int cx = -3; cx <= 3; cx++) AssertColumn(tx, new Vector2Int(cx, z));
            }
            // Prepare's placement callback also compares every actual reinsert to
            // the old cell oracle, before that identity is inserted again.
            Assert.That(tx.TryCommit(out _, out string error), Is.True, error); AssertAuxiliaryIndexes(tx);
        }

        [TestCase(-17)] [TestCase(-16)] [TestCase(-1)] [TestCase(0)] [TestCase(15)] [TestCase(16)]
        public void VerticalRowsKeepNegativeAndSixteenBoundariesSeparate(int baseY)
        {
            var f = new Fixture(new[] { baseY * BlockWorld.CellSize });
            var columns = new[] { new Vector2Int(-17, -16), new Vector2Int(-16, -17), new Vector2Int(-1, 0),
                new Vector2Int(0, -1), new Vector2Int(15, 16), new Vector2Int(16, 15) };
            foreach (var c in columns) { f.Color(1, c.x, c.y); f.Seed(baseY + 3, 1, c.x, c.y); }
            using var tx = f.Begin();
            foreach (var c in columns)
            {
                var from = new Vector3Int(c.x, Math.Min(25, baseY + 6), c.y);
                Assert.That(ActualTarget(tx, from, baseY), Is.EqualTo(baseY + 1));
                Assert.That(ActualTarget(tx, from, baseY), Is.EqualTo(LegacyTarget(tx, from, baseY, out _)));
                tx.Build(new[] { from }, 1, 1); AssertColumn(tx, c);
            }
            AssertAuxiliaryIndexes(tx);
        }

        [TestCase(-8f)] [TestCase(-4.7f)] [TestCase(-4.8000002f)]
        public void CompleteRangeRetainsTheOriginalFirstOutOfBoundsStop(float bottom)
        {
            var f = new Fixture(bottom: bottom); using var tx = f.Begin();
            foreach (int baseY in new[] { -80, -27, -26, -17, -16, -1, 0, 15, 16, 25, 30 })
            {
                var from = new Vector3Int(-16, 25, 15);
                Assert.That(ActualTarget(tx, from, baseY), Is.EqualTo(LegacyTarget(tx, from, baseY, out _)));
            }
        }

        [Test] public void EmptyFullHeightSearchReadsRowsInsteadOfEveryEmptyCell()
        {
            var f = new Fixture(); using var tx = f.Begin(); var from = new Vector3Int(-17, 25, 16);
            int expected = LegacyTarget(tx, from, -26, out int oldCells);
            long before = Counter(tx, "LandingRowReads");
            Assert.That(ActualTarget(tx, from, -26), Is.EqualTo(expected));
            Assert.That(oldCells, Is.EqualTo(51));
            Assert.That(Counter(tx, "LandingRowReads") - before, Is.EqualTo(4));
            Assert.That(Counter(tx, "LandingOccupiedChecks"), Is.Zero);
        }

        [Test] public void RemovingAndReplacingPublishedCellsUpdatesOverlayBeforeCommit()
        {
            var f = new Fixture(new[] { -4.8f }); f.Color();
            var a = f.Seed(-13); var b = f.Seed(-13); using var tx = f.Begin();
            tx.Destroy(new[] { b.Handle }, 2);
            Assert.That(ActualTarget(tx, new Vector3Int(0, 1, 0), -16), Is.EqualTo(-15));
            tx.Build(new[] { new Vector3Int(0, 1, 0) }, 1, 1);
            Assert.That(ActualTarget(tx, new Vector3Int(0, 1, 0), -16), Is.EqualTo(-14));
            Assert.That(tx.TryGet(a.Cell, out var retained), Is.True); Assert.That(retained.Handle, Is.EqualTo(a.Handle));
            AssertAuxiliaryIndexes(tx); AssertColumn(tx, Vector2Int.zero);
            Assert.That(tx.TryCommit(out _, out string error), Is.True, error); AssertAuxiliaryIndexes(tx);
        }

        [Test] public void NoOpRestackForgetsWritesAndRestoresPublishedRowFallback()
        {
            var f = new Fixture(new[] { -4.8f }); f.Color(); var a = f.Seed(-13); f.Seed(-13);
            var before = f.World.Capture(true); using var tx = f.Begin(); tx.ReconcileColumns(new[] { Vector2Int.zero });
            Assert.That(tx.TryCommit(out _, out string error), Is.True, error);
            Assert.That(Staged(tx), Is.Empty); Assert.That(CellWrites(tx), Is.Empty);
            Assert.That(((System.Collections.IDictionary)Rows.GetValue(tx)).Count, Is.Zero);
            AssertAuxiliaryIndexes(tx); SameSnapshot(f.World, before);
            Assert.That(ActualTarget(tx, new Vector3Int(0, 2, 0), -16), Is.EqualTo(-14));
            Assert.That(f.World.TryGet(a.Handle, out _), Is.True);
        }

        [Test] public void SameLandingPaintThenSolidAndAlternatingRestackConversionsKeepRules()
        {
            var f = new Fixture(); using (var tx = f.Begin())
            {
                Assert.That(tx.Build(new[] { new Vector3Int(0, 3, 0), new Vector3Int(0, 2, 0) }, 1, 2), Is.EqualTo(2));
                Assert.That(Staged(tx).Count, Is.EqualTo(1)); AssertAuxiliaryIndexes(tx);
                Assert.That(tx.TryCommit(out var result, out string error), Is.True, error); Assert.That(result.Added, Is.EqualTo(2));
            }
            f.Env.Transaction = null; var bottom = f.World.Blocks.Values.Single();
            var second = f.Seed(6, 2); var third = f.Seed(6, 1); var fourth = f.Seed(6, 2);
            using var restack = f.Begin(); restack.Destroy(new[] { bottom.Handle }, 2);
            Assert.That(restack.TryCommit(out var removed, out string failure), Is.True, failure);
            Assert.That(removed.Added, Is.Zero); Assert.That(removed.RemovedEnemy, Is.EqualTo(1));
            Assert.That(f.World.Count, Is.Zero); Assert.That(f.World.LastConvertedCount, Is.EqualTo(3));
            Assert.That(f.World.PinkCount, Is.Zero); Assert.That(f.World.BlueCount, Is.EqualTo(1));
            foreach (var old in new[] { bottom, second, third, fourth }) Assert.That(f.World.TryGet(old.Handle, out _), Is.False);
            Assert.That(ActualTarget(restack, new Vector3Int(0, 6, 0), 0), Is.Zero); AssertAuxiliaryIndexes(restack);
        }

        [Test] public void UnknownNonMonotoneEnvironmentMustRetainDistantUpperSupport()
        {
            var f = new Fixture(new[] { 0f, 1.2f }); f.Color(level: 1);
            f.Env.Override = from => from.y >= 10 ? Support(0, 0, from.x, from.z) : Support(1, 1.2f, from.x, from.z);
            for (int i = 0; i < 4; i++) f.Seed(7);
            using var tx = f.Begin(); var from = new Vector3Int(0, 10, 0);
            Assert.That(ActualTarget(tx, from, 0), Is.EqualTo(8));
            Assert.That(ActualTarget(tx, from, 0), Is.EqualTo(LegacyTarget(tx, from, 0, out _)));
            Assert.That(tx.Build(new[] { from }, 1, 1), Is.Zero, "A four-cell cropped search would incorrectly build below.");
            Assert.That(tx.TryCommit(out _, out string error), Is.True, error); Assert.That(f.World.Count, Is.EqualTo(4));
        }

        [Test] public void RejectedPlacementAndPaintDoNotCreateAuxiliaryWritesOrConsumeIds()
        {
            var f = new Fixture(); f.Color(); var before = f.World.Capture(true);
            using (var tx = f.Begin())
            {
                f.Env.DenyPlacement = true;
                Assert.That(tx.Build(new[] { new Vector3Int(0, 6, 0), new Vector3Int(0, 5, 0) }, 1, 2), Is.Zero);
                Assert.That(f.Env.PlacementCalls, Is.EqualTo(2)); AssertAuxiliaryIndexes(tx);
                Assert.That(CellWrites(tx), Is.Empty); Assert.That(Staged(tx), Is.Empty);
                Assert.That(tx.TryCommit(out _, out string error), Is.True, error); SameSnapshot(f.World, before);
            }
            var blank = new Fixture(); using var paint = blank.Begin(); blank.Env.DenyPaint = true;
            Assert.That(paint.Build(new[] { new Vector3Int(0, 6, 0) }, 1, 1), Is.Zero);
            Assert.That(paint.TryCommit(out _, out string failure), Is.True, failure);
            Assert.That(blank.World.NextId, Is.EqualTo(1)); Assert.That(blank.World.PinkCount, Is.Zero);
            AssertAuxiliaryIndexes(paint);
        }

        [Test] public void IndexFollowsAnIdentityMovedBetweenColumnsAndRestoredAtSameCell()
        {
            var f = new Fixture(); f.Color(); var old = f.Seed(3); using var tx = f.Begin();
            // Directly exercise the two private staging primitives, never mutate
            // auxiliary dictionaries. The live gameplay currently moves only y.
            RemoveState.Invoke(tx, new object[] { old });
            var moved = old; moved.Cell = new Vector3Int(-17, 0, 16); moved.Support = Support(0, 0, -17, 16);
            StageState.Invoke(tx, new object[] { moved, false });
            AssertColumn(tx, Vector2Int.zero); AssertColumn(tx, new Vector2Int(-17, 16)); AssertAuxiliaryIndexes(tx);
            Assert.That(ActualTarget(tx, new Vector3Int(-17, 5, 16), 0), Is.EqualTo(1));
            RemoveState.Invoke(tx, new object[] { moved });
            StageState.Invoke(tx, new object[] { old, false });
            AssertColumn(tx, Vector2Int.zero); AssertColumn(tx, new Vector2Int(-17, 16)); AssertAuxiliaryIndexes(tx);
            Assert.That(ActualTarget(tx, new Vector3Int(0, 5, 0), 0), Is.EqualTo(1));
            // No commit: direct primitive use intentionally does not cancel Deleted.
        }

        [TestCase(1)] [TestCase(16)] [TestCase(64)] [TestCase(400)]
        public void MultiColumnUnderCutPreservesIdentityAndVisitsOnlyLocalStagedEntries(int count)
        {
            var f = new Fixture(); var columns = Enumerable.Range(0, count).Select(i => new Vector2Int(i % 20 - 10, i / 20 - 10)).ToArray();
            foreach (var c in columns) f.Color(1, c.x, c.y);
            var seedCells = columns.SelectMany(c => Enumerable.Range(0, 3).Select(y => new Vector3Int(c.x, y, c.y))).ToArray();
            Assert.That(f.World.Build(seedCells, 1, seedCells.Length).Added, Is.EqualTo(seedCells.Length));
            var before = f.World.Blocks.Values.ToDictionary(s => s.Handle.Id);
            using var tx = f.Begin();
            var tops = columns.Select(c => new Vector3Int(c.x, 6, c.y)).ToArray();
            Assert.That(tx.Build(tops, 1, count), Is.EqualTo(count));
            var added = Staged(tx).Values.ToDictionary(s => s.Handle.Id);
            var removed = before.Values.Where(s => s.Cell.y == 0).Select(s => s.Handle).ToArray();
            tx.Destroy(removed, 2); AssertAuxiliaryIndexes(tx);
            foreach (var c in columns) AssertColumn(tx, c);
            long start = Counter(tx, "ColumnStagedEntriesVisited");
            Assert.That(tx.TryCommit(out var result, out string error), Is.True, error);
            Assert.That(Counter(tx, "ColumnStagedEntriesVisited") - start, Is.EqualTo(count));
            Assert.That(2L * count * count - count, Is.GreaterThanOrEqualTo(count), "Frozen global-scan visits for this fixture.");
            Assert.That(result.Added, Is.EqualTo(count)); Assert.That(result.RemovedEnemy, Is.EqualTo(count));
            Assert.That(f.World.Count, Is.EqualTo(3 * count)); Assert.That(f.World.LastRelocatedCount, Is.EqualTo(3 * count));
            Assert.That(f.World.LastConvertedCount, Is.Zero); Assert.That(f.World.NextId, Is.EqualTo((ulong)(4 * count + 1)));
            var expected = before.Values.Where(s => s.Cell.y > 0).Concat(added.Values).ToArray();
            foreach (var old in expected)
            {
                Assert.That(f.World.TryGet(old.Handle, out var actual), Is.True);
                Assert.That(actual.Cell, Is.EqualTo(old.Cell + Vector3Int.down)); Assert.That(actual.Owner, Is.EqualTo(old.Owner));
                Assert.That(actual.Support, Is.EqualTo(old.Support));
            }
            foreach (var handle in removed) Assert.That(f.World.TryGet(handle, out _), Is.False);
            AssertAuxiliaryIndexes(tx);
        }

        [Test] public void RejectedCommitLeavesPublishedOccupancyAndSnapshotsUnchanged()
        {
            var f = new Fixture(new[] { -4.8f }); f.Color(); var bottom = f.Seed(-13); f.Seed(-13); f.Seed(-13);
            var before = f.World.Capture(true); using (var tx = f.Begin())
            {
                tx.Destroy(new[] { bottom.Handle }, 2); f.Env.RejectGuard = true;
                Assert.That(tx.TryCommit(out _, out _), Is.False); SameSnapshot(f.World, before); AssertAuxiliaryIndexes(tx);
            }
            f.Env.RejectGuard = false; using var next = f.Begin();
            Assert.That(ActualTarget(next, new Vector3Int(0, 5, 0), -16), Is.EqualTo(-13));
            Assert.That(ActualTarget(next, new Vector3Int(0, 5, 0), -16), Is.EqualTo(LegacyTarget(next, new Vector3Int(0, 5, 0), -16, out _)));
            AssertAuxiliaryIndexes(next);
        }

        [Test] public void RecoveryFailureAfterApplyRestoresStoragePaintIdsAndTheNextTransaction()
        {
            var f = new Fixture(new[] { -4.8f }); f.Color(); f.Color(x: 1);
            var bottom = f.Seed(-13); f.Seed(-13); f.Seed(-13);
            var before = f.World.Capture(true); var oldStorage = f.World.Storage;
            int commits = 0; f.World.Committed += _ => commits++;
            int oldRecovery = f.Env.RecoveryCalls; f.Env.ThrowRecovery = true;
            using (var tx = f.Begin())
            {
                tx.Destroy(new[] { bottom.Handle }, 2);
                Assert.That(tx.Build(new[] { new Vector3Int(1, -13, 0) }, 1, 1), Is.EqualTo(1));
                Assert.That(tx.Paint(new[] { new BlockSurfaceKey(11, 2, 0) }, 2, 1), Is.EqualTo(1));
                Assert.That(tx.TryCommit(out _, out string error), Is.False);
                Assert.That(error, Does.Contain("fixture recovery failed after publication"));
                AssertAuxiliaryIndexes(tx);
            }
            Assert.That(f.Env.RecoveryCalls, Is.EqualTo(oldRecovery + 1)); Assert.That(f.Env.RollbackCalls, Is.EqualTo(1));
            Assert.That(commits, Is.Zero); Assert.That(f.World.Storage, Is.SameAs(oldStorage)); SameSnapshot(f.World, before);
            f.Env.ThrowRecovery = false; using var next = f.Begin();
            foreach (int x in new[] { 0, 1, 2 })
            {
                var from = new Vector3Int(x, 5, 0);
                Assert.That(ActualTarget(next, from, -16), Is.EqualTo(LegacyTarget(next, from, -16, out _)));
                AssertColumn(next, new Vector2Int(x, 0));
            }
            Assert.That(ActualTarget(next, new Vector3Int(0, 5, 0), -16), Is.EqualTo(-13));
            Assert.That(ActualTarget(next, new Vector3Int(1, 5, 0), -16), Is.EqualTo(-16));
            AssertAuxiliaryIndexes(next);
        }
    }
}
#endif
