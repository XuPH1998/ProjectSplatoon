using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        public bool IsStableCollider(Collider collider) => collider != null && _colliderChunks.ContainsKey(collider);

        /// <summary>Sphere overlap with the published occupied voxel union, independent of mesh interior faces.</summary>
        public bool TryOverlapStable(Vector3 origin, float radius, Vector3 fallbackNormal,
            out Collider collider, out BlockContact contact, out float distance)
        {
            collider = null; contact = default; distance = float.PositiveInfinity;
            if (!Initialized || !Finite(origin) || !float.IsFinite(radius) || radius < 0) return false;
            if (FixedGridMode)
            {
                if (!TryOverlapFixed(origin, radius, fallbackNormal, out var fixedHit)) return false;
                contact = FixedContact(fixedHit); distance = fixedHit.Distance; return true;
            }
            var low = BlockWorld.CellAt(origin - Vector3.one * (radius + .000001f));
            var high = BlockWorld.CellAt(origin + Vector3.one * radius);
            float nearest = float.PositiveInfinity, radiusSquared = radius * radius;
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!World.TryGet(cell, out var own)) continue;
                Vector3 min = (Vector3)cell * BlockWorld.CellSize, max = min + Vector3.one * BlockWorld.CellSize;
                // This clamp is the exact closest point of one occupied cube, not
                // the bounds of a concave chunk containing potentially empty cells.
                var point = new Vector3(Mathf.Clamp(origin.x, min.x, max.x), Mathf.Clamp(origin.y, min.y, max.y), Mathf.Clamp(origin.z, min.z, max.z));
                Vector3 delta = origin - point; float squared = delta.sqrMagnitude;
                if (squared > radiusSquared || squared >= nearest) continue;
                var chunk = BlockWorld.ChunkAt(cell);
                Collider candidate = _views.TryGetValue(chunk, out var view) ? view.Collider : InteriorCollider(chunk);
                if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy) continue;
                Vector3 normal = squared > .00000001f ? delta.normalized
                    : fallbackNormal.sqrMagnitude > .00000001f ? fallbackNormal.normalized : Vector3.up;
                collider = candidate; nearest = squared;
                contact = new BlockContact { Owner = own.Owner, Point = point, Normal = normal, OrientationNormal = normal,
                    Kind = own.SurfaceAttached ? BlockContactKind.SurfaceAttached : BlockContactKind.Legacy,
                    Stable = true, Climbable = Mathf.Abs(normal.y) < .7f, Cell = cell, Chunk = chunk, Revision = World.ChunkRevision(chunk) };
            }
            distance = Mathf.Sqrt(nearest); return collider != null;
        }

        Collider InteriorCollider(Vector3Int chunk)
        {
            // A fully enclosed solid chunk has no surface triangles or Collider.
            // Use a real published terrain collider solely as its identity handle;
            // the point/cell/revision still come from the occupied voxel above.
            Collider closest = null; float nearest = float.PositiveInfinity;
            foreach (var pair in _views)
            {
                var candidate = pair.Value.Collider;
                if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy) continue;
                float squared = ((Vector3)(pair.Key - chunk)).sqrMagnitude;
                if (squared >= nearest) continue;
                nearest = squared; closest = candidate;
            }
            return closest;
        }
    }
}
