Shader "Hidden/Akunaki/ApplyReflections"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            o.uv = GetFullScreenTriangleTexCoord(vertexID);
            return o;
        }
        ENDHLSL

        // Pass 0 - Grab: snapshot the current camera color into a temp target
        // (can't read + write the same target in one draw).
        Pass
        {
            Name "Grab"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_CopyBlitTex);
            SAMPLER(sampler_CopyBlitTex);

            float4 Frag(Varyings i) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_CopyBlitTex, sampler_CopyBlitTex, i.uv);
            }
            ENDHLSL
        }

        // Pass 1 - Apply: composite reflections over the snapshot, write back to camera.
        Pass
        {
            Name "Apply"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _REFLECTIONS_REPLACE

            TEXTURE2D(_ReflectionsGrabpass);
            SAMPLER(sampler_ReflectionsGrabpass);

            TEXTURE2D(_RTXReflectionsTex);
            SAMPLER(sampler_RTXReflectionsTex);

            float4 Frag(Varyings i) : SV_Target
            {
                float4 baseColor = SAMPLE_TEXTURE2D(_ReflectionsGrabpass, sampler_ReflectionsGrabpass, i.uv);
                float4 reflection = SAMPLE_TEXTURE2D(_RTXReflectionsTex, sampler_RTXReflectionsTex, i.uv);

            #ifdef _REFLECTIONS_REPLACE
                // Mirror-like: replace the surface with the reflection by its strength.
                baseColor.rgb = lerp(baseColor.rgb, reflection.rgb, saturate(reflection.a));
            #else
                // Glossy add-on: reflection added on top, weighted by strength.
                baseColor.rgb += reflection.rgb * reflection.a;
            #endif

                return baseColor;
            }
            ENDHLSL
        }
    }
}
