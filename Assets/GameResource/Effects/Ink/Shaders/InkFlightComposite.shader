Shader "Hidden/Splatoon/InkFlightComposite"
{
    Properties
    {
        _MainTex("Ink", 2D) = "black" {}
        _PreserveHue("Preserve team hue", Float) = 1
        _AlphaStep("Alpha low", Float) = 0
        _AlphaStep2("Alpha high", Float) = .7
        _Clip("Clip", Float) = .903
        _ColorStep("Color low", Float) = 0
        _ColorStep2("Color high", Float) = .404
        _CheckOcclusion("Final scene occlusion", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _MetaballDepthRT, _FlightSceneDepth;
            float4 _MainTex_TexelSize;
            float _PreserveHue, _AlphaStep, _AlphaStep2, _Clip, _ColorStep, _ColorStep2, _CheckOcclusion;
            float validMin(float a, float b) { return b > 0 ? min(a, b) : a; }
            float4 frag(v2f_img i) : SV_Target
            {
                float4 ink = tex2D(_MainTex, i.uv);
                float alpha = smoothstep(_AlphaStep, _AlphaStep2, ink.a);
                clip(alpha - _Clip);
                if (_CheckOcclusion > .5)
                {
                    float depth = tex2D(_MetaballDepthRT, i.uv).r;
                    if (depth <= 0)
                    {
                        // Small gaps fuse using their nearest ink surface; foreground geometry still wins.
                        depth = 2;
                        [unroll] for (int ring = 1; ring <= 3; ring++)
                        {
                            float2 d = _MainTex_TexelSize.xy * (ring * 2);
                            depth = validMin(depth, tex2D(_MetaballDepthRT, i.uv + float2(d.x,0)).r);
                            depth = validMin(depth, tex2D(_MetaballDepthRT, i.uv - float2(d.x,0)).r);
                            depth = validMin(depth, tex2D(_MetaballDepthRT, i.uv + float2(0,d.y)).r);
                            depth = validMin(depth, tex2D(_MetaballDepthRT, i.uv - float2(0,d.y)).r);
                        }
                    }
                    float scene = Linear01Depth(tex2D(_FlightSceneDepth, i.uv).r);
                    clip(scene + .00002 - depth);
                }
                float3 rgb = smoothstep(_ColorStep, _ColorStep2, ink.rgb);
                if (_PreserveHue > .5)
                {
                    float peak = max(ink.r, max(ink.g, ink.b));
                    rgb = ink.rgb / max(peak, .00001) * smoothstep(_ColorStep, _ColorStep2, peak);
                }
                return float4(rgb, alpha);
            }
            ENDCG
        }
    }
}
