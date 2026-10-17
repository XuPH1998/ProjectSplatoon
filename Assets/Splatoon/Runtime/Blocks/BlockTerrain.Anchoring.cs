using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // One neighbourhood query followed by at most four horizontal sweeps.
        // The construction stage may reserve this cost without widening its budget.
        const int StaticWallAnchorMaxQueryChecks = 5;
        const float StaticWallAnchorTolerance = .00001f;
        const float StaticWallAnchorProbeLength = BlockWorld.CellSize * 2.25f;
        static readonly Vector3[] StaticWallAnchorDirections =
        {
            Vector3.right, Vector3.left, Vector3.forward, Vector3.back
        };
        bool StaticMapWallAnchor(Vector3Int cell)
        {
            if (_staticColliders.Count == 0) return false;
            Vector3 center = BlockWorld.Center(cell);
            int mask = PlayerMotorSimulation.WorldMask & ~(1 << TerrainLayer);
            float half = BlockWorld.CellSize * .45f; // Same construction envelope as BuildMapOverlapCached.
            int nearby = Physics.OverlapBoxNonAlloc(center,
                new Vector3(StaticWallAnchorProbeLength, half, StaticWallAnchorProbeLength),
                _overlaps, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
            bool registered = nearby == _overlaps.Length;
            for (int i = 0; i < nearby && !registered; i++) registered = _staticColliders.Contains(_overlaps[i]);
            if (!registered) return false;

            foreach (var direction in StaticWallAnchorDirections)
            {
                int count = Physics.BoxCastNonAlloc(center, Vector3.one * half, direction, _rayHits,
                    Quaternion.identity, BlockWorld.CellSize, mask, QueryTriggerInteraction.Ignore);
                // A full buffer is not evidence of a wall. Only an actual returned,
                // registered hit may anchor; no allocation or unbounded retry is needed.
                for (int i = 0; i < count; i++)
                {
                    var hit = _rayHits[i];
                    // Production callers already reject MapOverlap(cell). Exclude
                    // initial-overlap sweep sentinels, whose normal opposes the sweep.
                    if (hit.distance <= 0f || !_staticColliders.Contains(hit.collider) ||
                        Mathf.Abs(hit.normal.y) >= .7f) continue;
                    Vector3 normal = hit.normal;
                    float gap = Vector3.Dot(center - hit.point, normal)
                        - half * (Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z));
                    // Sweep the actual construction envelope one horizontal grid
                    // step. Finite wall ends must intersect that swept neighbour,
                    // while slanted/non-aligned faces retain their first-row gap.
                    float step = BlockWorld.CellSize * Mathf.Max(Mathf.Abs(normal.x), Mathf.Abs(normal.z));
                    if (gap >= -StaticWallAnchorTolerance && gap < step - StaticWallAnchorTolerance) return true;
                }
            }
            return false;
        }
    }
}
