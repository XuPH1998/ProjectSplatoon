using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    /// <summary>Allocation-free translational SAT over a baked BVH. Normals point from map to box.</summary>
    [BurstCompile]
    public static class BlockMapQueries
    {
        const float Epsilon = .000001f;

        /// <summary>Earliest blocking TOI. Initial overlaps moving outward/tangentially are skipped,
        /// so correction can leave an overlap without ignoring a second obstacle further along its path.
        /// Use Overlap/CollectContacts to obtain the initial penetration itself.</summary>
        public static bool Sweep(in BlockMapSnapshot map, float3 center, float3 extents,
            float3 displacement, out BlockStaticHit hit)
        {
            hit = default;
            if (!Valid(map, center, extents) || !math.all(math.isfinite(displacement)) || math.lengthsq(displacement) <= 1e-20f) return false;
            float3 end = center + displacement;
            if (!math.all(math.isfinite(end))) return false;
            float3 min = math.min(center, end) - extents - Epsilon, max = math.max(center, end) + extents + Epsilon;
            var stack = new FixedList512Bytes<int>(); stack.Add(0);
            bool found = false; float best = 1;
            while (stack.Length != 0)
            {
                int index = stack[stack.Length - 1]; stack.RemoveAt(stack.Length - 1);
                var node = map.Nodes[index]; if (!Intersects(min, max, node.Minimum, node.Maximum)) continue;
                if (node.Count == 0) { stack.Add(node.Right); stack.Add(node.Left); continue; }
                for (int i = 0; i < node.Count; i++)
                {
                    var feature = map.Features[map.FeatureIndices[node.First + i]];
                    if (!SweepFeature(map, feature, center, extents, displacement, out var candidate) || candidate.Fraction > best + Epsilon) continue;
                    if (found && math.abs(candidate.Fraction - best) <= Epsilon && candidate.FeatureId >= hit.FeatureId) continue;
                    best = candidate.Fraction; hit = candidate; found = true;
                }
            }
            return found;
        }

        /// <summary>Strict overlap, excluding mere touching. Returns the smallest penetration contact.</summary>
        public static bool Overlap(in BlockMapSnapshot map, float3 center, float3 extents, out BlockStaticHit hit)
        {
            hit = default;
            if (!Valid(map, center, extents)) return false;
            var stack = new FixedList512Bytes<int>(); stack.Add(0);
            bool found = false;
            float3 min = center - extents, max = center + extents;
            while (stack.Length != 0)
            {
                int index = stack[stack.Length - 1]; stack.RemoveAt(stack.Length - 1);
                var node = map.Nodes[index]; if (!Intersects(min, max, node.Minimum, node.Maximum)) continue;
                if (node.Count == 0) { stack.Add(node.Right); stack.Add(node.Left); continue; }
                for (int i = 0; i < node.Count; i++)
                {
                    var feature = map.Features[map.FeatureIndices[node.First + i]];
                    if (!ContactFeature(map, feature, center, extents, 0, out var candidate) || candidate.Penetration <= Epsilon) continue;
                    if (!found || candidate.Penetration < hit.Penetration - Epsilon ||
                        math.abs(candidate.Penetration - hit.Penetration) <= Epsilon && candidate.FeatureId < hit.FeatureId)
                    { hit = candidate; found = true; }
                }
            }
            return found;
        }

        /// <summary>Appends contacts without clearing or truncation. Depth excludes skin, so near contacts
        /// can have negative Penetration. Coincident coplanar contacts are merged within this call.</summary>
        public static void CollectContacts(in BlockMapSnapshot map, float3 center, float3 extents,
            NativeList<BlockStaticHit> contacts, float skin = 0)
        {
            if (!Valid(map, center, extents) || !math.isfinite(skin) || skin < 0) return;
            int first = contacts.Length;
            float3 min = center - extents - skin - Epsilon, max = center + extents + skin + Epsilon;
            var stack = new FixedList512Bytes<int>(); stack.Add(0);
            while (stack.Length != 0)
            {
                int index = stack[stack.Length - 1]; stack.RemoveAt(stack.Length - 1);
                var node = map.Nodes[index]; if (!Intersects(min, max, node.Minimum, node.Maximum)) continue;
                if (node.Count == 0) { stack.Add(node.Right); stack.Add(node.Left); continue; }
                for (int i = 0; i < node.Count; i++)
                {
                    var feature = map.Features[map.FeatureIndices[node.First + i]];
                    if (!ContactFeature(map, feature, center, extents, skin, out var candidate)) continue;
                    bool duplicate = false;
                    for (int j = first; j < contacts.Length; j++)
                    {
                        var previous = contacts[j];
                        if (math.dot(previous.Normal, candidate.Normal) < .999999f ||
                            math.abs(previous.Penetration - candidate.Penetration) > Epsilon ||
                            math.abs(math.dot(previous.Point - candidate.Point, candidate.Normal)) > Epsilon ||
                            previous.StaticFriction != candidate.StaticFriction || previous.DynamicFriction != candidate.DynamicFriction) continue;
                        if (candidate.FeatureId < previous.FeatureId) contacts[j] = candidate;
                        duplicate = true; break;
                    }
                    if (!duplicate) contacts.Add(candidate);
                }
            }
        }

        public static bool OverlapOutsideSupportPlane(in BlockMapSnapshot map, float3 center, float3 extents,
            float3 supportPoint, float3 supportNormal, out int blockedFeatureId)
        {
            blockedFeatureId = 0;
            float normalLength = math.lengthsq(supportNormal);
            if (!Valid(map, center, extents) || !math.all(math.isfinite(supportPoint)) ||
                !math.all(math.isfinite(supportNormal)) || math.abs(normalLength - 1) > .00001f)
                throw new System.ArgumentException("A complete admission mesh bake and finite unit support plane are required.");

            const float planeDistance = .000002f;
            float3 min = center - extents - Epsilon, max = center + extents + Epsilon;
            var stack = new FixedList512Bytes<int>(); stack.Add(0);
            while (stack.Length != 0)
            {
                int index = stack[stack.Length - 1]; stack.RemoveAt(stack.Length - 1);
                var node = map.Nodes[index];
                if (!Intersects(min, max, node.Minimum, node.Maximum)) continue;
                if (node.Count == 0) { stack.Add(node.Right); stack.Add(node.Left); continue; }
                for (int i = 0; i < node.Count; i++)
                {
                    var feature = map.Features[map.FeatureIndices[node.First + i]];
                    if (feature.Kind != BlockMapFeatureKind.Triangle)
                        throw new System.InvalidOperationException("Concave admission proof unexpectedly contains a non-triangle feature.");
                    float3 distances = new(math.dot(feature.A - supportPoint, supportNormal),
                        math.dot(feature.B - supportPoint, supportNormal), math.dot(feature.C - supportPoint, supportNormal));
                    // The whole finite face is within the allowed map embedding, including
                    // platform undersides, side faces ending at the top, and coplanar seams.
                    if (math.cmax(distances) <= planeDistance ||
                        !SupportTriangleOverlap(feature.A, feature.B, feature.C, center, extents)) continue;
                    bool blocked = math.cmin(distances) > planeDistance;
                    if (!blocked)
                    {
                        var input = new FixedList128Bytes<float3>();
                        input.Add(feature.A); input.Add(feature.B); input.Add(feature.C);
                        var clipped = new FixedList128Bytes<float3>();
                        float3 previous = feature.C; float previousDistance = distances.z;
                        bool previousInside = previousDistance > planeDistance;
                        for (int vertex = 0; vertex < 3; vertex++)
                        {
                            float3 current = input[vertex]; float currentDistance = distances[vertex];
                            bool currentInside = currentDistance > planeDistance;
                            if (previousInside != currentInside)
                            {
                                float t = (planeDistance - previousDistance) / (currentDistance - previousDistance);
                                clipped.Add(math.lerp(previous, current, math.clamp(t, 0, 1)));
                            }
                            if (currentInside) clipped.Add(current);
                            previous = current; previousDistance = currentDistance; previousInside = currentInside;
                        }
                        // Clipping a triangle against one half-space yields at most four vertices.
                        // Its new edges need their own axes: cached original axes cannot prove this.
                        for (int vertex = 1; vertex + 1 < clipped.Length; vertex++)
                            if (SupportTriangleOverlap(clipped[0], clipped[vertex], clipped[vertex + 1], center, extents))
                            { blocked = true; break; }
                    }
                    if (!blocked) continue;
                    blockedFeatureId = feature.FeatureId; return true;
                }
            }
            return false;
        }

        static bool SupportTriangleOverlap(float3 a, float3 b, float3 c, float3 center, float3 extents)
        {
            float3 ab = b - a, bc = c - b, ca = a - c, normal = math.cross(ab, c - a);
            if (math.lengthsq(normal) <= 1e-20f) return false;
            float3 x = new(1, 0, 0), y = new(0, 1, 0), z = new(0, 0, 1);
            if (!SupportTriangleAxis(x, a, b, c, center, extents) ||
                !SupportTriangleAxis(y, a, b, c, center, extents) ||
                !SupportTriangleAxis(z, a, b, c, center, extents) ||
                !SupportTriangleAxis(normal, a, b, c, center, extents)) return false;
            return SupportTriangleAxis(math.cross(ab, x), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(ab, y), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(ab, z), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(bc, x), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(bc, y), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(bc, z), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(ca, x), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(ca, y), a, b, c, center, extents) &&
                SupportTriangleAxis(math.cross(ca, z), a, b, c, center, extents);
        }

        static bool SupportTriangleAxis(float3 axis, float3 a, float3 b, float3 c, float3 center, float3 extents)
        {
            float length = math.lengthsq(axis); if (length <= 1e-20f) return true;
            axis *= math.rsqrt(length);
            float3 projection = new(math.dot(a, axis), math.dot(b, axis), math.dot(c, axis));
            float origin = math.dot(center, axis), radius = math.dot(extents, math.abs(axis));
            // Normalize every new axis so the existing strict one-micron contact epsilon
            // remains a distance, independent of the clipped triangle's edge lengths.
            return origin + radius - math.cmin(projection) > Epsilon &&
                math.cmax(projection) - origin + radius > Epsilon;
        }

        static bool Valid(in BlockMapSnapshot map, float3 center, float3 extents)
            => map.IsCreated && map.FeatureCount > 0 && map.Nodes.Length > 0 &&
               math.all(math.isfinite(center)) && math.all(math.isfinite(extents)) && math.all(extents >= 0);

        static bool Intersects(float3 minA, float3 maxA, float3 minB, float3 maxB)
            => math.all(maxA >= minB) && math.all(maxB >= minA);

        static bool ContactFeature(in BlockMapSnapshot map, BlockMapFeature feature, float3 center,
            float3 extents, float skin, out BlockStaticHit hit)
        {
            hit = default;
            float depth = float.PositiveInfinity; float3 normal = 0; bool internalEdge = false;
            for (int i = 0; i < feature.AxisCount; i++)
            {
                var axis = map.Axes[feature.AxisStart + i];
                float projection = math.dot(center, axis.Direction), radius = math.dot(extents, math.abs(axis.Direction));
                float negativeExit = projection + radius - axis.Minimum, positiveExit = axis.Maximum - projection + radius;
                if (negativeExit < -skin - Epsilon || positiveExit < -skin - Epsilon) return false;
                bool negative = negativeExit < positiveExit;
                float candidate = negative ? negativeExit : positiveExit;
                if (candidate >= depth) continue;
                depth = candidate; normal = negative ? -axis.Direction : axis.Direction; internalEdge = axis.InternalEdge != 0;
            }
            if (!math.isfinite(depth)) return false;
            if (internalEdge && feature.Kind == BlockMapFeatureKind.Triangle)
                PlaneContact(feature, center, extents, ref normal, ref depth);
            hit = MakeHit(map, feature, center, extents, normal, 0, depth);
            return true;
        }

        static bool SweepFeature(in BlockMapSnapshot map, BlockMapFeature feature, float3 center,
            float3 extents, float3 displacement, out BlockStaticHit hit)
        {
            hit = default;
            if (ContactFeature(map, feature, center, extents, 0, out var initial))
            {
                if (math.dot(displacement, initial.Normal) >= -Epsilon) return false;
                hit = initial; return true;
            }
            float enter = float.NegativeInfinity, exit = float.PositiveInfinity;
            float3 normal = 0; bool internalEdge = false;
            for (int i = 0; i < feature.AxisCount; i++)
            {
                var axis = map.Axes[feature.AxisStart + i];
                float projection = math.dot(center, axis.Direction), radius = math.dot(extents, math.abs(axis.Direction));
                float speed = math.dot(displacement, axis.Direction);
                if (math.abs(speed) < 1e-12f)
                {
                    if (projection + radius < axis.Minimum - Epsilon || projection - radius > axis.Maximum + Epsilon) return false;
                    continue;
                }
                float t0 = (axis.Minimum - radius - projection) / speed, t1 = (axis.Maximum + radius - projection) / speed;
                float axisEnter = math.min(t0, t1), axisExit = math.max(t0, t1);
                if (axisEnter > enter)
                { enter = axisEnter; normal = speed > 0 ? -axis.Direction : axis.Direction; internalEdge = axis.InternalEdge != 0; }
                exit = math.min(exit, axisExit);
                if (enter > exit + Epsilon) return false;
            }
            if (enter < -Epsilon || enter > 1 + Epsilon || exit < 0 || !math.isfinite(enter)) return false;
            float fraction = math.clamp(enter, 0, 1); float3 at = center + displacement * fraction;
            float depth = 0;
            if (internalEdge && feature.Kind == BlockMapFeatureKind.Triangle)
            {
                PlaneContact(feature, at, extents, ref normal, ref depth);
                if (math.dot(displacement, normal) >= -Epsilon) return false;
            }
            hit = MakeHit(map, feature, at, extents, normal, fraction, 0);
            return true;
        }

        static void PlaneContact(BlockMapFeature feature, float3 center, float3 extents, ref float3 normal, ref float depth)
        {
            float signed = math.dot(center - feature.A, feature.PlaneNormal);
            normal = signed >= 0 ? feature.PlaneNormal : -feature.PlaneNormal;
            depth = math.dot(extents, math.abs(normal)) - math.abs(signed);
        }

        static BlockStaticHit MakeHit(in BlockMapSnapshot map, BlockMapFeature feature, float3 center,
            float3 extents, float3 normal, float fraction, float depth)
        {
            // Evaluate the support after its signed normal correction. Without this,
            // an initial overlap can report a point inside the solid (or on its opposite face).
            float3 support = center + normal * depth - math.sign(normal) * extents, point = support;
            if (feature.Kind == BlockMapFeatureKind.Box)
                point = math.clamp(support, feature.Center - feature.Extents, feature.Center + feature.Extents);
            else if (feature.Kind == BlockMapFeatureKind.Triangle)
                point = ClosestTriangle(support, feature.A, feature.B, feature.C);
            else
            {
                float best = float.PositiveInfinity;
                for (int t = 0; t < feature.TriangleCount; t++)
                {
                    int start = feature.TriangleStart + t * 3;
                    float3 a = map.Vertices[feature.VertexStart + map.Triangles[start]],
                        b = map.Vertices[feature.VertexStart + map.Triangles[start + 1]],
                        c = map.Vertices[feature.VertexStart + map.Triangles[start + 2]];
                    float3 candidate = ClosestTriangle(support, a, b, c); float distance = math.lengthsq(candidate - support);
                    if (distance < best) { best = distance; point = candidate; }
                }
            }
            return new BlockStaticHit { FeatureId = feature.FeatureId, Normal = normal, Point = point,
                Fraction = fraction, Penetration = depth, StaticFriction = feature.StaticFriction, DynamicFriction = feature.DynamicFriction };
        }

        static float3 ClosestTriangle(float3 p, float3 a, float3 b, float3 c)
        {
            float3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = math.dot(ab, ap), d2 = math.dot(ac, ap); if (d1 <= 0 && d2 <= 0) return a;
            float3 bp = p - b; float d3 = math.dot(ab, bp), d4 = math.dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            float3 cp = p - c; float d5 = math.dot(ab, cp), d6 = math.dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float inverse = 1 / (va + vb + vc); return a + ab * (vb * inverse) + ac * (vc * inverse);
        }
    }
}
