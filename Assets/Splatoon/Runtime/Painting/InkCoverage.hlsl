#ifndef SPLATOON_INK_COVERAGE
#define SPLATOON_INK_COVERAGE
float2 InkDetailUV(float3 p, float3 normal, float scale)
{
    normal = normalize(normal);
    float3 axis = abs(normal.y) > .5 ? float3(0,0,1) : float3(0,1,0);
    float3 tangent = normalize(cross(normal, axis));
    float3 bitangent = cross(tangent, normal);
    return float2(dot(p,tangent),dot(p,bitangent)) * scale + float2(.37866,.62134);
}
float2 InkNoiseDirection(float2 p)
{
    p = p - floor(p / 289) * 289;
    float x = (34*p.x+1)*p.x;
    x = x-floor(x/289)*289+p.y;
    x = (34*x+1)*x;
    x = x-floor(x/289)*289;
    x = frac(x/41)*2-1;
    return normalize(float2(x-floor(x+.5),abs(x)-.5));
}
float InkNoise(float2 uv, float scale)
{
    float2 p=uv*scale, ip=floor(p), fp=frac(p);
    float d00=dot(InkNoiseDirection(ip),fp);
    float d01=dot(InkNoiseDirection(ip+float2(0,1)),fp-float2(0,1));
    float d10=dot(InkNoiseDirection(ip+float2(1,0)),fp-float2(1,0));
    float d11=dot(InkNoiseDirection(ip+1),fp-1);
    fp=fp*fp*fp*(fp*(fp*6-15)+10);
    return lerp(lerp(d00,d01,fp.y),lerp(d10,d11,fp.y),fp.x)+.5;
}
float InkVisible(float alpha, float2 uv, float noiseScale, float threshold)
{
    return step(threshold,alpha*(1+.5*InkNoise(uv,noiseScale)));
}
float4 InkAccumulate(float4 state, float team, float f)
{
    precise float2 weights = floor(saturate(state.rg*(1-f) + float2(team==1,team==2)*f)*255+.5);
    float owner=weights.x>weights.y ? 1 : weights.y>weights.x ? 2 : round(state.b*255);
    return float4(weights,owner,min(255,weights.x+weights.y))/255;
}
#endif
