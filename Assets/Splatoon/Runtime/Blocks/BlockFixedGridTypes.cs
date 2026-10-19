using System;
using UnityEngine;

namespace Splatoon.Blocks
{
    public struct BlockFixedGridHandle : IEquatable<BlockFixedGridHandle>
    {
        public uint Epoch, Round, Generation;
        public ulong Id;
        public bool IsValid => Epoch != 0 && Generation != 0 && Id != 0;
        public bool Equals(BlockFixedGridHandle other) => Epoch == other.Epoch && Round == other.Round && Generation == other.Generation && Id == other.Id;
        public override bool Equals(object obj) => obj is BlockFixedGridHandle other && Equals(other);
        public override int GetHashCode() => unchecked((((int)Epoch * 397 ^ (int)Round) * 397 ^ (int)Generation) * 397 ^ Id.GetHashCode());
        public static bool operator ==(BlockFixedGridHandle a, BlockFixedGridHandle b) => a.Equals(b);
        public static bool operator !=(BlockFixedGridHandle a, BlockFixedGridHandle b) => !a.Equals(b);
    }

    public struct BlockFixedGridMapSupport
    {
        public int Id, BaseCellY;
        public float Height;
        public BlockSurfaceKey SurfaceKey;
        public BlockFixedGridMapSupport(int id, float height, BlockSurfaceKey surfaceKey)
        { Id = id; Height = height; BaseCellY = GridBaseY(height); SurfaceKey = surfaceKey; }
        public static int GridBaseY(float height)
        {
            double scaled = height / (double)BlockWorld.CellSize;
            double nearest = Math.Round(scaled);
            return (int)Math.Floor(Math.Abs(height - nearest * BlockWorld.CellSize) < .00001 ? nearest : scaled);
        }
    }

    public struct BlockFixedGridState
    {
        public BlockFixedGridHandle Handle;
        public Vector3Int Cell;
        public byte Owner;
        public BlockFixedGridMapSupport Support;
        public uint Revision;
    }

    public interface IBlockFixedGridEnvironment
    {
        bool TryGetMapSupport(Vector3Int fromCell, out BlockFixedGridMapSupport support);
        bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase, bool relocating, out string reason);
        bool Visible(Vector3 from, Vector3 to);
    }

    // Prepare must not move actors. Recovery is applied after the data commit and before observers.
    public interface IBlockFixedGridCommitGuard
    {
        bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason);
        void CommitPreparedRecovery();
    }

    public interface IBlockFixedGridRecoveryRollback
    {
        void RollbackPreparedRecovery();
    }

    public interface IBlockFixedGridPaintAdmission
    {
        bool PaintAllowed(BlockSurfaceKey key, out string reason);
    }

    public readonly struct BlockFixedGridCommit
    {
        public readonly uint Epoch, Round, Revision, SurfaceRevision;
        public readonly bool Frozen;
        public readonly Vector3Int[] ChangedCells;
        public readonly BlockSurfaceKey[] ChangedPaintKeys;
        public readonly BlockMutationResult Mutation;
        public readonly int RelocatedCount, ConvertedCount;
        public BlockFixedGridCommit(uint epoch, uint round, uint revision, uint surfaceRevision, bool frozen,
            Vector3Int[] changedCells, BlockMutationResult mutation, int relocated = 0, int converted = 0, BlockSurfaceKey[] changedPaintKeys = null)
        { Epoch = epoch; Round = round; Revision = revision; SurfaceRevision = surfaceRevision; Frozen = frozen;
            ChangedCells = changedCells; Mutation = mutation; RelocatedCount = relocated; ConvertedCount = converted;
            ChangedPaintKeys = changedPaintKeys ?? Array.Empty<BlockSurfaceKey>(); }
    }

    public sealed class BlockFixedGridSnapshot
    {
        public bool Full, Frozen;
        public uint Epoch, Round, BaseRevision, Revision, SurfaceBaseRevision, SurfaceRevision;
        public ulong NextId;
        public BlockFixedGridState[] Upserts = Array.Empty<BlockFixedGridState>();
        public BlockFixedGridHandle[] Removed = Array.Empty<BlockFixedGridHandle>();
        public byte[] SurfaceBytes = Array.Empty<byte>();
    }

    public sealed class BlockFixedGridPreparedImport
    {
        internal BlockFixedGridWorld Owner;
        internal uint SourceEpoch, SourceRound, SourceRevision, SourceSurfaceRevision;
        internal BlockFixedGridSnapshot Snapshot;
        internal BlockSurfaceWorld Surface;
        internal bool Consumed;
    }
}
