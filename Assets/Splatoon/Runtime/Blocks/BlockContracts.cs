using System;
using UnityEngine;

namespace Splatoon.Blocks
{
    public enum BlockContactKind : byte { Legacy, SurfaceAttached, Dynamic, SurfacePaint, FixedGrid }

    public struct BlockContact
    {
        public byte Owner;
        public Vector3 Point, Normal, OrientationNormal;
        public bool Stable, Climbable;
        public Vector3Int Cell, Chunk;
        public uint Revision;
        public BlockContactKind Kind;
        public ulong DynamicId;
        public BlockSurfaceKey SurfaceKey;
        public BlockFixedGridHandle FixedHandle;
        public bool IsBlock => Owner == 1 || Owner == 2;
    }

    public struct BlockFallingHit
    {
        public byte Owner;
        public Vector3 Point, Normal;
        public float Distance;
        public ulong RunId;
        public Vector3Int Cell;
        public Collider Collider;
    }

    public struct BlockMutationResult
    {
        public int Added, RemovedEnemy, RemovedFriendly;
        public uint Revision;
        public bool OrdinaryMainAttack;
        public int Changed => Added + RemovedEnemy + RemovedFriendly;
    }

    public readonly struct BlockTerrainCommit
    {
        public readonly uint Revision, Round;
        public readonly Vector3Int[] ChangedChunks;
        public BlockTerrainCommit(uint revision, uint round, Vector3Int[] chunks)
        { Revision = revision; Round = round; ChangedChunks = chunks; }
    }

    [Serializable]
    public struct BlockCell
    {
        public byte Owner, Flags;
        public ushort Durability;
        public bool Occupied => Owner == 1 || Owner == 2;
        public bool Anchored => (Flags & 1) != 0;
        public bool SurfaceAttached => (Flags & 2) != 0;
        public static BlockCell Create(byte owner, bool anchored = false, bool surfaceAttached = false)
            => new() { Owner = owner, Flags = (byte)((anchored || surfaceAttached ? 1 : 0) | (surfaceAttached ? 2 : 0)), Durability = 100 };
    }
}
