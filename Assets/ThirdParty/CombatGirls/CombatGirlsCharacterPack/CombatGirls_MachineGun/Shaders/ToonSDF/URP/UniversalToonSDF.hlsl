#ifndef UNIVERSAL_TOON_SDF_INCLUDED
#define UNIVERSAL_TOON_SDF_INCLUDED

float3 SafeNormalizeFaceSDF(float3 value, float3 fallback)
{
    return dot(value, value) > 0.000001 ? normalize(value) : normalize(fallback);
}

float ComputeFaceSDFShadowMask(float2 uv, float3 lightDirection, float attenuation, out float4 debugValues)
{
    float FdotLHorizontal = 0.0;
    float RdotLHorizontal = 0.0;

    if (_UseScriptVectors > 0.5)
    {
        float3 faceForward = SafeNormalizeFaceSDF(_FaceForward.xyz, float3(0.0, 0.0, 1.0));
        float3 faceRight = SafeNormalizeFaceSDF(_FaceRight.xyz, float3(1.0, 0.0, 0.0));
        float2 lightDir2D = float2(dot(faceRight, lightDirection), dot(faceForward, lightDirection));
        float lightDirLength = length(lightDir2D);
        lightDir2D = lightDirLength > 0.0001 ? lightDir2D / lightDirLength : float2(0.0, 1.0);
        RdotLHorizontal = lightDir2D.x;
        FdotLHorizontal = lightDir2D.y;
    }
    else
    {
        float3 objectSpaceLightDir = SafeNormalizeFaceSDF(
            mul((float3x3)GetWorldToObjectMatrix(), lightDirection), float3(0.0, 0.0, 1.0));
        float2 lightDir2D = objectSpaceLightDir.xz;
        float lightDirLength = length(lightDir2D);
        lightDir2D = lightDirLength > 0.0001 ? lightDir2D / lightDirLength : float2(0.0, 1.0);
        RdotLHorizontal = lightDir2D.x;
        FdotLHorizontal = lightDir2D.y;
    }

    float2 faceShadowUV = TRANSFORM_TEX(uv, _FaceShadowMap);
    float sdfValue = tex2D(_FaceShadowMap, faceShadowUV).r;

    if (_MirrorFaceShadowByLight > 0.5)
    {
        float2 mirroredUV = TRANSFORM_TEX(float2(1.0 - uv.x, uv.y), _FaceShadowMap);
        float mirroredSDFValue = tex2D(_FaceShadowMap, mirroredUV).r;
        float mirrorBlend = smoothstep(-_FaceShadowMirrorBlend, _FaceShadowMirrorBlend, RdotLHorizontal);
        sdfValue = lerp(mirroredSDFValue, sdfValue, mirrorBlend);
    }

    float shadowThreshold = saturate((0.5 - FdotLHorizontal * 0.5) + _FaceShadowOffset);
    float sdfShadowMask = 1.0 - smoothstep(
        shadowThreshold - _FaceShadowSoftness,
        shadowThreshold + _FaceShadowSoftness,
        sdfValue);

    float fixedShadowMask = tex2D(_FaceShadowMaskMap, TRANSFORM_TEX(uv, _FaceShadowMaskMap)).r;
    fixedShadowMask = saturate(fixedShadowMask * _FaceShadowMaskStrength);

    if (_Set_SystemShadowsToBase > 0.5)
    {
        sdfShadowMask = max(sdfShadowMask, 1.0 - attenuation);
    }

    float frontSuppress = smoothstep(_SDFFrontShadowSuppressStart, _SDFFrontShadowSuppressEnd, FdotLHorizontal);
    sdfShadowMask *= (1.0 - frontSuppress);
    sdfShadowMask = max(sdfShadowMask, fixedShadowMask);
    sdfShadowMask = saturate(sdfShadowMask);

    debugValues = float4(FdotLHorizontal, RdotLHorizontal, frontSuppress, fixedShadowMask);
    return sdfShadowMask;
}

float GetFaceSDFAdditionalLightMask(float2 uv, float3 lightDirection)
{
    float4 unusedDebugValues = float4(0.0, 0.0, 0.0, 0.0);
    if (_UseSDFShadow > 0.5)
    {
        return 1.0 - ComputeFaceSDFShadowMask(uv, lightDirection, 1.0, unusedDebugValues);
    }

    return 1.0;
}

float3 ApplyFaceSDFDebugColor(float3 color, float4 debugValues, float finalShadowMask)
{
    if (_UseSDFShadow <= 0.5 || _SDFDebugMode <= 0.5)
    {
        return color;
    }

    float debugValue = finalShadowMask;
    if (_SDFDebugMode < 1.5)
    {
        debugValue = saturate(debugValues.x * 0.5 + 0.5);
    }
    else if (_SDFDebugMode < 2.5)
    {
        debugValue = saturate(debugValues.y * 0.5 + 0.5);
    }
    else if (_SDFDebugMode < 3.5)
    {
        debugValue = saturate(debugValues.z);
    }
    else if (_SDFDebugMode < 4.5)
    {
        debugValue = saturate(finalShadowMask);
    }
    else
    {
        debugValue = saturate(debugValues.w);
    }

    return float3(debugValue, debugValue, debugValue);
}

#endif
