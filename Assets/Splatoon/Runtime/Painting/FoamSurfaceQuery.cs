using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon.Painting
{
    public static class FoamSurfaceQuery
    {
        public static bool Contact(Collider collider,Vector3 point,Vector3 normal,out InkContact contact)
        {
            var chunk=collider.GetComponent<FoamChunk>();
            if(chunk!=null && chunk.Patch.Sample(point,out _,out _,out byte owner))
            {
                var p=chunk.Patch;
                contact=new InkContact(p.Surface,p.Region,point,normal,owner,true,p.Local(point),PrototypeArena.Current?.Foam?.Revision??0);
                return true;
            }
            var surface=collider.GetComponentInParent<PaintSurface>();
            if(surface!=null && surface.QueryRegion(point,normal,out contact))return true;
            contact=default;return false;
        }
        public static bool InsideOrNear(Vector3 point,float radius,out FoamChunk chunk,out Vector3 closest,out Vector3 normal)
            => InsideOrNear(point,radius,out chunk,out closest,out normal,out _);
        public static bool InsideOrNear(Vector3 point,float radius,out FoamChunk chunk,out Vector3 closest,out Vector3 normal,out float distance)
            => InsideOrNear(PrototypeArena.Current?.Foam,point,radius,out chunk,out closest,out normal,out distance);
        public static bool InsideOrNear(FoamTerrainWorld world,Vector3 point,float radius,out FoamChunk chunk,out Vector3 closest,out Vector3 normal,out float distance)
        {
            chunk=null;closest=normal=default;float best=float.PositiveInfinity;
            distance=0;if(world==null)return false;
            foreach(var patch in world.PatchValues)
            {
                bool inside=patch.Sample(point,out var top,out var n,out _) && top.y>patch.BasePoint(point).y+.0005f && point.y>=patch.BasePoint(point).y && point.y<=top.y;
                foreach(var candidate in patch.Chunks)
                {
                    if(candidate.Collider==null||!candidate.Collider.enabled)continue;
                    var bounds=candidate.Collider.bounds;bounds.Expand(radius*2+.002f);
                    if(!bounds.Contains(point))continue;
                    if(inside){chunk=candidate;closest=top;normal=n;distance=0;return true;}
                    if(candidate.ClosestPoint(point,out var p,out var face))
                    {
                        float d=(point-p).sqrMagnitude;
                        if(d<=radius*radius+.00000001f&&d<best){best=d;chunk=candidate;closest=p;normal=face;}
                    }
                }
            }
            distance=Mathf.Sqrt(best);
            return chunk!=null;
        }
    }
}
