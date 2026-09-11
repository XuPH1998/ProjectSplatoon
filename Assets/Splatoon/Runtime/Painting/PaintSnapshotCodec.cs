using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Splatoon.Painting
{
    public sealed class PaintCheckpoint
    {
        public uint Round, Sequence;
        public byte[] Grid;
        public readonly Dictionary<int, byte[]> Surfaces = new();
    }
    public static class PaintSnapshotCodec
    {
        public static uint Hash(byte[] bytes)
        { uint hash = 2166136261; foreach (byte b in bytes) hash = unchecked((hash ^ b) * 16777619); return hash; }
        public static byte[] Encode(PaintCheckpoint checkpoint)
        {
            using var output = new MemoryStream();
            using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(zip))
            {
                writer.Write(2); writer.Write(checkpoint.Round); writer.Write(checkpoint.Sequence);
                writer.Write(checkpoint.Grid.Length); writer.Write(checkpoint.Grid);
                writer.Write(checkpoint.Surfaces.Count);
                var ids = new List<int>(checkpoint.Surfaces.Keys); ids.Sort();
                foreach (var id in ids) { var data = checkpoint.Surfaces[id]; writer.Write(id); writer.Write(data.Length); writer.Write(data); }
            }
            return output.ToArray();
        }
        public static PaintCheckpoint Decode(byte[] compressed, int gridLength, IReadOnlyDictionary<int, int> surfaceBytes)
        {
            using var input = new MemoryStream(compressed);
            using var zip = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip);
            if (reader.ReadInt32() != 2) throw new InvalidDataException("涂色快照版本不一致");
            var checkpoint = new PaintCheckpoint { Round = reader.ReadUInt32(), Sequence = reader.ReadUInt32() };
            int size = reader.ReadInt32(); if (size != gridLength) throw new InvalidDataException("归属网格尺寸不一致");
            checkpoint.Grid = reader.ReadBytes(size); if (checkpoint.Grid.Length != size) throw new EndOfStreamException();
            foreach (byte value in checkpoint.Grid) if (value > 2 && value != 255) throw new InvalidDataException("归属值无效");
            int count = reader.ReadInt32(); if (count < 0 || count > surfaceBytes.Count) throw new InvalidDataException("表面数量无效");
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32(), length = reader.ReadInt32();
                if (!surfaceBytes.TryGetValue(id, out int expected) || length != expected || checkpoint.Surfaces.ContainsKey(id)) throw new InvalidDataException("表面快照结构无效");
                var data = reader.ReadBytes(length); if (data.Length != length) throw new EndOfStreamException(); checkpoint.Surfaces.Add(id, data);
            }
            if (zip.ReadByte() != -1) throw new InvalidDataException("快照包含多余数据");
            return checkpoint;
        }
    }
}
