using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public struct BlockSimCapsule
    {
        // Endpoints are the centres of the two spherical caps, in world metres.
        public float3 a, b;
        public float radius;
        public BlockAabb Bounds => BlockAabb.FromMinMax(math.min(a, b) - radius, math.max(a, b) + radius);
    }

    public struct BlockCapsuleHit
    {
        public float fraction, separation;
        public float3 point, normal;
    }

    public enum BlockCapsuleSweepResult : byte { Miss, Hit, Unresolved }

    /// <summary>Translation-only capsule queries; an expanded box is used only for broad phase.</summary>
    public static class BlockCapsuleQueries
    {
        const float DistanceTolerance = .00001f;

        public static float DistanceSquared(float3 a, float3 b, BlockAabb box, out float3 segmentPoint, out float3 boxPoint)
        {
            float3 delta = b - a, min = box.Min, max = box.Max;
            var cuts = new FixedList64Bytes<float>();
            cuts.Add(0); cuts.Add(1);
            for (int axis = 0; axis < 3; axis++)
            {
                if (math.abs(delta[axis]) < 1e-12f) continue;
                Insert(ref cuts, (min[axis] - a[axis]) / delta[axis]);
                Insert(ref cuts, (max[axis] - a[axis]) / delta[axis]);
            }
            float best = float.PositiveInfinity, bestT = 0;
            for (int i = 0; i < cuts.Length - 1; i++)
            {
                float lo = cuts[i], hi = cuts[i + 1], middle = (lo + hi) * .5f;
                float3 atMiddle = a + delta * middle;
                float numerator = 0, denominator = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (atMiddle[axis] >= min[axis] && atMiddle[axis] <= max[axis]) continue;
                    float edge = atMiddle[axis] < min[axis] ? min[axis] : max[axis];
                    numerator += delta[axis] * (a[axis] - edge);
                    denominator += delta[axis] * delta[axis];
                }
                float t = denominator > 1e-20f ? math.clamp(-numerator / denominator, lo, hi) : lo;
                float3 p = a + delta * t;
                float squared = math.lengthsq(p - math.clamp(p, min, max));
                if (squared < best) { best = squared; bestT = t; }
            }
            segmentPoint = a + delta * bestT;
            boxPoint = math.clamp(segmentPoint, min, max);
            return best;
        }

        static void Insert(ref FixedList64Bytes<float> cuts, float value)
        {
            if (!(value > 0 && value < 1)) return;
            int index = 0;
            while (index < cuts.Length && cuts[index] < value) index++;
            if (index < cuts.Length && cuts[index] == value) return;
            cuts.Add(value);
            for (int j = cuts.Length - 1; j > index; j--) cuts[j] = cuts[j - 1];
            cuts[index] = value;
        }

        public static bool Contact(BlockSimCapsule capsule, BlockAabb box, float margin, out BlockCapsuleHit hit)
        {
            hit = default;
            if (!Valid(capsule, box) || margin < 0 || !math.isfinite(margin)) return false;
            float squared = DistanceSquared(capsule.a, capsule.b, box, out var axisPoint, out var onBox);
            float distance = math.sqrt(math.max(0, squared));
            float separation = distance - capsule.radius;
            if (separation > margin) return false;
            float3 normal;
            if (distance > 1e-8f) normal = (axisPoint - onBox) / distance;
            else
            {
                // A contained axis has no distance gradient. Use a real separating face,
                // rather than a made-up velocity or a diagonal from the bounds' centres.
                float3 lower = math.min(capsule.a, capsule.b) - capsule.radius;
                float3 upper = math.max(capsule.a, capsule.b) + capsule.radius;
                float depth = float.PositiveInfinity;
                normal = new float3(0, 1, 0);
                for (int axis = 0; axis < 3; axis++)
                {
                    float negative = upper[axis] - box.Min[axis], positive = box.Max[axis] - lower[axis];
                    if (negative < depth) { depth = negative; normal = 0; normal[axis] = -1; }
                    if (positive < depth) { depth = positive; normal = 0; normal[axis] = 1; }
                }
                separation = -depth;
                onBox = math.clamp(axisPoint, box.Min, box.Max);
                for (int axis = 0; axis < 3; axis++)
                    if (normal[axis] != 0) onBox[axis] = normal[axis] > 0 ? box.Max[axis] : box.Min[axis];
            }
            hit = new BlockCapsuleHit { normal = normal, point = onBox, separation = separation };
            return true;
        }

        public static BlockCapsuleSweepResult Sweep(BlockSimCapsule capsule, BlockAabb box, float3 displacement, out BlockCapsuleHit hit)
        {
            hit = default;
            if (!Valid(capsule, box) || !math.all(math.isfinite(displacement))) return BlockCapsuleSweepResult.Unresolved;
            if (!capsule.Bounds.Swept(displacement).Expanded(DistanceTolerance).Overlaps(box)) return BlockCapsuleSweepResult.Miss;
            float time = 0;
            for (int iteration = 0; iteration < 128; iteration++)
            {
                var current = capsule;
                current.a += displacement * time; current.b += displacement * time;
                float squared = DistanceSquared(current.a, current.b, box, out var axisPoint, out var onBox);
                float distance = math.sqrt(math.max(0, squared));
                float gap = distance - capsule.radius;
                if (gap <= DistanceTolerance)
                {
                    Contact(current, box, DistanceTolerance, out hit);
                    hit.fraction = time;
                    // A resting surface must not block a move that leaves it.
                    if (time == 0 && gap >= -DistanceTolerance && math.dot(displacement, hit.normal) >= 0)
                        return BlockCapsuleSweepResult.Miss;
                    return BlockCapsuleSweepResult.Hit;
                }
                float3 normal = (axisPoint - onBox) / math.max(distance, 1e-20f);
                float closing = -math.dot(displacement, normal);
                if (closing <= 0) return BlockCapsuleSweepResult.Miss;
                float next = time + gap / closing;
                if (next > 1) return BlockCapsuleSweepResult.Miss;
                if (!(next > time)) return BlockCapsuleSweepResult.Unresolved;
                time = next;
            }
            // The caller must report this as a solver fault, never as an empty sweep.
            return BlockCapsuleSweepResult.Unresolved;
        }

        static bool Valid(BlockSimCapsule capsule, BlockAabb box) => box.IsFinite &&
            math.all(math.isfinite(capsule.a)) && math.all(math.isfinite(capsule.b)) &&
            math.isfinite(capsule.radius) && capsule.radius >= 0;
    }
}
