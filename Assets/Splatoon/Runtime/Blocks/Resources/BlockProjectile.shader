Shader "Splatoon/BlockProjectile"
{
    Properties { _TeamColor("Team color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            ZWrite On Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _TeamColor;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; half3 normalWS:TEXCOORD0; float2 uv:TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output; UNITY_SETUP_INSTANCE_ID(input); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS=TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS=TransformObjectToWorldNormal(input.normalOS); output.uv=input.uv; return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                half3 n=normalize(input.normalWS); Light light=GetMainLight();
                half3 illumination=max(SampleSH(n)+light.color*(saturate(dot(n,light.direction))*.8+.2),.35);
                float2 edge=min(input.uv,1-input.uv); half border=smoothstep(.025,.07,min(edge.x,edge.y));
                return half4(_TeamColor.rgb*illumination*lerp(.65,1,border),1);
            }
            ENDHLSL
        }
    }
}
