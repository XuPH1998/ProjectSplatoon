using System;
using System.IO;

namespace Splatoon.Painting
{
    public sealed class FoamCommitData
    {
        public uint Revision,Tick,PaintSequence;
        public byte[] Data;
        public int Length;
        public int Count=>Length>0?Length:Data.Length;
        bool _pooled;
        static readonly System.Collections.Generic.Stack<FoamCommitData> Pool=new();
        public static FoamCommitData Rent(int length)
        {
            var value=Pool.Count>0?Pool.Pop():new FoamCommitData();value.Data=System.Buffers.ArrayPool<byte>.Shared.Rent(length);value.Length=length;value._pooled=true;return value;
        }
        public void Release()
        {
            if(!_pooled)return;System.Buffers.ArrayPool<byte>.Shared.Return(Data);Data=null;Length=0;_pooled=false;Pool.Push(this);
        }
        public void Write(BinaryWriter writer)
        {writer.Write(Revision);writer.Write(Tick);writer.Write(PaintSequence);writer.Write(Count);writer.Write(Data,0,Count);}
        public static FoamCommitData Read(BinaryReader reader,int maximumBytes)
        {
            var item=new FoamCommitData{Revision=reader.ReadUInt32(),Tick=reader.ReadUInt32(),PaintSequence=reader.ReadUInt32()};
            int length=reader.ReadInt32();if(length<4||length>maximumBytes)throw new InvalidDataException("泡沫日志大小无效");
            item.Data=reader.ReadBytes(length);if(item.Data.Length!=length)throw new EndOfStreamException();return item;
        }
    }
}
