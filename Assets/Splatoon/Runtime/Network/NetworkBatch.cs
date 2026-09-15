using System;
using System.Buffers;
using System.Collections.Generic;
using Unity.Netcode;

namespace Splatoon.Networking
{
    /// <summary>A synchronous send view; only deserialized instances own a rented buffer.</summary>
    public struct NetworkBatch<T> : INetworkSerializable, IDisposable where T : struct, INetworkSerializable
    {
        IReadOnlyList<T> _source;
        T[] _received;
        int _offset;
        public bool SuppressMetrics;
        public int Count { get; private set; }
        public T this[int index] => index >= 0 && index < Count
            ? _received != null ? _received[index] : _source[_offset + index]
            : throw new ArgumentOutOfRangeException(nameof(index));

        public NetworkBatch(IReadOnlyList<T> source, int offset = 0, int count = -1)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _received = null; _offset = offset; Count = count < 0 ? source.Count - offset : count; SuppressMetrics = false;
            if (offset < 0 || Count < 0 || offset > source.Count - Count) throw new ArgumentOutOfRangeException(nameof(count));
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer) where TReaderWriter : IReaderWriter
        {
            int count = Count, start = serializer.IsWriter ? serializer.GetFastBufferWriter().Position : 0;
            serializer.SerializeValue(ref count);
            if (serializer.IsReader)
            {
                // Every supported record contains at least one byte. Validate before renting.
                var reader = serializer.GetFastBufferReader();
                if (count < 0 || count > 65536 || count > reader.Length - reader.Position)
                    throw new InvalidOperationException("Invalid network batch length.");
                Count = count; _source = null; _offset = 0;
                _received = count == 0 ? null : ArrayPool<T>.Shared.Rent(count);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        T item = default;
                        serializer.SerializeValue(ref item);
                        _received[i] = item;
                    }
                }
                catch { Dispose(); throw; }
            }
            else
            {
                for (int i = 0; i < count; i++) { var item = this[i]; serializer.SerializeValue(ref item); }
                if (!SuppressMetrics) NetworkTrafficCounters.Record(NetworkBatchTraffic<T>.Kind, serializer.GetFastBufferWriter().Position - start);
            }
        }

        public void Dispose()
        {
            if (_received != null) ArrayPool<T>.Shared.Return(_received, true);
            _received = null; _source = null; _offset = Count = 0;
        }
    }

    /// <summary>Byte slice with the same ownership rules as NetworkBatch.</summary>
    public struct NetworkBytes : INetworkSerializable, IDisposable
    {
        public byte[] Buffer { get; private set; }
        public int Offset { get; private set; }
        public int Count { get; private set; }
        bool _rented;

        public NetworkBytes(byte[] buffer, int offset, int count)
        {
            if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Buffer = buffer; Offset = offset; Count = count; _rented = false;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            int count = Count;
            serializer.SerializeValue(ref count);
            if (serializer.IsReader)
            {
                var reader = serializer.GetFastBufferReader();
                if (count < 0 || count > 8192 || count > reader.Length - reader.Position) throw new InvalidOperationException("Invalid network byte slice.");
                Buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, count)); Offset = 0; Count = count; _rented = true;
                try { var bytes = Buffer; reader.ReadBytesSafe(ref bytes, count); }
                catch { Dispose(); throw; }
            }
            else serializer.GetFastBufferWriter().WriteBytesSafe(Buffer, Count, Offset);
        }

        public void Dispose()
        {
            if (_rented && Buffer != null) ArrayPool<byte>.Shared.Return(Buffer);
            Buffer = null; Offset = Count = 0; _rented = false;
        }
    }
}
