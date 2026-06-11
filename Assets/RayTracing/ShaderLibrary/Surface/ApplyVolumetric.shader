Shader "Hidden/Akunaki/ApplyVolumetric"
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

        // Pass 0 - Grab: snapshot the camera color (can't read + write the same target).
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

        // Pass 1 - Apply: depth-aware (bilateral) upsample of the low-res fog, then add it.
        Pass
        {
            Name "Apply"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_VolumetricsGrabpass);
            SAMPLER(sampler_VolumetricsGrabpass);

            TEXTURE2D(_RTXVolumetricsTex);
            SAMPLER(sampler_RTXVolumetricsTex);
            float4 _RTXVolumetricsTex_TexelSize; // (1/w, 1/h, w, h) of the low-res fog

            TEXTURE2D(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            // Set globally by RaytracePreparePass (GPU projection inverse).
            float4x4 _InverseProjectionMatrix;

            // Euclidean distance from camera (matches the .a stored in the fog buffer).
            // In view space the camera is the origin, so |viewPos| = that distance.
            float SceneEuclidDepth(float2 uv)
            {
                float raw = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r;
                float4 clip = float4(uv * 2.0 - 1.0, raw, 1.0);
                float4 vp = mul(_InverseProjectionMatrix, clip);
                return length(vp.xyz / vp.w);
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float4 baseColor = SAMPLE_TEXTURE2D(_VolumetricsGrabpass, sampler_VolumetricsGrabpass, i.uv);

                float sceneD = SceneEuclidDepth(i.uv);
                float tol = max(0.1, 0.1 * sceneD); // relative depth tolerance

                // 4-tap joint-bilateral upsample: weight low-res fog taps by depth match
                // so the fog doesn't bleed past geometry silhouettes when upscaled.
                float2 texel = _RTXVolumetricsTex_TexelSize.xy;
                float3 sum = 0.0;
                float wsum = 0.0;

                [unroll] for (int y = 0; y < 2; ++y)
                [unroll] for (int x = 0; x < 2; ++x)
                {
                    float2 o = (float2(x, y) - 0.5) * texel;
                    float4 f = SAMPLE_TEXTURE2D(_RTXVolumetricsTex, sampler_RTXVolumetricsTex, i.uv + o);
                    float w = saturate(1.0 - abs(f.a - sceneD) / tol);
                    sum += f.rgb * w;
                    wsum += w;
                }

                float3 fog = (wsum > 1e-4)
                    ? sum / wsum
                    : SAMPLE_TEXTURE2D(_RTXVolumetricsTex, sampler_RTXVolumetricsTex, i.uv).rgb;

                baseColor.rgb += fog;
                return baseColor;
            }
            ENDHLSL
        }
    }
}
