using System;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed partial class PlayerMotorSimulation
    {
        [ThreadStatic] static RaycastHit[] _groundFootprintHits;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static int GroundFootprintBufferCapacity => _groundFootprintHits?.Length ?? 0;
        public static int GroundFootprintBufferGrowths { get; private set; }
        public static int LastGroundFootprintHitCount { get; private set; }
#endif

        // Used only after the unchanged centre/cardinal ray priority has missed.
        // A complete multi-collider sweep avoids letting the first wall side hide
        // a valid nearby top. Candidate ranking never reads ownership.
        static bool SphereFootprintGround(Vector3 feet, float slopeLimit, float radius, out TraversalContact contact, ref GroundQueryDetails details)
        {
            contact = default;
            if (radius <= .0001f) return false;
            details.EvaluatedPaths |= GroundEvaluated.SphereCast;
            _groundFootprintHits ??= new RaycastHit[32];
            Vector3 origin = feet + Vector3.up * (radius + .08f);
            int count;
            while (true)
            {
                count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, _groundFootprintHits, .16f,
                    WorldMask, QueryTriggerInteraction.Ignore);
                if (count < _groundFootprintHits.Length) break;
                // A full array is not evidence of a complete hit set. Retry the
                // entire unchanged query with larger storage, without a cap.
                Array.Resize(ref _groundFootprintHits, checked(_groundFootprintHits.Length * 2));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                GroundFootprintBufferGrowths++;
#endif
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LastGroundFootprintHitCount = count;
#endif
            float minimumY = Mathf.Cos(slopeLimit * Mathf.Deg2Rad);
            bool found = false;
            float bestDistance = float.PositiveInfinity, bestPointDistance = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var sweep = _groundFootprintHits[i];
                if (sweep.collider == null || !sweep.collider.enabled || !sweep.collider.gameObject.activeInHierarchy ||
                    sweep.distance <= 0 || sweep.normal.y < minimumY) continue;
                // A sweep normal can be radial at an edge. Probe just inside the
                // candidate surface, on this exact collider, for a real face.
                Vector3 inside = sweep.point - Vector3.ProjectOnPlane(sweep.normal, Vector3.up) * .002f;
                if (!sweep.collider.Raycast(new Ray(inside + Vector3.up * .04f, Vector3.down), out var face, .08f) ||
                    face.normal.y < minimumY || !GroundFaceWithinLowerCap(feet, radius, face.point)) continue;
                TraversalContact candidate;
                var terrain = BlockTerrain.Current;
                if (terrain.TrySurfaceContactFromMap(face, out var surface))
                {
                    candidate = new TraversalContact(surface);
                }
                else if (face.collider.GetComponentInParent<BlockDynamicBody>() != null)
                {
                    if (!TryBlockTraversalContact(face, out candidate)) continue;
                }
                else if (terrain.TryContact(face, out var block))
                {
                    if (!block.Stable || !terrain.World.TryGet(block.Cell, out _) ||
                        terrain.World.TryGet(block.Cell + Vector3Int.up, out _) ||
                        Mathf.Abs(face.point.y - (block.Cell.y + 1) * BlockWorld.CellSize) > .002f) continue;
                    candidate = new TraversalContact(block);
                }
                else
                {
                    // A stale/unpublished/falling terrain collider is not a
                    // neutral map face. Ordinary authored map geometry remains
                    // eligible for neutral swimming without friendly benefits.
                    if (face.collider.gameObject.layer == PlayerBlockPushBridge.DynamicLayer ||
                        BlockTerrain.IsBlockGeometry(face.collider)) continue;
                    candidate = new TraversalContact(face);
                }
                float pointDistance = (face.point - feet).sqrMagnitude;
                bool better = !found || sweep.distance < bestDistance - .00001f ||
                    Mathf.Abs(sweep.distance - bestDistance) <= .00001f &&
                    (pointDistance < bestPointDistance - .00001f || Mathf.Abs(pointDistance - bestPointDistance) <= .00001f &&
                        CompareGroundPoint(candidate.Point, contact.Point) < 0);
                if (!better) continue;
                found = true; bestDistance = sweep.distance; bestPointDistance = pointDistance; contact = candidate;
                details.Winner = GroundWinner.SphereCast;
            }
            // Initial overlap can hide every subsequent face on one chunk
            // collider. Consider its current exposed tops even if another
            // collider already supplied a (potentially farther) candidate.
            details.EvaluatedPaths |= GroundEvaluated.OverlapCheck;
            if (Physics.CheckSphere(origin, radius, WorldMask & (1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore))
            {
                details.EvaluatedPaths |= GroundEvaluated.PublishedOverlap;
                PublishedOverlapGround(feet, radius, minimumY, origin, ref found, ref bestDistance, ref bestPointDistance, ref contact, ref details);
            }
            return found;
        }
        static void PublishedOverlapGround(Vector3 feet, float radius, float minimumY, Vector3 origin,
            ref bool found, ref float bestDistance, ref float bestPointDistance, ref TraversalContact contact, ref GroundQueryDetails details)
        {
            var terrain = BlockTerrain.Current;
            var world = terrain.World;
            float cellSize = BlockWorld.CellSize;
            int minX = Mathf.FloorToInt((feet.x - radius) / cellSize), maxX = Mathf.FloorToInt((feet.x + radius) / cellSize);
            int minZ = Mathf.FloorToInt((feet.z - radius) / cellSize), maxZ = Mathf.FloorToInt((feet.z + radius) / cellSize);
            int minY = Mathf.CeilToInt((feet.y - .08001f) / cellSize) - 1;
            int maxY = Mathf.FloorToInt((feet.y + radius * (1 - minimumY) + .08001f) / cellSize) - 1;
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!world.TryGet(cell, out _) || world.TryGet(cell + Vector3Int.up, out _)) continue;
                var point = new Vector3(Mathf.Clamp(feet.x, x * cellSize + .002f, (x + 1) * cellSize - .002f),
                    (y + 1) * cellSize, Mathf.Clamp(feet.z, z * cellSize + .002f, (z + 1) * cellSize - .002f));
                if (!GroundFaceWithinLowerCap(feet, radius, point)) continue;
                float radialSquared = Vector3.ProjectOnPlane(point - feet, Vector3.up).sqrMagnitude;
                float capCosine = Mathf.Sqrt(Mathf.Max(0, 1 - radialSquared / (radius * radius)));
                if (capCosine < minimumY) continue;
                float distance = Mathf.Max(0, origin.y - point.y - radius * capCosine);
                float pointDistance = (point - feet).sqrMagnitude;
                bool better = !found || distance < bestDistance - .00001f ||
                    Mathf.Abs(distance - bestDistance) <= .00001f &&
                    (pointDistance < bestPointDistance - .00001f || Mathf.Abs(pointDistance - bestPointDistance) <= .00001f &&
                        CompareGroundPoint(point, contact.Point) < 0);
                if (!better) continue;
                // World occupancy alone is not collision proof. The fresh ray
                // must hit this exact current exposed cell through a published
                // collider; a map obstruction or missing collider rejects it.
                if (!Physics.Raycast(point + Vector3.up * .04f, Vector3.down, out var face, .08f,
                    WorldMask, QueryTriggerInteraction.Ignore) || face.normal.y < minimumY ||
                    !terrain.TryContact(face, out var block) || !block.Stable || block.Cell != cell ||
                    Mathf.Abs(face.point.y - point.y) > .002f) continue;
                found = true; bestDistance = distance; bestPointDistance = pointDistance;
                contact = new TraversalContact(block);
                details.Winner = GroundWinner.PublishedOverlap;
            }
        }

        static bool GroundFaceWithinLowerCap(Vector3 feet, float radius, Vector3 point)
        {
            Vector3 horizontal = Vector3.ProjectOnPlane(point - feet, Vector3.up);
            float radialSquared = horizontal.sqrMagnitude, radiusSquared = radius * radius;
            if (radialSquared > radiusSquared || point.y > feet.y + radius) return false;
            float capY = feet.y + radius - Mathf.Sqrt(Mathf.Max(0, radiusSquared - radialSquared));
            // Keep the original ground reach. A full flat-radius footprint or
            // the controller step height must not bridge a vertical gap.
            return Mathf.Abs(point.y - capY) <= .08001f;
        }
        static int CompareGroundPoint(Vector3 a, Vector3 b)
        {
            int order = a.x.CompareTo(b.x); if (order != 0) return order;
            order = a.y.CompareTo(b.y); return order != 0 ? order : a.z.CompareTo(b.z);
        }
    }
}
