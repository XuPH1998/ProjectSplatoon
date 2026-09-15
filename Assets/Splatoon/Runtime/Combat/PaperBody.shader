Shader "Splatoon/PaperBody"
{
    Properties
    {
        _BaseMap("Character", 2D) = "white" {}
        _BaseColor("Team accent", Color) = (1,1,1,1)
        _Cutoff("Alpha cutoff", Range(0,1)) = .5
        _EnemyOutline("Enemy outline", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off ZWrite On
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST, _BaseColor, _BaseMap_TexelSize;
        float _Cutoff, _EnemyOutline;
        CBUFFER_END
        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
        Varyings Vert(Attributes v)
        {
            Varyings o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
            o.uv = TRANSFORM_TEX(v.uv, _BaseMap); return o;
        }
        ENDHLSL
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv); clip(c.a - _Cutoff);
                float2 t = _BaseMap_TexelSize.xy * 2;
                half edge = min(min(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv+float2(t.x,0)).a,
                    SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv-float2(t.x,0)).a),
                    min(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv+float2(0,t.y)).a,
                    SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv-float2(0,t.y)).a));
                half3 edgeColor = lerp(_BaseColor.rgb, half3(1,.035,.025), _EnemyOutline);
                c.rgb = lerp(c.rgb, edgeColor, (1-edge) * lerp(.65 + .08*sin(_Time.y*3), 1, _EnemyOutline));
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode"="DepthOnly" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Depth
            half4 Depth(Varyings i) : SV_Target { clip(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).a-_Cutoff); return 0; }
            ENDHLSL
        }
    }
}
