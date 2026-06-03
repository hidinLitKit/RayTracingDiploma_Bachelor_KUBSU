Shader "Raytracing/URPFallbackRaytrace"
{
    SubShader
    {


        Pass
        {
            Name "RaytraceShadowsPass"

            HLSLPROGRAM

            #pragma raytracing ClosestHit_Shadows
            #include "../Includes/RaytraceShadowsHitPass.hlsl"

            ENDHLSL
        }

        Pass
        {
            Name "RaytraceReflectionPass"

            HLSLPROGRAM

            #pragma raytracing URP_ClosestHit
            #include "../Includes/RaytraceReflectionHitPass.hlsl"

            ENDHLSL
        }


    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
