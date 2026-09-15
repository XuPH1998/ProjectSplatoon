Shader "Splatoon/EnemyOutline"
{
    Properties
    {
        _OutlineColor("Enemy red", Color) = (1,.035,.025,1)
        _OutlineWidth("World width", Float) = .018
        _MaxPixels("Maximum pixel width", Float) = 2.5
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        // Reserve stencil bit 6 for the visible union of every enemy body part.
        // The hull cannot draw over any part of that original silhouette.
        Pass
        {
            Name "SilhouetteMask"
            Cull Back ZTest LEqual ZWrite Off ColorMask 0
            Stencil { Ref 64 ReadMask 64 WriteMask 64 Comp Always Pass Replace }
            HLSLPROGRAM
            #pragma vertex MaskVert
            #pragma fragment MaskFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 MaskVert(float4 positionOS : POSITION) : SV_POSITION { return TransformObjectToHClip(positionOS.xyz); }
            half4 MaskFrag() : SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "ExteriorHull"
            Cull Front ZTest LEqual ZWrite Off
            Stencil { Ref 64 ReadMask 64 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineWidth, _MaxPixels;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 world = TransformObjectToWorld(v.positionOS.xyz);
                float4 position = TransformWorldToHClip(world);
                float4 expanded = TransformWorldToHClip(world + TransformObjectToWorldNormal(v.normalOS) * _OutlineWidth);
                float2 delta = expanded.xy / max(.0001, expanded.w) - position.xy / max(.0001, position.w);
                float pixels = length(delta * _ScaledScreenParams.xy * .5);
                position.xy += delta * min(1, _MaxPixels / max(.0001, pixels)) * position.w;
                o.positionCS = position; return o;
            }
            half4 Frag(Varyings i) : SV_Target { return _OutlineColor; }
            ENDHLSL
        }
    }
}
