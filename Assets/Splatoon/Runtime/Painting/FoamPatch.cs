using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Painting
{
    public sealed class FoamPatch
    {
        public readonly PaintSurface Surface;
        public readonly PaintRegion Region;
        public readonly FoamPatchBake Bake;
        public readonly Matrix4x4 Matrix, Inverse;
        public readonly Vector3 Normal, Origin;
        public readonly List<FoamChunk> Chunks = new();
        public readonly List<FoamPatch> Neighbours = new();
        public Texture2D OwnerTexture { get; private set; }
        readonly Color32[] _ownerPixels;
        internal readonly FoamNode[] Nodes;
        internal bool OwnershipDirty;
        Vector2 _dirtyMin=new(float.PositiveInfinity,float.PositiveInfinity),_dirtyMax=new(float.NegativeInfinity,float.NegativeInfinity);
        public int Key => Bake.RegionKey;
        public int Columns => Bake.Columns;
        public int Rows => Bake.Rows;
        public Vector2 Step => new(Bake.Size.x / (Columns - 1), Bake.Size.y / (Rows - 1));
        // Ownership depends on triangle barycentric weights and node owners, not surface height.
        // Avoid constructing world positions, normals and cross products for every score-grid cell.
        byte OwnerAtLocal(Vector3 p)
        {
            var step=Step;
            float gx=Mathf.Clamp((p.x+Bake.Size.x*.5f)/step.x,0,Columns-1);
            float gz=Mathf.Clamp((p.z+Bake.Size.y*.5f)/step.y,0,Rows-1);
            int x=Mathf.Min(Columns-2,(int)gx),z=Mathf.Min(Rows-2,(int)gz),i=z*Columns+x;
            float u=gx-x,v=gz-z;
            FoamNode a,b,c;float wa,wb,wc;
            if(u+v<=1){a=Nodes[i];b=Nodes[i+Columns];c=Nodes[i+1];wa=1-u-v;wb=v;wc=u;}
            else{a=Nodes[i+1];b=Nodes[i+Columns];c=Nodes[i+Columns+1];wa=1-v;wb=1-u;wc=u+v-1;}
            if(a.Limit<=0||b.Limit<=0||c.Limit<=0)return 0;
            return wa>=wb&&wa>=wc?a.Owner:wb>=wc?b.Owner:c.Owner;
        }

        internal FoamPatch(PaintSurface surface, PaintRegion region, FoamPatchBake bake)
        {
            Surface = surface; Region = region; Bake = bake;
            Matrix = region.Matrix(surface); Inverse = region.Inverse(surface);
            Normal = region.Normal(surface); Origin = Matrix.MultiplyPoint3x4(Vector3.zero);
            Nodes = new FoamNode[Columns * Rows];
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                OwnerTexture = new Texture2D(Columns, Rows, TextureFormat.RGBA32, false, true)
                { name = "Foam owners " + bake.RegionKey, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _ownerPixels = new Color32[Nodes.Length];
            }
        }
        public Vector3 BasePoint(Vector3 world) => world - Vector3.up * (Vector3.Dot(world - Origin, Normal) / Normal.y);
        public Vector3 Local(Vector3 world) => Inverse.MultiplyPoint3x4(BasePoint(world));
        public Vector3 NodeBase(int x, int z) => Matrix.MultiplyPoint3x4(new Vector3(-Bake.Size.x * .5f + x * Step.x, 0, -Bake.Size.y * .5f + z * Step.y));
        public bool Contains(Vector3 world, float margin = 0)
        {
            var p = Local(world);
            return Mathf.Abs(p.x) <= Bake.Size.x * .5f + margin && Mathf.Abs(p.z) <= Bake.Size.y * .5f + margin;
        }
        internal bool Triangle(Vector3 world, out FoamNode a, out FoamNode b, out FoamNode c, out Vector3 weights)
        {
            a = b = c = null; weights = default;
            if (!Contains(world, .0001f)) return false;
            var p = Local(world); var step = Step;
            float gx = Mathf.Clamp((p.x + Bake.Size.x * .5f) / step.x, 0, Columns - 1);
            float gz = Mathf.Clamp((p.z + Bake.Size.y * .5f) / step.y, 0, Rows - 1);
            int x = Mathf.Min(Columns - 2, (int)gx), z = Mathf.Min(Rows - 2, (int)gz);
            float u = gx - x, v = gz - z; int i = z * Columns + x;
            if (u + v <= 1)
            { a = Nodes[i]; b = Nodes[i + Columns]; c = Nodes[i + 1]; weights = new Vector3(1-u-v,v,u); }
            else
            { a = Nodes[i+1]; b = Nodes[i+Columns]; c = Nodes[i+Columns+1]; weights = new Vector3(1-v,1-u,u+v-1); }
            return a.Limit > 0 && b.Limit > 0 && c.Limit > 0;
        }
        public bool Sample(Vector3 world, out Vector3 top, out Vector3 normal, out byte team)
        {
            top = normal = default; team = 0;
            if (!Triangle(world, out var a, out var b, out var c, out var w)) return false;
            top = a.Top * w.x + b.Top * w.y + c.Top * w.z;
            normal = Vector3.Cross(b.Top - a.Top, c.Top - a.Top).normalized;
            if (normal.y < 0) normal = -normal;
            var closest = w.x >= w.y && w.x >= w.z ? a : w.y >= w.z ? b : c;
            team = closest.Owner;
            return true;
        }
        internal void MarkOwnership(Vector3 world)
        {
            var local=Local(world);var p=new Vector2(local.x,local.z);
            _dirtyMin=Vector2.Min(_dirtyMin,p-Step);_dirtyMax=Vector2.Max(_dirtyMax,p+Step);OwnershipDirty=true;
        }
        // Clip terrain triangles to the rigid paper footprint. The maximum of two linear
        // planes occurs at a clipped polygon vertex, so narrow peaks cannot fall between probes.
        public bool PaperSupport(Vector3 center,Vector3 normal,Vector3 right,Vector3 forward,Vector2 size,ref float lift,ref byte owner)
        {
            float det=right.x*forward.z-right.z*forward.x;if(Mathf.Abs(det)<.001f)return false;
            var local=Local(center);float reach=size.magnitude*.5f/Normal.y;var step=Step;
            int x0=Mathf.Max(0,Mathf.FloorToInt((local.x-reach+Bake.Size.x*.5f)/step.x)),x1=Mathf.Min(Columns-2,Mathf.CeilToInt((local.x+reach+Bake.Size.x*.5f)/step.x));
            int z0=Mathf.Max(0,Mathf.FloorToInt((local.z-reach+Bake.Size.y*.5f)/step.y)),z1=Mathf.Min(Rows-2,Mathf.CeilToInt((local.z+reach+Bake.Size.y*.5f)/step.y));
            Span<Vector3> polygon=stackalloc Vector3[10];Span<Vector3> scratch=stackalloc Vector3[10];bool found=false;
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)for(int half=0;half<2;half++)
            {
                int i=z*Columns+x;var a=Nodes[i+(half==0?0:1)];var b=Nodes[i+Columns];var c=Nodes[i+(half==0?1:Columns+1)];
                if(a.Limit<=0||b.Limit<=0||c.Limit<=0)continue;
                polygon[0]=a.Top;polygon[1]=b.Top;polygon[2]=c.Top;int count=3;
                for(int edge=0;edge<4&&count>0;edge++)
                {
                    int output=0;var prev=polygon[count-1];float pd=PaperEdge(prev-center,edge,right,forward,size,det);
                    for(int n=0;n<count;n++)
                    {
                        var curr=polygon[n];float cd=PaperEdge(curr-center,edge,right,forward,size,det);
                        if((pd>=0)!=(cd>=0))scratch[output++]=Vector3.LerpUnclamped(prev,curr,pd/(pd-cd));
                        if(cd>=0)scratch[output++]=curr;
                        prev=curr;pd=cd;
                    }
                    count=output;for(int n=0;n<count;n++)polygon[n]=scratch[n];
                }
                for(int n=0;n<count;n++)
                {
                    found=true;var p=polygon[n];var d=p-center;float amount=d.y+(normal.x*d.x+normal.z*d.z)/normal.y;
                    if(amount>lift){lift=amount;Sample(p,out _,out _,out owner);}
                }
            }
            return found;
        }
        static float PaperEdge(Vector3 d,int edge,Vector3 right,Vector3 forward,Vector2 size,float det)
        {
            float u=(d.x*forward.z-d.z*forward.x)/det,v=(right.x*d.z-right.z*d.x)/det;
            return edge==0?size.x*.5f-u:edge==1?size.x*.5f+u:edge==2?size.y*.5f-v:size.y*.5f+v;
        }
        internal void UpdateOwnership()
        {
            if (!OwnershipDirty) return;
            OwnershipDirty = false;
            if (OwnerTexture != null)
            {
                for (int i=0;i<Nodes.Length;i++) _ownerPixels[i] = new Color32(Nodes[i].Owner,0,0,255);
                OwnerTexture.SetPixels32(_ownerPixels); OwnerTexture.Apply(false,false);
            }
            var grid = Region.Grid;
            bool full=float.IsPositiveInfinity(_dirtyMin.x);
            int minX=full?0:Mathf.Max(0,Mathf.FloorToInt((_dirtyMin.x+Bake.Size.x*.5f)/grid.CellSize));
            int maxX=full?grid.Columns-1:Mathf.Min(grid.Columns-1,Mathf.CeilToInt((_dirtyMax.x+Bake.Size.x*.5f)/grid.CellSize));
            int minZ=full?0:Mathf.Max(0,Mathf.FloorToInt((_dirtyMin.y+Bake.Size.y*.5f)/grid.CellSize));
            int maxZ=full?grid.Rows-1:Mathf.Min(grid.Rows-1,Mathf.CeilToInt((_dirtyMax.y+Bake.Size.y*.5f)/grid.CellSize));
            for (int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
            {
                int i=z*grid.Columns+x;
                if (grid.Cells[i] == 255) continue;
                grid.Set(i,OwnerAtLocal(grid.Center(i)));
            }
            _dirtyMin=new Vector2(float.PositiveInfinity,float.PositiveInfinity);_dirtyMax=new Vector2(float.NegativeInfinity,float.NegativeInfinity);
        }
        internal void Dispose()
        {
            if (OwnerTexture != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(OwnerTexture); else UnityEngine.Object.DestroyImmediate(OwnerTexture); }
        }
    }
}
