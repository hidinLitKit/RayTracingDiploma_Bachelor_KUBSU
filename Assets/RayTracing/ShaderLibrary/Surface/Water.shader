Shader "Akunaki/Water"
{
    Properties
    {
        [Header(Color)]
        [HDR] _ShallowColor("Shallow Color", Color) = (0.1, 0.4, 0.5, 0.6)
        [HDR] _DeepColor("Deep Color", Color) = (0.0, 0.1, 0.2, 0.95)
        _DepthStrength("Depth Strength", Range(0.01, 5)) = 0.5

        [Header(Waves vertex)]
        _Displacement("Displacement", Range(0, 2)) = 0.15
        _NoiseScale("Noise Scale", Float) = 20
        _NoiseScroll("Noise Scroll (xy)", Vector) = (0.01, 0.01, 0, 0)

        [Header(Surface normals)]
        [Normal] _MainNormal("Main Normal", 2D) = "bump" {}
        [Normal] _SecondNormal("Second Normal", 2D) = "bump" {}
        _NormalTiling("Normal Tiling", Float) = 1
        _NormalStrength("Normal Strength", Range(0, 2)) = 1
        _NormalScrollMain("Main Scroll (xy)", Vector) = (0.01, 0.0, 0, 0)
        _NormalScrollSecond("Second Scroll (xy)", Vector) = (-0.02, 0.01, 0, 0)

        [Header(Lighting)]
        _Smoothness("Smoothness", Range(0, 1)) = 0.95
        _Metallic("Metallic", Range(0, 1)) = 0.0

        // --- Read by the RT fallback (reflections / shadow occlusion). Keep these names. ---
        // _MetallicGlossMap MUST default to white: the RT reflection multiplies
        // smoothness by its .a, so an unbound (black) map would zero the reflection.
        [HideInInspector] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _MetallicGlossMap("Metallic Map", 2D) = "white" {}
        [HideInInspector] _Cutoff("Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _AlphaClip("Alpha Clip", Float) = 0
        // 1 = RT reflections use the scrolling normal maps above (rippled reflection).
        [HideInInspector] _RtSurfaceNormalMode("RT Surface Normal Mode", Float) = 1
        // Reflection ripple is usually subtler than the surface lighting ripple.
        _RtReflectionNormalScale("RT Reflection Ripple", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_MainNormal);   SAMPLER(sampler_MainNormal);
            TEXTURE2D(_SecondNormal); SAMPLER(sampler_SecondNormal);

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float _DepthStrength;
                float _Displacement;
                float _NoiseScale;
                float4 _NoiseScroll;
                float _NormalTiling;
                float _NormalStrength;
                float4 _NormalScrollMain;
                float4 _NormalScrollSecond;
                float _Smoothness;
                float _Metallic;
                float4 _BaseColor;
                float _Cutoff;
                float _AlphaClip;
            CBUFFER_END

            // Gradient noise (matches Shader Graph's Gradient Noise node).
            float2 GradientNoiseDir(float2 p)
            {
                p = p % 289.0;
                float x = (34.0 * p.x + 1.0) * p.x % 289.0 + p.y;
                x = (34.0 * x + 1.0) * x % 289.0;
                x = frac(x / 41.0) * 2.0 - 1.0;
                return normalize(float2(x - floor(x + 0.5), abs(x) - 0.5));
            }

            float GradientNoise(float2 uv, float scale)
            {
                float2 p = uv * scale;
                float2 ip = floor(p);
                float2 fp = frac(p);
                float d00 = dot(GradientNoiseDir(ip), fp);
                float d01 = dot(GradientNoiseDir(ip + float2(0, 1)), fp - float2(0, 1));
                float d10 = dot(GradientNoiseDir(ip + float2(1, 0)), fp - float2(1, 0));
                float d11 = dot(GradientNoiseDir(ip + float2(1, 1)), fp - float2(1, 1));
                float2 w = fp * fp * fp * (fp * (fp * 6.0 - 15.0) + 10.0);
                return lerp(lerp(d00, d10, w.x), lerp(d01, d11, w.x), w.y) + 0.5;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float4 tangentWS  : TEXCOORD3; // xyz tangent, w sign
                float4 screenPos  : TEXCOORD4;
                float  fogCoord   : TEXCOORD5;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                // Vertical wave displacement from scrolling gradient noise.
                float n = GradientNoise(IN.uv + _Time.y * _NoiseScroll.xy, _NoiseScale);
                float3 positionOS = IN.positionOS.xyz;
                positionOS.y += n * _Displacement;

                VertexPositionInputs posIn = GetVertexPositionInputs(positionOS);
                VertexNormalInputs normIn = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = posIn.positionCS;
                OUT.positionWS = posIn.positionWS;
                OUT.normalWS = normIn.normalWS;
                OUT.tangentWS = float4(normIn.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv = IN.uv;
                OUT.screenPos = ComputeScreenPos(posIn.positionCS);
                OUT.fogCoord = ComputeFogFactor(posIn.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // --- Surface normal: two scrolling normal maps blended ---
                float2 uv1 = IN.uv * _NormalTiling + _Time.y * _NormalScrollMain.xy;
                float2 uv2 = IN.uv * _NormalTiling + _Time.y * _NormalScrollSecond.xy;
                half3 n1 = UnpackNormal(SAMPLE_TEXTURE2D(_MainNormal, sampler_MainNormal, uv1));
                half3 n2 = UnpackNormal(SAMPLE_TEXTURE2D(_SecondNormal, sampler_SecondNormal, uv2));
                half3 normalTS = half3(n1.xy + n2.xy, n1.z * n2.z);
                normalTS.xy *= _NormalStrength;
                normalTS = normalize(normalTS);

                float3 bitangent = IN.tangentWS.w * cross(IN.normalWS, IN.tangentWS.xyz);
                float3x3 tbn = float3x3(IN.tangentWS.xyz, bitangent, IN.normalWS);
                float3 normalWS = normalize(mul(normalTS, tbn));

                // --- Depth-based shallow/deep blend ---
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float surfaceEye = IN.screenPos.w;
                float waterDepth = saturate((sceneEye - surfaceEye) * _DepthStrength);

                float4 waterCol = lerp(_ShallowColor, _DeepColor, waterDepth);

                // --- Lighting (RT reflections are added later by the composite) ---
                float3 viewDir = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                float ndotl = saturate(dot(normalWS, mainLight.direction));
                float3 ambient = SampleSH(normalWS);
                float3 lit = waterCol.rgb * (mainLight.color * (ndotl * mainLight.shadowAttenuation) + ambient);

                // Sun glint
                float3 halfDir = SafeNormalize(mainLight.direction + viewDir);
                float specPower = exp2(_Smoothness * 11.0) + 2.0;
                float spec = pow(saturate(dot(normalWS, halfDir)), specPower);
                lit += mainLight.color * spec * mainLight.shadowAttenuation;

                // Fresnel boosts edge opacity (grazing water is more reflective/opaque)
                float fresnel = pow(1.0 - saturate(dot(normalWS, viewDir)), 5.0);
                float alpha = saturate(waterCol.a + fresnel);

                lit = MixFog(lit, IN.fogCoord);
                return half4(lit, alpha);
            }
            ENDHLSL
        }
    }

    // Picks up "RaytraceShadowsPass" / "RaytraceReflectionPass" -> RT shadows + reflections.
    FallBack "Raytracing/URPFallbackRaytrace"
}
