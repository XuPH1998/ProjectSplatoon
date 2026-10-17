using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    [Flags]
    public enum BlockPublicationOperation
    {
        None = 0, OrdinaryAttack = 1, Destruction = 2, Gravity = 4, Metadata = 8
    }

    // Used by the Reports Terrain candidate; not applied to Assets or accepted.
    // Keys are the complete data/face-neighbor footprint, not only mesh pieces.
    public sealed class BlockPublicationPolicy
    {
        const BlockPublicationOperation All = BlockPublicationOperation.OrdinaryAttack
            | BlockPublicationOperation.Destruction | BlockPublicationOperation.Gravity | BlockPublicationOperation.Metadata;
        public int Version { get; }
        public int OrdinaryAttackKeys { get; }
        public int DestructionKeys { get; }
        public int GravityKeys { get; }
        public int MetadataKeys { get; }
        public int FrameKeys { get; }
        public double FrameSynchronousMilliseconds { get; }
        // Existing stage accounting and real wall/upload limits stay unchanged.
        public const int MaximumChecks = 4096;
        public const int CommandChecksWithGravity = 3072;
        public const int GravityChecksWithCommands = 4096, GravityChecksAlone = 8192;
        public const double CommandMillisecondsAlone = .75, CommandMillisecondsWithGravity = .5;
        public const double GravityMillisecondsAlone = 1.5, GravityMillisecondsWithCommands = 1.0;
        public const double UploadMilliseconds = 1;

        public BlockPublicationPolicy(int version, int ordinaryAttackKeys, int destructionKeys,
            int gravityKeys, int metadataKeys, int frameKeys, double frameSynchronousMilliseconds)
        {
            if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
            // Eight is the explicit compatibility envelope. Smaller windows may
            // reject an indivisible legacy/cohort footprint and are not supported here.
            if (ordinaryAttackKeys < 8 || destructionKeys < 8 || gravityKeys < 8 || metadataKeys < 8)
                throw new ArgumentOutOfRangeException(nameof(ordinaryAttackKeys), "Every operation must admit the full compatibility footprint.");
            int largest = Math.Max(Math.Max(ordinaryAttackKeys, destructionKeys), Math.Max(gravityKeys, metadataKeys));
            if (frameKeys < largest) throw new ArgumentOutOfRangeException(nameof(frameKeys),
                "A staged window must fit at least once on a fresh frame.");
            if (double.IsNaN(frameSynchronousMilliseconds) || double.IsInfinity(frameSynchronousMilliseconds)
                || frameSynchronousMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(frameSynchronousMilliseconds));
            Version = version; OrdinaryAttackKeys = ordinaryAttackKeys; DestructionKeys = destructionKeys;
            GravityKeys = gravityKeys; MetadataKeys = metadataKeys; FrameKeys = frameKeys;
            FrameSynchronousMilliseconds = frameSynchronousMilliseconds;
        }

        public BlockPublicationWindow Capture(BlockPublicationOperation operations)
        {
            if (operations == BlockPublicationOperation.None || (operations & ~All) != 0)
                throw new ArgumentOutOfRangeException(nameof(operations));
            int limit = FrameKeys;
            if ((operations & BlockPublicationOperation.OrdinaryAttack) != 0) limit = Math.Min(limit, OrdinaryAttackKeys);
            if ((operations & BlockPublicationOperation.Destruction) != 0) limit = Math.Min(limit, DestructionKeys);
            if ((operations & BlockPublicationOperation.Gravity) != 0) limit = Math.Min(limit, GravityKeys);
            if ((operations & BlockPublicationOperation.Metadata) != 0) limit = Math.Min(limit, MetadataKeys);
            return new BlockPublicationWindow(this, operations, limit);
        }

        public static BlockPublicationPolicy CompatibilityEight(int version = 1)
            => new(version, 8, 8, 8, 8, 8, 3);
        // Candidate default for the next real measurement, not an accepted result.
        public static BlockPublicationPolicy CandidateDefault(int version = 2)
            => ExpandedUncalibrated(version);
        // Example for future calibration, deliberately not a selected default.
        public static BlockPublicationPolicy ExpandedUncalibrated(int version = 2)
            => new(version, 8, 32, 32, 8, 32, 3);
    }

    public sealed class BlockPublicationWindow
    {
        public BlockPublicationPolicy Policy { get; }
        public BlockPublicationOperation Operations { get; }
        public int MaximumChangedKeys { get; }
        internal BlockPublicationWindow(BlockPublicationPolicy policy, BlockPublicationOperation operations, int maximum)
        { Policy = policy; Operations = operations; MaximumChangedKeys = maximum; }
        public bool Includes(BlockPublicationOperation operation) => (Operations & operation) == operation;

        public bool CanIncludeCell(ISet<Vector3Int> changed, Vector3Int cell)
        {
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            if (changed.Count > MaximumChangedKeys) return false;
            var key = BlockWorld.ChunkAt(cell); var local = cell - BlockWorld.ChunkOrigin(key);
            int extra = changed.Contains(key) ? 0 : 1;
            if (local.x == 0 && !changed.Contains(key + Vector3Int.left)) extra++;
            if (local.x == BlockWorld.ChunkSize - 1 && !changed.Contains(key + Vector3Int.right)) extra++;
            if (local.y == 0 && !changed.Contains(key + Vector3Int.down)) extra++;
            if (local.y == BlockWorld.ChunkSize - 1 && !changed.Contains(key + Vector3Int.up)) extra++;
            if (local.z == 0 && !changed.Contains(key + new Vector3Int(0, 0, -1))) extra++;
            if (local.z == BlockWorld.ChunkSize - 1 && !changed.Contains(key + new Vector3Int(0, 0, 1))) extra++;
            return (long)changed.Count + extra <= MaximumChangedKeys;
        }

        // Supply a captured key collection, never a live command/cell iterator:
        // examining admission must not advance an attack or gravity cursor.
        public bool CanIncludeFootprint(ISet<Vector3Int> changed, IReadOnlyCollection<Vector3Int> completeFootprint)
        {
            if (changed == null || completeFootprint == null) throw new ArgumentNullException();
            if (changed.Count > MaximumChangedKeys) return false;
            var union = new HashSet<Vector3Int>(changed);
            // Admission may reject this complete footprint. The caller must keep
            // the entire cursor for a later stage; this helper never truncates it.
            foreach (var key in completeFootprint) if (union.Add(key) && union.Count > MaximumChangedKeys) return false;
            return true;
        }
    }
}
