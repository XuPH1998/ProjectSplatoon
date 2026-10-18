using UnityEngine;

namespace Splatoon.Blocks
{
    // Local query identity only. A known but stale contact must never become an unknown point query.
    public readonly struct BlockQueryContact
    {
        readonly byte _kind;
        public readonly BlockTerrain Terrain;
        public readonly BlockWorld SourceWorld;
        public readonly BlockContact Contact;
        public readonly ulong FallingRunId;
        public readonly int FallingIndex;
        public readonly double SimulationTime;
        public readonly uint WorldRevision, Round;
        public readonly BlockDynamicBody DynamicBody;
        public bool IsKnown => _kind != 0;

        BlockQueryContact(byte kind, BlockTerrain terrain, BlockContact contact, ulong runId, int index, BlockDynamicBody dynamicBody = null)
        {
            _kind = kind; Terrain = terrain; SourceWorld = terrain != null ? terrain.World : null;
            Contact = contact; FallingRunId = runId; FallingIndex = index;
            SimulationTime = terrain != null ? terrain.SimulationTime : 0;
            WorldRevision = SourceWorld != null ? SourceWorld.Revision : 0;
            Round = SourceWorld != null ? SourceWorld.Round : 0;
            DynamicBody = dynamicBody;
        }

        internal static BlockQueryContact Stable(BlockTerrain terrain, BlockContact contact)
            => new(1, terrain, contact, 0, -1);

        internal static BlockQueryContact Dynamic(BlockDynamicBody body, Vector3 point, Vector3 normal)
            => body != null && body.TryContact(point, normal, out var contact)
                ? new BlockQueryContact(3, BlockTerrain.Current, contact, 0, -1, body) : default;

        internal static BlockQueryContact Falling(BlockTerrain terrain, BlockFallingHit hit)
        {
            int index = -1;
            if (terrain != null && terrain.World != null && terrain.World.Falling.TryGetValue(hit.RunId, out var run))
            {
                int candidate = hit.Cell.y - run.Bottom.y;
                if (hit.Cell.x == run.Bottom.x && hit.Cell.z == run.Bottom.z &&
                    candidate >= 0 && candidate < run.Cells.Length) index = candidate;
            }
            var contact = new BlockContact { Owner = hit.Owner, Point = hit.Point, Normal = hit.Normal,
                OrientationNormal = hit.Normal, Stable = false, Cell = hit.Cell,
                Revision = terrain != null ? terrain.Revision : 0 };
            return new BlockQueryContact(2, terrain, contact, hit.RunId, index);
        }

        public bool TryResolveCurrent(out BlockContact contact)
        {
            contact = default;
            var current = BlockTerrain.Current;
            if (_kind == 3)
            {
                if (current == null || current != Terrain || DynamicBody == null || current.Dynamic == null ||
                    current.Dynamic.Round != Round || !current.Dynamic.Contains(DynamicBody) ||
                    DynamicBody.Id != Contact.DynamicId || DynamicBody.Owner != Contact.Owner) return false;
                contact = Contact; return true;
            }
            if (!IsKnown || current == null || !current.Initialized || !ReferenceEquals(current, Terrain) ||
                SourceWorld == null || !ReferenceEquals(current.World, SourceWorld) || SourceWorld.Round != Round ||
                !Contact.IsBlock) return false;
            if (_kind == 1)
            {
                // Interior overlap may borrow a neighboring chunk's collider as its handle.
                // The exact occupied cell and its chunk revision define this stable contact.
                if (!Contact.Stable || Contact.Chunk != BlockWorld.ChunkAt(Contact.Cell) ||
                    !SourceWorld.TryGet(Contact.Cell, out var cell) || !cell.Occupied || cell.Owner != Contact.Owner ||
                    SourceWorld.ChunkRevision(Contact.Chunk) != Contact.Revision) return false;
            }
            else if (_kind == 2)
            {
                // Do not resolve fracture aliases or search a new pose from a shared surface point.
                if (Contact.Stable || current.SimulationTime != SimulationTime || SourceWorld.Revision != WorldRevision ||
                    !SourceWorld.Falling.TryGetValue(FallingRunId, out var run) || FallingIndex < 0 ||
                    FallingIndex >= run.Cells.Length || run.Bottom + Vector3Int.up * FallingIndex != Contact.Cell ||
                    !run.Cells[FallingIndex].Occupied || run.Cells[FallingIndex].Owner != Contact.Owner) return false;
            }
            else return false;
            contact = Contact; return true;
        }
    }
}
