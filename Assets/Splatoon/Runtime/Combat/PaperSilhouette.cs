using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Splatoon.Combat
{
    /// <summary>CPU projection of the same posed triangles used by the capture camera. No GPU readback.</summary>
    public sealed class PaperSilhouette : IDisposable
    {
        public const int Columns = 64, Rows = 128;
        NativeArray<byte> _mask = new(Columns * Rows, Allocator.Persistent);
        NativeArray<Vector3> _bounds = new(2, Allocator.Persistent);
        public Bounds CaptureBounds { get { var b=new Bounds(); b.SetMinMax(_bounds[0],_bounds[1]); return b; } }
        readonly List<Vector3> _vertices = new();
        readonly List<int> _indices = new();
        readonly Dictionary<(int, int), int> _previous = new(), _next = new();
        public readonly List<Rect> Rects = new();
        static readonly int[] Faces = {0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5};
        public void Dispose() { _mask.Dispose(); _bounds.Dispose(); }
        public void Clear()
        {
            for (int i=0;i<_mask.Length;i++) _mask[i]=0;
            _bounds[0]=Vector3.one*float.PositiveInfinity; _bounds[1]=Vector3.one*float.NegativeInfinity;
        }
        public void Add(NativeArray<Vector3> vertices, NativeArray<int> indices, Matrix4x4 toCapture, Vector3 center, float height)
        {
            new Projection { Vertices=vertices, Indices=indices, Mask=_mask, Bounds=_bounds, ToCapture=toCapture, Center=center, Height=height }.Run();
        }
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        struct Projection : IJob
        {
            public NativeArray<Vector3> Vertices;
            [ReadOnly] public NativeArray<int> Indices;
            public NativeArray<byte> Mask;
            public NativeArray<Vector3> Bounds;
            public Matrix4x4 ToCapture;
            public Vector3 Center;
            public float Height;
            public void Execute()
            {
                Vector3 minimum=Bounds[0], maximum=Bounds[1];
                for (int i=0;i<Vertices.Length;i++)
                {
                    Vector3 local=ToCapture.MultiplyPoint3x4(Vertices[i]);
                    minimum=Vector3.Min(minimum,local); maximum=Vector3.Max(maximum,local);
                    Vector3 p=local-Center;
                    Vertices[i]=new Vector3((.5f-p.x/(Height*.5f))*Columns,(.5f+p.y/Height)*Rows,0);
                }
                Bounds[0]=minimum; Bounds[1]=maximum;
                for (int i=0;i<Indices.Length;i+=3)
                {
                    Vector2 a=Vertices[Indices[i]], b=Vertices[Indices[i+1]], c=Vertices[Indices[i+2]];
                    float area=Cross(b-a,c-a); if (Mathf.Abs(area)<.00001f) continue;
                    int x0=Mathf.Max(0,Mathf.CeilToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))-.5f));
                    int x1=Mathf.Min(Columns-1,Mathf.FloorToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x))-.5f));
                    int y0=Mathf.Max(0,Mathf.CeilToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))-.5f));
                    int y1=Mathf.Min(Rows-1,Mathf.FloorToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))-.5f));
                    for (int y=y0;y<=y1;y++) for (int x=x0;x<=x1;x++)
                    {
                        if (Mask[y*Columns+x]!=0) continue;
                        Vector2 p=new(x+.5f,y+.5f);
                        float u=Cross(b-a,p-a)/area, v=Cross(p-a,c-a)/area;
                        if (u>=0 && v>=0 && u+v<=1) Mask[y*Columns+x]=1;
                    }
                }
            }
            static float Cross(Vector2 a,Vector2 b) => a.x*b.y-a.y*b.x;
        }
        public void Build(Mesh mesh, Vector2 size, float thickness)
        {
            Rects.Clear(); _previous.Clear();
            for (int y=0; y<Rows; y++)
            {
                _next.Clear(); int x=0;
                while (x<Columns)
                {
                    if (_mask[y*Columns+x]==0) { x++; continue; }
                    int start=x; while (x<Columns && _mask[y*Columns+x]!=0) x++;
                    var key=(start,x);
                    if (_previous.TryGetValue(key,out int index))
                    { var r=Rects[index]; r.yMax=((y+1f)/Rows-.5f)*size.y; Rects[index]=r; _next[key]=index; }
                    else { _next[key]=Rects.Count; Rects.Add(new Rect((start/(float)Columns-.5f)*size.x,(y/(float)Rows-.5f)*size.y,(x-start)*size.x/Columns,size.y/Rows)); }
                }
                _previous.Clear(); foreach (var entry in _next) _previous.Add(entry.Key,entry.Value);
            }
            _vertices.Clear(); _indices.Clear();
            foreach (var r in Rects)
            {
                int start=_vertices.Count;
                for (int side=-1; side<=1; side+=2)
                {
                    float z=side*thickness/2;
                    _vertices.Add(new Vector3(r.xMin,r.yMin,z)); _vertices.Add(new Vector3(r.xMax,r.yMin,z));
                    _vertices.Add(new Vector3(r.xMax,r.yMax,z)); _vertices.Add(new Vector3(r.xMin,r.yMax,z));
                }
                foreach (int index in Faces) _indices.Add(start+index);
            }
            mesh.Clear(); mesh.SetVertices(_vertices); mesh.SetTriangles(_indices,0); mesh.RecalculateBounds();
        }
        public Vector3 ClosestPoint(Vector3 point, float thickness)
        {
            Vector3 best=point; float nearest=float.PositiveInfinity;
            foreach (var r in Rects)
            {
                var p=new Vector3(Mathf.Clamp(point.x,r.xMin,r.xMax),Mathf.Clamp(point.y,r.yMin,r.yMax),Mathf.Clamp(point.z,-thickness/2,thickness/2));
                float d=(point-p).sqrMagnitude; if (d<nearest) { nearest=d; best=p; }
            }
            return best;
        }
    }
}
