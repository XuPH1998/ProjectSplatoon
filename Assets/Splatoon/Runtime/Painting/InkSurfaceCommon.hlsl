#ifndef SPLATOON_SURFACE_COMMON
#define SPLATOON_SURFACE_COMMON
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "InkCoverage.hlsl"
TEXTURE2D(_MaskTexture); SAMPLER(sampler_MaskTexture);
float4 _MaskTexture_TexelSize;
TEXTURE2D(Texture2D_41271c3c5f484ca2a435c65087a81705); SAMPLER(sampler_Texture2D_41271c3c5f484ca2a435c65087a81705);
TEXTURE2D(Texture2D_01612b2f09a24a9c9879c83799445b96); SAMPLER(sampler_Texture2D_01612b2f09a24a9c9879c83799445b96);
float4 Texture2D_01612b2f09a24a9c9879c83799445b96_TexelSize;
TEXTURE2D(_InkVisualTexture); TEXTURE2D(_InkStateTexture); TEXTURE2D(_InkIslands);
TEXTURE2D(_InkFineNormal); SAMPLER(sampler_InkFineNormal);
CBUFFER_START(UnityPerMaterial)
    float4 Color_863351f5ceea4c998ef51baab6dd758b,Color_1bf9c5e6f5c34360a490da1c94e6a7c1;
    float4 Vector2_e97cb9b7b5564bc9857e7669e2d0b82f,Vector2_55edcb19ba1d459dbb3c027e66abbc1e;
    float Vector1_7bf270fe91494824b4209d2dc1faae23,Vector1_0de750b9c41b4a5daef844a1599f5ac7;
    float Vector1_2c6f3ce4bba145b09c0a22fced0d7f85,Vector1_b160a6374fb04a77b114bb611b8c55e4;
    float Vector1_8e760635099b4147956bb9600d13cac2,Vector1_b5cc7f6f25194a778cb438f45fbbce66,Vector1_f6677799b193415b8be7686b658a6e85;
    float _InkAppearance; float4 _InkRelief, _InkFinish;
    float _InkWorldScale,_InkShapeNoiseScale,_InkThreshold;
    float _InkEdgeAAScale,_InkEdgeNormalStrength,_InkEdgeSmoothness;
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

float3 InkWorldGradient(float h,float3 p,float3 n)
{
    float3 dx=ddx(p),dy=ddy(p),cx=cross(n,dx),cy=cross(dy,n);
    float det=dot(dx,cy);
    return (ddx(h)*cy+ddy(h)*cx)*((det<0?-1:1)/max(abs(det),1e-12));
}
float InkCoverageField(float alpha,float2 uv) { return alpha*(1+.5*InkNoise(uv,_InkShapeNoiseScale)); }
float InkVisibility(float field) { float w=max(.5*fwidth(field)*_InkEdgeAAScale,1e-5); return smoothstep(_InkThreshold-w,_InkThreshold+w,field); }
float InkTeamBoundary(float2 uv)
{
    #if defined(SHADER_API_GLES3) || defined(SHADER_API_MOBILE)
        return 0;
    #else
        float2 texel=_MaskTexture_TexelSize.xy;
        float4 center=SAMPLE_TEXTURE2D_LOD(_InkStateTexture,sampler_PointClamp,uv,0);
        float owner=round(center.b*255), edge=0;
        // Compare decoded, point-sampled owners. Numeric team identifiers are never filtered.
        const float2 offsets[4]={float2(-1,0),float2(1,0),float2(0,-1),float2(0,1)};
        float2 cell=frac(uv*_MaskTexture_TexelSize.zw);
        for(int k=0;k<4;k++) {
float2 at=uv+offsets[k]*texel;
float4 other=SAMPLE_TEXTURE2D_LOD(_InkStateTexture,sampler_PointClamp,at,0);
float valid=SAMPLE_TEXTURE2D_LOD(_InkIslands,sampler_PointClamp,at,0).r;
float distance=k==0?cell.x:k==1?1-cell.x:k==2?cell.y:1-cell.y;
if(owner>0 && other.a>.25 && center.a>.25 && valid>.99 && round(other.b*255)>0 && round(other.b*255)!=owner)
    edge=max(edge,1-smoothstep(0,.7,distance));
        }
        return edge;
    #endif
}
float3 InkBevelGradient(Varyings i,float3 n)
{
    // Reconstruct a rounded shoulder from merged coverage in a narrow world-space
    // neighbourhood. The visible contour still uses the original coverage field.
    // Finite differences keep a readable rim even once the inner coverage is flat.
    float2 dx=ddx(i.paintUV),dy=ddy(i.paintUV);
    float determinant=dx.x*dy.y-dx.y*dy.x;
    float inverse=(determinant<0?-1:1)/max(abs(determinant),1e-12);
    float3 du=(ddx(i.positionWS)*dy.y-ddy(i.positionWS)*dx.y)*inverse;
    float3 dv=(ddy(i.positionWS)*dx.x-ddx(i.positionWS)*dy.x)*inverse;
    float2 radius=min(_InkRelief.y*.5/max(float2(length(du),length(dv)),1e-4),_MaskTexture_TexelSize.xy*1.5);
    radius=max(radius,1e-6);
    float4 alpha=float4(
        SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV-float2(radius.x,0)).a,
        SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV+float2(radius.x,0)).a,
        SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV-float2(0,radius.y)).a,
        SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV+float2(0,radius.y)).a);
    float4 heights=smoothstep(.2,.9,alpha)*_InkRelief.x;
    float3 uDual=cross(dv,n),vDual=cross(n,du);
    float area=dot(du,uDual);
    return ((heights.y-heights.x)/(2*radius.x)*uDual+(heights.w-heights.z)/(2*radius.y)*vDual)
        *((area<0?-1:1)/max(abs(area),1e-8));
}
float3 InkWetNormal(Varyings i,float4 mask,float2 uv,float field,out float finish)
{
    float3 n=normalize(i.normalWS);
    float2 detail=SAMPLE_TEXTURE2D(_InkVisualTexture,sampler_LinearClamp,i.paintUV).rg/max(mask.a,1.0/255);
    detail=saturate(detail);
    float3 coverageGradient=InkWorldGradient(field,i.positionWS,n);
    float signedDistance=(field-_InkThreshold)/max(length(coverageGradient),.001);
    float width=max(_InkRelief.y,.001),q=saturate(signedDistance/width);
    float shoulder=q*q*(3-2*q);
    float teamEdge=InkTeamBoundary(i.paintUV);
    #if defined(SHADER_API_GLES3) || defined(SHADER_API_MOBILE)
        float broad=0;
    #else
        float broad=InkNoise(InkDetailUV(i.positionWS,n,1),.7)-.5;
    #endif
    float interiorHeight=(detail.r-.5)*2*_InkRelief.z+broad*_InkRelief.w;
    float3 gradient=InkBevelGradient(i,n);
    gradient+=shoulder*InkWorldGradient(interiorHeight,i.positionWS,n)-InkWorldGradient(teamEdge*_InkFinish.w,i.positionWS,n);
    gradient*=min(1,.65/max(length(gradient),1e-5));
    float3 inkNormal=normalize(n-gradient);
    float2 fineUV=InkDetailUV(i.positionWS,n,_InkFinish.z);
    float3 fine=UnpackNormal(SAMPLE_TEXTURE2D(_InkFineNormal,sampler_InkFineNormal,fineUV));
    float footprint=max(length(ddx(fineUV)),length(ddy(fineUV)))*512;
    float strength=_InkFinish.y*(1-smoothstep(1,8,footprint))*shoulder;
    float3 axis=abs(n.y)>.5?float3(0,0,1):float3(0,1,0);
    float3 t=normalize(cross(n,axis)),b=cross(t,n);
    inkNormal=normalize(inkNormal+(t*fine.x+b*fine.y)*strength);
    // Broaden the specular lobe as the normal field becomes undersampled.
    float variance=dot(ddx(inkNormal),ddx(inkNormal))+dot(ddy(inkNormal),ddy(inkNormal));
    finish=clamp(_InkFinish.x+(detail.g-.5)*.17-teamEdge*.06-saturate(variance*2)*.12,.65,.82);
    return inkNormal;
}
float3 InkDepthNormal(Varyings i)
{
    float3 n=normalize(i.normalWS);
    if(_InkAppearance<.5) return n;
    float2 uv=InkDetailUV(i.positionWS,n,_InkWorldScale);
    float4 mask=SAMPLE_TEXTURE2D(_MaskTexture,sampler_MaskTexture,i.paintUV);
    float field=InkCoverageField(mask.a,uv),finish;
    return normalize(lerp(n,InkWetNormal(i,mask,uv,field,finish),InkVisibility(field)));
}

#endif
