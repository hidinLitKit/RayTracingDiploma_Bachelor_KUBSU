#ifndef RT_LIT_FORWARD_PASS_INCLUDED
#define RT_LIT_FORWARD_PASS_INCLUDED

// Reuse URP's vertex stage, Varyings, InitializeInputData / InitializeBakedGIData,
// and all lighting helpers. We only override the fragment so we can fold the
// ray-traced shadow into the MAIN LIGHT attenuation (not into ambient / SSAO).
#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"

// Filtered ray-traced shadow map, bound globally by RaytraceShadowsPass.
// R = shadow factor in [0,1] (1 = lit), G = view depth (euclidean dist from camera).
TEXTURE2D(_RaytracingShadowMap);
float4 _RaytracingShadowMap_TexelSize;

// Depth-aware (bilateral) upsample of the low-res shadow map.
half SampleRtShadow(float2 screenUV, float3 positionWS)
{
    float fragDepth = distance(positionWS, _WorldSpaceCameraPos);
    float2 texel = _RaytracingShadowMap_TexelSize.xy;
    float tol = max(0.1, 0.1 * fragDepth);

    float sum = 0.0;
    float wsum = 0.0;

    [unroll] for (int y = 0; y < 2; ++y)
    [unroll] for (int x = 0; x < 2; ++x)
    {
        float2 o = (float2(x, y) - 0.5) * texel;
        float2 s = SAMPLE_TEXTURE2D(_RaytracingShadowMap, sampler_LinearClamp, screenUV + o).rg;
        float w = saturate(1.0 - abs(s.g - fragDepth) / tol);
        sum += s.r * w;
        wsum += w;
    }

    return (wsum > 1e-4)
        ? sum / wsum
        : SAMPLE_TEXTURE2D(_RaytracingShadowMap, sampler_LinearClamp, screenUV).r;
}

// ---------------------------------------------------------------------------
// Copy of URP's UniversalFragmentPBR(InputData, SurfaceData)
// (com.unity.render-pipelines.universal@4976252adeb8, Lighting.hlsl) with a
// single addition: the ray-traced shadow multiplies the main light's
// shadowAttenuation. Keep in sync if the URP version changes.
// ---------------------------------------------------------------------------
half4 UniversalFragmentPBR_RtShadow(InputData inputData, SurfaceData surfaceData, half rtShadow)
{
    #if defined(_SPECULARHIGHLIGHTS_OFF)
    bool specularHighlightsOff = true;
    #else
    bool specularHighlightsOff = false;
    #endif
    BRDFData brdfData;

    InitializeBRDFData(surfaceData, brdfData);

    #if defined(DEBUG_DISPLAY)
    half4 debugColor;

    if (CanDebugOverrideOutputColor(inputData, surfaceData, brdfData, debugColor))
    {
        return debugColor;
    }
    #endif

    BRDFData brdfDataClearCoat = CreateClearCoatBRDFData(surfaceData, brdfData);
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    uint meshRenderingLayers = GetMeshRenderingLayer();

    // >>> Ray-traced shadow injection.
    // ambient / GI / additional lights are unaffected.
    Light mainLight = GetMainLight();
#if !defined(_RECEIVE_SHADOWS_OFF)
    mainLight.shadowAttenuation = rtShadow;
#endif
    // <<<

    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

    LightingData lightingData = CreateLightingData(inputData, surfaceData);

    lightingData.giColor = GlobalIllumination(brdfData, brdfDataClearCoat, surfaceData.clearCoatMask,
                                              inputData.bakedGI, aoFactor.indirectAmbientOcclusion, inputData.positionWS,
                                              inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
    {
        lightingData.mainLightColor = LightingPhysicallyBased(brdfData, brdfDataClearCoat,
                                                              mainLight,
                                                              inputData.normalWS, inputData.viewDirectionWS,
                                                              surfaceData.clearCoatMask, specularHighlightsOff);
    }

    #if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();

    #if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK

        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                                                                          inputData.normalWS, inputData.viewDirectionWS,
                                                                          surfaceData.clearCoatMask, specularHighlightsOff);
        }
    }
    #endif

    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                                                                          inputData.normalWS, inputData.viewDirectionWS,
                                                                          surfaceData.clearCoatMask, specularHighlightsOff);
        }
    LIGHT_LOOP_END
    #endif

    #if defined(_ADDITIONAL_LIGHTS_VERTEX)
    lightingData.vertexLightingColor += inputData.vertexLighting * brdfData.diffuse;
    #endif

#if REAL_IS_HALF
    return min(CalculateFinalColor(lightingData, surfaceData.alpha), HALF_MAX);
#else
    return CalculateFinalColor(lightingData, surfaceData.alpha);
#endif
}

// Copy of URP's LitPassFragment, calling our RT-shadow-aware shading.
void LitPassFragmentRT(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS = input.viewDirTS;
#else
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
#endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);

    half rtShadow = SampleRtShadow(inputData.normalizedScreenSpaceUV, inputData.positionWS);

    half4 color = UniversalFragmentPBR_RtShadow(inputData, surfaceData, rtShadow);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
