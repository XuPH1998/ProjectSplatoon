#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridWorldTests
    {
        sealed class Environment : IBlockFixedGridEnvironment, IBlockFixedGridCommitGuard,
            IBlockFixedGridRecoveryRollback, IBlockFixedGridPaintAdmission
        {
            internal float Height;
            internal int MapId = -7;
            internal bool MissingSupport, InvalidPaintKey, DenyBuild, DenyRelocation, DenyPaint, RejectGuard, ThrowRecovery;
            internal int RecoveryCount, RollbackCount;
            internal Func<Vector3Int, BlockFixedGridMapSupport> SupportOverride;
            internal Action<BlockFixedGridCommitPreview> Preview;
            internal Action Recovery;
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            {
                support = SupportOverride != null ? SupportOverride(cell) : new BlockFixedGridMapSupport(MapId, Height,
                    new BlockSurfaceKey(InvalidPaintKey ? 999 : 11, cell.x, cell.z));
                return !MissingSupport;
            }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase, bool relocating, out string reason)
            { reason = "fixture placement rejection"; return relocating ? !DenyRelocation : !DenyBuild; }
            public bool Visible(Vector3 from, Vector3 to) => true;
            public bool PaintAllowed(BlockSurfaceKey key, out string reason) { reason = "fixture paint rejection"; return !DenyPaint; }
            public bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason)
            { Preview?.Invoke(preview); reason = "fixture guard rejection"; return !RejectGuard; }
            public void CommitPreparedRecovery()
            {
                RecoveryCount++; Recovery?.Invoke();
                if (ThrowRecovery) throw new InvalidOperationException("fixture recovery failure");
            }
            public void RollbackPreparedRecovery() { RollbackCount++; RecoveryCount--; }
        }
        sealed class Fixture
        {
            internal readonly Environment Env;
            internal readonly BlockFixedGridWorld World;
            internal Fixture(float height = 0)
            {
                Env = new Environment { Height = height };
                var surface = new BlockSurfaceWorld(7);
                var squares = new List<Vector2Int>();
                for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) squares.Add(new Vector2Int(x, z));
                surface.RegisterFace(11, Vector3.up * height, Vector3.right, Vector3.forward, squares);
                surface.RegisterFace(12, Vector3.up * 1.8f, Vector3.right, Vector3.forward, squares);
                World = new BlockFixedGridWorld(new BlockWorld(new Vector2(30, 30), 7, -8), surface, Env);
            }
            internal void Color(byte team = 1, int x = 0, int z = 0, int face = 11)
                => World.Surfaces.TrySet(new BlockSurfaceKey(face, x, z), team, out _);
            internal BlockFixedGridState Build(int y, byte team = 1, int x = 0, int z = 0)
            {
                var result = World.Build(new[] { new Vector3Int(x, y, z) }, team, 1);
                Assert.That(result.Added, Is.EqualTo(1), World.LastFailure);
                return World.Blocks.Values.Where(s => s.Cell.x == x && s.Cell.z == z).OrderByDescending(s => s.Cell.y).First();
            }
        }
        static BlockSurfaceKey Key(int x = 0, int z = 0, int face = 11) => new(face, x, z);
        static byte Owner(BlockFixedGridWorld world, BlockSurfaceKey key)
        { Assert.That(world.Surfaces.TryGet(key, out var cell), Is.True); return cell.Owner; }
        static void SameSnapshot(BlockFixedGridWorld world, BlockFixedGridSnapshot before)
        {
            var after = world.Capture(true);
            Assert.That(after.Epoch, Is.EqualTo(before.Epoch)); Assert.That(after.Round, Is.EqualTo(before.Round));
            Assert.That(after.Revision, Is.EqualTo(before.Revision)); Assert.That(after.NextId, Is.EqualTo(before.NextId));
            Assert.That(after.Frozen, Is.EqualTo(before.Frozen)); Assert.That(after.Upserts, Is.EqualTo(before.Upserts));
            Assert.That(after.SurfaceBytes, Is.EqualTo(before.SurfaceBytes));
        }

        [TestCase(.3f, 1)] [TestCase(.6f, 2)] [TestCase(.9f, 3)]
        [TestCase(.59999996f, 2)] [TestCase(-.30000004f, -1)] [TestCase(-.6f, -2)]
        [TestCase(.17f, 0)] [TestCase(.65f, 2)] [TestCase(-.17f, -1)]
        public void GlobalBaseYUsesGridPlaneToleranceAndFloorsOtherHeights(float height, int expected)
        {
            Assert.That(BlockFixedGridMapSupport.GridBaseY(height), Is.EqualTo(expected));
            var f = new Fixture(height); f.Color();
            var state = f.Build(expected + 3);
            Assert.That(state.Cell.y, Is.EqualTo(expected)); Assert.That(state.Support.BaseCellY, Is.EqualTo(expected));
            Assert.That(BlockFixedGridWorld.GetBounds(state).min.y, Is.EqualTo(expected * .30f).Within(.000001));
        }

        [TestCase(0f)] [TestCase(.17f)] [TestCase(.65f)] [TestCase(-.17f)] [TestCase(-.65f)]
        public void LocalMapBaseAllowsExactlyFourGlobalLayers(float height)
        {
            var f = new Fixture(height); f.Color(); int first = BlockFixedGridMapSupport.GridBaseY(height);
            for (int i = 0; i < 4; i++) Assert.That(f.Build(first + i).Cell.y, Is.EqualTo(first + i));
            Assert.That(f.World.Build(new[] { new Vector3Int(0, first + 4, 0) }, 1, 1).Added, Is.Zero);
            Assert.That(f.World.Count, Is.EqualTo(4));
            Assert.That((f.World.Blocks.Values.Max(s => s.Cell.y) + 1) * .30f - height, Is.LessThanOrEqualTo(1.20001f));
        }

        [Test] public void BlankAndEnemyMapPaintWhileOwnMapBuildsWithoutDoubleCharging()
        {
            var f = new Fixture();
            var first = f.World.Build(new[] { new Vector3Int(0, 3, 0) }, 1, 1);
            Assert.That(first.Added, Is.EqualTo(1)); Assert.That(f.World.Count, Is.Zero); Assert.That(f.World.PinkCount, Is.EqualTo(1));
            var replaced = f.World.Paint(new[] { Key(), Key() }, 2, 2);
            Assert.That(replaced.Added, Is.EqualTo(1)); Assert.That(replaced.RemovedEnemy, Is.EqualTo(1));
            Assert.That(f.World.PinkCount, Is.Zero); Assert.That(f.World.BlueCount, Is.EqualTo(1));
            var built = f.Build(3, 2); Assert.That(built.Cell, Is.EqualTo(Vector3Int.zero));
            Assert.That(f.World.BlueCount, Is.EqualTo(2)); Assert.That(f.World.NextId, Is.EqualTo(2));
        }

        [Test] public void CandidateOrderQuotaAndDuplicateAddressesArePreserved()
        {
            var f = new Fixture(); f.Color(x: 1); f.Color(x: -1); f.Color();
            var input = new[] { new Vector3Int(1, 3, 0), new Vector3Int(1, 3, 0), new Vector3Int(-1, 3, 0), new Vector3Int(0, 3, 0) };
            Assert.That(f.World.Build(input, 1, 2).Added, Is.EqualTo(2));
            Assert.That(f.World.TryGet(new Vector3Int(1, 0, 0), out var a), Is.True);
            Assert.That(f.World.TryGet(new Vector3Int(-1, 0, 0), out var b), Is.True);
            Assert.That(a.Handle.Id, Is.LessThan(b.Handle.Id)); Assert.That(f.World.TryGet(Vector3Int.zero, out _), Is.False);
        }

        // A finite lazy source whose next request after the last element is a fault.
        // It distinguishes exact quota consumption from foreach's extra MoveNext.
        sealed class CandidateStream : IEnumerable<Vector3Int>
        {
            readonly Vector3Int[] _cells;
            internal int Enumerators, MoveNextCalls, Disposals;
            internal readonly InvalidOperationException TailFailure = new("Unexpected candidate tail read.");
            internal CandidateStream(params Vector3Int[] cells) { _cells = cells; }
            public IEnumerator<Vector3Int> GetEnumerator() { Enumerators++; return new Cursor(this); }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            sealed class Cursor : IEnumerator<Vector3Int>
            {
                readonly CandidateStream _source;
                int _index = -1;
                internal Cursor(CandidateStream source) { _source = source; }
                public Vector3Int Current => _source._cells[_index];
                object System.Collections.IEnumerator.Current => Current;
                public bool MoveNext()
                {
                    _source.MoveNextCalls++;
                    if (++_index == _source._cells.Length) throw _source.TailFailure;
                    return true;
                }
                public void Reset() => throw new NotSupportedException();
                public void Dispose() { _source.Disposals++; }
            }
        }

        [Test] public void ExactBuildQuotaDoesNotReadTheNextLazyCandidateAndDisposes()
        {
            var f = new Fixture(); f.Color();
            var input = new CandidateStream(new Vector3Int(0, 3, 0));
            var result = f.World.Build(input, 1, 1);
            Assert.That(result.Added, Is.EqualTo(1)); Assert.That(f.World.Count, Is.EqualTo(1));
            Assert.That(f.World.TryGet(Vector3Int.zero, out var block), Is.True);
            Assert.That(block.Handle.Id, Is.EqualTo(1)); Assert.That(f.World.NextId, Is.EqualTo(2));
            Assert.That(input.Enumerators, Is.EqualTo(1)); Assert.That(input.MoveNextCalls, Is.EqualTo(1));
            Assert.That(input.Disposals, Is.EqualTo(1));
        }

        [Test] public void LazyRejectedAndDuplicateCandidatesKeepBudgetOrderAndNextId()
        {
            var f = new Fixture(); f.Color(); f.Color(x: 1); f.Color(x: -1);
            var existing = f.Build(0); ulong firstId = f.World.NextId;
            var a = new Vector3Int(1, 3, 0); var b = new Vector3Int(-1, 3, 0);
            var input = new CandidateStream(existing.Cell, new Vector3Int(1000000, 3, 0), a, a, b);
            var result = f.World.Build(input, 1, 2);
            Assert.That(result.Added, Is.EqualTo(2)); Assert.That(result.RemovedEnemy, Is.Zero);
            Assert.That(f.World.Count, Is.EqualTo(3)); Assert.That(f.World.NextId, Is.EqualTo(firstId + 2));
            Assert.That(f.World.TryGet(existing.Handle, out var retained), Is.True); Assert.That(retained, Is.EqualTo(existing));
            Assert.That(f.World.TryGet(new Vector3Int(1, 0, 0), out var first), Is.True);
            Assert.That(f.World.TryGet(new Vector3Int(-1, 0, 0), out var second), Is.True);
            Assert.That(first.Handle.Id, Is.EqualTo(firstId)); Assert.That(second.Handle.Id, Is.EqualTo(firstId + 1));
            Assert.That(input.Enumerators, Is.EqualTo(1)); Assert.That(input.MoveNextCalls, Is.EqualTo(5));
            Assert.That(input.Disposals, Is.EqualTo(1));
        }

        [Test] public void LazyCandidateFailureDisposesAndDoesNotCommitStagedIdentity()
        {
            var f = new Fixture(); f.Color(); var before = f.World.Capture(true);
            var input = new CandidateStream(new Vector3Int(0, 3, 0));
            var failure = Assert.Throws<InvalidOperationException>(() => f.World.Build(input, 1, 2));
            Assert.That(failure, Is.SameAs(input.TailFailure));
            Assert.That(input.Enumerators, Is.EqualTo(1)); Assert.That(input.MoveNextCalls, Is.EqualTo(2));
            Assert.That(input.Disposals, Is.EqualTo(1)); SameSnapshot(f.World, before);
            Assert.That(f.Build(0).Handle.Id, Is.EqualTo(before.NextId));
        }

        [Test] public void FirstInvalidMapPlaneBlocksPlacementWithoutSearchingThroughIt()
        {
            var f = new Fixture(); f.Env.InvalidPaintKey = true;
            Assert.That(f.World.Build(new[] { new Vector3Int(0, 3, 0) }, 1, 1).Added, Is.Zero);
            Assert.That(f.World.Count, Is.Zero); Assert.That(f.World.Surfaces.Revision, Is.Zero);
            f.Env.InvalidPaintKey = false; f.Env.Height = .9f;
            Assert.That(f.World.Build(new[] { new Vector3Int(0, 2, 0) }, 1, 1).Added, Is.Zero, "A map plane cannot lift a candidate upward.");
        }

        [Test, Timeout(1000)] public void MapSupportBelowWorldCannotCauseAnUnboundedLandingScan()
        {
            var f = new Fixture(-10000000); f.Color();
            Assert.That(f.World.Build(new[] { Vector3Int.zero }, 1, 1).Added, Is.Zero);
            Assert.That(f.World.Count, Is.Zero); Assert.That(f.World.NextId, Is.EqualTo(1));
        }

        [Test] public void EnemyFixedBlockCanSupportFriendlyBlock()
        {
            var f = new Fixture(); f.Color(); f.Build(0);
            var blue = f.Build(3, 2); Assert.That(blue.Cell.y, Is.EqualTo(1));
            Assert.That(Owner(f.World, Key()), Is.EqualTo(1));
        }

        [Test] public void LandingReadsEarlierAddsAndDeletesInTheSameTransaction()
        {
            var f = new Fixture(); f.Color(); f.Color(x: 1);
            var oldBase = f.Build(0); var unchangedTop = f.Build(1);
            using var tx = f.World.BeginMutation();
            Assert.That(tx.Destroy(new[] { oldBase.Handle }, 2, 1, 0).RemovedEnemy, Is.EqualTo(1));
            Assert.That(tx.Build(new[] { new Vector3Int(1, 3, 0), Vector3Int.zero,
                new Vector3Int(1, 2, 0), new Vector3Int(0, 4, 0) }, 1, 4), Is.EqualTo(4));
            Assert.That(tx.TryCommit(out var result, out var error), Is.True, error);
            Assert.That(result.Added, Is.EqualTo(4)); Assert.That(result.RemovedEnemy, Is.EqualTo(1));
            Assert.That(f.World.TryGet(oldBase.Handle, out _), Is.False);
            Assert.That(f.World.TryGet(unchangedTop.Handle, out var retained), Is.True);
            Assert.That(retained, Is.EqualTo(unchangedTop));
            foreach (var cell in new[] { Vector3Int.zero, Vector3Int.up, Vector3Int.up * 2,
                new Vector3Int(1, 0, 0), new Vector3Int(1, 1, 0) })
                Assert.That(f.World.TryGet(cell, out _), Is.True, cell.ToString());
            Assert.That(f.World.Count, Is.EqualTo(5)); Assert.That(f.World.Storage.PinkCount, Is.EqualTo(5));
        }

        [Test] public void RemovingSupportRestacksAffectedColumnAndKeepsStableIdentity()
        {
            var f = new Fixture(); f.Color(); f.Color(x: 1);
            var bottom = f.Build(0); var middle = f.Build(1); var top = f.Build(2); var other = f.Build(0, x: 1);
            var removed = f.World.Destroy(new[] { bottom.Handle, bottom.Handle }, 2, 1, 0);
            Assert.That(removed.RemovedEnemy, Is.EqualTo(1)); Assert.That(removed.Added, Is.Zero);
            Assert.That(f.World.TryGet(middle.Handle, out var newMiddle), Is.True); Assert.That(newMiddle.Cell.y, Is.Zero);
            Assert.That(f.World.TryGet(top.Handle, out var newTop), Is.True); Assert.That(newTop.Cell.y, Is.EqualTo(1));
            Assert.That(newMiddle.Revision, Is.GreaterThan(middle.Revision)); Assert.That(newMiddle.Handle, Is.EqualTo(middle.Handle));
            Assert.That(f.World.TryGet(other.Handle, out var unchanged), Is.True); Assert.That(unchanged, Is.EqualTo(other));
            Assert.That(f.World.LastRelocatedCount, Is.EqualTo(2)); Assert.That(f.World.Count, Is.EqualTo(3));
            Assert.That(f.World.Storage.PinkCount, Is.EqualTo(3));
        }

        [Test] public void BridgeAndUnderpassShareXZButRetainIndependentMapBases()
        {
            var f = new Fixture();
            f.Env.SupportOverride = c => new BlockFixedGridMapSupport(c.y >= 6 ? -12 : -11, c.y >= 6 ? 1.8f : 0,
                Key(face: c.y >= 6 ? 12 : 11));
            f.Color(); f.Color(face: 12);
            var low = f.Build(0); var upperLow = f.Build(1); var bridge = f.Build(6); var upperBridge = f.Build(7);
            f.World.Destroy(new[] { low.Handle }, 2, 1, 0);
            f.World.TryGet(upperLow.Handle, out var moved); f.World.TryGet(bridge.Handle, out var bridgeAfter); f.World.TryGet(upperBridge.Handle, out var upperAfter);
            Assert.That(moved.Cell.y, Is.Zero); Assert.That(bridgeAfter, Is.EqualTo(bridge)); Assert.That(upperAfter, Is.EqualTo(upperBridge));
        }

        [Test] public void RestackingOnBlankAndEnemyPaintConvertsWithoutNewBudgetOrCharge()
        {
            var f = new Fixture(); f.Color(); var bottom = f.Build(0); var middle = f.Build(1); var top = f.Build(2, 2);
            f.World.Paint(new[] { Key() }, 2, 1);
            var result = f.World.Destroy(new[] { bottom.Handle }, 2, 1, 0);
            Assert.That(result.RemovedEnemy, Is.EqualTo(1)); Assert.That(result.RemovedFriendly, Is.Zero); Assert.That(result.Added, Is.Zero);
            Assert.That(f.World.TryGet(middle.Handle, out _), Is.False); Assert.That(f.World.TryGet(top.Handle, out _), Is.False);
            Assert.That(f.World.LastConvertedCount, Is.EqualTo(2)); Assert.That(f.World.Count, Is.Zero);
            Assert.That(f.World.PinkCount, Is.Zero); Assert.That(f.World.BlueCount, Is.EqualTo(1));
            Assert.That(Owner(f.World, Key()), Is.EqualTo(2));
        }

        [Test] public void BlankMapConversionKeepsTotalCountAndDoesNotRepeatAdded()
        {
            var f = new Fixture(); f.Color(); var bottom = f.Build(0); var upper = f.Build(1);
            using (var tx = f.World.BeginMutation()) { tx.RemovePaint(new[] { Key() }, 1); Assert.That(tx.TryCommit(out _, out _), Is.True); }
            var result = f.World.Destroy(new[] { bottom.Handle }, 2, 1, 0);
            Assert.That(result.RemovedEnemy, Is.EqualTo(1)); Assert.That(result.Added, Is.Zero);
            Assert.That(f.World.TryGet(upper.Handle, out _), Is.False); Assert.That(f.World.LastConvertedCount, Is.EqualTo(1));
            Assert.That(f.World.PinkCount, Is.EqualTo(1)); Assert.That(Owner(f.World, Key()), Is.EqualTo(1));
        }

        [Test] public void BuildPlacementOrPaintAdmissionRejectionDoesNotSpendQuota()
        {
            var f = new Fixture(); f.Color(); f.Env.DenyBuild = true;
            Assert.That(f.World.Build(new[] { Vector3Int.zero }, 1, 1).Added, Is.Zero); Assert.That(f.World.NextId, Is.EqualTo(1));
            f.Env.DenyBuild = false; f.Env.DenyPaint = true;
            Assert.That(f.World.Paint(new[] { Key(1) }, 1, 1).Added, Is.Zero);
            Assert.That(f.World.Build(new[] { new Vector3Int(1, 3, 0) }, 1, 1).Added, Is.Zero);
            Assert.That(f.World.Surfaces.Revision, Is.EqualTo(1));
        }

        [TestCase(true)] [TestCase(false)]
        public void UnsafeRelocationOrConversionRollsBackWholeDestroy(bool denyRelocation)
        {
            var f = new Fixture(); f.Color(); var bottom = f.Build(0); f.Build(1);
            if (denyRelocation) f.Env.DenyRelocation = true;
            else { f.World.Paint(new[] { Key() }, 2, 1); f.Env.DenyPaint = true; }
            var before = f.World.Capture(true); var oldStorage = f.World.Storage;
            using var tx = f.World.BeginMutation(); tx.Destroy(new[] { bottom.Handle }, 2, 1, 0);
            Assert.That(tx.TryCommit(out var result, out var error), Is.False); Assert.That(error, Is.Not.Null);
            Assert.That(result.RemovedEnemy, Is.Zero); Assert.That(f.World.Storage, Is.SameAs(oldStorage)); SameSnapshot(f.World, before);
        }

        [Test] public void RejectedSafetyPreflightLeavesDataIdentityAndPaintUnchanged()
        {
            var f = new Fixture(); f.Color(); var before = f.World.Capture(true); f.Env.RejectGuard = true;
            using var tx = f.World.BeginMutation(); tx.Build(new[] { Vector3Int.zero }, 1, 1); tx.Paint(new[] { Key(1) }, 2, 1);
            Assert.That(tx.TryCommit(out _, out _), Is.False); SameSnapshot(f.World, before); Assert.That(f.Env.RecoveryCount, Is.Zero);
        }

        [Test] public void RecoveryExceptionRestoresPaintGridIndicesIdsJournalAndActor()
        {
            var f = new Fixture(); f.Color(); f.Color(x: 1); var bottom = f.Build(0); var upper = f.Build(1); var before = f.World.Capture(true);
            var oldStorage = f.World.Storage; int commits = 0; f.World.Committed += _ => commits++; f.Env.ThrowRecovery = true;
            using (var tx = f.World.BeginMutation())
            {
                tx.Destroy(new[] { bottom.Handle }, 2, 1, 0); tx.Paint(new[] { Key(1) }, 2, 1);
                Assert.That(tx.TryCommit(out _, out _), Is.False);
            }
            SameSnapshot(f.World, before); Assert.That(f.World.Storage, Is.SameAs(oldStorage)); Assert.That(commits, Is.Zero);
            Assert.That(f.Env.RollbackCount, Is.EqualTo(1)); Assert.That(f.World.TryGet(upper.Cell, out var restored), Is.True);
            Assert.That(restored.Handle, Is.EqualTo(upper.Handle));
            Assert.That(f.World.Capture(false, before.Revision, before.SurfaceRevision).Upserts, Is.Empty);
            f.Env.ThrowRecovery = false; var next = f.Build(0, x: 1);
            Assert.That(next.Handle.Id, Is.EqualTo(before.NextId), "Failed commits must not consume identities.");
        }

        [Test] public void RecoveryAndObserversSeePaintStorageAndIdentityAtSameRevision()
        {
            var f = new Fixture(); f.Color(); bool recovery = false, observed = false;
            f.Env.Preview = p => { Assert.That(p.Storage.TryGet(Vector3Int.zero, out _), Is.True); Assert.That(Owner(f.World, Key(1)), Is.Zero); };
            f.Env.Recovery = () => { recovery = true; Assert.That(f.World.TryGet(Vector3Int.zero, out _), Is.True); Assert.That(Owner(f.World, Key(1)), Is.EqualTo(2)); };
            f.World.Committed += c =>
            {
                observed = true; Assert.That(recovery, Is.True); Assert.That(c.Revision, Is.EqualTo(f.World.Storage.Revision));
                Assert.That(c.ChangedPaintKeys, Does.Contain(Key(1))); Assert.That(c.ChangedCells, Does.Contain(Vector3Int.zero));
            };
            using var tx = f.World.BeginMutation(); tx.Build(new[] { Vector3Int.zero }, 1, 1); tx.Paint(new[] { Key(1) }, 2, 1);
            Assert.That(tx.TryCommit(out _, out var error), Is.True, error); Assert.That(observed, Is.True); Assert.That(f.World.LastObserverException, Is.Null);
        }

        [Test] public void StaleOrConsumedTransactionsCannotPublish()
        {
            var f = new Fixture(); f.Color(); using var a = f.World.BeginMutation(); using var b = f.World.BeginMutation();
            a.Build(new[] { Vector3Int.zero }, 1, 1); b.Paint(new[] { Key(1) }, 2, 1);
            Assert.That(a.TryCommit(out _, out _), Is.True); Assert.That(b.TryCommit(out _, out _), Is.False);
            Assert.That(a.TryCommit(out _, out _), Is.False); Assert.That(Owner(f.World, Key(1)), Is.Zero);
        }

        [Test] public void FullAndDeltaImportsArePreparedWithoutPublishingEitherWorld()
        {
            var host = new Fixture(); host.Color(); var bottom = host.Build(0); var upper = host.Build(1); var client = new Fixture();
            var full = host.World.Capture(true); var oldStorage = client.World.Storage; var oldSurface = client.World.Surfaces;
            Assert.That(client.World.TryPrepareImport(full, out var prepared, out var error), Is.True, error);
            Assert.That(client.World.Storage, Is.SameAs(oldStorage)); Assert.That(client.World.Surfaces, Is.SameAs(oldSurface)); Assert.That(client.World.Count, Is.Zero);
            full.Upserts[0] = default; full.SurfaceBytes[0] = 0;
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.Count, Is.EqualTo(2)); Assert.That(client.World.PinkCount, Is.EqualTo(3));
            Assert.That(client.World.CommitImport(prepared), Is.False);
            var sameRoundNewEpoch = new Fixture().World.Capture(true); sameRoundNewEpoch.Epoch = client.World.Epoch + 1;
            var installed = client.World.Capture(true);
            Assert.That(client.World.TryPrepareImport(sameRoundNewEpoch, out var regressedSurface, out _), Is.False);
            Assert.That(regressedSurface, Is.Null); SameSnapshot(client.World, installed);
            uint revision = host.World.Revision, surfaceRevision = host.World.Surfaces.Revision;
            host.World.Destroy(new[] { bottom.Handle }, 2, 1, 0); host.World.Paint(new[] { Key(1) }, 2, 1);
            var delta = host.World.Capture(false, revision, surfaceRevision);
            Assert.That(client.World.TryPrepareImport(delta, out prepared, out error), Is.True, error);
            Assert.That(client.World.Count, Is.EqualTo(2)); Assert.That(Owner(client.World, Key(1)), Is.Zero);
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.Count, Is.EqualTo(1));
            client.World.TryGet(upper.Handle, out var state); Assert.That(state.Cell.y, Is.Zero);
            Assert.That(Owner(client.World, Key(1)), Is.EqualTo(2)); Assert.That(client.World.PinkCount, Is.EqualTo(host.World.PinkCount));
        }

        [Test] public void InvalidGridOrPaintSnapshotCannotInstallEitherHalf()
        {
            var host = new Fixture(); host.Color(); host.Build(0); var client = new Fixture(); var before = client.World.Capture(true);
            var badGrid = host.World.Capture(true); var bad = badGrid.Upserts[0]; bad.Cell.y = 3; badGrid.Upserts[0] = bad;
            Assert.That(client.World.TryPrepareImport(badGrid, out _, out _), Is.False); SameSnapshot(client.World, before);
            var badPaint = host.World.Capture(true); badPaint.SurfaceBytes[0] ^= 0xff;
            Assert.That(client.World.TryPrepareImport(badPaint, out _, out _), Is.False); SameSnapshot(client.World, before);
        }

        [Test] public void PreparedImportExpiresWhenLocalBaselineChanges()
        {
            var host = new Fixture(); host.Color(); host.Build(0); var client = new Fixture();
            Assert.That(client.World.TryPrepareImport(host.World.Capture(true), out var prepared, out _), Is.True);
            client.World.Paint(new[] { Key(1) }, 2, 1);
            Assert.That(client.World.CommitImport(prepared), Is.False); Assert.That(client.World.Count, Is.Zero);
        }

        [Test] public void ImportObserverSeesPairedPaintAndGridAndClearDirtiesOldPaint()
        {
            var host = new Fixture(); host.Color(); host.Build(0); var client = new Fixture(); bool observed = false;
            client.World.Committed += c =>
            {
                observed = true; Assert.That(client.World.Count, Is.EqualTo(1)); Assert.That(Owner(client.World, Key()), Is.EqualTo(1));
                Assert.That(c.ChangedPaintKeys, Does.Contain(Key()));
            };
            Assert.That(client.World.TryPrepareImport(host.World.Capture(true), out var prepared, out _), Is.True);
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(observed, Is.True); Assert.That(client.World.LastObserverException, Is.Null);
            var old = host.World.Blocks.Values.Single(); BlockFixedGridCommit clear = default; host.World.Committed += c => clear = c;
            host.World.Clear(8); Assert.That(host.World.Count, Is.Zero); Assert.That(host.World.PinkCount, Is.Zero);
            Assert.That(host.World.TryGet(old.Handle, out _), Is.False); Assert.That(clear.ChangedPaintKeys, Does.Contain(Key()));
            Assert.That(clear.ChangedCells, Does.Contain(old.Cell));
        }

        [Test] public void FreezeDeltaIsReliableAndNewEpochInvalidatesOldHandles()
        {
            var host = new Fixture(); host.Color(); var old = host.Build(0); var client = new Fixture();
            client.World.TryPrepareImport(host.World.Capture(true), out var prepared, out _); Assert.That(client.World.CommitImport(prepared), Is.True);
            uint before = host.World.Revision, surfaceBefore = host.World.Surfaces.Revision;
            Assert.That(host.World.Freeze(), Is.True); var delta = host.World.Capture(false, before, surfaceBefore);
            Assert.That(delta.Frozen, Is.True); Assert.That(delta.Revision, Is.EqualTo(before + 1));
            Assert.That(client.World.TryPrepareImport(delta, out prepared, out var error), Is.True, error); Assert.That(client.World.CommitImport(prepared), Is.True);
            Assert.That(client.World.Frozen, Is.True); Assert.That(host.World.Build(new[] { Vector3Int.up }, 1, 1).Added, Is.Zero);
            host.World.Clear(8); host.Color(); var fresh = host.Build(0);
            Assert.That(fresh.Handle.Id, Is.EqualTo(old.Handle.Id)); Assert.That(fresh.Handle, Is.Not.EqualTo(old.Handle));
            Assert.That(client.World.TryPrepareImport(host.World.Capture(true), out prepared, out error), Is.True, error);
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.Frozen, Is.False); Assert.That(client.World.TryGet(old.Handle, out _), Is.False);
        }

        [Test] public void PrunedJournalReturnsExplicitFullSnapshotForOlderBaselines()
        {
            var f = new Fixture(); f.World.Paint(new[] { Key() }, 1, 1); uint first = f.World.Revision, firstSurface = f.World.Surfaces.Revision;
            f.World.Paint(new[] { Key(1) }, 2, 1); f.World.PruneJournal(first);
            Assert.That(f.World.Capture(false, 0, 0).Full, Is.True);
            var delta = f.World.Capture(false, first, firstSurface); Assert.That(delta.Full, Is.False); Assert.That(delta.BaseRevision, Is.EqualTo(first));
            var client = new Fixture(); Assert.That(client.World.TryPrepareImport(f.World.Capture(false, 0, 0), out var prepared, out var error), Is.True, error);
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.PinkCount, Is.EqualTo(1)); Assert.That(client.World.BlueCount, Is.EqualTo(1));
        }

        [Test] public void DeltaCannotResurrectDeletedIdButCanCreateTheNextStableId()
        {
            var host = new Fixture(); host.Color(); var old = host.Build(0); host.World.Destroy(new[] { old.Handle }, 2, 1, 0);
            var client = new Fixture(); client.World.TryPrepareImport(host.World.Capture(true), out var prepared, out _);
            Assert.That(client.World.CommitImport(prepared), Is.True); uint baseline = host.World.Revision, surfaceBaseline = host.World.Surfaces.Revision;
            var forged = host.World.Capture(false, baseline, surfaceBaseline); forged.Revision++;
            old.Revision = forged.Revision; forged.Upserts = new[] { old };
            Assert.That(client.World.TryPrepareImport(forged, out _, out _), Is.False); Assert.That(client.World.Count, Is.Zero);
            var fresh = host.Build(0); Assert.That(fresh.Handle.Id, Is.EqualTo(2));
            Assert.That(client.World.TryPrepareImport(host.World.Capture(false, baseline, surfaceBaseline), out prepared, out var error), Is.True, error);
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.TryGet(fresh.Handle, out _), Is.True);
        }

        [Test] public void DeltaCanRemoveAnObjectCreatedAndDeletedBeforeClientReceivesIt()
        {
            var host = new Fixture(); host.Color(); var old = host.Build(0); host.World.Destroy(new[] { old.Handle }, 2, 1, 0);
            var client = new Fixture(); var delta = host.World.Capture(false, 0, 0);
            Assert.That(delta.Upserts, Is.Empty); Assert.That(delta.Removed.Length, Is.EqualTo(1));
            Assert.That(client.World.TryPrepareImport(delta, out var prepared, out var error), Is.True, error);
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.Count, Is.Zero); Assert.That(client.World.NextId, Is.EqualTo(2));
        }
        [Test] public void PreparedMixedPaintDeltaPublishesExactCountsAndStableRelocationWithItsGridHalf()
        {
            var host = new Fixture(); host.Env.MapId = 11; host.Color(); host.Color(2, 1); host.Color(1, -1);
            var bottom = host.Build(0); var upper = host.Build(1); var client = new Fixture(); client.Env.MapId = 11;
            Assert.That(client.World.TryPrepareImport(host.World.Capture(true), out var initial, out var error), Is.True, error);
            Assert.That(client.World.CommitImport(initial), Is.True);
            uint baseline = host.World.Revision, surfaceBaseline = host.World.Surfaces.Revision;
            var before = client.World.Capture(true); var oldStorage = client.World.Storage; var oldSurface = client.World.Surfaces;
            byte[] oldSurfaceBytes = oldSurface.ExportFull();
            host.World.Destroy(new[] { bottom.Handle }, 2, 1, 0);
            host.World.Paint(new[] { Key(1) }, 1, 1); host.World.Paint(new[] { Key(2) }, 2, 1);
            using (var tx = host.World.BeginMutation())
            {
                tx.RemovePaint(new[] { Key(-1) }, 2, 1, 0);
                Assert.That(tx.TryCommit(out _, out error), Is.True, error);
            }
            byte[] expected = BlockFixedGridCodec.Encode(host.World.Capture(true));
            var delta = BlockFixedGridCodec.Decode(BlockFixedGridCodec.Encode(host.World.Capture(false, baseline, surfaceBaseline)));
            Assert.That(delta.Full, Is.False); Assert.That(delta.Upserts.Length, Is.EqualTo(1)); Assert.That(delta.Removed.Length, Is.EqualTo(1));
            Assert.That(client.World.TryPrepareImport(delta, out var prepared, out error), Is.True, error);
            SameSnapshot(client.World, before); Assert.That(client.World.Storage, Is.SameAs(oldStorage));
            Assert.That(client.World.Surfaces, Is.SameAs(oldSurface));
            Array.Clear(delta.SurfaceBytes, 0, delta.SurfaceBytes.Length); delta.Upserts[0] = default; delta.Removed[0] = default;
            int observed = 0;
            client.World.Committed += commit =>
            {
                observed++; Assert.That(client.World.Count, Is.EqualTo(1));
                Assert.That(client.World.TryGet(upper.Handle, out var relocated), Is.True); Assert.That(relocated.Cell.y, Is.Zero);
                Assert.That(client.World.TryGet(bottom.Handle, out _), Is.False);
                Assert.That(client.World.PinkCount, Is.EqualTo(3)); Assert.That(client.World.BlueCount, Is.EqualTo(1));
                Assert.That(Owner(client.World, Key(1)), Is.EqualTo(1)); Assert.That(Owner(client.World, Key(2)), Is.EqualTo(2));
                Assert.That(client.World.Surfaces.TryGet(Key(-1), out var deleted), Is.True);
                Assert.That(deleted.Owner, Is.Zero); Assert.That(deleted.Revision, Is.GreaterThan(surfaceBaseline));
                Assert.That(commit.ChangedPaintKeys, Does.Contain(Key(-1)));
                Assert.That(BlockFixedGridCodec.Encode(client.World.Capture(true)), Is.EqualTo(expected));
            };
            Assert.That(client.World.CommitImport(prepared), Is.True); Assert.That(client.World.CommitImport(prepared), Is.False);
            Assert.That(observed, Is.EqualTo(1)); Assert.That(client.World.LastObserverException, Is.Null);
            Assert.That(oldSurface.ExportFull(), Is.EqualTo(oldSurfaceBytes));
            oldSurface.TrySet(Key(2), 1, out _);
            Assert.That(BlockFixedGridCodec.Encode(client.World.Capture(true)), Is.EqualTo(expected));
        }

        [Test] public void RejectedMixedDeltaLeavesThePublishedPairUntouchedAndDoesNotPoisonNextValidImport()
        {
            var host = new Fixture(); host.Env.MapId = 11; host.Color(); host.Build(0); var client = new Fixture(); client.Env.MapId = 11;
            Assert.That(client.World.TryPrepareImport(host.World.Capture(true), out var initial, out var error), Is.True, error);
            Assert.That(client.World.CommitImport(initial), Is.True);
            uint baseline = host.World.Revision, surfaceBaseline = host.World.Surfaces.Revision;
            host.Build(1); host.World.Paint(new[] { Key(1) }, 2, 1);
            byte[] wire = BlockFixedGridCodec.Encode(host.World.Capture(false, baseline, surfaceBaseline));
            var before = client.World.Capture(true); var oldStorage = client.World.Storage; var oldSurface = client.World.Surfaces;
            var badGrid = BlockFixedGridCodec.Decode(wire); var unsupported = badGrid.Upserts[0]; unsupported.Cell.y = 3; badGrid.Upserts[0] = unsupported;
            Assert.That(client.World.TryPrepareImport(badGrid, out var failedGrid, out _), Is.False); Assert.That(failedGrid, Is.Null);
            SameSnapshot(client.World, before);
            var badPaint = BlockFixedGridCodec.Decode(wire); badPaint.SurfaceBytes[badPaint.SurfaceBytes.Length - 5] = 3;
            Assert.That(client.World.TryPrepareImport(badPaint, out var failedPaint, out _), Is.False); Assert.That(failedPaint, Is.Null);
            SameSnapshot(client.World, before); Assert.That(client.World.Storage, Is.SameAs(oldStorage));
            Assert.That(client.World.Surfaces, Is.SameAs(oldSurface));
            Assert.That(client.World.TryPrepareImport(BlockFixedGridCodec.Decode(wire), out var valid, out error), Is.True, error);
            SameSnapshot(client.World, before);
            Assert.That(client.World.CommitImport(valid), Is.True);
            Assert.That(BlockFixedGridCodec.Encode(client.World.Capture(true)), Is.EqualTo(BlockFixedGridCodec.Encode(host.World.Capture(true))));
            Assert.That(client.World.PinkCount, Is.EqualTo(3)); Assert.That(client.World.BlueCount, Is.EqualTo(1));
        }

    }
}
#endif
