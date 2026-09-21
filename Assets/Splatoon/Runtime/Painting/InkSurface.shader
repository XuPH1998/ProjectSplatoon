Shader "Splatoon/InkSurface"
{
    Properties
    {
        _InkAppearance("Persistent wet ink",Float)=0
        _InkVisualTexture("Visual state",2D)="black" {}
        _InkStateTexture("Coverage state",2D)="black" {}
        _InkIslands("UV islands",2D)="white" {}
        [Normal] _InkFineNormal("Fine normal",2D)="bump" {}
        _InkRelief("Edge height / width / relief / broad",Vector)=(.018,.055,.002,.0005)
        _InkFinish("Smoothness / normal / tiling / groove",Vector)=(.75,.25,.65,.001)
        [Toggle] _InkRoundedEdge("Rounded outer edge",Float)=0
        _InkRoundedRelief("Rounded height / width / max slope",Vector)=(.030,.080,.9,0)
        _InkRoundedFinish("Rounded smoothness / contact shade",Vector)=(.78,.08,0,0)
        _MaskTexture("Ink display",2D)="black" {}
        Texture2D_41271c3c5f484ca2a435c65087a81705("Base texture",2D)="white" {}
        Texture2D_01612b2f09a24a9c9879c83799445b96("Glitter texture",2D)="gray" {}
        Color_863351f5ceea4c998ef51baab6dd758b("Base tint",Color)=(1,1,1,1)
        [HDR] Color_1bf9c5e6f5c34360a490da1c94e6a7c1("Glitter color",Color)=(5.34,5.34,5.34,1)
        Vector2_e97cb9b7b5564bc9857e7669e2d0b82f("Base tiling",Vector)=(20,20,0,0)
        Vector2_55edcb19ba1d459dbb3c027e66abbc1e("Glitter tiling",Vector)=(3,3,0,0)
        Vector1_7bf270fe91494824b4209d2dc1faae23("Ink smoothness",Range(0,1))=.7
        Vector1_0de750b9c41b4a5daef844a1599f5ac7("Ink metallic",Range(0,1))=.01
        Vector1_2c6f3ce4bba145b09c0a22fced0d7f85("Base smoothness",Range(0,1))=0
        Vector1_b160a6374fb04a77b114bb611b8c55e4("Base metallic",Range(0,1))=0
        Vector1_8e760635099b4147956bb9600d13cac2("Ink normal strength",Float)=.3
        Vector1_b5cc7f6f25194a778cb438f45fbbce66("Bump noise scale",Float)=25
        Vector1_f6677799b193415b8be7686b658a6e85("Glitter intensity",Float)=9
        _InkWorldScale("Detail UV / metre",Float)=.034424
        _InkShapeNoiseScale("Shape noise scale",Float)=110
        _InkThreshold("Coverage threshold",Float)=.5
        _InkEdgeAAScale("Edge antialiasing width",Range(0.25,2))=1
        _InkEdgeNormalStrength("Edge normal strength",Range(0,1))=.12
        _InkEdgeSmoothness("Edge smoothness",Range(0,1))=.55
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "InkSurfaceCommon.hlsl"
            half4 frag(Varyings i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n=normalize(i.normalWS),view=GetWorldSpaceNormalizeViewDir(i.positionWS);
                float2 uv=InkDetailUV(i.positionWS,n,_InkWorldScale);
                float4 mask=SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV);
                // Mask alpha now includes the authored irregular splat silhouette; retain the
                // existing wet edge/noise treatment on top of that silhouette.
                // Display-only coverage: keep InkVisible and the CPU ownership threshold exact.
                float coverageField=mask.a*(1+.5*InkNoise(uv,_InkShapeNoiseScale));
                float edgeHalfWidth=max(.5*fwidth(coverageField)*_InkEdgeAAScale,1e-5);
                float visible=smoothstep(_InkThreshold-edgeHalfWidth,_InkThreshold+edgeHalfWidth,coverageField);
                float interior=smoothstep(_InkThreshold,_InkThreshold+max(.15,2*edgeHalfWidth),coverageField);
                float3 inkNormal; float wetFinish=0,contactShade=0;
                if(_InkAppearance>.5) inkNormal=InkWetNormal(i,mask,uv,coverageField,wetFinish,contactShade);
                else
                {
                // Filter height only. Filtering the silhouette would grow/shrink gameplay ink.
                float2 texel=_MaskTexture_TexelSize.xy;
                float heightAlpha=mask.a*.5;
                heightAlpha+=.125*(SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV+float2(texel.x,0)).a
                    +SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV-float2(texel.x,0)).a
                    +SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV+float2(0,texel.y)).a
                    +SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV-float2(0,texel.y)).a);
                float noise=InkNoise(uv,Vector1_b5cc7f6f25194a778cb438f45fbbce66);
                float height=.3+heightAlpha*noise*noise;
                float3 dx=ddx(i.positionWS),dy=ddy(i.positionWS);
                float3 cx=cross(n,dx),cy=cross(dy,n);
                float det=dot(dx,cy);
                float3 gradient=(ddx(height)*cy+ddy(height)*cx)*((det<0?-1:1)/max(abs(det),1e-12));
                float normalStrength=lerp(_InkEdgeNormalStrength,Vector1_8e760635099b4147956bb9600d13cac2,interior);
                inkNormal=normalize(n-normalStrength*gradient);
                }
                float3 baseColor=SAMPLE_TEXTURE2D(Texture2D_41271c3c5f484ca2a435c65087a81705,sampler_Texture2D_41271c3c5f484ca2a435c65087a81705,uv*Vector2_e97cb9b7b5564bc9857e7669e2d0b82f.xy).rgb*Color_863351f5ceea4c998ef51baab6dd758b.rgb;
                float sparkle=_InkAppearance>.5?0:FilteredGlitter(uv*Vector2_55edcb19ba1d459dbb3c027e66abbc1e.xy,n,view);
                SurfaceData surface=(SurfaceData)0;
                // InkDisplay stores premultiplied color; do not multiply edge opacity twice.
                float3 inkColor=mask.a>1e-5 ? mask.rgb/max(mask.a,1e-5) : baseColor;
                inkColor*=1-contactShade;
                surface.albedo=lerp(baseColor,inkColor,visible);surface.alpha=1;surface.occlusion=1;surface.normalTS=float3(0,0,1);
                surface.metallic=lerp(Vector1_b160a6374fb04a77b114bb611b8c55e4,(_InkAppearance>.5?0:Vector1_0de750b9c41b4a5daef844a1599f5ac7),visible);
                float inkSmoothness=lerp(_InkEdgeSmoothness,Vector1_7bf270fe91494824b4209d2dc1faae23,interior);
                if(_InkAppearance>.5) inkSmoothness=wetFinish;
                surface.smoothness=lerp(Vector1_2c6f3ce4bba145b09c0a22fced0d7f85,inkSmoothness,visible);
                surface.emission=visible*sparkle*Color_1bf9c5e6f5c34360a490da1c94e6a7c1.rgb;
                InputData input=(InputData)0;input.positionWS=i.positionWS;input.normalWS=normalize(lerp(n,inkNormal,visible));input.viewDirectionWS=view;
                input.shadowCoord=TransformWorldToShadowCoord(i.positionWS);input.bakedGI=SampleSH(input.normalWS);input.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);
                input.vertexLighting=VertexLighting(i.positionWS,input.normalWS);input.shadowMask=half4(1,1,1,1);
                half4 color=UniversalFragmentPBR(input,surface);color.rgb=MixFog(color.rgb,i.fog);return color;
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment DepthNormalFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "InkSurfaceCommon.hlsl"
            half4 DepthNormalFragment(Varyings i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n=InkDepthNormal(i);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct=PackNormalOctQuadEncode(n);
                    return half4(PackFloat2To888(saturate(oct*.5+.5)),0);
                #else
                    return half4(n,0);
                #endif
            }
            ENDHLSL
        }
    }
}
