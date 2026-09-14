Shader "Splatoon/InkImpact"
{
    Properties
    {
        _ShapeAtlas("Splat alpha atlas", 2D) = "white" {}
        [Toggle] _AtlasMask("Use splat atlas alpha", Float) = 0
        _Smoothness("Wet smoothness", Range(0, 1)) = .55
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off ZTest LEqual Cull [_Cull]
        Pass
        {
            Name "ImpactForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_ShapeAtlas); SAMPLER(sampler_ShapeAtlas);
            CBUFFER_START(UnityPerMaterial)
            float4 _ShapeAtlas_ST;
            float _AtlasMask, _Smoothness, _Cull;
            CBUFFER_END
            // UV, AgePercent and Custom1X (atlas tile) are packed by the authored vertex streams.
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; half4 color:COLOR; float4 uvAge:TEXCOORD0; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1;
                float4 uvAge:TEXCOORD2; half4 color:COLOR; half fog:TEXCOORD3; };
            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS); o.uvAge = v.uvAge;
                o.color = v.color; o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }
            float Noise(float2 uv)
            {
                float2 p = floor(uv), f = frac(uv); f = f * f * (3 - 2 * f);
                float4 h = frac(sin(float4(dot(p,float2(127.1,311.7)), dot(p+float2(1,0),float2(127.1,311.7)),
                    dot(p+float2(0,1),float2(127.1,311.7)), dot(p+1,float2(127.1,311.7)))) * 43758.5453);
                return lerp(lerp(h.x,h.y,f.x),lerp(h.z,h.w,f.x),f.y);
            }
            half4 frag(Varyings i, FRONT_FACE_TYPE facing:FRONT_FACE_SEMANTIC):SV_Target
            {
                float alpha = i.color.a;
                float3 normal = normalize(i.normalWS) * IS_FRONT_VFACE(facing, 1, -1);
                if (_AtlasMask > .5)
                {
                    float tile = floor(i.uvAge.w + .5);
                    float2 atlasUV = (float2(fmod(tile, 4), floor(tile / 4)) + i.uvAge.xy) * .25;
                    float mask = SAMPLE_TEXTURE2D(_ShapeAtlas, sampler_ShapeAtlas, atlasUV).a;
                    // Erode the silhouette after expansion, retaining solid ink until its edge breaks up.
                    float breakup = Noise(i.uvAge.xy * 11 + tile);
                    float erosion = smoothstep(.28, 1, i.uvAge.z);
                    float edge = mask - lerp(.04, 1.1, erosion) - breakup * erosion * .18;
                    alpha *= smoothstep(-.025, .025, edge);
                    float3 dx = ddx(i.positionWS), dy = ddy(i.positionWS);
                    float3 cx = cross(normal, dx), cy = cross(dy, normal);
                    float det = dot(dx, cy);
                    float3 gradient = (ddx(mask) * cy + ddy(mask) * cx) * ((det < 0 ? -1 : 1) / max(abs(det), 1e-8));
                    normal = normalize(normal - gradient * .012);
                }
                clip(alpha - .015);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = i.color.rgb; surface.alpha = alpha; surface.occlusion = 1;
                surface.normalTS = float3(0,0,1); surface.smoothness = _Smoothness;
                InputData input = (InputData)0;
                input.positionWS = i.positionWS; input.normalWS = normal;
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                input.bakedGI = SampleSH(normal); input.shadowMask = 1;
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                half4 color = UniversalFragmentPBR(input, surface);
                color.rgb = MixFog(color.rgb, i.fog); color.a = alpha;
                return color;
            }
            ENDHLSL
        }
    }
}
