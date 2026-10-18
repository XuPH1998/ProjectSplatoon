using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using Unity.Profiling;
using UnityEngine;
using Splatoon.Blocks;

namespace Splatoon.Networking
{
    /// <summary>Reliable lifecycle payloads and bounded, independent motion packets.</summary>
    public static class BlockDynamicCodec
    {
        static readonly ProfilerMarker DynamicLifecycleEncodeCpu = new("Splatoon.Blocks.DynamicLifecycleEncode");

        public const int ProtocolVersion = 1, StateBytes = 62, MotionBatchSize = 12;
        public const int MaxBodies = 1000000, MaxTransferBytes = 128 * 1024 * 1024;
        const uint Magic = 0x31445942; // BYD1.

        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        public static bool Valid(BlockDynamicState state, uint round)
        {
            var q = state.Rotation;
            float length = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return state.Id != 0 && state.Round == round && (state.Owner == 1 || state.Owner == 2)
                && Finite(state.Position) && Finite(state.Velocity) && Finite(state.AngularVelocity)
                && float.IsFinite(length) && length > .98f && length < 1.02f;
        }
        static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static void WriteState(BinaryWriter w, BlockDynamicState s)
        {
            w.Write(s.Id); w.Write(s.Owner); WriteVector(w, s.Position);
            w.Write(s.Rotation.x); w.Write(s.Rotation.y); w.Write(s.Rotation.z); w.Write(s.Rotation.w);
            WriteVector(w, s.Velocity); WriteVector(w, s.AngularVelocity); w.Write(s.Sleeping);
        }
        static BlockDynamicState ReadState(BinaryReader r, uint round)
            => new() { Id = r.ReadUInt64(), Round = round, Owner = r.ReadByte(), Position = ReadVector(r),
                Rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Velocity = ReadVector(r), AngularVelocity = ReadVector(r), Sleeping = r.ReadBoolean() };

        public static byte[] Encode(uint round, IReadOnlyList<BlockDynamicState> created, IReadOnlyList<ulong> removed)
        {
            using var marker = DynamicLifecycleEncodeCpu.Auto();
            if (created == null || removed == null || created.Count > MaxBodies || removed.Count > MaxBodies)
                throw new ArgumentException("Invalid dynamic lifecycle size.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(created.Count); writer.Write(removed.Count);
            var ids = new HashSet<ulong>();
            foreach (var state in created)
            {
                if (!Valid(state, round) || !ids.Add(state.Id)) throw new InvalidDataException("Invalid or duplicate dynamic body.");
                WriteState(writer, state);
            }
            foreach (ulong id in removed)
            {
                if (id == 0 || !ids.Add(id)) throw new InvalidDataException("Invalid or duplicate dynamic removal.");
                writer.Write(id);
            }
            return stream.ToArray();
        }
        public static void Decode(byte[] bytes, uint round, List<BlockDynamicState> created, List<ulong> removed)
        {
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaxTransferBytes) throw new InvalidDataException("Invalid dynamic payload size.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unknown dynamic block protocol.");
            int count = reader.ReadInt32(), deletions = reader.ReadInt32();
            if (count < 0 || count > MaxBodies || deletions < 0 || deletions > MaxBodies
                || 12L + (long)count * StateBytes + (long)deletions * 8 != bytes.Length)
                throw new InvalidDataException("Invalid dynamic lifecycle counts.");
            var decoded = new List<BlockDynamicState>(count); var deleted = new List<ulong>(deletions); var ids = new HashSet<ulong>();
            for (int i = 0; i < count; i++)
            {
                var state = ReadState(reader, round);
                if (!Valid(state, round) || !ids.Add(state.Id)) throw new InvalidDataException("Invalid or duplicate dynamic body.");
                decoded.Add(state);
            }
            for (int i = 0; i < deletions; i++)
            {
                ulong id = reader.ReadUInt64();
                if (id == 0 || !ids.Add(id)) throw new InvalidDataException("Invalid or duplicate dynamic removal.");
                deleted.Add(id);
            }
            created.Clear(); created.AddRange(decoded); removed.Clear(); removed.AddRange(deleted);
        }
    }

    public struct BlockDynamicFragment : INetworkSerializable
    {
        public const int PayloadBytes = 900;
        public uint Round, Transfer, BaseRevision, Revision, Hash, Tick;
        public ulong Sequence;
        public bool Full, Frozen;
        public int Length, Index;
        public byte[] Data;
        public int Count => (Length + PayloadBytes - 1) / PayloadBytes;
        public bool Valid => Transfer != 0 && Sequence != 0 && Revision >= BaseRevision
            && Length >= 12 && Length <= BlockDynamicCodec.MaxTransferBytes && Index >= 0 && Index < Count
            && Data != null && Data.Length == Math.Min(PayloadBytes, Length - Index * PayloadBytes);
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Round); s.SerializeValue(ref Transfer); s.SerializeValue(ref BaseRevision); s.SerializeValue(ref Revision);
            s.SerializeValue(ref Hash); s.SerializeValue(ref Tick); s.SerializeValue(ref Sequence); s.SerializeValue(ref Full); s.SerializeValue(ref Frozen);
            s.SerializeValue(ref Length); s.SerializeValue(ref Index); s.SerializeValue(ref Data);
        }
    }

    public sealed class BlockDynamicAssembly
    {
        public readonly BlockDynamicFragment Manifest;
        public readonly byte[] Bytes;
        readonly bool[] _received;
        public int ReceivedCount { get; private set; }
        public bool Complete => ReceivedCount == _received.Length;
        public BlockDynamicAssembly(BlockDynamicFragment first)
        {
            if (!first.Valid) throw new ArgumentException("Invalid dynamic fragment.");
            Manifest = first; Bytes = new byte[first.Length]; _received = new bool[first.Count];
        }
        public bool Add(BlockDynamicFragment part)
        {
            var m = Manifest;
            if (!part.Valid || part.Round != m.Round || part.Transfer != m.Transfer || part.BaseRevision != m.BaseRevision
                || part.Revision != m.Revision || part.Tick != m.Tick || part.Sequence != m.Sequence || part.Hash != m.Hash || part.Length != m.Length
                || part.Full != m.Full || part.Frozen != m.Frozen) return false;
            if (_received[part.Index]) return true;
            Buffer.BlockCopy(part.Data, 0, Bytes, part.Index * BlockDynamicFragment.PayloadBytes, part.Data.Length);
            _received[part.Index] = true; ReceivedCount++; return true;
        }
        public bool Verify() => Complete && BlockTransferFragment.Checksum(Bytes) == Manifest.Hash;
    }

    public struct BlockDynamicMotion : INetworkSerializable
    {
        static readonly ProfilerMarker DynamicMotionEncodeCpu = new("Splatoon.Blocks.DynamicMotionEncode");
        static readonly ProfilerMarker DynamicMotionDecodeCpu = new("Splatoon.Blocks.DynamicMotionDecode");

        public uint Round, Revision, Tick;
        public ulong Sequence;
        public BlockDynamicState[] States;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            using var marker = s.IsReader ? DynamicMotionDecodeCpu.Auto() : DynamicMotionEncodeCpu.Auto();
            s.SerializeValue(ref Round); s.SerializeValue(ref Revision); s.SerializeValue(ref Tick); s.SerializeValue(ref Sequence);
            int count = States?.Length ?? 0; s.SerializeValue(ref count);
            if (count < 0 || count > BlockDynamicCodec.MotionBatchSize) throw new InvalidDataException("Invalid dynamic motion length.");
            if (s.IsReader) States = new BlockDynamicState[count];
            for (int i = 0; i < count; i++)
            {
                var state = s.IsReader ? default : States[i]; state.Round = Round;
                s.SerializeValue(ref state.Id); s.SerializeValue(ref state.Owner); s.SerializeValue(ref state.Position);
                s.SerializeValue(ref state.Rotation); s.SerializeValue(ref state.Velocity); s.SerializeValue(ref state.AngularVelocity);
                s.SerializeValue(ref state.Sleeping);
                if (!BlockDynamicCodec.Valid(state, Round)) throw new InvalidDataException("Invalid dynamic motion body.");
                if (s.IsReader) States[i] = state;
            }
        }
    }
}
