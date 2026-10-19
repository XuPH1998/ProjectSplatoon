using System;
using System.IO;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridTransferTests
    {
        static readonly BlockSurfaceKey Key = new(11, 0, 0);
        static BlockSurfaceWorld Surface()
        {
            var value = new BlockSurfaceWorld(7);
            value.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, new[] { Vector2Int.zero });
            return value;
        }
        static BlockFixedGridHandle Handle(ulong id = 1) => new() { Epoch = 3, Round = 7, Generation = 1, Id = id };
        static BlockFixedGridState State(ulong id = 1) => new()
        { Handle = Handle(id), Cell = new Vector3Int((int)id, 1, 2), Owner = 1,
            Support = new BlockFixedGridMapSupport { Id = 11, BaseCellY = 0, Height = .08f, SurfaceKey = Key }, Revision = 8 };
        static BlockFixedGridSnapshot Snapshot(params BlockFixedGridState[] states)
        {
            var surface = Surface(); surface.TrySet(Key, 2, out _);
            return new BlockFixedGridSnapshot { Full = true, Epoch = 3, Round = 7, Revision = 8,
                SurfaceRevision = surface.Revision, NextId = 1000, Upserts = states, SurfaceBytes = surface.ExportFull() };
        }
        static byte[] Edit(byte[] bytes, int offset, Action<BinaryWriter> edit)
        {
            var value = (byte[])bytes.Clone(); using var stream = new MemoryStream(value); using var writer = new BinaryWriter(stream);
            stream.Position = offset; edit(writer); return value;
        }
        static BlockFixedGridFragment Fragment(BlockFixedGridSnapshot snapshot, byte[] bytes, int index = 0)
        {
            int length = Math.Min(BlockFixedGridFragment.PayloadBytes, bytes.Length - index * BlockFixedGridFragment.PayloadBytes);
            var data = new byte[length]; Buffer.BlockCopy(bytes, index * BlockFixedGridFragment.PayloadBytes, data, 0, length);
            return new BlockFixedGridFragment { Version = BlockFixedGridCodec.ProtocolVersion, Epoch = snapshot.Epoch, Round = snapshot.Round,
                Transfer = 9, Sequence = 73, Tick = 120, Full = snapshot.Full, Frozen = snapshot.Frozen,
                BaseRevision = snapshot.BaseRevision, Revision = snapshot.Revision, SurfaceBaseRevision = snapshot.SurfaceBaseRevision,
                SurfaceRevision = snapshot.SurfaceRevision, Hash = BlockTransferFragment.Checksum(bytes), Length = bytes.Length, Index = index, Data = data };
        }

        [Test] public void FullWirePairsExactFixedIdentitySupportAndSurfaceOwnership()
        {
            var expected = Snapshot(State()); byte[] bytes = BlockFixedGridCodec.Encode(expected);
            Assert.That(bytes.Length, Is.EqualTo(BlockFixedGridCodec.HeaderBytes + BlockFixedGridCodec.StateBytes + expected.SurfaceBytes.Length));
            var actual = BlockFixedGridCodec.Decode(bytes);
            Assert.That(actual.Full, Is.True); Assert.That(actual.Epoch, Is.EqualTo(3)); Assert.That(actual.Round, Is.EqualTo(7));
            Assert.That(actual.Revision, Is.EqualTo(8)); Assert.That(actual.NextId, Is.EqualTo(1000));
            Assert.That(actual.Upserts.Length, Is.EqualTo(1)); Assert.That(actual.Removed, Is.Empty);
            var cell = actual.Upserts[0]; Assert.That(cell.Handle, Is.EqualTo(expected.Upserts[0].Handle));
            Assert.That(cell.Cell, Is.EqualTo(expected.Upserts[0].Cell)); Assert.That(cell.Owner, Is.EqualTo(1));
            Assert.That(cell.Support.Id, Is.EqualTo(11)); Assert.That(cell.Support.Height, Is.EqualTo(.08f));
            Assert.That(cell.Support.BaseCellY, Is.Zero); Assert.That(cell.Support.SurfaceKey, Is.EqualTo(Key));
            var surface = Surface(); Assert.That(surface.TryImportFull(actual.SurfaceBytes), Is.True);
            Assert.That(surface.BlueCount, Is.EqualTo(1)); Assert.That(surface.Revision, Is.EqualTo(actual.SurfaceRevision));
        }
        [Test] public void DeltaCarriesRelocationDeletionAndColourReplacementInOnePayload()
        {
            var surface = Surface(); surface.TrySet(Key, 2, out _); uint before = surface.Revision;
            surface.TrySet(Key, 1, out _); var moved = State(); moved.Cell = new Vector3Int(1, 0, 2); moved.Revision = 9;
            var snapshot = Snapshot(moved); snapshot.Full = false; snapshot.BaseRevision = 8; snapshot.Revision = 9;
            snapshot.SurfaceBaseRevision = before; snapshot.SurfaceRevision = surface.Revision;
            snapshot.SurfaceBytes = surface.ExportDelta(before); snapshot.Removed = new[] { Handle(2) };
            var actual = BlockFixedGridCodec.Decode(BlockFixedGridCodec.Encode(snapshot));
            Assert.That(actual.Full, Is.False); Assert.That(actual.BaseRevision, Is.EqualTo(8));
            Assert.That(actual.Upserts[0].Handle, Is.EqualTo(Handle())); Assert.That(actual.Upserts[0].Cell.y, Is.Zero);
            Assert.That(actual.Removed[0], Is.EqualTo(Handle(2)));
            var client = Surface(); client.TrySet(Key, 2, out _); Assert.That(client.TryImportDelta(actual.SurfaceBytes), Is.True);
            Assert.That(client.PinkCount, Is.EqualTo(1)); Assert.That(client.BlueCount, Is.Zero);
        }
        [Test] public void EmptyFullAndFreezeOnlyDeltaPreserveTheirDistinctBaselines()
        {
            var empty = Snapshot(); var decoded = BlockFixedGridCodec.Decode(BlockFixedGridCodec.Encode(empty));
            Assert.That(decoded.Upserts, Is.Empty); Assert.That(decoded.Removed, Is.Empty);
            var surface = Surface(); empty.Full = false; empty.Frozen = true; empty.BaseRevision = 8; empty.Revision = 9;
            empty.SurfaceRevision = empty.SurfaceBaseRevision = 0; empty.SurfaceBytes = surface.ExportDelta(0);
            decoded = BlockFixedGridCodec.Decode(BlockFixedGridCodec.Encode(empty));
            Assert.That(decoded.Full, Is.False); Assert.That(decoded.Frozen, Is.True); Assert.That(decoded.BaseRevision, Is.EqualTo(8));
        }
        [Test] public void DuplicateCellIdentityAndCreationRemovalConflictAreRejected()
        {
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Encode(Snapshot(State(), State())));
            var second = State(2); second.Cell = State().Cell;
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Encode(Snapshot(State(), second)));
            var value = Snapshot(State()); value.Full = false; value.Removed = new[] { Handle() };
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Encode(value));
        }
        [Test] public void WrongHandleEpochRoundGenerationOwnerSupportAndNextIdAreRejected()
        {
            foreach (Action<BlockFixedGridState[]> mutate in new Action<BlockFixedGridState[]>[]
            { cells => cells[0].Handle.Epoch++, cells => cells[0].Handle.Round++, cells => cells[0].Handle.Generation = 0,
                cells => cells[0].Owner = 0, cells => cells[0].Support.Id = 0, cells => cells[0].Support.Height = float.NaN,
                cells => cells[0].Revision = 9 })
            {
                var value = Snapshot(State()); mutate(value.Upserts);
                Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Encode(value));
            }
            var next = Snapshot(State()); next.NextId = 1;
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Encode(next));
        }
        [Test] public void OldDynamicAndSurfaceEnvelopePayloadsCannotDowngradeFixedProtocol()
        {
            byte[] dynamicBytes = BlockDynamicCodec.Encode(7, Array.Empty<BlockDynamicState>(), Array.Empty<ulong>());
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Decode(dynamicBytes));
            byte[] legacy = BlockSurfaceDynamicEnvelope.Encode(dynamicBytes, Surface().ExportFull(), 0, true);
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Decode(legacy));
        }
        [Test] public void MalformedVersionFlagsCountsAndTrailingBytesFailBeforeImport()
        {
            byte[] bytes = BlockFixedGridCodec.Encode(Snapshot(State()));
            foreach (var malformed in new[] { Edit(bytes, 4, writer => writer.Write(1)),
                Edit(bytes, 4, writer => writer.Write(BlockFixedGridCodec.ProtocolVersion + 1)), Edit(bytes, 8, writer => writer.Write((byte)2)),
                Edit(bytes, 9, writer => writer.Write((byte)2)), Edit(bytes, 42, writer => writer.Write(int.MaxValue)),
                Edit(bytes, 46, writer => writer.Write(-1)), Edit(bytes, 50, writer => writer.Write(29)) })
                Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Decode(malformed));
            Array.Resize(ref bytes, bytes.Length + 1);
            Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Decode(bytes));
        }
        [Test] public void EveryTruncatedPrefixIsRejected()
        {
            byte[] bytes = BlockFixedGridCodec.Encode(Snapshot(State()));
            for (int length = 0; length < bytes.Length; length++)
            { var truncated = new byte[length]; Array.Copy(bytes, truncated, length);
                Assert.Throws<InvalidDataException>(() => BlockFixedGridCodec.Decode(truncated), "Length " + length); }
        }
        [Test] public void FragmentsRejectMixedEpochRoundTickSequenceAndEitherBaseline()
        {
            var snapshot = Snapshot(State()); byte[] bytes = BlockFixedGridCodec.Encode(snapshot);
            var original = Fragment(snapshot, bytes); var assembly = new BlockFixedGridAssembly(original);
            foreach (ActionRef change in new ActionRef[] { (ref BlockFixedGridFragment value) => value.Epoch++,
                (ref BlockFixedGridFragment value) => value.Round++, (ref BlockFixedGridFragment value) => value.Sequence++,
                (ref BlockFixedGridFragment value) => value.Tick++, (ref BlockFixedGridFragment value) => value.BaseRevision++,
                (ref BlockFixedGridFragment value) => value.SurfaceBaseRevision++, (ref BlockFixedGridFragment value) => value.SurfaceRevision++,
                (ref BlockFixedGridFragment value) => value.Frozen = true, (ref BlockFixedGridFragment value) => value.Version++ })
            { var altered = original; change(ref altered); Assert.That(assembly.Add(altered), Is.False); }
            Assert.That(assembly.ReceivedCount, Is.Zero);
        }
        delegate void ActionRef(ref BlockFixedGridFragment value);
        [Test] public void ReorderedReliableFragmentsAndExactDuplicatesAssembleWithoutProgressInflation()
        {
            var states = new BlockFixedGridState[40]; for (int i = 0; i < states.Length; i++) states[i] = State((ulong)i + 1);
            var snapshot = Snapshot(states); byte[] bytes = BlockFixedGridCodec.Encode(snapshot);
            var first = Fragment(snapshot, bytes); var assembly = new BlockFixedGridAssembly(first);
            for (int index = first.Count - 1; index >= 0; index--)
            {
                var part = Fragment(snapshot, bytes, index); Assert.That(assembly.Add(part), Is.True);
                int count = assembly.ReceivedCount; Assert.That(assembly.Add(part), Is.True); Assert.That(assembly.ReceivedCount, Is.EqualTo(count));
                Assert.That(assembly.ReceivedThrough, Is.EqualTo(index == 0 ? first.Count : 0), "Only a contiguous prefix releases the sender window.");
            }
            Assert.That(assembly.Verify(), Is.True); CollectionAssert.AreEqual(bytes, assembly.Bytes);
            var conflicting = Fragment(snapshot, bytes); conflicting.Data[0] ^= 1;
            Assert.That(assembly.Add(conflicting), Is.False); Assert.That(assembly.Verify(), Is.True);
        }
        [Test] public void CorruptCompleteAssemblyNeverVerifies()
        {
            var snapshot = Snapshot(State()); byte[] bytes = BlockFixedGridCodec.Encode(snapshot);
            var part = Fragment(snapshot, bytes); var assembly = new BlockFixedGridAssembly(part); part.Data[10] ^= 1;
            Assert.That(assembly.Add(part), Is.True); Assert.That(assembly.Complete, Is.True); Assert.That(assembly.Verify(), Is.False);
        }
        [Test] public void ReliableManifestRoundTripsAndAcknowledgementBindsBothWorldVersions()
        {
            var snapshot = Snapshot(State()); byte[] bytes = BlockFixedGridCodec.Encode(snapshot); var expected = Fragment(snapshot, bytes);
            using var writer = new FastBufferWriter(1100, Allocator.Temp); writer.WriteNetworkSerializable(expected);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out BlockFixedGridFragment actual);
            Assert.That(actual.Valid, Is.True); Assert.That(actual.SameTransfer(expected), Is.True); Assert.That(actual.Matches(snapshot), Is.True);
            Assert.That(actual.MatchesAcknowledgement(3, 7, 9, 73, 8, 1, actual.Hash, false), Is.True);
            Assert.That(actual.MatchesAcknowledgement(4, 7, 9, 73, 8, 1, actual.Hash, false), Is.False);
            Assert.That(actual.MatchesAcknowledgement(3, 8, 9, 73, 8, 1, actual.Hash, false), Is.False);
            Assert.That(actual.MatchesAcknowledgement(3, 7, 9, 74, 8, 1, actual.Hash, false), Is.False);
            Assert.That(actual.MatchesAcknowledgement(3, 7, 9, 73, 8, 0, actual.Hash, false), Is.False);
            Assert.That(actual.MatchesAcknowledgement(3, 7, 9, 73, 8, 1, actual.Hash, true), Is.False);
        }
        [Test] public void MatchSnapshotSerializesFixedRevisionWithoutUsingDynamicRevision()
        {
            var expected = new MatchStateSnapshot { Round = 7, Tick = 120, TerrainRevision = 8,
                FixedBlockRevision = 8, DynamicBlockRevision = 0, PinkBlockCount = 5, BlueBlockCount = 3, Phase = MatchPhase.Finished };
            using var writer = new FastBufferWriter(256, Allocator.Temp); writer.WriteNetworkSerializable(expected);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out MatchStateSnapshot actual);
            Assert.That(actual.FixedBlockRevision, Is.EqualTo(8)); Assert.That(actual.DynamicBlockRevision, Is.Zero);
            Assert.That(actual.TerrainRevision, Is.EqualTo(8)); Assert.That(actual.Round, Is.EqualTo(7)); Assert.That(actual.Tick, Is.EqualTo(120));
            Assert.That(actual.PinkBlockCount, Is.EqualTo(5)); Assert.That(actual.BlueBlockCount, Is.EqualTo(3));
            Assert.That(actual.Phase, Is.EqualTo(MatchPhase.Finished));
        }
    }
}
