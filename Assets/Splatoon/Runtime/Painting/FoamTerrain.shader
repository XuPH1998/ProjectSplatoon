Shader "Splatoon/FoamTerrain"
{
    Properties
    {
        _Owners("Resolved owners",2D)="black" {}
        _Pink("Pink",Color)=(.9433962,.27945885,.47586557,1)
        _Blue("Blue",Color)=(.490196,.890196,.909804,1)
        _Grid("Grid",Vector)=(17,17,.0588,.0588)
        _Pores("Baked foam detail",2D)="gray" {}
        _FoamDetail("Scale, pore brightness, normal, highlight",Vector)=(2.5,.12,.22,.18)
        _FoamDistance("Detail fade near/far",Vector)=(6,18,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry+1" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_Owners); SAMPLER(sampler_Owners);
        TEXTURE2D(_Pores); SAMPLER(sampler_Pores);
        CBUFFER_START(UnityPerMaterial)
        float4 _Pink, _Blue, _Grid, _FoamDetail, _FoamDistance;
        CBUFFER_END
        struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float4 color:COLOR; float2 uv:TEXCOORD0; };
        struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float2 uv:TEXCOORD2; float height:TEXCOORD3; float fog:TEXCOORD4; };
        Varyings vert(Attributes v)
        {
            Varyings o; o.positionWS=TransformObjectToWorld(v.positionOS.xyz); o.positionCS=TransformWorldToHClip(o.positionWS);
            o.normalWS=TransformObjectToWorldNormal(v.normalOS); o.uv=v.uv; o.height=v.color.a; o.fog=ComputeFogFactor(o.positionCS.z); return o;
        }
        float Owner(float2 uv)
        {
            float2 p=saturate(uv)*(_Grid.xy-1), cell=min(floor(p),_Grid.xy-2), f=p-cell;
            float3 weights; float2 a,b,c;
            if(f.x+f.y<=1) { a=cell;b=cell+float2(0,1);c=cell+float2(1,0);weights=float3(1-f.x-f.y,f.y,f.x); }
            else { a=cell+float2(1,0);b=cell+float2(0,1);c=cell+1;weights=float3(1-f.y,1-f.x,f.x+f.y-1); }
            float2 node=weights.x>=weights.y && weights.x>=weights.z?a:weights.y>=weights.z?b:c;
            return SAMPLE_TEXTURE2D(_Owners,sampler_Owners,(node+.5)*_Grid.zw).r*255;
        }
        half4 FoamDetail(float3 p,float3 n,out half3 perturbation)
        {
            half3 weights=abs(n);weights*=weights;weights*=weights;
            weights/=max(dot(weights,half3(1,1,1)),.001);
            p*=_FoamDetail.x;
            half4 x=SAMPLE_TEXTURE2D(_Pores,sampler_Pores,p.zy);
            half4 y=SAMPLE_TEXTURE2D(_Pores,sampler_Pores,p.xz);
            half4 z=SAMPLE_TEXTURE2D(_Pores,sampler_Pores,p.xy);
            half2 sx=x.rg*2-1,sy=y.rg*2-1,sz=z.rg*2-1;
            perturbation=half3(0,sx.y,sx.x)*weights.x+half3(sy.x,0,sy.y)*weights.y+half3(sz.x,sz.y,0)*weights.z;
            perturbation-=n*dot(perturbation,n);
            return x*weights.x+y*weights.y+z*weights.z;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            half4 frag(Varyings i):SV_Target
            {
                clip(i.height-.00025);
                float owner=Owner(i.uv);
                float3 color=owner<.5?float3(.82,.86,.9):owner<1.5?_Pink.rgb:_Blue.rgb;
                float3 p=i.positionWS;
                float3 n=normalize(i.normalWS);
                half3 perturbation;
                half4 detail=FoamDetail(p,n,perturbation);
                float distanceToCamera=distance(_WorldSpaceCameraPos,p);
                half fade=1-smoothstep(_FoamDistance.x,_FoamDistance.y,distanceToCamera);
                n=normalize(n+perturbation*_FoamDetail.z*fade);
                Light light=GetMainLight(TransformWorldToShadowCoord(p));
                float shade=saturate(dot(n,light.direction))*.65+.35;
                float3 view=GetWorldSpaceNormalizeViewDir(p);
                float rim=pow(1-saturate(dot(n,view)),3)*.08;
                float highlight=pow(saturate(dot(n,normalize(light.direction+view))),lerp(10,18,detail.a))*_FoamDetail.w;
                color=lerp(color,float3(1,1,1),.06+detail.b*_FoamDetail.y*fade);
                color=color*(SampleSH(n)+light.color*shade*lerp(.5,1,light.shadowAttenuation))+rim+highlight;
                return half4(MixFog(color,i.fog),1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment depth
            half4 depth(Varyings i):SV_Target { clip(i.height-.00025); return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadow
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection,_LightPosition;
            Varyings shadowVert(Attributes v)
            {
                Varyings o=vert(v);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 direction=normalize(_LightPosition-o.positionWS);
                #else
                float3 direction=_LightDirection;
                #endif
                o.positionCS=TransformWorldToHClip(ApplyShadowBias(o.positionWS,o.normalWS,direction));
                #if UNITY_REVERSED_Z
                o.positionCS.z=min(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
                #else
                o.positionCS.z=max(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
                #endif
                return o;
            }
            half4 shadow(Varyings i):SV_Target { clip(i.height-.00025); return 0; }
            ENDHLSL
        }
    }
}
