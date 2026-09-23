using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Splatoon.Config;

namespace Splatoon.Painting
{
    public enum InkLook { Current, Soft }
    public enum InkBoundaryDebug { Off, OuterDistance, TeamDistance, Normals, ContourComparison }

    public sealed partial class PaintSurface
    {
        public InkLook Look { get; private set; }
        public const float BoundaryRange = .24f;
        public RenderTexture BoundaryTexture { get { FlushDisplay(); FlushSoftEdges(); return _boundary; } }
        public int BoundaryUpdates { get; private set; }
        public long BoundaryUpdatedPixels { get; private set; }
        RenderTexture _boundary;
        InkBoundaryTopology _boundaryTopology;
        readonly Dictionary<InkBoundaryTopology.Chart, RectInt> _boundaryDirty = new();
        static readonly List<PaintSurface> SoftSurfaces = new();
        static RenderTexture BoundaryField, BoundarySeedA, BoundarySeedB, BoundaryResolved;
        static Material BoundaryMaterial;
        static Mesh BoundaryQuad;
        static bool TopologyDirty;
        static readonly Unity.Profiling.ProfilerMarker BoundaryMarker = new("Splatoon.Paint.Boundary");

        public void SetInkLook(InkLook look)
        {
            if (!GraphicsEnabled) return;
            Initialize(); Look = look;
            if (look == InkLook.Soft && _boundary == null)
            {
                try
                {
                    _boundary = Texture(" Boundary", SelectBoundaryFormat());
                    _boundaryTopology = new InkBoundaryTopology(this);
                    SoftSurfaces.Add(this); TopologyDirty = true;
                    InvalidateInkBoundary();
                    if (AllocatedBytes + CheckpointBytes > GameplayConfig.Global.MaxPaintMemoryMiB * 1048576L)
                        throw new InvalidOperationException("墨迹边界缓存超过显存预算");
                }
                catch { ReleaseSoftEdges(); Look = InkLook.Current; throw; }
            }
            _renderer.GetPropertyBlock(_properties);
            InkAppearanceProfile.Current.BindSoft(_properties);
            _properties.SetFloat("_InkSoftEdge", look == InkLook.Soft ? 1 : 0);
            _properties.SetTexture("_InkBoundary", _boundary);
            _properties.SetFloat("_InkBoundaryRange", BoundaryRange);
            _properties.SetVector("_InkBoundaryDecode", _boundary!=null && _boundary.graphicsFormat==GraphicsFormat.R16G16_UNorm ? new Vector4(2*BoundaryRange,-BoundaryRange,0,0) : new Vector4(1,0,0,0));
            _renderer.SetPropertyBlock(_properties);
            // Keep warm caches while comparing looks; switching never rewrites either persistent plane.
        }
        public void SetInkBoundaryDebug(InkBoundaryDebug mode)
        {
            if (_renderer == null) return;
            _renderer.GetPropertyBlock(_properties); _properties.SetFloat("_InkBoundaryDebug", (float)mode); _renderer.SetPropertyBlock(_properties);
        }
        public static GraphicsFormat SelectBoundaryFormat(Func<GraphicsFormat, bool> supported = null)
        {
            supported ??= PaintTextureMemory.SupportsIslandFormat;
            foreach (var f in new[] { GraphicsFormat.R16G16_SFloat, GraphicsFormat.R16G16_UNorm }) if (supported(f)) return f;
            throw new NotSupportedException("设备不支持双通道墨迹边界缓存");
        }
        public void InvalidateInkBoundary()
        {
            if (_boundaryTopology == null) return;
            foreach (var c in _boundaryTopology.Charts) _boundaryDirty[c] = ChartRect(c);
        }
        RectInt ChartRect(InkBoundaryTopology.Chart c)
        {
            int x = Mathf.Max(0, Mathf.FloorToInt(c.UV.xMin * Resolution) - 1), y = Mathf.Max(0, Mathf.FloorToInt(c.UV.yMin * Height) - 1);
            return new RectInt(x, y, Mathf.Min(Resolution, Mathf.CeilToInt(c.UV.xMax * Resolution) + 1) - x,
                Mathf.Min(Height, Mathf.CeilToInt(c.UV.yMax * Height) + 1) - y);
        }
        static RectInt Intersect(RectInt a, RectInt b) => new(Mathf.Max(a.xMin,b.xMin), Mathf.Max(a.yMin,b.yMin),
            Mathf.Max(0,Mathf.Min(a.xMax,b.xMax)-Mathf.Max(a.xMin,b.xMin)), Mathf.Max(0,Mathf.Min(a.yMax,b.yMax)-Mathf.Max(a.yMin,b.yMin)));
        void DirtySoftEdges(PaintStamp stamp)
        {
            if(_boundaryTopology==null)return;
            if(TopologyDirty) { foreach(var s in SoftSurfaces)s.InvalidateInkBoundary(); return; }
            // Only the receiving mesh can change persistent pixels. Other surfaces
            // depend on this change only through a registered geometric seam.
            foreach (var c in _boundaryTopology.Charts)
            {
                float alignment=Vector3.Dot(c.Normal,stamp.Normal.normalized);
                if(alignment<.5f)continue;
                float radius = stamp.Radius * Mathf.Max(1, stamp.DepthScale)/Mathf.Max(.5f,alignment) + BoundaryRange * 2;
                if (Mathf.Abs(Vector3.Dot(stamp.Position-c.Origin,c.Normal)) > radius) continue;
                if(!MarkBoundaryDirty(c,stamp.Position,radius))continue;
                foreach(var neighbour in c.Neighbours)
                    neighbour.chart.Surface.MarkBoundaryDirty(neighbour.chart,neighbour.fold.inverse.MultiplyPoint3x4(stamp.Position),radius);
            }
        }
        bool MarkBoundaryDirty(InkBoundaryTopology.Chart chart,Vector3 position,float radius)
        {
            var uv=chart.Project(position);
            var extent=new Vector2(chart.DualU.magnitude,chart.DualV.magnitude)*radius;
            int x=Mathf.FloorToInt((uv.x-extent.x)*Resolution/32)*32,y=Mathf.FloorToInt((uv.y-extent.y)*Height/32)*32;
            var rect=new RectInt(x,y,Mathf.CeilToInt((uv.x+extent.x)*Resolution/32)*32-x,Mathf.CeilToInt((uv.y+extent.y)*Height/32)*32-y);
            rect=Intersect(rect,ChartRect(chart));if(rect.width<=0 || rect.height<=0)return false;
            if(_boundaryDirty.TryGetValue(chart,out var old))rect=new RectInt(Mathf.Min(old.x,rect.x),Mathf.Min(old.y,rect.y),
                Mathf.Max(old.xMax,rect.xMax)-Mathf.Min(old.x,rect.x),Mathf.Max(old.yMax,rect.yMax)-Mathf.Min(old.y,rect.y));
            _boundaryDirty[chart]=rect;return true;
        }
        static RenderTexture BoundaryScratch(string name, int w, int h, GraphicsFormat format)
        {
            var rt = new RenderTexture(w,h,0) { graphicsFormat=format, name=name, filterMode=FilterMode.Point, wrapMode=TextureWrapMode.Clamp };
            if(!rt.Create()) { DisposeObject(rt); throw new NotSupportedException("Cannot create boundary scratch " + format); }
            AllocatedBytes+=PaintTextureMemory.Bytes(rt); return rt;
        }
        static void EnsureBoundaryScratch(int width,int height,GraphicsFormat format)
        {
            if(BoundaryField!=null && BoundaryField.width>=width && BoundaryField.height>=height && BoundaryResolved.graphicsFormat==format)return;
            width=Mathf.Max(width,BoundaryField!=null?BoundaryField.width:0); height=Mathf.Max(height,BoundaryField!=null?BoundaryField.height:0);
            ReleaseBoundaryScratch();
            BoundaryField=BoundaryScratch("Ink boundary field",width,height,GraphicsFormat.R16G16B16A16_SFloat);
            // Full float seed coordinates avoid half-float stair steps on large charts.
            BoundarySeedA=BoundaryScratch("Ink boundary seed A",width,height,GraphicsFormat.R32G32B32A32_SFloat);
            BoundarySeedB=BoundaryScratch("Ink boundary seed B",width,height,GraphicsFormat.R32G32B32A32_SFloat);
            BoundaryResolved=BoundaryScratch("Ink boundary resolve",width,height,format);
            if(AllocatedBytes+CheckpointBytes>GameplayConfig.Global.MaxPaintMemoryMiB*1048576L)throw new InvalidOperationException("墨迹边界暂存超过显存预算");
        }
        static void ReleaseBoundaryScratch()
        {
            Release(BoundaryField); Release(BoundarySeedA); Release(BoundarySeedB); Release(BoundaryResolved);
            BoundaryField=BoundarySeedA=BoundarySeedB=BoundaryResolved=null;
        }
        void FlushSoftEdges()
        {
            if(_boundary==null || _boundaryDirty.Count==0)return;
            using var marker=BoundaryMarker.Auto();
            // Neighbour support must see this frame's complete stamp batch, regardless of component order.
            foreach(var s in SoftSurfaces)s.FlushPaint();
            if(TopologyDirty)
            {
                var all=new List<InkBoundaryTopology>(); foreach(var s in SoftSurfaces)all.Add(s._boundaryTopology);
                InkBoundaryTopology.Connect(all); TopologyDirty=false;
                foreach(var s in SoftSurfaces)s.InvalidateInkBoundary();
            }
            if(BoundaryMaterial==null)BoundaryMaterial=new Material(Resources.Load<Shader>("InkBoundaryCache")) { hideFlags=HideFlags.HideAndDontSave };
            foreach(var pair in _boundaryDirty) BuildBoundaryChart(pair.Key,pair.Value);
            _boundaryDirty.Clear(); BoundaryUpdates++;
        }
        void BuildBoundaryChart(InkBoundaryTopology.Chart chart,RectInt core)
        {
            Vector3 u=chart.U/Resolution,v=chart.V/Height;
            int halo=Mathf.CeilToInt(BoundaryRange*Mathf.Max(chart.DualU.magnitude*Resolution,chart.DualV.magnitude*Height))+3;
            int width=core.width+halo*2,height=core.height+halo*2;
            EnsureBoundaryScratch(width,height,_boundary.graphicsFormat);
            var m=BoundaryMaterial; var region=new Vector4(core.x-halo,core.y-halo,0,0);
            m.SetVector("_Size",new Vector4(width,height,0,0));
            m.SetVector("_Region",region); m.SetVector("_AtlasSize",new Vector4(Resolution,Height,0,0));
            m.SetVector("_Metric",new Vector4(Vector3.Dot(u,u),Vector3.Dot(u,v),Vector3.Dot(v,v),0));
            m.SetVector("_Origin",chart.Origin); m.SetVector("_DualU",chart.DualU); m.SetVector("_DualV",chart.DualV);
            m.SetFloat("_Range",BoundaryRange); m.SetFloat("_Threshold",GameplayConfig.Global.PaintThreshold);
            m.SetFloat("_EncodeUNorm",_boundary.graphicsFormat==GraphicsFormat.R16G16_UNorm?1:0);
            m.SetFloat("_WorldScale",GameplayConfig.Global.PaintWorldUvScale); m.SetFloat("_NoiseScale",GameplayConfig.Global.PaintShapeNoiseScale);
            var command=CommandBufferPool.Get("Ink boundary field");
            if(BoundaryQuad==null)
            {
                BoundaryQuad=new Mesh { name="Ink boundary fullscreen", hideFlags=HideFlags.HideAndDontSave };
                BoundaryQuad.vertices=new[]{new Vector3(-1,-1,0),new Vector3(-1,1,0),new Vector3(1,1,0),new Vector3(1,-1,0)};
                BoundaryQuad.triangles=new[]{0,1,2,0,2,3};
            }
            try
            {
                command.BeginSample("Splatoon.Paint.BoundaryGPU");
                command.SetRenderTarget(BoundaryField); command.ClearRenderTarget(false,true,Color.clear);
                command.SetViewport(new Rect(0,0,width,height));
                foreach(var neighbour in chart.Neighbours)Draw(neighbour.chart,neighbour.fold);
                Draw(chart,Matrix4x4.identity);
                Pass(BoundaryField,BoundarySeedA,1,0);
                RenderTexture a=BoundarySeedA,b=BoundarySeedB;
                for(int jump=Mathf.NextPowerOfTwo(halo);jump>=1;jump/=2) { Pass(a,b,2,jump); (a,b)=(b,a); }
                Pass(a,b,2,1); (a,b)=(b,a);
                Pass(a,BoundaryResolved,3,0);
                command.CopyTexture(BoundaryResolved,0,0,halo,halo,core.width,core.height,_boundary,0,0,core.x,core.y);
                command.EndSample("Splatoon.Paint.BoundaryGPU");
                Graphics.ExecuteCommandBuffer(command);
            }
            finally { CommandBufferPool.Release(command); }
            void Draw(InkBoundaryTopology.Chart source,Matrix4x4 fold)
            {
                var block=new MaterialPropertyBlock(); block.SetTexture("_State",source.Surface._mask);
                block.SetTexture("_Occupancy",source.Surface._islands);
                block.SetVector("_SourceTexel",new Vector4(1f/source.Surface.Resolution,1f/source.Surface.Height,0,0));
                block.SetVector("_SourceRect",new Vector4(source.UV.xMin,source.UV.yMin,source.UV.xMax,source.UV.yMax));
                block.SetMatrix("_Fold",fold); block.SetVector("_Normal",source.Normal);
                command.DrawMesh(source.Mesh,Matrix4x4.identity,m,0,0,block);
            }
            void Pass(RenderTexture source,RenderTexture destination,int pass,int jump)
            {
                var block=new MaterialPropertyBlock();block.SetTexture("_MainTex",source);block.SetTexture("_Field",BoundaryField);block.SetFloat("_Jump",jump);
                command.SetRenderTarget(destination);command.SetViewport(new Rect(0,0,width,height));
                command.DrawMesh(BoundaryQuad,Matrix4x4.identity,m,0,pass,block);
            }
            BoundaryUpdatedPixels+=(long)core.width*core.height;
        }
        void ClearSoftEdges()
        {
            if(_boundary==null)return;
            foreach(var s in SoftSurfaces)s.InvalidateInkBoundary();
        }
        void ReleaseSoftEdges()
        {
            Release(_boundary); _boundary=null; _boundaryTopology?.Dispose(); _boundaryTopology=null; _boundaryDirty.Clear();
            SoftSurfaces.Remove(this); TopologyDirty=true;
            foreach(var s in SoftSurfaces)s.InvalidateInkBoundary();
            if(SoftSurfaces.Count==0)
            {
                ReleaseBoundaryScratch(); if(BoundaryMaterial!=null)DisposeObject(BoundaryMaterial); BoundaryMaterial=null;
                if(BoundaryQuad!=null)DisposeObject(BoundaryQuad);BoundaryQuad=null;
            }
            Look=InkLook.Current;
        }
    }
}
