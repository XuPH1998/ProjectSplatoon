using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Profiling;
using Splatoon.Prototype;

namespace Splatoon.Painting
{
    public sealed class FoamTerrainWorld : IDisposable
    {
        static readonly ProfilerMarker CommitMarker = new("Splatoon.Foam.Commit");
        static readonly ProfilerMarker MeshMarker = new("Splatoon.Foam.MeshAndCollider");
        static readonly ProfilerMarker DepositMarker = new("Splatoon.Foam.Deposit");
        static readonly ProfilerMarker RoundMarker = new("Splatoon.Foam.Round");
        static readonly ProfilerMarker RelaxMarker = new("Splatoon.Foam.Relax");
        static readonly ProfilerMarker OwnerMarker = new("Splatoon.Foam.Ownership");
        static readonly ProfilerMarker InstallMarker = new("Splatoon.Foam.Install");
        readonly HashSet<int> _roundNodes = new();
        readonly List<int> _roundWork = new(4096);
        readonly Dictionary<FoamChunk,FoamSurfaceChange> _feedback = new();
        public event Action<FoamSurfaceChange> SurfaceChanged;
        public event Action SurfaceReset;
        public event Action<FoamUpdateMetrics> Updated;
        public FoamUpdateMetrics LastMetrics { get; private set; }
        FoamUpdateMetrics _metrics;
        public bool PresentationEnabled { get; set; } = true;
        public uint RoundingPassCount { get; private set; }
        public double PendingVolume { get { double v=0;foreach(var n in _nodes)v+=n.PendingHeight*n.Area;return v; } }
        public double CommittedVolume { get { double v=0;foreach(var n in _nodes)v+=n.Height*n.Area;return v; } }
        public double QuantizationVolumeBound { get { double v=0;foreach(var n in _nodes)if(n.PendingHeight>0||n.HeightMm>0)v+=n.Area*.0005;return v; } }
        static long Clock()=>System.Diagnostics.Stopwatch.GetTimestamp();
        static double Elapsed(long start)=>(Clock()-start)*1000d/System.Diagnostics.Stopwatch.Frequency;
        readonly Dictionary<int,FoamPatch> _patches = new();
        readonly List<FoamNode> _nodes = new();
        readonly List<Request> _requests = new(256);
        readonly HashSet<int> _active = new();
        readonly List<int> _work = new(4096), _changed = new(4096);
        readonly HashSet<FoamChunk> _dirtyChunks = new();
        readonly HashSet<FoamPatch> _dirtyPatches = new();
        readonly HashSet<int> _dirtyNormals=new();
        readonly List<FoamPatch> _candidatePatches = new(32);
        readonly HashSet<int> _visited = new();
        readonly HashSet<int> _reachable = new();
        readonly List<FoamNode> _flood=new(4096);
        readonly List<(FoamNode node,float weight)> _weights = new(4096);
        readonly MemoryStream _deltaStream = new();
        readonly BinaryWriter _deltaWriter;
        readonly float _dissolve, _slope;
        static readonly Comparison<int> NodeOrder=(a,b)=>a.CompareTo(b);
        struct Request { public PaintStamp Stamp; public FoamPatch Patch; public float Volume; }
        public IReadOnlyDictionary<int,FoamPatch> Patches => _patches;
        public Dictionary<int,FoamPatch>.ValueCollection PatchValues=>_patches.Values;
        public int LastDeltaBytes {get;private set;}
        public uint Revision { get; private set; }
        public int NodeCount => _nodes.Count;
        public int LastDirtyChunks { get; private set; }
        public int PendingCount => _requests.Count;
        public double LastCommitMilliseconds {get;private set;}
        public uint CommitCount {get;private set;}
        public Func<Vector3,float,float> LimitGrowth;

        public FoamTerrainWorld(PrototypeArena arena, FoamTerrainData data, Material material, float dissolve, float slopeDegrees)
        {
            _dissolve=dissolve; _slope=Mathf.Tan(slopeDegrees*Mathf.Deg2Rad); _deltaWriter=new BinaryWriter(_deltaStream);
            if(data==null || data.Patches.Length==0) throw new InvalidOperationException("缺少泡沫地图烘焙数据");
            var weld = new Dictionary<Vector3Int,FoamNode>();
            foreach(var bake in data.Patches)
            {
                if(!arena.Surfaces.TryGetValue(bake.RegionKey/256,out var surface)) throw new InvalidDataException("泡沫表面 ID 无效");
                PaintRegion region=null; foreach(var r in surface.GameplayRegions) if(r.Key(surface)==bake.RegionKey) {region=r;break;}
                if(region==null || bake.Columns<2 || bake.Rows<2 || bake.CeilingMm.Length!=bake.Columns*bake.Rows || bake.Edges.Length!=bake.CeilingMm.Length)
                    throw new InvalidDataException("泡沫烘焙区域尺寸无效");
                var patch=new FoamPatch(surface,region,bake); _patches.Add(patch.Key,patch);
                for(int z=0;z<patch.Rows;z++) for(int x=0;x<patch.Columns;x++)
                {
                    int i=z*patch.Columns+x; Vector3 point=patch.NodeBase(x,z);
                    var key=new Vector3Int(Mathf.RoundToInt(point.x*1000),Mathf.RoundToInt(point.y*1000),Mathf.RoundToInt(point.z*1000));
                    float area=patch.Step.x*patch.Step.y*patch.Normal.y*(x==0||x==patch.Columns-1?.5f:1)*(z==0||z==patch.Rows-1?.5f:1);
                    bool boundary=x==0||z==0||x==patch.Columns-1||z==patch.Rows-1;
                    FoamNode node;
                    if(bake.CeilingMm[i]>0 && weld.TryGetValue(key,out node) && (boundary||node.Boundary))
                    {
                        foreach(var neighbour in node.Patches)
                        { if(!patch.Neighbours.Contains(neighbour)) patch.Neighbours.Add(neighbour); if(!neighbour.Neighbours.Contains(patch))neighbour.Neighbours.Add(patch); }
                        node.Limit=Mathf.Min(node.Limit,bake.CeilingMm[i]*.001f); node.Area+=area;node.Boundary|=boundary;
                    }
                    else
                    {
                        node=new FoamNode { Id=_nodes.Count,Base=point,Limit=bake.CeilingMm[i]*.001f,Area=area,Boundary=boundary };
                        _nodes.Add(node); if(node.Limit>0) weld[key]=node;
                    }
                    patch.Nodes[i]=node; if(!node.Patches.Contains(patch))node.Patches.Add(patch);
                }
            }
            foreach(var patch in _patches.Values)
            {
                for(int z=0;z<patch.Rows;z++)for(int x=0;x<patch.Columns;x++)
                {
                    int i=z*patch.Columns+x;
                    if(x+1<patch.Columns && (patch.Bake.Edges[i]&1)!=0) Connect(patch.Nodes[i],patch.Nodes[i+1]);
                    if(z+1<patch.Rows && (patch.Bake.Edges[i]&2)!=0) Connect(patch.Nodes[i],patch.Nodes[i+patch.Columns]);
                    // Eight-way relaxation avoids the four-sided pyramids produced by an axial stencil.
                    // Only cross a cell when all four baked boundary edges are traversable.
                    if(x+1<patch.Columns&&z+1<patch.Rows&&(patch.Bake.Edges[i]&3)==3&&
                        (patch.Bake.Edges[i+1]&2)!=0&&(patch.Bake.Edges[i+patch.Columns]&1)!=0)
                    {Connect(patch.Nodes[i],patch.Nodes[i+patch.Columns+1]);Connect(patch.Nodes[i+1],patch.Nodes[i+patch.Columns]);}
                }
                foreach(var node in patch.Nodes)node.RefreshNormal();
                int nx=Mathf.Max(1,Mathf.RoundToInt(data.ChunkSize/patch.Step.x)),nz=Mathf.Max(1,Mathf.RoundToInt(data.ChunkSize/patch.Step.y));
                for(int z=0;z<patch.Rows-1;z+=nz) for(int x=0;x<patch.Columns-1;x+=nx)
                {
                    var chunk=new GameObject("Foam_"+patch.Key+"_"+x+"_"+z).AddComponent<FoamChunk>();
                    chunk.Initialize(patch,x,z,Mathf.Min(nx,patch.Columns-1-x),Mathf.Min(nz,patch.Rows-1-z),material);
                    patch.Chunks.Add(chunk);chunk.Rebuild();
                }
                patch.OwnershipDirty=true;patch.UpdateOwnership();
            }
            foreach(var node in _nodes)node.RefreshNormal();
        }
        static void Connect(FoamNode a,FoamNode b)
        {if(a.Limit<=0||b.Limit<=0||a==b)return;if(!a.Neighbours.Contains(b))a.Neighbours.Add(b);if(!b.Neighbours.Contains(a))b.Neighbours.Add(a);}
        public bool TryPatch(PaintSurface surface,Vector3 point,Vector3 normal,out FoamPatch patch)
        {
            patch=null;float best=float.PositiveInfinity;
            foreach(var r in surface.CachedRegions)
            {
                if(!_patches.TryGetValue(r.Key(surface),out var p) || !p.Sample(point,out var top,out _,out _))continue;
                float distance=Mathf.Abs(top.y-point.y);
                // Raised side hits are resolved by their containing volume; static walls must remain planar ink.
                bool inside=point.y>=p.BasePoint(point).y-.025f && point.y<=top.y+.06f && top.y>p.BasePoint(point).y+.001f;
                if((distance<=.065f && normal.y>.45f || inside) && distance<best){best=distance;patch=p;}
            }
            return patch!=null;
        }
        public bool Queue(PaintSurface surface,PaintStamp stamp,float volume)
        {
            if(!TryPatch(surface,stamp.Position,stamp.Normal,out var patch))return false;
            if(volume>0 && float.IsFinite(volume))_requests.Add(new Request {Patch=patch,Stamp=stamp,Volume=volume});
            return true;
        }
        public bool IsManaged(PaintSurface surface,PaintRegion region)=>_patches.ContainsKey(region.Key(surface));
        void Touch(FoamNode node){_active.Add(node.Id);}
        void Apply(Request request)
        {
            var lobes = new FoamLobes(request.Stamp.ShapeSeed);
            _candidatePatches.Clear();_visited.Clear();_candidatePatches.Add(request.Patch);_visited.Add(request.Patch.Key);
            float reach=request.Stamp.Radius*Mathf.Max(1,request.Stamp.DepthScale)*2;
            for(int index=0;index<_candidatePatches.Count;index++)
                foreach(var p in _candidatePatches[index].Neighbours)
                    if(p.Contains(request.Stamp.Position,reach) && _visited.Add(p.Key))_candidatePatches.Add(p);
            _visited.Clear();_weights.Clear();double total=0;
            foreach(var p in _candidatePatches)
            {
                var stamp=request.Stamp;stamp.Position=p.BasePoint(stamp.Position);stamp.Normal=p.Normal;
                var brush=new InkShapeAtlas.Brush(stamp);var center=p.Local(stamp.Position);var extent=brush.LocalExtents(p.Inverse);var step=p.Step;
                int minX=Mathf.Max(0,Mathf.FloorToInt((center.x-extent.x+p.Bake.Size.x*.5f)/step.x));
                int maxX=Mathf.Min(p.Columns-1,Mathf.CeilToInt((center.x+extent.x+p.Bake.Size.x*.5f)/step.x));
                int minZ=Mathf.Max(0,Mathf.FloorToInt((center.z-extent.y+p.Bake.Size.y*.5f)/step.y));
                int maxZ=Mathf.Min(p.Rows-1,Mathf.CeilToInt((center.z+extent.y+p.Bake.Size.y*.5f)/step.y));
                for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
                {
                    var node=p.Nodes[z*p.Columns+x];if(node.Limit<=0||!_visited.Add(node.Id))continue;
                    float coverage=brush.Coverage(node.Base);if(coverage<=0)continue;
                    var local=p.Inverse.MultiplyVector(node.Base-stamp.Position);
                    float d=local.x*local.x/Mathf.Max(.0001f,extent.x*extent.x)+local.z*local.z/Mathf.Max(.0001f,extent.y*extent.y);
                    float weight=lobes.Weight(new Vector2(local.x/Mathf.Max(.0001f,extent.x),local.z/Mathf.Max(.0001f,extent.y)))*coverage;if(weight<=0)continue;
                    // Reject disconnected footprints across fixed obstacles using the baked edge graph below.
                    _weights.Add((node,weight));total+=weight*node.Area;
                }
            }
            if(total<=0)return;
            // Restrict the footprint to the connected side of baked walls and blocked cells.
            _visited.Clear();FoamNode seed=null;float nearest=float.PositiveInfinity;
            foreach(var item in _weights){_visited.Add(item.node.Id);float d=(item.node.Base-request.Patch.BasePoint(request.Stamp.Position)).sqrMagnitude;if(d<nearest){nearest=d;seed=item.node;}}
            _reachable.Clear();_flood.Clear();_flood.Add(seed);_reachable.Add(seed.Id);
            for(int i=0;i<_flood.Count;i++)foreach(var n in _flood[i].Neighbours)if(_visited.Contains(n.Id)&&_reachable.Add(n.Id))_flood.Add(n);
            total=0;foreach(var item in _weights)if(_reachable.Contains(item.node.Id))total+=item.weight*item.node.Area;
            foreach(var item in _weights)
            {
                if(!_reachable.Contains(item.node.Id))continue;
                var node=item.node;float limit=LimitGrowth!=null?Mathf.Min(node.Limit,LimitGrowth(node.Base,node.PendingHeight)):node.Limit;
                FoamRules.Deposit(ref node.PendingHeight,ref node.PendingOwner,request.Stamp.Team,(float)(request.Volume*item.weight/total),_dissolve,limit);
                Touch(node);_roundNodes.Add(node.Id);
            }
        }
        void RoundDeposits()
        {
            if(_roundNodes.Count==0)return;
            _roundWork.Clear();foreach(int id in _roundNodes)_roundWork.Add(id);
            int deposited=_roundWork.Count;
            for(int i=0;i<deposited;i++)foreach(var n in _nodes[_roundWork[i]].Neighbours)
                if(_roundNodes.Add(n.Id))_roundWork.Add(n.Id);
            _roundWork.Sort(NodeOrder);
            // Bounded pairwise exchange conserves volume even when node areas differ.
            // Smooth thickness, preserving a ramp's underlying slope. Never seed empty/enemy cells.
            for(int pass=0;pass<2;pass++)
            {
                RoundingPassCount++;
                foreach(int id in _roundWork)
                {
                    var a=_nodes[id];if(a.PendingHeight<=0)continue;
                    foreach(var b in a.Neighbours)
                    {
                        if(b.Id<=id||!_roundNodes.Contains(b.Id)||b.PendingHeight<=0||a.PendingOwner!=b.PendingOwner)continue;
                        var high=a.PendingHeight>b.PendingHeight?a:b;var low=high==a?b:a;
                        float difference=high.PendingHeight-low.PendingHeight;if(difference<=.001f)continue;
                        float limit=LimitGrowth!=null?Mathf.Min(low.Limit,LimitGrowth(low.Base,low.PendingHeight)):low.Limit;
                        float volume=Mathf.Min(.25f*difference/(1/high.Area+1/low.Area),Mathf.Max(0,limit-low.PendingHeight)*low.Area);
                        volume=Mathf.Min(volume,high.PendingHeight*high.Area);
                        if(volume<=.0000001f)continue;
                        high.PendingHeight-=volume/high.Area;low.PendingHeight+=volume/low.Area;Touch(high);Touch(low);
                    }
                }
            }
            _roundNodes.Clear();_roundWork.Clear();
        }
        void Relax()
        {
            for(int pass=0;pass<4;pass++)
            {
                _work.Clear();foreach(int id in _active)_work.Add(id);_work.Sort(NodeOrder);
                foreach(int id in _work)
                {
                    var a=_nodes[id];if(a.PendingHeight<=0)continue;
                    foreach(var b in a.Neighbours)
                    {
                        if(b.PendingOwner!=0&&b.PendingOwner!=a.PendingOwner)continue;
                        float horizontal=new Vector2(a.Base.x-b.Base.x,a.Base.z-b.Base.z).magnitude;
                        float excess=a.Base.y+a.PendingHeight-b.Base.y-b.PendingHeight-horizontal*_slope;
                        if(excess<=.001f)continue;
                        float limit=LimitGrowth!=null?Mathf.Min(b.Limit,LimitGrowth(b.Base,b.PendingHeight)):b.Limit;
                        float volume=Mathf.Min(excess/(1/a.Area+1/b.Area),a.PendingHeight*a.Area);
                        volume=Mathf.Min(volume,Mathf.Max(0,limit-b.PendingHeight)*b.Area);
                        if(volume<=.000001f)continue;
                        b.PendingHeight+=volume/b.Area;b.PendingOwner=a.PendingOwner;a.PendingHeight-=volume/a.Area;Touch(b);
                    }
                }
            }
        }
        public byte[] Commit(bool copy=true)
        {
            long start=Clock();_metrics=default;_feedback.Clear();
            try{return CommitInternal(copy);}
            finally
            {
                LastCommitMilliseconds=Elapsed(start);CommitCount++;
                _metrics.TotalMs=LastCommitMilliseconds;_metrics.Revision=Revision;LastMetrics=_metrics;
                Updated?.Invoke(_metrics);
            }
        }
        byte[] CommitInternal(bool copy)
        {
            using var marker=CommitMarker.Auto();LastDirtyChunks=0;
            long stage=Clock();using(DepositMarker.Auto()){foreach(var request in _requests)Apply(request);_requests.Clear();}_metrics.DepositMs=Elapsed(stage);
            if(_active.Count==0)return null;
            stage=Clock();using(RoundMarker.Auto())RoundDeposits();_metrics.RoundMs=Elapsed(stage);
            stage=Clock();using(RelaxMarker.Auto())Relax();_metrics.RelaxMs=Elapsed(stage);
            _changed.Clear();_work.Clear();foreach(int id in _active)_work.Add(id);_work.Sort(NodeOrder);_active.Clear();
            foreach(int id in _work)
            {
                var node=_nodes[id];ushort h=FoamRules.Quantize(Mathf.Clamp(node.PendingHeight,0,node.Limit));byte owner=h==0?(byte)0:node.PendingOwner;
                // Preserve sub-millimetre volume on the authority; quantization is only the committed surface.
                // Otherwise small auxiliary drops disappear every 50 ms and rounding creates/destroys volume.
                if(node.PendingHeight<=0)node.PendingOwner=0;
                if(node.HeightMm!=h||node.Owner!=owner)
                {
                    bool geometry=node.HeightMm!=h,ownership=node.Owner!=owner;
                    CollectFeedback(node,h,owner);node.HeightMm=h;node.Owner=owner;
                    _changed.Add(id);MarkGeometry(node,geometry,ownership);Touch(node);
                }
            }
            if(_changed.Count==0)return null;
            PublishGeometry();Revision++;PublishFeedback();
            _deltaStream.SetLength(0);_deltaWriter.Write(_changed.Count);
            for(int n=0;n<_changed.Count;)
            {
                int first=_changed[n],count=1;while(n+count<_changed.Count&&_changed[n+count]==first+count&&count<ushort.MaxValue)count++;
                _deltaWriter.Write(first);_deltaWriter.Write((ushort)count);
                for(int k=0;k<count;k++){var node=_nodes[first+k];_deltaWriter.Write(node.HeightMm);_deltaWriter.Write(node.Owner);}n+=count;
            }
            LastDeltaBytes=(int)_deltaStream.Length;return copy?_deltaStream.ToArray():_deltaStream.GetBuffer();
        }
        void MarkNormal(FoamNode node)
        {
            if(!_dirtyNormals.Add(node.Id))return;
            foreach(var chunk in node.Chunks){chunk.NormalsDirty=true;_dirtyChunks.Add(chunk);}
        }
        void MarkGeometry(FoamNode node,bool geometry=true,bool ownership=true)
        {
            if(geometry)
            {
                foreach(var chunk in node.Chunks){chunk.Dirty=true;_dirtyChunks.Add(chunk);}
                MarkNormal(node);foreach(var neighbour in node.Neighbours)MarkNormal(neighbour);
            }
            if(ownership)foreach(var patch in node.Patches){patch.MarkOwnership(node.Base);_dirtyPatches.Add(patch);}
        }
        void CollectFeedback(FoamNode node,ushort height,byte owner)
        {
            int delta=height-node.HeightMm;
            if(!PresentationEnabled||SurfaceChanged==null||Math.Abs(delta)<10||node.Chunks.Count==0)return;
            // Shared boundary nodes belong to one feedback chunk, avoiding duplicate bursts.
            var chunk=node.Chunks[0];_feedback.TryGetValue(chunk,out var change);
            change.Chunk=chunk;
            float weight=Math.Abs(delta)*node.Area;
            if(delta>0){change.GrowthPosition+=(node.Base+Vector3.up*(height*.001f))*weight;change.GrowthWeight+=weight;change.GrowthTeam=owner;}
            else {change.LossPosition+=node.Top*weight;change.LossWeight+=weight;change.LossTeam=node.Owner;}
            _feedback[chunk]=change;
        }
        void PublishFeedback()
        {
            long start=Clock();
            foreach(var pair in _feedback)
            {
                var c=pair.Value;
                if(c.GrowthWeight>0)c.GrowthPosition/=c.GrowthWeight;
                if(c.LossWeight>0)c.LossPosition/=c.LossWeight;
                SurfaceChanged?.Invoke(c);
            }
            _feedback.Clear();_metrics.FeedbackMs=Elapsed(start);
        }
        void PublishGeometry()
        {
            using var marker=MeshMarker.Auto();LastDirtyChunks=_dirtyChunks.Count;
            foreach(int id in _dirtyNormals)_nodes[id].RefreshNormal();_dirtyNormals.Clear();
            long stage=Clock();using(OwnerMarker.Auto())foreach(var patch in _dirtyPatches){patch.UpdateOwnership();_metrics.OwnerUploads+=patch.OwnerTexture!=null?1:0;}
            _metrics.OwnershipMs=Elapsed(stage);
            foreach(var chunk in _dirtyChunks)
            {
                chunk.Rebuild();_metrics.MeshMs+=chunk.LastMeshMs;_metrics.ColliderMs+=chunk.LastColliderMs;
                if(chunk.RebuiltGeometry)_metrics.ColliderRebuilds++;else _metrics.NormalOnlyUpdates++;
            }
            _metrics.DirtyChunks=LastDirtyChunks;
            _dirtyPatches.Clear();_dirtyChunks.Clear();if(_metrics.ColliderRebuilds>0)Physics.SyncTransforms();
        }
        public byte[] Capture()
        {
            var bytes=new byte[_nodes.Count*3];
            for(int i=0;i<_nodes.Count;i++){var n=_nodes[i];bytes[i*3]=(byte)n.HeightMm;bytes[i*3+1]=(byte)(n.HeightMm>>8);bytes[i*3+2]=n.Owner;}return bytes;
        }
        void ValidateValue(int id,ushort height,byte team)
        {
            if(id<0||id>=_nodes.Count||team>2||(height==0)!=(team==0)||height>FoamRules.Quantize(_nodes[id].Limit))throw new InvalidDataException("泡沫高度、阵营或拓扑无效");
        }
        public void ValidateSnapshot(byte[] bytes)
        {
            if(bytes==null||bytes.Length!=_nodes.Count*3)throw new InvalidDataException("泡沫快照尺寸不一致");
            for(int i=0;i<_nodes.Count;i++)ValidateValue(i,(ushort)(bytes[i*3]|bytes[i*3+1]<<8),bytes[i*3+2]);
        }
        public void Restore(byte[] bytes,uint revision)
        {
            ValidateSnapshot(bytes);_requests.Clear();_active.Clear();_roundNodes.Clear();_feedback.Clear();_metrics=default;SurfaceReset?.Invoke();
            for(int i=0;i<_nodes.Count;i++)SetNode(i,(ushort)(bytes[i*3]|bytes[i*3+1]<<8),bytes[i*3+2]);
            Revision=revision;PublishGeometry();
        }
        void SetNode(int id,ushort height,byte owner,bool feedback=false)
        {
            var n=_nodes[id];if(n.HeightMm!=height||n.Owner!=owner){bool geometry=n.HeightMm!=height,ownership=n.Owner!=owner;if(feedback)CollectFeedback(n,height,owner);n.HeightMm=height;n.Owner=owner;MarkGeometry(n,geometry,ownership);}
            n.PendingHeight=height*.001f;n.PendingOwner=owner;
        }
        ref struct DeltaReader
        {
            ReadOnlySpan<byte> _bytes;public int Position;
            public DeltaReader(byte[] bytes,int count){_bytes=bytes.AsSpan(0,count);Position=0;}
            public byte Byte(){if(Position>=_bytes.Length)throw new InvalidDataException("泡沫增量被截断");return _bytes[Position++];}
            public ushort Short(){uint a=Byte();return (ushort)(a|((uint)Byte()<<8));}
            public int Int(){uint a=Short();return (int)(a|((uint)Short()<<16));}
        }
        public void ValidateDelta(byte[] bytes,int length=-1)
        {
            int size=length<0?bytes.Length:length;var reader=new DeltaReader(bytes,size);
            int remaining=reader.Int(),last=-1;if(remaining<0||remaining>_nodes.Count)throw new InvalidDataException("泡沫增量数量无效");
            while(remaining>0)
            {
                int first=reader.Int(),count=reader.Short();
                if(first<=last||count==0||count>remaining||first<0||first>_nodes.Count-count)throw new InvalidDataException("泡沫增量索引无效");
                for(int i=0;i<count;i++)ValidateValue(first+i,reader.Short(),reader.Byte());
                remaining-=count;last=first+count-1;
            }
            if(reader.Position!=size)throw new InvalidDataException("泡沫增量尾部无效");
        }
        public void ApplyDelta(byte[] bytes,uint revision,int length=-1,bool present=true)
        {
            using var marker=InstallMarker.Auto();long start=Clock();_metrics=default;_metrics.Install=true;_feedback.Clear();
            if(revision!=Revision+1)throw new InvalidDataException("泡沫版本不连续");ValidateDelta(bytes,length);
            var reader=new DeltaReader(bytes,length<0?bytes.Length:length);int remaining=reader.Int();
            while(remaining>0){int first=reader.Int(),count=reader.Short();for(int i=0;i<count;i++)SetNode(first+i,reader.Short(),reader.Byte(),present);remaining-=count;}
            Revision=revision;PublishGeometry();if(present)PublishFeedback();
            _metrics.TotalMs=Elapsed(start);_metrics.Revision=Revision;LastMetrics=_metrics;Updated?.Invoke(_metrics);
        }
        public void Clear()
        {
            _requests.Clear();_active.Clear();_roundNodes.Clear();_feedback.Clear();_metrics=default;SurfaceReset?.Invoke();for(int i=0;i<_nodes.Count;i++)SetNode(i,0,0);Revision=0;PublishGeometry();
        }
        public uint StateHash()
        {uint hash=2166136261;foreach(var n in _nodes){hash=unchecked((hash^(byte)n.HeightMm)*16777619);hash=unchecked((hash^(byte)(n.HeightMm>>8))*16777619);hash=unchecked((hash^n.Owner)*16777619);}return hash;}
        public void Dispose()
        {
            foreach(var p in _patches.Values){foreach(var c in p.Chunks)if(c!=null){if(Application.isPlaying)UnityEngine.Object.Destroy(c.gameObject);else UnityEngine.Object.DestroyImmediate(c.gameObject);}p.Dispose();}
            SurfaceReset?.Invoke();SurfaceChanged=null;SurfaceReset=null;Updated=null;_deltaWriter.Dispose();_deltaStream.Dispose();
        }
    }
}
