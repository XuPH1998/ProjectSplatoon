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
            sampler2D _MainTex;
            Texture2D<float4> _ShapeAtlas;
            float _PainterTeam;
            float3 _PainterPosition, _PainterNormal;
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
            float4 frag(Output i):SV_Target
            {
                if (_PrepareUV>0) return float4(0,0,1,1);
                float4 old=tex2D(_MainTex,i.uv);
                // Registered convex arena meshes have hard, disconnected faces in the atlas.
                if (dot(normalize(i.normalWS),normalize(_PainterNormal))<0.5) return old;
                float3 n=normalize(_PainterNormal); float3 axis=abs(n.y)>.5?float3(0,0,1):float3(0,1,0);
                float3 tangent=normalize(cross(n,axis)), bitangent=cross(tangent,n);
                float2 p=float2(dot(i.positionWS-_PainterPosition,tangent),dot(i.positionWS-_PainterPosition,bitangent))/max(.0001,_Radius);
                float c=_ShapeTransform.x,s=_ShapeTransform.y; p=float2((p.x*c-p.y*s)*_ShapeTransform.z,p.x*s+p.y*c)*.5+.5;
                int columns=(int)_ShapeLayout.x;
                float2 tile=float2(_ShapeIndex%columns,_ShapeIndex/columns); float alpha=0;
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
                }
                float h=max(.0001,saturate(_Hardness));
                float t=saturate((alpha-(1-h))/h);
                float f=t*t*(3-2*t)*saturate(_Strength);
                return InkAccumulate(old, _PainterTeam, saturate(f));
            }
            ENDHLSL
        }
    }
}
