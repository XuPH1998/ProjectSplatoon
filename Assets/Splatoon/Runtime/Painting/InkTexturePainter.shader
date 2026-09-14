// Dedicated UV1 brush, derived from the Mix and Jam/TNTC world-space painter.
Shader "Splatoon/InkTexturePainter"
{
    Properties
    {
        [NoScaleOffset] _ShapeAtlas("Ink Shape Atlas", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "InkCoverage.hlsl"
            sampler2D _MainTex, _ShapeAtlas;
            float _PainterTeam;
            float3 _PainterPosition, _PainterNormal;
            float4 _PainterColor;
            float _Radius, _Hardness, _Strength, _Threshold, _PrepareUV, _ShapeRotation;
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
                float c=cos(_ShapeRotation),s=sin(_ShapeRotation); p=float2(p.x*c-p.y*s,p.x*s+p.y*c)*.5+.5;
                float2 tile=float2(_ShapeIndex%4,_ShapeIndex/4); float alpha=0;
                if(all(p>=0)&&all(p<=1)) alpha=tex2D(_ShapeAtlas,(tile+p)/4).a;
                float f=smoothstep(0,1,max(0.0001,alpha-(1-_Hardness)))*_Strength;
                return InkAccumulate(old, _PainterTeam, saturate(f));
            }
            ENDHLSL
        }
    }
}
