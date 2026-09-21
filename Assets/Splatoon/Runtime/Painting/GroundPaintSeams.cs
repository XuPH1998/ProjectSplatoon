using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Painting
{
    // Static map topology: only physically shared edges of scoring ground faces can fold ink.
    // A bridge over another surface has no shared edge, and walls never enter this graph.
    internal sealed class GroundPaintSeams
    {
        internal readonly struct Link
        {
            public readonly PaintSurface Target;
            readonly Vector3 _start, _end;
            readonly Quaternion _rotation;
            public Link(PaintSurface target, Vector3 start, Vector3 end, Quaternion rotation)
            { Target = target; _start = start; _end = end; _rotation = rotation; }

            public bool TryFold(PaintStamp source, out PaintStamp folded)
            {
                folded = source;
                InkShapeAtlas.StampBasis(source, out var tangent, out var bitangent);
                var shape = InkShapeAtlas.Transform(source.ShapeSeed);
                Vector2 BrushPoint(Vector3 point)
                {
                    var delta = point - source.Position;
                    float x = Vector3.Dot(delta, tangent) / source.Radius;
                    float y = Vector3.Dot(delta, bitangent) / (source.Radius * (source.DepthScale > 0 ? source.DepthScale : 1));
                    return new Vector2((x * shape.x - y * shape.y) * shape.z, x * shape.y + y * shape.x);
                }
                if (source.Radius <= 0) return false;
                // Intersect the shared segment with the oriented brush support, not its bounding sphere.
                Vector2 a = BrushPoint(_start), b = BrushPoint(_end), d = b - a;
                float lo = 0, hi = 1;
                for (int axis = 0; axis < 2; axis++)
                {
                    if (Mathf.Abs(d[axis]) < 1e-7f) { if (Mathf.Abs(a[axis]) > 1) return false; }
                    else
                    {
                        float u = (-1 - a[axis]) / d[axis], v = (1 - a[axis]) / d[axis];
                        lo = Mathf.Max(lo, Mathf.Min(u, v)); hi = Mathf.Min(hi, Mathf.Max(u, v));
                        if (lo > hi) return false;
                    }
                }
                folded.SurfaceId = Target.SurfaceId;
                folded.Position = _start + _rotation * (source.Position - _start);
                folded.Normal = _rotation * source.Normal;
                // Explicitly transport the basis even when the incoming stamp used its default direction.
                folded.Direction = _rotation * bitangent;
                return true;
            }
        }

        readonly Dictionary<PaintSurface, List<Link>> _links = new();
        public List<Link> From(PaintSurface surface) => _links.TryGetValue(surface, out var links) ? links : null;

        public void Build(PaintSurface[] surfaces)
        {
            _links.Clear();
            for (int i = 0; i < surfaces.Length; i++)
            for (int j = i + 1; j < surfaces.Length; j++)
            {
                var a = surfaces[i]; var b = surfaces[j];
                if (!a.Scores || !b.Scores) continue;
                var na = a.transform.up; var nb = b.transform.up;
                float dot = Vector3.Dot(na, nb);
                if (na.y < .7071f || nb.y < .7071f || dot < .7071f || dot >= .9999f) continue;
                var rotation = Quaternion.FromToRotation(na, nb);
                var ac = Corners(a); var bc = Corners(b);
                for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++)
                {
                    if (!SharedSegment(ac[x], ac[(x + 1) % 4], bc[y], bc[(y + 1) % 4], out var start, out var end)) continue;
                    var axis = (end - start).normalized;
                    var inwardA = Vector3.ProjectOnPlane(a.transform.position - start, axis).normalized;
                    var inwardB = Vector3.ProjectOnPlane(b.transform.position - start, axis).normalized;
                    // Unfolded faces must lie on opposite sides of the hinge, never overlap.
                    if (Vector3.Dot(rotation * inwardA, inwardB) > -.999f) continue;
                    Add(a, new Link(b, start, end, rotation));
                    Add(b, new Link(a, start, end, Quaternion.Inverse(rotation)));
                }
            }
        }

        void Add(PaintSurface source, Link link)
        {
            if (!_links.TryGetValue(source, out var list)) _links[source] = list = new List<Link>();
            list.Add(link);
        }

        static Vector3[] Corners(PaintSurface surface)
        {
            var half = surface.WalkableSize * .5f; var t = surface.transform;
            return new[] { t.TransformPoint(new Vector3(-half.x, 0, -half.y)), t.TransformPoint(new Vector3(-half.x, 0, half.y)),
                t.TransformPoint(new Vector3(half.x, 0, half.y)), t.TransformPoint(new Vector3(half.x, 0, -half.y)) };
        }

        static bool SharedSegment(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 start, out Vector3 end)
        {
            start = end = default; var edge = b - a; float length = edge.magnitude;
            if (length < .005f || (d - c).sqrMagnitude < .000025f) return false;
            var axis = edge / length;
            if (Mathf.Abs(Vector3.Dot(axis, (d - c).normalized)) < .9999f ||
                Vector3.Cross(c - a, axis).magnitude > .005f || Vector3.Cross(d - a, axis).magnitude > .005f) return false;
            float u = Vector3.Dot(c - a, axis), v = Vector3.Dot(d - a, axis);
            float lo = Mathf.Max(0, Mathf.Min(u, v)), hi = Mathf.Min(length, Mathf.Max(u, v));
            if (hi - lo <= .005f) return false;
            start = a + axis * lo; end = a + axis * hi; return true;
        }
    }
}
