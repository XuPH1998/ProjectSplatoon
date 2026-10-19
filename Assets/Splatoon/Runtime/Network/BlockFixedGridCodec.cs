using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Networking
{
    /// <summary>A single reliable ownership transaction: fixed cells and zero-thickness surface paint.</summary>
    public static class BlockFixedGridCodec
    {
        public const int ProtocolVersion = 2; // Bounded reliable streams require paired commit and fragment-progress receipts.
        public const int HeaderBytes = 54, StateBytes = 61, HandleBytes = 20;
        public const int MaxBlocks = 1000000, MaxTransferBytes = 128 * 1024 * 1024;
        const uint Magic = 0x31474642; // BFG1; PhysX body and old terrain payloads are deliberately incompatible.

        static void WriteHandle(BinaryWriter writer, BlockFixedGridHandle value)
        { writer.Write(value.Epoch); writer.Write(value.Round); writer.Write(value.Generation); writer.Write(value.Id); }
        static BlockFixedGridHandle ReadHandle(BinaryReader reader) => new()
        { Epoch = reader.ReadUInt32(), Round = reader.ReadUInt32(), Generation = reader.ReadUInt32(), Id = reader.ReadUInt64() };
        static bool ValidHandle(BlockFixedGridHandle value, BlockFixedGridSnapshot snapshot)
            => value.Epoch == snapshot.Epoch && value.Round == snapshot.Round && value.Id != 0 && value.Generation != 0;
        static void WriteState(BinaryWriter writer, BlockFixedGridState value)
        {
            WriteHandle(writer, value.Handle);
            writer.Write(value.Cell.x); writer.Write(value.Cell.y); writer.Write(value.Cell.z); writer.Write(value.Owner);
            writer.Write(value.Support.Id); writer.Write(value.Support.BaseCellY); writer.Write(value.Support.Height);
            writer.Write(value.Support.SurfaceKey.FaceId); writer.Write(value.Support.SurfaceKey.U); writer.Write(value.Support.SurfaceKey.V);
            writer.Write(value.Revision);
        }
        static BlockFixedGridState ReadState(BinaryReader reader) => new()
        {
            Handle = ReadHandle(reader), Cell = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
            Owner = reader.ReadByte(), Support = new BlockFixedGridMapSupport
            { Id = reader.ReadInt32(), BaseCellY = reader.ReadInt32(), Height = reader.ReadSingle(),
                SurfaceKey = new BlockSurfaceKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()) }, Revision = reader.ReadUInt32()
        };
        static void Validate(BlockFixedGridSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Epoch == 0 || snapshot.NextId == 0 || snapshot.Upserts == null || snapshot.Removed == null ||
                snapshot.SurfaceBytes == null || snapshot.SurfaceBytes.Length < 30 || snapshot.Upserts.Length > MaxBlocks || snapshot.Removed.Length > MaxBlocks ||
                snapshot.Revision < snapshot.BaseRevision || snapshot.SurfaceRevision < snapshot.SurfaceBaseRevision ||
                snapshot.Full && (snapshot.BaseRevision != 0 || snapshot.SurfaceBaseRevision != 0 || snapshot.Removed.Length != 0) ||
                HeaderBytes + (long)snapshot.Upserts.Length * StateBytes + (long)snapshot.Removed.Length * HandleBytes + snapshot.SurfaceBytes.Length > MaxTransferBytes)
                throw new InvalidDataException("Invalid fixed-grid snapshot header or size.");
            var ids = new HashSet<ulong>(); var cells = new HashSet<Vector3Int>();
            foreach (var state in snapshot.Upserts)
            {
                if (!ValidHandle(state.Handle, snapshot) || state.Handle.Id >= snapshot.NextId || !ids.Add(state.Handle.Id) || !cells.Add(state.Cell) ||
                    (state.Owner != 1 && state.Owner != 2) || state.Support.Id <= 0 || !float.IsFinite(state.Support.Height) ||
                    state.Revision == 0 || state.Revision > snapshot.Revision)
                    throw new InvalidDataException("Invalid, duplicate or stale fixed-grid cell.");
            }
            foreach (var handle in snapshot.Removed)
                if (!ValidHandle(handle, snapshot) || handle.Id >= snapshot.NextId || !ids.Add(handle.Id))
                    throw new InvalidDataException("Invalid or duplicate fixed-grid removal.");
        }
        public static byte[] Encode(BlockFixedGridSnapshot snapshot)
        {
            Validate(snapshot);
            int length = checked(HeaderBytes + snapshot.Upserts.Length * StateBytes + snapshot.Removed.Length * HandleBytes + snapshot.SurfaceBytes.Length);
            using var stream = new MemoryStream(length); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(ProtocolVersion); writer.Write(snapshot.Full); writer.Write(snapshot.Frozen);
            writer.Write(snapshot.Epoch); writer.Write(snapshot.Round); writer.Write(snapshot.BaseRevision); writer.Write(snapshot.Revision);
            writer.Write(snapshot.SurfaceBaseRevision); writer.Write(snapshot.SurfaceRevision); writer.Write(snapshot.NextId);
            writer.Write(snapshot.Upserts.Length); writer.Write(snapshot.Removed.Length); writer.Write(snapshot.SurfaceBytes.Length);
            foreach (var state in snapshot.Upserts) WriteState(writer, state);
            foreach (var handle in snapshot.Removed) WriteHandle(writer, handle);
            writer.Write(snapshot.SurfaceBytes); return stream.ToArray();
        }
        public static BlockFixedGridSnapshot Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < HeaderBytes + 30 || bytes.Length > MaxTransferBytes)
                throw new InvalidDataException("Invalid fixed-grid payload length.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != ProtocolVersion)
                throw new InvalidDataException("Unknown fixed-grid protocol; old physics worlds cannot be imported.");
            byte full = reader.ReadByte(), frozen = reader.ReadByte();
            if (full > 1 || frozen > 1) throw new InvalidDataException("Invalid fixed-grid boolean flag.");
            var snapshot = new BlockFixedGridSnapshot { Full = full != 0, Frozen = frozen != 0,
                Epoch = reader.ReadUInt32(), Round = reader.ReadUInt32(), BaseRevision = reader.ReadUInt32(), Revision = reader.ReadUInt32(),
                SurfaceBaseRevision = reader.ReadUInt32(), SurfaceRevision = reader.ReadUInt32(), NextId = reader.ReadUInt64() };
            int count = reader.ReadInt32(), removed = reader.ReadInt32(), surfaceLength = reader.ReadInt32();
            if (count < 0 || count > MaxBlocks || removed < 0 || removed > MaxBlocks || surfaceLength < 30 ||
                HeaderBytes + (long)count * StateBytes + (long)removed * HandleBytes + surfaceLength != bytes.Length)
                throw new InvalidDataException("Invalid fixed-grid payload counts.");
            snapshot.Upserts = new BlockFixedGridState[count]; snapshot.Removed = new BlockFixedGridHandle[removed];
            for (int i = 0; i < count; i++) snapshot.Upserts[i] = ReadState(reader);
            for (int i = 0; i < removed; i++) snapshot.Removed[i] = ReadHandle(reader);
            snapshot.SurfaceBytes = reader.ReadBytes(surfaceLength); Validate(snapshot); return snapshot;
        }
    }

    public struct BlockFixedGridFragment : INetworkSerializable
    {
        public const int PayloadBytes = 900;
        public int Version;
        public uint Epoch, Round, Transfer, BaseRevision, Revision, SurfaceBaseRevision, SurfaceRevision, Tick, Hash;
        public ulong Sequence;
        public bool Full, Frozen;
        public int Length, Index;
        public byte[] Data;
        public int Count => (Length + PayloadBytes - 1) / PayloadBytes;
        public bool Valid => Version == BlockFixedGridCodec.ProtocolVersion && Epoch != 0 && Transfer != 0 && Sequence != 0 &&
            Revision >= BaseRevision && SurfaceRevision >= SurfaceBaseRevision && (!Full || BaseRevision == 0 && SurfaceBaseRevision == 0) &&
            Length >= BlockFixedGridCodec.HeaderBytes + 30 && Length <= BlockFixedGridCodec.MaxTransferBytes && Index >= 0 && Index < Count &&
            Data != null && Data.Length == Math.Min(PayloadBytes, Length - Index * PayloadBytes);
        public bool SameTransfer(BlockFixedGridFragment other) => Version == other.Version && Epoch == other.Epoch && Round == other.Round &&
            Transfer == other.Transfer && BaseRevision == other.BaseRevision && Revision == other.Revision && SurfaceBaseRevision == other.SurfaceBaseRevision &&
            SurfaceRevision == other.SurfaceRevision && Tick == other.Tick && Hash == other.Hash && Sequence == other.Sequence && Full == other.Full &&
            Frozen == other.Frozen && Length == other.Length;
        public bool Matches(BlockFixedGridSnapshot snapshot) => snapshot != null && snapshot.Epoch == Epoch && snapshot.Round == Round &&
            snapshot.BaseRevision == BaseRevision && snapshot.Revision == Revision && snapshot.SurfaceBaseRevision == SurfaceBaseRevision &&
            snapshot.SurfaceRevision == SurfaceRevision && snapshot.Full == Full && snapshot.Frozen == Frozen;
        public bool MatchesAcknowledgement(uint epoch, uint round, uint transfer, ulong sequence,
            uint revision, uint surfaceRevision, uint hash, bool frozen)
            => Epoch == epoch && Round == round && Transfer == transfer && Sequence == sequence && Revision == revision &&
                SurfaceRevision == surfaceRevision && Hash == hash && Frozen == frozen;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Version); serializer.SerializeValue(ref Epoch); serializer.SerializeValue(ref Round);
            serializer.SerializeValue(ref Transfer); serializer.SerializeValue(ref BaseRevision); serializer.SerializeValue(ref Revision);
            serializer.SerializeValue(ref SurfaceBaseRevision); serializer.SerializeValue(ref SurfaceRevision); serializer.SerializeValue(ref Tick);
            serializer.SerializeValue(ref Hash); serializer.SerializeValue(ref Sequence); serializer.SerializeValue(ref Full); serializer.SerializeValue(ref Frozen);
            serializer.SerializeValue(ref Length); serializer.SerializeValue(ref Index); serializer.SerializeValue(ref Data);
        }
    }

    public sealed class BlockFixedGridAssembly
    {
        public readonly BlockFixedGridFragment Manifest;
        public readonly byte[] Bytes;
        readonly bool[] _received;
        public int ReceivedCount { get; private set; }
        public int ReceivedThrough { get; private set; }
        public bool Complete => ReceivedCount == _received.Length;
        public BlockFixedGridAssembly(BlockFixedGridFragment first)
        {
            if (!first.Valid) throw new ArgumentException("Invalid fixed-grid fragment.");
            Manifest = first; Bytes = new byte[first.Length]; _received = new bool[first.Count];
        }
        public bool Add(BlockFixedGridFragment part)
        {
            if (!part.Valid || !Manifest.SameTransfer(part)) return false;
            if (_received[part.Index])
            {
                int offset = part.Index * BlockFixedGridFragment.PayloadBytes;
                for (int i = 0; i < part.Data.Length; i++) if (Bytes[offset + i] != part.Data[i]) return false;
                return true;
            }
            Buffer.BlockCopy(part.Data, 0, Bytes, part.Index * BlockFixedGridFragment.PayloadBytes, part.Data.Length);
            _received[part.Index] = true; ReceivedCount++;
            while (ReceivedThrough < _received.Length && _received[ReceivedThrough]) ReceivedThrough++;
            return true;
        }
        public bool Verify() => Complete && BlockTransferFragment.Checksum(Bytes) == Manifest.Hash;
    }
}
