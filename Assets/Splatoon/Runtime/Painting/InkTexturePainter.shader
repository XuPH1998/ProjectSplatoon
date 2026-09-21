// Dedicated UV1 brush, derived from the Mix and Jam/TNTC world-space painter.
Shader "Splatoon/InkTexturePainter"
{
    Properties
    {
        [NoScaleOffset] _ShapeAtlas("Ink Shape Atlas", 2D) = "black" {}
        _ShapeIndex("Shape index", Integer) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "InkCoverage.hlsl"
            Texture2D<float4> _MainTex;
            Texture2D<float4> _ShapeAtlas;
            Texture2D<float4> _DetailAtlas, _VisualTex;
            float _PainterTeam;
            float3 _PainterPosition, _PainterNormal, _PainterDirection;
            float _DepthScale, _ClipEnabled;
            float4 _Clip0, _Clip1;
            float4 _PainterColor;
            float _Radius, _Hardness, _Strength, _Threshold, _PrepareUV;
            float4 _ShapeTransform, _ShapeLayout;
            int _ShapeIndex;
            struct Input { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 paintUV:TEXCOORD1; };
            struct Output { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; float3 positionWS:TEXCOORD1; float3 normalWS:TEXCOORD2; };
            Output vert(Input v)
            {
                Output o; o.positionWS=TransformObjectToWorld(v.positionOS.xyz); o.normalWS=TransformObjectToWorldNormal(v.normalOS);
                o.uv=v.paintUV; o.positionCS=float4(v.paintUV*2-1,0,1);
                #if UNITY_UV_STARTS_AT_TOP
                    o.positionCS.y=-o.positionCS.y;
                #endif
                return o;
            }
            struct PaintOutput { float4 coverage:SV_Target0; float4 visual:SV_Target1; };
            PaintOutput frag(Output i)
            {
                PaintOutput result;
                result.coverage=float4(1,0,0,1); result.visual=0;
                if (_PrepareUV>0) return result;
                float4 old=_MainTex.Sample(sampler_LinearClamp,i.uv);
                result.coverage=old; result.visual=_VisualTex.Sample(sampler_LinearClamp,i.uv);
                // Registered convex arena meshes have hard, disconnected faces in the atlas.
                if (dot(normalize(i.normalWS),normalize(_PainterNormal))<0.5) return result;
                float3 n=normalize(_PainterNormal); float3 axis=abs(n.y)>.5?float3(0,0,1):float3(0,1,0);
                float3 tangent=normalize(cross(n,axis)), bitangent=cross(tangent,n);
                float3 projected=_PainterDirection-n*dot(_PainterDirection,n);
                if(dot(projected,projected)>1e-8) { bitangent=normalize(projected); tangent=normalize(cross(n,bitangent)); }
                float2 plane=float2(dot(i.positionWS-_PainterPosition,tangent),dot(i.positionWS-_PainterPosition,bitangent));
                if(_ClipEnabled>0) {
                    float angle=atan2(plane.y,plane.x); if(angle<0) angle+=6.28318530718;
                    int a=((int)floor(angle*1.27323954474))%8, b=(a+1)%8;
                    float ra=a<4?_Clip0[a]:_Clip1[a-4], rb=b<4?_Clip0[b]:_Clip1[b-4];
                    if(length(plane)>min(ra,rb)+.00001) return result;
                }
                float2 p=float2(dot(i.positionWS-_PainterPosition,tangent),dot(i.positionWS-_PainterPosition,bitangent)/(_DepthScale>0?_DepthScale:1))/max(.0001,_Radius);
                float c=_ShapeTransform.x,s=_ShapeTransform.y; p=float2((p.x*c-p.y*s)*_ShapeTransform.z,p.x*s+p.y*c)*.5+.5;
                int columns=(int)_ShapeLayout.x;
                float2 tile=float2(_ShapeIndex%columns,_ShapeIndex/columns); float alpha=0; float2 detail=0;
                if(all(p>=0)&&all(p<=1))
                {
                    // Explicit bilinear weights match the CPU lookup. Hardware filtering
                    // quantizes its weights and amplifies edge differences at low hardness.
                    float2 pixel=clamp(p*_ShapeLayout.z-.5,0,_ShapeLayout.z-1);
                    int2 lo=(int2)floor(pixel), hi=min(lo+1,(int)_ShapeLayout.z-1);
                    int2 origin=(int2)tile*(int)_ShapeLayout.z;
                    float2 weight=frac(pixel);
                    float a=lerp(_ShapeAtlas.Load(int3(origin+lo,0)).a,
                                 _ShapeAtlas.Load(int3(origin+int2(hi.x,lo.y),0)).a,weight.x);
                    float b=lerp(_ShapeAtlas.Load(int3(origin+int2(lo.x,hi.y),0)).a,
                                 _ShapeAtlas.Load(int3(origin+hi,0)).a,weight.x);
                    alpha=lerp(a,b,weight.y);
                    detail=lerp(lerp(_DetailAtlas.Load(int3(origin+lo,0)).rg,_DetailAtlas.Load(int3(origin+int2(hi.x,lo.y),0)).rg,weight.x),
                                lerp(_DetailAtlas.Load(int3(origin+int2(lo.x,hi.y),0)).rg,_DetailAtlas.Load(int3(origin+hi,0)).rg,weight.x),weight.y);
                }
                float h=max(.0001,saturate(_Hardness));
                float t=saturate((alpha-(1-h))/h);
                float f=t*t*(3-2*t)*saturate(_Strength);
                result.coverage=InkAccumulate(old, _PainterTeam, saturate(f));
                result.visual=float4(min(floor(saturate(result.visual.rg*(1-f)+detail*f)*255+.5)/255,result.coverage.aa),0,0);
                return result;
            }
            ENDHLSL
        }
    }
}
