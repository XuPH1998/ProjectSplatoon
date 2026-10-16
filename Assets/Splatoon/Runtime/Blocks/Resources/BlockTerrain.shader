Shader "Splatoon/Blocks"
{
    Properties { _TeamColor("Team color", Color) = (1,1,1,1) _GridWidth("Block edge width", Range(0,0.1)) = 0.035 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float _GridWidth;
            CBUFFER_END
            UNITY_INSTANCING_BUFFER_START(PerBlock)
            UNITY_DEFINE_INSTANCED_PROP(float4, _TeamColor)
            UNITY_INSTANCING_BUFFER_END(PerBlock)
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; half4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normalWS:TEXCOORD1; half4 color:TEXCOORD2; UNITY_VERTEX_INPUT_INSTANCE_ID };
            Varyings Vert(Attributes input)
            {
                Varyings output; UNITY_SETUP_INSTANCE_ID(input); UNITY_TRANSFER_INSTANCE_ID(input,output);
                VertexPositionInputs p=GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS=p.positionCS; output.positionWS=p.positionWS;
                output.normalWS=TransformObjectToWorldNormal(input.normalOS); output.color=input.color; return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 n=normalize(input.normalWS); float3 grid=input.positionWS*10;
                float3 edge=abs(frac(grid+.0001)-.5); float2 uv=abs(n.y)>.5 ? grid.xz : abs(n.x)>.5 ? grid.yz : grid.xy;
                float2 distanceToEdge=min(frac(uv+.0001),1-frac(uv+.0001));
                float width=max(_GridWidth,max(fwidth(uv.x),fwidth(uv.y))*.65);
                half seam=1-smoothstep(width,width*1.8,min(distanceToEdge.x,distanceToEdge.y));
                half3 color=input.color.rgb*UNITY_ACCESS_INSTANCED_PROP(PerBlock,_TeamColor).rgb;
                Light light=GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 illumination=SampleSH(n)+light.color*(saturate(dot(n,light.direction))*.8+.15)*light.shadowAttenuation;
                return half4(color*max(illumination,.25)*(1-seam*.28),1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; };
            Varyings ShadowVert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input); Varyings output;
                float3 p=TransformObjectToWorld(input.positionOS.xyz), n=TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 direction=normalize(_LightPosition-p);
                #else
                float3 direction=_LightDirection;
                #endif
                output.positionCS=TransformWorldToHClip(ApplyShadowBias(p,n,direction));
                #if UNITY_REVERSED_Z
                output.positionCS.z=min(output.positionCS.z,UNITY_NEAR_CLIP_VALUE*output.positionCS.w);
                #else
                output.positionCS.z=max(output.positionCS.z,UNITY_NEAR_CLIP_VALUE*output.positionCS.w);
                #endif
                return output;
            }
            half4 ShadowFrag(Varyings input):SV_Target { return 0; }
            ENDHLSL
        }
    }
}
