#include "RaytraceData.hlsl"

// Payload carried along reflection rays.
struct ReflectionPayload
{
	float4 color;            // accumulated reflected radiance; .a = reflection strength (set at the primary surface)
	float  bounceCounter;    // 0 = primary camera ray, 1 = first reflection, ...
	float  rayIntensity;     // weight of the current contribution (driven by gloss)
	int    reflectionRayType; // 0 = reflection ray, 1 = shadow probe inside a reflection
};

// Quality knobs, set from RaytraceReflectionsPass.
float _MaxReflectionBounces; // recursion cap for reflections
float _BounceFalloff;        // 1 / (bounces + 1); fades skybox with depth
int   _MaxRoughnessRays;     // max rays in the glossy cone
float _ShadowBounceCap;      // stop casting shadow probes past this bounce

// Returns 1 if the point is lit by the main light, 0 if occluded.
// Cheap: a single ray toward the directional light. reflectionRayType = 1 tells the
// closest-hit "this is only a visibility probe, don't shade".
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

	TraceRay(_RaytracingAccelerationStructure, _RayFlags, _RaytraceAgainstLayers, 0, 1, 0, ray, probe);

	// miss shader writes a = 1 ("reached the light"); a hit returns leaving a = 0.
	return probe.color.a;
}

// Spawns reflection rays in a cone whose width grows with roughness, then averages
// them. A mirror casts 1 sharp ray; a rough surface casts many spread rays (blur).
// Recurses through the closest-hit up to _MaxReflectionBounces.
inline void BounceRay(float3 worldPosition, float3 rayDirection, float gloss, float metallic, inout ReflectionPayload payload)
{
	payload.bounceCounter += 1.0;

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
		}

		averageColor /= ray_count;
		payload.color = averageColor;
	}
}
