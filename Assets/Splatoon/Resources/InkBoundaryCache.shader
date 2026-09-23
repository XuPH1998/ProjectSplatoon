Shader "Hidden/Splatoon/InkBoundaryCache"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "../Runtime/Painting/InkCoverage.hlsl"
        Texture2D<float4> _MainTex, _Field, _State;
        Texture2D<float> _Occupancy;
        float4 _SourceTexel, _SourceRect;
        float4 _Size, _Region, _AtlasSize, _Metric;
        float4 _Origin, _DualU, _DualV, _Normal;
        float4x4 _Fold;
        float _Jump, _Range, _Threshold, _WorldScale, _NoiseScale, _EncodeUNorm;
        struct App { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
        struct Var { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float3 world:TEXCOORD1; };
        Var Quad(App v) { Var o; o.position=float4(v.vertex.xy,0,1); o.uv=v.uv; o.world=0; return o; }
        Var Geometry(App v)
        {
            Var o; o.world=v.vertex.xyz; o.uv=v.uv;
            float3 folded=mul(_Fold,v.vertex).xyz-_Origin.xyz;
            float2 at=float2(dot(folded,_DualU.xyz),dot(folded,_DualV.xyz));
            o.position=float4(((at*_AtlasSize.xy-_Region.xy)/_Size.xy)*2-1,0,1);
            #if UNITY_UV_STARTS_AT_TOP
                o.position.y=-o.position.y;
            #endif
            return o;
        }
        float4 Fill(Var i):SV_Target
        {
            float4 s=_State.SampleLevel(sampler_LinearClamp,i.uv,0);
            float owner=round(_State.SampleLevel(sampler_PointClamp,i.uv,0).b*255);
            // Folded neighbours need not land on their own texel centres. Normalize
            // away UV padding, otherwise weak ink creates a false bare stripe at a seam.
            float occupancy=_Occupancy.SampleLevel(sampler_LinearClamp,i.uv,0);
            s.a=saturate(s.a/max(occupancy,1e-5));
            if(_Occupancy.SampleLevel(sampler_PointClamp,i.uv,0)<.99)
            {
                float best=1e10;
                [unroll] for(int y=-1;y<=1;y++) [unroll] for(int x=-1;x<=1;x++)
                {
                    float2 at=i.uv+float2(x,y)*_SourceTexel.xy;
                    if(any(at<_SourceRect.xy) || any(at>_SourceRect.zw))continue;
                    float d=x*x+y*y;
                    if(d<best && _Occupancy.SampleLevel(sampler_PointClamp,at,0)>.99)
                    { owner=round(_State.SampleLevel(sampler_PointClamp,at,0).b*255);best=d; }
                }
            }
            return float4(s.a*(1+.5*InkNoise(InkDetailUV(i.world,_Normal.xyz,_WorldScale),_NoiseScale)),owner,s.a,1);
        }
        bool Inside(int2 p) { return all(p>=0)&&all(p<(int2)_Size.xy); }
        float4 Field(int2 p) { return Inside(p)?_Field.Load(int3(p,0)):0; }
        float Distance(float2 delta) { return sqrt(max(0,delta.x*delta.x*_Metric.x+2*delta.x*delta.y*_Metric.y+delta.y*delta.y*_Metric.z)); }
        float4 Seed(Var i):SV_Target
        {
            int2 p=(int2)i.position.xy; float4 c=Field(p); float4 seeds=-1; float2 best=1e10;
            if(c.a<.5) return seeds;
            const int2 offsets[4]={int2(-1,0),int2(1,0),int2(0,-1),int2(0,1)};
            [unroll] for(int k=0;k<4;k++)
            {
                int2 offset=offsets[k]; float4 b=Field(p+offset); if(b.a<.5)continue;
                if((c.r>=_Threshold)!=(b.r>=_Threshold))
                {
                    float fraction=saturate((_Threshold-c.r)/(b.r-c.r)); float2 at=p+offset*fraction;
                    float d=Distance(at-p); if(d<best.x) { best.x=d; seeds.xy=at; }
                }
                if(c.r>=_Threshold && b.r>=_Threshold && c.g>0 && b.g>0 && c.g!=b.g)
                {
                    float2 at=p+offset*.5; float d=Distance(at-p);
                    if(d<best.y) { best.y=d; seeds.zw=at; }
                }
            }
            return seeds;
        }
        float4 Jump(Var i):SV_Target
        {
            int2 p=(int2)i.position.xy; float4 result=_MainTex.Load(int3(p,0));
            float2 best=float2(result.x<0?1e10:Distance(result.xy-p),result.z<0?1e10:Distance(result.zw-p));
            [unroll] for(int y=-1;y<=1;y++) [unroll] for(int x=-1;x<=1;x++)
            {
                int2 at=p+int2(x,y)*(int)_Jump; if(!Inside(at))continue;
                float4 candidate=_MainTex.Load(int3(at,0));
                float2 d=float2(candidate.x<0?1e10:Distance(candidate.xy-p),candidate.z<0?1e10:Distance(candidate.zw-p));
                if(d.x<best.x) { best.x=d.x; result.xy=candidate.xy; }
                if(d.y<best.y) { best.y=d.y; result.zw=candidate.zw; }
            }
            return result;
        }
        float4 Resolve(Var i):SV_Target
        {
            int2 p=(int2)i.position.xy; float4 c=Field(p);
            if(c.a<.5)
            {
                float best=1e10; int2 pick=p;
                [unroll] for(int y=-2;y<=2;y++) [unroll] for(int x=-2;x<=2;x++)
                {
                    int2 at=p+int2(x,y); float d=x*x+y*y;
                    if(Field(at).a>.5 && d<best) { best=d; pick=at; }
                }
                p=pick; c=Field(p);
            }
            float4 seed=_MainTex.Load(int3(p,0));
            float2 distance=float2(seed.x<0?_Range:Distance(seed.xy-p),seed.z<0?_Range:Distance(seed.zw-p));
            distance=min(distance,_Range)*float2(c.r>=_Threshold?1:-1,c.g==2?-1:1);
            // Signed half values retain precision around the actual zero contour.
            return float4(_EncodeUNorm>.5?distance/(2*_Range)+.5:distance,0,1);
        }
        ENDHLSL
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Geometry
            #pragma fragment Fill
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Quad
            #pragma fragment Seed
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Quad
            #pragma fragment Jump
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Quad
            #pragma fragment Resolve
            ENDHLSL
        }
    }
}
