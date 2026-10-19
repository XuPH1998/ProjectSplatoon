Shader "Splatoon/BlockSurfacePaint"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry+1" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            ZWrite Off ZTest LEqual Cull Back Offset -1, -1
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            struct Attributes { float3 positionOS:POSITION; float3 normalOS:NORMAL; float2 uv:TEXCOORD0; half4 color:COLOR; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normalWS:TEXCOORD1; float2 uv:TEXCOORD2; half4 color:COLOR; };
            Varyings Vert(Attributes input)
            {
                Varyings output; output.positionWS=TransformObjectToWorld(input.positionOS);
                output.positionCS=TransformWorldToHClip(output.positionWS); output.normalWS=TransformObjectToWorldNormal(input.normalOS);
                output.uv=input.uv; output.color=input.color; return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                half3 n=normalize(input.normalWS); float2 edge=min(input.uv,1-input.uv);
                float width=max(.025,max(fwidth(input.uv.x),fwidth(input.uv.y))*.65);
                half seam=1-smoothstep(width,width*1.8,min(edge.x,edge.y));
                Light light=GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 illumination=SampleSH(n)+light.color*(saturate(dot(n,light.direction))*.8+.15)*light.shadowAttenuation;
                return half4(input.color.rgb*max(illumination,.25)*(1-seam*.28),1);
            }
            ENDHLSL
        }
    }
}
