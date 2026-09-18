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
        public uint FoamRevision,FoamEndRevision;
        public byte[] Foam=Array.Empty<byte>();
        public readonly List<FoamCommitData> FoamJournal=new();
    }
    public static class PaintSnapshotCodec
    {
        public static uint Hash(byte[] bytes) => Hash(bytes, 0, bytes.Length);
        public static uint Hash(byte[] bytes, int offset, int count)
        { uint hash = 2166136261; for (int i = offset; i < offset + count; i++) hash = unchecked((hash ^ bytes[i]) * 16777619); return hash; }
        public static byte[] Encode(PaintCheckpoint checkpoint)
        {
            using var output = new MemoryStream();
            using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(zip))
            {
                writer.Write(6); writer.Write(checkpoint.Round); writer.Write(checkpoint.Sequence);
                writer.Write(checkpoint.Topology);
                WriteMaps(writer, checkpoint.Ownership); WriteMaps(writer, checkpoint.Surfaces);
                writer.Write(checkpoint.FoamRevision);writer.Write(checkpoint.FoamEndRevision);writer.Write(checkpoint.Foam.Length);writer.Write(checkpoint.Foam);
                writer.Write(checkpoint.FoamJournal.Count);foreach(var commit in checkpoint.FoamJournal)commit.Write(writer);
            }
            return output.ToArray();
        }
        static void WriteMaps(BinaryWriter writer, Dictionary<int, byte[]> maps)
        {
            writer.Write(maps.Count); var ids = new List<int>(maps.Keys); ids.Sort();
            foreach (int id in ids) { writer.Write(id); writer.Write(maps[id].Length); writer.Write(maps[id]); }
        }
        public static PaintCheckpoint Decode(byte[] compressed, string topology, IReadOnlyDictionary<int, int> ownershipBytes, IReadOnlyDictionary<int, int> surfaceBytes,int foamBytes=0)
        {
            using var input = new MemoryStream(compressed); using var zip = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip);
            if (reader.ReadInt32() != 6) throw new InvalidDataException("涂色快照版本不一致");
            var result = new PaintCheckpoint { Round = reader.ReadUInt32(), Sequence = reader.ReadUInt32() };
            result.Topology = reader.ReadString();
            if (result.Topology != topology) throw new InvalidDataException("地图拓扑不一致");
            ReadMaps(reader, result.Ownership, ownershipBytes, true);
            ReadMaps(reader, result.Surfaces, surfaceBytes, false);
            result.FoamRevision=reader.ReadUInt32();result.FoamEndRevision=reader.ReadUInt32();int length=reader.ReadInt32();
            if(length!=foamBytes||length<0||result.FoamEndRevision<result.FoamRevision)throw new InvalidDataException("泡沫快照尺寸或版本无效");
            result.Foam=reader.ReadBytes(length);if(result.Foam.Length!=length)throw new EndOfStreamException();
            int commits=reader.ReadInt32();if(commits<0||commits>8192||commits!=(long)result.FoamEndRevision-result.FoamRevision)throw new InvalidDataException("泡沫日志数量无效");
            long total=0;uint revision=result.FoamRevision;
            for(int i=0;i<commits;i++)
            {var commit=FoamCommitData.Read(reader,checked(foamBytes*3+4));if(commit.Revision!=++revision)throw new InvalidDataException("泡沫日志版本不连续");total+=commit.Data.Length;if(total>128*1048576)throw new InvalidDataException("泡沫日志超过内存上限");result.FoamJournal.Add(commit);}
            if (zip.ReadByte() != -1) throw new InvalidDataException("快照包含多余数据");
            return result;
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
