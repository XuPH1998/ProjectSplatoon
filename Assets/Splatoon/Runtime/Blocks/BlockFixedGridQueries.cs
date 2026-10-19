using System;
using UnityEngine;

namespace Splatoon.Blocks
{
    public readonly struct BlockFixedGridHit
    {
        public readonly BlockFixedGridState State;
        public readonly Vector3 Point, Normal;
        public readonly float Distance;
        public BlockFixedGridHit(BlockFixedGridState state, Vector3 point, Vector3 normal, float distance)
        { State = state; Point = point; Normal = normal; Distance = distance; }
    }

    /// <summary>Exact occupied-cube queries. Rendering and Collider publication never gate a hit.</summary>
    public static class BlockFixedGridQueries
    {
        static readonly Unity.Profiling.ProfilerMarker FixedSupportQueryCpu = new("Splatoon.Fixed.SupportQuery");
        const float Epsilon = .000001f;

        public static bool Cast(BlockFixedGridWorld world, Vector3 origin, Vector3 direction, float distance,
            float radius, out BlockFixedGridHit hit)
            => CastCapsule(world, origin, 0, radius, direction, distance, out hit);

        // An upright capsule is a sphere swept along a vertical segment. Its Minkowski
        // sum with an AABB is the same rounded AABB with the vertical half-segment added.
        public static bool CastCapsule(BlockFixedGridWorld world, Vector3 center, float halfSegment, float radius,
            Vector3 direction, float distance, out BlockFixedGridHit hit, float initialContactTolerance = 0)
        {
            hit = default;
            if (world == null || world.Count == 0 || !Finite(center) || !Finite(direction) || !float.IsFinite(distance) || distance < 0 ||
                !float.IsFinite(radius) || radius < 0 || !float.IsFinite(halfSegment) || halfSegment < 0) return false;
            float length = direction.magnitude;
            if (length <= Epsilon) return OverlapCapsule(world, center, halfSegment, radius, -direction, out hit);
            direction /= length;
            Vector3Int cell = BlockWorld.CellAt(center);
            int horizontal = Mathf.CeilToInt(radius / BlockWorld.CellSize) + 1;
            int vertical = Mathf.CeilToInt((radius + halfSegment) / BlockWorld.CellSize) + 1;
            // A short motor sweep fits in the old first DDA neighborhood. Query only
            // its complete swept capsule AABB, retaining that neighborhood's order.
            // Long casts and any unproven numeric bound keep the original DDA path.
            if (distance <= BlockWorld.CellSize && TryShortSweptCells(center, halfSegment, radius,
                direction, distance, cell, horizontal, vertical, out var shortLow, out var shortHigh))
                return CastShortBounds(world, center, halfSegment, radius, direction, distance, shortLow, shortHigh, out hit, initialContactTolerance);
            Vector3Int step = new(Math.Sign(direction.x), Math.Sign(direction.y), Math.Sign(direction.z));
            Vector3 next = new(NextBoundary(center.x, direction.x, cell.x), NextBoundary(center.y, direction.y, cell.y), NextBoundary(center.z, direction.z, cell.z));
            Vector3 interval = new(Interval(direction.x), Interval(direction.y), Interval(direction.z));
            float best = distance + Epsilon;
            bool found = false;
            bool previous = false; Vector3Int previousLow = default, previousHigh = default;
            while (true)
            {
                var low = cell - new Vector3Int(horizontal, vertical, horizontal);
                var high = cell + new Vector3Int(horizontal, vertical, horizontal);
                for (int z = cell.z - horizontal; z <= cell.z + horizontal; z++)
                for (int x = cell.x - horizontal; x <= cell.x + horizontal; x++)
                foreach (var entry in world.QueryColumn(x, z))
                {
                    int y = entry.Key;
                    if (y < low.y) continue;
                    if (y > high.y) break;
                    // A straight DDA walk is monotone in every axis. Any occupied
                    // cell in the previous neighborhood has already had its full
                    // sweep tested; only newly entered slabs need a narrow phase.
                    if (previous && x >= previousLow.x && x <= previousHigh.x &&
                        z >= previousLow.z && z <= previousHigh.z && y >= previousLow.y && y <= previousHigh.y) continue;
                    if (!world.TryGet(entry.Value, out var state)) continue;
                    Bounds bounds = BlockFixedGridWorld.GetBounds(state);
                    Bounds expanded = bounds; expanded.Expand(Vector3.up * (halfSegment * 2));
                    // Keep the existing stable-ID tie band reachable even when a
                    // slightly earlier high-ID candidate was enumerated first.
                    if (!SweepSphereBox(center, direction, Mathf.Min(distance, best + Epsilon), radius, expanded,
                        out float time, out Vector3 normal)) continue;
                    // Moving tangentially along or away from an existing touching face must
                    // not block sliding; a true initial penetration still rejects the move.
                    if (time <= Epsilon &&
                        (Vector3.Dot(direction, normal) >= -Epsilon &&
                            (center - expanded.ClosestPoint(center)).sqrMagnitude >= radius * radius - Epsilon ||
                         SeparatesFromAcceptedInitialContact(center, expanded, radius, direction, initialContactTolerance))) continue;
                    if (found && (time > best + Epsilon || Mathf.Abs(time - best) <= Epsilon && state.Handle.Id >= hit.State.Handle.Id)) continue;
                    best = time; found = true;
                    Vector3 sphereCenter = center + direction * time;
                    Vector3 point = bounds.ClosestPoint(sphereCenter - normal * radius);
                    hit = new BlockFixedGridHit(state, point, normal, time);
                }
                previous = true; previousLow = low; previousHigh = high;
                float advance = Mathf.Min(next.x, Mathf.Min(next.y, next.z));
                if (advance > distance || advance > best + Epsilon || float.IsPositiveInfinity(advance)) break;
                if (next.x <= advance + Epsilon) { cell.x += step.x; next.x += interval.x; }
                if (next.y <= advance + Epsilon) { cell.y += step.y; next.y += interval.y; }
                if (next.z <= advance + Epsilon) { cell.z += step.z; next.z += interval.z; }
            }
            return found;
        }

        static bool TryShortSweptCells(Vector3 center, float halfSegment, float radius, Vector3 direction,
            float distance, Vector3Int firstCell, int horizontal, int vertical, out Vector3Int low, out Vector3Int high)
        {
            low = high = default;
            for (int axis = 0; axis < 3; axis++)
            {
                // Work in double so constructing the broadphase does not round the
                // analytic segment inward. Padding covers the narrow phase's 1e-12
                // squared-distance tolerance and float cell/bounds construction.
                double start = center[axis], end = start + (double)direction[axis] * distance;
                double extent = (double)radius + (axis == 1 ? halfSegment : 0);
                double scale = Math.Max(1, Math.Max(extent, Math.Max(Math.Abs(start), Math.Abs(end))));
                double padding = 4 * Epsilon + 8 * 1.1920928955078125e-7 * scale;
                double a = Math.Floor((Math.Min(start, end) - extent - padding) / BlockWorld.CellSize);
                double b = Math.Floor((Math.Max(start, end) + extent + padding) / BlockWorld.CellSize);
                if (a < int.MinValue || b > int.MaxValue || !double.IsFinite(a) || !double.IsFinite(b)) return false;
                low[axis] = (int)a; high[axis] = (int)b;
                int neighborhood = axis == 1 ? vertical : horizontal;
                // Besides bounding work, this proves all short candidates would
                // have been visited in the old first neighborhood, in the same
                // z/x/y order. Preserve its stable-ID epsilon tie behavior.
                if (a < (long)firstCell[axis] - neighborhood || b > (long)firstCell[axis] + neighborhood) return false;
            }
            return true;
        }

        static bool CastShortBounds(BlockFixedGridWorld world, Vector3 center, float halfSegment, float radius,
            Vector3 direction, float distance, Vector3Int low, Vector3Int high, out BlockFixedGridHit hit, float initialContactTolerance)
        {
            hit = default; bool found = false; float best = distance + Epsilon;
            for (int z = low.z; z <= high.z; z++) for (int x = low.x; x <= high.x; x++)
            foreach (var entry in world.QueryColumn(x, z))
            {
                if (entry.Key < low.y) continue;
                if (entry.Key > high.y) break;
                if (!world.TryGet(entry.Value, out var state)) continue;
                Bounds bounds = BlockFixedGridWorld.GetBounds(state);
                Bounds expanded = bounds; expanded.Expand(Vector3.up * (halfSegment * 2));
                // Keep exactly the DDA narrow phase, sliding rule and stable-ID
                // tie band. The swept AABB only selects candidates, never hits.
                if (!SweepSphereBox(center, direction, Mathf.Min(distance, best + Epsilon), radius, expanded,
                    out float time, out Vector3 normal)) continue;
                if (time <= Epsilon &&
                    (Vector3.Dot(direction, normal) >= -Epsilon &&
                        (center - expanded.ClosestPoint(center)).sqrMagnitude >= radius * radius - Epsilon ||
                     SeparatesFromAcceptedInitialContact(center, expanded, radius, direction, initialContactTolerance))) continue;
                if (found && (time > best + Epsilon || Mathf.Abs(time - best) <= Epsilon && state.Handle.Id >= hit.State.Handle.Id)) continue;
                best = time; found = true;
                Vector3 sphereCenter = center + direction * time;
                Vector3 point = bounds.ClosestPoint(sphereCenter - normal * radius);
                hit = new BlockFixedGridHit(state, point, normal, time);
            }
            return found;
        }

        // Opt-in for a motor whose complete initial pose passed its existing endpoint check.
        // Outside a convex box, squared distance along a line is convex. A nonnegative
        // initial derivative cannot worsen this same box's shallow initial contact.
        // All other candidates retain their exact full-radius sweep.
        static bool SeparatesFromAcceptedInitialContact(Vector3 center, Bounds expanded, float radius,
            Vector3 direction, float initialContactTolerance)
        {
            if (!(initialContactTolerance > 0) || !float.IsFinite(initialContactTolerance) ||
                initialContactTolerance >= radius) return false;
            Vector3 delta = center - expanded.ClosestPoint(center);
            float squared = delta.sqrMagnitude;
            float acceptedRadius = Mathf.Max(0, radius - initialContactTolerance);
            if (squared <= acceptedRadius * acceptedRadius || squared <= Epsilon * Epsilon) return false;
            double separating = (double)direction.x * delta.x + (double)direction.y * delta.y + (double)direction.z * delta.z;
            return separating >= 0;
        }

        public static bool OverlapSphere(BlockFixedGridWorld world, Vector3 origin, float radius, Vector3 fallbackNormal,
            out BlockFixedGridHit hit) => OverlapCapsule(world, origin, 0, radius, fallbackNormal, out hit);

        public static bool OverlapCapsule(BlockFixedGridWorld world, Vector3 center, float halfSegment, float radius,
            Vector3 fallbackNormal, out BlockFixedGridHit hit, float penetrationTolerance = 0, float floorContactTolerance = 0)
        {
            hit = default;
            if (world == null || world.Count == 0 || !Finite(center) || radius < 0 || !float.IsFinite(radius) || halfSegment < 0 || !float.IsFinite(halfSegment)) return false;
            Vector3 extent = new(radius, radius + halfSegment, radius);
            var low = BlockWorld.CellAt(center - extent - Vector3.one * Epsilon);
            var high = BlockWorld.CellAt(center + extent + Vector3.one * Epsilon);
            float nearest = float.PositiveInfinity;
            float acceptedRadius = Mathf.Max(0, radius - penetrationTolerance);
            for (int z = low.z; z <= high.z; z++) for (int x = low.x; x <= high.x; x++)
            foreach (var entry in world.QueryColumn(x, z))
            {
                if (entry.Key < low.y) continue;
                if (entry.Key > high.y) break;
                if (!world.TryGet(entry.Value, out var state)) continue;
                Bounds bounds = BlockFixedGridWorld.GetBounds(state), expanded = bounds;
                expanded.Expand(Vector3.up * (halfSegment * 2));
                Vector3 point = expanded.ClosestPoint(center), delta = center - point;
                float squared = delta.sqrMagnitude;
                // Coplanar cells form one floor. A capsule settled within the
                // existing floor tolerance must not hit a neighbor's internal
                // rounded edge when its projection remains over that floor.
                // Keep isolated edges and holes exact, and keep scanning so an
                // accepted floor contact cannot hide another obstruction.
                double floorDepth = (double)bounds.max.y - ((double)center.y - halfSegment - acceptedRadius);
                if (squared <= acceptedRadius * acceptedRadius && delta.y > 0 &&
                    delta.x * delta.x + delta.z * delta.z > Epsilon * Epsilon &&
                    floorContactTolerance > 0 && floorDepth >= 0 && floorDepth < floorContactTolerance &&
                    InternalFloorSeam(world, state.Cell, center)) continue;
                // Native controllers can settle a fraction of a millimetre below a
                // data floor. This contact tolerance applies only to its flat +Y
                // face; walls, ceilings and rounded edge contacts keep their exact
                // test. Keep checking every candidate so a floor never hides a
                // simultaneous obstruction higher up the capsule.
                bool floorContact = delta.y > 0 && delta.x * delta.x + delta.z * delta.z <= Epsilon * Epsilon;
                float contactRadius = floorContact ? Mathf.Max(0, acceptedRadius - Mathf.Max(0, floorContactTolerance)) : acceptedRadius;
                if (squared > contactRadius * contactRadius || squared > nearest ||
                    squared == nearest && state.Handle.Id >= hit.State.Handle.Id) continue;
                Vector3 normal = squared > Epsilon * Epsilon ? delta.normalized : InsideNormal(center, expanded, fallbackNormal);
                nearest = squared;
                hit = new BlockFixedGridHit(state, bounds.ClosestPoint(center - normal * radius), normal, Mathf.Sqrt(squared));
            }
            return nearest < float.PositiveInfinity;
        }

        static bool InternalFloorSeam(BlockFixedGridWorld world, Vector3Int candidate, Vector3 center)
        {
            var under = BlockWorld.CellAt(center); under.y = candidate.y;
            int dx = candidate.x - under.x, dz = candidate.z - under.z;
            if (Mathf.Abs(dx) > 1 || Mathf.Abs(dz) > 1 || dx == 0 && dz == 0) return false;
            if (!ExposedFloorCell(world, under) || !ExposedFloorCell(world, candidate)) return false;
            // A diagonal is internal only when all four cells meet. Missing
            // either bridge leaves an exposed corner next to a hole or L edge.
            return dx == 0 || dz == 0 ||
                ExposedFloorCell(world, new Vector3Int(candidate.x, candidate.y, under.z)) &&
                ExposedFloorCell(world, new Vector3Int(under.x, candidate.y, candidate.z));
        }

        static bool ExposedFloorCell(BlockFixedGridWorld world, Vector3Int cell)
            => world.TryGet(cell, out _) && !world.TryGet(cell + Vector3Int.up, out _);

        public static bool OverlapBox(BlockFixedGridWorld world, Vector3 center, Vector3 half, Quaternion rotation)
        {
            if (world == null || world.Count == 0) return false;
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up, forward = rotation * Vector3.forward;
            Vector3 extent = Abs(right) * half.x + Abs(up) * half.y + Abs(forward) * half.z;
            Vector3Int low = BlockWorld.CellAt(center - extent), high = BlockWorld.CellAt(center + extent);
            for (int z = low.z; z <= high.z; z++) for (int x = low.x; x <= high.x; x++)
            foreach (var entry in world.QueryColumn(x, z))
            {
                if (entry.Key < low.y) continue;
                if (entry.Key > high.y) break;
                if (world.TryGet(entry.Value, out var state) &&
                    BoxesOverlap(BlockFixedGridWorld.GetBounds(state), center, half, right, up, forward)) return true;
            }
            return false;
        }

        public static bool Support(BlockFixedGridWorld world, Vector3 feet, float radius, float reach, out BlockFixedGridHit hit, float minimumY = 0)
        {
            using var timing = FixedSupportQueryCpu.Auto();
            hit = default;
            if (world == null || world.Count == 0) return false;
            var low = BlockWorld.CellAt(feet - new Vector3(radius, reach + BlockWorld.CellSize, radius));
            var high = BlockWorld.CellAt(feet + new Vector3(radius, radius + reach, radius));
            bool found = false; float best = float.PositiveInfinity;
            for (int z = low.z; z <= high.z; z++) for (int x = low.x; x <= high.x; x++)
            foreach (var entry in world.QueryColumn(x, z))
            {
                if (entry.Key < low.y) continue;
                if (entry.Key > high.y) break;
                if (!world.TryGet(entry.Value, out var state) || world.TryGet(state.Cell + Vector3Int.up, out _)) continue;
                var bounds = BlockFixedGridWorld.GetBounds(state);
                Vector3 point = new(Mathf.Clamp(feet.x, bounds.min.x, bounds.max.x), bounds.max.y, Mathf.Clamp(feet.z, bounds.min.z, bounds.max.z));
                float radial = (new Vector2(point.x - feet.x, point.z - feet.z)).sqrMagnitude;
                if (radial > radius * radius + Epsilon) continue;
                if (radius > Epsilon && Mathf.Sqrt(Mathf.Max(0, 1 - radial / (radius * radius))) < minimumY) continue;
                float cap = radius > Epsilon ? radius - Mathf.Sqrt(Mathf.Max(0, radius * radius - radial)) : 0;
                float separation = feet.y + cap - point.y;
                if (Mathf.Abs(separation) > reach) continue;
                float distance = Mathf.Abs(separation);
                if (found && (distance > best + Epsilon || Mathf.Abs(distance - best) <= Epsilon && state.Handle.Id >= hit.State.Handle.Id)) continue;
                found = true; best = distance; hit = new BlockFixedGridHit(state, point, Vector3.up, distance);
            }
            return found;
        }

        // Piecewise quadratic distance(point(t), AABB) gives an exact sphere/AABB TOI,
        // including rounded edges/corners. A radius-expanded slab alone would overblock corners.
        public static bool SweepSphereBox(Vector3 origin, Vector3 direction, float distance, float radius, Bounds box,
            out float time, out Vector3 normal)
        {
            time = 0; normal = default;
            Span<double> times = stackalloc double[8]; int count = 2;
            times[0] = 0; times[1] = distance;
            Vector3 min = box.min, max = box.max;
            for (int axis = 0; axis < 3; axis++)
            {
                double velocity = direction[axis]; if (Math.Abs(velocity) < 1e-12) continue;
                double a = (min[axis] - origin[axis]) / velocity, b = (max[axis] - origin[axis]) / velocity;
                if (a > 0 && a < distance) times[count++] = a;
                if (b > 0 && b < distance) times[count++] = b;
            }
            for (int i = 1; i < count; i++)
            { double value = times[i]; int j = i; while (j > 0 && times[j - 1] > value) { times[j] = times[j - 1]; j--; } times[j] = value; }
            double radiusSquared = (double)radius * radius;
            for (int interval = 0; interval < count - 1; interval++)
            {
                double from = times[interval], to = times[interval + 1], middle = (from + to) * .5;
                double a = 0, b = 0, c = -radiusSquared;
                for (int axis = 0; axis < 3; axis++)
                {
                    double p = origin[axis] + direction[axis] * middle;
                    double boundary = p < min[axis] ? min[axis] : p > max[axis] ? max[axis] : double.NaN;
                    if (double.IsNaN(boundary)) continue;
                    double offset = origin[axis] - boundary, velocity = direction[axis];
                    a += velocity * velocity; b += 2 * offset * velocity; c += offset * offset;
                }
                double atStart = (a * from + b) * from + c;
                double candidate = from;
                if (atStart > 1e-12)
                {
                    if (a <= 1e-24) continue;
                    double discriminant = b * b - 4 * a * c;
                    if (discriminant < 0) continue;
                    candidate = (-b - Math.Sqrt(discriminant)) / (2 * a);
                    if (candidate < from - 1e-8 || candidate > to + 1e-8) continue;
                }
                time = Mathf.Clamp((float)candidate, 0, distance);
                Vector3 point = origin + direction * time, delta = point - box.ClosestPoint(point);
                normal = delta.sqrMagnitude > Epsilon * Epsilon ? delta.normalized : InsideNormal(point, box, -direction);
                return true;
            }
            return false;
        }

        static Vector3 InsideNormal(Vector3 point, Bounds bounds, Vector3 fallback)
        {
            float best = float.PositiveInfinity; Vector3 result = fallback.sqrMagnitude > Epsilon ? fallback.normalized : Vector3.up;
            for (int axis = 0; axis < 3; axis++)
            {
                float low = point[axis] - bounds.min[axis], high = bounds.max[axis] - point[axis];
                if (low < best) { best = low; result = Vector3.zero; result[axis] = -1; }
                if (high < best) { best = high; result = Vector3.zero; result[axis] = 1; }
            }
            return result;
        }

        static bool BoxesOverlap(Bounds box, Vector3 center, Vector3 half, Vector3 x, Vector3 y, Vector3 z)
        {
            Vector3 delta = center - box.center, extent = box.extents;
            if (Separates(Vector3.right) || Separates(Vector3.up) || Separates(Vector3.forward) || Separates(x) || Separates(y) || Separates(z)) return false;
            if (Separates(Vector3.Cross(Vector3.right, x)) || Separates(Vector3.Cross(Vector3.right, y)) || Separates(Vector3.Cross(Vector3.right, z)) ||
                Separates(Vector3.Cross(Vector3.up, x)) || Separates(Vector3.Cross(Vector3.up, y)) || Separates(Vector3.Cross(Vector3.up, z)) ||
                Separates(Vector3.Cross(Vector3.forward, x)) || Separates(Vector3.Cross(Vector3.forward, y)) || Separates(Vector3.Cross(Vector3.forward, z))) return false;
            return true;
            bool Separates(Vector3 axis)
            {
                if (axis.sqrMagnitude < 1e-12f) return false;
                float a = Vector3.Dot(extent, Abs(axis));
                float b = half.x * Mathf.Abs(Vector3.Dot(x, axis)) + half.y * Mathf.Abs(Vector3.Dot(y, axis)) + half.z * Mathf.Abs(Vector3.Dot(z, axis));
                return Mathf.Abs(Vector3.Dot(delta, axis)) >= a + b - Epsilon;
            }
        }
        static float NextBoundary(float origin, float direction, int cell) => Mathf.Abs(direction) < 1e-12f ? float.PositiveInfinity :
            (((direction > 0 ? cell + 1 : cell) * BlockWorld.CellSize) - origin) / direction;
        static float Interval(float direction) => Mathf.Abs(direction) < 1e-12f ? float.PositiveInfinity : BlockWorld.CellSize / Mathf.Abs(direction);
        static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
