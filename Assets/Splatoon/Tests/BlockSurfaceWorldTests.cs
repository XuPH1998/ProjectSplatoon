#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockSurfacePaint")]
    public sealed class BlockSurfaceWorldTests
    {
        static BlockSurfaceKey Key(int u = 0, int v = 0, int face = 11) => new(face, u, v);
        static BlockSurfaceWorld World(uint round = 7, bool reverse = false)
        {
            var world = new BlockSurfaceWorld(round);
            var cells = new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up, Vector2Int.one };
            if (reverse) Array.Reverse(cells);
            world.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, cells);
            return world;
        }
        static byte[] Edit(byte[] source, int offset, Action<BinaryWriter> write)
        {
            var result = (byte[])source.Clone();
            using var stream = new MemoryStream(result); using var writer = new BinaryWriter(stream);
            stream.Position = offset; write(writer); return result;
        }

        [Test] public void OppositeFacesHaveIndependentOwnershipAndNormals()
        {
            var world = World();
            world.RegisterFace(12, Vector3.zero, Vector3.forward, Vector3.right, new[] { Vector2Int.zero });
            Assert.That(world.TryGetFace(11, out var front), Is.True);
            Assert.That(world.TryGetFace(12, out var back), Is.True);
            Assert.That(front.Normal, Is.EqualTo(Vector3.down)); Assert.That(back.Normal, Is.EqualTo(Vector3.up));
            Assert.That(world.TrySet(Key(), 1, out _), Is.True);
            Assert.That(world.TrySet(Key(face: 12), 2, out _), Is.True);
            Assert.That(world.TryGet(Key(), out var a), Is.True);
            Assert.That(world.TryGet(Key(face: 12), out var b), Is.True);
            Assert.That(a.Owner, Is.EqualTo(1)); Assert.That(b.Owner, Is.EqualTo(2));
            Assert.That(world.PinkCount, Is.EqualTo(1)); Assert.That(world.BlueCount, Is.EqualTo(1));
        }

        [Test] public void OnlyRegisteredCompleteSquaresCanBeProjectedPaintedOrCentred()
        {
            var world = new BlockSurfaceWorld(1);
            world.RegisterFace(11, new Vector3(2, 3, 4), Vector3.right, Vector3.up, new[] { Vector2Int.zero });
            Assert.That(world.TryGetCenter(Key(), out var center), Is.True);
            Assert.That(Vector3.Distance(center, new Vector3(2.15f, 3.15f, 4)), Is.LessThan(.000001f));
            Assert.That(world.TryProject(11, center, out var projected), Is.True); Assert.That(projected, Is.EqualTo(Key()));
            Assert.That(world.TryProject(11, center + Vector3.forward * .009f, out _), Is.True);
            Assert.That(world.TryProject(11, center + Vector3.forward * .011f, out _), Is.False);
            Assert.That(world.TryProject(11, center + Vector3.right * .30f, out _), Is.False, "An incomplete edge cell is absent, not clipped.");
            Assert.That(world.TryGetCenter(Key(1), out _), Is.False);
            Assert.That(world.TryGet(Key(1), out _), Is.False); Assert.That(world.TrySet(Key(1), 1, out _), Is.False);
            Assert.That(world.TrySet(Key(face: 999), 1, out _), Is.False); Assert.That(world.Revision, Is.Zero);
        }

        [Test] public void ProjectionSupportsNegativeAddressesAndAnObliqueWorldMetreBasis()
        {
            var world = new BlockSurfaceWorld(1);
            var u = new Vector3(1, 1, 0).normalized; var v = new Vector3(-1, 1, 1).normalized;
            v = Vector3.Cross(Vector3.Cross(u, v), u).normalized;
            world.RegisterFace(11, new Vector3(-3, 1, 2), u, v, new[] { new Vector2Int(-2, 3) });
            Assert.That(world.TryGetCenter(Key(-2, 3), out var center), Is.True);
            Assert.That(world.TryProject(11, center, out var key), Is.True); Assert.That(key, Is.EqualTo(Key(-2, 3)));
        }

        [Test] public void ReplacementDeletionAndNoOpMaintainExactCountsAndContactRevisions()
        {
            var world = World(); Assert.That(world.TryGet(Key(), out var blank), Is.True);
            Assert.That(blank.Owner, Is.Zero); Assert.That(blank.Revision, Is.Zero);
            Assert.That(world.TrySet(Key(), 0, out var old), Is.True); Assert.That(old, Is.Zero); Assert.That(world.Revision, Is.Zero);
            world.TrySet(Key(), 1, out old); Assert.That(old, Is.Zero);
            world.TryGet(Key(), out var pink); Assert.That(pink.Revision, Is.EqualTo(1));
            Assert.That(world.IsCurrent(7, Key(), 1, 1), Is.True);
            world.TrySet(Key(), 1, out old); Assert.That(old, Is.EqualTo(1)); Assert.That(world.Revision, Is.EqualTo(1));
            world.TrySet(Key(), 2, out old); Assert.That(old, Is.EqualTo(1));
            Assert.That(world.PinkCount, Is.Zero); Assert.That(world.BlueCount, Is.EqualTo(1));
            Assert.That(world.IsCurrent(Key(), pink.Owner, pink.Revision), Is.False);
            Assert.That(world.TrySet(Key(), 3, out _), Is.False); Assert.That(world.Revision, Is.EqualTo(2));
            world.TrySet(Key(), 0, out old); Assert.That(old, Is.EqualTo(2));
            Assert.That(world.BlueCount, Is.Zero); Assert.That(world.EnumeratePainted(), Is.Empty);
            Assert.That(world.TryGet(Key(), out var deleted), Is.True); Assert.That(deleted.Owner, Is.Zero); Assert.That(deleted.Revision, Is.EqualTo(3));
            Assert.That(world.IsCurrent(Key(), 0, 0), Is.False, "Deletion retains a tombstone, invalidating a stale blank contact.");
        }

        [Test] public void ClearPreservesTopologyButInvalidatesContactsFromThePreviousRound()
        {
            var world = World(); ulong topology = world.TopologyHash;
            world.TrySet(Key(), 1, out _); world.Clear(8);
            Assert.That(world.Round, Is.EqualTo(8)); Assert.That(world.Revision, Is.Zero);
            Assert.That(world.PinkCount, Is.Zero); Assert.That(world.BlueCount, Is.Zero);
            Assert.That(world.TopologyHash, Is.EqualTo(topology)); Assert.That(world.TryGet(Key(), out _), Is.True);
            world.TrySet(Key(), 1, out _);
            Assert.That(world.IsCurrent(7, Key(), 1, 1), Is.False, "Asynchronous contacts must include the round.");
            Assert.That(world.IsCurrent(8, Key(), 1, 1), Is.True);
        }

        [Test] public void DiskUsesTheOriginalInterleavedRingOrderAndSkipsIllegalCells()
        {
            var world = new BlockSurfaceWorld(1); var cells = new List<Vector2Int>();
            for (int x = -2; x <= 2; x++) for (int y = -2; y <= 2; y++) cells.Add(new Vector2Int(x, y));
            cells.Remove(new Vector2Int(1, 0)); world.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, cells);
            var expected = new[] { Key(), Key(-1, -1), Key(-1, 1), Key(0, -1), Key(0, 1), Key(1, -1), Key(1, 1), Key(-1, 0) };
            Assert.That(world.EnumerateDisk(11, Key(), .43f).ToArray(), Is.EqualTo(expected));
            Assert.That(world.EnumerateDisk(11, Key(), 0).ToArray(), Is.EqualTo(new[] { Key() }));
            Assert.That(world.EnumerateDisk(11, Key(), float.NaN), Is.Empty);
            Assert.That(world.EnumerateDisk(11, Key(), -1), Is.Empty);
            Assert.That(world.EnumerateDisk(12, Key(), 1), Is.Empty);
        }

        [Test] public void VeryLargeSparseDiskKeepsTheSameRingOrderWithoutScanningEmptySpace()
        {
            var world = new BlockSurfaceWorld(1);
            world.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward,
                new[] { Vector2Int.zero, new Vector2Int(300, 1), new Vector2Int(-300, 1),
                    new Vector2Int(300, -300), new Vector2Int(-300, -300) });
            Assert.That(world.EnumerateDisk(11, Key(), float.MaxValue).ToArray(), Is.EqualTo(
                new[] { Key(), Key(-300, -300), Key(300, -300), Key(-300, 1), Key(300, 1) }));
        }

        [Test] public void TopologyHashIsStableAcrossRegistrationOrderAndCoversGeometryAndLegalSquares()
        {
            var a = World(); var b = World(reverse: true);
            Assert.That(a.TopologyHash, Is.EqualTo(b.TopologyHash));
            a.RegisterFace(12, Vector3.one, Vector3.up, Vector3.forward, new[] { Vector2Int.zero });
            var c = new BlockSurfaceWorld(7);
            c.RegisterFace(12, Vector3.one, Vector3.up, Vector3.forward, new[] { Vector2Int.zero });
            c.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward,
                new[] { Vector2Int.one, Vector2Int.up, Vector2Int.right, Vector2Int.zero, Vector2Int.one });
            Assert.That(a.TopologyHash, Is.EqualTo(c.TopologyHash));
            Assert.That(a.TopologyHash, Is.Not.EqualTo(b.TopologyHash));
            var shifted = new BlockSurfaceWorld(7);
            shifted.RegisterFace(11, Vector3.right * .001f, Vector3.right, Vector3.forward,
                new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up, Vector2Int.one });
            Assert.That(b.TopologyHash, Is.Not.EqualTo(shifted.TopologyHash));
        }

        [Test] public void InvalidRegistrationIsAtomic()
        {
            var world = World(); ulong hash = world.TopologyHash;
            Assert.Throws<ArgumentException>(() => world.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, new[] { Vector2Int.zero }));
            Assert.Throws<ArgumentException>(() => world.RegisterFace(12, Vector3.zero, Vector3.right * 2, Vector3.forward, new[] { Vector2Int.zero }));
            Assert.Throws<ArgumentException>(() => world.RegisterFace(12, Vector3.zero, Vector3.right, Vector3.right, new[] { Vector2Int.zero }));
            Assert.Throws<ArgumentException>(() => world.RegisterFace(12, new Vector3(float.NaN, 0, 0), Vector3.right, Vector3.forward, new[] { Vector2Int.zero }));
            Assert.Throws<ArgumentNullException>(() => world.RegisterFace(12, Vector3.zero, Vector3.right, Vector3.forward, null));
            Assert.That(world.FaceCount, Is.EqualTo(1)); Assert.That(world.TopologyHash, Is.EqualTo(hash));
        }

        [Test] public void FullAndDeltaRoundtripPreserveReplacementsCountsAndDeletionTombstones()
        {
            var server = World(); var replica = World(reverse: true);
            server.TrySet(Key(), 1, out _); server.TrySet(Key(1), 2, out _);
            Assert.That(replica.TryImportFull(server.ExportFull()), Is.True);
            Assert.That(replica.ExportFull(), Is.EqualTo(server.ExportFull()));
            uint baseline = server.Revision;
            server.TrySet(Key(), 0, out _); server.TrySet(Key(1), 1, out _); server.TrySet(Key(0, 1), 2, out _);
            byte[] delta = server.ExportDelta(baseline);
            Assert.That(BitConverter.ToInt32(delta, 26), Is.EqualTo(3));
            Assert.That(replica.TryImportDelta(delta), Is.True);
            Assert.That(replica.ExportFull(), Is.EqualTo(server.ExportFull()));
            Assert.That(replica.PinkCount, Is.EqualTo(1)); Assert.That(replica.BlueCount, Is.EqualTo(1));
            Assert.That(replica.TryGet(Key(), out var deleted), Is.True); Assert.That(deleted.Revision, Is.EqualTo(3));
            Assert.That(replica.TryImportDelta(server.ExportDelta(server.Revision)), Is.True);
            Assert.That(replica.TryImportDelta(delta), Is.False, "A duplicate delta no longer has the current baseline.");
        }

        [Test] public void DeltaContainsOnlyEachChangedSquaresLatestStateIncludingDeletion()
        {
            var server = World(); var replica = World(); server.TrySet(Key(1), 2, out _);
            replica.TryImportFull(server.ExportFull()); uint baseline = server.Revision;
            server.TrySet(Key(), 1, out _); server.TrySet(Key(), 2, out _); server.TrySet(Key(), 0, out _);
            byte[] delta = server.ExportDelta(baseline);
            Assert.That(delta.Length, Is.EqualTo(30 + 17)); Assert.That(BitConverter.ToInt32(delta, 26), Is.EqualTo(1));
            Assert.That(replica.TryImportDelta(delta), Is.True); Assert.That(replica.ExportFull(), Is.EqualTo(server.ExportFull()));
            Assert.Throws<ArgumentOutOfRangeException>(() => server.ExportDelta(server.Revision + 1));
        }

        [Test] public void MalformedFullPacketsAreRejectedWithoutAnyPartialMutation()
        {
            var server = World(); server.TrySet(Key(), 1, out _); server.TrySet(Key(1), 2, out _);
            byte[] valid = server.ExportFull(); var replica = World(); replica.TrySet(Key(0, 1), 2, out _);
            byte[] before = replica.ExportFull();
            var malformed = new List<byte[]>
            {
                null, Array.Empty<byte>(), valid.Take(valid.Length - 1).ToArray(), valid.Concat(new byte[] { 0 }).ToArray(),
                Edit(valid, 0, w => w.Write(0u)), Edit(valid, 4, w => w.Write((byte)2)),
                Edit(valid, 5, w => w.Write((byte)2)), Edit(valid, 6, w => w.Write(8u)),
                Edit(valid, 10, w => w.Write(0UL)), Edit(valid, 18, w => w.Write(1u)),
                Edit(valid, 26, w => w.Write(-1)), Edit(valid, 26, w => w.Write(5)),
                Edit(valid, 30, w => w.Write(999)), Edit(valid, 34, w => w.Write(999)),
                Edit(valid, 42, w => w.Write((byte)3)), Edit(valid, 43, w => w.Write(3u)),
                Edit(valid, 43, w => w.Write(0u)), Edit(valid, 60, w => w.Write(1u)),
                Edit(valid, 22, w => w.Write(3u)), Edit(valid, 22, w => w.Write(0u)),
                Edit(valid, 47, w => { w.Write(11); w.Write(0); w.Write(0); }),
                Edit(valid, valid.Length - 5, w => w.Write((byte)3))
            };
            foreach (byte[] packet in malformed)
            {
                Assert.That(replica.TryImportFull(packet), Is.False);
                Assert.That(replica.TryPrepareImport(packet, true, replica.Round, out var prepared), Is.False);
                Assert.That(prepared, Is.Null); Assert.That(replica.PinkCount, Is.Zero); Assert.That(replica.BlueCount, Is.EqualTo(1));
                Assert.That(replica.ExportFull(), Is.EqualTo(before), "Validation must finish before replacing any ownership or count.");
            }
        }

        [Test] public void DeltaRejectsWrongBaselineRoundTopologyFutureAndOldEntryRevisionsAtomically()
        {
            var server = World(); var replica = World(); server.TrySet(Key(), 1, out _);
            replica.TryImportFull(server.ExportFull()); server.TrySet(Key(), 0, out _);
            byte[] valid = server.ExportDelta(1), before = replica.ExportFull();
            foreach (var packet in new[]
            {
                Edit(valid, 18, w => w.Write(0u)), Edit(valid, 18, w => w.Write(3u)),
                Edit(valid, 6, w => w.Write(6u)), Edit(valid, 10, w => w.Write(0UL)),
                Edit(valid, 43, w => w.Write(3u)), Edit(valid, 43, w => w.Write(1u)),
                Edit(valid, 22, w => w.Write(1u)), Edit(valid, 22, w => w.Write(3u))
            })
            {
                Assert.That(replica.TryImportDelta(packet), Is.False);
                Assert.That(replica.TryPrepareImport(packet, false, replica.Round, out var prepared), Is.False);
                Assert.That(prepared, Is.Null); Assert.That(replica.PinkCount, Is.EqualTo(1)); Assert.That(replica.BlueCount, Is.Zero);
                Assert.That(replica.ExportFull(), Is.EqualTo(before));
            }
            Assert.That(replica.TryImportDelta(valid), Is.True);
            Assert.That(replica.TryImportFull(before), Is.False, "An older full snapshot cannot rewind contact revisions.");
        }

        [Test] public void SameFaceIdsWithDifferentGeometryCannotImportOwnership()
        {
            var server = World(); server.TrySet(Key(), 1, out _);
            var replica = new BlockSurfaceWorld(7);
            replica.RegisterFace(11, Vector3.up, Vector3.right, Vector3.forward,
                new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up, Vector2Int.one });
            Assert.That(replica.TryImportFull(server.ExportFull()), Is.False);
            Assert.That(replica.Revision, Is.Zero); Assert.That(replica.PinkCount, Is.Zero);
        }

        [Test] public void CloneKeepsTopologyTombstonesAndCountsWithoutSharingMutableOwnership()
        {
            var source = World(); source.TrySet(Key(), 1, out _); source.TrySet(Key(), 0, out _);
            source.TrySet(Key(1), 2, out _); var clone = source.Clone();
            Assert.That(clone.ExportFull(), Is.EqualTo(source.ExportFull()));
            Assert.That(clone.Round, Is.EqualTo(source.Round)); Assert.That(clone.BlueCount, Is.EqualTo(1));
            clone.TrySet(Key(), 1, out _); clone.TrySet(Key(1), 0, out _);
            Assert.That(source.TryGet(Key(), out var tombstone), Is.True); Assert.That(tombstone.Revision, Is.EqualTo(2));
            Assert.That(tombstone.Owner, Is.Zero); Assert.That(source.BlueCount, Is.EqualTo(1));
            Assert.That(source.Revision, Is.EqualTo(3)); Assert.That(clone.Revision, Is.EqualTo(5));
            clone.Clear(8); Assert.That(source.Round, Is.EqualTo(7)); Assert.That(source.BlueCount, Is.EqualTo(1));
        }
        [Test] public void PreparedDeltaKeepsSourceAndIndependentResultsDetachedAcrossOwnershipChanges()
        {
            var server = World(); server.TrySet(Key(), 1, out _); server.TrySet(Key(1), 2, out _);
            server.TrySet(Key(0, 1), 1, out _);
            var source = World(reverse: true); Assert.That(source.TryImportFull(server.ExportFull()), Is.True);
            byte[] before = source.ExportFull(); uint baseline = server.Revision;
            server.TrySet(Key(), 2, out _); server.TrySet(Key(1), 0, out _);
            server.TrySet(Key(1, 1), 1, out _); server.TrySet(Key(1, 1), 0, out _);
            server.TrySet(Key(0, 1), 2, out _); server.TrySet(Key(0, 1), 1, out _);
            byte[] delta = server.ExportDelta(baseline), expected = server.ExportFull();
            Assert.That(source.TryPrepareImport(delta, false, 7, out var first), Is.True);
            Assert.That(source.TryPrepareImport(delta, false, 7, out var second), Is.True);
            Assert.That(first, Is.Not.SameAs(source)); Assert.That(second, Is.Not.SameAs(first));
            Assert.That(source.ExportFull(), Is.EqualTo(before));
            Assert.That(first.ExportFull(), Is.EqualTo(expected)); Assert.That(second.ExportFull(), Is.EqualTo(expected));
            Assert.That(second.PinkCount, Is.EqualTo(1)); Assert.That(second.BlueCount, Is.EqualTo(1));
            Assert.That(second.TryGet(Key(1), out var deleted), Is.True);
            Assert.That(deleted.Owner, Is.Zero); Assert.That(deleted.Revision, Is.EqualTo(baseline + 2));
            Assert.That(second.TryGet(Key(1, 1), out var newTombstone), Is.True);
            Assert.That(newTombstone.Owner, Is.Zero); Assert.That(newTombstone.Revision, Is.GreaterThan(baseline));
            Assert.That(second.TryGet(Key(0, 1), out var sameOwner), Is.True);
            Assert.That(sameOwner.Owner, Is.EqualTo(1)); Assert.That(sameOwner.Revision, Is.GreaterThan(baseline));
            Array.Clear(delta, 0, delta.Length);
            first.TrySet(Key(), 0, out _); source.TrySet(Key(0, 1), 2, out _);
            Assert.That(second.ExportFull(), Is.EqualTo(expected), "Packet reuse and either other mutable world must not alter a prepared result.");
            Assert.That(first.TryGet(Key(0, 1), out var unchanged), Is.True); Assert.That(unchanged.Owner, Is.EqualTo(1));
            Assert.That(source.TryGet(Key(), out var original), Is.True); Assert.That(original.Owner, Is.EqualTo(1));
        }

        [Test] public void PreparedFullForANewRoundDropsOldTombstonesWithoutRewindingTheLiveWorld()
        {
            var source = World(); source.TrySet(Key(), 1, out _); source.TrySet(Key(), 0, out _);
            source.TrySet(Key(1), 2, out _); byte[] before = source.ExportFull();
            var next = World(8); next.TrySet(Key(1, 1), 1, out _);
            Assert.That(source.TryPrepareImport(next.ExportFull(), true, 8, out var prepared), Is.True);
            Assert.That(prepared.Round, Is.EqualTo(8)); Assert.That(prepared.ExportFull(), Is.EqualTo(next.ExportFull()));
            Assert.That(prepared.PinkCount, Is.EqualTo(1)); Assert.That(prepared.BlueCount, Is.Zero);
            Assert.That(prepared.TryGet(Key(), out var freshBlank), Is.True); Assert.That(freshBlank.Revision, Is.Zero);
            Assert.That(source.TryPrepareImport(World().ExportFull(), true, 7, out var older), Is.False);
            Assert.That(older, Is.Null, "Same-round full data must retain the current revision floor.");
            Assert.That(source.TryPrepareImport(next.ExportDelta(0), false, 8, out var wrongRound), Is.False);
            Assert.That(wrongRound, Is.Null); Assert.That(source.ExportFull(), Is.EqualTo(before));
            prepared.RegisterFace(12, Vector3.up, Vector3.right, Vector3.forward, new[] { Vector2Int.zero });
            Assert.That(source.TryGetFace(12, out _), Is.False, "The face dictionary remains independently mutable; face chart values may be shared.");
            prepared.Clear(9); Assert.That(source.ExportFull(), Is.EqualTo(before));
        }

        [Test] public void PreparedEmptyDeltaRetainsCountsAndTombstonesWhileRemainingIndependent()
        {
            var source = World(); source.TrySet(Key(), 1, out _); source.TrySet(Key(), 0, out _);
            source.TrySet(Key(1), 2, out _); byte[] before = source.ExportFull();
            byte[] delta = source.ExportDelta(source.Revision);
            Assert.That(BitConverter.ToInt32(delta, 26), Is.Zero);
            Assert.That(source.TryPrepareImport(delta, false, source.Round, out var prepared), Is.True);
            Assert.That(prepared.ExportFull(), Is.EqualTo(before)); Assert.That(prepared.Revision, Is.EqualTo(3));
            Assert.That(prepared.PinkCount, Is.Zero); Assert.That(prepared.BlueCount, Is.EqualTo(1));
            source.TrySet(Key(), 1, out _); prepared.TrySet(Key(1), 0, out _);
            Assert.That(source.BlueCount, Is.EqualTo(1)); Assert.That(prepared.PinkCount, Is.Zero);
            Assert.That(prepared.TryGet(Key(), out var tombstone), Is.True);
            Assert.That(tombstone.Owner, Is.Zero); Assert.That(tombstone.Revision, Is.EqualTo(2));
        }

    }
}
#endif
