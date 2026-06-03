#include "ReflectionPayload.hlsl"

// Material inputs (URP Lit naming, bound per-instance through the fallback).
Texture2D _BaseMap;
SamplerState sampler_BaseMap;
float4 _BaseMap_ST;

float4 _BaseColor;
float _Metallic;
float _Smoothness;

// Alpha-clip state, read from material floats at runtime (the _ALPHATEST_ON keyword
// gets stripped on the fallback shader - same reason as in the shadow pass).
float _Cutoff;
float _AlphaClip;

// Interpolated base-map alpha at the hit, for the cutout test.
float GetReflectionSurfaceAlpha(AttributeData attributes)
{
	Vertex v0, v1, v2;
	GetVertexData(v0, v1, v2);

	float2 bc = attributes.barycentrics;
	float3 bcc = float3(1.0 - bc.x - bc.y, bc.x, bc.y);
	float2 texcoord = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.texcoord, v1.texcoord, v2.texcoord, bcc);

	float2 uvBase = TRANSFORM_TEX(texcoord, _BaseMap);
	return _BaseMap.SampleLevel(sampler_BaseMap, uvBase, 0).a * _BaseColor.a;
}

// Cutout for alpha-tested geometry (foliage). Runs per intersection candidate;
// IgnoreHit() lets the reflection/shadow ray pass through transparent texels and
// continue to the real surface behind, instead of reflecting the solid quad.
[shader("anyhit")]
void URP_AnyHit(inout ReflectionPayload payload : SV_RayPayload, AttributeData attributes : SV_IntersectionAttributes)
{
	if (_AlphaClip > 0.5)
	{
		float alpha = GetReflectionSurfaceAlpha(attributes);
		if (alpha < _Cutoff)
		{
			IgnoreHit();
		}
	}
}

// Simple lighting for a surface seen *inside* a reflection. Not a full PBR pass -
// direct main light with ray-traced shadow + SH ambient is enough to read plausibly.
float3 ShadeReflectedSurface(float3 albedo, float3 worldNormal, float3 worldPosition, float bounceCounter)
{
	float3 lightDir = normalize(_MainLightPosition.xyz);
	float ndotl = saturate(dot(worldNormal, lightDir));
	float shadow = GetShadowAttenuation(worldPosition, bounceCounter);

	float3 direct = _MainLightColor.rgb * ndotl * shadow;
	float3 ambient = SampleSH(worldNormal);

	return albedo * (direct + ambient);
}

[shader("closesthit")]
void URP_ClosestHit(inout ReflectionPayload payload : SV_RayPayload, AttributeData attributes : SV_IntersectionAttributes)
{
	// Shadow probe that hit geometry => the point is occluded. Leave color.a = 0.
	if (payload.reflectionRayType == 1)
	{
		return;
	}

	Vertex v0, v1, v2;
	GetVertexData(v0, v1, v2);

	float2 bc = attributes.barycentrics;
	float3 bcc = float3(1.0 - bc.x - bc.y, bc.x, bc.y);

	float3 normalOS = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.normal, v1.normal, v2.normal, bcc);
	float2 texcoord = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.texcoord, v1.texcoord, v2.texcoord, bcc);

	float2 uvBase = TRANSFORM_TEX(texcoord, _BaseMap);
	float3 albedo = (_BaseMap.SampleLevel(sampler_BaseMap, uvBase, 0) * _BaseColor).rgb;

	// Smooth interpolated normal -> world (inverse-transpose via the RT intrinsic).
	float3 worldNormal = normalize(mul(normalOS, (float3x3)WorldToObject3x4()));
	float3 worldPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();

	float metallic = _Metallic;
	float smoothness = _Smoothness;

	// Reflected surfaces (bounce > 0) add their own shaded color. The primary surface
	// (bounce 0) does NOT - its look is already in the rasterized frame; it only acts
	// as a mirror that kicks off the reflection.
	if (payload.bounceCounter > 0.0)
	{
		float3 lit = ShadeReflectedSurface(albedo, worldNormal, worldPosition, payload.bounceCounter);
		payload.color.rgb += lit * payload.rayIntensity;
	}

	float entryBounce = payload.bounceCounter;

	float3 reflDir = normalize(reflect(WorldRayDirection(), worldNormal));
	BounceRay(worldPosition, reflDir, smoothness, metallic, payload);

	if (entryBounce == 0.0)
	{
		// Metals tint the reflection by their albedo; dielectrics reflect neutrally.
		payload.color.rgb *= lerp(float3(1.0, 1.0, 1.0), albedo, metallic);

		// Reflection strength used by the composite (how much to add over the frame).
		payload.color.a = saturate(smoothness * 2.0 - 0.5);
	}
}
