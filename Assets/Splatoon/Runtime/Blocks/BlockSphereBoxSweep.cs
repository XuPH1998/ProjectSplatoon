using System;
using UnityEngine;

namespace Splatoon.Blocks
{
    internal static class BlockSphereBoxSweep
    {
        // Distance is sphere-centre travel in metres; Point lies on the real box.
        // This tests a frozen falling-cell pose at the caller's existing _now.
        public static bool TrySweep(Vector3 origin, Vector3 direction, float limit, float radius,
            Bounds box, out float distance, out Vector3 point, out Vector3 normal)
        {
            distance = 0; point = normal = default;
            if (!Finite(origin) || !Finite(direction) || !Finite(box.min) || !Finite(box.max) ||
                float.IsNaN(limit) || limit < 0 || !float.IsFinite(radius) || radius < 0) return false;
            double length = Math.Sqrt((double)direction.x * direction.x +
                (double)direction.y * direction.y + (double)direction.z * direction.z);
            if (!(length > 0)) return false;
            Span<double> o = stackalloc double[3] { origin.x, origin.y, origin.z };
            Span<double> d = stackalloc double[3] { direction.x / length, direction.y / length, direction.z / length };
            Vector3 low = box.min, high = box.max;
            Span<double> lo = stackalloc double[3] { low.x, low.y, low.z };
            Span<double> hi = stackalloc double[3] { high.x, high.y, high.z };
            for (int axis = 0; axis < 3; axis++) if (lo[axis] > hi[axis]) return false;
            double r2 = (double)radius * radius, initialSquared = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double gap = o[axis] - Math.Clamp(o[axis], lo[axis], hi[axis]);
                initialSquared += gap * gap;
            }
            if (initialSquared <= r2)
            { MakeHit(o, d, lo, hi, 0, out point, out normal); return true; }

            // Broad phase only. A ray entering these slabs is not yet a hit.
            double enter = 0, exit = limit;
            for (int axis = 0; axis < 3; axis++)
            {
                double min = lo[axis] - radius, max = hi[axis] + radius;
                if (d[axis] == 0)
                { if (o[axis] < min || o[axis] > max) return false; continue; }
                double first = (min - o[axis]) / d[axis], last = (max - o[axis]) / d[axis];
                if (first > last) (first, last) = (last, first);
                enter = Math.Max(enter, first); exit = Math.Min(exit, last);
                if (enter > exit) return false;
            }
            // One nonzero direction axis always makes exit finite, even for +inf limit.
            Span<double> cuts = stackalloc double[8]; int count = 2;
            cuts[0] = enter; cuts[1] = exit;
            for (int axis = 0; axis < 3; axis++)
            {
                if (d[axis] == 0) continue;
                InsertCut(cuts, ref count, (lo[axis] - o[axis]) / d[axis], enter, exit);
                InsertCut(cuts, ref count, (hi[axis] - o[axis]) / d[axis], enter, exit);
            }
            for (int segment = 0; segment < count - 1; segment++)
            {
                double start = cuts[segment], end = cuts[segment + 1];
                double middle = start + (end - start) * .5;
                double a = 0, b = 0, c = -r2;
                for (int axis = 0; axis < 3; axis++)
                {
                    double sample = o[axis] + d[axis] * middle;
                    if (sample >= lo[axis] && sample <= hi[axis]) continue;
                    double bound = sample < lo[axis] ? lo[axis] : hi[axis];
                    double gap = o[axis] + d[axis] * start - bound;
                    a += d[axis] * d[axis]; b += gap * d[axis]; c += gap * gap;
                }
                double offset;
                if (c <= 0) offset = 0;
                else
                {
                    if (!(a > 0) || b >= 0) continue;
                    double discriminant = b * b - a * c;
                    // Roundoff tolerance only; do not expand physical radius.
                    double error = 32 * 2.2204460492503131e-16 * (Math.Abs(b * b) + Math.Abs(a * c));
                    if (discriminant < -error) continue;
                    offset = c / (-b + Math.Sqrt(Math.Max(0, discriminant)));
                    double timeError = 32 * 2.2204460492503131e-16 * Math.Max(1, Math.Abs(end));
                    if (offset > end - start + timeError) continue;
                    offset = Math.Clamp(offset, 0, end - start);
                }
                double travel = start + offset;
                MakeHit(o, d, lo, hi, travel, out point, out normal);
                distance = (float)travel;
                return true;
            }
            return false;
        }

        static void InsertCut(Span<double> cuts, ref int count, double value, double enter, double exit)
        {
            if (!(value > enter && value < exit)) return;
            int index = 0;
            while (index < count && cuts[index] < value) index++;
            if (index < count && cuts[index] == value) return;
            for (int i = count; i > index; i--) cuts[i] = cuts[i - 1];
            cuts[index] = value; count++;
        }

        static void MakeHit(ReadOnlySpan<double> o, ReadOnlySpan<double> d, ReadOnlySpan<double> lo,
            ReadOnlySpan<double> hi, double travel, out Vector3 point, out Vector3 normal)
        {
            Span<double> center = stackalloc double[3];
            Span<double> closest = stackalloc double[3];
            Span<double> delta = stackalloc double[3];
            double squared = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                center[axis] = o[axis] + d[axis] * travel;
                closest[axis] = Math.Clamp(center[axis], lo[axis], hi[axis]);
                delta[axis] = center[axis] - closest[axis]; squared += delta[axis] * delta[axis];
            }
            if (squared > 1e-30)
            {
                double length = Math.Sqrt(squared);
                normal = new Vector3((float)(delta[0] / length), (float)(delta[1] / length), (float)(delta[2] / length));
            }
            else
            {
                // Initial containment and radius-zero contact have no separating
                // vector. Return the nearest real face, ties opposing motion.
                double nearest = double.PositiveInfinity, alignment = double.NegativeInfinity;
                int selectedAxis = 0, selectedSign = -1;
                for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
                {
                    double gap = sign < 0 ? center[axis] - lo[axis] : hi[axis] - center[axis];
                    double facing = -d[axis] * sign;
                    if (gap < nearest || gap == nearest && facing > alignment)
                    { nearest = gap; alignment = facing; selectedAxis = axis; selectedSign = sign; }
                }
                closest[selectedAxis] = selectedSign < 0 ? lo[selectedAxis] : hi[selectedAxis];
                normal = selectedAxis == 0 ? new Vector3(selectedSign, 0, 0) :
                    selectedAxis == 1 ? new Vector3(0, selectedSign, 0) : new Vector3(0, 0, selectedSign);
            }
            point = new Vector3((float)closest[0], (float)closest[1], (float)closest[2]);
        }

        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
