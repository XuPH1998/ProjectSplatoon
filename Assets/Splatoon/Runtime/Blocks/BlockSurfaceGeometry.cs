using System;
using System.Collections.Generic;
using System.Linq;
using Splatoon.Blocks.Simulation;
using Splatoon.Painting;
using Unity.Mathematics;
using UnityEngine;

namespace Splatoon.Blocks
{
    /// <summary>One-time, world-metre face bake. It never creates collision geometry.</summary>
    public static class BlockSurfaceGeometry
    {
        // World metres, only for validating a convex source's duplicate hard-edge
        // vertices. Original triangles remain untouched for surface projection.
        const double ConvexValidationWeldDistance = .000002;
        // World-metre numerical budget for transformed float vertices. Every pair
        // of source triangles must satisfy this bound in BOTH directions; neither
        // close intercepts nor a transitive chain can merge a real five-micron gap.
        const double CoplanarVertexDistance = .000002;
        sealed class Triangle
        {
            public readonly Vector3 A, B, C, ChartNormal;
            public readonly float ChartDistance;
            public readonly double3 Normal;
            public readonly double Distance;
            public Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                A = a; B = b; C = c; ChartNormal = normal; ChartDistance = Vector3.Dot(normal, a);
                var da = new double3(a.x, a.y, a.z);
                Normal = math.normalize(math.cross(new double3(b.x, b.y, b.z) - da, new double3(c.x, c.y, c.z) - da));
                Distance = math.dot(Normal, da);
            }
            public bool ContainsPlanePoint(Vector3 point)
                => Math.Abs(math.dot(Normal, new double3((double)point.x - A.x, (double)point.y - A.y, (double)point.z - A.z))) <= CoplanarVertexDistance;
        }
        sealed class Plane
        {
            public Vector3 Normal;
            public float Distance;
            public readonly List<Vector3> Triangles = new();
            public readonly List<Triangle> Members = new();
            public Plane(Triangle first) { Normal = first.ChartNormal; Distance = first.ChartDistance; Add(first); }
            public void Add(Triangle triangle)
            { Members.Add(triangle); Triangles.Add(triangle.A); Triangles.Add(triangle.B); Triangles.Add(triangle.C); }
        }
        readonly struct ClipPoint
        {
            public readonly double X, Y;
            public ClipPoint(double x, double y) { X = x; Y = y; }
            public static ClipPoint operator +(ClipPoint a, ClipPoint b) => new(a.X + b.X, a.Y + b.Y);
            public static ClipPoint operator -(ClipPoint a, ClipPoint b) => new(a.X - b.X, a.Y - b.Y);
            public static ClipPoint operator *(ClipPoint a, double scalar) => new(a.X * scalar, a.Y * scalar);
        }
        static readonly int[] BoxTriangles =
        { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 };

        public static Dictionary<Collider, int[]> Capture(BlockSurfaceWorld world, IEnumerable<Collider> sources)
        {
            if (world == null || sources == null) throw new ArgumentNullException();
            var result = new Dictionary<Collider, int[]>();
            var charts = new List<Plane>();
            var sourceCharts = new Dictionary<Collider, List<Plane>>();
            var ordered = sources.Where(c => c != null && c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger &&
                c.GetComponentInParent<PaintSurface>() != null).Distinct().OrderBy(SourceKey, StringComparer.Ordinal);
            foreach (var collider in ordered)
            {
                var planes = ReadPlanes(collider);
                planes.Sort(ComparePlanes);
                var mapped = new List<Plane>();
                foreach (var plane in planes)
                {
                    // Map tiles often split one physical square at a collider edge.
                    // Their complete-square test must use the coplanar triangle union,
                    // and either collider must resolve the same ownership at the seam.
                    // Exact component/plane equality is deliberate: Vector3's == uses
                    // a tolerance and must not merge genuinely separate nearby planes.
                    var chart = charts.Find(candidate => candidate.Normal.Equals(plane.Normal) && candidate.Distance.Equals(plane.Distance) && Coplanar(candidate, plane));
                    if (chart == null) { chart = plane; charts.Add(chart); }
                    else foreach (var triangle in plane.Members) chart.Add(triangle);
                    if (!mapped.Contains(chart)) mapped.Add(chart);
                }
                sourceCharts.Add(collider, mapped);
            }
            var ids = new Dictionary<Plane, int>();
            foreach (var plane in charts)
            {
                // SourceKey order and each source's sorted plane order assign stable
                // IDs independently of the incoming collider enumeration order.
                Vector3 u = Mathf.Abs(plane.Normal.y) > .7071067f ? Vector3.right : Vector3.up;
                u = (u - plane.Normal * Vector3.Dot(u, plane.Normal)).normalized;
                Vector3 v = Vector3.Cross(plane.Normal, u).normalized;
                Vector3 origin = plane.Normal * plane.Distance;
                var triangles = new List<Vector2>();
                foreach (var p in plane.Triangles) triangles.Add(new Vector2(Vector3.Dot(p - origin, u), Vector3.Dot(p - origin, v)));
                int id = checked(ids.Count + 1);
                world.RegisterFace(id, origin, u, v, FullCells(triangles)); ids.Add(plane, id);
            }
            foreach (var pair in sourceCharts) result.Add(pair.Key, pair.Value.Select(plane => ids[plane]).ToArray());
            return result;
        }

        static int ComparePlanes(Plane a, Plane b)
        {
            int c = a.Normal.x.CompareTo(b.Normal.x); if (c != 0) return c;
            c = a.Normal.y.CompareTo(b.Normal.y); if (c != 0) return c;
            c = a.Normal.z.CompareTo(b.Normal.z); if (c != 0) return c;
            c = a.Distance.CompareTo(b.Distance); return c != 0 ? c : CompareTriangles(a.Members[0], b.Members[0]);
        }

        static bool Coplanar(Triangle a, Triangle b)
            => math.dot(a.Normal, b.Normal) > .999999 &&
                a.ContainsPlanePoint(b.A) && a.ContainsPlanePoint(b.B) && a.ContainsPlanePoint(b.C) &&
                b.ContainsPlanePoint(a.A) && b.ContainsPlanePoint(a.B) && b.ContainsPlanePoint(a.C);

        static bool Coplanar(Plane plane, Triangle triangle)
        {
            foreach (var member in plane.Members) if (!Coplanar(member, triangle)) return false;
            return true;
        }

        static bool Coplanar(Plane a, Plane b)
        {
            foreach (var triangle in b.Members) if (!Coplanar(a, triangle)) return false;
            return true;
        }

        static int ComparePoints(Vector3 a, Vector3 b)
        {
            int c = a.x.CompareTo(b.x); if (c != 0) return c;
            c = a.y.CompareTo(b.y); return c != 0 ? c : a.z.CompareTo(b.z);
        }

        static int CompareTriangles(Triangle a, Triangle b)
        {
            int c = a.Normal.x.CompareTo(b.Normal.x); if (c != 0) return c;
            c = a.Normal.y.CompareTo(b.Normal.y); if (c != 0) return c;
            c = a.Normal.z.CompareTo(b.Normal.z); if (c != 0) return c;
            c = a.Distance.CompareTo(b.Distance); if (c != 0) return c;
            c = ComparePoints(a.A, b.A); if (c != 0) return c;
            c = ComparePoints(a.B, b.B); return c != 0 ? c : ComparePoints(a.C, b.C);
        }

        static string SourceKey(Collider source)
        {
            var surface = source.GetComponentInParent<PaintSurface>();
            string path = "";
            for (var t = source.transform; t != null; t = t.parent) path = t.GetSiblingIndex().ToString("D6") + ":" + t.name + "/" + path;
            return surface.SurfaceId.ToString("D10") + "/" + path + "/" + source.GetType().Name + "/" + Array.IndexOf(source.GetComponents<Collider>(), source);
        }

        static List<Plane> ReadPlanes(Collider collider)
        {
            Vector3[] points; int[] indices;
            bool convexMesh = collider is MeshCollider declaredMesh && declaredMesh.convex;
            if (collider is BoxCollider box)
            {
                var h = box.size * .5f; points = new Vector3[8];
                for (int side = 0; side < 2; side++)
                {
                    int o = side * 4; float z = side == 0 ? -h.z : h.z;
                    points[o] = box.center + new Vector3(-h.x, -h.y, z);
                    points[o + 1] = box.center + new Vector3(h.x, -h.y, z);
                    points[o + 2] = box.center + new Vector3(h.x, h.y, z);
                    points[o + 3] = box.center + new Vector3(-h.x, h.y, z);
                }
                indices = BoxTriangles;
            }
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
            {
                if (!mesh.sharedMesh.isReadable) throw new InvalidOperationException("Surface paint needs a readable collision bake: " + SourceKey(collider));
                points = mesh.sharedMesh.vertices; indices = mesh.sharedMesh.triangles;
            }
            else throw new NotSupportedException("Surface paint requires an explicit box or triangle mesh bake: " + SourceKey(collider));
            for (int i = 0; i < points.Length; i++) points[i] = collider.transform.TransformPoint(points[i]);
            Vector3 convexCenter = default;
            if (convexMesh)
            {
                // sharedMesh is the cooking input, not PhysX's cooked convex hull.
                // It is usable only when it already describes that closed convex
                // solid. Reuse the map bake's position-welded manifold, half-space
                // and nonzero-volume checks, including transformed source geometry.
                for (int i = 0; i < points.Length; i++) convexCenter += points[i];
                try { new BlockMapCollisionBuilder().AddConvex(WeldConvexValidationCopy(points), indices); }
                catch (ArgumentException error)
                {
                    throw new NotSupportedException("Surface paint cannot substitute a convex collider's source mesh for its cooked hull; bake a closed convex source explicitly: " + SourceKey(collider), error);
                }
                convexCenter /= points.Length;
            }
            var triangles = new List<Triangle>();
            bool reverseBoxWinding = collider is BoxCollider && collider.transform.localToWorldMatrix.determinant < 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = points[indices[i]], b = points[indices[i + 1]], c = points[indices[i + 2]];
                if (reverseBoxWinding) (b, c) = (c, b);
                // Rotate, preserving winding and every original coordinate, before
                // choosing a deterministic representative for the plane catalog.
                if (ComparePoints(b, a) < 0 && ComparePoints(b, c) <= 0) (a, b, c) = (b, c, a);
                else if (ComparePoints(c, a) < 0 && ComparePoints(c, b) < 0) (a, b, c) = (c, a, b);
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-16f) continue;
                // Convex cooking supplies outward normals independently of source
                // winding. Resolve them after the transform, including reflections.
                if (convexMesh && Vector3.Dot(n, convexCenter - a) > 0) { (b, c) = (c, b); n = -n; }
                n.Normalize(); triangles.Add(new Triangle(a, b, c, n));
            }
            // The same source geometry gets the same representative and chart
            // even when a mesh enumerates its triangles in the reverse order.
            triangles.Sort(CompareTriangles);
            var planes = new List<Plane>();
            foreach (var triangle in triangles)
            {
                var plane = planes.Find(candidate => Coplanar(candidate, triangle));
                if (plane == null) planes.Add(new Plane(triangle)); else plane.Add(triangle);
            }
            return planes;
        }

        static float3[] WeldConvexValidationCopy(Vector3[] points)
        {
            var copy = new float3[points.Length]; var order = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                if (!float.IsFinite(points[i].x) || !float.IsFinite(points[i].y) || !float.IsFinite(points[i].z))
                    throw new ArgumentException("Convex source vertices must be finite.");
                order[i] = i;
            }
            // Fixed representatives and a diameter bound avoid both input-order
            // dependence and transitive chains welding a larger real crack.
            Array.Sort(order, (a, b) =>
            {
                int c = points[a].x.CompareTo(points[b].x); if (c != 0) return c;
                c = points[a].y.CompareTo(points[b].y); if (c != 0) return c;
                c = points[a].z.CompareTo(points[b].z); return c != 0 ? c : a.CompareTo(b);
            });
            var groups = new List<List<int>>();
            double squaredLimit = ConvexValidationWeldDistance * ConvexValidationWeldDistance;
            foreach (int index in order)
            {
                List<int> selected = null;
                foreach (var group in groups)
                {
                    bool withinLimit = true;
                    foreach (int member in group)
                    {
                        double x = (double)points[index].x - points[member].x;
                        double y = (double)points[index].y - points[member].y;
                        double z = (double)points[index].z - points[member].z;
                        if (x * x + y * y + z * z > squaredLimit) { withinLimit = false; break; }
                    }
                    if (withinLimit) { selected = group; break; }
                }
                if (selected == null) { selected = new List<int>(); groups.Add(selected); }
                selected.Add(index);
                Vector3 representative = points[selected[0]];
                copy[index] = new float3(representative.x, representative.y, representative.z);
            }
            return copy;
        }

        /// <summary>Subtracts the triangle union from each square. Overlap and holes cannot inflate coverage.</summary>
        public static List<Vector2Int> FullCells(IReadOnlyList<Vector2> triangles)
        {
            var cells = new List<Vector2Int>();
            if (triangles == null || triangles.Count % 3 != 0) throw new ArgumentException("Triangle list required.");
            if (triangles.Count == 0) return cells;
            Vector2 min = triangles[0], max = min;
            foreach (var p in triangles) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            double size = BlockWorld.CellSize;
            int minU = (int)Math.Ceiling((min.x - .00001) / size), maxU = (int)Math.Floor((max.x + .00001) / size) - 1;
            int minV = (int)Math.Ceiling((min.y - .00001) / size), maxV = (int)Math.Floor((max.y + .00001) / size) - 1;
            for (int v = minV; v <= maxV; v++) for (int u = minU; u <= maxU; u++)
            {
                // Keep source projection unchanged. Double intermediates in this
                // square's local coordinates avoid float clipping residuals at
                // map-sized coordinates falsely cutting holes in complete cells.
                double lowX = u * size, lowY = v * size;
                var remaining = new List<List<ClipPoint>> { new() { new(0, 0), new(size, 0), new(size, size), new(0, size) } };
                for (int t = 0; t < triangles.Count && remaining.Count != 0; t += 3)
                {
                    var a = new ClipPoint(triangles[t].x - lowX, triangles[t].y - lowY);
                    var b = new ClipPoint(triangles[t + 1].x - lowX, triangles[t + 1].y - lowY);
                    var c = new ClipPoint(triangles[t + 2].x - lowX, triangles[t + 2].y - lowY);
                    if (Math.Max(a.X, Math.Max(b.X, c.X)) < 0 || Math.Min(a.X, Math.Min(b.X, c.X)) > size ||
                        Math.Max(a.Y, Math.Max(b.Y, c.Y)) < 0 || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > size) continue;
                    if (Cross(b - a, c - a) < 0) (b, c) = (c, b);
                    var next = new List<List<ClipPoint>>();
                    foreach (var polygon in remaining)
                    {
                        var inside = polygon;
                        var corners = new[] { a, b, c };
                        for (int e = 0; e < 3 && inside.Count >= 3; e++)
                        {
                            var outside = Clip(inside, corners[e], corners[(e + 1) % 3], false);
                            if (Area(outside) > 1e-12) next.Add(outside);
                            inside = Clip(inside, corners[e], corners[(e + 1) % 3], true);
                        }
                    }
                    remaining = next;
                }
                double uncovered = 0; foreach (var polygon in remaining) uncovered += Area(polygon);
                if (uncovered <= 1e-8) cells.Add(new Vector2Int(u, v));
            }
            return cells;
        }

        static double Cross(ClipPoint a, ClipPoint b) => a.X * b.Y - a.Y * b.X;
        static List<ClipPoint> Clip(List<ClipPoint> polygon, ClipPoint a, ClipPoint b, bool inside)
        {
            var result = new List<ClipPoint>(); if (polygon.Count == 0) return result;
            var previous = polygon[polygon.Count - 1]; double dp = Cross(b - a, previous - a) * (inside ? 1 : -1);
            foreach (var current in polygon)
            {
                double dc = Cross(b - a, current - a) * (inside ? 1 : -1);
                if ((dp >= 0) != (dc >= 0)) result.Add(previous + (current - previous) * (dp / (dp - dc)));
                if (dc >= 0) result.Add(current);
                previous = current; dp = dc;
            }
            return result;
        }
        static double Area(List<ClipPoint> polygon)
        {
            double sum = 0; for (int i = 0; i < polygon.Count; i++)
            { var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; sum += a.X * b.Y - a.Y * b.X; }
            return Math.Abs(sum) * .5;
        }
    }
}
