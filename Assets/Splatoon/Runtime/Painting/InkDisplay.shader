Shader "Splatoon/InkDisplay"
{
    Properties
    {
        _MainTex("State",2D)="black" {}
        // Color properties convert the authored sRGB palette once, like the reference painter.
        _InkPink("Pink",Color)=(.9433962,.27945885,.47586557,1)
        _InkBlue("Mint blue",Color)=(.4901961,.8901961,.9098039,1)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "InkDisplayCommon.hlsl"
            float4 frag(v2f_img i):SV_Target
            {
                float4 state; InkPaddedPixel(i.uv,state);
                float3 color=round(state.b*255)==1 ? _InkPink.rgb : _InkBlue.rgb;
                return float4(color*state.a,state.a);
            }
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragVisual
            #pragma target 3.5
            #include "InkDisplayCommon.hlsl"
            float4 fragVisual(v2f_img i):SV_Target
            {
                float4 state; int2 pixel=InkPaddedPixel(i.uv,state);
                return state.a>0 ? float4(_VisualTex.Load(int3(pixel,0)).rg,0,0) : 0;
            }
            ENDHLSL
        }
    }
}
