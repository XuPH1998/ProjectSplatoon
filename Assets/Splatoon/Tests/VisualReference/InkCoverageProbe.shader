Shader "Hidden/Splatoon/InkCoverageProbe"
{
    Properties { _MainTex("State",2D)="black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "../../Runtime/Painting/InkCoverage.hlsl"
            sampler2D _MainTex;
            float4x4 _LocalToWorld;
            float3 _Normal;
            float4 frag(v2f_img i):SV_Target
            {
                float4 state=tex2D(_MainTex,i.uv);
                float3 positionWS=mul(_LocalToWorld,float4((i.uv.x-.5)*8,0,(i.uv.y-.5)*8,1)).xyz;
                float visible=InkVisible(state.a,InkDetailUV(positionWS,_Normal,.034424),110,.5);
                return float4(state.b*visible,0,0,1);
            }
            ENDHLSL
        }
    }
}
