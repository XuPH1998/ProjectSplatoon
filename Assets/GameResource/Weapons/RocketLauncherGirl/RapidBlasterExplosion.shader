Shader "Splatoon/RapidBlasterExplosion"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On ZTest LEqual Cull Back
        Pass
        {
            Name "InkBurstForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Mesh particles pack UV.xy and AgePercent.z into TEXCOORD0.
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; half4 color:COLOR; float3 uvAge:TEXCOORD0; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normalWS:TEXCOORD1;
                float3 uvAge:TEXCOORD2; half4 color:COLOR; half fog:TEXCOORD3; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS); o.uvAge = v.uvAge;
                o.color = v.color; o.fog = ComputeFogFactor(p.positionCS.z); return o;
            }
            float Hash(float3 p) { p = frac(p * .1031); p += dot(p, p.yzx + 33.33); return frac((p.x + p.y) * p.z); }
            float Noise(float3 p)
            {
                float3 cell = floor(p), f = frac(p); f = f * f * (3 - 2 * f);
                return lerp(lerp(lerp(Hash(cell), Hash(cell + float3(1,0,0)), f.x),
                                 lerp(Hash(cell + float3(0,1,0)), Hash(cell + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(Hash(cell + float3(0,0,1)), Hash(cell + float3(1,0,1)), f.x),
                                 lerp(Hash(cell + float3(0,1,1)), Hash(cell + 1), f.x), f.y), f.z);
            }
            half4 Frag(Varyings i):SV_Target
            {
                // Spherical coordinates keep erosion continuous across the mesh UV seam.
                float latitude = i.uvAge.y * PI, longitude = i.uvAge.x * TWO_PI;
                float3 direction = float3(sin(latitude) * cos(longitude), cos(latitude), sin(latitude) * sin(longitude));
                float breakup = Noise(direction * 7 + 13.7);
                float erosion = smoothstep(.5, 1, i.uvAge.z);
                clip(breakup - lerp(-.05, 1.05, erosion));
                half3 n = normalize(i.normalWS), view = GetWorldSpaceNormalizeViewDir(i.positionWS);
                Light light = GetMainLight();
                half diffuse = saturate(dot(n, light.direction));
                half rim = pow(1 - saturate(dot(n, view)), 3);
                half shine = pow(saturate(dot(n, normalize(view + light.direction))), 44);
                half3 color = i.color.rgb * (.7 + .3 * diffuse) + lerp(i.color.rgb, half3(1,1,1), .45) * (rim * .17 + shine * .5);
                return half4(MixFog(color, i.fog), i.color.a);
            }
            ENDHLSL
        }
    }
}
