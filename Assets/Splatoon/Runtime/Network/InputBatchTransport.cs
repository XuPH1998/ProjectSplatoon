using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;

namespace Splatoon.Networking
{
    public struct InputBatchPart : INetworkSerializable, IDisposable
    {
        public uint Batch, Revision;
        public byte PartIndex, PartCount, FrameOffset, TotalFrames;
        public NetworkBatch<PlayerInputFrame> Frames;
        public const int HeaderBytes = 16; // Two uints, four bytes, and the counted batch length.

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            int start = s.IsWriter ? s.GetFastBufferWriter().Position : 0;
            s.SerializeValue(ref Batch); s.SerializeValue(ref Revision);
            s.SerializeValue(ref PartIndex); s.SerializeValue(ref PartCount);
            s.SerializeValue(ref FrameOffset); s.SerializeValue(ref TotalFrames);
            s.SerializeValue(ref Frames);
            if (s.IsWriter) NetworkTrafficCounters.Record(NetworkTrafficKind.Input, s.GetFastBufferWriter().Position - start);
        }
        public void Dispose() => Frames.Dispose();
    }

    public sealed class InputBatchSender : IDisposable
    {
        public const int MaxFrames = 32;
        readonly PlayerInputFrame[] _frames = new PlayerInputFrame[MaxFrames];
        readonly byte[] _offsets = new byte[MaxFrames], _counts = new byte[MaxFrames];
        FastBufferWriter _measure;
        uint _batch;
        int _total, _partCount;
        public int PartCount => _partCount;

        public void Prepare(IReadOnlyList<PlayerInputFrame> history, int mtu)
        {
            _partCount = 0; _total = Math.Min(history.Count, MaxFrames);
            if (_total == 0) return;
            if (!_measure.IsInitialized) _measure = new FastBufferWriter(256, Allocator.Persistent, 4096);
            int budget = Math.Min(1200, mtu - 96), used = InputBatchPart.HeaderBytes, start = 0;
            for (int i = 0; i < _total; i++)
            {
                _frames[i] = history[i];
                _measure.Seek(0); _measure.Truncate(0); _measure.WriteNetworkSerializable(_frames[i]);
                int size = _measure.Length;
                if (size + InputBatchPart.HeaderBytes > budget) throw new InvalidOperationException("NGO MTU cannot fit one input frame.");
                if (used + size > budget)
                {
                    _offsets[_partCount] = (byte)start; _counts[_partCount++] = (byte)(i - start);
                    start = i; used = InputBatchPart.HeaderBytes;
                }
                used += size;
            }
            _offsets[_partCount] = (byte)start; _counts[_partCount++] = (byte)(_total - start);
            _batch++;
        }

        public InputBatchPart GetPart(int index)
        {
            if (index < 0 || index >= _partCount) throw new ArgumentOutOfRangeException(nameof(index));
            return new InputBatchPart { Batch = _batch, Revision = _frames[0].Revision,
                PartIndex = (byte)index, PartCount = (byte)_partCount, TotalFrames = (byte)_total, FrameOffset = _offsets[index],
                Frames = new NetworkBatch<PlayerInputFrame>(_frames, _offsets[index], _counts[index]) { SuppressMetrics = true } };
        }
        public void Dispose() { if (_measure.IsInitialized) _measure.Dispose(); _measure = default; }
    }

    public sealed class InputBatchAssembler
    {
        public const int Capacity = 8;
        sealed class Pending
        {
            public uint Batch, Revision, Parts, Frames;
            public int Total, PartCount;
            public double Started;
            public bool Active;
            public readonly PlayerInputFrame[] Values = new PlayerInputFrame[InputBatchSender.MaxFrames];
        }
        readonly Pending[] _pending = new Pending[Capacity];
        public InputBatchAssembler() { for (int i = 0; i < Capacity; i++) _pending[i] = new Pending(); }
        public int PendingCount { get { int n = 0; foreach (var p in _pending) if (p.Active) n++; return n; } }
        public void Clear() { foreach (var p in _pending) p.Active = false; }
        public void Expire(double now, double timeout)
        { foreach (var p in _pending) if (p.Active && now - p.Started > timeout) p.Active = false; }

        // The returned array is borrowed until the next Add; the caller consumes it synchronously.
        public bool Add(InputBatchPart part, uint revision, double now, double timeout, out PlayerInputFrame[] frames, out int count)
        {
            frames = null; count = 0; Expire(now, timeout);
            if (part.Revision != revision || part.TotalFrames == 0 || part.TotalFrames > InputBatchSender.MaxFrames ||
                part.PartCount == 0 || part.PartCount > part.TotalFrames || part.PartIndex >= part.PartCount ||
                part.Frames.Count == 0 || part.FrameOffset + part.Frames.Count > part.TotalFrames) return false;
            Pending target = null, free = null, oldest = null;
            foreach (var p in _pending)
            {
                if (p.Active && p.Batch == part.Batch && p.Revision == revision) target = p;
                if (!p.Active && free == null) free = p;
                if (p.Active && (oldest == null || p.Started < oldest.Started)) oldest = p;
            }
            if (target == null)
            {
                target = free ?? oldest;
                target.Active = true; target.Batch = part.Batch; target.Revision = revision; target.Started = now;
                target.Total = part.TotalFrames; target.PartCount = part.PartCount; target.Parts = target.Frames = 0;
            }
            if (target.Total != part.TotalFrames || target.PartCount != part.PartCount) { target.Active = false; return false; }
            uint partBit = 1u << part.PartIndex;
            if ((target.Parts & partBit) != 0) return false;
            for (int i = 0; i < part.Frames.Count; i++)
            {
                uint bit = 1u << (part.FrameOffset + i);
                if ((target.Frames & bit) != 0 || part.Frames[i].Revision != revision) { target.Active = false; return false; }
                target.Values[part.FrameOffset + i] = part.Frames[i]; target.Frames |= bit;
            }
            target.Parts |= partBit;
            uint expectedParts = target.PartCount == 32 ? uint.MaxValue : (1u << target.PartCount) - 1;
            uint expectedFrames = target.Total == 32 ? uint.MaxValue : (1u << target.Total) - 1;
            if (target.Parts != expectedParts || target.Frames != expectedFrames) return false;
            target.Active = false;
            for (int i = 1; i < target.Total; i++) if (target.Values[i].Sequence <= target.Values[i - 1].Sequence) return false;
            frames = target.Values; count = target.Total; return true;
        }
    }
}
