Shader "Splatoon/FoamTerrain"
{
    Properties
    {
        _Owners("Resolved owners",2D)="black" {}
        _Pink("Pink",Color)=(.9433962,.27945885,.47586557,1)
        _Blue("Blue",Color)=(.490196,.890196,.909804,1)
        _Grid("Grid",Vector)=(17,17,.0588,.0588)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry+1" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_Owners); SAMPLER(sampler_Owners);
        CBUFFER_START(UnityPerMaterial)
        float4 _Pink, _Blue, _Grid;
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
        float3 FoamHash(float3 p){return frac(sin(float3(dot(p,float3(127.1,311.7,74.7)),dot(p,float3(269.5,183.3,246.1)),dot(p,float3(113.5,271.9,124.6))))*43758.5453);}
        float FoamCells(float3 p)
        {
            float3 cell=floor(p),f=frac(p);float d=2;
            [unroll]for(int z=-1;z<=1;z++)[unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++)
            {float3 q=float3(x,y,z);float3 v=q+FoamHash(cell+q)*.65+.175-f;d=min(d,dot(v,v));}
            return sqrt(d);
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
                float bubbles=FoamCells(p*18);
                float3 n=normalize(i.normalWS);
                // Small pockets change shading only; the silhouette remains the collision mesh.
                float3 dpdx=ddx(p),dpdy=ddy(p);
                float3 r1=cross(dpdy,n),r2=cross(n,dpdx);
                float determinant=dot(dpdx,r1);
                float3 gradient=(r1*ddx(bubbles)+r2*ddy(bubbles))/max(abs(determinant),1e-7)*sign(determinant);
                n=normalize(n-gradient*.004);
                Light light=GetMainLight(TransformWorldToShadowCoord(p));
                float shade=saturate(dot(n,light.direction))*.65+.35;
                float3 view=GetWorldSpaceNormalizeViewDir(p);
                float rim=pow(1-saturate(dot(n,view)),3)*.22;
                float highlight=pow(saturate(dot(n,normalize(light.direction+view))),28)*.22;
                color=lerp(color,float3(1,1,1),.12+smoothstep(.5,.72,bubbles)*.16);
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
