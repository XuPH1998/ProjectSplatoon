Shader "Splatoon/SubWeaponEffect"
{
    Properties { _BaseColor("Tint",Color)=(1,1,1,1) _Soft("Soft particle",Float)=0 _Flow("Curtain flow",Float)=0 _Phase("Effect age",Float)=0 _EffectCenter("Effect center",Vector)=(0,0,0,0) _EffectRadius("Effect radius",Float)=0 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float _Soft, _Flow, _Phase, _EffectRadius;
            float4 _EffectCenter;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION;float2 uv:TEXCOORD0;half4 color:COLOR; };
            struct Varyings { float3 positionWS:TEXCOORD1;float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;half4 color:COLOR; };
            Varyings vert(Attributes v) { Varyings o;o.positionWS=TransformObjectToWorld(v.positionOS.xyz);o.positionCS=TransformWorldToHClip(o.positionWS);o.uv=v.uv;o.color=v.color*_BaseColor;return o; }
            half4 frag(Varyings i):SV_Target { half4 c=i.color;float fade=saturate(1-length(i.uv*2-1));c.a*=lerp(1,fade*fade,saturate(_Soft));if(_EffectRadius>0)c.a*=saturate((_EffectRadius-distance(i.positionWS,_EffectCenter.xyz))*3);if(_Flow>0){float wave=.5+.5*sin(i.uv.x*90+sin(i.uv.y*12-_Phase*_Flow*6));float fall=.5+.5*sin(i.uv.y*35+_Phase*_Flow*9);c.rgb*=.7+.4*fall;c.a*=.5+.45*wave;}return c; }
            ENDHLSL
        }
    }
}
