#include "RaytraceData.hlsl"

struct ShadowsPayload
{
	float light;
	float rayIntensity;
	float rayType; // 0 = forward raycasting to find obj, 1 = after forward found something
	float blockerDistSum; // sum of receiver->blocker distances, weighted by opacity
	float blockerCount;   // number of (opacity-weighted) blocker hits
	float penumbra;       // estimated penumbra radius in pixels (filled at the receiver)
	float viewDepth;      // camera->receiver distance, used for the bilateral blur
};

float _RtShadowBias;
int _ShadowCastCount;
float _LightAngularRadius; // half-angle of the light cone, radians
float _ProjScaleY;         // height / (2 * tan(fovY/2)) — world->pixels at unit distance

uint PcgHash(uint v)
{
	uint state = v * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return (word >> 22u) ^ word;
}

float UintToUnitFloat(uint x)
{
	return float(x) * (1.0 / 4294967296.0); // -> [0, 1)
}

// Shirley concentric mapping: unit square -> unit disk (uniform, low distortion).
float2 ConcentricSampleDisk(float u1, float u2)
{
	float ox = 2.0 * u1 - 1.0;
	float oy = 2.0 * u2 - 1.0;
	if (ox == 0.0 && oy == 0.0)
	{
		return float2(0.0, 0.0);
	}

	float r, theta;
	if (abs(ox) > abs(oy))
	{
		r = ox;
		theta = (PI / 4.0) * (oy / ox);
	}
	else
	{
		r = oy;
		theta = (PI / 2.0) - (PI / 4.0) * (ox / oy);
	}

	return r * float2(cos(theta), sin(theta));
}

void BuildTangentBasis(float3 n, out float3 t, out float3 b)
{
	float3 up = abs(n.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
	t = normalize(cross(up, n));
	b = cross(n, t);
}

inline float Cast(float3 rayOrigin, float3 rayDirection, inout ShadowsPayload payload)
{
	RayDesc ray;
	ray.Origin = rayOrigin + rayDirection;
	ray.Direction = rayDirection;
	ray.TMin = 0.0;
	ray.TMax = _MaxRayDistance;

	int cast_count = max(1, _ShadowCastCount);

	payload.light = 0.0;
	payload.rayIntensity = 1.0 / cast_count;
	payload.rayType = 1.0;
	payload.blockerDistSum = 0.0;
	payload.blockerCount = 0.0;

	float coneSpread = tan(_LightAngularRadius);
	float3 tangent, bitangent;
	BuildTangentBasis(rayDirection, tangent, bitangent);

	uint2 pixel = DispatchRaysIndex().xy;
	// Per-frame term so the cone samples differ each frame -> temporal accumulation
	// converges to more effective samples (lets shadowCastCount drop).
	uint baseSeed = PcgHash(pixel.x + PcgHash(pixel.y) + (uint)_FrameIndex * 9781u);

	for (int i = 0; i < cast_count; ++i)
	{
		if (cast_count > 1)
		{
			uint s = PcgHash(baseSeed + (uint)i);
			float u1 = UintToUnitFloat(s);
			float u2 = UintToUnitFloat(PcgHash(s));
			float2 disk = ConcentricSampleDisk(u1, u2);

			float3 dir = normalize(rayDirection + (tangent * disk.x + bitangent * disk.y) * coneSpread);
			ray.Direction = dir;
			ray.Origin = rayOrigin + dir * 0.001;
		}

		ShadowsPayload shadowsPayload = payload;
		TraceRay(_RaytracingAccelerationStructure, RAY_FLAG_NONE, _RaytraceAgainstLayers, 0, 1, 0, ray, shadowsPayload);
		payload = shadowsPayload;
	}

	return saturate(payload.light);
}