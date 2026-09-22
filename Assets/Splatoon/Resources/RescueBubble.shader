Shader "Splatoon/RescueBubble"
{
    Properties
    {
        _BubbleColor("Team", Color) = (0.3,0.8,1,1)
        _Fade("Fade", Float) = 1
        _Selected("Selected", Float) = 0
        _Burst("Burst", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float4 _BubbleColor;
            float _Fade, _Selected, _Burst;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings o; o.positionWS=TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS=TransformWorldToHClip(o.positionWS); o.normalWS=TransformObjectToWorldNormal(input.normalOS); return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                float3 n=normalize(i.normalWS), v=normalize(GetWorldSpaceViewDir(i.positionWS));
                float rim=pow(1-saturate(dot(n,v)),3);
                float shine=pow(saturate(dot(n,normalize(float3(-.4,.8,-.4)))),32);
                float stripe=pow(.5+.5*sin(n.y*65+_Time.y*2),18)*.1;
                float pulse=.5+.5*sin(_Time.y*7);
                float3 rgb=lerp(_BubbleColor.rgb,float3(1,1,1),saturate(rim*.6+shine+_Selected*.25));
                float alpha=(.045+rim*(.65+_Selected*.2*pulse)+shine*.35+stripe)*_Fade;
                float holes=sin(n.x*47+n.z*31)*sin(n.y*39-n.z*25);
                if (_Burst>0) alpha*=smoothstep(_Burst*1.8-1,_Burst*1.8-.8,holes);
                return half4(rgb, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
