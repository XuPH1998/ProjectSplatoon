using System;
using UnityEngine;

namespace Splatoon.Painting
{
    [Serializable]
    public sealed class FoamPatchBake
    {
        public int RegionKey, Columns, Rows;
        public Vector2 Size;
        public ushort[] CeilingMm = Array.Empty<ushort>();
        // 0 is blocked. Each edge bit records a static, unobstructed neighbour.
        public byte[] Edges = Array.Empty<byte>();
    }

    [CreateAssetMenu(menuName = "喷墨对战/泡沫地形烘焙")]
    public sealed class FoamTerrainData : ScriptableObject
    {
        public const int Format = 1;
        public string SourceTopology;
        public float CellSize, ChunkSize, MaxHeight, CeilingGap;
        public FoamPatchBake[] Patches = Array.Empty<FoamPatchBake>();
        public string Signature()
        {
            using var stream=new System.IO.MemoryStream();using var writer=new System.IO.BinaryWriter(stream);
            writer.Write(Format);writer.Write(SourceTopology??"");writer.Write(CellSize);writer.Write(ChunkSize);writer.Write(MaxHeight);writer.Write(CeilingGap);
            foreach(var patch in Patches){writer.Write(patch.RegionKey);writer.Write(patch.Columns);writer.Write(patch.Rows);writer.Write(patch.Size.x);writer.Write(patch.Size.y);foreach(ushort h in patch.CeilingMm)writer.Write(h);writer.Write(patch.Edges);}
            using var hash=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(hash.ComputeHash(stream.ToArray()));
        }
    }

    public static class FoamRules
    {
        public const float Millimetre = .001f;
        public static float Kernel(float normalizedSquaredDistance)
        { float t = Mathf.Max(0, 1 - normalizedSquaredDistance); return t * t; }
        public static void Deposit(ref float height, ref byte owner, byte team, float amount, float dissolve, float limit)
        {
            if (team < 1 || team > 2 || amount <= 0 || !float.IsFinite(amount)) return;
            if (owner != 0 && owner != team && height > 0)
            {
                float consumed = Mathf.Min(amount, height / dissolve);
                height = Mathf.Max(0, height - consumed * dissolve); amount -= consumed;
                if (height <= .000001f) { height = 0; owner = 0; }
            }
            if (amount > .000001f && (owner == 0 || owner == team))
            { height = Mathf.Min(limit, height + amount); if (height > 0) owner = team; }
        }
        public static ushort Quantize(float height) => (ushort)Mathf.Clamp(Mathf.RoundToInt(height * 1000), 0, ushort.MaxValue);
    }

    internal sealed class FoamNode
    {
        public int Id;
        public bool Boundary;
        public Vector3 Base;
        public float Area, Limit, PendingHeight;
        public ushort HeightMm;
        public byte Owner, PendingOwner;
        public readonly System.Collections.Generic.List<FoamNode> Neighbours = new(4);
        public readonly System.Collections.Generic.List<FoamChunk> Chunks = new(4);
        public readonly System.Collections.Generic.List<FoamPatch> Patches = new(2);
        public float Height => HeightMm * FoamRules.Millimetre;
        public Vector3 Top => Base + Vector3.up * Height;
        public Vector3 Normal=Vector3.up;
        public void RefreshNormal()
        {
            float xx=0,zz=0,xz=0,xy=0,zy=0;
            foreach(var neighbour in Neighbours){var d=neighbour.Top-Top;xx+=d.x*d.x;zz+=d.z*d.z;xz+=d.x*d.z;xy+=d.x*d.y;zy+=d.z*d.y;}
            float determinant=xx*zz-xz*xz;
            if(determinant>.0000001f)Normal=new Vector3(-(xy*zz-zy*xz)/determinant,1,-(zy*xx-xy*xz)/determinant).normalized;
        }
    }
}
