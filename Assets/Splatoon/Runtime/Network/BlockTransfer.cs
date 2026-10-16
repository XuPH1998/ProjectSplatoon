using System;
using Unity.Netcode;

namespace Splatoon.Networking
{
    public struct BlockTransferFragment : INetworkSerializable
    {
        public const int PayloadBytes = 900;
        public const int MaxTransferBytes = 128 * 1024 * 1024;
        public uint Round, Id, BaseRevision, Revision, Hash;
        public int Length, Index;
        public bool Full;
        public byte[] Data;
        public int Count => (Length + PayloadBytes - 1) / PayloadBytes;
        public bool Valid => Length > 0 && Length <= MaxTransferBytes && Index >= 0 && Index < Count &&
            Revision >= BaseRevision && Data != null && Data.Length == Math.Min(PayloadBytes, Length - Index * PayloadBytes);
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Round); s.SerializeValue(ref Id); s.SerializeValue(ref BaseRevision); s.SerializeValue(ref Revision);
            s.SerializeValue(ref Hash); s.SerializeValue(ref Length); s.SerializeValue(ref Index); s.SerializeValue(ref Full);
            s.SerializeValue(ref Data);
        }
        public static uint Checksum(byte[] data)
        {
            uint hash = 2166136261;
            for (int i = 0; i < data.Length; i++) hash = (hash ^ data[i]) * 16777619;
            return hash;
        }
    }

    // One bounded assembly per client. A completed payload is not acknowledged as
    // applied until its meshes, colliders and voxel state have been published together.
    public sealed class BlockTransferAssembly
    {
        public readonly BlockTransferFragment Manifest;
        public readonly byte[] Bytes;
        readonly bool[] _received;
        public int ReceivedCount { get; private set; }
        public bool Complete => ReceivedCount == _received.Length;
        public BlockTransferAssembly(BlockTransferFragment first)
        {
            if (!first.Valid) throw new ArgumentException("Invalid block fragment.");
            Manifest = first; Bytes = new byte[first.Length]; _received = new bool[first.Count];
        }
        public bool Add(BlockTransferFragment fragment)
        {
            if (!fragment.Valid || fragment.Id != Manifest.Id || fragment.Round != Manifest.Round || fragment.Length != Manifest.Length ||
                fragment.Hash != Manifest.Hash || fragment.Full != Manifest.Full || fragment.BaseRevision != Manifest.BaseRevision || fragment.Revision != Manifest.Revision) return false;
            if (_received[fragment.Index]) return true;
            Buffer.BlockCopy(fragment.Data, 0, Bytes, fragment.Index * BlockTransferFragment.PayloadBytes, fragment.Data.Length);
            _received[fragment.Index] = true; ReceivedCount++; return true;
        }
        public bool Verify() => Complete && BlockTransferFragment.Checksum(Bytes) == Manifest.Hash;
    }
}
