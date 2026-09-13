#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Splatoon.Editor
{
    // Atlas charts are measured in metres. Long faces are split into charts, not colliders.
    public static class InkSurfaceAtlas
    {
        sealed class Face
        {
            public Vector3 Normal, Tangent, Bitangent;
            public float Plane;
            public readonly List<Vector2[]> Triangles = new();
        }
        sealed class Chart
        {
            public Face Face;
            public Vector2 Min, Max;
            public int Width, Height, X, Y;
            public readonly List<List<Vector2>> Polygons = new();
        }
        public static void Rebuild(Mesh mesh, out int width, out int height)
        {
            var vertices = mesh.vertices; var triangles = mesh.triangles; var faces = new List<Face>();
            for (int i=0;i<triangles.Length;i+=3)
            {
                Vector3 a=vertices[triangles[i]],b=vertices[triangles[i+1]],c=vertices[triangles[i+2]];
                Vector3 n=Vector3.Cross(b-a,c-a).normalized; float plane=Vector3.Dot(a,n);
                if(n.sqrMagnitude<.5f)continue;
                var face=faces.FirstOrDefault(f=>Vector3.Dot(f.Normal,n)>.99999f&&Mathf.Abs(f.Plane-plane)<.0001f);
                if(face==null)
                {
                    Vector3 t=Vector3.Cross(n,Mathf.Abs(n.y)>.5f?Vector3.forward:Vector3.up).normalized;
                    face=new Face{Normal=n,Tangent=t,Bitangent=Vector3.Cross(t,n),Plane=plane};faces.Add(face);
                }
                face.Triangles.Add(new[]{Project(a,face),Project(b,face),Project(c,face)});
            }
            var charts=new List<Chart>();
            foreach(var face in faces)
            {
                var points=face.Triangles.SelectMany(t=>t).ToArray();
                Vector2 min=new(points.Min(p=>p.x),points.Min(p=>p.y)),max=new(points.Max(p=>p.x),points.Max(p=>p.y));
                int nx=Mathf.Max(1,Mathf.CeilToInt((max.x-min.x)/15.5f)),ny=Mathf.Max(1,Mathf.CeilToInt((max.y-min.y)/15.5f));
                for(int y=0;y<ny;y++)for(int x=0;x<nx;x++)
                {
                    var chart=new Chart{Face=face,Min=new Vector2(Mathf.Lerp(min.x,max.x,x/(float)nx),Mathf.Lerp(min.y,max.y,y/(float)ny)),Max=new Vector2(Mathf.Lerp(min.x,max.x,(x+1f)/nx),Mathf.Lerp(min.y,max.y,(y+1f)/ny))};
                    foreach(var triangle in face.Triangles)
                    {
                        var polygon=new List<Vector2>(triangle);
                        polygon=Clip(polygon,0,chart.Min.x,true);polygon=Clip(polygon,0,chart.Max.x,false);
                        polygon=Clip(polygon,1,chart.Min.y,true);polygon=Clip(polygon,1,chart.Max.y,false);
                        if(polygon.Count>=3)chart.Polygons.Add(polygon);
                    }
                    if(chart.Polygons.Count==0)continue;
                    chart.Width=Mathf.CeilToInt((chart.Max.x-chart.Min.x)*32)+4;chart.Height=Mathf.CeilToInt((chart.Max.y-chart.Min.y)*32)+4;charts.Add(chart);
                }
            }
            charts=charts.OrderByDescending(c=>c.Height).ThenByDescending(c=>c.Width).ToList();
            width=512;height=0;long best=long.MaxValue;
            foreach(int candidate in new[]{256,512,1024,2048})
            {
                if(charts.Any(c=>c.Width>candidate))continue;
                int h=Pack(charts,candidate);long area=(long)candidate*h;
                if(area<best){best=area;width=candidate;height=h;}
            }
            Pack(charts,width);
            var output=new List<Vector3>();var normals=new List<Vector3>();var indices=new List<int>();var uv=new List<Vector2>();
            foreach(var chart in charts)foreach(var polygon in chart.Polygons)
            {
                int first=output.Count;
                foreach(var p in polygon)
                {
                    output.Add(chart.Face.Tangent*p.x+chart.Face.Bitangent*p.y+chart.Face.Normal*chart.Face.Plane);normals.Add(chart.Face.Normal);
                    uv.Add(new Vector2((chart.X+2+(p.x-chart.Min.x)*32)/width,(chart.Y+2+(p.y-chart.Min.y)*32)/height));
                }
                for(int i=1;i<polygon.Count-1;i++){indices.Add(first);indices.Add(first+i);indices.Add(first+i+1);}
            }
            mesh.Clear();mesh.SetVertices(output);mesh.SetNormals(normals);mesh.SetTriangles(indices,0);mesh.SetUVs(0,uv);mesh.SetUVs(1,uv);mesh.RecalculateTangents();mesh.RecalculateBounds();
        }
        static Vector2 Project(Vector3 p,Face f)=>new(Vector3.Dot(p,f.Tangent),Vector3.Dot(p,f.Bitangent));
        static int Pack(List<Chart> charts,int width)
        {
            int x=0,y=0,row=0;
            foreach(var c in charts){if(x+c.Width>width){x=0;y+=row;row=0;}c.X=x;c.Y=y;x+=c.Width;row=Mathf.Max(row,c.Height);}
            return Mathf.Max(32,Mathf.CeilToInt((y+row)/32f)*32);
        }
        static List<Vector2> Clip(List<Vector2> points,int axis,float edge,bool greater)
        {
            var result=new List<Vector2>();if(points.Count==0)return result;
            Vector2 prev=points[^1];bool prevIn=greater?prev[axis]>=edge:prev[axis]<=edge;
            foreach(var p in points)
            {
                bool inside=greater?p[axis]>=edge:p[axis]<=edge;
                if(inside!=prevIn){float t=(edge-prev[axis])/(p[axis]-prev[axis]);result.Add(Vector2.LerpUnclamped(prev,p,t));}
                if(inside)result.Add(p);prev=p;prevIn=inside;
            }
            return result;
        }
    }
}
#endif
