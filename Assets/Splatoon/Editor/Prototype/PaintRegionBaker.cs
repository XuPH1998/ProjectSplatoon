#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Splatoon.Painting;

namespace Splatoon.Editor
{
    public static class PaintRegionBaker
    {
        sealed class Plane
        {
            public Vector3 Normal;
            public float Distance;
            public readonly List<Vector3> Triangles = new();
        }
        public static void Bake(PaintSurface surface, float cellSize)
        {
            var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices; var indices = mesh.triangles; var planes = new List<Plane>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = vertices[indices[i]]; var b = vertices[indices[i+1]]; var c = vertices[indices[i+2]];
                var normal = Vector3.Cross(b-a,c-a).normalized;
                var worldNormal = surface.transform.TransformDirection(normal);
                // Only vertical planes and tops; authored scoring faces already own their ground grid.
                if (normal.sqrMagnitude < .5f || worldNormal.y < -.01f || (Mathf.Abs(worldNormal.y) > .01f && worldNormal.y < .65f) || (surface.Scores && worldNormal.y > .65f)) continue;
                float distance = Vector3.Dot(normal,a);
                var plane = planes.FirstOrDefault(p => Vector3.Dot(p.Normal,normal) > .99999f && Mathf.Abs(p.Distance-distance) < .001f);
                if (plane == null) { plane = new Plane { Normal = normal, Distance = distance }; planes.Add(plane); }
                plane.Triangles.AddRange(new[]{a,b,c});
            }
            var output = new List<PaintRegion>();
            foreach (var plane in planes.OrderBy(p=>p.Normal.x).ThenBy(p=>p.Normal.y).ThenBy(p=>p.Normal.z).ThenBy(p=>p.Distance))
            {
                Vector3 forward = Mathf.Abs(Vector3.Dot(plane.Normal,Vector3.up)) < .01f ? Vector3.up : Vector3.forward;
                Quaternion rotation = Quaternion.LookRotation(forward,plane.Normal); var inverse = Quaternion.Inverse(rotation);
                var points = plane.Triangles.Select(v=>inverse*v).ToArray();
                float minX=points.Min(v=>v.x), maxX=points.Max(v=>v.x), minZ=points.Min(v=>v.z), maxZ=points.Max(v=>v.z);
                Vector3 center=new((minX+maxX)/2,plane.Distance,(minZ+maxZ)/2);
                if (maxX-minX < .01f || maxZ-minZ < .01f) continue;
                var region=new PaintRegion { Id=output.Count+1, Origin=rotation*center, Rotation=rotation, Size=new Vector2(maxX-minX,maxZ-minZ),
                    Climbable=Mathf.Abs(surface.transform.TransformDirection(plane.Normal).y)<.01f && !surface.name.Contains("Boundary") && !surface.name.Contains("Rail") && !surface.name.Contains("Guard") };
                // Keep explicit author decisions for an unchanged plane when re-baking.
                var previous=surface.WallRegions.FirstOrDefault(r=>(r.Origin-region.Origin).sqrMagnitude<.000001f && Quaternion.Angle(r.Rotation,region.Rotation)<.01f);
                if(previous!=null)region.Climbable=previous.Climbable;
                var grid=new SurfaceOwnershipGrid(region.Size,cellSize,null); var blocked=new List<int>();
                for(int cell=0;cell<grid.Cells.Length;cell++)
                {
                    var point=grid.Center(cell)+center; bool inside=false;
                    for(int i=0;i<points.Length;i+=3)if(Inside(point,points[i],points[i+1],points[i+2])){inside=true;break;}
                    if(!inside)blocked.Add(cell);
                }
                region.BlockedCells=blocked.ToArray();output.Add(region);
            }
            if(output.Count>255)throw new InvalidOperationException("单个表面超过 255 个平面区域："+surface.name);
            surface.WallRegions=output.ToArray();surface.InitializeOwnership(cellSize);
        }
        static float Cross(Vector3 a, Vector3 b, Vector3 p)=>(b.x-a.x)*(p.z-a.z)-(b.z-a.z)*(p.x-a.x);
        static bool Inside(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        { float x=Cross(a,b,p),y=Cross(b,c,p),z=Cross(c,a,p);return (x>=-.0001f&&y>=-.0001f&&z>=-.0001f)||(x<=.0001f&&y<=.0001f&&z<=.0001f); }
    }
}
#endif
