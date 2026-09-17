Shader "Splatoon/BubbleInk"
{
    Properties { _BaseColor("Team ink", Color) = (0.1,0.7,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; };
            Varyings Vert(Attributes v)
            {
                Varyings o; o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS); o.normalWS = TransformObjectToWorldNormal(v.normalOS); return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                half3 n = normalize(i.normalWS), view = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(1 - saturate(dot(n,view)), 3);
                half light = saturate(dot(n,normalize(half3(-.4,.8,-.3))));
                half shine = pow(saturate(dot(n,normalize(view + half3(-.4,.8,-.3)))), 42);
                return half4(_BaseColor.rgb * (.65 + light * .35) + rim * .35 + shine * .55, 1);
            }
            ENDHLSL
        }
    }
}
