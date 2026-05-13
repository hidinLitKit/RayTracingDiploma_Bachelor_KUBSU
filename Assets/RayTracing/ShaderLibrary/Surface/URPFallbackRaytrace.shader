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


    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
