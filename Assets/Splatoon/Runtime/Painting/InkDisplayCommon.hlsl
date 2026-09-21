#ifndef SPLATOON_DISPLAY_COMMON
#define SPLATOON_DISPLAY_COMMON
#include "UnityCG.cginc"
Texture2D<float4> _MainTex, _VisualTex;
Texture2D<float> _UVIslands;
float4 _MainTex_TexelSize, _InkPink, _InkBlue;
// Coverage, color and visual extension use this identical winner and traversal order.
int2 InkPaddedPixel(float2 uv, out float4 state)
{
    int2 pixel=clamp((int2)(uv*_MainTex_TexelSize.zw),0,(int2)_MainTex_TexelSize.zw-1);
    state=_MainTex.Load(int3(pixel,0)); int2 selected=pixel;
    if(_UVIslands.Load(int3(pixel,0))<.99)
    {
        state=0;
        for(int y=-2;y<=2;y++) for(int x=-2;x<=2;x++)
        {
            int2 candidatePixel=clamp(pixel+int2(x,y),0,(int2)_MainTex_TexelSize.zw-1);
            float4 candidate=_MainTex.Load(int3(candidatePixel,0));
            if(_UVIslands.Load(int3(candidatePixel,0))>.99 && candidate.a>state.a) { state=candidate; selected=candidatePixel; }
        }
    }
    return selected;
}
#endif
