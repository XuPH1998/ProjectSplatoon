using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon.Networking
{
    /// <summary>Lossless delta of the canonical NetworkSerialize bytes, never struct padding.</summary>
    public static class PlayerSnapshotDelta
    {
        const int MaxBytes = 4096;
        static FastBufferWriter _canonical;
        static readonly byte[] Current = new byte[MaxBytes], Previous = new byte[MaxBytes], Mask = new byte[MaxBytes / 32];
        public static long FullBytes { get; private set; }
        public static long EncodedBytes { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Register()
        {
            Dispose(); FullBytes = EncodedBytes = 0;
            UserNetworkVariableSerialization<PlayerSnapshot>.WriteDelta = WriteDelta;
            UserNetworkVariableSerialization<PlayerSnapshot>.ReadDelta = ReadDelta;
            Application.quitting -= Dispose; Application.quitting += Dispose;
        }
        public static void Dispose() { if (_canonical.IsInitialized) _canonical.Dispose(); _canonical = default; }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void BindEditorCleanup()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
        }
#endif

        static int Serialize(in PlayerSnapshot value, byte[] bytes)
        {
            if (!_canonical.IsInitialized) _canonical = new FastBufferWriter(MaxBytes, Allocator.Persistent);
            _canonical.Seek(0); _canonical.Truncate(0); _canonical.WriteNetworkSerializable(value);
            using var reader = new FastBufferReader(_canonical, Allocator.None);
            reader.ReadBytesSafe(ref bytes, _canonical.Length);
            return _canonical.Length;
        }

        public static void WriteDelta(FastBufferWriter writer, in PlayerSnapshot value, in PlayerSnapshot previousValue)
        {
            int length = Serialize(value, Current), oldLength = Serialize(previousValue, Previous);
            int blocks = (length + 3) / 4, maskLength = (blocks + 7) / 8, changedBytes = 0;
            Array.Clear(Mask, 0, maskLength);
            for (int block = 0; block < blocks; block++)
            {
                int offset = block * 4, size = Math.Min(4, length - offset); bool changed = oldLength != length;
                for (int i = 0; i < size && !changed; i++) changed = Current[offset + i] != Previous[offset + i];
                if (changed) { Mask[block / 8] |= (byte)(1 << (block % 8)); changedBytes += size; }
            }
            bool full = oldLength != length || value.Revision != previousValue.Revision || value.HeroRevision != previousValue.HeroRevision ||
                value.HeroId != previousValue.HeroId || maskLength + changedBytes >= length;
            int start = writer.Position;
            writer.WriteByteSafe(full ? (byte)0 : (byte)1); writer.WriteValueSafe((ushort)length);
            if (full) writer.WriteBytesSafe(Current, length);
            else
            {
                writer.WriteBytesSafe(Mask, maskLength);
                for (int block = 0; block < blocks; block++)
                    if ((Mask[block / 8] & (1 << (block % 8))) != 0)
                        writer.WriteBytesSafe(Current, Math.Min(4, length - block * 4), block * 4);
            }
            FullBytes += length; EncodedBytes += writer.Position - start;
            NetworkTrafficCounters.Record(NetworkTrafficKind.PlayerDelta, writer.Position - start);
        }

        public static void ReadDelta(FastBufferReader reader, ref PlayerSnapshot value)
        {
            reader.ReadByteSafe(out byte mode); reader.ReadValueSafe(out ushort length);
            if (mode > 1 || length == 0 || length > MaxBytes) throw new InvalidOperationException("Invalid player delta header.");
            if (mode == 0) { var bytes = Current; reader.ReadBytesSafe(ref bytes, length); }
            else
            {
                if (Serialize(value, Current) != length) throw new InvalidOperationException("Player delta baseline layout mismatch.");
                int blocks = (length + 3) / 4, maskLength = (blocks + 7) / 8;
                var mask = Mask; reader.ReadBytesSafe(ref mask, maskLength);
                if (blocks % 8 != 0 && (Mask[maskLength - 1] >> (blocks % 8)) != 0) throw new InvalidOperationException("Invalid player delta mask.");
                for (int block = 0; block < blocks; block++)
                    if ((Mask[block / 8] & (1 << (block % 8))) != 0)
                    { var bytes = Current; reader.ReadBytesSafe(ref bytes, Math.Min(4, length - block * 4), block * 4); }
            }
            // A private candidate prevents a truncated/corrupt packet from partially changing the live state.
            if (!_canonical.IsInitialized) _canonical = new FastBufferWriter(MaxBytes, Allocator.Persistent);
            _canonical.Seek(0); _canonical.Truncate(0); _canonical.WriteBytesSafe(Current, length);
            using var canonical = new FastBufferReader(_canonical, Allocator.None);
            PlayerSnapshot candidate = default;
            canonical.ReadNetworkSerializableInPlace(ref candidate);
            if (canonical.Position != length) throw new InvalidOperationException("Player snapshot layout mismatch.");
            value = candidate;
        }
    }
}
