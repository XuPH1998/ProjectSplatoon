Shader "Splatoon/InkSurface"
{
    Properties
    {
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
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "InkCoverage.hlsl"
            TEXTURE2D(_MaskTexture); SAMPLER(sampler_MaskTexture);
            TEXTURE2D(Texture2D_41271c3c5f484ca2a435c65087a81705); SAMPLER(sampler_Texture2D_41271c3c5f484ca2a435c65087a81705);
            TEXTURE2D(Texture2D_01612b2f09a24a9c9879c83799445b96); SAMPLER(sampler_Texture2D_01612b2f09a24a9c9879c83799445b96);
            float4 Texture2D_01612b2f09a24a9c9879c83799445b96_TexelSize;
            CBUFFER_START(UnityPerMaterial)
                float4 Color_863351f5ceea4c998ef51baab6dd758b,Color_1bf9c5e6f5c34360a490da1c94e6a7c1;
                float4 Vector2_e97cb9b7b5564bc9857e7669e2d0b82f,Vector2_55edcb19ba1d459dbb3c027e66abbc1e;
                float Vector1_7bf270fe91494824b4209d2dc1faae23,Vector1_0de750b9c41b4a5daef844a1599f5ac7;
                float Vector1_2c6f3ce4bba145b09c0a22fced0d7f85,Vector1_b160a6374fb04a77b114bb611b8c55e4;
                float Vector1_8e760635099b4147956bb9600d13cac2,Vector1_b5cc7f6f25194a778cb438f45fbbce66,Vector1_f6677799b193415b8be7686b658a6e85;
                float _InkWorldScale,_InkShapeNoiseScale,_InkThreshold;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 paintUV:TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float2 paintUV:TEXCOORD2; float fog:TEXCOORD3; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert(Attributes i)
            {
                Varyings o=(Varyings)0; UNITY_SETUP_INSTANCE_ID(i); UNITY_TRANSFER_INSTANCE_ID(i,o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p=GetVertexPositionInputs(i.positionOS.xyz);o.positionCS=p.positionCS;o.positionWS=p.positionWS;
                o.normalWS=TransformObjectToWorldNormal(i.normalOS);o.paintUV=i.paintUV;o.fog=ComputeFogFactor(p.positionCS.z);return o;
            }
            float Glint(float2 texel,float3 n,float3 view)
            {
                float2 uv=(texel+.5)*Texture2D_01612b2f09a24a9c9879c83799445b96_TexelSize.xy;
                float3 jitter=SAMPLE_TEXTURE2D_LOD(Texture2D_01612b2f09a24a9c9879c83799445b96,sampler_PointRepeat,uv,0).rgb-.5;
                return pow(saturate(dot(normalize(n+normalize(jitter+1e-6)),view)),exp(Vector1_f6677799b193415b8be7686b658a6e85+1));
            }
            float FilteredGlitter(float2 uv,float3 n,float3 view)
            {
                float2 texel=uv*Texture2D_01612b2f09a24a9c9879c83799445b96_TexelSize.zw-.5;
                float2 cell=floor(texel),f=frac(texel);
                // Filter reflected light after the sharp lobe. Filtering random normals first
                // erases the reference glints; point sampling the lobe makes them shimmer.
                float sparkle=lerp(lerp(Glint(cell,n,view),Glint(cell+float2(1,0),n,view),f.x),lerp(Glint(cell+float2(0,1),n,view),Glint(cell+1,n,view),f.x),f.y);
                float footprint=max(length(ddx(texel)),length(ddy(texel)));
                return sparkle*(1-smoothstep(1,3,footprint));
            }
            half4 frag(Varyings i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n=normalize(i.normalWS),view=GetWorldSpaceNormalizeViewDir(i.positionWS);
                float2 uv=InkDetailUV(i.positionWS,n,_InkWorldScale);
                float4 mask=SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV);
                // Mask alpha now includes the authored irregular splat silhouette; retain the
                // existing wet edge/noise treatment on top of that silhouette.
                float visible=InkVisible(mask.a,uv,_InkShapeNoiseScale,_InkThreshold);
                float noise=InkNoise(uv,Vector1_b5cc7f6f25194a778cb438f45fbbce66);
                float height=.3+mask.a*noise*noise;
                float3 dx=ddx(i.positionWS),dy=ddy(i.positionWS);
                float3 cx=cross(n,dx),cy=cross(dy,n);
                float det=dot(dx,cy);
                float3 gradient=(ddx(height)*cy+ddy(height)*cx)*((det<0?-1:1)/max(abs(det),1e-12));
                float3 inkNormal=normalize(n-Vector1_8e760635099b4147956bb9600d13cac2*gradient);
                float3 baseColor=SAMPLE_TEXTURE2D(Texture2D_41271c3c5f484ca2a435c65087a81705,sampler_Texture2D_41271c3c5f484ca2a435c65087a81705,uv*Vector2_e97cb9b7b5564bc9857e7669e2d0b82f.xy).rgb*Color_863351f5ceea4c998ef51baab6dd758b.rgb;
                float sparkle=FilteredGlitter(uv*Vector2_55edcb19ba1d459dbb3c027e66abbc1e.xy,n,view);
                SurfaceData surface=(SurfaceData)0;
                surface.albedo=lerp(baseColor,mask.rgb,visible);surface.alpha=1;surface.occlusion=1;surface.normalTS=float3(0,0,1);
                surface.metallic=lerp(Vector1_b160a6374fb04a77b114bb611b8c55e4,Vector1_0de750b9c41b4a5daef844a1599f5ac7,visible);
                surface.smoothness=lerp(Vector1_2c6f3ce4bba145b09c0a22fced0d7f85,Vector1_7bf270fe91494824b4209d2dc1faae23,visible);
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
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }
}
