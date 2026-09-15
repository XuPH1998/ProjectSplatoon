using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Splatoon.Painting;

namespace Splatoon.Networking
{
    public sealed class SnapshotRateBudget
    {
        public const int BurstBytes = 32768;
        double _tokens, _time;
        bool _initialized;
        public double Available => _tokens;
        public void Refill(double now, int bytesPerSecond)
        {
            if (!_initialized) { _initialized = true; _tokens = BurstBytes; _time = now; return; }
            _tokens = Math.Min(BurstBytes, _tokens + Math.Max(0, now - _time) * bytesPerSecond); _time = now;
        }
        public bool TrySpend(int bytes)
        {
            if (bytes < 0 || bytes > BurstBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
            if (_tokens + 1e-7 < bytes) return false;
            _tokens = Math.Max(0, _tokens - bytes); return true;
        }
        public void Reset() { _tokens = _time = 0; _initialized = false; }
    }

    public sealed class SnapshotSendWindow
    {
        readonly bool[] _sent, _acked, _retryQueued;
        readonly int[] _sizes;
        readonly Queue<int> _retry;
        readonly int _limit;
        int _next, _ackCount;
        public int InFlight { get; private set; }
        public long InFlightBytes { get; private set; }
        public double LastProgress { get; private set; }
        public bool Complete => _ackCount == _sent.Length;
        public SnapshotSendWindow(int records, int limit, double now)
        {
            if (records <= 0 || limit < 1 || limit > 32) throw new ArgumentOutOfRangeException(nameof(records));
            _sent = new bool[records]; _acked = new bool[records]; _retryQueued = new bool[records]; _sizes = new int[records];
            _retry = new Queue<int>(limit); _limit = limit; LastProgress = now;
        }
        public bool TryPeek(out int index)
        {
            while (_retry.Count > 0 && _acked[_retry.Peek()]) _retryQueued[_retry.Dequeue()] = false;
            if (_retry.Count > 0) { index = _retry.Peek(); return true; }
            index = _next; return _next < _sent.Length && InFlight < _limit;
        }
        public void Sent(int index, int bytes)
        {
            if (index < 0 || index >= _sent.Length || bytes <= 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (_sent[index])
            {
                if (_retry.Count == 0 || _retry.Peek() != index) throw new InvalidOperationException("Unexpected snapshot retry.");
                _retry.Dequeue(); _retryQueued[index] = false; return;
            }
            if (index != _next || InFlight >= _limit) throw new InvalidOperationException("Snapshot send window exhausted.");
            _sent[index] = true; _sizes[index] = bytes; _next++; InFlight++; InFlightBytes += bytes;
        }
        public bool Acknowledge(int index, double now)
        {
            if (index < 0 || index >= _sent.Length || !_sent[index] || _acked[index]) return false;
            _acked[index] = true; _ackCount++; InFlight--; InFlightBytes -= _sizes[index]; LastProgress = now; return true;
        }
        public void Retry(int index)
        {
            if (index < 0 || index >= _sent.Length || !_sent[index] || _acked[index] || _retryQueued[index]) return;
            _retry.Enqueue(index); _retryQueued[index] = true;
        }
        public bool TimedOut(double now, double timeout) => now - LastProgress > timeout;
    }

    public struct SnapshotManifest : INetworkSerializable
    {
        public uint Round, Id, Hash, Sequence, EndSequence;
        public int Length, JournalCount, RecordBytes;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Round); s.SerializeValue(ref Id); s.SerializeValue(ref Hash);
            s.SerializeValue(ref Sequence); s.SerializeValue(ref EndSequence);
            s.SerializeValue(ref Length); s.SerializeValue(ref JournalCount); s.SerializeValue(ref RecordBytes);
        }
    }

    public struct SnapshotRecord : INetworkSerializable, IDisposable
    {
        public uint Round, TransferId, Hash;
        public int Index;
        public bool IsJournal;
        public NetworkBytes Bytes;
        public NetworkBatch<PaintStamp> Stamps;
        public const int HeaderBytes = 21; // 3 uints + int + bool + counted payload length.
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            int start = s.IsWriter ? s.GetFastBufferWriter().Position : 0;
            s.SerializeValue(ref Round); s.SerializeValue(ref TransferId); s.SerializeValue(ref Hash);
            s.SerializeValue(ref Index); s.SerializeValue(ref IsJournal);
            if (IsJournal) s.SerializeValue(ref Stamps); else s.SerializeValue(ref Bytes);
            if (s.IsWriter) NetworkTrafficCounters.Record(IsJournal ? NetworkTrafficKind.SnapshotJournal : NetworkTrafficKind.SnapshotData, s.GetFastBufferWriter().Position - start);
        }
        public void Dispose() { Bytes.Dispose(); Stamps.Dispose(); }
    }

    public sealed class SnapshotTransferLayout
    {
        public readonly SnapshotManifest Manifest;
        public readonly int DataBytes, StampsPerRecord, JournalRecords, DataRecords, TotalRecords, StampBytes;
        public SnapshotTransferLayout(SnapshotManifest manifest)
        {
            if (manifest.Length <= 0 || manifest.RecordBytes < 512 || manifest.RecordBytes > 8192 || manifest.JournalCount < 0 ||
                manifest.EndSequence < manifest.Sequence || (long)manifest.EndSequence - manifest.Sequence != manifest.JournalCount)
                throw new InvalidOperationException("Invalid snapshot manifest.");
            Manifest = manifest;
            using var writer = new FastBufferWriter(256, Allocator.Temp); writer.WriteNetworkSerializable(default(PaintStamp));
            StampBytes = writer.Length; DataBytes = manifest.RecordBytes - SnapshotRecord.HeaderBytes;
            StampsPerRecord = DataBytes / StampBytes;
            JournalRecords = (int)(((long)manifest.JournalCount + StampsPerRecord - 1) / StampsPerRecord);
            DataRecords = (int)(((long)manifest.Length + DataBytes - 1) / DataBytes);
            TotalRecords = checked(JournalRecords + DataRecords);
        }
        public int CountAt(int index) => index < JournalRecords
            ? Math.Min(StampsPerRecord, Manifest.JournalCount - index * StampsPerRecord)
            : Math.Min(DataBytes, Manifest.Length - (index - JournalRecords) * DataBytes);
        public int SizeAt(int index) => SnapshotRecord.HeaderBytes + CountAt(index) * (index < JournalRecords ? StampBytes : 1);
        public SnapshotRecord Record(int index, byte[] checkpoint, PaintStamp[] journal, FastBufferWriter scratch)
        {
            if (index < 0 || index >= TotalRecords) throw new ArgumentOutOfRangeException(nameof(index));
            var record = new SnapshotRecord { Round = Manifest.Round, TransferId = Manifest.Id, Index = index, IsJournal = index < JournalRecords };
            int count = CountAt(index);
            if (record.IsJournal) record.Stamps = new NetworkBatch<PaintStamp>(journal, index * StampsPerRecord, count) { SuppressMetrics = true };
            else record.Bytes = new NetworkBytes(checkpoint, (index - JournalRecords) * DataBytes, count);
            record.Hash = Hash(record, scratch); return record;
        }
        public static uint Hash(SnapshotRecord record, FastBufferWriter scratch)
        {
            if (!record.IsJournal) return PaintSnapshotCodec.Hash(record.Bytes.Buffer, record.Bytes.Offset, record.Bytes.Count);
            scratch.Seek(0); scratch.Truncate(0);
            for (int i = 0; i < record.Stamps.Count; i++) scratch.WriteNetworkSerializable(record.Stamps[i]);
            using var reader = new FastBufferReader(scratch, Allocator.None);
            uint hash = 2166136261;
            while (reader.Position < reader.Length) { reader.ReadByteSafe(out byte b); hash = unchecked((hash ^ b) * 16777619); }
            return hash;
        }
    }
}
