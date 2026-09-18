using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Painting
{
    [DisallowMultipleComponent]
    public sealed class FoamChunk : MonoBehaviour
    {
        public FoamPatch Patch { get; private set; }
        public MeshCollider Collider { get; private set; }
        public Mesh Mesh { get; private set; }
        internal bool Dirty,NormalsDirty;
        public bool RebuiltGeometry { get; private set; }
        public double LastMeshMs { get; private set; }
        public double LastColliderMs { get; private set; }
        public double NextFeedbackTime;
        static readonly Unity.Profiling.ProfilerMarker MeshMarker=new("Splatoon.Foam.Mesh");
        static readonly Unity.Profiling.ProfilerMarker ColliderMarker=new("Splatoon.Foam.Collider");
        static double Ms(long t)=>(System.Diagnostics.Stopwatch.GetTimestamp()-t)*1000d/System.Diagnostics.Stopwatch.Frequency;
        readonly List<Vector3> _vertices = new(), _normals = new();
        readonly List<Color> _colors = new();
        readonly List<Vector2> _uv = new();
        readonly List<int> _indices = new();
        int _x0, _z0, _nx, _nz;
        MeshRenderer _renderer;
        MaterialPropertyBlock _properties;
        internal void Initialize(FoamPatch patch, int x, int z, int nx, int nz, Material material)
        {
            Patch = patch; _x0 = x; _z0 = z; _nx = nx; _nz = nz;
            transform.SetParent(patch.Surface.transform, false);
            Mesh = new Mesh { name = "Foam " + patch.Key + " " + x + ":" + z, indexFormat = IndexFormat.UInt32 };
            Mesh.MarkDynamic();
            Collider = gameObject.AddComponent<MeshCollider>();Collider.enabled=false;
            gameObject.AddComponent<MeshFilter>().sharedMesh = Mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>(); _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = ShadowCastingMode.On; _renderer.receiveShadows = true;
            Collider.cookingOptions = MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.UseFastMidphase;
            _properties = new MaterialPropertyBlock();
            if(patch.OwnerTexture!=null)_properties.SetTexture("_Owners", patch.OwnerTexture);
            _properties.SetVector("_Grid", new Vector4(patch.Columns, patch.Rows, 1f / patch.Columns, 1f / patch.Rows));
            _renderer.SetPropertyBlock(_properties);
            for (int j = z; j <= z+nz; j++) for (int i = x; i <= x+nx; i++)
                patch.Nodes[j * patch.Columns + i].Chunks.Add(this);
            Dirty = true;
        }
        internal void Rebuild()
        {
            LastMeshMs=LastColliderMs=0;RebuiltGeometry=false;
            if(!Dirty&&!NormalsDirty)return;
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            if(!Dirty)
            {
                using var normalMarker=MeshMarker.Auto();int index=0;
                var inverseNormals=transform.worldToLocalMatrix;
                for(int z=_z0;z<=_z0+_nz;z++)for(int x=_x0;x<=_x0+_nx;x++)
                    _normals[index++]=inverseNormals.MultiplyVector(Patch.Nodes[z*Patch.Columns+x].Normal).normalized;
                if(_indices.Count>0)Mesh.SetNormals(_normals);
                NormalsDirty=false;LastMeshMs=Ms(start);return;
            }
            using var meshMarker=MeshMarker.Auto();
            Dirty=NormalsDirty=false;RebuiltGeometry=true;
            _vertices.Clear(); _indices.Clear(); _normals.Clear(); _colors.Clear(); _uv.Clear();
            var inverse=transform.worldToLocalMatrix;
            for (int z = _z0; z <= _z0+_nz; z++) for (int x = _x0; x <= _x0+_nx; x++)
            {
                var node = Patch.Nodes[z*Patch.Columns+x];
                _vertices.Add(inverse.MultiplyPoint3x4(node.Top));
                _normals.Add(inverse.MultiplyVector(node.Normal).normalized);
                _colors.Add(new Color(1,1,1,node.Height));
                _uv.Add(new Vector2((float)x/(Patch.Columns-1), (float)z/(Patch.Rows-1)));
            }
            for (int z = 0; z < _nz; z++) for (int x = 0; x < _nx; x++)
            {
                int i = z*(_nx+1)+x, n = (_z0+z)*Patch.Columns+_x0+x;
                AddTop(i,i+_nx+1,i+1, n,n+Patch.Columns,n+1);
                AddTop(i+1,i+_nx+1,i+_nx+2,n+1,n+Patch.Columns,n+Patch.Columns+1);
            }
            // Boundary skirts close the height volume. Shared top vertices keep neighbouring chunks watertight.
            for (int x=0;x<_nx;x++) { if(_z0==0)Skirt(x,x+1); if(_z0+_nz==Patch.Rows-1)Skirt(_nz*(_nx+1)+x+1,_nz*(_nx+1)+x); }
            for (int z=0;z<_nz;z++) { if(_x0==0)Skirt((z+1)*(_nx+1),z*(_nx+1)); if(_x0+_nx==Patch.Columns-1)Skirt(z*(_nx+1)+_nx,(z+1)*(_nx+1)+_nx); }
            bool active = _indices.Count > 0;
            Collider.sharedMesh = null; Mesh.Clear();
            if (active)
            {
                Mesh.SetVertices(_vertices); Mesh.SetNormals(_normals); Mesh.SetColors(_colors); Mesh.SetUVs(0,_uv); Mesh.SetTriangles(_indices,0,true);
                LastMeshMs=Ms(start);
                long cook=System.Diagnostics.Stopwatch.GetTimestamp();
                using(ColliderMarker.Auto())Collider.sharedMesh=Mesh;
                LastColliderMs=Ms(cook);
            }
            if(!active)LastMeshMs=Ms(start);
            Collider.enabled = active; _renderer.enabled = active;
        }
        void AddTop(int a,int b,int c,int na,int nb,int nc)
        {
            var nodes=Patch.Nodes;
            if (nodes[na].Limit<=0 || nodes[nb].Limit<=0 || nodes[nc].Limit<=0 ||
                (nodes[na].HeightMm==0 && nodes[nb].HeightMm==0 && nodes[nc].HeightMm==0)) return;
            _indices.Add(a); _indices.Add(b); _indices.Add(c);
        }
        void Skirt(int a,int b)
        {
            if (_colors[a].a<=0 && _colors[b].a<=0) return;
            int start=_vertices.Count;
            var av=_vertices[a]; var bv=_vertices[b];
            var up=transform.InverseTransformVector(Vector3.up);
            var bottomA=av-up*_colors[a].a; var bottomB=bv-up*_colors[b].a;
            var normal=Vector3.Cross(bv-av,bottomA-av).normalized;
            if(normal.sqrMagnitude<.5f)normal=Vector3.Cross(bottomB-bv,av-bv).normalized;
            _vertices.Add(av);_vertices.Add(bv);_vertices.Add(bottomA);_vertices.Add(bottomB);
            for(int i=0;i<4;i++){_normals.Add(normal);_colors.Add(i%2==0?_colors[a]:_colors[b]);_uv.Add(i%2==0?_uv[a]:_uv[b]);}
            _indices.Add(start);_indices.Add(start+1);_indices.Add(start+2);
            _indices.Add(start+1);_indices.Add(start+3);_indices.Add(start+2);
        }
        // Non-convex MeshCollider.ClosestPoint is unsupported. Use the exact rendered triangles.
        internal bool ClosestPoint(Vector3 world,out Vector3 point,out Vector3 normal)
        {
            point=normal=default;float best=float.PositiveInfinity;
            for(int i=0;i<_indices.Count;i+=3)
            {
                var a=transform.TransformPoint(_vertices[_indices[i]]);var b=transform.TransformPoint(_vertices[_indices[i+1]]);var c=transform.TransformPoint(_vertices[_indices[i+2]]);
                var face=Vector3.Cross(b-a,c-a);if(face.sqrMagnitude<1e-12f)continue;
                var p=ClosestTriangle(world,a,b,c);float d=(p-world).sqrMagnitude;
                if(d<best){best=d;point=p;normal=face.normalized;}
            }
            return best<float.PositiveInfinity;
        }
        static Vector3 ClosestTriangle(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        {
            var ab=b-a;var ac=c-a;var ap=p-a;float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
            if(d1<=0&&d2<=0)return a;
            var bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);if(d3>=0&&d4<=d3)return b;
            float vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            var cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;
            float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
            float sum=va+vb+vc;if(Mathf.Abs(sum)<1e-12f)return a;
            return a+ab*(vb/sum)+ac*(vc/sum);
        }
        void OnDestroy() { if(Mesh!=null) { if(Application.isPlaying) Destroy(Mesh); else DestroyImmediate(Mesh); } }
    }
}
