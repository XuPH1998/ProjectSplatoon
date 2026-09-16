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
            #include "UnityCG.cginc"
            sampler2D _MainTex, _UVIslands;
            float4 _MainTex_TexelSize, _InkPink, _InkBlue;
            float4 frag(v2f_img i):SV_Target
            {
                float4 state=tex2D(_MainTex,i.uv);
                if(tex2D(_UVIslands,i.uv).r<.99)
                {
                    // Copy a complete neighbour, never component-wise max two team states.
                    for(int y=-2;y<=2;y++) for(int x=-2;x<=2;x++)
                    {
                        float2 uv=i.uv+float2(x,y)*_MainTex_TexelSize.xy;
                        float4 candidate=tex2D(_MainTex,uv);
                        if(tex2D(_UVIslands,uv).r>.99 && candidate.a>state.a) state=candidate;
                    }
                }
                float3 color=round(state.b*255)==1 ? _InkPink.rgb : _InkBlue.rgb;
                return float4(color*state.a,state.a);
            }
            ENDHLSL
        }
    }
}
