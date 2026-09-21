using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
namespace Splatoon.Painting
{
    public sealed class PaintCheckpoint
    {
        public uint Round, Sequence;
        public string Topology;
        public Dictionary<int, byte[]> Ownership = new();
        public readonly Dictionary<int, byte[]> Surfaces = new();
        public readonly Dictionary<int, byte[]> VisualSurfaces = new();
    }
    public static class PaintSnapshotCodec
    {
        public static uint Hash(byte[] bytes) => Hash(bytes, 0, bytes.Length);
        public static uint Hash(byte[] bytes, int offset, int count)
        { uint hash = 2166136261; for (int i = offset; i < offset + count; i++) hash = unchecked((hash ^ bytes[i]) * 16777619); return hash; }
        public static byte[] Encode(PaintCheckpoint checkpoint)
        {
            ValidatePairs(checkpoint);
            using var output = new MemoryStream();
            using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(zip))
            {
                writer.Write(6); writer.Write(checkpoint.Round); writer.Write(checkpoint.Sequence);
                writer.Write(checkpoint.Topology);
                WriteMaps(writer, checkpoint.Ownership); WriteMaps(writer, checkpoint.Surfaces);
                WriteMaps(writer, checkpoint.VisualSurfaces);
            }
            return output.ToArray();
        }
        static void WriteMaps(BinaryWriter writer, Dictionary<int, byte[]> maps)
        {
            writer.Write(maps.Count); var ids = new List<int>(maps.Keys); ids.Sort();
            foreach (int id in ids) { writer.Write(id); writer.Write(maps[id].Length); writer.Write(maps[id]); }
        }
        public static PaintCheckpoint Decode(byte[] compressed, string topology, IReadOnlyDictionary<int, int> ownershipBytes, IReadOnlyDictionary<int, int> surfaceBytes)
        {
            using var input = new MemoryStream(compressed); using var zip = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip);
            if (reader.ReadInt32() != 6) throw new InvalidDataException("涂色快照版本不一致");
            var result = new PaintCheckpoint { Round = reader.ReadUInt32(), Sequence = reader.ReadUInt32() };
            result.Topology = reader.ReadString();
            if (result.Topology != topology) throw new InvalidDataException("地图拓扑不一致");
            ReadMaps(reader, result.Ownership, ownershipBytes, true);
            ReadMaps(reader, result.Surfaces, surfaceBytes, false);
            var visualBytes = new Dictionary<int, int>();
            foreach (var pair in surfaceBytes) visualBytes.Add(pair.Key, pair.Value / 2);
            ReadMaps(reader, result.VisualSurfaces, visualBytes, false);
            ValidatePairs(result);
            if (zip.ReadByte() != -1) throw new InvalidDataException("快照包含多余数据");
            return result;
        }
        static void ValidatePairs(PaintCheckpoint checkpoint)
        {
            if (checkpoint.Surfaces.Count != checkpoint.VisualSurfaces.Count) throw new InvalidDataException("覆盖与细节表面数量不匹配");
            foreach (var pair in checkpoint.Surfaces)
                if (pair.Value == null || pair.Value.Length % 4 != 0 || !checkpoint.VisualSurfaces.TryGetValue(pair.Key, out var visual) ||
                    visual == null || visual.Length != pair.Value.Length / 2) throw new InvalidDataException("覆盖与细节快照未成对");
        }
        static void ReadMaps(BinaryReader reader, Dictionary<int, byte[]> maps, IReadOnlyDictionary<int, int> sizes, bool ownership)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > sizes.Count || (ownership && count != sizes.Count)) throw new InvalidDataException("表面数量无效");
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32(), length = reader.ReadInt32();
                if (!sizes.TryGetValue(id, out int expected) || length != expected || maps.ContainsKey(id)) throw new InvalidDataException("表面快照结构无效");
                byte[] data = reader.ReadBytes(length); if (data.Length != length) throw new EndOfStreamException();
                if (ownership)
                {
                    if (length % 5 != 0) throw new InvalidDataException("累计归属尺寸无效");
                    for (int n=0;n<length/5;n++) if (data[n]>2 && data[n]!=255) throw new InvalidDataException("归属值无效");
                }
                maps.Add(id, data);
            }
        }
    }
}
