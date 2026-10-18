using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Blocks;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class BlockDynamicTransferTests
    {
        static BlockDynamicState Body(ulong id = 1) => new() { Id = id, Round = 7, Owner = 2,
            Position = new Vector3(-.2f, 4.2f, 3.7f), Rotation = Quaternion.Euler(17, 38, -23),
            Velocity = new Vector3(1, -2, 3), AngularVelocity = new Vector3(2, 4, -3), Sleeping = false };

        [Test] public void LifecyclePreservesRotatedBodyMotionOwnerAndIndependentRemoval()
        {
            var expected = Body(); var created = new List<BlockDynamicState>(); var removed = new List<ulong>();
            byte[] bytes = BlockDynamicCodec.Encode(7, new[] { expected }, new ulong[] { 19 });
            Assert.That(bytes.Length, Is.EqualTo(12 + BlockDynamicCodec.StateBytes + 8));
            BlockDynamicCodec.Decode(bytes, 7, created, removed);
            Assert.That(created.Count, Is.EqualTo(1)); var actual = created[0];
            Assert.That(actual.Id, Is.EqualTo(expected.Id)); Assert.That(actual.Round, Is.EqualTo(7)); Assert.That(actual.Owner, Is.EqualTo(2));
            Assert.That(actual.Position, Is.EqualTo(expected.Position)); Assert.That(actual.Rotation, Is.EqualTo(expected.Rotation));
            Assert.That(actual.Velocity, Is.EqualTo(expected.Velocity)); Assert.That(actual.AngularVelocity, Is.EqualTo(expected.AngularVelocity));
            Assert.That(actual.Sleeping, Is.EqualTo(expected.Sleeping)); CollectionAssert.AreEqual(new ulong[] { 19 }, removed);
        }
        [Test] public void EmptyBaselineClearsPreviousLifecycleWithoutInventingBodies()
        {
            var created = new List<BlockDynamicState> { Body() }; var removed = new List<ulong> { 4 };
            BlockDynamicCodec.Decode(BlockDynamicCodec.Encode(7, Array.Empty<BlockDynamicState>(), Array.Empty<ulong>()), 7, created, removed);
            Assert.That(created, Is.Empty); Assert.That(removed, Is.Empty);
        }
        [Test] public void DuplicateIdentityCreateRemoveConflictAndWrongRoundAreRejected()
        {
            var body = Body();
            Assert.Throws<InvalidDataException>(() => BlockDynamicCodec.Encode(7, new[] { body, body }, Array.Empty<ulong>()));
            Assert.Throws<InvalidDataException>(() => BlockDynamicCodec.Encode(7, new[] { body }, new[] { body.Id }));
            Assert.Throws<InvalidDataException>(() => BlockDynamicCodec.Encode(8, new[] { body }, Array.Empty<ulong>()));
            body.Position.x = float.NaN;
            Assert.Throws<InvalidDataException>(() => BlockDynamicCodec.Encode(7, new[] { body }, Array.Empty<ulong>()));
        }
        [Test] public void MalformedPayloadCannotPartiallyReplaceTheCallersState()
        {
            byte[] bytes = BlockDynamicCodec.Encode(7, new[] { Body() }, Array.Empty<ulong>());
            var created = new List<BlockDynamicState> { Body(99) }; var removed = new List<ulong> { 98 };
            // The count is checked against exact payload length before any allocation or output mutation.
            bytes[4] = 255; bytes[5] = 255; bytes[6] = 255; bytes[7] = 127;
            Assert.Throws<InvalidDataException>(() => BlockDynamicCodec.Decode(bytes, 7, created, removed));
            Assert.That(created[0].Id, Is.EqualTo(99)); CollectionAssert.AreEqual(new ulong[] { 98 }, removed);
            bytes = BlockDynamicCodec.Encode(7, new[] { Body() }, Array.Empty<ulong>());
            Array.Resize(ref bytes, bytes.Length + 1);
            Assert.Throws<InvalidDataException>(() => BlockDynamicCodec.Decode(bytes, 7, created, removed));
            Assert.That(created[0].Id, Is.EqualTo(99));
        }
        static BlockDynamicFragment Fragment(byte[] bytes, int index)
        {
            int size = Math.Min(BlockDynamicFragment.PayloadBytes, bytes.Length - index * BlockDynamicFragment.PayloadBytes);
            var data = new byte[size]; Buffer.BlockCopy(bytes, index * BlockDynamicFragment.PayloadBytes, data, 0, size);
            return new BlockDynamicFragment { Round = 7, Transfer = 2, BaseRevision = 5, Revision = 8, Tick = 90, Sequence = 100,
                Full = false, Frozen = false, Length = bytes.Length, Index = index, Data = data, Hash = BlockTransferFragment.Checksum(bytes) };
        }
        [Test] public void LifecycleFragmentsDoNotMixSequencesRoundsFreezeFlagsOrTransferBaselines()
        {
            var states = new BlockDynamicState[20]; for (int i = 0; i < states.Length; i++) states[i] = Body((ulong)i + 1);
            byte[] bytes = BlockDynamicCodec.Encode(7, states, Array.Empty<ulong>());
            var assembly = new BlockDynamicAssembly(Fragment(bytes, 1));
            var changed = Fragment(bytes, 0); changed.Sequence++; Assert.That(assembly.Add(changed), Is.False);
            changed = Fragment(bytes, 0); changed.Round++; Assert.That(assembly.Add(changed), Is.False);
            changed = Fragment(bytes, 0); changed.Tick++; Assert.That(assembly.Add(changed), Is.False);
            changed = Fragment(bytes, 0); changed.Frozen = true; Assert.That(assembly.Add(changed), Is.False);
            changed = Fragment(bytes, 0); changed.BaseRevision--; Assert.That(assembly.Add(changed), Is.False);
            Assert.That(assembly.Add(Fragment(bytes, 1)), Is.True); Assert.That(assembly.Add(Fragment(bytes, 1)), Is.True);
            Assert.That(assembly.ReceivedCount, Is.EqualTo(1)); Assert.That(assembly.Verify(), Is.False);
            Assert.That(assembly.Add(Fragment(bytes, 0)), Is.True); Assert.That(assembly.Verify(), Is.True);
            CollectionAssert.AreEqual(bytes, assembly.Bytes);
            var corrupt = new BlockDynamicAssembly(Fragment(bytes, 0)); changed = Fragment(bytes, 0); changed.Data[10] ^= 1;
            corrupt.Add(changed); corrupt.Add(Fragment(bytes, 1)); Assert.That(corrupt.Verify(), Is.False);
        }
        [Test] public void LargestMotionBatchRoundTripsWithinOneUnreliablePacket()
        {
            var states = new BlockDynamicState[BlockDynamicCodec.MotionBatchSize];
            for (int i = 0; i < states.Length; i++) states[i] = Body((ulong)i + 1);
            states[states.Length - 1].Sleeping = true;
            var expected = new BlockDynamicMotion { Round = 7, Revision = 12, Tick = 390, Sequence = 400, States = states };
            using var writer = new FastBufferWriter(1000, Allocator.Temp); writer.WriteNetworkSerializable(expected);
            Assert.That(writer.Length, Is.LessThan(900), "Leave space for NGO and transport headers.");
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out BlockDynamicMotion actual);
            Assert.That(actual.Revision, Is.EqualTo(12)); Assert.That(actual.Sequence, Is.EqualTo(400)); Assert.That(actual.Tick, Is.EqualTo(390));
            Assert.That(actual.States.Length, Is.EqualTo(states.Length));
            Assert.That(actual.States[0].Velocity, Is.EqualTo(states[0].Velocity));
            Assert.That(actual.States[states.Length - 1].Sleeping, Is.True);
        }
    }
}
