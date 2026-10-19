using System;
using System.IO;

namespace Splatoon.Networking
{
    /// <summary>One reliable lifecycle payload for surface ownership and independent free bodies.</summary>
    public static class BlockSurfaceDynamicEnvelope
    {
        public const int ProtocolVersion = 1;
        public const int HeaderBytes = 21;
        const uint Magic = 0x31455342; // BSE1.
        const int MinimumDynamicBytes = 12, MinimumSurfaceBytes = 30;

        public static byte[] Encode(byte[] dynamicBytes, byte[] surfaceBytes, uint surfaceRevision, bool full)
        {
            if (dynamicBytes == null || surfaceBytes == null || dynamicBytes.Length < MinimumDynamicBytes ||
                surfaceBytes.Length < MinimumSurfaceBytes ||
                HeaderBytes + (long)dynamicBytes.Length + surfaceBytes.Length > BlockDynamicCodec.MaxTransferBytes)
                throw new ArgumentException("Invalid combined surface/body lifecycle size.");
            using var stream = new MemoryStream(HeaderBytes + dynamicBytes.Length + surfaceBytes.Length);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(ProtocolVersion); writer.Write(surfaceRevision); writer.Write(full);
            writer.Write(dynamicBytes.Length); writer.Write(surfaceBytes.Length);
            writer.Write(dynamicBytes); writer.Write(surfaceBytes);
            return stream.ToArray();
        }

        // Each contained codec validates its own identity, topology and baseline before commit.
        // Surface mode always requires this envelope; a legacy body payload cannot downgrade it.
        public static void Decode(byte[] bytes, out byte[] dynamicBytes, out byte[] surfaceBytes,
            out uint surfaceRevision, out bool full)
        {
            if (bytes == null || bytes.Length < HeaderBytes + MinimumDynamicBytes + MinimumSurfaceBytes ||
                bytes.Length > BlockDynamicCodec.MaxTransferBytes)
                throw new InvalidDataException("Invalid combined surface/body lifecycle size.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != ProtocolVersion)
                throw new InvalidDataException("Unknown combined surface/body lifecycle protocol.");
            uint revision = reader.ReadUInt32(); byte fullByte = reader.ReadByte();
            int dynamicLength = reader.ReadInt32(), surfaceLength = reader.ReadInt32();
            if (fullByte > 1 || dynamicLength < MinimumDynamicBytes || surfaceLength < MinimumSurfaceBytes ||
                HeaderBytes + (long)dynamicLength + surfaceLength != bytes.Length)
                throw new InvalidDataException("Invalid combined surface/body lifecycle header.");
            byte[] dynamicPayload = reader.ReadBytes(dynamicLength), surfacePayload = reader.ReadBytes(surfaceLength);
            if (dynamicPayload.Length != dynamicLength || surfacePayload.Length != surfaceLength || stream.Position != stream.Length)
                throw new InvalidDataException("Truncated combined surface/body lifecycle.");
            dynamicBytes = dynamicPayload; surfaceBytes = surfacePayload; surfaceRevision = revision; full = fullByte != 0;
        }
    }
}
