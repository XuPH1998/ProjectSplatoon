using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Painting
{
    // Geometry metadata only. No coverage readback and no change to the authored UVs.
    internal sealed class InkBoundaryTopology : IDisposable
    {
        internal sealed class Chart
        {
            public PaintSurface Surface;
            public Mesh Mesh;
            public Vector3 Origin, U, V, Normal, DualU, DualV;
            public Rect UV;
            public readonly List<(Vector3 a, Vector3 b)> Edges = new();
            public readonly List<(Chart chart, Matrix4x4 fold)> Neighbours = new();
            public Vector2 Project(Vector3 p) => new(Vector3.Dot(p - Origin, DualU), Vector3.Dot(p - Origin, DualV));
            public Vector3 World(Vector2 uv) => Origin + U * uv.x + V * uv.y;
        }
        public readonly List<Chart> Charts = new();
        public InkBoundaryTopology(PaintSurface surface)
        {
            var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
            var p = mesh.vertices; var uv = mesh.uv2; var indices = mesh.triangles;
            var matrix = surface.transform.localToWorldMatrix;
            var groups = new List<List<int>>();
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                Vector3 pa = matrix.MultiplyPoint3x4(p[a]), pb = matrix.MultiplyPoint3x4(p[b]), pc = matrix.MultiplyPoint3x4(p[c]);
                Vector2 ab = uv[b] - uv[a], ac = uv[c] - uv[a];
                float det = ab.x * ac.y - ab.y * ac.x;
                if (Mathf.Abs(det) < 1e-10f) continue;
                Vector3 u = ((pb - pa) * ac.y - (pc - pa) * ab.y) / det;
                Vector3 v = ((pc - pa) * ab.x - (pb - pa) * ac.x) / det;
                Vector3 origin = pa - u * uv[a].x - v * uv[a].y;
                int group = Charts.FindIndex(ch => (ch.Origin - origin).sqrMagnitude < 1e-6f &&
                    (ch.U - u).sqrMagnitude < 1e-5f && (ch.V - v).sqrMagnitude < 1e-5f);
                if (group < 0)
                {
                    var normal = Vector3.Cross(pb - pa, pc - pa).normalized;
                    var cross = Vector3.Cross(u, v); float area = cross.sqrMagnitude;
                    Charts.Add(new Chart { Surface = surface, Origin = origin, U = u, V = v, Normal = normal,
                        DualU = Vector3.Cross(v, cross) / area, DualV = Vector3.Cross(cross, u) / area });
                    groups.Add(new List<int>()); group = Charts.Count - 1;
                }
                groups[group].Add(a); groups[group].Add(b); groups[group].Add(c);
            }
            for (int g = 0; g < Charts.Count; g++)
            {
                var chart = Charts[g]; var points = new List<Vector3>(); var tex = new List<Vector2>(); var tris = new List<int>();
                Vector2 lo = Vector2.one * float.MaxValue, hi = Vector2.one * float.MinValue;
                foreach (int index in groups[g])
                {
                    points.Add(matrix.MultiplyPoint3x4(p[index])); tex.Add(uv[index]); tris.Add(tris.Count);
                    lo = Vector2.Min(lo, uv[index]); hi = Vector2.Max(hi, uv[index]);
                }
                chart.UV = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);
                chart.Mesh = new Mesh { name = "Ink boundary chart", hideFlags = HideFlags.HideAndDontSave };
                chart.Mesh.SetVertices(points); chart.Mesh.SetUVs(0, tex); chart.Mesh.SetTriangles(tris, 0);
                // Internal triangulation edges cancel. Subdivided boundary segments remain usable.
                for (int t = 0; t < points.Count; t += 3) for (int e = 0; e < 3; e++)
                {
                    var a = points[t + e]; var b = points[t + (e + 1) % 3];
                    int opposite = chart.Edges.FindIndex(edge => (edge.a - b).sqrMagnitude < 1e-8f && (edge.b - a).sqrMagnitude < 1e-8f);
                    if (opposite >= 0) chart.Edges.RemoveAt(opposite); else chart.Edges.Add((a, b));
                }
            }
        }
        public static void Connect(IReadOnlyList<InkBoundaryTopology> topologies)
        {
            var all = new List<Chart>(); foreach (var t in topologies) all.AddRange(t.Charts);
            foreach (var a in all)
            {
                a.Neighbours.Clear();
                foreach (var b in all)
                {
                    if (a == b || Vector3.Dot(a.Normal, b.Normal) < .707f) continue;
                    if (!SharedEdge(a, b, out Vector3 anchor)) continue;
                    var rotation = Quaternion.FromToRotation(b.Normal, a.Normal);
                    var fold = Matrix4x4.TRS(anchor, rotation, Vector3.one) * Matrix4x4.Translate(-anchor);
                    a.Neighbours.Add((b, fold));
                }
            }
        }
        static bool SharedEdge(Chart a, Chart b, out Vector3 anchor)
        {
            foreach (var x in a.Edges) foreach (var y in b.Edges)
            {
                var direction = x.b - x.a; float length = direction.magnitude; if (length < 1e-5f) continue;
                direction /= length;
                if (Vector3.Cross(y.a - x.a, direction).sqrMagnitude > 1e-7f || Vector3.Cross(y.b - x.a, direction).sqrMagnitude > 1e-7f) continue;
                float lo = Mathf.Min(Vector3.Dot(y.a - x.a, direction), Vector3.Dot(y.b - x.a, direction));
                float hi = Mathf.Max(Vector3.Dot(y.a - x.a, direction), Vector3.Dot(y.b - x.a, direction));
                if (Mathf.Min(length, hi) - Mathf.Max(0, lo) > .001f) { anchor = x.a; return true; }
            }
            anchor = default; return false;
        }
        public void Dispose()
        {
            foreach (var c in Charts) { if (Application.isPlaying) UnityEngine.Object.Destroy(c.Mesh); else UnityEngine.Object.DestroyImmediate(c.Mesh); }
            Charts.Clear();
        }
    }
}
