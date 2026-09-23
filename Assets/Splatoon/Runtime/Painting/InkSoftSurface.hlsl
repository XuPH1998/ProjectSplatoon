#ifndef SPLATOON_SOFT_SURFACE
#define SPLATOON_SOFT_SURFACE
float2 InkSoftDistances(float2 uv,out float rawOuter)
{
    float2 texel=_MaskTexture_TexelSize.xy*.75;
    float2 centre=SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,uv,0).rg;
    rawOuter=centre.r*_InkBoundaryDecode.x+_InkBoundaryDecode.y;
    float2 value=centre*.5;
    value+=(SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,uv+float2(texel.x,0),0).rg+
        SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,uv-float2(texel.x,0),0).rg+
        SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,uv+float2(0,texel.y),0).rg+
        SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,uv-float2(0,texel.y),0).rg)*.125;
    return value*_InkBoundaryDecode.x+_InkBoundaryDecode.y;
}
// A monotonic broad shoulder avoids a raised ring around every droplet. All
// lighting uses merged coverage; interior folds never recreate individual stamps.
float3 InkSoftNormal(Varyings i,float4 mask,inout float field,out float finish,out float contact)
{
    float3 n=normalize(i.normalWS);
    float rawOuter;
    float2 distance=InkSoftDistances(i.paintUV,rawOuter);
    float width=max(_InkSoftRelief.y,.001), d=max(0,distance.x), q=saturate(d/width);
    float3 gd=InkWorldGradient(distance.x,i.positionWS,n);
    float pixel=max(length(ddx(i.positionWS)),length(ddy(i.positionWS)));
    float resolved=1-smoothstep(.5,2,pixel/width);
    // Smooth monotonic dome: finite slope at contact, zero slope in the body.
    // A small component never develops an inward-facing crater or sharp medial ridge.
    float shoulder=1-pow(1-q,3);
    float derivative=3*(1-q)*(1-q);
    float3 gradient=gd*(derivative*_InkSoftRelief.x/width)*resolved;

    float2 du=ddx(i.paintUV),dv=ddy(i.paintUV); float det=du.x*dv.y-du.y*dv.x;
    float inv=(det<0?-1:1)/max(abs(det),1e-12);
    float3 worldU=(ddx(i.positionWS)*dv.y-ddy(i.positionWS)*du.y)*inv;
    float3 worldV=(ddy(i.positionWS)*du.x-ddx(i.positionWS)*dv.x)*inv;
    float texelWorld=min(length(worldU)*_MaskTexture_TexelSize.x,length(worldV)*_MaskTexture_TexelSize.y);
    float limit=min(.5*texelWorld,.01)*saturate(_InkSoftDetail.w);
    // Advect only the display lookup, with a strict world-space displacement cap.
    // Two-sided clearance rejects thin bridges, small holes and isolated droplets.
    // Explicit LOD samples make this local branch independent of implicit derivatives.
    if(limit>0 && abs(rawOuter)<limit*2)
    {
        float3 direction=gd/max(length(gd),1e-5);
        float3 crossUV=cross(worldU,worldV);
        float2 uvDirection=float2(dot(direction,cross(worldV,crossUV)),dot(direction,cross(crossUV,worldU)))/max(dot(crossUV,crossUV),1e-12);
        float reach=max(2*texelWorld,limit*3);
        float inside=SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,i.paintUV+uvDirection*reach,0).r*_InkBoundaryDecode.x+_InkBoundaryDecode.y;
        float outside=SAMPLE_TEXTURE2D_LOD(_InkBoundary,sampler_LinearClamp,i.paintUV-uvDirection*reach,0).r*_InkBoundaryDecode.x+_InkBoundaryDecode.y;
        if(inside>reach*.6 && outside<-reach*.6)
        {
            float offset=clamp(distance.x-rawOuter,-limit,limit);
            float2 at=i.paintUV+uvDirection*offset;
            if(SAMPLE_TEXTURE2D_LOD(_InkIslands,sampler_PointClamp,at,0).r>.99)
            {
                float alpha=SAMPLE_TEXTURE2D_LOD(_MaskTexture,sampler_LinearClamp,at,0).a;
                field=InkCoverageField(alpha,InkDetailUV(i.positionWS+direction*offset,n,_InkWorldScale));
            }
        }
    }
    float2 radius=min(_InkSoftDetail.x/max(float2(length(worldU),length(worldV)),1e-4),_MaskTexture_TexelSize.xy*3.5);
    float owner=round(SAMPLE_TEXTURE2D_LOD(_InkStateTexture,sampler_PointClamp,i.paintUV,0).b*255);
    float2 detail=SAMPLE_TEXTURE2D_LOD(_InkVisualTexture,sampler_LinearClamp,i.paintUV,0).rg/max(mask.a,1.0/255);
    float2 filtered=saturate(detail)*mask.a; float weights=mask.a;
    // Five-point normalized filter. Reuse the centre already read above, and
    // weight premultiplied visual samples directly instead of decoding/re-encoding.
    const float2 offsets[4]={float2(-1,0),float2(1,0),float2(0,-1),float2(0,1)};
    [unroll] for(int k=0;k<4;k++)
    {
        float2 at=i.paintUV+offsets[k]*radius;
        float4 state=SAMPLE_TEXTURE2D_LOD(_InkStateTexture,sampler_PointClamp,at,0);
        float valid=step(.99,SAMPLE_TEXTURE2D_LOD(_InkIslands,sampler_PointClamp,at,0).r);
        float w=(round(state.b*255)==owner?1:0)*valid;
        float2 value=min(SAMPLE_TEXTURE2D_LOD(_InkVisualTexture,sampler_LinearClamp,at,0).rg,state.aa);
        filtered+=value*w; weights+=state.a*w;
    }
    filtered=weights>1e-5?filtered/weights:detail;
    float h=filtered.r+(detail.r-filtered.r)*_InkSoftFinish.z;
    float broad=InkNoise(InkDetailUV(i.positionWS,n,1),.7)-.5;
    float body=(h-.5)*2*_InkSoftRelief.z+broad*_InkSoftRelief.w;
    gradient+=shoulder*InkWorldGradient(body,i.positionWS,n);

    // Signed team distance gives a continuous, symmetric shallow depression.
    // No contact-darkening at ink/ink boundaries and no claim of chronological layering.
    float teamWidth=max(width*_InkSoftDetail.z,.001);
    float tq=saturate(abs(distance.y)/teamWidth);
    float taper=smoothstep(0,width*.7,d);
    float teamHeight=-_InkSoftRelief.x*_InkSoftDetail.y*(1-tq*tq*(3-2*tq))*taper;
    gradient+=InkWorldGradient(teamHeight,i.positionWS,n)*resolved;
    gradient*=rsqrt(1+dot(gradient,gradient)/(.8*.8));
    float3 inkNormal=normalize(n-gradient);
    float2 fineUV=InkDetailUV(i.positionWS,n,_InkFinish.z);
    float3 fine=UnpackNormal(SAMPLE_TEXTURE2D(_InkFineNormal,sampler_InkFineNormal,fineUV));
    float footprint=max(length(ddx(fineUV)),length(ddy(fineUV)))*512;
    float strength=_InkSoftFinish.w*(1-smoothstep(1,8,footprint))*shoulder;
    #if defined(SHADER_API_GLES3) || defined(SHADER_API_MOBILE)
        strength*=.5;
    #endif
    float3 axis=abs(n.y)>.5?float3(0,0,1):float3(0,1,0);
    float3 t=normalize(cross(n,axis)),b=cross(t,n);
    inkNormal=normalize(inkNormal+(t*fine.x+b*fine.y)*strength);
    float variance=dot(ddx(inkNormal),ddx(inkNormal))+dot(ddy(inkNormal),ddy(inkNormal));
    finish=clamp(_InkSoftFinish.x+(filtered.g-.5)*.08-saturate(variance*2)*.12,.65,.82);
    contact=min(_InkSoftFinish.y,.03)*(1-smoothstep(0,.2,q))*resolved;
    return inkNormal;
}
#endif
