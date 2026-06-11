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

            #pragma raytracing ClosestHit_Reflections
            #include "../Includes/RaytraceReflectionHitPass.hlsl"

            ENDHLSL
        }


    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
