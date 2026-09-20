#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public class NetworkOptimizationTests
    {
        static List<PlayerInputFrame> Inputs(int count) => Enumerable.Range(1, count).Select(i => new PlayerInputFrame {
            Sequence = (uint)i, Revision = 7, Tick = (uint)i, HeroRevision = 3, FireSequence = (uint)i,
            JumpSequence = (uint)(i / 4), ReleaseSequence = (uint)(i / 3), Fire = i % 2 == 0, Swim = i % 3 == 0,
            CancelFire = i % 5 == 0, Move = new Vector2(i % 2, 0), Look = new Vector2(i, -12) }).ToList();
        static byte[] Canonical<T>(T value) where T : INetworkSerializable
        { using var writer = new FastBufferWriter(8192, Allocator.Temp); writer.WriteNetworkSerializable(value); return writer.ToArray(); }
        static void DeltaRoundTrip(PlayerSnapshot before, PlayerSnapshot next, out int size, out byte mode)
        {
            using var writer = new FastBufferWriter(8192, Allocator.Temp);
            PlayerSnapshotDelta.WriteDelta(writer, next, before); size = writer.Length;
            using (var peek = new FastBufferReader(writer, Allocator.None)) peek.ReadByteSafe(out mode);
            using var reader = new FastBufferReader(writer, Allocator.None);
            PlayerSnapshotDelta.ReadDelta(reader, ref before);
            CollectionAssert.AreEqual(Canonical(next), Canonical(before));
            Assert.That(reader.Position, Is.EqualTo(reader.Length));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(27)] [TestCase(28)] [TestCase(32)] [TestCase(120)]
        public void InputPartsFitMtuAndAtomicallyRestoreOldest32(int historyCount)
        {
            var history = Inputs(historyCount); using var sender = new InputBatchSender(); var receiver = new InputBatchAssembler();
            sender.Prepare(history, 1296);
            if (historyCount == 0) { Assert.That(sender.PartCount, Is.Zero); return; }
            PlayerInputFrame[] result = null; int resultCount = 0;
            for (int part = sender.PartCount - 1; part >= 0; part--)
            {
                using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(sender.GetPart(part));
                Assert.That(writer.Length, Is.LessThanOrEqualTo(1200));
                using var reader = new FastBufferReader(writer, Allocator.None); reader.ReadNetworkSerializable(out InputBatchPart copy);
                try
                {
                    bool ready = receiver.Add(copy, 7, 1, .3, out result, out resultCount);
                    Assert.That(ready, Is.EqualTo(part == 0));
                    if (!ready) Assert.That(receiver.Add(copy, 7, 1.01, .3, out _, out _), Is.False, "Duplicate part must not complete a batch.");
                }
                finally { copy.Dispose(); }
            }
            Assert.That(resultCount, Is.EqualTo(Math.Min(32, historyCount)));
            for (int i = 0; i < resultCount; i++) CollectionAssert.AreEqual(Canonical(history[i]), Canonical(result[i]));
        }

        [Test]
        public void LostPartsExpireWithoutPartialInputsAndBoundMemory()
        {
            using var sender = new InputBatchSender(); var receiver = new InputBatchAssembler(); var history = Inputs(32);
            for (int i = 0; i < 20; i++)
            {
                sender.Prepare(history, 1296);
                Assert.That(receiver.Add(sender.GetPart(0), 7, i * .001, .3, out _, out _), Is.False);
                Assert.That(receiver.PendingCount, Is.LessThanOrEqualTo(8));
            }
            receiver.Expire(1, .3); Assert.That(receiver.PendingCount, Is.Zero);
            Assert.That(receiver.Add(sender.GetPart(1), 8, 1, .3, out _, out _), Is.False);
            Assert.That(receiver.PendingCount, Is.Zero);
            sender.Prepare(history, 1296);
            receiver.Add(sender.GetPart(0), 7, 1, .3, out _, out _); receiver.Clear();
            Assert.That(receiver.Add(sender.GetPart(1), 7, 1, .3, out _, out _), Is.False);
            Assert.That(receiver.Add(sender.GetPart(0), 7, 1, .3, out _, out int count), Is.True);
            Assert.That(count, Is.EqualTo(32));
        }

        [Test]
        public void FragmentValidationRejectsOverlapsAndUnorderedCommands()
        {
            using var sender = new InputBatchSender(); var receiver = new InputBatchAssembler(); var history = Inputs(32);
            sender.Prepare(history, 1296); var first = sender.GetPart(0); var overlap = sender.GetPart(1); overlap.FrameOffset = 0;
            receiver.Add(first, 7, 0, .3, out _, out _);
            Assert.That(receiver.Add(overlap, 7, 0, .3, out _, out _), Is.False);
            history[1] = history[0]; sender.Prepare(history, 1296); receiver.Clear();
            for (int i = 0; i < sender.PartCount; i++) Assert.That(receiver.Add(sender.GetPart(i), 7, 0, .3, out _, out _), Is.False);
        }

        [Test]
        public void DeltaCoversEveryCurrentPublicFieldWithoutQuantization()
        {
            foreach (var field in typeof(PlayerSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object boxed = new PlayerSnapshot(); var type = field.FieldType;
                object value = type == typeof(Vector2) ? new Vector2(.1234567f, -123.875f) :
                    type == typeof(Vector3) ? new Vector3(.1234567f, -123.875f, 9.25f) :
                    type == typeof(Quaternion) ? new Quaternion(.1f, .2f, .3f, .9f) :
                    type.IsEnum ? Enum.ToObject(type, 1) : Convert.ChangeType(type == typeof(bool) ? 1 : 7, type);
                field.SetValue(boxed, value);
                CollectionAssert.AreNotEqual(Canonical(default(PlayerSnapshot)), Canonical((PlayerSnapshot)boxed), "Field missing from canonical serialization: " + field.Name);
                DeltaRoundTrip(default, (PlayerSnapshot)boxed, out int size, out _);
                Assert.That(size, Is.LessThanOrEqualTo(Canonical((PlayerSnapshot)boxed).Length + 3), field.Name);
            }
            var special = new PlayerSnapshot { Health = float.NaN, Ink = float.PositiveInfinity, Position = new Vector3(-0f, float.NegativeInfinity, float.Epsilon), SimulatedAt = double.Epsilon };
            DeltaRoundTrip(default, special, out _, out _);
        }

        [Test]
        public void ConsecutiveDeltasUseNgoBaselineAndForceFullLifecycleSnapshots()
        {
            PlayerSnapshotDelta.Register();
            Assert.That(UserNetworkVariableSerialization<PlayerSnapshot>.ReadDelta, Is.Not.Null);
            var state = new PlayerSnapshot { HeroId = 1, Revision = 1, HeroRevision = 1, Health = 100, Ink = 100 };
            long full = 0, delta = 0;
            for (int i = 0; i < 600; i++)
            {
                var next = state; next.Position.x += .01325f; next.SimulatedAt += 1.0 / 30; next.SimulationTick += 2;
                next.AcknowledgedInput += 2; next.Ink = 100 - i % 90; next.Yaw = i % 360;
                DeltaRoundTrip(state, next, out int size, out byte mode); full += Canonical(next).Length; delta += size;
                Assert.That(mode, Is.EqualTo(1)); state = next;
            }
            var respawn = state; respawn.Revision++;
            DeltaRoundTrip(state, respawn, out _, out byte fullMode); Assert.That(fullMode, Is.Zero);
            var hero = state; hero.HeroRevision++;
            DeltaRoundTrip(state, hero, out _, out fullMode); Assert.That(fullMode, Is.Zero);
            PlayerSnapshotDelta.Register(); // Also exercises repeated startup with Domain Reload disabled.
            DeltaRoundTrip(state, state, out _, out _);
            Assert.That(delta, Is.LessThan(full));
            Directory.CreateDirectory("Reports/NetworkOptimization");
            File.WriteAllText("Reports/NetworkOptimization/codec-bytes.txt", $"fields={typeof(PlayerSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance).Length}\ncanonicalBytes={Canonical(state).Length}\nframes=600\nfullBytes={full}\ndeltaBytes={delta}\nratio={(double)delta / full:R}\n");
        }

        [Test]
        public void NgoNetworkVariableUsesDeltaAndRaisesCompleteSnapshotCallbacks()
        {
            NetworkVariableSerializationTypedInitializers.InitializeSerializer_UnmanagedINetworkSerializable<PlayerSnapshot>();
            NetworkVariableSerializationTypedInitializers.InitializeEqualityChecker_UnmanagedValueEquals<PlayerSnapshot>();
            PlayerSnapshotDelta.Register();
            var state = new PlayerSnapshot { Revision = 1, HeroRevision = 1, HeroId = 1, Health = 100 };
            using var sender = new NetworkVariable<PlayerSnapshot>(state);
            using var receiver = new NetworkVariable<PlayerSnapshot>();
            using var writer = new FastBufferWriter(4096, Allocator.Temp);
            sender.WriteField(writer);
            CollectionAssert.AreEqual(Canonical(state), writer.ToArray(), "Initial synchronization stays canonical.");
            using (var initial = new FastBufferReader(writer, Allocator.None)) receiver.ReadField(initial);
            sender.ResetDirty();
            int callbacks = 0;
            PlayerSnapshot expectedBefore = state, expectedAfter = state;
            receiver.OnValueChanged += (before, after) => {
                CollectionAssert.AreEqual(Canonical(expectedBefore), Canonical(before));
                CollectionAssert.AreEqual(Canonical(expectedAfter), Canonical(after)); callbacks++;
            };
            var postRead = typeof(NetworkVariable<PlayerSnapshot>).GetMethod("PostDeltaRead", BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < 20; i++)
            {
                expectedBefore = state; state.Position.x += .25f; state.SimulationTick += 2; state.SimulatedAt += 1.0 / 30;
                if (i == 7) state.Revision++;
                if (i == 13) state.HeroRevision++;
                expectedAfter = state; sender.Value = state;
                writer.Seek(0); writer.Truncate(0); sender.WriteDelta(writer);
                using (var delta = new FastBufferReader(writer, Allocator.None)) receiver.ReadDelta(delta, false);
                postRead.Invoke(receiver, null); sender.ResetDirty();
                CollectionAssert.AreEqual(Canonical(state), Canonical(receiver.Value));
            }
            Assert.That(callbacks, Is.EqualTo(20)); Assert.That(PlayerSnapshotDelta.EncodedBytes, Is.GreaterThan(0));
        }

        [Test]
        public void TruncatedDeltaDoesNotPartiallyMutateSnapshot()
        {
            var state = new PlayerSnapshot { Revision = 4, Health = 100 }; var next = state; next.Position = Vector3.one;
            using var writer = new FastBufferWriter(4096, Allocator.Temp); PlayerSnapshotDelta.WriteDelta(writer, next, state);
            var bytes = writer.ToArray(); using var reader = new FastBufferReader(bytes, Allocator.Temp, bytes.Length - 1);
            var before = Canonical(state);
            Assert.Throws<OverflowException>(() => PlayerSnapshotDelta.ReadDelta(reader, ref state));
            CollectionAssert.AreEqual(before, Canonical(state));
        }

        [Test]
        public void FullFallbackAndInitialSynchronizationRemainCanonical()
        {
            object boxed = new PlayerSnapshot();
            float denseFloat = BitConverter.Int32BitsToSingle(0x3f123456);
            foreach (var field in typeof(PlayerSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var type = field.FieldType;
                object value = type == typeof(Vector2) ? new Vector2(denseFloat, denseFloat) :
                    type == typeof(Vector3) ? new Vector3(denseFloat, denseFloat, denseFloat) :
                    type == typeof(Quaternion) ? new Quaternion(denseFloat, denseFloat, denseFloat, denseFloat) :
                    type == typeof(float) ? denseFloat : type == typeof(double) ? BitConverter.Int64BitsToDouble(0x3ff123456789abcd) :
                    type == typeof(ulong) ? 0x1234567812345678UL : type == typeof(long) ? 0x1234567812345678L :
                    type.IsEnum ? Enum.ToObject(type, 1) : Convert.ChangeType(type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool) ? 1 : 0x12345678, type);
                field.SetValue(boxed, value);
            }
            var next = (PlayerSnapshot)boxed;
            var before = new PlayerSnapshot { Revision = next.Revision, HeroId = next.HeroId, HeroRevision = next.HeroRevision };
            DeltaRoundTrip(before, next, out int size, out byte mode);
            Assert.That(mode, Is.Zero); Assert.That(size, Is.EqualTo(Canonical(next).Length + 3));
            using var initial = new FastBufferReader(Canonical(next), Allocator.Temp);
            initial.ReadNetworkSerializable(out PlayerSnapshot restored);
            DeltaRoundTrip(restored, next, out _, out mode); Assert.That(mode, Is.EqualTo(1));
        }

        [Test]
        public void CountedBatchSerializesOnlyTheSliceAndReturnsReadBuffers()
        {
            var inputs = Inputs(32); var send = new NetworkBatch<PlayerInputFrame>(inputs, 9, 2);
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(send);
            Assert.That(writer.Length, Is.EqualTo(4 + 56 * 2));
            using var reader = new FastBufferReader(writer, Allocator.None); reader.ReadNetworkSerializable(out NetworkBatch<PlayerInputFrame> received);
            Assert.That(received.Count, Is.EqualTo(2)); Assert.That(received[0].Sequence, Is.EqualTo(10));
            received.Dispose(); send.Dispose(); Assert.That(inputs.Count, Is.EqualTo(32));
        }

        [Test]
        public void LargeEventBatchesPreserveEveryEventAndPooledTailIsNotSerialized()
        {
            CheckEventBatch(Enumerable.Range(1, 1000).Select(i => new Splatoon.Combat.InkShot { Id = (uint)i, Born = i * .125 }).ToList());
            CheckEventBatch(Enumerable.Range(1, 1000).Select(i => new Splatoon.Combat.InkImpact { Id = (uint)i, Position = new Vector3(i, 2, 3) }).ToList());
            CheckEventBatch(Enumerable.Range(1, 1000).Select(i => new PaintStamp { Sequence = (uint)i, Radius = i * .125f }).ToList());
        }
        static void CheckEventBatch<T>(List<T> source) where T : struct, INetworkSerializable
        {
            using var writer = new FastBufferWriter(256, Allocator.Temp, 262144);
            writer.WriteNetworkSerializable(new NetworkBatch<T>(source));
            using var reader = new FastBufferReader(writer, Allocator.None);
            reader.ReadNetworkSerializable(out NetworkBatch<T> received);
            try
            {
                Assert.That(received.Count, Is.EqualTo(source.Count));
                for (int i = 0; i < source.Count; i++) CollectionAssert.AreEqual(Canonical(source[i]), Canonical(received[i]));
            }
            finally { received.Dispose(); }
            writer.Seek(0); writer.Truncate(0); writer.WriteNetworkSerializable(new NetworkBatch<T>(source, 2, 1));
            Assert.That(writer.Length, Is.EqualTo(4 + Canonical(source[2]).Length));
        }

        [Test]
        public void WarmInputBatchRoundTripsAllocateNoManagedArrays()
        {
            var inputs = Inputs(32); using var sender = new InputBatchSender();
            using var writer = new FastBufferWriter(4096, Allocator.Temp);
            long start = 0, allocated = 0;
            for (int i = 0; i < 110; i++)
            {
                if (i == 10) start = GC.GetAllocatedBytesForCurrentThread();
                sender.Prepare(inputs, 1296);
                for (int p = 0; p < sender.PartCount; p++)
                {
                    writer.Seek(0); writer.Truncate(0); writer.WriteNetworkSerializable(sender.GetPart(p));
                    using var reader = new FastBufferReader(writer, Allocator.None); reader.ReadNetworkSerializable(out InputBatchPart part); part.Dispose();
                }
                if (i == 109) allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            }
            Assert.That(allocated, Is.Zero);
        }

        [TestCase(1)] [TestCase(8)]
        public void SnapshotWindowRequiresRealProgressAndBoundsInflight(int limit)
        {
            var window = new SnapshotSendWindow(40, limit, 0);
            Assert.That(window.Acknowledge(0, 100), Is.False); // Unsent ACK must not renew a stalled transfer.
            for (int i = 0; i < limit; i++) { Assert.That(window.TryPeek(out int index), Is.True); Assert.That(index, Is.EqualTo(i)); window.Sent(index, 4096); }
            Assert.That(window.TryPeek(out _), Is.False); Assert.That(window.InFlightBytes, Is.EqualTo(4096 * limit));
            window.Retry(0); window.Retry(0); Assert.That(window.TryPeek(out int retry), Is.True); window.Sent(retry, 4096);
            Assert.That(window.InFlight, Is.EqualTo(limit));
            Assert.That(window.Acknowledge(0, 39), Is.True); Assert.That(window.Acknowledge(0, 1000), Is.False);
            Assert.That(window.TimedOut(78, 40), Is.False); Assert.That(window.TimedOut(80, 40), Is.True);
            Assert.That(window.TryPeek(out _), Is.True);
        }

        [TestCase(15)] [TestCase(60)] [TestCase(144)]
        public void SnapshotBudgetUsesTimeAndNeverExceedsRatePlusBurst(int fps)
        {
            var budget = new SnapshotRateBudget(); long sent = 0;
            for (int frame = 0; frame <= fps * 10; frame++)
            {
                double now = (double)frame / fps; budget.Refill(now, 262144);
                for (int i = 0; i < 8 && budget.TrySpend(4096); i++) sent += 4096;
                Assert.That(sent, Is.LessThanOrEqualTo(32768 + now * 262144 + .01));
            }
            budget.Refill(1000, 262144); Assert.That(budget.Available, Is.EqualTo(32768));
        }

        [Test]
        public void RegularAcknowledgementsAllowTransfersLongerThanTwoConnectionTimeouts()
        {
            var window = new SnapshotSendWindow(40, 8, 0);
            for (int record = 0; record < 40; record++)
            {
                double now = record * 5;
                Assert.That(window.TimedOut(now, 40), Is.False, "Progress, not total transfer duration, controls timeout.");
                Assert.That(window.TryPeek(out int index), Is.True);
                window.Sent(index, 4096);
                Assert.That(window.Acknowledge(index, now + 4), Is.True);
            }
            Assert.That(window.Complete, Is.True);
            Assert.That(window.LastProgress, Is.GreaterThan(40));
            Assert.That(window.InFlightBytes, Is.Zero);
        }

        [Test]
        public void SnapshotRecordsPreservePinnedHistoryAndCheckpointBytes()
        {
            var checkpoint = Enumerable.Range(0, 20000).Select(i => (byte)(i * 17)).ToArray();
            var source = Enumerable.Range(11, 300).Select(i => new PaintStamp { Sequence = (uint)i, Round = 2, Radius = i * .01f, ShapeSeed = (uint)i, SurfaceId = 1 }).ToList();
            var journal = source.ToArray(); source.Clear(); // A new checkpoint has pruned the shared journal.
            var layout = new SnapshotTransferLayout(new SnapshotManifest { Id = 5, Round = 2, Sequence = 10, EndSequence = 310, JournalCount = 300, Length = checkpoint.Length, RecordBytes = 4096 });
            var restored = new byte[checkpoint.Length]; var restoredStamps = new List<PaintStamp>();
            using var scratch = new FastBufferWriter(8192, Allocator.Temp);
            for (int i = 0; i < layout.TotalRecords; i++)
            {
                var record = layout.Record(i, checkpoint, journal, scratch);
                using var writer = new FastBufferWriter(8192, Allocator.Temp); writer.WriteNetworkSerializable(record);
                Assert.That(writer.Length, Is.EqualTo(layout.SizeAt(i))); Assert.That(writer.Length, Is.LessThanOrEqualTo(4096));
                using var reader = new FastBufferReader(writer, Allocator.None); reader.ReadNetworkSerializable(out SnapshotRecord received);
                try
                {
                    Assert.That(SnapshotTransferLayout.Hash(received, scratch), Is.EqualTo(record.Hash));
                    if (received.IsJournal) for (int n = 0; n < received.Stamps.Count; n++) restoredStamps.Add(received.Stamps[n]);
                    else Buffer.BlockCopy(received.Bytes.Buffer, received.Bytes.Offset, restored, (i - layout.JournalRecords) * layout.DataBytes, received.Bytes.Count);
                }
                finally { received.Dispose(); }
            }
            CollectionAssert.AreEqual(checkpoint, restored); Assert.That(restoredStamps.Count, Is.EqualTo(journal.Length));
            for (int i = 0; i < journal.Length; i++) CollectionAssert.AreEqual(Canonical(journal[i]), Canonical(restoredStamps[i]));
        }
    }
}
#endif
