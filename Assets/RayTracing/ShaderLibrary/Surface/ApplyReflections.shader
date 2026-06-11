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

		// Pass 1 - Apply: depth-aware (joint-bilateral) upsample of the low-res reflection
		Pass
		{
			Name "Apply"
			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			TEXTURE2D(_ReflectionsGrabpass);
			SAMPLER(sampler_ReflectionsGrabpass);

			TEXTURE2D(_RTXReflectionsTex);
			SAMPLER(sampler_RTXReflectionsTex);
			float4 _RTXReflectionsTex_TexelSize;

			TEXTURE2D(_CameraDepthTexture);
			SAMPLER(sampler_CameraDepthTexture);
			SAMPLER(sampler_LinearClamp);

			float4x4 _InverseProjectionMatrix;

			float SceneEuclidDepth(float2 uv)
			{
				float raw = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r;
				float4 clip = float4(uv * 2.0 - 1.0, raw, 1.0);
				float4 vp = mul(_InverseProjectionMatrix, clip);
				return length(vp.xyz / vp.w);
			}

			float4 Frag(Varyings i) : SV_Target
			{
				float4 baseColor = SAMPLE_TEXTURE2D(_ReflectionsGrabpass, sampler_ReflectionsGrabpass, i.uv);

				float sceneD = SceneEuclidDepth(i.uv);
				float tol = max(0.1, 0.1 * sceneD);

				float2 texel = _RTXReflectionsTex_TexelSize.xy;
				float4 sum = 0.0;
				float wsum = 0.0;

				// Bilinear taps weighted by depth match at each tap.
				[unroll] for (int y = 0; y < 2; ++y)
				[unroll] for (int x = 0; x < 2; ++x)
				{
					float2 o = (float2(x, y) - 0.5) * texel;
					float dTap = SceneEuclidDepth(i.uv + o);
					float w = saturate(1.0 - abs(dTap - sceneD) / tol);
					sum += SAMPLE_TEXTURE2D(_RTXReflectionsTex, sampler_LinearClamp, i.uv + o) * w;
					wsum += w;
				}

				float4 reflection = (wsum > 1e-4)
					? sum / wsum
					: SAMPLE_TEXTURE2D(_RTXReflectionsTex, sampler_LinearClamp, i.uv);

				baseColor.rgb += reflection.rgb * reflection.a;

				return baseColor;
			}
			ENDHLSL
		}
	}
}
