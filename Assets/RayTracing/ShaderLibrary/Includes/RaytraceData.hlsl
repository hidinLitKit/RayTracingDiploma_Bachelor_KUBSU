#include "UnityRaytracingMeshUtils.cginc"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

#define RAYDATAHELPERSINCLUDED 1

#define INTERPOLATE_RAYTRACING_ATTRIBUTE(A0, A1, A2, BARYCENTRIC_COORDINATES) (A0 * BARYCENTRIC_COORDINATES.x + A1 * BARYCENTRIC_COORDINATES.y + A2 * BARYCENTRIC_COORDINATES.z)

#if !defined(TRANSFORM_TEX)
	#define TRANSFORM_TEX_ST(tex,namest) (tex.xy * namest.xy + namest.zw)
	#define TRANSFORM_TEX(tex,name) TRANSFORM_TEX_ST(tex, name##_ST)
#endif

RaytracingAccelerationStructure _RaytracingAccelerationStructure : register(t0);
float4x4 _InverseProjectionMatrix;
uint _RayFlags;
uint _RaytraceAgainstLayers;
float _MaxRayDistance;

struct AttributeData
{
	float2 barycentrics;
	
#ifdef RAYTRACEPROCEDURAL
	float3 normalOS;
#endif
};

struct Vertex
{
	float3 position;
	float3 normal;
	float2 texcoord;
};

Vertex FetchVertex(uint vertexIndex)
{
	Vertex v;
	v.position = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributePosition);
	v.normal = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributeNormal);
	v.texcoord = UnityRayTracingFetchVertexAttribute2(vertexIndex, kVertexAttributeTexCoord0);
	return v;
}

// helper functions 
float GetAttenuation(float3 worldNormal)
{
	float attenuation = 1.0;
	Light mainLight = GetMainLight();
	float3 lightDir = mainLight.direction;
	attenuation *= saturate(dot(worldNormal, lightDir));

	return attenuation;
}

void GetVertexData(inout Vertex v0, inout Vertex v1, inout Vertex v2)
{
	uint3 triangleIndices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
	v0 = FetchVertex(triangleIndices.x);
	v1 = FetchVertex(triangleIndices.y);
	v2 = FetchVertex(triangleIndices.z);
}


float RandomValue1D(int x)
{
	int frequency = 10000;
	float result = sin(x + 100);

	result *= frequency;
	result = fmod(result, 1.0);

	return result;
}

float RandomValue1DLerp(float position)
{
	int intPosition = (int)floor(position);
	float fractionalPosition = fmod(position, 1.0);
	fractionalPosition = smoothstep(0.0, 1.0, fractionalPosition);

	float r0 = RandomValue1D(intPosition);
	float r1 = RandomValue1D(intPosition + 1);
	return lerp(r0, r1, fractionalPosition) * 2.0 - 1.0;
}

float3 GetRandom3D(float3 worldPos, float3 worldDirection, float scale, float offset)
{
	float r_m = RandomValue1DLerp( (length(worldPos.xyz) + length(worldDirection.xyz)) * scale + offset				+ 1000);
	float r_x = RandomValue1DLerp( (worldPos.x + worldDirection.x) * scale + offset + r_m		+ 2000);
	float r_y = RandomValue1DLerp( (worldPos.y + worldDirection.y) * scale + offset + r_x		+ 3000);
	float r_z = RandomValue1DLerp( (worldPos.z + worldDirection.z) * scale + offset + r_x + r_y + 4000);

	return normalize(float3(r_x, r_y, r_z)) * r_m;
}

