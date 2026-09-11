// Dedicated UV1 brush, derived from the Mix and Jam/TNTC world-space painter.
Shader "Splatoon/InkTexturePainter"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            sampler2D _MainTex;
            float3 _PainterPosition, _PainterNormal;
            float4 _PainterColor;
            float _Radius, _Hardness, _Strength, _Threshold, _PrepareUV;
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
                float d=distance(i.positionWS,_PainterPosition);
                float f=(1-smoothstep(_Radius*_Hardness,max(_Radius*_Hardness+0.000001,_Radius),d))*_Strength;
                if (f<_Threshold) return old;
                return float4(_PainterColor.rgb,1);
            }
            ENDHLSL
        }
    }
}
