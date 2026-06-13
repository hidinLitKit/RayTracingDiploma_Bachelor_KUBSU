#include "ReflectionPayload.hlsl"

Texture2D _BaseMap;
SamplerState sampler_BaseMap;
float4 _BaseMap_ST;

float4 _BaseColor;
float _Metallic;
float _Smoothness;

Texture2D _MetallicGlossMap;
SamplerState sampler_MetallicGlossMap;
float4 _MetallicGlossMap_ST;

float _Cutoff;
float _AlphaClip;

float _AnyHitFarCutout; // accept foliage past this ray distance as opaque (skip the texel test). 0 = always test
float _AnyHitAlphaMip;  // mip for the cutout alpha sample. 0 = full res


float3 ApplyReflectionFog(float3 color, float dist)
{
	float f = unity_FogParams.x * dist;
	float intensity = saturate(exp2(-f * f));
	return color * intensity;
}

// Interpolated base-map alpha at the hit, for the cutout test.
float GetReflectionSurfaceAlpha(AttributeData attributes)
{
	Vertex v0, v1, v2;
	GetVertexData(v0, v1, v2);

	float2 bc = attributes.barycentrics;
	float3 bcc = float3(1.0 - bc.x - bc.y, bc.x, bc.y);
	float2 texcoord = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.texcoord, v1.texcoord, v2.texcoord, bcc);

	float2 uvBase = TRANSFORM_TEX(texcoord, _BaseMap);
	return _BaseMap.SampleLevel(sampler_BaseMap, uvBase, _AnyHitAlphaMip).a * _BaseColor.a;
}

// Cutout for alpha-tested geometry
[shader("anyhit")]
void AnyHit_Reflections(inout ReflectionPayload payload : SV_RayPayload, AttributeData attributes : SV_IntersectionAttributes)
{
	if (_AlphaClip > 0.5)
	{
		// distant foliage: accept the hit as opaque and stop
		if (_AnyHitFarCutout > 0.0 && RayTCurrent() > _AnyHitFarCutout)
		{
			return;
		}

		float alpha = GetReflectionSurfaceAlpha(attributes);
		if (alpha < _Cutoff)
		{
			IgnoreHit();
		}
	}
}

// simple lighting for a surface seen inside a reflection
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
void ClosestHit_Reflections(inout ReflectionPayload payload : SV_RayPayload, AttributeData attributes : SV_IntersectionAttributes)
{
	// sshadow probe that hit geometry => the point is occluded. Leave color.a = 0.
	if (payload.reflectionRayType == 1)
	{
		return;
	}

	// first reflected hit: record its distance for temporal reprojection
	if (payload.bounceCounter == 1.0)
	{
		payload.reflRayT = RayTCurrent();
	}

	float surfaceDist = RayTCurrent();

	Vertex v0, v1, v2;
	GetVertexData(v0, v1, v2);

	float2 bc = attributes.barycentrics;
	float3 bcc = float3(1.0 - bc.x - bc.y, bc.x, bc.y);

	float3 normalOS = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.normal, v1.normal, v2.normal, bcc);
	float2 texcoord = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.texcoord, v1.texcoord, v2.texcoord, bcc);

	float2 uvBase = TRANSFORM_TEX(texcoord, _BaseMap);
	float3 albedo = (_BaseMap.SampleLevel(sampler_BaseMap, uvBase, 0) * _BaseColor).rgb;

	// smooth interpolated normal -> world
	float3 worldNormal = normalize(mul(normalOS, (float3x3)WorldToObject3x4()));
	float3 worldPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
	if (dot(worldNormal, -WorldRayDirection()) < 0.0)
	{
		worldNormal = -worldNormal;
	}

	// metallic from map.r, smoothness from map.a
	float2 uvMetallicGloss = TRANSFORM_TEX(texcoord, _MetallicGlossMap);
	float4 metallicGloss = _MetallicGlossMap.SampleLevel(sampler_MetallicGlossMap, uvMetallicGloss, 0);
	float metallic = metallicGloss.r * _Metallic;
	float smoothness = metallicGloss.a * _Smoothness;

	// reflected surfaces (bounce > 0) add their own shaded color
	if (payload.bounceCounter > 0.0)
	{
		float3 lit = ShadeReflectedSurface(albedo, worldNormal, worldPosition, payload.bounceCounter);
		lit = ApplyReflectionFog(lit, RayTCurrent());
		payload.color.rgb += lit * payload.rayIntensity;
	}

	float entryBounce = payload.bounceCounter;
	float3 reflDir = normalize(reflect(WorldRayDirection(), worldNormal));

	if (entryBounce == 0.0)
	{
		// schlick reflectance approximation
		float3 V = -WorldRayDirection();
		float NdotV = saturate(dot(worldNormal, V));
		float F0 = lerp(0.04, 1.0, metallic);
		float fresnel = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);

		if (fresnel < 0.01)
		{
			payload.color = float4(0.0, 0.0, 0.0, 0.0);
			return;
		}

		BounceRay(worldPosition, reflDir, smoothness, metallic, payload);

		//  unfold the reflected ray into a straight view ray (surface + first reflected hit)
		// so the raygen can reproject it as virtual geometry for temporal accumulation.
		payload.reflRayT += surfaceDist;

		// Metals tint the reflection by their albedo; dielectrics reflect neutrally.
		payload.color.rgb *= lerp(float3(1.0, 1.0, 1.0), albedo, metallic);
		payload.color.a = fresnel;
	}
	else
	{
		BounceRay(worldPosition, reflDir, smoothness, metallic, payload);
	}
}
