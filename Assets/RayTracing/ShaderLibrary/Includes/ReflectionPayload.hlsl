#include "RaytraceData.hlsl"

struct ReflectionPayload
{
	float4 color;            // accumulated reflected radiance; .a = reflection strength (set at the primary surface)
	float  bounceCounter;    // 0 = primary camera ray, 1 = first reflection, etc.
	float  rayIntensity;     // weight of the current contribution (driven by gloss)
	int    reflectionRayType; // 0 = reflection ray, 1 = shadow probe inside a reflection
	float  reflRayT;         // unfolded reflection path length (surface + first reflected hit), for temporal reprojection
};

float _MaxReflectionBounces;
float _BounceFalloff;
int   _MaxRoughnessRays;
float _ShadowBounceCap;


inline float GetShadowAttenuation(float3 worldPos, float bounceCount)
{
	if (bounceCount >= _ShadowBounceCap)
	{
		return 1.0;
	}

	RayDesc ray;
	ray.Direction = normalize(_MainLightPosition.xyz);
	ray.Origin = worldPos + ray.Direction * 0.001;
	ray.TMin = 0.001;
	ray.TMax = _MaxRayDistance;

	ReflectionPayload probe;
	probe.color = float4(0, 0, 0, 0);
	probe.bounceCounter = bounceCount;
	probe.rayIntensity = 1.0;
	probe.reflectionRayType = 1;

	TraceRay(_RaytracingAccelerationStructure,
		RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
		_RaytraceAgainstLayers, 0, 1, 0, ray, probe);

	return probe.color.a;
}


// Recurses through the closest-hit up to _MaxReflectionBounces.
inline void BounceRay(float3 worldPosition, float3 rayDirection, float gloss, float metallic, inout ReflectionPayload payload)
{
	payload.bounceCounter += 1.0;
	bool recordHit = (payload.bounceCounter == 1.0);

	if (payload.bounceCounter < _MaxReflectionBounces && gloss > 0.0125)
	{
		int ray_count = max((int)(_MaxRoughnessRays * saturate(1.0 - gloss)), 1);

		// gloss drives how strongly this reflection contributes downstream.
		payload.rayIntensity = saturate(gloss * 2.0 - 0.5);

		float4 averageColor = float4(0, 0, 0, 0);

		for (int i = 0; i < ray_count; ++i)
		{
			float3 rOffset = GetRandom3D(worldPosition, rayDirection, 10, i + payload.bounceCounter);

			RayDesc ray;
			ray.TMin = 0.0;
			ray.TMax = _MaxRayDistance;
			ray.Direction = normalize(rayDirection + rOffset * (0.05 * saturate(1.0 - gloss)));
			ray.Origin = worldPosition + ray.Direction * 0.001;

			ReflectionPayload bounce = payload;
			TraceRay(_RaytracingAccelerationStructure, _RayFlags, _RaytraceAgainstLayers, 0, 1, 0, ray, bounce);

			averageColor += bounce.color;

			if (recordHit && i == 0)
			{
				payload.reflRayT = bounce.reflRayT;
			}
		}

		averageColor /= ray_count;
		payload.color = averageColor;
	}
}
