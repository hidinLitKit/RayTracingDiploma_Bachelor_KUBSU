#include "RaytraceData.hlsl"

struct ShadowsPayload
{
	float light;
	float rayIntensity;
	float rayType; // 0 = forward raycasting to find obj, 1 = after forward found something
};

float _RtShadowBias;
float _RaySeparation;
int _ShadowCastCount;

inline float Cast(float3 rayOrigin, float3 rayDirection, inout ShadowsPayload payload)
{
	RayDesc ray;
	ray.Origin = rayOrigin + rayDirection * 0.001;
	ray.Direction = rayDirection;
	ray.TMin = 0.0;
	ray.TMax = _MaxRayDistance;

	payload.light = 1.0;
	payload.rayIntensity = 1.0;
	payload.rayType = 1.0;

	int cast_count = _ShadowCastCount;

	float cast_delta = cast_count / 8.0;
	payload.rayIntensity = 1.0 / max(1, cast_count);

	for (int i = 0; i < cast_count; ++i)
	{
		float3 castOffset = GetRandom3D(rayOrigin, rayDirection, 20, i * 1000);

		if (_ShadowCastCount > 1)
		{
			ray.Direction = normalize(rayDirection + castOffset * _RaySeparation * cast_delta);
			ray.Origin = rayOrigin + ray.Direction * 0.001;
		}

		ShadowsPayload shadowsPayload = payload;
		TraceRay(_RaytracingAccelerationStructure, RAY_FLAG_CULL_BACK_FACING_TRIANGLES, _RaytraceAgainstLayers, 0, 1, 0, ray, shadowsPayload);
		payload = shadowsPayload;
	}

	return saturate(payload.light);
}